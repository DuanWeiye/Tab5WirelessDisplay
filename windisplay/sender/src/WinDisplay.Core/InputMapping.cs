namespace WinDisplay.Core;

/// <summary>
/// 要往系统里注入的一次按键动作，用 PS/2 第一套扫描码（Windows 的 SendInput 认的那种）。
/// Extended = 前面带 E0 的键（方向键、Insert / Delete、右 Ctrl / 右 Alt、Win 键……）。
/// </summary>
public readonly record struct KeyAction(byte ScanCode, bool Extended, bool Down);

/// <summary>
/// Tab5 发来的键盘报文（8 字节 USB HID Boot 报文）→ 一串按键动作（协议「键盘」）。
/// 每份报文都是完整的按键状态；这里记着已经注入、还按着的那些，拿新报文**按集合**和它比，差别按这个顺序给出来：
/// 先松掉不在这份报文里的键，再把修饰键改成这份报文里的，最后按下新出现的键
/// （按住 a 再按 !，报文是「左 Shift + a + 1」：先按 1 再按 Shift 的话打出来的是 1）。
/// 键码按美式布局原样换成扫描码，主机是别的布局时打出来的字会不一样（约定：主机用美式）。
/// 不认识的键码（表里没有的）当作没有。键码位置里有出错标记（0x01~0x03，按的键太多、键盘说不清现在按着哪些）的报文
/// 不代表键松开了：按着的键保持上一份报文的样子，只照它改修饰键（HID 规范就是这么要求主机的）。只在一个线程上用。
/// </summary>
public sealed class HidKeyboard
{
    private byte _mods;                             // 已经注入的修饰键位图
    private readonly List<byte> _held = [];         // 已经注入、还按着的键码，按按下的先后
    private byte _repeat;                           // 该连发的键码：最后按下的那个键，它松开之前。0 = 没有

    /// <summary>
    /// 处理一份报文，返回要注入的动作（可能是空的：和已经注入的状态没有差别）。
    /// </summary>
    public IReadOnlyList<KeyAction> Apply(ReadOnlySpan<byte> report)
    {
        if (report.Length != Proto.KeySize) throw new ArgumentException($"键盘报文要 {Proto.KeySize} 字节，不是 {report.Length}", nameof(report));
        var actions = new List<KeyAction>();
        var now = new List<byte>(6);
        bool unsure = false;                        // 报文里有出错标记：按着哪些键它说不清
        foreach (byte code in report[2..])
        {
            if (code is >= 0x01 and <= 0x03) unsure = true;
            else if (ScanCodeOf(code) is not null && !now.Contains(code)) now.Add(code);
        }
        if (unsure)
        {
            now.Clear();
            now.AddRange(_held);
        }

        for (int i = _held.Count - 1; i >= 0; i--)
        {
            byte code = _held[i];
            if (now.Contains(code)) continue;
            _held.RemoveAt(i);
            if (_repeat == code) _repeat = 0;       // 连发的那个键松开了：不接着连发更早按下的键（真键盘也是这样）
            actions.Add(Key(code, false));
        }

        byte mods = report[0];
        for (int bit = 0; bit < 8; bit++)           // 修饰键：先松后按
            if ((_mods & 1 << bit) != 0 && (mods & 1 << bit) == 0) actions.Add(Modifier(bit, false));
        for (int bit = 0; bit < 8; bit++)
            if ((_mods & 1 << bit) == 0 && (mods & 1 << bit) != 0) actions.Add(Modifier(bit, true));
        _mods = mods;

        foreach (byte code in now)
        {
            if (_held.Contains(code)) continue;
            _held.Add(code);
            _repeat = code;
            actions.Add(Key(code, true));
        }
        return actions;
    }

    /// <summary>
    /// 把还按着的全部松开（连接结束时调：协议规定连接结束等于所有键都松开，Tab5 不会补发一份全松开的报文）。
    /// </summary>
    public IReadOnlyList<KeyAction> ReleaseAll() => Apply(stackalloc byte[Proto.KeySize]);

    /// <summary>
    /// 现在该连发的键（按下的动作），没有就是 null。Tab5 不做长按连发，要由注入的一方按主机的设置自己重复注入「按下」：
    /// 连发的是最后按下的那个键，它松开就停，哪怕更早按下的键还按着；修饰键不连发。
    /// </summary>
    public KeyAction? Repeat => _repeat == 0 ? null : Key(_repeat, true);

