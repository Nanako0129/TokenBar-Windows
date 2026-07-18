# TokenBar for Windows

Windows port of [TokenBar](https://github.com/Nanako0129/TokenBar) — the
menu-bar/tray AI coding-agent token-usage monitor. Same Rust parsing core,
a native WinUI 3 shell.

> **Status: pre-alpha, under active development.** See the progress table
> below. The macOS app is the shipping reference implementation.

## Architecture

| Layer | Path | Notes |
|---|---|---|
| Rust core | `crates/tb_core_ffi` + `vendor/tokscale-core` | Copied from the macOS repo (single sync source — see `vendor/tokscale-core/SYNC.md`). C ABI, JSON envelope, built as `cdylib` for P/Invoke |
| C ABI contract | `include/ctb.h` | 10 entry points, `{"ok":true,"data":…}` / `{"ok":false,"err":…}` |
| Interop | `src/TokenBar.Interop` | `net10.0`, platform-neutral — P/Invoke facade + envelope decode |
| Logic | `src/TokenBar.Core` | `net10.0`, platform-neutral — C# port of the macOS `TokenBarCore` |
| Shell | `src/TokenBar.App` | WinUI 3, unpackaged. Windows-only build (not in the slnx): `dotnet build src/TokenBar.App -c Release -p:Platform=x64` |

## Build

```bash
# macOS (inner loop — no Windows needed)
scripts/check.sh

# Windows
.\scripts\dev.ps1
```

Prereqs: Rust (stable), .NET 10 SDK; on Windows the MSVC toolchain.

Windows CI now blocks on the full Rust workspace release tests, the WinUI App
x64 Release build, solution build/tests, isolated P/Invoke and synthetic parse
smokes, a self-contained win-x64 smoke bundle, and the ARM64 Rust release
cross-build. The provider-pace branch passed local command-equivalent x64 and
ARM64 gates plus fresh review on 2026-07-19; it has not been pushed, so no new
remote CI result is claimed. `cargo fmt` is not currently a repository CI gate;
its existing workspace-wide formatting debt is tracked separately.

## Progress

| Phase | Scope | Status |
|---|---|---|
| 0 | Repo bootstrap + P/Invoke smoke | ✅ 2026-07-02 — C# ↔ Rust cdylib seam verified on macOS (`tb_probe` → 84k messages), CI on windows-latest |
| 1 | Rust Windows fixes (HOME→dirs, TLS, antigravity) | ✅ 2026-07-02 — all 10 entry points verified on a real x64 Windows box against real session data (271 msgs parsed, pricing fetched over rustls, quota windows decoded) |
| 2 | 3D contribution graph spike | ✅ 2026-07-02 — GO (Vortice/D3D11 instancing verified on real hardware, ~0.2ms/frame; the product SwapChainPanel lifecycle was completed in Phase 8). See `spike/RESULTS.md` |
| 3 | TokenBar.Core C# port + cross-check vs Swift | ✅ 2026-07-19 — all modules ported (incl. the v1.4.0 delta and provider pace v3), 268 unit tests green; **fixture cross-check vs Swift done** (`crosscheck/`: 116 legacy cases plus 12 provider-v3 cases, zero material difference; the original Format pass caught 4 real printf-rounding divergences — pre-round deleted, .NET Core F-formats are IEEE-correct) |
| 4 | Tray skeleton + flyout window | ✅ 2026-07-02 — tray icon + Open/Quit menu, borderless rounded Acrylic flyout (translucent while unfocused, topmost), PerMonitorV2 DPI, show/hide slide, single instance, taskbar-edge placement, polling engine. SwapChainPanel lifecycle was completed in Phase 8; compositor-native animation remains in the polish backlog |
| 5 | Overview lens + polling engine | ✅ 2026-07-02 — five cards (stacked chart + wrap legend, agent limits with live pace markers, trace, models, streaks), instant styled hover tooltips, WH_MOUSE_LL wheel path |
| 6 | Remaining five lenses | ✅ 2026-07-02 — lens router with 160ms crossfade transitions; Models (full list + pricing hint), Daily (tap drill-down), Hourly (Timeline/Profile + show-more), Stats, Agents; lazy report loading. Verified by the user against the full synced history (5.6B tokens / 70 days). Cold first paint 11.1s → **3.8s** (warm 3.2s) after the EcoQoS/priority fix + mac-parity slow lane: schtasks-launched processes inherit BELOW_NORMAL and Windows 11 throttles tray apps (EcoQoS) — the app now parses at normal QoS and returns to power-friendly throttling when idle; graph ∥ modelReport run concurrently and agentUsage no longer gates the first snapshot (both mirror the macOS DashboardModel) |
| 7 | Settings + tray extras | 🔶 feature-complete (macOS parity) — settings store (`%APPDATA%\TokenBar\settings.json`, atomic, unit-tested) with the year filter, chart persistence, manual-refresh spinner; tray: seven modes with the value drawn into the icon (tooltip carries the full string), bars/ring/popsicle gauges (macOS geometry verbatim), cat/parrot animation (HICON-cached, ~0.5% of a core at idle), full context menu with live quota sources; Mica settings window (ten sections, live keys, autostart via HKCU Run honoring StartupApproved); flyout footer gear+Quit; in-flyout Ctrl-shortcut set. Global `RegisterHotKey` dropped: the macOS reference ships no global shortcut, so it's not a parity gap (parked as an optional Windows-only nicety in Phase 9). Verification: icon gallery + live tray screenshots + synthesized input on the x64 box; the non-quota Settings flow passed the 125% DPI interactive gate on 2026-07-17, including live 520→600 DIP flyout resizing, persistence, singleton hide/reopen, autostart restoration, and the 48ms entrance-animation race. The separate 150% pass also passed on 2026-07-17 in an isolated RDP session: both windows reported 144 DPI, 520→600 DIP mapped immediately to 780→900 physical px, and the persisted height survived a process restart. A 33-active-day synthetic fixture supported user-checked 3D hover/orbit/zoom/Fit/Reset, and the Flyout Acrylic was subsequently verified with loaded 3D content in both light and dark themes at 200% DPI |
| 8 | 3D integration | ✅ 2026-07-17 — product card renders the real contribution grid with macOS-parity colors/lighting (sRGB-correct opaque faces), 4× MSAA, render-on-demand orbit/pan/zoom, persisted `tokenbar.orbit.v1`, Fit/Reset, custom ray-picked tooltip, and a persisted 2D/3D toggle. Real x64 checks include corrected pointer/DPI alignment, 2D↔3D in 6.7–20.1ms, a 241-frame drag trace, idle no-present, the 50-cycle lifecycle gate, and a retained 60-minute soak: 8230 cycles, `created=8230 released=8230 removed=0 errors=0`, 3600.7s elapsed, with private-memory and handle thresholds passing. Fresh review confirmed the lifecycle and cleanup result |
| 9 | Polish + parity + vendor re-sync | 🔶 vendor portability and non-quota client tabs complete (2026-07-17), with all 11 Windows-only code-drift commits recorded in `SYNC.md`. **Provider pace v3 reconciliation completed 2026-07-19** against the exact macOS `1e00e7b` tree: Codex, Claude, Grok, Antigravity, and Copilot recurring percentage cards use stable `cardId`, opaque account scope, exact/observed duration, typed lifecycle states, and backend-owned coherent history; Windows adds CNG-backed installation identity, protected DACLs, reparse/file-ID checks, locking, capacity bounds, and atomic replacement. Strict C# decoding, `clientId|cardId` selection retention/migration, shared Dashboard/Settings row semantics, responsive Full layout, Classic/Off suppression, and typed learning/unavailable previews are active. Verification includes 268 .NET tests; macOS and Windows x64 Rust/workspace/App/synthetic-smoke gates; ARM64 release build, 15 native security/history tests, 12 provider cases, and WinUI startup; Swift↔C# zero-difference checks across 12 provider-v3 and 116 legacy cases; light/dark responsive UI checks at 100%, 150%, and 200% DPI; production settings/profile preservation; and a fresh end-to-end verifier. Real-credential provider smoke and remote CI were not run. This completes the pace contract only; broader quota-source ordering/visibility and new Agent-limits feature scope remain unopened · backlog: demo mode; optional Windows-only global hotkey to toggle the flyout (no macOS equivalent — needs a key-binding UI) |
| 10–12 | Releases → Velopack → winget/Scoop | — |

## Credits

Parsing engine vendored from [tokscale](https://github.com/junhoyeo/tokscale)
by junhoyeo. Original menu-bar concept by
[handlecusion's tokcat](https://github.com/handlecusion/tokcat).
