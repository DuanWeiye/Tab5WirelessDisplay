using System.Diagnostics;
using System.Runtime.InteropServices;
using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>抓屏接成画面来源：断了隔一会儿重开，这期间会话不断；头一次打不开直接报错。抓屏本身用一台假的显示器代替。</summary>
public class CaptureSourceTests
{
    private const int ViewBytes = Proto.ViewW * 4 * Proto.ViewH;

    /// <summary>
    /// 一台假的显示器。Show(n) = 屏幕变成第 n 幅画面（一条竖线在第 n 个位置）；Unplug() = 拔掉：开着的抓屏下一次读就断，之后也打不开；Plug() = 插回去。
    /// 和真的抓屏一样：刚打开时第一次读一定有画面，之后屏幕变了才有。
    /// </summary>
    private sealed class FakeDisplay
    {
        private readonly object _lock = new();
        private int _picture, _version;
        private bool _unplugged;

        public int Opens, Disposes;
        /// <summary>不是 null：打开时报这个原因（不管插没插着）。</summary>
        public string? OpenError;
        /// <summary>关抓屏的时候抛异常。</summary>
        public bool DisposeThrows;
        /// <summary>最近一次读的时候给的时限。</summary>
        public TimeSpan LastTimeout;
        /// <summary>下一次读抛这个（不是「抓屏断了」的那种：代码里的错）。</summary>
        public Exception? ThrowNext;

        public void Show(int n)
        {
            lock (_lock)
            {
                _picture = n;
                _version++;
                Monitor.PulseAll(_lock);
            }
        }

        public void Unplug()
        {
            lock (_lock)
            {
                _unplugged = true;
                Monitor.PulseAll(_lock);
            }
        }

        public void Plug()
        {
            lock (_lock) _unplugged = false;
        }

        public static void Draw(Span<byte> dst, int n)
        {
            var px = MemoryMarshal.Cast<byte, uint>(dst);
            px.Fill(0xFF202830u);
            for (int y = 0; y < Proto.ViewH; y++) px.Slice(y * Proto.ViewW + n * 40, 8).Fill(0xFFFFFFFFu);
        }

        public ICaptureBackend Open()
        {
            lock (_lock)
            {
                Opens++;
                if (OpenError is not null) throw new CaptureLostException(OpenError);
                if (_unplugged) throw new CaptureLostException("没有显示器");
                return new Backend(this);
            }
        }

        private sealed class Backend(FakeDisplay d) : ICaptureBackend
        {
            private int _seen = -1;     // 读走的是第几版画面；-1 = 刚打开，还没读过

            public bool TryRead(Span<byte> dst, TimeSpan timeout)
            {
                lock (d._lock)
                {
                    d.LastTimeout = timeout;
                    if (d.ThrowNext is { } e)
                    {
                        d.ThrowNext = null;
                        throw e;
                    }
                    if (!d._unplugged && _seen == d._version && timeout > TimeSpan.Zero) Monitor.Wait(d._lock, timeout);
                    if (d._unplugged) throw new CaptureLostException("显示器拔了");
                    if (_seen == d._version) return false;
                    _seen = d._version;
                    Draw(dst, d._picture);
                    return true;
                }
            }

            public void Dispose()
            {
                lock (d._lock)
                {
                    d.Disposes++;
                    if (d.DisposeThrows) throw new InvalidOperationException("关抓屏时出错");
                }
            }
        }
    }

    private static byte[] Picture(int n)
    {
        var b = new byte[ViewBytes];
        FakeDisplay.Draw(b, n);
        return b;
    }

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

    [Fact]
    public void 屏幕变了才给新画面_没变等到时限()
    {
        var display = new FakeDisplay();
        display.Show(1);
        using var source = new CaptureSource(display.Open);
        var dst = new byte[ViewBytes];

        Assert.Equal(FrameResult.Frame, source.TryGetFrame(dst, TimeSpan.Zero));        // 刚打开：一定有
        Assert.True(dst.AsSpan().SequenceEqual(Picture(1)));

        var sw = Stopwatch.StartNew();
        Assert.Equal(FrameResult.NoChange, source.TryGetFrame(dst, TimeSpan.FromMilliseconds(120)));
        Assert.InRange(sw.ElapsedMilliseconds, 100, 1500);
        Assert.Equal(TimeSpan.FromMilliseconds(120), display.LastTimeout);
        Assert.True(dst.AsSpan().SequenceEqual(Picture(1)));                            // 没变：dst 不动

        display.Show(5);
        Assert.Equal(FrameResult.Frame, source.TryGetFrame(dst, TimeSpan.FromSeconds(2)));
        Assert.True(dst.AsSpan().SequenceEqual(Picture(5)));
        Assert.Equal(1, display.Opens);
    }

