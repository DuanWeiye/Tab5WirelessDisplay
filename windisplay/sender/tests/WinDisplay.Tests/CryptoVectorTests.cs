using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Math.EC.Rfc7748;
using WinDisplay.Core;
using Xunit;
using BigInteger = Org.BouncyCastle.Math.BigInteger;
using ECPoint = Org.BouncyCastle.Math.EC.ECPoint;

namespace WinDisplay.Tests;

/// <summary>
/// 配对、会话密钥、报文加密的算式，对 windisplay/testvectors/v2.json（固件、Python 工具对的是同一份；RFC 9382 那四组抄自 RFC 原文）。
/// 报文是逐字节比的：拼出来的 PAIR / AUTH 和向量差一个字节，两边算的摘要就不一样，真机上表现为「密码明明对却配不上」。
/// </summary>
public class CryptoVectorTests
{
    private static readonly JsonElement V = Load();

    private static JsonElement Load()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testvectors", "v2.json")));
        return doc.RootElement.Clone();
    }

    private static byte[] Hex(JsonElement e, string name) => Convert.FromHexString(e.GetProperty(name).GetString()!);
    private static BigInteger Big(JsonElement e, string name) => new(1, Hex(e, name));
    private static string Text(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private static byte[] X25519Pub(byte[] priv)
    {
        var pub = new byte[32];
        X25519.ScalarMultBase(priv, 0, pub, 0);
        return pub;
    }

    [Fact]
    public void SPAKE2_RFC9382附录B的四组向量_两方算出的都对得上()
    {
        int n = 0;
        foreach (JsonElement t in V.GetProperty("spake2_rfc9382").EnumerateArray())
        {
            byte[] idA = Encoding.ASCII.GetBytes(Text(t, "A")), idB = Encoding.ASCII.GetBytes(Text(t, "B"));
            PairingSecret secret = PairingSecret.FromW(Big(t, "w"));
            var a = new Spake2(Spake2.Role.A, secret, Big(t, "x"));
            var b = new Spake2(Spake2.Role.B, secret, Big(t, "y"));

            Assert.Equal(Hex(t, "pA"), a.Pub);
            Assert.Equal(Hex(t, "pB"), b.Pub);
            foreach (var (ke, cA, cB) in new[] { a.Finish(b.Pub, idA, idB, []), b.Finish(a.Pub, idA, idB, []) })
            {
                Assert.Equal(Hex(t, "Ke"), ke);
                Assert.Equal(Hex(t, "cA"), cA);
                Assert.Equal(Hex(t, "cB"), cB);
            }
            n++;
        }
        Assert.Equal(4, n);
    }

    [Fact]
    public void 密码算出的w0和w_对得上向量()
    {
        int n = 0;
        foreach (JsonElement t in V.GetProperty("password").EnumerateArray())
        {
            PairingSecret secret = PairingSecret.FromPassword(Text(t, "password"));

            Assert.Equal(Hex(t, "w0"), secret.W0);
            Assert.Equal(Hex(t, "w"), secret.W);
            // 托盘程序存的是 w0：从它恢复出来的和从密码算的是同一个
            Assert.Equal(secret.W, PairingSecret.FromW0(secret.W0).W);
            n++;
        }
        Assert.True(n >= 3);
    }

    [Fact]
    public void 配对_报文逐字节对得上_校验值和配对密钥对得上()
    {
        JsonElement t = V.GetProperty("pairing");
        byte[] idPc = Hex(t, "id_pc"), idTab5 = Hex(t, "id_tab5"), hello = Hex(t, "hello");
        PairingSecret secret = PairingSecret.FromPassword(Text(t, "password"));
        var pc = new Spake2(Spake2.Role.A, secret, Big(t, "x"));
        var tab5 = new Spake2(Spake2.Role.B, secret, Big(t, "y"));

        // HELLO 解得出向量里的 Tab5 编号和名字
        Hello h = Proto.ParseHello(Proto.Version, hello.AsSpan(Proto.HeaderSize));
        Assert.Equal(idTab5, h.Tab5Id);
        Assert.Equal("Tab5-TEST", h.Name);

        Assert.Equal(Hex(t, "w"), secret.W);
        Assert.Equal(Hex(t, "pA"), pc.Pub);
        Assert.Equal(Hex(t, "pB"), tab5.Pub);
        byte[] pair = Proto.PackPair(idPc, "电脑-TEST", pc.Pub);
        Assert.Equal(Hex(t, "pair"), pair);
        byte[] aad = SHA256.HashData([.. hello, .. pair]);
        Assert.Equal(Hex(t, "aad"), aad);

        var (ke, cA, cB) = pc.Finish(tab5.Pub, idPc, idTab5, aad);
        Assert.Equal(Hex(t, "Ke"), ke);
        Assert.Equal(Hex(t, "cA"), cA);
        Assert.Equal(Hex(t, "cB"), cB);
        Assert.Equal((ke, cA, cB), tab5.Finish(pc.Pub, idPc, idTab5, aad), TupleOfBytes.Instance);
        Assert.Equal(Hex(t, "pair_reply"), Proto.Pack(Proto.TPairReply, 0, [.. tab5.Pub, .. cB]));
        Assert.Equal(Hex(t, "pair_ok"), Proto.Pack(Proto.TPairOk, 0, cA));

        byte[] key = SessionCrypto.PairingKey(ke);
        Assert.Equal(Hex(t, "pairing_key"), key);
        Assert.Equal(Hex(t, "key_id"), SessionCrypto.KeyId(key));
    }

    private sealed class TupleOfBytes : IEqualityComparer<(byte[], byte[], byte[])>
    {
        public static readonly TupleOfBytes Instance = new();
        public bool Equals((byte[], byte[], byte[]) x, (byte[], byte[], byte[]) y) =>
            x.Item1.AsSpan().SequenceEqual(y.Item1) && x.Item2.AsSpan().SequenceEqual(y.Item2) && x.Item3.AsSpan().SequenceEqual(y.Item3);
        public int GetHashCode((byte[], byte[], byte[]) v) => v.Item1.Length;
    }

    [Fact]
    public void 配对_两边输的密码不一样_校验值对不上()
    {
        byte[] idPc = TestPairing.PcId, idTab5 = TestPairing.Tab5Id;
        var pc = new Spake2(Spake2.Role.A, PairingSecret.FromPassword("2468"));
        var tab5 = new Spake2(Spake2.Role.B, PairingSecret.FromPassword("8813"));

        var a = pc.Finish(tab5.Pub, idPc, idTab5, []);
        var b = tab5.Finish(pc.Pub, idPc, idTab5, []);

        Assert.NotEqual(a.Ke, b.Ke);
        Assert.NotEqual(a.CA, b.CA);
        Assert.NotEqual(a.CB, b.CB);
    }

    [Fact]
    public void 配对_握手里被一起确认的内容变了一个字节_校验值就变()
    {
        // 名字、编号、能力位这些都进了摘要：中途被人改过的话两边的校验值对不上
        PairingSecret secret = PairingSecret.FromPassword("2468");
        BigInteger x = P256.RandomScalar(), y = P256.RandomScalar();
        byte[] pB = new Spake2(Spake2.Role.B, secret, y).Pub;
        byte[] aad = SHA256.HashData("HELLO 和 PAIR 两个报文"u8), aad2 = SHA256.HashData("HELLO 和 PAIR 两个报文."u8);

        var same = new Spake2(Spake2.Role.A, secret, x).Finish(pB, TestPairing.PcId, TestPairing.Tab5Id, aad);
        var changed = new Spake2(Spake2.Role.A, secret, x).Finish(pB, TestPairing.PcId, TestPairing.Tab5Id, aad2);
        var otherId = new Spake2(Spake2.Role.A, secret, x).Finish(pB, TestPairing.Tab5Id, TestPairing.Tab5Id, aad);

        Assert.Equal(same.Ke, changed.Ke);              // K 没变，变的是校验值用的密钥
        Assert.NotEqual(same.CB, changed.CB);
        Assert.NotEqual(same.CA, changed.CA);
        Assert.NotEqual(same.Ke, otherId.Ke);           // 编号进的是 TT
    }

    [Fact]
    public void 会话密钥_AUTH报文逐字节对得上_两个方向的密钥对得上()
    {
        JsonElement t = V.GetProperty("session");
        byte[] key = Hex(t, "pairing_key"), privPc = Hex(t, "priv_pc"), privTab5 = Hex(t, "priv_tab5");
        byte[] idPc = Hex(V.GetProperty("pairing"), "id_pc");

        byte[] auth = Proto.PackAuth(0, idPc, SessionCrypto.KeyId(key), X25519Pub(privPc), "电脑-TEST");
        Assert.Equal(Hex(t, "auth"), auth);
        byte[] authOk = Proto.Pack(Proto.TAuthOk, 0, X25519Pub(privTab5));
        Assert.Equal(Hex(t, "auth_ok"), authOk);

        byte[] dh = SessionCrypto.X25519Shared(privPc, X25519Pub(privTab5));
        Assert.Equal(Hex(t, "dh"), dh);
        Assert.Equal(dh, SessionCrypto.X25519Shared(privTab5, X25519Pub(privPc)));
        byte[] th = SHA256.HashData([.. Hex(t, "hello"), .. auth, .. authOk]);
        Assert.Equal(Hex(t, "th"), th);

        var (pcToTab5, tab5ToPc) = SessionCrypto.SessionKeys(key, dh, th);
        Assert.Equal(Hex(t, "key_pc_to_tab5"), pcToTab5);
        Assert.Equal(Hex(t, "key_tab5_to_pc"), tab5ToPc);
    }

    [Fact]
    public void 一次性密钥对_每次都不一样_两边算出同一个共享值()
    {
        var (privA, pubA) = SessionCrypto.X25519KeyPair();
        var (privB, pubB) = SessionCrypto.X25519KeyPair();

        Assert.NotEqual(pubA, pubB);
        Assert.Equal(pubA, X25519Pub(privA));
        Assert.Equal(SessionCrypto.X25519Shared(privA, pubB), SessionCrypto.X25519Shared(privB, pubA));
    }

    [Fact]
    public void 报文加密_线上的字节对得上向量_解得回来_改一个比特就认证不过()
    {
        int n = 0;
        foreach (JsonElement t in V.GetProperty("records").EnumerateArray())
        {
            string what = Text(t, "what");
            byte[] key = Hex(t, "key"), header = Hex(t, "header"), payload = Hex(t, "payload"), wire = Hex(t, "wire");
            ulong count = t.GetProperty("count").GetUInt64();

            var sealedData = new byte[payload.Length + Proto.TagSize];
            using (var tx = new RecordCipher(key, count)) tx.Seal(header, payload, sealedData);
            Assert.True(wire.AsSpan().SequenceEqual([.. header, .. sealedData]), what);

            var opened = new byte[payload.Length];
            using (var rx = new RecordCipher(key, count)) Assert.True(rx.TryOpen(header, sealedData, opened), what);
            Assert.Equal(payload, opened);

            // 认证码、密文、报文头各改一个比特；报文计数差一个（丢了一个 / 重放）；别的密钥
            byte[] badTag = (byte[])sealedData.Clone();
            badTag[^1] ^= 0x01;
            byte[] badHeader = (byte[])header.Clone();
            badHeader[2] ^= 0x10;
            using (var rx = new RecordCipher(key, count)) Assert.False(rx.TryOpen(header, badTag, opened), what);
            using (var rx = new RecordCipher(key, count)) Assert.False(rx.TryOpen(badHeader, sealedData, opened), what);
            using (var rx = new RecordCipher(key, count + 1)) Assert.False(rx.TryOpen(header, sealedData, opened), what);
            using (var rx = new RecordCipher(RandomNumberGenerator.GetBytes(32), count)) Assert.False(rx.TryOpen(header, sealedData, opened), what);
            if (payload.Length > 0)
            {
                byte[] badBody = (byte[])sealedData.Clone();
                badBody[0] ^= 0x01;
                using var rx = new RecordCipher(key, count);
                Assert.False(rx.TryOpen(header, badBody, opened), what);
            }
            n++;
        }
        Assert.True(n >= 5);
    }

    [Fact]
    public void 报文加密_计数随报文往前走_同一个报文发两遍第二遍认证不过()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        using var tx = new RecordCipher(key);
        using var rx = new RecordCipher(key);
        var header = new byte[Proto.HeaderSize];
        Proto.WriteHeader(header, Proto.TKbdLeds, 2, 0);
        byte[] first = new byte[Proto.TagSize], second = new byte[Proto.TagSize];
        tx.Seal(header, [], first);
        tx.Seal(header, [], second);

        Assert.NotEqual(first, second);                     // 内容一样的两个报文，线上的字节不一样
        Assert.True(rx.TryOpen(header, first, []));
        Assert.False(rx.TryOpen(header, first, []));        // 重放
    }

    [Fact]
    public void 该拒的点_长度不对_不是04开头_不在曲线上_坐标超出范围_都拒掉()
    {
        var kept = new List<string>();
        int n = 0;
        foreach (JsonElement t in V.GetProperty("reject").GetProperty("points").EnumerateArray())
        {
            byte[] point = Hex(t, "point");
            try
            {
                P256.Decode(point);
                kept.Add(Text(t, "what"));
            }
            catch (CryptoCheckException)
            {
            }
            // 握手里是从 Finish 进来的：同样拒掉
            Assert.Throws<CryptoCheckException>(() =>
                new Spake2(Spake2.Role.A, PairingSecret.FromPassword("2468")).Finish(point, TestPairing.PcId, TestPairing.Tab5Id, []));
            n++;
        }
        Assert.Empty(kept);
        Assert.True(n >= 10);
    }

    [Fact]
    public void 对方给的点让K成了无穷远点_拒掉()
    {
        JsonElement t = V.GetProperty("reject").GetProperty("k_infinity");
        PairingSecret secret = PairingSecret.FromW(Big(t, "w"));

        var pc = Assert.Throws<CryptoCheckException>(() =>
            new Spake2(Spake2.Role.A, secret).Finish(Hex(t, "pB_for_pc"), TestPairing.PcId, TestPairing.Tab5Id, []));
        var tab5 = Assert.Throws<CryptoCheckException>(() =>
            new Spake2(Spake2.Role.B, secret).Finish(Hex(t, "pA_for_tab5"), TestPairing.PcId, TestPairing.Tab5Id, []));

        Assert.Contains("无穷远", pc.Message);
        Assert.Contains("无穷远", tab5.Message);
    }

    [Fact]
    public void X25519_对方给的是低阶公钥_共享值全0_拒掉()
    {
        var kept = new List<string>();
        int n = 0;
        foreach (JsonElement t in V.GetProperty("reject").GetProperty("x25519_pub").EnumerateArray())
        {
            var (priv, _) = SessionCrypto.X25519KeyPair();
            try
            {
                SessionCrypto.X25519Shared(priv, Hex(t, "pub"));
                kept.Add(Text(t, "what"));
            }
            catch (CryptoCheckException)
            {
            }
            n++;
        }
        Assert.Empty(kept);
        Assert.True(n >= 9);
    }

    [Fact]
    public void 带秘密标量的两种乘法_结果和库里默认的乘法一样()
    {
        // MulSecret（垫成 257 位 + 蒙哥马利阶梯）和 MulFixed（定点梳状）换的只是做法，结果得和默认的窗口法一模一样
        BigInteger n = P256.N;
        var scalars = new List<BigInteger> { BigInteger.One, BigInteger.Two, BigInteger.ValueOf(3), n.Subtract(BigInteger.One), n.Subtract(BigInteger.Two), n.ShiftRight(1) };
        for (int i = 0; i < 20; i++) scalars.Add(P256.RandomScalar());
        ECPoint other = P256.G.Multiply(P256.RandomScalar());       // 对端给的随便一个点
        foreach (BigInteger k in scalars)
        {
            foreach (ECPoint p in new[] { P256.G, P256.M, P256.NPoint, other })
            {
                ECPoint want = p.Multiply(k);
                Assert.True(want.Equals(P256.MulSecret(p, k)), $"MulSecret 和默认乘法不一样：k = {k.ToString(16)}");
                Assert.True(want.Equals(P256.MulFixed(p, k)), $"MulFixed 和默认乘法不一样：k = {k.ToString(16)}");
            }
        }
        // 垫出来的标量恒为 257 位：阶梯走的步数不随 k 变
        foreach (BigInteger k in scalars)
        {
            BigInteger padded = k.Add(n);
            if (padded.BitLength <= n.BitLength) padded = padded.Add(n);
            Assert.Equal(257, padded.BitLength);
        }
    }

    [Fact]
    public void 随机取的标量_在1到n减1之间_每次都不一样()
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < 200; i++)
        {
            BigInteger k = P256.RandomScalar();
            Assert.True(k.SignValue > 0 && k.CompareTo(P256.N) < 0);
            Assert.True(seen.Add(k.ToString(16)));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("密码")]
    [InlineData("a\tb")]
    [InlineData("café")]
    [InlineData("a\u007f")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345")]      // 65 个字符
    public void 配对密码_空的_带Tab5上打不出来的字符的_超过64个字符的_不收(string password)
    {
        Assert.False(PairingSecret.IsValidPassword(password));
        Assert.Throws<ArgumentException>(() => PairingSecret.FromPassword(password));
    }

    [Theory]
    [InlineData("1")]
    [InlineData(" ~")]
    [InlineData("correct horse battery staple")]
    [InlineData("P@ss-w0rd!#$%^&*()_+[]{}|;:',.<>/?`\"\\")]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901234")]       // 64 个字符
    public void 配对密码_空格到波浪号之间_1到64个字符的都可以(string password)
    {
        Assert.True(PairingSecret.IsValidPassword(password));
        Assert.Equal(PairingSecret.W0Size, PairingSecret.FromPassword(password).W0.Length);
    }
}
