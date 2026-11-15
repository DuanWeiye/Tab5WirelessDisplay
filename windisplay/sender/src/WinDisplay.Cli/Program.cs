using System.Globalization;
using System.Net;
using System.Net.Sockets;
using WinDisplay.Cli;
using WinDisplay.Core;

// wdsend：命令行测试发送端。用法见 Usage()。
// 退出码（和 windisplay/tools/wd_send_test.py 一致）：0 = 正常结束（发完，或者 Tab5 那边用户退出了显示模式）；
// 1 = 没连上 / 参数不对 / 画面怎么压都超过 Tab5 的上限；2 = 连接中途断了或对端不守协议；3 = 认证没过（没配对过又没给密码、
// 密码一直不对、对面没有配对密钥）；130 = 没发完就被 Ctrl-C 打断了。

// 数字的读和写都用固定的格式（小数点是「.」），不跟着系统的区域设置变：参数和输出要给脚本用
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length > 0 && args[0] == "--scene") return SceneTool.Run(args[1..]);

string? host = null;
int port = Proto.Port, quality = 60, stripRows = 160;
int motionKb = new SessionOptions().MotionFrameBytes / 1024, minQuality = new SessionOptions().MinQuality;
int maxInFlight = FlowControl.DefaultMaxInFlight, steadyInFlight = 0;
bool pictureGiven = false;          // 给了 --quality 或 --motion-kb：画质按命令行的固定，不听 Tab5 的
bool inflightGiven = false;         // 没给 --inflight：取默认的 2，但不超过 maxInFlight
long frames = -1;           // -1 = 没给：测试画面发 200 帧，抓屏不限。0 = 不限
double fps = 0, detail = 0, ackWait = FlowControl.DefaultMinAckWaitMs, spareHold = FlowControl.DefaultSpareHoldMs, seconds = 0;
string? pattern = null;
bool screen = false, screenCheck = false, input = false, injectCheck = false, forget = false, stay = false;
bool audio = false, audioCheck = false;
double tone = 0, toneDb = -6;       // --tone：生成的正弦波的频率（0 = 没给）和幅度
string? dumpLast = null, password = null, pairStorePath = null, name = null;
IPAddress? askAt = null;    // --broadcast：auto 时往哪个地址问；null = 每块连着的网卡的广播地址
int kbdLeds = -1;
var subsampling = ChromaSubsampling.S420;
bool subsamplingGiven = false;
bool rgb565 = false;                // --rgb565：不要 YUV422 帧缓冲。--subsampling 给了 4:2:2 以外的也不要（YUV422 帧缓冲只收 4:2:2），解析完再合起来
bool verbose = false;
SessionOptions options;

