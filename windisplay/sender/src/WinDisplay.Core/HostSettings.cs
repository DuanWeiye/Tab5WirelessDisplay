using System.Text.Json;

namespace WinDisplay.Core;

/// <summary>
/// 常驻的发送端（托盘程序）除了配对存储之外要存的两样（PROTOCOL.md「什么时候可以配对」）：配对密码算出来的 w0（不存密码本身）、
/// 密码不对累计的次数（程序重启不清零）。一个小 JSON 文件，每次改动整个重写；protect / unprotect 和 <see cref="PairStore"/> 的一样
/// （托盘程序交给 DPAPI）。文件是启动时读一遍、之后只写不读的。写不进文件抛 <see cref="PairStoreException"/>。各个方法哪个线程都能调。
/// </summary>
public sealed class HostSettings
{
    private readonly string _path;
    private readonly Func<byte[], byte[]>? _protect;
    private readonly object _lock = new();
    private byte[]? _w0;
    private int _wrong;

    /// <param name="path">文件路径；还没有这个文件 = 没设过密码。读不懂的文件抛 InvalidDataException（不悄悄当成没设过）。</param>
    public HostSettings(string path, Func<byte[], byte[]>? protect = null, Func<byte[], byte[]>? unprotect = null)
    {
        if ((protect is null) != (unprotect is null)) throw new ArgumentException("protect 和 unprotect 要么都给、要么都不给");
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _protect = protect;
        if (!File.Exists(path)) return;
        try
        {
            byte[] raw = File.ReadAllBytes(path);
            using JsonDocument doc = JsonDocument.Parse(unprotect is null ? raw : unprotect(raw));
            if (doc.RootElement.GetProperty("w0").GetString() is { } hex)
            {
                _w0 = Convert.FromHexString(hex);
                if (_w0.Length != PairingSecret.W0Size) throw new FormatException($"w0 是 {_w0.Length} 字节");
            }
            _wrong = doc.RootElement.GetProperty("wrong").GetInt32();
            if (_wrong < 0) throw new FormatException($"次数是 {_wrong}");
        }
        catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or InvalidOperationException
                                      or System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidDataException($"设置文件 {path} 读不懂（{e.Message}）。确认不要了就删掉它，再重新设密码", e);
        }
    }

    /// <summary>
    /// 照存着的内容建出「现在能不能配对」，并把它的次数变化接到存盘上（<see cref="PairingGate.WrongCountChanged"/> 的规矩：存盘要快，
    /// 这里只写一个几百字节的文件）。一份设置只建一次。
    /// </summary>
    public PairingGate CreateGate()
    {
        PairingGate gate;
        lock (_lock) gate = new PairingGate(_w0 is null ? null : PairingSecret.FromW0(_w0), _wrong);
        gate.WrongCountChanged += count => Save(w0 => w0, count);
        return gate;
    }

    /// <summary>
    /// 设了 / 改了密码：新密码连同清零的次数一次写进文件（新密码的次数从 0 数起）。分两次写的话，头一次写成、后一次没写成，
    /// 文件里就是旧密码带着清零的次数。在 <see cref="PairingGate.SetPassword"/> 的 commit 里调（那里拿着它的锁，次数这时不会变）。
    /// </summary>
    public void SaveSecret(PairingSecret secret)
    {
        byte[] w0 = (secret ?? throw new ArgumentNullException(nameof(secret))).W0;
        Save(_ => w0, 0);
    }

    private void Save(Func<byte[]?, byte[]?> w0, int wrong)
    {
        lock (_lock)
        {
            byte[]? next = w0(_w0);
            using var buf = new MemoryStream();
            using (var w = new Utf8JsonWriter(buf))
            {
                w.WriteStartObject();
                if (next is null) w.WriteNull("w0");
                else w.WriteString("w0", Convert.ToHexStringLower(next));
                w.WriteNumber("wrong", wrong);
                w.WriteEndObject();
            }
            byte[] data = buf.ToArray();
            try
            {
                AtomicFile.Write(_path, _protect is null ? data : _protect(data));
            }
            catch (Exception e)
            {
                throw new PairStoreException(e);
            }
            (_w0, _wrong) = (next, wrong);
        }
    }
}
