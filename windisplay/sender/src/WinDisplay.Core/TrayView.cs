namespace WinDisplay.Core;

/// <summary>一行右边那个值怎么画。</summary>
public enum RowTone
{
    Normal,     // 平常的状态：次要色的字
    Good,       // 连上了：强调色
    Busy,       // 正在做（查找、连接、等人输密码）：前面带一个转圈的动画
    Alert,      // 要人动手，或者出了错：错误色
}

/// <summary>点这一行做什么。</summary>
public enum RowAction
{
    None,
    ShowFault,          // 看出了什么错
    SetPassword,        // 设 / 改配对密码
    ResumePairing,      // 密码连着不对、停止配对之后恢复
    ForgetPeer,         // 移除这台 Tab5 的配对（PeerId）
    ToggleAutoStart,    // 开机启动 开 ↔ 关
}

/// <summary>
/// 设置窗口里的一行：标题 + 一个短的状态值（界面规矩：不挂说明文字，正常状态不解释，要人动手的用错误色、点一下直接去处理）。
/// </summary>
/// <param name="Switch">不是 null = 这一行画成开关，值是它现在开着没有。</param>
public sealed record SettingsRow(string Title, string Value, RowTone Tone = RowTone.Normal, RowAction Action = RowAction.None,
                                 byte[]? PeerId = null, bool? Switch = null);

public sealed record SettingsGroup(string Title, IReadOnlyList<SettingsRow> Rows);

/// <summary>托盘图标的三种样子。</summary>
public enum TrayIcon
{
    Idle,           // 没连着（在找 Tab5、正在连）
    Connected,      // Tab5 上显示着这台电脑的画面
    Alert,          // 要人动手，或者出了错
}

/// <summary>
/// 常驻的发送端的状态 → 设置窗口里显示什么、托盘图标什么样（纯计算，和平台无关；画出来是托盘程序的事）。
/// </summary>
public static class TrayView
{
    /// <summary>设置窗口的内容，从上到下。</summary>
    /// <param name="autoStart">开机启动现在开着没有。</param>
    public static IReadOnlyList<SettingsGroup> Build(HostStatus status, bool hasPassword, bool pairingStopped,
                                                     IReadOnlyList<PairedPeer> peers, bool autoStart)
    {
        var groups = new List<SettingsGroup>();

        var link = new List<SettingsRow> { StatusRow(status, hasPassword, pairingStopped) };
        if (status.State == HostState.Showing && !string.IsNullOrEmpty(status.Tab5)) link.Add(new SettingsRow("设备", status.Tab5));
        groups.Add(new SettingsGroup("连接", link));

        groups.Add(new SettingsGroup("配对",
        [
            !hasPassword ? new SettingsRow("配对密码", "未设置", RowTone.Alert, RowAction.SetPassword)
            : pairingStopped ? new SettingsRow("配对密码", "已停用", RowTone.Alert, RowAction.ResumePairing)
            : new SettingsRow("配对密码", "已设置", RowTone.Normal, RowAction.SetPassword),
        ]));

        if (peers.Count > 0)
            groups.Add(new SettingsGroup("已配对的设备", [.. peers.Select(p => new SettingsRow(p.Name, "移除", RowTone.Normal, RowAction.ForgetPeer, p.Id))]));

        groups.Add(new SettingsGroup("通用", [new SettingsRow("开机启动", autoStart ? "开" : "关", RowTone.Normal, RowAction.ToggleAutoStart, Switch: autoStart)]));
        return groups;
    }

    /// <summary>
    /// 托盘图标和鼠标停在上面时的提示。设置窗口里有哪一行是要人动手的（红的），图标就是「要人动手」的样子，提示说的是那件事：
    /// 状态行自己是红的就说它；不然没设密码、停了配对也算（这时没有 Tab5 在等，状态行是「查找中」「已连接」，但新的 Tab5 配对不上）。
    /// </summary>
    public static (TrayIcon Icon, string Tip) Tray(HostStatus status, bool hasPassword, bool pairingStopped)
    {
        SettingsRow row = StatusRow(status, hasPassword, pairingStopped);
        if (row.Tone == RowTone.Alert) return (TrayIcon.Alert, $"无线显示：{row.Value}");
        if (!hasPassword) return (TrayIcon.Alert, "无线显示：未设置密码");
        if (pairingStopped) return (TrayIcon.Alert, "无线显示：配对已停用");
        return status.State == HostState.Showing
            ? (TrayIcon.Connected, string.IsNullOrEmpty(status.Tab5) ? $"无线显示：{row.Value}" : $"无线显示：{row.Value} {status.Tab5}")
            : (TrayIcon.Idle, $"无线显示：{row.Value}");
    }

    private static SettingsRow StatusRow(HostStatus status, bool hasPassword, bool pairingStopped) => status.State switch
    {
        HostState.Stopped => new SettingsRow("状态", "已停止"),
        HostState.Showing => new SettingsRow("状态", "已连接", RowTone.Good),
        HostState.Connecting => new SettingsRow("状态", "连接中", RowTone.Busy),
        HostState.AwaitingPassword => new SettingsRow("状态", "等待输入密码", RowTone.Busy),      // 在 Tab5 上输
        HostState.Faulted => new SettingsRow("状态", "出错", RowTone.Alert, RowAction.ShowFault),
        // 有 Tab5 开着页面、但没配对过，而现在配对不了：缺的是哪一样，点一下就去办
        HostState.NeedPairing when !hasPassword => new SettingsRow("状态", "未设置密码", RowTone.Alert, RowAction.SetPassword),
        HostState.NeedPairing when pairingStopped => new SettingsRow("状态", "配对已停用", RowTone.Alert, RowAction.ResumePairing),
        // Searching；以及刚设了密码 / 刚恢复配对、状态还没跟上来的那一瞬间
        _ => new SettingsRow("状态", "查找中", RowTone.Busy),
    };
}
