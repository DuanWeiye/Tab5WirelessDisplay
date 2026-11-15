namespace WinDisplay.Core;

/// <summary>自动连接走到哪一步了（托盘程序靠它显示状态）。</summary>
public abstract record AutoEvent
{
    private AutoEvent()
    {
    }

    /// <summary>问了一轮，没有可以连的。Found = 应答了几台（不是 0 = 应答的都没配对过，而现在不能配对）。</summary>
    public sealed record NothingToConnect(int Found) : AutoEvent;

    /// <summary>要去连这一台了。Paired = 电脑这边有它的配对密钥。</summary>
    public sealed record Connecting(DiscoveredTab5 Target, bool Paired) : AutoEvent;

    /// <summary>那条连接结束了。RetryIn = 下一轮询问之前要等多久（0 = 马上再问）。</summary>
    public sealed record Ended(DiscoveredTab5 Target, SessionResult Result, TimeSpan RetryIn) : AutoEvent;
}

/// <summary>
/// 自动连接（协议「自动发现」「结束之后能不能重连」）：没有连接时一轮一轮地广播询问，有 Tab5 应答就挑一台去连，
/// 连接结束了回到询问。它只管「什么时候连、连哪一台」；一条连接怎么跑是调用方给的 connect（一般就是 DisplaySession.Run）。
/// <list type="bullet">
/// <item>配对过的优先；应答的里面一台配对过的都没有、现在又可以配对（设了密码、没停止配对），才连没配对过的（它那边会弹出密码框）。</item>
/// <item>上一次在某个地址上一帧都没显示出来就结束了（认证没过、连不上、接了就断、乱回……；这边的人要它断的不算），
///   下一轮把它排到同一档的后面：同一个编号从几个地址应答时（其中可能有假的、坏的）一个一个试，
///   先应答的那个不行也轮得到后面的。在那个地址上显示出画面了就不再排后面。</item>
/// <item>连接结束后从不照原来的地址直接重连，都是重新询问、有应答才连：Tab5 的地址可能换了；
///   是 Tab5 上的人退出的话它不再应答，自然就不连了，等人在 Tab5 上重新打开「无线显示」页。</item>
/// <item>一条连接一帧都没显示出来就结束了（认证没过、Tab5 嫌发的东西不合规定、密码框到时间了、连不上……）：
///   下一轮之前先等，第一次 1 秒，连着这样的话每次加倍，最长 30 秒；显示出一帧就清零。</item>
/// </list>
/// Run 在调用它的线程上一直跑；DropCurrent、Wake 哪个线程都能调。
/// </summary>
public sealed class AutoConnector
{
    /// <summary>没有可以连的 Tab5 时，两轮询问之间隔多久。</summary>
    public static readonly TimeSpan QueryInterval = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(1), MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly IDiscovery _discovery;
    private readonly PairStore _store;
    private readonly PairingGate? _gate;
    private readonly Func<DiscoveredTab5, CancellationToken, SessionResult> _connect;
    private readonly Action<TimeSpan, CancellationToken> _wait;
    private readonly HashSet<DiscoveredTab5> _demoted = [];         // 上一次在这个地址上一帧都没显示出来就结束了。只有跑 Run 的线程用
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _lock = new();
    private CancellationTokenSource? _current;                      // 正连着的那一条的取消（_lock 保护）
    private TimeSpan _backoff;
    private int _running;

