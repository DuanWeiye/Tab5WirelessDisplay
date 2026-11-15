using System.Net;
using System.Text.Json;
using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>
/// 常驻的发送端（托盘程序的内核）：Tab5 开着页面就连上去显示，状态报得对不对；设密码 / 改密码 / 恢复配对 / 移除配对之后该连的连、该断的断；
/// 这台电脑这边出了错不停、过一会儿再试；Tab5 发来的输入交给注入的一方，连接结束时按着的键松开、按着的触点作废。
/// 对着本机回环上的假 Tab5；「等一会儿」都缩成 10 毫秒。
/// </summary>
public sealed class SenderHostTests : IDisposable
{
    private const string Password = "2468";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wdhost-" + Guid.NewGuid().ToString("N"));

    public SenderHostTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>画面来源：一直有新画面（像屏幕上有东西在动）。记着开过几次、关过几次。</summary>
    private sealed class Screens
    {
        public int Opened, Closed;
        public int FailFirst;               // 头这么多次打不开
        public long BreakAtFrame = -1;      // 第一次打开的那个，取到第几幅时出错（-1 = 不出错）
        public bool BreakEvery;             // true = 不光第一次，每次打开的都在那一幅出错

        private sealed class One(Screens owner, IFrameSource inner) : IFrameSource, IDisposable
        {
            public FrameResult TryGetFrame(Span<byte> dst, TimeSpan timeout) => inner.TryGetFrame(dst, timeout);
            public void Dispose() => Interlocked.Increment(ref owner.Closed);
        }

        public IFrameSource Open()
        {
            int n = Interlocked.Increment(ref Opened);
            if (n <= FailFirst) throw new CaptureLostException("这个会话里没有桌面");
            return new One(this, new DisplaySessionTests.MovingBar(long.MaxValue, fps: 100, failAt: n == 1 || BreakEvery ? BreakAtFrame : -1));
        }
    }

    private sealed class Sink : IInputSink
    {
        public readonly List<KeyAction> KeyLog = [];
        public readonly List<Contact> Contacts = [];
        public readonly List<MouseAction> MouseLog = [];

        public void Keys(IReadOnlyList<KeyAction> actions)
        {
            lock (KeyLog) KeyLog.AddRange(actions);
        }

        public bool Touch(IReadOnlyList<Contact> frame)
        {
            lock (Contacts) Contacts.AddRange(frame);
            return true;
        }

        public void Mouse(IReadOnlyList<MouseAction> actions)
        {
            lock (MouseLog) MouseLog.AddRange(actions);
        }
    }

    /// <summary>一台电脑：存储、密码、常驻的发送端；局域网里依次出现的假 Tab5（一台只接一条连接，接过的就不再应答，像人退出之后的 Tab5）。</summary>
    private sealed class Pc : IDisposable
    {
        private readonly List<FakeTab5> _tab5s = [];
        private readonly Dictionary<FakeTab5, byte[]> _announced = [];      // 应答自动发现时报的编号和 HELLO 里不一样的那几台
        private readonly List<HostStatus> _seen = [];
        public readonly Screens Screens = new();
        public readonly Sink Sink = new();
        public readonly PairStore Store;
        public readonly PairingGate Gate;
        public readonly SenderHost Host;
        private readonly InputPump? _pump;

        /// <param name="store">配对存在哪；不给 = 只放在内存里的那一种（paired 说里面有没有假 Tab5 的配对）。</param>
        public Pc(bool paired, PairingGate? gate = null, HostSettings? settings = null, bool input = false, PairStore? store = null)
        {
            Store = store ?? TestPairing.SenderStore(paired);
            Gate = gate ?? settings?.CreateGate() ?? new PairingGate();
            if (input)
            {
                // 连发的延迟给得很长：这里的测试都不看连发，机器忙的时候线程被拖慢也不会多出几个连发的键
                _pump = new InputPump(new InputBridge(Sink, () => ViewFit.For(1920, 1080), () => new KeyRepeat(5000, 33), 100));
                _pump.Start();
            }
            Host = new SenderHost(Store, Gate, settings, TestPairing.PcName, new Lan(this), Screens.Open, _pump,
                                  wait: (_, ct) => ct.WaitHandle.WaitOne(10));
            Host.Changed += () =>
            {
                lock (_seen)
                    if (_seen.Count == 0 || _seen[^1] != Host.Status) _seen.Add(Host.Status);
            };
        }

        private sealed class Lan(Pc pc) : IDiscovery
        {
            public IReadOnlyList<DiscoveredTab5> Query(CancellationToken ct)
            {
                lock (pc._tab5s)
                    return [.. pc._tab5s.Where(t => !t.Accepted).Take(1).Select(t => new DiscoveredTab5(IPAddress.Loopback, t.Port, pc._announced.GetValueOrDefault(t, t.Store.Id)))];
            }
        }

