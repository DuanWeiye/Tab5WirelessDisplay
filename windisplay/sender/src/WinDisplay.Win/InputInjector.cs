using System.Runtime.InteropServices;
using WinDisplay.Core;

namespace WinDisplay.Win;

/// <summary>
/// 往系统里注入输入：按键用 SendInput（按扫描码，主机按它自己的键盘布局解释——约定是美式），触摸用 InjectTouchInput（注入成真的触摸，不是鼠标）；
/// 触摸板模式下的鼠标动作也用 SendInput（光标按绝对位置挪，只在主显示器里走——Tab5 上显示的就是主显示器）。
/// 触点的位置是主显示器上的物理像素（主显示器的左上角就是桌面坐标的原点），所以进程要是「按显示器感知 DPI」的：这里在建对象时设一次。
/// <para>
/// 注入不进去的情况（都只报 <see cref="Warning"/>，不抛异常）：前台是权限更高的窗口（以管理员身份运行的程序、UAC 提示、锁屏、
/// Ctrl-Alt-Del 那一屏）时系统不让普通权限的程序注入，而且 SendInput 不说是不是因为这个；Ctrl-Alt-Del、Win-L 这两个组合键注入不了。
/// </para>
/// 各个方法只在一个线程上同时调（<see cref="InputBridge"/> 在它的锁里调）。
/// </summary>
public sealed class InputInjector : IInputSink
{
    /// <summary>最多同时注入几个触点（Tab5 的触摸屏报得出的不超过这个数）。</summary>
    public const int MaxContacts = 10;

    /// <summary>
    /// 触点按着不动时隔多久重报一次（毫秒）。不重报的话系统过一阵就把它收走（程序收到的是一次普通的抬起）：
    /// 注入自检量出来（Windows 11）500 毫秒没事、1 秒时已经收走，所以取 100，留出几倍的余量（后台线程醒得晚一点也够）。
    /// </summary>
    public const double TouchKeepAliveMs = 100;

    private const uint InputMouse = 0, InputKeyboard = 1;
    private const uint MouseMove = 0x0001, MouseLeftDown = 0x0002, MouseLeftUp = 0x0004, MouseRightDown = 0x0008, MouseRightUp = 0x0010;
    private const uint MouseWheel = 0x0800, MouseHWheel = 0x1000, MouseAbsolute = 0x8000;

    /// <summary>挪过光标之后这么久之内再挪，接着上一次挪到的地方算，不重新读系统的光标位置（毫秒；见 <see cref="Mouse"/>）。</summary>
    public const long CursorTrustMs = 200;

    private const uint KeyEventExtended = 0x0001, KeyEventUp = 0x0002, KeyEventScanCode = 0x0008;
    private const uint PointerTypeTouch = 2;
    private const uint PointerInRange = 0x0002, PointerInContact = 0x0004, PointerCanceled = 0x8000;
    private const uint PointerDown = 0x00010000, PointerUpdate = 0x00020000, PointerUp = 0x00040000;
    private const uint TouchFeedbackDefault = 1;        // 触摸时屏幕上那个小圆圈：跟着用户在「笔和触控」里的设置
    private const uint TouchMaskContactArea = 1, TouchMaskOrientation = 2, TouchMaskPressure = 4;
    private const int ErrorNotReady = 21;
    private const uint SpiGetKeyboardSpeed = 0x000A, SpiGetKeyboardDelay = 0x0016;
    private const int VkCapital = 0x14, VkNumLock = 0x90, VkScroll = 0x91;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    // INPUT 里那个联合：最大的成员是 MOUSEINPUT，带上它整个结构的大小和对齐才对（64 位上 40 字节）
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeybdInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerInfo
    {
        public uint PointerType, PointerId, FrameId, PointerFlags;
        public IntPtr SourceDevice, Target;
        public Point PixelLocation, HimetricLocation, PixelLocationRaw, HimetricLocationRaw;
        public uint Time, HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerTouchInfo
    {
        public PointerInfo Pointer;
        public uint TouchFlags, TouchMask;
        public Rect Contact, ContactRaw;
        public uint Orientation, Pressure;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool InitializeTouchInjection(uint maxCount, uint mode);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool InjectTouchInput(uint count, [In] PointerTouchInfo[] contacts);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern short GetKeyState(int virtualKey);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out Point at);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfoW(uint action, uint param, out uint value, uint flags);

    /// <summary>
    /// 进程按显示器感知 DPI（第 2 版）：之后屏幕坐标一律是物理像素。要在建任何窗口之前调。
    /// 返回 false = 没设成，而且现在也不是按显示器感知的（宿主程序先设成了别的）：屏幕坐标被系统按缩放比例折算过，注入的触点会落到错的位置。
    /// </summary>
    public static bool UsePhysicalPixels() =>
        // 设不成有两种：已经是这样了（宿主程序自己设过，比如写在清单里），或者设成了别的。看现在实际是哪种：2 = 按显示器感知
        SetProcessDpiAwarenessContext(-4) || GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()) == 2;

