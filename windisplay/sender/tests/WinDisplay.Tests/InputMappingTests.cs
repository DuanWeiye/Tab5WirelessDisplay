using WinDisplay.Core;
using Xunit;

namespace WinDisplay.Tests;

/// <summary>Tab5 键盘模块的报文 → 要注入的按键动作（协议「键盘」）。报文的例子取自键盘模块实际会发的那些。</summary>
public class HidKeyboardTests
{
    private const byte LCtrl = 0x01, LShift = 0x02, LAlt = 0x04;
    private const byte A = 0x04, C = 0x06, Z = 0x1D, N1 = 0x1E, N0 = 0x27, Enter = 0x28, Minus = 0x2D, Delete = 0x4C, Left = 0x50, Up = 0x52;

    private static byte[] Report(byte mods, params byte[] keys)
    {
        var r = new byte[8];
        r[0] = mods;
        keys.CopyTo(r, 2);
        return r;
    }

    private static KeyAction Down(byte scan, bool ext = false) => new(scan, ext, true);
    private static KeyAction Up_(byte scan, bool ext = false) => new(scan, ext, false);

    [Fact]
    public void 按下再松开一个字母()
    {
        var kb = new HidKeyboard();
        Assert.Equal([Down(0x1E)], kb.Apply(Report(0, A)));
        Assert.Equal([Up_(0x1E)], kb.Apply(Report(0)));
    }

    [Fact]
    public void 和已经注入的状态没有差别_什么都不做()
    {
        var kb = new HidKeyboard();
        Assert.Empty(kb.Apply(Report(0)));                  // 接管屏幕之前按下、之后才松开的键：报上来是全松开
        kb.Apply(Report(0, A, C));
        Assert.Empty(kb.Apply(Report(0, C, A)));            // 只是 6 个位置里的先后不同
        Assert.Empty(kb.Apply(Report(0, 0, A, 0, C)));      // 中间有空位
    }

    [Fact]
    public void 上档符号_先按Shift再按键_松开时先松键再松Shift()
    {
        // 键盘模块上的 ! 发出来是「左 Shift + 1」
        var kb = new HidKeyboard();
        Assert.Equal([Down(0x2A), Down(0x02)], kb.Apply(Report(LShift, N1)));
        Assert.Equal([Up_(0x02), Up_(0x2A)], kb.Apply(Report(0)));
    }

    [Fact]
    public void 按住a再按感叹号_先按Shift再按1()
    {
        // 协议里的例子：报文是 02 00 04 1E。先按 1 再按 Shift 的话打出来的是 1
        var kb = new HidKeyboard();
        kb.Apply(Report(0, A));
        Assert.Equal([Down(0x2A), Down(0x02)], kb.Apply(Report(LShift, A, N1)));
    }

    [Fact]
    public void 同一份报文里有松有按_先松旧的_再改修饰键_最后按新的()
    {
        var kb = new HidKeyboard();
        kb.Apply(Report(LShift, A));
        // 松开 Shift 和 a，同时按下 Ctrl + c
        Assert.Equal([Up_(0x1E), Up_(0x2A), Down(0x1D), Down(0x2E)], kb.Apply(Report(LCtrl, C)));
    }

    [Fact]
    public void 同一个键码叠着按_中间那份没有它的报文让它松开再按下()
    {
        // 0 和 ) 是同一个键码：按着 0 再按 )，Tab5 先发一份没有这个键码的，再发一份「Shift + 0」
        var kb = new HidKeyboard();
        Assert.Equal([Down(0x0B)], kb.Apply(Report(0, N0)));
        Assert.Equal([Up_(0x0B)], kb.Apply(Report(0)));
        Assert.Equal([Down(0x2A), Down(0x0B)], kb.Apply(Report(LShift, N0)));
    }

    [Fact]
    public void 方向键和Delete带E0前缀()
    {
        var kb = new HidKeyboard();
        Assert.Equal([Down(0x4B, true)], kb.Apply(Report(0, Left)));
        Assert.Equal([Up_(0x4B, true), Down(0x48, true)], kb.Apply(Report(0, Up)));
        Assert.Equal([Up_(0x48, true), Down(0x1D), Down(0x38), Down(0x53, true)], kb.Apply(Report(LCtrl | LAlt, Delete)));
    }

