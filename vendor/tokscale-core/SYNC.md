# Vendored tok-scale sync

| Field | Value |
|---|---|
| Source repo | [Nanako0129/TokenBar](https://github.com/Nanako0129/TokenBar) |
| Copied commit | [`f820b06fd99a53cada8495338bd7d58898525a7b`](https://github.com/Nanako0129/TokenBar/commit/f820b06fd99a53cada8495338bd7d58898525a7b) |
| Copied on | 2026-07-26 |

`f820b06fd99a53cada8495338bd7d58898525a7b` is the M19-B0 / PR #99 merged
secure-storage canonical source. The active cache is format 2 at
`source-message-cache-v2`; format-1 shards are stale and rebuild cold under
format 2. The legacy schema-32 `source-message-cache.bin` remains unread,
unmodified, and undeleted.

The Windows shared-tree local patch table is **none**. The former 11 commits
from `e5200634` through `aec5bd88` are marked recovered in the Native vendor
ledger and must not be re-applied here. The Rust serializer-lock fixture at
`Fixtures/CrossCheck/provider-quota-pace-v3.json` and the Windows cross-check
copy are byte-identical to the same Native fixture.

## Sync procedure

Run from a clean Windows checkout with `TOKENBAR_NATIVE` pointing to a clean
Native checkout. Preserve this Windows-only file across the exact replacement,
then update its commit, date, and verification fields before committing:

```bash
: "${TOKENBAR_NATIVE:?set TOKENBAR_NATIVE to a clean Native checkout}"
source_commit=f820b06fd99a53cada8495338bd7d58898525a7b
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
