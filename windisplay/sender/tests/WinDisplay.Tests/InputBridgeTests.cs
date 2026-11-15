using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>
/// Tab5 的输入事件 → 注入动作，连同按时间做的两件事：按键连发、触点按着不动时重报；Tab5 没了消息时这两件事要停下。
/// 时间是测试里给的（毫秒），注入的地方是假的：记下注入了什么。
/// </summary>
public class InputBridgeTests
{
    private const byte LShift = 0x02;
    private const byte A = 0x04, B = 0x05;
    private const byte ScanA = 0x1E, ScanB = 0x30, ScanLShift = 0x2A;

    private static readonly ViewFit Fhd = ViewFit.For(1920, 1080), Qhd = ViewFit.For(2560, 1440);     // 都是 16:9，没有黑边

    private sealed class FakeSink : IInputSink
    {
        public readonly List<KeyAction> KeyLog = [];
        public readonly List<IReadOnlyList<Contact>> Frames = [];
        public readonly List<MouseAction> MouseLog = [];
        public bool RefuseTouch;            // 模拟系统没收下这一帧
        public bool FailKeys, FailTouch;    // 注入的地方抛异常（什么都没注入进去）

        public void Keys(IReadOnlyList<KeyAction> actions)
        {
            if (FailKeys) throw new InvalidOperationException("按键注入不进去");
            KeyLog.AddRange(actions);
        }

        public bool Touch(IReadOnlyList<Contact> frame)
        {
            if (FailTouch) throw new InvalidOperationException("触摸注入不进去");
            Frames.Add(frame);
            return !RefuseTouch;
        }

        public void Mouse(IReadOnlyList<MouseAction> actions) => MouseLog.AddRange(actions);

        public int Downs(byte scan) => KeyLog.Count(k => k.ScanCode == scan && k.Down);
    }

    private sealed class Rig
    {
        public readonly FakeSink Sink = new();
        public ViewFit Fit = Fhd;
        public KeyRepeat Rate = new(500, 33);       // Windows 的默认设置差不多是这样：按住半秒开始连发，一秒 30 个
        public readonly InputBridge Bridge;
        public bool Touchpad { set => Bridge.Mode(value); }     // Tab5 报过来的触摸方式（人在 Tab5 上选的）

        // 假 Tab5 这一侧：它上一份 KEY / TOUCH 里有没有按着的，上一个输入报文是什么时候发的
        private bool _keysDown, _fingersDown;
        private double _lastSent;

        public Rig() => Bridge = new InputBridge(Sink, () => Fit, () => Rate, keepAliveMs: 100);

        public void Key(double at, byte mods, params byte[] keys)
        {
            var r = new byte[8];
            r[0] = mods;
            keys.CopyTo(r, 2);
            (_keysDown, _lastSent) = (mods != 0 || keys.Length > 0, at);
            Bridge.Key(r, at);
        }

        public void Touch(double at, params TouchPoint[] points)
        {
            (_fingersDown, _lastSent) = (points.Length > 0, at);
            Bridge.Touch(points, at);
        }

        /// <summary>Tab5 报了一次「还按着」。</summary>
        public void Held(double at)
        {
            _lastSent = at;
            Bridge.Held(at);
        }

        /// <summary>
        /// 照后台线程的做法把 from 到 to 这一段时间走完：每次睡到下一件事的时间再调 Tick。
        /// 这期间 Tab5 照协议做：有键 / 触点按着时，离它上一个输入报文满 <see cref="Proto.HeldPeriodMs"/> 就报一次「还按着」；
        /// tab5Alive = false 是 Tab5 突然没了（连接还没断）：这一段里它什么都不发。
        /// </summary>
        public void RunUntil(double from, double to, bool tab5Alive = true)
        {
            bool reporting = tab5Alive && (_keysDown || _fingersDown);
            double now = from;
            while (true)
            {
                if (reporting && now - _lastSent >= Proto.HeldPeriodMs) Held(now);
                double next = now + Math.Max(Bridge.Tick(now), 0.001);
                if (reporting) next = Math.Min(next, _lastSent + Proto.HeldPeriodMs);
                if (!(next < to)) break;        // 没有要按时间做的事时 next 是正无穷
                now = next;
            }
        }
    }

    private static Contact At(byte id, int x, int y, ContactPhase phase) => new(id, x, y, phase);

    [Fact]
    public void 按住一个字母_过了延迟开始连发_松开就停()
    {
        var rig = new Rig();
        rig.Key(0, 0, A);
        rig.RunUntil(0, 499);
        Assert.Equal(1, rig.Sink.Downs(ScanA));             // 还没到半秒：只有按下的那一次
        rig.RunUntil(499, 1000);                            // 500、533、566 … 995：16 次
        Assert.Equal(1 + 16, rig.Sink.Downs(ScanA));
        rig.Key(1000, 0);
        Assert.Equal(new KeyAction(ScanA, false, false), rig.Sink.KeyLog[^1]);
        Assert.True(double.IsPositiveInfinity(rig.Bridge.Tick(1000)));
        rig.RunUntil(1000, 3000);
        Assert.Equal(1 + 16, rig.Sink.Downs(ScanA));        // 松开之后一个都不多
    }

