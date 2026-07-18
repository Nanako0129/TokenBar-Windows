//! GitHub Copilot quota — ported from codexbar's CopilotUsageFetcher.
//!
//! Copilot has no token-usage log, but GitHub exposes a per-account quota at
//! `/copilot_internal/user` (premium interactions + chat, as percent-remaining
//! snapshots). We authenticate with the GitHub OAuth token opencode already
//! stored for its Copilot login (`~/.local/share/opencode/auth.json`), so the
//! card appears whenever Copilot is signed in there. Maps to `UsageWindow`s.

use crate::agent_account_scope::{self, AccountScope, AccountScopeError};
use crate::agent_quota_duration::{copilot_calendar_duration, DurationEvidence};
use crate::agent_usage::{clean_plan, AgentIdentity, UsageWindow};
use crate::opencode_integrations::GitHubCopilotCredential;
use chrono::{DateTime, NaiveDate, TimeZone, Utc};
use serde::Deserialize;
use serde_json::value::RawValue;

const COPILOT_USAGE_URL: &str = "https://api.github.com/copilot_internal/user";

pub(crate) struct CopilotData {
    pub identity: Option<AgentIdentity>,
    pub account_scope: Result<AccountScope, AccountScopeError>,
    pub windows: Vec<UsageWindow>,
}

#[derive(Debug, Deserialize)]
struct CopilotUser {
    #[serde(default)]
    copilot_plan: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    quota_reset_date: Option<Box<RawValue>>,
    #[serde(default)]
    quota_snapshots: Option<QuotaSnapshots>,
}

#[derive(Debug, Clone, Deserialize)]
struct QuotaSnapshots {
    #[serde(default)]
    premium_interactions: Option<QuotaSnapshot>,
    #[serde(default)]
    chat: Option<QuotaSnapshot>,
}

#[derive(Debug, Clone, Deserialize)]
struct QuotaSnapshot {
    #[serde(default)]
    entitlement: f64,
    #[serde(default)]
    remaining: f64,
    #[serde(default)]
    percent_remaining: Option<f64>,
}

pub(crate) async fn fetch(
    now: DateTime<Utc>,
    credential: GitHubCopilotCredential,
) -> Result<CopilotData, String> {
    fetch_with(
        now,
        credential,
        request_usage,
        |semantic_source, canonical_location, marker| {
            agent_account_scope::resolve_credential(
                "copilot",
                semantic_source,
                canonical_location,
                marker,
            )
        },
    )
    .await
}

async fn request_usage(request_token: String) -> Result<CopilotUser, String> {
    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(30))
        .build()
        .map_err(|e| format!("build Copilot client: {e}"))?;
    let response = client
        .get(COPILOT_USAGE_URL)
        .header(reqwest::header::AUTHORIZATION, format!("token {request_token}"))
        .header(reqwest::header::ACCEPT, "application/json")
        .header(reqwest::header::USER_AGENT, "GitHubCopilotChat/0.26.7")
        .header("Editor-Version", "vscode/1.96.2")
        .header("Editor-Plugin-Version", "copilot-chat/0.26.7")
        .header("X-Github-Api-Version", "2025-04-01")
        .send()
        .await
        .map_err(|e| format!("Copilot usage request failed: {e}"))?;
    let status = response.status();
    if status == reqwest::StatusCode::UNAUTHORIZED || status == reqwest::StatusCode::FORBIDDEN {
        return Err("GitHub Copilot token expired or lacks access.".to_string());
    }
    if !status.is_success() {
        return Err(format!("Copilot usage API returned {}.", status.as_u16()));
    }
    let body = response
        .text()
        .await
        .map_err(|e| format!("read Copilot response: {e}"))?;
    serde_json::from_str(&body).map_err(|e| format!("decode Copilot usage: {e}"))
}

async fn fetch_with<Request, RequestFuture, ResolveScope>(
    now: DateTime<Utc>,
    credential: GitHubCopilotCredential,
    request: Request,
    resolve_scope: ResolveScope,
) -> Result<CopilotData, String>
where
    Request: FnOnce(String) -> RequestFuture,
    RequestFuture: std::future::Future<Output = Result<CopilotUser, String>>,
    ResolveScope: FnOnce(&str, &str, &[u8]) -> Result<AccountScope, AccountScopeError>,
{
    let GitHubCopilotCredential {
        request_token,
        marker,
        semantic_source,
        canonical_location,
    } = credential;
    let usage = request(request_token).await?;
    let windows = snapshot_windows(&usage, now);
    let account_scope = resolve_scope(semantic_source, &canonical_location, &marker);
    Ok(CopilotData {
        identity: Some(AgentIdentity {
            email: None,
            plan: usage.copilot_plan.filter(|s| !s.trim().is_empty()).map(clean_plan),
        }),
        account_scope,
        windows,
    })
}

