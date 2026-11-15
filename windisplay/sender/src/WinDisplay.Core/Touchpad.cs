namespace WinDisplay.Core;

/// <summary>要往系统里注入的一次鼠标动作是哪一种。</summary>
public enum MouseKind { Move, LeftDown, LeftUp, RightDown, RightUp, Wheel, HWheel }

/// <summary>要往系统里注入的一次鼠标动作。</summary>
/// <param name="X">Move：光标横向挪多少（显示器的像素，向右为正）。Wheel / HWheel：滚多少（120 = 滚轮的一格；Wheel 正的是往上滚，HWheel 正的是往右滚）。</param>
/// <param name="Y">Move：光标纵向挪多少（向下为正）。别的动作不用。</param>
public readonly record struct MouseAction(MouseKind Kind, int X = 0, int Y = 0);

/// <summary>
/// 触摸板模式：把 Tab5 的屏幕当成一块触摸板，Tab5 发来的触点快照（横屏画面里的坐标）→ 鼠标动作（纯计算，时间由调用方给，单位毫秒）。
/// 「一次接触」= 从第一根手指按下到全部抬起。手势：
/// <list type="bullet">
/// <item>一根手指滑动：挪光标。手指挪多远光标就在 Tab5 的画面上挪多远，滑得快的时候放大（最多 <see cref="MaxGain"/> 倍），一下能划过整个屏幕。</item>
/// <item>一根手指轻点（<see cref="TapMs"/> 之内抬起，没挪出 <see cref="Slop"/>）：左键点一下。</item>
/// <item>轻点之后 <see cref="DragArmMs"/> 之内再按下：马上抬起就是又一次轻点（双击的第二下）；按住滑动（挪出 Slop，或者按过了 TapMs 再动）
///   就是拖动——左键在看出是滑动的那一刻按下，到抬起才松。左键不在手指按下的那一刻就按：这时还分不清是不是两指手势的第一根手指
///   （两根手指几乎从不落在同一份快照里），先按下的话「点一下、马上两指滚动 / 两指点右键」会多出一次双击。</item>
/// <item>两根手指轻点：右键点一下。</item>
/// <item>两根手指滑动：滚轮。内容跟着手指走（手指往下，内容往下，也就是往上滚），和 Windows 触摸板的默认方向一样；
///   一次接触里只滚一个方向（先看出是竖着还是横着，之后另一个方向的抖动不算）。</item>
/// </list>
/// 手指刚按下、还分不清是轻点还是滑动的那一会儿（没挪出 Slop，也没过 TapMs）光标不动：轻点时指尖总会滚动几个像素，
/// 光标跟着动的话双击的两下会落在不同的位置。挪出 Slop 之前攒下的那一小段不补。
/// 三根以上手指的接触不算。两根手指的接触里一根先抬起，剩下的那根到抬起之前也不算（不然滚完抬手时光标会跳一下）。
/// 只在一个线程上用。
/// </summary>
public sealed class TouchpadMapper
{
    /// <summary>按下之后这么久之内抬起才算轻点（毫秒）。Tab5 要连着 40 毫秒读不到触点才报抬起，已经算在里面。</summary>
    public const double TapMs = 300;

    /// <summary>轻点时手指最多挪这么远（横屏画面的像素；屏幕约 294 像素/英寸，20 像素约 1.7 毫米）。</summary>
    public const int Slop = 20;

    /// <summary>轻点抬起之后这么久之内再按下，算「双击的第二下 / 拖动」（毫秒）。</summary>
    public const double DragArmMs = 250;

    /// <summary>两根手指划过这么多像素 = 滚轮的一格（120）。</summary>
    public const int NotchPx = 48;

