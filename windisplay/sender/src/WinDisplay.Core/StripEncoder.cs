using System.Runtime.ExceptionServices;
using SkiaSharp;

namespace WinDisplay.Core;

/// <summary>一个条带：帧缓冲里的起始行 + 一张 JPEG。</summary>
public readonly record struct Strip(int Row, byte[] Jpeg);

/// <summary>
/// 把横屏画面变成要发给 Tab5 的条带（PROTOCOL.md「画面的几何关系」）：
/// 逆时针转 90° 成帧缓冲的排列（720×1280）→ 和 Tab5 上已有的那一帧逐条带比较 → 变了的条带压成基线 JPEG。
/// 走 YUV422 帧缓冲的连接（协议「画面的色彩」）：转的那一遍顺手把三个通道压到有限范围（16~235），采样固定 4:2:2；
/// 比较「变没变」用的是压过范围之后的缓冲（两帧都压过，结果一样）。
/// 条带的划分每帧一致（默认每 160 行一条 = 横屏画面里 160 像素宽的一个竖条），Tab5 那边最省事。
/// 单个条带超过 Tab5 报的上限就对半拆；一帧超过上限就降质量重压。
/// 用法：Encode() 得到条带 → 发出去 → Commit()（发成功了才算 Tab5 有了这一帧；没发成不 Commit，下次照样和旧的比）。
///
/// 画面在动时压小（给了 motionBytes 才有）：大片都在变的一帧（变了的超过四分之一屏）按「每帧不超过 motionBytes 字节」来定质量——
/// 上一帧超了，这一帧质量就往下调（不低于 minQuality），小了再往回升；一帧只压一遍，所以是跟着上一帧调的，画面突变的那一帧可能超。
/// 为什么要压小：Tab5 一个帧周期（15.3 ms）里收得完、解得完的帧才能一个帧周期出一帧，链路每秒只有 3 MB 上下。
/// 只变了一小块的帧（打字、光标）本来就小，照原质量压。
/// 降过质量的那些地方，画面不再大片地动了之后补一遍：Encode() 带上 refine，这一帧（不算在动的话）就把这些条带也按原质量压进去。
/// 什么时候算「不再大片地动了」由调用方定（它有时钟）：有一阵没出过在动的帧了（LastWasMotion）。不能等「来源说画面没变」：
/// 整屏停了、还有一小块一直在变（动图、转圈、进度条）的画面永远等不到。
///
/// 一帧里各条带分到几个核上同时压（它们互不相干）。对象本身不是线程安全的：Encode / Commit 要在同一个线程上一个接一个地调。
/// </summary>
public sealed unsafe class StripEncoder
{
    private const int Bpp = 4;
    private const int FbStride = Proto.FbW * Bpp;
    private const int FbBytes = FbStride * Proto.FbH;

    private const int Units = Proto.FbH / Proto.StripAlign;       // 帧缓冲按 16 行一格分成 80 格
    private static readonly byte[] LimitedLut = [.. Enumerable.Range(0, 256).Select(Proto.ToLimited)];      // 0~255 → 16~235
    private const int FloorQuality = 10;          // 一帧超过单帧上限时质量最低降到这里，再超就报错

    private readonly int _stripH, _maxStripBytes, _maxFrameBytes, _minQuality;
    private int _motionBytes;                     // 画面在动时每帧的字节数预算；0 = 不降
    private readonly SKJpegEncoderDownsample _downsample;
    private readonly SKColorType _colorType;
    private readonly bool _limited;               // 走 YUV422 帧缓冲的连接：画面压到有限范围
    private byte[] _cur = new byte[FbBytes];      // 这一帧（帧缓冲的排列）
    private byte[] _sent = new byte[FbBytes];     // Tab5 上已有的那一帧
    private bool _haveSent;                       // false = Tab5 上还没有我们的画面，下一帧要发全部条带
    private int _motionQuality;                   // 画面在动时现在用的质量（不超过 Quality）
    private int _lastMotionQuality;               // 最近一帧在动的是按什么质量压的、压出来多大（SetMotionBudget 用）；还没有过是 0
    private long _lastMotionBytes;
    private readonly byte[] _sentQuality = new byte[Units];       // Tab5 上每一格现在的画面是按什么质量压的
    // 最近一次 Encode 压出来的是哪些行、什么质量：Commit 时照这个记账
    private readonly List<(int Row, int H)> _pending = [];
    private int _pendingQuality;

