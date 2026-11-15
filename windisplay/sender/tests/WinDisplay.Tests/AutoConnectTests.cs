using System.Diagnostics;
using System.Net;
using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>
/// 自动连接的规矩（协议「自动发现」「结束之后能不能重连」）：连哪一台、连接结束之后等多久再问、哪些情况不连。
/// 询问和连接都是假的（按剧本应答 / 结束），等待也不真等，只记下等了多久。
/// </summary>
public class AutoConnectTests
{
    private static readonly byte[] IdA = TestPairing.Tab5Id;
    private static readonly byte[] IdB = [.. Enumerable.Range(0xC0, Proto.IdSize).Select(i => (byte)i)];
    private static readonly DiscoveredTab5 A = new(IPAddress.Parse("192.168.1.50"), 7250, IdA);
    private static readonly DiscoveredTab5 AElsewhere = new(IPAddress.Parse("192.168.1.77"), 7250, IdA);     // 同一个编号、另一个地址
    private static readonly DiscoveredTab5 B = new(IPAddress.Parse("192.168.1.51"), 7250, IdB);

    private static SessionResult Shown(SessionEnd end) => new(end, null, 1200, 1198, 3, 60, TimeSpan.FromSeconds(40));

    private static SessionResult NoFrame(SessionEnd end, AuthFailure? why = null) =>
        new(end, "没显示出一帧", 0, 0, 0, 60, TimeSpan.FromSeconds(1)) { AuthFailure = why };

    /// <summary>剧本：每一轮询问谁应答、每一条连接怎么结束。剧本里的询问用完了就取消（Run 返回）。</summary>
    private sealed class Rig
    {
        public PairStore Store { get; } = PairStore.InMemory(TestPairing.PcId);
        public PairingGate? Gate { get; init; }
        public List<DiscoveredTab5[]> Rounds { get; } = [];
        public Queue<SessionResult> Results { get; } = new();
        public List<DiscoveredTab5> Connected { get; } = [];
        public List<TimeSpan> Waits { get; } = [];
        public List<AutoEvent> Events { get; } = [];
        public CancellationTokenSource Cts { get; } = new();
        public int Asked { get; private set; }

        private sealed class Discovery(Rig rig) : IDiscovery
        {
            public IReadOnlyList<DiscoveredTab5> Query(CancellationToken ct)
            {
                if (rig.Asked < rig.Rounds.Count) return rig.Rounds[rig.Asked++];
                rig.Cts.Cancel();
                return [];
            }
        }

        public Rig Paired(byte[] id)
        {
            Store.Save(id, TestPairing.Key, "Tab5");
            return this;
        }

        public Rig Run()
        {
            var auto = new AutoConnector(new Discovery(this), Store, Gate, (tab5, _) =>
            {
                Connected.Add(tab5);
                return Results.Dequeue();
            }, (time, _) => Waits.Add(time));
            auto.Changed += Events.Add;
            auto.Run(Cts.Token);
            return this;
        }
    }

    /// <summary>在另一个线程上跑一段（一般是 Run），之后等它按时跑完、没有抛异常。</summary>
    private sealed class Background
    {
        private readonly Thread _thread;
        private Exception? _error;

        public Background(Action body)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception e)
                {
                    _error = e;
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public void Finish(int timeoutMs)
        {
            Assert.True(_thread.Join(timeoutMs), "后台那一段没有按时跑完");
            Assert.Null(_error);
        }
    }

    private static PairingGate WithPassword(int wrong = 0) => new(PairingSecret.FromPassword("2468"), wrong);

    private static TimeSpan[] Seconds(params int[] s) => [.. s.Select(x => TimeSpan.FromSeconds(x))];

    [Fact]
    public void 配对过的优先_哪怕没配对过的先应答()
    {
        var rig = new Rig { Gate = WithPassword() }.Paired(IdA);
        rig.Rounds.Add([B, A]);
        rig.Results.Enqueue(Shown(SessionEnd.Dropped));
        rig.Run();
        Assert.Equal([A], rig.Connected);
    }

