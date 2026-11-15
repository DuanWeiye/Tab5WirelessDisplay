using System.Text;
using System.Text.Json;

namespace WinDisplay.Core;

/// <summary>配对过的一台 Tab5（给界面列出来用，不带密钥）。</summary>
public sealed record PairedPeer(byte[] Id, string Name);

/// <summary>
/// 配对存储写不进文件（磁盘满、没有权限、protect 抛了异常）。这一次的改动不算，内存里退回到和文件一致。原来的异常在 InnerException 里。
/// 单独一种类型：调用方不用靠「是不是 IOException」去猜出错的是不是存配对（同一次调用里别处也可能抛 IOException）。
/// </summary>
public sealed class PairStoreException(Exception inner) : Exception(inner.Message, inner);

/// <summary>
/// 握手开头对那台 Tab5 取的记号（PairStore.Mark），走完存的时候交回来（PairStore.Save）——握手要等人在 Tab5 上输密码，
/// 一等可能一两分钟，这期间人在电脑上改了密码（ForgetAll）或者移除了这台 Tab5 的配对（Forget）的话，这次握手的结果不能再存进来。
/// 移除别的 Tab5 的配对不碍它的事。
/// </summary>
/// <param name="All">ForgetAll 做过几轮。</param>
/// <param name="Peer">这一轮里这台 Tab5 被 Forget 掉过几次。</param>
public readonly record struct ForgetMark(int All, int Peer);

/// <summary>
/// 电脑这边存的东西（PROTOCOL.md「两边各存什么」）：自己的编号，和每台配对过的 Tab5 的 { 编号 → 配对密钥、名字 }。
/// 给了文件就存在文件里，每次改动都整个重写（先写临时文件再改名，写到一半断电不会把原来的弄坏）；不给文件只放在内存里。
/// 文件是启动时读一遍、之后只写不读的：同一个文件不要让两个进程同时改（后写的会盖掉先写的那一条）。
/// 文件是 JSON，和 windisplay/tools 的 Python 工具用的是同一种，可以共用一份。里面是密钥：Linux / macOS 上只许自己读写（0600）；
/// Windows 上靠放在用户自己的目录里，托盘程序另外用 protect / unprotect 把内容交给 DPAPI 包一层。
/// 写不进文件（磁盘满、没有权限、protect 抛了异常）时抛 PairStoreException，这一次的改动不算：内存里退回到和文件一致。
/// 各个方法哪个线程都能调。
/// </summary>
public sealed class PairStore
{
    private readonly string? _path;
    private readonly Func<byte[], byte[]>? _protect, _unprotect;
    private readonly object _lock = new();
    private readonly Dictionary<string, (byte[] Key, string Name)> _peers = [];     // 键是 Tab5 编号的十六进制串
    // 见 ForgetMark：ForgetAll 做过几轮；这一轮里各台 Tab5 被 Forget 掉过几次（没被忘过的不在里面，键和 _peers 一样）
    private int _forgotAll;
    private readonly Dictionary<string, int> _forgot = [];

    /// <summary>只放在内存里的存储，编号随机取（id 只有测试才给）。</summary>
    public static PairStore InMemory(byte[]? id = null) => new(null, null, null, id);

    /// <param name="path">文件路径；文件还没有就新取一个编号并建出来（建不出来抛 PairStoreException）。
    /// 读不懂的文件抛 InvalidDataException（不悄悄当成空的：那会把配对全丢掉）。</param>
    /// <param name="protect">写文件之前对内容做的变换（比如 DPAPI 加密）；和 unprotect 要么都给、要么都不给。</param>
    public PairStore(string path, Func<byte[], byte[]>? protect = null, Func<byte[], byte[]>? unprotect = null)
        : this(path ?? throw new ArgumentNullException(nameof(path)), protect, unprotect, null)
    {
    }

    private PairStore(string? path, Func<byte[], byte[]>? protect, Func<byte[], byte[]>? unprotect, byte[]? id)
    {
        if ((protect is null) != (unprotect is null)) throw new ArgumentException("protect 和 unprotect 要么都给、要么都不给");
        (_path, _protect, _unprotect) = (path, protect, unprotect);
        if (id is not null && id.Length != Proto.IdSize) throw new ArgumentException($"编号要 {Proto.IdSize} 字节", nameof(id));
        if (path is not null && File.Exists(path))
        {
            Id = Load(path);
        }
        else
        {
            Id = id ?? System.Security.Cryptography.RandomNumberGenerator.GetBytes(Proto.IdSize);
            Persist();
        }
    }

    /// <summary>自己的编号（16 字节）：第一次用到时随机取，之后一直是它。</summary>
    public byte[] Id { get; }

    /// <summary>配对过的 Tab5。</summary>
    public IReadOnlyList<PairedPeer> Peers
    {
        get
        {
            lock (_lock) return [.. _peers.Select(p => new PairedPeer(Convert.FromHexString(p.Key), p.Value.Name))];
        }
    }

