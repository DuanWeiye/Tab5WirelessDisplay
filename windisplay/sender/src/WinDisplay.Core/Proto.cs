using System.Buffers.Binary;
using System.Text;

namespace WinDisplay.Core;

/// <summary>
/// 协议常量和报文的打包 / 解析（见 windisplay/PROTOCOL.md，v2）。所有整数小端，每个报文 = 8 字节头 + 载荷；
/// 握手之后的报文逐个加密，载荷后面多 16 字节认证码（见 RecordCipher），这里说的长度都是明文载荷的。
/// </summary>
public static class Proto
{
    public const int Port = 7250;
    public const int Version = 2;
    public const int FbW = 720, FbH = 1280;         // 帧缓冲（面板原生竖屏）
    public const int ViewW = 1280, ViewH = 720;     // 人看到的横屏画面
    public const int StripAlign = 16;               // 条带的起始行和高都是它的倍数
    public const int HeaderSize = 8;
    public const int AckSize = 20, PresentSize = 8, TouchPointSize = 5, KeySize = 8, MouseSize = 6;
    public const int MaxTouchPoints = 5;
    /// <summary>Tab5 上有键 / 触点按着的时候，它至少每隔这么久发一个输入报文（TOUCH / KEY / HELD），见协议「按着的时候」。毫秒。</summary>
    public const int HeldPeriodMs = 200;
    /// <summary>有键 / 触点按着、却这么久没收到 Tab5 的输入报文：它报的那份状态不新鲜了（3 个周期多一点）。毫秒。</summary>
    public const int HeldStaleMs = 700;
    public const int IdSize = 16;                   // 两边各自的编号
    public const int NameSize = 32;                 // 给人看的名字，UTF-8，不足补 0
    public const int TagSize = 16;                  // 加密报文的认证码
    // 握手报文的载荷，都是定长
    public const int HelloSize = 68, AuthSize = 92, AuthOkSize = 32, PairSize = 113, PairReplySize = 97, PairOkSize = 32;

    public const byte THello = 0x01, TAuth = 0x02, TAuthOk = 0x03, TAuthUnknown = 0x04, TReady = 0x05;
    public const byte TPair = 0x06, TPairReply = 0x07, TPairOk = 0x08;
    public const byte TStrip = 0x10, TPresent = 0x11, TAck = 0x12;
    public const byte TTouch = 0x20, TKey = 0x21, TMouse = 0x22, THeld = 0x23, TInputMode = 0x24, TTouchpadGain = 0x25, TPicture = 0x26, TPictureMode = 0x27, TDevBye = 0x2F;
    public const byte TKbdLeds = 0x30, TBye = 0x3F;
    public const byte TAudio = 0x40;

    /// <summary>HELLO 的能力位 bit0：这台 Tab5 能收 AUDIO（见协议「声音」）。没置位不得发。</summary>
    public const uint CapDevAudio = 1u << 0;

    /// <summary>HELLO 的能力位 bit1：这台 Tab5 能走 YUV422 帧缓冲（见协议「画面的色彩」）。置了这一位，AUTH 里才可以置 <see cref="CapYuv422"/>。</summary>
    public const uint CapDevYuv422 = 1u << 1;

    /// <summary>声音的格式是定死的：48 kHz、双声道、16 位小端，左右交替。</summary>
    public const int AudioRate = 48000;
    /// <summary>一个采样帧的字节数（左、右各 16 位）。</summary>
    public const int AudioFrameSize = 4;
    /// <summary>一毫秒的声音有多少字节。</summary>
    public const int AudioBytesPerMs = AudioRate / 1000 * AudioFrameSize;
    /// <summary>一个 AUDIO 报文的载荷最多这么多字节（20 ms）。</summary>
    public const int AudioMaxBytes = 20 * AudioBytesPerMs;

    /// <summary>AUTH 的能力位 bit0：这个发送端认得 INPUT_MODE（Tab5 只对说了认得的发送端发它，见协议「触摸方式」）。</summary>
    public const uint CapInputMode = 1u << 0;

    /// <summary>AUTH 的能力位 bit1：这个发送端认得 TPAD_GAIN（触摸板模式下光标的加速，同上，见协议「触摸方式」）。</summary>
    public const uint CapTouchpadGain = 1u << 1;

