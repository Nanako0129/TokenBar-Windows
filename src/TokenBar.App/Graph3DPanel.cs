using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace TokenBar.App;

/// <summary>
/// SwapChainPanel host for the 3D contribution graph — Phase 8 Gate 0. Owns
/// the D3D renderer's lifetime against the flyout show/hide cycle: created
/// lazily on <see cref="Activate"/> once the panel has a real pixel size,
/// torn down on <see cref="Release"/>. DXGI device-removed/reset is caught,
/// logged, and answered by a full re-create on the next show.
///
/// This is the ONE file with WinUI-specific COM interop
/// (ISwapChainPanelNative.SetSwapChain); the interop is isolated below and
/// flagged as the highest-risk unverifiable seam in the Gate 0 report.
///
/// The static counters are the soak's assertion surface (see Soak3D): a single
/// panel exists per process, so process-global counters are simplest.
/// </summary>
internal sealed class Graph3DPanel : SwapChainPanel
{
    public static int CreatedCount;
    public static int ReleasedCount;
    public static int DeviceRemovedCount;
    public static int ErrorCount;

    private Graph3DRenderer? _renderer;
    private bool _active;

    // Fixed orbit angle for Gate 0 — visuals beyond the spike's grid are out
    // of scope; this only exercises the swapchain/device lifecycle.
    private const float Azimuth = 35f;
    private const float Elevation = 30f;

    public Graph3DPanel()
    {
        // A collapsed panel has no size at Activate time; the first layout
        // pass fires SizeChanged, which is where creation actually lands.
        SizeChanged += (_, _) => OnSizeChanged();
    }

    /// <summary>Flyout shown with the dev toggle on: create (or re-create)
    /// the device and render one frame.</summary>
    public void Activate()
    {
        _active = true;
        TryCreate();
    }

    /// <summary>Flyout hidden: dispose the swapchain + device deterministically
    /// so nothing GPU-side lingers while the flyout is away.</summary>
    public void Release()
    {
        _active = false;
        if (_renderer is null)
        {
            return;
        }

        DetachSwapChain();
        _renderer.Dispose();
        _renderer = null;
        Interlocked.Increment(ref ReleasedCount);
        DevLog.Write(
            $"graph3d: released (created={CreatedCount} released={ReleasedCount})");
    }

    private void OnSizeChanged()
    {
        if (!_active)
        {
            return;
        }

        if (_renderer is null)
        {
            TryCreate();
            return;
        }

        var (w, h) = PixelSize();
        if (w == 0 || h == 0)
        {
            return; // collapsed mid-life; keep the device, wait for real size
        }

        try
        {
            _renderer.Resize(w, h);
            _renderer.Render(Azimuth, Elevation);
        }
        catch (Exception ex)
        {
            HandleDeviceError("resize", ex);
        }
    }

    private void TryCreate()
    {
        if (_renderer is not null)
        {
            return;
        }

        var (w, h) = PixelSize();
        if (w == 0 || h == 0)
        {
            return; // no size yet; SizeChanged re-enters here after layout
        }

        try
        {
            _renderer = new Graph3DRenderer(w, h);
            SetSwapChain(_renderer.SwapChainPointer);
            Interlocked.Increment(ref CreatedCount);
            DevLog.Write($"graph3d: created {w}x{h} (created={CreatedCount})");
            if (!_renderer.Render(Azimuth, Elevation))
            {
                HandleDeviceLost("initial-render");
            }
        }
        catch (Exception ex)
        {
            HandleDeviceError("create", ex);
        }
    }

    private (int Width, int Height) PixelSize()
    {
        // SwapChainPanel renders at physical pixels; ActualWidth is in DIPs, so
        // scale by the composition scale (1.0 before the first layout pass).
        var scaleX = CompositionScaleX > 0 ? CompositionScaleX : 1f;
        var scaleY = CompositionScaleY > 0 ? CompositionScaleY : 1f;
        return ((int)(ActualWidth * scaleX), (int)(ActualHeight * scaleY));
    }

    /// <summary>Device-removed/reset (TDR, driver update, GPU reset): expected
    /// and recoverable — count it and re-create on the spot if still active.</summary>
    private void HandleDeviceLost(string where)
    {
        Interlocked.Increment(ref DeviceRemovedCount);
        DevLog.Write($"graph3d: device lost at {where} (removed={DeviceRemovedCount})");
        Recreate();
    }

    /// <summary>An unexpected throw from the render path — logged as an error
    /// (the soak treats errors as failure), then recovered like a lost device
    /// so a single stray fault doesn't wedge the panel.</summary>
    private void HandleDeviceError(string where, Exception ex)
    {
        Interlocked.Increment(ref ErrorCount);
        DevLog.Write($"graph3d: {where} FAILED (errors={ErrorCount}): {ex.Message}");
        Recreate();
    }

    private void Recreate()
    {
        DetachSwapChain();
        _renderer?.Dispose();
        _renderer = null;
        if (_active)
        {
            TryCreate();
        }
    }

    private void DetachSwapChain()
    {
        try
        {
            SetSwapChain(nint.Zero);
        }
        catch (Exception ex)
        {
            DevLog.Write($"graph3d: detach failed: {ex.Message}");
        }
    }

    // ── ISwapChainPanelNative interop (the one unverifiable seam) ──────────
    //
    // The documented WinUI 3 path to bind a DXGI swapchain to a SwapChainPanel:
    // QI the panel for ISwapChainPanelNative and hand it the swapchain's
    // IUnknown. CsWinRT's CastExtensions.As<T> performs the QI for a hand-
    // declared [ComImport] interface (the same As<T> the flyout uses for
    // ICompositionSupportsSystemBackdrop). Passing nint.Zero detaches.

    private void SetSwapChain(nint swapChain)
    {
        var native = WinRT.CastExtensions.As<ISwapChainPanelNative>(this);
        Marshal.ThrowExceptionForHR(native.SetSwapChain(swapChain));
    }

    [ComImport]
    [Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative
    {
        [PreserveSig]
        int SetSwapChain(nint swapChain);
    }
}
