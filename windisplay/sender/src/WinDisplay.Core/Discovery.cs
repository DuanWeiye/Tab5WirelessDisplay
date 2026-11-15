using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace WinDisplay.Core;

/// <summary>自动发现里应答了的一台 Tab5：连它用的地址（应答的来源地址 + 包里的端口）和它报的编号。</summary>
public sealed record DiscoveredTab5(IPAddress Address, int Port, byte[] Id)
{
    /// <summary>同一个地址、端口、编号算同一个（Id 是数组，按内容比）。</summary>
    public bool Equals(DiscoveredTab5? other) =>
        other is not null && Address.Equals(other.Address) && Port == other.Port && Id.AsSpan().SequenceEqual(other.Id);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Address);
        h.Add(Port);
        h.AddBytes(Id);
        return h.ToHashCode();
    }

    public override string ToString() => $"{Address}:{Port}（编号 {Convert.ToHexStringLower(Id.AsSpan(0, Math.Min(4, Id.Length)))}…）";
}

/// <summary>自动发现的包（协议「自动发现」）：询问和应答是同一种 32 字节的包。</summary>
public static class DiscoveryPacket
{
    public const int Size = 32, NonceSize = 8;
    public const byte Query = 1, Reply = 2;
    private static ReadOnlySpan<byte> Magic => "T5WD"u8;

    /// <summary>询问：端口和编号填 0（Tab5 用不着电脑的编号，电脑也不必把固定的编号广播出去）。</summary>
    public static byte[] PackQuery(ReadOnlySpan<byte> nonce) => Pack(Query, 0, default, nonce);

    /// <summary>应答（Tab5 那一侧；这里有是为了测试里的假 Tab5）。query 不是合格的询问时给 null。</summary>
    public static byte[]? ReplyTo(ReadOnlySpan<byte> query, ReadOnlySpan<byte> id, int tcpPort) =>
        Check(query, Query) ? Pack(Reply, tcpPort, id, query[24..]) : null;

    /// <summary>data 是不是对随机数为 nonce 的那次询问的应答；是的话给出 Tab5 监听的 TCP 端口和它的编号。</summary>
    public static bool TryParseReply(ReadOnlySpan<byte> data, ReadOnlySpan<byte> nonce, out int tcpPort, out byte[] id)
    {
        tcpPort = 0;
        id = [];
        if (!Check(data, Reply) || !data[24..].SequenceEqual(nonce)) return false;
        tcpPort = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        if (tcpPort == 0) return false;
        id = data.Slice(8, Proto.IdSize).ToArray();
        return true;
    }

    // 长度不是 32、标记或版本不对、类型不是要的那种：不认
    private static bool Check(ReadOnlySpan<byte> data, byte kind) =>
        data.Length == Size && data[..4].SequenceEqual(Magic) && data[4] == Proto.Version && data[5] == kind;

    private static byte[] Pack(byte kind, int tcpPort, ReadOnlySpan<byte> id, ReadOnlySpan<byte> nonce)
    {
        if (nonce.Length != NonceSize) throw new ArgumentException($"随机数要 {NonceSize} 字节", nameof(nonce));
        if (id.Length != 0 && id.Length != Proto.IdSize) throw new ArgumentException($"编号要 {Proto.IdSize} 字节", nameof(id));
        var p = new byte[Size];
        Magic.CopyTo(p);
        p[4] = Proto.Version;
        p[5] = kind;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), (ushort)tcpPort);
        id.CopyTo(p.AsSpan(8));
        nonce.CopyTo(p.AsSpan(24));
        return p;
    }
}

/// <summary>问一轮「局域网里哪些 Tab5 在等连接」。</summary>
public interface IDiscovery
{
    /// <summary>发一轮询问、收一会儿应答，返回应答了的那些（按应答到达的先后，不重复）。被取消时返回已经收到的。</summary>
    IReadOnlyList<DiscoveredTab5> Query(CancellationToken ct);
}

/// <summary>询问从哪个本机地址发、发到哪里。Local 是 null = 让系统选。</summary>
public readonly record struct DiscoveryRoute(IPAddress? Local, IPEndPoint Target);

