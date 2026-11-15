namespace WinDisplay.Core;

/// <summary>常驻的发送端现在在做什么（托盘图标和设置窗口显示的就是它）。</summary>
public enum HostState
{
    Stopped,            // 没在跑
    Searching,          // 在找 Tab5：没有 Tab5 应答（Tab5 上没开「无线显示」页，或者不在一个局域网里）
    NeedPairing,        // 有 Tab5 应答，但都没配对过，而现在不能配对（没设密码，或者密码连着不对、已经停止配对）
    Connecting,         // 正在连、握手
    AwaitingPassword,   // 等人在 Tab5 上输入配对密码
    Showing,            // 连上了，Tab5 上显示着这台电脑的画面
    Faulted,            // 这台电脑这边出了错（画面抓不了、配对存不进文件……），过一会儿自己再试；原因在 Detail 里
}

/// <param name="Tab5">Showing 时：连着的那台 Tab5 的名字。</param>
/// <param name="Detail">Faulted 时：出了什么错。</param>
public sealed record HostStatus(HostState State, string? Tab5 = null, string? Detail = null);

/// <summary>
/// 常驻的发送端（托盘程序的内核，和平台无关）：一直自动连接（<see cref="AutoConnector"/>）——问到 Tab5 就打开画面来源、连上去显示，
/// 连接结束就关掉画面来源、接着问；Tab5 发来的触摸和按键交给 <see cref="InputPump"/> 注入，主机的键盘指示灯状态发回去。
/// 另外管三件人在电脑上做的事：设 / 改配对密码、密码连着不对停止配对之后恢复、移除一台 Tab5 的配对。
/// <para>
/// 画面来源每条连接开一次、连接结束就关：没有连接的时候不抓屏。这台电脑这边出了错（画面来源打不开、配对存不进文件、别的异常）
/// 不让程序停掉：状态变成 Faulted，隔 <see cref="FaultRetry"/> 再试。
/// </para>
/// <see cref="Changed"/> 在后台线程上回调（状态、配对过的设备、能不能配对，哪一样变了都报）；界面要用 BeginInvoke 转到自己的线程，
/// **不要同步等**（Invoke）：界面线程这时可能正在等这边的某个操作返回。各个方法哪个线程都能调。
/// </summary>
public sealed class SenderHost : IDisposable
{
    /// <summary>这台电脑这边出了错之后，隔多久再试。</summary>
    public static readonly TimeSpan FaultRetry = TimeSpan.FromSeconds(5);

    private readonly PairStore _store;
    private readonly PairingGate _gate;
    private readonly HostSettings? _settings;
    private readonly LinkAuth _auth;
    private readonly Func<IFrameSource> _openSource;
    private readonly InputPump? _input;
    private readonly IAudioSource? _audio;
    private readonly SessionOptions _options;
    private readonly AutoConnector _auto;
    private readonly Action<TimeSpan, CancellationToken> _wait;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private Thread? _thread;
    private HostStatus _status = new(HostState.Stopped);
    private byte[]? _currentId;         // 正连着（或者正在连）的那台 Tab5 的编号（_lock 保护）
    // 各台 Tab5 上次报的画质，按编号记（_lock 保护；只在内存里，程序重开就没了）：第几档 + 屏幕的帧周期，
    // 老固件报的是质量、动态预算 KB（Tier 是 null）。下一条连接拿它当初值：
    // 不然每条连接的头几帧按默认的质量压、不限帧率，Tab5 报过来之后才改，质量比默认高的话紧接着还要把整屏补一遍
    private readonly Dictionary<string, (PictureTier? Tier, int PeriodUs, int Quality, int MotionKb)> _pictures = [];
    private string? _reportedFault;     // 上一次经 Faulted 报出去的原因（见那个事件）。只有跑自动连接的线程用
    private int _disposed;

    /// <param name="settings">配对密码存在哪；null = 不存盘（测试、或者密码每次启动另给）。gate 应该就是它的 CreateGate() 建的。</param>
    /// <param name="name">报给 Tab5 的名字（它的配对列表里显示），一般是机器名。</param>
    /// <param name="openSource">打开画面来源。每条连接开一次；连接结束后，它要是 IDisposable 就 Dispose。
    /// 打不开就抛异常（一般是 <see cref="CaptureLostException"/>）：算这台电脑这边出了错，状态变成 Faulted，隔 <see cref="FaultRetry"/> 再试。</param>
    /// <param name="input">往这台电脑里注入输入的那一套；null = 不注入。它归调用方所有：调用方先 Start 它，这里不 Dispose。</param>
    /// <param name="wait">等一段时间（见 <see cref="AutoConnector"/>）；不给 = 真的等。测试里换成不等的。</param>
    /// <param name="audio">声音来源（这台电脑正在放的声音）；null = 不发声音。归调用方所有；每条连接握手走完后抓、连接结束时停（见 <see cref="DisplaySession"/>）。</param>
    public SenderHost(PairStore store, PairingGate gate, HostSettings? settings, string name, IDiscovery discovery, Func<IFrameSource> openSource,
                      InputPump? input = null, SessionOptions? options = null, Action<TimeSpan, CancellationToken>? wait = null, IAudioSource? audio = null)
    {
        _audio = audio;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _settings = settings;
        _auth = new LinkAuth(store, name ?? throw new ArgumentNullException(nameof(name)), gate);
        _openSource = openSource ?? throw new ArgumentNullException(nameof(openSource));
        _input = input;
        _options = (options ?? new SessionOptions()).Validated();
        _auto = new AutoConnector(discovery, store, gate, Connect, wait);
        _auto.Changed += OnAuto;
        _wait = wait ?? ((time, ct) => ct.WaitHandle.WaitOne(time));
        _gate.WrongCountChanged += _ => Changed?.Invoke();
    }

