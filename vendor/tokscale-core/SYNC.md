# Vendor sync provenance

This repo's `crates/tb_core_ffi/` and `vendor/tokscale-core/` are **copies** from
the macOS repo (`Nanako0129/TokenBar`, local `~/side-project/TokenBar-Native`),
which is the **single sync source** for the shared Rust core. Upstream
(junhoyeo/tokscale) syncs land in the macOS repo first, then get re-copied here.

| Field | Value |
|---|---|
| Source repo | `Nanako0129/TokenBar` (macOS) |
| Copied at commit | `2ed256ee` (= v1.4.0 `fe19eebc` + the two backports below; landed on macOS main 2026-07-16 via ff-merge, SHA preserved) |
| Copied on | 2026-07-15 |
| Upstream milestone state | M6 + M7 + M9 backports merged; adds Grok Build + Hermes clients, cost-provenance contract, client-selection filter for hourly/agents (ctb.h signature change) |
| Cache schema version | 29 (`vendor/tokscale-core/src/message_cache.rs`) |

## Local patches (Windows repo only)

Patches that exist here but NOT yet in the macOS repo. Every local vendor drift
must be recorded here before delivery. Platform-neutral or cfg-gated fixes
should be upstreamed to the macOS sync source before the next copy; otherwise
the listed commits must be deliberately reapplied after syncing.

| Commit | Patch | Files | Upstreamed to macOS repo? |
|---|---|---|---|
| `e520063` | Hermetic Windows test portability: panic-safe env/cwd guards, serial coordination, platform-safe JSON/path fixtures, isolated cache/XDG roots, and non-mutating Windows legacy-path coverage | `src/clients.rs`, `src/lib.rs`, `src/message_cache.rs`, `src/pricing/cache.rs`, `src/scanner.rs`, `src/sessions/claudecode.rs`, `src/sessions/opencode.rs` | No — Windows repo only (2026-07-17) |
| `db2a96a` | Release the temp writer before Windows atomic replacement and reopen the final cache read/write for the durability sync | `src/message_cache.rs` | No — Windows repo only (2026-07-17) |
| `15f418e` | Route pure scanner fixtures through `use_env_roots=false` so host `TOKSCALE_EXTRA_DIRS` and other roots cannot change fixed-count tests | `src/scanner.rs` | No — Windows repo only (2026-07-17) |
| `e807f33` | Gate the Windows Zed known-folder fallback on `use_env_roots` | `src/scanner.rs` | No — Windows repo only (2026-07-17) |
| `0979cdb` | Make Windows `PathRoot::Config` use `%APPDATA%` only for environment-aware scans; explicit-home scans use the supplied home | `src/clients.rs` | No — Windows repo only (2026-07-17) |

Earlier divergences were upstreamed to the macOS repo on 2026-07-15
(branch `fix-vendor-rustls-tls`, the sync source above), so `crates/` +
`vendor/` were byte-identical at copied commit `2ed256ee` before the local
patches listed above:

- reqwest TLS: now 0.13 `rustls` (rustls-platform-verifier — native trust
  semantics on both OSes: Security.framework / SChannel, keeps scoped trust
  + AIA chasing; upgraded from our original `rustls-tls-native-roots` after
  a Codex review finding) — macOS commit `220cf8ab`.
- `crate-type = ["cdylib", "staticlib"]` for tb_core_ffi (P/Invoke needs the
  cdylib; was an unrecorded local divergence since Phase 0) — macOS commit
  `2ed256ee`.

Note: the macOS repo's own local-patch table vs junhoyeo/tokscale lives in
`vendor/README.md` (copied along) — that provenance chain still applies.

## How to re-sync from the macOS repo

```bash
cd ~/side-project/TokenBar-Native && git archive <commit> -- crates vendor \
  | tar -x -C ~/side-project/TokenBar-Windows
# then: restore this SYNC.md, update the table above, re-apply any local
# patches listed here, run scripts/check.sh + full smoke on Windows.
```
