using System.Runtime.InteropServices;
using WinDisplay.Core;

namespace WinDisplay.Win;

/// <summary>抓屏自检的结果。</summary>
/// <param name="SourceWidth">主显示器的分辨率。</param>
/// <param name="Fit">画面在 1280×720 里占的位置。</param>
/// <param name="Psnr">抓屏那条路的结果和对照的接近程度（分贝，越大越接近；两边完全一样记 99）。</param>
/// <param name="OffPixels">画面那一块里，有任何一个通道差了 2 以上的像素占百分之几。</param>
/// <param name="MaxDiff">差得最多的一个通道差多少。</param>
/// <param name="BarsBlack">画面没盖到的地方是不是全黑（比例正好 16:9 时没有这样的地方，记 true）。</param>
/// <param name="WrongPsnr">故意弄错的几种对照（左右翻转、上下翻转、红蓝对调、错开 8 个像素）里最接近的那个的分贝数：应该比 Psnr 低得多，
/// 不然说明屏幕上的内容分不出对错（比如整屏一个颜色），这次自检不算数。</param>
public sealed record CaptureCheckResult(int SourceWidth, int SourceHeight, ViewFit Fit, double Psnr, double OffPixels, int MaxDiff, bool BarsBlack,
                                        double WrongPsnr, ScreenCaptureStats Stats)
{
    /// <summary>「符合」的门槛：至少这么多分贝、差了 2 以上的像素最多占百分之这么多、比故意弄错的对照至少高这么多分贝。</summary>
    public const double MinPsnr = 40, MaxOffPixels = 1, MinMargin = 6;

    /// <summary>
    /// 抓屏那条路给出的画面和对照一致，而且这次比较分得出对错。
    /// 门槛是按「细微的错也要露出来」定的：屏幕静止时两条路只差显卡和 CPU 各自的舍入，实测 72.6 dB、差 2 以上的像素是 0；
    /// 错开一个像素、换成双线性缩放、源画面被系统按缩放比例折算过，在有文字和窗口边线的桌面上都到不了 40 dB，差 2 以上的像素也远不止 1%。
    /// 屏幕上有东西在动（视频、光标闪）的时候会因此判成不符：等它静下来重跑。
    /// </summary>
    public bool Ok => BarsBlack && Psnr >= MinPsnr && OffPixels <= MaxOffPixels && Psnr >= WrongPsnr + MinMargin;
}

