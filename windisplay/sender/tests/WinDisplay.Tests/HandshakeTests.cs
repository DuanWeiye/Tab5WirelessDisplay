using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>
/// 连上之后的握手走哪条路（PROTOCOL.md「认证和加密」）：配对过的直接认证；没配对过、或者 Tab5 那边没有这把密钥了，
/// 设了配对密码才配对；认证没过、密码不对、Tab5 上的人取消、对面不照协议来，各自怎么收场，两边存了什么、密码不对的次数怎么记。
/// 对着本机回环上的假 Tab5（它照协议走 Tab5 那一侧，并检查发送端有没有守规矩）。
/// </summary>
public class HandshakeTests
{
    private const string Password = "2468";
    private static readonly SessionOptions Options = new();

    /// <summary>电脑上设了配对密码。</summary>
    private static PairingGate Gate(string password = Password) => new(PairingSecret.FromPassword(password));

    /// <summary>对着假 Tab5 跑一次会话（画面来源给 3 幅就结束），返回结果和配对走过的几步。假 Tab5 那边的连接结束了才返回。</summary>
    private static (SessionResult Result, List<PairingStep> Steps) Run(FakeTab5 tab5, LinkAuth auth, SessionOptions? options = null)
    {
        var session = new DisplaySession(new DisplaySessionTests.MovingBar(3), options ?? Options, auth);
        var steps = new List<PairingStep>();
        session.Pairing += steps.Add;
        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());
        Assert.True(tab5.WaitEnd(), "假 Tab5 那边的连接没有结束");
        return (r, steps);
    }

    /// <summary>
    /// 对面不照协议来的时候：发送端断开了，而且之后一个字节都没发。断开是关闭还是重置都算
    /// （不合规定的报文读到一半就不读了，剩下没读的字节会让系统回 RST，对一个不守协议的对端这无所谓）。
    /// </summary>
    private static void AssertHungUpSilently(FakeTab5 tab5)
    {
        Assert.Contains(tab5.Hangup, new[] { "closed", "reset" });
        Assert.Equal(0, tab5.BytesAfterHangup);
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

    /// <summary>电脑上另外配对着的一台 Tab5（「客厅的 Tab5」），和它那一侧的存储：它手里有这台电脑的配对密钥，认证过得了。</summary>
    private static readonly byte[] LivingRoomId = [.. Enumerable.Range(0xC0, Proto.IdSize).Select(i => (byte)i)];

    private static PairStore LivingRoomStore()
    {
        PairStore store = PairStore.InMemory(LivingRoomId);
        store.Save(TestPairing.PcId, TestPairing.Key, TestPairing.PcName);
        return store;
    }

    [Fact]
    public void 按自动发现的应答去连_HELLO里是应答里的那个编号_照常认证显示()
    {
        using var tab5 = new FakeTab5();
        var session = new DisplaySession(new DisplaySessionTests.MovingBar(3), Options, TestPairing.SenderAuth());

        SessionResult r = session.Run(new DiscoveredTab5(IPAddress.Loopback, tab5.Port, TestPairing.Tab5Id), TestLimit.Session());

        Assert.True(tab5.WaitEnd(), "假 Tab5 那边的连接没有结束");
        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Null(tab5.Violation);
        Assert.Equal([Proto.TAuth, Proto.TReady], tab5.Handshake);
    }

    [Fact]
    public void 按自动发现的应答去连_HELLO里是另一台配对过的编号_什么都不发就断开_配对都不动()
    {
        // 书房、客厅两台都配对过。应答里报的是书房那台的编号，连上之后 HELLO 里却是客厅那台的：
        // 照 HELLO 里的去查配对，认证是过得了的——可连着的就不是当初按应答决定去连的那一台了
        PairStore pc = TestPairing.SenderStore();
        pc.Save(LivingRoomId, TestPairing.Key, "客厅的 Tab5");
        using var tab5 = new FakeTab5 { Store = LivingRoomStore(), Name = "客厅的 Tab5" };
        var session = new DisplaySession(new DisplaySessionTests.MovingBar(3), Options, new LinkAuth(pc, TestPairing.PcName));
        bool connected = false;
        session.Connected += _ => connected = true;

        SessionResult r = session.Run(new DiscoveredTab5(IPAddress.Loopback, tab5.Port, TestPairing.Tab5Id), TestLimit.Session());

        Assert.True(tab5.WaitEnd(), "假 Tab5 那边的连接没有结束");
        Assert.Equal(SessionEnd.ProtocolError, r.End);
        Assert.Contains("编号", r.Detail);
        Assert.False(connected);
        Assert.Empty(tab5.Handshake);                   // AUTH 都没发：假 Tab5 等握手的第一个报文时连接就关了
        Assert.Null(tab5.Violation);
        Assert.Equal(2, pc.Peers.Count);

        // 手填地址去连（没有应答可对）的不查这一条：同一台假 Tab5 照常认证
        using var again = new FakeTab5 { Store = LivingRoomStore(), Name = "客厅的 Tab5" };
        var (r2, _) = Run(again, new LinkAuth(pc, TestPairing.PcName));
        Assert.Equal(SessionEnd.Completed, r2.End);
    }

    [Fact]
    public void 按自动发现的应答去连_没配对过的那台HELLO里换了编号_不配对_密码框不弹()
    {
        PairStore pc = TestPairing.SenderStore(paired: false);
        PairingGate gate = Gate();
        using var tab5 = new FakeTab5 { Store = PairStore.InMemory(LivingRoomId), AskPassword = FakeTab5.Answers(Password) };
        var session = new DisplaySession(new DisplaySessionTests.MovingBar(3), Options, new LinkAuth(pc, TestPairing.PcName, gate));

        SessionResult r = session.Run(new DiscoveredTab5(IPAddress.Loopback, tab5.Port, TestPairing.Tab5Id), TestLimit.Session());

        Assert.True(tab5.WaitEnd(), "假 Tab5 那边的连接没有结束");
        Assert.Equal(SessionEnd.ProtocolError, r.End);
        Assert.Empty(tab5.Handshake);                   // PAIR 都没发
        Assert.Empty(tab5.Prompts);
        Assert.Empty(pc.Peers);
        Assert.Equal(0, gate.WrongCount);
    }

    [Fact]
    public void 第一次配对_Tab5上输对密码_两边存下同一把密钥_之后再连不用密码()
    {
        PairStore pc = TestPairing.SenderStore(paired: false), dev = TestPairing.Tab5Store(paired: false);
        PairingGate gate = Gate();
        using (var tab5 = new FakeTab5 { Store = dev, AskPassword = FakeTab5.Answers(Password) })
        {
            var (r, steps) = Run(tab5, new LinkAuth(pc, TestPairing.PcName, gate));

            Assert.Equal(SessionEnd.Completed, r.End);
            Assert.Equal(r.FramesSent, r.FramesAcked);
            Assert.InRange(r.FramesAcked, 2, 3);
            Assert.Null(tab5.Violation);
            Assert.Equal([Proto.TPair, Proto.TPairOk, Proto.TAuth, Proto.TReady], tab5.Handshake);
            Assert.Equal([new PasswordPrompt(TestPairing.PcName, Wrong: false, Known: false)], tab5.Prompts);
            Assert.True(tab5.NewlyPaired);
            Assert.Equal([PairingStep.WaitingForPassword, PairingStep.PasswordAccepted], steps);
        }
        byte[]? key = pc.FindKey(TestPairing.Tab5Id);
        Assert.NotNull(key);
        Assert.Equal(key, dev.FindKey(TestPairing.PcId));
        Assert.Equal(TestPairing.Tab5Name, Assert.Single(pc.Peers).Name);
        Assert.Equal(TestPairing.PcName, Assert.Single(dev.Peers).Name);
        Assert.True(gate.CanPair);                      // 配了一台之后照样可以配别的

        // 再连：认证一下就开始发画面，Tab5 上不弹密码框（这一次连配对密码都没给）
        using var again = new FakeTab5 { Store = dev };
        var (r2, steps2) = Run(again, new LinkAuth(pc, TestPairing.PcName));

        Assert.Equal(SessionEnd.Completed, r2.End);
        Assert.Equal(3, r2.FramesAcked);
        Assert.Null(again.Violation);
        Assert.Equal([Proto.TAuth, Proto.TReady], again.Handshake);
        Assert.Empty(again.Prompts);
        Assert.Empty(steps2);
        Assert.False(again.NewlyPaired);
        Assert.Equal(key, pc.FindKey(TestPairing.Tab5Id));
    }

    [Fact]
    public void 两边改了名字_下次连上各自记下对方的新名字_密钥不动()
    {
        PairStore pc = TestPairing.SenderStore(), dev = TestPairing.Tab5Store();
        using var tab5 = new FakeTab5 { Store = dev, Name = "Tab5-客厅" };

        var (r, _) = Run(tab5, new LinkAuth(pc, "新装的台式机"));

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Equal("Tab5-客厅", Assert.Single(pc.Peers).Name);
        Assert.Equal("新装的台式机", Assert.Single(dev.Peers).Name);
        Assert.Equal(TestPairing.Key, pc.FindKey(TestPairing.Tab5Id));
        Assert.Equal(TestPairing.Key, dev.FindKey(TestPairing.PcId));
    }

    [Fact]
    public void 密码输错两次第三次对了_每次都重新发PAIR_配对成功()
    {
        PairStore pc = TestPairing.SenderStore(paired: false), dev = TestPairing.Tab5Store(paired: false);
        PairingGate gate = Gate();
        using var tab5 = new FakeTab5 { Store = dev, AskPassword = FakeTab5.Answers("8821", "8182", Password) };

        var (r, steps) = Run(tab5, new LinkAuth(pc, TestPairing.PcName, gate));

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Null(tab5.Violation);
        Assert.Equal([Proto.TPair, Proto.TPair, Proto.TPair, Proto.TPairOk, Proto.TAuth, Proto.TReady], tab5.Handshake);
        Assert.Equal([false, true, true], tab5.Prompts.Select(p => p.Wrong));       // 第二、三次弹出来是标红的
        Assert.Equal([PairingStep.WaitingForPassword, PairingStep.WrongPassword, PairingStep.WaitingForPassword, PairingStep.WrongPassword,
                      PairingStep.WaitingForPassword, PairingStep.PasswordAccepted], steps);
        Assert.Equal(pc.FindKey(TestPairing.Tab5Id), dev.FindKey(TestPairing.PcId));
        Assert.NotNull(pc.FindKey(TestPairing.Tab5Id));
        Assert.Equal(0, gate.WrongCount);               // 配对成功：前面记的两次清零
    }

    [Fact]
    public void 密码一直不对_满十次就断开_停止配对_两边都不存()
    {
        PairStore pc = TestPairing.SenderStore(paired: false), dev = TestPairing.Tab5Store(paired: false);
        PairingGate gate = Gate();
        using var tab5 = new FakeTab5 { Store = dev, AskPassword = _ => "0000" };

        var (r, steps) = Run(tab5, new LinkAuth(pc, TestPairing.PcName, gate));

        Assert.Equal(SessionEnd.AuthFailed, r.End);
        Assert.Equal(AuthFailure.WrongPassword, r.AuthFailure);
        Assert.Equal(0, r.FramesSent);
        Assert.Equal(10, tab5.PromptCount);
        Assert.Equal(Enumerable.Repeat(Proto.TPair, 10), tab5.Handshake);
        Assert.Null(tab5.Violation);
        Assert.Equal("eof", tab5.EndedBy);              // 第 10 次不对之后电脑直接断开，不再发 PAIR
        Assert.Equal(20, steps.Count);
        Assert.False(gate.CanPair);
        Assert.True(gate.Stopped);
        Assert.Empty(pc.Peers);
        Assert.Empty(dev.Peers);
    }

    [Fact]
    public void 密码不对的次数_换了连接换了Tab5也接着数()
    {
        // 不然冒充 Tab5 的程序每次断开重连就又有 10 次机会
        PairStore pc = TestPairing.SenderStore(paired: false);
        PairingGate gate = Gate();
        var auth = new LinkAuth(pc, TestPairing.PcName, gate);
        using (var first = new FakeTab5 { Store = TestPairing.Tab5Store(paired: false), AskPassword = FakeTab5.Answers("1111", "2222", "3333", null) })
        {
            var (r, _) = Run(first, auth);

            Assert.Equal(SessionEnd.DeviceBye, r.End);          // 输错三次之后，Tab5 上的人点了取消
            Assert.Equal(4, first.PromptCount);
            Assert.Equal(("closed", 0), (first.Hangup, first.BytesAfterHangup));
        }
        Assert.True(gate.CanPair);

        using var second = new FakeTab5 { Store = PairStore.InMemory(), AskPassword = _ => "4444" };       // 另一台 Tab5
        var (r2, _) = Run(second, auth);

        Assert.Equal(SessionEnd.AuthFailed, r2.End);
        Assert.Equal(AuthFailure.WrongPassword, r2.AuthFailure);
        Assert.Equal(7, second.PromptCount);            // 前面记了 3 次，这里再 7 次就满了
        Assert.False(gate.CanPair);
        Assert.Empty(pc.Peers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]      // 托盘程序一直留着这个对象：人还没设密码的时候也一样
    public void 没配对过_电脑上没设配对密码_不发PAIR_报没配对过(bool gateWithoutPassword)
    {
        var auth = new LinkAuth(TestPairing.SenderStore(paired: false), TestPairing.PcName, gateWithoutPassword ? new PairingGate() : null);
        using var tab5 = new FakeTab5 { Store = TestPairing.Tab5Store(paired: false), AskPassword = FakeTab5.Answers(Password) };

        var (r, steps) = Run(tab5, auth);

        Assert.Equal(SessionEnd.AuthFailed, r.End);
        Assert.Equal(AuthFailure.NotPaired, r.AuthFailure);
        Assert.Contains(TestPairing.Tab5Name, r.Detail);
        Assert.Empty(tab5.Handshake);                   // HELLO 之后什么都没发就断开了
        Assert.Equal(0, tab5.PromptCount);
        Assert.Equal("eof", tab5.EndedBy);
        Assert.Empty(steps);
    }

    [Fact]
    public void Tab5上的人在密码框上点了取消_报DeviceBye_不算密码不对()
    {
        PairStore pc = TestPairing.SenderStore(paired: false);
        PairingGate gate = Gate();
        using var tab5 = new FakeTab5 { Store = TestPairing.Tab5Store(paired: false), AskPassword = FakeTab5.Answers() };

        var (r, steps) = Run(tab5, new LinkAuth(pc, TestPairing.PcName, gate));

        Assert.Equal(SessionEnd.DeviceBye, r.End);
        Assert.Equal(("closed", 0), (tab5.Hangup, tab5.BytesAfterHangup));      // 收到 BYE 马上关连接，什么都不回
        Assert.Equal([PairingStep.WaitingForPassword], steps);
        Assert.Equal(0, gate.WrongCount);
        Assert.Empty(pc.Peers);
    }

    [Theory]
    [InlineData(false)]     // Tab5 那边把这台电脑忘了（被挤掉、被人移除、恢复过出厂设置）
    [InlineData(true)]      // 两边存的不是同一把（上次重新配对时最后一步断了）
    public void Tab5那边没有这把配对密钥了_电脑上没设配对密码_报出来_电脑这边的配对不删(bool tab5HasAnotherKey)
    {
        PairStore pc = TestPairing.SenderStore(), dev = TestPairing.Tab5Store(paired: false);
        if (tab5HasAnotherKey) dev.Save(TestPairing.PcId, RandomNumberGenerator.GetBytes(32), TestPairing.PcName);
        using var tab5 = new FakeTab5 { Store = dev, AskPassword = FakeTab5.Answers(Password) };

        var (r, steps) = Run(tab5, new LinkAuth(pc, TestPairing.PcName));

        Assert.Equal(SessionEnd.AuthFailed, r.End);
        Assert.Equal(AuthFailure.Forgotten, r.AuthFailure);
        Assert.Equal([Proto.TAuth], tab5.Handshake);    // 收到 AUTH_UNKNOWN 之后没有发 PAIR
        Assert.Equal(0, tab5.PromptCount);
        Assert.Empty(steps);
        Assert.Equal(TestPairing.Key, pc.FindKey(TestPairing.Tab5Id));      // AUTH_UNKNOWN 谁都能伪造：不因为它删配对
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tab5那边没有这把配对密钥了_同一条连接上重新配对(bool tab5HasAnotherKey)
    {
        PairStore pc = TestPairing.SenderStore(), dev = TestPairing.Tab5Store(paired: false);
        if (tab5HasAnotherKey) dev.Save(TestPairing.PcId, RandomNumberGenerator.GetBytes(32), TestPairing.PcName);
        using var tab5 = new FakeTab5 { Store = dev, AskPassword = FakeTab5.Answers(Password) };

        var (r, steps) = Run(tab5, new LinkAuth(pc, TestPairing.PcName, Gate()));

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Null(tab5.Violation);
        Assert.Equal([Proto.TAuth, Proto.TPair, Proto.TPairOk, Proto.TAuth, Proto.TReady], tab5.Handshake);
        // Tab5 上还留着这台电脑的旧配对时，密码框的标题是「重新配对」
        Assert.Equal([new PasswordPrompt(TestPairing.PcName, Wrong: false, Known: tab5HasAnotherKey)], tab5.Prompts);
        Assert.Equal([PairingStep.WaitingForPassword, PairingStep.PasswordAccepted], steps);
        byte[] key = pc.FindKey(TestPairing.Tab5Id)!;
        Assert.NotEqual(TestPairing.Key, key);          // 换成了新配出来的那一把
        Assert.Equal(key, dev.FindKey(TestPairing.PcId));
        Assert.Single(pc.Peers);
        Assert.Single(dev.Peers);
    }

    [Fact]
    public void 配对到最后一步Tab5不见了_不算配对成功_不存_密码不对的次数不清零()
    {
        PairStore pc = TestPairing.SenderStore(paired: false), dev = TestPairing.Tab5Store(paired: false);
        PairingGate gate = Gate();
        using var tab5 = new FakeTab5 { Store = dev, AskPassword = FakeTab5.Answers("8821", Password), Fault = Fault.VanishAfterPairOk };

        var (r, steps) = Run(tab5, new LinkAuth(pc, TestPairing.PcName, gate));

        Assert.Equal(SessionEnd.ConnectFailed, r.End);
        Assert.Equal([PairingStep.WaitingForPassword, PairingStep.WrongPassword, PairingStep.WaitingForPassword, PairingStep.PasswordAccepted], steps);
        Assert.Empty(pc.Peers);                         // 密码对上了，握手却没走完：不存
        Assert.Empty(dev.Peers);
        Assert.Equal(1, gate.WrongCount);               // 也不算配对成功：前面输错的那一次还记着
        Assert.True(gate.CanPair);                      // 人接着在 Tab5 上再配一次就行
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 冒充的Tab5_手里没有配对密钥_READY认证不过_断开且什么都不再发(bool passwordSet)
    {
        PairStore pc = TestPairing.SenderStore();
        PairingGate? gate = passwordSet ? Gate() : null;
        using var tab5 = new FakeTab5 { Fault = Fault.ReadyWrongKey, AskPassword = FakeTab5.Answers(Password) };

        var (r, steps) = Run(tab5, new LinkAuth(pc, TestPairing.PcName, gate));

        Assert.Equal(SessionEnd.AuthFailed, r.End);
        Assert.Equal(AuthFailure.KeyMismatch, r.AuthFailure);
        Assert.Equal(0, r.FramesSent);
        Assert.Equal([Proto.TAuth], tab5.Handshake);    // 自己的 READY 没发，也没有转头去发 PAIR（设了配对密码也不）
        Assert.Equal(("closed", 0), (tab5.Hangup, tab5.BytesAfterHangup));
        Assert.Empty(steps);
        Assert.Equal(TestPairing.Key, pc.FindKey(TestPairing.Tab5Id));
    }

    [Fact]
    public void 认证时Tab5上的人正好离开了页面_收到明文的BYE_报DeviceBye()
    {
        using var tab5 = new FakeTab5 { Fault = Fault.ByeAfterAuth };

        var (r, _) = Run(tab5, TestPairing.SenderAuth());

        Assert.Equal(SessionEnd.DeviceBye, r.End);
        Assert.Equal(("closed", 0), (tab5.Hangup, tab5.BytesAfterHangup));
    }

    [Fact]
    public void 等人在Tab5上输密码的时候取消_马上返回_Tab5那边的密码框跟着关掉()
    {
        PairStore pc = TestPairing.SenderStore(paired: false);
        PairingGate gate = Gate();
        using var tab5 = new FakeTab5
        {
            Store = TestPairing.Tab5Store(paired: false), AskPassword = FakeTab5.Answers(Password), PasswordDelay = TimeSpan.FromSeconds(30),
        };
        var session = new DisplaySession(new DisplaySessionTests.MovingBar(3), Options, new LinkAuth(pc, TestPairing.PcName, gate));
        using var cts = new CancellationTokenSource();
        SessionResult? r = null;
        var run = new Thread(() => r = session.Run("127.0.0.1", tab5.Port, cts.Token));
        run.Start();
        Assert.True(WaitUntil(() => tab5.PromptCount == 1), "Tab5 上的密码框没弹出来");
        Thread.Sleep(200);                              // 人还在输

        cts.Cancel();

        Assert.True(run.Join(2000), "取消之后 2 秒还没返回");
        Assert.Equal(SessionEnd.Cancelled, r!.End);
        Assert.True(tab5.WaitEnd(2000), "连接断了，Tab5 那边的密码框却没关");
        Assert.Equal("eof", tab5.EndedBy);
        Assert.Null(tab5.Violation);                    // 等的时候、取消的时候都没有往 Tab5 发东西
        Assert.Equal(0, gate.WrongCount);
        Assert.Empty(pc.Peers);
    }

    [Fact]
    public void Tab5迟迟不回PAIR_REPLY_到时限就断开_不算密码不对()
    {
        PairingGate gate = Gate();
        using var tab5 = new FakeTab5 { Store = TestPairing.Tab5Store(paired: false), Fault = Fault.SilentAfterPair };
        var sw = Stopwatch.StartNew();

        var (r, steps) = Run(tab5, new LinkAuth(TestPairing.SenderStore(paired: false), TestPairing.PcName, gate),
                             Options with { PairReplyTimeout = TimeSpan.FromMilliseconds(400) });

        Assert.Equal(SessionEnd.ConnectFailed, r.End);
        Assert.InRange(sw.ElapsedMilliseconds, 350, 3500);
        Assert.Equal([Proto.TPair], tab5.Handshake);
        Assert.Equal(("closed", 0), (tab5.Hangup, tab5.BytesAfterHangup));      // 等的时候、放弃的时候都没有再发报文
        Assert.Equal([PairingStep.WaitingForPassword], steps);
        Assert.Equal(0, gate.WrongCount);               // 对面没拿到任何能验证猜测的东西：不记
    }

    [Fact]
    public void 人输密码花的时间比握手每一步的时限长_发送端安静地等_配对成功()
    {
        // 等 PAIR_REPLY 用的是它自己的时限（默认 130 秒），不是握手每一步的那个：人输密码总要十几秒。
        // 这期间发送端只能等，发了东西 Tab5 就断开（假 Tab5 会记成 Violation）
        PairStore pc = TestPairing.SenderStore(paired: false), dev = TestPairing.Tab5Store(paired: false);
        using var tab5 = new FakeTab5 { Store = dev, AskPassword = FakeTab5.Answers(Password), PasswordDelay = TimeSpan.FromSeconds(2) };

        var (r, _) = Run(tab5, new LinkAuth(pc, TestPairing.PcName, Gate()), Options with { ConnectTimeout = TimeSpan.FromMilliseconds(800) });

        Assert.Equal(SessionEnd.Completed, r.End);
        Assert.Null(tab5.Violation);
        Assert.True(tab5.NewlyPaired);
        Assert.Equal(pc.FindKey(TestPairing.Tab5Id), dev.FindKey(TestPairing.PcId));
    }

    [Fact]
    public void 停止配对之后_没配对过的Tab5不发PAIR_配对过的照常连_人在电脑上恢复了又可以配()
    {
        PairStore pc = TestPairing.SenderStore();               // 和一台 Tab5 配对过
        PairingGate gate = Gate();
        var auth = new LinkAuth(pc, TestPairing.PcName, gate);
        using (var guesser = new FakeTab5 { Store = PairStore.InMemory(), AskPassword = _ => "0000" })     // 另一台，密码一直不对
        {
            Assert.Equal(AuthFailure.WrongPassword, Run(guesser, auth).Result.AuthFailure);
        }
        Assert.True(gate.Stopped);

        // 它再来：HELLO 之后什么都不发就断开，Tab5 上不弹密码框——它试不了第 11 个密码
        using (var guesser = new FakeTab5 { Store = PairStore.InMemory(), AskPassword = FakeTab5.Answers(Password) })
        {
            var (r, steps) = Run(guesser, auth);

            Assert.Equal(SessionEnd.AuthFailed, r.End);
            Assert.Equal(AuthFailure.NotPaired, r.AuthFailure);
            Assert.Contains("停止配对", r.Detail);
            Assert.Empty(guesser.Handshake);
            Assert.Equal(0, guesser.PromptCount);
            Assert.Empty(steps);
        }

        // 配对过的那一台不受影响
        using (var known = new FakeTab5 { Store = TestPairing.Tab5Store() })
        {
            var (r, _) = Run(known, auth);

            Assert.Equal(SessionEnd.Completed, r.End);
            Assert.Equal([Proto.TAuth, Proto.TReady], known.Handshake);
        }
        Assert.True(gate.Stopped);                              // 认证通过不是配对成功：照旧停着

        gate.Resume();                                          // 人在托盘里点了恢复

        using var newcomer = new FakeTab5 { Store = PairStore.InMemory(), AskPassword = FakeTab5.Answers(Password) };
        var (r3, _) = Run(newcomer, auth);

        Assert.Equal(SessionEnd.Completed, r3.End);
        Assert.Null(newcomer.Violation);
        Assert.True(newcomer.NewlyPaired);
        Assert.Equal(2, pc.Peers.Count);
        Assert.Equal(0, gate.WrongCount);
    }

    [Theory]
    [InlineData(nameof(Fault.HelloVersion))]
    [InlineData(nameof(Fault.HelloFlags))]
    [InlineData(nameof(Fault.HelloShort))]
    [InlineData(nameof(Fault.ByeBeforeHello))]
    [InlineData(nameof(Fault.ByeWithPayload))]
    [InlineData(nameof(Fault.PairReplyForAuth))]
    [InlineData(nameof(Fault.AuthOkFlags))]
    [InlineData(nameof(Fault.AuthOkArg))]
    [InlineData(nameof(Fault.AuthOkShort))]
    [InlineData(nameof(Fault.AuthOkZeroKey))]
    [InlineData(nameof(Fault.EncryptedByeFirst))]
    public void 认证时Tab5发来的东西不合协议_断开且什么都不再发_报ProtocolError(string fault)
    {
        PairStore pc = TestPairing.SenderStore();
        using var tab5 = new FakeTab5 { Fault = Enum.Parse<Fault>(fault) };

        var (r, _) = Run(tab5, new LinkAuth(pc, TestPairing.PcName));

        Assert.Equal(SessionEnd.ProtocolError, r.End);
        Assert.Equal(0, r.FramesSent);
        AssertHungUpSilently(tab5);
        Assert.Equal(TestPairing.Key, pc.FindKey(TestPairing.Tab5Id));
    }

    [Theory]
    [InlineData(nameof(Fault.HelloTrickle))]        // 一个字节一个字节地等下去的话要 7 秒多
    [InlineData(nameof(Fault.AuthOkTrickle))]       // 4 秒
    [InlineData(nameof(Fault.ReadyTrickle))]        // 2.4 秒——等得到的话握手就走完了，结果不会是 ConnectFailed
    [InlineData(nameof(Fault.PairReplyTrickle))]    // 10 秒多
    public void 握手的报文一点一点地来_整个报文到期限没到齐就断开(string fault)
    {
        // 期限管的是「这一步的报文到齐」，不是「两次收到数据之间」：不然对面每隔一会儿给一个字节，就能把唯一的发送位置一直拖着
        Fault how = Enum.Parse<Fault>(fault);
        bool pairing = how == Fault.PairReplyTrickle;
        PairStore pc = TestPairing.SenderStore(paired: !pairing);
        PairingGate gate = Gate();
        using var tab5 = new FakeTab5 { Store = TestPairing.Tab5Store(paired: !pairing), AskPassword = FakeTab5.Answers(Password), Fault = how };
        // 等 PAIR_REPLY 用的是它自己的时限，别的几步用握手每一步的那个
        TimeSpan limit = TimeSpan.FromMilliseconds(600);
        SessionOptions options = pairing ? Options with { PairReplyTimeout = limit } : Options with { ConnectTimeout = limit };
        var session = new DisplaySession(new DisplaySessionTests.MovingBar(3), options, new LinkAuth(pc, TestPairing.PcName, gate));
        var sw = Stopwatch.StartNew();

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.ConnectFailed, r.End);
        Assert.InRange(sw.ElapsedMilliseconds, 550, 2500);
        Assert.True(tab5.WaitEnd(), "假 Tab5 那边的连接没有结束");
        Assert.Equal(0, gate.WrongCount);               // 没等到完整的 PAIR_REPLY：不算密码不对
        Assert.Equal(pairing ? 0 : 1, pc.Peers.Count);  // 原来的配对不动，没配完的不存
    }

    [Theory]
    [InlineData(nameof(Fault.AuthUnknownForPair))]
    [InlineData(nameof(Fault.PairReplyArg))]
    [InlineData(nameof(Fault.PairReplyOffCurve))]
    [InlineData(nameof(Fault.PairReplyKInfinity))]
    [InlineData(nameof(Fault.AuthUnknownAfterPair))]
    public void 配对时Tab5发来的东西不合协议_断开且什么都不再发_不存_不算密码不对(string fault)
    {
        PairStore pc = TestPairing.SenderStore(paired: false);
        PairingGate gate = Gate();
        using var tab5 = new FakeTab5 { Store = TestPairing.Tab5Store(paired: false), AskPassword = FakeTab5.Answers(Password), Fault = Enum.Parse<Fault>(fault) };

        var (r, _) = Run(tab5, new LinkAuth(pc, TestPairing.PcName, gate));

        Assert.Equal(SessionEnd.ProtocolError, r.End);
        Assert.Equal(0, r.FramesSent);
        AssertHungUpSilently(tab5);
        Assert.Empty(pc.Peers);
        Assert.Equal(0, gate.WrongCount);               // 这不是「密码不对」
    }

    [Fact]
    public void 配对完紧跟的AUTH之后Tab5上的人离开了页面_收到明文的BYE_报DeviceBye_两边都不存()
    {
        PairStore pc = TestPairing.SenderStore(paired: false), dev = TestPairing.Tab5Store(paired: false);
        PairingGate gate = Gate();
        using var tab5 = new FakeTab5 { Store = dev, AskPassword = FakeTab5.Answers(Password), Fault = Fault.ByeAfterPairOk };

        var (r, steps) = Run(tab5, new LinkAuth(pc, TestPairing.PcName, gate));

        Assert.Equal(SessionEnd.DeviceBye, r.End);
        Assert.Equal(("closed", 0), (tab5.Hangup, tab5.BytesAfterHangup));
        Assert.Equal([PairingStep.WaitingForPassword, PairingStep.PasswordAccepted], steps);
        Assert.Empty(pc.Peers);
        Assert.Empty(dev.Peers);
        Assert.Equal(0, gate.WrongCount);
    }

    [Fact]
    public void 已经停止配对_配对过的Tab5回AUTH_UNKNOWN_断开不发PAIR_报出来是停止配对了()
    {
        PairStore pc = TestPairing.SenderStore();
        var gate = new PairingGate(PairingSecret.FromPassword(Password), wrongCount: PairingGate.MaxWrong);    // 上次记满之后没人恢复过
        using var tab5 = new FakeTab5 { Store = TestPairing.Tab5Store(paired: false), AskPassword = FakeTab5.Answers(Password) };

        var (r, steps) = Run(tab5, new LinkAuth(pc, TestPairing.PcName, gate));

        Assert.Equal(SessionEnd.AuthFailed, r.End);
        Assert.Equal(AuthFailure.Forgotten, r.AuthFailure);
        Assert.Contains("停止配对", r.Detail);
        Assert.Equal([Proto.TAuth], tab5.Handshake);
        Assert.Equal(0, tab5.PromptCount);
        Assert.Equal("eof", tab5.EndedBy);
        Assert.Empty(steps);
        Assert.Equal(TestPairing.Key, pc.FindKey(TestPairing.Tab5Id));
        Assert.True(gate.Stopped);
    }

    [Theory]
    [InlineData(true)]      // 改了配对密码（= 清掉所有配对）
    [InlineData(false)]     // 在已配对的设备里移除了这一台
    public void 等人在Tab5上输密码的时候电脑上清掉了配对_这次配出来的不存_发BYE结束(bool changedPassword)
    {
        // 托盘程序这时还会取消正连着的会话；这里看的是来不及取消的那种：握手照样走完了
        PairStore pc = TestPairing.SenderStore(paired: !changedPassword), dev = TestPairing.Tab5Store(paired: false);
        PairingGate gate = Gate();
        using var tab5 = new FakeTab5 { Store = dev, AskPassword = FakeTab5.Answers(Password) };
        var session = new DisplaySession(new DisplaySessionTests.MovingBar(3), Options, new LinkAuth(pc, TestPairing.PcName, gate));
        session.Pairing += step =>
        {
            if (step != PairingStep.WaitingForPassword) return;
            if (changedPassword)
            {
                gate.SetPassword(PairingSecret.FromPassword("new password"));
                pc.ForgetAll();
            }
            else pc.Forget(TestPairing.Tab5Id);
        };

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.AuthFailed, r.End);
        Assert.Equal(AuthFailure.Revoked, r.AuthFailure);
        Assert.Equal(0, r.FramesSent);
        Assert.True(tab5.WaitEnd(), "假 Tab5 那边的连接没有结束");
        Assert.Null(tab5.Violation);
        Assert.Equal("bye", tab5.EndedBy);              // 握手已经走完：照协议发了 BYE 才断开
        Assert.Empty(pc.Peers);                         // 用旧密码配的没存进来 / 刚移除的没被写回去
        Assert.True(gate.CanPair);
        Assert.Equal(0, gate.WrongCount);
    }

    [Fact]
    public void 握手走完了配对却存不进文件_Run把这个错抛出来_不算配对成功_下次连接重新配对()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wdhs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 拿「包一层」的钩子顶替写不进去的磁盘（托盘程序里这一步是 DPAPI）
            bool diskFull = false;
            var pc = new PairStore(Path.Combine(dir, "pairings.json"), plain => diskFull ? throw new IOException("磁盘满了") : plain, raw => raw);
            PairStore dev = TestPairing.Tab5Store(paired: false);
            PairingGate gate = Gate();
            var auth = new LinkAuth(pc, TestPairing.PcName, gate);
            diskFull = true;
            using (var tab5 = new FakeTab5 { Store = dev, AskPassword = FakeTab5.Answers("8821", Password) })
            {
                var session = new DisplaySession(new DisplaySessionTests.MovingBar(3), Options, auth);

                var e = Assert.Throws<PairStoreException>(() => session.Run("127.0.0.1", tab5.Port, TestLimit.Session()));

                Assert.Equal("磁盘满了", e.Message);            // 认得出是配对存不进文件，没有被当成「没连上」
                Assert.IsType<IOException>(e.InnerException);   // 原来那个异常在里面
                Assert.True(tab5.WaitEnd(), "假 Tab5 那边的连接没有结束");
                Assert.Null(tab5.Violation);
                Assert.Equal("bye", tab5.EndedBy);              // 握手已经走完：照协议发了 BYE 才断开
                Assert.Equal(0, tab5.Frames);
            }
            Assert.Empty(pc.Peers);                             // 没存进文件的，内存里也不算
            Assert.Equal(1, gate.WrongCount);                   // 不算配对成功：前面输错的那一次还记着
            Assert.Single(dev.Peers);                           // Tab5 那边已经存了——只存了一边

            diskFull = false;                                   // 腾出了空间再连：Tab5 上再输一次密码，两边换成同一把新的
            using var again = new FakeTab5 { Store = dev, AskPassword = FakeTab5.Answers(Password) };
            var (r, _) = Run(again, auth);

            Assert.Equal(SessionEnd.Completed, r.End);
            Assert.Null(again.Violation);
            Assert.Equal([Proto.TPair, Proto.TPairOk, Proto.TAuth, Proto.TReady], again.Handshake);
            Assert.Equal([new PasswordPrompt(TestPairing.PcName, Wrong: false, Known: true)], again.Prompts);
            Assert.Equal(pc.FindKey(TestPairing.Tab5Id), dev.FindKey(pc.Id));
            Assert.NotNull(pc.FindKey(TestPairing.Tab5Id));
            Assert.Equal(0, gate.WrongCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 密码不对时存次数出了错_次数照记_Run把这个错抛出来_连接断开()
    {
        // 托盘程序在 WrongCountChanged 里把次数存盘，磁盘满了
        PairingGate gate = Gate();
        gate.WrongCountChanged += _ => throw new IOException("磁盘满了");
        using var tab5 = new FakeTab5 { Store = TestPairing.Tab5Store(paired: false), AskPassword = _ => "0000" };
        var session = new DisplaySession(new DisplaySessionTests.MovingBar(3), Options,
                                         new LinkAuth(TestPairing.SenderStore(paired: false), TestPairing.PcName, gate));

        var e = Assert.Throws<IOException>(() => session.Run("127.0.0.1", tab5.Port, TestLimit.Session()));

        Assert.Equal("磁盘满了", e.Message);                    // 没有被当成「没连上」
        Assert.Equal(1, gate.WrongCount);
        Assert.True(tab5.WaitEnd(), "假 Tab5 那边的连接没有结束");
        Assert.Equal(1, tab5.PromptCount);                      // 没有接着发下一个 PAIR
        Assert.Equal("eof", tab5.EndedBy);                      // 握手还没走完，按协议不能发 BYE：直接断开
        Assert.Null(tab5.Violation);
    }

    [Fact]
    public void 配对进度的回调里抛了异常_Run把它抛出来_连接断开_Tab5那边的密码框跟着关掉()
    {
        PairStore pc = TestPairing.SenderStore(paired: false);
        PairingGate gate = Gate();
        using var tab5 = new FakeTab5
        {
            Store = TestPairing.Tab5Store(paired: false), AskPassword = FakeTab5.Answers(Password), PasswordDelay = TimeSpan.FromSeconds(30),
        };
        var session = new DisplaySession(new DisplaySessionTests.MovingBar(3), Options, new LinkAuth(pc, TestPairing.PcName, gate));
        session.Pairing += _ => throw new IOException("日志写不进去");

        var e = Assert.Throws<IOException>(() => session.Run("127.0.0.1", tab5.Port, TestLimit.Session()));

        Assert.Equal("日志写不进去", e.Message);
        Assert.True(tab5.WaitEnd(2000), "连接断了，Tab5 那边的密码框却没关");
        Assert.Equal("eof", tab5.EndedBy);
        Assert.Null(tab5.Violation);
        Assert.Equal(0, gate.WrongCount);                       // 占着的那一次机会还回去了，没有记成密码不对
        Assert.True(gate.CanPair);
        Assert.Empty(pc.Peers);
    }

    /// <summary>握手之后 Tab5 → 电脑方向的一个报文被动了什么手脚。</summary>
    public enum Tamper
    {
        Tag,            // 认证码改了一个比特
        Body,           // 密文改了一个比特
        Header,         // 报文头改了（触点数 1 改成 0），认证码没变
        Flags,          // flags 不是 0
        Replay,         // 同一个报文发两遍
        Swapped,        // 两个报文换了先后
        Plaintext,      // 明文的报文（后面补 16 个 0 当认证码）
    }

    [Theory]
    [InlineData(Tamper.Tag)]
    [InlineData(Tamper.Body)]
    [InlineData(Tamper.Header)]
    [InlineData(Tamper.Flags)]
    [InlineData(Tamper.Replay)]
    [InlineData(Tamper.Swapped)]
    [InlineData(Tamper.Plaintext)]
    public void 握手之后收到的报文认证不过_断开_报ProtocolError_里面的内容不交给上层(Tamper how)
    {
        byte[] touch = FakeTab5.Touch((0, 640, 360)), moved = FakeTab5.Touch((0, 650, 362));
        using var tab5 = new FakeTab5
        {
            AfterFrame = 2,
            Then = c =>
            {
                byte[] m = c.Seal(Proto.TTouch, 1, touch);
                switch (how)
                {
                    case Tamper.Tag: m[^1] ^= 0x01; break;
                    case Tamper.Body: m[Proto.HeaderSize] ^= 0x01; break;
                    case Tamper.Header: m[2] = 0; break;
                    case Tamper.Flags: m[1] = 1; break;
                    case Tamper.Replay: m = [.. m, .. m]; break;
                    case Tamper.Swapped: m = [.. c.Seal(Proto.TTouch, 1, moved), .. m]; break;
                    case Tamper.Plaintext: m = [.. Proto.Pack(Proto.TTouch, 1, touch), .. new byte[Proto.TagSize]]; break;
                }
                c.Socket.Send(m);
            },
        };
        var session = new DisplaySession(new DisplaySessionTests.MovingBar(long.MaxValue, fps: 50), Options, TestPairing.SenderAuth());
        int touches = 0;
        session.Touch += _ => Interlocked.Increment(ref touches);

        SessionResult r = session.Run("127.0.0.1", tab5.Port, TestLimit.Session());

        Assert.Equal(SessionEnd.ProtocolError, r.End);
        Assert.Contains(how == Tamper.Flags ? "flags" : "认证码", r.Detail);
        // 重放的那一个：第一遍是真的，照常交给上层；第二遍不交。别的几种一个都不交
        Assert.Equal(how == Tamper.Replay ? 1 : 0, Volatile.Read(ref touches));
        Assert.True(tab5.WaitEnd());
        Assert.DoesNotContain(tab5.Received, x => x.Type == Proto.TBye);       // 对面不守协议：直接断开，不发 BYE
    }
}