    /// <param name="maxStripBytes">HELLO 里的单条带上限。</param>
    /// <param name="maxFrameBytes">HELLO 里的单帧上限。</param>
    /// <param name="quality">JPEG 质量 1~100。一帧超过上限时会自动往下降，降了之后不再升回去（除非 SetPicture 给了新的）。</param>
    /// <param name="stripRows">条带高（帧缓冲的行数），16 的倍数。</param>
    /// <param name="bgra">画面的字节顺序：true = B G R A（Windows 抓屏的格式），false = R G B A。A 不用。</param>
    /// <param name="motionBytes">画面在动时每帧的字节数预算；0 = 不管，一律按 quality 压。</param>
    /// <param name="minQuality">画面在动时质量最低降到多少（比 quality 大的话就是 quality：等于不降）。</param>
    /// <param name="subsampling">色度采样。yuv422 为 true 时不看它，固定 4:2:2。</param>
    /// <param name="yuv422">这条连接走 YUV422 帧缓冲（Tab5Link.Yuv422）：条带固定 4:2:2，画面先压到有限范围再压 JPEG。</param>
    public StripEncoder(int maxStripBytes, int maxFrameBytes, int quality = 60, int stripRows = 160,
                        ChromaSubsampling subsampling = ChromaSubsampling.S420, bool bgra = true,
                        int motionBytes = 0, int minQuality = 20, bool yuv422 = false)
    {
        if (stripRows <= 0 || stripRows % Proto.StripAlign != 0)
            throw new ArgumentException($"条带高必须是 {Proto.StripAlign} 的正整数倍", nameof(stripRows));
        if (quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(quality));
        if (motionBytes < 0) throw new ArgumentOutOfRangeException(nameof(motionBytes));
        if (minQuality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(minQuality));
        _stripH = stripRows;
        _maxStripBytes = maxStripBytes;
        _maxFrameBytes = maxFrameBytes;
        _motionBytes = motionBytes;
        _minQuality = minQuality;
        _motionQuality = quality;
        Quality = quality;
        _limited = yuv422;
        _downsample = (yuv422 ? ChromaSubsampling.S422 : subsampling) switch
        {
            ChromaSubsampling.S444 => SKJpegEncoderDownsample.Downsample444,
            ChromaSubsampling.S422 => SKJpegEncoderDownsample.Downsample422,
            _ => SKJpegEncoderDownsample.Downsample420,
        };
        _colorType = bgra ? SKColorType.Bgra8888 : SKColorType.Rgba8888;
    }

    /// <summary>画面不动时用的 JPEG 质量（一帧超过单帧上限时会往下降，降了不再升回去，到 SetPicture 给了新的为止）。</summary>
    public int Quality { get; private set; }

    /// <summary>
    /// 连接中途换画质（人在 Tab5 上拨了滑块，协议「画质」）：之后的帧按新的质量和预算压。
    /// 质量调高了：Tab5 上已有的画面是按低的质量压的，算「降过质量的地方」（NeedsRefine），画面不大片地动时照常补一遍。
    /// 调低了的不重发，哪里变了哪里用新的质量。以前因为超过单帧上限降下去的质量不留着，按新给的来（再超再降）。
    /// </summary>
    /// <param name="quality">JPEG 质量 1~100。</param>
    /// <param name="motionBytes">画面在动时每帧的字节数预算；0 = 不降。</param>
    public void SetPicture(int quality, int motionBytes)
    {
        if (quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(quality));
        if (motionBytes < 0) throw new ArgumentOutOfRangeException(nameof(motionBytes));
        Quality = quality;
        _motionBytes = motionBytes;
    }

