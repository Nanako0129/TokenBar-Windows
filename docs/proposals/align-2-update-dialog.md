# ALIGN-2: Sparkle-style update dialog — behaviour spec

> `ALIGN-2`'s acceptance intent opens with "先取得或建立明確行為規格" — obtain or
> create an explicit behaviour spec first. This is that spec.
>
> It is written against macOS's actually-rendered screen rather than Sparkle's
> source, and against this repository's update path as it actually behaves rather
> than as an earlier draft of this document described it.

| | |
|---|---|
| Reference screen | [`docs/references/sparkle-update-alert-macos.png`](../references/sparkle-update-alert-macos.png) — 660×514, the real dialog for TokenBar 1.0.6 → 1.0.7 |
| Element inventory | Sparkle `SUUpdateAlert.xib` (authoritative for *what*, silent on *why*) |
| Windows baseline | `main` after PR #74 |
| Written | 2026-08-27 |

## Scope

**One slice, one owner, end to end.** An earlier draft split this four ways. Each
piece was then too small to hand off and each carried a full plan → PR → review →
merge round of its own, so the splitting cost more than it saved. The work below
is delegated as a unit.

## What exists today

`RunUpdateCheckAsync` (`App.xaml.cs`) creates an `UpdateFlow`, gets a candidate,
and queues `App.PublishUpdate`. That calls `tray.PublishUpdate(version, action)`
where `action = () => StartUpdateDownload(flow, candidate)`. The tray shows a
notification and a menu item; invoking the menu item runs the action, which
downloads and hands off.

There is no dialog, no changelog, no defer, no skip.

## The reference screen

| Band | Content |
|---|---|
| Header | Rounded app icon left; two lines right — bold headline, then a secondary line naming both versions |
| Middle | Inset scrolling box, rounded, bordered, ground slightly lighter than the window; renders the changelog; scrollbar right; content clipped at the bottom |
| Footer | Unchecked "Automatically download and install updates in the future", then a button row |

No window title, only traffic lights. Dark throughout.

> **The button arrangement carries meaning, and the xib does not show it.**
>
> `Skip This Version` sits **alone on the left**; `Remind Me Later` and
> `Install Update` are **grouped right**, with `Install Update` the accent
> default at the far right.
>
> Skip is the only action with a persistent consequence — this version is never
> offered again — so it is spatially separated from the two reversible ones.

## Trigger, and the three actions

**The dialog is opened by the tray menu item's handler, before the pending
action is taken.** It is never shown on its own.

That distinction is the whole design, and getting it backwards breaks two of the
three buttons. `TrayService.InvokePendingUpdate` (`TrayService.cs:214`) does
`_pendingUpdate.Take()` and sets `_activeUpdate = pending` **before** running the
action. If the dialog were opened from inside the published action, then by the
time it is visible the action is already taken:

- Later's "the pending action stays" would be false — it is gone.
- Skip would leave `_activeUpdate` non-null, and `TrayService.PublishUpdate`'s
  `_activeUpdate is not null` guard then refuses **every** future offer for the
  life of the process. Skipping 0.2.3 would silently suppress 0.2.4.

So:

| Constraint | |
|---|---|
| The menu item's `RelayCommand` (`TrayService.cs:263`) and the balloon click (`:53`) **open the dialog** rather than calling `InvokePendingUpdate` | |
| `InvokePendingUpdate` — or an internal wrapper of it — is reached **only from Install** | It must become reachable from the dialog; it is `private` today |
| Later and Skip both leave `_activeUpdate` **null** | Neither took anything |
| Skip additionally clears `_pendingUpdate` | `PendingUpdateAction.Clear()` already exists and is already called from `TrayService.cs:183`; what is new is only a TrayService wrapper that calls it plus `RebuildMenu()`. Neither `RestoreUpdateAction` (restores a *taken* action) nor `CompleteUpdateAction` (clears after a successful hand-off) fits |

**A consequence the earlier draft missed:** `PublishUpdate(version, action)`
carries only a version string, but the dialog also needs the release notes and
the installed version. Its signature grows a small display record, or the tray
holds the candidate alongside the pending action.

