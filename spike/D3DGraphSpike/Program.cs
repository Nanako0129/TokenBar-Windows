using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

// Headless render spike for the 3D contribution graph (plan Phase 2, route A).
// Draws 53x7 instanced boxes with an orthographic orbit camera into an
// offscreen target, writes BMPs at several azimuth/elevation angles, and
// prints adapter + timing numbers. Everything runs without a window so it can
// be driven over SSH; the SwapChainPanel/WinUI stage builds on this.

const int Width = 1200;
const int Height = 800;
const int Cols = 53;
const int Rows = 7;

var swInit = Stopwatch.StartNew();

// Device: hardware first, WARP fallback (the verdict must note which).
ID3D11Device device;
ID3D11DeviceContext context;
var driver = "hardware";
try
{
    D3D11.D3D11CreateDevice(
        null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
        [FeatureLevel.Level_11_0], out device!, out context!).CheckError();
}
catch
{
    D3D11.D3D11CreateDevice(
        null, DriverType.Warp, DeviceCreationFlags.BgraSupport,
        [FeatureLevel.Level_11_0], out device!, out context!).CheckError();
    driver = "WARP (software)";
}

using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
using (var adapter = dxgiDevice.GetAdapter())
{
    Console.WriteLine($"adapter: {adapter.Description.Description.TrimEnd('\0')} [{driver}]");
}

// Offscreen target + depth.
using var renderTarget = device.CreateTexture2D(new Texture2DDescription
{
    Width = Width, Height = Height, MipLevels = 1, ArraySize = 1,
    Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
    Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget,
});
using var rtv = device.CreateRenderTargetView(renderTarget);
using var depth = device.CreateTexture2D(new Texture2DDescription
{
    Width = Width, Height = Height, MipLevels = 1, ArraySize = 1,
    Format = Format.D32_Float, SampleDescription = new SampleDescription(1, 0),
    Usage = ResourceUsage.Default, BindFlags = BindFlags.DepthStencil,
});
using var dsv = device.CreateDepthStencilView(depth);
using var staging = device.CreateTexture2D(new Texture2DDescription
{
    Width = Width, Height = Height, MipLevels = 1, ArraySize = 1,
    Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
    Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
});

