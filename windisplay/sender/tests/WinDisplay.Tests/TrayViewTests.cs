using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>设置窗口里每一行显示什么、点了做什么；托盘图标什么样。照人实际会碰到的几种局面走。</summary>
public sealed class TrayViewTests
{
    private static readonly PairedPeer Living = new([.. Enumerable.Range(1, Proto.IdSize).Select(i => (byte)i)], "Tab5-46C3");
    private static readonly PairedPeer Study = new([.. Enumerable.Range(0x40, Proto.IdSize).Select(i => (byte)i)], "书房的 Tab5");

    private static SettingsRow Row(IReadOnlyList<SettingsGroup> groups, string group, string title) =>
        groups.Single(g => g.Title == group).Rows.Single(r => r.Title == title);

    [Fact]
    public void 刚装好_没设密码_没有配对过的设备()
    {
        var groups = TrayView.Build(new HostStatus(HostState.Searching), hasPassword: false, pairingStopped: false, [], autoStart: false);
        Assert.Equal(["连接", "配对", "通用"], groups.Select(g => g.Title));         // 没有设备时不出「已配对的设备」那一组
        Assert.Equal(new SettingsRow("状态", "查找中", RowTone.Busy), Row(groups, "连接", "状态"));
        Assert.Equal(new SettingsRow("配对密码", "未设置", RowTone.Alert, RowAction.SetPassword), Row(groups, "配对", "配对密码"));
        Assert.Equal(new SettingsRow("开机启动", "关", RowTone.Normal, RowAction.ToggleAutoStart, Switch: false), Row(groups, "通用", "开机启动"));
        // 窗口里有一行是红的（要人动手），图标就带红点，提示说的是那件事：没设密码之前哪台 Tab5 都配对不上
        Assert.Equal((TrayIcon.Alert, "无线显示：未设置密码"), TrayView.Tray(new HostStatus(HostState.Searching), false, false));
    }

    [Fact]
    public void Tab5开着页面_电脑还没设密码_状态行直接带人去设密码()
    {
        var status = new HostStatus(HostState.NeedPairing);
        var groups = TrayView.Build(status, hasPassword: false, pairingStopped: false, [], autoStart: true);
        Assert.Equal(new SettingsRow("状态", "未设置密码", RowTone.Alert, RowAction.SetPassword), Row(groups, "连接", "状态"));
        Assert.Equal(TrayIcon.Alert, TrayView.Tray(status, false, false).Icon);
    }

    [Fact]
    public void 等人在Tab5上输密码_连接中_都是转圈_不用人在电脑上动手()
    {
        foreach (var (state, text) in new[] { (HostState.AwaitingPassword, "等待输入密码"), (HostState.Connecting, "连接中") })
        {
            var groups = TrayView.Build(new HostStatus(state), hasPassword: true, pairingStopped: false, [], autoStart: false);
            Assert.Equal(new SettingsRow("状态", text, RowTone.Busy), Row(groups, "连接", "状态"));
            Assert.Equal((TrayIcon.Idle, "无线显示：" + text), TrayView.Tray(new HostStatus(state), true, false));
        }
    }

    [Fact]
    public void 连上了_显示设备名_配对过的设备各一行_可以移除()
    {
        var status = new HostStatus(HostState.Showing, Tab5: "Tab5-46C3");
        var groups = TrayView.Build(status, hasPassword: true, pairingStopped: false, [Living, Study], autoStart: true);
        Assert.Equal(["连接", "配对", "已配对的设备", "通用"], groups.Select(g => g.Title));
        Assert.Equal(new SettingsRow("状态", "已连接", RowTone.Good), Row(groups, "连接", "状态"));
        Assert.Equal(new SettingsRow("设备", "Tab5-46C3"), Row(groups, "连接", "设备"));
        Assert.Equal(new SettingsRow("配对密码", "已设置", RowTone.Normal, RowAction.SetPassword), Row(groups, "配对", "配对密码"));
        var devices = groups.Single(g => g.Title == "已配对的设备").Rows;
        Assert.Equal(["Tab5-46C3", "书房的 Tab5"], devices.Select(r => r.Title));
        Assert.All(devices, r => Assert.Equal(("移除", RowAction.ForgetPeer), (r.Value, r.Action)));
        Assert.Equal(Study.Id, devices[1].PeerId);
        Assert.True(Row(groups, "通用", "开机启动").Switch);
        Assert.Equal((TrayIcon.Connected, "无线显示：已连接 Tab5-46C3"), TrayView.Tray(status, true, false));
    }

