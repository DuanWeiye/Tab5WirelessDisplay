using System.Diagnostics;
using System.Runtime.InteropServices;
using WinDisplay.Core;

namespace WinDisplay.Win;

/// <summary>
/// 注入自检：在主显示器中间开一个自己的小窗口，只往它身上注入触摸、鼠标动作和按键，看它收到的是不是注入的那些
/// （位置对不对、扫描码对不对、连发的次数、按着不动会不会被系统收走、作废的触点会不会变成点击、主机的键盘指示灯读不读得到；
/// 触摸板模式的那几样：光标挪到的是不是要的那个像素、左键、右键、滚轮的方向、拖动）。
/// 走的就是投屏时用的那条路（<see cref="InputPump"/> → <see cref="InputBridge"/> → <see cref="InputInjector"/>），最前面几项直接用注入器。
/// <para>
/// 要在登录用户的桌面会话里跑，而且要这台电脑空闲着：它会把焦点抢到自己的窗口上、往里面打几个字、
/// 把 Caps Lock 和 Num Lock 各按几下（结束时照指示灯的实际状态补回开始时的样子）、
/// 把光标沿屏幕正中横竖各挪一遍（只是经过，不点；结束时放回开始的地方）。
/// 最近 <see cref="MinIdle"/> 之内有人动过键盘鼠标就不跑；锁屏着、开着 UAC 提示或者屏保时也不跑（那时接收输入的不是用户的桌面）。
/// 触摸和鼠标的点击、滚轮只在那个位置上是自己的窗口时才注入，按键只在自己的窗口是前台窗口的时候才注入（按住的那一项每 50 毫秒看一次，焦点没了马上松开）。
/// </para>
/// </summary>
public static class InjectCheck
{
    /// <summary>键盘鼠标至少空闲这么久才跑。</summary>
    public static readonly TimeSpan MinIdle = TimeSpan.FromSeconds(10);

    private const int Width = 480, Height = 320;
    private const uint WmDestroy = 0x0002, WmClose = 0x0010, WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmChar = 0x0102, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    private const uint WmPointerUpdate = 0x0245, WmPointerDown = 0x0246, WmPointerUp = 0x0247, WmPointerCaptureChanged = 0x024C;
    private const uint WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, WmContextMenu = 0x007B;
    private const uint WmMouseMove = 0x0200, WmLButtonDown = 0x0201, WmRButtonDown = 0x0204, WmMouseWheel = 0x020A, WmMouseHWheel = 0x020E;
    private const long MkLButton = 0x0001;
    private const uint WsPopup = 0x80000000, WsVisible = 0x10000000, WsBorder = 0x00800000, WsExTopmost = 0x00000008, WsExToolWindow = 0x00000080;
    private const int PointerMessageCanceled = 0x8000;
    private const uint WmPowerBroadcast = 0x0218;
    private const int PbtPowerSettingChange = 0x8013;
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");     // GUID_CONSOLE_DISPLAY_STATE

