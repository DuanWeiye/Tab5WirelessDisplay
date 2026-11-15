using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Security.Authorization.AppCapabilityAccess;
using WinDisplay.Core;

namespace WinDisplay.Win;

/// <summary>抓屏的运行情况，给调用方看（命令行工具打印、以后界面上显示）。抓屏重开之后接着累计。</summary>
public sealed class ScreenCaptureStats
{
    /// <summary>被抓的显示器现在的分辨率。</summary>
    public int SourceWidth, SourceHeight;
    private object _fit = default(ViewFit);     // 装箱的 ViewFit：换的时候整个换，别的线程读到的要么是旧的、要么是新的，不会是半新半旧

    /// <summary>画面在 1280×720 里占的位置。哪个线程都能读（注入输入的那一边按它换算触摸的位置）；别的几项只在取画面的线程上读写。</summary>
    public ViewFit Fit
    {
        get => (ViewFit)Volatile.Read(ref _fit);
        set
        {
            if (!value.Equals(Volatile.Read(ref _fit))) Volatile.Write(ref _fit, value);     // 每帧都来设一次，没变就不换
        }
    }
    /// <summary>被抓的屏四周那圈黄框去掉了没有（系统不让去的时候是 false，抓屏照常）。</summary>
    public bool Borderless;
    /// <summary>读走了多少幅画面；每幅「缩放 + 读回内存」花的时间合计、最长的一次（毫秒）。</summary>
    public long Frames;
    public double TotalMs, MaxMs;
    /// <summary>系统一共给了多少幅（读走的，加上来不及读、被后一幅顶掉的）。在系统的回调线程上加，别的线程用 Volatile.Read 读。</summary>
    public long Arrived;
    /// <summary>每读走一幅调一次（在取画面的线程上，画面已经写好之后）：逐帧记录用。回调里不要做慢事。</summary>
    public Action<CaptureRead>? Read;
}

/// <summary>读走的一幅画面的记录。</summary>
/// <param name="ScaleMs">缩放 + 读回内存花的时间（显卡忙的时候会变长）。</param>
/// <param name="AgeMs">开始处理它的时候，离系统合成出这一幅已经过了多久。</param>
/// <param name="ComposeGapMs">系统合成这一幅和合成上一幅读走的画面之间隔了多久（第一幅是 0）。</param>
/// <param name="Arrived">到这时系统一共给了多少幅（和上一条记录的差减 1 = 中间被顶掉没读的）。</param>
public readonly record struct CaptureRead(double ScaleMs, double AgeMs, double ComposeGapMs, long Arrived);

/// <summary>
/// 用 Windows.Graphics.Capture（WGC）抓一台显示器：屏幕变了系统才给新的一帧（鼠标指针动了也算），拿到之后在显卡上缩成 1280×720 读回来。
/// 选 WGC 不选 DXGI 桌面复制：后者开着的时候会把窗口化的游戏从 120 帧压到 60 帧（gameController 工程 win_streamer 的实测）。
/// 要 Windows 11：不画黄框（IsBorderRequired）和按显示器建抓屏对象（TryCreateFromDisplayId）都是它才有的。
///
/// 系统在它自己的线程上回调「来了新的一帧」，这里只把最新的一帧留下（旧的没人取就丢掉，见 <see cref="LatestSlot{T}"/>）；
/// 缩放和读回都在调 TryRead 的线程上做。
/// 每来一帧都当场从帧池里取走：帧池满了系统就不再出新帧，等会话腾出手来取的时候拿到的会是很久以前的画面。
///
/// 出错一律报 <see cref="CaptureLostException"/>（上一层关掉重开），不按异常的类型挑：系统接口（WinRT 的投影）会把同一类故障
/// 报成各种 .NET 异常（COMException 之外还有 ArgumentException、InvalidCastException、NullReferenceException……），
/// 挑着接的话，换显示模式、驱动重置时没挑中的那一种会把整个显示会话弄崩。代价是这个类自己代码里的错也会被当成「抓屏断了」，
/// 所以上一层要把原因报出来（<see cref="CaptureSource.Lost"/>：头一次、以及原因变了的时候都报）。
/// </summary>
internal sealed class WgcCapture : ICaptureBackend
{
    // 帧池里几个缓冲：留着没取的最新一帧占一个，正在缩放的那一帧占一个，还得剩一个给系统画下一帧——
    // 系统没有空缓冲可画的时候那一帧就丢了，要是它正好是屏幕静下来之前的最后一帧，Tab5 上就停在旧画面
    private const int PoolBuffers = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    private const uint MonitorDefaultToPrimary = 1;

    private const string ItemGone = "被抓的显示器没了（拔了，或者显示器的排列变了）";

