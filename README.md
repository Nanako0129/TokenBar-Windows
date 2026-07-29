# TokenBar for Windows

Windows port of [TokenBar](https://github.com/Nanako0129/TokenBar) — the
menu-bar/tray AI coding-agent token-usage monitor. Same Rust parsing core,
a native WinUI 3 shell.

> **Status: v0.1.0 stable release candidate.** Phase 10 is the current unsigned
> portable release transaction; stable publication is not claimed. See the
> [`release contract`](docs/release.md) and the published
> [`v0.1.0-preview.1` prerelease](https://github.com/Nanako0129/TokenBar-Windows/releases/tag/v0.1.0-preview.1).

## Architecture

| Layer | Path | Notes |
|---|---|---|
| Rust core | `crates/tb_core_ffi` + `vendor/tokscale-core` | Public shared engine pinned as a Git submodule; consumer provenance is recorded in `vendor/ENGINE.md`. C ABI, JSON envelope, built as `cdylib` for P/Invoke |
| C ABI contract | `include/ctb.h` | 11 entry points, `{"ok":true,"data":…}` / `{"ok":false,"err":…}` |
| Interop | `src/TokenBar.Interop` | `net10.0`, platform-neutral — P/Invoke facade + envelope decode |
| Logic | `src/TokenBar.Core` | `net10.0`, platform-neutral — C# port of the macOS `TokenBarCore` |
| Shell | `src/TokenBar.App` | WinUI 3, unpackaged. Windows-only build (not in the slnx): `dotnet build src/TokenBar.App/TokenBar.App.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64` |
| Windows native packaging | `src/Directory.Build.targets` | The sole `Platform`/`RuntimeIdentifier` → explicit Rust-target mapping. `BuildTbNative` produces the target-specific `tb_core_ffi.dll`; missing/conflicting tuples, wrong PE machines, and stale output/publish bytes fail fast. |

Windows native packaging is opt-in for `TokenBar.App`, `TokenBar.Smoke`, and
`TokenBar.Core.Tests`. The supported tuples are `x64`/`win-x64` →
`x86_64-pc-windows-msvc` and `ARM64`/`win-arm64` →
`aarch64-pc-windows-msvc`; Windows managed builds never select a default
`target/release` DLL.

## Build

```bash
git submodule update --init --recursive

# macOS (inner loop — no Windows needed)
scripts/check.sh

# Windows x64: restore the locked graph, build the explicit native source, then run the x64 gates
dotnet restore src/TokenBar.slnx --locked-mode
dotnet restore src/TokenBar.App/TokenBar.App.csproj --locked-mode
.\scripts\dev.ps1
dotnet msbuild src/TokenBar.Smoke/TokenBar.Smoke.csproj -t:BuildTbNative -p:Configuration=Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:TbNativeCargoLocked=true -p:RestoreLockedMode=true
dotnet build src/TokenBar.Core.Tests/TokenBar.Core.Tests.csproj -c Release -p:Platform=x64 --no-restore
dotnet test src/TokenBar.Core.Tests/TokenBar.Core.Tests.csproj -c Release -p:Platform=x64 --no-build --no-restore
dotnet build src/TokenBar.App/TokenBar.App.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 --no-restore
dotnet build src/TokenBar.Smoke/TokenBar.Smoke.csproj -c Release -p:Platform=x64 --no-restore
dotnet run --project src/TokenBar.Smoke -c Release -p:Platform=x64 --no-build --no-restore
dotnet publish src/TokenBar.Smoke/TokenBar.Smoke.csproj -c Release -r win-x64 --self-contained -o out/smoke-win-x64 --no-restore
```

ARM64 is cross-build/package validation only on an x64 runner:

```bash
dotnet restore src/TokenBar.App/TokenBar.App.csproj --locked-mode
dotnet restore src/TokenBar.Smoke/TokenBar.Smoke.csproj --locked-mode
dotnet msbuild src/TokenBar.Smoke/TokenBar.Smoke.csproj -t:BuildTbNative -p:Configuration=Release -p:Platform=ARM64 -p:RuntimeIdentifier=win-arm64 -p:TbNativeCargoLocked=true -p:RestoreLockedMode=true
dotnet build src/TokenBar.App/TokenBar.App.csproj -c Release -p:Platform=ARM64 -p:RuntimeIdentifier=win-arm64 --no-restore
dotnet publish src/TokenBar.Smoke/TokenBar.Smoke.csproj -c Release -r win-arm64 --self-contained -o out/smoke-win-arm64 --no-restore
```

The unsigned portable App package command performs the locked restore, native
build, publish, ZIP creation, and structure/version/PE/hash checks. Use a clean
Git checkout and a new empty output root on a Windows host or CI runner:

```powershell
.\scripts\build-app-artifact.ps1 -Rid win-x64 -OutputRoot "$env:RUNNER_TEMP\tokenbar-phase10-x64"
.\scripts\build-app-artifact.ps1 -Rid win-arm64 -OutputRoot "$env:RUNNER_TEMP\tokenbar-phase10-arm64"
```

The cross-language CrossCheck stays platform-neutral and has no Platform/RID:

```bash
TZ=Asia/Taipei dotnet run \
  --project src/TokenBar.CrossCheck \
  -c Release -- \
  crosscheck/fixtures \
  crosscheck/csharp-out
```

CI publishes only short-retention Smoke test-harness artifacts and sanitized
Phase 10 App evidence/checksums. App ZIP/EXE/DLL files are never uploaded. The
hosted runners perform structure, version, PE, and hash checks; they do not
claim an interactive WinUI startup gate.

The M19-B1 real-ARM64 result is historical evidence from a separate Windows
ARM64 gate on 2026-07-27: 351 Rust tests, 12 provider-v3 CrossCheck cases, PE
checks, and synthetic WinUI startup were recorded in [the public issue
comment](https://github.com/Nanako0129/TokenBar/issues/45#issuecomment-5091092629).
That result does not satisfy or replace the Phase 10 published-artifact x64 and
ARM64 gates. The published `v0.1.0-preview.1` x64/ARM64 startup-smoke used the
active `Nanako` profile with outbound blocked under explicit approval; it was a
non-disposable exception, not disposable isolation. For stable, the default is
a disposable Windows VM/account with production credentials absent and outbound
blocked; a non-disposable exception needs separate explicit authorization, must
be labelled, and must never be called isolated. The current `v0.1.0` stable
transaction is explicitly authorized to use the active `Nanako` profile with
outbound blocked under those non-disposable terms.

Prereqs: Rust `1.96.1`, .NET SDK `10.0.301`, PowerShell `7.0+`; on Windows the
MSVC toolchain.

Windows CI runs the x64 Rust workspace tests, Core.Tests, WinUI App, and Smoke
from their project roots (the solution has no x64 configuration), then executes
both the all-entry-point and strict relocated-`CODEX_HOME` Smoke checks with
network and host-profile isolation. It runs the no-Platform/RID CrossCheck,
publishes and executes the self-contained `smoke-win-x64` bundle, and uploads
that test-harness artifact. The `arm64-cross` job only cross-builds and packages
the native DLL and Smoke bundle, and uploads the test-harness artifact plus
sanitized Phase 10 evidence/checksums; it is not an ARM64 runtime test. The provider-pace branch
passed local command-equivalent x64 and ARM64 gates plus fresh review on
2026-07-19; GitHub PR #3 preserves its remote CI and review record. `cargo fmt`
is not currently a repository CI gate; its existing workspace-wide formatting
debt is tracked separately.

## Progress

| Phase | Scope | Status |
|---|---|---|
| 0 | Repo bootstrap + P/Invoke smoke | ✅ 2026-07-02 — C# ↔ Rust cdylib seam verified on macOS (`tb_probe` → 84k messages), CI on windows-latest |
| 1 | Rust Windows fixes (HOME→dirs, TLS, antigravity) | ✅ 2026-07-02 — all 10 entry points verified on a real x64 Windows box against real session data (271 msgs parsed, pricing fetched over rustls, quota windows decoded) |
| 2 | 3D contribution graph spike | ✅ 2026-07-02 — GO (Vortice/D3D11 instancing verified on real hardware, ~0.2ms/frame; the product SwapChainPanel lifecycle was completed in Phase 8). See `spike/RESULTS.md` |
| 3 | TokenBar.Core C# port + cross-check vs Swift | ✅ 2026-07-19 — all modules ported (incl. the v1.4.0 delta and provider pace v3), 270 unit tests green; **fixture cross-check vs Swift done** (`crosscheck/`: 116 legacy cases plus 12 provider-v3 cases, zero material difference; the original Format pass caught 4 real printf-rounding divergences — pre-round deleted, .NET Core F-formats are IEEE-correct) |
| 4 | Tray skeleton + flyout window | ✅ 2026-07-02 — tray icon + Open/Quit menu, borderless rounded Acrylic flyout (translucent while unfocused, topmost), PerMonitorV2 DPI, show/hide slide, single instance, taskbar-edge placement, polling engine. SwapChainPanel lifecycle was completed in Phase 8; compositor-native animation remains in the polish backlog |
| 5 | Overview lens + polling engine | ✅ 2026-07-02 — five cards (stacked chart + wrap legend, agent limits with live pace markers, trace, models, streaks), instant styled hover tooltips, WH_MOUSE_LL wheel path |
| 6 | Remaining five lenses | ✅ 2026-07-02 — lens router with 160ms crossfade transitions; Models (full list + pricing hint), Daily (tap drill-down), Hourly (Timeline/Profile + show-more), Stats, Agents; lazy report loading. Verified by the user against the full synced history (5.6B tokens / 70 days). Cold first paint 11.1s → **3.8s** (warm 3.2s) after the EcoQoS/priority fix + mac-parity slow lane: schtasks-launched processes inherit BELOW_NORMAL and Windows 11 throttles tray apps (EcoQoS) — the app now parses at normal QoS and returns to power-friendly throttling when idle; graph ∥ modelReport run concurrently and agentUsage no longer gates the first snapshot (both mirror the macOS DashboardModel) |
| 7 | Settings + tray extras | 🔶 feature-complete (macOS parity) — settings store (`%APPDATA%\TokenBar\settings.json`, atomic, unit-tested) with the year filter, chart persistence, manual-refresh spinner; tray: seven modes with the value drawn into the icon (tooltip carries the full string), bars/ring/popsicle gauges (macOS geometry verbatim), cat/parrot animation (HICON-cached, ~0.5% of a core at idle), full context menu with live quota sources; Mica settings window (ten sections, live keys, autostart via HKCU Run honoring StartupApproved); flyout footer gear+Quit; in-flyout Ctrl-shortcut set. Global `RegisterHotKey` dropped: the macOS reference ships no global shortcut, so it's not a parity gap (parked as an optional Windows-only nicety in Phase 9). Verification: icon gallery + live tray screenshots + synthesized input on the x64 box; the non-quota Settings flow passed the 125% DPI interactive gate on 2026-07-17, including live 520→600 DIP flyout resizing, persistence, singleton hide/reopen, autostart restoration, and the 48ms entrance-animation race. The separate 150% pass also passed on 2026-07-17 in an isolated RDP session: both windows reported 144 DPI, 520→600 DIP mapped immediately to 780→900 physical px, and the persisted height survived a process restart. A 33-active-day synthetic fixture supported user-checked 3D hover/orbit/zoom/Fit/Reset, and the Flyout Acrylic was subsequently verified with loaded 3D content in both light and dark themes at 200% DPI |
| 8 | 3D integration | ✅ 2026-07-17 — product card renders the real contribution grid with macOS-parity colors/lighting (sRGB-correct opaque faces), 4× MSAA, render-on-demand orbit/pan/zoom, persisted `tokenbar.orbit.v1`, Fit/Reset, custom ray-picked tooltip, and a persisted 2D/3D toggle. Real x64 checks include corrected pointer/DPI alignment, 2D↔3D in 6.7–20.1ms, a 241-frame drag trace, idle no-present, the 50-cycle lifecycle gate, and a retained 60-minute soak: 8230 cycles, `created=8230 released=8230 removed=0 errors=0`, 3600.7s elapsed, with private-memory and handle thresholds passing. Fresh review confirmed the lifecycle and cleanup result |
| 9 | Polish + parity + shared-core sync | ⏸️ **Paused.** Shared-engine consumer migration pins the reviewed public `tokscale-core` commit used by Native; non-quota client tabs remain complete (2026-07-17). **Provider pace v3 reconciliation completed 2026-07-19** against the exact macOS `1e00e7b` tree: Codex, Claude, Grok, Antigravity, and Copilot recurring percentage cards use stable `cardId`, opaque account scope, exact/observed duration, typed lifecycle states, and backend-owned coherent history; Windows adds CNG-backed installation identity, protected DACLs, reparse/file-ID checks, locking, capacity bounds, and atomic replacement. Strict C# decoding, `clientId|cardId` selection retention/migration, shared Dashboard/Settings row semantics, responsive Full layout, Classic/Off suppression, and typed learning/unavailable previews are active. Verification includes 275 .NET tests; the latest Windows x64 `tb_core_ffi` release suite with 300 tests; macOS and Windows x64 workspace/App/synthetic-smoke gates; ARM64 release build, 15 native security/history tests, 12 provider cases, and WinUI startup; Swift↔C# zero-difference checks across 12 provider-v3 and 116 legacy cases; light/dark responsive UI checks at 100%, 150%, and 200% DPI; sanitized production-profile preservation; and fresh focused/end-to-end verifiers. Windows runtime follow-ups resolve provider homes without `HOME`, hide and cache the Claude version probe, and discover/probe every Antigravity language server without visible console windows. Provider compatibility closure on 2026-07-20 adds Windows Antigravity OAuth-client artifact discovery, proves the installed scanner against Antigravity 2.3.1, accepts Grok's unified-billing schema as a separate non-recurring financial cap, and completes sanitized Codex/Grok/Antigravity live gates. Final review fixes unify Antigravity local/remote history scope through verified Google Email and bind the tray last-good gauge to its effective selection. This completes the pace contract only; broader quota-source ordering/visibility and new Agent-limits feature scope remain unopened · backlog: demo mode; optional Windows-only global hotkey to toggle the flyout (no macOS equivalent — needs a key-binding UI) |
| 10 | v0.1.0 stable portable release transaction | 🔶 Current release-candidate state: unsigned portable `v0.1.0` contract prepared; stable publication/tag/assets are not claimed. See [`docs/release.md`](docs/release.md) and the published [`v0.1.0-preview.1` prerelease](https://github.com/Nanako0129/TokenBar-Windows/releases/tag/v0.1.0-preview.1) history. |
| 11 | Velopack/signing/installer/updater | ⏭️ Next priority; unopened. |
| 12 | winget/Scoop | — Unopened. |

### Provider runtime validation

| Provider | Windows live status |
|---|---|
| Claude | Exact-head live Smoke passed from the signed-in Windows profile with source `oauth` and 2 quota cards. |
| Copilot | Exact-head live Smoke passed from the signed-in Windows profile with source `oauth` and 2 quota cards. |
| Antigravity | Exact-head local IDE path passed with `HOME` absent and source `cli`: 8 quota cards with no provider error. Windows 2.3.1 installed-client discovery also paired the expected `resources/bin/language_server.exe` artifact without exposing or persisting embedded client values. Remote OAuth live coverage remains unavailable because this profile has no `~/.gemini/oauth_creds.json`; that path remains covered hermetically. |
| Codex | Logout/login restored the Windows credential; exact-head live Smoke passed with source `oauth` and 2 quota cards. |
| Grok | Exact-head live Smoke accepted the unified-billing response with source `oauth`. The account currently produces 0 active quota windows, replacing the former `response_shape` failure with a successful recognized-disabled result; raw billing values were not logged. |

Fixture cross-checks, synthetic session smokes, and path-only checks are kept
separate from credential-bound live coverage. The final live gate retained the
same exists/hash state across all 7 monitored credential and account-scope
paths; normal secure v3 pace-history writes remain permitted. Session-parser
environment-root overrides now flow through one shared FFI source context and
have strict relocated-`CODEX_HOME` coverage. RID-aware native-DLL selection and
freshness are complete through the shared `BuildTbNative`/`Directory.Build.targets`
path.

## Credits

Shared parsing engine from [tokscale-core](https://github.com/Nanako0129/tokscale-core),
originally derived from [tokscale](https://github.com/junhoyeo/tokscale) by
junhoyeo. Original menu-bar concept by
[handlecusion's tokcat](https://github.com/handlecusion/tokcat).
