using System.Diagnostics;
using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>
/// 「只留最新一个」的交接位：抓屏用它把系统回调线程上来的帧交给取画面的线程。
/// 这里的「帧」是个只记编号和被释放了几次的假对象；真的帧占着帧池里的缓冲，漏一个不释放、或者释放两次都是事故。
/// </summary>
public class LatestSlotTests
{
    private sealed class Frame(int n) : IDisposable
    {
        public readonly int N = n;
        public int Disposed;
        public bool ThrowOnDispose;

        public void Dispose()
        {
            Interlocked.Increment(ref Disposed);
            if (ThrowOnDispose) throw new InvalidOperationException("还帧的时候出错");
        }
    }

    [Fact]
    public void 没人取的旧帧被新帧顶掉并释放_取走的归调用方()
    {
        using var slot = new LatestSlot<Frame>();
        Frame a = new(1), b = new(2), c = new(3);
        slot.Offer(() => a);
        slot.Offer(() => b);
        Assert.Equal((1, 0), (a.Disposed, b.Disposed));

        Assert.Same(b, slot.Take(TimeSpan.Zero));
        Assert.Null(slot.Take(TimeSpan.Zero));              // 取走了就没有了
        slot.Offer(() => c);
        Assert.Equal(0, b.Disposed);                         // 已经取走的那一帧归调用方，不会被后来的顶掉
        Assert.Same(c, slot.Take(TimeSpan.Zero));
        Assert.Equal((1, 0, 0), (a.Disposed, b.Disposed, c.Disposed));
    }

    [Fact]
    public void 回调来了但帧池里没有帧_什么都不变()
    {
        using var slot = new LatestSlot<Frame>();
        var a = new Frame(1);
        slot.Offer(() => a);
        slot.Offer(() => null);
        Assert.Equal(0, a.Disposed);
        Assert.Same(a, slot.Take(TimeSpan.Zero));
    }

    [Fact]
    public void 没有帧时等到时限_时限是零就立刻返回()
    {
        using var slot = new LatestSlot<Frame>();
        var sw = Stopwatch.StartNew();
        Assert.Null(slot.Take(TimeSpan.Zero));
        Assert.InRange(sw.ElapsedMilliseconds, 0, 300);
        sw.Restart();
        Assert.Null(slot.Take(TimeSpan.FromMilliseconds(150)));
        Assert.InRange(sw.ElapsedMilliseconds, 130, 1500);
    }

    [Fact]
    public void 正在等的时候来了一帧_马上拿到()
    {
        using var slot = new LatestSlot<Frame>();
        var a = new Frame(1);
        var producer = new Thread(() =>
        {
            Thread.Sleep(100);
            slot.Offer(() => a);
        });
        producer.Start();
        var sw = Stopwatch.StartNew();
        Assert.Same(a, slot.Take(TimeSpan.FromSeconds(10)));
        Assert.InRange(sw.ElapsedMilliseconds, 50, 3000);       // 没有等满 10 秒
        producer.Join();
    }

    [Fact]
    public void 回调里取帧抛异常_下一次取的时候当成抓屏断了抛出来_之后一直是这样()
    {
        using var slot = new LatestSlot<Frame>();
        var a = new Frame(1);
        slot.Offer(() => a);
        var boom = new InvalidOperationException("设备已移除\r\n\r\n设备已移除");
        slot.Offer(() => throw boom);

        var e = Assert.Throws<CaptureLostException>(() => slot.Take(TimeSpan.Zero));     // 手里还有一帧没取走，也照样报
        Assert.Same(boom, e.InnerException);
        Assert.Equal("系统回调里出错：设备已移除", e.Message);                             // 系统的报错常常一句话重复两遍，只留第一行

        slot.Offer(() => new Frame(2));
        var again = Assert.Throws<CaptureLostException>(() => slot.Take(TimeSpan.FromMilliseconds(50)));
        Assert.Same(boom, again.InnerException);                                         // 记的是第一个原因
    }

