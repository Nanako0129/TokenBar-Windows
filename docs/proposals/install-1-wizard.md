# INSTALL-1 — GUI install wizard (per-user)

Status: approved 2026-08-28. Owner: main session.
Reviewed by four independent fresh plan reviews; five defects were caught
before any code existed, three of them factual errors in earlier drafts of
this document. The revision history at the end records them, because two were
the same mistake made twice.

## 1. Problem

What a user double-clicks today is Velopack's own `Setup.exe`. It shows a bare
progress splash: no welcome, no choice of location, no Back/Next/Cancel, no
statement of what is about to happen or where. It installs to
`%LocalAppData%\Nyanako.Syrtis` and there is no way to say otherwise.

Every other Windows application the user installs asks. This one should too.

## 2. Facts established before planning

These were checked against the running system or the repository, not assumed.

| Fact | Evidence |
|---|---|
| `Setup.exe -t, --installto <DIR>` exists | `Setup.exe --help`, Velopack 1.2.0, on 192.168.123.188 |
| Setup's only other options are `-s/--silent`, `-v`, `-l`, `-h` | same |
| `Update.exe` takes `--rootDir` to override the **default** locator root | `Update.exe --help`, same host |
| Install layout is `%LocalAppData%\Nyanako.Syrtis\{current, packages, Syrtis.App.exe, Update.exe}` | directory listing, same host |
| The app builds its `UpdateManager` with a null locator — the default, exe-relative one | `src/TokenBar.App/UpdateFlow.cs:93-107` |
| The app is `SelfContained` + `WindowsAppSDKSelfContained`; Setup.exe is ~83 MB | `src/TokenBar.App/TokenBar.App.csproj`; packaging output |
| A release publishes 16 assets, enumerated explicitly | `.github/workflows/release.yml:220-232` |
| CI asserts exactly 4 files per channel | `.github/workflows/release.yml:133-144` |
| `write-package-evidence.ps1` throws unless there is exactly one Setup.exe | `scripts/write-package-evidence.ps1:108` |
| The Setup/nupkg size budget is stated **twice** — `package-velopack.ps1:443-452` and an independent map at `write-package-evidence.ps1:113-125`, enforced at `:136-140` from `ci.yml:434` | both files |
| Everything shipped is unsigned; SmartScreen warns on first run | `docs/release-velopack.md:126` |
| packId is frozen and determines the install directory | `docs/release-velopack.md:119-121` |
| 192.168.123.188 has the .NET Framework 4.8.1 runtime but **no targeting packs** | registry + reference-assembly check |

## 3. Options considered

The wizard must run on a machine with no runtime installed, because it is the
thing that installs the runtime-bearing app. That eliminates a
framework-dependent .NET 10 build and makes the choice a product decision
about download size and the install chain.

| | A. Setup embedded | B. Setup beside it | C. .NET 10 self-contained |
|---|---|---|---|
| Wizard size | ~150 KB + embedded 83 MB | ~150 KB | ~70 MB + 83 MB |
| Files the user downloads | one | two | two |
| SmartScreen prompts | one | two | two |
| Runtime prerequisite | .NET Framework 4.8 (in Win10 1903+/11) | same | none |
| Release asset count | unchanged | +4, breaks two CI assertions | +4 |
| ARM64 | emulated | emulated | native |

**A was chosen.** The wizard is published under the existing filename
`Nyanako.Syrtis-<ch>-Setup.exe`, so no documentation link or release asset
name changes. Its cost is that the wizard becomes a mandatory link in every
install; `--silent` passes straight through to keep scripted installs working,
and Velopack's raw Setup remains buildable locally from the same pack.

## 4. Design

Four pages in one fixed-size form, `< Back` / `Next >` / `Cancel` right-aligned
along the bottom.

