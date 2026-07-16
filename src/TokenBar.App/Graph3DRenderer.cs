using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using TokenBar.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using DxgiFormat = Vortice.DXGI.Format;

namespace TokenBar.App;

/// <summary>
/// D3D11 renderer for the 3D contribution graph — Phase 8 Pass 1 (visuals).
/// Ports the macOS SceneKit reference (ContributionGraph3D.swift): a box per
/// in-year cell (active bars + inactive floor tiles), per-face top/side albedo,
/// an ambient + two-directional lighting model with a restrained specular, and
/// an orthographic orbit camera whose default pose + auto-fit frame the active
/// cells. MSAA 4× is rendered into an offscreen target and resolved into the
/// composition backbuffer (flip-model swapchains cannot be multisampled).
///
/// Render-on-demand: nothing draws unless <see cref="Render"/> is called.
/// Owns the device + swapchain; <see cref="Dispose"/> tears both down
/// deterministically. Single-threaded: every call is on the UI thread. Instance
/// data is installed via <see cref="SetData"/>; a renderer with no data renders
/// a clear frame (the soak's create/release lifecycle is unchanged).
/// </summary>
internal sealed class Graph3DRenderer : IDisposable
{
    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGISwapChain1 _swapChain = null!;
    // Backbuffer is single-sample (flip-model). Drawing lands in the MSAA
    // colour/depth pair, then ResolveSubresource copies into the backbuffer.
    private ID3D11Texture2D _backbuffer = null!;
    private ID3D11Texture2D _msaaColor = null!;
    private ID3D11RenderTargetView _msaaRtv = null!;
    private ID3D11Texture2D _msaaDepth = null!;
    private ID3D11DepthStencilView _msaaDepthView = null!;
    private ID3D11VertexShader _vs = null!;
    private ID3D11PixelShader _ps = null!;
    private ID3D11InputLayout _layout = null!;
    private ID3D11Buffer _vertexBuffer = null!;
    private ID3D11Buffer? _instanceBuffer;
    private ID3D11Buffer _sceneBuffer = null!;
    private Instance[] _instances = [];
    private RenderedHit[] _renderedHits = [];
    private int _hoveredInstance = -1;

    private readonly int _cubeVertexCount;
    private int _instanceCount;
    private int _width;
    private int _height;
    private int _sampleCount = 4;

    // Camera pose (set by Fit()). Orthographic orbit rig, macOS OrbitRig parity.
    private float _azimuth = MathF.PI / 4f;
    private float _elevation = MathF.Atan2(0.45f, MathF.Sqrt(2f) * 0.7f);
    private float _scale = 26f; // ortho half view-height, world units
    private Vector3 _target = Vector3.Zero;
    private Vector3[] _fitCorners = [];
    private bool _cameraReady;
    private const float CameraDistance = 150f; // clears clipping only
    private const float MinScale = 0.6f;
    private const float MaxScale = 160f;

