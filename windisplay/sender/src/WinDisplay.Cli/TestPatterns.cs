using System.Runtime.InteropServices;
using WinDisplay.Core;

namespace WinDisplay.Cli;

/// <summary>
/// 生成的测试画面（横屏 1280×720，B G R A），和 windisplay/tools/wd_send_test.py 的同名画面对应（没有文字）：
///   card    测试卡：四角色块（左上红、右上绿、左下蓝、右下白，各 60×60，Tab5 的调试命令 wd check 拿它核对方向和红蓝顺序）、
///           色条、80 像素网格、一个随帧号变长的进度条、一个横向移动的方块。每帧只有一两个条带在变（局部更新）。
///   scroll  测试卡整屏每帧横移 8 像素（整屏都在变，接近滚动 / 看视频）。
///           detail > 0 时测试卡上再叠一层细碎的纹理（跟着一起移）：越大每帧压出来越大，像满屏小字的网页、细节多的游戏画面。
///   noise   每帧一屏随机噪点（压不动，单帧最大，压链路用）。
/// 每取一次就是下一帧；取满 frames 帧之后报 End（frames = 0：不限，一直给）。
/// </summary>
public sealed class TestPatternSource : IFrameSource
{
    private const int W = Proto.ViewW, H = Proto.ViewH;
    private readonly string _pattern;
    private readonly long _frames;
    private readonly FramePacer? _pacer;        // 限帧率；null = 要多快给多快
    private readonly uint[] _base = new uint[W * H];
    private readonly Random _rng = new(1);
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private long _n;

    /// <summary>最近给出的那一帧（B G R A）；还没给过是空的。--dump-last 用它核对 Tab5 收到的画面。</summary>
    public byte[] Last { get; private set; } = [];

    /// <param name="fps">限帧率；0 = 要多快给多快。</param>
    /// <param name="detail">测试卡上叠的细碎纹理的强度 0~1（每个像素的亮度随机加减，最多这么多 × 255）；0 = 不叠。</param>
    public TestPatternSource(string pattern, long frames, double fps, double detail = 0)
    {
        _pattern = pattern;
        _frames = frames;
        _pacer = fps > 0 ? new FramePacer(fps) : null;
        DrawCard(_base);
        if (detail > 0)
        {
            var grain = new Random(2);
            for (int i = 0; i < _base.Length; i++)
            {
                int d = (int)((grain.NextDouble() * 2 - 1) * 255 * detail);
                uint p = _base[i];
                int r = Math.Clamp((int)(p >> 16 & 0xFF) + d, 0, 255), g = Math.Clamp((int)(p >> 8 & 0xFF) + d, 0, 255), b = Math.Clamp((int)(p & 0xFF) + d, 0, 255);
                _base[i] = Bgra(r, g, b);
            }
        }
    }

    public FrameResult TryGetFrame(Span<byte> dst, TimeSpan timeout)
    {
        if (_frames > 0 && _n >= _frames) return FrameResult.End;
        if (_pacer is not null)
        {
            double waitMs = _pacer.WaitMs(_clock.Elapsed.TotalMilliseconds);
            if (waitMs > timeout.TotalMilliseconds)
            {
                Thread.Sleep(timeout);
                return FrameResult.NoChange;
            }
            if (waitMs > 0) Thread.Sleep(TimeSpan.FromMilliseconds(waitMs));
            _pacer.Given(_clock.Elapsed.TotalMilliseconds);
        }
        Span<uint> px = MemoryMarshal.Cast<byte, uint>(dst);
        long n = _n++;
        switch (_pattern)
        {
            case "noise":
                _rng.NextBytes(dst);
                break;
            case "scroll":
                int shift = (int)(n * 8 % W);       // 整幅画面右移 shift 像素，右边出去的从左边回来
                for (int y = 0; y < H; y++)
                {
                    ReadOnlySpan<uint> row = _base.AsSpan(y * W, W);
                    row[..(W - shift)].CopyTo(px.Slice(y * W + shift, W - shift));
                    row[(W - shift)..].CopyTo(px.Slice(y * W, shift));
                }
                break;
            default:
                _base.CopyTo(px);
                Fill(px, 520, 470, 240, 90, Bgra(15, 23, 42));
                Fill(px, 520, 470, (int)(n * 4 % 240) + 1, 90, Bgra(45, 212, 191));
                Fill(px, (int)(n * 12 % (W - 240)) + 100, 600, 40, 40, Bgra(248, 113, 113));
                break;
        }
        Last = dst[..(W * H * 4)].ToArray();
        return FrameResult.Frame;
    }

    private static uint Bgra(int r, int g, int b) => 0xFF000000u | (uint)r << 16 | (uint)g << 8 | (uint)b;     // 内存里是 B G R A

    private static void Fill(Span<uint> px, int x, int y, int w, int h, uint color)
    {
        for (int yy = y; yy < y + h; yy++) px.Slice(yy * W + x, w).Fill(color);
    }

    private static void DrawCard(Span<uint> px)
    {
        px.Fill(Bgra(24, 28, 36));
        uint grid = Bgra(52, 58, 70);
        for (int x = 0; x < W; x += 80) Fill(px, x, 0, 1, H, grid);
        for (int y = 0; y < H; y += 80) Fill(px, 0, y, W, 1, grid);
        (int r, int g, int b)[] bars = [(255, 255, 255), (255, 255, 0), (0, 255, 255), (0, 255, 0), (255, 0, 255), (255, 0, 0), (0, 0, 255), (0, 0, 0)];
        for (int i = 0; i < bars.Length; i++) Fill(px, 160 + i * 120, 100, 120, 120, Bgra(bars[i].r, bars[i].g, bars[i].b));
        for (int ch = 0; ch < 3; ch++)          // 红、绿、蓝三条渐变
            for (int x = 0; x < 960; x++)
            {
                int v = x * 255 / 959;
                Fill(px, 160 + x, 240 + ch * 44, 1, 40, Bgra(ch == 0 ? v : 0, ch == 1 ? v : 0, ch == 2 ? v : 0));
            }
        Fill(px, 0, 0, 60, 60, Bgra(255, 0, 0));
        Fill(px, W - 60, 0, 60, 60, Bgra(0, 255, 0));
        Fill(px, 0, H - 60, 60, 60, Bgra(0, 0, 255));
        Fill(px, W - 60, H - 60, 60, 60, Bgra(255, 255, 255));
    }
}
