using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.EC;
using Org.BouncyCastle.Math.EC;
using Org.BouncyCastle.Math.EC.Multiplier;
using BigInteger = Org.BouncyCastle.Math.BigInteger;
using ECCurve = Org.BouncyCastle.Math.EC.ECCurve;
using ECPoint = Org.BouncyCastle.Math.EC.ECPoint;

namespace WinDisplay.Core;

/// <summary>对端给的东西过不了密码学上的检查：点不在曲线上、共享值全 0 之类。握手里遇到它按「对端不守协议」处理。</summary>
internal sealed class CryptoCheckException(string message) : Exception(message);

/// <summary>P-256 上的点运算（BouncyCastle 的定制曲线实现）。只有配对（SPAKE2）用。</summary>
internal static class P256
{
    public const int PointSize = 65;        // 非压缩格式：04 ‖ X ‖ Y
    public const int ScalarSize = 32;

    private static readonly X9ECParameters Params = CustomNamedCurves.GetByName("secp256r1");
    public static readonly ECCurve Curve = Params.Curve;
    public static readonly BigInteger N = Params.N;         // 群的阶
    public static readonly ECPoint G = Params.G;
    // RFC 9382 给 P-256 定的两个点
    public static readonly ECPoint M = Curve.DecodePoint(Convert.FromHexString("02886e2f97ace46e55ba9dd7242579f2993b64e16ef3dcab95afd497333d8fa12f"));
    public static readonly ECPoint NPoint = Curve.DecodePoint(Convert.FromHexString("03d8bbd6c639c62937b04d997f38c3770719c629d7014d49a24b4f98baa1292b49"));

    private static readonly FixedPointCombMultiplier Comb = new();
    private static readonly LadderMultiplier Ladder = new();

    /// <summary>
    /// 蒙哥马利阶梯：从次高位起每一位都是一次点加、一次倍点。给它的标量最高位必须是 1、位数固定（见 MulSecret）。
    /// 只有第一步略有不同：起点 P 是刚解出来的点（Z = 1）、2P 不是，库里的点加和倍点对 Z = 1 另有一条路，
    /// 所以第一步的耗时可能跟着垫过之后的次高位变，最多漏出这 1 位。随机数每次配对都重取、只用这一次，不值得为它另写一套点运算。
    /// </summary>
    private sealed class LadderMultiplier : AbstractECMultiplier
    {
        protected override ECPoint MultiplyPositive(ECPoint p, BigInteger k)
        {
            ECPoint[] r = [p, p.Twice()];                   // 一直保持 r[1] = r[0] + P
            for (int i = k.BitLength - 2; i >= 0; i--)
            {
                int b = k.TestBit(i) ? 1 : 0;
                r[1 - b] = r[1 - b].Add(r[b]);
                r[b] = r[b].Twice();
            }
            return r[0];
        }
    }

    /// <summary>
    /// k·P，P 是固定的点（G、M、N），k 是秘密（0 ≤ k &lt; n）。定点梳状乘法：步数固定、查表时把整张表扫一遍，
    /// 不随 k 变——BouncyCastle 生成密钥、签名走的就是这条路。
    /// </summary>
    public static ECPoint MulFixed(ECPoint fixedPoint, BigInteger k) => Comb.Multiply(fixedPoint, k);

    /// <summary>
    /// k·P，P 是对端给的点，k 是秘密（1 ≤ k &lt; n）。BouncyCastle 默认的乘法（窗口法）做多少次点加随 k 变，这里不用它：
    /// 先把 k 加上一两个 n 垫成固定的 257 位（P 的阶是 n，结果不变），再走蒙哥马利阶梯——每一位都是一次点加、一次倍点。
    /// 算完之后 AbstractECMultiplier 会核对结果在曲线上。
    /// </summary>
    public static ECPoint MulSecret(ECPoint p, BigInteger k)
    {
        BigInteger padded = k.Add(N);
        if (padded.BitLength <= N.BitLength) padded = padded.Add(N);
        return Ladder.Multiply(p, padded);
    }

