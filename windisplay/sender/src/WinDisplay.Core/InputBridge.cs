using System.Diagnostics;

namespace WinDisplay.Core;

/// <summary>按键连发的节奏（主机的键盘设置）：按住多久开始连发，之后每隔多久再发一次「按下」。</summary>
public readonly record struct KeyRepeat(double DelayMs, double PeriodMs);

/// <summary>
/// 往系统里注入输入的地方。Windows 上是 SendInput / InjectTouchInput（WinDisplay.Win 的 InputInjector）；测试里是假的。
/// 注入不进去不要抛异常（报出来就行）：<see cref="InputBridge"/> 调它之前已经把自己记的状态改了，抛出来它没法知道系统里到底注入了多少。
/// 真抛了的话 InputBridge 不会因此卡住或者反复重试，但按键的状态可能和系统里的对不上，直到那些键下一次松开。
/// </summary>
public interface IInputSink
{
    /// <summary>按顺序注入一批按键动作。</summary>
    void Keys(IReadOnlyList<KeyAction> actions);

    /// <summary>
    /// 注入一帧触摸，frame 是当前所有触点（系统要求每帧给全）。
    /// 返回 false = 系统没收下这一帧；这时已经注入、还按着的触点都被系统收走了（InjectTouchInput 就是这样规定的）。
    /// </summary>
    bool Touch(IReadOnlyList<Contact> frame);

    /// <summary>按顺序注入一批鼠标动作（触摸板模式，见 <see cref="TouchpadMapper"/>）。</summary>
    void Mouse(IReadOnlyList<MouseAction> actions);
}

/// <summary>
/// Tab5 发来的输入事件 → 往主机里注入的动作，连同要按时间做的两件事（纯计算，时间由调用方给，单位毫秒）：
/// <list type="bullet">
/// <item>按键连发。Tab5 不做长按连发（协议「键盘」），系统对注入的按键也不会自己连发：最后按下的那个键按住 DelayMs 之后，
///   每隔 PeriodMs 再注入一次「按下」，到它松开为止（规则见 <see cref="HidKeyboard.Repeat"/>）。节奏在键按下的时候取一次。</item>
/// <item>触点重报。手指按着不动时 Tab5 不发新的快照，而系统规定按着的触点要接着注入更新帧、不然过一秒左右被它收走（当成抬起了）：
///   离上一帧过了 keepAliveMs 还有触点按着，就把它们在原地重报一次。</item>
/// </list>
/// 这两件事都是照着 Tab5 上一次报的状态接着做的，所以**只在那份状态还新鲜的时候做**：有键 / 触点按着时 Tab5 至少每
/// <see cref="Proto.HeldPeriodMs"/> 发一个输入报文（TOUCH / KEY / HELD，协议「按着的时候」）；超过 <see cref="Proto.HeldStaleMs"/>
/// 一个都没收到，Tab5 多半已经不在了（它突然没了的话不会说一声，连接要过 5~11 秒才断），这时
/// <list type="bullet">
/// <item>不再连发。按着的键**不松开**：注入「松开」本身有效果（单按一下 Win、Alt 会弹出开始菜单、点亮菜单栏），
///   报文要是只是晚到了，松开再按下还会多打一个字；按着不连发的键什么都不会做。之后又收到输入报文就接着连发，键到连接结束时松开。</item>
/// <item>按着的触点作废：注入一帧「取消」（<see cref="ContactPhase.Cancel"/>），它们到抬起之前再报上来的都不算。
///   光是不再重报不行：系统过一秒左右把触点收走时，给程序的是一次普通的抬起，等于在那里点了一下（注入自检实测）。</item>
/// </list>
/// 一帧触摸没注入成、或者显示器的分辨率变了（系统这时也会把所有触点收走）：手还按着的那些到抬起之前都不算，见 <see cref="TouchMapper.Abandon"/>。
/// <para>
/// 触摸有两种注入方式，由人在 Tab5 上选、Tab5 报过来（<see cref="Mode"/>，协议「触摸方式」）：照原样注入成触摸（上面说的），
/// 或者**触摸板模式**——把 Tab5 的屏幕当触摸板，换成鼠标动作（<see cref="TouchpadMapper"/>）。每条连接从「触摸」开始，Tab5 报了才换；
/// 换的那一刻旧方式下按着的先收掉（触点作废 / 左键松开），这时手还按着的那些到全部抬起之前都不算（半路接手的话，新方式会把它当成刚按下的）。
/// 触摸板模式下拖动到一半 Tab5 没了消息：左键和按着的键一样不松开（松开就是把拖着的东西放下），到连接结束时松开。
/// </para>
/// 连接结束时调 <see cref="ReleaseAll"/>：Tab5 不会补发，按着的键要松开，按着的触点作废（手并没有抬起，不能在最后的位置上变成一次点击）。
/// 各个方法哪个线程都能调（里面有锁；注入是在锁里做的，按到来的先后）。
/// </summary>
public sealed class InputBridge
{
    private readonly object _lock = new();
    private readonly IInputSink _sink;
    private readonly Func<ViewFit> _fit;
    private readonly Func<KeyRepeat> _repeat;
    private readonly double _keepAliveMs;
    private readonly TouchpadMapper _pad = new();
    private bool _padMode;                                          // 现在是触摸板模式（这条连接上 Tab5 报的；没报过 = 不是）
    private bool _anyDown;                                          // 上一份快照里有手指按着
    private bool _waitAllUp;                                        // 注入方式刚换：手还按着的那些到全部抬起之前都不算
    private readonly HidKeyboard _keys = new();
    private TouchMapper? _touch;                                    // 还不知道画面在屏幕上占哪一块（没抓到过画面）时是 null
    private ViewFit _fitNow;
    private KeyRepeat _rate;
    private double _repeatAt = double.PositiveInfinity;             // 下一次连发的时刻；没有键要连发 = 正无穷
    private double _touchAt = double.PositiveInfinity;              // 下一次重报触点的时刻；没有触点按着 = 正无穷
    private double _heardAt = double.NegativeInfinity;              // 最近一次收到 Tab5 的输入报文（TOUCH / KEY / HELD）的时刻
    private bool _closed;                                           // 见 Close

