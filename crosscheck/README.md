# Swift ↔ C# fixture cross-check（對拍）

The C# `TokenBar.Core` port was validated by unit tests whose expected values
were written by the porter — a consistent misreading of the Swift source would
pass those tests. This cross-check feeds **the same fixture JSON** to the Swift
`TokenBarCore` (macOS repo, the shipping reference) and the C# port, then diffs
the two outputs field by field. Zero material difference = the port is faithful.

> Baseline: macOS repo commit `2ed256ee` (v1.4.0 + rustls/cdylib backports),
> Windows repo branch `sync-vendor-v140`. Runs entirely on macOS.

## Layout

| Path | What |
|---|---|
| `fixtures/usage-pace.json` | UsagePace compute + runOutRisk cases (exhaustive branch coverage) |
| `fixtures/format.json` | Format string functions + pace durationText (char-exact) |
| `diff.py` | Numeric-aware comparator (see rules below) |
| `swift-out/`, `csharp-out/` | Harness outputs, git-ignored |

Modules deliberately NOT fixture-checked (spot-checked by unit tests only):
Grid, ModelColors, DayBars, UsageStats, TraceCollapse, QuotaResolver,
ClientRegistry. Add a fixture file here if one of them ever diverges in the
field. <!-- ponytail: two exhaustive modules only (the plan's 必做 set); widen per-module when evidence demands -->

## Contract

- **Input encoding = the FFI wire format** (Rust serde camelCase), i.e. the DTO
  JSON both languages already decode in production. No bespoke input mapping.
- **`now`** is RFC3339 with explicit offset. Harnesses parse it with the
  language's standard parser (not the module under test).
- **Timezone-sensitive cases** (`todayKey`/`todayTokens`/`todayCost`): both
  harnesses MUST run with `TZ=Asia/Taipei`. The runner exports it; harnesses
  should assert it and fail fast otherwise. The harness converts `now` to a
  local wall-clock value before calling the module, mirroring production
  callers.
- **Output**: for each fixture file `X.json`, write `X.actual.json` to the
  out dir: `{ "<case name>": <result> }`.
  - UsagePace compute → `null` or `{stage, deltaPercent, expectedUsedPercent,
    actualUsedPercent, etaSeconds, willLastToReset, label, etaText}`.
    `stage` uses the Swift enum-case spelling: `onTrack`, `slightlyAhead`,
    `ahead`, `farAhead`, `slightlyBehind`, `behind`, `farBehind`.
  - `kind: "runOutRisk"` cases → the label string or `null`.
  - format cases → the returned string, or number for
    `todayTokens`/`todayCost`.
- **Comparison rules** (`diff.py`): strings byte-exact; missing key ≡ `null`;
  numbers equal when exactly equal OR |a−b| ≤ max(1e-12, 1e-9·max(|a|,|b|))
  (serializer digit-count differences are not findings; real FP divergence is).

## Running

```bash
# Swift side (macOS repo) — harness lives there, reads fixtures from here
TZ=Asia/Taipei swift run crosscheck-harness \
  ~/side-project/TokenBar-Windows/crosscheck/fixtures \
  ~/side-project/TokenBar-Windows/crosscheck/swift-out

# C# side
TZ=Asia/Taipei dotnet run --project src/TokenBar.CrossCheck -- \
  crosscheck/fixtures crosscheck/csharp-out

# Verdict
python3 crosscheck/diff.py crosscheck/swift-out crosscheck/csharp-out
```

## Fixture schema

`usage-pace.json`:

```json
{ "cases": [ {
    "name": "unique-kebab-name",
    "kind": "compute" | "runOutRisk",
    "mode": "linear" | "historical" | "off",   // compute only
    "now": "2026-07-10T12:00:00Z",             // compute only
    "window": { …UsageWindow wire DTO… },
    "note": "human hint, ignored by harnesses"
} ] }
```

`format.json`:

```json
{ "graph": { …UsagePayload wire DTO, shared by today* cases… },
  "cases": [ {
    "name": "...", "fn": "compactTokens" | "exactTokens" | "usd" | "monthDay"
          | "mmdd" | "relativeTime" | "todayKey" | "todayTokens" | "todayCost"
          | "paceDurationText",
    "arg": <fn-specific scalar>,               // count / amount / iso / seconds / epochSecs
    "now": "RFC3339"                            // relativeTime + today* only
} ] }
```
