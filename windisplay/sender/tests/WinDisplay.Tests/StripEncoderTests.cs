using System.Runtime.InteropServices;
using SkiaSharp;
using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>画面 → 条带：转 90° 的几何关系、只发变化的条带、超限时拆分 / 降质量、压出来的 JPEG 是 Tab5 能解的那种。</summary>
public class StripEncoderTests
{
    private const int W = Proto.ViewW, H = Proto.ViewH;
    private const int Big = 1 << 20;        // 单条带 / 单帧上限给得很宽，不触发拆分和降质量

    /// <summary>一幅像桌面的画面（B G R A）：深色底、几个纯色窗口、一条任务栏。平坦区域多，JPEG 压得动。</summary>
    private static byte[] Desktop()
    {
        var px = new uint[W * H];
        Array.Fill(px, 0xFF202830u);
        Fill(px, 80, 60, 600, 400, 0xFFF0F0F0u);        // 浅色窗口
        Fill(px, 700, 120, 480, 300, 0xFF2D6CDFu);      // 蓝色窗口
        Fill(px, 0, H - 48, W, 48, 0xFF101418u);        // 任务栏
        return MemoryMarshal.AsBytes(px.AsSpan()).ToArray();
    }

    private static void Fill(uint[] px, int x, int y, int w, int h, uint bgra)
    {
        for (int r = y; r < y + h; r++) px.AsSpan(r * W + x, w).Fill(bgra);
    }

    private static byte[] Noise(int seed)
    {
        var b = new byte[W * H * 4];
        new Random(seed).NextBytes(b);
        return b;
    }

    /// <summary>把条带解码后拼回帧缓冲（720×1280，B G R A），顺带核对每张 JPEG 的尺寸。</summary>
    private static uint[] DecodeToFb(IEnumerable<Strip> strips, uint[]? onto = null)
    {
        var fb = onto ?? new uint[Proto.FbW * Proto.FbH];
        foreach (var s in strips)
        {
            using var bmp = SKBitmap.Decode(s.Jpeg, new SKImageInfo(Proto.FbW, JpegHeight(s.Jpeg), SKColorType.Bgra8888, SKAlphaType.Opaque));
            Assert.NotNull(bmp);
            Assert.Equal(Proto.FbW, bmp.Width);
            MemoryMarshal.Cast<byte, uint>(bmp.GetPixelSpan()).CopyTo(fb.AsSpan(s.Row * Proto.FbW));
        }
        return fb;
    }