    /// <exception cref="InvalidOperationException">这个进程注入不了触摸：屏幕坐标不是物理像素，或者系统不让（没有桌面的会话、太老的系统）。</exception>
    public InputInjector()
    {
        if (!UsePhysicalPixels())
            throw new InvalidOperationException("这个进程的屏幕坐标不是物理像素（DPI 感知已经被设成了别的方式），注入的触点会落到错的位置：" +
                                                "要在建任何窗口之前调 InputInjector.UsePhysicalPixels()，或者在程序清单里写成 PerMonitorV2");
        if (!InitializeTouchInjection(MaxContacts, TouchFeedbackDefault))
            throw new InvalidOperationException($"触摸注入开不了（InitializeTouchInjection，错误 {Marshal.GetLastWin32Error()}）：要在登录用户的桌面会话里跑");
    }

    /// <summary>一次注入没成（说明是哪一种、系统给的错误码）。在调注入的那个线程上回调。</summary>
    public event Action<string>? Warning;

    public void Keys(IReadOnlyList<KeyAction> actions)
    {
        if (actions.Count == 0) return;
        var inputs = new Input[actions.Count];
        for (int i = 0; i < inputs.Length; i++)
        {
            KeyAction a = actions[i];
            inputs[i].Type = InputKeyboard;
            inputs[i].U.Keyboard = new KeybdInput
            {
                Scan = a.ScanCode,
                Flags = KeyEventScanCode | (a.Extended ? KeyEventExtended : 0) | (a.Down ? 0 : KeyEventUp),
            };
        }
        uint done = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (done != inputs.Length)
            Warning?.Invoke($"按键注入了 {done} / {inputs.Length} 个（错误 {Marshal.GetLastWin32Error()}；前台是权限更高的窗口时系统不让注入）");
    }

    private Point _cursor;                  // 上一次把光标挪到了哪（主显示器上的物理像素）
    private long _cursorAt = long.MinValue; // 那是什么时候（Environment.TickCount64）

    /// <summary>
    /// 鼠标动作。光标是「读出现在的位置、加上要挪的、按绝对位置注入」，不用相对移动：相对移动要过系统的指针速度和加速设置，
    /// 挪多少就不由 <see cref="TouchpadMapper"/> 说了算（它自己已经按手指的速度放大过）。
    /// 一笔里连着挪的时候不每次都读系统的光标位置，接着上一次挪到的地方算（<see cref="CursorTrustMs"/> 之内）：
    /// 注入是排队的，马上读回来的可能还是挪之前的位置；绝对位置换成系统要的 0~65535 时差一个像素，每次都读的话慢慢挪会原地不动。
    /// 隔得久了（人可能动过真鼠标）才重新读。
    /// </summary>
    public void Mouse(IReadOnlyList<MouseAction> actions)
    {
        if (actions.Count == 0) return;
        var inputs = new List<Input>(actions.Count);
        int w = Math.Max(GetSystemMetrics(0), 1), h = Math.Max(GetSystemMetrics(1), 1);        // 主显示器的宽、高（物理像素）
        foreach (MouseAction a in actions)
        {
            var m = new MouseInput();
            switch (a.Kind)
            {
                case MouseKind.Move:
                    long now = Environment.TickCount64;
                    // 写成「上一次早于 now − 200」：_cursorAt 的初值是 long.MinValue，写成 now − _cursorAt 会溢出成负数、第一次就不读了
                    if (_cursorAt < now - CursorTrustMs && !GetCursorPos(out _cursor))
                    {
                        // 读不出光标在哪（锁屏、UAC 那一屏）：这时也注入不进去
                        Warning?.Invoke($"读不出光标的位置（错误 {Marshal.GetLastWin32Error()}），这一下没挪");
                        continue;
                    }
                    _cursor = new Point { X = Math.Clamp(_cursor.X + a.X, 0, w - 1), Y = Math.Clamp(_cursor.Y + a.Y, 0, h - 1) };
                    _cursorAt = now;
                    // 0~65535 铺满主显示器；往上取整，系统换回像素（乘宽度、除以 65536、舍去零头）时正好落回这个像素
                    m.Dx = (int)Math.Min((_cursor.X * 65536L + w - 1) / w, 65535);
                    m.Dy = (int)Math.Min((_cursor.Y * 65536L + h - 1) / h, 65535);
                    m.Flags = MouseMove | MouseAbsolute;
                    break;
                case MouseKind.LeftDown: m.Flags = MouseLeftDown; break;
                case MouseKind.LeftUp: m.Flags = MouseLeftUp; break;
                case MouseKind.RightDown: m.Flags = MouseRightDown; break;
                case MouseKind.RightUp: m.Flags = MouseRightUp; break;
                case MouseKind.Wheel: (m.Flags, m.MouseData) = (MouseWheel, unchecked((uint)a.X)); break;
                case MouseKind.HWheel: (m.Flags, m.MouseData) = (MouseHWheel, unchecked((uint)a.X)); break;
                default: continue;
            }
            inputs.Add(new Input { Type = InputMouse, U = new InputUnion { Mouse = m } });
        }
        if (inputs.Count == 0) return;
        uint done = SendInput((uint)inputs.Count, [.. inputs], Marshal.SizeOf<Input>());
        if (done != inputs.Count)
            Warning?.Invoke($"鼠标动作注入了 {done} / {inputs.Count} 个（错误 {Marshal.GetLastWin32Error()}；前台是权限更高的窗口时系统不让注入）");
    }

