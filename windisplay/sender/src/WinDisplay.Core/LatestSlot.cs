using System.Diagnostics;

namespace WinDisplay.Core;

/// <summary>
/// 「只留最新一个」的交接位，抓屏用它把系统回调线程上来的帧交给取画面的线程：
/// 生产者（系统的回调线程）每来一个就放进来，旧的没人取就被顶掉并释放；消费者来取，没有就最多等一会儿。
/// 生产者那边出的错不能丢——系统的回调里抛异常没有人知道，画面就停在旧的一幅上看不出出了事——
/// 所以记在这里，消费者下一次来取的时候当成「抓屏断了」抛给它。出过错之后一直是这个状态，调用方该关掉重开。
/// </summary>
internal sealed class LatestSlot<T> : IDisposable where T : class, IDisposable
{
    private readonly object _lock = new();
    private T? _item;
    private string? _failure;
    private Exception? _cause;
    private bool _closed;

    /// <summary>
    /// 生产者线程调：produce 取出新的一个（没有就返回 null），留下它、顶掉旧的。produce 抛的异常记成失败。
    /// produce 是拿着锁调的，而且关了之后不再调：<see cref="Dispose"/> 一返回，就既没有正在跑的 produce、以后也不会再有，
    /// 调用方可以放心拆掉 produce 用到的东西（帧池）。
    /// </summary>
    public void Offer(Func<T?> produce)
    {
        lock (_lock)
        {
            if (_closed) return;
            try
            {
                T? item = produce();
                if (item is null) return;
                T? old = _item;
                _item = item;           // 先留下新的再释放旧的：释放旧的抛了异常，新的也不会漏掉没人管
                Monitor.PulseAll(_lock);
                old?.Dispose();
            }
            catch (Exception e)
            {
                _failure ??= $"系统回调里出错：{e.Message.Split('\n', 2)[0].Trim()}";
                _cause ??= e;
                Monitor.PulseAll(_lock);
            }
        }
    }

    /// <summary>记一个失败（比如被抓的显示器没了），并叫醒正在等的消费者。只记第一个；关了之后不记。哪个线程调都行。</summary>
    public void Fail(string reason, Exception? cause = null)
    {
        lock (_lock)
        {
            if (_closed) return;
            _failure ??= reason;
            _cause ??= cause;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>
    /// 消费者线程调：取走最新的那个（归调用方释放）；没有就最多等 timeout，等不到返回 null。timeout 可以是 0。
    /// 记着失败的时候抛 <see cref="CaptureLostException"/>（哪怕手里还有一个没取走的）。
    /// </summary>
    public T? Take(TimeSpan timeout)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        lock (_lock)
        {
            while (true)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (_failure is not null) throw new CaptureLostException(_failure, _cause);
                if (_item is { } item)
                {
                    _item = null;
                    return item;
                }
                TimeSpan left = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
                if (left <= TimeSpan.Zero) return null;
                // 等到时限、或者被叫醒，都回到上面重新看一遍：到时限的那一刻正好来了一个的话不能当成没有
                Monitor.Wait(_lock, left < MaxWait ? left : MaxWait);
            }
        }
    }

    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);      // Monitor.Wait 的时限有上限（约 24 天），取个小得多的，反正醒了会接着等

    /// <summary>关掉：释放没取走的那个；之后 <see cref="Offer"/> 不再调 produce、什么都不留。和 Take 在同一个线程上调。</summary>
    public void Dispose()
    {
        T? old;
        lock (_lock)
        {
            if (_closed) return;
            _closed = true;
            old = _item;
            _item = null;
            Monitor.PulseAll(_lock);
        }
        // 出了锁再释放：系统要是拿着它自己的锁回调我们（回调线程正卡在 Offer 的锁上），而还帧又要那把锁，在锁里释放就互相等住了。
        // 已经标了关闭，放到锁外不影响「返回之后不再有 produce」
        old?.Dispose();
    }
}