    [Fact]
    public void 六个键一起按_一个一个松()
    {
        var kb = new HidKeyboard();
        byte[] six = [0x04, 0x05, 0x06, 0x07, 0x08, 0x09];
        Assert.Equal(6, kb.Apply(Report(0, six)).Count);
        Assert.Equal([Up_(0x30)], kb.Apply(Report(0, 0x04, 0x06, 0x07, 0x08, 0x09)));       // 松了 b
        Assert.Equal(5, kb.Apply(Report(0)).Count);
    }

    [Fact]
    public void 连接结束_还按着的全部松开_键先松修饰键后松()
    {
        var kb = new HidKeyboard();
        kb.Apply(Report(LCtrl | LShift, A, Enter));
        var released = kb.ReleaseAll();
        Assert.Equal(4, released.Count);
        Assert.All(released, a => Assert.False(a.Down));
        Assert.Equal([0x1C, 0x1E], released.Take(2).Select(a => (int)a.ScanCode).Order());
        Assert.Equal([0x1D, 0x2A], released.Skip(2).Select(a => (int)a.ScanCode));
        Assert.Empty(kb.ReleaseAll());
        Assert.Equal([Down(0x1E)], kb.Apply(Report(0, A)));       // 下一条连接从全松开算起
    }

    [Fact]
    public void 不认识的键码当作没有()
    {
        var kb = new HidKeyboard();
        Assert.Equal([Down(0x1E)], kb.Apply(Report(0, 0x48, A, 0xE8)));            // Pause、多媒体键不支持
        Assert.Equal([Up_(0x1E)], kb.Apply(Report(0, 0x48, 0xE8)));
    }

    [Fact]
    public void 按键太多的出错报文_按着的键保持原样_只改修饰键()
    {
        var kb = new HidKeyboard();
        Assert.Empty(kb.Apply(Report(0, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01)));     // 原来就全松开：还是全松开
        kb.Apply(Report(LCtrl, A, C));
        // 按着 Ctrl + a + c 时键盘说「按的键太多」，同时 Ctrl 换成了 Shift：a、c 不松开，修饰键照改
        Assert.Equal([Up_(0x1D), Down(0x2A)], kb.Apply(Report(LShift, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01)));
        Assert.Equal(Down(0x2E), kb.Repeat);                                       // 连发的还是最后按下的 c
        Assert.Empty(kb.Apply(Report(LShift, 0x03, A)));                           // 只有一个位置是出错标记也一样，别的位置不看
        // 之后来了正常的报文：只有 a 还按着。c 这时才松开，a 没有被松开再按下（不会多打出一个 a）
        Assert.Equal([Up_(0x2E)], kb.Apply(Report(LShift, A)));
        Assert.Equal([Up_(0x1E), Up_(0x2A)], kb.ReleaseAll());
    }

    [Fact]
    public void 报文长度不对_报错()
    {
        Assert.Throws<ArgumentException>(() => new HidKeyboard().Apply(new byte[7]));
        Assert.Throws<ArgumentException>(() => new HidKeyboard().Apply(new byte[9]));
    }

    [Fact]
    public void 连发的是最后按下的键_它松开就停_修饰键不连发()
    {
        var kb = new HidKeyboard();
        Assert.Null(kb.Repeat);
        kb.Apply(Report(LShift));
        Assert.Null(kb.Repeat);
        kb.Apply(Report(LShift, A));
        Assert.Equal(Down(0x1E), kb.Repeat);
        kb.Apply(Report(LShift, A, Z));
        Assert.Equal(Down(0x2C), kb.Repeat);
        kb.Apply(Report(LShift, A));           // 松了 z，a 还按着：不接着连发 a
        Assert.Null(kb.Repeat);
        kb.Apply(Report(0, A, Minus));
        Assert.Equal(Down(0x0C), kb.Repeat);
        kb.ReleaseAll();
        Assert.Null(kb.Repeat);
    }

