using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>
/// 触摸板模式（<see cref="TouchpadMapper"/>）：Tab5 发来的触点快照 → 鼠标动作。Tab5 每 16 毫秒读一次触摸屏，位置变了才发一份快照，
/// 全部抬起时发一份空的。几条用例里的坐标是 2026-11-13 用手指在真机上点出来的（触摸回显的日志）。
/// </summary>
public class TouchpadTests
{
    private static readonly ViewFit Fhd = ViewFit.For(1920, 1080), Qhd = ViewFit.For(2560, 1440);     // 都是 16:9，没有黑边

    private sealed class Pad
    {
        private readonly TouchpadMapper _mapper = new();
        public readonly List<MouseAction> Log = [];
        public ViewFit Fit = Fhd;
        public double Now;

        public bool ButtonHeld => _mapper.ButtonHeld;

        /// <summary>手指滑得快时光标最多放大到几倍（人在 Tab5 上选的「触摸板加速」）。</summary>
        public double MaxGain { set => _mapper.MaxGain = value; }

        /// <summary>过了 afterMs 来一份快照（不给触点 = 全部抬起）。</summary>
        public Pad At(double afterMs, params (byte Id, int X, int Y)[] fingers)
        {
            Now += afterMs;
            Log.AddRange(_mapper.Apply([.. fingers.Select(f => new TouchPoint(f.Id, (ushort)f.X, (ushort)f.Y))], Fit, Now));
            return this;
        }

        /// <summary>一根手指（编号 0）每 16 毫秒一份快照，依次到这些位置。</summary>
        public Pad Stroke(params (int X, int Y)[] path)
        {
            foreach (var (x, y) in path) At(16, (0, x, y));
            return this;
        }

        /// <summary>一根手指从 (x, y) 起每 16 毫秒挪 (dx, dy)，一共 steps 步（连起点 steps + 1 份快照）。</summary>
        public Pad Slide(int x, int y, int dx, int dy, int steps)
        {
            for (int i = 0; i <= steps; i++) At(16, (0, x + dx * i, y + dy * i));
            return this;
        }

        /// <summary>两根手指（编号 0、1，横向隔 150 像素）从 (x, y) 起每 16 毫秒一起挪 (dx, dy)，一共 steps 步。</summary>
        public Pad Slide2(int x, int y, int dx, int dy, int steps)
        {
            for (int i = 0; i <= steps; i++) At(16, (0, x + dx * i, y + dy * i), (1, x + 150 + dx * i, y + dy * i));
            return this;
        }

        public Pad Lift(double afterMs = 56) => At(afterMs);        // Tab5 连着 40 毫秒读不到触点才报抬起

        public (int X, int Y) Moved => (Log.Where(a => a.Kind == MouseKind.Move).Sum(a => a.X), Log.Where(a => a.Kind == MouseKind.Move).Sum(a => a.Y));

        public int Wheel(MouseKind kind) => Log.Where(a => a.Kind == kind).Sum(a => a.X);

        public MouseKind[] Kinds => [.. Log.Select(a => a.Kind)];

        /// <summary>除了挪光标之外的动作，按先后。</summary>
        public MouseKind[] Clicks => [.. Log.Where(a => a.Kind != MouseKind.Move).Select(a => a.Kind)];
    }

    private static readonly MouseKind[] LeftClick = [MouseKind.LeftDown, MouseKind.LeftUp];
    private static readonly MouseKind[] RightClick = [MouseKind.RightDown, MouseKind.RightUp];

    [Fact]
    public void 轻点一下_只来得及报一个位置_抬起时左键点一下_光标不动()
    {
        // 真机上快速点一下：19 笔里都是只有一个采样点，接着就是全部抬起
        var pad = new Pad().At(0, (0, 830, 581)).Lift();
        Assert.Equal(LeftClick, pad.Kinds);
    }

    [Fact]
    public void 轻点时指尖滚了十几个像素_照样是点一下_光标没跟着动()
    {
        // 真机上的一笔：6 个采样点，纵向滚了 18 像素（96 毫秒）
        var pad = new Pad().Stroke((599, 376), (598, 369), (598, 367), (598, 365), (600, 361), (601, 358)).Lift();
        Assert.Equal(LeftClick, pad.Kinds);
    }