try
{
    for (int i = 0; i < args.Length; i++)
    {
        string a = args[i];
        string Next() => i + 1 < args.Length ? args[++i] : throw new FormatException($"{a} 后面缺一个值");
        int Int() => Next() is var s && int.TryParse(s, out int v) ? v : throw new FormatException($"{a} 的值 {s} 不是整数，或者太大了");
        long Long() => Next() is var s && long.TryParse(s, out long v) ? v : throw new FormatException($"{a} 的值 {s} 不是整数，或者太大了");
        double Real() => Next() is var s && double.TryParse(s, out double v) && double.IsFinite(v) ? v : throw new FormatException($"{a} 的值 {s} 不是数");
        switch (a)
        {
            case "--port": port = Int(); break;
            case "--pattern": pattern = Next(); break;
            case "--frames": frames = Long(); break;
            case "--seconds": seconds = Real(); break;
            case "--screen": screen = true; break;
            case "--screen-check": screenCheck = true; break;
            case "--input": input = true; break;
            case "--inject-check": injectCheck = true; break;
            case "--audio": audio = true; break;
            case "--audio-check": audioCheck = true; break;
            case "--tone": tone = Real(); if (tone is < 20 or > 20000) throw new FormatException("--tone 要在 20~20000 赫兹"); break;
            case "--tone-db": toneDb = Real(); if (toneDb is > 0 or < -90) throw new FormatException("--tone-db 要在 -90~0"); break;
            case "--password": password = Next(); break;
            case "--pair-store": pairStorePath = Next(); break;
            case "--forget": forget = true; break;
            case "--stay": stay = true; break;
            case "--broadcast":
                askAt = Next() is var b && IPAddress.TryParse(b, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork
                    ? parsed : throw new FormatException($"{a} 的值 {b} 不是 IPv4 地址");
                break;
            case "--name": name = Next(); break;
            case "--fps": fps = Real(); break;
            case "--detail": detail = Real(); if (detail is < 0 or > 1) throw new FormatException("--detail 要在 0~1"); break;
            case "--quality": quality = Int(); pictureGiven = true; break;
            case "--motion-kb": motionKb = Int(); pictureGiven = true; if (motionKb is < 0 or > 1024) throw new FormatException("--motion-kb 要在 0~1024"); break;
            case "--min-quality": minQuality = Int(); break;
            case "--strip-h": stripRows = Int(); break;
            case "--inflight": steadyInFlight = Int(); inflightGiven = true; break;
            case "--max-inflight": maxInFlight = Int(); if (maxInFlight < 1) throw new FormatException("--max-inflight 至少是 1"); break;
            case "--ack-wait": ackWait = Real(); break;
            case "--spare-hold": spareHold = Real(); break;
            case "--subsampling":
                subsamplingGiven = true;
                subsampling = Next() switch
                {
                    "420" => ChromaSubsampling.S420,
                    "422" => ChromaSubsampling.S422,
                    "444" => ChromaSubsampling.S444,
                    var s => throw new FormatException($"--subsampling 只能是 420 / 422 / 444，不是 {s}"),
                };
                break;
            case "--rgb565": rgb565 = true; break;
            case "--dump-last": dumpLast = Next(); break;
            case "--kbd-leds": kbdLeds = Int(); break;
            case "-v" or "--verbose": verbose = true; break;
            case "-h" or "--help": Usage(); return 0;
            default:
                if (a.StartsWith('-') || host is not null) throw new FormatException($"不认识的参数 {a}");
                host = a;
                break;
        }
    }
    if (screenCheck || injectCheck)
    {
        // 别的选项对它们都没有意义；一起给了就报错，不悄悄忽略
        if (args.Length != 1)
            throw new FormatException($"{(screenCheck ? "--screen-check" : "--inject-check")} 单独用，不带别的参数：它不连 Tab5，只核对这台电脑的{(screenCheck ? "抓屏" : "输入注入")}");
    }
    else if (audioCheck)
    {
        if (host is not null || args.Any(a => a.StartsWith('-') && a is not ("--audio-check" or "--seconds" or "--tone")))
            throw new FormatException("--audio-check 不连 Tab5，只核对这台电脑的声音抓取：只能再带 --seconds（抓多久，默认 5）和 --tone（量哪个频率，默认 1000）");
    }
    else if (host is null) throw new FormatException("缺 Tab5 的地址（不知道地址就写 auto：在局域网里广播询问）");
    if (audio && tone != 0) throw new FormatException("--audio（这台电脑正在放的声音）和 --tone（生成的正弦波）只能给一个");
    if (stay && host != "auto") throw new FormatException("--stay（一直自动连接）只能和 auto 一起用");
    if (askAt is not null && host != "auto") throw new FormatException("--broadcast（往哪个地址问）只能和 auto 一起用");
    if (screen && pattern is not null) throw new FormatException("--screen 和 --pattern 只能给一个");
    if (password is not null && !PairingSecret.IsValidPassword(password))
        throw new FormatException("--password：配对密码要 1~64 个字符，只能用英文字母、数字、空格和键盘上的英文符号（Tab5 上只打得出这些）");
    if (name is { Length: 0 }) throw new FormatException("--name 不能是空的");
    if (screen && dumpLast is not null) throw new FormatException("抓屏的画面不存盘：--dump-last 只能和测试画面一起用");
    if (screen && fps != 0) throw new FormatException("--fps 只管测试画面；抓屏是屏幕变了才有新画面");
    if (input && !screen) throw new FormatException("--input（把 Tab5 上的触摸和按键注入这台电脑）要和 --screen 一起用：触摸的位置是按抓到的屏幕换算的");
    if (input && kbdLeds >= 0) throw new FormatException("--input 会把这台电脑的键盘指示灯状态发给 Tab5，不能再给 --kbd-leds");
    pattern ??= "card";
    if (pattern is not ("card" or "scroll" or "noise")) throw new FormatException($"--pattern 只能是 card / scroll / noise，不是 {pattern}");
    if (frames < -1) throw new FormatException("--frames 不能是负数");
    if (seconds < 0) throw new FormatException("--seconds 不能是负数");
    if (frames == -1) frames = screen ? 0 : 200;
    if (kbdLeds is < -1 or > 7) throw new FormatException("--kbd-leds 只能是 0~7");
    if (port is < 1 or > 65535) throw new FormatException($"--port 要在 1~65535，不是 {port}");
    // 只给了 --max-inflight 1（死等 ACK 的对照）时，平时的在途帧数跟着降到 1
    if (!inflightGiven) steadyInFlight = Math.Min(FlowControl.DefaultSteadyInFlight, maxInFlight);
    else if (steadyInFlight < 1 || steadyInFlight > maxInFlight)
        throw new FormatException($"--inflight 要在 1 和 --max-inflight（{maxInFlight}）之间，不是 {steadyInFlight}");
    options = new SessionOptions
    {
        Quality = quality, StripRows = stripRows, Subsampling = subsampling,
        Yuv422 = !rgb565 && (!subsamplingGiven || subsampling == ChromaSubsampling.S422),
        MotionFrameBytes = motionKb * 1024, MinQuality = minQuality, FollowDevicePicture = !pictureGiven,
        SteadyInFlight = steadyInFlight, MaxInFlight = maxInFlight, MinAckWaitMs = ackWait, SpareHoldMs = spareHold,
    }.Validated();
}
catch (Exception e) when (e is FormatException or ArgumentException)     // ArgumentException：SessionOptions 里哪一项越界了
{
    Console.Error.WriteLine(e.Message);
    Usage();
    return 1;
}

if (screenCheck) return ScreenTool.Check();
if (injectCheck) return InputTool.Check();
if (audioCheck) return AudioTool.Check(seconds > 0 ? seconds : 5, tone != 0 ? tone : 1000);

// 配对存储（自己的编号 + 配对过的 Tab5）和配对密码：给了 --password 才能配对
LinkAuth auth;
try
{
    var store = new PairStore(pairStorePath ?? DefaultPairStore());
    if (forget) store.ForgetAll();
    PairingGate? pairing = password is null ? null : new PairingGate(PairingSecret.FromPassword(password));
    auth = new LinkAuth(store, name ?? Environment.MachineName, pairing);
}
catch (Exception e) when (e is InvalidDataException or PairStoreException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"配对存储用不了：{e.Message}");
    return 1;
}