// Shaders: row_major so System.Numerics row-vector matrices pass through.
const string Hlsl = """
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

var vsBlob = Compiler.Compile(Hlsl, "VSMain", "spike.hlsl", "vs_5_0");
var psBlob = Compiler.Compile(Hlsl, "PSMain", "spike.hlsl", "ps_5_0");
using var vs = device.CreateVertexShader(vsBlob.Span);
using var ps = device.CreatePixelShader(psBlob.Span);
using var layout = device.CreateInputLayout(
[
    new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0, InputClassification.PerVertexData, 0),
    new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0, InputClassification.PerVertexData, 0),
    new InputElementDescription("IOFFSET", 0, Format.R32G32B32_Float, 0, 1, InputClassification.PerInstanceData, 1),
    new InputElementDescription("IHEIGHT", 0, Format.R32_Float, 12, 1, InputClassification.PerInstanceData, 1),
    new InputElementDescription("ICOLOR", 0, Format.R32G32B32_Float, 16, 1, InputClassification.PerInstanceData, 1),
], vsBlob.Span);

// Unit cube: y in [0,1] so instance height scales upward; xz in [-0.4, 0.4]
// leaving a gutter between tiles, GitHub-graph style.
var cube = BuildCube(0.4f);
using var vertexBuffer = device.CreateBuffer(cube, new BufferDescription(
    (uint)(cube.Length * Marshal.SizeOf<Vertex>()), BindFlags.VertexBuffer));

// Instances: deterministic pseudo-usage (seeded LCG — no wall clock), ~70%
// active days, provider-ish blue ramp by intensity like the macOS graph.
var instances = BuildInstances(out var activeCount);
using var instanceBuffer = device.CreateBuffer(instances, new BufferDescription(
    (uint)(instances.Length * Marshal.SizeOf<Instance>()), BindFlags.VertexBuffer));

using var sceneBuffer = device.CreateBuffer(new BufferDescription(
    (uint)Marshal.SizeOf<Scene>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

Console.WriteLine($"init: {swInit.ElapsedMilliseconds}ms, instances={instances.Length} (active={activeCount})");

// Fixed pipeline state.
context.IASetInputLayout(layout);
context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
context.IASetVertexBuffer(0, vertexBuffer, (uint)Marshal.SizeOf<Vertex>());
context.IASetVertexBuffer(1, instanceBuffer, (uint)Marshal.SizeOf<Instance>());
context.VSSetShader(vs);
context.VSSetConstantBuffer(0, sceneBuffer);
context.PSSetShader(ps);
context.RSSetViewport(new Viewport(0, 0, Width, Height));
context.OMSetRenderTargets(rtv, dsv);

// Render the orbit sweep: four camera angles → four BMPs.
(string Name, float AzimuthDeg, float ElevationDeg)[] shots =
[
    ("front-high", 0f, 55f),
    ("oblique", 35f, 30f),
    ("side-low", 80f, 12f),
    ("top-down", 15f, 80f),
];

var outDir = Path.Combine(AppContext.BaseDirectory, "shots");
Directory.CreateDirectory(outDir);
foreach (var (name, az, el) in shots)
{
    var swFrame = Stopwatch.StartNew();
    RenderFrame(az, el);
    context.Flush();
    var renderMs = swFrame.Elapsed.TotalMilliseconds;
    SaveBmp(Path.Combine(outDir, $"{name}.bmp"));
    Console.WriteLine($"shot {name,-10} az={az,5:F0} el={el,4:F0} render={renderMs,6:F2}ms (+readback={swFrame.Elapsed.TotalMilliseconds - renderMs:F2}ms)");
}

// Sustained-rate estimate (render only, no readback) — the interactive drag
// budget. 300 frames across a sweeping azimuth.
var swLoop = Stopwatch.StartNew();
const int LoopFrames = 300;
for (var i = 0; i < LoopFrames; i++)
{
    RenderFrame(i * 1.2f, 35f);
}

context.Flush();
var fps = LoopFrames / swLoop.Elapsed.TotalSeconds;
Console.WriteLine($"sustained: {LoopFrames} frames in {swLoop.ElapsedMilliseconds}ms = {fps:F0} fps");
Console.WriteLine("PASS");
return 0;

void RenderFrame(float azimuthDeg, float elevationDeg)
{
    context.ClearRenderTargetView(rtv, new Color4(0.086f, 0.098f, 0.149f, 1f)); // #161926-ish
    context.ClearDepthStencilView(dsv, DepthStencilClearFlags.Depth, 1f, 0);

    // Orthographic orbit rig, matching the macOS OrbitRig parameterization:
    // azimuth around Y, elevation above the plane, fixed target at grid center.
    var az = azimuthDeg * MathF.PI / 180f;
    var el = elevationDeg * MathF.PI / 180f;
    var target = new Vector3(0, 0.6f, 0);
    const float radius = 80f;
    var eye = target + radius * new Vector3(
        MathF.Cos(el) * MathF.Sin(az), MathF.Sin(el), MathF.Cos(el) * MathF.Cos(az));
    var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
    const float viewHeight = 14f;
    var proj = Matrix4x4.CreateOrthographic(
        viewHeight * Width / (float)Height, viewHeight, -1000f, 1000f);

    var scene = new Scene
    {
        ViewProj = view * proj,
        LightDir = Vector3.Normalize(new Vector3(-0.5f, -1f, -0.3f)),
    };
    var mapped = context.Map(sceneBuffer, MapMode.WriteDiscard);
    Marshal.StructureToPtr(scene, mapped.DataPointer, false);
    context.Unmap(sceneBuffer, 0);

    context.DrawInstanced((uint)cube.Length, (uint)instances.Length, 0, 0);
}

unsafe void SaveBmp(string path)
{
    context.CopyResource(staging, renderTarget);
    var mapped = context.Map(staging, 0, MapMode.Read);
    try
    {
        var pixels = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        {
            var src = (byte*)mapped.DataPointer + y * mapped.RowPitch;
            Marshal.Copy((nint)src, pixels, y * Width * 4, Width * 4);
        }

        using var f = new BinaryWriter(File.Create(path));
        var rowSize = Width * 4;
        var dataSize = rowSize * Height;
        f.Write((ushort)0x4D42);          // BM
        f.Write(54 + dataSize);           // file size
        f.Write(0);                       // reserved
        f.Write(54);                      // pixel data offset
        f.Write(40);                      // BITMAPINFOHEADER
        f.Write(Width);
        f.Write(Height);
        f.Write((ushort)1);               // planes
        f.Write((ushort)32);              // bpp
        f.Write(0);                       // BI_RGB
        f.Write(dataSize);
        f.Write(2835); f.Write(2835);     // 72 dpi
        f.Write(0); f.Write(0);           // palette
        for (var y = Height - 1; y >= 0; y--) // BMP rows are bottom-up
        {
            f.Write(pixels, y * rowSize, rowSize);
        }
    }
    finally
    {
        context.Unmap(staging, 0);
    }
}

static Vertex[] BuildCube(float half)
{
    // 6 faces x 2 triangles; y in [0,1].
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

static Instance[] BuildInstances(out int activeCount)
{
    var list = new List<Instance>();
    uint seed = 20260702;
    uint Next() => seed = seed * 1664525u + 1013904223u;
    activeCount = 0;
    for (var col = 0; col < Cols; col++)
    {
        for (var row = 0; row < Rows; row++)
        {
            var roll = Next() % 100;
            if (roll < 30)
            {
                continue; // inactive day: no box (macOS draws a flat floor tile instead)
            }

            activeCount++;
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
readonly struct Vertex(Vector3 position, Vector3 normal)
{
    public readonly Vector3 Position = position;
    public readonly Vector3 Normal = normal;
}

[StructLayout(LayoutKind.Sequential)]
readonly struct Instance(Vector3 offset, float height, Vector3 color)
{
    public readonly Vector3 Offset = offset;
    public readonly float Height = height;
    public readonly Vector3 Color = color;
    public readonly float Pad = 0;
}

[StructLayout(LayoutKind.Sequential)]
struct Scene
{
    public Matrix4x4 ViewProj;
    public Vector3 LightDir;
    public float Pad;
}
