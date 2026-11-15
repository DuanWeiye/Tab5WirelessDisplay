using WinDisplay.Core;

namespace WinDisplay.Tray;

/// <summary>
/// 几个小对话框，和设置窗口一个风格（深色）：确认（有数据后果的操作）、看一段文字（出错原因）、输配对密码。
/// 文字都短：只说会发生什么，不解释。
/// </summary>
internal sealed class Dialog : Form
{
    private readonly TableLayoutPanel _body = new()
    {
        Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Theme.Window,
    };
    private readonly FlowLayoutPanel _buttons = new()
    {
        FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
        Margin = Padding.Empty, BackColor = Theme.Window,
    };
    private readonly Font _font;

    private Dialog(string title)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _font = Theme.Font(Theme.RowPx, DeviceDpi);
        Font = _font;
        Padding = new Padding(Px(20), Px(18), Px(20), Px(16));
        Controls.Add(_body);
    }

    private int Px(float logical) => (int)Math.Round(logical * DeviceDpi / 96f);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }

    private void AddText(string text) => _body.Controls.Add(new Label
    {
        Text = text, AutoSize = true, MaximumSize = new Size(Px(340), 0), MinimumSize = new Size(Px(260), 0),
        ForeColor = Theme.Text, BackColor = Theme.Window, Margin = new Padding(0, 0, 0, Px(16)), UseMnemonic = false,
    });

    /// <param name="kind">0 = 普通，1 = 主要的那个（强调色），2 = 有数据后果的那个（错误色）。</param>
    private Button AddButton(string text, DialogResult result, int kind)
    {
        Color back = kind switch { 1 => Theme.Accent, 2 => Theme.Alert, _ => Theme.Card };
        var button = new Button
        {
            Text = text, DialogResult = result, FlatStyle = FlatStyle.Flat, Size = new Size(Px(88), Px(32)),      // 按钮上都是两个字，定死大小
            BackColor = back, ForeColor = kind == 0 ? Theme.Text : Color.Black, Margin = new Padding(Px(8), 0, 0, 0), UseMnemonic = false,
        };
        button.FlatAppearance.BorderColor = kind == 0 ? Theme.Edge : back;
        button.FlatAppearance.MouseOverBackColor = kind == 0 ? Theme.CardHover : ControlPaint.Light(back, 0.3f);
        button.FlatAppearance.MouseDownBackColor = kind == 0 ? Theme.CardDown : ControlPaint.Dark(back, 0.05f);
        _buttons.Controls.Add(button);
        return button;
    }

    private void Finish()
    {
        _body.Controls.Add(_buttons);
    }

    /// <summary>
    /// 弹出来等人答。有 owner（设置窗口）就摆在它正中、跟着它走。没有 owner 的时候（启动没成：那时还没有托盘图标，也没有窗口）
    /// 摆在屏幕正中、压在别的窗口上面、任务栏上有它的按钮：不然它可能一出来就被别的窗口盖住，没有任何地方能把它找回来，
    /// 而程序一直在等它（这时再起一份也只会悄悄退出）。
    /// </summary>
    internal DialogResult ShowModal(IWin32Window? owner)
    {
        Place(owner);
        return ShowDialog(owner);
    }

    /// <summary>照有没有 owner 定摆在哪（见 <see cref="ShowModal"/>）。</summary>
    internal void Place(IWin32Window? owner)
    {
        if (owner is not null) return;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        TopMost = true;
    }

    /// <summary>确认一件有数据后果的事。ok 是那个动作的名字（「移除」「更改」）。</summary>
    public static bool Confirm(IWin32Window? owner, string text, string ok)
    {
        using Dialog dialog = BuildConfirm(text, ok);
        return dialog.ShowModal(owner) == DialogResult.OK;
    }

    internal static Dialog BuildConfirm(string text, string ok)
    {
        var dialog = new Dialog("无线显示");
        dialog.AddText(text);
        dialog.CancelButton = dialog.AddButton("取消", DialogResult.Cancel, 0);     // 先加的在右边
        dialog.AddButton(ok, DialogResult.OK, 2);
        dialog.Finish();
        dialog.ActiveControl = (Control)dialog.CancelButton;                        // 回车落在「取消」上：不小心按到不会删东西
        return dialog;
    }

    /// <summary>给人看一段文字（出错的原因）。</summary>
    public static void Info(IWin32Window? owner, string text)
    {
        using Dialog dialog = BuildInfo(text);
        dialog.ShowModal(owner);
    }

    internal static Dialog BuildInfo(string text)
    {
        var dialog = new Dialog("无线显示");
        dialog.AddText(text);
        Button close = dialog.AddButton("关闭", DialogResult.Cancel, 0);
        dialog.CancelButton = dialog.AcceptButton = close;
        dialog.Finish();
        return dialog;
    }

    /// <summary>输配对密码。取消返回 null。只收 Tab5 上打得出来的字符（见 <see cref="PairingSecret.IsValidPassword"/>），所以确定的时候一定是合规定的。</summary>
    public static string? AskPassword(IWin32Window? owner)
    {
        using Dialog dialog = BuildPassword(out TextBox box);
        return dialog.ShowModal(owner) == DialogResult.OK ? box.Text : null;
    }

    internal static Dialog BuildPassword(out TextBox box)
    {
        var dialog = new Dialog("配对密码");
        var input = new TextBox
        {
            UseSystemPasswordChar = true, MaxLength = PairingSecret.MaxPasswordLength, BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Card, ForeColor = Theme.Text, Width = dialog.Px(300), Margin = new Padding(0, 0, 0, dialog.Px(10)),
            ImeMode = ImeMode.Disable,
        };
        var reveal = new CheckBox
        {
            Text = "显示", AutoSize = true, ForeColor = Theme.Dim, BackColor = Theme.Window, Margin = new Padding(0, 0, 0, dialog.Px(16)), UseMnemonic = false,
        };
        reveal.CheckedChanged += (_, _) => input.UseSystemPasswordChar = !reveal.Checked;
        dialog._body.Controls.Add(input);
        dialog._body.Controls.Add(reveal);
        dialog.CancelButton = dialog.AddButton("取消", DialogResult.Cancel, 0);
        Button ok = dialog.AddButton("确定", DialogResult.OK, 1);
        ok.Enabled = false;
        dialog.AcceptButton = ok;
        // 打不出来的字符（中文、全角符号……）当场去掉：粘贴进来的也一样
        input.TextChanged += (_, _) =>
        {
            string kept = new([.. input.Text.Where(c => c is >= ' ' and <= '~')]);
            if (kept != input.Text)
            {
                int caret = Math.Min(input.SelectionStart, kept.Length);
                input.Text = kept;
                input.SelectionStart = caret;
            }
            ok.Enabled = PairingSecret.IsValidPassword(input.Text);
        };
        dialog.Finish();
        dialog.ActiveControl = input;
        box = input;
        return dialog;
    }
}
