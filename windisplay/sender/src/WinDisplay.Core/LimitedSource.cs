using System.Diagnostics;

namespace WinDisplay.Core;

/// <summary>
/// 给一个画面来源加上限：给够 maxFrames 幅画面，或者从建这个对象算起过了 seconds 秒，就报 End（会话正常收尾）。0（或负数）= 这一项不限。
/// 快到时限的时候把等新画面的时限截短，不会等过了头。
/// </summary>
public sealed class LimitedSource(IFrameSource inner, long maxFrames, double seconds) : IFrameSource
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _given;

    public FrameResult TryGetFrame(Span<byte> dst, TimeSpan timeout)
    {
        if (maxFrames > 0 && _given >= maxFrames) return FrameResult.End;
        if (seconds > 0)
        {
            double left = seconds - _clock.Elapsed.TotalSeconds;
            if (left <= 0) return FrameResult.End;
            if (timeout.TotalSeconds > left) timeout = TimeSpan.FromSeconds(left);      // 别等过了头
        }
        FrameResult r = inner.TryGetFrame(dst, timeout);
        if (r == FrameResult.Frame) _given++;
        return r;
    }
}
