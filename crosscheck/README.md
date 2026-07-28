# Swift ↔ C# fixture cross-check（對拍）

The C# `TokenBar.Core` port has ordinary unit tests, but their expected values
can repeat the same source-reading mistake as the port. This cross-check feeds
the **same fixture JSON** to the shipping Swift and C# implementations, writes
normalized JSON, and compares the results field by field.

Zero material difference is the acceptance criterion. Output byte hashes are
diagnostic only because serializer formatting and line endings differ by host.

## Baselines

| Surface | Canonical source |
|---|---|
| Legacy `usage-pace.json` and `format.json` | macOS commit `2ed256ee` |
| Provider quota pace v3 | issue-107 Native baseline `55ca05d3a0a5bf0f02ed20f46bb3e73e65a07218` |

Run the Swift side from a clean archive or worktree at the exact tested
provider-v3 merged SHA. Do not use an unrelated dirty macOS checkout as the
reference.

Canonical fixture fingerprints:

| File | Bytes | SHA-256 |
|---|---:|---|
| `fixtures/provider-quota-pace-v3.json` | 7,625 | `412f6ffd05f23f00266820c243376f265d29024d9e419217e55f8e1559b36c50` |
| `fixtures/usage-pace.json` | 13,532 | `9a83616ea5053b2073dcfbcfb975c923d8597c0c41ddd5877ac0ddd8a9b7806c` |
| `fixtures/format.json` | 7,504 | `408ac7604e1661b2732c05c8e113d62b6d365cd8ea02004933f681cf99ad0913` |

Observed provider-v3 output fingerprints at the reconciliation gate:

| Runner | SHA-256 |
|---|---|
| Swift canonical output | `f5a4ca602f4b31512705549fda9ce1bdd7245ac8038ab629885448851a13af72` |
| C# output on macOS (LF) | `00ccce9e79461b0ab5bafb3a44506b2179c5745b64f3ad83401006f5257931d0` |
| C# output on Windows x64 (CRLF) | `3898cf0c7fb56d4b02e79c085e243ee40b01269b926bf41345626d69b469795a` |

## Layout and coverage

| Path | What |
|---|---|
| `fixtures/usage-pace.json` | 42 legacy UsagePace compute/run-out-risk cases |
| `fixtures/format.json` | 74 Format and pace-duration text cases |
| `fixtures/provider-quota-pace-v3.json` | schema 3 provider lifecycle, pace, selection, legacy, and malformed cases |
| `diff.py` | Numeric-aware semantic comparator |
| `swift-out/`, `csharp-out/` | Harness outputs, git-ignored |

The provider fixture contains one agent, seven windows, and twelve cases:

- 3 pace cases;
- 3 quota-selection cases;
- 1 legacy-window case;
- 5 malformed-window cases.

It exercises production `AgentUsagePayload`/`UsageWindow` decoding,
`UsagePace.compute`/`Compute`, pace presentation, and the selected
`QuotaResolver` card-ID and legacy-label rules. Broader hidden-client,
partial-payload, and tray lifecycle behavior remains covered by unit tests.

Grid, ModelColors, DayBars, UsageStats, TraceCollapse, ClientRegistry, and UI
layout are not fixture-checked here. Add a shared fixture only when a
cross-language value contract exists for one of those surfaces.

## Contract

### Input and decoding

- Input encoding is the Rust FFI wire format: serde camelCase JSON decoded by
  the production DTOs in both languages.
- The provider fixture root and case metadata are typed. Wrong JSON types,
  missing required fields, or a `null` case element fail the whole fixture;
  they must not degrade into a case-level missing value.
- Embedded `rawWindow` strings are decoded through the production
  `UsageWindow` decoder. Expected malformed-window rejection catches only the
  language's JSON decode error; unrelated runtime failures remain visible.
- Provider pace cases use production pace computation/presentation. Historical
  projection values are not recomputed in either harness.
- Selection cases use production `QuotaResolver`, including exact `cardId`,
  unique legacy-label migration, and ambiguous-label retention.

### CLI selectors and timezone

The optional selector is one of:

- `format`;
- `usage-pace`;
- `provider-quota-pace-v3`.

Omitting it means `all`, which intentionally runs only the legacy
`usage-pace` and `format` fixtures. Provider v3 must be selected explicitly.

Timezone validation happens before arity validation, matching the canonical
Swift CLI:

- wrong timezone: exit `1`;
- bad arity or unknown selector: exit `2`;
- valid run: exit `0`.

Timezone-sensitive cases run under `Asia/Taipei`. The Windows runner also
accepts the native `Taipei Standard Time` identifier.

### Harness `now`

`now` is an RFC3339 timestamp with uppercase `T` and an explicit `Z`/`z` or
`±HH:mm` offset. The canonical practical fraction range is zero through eight
digits, matching the source fixture and production precision tests.

