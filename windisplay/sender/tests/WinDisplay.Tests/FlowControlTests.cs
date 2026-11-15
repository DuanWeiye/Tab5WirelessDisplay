using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>流量控制的算法（PROTOCOL.md「流量控制」）。时刻都是手填的毫秒数，不碰时钟。</summary>
public class FlowControlTests
{
    /// <summary>发一帧、过 rttMs 收到它的 ACK，连着来 n 帧（始终只有 1 帧在途）；返回结束时的时刻。</summary>
    private static double Exchange(FlowControl flow, ref uint seq, double nowMs, int n, double rttMs)
    {
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(0, flow.WaitBeforeNextMs(nowMs));
            flow.OnSent(seq, nowMs);
            nowMs += rttMs;
            Assert.Equal(rttMs, flow.OnAck(seq, nowMs), 6);
            seq++;
        }
        return nowMs;
    }

    /// <summary>
    /// 两帧在途的稳态：Tab5 每 periodMs 处理完一帧。开头连发两帧；之后每过一个周期，最早在途的那一帧的 ACK 到，发送端马上补发一帧。
    /// 每帧排在前一帧后面，往返是两个周期（头一帧前面没有别的，是一个周期）。从没有在途的帧开始，跑 n 个周期；
    /// 出去时 2 帧在途：一帧是一个周期之前发的，一帧刚发。返回结束时的时刻。
    /// </summary>
    private static double Pipelined(FlowControl flow, ref uint seq, double nowMs, int n, double periodMs)
    {
        Assert.Equal(0, flow.InFlight);
        flow.OnSent(seq++, nowMs);
        Assert.Equal(0, flow.WaitBeforeNextMs(nowMs));
        flow.OnSent(seq++, nowMs);
        for (int i = 0; i < n; i++)
        {
            Assert.True(flow.WaitBeforeNextMs(nowMs) > 0);              // 两帧在途、没迟到：要等
            nowMs += periodMs;
            Assert.True(flow.WaitBeforeNextMs(nowMs) > 0);              // ACK 到之前的一刻也还没算迟到
            Assert.Equal(i == 0 ? periodMs : 2 * periodMs, flow.OnAck(seq - 2, nowMs), 6);
            Assert.Equal(0, flow.WaitBeforeNextMs(nowMs));              // 只剩一帧在途：马上补发
            flow.OnSent(seq++, nowMs);
        }
        return nowMs;
    }

    [Fact]
    public void 平时保持两帧在途_不到两帧不用等ACK()
    {
        var flow = new FlowControl();
        Assert.Equal(0, flow.WaitBeforeNextMs(0));
        flow.OnSent(0, 0);
        Assert.Equal(0, flow.WaitBeforeNextMs(1));                      // 第 0 帧还没确认，第 1 帧照样可以发
        flow.OnSent(1, 1);
        Assert.Equal(double.PositiveInfinity, flow.WaitBeforeNextMs(2));    // 两帧在途了，等 ACK
        flow.OnAck(0, 30);
        Assert.Equal(0, flow.WaitBeforeNextMs(30));                     // 确认了一帧，又可以发一帧
        flow.OnSent(2, 30);
        Assert.Equal(2, flow.InFlight);
        Assert.Equal(0, flow.LateSends);                                // 这些都是平时的节奏，不算「迟到多发」
    }

    [Fact]
    public void 开头几帧往返时间还没摸准时_两帧在途就死等()
    {
        var flow = new FlowControl();
        uint seq = 0;
        double now = Exchange(flow, ref seq, 0, FlowControl.RttMinSamples - 1, 30);
        flow.OnSent(seq, now);
        flow.OnSent(seq + 1, now);
        // 只有 3 个样本：最早那一帧的 ACK 再怎么迟也不多发
        Assert.Equal(double.PositiveInfinity, flow.WaitBeforeNextMs(now + 5000));
        flow.OnAck(seq, now + 5000);
        Assert.Equal(0, flow.LateSends);
    }

    [Fact]
    public void 整屏滚动时一个ACK丢了_从最早那一帧算起过了门限_连着放到4帧在途()
    {
        // 整屏滚动、两帧在途：Tab5 每 24 ms 处理完一帧，每帧往返 48 ms，门限 = 中位数 × 2 = 96 ms
        var flow = new FlowControl();
        uint seq = 0;
        double now = Pipelined(flow, ref seq, 0, 40, 24);
        Assert.Equal(0, flow.LateSends);
        // 现在在途的是 a（now − 24 发的）和 b（now 发的）。a 的 ACK 在空中丢了，要等 Tab5 的 TCP 重传（1.3 秒后才到）
        uint a = seq - 2;
        double sentA = now - 24;
        Assert.Equal(96 - 24, flow.WaitBeforeNextMs(now), 6);           // 从 a 发出的时刻算起，不是从刚发的 b 算起
        Assert.Equal(96 - 60, flow.WaitBeforeNextMs(sentA + 60), 6);
        Assert.Equal(0, flow.WaitBeforeNextMs(sentA + 96));             // a 迟到了
        flow.OnSent(seq++, sentA + 96);
        Assert.Equal(0, flow.WaitBeforeNextMs(sentA + 96));             // 不用再等一个门限：马上还能发一帧
        flow.OnSent(seq++, sentA + 97);
        Assert.Equal(4, flow.InFlight);
        Assert.Equal(double.PositiveInfinity, flow.WaitBeforeNextMs(sentA + 1000));     // 到了 4 帧，只能等
        Assert.Equal(2, flow.LateSends);

        // 丢的那个 ACK 补到了，后面三个跟着一起到
        Assert.Equal(1300, flow.OnAck(a, sentA + 1300), 6);
        flow.OnAck(a + 1, sentA + 1301);
        flow.OnAck(a + 2, sentA + 1302);
        Assert.Equal(1, flow.InFlight);
        Assert.Equal(0, flow.WaitBeforeNextMs(sentA + 1302));           // 回到平时：不到两帧在途就能发
        flow.OnAck(a + 3, sentA + 1303);
        Assert.Equal(0, flow.InFlight);
        Assert.Equal(2, flow.LateSends);
    }

    /// <summary>上一个用例的场景走到「4 帧在途、丢的那个 ACK 刚补到并处理完」：在途 3 帧（a+1、a+2、a+3），返回 (flow, 下一个序号, a 发出的时刻)。</summary>
    private static (FlowControl Flow, uint NextSeq, double SentA) LostAckJustRecovered()
    {
        var flow = new FlowControl();
        uint seq = 0;
        double now = Pipelined(flow, ref seq, 0, 40, 24);
        uint a = seq - 2;
        double sentA = now - 24;
        flow.OnSent(seq++, sentA + 96);
        flow.OnSent(seq++, sentA + 97);
        flow.OnAck(a, sentA + 1300);
        Assert.Equal(3, flow.InFlight);
        Assert.Equal(2, flow.LateSends);
        return (flow, seq, sentA);
    }

    [Fact]
    public void 丢的ACK补到了_后面三个还没处理的那一刻_可以去准备下一帧_多不多发按真发出去那一刻算()
    {
        // TCP 按序交付：丢的那个 ACK 补到时，后面三个 ACK 其实也到了，但程序是一个一个处理的。处理完第一个的那一刻
        // 在途 3 帧，最早的那一帧早就过了门限 → 「还要等多久」是 0，发送线程可以去准备下一帧。
        // 情形一（日常）：下一帧编码要几毫秒，真发出去时后面三个 ACK 都处理完了 → 是平时的一帧，不算多发
        var (flow, seq, sentA) = LostAckJustRecovered();
        Assert.Equal(0, flow.WaitBeforeNextMs(sentA + 1300));
        flow.OnAck(seq - 3, sentA + 1300.1);
        flow.OnAck(seq - 2, sentA + 1300.2);
        flow.OnAck(seq - 1, sentA + 1300.3);
        flow.OnSent(seq, sentA + 1305);
        Assert.Equal(1, flow.InFlight);
        Assert.Equal(2, flow.LateSends);

        // 情形二：后面三个 ACK 真的还没到（它们各自又在路上耽搁了），这一帧发出去时在途的还是 3 帧 → 算多发，到 4 帧之后只能等
        (flow, seq, sentA) = LostAckJustRecovered();
        Assert.Equal(0, flow.WaitBeforeNextMs(sentA + 1300));
        flow.OnSent(seq, sentA + 1305);
        Assert.Equal(4, flow.InFlight);
        Assert.Equal(3, flow.LateSends);
        Assert.Equal(double.PositiveInfinity, flow.WaitBeforeNextMs(sentA + 5000));
    }

    [Fact]
    public void 平时和最多都是两帧_往返摸准了确认迟到也不多发()
    {
        var flow = new FlowControl(steadyInFlight: 2, maxInFlight: 2);
        uint seq = 0;
        double now = Pipelined(flow, ref seq, 0, 40, 24);       // 往返样本早就够了，门限 96 ms
        Assert.Equal(2, flow.InFlight);
        Assert.Equal(double.PositiveInfinity, flow.WaitBeforeNextMs(now + 5000));       // 最早那一帧迟到 5 秒也只能等
        Assert.Equal(0, flow.LateSends);
        flow.OnAck(seq - 2, now + 5000);
        Assert.Equal(0, flow.WaitBeforeNextMs(now + 5000));     // 确认了一帧才又能发
    }

    [Fact]
    public void 平时只留一帧在途_收到ACK才发下一帧_迟到才多发()
    {
        // SteadyInFlight = 1：每帧往返 30 ms，门限是下限 60 ms
        var flow = new FlowControl(steadyInFlight: 1);
        uint seq = 0;
        double t0 = Exchange(flow, ref seq, 0, 20, 30);
        flow.OnSent(seq, t0);
        Assert.Equal(60, flow.WaitBeforeNextMs(t0), 6);
        Assert.Equal(25, flow.WaitBeforeNextMs(t0 + 35), 6);
        Assert.Equal(0, flow.WaitBeforeNextMs(t0 + 60));
        flow.OnSent(seq + 1, t0 + 60);
        flow.OnSent(seq + 2, t0 + 60);
        flow.OnSent(seq + 3, t0 + 60);
        Assert.Equal(double.PositiveInfinity, flow.WaitBeforeNextMs(t0 + 1000));
        Assert.Equal(3, flow.LateSends);
    }

    [Fact]
    public void 死等模式_在途一帧就只能等()
    {
        var flow = new FlowControl(steadyInFlight: 1, maxInFlight: 1);
        uint seq = 0;
        double now = Exchange(flow, ref seq, 0, 20, 30);
        flow.OnSent(seq, now);
        Assert.Equal(double.PositiveInfinity, flow.WaitBeforeNextMs(now + 1300));
        Assert.Equal(0, flow.LateSends);
    }

    [Fact]
    public void 画面重每帧本来就慢时_不算迟到()
    {
        // 噪点画面、两帧在途：Tab5 每 140 ms 处理完一帧，每帧往返 280 ms，比 60 ms 的下限长得多。门限 = 中位数 × 2 = 560 ms
        var flow = new FlowControl();
        uint seq = 0;
        double now = Pipelined(flow, ref seq, 0, 12, 140);
        double sentOldest = now - 140;
        Assert.Equal(560 - 300, flow.WaitBeforeNextMs(sentOldest + 300), 6);    // 300 ms 了还没到 ACK：正常，接着等
        Assert.Equal(0, flow.WaitBeforeNextMs(sentOldest + 560));               // 560 ms 还没到才算迟到
        Assert.Equal(0, flow.LateSends);
    }

    [Fact]
    public void 门限只看最近16帧_画面从重变轻后门限跟着降回来()
    {
        var flow = new FlowControl(steadyInFlight: 1);
        uint seq = 0;
        double now = Exchange(flow, ref seq, 0, 16, 140);               // 一段噪点
        now = Exchange(flow, ref seq, now, 7, 30);                      // 换成轻的画面：16 帧里还有 9 帧是 140
        flow.OnSent(seq, now);
        Assert.Equal(280, flow.WaitBeforeNextMs(now), 6);
        flow.OnAck(seq++, now + 30);                                    // 第 8 帧轻的：140 和 30 各 8 个，中位数 = (30 + 140) / 2 = 85
        now += 30;
        flow.OnSent(seq, now);
        Assert.Equal(170, flow.WaitBeforeNextMs(now), 6);
        flow.OnAck(seq++, now + 30);                                    // 第 9 帧轻的：中位数 30，门限回到下限 60
        now += 30;
        flow.OnSent(seq, now);
        Assert.Equal(60, flow.WaitBeforeNextMs(now), 6);
    }

    [Fact]
    public void 下限可以调_往返很快时门限就是下限()
    {
        var flow = new FlowControl(steadyInFlight: 2, maxInFlight: 3, minAckWaitMs: 100);
        uint seq = 0;
        double now = Exchange(flow, ref seq, 0, 8, 12);                 // 局部更新：往返 12 ms，× 2 = 24 < 100
        flow.OnSent(seq, now);
        flow.OnSent(seq + 1, now + 5);
        Assert.Equal(95, flow.WaitBeforeNextMs(now + 5), 6);            // 从最早在途的那一帧（now 发的）算起
        flow.OnSent(seq + 2, now + 100);
        Assert.Equal(double.PositiveInfinity, flow.WaitBeforeNextMs(now + 500));    // 上限是 3 帧
        Assert.Equal(1, flow.LateSends);
    }

    [Fact]
    public void ACK对不上在途的帧_算对端不守协议()
    {
        var flow = new FlowControl();
        Assert.Throws<ProtocolException>(() => flow.OnAck(0, 10));      // 没有在途的帧
        flow.OnSent(0, 0);
        flow.OnSent(1, 5);
        Assert.Throws<ProtocolException>(() => flow.OnAck(1, 10));      // 跳过了第 0 帧
        Assert.Equal(2, flow.InFlight);                                 // 出错的 ACK 不改状态
        flow.OnAck(0, 10);
        flow.OnAck(1, 11);
        Assert.Throws<ProtocolException>(() => flow.OnAck(1, 12));      // 同一帧确认两次
    }

    [Theory]
    [InlineData(0, 4, 60.0)]
    [InlineData(-1, 4, 60.0)]
    [InlineData(1, 0, 60.0)]
    [InlineData(3, 2, 60.0)]        // 上限比平时的还少
    [InlineData(2, 4, 0.0)]
    [InlineData(2, 4, -5.0)]
    public void 参数不合理直接拒绝(int steadyInFlight, int maxInFlight, double minAckWaitMs)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowControl(steadyInFlight, maxInFlight, minAckWaitMs));
    }

    // ── 跟得上画面源时多放一帧 ───────────────────────────────────────────

    /// <summary>发送端跟得上画面源的一段：每 17 ms 一幅，每一帧都是等画面源等来的，发出 10 ms 后确认。返回结束的时刻（这时没有在途的帧）。</summary>
    private static double KeepingUp(FlowControl flow, ref uint seq, double nowMs, int n)
    {
        for (int i = 0; i < n; i++)
        {
            flow.OnSourceIdle(nowMs);
            flow.OnSent(seq, nowMs);
            flow.OnAck(seq++, nowMs + 10);
            nowMs += 17;
        }
        return nowMs;
    }

    [Fact]
    public void 多放一帧_连着三帧跟上画面源之后_两帧在途时还能再发一帧()
    {
        var flow = new FlowControl();
        uint seq = 0;
        double t = KeepingUp(flow, ref seq, 0, 2);                      // 只有 2 帧是等来的
        flow.OnSent(seq++, t);
        flow.OnSent(seq++, t + 5);
        Assert.True(flow.WaitBeforeNextMs(t + 6) > 0);                  // 还不算跟上：2 帧在途就等
        flow.OnAck(seq - 2, t + 10);
        flow.OnAck(seq - 1, t + 15);
        t = KeepingUp(flow, ref seq, t + 17, 2);                        // 中间那两帧不是等来的：重新数，2 帧还不够
        flow.OnSourceIdle(t);                                           // 第 3 帧也是等来的：跟上了
        flow.OnSent(seq++, t);
        flow.OnSent(seq++, t + 5);
        Assert.Equal(0, flow.WaitBeforeNextMs(t + 6));                  // 2 帧在途：可以多放一帧
        flow.OnSent(seq++, t + 6);
        Assert.Equal((3, 1, 0), (flow.InFlight, flow.SpareSends, flow.LateSends));
        Assert.True(flow.WaitBeforeNextMs(t + 7) > 0);                  // 只多放一帧
    }

    [Fact]
    public void 多放一帧_追的那一阵一直可以用_过了时限还没跟上就收回_跟上了再放开()
    {
        var flow = new FlowControl();
        uint seq = 0;
        double t = KeepingUp(flow, ref seq, 0, 3);
        flow.OnSourceIdle(t);                                           // 上一次跟上是在 t
        flow.OnSent(seq++, t);
        flow.OnSent(seq++, t + 17);
        uint oldest = seq - 2;
        // 追的那一阵：每 17 ms 发一帧（发的时候 2 帧在途），过 16 ms 最早的一帧确认——一直顶着 3 帧在途
        double now = t + 34;
        int spare = 0;
        for (; now <= t + 500; now += 17)
        {
            Assert.Equal(0, flow.WaitBeforeNextMs(now));                // 还在时限里：接着多放，不用等在途的落回 1 帧
            flow.OnSent(seq++, now);
            spare++;
            flow.OnSourceIdle(now + 5);                                 // 3 帧在途时等过画面源：不算跟上，时限不重新算
            flow.OnAck(oldest++, now + 16);
        }
        Assert.True(flow.WaitBeforeNextMs(now) > 0);                    // 离上一次跟上超过 500 ms：回到平时的 2 帧在途
        Assert.Equal((spare, 0), (flow.SpareSends, flow.LateSends));
        flow.OnAck(oldest++, now + 5);
        flow.OnAck(oldest++, now + 10);
        double u = KeepingUp(flow, ref seq, now + 17, 2);               // 追的那些帧不是等来的，重新数：2 帧不够
        flow.OnSent(seq, u);
        flow.OnSent(seq + 1, u + 5);
        Assert.True(flow.WaitBeforeNextMs(u + 6) > 0);
        flow.OnAck(seq, u + 10);
        flow.OnAck(seq + 1, u + 15);
        seq += 2;
        u = KeepingUp(flow, ref seq, u + 17, 3);                        // 又连着 3 帧跟上：放开
        flow.OnSent(seq, u);
        flow.OnSent(seq + 1, u + 5);
        Assert.Equal(0, flow.WaitBeforeNextMs(u + 6));
    }

    [Fact]
    public void 多放一帧_放行之后发出来时时限刚过_还算多放的_最早一帧的确认迟到了才算确认迟到()
    {
        // 放行和发出之间隔着等画面源、编码：放行时还在时限里，发出来时过了
        var flow = new FlowControl();
        uint seq = 0;
        double t = KeepingUp(flow, ref seq, 0, 5);
        flow.OnSourceIdle(t);                                           // 上一次跟上是在 t
        flow.OnSent(seq++, t + 470);
        flow.OnSent(seq++, t + 480);
        Assert.Equal(0, flow.WaitBeforeNextMs(t + 490));                // 2 帧在途，还在时限里：放行
        flow.OnSent(seq++, t + 520);                                    // 发出来时离上一次跟上已经 520 ms
        Assert.Equal((1, 0), (flow.SpareSends, flow.LateSends));

        // 对照：也在时限里，但最早在途的那一帧发出 100 ms 了还没确认（门限 60 ms）——没有多放这条规则它也会放行，算确认迟到
        flow = new FlowControl();
        seq = 0;
        t = KeepingUp(flow, ref seq, 0, 5);
        flow.OnSourceIdle(t);
        flow.OnSent(seq++, t);
        flow.OnSent(seq++, t + 17);
        Assert.Equal(0, flow.WaitBeforeNextMs(t + 100));
        flow.OnSent(seq++, t + 100);
        Assert.Equal((0, 1), (flow.SpareSends, flow.LateSends));
    }

    [Fact]
    public void 多放一帧_画面源一直有新画面等着时用不上_和原来一样两帧在途()
    {
        // 画面源比管线快：发送端从来不用等画面源（不调 OnSourceIdle），在途的到了 2 帧就等 ACK
        var flow = new FlowControl();
        uint seq = 0;
        Pipelined(flow, ref seq, 0, 40, 15.3);
        Assert.Equal((2, 0, 0), (flow.InFlight, flow.SpareSends, flow.LateSends));
    }

    [Theory]
    [InlineData(2, 4, 0.0)]         // 关掉了
    [InlineData(2, 2, 500.0)]       // 最多也只许 2 帧在途：没有位置可放
    public void 多放一帧_关掉或者没有位置时_跟上过也不多发(int steady, int max, double hold)
    {
        var flow = new FlowControl(steady, max, spareHoldMs: hold);
        Assert.Equal(0, flow.SpareHoldMs);
        uint seq = 0;
        double t = KeepingUp(flow, ref seq, 0, 5);
        flow.OnSourceIdle(t);
        flow.OnSent(seq, t);
        flow.OnSent(seq + 1, t + 5);
        Assert.True(flow.WaitBeforeNextMs(t + 6) > 0);
    }

    [Fact]
    public void 连接中途换平时几帧在途_改多了下一帧就能多发_改回去要等在途的少下来()
    {
        var flow = new FlowControl(spareHoldMs: 0);
        flow.OnSent(0, 0);
        flow.OnSent(1, 10);
        Assert.True(flow.WaitBeforeNextMs(20) > 0);                     // 平时 2 帧在途：第三帧要等
        flow.SetSteadyInFlight(3);                                      // 画质换到 均衡档
        Assert.Equal(0, flow.WaitBeforeNextMs(20));
        flow.OnSent(2, 20);
        Assert.Equal(0, flow.LateSends);                                // 这一帧是平时的在途帧数里的，不算「确认迟到多发的」
        Assert.True(flow.WaitBeforeNextMs(30) > 0);                     // 3 帧在途了：再等
        flow.SetSteadyInFlight(2);                                      // 换回别的档
        flow.OnAck(0, 40);
        Assert.True(flow.WaitBeforeNextMs(40) > 0);                     // 还有 2 帧在途
        flow.OnAck(1, 45);
        Assert.Equal(0, flow.WaitBeforeNextMs(45));
        Assert.Throws<ArgumentOutOfRangeException>(() => flow.SetSteadyInFlight(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => flow.SetSteadyInFlight(flow.MaxInFlight + 1));
    }

    [Fact]
    public void 多放一帧_时限不能是负数()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowControl(spareHoldMs: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowControl(spareHoldMs: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowControl(spareHoldMs: double.PositiveInfinity));
    }

    /// <summary>
    /// 照 DisplaySession 的节奏走一遍。画面源每 periodMs 出一幅，只留最新的一幅。发送端：流量控制放行了才去取画面，取的时候没有新的就等到下一幅出来
    /// （等完告诉流量控制），取到之后 EncodeMs 发出。Tab5：一帧发出后最快 rtt(i) 毫秒确认，而且两帧的确认至少隔 tab5Ms
    /// （它按屏幕的刷新一帧一帧上屏；或者它处理一帧就要这么久）。
    /// 返回被顶掉、没发出去的幅数，和每一帧发出时的（时刻，连这一帧在内在途几帧）。
    /// </summary>
    private static (int Skipped, List<(double At, int InFlight)> Sends) Stream(FlowControl flow, int frames, double periodMs, double tab5Ms, Func<int, double> rtt,
                                                                               double startMs = 0, uint firstSeq = 0)
    {
        const double EncodeMs = 2;
        var acks = new Queue<(uint Seq, double At)>();
        var sends = new List<(double, int)>();
        double lastAck = double.NegativeInfinity;
        int skipped = 0;
        int taken = -1;                                                 // 取走的最后一幅是第几幅（画面源从 startMs 起出第 0 幅）
        double now = startMs;
        void Deliver(double until)
        {
            while (acks.Count > 0 && acks.Peek().At <= until)
            {
                var (seq, at) = acks.Dequeue();
                flow.OnAck(seq, at);
            }
        }
        while (true)
        {
            // 1. 等流量控制放行：等到下一个 ACK，或者等够了「确认迟到」的门限
            Deliver(now);
            for (double wait; (wait = flow.WaitBeforeNextMs(now)) > 0; Deliver(now))
            {
                Assert.True(acks.Count > 0, "没有在途的帧却不让发");
                now = Math.Min(now + wait, Math.Max(now, acks.Peek().At));
            }
            // 2. 取最新的一幅：没有新的就等到下一幅出来
            int latest = (int)Math.Floor((now - startMs) / periodMs);
            if (latest == taken)
            {
                latest = taken + 1;
                now = startMs + latest * periodMs;
                Deliver(now);
                flow.OnSourceIdle(now);
            }
            if (latest >= frames) return (skipped, sends);
            skipped += latest - taken - 1;
            taken = latest;
            // 3. 压、发
            now += EncodeMs;
            Deliver(now);
            int n = sends.Count;
            flow.OnSent(firstSeq + (uint)n, now);
            sends.Add((now, flow.InFlight));
            lastAck = Math.Max(lastAck + tab5Ms, now + rtt(n));
            acks.Enqueue((firstSeq + (uint)n, lastAck));
        }
    }

    // 实测的样子（全屏在动、每帧 36 KB，每秒 60 幅）：屏幕 15.3 ms 刷一次，往返中位 27 ms；每秒两三次有一帧晚到二十来毫秒（错过一次刷新）
    private const double Period60 = 1000.0 / 60, PanelMs = 15.3;
    private static double RttWithHiccups(int i) => i % 25 == 7 ? 48 : 27;

    [Fact]
    public void 多放一帧_每秒60幅_隔一阵有一帧晚到错过一次刷新_只许两帧在途每次丢一幅_多放一帧不丢()
    {
        var strict = new FlowControl(spareHoldMs: 0);
        var spare = new FlowControl();
        int lost = Stream(strict, 600, Period60, PanelMs, RttWithHiccups).Skipped;
        var (kept, sends) = Stream(spare, 600, Period60, PanelMs, RttWithHiccups);
        Assert.InRange(lost, 20, 30);                                   // 10 秒里晚到 24 次，差不多每次一幅
        Assert.Equal(0, kept);
        Assert.InRange(spare.SpareSends, 24, 300);                      // 每次晚到之后多放几帧，追回来就不用了
        Assert.Equal((0, 0), (strict.LateSends, spare.LateSends));
        Assert.Equal(3, sends.Max(s => s.InFlight));
        // 平时还是 1、2 帧在途：多放的只在追的那一阵
        Assert.True(sends.Count(s => s.InFlight <= 2) > sends.Count / 2, $"3 帧在途的有 {sends.Count(s => s.InFlight == 3)} / {sends.Count} 帧");
    }

    [Fact]
    public void 多放一帧_画面本来停着_接着来的画面Tab5处理不过来_多放半秒就收回_之后一直两帧在途()
    {
        // 重的帧：Tab5 每 22 ms 才处理完一帧，画面源每 16.7 ms 一幅
        static double Rtt(int i) => 30;
        var strict = new FlowControl(spareHoldMs: 0);
        var spare = new FlowControl();
        uint seq = 0;
        double t = KeepingUp(spare, ref seq, 0, 5);                     // 画面本来只是零星地变（打字、光标）：每一帧都是等来的
        int sentStrict = Stream(strict, 600, Period60, 22, Rtt).Sends.Count;
        var sends = Stream(spare, 600, Period60, 22, Rtt, startMs: t, firstSeq: seq).Sends;
        Assert.InRange(sends.Count - sentStrict, 0, 1);                 // 帧率是 Tab5 定的，多放也不多发（顶多多出开头排进去的那一帧）
        Assert.Equal(3, sends.Where(s => s.At <= t + 500).Max(s => s.InFlight));
        Assert.All(sends.Where(s => s.At > t + 600), s => Assert.True(s.InFlight <= 2, $"{s.At:F0} ms 时 {s.InFlight} 帧在途"));
        Assert.InRange(spare.SpareSends, 1, 30);                        // 只有头半秒
        Assert.Equal((0, 0), (strict.LateSends, spare.LateSends));
    }

    [Fact]
    public void 多放一帧_Tab5只比画面源慢一点时_不会一直三帧在途()
    {
        // 画面源每秒 60 幅，Tab5 每 17.5 ms 一帧（57 帧/秒），往返有一两毫秒的抖动。开头那 0.5 秒多放一帧，之后发送端一直有新画面可取，
        // 不用等画面源：过了时限回到 2 帧在途，不再多放
        static double Rtt(int i) => 27 + i * 7 % 5 * 0.6;
        var flow = new FlowControl();
        uint seq = 0;
        double t = KeepingUp(flow, ref seq, 0, 5);
        var sends = Stream(flow, 1200, Period60, 17.5, Rtt, startMs: t, firstSeq: seq).Sends;
        var late = sends.Where(s => s.At > t + 2000).ToList();
        Assert.True(late.Count(s => s.InFlight == 3) < late.Count / 10, $"2 秒以后 3 帧在途的有 {late.Count(s => s.InFlight == 3)} / {late.Count} 帧");
    }

    [Fact]
    public void 多放一帧_Tab5和画面源几乎一样快_晚到一次追不回来_靠多放的那一帧等到画面源不算跟上()
    {
        // 画面源和屏幕几乎一样快（Tab5 每帧只比画面源快 0.05 ms），第 60 帧晚到 20 ms：Tab5 落下的这一截几百帧也追不回来。
        // 多放一帧之后发送端每帧都比画面源早 1 ms 腾出手（等它一下），但那时在途的还是 2 帧——这要是算跟上，Tab5 追平之前（几百帧）都是 3 帧在途、
        // 每帧多晚 20 ms；回到 2 帧在途只丢一两幅就收回来了
        static double Rtt(int i) => i == 60 ? 47 : 27;
        var flow = new FlowControl();
        var sends = Stream(flow, 900, Period60, Period60 - 0.05, Rtt).Sends;
        double hiccup = sends[60].At;
        Assert.Equal(3, sends.Where(s => s.At > hiccup && s.At <= hiccup + 500).Max(s => s.InFlight));     // 晚到之后先多放着
        var after = sends.Where(s => s.At > hiccup + 700).ToList();
        Assert.True(after.Count > 500);
        Assert.All(after, s => Assert.True(s.InFlight <= 2, $"晚到之后 {s.At - hiccup:F0} ms 还是 {s.InFlight} 帧在途"));
    }

    [Fact]
    public void 多放一帧_画面源隔几帧少出一幅_Tab5又比它慢_偶尔等到它一次不算跟上()
    {
        // 实测的样子：全屏在动、每帧 70 KB，Tab5 每 23.7 ms 一帧（42 帧/秒）；画面源每秒 60 幅里少出 10 幅（每 6 幅少 1 幅，两幅隔 33 ms）。
        // 发送端每次碰上少出的那一幅都会等画面源一下，但连不上 3 帧
        static double Rtt(int i) => 30;
        var flow = new FlowControl();
        uint seq = 0;
        double t = KeepingUp(flow, ref seq, 0, 5);
        var sends = GappySource(flow, 3000, Period60, 6, 23.7, Rtt, t, seq);
        var late = sends.Where(s => s.At > t + 1000).ToList();
        Assert.True(late.Count > 1500);
        Assert.True(late.Count(s => s.InFlight == 3) < late.Count / 20, $"1 秒以后 3 帧在途的有 {late.Count(s => s.InFlight == 3)} / {late.Count} 帧");
    }

    /// <summary>和 Stream 一样的节奏，只是画面源每 skipEvery 幅少出 1 幅（那一幅根本没有，不是被顶掉的）。只返回各帧发出时的（时刻，在途几帧）。</summary>
    private static List<(double At, int InFlight)> GappySource(FlowControl flow, int slots, double periodMs, int skipEvery, double tab5Ms,
                                                              Func<int, double> rtt, double startMs, uint firstSeq)
    {
        var acks = new Queue<(uint Seq, double At)>();
        var sends = new List<(double, int)>();
        double lastAck = double.NegativeInfinity, now = startMs;
        int taken = -1;                                                 // 取走的最后一幅在第几个画面周期
        bool Has(int slot) => slot % skipEvery != skipEvery - 1;        // 这个画面周期出不出画面
        void Deliver(double until)
        {
            while (acks.Count > 0 && acks.Peek().At <= until)
            {
                var (seq, at) = acks.Dequeue();
                flow.OnAck(seq, at);
            }
        }
        while (true)
        {
            Deliver(now);
            for (double wait; (wait = flow.WaitBeforeNextMs(now)) > 0; Deliver(now))
            {
                Assert.True(acks.Count > 0, "没有在途的帧却不让发");
                now = Math.Min(now + wait, Math.Max(now, acks.Peek().At));
            }
            int latest = (int)Math.Floor((now - startMs) / periodMs);
            while (latest > taken && !Has(latest)) latest--;            // 最新的一幅：往回找到出了画面的那个周期
            if (latest <= taken)
            {
                latest = (int)Math.Floor((now - startMs) / periodMs) + 1;
                while (!Has(latest)) latest++;
                now = startMs + latest * periodMs;
                Deliver(now);
                flow.OnSourceIdle(now);
            }
            if (latest >= slots) return sends;
            taken = latest;
            now += 2;
            Deliver(now);
            int n = sends.Count;
            flow.OnSent(firstSeq + (uint)n, now);
            sends.Add((now, flow.InFlight));
            lastAck = Math.Max(lastAck + tab5Ms, now + rtt(n));
            acks.Enqueue((firstSeq + (uint)n, lastAck));
        }
    }
}
