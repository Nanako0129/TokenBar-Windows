# Vendor sync provenance

This repo's `crates/tb_core_ffi/` and `vendor/tokscale-core/` are **copies** from
the macOS repo (`Nanako0129/TokenBar`, local `~/side-project/TokenBar-Native`),
which is the **single sync source** for the shared Rust core. Upstream
(junhoyeo/tokscale) syncs land in the macOS repo first, then get re-copied here.

| Field | Value |
|---|---|
| Source repo | `Nanako0129/TokenBar` (macOS) |
| Copied at commit | `2ed256ee` (branch `fix-vendor-rustls-tls` = v1.4.0 main `fe19eebc` + our two backports below; update this SHA if that branch lands squashed) |
| Copied on | 2026-07-15 |
| Upstream milestone state | M6 + M7 + M9 backports merged; adds Grok Build + Hermes clients, cost-provenance contract, client-selection filter for hourly/agents (ctb.h signature change) |
| Cache schema version | 29 (`vendor/tokscale-core/src/message_cache.rs`) |

## Local patches (Windows repo only)

Patches that exist here but NOT yet in the macOS repo. Policy: keep this table
empty — platform-neutral or cfg-gated fixes should be PR'd back to the macOS
repo first, so future syncs are a plain rsync with zero reapply.

| Patch | Files | Upstreamed to macOS repo? |
|---|---|---|
| *(none — table intentionally empty)* | | |

Both former divergences were upstreamed to the macOS repo on 2026-07-15
(branch `fix-vendor-rustls-tls`, the sync source above), so `crates/` +
`vendor/` are now byte-identical copies:

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
