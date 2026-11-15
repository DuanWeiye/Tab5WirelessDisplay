using WinDisplay.Core;
#if WINDOWS
using WinDisplay.Win;
#endif

namespace WinDisplay.Cli;

/// <summary>wdsend 里和注入输入有关的部分（--input、--inject-check）。只有 Windows 版带；别的版本里这两个选项报「没有」。</summary>
internal static class InputTool
{
#if WINDOWS
    /// <summary>屏幕坐标改成按物理像素算（注入触摸要用）。要在打开抓屏、建任何窗口之前调。</summary>
    public static void Prepare() => InputInjector.UsePhysicalPixels();

    /// <summary>
    /// 把注入了什么打印出来（-v）。按着不动的触点每 0.1 秒重报一次，也会打出来。
    /// 先注入、后打印，打印不出去（输出接的管道被对面关了）也不算注入出错：InputBridge 已经把这次注入记成做过了，
    /// 这里要是没注入就抛出去，系统里的键和它记的就对不上，连接结束时该松的键松不掉。
    /// </summary>
    private sealed class Echo(IInputSink inner) : IInputSink
    {
        public void Keys(IReadOnlyList<KeyAction> actions)
        {
            inner.Keys(actions);
            Say("  → 注入按键 " + string.Join("，", actions.Select(a => $"{(a.Extended ? "E0 " : "")}{a.ScanCode:X2} {(a.Down ? "按下" : "松开")}")));
        }

        public bool Touch(IReadOnlyList<Contact> frame)
        {
            bool ok = inner.Touch(frame);
            Say("  → 注入触摸 " + string.Join(' ', frame.Select(c => $"#{c.Id}({c.X},{c.Y}){c.Phase switch { ContactPhase.Down => "按下", ContactPhase.Up => "抬起", ContactPhase.Cancel => "作废", _ => "" }}"))
                + (ok ? "" : "：系统没收下"));
            return ok;
        }

        public void Mouse(IReadOnlyList<MouseAction> actions)
        {
            inner.Mouse(actions);
            Say("  → 注入鼠标 " + string.Join("，", actions.Select(a => a.Kind switch
            {
                MouseKind.Move => $"挪 ({a.X},{a.Y})",
                MouseKind.LeftDown => "左键按下",
                MouseKind.LeftUp => "左键松开",
                MouseKind.RightDown => "右键按下",
                MouseKind.RightUp => "右键松开",
                MouseKind.Wheel => $"滚轮 {a.X}",
                _ => $"横向滚轮 {a.X}",
            })));
        }

        private static void Say(string line)
        {
            try
            {
                Console.WriteLine(line);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// 把会话收到的触摸和按键注入这台电脑，这台电脑的键盘指示灯状态经会话发回 Tab5。开不了：打印原因，返回 null。
    /// 返回的东西每次 session.Run 返回之后要调 ReleaseAll（连接结束时按着的键松开、按着的触点作废），最后 Dispose。
    /// 进程要是不走正常的路结束（关掉控制台窗口、没人接的异常），这里登记的几个钩子会先把按着的键松开。
    /// </summary>
    /// <param name="verbose">每次注入都打印一行。</param>
    public static InputPump? Attach(DisplaySession session, bool verbose)
    {
        InputInjector injector;
        try
        {
            injector = new InputInjector();
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine(e.Message);
            return null;
        }
        string? last = null;
        injector.Warning += why =>
        {
            // 前台是权限更高的窗口（UAC 提示、锁屏）时每一次注入都不成：同样的话只说一遍
            lock (injector)
            {
                if (why == last) return;
                last = why;
            }
            Console.WriteLine($"注入没成：{why}");
        };
        IInputSink sink = verbose ? new Echo(injector) : injector;
        var pump = new InputPump(new InputBridge(sink, ScreenTool.Fit, InputInjector.HostKeyRepeat, InputInjector.TouchKeepAliveMs),
                                 InputInjector.HostLeds);
        pump.LedsChanged += leds =>
        {
            if (verbose) Console.WriteLine($"  → 键盘指示灯 Num={leds & 1} Caps={leds >> 1 & 1} Scroll={leds >> 2 & 1}，发给 Tab5");
            session.SetKeyboardLeds(leds);
        };
        pump.Faulted += e => Console.Error.WriteLine($"注入输入的后台线程出错：{e.Message}");
        pump.Start();           // 上面两个事件挂好之后才起线程：开头那一次指示灯状态不会没人收
        session.Touch += points => pump.Touch(points);
        session.Key += report => pump.Key(report);
        session.Held += pump.Held;
        session.InputMode += pump.Mode;
        session.TouchpadGain += pump.TouchpadGain;
        // 进程结束时还按着的键要松开，不然系统里那个键一直是按下的，直到有人在真键盘上按它一下；按着的触点要作废，
        // 不然系统过一秒左右把它收走时，程序收到的是一次抬起，等于在那里点了一下。
        // 正常结束、Ctrl-C 走的是 Program 里的 finally / using；这里管不走那条路的几种（关掉控制台窗口、没人接的异常）。
        // Close = 松开，而且之后再来的输入都不算：处理函数返回到进程真的结束还有一小会儿，会话可能还在收 Tab5 的按键
        ExitHooks.Register(pump.Close);
        return pump;
    }

    /// <summary>注入自检：逐项打印，返回退出码（0 = 全部符合，1 = 有不符的或者跑不了）。</summary>
    public static int Check()
    {
        try
        {
            bool ok = InjectCheck.Run(Console.WriteLine);
            Console.WriteLine(ok ? "注入自检：符合" : "注入自检：不符");
            return ok ? 0 : 1;
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }
#else
    private const string NotHere = "这一份 wdsend 不带输入注入：--input 和 --inject-check 要用 Windows 版（bin 下 net10.0-windows… 目录里的那份）";

    public static void Prepare() { }

    public static InputPump? Attach(DisplaySession session, bool verbose)
    {
        Console.Error.WriteLine(NotHere);
        return null;
    }

    public static int Check()
    {
        Console.Error.WriteLine(NotHere);
        return 1;
    }
#endif
}