    /// <summary>
    /// 只换画面在动时每帧的字节数预算（闭环调预算时每调一次来一下，见 BudgetControl）；要大于 0。
    /// 和 SetPicture 不一样：不动 Quality——因为超过单帧上限降下去的质量照旧留着。
    /// 在动的质量跟着挪一步，对着新预算的 95%：平时「预算的 85%~105% 之间不动」（见 AdjustMotionQuality）是为了质量不逐帧来回摆，
    /// 但预算一次只调百分之几，落在这一段里的话帧的大小根本不跟着变——往上攒了几步才一下子大一截，往下退一步又退不下来（2026-11-15 真机上就是这样）。
    /// 调小了：上一帧在动的比新预算的 95% 大，就照「超了往下调」的算法降；调大了：比它小，就照「往回升」的算法升一步。
    /// </summary>
    public void SetMotionBudget(int motionBytes)
    {
        if (motionBytes <= 0) throw new ArgumentOutOfRangeException(nameof(motionBytes));
        int old = _motionBytes;
        _motionBytes = motionBytes;
        if (old <= 0 || motionBytes == old || _lastMotionBytes == 0) return;
        long aim = motionBytes * 95L / 100;
        if (motionBytes < old && _lastMotionBytes > aim)
            _motionQuality = Math.Min(_motionQuality, LoweredQuality(_lastMotionQuality, _lastMotionBytes, aim));
        else if (motionBytes > old && _lastMotionBytes < aim)
            _motionQuality = Math.Max(_motionQuality, RaisedQuality(_lastMotionQuality, _lastMotionBytes));
    }

    /// <summary>画面在动时每帧的字节数预算；0 = 不降。</summary>
    public int MotionBudget => _motionBytes;

    /// <summary>最近一次 Encode() 用的质量。</summary>
    public int LastQuality => _pendingQuality;

    /// <summary>最近一次 Encode() 的那一帧算不算「在动」（大片都在变、按预算定的质量）。</summary>
    public bool LastWasMotion { get; private set; }

    /// <summary>Tab5 上有降过质量的地方还没补：画面不再大片地动了之后，Encode() 该带上 refine。</summary>
    public bool NeedsRefine
    {
        get
        {
            if (!_haveSent) return false;
            foreach (byte q in _sentQuality)
                if (q < Quality) return true;
            return false;
        }
    }

    /// <summary>把这一次 Encode() 的画面记成「Tab5 已经有了」。</summary>
    public void Commit()
    {
        (_cur, _sent) = (_sent, _cur);
        _haveSent = true;
        foreach (var (row, h) in _pending)
            _sentQuality.AsSpan(row / Proto.StripAlign, h / Proto.StripAlign).Fill((byte)_pendingQuality);
        _pending.Clear();
    }

    /// <summary>忘掉 Tab5 上的画面（换了一条连接）：下一帧发全部条带。</summary>
    public void Reset() => _haveSent = false;

