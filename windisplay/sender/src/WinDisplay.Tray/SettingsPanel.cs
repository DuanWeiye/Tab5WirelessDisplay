using System.Drawing.Drawing2D;
using WinDisplay.Core;

namespace WinDisplay.Tray;

/// <summary>
/// 设置窗口的内容：一组一组的行，每行左边标题、右边一个短的状态值（<see cref="TrayView"/> 给什么就画什么）。
/// 全部自己画（一个控件），能点的行鼠标移上去变亮、点了报 <see cref="RowClicked"/>。Busy 的行值前面有个转圈的动画。
/// </summary>
internal sealed class SettingsPanel : Control
{
    private readonly record struct Slot(Rectangle Bounds, SettingsRow Row);

    private readonly System.Windows.Forms.Timer _spin = new() { Interval = 40 };
    private Font _rowFont, _smallFont;
    private int _fontDpi;
    private IReadOnlyList<SettingsGroup> _groups = [];
    private readonly List<Slot> _slots = [];
    private readonly List<(Rectangle Bounds, string Title)> _titles = [];
    private int _hover = -1, _down = -1, _angle;
    private SettingsRow? _downRow;      // 鼠标按下去的那一行（_down 是它现在排在第几个；内容刷新后重新找）
    private Point? _mouse;              // 鼠标现在在哪（不在这个控件上 = null）