    [Fact]
    public void 按住a再按住b_改成连发b并且重新等延迟_松开b之后不接着连发a()
    {
        var rig = new Rig();
        rig.Key(0, 0, A);
        rig.RunUntil(0, 700);                               // a 在 500、533 … 698 连发了 7 次
        Assert.Equal(1 + 7, rig.Sink.Downs(ScanA));
        rig.Key(700, 0, A, B);
        rig.RunUntil(700, 1199);                            // b 要到 1200 才开始连发；a 不再连发
        Assert.Equal(1 + 7, rig.Sink.Downs(ScanA));
        Assert.Equal(1, rig.Sink.Downs(ScanB));
        rig.RunUntil(1199, 1300);                           // 1200、1233、1266、1299
        Assert.Equal(1 + 4, rig.Sink.Downs(ScanB));
        rig.Key(1300, 0, A);                                // 松开 b，a 还按着：真键盘也不会回头连发 a
        rig.RunUntil(1300, 3000);
        Assert.Equal(1 + 7, rig.Sink.Downs(ScanA));
        Assert.Equal(1 + 4, rig.Sink.Downs(ScanB));
    }

    [Fact]
    public void 修饰键不连发_按着Shift打字时连发的是字母_中途松开Shift不打断连发()
    {
        var rig = new Rig();
        rig.Key(0, LShift);
        rig.RunUntil(0, 2000);
        Assert.Equal(1, rig.Sink.Downs(ScanLShift));        // 只按着 Shift 两秒：没有连发
        rig.Key(2000, LShift, A);
        rig.RunUntil(2000, 2600);                           // 2500、2533、2566、2599
        Assert.Equal(1 + 4, rig.Sink.Downs(ScanA));
        rig.Key(2600, 0, A);                                // 松开 Shift，a 还按着：接着按原来的节拍连发，不重新等延迟
        rig.RunUntil(2600, 2700);                           // 2632、2665、2698
        Assert.Equal(1 + 4 + 3, rig.Sink.Downs(ScanA));
        Assert.Equal(1, rig.Sink.Downs(ScanLShift));
    }

    [Fact]
    public void 后台线程卡了一下来晚了_欠下的连发不补()
    {
        var rig = new Rig();
        rig.Key(0, 0, A);
        rig.RunUntil(0, 600);                               // 500、533、566、599
        Assert.Equal(1 + 4, rig.Sink.Downs(ScanA));
        rig.Held(1900);                                     // 这期间 Tab5 的报文照常在收（那是另一个线程）
        double wait = rig.Bridge.Tick(2000);                // 卡了 1.4 秒才又轮到：只发一次，下一次在一个周期之后
        Assert.Equal(1 + 5, rig.Sink.Downs(ScanA));
        Assert.Equal(33, wait, 3);
    }

    [Fact]
    public void 主机的连发设置不合理_不会变成死循环()
    {
        var rig = new Rig { Rate = new KeyRepeat(0, 0) };
        rig.Key(0, 0, A);
        rig.RunUntil(0, 10);                                // 都按 1 毫秒算：1、2 … 9
        Assert.InRange(rig.Sink.Downs(ScanA), 1 + 8, 1 + 10);
    }

    [Fact]
    public void 连发的节奏在键按下的时候取_按住期间改了主机的设置_正在连发的照旧_下一个键按新的()
    {
        var rig = new Rig();
        rig.Key(0, 0, A);
        rig.RunUntil(0, 600);                               // 500、533、566、599
        Assert.Equal(1 + 4, rig.Sink.Downs(ScanA));
        rig.Rate = new KeyRepeat(250, 100);                 // 人在控制面板里把连发调慢了
        rig.RunUntil(600, 700);                             // a 还是每 33 毫秒一次：632、665、698
        Assert.Equal(1 + 4 + 3, rig.Sink.Downs(ScanA));
        rig.Key(700, 0, A, B);                              // b 按新的设置：950 开始，每 100 毫秒一次
        rig.RunUntil(700, 949);
        Assert.Equal(1, rig.Sink.Downs(ScanB));
        rig.RunUntil(949, 1100);                            // 950、1050
        Assert.Equal(1 + 2, rig.Sink.Downs(ScanB));
        Assert.Equal(1 + 4 + 3, rig.Sink.Downs(ScanA));
    }

    // ── Tab5 没了消息（协议「按着的时候」）──
    [Fact]
    public void 按着键的Tab5没了消息_连发停下_键不松开_又有消息了接着连发()
    {
        var rig = new Rig();
        rig.Key(0, 0, A);
        rig.RunUntil(0, 600);                               // 连发 500、533、566、599；Tab5 在 200、400 报过「还按着」
        Assert.Equal(1 + 4, rig.Sink.Downs(ScanA));
        rig.RunUntil(600, 3000, tab5Alive: false);          // Tab5 没了：最后一个报文在 400，到 1100 为止还照着连发（632 … 1094：15 次）
        Assert.Equal(1 + 4 + 15, rig.Sink.Downs(ScanA));
        Assert.All(rig.Sink.KeyLog, k => Assert.True(k.Down));          // 没有注入过「松开」
        rig.Held(3000);                                     // 原来只是报文晚到了：接着连发（3008、3041、3074），不重新等延迟
        rig.RunUntil(3000, 3100);
        Assert.Equal(1 + 4 + 15 + 3, rig.Sink.Downs(ScanA));
        rig.Key(3100, 0);
        Assert.Equal(new KeyAction(ScanA, false, false), rig.Sink.KeyLog[^1]);
    }

