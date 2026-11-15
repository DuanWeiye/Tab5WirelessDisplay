using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>
/// 数据目录的锁：同一份数据（配对、密码）同一时刻只让一个进程动。托盘程序一直占着；脚本用 --set-password 设密码时占一下就放。
/// </summary>
public sealed class DataDirLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wdlock-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void 头一次用_目录还没有_建出来并占住()
    {
        string dir = Path.Combine(_dir, "WinDisplay");
        using IDisposable? held = DataDirLock.TryTake(dir, TimeSpan.Zero);
        Assert.NotNull(held);
        Assert.True(File.Exists(Path.Combine(dir, DataDirLock.FileName)));
    }

    [Fact]
    public void 托盘程序占着的时候_来设密码的拿不到_托盘程序退出之后拿得到()
    {
        IDisposable? tray = DataDirLock.TryTake(_dir, TimeSpan.Zero);
        Assert.NotNull(tray);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(DataDirLock.TryTake(_dir, TimeSpan.FromMilliseconds(300)));
        Assert.InRange(watch.ElapsedMilliseconds, 250, 5000);       // 等够了说好的时间才放弃
        Assert.Null(DataDirLock.TryTake(_dir, TimeSpan.Zero));      // 不等的：马上回答

        tray.Dispose();
        using IDisposable? script = DataDirLock.TryTake(_dir, TimeSpan.Zero);
        Assert.NotNull(script);
    }

    [Fact]
    public void 脚本正在设密码_这时托盘程序启动_等它一下就拿到了()
    {
        IDisposable? script = DataDirLock.TryTake(_dir, TimeSpan.Zero);
        Assert.NotNull(script);
        var release = new Thread(() =>
        {
            Thread.Sleep(200);          // 设密码一下就完
            script.Dispose();
        });
        release.Start();

        using IDisposable? tray = DataDirLock.TryTake(_dir, TimeSpan.FromSeconds(10));
        Assert.NotNull(tray);
        release.Join();
    }

    [Fact]
    public void 占着锁不碍着读写这个目录里别的文件()
    {
        using IDisposable? held = DataDirLock.TryTake(_dir, TimeSpan.Zero);
        var settings = new HostSettings(Path.Combine(_dir, "host.bin"));
        settings.SaveSecret(PairingSecret.FromPassword("2468"));
        Assert.True(new HostSettings(Path.Combine(_dir, "host.bin")).CreateGate().HasPassword);
    }

    [Fact]
    public void 目录建不出来_报错_不当成有人占着()
    {
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "占着这个名字");
        File.WriteAllText(blocker, "");
        Assert.ThrowsAny<IOException>(() => DataDirLock.TryTake(Path.Combine(blocker, "WinDisplay"), TimeSpan.FromSeconds(5)));
    }
}
