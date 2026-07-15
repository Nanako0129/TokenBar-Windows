//! Grok Build subscription quota (weekly SuperGrok credits).
//!
//! Grok Build stores OIDC credentials at `$GROK_HOME/auth.json` (default
//! `~/.grok/auth.json`). TokenBar refreshes the access token against
//! `auth.x.ai` and reads weekly credit usage from the same private billing
//! endpoint the CLI uses:
//!
//!   GET https://cli-chat-proxy.grok.com/v1/billing?format=credits
//!
//! Prefer the `GrokBuild` product percent when present; fall back to overall
//! `creditUsagePercent`. Omit the card entirely when no Grok auth is on disk
//! (same stance as Copilot).

use crate::agent_usage::{AgentIdentity, UsageWindow};
use chrono::{DateTime, SecondsFormat, Utc};
use serde::Deserialize;
use serde_json::Value;
use std::fs;
use std::path::{Path, PathBuf};

const GROK_TOKEN_URL: &str = "https://auth.x.ai/oauth2/token";
const GROK_BILLING_URL: &str = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";
/// Refresh a few minutes early so a clock-skewed expiry doesn't 401 the billing call.
const ACCESS_SKEW_SECS: i64 = 120;

pub(crate) struct GrokData {
    pub identity: Option<AgentIdentity>,
    pub windows: Vec<UsageWindow>,
}

#[derive(Debug, Clone)]
struct GrokCredentials {
    auth_path: PathBuf,
    entry_key: String,
    access_token: String,
    refresh_token: String,
    client_id: String,
    expires_at: Option<DateTime<Utc>>,
    email: Option<String>,
    /// Full auth.json so we can patch only this entry and keep siblings intact.
    raw_json: Value,
}

#[derive(Debug, Deserialize)]
struct BillingResponse {
    #[serde(default)]
    config: Option<BillingConfig>,
    /// Present on some older CLI payloads; optional today.
    #[serde(default, rename = "subscriptionTiers")]
    subscription_tiers: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct BillingConfig {
    #[serde(default)]
    current_period: Option<UsagePeriod>,
    #[serde(default)]
    credit_usage_percent: Option<f64>,
    #[serde(default)]
    product_usage: Option<Vec<ProductUsage>>,
    #[serde(default)]
    billing_period_start: Option<String>,
    #[serde(default)]
    billing_period_end: Option<String>,
}

#[derive(Debug, Deserialize)]
struct UsagePeriod {
    #[serde(default, rename = "type")]
    period_type: Option<String>,
    #[serde(default)]
    start: Option<String>,
    #[serde(default)]
    end: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ProductUsage {
    #[serde(default)]
    product: Option<String>,
    #[serde(default)]
    usage_percent: Option<f64>,
}

#[derive(Debug, Deserialize)]
struct TokenResponse {
    access_token: String,
    #[serde(default)]
    refresh_token: Option<String>,
    #[serde(default)]
    expires_in: Option<i64>,
}

/// Fetch Grok quota when local auth exists. Returns `None` when the user has
/// never signed into Grok Build (no card). Returns `Err` when auth exists but
/// the fetch fails so the card can show an error state.
pub(crate) async fn fetch(now: DateTime<Utc>) -> Option<Result<GrokData, String>> {
    let credentials = match load_credentials() {
        Ok(Some(c)) => c,
        Ok(None) => return None,
        Err(e) => return Some(Err(e)),
    };
    Some(fetch_with_credentials(credentials, now).await)
}

async fn fetch_with_credentials(
    mut credentials: GrokCredentials,
    now: DateTime<Utc>,
) -> Result<GrokData, String> {
    if credentials_needs_refresh(&credentials, now) {
        credentials = refresh_credentials(credentials).await?;
    }

    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(30))
        .build()
        .map_err(|e| format!("build Grok billing client: {e}"))?;

    let response = client
        .get(GROK_BILLING_URL)
        .bearer_auth(&credentials.access_token)
        .header(reqwest::header::ACCEPT, "application/json")
        .header(reqwest::header::USER_AGENT, "TokenBar")
        .send()
        .await
        .map_err(|e| format!("Grok billing request failed: {e}"))?;