| Page | Content | Buttons |
|---|---|---|
| Welcome | icon, heading, what will be installed and its version | Back disabled |
| Location | path box defaulting to `%LocalAppData%\Nyanako.Syrtis`, `Browse…`, a line stating current-user-only and no administrator rights | Next disabled while the path is invalid, validated as typed |
| Installing | heading, status line, **indeterminate** bar | all disabled, Cancel included |
| Done | success: `Launch Syrtis` checkbox. failure: exit code and log path | single `Finish` |

Setup is invoked as `--installto "<dir>" --silent --log "<temp>\syrtis-install.log"`.

The progress bar is indeterminate because `Setup.exe --silent` reports no
progress. Faking a percentage was rejected: the update dialog in #76 spent four
rounds learning that invented feedback is worse than honest absence.

**Bilingual, with its own string table.** The wizard cannot reference the app's
i18n — `TokenBar.Core` is net10 and this is net48 — so it carries one static
class holding every user-visible string in English and Traditional Chinese,
selected from `CultureInfo.CurrentUICulture`. An English installer for a
localised app would be a visible inconsistency; a twenty-entry table is
cheaper than that.

## 5. Slices

### INSTALL-1a — the wizard, standalone

A `src/Syrtis.Installer/` net48 WinForms project referencing
`Microsoft.NETFramework.ReferenceAssemblies`. It takes the path to a Velopack
Setup.exe as an argument, so it is runnable and observable before any
packaging work exists.

`src/Syrtis.Installer/` **stays out of `src/TokenBar.slnx`**, for the same
reason `TokenBar.App` does (`TokenBar.App.csproj:3-5`): the slnx is the macOS
inner loop, `scripts/check.sh:19-21` restores it `--locked-mode` and builds it,
and net48 WinForms cannot build there. Keeping it out is also what makes this
slice's rollback — delete the directory — true.

The build policy that reaches it is the repo-root `Directory.Build.props:9-21`;
there is no `src/Directory.Build.props`. It applies product and version
identity to every project in the tree, plus `RestorePackagesWithLockFile`. Two
consequences, both accepted: the wizard assembly is stamped with the repo
version, and it produces a committed `packages.lock.json`. Nothing globs
`src/**/packages.lock.json` — `build-app-artifact.ps1:274-277` is an explicit
four-entry list — so a lock file outside the slnx has no further consequence.
`src/Directory.Build.targets` does not apply: every target in it is gated on
the opt-in `TbNativePackagingEnabled`.

**Acceptance — three observations. A1 and A3 are the user's.**

*A1 (user, at 192.168.123.188).* The four pages render, Back/Next/Cancel
behave, Browse picks a directory, and the wizard installs to a **non-default**
directory outside `%LocalAppData%`. Nobody else can judge the dialog: this
slice is blocked, not in progress, until the user has looked at it.

*A2 (main session, from logs) — the decisive one.* The app launched from that
non-default directory must produce `update-available: v0.2.2` in
`%LocalAppData%\Temp\tokenbar-app.log`, and after Install Update,
`update-handoff: started v0.2.2` then a fresh `launch: tray up`, with
`<non-default dir>\current\Syrtis.App.exe` reporting FileVersion `0.2.2.0`.
Prerequisite: a locally packed Setup.exe at **0.2.1**, so the published v0.2.2
is a real available update.

*A3 (user, at 192.168.54.128, the ARM VM).* The same AnyCPU executable
launches and renders its four pages. No install at this stage — a cheap early
signal taken before 1b builds any embedding machinery on the assumption that
it works. Whether ARM64 Windows runs it natively or emulated is not something
this plan relies on; the window either appears or it does not.

ARM64 is not a minority case: `win-arm64` and `win-arm64-lite` are 2 of the 4
published channels and 8 of the 16 release assets.

**Result, 2026-08-28: all three observed, INSTALL-1a accepted.**

A1 and A3 were confirmed by the user at the machines. A2's evidence, from
`%LocalAppData%\Temp\tokenbar-app.log` after installing to `C:\SyrtisTest\Syrtis`:

```
14:26:26.423 update-download: verified v0.2.2
14:26:27.331 update-handoff: start v0.2.2        (+908 ms dwell, as designed)
14:26:27.491 update-handoff: started v0.2.2
14:27:30.434 <new process, PID 18764>
14:27:30.682 launch: tray up
14:27:31.900 update-check: none
C:\SyrtisTest\Syrtis -> 0.2.2.0
```

**`--installto` does not break the updater.** The app installed outside
`%LocalAppData%` resolved its GitHub feed, downloaded, applied and restarted
into 0.2.2. The Browse button stays; the risk row below is closed as refuted.

**One finding this produced, outside this slice's scope.** The gap between
hand-off and restart was **63 seconds** here against 4.5 seconds for the same
0.2.1-to-0.2.2 update at the default location — 13x, with nothing on screen for
all of it, because our process is gone by then. The user reasonably read it as
a hang and reported it as one. #76's PR body describes that window as "several
seconds"; that is now known to be wrong for at least one real install path. The
likely cause is Defender scanning a directory outside the usual application
path, unconfirmed. Tracked separately: it belongs to the update flow, not to
the wizard, and no change here would fix it.

### INSTALL-1b — embed and package

`package-velopack.ps1` builds the wizard, embeds Setup.exe as a resource, and
emits the result under the existing Setup filename. **Both** statements of the
size budget are updated with measured values. The exactly-one-Setup rule at
`write-package-evidence.ps1:108` is re-read before anything near it is touched.

Acceptance: a local pack installs correctly; `--silent` installs headlessly
with no window; the CI evidence step (`ci.yml:434`) is run against the packed
output, because that is the path the second budget map is enforced on.

**ARM64 gate.** 1b commits the shipping form for all four channels, so it does
not close until a packed `win-arm64` wrapped Setup has installed on
192.168.54.128 and the app has started, observed by the user.

### INSTALL-1c — CI and docs

`.github/workflows/release.yml`, `docs/release-velopack.md`,
`docs/lite-distribution.md`. Acceptance: a release dry run publishes the same
16 asset names and the four-file assertion still holds.

## 6. Risks

| Risk | Handling |
|---|---|
| ~~`--installto` breaks the updater~~ | **Closed 2026-08-28, refuted by A2.** An install at `C:\SyrtisTest\Syrtis` found the feed, updated and restarted into 0.2.2. Browse stays. |
| .NET Framework 4.8 absent | Windows 10 1903+ and all Windows 11 ship it. The wizard fails to start with a Windows-supplied message. Documented in 1c, not handled. |
| ARM64 | Split across both slices so neither can skip it: A3 in 1a is launch-and-render; 1b does not close without a real ARM64 install. |
| A broken wizard blocks every install | `--silent` passthrough keeps scripted installs working; the raw Velopack Setup is still buildable locally. |
| A new project collides with repo-wide build policy | Enumerated above from `Directory.Build.props:9-21`; both consequences stated and accepted. |

## 7. Non-goals

Machine-wide installation. Elevation. An uninstall wizard. Signing. Changing
packId or the install layout. A progress percentage. Any change to how the app
updates.

## 8. Revision history

Four fresh reviews, five blockers, before any code:

1. INSTALL-1a's acceptance named a machine but no human actor and no build
   host, so the slice could have been closed with nobody having seen the
   dialog it exists to produce.
2. The Setup size budget is stated in two files; the first draft owned one.
   The repository's own rule — a contract stated in N places is reconciled
   only after reading all N — was written after an identical failure.
3. "Finds its update feed" had no observable. Replaced with named log strings.
4. A repo-wide collision with `Directory.Build.targets` was asserted by
   generalising from a single error message, without checking that every
   target in that file is gated on an opt-in property. The named work did not
   exist.
5. The correction to (4) cited `src/Directory.Build.props:20`, copied from a
   reviewer without opening the file. That file does not exist; the real one
   is at the repo root and carries six properties, not one.

(4) and (5) are the same failure twice in one hour: asserting a fact about a
file without opening it.
