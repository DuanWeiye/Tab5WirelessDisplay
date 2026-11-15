using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Win.Tests;

/// <summary>测试用的 Direct3D 设备：优先用显卡，建不了（比如经 ssh 跑、机器上没有可用的显卡）就用软件渲染——着色器是同一个，结果应当一样。</summary>
public sealed class GpuFixture : IDisposable
{
    internal ID3D11Device Device { get; }
    internal ID3D11DeviceContext Context { get; }
    public bool Hardware { get; }

    public GpuFixture()
    {
        try
        {
            (Device, Context) = Gpu.CreateDevice(hardware: true);
            Hardware = true;
        }
        catch (CaptureLostException)
        {
            (Device, Context) = Gpu.CreateDevice(hardware: false);
        }
    }

    public void Dispose()
    {
        Context.Dispose();
        Device.Dispose();
    }
}

/// <summary>显卡上的缩放：各种分辨率的屏缩成 1280×720，和 CPU 上的对照实现（ReferenceScaler，另有单元测试验它）逐像素比。</summary>
public class GpuScalerTests(GpuFixture gpu) : IClassFixture<GpuFixture>
{
    private const int ViewBytes = Proto.ViewW * 4 * Proto.ViewH;

    /// <summary>
    /// 一幅像桌面的画面（B G R A）：深色底、几个窗口、一条任务栏，外加缩放最容易出毛病的东西——1 像素宽的格线、
    /// 黑白相间的细条纹、像小字的散点、渐变、一块噪点；四个角各一块不同颜色的色块（翻转了、串通道了能看出来）。
    /// </summary>
    private static byte[] Desktop(int w, int h, int seed)
    {
        var px = new uint[w * h];
        var rng = new Random(seed);
        Array.Fill(px, 0xFF202830u);
        void Fill(int x, int y, int fw, int fh, uint c)
        {
            for (int r = Math.Max(0, y); r < Math.Min(h, y + fh); r++)
                px.AsSpan(r * w + Math.Max(0, x), Math.Max(0, Math.Min(w, x + fw) - Math.Max(0, x))).Fill(c);
        }
        Fill(w / 16, h / 12, w / 2, h / 2, 0xFFF0F0F0u);                     // 浅色窗口
        Fill(w / 2 + w / 20, h / 6, w / 3, h / 3, 0xFF2D6CDFu);              // 蓝色窗口
        Fill(0, h - h / 22, w, h / 22, 0xFF101418u);                         // 任务栏
        for (int x = w / 16; x < w / 16 + w / 2; x += 7) Fill(x, h / 12, 1, h / 2, 0xFFB0B0B0u);       // 1 像素的竖线
        for (int y = h / 12; y < h / 12 + h / 2; y += 5) Fill(w / 16, y, w / 2, 1, 0xFF909090u);       // 1 像素的横线
        for (int x = 0; x < w / 4; x++)                                       // 黑白相间的细条纹
            if ((x & 1) == 1) Fill(w / 2 + w / 20 + x, h / 6 + h / 3 + 10, 1, h / 10, 0xFFFFFFFFu);
        for (int i = 0; i < w * h / 200; i++)                                 // 像小字的散点
            px[rng.Next(h / 12, h / 12 + h / 2) * w + rng.Next(w / 16, w / 16 + w / 2)] = 0xFF000000u;
        for (int x = 0; x < w; x++)                                           // 横向渐变
        {
            uint v = (uint)(x * 255L / (w - 1));
            Fill(x, h * 2 / 3, 1, h / 20, 0xFF000000u | v << 16 | (255 - v) << 8 | v / 2);
        }
        for (int y = h * 3 / 4; y < h * 3 / 4 + h / 8; y++)                   // 一块噪点
            for (int x = w / 10; x < w / 10 + w / 5; x++) px[y * w + x] = 0xFF000000u | (uint)rng.Next(0x1000000);
        int corner = Math.Max(8, Math.Min(w, h) / 12);
        Fill(0, 0, corner, corner, 0xFFFF0000u);
        Fill(w - corner, 0, corner, corner, 0xFF00FF00u);
        Fill(0, h - corner, corner, corner, 0xFF0000FFu);
        Fill(w - corner, h - corner, corner, corner, 0xFFFFFFFFu);
        return MemoryMarshal.AsBytes(px.AsSpan()).ToArray();
    }

