using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace WinDisplay.Core;

/// <summary>握手要用的东西：配对存在哪（里面有自己的编号）、报给 Tab5 的名字、现在能不能配对。</summary>
/// <param name="Name">给人看的名字，Tab5 的配对列表里显示它；一般是机器名。</param>
/// <param name="Pairing">配对密码和密码不对的次数。不给、没设密码、或者已经停止配对：遇到要配对的情况就按认证没过处理，不发 PAIR。</param>
public sealed record LinkAuth(PairStore Store, string Name, PairingGate? Pairing = null);

/// <summary>配对走到哪一步了（给界面 / 日志用）。</summary>
public enum PairingStep
{
    WaitingForPassword,     // PAIR 发出去了，等人在 Tab5 上输密码
    WrongPassword,          // Tab5 上输的密码和电脑的不一样；次数没记满就接着再来一次
    PasswordAccepted,       // 密码对上了，接着认证
}

/// <summary>
/// 发送端这一侧的握手（PROTOCOL.md「认证和加密」）：收 HELLO；有这台 Tab5 的配对密钥就认证，没有（或者 Tab5 回了 AUTH_UNKNOWN）
/// 而且现在可以配对，就先配对再认证；换出两个方向的会话密钥。握手期间的报文是明文，两边的 READY 起加密。
/// </summary>
internal static class SenderHandshake
{
    /// <summary>发出 PAIR 之后等 PAIR_REPLY 最多这么久：Tab5 的密码框开 2 分钟，这里略长一点，正常是 Tab5 先放弃。</summary>
    public static readonly TimeSpan DefaultPairReplyTimeout = TimeSpan.FromSeconds(130);

    private const int MaxHandshakePayload = 256;    // 握手报文最长的是 PAIR_REPLY（97 字节）
    private const int ByeGraceMs = 300;             // 见 Bye

    private readonly record struct Plain(byte Type, ushort Arg, byte[] Payload, byte[] Raw);

