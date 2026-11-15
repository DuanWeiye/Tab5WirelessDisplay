using System.Buffers.Binary;
using System.Net.Sockets;

namespace WinDisplay.Core;

/// <summary>Tab5 发来的一个报文（已经解密）。</summary>
public readonly record struct Message(byte Type, ushort Arg, byte[] Payload);

/// <summary>
/// 和 Tab5 的一条连接：连上、握手（认证，没配对过的先配对，见 SenderHandshake）、收发报文。握手之后的报文逐个加密。
/// 只管字节的收发，不管什么时候发什么（那是 DisplaySession 的事）。
/// 读只能由一个线程做；写可以多个线程做（内部加锁：一个报文不会和别的报文搅在一起，加密用的报文计数也和发出的先后一致）。
/// 声音（<see cref="QueueAudio"/>）不用等一整帧画面发完：一帧是一个条带一个条带发的，每个条带之前先把排着的声音发掉。
/// Dispose 可以在别的线程调，卡在读或写上的调用会抛异常出来。
/// </summary>
public sealed class Tab5Link : IDisposable
{
    private const int MaxInboundPayload = 4096;     // Tab5 在握手之后发来的报文都很小（ACK 20、TOUCH 最多 25 字节）

    // 一次交给系统的字节数。发送时限是按「一次 Send 调用」算的（Windows 上尤其如此），整帧一次交出去的话，
    // 一帧接近 1 MB、链路又慢时，正常在发也会被判成超时。切成小块之后时限管的就是「这么久一点都发不动」
    private const int SendChunk = 64 * 1024;

    /// <summary>发不动（Tab5 不收数据了，TCP 窗口一直是满的）超过这么久就当连接断了。Tab5 那边对应的规定是「报文收到一半 5 秒没有新数据就断开」。</summary>
    public static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(5);

    private readonly Socket _sock;
    private readonly object _writeLock = new();
    private readonly byte[] _hdr = new byte[Proto.HeaderSize];
    private readonly byte[] _rxBuf = new byte[MaxInboundPayload + Proto.TagSize];   // 收一个报文的密文 ‖ 认证码，只有读的那个线程用
    private readonly RecordCipher _rxCipher;        // Tab5 → 电脑方向，只有读的那个线程用
    private readonly RecordCipher _txCipher;        // 电脑 → Tab5 方向，只在 _writeLock 里用
    private byte[] _tx = new byte[64 * 1024];       // 装一个加密好的报文，只在 _writeLock 里用；不够就换大的，之后一直留着（免得每帧分配一大块）

    // ── 声音 ──
    // 排着等发的声音。连接堵住时最多留这么多（200 ms），再多就扔掉最旧的：延迟不往上积（协议「声音」）
    private const int AudioBacklog = 200 * Proto.AudioBytesPerMs;
    private const int AudioOverhead = Proto.HeaderSize + Proto.TagSize;
    private readonly AudioQueue _audioQ = new(AudioBacklog);
    private long _audioSent;                                        // 只在 _writeLock 里写
    private volatile bool _audioOff;                                // 不再发声音了（StopAudio，或者发声音时连接断了）
    // 下面两个只在 _writeLock 里用：从队列里取出来的声音、加密好等着发的报文
    private readonly byte[] _audioTake = new byte[AudioBacklog];
    private readonly byte[] _audioTx = new byte[AudioBacklog + (AudioBacklog / Proto.AudioMaxBytes + 1) * AudioOverhead];

    private Tab5Link(Socket sock, Hello hello, RecordCipher tx, RecordCipher rx, uint caps)
    {
        _sock = sock;
        Hello = hello;
        Yuv422 = (caps & Proto.CapYuv422) != 0;
        _txCipher = tx;
        _rxCipher = rx;
    }

    public Hello Hello { get; }

    /// <summary>
    /// 这条连接走 YUV422 帧缓冲（握手里定的，协议「画面的色彩」）：条带必须是 4:2:2、画面先压到有限范围。
    /// false = 走 RGB565（Tab5 没说能走，或者 Connect 的 caps 里没要）：全范围，采样随意。
    /// </summary>
    public bool Yuv422 { get; }