/// <summary>
/// 在局域网里广播询问（UDP）：每块连着的网卡各发一个到它那个网段的广播地址，发出后收一会儿应答，只认随机数是这一次询问的那些。
/// 应答不带认证，也用不着：假的应答把电脑引到别处，握手对不上就断开，画面一个字节都不会发。
/// </summary>
public sealed class LanDiscovery : IDiscovery
{
    /// <summary>发出询问后收多久的应答。Tab5 等连接时实测 14~126 毫秒应答（要等一个信标）。</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromMilliseconds(300);

    private readonly TimeSpan _wait;
    private readonly Func<IReadOnlyList<DiscoveryRoute>> _routes;

    /// <param name="routes">询问发到哪些地方；不给 = 每块连着的网卡的广播地址。测试里指到本机回环上的假 Tab5。</param>
    public LanDiscovery(int port = Proto.Port, TimeSpan? wait = null, Func<IReadOnlyList<DiscoveryRoute>>? routes = null)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), port, "端口要在 1~65535");
        _wait = wait ?? DefaultWait;
        if (_wait <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(wait), wait, "收应答的时间要大于 0");
        _routes = routes ?? (() => BroadcastRoutes(port));
    }

    public IReadOnlyList<DiscoveredTab5> Query(CancellationToken ct)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(DiscoveryPacket.NonceSize);
        byte[] query = DiscoveryPacket.PackQuery(nonce);
        var socks = new List<Socket>();
        var found = new List<DiscoveredTab5>();
        try
        {
            foreach (var route in _routes())
            {
                Socket? s = null;
                try
                {
                    s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    s.EnableBroadcast = true;
                    s.Bind(new IPEndPoint(route.Local ?? IPAddress.Any, 0));
                    s.SendTo(query, route.Target);
                    socks.Add(s);
                }
                catch (SocketException)     // 这块网卡现在发不出去（刚断开、地址没了……），或者系统一时开不出套接字：跳过它，别的照发
                {
                    s?.Dispose();
                }
            }
            var buf = new byte[DiscoveryPacket.Size + 1];       // 多一个字节：长了的包读出来长度不是 32，不认
            var clock = Stopwatch.StartNew();
            while (socks.Count > 0 && !ct.IsCancellationRequested)
            {
                TimeSpan left = _wait - clock.Elapsed;
                if (left <= TimeSpan.Zero) break;
                var ready = new List<Socket>(socks);
                Socket.Select(ready, null, null, left < Poll ? left : Poll);      // 分小段等：好及时看到取消
                foreach (var s in ready)
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n;
                    try
                    {
                        n = s.ReceiveFrom(buf, ref from);
                    }
                    catch (SocketException)     // 包比缓冲长（Windows 上报错）、上一次发送换来了「端口不可达」……：这一个不算
                    {
                        continue;
                    }
                    if (!DiscoveryPacket.TryParseReply(buf.AsSpan(0, n), nonce, out int tcpPort, out byte[] id)) continue;
                    var tab5 = new DiscoveredTab5(((IPEndPoint)from).Address, tcpPort, id);
                    if (!found.Contains(tab5)) found.Add(tab5);
                }
            }
        }
        finally
        {
            foreach (var s in socks) s.Dispose();
        }
        return found;
    }

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// 每块连着的、有 IPv4 地址的网卡（回环除外）：从它的地址发到它那个网段的广播地址。
    /// 只往 255.255.255.255 发一个的话，系统只从默认路由那块网卡发，Tab5 在另一块网卡的网段里就问不到。
    /// 一块都没有（或者列不出来）时退回 255.255.255.255。
    /// </summary>
    public static IReadOnlyList<DiscoveryRoute> BroadcastRoutes(int port)
    {
        var routes = new List<DiscoveryRoute>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                    if (DirectedBroadcast(ua.Address, ua.PrefixLength) is { } bc) routes.Add(new DiscoveryRoute(ua.Address, new IPEndPoint(bc, port)));
                }
            }
        }
        catch (NetworkInformationException)
        {
        }
        if (routes.Count == 0) routes.Add(new DiscoveryRoute(null, new IPEndPoint(IPAddress.Broadcast, port)));
        return routes;
    }

    /// <summary>address 所在网段的广播地址（主机位全 1）。前缀长度是 31、32（点对点、单机）时没有广播地址，给 null。</summary>
    public static IPAddress? DirectedBroadcast(IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || prefixLength is < 1 or > 30) return null;
        uint a = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        uint hostMask = uint.MaxValue >> prefixLength;
        var raw = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(raw, a | hostMask);
        return new IPAddress(raw);
    }
}