    [Fact]
    public void 按着键的Tab5没了消息_连接结束时键照样松开()
    {
        var rig = new Rig();
        rig.Key(0, LShift, A);
        rig.RunUntil(0, 5000, tab5Alive: false);            // 一个报文都没再来：连发到 700 为止（500 … 698：7 次）
        Assert.Equal(1 + 7, rig.Sink.Downs(ScanA));
        rig.Bridge.ReleaseAll();                            // 过了几秒连接才断
        Assert.Equal([new KeyAction(ScanA, false, false), new KeyAction(ScanLShift, false, false)], rig.Sink.KeyLog.TakeLast(2));
    }

    [Fact]
    public void 按着手指的Tab5没了消息_这根手指作废_注入的是取消不是抬起_连接结束时不再补_下一条连接照常()
    {
        var rig = new Rig();
        rig.Touch(0, new TouchPoint(0, 640, 360));
        rig.RunUntil(0, 250);                               // 重报 100、200；Tab5 在 200 报过「还按着」
        Assert.Equal(1 + 2, rig.Sink.Frames.Count);
        rig.RunUntil(250, 3000, tab5Alive: false);          // Tab5 没了：到 900 为止还照着重报（300 … 900：7 次），1000 那一次改成作废
        Assert.Equal(1 + 2 + 7 + 1, rig.Sink.Frames.Count);
        Assert.All(rig.Sink.Frames.Skip(1).SkipLast(1), f => Assert.Equal([At(0, 960, 540, ContactPhase.Move)], f));
        Assert.Equal([At(0, 960, 540, ContactPhase.Cancel)], rig.Sink.Frames[^1]);     // 不是抬起：抬起等于在那里点了一下
        Assert.True(double.IsPositiveInfinity(rig.Bridge.Tick(3000)));

        rig.Touch(3000, new TouchPoint(0, 700, 360));       // 原来只是报文晚到了：这根手指已经作废，接着报会被系统当成从半路按下
        rig.Bridge.ReleaseAll();                            // 连接结束：没有要收的了
        Assert.Equal(1 + 2 + 7 + 1, rig.Sink.Frames.Count);

        rig.Touch(9000, new TouchPoint(0, 640, 360));       // 下一条连接里同一个编号的手指：照常按下
        Assert.Equal([At(0, 960, 540, ContactPhase.Down)], rig.Sink.Frames[^1]);
    }

    [Fact]
    public void 手指在动的时候不用另外报_触点快照本身就算Tab5的消息()
    {
        var rig = new Rig();
        for (int t = 0; t <= 2000; t += 50)                 // 拖了 2 秒，每 50 毫秒一份快照，中间没有 HELD
        {
            rig.Touch(t, new TouchPoint(0, (ushort)(100 + t / 4), 360));
            rig.Bridge.Tick(t);
        }
        Assert.Equal(41, rig.Sink.Frames.Count);
        Assert.Equal(ContactPhase.Down, rig.Sink.Frames[0][0].Phase);
        Assert.All(rig.Sink.Frames.Skip(1), f => Assert.Equal(ContactPhase.Move, f[0].Phase));
    }

    // ── 注入的地方抛异常 ──
    [Fact]
    public void 连发那一下注入时抛异常_不会每次醒来都重试_同一次里触点照样重报()
    {
        var rig = new Rig();
        rig.Key(0, 0, A);
        rig.Touch(0, new TouchPoint(0, 640, 360));
        rig.RunUntil(0, 499);                               // 触点重报了 100、200、300、400
        Assert.Equal(1 + 4, rig.Sink.Frames.Count);
        rig.Sink.FailKeys = true;
        Assert.Throws<InvalidOperationException>(() => rig.Bridge.Tick(500));       // 连发和重报都到点了
        Assert.Equal(1 + 4 + 1, rig.Sink.Frames.Count);     // 触点没有被连累
        Assert.Throws<InvalidOperationException>(() => rig.Bridge.Tick(533));       // 下一次是一个周期之后，不是马上
        rig.Sink.FailKeys = false;
        Assert.Equal(566 - 540, rig.Bridge.Tick(540), 3);
        Assert.Equal(1, rig.Sink.Downs(ScanA));
    }

    [Fact]
    public void 连接结束时松键抛异常_触点照样松开_之后没有要按时间做的事()
    {
        var rig = new Rig();
        rig.Key(0, 0, A);
        rig.Touch(0, new TouchPoint(0, 640, 360));
        rig.Sink.FailKeys = true;
        Assert.Throws<InvalidOperationException>(rig.Bridge.ReleaseAll);
        Assert.Equal([At(0, 960, 540, ContactPhase.Cancel)], rig.Sink.Frames[^1]);
        Assert.True(double.IsPositiveInfinity(rig.Bridge.Tick(1)));
    }