    /// <summary>65 字节的非压缩格式 → 点。长度不对、不是 04 开头、坐标超出范围、不在曲线上都抛 CryptoCheckException。</summary>
    public static ECPoint Decode(ReadOnlySpan<byte> raw)
    {
        // DecodePoint 自己也认压缩格式和无穷远点的编码，协议里不许，先挡掉
        if (raw.Length != PointSize || raw[0] != 0x04) throw new CryptoCheckException("点的编码不对（要 65 字节、04 开头）");
        try
        {
            return Curve.DecodePoint(raw.ToArray());
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            throw new CryptoCheckException("点不在曲线上");
        }
    }

    /// <summary>点 → 65 字节的非压缩格式。无穷远点没有这种编码，调用方要先排除。</summary>
    public static byte[] Encode(ECPoint p)
    {
        if (p.IsInfinity) throw new InvalidOperationException("无穷远点没有非压缩格式的编码");
        return p.GetEncoded(false);
    }

    /// <summary>随机取一个标量，1 ≤ k &lt; n，均匀。</summary>
    public static BigInteger RandomScalar()
    {
        Span<byte> raw = stackalloc byte[ScalarSize];
        while (true)
        {
            RandomNumberGenerator.Fill(raw);
            var k = new BigInteger(1, raw.ToArray());
            if (k.SignValue > 0 && k.CompareTo(N) < 0)      // 不合格的概率约 2^-32，重取
            {
                CryptographicOperations.ZeroMemory(raw);
                return k;
            }
        }
    }

    public static byte[] ScalarBytes(BigInteger k) => Org.BouncyCastle.Utilities.BigIntegers.AsUnsignedByteArray(ScalarSize, k);
}

/// <summary>
/// 由配对密码算出来的东西（PROTOCOL.md「配对」）。电脑存 W0 就行（托盘程序用 DPAPI 包起来存），不用存密码本身；
/// w·M、w·N 在这里算好，之后配对时不再拿 w 做乘法。
/// </summary>
public sealed class PairingSecret
{
    public const int W0Size = 40;
    private const int Pbkdf2Rounds = 10000;
    private static ReadOnlySpan<byte> Pbkdf2Salt => "Tab5 WinDisplay v2 password"u8;

    private readonly byte[] _w0;

    private PairingSecret(byte[] w0, BigInteger w)
    {
        _w0 = w0;
        W = P256.ScalarBytes(w);
        WM = P256.MulFixed(P256.M, w);
        WN = P256.MulFixed(P256.NPoint, w);
    }

    /// <summary>w（32 字节大端）。</summary>
    internal byte[] W { get; }
    internal ECPoint WM { get; }
    internal ECPoint WN { get; }

    /// <summary>存起来用的 40 字节（每次给一份新的拷贝）。</summary>
    public byte[] W0 => (byte[])_w0.Clone();

    /// <summary>配对密码最多这么多个字符：Tab5 的输入框只收这么多。</summary>
    public const int MaxPasswordLength = 64;

    /// <summary>
    /// 密码能不能用：1~64 个字符，而且只有 ASCII 的可打印字符（0x20~0x7E）——Tab5 上只打得出这些、输入框最多收 64 个，
    /// 不合规定的密码在 Tab5 上永远输不对。
    /// </summary>
    public static bool IsValidPassword(string password) =>
        password.Length is >= 1 and <= MaxPasswordLength && password.All(c => c is >= ' ' and <= '~');

    /// <summary>密码不合规定（见 IsValidPassword）抛 ArgumentException。</summary>
    public static PairingSecret FromPassword(string password)
    {
        if (!IsValidPassword(password))
            throw new ArgumentException("配对密码要 1~64 个字符，只能用英文字母、数字、空格和键盘上的英文符号", nameof(password));
        return FromW0(Rfc2898DeriveBytes.Pbkdf2(Encoding.ASCII.GetBytes(password), Pbkdf2Salt, Pbkdf2Rounds, HashAlgorithmName.SHA256, W0Size));
    }

