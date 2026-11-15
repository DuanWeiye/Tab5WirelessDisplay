using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace WinDisplay.Win;

/// <summary>
/// 用 Windows 的数据保护（DPAPI，按当前登录用户）把一小块数据包起来 / 解开：包出来的东西只有同一个用户在同一台电脑上解得开。
/// 给 <see cref="WinDisplay.Core.PairStore"/> 和 <see cref="WinDisplay.Core.HostSettings"/> 的 protect / unprotect 用（里面是配对密钥和密码算出来的数据）。
/// 解不开（文件坏了、换了用户、换了电脑）抛 <see cref="CryptographicException"/>——那两个类把它当成「文件读不懂」报出来。
/// </summary>
public static class Dpapi
{
    private const uint UiForbidden = 0x1;       // CRYPTPROTECT_UI_FORBIDDEN：不许弹窗口
    // 附加的一段：别的程序拿到文件、也用同一个用户调 DPAPI，不知道这一段就解不开（挡不住有心人，只防误用）
    private static readonly byte[] Entropy = "Tab5 WinDisplay v2 store"u8.ToArray();

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob dataIn, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] Protect(byte[] data) => Run(data, protect: true);

    /// <exception cref="CryptographicException">解不开。</exception>
    public static byte[] Unprotect(byte[] data) => Run(data, protect: false);

    private static byte[] Run(byte[] data, bool protect)
    {
        ArgumentNullException.ThrowIfNull(data);
        GCHandle pinData = GCHandle.Alloc(data, GCHandleType.Pinned), pinEntropy = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = pinData.AddrOfPinnedObject() };
            var entropy = new Blob { Size = Entropy.Length, Data = pinEntropy.AddrOfPinnedObject() };
            Blob output;
            bool ok = protect
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok)
            {
                int error = Marshal.GetLastWin32Error();
                throw new CryptographicException($"Windows 数据保护{(protect ? "包不起来" : "解不开")}（错误 0x{error:X8}：{new Win32Exception(error).Message}）");
            }
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            pinData.Free();
            pinEntropy.Free();
        }
    }
}
