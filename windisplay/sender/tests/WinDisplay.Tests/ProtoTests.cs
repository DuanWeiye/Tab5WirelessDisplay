using System.Buffers.Binary;
using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>Tab5 发来的报文的解析：合规的解对，不合规的（长度、个数、范围）都报 ProtocolException，解出来的值上层可以直接用。</summary>
public class ProtoTests
{
    private static byte[] TouchPayload(params (byte Id, ushort X, ushort Y)[] pts) => FakeTab5.Touch(pts);

    private static byte[] HelloPayload(uint maxStrip, uint maxFrame)
    {
        var p = new byte[Proto.HelloSize];
        BinaryPrimitives.WriteUInt16LittleEndian(p, Proto.FbW);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), Proto.FbH);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), Proto.ViewW);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), Proto.ViewH);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), maxStrip);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), maxFrame);
        for (int i = 0; i < Proto.IdSize; i++) p[20 + i] = (byte)(0x10 + i);       // Tab5 的编号
        Proto.PackName("Tab5-46C3").CopyTo(p, 20 + Proto.IdSize);
        return p;
    }

    [Fact]
    public void 触摸_五个触点和画面四角都解得出来()
    {
        TouchPoint[] pts = Proto.ParseTouch(5, TouchPayload((0, 0, 0), (1, 1279, 0), (2, 0, 719), (3, 1279, 719), (4, 640, 360)));

        Assert.Equal([new TouchPoint(0, 0, 0), new TouchPoint(1, 1279, 0), new TouchPoint(2, 0, 719), new TouchPoint(3, 1279, 719), new TouchPoint(4, 640, 360)], pts);
        Assert.Empty(Proto.ParseTouch(0, []));      // 全部抬起
    }

    [Fact]
    public void 触摸_超过五个触点就拒绝()
    {
        byte[] six = TouchPayload((0, 10, 10), (1, 20, 20), (2, 30, 30), (3, 40, 40), (4, 50, 50), (5, 60, 60));

        var e = Assert.Throws<ProtocolException>(() => Proto.ParseTouch(6, six));
        Assert.Contains("6 个触点", e.Message);
    }

    [Theory]
    [InlineData(1280, 0)]       // 横屏画面是 1280×720，坐标从 0 数：1280 已经出了右边
    [InlineData(0, 720)]
    [InlineData(65535, 65535)]
    public void 触摸_坐标出了画面就拒绝(int x, int y)
    {
        Assert.Throws<ProtocolException>(() => Proto.ParseTouch(2, TouchPayload((0, 100, 100), (1, (ushort)x, (ushort)y))));
    }

    [Fact]
    public void 触摸_个数和载荷长度对不上就拒绝()
    {
        Assert.Throws<ProtocolException>(() => Proto.ParseTouch(2, TouchPayload((0, 100, 100))));
    }

    [Fact]
    public void HELLO_固件现在报的值解得出来()
    {
        Hello h = Proto.ParseHello(Proto.Version, HelloPayload(256 * 1024, 1024 * 1024));

        Assert.Equal((720, 1280, 1280, 720), (h.FbW, h.FbH, h.ViewW, h.ViewH));
        Assert.Equal((256 * 1024, 1024 * 1024), (h.MaxStripBytes, h.MaxFrameBytes));
        Assert.Equal(0u, h.Caps);
        Assert.Equal(Convert.FromHexString("101112131415161718191A1B1C1D1E1F"), h.Tab5Id);
        Assert.Equal("Tab5-46C3", h.Name);
    }

    [Theory]
    [InlineData("Tab5-46C3", "Tab5-46C3")]
    [InlineData("书房的电脑", "书房的电脑")]
    [InlineData("一二三四五六七八九十一二", "一二三四五六七八九十")]         // 12 个汉字 36 字节：在字符的边界上截到 30 字节
    [InlineData("客厅那台很旧的笔记本💻", "客厅那台很旧的笔记本")]          // 30 字节之后的 emoji 占 4 字节，放不下就整个不要
    [InlineData("a\u0001b\tc", "a?b?c")]                                   // 控制字符
    [InlineData("del\u007f c1\u0085", "del? c1?")]
    [InlineData("零宽\u200b方向\u202e分段\u2029", "零宽?方向?分段?")]     // 看不见又能搅乱显示的：零宽、方向控制、分段
    [InlineData("\ufeff头", "?头")]
    [InlineData("全角\u3000空格 不换", "全角\u3000空格 不换")]             // 别的原样：全角空格、普通空格
    [InlineData("笔记本 💻", "笔记本 💻")]
    [InlineData("a\u00a0b", "a\u00a0b")]
    public void 名字_打包成32字节_对端只当显示用的文字清洗(string name, string shown)
    {
        byte[] packed = Proto.PackName(name);

        Assert.Equal(Proto.NameSize, packed.Length);
        Assert.Equal(shown, Proto.UnpackName(packed));
    }

    [Fact]
    public void 名字_正好32字节时没有结尾的0_不合法的字节换成问号_到第一个0为止()
    {
        string full = new('x', Proto.NameSize);
        Assert.Equal(full, Proto.UnpackName(Proto.PackName(full)));
        Assert.Equal("ab??", Proto.UnpackName([(byte)'a', (byte)'b', 0xFF, 0xFE, 0, (byte)'z', (byte)'z', .. new byte[25]]));
        Assert.Equal("", Proto.UnpackName(new byte[Proto.NameSize]));
    }

    [Theory]
    [InlineData(0u, 1024u * 1024)]
    [InlineData(256u * 1024, 0u)]
    [InlineData(256u * 1024, 0xFFFF_FFFFu)]     // 超过 int 能表示的范围：当成负数用的话编码器会出错
    public void HELLO_上限是0或大得不合理就拒绝(uint maxStrip, uint maxFrame)
    {
        Assert.Throws<ProtocolException>(() => Proto.ParseHello(Proto.Version, HelloPayload(maxStrip, maxFrame)));
    }
}
