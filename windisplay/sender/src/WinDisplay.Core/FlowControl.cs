namespace WinDisplay.Core;

/// <summary>
/// 流量控制（PROTOCOL.md「流量控制」）：限制「已发出 PRESENT、还没收到 ACK」的帧数。
/// 平时保持 SteadyInFlight 帧在途：在途的不到这么多就可以发下一帧，不用等 ACK（Tab5 解码、等翻页的时候下一帧已经在路上了）。
/// 到了 SteadyInFlight 帧就等 ACK；最早在途的那一帧迟迟等不到 ACK 就接着发，最多 MaxInFlight 帧在途。
/// 「迟迟」= 从那一帧发出算起，超过 MinAckWaitMs 和「最近 RttWindow 帧往返时间的中位数 × 2」里大的那个
/// （画面重、每帧本来就慢的时候不算迟到）；收到的 ACK 不到 RttMinSamples 个之前不算迟到（往返时间还没摸准）。
/// 多放一帧（SpareHoldMs）：发送端最近 SpareHoldMs 之内「跟上过画面源」的话，在途的到了 SteadyInFlight 帧还可以再发一帧，不等 ACK。
/// 「跟上过」= 连着 KeepUpFrames 帧，发送端都是等画面源等来的（可以发了、它却还没出新的一幅），等完的时候在途的不到 SteadyInFlight 帧；
/// 调用方用 OnSourceIdle 告诉我们。
/// 为什么要有：Tab5 按屏幕的刷新一帧一帧上屏，哪一帧晚到几毫秒、错过了一次刷新，后面的帧都跟着晚一个刷新周期（15 ms），
/// 要靠屏幕比画面源快的那一点（65 Hz 对 60 幅/秒，每帧追回 1 ms 多）十几帧才追得回来。这期间 ACK 都晚，
/// 只许 SteadyInFlight 帧在途的话新抓到的那一幅只能等，等的时候再来一幅它就被顶掉了（实测每分钟一百多幅）；
/// 多放一帧，晚的这一段就排在 Tab5 的接收窗口里，不丢，追回来之后在途的自己落回去。
/// 为什么不干脆平时 3 帧在途：画面源比管线快的时候（重的帧）在途的会一直顶着上限，多一帧在途 = 每帧多晚一个帧周期，帧率却不涨。
/// 那种时候发送端从来不用等画面源，过了 SpareHoldMs 就回到 SteadyInFlight 帧在途。
/// 为什么要看「等完的时候在途的不到 SteadyInFlight 帧」：靠多放的这一帧才腾出手来等画面源的，只说明还撑得住，不说明追回来了。
/// 为什么要连着几帧：画面源自己不匀的时候（隔几帧少出一幅），管线比它慢也会偶尔等到它一次。实测全屏在动、每帧 70 KB（Tab5 每秒只跟得上 42 帧）、
/// 只看一次的话 35%~60% 的帧是 3 帧在途发的（画面源匀的时候少些，每秒少出 10 幅的时候多些），往返中位 42 → 45~51 ms，帧率不涨；连着 3 帧跟上的一分钟只有几段到三四十段，
/// 而管线跟得上的画面一分钟有两百段上下。
/// 平时 2 帧在途要求接收端的 TCP 在接收窗口被顶满时不丢数据（PROTOCOL.md「流量控制」里「对接收端的要求」；
/// Tab5 固件靠 lwIP 的几项配置满足，见 firmware/sdkconfig.defaults）。
/// 为什么迟到了要接着发：Tab5 发回的 ACK 在空中丢了的时候，它的 TCP 要收到 3 个重复确认才立刻重传，否则等它的重传超时
/// （lwIP 默认配置下 1 秒以上；TCP 定时器间隔配成 50 ms 的 Tab5 固件是 0.23~0.5 秒）。
/// 重复确认是我们这边的 TCP 每收到 Tab5 后面发来的一个段回一个，所以丢的那个 ACK 之后 Tab5 还得再确认 3 帧——在途的要到 4 帧。
/// 门限从最早在途的那一帧算起，所以一迟到就连着放到 MaxInFlight 帧，不是放一帧再等一个门限。
/// 这一招实测救回四次里的三次（0.1~0.3 秒补上），剩下的靠 Tab5 的重传超时，见 PROTOCOL.md「流量控制」。
/// 本类只管算，不碰时钟和网络：时刻由调用方传进来（毫秒，单调时钟），所以能脱开网络单独测。不是线程安全的，调用方加锁。
/// </summary>
public sealed class FlowControl(int steadyInFlight = FlowControl.DefaultSteadyInFlight, int maxInFlight = FlowControl.DefaultMaxInFlight,
                                double minAckWaitMs = FlowControl.DefaultMinAckWaitMs, double spareHoldMs = FlowControl.DefaultSpareHoldMs)
{
    /// <summary>默认值只写在这里：SessionOptions 和命令行都取这几个。</summary>
    public const int DefaultSteadyInFlight = 2, DefaultMaxInFlight = 4;
    public const double DefaultMinAckWaitMs = 60;
    /// <summary>错过一次屏幕刷新要十几帧（0.2 秒上下）追回来，余量小的时候更久；取 0.5 秒。</summary>
    public const double DefaultSpareHoldMs = 500;
    public const int RttWindow = 16, RttMinSamples = 4;
    /// <summary>连着这么多帧跟上画面源才算跟上了（见类的说明）。</summary>
    public const int KeepUpFrames = 3;

    private readonly Queue<(uint Seq, double SentMs)> _pending = new();
    private readonly double[] _rtt = new double[RttWindow];
    private int _rttCount, _rttNext;
    private double _keptUpMs = double.NegativeInfinity;     // 上一次跟上画面源是什么时刻
    private bool _waited;           // 上一帧发出之后等过画面源，等完时在途的不到 SteadyInFlight 帧
    private int _waitedRun;         // 到上一帧为止，连着多少帧是这样等来的

    public int SteadyInFlight { get; private set; } = steadyInFlight >= 1 ? steadyInFlight : throw new ArgumentOutOfRangeException(nameof(steadyInFlight));
    public int MaxInFlight { get; } = maxInFlight >= steadyInFlight ? maxInFlight : throw new ArgumentOutOfRangeException(nameof(maxInFlight));
    public double MinAckWaitMs { get; } = minAckWaitMs > 0 ? minAckWaitMs : throw new ArgumentOutOfRangeException(nameof(minAckWaitMs));

    /// <summary>在途的帧数。</summary>
    public int InFlight => _pending.Count;

    /// <summary>在途的帧里最早的那一帧是什么时刻发出的（毫秒）；没有在途的帧时是 NaN。</summary>
    public double OldestSentMs => _pending.Count > 0 ? _pending.Peek().SentMs : double.NaN;

    private readonly double _spareHoldMs = spareHoldMs >= 0 && double.IsFinite(spareHoldMs) ? spareHoldMs : throw new ArgumentOutOfRangeException(nameof(spareHoldMs));

    /// <summary>跟上过画面源之后多久之内可以多放一帧（见类的说明）；0 = 不多放。MaxInFlight 不比 SteadyInFlight 多时没有位置可放，也是 0。</summary>
    public double SpareHoldMs => MaxInFlight > SteadyInFlight ? _spareHoldMs : 0;

    /// <summary>
    /// 连接中途换「平时几帧在途」（画质换档时，见 PictureTiers：限帧率的档按一拍多长定它）；要在 1 和 MaxInFlight 之间。
    /// 在途的帧不动：改少了就是等它们确认到新的数以下再发，改多了下一帧起就可以多发。
    /// </summary>
    public void SetSteadyInFlight(int steadyInFlight)
    {
        if (steadyInFlight < 1 || steadyInFlight > MaxInFlight) throw new ArgumentOutOfRangeException(nameof(steadyInFlight));
        SteadyInFlight = steadyInFlight;
    }

    /// <summary>因为确认迟到、在平时的在途帧数之外多发的帧数。</summary>
    public int LateSends { get; private set; }

    /// <summary>跟得上画面源、在平时的在途帧数之外多发的帧数。</summary>
    public int SpareSends { get; private set; }

    /// <summary>
    /// 最近发出的那一帧是不是「跟上了」：它是等来的（调用方在它之前报过 <see cref="OnSourceIdle"/>：等画面源出新画面，或者限帧率时等下一拍），
    /// 等完的时候在途的不到 SteadyInFlight 帧。不是 = 可以发的时候画面早就等着了、或者拍子已经过了（管线比要的帧率慢），
    /// 或者是靠多放的一帧 / 确认迟到才发出去的。<see cref="BudgetControl"/> 按它调预算。
    /// </summary>
    public bool LastSentKeptUp { get; private set; }

    /// <summary>刚发出一帧。</summary>
    public void OnSent(uint seq, double nowMs)
    {
        if (_pending.Count >= SteadyInFlight)
        {
            // 在途正好 SteadyInFlight 帧、最早一帧的确认又没迟到：只能是靠多放的那一帧放行的。不再看时限——放行之后调用方还要等画面源、编码，
            // 发出来的时候时限可能刚过
            if (_pending.Count == SteadyInFlight && SpareHoldMs > 0 && nowMs < LateAtMs()) SpareSends++;
            else LateSends++;
        }
        _pending.Enqueue((seq, nowMs));
        _waitedRun = _waited ? _waitedRun + 1 : 0;
        LastSentKeptUp = _waited;
        _waited = false;
    }

    /// <summary>
    /// 调用方可以发下一帧的时候画面源没有新画面，它等了一阵（等到了新画面，或者等满了时限），现在是 nowMs。
    /// 限帧率的时候等下一拍也算：可以发了、拍子却还没到，和画面源还没出新画面是一回事（管线比要的帧率快）。
    /// 这时在途的不到 SteadyInFlight 帧，下一帧就算「等画面源等来的」；连上它已经有 KeepUpFrames 帧都是这样，就是跟上了，记下时刻。
    /// 在途的还有 SteadyInFlight 帧（调用方是靠多放的那一帧、或者确认迟到才腾出手来等的）不算数。
    /// </summary>
    public void OnSourceIdle(double nowMs)
    {
        if (_pending.Count >= SteadyInFlight) return;
        _waited = true;
        if (_waitedRun + 1 >= KeepUpFrames) _keptUpMs = nowMs;
    }

    private bool SpareUsable(double nowMs) => SpareHoldMs > 0 && nowMs - _keptUpMs <= SpareHoldMs;

    /// <summary>收到一个 ACK，返回这一帧的往返时间（毫秒）。ACK 按 PRESENT 的先后回来，对不上最早在途的那一帧就是对端不守协议。</summary>
    public double OnAck(uint seq, double nowMs)
    {
        if (_pending.Count == 0) throw new ProtocolException($"来了第 {seq} 帧的 ACK，但没有在途的帧");
        var (want, sentMs) = _pending.Peek();
        if (seq != want) throw new ProtocolException($"来了第 {seq} 帧的 ACK，在途的最早一帧是第 {want} 帧");
        _pending.Dequeue();
        double rtt = nowMs - sentMs;
        _rtt[_rttNext] = rtt;
        _rttNext = (_rttNext + 1) % RttWindow;
        if (_rttCount < RttWindow) _rttCount++;
        return rtt;
    }

    /// <summary>
    /// 现在离「可以发下一帧」还有多久（毫秒）：0 = 现在就能发；正无穷 = 只能等 ACK 回来（在途的到了上限，或者往返时间还没摸准）。
    /// </summary>
    public double WaitBeforeNextMs(double nowMs)
    {
        if (_pending.Count < SteadyInFlight) return 0;
        if (_pending.Count == SteadyInFlight && SpareUsable(nowMs)) return 0;
        if (_pending.Count >= MaxInFlight) return double.PositiveInfinity;
        return Math.Max(0, LateAtMs() - nowMs);
    }

    /// <summary>最早在途的那一帧的确认到什么时刻算迟到（毫秒）；往返时间还没摸准时是正无穷。要有在途的帧。</summary>
    private double LateAtMs() =>
        _rttCount < RttMinSamples ? double.PositiveInfinity : _pending.Peek().SentMs + Math.Max(MinAckWaitMs, 2 * MedianRtt());

    private double MedianRtt()
    {
        Span<double> s = stackalloc double[_rttCount];
        _rtt.AsSpan(0, _rttCount).CopyTo(s);
        s.Sort();
        return _rttCount % 2 == 1 ? s[_rttCount / 2] : (s[_rttCount / 2 - 1] + s[_rttCount / 2]) / 2;
    }
}