    private readonly ScreenCaptureStats? _stats;
    private readonly bool _cursor;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private GpuScaler? _scaler;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private TimeSpan? _lastComposed;            // 上一幅读走的画面是系统什么时候合成的
    private GraphicsCaptureSession? _session;
    private TypedEventHandler<Direct3D11CaptureFramePool, object>? _onFrame;
    private SizeInt32 _size;                            // 这一次抓屏会话是按多大的画面开的
    // 这一次抓屏会话的「最新一帧」，每开一次会话换一个新的。只有 OnItemClosed 在别的线程上读它
    private volatile LatestSlot<Direct3D11CaptureFrame>? _slot;
    private volatile bool _closed;                      // 被抓的显示器没了
    private bool _disposed;

    /// <summary>主显示器（任务栏所在的那台）的句柄。</summary>
    public static IntPtr PrimaryMonitor() => MonitorFromPoint(default, MonitorDefaultToPrimary);      // 主显示器的左上角就是坐标原点

    /// <summary>打开一台显示器的抓屏。打不开抛 <see cref="CaptureLostException"/>。</summary>
    /// <param name="cursor">画面里带不带鼠标指针。</param>
    public WgcCapture(IntPtr monitor, bool cursor = true, ScreenCaptureStats? stats = null)
    {
        _stats = stats;
        _cursor = cursor;
        try
        {
            (_device, _context) = Gpu.CreateDevice();
            _scaler = new GpuScaler(_device, _context);
            _winrtDevice = Gpu.ToWinRT(_device);
            // DisplayId 的值就是显示器句柄（HMONITOR）：这一点没有单独的文档，依据是 Windows App SDK 的
            // Win32Interop.GetDisplayIdFromMonitor 也是直接把句柄的值填进 DisplayId.Value；主显示器实测可用（--screen-check 两条路的画面一致）
            _item = GraphicsCaptureItem.TryCreateFromDisplayId(new DisplayId((ulong)monitor))
                    ?? throw new CaptureLostException("系统没给出这台显示器的抓屏对象");
            _item.Closed += OnItemClosed;
            StartSession(_item.Size);
        }
        catch (Exception e)
        {
            Dispose();
            if (e is CaptureLostException) throw;
            string where = Process.GetCurrentProcess().SessionId == 0 ? "（这个程序不在登录用户的桌面会话里：从 ssh 或者服务里起的程序抓不了屏）" : "";
            throw new CaptureLostException($"抓屏打不开{where}：{FirstLine(e.Message)}", e);
        }
    }

    /// <summary>系统接口报的错，同一句话常常隔一个空行重复一遍：只留第一行。</summary>
    private static string FirstLine(string message) => message.Split('\n', 2)[0].Trim();

