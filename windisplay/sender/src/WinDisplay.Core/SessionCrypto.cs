using System.Buffers.Binary;
using System.Security.Cryptography;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace WinDisplay.Core;

/// <summary>每次连接的密钥怎么算（PROTOCOL.md「配对」末尾和「每次连接」）。算式改了先改 PROTOCOL.md；固件和 Python 工具各有一份同样的。</summary>
internal static class SessionCrypto
{
    public const int KeySize = 32, X25519Size = 32, KeyIdSize = 8, ConfirmSize = 32;

    /// <summary>配对密钥：两边配对之后各自存下的 32 字节。</summary>
    public static byte[] PairingKey(ReadOnlySpan<byte> ke)
    {
        var key = new byte[KeySize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ke, key, salt: default, "Tab5 WinDisplay v2 pairing key"u8);
        return key;
    }

    /// <summary>配对密钥的标识（AUTH 里带着）：两边存的是不是同一把，Tab5 靠它看出来。</summary>
    public static byte[] KeyId(ReadOnlySpan<byte> pairingKey) =>
        HMACSHA256.HashData(pairingKey, "Tab5 WinDisplay v2 key id"u8)[..KeyIdSize];

    /// <summary>一次性的 X25519 密钥对。每条连接重新取。</summary>
    public static (byte[] Priv, byte[] Pub) X25519KeyPair()
    {
        byte[] priv = RandomNumberGenerator.GetBytes(X25519Size), pub = new byte[X25519Size];
        X25519.ScalarMultBase(priv, 0, pub, 0);
        return (priv, pub);
    }

    /// <summary>X25519 的共享值。全 0（对方给的是低阶点）抛 CryptoCheckException。</summary>
    public static byte[] X25519Shared(byte[] priv, ReadOnlySpan<byte> peerPub)
    {
        if (peerPub.Length != X25519Size) throw new CryptoCheckException($"X25519 公钥要 {X25519Size} 字节");
        var dh = new byte[X25519Size];
        if (!X25519.CalculateAgreement(priv, 0, peerPub.ToArray(), 0, dh, 0)) throw new CryptoCheckException("X25519 的共享值全 0");
        return dh;
    }

    /// <summary>会话密钥。th = SHA-256(HELLO ‖ AUTH ‖ AUTH_OK 三个报文在线上的字节)。</summary>
    public static (byte[] PcToTab5, byte[] Tab5ToPc) SessionKeys(ReadOnlySpan<byte> pairingKey, ReadOnlySpan<byte> dh, ReadOnlySpan<byte> th)
    {
        byte[] info = [.. "Tab5 WinDisplay v2 session"u8, .. th];
        Span<byte> okm = stackalloc byte[2 * KeySize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, dh, okm, salt: pairingKey, info);
        var keys = (okm[..KeySize].ToArray(), okm[KeySize..].ToArray());
        CryptographicOperations.ZeroMemory(okm);
        return keys;
    }
}

/// <summary>
/// 一个方向的逐报文加密（PROTOCOL.md「报文加密」）：ChaCha20-Poly1305，nonce = 4 字节 0 ‖ 这个方向的报文计数（u64 小端），
/// 附加数据 = 8 字节报文头。Seal / Open 各调一次就往前走一个报文，所以调用的先后必须就是报文在线上的先后。不是线程安全的。
/// </summary>
internal sealed class RecordCipher : IDisposable
{
    private readonly ChaCha20Poly1305 _aead;
    private ulong _count;

    /// <param name="count">下一个报文的计数；只有对测试向量时才给，平时从 0 起。</param>
    public RecordCipher(ReadOnlySpan<byte> key, ulong count = 0)
    {
        if (!ChaCha20Poly1305.IsSupported)
            throw new PlatformNotSupportedException("这台电脑的系统不支持 ChaCha20-Poly1305（Windows 要 10 的 20142 版以上，Linux 要 OpenSSL 1.1 以上）");
        _aead = new ChaCha20Poly1305(key);
        _count = count;
    }

    private void NextNonce(Span<byte> nonce)
    {
        nonce[..4].Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _count++);
    }

    /// <summary>加密一个报文的载荷。dst 要正好是 载荷长度 + 16：密文 ‖ 认证码。header 是 8 字节报文头（明文发，但算进认证码）。</summary>
    public void Seal(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload, Span<byte> dst)
    {
        Span<byte> nonce = stackalloc byte[12];
        NextNonce(nonce);
        _aead.Encrypt(nonce, payload, dst[..payload.Length], dst.Slice(payload.Length, Proto.TagSize), header);
    }

    /// <summary>解一个报文。data = 密文 ‖ 认证码，payload 要正好是密文那么长。认证码对不上返回 false（payload 里的内容不能用）。</summary>
    public bool TryOpen(ReadOnlySpan<byte> header, ReadOnlySpan<byte> data, Span<byte> payload)
    {
        Span<byte> nonce = stackalloc byte[12];
        NextNonce(nonce);
        try
        {
            _aead.Decrypt(nonce, data[..^Proto.TagSize], data[^Proto.TagSize..], payload, header);
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }
    }

    public void Dispose() => _aead.Dispose();
}
