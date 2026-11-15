using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>一次会话怎么开始、怎么结束：各种结束方式报对了没有，连接有没有收干净，输入事件和键盘指示灯。对着本机回环上的假 Tab5。</summary>
public class DisplaySessionTests
{
    /// <summary>画面给完 frames 帧之后：来源结束 / 画面不再变（像静止的桌面，抓屏接口等到时限后报「没变」）/ 一直给同一幅画面。</summary>
    public enum Then { End, Still, SamePicture }

    /// <summary>
    /// 每取一次给一幅不一样的画面（一条竖线每帧右移，像拖动窗口时局部在变）；给完 frames 帧之后按 then 办，或者在第 failAt 帧抛异常。
    /// </summary>
    internal sealed class MovingBar(long frames, int fps = 0, long failAt = -1, Then then = Then.End) : IFrameSource
    {
        private long _given, _calls;

        /// <summary>给出过多少幅不一样的画面。</summary>
        public long Given => Interlocked.Read(ref _given);
        /// <summary>被要过多少次画面。</summary>
        public long Calls => Interlocked.Read(ref _calls);

        public FrameResult TryGetFrame(Span<byte> dst, TimeSpan timeout)
        {
            Interlocked.Increment(ref _calls);
            long n = _given;
            if (n == failAt) throw new InvalidOperationException("抓屏失败");
            if (n >= frames)
            {
                if (then == Then.End) return FrameResult.End;
                if (then == Then.Still || n == 0)
                {
                    Thread.Sleep(timeout);
                    return FrameResult.NoChange;
                }
                Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(10, timeout.TotalMilliseconds)));
                n--;                            // 再画一遍上一幅
            }
            else
            {
                if (fps > 0) Thread.Sleep(1000 / fps);
                Interlocked.Increment(ref _given);
            }
            var px = MemoryMarshal.Cast<byte, uint>(dst);
            px.Fill(0xFF202830u);
            int x = (int)(n * 40 % (Proto.ViewW - 8));
            for (int y = 0; y < Proto.ViewH; y++) px.Slice(y * Proto.ViewW + x, 8).Fill(0xFFFFFFFFu);
            return FrameResult.Frame;
        }
    }

    /// <summary>
    /// 像抓屏接口那样的来源：屏幕变了才给新画面，没变就等到时限报「没变」。Show(n) = 屏幕变成第 n 幅（一条竖线在第 n 个位置）。
    /// FailNext = 下一次给画面时写到一半出错（比如显卡设备丢了）：抛异常，而这一幅算取走了，之后屏幕不变就不再给。
    /// </summary>
    private sealed class Screen : IFrameSource
    {
        private readonly object _lock = new();
        private int _picture;
        private bool _changed;

        public bool FailNext { get; set; }

        public void Show(int n)
        {
            lock (_lock)
            {
                _picture = n;
                _changed = true;
                Monitor.PulseAll(_lock);
            }
        }

        public static void Draw(Span<byte> dst, int n)
        {
            var px = MemoryMarshal.Cast<byte, uint>(dst);
            px.Fill(0xFF202830u);
            for (int y = 0; y < Proto.ViewH; y++) px.Slice(y * Proto.ViewW + n * 40, 8).Fill(0xFFFFFFFFu);
        }

        public FrameResult TryGetFrame(Span<byte> dst, TimeSpan timeout)
        {
            lock (_lock)
            {
                if (!_changed && timeout > TimeSpan.Zero) Monitor.Wait(_lock, timeout);
                if (!_changed) return FrameResult.NoChange;
                _changed = false;
                if (FailNext)
                {
                    FailNext = false;
                    dst[..(dst.Length / 2)].Fill(0xFF);         // 写了一半
                    throw new InvalidOperationException("抓屏失败");
                }
                Draw(dst, _picture);
                return FrameResult.Frame;
            }
        }
    }

    /// <summary>假 Tab5 收到的第一帧，是不是 Screen 的第 picture 幅画面整屏压出来的那些条带（逐字节比）。</summary>
    private static void AssertFirstFrameIs(FakeTab5 tab5, int picture)
    {
        var view = new byte[Proto.ViewW * 4 * Proto.ViewH];
        Screen.Draw(view, picture);
        List<Strip> want = new StripEncoder((int)tab5.MaxStripBytes, (int)tab5.MaxFrameBytes, Options.Quality, Options.StripRows,
                                            Options.Subsampling, Options.Bgra, yuv422: tab5.Yuv422).Encode(view, Proto.ViewW * 4);
        Assert.Equal(want.Select(s => s.Row), tab5.FirstFrame.Select(s => (int)s.Row));
        for (int i = 0; i < want.Count; i++)
            Assert.True(want[i].Jpeg.AsSpan().SequenceEqual(tab5.FirstFrame[i].Jpeg), $"第一帧的第 {i} 个条带不是第 {picture} 幅画面");
    }

    /// <summary>每取一次给一屏随机噪点，要多快给多快：压不动，一帧几百 KB，用来把连接塞满。</summary>
    private sealed class NoiseSource : IFrameSource
    {
        private readonly Random _rng = new(7);
        private long _given;

        public long Given => Interlocked.Read(ref _given);

        public FrameResult TryGetFrame(Span<byte> dst, TimeSpan timeout)
        {
            _rng.NextBytes(dst);
            Interlocked.Increment(ref _given);
            return FrameResult.Frame;
        }
    }

    /// <summary>等到 cond 成立，最多 timeoutMs；返回等到了没有。</summary>
    private static bool WaitUntil(Func<bool> cond, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) return false;
            Thread.Sleep(10);
        }
        return true;
    }

    private static readonly SessionOptions Options = new();

    // 预先算好的几幅「游戏画面」（现算一幅要二三十毫秒，比限帧率的一拍还久）
    private static readonly Lazy<byte[][]> GameLoop = new(() => [.. Enumerable.Range(0, 6).Select(StripEncoderTests.Game)]);

    /// <summary>
    /// 像游戏那样连着动 frames 帧（要多快给多快），然后画面停住：之后说「没变」，等满时限的有 still 次（只问不等的不算），最后结束。
    /// quick = 画面从预先算好的几幅里轮着给（每幅整屏都和上一幅不一样），取一幅不到 1 毫秒，像抓屏那样；不给就每幅现算。
    /// </summary>
    private sealed class PlayThenPause(int frames, int still, bool quick = false) : IFrameSource
    {
        private int _n, _still;

        public FrameResult TryGetFrame(Span<byte> dst, TimeSpan timeout)
        {
            if (_n < frames)
            {
                if (quick) GameLoop.Value[_n++ % GameLoop.Value.Length].CopyTo(dst);
                else StripEncoderTests.Game(_n++).CopyTo(dst);
                return FrameResult.Frame;
            }
            if (_still >= still) return FrameResult.End;
            if (timeout == TimeSpan.Zero) return FrameResult.NoChange;     // 只问不等的不算一次
            _still++;
            Thread.Sleep(timeout);
            return FrameResult.NoChange;
        }
    }

    [Theory]
    [InlineData(true, true, true)]          // 现在的固件、默认的选项：走 YUV422
    [InlineData(false, true, false)]        // 加 YUV422 之前的固件：HELLO 里没有那一位，AUTH 里不能置，照旧 RGB565
    [InlineData(true, false, false)]        // 选项里说不要（对照实验）：照旧 RGB565
    public void 帧缓冲走哪种格式在握手里定_条带跟着来(bool tab5Offers, bool wanted, bool yuv)
    {
        using var tab5 = new FakeTab5 { HelloCaps = Proto.CapDevAudio | (tab5Offers ? Proto.CapDevYuv422 : 0) };
        var screen = new Screen();
        screen.Show(1);
        var session = new DisplaySession(new LimitedSource(screen, 1, 0), Options with { Yuv422 = wanted }, TestPairing.SenderAuth());

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.True(tab5.WaitEnd());
        Assert.Null(tab5.Violation);            // 走 YUV422 的连接上来了别的采样的条带算违规（FakeTab5）
        Assert.Equal(yuv, r.Yuv422);
        Assert.Equal(yuv, tab5.Yuv422);
        Assert.Equal(yuv ? Proto.CapYuv422 : 0u, tab5.AuthCaps & Proto.CapYuv422);
        // YUV422：4:2:2（亮度的采样系数 0x21）、压过范围；RGB565：选项里的采样（默认 4:2:0 = 0x22）、全范围
        Assert.All(tab5.FirstFrame, s => Assert.Equal(yuv ? 0x21 : 0x22, FakeTab5.LumaSampling(s.Jpeg)));
        AssertFirstFrameIs(tab5, 1);
    }

    [Theory]
    [InlineData(true)]          // 现在的固件：YUV422 帧缓冲
    [InlineData(false)]         // 加 YUV422 之前的固件：RGB565，采样按选项（4:2:0）
    public void 画面在动时降质量发_停下来之后按原质量补发一帧(bool yuv)
    {
        using var tab5 = new FakeTab5 { HelloCaps = Proto.CapDevAudio | (yuv ? Proto.CapDevYuv422 : 0) };
        var session = new DisplaySession(new PlayThenPause(frames: 12, still: 3), Options, TestPairing.SenderAuth());
        var frames = new List<FrameStats>();
        session.FrameAcked += f => { lock (frames) frames.Add(f); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.True(tab5.WaitEnd());
        Assert.Null(tab5.Violation);
        // 12 帧会动的画面 + 停下来之后补清晰的 1 帧，都是整屏 8 个条带
        Assert.Equal(13, frames.Count);
        Assert.All(frames, f => Assert.Equal(8, f.Strips));
        Assert.Equal(Options.Quality, frames[0].Quality);                       // 第一帧按原质量
        Assert.True(frames[11].Quality < Options.Quality, $"动到第 12 帧质量还是 {frames[11].Quality}");
        Assert.True(frames[11].Bytes <= Options.MotionFrameBytes * 11 / 10, $"第 12 帧 {frames[11].Bytes} 字节，没压进预算");
        Assert.Equal(Options.Quality, frames[12].Quality);                      // 补清晰的那一帧
        Assert.True(frames[12].Bytes > frames[11].Bytes);
        Assert.Equal(Options.Quality, r.Quality);
    }

    /// <summary>
    /// 滚完网页停下来、页面上还有一个动图：先整屏动 frames 帧，之后每 16 毫秒来一幅只有一小块在变的画面，共 blinks 幅，从不说「没变」，最后结束。
    /// </summary>
    private sealed class PlayThenBlink(int frames, int blinks) : IFrameSource
    {
        private int _n, _blink;
        private byte[] _last = [];

        public FrameResult TryGetFrame(Span<byte> dst, TimeSpan timeout)
        {
            if (_n < frames)
            {
                _last = StripEncoderTests.Game(_n++);
                _last.CopyTo(dst);
                return FrameResult.Frame;
            }
            if (_blink >= blinks) return FrameResult.End;
            Thread.Sleep(16);
            _blink++;
            var px = MemoryMarshal.Cast<byte, uint>(_last.AsSpan());
            for (int y = 200; y < 232; y++) px.Slice(y * Proto.ViewW + 100, 32).Fill(0xFF000000u | (uint)(_blink * 0x050709));
            _last.CopyTo(dst);
            return FrameResult.Frame;
        }
    }

    [Fact]
    public void 整屏停了还有一小块一直在变_来源从不说没变_过一会儿也把降过质量的地方补清晰_只补一次()
    {
        using var tab5 = new FakeTab5();
        var session = new DisplaySession(new PlayThenBlink(frames: 12, blinks: 40), Options, TestPairing.SenderAuth());
        var frames = new List<FrameStats>();
        session.FrameAcked += f => { lock (frames) frames.Add(f); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.True(tab5.WaitEnd());
        Assert.Null(tab5.Violation);
        Assert.True(frames[11].Quality < Options.Quality, $"动到第 12 帧质量还是 {frames[11].Quality}");
        // 整屏停下来之后：先是只有一个条带的小帧（照原质量），隔了一阵有一帧整屏 8 个条带、按原质量（补清晰），之后又全是小帧。
        // 和时间有关的两个前提：小帧一共 40 × 16 ms，比「150 ms 没出过在动的帧」长得多，所以一定轮得到补；
        // 停下来之后的第一幅小帧离最后一帧在动的帧不到 150 ms（来源 16 ms 后就给），所以它还不带补的——机器卡住 130 ms 以上时这一条才会不成立
        var after = frames.Skip(12).ToList();
        Assert.All(after, f => Assert.Equal(Options.Quality, f.Quality));
        var full = after.Select((f, i) => (f, i)).Where(x => x.f.Strips == 8).ToList();
        string shape = string.Join(' ', after.Select(f => f.Strips));
        Assert.True(full.Count == 1, $"整屏停下来之后各帧的条带数：{shape}");
        Assert.True(full[0].i >= 1, $"刚停下来的第一帧就补了，没等：{shape}");
        Assert.All(after.Where(f => f.Strips != 8), f => Assert.Equal(1, f.Strips));
        Assert.True(full[0].f.Bytes > frames[11].Bytes);
    }

    [Fact]
    public void 预算设成0_画面在动也不降质量_停下来没有要补的()
    {
        using var tab5 = new FakeTab5();
        var session = new DisplaySession(new PlayThenPause(frames: 6, still: 2), Options with { MotionFrameBytes = 0 }, TestPairing.SenderAuth());
        var frames = new List<FrameStats>();
        session.FrameAcked += f => { lock (frames) frames.Add(f); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Equal(6, frames.Count);
        Assert.All(frames, f => Assert.Equal(Options.Quality, f.Quality));
    }

    [Fact]
    public void 画面发完_正常收尾并发BYE()
    {
        using var tab5 = new FakeTab5();
        var session = new DisplaySession(new MovingBar(6), Options, TestPairing.SenderAuth());
        var acked = new List<uint>();
        session.FrameAcked += f => { lock (acked) acked.Add(f.Seq); };
        Hello? hello = null;
        session.Connected += h => hello = h;

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Equal((6, 6), (r.FramesSent, r.FramesAcked));
        Assert.Equal([0u, 1u, 2u, 3u, 4u, 5u], acked);
        Assert.Equal(256 * 1024, hello?.MaxStripBytes);
        Assert.True(tab5.WaitEnd());
        Assert.Null(tab5.Violation);
        Assert.Equal("bye", tab5.EndedBy);
        Assert.Equal(6, tab5.Frames);
        Assert.Equal([Proto.TAuth, Proto.TReady], tab5.Handshake);      // 配对过的：认证一下就开始发画面，不用人动手
        // 第一帧发全部 8 个条带；之后竖线每帧只挪 40 像素，一帧最多动到 2 个条带
        int strips = tab5.Received.Count(m => m.Type == Proto.TStrip);
        Assert.InRange(strips, 8 + 5, 8 + 5 * 2);
    }

    [Fact]
    public void 调用方取消_发BYE后结束()
    {
        using var tab5 = new FakeTab5();
        using var cts = new CancellationTokenSource();
        var session = new DisplaySession(new MovingBar(long.MaxValue, fps: 50), Options, TestPairing.SenderAuth());
        session.FrameAcked += f => { if (f.Seq == 4) cts.Cancel(); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, cts.Token);

        Assert.Equal(SessionEnd.Cancelled, r.End);
        Assert.InRange(r.FramesAcked, 5, 8);
        Assert.True(tab5.WaitEnd());
        Assert.Equal("bye", tab5.EndedBy);
    }

    private static double Median(IEnumerable<double> xs)
    {
        double[] a = [.. xs.Order()];
        return a[a.Length / 2];
    }

    // FrameStats 的 WaitMs / FetchMs：两帧隔得久的时候，靠这两项分清是 Tab5 / 网络来不及（等确认），还是来源没给新画面。
    // 两个用例是卡顿排查时的两种典型情况；时间都留了很宽的余量（机器忙的时候 Sleep 会多睡），比的是「哪一项占大头」。

    [Fact]
    public void 逐帧记录_来源出画面慢_间隔记在等画面上()
    {
        // 来源每 50 毫秒才有一幅新画面（比如游戏只有 20 帧），Tab5 立刻确认
        using var tab5 = new FakeTab5();
        var session = new DisplaySession(new MovingBar(24, fps: 20), Options, TestPairing.SenderAuth());
        var stats = new List<FrameStats>();
        session.FrameAcked += f => { lock (stats) stats.Add(f); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Equal(24, stats.Count);
        FrameStats[] later = [.. stats.Skip(2)];             // 头两帧：第一帧没有「上一帧」，握手后的第一幅是预先取好的
        Assert.InRange(Median(later.Select(f => f.SendGapMs)), 45, 120);
        Assert.InRange(Median(later.Select(f => f.FetchMs)), 40, 120);
        Assert.InRange(Median(later.Select(f => f.WaitMs)), 0, 10);
        // 三项加起来不超过两帧的间隔，差的只是上一帧往连接里写和循环里的零碎
        foreach (FrameStats f in later)
        {
            double parts = f.WaitMs + f.FetchMs + f.EncodeMs;
            Assert.True(parts <= f.SendGapMs + 0.5, $"帧 {f.Seq}：等确认 {f.WaitMs:F1} + 等画面 {f.FetchMs:F1} + 编码 {f.EncodeMs:F1} > 间隔 {f.SendGapMs:F1}");
        }
        Assert.InRange(Median(later.Select(f => f.SendGapMs - f.WaitMs - f.FetchMs - f.EncodeMs)), 0, 15);
    }

    [Fact]
    public void 逐帧记录_Tab5确认慢_间隔记在等确认上()
    {
        // 来源随时有新画面，Tab5 每帧要 60 毫秒才确认（接收、解码、上屏慢）
        using var tab5 = new FakeTab5 { AckDelay = TimeSpan.FromMilliseconds(60) };
        using var cts = new CancellationTokenSource();
        var session = new DisplaySession(new MovingBar(long.MaxValue), Options, TestPairing.SenderAuth());
        var stats = new List<FrameStats>();
        session.FrameAcked += f => { lock (stats) { stats.Add(f); if (stats.Count == 20) cts.Cancel(); } };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, cts.Token);

        Assert.Equal(SessionEnd.Cancelled, r.End);
        FrameStats[] later;
        lock (stats) later = [.. stats.Skip(6).Take(14)];    // 前几帧：在途帧数还没顶到上限，不用等
        Assert.InRange(Median(later.Select(f => f.SendGapMs)), 50, 150);
        Assert.InRange(Median(later.Select(f => f.WaitMs)), 40, 150);
        Assert.InRange(Median(later.Select(f => f.FetchMs)), 0, 10);
        foreach (FrameStats f in later)
        {
            double parts = f.WaitMs + f.FetchMs + f.EncodeMs;
            Assert.True(parts <= f.SendGapMs + 0.5, $"帧 {f.Seq}：等确认 {f.WaitMs:F1} + 等画面 {f.FetchMs:F1} + 编码 {f.EncodeMs:F1} > 间隔 {f.SendGapMs:F1}");
        }
    }

    [Fact]
    public void Tab5那边退出显示模式_报DeviceBye_不回发BYE()
    {
        using var tab5 = new FakeTab5 { AfterFrame = 3, Then = c => { c.Send(Proto.TDevBye); c.Socket.Shutdown(SocketShutdown.Send); } };
        var session = new DisplaySession(new MovingBar(long.MaxValue, fps: 50), Options, TestPairing.SenderAuth());

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.DeviceBye, r.End);
        Assert.Equal(3, r.FramesAcked);
        Assert.True(tab5.WaitEnd());
        Assert.DoesNotContain(tab5.Received, m => m.Type == Proto.TBye);
    }

    [Fact]
    public void Tab5掉线_报Dropped()
    {
        using var tab5 = new FakeTab5 { AfterFrame = 3, Then = c => c.Socket.Close() };
        var session = new DisplaySession(new MovingBar(long.MaxValue, fps: 50), Options, TestPairing.SenderAuth());

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Dropped, r.End);
        Assert.Equal(3, r.FramesAcked);
    }

    [Fact]
    public void Tab5没开无线显示页_报没连上()
    {
        int port;
        using (var tab5 = new FakeTab5()) port = tab5.Port;         // 拿一个刚关掉、没人监听的端口
        var session = new DisplaySession(new MovingBar(3), Options, TestPairing.SenderAuth());

        SessionResult r = session.Run("127.0.0.1", port, TestLimit.Session());

        Assert.Equal(SessionEnd.ConnectFailed, r.End);
        Assert.Equal(0, r.FramesSent);
    }

    [Fact]
    public void 对端发来不认识的报文_报ProtocolError()
    {
        using var tab5 = new FakeTab5 { AfterFrame = 2, Then = c => c.Send(0x77) };
        var session = new DisplaySession(new MovingBar(long.MaxValue, fps: 50), Options, TestPairing.SenderAuth());

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.ProtocolError, r.End);
        Assert.Contains("0x77", r.Detail);
    }

    [Fact]
    public void 触摸键盘鼠标事件按先后回调()
    {
        using var tab5 = new FakeTab5
        {
            AfterFrame = 2,
            Then = c =>
            {
                c.Touch((0, 640, 360));                                             // 按下
                c.Touch((0, 650, 362), (1, 100, 700));                              // 第二根手指
                c.Touch();                                                          // 全部抬起
                c.Send(Proto.TKey, 0, [0x02, 0, 0x04, 0, 0, 0, 0, 0]);              // Shift + A
                c.Send(Proto.THeld);                                                // 还按着
                c.Send(Proto.TMouse, 0, [5, 0, 0xFD, 0xFF, 0xFF, 0x01]);            // 右 5、上 3、滚轮 −1、左键
            },
        };
        var session = new DisplaySession(new MovingBar(8, fps: 50), Options, TestPairing.SenderAuth());
        var log = new List<string>();
        session.Touch += pts => { lock (log) log.Add("T " + string.Join(' ', pts.Select(p => $"{p.Id}:{p.X},{p.Y}"))); };
        session.Key += k => { lock (log) log.Add("K " + Convert.ToHexString(k)); };
        session.Mouse += m => { lock (log) log.Add($"M {m.Dx} {m.Dy} {m.Wheel} {m.Buttons}"); };
        session.Held += () => { lock (log) log.Add("H"); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Equal(["T 0:640,360", "T 0:650,362 1:100,700", "T ", "K 0200040000000000", "H", "M 5 -3 -1 1"], log);
    }

    [Fact]
    public void Tab5报触摸方式_先于它后面的触点回调_AUTH里说了认得这个报文()
    {
        using var tab5 = new FakeTab5
        {
            AfterFrame = 2,
            Then = c =>
            {
                c.Send(Proto.TInputMode, 1);                // 人在 Tab5 上开了触摸板模式：握手走完先报一次
                c.Touch((0, 640, 360));
                c.Touch();
                c.Send(Proto.TInputMode, 0);                // 关了
                c.Touch((0, 300, 200));
            },
        };
        var session = new DisplaySession(new MovingBar(8, fps: 50), Options, TestPairing.SenderAuth());
        var log = new List<string>();
        session.InputMode += pad => { lock (log) log.Add(pad ? "触摸板" : "触摸"); };
        session.Touch += pts => { lock (log) log.Add("T" + pts.Length); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Equal(["触摸板", "T1", "T0", "触摸", "T1"], log);
        // Tab5 只对在 AUTH 里说了认得 INPUT_MODE 的发送端发它（老的发送端收到不认识的报文会断开）
        Assert.Equal(Proto.CapInputMode, tab5.AuthCaps & Proto.CapInputMode);
    }

    [Fact]
    public void Tab5报触摸板的加速_先于它后面的触点回调_AUTH里说了认得这个报文()
    {
        using var tab5 = new FakeTab5
        {
            AfterFrame = 2,
            Then = c =>
            {
                c.Send(Proto.TTouchpadGain, 15);            // 人在 Tab5 上选的「触摸板加速」是「低」：握手走完先报一次
                c.Touch((0, 640, 360));
                c.Touch();
                c.Send(Proto.TTouchpadGain, 30);            // 改成「高」
                c.Touch((0, 300, 200));
            },
        };
        var session = new DisplaySession(new MovingBar(8, fps: 50), Options, TestPairing.SenderAuth());
        var log = new List<string>();
        session.TouchpadGain += gain => { lock (log) log.Add(FormattableString.Invariant($"{gain:F1} 倍")); };
        session.Touch += pts => { lock (log) log.Add("T" + pts.Length); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Equal(["1.5 倍", "T1", "T0", "3.0 倍", "T1"], log);
        Assert.Equal(Proto.CapTouchpadGain, tab5.AuthCaps & Proto.CapTouchpadGain);
    }

    [Fact]
    public void TPAD_GAIN的两头_1倍和5倍_都收下()
    {
        using var tab5 = new FakeTab5
        {
            AfterFrame = 2,
            Then = c =>
            {
                c.Send(Proto.TTouchpadGain, Proto.TouchpadGainMin);
                c.Send(Proto.TTouchpadGain, Proto.TouchpadGainMax);
            },
        };
        var session = new DisplaySession(new MovingBar(8, fps: 50), Options, TestPairing.SenderAuth());
        var told = new List<double>();
        session.TouchpadGain += gain => { lock (told) told.Add(gain); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Equal([1.0, 5.0], told);
    }

    [Theory]
    [InlineData(9, 0)]      // 不到 1 倍
    [InlineData(51, 0)]     // 超过 5 倍
    [InlineData(0, 0)]
    [InlineData(20, 1)]     // 带了载荷
    public void TPAD_GAIN的arg不在10到50之间_或者带了载荷_报ProtocolError(int arg, int payload)
    {
        using var tab5 = new FakeTab5 { AfterFrame = 2, Then = c => c.Send(Proto.TTouchpadGain, (ushort)arg, new byte[payload]) };
        var session = new DisplaySession(new MovingBar(long.MaxValue, fps: 50), Options, TestPairing.SenderAuth());
        double? told = null;
        session.TouchpadGain += gain => told = gain;

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.ProtocolError, r.End);
        Assert.Contains("TPAD_GAIN", r.Detail);
        Assert.Null(told);
    }

    [Theory]
    [InlineData(2, 0)]      // 没有这种方式
    [InlineData(1, 1)]      // 带了载荷
    public void INPUT_MODE的arg不是0或1_或者带了载荷_报ProtocolError(int arg, int payload)
    {
        using var tab5 = new FakeTab5 { AfterFrame = 2, Then = c => c.Send(Proto.TInputMode, (ushort)arg, new byte[payload]) };
        var session = new DisplaySession(new MovingBar(long.MaxValue, fps: 50), Options, TestPairing.SenderAuth());
        bool? told = null;
        session.InputMode += pad => told = pad;

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.ProtocolError, r.End);
        Assert.Contains("INPUT_MODE", r.Detail);
        Assert.Null(told);
    }

    /// <summary>PICTURE 的 arg：低 8 位是画面不动时的质量，高 8 位是画面在动时每帧的预算（KB，0 = 不降）。</summary>
    private static ushort PictureArg(int quality, int motionKb) => (ushort)(quality | motionKb << 8);

    [Fact]
    public void Tab5报画质_之后的帧按它压_已经在Tab5上的画面按新质量补一遍_AUTH里说了认得这个报文()
    {
        using var tab5 = new FakeTab5
        {
            AfterFrame = 2,
            Then = c => c.Send(Proto.TPicture, PictureArg(85, 0)),      // 人在 Tab5 上把「画质」拉到 85
        };
        // 一条竖线每帧右移（拖窗口那样只有一两个条带在变），50 帧/秒，共 30 帧
        var session = new DisplaySession(new MovingBar(30, fps: 50), Options, TestPairing.SenderAuth());
        var frames = new List<FrameStats>();
        var told = new List<(int, int)>();
        session.FrameAcked += f => { lock (frames) frames.Add(f); };
        session.Picture += (q, kb) => { lock (told) told.Add((q, kb)); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.True(tab5.WaitEnd());
        Assert.Null(tab5.Violation);
        Assert.Equal([(85, 0)], told);
        Assert.Equal(Proto.CapPicture, tab5.AuthCaps & Proto.CapPicture);
        // 头两帧（PICTURE 还没来）按选项里的 60；之后换成 85，换了就不再回去
        string shape = string.Join(' ', frames.Select(f => $"{f.Quality}×{f.Strips}"));
        Assert.Equal(Options.Quality, frames[0].Quality);
        Assert.Equal(Options.Quality, frames[1].Quality);
        int at = frames.FindIndex(f => f.Quality == 85);
        Assert.True(at is >= 2 and <= 6, $"PICTURE 之后过了好几帧才换质量：{shape}");
        Assert.All(frames.Take(at), f => Assert.Equal(Options.Quality, f.Quality));
        Assert.All(frames.Skip(at), f => Assert.Equal(85, f.Quality));
        // 换质量的那一帧把整屏都带上了（Tab5 上那些没变的条带是按 60 压的），只补这一次
        Assert.Equal(8, frames[at].Strips);
        Assert.All(frames.Skip(at + 1), f => Assert.True(f.Strips < 8, shape));
        Assert.Equal(85, r.Quality);
    }

    [Fact]
    public void Tab5报的动态预算_在动的画面按它降质量_停下来按报的质量补清晰()
    {
        using var tab5 = new FakeTab5
        {
            AfterFrame = 2,
            Then = c => c.Send(Proto.TPicture, PictureArg(90, 100)),    // 画质 90、动态预算 100 KB
        };
        var session = new DisplaySession(new PlayThenPause(frames: 40, still: 3), Options, TestPairing.SenderAuth());
        var frames = new List<FrameStats>();
        session.FrameAcked += f => { lock (frames) frames.Add(f); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Null(tab5.Violation);
        Assert.Equal(41, frames.Count);                     // 40 帧会动的 + 停下来之后补清晰的 1 帧
        FrameStats last = frames[39], fine = frames[40];
        // 选项里的预算是 40 KB：动到最后每帧明显比它大（用上了 100 KB），又不超过 100 KB；质量比 90 低（这种画面 90 的质量一帧远不止 100 KB）
        Assert.InRange(last.Bytes, 60 * 1024, 110 * 1024);
        Assert.True(last.Quality < 90, $"动到第 40 帧质量是 {last.Quality}");
        Assert.Equal(90, fine.Quality);
        Assert.Equal(8, fine.Strips);
        Assert.True(fine.Bytes > last.Bytes);
    }

    [Fact]
    public void Tab5报动态预算不降_画面在动也按原质量()
    {
        using var tab5 = new FakeTab5
        {
            AfterFrame = 2,
            Then = c => c.Send(Proto.TPicture, PictureArg(60, 0)),      // 「动态预算」拉到头
        };
        var session = new DisplaySession(new PlayThenPause(frames: 30, still: 2), Options, TestPairing.SenderAuth());
        var frames = new List<FrameStats>();
        session.FrameAcked += f => { lock (frames) frames.Add(f); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.All(frames.Skip(10), f => Assert.Equal(60, f.Quality));
        Assert.All(frames.Skip(10).Take(20), f => Assert.True(f.Bytes > Options.MotionFrameBytes, $"第 {f.Seq} 帧 {f.Bytes} 字节"));
    }

    [Theory]
    [InlineData(0, 40, 0)]      // 质量 0
    [InlineData(101, 40, 0)]    // 质量超过 100
    [InlineData(60, 40, 1)]     // 带了载荷
    public void PICTURE的质量不在1到100之间_或者带了载荷_报ProtocolError(int quality, int motionKb, int payload)
    {
        using var tab5 = new FakeTab5 { AfterFrame = 2, Then = c => c.Send(Proto.TPicture, PictureArg(quality, motionKb), new byte[payload]) };
        var session = new DisplaySession(new MovingBar(long.MaxValue, fps: 50), Options, TestPairing.SenderAuth());
        (int, int)? told = null;
        session.Picture += (q, kb) => told = (q, kb);

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.ProtocolError, r.End);
        Assert.Contains("PICTURE", r.Detail);
        Assert.Null(told);
    }

    [Fact]
    public void 选项里说了画质不听Tab5的_AUTH里不声明_Tab5还发PICTURE就算不认识的报文()
    {
        using var tab5 = new FakeTab5 { AfterFrame = 2, Then = c => c.Send(Proto.TPicture, PictureArg(85, 0)) };
        var session = new DisplaySession(new MovingBar(long.MaxValue, fps: 50), Options with { FollowDevicePicture = false }, TestPairing.SenderAuth());
        (int, int)? told = null;
        session.Picture += (q, kb) => told = (q, kb);

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        // 守协议的 Tab5 看到 AUTH 里没有这一位就不会发；发了说明它不守协议
        Assert.Equal(Proto.CapInputMode | Proto.CapTouchpadGain | Proto.CapYuv422, tab5.AuthCaps);
        Assert.Equal(SessionEnd.ProtocolError, r.End);
        Assert.Contains("0x26", r.Detail);
        Assert.Null(told);
    }

    // ---- 画质的四档（PICTURE_MODE）----

    private const int PanelUs = 15270;      // Tab5 的屏幕实测的帧周期

    /// <summary>假 Tab5 在第 2 帧确认之后报「画质第 tier 档」（人在它上面选的），屏幕的帧周期 periodUs 微秒。</summary>
    private static Action<FakeConn> ReportTier(PictureTier tier, uint periodUs = PanelUs) =>
        c => c.Send(Proto.TPictureMode, (ushort)tier, BitConverter.GetBytes(periodUs));

    [Theory]
    [InlineData(PictureTier.Fps30, 2.0)]        // 每两次屏幕刷新一帧：32.7 帧/秒
    [InlineData(PictureTier.Fps45, 1.5)]        // 每三次刷新两帧：43.6 帧/秒
    public void Tab5报限帧率的档_之后按屏幕刷新的整数比发_画面不动时的质量是90_AUTH里说了认得这个报文(PictureTier tier, double refreshes)
    {
        using var tab5 = new FakeTab5 { AfterFrame = 2, Then = ReportTier(tier) };
        // 一条竖线每帧右移，画面源要多快给多快：不限的话一秒几百帧
        var session = new DisplaySession(new MovingBar(100), Options, TestPairing.SenderAuth());
        var frames = new List<FrameStats>();
        var told = new List<(PictureTier, int)>();
        session.FrameAcked += f => { lock (frames) frames.Add(f); };
        session.PictureMode += (t, us) => { lock (told) told.Add((t, us)); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.True(tab5.WaitEnd());
        Assert.Null(tab5.Violation);
        Assert.Equal([(tier, PanelUs)], told);
        Assert.Equal(Proto.CapPictureMode | Proto.CapPicture, tab5.AuthCaps & (Proto.CapPictureMode | Proto.CapPicture));
        // 换档之后（留出头几帧）：相邻两帧隔的就是那个整数比，总的帧率也对
        double period = PanelUs / 1000.0 * refreshes;
        var paced = frames.Skip(10).ToList();
        Assert.InRange(Median(paced.Select(f => f.SendGapMs)), period - 1.5, period + 1.5);
        Assert.InRange(paced.Sum(f => f.SendGapMs) / paced.Count, period * 0.97, period * 1.06);
        Assert.All(paced, f => Assert.Equal(PictureTiers.StillQuality, f.Quality));
        Assert.Equal(PictureTiers.StillQuality, r.Quality);
    }

    [Fact]
    public void Tab5报最佳画质_不限帧率_画面在动也不降质量()
    {
        using var tab5 = new FakeTab5 { AfterFrame = 2, Then = ReportTier(PictureTier.BestQuality) };
        var session = new DisplaySession(new PlayThenPause(frames: 30, still: 2, quick: true), Options, TestPairing.SenderAuth());
        var frames = new List<FrameStats>();
        session.FrameAcked += f => { lock (frames) frames.Add(f); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Null(tab5.Violation);
        var late = frames.Skip(10).Take(20).ToList();
        Assert.All(late, f => Assert.Equal((PictureTiers.StillQuality, 0), (f.Quality, f.BudgetBytes)));
        Assert.All(late, f => Assert.True(f.Bytes > 100 * 1024, $"第 {f.Seq} 帧 {f.Bytes} 字节"));        // 游戏那样的画面按 90 压，一帧一百多 KB
        // 没有限帧率：这 20 帧不是按 30.5 / 22.9 毫秒一帧发的（压一帧要几毫秒，回环上确认立刻就到）
        Assert.True(Median(late.Select(f => f.SendGapMs)) < 20, $"相邻两帧的间隔中位 {Median(late.Select(f => f.SendGapMs)):F1} ms");
    }

    [Fact]
    public void Tab5报最佳速度_不限帧率_画面在动按这一档的预算降质量_停下来按90补清晰()
    {
        using var tab5 = new FakeTab5 { AfterFrame = 2, Then = ReportTier(PictureTier.BestSpeed) };
        var session = new DisplaySession(new PlayThenPause(frames: 40, still: 3), Options, TestPairing.SenderAuth());
        var frames = new List<FrameStats>();
        session.FrameAcked += f => { lock (frames) frames.Add(f); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Null(tab5.Violation);
        Assert.Equal(41, frames.Count);                     // 40 帧会动的 + 停下来之后补清晰的 1 帧
        TierPlan plan = PictureTiers.Plan(PictureTier.BestSpeed);
        FrameStats last = frames[39], fine = frames[40];
        Assert.True(last.Motion);
        Assert.InRange(last.BudgetBytes, plan.MinBytes, plan.MaxBytes);
        // 压到了预算上下；这种画面质量降到底（20）一帧也有二十几 KB，预算比它小的时候就是降到底
        Assert.True(last.Quality == Options.MinQuality || last.Bytes <= last.BudgetBytes * 1.15, $"质量 {last.Quality}，{last.Bytes} 字节，预算 {last.BudgetBytes}");
        Assert.True(last.Quality < PictureTiers.StillQuality && last.Bytes < 70 * 1024);
        Assert.Equal((PictureTiers.StillQuality, 8, false), (fine.Quality, fine.Strips, fine.Motion));
    }

    [Fact]
    public void 限帧率的档_Tab5处理不过来_预算几秒之内降下去()
    {
        // Tab5 处理一帧要 45 毫秒（一秒 22 帧），45 那一档要一秒 43.6 帧：帧帧都是等确认等来的，没有一帧赶在拍子上
        using var tab5 = new FakeTab5 { AfterFrame = 2, Then = ReportTier(PictureTier.Fps45), AckDelay = TimeSpan.FromMilliseconds(45) };
        var session = new DisplaySession(new PlayThenPause(frames: 110, still: 1, quick: true), Options, TestPairing.SenderAuth());
        var frames = new List<FrameStats>();
        session.FrameAcked += f => { lock (frames) frames.Add(f); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        TierPlan plan = PictureTiers.Plan(PictureTier.Fps45);
        var moving = frames.Where(f => f.Motion && f.BudgetBytes <= plan.MaxBytes).ToList();       // 换档之后在动的帧
        string shape = string.Join(' ', moving.Select(f => $"{f.BudgetBytes / 1024}{(f.KeptUp ? "+" : "-")}"));
        Assert.Equal(plan.StartBytes, moving.First(f => f.BudgetBytes != Options.MotionFrameBytes).BudgetBytes);    // 从这一档的起点起
        Assert.True(moving.Count(f => f.KeptUp) < moving.Count / 10, shape);
        Assert.True(moving[^1].BudgetBytes <= plan.StartBytes * 0.6, shape);                        // 110 帧约 5 秒：降了至少两回
        Assert.True(moving[^1].BudgetBytes >= plan.MinBytes, shape);
        Assert.True(moving[^1].Bytes < plan.StartBytes * 0.7, $"最后一帧在动的还有 {moving[^1].Bytes} 字节；{shape}");
    }

    [Fact]
    public void 限帧率的档_Tab5跟得上_预算不往下掉()
    {
        using var tab5 = new FakeTab5 { AfterFrame = 2, Then = ReportTier(PictureTier.Fps30) };
        var session = new DisplaySession(new PlayThenPause(frames: 100, still: 1, quick: true), Options, TestPairing.SenderAuth());
        var frames = new List<FrameStats>();
        session.FrameAcked += f => { lock (frames) frames.Add(f); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        TierPlan plan = PictureTiers.Plan(PictureTier.Fps30);
        var moving = frames.Skip(10).Where(f => f.Motion).ToList();
        string shape = string.Join(' ', moving.Select(f => $"{f.BudgetBytes / 1024}{(f.KeptUp ? "+" : "-")}"));
        Assert.True(moving.Count(f => f.KeptUp) >= moving.Count * 0.9, shape);
        Assert.All(moving, f => Assert.InRange(f.BudgetBytes, plan.StartBytes, plan.MaxBytes));
        Assert.InRange(Median(moving.Select(f => f.SendGapMs)), 29, 32);
    }

    [Fact]
    public void 第45档_平时3帧在途_一帧要两拍多才确认也按拍子发得出去()
    {
        // 真机上一帧从发出到确认，除了接收还要 31 毫秒上下（解码、等屏幕刷新上屏）：每帧五六十 KB 时是五十几毫秒，比 均衡档的两拍（45.8 毫秒）长、比三拍短。
        // Tab5 那头是流水，帧率不受它限制
        using var tab5 = new FakeTab5 { AckLag = TimeSpan.FromMilliseconds(55) };
        var session = new DisplaySession(new PlayThenPause(frames: 130, still: 1, quick: true), Options with { Tier = PictureTier.Fps45 }, TestPairing.SenderAuth());
        var frames = new List<FrameStats>();
        session.FrameAcked += f => { lock (frames) frames.Add(f); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Null(tab5.Violation);
        TierPlan plan = PictureTiers.Plan(PictureTier.Fps45);
        var moving = frames.Skip(10).Where(f => f.Motion).ToList();
        string shape = string.Join(' ', moving.Select(f => $"{f.BudgetBytes / 1024}{(f.KeptUp ? "+" : "-")}{f.SendGapMs:F0}"));
        Assert.InRange(Median(moving.Select(f => f.SendGapMs)), 21.5, 24.5);
        Assert.True(moving.Count(f => f.KeptUp) >= moving.Count * 0.9, shape);
        Assert.All(moving, f => Assert.True(f.BudgetBytes >= plan.StartBytes, shape));     // 预算没有因此往下掉
    }

    [Fact]
    public void 第30档_还是平时2帧在途_一帧要两拍多才确认就算没跟上_预算往下降()
    {
        // 高画质档的两拍是 61 毫秒：一帧 75 毫秒才确认，两帧在途就得等，一秒发不到 32.7 帧
        using var tab5 = new FakeTab5 { AckLag = TimeSpan.FromMilliseconds(75) };
        var session = new DisplaySession(new PlayThenPause(frames: 90, still: 1, quick: true), Options with { Tier = PictureTier.Fps30 }, TestPairing.SenderAuth());
        var frames = new List<FrameStats>();
        session.FrameAcked += f => { lock (frames) frames.Add(f); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        TierPlan plan = PictureTiers.Plan(PictureTier.Fps30);
        var moving = frames.Skip(10).Where(f => f.Motion).ToList();
        string shape = string.Join(' ', moving.Select(f => $"{f.BudgetBytes / 1024}{(f.KeptUp ? "+" : "-")}{f.SendGapMs:F0}"));
        Assert.True(moving.Count(f => f.KeptUp) < moving.Count / 4, shape);
        Assert.True(moving[^1].BudgetBytes < plan.StartBytes, shape);
    }

    // ---- 连接中途换档（平时是每条连接开头报一次；协议不限制，调试命令 wd pic 能在显示期间改）----

    private static string Shape(IEnumerable<FrameStats> frames) =>
        string.Join(' ', frames.Select(f => $"{f.Seq}:{f.BudgetBytes / 1024}{(f.KeptUp ? "+" : "-")}{f.SendGapMs:F0}"));

    [Fact]
    public void 连接中途从45档换到30档_平时的在途帧数改回2帧_节拍和预算从30档的起点起()
    {
        // 一帧 75 毫秒才确认。高画质档的两拍是 61 毫秒：平时 2 帧在途就跟不上（预算往下降）；要是换档之后还留着 均衡档的 3 帧在途（三拍 91 毫秒），就跟得上了
        using var tab5 = new FakeTab5
        {
            AckLag = TimeSpan.FromMilliseconds(75),
            Steps = [(2, ReportTier(PictureTier.Fps45)), (50, ReportTier(PictureTier.Fps30))],
        };
        var session = new DisplaySession(new PlayThenPause(frames: 150, still: 1, quick: true), Options, TestPairing.SenderAuth());
        var frames = new List<FrameStats>();
        var told = new List<(PictureTier, int)>();
        session.FrameAcked += f => { lock (frames) frames.Add(f); };
        session.PictureMode += (t, us) => { lock (told) told.Add((t, us)); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Null(tab5.Violation);
        Assert.Equal([(PictureTier.Fps45, PanelUs), (PictureTier.Fps30, PanelUs)], told);
        TierPlan plan45 = PictureTiers.Plan(PictureTier.Fps45), plan30 = PictureTiers.Plan(PictureTier.Fps30);
        string shape = Shape(frames);
        // 均衡档那一段：预算从 50 KB 起，一直跟不上（三拍 68.7 毫秒也不够 75 毫秒），到不了 高画质档的起点
        int at = frames.FindIndex(f => f.BudgetBytes == plan30.StartBytes);
        Assert.True(at > 10, shape);
        Assert.Contains(frames.Take(at), f => f.BudgetBytes == plan45.StartBytes);
        Assert.All(frames.Take(at).Skip(6), f => Assert.True(f.BudgetBytes <= plan45.StartBytes, shape));
        // Tab5 是收到第 50 帧时报的：发送端从那之后的头几帧起换（它这时最多还有 4 帧在途 / 正在压）
        Assert.InRange(frames[at].Seq, 50u, 56u);
        // 换到 高画质档之后：预算从它的起点起；2 帧在途、一帧 75 毫秒，发得出去的最快是 37.5 毫秒一帧——没按 30.5 毫秒的拍子，算没跟上，预算往下降
        var after = frames.Skip(at + 10).Where(f => f.Motion).ToList();
        Assert.True(after.Count >= 60, shape);
        Assert.True(after.Count(f => f.KeptUp) < after.Count / 4, shape);
        Assert.True(Median(after.Select(f => f.SendGapMs)) > 34, shape);
        Assert.True(after[^1].BudgetBytes < plan30.StartBytes, shape);
        Assert.All(after, f => Assert.True(f.BudgetBytes >= plan30.MinBytes, shape));
    }

    [Fact]
    public void 连接中途从30档换到45档_平时的在途帧数改成3帧_按45档的拍子发得出去()
    {
        // 一帧 50 毫秒才确认：均衡档的两拍只有 45.8 毫秒，2 帧在途的话最快 25 毫秒一帧、帧帧都算没跟上，要 3 帧在途（三拍 68.7 毫秒）才按得上拍子。
        // 高画质档那一段（两拍 61 毫秒）只是铺垫：机器忙的时候它跟不跟得上都行，下面只看它没比 高画质档的拍子快
        using var tab5 = new FakeTab5
        {
            AckLag = TimeSpan.FromMilliseconds(50),
            Steps = [(2, ReportTier(PictureTier.Fps30)), (40, ReportTier(PictureTier.Fps45))],
        };
        var session = new DisplaySession(new PlayThenPause(frames: 150, still: 1, quick: true), Options, TestPairing.SenderAuth());
        var frames = new List<FrameStats>();
        session.FrameAcked += f => { lock (frames) frames.Add(f); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Null(tab5.Violation);
        TierPlan plan45 = PictureTiers.Plan(PictureTier.Fps45), plan30 = PictureTiers.Plan(PictureTier.Fps30);
        string shape = Shape(frames);
        // Tab5 是收到第 40 帧时报的：发送端从那之后的头几帧起换，预算从 均衡档的起点起
        int at = frames.FindIndex(f => f.Seq >= 40 && f.BudgetBytes == plan45.StartBytes);
        Assert.True(at >= 0 && frames[at].Seq <= 46, shape);
        // 之前是 高画质档：不比 30.5 毫秒一帧快
        Assert.True(Median(frames.Take(at).Skip(10).Select(f => f.SendGapMs)) >= 29, shape);
        // 用的是 高画质档的预算（从它的起点起），不是选项里默认的那个数
        Assert.Contains(frames.Take(at), f => f.BudgetBytes == plan30.StartBytes);
        Assert.All(frames.Take(at).Skip(8), f => Assert.True(f.BudgetBytes != Options.MotionFrameBytes && f.BudgetBytes != plan45.StartBytes, shape));
        // 换到 均衡档之后：按 22.9 毫秒一帧，跟得上，预算不往下掉
        var after = frames.Skip(at + 10).Where(f => f.Motion).ToList();
        Assert.True(after.Count >= 80, shape);
        Assert.InRange(Median(after.Select(f => f.SendGapMs)), 21.5, 24.5);
        Assert.True(after.Count(f => f.KeptUp) >= after.Count * 0.9, shape);
        Assert.All(after, f => Assert.True(f.BudgetBytes >= plan45.StartBytes, shape));
    }

    [Fact]
    public void 同一档又报了一次_只是帧周期差几微秒_预算接着用闭环找到的数_不从头起()
    {
        // Tab5 处理一帧要 45 毫秒，均衡档跟不上：预算几秒之内降下去（见上面「Tab5 处理不过来」那一条）。
        // 降下去之后 Tab5 又报了一次 均衡档（屏幕的帧周期它每次量出来差几微秒；托盘记着上一条连接报的，新连接上 Tab5 再报一次也是这样）
        using var tab5 = new FakeTab5
        {
            AckDelay = TimeSpan.FromMilliseconds(45),
            Steps = [(2, ReportTier(PictureTier.Fps45)), (70, ReportTier(PictureTier.Fps45)), (80, ReportTier(PictureTier.Fps45, PanelUs - 4))],
        };
        var session = new DisplaySession(new PlayThenPause(frames: 120, still: 1, quick: true), Options, TestPairing.SenderAuth());
        var frames = new List<FrameStats>();
        var told = new List<(PictureTier, int)>();
        session.FrameAcked += f => { lock (frames) frames.Add(f); };
        session.PictureMode += (t, us) => { lock (told) told.Add((t, us)); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Null(tab5.Violation);
        Assert.Equal([(PictureTier.Fps45, PanelUs), (PictureTier.Fps45, PanelUs), (PictureTier.Fps45, PanelUs - 4)], told);
        TierPlan plan = PictureTiers.Plan(PictureTier.Fps45);
        string shape = Shape(frames);
        var before = frames.Where(f => f.Motion && f.Seq is >= 60 and < 69).ToList();
        var after = frames.Where(f => f.Motion && f.Seq >= 69).ToList();
        Assert.All(before, f => Assert.True(f.BudgetBytes < plan.StartBytes, shape));       // 第二次报之前已经降过了
        Assert.True(after.Count >= 45, shape);
        Assert.All(after, f => Assert.True(f.BudgetBytes <= before[^1].BudgetBytes, shape));        // 之后没有回到起点（一直跟不上，也不会升）
    }

    [Fact]
    public void 老固件的PICTURE之后来了PICTURE_MODE_改成按档位_反过来也一样()
    {
        // 一条连接上两种报文都来是不守常理的（一个固件只发其中一种），但协议不禁止：后到的算数
        using var tab5 = new FakeTab5
        {
            Steps =
            [
                (2, c => c.Send(Proto.TPicture, PictureArg(60, 60))),
                (30, ReportTier(PictureTier.Fps30)),
                (60, c => c.Send(Proto.TPicture, PictureArg(70, 0))),
            ],
        };
        var session = new DisplaySession(new MovingBar(100), Options, TestPairing.SenderAuth());
        var frames = new List<FrameStats>();
        session.FrameAcked += f => { lock (frames) frames.Add(f); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Null(tab5.Violation);
        string shape = string.Join(' ', frames.Select(f => $"{f.Seq}:q{f.Quality}/{f.BudgetBytes / 1024}/{f.SendGapMs:F0}"));
        // 第 2 帧之后：质量 60、预算 60 KB，不限帧率
        var first = frames.Where(f => f.Seq is >= 8 and < 29).ToList();
        Assert.All(first, f => Assert.True((f.Quality, f.BudgetBytes) == (60, 60 * 1024), shape));
        Assert.True(Median(first.Select(f => f.SendGapMs)) < 15, shape);
        // 第 30 帧之后：高画质档——质量 90、预算是这一档的、30.5 毫秒一帧
        TierPlan plan = PictureTiers.Plan(PictureTier.Fps30);
        var second = frames.Where(f => f.Seq is >= 36 and < 59).ToList();
        Assert.All(second, f => Assert.True((f.Quality, f.BudgetBytes) == (PictureTiers.StillQuality, plan.StartBytes), shape));
        Assert.InRange(Median(second.Select(f => f.SendGapMs)), 29, 32);
        // 第 60 帧之后：又是固定的质量 70、不降，不再限帧率
        var third = frames.Where(f => f.Seq >= 66).ToList();
        Assert.True(third.Count >= 30, shape);
        Assert.All(third, f => Assert.True((f.Quality, f.BudgetBytes) == (70, 0), shape));
        Assert.True(Median(third.Select(f => f.SendGapMs)) < 15, shape);
    }

    [Theory]
    [InlineData(4, 4, PanelUs)]         // 没有第 4 档
    [InlineData(2, 0, PanelUs)]         // 没带帧周期
    [InlineData(2, 3, PanelUs)]         // 载荷不是 4 字节
    [InlineData(2, 4, 4999)]            // 帧周期不到 5 毫秒
    [InlineData(2, 4, 50001)]           // 超过 50 毫秒
    [InlineData(2, 4, 0)]
    public void PICTURE_MODE的档位_载荷_帧周期不对_报ProtocolError(int tier, int payload, int periodUs)
    {
        using var tab5 = new FakeTab5
        {
            AfterFrame = 2,
            Then = c => c.Send(Proto.TPictureMode, (ushort)tier, BitConverter.GetBytes((uint)periodUs).AsSpan(0, payload)),
        };
        var session = new DisplaySession(new MovingBar(long.MaxValue, fps: 50), Options, TestPairing.SenderAuth());
        (PictureTier, int)? told = null;
        session.PictureMode += (t, us) => told = (t, us);

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.ProtocolError, r.End);
        Assert.Contains("PICTURE_MODE", r.Detail);
        Assert.Null(told);
    }

    [Fact]
    public void 选项里说了画质不听Tab5的_AUTH里两个画质的位都不声明_Tab5还发PICTURE_MODE就算不认识的报文()
    {
        using var tab5 = new FakeTab5 { AfterFrame = 2, Then = ReportTier(PictureTier.Fps30) };
        var session = new DisplaySession(new MovingBar(long.MaxValue, fps: 50), Options with { FollowDevicePicture = false }, TestPairing.SenderAuth());

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(Proto.CapInputMode | Proto.CapTouchpadGain | Proto.CapYuv422, tab5.AuthCaps);
        Assert.Equal(SessionEnd.ProtocolError, r.End);
        Assert.Contains("0x27", r.Detail);
    }

    [Fact]
    public void 选项里给了档位_Tab5不报_从第一帧起就按这一档()
    {
        using var tab5 = new FakeTab5();
        var session = new DisplaySession(new MovingBar(70), Options with { Tier = PictureTier.Fps30 }, TestPairing.SenderAuth());
        var frames = new List<FrameStats>();
        session.FrameAcked += f => { lock (frames) frames.Add(f); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.All(frames, f => Assert.Equal(PictureTiers.StillQuality, f.Quality));
        // 选项里默认的帧周期就是 Tab5 的屏幕实测的那个数
        Assert.InRange(Median(frames.Skip(1).Select(f => f.SendGapMs)), 29, 32);
        // 第一帧起就限着：70 帧至少要 69 拍。上界只防「慢得离谱」（机器忙的时候握手、压第一帧都会多花时间，卡得紧了会误报）
        Assert.InRange(r.Elapsed.TotalMilliseconds, 69 * 30.54 * 0.97, 69 * 30.54 * 1.5 + 1000);
    }

    [Fact]
    public void 选项里给了档位_连上的是只会报质量和预算的老固件_照它报的固定_不再限帧率()
    {
        using var tab5 = new FakeTab5 { AfterFrame = 2, Then = c => c.Send(Proto.TPicture, PictureArg(60, 60)) };
        var session = new DisplaySession(new MovingBar(80), Options with { Tier = PictureTier.Fps30 }, TestPairing.SenderAuth());
        var frames = new List<FrameStats>();
        var told = new List<(int, int)>();
        session.FrameAcked += f => { lock (frames) frames.Add(f); };
        session.Picture += (q, kb) => { lock (told) told.Add((q, kb)); };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Equal([(60, 60)], told);
        var late = frames.Skip(10).ToList();
        Assert.All(late, f => Assert.Equal((60, 60 * 1024), (f.Quality, f.BudgetBytes)));
        Assert.True(Median(late.Select(f => f.SendGapMs)) < 15, $"相邻两帧的间隔中位 {Median(late.Select(f => f.SendGapMs)):F1} ms：还在限帧率");
    }

    [Fact]
    public void HELD带了载荷_报ProtocolError()
    {
        using var tab5 = new FakeTab5 { AfterFrame = 2, Then = c => c.Send(Proto.THeld, 0, [1]) };
        var session = new DisplaySession(new MovingBar(long.MaxValue, fps: 50), Options, TestPairing.SenderAuth());

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.ProtocolError, r.End);
        Assert.Contains("HELD", r.Detail);
    }

    [Fact]
    public void 键盘指示灯_连上先发_变了再发_重连后补发最新的()
    {
        var session = new DisplaySession(new MovingBar(long.MaxValue, fps: 50), Options, TestPairing.SenderAuth());
        session.SetKeyboardLeds(0b011);                             // 还没连上就设了：Num + Caps
        session.FrameAcked += f => { if (f.Seq == 1) session.SetKeyboardLeds(0b001); };   // 中途 Caps 灭了
        using (var first = new FakeTab5 { AfterFrame = 6, Then = c => c.Socket.Close() })
        {
            Assert.Equal(SessionEnd.Dropped, session.Run("127.0.0.1", first.Port, TestLimit.Session()).End);
            Assert.True(first.WaitEnd());
            Assert.Equal((Proto.TKbdLeds, (ushort)0b011), first.Received[0]);       // 排在第一帧前面
            // 状态没变的时候不重复发：整条连接上就这两个
            Assert.Equal([(ushort)0b011, (ushort)0b001], first.Received.Where(m => m.Type == Proto.TKbdLeds).Select(m => m.Arg));
        }

        // 掉线重连：换了一条连接，Tab5 那边的键盘灯状态不作数了，调用方不用再设，会话自己把最新的补发过去
        using var second = new FakeTab5 { AfterFrame = 2, Then = c => { c.Send(Proto.TDevBye); c.Socket.Shutdown(SocketShutdown.Send); } };
        Assert.Equal(SessionEnd.DeviceBye, session.Run("127.0.0.1", second.Port, TestLimit.Session()).End);
        Assert.True(second.WaitEnd());
        Assert.Equal((Proto.TKbdLeds, (ushort)0b001), second.Received[0]);
        Assert.Single(second.Received, m => m.Type == Proto.TKbdLeds);
    }

    [Fact]
    public void 键盘指示灯_从没设过就不发()
    {
        using var tab5 = new FakeTab5();
        var session = new DisplaySession(new MovingBar(3), Options, TestPairing.SenderAuth());

        Assert.Equal(SessionEnd.Completed, session.Run("127.0.0.1", tab5.Port, TestLimit.Session()).End);

        Assert.True(tab5.WaitEnd());
        Assert.DoesNotContain(tab5.Received, m => m.Type == Proto.TKbdLeds);
    }

    [Fact]
    public void 画面来源抛异常_异常交给调用方_连接照样收干净()
    {
        using var tab5 = new FakeTab5();
        var session = new DisplaySession(new MovingBar(long.MaxValue, failAt: 3), Options, TestPairing.SenderAuth());

        var e = Assert.Throws<InvalidOperationException>(() => session.Run("127.0.0.1", tab5.Port, TestLimit.Session()));

        Assert.Equal("抓屏失败", e.Message);
        Assert.True(tab5.WaitEnd(), "连接没关：假 Tab5 还在等数据");
        Assert.Equal("bye", tab5.EndedBy);
        Assert.Equal(3, tab5.Frames);
    }

    [Fact]
    public void 事件回调里抛异常_从Run抛出来_连接照样收干净()
    {
        using var tab5 = new FakeTab5 { AfterFrame = 2, Then = c => c.Touch((0, 300, 200)) };
        var session = new DisplaySession(new MovingBar(long.MaxValue, fps: 50), Options, TestPairing.SenderAuth());
        session.Touch += _ => throw new NotSupportedException("注入触摸失败");

        var e = Assert.Throws<NotSupportedException>(() => session.Run("127.0.0.1", tab5.Port, TestLimit.Session()));

        Assert.Equal("注入触摸失败", e.Message);
        Assert.True(tab5.WaitEnd(), "连接没关：假 Tab5 还在等数据");
        Assert.Equal("bye", tab5.EndedBy);
    }

    [Fact]
    public void Tab5收了帧却不再确认_超时后按掉线处理()
    {
        // 前 6 帧照常确认（往返时间已经摸准），之后 Tab5 卡住：连接还在、帧也收，就是不回 ACK。
        // 发送端按流量控制放到 4 帧在途（平时 2 帧，确认迟到再多发 2 帧），然后只能等；最早那一帧发出 0.5 秒没确认就不等了
        using var tab5 = new FakeTab5 { StopAckAfter = 6 };
        var session = new DisplaySession(new MovingBar(long.MaxValue, fps: 50), Options with { AckTimeout = TimeSpan.FromMilliseconds(500) }, TestPairing.SenderAuth());
        var sw = Stopwatch.StartNew();

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Dropped, r.End);
        Assert.Contains("没有确认", r.Detail);
        Assert.Equal((10, 6), (r.FramesSent, r.FramesAcked));
        Assert.InRange(sw.ElapsedMilliseconds, 500, 3000);
        Assert.True(tab5.WaitEnd());
        Assert.Equal("eof", tab5.EndedBy);                         // 当成掉线：不发 BYE，直接断开
    }

    [Theory]
    [InlineData(2, 3)]      // 默认：第 1 帧确认之前第 2 帧已经发了，确认之后再补第 3 帧
    [InlineData(1, 2)]      // 收到确认才发下一帧
    public void 平时保持的在途帧数_到了这个数就等确认(int steady, long sent)
    {
        // Tab5 只确认第 1 帧，之后卡住。往返时间只有 1 个样本、还没摸准，不会因为确认迟到多发：在途的到了平时的帧数就只能等
        using var tab5 = new FakeTab5 { StopAckAfter = 1 };
        var session = new DisplaySession(new MovingBar(long.MaxValue, fps: 50),
                                         Options with { SteadyInFlight = steady, AckTimeout = TimeSpan.FromMilliseconds(500) }, TestPairing.SenderAuth());

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Dropped, r.End);
        Assert.Equal((sent, 1L), (r.FramesSent, r.FramesAcked));
        Assert.Equal(0, r.LateSends);
    }

    /// <summary>像显示器那样按墙上的钟出画面：每 periodMs 一幅，只留最新的；还没到下一幅就等到时限报「没变」（时限是 0 就立刻报）。</summary>
    private sealed class Ticker(int periodMs) : IFrameSource
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _taken = -1;

        public FrameResult TryGetFrame(Span<byte> dst, TimeSpan timeout)
        {
            long due = _clock.ElapsedMilliseconds / periodMs;
            if (due == _taken)
            {
                long wait = (_taken + 1) * periodMs - _clock.ElapsedMilliseconds;
                if (wait > timeout.TotalMilliseconds)
                {
                    if (timeout > TimeSpan.Zero) Thread.Sleep(timeout);
                    return FrameResult.NoChange;
                }
                if (wait > 0) Thread.Sleep((int)wait);
                due = _taken + 1;
            }
            _taken = due;
            Screen.Draw(dst, (int)(due % 30));
            return FrameResult.Frame;
        }
    }

    [Theory]
    [InlineData(500, 6, 1)]         // 第 4、5 帧在途时下一幅画面出来了：多放一帧（第 6 帧）
    [InlineData(0, 5, 0)]           // 关掉：到了 2 帧在途就等确认
    public void 画面源跟得上时_两帧在途还能多放一帧(int holdMs, long sent, int spare)
    {
        // 画面源 0.1 秒一幅，发送端每发完一帧都要等它（连着几帧都跟得上）。Tab5 确认头 3 帧，之后卡住；往返时间没摸准，不会因为确认迟到多发
        using var tab5 = new FakeTab5 { StopAckAfter = 3 };
        var session = new DisplaySession(new Ticker(100), Options with { SpareHoldMs = holdMs, AckTimeout = TimeSpan.FromMilliseconds(600) }, TestPairing.SenderAuth());

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Dropped, r.End);
        Assert.Equal((sent, 3L), (r.FramesSent, r.FramesAcked));
        Assert.Equal((spare, 0), (r.SpareSends, r.LateSends));
    }

    [Fact]
    public void 画面源一直有新画面等着时_到了两帧在途就等确认_不多放()
    {
        // 画面源要多快给多快（只问不等的那一次也有画面）：发送端从来不用等它，不算跟上。Tab5 确认头 3 帧，之后卡住
        using var tab5 = new FakeTab5 { StopAckAfter = 3 };
        var session = new DisplaySession(new PlayThenPause(frames: 1000, still: 0), Options with { AckTimeout = TimeSpan.FromMilliseconds(600) }, TestPairing.SenderAuth());

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Dropped, r.End);
        Assert.Equal((5L, 3L), (r.FramesSent, r.FramesAcked));
        Assert.Equal((0, 0), (r.SpareSends, r.LateSends));
    }

    [Fact]
    public void 画面发完时最后一帧一直没确认_也不会永远等下去()
    {
        using var tab5 = new FakeTab5 { StopAckAfter = 2 };
        var session = new DisplaySession(new MovingBar(3), Options with { AckTimeout = TimeSpan.FromMilliseconds(400) }, TestPairing.SenderAuth());

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Dropped, r.End);
        Assert.Equal((3, 2), (r.FramesSent, r.FramesAcked));
    }

    [Fact]
    public void Tab5不收数据了_发送不会永远卡住()
    {
        // Tab5 连着但不读：TCP 的缓冲塞满之后发送就卡住。0.3 秒发不动就报错，而不是一直卡着（卡着的话取消也取消不了）
        using var tab5 = new FakeTab5 { ReadNothing = true };
        using var link = Tab5Link.Connect("127.0.0.1", tab5.Port, TestPairing.SenderAuth(), TimeSpan.FromSeconds(5), sendTimeout: TimeSpan.FromMilliseconds(300));
        var frame = new List<Strip> { new(0, new byte[1 << 20]) };  // 1 MB 一帧，内容无所谓
        var sw = Stopwatch.StartNew();

        var e = Assert.Throws<SocketException>(() =>
        {
            for (uint seq = 0; seq < 256; seq++) link.SendFrame(frame, seq, 0);
        });

        Assert.Equal(SocketError.TimedOut, e.SocketErrorCode);
        Assert.InRange(sw.ElapsedMilliseconds, 250, 10000);
    }

    [Fact]
    public void 同一个会话对象可以接着连下一次()
    {
        // 托盘程序掉线后重连用的就是同一个对象：第二次要从全部条带发起（Tab5 上已经没有我们的画面）
        var session = new DisplaySession(new MovingBar(long.MaxValue, fps: 50), Options, TestPairing.SenderAuth());
        using (var first = new FakeTab5 { AfterFrame = 3, Then = c => c.Socket.Close() })
            Assert.Equal(SessionEnd.Dropped, session.Run("127.0.0.1", first.Port, TestLimit.Session()).End);
        using var second = new FakeTab5 { AfterFrame = 2, Then = c => { c.Send(Proto.TDevBye); c.Socket.Shutdown(SocketShutdown.Send); } };
        SessionResult r = session.Run("127.0.0.1", second.Port, TestLimit.Session());
        Assert.Equal(SessionEnd.DeviceBye, r.End);
        Assert.True(second.WaitEnd());
        Assert.True(second.Received.TakeWhile(m => m.Type != Proto.TPresent).Count(m => m.Type == Proto.TStrip) == 8, "重连后的第一帧没有发全部条带");
    }

    [Theory]
    [InlineData(Then.Still)]            // 抓屏接口报「没变」
    [InlineData(Then.SamePicture)]      // 抓屏接口给了画面，但内容和上一幅一样（压出来没有条带）
    public void 画面静止时Tab5不再确认_照样超时按掉线处理(Then then)
    {
        // 桌面静止是最常见的状态：画面动了 7 帧之后不动了，而 Tab5 从第 7 帧起卡住（连接还在、帧也收，就是不回 ACK）。
        // 这时没有新的帧要发，主循环不在流量控制那里等，确认超时也得照样查到
        using var tab5 = new FakeTab5 { StopAckAfter = 6 };
        var session = new DisplaySession(new MovingBar(7, fps: 50, then: then), Options with { AckTimeout = TimeSpan.FromMilliseconds(500) }, TestPairing.SenderAuth());
        using var cts = new CancellationTokenSource(4000);          // 查不到的话 4 秒后取消，结果会是 Cancelled

        SessionResult r = session.Run("127.0.0.1", tab5.Port, cts.Token);

        Assert.Equal(SessionEnd.Dropped, r.End);
        Assert.Contains("没有确认", r.Detail);
        Assert.Equal((7, 6), (r.FramesSent, r.FramesAcked));
        Assert.True(tab5.WaitEnd());
        Assert.Equal("eof", tab5.EndedBy);
    }

    [Fact]
    public void 掉线重连时画面是静止的_第一帧发手里的那一幅()
    {
        // 抓屏接口只在画面变了的时候给新的。掉线之后桌面没动，重连上去 Tab5 上是空的，得把手里那一幅整屏发过去；
        // 不然第一帧永远发不出去，Tab5 5 秒后断开，然后一直这么循环
        var session = new DisplaySession(new MovingBar(3, fps: 50, then: Then.Still), Options, TestPairing.SenderAuth());
        using (var first = new FakeTab5 { AfterFrame = 3, Then = c => c.Socket.Close() })
            Assert.Equal(SessionEnd.Dropped, session.Run("127.0.0.1", first.Port, TestLimit.Session()).End);

        using var second = new FakeTab5();
        using var cts = new CancellationTokenSource(3000);
        session.FrameAcked += _ => cts.Cancel();
        SessionResult r = session.Run("127.0.0.1", second.Port, cts.Token);

        Assert.Equal(SessionEnd.Cancelled, r.End);
        Assert.Equal((1, 1), (r.FramesSent, r.FramesAcked));
        Assert.True(second.WaitEnd());
        Assert.Equal(8, second.Received.Count(m => m.Type == Proto.TStrip));
        Assert.Equal("bye", second.EndedBy);
    }

    [Fact]
    public void 隔了一阵再连_这期间画面变了_第一帧就是现在的画面()
    {
        // 用户在 Tab5 上退出显示之后电脑上的画面变了，过一阵又发起连接：Tab5 上先出来的应该是现在的画面，
        // 不是上次连接留下的那一幅（可能是几小时前的、早就关掉的窗口），也不用整屏发两遍
        var screen = new Screen();
        screen.Show(1);
        var session = new DisplaySession(screen, Options, TestPairing.SenderAuth());
        using (var first = new FakeTab5 { AfterFrame = 1, Then = c => { c.Send(Proto.TDevBye); c.Socket.Shutdown(SocketShutdown.Send); } })
        {
            Assert.Equal(SessionEnd.DeviceBye, session.Run("127.0.0.1", first.Port, TestLimit.Session()).End);
            AssertFirstFrameIs(first, 1);
        }
        screen.Show(2);

        using var second = new FakeTab5();
        using var cts = new CancellationTokenSource(3000);
        session.FrameAcked += _ => cts.Cancel();
        SessionResult r = session.Run("127.0.0.1", second.Port, cts.Token);

        Assert.Equal(SessionEnd.Cancelled, r.End);
        Assert.True(second.WaitEnd());
        AssertFirstFrameIs(second, 2);
        Assert.Equal(1, r.FramesSent);
    }

    [Fact]
    public void 配对时等人输密码的工夫画面变了_第一帧发的是现在的画面()
    {
        // 配对要等人在 Tab5 上输密码，一两分钟都有可能：连接之前取的那一幅这时已经旧了，接管屏幕的第一帧不该是它
        var screen = new Screen();
        screen.Show(1);
        var gate = new PairingGate(PairingSecret.FromPassword("2468"));
        using var tab5 = new FakeTab5
        {
            Store = TestPairing.Tab5Store(paired: false),
            AskPassword = _ =>
            {
                screen.Show(2);         // 人输密码的时候电脑上的画面变了
                return "2468";
            },
        };
        var session = new DisplaySession(screen, Options, new LinkAuth(TestPairing.SenderStore(paired: false), TestPairing.PcName, gate));
        using var cts = new CancellationTokenSource(5000);
        session.FrameAcked += _ => cts.Cancel();

        SessionResult r = session.Run("127.0.0.1", tab5.Port, cts.Token);

        Assert.Equal(SessionEnd.Cancelled, r.End);
        Assert.True(tab5.WaitEnd());
        Assert.Null(tab5.Violation);
        Assert.True(tab5.NewlyPaired);
        AssertFirstFrameIs(tab5, 2);
        Assert.Equal(1, r.FramesSent);
    }

    [Fact]
    public void 来源取画面时出错_写了一半的那一幅不会发给Tab5()
    {
        // 抓屏写到一半出错（Run 把异常抛给调用方），调用方过后再 Run：缓冲里是半截画面，不能当成「手里的那一幅」发出去。
        // 这个来源出错之后屏幕不变就不再给画面，所以要等到屏幕下一次变了才连接
        var screen = new Screen();
        screen.Show(1);
        var session = new DisplaySession(screen, Options, TestPairing.SenderAuth());
        using (var first = new FakeTab5 { AfterFrame = 1, Then = _ => { screen.FailNext = true; screen.Show(2); } })
            Assert.Throws<InvalidOperationException>(() => session.Run("127.0.0.1", first.Port, TestLimit.Session()));

        using var second = new FakeTab5();
        using var cts = new CancellationTokenSource(5000);
        session.FrameAcked += _ => cts.Cancel();
        using var later = new Timer(_ => screen.Show(3), null, 300, Timeout.Infinite);
        var sw = Stopwatch.StartNew();
        SessionResult r = session.Run("127.0.0.1", second.Port, cts.Token);

        Assert.Equal(SessionEnd.Cancelled, r.End);
        Assert.InRange(sw.ElapsedMilliseconds, 250, 4000);
        Assert.True(second.WaitEnd());
        AssertFirstFrameIs(second, 3);
    }

    [Fact]
    public void 来源还没有画面时不去连接_这时取消马上返回()
    {
        // Tab5 只有一个连接的位置，连上 5 秒没发完第一帧就断开：手里没有画面就不该连上去占着
        using var tab5 = new FakeTab5();
        var source = new MovingBar(0, then: Then.Still);
        var session = new DisplaySession(source, Options, TestPairing.SenderAuth());
        using var cts = new CancellationTokenSource(300);
        var sw = Stopwatch.StartNew();

        SessionResult r = session.Run("127.0.0.1", tab5.Port, cts.Token);

        Assert.Equal(SessionEnd.Cancelled, r.End);
        Assert.InRange(sw.ElapsedMilliseconds, 250, 2000);
        Assert.True(source.Calls >= 2);
        Assert.False(tab5.Accepted, "还没有画面就连上了 Tab5");
    }

    [Fact]
    public void 来源一幅画面都没给就结束_不连接()
    {
        using var tab5 = new FakeTab5();
        var session = new DisplaySession(new MovingBar(0), Options, TestPairing.SenderAuth());

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Equal(0, r.FramesSent);
        Assert.False(tab5.Accepted);
    }

    [Fact]
    public void 等HELLO时取消_马上返回()
    {
        // 连上了但对方一直不发 HELLO（比如连到了别的服务上）：不取消的话要等满连接时限 5 秒
        using var tab5 = new FakeTab5 { NoHello = true };
        var session = new DisplaySession(new MovingBar(long.MaxValue), Options, TestPairing.SenderAuth());
        using var cts = new CancellationTokenSource(200);
        var sw = Stopwatch.StartNew();

        SessionResult r = session.Run("127.0.0.1", tab5.Port, cts.Token);

        Assert.Equal(SessionEnd.Cancelled, r.End);
        Assert.InRange(sw.ElapsedMilliseconds, 150, 2000);
        Assert.True(tab5.Accepted);
    }

    [Fact]
    public void 对方一直不发HELLO_到时限报没连上()
    {
        using var tab5 = new FakeTab5 { NoHello = true };
        var session = new DisplaySession(new MovingBar(long.MaxValue), Options with { ConnectTimeout = TimeSpan.FromMilliseconds(300) }, TestPairing.SenderAuth());
        var sw = Stopwatch.StartNew();

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.ConnectFailed, r.End);
        Assert.InRange(sw.ElapsedMilliseconds, 250, 3000);
    }

    [Fact]
    public void 发送卡住时取消_很快返回而不是等到发送超时()
    {
        // Tab5 确认了 5 帧之后不收数据了：发送端接着发（最多 64 帧在途），把系统的缓冲塞满之后卡在写上。
        // 这时取消（托盘上点「停止」），不能等到 5 秒的发送超时才出来，结果也得是「取消」而不是「掉线」
        using var tab5 = new FakeTab5 { StopReadingAfter = 5 };
        var source = new NoiseSource();
        var session = new DisplaySession(source, Options with { MaxInFlight = 64, AckTimeout = TimeSpan.FromSeconds(30) }, TestPairing.SenderAuth());
        using var cts = new CancellationTokenSource();
        SessionResult? r = null;
        var run = new Thread(() => r = session.Run("127.0.0.1", tab5.Port, cts.Token));
        run.Start();

        // 来源不再被要画面 = 主循环卡在写上了
        long last = -1;
        var still = Stopwatch.StartNew();
        Assert.True(WaitUntil(() =>
        {
            long now = source.Given;
            if (now != last) { last = now; still.Restart(); }
            return now > 5 && still.ElapsedMilliseconds >= 500;
        }, 20000), "发送一直没有卡住");
        var sw = Stopwatch.StartNew();
        cts.Cancel();

        Assert.True(run.Join(2000), "取消之后 2 秒还没返回");
        Assert.Equal(SessionEnd.Cancelled, r!.End);
        Assert.InRange(sw.ElapsedMilliseconds, 0, 2000);
        Assert.Equal(5, r.FramesAcked);
    }

    [Fact]
    public void Run返回之后不再有回调()
    {
        // Tab5 一口气发来 200 个触摸报文，回调每个要 70 毫秒（比如在往系统里注入）。处理到第 3 个时取消：
        // Run 要等正在跑的那个回调跑完才返回，返回之后剩下的报文不再回调。
        // （70 毫秒是有意不取会话里那几个 300 毫秒时限的约数：不然时限到点正好落在两个回调之间，没等读线程也看不出来）
        using var tab5 = new FakeTab5
        {
            AfterFrame = 2,
            Then = c => { for (int i = 0; i < 200; i++) c.Touch((0, (ushort)(100 + i), 300)); },
        };
        var session = new DisplaySession(new MovingBar(long.MaxValue, fps: 50), Options, TestPairing.SenderAuth());
        using var cts = new CancellationTokenSource();
        int calls = 0, late = 0;
        bool returned = false;
        session.Touch += _ =>
        {
            if (Volatile.Read(ref returned)) Interlocked.Increment(ref late);
            if (Interlocked.Increment(ref calls) == 3) cts.Cancel();
            Thread.Sleep(70);
            if (Volatile.Read(ref returned)) Interlocked.Increment(ref late);
        };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, cts.Token);
        Volatile.Write(ref returned, true);
        int callsAtReturn = Volatile.Read(ref calls);
        Thread.Sleep(300);

        Assert.Equal(SessionEnd.Cancelled, r.End);
        Assert.Equal(0, Volatile.Read(ref late));
        Assert.Equal(callsAtReturn, Volatile.Read(ref calls));
        Assert.InRange(callsAtReturn, 3, 20);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]        // 收报文的线程正忙着（确认第 5 帧的回调要 50 毫秒）：写失败的时候 BYE 还没读到
    public void Tab5发完BYE立刻关连接_发送端正在满速发_仍报DeviceBye(int ackCallbackMs)
    {
        // 对端发完 BYE 立刻关连接（协议要求它等发送端关了再关，早期的固件没守这一条）。发送端这时还在发，对面回的是 RST，发送端的写会失败——
        // 不能因此报成「掉线」（掉线会自动重连，而协议规定收到 BYE 不得自动重连）。
        // 这一条只有 Linux 上做得到：Linux 收到 RST 之后已经到的数据还读得出来；Windows 会把接收缓冲里没读的数据丢掉
        // （2026-11-12 在 Windows 11 上实测，BYE 读不到），只能报成掉线——所以对端必须守收尾的规定，见下一条用例
        for (int round = 0; round < 5; round++)
        {
            using var tab5 = new FakeTab5 { AfterFrame = 5, Then = FakeTab5.ByeThenReset };
            var session = new DisplaySession(new NoiseSource(), Options, TestPairing.SenderAuth());
            session.FrameAcked += f => { if (f.Seq == 4) Thread.Sleep(ackCallbackMs); };

            SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

            if (OperatingSystem.IsWindows())
            {
                Assert.Contains(r.End, new[] { SessionEnd.DeviceBye, SessionEnd.Dropped });
                Assert.InRange(r.FramesAcked, 4, 5);        // 第 5 帧的 ACK 可能和 BYE 一起被丢掉
            }
            else
            {
                Assert.Equal(SessionEnd.DeviceBye, r.End);
                Assert.Equal(5, r.FramesAcked);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]        // 收报文的线程正忙着：BYE 到的时候它还没空读
    public void Tab5发完BYE等发送端关了才关连接_发送端正在满速发_报DeviceBye(int ackCallbackMs)
    {
        // 协议规定的收尾（固件就是这样）：发完 BYE 不马上关、把发送端还在路上的数据读掉、等发送端关了再关（不先关发送方向）。
        // 这样不回 RST，BYE 在哪个系统上都读得到，发送端不会把「Tab5 结束的」当成掉线
        for (int round = 0; round < 5; round++)
        {
            using var tab5 = new FakeTab5 { AfterFrame = 5, ByeThenDrain = true };
            var session = new DisplaySession(new NoiseSource(), Options, TestPairing.SenderAuth());
            session.FrameAcked += f => { if (f.Seq == 4) Thread.Sleep(ackCallbackMs); };

            SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

            Assert.Equal(SessionEnd.DeviceBye, r.End);
            Assert.Equal(5, r.FramesAcked);
            Assert.True(tab5.WaitEnd());
            // 协议：收到 BYE 的一方马上关连接，不等对方先关——假 Tab5 是读到发送端关了才结束的，不是等到超时
            Assert.Equal("closed", tab5.DrainEnd);
            // 收到 BYE 之后不再发东西，也不回 BYE（BYE 之前已经在路上的画面不算，假 Tab5 都读掉了）
            Assert.DoesNotContain(tab5.Received, m => m.Type == Proto.TBye);
        }
    }

    [Theory]
    [InlineData(0u, 1024u * 1024)]
    [InlineData(256u * 1024, 0u)]
    [InlineData(0x8000_0000u, 1024u * 1024)]
    public void HELLO里的上限不合理_报ProtocolError(uint maxStrip, uint maxFrame)
    {
        using var tab5 = new FakeTab5 { MaxStripBytes = maxStrip, MaxFrameBytes = maxFrame };
        var session = new DisplaySession(new MovingBar(3), Options, TestPairing.SenderAuth());

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.ProtocolError, r.End);
        Assert.Contains("上限", r.Detail);
        Assert.Equal(0, r.FramesSent);
    }

    [Fact]
    public void 同一个会话同一时刻只能有一个Run()
    {
        using var tab5 = new FakeTab5();
        var source = new MovingBar(0, then: Then.Still);            // 一直没有画面：Run 停在连接之前
        var session = new DisplaySession(source, Options, TestPairing.SenderAuth());
        using var cts = new CancellationTokenSource();
        SessionResult? r = null;
        var run = new Thread(() => r = session.Run("127.0.0.1", tab5.Port, cts.Token));
        run.Start();
        Assert.True(WaitUntil(() => source.Calls > 0));

        Assert.Throws<InvalidOperationException>(() => session.Run("127.0.0.1", tab5.Port, TestLimit.Session()));

        cts.Cancel();
        Assert.True(run.Join(2000));
        Assert.Equal(SessionEnd.Cancelled, r!.End);
        // 上一个结束了就可以再跑
        using var again = new CancellationTokenSource(0);
        Assert.Equal(SessionEnd.Cancelled, session.Run("127.0.0.1", tab5.Port, again.Token).End);
    }

    public static TheoryData<SessionOptions> 不合理的选项 => new()
    {
        Options with { Quality = 0 },
        Options with { Quality = 101 },
        Options with { StripRows = 0 },
        Options with { StripRows = 100 },           // 不是 16 的倍数
        Options with { StripRows = 1296 },          // 比帧缓冲还高
        Options with { Tier = (PictureTier)4 },     // 没有这一档
        Options with { RefreshPeriodUs = 4999 },
        Options with { RefreshPeriodUs = 50001 },
        Options with { MaxInFlight = 0 },
        Options with { SteadyInFlight = 0 },
        Options with { SteadyInFlight = 5 },        // 比最多的 4 帧还多
        Options with { MaxInFlight = 1 },           // 平时默认是 2，比最多的 1 帧多：要死等得两项一起设
        Options with { MinAckWaitMs = 0 },
        Options with { MinAckWaitMs = double.NaN },
        Options with { SpareHoldMs = -1 },
        Options with { SpareHoldMs = double.NaN },
        Options with { SpareHoldMs = double.PositiveInfinity },     // 一次都没跟上过也能多放
        Options with { AckTimeout = TimeSpan.Zero },
        Options with { AckTimeout = TimeSpan.FromDays(30) },        // 换算成毫秒会超出 int
        Options with { ConnectTimeout = TimeSpan.FromMilliseconds(-1) },
    };

    [Theory]
    [MemberData(nameof(不合理的选项))]
    public void 选项不合理_构造会话时就报出来(SessionOptions options)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DisplaySession(new MovingBar(1), options, TestPairing.SenderAuth()));
    }

    [Fact]
    public void 要死等确认_平时和最多两项都设成1_只设最多的那一项时报错里说了怎么写()
    {
        _ = new DisplaySession(new MovingBar(1), Options with { SteadyInFlight = 1, MaxInFlight = 1 }, TestPairing.SenderAuth());
        var e = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DisplaySession(new MovingBar(1), Options with { MaxInFlight = 1 }, TestPairing.SenderAuth()));
        Assert.Equal(nameof(SessionOptions.SteadyInFlight), e.ParamName);
        Assert.Contains("SteadyInFlight 和 MaxInFlight 都设成 1", e.Message);
    }
}
