using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace TokenBar.App;

/// <summary>
/// D3D11 renderer for the 3D contribution graph, ported verbatim from the
/// Phase 2 headless spike (spike/D3DGraphSpike/Program.cs): one cube vertex
/// buffer, 32-byte per-instance data, orthographic orbit camera, row_major
/// constant buffer. The one product change is the output surface — a DXGI
/// swapchain created FOR COMPOSITION (so a SwapChainPanel can present it)
/// instead of the spike's offscreen texture + BMP readback.
///
/// Render-on-demand: nothing draws unless <see cref="Render"/> is called.
/// Owns the device + swapchain; <see cref="Dispose"/> tears both down
/// deterministically. Single-threaded: every call is on the UI thread.
/// </summary>
internal sealed class Graph3DRenderer : IDisposable
{
    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGISwapChain1 _swapChain = null!;
    private ID3D11RenderTargetView _rtv = null!;
    private ID3D11Texture2D _depth = null!;
    private ID3D11DepthStencilView _depthView = null!;
    private ID3D11VertexShader _vs = null!;
    private ID3D11PixelShader _ps = null!;
    private ID3D11InputLayout _layout = null!;
    private ID3D11Buffer _vertexBuffer = null!;
    private ID3D11Buffer _instanceBuffer = null!;
    private ID3D11Buffer _sceneBuffer = null!;

    private readonly int _cubeVertexCount;
    private readonly int _instanceCount;
    private int _width;
    private int _height;

    // Shaders: row_major so System.Numerics row-vector matrices pass through
    // untransposed (verbatim from the spike).
    private const string Hlsl = """
        cbuffer Scene : register(b0)
        {
            row_major float4x4 viewProj;
            float3 lightDir;
            float pad;
        };

        struct VsIn
        {
            float3 pos : POSITION;
            float3 normal : NORMAL;
            float3 ioffset : IOFFSET;
            float iheight : IHEIGHT;
            float3 icolor : ICOLOR;
        };

        struct VsOut
        {
            float4 pos : SV_Position;
            float3 color : COLOR0;
        };

        VsOut VSMain(VsIn v)
        {
            float3 world = float3(
                v.pos.x + v.ioffset.x,
                v.pos.y * v.iheight,
                v.pos.z + v.ioffset.z);
            VsOut o;
            o.pos = mul(float4(world, 1.0), viewProj);
            float shade = 0.55 + 0.45 * saturate(dot(normalize(v.normal), -lightDir));
            o.color = v.icolor * shade;
            return o;
        }

        float4 PSMain(VsOut i) : SV_Target
        {
            return float4(i.color, 1.0);
        }
        """;

    public Graph3DRenderer(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);

        CreateDevice();
        CreateSwapChain(_width, _height);
        CreatePipeline();

        var cube = BuildCube(0.4f);
        _cubeVertexCount = cube.Length;
        _vertexBuffer = _device.CreateBuffer(cube, new BufferDescription(
            (uint)(cube.Length * Marshal.SizeOf<Vertex>()), BindFlags.VertexBuffer));

        var instances = BuildInstances();
        _instanceCount = instances.Length;
        _instanceBuffer = _device.CreateBuffer(instances, new BufferDescription(
            (uint)(instances.Length * Marshal.SizeOf<Instance>()), BindFlags.VertexBuffer));

