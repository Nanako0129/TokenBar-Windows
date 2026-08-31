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
install. `-s` / `--silent` is honoured so scripted installs keep working, and
Velopack's raw Setup remains buildable locally from the same pack.

An earlier draft said `--silent` "passes straight through", which was never
true and would have been dangerous once 1b puts this under Setup's filename:
1a understands only the silent switch and the setup path, and it now **refuses**
anything else rather than ignoring it. A script carrying `--installto` gets a
non-zero exit and a message instead of a silent install into the default
directory. Forwarding the rest of Setup's command line — `-v`, `-l`,
`-t/--installto` — is 1b's decision, since 1b is where the substitution
actually happens.

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
and net48 WinForms cannot build there.

**This sentence used to continue "keeping it out is also what makes this
slice's rollback — delete the directory — true", and INSTALL-1a2 made that
false.** `TokenBar.Core.Tests` now compiles several installer sources through
`<Compile Include>`, and a stale include path is a hard build error in a
project that both the macOS inner loop and CI build. Backing the wizard out
means removing those include items and the test file in the same change. The
coupling is accepted deliberately: it is the price of the sources being
reachable by a test at all. The project itself still stays out of the slnx —
that part is unchanged, and it is what keeps net48 WinForms off the macOS
inner loop.

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

### INSTALL-1a2 — make the boundary logic testable

Added 2026-08-31, after eight consecutive external review rounds on #77 each
found a real defect and **five of them were introduced or left incomplete by
the previous round's fix**. That is not a converging series, and the reason it
was not converging is structural rather than careless.

**Seven of the eight findings sit at the boundary with Win32** — command-line
parsing, path parsing, process launch, the filesystem, the console subsystem.
Only one was wizard flow, and it was the mildest. This project is a thin shim
over hostile platform semantics, and that shim had no automated coverage at
all: no test project compiled `src/Syrtis.Installer/`. Every fix was verified
by a throwaway probe that reflected into the built binary, proved one thing,
and was deleted — so each round began again from nothing.

**The two surfaces also re-derived shared decisions independently.** Program
parsed arguments, SetupLocator parsed them again, WizardForm chose the
directory, SetupRunner formatted the command line, and the Done page re-derived
whether the log was usable. Findings 4 and 7 were one defect on two surfaces;
finding 5 was one rule in two files; the whole-directory review found five more
duplicated rules. The plan for this slice reproduced the disease while
describing the cure: its own `--self-check` switch would have been swallowed by
its own round-8 argument fix and reported as a missing setup file. That is the
strongest available evidence that the fault is the missing single answer to
"what is this run", not insufficient care.

So: one `InstallRequest` produced by one `Parse`, consumed by all five call
sites; the pure logic moved where a test can reach it; the nine probes already
run by hand become tests.

**What cannot be tested there, stated because a silent omission would read as
coverage.** Windows path semantics stay out of `TokenBar.Core.Tests`: that
suite also runs on the macOS inner loop, where `Path.IsPathRooted("C:\")` is
false, and net10 does not throw where net48 does — which is finding 1 itself.
Those assertions live in a `--self-check` switch that runs them in-process on
the framework that actually ships, and it is required to fail when an assertion
is deliberately broken.

### INSTALL-1b — embed and package

**Mechanism, settled 2026-08-31 over four review rounds and ten blockers.**
Reading the pipeline first made the slice smaller than this document assumed:
`write-package-evidence.ps1` validates the Setup.exe by **name and size only**
— every structural check is on the nupkg — and `deployment-matrix` packages by
calling `package-velopack.ps1` directly. So keeping the filename and staying
under budget means neither that script nor CI changes. Full/win-x64 had 7.3 MB
of headroom against a 450 KB wrapper.

