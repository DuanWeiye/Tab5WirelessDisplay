using System.Diagnostics;
using System.Net.Sockets;
using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>声音（协议「声音」）：排队、夹在画面的条带之间发、Tab5 不收就不发、连接结束时收干净。对着本机回环上的假 Tab5。</summary>
public class AudioTests
{
    private static readonly SessionOptions Options = new() { ConnectTimeout = TimeSpan.FromSeconds(5) };
    private const int Chunk = 10 * Proto.AudioBytesPerMs;       // 10 ms 一段

    /// <summary>第 i 段声音：每个字节都由它在整条声音里的位置定，接错了、丢了、重了都看得出来。</summary>
    private static byte[] Pcm(int i, int bytes = Chunk)
    {
        var b = new byte[bytes];
        for (int k = 0; k < bytes; k++) b[k] = (byte)((i * 131 + k * 7 + (k >> 8)) & 0xFF);
        return b;
    }

    /// <summary>
    /// 假的声音来源：Start 之后在自己的线程上每隔 gapMs 给一段，给完 chunks 段就停（chunks &lt; 0 = 一直给到 Dispose）。
    /// </summary>
    private sealed class FakeAudio(int chunks, int gapMs = 5) : IAudioSource
    {
        private int _started, _stopped, _given;

        public int Started => Volatile.Read(ref _started);
        public int Stopped => Volatile.Read(ref _stopped);
        /// <summary>给出去了多少段。</summary>
        public int Given => Volatile.Read(ref _given);
        /// <summary>Dispose 返回之后来源的线程还在不在回调（应该一直是 false）。</summary>
        public bool CalledAfterStop { get; private set; }
        /// <summary>全部给完了。</summary>
        public ManualResetEventSlim Done { get; } = new();

        public IDisposable Start(AudioSink sink)
        {
            Interlocked.Increment(ref _started);
            var quit = new ManualResetEventSlim();
            bool stopped = false;
            var thread = new Thread(() =>
            {
                for (int i = 0; chunks < 0 || i < chunks; i++)
                {
                    if (quit.Wait(gapMs)) return;
                    if (stopped) CalledAfterStop = true;
                    sink(Pcm(i));
                    Interlocked.Increment(ref _given);
                }
                Done.Set();
            }) { IsBackground = true, Name = "fake-audio" };
            thread.Start();
            return new Stopper(() =>
            {
                quit.Set();
                thread.Join();
                stopped = true;
                Interlocked.Increment(ref _stopped);
            });
        }