    [Fact]
    public void 应答的都没配对过_设了密码就连它去配对()
    {
        var rig = new Rig { Gate = WithPassword() };
        rig.Rounds.Add([B]);
        rig.Results.Enqueue(Shown(SessionEnd.Dropped));
        rig.Run();
        Assert.Equal([B], rig.Connected);
        Assert.Equal(new AutoEvent.Connecting(B, Paired: false), rig.Events[0]);
    }

    [Fact]
    public void 应答的都没配对过_没设密码不连_隔3秒再问()
    {
        foreach (var gate in new[] { null, new PairingGate() })
        {
            var rig = new Rig { Gate = gate };
            rig.Rounds.Add([B]);
            rig.Rounds.Add([B]);
            rig.Run();
            Assert.Empty(rig.Connected);
            Assert.Equal(Seconds(3, 3), rig.Waits);
            Assert.Equal([new AutoEvent.NothingToConnect(1), new AutoEvent.NothingToConnect(1)], rig.Events);
        }
    }

    [Fact]
    public void 密码不对记满次数停止配对之后_不连没配对过的_配对过的照连()
    {
        var rig = new Rig { Gate = WithPassword(wrong: PairingGate.MaxWrong) }.Paired(IdA);
        rig.Rounds.Add([B]);
        rig.Rounds.Add([B, A]);
        rig.Results.Enqueue(Shown(SessionEnd.Dropped));
        rig.Run();
        Assert.Equal([A], rig.Connected);
    }

    [Fact]
    public void 没有Tab5应答_每隔3秒问一轮()
    {
        var rig = new Rig { Gate = WithPassword() }.Paired(IdA);
        for (int i = 0; i < 4; i++) rig.Rounds.Add([]);
        rig.Run();
        Assert.Equal(4, rig.Asked);
        Assert.Equal(Seconds(3, 3, 3, 3), rig.Waits);
        Assert.All(rig.Events, e => Assert.Equal(new AutoEvent.NothingToConnect(0), e));
    }

    [Fact]
    public void 连着几条连接都没显示出一帧_等1秒起每次加倍最长30秒()
    {
        var rig = new Rig().Paired(IdA);
        for (int i = 0; i < 8; i++)
        {
            rig.Rounds.Add([A]);
            rig.Results.Enqueue(NoFrame(i % 2 == 0 ? SessionEnd.ConnectFailed : SessionEnd.ProtocolError));
        }
        rig.Run();
        Assert.Equal(8, rig.Connected.Count);
        Assert.Equal(Seconds(1, 2, 4, 8, 16, 30, 30, 30), rig.Waits);
        Assert.Equal(TimeSpan.FromSeconds(30), ((AutoEvent.Ended)rig.Events[^1]).RetryIn);
    }

    [Fact]
    public void 显示出过画面的连接断了_马上再问_之后的等待从1秒重新算()
    {
        var rig = new Rig().Paired(IdA);
        foreach (var r in new[] { NoFrame(SessionEnd.ConnectFailed), NoFrame(SessionEnd.Dropped), Shown(SessionEnd.Dropped), NoFrame(SessionEnd.ConnectFailed) })
        {
            rig.Rounds.Add([A]);
            rig.Results.Enqueue(r);
        }
        rig.Run();
        Assert.Equal(4, rig.Connected.Count);
        Assert.Equal(Seconds(1, 2, 1), rig.Waits);      // 第三条显示出了画面：它结束之后没有等
    }

    [Fact]
    public void Tab5上的人退出之后_它不应答就不连_重新打开页面应答了才连()
    {
        var rig = new Rig().Paired(IdA);
        rig.Rounds.Add([A]);
        rig.Results.Enqueue(Shown(SessionEnd.DeviceBye));
        rig.Rounds.Add([]);         // 人退出之后 Tab5 不再监听，也不应答询问
        rig.Rounds.Add([]);
        rig.Rounds.Add([A]);        // 人在 Tab5 上重新打开了「无线显示」页
        rig.Results.Enqueue(Shown(SessionEnd.DeviceBye));
        rig.Run();
        Assert.Equal([A, A], rig.Connected);
        Assert.Equal(Seconds(3, 3), rig.Waits);
    }

    [Fact]
    public void 密码框上点了取消_没显示出画面_下一轮之前也要等()
    {
        var rig = new Rig { Gate = WithPassword() };
        rig.Rounds.Add([B]);
        rig.Results.Enqueue(NoFrame(SessionEnd.DeviceBye));
        rig.Run();
        Assert.Equal(Seconds(1), rig.Waits);
    }