TestPatternSource? patternSource = null;
IFrameSource source;
CaptureSource? capture = null;
if (input) InputTool.Prepare();         // 在打开抓屏之前
if (screen)
{
    if (ScreenTool.Open(verbose) is not { } opened) return 1;
    source = capture = opened;
    if (frames > 0 || seconds > 0) source = new LimitedSource(source, frames, seconds);     // LimitedSource 里 0 = 这一项不限
}
else
{
    source = patternSource = new TestPatternSource(pattern, frames, fps, detail);
    if (seconds > 0) source = new LimitedSource(source, 0, seconds);
}
using var captureGuard = capture;       // 会话的 Run 返回之后才关抓屏
// 声音：这台电脑正在放的（--audio），或者生成的正弦波（--tone）；Tab5 说能收才发（会话管）
IAudioSource? sound = null;
if (audio && (sound = AudioTool.Open()) is null) return 1;
if (tone != 0) sound = new ToneSource(tone, toneDb);
var session = new DisplaySession(source, options, auth, sound);
if (kbdLeds >= 0) session.SetKeyboardLeds((byte)kbdLeds);       // 会话连上后先把它发出去
var stats = new List<FrameStats>();
session.Pairing += step => Console.WriteLine(step switch
{
    PairingStep.WaitingForPassword => "配对：等 Tab5 上输入密码",
    PairingStep.WrongPassword => "配对：Tab5 上输的密码不对",
    _ => "配对：密码对上了",
});
session.Connected += h =>
{
    Console.WriteLine($"认证通过，连上了 {h.Name}：协议 {h.Version}，帧缓冲 {h.FbW}×{h.FbH}，画面 {h.ViewW}×{h.ViewH}，" +
                      $"单条带上限 {h.MaxStripBytes}，单帧上限 {h.MaxFrameBytes} 字节");
    if (sound is not null && (h.Caps & Proto.CapDevAudio) == 0) Console.WriteLine("Tab5 没说能收声音（HELLO 的能力位），声音不发");
};
session.FrameAcked += f =>
{
    lock (stats) stats.Add(f);
    if (verbose)
        Console.WriteLine($"帧 {f.Seq}：{f.Strips} 个条带 {f.Bytes / 1024.0:F1} KB，编码 {f.EncodeMs:F1} ms，发出到 ACK {f.RttMs:F1} ms" +
                          $"（Tab5：接收 {f.Ack.RecvUs / 1e3:F1} 解码 {f.Ack.DecodeUs / 1e3:F1} 上屏 {f.Ack.PresentUs / 1e3:F1} ms）；" +
                          // 这一行是收到 ACK 的时候打的，往回减掉「发出到 ACK」就是发出的时刻
                          $"发于 @{ScreenTool.NowMs() - f.RttMs:F1}，离上一帧 {f.SendGapMs:F1} ms（等确认 {f.WaitMs:F1} 等画面 {f.FetchMs:F1}）；质量 {f.Quality}" +
                          // 新加的项只往行尾追加：wd_stutter.py 按前面的格式解析这一行
                          $"，预算 {(f.BudgetBytes == 0 ? "不降" : $"{f.BudgetBytes / 1024.0:F0} KB")}，{(!f.Motion ? "不算在动" : f.KeptUp ? "跟上" : "没跟上")}");
};
session.Touch += pts => Console.WriteLine("  ← TOUCH " + (pts.Length == 0 ? "全部抬起" : string.Join(' ', pts.Select(p => $"#{p.Id}({p.X},{p.Y})"))));
session.Key += report => Console.WriteLine("  ← KEY " + string.Join(' ', report.Select(b => b.ToString("x2"))));
session.Mouse += m => Console.WriteLine($"  ← MOUSE dx={m.Dx} dy={m.Dy} wheel={m.Wheel} buttons=0x{m.Buttons:x2}");
session.InputMode += pad => Console.WriteLine("  ← INPUT_MODE " + (pad ? "触摸板" : "触摸"));     // Tab5 上选的触摸方式
session.TouchpadGain += gain => Console.WriteLine(FormattableString.Invariant($"  ← TPAD_GAIN {gain:F1} 倍"));     // Tab5 上选的触摸板加速
int pictures = 0;                   // 这次运行里 Tab5 报过几次画质
int tierNow = -1, periodUsNow = 0;  // Tab5 最近报的是第几档、屏幕的帧周期（微秒）；-1 = 没报过档位（老固件报的是质量和预算，或者画质按命令行的固定）
session.Picture += (q, kb) =>       // 老固件：Tab5 上选的画质折成的质量和预算；之后的帧按它压，结尾的小结也按它报预算
{
    Volatile.Write(ref motionKb, kb);
    Volatile.Write(ref tierNow, -1);
    Interlocked.Increment(ref pictures);
    Console.WriteLine($"  ← PICTURE 质量 {q}，预算 {(kb == 0 ? "不降" : kb + " KB")}");
};
session.PictureMode += (tier, periodUs) =>      // Tab5 上选的画质是第几档；之后限不限帧率、预算怎么调都按这一档
{
    Volatile.Write(ref periodUsNow, periodUs);
    Volatile.Write(ref tierNow, (int)tier);
    Interlocked.Increment(ref pictures);
    Console.WriteLine($"  ← PICTURE_MODE 第 {(int)tier} 档「{PictureTiers.Name(tier)}」，屏幕的帧周期 {periodUs} 微秒");
};
session.Held += () => Console.WriteLine("  ← HELD");        // Tab5 上有键 / 手指按着的时候每 0.2 秒一个

