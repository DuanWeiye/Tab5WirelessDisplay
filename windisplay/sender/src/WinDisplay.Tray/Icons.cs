using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinDisplay.Core;

namespace WinDisplay.Tray;

/// <summary>
/// 托盘图标：一个小显示器，现画的（不带图片文件）。没连着是空心的；连着的时候屏幕填上强调色；要人动手 / 出错时右下角多一个错误色的点。
/// 线条的颜色跟着任务栏：深色任务栏上是白的，浅色任务栏上是深的。
/// </summary>
internal static class Icons
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    /// <summary>
    /// 托盘图标现在该画多大（像素）：主显示器（托盘在它上面）现在的缩放比例下，小图标的尺寸（100% 是 16，125% 是 20）。
    /// 不用 SystemInformation.SmallIconSize：那个给的是进程启动那一刻的缩放比例下的，人改了缩放、没注销的话它不跟着变，
    /// 图标就还是旧尺寸、被系统拉伸得发糊。
    /// </summary>
    public static int TraySize()
    {
        const uint monitorDefaultToPrimary = 1;
        const int effectiveDpi = 0, smCxSmIcon = 49;
        IntPtr primary = MonitorFromPoint(Point.Empty, monitorDefaultToPrimary);        // (0, 0) 总在主显示器上
        uint dpi = GetDpiForMonitor(primary, effectiveDpi, out uint x, out _) == 0 ? x : 96;
        int size = GetSystemMetricsForDpi(smCxSmIcon, dpi);
        return size > 0 ? size : 16;
    }

    public static Icon Make(TrayIcon kind, int size, bool lightTaskbar)
    {
        using Bitmap bitmap = Draw(kind, size, lightTaskbar);
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();      // 拷一份自己的：handle 这里就还掉
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public static Bitmap Draw(TrayIcon kind, int size, bool lightTaskbar)
    {
        Color ink = lightTaskbar ? Color.FromArgb(0x1F, 0x1F, 0x1F) : Color.White;
        Color accent = lightTaskbar ? Color.FromArgb(0x00, 0x67, 0xC0) : Theme.Accent;
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float u = size / 16f, line = Math.Max(1f, 1.3f * u);
        var screen = new RectangleF(1.5f * u, 2.5f * u, 13f * u, 8.5f * u);
        using (GraphicsPath path = Theme.RoundRect(screen, 1.5f * u))
        {
            if (kind == TrayIcon.Connected)
            {
                using var fill = new SolidBrush(accent);
                g.FillPath(fill, path);
            }
            using var pen = new Pen(kind == TrayIcon.Connected ? accent : ink, line);
            g.DrawPath(pen, path);
        }
        using (var stand = new Pen(ink, line) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(stand, 8f * u, 11f * u, 8f * u, 13.2f * u);
            g.DrawLine(stand, 5f * u, 13.6f * u, 11f * u, 13.6f * u);
        }
        if (kind == TrayIcon.Alert)
        {
            float d = 7f * u;
            using var dot = new SolidBrush(Color.FromArgb(0xFF, 0x5A, 0x6A));
            // 点的周围挖掉一圈（透明），和显示器的线条分开：什么底色的任务栏上都看得清
            g.CompositingMode = CompositingMode.SourceCopy;
            g.FillEllipse(Brushes.Transparent, size - d - 0.5f * u, size - d - 0.5f * u, d + u, d + u);
            g.CompositingMode = CompositingMode.SourceOver;
            g.FillEllipse(dot, size - d, size - d, d, d);
        }
        return bitmap;
    }
}
