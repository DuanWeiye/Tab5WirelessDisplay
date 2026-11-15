using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WinDisplay.Tray;

/// <summary>界面的颜色、字体和几样画法：深色，照着 Windows 11 深色模式下系统设置页的样子。</summary>
internal static class Theme
{
    public static readonly Color Window = Color.FromArgb(0x20, 0x20, 0x20);        // 窗口底色
    public static readonly Color Card = Color.FromArgb(0x2B, 0x2B, 0x2B);          // 一行的底色
    public static readonly Color CardHover = Color.FromArgb(0x34, 0x34, 0x34);
    public static readonly Color CardDown = Color.FromArgb(0x27, 0x27, 0x27);
    public static readonly Color Edge = Color.FromArgb(0x3A, 0x3A, 0x3A);          // 描边
    public static readonly Color Text = Color.White;
    public static readonly Color Dim = Color.FromArgb(0xC8, 0xC8, 0xC8);           // 次要的字（状态值、组标题）
    public static readonly Color Accent = Color.FromArgb(0x60, 0xCD, 0xFF);        // 强调色
    public static readonly Color Alert = Color.FromArgb(0xFF, 0x99, 0xA4);         // 错误色

    private const string Face = "Microsoft YaHei UI";

    /// <summary>按像素定大小的字体（logicalPx 是 96 DPI 下的像素数）：字的大小跟着窗口所在显示器的 DPI 走，和别的尺寸一个算法。</summary>
    public static Font Font(float logicalPx, int dpi) => new(Face, logicalPx * dpi / 96f, GraphicsUnit.Pixel);

    public const float RowPx = 13.5f, SmallPx = 12f;        // 行里的字、组标题的字

    /// <summary>任务栏现在是不是浅色的（系统「个性化 → 颜色」里的 Windows 模式）。读不出来当深色。</summary>
    public static bool TaskbarIsLight()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int light && light != 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>标题栏也用深色（DWMWA_USE_IMMERSIVE_DARK_MODE）。设不成就算了，标题栏是浅色的而已。</summary>
    public static void DarkTitleBar(IntPtr window)
    {
        int on = 1;
        _ = DwmSetWindowAttribute(window, 20, ref on, sizeof(int));
    }

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