    /// <summary>
    /// 把横屏画面（1280×720，每像素 4 字节，一行 stride 字节）转成这一帧要发的条带；没有要发的返回空表。
    /// refine = 这一帧不算在动的话，Tab5 上降过质量的条带也按原质量压进去（画面和 Tab5 上的一样时就只有这些条带）。
    /// </summary>
    public List<Strip> Encode(ReadOnlySpan<byte> view, int stride, bool refine = false)
    {
        if (stride < Proto.ViewW * Bpp || view.Length < (long)stride * (Proto.ViewH - 1) + Proto.ViewW * Bpp)   // 用 long 算：stride 很大时 int 会回绕、把不够长的缓冲放过去
            throw new ArgumentException("画面必须是 1280×720、每像素 4 字节");
        RotateToFb(view, stride, _cur, _limited);

        // 各条带：变了没有；没变的，Tab5 上那一条是不是降过质量的
        int n = (Proto.FbH + _stripH - 1) / _stripH;
        Span<bool> changed = stackalloc bool[n];
        Span<bool> low = stackalloc bool[n];
        int rows = 0;
        for (int i = 0; i < n; i++)
        {
            int r0 = i * _stripH, h = Math.Min(_stripH, Proto.FbH - r0);
            changed[i] = !_haveSent || !_cur.AsSpan(r0 * FbStride, h * FbStride).SequenceEqual(_sent.AsSpan(r0 * FbStride, h * FbStride));
            if (changed[i]) rows += h;
            else if (refine)
                foreach (byte q in _sentQuality.AsSpan(r0 / Proto.StripAlign, h / Proto.StripAlign))
                    if (q < Quality) { low[i] = true; break; }
        }

        // 大片都在变的一帧按预算定质量；只变了一小块的照原质量（它本来就小，降了质量还得回头补）。
        // 连接上的第一帧发全部条带是因为 Tab5 上还没有画面，不是画面在动：照原质量，也不拿它的大小去调质量
        bool motion = _motionBytes > 0 && _haveSent && rows * 4 > Proto.FbH;
        var regions = new List<(int Row, int H)>();
        for (int i = 0; i < n; i++)
            if (changed[i] || (low[i] && !motion))      // 没变的条带 _cur 和 _sent 里是同一幅画面，从 _cur 压就是补 Tab5 上那一幅
                regions.Add((i * _stripH, Math.Min(_stripH, Proto.FbH - i * _stripH)));

        var strips = new List<Strip>();
        _pending.Clear();
        LastWasMotion = motion;
        if (regions.Count == 0) return strips;
        int quality = motion ? Math.Min(_motionQuality, Quality) : Quality;
        fixed (byte* fb = _cur)
        {
            long total = EncodeWithinFrameLimit(fb, regions, ref quality, strips);
            if (motion)
            {
                AdjustMotionQuality(quality, total);
                (_lastMotionQuality, _lastMotionBytes) = (quality, total);
            }
        }
        _pending.AddRange(regions);
        _pendingQuality = quality;
        return strips;
    }

    /// <summary>
    /// 按 quality 把这些行压成一帧的条带，返回总字节数。超过单帧上限就降质量重压（quality 跟着变）；降到比 Quality 还低的话 Quality 也跟着降，
    /// 之后不再升回去。降到底还超就抛异常。
    /// </summary>
    private long EncodeWithinFrameLimit(byte* fb, List<(int Row, int H)> regions, ref int quality, List<Strip> strips)
    {
        while (true)
        {
            strips.Clear();
            long total = EncodeRegions(fb, regions, quality, strips);
            if (total <= _maxFrameBytes) return total;
            if (quality <= FloorQuality)
                throw new InvalidOperationException($"质量降到 {quality} 一帧仍有 {total} 字节，超过 Tab5 的单帧上限 {_maxFrameBytes}");
            quality = Math.Max(FloorQuality, quality * 3 / 4);
            if (quality < Quality) Quality = quality;
        }
    }

    // JPEG 的质量数和量化步长不成正比（libjpeg 的定义）：质量 50 以下步长的倍数是 5000 / 质量，50 以上是 200 − 2 × 质量。
    // 所以质量 93 → 82 步长是 2.6 倍，20 → 18 才 1.1 倍。调质量要在步长上按比例调，直接在质量数上加减百分之几的话，
    // 质量高的那一头一步就从预算的一边跨到另一边，来回摆。
    private static double StepScale(int quality) => quality < 50 ? 5000.0 / quality : Math.Max(1, 200 - 2 * quality);
    private static int QualityOfStepScale(double scale) =>
        Math.Clamp((int)Math.Round(scale > 100 ? 5000 / scale : (200 - scale) / 2), 1, 100);