    /// <summary>从 JPEG 的帧头（SOF）读出高度，同时确认是基线 JPEG（SOF0）：Tab5 的硬件解码器不解渐进式。</summary>
    private static int JpegHeight(byte[] jpeg)
    {
        Assert.True(jpeg.Length > 4 && jpeg[0] == 0xFF && jpeg[1] == 0xD8, "不是 JPEG");
        for (int i = 2; i + 9 < jpeg.Length;)
        {
            Assert.Equal(0xFF, jpeg[i]);
            byte marker = jpeg[i + 1];
            int len = jpeg[i + 2] << 8 | jpeg[i + 3];
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                Assert.Equal(0xC0, marker);
                Assert.Equal(Proto.FbW, jpeg[i + 7] << 8 | jpeg[i + 8]);
                return jpeg[i + 5] << 8 | jpeg[i + 6];
            }
            i += 2 + len;
        }
        Assert.Fail("JPEG 里没有帧头");
        return 0;
    }

    /// <summary>
    /// 一幅像游戏的画面（B G R A）：整屏都有纹理（大块的明暗起伏上叠着细碎的颗粒），第 n 帧整体横移 8n 像素——每一帧每个条带都变。
    /// 质量 60 时一帧约 70 KB，比「画面在动时每帧 40 KB」的预算大得多。
    /// </summary>
    internal static byte[] Game(int n)
    {
        var px = new uint[W * H];
        int shift = n * 8 % W;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int sx = (x + W - shift) % W;
                uint hash = (uint)(sx * 73856093) ^ (uint)(y * 19349663);
                hash ^= hash >> 13; hash *= 0x5bd1e995; hash ^= hash >> 15;
                int grain = (int)(hash & 31) - 16;                                    // 细碎的颗粒
                int r = (int)(128 + 80 * Math.Sin(sx / 37.0) + grain);
                int g = (int)(128 + 80 * Math.Sin(y / 29.0 + sx / 113.0) + grain);
                int b = (int)(128 + 80 * Math.Cos((sx + y) / 53.0) + grain);
                px[y * W + x] = 0xFF000000u | (uint)Math.Clamp(r, 0, 255) << 16 | (uint)Math.Clamp(g, 0, 255) << 8 | (uint)Math.Clamp(b, 0, 255);
            }
        return MemoryMarshal.AsBytes(px.AsSpan()).ToArray();
    }

    private static int Bytes(IEnumerable<Strip> strips) => strips.Sum(s => s.Jpeg.Length);

    private static double MeanAbsDiff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
            if (i % 4 != 3) sum += Math.Abs(a[i] - b[i]);       // 不比 A
        return sum / (a.Length * 0.75);
    }

    [Fact]
    public void 转90度的几何关系和协议文档一致()
    {
        // PROTOCOL.md：帧缓冲第 r 行、第 c 列 = 横屏画面第 c 行、第 (1279 − r) 列。画面带行间填充（一行比 1280×4 多 12 字节）
        const int stride = W * 4 + 12;
        var view = new byte[stride * H];
        new Random(7).NextBytes(view);
        var fb = new byte[Proto.FbW * Proto.FbH * 4];
        StripEncoder.RotateToFb(view, stride, fb);
        var rng = new Random(8);
        for (int k = 0; k < 20000; k++)
        {
            int r = rng.Next(Proto.FbH), c = rng.Next(Proto.FbW);
            Assert.True(fb.AsSpan((r * Proto.FbW + c) * 4, 4).SequenceEqual(view.AsSpan(c * stride + (W - 1 - r) * 4, 4)), $"帧缓冲 ({r},{c})");
        }
        // 四个角：画面左上角在帧缓冲的左下角，画面右上角在帧缓冲的左上角（面板竖着装，顶边在右手边）
        Assert.True(fb.AsSpan((Proto.FbH - 1) * Proto.FbW * 4, 4).SequenceEqual(view.AsSpan(0, 4)));
        Assert.True(fb.AsSpan(0, 4).SequenceEqual(view.AsSpan((W - 1) * 4, 4)));
        Assert.True(fb.AsSpan((Proto.FbW - 1) * 4, 4).SequenceEqual(view.AsSpan((H - 1) * stride + (W - 1) * 4, 4)));
    }

    [Fact]
    public void 第一帧发全部条带_解出来和原画面一致_红蓝没有反()
    {
        var view = Desktop();
        var enc = new StripEncoder(Big, Big, quality: 92, subsampling: ChromaSubsampling.S444);
        List<Strip> strips = enc.Encode(view, W * 4);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => i * 160), strips.Select(s => s.Row));
        Assert.All(strips, s => Assert.Equal(160, JpegHeight(s.Jpeg)));

        var want = new byte[Proto.FbW * Proto.FbH * 4];
        StripEncoder.RotateToFb(view, W * 4, want);
        byte[] got = MemoryMarshal.AsBytes(DecodeToFb(strips).AsSpan()).ToArray();
        Assert.InRange(MeanAbsDiff(want, got), 0, 1.5);
        // 蓝色窗口中间一点（画面 (940, 270)）→ 帧缓冲第 1279 − 940 行、第 270 列：B 应该远大于 R
        int at = ((W - 1 - 940) * Proto.FbW + 270) * 4;
        Assert.True(got[at] > 180 && got[at + 2] < 90, $"B={got[at]} R={got[at + 2]}");
    }

    [Fact]
    public void 发成功才算Tab5有了这一帧_之后只发变了的条带()
    {
        var view = Desktop();
        var enc = new StripEncoder(Big, Big);
        Assert.Equal(8, enc.Encode(view, W * 4).Count);
        Assert.Equal(8, enc.Encode(view, W * 4).Count);     // 上一帧没发成（没 Commit）：照样全发
        enc.Commit();
        Assert.Empty(enc.Encode(view, W * 4));              // 画面没变：不发

        // 光标大小的一块变了：画面 x 100~131、y 200~231 → 帧缓冲第 1148~1179 行，落在从第 1120 行起的那一条里
        var px = MemoryMarshal.Cast<byte, uint>(view.AsSpan());
        for (int y = 200; y < 232; y++) px.Slice(y * W + 100, 32).Fill(0xFFFFFFFFu);
        List<Strip> strips = enc.Encode(view, W * 4);
        Assert.Equal([1120], strips.Select(s => s.Row));
        enc.Commit();

        // 横跨两条的一块：画面 x 630~669 → 帧缓冲第 610~649 行，跨第 480 行起和第 640 行起的两条
        for (int y = 300; y < 340; y++) px.Slice(y * W + 630, 40).Fill(0xFF00FF00u);
        Assert.Equal([480, 640], enc.Encode(view, W * 4).Select(s => s.Row));
        enc.Commit();

        enc.Reset();                                        // 换了一条连接：Tab5 上没有我们的画面了
        Assert.Equal(8, enc.Encode(view, W * 4).Count);
    }

    [Fact]
    public void 条带超过单条带上限时对半拆_拆完仍然不重不漏()
    {
        const int limit = 40 * 1024;
        var enc = new StripEncoder(limit, Big, quality: 80);
        List<Strip> strips = enc.Encode(Noise(1), W * 4);
        Assert.True(strips.Count > 8, $"只有 {strips.Count} 条");
        int next = 0;
        foreach (var s in strips)                           // 一条接一条盖满 0~1279 行，起始行和高都是 16 的倍数，每条都不超限
        {
            Assert.Equal(next, s.Row);
            int h = JpegHeight(s.Jpeg);
            Assert.Equal(0, s.Row % Proto.StripAlign);
            Assert.Equal(0, h % Proto.StripAlign);
            Assert.InRange(s.Jpeg.Length, 1, limit);
            next += h;
        }
        Assert.Equal(Proto.FbH, next);
        Assert.Equal(80, enc.Quality);                      // 拆条带不动质量
    }

    [Fact]
    public void 一帧超过单帧上限时降质量重压_降了不再升回去()
    {
        const int limit = 300 * 1024;
        var enc = new StripEncoder(Big, limit, quality: 80);
        List<Strip> strips = enc.Encode(Noise(2), W * 4);
        Assert.InRange(strips.Sum(s => s.Jpeg.Length), 1, limit);
        int lowered = enc.Quality;
        Assert.InRange(lowered, 10, 79);
        enc.Commit();
        enc.Encode(Desktop(), W * 4);                       // 换成好压的画面，质量留在降过的那一档
        Assert.Equal(lowered, enc.Quality);
    }

    [Fact]
    public void 质量降到底还超过单帧上限_报错而不是硬发()
    {
        var enc = new StripEncoder(Big, 20 * 1024, quality: 60);
        var e = Assert.Throws<InvalidOperationException>(() => enc.Encode(Noise(3), W * 4));
        Assert.Contains("单帧上限", e.Message);
    }

    private const int Budget = 40 * 1024;

    /// <summary>像游戏那样连着动 frames 帧（每帧都发成了），返回最后一帧的画面和条带。</summary>
    private static (byte[] View, List<Strip> Strips) Play(StripEncoder enc, int frames)
    {
        byte[] view = [];
        List<Strip> strips = [];
        for (int n = 0; n < frames; n++)
        {
            view = Game(n);
            strips = enc.Encode(view, W * 4);
            Assert.Equal([0, 160, 320, 480, 640, 800, 960, 1120], strips.Select(s => s.Row));     // 各条带是分到几个核上压的，出来仍按行的先后排
            enc.Commit();
        }
        return (view, strips);
    }

    [Fact]
    public void 画面在动时一帧超过预算就降质量_几帧之内压进预算_不低于最低质量()
    {
        var enc = new StripEncoder(Big, Big, quality: 60, motionBytes: Budget, minQuality: 15);
        int first = Bytes(enc.Encode(Game(0), W * 4));
        enc.Commit();
        Assert.Equal(60, enc.LastQuality);                  // 第一帧还不知道画面有多大，按原质量压
        Assert.True(first > Budget * 13 / 10, $"测试画面质量 60 时只有 {first} 字节，不够大");

        var sizes = new List<int>();
        for (int n = 1; n <= 12; n++)
        {
            sizes.Add(Bytes(enc.Encode(Game(n), W * 4)));
            Assert.InRange(enc.LastQuality, 15, 60);
            enc.Commit();
        }
        Assert.True(sizes.Skip(6).All(b => b <= Budget * 11 / 10), $"降了质量之后每帧还是超预算：{string.Join(' ', sizes)}");
        Assert.InRange(enc.LastQuality, 15, 59);
        Assert.Equal(60, enc.Quality);                      // 画面不动时用的质量没被动过
    }

    [Theory]
    [InlineData(93, 94)]        // 预算只比原质量的一帧小一点。质量高的这一头，质量数差一点字节数就差很多：按质量数的百分比调会来回摆
    [InlineData(93, 90)]
    [InlineData(85, 94)]
    [InlineData(75, 95)]
    [InlineData(93, 70)]
    [InlineData(85, 57)]        // 预算只有原质量一帧的一半多一点
    [InlineData(60, 57)]
    [InlineData(40, 70)]
    public void 画面一直在动_质量调到预算附近就停住_不在预算两边来回摆(int quality, int budgetPercent)
    {
        int full = Bytes(new StripEncoder(Big, Big, quality: quality).Encode(Game(0), W * 4));
        int budget = full * budgetPercent / 100;
        var enc = new StripEncoder(Big, Big, quality: quality, motionBytes: budget, minQuality: 5);
        var qualities = new List<int>();
        var sizes = new List<int>();
        for (int n = 0; n < 40; n++)
        {
            sizes.Add(Bytes(enc.Encode(Game(n), W * 4)));
            qualities.Add(enc.LastQuality);
            enc.Commit();
        }
        string log = string.Join(' ', qualities.Zip(sizes, (q, b) => $"{q}:{b * 100 / budget}%"));
        Assert.True(qualities.Skip(20).Distinct().Count() == 1, $"后 20 帧质量还在变（质量:占预算的百分比）：{log}");
        Assert.True(sizes.Skip(20).All(b => b * 100 >= budget * 80L && b * 100 <= budget * 108L), $"停住的地方离预算太远：{log}");
        Assert.InRange(qualities[^1], 6, quality - 1);
    }

    [Fact]
    public void 没给预算_画面再怎么动也按原质量压()
    {
        var enc = new StripEncoder(Big, Big, quality: 60);
        var (view, strips) = Play(enc, 4);
        Assert.Equal(60, enc.LastQuality);
        Assert.False(enc.LastWasMotion);
        Assert.True(Bytes(strips) > Budget, "测试画面不够大");
        Assert.False(enc.NeedsRefine);
        Assert.Empty(enc.Encode(view, W * 4, refine: true));
    }

    [Fact]
    public void 最低质量比设定的质量高_等于不降()
    {
        var enc = new StripEncoder(Big, Big, quality: 30, motionBytes: Budget, minQuality: 50);
        Play(enc, 6);
        Assert.Equal(30, enc.LastQuality);
        Assert.False(enc.NeedsRefine);
    }

    [Fact]
    public void 画面在动之后只变了一小块_照原质量压()
    {
        var enc = new StripEncoder(Big, Big, quality: 60, motionBytes: Budget);
        var (view, _) = Play(enc, 8);
        Assert.True(enc.LastQuality < 60);

        // 光标大小的一块变了（和「之后只发变了的条带」那一条是同一块地方）：只有一个条带，本来就小，不降质量
        var px = MemoryMarshal.Cast<byte, uint>(view.AsSpan());
        for (int y = 200; y < 232; y++) px.Slice(y * W + 100, 32).Fill(0xFFFFFFFFu);
        List<Strip> strips = enc.Encode(view, W * 4);
        Assert.Equal([1120], strips.Select(s => s.Row));
        Assert.Equal(60, enc.LastQuality);
    }

    [Fact]
    public void 连接上的第一帧不算在动_不拿它的大小去调质量()
    {
        // 连上时是一幅不动的画面，整屏发过去比预算大得多；之后第一次大片地动，从原质量起调，不是从最低质量起
        var enc = new StripEncoder(Big, Big, quality: 60, motionBytes: Budget);
        Assert.True(Bytes(enc.Encode(Game(0), W * 4)) > Budget * 13 / 10);
        Assert.False(enc.LastWasMotion);
        enc.Commit();
        Assert.False(enc.NeedsRefine);
        enc.Encode(Game(1), W * 4);
        Assert.True(enc.LastWasMotion);
        Assert.Equal(60, enc.LastQuality);
    }

    [Fact]
    public void 画面停下来_降过质量的条带按原质量补一遍_补完更清晰_不再补()
    {
        var enc = new StripEncoder(Big, Big, quality: 60, motionBytes: Budget);
        Assert.False(enc.NeedsRefine);                      // Tab5 上还没有画面：没什么可补的
        var (view, low) = Play(enc, 8);
        int lowQuality = enc.LastQuality;
        Assert.True(lowQuality < 60);
        Assert.True(enc.LastWasMotion);
        Assert.True(enc.NeedsRefine);
        Assert.Empty(enc.Encode(view, W * 4));              // 同一幅画面再来、没说要补：没有要发的

        List<Strip> fine = enc.Encode(view, W * 4, refine: true);
        Assert.Equal(60, enc.LastQuality);
        Assert.False(enc.LastWasMotion);
        Assert.Equal([0, 160, 320, 480, 640, 800, 960, 1120], fine.Select(s => s.Row));
        Assert.True(Bytes(fine) > Bytes(low), "按原质量补的那一帧应该比降了质量的那一帧大");
        Assert.True(enc.NeedsRefine);                       // 还没发成（没 Commit）：下次照样要补
        enc.Commit();
        Assert.False(enc.NeedsRefine);
        Assert.Empty(enc.Encode(view, W * 4, refine: true));

        // 补的是 Tab5 上已有的那一幅画面，而且更接近原画面
        var want = new byte[Proto.FbW * Proto.FbH * 4];
        StripEncoder.RotateToFb(view, W * 4, want);
        double errLow = MeanAbsDiff(MemoryMarshal.AsBytes(DecodeToFb(low).AsSpan()), want);
        double errFine = MeanAbsDiff(MemoryMarshal.AsBytes(DecodeToFb(fine).AsSpan()), want);
        Assert.True(errFine < errLow * 0.9, $"补完的误差 {errFine:F2} 应该明显小于降质量时的 {errLow:F2}（质量 {lowQuality}）");

        // 补过之后画面真的变了，照常只发变了的条带
        var px = MemoryMarshal.Cast<byte, uint>(view.AsSpan());
        for (int y = 200; y < 232; y++) px.Slice(y * W + 100, 32).Fill(0xFFFFFFFFu);
        Assert.Equal([1120], enc.Encode(view, W * 4, refine: true).Select(s => s.Row));
    }

    [Fact]
    public void 整屏停了还有一小块一直在变_说要补时变了的和降过质量的条带一起按原质量发_只补一次()
    {
        // 滚完网页停下来，页面上还有一个动图：每一帧都有一小块在变，等不到「画面没变」
        var enc = new StripEncoder(Big, Big, quality: 60, motionBytes: Budget);
        var (view, low) = Play(enc, 8);
        void Blink(uint bgra)
        {
            var px = MemoryMarshal.Cast<byte, uint>(view.AsSpan());
            for (int y = 200; y < 232; y++) px.Slice(y * W + 100, 32).Fill(bgra);
        }

        // 刚停下来、还没到该补的时候：只发变了的那一条（它照原质量），别的条带还是降过质量的
        Blink(0xFFFFFFFFu);
        Assert.Equal([1120], enc.Encode(view, W * 4).Select(s => s.Row));
        Assert.Equal(60, enc.LastQuality);
        enc.Commit();
        Assert.True(enc.NeedsRefine);

        // 该补了：这一帧变了的那一条和另外 7 条降过质量的一起发，都按原质量
        Blink(0xFF0000FFu);
        List<Strip> fine = enc.Encode(view, W * 4, refine: true);
        Assert.Equal([0, 160, 320, 480, 640, 800, 960, 1120], fine.Select(s => s.Row));
        Assert.Equal(60, enc.LastQuality);
        Assert.False(enc.LastWasMotion);
        Assert.True(Bytes(fine) > Bytes(low));
        enc.Commit();
        Assert.False(enc.NeedsRefine);
        var want = new byte[Proto.FbW * Proto.FbH * 4];
        StripEncoder.RotateToFb(view, W * 4, want);
        // 发的是现在这一幅，比 Tab5 上原来那一幅（降了质量的）更接近原画面
        double errLow = MeanAbsDiff(MemoryMarshal.AsBytes(DecodeToFb(low).AsSpan()), want);
        double errFine = MeanAbsDiff(MemoryMarshal.AsBytes(DecodeToFb(fine).AsSpan()), want);
        Assert.True(errFine < errLow * 0.9, $"补完的误差 {errFine:F2} 应该明显小于降质量时的 {errLow:F2}");

        // 补过了：之后又只发变了的那一条
        Blink(0xFF00FF00u);
        Assert.Equal([1120], enc.Encode(view, W * 4, refine: true).Select(s => s.Row));
    }

    [Fact]
    public void 说要补的那一帧又是大片在动_不补_照预算降质量()
    {
        var enc = new StripEncoder(Big, Big, quality: 60, motionBytes: Budget);
        Play(enc, 8);
        List<Strip> strips = enc.Encode(Game(8), W * 4, refine: true);
        Assert.True(enc.LastWasMotion);
        Assert.True(enc.LastQuality < 60);
        Assert.True(Bytes(strips) <= Budget * 11 / 10);
    }

    /// <summary>接着 Play 往下动：第 from 帧起再动 frames 帧，返回最后一帧的条带。</summary>
    private static List<Strip> PlayOn(StripEncoder enc, int from, int frames)
    {
        List<Strip> strips = [];
        for (int n = from; n < from + frames; n++)
        {
            strips = enc.Encode(Game(n), W * 4);
            enc.Commit();
        }
        return strips;
    }

    [Fact]
    public void 连接中途调高质量_画面没变也把整屏按新质量补一遍_补完更清晰_只补一次()
    {
        var enc = new StripEncoder(Big, Big, quality: 40, motionBytes: Budget);
        byte[] view = Game(0);
        List<Strip> before = enc.Encode(view, W * 4);
        enc.Commit();
        Assert.False(enc.NeedsRefine);

        enc.SetPicture(85, Budget);                         // 人在 Tab5 上把「画质」从 40 拉到 85
        Assert.Equal(85, enc.Quality);
        Assert.True(enc.NeedsRefine);                       // Tab5 上那一幅是按 40 压的
        Assert.Empty(enc.Encode(view, W * 4));              // 调用方还没说该补：画面没变就没有要发的
        List<Strip> after = enc.Encode(view, W * 4, refine: true);
        Assert.Equal([0, 160, 320, 480, 640, 800, 960, 1120], after.Select(s => s.Row));
        Assert.Equal(85, enc.LastQuality);
        Assert.False(enc.LastWasMotion);
        enc.Commit();
        Assert.False(enc.NeedsRefine);
        Assert.Empty(enc.Encode(view, W * 4, refine: true));

        var want = new byte[Proto.FbW * Proto.FbH * 4];
        StripEncoder.RotateToFb(view, W * 4, want);
        double errBefore = MeanAbsDiff(MemoryMarshal.AsBytes(DecodeToFb(before).AsSpan()), want);
        double errAfter = MeanAbsDiff(MemoryMarshal.AsBytes(DecodeToFb(after).AsSpan()), want);
        Assert.True(errAfter < errBefore * 0.8, $"质量 85 的误差 {errAfter:F2} 应该明显小于质量 40 的 {errBefore:F2}");
    }

    [Fact]
    public void 连接中途调低质量_没变的不重发_之后变了的条带按新质量()
    {
        var enc = new StripEncoder(Big, Big, quality: 80, motionBytes: Budget);
        byte[] view = Desktop();
        enc.Encode(view, W * 4);
        enc.Commit();

        enc.SetPicture(35, Budget);                         // 人嫌占带宽，把「画质」从 80 拉到 35
        Assert.False(enc.NeedsRefine);                      // Tab5 上的比新质量还清楚：没什么要补的
        Assert.Empty(enc.Encode(view, W * 4, refine: true));

        var px = MemoryMarshal.Cast<byte, uint>(view.AsSpan());
        for (int y = 200; y < 232; y++) px.Slice(y * W + 100, 32).Fill(0xFFFFFFFFu);        // 左边变了一小块
        List<Strip> strips = enc.Encode(view, W * 4, refine: true);
        Assert.Equal([1120], strips.Select(s => s.Row));
        Assert.Equal(35, enc.LastQuality);
        enc.Commit();
        Assert.False(enc.NeedsRefine);
    }

    [Fact]
    public void 连接中途把预算改成不降_画面在动也按原质量_改回来马上又压进预算()
    {
        var enc = new StripEncoder(Big, Big, quality: 60, motionBytes: Budget);
        Play(enc, 8);
        Assert.True(enc.LastQuality < 60);

        enc.SetPicture(60, 0);                              // 「动态预算」拉到头：不降
        List<Strip> full = PlayOn(enc, 8, 3);
        Assert.Equal(60, enc.LastQuality);
        Assert.False(enc.LastWasMotion);
        Assert.True(Bytes(full) > Budget, $"按原质量的一帧 {Bytes(full)} 字节，应该比原来的预算大");
        Assert.False(enc.NeedsRefine);                      // 整屏都按原质量发过了

        enc.SetPicture(60, Budget);                         // 又拉回 40 KB
        List<Strip> tight = PlayOn(enc, 11, 3);
        Assert.True(enc.LastWasMotion);
        Assert.True(enc.LastQuality < 60);
        Assert.InRange(Bytes(tight), 1, Budget * 11 / 10);
    }

    [Fact]
    public void 连接中途调大预算_在动的画面质量升上去_每帧不超过新预算()
    {
        var enc = new StripEncoder(Big, Big, quality: 90, motionBytes: 30 * 1024);
        List<Strip> small = Play(enc, 10).Strips;
        int low = enc.LastQuality;
        Assert.True(low < 90);

        enc.SetPicture(90, 100 * 1024);                     // 「动态预算」从 30 KB 拉到 100 KB
        List<Strip> big = PlayOn(enc, 10, 20);
        Assert.True(enc.LastWasMotion);
        Assert.True(enc.LastQuality > low, $"预算放大之后质量还是 {enc.LastQuality}（原来 {low}）");
        Assert.True(Bytes(big) > Bytes(small));
        Assert.InRange(Bytes(big), 1, 100 * 1024 * 11 / 10);
    }

    [Fact]
    public void 闭环把预算调小百分之几_在动的帧跟着小下来_不是停在死区里不动()
    {
        var enc = new StripEncoder(Big, Big, quality: 90, motionBytes: 60 * 1024);
        int before = Bytes(Play(enc, 12).Strips);
        Assert.InRange(before, 60 * 1024 * 85 / 100, 60 * 1024 * 105 / 100);       // 停在 60 KB 的「85%~105%」里

        // 刚升到 60 KB 就站不住，退回 56 KB（BudgetControl）：上一帧只要不超过 56 KB 的 105%，平时的算法是不动的
        enc.SetMotionBudget(before * 100 / 104);
        int after = Bytes(PlayOn(enc, 12, 1));
        Assert.True(after < before * 97 / 100, $"预算从 60 KB 退到 {before * 100 / 104 / 1024} KB，帧还是 {after} 字节（原来 {before}）");
        Assert.True(after <= enc.MotionBudget);
        Assert.True(enc.LastWasMotion);
    }

    [Fact]
    public void 闭环把预算调大百分之几_在动的帧跟着大一点_不超过新预算太多()
    {
        var enc = new StripEncoder(Big, Big, quality: 90, motionBytes: 50 * 1024);
        int before = Bytes(Play(enc, 12).Strips);
        int quality = enc.LastQuality;

        enc.SetMotionBudget(before * 106 / 95);             // 帧正好在新预算的 95% 以下一点：平时的算法要等到不足 85% 才升
        PlayOn(enc, 12, 1);
        Assert.True(enc.LastQuality > quality, $"预算调大之后质量还是 {quality}");
        int after = Bytes(PlayOn(enc, 13, 6));              // 再走几帧：停得住（质量没有掉回去），不超预算
        Assert.True(enc.LastQuality >= quality, $"质量又掉到了 {enc.LastQuality}（调大之前 {quality}）");
        Assert.InRange(after, before * 95 / 100, enc.MotionBudget * 105 / 100);
    }

    [Fact]
    public void 闭环调预算_不动因为单帧上限降下去的质量()
    {
        var enc = new StripEncoder(Big, 300 * 1024, quality: 95, motionBytes: 80 * 1024);
        enc.Encode(Noise(1), W * 4);                        // 第一帧（不算在动）超过单帧上限：质量降下去了
        enc.Commit();
        int lowered = enc.Quality;
        Assert.True(lowered < 95);
        enc.SetMotionBudget(60 * 1024);
        Assert.Equal(lowered, enc.Quality);
        Assert.Throws<ArgumentOutOfRangeException>(() => enc.SetMotionBudget(0));
    }

    [Fact]
    public void 因为超过单帧上限降下去的质量_人给了新的画质就按新的来()
    {
        const int limit = 300 * 1024;
        var enc = new StripEncoder(Big, limit, quality: 80);
        enc.Encode(Noise(2), W * 4);
        enc.Commit();
        Assert.InRange(enc.Quality, 10, 79);

        enc.SetPicture(75, 0);
        enc.Encode(Desktop(), W * 4);
        Assert.Equal(75, enc.LastQuality);
        Assert.Equal(75, enc.Quality);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(101, 0)]
    [InlineData(60, -1)]
    public void 中途给的质量或预算越界_拒绝_原来的不变(int quality, int motionBytes)
    {
        var enc = new StripEncoder(Big, Big, quality: 60, motionBytes: Budget);
        Assert.Throws<ArgumentOutOfRangeException>(() => enc.SetPicture(quality, motionBytes));
        Assert.Equal(60, enc.Quality);
        PlayOn(enc, 0, 8);
        Assert.True(enc.LastQuality < 60);                  // 预算也还是原来的 40 KB：在动的画面照样降
    }

    /// <summary>整屏横移的纯色竖条（每条 64 像素宽，每帧移 8 像素）：每个条带每帧都变，但压出来很小。</summary>
    private static byte[] Bars(int n)
    {
        var px = new uint[W * H];
        for (int x = 0; x < W; x++)
        {
            int k = (x + W - n * 8 % W) % W / 64;
            uint c = 0xFF000000u | (uint)(40 + k * 37 % 200) << 16 | (uint)(60 + k * 91 % 180) << 8 | (uint)(30 + k * 53 % 220);
            for (int y = 0; y < H; y++) px[y * W + x] = c;
        }
        return MemoryMarshal.AsBytes(px.AsSpan()).ToArray();
    }

    [Theory]
    [InlineData(94)]
    [InlineData(70)]
    [InlineData(45)]
    public void 质量设到100_也停得住_不超预算(int budgetPercent)
    {
        // 质量数 97 以上加 1 步长就少三分之一到一半，字节数一步能多几成：停住的地方可能离预算远一些，但不能来回摆、不能超
        int full = Bytes(new StripEncoder(Big, Big, quality: 100).Encode(Game(0), W * 4));
        int budget = full * budgetPercent / 100;
        var enc = new StripEncoder(Big, Big, quality: 100, motionBytes: budget, minQuality: 5);
        var qualities = new List<int>();
        var sizes = new List<int>();
        for (int n = 0; n < 40; n++)
        {
            sizes.Add(Bytes(enc.Encode(Game(n), W * 4)));
            qualities.Add(enc.LastQuality);
            enc.Commit();
        }
        string log = string.Join(' ', qualities.Zip(sizes, (q, b) => $"{q}:{b * 100 / budget}%"));
        Assert.True(qualities.Skip(20).Distinct().Count() == 1, $"后 20 帧质量还在变（质量:占预算的百分比）：{log}");
        Assert.True(sizes.Skip(20).All(b => b * 100 <= budget * 108L), $"停住的地方超预算：{log}");
        Assert.True(qualities[^1] >= 97 || sizes.Skip(20).All(b => b * 100 >= budget * 80L), $"停在质量 96 以下、离预算却很远：{log}");
        Assert.InRange(qualities[^1], 6, 99);
    }

    [Fact]
    public void 质量设到100_重的画面把质量压下去之后画面变轻_升得回100()
    {
        // 质量 97、98、99 这几级「步长 × 0.85」取整后不变：预算够的时候要一级一级升回去，不能卡在 97
        int light = Bytes(new StripEncoder(Big, Big, quality: 100).Encode(Bars(0), W * 4));
        int heavy97 = Bytes(new StripEncoder(Big, Big, quality: 97).Encode(Game(0), W * 4));
        int budget = heavy97 * 2 / 3;
        Assert.True(light * 3 < budget, $"测试画面不合适：竖条质量 100 时 {light} 字节，带颗粒的质量 97 时 {heavy97} 字节");
        var enc = new StripEncoder(Big, Big, quality: 100, motionBytes: budget, minQuality: 5);
        var qualities = new List<int>();
        for (int n = 0; n < 12; n++) { enc.Encode(Game(n), W * 4); qualities.Add(enc.LastQuality); enc.Commit(); }
        Assert.True(enc.LastQuality < 97, $"重的画面没把质量压到 97 以下：{string.Join(' ', qualities)}");
        for (int n = 12; n < 40; n++) { enc.Encode(Bars(n), W * 4); qualities.Add(enc.LastQuality); enc.Commit(); }
        Assert.True(enc.LastWasMotion);
        Assert.True(enc.LastQuality == 100, $"画面变轻之后没升回 100：{string.Join(' ', qualities)}");
    }

    [Fact]
    public void 一处条带压不进单条带上限_并行压的时候抛的也是原来那个异常()
    {
        // 整屏噪点、单条带上限 2 KB：16 行的条带也压不进。八条一起压（并行）和只压一条抛的是同一种异常
        var enc = new StripEncoder(2048, Big, quality: 90);
        var e = Assert.Throws<InvalidOperationException>(() => enc.Encode(Noise(1), W * 4));
        Assert.Contains("单条带上限", e.Message);
    }

    [Fact]
    public void 三种色度采样都压得出基线JPEG()
    {
        foreach (var sub in new[] { ChromaSubsampling.S420, ChromaSubsampling.S422, ChromaSubsampling.S444 })
        {
            var enc = new StripEncoder(Big, Big, subsampling: sub);
            List<Strip> strips = enc.Encode(Desktop(), W * 4);
            Assert.Equal(8, strips.Count);
            Assert.All(strips, s => Assert.Equal(160, JpegHeight(s.Jpeg)));
        }
    }

    [Fact]
    public void 字节顺序选RGBA时红蓝对调()
    {
        // 同一块内存按 R G B A 解释：蓝色窗口（B G R A 的 DF 6C 2D）就成了偏橙的颜色，R 远大于 B
        var enc = new StripEncoder(Big, Big, quality: 92, subsampling: ChromaSubsampling.S444, bgra: false);
        byte[] got = MemoryMarshal.AsBytes(DecodeToFb(enc.Encode(Desktop(), W * 4)).AsSpan()).ToArray();
        int at = ((W - 1 - 940) * Proto.FbW + 270) * 4;
        Assert.True(got[at + 2] > 180 && got[at] < 90, $"B={got[at]} R={got[at + 2]}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(-160)]
    public void 条带高不是16的正整数倍就拒绝(int stripRows)
    {
        Assert.Throws<ArgumentException>(() => new StripEncoder(Big, Big, stripRows: stripRows));
    }

    /// <summary>有限范围展开回全范围（Tab5 的 DSI 桥做的事）：16 以下是 0，235 以上是 255。</summary>
    private static byte FromLimited(byte v) => (byte)Math.Clamp(((v - 16) * 255 + 109) / 219, 0, 255);

    [Fact]
    public void 有限范围的两头和中间()
    {
        Assert.Equal(16, Proto.ToLimited(0));
        Assert.Equal(235, Proto.ToLimited(255));
        Assert.Equal(126, Proto.ToLimited(128));
        for (int v = 0; v < 255; v++)
        {
            Assert.True(Proto.ToLimited(v + 1) >= Proto.ToLimited(v), $"{v} 到 {v + 1} 反而变小了");
            Assert.InRange(Math.Abs(FromLimited(Proto.ToLimited(v)) - v), 0, 1);     // 压了再展开，最多差 1
        }
    }

    [Fact]
    public void 转90度时压到有限范围_三个颜色通道各自查表_第四个字节原样()
    {
        var view = Noise(11);
        var full = new byte[Proto.FbW * Proto.FbH * 4];
        var limited = new byte[full.Length];
        StripEncoder.RotateToFb(view, W * 4, full);
        StripEncoder.RotateToFb(view, W * 4, limited, limited: true);
        for (int i = 0; i < full.Length; i++)
            if (limited[i] != (i % 4 == 3 ? full[i] : Proto.ToLimited(full[i])))
                Assert.Fail($"第 {i} 个字节：全范围 {full[i]}，压完是 {limited[i]}");
    }

    [Fact]
    public void 走YUV422的连接_条带固定422_画面压到有限范围_展开回来和原画面一致()
    {
        // 桌面画面再加一块纯黑、一块纯白：黑白两头是有限范围最容易出错的地方（没压的话 Tab5 上 16 以下全黑、235 以上全白）
        var px = MemoryMarshal.Cast<byte, uint>(Desktop().AsSpan()).ToArray();
        Fill(px, 100, 500, 200, 100, 0xFF000000u);
        Fill(px, 400, 500, 200, 100, 0xFFFFFFFFu);
        byte[] view = MemoryMarshal.AsBytes(px.AsSpan()).ToArray();
        // 采样给的是 4:2:0：走 YUV422 的连接不看它
        var enc = new StripEncoder(Big, Big, quality: 92, subsampling: ChromaSubsampling.S420, yuv422: true);
        List<Strip> strips = enc.Encode(view, W * 4);
        Assert.Equal(8, strips.Count);
        Assert.All(strips, s => Assert.Equal(0x21, FakeTab5.LumaSampling(s.Jpeg)));

        byte[] got = MemoryMarshal.AsBytes(DecodeToFb(strips).AsSpan()).ToArray();
        // 解出来的是有限范围：纯黑那块中间在 16 上下、纯白那块中间在 235 上下（画面 (x, y) → 帧缓冲第 1279 − x 行、第 y 列）
        int black = ((W - 1 - 200) * Proto.FbW + 550) * 4, white = ((W - 1 - 500) * Proto.FbW + 550) * 4;
        for (int ch = 0; ch < 3; ch++)
        {
            Assert.InRange(got[black + ch], 14, 18);
            Assert.InRange(got[white + ch], 233, 237);
        }
        // 展开回全范围之后和原画面一致，红蓝没有反
        var want = new byte[Proto.FbW * Proto.FbH * 4];
        StripEncoder.RotateToFb(view, W * 4, want);
        byte[] shown = [.. got.Select((v, i) => i % 4 == 3 ? v : FromLimited(v))];
        Assert.InRange(MeanAbsDiff(want, shown), 0, 1.5);
        int at = ((W - 1 - 940) * Proto.FbW + 270) * 4;
        Assert.True(shown[at] > 180 && shown[at + 2] < 90, $"B={shown[at]} R={shown[at + 2]}");
        // 没展开的话差得多：这项测得出「接收端忘了展开 / 发送端忘了压」
        Assert.True(MeanAbsDiff(want, got) > 6, $"没展开时的平均差 {MeanAbsDiff(want, got):F1}");

        // 同一幅画面再来一次：比的是压过范围的缓冲，照样认得出没变
        enc.Commit();
        Assert.Empty(enc.Encode(view, W * 4));
    }

    [Fact]
    public void 画面尺寸不对就拒绝()
    {
        var enc = new StripEncoder(Big, Big);
        Assert.Throws<ArgumentException>(() => enc.Encode(new byte[W * 4 * (H - 1)], W * 4));
        Assert.Throws<ArgumentException>(() => enc.Encode(new byte[W * 4 * H], W * 4 - 4));
        // 一行的字节数很大（行间填充很多）而缓冲不够长：所需长度超过 int 能表示的范围，检查不能因为回绕而放过去
        Assert.Throws<ArgumentException>(() => enc.Encode(new byte[27_464], 5_973_560));
    }
}
