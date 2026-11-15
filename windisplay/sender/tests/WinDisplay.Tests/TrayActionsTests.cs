using System.Net;
using System.Text.Json;
using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>
/// 人在设置窗口里点了一行之后：该问的问了没有（问的是什么话）、答「取消」时是不是什么都没动、答「确定」之后做成了什么；
/// 存不进去的时候告诉人、程序照常。点的那一行都是照 <see cref="TrayView"/> 当时排出来的取的，和界面上走的是同一条路；
/// 对话框换成照写好的回答的，别的（常驻的发送端、配对存储、设置文件）都是真的。
/// </summary>
public sealed class TrayActionsTests : IDisposable
{
    private const string OldPassword = "2468", NewPassword = "tab5 @ study-2026";
    private static readonly byte[] LivingId = [.. Enumerable.Range(1, Proto.IdSize).Select(i => (byte)i)];
    private static readonly byte[] StudyId = [.. Enumerable.Range(0x40, Proto.IdSize).Select(i => (byte)i)];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wdtray-" + Guid.NewGuid().ToString("N"));

    public TrayActionsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>照写好的回答的对话框；问过什么按先后记着。</summary>
    private sealed class Prompts : ITrayPrompts
    {
        public string? Password;        // 输密码的框里填什么；null = 点了取消
        public bool Yes;                // 确认框点的是不是确认
        public readonly List<string> Asked = [];

        public bool Confirm(string text, string verb)
        {
            Asked.Add($"确认［{verb}］{text}");
            return Yes;
        }

        public string? AskPassword()
        {
            Asked.Add("输密码");
            return Password;
        }

        public void Info(string text) => Asked.Add("看：" + text);
    }

    /// <summary>一台电脑上的托盘程序，没有界面的那部分：存储、设置文件、常驻的发送端、点一行的处理。</summary>
    private sealed class Pc : IDisposable
    {
        public readonly Prompts Prompts = new();
        public readonly List<string> Log = [];
        public readonly PairStore Store = PairStore.InMemory(TestPairing.PcId);
        public readonly string SettingsFile;
        public readonly PairingGate Gate;
        public readonly SenderHost Host;
        public readonly TrayActions Actions;
        public bool AutoStart;
        public Exception? AutoStartFails;       // 不是 null = 写注册表时抛这个
        public bool DiskFull;                   // true = 从现在起设置文件存不进去

        /// <param name="password">已经设着的密码；null = 还没设过。</param>
        /// <param name="wrong">密码已经连着不对了几次。</param>
        /// <param name="tab5">局域网里开着「无线显示」页的那台 Tab5（有它，Start 之后就会去连）。</param>
        /// <param name="settingsFile">设置文件放哪；不给 = 测试目录里的 host.bin。</param>
        public Pc(string dir, string? password = null, int wrong = 0, DiscoveredTab5? tab5 = null, string? settingsFile = null)
        {
            SettingsFile = settingsFile ?? Path.Combine(dir, "host.bin");
            if (password is not null)
            {
                var seed = new HostSettings(SettingsFile);
                seed.SaveSecret(PairingSecret.FromPassword(password));
                PairingGate counting = seed.CreateGate();
                for (int i = 0; i < wrong; i++)         // 之前有人在 Tab5 上输错过这么多次（每次都记进了文件）
                {
                    Assert.NotNull(counting.Begin());
                    counting.WrongPassword();
                }
            }
            // 存盘前后各过一道原样给回去的「保护」：文件里还是 JSON，只是 DiskFull 之后这一道会抛异常，像磁盘满了
            var settings = new HostSettings(SettingsFile, data => DiskFull ? throw new IOException("磁盘空间不足") : data, data => data);
            Gate = settings.CreateGate();
            Host = new SenderHost(Store, Gate, settings, TestPairing.PcName, new Lan(tab5),
                                  () => throw new CaptureLostException("这个会话里没有桌面"),
                                  wait: (_, ct) => ct.WaitHandle.WaitOne());       // 出了错之后不再试：状态停在那里给测试看
            Actions = new TrayActions(Host, Prompts, on =>
            {
                if (AutoStartFails is not null) throw AutoStartFails;
                AutoStart = on;
            }, Log.Add);
        }