    [Theory]
    // 键盘模块会发的键码，各抽几个；右边是美式键盘上那个键的第一套扫描码
    [InlineData(0x04, 0x1E, false)]     // a
    [InlineData(0x1D, 0x2C, false)]     // z
    [InlineData(0x1E, 0x02, false)]     // 1
    [InlineData(0x27, 0x0B, false)]     // 0
    [InlineData(0x28, 0x1C, false)]     // 回车
    [InlineData(0x29, 0x01, false)]     // Esc
    [InlineData(0x2A, 0x0E, false)]     // 退格
    [InlineData(0x2B, 0x0F, false)]     // Tab
    [InlineData(0x2C, 0x39, false)]     // 空格
    [InlineData(0x2D, 0x0C, false)]     // -
    [InlineData(0x2E, 0x0D, false)]     // =
    [InlineData(0x2F, 0x1A, false)]     // [
    [InlineData(0x30, 0x1B, false)]     // ]
    [InlineData(0x31, 0x2B, false)]     // \
    [InlineData(0x33, 0x27, false)]     // ;
    [InlineData(0x34, 0x28, false)]     // '
    [InlineData(0x35, 0x29, false)]     // `
    [InlineData(0x36, 0x33, false)]     // ,
    [InlineData(0x37, 0x34, false)]     // .
    [InlineData(0x38, 0x35, false)]     // /
    [InlineData(0x4C, 0x53, true)]      // Delete
    [InlineData(0x4F, 0x4D, true)]      // 右
    [InlineData(0x50, 0x4B, true)]      // 左
    [InlineData(0x51, 0x50, true)]      // 下
    [InlineData(0x52, 0x48, true)]      // 上
    // 表的两头和别的键盘上才有的键
    [InlineData(0x39, 0x3A, false)]     // Caps Lock
    [InlineData(0x3A, 0x3B, false)]     // F1
    [InlineData(0x45, 0x58, false)]     // F12
    [InlineData(0x58, 0x1C, true)]      // 小键盘回车
    [InlineData(0x65, 0x5D, true)]      // 菜单键
    public void 键码换成扫描码(byte hid, byte scan, bool extended)
    {
        Assert.Equal((scan, extended), HidKeyboard.ScanCodeOf(hid));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x03)]
    [InlineData(0x48)]      // Pause
    [InlineData(0x66)]
    [InlineData(0xE0)]      // 修饰键的键码不会出现在 6 个位置里
    [InlineData(0xFF)]
    public void 表里没有的键码(byte hid)
    {
        Assert.Null(HidKeyboard.ScanCodeOf(hid));
    }

    [Fact]
    public void 八个修饰键各自的扫描码()
    {
        (byte Scan, bool Ext)[] expected =
            [(0x1D, false), (0x2A, false), (0x38, false), (0x5B, true), (0x1D, true), (0x36, false), (0x38, true), (0x5C, true)];
        for (int bit = 0; bit < 8; bit++)
        {
            var kb = new HidKeyboard();
            Assert.Equal([Down(expected[bit].Scan, expected[bit].Ext)], kb.Apply(Report((byte)(1 << bit))));
            Assert.Equal([Up_(expected[bit].Scan, expected[bit].Ext)], kb.Apply(Report(0)));
        }
    }
}

/// <summary>Tab5 的触点快照（横屏画面里的坐标）→ 显示器上的触点。</summary>
public class TouchMapperTests
{
    private static TouchPoint P(byte id, int x, int y) => new(id, (ushort)x, (ushort)y);
    private static Contact C(byte id, int x, int y, ContactPhase phase) => new(id, x, y, phase);

    [Fact]
    public void 一根手指_按下_滑动_抬起_坐标换到1080p的屏上()
    {
        var m = new TouchMapper(ViewFit.For(1920, 1080));
        Assert.Equal([C(0, 960, 540, ContactPhase.Down)], m.Apply([P(0, 640, 360)]));
        Assert.Equal([C(0, 1050, 600, ContactPhase.Move)], m.Apply([P(0, 700, 400)]));
        Assert.Equal([C(0, 1050, 600, ContactPhase.Up)], m.Apply([]));         // 抬起在它最后的位置
        Assert.Empty(m.Apply([]));
    }

    [Fact]
    public void 四个角落在屏幕里面()
    {
        var m = new TouchMapper(ViewFit.For(2560, 1440));
        var down = m.Apply([P(0, 0, 0), P(1, 1279, 719)]);
        Assert.Equal([C(0, 1, 1, ContactPhase.Down), C(1, 2559, 1439, ContactPhase.Down)], down);
    }

    [Fact]
    public void 两根手指_先后按下_先后抬起_每次都给全当前的触点()
    {
        var m = new TouchMapper(ViewFit.For(1280, 720));
        Assert.Equal([C(0, 100, 100, ContactPhase.Down)], m.Apply([P(0, 100, 100)]));
        Assert.Equal([C(0, 100, 100, ContactPhase.Move), C(1, 300, 300, ContactPhase.Down)], m.Apply([P(0, 100, 100), P(1, 300, 300)]));
        Assert.Equal([C(1, 320, 310, ContactPhase.Move), C(0, 100, 100, ContactPhase.Up)], m.Apply([P(1, 320, 310)]));
        Assert.Equal([C(1, 320, 310, ContactPhase.Up)], m.Apply([]));
    }