    /// <param name="fit">显示器的画面现在在横屏画面里占哪一块（抓屏那边给；分辨率变了它跟着变）。宽或高是 0 = 还不知道，这期间触摸不算。</param>
    /// <param name="repeat">主机现在的按键连发设置。</param>
    /// <param name="keepAliveMs">触点按着不动时隔多久重报一次。</param>
    public InputBridge(IInputSink sink, Func<ViewFit> fit, Func<KeyRepeat> repeat, double keepAliveMs)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _fit = fit ?? throw new ArgumentNullException(nameof(fit));
        _repeat = repeat ?? throw new ArgumentNullException(nameof(repeat));
        if (!(keepAliveMs > 0)) throw new ArgumentOutOfRangeException(nameof(keepAliveMs), keepAliveMs, "重报的间隔要大于 0");
        _keepAliveMs = keepAliveMs;
    }

    /// <summary>Tab5 发来一份键盘报文（8 字节）。</summary>
    public void Key(ReadOnlySpan<byte> report, double nowMs)
    {
        lock (_lock)
        {
            if (_closed) return;
            _heardAt = nowMs;
            KeyAction? before = _keys.Repeat;
            var actions = _keys.Apply(report);
            if (actions.Count > 0) _sink.Keys(actions);
            KeyAction? after = _keys.Repeat;
            if (after is null)
            {
                _repeatAt = double.PositiveInfinity;
            }
            else if (after != before)       // 换了一个键来连发（或者刚开始有键按下）：重新等一个 DelayMs
            {
                KeyRepeat rate = _repeat();
                _rate = new KeyRepeat(Math.Max(rate.DelayMs, 1), Math.Max(rate.PeriodMs, 1));     // 不合理的设置不能变成死循环
                _repeatAt = nowMs + _rate.DelayMs;
            }
        }
    }

    /// <summary>Tab5 发来一份触点快照（空的 = 全部抬起）。</summary>
    public void Touch(ReadOnlySpan<TouchPoint> snapshot, double nowMs)
    {
        lock (_lock)
        {
            if (_closed) return;
            _heardAt = nowMs;
            _anyDown = snapshot.Length > 0;
            if (_waitAllUp)
            {
                _waitAllUp = _anyDown;
                return;
            }
            ViewFit fit = _fit();
            if (fit.W <= 0 || fit.H <= 0 || fit.SrcW <= 0 || fit.SrcH <= 0) return;
            if (_padMode)
            {
                var actions = _pad.Apply(snapshot, fit, nowMs);
                if (actions.Count > 0) _sink.Mouse(actions);
                return;
            }
            if (_touch is null)
            {
                _touch = new TouchMapper(fit);
            }
            else if (fit != _fitNow)
            {
                // 分辨率变了：系统已经把按着的触点收走了，位置也按新的算
                _touch.Abandon(fit);
                _touchAt = double.PositiveInfinity;
            }
            _fitNow = fit;
            var frame = _touch.Apply(snapshot);
            if (frame.Count > 0) Inject(frame, nowMs);
        }
    }

    /// <summary>
    /// Tab5 报它那边选的触摸方式（true = 触摸板模式）。和现在的不一样才有事做：旧方式下按着的先收掉（触点作废 / 左键松开），
    /// 手还按着的那些到全部抬起之前都不算。
    /// </summary>
    public void Mode(bool touchpad)
    {
        lock (_lock)
        {
            if (_closed || touchpad == _padMode) return;
            _padMode = touchpad;                // 先换：下面收尾的时候出了错，之后的触摸也按新的算
            _waitAllUp = _anyDown;              // 上一份快照里还有手指按着：之后来的是同一次接触的后半截
            DropTouch();
        }
    }

    /// <summary>
    /// Tab5 报触摸板模式下光标的加速（手指滑得快时最多放大到几倍，不小于 1）。从下一份快照起按新的算，按着的不用收。
    /// </summary>
    public void TouchpadGain(double maxGain)
    {
        lock (_lock)
        {
            if (!_closed) _pad.MaxGain = maxGain;
        }
    }

    /// <summary>Tab5 发来一个 HELD：它那边按着的键 / 触点还按着。</summary>
    public void Held(double nowMs)
    {
        lock (_lock) _heardAt = nowMs;
    }

    /// <summary>
    /// 把到点的事做掉（连发、重报），返回离下一件事还有多少毫秒（正无穷 = 现在没有要按时间做的事）。
    /// 调用方隔这么久再调；这期间来了新的按键 / 触摸要提前再调一次（下一件事的时间可能变早了），见 <see cref="InputPump"/>。
    /// </summary>
    public double Tick(double nowMs)
    {
        lock (_lock)
        {
            bool fresh = nowMs - _heardAt <= Proto.HeldStaleMs;     // Tab5 报的那份状态还新不新鲜，见类的说明
            try
            {
                if (nowMs >= _repeatAt)
                {
                    if (_keys.Repeat is { } key)
                    {
                        // 先排好下一次、再注入：注入的地方抛了异常，也不会每次醒来都再试一遍。
                        // 不新鲜的时候节拍照走、只是不注入：之后又收到输入报文，下一拍就接着连发
                        _repeatAt += _rate.PeriodMs;
                        if (_repeatAt <= nowMs) _repeatAt = nowMs + _rate.PeriodMs;     // 来晚了：欠下的不补，不然会连着打出一串
                        if (fresh) _sink.Keys([key]);
                    }
                    else
                    {
                        _repeatAt = double.PositiveInfinity;
                    }
                }
            }
            finally     // 连发那一下出了错，触点也照样要重报（不然系统把它收走了）
            {
                if (nowMs >= _touchAt)
                {
                    var held = _touch?.Held ?? [];
                    if (held.Count > 0 && fresh)
                    {
                        Inject(held, nowMs);
                    }
                    else
                    {
                        _touchAt = double.PositiveInfinity;
                        var cancel = _touch?.Cancel() ?? [];
                        if (cancel.Count > 0) _sink.Touch(cancel);      // 没收下也不用管：那是系统已经把它们收走了
                    }
                }
            }
            return Math.Min(_repeatAt, _touchAt) - nowMs;
        }
    }

    /// <summary>
    /// 连接结束时调：按着的键都松开，按着的触点都作废（没有按着的就什么都不做）；触摸方式回到「触摸」、触摸板的加速回到默认——
    /// 那是上一条连接上 Tab5 报的，下一条连接要它重新报（老固件不报，就一直是触摸）。
    /// </summary>
    public void ReleaseAll()
    {
        lock (_lock)
        {
            try
            {
                var keys = _keys.ReleaseAll();
                if (keys.Count > 0) _sink.Keys(keys);
            }
            finally     // 松键的时候出了错，触点和左键也照样要收掉
            {
                _repeatAt = double.PositiveInfinity;
                _anyDown = _waitAllUp = _padMode = false;       // 下一条连接里的手指、触摸方式和这一条的没有关系
                _pad.MaxGain = TouchpadMapper.DefaultMaxGain;
                DropTouch();
            }
        }
    }

    // 在锁里调：按着的触点作废，触摸板模式下按着的左键松开（两种方式各自记着的都收掉，没有按着的就什么都不做）
    private void DropTouch()
    {
        try
        {
            _touchAt = double.PositiveInfinity;
            var cancel = _touch?.ReleaseAll() ?? [];
            if (cancel.Count > 0) _sink.Touch(cancel);
        }
        finally
        {
            var up = _pad.ReleaseAll();
            if (up.Count > 0) _sink.Mouse(up);
        }
    }

    /// <summary>
    /// 不再接收输入：按着的键都松开、触点都作废，之后再来的按键 / 触摸都不算。进程要结束的时候调（哪个线程都行，调几次都行）：
    /// 这之后再注入进去的键没有人负责松开，会一直按在系统里。
    /// </summary>
    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
            ReleaseAll();
        }
    }

    // 在锁里调
    private void Inject(IReadOnlyList<Contact> frame, double nowMs)
    {
        bool taken = false;
        try
        {
            taken = _sink.Touch(frame);
        }
        finally
        {
            if (!taken) _touch!.Abandon();      // 没收下（抛了异常也算）：系统那边按着的触点都没了
            _touchAt = _touch!.Held.Count > 0 ? nowMs + _keepAliveMs : double.PositiveInfinity;
        }
    }
}