    /// <summary>
    /// 连上 Tab5 并走完握手。连不上、握手的某一步等过了时限抛 SocketException；握手中途断了抛 IOException；
    /// 对方不是这个协议的 Tab5 抛 ProtocolException；认证没过（含要配对而现在不能配对、密码不对满了次数）抛 AuthException；
    /// Tab5 上的人在握手期间结束了（密码框上点了取消）抛 DeviceByeException；
    /// 握手期间本端的代码出了错（配对存不进文件、次数变了的通知或者 onPairing 里抛了异常）抛 LocalFaultException；
    /// ct 取消了抛 OperationCanceledException（连接、握手、等人输密码期间都立刻生效）。
    /// </summary>
    /// <param name="auth">配对存储、报给 Tab5 的名字、配对密码和密码不对的次数。</param>
    /// <param name="timeout">连接和握手每一步各自的时限。</param>
    /// <param name="sendTimeout">每次发送的时限，见 DefaultSendTimeout（不给就用它）。</param>
    /// <param name="pairReplyTimeout">配对时等人在 Tab5 上输密码最多多久，见 SenderHandshake.DefaultPairReplyTimeout（不给就用它）。</param>
    /// <param name="onPairing">配对走到哪一步了。在调用 Connect 的线程上回调；握手正停在回调里，不许做耗时的事（见 DisplaySession.Pairing）。</param>
    /// <param name="expectTab5Id">按自动发现的应答去连时给应答里的编号：HELLO 里报的不是它就抛 ProtocolException（见 SenderHandshake.Run）。</param>
    /// <param name="caps">AUTH 里声明的能力位（见 SenderHandshake.Run）。</param>
    public static Tab5Link Connect(string host, int port, LinkAuth auth, TimeSpan timeout, TimeSpan? sendTimeout = null,
                                   TimeSpan? pairReplyTimeout = null, Action<PairingStep>? onPairing = null, CancellationToken ct = default,
                                   byte[]? expectTab5Id = null, uint caps = Proto.SenderCaps)
    {
        ArgumentNullException.ThrowIfNull(auth);
        var sock = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var onCancel = ct.Register(sock.Dispose);     // 取消时关掉 socket，卡着的连接 / 握手里的等待马上出来
        try
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(timeout);
                try { sock.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult(); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new SocketException((int)SocketError.TimedOut); }
            }
            sock.SendTimeout = Math.Max(1, (int)(sendTimeout ?? DefaultSendTimeout).TotalMilliseconds);     // 0 在这里是「不限时」，不到 1 毫秒的时限不能变成它
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            try
            {
                sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
                sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 2);
                sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
            }
            catch (SocketException)
            {
                // 这三个选项老系统不支持（Windows 10 1709 之前）：用系统默认的保活间隔，不能因此连不上
            }
            var (hello, tx, rx, sentCaps) = SenderHandshake.Run(sock, auth, timeout, pairReplyTimeout ?? SenderHandshake.DefaultPairReplyTimeout, onPairing, expectTab5Id, caps);
            sock.ReceiveTimeout = 0;        // 之后报文之间可以一直没有数据；对端没了靠 TCP 保活发现
            var link = new Tab5Link(sock, hello, tx, rx, sentCaps);
            // 先撤掉取消的登记再看取消了没有：撤的时候会等正在跑的回调跑完，之后要么回调已经跑过（下一行就抛），
            // 要么永远不会再跑——返回出去的一定是一条没被关掉的连接
            onCancel.Dispose();
            if (ct.IsCancellationRequested)
            {
                link.Dispose();
                throw new OperationCanceledException(ct);
            }
            return link;
        }
        catch (Exception e) when (ct.IsCancellationRequested && e is not (OperationCanceledException or LocalFaultException))
        {
            sock.Dispose();
            throw new OperationCanceledException(ct);       // socket 是被取消关掉的，卡着的调用抛什么的都有，统一成「取消了」
        }
        catch
        {
            sock.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 收一个完整的报文并解密（阻塞）。连接断了抛 IOException / SocketException；报文头不合规定、认证码对不上抛 ProtocolException
    /// （认证码对不上之后这条连接不能再用：重放、丢了一个、换了顺序、改了头或内容，都落在这一条上）。
    /// </summary>
    public Message Read()
    {
        SenderHandshake.ReadExact(_sock, _hdr);
        var (type, flags, arg, len) = Proto.ReadHeader(_hdr);
        if (flags != 0) throw new ProtocolException($"报文 0x{type:x2} 的 flags 是 {flags}，应为 0");
        if (len > MaxInboundPayload) throw new ProtocolException($"报文 0x{type:x2} 的载荷 {len} 字节，太长了");
        Span<byte> data = _rxBuf.AsSpan(0, (int)len + Proto.TagSize);
        SenderHandshake.ReadExact(_sock, data);
        var payload = len == 0 ? [] : new byte[len];
        if (!_rxCipher.TryOpen(_hdr, data, payload)) throw new ProtocolException($"报文 0x{type:x2}（载荷 {len} 字节）的认证码对不上");
        return new Message(type, arg, payload);
    }

    /// <summary>
    /// 发一帧：若干个 STRIP + 一个 PRESENT，各自加密，连在一起发。发不动超过时限抛 SocketException，之后这条连接不能再用
    /// （报文计数已经往前走了，没发出去的那几个补不回来）。
    /// </summary>
    public void SendFrame(IReadOnlyList<Strip> strips, uint seq, uint tMs)
    {
        const int overhead = Proto.HeaderSize + Proto.TagSize;
        int biggest = Proto.PresentSize;
        foreach (var s in strips) biggest = Math.Max(biggest, s.Jpeg.Length);
        Span<byte> present = stackalloc byte[Proto.PresentSize];
        BinaryPrimitives.WriteUInt32LittleEndian(present, seq);
        BinaryPrimitives.WriteUInt32LittleEndian(present[4..], tMs);
        lock (_writeLock)
        {
            if (_tx.Length < biggest + overhead) _tx = new byte[Math.Max(biggest + overhead, _tx.Length * 2)];
            // 一个条带一个条带地发，每个之前先把排着的声音发掉：一帧有几十上百 KB，整帧一口气交出去的话这段时间到的声音都得排在它后面
            foreach (var s in strips)
            {
                FlushAudio();
                SendAll(_tx, Seal(_tx, Proto.TStrip, (ushort)s.Row, s.Jpeg));
            }
            FlushAudio();
            SendAll(_tx, Seal(_tx, Proto.TPresent, 0, present));
        }
        PumpAudio();
    }

    /// <summary>
    /// 发一个没有载荷的报文（BYE、KBD_LEDS）。lockTimeoutMs：别的线程正占着发送（卡在一次写上）时最多等这么久，等不到抛 IOException；
    /// 不给 = 一直等。
    /// </summary>
    public void SendSimple(byte type, ushort arg = 0, int lockTimeoutMs = Timeout.Infinite)
    {
        var buf = new byte[Proto.HeaderSize + Proto.TagSize];
        if (!Monitor.TryEnter(_writeLock, lockTimeoutMs)) throw new IOException("别的线程正卡在发送上，这个报文发不出去");
        try
        {
            Seal(buf, type, arg, []);
            SendAll(buf, buf.Length);
        }
        finally
        {
            Monitor.Exit(_writeLock);
        }
        PumpAudio();
    }

    /// <summary>
    /// 排一段声音（48 kHz、双声道、16 位小端，长度是 4 的倍数；协议 AUDIO）。哪个线程都能调，一般是抓声音的线程。
    /// 发送这时空着就当场发掉；有别的线程正在发画面就留在队列里，由它在下一个条带之前发。队列里超过 200 ms 的扔掉最旧的。
    /// 不抛连接方面的异常：发的时候发现连接断了就把连接关掉，读报文的那一头会报出来。<see cref="StopAudio"/> 之后调什么都不做。
    /// 调用方要先看 <see cref="Hello"/> 的能力位：Tab5 没说能收声音就不能调。
    /// </summary>
    public void QueueAudio(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length % Proto.AudioFrameSize != 0) throw new ArgumentException($"声音的长度 {pcm.Length} 不是 {Proto.AudioFrameSize} 的倍数", nameof(pcm));
        if (_audioOff || pcm.IsEmpty) return;
        _audioQ.Push(pcm);
        try
        {
            PumpAudio();
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            // 发不出去（发了一半的报文补不回来，这条连接不能再用）：关掉它，卡在读上的那个线程会抛异常出来，会话按掉线处理
            _audioOff = true;
            CloseSocket();
        }
    }

    /// <summary>不再发声音：之后排进来的、队列里还没发的都不发了（正在发的那一段照常发完）。发 BYE 之前调。</summary>
    public void StopAudio() => _audioOff = true;

    /// <summary>这条连接上发出去的声音（毫秒），和连接堵住时扔掉的（毫秒）。</summary>
    public (double SentMs, double DroppedMs) AudioStats =>
        ((double)Interlocked.Read(ref _audioSent) / Proto.AudioBytesPerMs, (double)_audioQ.DroppedBytes / Proto.AudioBytesPerMs);

    // 队列里有声音、发送又空着，就发掉。不拿着 _writeLock 调。每个用完 _writeLock 的地方放锁之后都要调一次：
    // 它占着锁的时候别的线程排进来的声音拿不到锁、留在队列里，得由它出来之后发（不然要等到下一次有人发东西）
    private void PumpAudio()
    {
        while (!_audioOff && !_audioQ.IsEmpty && Monitor.TryEnter(_writeLock))
        {
            try { FlushAudio(); }
            finally { Monitor.Exit(_writeLock); }
        }
    }

    // 把队列里的声音都发掉，切成不超过 Proto.AudioMaxBytes 的报文。要拿着 _writeLock 调
    private void FlushAudio()
    {
        if (_audioOff || _audioQ.IsEmpty) return;
        int n = _audioQ.TakeAll(_audioTake);
        int pos = 0;
        for (int at = 0; at < n; at += Proto.AudioMaxBytes)
            pos += Seal(_audioTx.AsSpan(pos), Proto.TAudio, 0, _audioTake.AsSpan(at, Math.Min(Proto.AudioMaxBytes, n - at)));
        SendAll(_audioTx, pos);
        Interlocked.Add(ref _audioSent, n);
    }

    // 往 dst 里写一个加密的报文：头 ‖ 密文 ‖ 认证码，返回写了多少字节。要拿着 _writeLock 调，而且写完要按先后发出去
    private int Seal(Span<byte> dst, byte type, ushort arg, ReadOnlySpan<byte> payload)
    {
        Proto.WriteHeader(dst, type, arg, payload.Length);
        _txCipher.Seal(dst[..Proto.HeaderSize], payload, dst.Slice(Proto.HeaderSize, payload.Length + Proto.TagSize));
        return Proto.HeaderSize + payload.Length + Proto.TagSize;
    }

    /// <summary>改发送时限（对之后的发送生效）。</summary>
    public void SetSendTimeout(TimeSpan timeout) => _sock.SendTimeout = Math.Max(1, (int)timeout.TotalMilliseconds);

    /// <summary>不再发了（发完 BYE 之后调）：让对端读到头，但还能接着收，直到对端关连接。</summary>
    public void ShutdownSend() => _sock.Shutdown(SocketShutdown.Send);

    private void SendAll(byte[] buf, int len)
    {
        for (int sent = 0; sent < len;)
            sent += _sock.Send(buf, sent, Math.Min(SendChunk, len - sent), SocketFlags.None);
    }

    private void CloseSocket()
    {
        try { _sock.Shutdown(SocketShutdown.Both); } catch (SocketException) { } catch (ObjectDisposedException) { }
        _sock.Dispose();
    }

    /// <summary>关掉连接，会话密钥也一起收掉。别的线程正卡在 Read() 或发送里的话会抛异常出来。可以重复调。</summary>
    public void Dispose()
    {
        _audioOff = true;
        CloseSocket();
        // 别的线程正在加密 / 解密一个报文也没事：那一次照常做完，之后再用的抛 ObjectDisposedException（和连接关了之后再读写一样）
        _txCipher.Dispose();
        _rxCipher.Dispose();
    }
}