    public static PairingSecret FromW0(ReadOnlySpan<byte> w0)
    {
        if (w0.Length != W0Size) throw new ArgumentException($"w0 要 {W0Size} 字节，给的是 {w0.Length}", nameof(w0));
        byte[] copy = w0.ToArray();
        return new PairingSecret(copy, new BigInteger(1, copy).Mod(P256.N));
    }

    /// <summary>直接给 w（对 RFC 9382 的测试向量用：那里给的是 w，不是密码）。</summary>
    internal static PairingSecret FromW(BigInteger w) => new(new byte[W0Size], w);
}

/// <summary>
/// 现在能不能配对（PROTOCOL.md「什么时候可以配对」）：电脑上设了密码就随时可以配对。但对面要是个冒充 Tab5 的程序，每配对一次就能试一个密码，
/// 所以给「密码不对」记数——记满 MaxWrong 次就停止配对，直到人在电脑上恢复（Resume）或者重新设密码；配对成功一次清零。不会过一阵自己恢复。
/// 托盘程序一直留着同一个对象（次数要跨连接、跨 Tab5 累计），并把次数存盘：在 WrongCountChanged 里存，启动时经构造函数给回来，
/// 程序重启不清零。命令行工具给了密码就建一个，不存。各个方法哪个线程都能调，好几个握手同时用同一个对象也行：
/// 发出去还没有结果的 PAIR 也占着机会，加起来不会超过 MaxWrong。
/// </summary>
public sealed class PairingGate
{
    /// <summary>密码不对记满这么多次，停止配对。</summary>
    public const int MaxWrong = 10;

    private readonly object _lock = new();
    private PairingSecret? _secret;
    private int _wrong;
    private int _pending;       // 发出去了、还没有结果的 PAIR 有几个（见 Begin）

    /// <param name="secret">配对密码算出来的数据；不给 = 还没设密码，不能配对。</param>
    /// <param name="wrongCount">上次存下的次数（程序重启之后接着数）。</param>
    public PairingGate(PairingSecret? secret = null, int wrongCount = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(wrongCount);
        _secret = secret;
        _wrong = wrongCount;
    }

    /// <summary>
    /// 次数变了，参数是新的次数；托盘程序在这里存盘。按变化的先后一个一个调（在锁里），所以存下的总是最新的。
    /// 处理函数里**不许等别的线程、不许做耗时的事**：握手正停在这里，Tab5 那边只等 5 秒（见 DisplaySession.Pairing）。
    /// 处理函数抛异常（没存上）：往上记的次数照样记上了，清零的不算；握手按本端出错结束（DisplaySession.Run 把这个异常抛出去）。
    /// </summary>
    public event Action<int>? WrongCountChanged;

    /// <summary>
    /// 设了 / 改了密码（给 null = 密码清掉了，不能配对）。次数清零。
    /// 「改密码 = 清掉所有配对」要跟着做的两步放在 commit 里交进来：PairStore.ForgetAll()，再把新密码连同清零的次数存盘
    /// （HostSettings.SaveSecret，一次写进文件）。commit 是换了密码之后、拿着锁调的，这期间别的握手拿不到密码、也记不了次数：
    /// 之前拿到旧密码的握手，记号都是 ForgetAll 之前取的，配出来的密钥存不进去（AuthFailure.Revoked）；之后拿到的都是新密码。
    /// 取记号在 ForgetAll 之前、拿密码在这之后的那一次，用新密码配成了也会被判 Revoked，要在 Tab5 上再输一次密码——只有一瞬间的窗口，代价也只是多输一次。
    /// 结束正连着的连接（取消它的 DisplaySession.Run）由调用方在这之后做。
    /// <para>
    /// commit 抛了异常（没存上）时这一次不算：密码和次数都退回原样，异常抛出去。清零的次数不报 WrongCountChanged：
    /// 它和新密码写的是同一个文件，分两次存的话，头一次存上、后一次没存上，留下来的就是旧密码带着清零的次数。
    /// commit 里**不许等别的线程**（和 WrongCountChanged 的处理函数一样，握手可能正等着这把锁）。
    /// </para>
    /// </summary>
    public void SetPassword(PairingSecret? secret, Action? commit = null)
    {
        lock (_lock)
        {
            (PairingSecret? secretBefore, int wrongBefore) = (_secret, _wrong);
            // _pending 不清：用旧密码发出去、还没有结果的 PAIR 照旧占着机会，结果是「不对」的话记在新密码的次数上。
            // 偏严（新密码可能因此少几次机会），但不会让加起来试的密码超过 MaxWrong 个
            (_secret, _wrong) = (secret, 0);
            try
            {
                commit?.Invoke();
            }
            catch
            {
                (_secret, _wrong) = (secretBefore, wrongBefore);
                throw;
            }
        }
    }

