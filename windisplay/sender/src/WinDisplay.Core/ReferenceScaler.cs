namespace WinDisplay.Core;

/// <summary>
/// 把一幅画面缩放成 1280×720 的对照实现：算法和 Windows 上显卡里跑的那个着色器一样（位置按 <see cref="ViewFit"/>，
/// 每个目标像素取它盖住的那块源画面的面积加权平均，四周补黑），但是在 CPU 上逐像素用双精度算，慢（一幅几十毫秒）。
/// 不用在发画面的路上：单元测试拿它核对显卡算出来的结果，抓屏自检拿它把另一条路抓到的整屏画面缩成同样大小来比。
/// </summary>
internal static class ReferenceScaler
{
    /// <param name="src">源画面，每像素 4 字节（前 3 个字节是颜色，第 4 个不用），一行 stride 字节。</param>
    /// <returns>1280×720、每像素 4 字节、没有行间填充；第 4 个字节恒为 255。</returns>
    public static byte[] Scale(ReadOnlySpan<byte> src, int srcW, int srcH, int stride)
    {
        ViewFit fit = ViewFit.For(srcW, srcH);
        if (stride < srcW * 4 || src.Length < (long)stride * (srcH - 1) + srcW * 4L)
            throw new ArgumentException($"缓冲不够放 {srcW}×{srcH} 的画面");
        var dst = new byte[Proto.ViewW * 4 * Proto.ViewH];
        for (int i = 3; i < dst.Length; i += 4) dst[i] = 255;
        double sx = (double)srcW / fit.W, sy = (double)srcH / fit.H;       // 一个目标像素盖住几个源像素
        Span<double> sum = stackalloc double[3];
        for (int dy = 0; dy < fit.H; dy++)
        {
            double ay = dy * sy, by = Math.Min(ay + sy, srcH);
            int y0 = (int)ay, y1 = (int)Math.Ceiling(by);
            for (int dx = 0; dx < fit.W; dx++)
            {
                double ax = dx * sx, bx = Math.Min(ax + sx, srcW);
                int x0 = (int)ax, x1 = (int)Math.Ceiling(bx);
                sum.Clear();
                double wsum = 0;
                for (int y = y0; y < y1; y++)
                {
                    double wy = Math.Min(by, y + 1) - Math.Max(ay, y);
                    ReadOnlySpan<byte> row = src.Slice(y * stride, srcW * 4);
                    for (int x = x0; x < x1; x++)
                    {
                        double w = (Math.Min(bx, x + 1) - Math.Max(ax, x)) * wy;
                        sum[0] += row[x * 4] * w;
                        sum[1] += row[x * 4 + 1] * w;
                        sum[2] += row[x * 4 + 2] * w;
                        wsum += w;
                    }
                }
                int o = ((fit.Y + dy) * Proto.ViewW + fit.X + dx) * 4;
                for (int c = 0; c < 3; c++) dst[o + c] = (byte)Math.Round(sum[c] / wsum, MidpointRounding.AwayFromZero);
            }
        }
        return dst;
    }
}
