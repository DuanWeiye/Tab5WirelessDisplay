using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace WinDisplay.Core;

/// <summary>画面来源。</summary>
public interface IFrameSource
{
    /// <summary>
    /// 取最新的横屏画面（1280×720，每像素 4 字节，一行 1280×4 字节，没有行间填充）写进 dst。
    /// 画面自上次取走之后没变过：最多等 timeout。**必须守这个时限**——会话靠它定期醒来看确认超时、取消和键盘指示灯。
    /// 返回 Frame = dst 里是新画面；NoChange = 没等到新画面（dst 不动）；End = 这个来源不会再有画面了（会话正常收尾）。
    /// 「没变过」是相对上一次返回 Frame 说的，和连接无关：掉线重连之后来源没有新画面的话，会话发它手里的那一幅，来源不用重给。
    /// timeout 可以是 0（有新画面就给，没有立刻返回 NoChange）。抛异常的那一次不算给过画面，dst 里的内容会话不再用。
    /// </summary>
    FrameResult TryGetFrame(Span<byte> dst, TimeSpan timeout);
}

public enum FrameResult { Frame, NoChange, End }

/// <summary>一次会话是怎么结束的。</summary>
public enum SessionEnd
{
    Completed,      // 画面来源结束了，已向 Tab5 发 BYE
    Cancelled,      // 调用方取消了，已向 Tab5 发 BYE（还没连上就取消的不用发；Tab5 不收数据、发不出去的时候也发不了）
    DeviceBye,      // Tab5 那边的人结束的（退出手势、离开了「无线显示」页，或者在配对的密码框上点了取消）。按协议不要立刻自动重连
    Dropped,        // 连接意外断了（Tab5 掉线、WiFi 抖动、Tab5 不确认也不收数据了，或者 Tab5 认为我们不守协议）。可以自动重连
    ConnectFailed,  // 没连上（Tab5 没开「无线显示」页时就是这样），或者握手没走完连接就断了、等过了时限
    ProtocolError,  // Tab5 发来的东西不符合协议
    AuthFailed,     // 认证没过（哪一种见 SessionResult.AuthFailure）
}

public sealed record SessionOptions
{
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromHours(1);

