using System.Runtime.InteropServices;

namespace WinDisplay.Core;

/// <summary>
/// 把这个进程的定时精度提到 1 毫秒（Windows；别的系统本来就够细，什么都不做）。
/// Windows 默认 15.6 毫秒一个节拍：Thread.Sleep(5)、Monitor.Wait(…, 5) 实际要等 15.6 毫秒（2026-11-15 在 Windows 11、.NET 10 上量的；
/// 申请之后超出不到 1.5 毫秒）。限帧率的两档画质一拍是 22.9 / 30.6 毫秒，按默认的节拍卡不准。
/// 用法：<c>using var _ = PreciseTimer.Raise();</c>，出了作用域还回去。可以嵌套、可以几个线程各申请各的（系统按进程计数）。
/// </summary>
public static partial class PreciseTimer
{
    public static IDisposable Raise() => OperatingSystem.IsWindows() && timeBeginPeriod(1) == 0 ? new Raised() : Nothing.Instance;

    private sealed class Raised : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) _ = timeEndPeriod(1);
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }

    [LibraryImport("winmm.dll")]
    private static partial uint timeBeginPeriod(uint milliseconds);

    [LibraryImport("winmm.dll")]
    private static partial uint timeEndPeriod(uint milliseconds);
}