        /// <summary>局域网里多了一台开着「无线显示」页的 Tab5。</summary>
        /// <param name="announce">它应答自动发现时报的编号；不给 = 就是它 HELLO 里报的那个（守协议的 Tab5 都是这样）。</param>
        public FakeTab5 Appears(FakeTab5 tab5, byte[]? announce = null)
        {
            lock (_tab5s)
            {
                if (announce is not null) _announced[tab5] = announce;
                _tab5s.Add(tab5);
            }
            return tab5;
        }

        /// <summary>到现在为止经过的状态，按先后（相邻重复的只算一次）。</summary>
        public List<HostState> States
        {
            get
            {
                lock (_seen) return [.. _seen.Select(s => s.State)];
            }
        }

        public void WaitFor(Func<bool> what, string name, int timeoutMs = 8000) =>
            Assert.True(SpinWait.SpinUntil(what, timeoutMs), $"{timeoutMs} 毫秒了还没等到：{name}；现在是 {Host.Status}，经过 {string.Join(" → ", States)}");

        public void WaitState(HostState state) => WaitFor(() => Host.Status.State == state, $"状态变成 {state}");

        /// <summary>
        /// 等到经过了这个状态，返回当时的那一份。只停留一小会儿的状态（出错之后隔 10 毫秒就再试）要用它：
        /// 看的是经过的记录；用 WaitState 等「现在是」的话可能正好错过，等到了再去读 Status 也可能已经变了。
        /// </summary>
        public HostStatus WaitSeen(HostState state)
        {
            HostStatus? hit = null;
            WaitFor(() =>
            {
                lock (_seen) hit = _seen.FirstOrDefault(s => s.State == state);
                return hit is not null;
            }, $"经过 {state}");
            return hit!;
        }

        public void Dispose()
        {
            Host.Dispose();
            _pump?.Dispose();
            lock (_tab5s)
                foreach (FakeTab5 t in _tab5s) t.Dispose();
        }
    }

    [Fact]
    public void 配对过的Tab5开着页面_连上去显示_停下来时正常收尾_画面来源每条连接开关一次()
    {
        using var pc = new Pc(paired: true);
        Assert.Equal(HostState.Stopped, pc.Host.Status.State);
        pc.Host.Start();
        pc.WaitState(HostState.Searching);
        Assert.Equal(0, pc.Screens.Opened);         // 没有 Tab5 的时候不抓屏

        FakeTab5 tab5 = pc.Appears(new FakeTab5());
        pc.WaitState(HostState.Showing);
        Assert.Equal(TestPairing.Tab5Name, pc.Host.Status.Tab5);
        pc.WaitFor(() => tab5.Frames >= 3, "Tab5 收到了几帧");

        pc.Host.Dispose();
        Assert.True(tab5.WaitEnd(), "停下来之后连接没关");
        Assert.Equal("bye", tab5.EndedBy);
        Assert.Equal(HostState.Stopped, pc.Host.Status.State);
        Assert.Equal((1, 1), (pc.Screens.Opened, pc.Screens.Closed));
        Assert.Equal([HostState.Searching, HostState.Connecting, HostState.Showing, HostState.Stopped], pc.States);
    }

    [Fact]
    public void 没设密码时不连没配对过的Tab5_设了密码就去配对_密码存了盘()
    {
        string file = Path.Combine(_dir, "host.json");
        var settings = new HostSettings(file);
        using var pc = new Pc(paired: false, settings: settings);
        FakeTab5 tab5 = pc.Appears(new FakeTab5 { Store = TestPairing.Tab5Store(paired: false), AskPassword = FakeTab5.Answers(Password) });
        pc.Host.Start();
        pc.WaitState(HostState.NeedPairing);
        Assert.False(pc.Host.HasPassword);
        Thread.Sleep(100);
        Assert.False(tab5.Accepted);                // 没设密码：一直不去连它

        pc.Host.SetPassword(Password);
        pc.WaitState(HostState.Showing);
        Assert.Contains(HostState.AwaitingPassword, pc.States);
        Assert.True(pc.Host.HasPassword);
        Assert.Equal([TestPairing.Tab5Name], pc.Host.Peers.Select(p => p.Name));

        // 重启之后：密码还在（存的是 w0，不是密码），能照它再配对
        PairingGate reloaded = new HostSettings(file).CreateGate();
        Assert.True(reloaded.HasPassword && reloaded.CanPair);
        Assert.DoesNotContain(Password, File.ReadAllText(file));
    }