        private sealed class Lan(DiscoveredTab5? tab5) : IDiscovery
        {
            public IReadOnlyList<DiscoveredTab5> Query(CancellationToken ct) => tab5 is null ? [] : [tab5];
        }

        /// <summary>设置窗口现在排出来的那一行（标题在哪一组里）。</summary>
        public SettingsRow Row(string group, string title) =>
            TrayView.Build(Host.Status, Host.HasPassword, Host.PairingStopped, Host.Peers, AutoStart)
                    .Single(g => g.Title == group).Rows.Single(r => r.Title == title);

        public void Click(string group, string title) => Actions.Run(Row(group, title));

        /// <summary>设置文件里存着的是不是这个密码（存的是密码算出来的数据）。</summary>
        public bool SavedPasswordIs(string password)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(SettingsFile));
            return doc.RootElement.GetProperty("w0").GetString() == Convert.ToHexStringLower(PairingSecret.FromPassword(password).W0);
        }

        /// <summary>现在拿去和 Tab5 配对的是不是这个密码（内存里的那一份）。</summary>
        public bool PairsWith(string password)
        {
            PairingSecret? inUse = Gate.Begin();
            if (inUse is null) return false;
            Gate.End();
            return inUse.W0.AsSpan().SequenceEqual(PairingSecret.FromPassword(password).W0);
        }

        public void Dispose() => Host.Dispose();
    }

    private Pc TwoPaired(string password = OldPassword)
    {
        var pc = new Pc(_dir, password);
        pc.Store.Save(LivingId, TestPairing.Key, "Tab5-46C3");
        pc.Store.Save(StudyId, TestPairing.Key, "书房的 Tab5");
        return pc;
    }

    [Fact]
    public void 头一次设密码_没有配对过的设备_不用确认_设上并存盘()
    {
        using var pc = new Pc(_dir);
        Assert.Equal("未设置", pc.Row("配对", "配对密码").Value);
        pc.Prompts.Password = OldPassword;
        pc.Click("配对", "配对密码");

        Assert.Equal(["输密码"], pc.Prompts.Asked);
        Assert.True(pc.Host.HasPassword);
        Assert.Equal("已设置", pc.Row("配对", "配对密码").Value);
        Assert.True(pc.SavedPasswordIs(OldPassword));
        Assert.Empty(pc.Log);
    }

    [Fact]
    public void 改密码_有配对过的设备_输完新密码要确认_确认了设备全部移除_存的是新密码()
    {
        using Pc pc = TwoPaired();
        pc.Prompts.Password = NewPassword;
        pc.Prompts.Yes = true;
        pc.Click("配对", "配对密码");

        Assert.Equal(["输密码", "确认［更改］更改密码会移除 2 台已配对的设备。"], pc.Prompts.Asked);
        Assert.Empty(pc.Host.Peers);
        Assert.True(pc.SavedPasswordIs(NewPassword));
        Assert.Equal("已设置", pc.Row("配对", "配对密码").Value);
    }

    [Fact]
    public void 改密码_输密码时取消_或者确认时取消_设备和密码都没动()
    {
        using Pc pc = TwoPaired();
        pc.Prompts.Password = null;                 // 输密码的框点了取消：后面那个确认框不该再出来
        pc.Click("配对", "配对密码");
        Assert.Equal(["输密码"], pc.Prompts.Asked);

        pc.Prompts.Password = NewPassword;          // 输了新密码，看到「会移除 2 台」之后点了取消
        pc.Prompts.Yes = false;
        pc.Click("配对", "配对密码");
        Assert.Equal(["输密码", "输密码", "确认［更改］更改密码会移除 2 台已配对的设备。"], pc.Prompts.Asked);

        Assert.Equal(["Tab5-46C3", "书房的 Tab5"], pc.Host.Peers.Select(p => p.Name).Order(StringComparer.Ordinal));     // 按码点排：照语言排的话各台电脑上先后不一样
        Assert.True(pc.SavedPasswordIs(OldPassword));
    }

    [Fact]
    public void 移除一台设备_要确认_取消了还在_确认了只移除这一台()
    {
        using Pc pc = TwoPaired();
        pc.Prompts.Yes = false;
        pc.Click("已配对的设备", "书房的 Tab5");
        Assert.Equal(["确认［移除］移除「书房的 Tab5」？"], pc.Prompts.Asked);
        Assert.Equal(2, pc.Host.Peers.Count);

        pc.Prompts.Yes = true;
        pc.Click("已配对的设备", "书房的 Tab5");
        Assert.Equal(["Tab5-46C3"], pc.Host.Peers.Select(p => p.Name));
        Assert.Null(pc.Store.FindKey(StudyId));
        Assert.NotNull(pc.Store.FindKey(LivingId));
        Assert.True(pc.SavedPasswordIs(OldPassword));           // 密码不受影响
    }

    [Fact]
    public void 密码连着不对停了配对_点一下就恢复_不问也不动配对过的设备()
    {
        using var pc = new Pc(_dir, OldPassword, wrong: PairingGate.MaxWrong);
        pc.Store.Save(LivingId, TestPairing.Key, "Tab5-46C3");
        Assert.True(pc.Host.PairingStopped);
        Assert.Equal(new SettingsRow("配对密码", "已停用", RowTone.Alert, RowAction.ResumePairing), pc.Row("配对", "配对密码"));

        pc.Click("配对", "配对密码");
        Assert.False(pc.Host.PairingStopped);
        Assert.Empty(pc.Prompts.Asked);
        Assert.Equal("已设置", pc.Row("配对", "配对密码").Value);
        Assert.Single(pc.Host.Peers);
        Assert.False(new HostSettings(pc.SettingsFile).CreateGate().Stopped);       // 存了盘：程序重启之后也是恢复了的
    }

    [Fact]
    public void 开机启动_点一下开_再点一下关_不问()
    {
        using var pc = new Pc(_dir, OldPassword);
        Assert.Equal("关", pc.Row("通用", "开机启动").Value);
        pc.Click("通用", "开机启动");
        Assert.True(pc.AutoStart);
        Assert.Equal("开", pc.Row("通用", "开机启动").Value);
        pc.Click("通用", "开机启动");
        Assert.False(pc.AutoStart);
        Assert.Empty(pc.Prompts.Asked);
    }

    [Fact]
    public void 开机启动写不进注册表_告诉人没成和原因_开关还是原来的样子()
    {
        using var pc = new Pc(_dir, OldPassword) { AutoStartFails = new UnauthorizedAccessException("对注册表项的访问被拒绝") };
        pc.Click("通用", "开机启动");
        Assert.Equal(["看：没存上：对注册表项的访问被拒绝"], pc.Prompts.Asked);
        Assert.Equal(["没存上：对注册表项的访问被拒绝"], pc.Log);
        Assert.Equal("关", pc.Row("通用", "开机启动").Value);
    }

    [Fact]
    public void 密码存不进文件_告诉人没成_程序照常_再点一次还能接着设()
    {
        string blocker = Path.Combine(_dir, "占着这个名字");
        File.WriteAllText(blocker, "");
        using var pc = new Pc(_dir, settingsFile: Path.Combine(blocker, "host.bin"));       // 上一级是个文件：存不进去
        pc.Prompts.Password = OldPassword;
        pc.Click("配对", "配对密码");

        string told = Assert.Single(pc.Prompts.Asked, a => a.StartsWith("看："));
        Assert.StartsWith("看：没存上：", told);
        Assert.Equal(told["看：".Length..], Assert.Single(pc.Log));
        // 没存上就是没设上：那一行不能变成「已设置」（那样人以为设好了，程序一重启又没了）
        Assert.False(pc.Host.HasPassword);
        Assert.Equal(new SettingsRow("配对密码", "未设置", RowTone.Alert, RowAction.SetPassword), pc.Row("配对", "配对密码"));
        pc.Click("配对", "配对密码");                 // 没有抛出来，下一次点照常弹框
        Assert.Equal(2, pc.Prompts.Asked.Count(a => a == "输密码"));
    }

    [Fact]
    public void 改密码_新密码存不进文件_告诉人没成_用的还是原来的密码_磁盘好了再改一次就成()
    {
        using Pc pc = TwoPaired();
        pc.DiskFull = true;
        pc.Prompts.Password = NewPassword;
        pc.Prompts.Yes = true;
        pc.Click("配对", "配对密码");

        Assert.Equal("看：没存上：磁盘空间不足", pc.Prompts.Asked[^1]);
        Assert.True(pc.SavedPasswordIs(OldPassword));
        Assert.True(pc.PairsWith(OldPassword));     // 内存里和文件里一致：不然这次运行里按新密码配对，重启之后又变回旧的
        Assert.Equal("已设置", pc.Row("配对", "配对密码").Value);
        Assert.Empty(pc.Host.Peers);                // 配对是先清掉的（人确认过）：这一步已经做了

        pc.DiskFull = false;
        pc.Click("配对", "配对密码");               // 没有配对过的设备了：不再问确认
        Assert.Equal("输密码", pc.Prompts.Asked[^1]);
        Assert.True(pc.SavedPasswordIs(NewPassword));
        Assert.True(pc.PairsWith(NewPassword));
    }

    [Fact]
    public void 输错过几次之后改密码_新密码存不进文件_次数不清零_文件里也还是原来的次数()
    {
        // 改密码没改成，不能顺带把次数清零：没人点过恢复，原来的密码却又有 10 次可以试
        using var pc = new Pc(_dir, OldPassword, wrong: 3);
        pc.Store.Save(LivingId, TestPairing.Key, "Tab5-46C3");
        pc.DiskFull = true;
        pc.Prompts.Password = NewPassword;
        pc.Prompts.Yes = true;
        pc.Click("配对", "配对密码");

        Assert.Equal("看：没存上：磁盘空间不足", pc.Prompts.Asked[^1]);
        Assert.Equal(3, pc.Gate.WrongCount);
        Assert.True(pc.SavedPasswordIs(OldPassword));
        Assert.Equal(3, new HostSettings(pc.SettingsFile).CreateGate().WrongCount);     // 程序重启之后读回来的
        Assert.True(pc.PairsWith(OldPassword));
    }

    [Fact]
    public void 停了配对_点恢复时存不进文件_告诉人没成_还停着_磁盘好了再点就恢复()
    {
        using var pc = new Pc(_dir, OldPassword, wrong: PairingGate.MaxWrong);
        pc.DiskFull = true;
        pc.Click("配对", "配对密码");

        Assert.Equal(["看：没存上：磁盘空间不足"], pc.Prompts.Asked);
        Assert.Equal(["没存上：磁盘空间不足"], pc.Log);
        Assert.True(pc.Host.PairingStopped);
        Assert.Equal(new SettingsRow("配对密码", "已停用", RowTone.Alert, RowAction.ResumePairing), pc.Row("配对", "配对密码"));
        Assert.True(new HostSettings(pc.SettingsFile).CreateGate().Stopped);        // 文件里也还是停着的

        pc.DiskFull = false;
        pc.Click("配对", "配对密码");
        Assert.False(pc.Host.PairingStopped);
        Assert.False(new HostSettings(pc.SettingsFile).CreateGate().Stopped);
    }

    [Fact]
    public void 出了错_状态行点开看到原因()
    {
        // 配对过的 Tab5 开着页面，这台电脑却抓不了屏：连不成，状态是出错，原因在里面
        using var pc = new Pc(_dir, OldPassword, tab5: new DiscoveredTab5(IPAddress.Loopback, 7250, LivingId));
        pc.Store.Save(LivingId, TestPairing.Key, "Tab5-46C3");
        pc.Host.Start();
        Assert.True(SpinWait.SpinUntil(() => pc.Host.Status.State == HostState.Faulted, TestLimit.Default));
        Assert.Equal(new SettingsRow("状态", "出错", RowTone.Alert, RowAction.ShowFault), pc.Row("连接", "状态"));

        pc.Click("连接", "状态");
        Assert.Equal(["看：这个会话里没有桌面"], pc.Prompts.Asked);
    }

    [Fact]
    public void 点下去的时候已经不出错了_什么都不弹()
    {
        using var pc = new Pc(_dir, OldPassword);
        // 窗口上那一行还是出错时画的（状态过一会儿自己再试，好了之后界面晚一拍才重画）
        pc.Actions.Run(new SettingsRow("状态", "出错", RowTone.Alert, RowAction.ShowFault));
        Assert.Empty(pc.Prompts.Asked);
    }
}