    [Fact]
    public void 注入触摸时抛异常_当成系统没收下_这根手指到抬起之前都不算()
    {
        var rig = new Rig();
        rig.Touch(0, new TouchPoint(0, 640, 360));
        rig.Sink.FailTouch = true;
        Assert.Throws<InvalidOperationException>(() => rig.Touch(20, new TouchPoint(0, 650, 360)));
        rig.Sink.FailTouch = false;
        int frames = rig.Sink.Frames.Count;
        rig.Touch(40, new TouchPoint(0, 660, 360));
        rig.RunUntil(40, 500);
        rig.Touch(500);
        Assert.Equal(frames, rig.Sink.Frames.Count);
        rig.Touch(600, new TouchPoint(0, 640, 360));
        Assert.Equal([At(0, 960, 540, ContactPhase.Down)], rig.Sink.Frames[^1]);
    }

    [Fact]
    public void 手指按着不动_隔一阵在原地重报一次_动了从那一帧重新算_抬起后不再报()
    {
        var rig = new Rig();
        rig.Touch(0, new TouchPoint(0, 640, 360));
        Assert.Equal([At(0, 960, 540, ContactPhase.Down)], rig.Sink.Frames[^1]);
        rig.RunUntil(0, 350);                               // 100、200、300 各重报一次
        Assert.Equal(1 + 3, rig.Sink.Frames.Count);
        Assert.All(rig.Sink.Frames.Skip(1), f => Assert.Equal([At(0, 960, 540, ContactPhase.Move)], f));
        rig.Touch(350, new TouchPoint(0, 700, 360));        // 手指动了
        rig.RunUntil(350, 449);                             // 离这一帧还不到 100 毫秒：不重报
        Assert.Equal(1 + 3 + 1, rig.Sink.Frames.Count);
        rig.RunUntil(449, 460);                             // 450 重报，在新的位置
        Assert.Equal([At(0, 1050, 540, ContactPhase.Move)], rig.Sink.Frames[^1]);
        rig.Touch(460);                                     // 抬起：位置和上一帧一样
        Assert.Equal([At(0, 1050, 540, ContactPhase.Up)], rig.Sink.Frames[^1]);
        int frames = rig.Sink.Frames.Count;
        Assert.True(double.IsPositiveInfinity(rig.Bridge.Tick(460)));
        rig.RunUntil(460, 2000);
        Assert.Equal(frames, rig.Sink.Frames.Count);
    }

    [Fact]
    public void 两根手指_一根抬起后另一根接着重报()
    {
        var rig = new Rig();
        rig.Touch(0, new TouchPoint(0, 100, 100), new TouchPoint(1, 1000, 600));
        rig.Touch(50, new TouchPoint(1, 1000, 600));        // 0 号抬起
        Assert.Equal([At(1, 1500, 900, ContactPhase.Move), At(0, 150, 150, ContactPhase.Up)], rig.Sink.Frames[^1]);
        rig.RunUntil(50, 160);
        Assert.Equal([At(1, 1500, 900, ContactPhase.Move)], rig.Sink.Frames[^1]);       // 重报里只有还按着的那一根
    }

    [Fact]
    public void 系统没收下一帧_这根手指到抬起之前都不算_抬起再按就恢复()
    {
        var rig = new Rig();
        rig.Touch(0, new TouchPoint(0, 640, 360));
        rig.Sink.RefuseTouch = true;
        rig.Touch(20, new TouchPoint(0, 650, 360));         // 这一帧系统没收（它同时把按着的触点都取消了）
        rig.Sink.RefuseTouch = false;
        int frames = rig.Sink.Frames.Count;
        rig.Touch(40, new TouchPoint(0, 660, 360));         // 手还在滑：不再注入，不然系统会当成从这里新按下
        rig.RunUntil(40, 500);                              // 也没有东西可重报
        rig.Touch(500);                                     // 抬起：系统那边早就没有这个触点了，不用报
        Assert.Equal(frames, rig.Sink.Frames.Count);
        rig.Touch(600, new TouchPoint(0, 640, 360));        // 再按下：照常
        Assert.Equal([At(0, 960, 540, ContactPhase.Down)], rig.Sink.Frames[^1]);
    }

    [Fact]
    public void 重报的那一帧系统没收下_不再重报()
    {
        var rig = new Rig();
        rig.Touch(0, new TouchPoint(0, 640, 360));
        rig.Sink.RefuseTouch = true;
        rig.RunUntil(0, 1000);
        Assert.Equal(1 + 1, rig.Sink.Frames.Count);         // 100 毫秒那一次没成，之后不再试
    }