    [Fact]
    public void 改密码_配对全部移除_正连着的断开_之后要在Tab5上输新密码才连得上()
    {
        var settings = new HostSettings(Path.Combine(_dir, "host.json"));
        using var pc = new Pc(paired: true, settings: settings);
        FakeTab5 first = pc.Appears(new FakeTab5());
        pc.Host.Start();
        pc.WaitState(HostState.Showing);

        pc.Host.SetPassword("new pass 2026");
        Assert.True(first.WaitEnd(), "改了密码，正连着的那条没断");
        Assert.Equal("bye", first.EndedBy);
        Assert.Empty(pc.Host.Peers);
        // 假 Tab5 看到连接结束的时候，这边的状态可能还没来得及从 Showing 变过去：等它变了再往下，不然下面等 Showing 等到的是上一条连接的
        pc.WaitFor(() => pc.Host.Status.State != HostState.Showing, "第一条连接结束后状态离开 Showing");

        // 那台 Tab5 上还留着旧的配对：它认得这台电脑，但电脑这边已经没有密钥了，要重新配对
        FakeTab5 second = pc.Appears(new FakeTab5 { AskPassword = FakeTab5.Answers("new pass 2026") });
        pc.WaitState(HostState.Showing);
        pc.WaitFor(() => second.PromptCount == 1, "Tab5 上弹过一次密码框");
        Assert.Single(pc.Host.Peers);
    }

    /// <summary>设置文件；Full 设成 true 之后就存不进去了（像磁盘满了）。</summary>
    private sealed class Disk(string file)
    {
        public bool Full;
        public int Writes;          // 存过几次（存不进去的不算）
        public readonly string File = file;

        // 存盘前后各过一道原样给回去的「保护」：文件里还是 JSON，只是 Full 之后这一道会抛异常
        public HostSettings Open() => new(File, Protect, data => data);

        public byte[] Protect(byte[] data)
        {
            if (Full) throw new IOException("磁盘空间不足");
            Writes++;
            return data;
        }

        /// <summary>文件里现在存着的：密码算出来的 w0（十六进制）、密码不对的次数。</summary>
        public (string? W0, int Wrong) Stored()
        {
            using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllText(File));
            return (doc.RootElement.GetProperty("w0").GetString(), doc.RootElement.GetProperty("wrong").GetInt32());
        }

