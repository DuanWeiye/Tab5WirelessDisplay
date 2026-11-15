using Microsoft.Win32;

namespace WinDisplay.Win;

/// <summary>
/// 开机启动（登录时自动起）：当前用户的注册表「Run」项里写一条「名字 = 带引号的程序路径」。不用管理员权限，只对当前用户有效。
/// 「开着」= 那一条在，而且指的就是这个程序现在的路径（程序挪了地方之后旧的那条不算，再开一次就改对了）。
/// </summary>
public static class AutoStart
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <param name="key">HKEY_CURRENT_USER 下的哪一项；只有测试才给别的。</param>
    public static bool IsEnabled(string name, string exePath, string key = RunKey)
    {
        using RegistryKey? run = Registry.CurrentUser.OpenSubKey(key);
        return run?.GetValue(name) is string value && string.Equals(value, Quote(exePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>开（写上 / 改成这个路径）或者关（删掉那一条；本来就没有也算成了）。</summary>
    public static void Set(string name, string exePath, bool enabled, string key = RunKey)
    {
        using RegistryKey run = Registry.CurrentUser.CreateSubKey(key, writable: true);
        if (enabled) run.SetValue(name, Quote(exePath), RegistryValueKind.String);
        else run.DeleteValue(name, throwOnMissingValue: false);
    }

    private static string Quote(string path) => $"\"{path}\"";
}