        private sealed class Stopper(Action stop) : IDisposable
        {
            private int _done;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) == 0) stop();
            }
        }
    }

    private static byte[] Expected(int chunks) => [.. Enumerable.Range(0, chunks).SelectMany(i => Pcm(i))];

    [Fact]
    public void 声音夹在画面中间发过去_一个字节不差()
    {
        // 画面 50 帧/秒一直在发，声音 5 毫秒给一段、共 100 段：两个线程同时在往一条连接上发，
        // 报文是逐个加密的，先后乱了假 Tab5 那头认证码就对不上。声音给完之后再发几帧画面（排着的最后一段也发掉了）才结束
        using var tab5 = new FakeTab5();
        var audio = new FakeAudio(100);
        var session = new DisplaySession(new DisplaySessionTests.MovingBar(long.MaxValue, fps: 50), Options, TestPairing.SenderAuth(), audio);
        using var cts = new CancellationTokenSource(TestLimit.Default);
        SessionResult? result = null;
        var run = new Thread(() => result = session.Run("127.0.0.1", tab5.Port, cts.Token));
        run.Start();

        Assert.True(audio.Done.Wait(TestLimit.Default), "声音没给完");
        Thread.Sleep(150);
        cts.Cancel();
        Assert.True(run.Join(TestLimit.Default), "会话没结束");
        SessionResult r = result!;

        Assert.Equal(SessionEnd.Cancelled, r.End);
        Assert.True(tab5.WaitEnd());
        Assert.Null(tab5.Violation);
        Assert.Equal("bye", tab5.EndedBy);
        Assert.True(tab5.Frames >= 10, $"只发了 {tab5.Frames} 帧");
        Assert.Equal(100, audio.Given);
        Assert.Equal(Expected(100), tab5.Audio);
        Assert.Equal((1000.0, 0.0), (r.AudioSentMs, r.AudioDroppedMs));
        // 声音不是等画面发完才发的：第一帧和最后一帧之间就有 AUDIO
        var types = tab5.Received.Select(m => m.Type).ToList();
        int firstPresent = types.IndexOf(Proto.TPresent), lastPresent = types.LastIndexOf(Proto.TPresent);
        Assert.Contains(Proto.TAudio, types.Skip(firstPresent).Take(lastPresent - firstPresent));
        Assert.Equal((1, 1), (audio.Started, audio.Stopped));
    }

    [Fact]
    public void 画面不动的时候声音照样发()
    {
        // 桌面静止（放着音乐）：只有第一帧，之后没有画面在发，声音由抓声音的线程自己发出去
        using var tab5 = new FakeTab5();
        var audio = new FakeAudio(60);
        var session = new DisplaySession(new DisplaySessionTests.MovingBar(1, then: DisplaySessionTests.Then.Still), Options, TestPairing.SenderAuth(), audio);
        using var cts = new CancellationTokenSource(TestLimit.Default);
        SessionResult? result = null;
        var run = new Thread(() => result = session.Run("127.0.0.1", tab5.Port, cts.Token));
        run.Start();

        Assert.True(audio.Done.Wait(TestLimit.Default), "声音没给完");
        cts.Cancel();
        Assert.True(run.Join(TestLimit.Default), "会话没结束");
        SessionResult r = result!;

        Assert.Equal(SessionEnd.Cancelled, r.End);
        Assert.True(tab5.WaitEnd());
        Assert.Null(tab5.Violation);
        Assert.Equal(1, tab5.Frames);
        Assert.Equal(Expected(60), tab5.Audio);
        Assert.Equal(600.0, r.AudioSentMs);
    }

    [Fact]
    public void Tab5没说能收声音_不抓也不发()
    {
        // 加声音之前的固件：HELLO 的能力位是 0，收到 AUDIO 会当成不认识的报文断开
        using var tab5 = new FakeTab5 { HelloCaps = 0 };
        var audio = new FakeAudio(-1);
        var session = new DisplaySession(new DisplaySessionTests.MovingBar(10, fps: 50), Options, TestPairing.SenderAuth(), audio);

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.True(tab5.WaitEnd());
        Assert.Null(tab5.Violation);
        Assert.Equal(10, tab5.Frames);
        Assert.Equal(0, audio.Started);
        Assert.DoesNotContain(tab5.Received, m => m.Type == Proto.TAudio);
        Assert.Equal(0.0, r.AudioSentMs);
    }

    [Fact]
    public void 连接结束时声音停抓_BYE之后没有声音()
    {
        // 来源一直在给（2 毫秒一段）。画面发完、会话发 BYE 结束：BYE 是这条连接上的最后一个报文，Run 返回时来源已经停了、之后不再回调
        using var tab5 = new FakeTab5();
        var audio = new FakeAudio(-1, gapMs: 2);
        var session = new DisplaySession(new DisplaySessionTests.MovingBar(20, fps: 50), Options, TestPairing.SenderAuth(), audio);

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());
        int given = audio.Given;

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Equal((1, 1), (audio.Started, audio.Stopped));
        Assert.True(tab5.WaitEnd());
        Assert.Null(tab5.Violation);
        Assert.Equal("bye", tab5.EndedBy);
        Assert.Equal(Proto.TBye, tab5.Received[^1].Type);
        Thread.Sleep(50);
        Assert.Equal(given, audio.Given);
        Assert.False(audio.CalledAfterStop);
        // 收到的是给出去的那些的开头一段（最后几段可能排在 BYE 之后、没发），中间没有缺的
        Assert.True(tab5.Audio.Count > 0 && tab5.Audio.Count % Proto.AudioFrameSize == 0);
        Assert.Equal(Expected(given).Take(tab5.Audio.Count), tab5.Audio);
    }

    [Fact]
    public void 同一个会话重连_声音每条连接各抓一次()
    {
        var audio = new FakeAudio(-1);
        var session = new DisplaySession(new DisplaySessionTests.MovingBar(long.MaxValue, fps: 50), Options, TestPairing.SenderAuth(), audio);
        using (var first = new FakeTab5 { AfterFrame = 3, Then = c => c.Socket.Close() })
            Assert.Equal(SessionEnd.Dropped, session.Run("127.0.0.1", first.Port, TestLimit.Session()).End);
        Assert.Equal((1, 1), (audio.Started, audio.Stopped));
        using var second = new FakeTab5 { AfterFrame = 2, Then = c => { c.Send(Proto.TDevBye); c.Socket.Shutdown(SocketShutdown.Send); } };
        Assert.Equal(SessionEnd.DeviceBye, session.Run("127.0.0.1", second.Port, TestLimit.Session()).End);
        Assert.Equal((2, 2), (audio.Started, audio.Stopped));
        Assert.True(second.WaitEnd());
        Assert.Null(second.Violation);
    }

    [Fact]
    public void Tab5不收数据了_排声音的线程不会永远卡住_连接被关掉()
    {
        // Tab5 连着但不读：缓冲塞满之后声音发不出去。排声音的调用不抛异常（它在抓声音的线程上），0.3 秒发不动就把连接关掉，
        // 之后发画面的那一头马上出错（会话按掉线处理），而不是两边都卡着
        using var tab5 = new FakeTab5 { ReadNothing = true };
        using var link = Tab5Link.Connect("127.0.0.1", tab5.Port, TestPairing.SenderAuth(), TimeSpan.FromSeconds(5), sendTimeout: TimeSpan.FromMilliseconds(300));
        var big = new byte[100 * Proto.AudioBytesPerMs];
        var total = Stopwatch.StartNew();

        // 发不动之前每次都当场发掉、立刻返回；发不动的那一次卡满 0.3 秒，然后把连接关掉
        double stuckMs = 0;
        while (stuckMs < 250 && total.Elapsed < TimeSpan.FromSeconds(15))
        {
            var sw = Stopwatch.StartNew();
            link.QueueAudio(big);
            stuckMs = sw.Elapsed.TotalMilliseconds;
        }

        Assert.InRange(stuckMs, 250, 5000);
        Assert.ThrowsAny<Exception>(() => link.SendSimple(Proto.TKbdLeds, 1));      // 连接已经关了：发画面的那一头马上出错
        var again = Stopwatch.StartNew();
        link.QueueAudio(big);                           // 关掉之后再排：什么都不做，不抛、不卡
        Assert.True(again.ElapsedMilliseconds < 200);
    }

    [Fact]
    public void 链路停了一阵_这期间排的声音只留最后200毫秒_通了之后在条带之间按先后发出去()
    {
        // 协议「声音」里「连接堵住时不攒」的场景：Tab5 一阵子不收数据（链路停了），发画面的线程卡在某个条带上、占着发送；
        // 抓声音的线程这期间照常 10 ms 排一段。通了之后发出去的只有最后 200 ms（延迟不往上积），而且不等那一帧画面发完
        using var tab5 = new FakeTab5();
        using var link = Tab5Link.Connect("127.0.0.1", tab5.Port, TestPairing.SenderAuth(), TimeSpan.FromSeconds(5), sendTimeout: TimeSpan.FromSeconds(60));
        tab5.ReadGate.Reset();

        var frame = new List<Strip> { new(0, new byte[256 * 1024]), new(16, new byte[256 * 1024]), new(32, new byte[256 * 1024]), new(48, new byte[256 * 1024]) };
        int done = 0, quit = 0;
        Exception? frameError = null;
        var frames = new Thread(() =>
        {
            try
            {
                for (uint seq = 0; Volatile.Read(ref quit) == 0; seq++)
                {
                    link.SendFrame(frame, seq, 0);
                    Interlocked.Increment(ref done);
                }
            }
            catch (Exception e) { frameError = e; }
        }) { IsBackground = true, Name = "frames" };
        frames.Start();

        // 等缓冲塞满、发画面的线程卡住：连着看 25 次（每次隔 20 ms）都没有新的一帧发完（没堵的时候一帧只要几毫秒）。
        // 数「看了几次」而不是量时间：整个进程被停了一下（垃圾回收）的话时间过去了、次数没涨，不会把「还没塞满」当成卡住
        var limit = Stopwatch.StartNew();
        int stuckAt = -1;
        for (int same = 0; same < 25;)
        {
            Assert.True(limit.Elapsed < TimeSpan.FromSeconds(30), "发画面的线程一直没卡住");
            Thread.Sleep(20);
            int now = Volatile.Read(ref done);
            same = now == stuckAt ? same + 1 : 0;
            stuckAt = now;
        }

        for (int i = 0; i < 100; i++) link.QueueAudio(Pcm(i));      // 1 秒的声音；每次都立刻返回（占不到发送就留在队列里）
        Assert.Equal((0.0, 800.0), link.AudioStats);

        Volatile.Write(ref quit, 1);
        tab5.ReadGate.Set();                                        // 链路通了
        Assert.True(frames.Join(30000));
        Assert.Null(frameError);
        link.SendSimple(Proto.TBye);
        Assert.True(tab5.WaitEnd(30000));

        Assert.Null(tab5.Violation);
        Assert.Equal([.. Enumerable.Range(80, 20).SelectMany(i => Pcm(i))], tab5.Audio);
        Assert.Equal((200.0, 800.0), link.AudioStats);
        // 声音没有等卡住的那一帧发完：第一个 AUDIO 排在那一帧（卡住之前已经发完 stuckAt 帧）的 PRESENT 之前
        int firstAudio = tab5.Received.FindIndex(m => m.Type == Proto.TAudio);
        Assert.Equal(stuckAt, tab5.Received.Take(firstAudio).Count(m => m.Type == Proto.TPresent));
    }

    [Fact]
    public void 排声音的长度不是4的倍数_报参数错()
    {
        using var tab5 = new FakeTab5();
        using var link = Tab5Link.Connect("127.0.0.1", tab5.Port, TestPairing.SenderAuth(), TimeSpan.FromSeconds(5));
        Assert.Throws<ArgumentException>(() => link.QueueAudio(new byte[6]));
    }

    [Fact]
    public void 一大段声音切成不超过20毫秒的报文()
    {
        using var tab5 = new FakeTab5();
        using (var link = Tab5Link.Connect("127.0.0.1", tab5.Port, TestPairing.SenderAuth(), TimeSpan.FromSeconds(5)))
        {
            link.QueueAudio(Pcm(7, 130 * Proto.AudioBytesPerMs));       // 130 ms：7 个报文（6 个 20 ms + 1 个 10 ms）
            Assert.Equal((130.0, 0.0), link.AudioStats);
            link.SendSimple(Proto.TBye);
            link.ShutdownSend();
            Assert.True(tab5.WaitEnd());
        }
        Assert.Null(tab5.Violation);
        Assert.Equal(7, tab5.Received.Count(m => m.Type == Proto.TAudio));
        Assert.Equal(Pcm(7, 130 * Proto.AudioBytesPerMs), tab5.Audio);
    }

    // ── 静音闸 ──

    [Fact]
    public void 静音闸_短的静音照发_连着半秒全是0之后不发_来了声音再发()
    {
        var gate = new SilenceGate();
        byte[] zeros = new byte[Chunk], sound = Pcm(1);
        Assert.True(gate.ShouldSend(sound));
        // 半秒之内的静音照发（一句话和下一句之间）：50 段 × 10 ms
        for (int i = 0; i < SilenceGate.HoldMs / 10; i++) Assert.True(gate.ShouldSend(zeros), $"第 {i} 段静音没发");
        // 之后不发
        for (int i = 0; i < 100; i++) Assert.False(gate.ShouldSend(zeros));
        // 来了声音：马上接着发，静音重新计时
        Assert.True(gate.ShouldSend(sound));
        Assert.True(gate.ShouldSend(zeros));
        // 只有一个采样不是 0 也算有声音
        var faint = new byte[Chunk];
        faint[Chunk - 3] = 1;
        for (int i = 0; i < 200; i++) Assert.True(gate.ShouldSend(faint));
    }

    // ── 队列 ──

    [Fact]
    public void 队列_按先后取出_绕回去也对()
    {
        var q = new AudioQueue(40);
        var take = new byte[40];
        Assert.True(q.IsEmpty);
        q.Push(Pcm(0, 24));
        Assert.False(q.IsEmpty);
        Assert.Equal(24, q.TakeAll(take));
        Assert.Equal(Pcm(0, 24), take[..24]);
        Assert.True(q.IsEmpty);
        // 连着放三段、中间取一次：写的位置绕过缓冲的末尾
        q.Push(Pcm(1, 16));
        q.Push(Pcm(2, 16));
        Assert.Equal(32, q.TakeAll(take));
        Assert.Equal([.. Pcm(1, 16), .. Pcm(2, 16)], take[..32]);
        q.Push(Pcm(3, 36));
        q.Push(Pcm(4, 4));
        Assert.Equal(40, q.TakeAll(take));
        Assert.Equal([.. Pcm(3, 36), .. Pcm(4, 4)], take);
        Assert.Equal(0, q.DroppedBytes);
    }

    [Fact]
    public void 队列_放不下时扔掉最旧的_留下的是最后那一段()
    {
        var q = new AudioQueue(40);
        var take = new byte[40];
        q.Push(Pcm(0, 16));
        q.Push(Pcm(1, 16));
        q.Push(Pcm(2, 16));         // 共 48，多出 8：第 0 段的前 8 个字节没了
        Assert.Equal(8, q.DroppedBytes);
        Assert.Equal(40, q.TakeAll(take));
        Assert.Equal([.. Pcm(0, 16)[8..], .. Pcm(1, 16), .. Pcm(2, 16)], take);
        // 一次就比容量大：只留最后 40 个字节，之前排着的也都扔掉
        q.Push(Pcm(3, 12));
        q.Push(Pcm(4, 100));
        Assert.Equal(8 + 60 + 12, q.DroppedBytes);
        Assert.Equal(40, q.TakeAll(take));
        Assert.Equal(Pcm(4, 100)[60..], take);
    }

    [Fact]
    public void 队列_长度和容量都得是4的倍数()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioQueue(42));
        Assert.Throws<ArgumentException>(() => new AudioQueue(40).Push(new byte[6]));
    }
}
