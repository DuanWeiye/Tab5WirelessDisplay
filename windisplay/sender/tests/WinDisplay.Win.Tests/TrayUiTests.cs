using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WinDisplay.Core;
using WinDisplay.Tray;
using Xunit;

namespace WinDisplay.Win.Tests;

/// <summary>
/// 托盘程序的设置窗口和对话框，画和接线的那一层：鼠标在窗口上点下去，报出来的是不是那一行；对话框里什么时候能点确定、默认落在哪个按钮上。
/// （点了一行之后做什么在 Core 的 TrayActionsTests 里测，那边不要窗口。）真的建窗口、给它发鼠标消息；经 ssh 也能跑。只在 Windows 上跑。
/// </summary>
public sealed class TrayUiTests
{
    private const int WmLButtonDown = 0x0201, WmLButtonUp = 0x0202, WmMouseMove = 0x0200, MkLButton = 0x0001;

    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private static readonly PairedPeer Living = new([.. Enumerable.Range(1, Proto.IdSize).Select(i => (byte)i)], "Tab5-46C3");
    private static readonly PairedPeer Study = new([.. Enumerable.Range(0x40, Proto.IdSize).Select(i => (byte)i)], "书房的 Tab5");

    // 窗口要在单线程套间的线程上建
    private static void OnUiThread(Action body)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                error = ExceptionDispatchInfo.Capture(e);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
    }

    private static IntPtr At(int x, int y) => (y << 16) | (x & 0xFFFF);

    private static void Press(Control c, int x, int y) => SendMessageW(c.Handle, WmLButtonDown, MkLButton, At(x, y));

    private static void Release(Control c, int x, int y) => SendMessageW(c.Handle, WmLButtonUp, IntPtr.Zero, At(x, y));

    /// <summary>连着的时候的设置窗口：状态、设备两行不能点，配对密码、两台设备、开机启动能点。</summary>
    private static SettingsForm Connected(List<SettingsRow> clicked)
    {
        var form = new SettingsForm();
        form.Panel.RowClicked += clicked.Add;
        form.SetContent(TrayView.Build(new HostStatus(HostState.Showing, "Tab5-46C3"), hasPassword: true, pairingStopped: false, [Living, Study], autoStart: true));
        form.Show();
        Application.DoEvents();
        return form;
    }

    /// <summary>沿着窗口正中从上到下，隔几个像素点一下：依次点到了哪些行（挨着的同一行只算一次）。</summary>
    private static List<SettingsRow> ClickDownTheMiddle(SettingsForm form, List<SettingsRow> clicked)
    {
        var hit = new List<SettingsRow>();
        for (int y = 1; y < form.Panel.Height; y += 3)
        {
            clicked.Clear();
            Press(form.Panel, form.Panel.Width / 2, y);
            Release(form.Panel, form.Panel.Width / 2, y);
            Assert.True(clicked.Count <= 1, "点一下报了不止一行");
            if (clicked.Count == 1 && (hit.Count == 0 || !ReferenceEquals(hit[^1], clicked[0]))) hit.Add(clicked[0]);
        }
        return hit;
    }

    [Fact]
    public void 从上到下点一遍_能点的行按画的先后各报一次_不能点的行和空白处不报()
    {
        OnUiThread(() =>
        {
            var clicked = new List<SettingsRow>();
            using SettingsForm form = Connected(clicked);
            Assert.Equal(form.Panel.ContentHeight, form.ClientSize.Height);         // 窗口照内容定高：最下面一行没有被裁掉

            List<SettingsRow> hit = ClickDownTheMiddle(form, clicked);
            Assert.Equal(["配对密码", "Tab5-46C3", "书房的 Tab5", "开机启动"], hit.Select(r => r.Title));
            Assert.Equal([RowAction.SetPassword, RowAction.ForgetPeer, RowAction.ForgetPeer, RowAction.ToggleAutoStart], hit.Select(r => r.Action));
            Assert.Equal(Study.Id, hit[2].PeerId);                                  // 点的是哪一台，带的就是哪一台的编号
        });
    }

    [Fact]
    public void 内容变了窗口跟着变_少了一台设备之后点到的行也跟着变()
    {
        OnUiThread(() =>
        {
            var clicked = new List<SettingsRow>();
            using SettingsForm form = Connected(clicked);
            int before = form.ClientSize.Height;
            form.SetContent(TrayView.Build(new HostStatus(HostState.Searching), hasPassword: true, pairingStopped: false, [Living], autoStart: false));
            Application.DoEvents();
            Assert.True(form.ClientSize.Height < before, "少了两行，窗口没有变矮");
            Assert.Equal(["配对密码", "Tab5-46C3", "开机启动"], ClickDownTheMiddle(form, clicked).Select(r => r.Title));
        });
    }

    /// <summary>能点的各行在哪：沿中线点下去，记下每一行第一次报出来时的 y（再往里几个像素）。标题 → y。</summary>
    private static Dictionary<string, int> RowYs(SettingsForm form, List<SettingsRow> clicked)
    {
        var rowY = new Dictionary<string, int>();
        for (int y = 1; y < form.Panel.Height; y += 3)
        {
            clicked.Clear();
            Press(form.Panel, form.Panel.Width / 2, y);
            Release(form.Panel, form.Panel.Width / 2, y);
            if (clicked.Count == 1) rowY.TryAdd(clicked[0].Title, y + 6);
        }
        clicked.Clear();
        return rowY;
    }

    [Fact]
    public void 在一行上按下_挪到别的行才松手_不算点()
    {
        OnUiThread(() =>
        {
            var clicked = new List<SettingsRow>();
            using SettingsForm form = Connected(clicked);
            Dictionary<string, int> rowY = RowYs(form, clicked);
            int x = form.Panel.Width / 2;
            Press(form.Panel, x, rowY["Tab5-46C3"]);
            SendMessageW(form.Panel.Handle, WmMouseMove, MkLButton, At(x, rowY["书房的 Tab5"]));
            Release(form.Panel, x, rowY["书房的 Tab5"]);
            Assert.Empty(clicked);                                                  // 不然想反悔挪开手，结果移除了旁边那一台

            Press(form.Panel, x, rowY["Tab5-46C3"]);                                // 挪出去又挪回原来那一行再松手：算点
            SendMessageW(form.Panel.Handle, WmMouseMove, MkLButton, At(x, rowY["书房的 Tab5"]));
            Release(form.Panel, x, rowY["Tab5-46C3"]);
            Assert.Equal("Tab5-46C3", Assert.Single(clicked).Title);
        });
    }

    [Fact]
    public void 按着一行的时候正好连上了_下面的行整体往下挪_原地松手不算点_不会移除另一台()
    {
        OnUiThread(() =>
        {
            var clicked = new List<SettingsRow>();
            using var form = new SettingsForm();
            form.Panel.RowClicked += clicked.Add;
            // 还没连上：状态、配对密码、两台设备、开机启动
            form.SetContent(TrayView.Build(new HostStatus(HostState.Searching), hasPassword: true, pairingStopped: false, [Living, Study], autoStart: true));
            form.Show();
            Application.DoEvents();
            int x = form.Panel.Width / 2, y = RowYs(form, clicked)["书房的 Tab5"];

            Press(form.Panel, x, y);
            // 手还按着，连上了：「连接」那一组多出「设备」一行，下面的都往下挪一行，鼠标底下变成了另一台
            form.SetContent(TrayView.Build(new HostStatus(HostState.Showing, "Tab5-46C3"), hasPassword: true, pairingStopped: false, [Living, Study], autoStart: true));
            Application.DoEvents();
            Release(form.Panel, x, y);
            Assert.Empty(clicked);                                                  // 不然确认框里问的、移除的就是另一台

            Press(form.Panel, x, y);                                                // 前提没错：那个位置上现在确实是另一台，单点一下报的是它
            Release(form.Panel, x, y);
            Assert.Equal(Living.Id, Assert.Single(clicked).PeerId);
        });
    }

    [Fact]
    public void 按着一行的时候只是状态变了_行没挪地方_松手照常算点()
    {
        OnUiThread(() =>
        {
            var clicked = new List<SettingsRow>();
            using var form = new SettingsForm();
            form.Panel.RowClicked += clicked.Add;
            form.SetContent(TrayView.Build(new HostStatus(HostState.Searching), hasPassword: true, pairingStopped: false, [Living, Study], autoStart: false));
            form.Show();
            Application.DoEvents();
            Dictionary<string, int> rowY = RowYs(form, clicked);
            int x = form.Panel.Width / 2;

            // 状态一秒里可能变好几次（查找中 → 连接中 → 查找中），每变一次内容就刷新一次：不能把正常的点击吞掉
            Press(form.Panel, x, rowY["书房的 Tab5"]);
            form.SetContent(TrayView.Build(new HostStatus(HostState.Connecting), hasPassword: true, pairingStopped: false, [Living, Study], autoStart: false));
            Application.DoEvents();
            Release(form.Panel, x, rowY["书房的 Tab5"]);
            Assert.Equal(Study.Id, Assert.Single(clicked).PeerId);

            // 按着的这一行自己的值变了（别处把开机启动打开了）：还是这一行，报出去的是现在的样子
            clicked.Clear();
            Press(form.Panel, x, rowY["开机启动"]);
            form.SetContent(TrayView.Build(new HostStatus(HostState.Connecting), hasPassword: true, pairingStopped: false, [Living, Study], autoStart: true));
            Application.DoEvents();
            Release(form.Panel, x, rowY["开机启动"]);
            Assert.True(Assert.Single(clicked).Switch);
        });
    }

    /// <summary>
    /// 把对话框照 owner 弹出来，一显示出来就记下它摆成什么样、关掉。ssh 进来的会话不是交互式的，弹不了模态框（ShowDialog 会抛异常）：
    /// 那里只摆、不弹。
    /// </summary>
    private static (FormStartPosition Start, bool InTaskbar, bool TopMost) HowShown(Dialog dialog, IWin32Window? owner)
    {
        if (!SystemInformation.UserInteractive)
        {
            dialog.Place(owner);
            return (dialog.StartPosition, dialog.ShowInTaskbar, dialog.TopMost);
        }
        (FormStartPosition, bool, bool)? seen = null;
        dialog.Shown += (_, _) =>
        {
            seen = (dialog.StartPosition, dialog.ShowInTaskbar, dialog.TopMost);
            dialog.Close();
        };
        dialog.ShowModal(owner);
        return Assert.NotNull(seen);
    }

    [Fact]
    public void 启动没成时的对话框_没有窗口可依附_摆在屏幕正中_压在最上面_任务栏上找得到()
    {
        OnUiThread(() =>
        {
            // 那时还没有托盘图标：对话框要是被别的窗口盖住，就没有任何地方能把它找回来了
            using Dialog alone = Dialog.BuildInfo("这个会话里注入不了输入（要在登录用户的桌面会话里跑）");
            Assert.Equal((FormStartPosition.CenterScreen, true, true), HowShown(alone, null));

            // 从设置窗口里弹出来的：依附着它，摆在它正中，不单占任务栏、不压在别的程序上面
            using var owner = new SettingsForm();
            owner.Show();
            using Dialog child = Dialog.BuildConfirm("移除「书房的 Tab5」？", "移除");
            Assert.Equal((FormStartPosition.CenterParent, false, false), HowShown(child, owner));
        });
    }

    [Fact]
    public void 托盘图标照主显示器现在的缩放比例定尺寸_画出来就是那么大()
    {
        int size = Icons.TraySize();
        Assert.InRange(size, 16, 64);               // 100% 是 16，往上按比例
        foreach (TrayIcon kind in Enum.GetValues<TrayIcon>())
            foreach (bool light in new[] { false, true })
            {
                using Icon icon = Icons.Make(kind, size, light);
                Assert.Equal(new Size(size, size), icon.Size);
            }
    }

    [Fact]
    public void 输密码的框_空着不能确定_打不出来的字符当场去掉_合规定才能确定()
    {
        OnUiThread(() =>
        {
            using Dialog dialog = Dialog.BuildPassword(out TextBox box);
            var ok = Assert.IsType<Button>(dialog.AcceptButton);
            Assert.False(ok.Enabled);                           // 还没输
            Assert.Same(box, dialog.ActiveControl);             // 一打开光标就在输入框里

            box.Text = "2468";
            Assert.True(ok.Enabled);
            box.Text = "tab5 密码，2026！~";                     // 粘贴进来的：中文和全角符号 Tab5 上打不出来
            Assert.Equal("tab5 2026~", box.Text);
            Assert.True(ok.Enabled);
            box.Text = "密码";
            Assert.Equal("", box.Text);
            Assert.False(ok.Enabled);
            box.Text = new string('a', PairingSecret.MaxPasswordLength);
            Assert.True(ok.Enabled);
            Assert.Equal(PairingSecret.MaxPasswordLength, box.MaxLength);           // 再多就打不进去了
            Assert.Equal(DialogResult.OK, ok.DialogResult);
            Assert.Equal(DialogResult.Cancel, Assert.IsType<Button>(dialog.CancelButton).DialogResult);
        });
    }

    [Fact]
    public void 确认框_回车和Esc都落在取消上_要点那个动作才算确认()
    {
        OnUiThread(() =>
        {
            using Dialog dialog = Dialog.BuildConfirm("移除「书房的 Tab5」？", "移除");
            var cancel = Assert.IsType<Button>(dialog.CancelButton);
            Assert.Equal("取消", cancel.Text);
            Assert.Equal(DialogResult.Cancel, cancel.DialogResult);
            Assert.Same(cancel, dialog.ActiveControl);          // 回车按下去是取消：不小心按到不会删东西
            Assert.Null(dialog.AcceptButton);
            Button act = Assert.Single(AllButtons(dialog), b => b != cancel);
            Assert.Equal("移除", act.Text);
            Assert.Equal(DialogResult.OK, act.DialogResult);
        });
    }

    private static IEnumerable<Button> AllButtons(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Button b) yield return b;
            foreach (Button inner in AllButtons(c)) yield return inner;
        }
    }
}
