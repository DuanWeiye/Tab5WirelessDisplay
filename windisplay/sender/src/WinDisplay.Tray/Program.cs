using WinDisplay.Core;
using WinDisplay.Win;

namespace WinDisplay.Tray;

/// <summary>
/// 托盘程序的入口。平时不带参数（开机启动的也是它）；另外几种用法是给装机 / 更新的脚本和调试用的：
/// <code>
/// WinDisplayTray.exe [--data-dir 目录]                        常驻。数据（配对、密码、日志）默认放 %LOCALAPPDATA%\WinDisplay
/// WinDisplayTray.exe [--data-dir 目录] --set-password 密码    不开界面设 / 改配对密码，设完退出。和在设置窗口里改一样：配对过的设备全部移除
/// WinDisplayTray.exe --exit                                   叫正在跑的那一份正常退出（给 Tab5 发 BYE、松开按着的键），等它退完
/// WinDisplayTray.exe --autostart on|off                        开 / 关开机启动（和设置窗口里那个开关是同一件事；指的是这个 exe 现在所在的路径）
/// WinDisplayTray.exe --snapshot 目录                          调试：把界面画成 PNG（见 Snapshot）
/// </code>
/// 退出码：0 = 成了；1 = 没成（原因写在日志和标准错误里）；2 = 参数不对。
/// <para>
/// 同一个登录会话（桌面）里只跑一份：再起一份只是叫先起的那一份把设置窗口打开。
/// 同一份数据同一时刻只让一个进程动（<see cref="DataDirLock"/>，跨会话也管用：ssh 进来设密码的、远程桌面里的另一份都算）：
/// 常驻的那一份一直占着数据目录，--set-password 占一下就放；占不到的那一个说明原因后退出。
/// </para>
/// </summary>
internal static class Program
{
    // Local\ = 只在这个登录会话里看得见。互斥量和两个事件只是「叫正在跑的那一份打开窗口 / 退出」的通道
    private const string MutexName = @"Local\WinDisplayTray", ShowEventName = @"Local\WinDisplayTray.Show", ExitEventName = @"Local\WinDisplayTray.Exit";
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(15);
    // 等数据目录的锁最多等多久。常驻的那一份多等一会儿：开机启动时脚本可能正好在设密码（一下就完）
    private static readonly TimeSpan DataWait = TimeSpan.FromSeconds(3), DataWaitSetPassword = TimeSpan.FromSeconds(1);