    [Fact]
    public void 显示器换了分辨率_按着的手指作废_新按下的按新分辨率换算()
    {
        var rig = new Rig();
        rig.Touch(0, new TouchPoint(0, 640, 360));
        rig.Fit = Qhd;                                      // 抓屏按新尺寸重开了
        int frames = rig.Sink.Frames.Count;
        rig.Touch(50, new TouchPoint(0, 640, 360), new TouchPoint(1, 100, 100));    // 0 号还按着，又按下 1 号
        Assert.Equal(frames + 1, rig.Sink.Frames.Count);
        Assert.Equal([At(1, 201, 201, ContactPhase.Down)], rig.Sink.Frames[^1]);    // 0 号不在里面
        rig.Touch(100, new TouchPoint(1, 100, 100));        // 0 号抬起：没有它的抬起帧
        rig.Touch(150, new TouchPoint(0, 640, 360), new TouchPoint(1, 100, 100));   // 0 号重新按下：按新分辨率
        Assert.Contains(At(0, 1281, 721, ContactPhase.Down), rig.Sink.Frames[^1]);
    }

    [Fact]
    public void 还不知道画面在屏幕上占哪一块_触摸不算()
    {
        var rig = new Rig { Fit = default };
        rig.Touch(0, new TouchPoint(0, 640, 360));
        Assert.Empty(rig.Sink.Frames);
        rig.Fit = Fhd;
        rig.Touch(10, new TouchPoint(0, 640, 360));         // 知道了之后这根还按着的手指当成新按下（之前系统里没有它）
        Assert.Equal([At(0, 960, 540, ContactPhase.Down)], rig.Sink.Frames[^1]);
    }

    [Fact]
    public void 连接结束_按着的键和触点都松开_之后没有要按时间做的事()
    {
        var rig = new Rig();
        rig.Key(0, LShift, A);
        rig.Touch(0, new TouchPoint(0, 640, 360));
        rig.Bridge.ReleaseAll();
        Assert.Equal([new KeyAction(ScanA, false, false), new KeyAction(ScanLShift, false, false)], rig.Sink.KeyLog.Skip(2));
        Assert.Equal([At(0, 960, 540, ContactPhase.Cancel)], rig.Sink.Frames[^1]);      // 手并没有抬起：作废，不在这里点一下
        Assert.True(double.IsPositiveInfinity(rig.Bridge.Tick(1)));
        int keys = rig.Sink.KeyLog.Count, frames = rig.Sink.Frames.Count;
        rig.Bridge.ReleaseAll();                            // 没有按着的：什么都不做
        rig.RunUntil(1, 3000);
        Assert.Equal((keys, frames), (rig.Sink.KeyLog.Count, rig.Sink.Frames.Count));
    }

    [Fact]
    public void 进程要结束了_按着的都松开_之后再来的输入不算()
    {
        var rig = new Rig();
        rig.Key(0, 0, A);
        rig.Touch(0, new TouchPoint(0, 640, 360));
        rig.Bridge.Close();
        Assert.Equal(new KeyAction(ScanA, false, false), rig.Sink.KeyLog[^1]);
        Assert.Equal([At(0, 960, 540, ContactPhase.Cancel)], rig.Sink.Frames[^1]);
        int keys = rig.Sink.KeyLog.Count, frames = rig.Sink.Frames.Count;
        rig.Key(10, 0, B);                                  // 会话还没停，Tab5 又发来一个键：不注入（没有人负责松开它了）
        rig.Touch(10, new TouchPoint(1, 100, 100));
        rig.RunUntil(10, 2000);
        rig.Bridge.Close();
        Assert.Equal((keys, frames), (rig.Sink.KeyLog.Count, rig.Sink.Frames.Count));
    }

    // ── 带时钟和后台线程的那一层（真的等时间，所以只核对大概的数）──
    [Fact]
    public void 后台线程_按住的键按时连发_指示灯变了才报_关掉时松开按着的()
    {
        var sink = new SyncSink();
        int leds = 0b010;                                   // Caps Lock 亮着
        var seen = new List<byte>();
        var bridge = new InputBridge(sink, () => Fhd, () => new KeyRepeat(60, 20), keepAliveMs: 50);
        using (var pump = new InputPump(bridge, () => (byte)Volatile.Read(ref leds)))
        {
            pump.LedsChanged += v => { lock (seen) seen.Add(v); };
            pump.Start();
            byte[] report = [0, 0, A, 0, 0, 0, 0, 0];
            pump.Key(report);
            pump.Touch([new TouchPoint(0, 640, 360)]);
            // 等的这一阵 Tab5 一直在报「还按着」（机器忙、后台线程轮不上的时候，不能因为没报而停了连发）
            Assert.True(SpinWait.SpinUntil(() => { pump.Held(); return sink.Downs(ScanA) >= 4 && sink.TouchFrames >= 3; }, 3000),
                        $"3 秒里只连发了 {sink.Downs(ScanA) - 1} 次、重报了 {sink.TouchFrames - 1} 次");
            Volatile.Write(ref leds, 0b011);                // 主机上按了 Num Lock
            Assert.True(SpinWait.SpinUntil(() => { lock (seen) return seen.Count >= 2; }, 3000));
        }
        Assert.Equal(new KeyAction(ScanA, false, false), sink.LastKey);                 // 关掉时键松开了，触点作废了
        Assert.Equal(ContactPhase.Cancel, sink.LastFrame![0].Phase);
        Assert.Equal<byte>([0b010, 0b011], seen);                                     // 开始时报一次，之后变了才报
    }

