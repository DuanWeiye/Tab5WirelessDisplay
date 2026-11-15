using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>本机回环上的一个假 Tab5（只有自动发现那一半）：收到一个 UDP 包，按 Answer 回若干个包。</summary>
internal sealed class FakeResponder : IDisposable
{
    private readonly Socket _sock = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly Thread _thread;
    private readonly Func<byte[], IEnumerable<byte[]>> _answer;

    public FakeResponder(Func<byte[], IEnumerable<byte[]>> answer)
    {
        _answer = answer;
        _sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_sock.LocalEndPoint!).Port;
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.Start();
    }

    /// <summary>守规矩的 Tab5：编号 id，说自己在 tcpPort 上等连接。</summary>
    public static FakeResponder Tab5(byte[] id, int tcpPort) =>
        new(query => DiscoveryPacket.ReplyTo(query, id, tcpPort) is { } reply ? [reply] : []);

    public int Port { get; }
    public List<byte[]> Queries { get; } = [];
    public DiscoveryRoute Route => new(IPAddress.Loopback, new IPEndPoint(IPAddress.Loopback, Port));

    private void Loop()
    {
        var buf = new byte[256];
        try
        {
            for (;;)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n = _sock.ReceiveFrom(buf, ref from);
                byte[] query = buf[..n];
                lock (Queries) Queries.Add(query);
                foreach (byte[] reply in _answer(query)) _sock.SendTo(reply, from);
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _sock.Dispose();
        _thread.Join(2000);
    }
}

/// <summary>自动发现：询问 / 应答的包，和在局域网里问一轮（这里问的是本机回环上的假 Tab5）。</summary>
public class DiscoveryTests
{
    private static readonly byte[] IdA = TestPairing.Tab5Id;
    private static readonly byte[] IdB = [.. Enumerable.Range(0xC0, Proto.IdSize).Select(i => (byte)i)];
    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(300);

    private static LanDiscovery Asking(params FakeResponder[] tab5s) =>
        new(wait: Wait, routes: () => [.. tab5s.Select(t => t.Route)]);