    [STAThread]
    private static int Main(string[] args)
    {
        // 屏幕坐标按物理像素算（注入触摸要这样，InputInjector 建的时候会核对）；要在建任何窗口之前设
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // 界面线程上没人接的异常不弹系统那个「继续 / 退出」的框：照没人接的异常走（先松开按着的键，再让进程结束）
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);

        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDisplay");
        string? newPassword = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--snapshot" when i + 1 < args.Length && args.Length == 2:
                    return Snapshot.Run(args[i + 1]);
                case "--exit" when args.Length == 1:
                    return AskRunningToExit();
                case "--autostart" when args.Length == 2 && args[1] is "on" or "off":
                    return SetAutoStart(args[1] == "on");
                case "--data-dir" when i + 1 < args.Length:
                    dir = Path.GetFullPath(args[++i]);
                    break;
                case "--set-password" when i + 1 < args.Length:
                    newPassword = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"不认识的参数：{args[i]}");
                    return 2;
            }
        }

        var log = new Log(Path.Combine(dir, "tray.log"));
        // 设密码的不占互斥量、不建那两个事件（它不是「正在跑的那一份」：占了的话，这时开机启动的托盘会以为已经有一份、自己退出）
        if (newPassword is not null) return SetPassword(dir, newPassword, log);

        // 两个事件建在互斥量前面：别的进程看得到互斥量的时候事件一定已经在了（--exit 不会在这一份启动的半路上扑空）。
        // 收的时候反过来（后声明的先收）：互斥量先没
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        using var exit = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        using var single = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            show.Set();         // 这个桌面上已经有一份在跑：叫它把设置窗口打开，自己退出
            return 0;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Write("没人接的异常：" + e.ExceptionObject);

        using IDisposable? data = TakeData(dir, DataWait, $"另一份托盘程序正在用这份数据（{dir}）", out string busy);
        if (data is null) return Fail(log, busy, quiet: false);

        PairStore store;
        HostSettings settings;
        PairingGate gate;
        try
        {
            (store, settings) = OpenData(dir);
            gate = settings.CreateGate();
        }
        catch (Exception e) when (e is InvalidDataException or PairStoreException)
        {
            // 存的文件读不懂 / 建不出来：说明原因就退出（硬撑着跑起来的话配对就全丢了）
            return Fail(log, e.Message, quiet: false);
        }

        SenderHost host;
        InputPump pump;
        try
        {
            var screen = new ScreenCaptureStats();
            var injector = new InputInjector();
            injector.Warning += what => log.Write("注入：" + what);
            pump = new InputPump(new InputBridge(injector, () => screen.Fit, InputInjector.HostKeyRepeat, InputInjector.TouchKeepAliveMs), InputInjector.HostLeds);
            pump.Faulted += e => log.Write("注入输入的后台线程出错：" + e.Message);
            pump.Start();
            // 电脑正在放的声音也发给 Tab5（只旁听，不动电脑的声音设置）。抓不了不影响画面，原因记进日志
            var sound = new LoopbackAudio();
            sound.Warning += why => log.Write("声音：" + why);
            host = new SenderHost(store, gate, settings, Environment.MachineName, new LanDiscovery(), () => ScreenCapture.OpenPrimary(screen), pump, audio: sound);
        }
        catch (InvalidOperationException e)
        {
            // 这个会话里注入不了输入（不是登录用户的桌面会话）：硬撑着跑起来的话 Tab5 上的触摸和按键都没反应
            return Fail(log, e.Message, quiet: false);
        }

        // 进程不走正常的路结束时（没人接的异常），注入进去还按着的键也要松开。注销 / 关机走的是正常退出，见 TrayApp
        ExitHooks.Register(pump.Close);

        using var app = new TrayApp(host, pump, log);
        RegisteredWaitHandle onShow = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => app.RequestShow(), null, Timeout.Infinite, executeOnlyOnce: false);
        RegisteredWaitHandle onExit = ThreadPool.RegisterWaitForSingleObject(exit, (_, _) => app.RequestExit(), null, Timeout.Infinite, executeOnlyOnce: false);
        host.Start();
        log.Write($"启动 {Application.ProductVersion}（数据在 {dir}）");
        if (!host.HasPassword) app.ShowSettings();      // 头一次用：还没设密码，把设置窗口打开（那一行是红的）
        Application.Run(app);
        onShow.Unregister(null);
        onExit.Unregister(null);
        log.Write("退出");
        single.ReleaseMutex();      // --exit 的那一份在等它：放掉了它才算我们退完
        return 0;
    }

    /// <summary>配对密钥、配对密码算出来的数据：存在用户自己的目录里，再交给 Windows 的数据保护包一层。要先占住数据目录（TakeData）。</summary>
    private static (PairStore Store, HostSettings Settings) OpenData(string dir) =>
        (new PairStore(Path.Combine(dir, "pairs.bin"), Dpapi.Protect, Dpapi.Unprotect),
         new HostSettings(Path.Combine(dir, "host.bin"), Dpapi.Protect, Dpapi.Unprotect));

    /// <summary>占住数据目录（见 <see cref="DataDirLock"/>）。占不到返回 null，why 是原因：别的进程占着就是 held，不然是目录用不了的原因。</summary>
    private static IDisposable? TakeData(string dir, TimeSpan wait, string held, out string why)
    {
        why = held;
        try
        {
            return DataDirLock.TryTake(dir, wait);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            why = $"数据目录 {dir} 用不了：{e.Message}";
            return null;
        }
    }

    /// <summary>
    /// --set-password：不开界面设 / 改配对密码。和设置窗口里改密码走的是同一个 SenderHost.SetPassword（没有 Start：不连 Tab5、不抓屏）。
    /// 托盘程序正在用这份数据时拒绝：它只在启动时读文件，这里改了它也不知道，还会把这里存的盖掉。
    /// </summary>
    private static int SetPassword(string dir, string password, Log log)
    {
        using IDisposable? data = TakeData(dir, DataWaitSetPassword, "托盘程序正在用这份数据：在它的设置窗口里改密码，或者先让它退出（--exit）", out string busy);
        if (data is null) return Fail(log, busy, quiet: true);
        try
        {
            (PairStore store, HostSettings settings) = OpenData(dir);
            using var offline = new SenderHost(store, settings.CreateGate(), settings, Environment.MachineName, new LanDiscovery(),
                                               () => throw new CaptureLostException("只是来设密码的"));
            offline.SetPassword(password);
            log.Write("配对密码已设置（命令行）");
            return 0;
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or PairStoreException)
        {
            return Fail(log, e.Message, quiet: true);
        }
    }

    // quiet：是脚本调的（设密码），不弹对话框。不在交互式的桌面上（比如 ssh 进来的会话）也不弹：那里没人看得见，模态框也弹不出来（ShowDialog 会抛异常）
    private static int Fail(Log log, string why, bool quiet)
    {
        log.Write("没成：" + why);
        Console.Error.WriteLine(why);
        if (!quiet && SystemInformation.UserInteractive) Dialog.Info(null, why);
        return 1;
    }

    private static int SetAutoStart(bool on)
    {
        try
        {
            AutoStart.Set(TrayApp.AutoStartName, Application.ExecutablePath, on);
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    /// <summary>叫正在跑的那一份退出，等它退完（它退出时放掉那个互斥量）。没有在跑的也算成了。</summary>
    private static int AskRunningToExit()
    {
        if (!Mutex.TryOpenExisting(MutexName, out Mutex? running)) return 0;
        using (running)
        {
            // 事件建在互斥量前面、收在它后面（见 Main）：有互斥量就有事件。打不开只能是那一份正好在这两步之间没了
            if (!EventWaitHandle.TryOpenExisting(ExitEventName, out EventWaitHandle? exit)) return 0;
            using (exit) exit.Set();
            try
            {
                if (!running.WaitOne(ExitWait))
                {
                    Console.Error.WriteLine($"等了 {ExitWait.TotalSeconds:F0} 秒，正在跑的那一份还没退出");
                    return 1;
                }
            }
            catch (AbandonedMutexException)
            {
                // 它没放掉互斥量就没了（进程结束时系统替它放的）：也是退出了
            }
            running.ReleaseMutex();
            return 0;
        }
    }
}
