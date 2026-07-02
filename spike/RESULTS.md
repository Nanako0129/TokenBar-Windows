# Phase 2 spike results — 3D contribution graph on D3D11

> Verdict: **GO for route A** (Vortice.Windows + D3D11 instancing,
> render-on-demand), with one deferred item — the SwapChainPanel/WinUI
> lifecycle check moves into Phase 4, where the flyout window exists anyway.

## Stage 1 — headless render path (2026-07-02, x64 box)

`D3DGraphSpike` renders the 53×7 grid as instanced boxes (one cube VB + 32-byte
per-instance data) with an orthographic orbit camera into an offscreen target,
then writes BMPs — fully verifiable over SSH on a bare machine.

| Measurement | Result |
|---|---|
| Adapter | AMD Radeon RX 9070 XT (hardware; WARP fallback wired) |
| Device + pipeline init | 90 ms |
| Per-frame render (265 instances) | ~0.2 ms |
| Readback (1200×800 staging copy) | ~5 ms (debug tooling only; product never reads back) |
| Sustained command submission | 300 frames ≪ 1 s (GPU load trivial) |
| Visual | 4 orbit angles verified by eye: intensity ramp, per-face shading, gutters, GitHub band structure all correct |

Notes for Phase 8 productization:

- `row_major float4x4` in HLSL lets System.Numerics matrices pass through
  untransposed.
- Vortice 3.x API: `Compiler.Compile` returns `ReadOnlyMemory<byte>`; sizes,
  strides, and draw counts are `uint`.
- Height curve `0.15 + 3.0 * pow(frac, 0.6)` and the
  `#BFDBFE → #1E3A8A` intensity ramp mirror ContributionGraph3D.swift.
- Camera: orbit rig (azimuth/elevation/ortho-scale) parameterization matches
  the macOS `tokenbar.orbit.v1` persistence contract; fit-to-cluster math
  still to port in Phase 8.
- render-on-demand holds trivially: nothing draws without an explicit frame.

## Stage 2 — SwapChainPanel/WinUI lifecycle (deferred to Phase 4)

Needs an interactive window station (SSH sessions can't show or reliably
exercise a real swapchain's present/occlusion path). Phase 4 builds the WinUI
flyout skeleton anyway; the open/close ×50 device-lifetime soak and the
Acrylic-composition interaction run there, before Phase 8 commits to the
integration. Risk assessment: low — the D3D11 side is proven, and
SwapChainPanel is the documented, widely-used interop path.

## RTSS

Not installed on the verification box; the earlier research stands (RTSS
7.3.4+ ships WinUI3 runtime ignore-triggers, and render-on-demand means a
frozen overlay at worst). Re-check only if a user report surfaces.
