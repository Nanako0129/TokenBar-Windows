# Vendored tok-scale sync

| Field | Value |
|---|---|
| Source repo | [Nanako0129/TokenBar](https://github.com/Nanako0129/TokenBar) |
| Copied commit | [`55ca05d3a0a5bf0f02ed20f46bb3e73e65a07218`](https://github.com/Nanako0129/TokenBar/commit/55ca05d3a0a5bf0f02ed20f46bb3e73e65a07218) |
| Copied on | 2026-07-28 |

`55ca05d3a0a5bf0f02ed20f46bb3e73e65a07218` is the final rebase-merged Native
PR #111 commit on `main`. It preserves the post-merge M19-B checkpoint and adds
the issue-107 source-generation-aware filter-parity diagnostic: one Rust-owned
probe uses a fresh graph and brackets hourly and Agents nil/full reports with
opaque local-source tokens. Source movement is classified as `sourceChanged`
instead of a filter mismatch; independently refreshed cost remains diagnostic
but cannot decide parity. A hermetic vendor fixture covers exact client gates,
synthetic traffic, duplicate Codebuff roots, unattributed `Main`, cold/warm
cache, and inherited scanner-root isolation.

The active cache is format 2 at `source-message-cache-v2`; format-1 shards are
stale and rebuild cold under format 2. The legacy schema-32 monolith
`source-message-cache.bin` remains inert, unread, unmodified, and undeleted.

The Windows shared-tree local patch table is **none**. The Native-only
`vendor/AGENTS.md` repository adapter is intentionally excluded because this
repository keeps local agent guides untracked; it is not runtime source. Every
tracked runtime vendor file is byte-identical to Native except this Windows-only
`vendor/tokscale-core/SYNC.md` provenance record. The Rust serializer-lock
fixture at `Fixtures/CrossCheck/provider-quota-pace-v3.json` and the Windows
cross-check copy are byte-identical to the same Native fixture.

## Verification

The macOS-side downstream gate passed against this exact sync:

- `crates/` is byte-identical to Native `55ca05d3`; `vendor/` is
  byte-identical after excluding Native-only `vendor/AGENTS.md` and this
  Windows-only `SYNC.md`; the C header and both provider-v3 fixture copies also
  match byte-for-byte.
- `Cargo.lock` is unchanged. The two new Rust fixture/source files pass a
  focused `rustfmt --check`; the exact shared tree was not reformatted.
- Focused parity tests pass (`tb_core_ffi` 10/10 and vendor fixture 1/1).
- `scripts/check.sh` passes with locked dependencies: workspace check, all Rust
  tests (including 319 FFI and 1,290 vendor unit tests), release build, locked
  .NET restore, solution build with zero warnings/errors, all 287 Core tests,
  and the 11-entry P/Invoke smoke.
- The live smoke observed source movement while local sessions were changing,
  so both parity reports correctly returned `sourceChanged` and skipped
  comparison. Credential/network-backed agent usage was intentionally disabled
  with `TB_SMOKE_SKIP_NETWORK=1`; pricing was cache-only.

This is local macOS-side evidence. Hosted Windows x64 and ARM64 cross-package
checks remain CI-owned, and this record does not claim real ARM64 runtime
validation for this sync.

## Sync procedure

Run from a clean Windows checkout with `TOKENBAR_NATIVE` pointing to a clean
Native checkout. Preserve this Windows-only file across the exact replacement,
then update its commit, date, and verification fields before committing:

```bash
: "${TOKENBAR_NATIVE:?set TOKENBAR_NATIVE to a clean Native checkout}"
source_commit=55ca05d3a0a5bf0f02ed20f46bb3e73e65a07218
stage="$(mktemp -d)"
sync_record="$(mktemp)"
trap 'rm -rf "$stage"; rm -f "$sync_record"' EXIT

cp vendor/tokscale-core/SYNC.md "$sync_record"
git -C "$TOKENBAR_NATIVE" archive \
  "$source_commit" crates vendor Sources/CTB/include/ctb.h \
  Fixtures/CrossCheck/provider-quota-pace-v3.json \
  | tar -x -C "$stage"

rm -rf crates vendor
cp -a "$stage/crates" crates
cp -a "$stage/vendor" vendor
rm -f vendor/AGENTS.md
cp "$stage/Sources/CTB/include/ctb.h" include/ctb.h
mkdir -p Fixtures/CrossCheck
cp "$stage/Fixtures/CrossCheck/provider-quota-pace-v3.json" \
  Fixtures/CrossCheck/provider-quota-pace-v3.json
cp "$stage/Fixtures/CrossCheck/provider-quota-pace-v3.json" \
  crosscheck/fixtures/provider-quota-pace-v3.json
cp "$sync_record" vendor/tokscale-core/SYNC.md
```

Do not write private local paths or credentials. This record does not claim that
real ARM64 runtime validation is complete.