    [Fact]
    public void 正在等的时候出了事_马上醒来报错_不等满时限()
    {
        using var slot = new LatestSlot<Frame>();
        var failer = new Thread(() =>
        {
            Thread.Sleep(100);
            slot.Fail("被抓的显示器没了");
        });
        failer.Start();
        var sw = Stopwatch.StartNew();
        var e = Assert.Throws<CaptureLostException>(() => slot.Take(TimeSpan.FromSeconds(10)));
        Assert.Equal("被抓的显示器没了", e.Message);
        Assert.InRange(sw.ElapsedMilliseconds, 50, 3000);
        failer.Join();
    }

    [Fact]
    public void 顶掉旧帧时还帧出错_新帧不漏_记成失败()
    {
        var slot = new LatestSlot<Frame>();
        Frame a = new(1) { ThrowOnDispose = true }, b = new(2);
        slot.Offer(() => a);
        slot.Offer(() => b);                                // 释放 a 抛了异常：不能从回调里漏出去，也不能把 b 弄丢
        Assert.Throws<CaptureLostException>(() => slot.Take(TimeSpan.Zero));
        slot.Dispose();
        Assert.Equal((1, 1), (a.Disposed, b.Disposed));     // b 留在了交接位里，关的时候释放
    }

    [Fact]
    public void 关掉_没取走的释放一次_之后回调不再取帧()
    {
        var slot = new LatestSlot<Frame>();
        Frame a = new(1), b = new(2);
        slot.Offer(() => a);
        slot.Dispose();
        slot.Dispose();
        Assert.Equal(1, a.Disposed);

        int produced = 0;
        slot.Offer(() =>
        {
            produced++;
            return b;
        });
        Assert.Equal(0, produced);                          // 关了之后不能再去碰帧池
        Assert.Equal(0, b.Disposed);
        slot.Fail("晚到的失败");                              // 关了之后也不记
        Assert.Throws<ObjectDisposedException>(() => slot.Take(TimeSpan.Zero));
    }

    [Fact]
    public void 关的时候回调正取到一半_等它取完才返回_取到的那一帧也释放()
    {
        var slot = new LatestSlot<Frame>();
        var a = new Frame(1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var producer = new Thread(() => slot.Offer(() =>
        {
            entered.Set();
            release.Wait();
            return a;
        }));
        producer.Start();
        Assert.True(entered.Wait(5000));

        var closer = new Thread(slot.Dispose);
        closer.Start();
        Assert.False(closer.Join(200));                     // 回调还在帧池里：这时候关不能返回，不然调用方接着就把帧池拆了
        release.Set();
        Assert.True(closer.Join(5000));
        producer.Join();
        Assert.Equal(1, a.Disposed);
    }

    [Fact]
    public void 回调线程猛塞_另一个线程取_每一帧恰好释放一次_取到的越来越新_最后一帧一定拿得到()
    {
        const int Total = 20000;
        using var slot = new LatestSlot<Frame>();
        var frames = new Frame[Total];
        for (int i = 0; i < Total; i++) frames[i] = new Frame(i);
        var producer = new Thread(() =>
        {
            for (int i = 0; i < Total; i++)
            {
                Frame f = frames[i];
                slot.Offer(() => f);
                if (i % 500 == 0) Thread.Yield();
            }
        });
        var taken = new List<int>();
        producer.Start();
        var sw = Stopwatch.StartNew();
        while (taken.Count == 0 || taken[^1] != Total - 1)
        {
            Assert.True(sw.ElapsedMilliseconds < 20000, "最后一帧一直没拿到");
            if (slot.Take(TimeSpan.FromMilliseconds(20)) is { } f)
            {
                taken.Add(f.N);
                f.Dispose();
            }
        }
        producer.Join();

        Assert.Null(slot.Take(TimeSpan.Zero));
        for (int i = 1; i < taken.Count; i++) Assert.True(taken[i] > taken[i - 1], $"取到的帧倒回去了：{taken[i - 1]} 之后是 {taken[i]}");
        Assert.All(frames, f => Assert.Equal(1, f.Disposed));       // 被顶掉的由交接位释放，取走的由这里释放：都是恰好一次
    }
}