/// <summary>
/// 给 <see cref="InputBridge"/> 配上时钟和一个后台线程：按键 / 触摸来了马上注入，连发和重报到点由后台线程做。
/// 另外隔一阵看一次主机的键盘指示灯（Num / Caps / Scroll Lock），变了就报出来（会话拿去发给 Tab5 上的键盘）。
/// 建好之后先把 <see cref="LedsChanged"/>、<see cref="Faulted"/> 挂上，再 <see cref="Start"/>；用完 Dispose（先把按着的都松开，再停线程）。
/// Dispose 要等不会再有输入来了才调（会话的 Run 已经返回）；进程要结束、而会话可能还在跑的时候调 <see cref="Close"/>。
/// </summary>
public sealed class InputPump : IDisposable
{
    /// <summary>隔多久看一次键盘指示灯。</summary>
    public static readonly TimeSpan LedPoll = TimeSpan.FromMilliseconds(100);

    private readonly InputBridge _bridge;
    private readonly Func<byte>? _leds;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _stop;
    private int _started;
    private volatile int _lastLeds = -1;

    /// <param name="leds">读主机现在的键盘指示灯状态（bit0 Num、bit1 Caps、bit2 Scroll）；不给 = 不管指示灯。在后台线程上调。</param>
    public InputPump(InputBridge bridge, Func<byte>? leds = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _leds = leds;
        _thread = new Thread(Loop) { IsBackground = true, Name = "输入注入" };
    }

