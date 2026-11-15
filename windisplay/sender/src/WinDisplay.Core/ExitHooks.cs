using System.Runtime.InteropServices;

namespace WinDisplay.Core;

/// <summary>
/// 进程不走正常的路结束时也要做的收尾。注入进系统、还按着的键不会因为进程没了就松开（要到有人在真键盘上按它一下），
/// 所以往系统里注入输入的程序都要登记一下：关掉控制台窗口、没人接的异常、进程退出，这几条路上都先调一次 release。
/// 被直接杀掉（taskkill /f）的谁也管不了。注销 / 关机这里不管：整个桌面会话都没了，按着的键跟着没了
/// （而且用了 user32 的控制台程序，系统不把这两个事件交给处理函数；有窗口的程序想体面收尾，自己接 SystemEvents.SessionEnded）。
/// </summary>
public static class ExitHooks
{
    private static readonly List<PosixSignalRegistration> Signals = [];     // 留着引用：被回收了登记就没了

    /// <param name="release">要做的收尾，比如 <see cref="InputPump.Close"/>。哪个线程上都可能调、可能调好几次；抛的异常这里吞掉（进程正在结束，没有地方可报）。</param>
    public static void Register(Action release)
    {
        ArgumentNullException.ThrowIfNull(release);
        void Run()
        {
            try
            {
                release();
            }
            catch (Exception)
            {
            }
        }
        // 关掉控制台窗口（SIGHUP）：处理函数返回之后系统就结束进程，不等主线程，所以在处理函数里做
        lock (Signals) Signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => Run()));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Run();
        // 没人接的异常：运行时不保证跑 finally，这里先做，异常照常报出去、进程照常结束
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Run();
    }
}
