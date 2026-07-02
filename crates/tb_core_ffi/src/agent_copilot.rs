//! GitHub Copilot quota — ported from codexbar's CopilotUsageFetcher.
//!
//! Copilot has no token-usage log, but GitHub exposes a per-account quota at
//! `/copilot_internal/user` (premium interactions + chat, as percent-remaining
//! snapshots). We authenticate with the GitHub OAuth token opencode already
//! stored for its Copilot login (`~/.local/share/opencode/auth.json`), so the
//! card appears whenever Copilot is signed in there. Maps to `UsageWindow`s.

use crate::agent_usage::{clean_plan, AgentIdentity, UsageWindow};
use chrono::{DateTime, NaiveDate, TimeZone, Utc};
use serde::Deserialize;

const COPILOT_USAGE_URL: &str = "https://api.github.com/copilot_internal/user";

pub(crate) struct CopilotData {
    pub identity: Option<AgentIdentity>,
    pub windows: Vec<UsageWindow>,
}

#[derive(Debug, Deserialize)]
struct CopilotUser {
    #[serde(default)]
    copilot_plan: Option<String>,
    #[serde(default)]
    quota_reset_date: Option<String>,
    #[serde(default)]
    quota_snapshots: Option<QuotaSnapshots>,
}

#[derive(Debug, Deserialize)]
struct QuotaSnapshots {
    #[serde(default)]
    premium_interactions: Option<QuotaSnapshot>,
    #[serde(default)]
    chat: Option<QuotaSnapshot>,
}

#[derive(Debug, Deserialize)]
struct QuotaSnapshot {
    #[serde(default)]
    entitlement: f64,
    #[serde(default)]
    remaining: f64,
    #[serde(default)]
    percent_remaining: Option<f64>,
}

pub(crate) async fn fetch(now: DateTime<Utc>) -> Result<CopilotData, String> {
    let token = crate::opencode_integrations::github_copilot_token()
        .ok_or_else(|| "GitHub Copilot not signed in (no opencode github-copilot auth).".to_string())?;

    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(30))
        .build()
        .map_err(|e| format!("build Copilot client: {e}"))?;
    let response = client
        .get(COPILOT_USAGE_URL)
        .header(reqwest::header::AUTHORIZATION, format!("token {token}"))
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
    let usage: CopilotUser =
        serde_json::from_str(&body).map_err(|e| format!("decode Copilot usage: {e}"))?;

    let resets_at = usage
        .quota_reset_date
        .as_deref()
        .and_then(parse_reset_date);
    let snapshots = usage.quota_snapshots;
    let mut windows = Vec::new();
    if let Some(snapshots) = snapshots {
        if let Some(window) = snapshot_window("Premium", snapshots.premium_interactions, resets_at, now) {
            windows.push(window);
        }
        if let Some(window) = snapshot_window("Chat", snapshots.chat, resets_at, now) {
            windows.push(window);
        }
    }

    Ok(CopilotData {
        identity: Some(AgentIdentity {
            email: None,
            plan: usage.copilot_plan.filter(|s| !s.trim().is_empty()).map(clean_plan),
        }),
        windows,
    })
}

fn snapshot_window(
    label: &str,
    snapshot: Option<QuotaSnapshot>,
    resets_at: Option<DateTime<Utc>>,
    now: DateTime<Utc>,
) -> Option<UsageWindow> {
    let snapshot = snapshot?;
    // Skip explicit zero-entitlement placeholders (no usable quota signal).
    if snapshot.entitlement == 0.0 && snapshot.remaining == 0.0 && snapshot.percent_remaining.is_none() {
        return None;
    }
    let percent_remaining = snapshot.percent_remaining.or_else(|| {
        (snapshot.entitlement > 0.0).then(|| (snapshot.remaining / snapshot.entitlement) * 100.0)
    })?;
    Some(UsageWindow::from_fraction(
        label.to_string(),
        percent_remaining / 100.0,
        resets_at,
        now,
    ))
}

/// Copilot reports `quota_reset_date` as a bare `YYYY-MM-DD`; treat it as UTC midnight.
fn parse_reset_date(value: &str) -> Option<DateTime<Utc>> {
    let date = NaiveDate::parse_from_str(value.trim(), "%Y-%m-%d").ok()?;
    Utc.from_utc_datetime(&date.and_hms_opt(0, 0, 0)?).into()
}

#[cfg(test)]
mod tests {
    use super::*;

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
        assert!((premium.remaining_for_test() - 30.0).abs() < 0.01);
        // chat is a zero-entitlement placeholder → skipped
        assert!(snapshot_window("Chat", snaps.chat, None, now).is_none());
    }

    #[test]
    fn parses_reset_date() {
        assert!(parse_reset_date("2026-07-01").is_some());
        assert!(parse_reset_date("not-a-date").is_none());
    }
}