    let status = response.status();
    let body = response
        .text()
        .await
        .map_err(|e| format!("read Grok billing response: {e}"))?;

    if status == reqwest::StatusCode::UNAUTHORIZED || status == reqwest::StatusCode::FORBIDDEN {
        // One retry after a forced refresh in case the access token was revoked
        // mid-window while the refresh token still works.
        if !credentials.refresh_token.is_empty() {
            credentials = refresh_credentials(credentials).await?;
            let retry = client
                .get(GROK_BILLING_URL)
                .bearer_auth(&credentials.access_token)
                .header(reqwest::header::ACCEPT, "application/json")
                .header(reqwest::header::USER_AGENT, "TokenBar")
                .send()
                .await
                .map_err(|e| format!("Grok billing retry failed: {e}"))?;
            let retry_status = retry.status();
            let retry_body = retry
                .text()
                .await
                .map_err(|e| format!("read Grok billing retry: {e}"))?;
            if !retry_status.is_success() {
                return Err(format!(
                    "Grok billing API returned {}.",
                    retry_status.as_u16()
                ));
            }
            return map_billing(&retry_body, &credentials, now);
        }
        return Err("Grok OAuth token expired or invalid. Run `grok` to log in again.".to_string());
    }
    if !status.is_success() {
        return Err(format!("Grok billing API returned {}.", status.as_u16()));
    }

    map_billing(&body, &credentials, now)
}

fn map_billing(
    body: &str,
    credentials: &GrokCredentials,
    now: DateTime<Utc>,
) -> Result<GrokData, String> {
    let payload: BillingResponse =
        serde_json::from_str(body).map_err(|e| format!("decode Grok billing response: {e}"))?;
    let config = payload
        .config
        .ok_or_else(|| "Grok billing response missing config.".to_string())?;

    let used_percent = used_percent_from_config(&config).ok_or_else(|| {
        "Grok billing response has no creditUsagePercent or GrokBuild usage.".to_string()
    })?;

    let (label, resets_at, window_minutes) = period_meta(&config);
    let window =
        UsageWindow::from_used_percent(label, used_percent, resets_at, now, window_minutes);

    Ok(GrokData {
        identity: Some(AgentIdentity {
            email: credentials.email.clone(),
            plan: payload
                .subscription_tiers
                .filter(|s| !s.trim().is_empty())
                .map(|s| s.trim().to_string()),
        }),
        windows: vec![window],
    })
}

fn used_percent_from_config(config: &BillingConfig) -> Option<f64> {
    if let Some(products) = config.product_usage.as_ref() {
        for product in products {
            let name = product.product.as_deref().unwrap_or("");
            if name.eq_ignore_ascii_case("GrokBuild") {
                if let Some(pct) = product.usage_percent {
                    return Some(pct);
                }
            }
        }
    }
    config.credit_usage_percent
}

fn period_meta(config: &BillingConfig) -> (String, Option<DateTime<Utc>>, Option<i64>) {
    let period_type = config
        .current_period
        .as_ref()
        .and_then(|p| p.period_type.as_deref())
        .unwrap_or("");
    let label = if period_type.contains("WEEKLY") {
        "Weekly".to_string()
    } else if period_type.contains("MONTHLY") {
        "Monthly".to_string()
    } else {
        "Weekly".to_string()
    };

    let start = config
        .current_period
        .as_ref()
        .and_then(|p| p.start.as_deref())
        .or(config.billing_period_start.as_deref())
        .and_then(parse_timestamp);
    let end = config
        .current_period
        .as_ref()
        .and_then(|p| p.end.as_deref())
        .or(config.billing_period_end.as_deref())
        .and_then(parse_timestamp);

    let window_minutes = match (start, end) {
        (Some(s), Some(e)) => {
            let mins = (e - s).num_minutes();
            (mins > 0).then_some(mins)
        }
        _ => None,
    };

    (label, end, window_minutes)
}

fn parse_timestamp(value: &str) -> Option<DateTime<Utc>> {
    DateTime::parse_from_rfc3339(value.trim())
        .ok()
        .map(|dt| dt.with_timezone(&Utc))
        .or_else(|| {
            // Accept trailing "Z" variants chrono sometimes chokes on without offset parse.
            chrono::DateTime::parse_from_str(value.trim(), "%Y-%m-%dT%H:%M:%S%.f%z")
                .ok()
                .map(|dt| dt.with_timezone(&Utc))
        })
}

fn credentials_needs_refresh(credentials: &GrokCredentials, now: DateTime<Utc>) -> bool {
    if credentials.access_token.trim().is_empty() {
        return true;
    }
    match credentials.expires_at {
        Some(exp) => exp <= now + chrono::Duration::seconds(ACCESS_SKEW_SECS),
        None => false, // unknown expiry — try current token first
    }
}

async fn refresh_credentials(mut credentials: GrokCredentials) -> Result<GrokCredentials, String> {
    if credentials.refresh_token.trim().is_empty() {
        return Err(
            "Grok OAuth token needs refresh but auth.json has no refresh token.".to_string(),
        );
    }
    if credentials.client_id.trim().is_empty() {
        return Err("Grok auth.json is missing oidc_client_id.".to_string());
    }

    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(30))
        .build()
        .map_err(|e| format!("build Grok token client: {e}"))?;