    /// <summary>系统的线程上回调：被抓的显示器没了。</summary>
    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        // 先立标记、再叫醒：这当口取画面的线程要是正好换了一个新的 slot，叫醒落在旧的上也不要紧，它转回来会看到标记
        _closed = true;
        _slot?.Fail(ItemGone);
    }

    private void StartSession(SizeInt32 size)
    {
        _lastComposed = null;       // 新的抓屏会话：第一幅没有「上一幅」可比（不然 -v 里会记成中断了多久）
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;
        var slot = new LatestSlot<Direct3D11CaptureFrame>();
        try
        {
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, size);
            session = pool.CreateCaptureSession(_item!);
            session.IsCursorCaptureEnabled = _cursor;
            bool borderless = false;
            try
            {
                // 被抓的屏四周默认会画一圈黄框。Windows 11 可以不画：先向系统申请（没打包的桌面程序不弹窗，直接给），给了才关
                AppCapabilityAccessStatus access =
                    GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().GetAwaiter().GetResult();
                if (access == AppCapabilityAccessStatus.Allowed)
                {
                    session.IsBorderRequired = false;
                    borderless = true;
                }
            }
            catch (Exception)
            {
                // 去不掉黄框不耽误抓屏；调用方从 ScreenCaptureStats.Borderless 能看到
            }
            if (_stats is not null) _stats.Borderless = borderless;
            // 系统的线程上回调。取帧出的错由 slot 记下来，TryRead 下一次取的时候会抛出去（不然没有人知道）；
            // slot 关了之后不会再去碰帧池（见 LatestSlot.Offer），所以 StopSession 先关 slot 再拆帧池
            Direct3D11CaptureFramePool framePool = pool;
            ScreenCaptureStats? stats = _stats;
            TypedEventHandler<Direct3D11CaptureFramePool, object> onFrame = (_, _) => slot.Offer(() =>
            {
                Direct3D11CaptureFrame? frame = framePool.TryGetNextFrame();
                if (frame is not null && stats is not null) Interlocked.Increment(ref stats.Arrived);
                return frame;
            });
            pool.FrameArrived += onFrame;
            (_pool, _session, _onFrame, _size, _slot) = (pool, session, onFrame, size, slot);
            session.StartCapture();
        }
        catch
        {
            // 开到一半失败：已经建出来的都关掉，别等垃圾回收（帧池占着显存）
            if (ReferenceEquals(_slot, slot))
            {
                StopSession();
            }
            else
            {
                slot.Dispose();
                Quietly(() => session?.Dispose());
                Quietly(() => pool?.Dispose());
            }
            throw;
        }
    }

    /// <summary>关掉这一次抓屏会话。每一步出的错都不往外抛：关的时候多半已经是出了事（设备丢了），再抛会盖掉原来的原因、或者让「断了重开」变成会话崩溃。</summary>
    private void StopSession()
    {
        (Direct3D11CaptureFramePool? pool, GraphicsCaptureSession? session, var onFrame, var slot) = (_pool, _session, _onFrame, _slot);
        (_pool, _session, _onFrame, _slot) = (null, null, null, null);
        slot?.Dispose();        // 先关它：它返回之后回调不会再进帧池，没取走的那一帧也还回去了
        if (pool is not null && onFrame is not null) Quietly(() => pool.FrameArrived -= onFrame);
        Quietly(() => session?.Dispose());
        Quietly(() => pool?.Dispose());
    }

    private static void Quietly(Action close)
    {
        try
        {
            close();
        }
        catch (Exception)
        {
            // 见 StopSession 的说明
        }
    }

    /// <summary>
    /// 显卡设备丢了（驱动重置、驱动更新、显卡被拔）就抛。没等到新的一帧时要查这个：设备丢了之后帧池可能就不再出帧，
    /// 不查的话会一直当成「屏幕没变」，Tab5 上停在旧画面，也不会去重开。
    /// </summary>
    private void ThrowIfDeviceLost()
    {
        SharpGen.Runtime.Result reason = _device!.DeviceRemovedReason;
        if (reason.Failure) throw new CaptureLostException($"显卡设备丢了（{reason}）");
    }

    public bool TryRead(Span<byte> dst, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (dst.Length < Proto.ViewW * 4 * Proto.ViewH) throw new ArgumentException("缓冲不够放 1280×720 的画面", nameof(dst));
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        try
        {
            while (true)
            {
                if (_closed) throw new CaptureLostException(ItemGone);
                TimeSpan left = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
                Direct3D11CaptureFrame? frame = _slot!.Take(left > TimeSpan.Zero ? left : TimeSpan.Zero);
                if (frame is null)
                {
                    ThrowIfDeviceLost();
                    return false;
                }
                try
                {
                    SizeInt32 size = frame.ContentSize;
                    if (size.Width != _size.Width || size.Height != _size.Height)
                    {
                        // 换了分辨率，或者屏转了方向：这一帧还是按旧尺寸的缓冲给的，可能缺一块。
                        // 按新尺寸重开抓屏会话——新会话一开始系统一定先给一帧完整的，哪怕之后屏幕不再变
                        if (size.Width <= 0 || size.Height <= 0) throw new CaptureLostException($"系统报的画面尺寸是 {size.Width}×{size.Height}");
                        StopSession();
                        StartSession(size);
                        continue;
                    }
                    long t0 = Stopwatch.GetTimestamp();
                    // 系统合成出这一幅的时刻，和 Stopwatch 是同一个钟（开机以来的时间）
                    TimeSpan composed = frame.SystemRelativeTime;
                    using ID3D11Texture2D texture = Gpu.TextureOf(frame.Surface);
                    ViewFit fit = _scaler!.Scale(texture, size.Width, size.Height, dst);
                    if (_stats is { } s)
                    {
                        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                        (s.SourceWidth, s.SourceHeight, s.Fit) = (size.Width, size.Height, fit);
                        s.Frames++;
                        s.TotalMs += ms;
                        s.MaxMs = Math.Max(s.MaxMs, ms);
                        s.Read?.Invoke(new CaptureRead(ms, (Stopwatch.GetElapsedTime(0, t0) - composed).TotalMilliseconds,
                                                       _lastComposed is { } last ? (composed - last).TotalMilliseconds : 0, Volatile.Read(ref s.Arrived)));
                    }
                    _lastComposed = composed;
                    return true;
                }
                finally
                {
                    // 把帧还给帧池。出错不抛：画面这时已经写进 dst 了，抛出去就违反「抛之前不能动 dst」；真是设备出了事，下一次取帧自己会报
                    Quietly(frame.Dispose);
                }
            }
        }
        catch (Exception e) when (e is not CaptureLostException)
        {
            // 显卡设备丢了、驱动重置、抓屏会话被系统收走……：关掉重开。为什么不按异常类型挑，见类的说明
            throw new CaptureLostException($"抓屏出错（{e.GetType().Name}）：{FirstLine(e.Message)}", e);
        }
    }

    /// <summary>每一步出的错都不往外抛，理由同 <see cref="StopSession"/>。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_item is not null) Quietly(() => _item.Closed -= OnItemClosed);
        StopSession();
        Quietly(() => _scaler?.Dispose());
        Quietly(() => _winrtDevice?.Dispose());
        Quietly(() => _context?.Dispose());
        Quietly(() => _device?.Dispose());
    }
}