    /// <summary>
    /// 看这一帧压出来多大，定下一帧在动时用的质量：超过预算 5% 往下调，不到预算的 85% 往回升，中间不动。
    /// 字节数大致和步长的 0.3~0.6 次方成反比（看画面）。往下调按 0.5 次方算（步长 ×（字节 / 预算）²，一次最多 4 倍）：
    /// 一步调不够的下一帧接着调。往回升每帧步长 × 0.85，字节数最多多一成，跨不过「85%~105%」这一段，所以升到这一段里就停住。
    /// 步长 × 0.85 取整之后质量数没变的（质量 97 以上、2 以下：质量数加 1 步长就少三分之一到一半）不能硬升 1，那样又会跨过去：
    /// 按 0.75 次方（往大里估）算一下升 1 之后的字节数，不超过预算的 105% 才升，否则停在原地。
    /// </summary>
    private void AdjustMotionQuality(int quality, long bytes)
    {
        if (bytes * 100 > _motionBytes * 105L) _motionQuality = LoweredQuality(quality, bytes, _motionBytes);
        else if (bytes * 100 < _motionBytes * 85L) _motionQuality = RaisedQuality(quality, bytes);
        else _motionQuality = quality;
    }

    /// <summary>按 quality 压出来 bytes 字节、要压到 target 字节上下：往下调到多少（至少降 1，不低于最低质量）。</summary>
    private int LoweredQuality(int quality, long bytes, long target)
    {
        double over = (double)bytes / target;
        int next = QualityOfStepScale(StepScale(quality) * Math.Min(4, over * over));
        return Math.Max(Math.Min(_minQuality, Quality), Math.Min(next, quality - 1));
    }

    /// <summary>按 quality 压出来 bytes 字节、还有余量：往回升一步到多少（不超过 Quality；升 1 都可能超预算的停在原地）。</summary>
    private int RaisedQuality(int quality, long bytes)
    {
        int next = QualityOfStepScale(StepScale(quality) * 0.85);
        if (next <= quality && quality < 100)
        {
            double grown = bytes * Math.Pow(StepScale(quality) / StepScale(quality + 1), 0.75);
            next = grown * 100 <= _motionBytes * 105L ? quality + 1 : quality;
        }
        return Math.Min(Quality, next);
    }

    /// <summary>把这些行各压成条带加进 strips（按行的先后），返回总字节数。不止一处时分到几个核上同时压。</summary>
    private long EncodeRegions(byte* fb, List<(int Row, int H)> regions, int quality, List<Strip> strips)
    {
        var parts = new List<Strip>[regions.Count];
        if (regions.Count == 1)
        {
            parts[0] = [];
            EncodeRegion(fb, regions[0].Row, regions[0].H, quality, parts[0]);
        }
        else
        {
            IntPtr p = (IntPtr)fb;      // 指针不能直接带进匿名函数；缓冲在调用方那里钉着，这段时间不会挪
            try
            {
                Parallel.For(0, regions.Count, i =>
                {
                    var part = new List<Strip>(1);
                    EncodeRegion((byte*)p, regions[i].Row, regions[i].H, quality, part);
                    parts[i] = part;
                });
            }
            catch (AggregateException e)    // 并行的那几个里抛的被包了一层：拆出来原样抛，和只有一处时（不并行）抛的一样
            {
                ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
            }
        }
        long total = 0;
        foreach (var part in parts)
            foreach (Strip st in part)
            {
                strips.Add(st);
                total += st.Jpeg.Length;
            }
        return total;
    }

