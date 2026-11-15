namespace WinDisplay.Core;

/// <summary>
/// 抓到一段声音时的回调：48 kHz、双声道、16 位小端 PCM，左右交替，长度是 <see cref="Proto.AudioFrameSize"/> 的倍数（协议「声音」）。
/// 在来源自己的线程上调；pcm 只在回调期间有效。
/// </summary>
public delegate void AudioSink(ReadOnlySpan<byte> pcm);

/// <summary>
/// 声音来源：这台电脑正在放的声音（平台专有的那一层实现它，比如 Windows 的 WASAPI 环回）。别的采样率 / 声道数由来源转成上面那一种再给。
/// </summary>
public interface IAudioSource
{
    /// <summary>
    /// 开始抓，抓到一段就调 sink（相邻两次给的采样在时间上是接着的；电脑没有声音时可以不调）。
    /// 返回的对象 Dispose 之后不再调 sink：Dispose 返回时最后一次回调已经结束。
    /// 抓不了（没有声卡、设备被拔了、换了默认设备……）不抛异常、不结束会话：来源自己隔一阵重开，这期间就是没有声音。
    /// </summary>
    IDisposable Start(AudioSink sink);
}

/// <summary>
/// 排着等发的声音：一个定长的环形缓冲，放不下时扔掉最旧的（连接堵住时延迟不往上积，协议「声音」）。
/// 放进来的每一段、容量都是 <see cref="Proto.AudioFrameSize"/> 的倍数，所以扔掉的也是整的采样帧，左右声道不会错开。线程安全。
/// </summary>
internal sealed class AudioQueue
{
    private readonly object _lock = new();
    private readonly byte[] _ring;
    private int _head, _len;
    private long _dropped;

    public AudioQueue(int capacity)
    {
        if (capacity <= 0 || capacity % Proto.AudioFrameSize != 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, $"容量要是 {Proto.AudioFrameSize} 的正整数倍");
        _ring = new byte[capacity];
    }

    public int Capacity => _ring.Length;

    /// <summary>里面有没有东西。不加锁地看一眼（看完可能就变了）。</summary>
    public bool IsEmpty => Volatile.Read(ref _len) == 0;

    /// <summary>放不下、扔掉的字节数合计。</summary>
    public long DroppedBytes
    {
        get { lock (_lock) return _dropped; }
    }

    public void Push(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length % Proto.AudioFrameSize != 0) throw new ArgumentException($"声音的长度 {pcm.Length} 不是 {Proto.AudioFrameSize} 的倍数", nameof(pcm));
        lock (_lock)
        {
            if (pcm.Length > _ring.Length)          // 一次就比容量大：只留最后那一段
            {
                _dropped += pcm.Length - _ring.Length;
                pcm = pcm[^_ring.Length..];
            }
            int over = _len + pcm.Length - _ring.Length;
            if (over > 0)                           // 放不下：扔掉最旧的
            {
                _head = (_head + over) % _ring.Length;
                _len -= over;
                _dropped += over;
            }
            int tail = (_head + _len) % _ring.Length;
            int first = Math.Min(pcm.Length, _ring.Length - tail);
            pcm[..first].CopyTo(_ring.AsSpan(tail));
            pcm[first..].CopyTo(_ring);
            _len += pcm.Length;
        }
    }

    /// <summary>全部取走，按放进来的先后写进 dst（至少要有 <see cref="Capacity"/> 那么大），返回取了多少字节。</summary>
    public int TakeAll(Span<byte> dst)
    {
        lock (_lock)
        {
            int n = _len;
            int first = Math.Min(n, _ring.Length - _head);
            _ring.AsSpan(_head, first).CopyTo(dst);
            _ring.AsSpan(0, n - first).CopyTo(dst[first..]);
            _head = 0;
            _len = 0;
            return n;
        }
    }
}

/// <summary>
/// 挡掉长时间的静音：电脑上常有程序开着一路声音却不出声（暂停的播放器、开着的网页），系统那时一直给全 0 的采样。
/// 照发的话白占 1.5 Mbps，Tab5 的 codec 也一直开着。连着 <see cref="HoldMs"/> 全是 0 之后就不发了，来了不是 0 的再接着发
/// （Tab5 那头和「电脑没有声音」一样处理：放空、重新攒，见协议「声音」）。短的静音（一句话和下一句之间）照发，不动它的时间。
/// 不是线程安全的：只在抓声音的那个线程上用。
/// </summary>
public sealed class SilenceGate
{
    /// <summary>连着这么久全是 0 才开始挡。</summary>
    public const int HoldMs = 500;

    private long _silentBytes;

    /// <summary>这一段要不要发。pcm 是协议的那种格式（长度是 <see cref="Proto.AudioFrameSize"/> 的倍数）。</summary>
    public bool ShouldSend(ReadOnlySpan<byte> pcm)
    {
        if (pcm.ContainsAnyExcept((byte)0))
        {
            _silentBytes = 0;
            return true;
        }
        if (_silentBytes >= (long)HoldMs * Proto.AudioBytesPerMs) return false;
        _silentBytes += pcm.Length;
        return true;
    }
}
