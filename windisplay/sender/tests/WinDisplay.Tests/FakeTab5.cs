using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using WinDisplay.Core;

namespace WinDisplay.Tests;

/// <summary>
/// 测试里跑会话用的时限：到点就取消。会话该自己结束的测试都带上它——发送端哪天改坏了、该停的地方不停了
/// （比如密码不对不记次数，和假 Tab5 一直你来我往），结果是 Cancelled、断言不过，这一条变红，而不是整轮测试卡住。
/// 正常的测试单条最长两三秒，碰不到这个时限。
/// </summary>
internal static class TestLimit
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(20);

    /// <summary>到时限就取消的令牌（定时器到点自己了结，不用释放）。</summary>
    public static CancellationToken Session() => new CancellationTokenSource(Default).Token;
}

/// <summary>测试里「两边早就配对好了」用的那一套：固定的两个编号、一把配对密钥、两个名字。</summary>
internal static class TestPairing
{
    public static readonly byte[] PcId = [.. Enumerable.Range(0xA0, Proto.IdSize).Select(i => (byte)i)];
    public static readonly byte[] Tab5Id = [.. Enumerable.Range(0xB0, Proto.IdSize).Select(i => (byte)i)];
    public static readonly byte[] Key = [.. Enumerable.Range(1, 32).Select(i => (byte)(i * 7))];
    public const string PcName = "书房的电脑", Tab5Name = "Tab5-3F9A";

    /// <summary>电脑这一侧的存储。paired = 里面已经有假 Tab5 的配对。</summary>
    public static PairStore SenderStore(bool paired = true)
    {
        PairStore store = PairStore.InMemory(PcId);
        if (paired) store.Save(Tab5Id, Key, Tab5Name);
        return store;
    }

    /// <summary>Tab5 这一侧的存储。paired = 里面已经有测试用的那台电脑的配对。</summary>
    public static PairStore Tab5Store(bool paired = true)
    {
        PairStore store = PairStore.InMemory(Tab5Id);
        if (paired) store.Save(PcId, Key, PcName);
        return store;
    }

    /// <summary>发送端握手要用的东西：配对好了的存储 + 名字，没设配对密码。</summary>
    public static LinkAuth SenderAuth() => new(SenderStore(), PcName);
}

/// <summary>假 Tab5 握手之后的连接：往发送端发加密的报文。Send 可以几个线程调（一次发一个完整的报文）；Seal 之后自己用 Socket 发的只能在假 Tab5 自己的线程上（Then 就是在那个线程上调的）。</summary>
internal sealed class FakeConn(Socket socket, RecordCipher tx)
{
    public Socket Socket { get; } = socket;

    /// <summary>
    /// 加密一个报文（头 ‖ 密文 ‖ 认证码），这个方向的报文计数往前走一个。只加密不发：
    /// 要测「改一个比特 / 重放 / 换先后」时拿到字节自己摆弄，再用 Socket 发。
    /// </summary>
    public byte[] Seal(byte type, ushort arg = 0, ReadOnlySpan<byte> payload = default)
    {
        var buf = new byte[Proto.HeaderSize + payload.Length + Proto.TagSize];
        Proto.WriteHeader(buf, type, arg, payload.Length);
        tx.Seal(buf.AsSpan(0, Proto.HeaderSize), payload, buf.AsSpan(Proto.HeaderSize));
        return buf;
    }

    public void Send(byte type, ushort arg = 0, ReadOnlySpan<byte> payload = default)
    {
        lock (this) Socket.Send(Seal(type, arg, payload));      // 加密和发出要连在一起：报文计数的先后就是线上的先后
    }

    /// <summary>发一个 TOUCH 报文：当前所有触点（编号、x、y）。</summary>
    public void Touch(params (byte Id, ushort X, ushort Y)[] pts) => Send(Proto.TTouch, (ushort)pts.Length, FakeTab5.Touch(pts));
}

/// <summary>假 Tab5 上弹出的一次密码框：电脑报的名字、是不是上一次输错了（标红）、这台电脑以前配对过没有（标题写「重新配对」）。</summary>
internal readonly record struct PasswordPrompt(string PcName, bool Wrong, bool Known);

