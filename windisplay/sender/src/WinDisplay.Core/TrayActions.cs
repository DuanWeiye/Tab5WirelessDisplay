using System.Security;

namespace WinDisplay.Core;

/// <summary>设置窗口里要问人的几样。托盘程序用对话框做；单元测试里换成照写好的回答的。</summary>
public interface ITrayPrompts
{
    /// <summary>确认一件有数据后果的事。verb 是那个动作的名字（「移除」「更改」），写在确认的那个按钮上。</summary>
    bool Confirm(string text, string verb);

    /// <summary>输配对密码。取消返回 null；给回来的一定合规定（<see cref="PairingSecret.IsValidPassword"/>，不合规定的在对话框里点不了确定）。</summary>
    string? AskPassword();

    /// <summary>给人看一段文字（出错的原因）。</summary>
    void Info(string text);
}

/// <summary>
/// 人在设置窗口里点了一行之后做什么（<see cref="TrayView"/> 管显示什么，这里管点了怎么办）：该问的先问，然后叫 <see cref="SenderHost"/> 去做。
/// 存不进文件、写不进注册表这类错不往外抛：告诉人没成和原因，程序照常。在界面线程上用。
/// </summary>
/// <param name="setAutoStart">把开机启动设成开（true）/ 关。</param>
/// <param name="log">没成的事记一笔（给查问题用）。</param>
public sealed class TrayActions(SenderHost host, ITrayPrompts prompts, Action<bool> setAutoStart, Action<string>? log = null)
{
    public void Run(SettingsRow row)
    {
        switch (row.Action)
        {
            case RowAction.ShowFault:
                // 出了错过一会儿自己再试：点下去的时候可能已经好了，那就没什么可看的
                if (host.Status.Detail is { } why) prompts.Info(why);
                break;
            case RowAction.SetPassword:
                if (prompts.AskPassword() is not { } password) break;
                int paired = host.Peers.Count;
                if (paired > 0 && !prompts.Confirm($"更改密码会移除 {paired} 台已配对的设备。", "更改")) break;
                Try(() => host.SetPassword(password));
                break;
            case RowAction.ResumePairing:
                Try(host.ResumePairing);
                break;
            case RowAction.ForgetPeer:
                if (row.PeerId is { } id && prompts.Confirm($"移除「{row.Title}」？", "移除")) Try(() => host.Forget(id));
                break;
            case RowAction.ToggleAutoStart:
                Try(() => setAutoStart(row.Switch != true));
                break;
        }
    }

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is PairStoreException or IOException or UnauthorizedAccessException or SecurityException)
        {
            log?.Invoke("没存上：" + e.Message);
            prompts.Info("没存上：" + e.Message);
        }
    }
}
