using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using WinDisplay.Core;

namespace WinDisplay.Win;

/// <summary>
/// 在显卡上把一幅画面（一张 B G R A 的纹理）缩放成 1280×720，再读回内存：等比、居中、四周补黑（位置按 <see cref="ViewFit"/>）。
/// 每个目标像素取它盖住的那块源画面的面积加权平均（缩小时不丢细线、不出摩尔纹；小字比双线性清楚），
/// 和 <see cref="ReferenceScaler"/> 是同一个算法，单元测试拿两边的结果逐像素比。
/// 放在显卡上做是因为抓到的画面本来就在显存里：读回来的只有 1280×720（3.7 MB），4K 屏也不用每帧搬 33 MB 再在 CPU 上缩。
/// 只在一个线程上用（设备上下文不是线程安全的）。
/// </summary>
internal sealed unsafe class GpuScaler : IDisposable
{
    // 着色器的源码里只能有 ASCII：编译器按系统的 ANSI 代码页读源码，中文注释在中文系统上会被读坏（长度也会算错）。说明写在这里：
    //   Params：srcSize = 源画面的宽、高；origin = 画面在 1280×720 里占的那一块的左上角；scale = 一个目标像素盖住几个源像素（横、竖）。
    //   vs：不用顶点缓冲，三个顶点 (-1, 1)、(3, 1)、(-1, -3) 拼成一个盖住整个视口的大三角形。
    //   ps：这个目标像素盖住源画面的 [a, b) 这一块，把里面每个源像素按盖住的面积加权平均。
    private const string Shader = """
        Texture2D<float4> src : register(t0);

        cbuffer Params : register(b0)
        {
            float2 srcSize;
            float2 origin;
            float2 scale;
            float2 unused;
        };

        float4 vs(uint id : SV_VertexID) : SV_Position
        {
            return float4((id & 1) ? 3.0 : -1.0, (id & 2) ? -3.0 : 1.0, 0.0, 1.0);
        }

        float4 ps(float4 pos : SV_Position) : SV_Target
        {
            float2 a = floor(pos.xy - origin) * scale;
            float2 b = min(a + scale, srcSize);
            int x0 = (int)a.x, y0 = (int)a.y;
            int x1 = (int)ceil(b.x), y1 = (int)ceil(b.y);
            float3 sum = 0;
            float wsum = 0;
            [loop] for (int y = y0; y < y1; y++)
            {
                float wy = min(b.y, y + 1.0) - max(a.y, (float)y);
                [loop] for (int x = x0; x < x1; x++)
                {
                    float w = (min(b.x, x + 1.0) - max(a.x, (float)x)) * wy;
                    sum += src.Load(int3(x, y, 0)).rgb * w;
                    wsum += w;
                }
            }
            return float4(sum / wsum, 1.0);
        }
        """;

    // 着色器在进程里只编一次，各个 GpuScaler 共用字节码：抓屏断了之后每秒重开一次，不能每次都重编（一次几十毫秒）
    private static readonly Lazy<(byte[] Vs, byte[] Ps)> Bytecode = new(() =>
        (Compiler.Compile(Shader, "vs", "scale.hlsl", "vs_4_0").ToArray(), Compiler.Compile(Shader, "ps", "scale.hlsl", "ps_4_0").ToArray()));

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public float SrcW, SrcH, OriginX, OriginY, ScaleX, ScaleY, Unused0, Unused1;
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11Buffer _params;
    private readonly ID3D11Texture2D _target, _staging;        // 画缩放结果的那张；读回内存用的那张
    private readonly ID3D11RenderTargetView _targetView;
    private ID3D11Texture2D? _input;                            // 源画面的副本（抓屏给的纹理不一定能直接当着色器的输入，尺寸也可能比画面大）
    private ID3D11ShaderResourceView? _inputView;
    private int _inputW, _inputH;