    public int Quality { get; init; } = 60;
    /// <summary>
    /// 画面在动时每帧的字节数预算（大片都在变的帧按它降质量，不再大片地动了就按 Quality 补清晰，见 StripEncoder）；0 = 不降。
    /// 默认 40 KB：Tab5 一个帧周期收得完、解得完，60 帧/秒时约 19 Mbps，链路（上限 26~30 Mbps）装得下。
    /// </summary>
    public int MotionFrameBytes { get; init; } = 40 * 1024;
    /// <summary>画面在动时质量最低降到多少。</summary>
    public int MinQuality { get; init; } = 20;
    /// <summary>
    /// 画质听 Tab5 的（协议「画质」）：握手时声明认得 PICTURE_MODE 和 PICTURE，Tab5 把人在它上面选的那一档报过来（老固件报的是质量和预算），
    /// 之后的帧就按那个来；Tier（没给就是 Quality 和 MotionFrameBytes）只用到它报过来之前（还有不报的更老的固件）。
    /// false = 不声明，一直按 Tier 或者 Quality 和 MotionFrameBytes（测试工具要固定参数时用）。
    /// </summary>
    public bool FollowDevicePicture { get; init; } = true;
    /// <summary>
    /// 连接开头用哪一档画质（<see cref="PictureTiers"/>：限不限帧率、画面在动时的预算怎么调都按档位来，画面不动时的质量是 <see cref="PictureTiers.StillQuality"/>）；
    /// null = 不按档位，按 Quality 和 MotionFrameBytes（质量和预算固定，不限帧率）。
    /// </summary>
    public PictureTier? Tier { get; init; }
    /// <summary>Tab5 屏幕的帧周期（微秒）：限帧率的档按它算节拍。Tab5 报了（PICTURE_MODE）就按它报的，这一项只用到那之前。</summary>
    public int RefreshPeriodUs { get; init; } = Proto.FramePeriodUsDefault;
    public int StripRows { get; init; } = 160;
    /// <summary>
    /// Tab5 能走 YUV422 帧缓冲就走（协议「画面的色彩」：条带固定 4:2:2、画面压到有限范围，平滑的灰色上没有色阶断层）。
    /// false = 不要，照旧走 RGB565（对照实验用）。Tab5 不能走（老固件）时不管这一项，都是 RGB565。
    /// </summary>
    public bool Yuv422 { get; init; } = true;
    /// <summary>走 RGB565 的连接上用的色度采样。走 YUV422 的连接固定是 4:2:2，不看这一项。</summary>
    public ChromaSubsampling Subsampling { get; init; } = ChromaSubsampling.S420;
    /// <summary>画面的字节顺序：true = B G R A（Windows 抓屏的格式），false = R G B A。</summary>
    public bool Bgra { get; init; } = true;
    /// <summary>平时保持几帧在途（已发出、还没确认），不能比 MaxInFlight 多。1 = 平时收到上一帧的确认再发下一帧（确认迟到时照样多发，最多 MaxInFlight 帧）。</summary>
    public int SteadyInFlight { get; init; } = FlowControl.DefaultSteadyInFlight;
    /// <summary>确认迟到时最多放到几帧在途，不能比 SteadyInFlight 少；和 SteadyInFlight 一样大时也没有位置多放一帧（SpareHoldMs 不起作用）。要死等确认（一帧都不多发），这两项都设成 1。</summary>
    public int MaxInFlight { get; init; } = FlowControl.DefaultMaxInFlight;
    public double MinAckWaitMs { get; init; } = FlowControl.DefaultMinAckWaitMs;
    /// <summary>跟上过画面源之后多久之内可以在 SteadyInFlight 帧之外多放一帧在途（FlowControl 的说明）；0 = 不多放（对照实验用）。</summary>
    public double SpareHoldMs { get; init; } = FlowControl.DefaultSpareHoldMs;
    /// <summary>连接和握手每一步各自的时限。</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>配对时等人在 Tab5 上输密码最多多久。Tab5 的密码框开 2 分钟，这里略长一点，正常是 Tab5 先放弃。</summary>
    public TimeSpan PairReplyTimeout { get; init; } = TimeSpan.FromSeconds(130);
    /// <summary>
    /// 一帧发出后这么久还没等到 ACK，就当 Tab5 失联、按掉线处理（Dropped）。管的是「连接还在、但 Tab5 不动了」：
    /// TCP 保活只能发现对端整个没了。正常时一帧的往返是几十到一百多毫秒，ACK 在空中丢了靠 TCP 重传也只要 1 秒多。
    /// </summary>
    public TimeSpan AckTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>检查各项取值，有不合理的抛 ArgumentOutOfRangeException；都合理就返回自己。</summary>
    public SessionOptions Validated()
    {
        if (Quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(Quality), Quality, "JPEG 质量要在 1~100");
        if (MotionFrameBytes < 0) throw new ArgumentOutOfRangeException(nameof(MotionFrameBytes), MotionFrameBytes, "画面在动时每帧的字节数预算不能是负数（0 = 不降质量）");
        if (MinQuality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(MinQuality), MinQuality, "画面在动时的最低质量要在 1~100");
        if (Tier is { } tier && !Enum.IsDefined(tier)) throw new ArgumentOutOfRangeException(nameof(Tier), Tier, "没有这一档画质");
        if (RefreshPeriodUs is < Proto.FramePeriodUsMin or > Proto.FramePeriodUsMax)
            throw new ArgumentOutOfRangeException(nameof(RefreshPeriodUs), RefreshPeriodUs, $"屏幕的帧周期要在 {Proto.FramePeriodUsMin}~{Proto.FramePeriodUsMax} 微秒");
        if (StripRows <= 0 || StripRows > Proto.FbH || StripRows % Proto.StripAlign != 0)
            throw new ArgumentOutOfRangeException(nameof(StripRows), StripRows, $"条带高要是 {Proto.StripAlign} 的正整数倍，不超过 {Proto.FbH}");
        if (MaxInFlight < 1) throw new ArgumentOutOfRangeException(nameof(MaxInFlight), MaxInFlight, "最多几帧在途至少是 1");
        if (SteadyInFlight < 1) throw new ArgumentOutOfRangeException(nameof(SteadyInFlight), SteadyInFlight, "平时几帧在途至少是 1");
        if (SteadyInFlight > MaxInFlight)
            throw new ArgumentOutOfRangeException(nameof(SteadyInFlight), SteadyInFlight,
                $"平时几帧在途不能比最多的 {MaxInFlight} 帧多（要死等确认，SteadyInFlight 和 MaxInFlight 都设成 1）");
        if (!(MinAckWaitMs > 0)) throw new ArgumentOutOfRangeException(nameof(MinAckWaitMs), MinAckWaitMs, "等 ACK 的下限必须是正数");
        if (!(SpareHoldMs >= 0) || !double.IsFinite(SpareHoldMs)) throw new ArgumentOutOfRangeException(nameof(SpareHoldMs), SpareHoldMs, "多放一帧的时限要是不小于 0 的有限数");
        if (ConnectTimeout <= TimeSpan.Zero || ConnectTimeout > MaxTimeout)
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeout), ConnectTimeout, "连接时限要大于 0、不超过 1 小时");
        if (PairReplyTimeout <= TimeSpan.Zero || PairReplyTimeout > MaxTimeout)
            throw new ArgumentOutOfRangeException(nameof(PairReplyTimeout), PairReplyTimeout, "等人输密码的时限要大于 0、不超过 1 小时");
        if (AckTimeout <= TimeSpan.Zero || AckTimeout > MaxTimeout)
            throw new ArgumentOutOfRangeException(nameof(AckTimeout), AckTimeout, "确认时限要大于 0、不超过 1 小时");
        return this;
    }
}

/// <summary>一帧从发出到确认的记录。</summary>
public readonly record struct FrameStats(uint Seq, int Strips, int Bytes, double EncodeMs, double SendGapMs, double RttMs, Ack Ack)
{
    /// <summary>
    /// 上一帧发出之后到这一帧发出之前，会话卡在「等流量控制放行」（等 Tab5 的 ACK）上的毫秒数。
    /// 和 <see cref="FetchMs"/>、<see cref="EncodeMs"/> 加起来大致就是 <see cref="SendGapMs"/>（差的是上一帧往连接里写花的时间）：
    /// 两帧隔得久的时候，看这三项就知道是 Tab5 / 网络来不及、来源没给新画面，还是编码慢。
    /// </summary>
    public double WaitMs { get; init; }
    /// <summary>
    /// 同一段时间里花在「向来源要画面」上的毫秒数：等画面变化，加上来源自己取画面的时间（抓屏的缩放、读回）。
    /// 限帧率的档等下一拍的时间也算在这里（都是「可以发了、还不该发」）。
    /// </summary>
    public double FetchMs { get; init; }
    /// <summary>这一帧是按什么 JPEG 质量压的（画面在动时会比设定的低，见 SessionOptions.MotionFrameBytes）。</summary>
    public int Quality { get; init; }
    /// <summary>压这一帧的时候，画面在动时每帧的字节数预算是多少（按档位的连接上它是闭环调的，见 BudgetControl）；0 = 不降。</summary>
    public int BudgetBytes { get; init; }
    /// <summary>这一帧算「在动」（大片都在变、按预算定的质量）。</summary>
    public bool Motion { get; init; }
    /// <summary>这一帧发出的时候管线跟得上（<see cref="FlowControl.LastSentKeptUp"/>）：闭环调预算看的就是在动的帧里它占多少。</summary>
    public bool KeptUp { get; init; }
}

