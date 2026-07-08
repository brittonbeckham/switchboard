using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Switchboard.Core.Blur;

/// <summary>
/// GPU blur for one monitor: captured frames come in at full resolution, get
/// downsampled to half size, Gaussian-blurred (separable H/V, two rounds), and
/// drawn upscaled into a composition swapchain that a visual displays.
/// All rendering is serialized on the shared device's context lock.
/// </summary>
public sealed class BlurRenderer : IDisposable
{
    private const string Hlsl = """
        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

        VSOut VSMain(uint id : SV_VertexID)
        {
            VSOut o;
            float2 uv = float2((id << 1) & 2, id & 2);
            o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            o.uv = uv;
            return o;
        }

        Texture2D tex : register(t0);
        Texture2D sharpTex : register(t1);
        SamplerState samp : register(s0);
        cbuffer Params : register(b0) { float2 texel; float2 dir; float blurMix; float3 pad; };

        float4 PSCopy(VSOut i) : SV_Target
        {
            return tex.Sample(samp, i.uv);
        }

        // Final pass: cross-fade between the sharp capture and the blurred copy
        // so blur strength can animate (focus-pull effect).
        float4 PSFinal(VSOut i) : SV_Target
        {
            float4 blurred = tex.Sample(samp, i.uv);
            float4 sharp = sharpTex.Sample(samp, i.uv);
            return lerp(sharp, blurred, saturate(blurMix));
        }

        static const float w[5] = { 0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216 };

        float4 PSBlur(VSOut i) : SV_Target
        {
            float4 c = tex.Sample(samp, i.uv) * w[0];
            [unroll]
            for (int k = 1; k < 5; k++)
            {
                float2 off = dir * texel * k * 1.5;
                c += tex.Sample(samp, i.uv + off) * w[k];
                c += tex.Sample(samp, i.uv - off) * w[k];
            }
            return c;
        }
        """;

    private readonly BlurDevice _device;
    private readonly int _width;
    private readonly int _height;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly ID3D11Texture2D _source;
    private readonly ID3D11ShaderResourceView _sourceSrv;
    private readonly (ID3D11Texture2D Tex, ID3D11RenderTargetView Rtv, ID3D11ShaderResourceView Srv)[] _half;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _psCopy;
    private readonly ID3D11PixelShader _psBlur;
    private readonly ID3D11PixelShader _psFinal;

    /// <summary>0 = sharp, 1 = fully blurred. Animated from the UI thread.</summary>
    public float BlurMix { get; set; } = 1f;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11Buffer _params;
    private bool _disposed;

    public IntPtr SwapChainPointer => _swapChain.NativePointer;

    public BlurRenderer(BlurDevice device, int width, int height)
    {
        _device = device;
        _width = width;
        _height = height;
        var d = device.Device;

        using (var dxgiDevice = d.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgiDevice.GetAdapter())
        using (var factory = adapter.GetParent<IDXGIFactory2>())
        {
            _swapChain = factory.CreateSwapChainForComposition(d, new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            });
        }

