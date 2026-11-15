using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>
/// 闭环调「画面在动时每帧的预算」（PROTOCOL.md「画质」）。前一半是手填时刻的小用例；后一半把流量控制、限帧率的节拍和它接在一起，
/// 对着一个照实测数字做的 Tab5 跑几十秒（<see cref="Play"/>），看预算停在哪、稳不稳。都不碰时钟。
/// </summary>
public class BudgetControlTests
{
    private const int KB = 1024;

    /// <summary>从 nowMs 起每 gapMs 一帧在动的，共 n 帧，其中每 behindEvery 帧有一帧没跟上（0 = 都跟上）；每帧 bytes 字节（-1 = 把预算用满）。返回结束时的时刻。</summary>
    private static double Feed(BudgetControl budget, double nowMs, int n, double gapMs = 23, int behindEvery = 0, int bytes = -1)
    {
        for (int i = 0; i < n; i++)
        {
            budget.OnFrame(true, behindEvery == 0 || i % behindEvery != 0, bytes < 0 ? budget.Bytes * 95 / 100 : bytes, nowMs);
            nowMs += gapMs;
        }
        return nowMs;
    }

    [Fact]
    public void 范围不对_抛异常_起点夹进范围里()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BudgetControl(50 * KB, 0, 100 * KB));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BudgetControl(50 * KB, 60 * KB, 40 * KB));
        Assert.Equal(20 * KB, new BudgetControl(5 * KB, 20 * KB, 110 * KB).Bytes);
        Assert.Equal(110 * KB, new BudgetControl(500 * KB, 20 * KB, 110 * KB).Bytes);
    }

    [Fact]
    public void 每帧都跟上_预算用满了_隔两秒升一步_到上限为止()
    {
        var budget = new BudgetControl(50 * KB, 20 * KB, 110 * KB);
        double t = Feed(budget, 0, 80);                 // 1.8 秒：还没到两秒，不动
        Assert.Equal(50 * KB, budget.Bytes);
        t = Feed(budget, t, 30);                        // 过了两秒：升 6%
        Assert.Equal((int)(50 * KB * 1.06), budget.Bytes);
        Feed(budget, t, 44 * 60);                       // 一分钟：早就到上限了
        Assert.Equal(110 * KB, budget.Bytes);
    }

    [Fact]
    public void 每帧都跟上_但画面简单_预算没用满_不往上升()
    {
        var budget = new BudgetControl(50 * KB, 20 * KB, 110 * KB);
        Feed(budget, 0, 44 * 60, bytes: 30 * KB);       // 一分钟，按原质量压出来每帧才 30 KB
        Assert.Equal(50 * KB, budget.Bytes);
    }

    [Theory]
    [InlineData(3, 0.85)]       // 三帧里一帧没跟上（33%）：降一成半
    [InlineData(1, 0.7)]        // 帧帧没跟上：降三成
    public void 连着两个半秒没跟上的帧都多_降(int behindEvery, double scale)
    {
        var budget = new BudgetControl(80 * KB, 30 * KB, 160 * KB);
        double t = Feed(budget, 0, 40, gapMs: 30.6, behindEvery: behindEvery);      // 1.2 秒：开头半秒不看，再看了半秒——只有一段，先不降
        Assert.Equal(80 * KB, budget.Bytes);
        Feed(budget, t, 18, gapMs: 30.6, behindEvery: behindEvery);                 // 又半秒还是这样：降
        Assert.Equal((int)(80 * KB * scale), budget.Bytes);
    }

    /// <summary>一帧一帧喂（都把预算用满），每帧跟没跟上由 keptUp(第几帧) 定；返回结束时的时刻和这中间预算降过几次。</summary>
    private static (double EndMs, int Drops) FeedWatch(BudgetControl budget, double nowMs, int n, Func<int, bool> keptUp, double gapMs = 23)
    {
        int drops = 0;
        for (int i = 0; i < n; i++, nowMs += gapMs)
        {
            int before = budget.Bytes;
            budget.OnFrame(true, keptUp(i), budget.Bytes * 95 / 100, nowMs);
            if (budget.Bytes < before) drops++;
        }
        return (nowMs, drops);
    }

    [Fact]
    public void 无线上停了一下_之后的几帧都晚_只这一段_不降()
    {
        // 实测：路由器每 30 秒整停 60 毫秒上下，那之后六七帧都是等确认等来的；别的时候都跟得上。
        // 预算先站稳（这里是升到了上限，离上一次升早过了 5 秒），再来四次停顿：一次都不降
        var budget = new BudgetControl(50 * KB, 20 * KB, 110 * KB);
        double t = Feed(budget, 0, 44 * 45);
        Assert.Equal(110 * KB, budget.Bytes);
        int drops = 0;
        for (int round = 0; round < 4; round++)
        {
            (t, int d) = FeedWatch(budget, t, 1300, i => i >= 7);       // 停顿之后的 7 帧没跟上，之后 30 秒都跟上
            drops += d;
        }
        Assert.Equal(0, drops);
        Assert.Equal(110 * KB, budget.Bytes);
    }

    [Fact]
    public void 停顿正落在刚升了一步之后_退回升之前的数_半分钟里不再往那儿升()
    {
        // 上面那条的例外：升了一步之后 5 秒之内，一段没跟上的帧多就当成「这一步没站住」，分不出是升出来的还是碰巧停了一下。
        // 代价是退回一步（6%）、半分钟不往上试，比「为一次停顿降一成半」小
        var budget = new BudgetControl(50 * KB, 20 * KB, 110 * KB);
        double t = 0;
        while (budget.Bytes == 50 * KB) t = Feed(budget, t, 1);         // 跟得上，两秒多之后升了一步
        int raised = budget.Bytes;
        Assert.Equal((int)(50 * KB * 1.06), raised);
        Assert.InRange(t, 2000, 2600);
        (t, int drops) = FeedWatch(budget, t, 30, i => i >= 7);         // 紧接着停了一下：7 帧没跟上，这半秒里占三成
        Assert.Equal(1, drops);
        Assert.Equal(50 * KB, budget.Bytes);
        (t, drops) = FeedWatch(budget, t, 44 * 25, _ => true);          // 之后 25 秒都跟上：不往站不住的那个数升
        Assert.Equal(0, drops);
        Assert.Equal(50 * KB, budget.Bytes);
        Feed(budget, t, 44 * 8);                                        // 过了半分钟：再往上试，这回站住了，接着升
        Assert.True(budget.Bytes > raised, $"过了半分钟预算还是 {budget.Bytes / KB} KB");
    }

    [Fact]
    public void 连接刚起来的半秒没跟上_不算()
    {
        // 实测：连接的头十来帧 Tab5 在接管屏幕，确认一百多毫秒才回来，帧帧都算没跟上；之后就正常了
        var budget = new BudgetControl(50 * KB, 20 * KB, 110 * KB);
        double t = Feed(budget, 0, 20, behindEvery: 1);                     // 0.46 秒
        Feed(budget, t, 44 * 2 - 20);                                       // 到两秒为止都跟上
        Assert.Equal(50 * KB, budget.Bytes);
    }

    [Fact]
    public void 偶尔一帧没跟上_不动()
    {
        var budget = new BudgetControl(50 * KB, 20 * KB, 110 * KB);
        Feed(budget, 0, 44 * 30, behindEvery: 10);      // 半分钟，十帧里一帧（10%）：不到降的线，也过了升的线
        Assert.Equal(50 * KB, budget.Bytes);
    }

    [Fact]
    public void 一直跟不上_降到下限为止()
    {
        var budget = new BudgetControl(28 * KB, 20 * KB, 60 * KB);
        Feed(budget, 0, 600, gapMs: 17, behindEvery: 1);
        Assert.Equal(20 * KB, budget.Bytes);
    }

    [Fact]
    public void 预算没用满还跟不上_从实际的大小往下降()
    {
        var budget = new BudgetControl(80 * KB, 30 * KB, 160 * KB);
        Feed(budget, 0, 60, gapMs: 30.6, behindEvery: 1, bytes: 50 * KB);   // 每帧才 50 KB 就帧帧跟不上（链路变差了）
        Assert.Equal((int)(50 * KB * 0.7), budget.Bytes);
    }

    [Fact]
    public void 降过之后_半分钟里不升回站不住的那个数_过了半分钟再试()
    {
        var budget = new BudgetControl(50 * KB, 20 * KB, 110 * KB);
        double t = Feed(budget, 0, 75, behindEvery: 3);                     // 在 50 KB 上站不住：降到 42.5 KB
        int low = budget.Bytes;
        Assert.Equal((int)(50 * KB * 0.85), low);
        t = Feed(budget, t, 44 * 25);                                       // 25 秒都跟上：最多升到 50 KB 下面一点
        Assert.InRange(budget.Bytes, low, 50 * KB - 1);
        int held = budget.Bytes;
        t = Feed(budget, t, 44 * 4);                                        // 还在半分钟之内：不动
        Assert.Equal(held, budget.Bytes);
        Feed(budget, t, 44 * 6);                                            // 过了半分钟：往上试
        Assert.True(budget.Bytes >= 50 * KB, $"过了半分钟预算还是 {budget.Bytes / KB} KB");
    }

    [Fact]
    public void 往上试了又站不住_下一次隔得加倍()
    {
        var budget = new BudgetControl(50 * KB, 20 * KB, 110 * KB);
        double t = Feed(budget, 0, 75, behindEvery: 3);                     // 50 KB 站不住
        // 之后的链路：48 KB 以下都跟上，以上三帧里一帧跟不上。一帧一帧喂，记下每次往上试到站不住的时刻
        var failedAt = new List<double>();
        int last = budget.Bytes;
        for (int i = 0; t < 400_000; i++, t += 23)
        {
            bool over = budget.Bytes > 48 * KB;
            budget.OnFrame(true, !over || i % 3 != 0, budget.Bytes * 95 / 100, t);
            if (budget.Bytes < last && over) failedAt.Add(t);
            last = budget.Bytes;
        }
        // 第一次试在半分钟之后，再下一次隔一分钟、两分钟……不是每半分钟卡一下
        Assert.InRange(failedAt.Count, 3, 4);
        Assert.InRange(failedAt[1] - failedAt[0], 55_000, 70_000);
        Assert.InRange(failedAt[2] - failedAt[1], 115_000, 130_000);
    }

    [Fact]
    public void 画面停过_重新数_不把停之前的半段算进来()
    {
        var budget = new BudgetControl(50 * KB, 20 * KB, 110 * KB);
        double t = Feed(budget, 0, 6, behindEvery: 1);                      // 滚了 6 帧都没跟上（不够 8 帧，还不看）
        t += 3000;                                                          // 停了 3 秒
        Feed(budget, t, 44);                                                // 再滚 1 秒，都跟上：没有降
        Assert.True(budget.Bytes >= 50 * KB, $"预算降到了 {budget.Bytes / KB} KB");
    }

    [Fact]
    public void 不算在动的帧_不看()
    {
        var budget = new BudgetControl(50 * KB, 20 * KB, 110 * KB);
        for (int i = 0; i < 200; i++) budget.OnFrame(false, false, 4 * KB, i * 23);    // 只变了一小块的帧：跟没跟上都不算
        Assert.Equal(50 * KB, budget.Bytes);
    }

    // ---- 接上流量控制和节拍，对着照实测数字做的 Tab5 跑 ----

    /// <summary>屏幕一拍（毫秒）；Tab5 解一帧整屏的 4:2:2 要多久（2026-11-15 实测）。</summary>
    private const double PanelMs = 15.27, DecodeMs = 13.7;

    private readonly record struct Sent(double At, int Bytes, int Budget, bool KeptUp);

    /// <summary>
    /// 照 DisplaySession 的节奏跑 seconds 秒，整屏一直在动。发送端：等流量控制放行 → 限帧率的档等下一拍 → 取画面（sourceFps &gt; 0：画面源每秒出这么多幅，
    /// 没有新的就等；0 = 要多快给多快）→ 压 2 毫秒 → 发。每帧的大小：按原质量压出来是 content(时刻) 字节，超过预算就压到预算的 95%。
    /// Tab5（照固件的做法，wdisp.cpp「翻页」）：一帧的数据按 link(时刻)（字节 / 毫秒）在连接上排着队收；边收边解，整屏解够 DecodeMs；
    /// 上一帧上屏之后才有空着的帧缓冲给这一帧解；解完等下一次屏幕刷新上屏、回 ACK（再过 2~5 毫秒到电脑）。
    /// 无线上的抖动照实测的样子加：每帧的速率在 ±15% 里变，八十帧里有一帧要重传、多花 15 毫秒（随机数的种子是固定的，每次跑都一样）。
    /// 和 2026-11-15 真机量的比（PictureTiers 的注释）：高画质档（这里 77~96 KB，真机 80~95）、最佳速度（画面源比 Tab5 快时都停在下限）对得上；
    /// 均衡档这里站在 67 KB 上下，真机 45~53 KB，偏乐观；差在哪儿没查实（真机落后一拍之后往返会锁在 68 毫秒上下、上屏固定 25 毫秒，这里出不来）。
    /// 这个模型也分不出 均衡档平时 2 帧还是 3 帧在途（这里 61 对 66 KB，真机 34 对 45~53 KB）：「均衡档要 3 帧在途」不是它守的，
    /// 是 DisplaySessionTests（「第45档_平时3帧在途」和中途换档的两条）和真机量的数守的。
    /// 所以这些用例验的是闭环的走势（降得下来、升得回去、不来回摆、不空涨、各档的先后），均衡档具体停在多少 KB 以真机量的为准。
    /// </summary>
    private static List<Sent> Play(PictureTier tier, double seconds, Func<double, double> link, Func<double, int> content, double sourceFps = 0)
    {
        TierPlan plan = PictureTiers.Plan(tier);
        var flow = new FlowControl(Math.Max(FlowControl.DefaultSteadyInFlight, plan.InFlight));     // 和 DisplaySession 一样：均衡档平时 3 帧在途
        var pacer = plan.FramesPerRefresh > 0 ? new FramePacer(1000 / PanelMs * plan.FramesPerRefresh) : null;
        var budget = new BudgetControl(plan.StartBytes, plan.MinBytes, plan.MaxBytes);
        var acks = new Queue<(uint Seq, double At)>();
        var sent = new List<Sent>();
        double now = 0, recvDone = 0, decodeDone = 0, shown = double.NegativeInfinity, ackAt = 0;
        int taken = -1;
        var air = new Random(20261115);
        void Deliver(double until)
        {
            while (acks.Count > 0 && acks.Peek().At <= until)
            {
                var (seq, at) = acks.Dequeue();
                flow.OnAck(seq, at);
            }
        }
        while (now < seconds * 1000)
        {
            Deliver(now);
            for (double wait; (wait = flow.WaitBeforeNextMs(now)) > 0; Deliver(now))
            {
                Assert.True(acks.Count > 0, "没有在途的帧却不让发");
                now = Math.Min(now + wait, Math.Max(now, acks.Peek().At));
            }
            bool idled = false;
            if (pacer is not null && sent.Count > 0 && pacer.WaitMs(now) is > 0 and var beat)
            {
                now += beat;
                idled = true;
            }
            if (sourceFps > 0)
            {
                int latest = (int)Math.Floor(now * sourceFps / 1000);
                if (latest == taken)
                {
                    latest = taken + 1;
                    now = latest * 1000 / sourceFps;
                    idled = true;
                }
                taken = latest;
            }
            Deliver(now);
            if (idled) flow.OnSourceIdle(now);
            now += 2;
            Deliver(now);
            int bytes = Math.Min(content(now), budget.Bytes * 95 / 100);
            uint n = (uint)sent.Count;
            flow.OnSent(n, now);
            sent.Add(new Sent(now, bytes, budget.Bytes, flow.LastSentKeptUp));
            pacer?.Given(now);
            budget.OnFrame(true, flow.LastSentKeptUp, bytes, now);

            double recvStart = Math.Max(now, recvDone);
            recvDone = recvStart + bytes / (link(now) * (0.85 + 0.3 * air.NextDouble())) + (air.Next(80) == 0 ? 15 : 0);
            double decodeStart = Math.Max(recvStart, shown);                    // 上一帧上屏了，才有空着的帧缓冲给这一帧解
            decodeDone = Math.Max(decodeStart + DecodeMs, recvDone + 1);        // 边收边解：数据早到齐了也要解够时间；到得晚就是收完再解最后一条
            shown = Math.Ceiling(decodeDone / PanelMs) * PanelMs;
            ackAt = Math.Max(ackAt, shown + 2 + 3 * air.NextDouble());          // ACK 按先后到
            acks.Enqueue((n, ackAt));
        }
        return sent;
    }

    private static double Fps(List<Sent> sent, double fromMs, double toMs) => sent.Count(s => s.At >= fromMs && s.At < toMs) * 1000 / (toMs - fromMs);
    /// <summary>这一段里预算降了几次。</summary>
    private static int Drops(List<Sent> sent, double fromMs, double toMs)
    {
        var part = sent.Where(s => s.At >= fromMs && s.At < toMs).ToList();
        return part.Zip(part.Skip(1)).Count(p => p.Second.Budget < p.First.Budget);
    }
    /// <summary>给断言失败时看的：预算每变一次记一笔（时刻、变成多少 KB、从上一次变到这一次之间跟上的帧占百分之几）。</summary>
    private static string Trace(List<Sent> sent)
    {
        var parts = new List<string>();
        int from = 0;
        for (int i = 1; i <= sent.Count; i++)
        {
            if (i < sent.Count && sent[i].Budget == sent[i - 1].Budget) continue;
            int kept = sent.Skip(from).Take(i - from).Count(s => s.KeptUp);
            parts.Add($"{sent[from].At / 1000:F1}s:{sent[from].Budget / 1024.0:F1}KB({kept * 100 / (i - from)}%)");
            from = i;
        }
        return string.Join(' ', parts);
    }

    // 整屏滚动的网页 / 文档：按质量 90 压出来一帧二三百 KB，远超哪一档的预算
    private static int Heavy(double _) => 250 * KB;
    // 链路：平时每秒 3 MB 上下
    private static double Link3(double _) => 3000;

    [Theory]
    [InlineData(PictureTier.Fps30, 32.7)]
    [InlineData(PictureTier.Fps45, 43.7)]
    public void 限帧率的两档_整屏一直在动_帧率站在限的那个数上_预算稳住不来回摆(PictureTier tier, double fps)
    {
        List<Sent> sent = Play(tier, 120, Link3, Heavy);
        string trace = Trace(sent);
        // 头 10 秒找位置，之后帧率就是限的那个数
        Assert.InRange(Fps(sent, 10_000, 120_000), fps - 0.6, fps + 0.3);
        // 稳下来之后预算两分钟里只降几次（往上试了站不住、碰上一阵重传），不是每隔一两秒摆一下；也不会越降越低
        Assert.True(Drops(sent, 10_000, 120_000) <= 8, trace);
        var settled = sent.Where(s => s.At >= 10_000).Select(s => s.Budget).ToList();
        Assert.True(settled.Min() >= settled.Max() * 0.6, trace);
        // 在动的帧绝大多数是按节拍发出去的
        var steady = sent.Where(s => s.At >= 10_000).ToList();
        Assert.True(steady.Count(s => s.KeptUp) >= steady.Count * 0.9, trace);
        // 预算落在这一档的范围里，而且没有掉到下限：这条链路撑得起比下限大的帧
        TierPlan plan = PictureTiers.Plan(tier);
        Assert.InRange(steady[^1].Budget, plan.MinBytes + 5 * KB, plan.MaxBytes);
    }

    [Fact]
    public void 帧率低的那一档_预算停得比高的那一档大()
    {
        int at30 = Play(PictureTier.Fps30, 60, Link3, Heavy)[^1].Budget, at45 = Play(PictureTier.Fps45, 60, Link3, Heavy)[^1].Budget;
        Assert.True(at30 > at45 * 1.2, $"高画质档停在 {at30 / KB} KB，均衡档停在 {at45 / KB} KB");
    }

    [Fact]
    public void 链路变差_三秒之内降下来_帧率回到限的那个数_变好之后慢慢升回去()
    {
        // 第 40 秒起有别的设备占着信道，速率掉到一半；第 80 秒恢复
        static double Link(double t) => t is >= 40_000 and < 80_000 ? 1500 : 3000;
        List<Sent> sent = Play(PictureTier.Fps45, 200, Link, Heavy);
        string trace = Trace(sent);
        int before = sent.Last(s => s.At < 40_000).Budget, during = sent.Last(s => s.At < 43_000).Budget;
        Assert.True(during < before * 0.8, $"变差 3 秒后预算才从 {before / KB} 降到 {during / KB} KB；{trace}");
        Assert.InRange(Fps(sent, 45_000, 80_000), 43.0, 44.0);
        int bad = sent.Last(s => s.At < 80_000).Budget;
        Assert.True(sent.Last(s => s.At < 82_000).Budget <= bad * 1.07, trace);     // 恢复之后不是立刻弹回去
        int after = sent[^1].Budget;
        Assert.True(after > bad * 1.3 && after >= before * 0.85, $"恢复两分钟后预算 {after / KB} KB（变差前 {before / KB}，变差时 {bad / KB}）；{trace}");
        Assert.InRange(Fps(sent, 90_000, 200_000), 43.0, 44.0);
    }

    [Fact]
    public void 画面简单_每帧到不了预算_预算不空涨_画面变重时不用先卡一下()
    {
        // 头一分钟是简单的画面（按原质量每帧 30 KB），之后变成整屏滚动的网页
        static int Content(double t) => t < 60_000 ? 30 * KB : 250 * KB;
        List<Sent> sent = Play(PictureTier.Fps45, 90, Link3, Content);
        TierPlan plan = PictureTiers.Plan(PictureTier.Fps45);
        Assert.Equal(plan.StartBytes, sent.Last(s => s.At < 60_000).Budget);
        // 变重之后的头两秒：帧率照旧
        Assert.InRange(Fps(sent, 60_000, 62_000), 42.5, 44.5);
    }

    [Fact]
    public void 最佳速度_画面源比Tab5快_预算降到下限_帧率是Tab5能上屏的上限()
    {
        // 画面源要多快给多快（测试画面、每秒一百多帧的游戏）：每一帧都是「可以发的时候画面早就等着了」，没有跟上的
        List<Sent> sent = Play(PictureTier.BestSpeed, 60, Link3, Heavy);
        TierPlan plan = PictureTiers.Plan(PictureTier.BestSpeed);
        Assert.True(sent.Where(s => s.At >= 5000).All(s => s.Budget == plan.MinBytes), Trace(sent));
        Assert.InRange(Fps(sent, 10_000, 60_000), 55, 65.5);        // 最多每次屏幕刷新一帧
    }

    [Fact]
    public void 最佳速度_画面源每秒60幅_帧率跟上画面源_预算不往上走()
    {
        // 60 帧的画面源只比屏幕的刷新慢一成：小帧跟得上，稍大一点就有一部分帧赶不上那一次刷新。
        // 这一条验两件事：帧率跟上了画面源（没有因为预算摆来摆去掉帧）；预算没有往上涨——它在下限和开头的数之间来回
        // （这个模型里 20~29 KB，后半段贴着下限），具体在哪儿不断言
        List<Sent> sent = Play(PictureTier.BestSpeed, 60, Link3, Heavy, sourceFps: 60);
        TierPlan plan = PictureTiers.Plan(PictureTier.BestSpeed);
        Assert.InRange(Fps(sent, 10_000, 60_000), 59.5, 60.5);
        Assert.True(sent.Max(s => s.Budget) <= plan.StartBytes * 1.1, Trace(sent));
    }

    [Fact]
    public void 最佳速度_画面源每秒30幅_跟得上_预算往上走()
    {
        // 看视频：画面源比 Tab5 慢，每帧都是等画面源等来的
        List<Sent> sent = Play(PictureTier.BestSpeed, 120, Link3, Heavy, sourceFps: 30);
        TierPlan plan = PictureTiers.Plan(PictureTier.BestSpeed);
        Assert.InRange(Fps(sent, 10_000, 120_000), 29.5, 30.5);
        Assert.True(sent[^1].Budget > plan.StartBytes * 1.3, Trace(sent));
    }
}