    private static KeyAction Key(byte code, bool down)
    {
        var (scan, extended) = ScanCodeOf(code)!.Value;
        return new KeyAction(scan, extended, down);
    }

    // 修饰键位图的 8 位：左 Ctrl、左 Shift、左 Alt、左 Win、右 Ctrl、右 Shift、右 Alt、右 Win
    private static readonly (byte Scan, bool Extended)[] Modifiers =
        [(0x1D, false), (0x2A, false), (0x38, false), (0x5B, true), (0x1D, true), (0x36, false), (0x38, true), (0x5C, true)];

    private static KeyAction Modifier(int bit, bool down) => new(Modifiers[bit].Scan, Modifiers[bit].Extended, down);

    /// <summary>
    /// HID 键码（Keyboard 页）→ 第一套扫描码。表里没有的（0 = 空位、1~3 = 出错标记、Pause、多媒体键……）给 null。
    /// 取自 USB HID 到 PS/2 扫描码的对照表（0x04~0x65，标准 104 键盘上有的那些）。
    /// Num Lock（0x53）是不带 E0 的 45。窗口收到的 Num Lock 消息带着扩展标志，但那是系统自己加上去的，注入时不能带：
    /// 注入自检实测（Windows 11，SendInput 按扫描码），45 不带扩展认成 Num Lock，带扩展的认不出来（虚拟键码 FF）。
    /// </summary>
    public static (byte Scan, bool Extended)? ScanCodeOf(byte hid)
    {
        if (hid < 0x04 || hid >= 0x04 + Table.Length) return null;
        ushort v = Table[hid - 0x04];
        return v == 0 ? null : ((byte)v, (v & 0x100) != 0);
    }

    private const ushort E = 0x100;     // 带 E0 前缀

    private static readonly ushort[] Table =
    [
        // 0x04 a~z
        0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,
        0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C,
        // 0x1E 1~9、0
        0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B,
        // 0x28 回车、Esc、退格、Tab、空格、- = [ ] \、（非美式键盘的 #，和 \ 同一个扫描码）、; ' ` , . /、Caps Lock
        0x1C, 0x01, 0x0E, 0x0F, 0x39, 0x0C, 0x0D, 0x1A, 0x1B, 0x2B, 0x2B, 0x27, 0x28, 0x29, 0x33, 0x34, 0x35, 0x3A,
        // 0x3A F1~F12
        0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58,
        // 0x46 Print Screen、Scroll Lock、Pause（它的扫描码是一串三个字节，这里不支持）
        E | 0x37, 0x46, 0,
        // 0x49 Insert、Home、Page Up、Delete、End、Page Down、右、左、下、上
        E | 0x52, E | 0x47, E | 0x49, E | 0x53, E | 0x4F, E | 0x51, E | 0x4D, E | 0x4B, E | 0x50, E | 0x48,
        // 0x53 Num Lock、小键盘 / * - + 回车、1~9、0、.
        0x45, E | 0x35, 0x37, 0x4A, 0x4E, E | 0x1C, 0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49, 0x52, 0x53,
        // 0x64 非美式键盘左 Shift 旁边的 \、菜单键
        0x56, E | 0x5D,
    ];
}

/// <summary>Cancel = 这个触点作废（手指并没有抬起，是我们不再知道它怎样了）：系统和程序把这次触摸当成没做完，不算一次点击。</summary>
public enum ContactPhase { Down, Move, Up, Cancel }

/// <summary>要往系统里注入的一个触点：编号、在显示器上的像素位置、这一次是按下、移动、抬起还是作废。</summary>
public readonly record struct Contact(byte Id, int X, int Y, ContactPhase Phase);

/// <summary>
/// Tab5 发来的触点快照（横屏画面 1280×720 里的坐标）→ 显示器上的触点（像素坐标 + 按下 / 移动 / 抬起）。
/// 显示器的画面在横屏画面里占哪一块由 ViewFit 定（比例不是 16:9 的屏两边有黑边）：
/// 落在黑边上的触点不算（它抬起之前都不算，滑进画面里也不算——半路冒出来的触点系统会当成从那里按下的）；
/// 在画面里按下、滑到黑边上的，位置贴着画面的边。
/// 每次返回的是**当前所有触点**（系统的触摸注入要求每次都给全），抬起的那些在这一次里标 Up、之后不再出现。
/// 手并没有抬起、而是我们不再知道它怎样了（连接结束了，Tab5 没了消息）的时候，触点是**作废**（Cancel）不是抬起：
/// 抬起会在它最后的位置上变成一次点击，按得久的还会变成右键。只在一个线程上用。
/// </summary>
public sealed class TouchMapper(ViewFit fit)
{
    private readonly Dictionary<byte, (int X, int Y)> _active = [];     // 已经注入、还按着的触点和它上一次的位置
    private readonly HashSet<byte> _ignored = [];                       // 在黑边上按下的，抬起之前不算

