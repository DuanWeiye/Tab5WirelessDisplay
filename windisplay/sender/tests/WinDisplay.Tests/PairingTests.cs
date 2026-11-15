using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>现在能不能配对：设了密码就能，密码不对记满次数就停止、等人恢复（PROTOCOL.md「什么时候可以配对」）。</summary>
public class PairingGateTests
{
    private static readonly PairingSecret Secret = PairingSecret.FromPassword("2468");

    /// <summary>握手里的做法：发 PAIR 之前占一次机会，核对出密码不对。</summary>
    private static void Wrong(PairingGate gate, int times)
    {
        for (int i = 0; i < times; i++)
        {
            Assert.NotNull(gate.Begin());
            gate.WrongPassword();
        }
    }

    [Fact]
    public void 没设密码不能配对_设了就能_清掉又不能()
    {
        var gate = new PairingGate();
        Assert.False(gate.CanPair);
        Assert.False(gate.HasPassword);
        Assert.Null(gate.Begin());
        Assert.False(gate.Stopped);                     // 没设密码不叫「停止配对」：托盘上不该报错

        gate.SetPassword(Secret);
        Assert.True(gate.CanPair);
        Assert.True(gate.HasPassword);
        Assert.Same(Secret, gate.Begin());
        gate.End();

        gate.SetPassword(null);
        Assert.False(gate.CanPair);
        Assert.Null(gate.Begin());
    }

    [Fact]
    public void 密码不对九次以内照常_第十次起停止配对()
    {
        var gate = new PairingGate(Secret);

        for (int i = 0; i < 9; i++)
        {
            Wrong(gate, 1);
            Assert.True(gate.CanPair, $"第 {i + 1} 次不对之后就不能配对了");
            Assert.False(gate.Stopped);
        }
        Wrong(gate, 1);

        Assert.False(gate.CanPair);
        Assert.Null(gate.Begin());
        Assert.True(gate.Stopped);
        Assert.Equal(10, gate.WrongCount);
    }

    [Fact]
    public void 没有核对出密码不对的_不记次数_试多少回都一样()
    {
        // PAIR 发出去没等到 PAIR_REPLY（超时、断线）、Tab5 上的人点了取消、对面不守协议：对面没拿到能验证猜测的东西
        var gate = new PairingGate(Secret);

        for (int i = 0; i < 50; i++)
        {
            Assert.NotNull(gate.Begin());
            gate.End();
        }

        Assert.Equal(0, gate.WrongCount);
        Assert.True(gate.CanPair);
    }

    [Fact]
    public void 好几个握手同时配对_没有结果的也占着机会_加起来不超过上限()
    {
        var gate = new PairingGate(Secret, wrongCount: 7);

        // 还剩 3 次：三个握手各占一次，第四个拿不到——哪怕前三个都还没有结果
        Assert.NotNull(gate.Begin());
        Assert.NotNull(gate.Begin());
        Assert.NotNull(gate.Begin());
        Assert.Null(gate.Begin());
        Assert.False(gate.Stopped);                     // 这不叫停止配对：等那三个有了结果再说
        Assert.True(gate.CanPair);

        gate.End();                                     // 其中一个没等到回复：机会还回来
        Assert.NotNull(gate.Begin());
        Assert.Null(gate.Begin());

        gate.WrongPassword();
        gate.WrongPassword();
        gate.WrongPassword();
        Assert.Equal(10, gate.WrongCount);
        Assert.True(gate.Stopped);
        Assert.Null(gate.Begin());
    }

    [Fact]
    public void 很多个线程同时拿密码去试_一共只给出十次机会()
    {
        // 冒充 Tab5 的程序同时开很多条连接，每条都回一个不对的 PAIR_REPLY
        var gate = new PairingGate(Secret);
        int granted = 0;

        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
        {
            for (int i = 0; i < 20; i++)
            {
                if (gate.Begin() is null) continue;
                Interlocked.Increment(ref granted);
                Thread.Yield();                         // PAIR 在路上
                gate.WrongPassword();
            }
        });