    public bool Touch(IReadOnlyList<Contact> frame)
    {
        if (frame.Count == 0) return true;
        if (frame.Count > MaxContacts)
        {
            Warning?.Invoke($"一帧里有 {frame.Count} 个触点，超过了 {MaxContacts} 个");
            return false;
        }
        var contacts = new PointerTouchInfo[frame.Count];
        int right = GetSystemMetrics(0) - 1, bottom = GetSystemMetrics(1) - 1;       // 主显示器最右、最下的那个像素
        for (int i = 0; i < contacts.Length; i++)
        {
            Contact c = frame[i];
            contacts[i].Pointer.PointerType = PointerTypeTouch;
            contacts[i].Pointer.PointerId = c.Id;
            contacts[i].Pointer.PixelLocation = new Point { X = c.X, Y = c.Y };
            contacts[i].Pointer.PointerFlags = c.Phase switch
            {
                ContactPhase.Down => PointerDown | PointerInRange | PointerInContact,
                ContactPhase.Move => PointerUpdate | PointerInRange | PointerInContact,
                ContactPhase.Cancel => PointerUp | PointerCanceled,
                _ => PointerUp,
            };
            // 指尖大小的接触面、垂直按下、中等压力（系统文档里的默认值）。触点贴着屏幕边时接触面不伸到屏幕外面去
            contacts[i].TouchMask = TouchMaskContactArea | TouchMaskOrientation | TouchMaskPressure;
            contacts[i].Contact = new Rect
            {
                Left = Math.Max(c.X - 2, 0), Top = Math.Max(c.Y - 2, 0),
                Right = Math.Max(Math.Min(c.X + 2, right), c.X), Bottom = Math.Max(Math.Min(c.Y + 2, bottom), c.Y),
            };
            contacts[i].Orientation = 90;
            contacts[i].Pressure = 512;
        }
        for (int attempt = 0; ; attempt++)
        {
            if (InjectTouchInput((uint)contacts.Length, contacts)) return true;
            int error = Marshal.GetLastWin32Error();
            // 和上一帧离得太近（不到 0.1 毫秒）：系统说「还没准备好」，这一帧没有作废，稍等重发同一帧
            if (error == ErrorNotReady && attempt < 3)
            {
                Thread.Sleep(1);
                continue;
            }
            Warning?.Invoke($"触摸注入没成（{frame.Count} 个触点，错误 {error}）");
            return false;
        }
    }

    /// <summary>主机现在的按键连发设置（控制面板「键盘」里那两个滑块）。读不出来时用系统的默认值。</summary>
    public static KeyRepeat HostKeyRepeat()
    {
        // 延迟 0~3 档 = 250 毫秒 ~ 1 秒；速度 0~31 档 = 每秒约 2.5 ~ 30 个
        uint delay = SystemParametersInfoW(SpiGetKeyboardDelay, 0, out uint d, 0) ? Math.Min(d, 3) : 1;
        uint speed = SystemParametersInfoW(SpiGetKeyboardSpeed, 0, out uint s, 0) ? Math.Min(s, 31) : 31;
        return new KeyRepeat(250.0 * (delay + 1), 1000.0 / (2.5 + speed * 27.5 / 31));
    }

    /// <summary>
    /// 主机现在的键盘指示灯状态（bit0 Num Lock、bit1 Caps Lock、bit2 Scroll Lock），协议里 KBD_LEDS 的排法。
    /// </summary>
    public static byte HostLeds() =>
        (byte)((GetKeyState(VkNumLock) & 1) | (GetKeyState(VkCapital) & 1) << 1 | (GetKeyState(VkScroll) & 1) << 2);
}