    /// <summary>
    /// 手指滑得慢（每秒不超过 <see cref="SlowSpeed"/> 像素）时不放大；到 <see cref="FastSpeed"/> 放大到这么多倍，中间按比例。1 = 不加速。
    /// 人在 Tab5 上选（「触摸板加速」），Tab5 报过来；随时可以改，从下一份快照起按新的算。
    /// </summary>
    public double MaxGain
    {
        get => _maxGain;
        set => _maxGain = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "放大倍数不能小于 1");
    }
    private double _maxGain = DefaultMaxGain;

    /// <summary>Tab5 没报过加速（老固件）时用的倍数，也是 Tab5 上那个设置的默认值。</summary>
    public const double DefaultMaxGain = 2.0;

    public const double SlowSpeed = 150, FastSpeed = 1500;

    private const double SampleMs = 16;     // Tab5 每隔这么久读一次触摸屏：两份快照之间至少隔了这么久（挤在一起到的也按它算速度）
    private const int WheelStep = 10;       // 滚轮一次至少发这么多（1/12 格）：攒不够先不发
    private const int AxisLockPx = 8;       // 两指的中心挪出这么远才定下是竖着滚还是横着滚

    private readonly Dictionary<byte, (int X, int Y)> _last = [];       // 上一份快照里的手指
    private readonly Dictionary<byte, (int X, int Y)> _origin = [];     // 这一次接触里各根手指起算的位置（手指数变多时重新起算）
    private bool _active;               // 一次接触正在进行
    private int _fingers;               // 这一次接触里同时按着的手指最多有几根
    private double _t0, _lastAt;        // 第一根手指按下的时刻；上一份快照的时刻
    private bool _moved;                // 已经不可能是轻点了（挪出了 Slop，或者过了 TapMs）
    private bool _void;                 // 这一次接触不算（三根以上手指）
    private bool _armed;                // 这一次接触是轻点之后马上又按下的：看出是滑动（不是又一次轻点、不是两指手势）就按下左键、开始拖
    private bool _drag;                 // 左键按着（拖动中）
    private double _tapAt = double.NegativeInfinity;    // 上一次单指轻点抬起的时刻
    private int _axis;                  // 滚哪个方向：0 = 还没定，1 = 竖，2 = 横
    private double _axisX, _axisY;      // 定方向之前两指中心挪了多少
    private double _remX, _remY, _remWheel;     // 不够一个像素 / 一个 WheelStep 的零头，留到下一次

    /// <summary>左键现在是不是按着（拖动中）。</summary>
    public bool ButtonHeld => _drag;

    /// <summary>处理一份快照（空的 = 全部抬起），返回要注入的鼠标动作（可能是空的）。</summary>
    /// <param name="fit">显示器的画面在横屏画面里占哪一块：用它把手指挪的距离换成显示器上的像素。</param>
    public IReadOnlyList<MouseAction> Apply(ReadOnlySpan<TouchPoint> snapshot, ViewFit fit, double nowMs)
    {
        var actions = new List<MouseAction>();
        var now = new Dictionary<byte, (int X, int Y)>();
        foreach (var p in snapshot) now.TryAdd(p.Id, (p.X, p.Y));       // 一份快照里同一个编号出现两次：只认第一个

        if (now.Count == 0)
        {
            if (_active) End(actions, nowMs);
            return actions;
        }
        if (!_active)
        {
            _active = true;
            _t0 = nowMs;
            _fingers = now.Count;
            _moved = false;
            _void = now.Count > 2;
            Anchor(now);
            _armed = now.Count == 1 && nowMs - _tapAt <= DragArmMs;
            _tapAt = double.NegativeInfinity;
        }
        else if (now.Count > _fingers)      // 又按下一根：各根手指从现在的位置重新起算
        {
            _fingers = now.Count;
            _void |= now.Count > 2;
            Anchor(now);
            _armed = false;                 // 是两指手势的第一根手指，不是要拖
            if (_drag)                      // 拖着的时候放上第二根手指：不拖了
            {
                _drag = false;
                actions.Add(new MouseAction(MouseKind.LeftUp));
            }
        }

        if (!_void)
        {
            if (_fingers == 1) OneFinger(actions, now, fit, nowMs);
            else if (now.Count == 2) TwoFingers(actions, now, nowMs);
            // 两根手指的接触里只剩一根：不算
        }
        _last.Clear();
        foreach (var (id, at) in now) _last[id] = at;
        _lastAt = nowMs;
        return actions;
    }

    /// <summary>
    /// 连接结束、或者换成了别的输入方式时调：按着的左键松开，然后从头开始（这之后的手指和之前的没有关系）。
    /// 返回要注入的动作（没有按着的 = 空）。
    /// </summary>
    public IReadOnlyList<MouseAction> ReleaseAll()
    {
        IReadOnlyList<MouseAction> actions = _drag ? [new MouseAction(MouseKind.LeftUp)] : [];
        _drag = _armed = _active = false;
        _tapAt = double.NegativeInfinity;
        _last.Clear();
        _origin.Clear();
        return actions;
    }

    private void Anchor(Dictionary<byte, (int X, int Y)> now)
    {
        _origin.Clear();
        foreach (var (id, at) in now) _origin[id] = at;
        _axis = 0;
        _axisX = _axisY = _remX = _remY = _remWheel = 0;
    }

    private static bool Beyond((int X, int Y) a, (int X, int Y) b, int far)
    {
        long dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy > (long)far * far;
    }

    private void OneFinger(List<MouseAction> actions, Dictionary<byte, (int X, int Y)> now, ViewFit fit, double nowMs)
    {
        var (id, at) = now.First();
        if (!_last.TryGetValue(id, out var prev))       // 这根手指上一份快照里没有（接触的第一份快照，或者换了一根）：从这里起算
        {
            _origin[id] = at;
            return;
        }
        if (!_moved)
        {
            if (!_origin.TryGetValue(id, out var from)) _origin[id] = from = at;
            if (!Beyond(at, from, Slop) && nowMs - _t0 <= TapMs) return;
            _moved = true;                  // 这一份之前攒下的那一小段不补
            if (_armed)                     // 轻点之后马上又按下、现在看出是滑动：从这里开始拖（光标还在轻点的那个位置上）
            {
                _armed = false;
                _drag = true;
                actions.Add(new MouseAction(MouseKind.LeftDown));
            }
            return;
        }
        int dx = at.X - prev.X, dy = at.Y - prev.Y;
        if (dx == 0 && dy == 0) return;
        // 手指的速度（横屏画面的像素 / 秒）→ 放大几倍
        double speed = Math.Sqrt((double)dx * dx + (double)dy * dy) * 1000 / Math.Max(nowMs - _lastAt, SampleMs);
        double gain = speed <= SlowSpeed ? 1 : speed >= FastSpeed ? MaxGain : 1 + (MaxGain - 1) * (speed - SlowSpeed) / (FastSpeed - SlowSpeed);
        // 画面上的一个像素是显示器上的几个像素（1920×1080 的屏是 1.5）
        _remX += dx * gain * fit.SrcW / fit.W;
        _remY += dy * gain * fit.SrcH / fit.H;
        int mx = (int)Math.Truncate(_remX), my = (int)Math.Truncate(_remY);
        if (mx == 0 && my == 0) return;
        _remX -= mx;
        _remY -= my;
        actions.Add(new MouseAction(MouseKind.Move, mx, my));
    }

    private void TwoFingers(List<MouseAction> actions, Dictionary<byte, (int X, int Y)> now, double nowMs)
    {
        // 两指中心挪了多少：两份快照里都有的手指各自挪的取平均
        double dx = 0, dy = 0;
        int n = 0;
        bool far = false;
        foreach (var (id, at) in now)
        {
            if (_origin.TryGetValue(id, out var from)) far |= Beyond(at, from, Slop);
            else _origin[id] = at;
            if (!_last.TryGetValue(id, out var prev)) continue;
            dx += at.X - prev.X;
            dy += at.Y - prev.Y;
            n++;
        }
        if (n > 0)
        {
            dx /= n;
            dy /= n;
        }
        if (!_moved)
        {
            // 定方向要用上挪出 Slop 之前的那一段（不然刚够 Slop 的短滑动分不出横竖）；滚多少不补
            _axisX += dx;
            _axisY += dy;
            if (far || nowMs - _t0 > TapMs) _moved = true;
            return;
        }
        if (_axis == 0)
        {
            _axisX += dx;
            _axisY += dy;
            if (Math.Max(Math.Abs(_axisX), Math.Abs(_axisY)) < AxisLockPx) return;
            _axis = Math.Abs(_axisY) >= Math.Abs(_axisX) ? 1 : 2;
            return;
        }
        // 内容跟着手指走：手指往下 = 往上滚（正），手指往右 = 往左滚（负）
        _remWheel += (_axis == 1 ? dy : -dx) * 120 / NotchPx;
        int wheel = (int)Math.Truncate(_remWheel / WheelStep) * WheelStep;
        if (wheel == 0) return;
        _remWheel -= wheel;
        actions.Add(new MouseAction(_axis == 1 ? MouseKind.Wheel : MouseKind.HWheel, wheel));
    }

    private void End(List<MouseAction> actions, double nowMs)
    {
        bool tap = !_moved && !_void && nowMs - _t0 <= TapMs;
        if (_drag)
        {
            _drag = false;
            actions.Add(new MouseAction(MouseKind.LeftUp));
        }
        else if (tap && _fingers == 1)
        {
            actions.Add(new MouseAction(MouseKind.LeftDown));
            actions.Add(new MouseAction(MouseKind.LeftUp));
            _tapAt = nowMs;                 // 紧接着再按下的接着算（双击、三连击、拖动）
        }
        else if (tap && _fingers == 2)
        {
            actions.Add(new MouseAction(MouseKind.RightDown));
            actions.Add(new MouseAction(MouseKind.RightUp));
        }
        _active = _armed = false;
        _last.Clear();
        _origin.Clear();
    }
}