fn snapshot_windows(usage: &CopilotUser, now: DateTime<Utc>) -> Vec<UsageWindow> {
    let resets_at = usage.quota_reset_date.as_deref().and_then(parse_reset_raw);
    let reset_was_supplied = usage.quota_reset_date.is_some();
    let Some(snapshots) = usage.quota_snapshots.as_ref() else {
        return Vec::new();
    };
    [
        snapshot_window_with_identity(
            "Premium",
            "premium_interactions.v1",
            Some("premium_interactions.v1".to_string()),
            snapshots.premium_interactions.clone(),
            resets_at,
            reset_was_supplied,
            now,
        ),
        snapshot_window_with_identity(
            "Chat",
            "chat.v1",
            Some("chat.v1".to_string()),
            snapshots.chat.clone(),
            resets_at,
            reset_was_supplied,
            now,
        ),
    ]
    .into_iter()
    .flatten()
    .collect()
}

fn snapshot_window_with_identity(
    label: &str,
    card_id: &str,
    window_key: Option<String>,
    snapshot: Option<QuotaSnapshot>,
    resets_at: Option<DateTime<Utc>>,
    reset_was_supplied: bool,
    now: DateTime<Utc>,
) -> Option<UsageWindow> {
    let snapshot = snapshot?;
    // Skip explicit zero-entitlement placeholders (no usable quota signal).
    if snapshot.entitlement == 0.0
        && snapshot.remaining == 0.0
        && snapshot.percent_remaining.is_none()
    {
        return None;
    }
    let percent_remaining = snapshot.percent_remaining.or_else(|| {
        (snapshot.entitlement > 0.0).then(|| (snapshot.remaining / snapshot.entitlement) * 100.0)
    })?;
    if !percent_remaining.is_finite() || !(0.0..=100.0).contains(&percent_remaining) {
        return None;
    }

    let window = UsageWindow::from_fraction(
        label.to_string(),
        percent_remaining / 100.0,
        resets_at,
        now,
    )
    .with_identity(card_id, window_key);
    let contract_duration = resets_at.and_then(|reset| copilot_calendar_duration(reset.timestamp()));
    Some(match contract_duration {
        Some(duration) => window.with_contract_duration_evidence(
            now,
            reset_was_supplied,
            DurationEvidence::contract(duration),
        ),
        None => window.with_observed_duration_evidence(now, reset_was_supplied),
    })
}

#[cfg(test)]
fn snapshot_window(
    label: &str,
    snapshot: Option<QuotaSnapshot>,
    resets_at: Option<DateTime<Utc>>,
    now: DateTime<Utc>,
) -> Option<UsageWindow> {
    let (card_id, window_key) = match label {
        "Premium" => ("premium_interactions.v1", Some("premium_interactions.v1".to_string())),
        "Chat" => ("chat.v1", Some("chat.v1".to_string())),
        _ => ("row.copilot.unknown.v1", None),
    };
    snapshot_window_with_identity(
        label,
        card_id,
        window_key,
        snapshot,
        resets_at,
        resets_at.is_some(),
        now,
    )
}

fn deserialize_optional_raw<'de, D>(deserializer: D) -> Result<Option<Box<RawValue>>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    Box::<RawValue>::deserialize(deserializer).map(Some)
}

fn parse_reset_raw(raw: &RawValue) -> Option<DateTime<Utc>> {
    let value = serde_json::from_str::<String>(raw.get()).ok()?;
    parse_reset_date(&value)
}