    /// <summary>有东西变了：<see cref="Status"/>、<see cref="Peers"/>、<see cref="HasPassword"/>、<see cref="PairingStopped"/> 里的哪一样。</summary>
    public event Action? Changed;

    /// <summary>一条连接结束了（给日志用）：连的是哪一台、怎么结束的。在后台线程上回调。</summary>
    public event Action<DiscoveredTab5, SessionResult>? ConnectionEnded;

    /// <summary>
    /// 这台电脑这边出了错（给日志用）：原因，和 <see cref="HostStatus.Detail"/> 里的一样。隔 <see cref="FaultRetry"/> 再试、还是同一个原因的不重复报
    /// （不然毛病一直在的时候日志里几秒一行）；显示出过画面之后再出的，另报。在后台线程上回调。
    /// </summary>
    public event Action<string>? Faulted;

    public HostStatus Status
    {
        get
        {
            lock (_lock) return _status;
        }
    }

    /// <summary>配对过的 Tab5。</summary>
    public IReadOnlyList<PairedPeer> Peers => _store.Peers;

    public bool HasPassword => _gate.HasPassword;

    /// <summary>密码连着不对记满了次数，停止配对了；<see cref="ResumePairing"/> 恢复。配对过的 Tab5 不受影响。</summary>
    public bool PairingStopped => _gate.Stopped;