// 注入排在上面那几行打印之后登记：日志里先是「← 收到了什么」，再是「→ 注入了什么」
InputPump? inputs = null;
if (input && (inputs = InputTool.Attach(session, verbose)) is null) return 1;
using var inputGuard = inputs;          // 先于抓屏关掉（按着的都松开）
// 跑一条连接。连接结束等于所有键、所有触点都松开（Tab5 不会补发）：注入进去还按着的要自己放掉
// found：按自动发现的应答去连的那一台（HELLO 里的编号要和应答里的一样）；手填地址时没有
SessionResult RunOnce(string address, int tcpPort, CancellationToken ct, DiscoveredTab5? found = null)
{
    try
    {
        return found is null ? session.Run(address, tcpPort, ct) : session.Run(found, ct);
    }
    finally
    {
        inputs?.ReleaseAll();
    }
}

// auto 时的询问：发到每块连着的网卡的广播地址，给了 --broadcast 就只发到那一个地址
int askPort = port;
LanDiscovery Discovery(TimeSpan? wait = null) =>
    new(askPort, wait, askAt is null ? null : () => [new DiscoveryRoute(null, new IPEndPoint(askAt, askPort))]);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
SessionResult r;
DiscoveredTab5? picked = null;          // auto：广播问到、挑中的那一台
try
{
    if (stay)
    {
        // 一直自动连接（托盘程序做的就是这件事，这里没有界面）：问到 Tab5 就连，断了再问，直到 Ctrl-C 或者画面发完
        var auto = new AutoConnector(Discovery(), auth.Store, auth.Pairing, (tab5, ct) => RunOnce(tab5.Address.ToString(), tab5.Port, ct, tab5));
        string? last = null;
        auto.Changed += e =>
        {
            string line = e switch
            {
                AutoEvent.NothingToConnect { Found: 0 } => "没有 Tab5 应答，隔 3 秒再问（Tab5 上要开着「无线显示」页）",
                AutoEvent.NothingToConnect n => $"应答的 {n.Found} 台 Tab5 都没配对过，而现在不能配对（没给 --password，或者密码不对记满了次数）",
                AutoEvent.Connecting c => $"连 {c.Target}，{(c.Paired ? "配对过" : "没配对过：要在 Tab5 上输密码")}",
                AutoEvent.Ended d => $"连接结束（{EndText(d.Result)}）：显示了 {d.Result.FramesAcked} 帧，{d.Result.Elapsed.TotalSeconds:F1} 秒；" +
                                     (d.RetryIn > TimeSpan.Zero ? $"{d.RetryIn.TotalSeconds:F0} 秒后再找" : "马上再找"),
                _ => e.ToString(),
            };
            if (line != last) Console.WriteLine(line);      // 没有 Tab5 应答时每 3 秒一轮，同样的话只说一遍
            last = line;
        };
        auto.Run(cts.Token);
        Console.WriteLine(cts.IsCancellationRequested ? "按了 Ctrl-C，结束" : "画面发完");
        return cts.IsCancellationRequested ? 130 : 0;
    }
    if (host == "auto")
    {
        // 广播询问一次：一轮收 1 秒，有应答就挑一台，最多问 3 轮（和 wd_send_test.py 的 auto 一样）
        var discovery = Discovery(TimeSpan.FromSeconds(1));
        IReadOnlyList<DiscoveredTab5> found = [];
        for (int round = 0; round < 3 && found.Count == 0 && !cts.IsCancellationRequested; round++) found = discovery.Query(cts.Token);
        if (cts.IsCancellationRequested) return 130;
        if (found.Count == 0)
        {
            Console.Error.WriteLine("广播询问没有 Tab5 应答（Tab5 上要开着「无线显示」页，而且正在等连接）");
            return 1;
        }
        var usable = AutoConnector.Pick(found, auth.Store, auth.Pairing?.CanPair == true);
        if (usable.Count == 0)
        {
            Console.Error.WriteLine($"应答的 {found.Count} 台 Tab5 都没配对过：要配对就给 --password");
            return 1;
        }
        Console.WriteLine($"{found.Count} 台 Tab5 应答，连 {usable[0]}");
        picked = usable[0];
        (host, port) = (picked.Address.ToString(), picked.Port);
    }
    r = RunOnce(host!, port, cts.Token, picked);           // 走到这里 host 一定有：只有 --screen-check 可以不给，而它上面已经返回了
}
catch (InvalidOperationException e)     // 编码器：画面怎么压都超过 Tab5 报的上限。连接已经收干净了
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
catch (PairStoreException e)            // 握手走完了，配对却存不进文件。连接已经收干净了
{
    Console.Error.WriteLine($"配对存不下来：{e.Message}");
    return 1;
}
catch (IOException e)                   // 上面那些回调里往终端 / 管道打印时出的错（比如输出接的管道被对面关了）
{
    Console.Error.WriteLine($"输出写不出去：{e.Message}");
    return 1;
}