    [Fact]
    public void 没连着的时候不出设备那一行()
    {
        var groups = TrayView.Build(new HostStatus(HostState.Searching), hasPassword: true, pairingStopped: false, [Living], autoStart: false);
        Assert.DoesNotContain(groups.Single(g => g.Title == "连接").Rows, r => r.Title == "设备");
    }

    [Fact]
    public void 密码连着不对停止配对了_两处都是错误色_点一下恢复()
    {
        var status = new HostStatus(HostState.NeedPairing);
        var groups = TrayView.Build(status, hasPassword: true, pairingStopped: true, [Living], autoStart: false);
        Assert.Equal(new SettingsRow("状态", "配对已停用", RowTone.Alert, RowAction.ResumePairing), Row(groups, "连接", "状态"));
        Assert.Equal(new SettingsRow("配对密码", "已停用", RowTone.Alert, RowAction.ResumePairing), Row(groups, "配对", "配对密码"));
        Assert.Equal(TrayIcon.Alert, TrayView.Tray(status, true, true).Icon);

        // 停止配对不碍着配对过的那台：它连着的时候状态照常，只有密码那一行还标着
        var showing = TrayView.Build(new HostStatus(HostState.Showing, "Tab5-46C3"), hasPassword: true, pairingStopped: true, [Living], autoStart: false);
        Assert.Equal(RowTone.Good, Row(showing, "连接", "状态").Tone);
        Assert.Equal(RowAction.ResumePairing, Row(showing, "配对", "配对密码").Action);
        // 图标跟着窗口里红的那一行走：连着也带红点（别的 Tab5 现在配对不上），提示说的是要人去办的那件事
        Assert.Equal((TrayIcon.Alert, "无线显示：配对已停用"), TrayView.Tray(new HostStatus(HostState.Showing, "Tab5-46C3"), true, true));
        Assert.Equal((TrayIcon.Alert, "无线显示：配对已停用"), TrayView.Tray(new HostStatus(HostState.Searching), true, true));
    }

    [Fact]
    public void 窗口里有红的行_图标就带红点_没有就不带()
    {
        // 所有局面过一遍：图标是不是「要人动手」，和窗口里有没有错误色的行是一回事
        foreach (HostState state in Enum.GetValues<HostState>())
            foreach (bool password in new[] { false, true })
                foreach (bool stopped in new[] { false, true })
                {
                    var status = new HostStatus(state, Tab5: "Tab5-46C3", Detail: "抓屏开不了");
                    bool red = TrayView.Build(status, password, stopped, [Living], autoStart: true).Any(g => g.Rows.Any(r => r.Tone == RowTone.Alert));
                    Assert.Equal(red, TrayView.Tray(status, password, stopped).Icon == TrayIcon.Alert);
                }
    }

    [Fact]
    public void 出了错_只显示出错两个字_原因点开才看()
    {
        var status = new HostStatus(HostState.Faulted, Detail: "这个会话里没有桌面，抓不了屏幕（WGC：0x80070005）");
        var groups = TrayView.Build(status, hasPassword: true, pairingStopped: false, [Living], autoStart: false);
        Assert.Equal(new SettingsRow("状态", "出错", RowTone.Alert, RowAction.ShowFault), Row(groups, "连接", "状态"));
        Assert.Equal((TrayIcon.Alert, "无线显示：出错"), TrayView.Tray(status, true, false));
    }

    [Fact]
    public void 刚设了密码_状态还没跟上来的那一瞬间_不再叫人去设密码()
    {
        var groups = TrayView.Build(new HostStatus(HostState.NeedPairing), hasPassword: true, pairingStopped: false, [], autoStart: false);
        Assert.Equal(new SettingsRow("状态", "查找中", RowTone.Busy), Row(groups, "连接", "状态"));
    }

    [Fact]
    public void 每一行都只有标题和一个短的状态值()
    {
        // 界面规矩：值是几个字的状态，不是一句说明。所有局面都过一遍（设备名是人起的，不算）
        foreach (HostState state in Enum.GetValues<HostState>())
            foreach (bool password in new[] { false, true })
                foreach (bool stopped in new[] { false, true })
                {
                    var status = new HostStatus(state, Tab5: "Tab5-46C3", Detail: "一段很长很长的出错原因，不应该出现在主界面上");
                    foreach (SettingsGroup group in TrayView.Build(status, password, stopped, [Living], autoStart: true))
                        foreach (SettingsRow row in group.Rows.Where(r => r.Action != RowAction.ForgetPeer && r.Title != "设备"))
                        {
                            Assert.InRange(row.Title.Length, 1, 6);
                            Assert.InRange(row.Value.Length, 1, 12);
                        }
                    Assert.InRange(TrayView.Tray(status, password, stopped).Tip.Length, 1, 63);       // 托盘提示系统最多显示 63 个字符
                }
    }
}