    /// <summary>处理一份快照（空的 = 全部抬起）。返回空 = 这一次没有要注入的。</summary>
    public IReadOnlyList<Contact> Apply(ReadOnlySpan<TouchPoint> snapshot)
    {
        var result = new List<Contact>();
        var seen = new HashSet<byte>();
        foreach (var p in snapshot)
        {
            if (!seen.Add(p.Id)) continue;          // 一份快照里同一个编号出现两次：只认第一个
            if (_ignored.Contains(p.Id)) continue;
            bool inside = p.X >= fit.X && p.X < fit.X + fit.W && p.Y >= fit.Y && p.Y < fit.Y + fit.H;
            if (!_active.ContainsKey(p.Id) && !inside)
            {
                _ignored.Add(p.Id);
                continue;
            }
            var at = ToScreen(p.X, p.Y);
            result.Add(new Contact(p.Id, at.X, at.Y, _active.ContainsKey(p.Id) ? ContactPhase.Move : ContactPhase.Down));
            _active[p.Id] = at;
        }
        _ignored.RemoveWhere(id => !seen.Contains(id));
        foreach (var (id, at) in _active.Where(a => !seen.Contains(a.Key)).ToList())
        {
            _active.Remove(id);
            // 抬起的位置 = 它最后在的地方。不只是因为快照里没有它的新位置：InjectTouchInput 规定抬起那一帧的坐标要和上一帧一样，
            // 不一样就报参数错、所有触点都被取消
            result.Add(new Contact(id, at.X, at.Y, ContactPhase.Up));
        }
        return result;
    }

    /// <summary>
    /// 按着的触点都作废，返回要注入的那一帧（各在它上一次的位置，标 Cancel；没有按着的 = 空）。Tab5 没了消息时调。
    /// 手还按着的那些到抬起之前都不算（同 <see cref="Abandon"/>）。
    /// </summary>
    public IReadOnlyList<Contact> Cancel()
    {
        IReadOnlyList<Contact> frame = [.. _active.Select(a => new Contact(a.Key, a.Value.X, a.Value.Y, ContactPhase.Cancel))];
        Abandon();
        return frame;
    }

    /// <summary>
    /// 连接结束时调：按着的触点都作废（返回要注入的那一帧，同 <see cref="Cancel"/>），然后从头开始——
    /// 下一条连接里的手指和这一条的没有关系，同一个编号再按下就是新的一次。
    /// </summary>
    public IReadOnlyList<Contact> ReleaseAll()
    {
        var frame = Cancel();
        _ignored.Clear();
        return frame;
    }

    /// <summary>
    /// 系统把已经注入的触点都收走了（一帧没注入成，或者显示器的分辨率变了）：手还按着的那些到抬起之前都不算——
    /// 接着报的话系统会当成从半路按下的。分辨率变了就把新的位置关系给进来，之后按它换算。
    /// </summary>
    public void Abandon(ViewFit? newFit = null)
    {
        foreach (byte id in _active.Keys) _ignored.Add(id);
        _active.Clear();
        if (newFit is { } f) fit = f;
    }

    /// <summary>
    /// 还按着的触点，各在它上一次的位置（Move）。Windows 的 InjectTouchInput 规定：触点按着不动时也要接着注入更新帧
    /// （INRANGE | INCONTACT | UPDATE），不然系统把这个触点收走（程序收到的是一次普通的抬起）；隔多久文档没写，注入的一方实测后定时拿它重报。
    /// </summary>
    public IReadOnlyList<Contact> Held => [.. _active.Select(a => new Contact(a.Key, a.Value.X, a.Value.Y, ContactPhase.Move))];

    // 横屏画面里的一点 → 显示器上的像素（取那个像素格的中心来换算，超出画面的贴边）
    private (int X, int Y) ToScreen(int x, int y) =>
        (Math.Clamp((int)(((x - fit.X) * 2L + 1) * fit.SrcW / (2L * fit.W)), 0, fit.SrcW - 1),
         Math.Clamp((int)(((y - fit.Y) * 2L + 1) * fit.SrcH / (2L * fit.H)), 0, fit.SrcH - 1));
}
