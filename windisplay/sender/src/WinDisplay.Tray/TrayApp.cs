using Microsoft.Win32;
using WinDisplay.Core;
using WinDisplay.Win;

namespace WinDisplay.Tray;

/// <summary>写到文件里的日志（给查问题用，界面上不显示）：连接怎么结束的、出了什么错。文件大了就从头再来。</summary>
internal sealed class Log(string path)
{
    private const long MaxBytes = 1 << 20;
    private readonly object _lock = new();

    public void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 日志写不进去不耽误正事
            }
        }
    }
}

/// <summary>
/// 托盘里的那个图标，和从它打开的设置窗口。显示什么全照 <see cref="TrayView"/>，点了一行做什么全照 <see cref="TrayActions"/>；这里只管画和接线。
/// </summary>
internal sealed class TrayApp : ApplicationContext, ITrayPrompts
{
    /// <summary>开机启动那一条在注册表里的名字。</summary>
    public const string AutoStartName = "WinDisplayTray";

    private readonly SenderHost _host;
    private readonly InputPump _pump;
    private readonly Log _log;
    private readonly SynchronizationContext _ui = new WindowsFormsSynchronizationContext();
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Dictionary<(TrayIcon Kind, bool Light, int Size), Icon> _icons = [];
    private readonly TrayActions _actions;
    private SettingsForm? _form;
    private int _refreshQueued;
    private bool _exiting;

    /// <summary>在界面线程上建。host 还没 Start 也行。</summary>
    public TrayApp(SenderHost host, InputPump pump, Log log)
    {
        (_host, _pump, _log) = (host, pump, log);
        _actions = new TrayActions(host, this, on => AutoStart.Set(AutoStartName, Application.ExecutablePath, on), log.Write);
        _menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new MenuColors()), ShowImageMargin = false, BackColor = Theme.Card, ForeColor = Theme.Text,
        };
        _menu.Items.Add("设置", null, (_, _) => ShowSettings());
        _menu.Items.Add("退出", null, (_, _) => Exit());
        _icon = new NotifyIcon { ContextMenuStrip = _menu, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowSettings();
        };
        // 这两个事件在后台线程上来：转到界面线程，而且不等（SenderHost.Changed 的规矩）
        _host.Changed += QueueRefresh;
        _host.ConnectionEnded += (tab5, result) => _log.Write($"连接结束：{tab5}，{result.End}{(result.Detail is null ? "" : "（" + result.Detail + "）")}，显示了 {result.FramesAcked} 帧");
        _host.Faulted += why => _log.Write("出错：" + why);       // 界面上只有「出错」两个字，原因要点开才看；事后查问题靠这一行
        // 注销 / 关机：照正常退出收尾（给 Tab5 发 BYE）。SessionEnded 是「真的要结束了」；SessionEnding 只是在问能不能结束，
        // 人可能取消，在那里收尾的话程序还活着却不再工作了。上面已经建过控件，这个事件会转到界面线程上来，处理完系统才接着往下走
        SystemEvents.SessionEnded += OnSessionEnded;
        // 任务栏换了深浅色、主显示器的缩放比例变了：图标要照新的重画（不然要等到下一次状态变化）
        SystemEvents.UserPreferenceChanged += OnLookChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Refresh();
    }

    private void OnLookChanged(object? sender, UserPreferenceChangedEventArgs e) => QueueRefresh();

    private void OnDisplayChanged(object? sender, EventArgs e) => QueueRefresh();

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        _log.Write("注销 / 关机");
        Exit();
    }

    bool ITrayPrompts.Confirm(string text, string verb) => Dialog.Confirm(_form, text, verb);

    string? ITrayPrompts.AskPassword() => Dialog.AskPassword(_form);

    void ITrayPrompts.Info(string text) => Dialog.Info(_form, text);

    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Card;
        public override Color MenuBorder => Theme.Edge;
        public override Color MenuItemBorder => Theme.CardHover;
        public override Color MenuItemSelected => Theme.CardHover;
        public override Color ImageMarginGradientBegin => Theme.Card;
        public override Color ImageMarginGradientMiddle => Theme.Card;
        public override Color ImageMarginGradientEnd => Theme.Card;
    }

    /// <summary>哪个线程都能调：把设置窗口打开（又起了一份程序时，先起的那一份被叫来做这件事）。</summary>
    public void RequestShow() => Post(ShowSettings);

    /// <summary>哪个线程都能调：正常退出（--exit 叫先起的那一份做这件事）。</summary>
    public void RequestExit() => Post(Exit);

    // 转到界面线程上做，不等。界面线程已经收摊了（退出的最后一刻，又有人起了一份或者叫它退出）Post 会抛 InvalidOperationException：
    // 那时已经没有什么可做的，不能让它变成线程池线程上没人接的异常
    private void Post(Action action)
    {
        try
        {
            _ui.Post(_ => action(), null);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void QueueRefresh()
    {
        // 状态一连变好几次时只排一次：界面线程到时候看的是最新的
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
            Post(() =>
            {
                Volatile.Write(ref _refreshQueued, 0);
                Refresh();
            });
    }

    private static bool AutoStartOn() => AutoStart.IsEnabled(AutoStartName, Application.ExecutablePath);

    private void Refresh()
    {
        if (_exiting) return;
        HostStatus status = _host.Status;
        bool hasPassword = _host.HasPassword, stopped = _host.PairingStopped;
        (TrayIcon kind, string tip) = TrayView.Tray(status, hasPassword, stopped);
        bool light = Theme.TaskbarIsLight();
        int size = Icons.TraySize();
        if (!_icons.TryGetValue((kind, light, size), out Icon? icon)) _icons[(kind, light, size)] = icon = Icons.Make(kind, size, light);
        _icon.Icon = icon;
        _icon.Text = tip;
        _form?.SetContent(TrayView.Build(status, hasPassword, stopped, _host.Peers, AutoStartOn()));
    }

    public void ShowSettings()
    {
        if (_exiting) return;
        if (_form is null)
        {
            _form = new SettingsForm();
            _form.Panel.RowClicked += OnRow;
            _form.FormClosed += (_, _) => _form = null;
            Refresh();
            _form.Show();
        }
        _form.Activate();
    }

    private void OnRow(SettingsRow row)
    {
        _actions.Run(row);
        Refresh();
    }

    private void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        _icon.Visible = false;
        _form?.Close();
        _host.Dispose();        // 正连着的那条正常收尾（给 Tab5 发 BYE）
        _pump.Dispose();        // 按着的键松开
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.SessionEnded -= OnSessionEnded;
            SystemEvents.UserPreferenceChanged -= OnLookChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            _icon.Dispose();
            _menu.Dispose();
            foreach (Icon icon in _icons.Values) icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