That settles what each action means and how they differ observably:

| Action | Behaviour | Implementation |
|---|---|---|
| `Install Update` | Takes and runs the pending action — what the menu item did directly before the dialog existed | **Must go through `InvokePendingUpdate`**, see below |
| `Remind Me Later` | Closes. The pending action stays, so the tray still offers it and the next check offers it again | Nothing; do not clear |
| `Skip This Version` | Closes, records the version, clears the pending action so the tray stops offering it | New settings key |

The checkbox is **out of scope** — unattended installation is a behaviour change,
not a UI port. The dialog ships with three buttons. Rendering it disabled for
visual parity is **rejected**: a disabled, unexplained control is worse than its
absence. Allowed visual differences are listed under acceptance.

## Install must take the pending action

`UpdateFlow._downloadActive` is **per instance**, and every check does
`new UpdateFlow()`. The only cross-flow guard is `TrayService._activeUpdate`,
which is set when the pending action is *taken*.

If the dialog's Install calls `flow.DownloadAndVerifyAsync` directly,
`_activeUpdate` is never set, `TrayService.PublishUpdate`'s "refuse while
downloading" guard is bypassed, and the menu item stays live during the download.
Invoking it starts a second `UpdateFlow` against the same package path — and
either flow's failure calls `CleanAllPackages`, so the two destroy each other's
work.

**Constraint: Install invokes `InvokePendingUpdate` — the path the menu item
used to take directly, and which now runs only from the dialog.**

## What actually protects a candidate held open

The dialog may sit open for minutes. That is safe, but **not** for the reason an
earlier draft of this document gave. There is no live re-fetch of the feed.

| Mechanism | Where |
|---|---|
| The candidate's `UpdateInfo` is captured in memory; its SHA256 is pinned when the candidate is built | `ValidateTarget` |
| `ValidateCandidate` re-runs the full validation **three times** — before download, after download before checksum, and in `TryHandoff` | `UpdateFlow.cs:123, 129, 155` |
| Each re-validation re-reads the **current installation**, so a version that moved underneath is rejected | `GetInstallation()` inside `ValidateTarget` |
| Downloaded bytes are verified against the pinned hash | `VerifyChecksumAsync` |
| The package's own nuspec is parsed and checked after download | `ValidateNuspec` |

A publisher swapping the bytes behind the same version fails the checksum and
closes.

> **What must not be done** is skip any of the three `ValidateCandidate` calls,
> or hand `WaitExitThenApplyUpdates` a target that did not come from one of their
> return values. Caching the captured `UpdateInfo` is not the hazard — it is the
> design.

## Skip: three decisions

| Decision | Value |
|---|---|
| What is stored | `UpdateCandidate.Version` verbatim — it comes from `target.Version.ToNormalizedString()` and has already passed length, control-character and non-prerelease checks. **Never** a string read back out of the dialog's own display text |
| How it compares | **Exact ordinal equality.** A rule containing `<` or `<=` is the wrong rule: `"999.0.0"` under an ordering rule permanently and silently suppresses every future update. Sparkle's semantics are skip *this* version, and "a newer version is still offered" then holds for free |
| On read | Anything that is not a well-formed version — null, empty, over-long, `"*"`, containing control characters — resolves to **offer the update** |
| **Who is asking** | A skip suppresses the **automatic** check only. A user-initiated check re-offers a skipped version |

> **The last row was wrong in the first implementation, and a user found it.**
> The gate applied unconditionally, so a mis-clicked Skip left no way back
> except editing `settings.json`: the tray stopped offering, and Settings'
> "Check now" reported the version honestly but could not act on it.
>
> Sparkle reads the skipped version **only for a background check** —
> `SUAppcastDriver.m:228`, `background ? [SPUSkippedUpdate skippedUpdateForHost:_host] : nil`.
> Skip means "stop nagging me", not "never show me this again". This spec had
> already written "Sparkle's semantics are skip *this* version" and then
> implemented something stricter than Sparkle.
>
> `ShouldOffer` takes `userInitiated` so the distinction lives in the layer the
> tests compile, not in `App.xaml.cs` where it would have none.