    let form = [
        ("grant_type", "refresh_token"),
        ("refresh_token", credentials.refresh_token.as_str()),
        ("client_id", credentials.client_id.as_str()),
    ]
    .iter()
    .map(|(k, v)| {
        format!(
            "{}={}",
            crate::agent_usage::percent_encode(k),
            crate::agent_usage::percent_encode(v)
        )
    })
    .collect::<Vec<_>>()
    .join("&");

    let response = client
        .post(GROK_TOKEN_URL)
        .header(reqwest::header::ACCEPT, "application/json")
        .header(reqwest::header::USER_AGENT, "TokenBar")
        .header(
            reqwest::header::CONTENT_TYPE,
            "application/x-www-form-urlencoded",
        )
        .body(form)
        .send()
        .await
        .map_err(|e| format!("Grok token refresh failed: {e}"))?;

    let status = response.status();
    let body = response
        .text()
        .await
        .map_err(|e| format!("read Grok token refresh response: {e}"))?;
    if !status.is_success() {
        return Err(format!(
            "Grok token refresh returned {}. Run `grok` to log in again.",
            status.as_u16()
        ));
    }

    let tokens: TokenResponse =
        serde_json::from_str(&body).map_err(|e| format!("decode Grok token refresh: {e}"))?;
    credentials.access_token = tokens.access_token;
    if let Some(refresh) = tokens.refresh_token.filter(|s| !s.trim().is_empty()) {
        credentials.refresh_token = refresh;
    }
    if let Some(expires_in) = tokens.expires_in {
        credentials.expires_at = Some(Utc::now() + chrono::Duration::seconds(expires_in.max(0)));
    }

    // Grok rotates refresh tokens on refresh: the token we just spent is now
    // dead. Persist the new pair back, or the next refresh — by TokenBar *or* the
    // grok CLI — fails with a stale token, forcing a manual `grok` re-login.
    // Best-effort: a write failure shouldn't sink this usage fetch, but it's
    // worth surfacing in logs (mirrors the Claude write-back in agent_usage.rs).
    if let Err(error) = save_credentials(&credentials) {
        eprintln!("tb_core_ffi: failed to persist refreshed Grok credentials: {error}");
    }

    Ok(credentials)
}

fn load_credentials() -> Result<Option<GrokCredentials>, String> {
    load_credentials_from(&grok_home().join("auth.json"))
}

