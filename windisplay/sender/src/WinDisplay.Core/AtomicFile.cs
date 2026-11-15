namespace WinDisplay.Core;

/// <summary>整个重写一个小文件：先写临时文件再改名，写到一半断电不会把原来的弄坏。</summary>
internal static class AtomicFile
{
    // 改名换掉原文件时，原文件正被别的程序开着（Windows 上杀毒软件、索引服务常在文件刚写完时扫它）会失败：稍等再试，共这么多次
    private const int MoveAttempts = 6;
    private const int MoveFirstWaitMs = 10;     // 第一次等这么久，之后每次加倍（一共 0.3 秒左右）

    /// <summary>目录没有就建。Linux / macOS 上文件只许自己读写（0600）。写不成抛 IOException / UnauthorizedAccessException。</summary>
    public static void Write(string path, byte[] data)
    {
        string full = Path.GetFullPath(path);
        if (Path.GetDirectoryName(full) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        // 临时文件每次一个新名字：上一次写到一半留下的、别的进程正在写的，都不会被这一次接着写
        string tmp = $"{full}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var f = new FileStream(tmp, options))
            {
                f.Write(data);
                f.Flush(flushToDisk: true);
            }
            for (int attempt = 1, waitMs = MoveFirstWaitMs; ; attempt++, waitMs *= 2)
            {
                try
                {
                    File.Move(tmp, full, overwrite: true);
                    break;
                }
                catch (Exception e) when (attempt < MoveAttempts && e is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(waitMs);
                }
            }
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
