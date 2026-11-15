using System.Diagnostics;

namespace WinDisplay.Core;

/// <summary>
/// 一次打开的抓屏（平台专有的那一层实现它，比如 Windows 的 WGC）。给出的画面已经缩放成 1280×720、每像素 4 字节。
/// 只在一个线程上用（会话取画面的那个线程）。
/// </summary>
public interface ICaptureBackend : IDisposable
{
    /// <summary>
    /// 画面自上次读走之后变过：写进 dst，返回 true。没变过：最多等 timeout，等不到返回 false（dst 不动）。
    /// 刚打开时的第一次一定有画面。抓屏断了抛 <see cref="CaptureLostException"/>，**抛之前不能动 dst**。
    /// </summary>
    bool TryRead(Span<byte> dst, TimeSpan timeout);
}

/// <summary>
/// 抓屏打不开，或者开着的时候断了：显示器拔了、换了分辨率或排列、显卡驱动重置、程序不在桌面会话里……
/// 这些都可能过一会儿自己好，所以 <see cref="CaptureSource"/> 遇到它不结束会话，隔一会儿重开。
/// </summary>
public sealed class CaptureLostException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 把抓屏接成会话的画面来源，管「断了重开」：抓屏断了之后隔 retryDelay 重开一次，一直试到开成；
/// 这期间报 NoChange，会话照常连着，Tab5 上停在最后一幅画面。
/// 头一次打开在构造时做，打不开直接把异常抛给调用方（程序一起来就抓不了屏，多半是环境不对，重试没有用）。
/// 时限：等新画面、等重开的时刻都算在 TryGetFrame 的时限里；只有真去重开的那一次，打开抓屏本身花的时间（建显卡设备、
/// 开抓屏会话，一两百毫秒）不算，那一次可能超出时限这么多——每隔 retryDelay 才有一次，会话的定时差这一点不要紧。
/// 不是线程安全的：TryGetFrame 只在会话的线程上调；Dispose 要等会话的 Run 返回之后。
/// </summary>
public sealed class CaptureSource : IFrameSource, IDisposable
{
    private readonly Func<ICaptureBackend> _open;
    private readonly TimeSpan _retryDelay;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private ICaptureBackend? _backend;
    private TimeSpan _retryAt;
    private string? _reported;          // 这一次断开期间最近报出去的原因；重开成了清掉
    private bool _disposed;

    /// <param name="open">打开抓屏；打不开抛 <see cref="CaptureLostException"/>。</param>
    /// <param name="retryDelay">断了之后隔多久重开一次，默认 1 秒。</param>
    public CaptureSource(Func<ICaptureBackend> open, TimeSpan? retryDelay = null)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
        if (_retryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryDelay), retryDelay, "重开的间隔不能是负数");
        _backend = open();
    }

    /// <summary>
    /// 抓屏断了，或者断了之后重开没成（参数是原因）。断的那一次一定报；重开没成的，原因和最近报的那个不一样才报——
    /// 所以一直是同一个原因的时候只报一次，换了原因（比如先是「显示器没了」，后来变成「建不了显卡设备」）会再报。
    /// 在调 TryGetFrame 的线程上回调。
    /// </summary>
    public event Action<string>? Lost;
    /// <summary>断了之后重开成了。在调 TryGetFrame 的线程上回调。</summary>
    public event Action? Restored;

    public FrameResult TryGetFrame(Span<byte> dst, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_backend is null)
        {
            TimeSpan wait = _retryAt - _clock.Elapsed;
            if (wait > timeout)         // 还没到重开的时候：照样守时限，不让会话空转
            {
                if (timeout > TimeSpan.Zero) Thread.Sleep(timeout);
                return FrameResult.NoChange;
            }
            if (wait > TimeSpan.Zero)
            {
                Thread.Sleep(wait);
                timeout -= wait;
            }
            try
            {
                _backend = _open();
            }
            catch (CaptureLostException e)
            {
                _retryAt = _clock.Elapsed + _retryDelay;
                Report(e.Message, always: false);
                return FrameResult.NoChange;
            }
            _reported = null;
            Restored?.Invoke();
        }
        try
        {
            return _backend.TryRead(dst, timeout) ? FrameResult.Frame : FrameResult.NoChange;
        }
        catch (CaptureLostException e)
        {
            ICaptureBackend broken = _backend;
            _backend = null;
            _retryAt = _clock.Elapsed + _retryDelay;
            try
            {
                broken.Dispose();
            }
            catch (Exception)
            {
                // 已经断了的抓屏，关的时候再出错不稀奇（显卡设备都没了）。不往外抛：抛出去会话就结束了，而这里要的正是「断了重开、会话不断」
            }
            Report(e.Message, always: true);
            return FrameResult.NoChange;
        }
    }

    private void Report(string reason, bool always)
    {
        if (!always && reason == _reported) return;
        _reported = reason;
        Lost?.Invoke(reason);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _backend?.Dispose();
        _backend = null;
    }
}