    private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size, Style;
        public IntPtr Proc;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size, Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X, Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WndClassEx cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h,
                                                 IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Msg msg, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Msg msg);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, in Guid setting, uint flags);
    [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(ScreenPoint point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref ScreenPoint point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out ScreenPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string? device, int mode, IntPtr devMode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformationW(IntPtr handle, int index, [Out] char[] info, int bytes, out int needed);

    // Click = 鼠标键松开或者弹右键菜单。触摸那几项里出现它 = 系统把这次触摸折成了鼠标点击交给窗口（不处理触摸消息的程序收到的就是它）；
    // 鼠标那几项里它就是注入的那一下松开。MouseDown / MouseMove / Wheel 只在鼠标那几项里看
    private enum Kind { TouchDown, TouchMove, TouchUp, TouchLost, KeyDown, KeyUp, Char, Click, MouseDown, MouseMove, Wheel }

    /// <summary>窗口收到的一件事。触摸：Id = 系统给的指针编号，A / B = 屏幕坐标，Flag = 带「取消」标记（这次触摸作废了，不是抬起）。
    /// 按键：Id = 系统认成的虚拟键码，A = 扫描码，B = 窗口线程这时读到的指示灯状态，Flag = 带扩展前缀；Repeat = 是连发的。字符：A = 字符。
    /// 鼠标键按下 / 松开：Id = 哪条消息，A / B = 屏幕坐标。鼠标移动：A / B = 屏幕坐标，Flag = 左键按着。滚轮：A = 滚了多少，Flag = 横向的。</summary>
    private readonly record struct Seen(Kind Kind, uint Id, int A, int B, bool Flag, bool Repeat = false);

    /// <summary>自检用的窗口：在自己的线程上收消息，把收到的触摸和按键记下来。</summary>
    private sealed class Probe : IDisposable
    {
        private readonly List<Seen> _seen = [];
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private readonly WndProc _proc;         // 留着引用：窗口还在的时候不能被回收
        private string? _error;

        private volatile int _display = -1;
        private IntPtr _powerNotify;

        public IntPtr Window { get; private set; }
        public int Left { get; }
        public int Top { get; }

        /// <summary>显示器现在的状态（系统告诉这个窗口的）：0 = 睡着，1 = 亮着，2 = 调暗了，-1 = 还不知道。</summary>
        public int Display => _display;

        public Probe()
        {
            _proc = Handle;
            Left = (GetSystemMetrics(0) - Width) / 2;       // 主显示器的宽、高（进程按显示器感知 DPI，所以是物理像素）
            Top = (GetSystemMetrics(1) - Height) / 2;
            _thread = new Thread(Pump) { IsBackground = true, Name = "注入自检的窗口" };
            _thread.Start();
            _ready.Wait();
            if (_error is not null) throw new InvalidOperationException(_error);
        }

        private void Pump()
        {
            IntPtr instance = GetModuleHandleW(null);
            string name = $"WinDisplayInjectCheck{Environment.ProcessId}";
            var cls = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(), Proc = Marshal.GetFunctionPointerForDelegate(_proc), Instance = instance,
                Background = 6,     // COLOR_WINDOW + 1
                ClassName = name,
            };
            if (RegisterClassExW(ref cls) == 0)
            {
                _error = $"注册窗口类没成（错误 {Marshal.GetLastWin32Error()}）";
            }
            else
            {
                // 置顶、不进任务栏的小窗口；先不抢焦点，等第一下触摸把它点成前台
                Window = CreateWindowExW(WsExTopmost | WsExToolWindow, name, "WinDisplay 注入自检", WsPopup | WsVisible | WsBorder,
                                         Left, Top, Width, Height, 0, 0, instance, 0);
                if (Window == 0) _error = $"开不出自检的窗口（错误 {Marshal.GetLastWin32Error()}）：要在登录用户的桌面会话里跑";
                // 登记之后系统马上发一条现在的状态过来，之后变了再发
                else _powerNotify = RegisterPowerSettingNotification(Window, in ConsoleDisplayState, 0);
            }
            _ready.Set();
            if (_error is not null) return;
            while (GetMessageW(out Msg msg, 0, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }

        private IntPtr Handle(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            long w = wParam.ToInt64(), l = lParam.ToInt64();
            switch (message)
            {
                case WmPointerDown or WmPointerUpdate or WmPointerUp:
                    Add(new Seen(message == WmPointerDown ? Kind.TouchDown : message == WmPointerUp ? Kind.TouchUp : Kind.TouchMove,
                                 (uint)(w & 0xFFFF), (short)(l & 0xFFFF), (short)(l >> 16 & 0xFFFF), (w >> 16 & PointerMessageCanceled) != 0));
                    break;
                case WmPointerCaptureChanged:
                    Add(new Seen(Kind.TouchLost, (uint)(w & 0xFFFF), 0, 0, true));
                    break;
                case WmKeyDown or WmSysKeyDown or WmKeyUp or WmSysKeyUp:
                    bool down = message is WmKeyDown or WmSysKeyDown;
                    Add(new Seen(down ? Kind.KeyDown : Kind.KeyUp, (uint)(w & 0xFFFF), (int)(l >> 16 & 0xFF), InputInjector.HostLeds(), (l >> 24 & 1) != 0,
                                 down && (l >> 30 & 1) != 0));
                    break;
                case WmChar:
                    Add(new Seen(Kind.Char, 0, (int)w, 0, false));
                    break;
                case WmContextMenu:
                    Add(new Seen(Kind.Click, message, 0, 0, true));
                    break;
                case WmLButtonUp or WmRButtonUp or WmLButtonDown or WmRButtonDown or WmMouseMove:
                    // 这几条消息里的位置是窗口客户区里的，换成屏幕坐标
                    var at = new ScreenPoint { X = (short)(l & 0xFFFF), Y = (short)(l >> 16 & 0xFFFF) };
                    ClientToScreen(window, ref at);
                    Add(message switch
                    {
                        WmMouseMove => new Seen(Kind.MouseMove, message, at.X, at.Y, (w & MkLButton) != 0),
                        WmLButtonDown or WmRButtonDown => new Seen(Kind.MouseDown, message, at.X, at.Y, message == WmRButtonDown),
                        _ => new Seen(Kind.Click, message, at.X, at.Y, message != WmLButtonUp),
                    });
                    break;
                case WmMouseWheel or WmMouseHWheel:
                    Add(new Seen(Kind.Wheel, message, (short)(w >> 16 & 0xFFFF), 0, message == WmMouseHWheel));
                    break;
                case WmPowerBroadcast when w == PbtPowerSettingChange && lParam != 0:
                    // POWERBROADCAST_SETTING：16 字节的设置编号、4 字节的数据长度、数据
                    if (Marshal.PtrToStructure<Guid>(lParam) == ConsoleDisplayState && Marshal.ReadInt32(lParam, 16) >= 4)
                        _display = Marshal.ReadInt32(lParam, 20);
                    break;
                case WmDestroy:
                    if (_powerNotify != 0) UnregisterPowerSettingNotification(_powerNotify);
                    PostQuitMessage(0);
                    break;
            }
            // 触摸的消息也交给系统的默认处理：它负责把被点到的窗口变成前台窗口
            return DefWindowProcW(window, message, wParam, lParam);
        }

        private void Add(Seen s)
        {
            lock (_seen) _seen.Add(s);
        }

        /// <summary>到现在收到了多少件事（之后用它切出「这一步里收到的」）。</summary>
        public int Mark()
        {
            lock (_seen) return _seen.Count;
        }

        public List<Seen> Since(int mark)
        {
            lock (_seen) return _seen.GetRange(mark, _seen.Count - mark);
        }

        /// <summary>等到 mark 之后收到的事里有满足条件的（最多等这么多毫秒）。</summary>
        public bool Wait(int mark, Func<List<Seen>, bool> found, int timeoutMs = 1000) => SpinWait.SpinUntil(() => found(Since(mark)), timeoutMs);

        public bool IsForeground => GetForegroundWindow() == Window;

        /// <summary>屏幕上这一点最上面的窗口是不是它（上面盖着别的置顶窗口就不是）。</summary>
        public bool Covers(int x, int y) => WindowFromPoint(new ScreenPoint { X = x, Y = y }) == Window;

        /// <summary>它所在的显示器的缩放比例（百分数）。</summary>
        public int ScalePercent => (int)(GetDpiForWindow(Window) * 100 / 96);

        public bool TakeForeground() => IsForeground || (SetForegroundWindow(Window) && SpinWait.SpinUntil(() => IsForeground, 500));

        public void Dispose()
        {
            if (Window != 0) PostMessageW(Window, WmClose, 0, 0);
            _thread.Join(2000);
            _ready.Dispose();
        }
    }

    /// <summary>键盘鼠标空闲了多久（这个桌面会话里；别的程序注入的输入也算输入）。</summary>
    public static TimeSpan Idle()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.Zero;
    }

    /// <summary>
    /// 现在接收输入的是不是用户自己的桌面。锁屏、UAC 提示、屏保各有各的桌面：那时普通权限的程序打不开它，或者打开了名字不是 Default。
    /// </summary>
    public static bool OnUserDesktop()
    {
        IntPtr desktop = OpenInputDesktop(0, false, 0x0001);        // DESKTOP_READOBJECTS
        if (desktop == 0) return false;
        try
        {
            var name = new char[64];
            if (!GetUserObjectInformationW(desktop, 2, name, name.Length * sizeof(char), out _)) return false;     // UOI_NAME
            int end = Array.IndexOf(name, '\0');
            return new string(name, 0, end < 0 ? name.Length : end).Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    /// <summary>主显示器现在的显示模式的分辨率（物理像素，不随进程的 DPI 感知方式变）。读不到给 (0, 0)。</summary>
    private static (int W, int H) DisplayMode()
    {
        const int Size = 220, SizeAt = 68, WidthAt = 172, HeightAt = 176;       // DEVMODEW 整个 220 字节，这里只用 dmSize、dmPelsWidth、dmPelsHeight
        IntPtr mode = Marshal.AllocHGlobal(Size);
        try
        {
            for (int i = 0; i < Size; i++) Marshal.WriteByte(mode, i, 0);
            Marshal.WriteInt16(mode, SizeAt, Size);
            return EnumDisplaySettingsW(null, -1, mode) ? (Marshal.ReadInt32(mode, WidthAt), Marshal.ReadInt32(mode, HeightAt)) : (0, 0);      // -1 = 现在的模式
        }
        finally
        {
            Marshal.FreeHGlobal(mode);
        }
    }

    /// <summary>
    /// 跑一遍，逐项用 say 报出来（「[符合] …」/「[不符] …」/「[数字] …」），返回是不是全部符合。
    /// </summary>
    /// <exception cref="InvalidOperationException">跑不了：有人在用这台电脑、锁屏着、不在桌面会话里、系统不让注入。</exception>
    public static bool Run(Action<string> say)
    {
        InputInjector.UsePhysicalPixels();
        if (!OnUserDesktop())
            throw new InvalidOperationException("现在接收输入的不是用户的桌面（锁屏着、开着 UAC 提示或者屏保，或者这不是登录用户的桌面会话）：注入自检要在解了锁的桌面上跑");
        TimeSpan idle = Idle();
        if (idle < MinIdle)
            throw new InvalidOperationException($"有人在用这台电脑（{idle.TotalSeconds:F0} 秒前有键盘 / 鼠标输入）：注入自检要抢焦点、往自己的窗口里打几个字，" +
                                                $"空闲 {MinIdle.TotalSeconds:F0} 秒以上再跑");
        var injector = new InputInjector();
        byte ledsBefore = InputInjector.HostLeds();         // 结束时 Caps Lock、Num Lock 要补回这个样子
        var warnings = new List<string>();
        injector.Warning += w => { lock (warnings) warnings.Add(w); };
        int bad = 0;
        void Item(string name, bool ok, string note = "")
        {
            if (!ok) bad++;
            say($"[{(ok ? "符合" : "不符")}] {name}{(note.Length > 0 ? "：" + note : "")}");
        }

        using var probe = new Probe();
        int screenW = GetSystemMetrics(0), screenH = GetSystemMetrics(1);
        int cx = probe.Left + Width / 2, cy = probe.Top + Height / 2;
        Thread.Sleep(300);          // 等窗口显示出来
        IInputSink sink = injector;

        // 屏幕坐标是不是物理像素：系统报给这个进程的主显示器尺寸要和显示模式的分辨率一样。进程没按显示器感知 DPI 的话前者是按缩放比例折算过的，
        // 而投屏时触点的位置是照抓到的画面（物理像素）算的，会落到错的地方——这种错在自检自己的窗口上看不出来（窗口和注入用的是同一套坐标）
        var (modeW, modeH) = DisplayMode();
        Item("屏幕坐标是物理像素：系统报的主显示器尺寸和显示模式的分辨率一样", modeW == screenW && modeH == screenH,
             $"系统报的 {screenW}×{screenH}，显示模式 {modeW}×{modeH}，缩放 {probe.ScalePercent}%");

        // 注入触摸之前看一眼那几个点上是不是自检的窗口：上面盖着别的置顶窗口（通知、通话的小窗）的话，点下去就点到别人身上了
        bool Clear(string name, params (int X, int Y)[] points)
        {
            if (points.All(p => probe.Covers(p.X, p.Y))) return true;
            Item(name, false, "要注入的位置上盖着别的窗口，这一项没有注入");
            return false;
        }

        // 0. 显示器睡着的话先点一下把它叫醒：把显示器叫醒的那一下触摸，系统不交给窗口（平板上点一下黑着的屏只是亮屏，是同一个规矩）
        int display = probe.Display;
        if (display == 0 && probe.Covers(cx, cy))
        {
            int wake = probe.Mark();
            sink.Touch([new Contact(0, cx, cy, ContactPhase.Down)]);
            Thread.Sleep(40);
            sink.Touch([new Contact(0, cx, cy, ContactPhase.Up)]);
            bool woke = SpinWait.SpinUntil(() => probe.Display == 1, 3000);
            Thread.Sleep(500);
            say($"[数字] 显示器睡着：先点一下，{(woke ? "它亮了" : "3 秒还没亮")}；这一下窗口{(probe.Since(wake).Any(e => e.Kind == Kind.TouchDown) ? "收到了" : "没收到")}");
        }
        else
        {
            say($"[数字] 显示器{display switch { 0 => "睡着（要点的位置上盖着别的窗口，没有去叫醒）", 1 => "亮着", 2 => "调暗了", _ => "的状态系统没告诉" }}");
        }

        // 1. 点一下：窗口在注入的那个像素上收到按下和抬起（位置不对 = 坐标被系统按缩放比例折算过）
        int mark;
        bool sent;
        List<Seen> got;
        const string TapName = "点一下：窗口在注入的那个像素上收到按下和抬起";
        int tapClicks = -1;         // 这一下被系统折成了几次鼠标点击（给第 2b 项当对照）；-1 = 这一项没做
        if (Clear(TapName, (cx, cy)))
        {
            mark = probe.Mark();
            sent = sink.Touch([new Contact(0, cx, cy, ContactPhase.Down)]);
            Thread.Sleep(40);
            sent &= sink.Touch([new Contact(0, cx, cy, ContactPhase.Move)]);
            Thread.Sleep(40);
            sent &= sink.Touch([new Contact(0, cx, cy, ContactPhase.Up)]);
            probe.Wait(mark, s => s.Any(e => e.Kind == Kind.TouchUp));
            Thread.Sleep(300);      // 折出来的鼠标点击比抬起晚一点到
            got = probe.Since(mark);
            tapClicks = got.Count(e => e.Kind == Kind.Click);
            Seen? tapDown = got.Cast<Seen?>().FirstOrDefault(e => e!.Value.Kind == Kind.TouchDown);
            Item(TapName, sent && tapDown is { A: var dx, B: var dy } && dx == cx && dy == cy && got.Any(e => e is { Kind: Kind.TouchUp, Flag: false }),
                 $"注入在 ({cx},{cy})，窗口收到按下在 {(tapDown is { } d ? $"({d.A},{d.B})" : "（没收到）")}；屏幕 {screenW}×{screenH}");
        }

        // 2. 两根手指先后按下、各自移动、先后抬起
        const string TwoName = "两根手指：窗口收到两个触点，各在各的位置按下、都正常抬起";
        if (Clear(TwoName, (cx - 60, cy), (cx + 60, cy), (cx - 70, cy + 10), (cx + 70, cy + 10)))
        {
            mark = probe.Mark();
            sent = sink.Touch([new Contact(0, cx - 60, cy, ContactPhase.Down)]);
            Thread.Sleep(30);
            sent &= sink.Touch([new Contact(0, cx - 60, cy, ContactPhase.Move), new Contact(1, cx + 60, cy, ContactPhase.Down)]);
            Thread.Sleep(30);
            sent &= sink.Touch([new Contact(0, cx - 70, cy + 10, ContactPhase.Move), new Contact(1, cx + 70, cy + 10, ContactPhase.Move)]);
            Thread.Sleep(30);
            sent &= sink.Touch([new Contact(1, cx + 70, cy + 10, ContactPhase.Move), new Contact(0, cx - 70, cy + 10, ContactPhase.Up)]);
            Thread.Sleep(30);
            sent &= sink.Touch([new Contact(1, cx + 70, cy + 10, ContactPhase.Up)]);
            probe.Wait(mark, s => s.Count(e => e.Kind == Kind.TouchUp) >= 2);
            got = probe.Since(mark);
            var downs = got.Where(e => e.Kind == Kind.TouchDown).Select(e => (e.A, e.B)).ToList();
            Item(TwoName, sent && downs.Contains((cx - 60, cy)) && downs.Contains((cx + 60, cy))
                 && got.Count(e => e is { Kind: Kind.TouchUp, Flag: false }) == 2 && got.Where(e => e.Kind == Kind.TouchDown).Select(e => e.Id).Distinct().Count() == 2,
                 $"按下 {string.Join(" ", downs.Select(p => $"({p.A},{p.B})"))}，抬起 {got.Count(e => e.Kind == Kind.TouchUp)} 次");
        }

        // 2b. 作废一个触点（UP | CANCELED）：系统收下，窗口收到的是取消、不是抬起，也不折成鼠标点击。对照：上面第 1 项点一下折成了几次点击
        const string CancelName = "作废一个按着的触点：窗口收到的是取消，不是抬起，也没有变成点击";
        if (Clear(CancelName, (cx, cy)))
        {
            mark = probe.Mark();
            sent = sink.Touch([new Contact(0, cx, cy, ContactPhase.Down)]);
            Thread.Sleep(40);
            sent &= sink.Touch([new Contact(0, cx, cy, ContactPhase.Move)]);
            Thread.Sleep(40);
            bool taken = sink.Touch([new Contact(0, cx, cy, ContactPhase.Cancel)]);
            probe.Wait(mark, s => s.Any(e => e.Kind is Kind.TouchUp or Kind.TouchLost), 1500);
            Thread.Sleep(300);
            got = probe.Since(mark);
            Item(CancelName, sent && taken && got.Any(e => e is { Kind: Kind.TouchUp, Flag: true } || e.Kind == Kind.TouchLost)
                 && !got.Any(e => e is { Kind: Kind.TouchUp, Flag: false }) && !got.Any(e => e.Kind == Kind.Click),
                 $"系统{(taken ? "收下了" : "没收下")}；窗口收到 {string.Join(" ", got.Where(e => e.Kind is Kind.TouchUp or Kind.TouchLost or Kind.Click).Select(e => e.Kind switch { Kind.TouchUp => e.Flag ? "取消" : "抬起", Kind.TouchLost => "失去触点", _ => $"点击({e.Id:X})" }))}" +
                 $"；对照：第 1 项点一下折成了 {(tapClicks < 0 ? "？" : tapClicks)} 次点击");
            if (!taken) sink.Touch([new Contact(0, cx, cy, ContactPhase.Up)]);
        }

        // 3. 按着不动、不重报，系统多久把它收走、收走时给窗口的是什么（只是量出来：重报的间隔要比它短得多）
        int survived = 0, cancelledAt = 0;
        string takenAs = "";
        foreach (int hold in new[] { 250, 500, 1000, 2000, 5000 })
        {
            mark = probe.Mark();
            if (!probe.Covers(cx, cy) || !sink.Touch([new Contact(0, cx, cy, ContactPhase.Down)])) break;
            Thread.Sleep(hold);
            var ends = probe.Since(mark).Where(e => e.Kind is Kind.TouchLost or Kind.TouchUp).ToList();     // 我们还没注入抬起：有的话是系统收走的
            bool up = sink.Touch([new Contact(0, cx, cy, ContactPhase.Up)]);
            Thread.Sleep(150);
            if (ends.Count > 0 || !up)
            {
                cancelledAt = hold;
                takenAs = ends.Count == 0 ? "什么都没收到" : ends.Any(e => e is { Kind: Kind.TouchUp, Flag: false }) ? "一次普通的抬起" : "取消";
                break;
            }
            survived = hold;
        }
        say($"[数字] 按着不动、不重报：{(survived > 0 ? $"{survived} 毫秒没事" : "250 毫秒都撑不住")}" +
            (cancelledAt > 0 ? $"，{cancelledAt} 毫秒时已经被系统收走（窗口收到的是{takenAs}）" : "，最长试到 5 秒都没有被收走"));

        // 4. 照投屏时的做法（按着不动就隔一阵重报）按 3 秒：不被系统收走，抬起在原地
        ViewFit fit = ViewFit.For(screenW, screenH);
        var bridge = new InputBridge(injector, () => fit, InputInjector.HostKeyRepeat, InputInjector.TouchKeepAliveMs);
        var ledLog = new List<byte>();
        var faults = new List<string>();
        using var pump = new InputPump(bridge, InputInjector.HostLeds);
        pump.LedsChanged += v => { lock (ledLog) ledLog.Add(v); };
        pump.Faulted += e => { lock (faults) faults.Add(e.Message); };
        pump.Start();
        // 照 Tab5 的做法按着这么久：按着的时候它隔一阵报一次「还按着」（协议「按着的时候」），不报的话过 0.7 秒连发和重报就停了。
        // heard = false：当成 Tab5 没了消息，只等、不报「还按着」。
        // 每 50 毫秒看一次 still（不给 = 不看）：不成立了就提前结束，返回 false
        bool Hold(double ms, Func<bool>? still = null, bool heard = true)
        {
            long start = Stopwatch.GetTimestamp();
            while (true)
            {
                if (still is not null && !still()) return false;
                double left = ms - Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (left <= 0) return true;
                Thread.Sleep((int)Math.Ceiling(Math.Min(left, 50)));
                if (heard) pump.Held();
            }
        }

        var finger = new TouchPoint(0, (ushort)(fit.X + (long)cx * fit.W / fit.SrcW), (ushort)(fit.Y + (long)cy * fit.H / fit.SrcH));
        Contact want = new TouchMapper(fit).Apply([finger])[0];
        string heldName = $"按着不动 3 秒（每 {InputInjector.TouchKeepAliveMs:F0} 毫秒重报一次）：没有被系统收走，按下和抬起都在换算出来的位置";
        if (Clear(heldName, (want.X, want.Y)))
        {
            mark = probe.Mark();
            pump.Touch([finger]);
            Hold(3000);
            // 我们还没注入抬起：这时窗口收到抬起 / 取消 / 失去触点，都是系统把它收走了（第 3 项量到的：收走时给的是一次普通的抬起）
            bool lostHeld = probe.Since(mark).Any(e => e.Kind is Kind.TouchLost or Kind.TouchUp);
            pump.Touch([]);
            probe.Wait(mark, s => s.Any(e => e.Kind == Kind.TouchUp));
            got = probe.Since(mark);
            Item(heldName, !lostHeld && got.Any(e => e.Kind == Kind.TouchDown && e.A == want.X && e.B == want.Y)
                 && got.Any(e => e is { Kind: Kind.TouchUp, Flag: false } && e.A == want.X && e.B == want.Y),
                 $"横屏画面里的 ({finger.X},{finger.Y}) → 屏幕 ({want.X},{want.Y})；期间窗口收到 {got.Count(e => e.Kind == Kind.TouchMove)} 次移动");

            // 4b. Tab5 突然没了（不再报「还按着」，连接还没断）：过 0.7 秒这根手指作废——窗口收到的是取消，不是抬起，也没有变成点击
            mark = probe.Mark();
            pump.Touch([finger]);
            Thread.Sleep(Proto.HeldStaleMs + 2000);
            got = probe.Since(mark);
            bool cancelled = got.Any(e => e.Kind == Kind.TouchLost || (e.Kind == Kind.TouchUp && e.Flag));
            Item("按着的时候 Tab5 没了消息：这根手指作废，窗口收到的是取消，不是抬起，也没有变成点击",
                 cancelled && !got.Any(e => e is { Kind: Kind.TouchUp, Flag: false }) && !got.Any(e => e.Kind == Kind.Click),
                 $"窗口收到 {got.Count(e => e.Kind == Kind.TouchMove)} 次移动之后：{(cancelled ? "取消" : got.Any(e => e.Kind == Kind.TouchUp) ? "抬起" : "什么都没收到")}，" +
                 $"折成点击 {got.Count(e => e.Kind == Kind.Click)} 次");
            pump.Touch([]);         // 这根手指已经作废，抬起不会再注入

            // 4c. 连接结束时手指还按着：同样是作废，不在最后的位置上点一下
            mark = probe.Mark();
            pump.Touch([finger]);
            Hold(300);
            pump.ReleaseAll();
            probe.Wait(mark, s => s.Any(e => e.Kind is Kind.TouchUp or Kind.TouchLost));
            Thread.Sleep(300);
            got = probe.Since(mark);
            Item("连接结束时手指还按着：窗口收到的是取消，不是抬起，也没有变成点击",
                 got.Any(e => e.Kind == Kind.TouchLost || (e.Kind == Kind.TouchUp && e.Flag)) && !got.Any(e => e is { Kind: Kind.TouchUp, Flag: false })
                 && !got.Any(e => e.Kind == Kind.Click), $"折成点击 {got.Count(e => e.Kind == Kind.Click)} 次");
        }

        // 4d. 触摸板模式的鼠标动作。光标要挪来挪去：结束时放回开始的地方
        bool cursorKnown = GetCursorPos(out ScreenPoint cursorBefore);
        try
        {
            ScreenPoint Cursor()
            {
                GetCursorPos(out ScreenPoint p);
                return p;
            }
            bool CursorAt(int x, int y, int timeoutMs = 200) => SpinWait.SpinUntil(() =>
            {
                ScreenPoint p = Cursor();
                return p.X == x && p.Y == y;
            }, timeoutMs);
            // 把光标摆到 (x, y)（不经过注入器），再等到注入器不再接着它上一次挪到的地方算：下一次挪是从系统报的位置起算的
            bool Place(int x, int y)
            {
                SetCursorPos(x, y);
                Thread.Sleep((int)InputInjector.CursorTrustMs + 100);
                return CursorAt(x, y);
            }
            string Where()
            {
                ScreenPoint p = Cursor();
                return $"({p.X},{p.Y})";
            }

            // 光标一个像素一个像素地挪过整个屏幕（沿屏幕正中，横、竖各一遍）：每一步读回来都正好是那个像素。
            // 注入器是按绝对位置挪的（位置换成系统要的 0~65535），换算差一点就会有的像素到不了、或者慢慢挪的时候原地不动
            int Sweep(bool across, out string firstWrong)
            {
                firstWrong = "";
                int wrong = 0, n = across ? screenW : screenH;
                for (int i = 1; i < n && wrong < 20; i++)       // 错了 20 处就不往下试了（每一处要等 100 毫秒）
                {
                    sink.Mouse([across ? new MouseAction(MouseKind.Move, 1, 0) : new MouseAction(MouseKind.Move, 0, 1)]);
                    int x = across ? i : cx, y = across ? cy : i;
                    if (CursorAt(x, y, 100)) continue;
                    if (wrong++ == 0) firstWrong = $"该在 ({x},{y})，读到 {Where()}";
                }
                return wrong;
            }
            bool placed = Place(0, cy);
            int wrongX = Sweep(true, out string firstX);
            sink.Mouse([new MouseAction(MouseKind.Move, 50, 0)]);           // 到了右边缘再往右：停在最后一个像素上
            bool edge = CursorAt(screenW - 1, cy);
            placed &= Place(cx, 0);
            int wrongY = Sweep(false, out string firstY);
            sink.Mouse([new MouseAction(MouseKind.Move, 0, 50)]);
            edge &= CursorAt(cx, screenH - 1);
            Item("光标一个像素一个像素地挪过整个屏幕（横、竖各一遍）：每一步读回来都正好是那个像素，到了边上不出去",
                 placed && edge && wrongX == 0 && wrongY == 0,
                 $"横向 {screenW - 1} 步错 {wrongX}{(wrongX >= 20 ? "+" : "")} 处{(firstX.Length > 0 ? $"（头一处：{firstX}）" : "")}，" +
                 $"纵向 {screenH - 1} 步错 {wrongY}{(wrongY >= 20 ? "+" : "")} 处{(firstY.Length > 0 ? $"（头一处：{firstY}）" : "")}" +
                 (edge ? "" : "；到了边上再挪，光标不在最后一个像素上") + (placed ? "" : "；光标摆不到起点"));

            // 隔了一阵（人动过真鼠标）之后再挪：从光标现在的位置起算，不是从注入器上一次挪到的地方
            placed = Place(cx - 40, cy - 30);
            sink.Mouse([new MouseAction(MouseKind.Move, 25, 15)]);
            bool resumed = CursorAt(cx - 15, cy - 15);
            Item("隔了一阵、光标被别人挪过之后再挪：从光标现在的位置起算", placed && resumed, $"从 ({cx - 40},{cy - 30}) 挪 (25,15)，读到 {Where()}");

            // 左键点一下：窗口在光标的位置收到按下和松开
            const string LeftName = "鼠标左键点一下：窗口在光标的位置收到左键的按下和松开";
            if (Place(cx, cy) && Clear(LeftName, (cx, cy)))
            {
                mark = probe.Mark();
                sink.Mouse([new MouseAction(MouseKind.LeftDown), new MouseAction(MouseKind.LeftUp)]);
                probe.Wait(mark, s => s.Any(e => e is { Kind: Kind.Click, Id: WmLButtonUp }));
                got = probe.Since(mark);
                Item(LeftName, got.Any(e => e is { Kind: Kind.MouseDown, Id: WmLButtonDown } && e.A == cx && e.B == cy)
                     && got.Any(e => e is { Kind: Kind.Click, Id: WmLButtonUp } && e.A == cx && e.B == cy) && !got.Any(e => e.Id is WmRButtonDown or WmRButtonUp),
                     Buttons(got));
            }

            // 右键点一下
            const string RightName = "鼠标右键点一下：窗口在光标的位置收到右键的按下和松开";
            if (Place(cx, cy) && Clear(RightName, (cx, cy)))
            {
                mark = probe.Mark();
                sink.Mouse([new MouseAction(MouseKind.RightDown), new MouseAction(MouseKind.RightUp)]);
                probe.Wait(mark, s => s.Any(e => e is { Kind: Kind.Click, Id: WmRButtonUp }));
                got = probe.Since(mark);
                Item(RightName, got.Any(e => e is { Kind: Kind.MouseDown, Id: WmRButtonDown } && e.A == cx && e.B == cy)
                     && got.Any(e => e is { Kind: Kind.Click, Id: WmRButtonUp } && e.A == cx && e.B == cy) && !got.Any(e => e.Id is WmLButtonDown or WmLButtonUp),
                     Buttons(got));
            }

            // 滚轮：正的是往上滚、不够一格的也照数给；横向的正的是往右滚
            const string WheelName = "滚轮：窗口收到的方向和多少就是注入的（往上一格、往下四分之一格、往右一格）";
            if (Place(cx, cy) && Clear(WheelName, (cx, cy)))
            {
                mark = probe.Mark();
                sink.Mouse([new MouseAction(MouseKind.Wheel, 120)]);
                Thread.Sleep(30);
                sink.Mouse([new MouseAction(MouseKind.Wheel, -30)]);
                Thread.Sleep(30);
                sink.Mouse([new MouseAction(MouseKind.HWheel, 120)]);
                probe.Wait(mark, s => s.Count(e => e.Kind == Kind.Wheel) >= 3);
                var wheels = probe.Since(mark).Where(e => e.Kind == Kind.Wheel).Select(e => (e.Flag, e.A)).ToList();
                Item(WheelName, wheels.SequenceEqual([(false, 120), (false, -30), (true, 120)]),
                     "窗口收到 " + (wheels.Count == 0 ? "（没有滚轮）" : string.Join(" ", wheels.Select(v => $"{(v.Flag ? "横" : "竖")}{v.A:+0;-0}"))));
            }

            // 拖动：左键按着的时候挪光标，窗口收到带着左键的移动，松开在挪到的地方
            const string DragName = "拖动：左键按着挪光标，窗口收到按着左键的移动，松开在挪到的位置";
            if (Place(cx, cy) && Clear(DragName, (cx, cy), (cx + 30, cy + 20)))
            {
                mark = probe.Mark();
                sink.Mouse([new MouseAction(MouseKind.LeftDown)]);
                Thread.Sleep(30);
                sink.Mouse([new MouseAction(MouseKind.Move, 30, 20)]);
                Thread.Sleep(30);
                sink.Mouse([new MouseAction(MouseKind.LeftUp)]);
                probe.Wait(mark, s => s.Any(e => e is { Kind: Kind.Click, Id: WmLButtonUp }));
                got = probe.Since(mark);
                Item(DragName, got.Any(e => e is { Kind: Kind.MouseMove, Flag: true } && e.A == cx + 30 && e.B == cy + 20)
                     && got.Any(e => e is { Kind: Kind.Click, Id: WmLButtonUp } && e.A == cx + 30 && e.B == cy + 20),
                     Buttons(got) + $"；按着左键的移动 {got.Count(e => e is { Kind: Kind.MouseMove, Flag: true })} 次");
            }

            // 整条路（Tab5 的触点快照 → InputBridge 的触摸板模式 → 注入器），和托盘程序接的一样
            pump.Mode(true);            // Tab5 报：触摸板模式
            static TouchPoint Finger(byte id, int x, int y) => new(id, (ushort)x, (ushort)y);

            const string PadTapName = "触摸板模式，一根手指轻点：光标不动，窗口在光标的位置收到左键点一下；没有注入成触摸";
            if (Place(cx, cy) && Clear(PadTapName, (cx, cy)))
            {
                mark = probe.Mark();
                pump.Touch([Finger(0, 640, 360)]);
                Hold(60);
                pump.Touch([Finger(0, 643, 366)]);          // 轻点时指尖会滚动几个像素
                Hold(40);
                pump.Touch([]);
                probe.Wait(mark, s => s.Any(e => e is { Kind: Kind.Click, Id: WmLButtonUp }));
                Thread.Sleep(100);
                got = probe.Since(mark);
                Item(PadTapName, got.Count(e => e is { Kind: Kind.MouseDown, Id: WmLButtonDown } && e.A == cx && e.B == cy) == 1
                     && got.Count(e => e is { Kind: Kind.Click, Id: WmLButtonUp } && e.A == cx && e.B == cy) == 1
                     && !got.Any(e => e.Kind is Kind.TouchDown or Kind.TouchUp) && CursorAt(cx, cy, 0),
                     Buttons(got) + $"；光标在 {Where()}，该在 ({cx},{cy})；触摸消息 {got.Count(e => e.Kind is Kind.TouchDown or Kind.TouchUp)} 条");
            }

            // 一根手指慢慢滑：手指在 Tab5 的画面上挪多远，光标就在那幅画面上挪多远（换成显示器的像素）。每秒 100 像素，不到放大的速度
            Thread.Sleep((int)TouchpadMapper.DragArmMs + 150);      // 过了「轻点之后马上再按下 = 拖动」的那一会儿
            if (Place(cx, cy))
            {
                mark = probe.Mark();
                pump.Touch([Finger(0, 400, 360)]);
                Thread.Sleep(40);
                pump.Touch([Finger(0, 400 + TouchpadMapper.Slop + 10, 360)]);       // 先一下挪出「轻点」的范围：这一段不算
                for (int i = 1; i <= 25; i++)
                {
                    Thread.Sleep(40);
                    pump.Touch([Finger(0, 400 + TouchpadMapper.Slop + 10 + 4 * i, 360)]);
                }
                Thread.Sleep(40);
                pump.Touch([]);
                double expect = 100.0 * fit.SrcW / fit.W;
                Thread.Sleep(100);
                ScreenPoint end = Cursor();
                got = probe.Since(mark);
                Item("触摸板模式，一根手指慢慢滑 100 像素：光标横着挪了画面上的 100 像素那么远，没有点击",
                     Math.Abs(end.X - cx - expect) <= 1 && end.Y == cy && !got.Any(e => e.Kind is Kind.MouseDown or Kind.Click),
                     $"光标从 ({cx},{cy}) 到 ({end.X},{end.Y})，照算是横着挪 {expect:F1} 个显示器像素；{Buttons(got)}");
            }

            const string PadRightName = "触摸板模式，两根手指轻点：窗口在光标的位置收到右键点一下";
            if (Place(cx, cy) && Clear(PadRightName, (cx, cy)))
            {
                mark = probe.Mark();
                pump.Touch([Finger(0, 600, 360)]);
                Hold(20);
                pump.Touch([Finger(0, 600, 360), Finger(1, 720, 350)]);
                Hold(60);
                pump.Touch([Finger(1, 720, 350)]);
                Hold(20);
                pump.Touch([]);
                probe.Wait(mark, s => s.Any(e => e is { Kind: Kind.Click, Id: WmRButtonUp }));
                Thread.Sleep(100);
                got = probe.Since(mark);
                Item(PadRightName, got.Count(e => e is { Kind: Kind.MouseDown, Id: WmRButtonDown } && e.A == cx && e.B == cy) == 1
                     && got.Count(e => e is { Kind: Kind.Click, Id: WmRButtonUp } && e.A == cx && e.B == cy) == 1
                     && !got.Any(e => e.Id is WmLButtonDown or WmLButtonUp) && CursorAt(cx, cy, 0),
                     Buttons(got) + $"；光标在 {Where()}");
            }
        }
        finally
        {
            pump.ReleaseAll();          // 连接结束：触摸方式也回到「触摸」
            if (cursorKnown) SetCursorPos(cursorBefore.X, cursorBefore.Y);
        }

        // Caps Lock、Num Lock 照开始时的样子补回去：中途丢了焦点、有一下按了没数上，都可能剩下单数下。
        // 看的是指示灯的实际状态，不看焦点——这两个键打进哪个窗口都只是切换一下灯
        void RestoreLocks()
        {
            foreach (var (bit, scan) in new (int, byte)[] { (0b001, 0x45), (0b010, 0x3A) })            // Num Lock、Caps Lock
            {
                bool Differs() => ((InputInjector.HostLeds() ^ ledsBefore) & bit) != 0;
                for (int attempt = 0; attempt < 2 && Differs(); attempt++)
                {
                    sink.Keys([new KeyAction(scan, false, true)]);
                    Thread.Sleep(30);
                    sink.Keys([new KeyAction(scan, false, false)]);
                    // 等到读出来的状态真的变回去了再往下看：读到的比按下去的晚一点的话，紧接着再补一下就又按反了。
                    // 等了 1 秒还没变 = 这一下没按进去（比如前台是权限更高的窗口），再补一次
                    SpinWait.SpinUntil(() => !Differs(), 1000);
                }
            }
        }

        // 5. 按键只往自己的窗口里打：前面的触摸应该已经把它点成前台窗口了，没有的话自己要
        bool focus = probe.TakeForeground();
        Item("自检的窗口是前台窗口", focus, focus ? "" : "拿不到焦点，下面的按键几项没法测");
        if (focus)
        try
        {
            byte[] Report(byte mods, params byte[] keys)
            {
                var r = new byte[8];
                r[0] = mods;
                keys.CopyTo(r, 2);
                return r;
            }
            // 每一步之前都再看一眼焦点还在不在：不在了就不注入（不能把字打到别的程序里）
            bool Type(params byte[][] reports)
            {
                foreach (byte[] r in reports)
                {
                    if (!probe.IsForeground)
                    {
                        pump.ReleaseAll();
                        return false;
                    }
                    pump.Key(r);
                    Thread.Sleep(30);
                }
                return true;
            }

            // 6. 字母键 a：扫描码 1E，不带扩展前缀
            mark = probe.Mark();
            bool typed = Type(Report(0, 0x04), Report(0));
            probe.Wait(mark, s => s.Any(e => e.Kind == Kind.KeyUp));
            got = probe.Since(mark);
            Item("字母键 a：窗口收到扫描码 1E 的按下和松开，不带扩展前缀", typed && got.Any(e => e is { Kind: Kind.KeyDown, A: 0x1E, Flag: false })
                 && got.Any(e => e is { Kind: Kind.KeyUp, A: 0x1E, Flag: false }), Keys(got));

            // 7. 右方向键：扫描码 4D，带扩展前缀（不带的话是小键盘的 6）
            mark = probe.Mark();
            typed = Type(Report(0, 0x4F), Report(0));
            probe.Wait(mark, s => s.Any(e => e.Kind == Kind.KeyUp));
            got = probe.Since(mark);
            Item("右方向键：扫描码 4D，带扩展前缀", typed && got.Any(e => e is { Kind: Kind.KeyDown, A: 0x4D, Flag: true })
                 && got.Any(e => e is { Kind: Kind.KeyUp, A: 0x4D, Flag: true }), Keys(got));

            // 8. Shift + 1：先按 Shift 再按 1，美式布局打出来是 !
            mark = probe.Mark();
            typed = Type(Report(0x02), Report(0x02, 0x1E), Report(0x02), Report(0));
            probe.Wait(mark, s => s.Any(e => e.Kind == Kind.Char));
            got = probe.Since(mark);
            string chars = string.Concat(got.Where(e => e.Kind == Kind.Char).Select(e => (char)e.A));
            Item("Shift + 1 打出 !（主机的键盘布局是美式）", typed && chars == "!", $"打出来的是「{chars}」");

            // 9. 按住 a：过了主机设置的延迟开始连发，次数和设置相符
            KeyRepeat rate = InputInjector.HostKeyRepeat();
            mark = probe.Mark();
            long pressed = Stopwatch.GetTimestamp();
            typed = Type(Report(0, 0x04));
            bool still = typed && Hold(rate.DelayMs + 5.5 * rate.PeriodMs, () => probe.IsForeground);
            if (still) pump.Key(Report(0));
            else pump.ReleaseAll();         // 焦点没了：马上松开，不接着往别的窗口里连发
            double held = Stopwatch.GetElapsedTime(pressed).TotalMilliseconds;
            probe.Wait(mark, s => s.Any(e => e.Kind == Kind.KeyUp));
            got = probe.Since(mark);
            int repeats = got.Count(e => e is { Kind: Kind.KeyDown, A: 0x1E, Repeat: true });
            int expect = (int)((held - rate.DelayMs) / rate.PeriodMs) + 1;
            Item("按住 a：按主机的设置连发", typed && still && Math.Abs(repeats - expect) <= 2,
                 $"延迟 {rate.DelayMs:F0} 毫秒、每 {rate.PeriodMs:F0} 毫秒一次，按住了 {held:F0} 毫秒，连发了 {repeats} 次（照设置算是 {expect} 次，差 2 次以内算符合）");

            // 9b. 按住 a 的时候 Tab5 突然没了（不再报「还按着」，连接还没断）：过 0.7 秒不再连发，键不松开；Tab5 又有消息了接着连发
            mark = probe.Mark();
            typed = Type(Report(0, 0x04));
            int Repeats() => probe.Since(mark).Count(e => e is { Kind: Kind.KeyDown, A: 0x1E, Repeat: true });
            still = typed && Hold(rate.DelayMs + 5.5 * rate.PeriodMs, () => probe.IsForeground);
            // 没消息：这一段的头 0.7 秒还在连发，之后停下。键一直按着，所以照样盯着焦点：没了就不等了，直接走到下面的松开
            still = still && Hold(Proto.HeldStaleMs + 300, () => probe.IsForeground, heard: false);
            int stopped = Repeats();
            still = still && Hold(1000, () => probe.IsForeground, heard: false);                            // 停下之后这 1 秒里一个都不该多
            int quiet = Repeats() - stopped;
            bool keptDown = !probe.Since(mark).Any(e => e is { Kind: Kind.KeyUp, A: 0x1E });
            still = still && Hold(5.5 * rate.PeriodMs, () => probe.IsForeground);                           // 又有消息了
            int resumed = Repeats() - stopped - quiet;
            pump.ReleaseAll();
            Item("按住 a 的时候 Tab5 没了消息：不再连发、键不松开；又有消息了接着连发", typed && still && stopped >= 3 && quiet == 0 && keptDown && resumed >= 3,
                 $"没消息之前连发了 {stopped} 次，之后 1 秒里 {quiet} 次，键{(keptDown ? "一直按着" : "被松开了")}；又有消息之后连发了 {resumed} 次");

            // 10. 指示灯：按一下 Caps Lock，后台线程读到的状态跟着变（和窗口线程读到的一致）；再按一下变回去
            {
                var steps = new List<string>();
                bool ledsOk = true;
                for (int round = 0; round < 2 && ledsOk; round++)
                {
                    mark = probe.Mark();
                    if (!Type(Report(0, 0x39), Report(0)))
                    {
                        ledsOk = false;
                        steps.Add("焦点没了");
                        break;
                    }
                    if (!probe.Wait(mark, s => s.Any(e => e is { Kind: Kind.KeyUp, A: 0x3A })))
                    {
                        ledsOk = false;
                        steps.Add("窗口没收到 Caps Lock");
                        break;
                    }
                    int truth = probe.Since(mark).Last(e => e is { Kind: Kind.KeyUp, A: 0x3A }).B;
                    bool seen = SpinWait.SpinUntil(() => { lock (ledLog) return ledLog.Count > 0 && ledLog[^1] == truth; }, 1000);
                    byte last;
                    lock (ledLog) last = ledLog.Count > 0 ? ledLog[^1] : (byte)0xFF;
                    steps.Add($"窗口线程读到 {truth:b3}，后台线程报的是 {(last == 0xFF ? "（没报过）" : $"{last:b3}")}");
                    ledsOk &= seen;
                }
                Item("键盘指示灯：按 Caps Lock 之后后台线程读到的状态跟着变，再按一下变回去", ledsOk, string.Join("；", steps) + "（三位是 Scroll Caps Num）");
                RestoreLocks();
            }

            // 11. Num Lock：扫描码 45 带不带扩展前缀，系统各认成哪个键（一个是 Num Lock，一个是 Pause）；换算表里填的那种要是 Num Lock。
            // 每种都按两下：是 Num Lock 的话指示灯变过去又变回来
            const uint VkNumLock = 0x90;
            var numLock = HidKeyboard.ScanCodeOf(0x53)!.Value;
            var seenVk = new Dictionary<bool, string>();
            bool tableOk = false, injected = true;
            foreach (bool extended in new[] { false, true })
            {
                mark = probe.Mark();
                for (int press = 0; press < 2 && injected; press++)
                {
                    if (!probe.IsForeground)
                    {
                        injected = false;
                        break;
                    }
                    sink.Keys([new KeyAction(0x45, extended, true)]);
                    Thread.Sleep(30);
                    sink.Keys([new KeyAction(0x45, extended, false)]);
                    Thread.Sleep(30);
                }
                probe.Wait(mark, s => s.Count(e => e.Kind == Kind.KeyUp) >= 2, 500);
                var vks = probe.Since(mark).Where(e => e.Kind == Kind.KeyDown).Select(e => e.Id).Distinct().ToList();
                seenVk[extended] = vks.Count == 0 ? "（窗口没收到）" : string.Join(" ", vks.Select(v => $"{v:X2}"));
                if (extended == numLock.Extended && numLock.Scan == 0x45) tableOk = vks.Count == 1 && vks[0] == VkNumLock;
            }
            Item("Num Lock：换算表里填的扫描码，系统认成 Num Lock（虚拟键码 90）", injected && tableOk,
                 $"表里是 {(numLock.Extended ? "E0 " : "")}{numLock.Scan:X2}；扫描码 45 不带扩展 → 虚拟键码 {seenVk.GetValueOrDefault(false, "（没注入）")}，" +
                 $"带扩展 → {seenVk.GetValueOrDefault(true, "（没注入）")}（13 是 Pause）");
        }
        finally
        {
            pump.ReleaseAll();
            RestoreLocks();
        }

        pump.ReleaseAll();
        lock (warnings)
            Item("注入的接口没有报过错", warnings.Count == 0 || cancelledAt > 0 && warnings.Count <= 1,
                 warnings.Count == 0 ? "" : string.Join("；", warnings) + (cancelledAt > 0 ? "（第 3 项里触点被系统收走之后我们再注入抬起，这一次不算）" : ""));
        lock (faults)
            if (faults.Count > 0) Item("后台线程没有出错", false, string.Join("；", faults));
        return bad == 0;

        static string Buttons(List<Seen> seen)
        {
            var parts = seen.Where(e => e.Kind is Kind.MouseDown or Kind.Click).Select(e => e.Id switch
            {
                WmLButtonDown => $"左键按下({e.A},{e.B})",
                WmLButtonUp => $"左键松开({e.A},{e.B})",
                WmRButtonDown => $"右键按下({e.A},{e.B})",
                WmRButtonUp => $"右键松开({e.A},{e.B})",
                _ => "右键菜单",
            }).ToList();
            return "窗口收到 " + (parts.Count == 0 ? "（没有鼠标键）" : string.Join(" ", parts));
        }

        static string Keys(List<Seen> seen) =>
            "窗口收到 " + (seen.Any(e => e.Kind is Kind.KeyDown or Kind.KeyUp)
                ? string.Join(" ", seen.Where(e => e.Kind is Kind.KeyDown or Kind.KeyUp).Select(e => $"{(e.Kind == Kind.KeyDown ? "按下" : "松开")} {(e.Flag ? "E0 " : "")}{e.A:X2}"))
                : "（没有按键）");
    }
}