    [Fact]
    public void 按住不动半秒才抬起_不是轻点_什么都不做()
    {
        var pad = new Pad().At(0, (0, 600, 400)).At(16, (0, 601, 401)).Lift(500);
        Assert.Empty(pad.Log);
    }

    [Fact]
    public void 慢慢滑_光标在Tab5的画面上挪的和手指一样远_挪出轻点范围之前的那一段不补()
    {
        // 每 16 毫秒 2 像素 = 每秒 125 像素（不放大），一共 120 像素。挪出 20 像素是第 11 步（22 像素），之后的 49 步 × 2 = 98 像素；
        // 1920×1080 的屏上画面的一个像素是 1.5 个屏幕像素
        var pad = new Pad().Slide(400, 300, 2, 0, 60).Lift();
        Assert.Equal((147, 0), pad.Moved);
        Assert.Empty(pad.Clicks);                   // 滑完抬起不点击

        // 2560×1440 的屏：一个像素是 2 个屏幕像素；斜着滑两个方向各算各的
        var qhd = new Pad { Fit = Qhd }.Slide(400, 300, 0, 2, 60).Lift();
        Assert.Equal((0, 196), qhd.Moved);
    }

    [Fact]
    public void 滑得快的时候放大_没设过加速时最多两倍()
    {
        // 每 16 毫秒 30 像素 = 每秒 1875 像素，过了放大到头的速度：每一步 30 × 2 × 1.5 = 90 个屏幕像素。
        // 第 1 步（30 像素）挪出了轻点的范围、不补，之后 9 步
        var pad = new Pad().Slide(100, 360, 30, 0, 10).Lift();
        Assert.Equal((90 * 9, 0), pad.Moved);

        // 中间的速度按比例：每 16 毫秒 12 像素 = 每秒 750 像素 → 1 + 1 × (750 − 150) / 1350 ≈ 1.444 倍；头两步（24 像素）不补，之后 18 步
        var mid = new Pad().Slide(100, 360, 12, 0, 20).Lift();
        Assert.InRange(mid.Moved.X, (int)(18 * 12 * 1.5 * 1.443), (int)(18 * 12 * 1.5 * 1.446) + 1);
    }

    [Theory]
    [InlineData(1.0, 45)]       // 「关」：滑多快都是手指挪多远光标挪多远，30 × 1.5
    [InlineData(1.5, 67.5)]     // 「低」
    [InlineData(3.0, 135)]      // 「高」
    public void 加速的倍数是Tab5上选的_滑得快时每一步放大到那么多(double maxGain, double perStep)
    {
        var pad = new Pad { MaxGain = maxGain }.Slide(100, 360, 30, 0, 10).Lift();
        Assert.Equal(((int)(perStep * 9), 0), pad.Moved);

        // 慢慢滑（每秒 125 像素）不管选的哪一档都不放大：挪出 20 像素是第 11 步，之后的 49 步 × 2 × 1.5，和「滑动」那条用例一样
        var slow = new Pad { MaxGain = maxGain }.Slide(400, 300, 2, 0, 60).Lift();
        Assert.Equal((147, 0), slow.Moved);
    }

    [Fact]
    public void 滑到一半改了加速_从下一份快照起按新的算()
    {
        var pad = new Pad { MaxGain = 3 }.Slide(100, 360, 30, 0, 5);     // 第 1 步不补，之后 4 步 × 135
        Assert.Equal((135 * 4, 0), pad.Moved);
        pad.MaxGain = 1;                                                // 人把加速关了（现在只有调试命令能在连接中途改）
        pad.Stroke((280, 360), (310, 360)).Lift();                      // 接着滑两步，每步 30 × 1.5
        Assert.Equal((135 * 4 + 45 * 2, 0), pad.Moved);
    }