    [Fact]
    public void 同一个编号从两个地址应答_认证没过的那个地址下一轮排到后面()
    {
        var rig = new Rig().Paired(IdA);
        rig.Rounds.Add([A, AElsewhere]);
        rig.Results.Enqueue(NoFrame(SessionEnd.AuthFailed, AuthFailure.KeyMismatch));      // 先应答的那个是假的：它没有配对密钥
        rig.Rounds.Add([A, AElsewhere]);
        rig.Results.Enqueue(Shown(SessionEnd.Dropped));
        rig.Rounds.Add([A, AElsewhere]);
        rig.Results.Enqueue(Shown(SessionEnd.Dropped));
        rig.Rounds.Add([A]);                                                                // 只剩认证没过的那个应答：还是会去试
        rig.Results.Enqueue(NoFrame(SessionEnd.AuthFailed, AuthFailure.KeyMismatch));
        rig.Run();
        Assert.Equal([A, AElsewhere, AElsewhere, A], rig.Connected);
    }

    [Fact]
    public void 先应答的那个地址每次都连不上_下一轮改连另一个地址()
    {
        // 一个带着同一个编号、总是先应答的坏应答者（不听连接、接了就断、乱回）：不能让真的那台永远轮不到
        foreach (var bad in new[] { SessionEnd.ConnectFailed, SessionEnd.Dropped, SessionEnd.ProtocolError, SessionEnd.DeviceBye })
        {
            var rig = new Rig().Paired(IdA);
            rig.Rounds.Add([A, AElsewhere]);
            rig.Results.Enqueue(NoFrame(bad));
            rig.Rounds.Add([A, AElsewhere]);
            rig.Results.Enqueue(Shown(SessionEnd.Dropped));
            rig.Rounds.Add([A, AElsewhere]);        // 坏的那个还是先应答：接着连上一次显示出画面的那个
            rig.Results.Enqueue(Shown(SessionEnd.Dropped));
            rig.Run();
            Assert.Equal([A, AElsewhere, AElsewhere], rig.Connected);
        }
    }

    [Fact]
    public void 这边的人断开的连接_不把那个地址排到后面_原来排在后面的也不挪回来()
    {
        var cancelled = NoFrame(SessionEnd.Cancelled);      // 握手期间 DropCurrent（改了密码、移除了配对）：说明不了这个地址行不行
        var rig = new Rig().Paired(IdA);
        rig.Rounds.Add([A, AElsewhere]);
        rig.Results.Enqueue(cancelled);
        rig.Rounds.Add([A, AElsewhere]);
        rig.Results.Enqueue(NoFrame(SessionEnd.ConnectFailed));
        rig.Rounds.Add([A, AElsewhere]);
        rig.Results.Enqueue(cancelled);
        rig.Rounds.Add([A, AElsewhere]);
        rig.Results.Enqueue(Shown(SessionEnd.Dropped));
        rig.Run();
        Assert.Equal([A, A, AElsewhere, AElsewhere], rig.Connected);
        Assert.Equal(Seconds(1), rig.Waits);        // 只有连不上的那一次要等
    }

    [Fact]
    public void 认证没过的地址后来连上了_不再排到后面()
    {
        var rig = new Rig().Paired(IdA);
        rig.Rounds.Add([A, AElsewhere]);
        rig.Results.Enqueue(NoFrame(SessionEnd.AuthFailed, AuthFailure.Forgotten));
        rig.Rounds.Add([A, AElsewhere]);
        rig.Results.Enqueue(NoFrame(SessionEnd.AuthFailed, AuthFailure.Forgotten));
        rig.Rounds.Add([A, AElsewhere]);        // 两个都没过：按应答的先后
        rig.Results.Enqueue(Shown(SessionEnd.Dropped));
        rig.Rounds.Add([A, AElsewhere]);
        rig.Results.Enqueue(Shown(SessionEnd.Dropped));
        rig.Run();
        Assert.Equal([A, AElsewhere, A, A], rig.Connected);
    }

