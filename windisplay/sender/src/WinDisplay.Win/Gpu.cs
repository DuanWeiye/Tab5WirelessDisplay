using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinDisplay.Core;
using WinRT;

namespace WinDisplay.Win;

/// <summary>Direct3D 11 设备，以及它和抓屏接口（WinRT）之间来回转换的那几步。</summary>
internal static unsafe class Gpu
{
    private static readonly Guid IidDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");     // IDirect3DDxgiInterfaceAccess

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    /// <summary>建设备。hardware = false 用软件渲染（WARP）：没有显卡的环境里跑单元测试用。建不了抛 <see cref="CaptureLostException"/>。</summary>
    public static (ID3D11Device Device, ID3D11DeviceContext Context) CreateDevice(bool hardware = true)
    {
        // 缩放的着色器只用到 Direct3D 10 就有的功能，所以老显卡也行
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        Result r = D3D11.D3D11CreateDevice(IntPtr.Zero, hardware ? DriverType.Hardware : DriverType.Warp, DeviceCreationFlags.BgraSupport, levels,
                                           out ID3D11Device? device, out ID3D11DeviceContext? context);
        if (r.Failure || device is null || context is null)
            throw new CaptureLostException($"建不了 Direct3D 设备（{(hardware ? "显卡" : "软件渲染")}，{r}）");
        // 这个设备有两个线程在用：系统的线程在「来了新的一帧」的回调里从帧池取帧，取画面的线程拿它缩放、读回。
        // 设备本身可以多线程用，它的即时上下文不行——让 Direct3D 自己给上下文加锁（每次调用多进出一次临界区，量不出差别）
        using (ID3D11Multithread guard = context.QueryInterface<ID3D11Multithread>()) guard.SetMultithreadProtected(true);
        return (device, context);
    }

    /// <summary>把 Direct3D 设备包成抓屏接口要的那种设备对象。</summary>
    public static IDirect3DDevice ToWinRT(ID3D11Device device)
    {
        using IDXGIDevice dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr p));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(p);
        }
        finally
        {
            Marshal.Release(p);
        }
    }

    /// <summary>抓到的一帧（WinRT 的表面对象）背后的那张纹理。用完要 Dispose。</summary>
    public static ID3D11Texture2D TextureOf(IDirect3DSurface surface)
    {
        IntPtr unknown = MarshalInterface<IDirect3DSurface>.FromManaged(surface);       // 带一个引用
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in IidDxgiInterfaceAccess, out IntPtr access));
            try
            {
                // IDirect3DDxgiInterfaceAccess 只有一个方法 GetInterface(iid, out 对象)，排在 IUnknown 的三个之后
                Guid iid = typeof(ID3D11Texture2D).GUID;
                IntPtr texture;
                var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(void***)access)[3];
                Marshal.ThrowExceptionForHR(getInterface(access, &iid, &texture));
                return new ID3D11Texture2D(texture);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
