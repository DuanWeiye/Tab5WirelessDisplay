using WinDisplay.Core;

namespace WinDisplay.Win;

/// <summary>Windows 上的抓屏，接成显示会话的画面来源。</summary>
public static class ScreenCapture
{
    /// <summary>
    /// 抓主显示器（任务栏所在的那台），缩放成 1280×720：等比、居中，比例不是 16:9 的屏两边补黑。
    /// 打不开抛 <see cref="CaptureLostException"/>；开着的时候断了（显示器拔插、显卡驱动重置……）自己隔 1 秒重开，见 <see cref="CaptureSource"/>。
    /// 用完要 Dispose（等会话的 Run 返回之后）。
    /// </summary>
    public static CaptureSource OpenPrimary(ScreenCaptureStats? stats = null) =>
        new(() => new WgcCapture(WgcCapture.PrimaryMonitor(), cursor: true, stats));
}