        Assert.Equal(PairingGate.MaxWrong, granted);
        Assert.Equal(PairingGate.MaxWrong, gate.WrongCount);
        Assert.True(gate.Stopped);
    }

    [Fact]
    public void 人在电脑上恢复_次数清零_又可以配对()
    {
        var gate = new PairingGate(Secret);
        Wrong(gate, 10);

        gate.Resume();

        Assert.False(gate.Stopped);
        Wrong(gate, 9);
        Assert.True(gate.CanPair);                      // 从 0 重新数，不是恢复之后错一次又停
    }

    [Fact]
    public void 配对成功一次_次数清零()
    {
        var gate = new PairingGate(Secret);
        Wrong(gate, 7);

        Assert.NotNull(gate.Begin());                   // 这一次密码对上了，握手走完，密钥存下了
        gate.End();
        gate.Paired();

        Assert.Equal(0, gate.WrongCount);
        Wrong(gate, 9);
        Assert.True(gate.CanPair);                      // 之前的 7 次不算进来
    }

    [Fact]
    public void 改了密码_换成新的_停止了的也马上可以配对()
    {
        var gate = new PairingGate(Secret);
        Wrong(gate, 10);

        PairingSecret changed = PairingSecret.FromPassword("new password");
        gate.SetPassword(changed);

        Assert.Same(changed, gate.Begin());
        Assert.False(gate.Stopped);
        Assert.Equal(0, gate.WrongCount);
    }

    [Fact]
    public void PAIR发出去之后密码被清掉了_回来的密码不对_不记次数()
    {
        var gate = new PairingGate(Secret);
        var changes = new List<int>();
        gate.WrongCountChanged += changes.Add;
        Assert.NotNull(gate.Begin());

        gate.SetPassword(null);                         // 人在托盘里把密码清掉了
        gate.WrongPassword();

        Assert.Equal(0, gate.WrongCount);
        Assert.Empty(changes);
        Assert.False(gate.Stopped);
    }

    [Fact]
    public void 次数每变一次通知一次_托盘程序存下来_重启之后接着数()
    {
        // 不然冒充 Tab5 的程序等电脑每重启一次就又有 10 次机会
        int saved = 0;
        var changes = new List<int>();
        var gate = new PairingGate(Secret);
        gate.WrongCountChanged += n => { saved = n; changes.Add(n); };

        Wrong(gate, 6);
        Assert.Equal([1, 2, 3, 4, 5, 6], changes);

        var restarted = new PairingGate(Secret, saved);         // 托盘程序重启：密码和次数都是从盘上读回来的
        Assert.Equal(6, restarted.WrongCount);
        Wrong(restarted, 3);
        Assert.True(restarted.CanPair);
        Wrong(restarted, 1);
        Assert.True(restarted.Stopped);
    }

    [Fact]
    public void 停止配对的状态重启之后还在_清零也通知_没变化不通知()
    {
        var changes = new List<int>();
        var gate = new PairingGate(Secret, wrongCount: 10);     // 上次停止配对之后没人恢复过
        gate.WrongCountChanged += changes.Add;
        Assert.True(gate.Stopped);
        Assert.Null(gate.Begin());

        gate.Resume();
        gate.Resume();                                  // 已经是 0 了：不用再存一遍
        Wrong(gate, 1);
        gate.Paired();
        gate.SetPassword(Secret);

        Assert.Equal([0, 1, 0], changes);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PairingGate(Secret, wrongCount: -1));
    }

    [Fact]
    public void 存次数的处理函数抛了异常_次数照样记上了()
    {
        var gate = new PairingGate(Secret);
        gate.WrongCountChanged += _ => throw new IOException("磁盘满了");
        Assert.NotNull(gate.Begin());

        Assert.Throws<IOException>(gate.WrongPassword);

        Assert.Equal(1, gate.WrongCount);
        // 占着的那一次机会也还回去了：还剩 9 次，不是 8 次
        for (int i = 0; i < 9; i++) Assert.NotNull(gate.Begin());
        Assert.Null(gate.Begin());
    }

    [Fact]
    public void 恢复配对时清零的次数没存上_不算恢复_还停着_存得进去了再点才恢复()
    {
        // 内存里要是先清零了：界面上说恢复了，程序一重启读回来的又是停着的
        bool diskFull = true;
        var gate = new PairingGate(Secret, wrongCount: 10);
        gate.WrongCountChanged += _ =>
        {
            if (diskFull) throw new IOException("磁盘满了");
        };

        Assert.Throws<IOException>(gate.Resume);
        Assert.True(gate.Stopped);
        Assert.Equal(10, gate.WrongCount);
        Assert.Null(gate.Begin());

        diskFull = false;
        gate.Resume();
        Assert.True(gate.CanPair);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(PairingGate.MaxWrong)]      // 停止配对了的：改密码没改成，不能就这么恢复了
    public void 改密码时后面的步骤没成_密码和次数都退回原样_存得进去了再改才算(int wrongBefore)
    {
        var changes = new List<int>();
        var gate = new PairingGate(Secret, wrongBefore);
        gate.WrongCountChanged += changes.Add;
        PairingSecret changed = PairingSecret.FromPassword("new password");

        // 调用方的步骤（清配对、把新密码连同清零的次数存盘）做的时候，门里已经是新密码、次数 0
        Assert.Throws<IOException>(() => gate.SetPassword(changed, () =>
        {
            Assert.Equal(0, gate.WrongCount);
            throw new IOException("磁盘满了");
        }));
        Assert.Equal(wrongBefore, gate.WrongCount);
        Assert.Equal(wrongBefore >= PairingGate.MaxWrong, gate.Stopped);
        if (!gate.Stopped)
        {
            Assert.Same(Secret, gate.Begin());                  // 之后的配对用的还是原来的密码
            gate.End();
        }

        bool committed = false;
        gate.SetPassword(changed, () => committed = true);      // 存得进去了：换成新的，次数清零
        Assert.True(committed);
        Assert.Equal(0, gate.WrongCount);
        Assert.Same(changed, gate.Begin());
        gate.End();
        // 清零的次数不单独存：它和新密码在同一个文件里，是调用方一次存下的。分两次存的话，头一次存上、后一次没存上，
        // 留下来的就是旧密码带着清零的次数——没人点过恢复，旧密码却又有 10 次可以试
        Assert.Empty(changes);
    }

    [Fact]
    public void 改密码做到一半时别的握手拿不到密码也记不了次数_等改完了拿到的是新密码()
    {
        // 清配对、存新密码要是不在锁里做：这中间记上的一次「密码不对」会把「旧密码 + 次数 1」写进文件，没存上退回去时又对不上
        var gate = new PairingGate(Secret, wrongCount: 3);
        Assert.NotNull(gate.Begin());                           // 一条握手用旧密码发了 PAIR，Tab5 回来的是密码不对
        PairingSecret changed = PairingSecret.FromPassword("new password");
        PairingSecret? got = null;
        using var started = new CountdownEvent(2);
        var wrong = new Thread(() => { started.Signal(); gate.WrongPassword(); });
        var next = new Thread(() => { started.Signal(); got = gate.Begin(); });

        gate.SetPassword(changed, () =>
        {
            wrong.Start();
            next.Start();
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(wrong.Join(200) || next.Join(1), "改密码还没做完，别的握手就动了门里的东西");
        });

        Assert.True(wrong.Join(TimeSpan.FromSeconds(10)) && next.Join(TimeSpan.FromSeconds(10)));
        Assert.Same(changed, got);
        Assert.Equal(1, gate.WrongCount);                       // 那一次「不对」记在新密码的次数上（偏严），原来的 3 次不带过来
    }
}