    /// <summary>人在电脑上点了恢复配对：次数清零。清零没存上（WrongCountChanged 的处理函数抛了异常）时不算恢复，异常抛出去。</summary>
    public void Resume()
    {
        lock (_lock) SetWrong(0);
    }

    /// <summary>设了配对密码。</summary>
    public bool HasPassword
    {
        get
        {
            lock (_lock) return _secret is not null;
        }
    }

    /// <summary>现在可以配对：设了密码，而且没停止配对。</summary>
    public bool CanPair
    {
        get
        {
            lock (_lock) return _secret is not null && _wrong < MaxWrong;
        }
    }

    /// <summary>密码不对记满了次数，停止配对了，等人恢复（托盘上要显示出来）。</summary>
    public bool Stopped
    {
        get
        {
            lock (_lock) return _secret is not null && _wrong >= MaxWrong;
        }
    }

    /// <summary>上次清零以来密码不对了几次。</summary>
    public int WrongCount
    {
        get
        {
            lock (_lock) return _wrong;
        }
    }

    /// <summary>
    /// 要发一个 PAIR 了：现在可以配对就占下一次机会、给出配对用的密码数据；不可以（没设密码、已经停止配对、
    /// 剩下的机会都被还没有结果的 PAIR 占着）给 null。先占再发，好几个握手同时配对时加起来也试不出 MaxWrong 个以上的密码。
    /// 拿到了的，之后必须正好调一次 WrongPassword（核对出密码不对）或者 End（别的结局）。
    /// </summary>
    internal PairingSecret? Begin()
    {
        lock (_lock)
        {
            if (_secret is null || _wrong + _pending >= MaxWrong) return null;
            _pending++;
            return _secret;
        }
    }

    /// <summary>Begin 占下的那一次：核对出 Tab5 上输的密码不对。记一次。</summary>
    internal void WrongPassword()
    {
        lock (_lock)
        {
            if (_pending > 0) _pending--;
            if (_secret is not null && _wrong < int.MaxValue) SetWrong(_wrong + 1);
        }
    }

    /// <summary>Begin 占下的那一次有了别的结局（密码对上了、对面没回、连接断了、对面不守协议）：不记，把机会还回去。</summary>
    internal void End()
    {
        lock (_lock)
        {
            if (_pending > 0) _pending--;
        }
    }

    /// <summary>配对成功（配出来的密钥已经存下）：次数清零。</summary>
    internal void Paired()
    {
        lock (_lock) SetWrong(0);
    }

    private void SetWrong(int value)
    {
        if (_wrong == value) return;
        int before = _wrong;
        _wrong = value;
        try
        {
            WrongCountChanged?.Invoke(value);
        }
        catch
        {
            // 没存上：内存里留偏严的那个。往上记的照样记上（不然存不进去的时候可以一直试）；清零的不算（文件里还是原来的次数）
            _wrong = Math.Max(before, value);
            throw;
        }
    }
}