if (dumpLast is not null && patternSource is { Last.Length: > 0 })
{
    unsafe
    {
        fixed (byte* p = patternSource.Last)
        {
            using var pixmap = new SkiaSharp.SKPixmap(
                new SkiaSharp.SKImageInfo(Proto.ViewW, Proto.ViewH, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque), (IntPtr)p, Proto.ViewW * 4);
            using var png = pixmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            if (png is null) Console.Error.WriteLine("--dump-last：最后一帧压 PNG 失败");
            else File.WriteAllBytes(dumpLast, png.ToArray());
        }
    }
}

Console.WriteLine(EndText(r));
if (stats.Count > 0)
{
    double sec = r.Elapsed.TotalSeconds;
    long bytes = stats.Sum(f => (long)f.Bytes);
    Console.WriteLine($"== {(screen ? "抓屏" : pattern)}，质量 {r.Quality}，{(r.Yuv422 ? "YUV422 帧缓冲（422，有限范围）" : $"RGB565 帧缓冲（{subsampling.ToString()[1..]}）")}，条带高 {stripRows} ==");
    Console.WriteLine($"发了 {r.FramesAcked} 帧，用时 {sec:F2} 秒，{r.FramesAcked / sec:F1} 帧/秒，{bytes * 8 / sec / 1e6:F2} Mbps" +
                      (r.FramesSent != r.FramesAcked ? $"（另有 {r.FramesSent - r.FramesAcked} 帧发出后没等到 ACK）" : ""));
    Console.WriteLine($"每帧：{stats.Average(f => f.Strips):F1} 个条带，平均 {bytes / (double)stats.Count / 1024:F1} KB，最大 {stats.Max(f => f.Bytes) / 1024.0:F1} KB");
    // 各帧的质量不都一样：画面在动时按预算降过，或者画质听 Tab5 的、连接中途换过（报来之前的头一两帧是默认的 60）
    if (stats.Min(f => f.Quality) != stats.Max(f => f.Quality))
        Console.WriteLine($"各帧的质量：{stats.Min(f => f.Quality)}~{stats.Max(f => f.Quality)}，平均 {stats.Average(f => f.Quality):F0}；" +
                          $"按最后的设定 {r.Quality} 发的 {stats.Count(f => f.Quality == r.Quality)} 帧" +
                          (tierNow >= 0 ? "" : $"（画面在动时每帧预算 {(motionKb == 0 ? "不降" : motionKb + " KB")}") +
                          (tierNow >= 0 ? "" : pictures > 0 ? $"；画质听 Tab5 的，它报过 {pictures} 次）" : "）"));
    if (tierNow >= 0)
    {
        // 按档位的连接：限到多少帧、预算（闭环调的）停在哪、在动的帧里跟上的占多少
        var tier = (PictureTier)tierNow;
        TierPlan plan = PictureTiers.Plan(tier);
        var moving = stats.Where(f => f.Motion).ToList();
        Console.WriteLine($"画质第 {tierNow} 档「{PictureTiers.Name(tier)}」（听 Tab5 的，它报过 {pictures} 次；屏幕的帧周期 {periodUsNow} 微秒）：" +
                          (plan.FramesPerRefresh > 0 ? $"限 {1e6 / periodUsNow * plan.FramesPerRefresh:F1} 帧/秒" : "不限帧率") + "，" +
                          (plan.MaxBytes == 0 ? "画面在动也不降质量"
                           : $"画面在动时每帧预算在 {plan.MinBytes / 1024}~{plan.MaxBytes / 1024} KB 里调" +
                             (moving.Count == 0 ? "（没有在动的帧）"
                              : $"：用过 {moving.Min(f => f.BudgetBytes) / 1024.0:F0}~{moving.Max(f => f.BudgetBytes) / 1024.0:F0} KB，最后 {moving[^1].BudgetBytes / 1024.0:F0} KB；" +
                                $"在动的 {moving.Count} 帧里跟上的 {moving.Count(f => f.KeptUp)} 帧")));
    }
    Console.WriteLine($"编码：{Stat(stats.Select(f => f.EncodeMs))}");
    Console.WriteLine($"相邻两帧发出的间隔：{Stat(stats.Skip(1).Select(f => f.SendGapMs))}");
    // 按档位的连接平时几帧在途看档位（均衡档是 3 帧，见 PictureTiers）
    if (tierNow >= 0) steadyInFlight = Math.Min(maxInFlight, Math.Max(steadyInFlight, PictureTiers.Plan((PictureTier)tierNow).InFlight));
    Console.WriteLine($"确认迟到、先发下一帧：{r.LateSends} 次（平时 {steadyInFlight} 帧在途，迟到时最多 {maxInFlight} 帧，至少等 {ackWait:G} ms）");
    Console.WriteLine(spareHold > 0 && maxInFlight > steadyInFlight
        ? $"跟得上画面源、多放一帧在途：{r.SpareSends} 次（跟上过之后 {spareHold:G} ms 之内）" : "跟得上画面源、多放一帧在途：关着");
    Console.WriteLine($"发出到 ACK：{Stat(stats.Select(f => f.RttMs))}");
    Console.WriteLine($"Tab5 接收：{Stat(stats.Select(f => f.Ack.RecvUs / 1e3))}；解码：{Stat(stats.Select(f => f.Ack.DecodeUs / 1e3))}；" +
                      $"上屏：{Stat(stats.Select(f => f.Ack.PresentUs / 1e3))}");
}
if (sound is not null)
    Console.WriteLine($"声音：发了 {r.AudioSentMs / 1000:F1} 秒，连接堵住时扔掉 {r.AudioDroppedMs:F0} ms");