    /// <summary>设备和上下文是借来用的，不归这个对象释放。</summary>
    public GpuScaler(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;
        _vs = device.CreateVertexShader(Bytecode.Value.Vs);
        _ps = device.CreatePixelShader(Bytecode.Value.Ps);
        _params = device.CreateBuffer((uint)sizeof(Params), BindFlags.ConstantBuffer);
        _target = device.CreateTexture2D(Format.B8G8R8A8_UNorm, Proto.ViewW, Proto.ViewH, bindFlags: BindFlags.RenderTarget);
        _targetView = device.CreateRenderTargetView(_target);
        _staging = device.CreateTexture2D(Format.B8G8R8A8_UNorm, Proto.ViewW, Proto.ViewH, bindFlags: BindFlags.None,
                                          usage: ResourceUsage.Staging, cpuAccessFlags: CpuAccessFlags.Read);
    }

    /// <summary>
    /// 把 frame 左上角 srcW×srcH 的那一块缩放进 dst（1280×720，每像素 4 字节，没有行间填充），返回它在画面里占的位置。
    /// 显卡那边出了错（设备丢了之类）抛 <see cref="SharpGenException"/>，抛的时候 dst 还没动过。
    /// </summary>
    public ViewFit Scale(ID3D11Texture2D frame, int srcW, int srcH, Span<byte> dst)
    {
        if (dst.Length < Proto.ViewW * 4 * Proto.ViewH) throw new ArgumentException("缓冲不够放 1280×720 的画面", nameof(dst));
        ViewFit fit = ViewFit.For(srcW, srcH);
        if (_input is null || srcW != _inputW || srcH != _inputH)
        {
            _inputView?.Dispose();
            _input?.Dispose();
            _inputView = null;
            _input = null;
            _input = _device.CreateTexture2D(Format.B8G8R8A8_UNorm, (uint)srcW, (uint)srcH, bindFlags: BindFlags.ShaderResource);
            _inputView = _device.CreateShaderResourceView(_input);
            (_inputW, _inputH) = (srcW, srcH);
        }
        _context.CopySubresourceRegion(_input, 0, 0, 0, 0, frame, 0, new Box(0, 0, 0, srcW, srcH, 1));

        var p = new Params
        {
            SrcW = srcW, SrcH = srcH, OriginX = fit.X, OriginY = fit.Y, ScaleX = (float)srcW / fit.W, ScaleY = (float)srcH / fit.H,
        };
        _context.UpdateSubresource(in p, _params);
        _context.OMSetRenderTargets(_targetView);
        _context.ClearRenderTargetView(_targetView, new Color4(0f, 0f, 0f, 1f));     // 画面没盖到的地方是黑的
        _context.RSSetViewport(fit.X, fit.Y, fit.W, fit.H);
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vs);
        _context.PSSetShader(_ps);
        _context.PSSetConstantBuffer(0, _params);
        _context.PSSetShaderResource(0, _inputView!);
        _context.Draw(3, 0);
        _context.PSSetShaderResource(0, null!);         // 下一幅要往 _input 里拷，先把它从着色器的输入上摘下来
        _context.CopyResource(_staging, _target);

        _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapped).CheckError();    // 等显卡把上面这些做完
        try
        {
            const int rowBytes = Proto.ViewW * 4;
            byte* src = (byte*)mapped.DataPointer;
            fixed (byte* d = dst)
            {
                if (mapped.RowPitch == rowBytes)
                    Buffer.MemoryCopy(src, d, dst.Length, (long)rowBytes * Proto.ViewH);
                else
                    for (int y = 0; y < Proto.ViewH; y++)
                        Buffer.MemoryCopy(src + (long)y * mapped.RowPitch, d + (long)y * rowBytes, rowBytes, rowBytes);
            }
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
        return fit;
    }

    public void Dispose()
    {
        _inputView?.Dispose();
        _input?.Dispose();
        _targetView.Dispose();
        _target.Dispose();
        _staging.Dispose();
        _params.Dispose();
        _ps.Dispose();
        _vs.Dispose();
    }
}