    /// <param name="gate">配对密码和密码不对的次数；null = 这个程序不配对，只连配对过的。</param>
    /// <param name="connect">连这一台、跑到那条连接结束，返回它是怎么结束的。给它的取消：整个自动连接被取消，或者有人调了 DropCurrent。</param>
    /// <param name="wait">等一段时间（被取消就提前返回，不抛异常）；不给 = 真的等，Wake 能叫醒。测试里换成不等的。</param>
    public AutoConnector(IDiscovery discovery, PairStore store, PairingGate? gate,
                         Func<DiscoveredTab5, CancellationToken, SessionResult> connect, Action<TimeSpan, CancellationToken>? wait = null)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gate = gate;
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _wait = wait ?? WaitOrWake;
    }

    /// <summary>状态变了。在跑 Run 的线程上回调；回调里不要做慢事，抛了异常 Run 就到此为止（见 Run）。</summary>
    public event Action<AutoEvent>? Changed;

    /// <summary>
    /// found（一轮询问里应答了的）里可以去连的，按先后。demoted = 上一次没显示出画面就结束了的地址，排到同一档的后面。
    /// </summary>
    public static IReadOnlyList<DiscoveredTab5> Pick(IReadOnlyList<DiscoveredTab5> found, PairStore store, bool canPair,
                                                    IReadOnlySet<DiscoveredTab5>? demoted = null)
    {
        var peers = store.Peers;
        var paired = found.Where(f => IsPaired(peers, f)).ToList();
        IReadOnlyList<DiscoveredTab5> pool = paired.Count > 0 ? paired : canPair ? found : [];
        if (demoted is null || demoted.Count == 0) return [.. pool];
        return [.. pool.Where(f => !demoted.Contains(f)), .. pool.Where(demoted.Contains)];
    }

    private static bool IsPaired(IReadOnlyList<PairedPeer> peers, DiscoveredTab5 tab5) =>
        peers.Any(p => p.Id.AsSpan().SequenceEqual(tab5.Id));

    /// <summary>
    /// 一直自动连接，直到 ct 被取消，或者画面来源结束（那条连接以 Completed 结束）。
    /// connect 抛的异常（本端的代码出了错，见 DisplaySession.Run）、Changed 的处理函数抛的异常都原样抛出去，Run 到此为止
    /// （要接着自动连接得再调一次 Run）。同一时刻只能有一个 Run。
    /// </summary>
    public void Run(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) throw new InvalidOperationException("这个 AutoConnector 已经在跑了");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var found = _discovery.Query(ct);
                if (ct.IsCancellationRequested) return;
                var targets = Pick(found, _store, _gate?.CanPair == true, _demoted);
                if (targets.Count == 0)
                {
                    Changed?.Invoke(new AutoEvent.NothingToConnect(found.Count));
                    _wait(QueryInterval, ct);
                    continue;
                }

                // 一轮只连一台，结束了重新询问：这期间别的 Tab5 可能已经不在等连接了
                var target = targets[0];
                Changed?.Invoke(new AutoEvent.Connecting(target, IsPaired(_store.Peers, target)));
                SessionResult result = ConnectOne(target, ct);
                if (result.End == SessionEnd.Completed || ct.IsCancellationRequested) return;

                // 没显示出画面就结束了：下一轮先试别的地址。这边的人要它断的（Cancelled）说明不了这个地址行不行，原来怎样还怎样
                if (result.FramesAcked > 0) _demoted.Remove(target);
                else if (result.End != SessionEnd.Cancelled) _demoted.Add(target);
                // 显示出过画面，或者是这边的人要它断的（DropCurrent：改了密码、移除了配对）：马上再问；不然先等，连着这样就越等越久
                if (result.FramesAcked > 0 || result.End == SessionEnd.Cancelled) _backoff = TimeSpan.Zero;
                else _backoff = _backoff == TimeSpan.Zero ? FirstBackoff : _backoff + _backoff < MaxBackoff ? _backoff + _backoff : MaxBackoff;
                Changed?.Invoke(new AutoEvent.Ended(target, result, _backoff));
                if (_backoff > TimeSpan.Zero) _wait(_backoff, ct);
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private SessionResult ConnectOne(DiscoveredTab5 target, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_lock) _current = cts;
        try
        {
            return _connect(target, cts.Token);
        }
        finally
        {
            lock (_lock) _current = null;
        }
    }

    /// <summary>
    /// 断开正连着的那一条（没有连着就什么都不做），自动连接照常继续、马上重新询问。
    /// 改了配对密码、移除了一台 Tab5 的配对之后调：正连着的那一条用的可能就是刚作废的密钥。
    /// </summary>
    public void DropCurrent()
    {
        lock (_lock) _current?.Cancel();
    }

    /// <summary>
    /// 下一次等待不等（两轮询问之间的、连接没显示出画面之后的都算）；正等着就马上醒。设了密码、恢复了配对之后调：现在可能有能连的了。
    /// 不在等的时候调（正连着、正在问、Run 还没开始）也记着，到下一次等待时用掉；记着的最多一次，连着调几次和调一次一样。
    /// </summary>
    public void Wake()
    {
        lock (_lock)
        {
            if (_wake.CurrentCount == 0) _wake.Release();
        }
    }

    private void WaitOrWake(TimeSpan time, CancellationToken ct)
    {
        try
        {
            _wake.Wait(time, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
