using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WinDisplay.Win;

/// <summary>测试场景怎么跑。</summary>
/// <param name="Seconds">跑多少秒。</param>
/// <param name="GpuLoad">每个像素额外多算几遍噪声：模拟游戏占着显卡（0 = 不加）。</param>
/// <param name="CpuThreads">另开几个一直空转的线程：模拟游戏占着 CPU（0 = 不加）。</param>
/// <param name="VSync">等垂直同步再呈现（画面帧率 = 显示器刷新率）；false = 不等，能画多快画多快（显卡一直满载，像不限帧率的游戏）。</param>
/// <param name="Detail">画面上细碎纹理的强度（0~1）：越大每帧压出来越大。</param>
/// <param name="IdleSeconds">这台电脑上最近这么多秒内有人动过键盘 / 鼠标 / 触摸就不开（0 = 不管）：别抢正在用电脑的人的桌面。</param>
public sealed record SceneOptions(double Seconds, int GpuLoad, int CpuThreads, bool VSync, double Detail, double IdleSeconds);

/// <summary>测试场景跑完的结果。</summary>
/// <param name="Started">开起来了没有（没开：<paramref name="Why"/> 是原因）。</param>
/// <param name="Dismissed">没跑满时间，被人按键 / 点鼠标关掉的。</param>
/// <param name="PresentMs">每一帧呈现完的时刻（开机以来的毫秒，和 wdsend 逐帧记录里的时刻是同一个钟）。</param>
public sealed record SceneResult(bool Started, string? Why, bool Dismissed, int Width, int Height, IReadOnlyList<double> PresentMs);

