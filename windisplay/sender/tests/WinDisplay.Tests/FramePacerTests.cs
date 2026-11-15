using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

public class FramePacerTests
{
    // 照 TestPatternSource 的用法跑一遍：每次到 askAt 来取，没到时间就等到时间再给。返回每一帧给出的时刻
    private static List<double> Take(FramePacer pacer, params double[] askAt)
    {
        var given = new List<double>();
        foreach (double t in askAt)
        {
            double at = t + pacer.WaitMs(t);
            pacer.Given(at);
            given.Add(at);
        }
        return given;
    }

    [Fact]
    public void 取得比限的快_每帧隔一个周期给()
    {
        var pacer = new FramePacer(10);
        // 发送端 30 ms 就能处理完一帧，回头马上来取下一帧
        Assert.Equal([0, 100, 200, 300, 400], Take(pacer, 0, 30, 130, 230, 330));
    }

    [Fact]
    public void 偶尔晚了不到一个周期_后面还按原来的节拍_不整体往后拖()
    {
        var pacer = new FramePacer(10);
        // 第三帧因为等 ACK 晚了 60 ms 才来取：它晚 60 ms 给，第四帧仍然在 300 ms 那一拍上
        Assert.Equal([0, 100, 260, 300, 400], Take(pacer, 0, 30, 260, 290, 330));
    }

    [Fact]
    public void 连接断了十几秒再接上_欠的不补发_从接上那一刻重新起拍()
    {
        var pacer = new FramePacer(10);
        var given = Take(pacer, 0, 30, 130);                        // 0、100、200
        // Tab5 退出了显示模式，13.5 秒之后页面重新打开、连上了才又有人取
        given = Take(pacer, 13_700, 13_701, 13_702, 13_830);
        Assert.Equal([13_700, 13_800, 13_900, 14_000], given);      // 不是 13700、13701、13702……那样连着给
    }

    [Fact]
    public void 第一帧很晚才来取_也不补()
    {
        var pacer = new FramePacer(20);
        // 先广播询问、握手，1.5 秒之后才取第一帧
        Assert.Equal([1500, 1550, 1600], Take(pacer, 1500, 1501, 1560));
    }

    [Fact]
    public void 对面一直处理不过来_来取就给_不额外等()
    {
        var pacer = new FramePacer(50);                             // 限 50 帧/秒，链路只跑得动 28 帧/秒
        double[] ask = [0, 35, 70, 105, 140, 175, 210];
        Assert.Equal(ask, Take(pacer, ask));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void 帧率不是正数_不收(double fps) => Assert.Throws<ArgumentOutOfRangeException>(() => new FramePacer(fps));
}
