using System.Diagnostics;
using WinDisplay.Core;
#if WINDOWS
using WinDisplay.Win;
#endif

namespace WinDisplay.Cli;

/// <summary>wdsend 里和抓屏有关的部分（--screen、--screen-check）。只有 Windows 版带抓屏；别的版本里这几个选项报「没有」。</summary>
internal static class ScreenTool
{
    /// <summary>逐帧记录里用的时刻：开机以来的毫秒。几个进程打出来的时刻是同一个钟，可以放在一起对。</summary>
    public static double NowMs() => Stopwatch.GetElapsedTime(0).TotalMilliseconds;

#if WINDOWS
    private static readonly ScreenCaptureStats Stats = new();

    /// <summary>打开主显示器的抓屏。打不开：打印原因，返回 null。</summary>
    /// <param name="verbose">每读走一幅屏幕打印一行。</param>
    public static CaptureSource? Open(bool verbose)
    {
        try
        {
            if (verbose)
            {
                long arrived = 0;       // 上一行打印时系统一共给了多少幅
                Stats.Read = c =>
                {
                    Console.WriteLine($"抓 @{NowMs():F1}：缩放读回 {c.ScaleMs:F1} ms，取到时这一幅已经出来 {c.AgeMs:F1} ms，离上一幅的合成 {c.ComposeGapMs:F1} ms，" +
                                      $"中间没读的 {Math.Max(0, c.Arrived - arrived - 1)} 幅");
                    arrived = c.Arrived;
                };
            }
            CaptureSource source = ScreenCapture.OpenPrimary(Stats);
            source.Lost += why => Console.WriteLine($"抓屏断了：{why}；每秒重开一次");
            source.Restored += () => Console.WriteLine("抓屏重开成了");
            return source;
        }
        catch (CaptureLostException e)
        {
            Console.Error.WriteLine(e.Message);
            return null;
        }
    }

    /// <summary>主显示器的画面现在在横屏画面里占哪一块（还没抓到过画面时宽高是 0）。哪个线程都能调。</summary>
    public static ViewFit Fit() => Stats.Fit;

    public static void PrintStats(TimeSpan elapsed)
    {
        if (Stats.Frames == 0) return;
        ViewFit f = Stats.Fit;
        Console.WriteLine($"屏幕 {Stats.SourceWidth}×{Stats.SourceHeight} → 画面里 {f.W}×{f.H}（左上角 {f.X},{f.Y}）；黄框{(Stats.Borderless ? "去掉了" : "去不掉")}");
        Console.WriteLine($"抓到 {Stats.Frames} 幅，缩放 + 读回：平均 {Stats.TotalMs / Stats.Frames:F2} / 最大 {Stats.MaxMs:F2} ms");
        double cpu = Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;
        Console.WriteLine($"本进程用的 CPU：{cpu:F2} 秒（会话 {elapsed.TotalSeconds:F1} 秒，折合 {100 * cpu / Math.Max(elapsed.TotalSeconds, 0.001):F0}% 个核）");
    }

    /// <summary>抓屏自检：打印结果，返回退出码（0 = 对，1 = 不对或者跑不了）。</summary>
    public static int Check()
    {
        CaptureCheckResult c;
        try
        {
            c = CaptureCheck.Run();
        }
        catch (Exception e) when (e is CaptureLostException or InvalidOperationException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        ViewFit f = c.Fit;
        Console.WriteLine($"屏幕 {c.SourceWidth}×{c.SourceHeight} → 画面里 {f.W}×{f.H}（左上角 {f.X},{f.Y}）；黄框{(c.Stats.Borderless ? "去掉了" : "去不掉")}");
        Console.WriteLine($"缩放 + 读回：平均 {c.Stats.TotalMs / Math.Max(1, c.Stats.Frames):F2} ms（{c.Stats.Frames} 幅，头一幅含建资源的时间）");
        Console.WriteLine($"和对照（GDI 拷屏 + CPU 缩放）比：{c.Psnr:F1} dB，{c.OffPixels:F2}% 的像素差了 2 以上，最多差 {c.MaxDiff}；黑边{(c.BarsBlack ? "全黑" : "不是全黑")}");
        Console.WriteLine($"和故意弄错的对照（左右翻转 / 上下翻转 / 红蓝对调 / 错开 8 像素）比，最接近的：{c.WrongPsnr:F1} dB");
        Console.WriteLine($"门槛：黑边全黑，至少 {CaptureCheckResult.MinPsnr:G} dB，差 2 以上的像素不超过 {CaptureCheckResult.MaxOffPixels:G}%，" +
                          $"比故意弄错的至少高 {CaptureCheckResult.MinMargin:G} dB");
        Console.WriteLine(c.Ok ? "抓屏自检：符合" : "抓屏自检：不符（屏幕上有东西在动的话，等它静下来重跑一次）");
        return c.Ok ? 0 : 1;
    }
#else
    private const string NotHere = "这一份 wdsend 不带抓屏：--screen 和 --screen-check 要用 Windows 版（bin 下 net10.0-windows… 目录里的那份）";

    public static CaptureSource? Open(bool verbose)
    {
        Console.Error.WriteLine(NotHere);
        return null;
    }

    public static void PrintStats(TimeSpan elapsed) { }

    public static int Check()
    {
        Console.Error.WriteLine(NotHere);
        return 1;
    }
#endif
}
