# Verification history

Historical gate records that used to live inline in `README.md`'s `## Build` section.
They describe how a past gate was satisfied, not how to build the app today — see the
[README](../README.md#build) for the current build commands.

## Portable-release gates (Phase 10, `v0.1.0` and earlier)

The M19-B1 real-ARM64 gate result, the `v0.1.0-preview.1` non-disposable active-profile
exception, and the `tray-ready`/exit-`0` startup-smoke results for both the preview and
`v0.1.0` stable are recorded in full in [`docs/release.md`](release.md) (Startup gate and
Published stable outcome sections) — they are not duplicated here.

## CI record predating the Phase 10 gates

The provider-pace v3 branch passed local command-equivalent x64 and ARM64 gates plus fresh
review on 2026-07-19. [GitHub PR #3](https://github.com/Nanako0129/TokenBar-Windows/pull/3)
preserves its remote CI and review record.

## Provider runtime validation (as of the `v0.2.2` release cycle)

Per-provider live-credential validation on Windows, run against the shared engine pinned at
the time:

| Provider | Windows live status |
|---|---|
| Claude | Exact-head live Smoke passed from the signed-in Windows profile with source `oauth` and 2 quota cards. |
| Copilot | Exact-head live Smoke passed from the signed-in Windows profile with source `oauth` and 2 quota cards. |
| Antigravity | Exact-head local IDE path passed with `HOME` absent and source `cli`: 8 quota cards with no provider error. Windows 2.3.1 installed-client discovery also paired the expected `resources/bin/language_server.exe` artifact without exposing or persisting embedded client values. Remote OAuth live coverage remains unavailable because this profile has no `~/.gemini/oauth_creds.json`; that path remains covered hermetically. |
| Codex | Logout/login restored the Windows credential; exact-head live Smoke passed with source `oauth` and 2 quota cards. |
| Grok | Exact-head live Smoke accepted the unified-billing response with source `oauth`. The account currently produces 0 active quota windows, replacing the former `response_shape` failure with a successful recognized-disabled result; raw billing values were not logged. |

Fixture cross-checks, synthetic session smokes, and path-only checks are kept
separate from credential-bound live coverage. The final live gate retained the
same exists/hash state across all 7 monitored credential and account-scope
paths; normal secure v3 pace-history writes remain permitted. Session-parser
environment-root overrides now flow through one shared FFI source context and
have strict relocated-`CODEX_HOME` coverage. RID-aware native-DLL selection and
freshness are complete through the shared `BuildTbNative`/`Directory.Build.targets`
path.