    public SettingsPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Window;
        _fontDpi = DeviceDpi;
        _rowFont = Theme.Font(Theme.RowPx, _fontDpi);
        _smallFont = Theme.Font(Theme.SmallPx, _fontDpi);
        _spin.Tick += (_, _) =>
        {
            _angle = (_angle + 24) % 360;
            foreach (Slot s in _slots)
                if (s.Row.Tone == RowTone.Busy) Invalidate(s.Bounds);
        };
    }

    /// <summary>点了一行（只有能点的行会报）。</summary>
    public event Action<SettingsRow>? RowClicked;

    /// <summary>内容排下来有多高（像素），窗口照它定高。</summary>
    public int ContentHeight { get; private set; }

    public void SetContent(IReadOnlyList<SettingsGroup> groups)
    {
        _groups = groups;
        Arrange();
        _spin.Enabled = _slots.Any(s => s.Row.Tone == RowTone.Busy);
        Invalidate();
    }

    private int Px(float logical) => (int)Math.Round(logical * DeviceDpi / 96f);

    private void Arrange()
    {
        if (_fontDpi != DeviceDpi)      // 窗口挪到了缩放比例不一样的显示器上：字跟着换
        {
            _rowFont.Dispose();
            _smallFont.Dispose();
            _fontDpi = DeviceDpi;
            _rowFont = Theme.Font(Theme.RowPx, _fontDpi);
            _smallFont = Theme.Font(Theme.SmallPx, _fontDpi);
        }
        _slots.Clear();
        _titles.Clear();
        int side = Px(20), y = Px(8), rowH = Px(46), gap = Px(3);
        foreach (SettingsGroup group in _groups)
        {
            _titles.Add((new Rectangle(side, y, Width - 2 * side, Px(30)), group.Title));
            y += Px(30);
            foreach (SettingsRow row in group.Rows)
            {
                _slots.Add(new Slot(new Rectangle(side, y, Width - 2 * side, rowH), row));
                y += rowH + gap;
            }
            y += Px(6);
        }
        ContentHeight = y + Px(12);
        // 行可能挪了位置（多了一行、少了一行）：鼠标底下是哪一行、按着的那一行现在排第几，都重新认
        _hover = _mouse is { } at ? HitTest(at) : -1;
        _down = _downRow is { } down ? _slots.FindIndex(s => SameRow(s.Row, down)) : -1;
        if (IsHandleCreated) Cursor = _hover >= 0 ? Cursors.Hand : Cursors.Default;
    }

    /// <summary>
    /// 是不是同一行（内容刷新前后）：点了做的是同一件事、对的是同一台设备。右边的值不算（状态变了还是这一行）。
    /// </summary>
    private static bool SameRow(SettingsRow a, SettingsRow b) =>
        a.Action == b.Action && a.Title == b.Title && (a.PeerId ?? []).AsSpan().SequenceEqual(b.PeerId ?? []);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Arrange();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        const TextFormatFlags left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        const TextFormatFlags right = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        foreach ((Rectangle bounds, string title) in _titles)
            TextRenderer.DrawText(g, title, _smallFont, bounds, Theme.Dim, Theme.Window, left);

        for (int i = 0; i < _slots.Count; i++)
        {
            (Rectangle r, SettingsRow row) = _slots[i];
            bool clickable = row.Action != RowAction.None, hover = clickable && i == _hover;
            Color back = !hover ? Theme.Card : i == _down ? Theme.CardDown : Theme.CardHover;
            using (GraphicsPath card = Theme.RoundRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), Px(5)))
            using (var fill = new SolidBrush(back))
            using (var edge = new Pen(Theme.Edge))
            {
                g.FillPath(fill, card);
                g.DrawPath(edge, card);
            }

            int x = r.Right - Px(16);       // 从右往左排：开关 / 箭头，然后是值，值前面可能有转圈
            if (row.Switch is bool on)
            {
                var pill = new Rectangle(x - Px(40), r.Y + (r.Height - Px(20)) / 2, Px(40), Px(20));
                DrawSwitch(g, pill, on);
                x = pill.X - Px(10);
            }
            else if (clickable && row.Action != RowAction.ForgetPeer)
            {
                using var pen = new Pen(Theme.Dim, Math.Max(1f, Px(1.4f)));
                int cy = r.Y + r.Height / 2, s = Px(4);
                g.DrawLines(pen, [new Point(x - s, cy - s), new Point(x, cy), new Point(x - s, cy + s)]);
                x -= s + Px(10);
            }

            Color valueColor = row.Tone switch
            {
                RowTone.Good => Theme.Accent,
                RowTone.Alert => Theme.Alert,
                _ => row.Action == RowAction.ForgetPeer && hover ? Theme.Alert : Theme.Dim,     // 「移除」：鼠标移上去变成错误色
            };
            Size valueSize = TextRenderer.MeasureText(g, row.Value, _rowFont, r.Size, right);
            int valueW = Math.Min(valueSize.Width, r.Width / 2 + Px(40));
            var valueRect = new Rectangle(x - valueW, r.Y, valueW, r.Height);
            TextRenderer.DrawText(g, row.Value, _rowFont, valueRect, valueColor, back, right | TextFormatFlags.EndEllipsis);
            x = valueRect.X;
            if (row.Tone == RowTone.Busy)
            {
                int d = Px(14);
                var arc = new Rectangle(x - Px(6) - d, r.Y + (r.Height - d) / 2, d, d);
                using var pen = new Pen(Theme.Accent, Math.Max(1.5f, Px(2f))) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, arc, _angle, 250);
                x = arc.X;
            }

            var titleRect = new Rectangle(r.X + Px(16), r.Y, Math.Max(0, x - Px(12) - r.X - Px(16)), r.Height);
            TextRenderer.DrawText(g, row.Title, _rowFont, titleRect, Theme.Text, back, left);
        }
    }

    private void DrawSwitch(Graphics g, Rectangle pill, bool on)
    {
        using GraphicsPath path = Theme.RoundRect(new RectangleF(pill.X + 0.5f, pill.Y + 0.5f, pill.Width - 1, pill.Height - 1), (pill.Height - 1) / 2f);
        int knob = pill.Height - Px(8);
        int knobX = on ? pill.Right - Px(4) - knob : pill.X + Px(4);
        if (on)
        {
            using var fill = new SolidBrush(Theme.Accent);
            g.FillPath(fill, path);
        }
        else
        {
            using var edge = new Pen(Theme.Dim);
            g.DrawPath(edge, path);
        }
        using var dot = new SolidBrush(on ? Color.Black : Theme.Dim);
        g.FillEllipse(dot, knobX, pill.Y + Px(4), knob, knob);
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _slots.Count; i++)
            if (_slots[i].Row.Action != RowAction.None && _slots[i].Bounds.Contains(p)) return i;
        return -1;
    }

    private void SetHover(int index)
    {
        if (index == _hover) return;
        if (_hover >= 0 && _hover < _slots.Count) Invalidate(_slots[_hover].Bounds);
        _hover = index;
        if (index >= 0) Invalidate(_slots[index].Bounds);
        Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.Location;
        SetHover(HitTest(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = null;
        SetHover(-1);
        _down = -1;
        _downRow = null;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _mouse = e.Location;
        _down = HitTest(e.Location);
        _downRow = _down >= 0 ? _slots[_down].Row : null;
        if (_down >= 0) Invalidate(_slots[_down].Bounds);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        SettingsRow? down = _downRow;
        if (_down >= 0 && _down < _slots.Count) Invalidate(_slots[_down].Bounds);
        _down = -1;
        _downRow = null;
        if (down is null || e.Button != MouseButtons.Left) return;
        // 松手的地方得是按下去的那一行才算点。按「是不是同一行」认，不按排在第几个认：按着的时候内容可能刷新过
        // （正好连上了，上面多出一行，下面的整体往下挪），原地松手时鼠标底下已经是另一行——那不算点，不然移除的就是另一台设备。
        // 报出去的是现在的这一行（值可能变过，比如开关）
        int at = HitTest(e.Location);
        if (at >= 0 && SameRow(_slots[at].Row, down)) RowClicked?.Invoke(_slots[at].Row);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _spin.Dispose();
            _rowFont.Dispose();
            _smallFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>设置窗口：固定宽度，高度跟着内容走；关掉只是关窗口，程序还在托盘里。</summary>
internal sealed class SettingsForm : Form
{
    private const int LogicalWidth = 400;

    public SettingsForm()
    {
        Text = "无线显示";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Window;
        AutoScaleMode = AutoScaleMode.None;         // 尺寸都是自己按 DPI 算的
        KeyPreview = true;
        Panel = new SettingsPanel { Dock = DockStyle.Fill };
        Controls.Add(Panel);
    }

    public SettingsPanel Panel { get; }

    private IReadOnlyList<SettingsGroup> _groups = [];

    public void SetContent(IReadOnlyList<SettingsGroup> groups)
    {
        _groups = groups;
        int width = (int)Math.Round(LogicalWidth * DeviceDpi / 96f);
        if (ClientSize.Width != width) ClientSize = new Size(width, Math.Max(ClientSize.Height, 1));
        Panel.SetContent(groups);
        if (ClientSize.Height != Panel.ContentHeight) ClientSize = new Size(width, Panel.ContentHeight);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        SetContent(_groups);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }
}