fn load_credentials_from(auth_path: &Path) -> Result<Option<GrokCredentials>, String> {
    if !auth_path.is_file() {
        return Ok(None);
    }
    let data = fs::read(auth_path).map_err(|e| format!("read Grok auth.json: {e}"))?;
    let raw: Value =
        serde_json::from_slice(&data).map_err(|e| format!("parse Grok auth.json: {e}"))?;
    let map = raw
        .as_object()
        .ok_or_else(|| "Grok auth.json is not an object.".to_string())?;

    // Use ONLY the auth.x.ai OIDC entry Grok Build writes (keys look like
    // `https://auth.x.ai::<client_id>`). Never fall back to any other entry: a
    // sibling provider's bearer/refresh tokens would then be shipped to the Grok
    // billing endpoint and, on 401, POSTed to auth.x.ai/oauth2/token. Absent that
    // entry, treat it as no Grok auth on disk and omit the card silently — the
    // same stance as a missing auth.json.
    let (entry_key, entry) = match map.iter().find(|(k, _)| is_grok_auth_entry_key(k)) {
        Some((k, v)) => (k.clone(), v.clone()),
        None => return Ok(None),
    };

    let obj = entry
        .as_object()
        .ok_or_else(|| "Grok auth entry is not an object.".to_string())?;

    let access_token = obj
        .get("key")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_string();
    let refresh_token = obj
        .get("refresh_token")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_string();
    if access_token.is_empty() && refresh_token.is_empty() {
        return Ok(None);
    }

    let client_id = obj
        .get("oidc_client_id")
        .and_then(|v| v.as_str())
        .map(str::to_string)
        .or_else(|| client_id_from_entry_key(&entry_key))
        .unwrap_or_default();

    let email = obj
        .get("email")
        .and_then(|v| v.as_str())
        .filter(|s| !s.is_empty())
        .map(str::to_string);
    let expires_at = obj
        .get("expires_at")
        .and_then(|v| v.as_str())
        .and_then(parse_timestamp);

    Ok(Some(GrokCredentials {
        auth_path: auth_path.to_path_buf(),
        entry_key,
        access_token,
        refresh_token,
        client_id,
        expires_at,
        email,
        raw_json: raw,
    }))
}

/// True only when `key` is a genuine Grok Build OIDC entry, keyed
/// `https://auth.x.ai::<client_id>`. The issuer segment (everything before the
/// first `::` separator) must equal `https://auth.x.ai` by byte equality — a
/// substring/prefix test would let a lookalike host like
/// `https://auth.x.ai.example.com::<id>` masquerade as the real issuer and get
/// its bearer/refresh tokens shipped to the Grok billing + token endpoints. A
/// key with no `::` separator has no client-id segment and is not the shape
/// Grok writes, so it is rejected too (fail-closed).
fn is_grok_auth_entry_key(key: &str) -> bool {
    matches!(key.split_once("::"), Some((issuer, _)) if issuer == "https://auth.x.ai")
}

fn client_id_from_entry_key(key: &str) -> Option<String> {
    // Keys look like: https://auth.x.ai::b1a00492-073a-47ea-816f-4c329264a828
    key.rsplit("::")
        .next()
        .map(str::to_string)
        .filter(|s| !s.is_empty())
}

fn save_credentials(credentials: &GrokCredentials) -> Result<(), String> {
    let mut raw = credentials.raw_json.clone();
    let entry = raw
        .as_object_mut()
        .and_then(|m| m.get_mut(&credentials.entry_key))
        .and_then(|v| v.as_object_mut())
        .ok_or_else(|| "Grok auth entry missing while saving.".to_string())?;

    entry.insert(
        "key".to_string(),
        Value::String(credentials.access_token.clone()),
    );
    entry.insert(
        "refresh_token".to_string(),
        Value::String(credentials.refresh_token.clone()),
    );
    if let Some(exp) = credentials.expires_at {
        entry.insert(
            "expires_at".to_string(),
            Value::String(exp.to_rfc3339_opts(SecondsFormat::Millis, true)),
        );
    }

    let data =
        serde_json::to_vec_pretty(&raw).map_err(|e| format!("encode Grok auth.json: {e}"))?;
    atomic_write(&credentials.auth_path, &data).map_err(|e| format!("save Grok auth.json: {e}"))
}

