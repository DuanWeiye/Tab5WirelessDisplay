using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Win.Tests;

/// <summary>托盘程序存盘用的两样 Windows 专有的东西：数据保护（DPAPI）、开机启动。只在 Windows 上跑。</summary>
public sealed class DpapiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wddpapi-" + Guid.NewGuid().ToString("N"));

    public DpapiTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void 包起来再解开是原样_包出来的东西里看不到原文()
    {
        byte[] plain = Encoding.UTF8.GetBytes("{\"id\": \"0102\", \"peers\": {\"客厅的 Tab5\": {\"key\": \"00112233\"}}}");
        byte[] wrapped = Dpapi.Protect(plain);
        Assert.Equal(plain, Dpapi.Unprotect(wrapped));
        Assert.DoesNotContain("peers", Encoding.Latin1.GetString(wrapped));
        Assert.Empty(Dpapi.Unprotect(Dpapi.Protect([])));
    }

    [Fact]
    public void 改过一个字节_或者根本不是包出来的东西_解不开()
    {
        byte[] wrapped = Dpapi.Protect(Encoding.UTF8.GetBytes("配对密钥"));
        wrapped[^5] ^= 0x01;
        Assert.Throws<CryptographicException>(() => Dpapi.Unprotect(wrapped));
        Assert.Throws<CryptographicException>(() => Dpapi.Unprotect(Encoding.UTF8.GetBytes("{\"id\": \"0102\"}")));
    }

    [Fact]
    public void 配对存储和设置经它存盘_文件里看不到内容_重新打开读得回来_没经它的读不懂()
    {
        string pairs = Path.Combine(_dir, "pairs.bin"), hostFile = Path.Combine(_dir, "host.bin");
        byte[] tab5 = [.. Enumerable.Range(1, Proto.IdSize).Select(i => (byte)i)];
        byte[] key = [.. Enumerable.Range(100, SessionCrypto.KeySize).Select(i => (byte)i)];

        var store = new PairStore(pairs, Dpapi.Protect, Dpapi.Unprotect);
        store.Save(tab5, key, "客厅的 Tab5");
        var settings = new HostSettings(hostFile, Dpapi.Protect, Dpapi.Unprotect);
        settings.SaveSecret(PairingSecret.FromPassword("2468"));

        Assert.DoesNotContain("peers", Encoding.Latin1.GetString(File.ReadAllBytes(pairs)));
        Assert.DoesNotContain("w0", Encoding.Latin1.GetString(File.ReadAllBytes(hostFile)));

        var reopened = new PairStore(pairs, Dpapi.Protect, Dpapi.Unprotect);
        Assert.Equal(key, reopened.FindKey(tab5));
        Assert.Equal(["客厅的 Tab5"], reopened.Peers.Select(p => p.Name));
        Assert.True(new HostSettings(hostFile, Dpapi.Protect, Dpapi.Unprotect).CreateGate().CanPair);

        // 当成明文去读（比如拿 wdsend 的 --pair-store 指到这个文件上）：报读不懂，不悄悄当成空的
        Assert.Throws<InvalidDataException>(() => new PairStore(pairs));
        Assert.Throws<InvalidDataException>(() => new HostSettings(hostFile));
    }

    [Fact]
    public void 开机启动_开了查得到_程序挪了地方不算开着_再开一次改对_关了就没有()
    {
        string key = @"Software\WinDisplayTest\" + Guid.NewGuid().ToString("N");     // 不碰真的 Run 项
        const string name = "WinDisplayTray";
        string exe = @"C:\Program Files\WinDisplay\WinDisplayTray.exe", moved = @"D:\Apps\无线显示\WinDisplayTray.exe";
        try
        {
            Assert.False(AutoStart.IsEnabled(name, exe, key));
            AutoStart.Set(name, exe, enabled: false, key);                           // 本来就没有：关一次不出错
            AutoStart.Set(name, exe, enabled: true, key);
            Assert.True(AutoStart.IsEnabled(name, exe, key));
            Assert.True(AutoStart.IsEnabled(name, exe.ToUpperInvariant(), key));     // Windows 的路径不分大小写
            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(key)!)
                Assert.Equal("\"" + exe + "\"", run.GetValue(name));                 // 路径里有空格：要带引号

            Assert.False(AutoStart.IsEnabled(name, moved, key));
            AutoStart.Set(name, moved, enabled: true, key);
            Assert.True(AutoStart.IsEnabled(name, moved, key));
            Assert.False(AutoStart.IsEnabled(name, exe, key));

            AutoStart.Set(name, moved, enabled: false, key);
            Assert.False(AutoStart.IsEnabled(name, moved, key));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\WinDisplayTest", throwOnMissingSubKey: false);
        }
    }
}