    /// <summary>开始自动连接。只能调一次。</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_thread is not null) throw new InvalidOperationException("这个 SenderHost 已经开始过了");
            _thread = new Thread(Loop) { IsBackground = true, Name = "无线显示" };
        }
        SetStatus(new HostStatus(HostState.Searching));
        _thread.Start();
    }

    /// <summary>
    /// 设 / 改配对密码（不合规定抛 ArgumentException，见 <see cref="PairingSecret.IsValidPassword"/>）。改密码等于清掉所有配对：
    /// 配对过的 Tab5 全部移除，正连着的断开，之后每台 Tab5 都要在它上面输一次新密码。
    /// 存不进文件抛 <see cref="PairStoreException"/>：这一次不算，密码和密码不对的次数都还是原来的（内存里和文件里一致）。停在哪一步就退到哪一步：
    /// 清配对就没存上的话什么都没动；到存新密码才没存上的话，配对已经清掉了、正连着的也断开了。再设一次就好。
    /// </summary>
    public void SetPassword(string password)
    {
        PairingSecret secret = PairingSecret.FromPassword(password);
        bool cleared = false;
        try
        {
            // 清配对、存新密码都在 PairingGate 的锁里做（见 PairingGate.SetPassword）：没存上时密码和次数一起退回去，
            // 不然界面上说设好了、重启却没了。新密码最后才存盘、连同清零的次数一次写入：
            // 中途断电的话文件里是「旧密码 + 原来的次数 + 没有配对」，旧密码配出来的密钥不会跟着新密码留下来
            _gate.SetPassword(secret, () =>
            {
                _store.ForgetAll();
                cleared = true;
                _settings?.SaveSecret(secret);
            });
        }
        finally
        {
            if (cleared)        // 配对清掉了（新密码存没存上都一样）：正连着的那条用的是刚作废的密钥。没清成 = 什么都没动，不用断
            {
                _auto.DropCurrent();
                _auto.Wake();
                Changed?.Invoke();
            }
        }
    }

    /// <summary>密码连着不对停止配对之后，人在电脑上点了恢复。存不进文件抛 <see cref="PairStoreException"/>：不算恢复，还停着。</summary>
    public void ResumePairing()
    {
        _gate.Resume();
        _auto.Wake();
    }

    /// <summary>移除一台 Tab5 的配对。正连着的就是它的话断开（那条连接用的就是刚作废的密钥）。存不进文件抛 <see cref="PairStoreException"/>。</summary>
    public void Forget(ReadOnlySpan<byte> tab5Id)
    {
        _store.Forget(tab5Id);
        bool current;
        lock (_lock) current = _currentId is not null && tab5Id.SequenceEqual(_currentId);
        if (current) _auto.DropCurrent();
        Changed?.Invoke();
    }

    private void Loop()
    {
        CancellationToken ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            string fault;
            try
            {
                _auto.Run(ct);
                if (ct.IsCancellationRequested) break;
                fault = "画面来源结束了";       // 抓屏不会走到这里；别的来源给完了画面也不能让循环空转
            }
            catch (Exception e)
            {
                // 这台电脑这边的错（见 DisplaySession.Run、AutoConnector.Run）：画面来源打不开、配对存不进文件、画面压不进 Tab5 的上限、某个回调里抛的……
                // 常驻的程序不能因为它停掉：报出来，过一会儿再试
                fault = e.Message;
            }
            SetStatus(new HostStatus(HostState.Faulted, Detail: fault));
            if (fault != _reportedFault)
            {
                _reportedFault = fault;
                Faulted?.Invoke(fault);
            }
            _wait(FaultRetry, ct);
            if (!ct.IsCancellationRequested) SetStatus(new HostStatus(HostState.Searching));
        }
        SetStatus(new HostStatus(HostState.Stopped));
    }

    // 在跑自动连接的线程上：连这一台，跑到这条连接结束
    private SessionResult Connect(DiscoveredTab5 tab5, CancellationToken ct)
    {
        // 打不开就让异常出去（Loop 里接：Faulted，隔 FaultRetry 再试）。不能当成「这台 Tab5 没连上」报给自动连接：
        // 那样它会把这台 Tab5 往后排、越等越久（最长 30 秒），而毛病在这台电脑上
        IFrameSource source = _openSource();
        InputPump? input = _input;
        Action<byte>? leds = null;
        try
        {
            string id = Convert.ToHexString(tab5.Id);
            SessionOptions options = _options;
            lock (_lock)
                if (options.FollowDevicePicture && _pictures.TryGetValue(id, out var last))
                    options = last.Tier is null ? options with { Tier = null, Quality = last.Quality, MotionFrameBytes = last.MotionKb * 1024 }
                                                : options with { Tier = last.Tier, RefreshPeriodUs = last.PeriodUs };
            var session = new DisplaySession(source, options, _auth, _audio);
            session.Picture += (quality, motionKb) => { lock (_lock) _pictures[id] = (null, 0, quality, motionKb); };
            session.PictureMode += (tier, periodUs) => { lock (_lock) _pictures[id] = (tier, periodUs, 0, 0); };
            session.Pairing += step => SetStatus(new HostStatus(step == PairingStep.PasswordAccepted ? HostState.Connecting : HostState.AwaitingPassword));
            session.Connected += hello =>
            {
                _reportedFault = null;
                SetStatus(new HostStatus(HostState.Showing, hello.Name));
            };
            if (input is not null)
            {
                session.Touch += points => input.Touch(points);
                session.Key += report => input.Key(report);
                session.Held += input.Held;
                session.InputMode += input.Mode;
                session.TouchpadGain += input.TouchpadGain;
                leds = session.SetKeyboardLeds;
                input.LedsChanged += leds;                      // 先登记、再取现在的：两步之间变了的也不会漏
                session.SetKeyboardLeds(input.Leds);
            }
            // 记的是应答里的编号；握手查配对用的是 HELLO 里的。session.Run(tab5, …) 保证两个是同一个，Forget 才认得出正连着的是不是它
            lock (_lock) _currentId = tab5.Id;
            return session.Run(tab5, ct);
        }
        finally
        {
            lock (_lock) _currentId = null;
            if (input is not null)
            {
                input.LedsChanged -= leds;
                input.ReleaseAll();         // 连接结束时 Tab5 不会补发：按着的键松开，按着的触点作废
            }
            (source as IDisposable)?.Dispose();
        }
    }

    private void OnAuto(AutoEvent e)
    {
        switch (e)
        {
            case AutoEvent.NothingToConnect nothing:
                SetStatus(new HostStatus(nothing.Found == 0 ? HostState.Searching : HostState.NeedPairing));
                break;
            case AutoEvent.Connecting:
                SetStatus(new HostStatus(HostState.Connecting));
                break;
            case AutoEvent.Ended ended:
                ConnectionEnded?.Invoke(ended.Target, ended.Result);
                SetStatus(new HostStatus(HostState.Searching));
                // 这条连接上可能刚配对成了（多了一台）、也可能配对被移除了：状态没变也报一次
                Changed?.Invoke();
                break;
        }
    }

    private void SetStatus(HostStatus status)
    {
        lock (_lock)
        {
            if (_status == status) return;
            _status = status;
        }
        Changed?.Invoke();
    }

    /// <summary>停下来：正连着的那条正常收尾（给 Tab5 发 BYE），按着的键松开、按着的触点作废。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        Thread? thread;
        lock (_lock) thread = _thread;
        if (thread is not null && thread != Thread.CurrentThread) thread.Join();
        _stop.Dispose();
    }
}
