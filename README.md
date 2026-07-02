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
| Logic | `src/TokenBar.Core` | `net10.0`, platform-neutral — C# port of the macOS `TokenBarCore` (in progress) |
| Shell | `src/TokenBar.App` | WinUI 3, unpackaged. Windows-only build (not in the slnx): `dotnet build src/TokenBar.App -c Release -p:Platform=x64` |

## Build

```bash
# macOS (inner loop — no Windows needed)
scripts/check.sh

# Windows
.\scripts\dev.ps1
```

Prereqs: Rust (stable), .NET 10 SDK; on Windows the MSVC toolchain.

## Progress

| Phase | Scope | Status |
|---|---|---|
| 0 | Repo bootstrap + P/Invoke smoke | ✅ 2026-07-02 — C# ↔ Rust cdylib seam verified on macOS (`tb_probe` → 84k messages), CI on windows-latest |
| 1 | Rust Windows fixes (HOME→dirs, TLS, antigravity) | ✅ 2026-07-02 — all 10 entry points verified on a real x64 Windows box against real session data (271 msgs parsed, pricing fetched over rustls, quota windows decoded) |
| 2 | 3D contribution graph spike | ✅ 2026-07-02 — GO (Vortice/D3D11 instancing verified on real hardware, ~0.2ms/frame; SwapChainPanel lifecycle rides with Phase 4). See `spike/RESULTS.md` |
| 3 | TokenBar.Core C# port + cross-check vs Swift | 🔶 all 14 modules ported, 85 unit tests green (2026-07-02); fixture cross-check vs Swift pending |
| 4 | Tray skeleton + flyout window | ✅ 2026-07-02 — tray icon + Open/Quit menu, borderless rounded Acrylic flyout (translucent while unfocused, topmost), PerMonitorV2 DPI, show/hide slide, single instance, taskbar-edge placement, polling engine. Deferred to polish backlog: SwapChainPanel lifecycle, compositor-native animation |
| 5 | Overview lens + polling engine | ✅ 2026-07-02 — five cards (stacked chart + wrap legend, agent limits with live pace markers, trace, models, streaks), instant styled hover tooltips, WH_MOUSE_LL wheel path |
| 6 | Remaining five lenses | ✅ 2026-07-02 — lens router with 160ms crossfade transitions; Models (full list + pricing hint), Daily (tap drill-down), Hourly (Timeline/Profile + show-more), Stats, Agents; lazy report loading. Verified by the user against the full synced history (5.6B tokens / 70 days). Cold first paint 11.1s → **3.8s** (warm 3.2s) after the EcoQoS/priority fix + mac-parity slow lane: schtasks-launched processes inherit BELOW_NORMAL and Windows 11 throttles tray apps (EcoQoS) — the app now parses at normal QoS and returns to power-friendly throttling when idle; graph ∥ modelReport run concurrently and agentUsage no longer gates the first snapshot (both mirror the macOS DashboardModel) |
| 7 | Settings + tray extras | 🔶 in progress — settings store (`%APPDATA%\TokenBar\settings.json`, `tokenbar.*` keys, atomic writes, 7 unit tests), chart stack-by/metric persistence, year filter (header picker + `tokenbar.dashboard.year` + `--year=` flag, with the macOS stale-slice and vanished-year guards). Remaining: tray modes/gauge icons, cat/parrot animation, full context menu, SettingsPanel window, autostart, hotkeys |
| 8 | 3D integration | — |
| 9 | Polish + parity + vendor re-sync | — |
| 10–12 | Releases → Velopack → winget/Scoop | — |

## Credits

Parsing engine vendored from [tokscale](https://github.com/junhoyeo/tokscale)
by junhoyeo. Original menu-bar concept by
[handlecusion's tokcat](https://github.com/handlecusion/tokcat).