    [Fact]
    public void 头一次就打不开_构造时报错()
    {
        var display = new FakeDisplay();
        display.Unplug();
        var e = Assert.Throws<CaptureLostException>(() => new CaptureSource(display.Open));
        Assert.Equal("没有显示器", e.Message);
    }

    [Fact]
    public void 抓屏断了_关掉旧的_重开之后接着给画面()
    {
        var display = new FakeDisplay();
        display.Show(1);
        using var source = new CaptureSource(display.Open, TimeSpan.Zero);
        var lost = new List<string>();
        int restored = 0;
        source.Lost += lost.Add;
        source.Restored += () => restored++;
        var dst = new byte[ViewBytes];
        Assert.Equal(FrameResult.Frame, source.TryGetFrame(dst, TimeSpan.Zero));

        display.Unplug();
        Assert.Equal(FrameResult.NoChange, source.TryGetFrame(dst, TimeSpan.FromMilliseconds(50)));
        Assert.True(dst.AsSpan().SequenceEqual(Picture(1)));        // 断的那一次 dst 不动：会话手里还是上一幅
        Assert.Equal(["显示器拔了"], lost);
        Assert.Equal(1, display.Disposes);

        // 还没插回去：每次都去重开、都开不成。重开没成的原因和「断了」的不一样，报一次；之后同一个原因不重复报
        Assert.Equal(FrameResult.NoChange, source.TryGetFrame(dst, TimeSpan.Zero));
        Assert.Equal(["显示器拔了", "没有显示器"], lost);
        Assert.Equal(FrameResult.NoChange, source.TryGetFrame(dst, TimeSpan.Zero));
        Assert.Equal(FrameResult.NoChange, source.TryGetFrame(dst, TimeSpan.Zero));
        Assert.Equal(4, display.Opens);
        Assert.Equal(2, lost.Count);
        Assert.Equal(0, restored);

        // 重开没成的原因变了：再报一次
        display.OpenError = "建不了显卡设备";
        Assert.Equal(FrameResult.NoChange, source.TryGetFrame(dst, TimeSpan.Zero));
        Assert.Equal(FrameResult.NoChange, source.TryGetFrame(dst, TimeSpan.Zero));
        Assert.Equal(["显示器拔了", "没有显示器", "建不了显卡设备"], lost);
        display.OpenError = null;

        display.Plug();
        display.Show(7);
        Assert.Equal(FrameResult.Frame, source.TryGetFrame(dst, TimeSpan.Zero));       // 重开成了：新开的抓屏第一次读就有画面
        Assert.True(dst.AsSpan().SequenceEqual(Picture(7)));
        Assert.Equal(1, restored);
        Assert.Equal(7, display.Opens);

        // 重开成了之后再断：哪怕原因和上一回一模一样，也都要报（那是新的一次断开）
        display.Unplug();
        Assert.Equal(FrameResult.NoChange, source.TryGetFrame(dst, TimeSpan.Zero));
        Assert.Equal(FrameResult.NoChange, source.TryGetFrame(dst, TimeSpan.Zero));
        Assert.Equal(["显示器拔了", "没有显示器", "建不了显卡设备", "显示器拔了", "没有显示器"], lost);
    }

    [Fact]
    public void 断了的抓屏关的时候又出错_不往外抛_照常重开()
    {
        var display = new FakeDisplay();
        display.Show(1);
        using var source = new CaptureSource(display.Open, TimeSpan.Zero);
        var lost = new List<string>();
        source.Lost += lost.Add;
        var dst = new byte[ViewBytes];
        Assert.Equal(FrameResult.Frame, source.TryGetFrame(dst, TimeSpan.Zero));

        display.DisposeThrows = true;       // 显卡设备丢了之后，关抓屏的那几步也可能报错
        display.ThrowNext = new CaptureLostException("显卡设备丢了");
        Assert.Equal(FrameResult.NoChange, source.TryGetFrame(dst, TimeSpan.Zero));
        Assert.Equal(["显卡设备丢了"], lost);
        Assert.Equal(1, display.Disposes);
        display.DisposeThrows = false;

        display.Show(3);
        Assert.Equal(FrameResult.Frame, source.TryGetFrame(dst, TimeSpan.Zero));        // 重开成了
        Assert.True(dst.AsSpan().SequenceEqual(Picture(3)));
        Assert.Equal(2, display.Opens);
    }