        _sceneBuffer = _device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<Scene>(), BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic, CpuAccessFlags.Write));

        ConfigurePipeline();
        CreateBackbufferViews(_width, _height);
    }

    /// <summary>Native pointer of the composition swapchain, handed to
    /// ISwapChainPanelNative.SetSwapChain so the panel presents it.</summary>
    public nint SwapChainPointer => _swapChain.NativePointer;

    /// <summary>Render exactly one frame and present it. Returns false if the
    /// device was removed/reset (the caller then re-creates on next show).</summary>
    public bool Render(float azimuthDeg, float elevationDeg)
    {
        if (_device.DeviceRemovedReason.Failure)
        {
            return false;
        }

        _context.RSSetViewport(new Viewport(0, 0, _width, _height));
        _context.OMSetRenderTargets(_rtv, _depthView);
        _context.ClearRenderTargetView(_rtv, new Color4(0.086f, 0.098f, 0.149f, 1f)); // #161926-ish
        _context.ClearDepthStencilView(_depthView, DepthStencilClearFlags.Depth, 1f, 0);

        // Orthographic orbit rig, matching the macOS OrbitRig parameterization
        // (verbatim from the spike's RenderFrame).
        var az = azimuthDeg * MathF.PI / 180f;
        var el = elevationDeg * MathF.PI / 180f;
        var target = new Vector3(0, 0.6f, 0);
        const float radius = 80f;
        var eye = target + radius * new Vector3(
            MathF.Cos(el) * MathF.Sin(az), MathF.Sin(el), MathF.Cos(el) * MathF.Cos(az));
        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        const float viewHeight = 14f;
        var proj = Matrix4x4.CreateOrthographic(
            viewHeight * _width / (float)_height, viewHeight, -1000f, 1000f);

        var scene = new Scene
        {
            ViewProj = view * proj,
            LightDir = Vector3.Normalize(new Vector3(-0.5f, -1f, -0.3f)),
        };
        var mapped = _context.Map(_sceneBuffer, MapMode.WriteDiscard);
        Marshal.StructureToPtr(scene, mapped.DataPointer, false);
        _context.Unmap(_sceneBuffer, 0);

        _context.DrawInstanced((uint)_cubeVertexCount, (uint)_instanceCount, 0, 0);

        var present = _swapChain.Present(1, PresentFlags.None);
        return present.Success && _device.DeviceRemovedReason.Success;
    }

    /// <summary>Resize the swapchain buffers to a new panel size. Zero-size
    /// (a collapsed / not-yet-laid-out panel) is guarded — ResizeBuffers with
    /// a 0 dimension fails.</summary>
    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == _width && height == _height)
        {
            return;
        }

        // Views must be released before ResizeBuffers can touch the buffers.
        _rtv.Dispose();
        _depthView.Dispose();
        _depth.Dispose();
        _swapChain.ResizeBuffers(
            2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
        _width = width;
        _height = height;
        CreateBackbufferViews(width, height);
    }

    private void CreateDevice()
    {
        // Hardware first, WARP fallback — same as the spike. BgraSupport is
        // required for a composition swapchain.
        try
        {
            D3D11.D3D11CreateDevice(
                null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out _device!, out _context!).CheckError();
        }
        catch
        {
            D3D11.D3D11CreateDevice(
                null, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out _device!, out _context!).CheckError();
        }
    }

    private void CreateSwapChain(int width, int height)
    {
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        // Flip-model swapchain for composition: no window, no output; the
        // SwapChainPanel's DirectComposition visual owns presentation.
        var desc = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Premultiplied,
            Flags = SwapChainFlags.None,
        };
        _swapChain = factory.CreateSwapChainForComposition(_device, desc);
    }

    private void CreatePipeline()
    {
        var vsBlob = Compiler.Compile(Hlsl, "VSMain", "graph3d.hlsl", "vs_5_0");
        var psBlob = Compiler.Compile(Hlsl, "PSMain", "graph3d.hlsl", "ps_5_0");
        _vs = _device.CreateVertexShader(vsBlob.Span);
        _ps = _device.CreatePixelShader(psBlob.Span);
        _layout = _device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0, InputClassification.PerVertexData, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0, InputClassification.PerVertexData, 0),
            new InputElementDescription("IOFFSET", 0, Format.R32G32B32_Float, 0, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("IHEIGHT", 0, Format.R32_Float, 12, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("ICOLOR", 0, Format.R32G32B32_Float, 16, 1, InputClassification.PerInstanceData, 1),
        ], vsBlob.Span);
    }

    private void ConfigurePipeline()
    {
        _context.IASetInputLayout(_layout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetVertexBuffer(0, _vertexBuffer, (uint)Marshal.SizeOf<Vertex>());
        _context.IASetVertexBuffer(1, _instanceBuffer, (uint)Marshal.SizeOf<Instance>());
        _context.VSSetShader(_vs);
        _context.VSSetConstantBuffer(0, _sceneBuffer);
        _context.PSSetShader(_ps);
    }

    private void CreateBackbufferViews(int width, int height)
    {
        using var backbuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device.CreateRenderTargetView(backbuffer);
        _depth = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
        });
        _depthView = _device.CreateDepthStencilView(_depth);
    }

    public void Dispose()
    {
        // Reverse of construction. Every field is non-null once the ctor
        // returns; a ctor that threw disposes nothing (caller drops the
        // half-built renderer and re-creates).
        _sceneBuffer?.Dispose();
        _instanceBuffer?.Dispose();
        _vertexBuffer?.Dispose();
        _layout?.Dispose();
        _ps?.Dispose();
        _vs?.Dispose();
        _depthView?.Dispose();
        _depth?.Dispose();
        _rtv?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }

    // ── grid geometry + instances (verbatim from the spike) ───────────────

    private const int Cols = 53;
    private const int Rows = 7;

    private static Vertex[] BuildCube(float half)
    {
        (Vector3 N, Vector3[] Corners)[] faces =
        [
            (new(0, 0, -1), [new(-half, 0, -half), new(-half, 1, -half), new(half, 1, -half), new(half, 0, -half)]),
            (new(0, 0, 1), [new(half, 0, half), new(half, 1, half), new(-half, 1, half), new(-half, 0, half)]),
            (new(-1, 0, 0), [new(-half, 0, half), new(-half, 1, half), new(-half, 1, -half), new(-half, 0, -half)]),
            (new(1, 0, 0), [new(half, 0, -half), new(half, 1, -half), new(half, 1, half), new(half, 0, half)]),
            (new(0, 1, 0), [new(-half, 1, -half), new(-half, 1, half), new(half, 1, half), new(half, 1, -half)]),
            (new(0, -1, 0), [new(-half, 0, half), new(-half, 0, -half), new(half, 0, -half), new(half, 0, half)]),
        ];
        var list = new List<Vertex>(36);
        foreach (var (n, c) in faces)
        {
            list.Add(new Vertex(c[0], n));
            list.Add(new Vertex(c[1], n));
            list.Add(new Vertex(c[2], n));
            list.Add(new Vertex(c[0], n));
            list.Add(new Vertex(c[2], n));
            list.Add(new Vertex(c[3], n));
        }

        return [.. list];
    }

    private static Instance[] BuildInstances()
    {
        var list = new List<Instance>();
        uint seed = 20260702;
        uint Next() => seed = seed * 1664525u + 1013904223u;
        for (var col = 0; col < Cols; col++)
        {
            for (var row = 0; row < Rows; row++)
            {
                var roll = Next() % 100;
                if (roll < 30)
                {
                    continue; // inactive day
                }

                var frac = (Next() % 1000) / 1000f;
                var height = 0.15f + 3.0f * MathF.Pow(frac, 0.6f); // macOS height curve
                // activeLight #BFDBFE → activeDark #1E3A8A ramp by intensity.
                var t = frac;
                var color = new Vector3(
                    Lerp(0.749f, 0.118f, t), Lerp(0.859f, 0.227f, t), Lerp(0.996f, 0.541f, t));
                list.Add(new Instance(
                    new Vector3(col - Cols / 2f + 0.5f, 0, row - Rows / 2f + 0.5f), height, color));
            }
        }

        return [.. list];

        static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Vertex(Vector3 position, Vector3 normal)
    {
        public readonly Vector3 Position = position;
        public readonly Vector3 Normal = normal;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Instance(Vector3 offset, float height, Vector3 color)
    {
        public readonly Vector3 Offset = offset;
        public readonly float Height = height;
        public readonly Vector3 Color = color;
        public readonly float Pad = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Scene
    {
        public Matrix4x4 ViewProj;
        public Vector3 LightDir;
        public float Pad;
    }
}