    /// <summary>把一幅画面传上显卡，用 scaler 缩回来。texW / texH 给得比画面大时，画面放在纹理的左上角，其余是填充色（抓屏的缓冲比画面大时就是这样）。</summary>
    private byte[] ScaleOnGpu(GpuScaler scaler, byte[] image, int w, int h, int texW = 0, int texH = 0)
    {
        if (texW == 0) (texW, texH) = (w, h);
        byte[] upload = image;
        if (texW != w || texH != h)
        {
            upload = new byte[texW * 4 * texH];
            Array.Fill(upload, (byte)0xEE);
            for (int y = 0; y < h; y++) image.AsSpan(y * w * 4, w * 4).CopyTo(upload.AsSpan(y * texW * 4));
        }
        using ID3D11Texture2D texture = gpu.Device.CreateTexture2D<byte>(upload, Format.B8G8R8A8_UNorm, (uint)texW, (uint)texH, bindFlags: BindFlags.ShaderResource);
        var dst = new byte[ViewBytes];
        Assert.Equal(ViewFit.For(w, h), scaler.Scale(texture, w, h, dst));
        return dst;
    }

    private static void AssertMatches(byte[] want, byte[] got, int tolerance, string what)
    {
        int worst = 0, at = -1;
        for (int i = 0; i < want.Length; i++)
        {
            int d = Math.Abs(want[i] - got[i]);
            if (d > worst) (worst, at) = (d, i);
        }
        Assert.True(worst <= tolerance,
            $"{what}：和对照最多差 {worst}（容许 {tolerance}），在第 {at / 4 / Proto.ViewW} 行第 {at / 4 % Proto.ViewW} 列的第 {at % 4} 个通道：对照 {(at < 0 ? 0 : want[at])}，显卡 {(at < 0 ? 0 : got[at])}");
    }

    [Theory]
    [InlineData(1920, 1080)]        // 这台电脑；缩 1.5 倍
    [InlineData(2560, 1440)]        // 正好缩一半
    [InlineData(3840, 2160)]        // 4K，缩 3 倍
    [InlineData(1920, 1200)]        // 16:10，左右有黑边
    [InlineData(2560, 1600)]
    [InlineData(3440, 1440)]        // 带鱼屏，上下有黑边，缩 2.69 倍
    [InlineData(1366, 768)]         // 老笔记本，缩 1.07 倍
    [InlineData(1080, 1920)]        // 竖着放的屏
    [InlineData(800, 600)]          // 比 1280×720 小：放大
    [InlineData(5120, 2880)]        // 5K，缩 4 倍
    public void 各种分辨率_和对照实现逐像素一致(int w, int h)
    {
        byte[] image = Desktop(w, h, seed: w + h);
        using var scaler = new GpuScaler(gpu.Device, gpu.Context);
        byte[] got = ScaleOnGpu(scaler, image, w, h);
        // 显卡用单精度算、对照用双精度算，正好落在两个整数中间的值舍入方向可能不同：容许差 1
        AssertMatches(ReferenceScaler.Scale(image, w, h, w * 4), got, 1, $"{w}×{h}（{(gpu.Hardware ? "显卡" : "软件渲染")}）");
        Assert.True(CaptureCheck.BarsAreBlack(got, ViewFit.For(w, h)), "画面没盖到的地方不是全黑");
    }