    /// <summary>这台 Tab5 的配对密钥（一份拷贝）；没配对过返回 null。</summary>
    public byte[]? FindKey(ReadOnlySpan<byte> peerId)
    {
        lock (_lock) return _peers.TryGetValue(Hex(peerId), out var p) ? (byte[])p.Key.Clone() : null;
    }

    /// <summary>对这台 Tab5 取一个记号（见 ForgetMark）：握手开头取，走完存的时候交给 Save。</summary>
    public ForgetMark Mark(ReadOnlySpan<byte> peerId)
    {
        lock (_lock) return MarkOf(Hex(peerId));
    }

    // 要拿着 _lock 调
    private ForgetMark MarkOf(string id) => new(_forgotAll, _forgot.GetValueOrDefault(id));

    /// <summary>
    /// 记下（或者换掉）这台 Tab5 的配对密钥和名字。和已有的一样就不动文件。
    /// 给了 since（握手开头对这台 Tab5 取的记号）而那之后它被 Forget 掉过、或者做过 ForgetAll：不存，返回 false。
    /// </summary>
    public bool Save(ReadOnlySpan<byte> peerId, ReadOnlySpan<byte> key, string name, ForgetMark? since = null)
    {
        if (peerId.Length != Proto.IdSize) throw new ArgumentException($"编号要 {Proto.IdSize} 字节", nameof(peerId));
        if (key.Length != SessionCrypto.KeySize) throw new ArgumentException($"配对密钥要 {SessionCrypto.KeySize} 字节", nameof(key));
        lock (_lock)
        {
            string id = Hex(peerId);
            if (since is { } mark && mark != MarkOf(id)) return false;
            bool had = _peers.TryGetValue(id, out var old);
            if (had && old.Name == name && key.SequenceEqual(old.Key)) return true;
            _peers[id] = (key.ToArray(), name);
            try
            {
                Persist();
            }
            catch
            {
                if (had) _peers[id] = old;
                else _peers.Remove(id);
                throw;
            }
            return true;
        }
    }

    /// <summary>忘掉一台 Tab5。</summary>
    public void Forget(ReadOnlySpan<byte> peerId)
    {
        lock (_lock)
        {
            string id = Hex(peerId);
            if (!_peers.Remove(id, out var old)) return;
            try
            {
                Persist();
            }
            catch
            {
                _peers[id] = old;
                throw;
            }
            _forgot[id] = _forgot.GetValueOrDefault(id) + 1;
        }
    }

    /// <summary>
    /// 忘掉所有配对（改了密码之后要做）。自己的编号不变。一条配对都没有时也算一轮：
    /// 正在用旧密码配对的握手，配出来的密钥不能在这之后存进来。
    /// </summary>
    public void ForgetAll()
    {
        lock (_lock)
        {
            if (_peers.Count > 0)
            {
                var old = new Dictionary<string, (byte[] Key, string Name)>(_peers);
                _peers.Clear();
                try
                {
                    Persist();
                }
                catch
                {
                    foreach (var (id, p) in old) _peers[id] = p;
                    throw;
                }
            }
            _forgotAll++;
            _forgot.Clear();        // 这一轮之前取的记号靠 _forgotAll 就对不上了
        }
    }

    private static string Hex(ReadOnlySpan<byte> id) => Convert.ToHexStringLower(id);

    private byte[] Load(string path)
    {
        try
        {
            byte[] raw = File.ReadAllBytes(path);
            using JsonDocument doc = JsonDocument.Parse(_unprotect is null ? raw : _unprotect(raw));
            byte[] id = Convert.FromHexString(doc.RootElement.GetProperty("id").GetString() ?? throw new FormatException("编号是 null"));
            if (id.Length != Proto.IdSize) throw new FormatException($"编号是 {id.Length} 字节");
            foreach (JsonProperty p in doc.RootElement.GetProperty("peers").EnumerateObject())
            {
                byte[] peer = Convert.FromHexString(p.Name);
                byte[] key = Convert.FromHexString(p.Value.GetProperty("key").GetString() ?? throw new FormatException("配对条目里的密钥是 null"));
                if (peer.Length != Proto.IdSize || key.Length != SessionCrypto.KeySize) throw new FormatException("配对条目里的编号或密钥长度不对");
                string name = p.Value.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
                _peers[Hex(peer)] = (key, name);
            }
            return id;
        }
        catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or NullReferenceException
                                      or System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidDataException($"配对存储 {path} 读不懂（{e.Message}）。确认不要了就删掉它，再重新配对", e);
        }
    }

    // 要拿着 _lock 调（构造函数里除外）
    private void Persist()
    {
        if (_path is null) return;
        try
        {
            Write(_path);
        }
        catch (Exception e)
        {
            throw new PairStoreException(e);
        }
    }

    private void Write(string path)
    {
        using var buf = new MemoryStream();
        using (var w = new Utf8JsonWriter(buf, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("id", Hex(Id));
            w.WriteStartObject("peers");
            foreach (var (id, p) in _peers)
            {
                w.WriteStartObject(id);
                w.WriteString("key", Hex(p.Key));
                w.WriteString("name", p.Name);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        byte[] data = buf.ToArray();
        if (_protect is not null) data = _protect(data);

        AtomicFile.Write(path, data);
    }
}