        /// <summary>文件里先有：设着这个密码，之前有人在别的 Tab5 上输错过 wrong 次（每次都记进了文件）。</summary>
        public void Seed(string password, int wrong)
        {
            var seed = new HostSettings(File);
            seed.SaveSecret(PairingSecret.FromPassword(password));
            PairingGate counting = seed.CreateGate();
            for (int i = 0; i < wrong; i++)
            {
                Assert.NotNull(counting.Begin());
                counting.WrongPassword();
            }
        }
    }

    private static string W0Hex(string password) => Convert.ToHexStringLower(PairingSecret.FromPassword(password).W0);

    [Fact]
    public void 改密码_新密码存不进文件_报出来_密码还是原来的_Tab5上输原来的密码照常配对()
    {
        var disk = new Disk(Path.Combine(_dir, "host.json"));
        using var pc = new Pc(paired: false, settings: disk.Open());
        pc.Host.SetPassword(Password);
        disk.Full = true;

        Assert.Equal("磁盘空间不足", Assert.Throws<PairStoreException>(() => pc.Host.SetPassword("new pass 2026")).Message);
        Assert.True(pc.Host.HasPassword);

        // 内存里要是已经换成了新密码：这次运行里 Tab5 上输原来的密码对不上，程序一重启读回来的又是原来的
        FakeTab5 tab5 = pc.Appears(new FakeTab5 { Store = TestPairing.Tab5Store(paired: false), AskPassword = FakeTab5.Answers(Password) });
        pc.Host.Start();
        pc.WaitState(HostState.Showing);
        Assert.Equal(1, tab5.PromptCount);
        Assert.Equal(0, pc.Gate.WrongCount);
        Assert.Equal(PairingSecret.FromPassword(Password).W0, new HostSettings(disk.File).CreateGate().Begin()!.W0);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(PairingGate.MaxWrong)]      // 已经停止配对了的
    public void 输错过几次之后改密码_新密码存不进文件_密码和次数都还是原来的_文件里也是(int wrongBefore)
    {
        var disk = new Disk(Path.Combine(_dir, "host.json"));
        disk.Seed(Password, wrongBefore);
        using var pc = new Pc(paired: true, settings: disk.Open());
        FakeTab5 tab5 = pc.Appears(new FakeTab5());
        pc.Host.Start();
        pc.WaitState(HostState.Showing);
        disk.Full = true;

        Assert.Throws<PairStoreException>(() => pc.Host.SetPassword("new pass 2026"));

        // 改密码没改成，不能顺带把次数清零：没人点过恢复，原来的密码却又有 10 次可以试
        Assert.Equal(wrongBefore, pc.Gate.WrongCount);
        Assert.Equal(wrongBefore >= PairingGate.MaxWrong, pc.Host.PairingStopped);
        Assert.Equal((W0Hex(Password), wrongBefore), disk.Stored());        // 程序重启之后读回来的也一样
        // 清配对那一步是存上了的：配对没了，正连着的那条断开
        Assert.Empty(pc.Host.Peers);
        Assert.True(tab5.WaitEnd(), "配对清掉了，正连着的那条没断");
    }

    [Fact]
    public void 输错过几次之后改密码_新密码和清零的次数一次写进文件()
    {
        // 分两次写（先清零的次数、后新密码）的话，头一次写成、后一次没写成，文件里就是原来的密码带着清零的次数
        var disk = new Disk(Path.Combine(_dir, "host.json"));
        disk.Seed(Password, wrong: 3);
        using var pc = new Pc(paired: false, settings: disk.Open());

        pc.Host.SetPassword("new pass 2026");

        Assert.Equal(1, disk.Writes);
        Assert.Equal((W0Hex("new pass 2026"), 0), disk.Stored());
        Assert.Equal(0, pc.Gate.WrongCount);
    }

    [Fact]
    public void 改密码_清配对那一步就存不进文件_什么都没动_配对还在_正连着的不断()
    {
        // 配对存在文件里（托盘程序就是这样），这个文件到时候存不进去
        bool full = false;
        var store = new PairStore(Path.Combine(_dir, "pairs.json"), data => full ? throw new IOException("磁盘空间不足") : data, data => data);
        store.Save(TestPairing.Tab5Id, TestPairing.Key, TestPairing.Tab5Name);
        PairStore tab5Side = PairStore.InMemory(TestPairing.Tab5Id);        // 那台 Tab5 上存着的是这台电脑（编号是建文件时新取的）
        tab5Side.Save(store.Id, TestPairing.Key, TestPairing.PcName);
        var disk = new Disk(Path.Combine(_dir, "host.json"));
        disk.Seed(Password, wrong: 3);
        using var pc = new Pc(paired: true, settings: disk.Open(), store: store);
        FakeTab5 tab5 = pc.Appears(new FakeTab5 { Store = tab5Side });
        pc.Host.Start();
        pc.WaitState(HostState.Showing);
        full = true;

        Assert.Equal("磁盘空间不足", Assert.Throws<PairStoreException>(() => pc.Host.SetPassword("new pass 2026")).Message);

        Assert.Equal([TestPairing.Tab5Name], pc.Host.Peers.Select(p => p.Name));
        Assert.Equal(3, pc.Gate.WrongCount);
        Assert.Equal(0, disk.Writes);                                       // 设置文件没碰过
        Assert.Equal((W0Hex(Password), 3), disk.Stored());
        Assert.False(tab5.WaitEnd(300), "什么都没改成，正连着的那条却断了");
        Assert.Equal(HostState.Showing, pc.Host.Status.State);
    }

    [Fact]
    public void Tab5报过画质_这条连接断了再连上_第一帧就按上次报的压()
    {
        using var pc = new Pc(paired: true);
        // 第一条连接：第 1 帧确认之后 Tab5 报「画质 85、动态预算不降」（人在它上面选的）
        FakeTab5 first = pc.Appears(new FakeTab5 { AfterFrame = 1, Then = c => c.Send(Proto.TPicture, (ushort)(85 | 0 << 8)) });
        pc.Host.Start();
        pc.WaitFor(() => first.Frames >= 4, "第一条连接上收到了几帧");
        first.Dispose();                    // Tab5 那头断了（人退出了页面）
        pc.WaitFor(() => pc.Screens.Closed >= 1, "第一条连接结束");

        FakeTab5 second = pc.Appears(new FakeTab5());
        pc.WaitFor(() => second.Frames >= 1, "第二条连接上收到了第一帧");
        // 两条连接的第一帧是同一幅画面（画面来源每条连接重开）：按 85 压出来的比按默认 60（还带动态预算）压出来的大
        int a = first.FirstFrame.Sum(s => s.Jpeg.Length), b = second.FirstFrame.Sum(s => s.Jpeg.Length);
        Assert.True(b > a, $"第二条连接的第一帧 {b} 字节，没有比第一条的 {a} 字节大：没按上次报的画质压");
    }

    [Fact]
    public void Tab5报过画质的档位_这条连接断了再连上_第一帧就按那一档()
    {
        using var pc = new Pc(paired: true);
        // 第一条连接：第 1 帧确认之后 Tab5 报「最佳画质」（人在它上面选的）
        FakeTab5 first = pc.Appears(new FakeTab5
        {
            AfterFrame = 1,
            Then = c => c.Send(Proto.TPictureMode, (ushort)PictureTier.BestQuality, BitConverter.GetBytes(15270u)),
        });
        pc.Host.Start();
        pc.WaitFor(() => first.Frames >= 4, "第一条连接上收到了几帧");
        first.Dispose();
        pc.WaitFor(() => pc.Screens.Closed >= 1, "第一条连接结束");

        FakeTab5 second = pc.Appears(new FakeTab5());
        pc.WaitFor(() => second.Frames >= 1, "第二条连接上收到了第一帧");
        // 两条连接的第一帧是同一幅画面：按这一档的 90 压出来的比按默认的 60 压出来的大
        int a = first.FirstFrame.Sum(s => s.Jpeg.Length), b = second.FirstFrame.Sum(s => s.Jpeg.Length);
        Assert.True(b > a, $"第二条连接的第一帧 {b} 字节，没有比第一条的 {a} 字节大：没按上次报的档位来");
        Assert.Equal(Proto.CapPictureMode, second.AuthCaps & Proto.CapPictureMode);
    }

    [Fact]
    public void 移除正连着的那台_断开_移除别的不影响它()
    {
        using var pc = new Pc(paired: true);
        byte[] other = [.. Enumerable.Range(0x10, Proto.IdSize).Select(i => (byte)i)];
        pc.Store.Save(other, TestPairing.Key, "客厅的 Tab5");
        FakeTab5 tab5 = pc.Appears(new FakeTab5());
        pc.Host.Start();
        pc.WaitState(HostState.Showing);

        pc.Host.Forget(other);
        Thread.Sleep(200);
        Assert.Equal(HostState.Showing, pc.Host.Status.State);
        Assert.Equal([TestPairing.Tab5Name], pc.Host.Peers.Select(p => p.Name));

        pc.Host.Forget(TestPairing.Tab5Id);
        Assert.True(tab5.WaitEnd(), "移除了正连着的那台，连接没断");
        Assert.Equal("bye", tab5.EndedBy);
        Assert.Empty(pc.Host.Peers);
        pc.WaitState(HostState.Searching);
    }

    [Fact]
    public void 应答里报的是一台_连上之后报的是另一台配对过的_不认证_不显示()
    {
        // 书房、客厅两台都配对过。一台 Tab5 应答时报书房的编号，连上之后 HELLO 里是客厅的（客厅那把密钥它有）。
        // 让它连着的话，人移除「客厅的 Tab5」时电脑认不出正连着的就是它（正连着的是哪台按应答里的编号记），
        // 那条连接会接着用刚作废的密钥，画面照发、按键照收
        using var pc = new Pc(paired: true);
        byte[] livingRoom = [.. Enumerable.Range(0x10, Proto.IdSize).Select(i => (byte)i)];
        pc.Store.Save(livingRoom, TestPairing.Key, "客厅的 Tab5");
        PairStore itsStore = PairStore.InMemory(livingRoom);
        itsStore.Save(TestPairing.PcId, TestPairing.Key, TestPairing.PcName);
        FakeTab5 tab5 = pc.Appears(new FakeTab5 { Store = itsStore, Name = "客厅的 Tab5" }, announce: TestPairing.Tab5Id);
        pc.Host.Start();

        Assert.True(tab5.WaitEnd(), "编号对不上的那条连接没有断");
        Assert.Empty(tab5.Handshake);
        Assert.Equal(0, tab5.Frames);
        pc.WaitState(HostState.Searching);
        Assert.DoesNotContain(HostState.Showing, pc.States);
        Assert.Equal(2, pc.Host.Peers.Count);
    }

    [Fact]
    public void 密码连着不对停止配对之后_不再去连没配对过的_恢复之后接着配对()
    {
        using var pc = new Pc(paired: false, gate: new PairingGate(PairingSecret.FromPassword(Password), PairingGate.MaxWrong));
        FakeTab5 tab5 = pc.Appears(new FakeTab5 { Store = TestPairing.Tab5Store(paired: false), AskPassword = FakeTab5.Answers(Password) });
        pc.Host.Start();
        pc.WaitState(HostState.NeedPairing);
        Assert.True(pc.Host.PairingStopped);
        Thread.Sleep(100);
        Assert.False(tab5.Accepted);

        pc.Host.ResumePairing();
        pc.WaitState(HostState.Showing);
        Assert.False(pc.Host.PairingStopped);
    }

    [Fact]
    public void 画面来源打不开_报出原因_过一会儿再试_能打开了就连上_不算那台Tab5没连上()
    {
        using var pc = new Pc(paired: true);
        var ended = new List<SessionEnd>();
        var faults = new List<string>();
        pc.Host.ConnectionEnded += (_, r) =>
        {
            lock (ended) ended.Add(r.End);
        };
        pc.Host.Faulted += why =>
        {
            lock (faults) faults.Add(why);
        };
        pc.Screens.FailFirst = 3;
        pc.Appears(new FakeTab5());
        pc.Host.Start();
        pc.WaitState(HostState.Showing);
        Assert.Equal(4, pc.Screens.Opened);
        Assert.Equal("这个会话里没有桌面", pc.WaitSeen(HostState.Faulted).Detail);
        // 毛病在这台电脑上：不记成「那台 Tab5 没连上」（那样自动连接会把它往后排、越等越久，最长 30 秒）
        lock (ended) Assert.Empty(ended);
        // 连着三次都是同一个毛病：日志里只记一笔
        lock (faults) Assert.Equal(["这个会话里没有桌面"], faults);
    }

    [Fact]
    public void 显示出过画面之后又出同样的错_日志里另记一笔()
    {
        using var pc = new Pc(paired: true);
        var faults = new List<string>();
        pc.Host.Faulted += why =>
        {
            lock (faults) faults.Add(why);
        };
        pc.Screens.BreakAtFrame = 5;                // 每条连接都是显示到第 5 幅时抓屏出错
        pc.Screens.BreakEvery = true;
        pc.Appears(new FakeTab5());
        pc.Appears(new FakeTab5());                 // 过一会儿又连上了一条，也是显示了几幅之后出同样的错：是另一回事，要记
        pc.Host.Start();
        pc.WaitFor(() => { lock (faults) return faults.Count >= 2; }, "两条连接各出一次错、各记一笔");
        pc.WaitFor(() => pc.Screens.Closed == 2, "两条连接的画面来源都关了");
        lock (faults) Assert.Equal(["抓屏失败", "抓屏失败"], faults);
        Assert.Equal(2, pc.States.Count(s => s == HostState.Showing));
    }

    [Fact]
    public void 等人在Tab5上输密码的时候停下来_很快返回_Tab5那边的密码框跟着关掉_之后不再报变化()
    {
        using var pc = new Pc(paired: false, gate: new PairingGate(PairingSecret.FromPassword(Password)));
        int changes = 0;
        pc.Host.Changed += () => Interlocked.Increment(ref changes);
        FakeTab5 tab5 = pc.Appears(new FakeTab5
        {
            Store = TestPairing.Tab5Store(paired: false), AskPassword = FakeTab5.Answers(Password), PasswordDelay = TimeSpan.FromSeconds(30),
        });
        pc.Host.Start();
        pc.WaitState(HostState.AwaitingPassword);
        pc.WaitFor(() => tab5.PromptCount == 1, "Tab5 上的密码框弹出来");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        pc.Host.Dispose();                          // 人点了退出 / 注销：不能等到密码框自己超时
        Assert.True(watch.ElapsedMilliseconds < 3000, $"停下来花了 {watch.ElapsedMilliseconds} 毫秒");
        Assert.Equal(HostState.Stopped, pc.Host.Status.State);
        Assert.True(tab5.WaitEnd(2000), "停下来了，Tab5 那边的密码框却没关");
        Assert.Equal("eof", tab5.EndedBy);
        Assert.Equal(0, pc.Gate.WrongCount);        // 没输完不算输错

        // 停下来之后界面可能已经收摊了：不能再有回调过来
        int after = Volatile.Read(ref changes);
        Thread.Sleep(150);
        Assert.Equal(after, Volatile.Read(ref changes));
        Assert.Equal(1, pc.Screens.Closed);
    }

    [Fact]
    public void 画面来源中途出错_这条连接收干净_不停_过一会儿接着连()
    {
        using var pc = new Pc(paired: true);
        pc.Screens.BreakAtFrame = 5;
        FakeTab5 first = pc.Appears(new FakeTab5());
        pc.Host.Start();
        Assert.Equal("抓屏失败", pc.WaitSeen(HostState.Faulted).Detail);
        Assert.True(first.WaitEnd(), "出错的那条连接没关");
        Assert.Equal("bye", first.EndedBy);

        pc.Appears(new FakeTab5());
        pc.WaitState(HostState.Showing);
        Assert.Equal((2, 1), (pc.Screens.Opened, pc.Screens.Closed));
    }

    [Fact]
    public void Tab5发来的触摸和按键交给注入的一方_连接断了_按着的键松开_按着的触点作废()
    {
        using var pc = new Pc(paired: true, input: true);
        // 第 2 帧之后：手指按在画面中间，键盘上按住 Ctrl + c（没松开）；接着 Tab5 掉线了
        FakeTab5 tab5 = pc.Appears(new FakeTab5
        {
            AfterFrame = 2,
            Then = c =>
            {
                c.Touch((0, 640, 360));
                c.Send(Proto.TKey, 0, [0x01, 0, 0x06, 0, 0, 0, 0, 0]);
                Thread.Sleep(300);
                c.Socket.Close();
            },
        });
        pc.Host.Start();
        Assert.True(tab5.WaitEnd(), "假 Tab5 没走到断开那一步");
        pc.WaitState(HostState.Searching);
        pc.WaitFor(() => { lock (pc.Sink.KeyLog) return pc.Sink.KeyLog.Count >= 4; }, "按下两个、松开两个");

        lock (pc.Sink.KeyLog)
        {
            // 左 Ctrl（1D）、c（2E）：先按修饰键再按字母；断开时先松字母再松修饰键
            Assert.Equal([new KeyAction(0x1D, false, true), new KeyAction(0x2E, false, true), new KeyAction(0x2E, false, false), new KeyAction(0x1D, false, false)],
                         pc.Sink.KeyLog);
        }
        lock (pc.Sink.Contacts)
        {
            Assert.Equal(new Contact(0, 960, 540, ContactPhase.Down), pc.Sink.Contacts[0]);
            Assert.Equal(new Contact(0, 960, 540, ContactPhase.Cancel), pc.Sink.Contacts[^1]);     // 手并没有抬起：作废，不是抬起
            Assert.All(pc.Sink.Contacts.Skip(1).SkipLast(1), c => Assert.Equal(new Contact(0, 960, 540, ContactPhase.Move), c));      // 中间是按着不动的重报
        }
    }

    [Fact]
    public void Tab5上把触摸板加速关了_快滑也是手指挪多远光标挪多远()
    {
        using var pc = new Pc(paired: true, input: true);
        FakeTab5 tab5 = pc.Appears(new FakeTab5
        {
            AfterFrame = 2,
            Then = c =>
            {
                c.Send(Proto.TInputMode, 1);
                c.Send(Proto.TTouchpadGain, 10);            // 「触摸板加速」选的是「关」
                // 快滑：每步 30 像素。不加速时放大倍数和两份快照隔多久到没有关系，所以这里的结果不看时间；
                // 没按 Tab5 报的算（默认 2 倍）的话，要两份快照隔 200 毫秒以上才会一点不放大
                for (int i = 0; i <= 10; i++)
                {
                    c.Touch((0, (ushort)(100 + 30 * i), 360));
                    Thread.Sleep(16);
                }
                Thread.Sleep(60);
                c.Touch();
                Thread.Sleep(100);
                c.Send(Proto.TDevBye);
            },
        });
        pc.Host.Start();
        Assert.True(tab5.WaitEnd(), "没走到退出那一步");
        // 第 1 步挪出轻点的范围、不补，之后 9 步 × 30 × 1.5（1920×1080 的屏）
        pc.WaitFor(() => { lock (pc.Sink.MouseLog) return pc.Sink.MouseLog.Where(a => a.Kind == MouseKind.Move).Sum(a => a.X) == 9 * 45; }, "光标一共横着挪 405 像素");
        lock (pc.Sink.MouseLog) Assert.All(pc.Sink.MouseLog, a => Assert.Equal(MouseKind.Move, a.Kind));     // 没有点击
        Assert.Equal(Proto.CapTouchpadGain, tab5.AuthCaps & Proto.CapTouchpadGain);
        Assert.Equal(Proto.CapPicture, tab5.AuthCaps & Proto.CapPicture);       // 托盘的画质总是听 Tab5 的
    }

    [Fact]
    public void Tab5上开着触摸板模式_轻点注入成鼠标左键_换一台不报触摸方式的Tab5_照原样注入成触摸()
    {
        using var pc = new Pc(paired: true, input: true);
        // 第一台：人在它上面开了触摸板模式。握手走完先报一次，然后手指轻点一下，人退出
        FakeTab5 first = pc.Appears(new FakeTab5
        {
            AfterFrame = 2,
            Then = c =>
            {
                c.Send(Proto.TInputMode, 1);
                c.Touch((0, 640, 360));
                Thread.Sleep(60);
                c.Touch();
                Thread.Sleep(100);
                c.Send(Proto.TDevBye);
            },
        });
        pc.Host.Start();
        Assert.True(first.WaitEnd(), "第一台没走到退出那一步");
        pc.WaitFor(() => { lock (pc.Sink.MouseLog) return pc.Sink.MouseLog.Count >= 2; }, "左键按下、松开");
        lock (pc.Sink.MouseLog) Assert.Equal([MouseKind.LeftDown, MouseKind.LeftUp], pc.Sink.MouseLog.Select(a => a.Kind));
        lock (pc.Sink.Contacts) Assert.Empty(pc.Sink.Contacts);                     // 没有注入成触摸
        Assert.Equal(Proto.CapInputMode, first.AuthCaps & Proto.CapInputMode);

        // 第二台：老固件，不报触摸方式。上一条连接上报的不能留着
        FakeTab5 second = pc.Appears(new FakeTab5
        {
            AfterFrame = 2,
            Then = c =>
            {
                c.Touch((0, 640, 360));
                Thread.Sleep(60);
                c.Touch();
                Thread.Sleep(100);
                c.Send(Proto.TDevBye);
            },
        });
        Assert.True(second.WaitEnd(), "第二台没走到退出那一步");
        pc.WaitFor(() => { lock (pc.Sink.Contacts) return pc.Sink.Contacts.Count >= 2; }, "触点按下、抬起");
        lock (pc.Sink.Contacts)
        {
            Assert.Equal(new Contact(0, 960, 540, ContactPhase.Down), pc.Sink.Contacts[0]);
            Assert.Equal(new Contact(0, 960, 540, ContactPhase.Up), pc.Sink.Contacts[^1]);
        }
        lock (pc.Sink.MouseLog) Assert.Equal(2, pc.Sink.MouseLog.Count);            // 没有再多出鼠标动作
    }
}