    [Fact]
    public void 一样大_原样照搬()
    {
        byte[] image = Desktop(Proto.ViewW, Proto.ViewH, seed: 5);
        using var scaler = new GpuScaler(gpu.Device, gpu.Context);
        byte[] got = ScaleOnGpu(scaler, image, Proto.ViewW, Proto.ViewH);
        Assert.True(image.AsSpan().SequenceEqual(got), "1280×720 的画面缩放之后应该一个字节都不变");
    }

    [Fact]
    public void 中途换分辨率_同一个缩放器接着用()
    {
        // 抓屏开着的时候用户改了分辨率、把屏转成竖的又转回来：缩放器不重建，每次都得对
        using var scaler = new GpuScaler(gpu.Device, gpu.Context);
        foreach ((int w, int h) in ((int, int)[])[(1920, 1080), (2560, 1440), (1080, 1920), (1920, 1080), (1280, 720), (1920, 1200)])
        {
            byte[] image = Desktop(w, h, seed: w * 3 + h);
            AssertMatches(ReferenceScaler.Scale(image, w, h, w * 4), ScaleOnGpu(scaler, image, w, h), 1, $"换到 {w}×{h}");
        }
    }

    [Fact]
    public void 纹理比画面大_只取左上角那一块()
    {
        const int w = 1920, h = 1080;
        byte[] image = Desktop(w, h, seed: 9);
        using var scaler = new GpuScaler(gpu.Device, gpu.Context);
        byte[] got = ScaleOnGpu(scaler, image, w, h, texW: 2560, texH: 1440);
        AssertMatches(ReferenceScaler.Scale(image, w, h, w * 4), got, 1, "纹理 2560×1440、画面 1920×1080");
    }

    [Fact]
    public void 画面一直在变_连着缩一百幅_每幅都对()
    {
        // 同一个缩放器一幅接一幅地用（真用起来就是这样）：上一幅的东西不能留到下一幅里
        const int w = 1920, h = 1080;
        using var scaler = new GpuScaler(gpu.Device, gpu.Context);
        byte[] image = Desktop(w, h, seed: 21);
        var px = MemoryMarshal.Cast<byte, uint>(image.AsSpan());
        for (int n = 0; n < 100; n++)
        {
            for (int y = 300; y < 340; y++) px.Slice(y * w + n * 15, 40).Fill(0xFFFF8040u);        // 一个方块每幅右移 15 像素
            byte[] got = ScaleOnGpu(scaler, image, w, h);
            if (n % 10 == 0 || n == 99) AssertMatches(ReferenceScaler.Scale(image, w, h, w * 4), got, 1, $"第 {n} 幅");
            for (int y = 300; y < 340; y++) px.Slice(y * w + n * 15, 40).Fill(0xFFF0F0F0u);
        }
    }

    [Fact]
    public void 自检用的比较_分得出对错()
    {
        const int w = 1920, h = 1200;
        ViewFit fit = ViewFit.For(w, h);
        byte[] view = ReferenceScaler.Scale(Desktop(w, h, seed: 2), w, h, w * 4);
        Assert.Equal((99, 0, 0), CaptureCheck.Compare(view, view, fit));
        Assert.True(CaptureCheck.BarsAreBlack(view, fit));
        foreach (byte[] wrong in (byte[][])[
            CaptureCheck.Transform(view, fit, (x, y) => (fit.W - 1 - x, y), swapRedBlue: false),
            CaptureCheck.Transform(view, fit, (x, y) => (x, fit.H - 1 - y), swapRedBlue: false),
            CaptureCheck.Transform(view, fit, (x, y) => (x, y), swapRedBlue: true),
            CaptureCheck.Transform(view, fit, (x, y) => ((x + 8) % fit.W, y), swapRedBlue: false)])
        {
            Assert.InRange(CaptureCheck.Compare(view, wrong, fit).Psnr, 0, 25);
            Assert.True(CaptureCheck.BarsAreBlack(wrong, fit));
        }
        byte[] dirty = (byte[])view.Clone();
        dirty[0] = 1;                                   // 左上角在黑边里
        Assert.False(CaptureCheck.BarsAreBlack(dirty, fit));
    }
}
