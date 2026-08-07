# Lite deployment channel — distribution policy

Framework-dependent **Lite** packages omit the bundled .NET 10 runtime. Full remains self-contained.

## One default per surface

| Surface | Default | Also available |
| --- | --- | --- |
| README / primary download button | Full | none |
| GitHub Releases asset list | Full listed first | Lite |
| winget | `Nyanako.Syrtis` = Full | `Nyanako.Syrtis.Lite` |
| Scoop | Lite *(intent)* | Full optional |

## Guidance

- Scoop default is **intent only**, not yet decided: Scoop manages `~/scoop/apps/<name>/<version>` with its own `current` junction, while Velopack installs under `%LocalAppData%\<packId>`. Whether Scoop consumes a portable artifact or runs the Velopack installer is still open and determines whether the Lite-default row is implementable.
- Scoop users are more likely to already have .NET 10, so Lite is the useful default *once* that packaging path is chosen.
- On a clean machine, Lite download plus runtime bootstrap may approach the Full total download size.
- Do **not** advertise Lite as an unconditional ~50% saving for every user.
- The .NET runtime bootstrap remains **Velopack's** responsibility.
- Do **not** assume winget `PackageDependencies` installs or enforces the runtime.
- All Full and Lite assets remain reachable even though each surface has one default.

## Channels

| Channel | Mode | Architecture |
| --- | --- | --- |
| `win-x64` | Full | x64 |
| `win-x64-lite` | Lite | x64 |
| `win-arm64` | Full | arm64 |
| `win-arm64-lite` | Lite | arm64 |

Full cannot consume Lite updates and Lite cannot consume Full updates. Near-miss channel names fail closed before network access.

## Package identity

Durable Velopack pack id is the frozen literal `Nyanako.Syrtis` (not derived from `TbProductName`). UpdateFlow.PackageId must match the packaging-script literal.