    /// <summary>主机的键盘指示灯状态：开始时报一次，之后变了才报。在后台线程上回调。</summary>
    public event Action<byte>? LedsChanged;

    /// <summary>最近一次读到的指示灯状态（还没读过、或者不管指示灯时是 0）。后来才接上来的一方先拿它，再等 <see cref="LedsChanged"/>。</summary>
    public byte Leds => (byte)Math.Max(_lastLeds, 0);

    /// <summary>后台线程里出的错（注入的接口、读指示灯、LedsChanged 的处理函数抛的）。出了错线程照常接着跑。</summary>
    public event Action<Exception>? Faulted;

    /// <summary>
    /// 起后台线程，只能调一次。线程一起来就读一次指示灯、报出来，所以要先把 <see cref="LedsChanged"/>、<see cref="Faulted"/> 挂好再调：
    /// 挂晚了，开头那一次（之后灯不变就是唯一的一次）就没人收到。
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("这个 InputPump 已经 Start 过了");
        _thread.Start();
    }

    // 没 Start 就来了输入：连发和重报没有线程做，按住的键不会连发、按着的触点过一阵被系统收走——当场报出来，别等人发现
    private double Now()
    {
        if (Volatile.Read(ref _started) == 0) throw new InvalidOperationException("这个 InputPump 还没有 Start");
        return _clock.Elapsed.TotalMilliseconds;
    }

    public void Key(ReadOnlySpan<byte> report)
    {
        _bridge.Key(report, Now());
        _wake.Set();
    }

    public void Touch(ReadOnlySpan<TouchPoint> snapshot)
    {
        _bridge.Touch(snapshot, Now());
        _wake.Set();
    }

    /// <summary>Tab5 发来一个 HELD（它那边按着的还按着），见 <see cref="InputBridge.Held"/>。</summary>
    public void Held() => _bridge.Held(Now());

    /// <summary>Tab5 报它那边选的触摸方式（true = 触摸板模式），见 <see cref="InputBridge.Mode"/>。</summary>
    public void Mode(bool touchpad) => _bridge.Mode(touchpad);

    /// <summary>Tab5 报触摸板模式下光标的加速（最多放大到几倍），见 <see cref="InputBridge.TouchpadGain"/>。</summary>
    public void TouchpadGain(double maxGain) => _bridge.TouchpadGain(maxGain);

    /// <summary>连接结束时调：按着的键都松开，按着的触点都作废。</summary>
    public void ReleaseAll() => _bridge.ReleaseAll();

    /// <summary>不再接收输入：按着的都松开，之后再来的按键 / 触摸都不算，见 <see cref="InputBridge.Close"/>。哪个线程都能调。</summary>
    public void Close() => _bridge.Close();

    private void Loop()
    {
        while (!_stop)
        {
            double wait = LedPoll.TotalMilliseconds;
            try
            {
                wait = Math.Min(wait, _bridge.Tick(_clock.Elapsed.TotalMilliseconds));
                if (_leds is not null)
                {
                    int now = _leds() & 0x07;
                    if (now != _lastLeds)
                    {
                        _lastLeds = now;
                        LedsChanged?.Invoke((byte)now);
                    }
                }
            }
            catch (Exception e)
            {
                try
                {
                    Faulted?.Invoke(e);
                }
                catch
                {
                    // 报错的处理函数自己又出错：没有地方可报了
                }
            }
            _wake.WaitOne(TimeSpan.FromMilliseconds(Math.Max(wait, 1)));
        }
    }

    public void Dispose()
    {
        if (_stop) return;
        try
        {
            _bridge.Close();
        }
        finally
        {
            _stop = true;
            if (Volatile.Read(ref _started) != 0)
            {
                _wake.Set();
                _thread.Join();
            }
            _wake.Dispose();
        }
    }
}