`PendingUpdateAction.ValidateVersion` is `private static void` and throws, so it
**cannot be called from outside that class as written**. This slice owns
promoting it to `internal static bool TryValidateVersion(string)` and routing the
existing call through it, so the skip rule reuses one definition rather than
growing a second.

**The skip rule itself lives in `PendingUpdateAction.cs`, beside
`TryValidateVersion`**, with `App.PublishUpdate` calling it. That file is already
in `TokenBar.Core.Tests.csproj`'s `<Compile Include>` set; `App.xaml.cs` is not
and is not going to be. Putting the rule in `App.xaml.cs` would leave acceptance
item 1 with nowhere to run from.

### The gate goes in `App.PublishUpdate`

Named by file and method because there are two `PublishUpdate` methods and they
behave differently.

`App.PublishUpdate` (`App.xaml.cs`) is where the app decides **whether to offer**
a found update; gating there means a skipped version produces no notification and
no menu item. `TrayService.PublishUpdate` is the mechanism that offers it, and
gating there would conflate "skipped" with "already downloading".

> **The gate must not touch the result.** `CheckForUpdatesAsync` is the single
> entry shared by the startup check and Settings' "Check now", so the seam cannot
> tell callers apart. Gating inside `RunUpdateCheckAsync` would suppress
> `UpdateCheckResult.Available` and make Settings report **"You are up to
> date."** while an update exists — the exact shape
> `.github/release-notes/v0.2.2.md:37` describes: *"They will check for updates,
> find nothing they consider valid, and report that you are up to date."*
>
> A skipped version still reports honestly to a manual check. It is only not
> *offered*.

## Release notes are not in the package yet

**Measured, not inferred.** The published `Nyanako.Syrtis-0.2.2-win-x64-full.nupkg`
nuspec is 545 bytes and carries no notes element at all:

```
<id>Nyanako.Syrtis</id>  <version>0.2.2</version>
<title>Syrtis</title>    <description>Syrtis</description>
                         no <releaseNotes>
                         no <releaseNotesHtml>
```

`scripts/package-velopack.ps1`'s `$vpkArgs` passes no `--releaseNotes`.
`.github/release-notes/v0.2.2.md` exists, but `release.yml` only feeds it to
`gh release --notes-file` — that is the **GitHub release body**, which Velopack's
`GithubSource` never reads; the client reads `releases.{channel}.json` from the
release assets.

> An earlier draft said "Velopack provides both formats". That proves the *type*
> has the fields, not that **this pipeline fills them**. Without this work the
> dialog's middle band — the only thing it has that a notification does not —
> renders empty.

This slice wires `--releaseNotes` into packaging. Notes are baked into the nuspec
at pack time and **cannot be backfilled**, so "no notes" is a **first-class
state** — in the dialog ("This update has no release notes") and in packaging
alike, not an error path in either: every version published so far is in it.

### The packer runs in two workflows, identically

`scripts/package-velopack.ps1` is invoked with the same three arguments by
`ci.yml:401` on every push and by `release.yml:126` on a tag. **Neither passes a
tag**, and the script already computes `$packProperties.SemanticVersion` itself.

So the notes path is derived **inside the script** from that version —
`.github/release-notes/v$($packProperties.SemanticVersion).md` — and the argument
is appended only when the file exists. An in-flight version with no notes file
packs exactly as it does today, so `ci.yml` keeps working, and the two workflows
cannot drift because neither passes the path.

**The feed assertion goes in the script too**, beside the pack-id, version,
mainExe, architecture and channel assertions it already makes (`release.yml`'s
own comment at :131-133 names those). Stated conditionally: *if a notes file was
found, the produced `releases.{channel}.json` must carry non-empty
`notesMarkdown`.* That runs on every PR rather than only at tag time, and it is
red the moment `--releaseNotes` stops reaching `vpk pack`.

## Changelog rendering: hand-written Markdown subset