public sealed record SessionResult(SessionEnd End, string? Detail, long FramesSent, long FramesAcked, int LateSends, int Quality, TimeSpan Elapsed)
{
    /// <summary>这条连接走的是 YUV422 帧缓冲（握手里定的）。没连上、走 RGB565 的都是 false。</summary>
    public bool Yuv422 { get; init; }
    /// <summary>跟得上画面源、在平时的在途帧数之外多发的帧数（FlowControl 的说明）。</summary>
    public int SpareSends { get; init; }
    /// <summary>End 是 AuthFailed 时：认证没过是哪一种。别的时候是 null。</summary>
    public AuthFailure? AuthFailure { get; init; }
    /// <summary>这条连接上发给 Tab5 的声音（毫秒）。没有声音来源、Tab5 不收声音、电脑一直没出声都是 0。</summary>
    public double AudioSentMs { get; init; }
    /// <summary>连接堵住时没发出去、扔掉的声音（毫秒）。</summary>
    public double AudioDroppedMs { get; init; }
}

/// <summary>
/// 显示会话：等来源有了画面 → 连上 Tab5、握手（认证，没配对过的先配对）→ 反复「等到可以发 → 取最新画面 → 压变化的条带 → 发」，
/// 同时收 Tab5 的确认和输入事件，直到一方结束。
/// 等 ACK 期间画面变了多少次都不排队，轮到发的时候只取最新的画面：Tab5 或网络来不及时掉的是帧率，延迟不会越积越多。
/// 给了声音来源、Tab5 又说能收声音（HELLO 的能力位）的话，握手走完起把电脑的声音也发过去（协议「声音」），到这条连接结束为止。
/// 同一个对象可以一次接一次地 Run（掉线重连就是这么用的），但同一时刻只能有一个 Run。
///
/// 事件（Touch / Key / Mouse / Held / InputMode / TouchpadGain / Picture / PictureMode / FrameAcked）在收报文的线程上回调，Run 返回之后不会再有回调。回调里不要做慢事，
/// 也**不要同步等别的线程**（比如 Control.Invoke 到界面线程）：界面线程要是正在等 Run 结束，两边就互相等死了；用 BeginInvoke / Post。
///
/// Run 抛异常 = 本端的代码出了错（画面来源、编码器、某个事件回调里抛的，或者握手走完时配对存不进文件——这一种是 PairStoreException）；抛的就是原来那个异常。
/// 抛出来之前连接已经收干净，也给 Tab5 发过 BYE 了（握手还没走完时出的错除外：那时按协议不能发 BYE，直接断开）。
/// </summary>
public sealed class DisplaySession
{
    private const double RefineAfterMs = 150;       // 这么久没出过在动的帧，就把降过质量的地方补清晰（滚动中间顿一下不算停：补一次是整屏按原质量的一大帧）
    private static readonly TimeSpan SourcePoll = TimeSpan.FromMilliseconds(100);   // 没有新画面时，隔这么久醒来看一眼别的事
    private const int ReaderGraceMs = 300;      // 发送失败后给读线程这么久：Tab5 刚发的 BYE 可能还没读到
    private const int ByeGraceMs = 300;         // 发 BYE 最多卡这么久；发完后最多等 Tab5 这么久关连接
    private const int StuckSendGraceMs = 300;   // 会话已经定了要结束、主线程却还卡在写上：给它这么久写完（正常的一帧够了），写不完就掐断连接

    private readonly IFrameSource _source;
    private readonly SessionOptions _options;
    private readonly LinkAuth _auth;
    private readonly IAudioSource? _audio;
    private readonly byte[] _view = new byte[Proto.ViewW * 4 * Proto.ViewH];    // 来源最近给的那一幅画面，跨连接留着
    private bool _haveView;
    private int _ledsWanted = -1;               // 调用方要的键盘指示灯状态；-1 = 从没设过
    private int _running;

    /// <param name="auth">配对存储（里面有这台电脑的编号）、报给 Tab5 的名字、配对密码和密码不对的次数。</param>
    /// <param name="audio">声音来源；null = 不发声音。每条连接握手走完后 Start 一次（Tab5 不收声音的不 Start），连接结束时 Dispose 它返回的对象。</param>
    public DisplaySession(IFrameSource source, SessionOptions options, LinkAuth auth, IAudioSource? audio = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Validated();
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _audio = audio;
    }