    [Fact]
    public void 画面来源结束_不再询问()
    {
        var rig = new Rig().Paired(IdA);
        rig.Rounds.Add([A]);
        rig.Results.Enqueue(Shown(SessionEnd.Completed));
        rig.Rounds.Add([A]);
        rig.Run();
        Assert.Equal(1, rig.Asked);
        Assert.False(rig.Cts.IsCancellationRequested);
    }

    [Fact]
    public void 每一步都报出来_连哪一台_怎么结束的_多久之后再问()
    {
        var rig = new Rig().Paired(IdA);
        rig.Rounds.Add([]);
        rig.Rounds.Add([A]);
        var failed = NoFrame(SessionEnd.ConnectFailed);
        rig.Results.Enqueue(failed);
        rig.Run();
        Assert.Equal(
        [
            new AutoEvent.NothingToConnect(0),
            new AutoEvent.Connecting(A, Paired: true),
            new AutoEvent.Ended(A, failed, TimeSpan.FromSeconds(1)),
        ], rig.Events);
    }

    [Fact]
    public void 没有可连的时候取消_不用等满3秒()
    {
        var store = PairStore.InMemory(TestPairing.PcId);
        var auto = new AutoConnector(new Empty(), store, null, (_, _) => throw new InvalidOperationException("不该去连"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var clock = Stopwatch.StartNew();
        auto.Run(cts.Token);
        Assert.InRange(clock.ElapsedMilliseconds, 100, 2000);
    }

    private sealed class Empty : IDiscovery
    {
        public int Asked;
        public IReadOnlyList<DiscoveredTab5> Query(CancellationToken ct)
        {
            Interlocked.Increment(ref Asked);
            return [];
        }
    }

    [Fact]
    public void 等着的时候叫醒_马上问下一轮()
    {
        var store = PairStore.InMemory(TestPairing.PcId);
        var discovery = new Empty();
        var auto = new AutoConnector(discovery, store, null, (_, _) => throw new InvalidOperationException("不该去连"));
        using var cts = new CancellationTokenSource();
        var run = new Background(() => auto.Run(cts.Token));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref discovery.Asked) == 1, 2000));
        Thread.Sleep(50);       // 这时它在等那 3 秒
        auto.Wake();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref discovery.Asked) >= 2, 1000), "叫醒之后 1 秒内应该问了第二轮");
        cts.Cancel();
        run.Finish(2000);
    }

    [Fact]
    public void 断开正连着的那一条_自动连接照常继续_马上重新询问()
    {
        var rig = new Rig().Paired(IdA);
        rig.Rounds.Add([A]);
        rig.Rounds.Add([A]);
        using var connected = new ManualResetEventSlim();
        AutoConnector? auto = null;
        int n = 0;
        auto = new AutoConnector(new RigDiscovery(rig), rig.Store, null, (tab5, ct) =>
        {
            rig.Connected.Add(tab5);
            if (++n == 2) return Shown(SessionEnd.Dropped);
            connected.Set();
            Assert.True(ct.WaitHandle.WaitOne(5000), "DropCurrent 之后这条连接的取消应该生效");
            return new SessionResult(SessionEnd.Cancelled, null, 500, 499, 0, 60, TimeSpan.FromSeconds(20));
        }, (time, _) => rig.Waits.Add(time));
        var run = new Background(() => auto.Run(rig.Cts.Token));
        Assert.True(connected.Wait(2000));
        auto.DropCurrent();
        run.Finish(5000);
        Assert.Equal([A, A], rig.Connected);
        Assert.Empty(rig.Waits);
    }

    // 上面那条要自己写 connect，所以单独给它一个按剧本应答的询问
    private sealed class RigDiscovery(Rig rig) : IDiscovery
    {
        private int _asked;
        public IReadOnlyList<DiscoveredTab5> Query(CancellationToken ct)
        {
            if (_asked < rig.Rounds.Count) return rig.Rounds[_asked++];
            rig.Cts.Cancel();
            return [];
        }
    }

    [Fact]
    public void 没有连着的时候断开_什么都不发生()
    {
        var discovery = new Empty();
        var auto = new AutoConnector(discovery, PairStore.InMemory(TestPairing.PcId), null, (_, _) => throw new InvalidOperationException("不该去连"));
        auto.DropCurrent();
        auto.DropCurrent();
        using var cts = new CancellationTokenSource();
        var run = new Background(() => auto.Run(cts.Token));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref discovery.Asked) == 1, 2000));
        Thread.Sleep(300);
        Assert.Equal(1, Volatile.Read(ref discovery.Asked));       // 照常等那 3 秒：之前的断开没有留下什么
        cts.Cancel();
        run.Finish(2000);
    }

    [Fact]
    public void 不在等的时候叫醒_下一次等待不等_连着叫几次也只管一次()
    {
        var discovery = new Empty();
        var auto = new AutoConnector(discovery, PairStore.InMemory(TestPairing.PcId), null, (_, _) => throw new InvalidOperationException("不该去连"));
        auto.Wake();        // Run 还没开始
        auto.Wake();
        auto.Wake();
        using var cts = new CancellationTokenSource();
        var run = new Background(() => auto.Run(cts.Token));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref discovery.Asked) >= 2, 1000), "第一次等待应该不等，1 秒内问到第二轮");
        Thread.Sleep(300);
        Assert.Equal(2, Volatile.Read(ref discovery.Asked));       // 第二次等待照常等 3 秒
        cts.Cancel();
        run.Finish(2000);
    }

    [Fact]
    public void 状态回调里抛了异常_原样抛出来_之后还能再跑()
    {
        var rig = new Rig().Paired(IdA);
        rig.Rounds.Add([A]);
        rig.Rounds.Add([A]);
        var boom = new InvalidOperationException("界面已经关了");
        int connected = 0, events = 0;
        var auto = new AutoConnector(new RigDiscovery(rig), rig.Store, null, (_, _) =>
        {
            connected++;
            return Shown(SessionEnd.Completed);
        }, (time, _) => rig.Waits.Add(time));
        auto.Changed += _ =>
        {
            if (events++ == 0) throw boom;
        };
        Assert.Same(boom, Assert.Throws<InvalidOperationException>(() => auto.Run(rig.Cts.Token)));
        Assert.Equal(0, connected);         // 「要去连这一台了」的回调抛的：没有去连
        auto.Run(rig.Cts.Token);
        Assert.Equal(1, connected);
    }

    [Fact]
    public void 连接里本端的代码出了错_原样抛出来_之后还能再跑()
    {
        var rig = new Rig().Paired(IdA);
        rig.Rounds.Add([A]);
        rig.Rounds.Add([A]);
        var boom = new PairStoreException(new IOException("磁盘满了"));     // 握手走完了、配对存不进文件：DisplaySession.Run 就是这样抛的
        int n = 0;
        var auto = new AutoConnector(new RigDiscovery(rig), rig.Store, null, (_, _) => n++ == 0 ? throw boom : Shown(SessionEnd.Completed),
                                     (time, _) => rig.Waits.Add(time));
        Assert.Same(boom, Assert.Throws<PairStoreException>(() => auto.Run(rig.Cts.Token)));
        auto.Run(rig.Cts.Token);
        Assert.Equal(2, n);
    }

    [Fact]
    public void 同一时刻只能有一个Run()
    {
        var discovery = new Empty();
        var auto = new AutoConnector(discovery, PairStore.InMemory(TestPairing.PcId), null, (_, _) => Shown(SessionEnd.Dropped));
        using var cts = new CancellationTokenSource();
        var run = new Background(() => auto.Run(cts.Token));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref discovery.Asked) >= 1, 2000));
        Assert.Throws<InvalidOperationException>(() => auto.Run(cts.Token));
        cts.Cancel();
        run.Finish(2000);
    }

    [Fact]
    public void 挑选_不改动应答的先后_只把上一次没显示出画面的挪到同一档的后面()
    {
        var store = PairStore.InMemory(TestPairing.PcId);
        store.Save(IdA, TestPairing.Key, "Tab5");
        Assert.Equal([A, AElsewhere], AutoConnector.Pick([B, A, AElsewhere], store, canPair: true));
        Assert.Equal([AElsewhere, A], AutoConnector.Pick([B, A, AElsewhere], store, canPair: true, new HashSet<DiscoveredTab5> { A }));
        Assert.Equal([B], AutoConnector.Pick([B], store, canPair: true));
        Assert.Empty(AutoConnector.Pick([B], store, canPair: false));
        Assert.Empty(AutoConnector.Pick([], store, canPair: true));
    }
}