    /// <summary>
    /// AUTH 的能力位 bit2：这个发送端认得 PICTURE（见协议「画质」）：Tab5 把选的那一档折成「质量 / 画面在动时每帧多少 KB」报过来。
    /// 认得 <see cref="CapPictureMode"/> 的发送端 Tab5 不发这个；这一位留着是为了连上不认得档位的老固件时画质照旧听它的。
    /// </summary>
    public const uint CapPicture = 1u << 2;

    /// <summary>
    /// AUTH 的能力位 bit3：这条连接的条带都是 4:2:2、画面压在有限范围里，Tab5 走 YUV422 帧缓冲（见协议「画面的色彩」）。
    /// 只有 HELLO 里置了 <see cref="CapDevYuv422"/> 的 Tab5 才可以对它置（握手里自动去掉，见 SenderHandshake.Run）。
    /// </summary>
    public const uint CapYuv422 = 1u << 3;

    /// <summary>
    /// AUTH 的能力位 bit4：这个发送端认得 PICTURE_MODE（见协议「画质」）：Tab5 报人在它上面选的是第几档、它的屏幕多久刷新一次，
    /// 帧率怎么限、画面在动时降多少质量由发送端按档位自己定。置了这一位，Tab5 就不发 PICTURE 了。
    /// </summary>
    public const uint CapPictureMode = 1u << 4;

    /// <summary>这一版发送端认得的全部能力位。</summary>
    public const uint SenderCaps = CapInputMode | CapTouchpadGain | CapPicture | CapYuv422 | CapPictureMode;

    /// <summary>PICTURE_MODE 的载荷：屏幕的帧周期（微秒，u32 小端）。能取的范围 5~50 毫秒（200~20 Hz），之外算不守协议。</summary>
    public const int PictureModeSize = 4;
    public const int FramePeriodUsMin = 5000, FramePeriodUsMax = 50000;
    /// <summary>Tab5 的屏幕实测的帧周期（微秒，65.4 Hz）：Tab5 还没报的时候按它。</summary>
    public const int FramePeriodUsDefault = 15270;

    /// <summary>有限范围：0~255 压到 16~235（协议「画面的色彩」，四舍五入）。走 YUV422 的连接上画面的三个通道编码之前各过一遍。</summary>
    public static byte ToLimited(int v) => (byte)(16 + (v * 219 + 127) / 255);

    /// <summary>TPAD_GAIN 的 arg（单位 0.1 倍）能取的范围：1 倍（不加速）到 5 倍。</summary>
    public const int TouchpadGainMin = 10, TouchpadGainMax = 50;