/// <summary>配对存储：电脑自己的编号和配对过的 Tab5，存成和 Python 工具同一种 JSON 文件。</summary>
public sealed class PairStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wdpair-" + Guid.NewGuid().ToString("N"));

    public PairStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File1 => Path.Combine(_dir, "pairings.json");

    private static byte[] Id(byte first) => [first, .. new byte[Proto.IdSize - 1]];
    private static byte[] Key(byte fill) => [.. Enumerable.Repeat(fill, 32)];

    [Fact]
    public void 第一次用_取一个编号并建出文件_之后一直是它()
    {
        var first = new PairStore(File1);
        Assert.Equal(Proto.IdSize, first.Id.Length);
        Assert.True(File.Exists(File1));
        Assert.Empty(first.Peers);

        var again = new PairStore(File1);
        Assert.Equal(first.Id, again.Id);

        // 换一个文件就是另一台「电脑」：编号是随机取的
        Assert.NotEqual(first.Id, new PairStore(Path.Combine(_dir, "other.json")).Id);
    }

    [Fact]
    public void 存下的配对_重新打开还在_换密钥改名字忘掉都落到文件上()
    {
        var store = new PairStore(File1);
        store.Save(Id(1), Key(0x11), "客厅的 Tab5");
        store.Save(Id(2), Key(0x22), "书房的 Tab5");

        var reopened = new PairStore(File1);
        Assert.Equal(Key(0x11), reopened.FindKey(Id(1)));
        Assert.Equal(Key(0x22), reopened.FindKey(Id(2)));
        Assert.Null(reopened.FindKey(Id(3)));
        Assert.Equal(new HashSet<string> { "客厅的 Tab5", "书房的 Tab5" }, reopened.Peers.Select(p => p.Name).ToHashSet());

        reopened.Save(Id(1), Key(0x33), "客厅的 Tab5（新）");       // 重新配对：换掉原来那条
        reopened.Forget(Id(2));
        var third = new PairStore(File1);
        Assert.Equal(Key(0x33), third.FindKey(Id(1)));
        Assert.Null(third.FindKey(Id(2)));
        PairedPeer only = Assert.Single(third.Peers);
        Assert.Equal((Convert.ToHexString(Id(1)), "客厅的 Tab5（新）"), (Convert.ToHexString(only.Id), only.Name));

        third.ForgetAll();                                          // 改了密码：清掉所有配对，自己的编号不变
        var fourth = new PairStore(File1);
        Assert.Empty(fourth.Peers);
        Assert.Equal(store.Id, fourth.Id);
    }

    [Fact]
    public void 拿到的密钥是拷贝_改它不影响存着的()
    {
        PairStore store = PairStore.InMemory();
        store.Save(Id(1), Key(0x11), "Tab5");

        store.FindKey(Id(1))![0] ^= 0xFF;

        Assert.Equal(Key(0x11), store.FindKey(Id(1)));
    }

    [Fact]
    public void 文件格式和Python工具的一样_两边可以共用一份()
    {
        // wdproto.PairStore 写出来的样子（json.dump(..., ensure_ascii=False, indent=1)）
        string fromPython = """
            {
             "id": "000102030405060708090a0b0c0d0e0f",
             "peers": {
              "101112131415161718191a1b1c1d1e1f": {
               "key": "804d02488e07306415e346ae0a55e00ad6a03f626002117bece6431ed535621e",
               "name": "书房的 Tab5"
              }
             }
            }
            """;
        File.WriteAllText(File1, fromPython, new UTF8Encoding(false));

        var store = new PairStore(File1);
        Assert.Equal(Convert.FromHexString("000102030405060708090a0b0c0d0e0f"), store.Id);
        Assert.Equal(Convert.FromHexString("804d02488e07306415e346ae0a55e00ad6a03f626002117bece6431ed535621e"),
                     store.FindKey(Convert.FromHexString("101112131415161718191a1b1c1d1e1f")));
        Assert.Equal("书房的 Tab5", Assert.Single(store.Peers).Name);

        // 这边写出来的，Python 那边读的就是这几个字段：十六进制小写，名字不转义成 \uXXXX
        store.Save(Id(0xAB), Key(0xCD), "客厅的 Tab5");
        string written = File.ReadAllText(File1);
        using JsonDocument doc = JsonDocument.Parse(written);
        Assert.Equal("000102030405060708090a0b0c0d0e0f", doc.RootElement.GetProperty("id").GetString());
        JsonElement peer = doc.RootElement.GetProperty("peers").GetProperty("ab" + new string('0', 30));
        Assert.Equal(string.Concat(Enumerable.Repeat("cd", 32)), peer.GetProperty("key").GetString());
        Assert.Equal("客厅的 Tab5", peer.GetProperty("name").GetString());
        Assert.Contains("客厅的 Tab5", written);
        Assert.Equal(2, doc.RootElement.GetProperty("peers").EnumerateObject().Count());
    }

    [Fact]
    public void 文件里是密钥_只许自己读写_不留临时文件()
    {
        var store = new PairStore(File1);
        store.Save(Id(1), Key(0x11), "Tab5");
        store.Save(Id(2), Key(0x22), "Tab5");
        store.Forget(Id(2));

        Assert.Equal([File1], Directory.GetFiles(_dir));        // 每次都是「写临时文件再改名」，临时文件不留下
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(File1));
    }

    [Fact]
    public void 文件所在的目录还没有_自己建出来()
    {
        string nested = Path.Combine(_dir, "WinDisplay", "pairings.json");

        var store = new PairStore(nested);
        store.Save(Id(1), Key(0x11), "Tab5");

        Assert.Equal(Key(0x11), new PairStore(nested).FindKey(Id(1)));
    }

    [Theory]
    [InlineData("")]                                                            // 空文件（写到一半断电，要是没走临时文件就会这样）
    [InlineData("这不是 JSON")]
    [InlineData("[]")]
    [InlineData("""{"peers": {}}""")]                                           // 没有编号
    [InlineData("""{"id": "0001", "peers": {}}""")]                             // 编号不是 16 字节
    [InlineData("""{"id": "000102030405060708090a0b0c0d0e0f"}""")]              // 没有 peers
    [InlineData("""{"id": "000102030405060708090a0b0c0d0e0f", "peers": {"1011": {"key": "00", "name": "x"}}}""")]
    [InlineData("""{"id": "000102030405060708090a0b0c0d0e0f", "peers": {"101112131415161718191a1b1c1d1e1f": {"name": "x"}}}""")]
    [InlineData("""{"id": "zz0102030405060708090a0b0c0d0e0f", "peers": {}}""")]
    [InlineData("""{"id": null, "peers": {}}""")]
    [InlineData("""{"id": "000102030405060708090a0b0c0d0e0f", "peers": null}""")]
    [InlineData("""{"id": "000102030405060708090a0b0c0d0e0f", "peers": {"101112131415161718191a1b1c1d1e1f": {"key": null, "name": "x"}}}""")]
    public void 读不懂的文件_报错_不悄悄当成空的(string content)
    {
        File.WriteAllText(File1, content);

        var e = Assert.Throws<InvalidDataException>(() => new PairStore(File1));

        Assert.Contains("pairings.json", e.Message);
        Assert.Equal(content, File.ReadAllText(File1));         // 原文件不动：里面可能还有救得回来的东西
    }

    [Fact]
    public void 内容可以再包一层_比如交给DPAPI_文件里就看不到密钥()
    {
        // 托盘程序在 Windows 上把内容交给 DPAPI；这里拿 AES-GCM 顶替，看的是这两个钩子接得对不对
        byte[] wrap = RandomNumberGenerator.GetBytes(32);
        byte[] Protect(byte[] plain)
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], body = new byte[plain.Length];
            using var aes = new AesGcm(wrap, 16);
            aes.Encrypt(nonce, plain, body, tag);
            return [.. nonce, .. tag, .. body];
        }
        byte[] Unprotect(byte[] blob)
        {
            var plain = new byte[blob.Length - 28];
            using var aes = new AesGcm(wrap, 16);
            aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain);
            return plain;
        }

        var store = new PairStore(File1, Protect, Unprotect);
        store.Save(Id(1), Key(0x5A), "Tab5");

        byte[] onDisk = File.ReadAllBytes(File1);
        Assert.DoesNotContain(string.Concat(Enumerable.Repeat("5a", 32)), Encoding.Latin1.GetString(onDisk));
        Assert.Equal(Key(0x5A), new PairStore(File1, Protect, Unprotect).FindKey(Id(1)));
        // 文件被人动过（或者换了一个 Windows 账号，DPAPI 解不开）：报错，不当成空的
        onDisk[^1] ^= 1;
        File.WriteAllBytes(File1, onDisk);
        Assert.Throws<InvalidDataException>(() => new PairStore(File1, Protect, Unprotect));
        // 两个钩子要成对
        Assert.Throws<ArgumentException>(() => new PairStore(Path.Combine(_dir, "x.json"), Protect, null));
    }

    [Fact]
    public void 编号和密钥的长度不对_不收()
    {
        PairStore store = PairStore.InMemory();

        Assert.Throws<ArgumentException>(() => store.Save(new byte[15], Key(1), "x"));
        Assert.Throws<ArgumentException>(() => store.Save(Id(1), new byte[31], "x"));
        Assert.Throws<ArgumentException>(() => PairStore.InMemory(new byte[8]));
        Assert.Empty(store.Peers);
    }

    [Fact]
    public void 文件写不进去_报错_这一次的改动不算_内存里和文件一致()
    {
        var store = new PairStore(File1);
        store.Save(Id(1), Key(0x11), "客厅的 Tab5");
        ForgetMark mark = store.Mark(Id(1));
        // 文件的位置被一个目录占了：怎么写都换不掉它（Windows 和 Linux 上都是）
        File.Delete(File1);
        Directory.CreateDirectory(File1);

        static void Fails(Action change) =>
            Assert.True(Assert.Throws<PairStoreException>(change).InnerException is IOException or UnauthorizedAccessException);
        Fails(() => store.Save(Id(2), Key(0x22), "书房的 Tab5"));          // 新加一条
        Fails(() => store.Save(Id(1), Key(0x33), "客厅的 Tab5（新）"));    // 换掉原来那条
        Fails(() => store.Forget(Id(1)));
        Fails(store.ForgetAll);

        PairedPeer only = Assert.Single(store.Peers);
        Assert.Equal("客厅的 Tab5", only.Name);
        Assert.Equal(Key(0x11), store.FindKey(Id(1)));
        Assert.Null(store.FindKey(Id(2)));
        Assert.Equal(mark, store.Mark(Id(1)));                              // 没忘成的不算忘过
        Assert.Equal([File1], Directory.GetFileSystemEntries(_dir));        // 临时文件不留下

        // 位置让出来之后照常能存
        Directory.Delete(File1);
        store.Save(Id(2), Key(0x22), "书房的 Tab5");
        var reopened = new PairStore(File1);
        Assert.Equal(Key(0x11), reopened.FindKey(Id(1)));
        Assert.Equal(Key(0x22), reopened.FindKey(Id(2)));
    }

    [Fact]
    public void 存的时候文件正被别的程序开着_稍等一下再换_存得进去()
    {
        // Windows 上杀毒软件、索引服务常在文件刚写完时开着它扫一遍，这时换掉它会失败；Linux 上换得掉，这一条直接过
        var store = new PairStore(File1);
        store.Save(Id(1), Key(0x11), "客厅的 Tab5");
        var scanner = new FileStream(File1, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var release = new Timer(_ => scanner.Dispose(), null, 100, Timeout.Infinite);

        store.Save(Id(2), Key(0x22), "书房的 Tab5");

        Assert.Equal(Key(0x22), new PairStore(File1).FindKey(Id(2)));
        Assert.Equal([File1], Directory.GetFiles(_dir));
    }

    [Fact]
    public void 握手期间忘掉过配对_这次握手的结果存不进来()
    {
        // 握手要等人在 Tab5 上输密码，一等可能一两分钟；握手开头对这台 Tab5 取一个记号，走完存的时候带上
        PairStore store = PairStore.InMemory();
        store.Save(Id(1), Key(0x11), "客厅的 Tab5");
        store.Save(Id(2), Key(0x22), "书房的 Tab5");

        Assert.True(store.Save(Id(1), Key(0x11), "客厅的 Tab5（改了名）", store.Mark(Id(1))));     // 这期间没人动过：照常存
        Assert.True(store.Save(Id(3), Key(0x33), "新来的 Tab5", store.Mark(Id(3))));

        ForgetMark began = store.Mark(Id(2));
        store.Forget(Id(9));                            // 忘一台本来就没有的：什么都没变
        Assert.Equal(began, store.Mark(Id(2)));
        store.Forget(Id(2));                            // 人在托盘里移除了这一台
        Assert.False(store.Save(Id(2), Key(0x22), "书房的 Tab5", began));            // 刚移除的不会被写回去
        Assert.Null(store.FindKey(Id(2)));
        Assert.True(store.Save(Id(2), Key(0x2A), "书房的 Tab5（重新配的）", store.Mark(Id(2))));      // 移除之后才开始的握手照常存

        began = store.Mark(Id(4));
        store.ForgetAll();                              // 人改了密码
        Assert.False(store.Save(Id(4), Key(0x44), "用旧密码配的", began));
        Assert.Empty(store.Peers);

        began = store.Mark(Id(4));
        store.ForgetAll();                              // 一条配对都没有的时候改密码，也一样
        Assert.False(store.Save(Id(4), Key(0x44), "用旧密码配的", began));
        Assert.Empty(store.Peers);
        Assert.True(store.Save(Id(4), Key(0x44), "改完密码之后配的", store.Mark(Id(4))));
    }

    [Fact]
    public void 握手期间移除的是别的Tab5_这次握手的结果照常存()
    {
        // 家里两台 Tab5：正和书房那台配对（等人输密码）的时候，人在托盘里把客厅那台旧的移除了
        PairStore store = PairStore.InMemory();
        store.Save(Id(1), Key(0x11), "客厅的 Tab5");

        ForgetMark study = store.Mark(Id(2)), living = store.Mark(Id(1));
        store.Forget(Id(1));
        Assert.True(store.Save(Id(2), Key(0x22), "书房的 Tab5", study));
        Assert.Equal(Key(0x22), store.FindKey(Id(2)));
        Assert.False(store.Save(Id(1), Key(0x11), "客厅的 Tab5", living));           // 被移除的那一台自己的握手还是存不进来

        // 同一台被移除、重新配上、又被移除：第一次移除之前取的记号、两次移除之间取的记号都不作数了
        ForgetMark between = store.Mark(Id(1));
        Assert.True(store.Save(Id(1), Key(0x1A), "客厅的 Tab5", between));
        store.Forget(Id(1));
        Assert.False(store.Save(Id(1), Key(0x1B), "客厅的 Tab5", living));
        Assert.False(store.Save(Id(1), Key(0x1B), "客厅的 Tab5", between));
        Assert.Null(store.FindKey(Id(1)));
        Assert.Equal(Key(0x22), store.FindKey(Id(2)));
    }

    [Fact]
    public void 好几个线程同时存_一条都不丢_文件始终读得懂()
    {
        // 托盘程序里：收报文的线程在握手完时存，界面线程同时在「移除配对」
        var store = new PairStore(File1);
        Parallel.For(0, 32, i => store.Save(Id((byte)(i + 1)), Key((byte)i), $"Tab5-{i}"));

        var reopened = new PairStore(File1);
        Assert.Equal(32, reopened.Peers.Count);
        for (int i = 0; i < 32; i++) Assert.Equal(Key((byte)i), reopened.FindKey(Id((byte)(i + 1))));
    }
}
