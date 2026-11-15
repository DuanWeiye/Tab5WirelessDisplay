using System.Runtime.InteropServices;
using WinDisplay.Core;

namespace WinDisplay.Win;

/// <summary>
/// 这台电脑正在放的声音：默认播放设备的 WASAPI 环回（系统混好、要送去扬声器的那一路），接成会话的声音来源。
/// 只是旁听，不动电脑的声音设置：电脑本机照常出声，音量、静音、默认设备都不改。
/// 格式让系统转成协议要的 48 kHz、双声道、16 位再给（设备本身是别的采样率 / 声道数也行）。
/// 电脑没有东西在放的时候系统不给数据；有程序开着一路声音却不出声时给的是全 0，长的那种挡掉（<see cref="SilenceGate"/>）。
/// 抓不了（没有播放设备、设备被拔了）或者默认设备换了：隔 1 秒重开，不结束会话，这期间就是没有声音。
/// </summary>
public sealed unsafe class LoopbackAudio : IAudioSource
{
    private static readonly Guid ClsidEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");     // MMDeviceEnumerator
    private static readonly Guid IidEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");       // IMMDeviceEnumerator
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");      // IAudioClient
    private static readonly Guid IidCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");    // IAudioCaptureClient

    private const uint ClsCtxAll = 23;
    private const uint CoInitMultithreaded = 0;
    private const int RenderFlow = 0, ConsoleRole = 0;              // eRender、eConsole：默认的播放设备
    private const uint StreamLoopback = 0x00020000;                 // 旁听这个播放设备
    private const uint StreamEventCallback = 0x00040000;            // 有数据时点一下事件（Windows 10 1703 起环回也支持）
    private const uint StreamAutoConvert = 0x80000000;              // 设备的格式不是我们要的：系统转
    private const uint StreamSrcDefaultQuality = 0x08000000;        // 转采样率用好一点的算法
    private const uint BufferSilent = 0x2;                          // 这一包是静音：数据区不保证是 0，当成全 0
    private const long BufferHns = 200 * 10000;                     // 系统那边的缓冲 200 ms（单位 100 纳秒）
    private const int ElementNotFound = unchecked((int)0x80070490); // 没有默认的播放设备

    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(1);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint model);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(Guid* clsid, IntPtr outer, uint context, Guid* iid, IntPtr* obj);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoTaskMemFree(IntPtr p);

    /// <summary>
    /// 抓不了声音，或者开着的时候断了（参数是一句说明：什么原因、之后会不会重开）。同一个原因连着出现只报一次；重开成了之后再出同样的毛病会再报。
    /// 在抓声音的线程上回调。
    /// </summary>
    public event Action<string>? Warning;
    /// <summary>开始抓了（头一次，或者断了之后重开成了）；参数是设备的编号。在抓声音的线程上回调。</summary>
    public event Action<string>? Opened;

    public IDisposable Start(AudioSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return new Run(this, sink);
    }

    private sealed class AudioLostException(string message) : Exception(message);

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new AudioLostException($"{what}（0x{hr:X8}）");
    }

    private static void Release(ref IntPtr p)
    {
        if (p != IntPtr.Zero) Marshal.Release(p);
        p = IntPtr.Zero;
    }

    // 默认播放设备（带一个引用）和它的编号。没有播放设备抛 AudioLostException
    private static IntPtr DefaultDevice(IntPtr enumerator, out string id)
    {
        IntPtr device;
        // IMMDeviceEnumerator：IUnknown 的三个之后是 EnumAudioEndpoints、GetDefaultAudioEndpoint、……
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)(*(void***)enumerator)[4])(enumerator, RenderFlow, ConsoleRole, &device);
        if (hr == ElementNotFound) throw new AudioLostException("这台电脑现在没有播放设备");
        Check(hr, "取不到默认的播放设备");
        try
        {
            IntPtr text;
            // IMMDevice：Activate、OpenPropertyStore、GetId、GetState
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(void***)device)[5])(device, &text), "读不到播放设备的编号");
            id = Marshal.PtrToStringUni(text) ?? "";
            CoTaskMemFree(text);
            return device;
        }
        catch
        {
            Marshal.Release(device);
            throw;
        }
    }

    /// <summary>一次打开的环回抓取。只在抓声音的线程上用。</summary>
    private sealed class Stream : IDisposable
    {
        private IntPtr _enumerator, _client, _capture;
        private bool _started;

        public string DeviceId { get; }

        public Stream(IntPtr dataEvent)
        {
            try
            {
                Guid clsid = ClsidEnumerator, iid = IidEnumerator;
                fixed (IntPtr* p = &_enumerator) Check(CoCreateInstance(&clsid, IntPtr.Zero, ClsCtxAll, &iid, p), "建不了声音设备的枚举器");
                IntPtr device = DefaultDevice(_enumerator, out string id);
                DeviceId = id;
                try
                {
                    Guid iidClient = IidAudioClient;
                    fixed (IntPtr* p = &_client)
                        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, IntPtr, IntPtr*, int>)(*(void***)device)[3])(device, &iidClient, ClsCtxAll, IntPtr.Zero, p),
                              "打不开播放设备");
                }
                finally
                {
                    Marshal.Release(device);
                }
                var format = new WaveFormatEx
                {
                    FormatTag = 1,          // PCM
                    Channels = 2, SamplesPerSec = Proto.AudioRate, BitsPerSample = 16, BlockAlign = Proto.AudioFrameSize,
                    AvgBytesPerSec = Proto.AudioRate * Proto.AudioFrameSize,
                };
                // IAudioClient：Initialize、GetBufferSize、GetStreamLatency、GetCurrentPadding、IsFormatSupported、GetMixFormat、GetDevicePeriod、
                // Start、Stop、Reset、SetEventHandle、GetService（依次是 3~14）
                void** client = *(void***)_client;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, int, uint, long, long, WaveFormatEx*, Guid*, int>)client[3])(
                          _client, 0, StreamLoopback | StreamEventCallback | StreamAutoConvert | StreamSrcDefaultQuality, BufferHns, 0, &format, null),
                      "环回抓取开不了（要 48 kHz 双声道 16 位）");
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)client[13])(_client, dataEvent), "环回抓取登记不了事件");
                Guid iidCapture = IidCaptureClient;
                fixed (IntPtr* p = &_capture)
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)client[14])(_client, &iidCapture, p), "取不到环回抓取的读接口");
                Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)client[10])(_client), "环回抓取起不来");
                _started = true;
            }
            catch
            {
                Dispose();
                throw;
            }
            DeviceId ??= "";
        }

        /// <summary>默认播放设备还是不是打开时的那一个。</summary>
        public bool StillDefault()
        {
            IntPtr device = DefaultDevice(_enumerator, out string id);
            Marshal.Release(device);
            return id == DeviceId;
        }

        /// <summary>把系统手里攒着的包都读出来交给 sink。返回读了多少字节。</summary>
        public int Drain(AudioSink sink, SilenceGate gate, byte[] zeros)
        {
            // IAudioCaptureClient：GetBuffer、ReleaseBuffer、GetNextPacketSize
            void** capture = *(void***)_capture;
            int total = 0;
            while (true)
            {
                uint next;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)capture[5])(_capture, &next), "环回抓取断了");
                if (next == 0) return total;
                byte* data;
                uint frames, flags;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, ulong*, ulong*, int>)capture[3])(_capture, &data, &frames, &flags, null, null),
                      "环回抓取断了");
                try
                {
                    int bytes = checked((int)frames * Proto.AudioFrameSize);
                    total += bytes;
                    if ((flags & BufferSilent) != 0)
                    {
                        for (int at = 0; at < bytes; at += zeros.Length)
                        {
                            ReadOnlySpan<byte> part = zeros.AsSpan(0, Math.Min(zeros.Length, bytes - at));
                            if (gate.ShouldSend(part)) sink(part);
                        }
                    }
                    else
                    {
                        var pcm = new ReadOnlySpan<byte>(data, bytes);
                        if (gate.ShouldSend(pcm)) sink(pcm);
                    }
                }
                finally
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)capture[4])(_capture, frames);
                }
            }
        }

        public void Dispose()
        {
            if (_started) ((delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)_client)[11])(_client);
            _started = false;
            Release(ref _capture);
            Release(ref _client);
            Release(ref _enumerator);
        }
    }

    /// <summary>一次 Start：一个线程，抓到 Dispose 为止。</summary>
    private sealed class Run : IDisposable
    {
        private readonly LoopbackAudio _owner;
        private readonly AudioSink _sink;
        private readonly ManualResetEvent _quit = new(false);
        private readonly AutoResetEvent _data = new(false);
        private readonly Thread _thread;
        private int _disposed;

        public Run(LoopbackAudio owner, AudioSink sink)
        {
            _owner = owner;
            _sink = sink;
            _thread = new Thread(Loop) { IsBackground = true, Name = "wd-audio", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        private void Loop()
        {
            int com = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
            try
            {
                var gate = new SilenceGate();
                var zeros = new byte[Proto.AudioMaxBytes];
                WaitHandle[] either = [_data, _quit];
                string? reported = null;        // 最近报出去的原因；开成了清掉
                while (!_quit.WaitOne(0))
                {
                    try
                    {
                        using var stream = new Stream(_data.SafeWaitHandle.DangerousGetHandle());
                        reported = null;
                        _owner.Opened?.Invoke(stream.DeviceId);
                        long checkedAt = Environment.TickCount64;
                        while (true)
                        {
                            // 有数据时系统点 _data；没有东西在放的时候它不点，所以带着时限等，隔一阵看一眼默认设备换了没有
                            if (WaitHandle.WaitAny(either, 200) == 1) return;
                            stream.Drain(_sink, gate, zeros);
                            if (Environment.TickCount64 - checkedAt >= 1000)
                            {
                                checkedAt = Environment.TickCount64;
                                if (!stream.StillDefault()) break;      // 人换了播放设备：跟着换
                            }
                        }
                    }
                    catch (AudioLostException e)
                    {
                        if (e.Message != reported)
                        {
                            reported = e.Message;
                            _owner.Warning?.Invoke("抓不了（" + e.Message + "），每秒重开一次");
                        }
                        if (_quit.WaitOne(Retry)) return;
                    }
                }
            }
            catch (Exception e)
            {
                // 别的异常（接声音的回调里抛的、本层的代码有错）：这个线程上没有人接，放出去整个程序就没了。
                // 声音不值得这样：报一声，这条连接之后没有声音，画面照常
                _owner.Warning?.Invoke("抓声音的线程出错、停了，这条连接之后没有声音：" + e.Message);
            }
            finally
            {
                if (com >= 0) CoUninitialize();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _quit.Set();
            _thread.Join();
            _quit.Dispose();
            _data.Dispose();
        }
    }
}