if (screen) ScreenTool.PrintStats(r.Elapsed);
return r.End switch
{
    SessionEnd.Completed or SessionEnd.DeviceBye => 0,
    SessionEnd.Cancelled => 130,
    SessionEnd.ConnectFailed => 1,
    SessionEnd.AuthFailed => 3,
    _ => 2,
};

// 一条连接是怎么结束的（给人看的一句话）
static string EndText(SessionResult r) => r.End switch
{
    SessionEnd.Completed => "画面发完",
    SessionEnd.Cancelled => "按了 Ctrl-C，结束",
    SessionEnd.DeviceBye => r.FramesSent > 0 ? "Tab5 退出了显示模式（BYE）" : "Tab5 那边的人结束了连接（BYE）",
    SessionEnd.Dropped => $"连接中途断了：{r.Detail}",
    SessionEnd.ConnectFailed => $"没连上：{r.Detail}",
    SessionEnd.AuthFailed => $"认证没过：{r.Detail}" + r.AuthFailure switch
    {
        AuthFailure.NotPaired or AuthFailure.Forgotten => "（给 --password 配对，在 Tab5 上输同一个密码）",
        AuthFailure.KeyMismatch => "（对面可能不是那台 Tab5；确认是它的话，用 --forget --password 重新配对）",
        _ => "",
    },
    _ => $"对端不守协议：{r.Detail}",
};