    [Fact]
    public void 后台线程_指示灯的头一次状态一定报得到_哪怕之后灯一直不变()
    {
        // 事件是 Start 之前挂的：线程一起来读到的那一次不会没人收（以前线程在构造里就起了，挂晚了这一次就丢了，之后灯不变就再也不报）
        for (int round = 0; round < 50; round++)
        {
            int first = -1;
            using var pump = new InputPump(new InputBridge(new SyncSink(), () => Fhd, () => new KeyRepeat(500, 33), keepAliveMs: 100), () => 0b010);
            pump.LedsChanged += v => Interlocked.CompareExchange(ref first, v, -1);
            pump.Start();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref first) >= 0, 3000), $"第 {round} 轮：3 秒了还没报头一次指示灯状态");
            Assert.Equal(0b010, Volatile.Read(ref first));
        }
    }

    [Fact]
    public void 后台线程_没Start就来了输入_当场报错_不Start直接Dispose也行()
    {
        var sink = new SyncSink();
        var pump = new InputPump(new InputBridge(sink, () => Fhd, () => new KeyRepeat(500, 33), keepAliveMs: 100));
        Assert.Throws<InvalidOperationException>(() => pump.Key(new byte[8]));
        Assert.Throws<InvalidOperationException>(() => pump.Touch([new TouchPoint(0, 640, 360)]));
        Assert.Throws<InvalidOperationException>(pump.Held);
        pump.Dispose();
        pump.Dispose();

        using var twice = new InputPump(new InputBridge(sink, () => Fhd, () => new KeyRepeat(500, 33), keepAliveMs: 100));
        twice.Start();
        Assert.Throws<InvalidOperationException>(twice.Start);
    }

    // ── 接到会话上：对着假 Tab5 ──
    [Fact]
    public void 按着键的Tab5突然没了消息_连接没断_连发停下_键留到连接结束才松开()
    {
        // 第 2 帧之后按下 a，照协议每 200 毫秒报一次「还按着」，报了 3 次之后没了消息——连接没关，像 WiFi 断了、死机了
        using var tab5 = new FakeTab5
        {
            AfterFrame = 2,
            Then = c =>
            {
                c.Send(Proto.TKey, 0, [0, 0, A, 0, 0, 0, 0, 0]);
                for (int i = 0; i < 3; i++)
                {
                    Thread.Sleep(Proto.HeldPeriodMs);
                    c.Send(Proto.THeld);
                }
                Thread.Sleep(2500);
            },
        };
        var sink = new SyncSink();
        using var pump = new InputPump(new InputBridge(sink, () => Fhd, () => new KeyRepeat(100, 20), keepAliveMs: 50));
        pump.Start();
        var session = new DisplaySession(new DisplaySessionTests.MovingBar(long.MaxValue, fps: 50), new SessionOptions(), TestPairing.SenderAuth());
        session.Key += report => pump.Key(report);
        session.Held += pump.Held;
        using var cts = new CancellationTokenSource(TestLimit.Default);
        SessionResult? result = null;
        var run = new Thread(() =>
        {
            try
            {
                result = session.Run("127.0.0.1", tab5.Port, cts.Token);
            }
            finally
            {
                pump.ReleaseAll();
            }
        });
        run.Start();
        try
        {
            // 按下之后 0.6 秒里 Tab5 一直在报，再过 0.7 秒它报的状态就不新鲜了：连发从 0.1 秒到 1.3 秒，每 20 毫秒一次，约 60 次
            Assert.True(SpinWait.SpinUntil(() => sink.Downs(ScanA) >= 1 + 10, 5000), "按住的键没有连发");
            int last = -1, now = sink.Downs(ScanA);
            for (int i = 0; i < 20 && now != last; i++)     // 等它停下：连着 300 毫秒没有新的
            {
                last = now;
                Thread.Sleep(300);
                now = sink.Downs(ScanA);
            }
            // 不停的话，到假 Tab5 那 2.5 秒过完还要多连发 120 多次
            Assert.InRange(now, 1 + 10, 1 + 85);
            Thread.Sleep(500);
            Assert.Equal(now, sink.Downs(ScanA));           // 停下之后一个都不多
            Assert.True(sink.LastKey.Down, "连接还在的时候就把键松开了");
        }
        finally
        {
            cts.Cancel();
            run.Join();
        }
        Assert.Equal(SessionEnd.Cancelled, result!.End);
        Assert.Equal(new KeyAction(ScanA, false, false), sink.LastKey);     // 连接结束：松开
    }

    [Fact]
    public void 触摸板模式_触摸换成鼠标动作_不注入成触摸()
    {
        var rig = new Rig { Touchpad = true };
        rig.Touch(0, new TouchPoint(0, 830, 581));
        rig.Touch(60);
        Assert.Equal([MouseKind.LeftDown, MouseKind.LeftUp], rig.Sink.MouseLog.Select(a => a.Kind));
        Assert.Empty(rig.Sink.Frames);
        rig.RunUntil(60, 2000);     // 之后没有要按时间做的事（没有触点要重报）
        Assert.Empty(rig.Sink.Frames);
    }

    [Fact]
    public void 触摸板的加速_Tab5报了按它的算_连接结束之后回到默认()
    {
        // 快滑：每 16 毫秒 30 像素（每秒 1875 像素，放大到头）。第 1 步挪出轻点的范围、不补，之后 9 步
        static void Swipe(Rig rig, double t0)
        {
            for (int i = 0; i <= 10; i++) rig.Touch(t0 + 16 * i, new TouchPoint(0, (ushort)(100 + 30 * i), 360));
            rig.Touch(t0 + 216);
        }
        static int MovedX(Rig rig) => rig.Sink.MouseLog.Where(a => a.Kind == MouseKind.Move).Sum(a => a.X);

        var rig = new Rig { Touchpad = true };
        rig.Bridge.TouchpadGain(1.0);                           // Tab5 报：人把「触摸板加速」关了
        Swipe(rig, 0);
        Assert.Equal(9 * 45, MovedX(rig));                      // 每步 30 × 1.5，不放大

        rig.Bridge.TouchpadGain(3.0);                           // 改成「高」
        rig.Sink.MouseLog.Clear();
        Swipe(rig, 1000);
        Assert.Equal(9 * 135, MovedX(rig));

        rig.Bridge.ReleaseAll();                                // 连接结束。下一条连接上的 Tab5 是老固件，只报触摸板模式、不报加速
        rig.Touchpad = true;
        rig.Sink.MouseLog.Clear();
        Swipe(rig, 2000);
        Assert.Equal(9 * 90, MovedX(rig));                      // 默认 2 倍
    }

    [Fact]
    public void 手指按着的时候从触摸换成触摸板_按着的触点作废_这次接触剩下的不算_抬起之后按触摸板算()
    {
        var rig = new Rig();
        rig.Touch(0, new TouchPoint(0, 640, 360));
        Assert.Equal(ContactPhase.Down, Assert.Single(rig.Sink.Frames[^1]).Phase);
        rig.Touchpad = true;                                    // Tab5 报：换成触摸板模式了
        rig.Touch(16, new TouchPoint(0, 660, 360));
        Assert.Equal(ContactPhase.Cancel, Assert.Single(rig.Sink.Frames[^1]).Phase);
        int frames = rig.Sink.Frames.Count;
        for (int t = 32; t <= 320; t += 16) rig.Touch(t, new TouchPoint(0, (ushort)(660 + t), 360));
        rig.Touch(336);
        Assert.Empty(rig.Sink.MouseLog);                        // 半路接手的这一次：不挪光标，抬起也不点击
        Assert.Equal(frames, rig.Sink.Frames.Count);
        rig.Touch(1000, new TouchPoint(0, 830, 581));
        rig.Touch(1060);
        Assert.Equal([MouseKind.LeftDown, MouseKind.LeftUp], rig.Sink.MouseLog.Select(a => a.Kind));
        Assert.Equal(frames, rig.Sink.Frames.Count);
    }

    [Fact]
    public void 触摸板拖到一半换回触摸_左键松开_这次接触剩下的不算_之后照原样注入触摸()
    {
        var rig = new Rig { Touchpad = true };
        rig.Touch(0, new TouchPoint(0, 700, 300));
        rig.Touch(60);
        rig.Touch(160, new TouchPoint(0, 700, 300));            // 轻点之后马上再按下、滑出去：左键按着
        rig.Touch(176, new TouchPoint(0, 730, 300));
        Assert.Equal(MouseKind.LeftDown, rig.Sink.MouseLog[^1].Kind);
        rig.Touchpad = false;
        rig.Touch(192, new TouchPoint(0, 750, 300));
        Assert.Equal(MouseKind.LeftUp, rig.Sink.MouseLog[^1].Kind);
        Assert.Empty(rig.Sink.Frames);
        rig.Touch(208);
        Assert.Empty(rig.Sink.Frames);
        rig.Touch(1000, new TouchPoint(0, 640, 360));
        Contact c = Assert.Single(rig.Sink.Frames[^1]);
        Assert.Equal((960, 540, ContactPhase.Down), (c.X, c.Y, c.Phase));
    }

    [Fact]
    public void 没有手指按着的时候换方式_下一次触摸马上按新的算()
    {
        var rig = new Rig();
        rig.Touch(0, new TouchPoint(0, 640, 360));
        rig.Touch(60);
        int frames = rig.Sink.Frames.Count;
        rig.Touchpad = true;
        rig.Touch(1000, new TouchPoint(0, 830, 581));
        rig.Touch(1060);
        Assert.Equal([MouseKind.LeftDown, MouseKind.LeftUp], rig.Sink.MouseLog.Select(a => a.Kind));
        Assert.Equal(frames, rig.Sink.Frames.Count);
    }

    [Fact]
    public void 触摸板拖到一半连接结束_左键松开_下一条连接里的手指从头算()
    {
        var rig = new Rig { Touchpad = true };
        rig.Touch(0, new TouchPoint(0, 700, 300));
        rig.Touch(60);
        rig.Touch(160, new TouchPoint(0, 700, 300));
        rig.Touch(176, new TouchPoint(0, 730, 300));
        Assert.Equal(MouseKind.LeftDown, rig.Sink.MouseLog[^1].Kind);
        rig.Bridge.ReleaseAll();
        Assert.Equal(MouseKind.LeftUp, rig.Sink.MouseLog[^1].Kind);
        rig.Sink.MouseLog.Clear();
        rig.Bridge.ReleaseAll();                                // 再收一次：没有按着的，什么都不做
        Assert.Empty(rig.Sink.MouseLog);
        rig.Touchpad = true;                                    // 下一条连接：Tab5 握手走完又报了一次
        rig.Touch(5000, new TouchPoint(0, 830, 581));
        rig.Touch(5060);
        Assert.Equal([MouseKind.LeftDown, MouseKind.LeftUp], rig.Sink.MouseLog.Select(a => a.Kind));
    }

    [Fact]
    public void 连接结束之后触摸方式回到触摸_下一条连接上Tab5没报就照原样注入触摸()
    {
        var rig = new Rig { Touchpad = true };
        rig.Touch(0, new TouchPoint(0, 830, 581));
        rig.Touch(60);
        Assert.Equal([MouseKind.LeftDown, MouseKind.LeftUp], rig.Sink.MouseLog.Select(a => a.Kind));
        rig.Bridge.ReleaseAll();
        rig.Sink.MouseLog.Clear();
        // 下一条连接是一台不报触摸方式的 Tab5（老固件）：上一条连接上报的不能留着
        rig.Touch(3000, new TouchPoint(0, 640, 360));
        Contact c = Assert.Single(rig.Sink.Frames[^1]);
        Assert.Equal((960, 540, ContactPhase.Down), (c.X, c.Y, c.Phase));
        Assert.Empty(rig.Sink.MouseLog);
    }

    [Fact]
    public void Tab5又报了一次同样的触摸方式_按着的手指不受影响()
    {
        var rig = new Rig { Touchpad = true };
        rig.Touch(0, new TouchPoint(0, 700, 300));
        rig.Touch(60);
        rig.Touch(160, new TouchPoint(0, 700, 300));
        rig.Touch(176, new TouchPoint(0, 730, 300));            // 拖着
        rig.Sink.MouseLog.Clear();
        rig.Touchpad = true;
        Assert.Empty(rig.Sink.MouseLog);                        // 左键没有被松开
        rig.Touch(192, new TouchPoint(0, 750, 300));
        Assert.Equal(MouseKind.Move, rig.Sink.MouseLog[^1].Kind);
        rig.Touch(250);
        Assert.Equal(MouseKind.LeftUp, rig.Sink.MouseLog[^1].Kind);
    }

    [Fact]
    public void 拖着不动的时候Tab5报换回触摸_左键当场松开_不等下一份快照()
    {
        var rig = new Rig { Touchpad = true };
        rig.Touch(0, new TouchPoint(0, 700, 300));
        rig.Touch(60);
        rig.Touch(160, new TouchPoint(0, 700, 300));
        rig.Touch(176, new TouchPoint(0, 730, 300));
        Assert.Equal(MouseKind.LeftDown, rig.Sink.MouseLog[^1].Kind);
        rig.Touchpad = false;                                   // 手指按着不动时 Tab5 不发快照：要是等下一份快照才收，左键会一直按着
        Assert.Equal(MouseKind.LeftUp, rig.Sink.MouseLog[^1].Kind);
    }

    [Fact]
    public void 触摸板拖到一半Tab5没了消息_左键不松开_到连接结束才松()
    {
        var rig = new Rig { Touchpad = true };
        rig.Touch(0, new TouchPoint(0, 700, 300));
        rig.Touch(60);
        rig.Touch(160, new TouchPoint(0, 700, 300));
        rig.Touch(176, new TouchPoint(0, 730, 300));
        Assert.Equal(MouseKind.LeftDown, rig.Sink.MouseLog[^1].Kind);
        rig.Sink.MouseLog.Clear();
        rig.RunUntil(176, 6000, tab5Alive: false);              // Tab5 突然没了（连 HELD 都没有，连接还没断）
        Assert.Empty(rig.Sink.MouseLog);
        rig.Bridge.ReleaseAll();
        Assert.Equal([MouseKind.LeftUp], rig.Sink.MouseLog.Select(a => a.Kind));
    }

    private sealed class SyncSink : IInputSink
    {
        private readonly List<KeyAction> _keys = [];
        private int _frames;
        public IReadOnlyList<Contact>? LastFrame;

        public void Keys(IReadOnlyList<KeyAction> actions)
        {
            lock (_keys) _keys.AddRange(actions);
        }

        public bool Touch(IReadOnlyList<Contact> frame)
        {
            LastFrame = frame;
            Interlocked.Increment(ref _frames);
            return true;
        }

        public void Mouse(IReadOnlyList<MouseAction> actions)
        {
        }

        public int TouchFrames => Volatile.Read(ref _frames);

        public int Downs(byte scan)
        {
            lock (_keys) return _keys.Count(k => k.ScanCode == scan && k.Down);
        }

        public KeyAction LastKey
        {
            get { lock (_keys) return _keys[^1]; }
        }
    }
}
