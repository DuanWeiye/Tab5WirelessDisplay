namespace WinDisplay.Core;

/// <summary>画质的四档（PROTOCOL.md「画质」）：人在 Tab5 的主页面上选，Tab5 用 PICTURE_MODE 报过来。数值就是报文里的 arg。</summary>
public enum PictureTier
{
    /// <summary>最佳画质：不限帧率，画面在动也不降质量；整屏在动时帧率落到多少算多少。</summary>
    BestQuality = 0,
    /// <summary>30：限到屏幕刷新率的 1/2（32.7 帧/秒），在这个帧率上尽量清楚。</summary>
    Fps30 = 1,
    /// <summary>45：限到屏幕刷新率的 2/3（43.6 帧/秒），在这个帧率上尽量清楚。</summary>
    Fps45 = 2,
    /// <summary>最佳速度：不限帧率，尽量跟上画面源（朝 60 帧/秒靠）。</summary>
    BestSpeed = 3,
}

/// <summary>
/// 一档画质怎么做。
/// </summary>
/// <param name="FramesPerRefresh">限帧率：每次屏幕刷新最多发几帧（1/2、2/3）；0 = 不限。</param>
/// <param name="StartBytes">画面在动时每帧的字节数预算，连接开头用多少（之后由 <see cref="BudgetControl"/> 调）；0 = 不降质量（三个数都是 0）。</param>
/// <param name="MinBytes">预算最低调到多少。</param>
/// <param name="MaxBytes">预算最高调到多少。</param>
/// <param name="InFlight">平时至少几帧在途（<see cref="FlowControl.SteadyInFlight"/>）；0 = 不管，按会话的选项。</param>
public readonly record struct TierPlan(double FramesPerRefresh, int StartBytes, int MinBytes, int MaxBytes, int InFlight = 0);

public static class PictureTiers
{
    /// <summary>
    /// 画面不动时的 JPEG 质量，四档都一样（画面停下来之后按它补清晰，见 StripEncoder）。
    /// 90：字的边缘看不出压缩的痕迹；再高每帧大得多、看不出差别。
    /// </summary>
    public const int StillQuality = 90;

    public const int Count = 4;

    /// <summary>
    /// 各档的预算范围（起点是实测站得住的数上下，实际站在哪儿由 BudgetControl 闭环找）。
    /// 2026-11-15 真机、整屏滚动的重画面（按质量 90 压一帧 180 KB 上下）：高画质档站在 80~95 KB（质量 73~78，32.5 帧/秒），
    /// 均衡档站在 45~53 KB（质量 54 上下，43.4 帧/秒），最佳速度降到下限 20 KB（质量 20，60.5 帧/秒）。
    /// Tab5 每帧有一块固定的开销（硬件解码整屏 4:2:2 要 13.7 ms，解完还要等屏幕刷新上屏），帧率越高留给「多收一些字节」的时间越少，
    /// 所以预算不和帧率成反比：均衡档只比不限帧率的时候大一截，高画质档才大得多。
    /// 下限是「再小画面就糊得没法看了」的那个数，不是链路的限制。
    /// 均衡档平时 3 帧在途：Tab5 一帧从发出到确认，除了接收还要 31 毫秒上下（解码 13.7 + 等屏幕刷新上屏约 15 + 来回），
    /// 「平时 2 帧在途」等于要求它不超过两拍。高画质档两拍 61 毫秒，够每帧七八十 KB；均衡档两拍只有 45.8 毫秒，留给接收的才十来毫秒——
    /// 2026-11-15 实测预算停在 34 KB（质量 43~79），比不限帧率、40 KB 的时候还糊。Tab5 那头是接收、解码、等上屏三段流水，
    /// 三帧各占一段并不排队，所以 均衡档放到 3 帧（三拍 68.7 毫秒）。
    /// </summary>
    public static TierPlan Plan(PictureTier tier) => tier switch
    {
        PictureTier.BestQuality => new TierPlan(0, 0, 0, 0),
        PictureTier.Fps30 => new TierPlan(1.0 / 2, 80 * 1024, 30 * 1024, 160 * 1024),
        PictureTier.Fps45 => new TierPlan(2.0 / 3, 50 * 1024, 20 * 1024, 110 * 1024, InFlight: 3),
        PictureTier.BestSpeed => new TierPlan(0, 28 * 1024, 20 * 1024, 60 * 1024),
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "没有这一档"),
    };

    /// <summary>Tab5 界面上这一档的叫法。</summary>
    public static string Name(PictureTier tier) => tier switch
    {
        PictureTier.BestQuality => "最佳画质",
        PictureTier.Fps30 => "高画质",
        PictureTier.Fps45 => "均衡",
        PictureTier.BestSpeed => "最佳速度",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "没有这一档"),
    };
}