    /// <summary>当前所有触点的快照：某个编号不在里面了就是抬起，空数组 = 全部抬起。</summary>
    public event Action<TouchPoint[]>? Touch;
    /// <summary>接在 Tab5 上的键盘的 8 字节 USB HID Boot 报文。</summary>
    public event Action<byte[]>? Key;
    public event Action<MouseEvent>? Mouse;
    /// <summary>
    /// Tab5 报：它那边的键 / 触点还按着（协议「按着的时候」）。Tab5 突然没了的时候连接要过好几秒才断，
    /// 所以照着 Tab5 的状态按时间做事的一方（按键连发、触点重报）要拿 Touch、Key、Held 这三样判断那份状态还新不新鲜。
    /// </summary>
    public event Action? Held;
    /// <summary>
    /// Tab5 报：它那边选的触摸方式（协议「触摸方式」）。true = 触摸板模式（把 Tab5 的屏幕当触摸板，触点要换成鼠标动作），false = 照原样注入成触摸。
    /// 一条连接上 Tab5 没报过就是 false（老固件不报）；它握手走完报一次，之后变了再报。
    /// </summary>
    public event Action<bool>? InputMode;
    /// <summary>
    /// Tab5 报：触摸板模式下手指滑得快时光标最多放大到几倍（人在 Tab5 上选的「触摸板加速」，协议「触摸方式」）。1 = 不加速。
    /// 一条连接上 Tab5 没报过就按 <see cref="TouchpadMapper.DefaultMaxGain"/>（老固件不报）；它握手走完报一次，之后变了再报。
    /// </summary>
    public event Action<double>? TouchpadGain;
    /// <summary>
    /// 老固件（不认得画质档位的）报：人在它上面选的画质（协议「画质」）——画面不动时的 JPEG 质量、画面在动时每帧的预算（KB，0 = 不降）。会话自己会照着改，这个事件只是告诉调用方一声。
    /// SessionOptions.FollowDevicePicture 关着就不会有（没声明认得，Tab5 不发）；Tab5 握手走完报一次，之后变了再报。
    /// </summary>
    public event Action<int, int>? Picture;
    /// <summary>
    /// Tab5 报：人在它上面选的画质是哪一档、它的屏幕的帧周期（微秒）（协议「画质」）。会话自己会照着改（限帧率、调预算），这个事件只是告诉调用方一声。
    /// SessionOptions.FollowDevicePicture 关着就不会有；Tab5 握手走完报一次，之后变了再报。
    /// </summary>
    public event Action<PictureTier, int>? PictureMode;
    public event Action<FrameStats>? FrameAcked;
    /// <summary>连上了，握手也走完了（两边互相认证过）。在调用 Run 的线程上回调。</summary>
    public event Action<Hello>? Connected;
    /// <summary>
    /// 这条连接上在配对，走到哪一步了（等人在 Tab5 上输密码、密码不对、对上了）。在调用 Run 的线程上回调，Connected 之前。
    /// 握手正停在回调里，而 Tab5 每一步只等 5 秒：回调里**不许等别的线程、不许做耗时的事**（要更新界面就 BeginInvoke / Post）。
    /// PairingGate.WrongCountChanged 的处理函数同理（它在「密码不对」和下一个 PAIR 之间被调）。
    /// </summary>
    public event Action<PairingStep>? Pairing;

    private readonly record struct SentFrame(int Strips, int Bytes, double EncodeMs, double SendGapMs, double WaitMs, double FetchMs, int Quality,
                                             int BudgetBytes, bool Motion, bool KeptUp);

    // 「现在该用的画质」记成一个数（读线程写、主线程读，后到的算数）：-1 = 没有（按选项里的 Quality / MotionFrameBytes）；
    // 老固件报的 PICTURE = 它的 arg（0~65535）；档位 = PictureModeFlag | 第几档 << 32 | 屏幕的帧周期（微秒）
    private const long PictureModeFlag = 1L << 40;
    private static long PictureModeCode(PictureTier tier, int periodUs) => PictureModeFlag | (long)tier << 32 | (uint)periodUs;

    /// <summary>
    /// 告诉 Tab5 这台电脑的键盘指示灯状态（bit0 Num Lock、bit1 Caps Lock、bit2 Scroll Lock），Tab5 写给接在它上面的键盘。
    /// 只是记下来，立刻返回，哪个线程都能调（键盘钩子里也行）；会话在两帧之间把最新的状态发出去（画面静止时最晚约 0.1 秒；
    /// 正在等一帧的确认或者正发着一个大帧时，要等它完），每次连上后也会先补发一遍，调用方不用管连没连着。
    /// </summary>
    public void SetKeyboardLeds(byte leds) => Volatile.Write(ref _ledsWanted, leds & 0x07);

    public SessionResult Run(string host, int port, CancellationToken ct) => Run(host, port, null, ct);