    [Fact]
    public void 抬起又按下同一个编号_算新的一次按下()
    {
        var m = new TouchMapper(ViewFit.For(1280, 720));
        m.Apply([P(0, 100, 100)]);
        m.Apply([]);
        Assert.Equal([C(0, 500, 500, ContactPhase.Down)], m.Apply([P(0, 500, 500)]));
    }

    [Fact]
    public void 十六比十的屏_画面两边是黑边_坐标按画面那一块换算()
    {
        // 1920×1200 在 1280×720 里占 x = 64..1215
        var m = new TouchMapper(ViewFit.For(1920, 1200));
        Assert.Equal([C(0, 0, 0, ContactPhase.Down)], m.Apply([P(0, 64, 0)]));
        Assert.Equal([C(0, 1919, 1199, ContactPhase.Move)], m.Apply([P(0, 1215, 719)]));
        Assert.Equal([C(0, 960, 600, ContactPhase.Move)], m.Apply([P(0, 640, 360)]));
    }

    [Fact]
    public void 在黑边上按下的不算_滑进画面也不算_抬起再按才算()
    {
        var m = new TouchMapper(ViewFit.For(1920, 1200));
        Assert.Empty(m.Apply([P(0, 30, 300)]));
        Assert.Empty(m.Apply([P(0, 200, 300)]));
        Assert.Empty(m.Apply([]));
        Assert.Equal([C(0, 227, 500, ContactPhase.Down)], m.Apply([P(0, 200, 300)]));
    }

    [Fact]
    public void 在画面里按下滑到黑边上_贴着画面的边()
    {
        var m = new TouchMapper(ViewFit.For(1920, 1200));
        m.Apply([P(0, 100, 300)]);
        Assert.Equal([C(0, 0, 500, ContactPhase.Move)], m.Apply([P(0, 10, 300)]));
        Assert.Equal([C(0, 1919, 500, ContactPhase.Move)], m.Apply([P(0, 1270, 300)]));
    }

    [Fact]
    public void 黑边上的手指不影响画面里的手指()
    {
        var m = new TouchMapper(ViewFit.For(1920, 1200));
        Assert.Equal([C(1, 960, 600, ContactPhase.Down)], m.Apply([P(0, 10, 10), P(1, 640, 360)]));
        Assert.Equal([C(1, 960, 600, ContactPhase.Up)], m.Apply([P(0, 10, 10)]));
    }

    [Fact]
    public void 连接结束_还按着的全部作废_不是抬起_下一条连接里同一个编号照常按下()
    {
        var m = new TouchMapper(ViewFit.For(1280, 720));
        m.Apply([P(0, 100, 100), P(3, 900, 600)]);
        Assert.Equal(2, m.Held.Count);
        var gone = m.ReleaseAll();
        Assert.Equal([C(0, 100, 100, ContactPhase.Cancel), C(3, 900, 600, ContactPhase.Cancel)], gone.OrderBy(c => c.Id));
        Assert.Empty(m.Held);
        Assert.Empty(m.ReleaseAll());
        Assert.Equal([C(0, 200, 200, ContactPhase.Down)], m.Apply([P(0, 200, 200)]));
    }

    [Fact]
    public void Tab5没了消息_按着的全部作废_手还按着的到抬起之前都不算()
    {
        var m = new TouchMapper(ViewFit.For(1280, 720));
        m.Apply([P(0, 100, 100), P(3, 900, 600)]);
        var gone = m.Cancel();
        Assert.Equal([C(0, 100, 100, ContactPhase.Cancel), C(3, 900, 600, ContactPhase.Cancel)], gone.OrderBy(c => c.Id));
        Assert.Empty(m.Held);
        Assert.Empty(m.Cancel());
        Assert.Empty(m.Apply([P(0, 110, 100), P(3, 900, 600)]));        // 报文又来了，两根手指都还按着：不算
        Assert.Empty(m.Apply([P(3, 900, 600)]));                        // 0 号抬起：没有它的抬起帧
        Assert.Equal([C(0, 300, 300, ContactPhase.Down)], m.Apply([P(0, 300, 300), P(3, 900, 600)]));       // 0 号再按下：照常；3 号还不算
    }

    [Fact]
    public void 一份快照里同一个编号出现两次_只认第一个()
    {
        var m = new TouchMapper(ViewFit.For(1280, 720));
        Assert.Equal([C(0, 100, 100, ContactPhase.Down)], m.Apply([P(0, 100, 100), P(0, 700, 700)]));
    }
}
