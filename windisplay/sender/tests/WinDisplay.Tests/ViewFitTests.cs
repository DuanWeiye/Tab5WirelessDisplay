using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>显示器的画面怎么放进 1280×720：等比、居中，常见的几种屏各占哪一块。</summary>
public class ViewFitTests
{
    [Theory]
    // 16:9 的屏正好占满
    [InlineData(1920, 1080, 0, 0, 1280, 720)]
    [InlineData(2560, 1440, 0, 0, 1280, 720)]
    [InlineData(3840, 2160, 0, 0, 1280, 720)]
    [InlineData(1280, 720, 0, 0, 1280, 720)]
    // 1366×768 差一点不到 16:9，取整之后也占满
    [InlineData(1366, 768, 0, 0, 1280, 720)]
    // 16:10：左右留黑边
    [InlineData(1920, 1200, 64, 0, 1152, 720)]
    [InlineData(2560, 1600, 64, 0, 1152, 720)]
    // 带鱼屏：上下留黑边
    [InlineData(3440, 1440, 0, 92, 1280, 536)]
    [InlineData(2560, 1080, 0, 90, 1280, 540)]
    // 竖着放的屏
    [InlineData(1080, 1920, 437, 0, 405, 720)]
    // 比 1280×720 小的屏：放大
    [InlineData(800, 600, 160, 0, 960, 720)]
    // 极端的长条也至少留一个像素
    [InlineData(10000, 1, 0, 359, 1280, 1)]
    [InlineData(1, 10000, 639, 0, 1, 720)]
    public void 等比缩放居中(int srcW, int srcH, int x, int y, int w, int h)
    {
        Assert.Equal(new ViewFit(srcW, srcH, x, y, w, h), ViewFit.For(srcW, srcH));
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(-1, 1080)]
    public void 尺寸不是正数_报错(int srcW, int srcH)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ViewFit.For(srcW, srcH));
    }

    [Fact]
    public void 各种尺寸_都落在画面里而且至少占满一个方向()
    {
        var rng = new Random(3);
        for (int i = 0; i < 2000; i++)
        {
            int w = rng.Next(1, 8000), h = rng.Next(1, 8000);
            ViewFit f = ViewFit.For(w, h);
            Assert.True(f.X >= 0 && f.Y >= 0 && f.W >= 1 && f.H >= 1 && f.X + f.W <= Proto.ViewW && f.Y + f.H <= Proto.ViewH, $"{w}×{h} → {f}");
            Assert.True(f.W == Proto.ViewW || f.H == Proto.ViewH, $"{w}×{h} → {f} 两个方向都没占满");
            // 居中：两边的黑边最多差一个像素
            Assert.InRange(Proto.ViewW - f.W - 2 * f.X, 0, 1);
            Assert.InRange(Proto.ViewH - f.H - 2 * f.Y, 0, 1);
            // 等比：没占满的那个方向和按比例算出来的差不到一个像素
            if (f.W == Proto.ViewW && f.H > 1) Assert.InRange(f.H - (double)h * Proto.ViewW / w, -0.5001, 0.5001);
            if (f.H == Proto.ViewH && f.W > 1) Assert.InRange(f.W - (double)w * Proto.ViewH / h, -0.5001, 0.5001);
        }
    }
}