    /// <summary>写 8 字节报文头：type、flags（恒 0）、arg、载荷长度。</summary>
    public static void WriteHeader(Span<byte> dst, byte type, ushort arg, int payloadLen)
    {
        dst[0] = type;
        dst[1] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(dst[2..], arg);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], (uint)payloadLen);
    }

    public static (byte Type, byte Flags, ushort Arg, uint Len) ReadHeader(ReadOnlySpan<byte> src) =>
        (src[0], src[1], BinaryPrimitives.ReadUInt16LittleEndian(src[2..]), BinaryPrimitives.ReadUInt32LittleEndian(src[4..]));

    public static Hello ParseHello(ushort version, ReadOnlySpan<byte> p)
    {
        if (p.Length != HelloSize) throw new ProtocolException($"HELLO 载荷 {p.Length} 字节，应为 {HelloSize}");
        uint maxStrip = BinaryPrimitives.ReadUInt32LittleEndian(p[8..]), maxFrame = BinaryPrimitives.ReadUInt32LittleEndian(p[12..]);
        if (maxStrip == 0 || maxStrip > int.MaxValue || maxFrame == 0 || maxFrame > int.MaxValue)
            throw new ProtocolException($"HELLO 里的单条带上限 {maxStrip}、单帧上限 {maxFrame} 不合理");
        return new Hello(version,
            BinaryPrimitives.ReadUInt16LittleEndian(p), BinaryPrimitives.ReadUInt16LittleEndian(p[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[4..]), BinaryPrimitives.ReadUInt16LittleEndian(p[6..]),
            (int)maxStrip, (int)maxFrame, BinaryPrimitives.ReadUInt32LittleEndian(p[16..]),
            p.Slice(20, IdSize).ToArray(), UnpackName(p.Slice(20 + IdSize, NameSize)));
    }

    /// <summary>名字 → 32 字节：UTF-8，太长就在字符的边界上截断，不足补 0。</summary>
    public static byte[] PackName(string name)
    {
        byte[] raw = Encoding.UTF8.GetBytes(name);
        int n = Math.Min(raw.Length, NameSize);
        if (n < raw.Length)
            while (n > 0 && (raw[n] & 0xC0) == 0x80) n--;       // 落在一个字符的中间：退到它的第一个字节之前
        var packed = new byte[NameSize];
        raw.AsSpan(0, n).CopyTo(packed);
        return packed;
    }

    /// <summary>
    /// 对端报的名字只当显示用的文字：到第一个 0 为止；不是合法 UTF-8 的字节、控制字符、看不见又能搅乱显示的字符
    /// （零宽、换行 / 分段、方向控制）换成 ?，别的原样。换哪些字符是 PROTOCOL.md「握手」表下面写死的，固件和 Python 工具同一份。
    /// </summary>
    public static string UnpackName(ReadOnlySpan<byte> raw)
    {
        int end = raw.IndexOf((byte)0);
        string text = Encoding.UTF8.GetString(end < 0 ? raw : raw[..end]);      // 不合法的字节解出来是 U+FFFD
        var sb = new StringBuilder(text.Length);
        foreach (Rune r in text.EnumerateRunes()) sb.Append(HiddenInName(r.Value) ? "?" : r.ToString());
        return sb.ToString();
    }

    private static bool HiddenInName(int c) =>
        c is < 0x20 or (>= 0x7F and <= 0x9F) or (>= 0x200B and <= 0x200F) or (>= 0x2028 and <= 0x202E) or (>= 0x2060 and <= 0x206F)
            or 0xFEFF or 0xFFFD;

    /// <summary>一个明文报文在线上的字节（握手用；握手之后的报文由 Tab5Link 加密）。</summary>
    public static byte[] Pack(byte type, ushort arg, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[HeaderSize + payload.Length];
        WriteHeader(buf, type, arg, payload.Length);
        payload.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    /// <summary>AUTH 报文：能力位（<see cref="CapInputMode"/> 等）、电脑编号、配对密钥的标识、一次性 X25519 公钥、电脑的名字。</summary>
    public static byte[] PackAuth(uint caps, ReadOnlySpan<byte> pcId, ReadOnlySpan<byte> keyId, ReadOnlySpan<byte> x25519Pub, string name)
    {
        Span<byte> p = stackalloc byte[AuthSize];
        p.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(p, caps);
        pcId.CopyTo(p[4..]);
        keyId.CopyTo(p[(4 + IdSize)..]);
        x25519Pub.CopyTo(p[(4 + IdSize + 8)..]);
        PackName(name).CopyTo(p[(4 + IdSize + 8 + 32)..]);
        return Pack(TAuth, 0, p);
    }

    /// <summary>PAIR 报文：电脑编号、电脑的名字、pA。</summary>
    public static byte[] PackPair(ReadOnlySpan<byte> pcId, string name, ReadOnlySpan<byte> pA)
    {
        Span<byte> p = stackalloc byte[PairSize];
        pcId.CopyTo(p);
        PackName(name).CopyTo(p[IdSize..]);
        pA.CopyTo(p[(IdSize + NameSize)..]);
        return Pack(TPair, 0, p);
    }

    public static Ack ParseAck(ReadOnlySpan<byte> p)
    {
        if (p.Length != AckSize) throw new ProtocolException($"ACK 载荷 {p.Length} 字节，应为 {AckSize}");
        return new Ack(BinaryPrimitives.ReadUInt32LittleEndian(p), BinaryPrimitives.ReadUInt32LittleEndian(p[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(p[8..]), BinaryPrimitives.ReadUInt32LittleEndian(p[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(p[16..]));
    }

    /// <summary>TOUCH 的载荷：arg 个触点（最多 5 个），每个 id(1) x(2) y(2)，坐标在横屏画面之内。不符合的算对端不守协议，拿到的触点可以直接用。</summary>
    public static TouchPoint[] ParseTouch(ushort count, ReadOnlySpan<byte> p)
    {
        if (count > MaxTouchPoints) throw new ProtocolException($"TOUCH 写着 {count} 个触点，最多 {MaxTouchPoints} 个");
        if (p.Length != count * TouchPointSize) throw new ProtocolException($"TOUCH 写着 {count} 个触点，载荷却是 {p.Length} 字节");
        var pts = new TouchPoint[count];
        for (int i = 0; i < count; i++)
        {
            var q = p[(i * TouchPointSize)..];
            pts[i] = new TouchPoint(q[0], BinaryPrimitives.ReadUInt16LittleEndian(q[1..]), BinaryPrimitives.ReadUInt16LittleEndian(q[3..]));
            if (pts[i].X >= ViewW || pts[i].Y >= ViewH) throw new ProtocolException($"TOUCH 的触点 ({pts[i].X},{pts[i].Y}) 在画面之外");
        }
        return pts;
    }

    public static MouseEvent ParseMouse(ReadOnlySpan<byte> p)
    {
        if (p.Length != MouseSize) throw new ProtocolException($"MOUSE 载荷 {p.Length} 字节，应为 {MouseSize}");
        return new MouseEvent(BinaryPrimitives.ReadInt16LittleEndian(p), BinaryPrimitives.ReadInt16LittleEndian(p[2..]), (sbyte)p[4], p[5]);
    }
}

/// <summary>Tab5 连上后报的参数。Tab5Id 是它的编号（按它查配对），Name 是给人看的名字（已经清洗过，只当显示用）。</summary>
public sealed record Hello(int Version, int FbW, int FbH, int ViewW, int ViewH, int MaxStripBytes, int MaxFrameBytes,
                           uint Caps, byte[] Tab5Id, string Name);

/// <summary>Tab5 对一帧的确认。三个耗时是 Tab5 量的（微秒）：接收、解码、从收到 PRESENT 到换上屏幕。</summary>
public readonly record struct Ack(uint Seq, uint TMs, uint RecvUs, uint DecodeUs, uint PresentUs);

/// <summary>一个触点：编号 + 横屏画面里的像素坐标（x: 0..1279，y: 0..719）。</summary>
public readonly record struct TouchPoint(byte Id, ushort X, ushort Y);

/// <summary>接在 Tab5 上的鼠标：相对位移、滚轮、按键（bit0 左、bit1 右、bit2 中）。</summary>
public readonly record struct MouseEvent(short Dx, short Dy, sbyte Wheel, byte Buttons);

/// <summary>对端发来的东西不符合协议。</summary>
public sealed class ProtocolException(string message) : Exception(message);

/// <summary>认证没过是哪一种。</summary>
public enum AuthFailure
{
    NotPaired,      // 没和这台 Tab5 配对过，现在又不能配对（没设密码，或者已经停止配对）
    Forgotten,      // Tab5 说它没有这台电脑的这把配对密钥（AUTH_UNKNOWN），现在又不能配对。电脑这边的配对不删
    KeyMismatch,    // Tab5 回了 AUTH_OK，可它的 READY 认证码对不上：它手里没有这把配对密钥
    WrongPassword,  // 配对时 Tab5 上输的密码一直不对，记满了次数，电脑停止配对
    Revoked,        // 握手期间人在电脑上改了配对密码、或者移除了配对：这次握手用的 / 配出来的密钥不作数，没有存
}

/// <summary>认证没过（见 AuthFailure）。连接已经关掉。</summary>
public sealed class AuthException(AuthFailure reason, string message) : Exception(message)
{
    public AuthFailure Reason { get; } = reason;
}

/// <summary>握手期间 Tab5 那边的人结束了（在密码框上点了取消，或者离开了「无线显示」页）。连接已经关掉。</summary>
public sealed class DeviceByeException() : Exception("Tab5 那边结束了连接（BYE）");

/// <summary>
/// 握手期间本端自己的代码出了错：配对存不进文件，或者 PairingGate.WrongCountChanged、配对进度的回调里抛了异常。
/// 不是连接断了、也不是对端的问题，所以单独一种（原来的异常哪怕正好是 IOException，也不会被当成「没连上」）；
/// 原来的异常在 InnerException 里。连接已经关掉。
/// </summary>
public sealed class LocalFaultException(Exception inner) : Exception(inner.Message, inner);