    /// <summary>
    /// 走完握手，返回 HELLO、两个方向的加密状态和 AUTH 里实际声明的能力位。每一步等 Tab5 的下一个报文到齐最多 step（等人输密码那一步是 pairReply）；
    /// sock 的接收时限由这里设，返回时不恢复。
    /// 抛 ProtocolException（Tab5 不守协议）、AuthException（认证没过、要配对而现在不能配对、密码不对满了次数、握手期间配对被清掉了）、
    /// DeviceByeException（Tab5 上的人结束的）、SocketException / IOException（断了，或者等过了时限）、
    /// LocalFaultException（本端的代码出了错：配对存不进文件、次数变了的通知或者 onPairing 里抛了异常）。
    /// </summary>
    /// <param name="expectTab5Id">按自动发现的应答连过来的：应答里的 Tab5 编号。HELLO 里报的不是它就抛 ProtocolException，什么都不发。</param>
    /// <param name="caps">AUTH 里声明的能力位（<see cref="Proto.CapInputMode"/> 等）：声明了哪一位，握手之后就得认得 Tab5 发来的那种报文。
    /// <see cref="Proto.CapYuv422"/> 只有 Tab5 在 HELLO 里说了能走才留着，否则去掉（返回值里的 Caps 是去掉之后的）。</param>
    public static (Hello Hello, RecordCipher Tx, RecordCipher Rx, uint Caps) Run(Socket sock, LinkAuth auth, TimeSpan step, TimeSpan pairReply,
                                                                    Action<PairingStep>? onPairing, byte[]? expectTab5Id = null,
                                                                    uint caps = Proto.SenderCaps)
    {
        PairStore store = auth.Store;
        PairingGate? gate = auth.Pairing;
        void Report(PairingStep at) => Local(() => onPairing?.Invoke(at));

        Plain first = ReadPlain(sock, step);
        if (first.Type != Proto.THello || first.Payload.Length != Proto.HelloSize)
            throw new ProtocolException($"连上后第一个报文不是 HELLO（type=0x{first.Type:x2}，载荷 {first.Payload.Length} 字节）");
        Hello hello = Proto.ParseHello(first.Arg, first.Payload);
        if (hello.Version != Proto.Version || hello.FbW != Proto.FbW || hello.FbH != Proto.FbH
            || hello.ViewW != Proto.ViewW || hello.ViewH != Proto.ViewH)
            throw new ProtocolException($"Tab5 报的协议版本 {hello.Version}、帧缓冲 {hello.FbW}×{hello.FbH}、画面 {hello.ViewW}×{hello.ViewH} 和本程序不一致");
        // 连不连这一台、它配对过没有，是按应答里的编号定的；人在电脑上移除配对时「正连着的是不是它」也按那个编号认。
        // 下面查配对、存配对用的是 HELLO 里的编号：两个不一样的话，连着的就不是当初决定去连的那一台
        if (expectTab5Id is not null && !hello.Tab5Id.AsSpan().SequenceEqual(expectTab5Id))
            throw new ProtocolException("HELLO 里的 Tab5 编号和它应答自动发现时报的不一样");
        byte[] helloRaw = first.Raw;
        // 走不走 YUV422 帧缓冲由发送端在 AUTH 里说，但只能对说了「能走」的 Tab5 说（协议「画面的色彩」）
        if ((hello.Caps & Proto.CapDevYuv422) == 0) caps &= ~Proto.CapYuv422;

        // 走「每次连接」。认证过了返回两个方向的加密状态；Tab5 回 AUTH_UNKNOWN 返回 null。sent：AUTH 发出去之后调一下
        (RecordCipher Tx, RecordCipher Rx)? Authenticate(byte[] pairingKey, Action? sent = null)
        {
            var (priv, pub) = SessionCrypto.X25519KeyPair();
            byte[]? dh = null, pcToTab5 = null, tab5ToPc = null;
            try
            {
                byte[] authRaw = Proto.PackAuth(caps, store.Id, SessionCrypto.KeyId(pairingKey), pub, auth.Name);
                SendAll(sock, authRaw);
                sent?.Invoke();
                Plain reply = ReadPlain(sock, step);
                if (reply.Type == Proto.TAuthUnknown && reply.Payload.Length == 0) return null;
                if (reply.Type == Proto.TDevBye && reply.Payload.Length == 0) throw new DeviceByeException();
                if (reply.Type != Proto.TAuthOk || reply.Payload.Length != Proto.AuthOkSize)
                    throw new ProtocolException($"AUTH 之后来的是 0x{reply.Type:x2}（载荷 {reply.Payload.Length} 字节），不是 AUTH_OK");
                try
                {
                    dh = SessionCrypto.X25519Shared(priv, reply.Payload);
                }
                catch (CryptoCheckException e)
                {
                    throw new ProtocolException($"AUTH_OK：{e.Message}");
                }
                byte[] th = SHA256.HashData([.. helloRaw, .. authRaw, .. reply.Raw]);
                (pcToTab5, tab5ToPc) = SessionCrypto.SessionKeys(pairingKey, dh, th);

                // AUTH_OK 之后 Tab5 发的都是加密的，第一个必须是 READY。认证码对不上 = 它手里没有这把配对密钥：断开，什么都不再发
                var rx = new RecordCipher(tab5ToPc);
                try
                {
                    var within = new Deadline(step);
                    var hdr = new byte[Proto.HeaderSize];
                    ReadExact(sock, hdr, within);
                    var (type, flags, arg, len) = Proto.ReadHeader(hdr);
                    if (len != 0) throw new ProtocolException($"Tab5 加密后的第一个报文 0x{type:x2} 带了 {len} 字节的载荷，不是 READY");
                    var tag = new byte[Proto.TagSize];
                    ReadExact(sock, tag, within);
                    if (!rx.TryOpen(hdr, tag, []))
                        throw new AuthException(AuthFailure.KeyMismatch, "Tab5 的 READY 认证码对不上：它手里没有和这台电脑配对时留下的密钥");
                    // 只认 READY：AUTH_OK 和 READY 是一次发出的，Tab5 这时要结束的话 BYE 也排在 READY 后面
                    if (type != Proto.TReady || flags != 0 || arg != 0)
                        throw new ProtocolException($"Tab5 加密后的第一个报文是 0x{type:x2}（flags={flags} arg={arg}），不是 READY");

                    var tx = new RecordCipher(pcToTab5);
                    try
                    {
                        SendAll(sock, Sealed(tx, Proto.TReady));
                        return (tx, rx);
                    }
                    catch
                    {
                        tx.Dispose();
                        throw;
                    }
                }
                catch
                {
                    rx.Dispose();
                    throw;
                }
            }
            finally
            {
                // 怎么出来的都清掉：一次性的私钥、共享值、两把会话密钥（已经交给 RecordCipher 了，这里的是拷贝）
                CryptographicOperations.ZeroMemory(priv);
                CryptographicOperations.ZeroMemory(dh);
                CryptographicOperations.ZeroMemory(pcToTab5);
                CryptographicOperations.ZeroMemory(tab5ToPc);
            }
        }

        // 走「配对」，返回配出来的密钥（还没存：握手走完才存）。现在不能配对抛 AuthException
        byte[] Pair(AuthFailure why, string what)
        {
            bool tried = false;         // 这条连接上已经有一次密码不对，而且还没经 onPairing 说出去
            while (true)
            {
                // 每发一个 PAIR 之前先占一次机会（密码不对的次数有上限，见 PairingGate.Begin）
                PairingSecret? secret = gate?.Begin();
                if (secret is null)
                {
                    if (tried) Report(PairingStep.WrongPassword);
                    if (tried && gate!.Stopped)
                        throw new AuthException(AuthFailure.WrongPassword, "配对：Tab5 上输的密码不对，次数记满了，停止配对");
                    string? because = gate switch
                    {
                        null => null,
                        { Stopped: true } => "密码不对的次数记满了，已经停止配对，要在电脑上恢复",
                        { HasPassword: false } => tried ? "配对到一半，电脑上的配对密码被清掉了" : null,
                        _ => "剩下的几次机会正被别的连接占着，等它们有了结果再连",
                    };
                    throw new AuthException(why, because is null ? what : $"{what}（{because}）");
                }
                bool wrong = false;
                byte[]? ke = null, cA = null;
                try
                {
                    var spake = new Spake2(Spake2.Role.A, secret);      // 每次重新取随机数
                    byte[] pairRaw = Proto.PackPair(store.Id, auth.Name, spake.Pub);
                    SendAll(sock, pairRaw);
                    // 上一次密码不对，等这一个 PAIR 发出去了才说：Tab5 发出 PAIR_REPLY 之后只等 5 秒，回调不能挡在它前面；
                    // 这时 Tab5 等的是人输密码，有 2 分钟
                    if (tried) Report(PairingStep.WrongPassword);
                    tried = false;
                    Report(PairingStep.WaitingForPassword);
                    Plain reply = ReadPlain(sock, pairReply);
                    if (reply.Type == Proto.TDevBye && reply.Payload.Length == 0) throw new DeviceByeException();
                    if (reply.Type != Proto.TPairReply || reply.Payload.Length != Proto.PairReplySize)
                        throw new ProtocolException($"PAIR 之后来的是 0x{reply.Type:x2}（载荷 {reply.Payload.Length} 字节），不是 PAIR_REPLY");
                    ReadOnlySpan<byte> pB = reply.Payload.AsSpan(0, P256.PointSize), cB = reply.Payload.AsSpan(P256.PointSize);
                    byte[] wantCb;
                    try
                    {
                        (ke, cA, wantCb) = spake.Finish(pB, store.Id, hello.Tab5Id, SHA256.HashData([.. helloRaw, .. pairRaw]));
                    }
                    catch (CryptoCheckException e)
                    {
                        throw new ProtocolException($"PAIR_REPLY：{e.Message}");
                    }
                    if (CryptographicOperations.FixedTimeEquals(cB, wantCb))
                    {
                        SendAll(sock, Proto.Pack(Proto.TPairOk, 0, cA));
                        return SessionCrypto.PairingKey(ke);
                    }
                    wrong = tried = true;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(ke);
                    CryptographicOperations.ZeroMemory(cA);
                    // 占下的那一次机会的下文。密码不对的次数在发下一个 PAIR 之前记上（存盘的通知也在这里面）
                    if (wrong) Local(gate!.WrongPassword);
                    else gate!.End();
                }
            }
        }

        ForgetMark mark = store.Mark(hello.Tab5Id);     // 握手期间人在电脑上改了密码、或者移除了这台 Tab5 的配对的话，最后不存（见 ForgetMark）
        byte[]? stored = store.FindKey(hello.Tab5Id);
        (RecordCipher Tx, RecordCipher Rx)? ciphers = stored is null ? null : Authenticate(stored);
        byte[] used = stored!;
        if (ciphers is null)
        {
            used = stored is null
                ? Pair(AuthFailure.NotPaired, $"没和这台 Tab5（{hello.Name}）配对过，要先配对")
                : Pair(AuthFailure.Forgotten, $"这台 Tab5（{hello.Name}）那边没有和这台电脑配对时留下的密钥了，要重新配对");
            // 「密码对上了」等 AUTH 发出去了才说：Tab5 收到 PAIR_OK 之后只等 5 秒
            ciphers = Authenticate(used, () => Report(PairingStep.PasswordAccepted)) ?? throw new ProtocolException("刚配对完，Tab5 却回了 AUTH_UNKNOWN");
        }
        var (tx, rx) = ciphers.Value;
        try
        {
            // 握手走完了才存：新配的密钥、Tab5 改了的名字。存下了才算配对成功，密码不对的次数到这时才清零
            bool saved = false;
            Local(() => saved = store.Save(hello.Tab5Id, used, hello.Name, mark));
            if (!saved)
                throw new AuthException(AuthFailure.Revoked, $"和这台 Tab5（{hello.Name}）握手的时候，电脑上改了配对密码或者移除了配对：这一次的不作数");
            if (!ReferenceEquals(used, stored)) Local(() => gate?.Paired());
            return (hello, tx, rx, caps);
        }
        catch
        {
            // 握手已经走完，本端却不能用这条连接：照协议发 BYE（Tab5 马上回到等连接），再把加密状态收掉
            Bye(sock, tx);
            tx.Dispose();
            rx.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 调本端自己的代码（存配对、次数变了的通知、配对进度的回调）：它抛的异常包成 LocalFaultException 带出去，
    /// 不和连接上的错（SocketException / IOException）混在一起——存配对时磁盘满了不是「没连上」。
    /// </summary>
    private static void Local(Action call)
    {
        try
        {
            call();
        }
        catch (Exception e)
        {
            throw new LocalFaultException(e);
        }
    }

    /// <summary>一个没有载荷的加密报文（READY、BYE）：头 ‖ 认证码。</summary>
    private static byte[] Sealed(RecordCipher tx, byte type)
    {
        var raw = new byte[Proto.HeaderSize + Proto.TagSize];
        Proto.WriteHeader(raw, type, 0, 0);
        tx.Seal(raw.AsSpan(0, Proto.HeaderSize), [], raw.AsSpan(Proto.HeaderSize));
        return raw;
    }

    /// <summary>
    /// 发 BYE，只关发送方向，等 Tab5 关连接（PROTOCOL.md「BYE 之后怎么关连接」），最多等 ByeGraceMs 的两倍。
    /// 发不出去、等不到都算了：调用方接着就把连接关掉。
    /// </summary>
    private static void Bye(Socket sock, RecordCipher tx)
    {
        try
        {
            SendAll(sock, Sealed(tx, Proto.TBye));
            sock.Shutdown(SocketShutdown.Send);
            sock.ReceiveTimeout = ByeGraceMs;
            long start = Stopwatch.GetTimestamp();
            var rest = new byte[256];
            while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < ByeGraceMs && sock.Receive(rest, SocketFlags.None) > 0)
            {
            }
        }
        catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// 握手里一步的期限：从建出来起算，整个报文要在这之前到齐（不是每收到一点数据就重新计时——那样对面每隔几秒给一个字节就能一直拖着）。
    /// </summary>
    private readonly struct Deadline(TimeSpan limit)
    {
        private readonly long _start = Stopwatch.GetTimestamp();

        /// <summary>离期限还有多久；已经过了抛 SocketException（TimedOut），和系统的接收超时一样。</summary>
        public TimeSpan Left()
        {
            TimeSpan left = limit - Stopwatch.GetElapsedTime(_start);
            return left > TimeSpan.Zero ? left : throw new SocketException((int)SocketError.TimedOut);
        }
    }

    /// <summary>
    /// 收一个明文的握手报文，最多等 limit。flags 不是 0、arg 不是 0（HELLO 的 arg 是版本号，由调用方查）、载荷太长都算不守协议。
    /// </summary>
    private static Plain ReadPlain(Socket sock, TimeSpan limit)
    {
        var within = new Deadline(limit);
        var hdr = new byte[Proto.HeaderSize];
        ReadExact(sock, hdr, within);
        var (type, flags, arg, len) = Proto.ReadHeader(hdr);
        if (flags != 0) throw new ProtocolException($"握手报文 0x{type:x2} 的 flags 是 {flags}，应为 0");
        if (type != Proto.THello && arg != 0) throw new ProtocolException($"握手报文 0x{type:x2} 的 arg 是 {arg}，应为 0");
        if (len > MaxHandshakePayload) throw new ProtocolException($"握手报文 0x{type:x2} 的载荷 {len} 字节，太长了");
        var raw = new byte[Proto.HeaderSize + (int)len];
        hdr.CopyTo(raw, 0);
        ReadExact(sock, raw.AsSpan(Proto.HeaderSize), within);
        return new Plain(type, arg, raw[Proto.HeaderSize..], raw);
    }

    private static void ReadExact(Socket sock, Span<byte> buf, Deadline within)
    {
        while (buf.Length > 0)
        {
            // 0 在这里是「不限时」，不到 1 毫秒的时限不能变成它
            sock.ReceiveTimeout = (int)Math.Clamp(Math.Ceiling(within.Left().TotalMilliseconds), 1, int.MaxValue);
            int n = sock.Receive(buf, SocketFlags.None);
            if (n == 0) throw new IOException("对端断开了连接");
            buf = buf[n..];
        }
    }

    internal static void ReadExact(Socket sock, Span<byte> buf)
    {
        while (buf.Length > 0)
        {
            int n = sock.Receive(buf, SocketFlags.None);
            if (n == 0) throw new IOException("对端断开了连接");
            buf = buf[n..];
        }
    }

    private static void SendAll(Socket sock, ReadOnlySpan<byte> data)
    {
        while (data.Length > 0) data = data[sock.Send(data, SocketFlags.None)..];
    }
}