| | Approach | Disposition |
|---|---|---|
| A | WebView2 + `NotesHTML` | **Rejected.** Hands a script engine, `fetch()` and a navigation surface to the dialog; making it safe needs CSP, `NavigationStarting` interception and no host bridge — more guard than feature. Also needs a runtime the user's machine may lack |
| B | Markdown subset → `RichTextBlock` | **Chosen** |
| C | `CommunityToolkit.WinUI` `MarkdownTextBlock` | **Rejected, for different reasons than A** — it is a NuGet package in the payload, not a machine runtime, so the v0.2.2 lesson does not apply. Rejected on payload and maintenance cost, and because it renders real `Hyperlink`s, reintroducing the link surface |

> **`NotesMarkdown` is not cleaner than `NotesHTML`.** Both come from the same
> `--releaseNotes` markdown via our own CI; Velopack sanitizes neither, and
> Markdown passes raw HTML through, so `NotesHTML` can contain `<script>`. They
> are equally trusted.
>
> **B is safer because native controls have no executable semantics** — no
> script, no network, no navigation. Not because Markdown is cleaner. B's honest
> cost is that it puts new hand-written code on the trust path, which is what the
> bounds below exist for; its output is a paragraph model, not anything
> executable, so that cost is bounded.

### Degradation, derived from the real release body

| Construct | Renders as | Note |
|---|---|---|
| `# ` / `## ` heading | Bold line | The space is required. `#39` is not a heading, wherever it appears |
| Unordered list | Bullet | |
| Ordered list | Bullet, **numbering lost** | Accepted degradation |
| `**bold**` | Bold run | Must be **inline**: `**Unsigned.** Windows SmartScreen…` is bold followed by plain text on one line |
| `*italic*` | Italic run | 3 occurrences in the real body |
| `` `code` `` | Monospace run | 10+ occurrences in the real body |
| `[text](url)` | **Text only, URL discarded** | **Never** render `text (url)` — the common "helpful" variant, and strictly worse |
| Table | Plain text | |
| Consecutive plain lines | **One paragraph**, joined with a space | Markdown's soft wrap. Added after the first visual check: without it a hard-wrapped changelog renders every fragment of a sentence as its own spaced block. A blank line, a heading or a bullet ends the paragraph |

### Bounds

The parser runs on the UI thread during dialog construction, on input from the
feed. **It must be total — it never throws.**

Shapes to copy from this repository rather than invent:

| Bound | Existing precedent |
|---|---|
| Explicit constant limit checked **before** parsing, not after reading it all | `MaxNuspecBytes = 65_536` with a `Max+1` buffer (`UpdateFlow.cs:69, 294-311`) |
| Control-character rejection | `version.Any(char.IsControl)` (`UpdateFlow.cs:207`) |
| Display-string length cap | `MaxVersionLength = 64` (`PendingUpdateAction.cs:5`) |
| Truncate untrusted text before it reaches a log | `type[..Math.Min(type.Length, 64)]` (`TrayService.cs:174-176`) |

New, with no precedent here:

| # | Bound | Why |
|---|---|---|
| 1 | Total input size, 64 KB | Neither the feed nor the notes field is bounded upstream; the real notes are ~4 KB |
| 2 | **Paragraph and run caps, independent of the byte cap** — 500 paragraphs | 64 KB of newlines is 65 536 empty paragraphs. Thousands of `Paragraph`s on a `RichTextBlock` **hangs the UI thread rather than throwing** — no try/catch reaches it, and the dialog is modal |
| 3 | Line length, 2 000 chars then ellipsis | A 64 KB line with wrapping on is one enormous measure pass |
| 4 | **No recursion** — line-oriented single pass, flat block model | Nothing to blow. A future nested list adds an explicit depth counter, never unbounded recursion |
| 5 | **Inline scanner as a character loop, no regex** | Shorter than getting the regex right for this subset. If regex is used anyway: `RegexOptions.NonBacktracking` **and** a match timeout — a timeout alone still burns a second on the UI thread |
| 6 | **Strip** bidirectional and zero-width formatting characters | `char.IsControl` covers only `Cc`. U+202A–202E, U+2066–2069, U+200E/200F and U+061C are `Cf` and return false — the Trojan Source class, which in a changelog can reverse or hide a line. **Strip rather than reject**: one stray character must not erase the whole changelog. **Decision: U+200D ZWJ is stripped too**, accepting that emoji sequences like 👨‍👩‍👧 break in changelogs |