    /// <summary>
    /// 连自动发现找到的这一台，其余同上。Tab5 在 HELLO 里报的编号必须是它应答时报的那个，不是就按「不守协议」结束
    /// （PROTOCOL.md「自动发现」）：调用方按应答里的编号定了连不连它、认它是哪一台，连上的得是同一台。
    /// </summary>
    public SessionResult Run(DiscoveredTab5 tab5, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tab5);
        return Run(tab5.Address.ToString(), tab5.Port, tab5.Id, ct);
    }

    private SessionResult Run(string host, int port, byte[]? expectTab5Id, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) throw new InvalidOperationException("这个会话正在跑：同一时刻只能有一个 Run");
        try
        {
            return RunCore(host, port, expectTab5Id, ct);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private SessionResult RunCore(string host, int port, byte[]? expectTab5Id, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        SessionResult Early(SessionEnd how, string? why = null) => new(how, why, 0, 0, 0, _options.Quality, clock.Elapsed);

        // 向来源要画面，写进 _view。来源写到一半抛异常的话 _view 里是半截画面，不能再当成「手里的那一幅」，所以先把标志清掉
        FrameResult Fetch(TimeSpan timeout)
        {
            bool had = _haveView;
            _haveView = false;
            FrameResult got = _source.TryGetFrame(_view, timeout);
            _haveView = had || got == FrameResult.Frame;
            return got;
        }

        // 0. 先有画面再连接。Tab5 只有一个连接的位置，连上 5 秒内没发完第一帧就断开：手里没有画面时连上去，只会占着位置等着被踢。
        //    以前连过的话手里已经有一幅了
        bool fresh = false;                         // 手里这一幅是这次 Run 刚取的
        while (!_haveView)
        {
            if (ct.IsCancellationRequested) return Early(SessionEnd.Cancelled);
            FrameResult first = Fetch(SourcePoll);
            if (first == FrameResult.End) return Early(SessionEnd.Completed);
            fresh = first == FrameResult.Frame;
        }

        Tab5Link link;
        bool paired = false;                        // 这条连接上配过对
        try
        {
            link = Tab5Link.Connect(host, port, _auth, _options.ConnectTimeout, pairReplyTimeout: _options.PairReplyTimeout,
                                    onPairing: step => { paired = true; Pairing?.Invoke(step); }, ct: ct, expectTab5Id: expectTab5Id,
                                    caps: Proto.SenderCaps & ~(_options.FollowDevicePicture ? 0 : Proto.CapPicture | Proto.CapPictureMode)
                                                           & ~(_options.Yuv422 ? 0 : Proto.CapYuv422));
        }
        catch (LocalFaultException e)
        {
            // 握手期间本端的代码出的错（配对存不进文件、回调里抛的）：把原来的异常抛给 Run 的调用方。连接已经关掉
            ExceptionDispatchInfo.Capture(e.InnerException!).Throw();
            throw;      // 走不到这里
        }
        catch (OperationCanceledException)
        {
            return Early(SessionEnd.Cancelled);
        }
        catch (DeviceByeException)
        {
            return Early(SessionEnd.DeviceBye);
        }
        catch (AuthException e)
        {
            return Early(SessionEnd.AuthFailed, e.Message) with { AuthFailure = e.Reason };
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            return Early(SessionEnd.ConnectFailed, e.Message);
        }
        catch (ProtocolException e)
        {
            return Early(SessionEnd.ProtocolError, e.Message);
        }

        // 配对要等人在 Tab5 上输密码，手里那一幅可能已经是一两分钟前的了：第一帧之前再问一下来源
        if (paired) fresh = false;

        // 选项在构造会话时查过、HELLO 里的上限在 Connect 里查过，下面两个构造不会抛
        var encoder = new StripEncoder(link.Hello.MaxStripBytes, link.Hello.MaxFrameBytes, _options.Quality, _options.StripRows,
                                       _options.Subsampling, _options.Bgra, _options.MotionFrameBytes, _options.MinQuality, link.Yuv422);
        var flow = new FlowControl(_options.SteadyInFlight, _options.MaxInFlight, _options.MinAckWaitMs, _options.SpareHoldMs);
        var gate = new object();                    // 保护 flow、inFlight、end、detail、fault、acked；状态变了就 PulseAll
        var inFlight = new Queue<SentFrame>();      // 和 flow 里在途的帧一一对应
        SessionEnd? end = null;
        string? detail = null;
        ExceptionDispatchInfo? fault = null;        // 事件回调里抛出来的异常：连接收干净之后在调用 Run 的线程上重新抛出
        long sent = 0, acked = 0;
        bool sending = false;                       // 主线程正在往连接里写（gate 保护）
        Timer? killer = null;                       // 见 Finish（gate 保护）
        double ackTimeoutMs = _options.AckTimeout.TotalMilliseconds;
        // 现在该用的画质（见 PictureModeCode）：开头是选项里的那一档，Tab5 报了就换成它报的。读线程写、主线程读
        long pictureWanted = _options.Tier is { } startTier ? PictureModeCode(startTier, _options.RefreshPeriodUs) : -1;

        void Finish(SessionEnd how, string? why = null)
        {
            lock (gate)
            {
                if (end is null) { end = how; detail = why; }       // 先到的原因算数（我们自己关连接时，读线程随后报的「断了」不算）
                Monitor.PulseAll(gate);
                // 主线程这时正在写：正常的话马上写完、回头就看到 end。Tab5 不收数据了（TCP 窗口一直是满的）的话它会卡到发送超时，
                // 所以过一小会儿还在写就把连接掐断让它出来——不然调用方取消之后还要等上好几秒
                if (sending)
                    killer ??= new Timer(_ =>
                    {
                        // 锁里只看一眼，出了锁再关：关连接要等卡着的那次写退出来才算完，这期间不能占着 gate
                        // （取消回调也要拿 gate，它跑在调 Cancel() 的线程上，可能是界面线程）。
                        // 看完到关之间主线程正好写完了也没事：最坏是紧接着的 BYE 发不出去
                        bool stuck;
                        lock (gate) stuck = sending;
                        if (stuck) link.Dispose();
                    }, null, StuckSendGraceMs, Timeout.Infinite);
            }
        }

        // 离确认超时还有多少毫秒。没有在途的帧 = 正无穷；≤ 0 = 最早那一帧发出去超过 AckTimeout 还没确认，会话记成掉线。要拿着 gate 调
        double AckTimeLeft()
        {
            if (flow.InFlight == 0) return double.PositiveInfinity;
            double left = flow.OldestSentMs + ackTimeoutMs - clock.Elapsed.TotalMilliseconds;
            if (left <= 0 && end is null)
            {
                end = SessionEnd.Dropped;
                detail = $"Tab5 超过 {_options.AckTimeout.TotalSeconds:G} 秒没有确认发出去的帧";
            }
            return left;
        }

        // 在 gate 上等状态变化，最多 ms 毫秒（正无穷 = 不限），而且不会睡过确认超时的时刻。要拿着 gate 调；醒来后调用方自己看 end
        void WaitGate(double ms)
        {
            ms = Math.Min(ms, AckTimeLeft());
            if (ms <= 0) return;
            if (double.IsPositiveInfinity(ms)) Monitor.Wait(gate);
            else Monitor.Wait(gate, (int)Math.Clamp(Math.Ceiling(ms), 1, int.MaxValue));
        }

        // 往连接里写东西。写不出去 = 连接断了；先给读线程一点时间再定性：Tab5 可能是发了 BYE 才关的连接，那该报「Tab5 结束的」而不是掉线。
        // 返回 false = 没写成，会话该结束了
        bool TrySend(Action write)
        {
            lock (gate)
            {
                if (end is not null) return false;
                sending = true;
            }
            try
            {
                write();
                return true;
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                lock (gate)
                {
                    double deadline = clock.Elapsed.TotalMilliseconds + ReaderGraceMs;
                    while (end is null)         // 别的事（比如来了一个 ACK）也会把这里唤醒，所以要看着时刻等
                    {
                        double left = deadline - clock.Elapsed.TotalMilliseconds;
                        if (left <= 0) break;
                        Monitor.Wait(gate, (int)Math.Ceiling(left));
                    }
                    if (end is null) { end = SessionEnd.Dropped; detail = e.Message; }
                }
                return false;
            }
            finally
            {
                lock (gate) sending = false;
            }
        }

        var reader = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    Message m = link.Read();
                    switch (m.Type)
                    {
                        case Proto.TAck:
                            Ack ack = Proto.ParseAck(m.Payload);
                            double rtt;
                            SentFrame f;
                            lock (gate)
                            {
                                rtt = flow.OnAck(ack.Seq, clock.Elapsed.TotalMilliseconds);
                                f = inFlight.Dequeue();
                                acked++;
                                Monitor.PulseAll(gate);
                            }
                            FrameAcked?.Invoke(new FrameStats(ack.Seq, f.Strips, f.Bytes, f.EncodeMs, f.SendGapMs, rtt, ack)
                            {
                                WaitMs = f.WaitMs, FetchMs = f.FetchMs, Quality = f.Quality, BudgetBytes = f.BudgetBytes, Motion = f.Motion, KeptUp = f.KeptUp,
                            });
                            break;
                        case Proto.TTouch:
                            Touch?.Invoke(Proto.ParseTouch(m.Arg, m.Payload));
                            break;
                        case Proto.TKey:
                            if (m.Payload.Length != Proto.KeySize) throw new ProtocolException($"KEY 载荷 {m.Payload.Length} 字节，应为 {Proto.KeySize}");
                            Key?.Invoke(m.Payload);
                            break;
                        case Proto.TMouse:
                            Mouse?.Invoke(Proto.ParseMouse(m.Payload));
                            break;
                        case Proto.THeld:
                            if (m.Payload.Length != 0) throw new ProtocolException($"HELD 带了 {m.Payload.Length} 字节的载荷");
                            Held?.Invoke();
                            break;
                        case Proto.TInputMode:
                            if (m.Payload.Length != 0 || m.Arg > 1) throw new ProtocolException($"INPUT_MODE 的 arg 是 {m.Arg}、载荷 {m.Payload.Length} 字节，应为 0 或 1、没有载荷");
                            InputMode?.Invoke(m.Arg == 1);
                            break;
                        case Proto.TTouchpadGain:
                            if (m.Payload.Length != 0 || m.Arg < Proto.TouchpadGainMin || m.Arg > Proto.TouchpadGainMax)
                                throw new ProtocolException($"TPAD_GAIN 的 arg 是 {m.Arg}、载荷 {m.Payload.Length} 字节，应为 {Proto.TouchpadGainMin}~{Proto.TouchpadGainMax}、没有载荷");
                            TouchpadGain?.Invoke(m.Arg / 10.0);
                            break;
                        case Proto.TPicture when _options.FollowDevicePicture:      // 没声明认得的话 Tab5 不该发：落到下面「不认识的报文」
                            int quality = m.Arg & 0xFF, motionKb = m.Arg >> 8;
                            if (m.Payload.Length != 0 || quality is < 1 or > 100)
                                throw new ProtocolException($"PICTURE 的质量是 {quality}、载荷 {m.Payload.Length} 字节，应为 1~100、没有载荷");
                            Volatile.Write(ref pictureWanted, m.Arg);
                            Picture?.Invoke(quality, motionKb);
                            break;
                        case Proto.TPictureMode when _options.FollowDevicePicture:  // 同上
                            if (m.Arg >= PictureTiers.Count || m.Payload.Length != Proto.PictureModeSize)
                                throw new ProtocolException($"PICTURE_MODE 的档位是 {m.Arg}、载荷 {m.Payload.Length} 字节，应为 0~{PictureTiers.Count - 1}、{Proto.PictureModeSize} 字节");
                            uint periodUs = BinaryPrimitives.ReadUInt32LittleEndian(m.Payload);
                            if (periodUs is < Proto.FramePeriodUsMin or > Proto.FramePeriodUsMax)
                                throw new ProtocolException($"PICTURE_MODE 里屏幕的帧周期是 {periodUs} 微秒，应在 {Proto.FramePeriodUsMin}~{Proto.FramePeriodUsMax}");
                            Volatile.Write(ref pictureWanted, PictureModeCode((PictureTier)m.Arg, (int)periodUs));
                            PictureMode?.Invoke((PictureTier)m.Arg, (int)periodUs);
                            break;
                        case Proto.TDevBye:
                            Finish(SessionEnd.DeviceBye);
                            return;
                        default:
                            throw new ProtocolException($"不认识的报文类型 0x{m.Type:x2}（arg={m.Arg}，载荷 {m.Payload.Length} 字节）");
                    }
                }
            }
            catch (ProtocolException e) { Finish(SessionEnd.ProtocolError, e.Message); }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { Finish(SessionEnd.Dropped, e.Message); }
            catch (Exception e)         // 事件回调里抛出来的：本端结束这次会话（主循环停下、给 Tab5 发 BYE），异常留给 Run 的调用方
            {
                lock (gate) fault ??= ExceptionDispatchInfo.Capture(e);
                Finish(SessionEnd.Cancelled);
            }
        }) { IsBackground = true, Name = "wd-reader" };

        uint seq = 0;
        double lastSendMs = double.NaN;
        double lastMotionMs = double.NegativeInfinity;      // 上一次发「在动」的帧（大片都在变、降了质量发的）是什么时候
        double waitAcc = 0, fetchAcc = 0;           // 上一帧发出之后，等流量控制、向来源要画面各花了多少毫秒（见 FrameStats）
        int ledsSent = -1;                          // 这条连接上发过的指示灯状态
        long pictureApplied = -1;                   // 现在用的是哪一份画质（见 PictureModeCode）；-1 = 选项里的 Quality / MotionFrameBytes
        FramePacer? pacer = null;                   // 限帧率的档：发帧的节拍；null = 不限
        BudgetControl? budget = null;               // 按档位闭环调「画面在动时每帧的预算」；null = 预算是固定的（或者不降）
        IDisposable? audioRun = null;               // 正抓着的声音
        try
        {
            // 限帧率的档一拍是 22.9 / 30.6 毫秒，下面等拍子、等确认都靠 Monitor.Wait：系统默认的定时精度（Windows 上 15.6 毫秒）卡不准
            using var precise = PreciseTimer.Raise();
            Connected?.Invoke(link.Hello);
            // 取消：记下结束原因并唤醒主线程。ct 早就取消了的话，这里登记时回调立刻执行
            // （在读线程开始之前登记：这时「取消」一定先记上，读线程随后报什么都不算）
            using var onCancel = ct.Register(() => Finish(SessionEnd.Cancelled));
            reader.Start();
            // 声音：Tab5 说了能收才发。抓到的每一段直接交给连接（它自己排队、夹在条带之间发），不经过这个循环
            if (_audio is not null && (link.Hello.Caps & Proto.CapDevAudio) != 0) audioRun = _audio.Start(link.QueueAudio);
            while (true)
            {
                // 0. Tab5 上的人改了画质（每条连接的第一轮也走这里：选项里给了档位的话在这儿用上）：从这一轮要发的帧起按新的来。
                //    放在等流量控制之前：换档会改「平时几帧在途」，这一帧就该按新的数放行（等的时候才到的，下一轮用上，晚一帧）。
                //    质量调高了的话 Tab5 上已有的画面算「降过质量的」，下面照常补
                long picture = Volatile.Read(ref pictureWanted);
                if (picture != pictureApplied)
                {
                    int steady = _options.SteadyInFlight;
                    if ((picture & PictureModeFlag) != 0)
                    {
                        // 档位：限不限帧率、预算从多少起调、在哪个范围里调、平时几帧在途都按档位来（PictureTiers）
                        TierPlan plan = PictureTiers.Plan((PictureTier)(int)(picture >> 32 & 0xFF));
                        steady = Math.Min(_options.MaxInFlight, Math.Max(steady, plan.InFlight));
                        double fps = 1e6 / (uint)picture * plan.FramesPerRefresh;
                        if (pictureApplied >> 32 == picture >> 32)
                        {
                            // 还是那一档，只是屏幕的帧周期不一样（Tab5 每次量出来差几微秒；托盘记着的是上一条连接报的）：
                            // 节拍换成新的周期，预算接着用闭环已经找到的数，不从头起
                            pacer?.SetFps(fps);
                        }
                        else
                        {
                            // 换了一档：节拍和预算都从头起
                            pacer = plan.FramesPerRefresh > 0 ? new FramePacer(fps) : null;
                            budget = plan.MaxBytes > 0 ? new BudgetControl(plan.StartBytes, plan.MinBytes, plan.MaxBytes) : null;
                            encoder.SetPicture(PictureTiers.StillQuality, budget?.Bytes ?? 0);
                        }
                    }
                    else
                    {
                        // 老固件报的质量和预算：固定用它，不限帧率
                        pacer = null;
                        budget = null;
                        encoder.SetPicture((int)(picture & 0xFF), (int)(picture >> 8) * 1024);
                    }
                    lock (gate) flow.SetSteadyInFlight(steady);
                    pictureApplied = picture;
                }

                // 1. 等到可以发下一帧（流量控制），或者会话该结束了。每一轮都看确认超时：画面不动的时候循环不在这里睡，也得查到
                double stepAt = clock.Elapsed.TotalMilliseconds;
                lock (gate)
                {
                    while (end is null && AckTimeLeft() > 0)
                    {
                        double wait = flow.WaitBeforeNextMs(clock.Elapsed.TotalMilliseconds);
                        if (wait <= 0) break;
                        WaitGate(wait);
                    }
                    if (end is not null) break;
                }
                waitAcc += clock.Elapsed.TotalMilliseconds - stepAt;

                // 2. 键盘指示灯有变化就发（每条连接的第一轮也走这里：补发最新状态）
                int leds = Volatile.Read(ref _ledsWanted);
                if (leds != ledsSent)
                {
                    if (!TrySend(() => link.SendSimple(Proto.TKbdLeds, (ushort)leds))) break;
                    ledsSent = leds;
                }

                // 3. 取最新的画面。这条连接的第一帧：手里那一幅是这次 Run 刚取的就直接发；是以前留下的（可能隔了很久）就先问一下来源，
                //    但不等——有新的用新的，来源说没变过就发手里的：Tab5 上还没有我们的画面，而来源要等画面变了才给新的
                bool firstFrame = sent == 0;
                bool idled = false;                         // 这一轮等过：等下一拍，或者等画面源出新画面
                double paceAt;                              // 这一帧占的是哪一刻的拍子（见下面取画面的地方）
                if (pacer is not null && !firstFrame)
                {
                    // 限帧率的档：还没到下一拍就等到了再取画面（取的是那一刻最新的一幅）。和第 1 步一样看着会话结束和确认超时
                    stepAt = clock.Elapsed.TotalMilliseconds;
                    lock (gate)
                    {
                        while (end is null && AckTimeLeft() > 0)
                        {
                            double wait = pacer.WaitMs(clock.Elapsed.TotalMilliseconds);
                            if (wait <= 0) break;
                            WaitGate(wait);
                            idled = true;
                        }
                        if (end is not null) break;
                    }
                    fetchAcc += clock.Elapsed.TotalMilliseconds - stepAt;
                }
                if (!firstFrame || !fresh)
                {
                    stepAt = clock.Elapsed.TotalMilliseconds;
                    // 有降过质量的地方等着补的时候，最多等到该补的那一刻（往上取整到毫秒：等够了才回来，不会差一点点又等一轮）
                    TimeSpan poll = firstFrame ? TimeSpan.Zero : SourcePoll;
                    if (!firstFrame && encoder.NeedsRefine)
                    {
                        double left = lastMotionMs + RefineAfterMs - stepAt;
                        if (left < poll.TotalMilliseconds) poll = TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(left)));
                    }
                    // 先不等地问一次：已经有新画面等着就直接用。没有 = 我们可以发了、画面源却还没出新的一幅：等它。
                    // 等过（等拍子也算）就告诉流量控制：这时在途的帧数说明管线跟不跟得上，它据此决定能不能多放一帧在途，
                    // 闭环调预算看的也是它（FlowControl.LastSentKeptUp）
                    // 节拍从开始取画面的那一刻算（画面是现成的：取它、压它花的时间都算在这一拍里，不然取得慢、压得慢的电脑上每帧都要多等一拍）；
                    // 画面是等来的，就从等到的那一刻算（等的是画面源，不是我们慢）
                    paceAt = stepAt;
                    FrameResult got = Fetch(TimeSpan.Zero);
                    if (got == FrameResult.NoChange && poll > TimeSpan.Zero)
                    {
                        got = Fetch(poll);
                        idled = true;
                        paceAt = clock.Elapsed.TotalMilliseconds;
                    }
                    if (idled) lock (gate) flow.OnSourceIdle(clock.Elapsed.TotalMilliseconds);
                    fetchAcc += clock.Elapsed.TotalMilliseconds - stepAt;
                    if (got == FrameResult.End)
                    {
                        lock (gate)         // 等在途的几帧都确认了再收尾
                        {
                            while (end is null && flow.InFlight > 0) WaitGate(double.PositiveInfinity);
                        }
                        Finish(SessionEnd.Completed);
                        break;
                    }
                    if (got == FrameResult.NoChange && !firstFrame)
                    {
                        // 没有新画面。有降过质量的地方、也到了该补的时候，就往下走：拿手里那一幅（就是 Tab5 上的那一幅）再压一遍，压出来的只有要补的条带
                        if (!encoder.NeedsRefine || clock.Elapsed.TotalMilliseconds - lastMotionMs < RefineAfterMs) continue;
                    }
                }
                else paceAt = clock.Elapsed.TotalMilliseconds;

                // 4. 压变化的条带（画面和 Tab5 上的一样就没有条带，不发）。有 RefineAfterMs 没出过在动的帧了，就把降过质量的条带也按原质量带上：
                //    不管这一轮来源给没给新画面——整屏停了、还有一小块一直在变的画面（动图、转圈）永远等不到「没变」
                double t0 = clock.Elapsed.TotalMilliseconds;
                List<Strip> strips = encoder.Encode(_view, Proto.ViewW * 4, refine: t0 - lastMotionMs >= RefineAfterMs);
                if (strips.Count == 0) continue;
                int bytes = 0;
                foreach (var s in strips) bytes += s.Jpeg.Length;

                // 5. 发。先登记再发：ACK 可能在发送返回之前就到了
                double now = clock.Elapsed.TotalMilliseconds;
                if (encoder.LastWasMotion) lastMotionMs = now;
                bool keptUp;
                lock (gate)
                {
                    if (end is not null) break;
                    flow.OnSent(seq, now);
                    keptUp = flow.LastSentKeptUp;
                    inFlight.Enqueue(new SentFrame(strips.Count, bytes, now - t0, double.IsNaN(lastSendMs) ? 0 : now - lastSendMs, waitAcc, fetchAcc, encoder.LastQuality,
                                                   encoder.MotionBudget, encoder.LastWasMotion, keptUp));
                }
                pacer?.Given(paceAt);
                if (budget is not null)             // 按这一帧跟没跟上调预算，下一帧起用
                {
                    budget.OnFrame(encoder.LastWasMotion, keptUp, bytes, now);
                    if (budget.Bytes != encoder.MotionBudget) encoder.SetMotionBudget(budget.Bytes);
                }
                waitAcc = fetchAcc = 0;
                lastSendMs = now;
                if (!TrySend(() => link.SendFrame(strips, seq, (uint)(long)now))) break;
                encoder.Commit();
                seq++;
                sent++;
            }
        }
        finally
        {
            // 不管怎么出来的都把连接收干净。是本端要结束的（画面发完、调用方取消，或者本端的代码抛了异常——这时 end 还空着）先发 BYE，
            // Tab5 立刻回到自己的界面。发完只关发送方向、等 Tab5 关连接（读线程读到头）再全关：
            // 接收缓冲里还有没读的 ACK / 触摸时直接关，系统会发 RST，还没发出去的 BYE 也跟着丢
            SessionEnd? how;
            lock (gate)
            {
                how = end;
                killer?.Dispose();      // 主线程已经不在写了，用不着它了（它正好在这时到点也没事：看到 sending 是 false 就什么都不做）
            }
            link.StopAudio();           // BYE 之后不能再有声音；正在发的那一段照常发完
            if (how is null or SessionEnd.Completed or SessionEnd.Cancelled)
            {
                try
                {
                    link.SetSendTimeout(TimeSpan.FromMilliseconds(ByeGraceMs));     // 系统的发送缓冲正好满着的话，别为了 8 个字节卡上几秒
                    // 抓声音的线程正卡在一次发送上（Tab5 不收数据了）的话也只等这么久：等不到就不发 BYE 了，下面关连接时它会出来
                    link.SendSimple(Proto.TBye, lockTimeoutMs: ByeGraceMs);
                    link.ShutdownSend();
                    if (reader.IsAlive) reader.Join(ByeGraceMs);
                }
                catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
            }
            link.Dispose();                         // 读线程正卡在收报文上，关了连接它才出得来
            audioRun?.Dispose();                    // 声音停抓（来源的线程要是正卡在发送上，连接关了它就出来了）：Run 返回之后来源不再回调
            if (reader.IsAlive) reader.Join();      // 等它出来：Run 返回之后不能再有回调
        }
        lock (gate)
        {
            fault?.Throw();
            var (audioSent, audioDropped) = link.AudioStats;
            return new SessionResult(end!.Value, detail, sent, acked, flow.LateSends, encoder.Quality, clock.Elapsed)
            {
                AudioSentMs = audioSent, AudioDroppedMs = audioDropped, SpareSends = flow.SpareSends, Yuv422 = link.Yuv422,
            };
        }
    }
}