/// <summary>
/// 测试场景：在主显示器上开一个盖满全屏、置顶的窗口，用 Direct3D 画一幅整屏都在动的画面（地形一样的底图横着走，上面有细碎的纹理和几个移动的色块），
/// 用来代替「开一个游戏」去量投屏：呈现方式和游戏的无边框全屏一样（翻页模式的交换链），画面只跟时间有关（每次跑出来的是同一段动画），
/// 显卡和 CPU 的负载可以调。它自己记下每一帧呈现的时刻，跑完能看出它自己有没有卡。
/// 和发送端分开跑（另一个进程）：游戏和投屏抢显卡是进程之间的事。
/// 按任意键 / 点鼠标就关。
/// </summary>
public static unsafe class Scene
{
    // 着色器的源码里只能有 ASCII（原因见 GpuScaler）。说明：
    //   Params：time = 开始以来的秒数；size = 窗口的宽、高；detail = 细碎纹理的强度；load = 每个像素额外多算几遍。
    //   ps：uv 以窗口的高为 1。底图 = 分形噪声（fbm）按高度上色，整幅随时间横移、上下晃；再叠一层高频噪声（它决定一帧压出来多大）；
    //       6 个色块各走各的；最后那个循环是白做的功，只为了占显卡（结果乘上很小的系数加进颜色里，编译器删不掉它）。
    private const string Shader = """
        cbuffer Params : register(b0)
        {
            float time;
            float2 size;
            float detail;
            int load;
            int3 unused;
        };

        float4 vs(uint id : SV_VertexID) : SV_Position
        {
            return float4((id & 1) ? 3.0 : -1.0, (id & 2) ? -3.0 : 1.0, 0.0, 1.0);
        }

        float hash(float2 p)
        {
            p = frac(p * float2(123.34, 456.21));
            p += dot(p, p + 45.32);
            return frac(p.x * p.y);
        }

        float noise(float2 p)
        {
            float2 i = floor(p), f = frac(p);
            f = f * f * (3.0 - 2.0 * f);
            return lerp(lerp(hash(i), hash(i + float2(1, 0)), f.x), lerp(hash(i + float2(0, 1)), hash(i + float2(1, 1)), f.x), f.y);
        }

        float fbm(float2 p)
        {
            float v = 0.0, a = 0.5;
            for (int i = 0; i < 5; i++)
            {
                v += a * noise(p);
                p = p * 2.03 + 17.0;
                a *= 0.5;
            }
            return v;
        }

        float4 ps(float4 pos : SV_Position) : SV_Target
        {
            float2 uv = pos.xy / size.y;
            float2 p = uv * 5.0 + float2(time * 1.7, sin(time * 0.6) * 0.8);
            float h = fbm(p);
            float3 col = lerp(float3(0.08, 0.22, 0.42), float3(0.86, 0.76, 0.46), smoothstep(0.35, 0.65, h));
            col = lerp(col, float3(0.15, 0.50, 0.20), smoothstep(0.50, 0.52, h) * (1.0 - smoothstep(0.60, 0.62, h)));
            col += detail * (noise(p * 60.0) - 0.5);
            for (int k = 0; k < 6; k++)
            {
                float2 c = float2(frac(time * (0.11 + 0.03 * k) + k * 0.37) * size.x / size.y, 0.5 + 0.35 * sin(time * (0.9 + 0.2 * k) + k));
                col = lerp(col, float3(frac(k * 0.37 + 0.2), frac(k * 0.61 + 0.5), frac(k * 0.83 + 0.8)), smoothstep(0.06, 0.055, length(uv - c)));
            }
            float waste = 0.0;
            [loop] for (int n = 0; n < load; n++) waste += fbm(p + n * 3.1);
            col += waste * 1e-7;
            return float4(saturate(col), 1.0);
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public float Time, W, H, Detail;
        public int Load, Unused0, Unused1, Unused2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size, Style;
        public IntPtr WndProc;
        public int ClsExtra, WndExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName;
        public IntPtr IconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size, Time;
    }

    private delegate IntPtr WndProcFn(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WndClassEx cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h,
                                                 IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string cls, IntPtr instance);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out Msg msg, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Msg msg);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    private const int DxgiStatusOccluded = 0x087A0001;      // Present 的成功码：窗口被挡住，没有呈现
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);

    private const string ClassName = "WinDisplayScene";
    private const uint WsPopup = 0x80000000, WsVisible = 0x10000000, WsExTopmost = 0x8;
    private const uint WmDestroy = 0x0002, WmClose = 0x0010, WmKeyDown = 0x0100, WmSysKeyDown = 0x0104, WmLButtonDown = 0x0201, WmRButtonDown = 0x0204;
    private const uint EsContinuous = 0x80000000, EsDisplayRequired = 0x2, EsSystemRequired = 0x1;

    private static bool _dismissed;         // 有人按了键 / 点了鼠标（只在跑场景的那个线程上读写：窗口过程就在它上面被调）

    private static IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message is WmKeyDown or WmSysKeyDown or WmLButtonDown or WmRButtonDown or WmClose or WmDestroy)
        {
            _dismissed = true;
            return IntPtr.Zero;
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    /// <summary>这台电脑上离最近一次有人动键盘 / 鼠标 / 触摸过了多少秒。</summary>
    public static double IdleSeconds()
    {
        var info = new LastInputInfo { Size = (uint)sizeof(LastInputInfo) };
        if (!GetLastInputInfo(ref info)) return double.PositiveInfinity;
        return unchecked((uint)Environment.TickCount - info.Time) / 1000.0;     // 两边都是开机以来的毫秒（32 位，会回绕；相减不受影响）
    }

    /// <summary>
    /// 跑测试场景，跑完（到时间，或者被人关掉）才返回。一个进程里同一时刻只能跑一个。
    /// 开不起来（电脑上有人在用、建不了显卡设备或窗口）不抛异常，结果里 Started = false。
    /// </summary>
    public static SceneResult Run(SceneOptions o)
    {
        double idle = IdleSeconds();
        if (o.IdleSeconds > 0 && idle < o.IdleSeconds)
            return new SceneResult(false, $"这台电脑 {idle:F0} 秒前还有人在用（要空闲满 {o.IdleSeconds:F0} 秒才开）", false, 0, 0, []);

        SetProcessDpiAwarenessContext(-4);          // 按显示器的真实像素算（已经设过的话这一句不起作用，没关系）
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);       // 主显示器的宽、高
        IntPtr instance = GetModuleHandleW(null);
        WndProcFn proc = WndProc;                   // 窗口还在的时候不能让它被回收：下面 GC.KeepAlive
        var cls = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(), WndProc = Marshal.GetFunctionPointerForDelegate(proc), Instance = instance,
            Cursor = LoadCursorW(IntPtr.Zero, 32512), ClassName = ClassName,
        };
        if (RegisterClassExW(ref cls) == 0) return new SceneResult(false, $"登记不了窗口类（错误 {Marshal.GetLastWin32Error()}）", false, 0, 0, []);
        IntPtr window = IntPtr.Zero;
        var stop = new CancellationTokenSource();
        var times = new List<double>(o.VSync ? (int)(o.Seconds * 250) : 1 << 20);
        _dismissed = false;
        try
        {
            window = CreateWindowExW(WsExTopmost, ClassName, "WinDisplay scene", WsPopup | WsVisible, 0, 0, w, h, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (window == IntPtr.Zero) return new SceneResult(false, $"建不了窗口（错误 {Marshal.GetLastWin32Error()}）", false, w, h, []);

            FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
            Result r = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels,
                                               out ID3D11Device? device, out ID3D11DeviceContext? context);
            if (r.Failure || device is null || context is null) return new SceneResult(false, $"建不了 Direct3D 设备（{r}）", false, w, h, []);
            using ID3D11Device deviceGuard = device;
            using ID3D11DeviceContext contextGuard = context;
            using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
            using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();
            var desc = new SwapChainDescription1
            {
                Width = (uint)w, Height = (uint)h, Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput, BufferCount = 2, SwapEffect = SwapEffect.FlipDiscard, Scaling = Scaling.Stretch,
                AlphaMode = AlphaMode.Ignore, Flags = o.VSync ? SwapChainFlags.None : SwapChainFlags.AllowTearing,
            };
            using IDXGISwapChain1 swap = factory.CreateSwapChainForHwnd(device, window, desc);
            using ID3D11Texture2D back = swap.GetBuffer<ID3D11Texture2D>(0);
            using ID3D11RenderTargetView view = device.CreateRenderTargetView(back);
            using ID3D11VertexShader vs = device.CreateVertexShader(Compiler.Compile(Shader, "vs", "scene.hlsl", "vs_4_0").ToArray());
            using ID3D11PixelShader ps = device.CreatePixelShader(Compiler.Compile(Shader, "ps", "scene.hlsl", "ps_4_0").ToArray());
            using ID3D11Buffer constants = device.CreateBuffer((uint)sizeof(Params), BindFlags.ConstantBuffer);

            for (int i = 0; i < o.CpuThreads; i++)
                new Thread(() =>
                {
                    double x = 1;
                    while (!stop.IsCancellationRequested) x = Math.Sqrt(x + 1.000001);
                    GC.KeepAlive(x);
                }) { IsBackground = true, Name = "scene-cpu" }.Start();
            SetThreadExecutionState(EsContinuous | EsDisplayRequired | EsSystemRequired);       // 跑的时候别息屏、别睡

            long start = Stopwatch.GetTimestamp();
            while (!_dismissed)
            {
                while (PeekMessageW(out Msg msg, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
                double t = Stopwatch.GetElapsedTime(start).TotalSeconds;
                if (t >= o.Seconds) break;
                var p = new Params { Time = (float)t, W = w, H = h, Detail = (float)o.Detail, Load = o.GpuLoad };
                context.UpdateSubresource(in p, constants);
                context.OMSetRenderTargets(view);
                context.RSSetViewport(0, 0, w, h);
                context.IASetInputLayout(null);
                context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                context.VSSetShader(vs);
                context.PSSetShader(ps);
                context.PSSetConstantBuffer(0, constants);
                context.Draw(3, 0);
                Result presented = swap.Present(o.VSync ? 1u : 0u, o.VSync ? PresentFlags.None : PresentFlags.AllowTearing);
                presented.CheckError();
                if (presented.Code == DxgiStatusOccluded)
                {
                    // 窗口被整个挡住了（锁屏、UAC 提示）：这一帧没有上屏，Present 也不等垂直同步、立刻返回。
                    // 不记成一帧（记了的话帧率是假的，「场景自己卡没卡」也看不出来），歇一帧的时间再试
                    Thread.Sleep(16);
                    continue;
                }
                times.Add(Stopwatch.GetElapsedTime(0).TotalMilliseconds);
            }
            return new SceneResult(true, null, _dismissed, w, h, times);
        }
        catch (SharpGenException e)
        {
            return new SceneResult(times.Count > 0, $"显卡那边出错：{e.Message.Split('\n', 2)[0].Trim()}", false, w, h, times);
        }
        finally
        {
            stop.Cancel();
            SetThreadExecutionState(EsContinuous);
            if (window != IntPtr.Zero) DestroyWindow(window);
            UnregisterClassW(ClassName, instance);
            GC.KeepAlive(proc);
        }
    }
}