/// <summary>常驻的发送端存的设置：配对密码算出来的 w0、密码不对的次数。</summary>
public sealed class HostSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wdset-" + Guid.NewGuid().ToString("N"));

    public HostSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File1 => Path.Combine(_dir, "host.json");

    /// <summary>一次配对、Tab5 上输的密码不对（握手里就是这两步）。</summary>
    private static void WrongOnce(PairingGate gate)
    {
        Assert.NotNull(gate.Begin());
        gate.WrongPassword();
    }

    [Fact]
    public void 还没有文件_没设过密码_不能配对()
    {
        PairingGate gate = new HostSettings(File1).CreateGate();
        Assert.False(gate.HasPassword);
        Assert.False(gate.CanPair);
        Assert.False(File.Exists(File1));
    }

    [Fact]
    public void 密码不对的次数跟着存_重启之后接着数_记满了重启还是停着()
    {
        var settings = new HostSettings(File1);
        PairingGate gate = settings.CreateGate();
        settings.SaveSecret(PairingSecret.FromPassword("2468"));
        gate.SetPassword(PairingSecret.FromPassword("2468"));
        for (int i = 0; i < 3; i++) WrongOnce(gate);

        gate = new HostSettings(File1).CreateGate();
        Assert.True(gate.CanPair);
        for (int i = 0; i < PairingGate.MaxWrong - 3; i++) WrongOnce(gate);
        Assert.True(gate.Stopped);

        gate = new HostSettings(File1).CreateGate();
        Assert.True(gate.Stopped);
        gate.Resume();
        Assert.True(new HostSettings(File1).CreateGate().CanPair);
    }

    [Fact]
    public void 内容交给保护函数包一层_文件里看不到明文_读回来一样()
    {
        static byte[] Flip(byte[] data) => [.. data.Select(b => (byte)(b ^ 0x5A))];
        var settings = new HostSettings(File1, Flip, Flip);
        PairingSecret secret = PairingSecret.FromPassword("correct horse");
        settings.SaveSecret(secret);
        Assert.DoesNotContain("w0", File.ReadAllText(File1));

        Assert.True(new HostSettings(File1, Flip, Flip).CreateGate().HasPassword);
        Assert.Throws<InvalidDataException>(() => new HostSettings(File1));       // 没给保护函数：读不懂，不当成没设过
    }

    [Fact]
    public void 读不懂的文件_报错_不悄悄当成没设过密码()
    {
        File.WriteAllText(File1, "{\"w0\": \"abcd\", \"wrong\": 0}");
        Assert.Throws<InvalidDataException>(() => new HostSettings(File1));
        File.WriteAllText(File1, "不是 JSON");
        Assert.Throws<InvalidDataException>(() => new HostSettings(File1));
    }

    [Fact]
    public void 存不进文件_抛出来_内存里的不算改过()
    {
        var settings = new HostSettings(Path.Combine(File1, "sub", "host.json"));     // 上一级是个文件，目录建不出来
        File.WriteAllText(File1, "占着这个名字");
        Assert.Throws<PairStoreException>(() => settings.SaveSecret(PairingSecret.FromPassword("2468")));
        Assert.False(settings.CreateGate().HasPassword);
    }
}
