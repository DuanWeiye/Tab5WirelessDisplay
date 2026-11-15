using System.Diagnostics;
using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>给画面来源加帧数 / 时间上限（wdsend 的 --frames、--seconds）：到了上限报 End，会话正常收尾。</summary>
public class LimitedSourceTests
{
    /// <summary>一个假的画面来源：按剧本依次返回，剧本演完之后一直是 NoChange；等够交给它的时限才返回 NoChange。</summary>
    private sealed class Script(params FrameResult[] results) : IFrameSource
    {
        private int _next;
        public int Calls;
        public TimeSpan LastTimeout;

        public FrameResult TryGetFrame(Span<byte> dst, TimeSpan timeout)
        {
            Calls++;
            LastTimeout = timeout;
            FrameResult r = _next < results.Length ? results[_next++] : FrameResult.NoChange;
            // 往上取整到毫秒：Thread.Sleep(TimeSpan) 会把不足一毫秒的零头去掉，那样就没等够
            if (r == FrameResult.NoChange && timeout > TimeSpan.Zero) Thread.Sleep((int)Math.Ceiling(timeout.TotalMilliseconds));
            return r;
        }
    }

    private static readonly byte[] Dst = new byte[16];

    [Fact]
    public void 给够帧数就结束_没有新画面的那几次不算数()
    {
        var inner = new Script(FrameResult.Frame, FrameResult.NoChange, FrameResult.Frame, FrameResult.NoChange, FrameResult.Frame, FrameResult.Frame);
        var source = new LimitedSource(inner, maxFrames: 3, seconds: 0);
        FrameResult[] got = [.. Enumerable.Range(0, 7).Select(_ => source.TryGetFrame(Dst, TimeSpan.Zero))];
        Assert.Equal([FrameResult.Frame, FrameResult.NoChange, FrameResult.Frame, FrameResult.NoChange, FrameResult.Frame, FrameResult.End, FrameResult.End], got);
        Assert.Equal(5, inner.Calls);           // 到了上限就不再去问里面的来源：第 4 幅画面没被取走
    }

    [Fact]
    public void 到了时间就结束_快到的时候把等新画面的时限截短()
    {
        var inner = new Script(FrameResult.Frame);
        var source = new LimitedSource(inner, maxFrames: 0, seconds: 1.5);     // 给得宽一点：机器忙、线程被挂起几百毫秒的时候，第二次调用也还没到点
        Assert.Equal(FrameResult.Frame, source.TryGetFrame(Dst, TimeSpan.FromSeconds(5)));

        var sw = Stopwatch.StartNew();
        Assert.Equal(FrameResult.NoChange, source.TryGetFrame(Dst, TimeSpan.FromSeconds(30)));      // 会话说可以等 30 秒，但 1.5 秒就该结束了
        Assert.InRange(inner.LastTimeout.TotalMilliseconds, 1, 1500);
        Assert.InRange(sw.ElapsedMilliseconds, 0, 5000);
        Assert.Equal(FrameResult.End, source.TryGetFrame(Dst, TimeSpan.FromSeconds(30)));
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public void 离结束还早_时限原样交下去()
    {
        var inner = new Script();
        var source = new LimitedSource(inner, maxFrames: 0, seconds: 3600);
        Assert.Equal(FrameResult.NoChange, source.TryGetFrame(Dst, TimeSpan.FromMilliseconds(30)));
        Assert.Equal(TimeSpan.FromMilliseconds(30), inner.LastTimeout);
    }

    [Fact]
    public void 两项都是零_不限_里面的来源结束了才结束()
    {
        var results = Enumerable.Repeat(FrameResult.Frame, 500).Append(FrameResult.End).ToArray();
        var source = new LimitedSource(new Script(results), maxFrames: 0, seconds: 0);
        int frames = 0;
        FrameResult r;
        while ((r = source.TryGetFrame(Dst, TimeSpan.Zero)) == FrameResult.Frame) frames++;
        Assert.Equal((500, FrameResult.End), (frames, r));
    }
}