/// <summary>假 Tab5 在握手里不照常走的一件事（冒充的、没照协议实现的 Tab5，或者人中途走了）。发送端应该断开，而且之后什么都不发。</summary>
internal enum Fault
{
    None,
    HelloVersion,           // HELLO 报的协议版本是 3
    HelloFlags,             // HELLO 的 flags 是 1
    HelloShort,             // HELLO 的载荷少一个字节（67 字节）
    HelloTrickle,           // HELLO 一个字节一个字节地给，每 100 毫秒一个（整个报文要 7 秒多才到齐）
    ByeBeforeHello,         // 连上之后先来的是明文的 BYE，不是 HELLO
    ByeAfterAuth,           // 收到 AUTH 之后回明文的 BYE（人正好在这时离开了「无线显示」页）——这一个不是做错，是协议允许的
    ByeWithPayload,         // 收到 AUTH 之后回的 BYE 带了 1 字节的载荷
    PairReplyForAuth,       // 收到 AUTH 之后回的是 PAIR_REPLY
    AuthOkFlags,            // AUTH_OK 的 flags 是 1
    AuthOkArg,              // AUTH_OK 的 arg 是 1
    AuthOkShort,            // AUTH_OK 的载荷少一个字节（31 字节）
    AuthOkZeroKey,          // AUTH_OK 里的 X25519 公钥全 0
    AuthOkTrickle,          // AUTH_OK 一个字节一个字节地给
    ReadyTrickle,           // AUTH_OK 照常，后面的 READY 一个字节一个字节地给
    ReadyWrongKey,          // 手里没有配对密钥（冒充的 Tab5）：照样回 AUTH_OK，READY 是拿别的密钥加密的
    EncryptedByeFirst,      // AUTH_OK 之后的第一个加密报文是 BYE，不是 READY
    AuthUnknownForPair,     // 收到 PAIR 之后回的是 AUTH_UNKNOWN
    SilentAfterPair,        // 收到 PAIR 之后一直不回（密码框开着没人管）
    PairReplyArg,           // PAIR_REPLY 的 arg 是 1
    PairReplyOffCurve,      // PAIR_REPLY 里的 pB 不在曲线上
    PairReplyKInfinity,     // PAIR_REPLY 里的 pB 是 w·N：发送端算出来的 K 是无穷远点
    PairReplyTrickle,       // PAIR_REPLY 一个字节一个字节地给
    VanishAfterPairOk,      // 收到 PAIR_OK 之后直接断开（配对的最后一步没走完）
    ByeAfterPairOk,         // 收到 PAIR_OK 和紧跟的 AUTH 之后回明文的 BYE（人正好在这时离开了页面）——协议允许的
    AuthUnknownAfterPair,   // 刚配对完，对紧跟着的 AUTH 回 AUTH_UNKNOWN
}

/// <summary>
/// 本机回环上的假 Tab5，只服务一条连接：连上就发 HELLO，照协议走 Tab5 这一侧的握手（认证；没配对过的弹「密码框」配对），
/// 之后每收到一个 PRESENT 回一个 ACK，把收到的报文按先后记下来，直到发送端发 BYE 或者断开。握手之后两个方向的报文都是加密的。
/// 默认和测试用的发送端早就配对好了（TestPairing）。可以设定：密码框上输什么、握手里故意做错一件事（Fault）、
/// 在第几帧之后做一件事（发输入事件、发 BYE、直接断开），或者模拟「连接还在但 Tab5 不动了」（不确认、不读、连上不发 HELLO）。
/// 发送端不守协议（握手的某一步来的不是该来的、密码框开着时发了东西、认证码对不上）记在 Violation 里并断开。
/// 不检查条带内容（那是 windisplay/tools/wd_mock_tab5.py 的事，见 scripts/mockcheck.sh）。
/// </summary>
internal sealed class FakeTab5 : IDisposable
{
    private const int MaxPayload = 8 * 1024 * 1024;     // 发送端一个报文最多这么大（挡住乱写的长度，不是协议里的数）

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _done = new();
    private readonly ManualResetEventSlim _stop = new();
    private volatile bool _accepted;
    private volatile Socket? _sock;
    private volatile int _promptCount;
    private RecordCipher? _tx, _rx;

    /// <summary>发送端不守协议。</summary>
    private sealed class SenderViolation(string message) : Exception(message);

    /// <summary>JPEG 帧头（SOF0）里亮度分量的采样系数：0x22 = 4:2:0，0x21 = 4:2:2，0x11 = 4:4:4。读不出来返回 0。</summary>
    public static byte LumaSampling(ReadOnlySpan<byte> jpeg)
    {
        for (int i = 2; i + 11 < jpeg.Length && jpeg[i] == 0xFF;)
        {
            if (jpeg[i + 1] == 0xC0) return jpeg[i + 11];      // 段长 2、精度 1、高 2、宽 2、分量数 1、第一个分量的编号 1，然后是它的采样系数
            i += 2 + (jpeg[i + 2] << 8 | jpeg[i + 3]);
        }
        return 0;
    }

