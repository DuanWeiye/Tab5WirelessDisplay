using System.Globalization;
#if WINDOWS
using WinDisplay.Win;
#endif

namespace WinDisplay.Cli;

/// <summary>wdsend --scene：全屏的测试场景（代替「开一个游戏」去量投屏，见 WinDisplay.Win 的 Scene）。只有 Windows 版有。</summary>
internal static class SceneTool
{
    /// <summary>args：--scene 后面的那些参数。返回退出码：0 = 跑满了时间（--check-idle：没人在用）；1 = 参数不对 / 开不起来；4 = 电脑上有人在用，没开；5 = 中途被人关掉了。</summary>
    public static int Run(string[] args)
    {
#if WINDOWS
        double seconds = 30, detail = 0.05, idle = 60;
        int gpu = 0, cpu = 0;
        bool vsync = true, checkIdle = false;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new FormatException($"{a} 后面缺一个值");
                double Real() => Next() is var s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) && v >= 0
                    ? v : throw new FormatException($"{a} 的值 {s} 不是一个不小于 0 的数");
                int Int() => Next() is var s && int.TryParse(s, out int v) && v >= 0 ? v : throw new FormatException($"{a} 的值 {s} 不是一个不小于 0 的整数");
                switch (a)
                {
                    case "--seconds": seconds = Real(); break;
                    case "--gpu": gpu = Int(); break;
                    case "--cpu": cpu = Int(); break;
                    case "--detail": detail = Real(); break;
                    case "--idle": idle = Real(); break;
                    case "--check-idle": checkIdle = true; break;
                    case "--vsync":
                        vsync = Next() switch
                        {
                            "1" => true,
                            "0" => false,
                            var s => throw new FormatException($"--vsync 只能是 1 / 0，不是 {s}"),
                        };
                        break;
                    default: throw new FormatException($"--scene 不认识的选项：{a}");
                }
            }
            if (seconds is <= 0 or > 3600) throw new FormatException("--seconds 要大于 0、不超过 3600");
            if (detail > 1) throw new FormatException("--detail 要在 0~1");
            if (cpu > 64) throw new FormatException("--cpu 不超过 64");
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        if (checkIdle)
        {
            // 只查这台电脑有没有人在用，不开场景。给一键脚本用：它要先停日常的投屏、换 Tab5 的页面，有人在用的话这些都不该动
            double idleNow = Scene.IdleSeconds();
            bool free = idle <= 0 || idleNow >= idle;
            Console.WriteLine(double.IsInfinity(idleNow) ? "查不到最近一次键盘 / 鼠标输入的时间，当作没人在用"
                : $"这台电脑 {idleNow:F0} 秒前有人动过键盘 / 鼠标（要空闲满 {idle:F0} 秒）：{(free ? "没人在用" : "有人在用")}");
            return free ? 0 : 4;
        }

        Console.WriteLine($"场景：{seconds:G} 秒，显卡负载 {gpu}，CPU 负载 {cpu} 个线程，{(vsync ? "等垂直同步" : "不等垂直同步")}，细节 {detail:G}");
        SceneResult r;
        try
        {
            r = Scene.Run(new SceneOptions(seconds, gpu, cpu, vsync, detail, idle));
        }
        catch (Exception e)         // 显卡的错 Scene 自己接住了；这里是别的（建窗口、线程……），窗口它在 finally 里已经收掉
        {
            Console.Error.WriteLine($"场景出错：{e.GetType().Name}：{e.Message}");
            return 1;
        }
        if (!r.Started)
        {
            Console.Error.WriteLine($"场景没开：{r.Why}");
            return r.Why?.Contains("有人在用") == true ? 4 : 1;
        }
        IReadOnlyList<double> t = r.PresentMs;
        if (t.Count < 2)
        {
            Console.WriteLine($"场景 {r.Width}×{r.Height}：只呈现了 {t.Count} 帧{(r.Why is null ? "" : "；" + r.Why)}");
            return 1;
        }
        double[] gaps = new double[t.Count - 1];
        for (int i = 1; i < t.Count; i++) gaps[i - 1] = t[i] - t[i - 1];
        double[] sorted = [.. gaps.Order()];
        double median = sorted[sorted.Length / 2];
        Console.WriteLine($"场景 {r.Width}×{r.Height}：@{t[0]:F1} 到 @{t[^1]:F1}，呈现了 {t.Count} 帧，{(t.Count - 1) / ((t[^1] - t[0]) / 1000):F1} 帧/秒；" +
                          $"相邻两帧：中位 {median:F2} / 99% {sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))]:F2} / 最大 {sorted[^1]:F2} ms");
        // 场景自己卡的地方（相邻两帧隔了中位数的 2.5 倍以上，而且至少 25 毫秒）：投屏那边同一时刻的停顿不能算在投屏头上
        int hitches = 0;
        for (int i = 0; i < gaps.Length; i++)
        {
            if (gaps[i] < Math.Max(25, median * 2.5)) continue;
            if (++hitches <= 200) Console.WriteLine($"场景卡 @{t[i]:F1}：{gaps[i]:F1} ms");
        }
        Console.WriteLine($"场景自己卡了 {hitches} 次");
        if (r.Why is not null) Console.WriteLine(r.Why);
        if (r.Dismissed) Console.WriteLine("中途被人关掉了（按了键 / 点了鼠标）");
        return r.Dismissed ? 5 : r.Why is null ? 0 : 1;
#else
        Console.Error.WriteLine("这一份 wdsend 不带测试场景：--scene 要用 Windows 版（bin 下 net10.0-windows… 目录里的那份）");
        return 1;
#endif
    }
}
