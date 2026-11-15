namespace WinDisplay.Core;

/// <summary>
/// 一块 SrcW×SrcH 的画面（一台显示器）怎么放进 1280×720 的横屏画面：等比缩放、居中，占满一个方向，另一个方向两边补黑。
/// X / Y / W / H 是它在横屏画面里占的那一块。比例不是 16:9 的屏（16:10、带鱼屏、竖屏）才有黑边；比 1280×720 小的屏会被放大。
/// 缩放比横竖各算各的（SrcW / W 和 SrcH / H），取整带来的差别不到一个像素。
/// </summary>
public readonly record struct ViewFit(int SrcW, int SrcH, int X, int Y, int W, int H)
{
    public static ViewFit For(int srcW, int srcH)
    {
        if (srcW <= 0) throw new ArgumentOutOfRangeException(nameof(srcW), srcW, "画面的宽要大于 0");
        if (srcH <= 0) throw new ArgumentOutOfRangeException(nameof(srcH), srcH, "画面的高要大于 0");
        int w = Proto.ViewW, h = Proto.ViewH;
        if ((long)srcW * Proto.ViewH >= (long)srcH * Proto.ViewW)       // 比 16:9 宽（或者正好）：横向占满
            h = (int)Math.Clamp((2L * srcH * Proto.ViewW + srcW) / (2L * srcW), 1, Proto.ViewH);
        else                                                            // 比 16:9 窄：纵向占满
            w = (int)Math.Clamp((2L * srcW * Proto.ViewH + srcH) / (2L * srcH), 1, Proto.ViewW);
        return new ViewFit(srcW, srcH, (Proto.ViewW - w) / 2, (Proto.ViewH - h) / 2, w, h);
    }
}