    [Fact]
    public void 询问包_32字节_端口和编号填0()
    {
        byte[] nonce = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] q = DiscoveryPacket.PackQuery(nonce);
        Assert.Equal(Convert.FromHexString("54355744" + "02" + "01" + "0000" + new string('0', 32) + "0102030405060708"), q);
    }

    [Fact]
    public void 应答包_原样带回随机数_带上端口和编号()
    {
        byte[] nonce = [9, 8, 7, 6, 5, 4, 3, 2];
        byte[]? reply = DiscoveryPacket.ReplyTo(DiscoveryPacket.PackQuery(nonce), IdA, 7250);
        Assert.NotNull(reply);
        Assert.Equal(Convert.FromHexString("54355744" + "02" + "02" + "521c" + Convert.ToHexString(IdA) + "0908070605040302"), reply);
        Assert.True(DiscoveryPacket.TryParseReply(reply, nonce, out int port, out byte[] id));
        Assert.Equal(7250, port);
        Assert.Equal(IdA, id);
    }

    [Fact]
    public void 不是询问的包_不应答()
    {
        byte[] q = DiscoveryPacket.PackQuery(new byte[8]);
        Assert.Null(DiscoveryPacket.ReplyTo(q[..31], IdA, 7250));                                    // 短了
        Assert.Null(DiscoveryPacket.ReplyTo([.. q, 0], IdA, 7250));                                  // 长了
        Assert.Null(DiscoveryPacket.ReplyTo(DiscoveryPacket.ReplyTo(q, IdA, 7250)!, IdA, 7250));     // 是别人的应答
        byte[] v1 = (byte[])q.Clone();
        v1[4] = 1;
        Assert.Null(DiscoveryPacket.ReplyTo(v1, IdA, 7250));                                         // 旧版本
        byte[] other = (byte[])q.Clone();
        other[0] = (byte)'X';
        Assert.Null(DiscoveryPacket.ReplyTo(other, IdA, 7250));                                      // 标记不对
    }

    [Fact]
    public void 问一轮_等着连接的Tab5应答_给出它的地址端口编号()
    {
        using var tab5 = FakeResponder.Tab5(IdA, 7250);
        var found = Asking(tab5).Query(TestLimit.Session());
        Assert.Equal([new DiscoveredTab5(IPAddress.Loopback, 7250, IdA)], found);
        byte[] query = Assert.Single(tab5.Queries);
        Assert.Equal(DiscoveryPacket.Size, query.Length);
        Assert.Equal(new byte[Proto.IdSize], query[8..24]);        // 询问里不带电脑自己的编号
    }

    [Fact]
    public void 问一轮_两台Tab5都应答_各算一台()
    {
        using var a = FakeResponder.Tab5(IdA, 7250);
        using var b = FakeResponder.Tab5(IdB, 7251);
        var found = Asking(a, b).Query(TestLimit.Session());
        Assert.Equal(2, found.Count);
        Assert.Contains(new DiscoveredTab5(IPAddress.Loopback, 7250, IdA), found);
        Assert.Contains(new DiscoveredTab5(IPAddress.Loopback, 7251, IdB), found);
    }

    [Fact]
    public void 问一轮_每次询问的随机数都不一样()
    {
        using var tab5 = FakeResponder.Tab5(IdA, 7250);
        var discovery = Asking(tab5);
        discovery.Query(TestLimit.Session());
        discovery.Query(TestLimit.Session());
        Assert.Equal(2, tab5.Queries.Count);
        Assert.NotEqual(tab5.Queries[0][24..], tab5.Queries[1][24..]);
    }

    [Fact]
    public void 问一轮_同一个应答来两遍_只算一台()
    {
        using var tab5 = new FakeResponder(q => [DiscoveryPacket.ReplyTo(q, IdA, 7250)!, DiscoveryPacket.ReplyTo(q, IdA, 7250)!]);
        Assert.Single(Asking(tab5).Query(TestLimit.Session()));
    }

    public static TheoryData<string, Func<byte[], byte[]>> BadReplies => new()
    {
        { "随机数不是这次询问的（旧的应答、别人的应答）", q => DiscoveryPacket.ReplyTo(DiscoveryPacket.PackQuery(new byte[8]), IdA, 7250)! },
        { "把询问原样弹回来（类型还是询问）", q => q },
        { "端口是 0", q => DiscoveryPacket.ReplyTo(q, IdA, 0)! },
        { "少一个字节", q => DiscoveryPacket.ReplyTo(q, IdA, 7250)![..31] },
        { "多一个字节", q => [.. DiscoveryPacket.ReplyTo(q, IdA, 7250)!, 0] },
        { "长得多的包", q => new byte[200] },
        { "版本是 1", q => Patch(DiscoveryPacket.ReplyTo(q, IdA, 7250)!, 4, 1) },
        { "标记不对", q => Patch(DiscoveryPacket.ReplyTo(q, IdA, 7250)!, 3, (byte)'X') },
    };

    private static byte[] Patch(byte[] packet, int at, byte value)
    {
        packet[at] = value;
        return packet;
    }

    [Theory]
    [MemberData(nameof(BadReplies))]
    public void 问一轮_不合格的应答不认_合格的照收(string what, Func<byte[], byte[]> bad)
    {
        _ = what;
        using var liar = new FakeResponder(q => [bad(q)]);
        using var tab5 = FakeResponder.Tab5(IdB, 7251);
        var found = Asking(liar, tab5).Query(TestLimit.Session());
        Assert.Equal([new DiscoveredTab5(IPAddress.Loopback, 7251, IdB)], found);
    }

    [Fact]
    public void 问一轮_没有Tab5应答_等满时间后返回空()
    {
        using var silent = new FakeResponder(_ => []);
        var clock = Stopwatch.StartNew();
        var found = Asking(silent).Query(TestLimit.Session());
        Assert.Empty(found);
        Assert.InRange(clock.ElapsedMilliseconds, 280, 2000);
    }

    [Fact]
    public void 问一轮_中途取消_马上返回()
    {
        using var silent = new FakeResponder(_ => []);
        var discovery = new LanDiscovery(wait: TimeSpan.FromSeconds(30), routes: () => [silent.Route]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var clock = Stopwatch.StartNew();
        Assert.Empty(discovery.Query(cts.Token));
        Assert.InRange(clock.ElapsedMilliseconds, 0, 3000);
    }

    [Fact]
    public void 问一轮_有一块网卡发不出去_别的照问()
    {
        using var tab5 = FakeResponder.Tab5(IdA, 7250);
        // 192.0.2.1（文档专用网段）不是本机的地址：从它发会绑定失败，相当于那块网卡刚断开
        var gone = new DiscoveryRoute(IPAddress.Parse("192.0.2.1"), new IPEndPoint(IPAddress.Parse("192.0.2.255"), 7250));
        var discovery = new LanDiscovery(wait: Wait, routes: () => [gone, tab5.Route]);
        Assert.Single(discovery.Query(TestLimit.Session()));
    }

    [Theory]
    [InlineData("192.168.0.10", 24, "192.168.0.255")]        // 常见的家用网段
    [InlineData("10.20.30.40", 8, "10.255.255.255")]
    [InlineData("172.17.0.1", 16, "172.17.255.255")]
    [InlineData("192.168.1.130", 25, "192.168.1.255")]
    [InlineData("192.168.1.5", 30, "192.168.1.7")]
    public void 网段的广播地址(string address, int prefix, string broadcast)
    {
        Assert.Equal(IPAddress.Parse(broadcast), LanDiscovery.DirectedBroadcast(IPAddress.Parse(address), prefix));
    }

    [Theory]
    [InlineData("10.0.0.1", 31)]        // 点对点
    [InlineData("10.0.0.1", 32)]        // 单机
    [InlineData("10.0.0.1", 0)]
    [InlineData("fe80::1", 64)]         // 不是 IPv4
    public void 没有广播地址的网段(string address, int prefix)
    {
        Assert.Null(LanDiscovery.DirectedBroadcast(IPAddress.Parse(address), prefix));
    }

    [Fact]
    public void 默认往每块网卡的广播地址问_一块都没有时退回全网广播()
    {
        var routes = LanDiscovery.BroadcastRoutes(7250);
        Assert.NotEmpty(routes);
        Assert.All(routes, r =>
        {
            Assert.Equal(7250, r.Target.Port);
            Assert.False(IPAddress.IsLoopback(r.Target.Address));
            if (r.Local is null) Assert.Equal(IPAddress.Broadcast, r.Target.Address);
        });
    }
}