    // Shaders: row_major so System.Numerics row-vector matrices pass through
    // untransposed. Lighting is ported from the SceneKit reference:
    // ambient 0.7 + key 0.8·N·L1 + fill 0.25·N·L2, with a 4%-F0 Blinn-Phong
    // specular broadened by roughness (0.5 top / 0.6 side). Albedo constants are
    // authored in sRGB, so lighting happens in linear space and is encoded back
    // to sRGB at the end. Multiplying the sRGB values directly made shaded faces
    // unnaturally dim and read like translucent acrylic even with alpha=1.
    private const string Hlsl = """
        cbuffer Scene : register(b0)
        {
            row_major float4x4 viewProj;
            float3 eyeDir; // unit vector from target toward the eye (toward viewer)
            float  pad;
        };

        static const float3 KEY_L  = normalize(float3( 20.0, 30.0,  15.0)); // toward key
        static const float3 FILL_L = normalize(float3(-15.0, 20.0, -10.0)); // toward fill
        static const float  AMBIENT = 0.7;
        static const float  KEY_I   = 0.8;
        static const float  FILL_I  = 0.25;
        static const float  F0      = 0.04;

        float3 SRGBToLinear(float3 c)
        {
            float3 lo = c / 12.92;
            float3 hi = pow((c + 0.055) / 1.055, 2.4);
            return lerp(lo, hi, step(0.04045, c));
        }

        float3 LinearToSRGB(float3 c)
        {
            c = saturate(c);
            float3 lo = c * 12.92;
            float3 hi = 1.055 * pow(c, 1.0 / 2.4) - 0.055;
            return lerp(lo, hi, step(0.0031308, c));
        }

        struct VsIn
        {
            float3 pos : POSITION;
            float3 normal : NORMAL;
            float3 ioffset : IOFFSET;
            float  iheight : IHEIGHT;
            float3 itop : ITOP;
            float3 iside : ISIDE;
            float  ihighlight : IHIGHLIGHT;
        };

        struct VsOut
        {
            float4 pos : SV_Position;
            float3 normal : NORMAL;
            float3 albedo : COLOR0;
            float  roughness : COLOR1;
            float  highlight : COLOR2;
        };

        VsOut VSMain(VsIn v)
        {
            // Boxes span y in [0, iheight]; x/z placed at the grid cell centre.
            float3 world = float3(
                v.pos.x + v.ioffset.x,
                v.pos.y * v.iheight,
                v.pos.z + v.ioffset.z);
            VsOut o;
            o.pos = mul(float4(world, 1.0), viewProj);
            o.normal = v.normal; // axis-aligned boxes: object == world normal
            bool topFace = abs(v.normal.y) > 0.5;
            o.albedo = topFace ? v.itop : v.iside;
            o.roughness = topFace ? 0.5 : 0.6;
            o.highlight = v.ihighlight;
            return o;
        }

        float4 PSMain(VsOut i) : SV_Target
        {
            float3 N = normalize(i.normal);
            float ndl1 = saturate(dot(N, KEY_L));
            float ndl2 = saturate(dot(N, FILL_L));
            float diffuse = AMBIENT + KEY_I * ndl1 + FILL_I * ndl2;

            // Restrained Blinn-Phong specular, broadened by roughness, 4% F0.
            float3 H = normalize(KEY_L + eyeDir);
            float specPower = 2.0 / max(i.roughness * i.roughness, 1e-3);
            float spec = pow(saturate(dot(N, H)), specPower) * F0 * KEY_I;

            // Covered samples are deliberately opaque. The swapchain remains
            // premultiplied only so the clear pixels reveal the card behind it.
            float3 albedo = SRGBToLinear(saturate(i.albedo));
            float3 emission = SRGBToLinear(float3(0.15, 0.3, 0.9));
            float3 color = albedo * saturate(diffuse) + spec
                + i.highlight * emission * 0.35;
            return float4(LinearToSRGB(color), 1.0);
        }
        """;

    public Graph3DRenderer(int width, int height)
    {
        try
        {
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            _cameraReady = RestoreCamera();

            CreateDevice();
            // Flip-model composition backbuffers cannot be multisampled; MSAA lives
            // in an offscreen target that resolves into the backbuffer each frame.
            var colorQuality = _device.CheckMultisampleQualityLevels(
                DxgiFormat.B8G8R8A8_UNorm, 4);
            var depthQuality = _device.CheckMultisampleQualityLevels(DxgiFormat.D32_Float, 4);
            _sampleCount = colorQuality > 0 && depthQuality > 0 ? 4 : 1;
            CreateSwapChain(_width, _height);
            CreatePipeline();

            var cube = BuildCube(0.5f); // CELL / 2
            _cubeVertexCount = cube.Length;
            _vertexBuffer = _device.CreateBuffer(cube, new BufferDescription(
                (uint)(cube.Length * Marshal.SizeOf<Vertex>()), BindFlags.VertexBuffer));

            _sceneBuffer = _device.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<Scene>(), BindFlags.ConstantBuffer,
                ResourceUsage.Dynamic, CpuAccessFlags.Write));