    [Fact]
    public void 没到重开的时候_不去开_照样守时限()
    {
        var display = new FakeDisplay();
        using var source = new CaptureSource(display.Open, TimeSpan.FromSeconds(30));
        var dst = new byte[ViewBytes];
        Assert.Equal(FrameResult.Frame, source.TryGetFrame(dst, TimeSpan.Zero));
        display.Unplug();
        Assert.Equal(FrameResult.NoChange, source.TryGetFrame(dst, TimeSpan.Zero));
        display.Plug();

        var sw = Stopwatch.StartNew();
        Assert.Equal(FrameResult.NoChange, source.TryGetFrame(dst, TimeSpan.Zero));     // 时限 0：立刻返回
        Assert.InRange(sw.ElapsedMilliseconds, 0, 300);            // 上限放得很宽：机器忙的时候线程可能被挂起一会儿；要验的只是「没去等重开的 30 秒」
        sw.Restart();
        Assert.Equal(FrameResult.NoChange, source.TryGetFrame(dst, TimeSpan.FromMilliseconds(150)));
        Assert.InRange(sw.ElapsedMilliseconds, 130, 1500);                              // 等满时限再返回，会话不会空转
        Assert.Equal(1, display.Opens);                                                 // 30 秒没到，没去重开
    }

    [Fact]
    public void 等重开的时间算在时限里()
    {
        var display = new FakeDisplay();
        using var source = new CaptureSource(display.Open, TimeSpan.FromMilliseconds(300));
        var dst = new byte[ViewBytes];
        Assert.Equal(FrameResult.Frame, source.TryGetFrame(dst, TimeSpan.Zero));
        display.Unplug();
        Assert.Equal(FrameResult.NoChange, source.TryGetFrame(dst, TimeSpan.Zero));
        display.Plug();

        var sw = Stopwatch.StartNew();
        Assert.Equal(FrameResult.Frame, source.TryGetFrame(dst, TimeSpan.FromSeconds(5)));      // 等 0.3 秒 → 重开 → 新开的第一次读有画面
        Assert.InRange(sw.ElapsedMilliseconds, 250, 3000);
        Assert.InRange(display.LastTimeout.TotalMilliseconds, 3000, 4800);                      // 交给抓屏的时限扣掉了等的那一段
    }

    [Fact]
    public void 不是抓屏断了的错_原样抛出_不重开()
    {
        var display = new FakeDisplay();
        using var source = new CaptureSource(display.Open, TimeSpan.Zero);
        var dst = new byte[ViewBytes];
        display.ThrowNext = new InvalidOperationException("代码里的错");
        Assert.Throws<InvalidOperationException>(() => source.TryGetFrame(dst, TimeSpan.Zero));
        Assert.Equal((1, 0), (display.Opens, display.Disposes));
    }

    [Fact]
    public void 释放之后_抓屏关掉_再用报错()
    {
        var display = new FakeDisplay();
        var source = new CaptureSource(display.Open);
        source.Dispose();
        source.Dispose();
        Assert.Equal(1, display.Disposes);
        Assert.Throws<ObjectDisposedException>(() => source.TryGetFrame(new byte[ViewBytes], TimeSpan.Zero));
    }

    [Fact]
    public void 会话当中抓屏断了又恢复_连接不断_恢复后的画面照常发到Tab5()
    {
        using var tab5 = new FakeTab5();
        var display = new FakeDisplay();
        display.Show(1);
        using var source = new CaptureSource(display.Open, TimeSpan.FromMilliseconds(100));
        int lost = 0, restored = 0;
        source.Lost += _ => Interlocked.Increment(ref lost);
        source.Restored += () => Interlocked.Increment(ref restored);
        var session = new DisplaySession(source, new SessionOptions(), TestPairing.SenderAuth());
        using var cts = new CancellationTokenSource();
        SessionResult? r = null;
        var run = new Thread(() => r = session.Run("127.0.0.1", tab5.Port, cts.Token));
        run.Start();
        Assert.True(WaitUntil(() => tab5.Frames == 1), "第一帧没到 Tab5");

        display.Unplug();
        Assert.True(WaitUntil(() => Volatile.Read(ref lost) == 2), "没报抓屏断了、重开没成");       // 两个原因各报一次
        Thread.Sleep(400);                                          // 断着的这段时间：重开了几次都没成
        Assert.True(run.IsAlive, "抓屏断了，会话不该跟着结束");
        Assert.Equal(1, tab5.Frames);
        Assert.True(display.Opens >= 3, $"断着的 0.4 秒里只去重开了 {display.Opens - 1} 次");

        display.Plug();
        display.Show(9);
        Assert.True(WaitUntil(() => tab5.Frames == 2), "恢复之后的画面没到 Tab5");
        Assert.Equal((2, 1), (Volatile.Read(ref lost), Volatile.Read(ref restored)));

        cts.Cancel();
        Assert.True(run.Join(5000));
        Assert.Equal(SessionEnd.Cancelled, r?.End);
        Assert.True(tab5.WaitEnd());
        Assert.Equal("bye", tab5.EndedBy);
    }
}