    public FakeTab5()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _thread = new Thread(Serve) { IsBackground = true, Name = "fake-tab5" };
        _thread.Start();
    }

    public int Port { get; }

    /// <summary>Tab5 这一侧的存储：自己的编号 + 配对过的电脑。默认里面已经有测试用的那台电脑。握手走完时照协议把这次的密钥和名字存进去。</summary>
    public PairStore Store { get; init; } = TestPairing.Tab5Store();

    /// <summary>HELLO 里报的名字。</summary>
    public string Name { get; init; } = TestPairing.Tab5Name;

    /// <summary>密码框弹出来时人输什么；返回 null = 点了取消（发 BYE）。不给 = 一律点取消。</summary>
    public Func<PasswordPrompt, string?>? AskPassword { get; init; }

    /// <summary>人输密码花多久（密码框开着的时间）。这期间发送端只能等：发了东西算它不守协议，它断开了就关掉密码框。</summary>
    public TimeSpan PasswordDelay { get; init; }

    public Fault Fault { get; init; }

    /// <summary>第几帧（从 1 数）的 ACK 发出之后做 Then；0 = 不做。</summary>
    public int AfterFrame { get; init; }
    public Action<FakeConn>? Then { get; init; }

    /// <summary>连接中途还要做的事（可以有好几件）：第 Frame 帧（从 1 数）的 ACK 发出之后做 Do。和 AfterFrame / Then 各管各的。</summary>
    public (int Frame, Action<FakeConn> Do)[] Steps { get; init; } = [];

    /// <summary>前这么多帧照常确认，之后的帧照收但不回 ACK（Tab5 卡住了、连接还在）；0 = 一直确认。</summary>
    public int StopAckAfter { get; init; }

    /// <summary>每帧收完之后隔这么久才回 ACK（Tab5 解码、上屏慢，这期间也不读下一帧）。</summary>
    public TimeSpan AckDelay { get; init; }

    /// <summary>
    /// 每帧收完之后隔这么久才回 ACK，这期间照常读后面的帧（像真机：接收、解码、等屏幕刷新上屏是流水，几帧可以同时在里面，
    /// 每帧从发出到确认的时间和帧率没有关系）。给了它，Then 和 Steps 在那一帧收到之后就做（不等它的 ACK 发出去）；
    /// ByeThenDrain、StopReadingAfter 不起作用。和 AckDelay 二选一。
    /// </summary>
    public TimeSpan AckLag { get; init; }

    /// <summary>握手之后什么都不读（Tab5 不收数据了），连接一直开着，到 Dispose 为止。</summary>
    public bool ReadNothing { get; init; }

    /// <summary>确认了这么多帧之后不再读（Tab5 中途不收数据了），连接一直开着，到 Dispose 为止；0 = 一直读。</summary>
    public int StopReadingAfter { get; init; }

    /// <summary>连上之后不发 HELLO，连接一直开着，到 Dispose 为止。</summary>
    public bool NoHello { get; init; }

    /// <summary>
    /// 链路停了一阵再通：Reset 之后读完手头这个报文就不再读（发送端那头缓冲塞满、卡在发送上），Set 之后接着读。
    /// </summary>
    public ManualResetEventSlim ReadGate { get; } = new(true);

    /// <summary>HELLO 里报的能力位。默认和现在的固件一样：能收声音、能走 YUV422 帧缓冲。</summary>
    public uint HelloCaps { get; init; } = Proto.CapDevAudio | Proto.CapDevYuv422;

    /// <summary>这条连接走 YUV422 帧缓冲：HELLO 里说了能走、电脑在 AUTH 里说了用（协议「画面的色彩」）。连接结束后再读。</summary>
    public bool Yuv422 => (HelloCaps & Proto.CapDevYuv422) != 0 && (AuthCaps & Proto.CapYuv422) != 0;

    /// <summary>HELLO 里报的单条带上限和单帧上限。</summary>
    public uint MaxStripBytes { get; init; } = 256 * 1024;
    public uint MaxFrameBytes { get; init; } = 1024 * 1024;

    /// <summary>有没有被连上过。</summary>
    public bool Accepted => _accepted;

    /// <summary>握手里收到的报文类型，按先后（AUTH、PAIR、PAIR_OK、READY）。连接结束后再读。</summary>
    public List<byte> Handshake { get; } = [];

    /// <summary>弹出过的密码框，按先后。连接结束后再读；还在跑的时候只看 PromptCount。</summary>
    public List<PasswordPrompt> Prompts { get; } = [];

    /// <summary>弹出过几次密码框（哪个线程都能读）。</summary>
    public int PromptCount => _promptCount;

    /// <summary>这条连接上是不是新配对的（握手走完才算）。</summary>
    public bool NewlyPaired { get; private set; }

    /// <summary>电脑在 AUTH 里填的能力位（最近收到的那个 AUTH）。连接结束后再读。</summary>
    public uint AuthCaps { get; private set; }

    /// <summary>握手之后收到的报文（类型、arg），按先后。连接结束后再读。</summary>
    public List<(byte Type, ushort Arg)> Received { get; } = [];

    /// <summary>第一帧的条带（起始行、JPEG），按收到的先后。</summary>
    public List<(ushort Row, byte[] Jpeg)> FirstFrame { get; } = [];

    /// <summary>收到的声音（各个 AUDIO 的载荷按先后接在一起）。连接结束（WaitEnd）之后再读。</summary>
    public List<byte> Audio { get; } = [];

    /// <summary>
    /// 连接是怎么结束的：bye = 发送端发了 BYE；eof = 发送端没发 BYE 就断了；self = 本端结束的（Then、Fault、密码框上点了取消）；
    /// violation = 发送端不守协议（原因在 Violation）；timeout = 等发送端的下一个报文等过了时限。
    /// </summary>
    public string? EndedBy { get; private set; }

    /// <summary>发送端不守协议的地方；守规矩的话是 null。</summary>
    public string? Violation { get; private set; }

    /// <summary>
    /// 本端做了一件该让发送端挂断的事（Fault、密码框上点了取消）之后它的反应：closed = 关了连接；reset = 连接被重置；
    /// timeout = 5 秒了还没关。没有这种事是 null。
    /// </summary>
    public string? Hangup { get; private set; }

    /// <summary>那之后发送端还发来了多少字节（照协议应该一个都不发）。</summary>
    public int BytesAfterHangup { get; private set; }

    /// <summary>
    /// AfterFrame 那一帧确认之后照协议结束：发 BYE，读掉发送端在途的数据，等它关了连接再关（见 DrainAfterBye）。和 Then 二选一。
    /// </summary>
    public bool ByeThenDrain { get; init; }

    /// <summary>
    /// ByeThenDrain 的收尾是怎么结束的：closed = 发送端关了连接；idle = 2 秒没有新数据、它也没关；
    /// limit = 到了 3 秒的总时限它还没关；reset = 连接被重置了。
    /// </summary>
    public string? DrainEnd { get; private set; }

    public int Frames => Received.Count(m => m.Type == Proto.TPresent);

    /// <summary>等连接结束。超时返回 false（发送端没把连接收干净）。</summary>
    public bool WaitEnd(int timeoutMs = 5000) => _done.Wait(timeoutMs);

    /// <summary>密码框上依次输这些（null = 点取消）；问的次数比给的多，多出来的也点取消。</summary>
    public static Func<PasswordPrompt, string?> Answers(params string?[] answers)
    {
        int next = 0;
        return _ => next < answers.Length ? answers[next++] : null;
    }

    /// <summary>TOUCH 报文的载荷：若干个触点（编号、x、y）。</summary>
    public static byte[] Touch(params (byte Id, ushort X, ushort Y)[] pts)
    {
        var p = new byte[pts.Length * Proto.TouchPointSize];
        for (int i = 0; i < pts.Length; i++)
        {
            p[i * 5] = pts[i].Id;
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(i * 5 + 1), pts[i].X);
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(i * 5 + 3), pts[i].Y);
        }
        return p;
    }

    /// <summary>
    /// 给 Then 用：发完 BYE 立刻关连接，不等发送端（协议要求等发送端关了再关；早期的固件就是立刻关的，这里模拟不守这一条的对端）。这时发送端发来的数据还有没读的，
    /// 系统回的是 RST（这里用 linger 0 保证回 RST，不看时序），发送端的写会因此失败。
    /// </summary>
    public static void ByeThenReset(FakeConn c)
    {
        c.Send(Proto.TDevBye);
        c.Socket.LingerState = new LingerOption(true, 0);
        c.Socket.Close();
    }

    /// <summary>
    /// 照协议（PROTOCOL.md「BYE 之后怎么关连接」，固件也是这样）结束——发完 BYE 不马上关，
    /// 把发送端还在路上的数据读掉，等它关了连接再关：总共最多等 3 秒，这期间 2 秒没有新数据、它也没关就不等了
    /// （比 TCP 的重传超时长：BYE 在空中丢了的话要等重传才到发送端）。
    /// 和固件一样不先关发送方向：发送端得凭 BYE 报文自己关，不能等这边先关。
    /// 这期间收到的报文照样记进 Received：报文头到齐就记，不管载荷到没到齐（报文头只到了一半的不记）。怎么结束的记在 DrainEnd。
    /// </summary>
    private void DrainAfterBye(FakeConn c)
    {
        Socket s = c.Socket;
        c.Send(Proto.TDevBye);
        var buf = new byte[65536];
        var hdr = new byte[Proto.HeaderSize];
        int hdrGot = 0;
        long skip = 0;                                  // 当前报文的密文和认证码还有这么多字节没到
        var sw = Stopwatch.StartNew();
        DrainEnd = "limit";
        try
        {
            while (true)
            {
                long left = 3000 - sw.ElapsedMilliseconds;
                if (left <= 0) break;                           // 总时限到了，DrainEnd 还是 "limit"
                s.ReceiveTimeout = (int)Math.Min(2000, left);
                int n;
                try
                {
                    n = s.Receive(buf);
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
                {
                    if (left >= 2000) { DrainEnd = "idle"; break; }     // 等满了 2 秒
                    continue;                                   // 总时限先到，回到循环开头
                }
                if (n == 0) { DrainEnd = "closed"; break; }
                for (int i = 0; i < n;)
                {
                    if (skip > 0)
                    {
                        int k = (int)Math.Min(skip, n - i);
                        skip -= k;
                        i += k;
                        continue;
                    }
                    int c2 = Math.Min(Proto.HeaderSize - hdrGot, n - i);
                    Array.Copy(buf, i, hdr, hdrGot, c2);
                    hdrGot += c2;
                    i += c2;
                    if (hdrGot < Proto.HeaderSize) break;
                    var (type, _, arg, len) = Proto.ReadHeader(hdr);
                    Received.Add((type, arg));
                    skip = (long)len + Proto.TagSize;
                    hdrGot = 0;
                }
            }
        }
        catch (SocketException)
        {
            DrainEnd = "reset";
        }
        s.Close();
    }

    // 做了一件该让发送端挂断的事之后：等它关连接，数一数这期间它还发了多少字节
    private void WaitHangup(Socket s)
    {
        const int maxMs = 5000;
        var buf = new byte[4096];
        var sw = Stopwatch.StartNew();
        Hangup = "timeout";
        EndedBy = "self";
        try
        {
            while (sw.ElapsedMilliseconds < maxMs)
            {
                s.ReceiveTimeout = (int)Math.Max(1, maxMs - sw.ElapsedMilliseconds);
                int n = s.Receive(buf);
                if (n == 0) { Hangup = "closed"; return; }
                BytesAfterHangup += n;
            }
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
        {
        }
        catch (SocketException)
        {
            Hangup = "reset";
        }
    }

    /// <summary>
    /// Tab5 这一侧的握手（PROTOCOL.md「连上之后走哪条路」的第一张表）。走完了返回加密的连接；
    /// 没走完（本端按 Fault 结束了、人点了取消、发送端在密码框开着时断开了）返回 null，EndedBy 已经记好。
    /// 发送端不守协议抛 SenderViolation，连接断了抛 EndOfStreamException / SocketException。
    /// </summary>
    private FakeConn? DoHandshake(Socket s)
    {
        var hp = new byte[Proto.HelloSize];
        BinaryPrimitives.WriteUInt16LittleEndian(hp, Proto.FbW);
        BinaryPrimitives.WriteUInt16LittleEndian(hp.AsSpan(2), Proto.FbH);
        BinaryPrimitives.WriteUInt16LittleEndian(hp.AsSpan(4), Proto.ViewW);
        BinaryPrimitives.WriteUInt16LittleEndian(hp.AsSpan(6), Proto.ViewH);
        BinaryPrimitives.WriteUInt32LittleEndian(hp.AsSpan(8), MaxStripBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(hp.AsSpan(12), MaxFrameBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(hp.AsSpan(16), HelloCaps);
        Store.Id.CopyTo(hp, 20);
        Proto.PackName(Name).CopyTo(hp, 20 + Proto.IdSize);
        byte[] helloRaw = Fault switch
        {
            Fault.ByeBeforeHello => Proto.Pack(Proto.TDevBye, 0, []),
            Fault.HelloShort => Proto.Pack(Proto.THello, (ushort)Proto.Version, hp.AsSpan(0, hp.Length - 1)),
            Fault.HelloVersion => Proto.Pack(Proto.THello, 3, hp),
            _ => Proto.Pack(Proto.THello, (ushort)Proto.Version, hp),
        };
        if (Fault == Fault.HelloFlags) helloRaw[1] = 1;
        if (Fault == Fault.HelloTrickle) Trickle(s, helloRaw);
        else s.Send(helloRaw);
        if (Fault is Fault.HelloVersion or Fault.HelloFlags or Fault.HelloShort or Fault.ByeBeforeHello)
        {
            WaitHangup(s);
            return null;
        }

        // 收握手的下一个明文报文：类型得是这一步该来的，长度是定长，flags 和 arg 都是 0
        (byte Type, byte[] Raw, byte[] Payload) Step(params byte[] expect)
        {
            var hdr = new byte[Proto.HeaderSize];
            ReadAll(s, hdr);
            var (type, flags, arg, len) = Proto.ReadHeader(hdr);
            int want = type switch { Proto.TAuth => Proto.AuthSize, Proto.TPair => Proto.PairSize, Proto.TPairOk => Proto.PairOkSize, _ => -1 };
            if (!expect.Contains(type) || flags != 0 || arg != 0 || len != want)
                throw new SenderViolation($"握手：这一步该来 {string.Join(" 或 ", expect.Select(t => $"0x{t:x2}"))}，" +
                                          $"来的是 0x{type:x2}（flags={flags} arg={arg}，载荷 {len} 字节）");
            var raw = new byte[Proto.HeaderSize + want];
            hdr.CopyTo(raw, 0);
            ReadAll(s, raw.AsSpan(Proto.HeaderSize));
            Handshake.Add(type);
            if (type == Proto.TAuth)
            {
                AuthCaps = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(Proto.HeaderSize));
                if ((AuthCaps & Proto.CapYuv422) != 0 && (HelloCaps & Proto.CapDevYuv422) == 0)
                    throw new SenderViolation("HELLO 里没说能走 YUV422 帧缓冲，发送端却在 AUTH 里说用它");
            }
            return (type, raw, raw[Proto.HeaderSize..]);
        }

        const int authId = 4, authKeyId = authId + Proto.IdSize, authPub = authKeyId + SessionCrypto.KeyIdSize, authName = authPub + SessionCrypto.X25519Size;

        // AUTH 里的电脑配对过，而且它那把密钥就是这边存的这一把
        bool Known(byte[] auth)
        {
            byte[]? key = Store.FindKey(auth.AsSpan(authId, Proto.IdSize));
            return key is not null && auth.AsSpan(authKeyId, SessionCrypto.KeyIdSize).SequenceEqual(SessionCrypto.KeyId(key));
        }

        byte[]? newKey = null;          // 这条连接上新配出来的密钥：握手走完才存
        var (type, raw, payload) = Step(Proto.TAuth, Proto.TPair);
        if (type == Proto.TAuth && Fault is Fault.ByeAfterAuth or Fault.ByeWithPayload or Fault.PairReplyForAuth)
        {
            s.Send(Fault switch
            {
                Fault.ByeWithPayload => Proto.Pack(Proto.TDevBye, 0, [0]),
                Fault.PairReplyForAuth => Proto.Pack(Proto.TPairReply, 0, new byte[Proto.PairReplySize]),
                _ => Proto.Pack(Proto.TDevBye, 0, []),      // AUTH_OK 之前：明文
            });
            WaitHangup(s);
            return null;
        }
        if (type == Proto.TAuth && Fault != Fault.ReadyWrongKey && !Known(payload))
        {
            s.Send(Proto.Pack(Proto.TAuthUnknown, 0, []));
            (type, raw, payload) = Step(Proto.TPair);
        }
        if (type == Proto.TPair)
        {
            byte[] pairId;
            bool wrong = false;
            while (true)
            {
                if (Fault == Fault.AuthUnknownForPair)
                {
                    s.Send(Proto.Pack(Proto.TAuthUnknown, 0, []));
                    WaitHangup(s);
                    return null;
                }
                pairId = payload[..Proto.IdSize];
                string pcName = Proto.UnpackName(payload.AsSpan(Proto.IdSize, Proto.NameSize));
                byte[] pA = payload[(Proto.IdSize + Proto.NameSize)..];
                try
                {
                    P256.Decode(pA);            // 点不合格的不去问密码
                }
                catch (CryptoCheckException e)
                {
                    throw new SenderViolation($"PAIR：{e.Message}");
                }
                var prompt = new PasswordPrompt(pcName, wrong, Store.FindKey(pairId) is not null);
                Prompts.Add(prompt);
                _promptCount++;
                if (Fault == Fault.SilentAfterPair)
                {
                    WaitHangup(s);
                    return null;
                }
                string? password = AskPassword?.Invoke(prompt);
                // 密码框开着：这期间电脑什么都不该发；连接断了就关掉密码框
                if (s.Poll(PasswordDelay, SelectMode.SelectRead))
                {
                    if (s.Available > 0) throw new SenderViolation("密码框开着的时候电脑发来了东西（这期间它只能等 PAIR_REPLY）");
                    EndedBy = "eof";
                    return null;
                }
                if (password is null)
                {
                    s.Send(Proto.Pack(Proto.TDevBye, 0, []));       // AUTH_OK 之前：明文
                    WaitHangup(s);
                    return null;
                }
                PairingSecret secret = PairingSecret.FromPassword(password);
                var spake = new Spake2(Spake2.Role.B, secret);
                byte[] ke, wantCa, cB;
                try
                {
                    (ke, wantCa, cB) = spake.Finish(pA, pairId, Store.Id, SHA256.HashData([.. helloRaw, .. raw]));
                }
                catch (CryptoCheckException e)
                {
                    throw new SenderViolation($"PAIR：{e.Message}");
                }
                byte[] pB = (byte[])spake.Pub.Clone();
                if (Fault == Fault.PairReplyOffCurve) pB[^1] ^= 1;
                if (Fault == Fault.PairReplyKInfinity) pB = P256.Encode(secret.WN);
                byte[] replyRaw = Proto.Pack(Proto.TPairReply, Fault == Fault.PairReplyArg ? (ushort)1 : (ushort)0, [.. pB, .. cB]);
                if (Fault == Fault.PairReplyTrickle) Trickle(s, replyRaw);
                else s.Send(replyRaw);
                if (Fault is Fault.PairReplyOffCurve or Fault.PairReplyKInfinity or Fault.PairReplyArg)
                {
                    WaitHangup(s);
                    return null;
                }
                (type, raw, payload) = Step(Proto.TPair, Proto.TPairOk);
                if (type == Proto.TPairOk)
                {
                    if (!CryptographicOperations.FixedTimeEquals(payload, wantCa)) throw new SenderViolation("PAIR_OK 里的 cA 不对");
                    newKey = SessionCrypto.PairingKey(ke);
                    break;
                }
                wrong = true;       // 又来了一个 PAIR：刚才输的密码和电脑的不一样
            }
            if (Fault == Fault.VanishAfterPairOk)
            {
                s.Close();
                EndedBy = "self";
                return null;
            }
            (type, raw, payload) = Step(Proto.TAuth);
            if (!payload.AsSpan(authId, Proto.IdSize).SequenceEqual(pairId)
                || !payload.AsSpan(authKeyId, SessionCrypto.KeyIdSize).SequenceEqual(SessionCrypto.KeyId(newKey)))
                throw new SenderViolation("配对之后的 AUTH 用的不是配对时的电脑编号、或者不是刚配出来的那把密钥");
            if (Fault == Fault.ByeAfterPairOk)
            {
                s.Send(Proto.Pack(Proto.TDevBye, 0, []));       // AUTH_OK 之前：明文
                WaitHangup(s);
                return null;
            }
            if (Fault == Fault.AuthUnknownAfterPair)
            {
                s.Send(Proto.Pack(Proto.TAuthUnknown, 0, []));
                WaitHangup(s);
                return null;
            }
        }

        byte[] pcId = payload[authId..authKeyId];
        string name = Proto.UnpackName(payload.AsSpan(authName, Proto.NameSize));
        // 冒充的 Tab5 手里没有密钥，随便拿一把
        byte[] key = Fault == Fault.ReadyWrongKey ? RandomNumberGenerator.GetBytes(SessionCrypto.KeySize) : newKey ?? Store.FindKey(pcId)!;
        var (priv, pub) = SessionCrypto.X25519KeyPair();
        if (Fault == Fault.AuthOkShort)
        {
            s.Send(Proto.Pack(Proto.TAuthOk, 0, pub.AsSpan(0, pub.Length - 1)));
            WaitHangup(s);
            return null;
        }
        byte[] okRaw = Proto.Pack(Proto.TAuthOk, Fault == Fault.AuthOkArg ? (ushort)1 : (ushort)0,
                                  Fault == Fault.AuthOkZeroKey ? new byte[SessionCrypto.X25519Size] : pub);
        if (Fault == Fault.AuthOkFlags) okRaw[1] = 1;
        byte[] dh;
        try
        {
            dh = SessionCrypto.X25519Shared(priv, payload.AsSpan(authPub, SessionCrypto.X25519Size));
        }
        catch (CryptoCheckException e)
        {
            throw new SenderViolation($"AUTH：{e.Message}");
        }
        var (pcToTab5, tab5ToPc) = SessionCrypto.SessionKeys(key, dh, SHA256.HashData([.. helloRaw, .. raw, .. okRaw]));
        _tx = new RecordCipher(tab5ToPc);       // AUTH_OK 之后发的都加密
        _rx = new RecordCipher(pcToTab5);
        var conn = new FakeConn(s, _tx);
        byte[] readyRaw = conn.Seal(Fault == Fault.EncryptedByeFirst ? Proto.TDevBye : Proto.TReady);
        switch (Fault)
        {
            case Fault.AuthOkTrickle: Trickle(s, [.. okRaw, .. readyRaw]); break;
            case Fault.ReadyTrickle: s.Send(okRaw); Trickle(s, readyRaw); break;
            default: s.Send([.. okRaw, .. readyRaw]); break;        // AUTH_OK 和 READY 一次发出，中间不插别的
        }
        if (Fault is Fault.AuthOkFlags or Fault.AuthOkArg or Fault.AuthOkZeroKey or Fault.ReadyWrongKey or Fault.EncryptedByeFirst)
        {
            WaitHangup(s);
            return null;
        }

        var rh = new byte[Proto.HeaderSize];
        ReadAll(s, rh);
        var (rtype, rflags, rarg, rlen) = Proto.ReadHeader(rh);
        if (rlen != 0) throw new SenderViolation($"发送端加密后的第一个报文 0x{rtype:x2} 带了 {rlen} 字节的载荷，不是 READY");
        var tag = new byte[Proto.TagSize];
        ReadAll(s, tag);
        if (!_rx.TryOpen(rh, tag, [])) throw new SenderViolation("发送端的 READY 认证码对不上");
        if (rtype != Proto.TReady || rflags != 0 || rarg != 0)
            throw new SenderViolation($"发送端加密后的第一个报文是 0x{rtype:x2}（flags={rflags} arg={rarg}），不是 READY");
        Handshake.Add(rtype);
        Store.Save(pcId, key, name);            // 握手走完了才存（新配的密钥、改了的名字）
        NewlyPaired = newKey is not null;
        return conn;
    }

    private void Serve()
    {
        try
        {
            using Socket s = _listener.AcceptSocket();
            _sock = s;
            _accepted = true;
            _listener.Stop();
            s.NoDelay = true;
            if (NoHello)
            {
                _stop.Wait();
                EndedBy = "self";
                return;
            }
            s.ReceiveTimeout = 5000;            // 握手的每一步
            FakeConn? conn = DoHandshake(s);
            if (conn is null) return;
            s.ReceiveTimeout = 10000;

            if (ReadNothing)
            {
                _stop.Wait();
                EndedBy = "self";
                return;
            }
            var hdr = new byte[Proto.HeaderSize];
            int frames = 0;
            // AckLag：到点了才发的 ACK 排在这里，另一个线程按先后发（连接断了它就出来）
            using var lagged = new System.Collections.Concurrent.BlockingCollection<(long DueMs, byte[] Ack)>();
            var lagClock = Stopwatch.StartNew();
            Thread? lagThread = null;
            if (AckLag > TimeSpan.Zero)
            {
                lagThread = new Thread(() =>
                {
                    try
                    {
                        foreach (var (dueMs, late) in lagged.GetConsumingEnumerable())
                        {
                            long wait = dueMs - lagClock.ElapsedMilliseconds;
                            if (wait > 0) Thread.Sleep((int)wait);
                            conn.Send(Proto.TAck, 0, late);
                        }
                    }
                    catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException) { }
                }) { IsBackground = true, Name = "fake-tab5-ack" };
                lagThread.Start();
            }
            using var lagStop = new LagStop(lagged, lagThread);
            while (true)
            {
                if (!ReadGate.IsSet && WaitHandle.WaitAny([ReadGate.WaitHandle, _stop.WaitHandle]) == 1)
                {
                    EndedBy = "self";
                    return;
                }
                ReadAll(s, hdr);
                var (type, flags, arg, len) = Proto.ReadHeader(hdr);
                if (flags != 0) throw new SenderViolation($"报文 0x{type:x2} 的 flags 是 {flags}");
                if (len > MaxPayload) throw new SenderViolation($"报文 0x{type:x2} 的载荷 {len} 字节，太长了");
                var sealedData = new byte[len + Proto.TagSize];
                ReadAll(s, sealedData);
                var payload = new byte[len];
                if (!_rx!.TryOpen(hdr, sealedData, payload)) throw new SenderViolation($"报文 0x{type:x2}（载荷 {len} 字节）的认证码对不上");
                Received.Add((type, arg));
                if (type == Proto.TStrip && frames == 0) FirstFrame.Add((arg, payload));
                // 走 YUV422 的连接只收基线 4:2:2 的 JPEG（真机收到别的就断开）。不以 SOI 开头的不查：有些用例拿一串字节当条带占位，这里本来就不解码
                if (type == Proto.TStrip && Yuv422 && payload is [0xFF, 0xD8, ..] && LumaSampling(payload) != 0x21)
                    throw new SenderViolation($"走 YUV422 的连接上来了不是基线 4:2:2 的条带（亮度的采样系数 0x{LumaSampling(payload):x2}，0 = 没找到 SOF0）");
                if (type == Proto.TBye) { EndedBy = "bye"; return; }
                if (type == Proto.TAudio)
                {
                    if ((HelloCaps & Proto.CapDevAudio) == 0) throw new SenderViolation("HELLO 里没说能收声音，发送端却发来 AUDIO");
                    if (arg != 0 || len == 0 || len > Proto.AudioMaxBytes || len % Proto.AudioFrameSize != 0)
                        throw new SenderViolation($"AUDIO 的 arg / 长度是 {arg} / {len}");
                    Audio.AddRange(payload);
                    continue;
                }
                if (type != Proto.TPresent) continue;
                frames++;
                if (StopAckAfter > 0 && frames > StopAckAfter) continue;
                if (AckDelay > TimeSpan.Zero) Thread.Sleep(AckDelay);
                var ack = new byte[Proto.AckSize];
                payload.CopyTo(ack, 0);                 // seq 和 t_ms 原样带回，三个耗时填 0
                if (AckLag > TimeSpan.Zero)
                {
                    lagged.Add((lagClock.ElapsedMilliseconds + (long)AckLag.TotalMilliseconds, ack));
                    // 只在这一帧真做了 Then / Steps 的时候看连接：发 ACK 的线程也在这条连接上发，对面断了它发不出去，
                    // Connected 一样变成 false，那是 eof 不是 self
                    bool acted = frames == AfterFrame && Then is not null;
                    if (acted) Then!(conn);
                    foreach (var (frame, step) in Steps)
                        if (frames == frame) { step(conn); acted = true; }
                    if (acted && !s.Connected) { EndedBy = "self"; return; }    // Then / Steps 自己关了连接
                    continue;
                }
                conn.Send(Proto.TAck, 0, ack);
                bool stepped = false;
                foreach (var (frame, step) in Steps)
                    if (frames == frame) { step(conn); stepped = true; }
                if (stepped && !s.Connected) { EndedBy = "self"; return; }      // Steps 自己关了连接
                if (frames == AfterFrame && ByeThenDrain)
                {
                    DrainAfterBye(conn);
                    EndedBy = "self";
                    return;
                }
                if (frames == AfterFrame && Then is not null)
                {
                    Then(conn);
                    if (!s.Connected) { EndedBy = "self"; return; }
                }
                if (frames == StopReadingAfter)
                {
                    _stop.Wait();
                    EndedBy = "self";
                    return;
                }
            }
        }
        catch (SenderViolation e)
        {
            Violation = e.Message;
            EndedBy = "violation";
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
        {
            EndedBy ??= "timeout";
        }
        catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
        {
            EndedBy ??= "eof";
        }
        finally
        {
            _tx?.Dispose();
            _rx?.Dispose();
            _done.Set();
        }
    }

    /// <summary>
    /// Serve 出来的时候（不管怎么出来的）让发 ACK 的线程收工：不再有新的 ACK 排进来，等它把排着的发完（或者发现连接断了）出来。
    /// 等它出来了，队列才能 Dispose、连接的密钥才能清掉（排着的最多再等一个 AckLag；对面不收的话发送会卡住，所以等也有个限度）。
    /// </summary>
    private sealed class LagStop(System.Collections.Concurrent.BlockingCollection<(long, byte[])> queue, Thread? sender) : IDisposable
    {
        public void Dispose()
        {
            queue.CompleteAdding();
            sender?.Join(2000);
        }
    }

    /// <summary>一个字节一个字节地给，每 100 毫秒一个。发送端等不及断开了的话，这里抛 SocketException，由 Serve 记成 eof。</summary>
    private static void Trickle(Socket s, byte[] data)
    {
        foreach (byte b in data)
        {
            s.Send([b]);
            Thread.Sleep(100);
        }
    }

    private static void ReadAll(Socket s, Span<byte> buf)
    {
        while (buf.Length > 0)
        {
            int n = s.Receive(buf, SocketFlags.None);
            if (n == 0) throw new EndOfStreamException();
            buf = buf[n..];
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _listener.Stop();
        try { _sock?.Close(); } catch (ObjectDisposedException) { }     // 还卡在等密码、等报文上的话让它出来
        _thread.Join(2000);
    }
}
