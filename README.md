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
| Shell | `src/TokenBar.App` | WinUI 3, unpackaged (arrives in Phase 4) |

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
| 1 | Rust Windows fixes (HOME→dirs, TLS, antigravity) | — |
| 2 | 3D contribution graph spike | — |
| 3 | TokenBar.Core C# port + cross-check vs Swift | — |
| 4 | Tray skeleton + flyout window | — |
| 5 | Overview lens + polling engine | — |
| 6 | Remaining five lenses | — |
| 7 | Settings + tray extras | — |
| 8 | 3D integration | — |
| 9 | Polish + parity + vendor re-sync | — |
| 10–12 | Releases → Velopack → winget/Scoop | — |

## Credits

Parsing engine vendored from [tokscale](https://github.com/junhoyeo/tokscale)
by junhoyeo. Original menu-bar concept by
[handlecusion's tokcat](https://github.com/handlecusion/tokcat).
