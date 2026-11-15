namespace WinDisplay.Core;

/// <summary>
/// 一份数据（配对、密码）同一时刻只让一个进程动：占着数据目录里的一个锁文件，进程活着就一直占着，进程没了系统自己放掉。
/// 托盘程序只在启动时读一遍文件、之后只写不读，两个进程各写各的话后写的会把先写的盖掉（比如把刚设的新密码盖回旧的）。
/// 互斥量管不了这件事：它只在同一个登录会话里看得见，而数据是按用户放的（ssh 进去的、远程桌面的是另一个会话）。
/// </summary>
public static class DataDirLock
{
    public const string FileName = ".lock";

    /// <summary>
    /// 占住 dir（没有就建）。占到了返回要一直留着的那个东西（Dispose = 放掉）；别的进程正占着，等了 wait 还没放就返回 null。
    /// 目录建不了、锁文件建不了（没权限、磁盘坏了）抛 IOException / UnauthorizedAccessException。
    /// </summary>
    public static IDisposable? TryTake(string dir, TimeSpan wait)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, FileName);
        long deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (File.Exists(path))
            {
                // 文件在、却打不开：有人占着。多半一下就放（脚本正在用 --set-password 设密码、杀毒软件在扫这个刚建的文件）：等一会儿再试
                if (Environment.TickCount64 >= deadline) return null;
                Thread.Sleep(50);
            }
        }
    }
}
