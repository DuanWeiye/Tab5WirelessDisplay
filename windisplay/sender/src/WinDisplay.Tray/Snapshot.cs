using System.Drawing.Imaging;
using WinDisplay.Core;

namespace WinDisplay.Tray;

/// <summary>
/// 调试入口（WinDisplayTray.exe --snapshot 目录）：把设置窗口在几种局面下的样子、几个对话框、托盘图标各画成一张 PNG 存下来，
/// 不连 Tab5、不抓屏、不碰存的文件。界面改了之后拿它看一眼长什么样（只画自己的窗口，不截桌面）。
/// </summary>
internal static class Snapshot
{
    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        PairedPeer living = new(new byte[Proto.IdSize], "Tab5-46C3"), study = new(new byte[Proto.IdSize], "书房的 Tab5");
        (string Name, IReadOnlyList<SettingsGroup> Groups)[] cases =
        [
            ("1_fresh", TrayView.Build(new HostStatus(HostState.Searching), false, false, [], false)),
            ("2_need_password", TrayView.Build(new HostStatus(HostState.NeedPairing), false, false, [], false)),
            ("3_waiting", TrayView.Build(new HostStatus(HostState.AwaitingPassword), true, false, [], true)),
            ("4_connected", TrayView.Build(new HostStatus(HostState.Showing, "Tab5-46C3"), true, false, [living, study], true)),
            ("5_stopped", TrayView.Build(new HostStatus(HostState.NeedPairing), true, true, [living], true)),
            ("6_fault", TrayView.Build(new HostStatus(HostState.Faulted, Detail: "抓屏开不了"), true, false, [living], false)),
        ];
        foreach ((string name, IReadOnlyList<SettingsGroup> groups) in cases)
        {
            using var form = new SettingsForm();
            form.SetContent(groups);
            form.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            form.Panel.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.ClientSize));
            bitmap.Save(Path.Combine(dir, $"settings_{name}.png"), ImageFormat.Png);
            Console.WriteLine($"settings_{name}.png {bitmap.Width}×{bitmap.Height}（DPI {form.DeviceDpi}）");
        }

        Save(Dialog.BuildConfirm("更改密码会移除 2 台已配对的设备。", "更改"), "dialog_confirm_change");
        Save(Dialog.BuildConfirm("移除「书房的 Tab5」？", "移除"), "dialog_confirm_forget");
        Save(Dialog.BuildInfo("这个会话里没有桌面，抓不了屏幕（要在登录用户的桌面会话里跑）"), "dialog_fault");
        Dialog password = Dialog.BuildPassword(out TextBox box);
        box.Text = "2468";
        Save(password, "dialog_password");

        // 托盘图标：三种，放大了画在深、浅两种底色上（任务栏可能是深色也可能是浅色）
        const int size = 64, pad = 16;
        using (var sheet = new Bitmap(3 * (size + pad) + pad, 2 * (size + pad) + pad))
        {
            using (Graphics g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(0x1C, 0x1C, 0x1C));
                g.FillRectangle(Brushes.Gainsboro, 0, size + pad + pad / 2, sheet.Width, sheet.Height);
                TrayIcon[] kinds = [TrayIcon.Idle, TrayIcon.Connected, TrayIcon.Alert];
                for (int i = 0; i < kinds.Length; i++)
                    for (int row = 0; row < 2; row++)
                    {
                        using Bitmap icon = Icons.Draw(kinds[i], size, lightTaskbar: row == 1);
                        g.DrawImage(icon, pad + i * (size + pad), pad + row * (size + pad));
                    }
            }
            sheet.Save(Path.Combine(dir, "tray_icons.png"), ImageFormat.Png);
        }

        // 同样三种，照托盘里现在的实际尺寸画（不放大）：图的高度就是 Icons.TraySize() 给的像素数
        int actual = Icons.TraySize();
        using (var strip = new Bitmap(3 * actual, actual))
        {
            using (Graphics g = Graphics.FromImage(strip))
            {
                g.Clear(Color.FromArgb(0x1C, 0x1C, 0x1C));
                TrayIcon[] kinds = [TrayIcon.Idle, TrayIcon.Connected, TrayIcon.Alert];
                for (int i = 0; i < kinds.Length; i++)
                {
                    using Bitmap icon = Icons.Draw(kinds[i], actual, lightTaskbar: false);
                    g.DrawImage(icon, i * actual, 0);
                }
            }
            strip.Save(Path.Combine(dir, "tray_icons_actual.png"), ImageFormat.Png);
        }
        Console.WriteLine("画完了");
        return 0;

        void Save(Dialog dialog, string name)
        {
            using (dialog)
            {
                dialog.Show();
                Application.DoEvents();
                using var bitmap = new Bitmap(dialog.Width, dialog.Height);
                dialog.DrawToBitmap(bitmap, new Rectangle(0, 0, dialog.Width, dialog.Height));
                bitmap.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
                Console.WriteLine($"{name}.png {bitmap.Width}×{bitmap.Height}");
            }
        }
    }
}
