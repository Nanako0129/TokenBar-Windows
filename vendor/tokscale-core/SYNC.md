# Vendor sync provenance

This repo's `crates/tb_core_ffi/` and `vendor/tokscale-core/` are **copies** from
the macOS repo (`Nanako0129/TokenBar`, local `~/side-project/TokenBar-Native`),
which is the **single sync source** for the shared Rust core. Upstream
(junhoyeo/tokscale) syncs land in the macOS repo first, then get re-copied here.

| Field | Value |
|---|---|
| Source repo | `Nanako0129/TokenBar` (macOS) |
| Copied at commit | `04ba9c1f416aa9879ab9a6ee506cd5bb5e790660` |
| Copied on | 2026-07-02 |
| Upstream milestone state | M5a + M5b merged (see macOS repo's sync plan); M6/M7 pending |
| Cache schema version | 21 (`vendor/tokscale-core/src/message_cache.rs`) |

## Local patches (Windows repo only)

Patches that exist here but NOT yet in the macOS repo. Policy: keep this table
empty — platform-neutral or cfg-gated fixes should be PR'd back to the macOS
repo first, so future syncs are a plain rsync with zero reapply.

| Patch | Files | Upstreamed to macOS repo? |
|---|---|---|
| reqwest TLS: `native-tls-vendored` → `rustls-tls-native-roots` (drops vendored OpenSSL — Perl+NASM toolchain dependency and the slowest unit of a clean build; CI runners ship those tools, local Windows boxes often don't. Native roots keep installed OS trust-store CAs incl. corporate MITM roots; note rustls does no SChannel-style AIA chasing / AuthRoot auto-fetch, so servers with incomplete chains fail where SChannel would recover) | `vendor/tokscale-core/Cargo.toml`, `Cargo.lock` | ❌ pending — PR to TokenBar-Native planned (Phase 1 item 5) |

Note: the macOS repo's own local-patch table vs junhoyeo/tokscale lives in
`vendor/README.md` (copied along) — that provenance chain still applies.

## How to re-sync from the macOS repo

```bash
cd ~/side-project/TokenBar-Native && git archive <commit> -- crates vendor \
  | tar -x -C ~/side-project/TokenBar-Windows
# then: restore this SYNC.md, update the table above, re-apply any local
# patches listed here, run scripts/check.sh + full smoke on Windows.
```
