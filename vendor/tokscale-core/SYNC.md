# Vendored tok-scale sync

| Field | Value |
|---|---|
| Source repo | [Nanako0129/TokenBar](https://github.com/Nanako0129/TokenBar) |
| Copied commit | [`729dc3adf21cc31e16ef0b8b742f0244197d7058`](https://github.com/Nanako0129/TokenBar/commit/729dc3adf21cc31e16ef0b8b742f0244197d7058) |
| Copied on | 2026-07-28 |

`729dc3adf21cc31e16ef0b8b742f0244197d7058` is the final rebase-merged Native
PR #113 commit on `main`. It keeps the Copilot Desktop mtime helper import on
its existing Unix-only test and keeps four Kiro globalStorage fixture helpers
on their existing macOS-only tests. Windows Rust 1.96.1 strict Clippy therefore
does not compile unused test declarations. Test bodies, parsing, cache schema,
C ABI, and runtime output are unchanged.

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

- `crates/` is byte-identical to Native `729dc3ad`; `vendor/` is
  byte-identical after excluding Native-only `vendor/AGENTS.md` and this
  Windows-only `SYNC.md`; the C header and both provider-v3 fixture copies also
  match byte-for-byte.
- `Cargo.lock` is unchanged, `git diff --check` passes, and the exact shared
  tree was not reformatted.
- Rust 1.96.1 strict Clippy passes for `tokscale-core`. Before this exact sync,
  the same cfg repair also passed native Windows x64 strict Clippy with the
  standalone candidate lock unchanged.
- `scripts/check.sh` passes with locked dependencies: workspace check, all Rust
  tests (including 319 FFI and 1,290 vendor unit tests), release build, locked
  .NET restore, solution build with zero warnings/errors, all 287 Core tests,
  and the 11-entry P/Invoke smoke.
- The live smoke observed source movement while local sessions were changing,
  so both parity reports correctly returned `sourceChanged` and skipped
  comparison. The agent-usage probe also completed successfully.

This is local macOS-side evidence. Hosted Windows x64 and ARM64 cross-package
checks remain CI-owned, and this record does not claim real ARM64 runtime
validation for this sync.

## Sync procedure

Run from a clean Windows checkout with `TOKENBAR_NATIVE` pointing to a clean
Native checkout. Preserve this Windows-only file across the exact replacement,
then update its commit, date, and verification fields before committing:

```bash
: "${TOKENBAR_NATIVE:?set TOKENBAR_NATIVE to a clean Native checkout}"
source_commit=729dc3adf21cc31e16ef0b8b742f0244197d7058
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
