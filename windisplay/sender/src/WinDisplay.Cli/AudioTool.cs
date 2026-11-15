using System.Buffers.Binary;
using System.Diagnostics;
using WinDisplay.Core;
#if WINDOWS
using WinDisplay.Win;
#endif

namespace WinDisplay.Cli;

/// <summary>
/// 生成的声音：一个正弦波，按墙上的钟每 10 ms 给一段（第 i 段在开始之后 (i + 1) × 10 ms 给：那时这一段才「录完」），左右一样。
/// 和 windisplay/tools/wd_send_test.py 的 --tone 是同一个东西，不用声卡，Linux 开发机上也能跑。
/// </summary>
internal sealed class ToneSource(double hz, double db) : IAudioSource
{
    private const int ChunkFrames = Proto.AudioRate / 100;

    public IDisposable Start(AudioSink sink)
    {
        var quit = new ManualResetEventSlim();
        double amp = 32768 * Math.Pow(10, db / 20);
        var thread = new Thread(() =>
        {
            var clock = Stopwatch.StartNew();
            var chunk = new byte[ChunkFrames * Proto.AudioFrameSize];
            for (long i = 0; ; i++)
            {
                double wait = (i + 1) * 10 - clock.Elapsed.TotalMilliseconds;
                if (quit.Wait(wait > 0 ? (int)Math.Ceiling(wait) : 0)) return;
                for (int k = 0; k < ChunkFrames; k++)
                {
                    long n = i * ChunkFrames + k;
                    short v = (short)Math.Clamp(Math.Round(amp * Math.Sin(2 * Math.PI * hz * n / Proto.AudioRate)), short.MinValue, short.MaxValue);
                    BinaryPrimitives.WriteInt16LittleEndian(chunk.AsSpan(k * 4), v);
                    BinaryPrimitives.WriteInt16LittleEndian(chunk.AsSpan(k * 4 + 2), v);
                }
                sink(chunk);
            }
        }) { IsBackground = true, Name = "wd-tone" };
        thread.Start();
        return new Stopper(quit, thread);
    }

    private sealed class Stopper(ManualResetEventSlim quit, Thread thread) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            quit.Set();
            thread.Join();
        }
    }
}

/// <summary>wdsend 里和这台电脑的声音有关的部分（--audio、--audio-check）。只有 Windows 版带；别的版本里这两个选项报「没有」。</summary>
internal static class AudioTool
{
#if WINDOWS
    /// <summary>这台电脑正在放的声音（默认播放设备的环回）。</summary>
    public static IAudioSource? Open()
    {
        var audio = new LoopbackAudio();
        audio.Opened += _ => Console.WriteLine("声音：开始抓这台电脑正在放的声音（默认播放设备）");
        audio.Warning += why => Console.WriteLine($"声音：{why}");
        return audio;
    }

    /// <summary>
    /// 声音抓取自检：抓 seconds 秒，报抓到多少、最响处多响、hz 这个频率上多响。不连 Tab5。
    /// 返回退出码：0 = 抓到了不是全 0 的声音，1 = 没抓到（这段时间电脑上没有东西在放，或者抓不了）。
    /// </summary>
    public static int Check(double seconds, double hz)
    {
        var audio = new LoopbackAudio();
        audio.Opened += id => Console.WriteLine($"开始抓（默认播放设备 {id}）");
        audio.Warning += why => Console.WriteLine($"抓不了：{why}");
        long bytes = 0, loudBytes = 0, calls = 0;
        int peak = 0;
        double s1 = 0, s2 = 0, coeff = 2 * Math.Cos(2 * Math.PI * hz / Proto.AudioRate);     // Goertzel：左声道在 hz 上的幅度
        long frames = 0;
        double lastMs = double.NaN, maxGapMs = 0;
        var clock = Stopwatch.StartNew();
        using (audio.Start(pcm =>
               {
                   double now = clock.Elapsed.TotalMilliseconds;
                   if (!double.IsNaN(lastMs)) maxGapMs = Math.Max(maxGapMs, now - lastMs);
                   lastMs = now;
                   calls++;
                   bytes += pcm.Length;
                   bool loud = false;
                   for (int i = 0; i + 4 <= pcm.Length; i += 4)
                   {
                       short l = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]), r = BinaryPrimitives.ReadInt16LittleEndian(pcm[(i + 2)..]);
                       int a = Math.Max(Math.Abs((int)l), Math.Abs((int)r));
                       if (a > peak) peak = a;
                       loud |= a != 0;
                       double s0 = l / 32768.0 + coeff * s1 - s2;
                       s2 = s1;
                       s1 = s0;
                       frames++;
                   }
                   if (loud) loudBytes += pcm.Length;
               }))
        {
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
        }
        double Db(double amplitude) => 20 * Math.Log10(Math.Max(amplitude, 1e-9));
        double tone = frames == 0 ? 0 : 2 * Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - coeff * s1 * s2)) / frames;
        Console.WriteLine($"抓了 {seconds:G} 秒：给了 {calls} 段共 {bytes / (double)Proto.AudioBytesPerMs:F0} ms，其中不是全 0 的 {loudBytes / (double)Proto.AudioBytesPerMs:F0} ms；" +
                          $"最响处 {Db(peak / 32768.0):F1} dBFS，{hz:G} Hz 上 {Db(tone):F1} dBFS（按抓到的全长平均）；相邻两段到达的间隔最长 {maxGapMs:F1} ms");
        Console.WriteLine(loudBytes > 0 ? "声音抓取自检：抓到了" : "声音抓取自检：没抓到声音（这段时间电脑上没有东西在放，或者抓不了）");
        return loudBytes > 0 ? 0 : 1;
    }
#else
    private const string NotHere = "这一份 wdsend 不带声音抓取：--audio 和 --audio-check 要用 Windows 版（bin 下 net10.0-windows… 目录里的那份）";

    public static IAudioSource? Open()
    {
        Console.Error.WriteLine(NotHere);
        return null;
    }

    public static int Check(double seconds, double hz)
    {
        Console.Error.WriteLine(NotHere);
        return 1;
    }
#endif
}