Foundation keeps millisecond precision throughout that covered range. Beyond
eight digits, `ISO8601DateFormatter` has content-dependent, undocumented
parsing quirks; those values are deliberately outside this cross-check
contract rather than reimplemented in C#.

The C# harness applies valid two-digit offsets manually before constructing a
UTC `DateTimeOffset`, because Foundation accepts offsets such as `+14:30` and
`+23:59` that exceed `DateTimeOffset`'s representable `±14:00` offset range.

### Normalized output

For each fixture `X.json`, the runner writes `X.actual.json`.

Legacy `usage-pace` output is:

```text
{ "<case name>": null | {
    stage, deltaPercent, expectedUsedPercent, actualUsedPercent,
    etaSeconds, willLastToReset, label, etaText
  } | { rejected: true } }
```

Provider-v3 output has three top-level keys:

```text
{
  schemaVersion,
  lifecycle: [
    { clientId, cardId, label, state, reason, durationSeconds,
      durationSource, completeCycles, hasHistorical }
  ],
  cases: {
    "<case name>": <pace | selection | legacy | malformed result>
  }
}
```

Pace results normalize:

```text
basis, stage, deltaPercent, expectedUsedPercent, actualUsedPercent,
etaSeconds, willLastToReset, label, etaText, riskText,
isHistoricalDeficit
```

`diff.py` reports the provider result as **3 cases** because it counts the
three root keys (`schemaVersion`, `lifecycle`, and `cases`), not the twelve
fixture cases.

### Comparison rules

- strings are byte-exact;
- a missing key is equivalent to `null`;
- numbers are equal when exactly equal or when
  `|a-b| ≤ max(1e-12, 1e-9 × max(|a|, |b|))`.

The numeric tolerance handles serializer/FP representation noise, not semantic
projection differences.

## Running

Set `TOKENBAR_MAC_CANONICAL` to a clean archive or worktree at the issue-107
Native baseline `55ca05d3a0a5bf0f02ed20f46bb3e73e65a07218`.

`Package.swift` links `target/release/libtb_core_ffi.a` by a path relative to
the canonical macOS repo root. Build the Rust static library first, and run the
Swift harness from that same root; otherwise the linker can fail or pick up an
unrelated checkout's `target/` artifact.

```bash
export TOKENBAR_WINDOWS="$(pwd)"
export TOKENBAR_MAC_CANONICAL="${TMPDIR:-/tmp}/tokenbar-mac-55ca05d3"

(
  cd "$TOKENBAR_MAC_CANONICAL"
  cargo build --release
)
```

### Provider quota pace v3

```bash
rm -rf crosscheck/swift-out crosscheck/csharp-out
mkdir -p crosscheck/swift-out crosscheck/csharp-out

(
  cd "$TOKENBAR_MAC_CANONICAL"
  TZ=Asia/Taipei swift run crosscheck-harness \
    "$TOKENBAR_WINDOWS/crosscheck/fixtures" \
    "$TOKENBAR_WINDOWS/crosscheck/swift-out" \
    provider-quota-pace-v3
)

TZ=Asia/Taipei dotnet run \
  --project src/TokenBar.CrossCheck \
  -c Release -- \
  crosscheck/fixtures \
  crosscheck/csharp-out \
  provider-quota-pace-v3

python3 crosscheck/diff.py crosscheck/swift-out crosscheck/csharp-out
```

Expected semantic verdict:

```text
CROSSCHECK OK — 3 case(s), zero material difference
```

### Legacy default run

```bash
rm -rf crosscheck/swift-out crosscheck/csharp-out
mkdir -p crosscheck/swift-out crosscheck/csharp-out

(
  cd "$TOKENBAR_MAC_CANONICAL"
  TZ=Asia/Taipei swift run crosscheck-harness \
    "$TOKENBAR_WINDOWS/crosscheck/fixtures" \
    "$TOKENBAR_WINDOWS/crosscheck/swift-out"
)

TZ=Asia/Taipei dotnet run \
  --project src/TokenBar.CrossCheck \
  -c Release -- \
  crosscheck/fixtures \
  crosscheck/csharp-out

python3 crosscheck/diff.py crosscheck/swift-out crosscheck/csharp-out
```

Expected semantic verdict:

```text
usage-pace.actual.json: 42 cases
format.actual.json: 74 cases
CROSSCHECK OK — 116 case(s), zero material difference
```

## Legacy UsagePace result after the v3 cutover

`usage-pace.json` remains byte-identical to its legacy baseline. Its 42 cases
now intentionally normalize as follows in both final Swift and C#:

- 40 legacy windows decode with internal `legacyMissing` pace state and produce
  `null`; the removed top-level historical scalar no longer triggers a silent
  Historical → Linear fallback;
- 2 invalid percentage/clamp windows are rejected by the production v3
  decoder and produce `{ "rejected": true }`.

This is an intended contract transition, not an untracked fixture rewrite.
`format.json` still contributes 74 zero-difference cases, so the default run
remains a 116-case semantic gate.