/// <summary>
/// 抓屏自检：用发画面走的那条路（WGC → 显卡缩放 → 读回）抓一幅主显示器的画面，同时用完全不相干的另一条路
/// （GDI 把整屏原样拷下来 → <see cref="ReferenceScaler"/> 在 CPU 上缩）得到一幅对照，比较两边。
/// 位置、方向、红蓝顺序、缩放有一样不对，两边就对不上。全程只出数字，不把屏幕内容存下来或者发出去。
/// 两条路不是同一瞬间抓的：屏幕上有东西在动的时候会有出入，判成不符就等屏幕静下来重跑一次（门槛见 <see cref="CaptureCheckResult.Ok"/>）。
/// </summary>
public static class CaptureCheck
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor, Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, in BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int srcX, int srcY, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();

    private const uint SrcCopy = 0x00CC0020, CaptureBlt = 0x40000000;
    private static readonly IntPtr PerMonitorAwareV2 = -4;

    /// <summary>
    /// 跑一遍自检。抓屏打不开抛 <see cref="CaptureLostException"/>；GDI 那条路拿不到画面抛 <see cref="InvalidOperationException"/>。
    /// 会把本进程设成「按显示器感知缩放比例」：GDI 那条路要按实际像素拷屏幕，不然系统给的是按缩放比例折算过的画面。
    /// </summary>
    public static CaptureCheckResult Run()
    {
        SetProcessDpiAwarenessContext(PerMonitorAwareV2);       // 已经设过（或者清单里写了）会失败，不用管
        IntPtr monitor = WgcCapture.PrimaryMonitor();
        var stats = new ScreenCaptureStats();
        var first = new byte[Proto.ViewW * 4 * Proto.ViewH];
        var second = new byte[first.Length];
        byte[] reference;
        int w, h;
        // 不带鼠标指针抓：GDI 拷下来的画面里没有指针
        using (var capture = new WgcCapture(monitor, cursor: false, stats))
        {
            if (!capture.TryRead(first, TimeSpan.FromSeconds(3))) throw new CaptureLostException("抓屏打开了，但 3 秒里系统一帧都没给");
            (byte[] full, w, h) = GrabWithGdi(monitor);
            reference = ReferenceScaler.Scale(full, w, h, w * 4);
            // 屏幕在这当中变过的话，再读一次拿到的是 GDI 那一拷之后的画面；没变过就还是第一幅
            if (!capture.TryRead(second, TimeSpan.FromMilliseconds(300))) first.CopyTo(second, 0);
        }
        if (stats.SourceWidth != w || stats.SourceHeight != h)
            throw new InvalidOperationException($"两条路看到的分辨率不一样：抓屏 {stats.SourceWidth}×{stats.SourceHeight}，GDI {w}×{h}");

        ViewFit fit = stats.Fit;
        var a = Compare(first, reference, fit);
        var b = Compare(second, reference, fit);
        byte[] got = b.Psnr > a.Psnr ? second : first;
        var best = b.Psnr > a.Psnr ? b : a;
        double wrong = new[]
        {
            Compare(got, Transform(reference, fit, (x, y) => (fit.W - 1 - x, y), swapRedBlue: false), fit).Psnr,
            Compare(got, Transform(reference, fit, (x, y) => (x, fit.H - 1 - y), swapRedBlue: false), fit).Psnr,
            Compare(got, Transform(reference, fit, (x, y) => (x, y), swapRedBlue: true), fit).Psnr,
            Compare(got, Transform(reference, fit, (x, y) => ((x + 8) % fit.W, y), swapRedBlue: false), fit).Psnr,
        }.Max();
        return new CaptureCheckResult(w, h, fit, best.Psnr, best.OffPixels, best.MaxDiff, BarsAreBlack(got, fit), wrong, stats);
    }

    /// <summary>用 GDI 把一台显示器的整屏原样拷下来（B G R A，从上到下，没有行间填充）。</summary>
    private static (byte[] Pixels, int W, int H) GrabWithGdi(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info)) throw new InvalidOperationException("取不到主显示器的位置");
        int w = info.Monitor.Right - info.Monitor.Left, h = info.Monitor.Bottom - info.Monitor.Top;
        IntPtr screen = GetDC(IntPtr.Zero), mem = IntPtr.Zero, bitmap = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            mem = CreateCompatibleDC(screen);
            var header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = w, Height = -h /* 负数 = 从上到下 */, Planes = 1, BitCount = 32,
            };
            bitmap = CreateDIBSection(screen, in header, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (screen == IntPtr.Zero || mem == IntPtr.Zero || bitmap == IntPtr.Zero) throw new InvalidOperationException("GDI 建不了拷屏用的位图");
            old = SelectObject(mem, bitmap);
            if (!BitBlt(mem, 0, 0, w, h, screen, info.Monitor.Left, info.Monitor.Top, SrcCopy | CaptureBlt))
                throw new InvalidOperationException("GDI 拷屏失败（这个程序不在登录用户的桌面会话里的时候就是这样）");
            GdiFlush();         // 直接读位图的内存之前，要等 GDI 把排着队的绘制做完（CreateDIBSection 的文档要求）
            var pixels = new byte[w * 4 * h];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return (pixels, w, h);
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>只比画面占的那一块、只比颜色的三个通道。</summary>
    internal static (double Psnr, double OffPixels, int MaxDiff) Compare(byte[] got, byte[] want, ViewFit fit)
    {
        double squares = 0;
        long off = 0;
        int max = 0;
        for (int y = fit.Y; y < fit.Y + fit.H; y++)
            for (int x = fit.X; x < fit.X + fit.W; x++)
            {
                int o = (y * Proto.ViewW + x) * 4, worst = 0;
                for (int c = 0; c < 3; c++)
                {
                    int d = Math.Abs(got[o + c] - want[o + c]);
                    squares += d * d;
                    worst = Math.Max(worst, d);
                }
                if (worst >= 2) off++;
                max = Math.Max(max, worst);
            }
        long pixels = (long)fit.W * fit.H;
        double mse = squares / (pixels * 3);
        return (mse == 0 ? 99 : Math.Min(99, 10 * Math.Log10(255.0 * 255.0 / mse)), 100.0 * off / pixels, max);
    }

    /// <summary>把对照画面弄错：画面那一块里的像素 (x, y) 换成 map(x, y) 处的，可以再把红蓝对调。</summary>
    internal static byte[] Transform(byte[] view, ViewFit fit, Func<int, int, (int X, int Y)> map, bool swapRedBlue)
    {
        var result = (byte[])view.Clone();
        for (int y = 0; y < fit.H; y++)
            for (int x = 0; x < fit.W; x++)
            {
                (int sx, int sy) = map(x, y);
                int from = ((fit.Y + sy) * Proto.ViewW + fit.X + sx) * 4, to = ((fit.Y + y) * Proto.ViewW + fit.X + x) * 4;
                result[to] = view[from + (swapRedBlue ? 2 : 0)];
                result[to + 1] = view[from + 1];
                result[to + 2] = view[from + (swapRedBlue ? 0 : 2)];
            }
        return result;
    }

    internal static bool BarsAreBlack(byte[] view, ViewFit fit)
    {
        var px = MemoryMarshal.Cast<byte, uint>(view);
        for (int y = 0; y < Proto.ViewH; y++)
            for (int x = 0; x < Proto.ViewW; x++)
            {
                bool inside = x >= fit.X && x < fit.X + fit.W && y >= fit.Y && y < fit.Y + fit.H;
                if (!inside && px[y * Proto.ViewW + x] != 0xFF000000u) return false;
            }
        return true;
    }
}