### Notes are outside the existing validation chain

Notes arrive with `releases.{channel}.json` **before** any download.
`ValidateTarget` and `ValidateNuspec` protect the **package** — hash, filename,
nuspec fields — and none of them covers notes. A candidate that would ultimately
fail `ValidateNuspec` has already had its notes rendered in front of the user.

**Bound the notes inside `ValidateTarget`**, which is already the established
failure shape, and put the bounded string on `UpdateCandidate` so the dialog only
ever sees a clean value. On violation, **drop the notes and keep the update** —
otherwise notes become a lever for denying updates entirely.

## Also needed

`UpdateCandidate` must carry the installed version. `GetInstallation()` computes
it; nothing surfaces it, and the version line names both.

## Acceptance

| # | Item | What turns it red |
|---|---|---|
| 0 | Notes reach the package | The assertion lives in `package-velopack.ps1` beside the ones it already makes, stated conditionally: *if a notes file was found for this version, the produced `releases.{channel}.json` must carry non-empty `notesMarkdown`*. Removing `--releaseNotes` from the script — or removing the derived-path append — turns the `deployment-matrix` job red **on every PR**. `release.yml` is not mentioned because it passes no notes argument; that is the point of deriving the path inside the script |
| 1 | Skip suppresses exactly one version | Written and compared through the same `UpdateCandidate.Version` accessor; substituting any other version-string form on the write side turns it red |
| 2 | Bad stored values offer the update | null, empty, over-long, `"*"`, control characters, `"999.0.0"` — each returns *offer*, and the rule returns a bool for every input without throwing. Changing equality to `<=` turns the `"999.0.0"` case red |
| 3 | Markdown degradation | Per-construct cases **plus** the whole of `.github/release-notes/v0.2.2.md` as one input, asserting no residual backtick, paired asterisk or line-initial `#` survives. Removing code-span or emphasis handling turns it red. A synthetic line beginning `#39` must not become a heading; making the heading check naive turns that red |
| 4 | Parser bounds | One case per bound. Two must be independent of the byte cap: a **small** input with more than 500 paragraphs, and a case covering U+2066–2069, U+061C and U+200D. Deleting the paragraph cap, or any single stripped code point, turns one red |
| 5 | Version line | Exact string, **rendered in both languages**. The translation table *is* the format string — `Localized(a, b)` goes through `string.Format` — so a stray `{2}` in `strings-zh-Hant.json` throws `FormatException` at dialog construction. (An earlier draft called this the codebase's first two-placeholder string. It is not: the table already has three-placeholder entries, and `format.monthYear` reorders them. `.Localized(a, b)` is in use at `TrayService.cs:321, 383` — no new mechanism is needed) |
| 6 | The three actions (manual) | Install downloads **and the tray stops offering the update while it runs**; Later closes and the tray still offers it; Skip closes, the tray stops offering it, and the automatic check no longer offers it — **while Settings' "Check now" still reports `Update available: vX`**. That last clause is red if the gate sits in `RunUpdateCheckAsync` instead of `App.PublishUpdate`. Plus, per decision 2: with the dialog open, invoking "Update to v…" again produces one dialog and no crash |
| 6b | A skip or a defer does not suppress the *next* version | **Rewritten against the invariant** (decision 4): after Later and after Skip, `_activeUpdate` is null. Observed directly in DevLog — `update-dialog: later v… active=False` and `update-dialog: skip v… active=False` — and in menu state (Later: item still present; Skip: gone). Red for any implementation that opens the dialog after `InvokePendingUpdate` has taken the action, because `_activeUpdate` then stays set and `PublishUpdate`'s guard refuses every later offer. Item 6 alone cannot catch that — "the tray stops offering *this* version" is true in the broken case too. Needs no second real release |
| 7 | Visual (manual) | Against the difference list below. Run with `--update-dialog-demo` (decision 4) |

The parser must be added to `TokenBar.Core.Tests.csproj`'s `<Compile Include>`
set, or placed in `TokenBar.Core` — items 3 and 4 are not testable otherwise.
Items 6 and 7 cannot be automated: no test project compiles the dialog.

### Allowed differences from the reference

| Difference | |
|---|---|
| No "automatically download" checkbox; button row moves up into that space | **Allowed** |
| Skip left, other two grouped right | **Must match** |
| Install rightmost and accent-coloured | **Must match** |
| Header's three elements (icon, bold headline, secondary version line) | **Must match** |
| Changelog as an inset scrolling box whose content may be clipped | **Must match** |
| Absolute type sizes, corner radii, spacing | **Allowed** to follow platform convention |

## Non-goals

- Unattended download and install (the checkbox) — separate slice
- Beta channel — unrelated, and `release.yml` publishes no pre-releases, so a toggle would be inert (see [`macos-parity-v1143.md`](macos-parity-v1143.md))
- No change to `packId` or the channel contract

## Corrections to this document, found while implementing it

| Where | What it said | What is true |
|---|---|---|
| "Trigger, and the three actions" | `TrayService.InvokePendingUpdate` is at `TrayService.cs:214`, the menu item's `RelayCommand` at `:263`, the balloon click at `:53`, `RelayCommand` at `:698-705` | Line numbers only, and all four were right on `main` at `a64ee14`. Recorded because they have already moved with this change |
| Acceptance 5 | "`Localized(a, b)` … the version line is two placeholders" | It is **three**: product name, new version, installed version — `"{0} {1} is now available — you have {2}. …"`. The point stands unchanged (it is a `string.Format` format string either way), and the table already had three-placeholder entries, as the item itself notes |
| "Changelog rendering" | Nothing about soft wrap | A line-oriented parser that emits one block per source line renders a hard-wrapped changelog as a column of one-sentence paragraphs. Found by looking at the first screenshot, not by any test. See the degradation table |
| Acceptance 0 | "turns the `deployment-matrix` job red **on every PR**" | Still true, and still the only place it can be proven. `scripts/package-velopack.ps1` calls `build-app-artifact.ps1`, which refuses a dirty checkout *and* needs `vendor/tokscale-core` present, so an end-to-end run of the packer is not available from a development checkout with uncommitted work. What was verified locally instead: that `vpk pack --releaseNotes <path>` is real and lands as `Assets[].NotesMarkdown` in `releases.{channel}.json` (a direct `vpk` run against the repo-pinned tool), and that the assertion block parses that real feed and fires on an emptied `NotesMarkdown` |

## A pre-existing inconsistency this slice leans on

`PendingUpdateAction.ValidateVersion` uses `System.Version.TryParse` plus a
`ToString()` round-trip, which **rejects semver build metadata and prerelease
suffixes** (`1.0.7-beta`, `1.0.7+abc`), while `ToNormalizedString()` is semver
normalisation. Harmless today — `ValidateTarget` already rejects prereleases and
the release pipeline emits three-part versions — but the two validators disagree
in principle. If a version ever carries build metadata, `PublishUpdate` throws and
the notification silently disappears. Not introduced here, but depended on twice
as heavily.

## Open decisions the implementer owns — resolved

> Resolved during implementation on `feat/update-dialog`. Each section below
> keeps the question and the reviewer's minimum revision, and adds **Resolved**
> with the choice actually in the tree.

Four rounds of plan review converged on the security decisions and the gate
location — those are settled and unchanged since round 1. What it did not
converge on is the implementation seams below, because they are answered by
reading the code and choosing, not by more specification. They are handed over
explicitly rather than left implicit.

Each carries the reviewer's minimum revision.

### 1. Where the skip rule lives, given it reads a settings key

Putting it in `PendingUpdateAction.cs` (round 3's fix) puts it in the test
compile set, but `AppSettings` is **not** in that set — an implementation that
calls `AppSettings.Store.GetString(...)` from there will not compile, and the
obvious escape is to move it back to `App.xaml.cs`, where acceptance 1 has
nowhere to run.

**Reviewer's minimum revision:** make the rule a pure function that does not
reference `AppSettings` — `ShouldOffer(string candidateVersion, string? storedSkipped)`,
with `App.PublishUpdate` doing the key read and passing the value in — or have
it take a `SettingsStore` (which *is* reachable, `src/TokenBar.Core/SettingsStore.cs`).

**Done when:** acceptance 1 and 2 compile and run inside `TokenBar.Core.Tests`,
and that project's `<Compile Include>` set gains nothing from the App layer
except the parser.

**Resolved — the pure function.** `PendingUpdateAction.ShouldOffer(string
candidateVersion, string? storedSkipped)`, beside `TryValidateVersion` in
`PendingUpdateAction.cs`. `App.PublishUpdate` does the read:
`AppSettings.Store.GetString(PendingUpdateAction.SkippedVersionKey)`.

The `SettingsStore` alternative was rejected on cost, not principle: the rule
needs one string, and taking a store would make every test construct a
temporary JSON file to assert a string comparison. The key *name*
(`tokenbar.update.skippedVersion`) still lives in `PendingUpdateAction.cs` as a
bare `const`, which costs nothing and keeps the reader and the rule together.

`ValidateVersion` became `internal static bool TryValidateVersion(string?)`; the
throwing `Publish` path now calls it and raises the same `ArgumentException`, so
there is one definition of a displayable version string.

The test project's `<Compile Include>` set gained exactly one App file,
`UpdateDialogContent.cs` — the parser plus the dialog's copy. See decision 5
below, which was not on the list.

### 2. Dialog re-entrancy

Both triggers stay live while the dialog is open, by design — nothing was taken.
The plan never says what a second trigger does. A `ContentDialog` throws
`InvalidOperationException` on a second open, synchronously inside
`RelayCommand.Execute` (`TrayService.cs:698-705`), which has no exception
handling — that is a crash on the UI thread. A separate `Window` instead gives
two dialogs racing for one pending action.

**Reviewer's minimum revision:** one instance; a second trigger re-activates the
existing window, following `SettingsWindow.Present`'s `_shared` shape
(`SettingsWindow.cs:77-88`, including its `MoveInZOrderAtTop()` foreground
lesson).

**Done when:** with the dialog open, invoking "Update to v…" again produces one
dialog and no crash — added to acceptance 6 as an observable step.

**Resolved — one shared `Window`, `SettingsWindow.Present`'s shape.**
`UpdateDialog` is a `Window` with a `private static UpdateDialog? _shared`.
`Present` does `_shared ??= new UpdateDialog()`, then `Bind(offer, install,
later, skip)`, then `AppWindow.Show()` → `MoveInZOrderAtTop()` → `Activate()`.
A second trigger is therefore a re-bind and a re-activate of the window that is
already on screen; nothing is constructed and nothing throws.

`ContentDialog` was not used at all, so the crash path in the question does not
exist. The `MoveInZOrderAtTop()` line is load-bearing for the same reason it is
in `SettingsWindow`: the opener is a tray-menu click, which has no foreground
rights, and `Activate()` alone does not raise the window.

The three handlers are stored as fields and replaced on every `Bind`, so a
re-entrant `Present` cannot leave a button acting on a stale offer. Closing the
window means Remind Me Later: `AppWindow.Closing` cancels and hides, keeping
the instance and the pending action.

### 3. A newer candidate published while the dialog is open

`_activeUpdate` is null while the dialog is open, so `TrayService.PublishUpdate`
lets a new offer through and `PendingUpdateAction.Publish` overwrites
unconditionally. The dialog then shows v1's notes while Install takes v2 —
installing a different version than the one described. Skip records v1 but
clears v2's pending, and the automatic check runs once per process, so v2 does
not reappear until a restart.

**Reviewer's minimum revision:** the dialog captures the version it displays;
Install and Skip compare against the current pending (`Peek()`, or the existing
`Generation`) before acting, and on a mismatch do nothing and re-present.

**Done when:** with vX shown, publishing vY means Install does not install vY and
Skip does not clear vY's pending — the menu item is still there.

**Resolved — compare against `Peek()`, and re-present rather than close.**
`TrayService.TryOpenUpdateDialog` captures the version it is about to display
and hands each handler a closure over it. Both Install and Skip go through
`ActOnShownOffer(shownVersion, act)`, which compares `shownVersion` against
`_pendingUpdate.Peek()` ordinally before running `act`.

`Peek()`, not `Generation`: `Generation` is `private` to `PendingUpdateAction`
and reachable only through a `PendingAction`, which is exactly the thing the
dialog must not have taken. The version string is already the identity every
other part of this path compares on.

On a mismatch nothing is taken, and `ActOnShownOffer` returns
`!TryOpenUpdateDialog()` — it re-presents against whatever is pending now, and
returns "keep the window open". If nothing is pending at all,
`TryOpenUpdateDialog` returns false and the window closes instead of sitting
there showing an offer that no longer exists.

The offer record itself lives on `TrayService` as `_pendingOffer`, set in
`PublishUpdate` and cleared everywhere the pending action is cleared
(`CompleteUpdateAction`, `SkipUpdate`, the `PublishUpdate` failure path,
`Dispose`). That is the "tray holds the candidate alongside the pending action"
branch of the choice above: `PendingUpdateAction` has no opinion about display
data and does not grow a payload for one.

### 4. Making 6b observable

6b is the only item that catches the dialog being opened *after* the action is
taken, but as written it needs one installed build to see two successive
releases — two real releases for one acceptance run.

**Reviewer's minimum revision:** rewrite it against the invariant instead of the
scenario — after Skip and after Later, observe directly that `_activeUpdate` is
null (Later: menu item still present; Skip: gone; neither in a download state),
via DevLog or menu state. Or state the manual preconditions in full.

**Done when:** 6b can be run without publishing a second real release, and is
still red for an implementation that opens the dialog after
`InvokePendingUpdate`.

**Resolved — the invariant, in DevLog, plus a debug flag that makes the whole
dialog reachable at all.**

Each of the three actions now writes one line naming the invariant directly:

```
update-dialog: later   v9.9.9 active=False
update-dialog: skip    v9.9.9 active=False
update-dialog: install v9.9.9 active=True
```

`active=` is `_activeUpdate is not null`. An implementation that opened the
dialog *after* `InvokePendingUpdate` would print `active=True` on the Later and
Skip lines, because the action would already have been taken — which is exactly
the state whose `_activeUpdate is not null` guard in `PublishUpdate` then
refuses every later offer. No second release is needed to see it.

The harder half was that the dialog is unreachable without a Velopack-installed
build *and* a newer published release, so none of items 6, 6b or 7 could be run
at all on a development machine. `--update-dialog-demo` publishes a synthetic
offer (`9.9.9`, installed `0.0.0`, a changelog exercising every construct)
through the real `TrayService.PublishUpdate` and opens the dialog on it, so all
three buttons act on a genuine `PendingUpdateAction`. The published action does
nothing but write `update-dialog-demo: install invoked` — that path cannot
download, verify or hand off anything. It applies the same skip gate as the real
path, so a Skip taken in the demo is observable on the next launch as
`update-dialog-demo: v9.9.9 skipped by user` with no dialog and no menu item;
clear `tokenbar.update.skippedVersion` from `settings.json` to get it back.

Same convention as the `--settings`, `--open-flyout` and `--graph3d` flags this
repository already carries.

### 5. Not on the list: where the dialog's copy lives

Item 5 wants the version line asserted in both languages, but no test project
compiles a XAML window, so a format string written inside `UpdateDialog.cs`
would be exactly as untestable as the parser.

**Resolved.** `UpdateDialogContent.cs` holds the whole content layer with no
XAML in it — `UpdateOffer`, the block model, `ReleaseNotesMarkdown`, and
`UpdateDialogText` (title, headline, version line, no-notes, three button
labels). `UpdateDialog.cs` only turns those values into controls. One file
added to the test project's `<Compile Include>` set covers items 3, 4 and 5.