    /// <summary>把帧缓冲的 [row, row+h) 行压成一张或几张 JPEG 加进 strips，返回字节数。超过单条带上限就对半拆（拆到 16 行为止）。</summary>
    private long EncodeRegion(byte* fb, int row, int h, int quality, List<Strip> strips)
    {
        var info = new SKImageInfo(Proto.FbW, h, _colorType, SKAlphaType.Opaque);
        using var pixmap = new SKPixmap(info, (IntPtr)(fb + (long)row * FbStride), FbStride);
        using SKData? data = pixmap.Encode(new SKJpegEncoderOptions(quality, _downsample, SKJpegEncoderAlphaOption.Ignore));
        if (data is null) throw new InvalidOperationException($"第 {row} 行起的 {h} 行条带压 JPEG 失败");
        if (data.Size <= _maxStripBytes)
        {
            strips.Add(new Strip(row, data.ToArray()));
            return data.Size;
        }
        if (h <= Proto.StripAlign)
            throw new InvalidOperationException($"第 {row} 行起的 16 行条带压出来 {data.Size} 字节，超过 Tab5 的单条带上限 {_maxStripBytes}");
        int h1 = (h / 2 + Proto.StripAlign - 1) / Proto.StripAlign * Proto.StripAlign;
        return EncodeRegion(fb, row, h1, quality, strips) + EncodeRegion(fb, row + h1, h - h1, quality, strips);
    }

    /// <summary>
    /// 横屏画面 → 帧缓冲的排列（逆时针转 90°）：帧缓冲第 r 行、第 c 列 = 横屏画面第 c 行、第 (1279 − r) 列。
    /// 按 16 行一块来转：一块里读的是画面上 16 像素宽的一个竖条，来回扫 720 行都落在缓存里；
    /// 一整行一整行地转的话每读一个像素就换一行，全是缓存缺失。
    /// 帧缓冲从上到下分成几段，各段在不同的核上转（段和段写的地方不重叠）。
    /// limited = 转的同时把三个颜色通道各自压到有限范围（查表；第四个字节 A 不用，原样带过去）。
    /// </summary>
    internal static void RotateToFb(ReadOnlySpan<byte> view, int stride, Span<byte> fbOut, bool limited = false)
    {
        const int Block = 16, Parts = 4, BlocksPerPart = Proto.FbH / Block / Parts;
        fixed (byte* src = view)
        fixed (byte* dst = fbOut)
        fixed (byte* lutPin = LimitedLut)
        {
            IntPtr lp = limited ? (IntPtr)lutPin : IntPtr.Zero;
            IntPtr sp = (IntPtr)src, dp = (IntPtr)dst;      // 指针不能直接带进匿名函数；两块缓冲在这里钉着
            Parallel.For(0, Parts, part =>
            {
                byte* from = (byte*)sp, to = (byte*)dp, lut = (byte*)lp;
                int end = (part + 1) * BlocksPerPart * Block;
                for (int r0 = part * BlocksPerPart * Block; r0 < end; r0 += Block)
                {
                    for (int c = 0; c < Proto.FbW; c++)
                    {
                        uint* s = (uint*)(from + (long)c * stride) + (Proto.ViewW - 1 - r0);    // 画面第 c 行、第 (1279 − r0) 列，往左走
                        uint* d = (uint*)(to + (long)r0 * FbStride) + c;                        // 帧缓冲第 r0 行、第 c 列，往下走
                        if (lut is null)
                        {
                            for (int k = 0; k < Block; k++)
                            {
                                *d = *s;
                                s--;
                                d += Proto.FbW;
                            }
                        }
                        else
                        {
                            for (int k = 0; k < Block; k++)
                            {
                                uint v = *s;
                                *d = lut[v & 0xFF] | (uint)lut[(v >> 8) & 0xFF] << 8 | (uint)lut[(v >> 16) & 0xFF] << 16 | (v & 0xFF000000);
                                s--;
                                d += Proto.FbW;
                            }
                        }
                    }
                }
            });
        }
    }
}

/// <summary>JPEG 的色度采样。Tab5 的硬件解码器这三种都能解（走 YUV422 帧缓冲的连接只收 4:2:2）；4:2:0 最小，彩色小字的边缘最虚。</summary>
public enum ChromaSubsampling { S420, S422, S444 }
