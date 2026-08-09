# Proposal: Taskbar Widget (usage readout on the taskbar itself)

**Status:** draft, awaiting Go/No-Go spike
**Date:** 2026-07-17
**Owner:** Nanako

## 1. Problem

The notification-area icon is the app's only always-visible surface, and it is
16×16 (24×24 at 150%): even with the value drawn into the icon
(`TrayIconRenderer`), a figure like `50M` is at the edge of legibility and
anything richer (cost + tokens + rate) is impossible. macOS gets a real text
menu-bar item for free; Windows does not — parity table #1 works around it
with the tooltip and the always-on flyout header, but none of those are
*glanceable*.

Idea: render the readout directly on the taskbar's empty area, the way
[taskbar-monitor](https://github.com/leandrosa81/taskbar-monitor),
[TrafficMonitor](https://github.com/zhongyang219/TrafficMonitor) and
[ElevenClock](https://github.com/marticliment/ElevenClock) do.

## 2. Research: how existing tools do it

Source read in full (cloned at `~/side-project/taskbar-monitor`; **GPL-3.0 —
technique reference only, no code may be copied**).

taskbar-monitor is really two implementations:

| | Windows 10 (`TaskbarMonitor/`) | Windows 11 (`TaskbarMonitorWindows11/`) |
|---|---|---|
| Mechanism | COM **DeskBand** (CSDeskBand) | WinForms tray app; control **`SetParent`-ed into `Shell_TrayWnd`** (`TaskbarManager.cs:268`) |
| Position | shell-managed | manual: right-aligned, offset by the `TrayNotifyWnd` rect (`TaskbarManager.cs:100-104`) |
| Tracking | shell-managed | `SetWinEventHook` LocationChange + Destroy (re-embed on explorer restart, `TaskbarManager.cs:385-409`) **plus a 4 s polling timer as a belt** (`TaskbarManager.cs:32`) |
| Win11 ≥ 22621 extras | n/a | `WS_EX_LAYERED \| WS_EX_TRANSPARENT` + color-key hack to stay visible (`TaskbarManager.cs:271-274`) — the control becomes click-through |

Takeaways:

- **DeskBand is dead.** It was the last supported taskbar-extension API and
  Windows 11 removed it. Not an option.
- **`SetParent` embedding works but is the fragile path.** Even its own author
  doesn't trust the event hooks and re-asserts on a 4-second timer; each
  Win11 feature update (the XAML taskbar rewrite, 22621's input changes)
  broke it and required new ex-style hacks that cost interactivity. The
  project's issue tracker (51 open) is dominated by "invisible after
  update" reports.
- **ElevenClock proves the third path**: an independent frameless topmost
  window *positioned over* the taskbar, never re-parented. It shipped to a
  large user base across many Win11 builds. Failure mode is graceful — worst
  case the pill is misplaced, never swallowed by an explorer repaint.
- `Shell_TrayWnd` / `TrayNotifyWnd` window classes still exist on Win11
  (the Win11 fork queries them, `TaskbarManager.cs:157-163`) and have been
  the stable anchor across every build so far; we need them only for
  *rects*, not as a parent.

## 3. Options

**A. `SetParent` into `Shell_TrayWnd`** (taskbar-monitor / TrafficMonitor)
Auto-follows the taskbar, shell clips and z-orders for us. But a WinUI 3
window (bridge windows, InputSite) almost certainly cannot survive a
cross-process re-parent into explorer, so this path forces a second
rendering stack (raw HWND + GDI/D2D) that shares nothing with the app; and
it inherits the click-through layered hack plus the break-on-every-update
maintenance profile above.

**B. Independent topmost overlay window** (ElevenClock)
Frameless `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` topmost WinUI 3 window whose
rect is computed from `Shell_TrayWnd`/`TrayNotifyWnd`. Reuses everything the
flyout already proved out: popup chrome + DWM rounded corners
(`FlyoutWindow.ApplyPopupChrome`), PerMonitorV2 DPI, focusless-window input
lessons, `IsSystemDark()` theming, `TrayFeed` data. We own position tracking,
explorer-restart recovery, and fullscreen avoidance.

**Decision: B.** Same conclusion for both axes that matter: it reuses the
existing WinUI stack instead of adding a GDI one, and its dependency on
explorer internals is read-only (two window rects) rather than structural
(living inside explorer's window tree).

## 4. Design

New `TaskbarWidgetWindow` owned by `TrayService` (the widget is a second face
of the tray, sharing its `TrayFeed` and lifetime).

### Window

- WinUI 3 `Window`, borderless via the flyout's `ApplyPopupChrome` recipe
  (extracted to a shared helper), plus `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`,
  `IsShownInSwitchers = false`, `HWND_TOPMOST`.
- Small "pill": DWM `DWMWCP_ROUNDSMALL` corners, opaque background from the
  taskbar theme (`IsSystemDark()`), **no attempt to fake Mica** — a deliberate
  pill reads intentional; a near-miss texture clone reads broken.
- Content: the selected `TrayMode`'s full `Title()` string (the icon truncates
  via `IconTitle`; the widget doesn't need to), quota gauge color accent when
  in `QuotaLeft` mode. One `TextBlock` + optional colored dot — no lens
  content on the taskbar. When `TrayMode.Title()` is temporarily empty,
  including before the first async refresh, the widget remains hidden; it never
  shows a loading placeholder or blank pill. The first non-empty title shows it,
  and a later empty title hides it again. `TrayMode.Hidden` / “Icon only” follows
  the same rule. The existing toggle remains available, and switching back to a
  displayable mode restores the widget.
- Input: left-click → `FlyoutWindow.ToggleFlyout()` (pointer events arrive
  without activation). Hover tooltip (`HoverTip` reuse) and right-click menu
  are Phase 2.

### Placement

Placement is supported only for a horizontal primary taskbar. Read the
`Shell_TrayWnd` rect orientation first; on Windows 10, a left or right vertical
taskbar hides the widget rather than applying the horizontal formula.

For a horizontal taskbar, first compute a candidate rect next to the system
tray, then obtain a verifiable task-list/overflow available boundary. If the
candidate intersects the task-list host or its controls, hide the widget;
show it again when space returns. If Gate 0 cannot reliably resolve that
boundary on a Windows build, fail closed and hide the widget; that is a No-Go,
not permission to cover clickable icons.

```
candidate.right  = TrayNotifyWnd.rect.left − gap   (physical px)
candidate.centerY = Shell_TrayWnd.rect.centerY
candidate.height ≈ taskbar height − 2·margin; width fits the text
```

All inputs are physical-pixel rects from `GetWindowRect`, so placement needs
no DIP conversion; text scale comes from `GetDpiForWindow` as usual
(PerMonitorV2 manifest already in place). `Shell_SecondaryTrayWnd` is Phase 2.

### Tracking & resilience

| Event | Mechanism | Response |
|---|---|---|
| Taskbar/tray/task-list rect moves (resolution, DPI, auto-hide slide, tray icons, overflow/task-list controls) | `SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE)` filtered to the taskbar, tray, task-list host, and overflow-relevant HWNDs | re-evaluate orientation, candidate rect, and available boundary; reposition or hide |
| Explorer restart | `RegisterWindowMessage("TaskbarCreated")` broadcast, received by subclassing the widget HWND (`SetWindowSubclass`) | re-resolve HWNDs, reposition, re-assert topmost |
| Fullscreen app on the same monitor | `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` switches the current foreground HWND; `SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE)` re-evaluates the taskbar, tray, and current foreground HWND rects | hide/show immediately when the same HWND enters or leaves browser/video fullscreen |
| Windows system theme setting changes | subscribe to the OS theme/settings notification that covers `SystemUsesLightTheme`, rather than relying only on WinUI app-theme notifications | re-read `IsSystemDark()` and rerender the pill colors even when the title is unchanged |
| Auto-hide taskbar | falls out of the LocationChange handling: taskbar rect mostly off-screen → hide widget | follow taskbar visibility |
| Anything the hooks miss | piggyback `TrayFeed`'s existing 30 s fast tick with a cheap rect re-check — **no new polling timer** | self-heal |

No `SetParent`, no layered color-key, no injected hooks beyond the two
out-of-context WinEvent hooks: foreground changes select the current HWND,
and location changes re-evaluate the taskbar, tray, and that foreground HWND.
The app already ships a WH_MOUSE_LL precedent.

### Settings & lifecycle

- `tokenbar.taskbar.widget` (bool, **default off**). One toggle in Settings;
  the widget reuses the existing "Menu bar shows" mode selection rather than
  growing its own. In `TrayMode.Hidden` / “Icon only”, Settings keeps the
  toggle available but shows a short helper that the widget stays hidden until
  a displayable mode is selected; no separate widget mode is introduced.
- `TrayService` creates/destroys the window on the setting change, same
  pattern as the animator. Destruction is idempotent: it calls
  `UnhookWinEvent` for both hooks before releasing their callback delegates or
  widget state, then removes the subclass and destroys the HWND. Re-enabling
  creates one fresh hook pair, so repeated toggles cannot accumulate
  registrations. Tray icon behavior is completely unchanged — it remains the
  fallback and the only surface when the widget is off or hidden.
- This is a **Windows-only divergence from macOS parity** (macOS has a real
  text menu-bar item and needs none of this), same bucket as the parked
  global hotkey — hence opt-in, and quota/agent-limit content follows
  whatever the paused macOS contract lands on, not this proposal.

## 5. Phasing

**Gate 0 — spike (Go/No-Go, ~1 day).** Bare window, hardcoded text, full
placement + tracking logic. Run the verification matrix below on the real
x64 box and the ARM64 VM. This is where the approach earns the right to
product code — mirrors the `spike/RESULTS.md` precedent from the 3D work.

**Phase 1 — product widget.** Setting, `TrayFeed` wiring, mode-driven text,
theme, click-to-flyout. Ships behind the default-off toggle.

**Phase 2 — polish (optional, demand-driven).** Hover tooltip, right-click
menu, secondary taskbars, gauge rendering, entrance animation.

### Gate 0 verification matrix

| Axis | Cases |
|---|---|
| DPI | 100% / 125% / 150%, live DPI change |
| Taskbar | centered / left-aligned icons; auto-hide on/off; tray icon count change (rect shift); Windows 10 left/right vertical taskbar → hide and do not cover controls |
| Resilience | `taskkill /f /im explorer.exe` + restart → widget re-appears correctly ≤ 2 s |
| Fullscreen | video fullscreen + a game/borderless window → widget hides, returns on exit |
| Theme | light / dark switch live |
| Crowding | many open windows and overflow pressure → hide before the candidate reaches task-list/overflow controls; show again when space returns |
| Arch | x64 physical + ARM64 VM (RDP resolution changes included) |

## 6. Risks

| Risk | Mitigation |
|---|---|
| Windows update changes taskbar internals | We depend on verifiable taskbar, tray, and task-list/overflow *rects*; if the available boundary cannot be resolved, the widget fails closed and hides. Failure mode: widget unavailable; tray icon unaffected. Default-off + toggle = instant user-side rollback. |
| Topmost z-order battles (shell re-asserts taskbar above us) | Re-assert `HWND_TOPMOST` on every hook event; verified in Gate 0. |
| Widget floats over fullscreen video (classic overlay bug) | Foreground-fullscreen detection is a Gate 0 pass/fail criterion, not a nice-to-have. |
| WinUI window per-monitor quirks when the taskbar is on a secondary display | v1 is primary-taskbar-only; explicit non-goal until Phase 2. |
| GPL contamination from the reference repo | No code copied — API technique only (window classes, WinEvent usage are public Win32 surface). |

## 7. Non-goals

- DeskBand (removed from Windows 11).
- `SetParent` embedding / living inside explorer's window tree.
- Faking the taskbar's Mica material.
- Replacing the tray icon — the widget is additive and opt-in.
- Vertical primary taskbars — v1 supports horizontal primary taskbars only.
- Graphs/charts on the taskbar (taskbar-monitor's whole point, not ours —
  our rich surface is the flyout, one click away).