fn atomic_write(path: &Path, data: &[u8]) -> std::io::Result<()> {
    use std::io::Write as _;

    let parent = path.parent().ok_or_else(|| {
        std::io::Error::new(
            std::io::ErrorKind::InvalidInput,
            format!("credentials path {} has no parent", path.display()),
        )
    })?;
    let file_name = path
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("auth.json");
    static TMP_SEQ: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
    let seq = TMP_SEQ.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    let tmp = parent.join(format!(
        ".{file_name}.tokenbar.{}.{}",
        std::process::id(),
        seq
    ));

    let staged = (|| -> std::io::Result<()> {
        let mut options = fs::OpenOptions::new();
        options.write(true).create_new(true);
        #[cfg(unix)]
        {
            use std::os::unix::fs::OpenOptionsExt as _;
            options.mode(0o600);
        }
        let mut file = options.open(&tmp)?;
        file.write_all(data)?;
        file.sync_all()
    })();
    if let Err(error) = staged {
        let _ = fs::remove_file(&tmp);
        return Err(error);
    }
    if let Err(error) = fs::rename(&tmp, path) {
        let _ = fs::remove_file(&tmp);
        return Err(error);
    }
    Ok(())
}

fn grok_home() -> PathBuf {
    std::env::var_os("GROK_HOME")
        .map(PathBuf::from)
        .filter(|p| !p.as_os_str().is_empty())
        .or_else(|| std::env::var_os("HOME").map(|home| PathBuf::from(home).join(".grok")))
        .unwrap_or_else(|| PathBuf::from(".grok"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn prefers_grok_build_product_percent() {
        let config: BillingConfig = serde_json::from_str(
            r#"{
                "creditUsagePercent": 50.0,
                "productUsage": [
                    { "product": "GrokChat", "usagePercent": 10.0 },
                    { "product": "GrokBuild", "usagePercent": 4.0 }
                ]
            }"#,
        )
        .unwrap();
        assert!((used_percent_from_config(&config).unwrap() - 4.0).abs() < 0.01);
    }

    #[test]
    fn falls_back_to_overall_credit_percent() {
        let config: BillingConfig = serde_json::from_str(
            r#"{
                "creditUsagePercent": 12.5,
                "productUsage": [
                    { "product": "GrokChat" },
                    { "product": "GrokBuild" }
                ]
            }"#,
        )
        .unwrap();
        assert!((used_percent_from_config(&config).unwrap() - 12.5).abs() < 0.01);
    }

    #[test]
    fn maps_weekly_window_from_period() {
        let body = r#"{
            "config": {
                "currentPeriod": {
                    "type": "USAGE_PERIOD_TYPE_WEEKLY",
                    "start": "2026-07-07T15:40:06.727001+00:00",
                    "end": "2026-07-14T15:40:06.727001+00:00"
                },
                "creditUsagePercent": 4.0,
                "productUsage": [
                    { "product": "GrokBuild", "usagePercent": 4.0 }
                ],
                "billingPeriodEnd": "2026-07-14T15:40:06.727001+00:00"
            },
            "subscriptionTiers": "X Premium+"
        }"#;
        let credentials = GrokCredentials {
            auth_path: PathBuf::from("/tmp/unused"),
            entry_key: "k".into(),
            access_token: "t".into(),
            refresh_token: "r".into(),
            client_id: "c".into(),
            expires_at: None,
            email: Some("user@example.com".into()),
            raw_json: Value::Object(Default::default()),
        };
        let now = DateTime::parse_from_rfc3339("2026-07-11T12:00:00Z")
            .unwrap()
            .with_timezone(&Utc);
        let data = map_billing(body, &credentials, now).unwrap();
        assert_eq!(data.windows.len(), 1);
        assert_eq!(data.windows[0].label_for_test(), "Weekly");
        assert!((data.windows[0].remaining_for_test() - 96.0).abs() < 0.01);
        assert_eq!(
            data.identity.as_ref().and_then(|i| i.email.as_deref()),
            Some("user@example.com")
        );
        assert_eq!(
            data.identity.as_ref().and_then(|i| i.plan.as_deref()),
            Some("X Premium+")
        );
    }

    #[test]
    fn client_id_from_key() {
        assert_eq!(
            client_id_from_entry_key("https://auth.x.ai::b1a00492-073a-47ea-816f-4c329264a828")
                .as_deref(),
            Some("b1a00492-073a-47ea-816f-4c329264a828")
        );
    }

    /// Write `contents` to a fresh temp `auth.json` and return (dir, path).
    fn temp_auth_json(tag: &str, contents: &str) -> (PathBuf, PathBuf) {
        let dir = std::env::temp_dir().join(format!(
            "tb_grok_load_{tag}_{}_{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir_all(&dir).unwrap();
        let path = dir.join("auth.json");
        fs::write(&path, contents).unwrap();
        (dir, path)
    }

    #[test]
    fn foreign_only_auth_json_yields_no_credentials() {
        // auth.json holding ONLY a sibling provider's entry (no auth.x.ai key)
        // must not become a request-bearing candidate: no card, and — crucially —
        // none of the foreign entry's tokens are surfaced anywhere.
        let (dir, path) = temp_auth_json(
            "foreign",
            r#"{
                "https://auth.openai.com::deadbeef": {
                    "key": "FAKE-FOREIGN-ACCESS",
                    "refresh_token": "FAKE-FOREIGN-REFRESH",
                    "oidc_client_id": "deadbeef"
                }
            }"#,
        );
        let loaded = load_credentials_from(&path).unwrap();
        assert!(
            loaded.is_none(),
            "a foreign-only auth.json must yield no Grok credentials, got {loaded:?}"
        );
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn picks_auth_x_ai_entry_never_a_sibling() {
        // With both a sibling entry and the real auth.x.ai entry present, the
        // loader must select the auth.x.ai one and never read the sibling's
        // secrets into the request-bearing credentials.
        let (dir, path) = temp_auth_json(
            "mixed",
            r#"{
                "https://auth.openai.com::deadbeef": {
                    "key": "FAKE-FOREIGN-ACCESS",
                    "refresh_token": "FAKE-FOREIGN-REFRESH",
                    "oidc_client_id": "deadbeef"
                },
                "https://auth.x.ai::b1a00492-073a-47ea-816f-4c329264a828": {
                    "key": "FAKE-XAI-ACCESS",
                    "refresh_token": "FAKE-XAI-REFRESH"
                }
            }"#,
        );
        let creds = load_credentials_from(&path).unwrap().expect("auth.x.ai entry loads");
        assert!(creds.entry_key.contains("auth.x.ai"));
        assert_eq!(creds.access_token, "FAKE-XAI-ACCESS");
        assert_eq!(creds.refresh_token, "FAKE-XAI-REFRESH");
        // Client id derives from the auth.x.ai key, not the sibling's.
        assert_eq!(creds.client_id, "b1a00492-073a-47ea-816f-4c329264a828");
        // No field ever carries the sibling secrets.
        assert_ne!(creds.access_token, "FAKE-FOREIGN-ACCESS");
        assert_ne!(creds.refresh_token, "FAKE-FOREIGN-REFRESH");
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn is_grok_auth_entry_key_requires_exact_issuer() {
        // The genuine Grok Build key shape.
        assert!(is_grok_auth_entry_key(
            "https://auth.x.ai::b1a00492-073a-47ea-816f-4c329264a828"
        ));
        // A lookalike subdomain must NOT match — a substring/prefix test would
        // have accepted it and shipped its tokens to the Grok endpoints.
        assert!(!is_grok_auth_entry_key(
            "https://auth.x.ai.example.com::b1a00492"
        ));
        assert!(!is_grok_auth_entry_key(
            "https://auth.x.ai.evil.example::deadbeef"
        ));
        // A foreign issuer and a shapeless key are rejected.
        assert!(!is_grok_auth_entry_key("https://auth.openai.com::deadbeef"));
        assert!(!is_grok_auth_entry_key("https://auth.x.ai"));
    }

    #[test]
    fn lookalike_only_auth_json_yields_no_credentials() {
        // A key whose issuer merely CONTAINS "auth.x.ai" (a lookalike host) must
        // not be selected: no card, and none of its tokens are read into the
        // request-bearing credentials. Fail-closed, exactly like a foreign entry.
        let (dir, path) = temp_auth_json(
            "lookalike",
            r#"{
                "https://auth.x.ai.example.com::deadbeef": {
                    "key": "FAKE-LOOKALIKE-ACCESS",
                    "refresh_token": "FAKE-LOOKALIKE-REFRESH",
                    "oidc_client_id": "deadbeef"
                }
            }"#,
        );
        let loaded = load_credentials_from(&path).unwrap();
        assert!(
            loaded.is_none(),
            "a lookalike-host auth.json must yield no Grok credentials, got {loaded:?}"
        );
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn picks_real_issuer_over_lookalike_sibling() {
        // A lookalike entry sitting next to the genuine one must never win: the
        // loader selects the exact-issuer key and reads only its secrets.
        let (dir, path) = temp_auth_json(
            "lookalike_mixed",
            r#"{
                "https://auth.x.ai.example.com::deadbeef": {
                    "key": "FAKE-LOOKALIKE-ACCESS",
                    "refresh_token": "FAKE-LOOKALIKE-REFRESH",
                    "oidc_client_id": "deadbeef"
                },
                "https://auth.x.ai::b1a00492-073a-47ea-816f-4c329264a828": {
                    "key": "FAKE-XAI-ACCESS",
                    "refresh_token": "FAKE-XAI-REFRESH"
                }
            }"#,
        );
        let creds = load_credentials_from(&path)
            .unwrap()
            .expect("genuine auth.x.ai entry loads");
        assert_eq!(creds.entry_key, "https://auth.x.ai::b1a00492-073a-47ea-816f-4c329264a828");
        assert_eq!(creds.access_token, "FAKE-XAI-ACCESS");
        assert_eq!(creds.refresh_token, "FAKE-XAI-REFRESH");
        assert_ne!(creds.access_token, "FAKE-LOOKALIKE-ACCESS");
        assert_ne!(creds.refresh_token, "FAKE-LOOKALIKE-REFRESH");
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn save_credentials_surfaces_missing_entry_as_err() {
        // P2: a failed write-back must be an inspectable Err (the caller logs it
        // instead of swallowing it), not a silent success that drops a rotated
        // refresh token. An entry_key absent from raw_json is the deterministic
        // failure the seam exposes without touching the network.
        let creds = GrokCredentials {
            auth_path: PathBuf::from("/tmp/unused-grok-save"),
            entry_key: "https://auth.x.ai::missing".into(),
            access_token: "new-access".into(),
            refresh_token: "new-refresh".into(),
            client_id: "missing".into(),
            expires_at: None,
            email: None,
            raw_json: Value::Object(Default::default()),
        };
        let result = save_credentials(&creds);
        assert!(
            result.is_err(),
            "save_credentials must surface a failure as Err so the caller can log it"
        );
    }

    #[cfg(unix)]
    #[test]
    fn atomic_write_preserves_private_permissions() {
        use std::os::unix::fs::PermissionsExt;

        let dir = std::env::temp_dir().join(format!(
            "tb_grok_atomic_{}_{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir_all(&dir).unwrap();
        let path = dir.join("auth.json");
        fs::write(&path, b"old").unwrap();
        fs::set_permissions(&path, fs::Permissions::from_mode(0o600)).unwrap();

        atomic_write(&path, b"new").unwrap();

        assert_eq!(fs::read(&path).unwrap(), b"new");
        assert_eq!(
            fs::metadata(&path).unwrap().permissions().mode() & 0o777,
            0o600
        );
        let _ = fs::remove_dir_all(&dir);
    }
}