            ConfigurePipeline();
            CreateRenderTargets(_width, _height);
        }
        catch
        {
            // The instance never reaches Graph3DPanel when construction fails,
            // so unwind every native resource that was initialized before the
            // failure here. Preserve the construction exception if cleanup also
            // encounters a driver error.
            try
            {
                Dispose();
            }
            catch
            {
                // Best-effort cleanup during exception unwinding.
            }

            throw;
        }
    }

    /// <summary>Native pointer of the composition swapchain, handed to
    /// ISwapChainPanelNative.SetSwapChain so the panel presents it.</summary>
    public nint SwapChainPointer => _swapChain.NativePointer;

    /// <summary>Cancel the scale that XAML applies to SwapChainPanel content.
    /// Buffers and pointer coordinates are already expressed in physical
    /// pixels, so without this inverse transform the composition visual is
    /// scaled a second time and picking drifts away from the cursor.</summary>
    public void SetCompositionScale(float scaleX, float scaleY)
    {
        scaleX = scaleX > 0 ? scaleX : 1f;
        scaleY = scaleY > 0 ? scaleY : 1f;
        using var swapChain2 = _swapChain.QueryInterface<IDXGISwapChain2>();
        swapChain2.MatrixTransform = Matrix3x2.CreateScale(1f / scaleX, 1f / scaleY);
    }

    /// <summary>Install real grid instance data (active bars plus inactive
    /// floor tiles). Rebuilds the instance buffer from scratch; camera state is
    /// preserved after the first fit or a restored persisted pose.</summary>
    public void SetData(GridLayout grid, bool dark)
    {
        var (instances, corners, renderedHits) = BuildInstances(grid, dark);
        _instanceBuffer?.Dispose();
        _instanceCount = instances.Length;
        _instances = instances;
        _renderedHits = renderedHits;
        _hoveredInstance = -1;
        _instanceBuffer = _instanceCount == 0
            ? null
            : _device.CreateBuffer(instances, new BufferDescription(
                (uint)(instances.Length * Marshal.SizeOf<Instance>()), BindFlags.VertexBuffer,
                ResourceUsage.Dynamic, CpuAccessFlags.Write));
        if (_instanceBuffer is not null)
        {
            _context.IASetVertexBuffer(1, _instanceBuffer, (uint)Marshal.SizeOf<Instance>());
        }

        _fitCorners = corners;
        if (!_cameraReady)
        {
            FitToContent();
        }
    }

    /// <summary>Render exactly one frame and present it. Returns false if the
    /// device was removed/reset (the caller then re-creates on next show).</summary>
    public bool Render()
    {
        if (_device.DeviceRemovedReason.Failure)
        {
            return false;
        }

        var (view, proj, eyeDir) = BuildCamera();

        _context.RSSetViewport(new Viewport(0, 0, _width, _height));
        _context.OMSetRenderTargets(_msaaRtv, _msaaDepthView);
        // Transparent clear: the panel sits on Acrylic (macOS view.clear parity).
        _context.ClearRenderTargetView(_msaaRtv, new Color4(0f, 0f, 0f, 0f));
        _context.ClearDepthStencilView(_msaaDepthView, DepthStencilClearFlags.Depth, 1f, 0);

        var scene = new Scene { ViewProj = view * proj, EyeDir = eyeDir };
        var mapped = _context.Map(_sceneBuffer, MapMode.WriteDiscard);
        Marshal.StructureToPtr(scene, mapped.DataPointer, false);
        _context.Unmap(_sceneBuffer, 0);

        if (_instanceCount > 0)
        {
            _context.DrawInstanced((uint)_cubeVertexCount, (uint)_instanceCount, 0, 0);
        }

        // Resolve MSAA into the single-sample backbuffer before present.
        if (_sampleCount > 1)
        {
            _context.ResolveSubresource(
                _backbuffer, 0, _msaaColor, 0, DxgiFormat.B8G8R8A8_UNorm);
        }
        else
        {
            _context.CopyResource(_backbuffer, _msaaColor);
        }

        var present = _swapChain.Present(1, PresentFlags.None);
        return present.Success && _device.DeviceRemovedReason.Success;
    }

    /// <summary>Orbit the camera by a physical-pixel drag delta.</summary>
    public void Orbit(float dx, float dy)
    {
        _azimuth -= dx * 0.01f * 0.7f;
        // WinUI pointer Y grows downward; AppKit's deltaY used by the reference
        // grows upward, so invert Y while preserving the OrbitControls speed.
        _elevation = Math.Clamp(_elevation - dy * 0.01f * 0.7f,
            -89f * MathF.PI / 180f, 89f * MathF.PI / 180f);
        _cameraReady = true;
    }

    /// <summary>Pan by a physical-pixel drag delta in the camera plane.</summary>
    public void Pan(float dx, float dy)
    {
        var view = BuildView();
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        var worldPerPixel = 2f * _scale / Math.Max(_height, 1);
        _target -= right * dx * worldPerPixel;
        // Same coordinate-system correction as Orbit: dragging the pointer
        // down should move the scene down, which means shifting the target up.
        _target += up * dy * worldPerPixel;
        _cameraReady = true;
    }

    /// <summary>Zoom using the WinUI mouse-wheel delta (normally ±120).</summary>
    public void ZoomFromWheel(int wheelDelta)
    {
        _scale *= MathF.Exp(-wheelDelta * 0.002f);
        _scale = Math.Clamp(_scale, MinScale, MaxScale);
        _cameraReady = true;
        PersistCamera();
    }

    /// <summary>Fit the active-cell bounds (or whole-grid fallback) and save
    /// the resulting pose for the next renderer creation.</summary>
    public void FitToContent()
    {
        Fit();
        _cameraReady = true;
        PersistCamera();
    }

    /// <summary>Clear the saved pose, restore the default orbit, fit, and save
    /// the resulting frame.</summary>
    public void ResetCamera()
    {
        AppSettings.Store.Remove(CameraStorageKey);
        _cameraReady = false;
        FitToContent();
    }

    /// <summary>Update the hovered active cell from a panel-local physical
    /// pixel coordinate. Returns true only when the highlighted instance
    /// changed, allowing the panel to render strictly on demand.</summary>
    public bool UpdateHover(float pixelX, float pixelY, out GridCell? cell)
    {
        var hit = Pick(pixelX, pixelY);
        var next = hit?.InstanceIndex ?? -1;
        cell = hit?.Cell;
        if (next == _hoveredInstance)
        {
            return false;
        }

        _hoveredInstance = next;
        UpdateInstanceHighlight();
        return true;
    }

    public bool ClearHover()
    {
        if (_hoveredInstance < 0)
        {
            return false;
        }

        _hoveredInstance = -1;
        UpdateInstanceHighlight();
        return true;
    }

    private RenderedHit? Pick(float pixelX, float pixelY)
    {
        if (_renderedHits.Length == 0)
        {
            return null;
        }

        // Unproject through the exact matrix sent to HLSL. Reconstructing an
        // orthographic ray from hand-extracted view bases is easy to skew when
        // the row-vector System.Numerics convention meets D3D viewport/DPI
        // coordinates; using the inverse view-projection keeps hover and the
        // rendered cube under the same transform by construction.
        var (view, proj, _) = BuildCamera();
        if (!Matrix4x4.Invert(view * proj, out var inverseViewProj))
        {
            return null;
        }

        var ndcX = 2f * pixelX / Math.Max(_width, 1) - 1f;
        var ndcY = 1f - 2f * pixelY / Math.Max(_height, 1);
        var origin = Unproject(ndcX, ndcY, 0f, inverseViewProj);
        var far = Unproject(ndcX, ndcY, 1f, inverseViewProj);
        var direction = Vector3.Normalize(far - origin);

        RenderedHit? closest = null;
        var closestDistance = float.PositiveInfinity;
        foreach (var hit in _renderedHits)
        {
            if (RayIntersects(origin, direction, hit.Min, hit.Max, out var distance)
                && distance < closestDistance)
            {
                closestDistance = distance;
                closest = hit;
            }
        }

        // Inactive floor tiles participate in the depth test even though they
        // have no tooltip. Otherwise the ray can pass through a visible floor
        // tile and select an active bar hidden behind it.
        return closest is { Cell.Active: true } ? closest : null;
    }

    private static Vector3 Unproject(
        float ndcX, float ndcY, float ndcZ, Matrix4x4 inverseViewProj)
    {
        var world = Vector4.Transform(new Vector4(ndcX, ndcY, ndcZ, 1f), inverseViewProj);
        return new Vector3(world.X, world.Y, world.Z) / world.W;
    }

    private static bool RayIntersects(
        Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, out float distance)
    {
        var near = 0f;
        var far = float.PositiveInfinity;
        for (var axis = 0; axis < 3; axis++)
        {
            var o = origin[axis];
            var d = direction[axis];
            if (MathF.Abs(d) < 1e-6f)
            {
                if (o < min[axis] || o > max[axis])
                {
                    distance = 0;
                    return false;
                }

                continue;
            }

            var a = (min[axis] - o) / d;
            var b = (max[axis] - o) / d;
            if (a > b)
            {
                (a, b) = (b, a);
            }

            near = MathF.Max(near, a);
            far = MathF.Min(far, b);
            if (far < near)
            {
                distance = 0;
                return false;
            }
        }

        distance = near;
        return far >= MathF.Max(near, 0f);
    }

    private void UpdateInstanceHighlight()
    {
        if (_instanceBuffer is null || _instances.Length == 0)
        {
            return;
        }

        for (var i = 0; i < _instances.Length; i++)
        {
            _instances[i] = _instances[i].WithHighlight(i == _hoveredInstance ? 1f : 0f);
        }

        var mapped = _context.Map(_instanceBuffer, MapMode.WriteDiscard);
        var stride = Marshal.SizeOf<Instance>();
        for (var i = 0; i < _instances.Length; i++)
        {
            Marshal.StructureToPtr(_instances[i], mapped.DataPointer + i * stride, false);
        }

        _context.Unmap(_instanceBuffer, 0);
    }

    /// <summary>Resize the swapchain buffers to a new panel size. Zero-size
    /// (a collapsed / not-yet-laid-out panel) is guarded — ResizeBuffers with
    /// a 0 dimension fails. The user's camera pose is intentionally preserved;
    /// changing aspect ratio must not silently reset orbit, pan, or zoom.</summary>
    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == _width && height == _height)
        {
            return;
        }

        ReleaseRenderTargets();
        _swapChain.ResizeBuffers(
            2, (uint)width, (uint)height, DxgiFormat.B8G8R8A8_UNorm, SwapChainFlags.None);
        _width = width;
        _height = height;
        CreateRenderTargets(width, height);
    }

    private void CreateDevice()
    {
        // Hardware first, WARP fallback. BgraSupport is required for a
        // composition swapchain.
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
        // SwapChainPanel's DirectComposition visual owns presentation. The
        // backbuffer is always single-sample here (flip-model requirement).
        var desc = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = DxgiFormat.B8G8R8A8_UNorm,
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
        using var vsBlob = Compiler.Compile(Hlsl, "VSMain", "graph3d.hlsl", "vs_5_0");
        using var psBlob = Compiler.Compile(Hlsl, "PSMain", "graph3d.hlsl", "ps_5_0");
        _vs = _device.CreateVertexShader(vsBlob.Span);
        _ps = _device.CreatePixelShader(psBlob.Span);
        _layout = _device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, DxgiFormat.R32G32B32_Float, 0, 0, InputClassification.PerVertexData, 0),
            new InputElementDescription("NORMAL", 0, DxgiFormat.R32G32B32_Float, 12, 0, InputClassification.PerVertexData, 0),
            new InputElementDescription("IOFFSET", 0, DxgiFormat.R32G32B32_Float, 0, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("IHEIGHT", 0, DxgiFormat.R32_Float, 12, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("ITOP", 0, DxgiFormat.R32G32B32_Float, 16, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("ISIDE", 0, DxgiFormat.R32G32B32_Float, 28, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("IHIGHLIGHT", 0, DxgiFormat.R32_Float, 40, 1, InputClassification.PerInstanceData, 1),
        ], vsBlob.Span);
    }

    private void ConfigurePipeline()
    {
        _context.IASetInputLayout(_layout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetVertexBuffer(0, _vertexBuffer, (uint)Marshal.SizeOf<Vertex>());
        _context.VSSetShader(_vs);
        _context.VSSetConstantBuffer(0, _sceneBuffer);
        _context.PSSetShader(_ps);
        _context.PSSetConstantBuffer(0, _sceneBuffer);
    }

    private void CreateRenderTargets(int width, int height)
    {
        _backbuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        var sample = new SampleDescription((uint)_sampleCount, 0);
        _msaaColor = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormat.B8G8R8A8_UNorm,
            SampleDescription = sample,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
        });
        _msaaRtv = _device.CreateRenderTargetView(_msaaColor);
        _msaaDepth = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormat.D32_Float,
            SampleDescription = sample,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
        });
        _msaaDepthView = _device.CreateDepthStencilView(_msaaDepth);
    }

    private void ReleaseRenderTargets()
    {
        _msaaDepthView.Dispose();
        _msaaDepth.Dispose();
        _msaaRtv.Dispose();
        _msaaColor.Dispose();
        _backbuffer.Dispose();
    }

    public void Dispose()
    {
        // Reverse of construction. Null-conditionals also make this safe while
        // the constructor unwinds a partially initialized renderer.
        _sceneBuffer?.Dispose();
        _instanceBuffer?.Dispose();
        _vertexBuffer?.Dispose();
        _layout?.Dispose();
        _ps?.Dispose();
        _vs?.Dispose();
        _msaaDepthView?.Dispose();
        _msaaDepth?.Dispose();
        _msaaRtv?.Dispose();
        _msaaColor?.Dispose();
        _backbuffer?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }

    // ── camera: orthographic orbit rig + auto-fit (macOS OrbitRig parity) ──

    private const string CameraStorageKey = "tokenbar.orbit.v1";

    /// <summary>Persist the current camera pose after an interaction. The
    /// panel calls this on pointer release so drag motion never blocks the UI
    /// on synchronous settings-file writes.</summary>
    public void PersistCamera()
    {
        var raw = string.Join(",", new[]
        {
            _azimuth.ToString("R", CultureInfo.InvariantCulture),
            _elevation.ToString("R", CultureInfo.InvariantCulture),
            _scale.ToString("R", CultureInfo.InvariantCulture),
            _target.X.ToString("R", CultureInfo.InvariantCulture),
            _target.Y.ToString("R", CultureInfo.InvariantCulture),
            _target.Z.ToString("R", CultureInfo.InvariantCulture),
        });
        AppSettings.Store.SetString(CameraStorageKey, raw);
    }

    private bool RestoreCamera()
    {
        var raw = AppSettings.Store.GetString(CameraStorageKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 6)
        {
            return false;
        }

        var values = new float[6];
        for (var i = 0; i < values.Length; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out values[i])
                || !float.IsFinite(values[i]))
            {
                return false;
            }
        }

        _azimuth = values[0];
        _elevation = Math.Clamp(values[1], -89f * MathF.PI / 180f, 89f * MathF.PI / 180f);
        _scale = Math.Clamp(values[2], MinScale, MaxScale);
        _target = new Vector3(values[3], values[4], values[5]);
        return true;
    }

    /// <summary>World→view matrix for the current pose.</summary>
    private Matrix4x4 BuildView()
    {
        var cosE = MathF.Cos(_elevation);
        var dir = new Vector3(
            cosE * MathF.Sin(_azimuth), MathF.Sin(_elevation), cosE * MathF.Cos(_azimuth));
        var eye = _target + CameraDistance * dir;
        return Matrix4x4.CreateLookAt(eye, _target, Vector3.UnitY);
    }

    private (Matrix4x4 View, Matrix4x4 Proj, Vector3 EyeDir) BuildCamera()
    {
        var view = BuildView();
        var aspect = (float)_width / _height;
        var proj = Matrix4x4.CreateOrthographic(
            2f * _scale * aspect, 2f * _scale, -1000f, 1000f);
        var cosE = MathF.Cos(_elevation);
        var eyeDir = new Vector3(
            cosE * MathF.Sin(_azimuth), MathF.Sin(_elevation), cosE * MathF.Cos(_azimuth));
        return (view, proj, eyeDir);
    }

    /// <summary>Frame the active-cell AABB: reset to the default pose, project
    /// the corners into camera space, size the ortho scale to fit with padding,
    /// then re-center the target on the box. macOS OrbitRig.fit() parity.</summary>
    private void Fit()
    {
        _azimuth = MathF.PI / 4f;
        _elevation = MathF.Atan2(0.45f, MathF.Sqrt(2f) * 0.7f);
        _target = Vector3.Zero;
        if (_fitCorners.Length == 0)
        {
            _scale = 26f;
            return;
        }

        var view = BuildView();
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
        foreach (var c in _fitCorners)
        {
            var v = Vector3.Transform(c, view); // world→camera; x=right, y=up
            minX = MathF.Min(minX, v.X);
            maxX = MathF.Max(maxX, v.X);
            minY = MathF.Min(minY, v.Y);
            maxY = MathF.Max(maxY, v.Y);
        }

        var aspect = (float)_width / _height;
        const float padding = 0.85f;
        _scale = MathF.Max((maxY - minY) / 2f, (maxX - minX) / 2f / aspect) / padding;
        _scale = Math.Clamp(_scale, MinScale, MaxScale);

        // Re-center on the box: shift the target along the camera's world-space
        // right/up axes (the view matrix's basis columns).
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        var centerX = (minX + maxX) / 2f;
        var centerY = (minY + maxY) / 2f;
        _target += right * centerX + up * centerY;
    }

    // ── grid geometry + instances (macOS ContributionGraph3D parity) ───────

    private const float Cell = 1.0f;
    private const float Step = 1.15f; // CELL + GAP (0.15)
    private const float BaseHeight = 0.05f;
    private const float MaxHeight = 4.0f;

    private static readonly Vector3 ActiveLight = Rgb(0xBFDBFE);
    private static readonly Vector3 ActiveDark = Rgb(0x1E3A8A);

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

    /// <summary>Build one box per in-year cell (active bars + inactive floor
    /// tiles), plus the world-space AABB corners of the populated cells for a
    /// useful, centered fit. Every rendered AABB is retained for CPU
    /// orthographic picking so inactive floor tiles correctly occlude bars.</summary>
    private static (Instance[] Instances, Vector3[] Corners, RenderedHit[] RenderedHits) BuildInstances(
        GridLayout grid, bool dark)
    {
        var (inactiveTop, inactiveSide) = dark
            ? (Rgb(0x3A4150), Rgb(0x2B313D))
            : (Rgb(0xFFFFFF), Rgb(0xEAEDF2));
        var totalWidth = grid.Cols * Step;
        var totalDepth = grid.Rows * Step;
        var offsetX = -totalWidth / 2f;
        var offsetZ = -totalDepth / 2f;
        var maxTokens = (float)Math.Max(grid.MaxTokens, 1);
        const float half = Cell / 2f;

        var instances = new List<Instance>(grid.Cols * grid.Rows);
        var corners = new List<Vector3>();
        var renderedHits = new List<RenderedHit>();
        foreach (var cell in grid.Cells)
        {
            if (!cell.InYear)
            {
                continue;
            }

            var x = offsetX + cell.Col * Step + Step / 2f;
            var z = offsetZ + cell.Row * Step + Step / 2f;
            var height = BaseHeight;
            var top = inactiveTop;
            var side = inactiveSide;
            if (cell.Active)
            {
                var frac = cell.Tokens / maxTokens;
                height = BaseHeight + MathF.Pow(frac, 0.6f) * MaxHeight;
                var t = Math.Clamp(MathF.Pow(frac, 0.5f), 0f, 1f);
                top = Lerp(ActiveLight, ActiveDark, t);
                side = top * 0.78f;
                foreach (var dx in (ReadOnlySpan<float>)[-half, half])
                {
                    foreach (var dz in (ReadOnlySpan<float>)[-half, half])
                    {
                        foreach (var y in (ReadOnlySpan<float>)[0f, height])
                        {
                            corners.Add(new Vector3(x + dx, y, z + dz));
                        }
                    }
                }
            }

            renderedHits.Add(new RenderedHit(
                cell,
                new Vector3(x - half, 0, z - half),
                new Vector3(x + half, height, z + half),
                instances.Count));
            instances.Add(new Instance(new Vector3(x, 0, z), height, top, side));
        }

        // With no activity there is no populated AABB, so frame the whole
        // calendar footprint at the graph's maximum design height.
        if (corners.Count == 0)
        {
            var halfWidth = totalWidth / 2f;
            var halfDepth = totalDepth / 2f;
            foreach (var x in (ReadOnlySpan<float>)[-halfWidth, halfWidth])
            {
                foreach (var z in (ReadOnlySpan<float>)[-halfDepth, halfDepth])
                {
                    foreach (var y in (ReadOnlySpan<float>)[0f, MaxHeight])
                    {
                        corners.Add(new Vector3(x, y, z));
                    }
                }
            }
        }

        return ([.. instances], [.. corners], [.. renderedHits]);
    }

    private static Vector3 Rgb(uint hex) => new(
        ((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f);

    private static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b - a) * t;

    private readonly struct RenderedHit(
        GridCell cell, Vector3 min, Vector3 max, int instanceIndex)
    {
        public readonly GridCell Cell = cell;
        public readonly Vector3 Min = min;
        public readonly Vector3 Max = max;
        public readonly int InstanceIndex = instanceIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Vertex(Vector3 position, Vector3 normal)
    {
        public readonly Vector3 Position = position;
        public readonly Vector3 Normal = normal;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Instance(
        Vector3 offset, float height, Vector3 top, Vector3 side, float highlight = 0)
    {
        public readonly Vector3 Offset = offset;
        public readonly float Height = height;
        public readonly Vector3 Top = top;
        public readonly Vector3 Side = side;
        public readonly float Highlight = highlight;
        public readonly float Pad = 0;

        public Instance WithHighlight(float value) =>
            new(Offset, Height, Top, Side, value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Scene
    {
        public Matrix4x4 ViewProj;
        public Vector3 EyeDir;
        public float Pad;
    }
}