        _source = d.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });
        _sourceSrv = d.CreateShaderResourceView(_source);

        _half = new (ID3D11Texture2D, ID3D11RenderTargetView, ID3D11ShaderResourceView)[2];
        for (var i = 0; i < 2; i++)
        {
            var tex = d.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)Math.Max(1, width / 2),
                Height = (uint)Math.Max(1, height / 2),
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            });
            _half[i] = (tex, d.CreateRenderTargetView(tex), d.CreateShaderResourceView(tex));
        }

        Compiler.Compile(Hlsl, null, null, "VSMain", "veil", "vs_5_0", ShaderFlags.None, out var vsBlob, out _).CheckError();
        Compiler.Compile(Hlsl, null, null, "PSCopy", "veil", "ps_5_0", ShaderFlags.None, out var psCopyBlob, out _).CheckError();
        Compiler.Compile(Hlsl, null, null, "PSBlur", "veil", "ps_5_0", ShaderFlags.None, out var psBlurBlob, out _).CheckError();
        Compiler.Compile(Hlsl, null, null, "PSFinal", "veil", "ps_5_0", ShaderFlags.None, out var psFinalBlob, out _).CheckError();
        _vs = d.CreateVertexShader(vsBlob!.AsSpan());
        _psCopy = d.CreatePixelShader(psCopyBlob!.AsSpan());
        _psBlur = d.CreatePixelShader(psBlurBlob!.AsSpan());
        _psFinal = d.CreatePixelShader(psFinalBlob!.AsSpan());
        vsBlob.Dispose();
        psCopyBlob.Dispose();
        psBlurBlob.Dispose();
        psFinalBlob.Dispose();

        _sampler = d.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
        _params = d.CreateBuffer(new BufferDescription(32, BindFlags.ConstantBuffer, ResourceUsage.Default));
    }

    /// <summary>Runs the blur chain for one captured frame and presents it.</summary>
    public void Render(ID3D11Texture2D capturedFrame)
    {
        lock (_device.ContextLock)
        {
            if (_disposed) return;
            var ctx = _device.Context;
            ctx.CopyResource(_source, capturedFrame);

            ctx.VSSetShader(_vs);
            ctx.PSSetSampler(0, _sampler);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.PSSetConstantBuffer(0, _params);

            var hw = Math.Max(1, _width / 2);
            var hh = Math.Max(1, _height / 2);

            // Downsample into half[0].
            DrawPass(_sourceSrv, _half[0].Rtv, hw, hh, _psCopy, 0, 0);

            // Two rounds of separable Gaussian at half resolution.
            for (var round = 0; round < 2; round++)
            {
                DrawPass(_half[0].Srv, _half[1].Rtv, hw, hh, _psBlur, 1f, 0f);
                DrawPass(_half[1].Srv, _half[0].Rtv, hw, hh, _psBlur, 0f, 1f);
            }

            // Upscale to the swapchain backbuffer, cross-fading with the sharp
            // source by BlurMix, and present.
            using (var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
            using (var rtv = _device.Device.CreateRenderTargetView(backBuffer))
            {
                ctx.PSSetShaderResource(1, _sourceSrv);
                DrawPass(_half[0].Srv, rtv, _width, _height, _psFinal, 0, 0);
                ctx.PSSetShaderResource(1, null);
            }
            ctx.PSSetShaderResource(0, null);
            _swapChain.Present(0, PresentFlags.None);
        }
    }

    private void DrawPass(ID3D11ShaderResourceView source, ID3D11RenderTargetView target,
        int width, int height, ID3D11PixelShader shader, float dirX, float dirY)
    {
        var ctx = _device.Context;
        var cb = new[] { 1f / width, 1f / height, dirX, dirY, BlurMix, 0f, 0f, 0f };
        ctx.UpdateSubresource(cb, _params);
        ctx.PSSetShaderResource(0, null); // unbind before rebinding as target elsewhere
        ctx.OMSetRenderTargets(target);
        ctx.RSSetViewport(0, 0, width, height);
        ctx.PSSetShader(shader);
        ctx.PSSetShaderResource(0, source);
        ctx.Draw(3, 0);
        ctx.OMSetRenderTargets(renderTargetView: null);
    }

    public void Dispose()
    {
        lock (_device.ContextLock)
        {
            _disposed = true;
            foreach (var (tex, rtv, srv) in _half)
            {
                rtv.Dispose();
                srv.Dispose();
                tex.Dispose();
            }
            _sourceSrv.Dispose();
            _source.Dispose();
            _vs.Dispose();
            _psCopy.Dispose();
            _psBlur.Dispose();
            _psFinal.Dispose();
            _sampler.Dispose();
            _params.Dispose();
            _swapChain.Dispose();
        }
    }
}

/// <summary>Shared D3D11 device + WinRT projection of it for the capture API.</summary>
public sealed class BlurDevice : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice WinRtDevice { get; }
    public object ContextLock { get; } = new();

    public BlurDevice()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            Array.Empty<Vortice.Direct3D.FeatureLevel>(),
            out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        Device = device;
        Context = context;

        using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable);
        if (hr != 0) throw new System.Runtime.InteropServices.COMException("CreateDirect3D11DeviceFromDXGIDevice", hr);
        try
        {
            WinRtDevice = WinRT.MarshalInterface<Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.Release(inspectable);
        }
    }

    public void Dispose()
    {
        Context.Dispose();
        Device.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