/// Copilot reports `quota_reset_date` as a bare `YYYY-MM-DD`; treat it as UTC midnight.
fn parse_reset_date(value: &str) -> Option<DateTime<Utc>> {
    let value = value.trim();
    let bytes = value.as_bytes();
    if bytes.len() != 10
        || bytes[4] != b'-'
        || bytes[7] != b'-'
        || !bytes[..4].iter().all(u8::is_ascii_digit)
        || !bytes[5..7].iter().all(u8::is_ascii_digit)
        || !bytes[8..].iter().all(u8::is_ascii_digit)
    {
        return None;
    }
    let date = NaiveDate::parse_from_str(value, "%Y-%m-%d").ok()?;
    Some(Utc.from_utc_datetime(&date.and_hms_opt(0, 0, 0)?))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::agent_account_scope::test_support::TestRefreshScope;
    use crate::agent_account_scope::RefreshScopeTransaction;
    use serde_json::json;
    use std::sync::atomic::{AtomicU64, Ordering};

    static TEMP_COUNTER: AtomicU64 = AtomicU64::new(0);

    fn temp_auth_path(tag: &str) -> std::path::PathBuf {
        let root = std::env::temp_dir().join(format!(
            "tb-copilot-{tag}-{}-{}",
            std::process::id(),
            TEMP_COUNTER.fetch_add(1, Ordering::Relaxed)
        ));
        std::fs::create_dir_all(&root).unwrap();
        root.join("auth.json")
    }

    fn fixture_credential(path: &std::path::Path, marker: &str) -> GitHubCopilotCredential {
        let json = json!({"github-copilot": {
            "type": "oauth", "refresh": marker, "access": "fake-access-token"
        }});
        std::fs::write(path, serde_json::to_vec(&json).unwrap()).unwrap();
        crate::opencode_integrations::github_copilot_credential_from(path, &json).unwrap()
    }

    fn fixture_user() -> CopilotUser {
        serde_json::from_value(json!({
            "copilot_plan": "individual",
            "quota_reset_date": "2026-08-01",
            "quota_snapshots": {
                "premium_interactions": {
                    "entitlement": 300, "remaining": 90, "percent_remaining": 30
                },
                "chat": {"entitlement": 100, "remaining": 75, "percent_remaining": 75}
            }
        }))
        .unwrap()
    }

    #[test]
    fn maps_premium_and_chat_snapshots() {
        let now = Utc::now();
        let body = r#"{
            "copilot_plan": "individual",
            "quota_reset_date": "2026-07-01",
            "quota_snapshots": {
                "premium_interactions": { "entitlement": 300, "remaining": 90, "percent_remaining": 30 },
                "chat": { "entitlement": 0, "remaining": 0 }
            }
        }"#;
        let usage: CopilotUser = serde_json::from_str(body).unwrap();
        let snaps = usage.quota_snapshots.unwrap();
        let premium = snapshot_window("Premium", snaps.premium_interactions, None, now).unwrap();
        assert_eq!(premium.card_id_for_test(), "premium_interactions.v1");
        assert_eq!(premium.pace_window_key_for_test(), Some("premium_interactions.v1"));
        assert!((premium.remaining_for_test() - 30.0).abs() < 0.01);
        // chat is a zero-entitlement placeholder → skipped
        assert!(snapshot_window("Chat", snaps.chat, None, now).is_none());
    }

    #[test]
    fn maps_exact_calendar_duration_for_each_month_length_on_both_cards() {
        let cases = [
            ("2023-03-01", "2023-02-15T00:00:00Z", 28 * 86_400),
            ("2024-03-01", "2024-02-15T00:00:00Z", 29 * 86_400),
            ("2023-05-01", "2023-04-15T00:00:00Z", 30 * 86_400),
            ("2023-08-01", "2023-07-15T00:00:00Z", 31 * 86_400),
        ];
        for (reset, now_text, expected_seconds) in cases {
            let now = now_text.parse::<DateTime<Utc>>().unwrap();
            let usage: CopilotUser = serde_json::from_value(json!({
                "quota_reset_date": reset,
                "quota_snapshots": {
                    "premium_interactions": {
                        "entitlement": 300, "remaining": 90, "percent_remaining": 30
                    },
                    "chat": {"entitlement": 100, "remaining": 75, "percent_remaining": 75}
                }
            }))
            .unwrap();
            assert!(usage.quota_reset_date.is_some());
            let windows = snapshot_windows(&usage, now);
            assert_eq!(windows.len(), 2, "{reset}");
            for (window, card_id) in windows.iter().zip([
                "premium_interactions.v1",
                "chat.v1",
            ]) {
                let wire = serde_json::to_value(window).unwrap();
                assert_eq!(wire["cardId"], card_id, "{reset}");
                assert_eq!(wire["paceStatus"]["state"], "learningHistory", "{reset}");
                assert_eq!(wire["paceStatus"]["durationSource"], "contract", "{reset}");
                assert_eq!(
                    wire["paceStatus"]["durationSeconds"],
                    expected_seconds,
                    "{reset}"
                );
                assert_eq!(wire["windowMinutes"], expected_seconds / 60, "{reset}");
                assert!(wire.get("historicalPace").is_none(), "{reset}");
            }
        }
    }

    #[test]
    fn valid_future_non_month_start_uses_observed_duration_learning() {
        let now = "2023-08-01T00:00:00Z".parse::<DateTime<Utc>>().unwrap();
        let usage: CopilotUser = serde_json::from_value(json!({
            "quota_reset_date": "2023-08-15",
            "quota_snapshots": {
                "premium_interactions": {"entitlement": 300, "remaining": 90},
                "chat": {"entitlement": 100, "remaining": 75}
            }
        }))
        .unwrap();
        let windows = snapshot_windows(&usage, now);
        assert_eq!(windows.len(), 2);
        for window in windows {
            let wire = serde_json::to_value(window).unwrap();
            assert_eq!(wire["paceStatus"]["state"], "learningDuration");
            assert_eq!(wire["paceStatus"]["durationSource"], "observed");
            assert!(wire["paceStatus"].get("durationSeconds").is_none());
            assert!(wire.get("windowMinutes").is_none());
            assert!(wire.get("historicalPace").is_none());
        }
    }

    #[test]
    fn reset_presence_and_validity_fail_closed_without_observed_fallback() {
        let now = "2023-08-15T00:00:00Z".parse::<DateTime<Utc>>().unwrap();
        let cases = [
            ("missing", json!({}), false, "missingReset"),
            (
                "null",
                json!({"quota_reset_date": null}),
                true,
                "invalidEvidence",
            ),
            (
                "object",
                json!({"quota_reset_date": {"reset": "2023-08-01"}}),
                true,
                "invalidEvidence",
            ),
            (
                "number",
                json!({"quota_reset_date": 42}),
                true,
                "invalidEvidence",
            ),
            (
                "bool",
                json!({"quota_reset_date": true}),
                true,
                "invalidEvidence",
            ),
            (
                "blank",
                json!({"quota_reset_date": ""}),
                true,
                "invalidEvidence",
            ),
            (
                "malformed",
                json!({"quota_reset_date": "not-a-date"}),
                true,
                "invalidEvidence",
            ),
            (
                "past",
                json!({"quota_reset_date": "2023-08-01"}),
                true,
                "invalidEvidence",
            ),
        ];
        for (case, reset, supplied, reason) in cases {
            let mut body = json!({
                "quota_snapshots": {
                    "premium_interactions": {"entitlement": 300, "remaining": 90},
                    "chat": {"entitlement": 100, "remaining": 75}
                }
            });
            if let Some(reset) = reset.get("quota_reset_date") {
                body["quota_reset_date"] = reset.clone();
            }
            let usage: CopilotUser = serde_json::from_value(body).unwrap();
            assert_eq!(usage.quota_reset_date.is_some(), supplied, "{case}");
            let windows = snapshot_windows(&usage, now);
            assert_eq!(windows.len(), 2, "{case}");
            for window in windows {
                let wire = serde_json::to_value(window).unwrap();
                assert_eq!(wire["paceStatus"]["state"], "unavailable", "{case}");
                assert_eq!(wire["paceStatus"]["reason"], reason, "{case}");
                assert!(wire["paceStatus"].get("durationSeconds").is_none(), "{case}");
                assert!(wire.get("windowMinutes").is_none(), "{case}");
                assert!(wire.get("historicalPace").is_none(), "{case}");
            }
        }

        let early_now = "2023-06-15T00:00:00Z".parse::<DateTime<Utc>>().unwrap();
        let future_calendar: CopilotUser = serde_json::from_value(json!({
            "quota_reset_date": "2023-08-01",
            "quota_snapshots": {
                "premium_interactions": {"entitlement": 300, "remaining": 90},
                "chat": {"entitlement": 100, "remaining": 75}
            }
        }))
        .unwrap();
        for window in snapshot_windows(&future_calendar, early_now) {
            let wire = serde_json::to_value(window).unwrap();
            assert_eq!(wire["paceStatus"]["state"], "unavailable");
            assert_eq!(wire["paceStatus"]["reason"], "invalidEvidence");
        }
    }

    #[tokio::test]
    async fn request_and_lineage_use_the_same_normalized_marker() {
        let path = temp_auth_path("lineage");
        let store = TestRefreshScope::new("copilot", "copilot-lineage");
        let now = Utc.timestamp_opt(1_751_328_000, 0).single().unwrap();
        let events = std::cell::RefCell::new(Vec::new());
        let first = fetch_with(
            now,
            fixture_credential(&path, " stable-marker "),
            |token| {
                events.borrow_mut().push("request");
                assert_eq!(token, "stable-marker");
                std::future::ready(Ok(fixture_user()))
            },
            |source, location, marker| {
                events.borrow_mut().push("scope");
                assert_eq!(source, "opencode-auth-json");
                assert!(location.ends_with("\0github-copilot"));
                assert_eq!(marker, b"stable-marker");
                store.resolve_current(source, location, marker)
            },
        )
        .await
        .unwrap();
        assert_eq!(&*events.borrow(), &["request", "scope"]);
        assert_eq!(first.windows.len(), 2);
        assert_eq!(
            first.windows[0].pace_window_key_for_test(),
            Some("premium_interactions.v1")
        );
        assert_eq!(first.windows[1].pace_window_key_for_test(), Some("chat.v1"));
        let first_scope = first.account_scope.unwrap();
        let normalized_scope = fetch_with(
            now,
            fixture_credential(&path, "stable-marker"),
            |_| std::future::ready(Ok(fixture_user())),
            |source, location, marker| store.resolve_current(source, location, marker),
        )
        .await
        .unwrap()
        .account_scope
        .unwrap();
        let different_scope = fetch_with(
            now,
            fixture_credential(&path, "different-marker"),
            |_| std::future::ready(Ok(fixture_user())),
            |source, location, marker| store.resolve_current(source, location, marker),
        )
        .await
        .unwrap()
        .account_scope
        .unwrap();
        assert_eq!(first_scope, normalized_scope);
        assert_ne!(first_scope, different_scope);
        store.cleanup();
        let _ = std::fs::remove_dir_all(path.parent().unwrap());
    }

    #[tokio::test]
    async fn api_and_scope_errors_fail_closed_without_losing_successful_gauges() {
        let path = temp_auth_path("errors");
        let scope_calls = std::cell::Cell::new(0);
        let result = fetch_with(
            Utc::now(),
            fixture_credential(&path, " api-error-secret "),
            |token| {
                assert_eq!(token, "api-error-secret");
                std::future::ready(Err("Copilot usage API returned 503.".to_string()))
            },
            |_, _, _| {
                scope_calls.set(scope_calls.get() + 1);
                Err(AccountScopeError::MetadataWrite)
            },
        )
        .await;
        let error = match result {
            Ok(_) => panic!("API failure unexpectedly succeeded"),
            Err(error) => error,
        };
        assert_eq!(scope_calls.get(), 0);
        assert_eq!(error, "Copilot usage API returned 503.");
        assert!(!error.contains("api-error-secret"));
        assert!(!error.contains(path.to_string_lossy().as_ref()));

        let data = fetch_with(
            Utc::now(),
            fixture_credential(&path, " scope-error-secret "),
            |_| std::future::ready(Ok(fixture_user())),
            |source, location, marker| {
                assert_eq!(source, "opencode-auth-json");
                assert!(location.ends_with("\0github-copilot"));
                assert_eq!(marker, b"scope-error-secret");
                Err(AccountScopeError::MetadataWrite)
            },
        )
        .await
        .unwrap();
        assert_eq!(data.account_scope, Err(AccountScopeError::MetadataWrite));
        assert_eq!(data.windows.len(), 2);
        let _ = std::fs::remove_dir_all(path.parent().unwrap());
    }

    #[test]
    fn unknown_test_window_has_no_history_identity() {
        let window = snapshot_window(
            "Other",
            Some(QuotaSnapshot {
                entitlement: 10.0,
                remaining: 5.0,
                percent_remaining: None,
            }),
            None,
            Utc::now(),
        )
        .unwrap();
        assert_eq!(window.card_id_for_test(), "row.copilot.unknown.v1");
        assert!(window.pace_window_key_for_test().is_none());
        assert_eq!(window.pace_reason_for_test(), Some("windowIdentity"));
    }

    #[test]
    fn parses_only_trimmed_exact_utc_calendar_dates() {
        assert_eq!(
            parse_reset_date(" 2026-07-01 \t"),
            Some("2026-07-01T00:00:00Z".parse::<DateTime<Utc>>().unwrap())
        );
        for invalid in [
            "not-a-date",
            "2026-7-01",
            "2026-07-1",
            "2026-07-01T00:00:00Z",
            "2026/07/01",
            "2026-02-29",
        ] {
            assert!(parse_reset_date(invalid).is_none(), "{invalid}");
        }
    }
}