/// <summary>
/// SPAKE2（RFC 9382，P-256 / SHA-256 / HKDF / HMAC）的一方。电脑是 A；B 是 Tab5 那一侧，这里也有是为了测试里的假 Tab5。
/// 建出来就有 Pub（自己要发的 pA / pB）；收到对方的之后调 Finish。每个对象只用一次：随机数不能重复使用。
/// </summary>
internal sealed class Spake2
{
    public enum Role { A, B }

    private readonly Role _role;
    private readonly PairingSecret _secret;
    private readonly BigInteger _k;

    /// <param name="scalar">自己的随机数；只有对测试向量时才给，平时不给（随机取）。</param>
    public Spake2(Role role, PairingSecret secret, BigInteger? scalar = null)
    {
        _role = role;
        _secret = secret;
        _k = scalar ?? P256.RandomScalar();
        Pub = P256.Encode(P256.MulFixed(P256.G, _k).Add(role == Role.A ? secret.WM : secret.WN));
    }

    /// <summary>pA（A）/ pB（B），65 字节。</summary>
    public byte[] Pub { get; }

    /// <summary>
    /// 返回 Ke 和两个校验值。对方给的点不合格、或者算出来的 K 是无穷远点，抛 CryptoCheckException。
    /// idA / idB：A、B 两方的编号；aad：握手里要一起确认的内容的摘要（没有就给空的）。
    /// </summary>
    public (byte[] Ke, byte[] CA, byte[] CB) Finish(ReadOnlySpan<byte> peerPub, ReadOnlySpan<byte> idA, ReadOnlySpan<byte> idB, ReadOnlySpan<byte> aad)
    {
        ECPoint unmasked = P256.Decode(peerPub).Subtract(_role == Role.A ? _secret.WN : _secret.WM);
        if (unmasked.IsInfinity) throw new CryptoCheckException("K 是无穷远点");
        ECPoint k = P256.MulSecret(unmasked, _k);
        if (k.IsInfinity) throw new CryptoCheckException("K 是无穷远点");

        ReadOnlySpan<byte> pA = _role == Role.A ? Pub : peerPub, pB = _role == Role.A ? peerPub : Pub;
        byte[] kBytes = P256.Encode(k);
        // TT = 六项各带 8 字节小端的长度：A、B、pA、pB、K、w
        var tt = new byte[6 * 8 + idA.Length + idB.Length + pA.Length + pB.Length + kBytes.Length + _secret.W.Length];
        int pos = 0;
        void Put(ReadOnlySpan<byte> v, Span<byte> dst)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(dst[pos..], (ulong)v.Length);
            v.CopyTo(dst[(pos + 8)..]);
            pos += 8 + v.Length;
        }
        Put(idA, tt);
        Put(idB, tt);
        Put(pA, tt);
        Put(pB, tt);
        Put(kBytes, tt);
        Put(_secret.W, tt);

        byte[] digest = SHA256.HashData(tt);                    // Ke ‖ Ka，各 16 字节
        byte[] info = [.. "ConfirmationKeys"u8, .. aad];
        byte[] kc = new byte[32];                               // KcA ‖ KcB，各 16 字节
        HKDF.DeriveKey(HashAlgorithmName.SHA256, digest.AsSpan(16), kc, salt: default, info);
        byte[] cA = HMACSHA256.HashData(kc.AsSpan(0, 16), tt), cB = HMACSHA256.HashData(kc.AsSpan(16), tt);
        byte[] ke = digest[..16];
        // 用完就清掉的：K 的编码、TT（里面有 K 和 w）、Ke ‖ Ka、两把确认密钥。_k 和点 K 本身是库里的大整数对象，清不掉，等回收
        CryptographicOperations.ZeroMemory(kBytes);
        CryptographicOperations.ZeroMemory(tt);
        CryptographicOperations.ZeroMemory(digest);
        CryptographicOperations.ZeroMemory(kc);
        return (ke, cA, cB);
    }
}
