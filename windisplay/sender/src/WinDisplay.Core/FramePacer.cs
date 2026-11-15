namespace WinDisplay.Core;

/// <summary>
/// 限帧率的节拍（纯计算，不碰时钟）：按 fps 定出每一帧最早什么时候可以给，两帧之间平均不短于一个周期。
/// <list type="bullet">
/// <item>取的一方准时来，或者晚了不到一个周期：下一帧还按原来的节拍算（偶尔晚一点的那一帧不会把后面整体拖慢）。</item>
/// <item>晚了一个周期以上（对面处理不过来；没有连接的那一阵根本没人取）：欠下的不补，从这一次重新起拍。
///   不然连接断了十几秒再接上，会按「能多快就多快」连着给出一百多帧，量出来的帧率就不是限的那个数了。</item>
/// </list>
/// 时间用毫秒，起点随调用方定（一般是一个 Stopwatch）。只在一个线程上用。
/// </summary>
public sealed class FramePacer
{
    private double _periodMs;
    private double _dueMs;          // 下一帧最早什么时候可以给

    public FramePacer(double fps) => SetFps(fps);

    /// <summary>换一个帧率：从下一次 <see cref="Given"/> 起按新的周期排，已经定下的那一拍不动（节拍不从头起）。</summary>
    public void SetFps(double fps)
    {
        if (!(fps > 0) || double.IsInfinity(fps)) throw new ArgumentOutOfRangeException(nameof(fps), fps, "帧率要大于 0");
        _periodMs = 1000.0 / fps;
    }

    /// <summary>现在是 nowMs：下一帧还要等多少毫秒（0 = 现在就可以给）。</summary>
    public double WaitMs(double nowMs) => Math.Max(0, _dueMs - nowMs);

    /// <summary>在 nowMs 给出了一帧：定下一帧的时间。</summary>
    public void Given(double nowMs) => _dueMs = nowMs - _dueMs > _periodMs ? nowMs + _periodMs : _dueMs + _periodMs;
}
