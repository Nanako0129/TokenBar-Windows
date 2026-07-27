# Vendored tok-scale sync

| Field | Value |
|---|---|
| Source repo | [Nanako0129/TokenBar](https://github.com/Nanako0129/TokenBar) |
| Copied commit | [`4dfed5ffab26e2707a8cd82ee99864520b75892b`](https://github.com/Nanako0129/TokenBar/commit/4dfed5ffab26e2707a8cd82ee99864520b75892b) |
| Copied on | 2026-07-27 |

`4dfed5ffab26e2707a8cd82ee99864520b75892b` is the final merged Native PR
#102 commit on `main`. In addition to the Antigravity discovery, format-2 writer
lifecycle, malformed Copilot Desktop entry, and fixture portability
corrections, this source retries Codex credential replacement, merges duplicate
Copilot span endpoints, advances only the Copilot parser identity to 4, and
makes shipping cache fixtures build fingerprints, seed, and query with the
scanner-returned path spelling. The post-review merge also preserves valid
Copilot quota rows when the optional reset field has a non-string type and
emits DNS/TLS transport categories from typed error sources without exposing
free-form error text. This is the canonical post-merge M19-B checkpoint.

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

## Sync procedure

Run from a clean Windows checkout with `TOKENBAR_NATIVE` pointing to a clean
Native checkout. Preserve this Windows-only file across the exact replacement,
then update its commit, date, and verification fields before committing:

```bash
: "${TOKENBAR_NATIVE:?set TOKENBAR_NATIVE to a clean Native checkout}"
source_commit=4dfed5ffab26e2707a8cd82ee99864520b75892b
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