    [Fact]
    public void 加速的倍数小于1_拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TouchpadMapper { MaxGain = 0.9 });
    }

    [Fact]
    public void 几份快照挤在一起到_不当成滑得飞快()
    {
        // 网络上堵了一下：后面 5 份快照同一时刻到。每份只挪 2 像素，手指其实是慢慢滑的（Tab5 16 毫秒才读一次）
        var pad = new Pad().Slide(400, 300, 2, 0, 15);           // 到这里已经在挪光标了：(15 − 11) × 2 × 1.5 = 12
        Assert.Equal((12, 0), pad.Moved);
        for (int i = 16; i <= 20; i++) pad.At(0, (0, 400 + 2 * i, 300));
        Assert.Equal((12 + 5 * 3, 0), pad.Moved);
    }

    [Fact]
    public void 双击_两下落在同一个位置()
    {
        var pad = new Pad().At(0, (0, 700, 300)).Lift().At(90, (0, 703, 298)).At(16, (0, 704, 297)).Lift();
        Assert.Equal([MouseKind.LeftDown, MouseKind.LeftUp, MouseKind.LeftDown, MouseKind.LeftUp], pad.Kinds);     // 中间没有挪光标
        Assert.False(pad.ButtonHeld);
    }

    [Fact]
    public void 轻点之后马上再按住滑动_是拖动_抬起才松左键()
    {
        var pad = new Pad().At(0, (0, 700, 300)).Lift();
        pad.Log.Clear();
        pad.At(100, (0, 700, 300));
        Assert.Empty(pad.Kinds);                                // 刚按下还分不清：可能是双击的第二下，也可能是两指手势的第一根手指
        Assert.False(pad.ButtonHeld);
        pad.Slide(700, 300, 2, 2, 40);
        Assert.True(pad.ButtonHeld);
        Assert.Equal(MouseKind.LeftDown, pad.Kinds[0]);         // 看出是滑动的那一刻按下，光标还没挪：拖的是轻点的那个位置上的东西
        pad.Lift();
        Assert.Equal(MouseKind.LeftUp, pad.Kinds[^1]);
        Assert.Equal([MouseKind.LeftDown, MouseKind.LeftUp], pad.Clicks);      // 抬起时不再多点一下
        Assert.True(pad.Moved.X > 60 && pad.Moved.Y > 60);
    }

    [Fact]
    public void 轻点之后马上再按住_停了一会儿才开始挪_也是拖动()
    {
        var pad = new Pad().At(0, (0, 700, 300)).Lift();
        pad.Log.Clear();
        pad.At(120, (0, 701, 300));
        pad.At(450, (0, 702, 301));                             // 按着想了一会儿（手指不动时 Tab5 不发快照），才动了一点
        Assert.Equal([MouseKind.LeftDown], pad.Kinds);
        pad.Slide(702, 301, 3, 0, 20).Lift();
        Assert.Equal([MouseKind.LeftDown, MouseKind.LeftUp], pad.Clicks);
        Assert.True(pad.Moved.X > 60);
        Assert.Equal(MouseKind.LeftDown, pad.Kinds[0]);
    }

    [Fact]
    public void 轻点之后马上再按下_还没看出是不是滑动时换了手指编号_滑出去照样是拖动_光标不跳()
    {
        var pad = new Pad().At(0, (0, 700, 300)).Lift();
        pad.Log.Clear();
        pad.At(100, (0, 700, 300)).At(16, (3, 702, 300));       // 触摸屏把同一根手指换了个编号报
        Assert.Empty(pad.Kinds);
        for (int i = 1; i <= 30; i++) pad.At(16, (3, 702 + 2 * i, 300));
        pad.Lift();
        Assert.Equal([MouseKind.LeftDown, MouseKind.LeftUp], pad.Clicks);
        Assert.Equal(MouseKind.LeftDown, pad.Kinds[0]);
        Assert.InRange(pad.Moved.X, 40, 90);                    // 从新编号的位置起算：挪的不超过手指滑的 60 像素（×1.5），没有凭空跳一段
        Assert.Equal(0, pad.Moved.Y);
    }

    [Fact]
    public void 轻点之后马上再按下_还没看出是不是滑动时连接结束_没有要松的_之后按下滑动只挪光标()
    {
        var mapper = new TouchpadMapper();
        mapper.Apply([new TouchPoint(0, 700, 300)], Fhd, 0);
        Assert.Equal(LeftClick, mapper.Apply([], Fhd, 60).Select(a => a.Kind));
        Assert.Empty(mapper.Apply([new TouchPoint(0, 700, 300)], Fhd, 160));
        Assert.Empty(mapper.ReleaseAll());                      // 左键还没按下：换注入方式、连接结束时都没有要收的
        Assert.False(mapper.ButtonHeld);
        // 手没抬、接着滑（下一条连接，或者开关拨回来之后）：和之前那次轻点没有关系了，不拖
        var after = new List<MouseAction>();
        for (int i = 1; i <= 30; i++) after.AddRange(mapper.Apply([new TouchPoint(0, (ushort)(700 + 3 * i), 300)], Fhd, 200 + 16 * i));
        after.AddRange(mapper.Apply([], Fhd, 800));
        Assert.All(after, a => Assert.Equal(MouseKind.Move, a.Kind));
        Assert.True(after.Sum(a => a.X) > 0);
    }

    [Fact]
    public void 点一下之后马上两指轻点_是左键一下加右键一下_不多出一次双击()
    {
        // 点选一个文件，马上两根手指点出右键菜单。两根手指几乎从不落在同一份快照里：先看到的是一根
        var pad = new Pad().At(0, (0, 600, 400)).At(60).At(140, (0, 700, 400)).At(16, (0, 700, 400), (1, 850, 395)).At(64);
        Assert.Equal([.. LeftClick, .. RightClick], pad.Kinds);
        Assert.False(pad.ButtonHeld);
    }

    [Fact]
    public void 点一下之后马上两指滚动_只滚动_不多出一次双击()
    {
        var pad = new Pad().At(0, (0, 600, 400)).At(60).At(140, (0, 700, 300));
        pad.At(16, (0, 700, 301), (1, 850, 300));               // 第二根手指晚一份快照落下
        for (int i = 1; i <= 30; i++) pad.At(16, (0, 700, 301 + 8 * i), (1, 850, 300 + 8 * i));
        pad.At(16, (1, 850, 540)).Lift();                       // 一根先抬起
        Assert.Equal(LeftClick, pad.Clicks.Where(k => k is not (MouseKind.Wheel or MouseKind.HWheel)));
        Assert.True(pad.Wheel(MouseKind.Wheel) > 0);            // 手指往下 = 往上滚
        Assert.Equal((0, 0), pad.Moved);
    }

    [Fact]
    public void 轻点之后隔了半秒再按下滑动_只是挪光标_左键不按下()
    {
        var pad = new Pad().At(0, (0, 700, 300)).Lift();
        pad.Log.Clear();
        pad.At(500, (0, 700, 300)).Slide(700, 300, 2, 0, 40).Lift();
        Assert.Empty(pad.Clicks);
        Assert.True(pad.Moved.X > 0);
    }

    [Fact]
    public void 连着轻点三下_三连击()
    {
        var pad = new Pad();
        for (int i = 0; i < 3; i++) pad.At(i == 0 ? 0 : 100, (0, 700, 300)).Lift();
        Assert.Equal([.. LeftClick, .. LeftClick, .. LeftClick], pad.Kinds);
    }

    [Fact]
    public void 两根手指轻点_右键点一下_两根不是同时落下同时抬起也算()
    {
        // 第二根手指晚 16 毫秒落下，第一根先抬起
        var pad = new Pad().At(0, (0, 600, 400)).At(16, (0, 600, 400), (1, 760, 380)).At(48, (1, 761, 381)).Lift(32);
        Assert.Equal(RightClick, pad.Kinds);
    }

    [Fact]
    public void 两根手指往下滑_往上滚_内容跟着手指走()
    {
        // 每 16 毫秒 6 像素，21 步 = 126 像素。第 4 步挪出轻点的范围，第 5 步定下是竖着滚，之后 16 步 × 6 = 96 像素 = 两格（240）
        var pad = new Pad().Slide2(500, 200, 0, 6, 21).Lift();
        Assert.Equal(240, pad.Wheel(MouseKind.Wheel));
        Assert.All(pad.Log, a => Assert.Equal(MouseKind.Wheel, a.Kind));        // 不挪光标、不点击、不横着滚

        var up = new Pad().Slide2(500, 600, 0, -6, 21).Lift();
        Assert.Equal(-240, up.Wheel(MouseKind.Wheel));
    }

    [Fact]
    public void 两根手指横着滑_横向滚轮_往右滑是往左滚()
    {
        var pad = new Pad().Slide2(300, 360, 6, 0, 21).Lift();
        Assert.Equal(-240, pad.Wheel(MouseKind.HWheel));
        Assert.All(pad.Log, a => Assert.Equal(MouseKind.HWheel, a.Kind));
    }

    [Fact]
    public void 竖着滚的时候手指横向的晃动不算()
    {
        var pad = new Pad();
        for (int i = 0; i <= 40; i++)
        {
            int wobble = i % 4 < 2 ? 5 : -5;        // 一边往下滑一边左右晃
            pad.At(16, (0, 500 + wobble, 200 + 6 * i), (1, 650 + wobble, 200 + 6 * i));
        }
        pad.Lift();
        Assert.All(pad.Log, a => Assert.Equal(MouseKind.Wheel, a.Kind));
        Assert.True(pad.Wheel(MouseKind.Wheel) > 400);
    }

    [Fact]
    public void 滚完一根手指先抬起_剩下那根不挪光标_全抬起也不点击()
    {
        var pad = new Pad().Slide2(500, 200, 0, 6, 21);
        pad.Log.Clear();
        for (int i = 1; i <= 10; i++) pad.At(16, (0, 500 + 8 * i, 326));
        pad.Lift();
        Assert.Empty(pad.Log);
    }

    [Fact]
    public void 三根手指的接触不算_抬起之后照常()
    {
        var pad = new Pad().At(0, (0, 300, 300), (1, 500, 300), (2, 700, 300)).At(16, (0, 300, 320), (1, 500, 320), (2, 700, 320))
                           .At(16, (0, 300, 340)).Lift();
        Assert.Empty(pad.Log);
        pad.At(600, (0, 830, 581)).Lift();
        Assert.Equal(LeftClick, pad.Kinds);
    }

    [Fact]
    public void 拖着的时候放上第二根手指_左键松开_这次接触到抬起不再点击()
    {
        var pad = new Pad().At(0, (0, 700, 300)).Lift().At(100, (0, 700, 300)).Slide(700, 300, 2, 0, 20);
        Assert.True(pad.ButtonHeld);
        pad.Log.Clear();
        pad.At(16, (0, 740, 300), (1, 900, 300));
        Assert.Equal([MouseKind.LeftUp], pad.Kinds);
        pad.Lift();
        Assert.Equal([MouseKind.LeftUp], pad.Kinds);
    }

    [Fact]
    public void 拖到一半连接结束_左键松开_之后从头算()
    {
        var mapper = new TouchpadMapper();
        mapper.Apply([new TouchPoint(0, 700, 300)], Fhd, 0);
        Assert.Equal(LeftClick, mapper.Apply([], Fhd, 60).Select(a => a.Kind));
        Assert.Empty(mapper.Apply([new TouchPoint(0, 700, 300)], Fhd, 160));
        Assert.Equal([MouseKind.LeftDown], mapper.Apply([new TouchPoint(0, 730, 300)], Fhd, 176).Select(a => a.Kind));     // 滑出去了：开始拖
        Assert.True(mapper.ButtonHeld);
        Assert.Equal([MouseKind.LeftUp], mapper.ReleaseAll().Select(a => a.Kind));
        Assert.False(mapper.ButtonHeld);
        Assert.Empty(mapper.ReleaseAll());
        // 下一条连接：紧接着按下的不算「轻点之后马上再按下」
        Assert.Empty(mapper.Apply([new TouchPoint(0, 700, 300)], Fhd, 200));
        Assert.Equal(LeftClick, mapper.Apply([], Fhd, 260).Select(a => a.Kind));
    }
}