The wrapper replaces the Velopack Setup in `releases\` **before**
`package-velopack.ps1`'s own budget assertion, so that assertion measures the
file that ships. An explicit path still wins over the embedded payload, and a
path that does not exist is still an error rather than being silently replaced
— the payload is the default, not an override. The payload is materialised to
a temp file immediately after parsing and before `WizardForm` is constructed,
because the Welcome page reads the payload's product and version in that
constructor; extracting any later would put the wrapper's own version on
screen, which is what `PayloadIdentity` exists to prevent.

**The pack refuses to emit a wrapper whose payload it did not just produce.**
It reads the resource back out by reflection and compares SHA-256. Nothing
else in the pipeline could catch a stale or cross-channel payload: the
Full/win-x64 Setup at 83,721,388 bytes fits under the Full/win-arm64 budget of
87,104,109, so an arm64-named installer carrying the x64 payload would pass
every existing gate and reach half the published channels.

**A quality regression taken deliberately: PerMonitorV2 is given up.**
`App.config` ships as `<exe-name>.exe.config` and its
`System.Windows.Forms.ApplicationConfigurationSection` entry is what actually
makes WinForms DPI-aware — the manifest alone opts the *process* in and leaves
WinForms laying out at 96 DPI. Renaming the wrapper to the Setup filename
orphans that file even if it were copied, and the shipping form is one file by
design; net48 has no programmatic substitute, `Application.SetHighDpiMode`
being .NET Core 3.0+.

Of the three possible outcomes the worst is the one that arrives by doing
nothing: a DPI-aware process with an unaware WinForms **clips** at 150%. So
**both** manifest declarations go — `dpiAwareness` (2016 schema) and
`dpiAware` (2005 schema), the second of which alone keeps the process aware —
and `gdiScaling` stays, since it is honoured only for a DPI-unaware process
and is what makes the bitmap-scaled result acceptable. `App.config`'s entry is
removed too, so the two-file dev build and the one-file shipped build are not
two different DPI configurations.

The result is soft at 150% but correctly sized and never clipped, which is
what a great many installers look like. If that is judged unacceptable once
seen, the honest alternatives are shipping two files or writing the wizard
native, and both belong to 1c with the measurement in hand rather than to a
guess now.

**Result, 2026-08-31.** A full `package-velopack.ps1 -Rid win-x64` emits a
wrapper of 84,596,224 bytes, inside the existing `Full|win-x64|setup` budget of
91,040,173 with 6.4 MB to spare, so **neither budget map was edited** and
`write-package-evidence.ps1` ran unmodified against it (exit 0,
`setupName: Nyanako.Syrtis-win-x64-Setup.exe`, `setupBytes: 84596224`).
Independently confirmed on the emitted file, without executing it: it loads as
the managed assembly `Syrtis.Installer` and carries both resources —
`syrtis.ico` at 419,110 bytes and `payload.setup.exe` at 83,726,516. Velopack's
native Setup can satisfy neither. Extraction of the payload measures 62 ms.
692 tests pass, up from 677.

**Evidence status of the SHA-256 guard, stated precisely.** Its logic was
executed — a deliberately mismatched payload produced the refusal and exit 1 —
but in a harness mirroring the script's step, not in `package-velopack.ps1`
itself, because injecting a wrong payload into the real script means editing
the thing under test. The wiring was confirmed by reading: the hash is taken
from `$setupPath` before the build, the build is given that same path, the
resource is read back by reflection and compared, and the replacement and then
the budget assertion follow. Logic executed, wiring read. That is less than
end-to-end and is recorded as such rather than described as "the pack refuses".

**Carried in from 1a.** The wizard is a WinExe, so it does not attach the
parent's console: `--silent`'s diagnostic text reaches a caller that redirects
(a pipe or file is inherited) but not a person typing in a real console window.
The exit-code contract holds everywhere — 1 never reached Setup, -1 could not
start it, anything else is Setup's own — and the text is the part that is
conditional. Two reviews disagreed about this and the one calling it harmless
was wrong: both had "measured" it through redirected pipes, which is precisely
the case that works. It is deferred rather than fixed because the fix
(`AttachConsole(ATTACH_PARENT_PROCESS)`) cannot be verified from a remote
session for the same reason the defect cannot be reproduced there, and 1b
already needs a person at a real console for its ARM64 gate.

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