// 命令行工具自己的一份，明文 JSON（和 Python 工具的格式一样）。托盘程序的配对另存、交给 DPAPI，不和它共用
static string DefaultPairStore() =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDisplay", "wdsend-pairings.json");

// 平均 / 中位 / 95% / 最大。比两种跑法时看中位数：无线上偶尔丢一个包要等 TCP 重传，一次就停一秒左右，平均数会被它带偏
static string Stat(IEnumerable<double> values)
{
    double[] v = [.. values.Order()];
    if (v.Length == 0) return "无数据";
    double median = v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    return $"平均 {v.Average():F1} / 中位 {median:F1} / 95% {v[Math.Min(v.Length - 1, (int)(v.Length * 0.95))]:F1} / 最大 {v[^1]:F1} ms";
}

static void Usage() => Console.Error.WriteLine("""
    用法：wdsend <Tab5 地址 | auto> [选项]
          wdsend --screen-check        不连 Tab5，只核对这台电脑的抓屏对不对（Windows 版才有）
          wdsend --audio-check         不连 Tab5，只核对这台电脑的声音抓不抓得到（Windows 版才有）：抓 --seconds 秒（默认 5），
                                       报抓到多少、多响、--tone 那个频率（默认 1000 赫兹）上多响。电脑上这段时间得有东西在放
          wdsend --inject-check        不连 Tab5，只核对往这台电脑里注入触摸和按键对不对（Windows 版才有）。它会开一个小窗口、
                                       把焦点抢过去、往里面点几下打几个字，所以只在电脑空闲（10 秒没人动键盘鼠标）、没锁屏时跑
      auto                不填地址：在局域网里广播询问，连应答的那台（配对过的优先；没配对过的要给 --password 才连）
      --stay              和 auto 一起用：一直自动连接——问到 Tab5 就连，断了再问，直到 Ctrl-C 或者画面发完（--frames / --seconds）
      --broadcast 地址    和 auto 一起用：只往这个地址问（默认每块连着的网卡各往自己网段的广播地址问）
      --port N            端口（默认 7250；auto 时是广播询问用的端口）
      --pattern P         发生成的测试画面：card（默认，局部更新）/ scroll（整屏滚动）/ noise（噪点）
      --screen            发这台电脑主显示器的画面（Windows 版才有；要在登录用户的桌面会话里跑）
      --input             和 --screen 一起用：把 Tab5 上的触摸、键盘模块的按键注入这台电脑（按键按美式键盘的扫描码；触摸照 Tab5 上选的方式：
                          注入成触摸，或者设置页上的「触摸板模式」开着时把屏幕当触摸板、换成鼠标动作），
                          这台电脑的键盘指示灯状态发回 Tab5。前台是权限更高的窗口（UAC 提示、锁屏）时注入不进去
      --audio             把这台电脑正在放的声音也发给 Tab5（Windows 版才有；电脑本机照常出声）。Tab5 没说能收声音就不发
      --tone HZ           不用声卡，发一个生成的正弦波（HZ 赫兹，10 ms 一段）；--tone-db DB 是幅度（相对满幅的分贝数，默认 -6，夜里测给 -50）
      --password P        配对密码：没和这台 Tab5 配对过（或者它那边没有这把密钥了）就用它配对，在 Tab5 上输同一个；
                          配对过的直接认证，给不给都一样。没给又要配对：退出码 3
      --pair-store 文件   自己的编号和配对过的 Tab5 存在哪（默认在用户目录下的 WinDisplay/wdsend-pairings.json；里面是密钥）
      --forget            先忘掉所有配对
      --name 名字         报给 Tab5 的名字，它的配对列表里显示（默认机器名）
      --frames N          发多少帧，0 = 不限，到 Ctrl-C 或 --seconds 为止（测试画面默认 200；抓屏默认不限）
      --seconds S         最多跑这么多秒（默认不限）
      --fps F             测试画面限帧率；0 = 能多快就多快（默认）
      --detail D          测试画面上叠一层细碎纹理，强度 0~1（默认 0 = 不叠）：越大每帧压出来越大，--pattern scroll 配它就像整屏滚动满是小字的网页
      --quality Q         JPEG 质量 1~100
      --motion-kb N       画面在动时每帧的字节数预算（KB）：大片都在变的帧按它降质量，不再大片地动了就按 --quality 补清晰；0 = 不降
                          这两项都没给：听 Tab5 的（人在主页面上选的画质那一档：最佳画质 / 高画质 / 均衡 / 最佳速度，
                          限不限帧率、画面在动时的预算怎么调都按档位来，见 PROTOCOL.md「画质」；老固件报的是质量和预算，照它的固定；更老的不报，按 60 / 40 KB）。
                          给了任何一项：按命令行的固定（没给的那一项取 60 / 40 KB；不限帧率），Tab5 上怎么选都不管
      --min-quality Q     画面在动时质量最低降到多少（默认 20）
      --rgb565            不走 YUV422 帧缓冲，照旧走 RGB565（对照用）。不给：Tab5 能走 YUV422 就走（条带固定 4:2:2、画面压到有限范围）
      --subsampling S     走 RGB565 时的色度采样 420（默认）/ 422 / 444。给 420 / 444 等于同时给了 --rgb565
      --strip-h N         条带高（帧缓冲的行数，16 的倍数，默认 160）
      --inflight N        平时保持几帧在途（默认 2，没给时不超过 --max-inflight；1 = 平时等到 ACK 才发下一帧，ACK 迟到时照样多发）
      --max-inflight N    最早在途的那一帧的 ACK 迟到时，最多放到几帧在途（默认 4；1 = 死等 ACK；和 --inflight 一样大时 --spare-hold 也不起作用）
      --ack-wait MS       最早在途的那一帧发出后至少等这么多毫秒的 ACK，等不到才算迟到（默认 60）
      --spare-hold MS     跟上过画面源之后这么多毫秒之内，在途的到了 --inflight 帧还可以多发一帧（默认 500；0 = 不多发，见 PROTOCOL.md「流量控制」）
      --dump-last 文件    把最后给出的那一帧存成 PNG（和模拟接收端收到的画面对比用）
      --kbd-leds N        连上后先发键盘指示灯状态（位：1 = Num，2 = Caps，4 = Scroll）
      -v                  每帧打印一行（@ 后面的时刻是开机以来的毫秒，--scene 打的时刻是同一个钟）；有 --screen 时每读走一幅屏幕也打印一行；
                          有 --input 时每次注入也打印一行
          wdsend --scene [选项]        不连 Tab5：在主显示器上开一个全屏的测试场景（整屏都在动，像游戏；Windows 版才有），到时间自己关，
                                       点鼠标 / 按任意键也关（从计划任务起的不一定拿得到键盘焦点，点鼠标一定行）。和另一个 wdsend --screen 同时跑，用来量屏幕上有游戏那样的画面时投屏卡不卡
            --seconds S       跑多少秒（默认 30）
            --gpu N           每个像素额外多算 N 遍：占显卡（默认 0）
            --cpu N           另开 N 个空转的线程：占 CPU（默认 0）
            --vsync 1|0       等不等垂直同步（默认 1；0 = 能画多快画多快，显卡一直满载）
            --detail D        画面细碎纹理的强度 0~1（默认 0.05，1920×1080 的屏上每帧压出来约 70 KB；越大每帧越大）
            --idle S          电脑上最近 S 秒内有人动过键盘鼠标就不开（默认 60；0 = 不管）
            --check-idle      只按 --idle 查这台电脑有没有人在用，不开场景：退出码 0 = 没人在用，4 = 有人在用
    """);
