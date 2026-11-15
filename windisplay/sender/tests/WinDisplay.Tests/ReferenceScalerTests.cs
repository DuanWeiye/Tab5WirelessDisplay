using System.Runtime.InteropServices;
using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>缩放的对照实现本身对不对：拿能手算出结果的画面来验（它是 Windows 上核对显卡缩放的依据）。</summary>
public class ReferenceScalerTests
{
    private const int W = Proto.ViewW, H = Proto.ViewH;

    private static uint At(byte[] view, int x, int y) => MemoryMarshal.Cast<byte, uint>(view)[y * W + x];

    private static byte[] Image(int w, int h, Func<int, int, uint> pixel)
    {
        var px = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) px[y * w + x] = pixel(x, y);
        return MemoryMarshal.AsBytes(px.AsSpan()).ToArray();
    }

    [Fact]
    public void 一样大_原样照搬_透明度填满()
    {
        var rng = new Random(11);
        byte[] src = new byte[W * H * 4];
        rng.NextBytes(src);
        byte[] got = ReferenceScaler.Scale(src, W, H, W * 4);
        for (int i = 0; i < src.Length; i++)
            Assert.Equal(i % 4 == 3 ? 255 : src[i], got[i]);
    }

    [Fact]
    public void 正好缩小一半_每个像素是2x2四个的平均()
    {
        // 2560×1440：每个 2×2 小块里四个像素的值是 4k、4k+4、4k+8、4k+12 这样的等差（平均数是整数，不牵涉舍入）
        byte[] src = Image(2560, 1440, (x, y) =>
        {
            uint baseV = (uint)((x / 2 * 7 + y / 2 * 13) % 60 * 4);
            uint v = baseV + (uint)((x & 1) * 4 + (y & 1) * 8);
            return 0xFF000000u | v << 16 | (v / 2) << 8 | (255 - v);        // 三个通道各不一样，串了能看出来
        });
        byte[] got = ReferenceScaler.Scale(src, 2560, 1440, 2560 * 4);
        for (int y = 0; y < H; y += 37)
            for (int x = 0; x < W; x += 41)
            {
                uint baseV = (uint)((x * 7 + y * 13) % 60 * 4);
                uint p = At(got, x, y);
                Assert.Equal(baseV + 6, p >> 16 & 0xFF);                    // 四个值 b、b+4、b+8、b+12 的平均
                Assert.Equal(255 - (baseV + 6), p & 0xFF);
                // 绿色通道是 v/2：四个值 b/2、b/2+2、b/2+4、b/2+6（b 是 4 的倍数），平均 b/2 + 3
                Assert.Equal(baseV / 2 + 3, p >> 8 & 0xFF);
            }
    }

    [Fact]
    public void 一点五倍缩小_黑白相间的细线变成该有的灰度()
    {
        // 1920×1080 → 1280×720：每个目标像素盖住 1.5 个源像素。源画面是一列黑一列白，目标第 dx 列盖住源的 [1.5dx, 1.5dx+1.5)：
        // dx 是偶数：整列 1.5dx（偶数列 = 黑的那种还是白的那种看 1.5dx 的奇偶）加上半列下一列；dx 是奇数：半列加整列
        byte[] src = Image(1920, 1080, (x, _) => (x & 1) == 0 ? 0xFF000000u : 0xFFFFFFFFu);
        byte[] got = ReferenceScaler.Scale(src, 1920, 1080, 1920 * 4);
        for (int dx = 0; dx < W; dx++)
        {
            double a = dx * 1.5, sum = 0;
            for (int x = (int)a; x < Math.Ceiling(a + 1.5); x++)
                sum += (Math.Min(a + 1.5, x + 1) - Math.Max(a, x)) * ((x & 1) == 0 ? 0 : 255);
            uint want = (uint)Math.Round(sum / 1.5, MidpointRounding.AwayFromZero);
            Assert.True(want is 85 or 170, $"第 {dx} 列手算出来是 {want}");       // 只会是 1/3 或 2/3 的灰
            foreach (int y in (int[])[0, 359, 719])
                Assert.Equal(0xFF000000u | want << 16 | want << 8 | want, At(got, dx, y));
        }
    }

    [Fact]
    public void 比例不是16比9_画面居中_两边是黑的()
    {
        // 16:10 的屏整屏纯色：中间 1152 列是那个颜色，左右各 64 列黑
        byte[] src = Image(1920, 1200, (_, _) => 0xFF3366CCu);
        byte[] got = ReferenceScaler.Scale(src, 1920, 1200, 1920 * 4);
        foreach (int y in (int[])[0, 400, 719])
        {
            Assert.Equal(0xFF000000u, At(got, 0, y));
            Assert.Equal(0xFF000000u, At(got, 63, y));
            Assert.Equal(0xFF3366CCu, At(got, 64, y));
            Assert.Equal(0xFF3366CCu, At(got, 1215, y));
            Assert.Equal(0xFF000000u, At(got, 1216, y));
            Assert.Equal(0xFF000000u, At(got, 1279, y));
        }
        // 带鱼屏：上下各 92 行黑
        src = Image(3440, 1440, (_, _) => 0xFF3366CCu);
        got = ReferenceScaler.Scale(src, 3440, 1440, 3440 * 4);
        Assert.Equal(0xFF000000u, At(got, 640, 91));
        Assert.Equal(0xFF3366CCu, At(got, 640, 92));
        Assert.Equal(0xFF3366CCu, At(got, 640, 627));
        Assert.Equal(0xFF000000u, At(got, 640, 628));
    }

    [Fact]
    public void 四个角的色块缩完还在四个角_没有翻转也没有串通道()
    {
        // 4K 屏的四个角各一块 300×300 的纯色：左上红、右上绿、左下蓝、右下白（和 wdsend 的测试卡同一个约定）
        byte[] src = Image(3840, 2160, (x, y) => (x < 300, x >= 3540, y < 300, y >= 1860) switch
        {
            (true, _, true, _) => 0xFFFF0000u,
            (_, true, true, _) => 0xFF00FF00u,
            (true, _, _, true) => 0xFF0000FFu,
            (_, true, _, true) => 0xFFFFFFFFu,
            _ => 0xFF202830u,
        });
        byte[] got = ReferenceScaler.Scale(src, 3840, 2160, 3840 * 4);
        Assert.Equal(0xFFFF0000u, At(got, 50, 50));
        Assert.Equal(0xFF00FF00u, At(got, W - 50, 50));
        Assert.Equal(0xFF0000FFu, At(got, 50, H - 50));
        Assert.Equal(0xFFFFFFFFu, At(got, W - 50, H - 50));
        Assert.Equal(0xFF202830u, At(got, W / 2, H / 2));
    }

    [Fact]
    public void 行间有填充_不把填充当成画面()
    {
        // 显卡读回来的画面一行可能比 宽×4 长：多出来的字节不能算进去
        const int w = 1920, h = 1080, stride = w * 4 + 64;
        var src = new byte[stride * h];
        Array.Fill(src, (byte)0xEE);                                        // 填充全是 0xEE
        for (int y = 0; y < h; y++)
            MemoryMarshal.Cast<byte, uint>(src.AsSpan(y * stride, w * 4)).Fill(0xFF102030u);
        byte[] got = ReferenceScaler.Scale(src, w, h, stride);
        Assert.All(MemoryMarshal.Cast<byte, uint>(got).ToArray(), p => Assert.Equal(0xFF102030u, p));
    }

    [Fact]
    public void 缓冲不够大_报错()
    {
        Assert.Throws<ArgumentException>(() => ReferenceScaler.Scale(new byte[1920 * 4 * 1079], 1920, 1080, 1920 * 4));
        Assert.Throws<ArgumentException>(() => ReferenceScaler.Scale(new byte[1920 * 4 * 1080], 1920, 1080, 1919 * 4));
    }
}
