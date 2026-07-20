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

use crate::agent_account_scope::{
    self, AccountScope, AccountScopeError, RefreshCheckpoint, RefreshScopeTransaction,
};
use crate::agent_quota_duration::DurationEvidence;
use crate::agent_usage::{AgentIdentity, UsageWindow};
use chrono::{DateTime, SecondsFormat, Utc};
use serde::Deserialize;
use serde_json::{value::RawValue, Value};
use std::fs;
use std::path::{Path, PathBuf};

const GROK_TOKEN_URL: &str = "https://auth.x.ai/oauth2/token";
const GROK_BILLING_URL: &str = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";
/// Refresh a few minutes early so a clock-skewed expiry doesn't 401 the billing call.
const ACCESS_SKEW_SECS: i64 = 120;

pub(crate) struct GrokData {
    pub identity: Option<AgentIdentity>,
    pub account_scope: Result<AccountScope, AccountScopeError>,
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

impl GrokCredentials {
    fn scope_marker(&self) -> Option<&[u8]> {
        let marker = self.refresh_token.trim();
        (!marker.is_empty()).then_some(marker.as_bytes())
    }

    fn scope_location(&self) -> Result<String, AccountScopeError> {
        agent_account_scope::canonical_file_location(&self.auth_path, Some(&self.entry_key))
    }

    fn resolve_account_scope(&self) -> Result<AccountScope, AccountScopeError> {
        let marker = self
            .scope_marker()
            .ok_or(AccountScopeError::NoTrustedEvidence)?;
        agent_account_scope::resolve_credential(
            "grok",
            "grok-auth-json",
            &self.scope_location()?,
            marker,
        )
    }
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
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    credit_usage_percent: Option<Box<RawValue>>,
    #[serde(default)]
    product_usage: Option<Vec<ProductUsage>>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    billing_period_start: Option<Box<RawValue>>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    billing_period_end: Option<Box<RawValue>>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    used: Option<Box<RawValue>>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    monthly_limit: Option<Box<RawValue>>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    on_demand_used: Option<Box<RawValue>>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    on_demand_cap: Option<Box<RawValue>>,
}

#[derive(Debug, Deserialize)]
struct UsagePeriod {
    #[serde(default, rename = "type")]
    period_type: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    start: Option<Box<RawValue>>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    end: Option<Box<RawValue>>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ProductUsage {
    #[serde(default)]
    product: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_raw")]
    usage_percent: Option<Box<RawValue>>,
}

enum LegacyPercentageEvidence {
    Absent,
    Invalid,
    GrokBuild(f64),
    Credit(f64),
    CreditZero(f64),
}

enum PercentageEvidence {
    Absent,
    Invalid,
    Valid(f64),
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
    let mut refreshed_scope = None;
    if credentials_needs_refresh(&credentials, now) {
        let refreshed =
            refresh_credentials(&credentials.auth_path, &credentials.entry_key, false).await?;
        credentials = refreshed.0;
        refreshed_scope = merge_refreshed_scope(refreshed_scope, refreshed.1);
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
            let refreshed =
                refresh_credentials(&credentials.auth_path, &credentials.entry_key, true).await?;
            credentials = refreshed.0;
            refreshed_scope = merge_refreshed_scope(refreshed_scope, refreshed.1);
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
            let account_scope =
                refreshed_scope.unwrap_or_else(|| credentials.resolve_account_scope());
            return map_billing(&retry_body, &credentials, now, account_scope);
        }
        return Err("Grok OAuth token expired or invalid. Run `grok` to log in again.".to_string());
    }
    if !status.is_success() {
        return Err(format!("Grok billing API returned {}.", status.as_u16()));
    }

    let account_scope = refreshed_scope.unwrap_or_else(|| credentials.resolve_account_scope());
    map_billing(&body, &credentials, now, account_scope)
}

fn merge_refreshed_scope(
    current: Option<Result<AccountScope, AccountScopeError>>,
    next: Result<AccountScope, AccountScopeError>,
) -> Option<Result<AccountScope, AccountScopeError>> {
    Some(match current {
        None => next,
        Some(Err(first_error)) => Err(first_error),
        Some(Ok(current_scope)) => match next {
            Err(error) => Err(error),
            Ok(next_scope) if next_scope == current_scope => Ok(current_scope),
            Ok(_) => Err(AccountScopeError::MetadataConflict),
        },
    })
}

fn map_billing(
    body: &str,
    credentials: &GrokCredentials,
    now: DateTime<Utc>,
    account_scope: Result<AccountScope, AccountScopeError>,
) -> Result<GrokData, String> {
    let payload: BillingResponse =
        serde_json::from_str(body).map_err(|e| format!("decode Grok billing response: {e}"))?;
    let config = payload
        .config
        .ok_or_else(|| "Grok billing response missing config.".to_string())?;

    let (used_percent, included_monthly_fallback, included_invalid) =
        match legacy_percentage_evidence(&config) {
            LegacyPercentageEvidence::GrokBuild(percent)
            | LegacyPercentageEvidence::Credit(percent) => (Some(percent), false, false),
            LegacyPercentageEvidence::CreditZero(percent) => {
                match included_percentage_evidence(&config) {
                    PercentageEvidence::Valid(included) => (Some(included), true, false),
                    PercentageEvidence::Absent | PercentageEvidence::Invalid => {
                        (Some(percent), false, false)
                    }
                }
            }
            LegacyPercentageEvidence::Invalid => (None, false, false),
            LegacyPercentageEvidence::Absent => match included_percentage_evidence(&config) {
                PercentageEvidence::Valid(percent) => (Some(percent), true, false),
                PercentageEvidence::Absent => (None, false, false),
                PercentageEvidence::Invalid => (None, false, true),
            },
        };
    let (on_demand_recognized, extra_window) = extra_usage_window(&config, now);
    if included_invalid && extra_window.is_none() {
        return Err(
            "Grok billing response has no creditUsagePercent or GrokBuild usage.".to_string(),
        );
    }
    if used_percent.is_none() && !on_demand_recognized {
        return Err(
            "Grok billing response has no creditUsagePercent or GrokBuild usage.".to_string(),
        );
    }

    let mut windows = Vec::with_capacity(2);
    if let Some(used_percent) = used_percent {
        let period = period_details(&config);
        let kind = period.kind.or(
            included_monthly_fallback.then_some(("Monthly", "billing.monthly.v1")),
        );
        let window = match kind {
            Some((label, window_key)) => {
                let mut window = UsageWindow::from_used_percent(
                    label.to_string(),
                    used_percent,
                    period.end,
                    now,
                )
                .with_identity(window_key, Some(window_key.to_string()));

                if period.invalid_evidence {
                    let invalid_provider = period
                        .end
                        .map(|end| DurationEvidence::provider(provider_reset_at(end, now), 0));
                    window = window.with_provider_duration_evidence(now, true, invalid_provider);
                } else if let (Some(end), Some(duration_seconds)) =
                    (period.end, period.duration_seconds)
                {
                    window = window.with_provider_duration_evidence(
                        now,
                        true,
                        Some(DurationEvidence::provider(
                            provider_reset_at(end, now),
                            duration_seconds,
                        )),
                    );
                } else if period.end_was_supplied {
                    window = window.with_observed_duration_evidence(now, true);
                }
                window
            }
            None => {
                UsageWindow::from_used_percent("Unknown".to_string(), used_percent, period.end, now)
                    .with_identity("row.billing.unknown.v1", None)
            }
        };
        windows.push(window);
    }
    if let Some(window) = extra_window {
        windows.push(window);
    }

    Ok(GrokData {
        identity: Some(AgentIdentity {
            email: credentials.email.clone(),
            plan: payload
                .subscription_tiers
                .filter(|s| !s.trim().is_empty())
                .map(|s| s.trim().to_string()),
        }),
        account_scope,
        windows,
    })
}

fn legacy_percentage_evidence(config: &BillingConfig) -> LegacyPercentageEvidence {
    if let Some(products) = config.product_usage.as_ref() {
        for product in products {
            if !product
                .product
                .as_deref()
                .is_some_and(|name| name.eq_ignore_ascii_case("GrokBuild"))
            {
                continue;
            }
            if let Some(raw) = product.usage_percent.as_deref() {
                return valid_percentage(raw).map_or(
                    LegacyPercentageEvidence::Invalid,
                    LegacyPercentageEvidence::GrokBuild,
                );
            }
        }
    }
    match config.credit_usage_percent.as_deref() {
        Some(raw) => match valid_percentage(raw) {
            Some(percent) if decimal_mantissa_is_zero(raw.get()) => {
                LegacyPercentageEvidence::CreditZero(percent)
            }
            Some(percent) => LegacyPercentageEvidence::Credit(percent),
            None => LegacyPercentageEvidence::Invalid,
        },
        None => LegacyPercentageEvidence::Absent,
    }
}

fn included_percentage_evidence(config: &BillingConfig) -> PercentageEvidence {
    let (used, monthly_limit) = match (config.used.as_deref(), config.monthly_limit.as_deref()) {
        (None, None) => return PercentageEvidence::Absent,
        (Some(used), Some(monthly_limit)) => (used, monthly_limit),
        _ => return PercentageEvidence::Invalid,
    };
    let (Some(used), Some(monthly_limit)) =
        (raw_amount_value(used), raw_amount_value(monthly_limit))
    else {
        return PercentageEvidence::Invalid;
    };
    if used < 0.0 || monthly_limit <= 0.0 {
        return PercentageEvidence::Invalid;
    }
    PercentageEvidence::Valid(((used / monthly_limit) * 100.0).min(100.0))
}

fn extra_usage_window(config: &BillingConfig, now: DateTime<Utc>) -> (bool, Option<UsageWindow>) {
    let Some(used) = config
        .on_demand_used
        .as_deref()
        .and_then(raw_amount_value)
    else {
        return (false, None);
    };
    let Some(cap) = config
        .on_demand_cap
        .as_deref()
        .and_then(raw_amount_value)
    else {
        return (false, None);
    };
    if used < 0.0 || cap < 0.0 {
        return (false, None);
    }
    if cap == 0.0 {
        return (true, None);
    }

    let used_percent = ((used / cap) * 100.0).min(100.0);
    let reset = config
        .billing_period_end
        .as_deref()
        .and_then(parse_timestamp_raw);
    let window =
        UsageWindow::from_used_percent("Extra usage".to_string(), used_percent, reset, now)
            .with_identity("extra_usage.v1", Some("extra_usage.v1".to_string()))
            .with_non_recurring();
    (true, Some(window))
}

fn raw_amount_value(raw: &RawValue) -> Option<f64> {
    let raw_value = raw.get();
    strict_json_number(raw_value).or_else(|| {
        let fields: std::collections::BTreeMap<String, Box<RawValue>> =
            serde_json::from_str(raw_value).ok()?;
        strict_json_number(fields.get("val")?.get())
    })
}

fn strict_json_number(raw_value: &str) -> Option<f64> {
    let value = serde_json::from_str::<f64>(raw_value).ok()?;
    // Reject any syntactically nonzero number that underflows to signed zero.
    if value == 0.0 && !decimal_mantissa_is_zero(raw_value) {
        return None;
    }
    value.is_finite().then_some(value)
}

fn decimal_mantissa_is_zero(value: &str) -> bool {
    let value = value.trim();
    let unsigned = value.strip_prefix('-').unwrap_or(value);
    let mantissa = unsigned.split(['e', 'E']).next().unwrap_or(unsigned);
    !mantissa
        .bytes()
        .any(|digit| digit.is_ascii_digit() && digit != b'0')
}

fn deserialize_optional_raw<'de, D>(deserializer: D) -> Result<Option<Box<RawValue>>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    Box::<RawValue>::deserialize(deserializer).map(Some)
}

fn valid_percentage(raw: &RawValue) -> Option<f64> {
    let value = raw.get();
    serde_json::from_str::<f64>(value)
        .ok()
        .filter(|pct| pct.is_finite() && decimal_percentage_in_range(value))
}

fn decimal_percentage_in_range(value: &str) -> bool {
    let (negative, unsigned) = match value.strip_prefix('-') {
        Some(unsigned) => (true, unsigned),
        None => (false, value),
    };
    let exponent_index = unsigned.find(|character| matches!(character, 'e' | 'E'));
    let (mantissa, exponent_text) = match exponent_index {
        Some(index) => (&unsigned[..index], Some(&unsigned[index + 1..])),
        None => (unsigned, None),
    };
    let fractional_digits = mantissa
        .split_once('.')
        .map_or(0, |(_, fraction)| fraction.len());
    let digits = mantissa
        .bytes()
        .filter(|byte| byte.is_ascii_digit())
        .collect::<Vec<_>>();
    let Some(first_nonzero) = digits.iter().position(|digit| *digit != b'0') else {
        return true;
    };
    if negative {
        return false;
    }
    let exponent = match exponent_text {
        Some(text) => match text.parse::<i128>() {
            Ok(exponent) => exponent,
            Err(_) => return text.starts_with('-'),
        },
        None => 0,
    };

    let significant = &digits[first_nonzero..];
    let integer_digits = exponent
        .saturating_add(significant.len() as i128)
        .saturating_sub(fractional_digits as i128);
    match integer_digits.cmp(&3) {
        std::cmp::Ordering::Less => true,
        std::cmp::Ordering::Greater => false,
        std::cmp::Ordering::Equal => {
            significant[0] == b'1' && significant[1..].iter().all(|digit| *digit == b'0')
        }
    }
}

struct PeriodDetails {
    kind: Option<(&'static str, &'static str)>,
    end: Option<DateTime<Utc>>,
    end_was_supplied: bool,
    duration_seconds: Option<i64>,
    invalid_evidence: bool,
}

fn period_details(config: &BillingConfig) -> PeriodDetails {
    let period = config.current_period.as_ref();
    let period_type = period
        .and_then(|period| period.period_type.as_deref())
        .unwrap_or("")
        .trim()
        .to_ascii_uppercase();
    let kind = match period_type.as_str() {
        "WEEKLY" | "USAGE_PERIOD_TYPE_WEEKLY" => Some(("Weekly", "billing.weekly.v1")),
        "MONTHLY" | "USAGE_PERIOD_TYPE_MONTHLY" => Some(("Monthly", "billing.monthly.v1")),
        _ => None,
    };

    // Select each primary field independently. A present but malformed primary
    // value must not be hidden by a valid billing-level fallback.
    let start_raw = period
        .and_then(|period| period.start.as_deref())
        .or(config.billing_period_start.as_deref());
    let end_raw = period
        .and_then(|period| period.end.as_deref())
        .or(config.billing_period_end.as_deref());
    let start = start_raw.and_then(parse_timestamp_raw);
    let end = end_raw.and_then(parse_timestamp_raw);
    let (duration_seconds, invalid_evidence) = match (start_raw, end_raw, start, end) {
        (Some(_), Some(_), Some(start), Some(end)) if end > start => {
            (Some((end - start).num_seconds()), false)
        }
        (Some(_), Some(_), Some(_), Some(_)) => (None, true),
        (Some(_), Some(_), _, _) => (None, true),
        (Some(_), None, Some(_), _) => (None, false),
        (Some(_), None, None, _) => (None, true),
        (None, Some(_), _, Some(_)) => (None, false),
        (None, Some(_), _, None) => (None, true),
        _ => (None, false),
    };
    PeriodDetails {
        kind,
        end,
        end_was_supplied: end_raw.is_some(),
        duration_seconds,
        invalid_evidence,
    }
}

fn provider_reset_at(reset: DateTime<Utc>, now: DateTime<Utc>) -> i64 {
    if reset > now {
        reset.timestamp().max(now.timestamp().saturating_add(1))
    } else {
        reset.timestamp()
    }
}

fn parse_timestamp_raw(value: &RawValue) -> Option<DateTime<Utc>> {
    serde_json::from_str::<String>(value.get())
        .ok()
        .and_then(|value| parse_timestamp(&value))
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

async fn refresh_credentials(
    auth_path: &Path,
    entry_key: &str,
    force: bool,
) -> Result<(GrokCredentials, Result<AccountScope, AccountScopeError>), String> {
    let refresh = agent_account_scope::begin_refresh("grok")
        .map_err(|_| "Grok credential refresh lock is unavailable.".to_string())?;
    refresh_credentials_with(
        auth_path,
        entry_key,
        force,
        &refresh,
        request_refresh,
        save_credentials,
        |_| Ok(()),
    )
    .await
}

async fn request_refresh(
    refresh_token: String,
    client_id: String,
) -> Result<TokenResponse, String> {
    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(30))
        .build()
        .map_err(|e| format!("build Grok token client: {e}"))?;
    let form = [
        ("grant_type", "refresh_token"),
        ("refresh_token", refresh_token.as_str()),
        ("client_id", client_id.as_str()),
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

    serde_json::from_str(&body).map_err(|e| format!("decode Grok token refresh: {e}"))
}

async fn refresh_credentials_with<R, Request, RequestFuture, Save, Checkpoint>(
    auth_path: &Path,
    entry_key: &str,
    force: bool,
    refresh: &R,
    request: Request,
    save: Save,
    mut checkpoint: Checkpoint,
) -> Result<(GrokCredentials, Result<AccountScope, AccountScopeError>), String>
where
    R: RefreshScopeTransaction + ?Sized,
    Request: FnOnce(String, String) -> RequestFuture,
    RequestFuture: std::future::Future<Output = Result<TokenResponse, String>>,
    Save: FnOnce(&GrokCredentials) -> Result<(), String>,
    Checkpoint: FnMut(RefreshCheckpoint) -> Result<(), String>,
{
    // Another TokenBar process may have refreshed while this caller waited.
    // Reload the exact request-bearing entry only after the provider lock is held.
    let mut credentials = load_credentials_entry_from(auth_path, Some(entry_key))?
        .ok_or_else(|| "Grok auth entry disappeared during refresh.".to_string())?;
    checkpoint(RefreshCheckpoint::Reloaded)?;
    if !force && !credentials_needs_refresh(&credentials, Utc::now()) {
        let scope = match credentials.scope_marker() {
            Some(marker) => refresh.resolve_current(
                "grok-auth-json",
                &credentials
                    .scope_location()
                    .map_err(|_| "Grok auth location cannot be scoped safely.".to_string())?,
                marker,
            ),
            None => Err(AccountScopeError::NoTrustedEvidence),
        };
        return Ok((credentials, scope));
    }
    if credentials.refresh_token.trim().is_empty() {
        return Err(
            "Grok OAuth token needs refresh but auth.json has no refresh token.".to_string(),
        );
    }
    if credentials.client_id.trim().is_empty() {
        return Err("Grok auth.json is missing oidc_client_id.".to_string());
    }

    let old_marker = credentials
        .scope_marker()
        .ok_or_else(|| "Grok OAuth token needs refresh but auth.json has no refresh token.".to_string())?
        .to_vec();
    let refresh_token = credentials.refresh_token.trim().to_string();
    let tokens = request(refresh_token, credentials.client_id.clone()).await?;
    checkpoint(RefreshCheckpoint::NetworkReturned)?;
    credentials.access_token = tokens.access_token;
    if let Some(refresh_token) = tokens
        .refresh_token
        .map(|token| token.trim().to_string())
        .filter(|token| !token.is_empty())
    {
        credentials.refresh_token = refresh_token;
    }
    if let Some(expires_in) = tokens.expires_in {
        credentials.expires_at = Some(Utc::now() + chrono::Duration::seconds(expires_in.max(0)));
    }
    let new_marker = credentials
        .scope_marker()
        .ok_or_else(|| "Grok refreshed credential has no usable refresh token.".to_string())?;
    let refresh_token_rotated = new_marker != old_marker.as_slice();
    let location = credentials
        .scope_location()
        .map_err(|_| "Grok auth location cannot be scoped safely.".to_string())?;
    let scope = refresh.transfer("grok-auth-json", &location, &old_marker, new_marker);
    checkpoint(RefreshCheckpoint::MetadataHandled)?;

    // A rotated marker may reach disk only after its lineage transfer is durable.
    // The refreshed access token remains usable in memory for this poll.
    if refresh_token_rotated && scope.is_err() {
        return Ok((credentials, scope));
    }

    // If write-back fails, the still-stored old marker resolves the same scope.
    if let Err(error) = save(&credentials) {
        eprintln!("tb_core_ffi: failed to persist refreshed Grok credentials: {error}");
    }
    checkpoint(RefreshCheckpoint::CredentialsPersisted)?;

    Ok((credentials, scope))
}

fn load_credentials() -> Result<Option<GrokCredentials>, String> {
    load_credentials_from(&grok_home().join("auth.json"))
}

fn load_credentials_from(auth_path: &Path) -> Result<Option<GrokCredentials>, String> {
    load_credentials_entry_from(auth_path, None)
}

fn load_credentials_entry_from(
    auth_path: &Path,
    expected_entry_key: Option<&str>,
) -> Result<Option<GrokCredentials>, String> {
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
    let selected = match expected_entry_key {
        Some(expected) if is_grok_auth_entry_key(expected) => map
            .get(expected)
            .map(|entry| (expected.to_string(), entry.clone())),
        Some(_) => None,
        None => map
            .iter()
            .find(|(key, _)| is_grok_auth_entry_key(key))
            .map(|(key, entry)| (key.clone(), entry.clone())),
    };
    let Some((entry_key, entry)) = selected else {
        return Ok(None);
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
    matches!(
        key.split_once("::"),
        Some((issuer, client_id))
            if issuer == "https://auth.x.ai"
                && !client_id.trim().is_empty()
                && !client_id.contains("::")
    )
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
        .or_else(|| crate::user_home_dir().map(|home| home.join(".grok")))
        .unwrap_or_else(|| PathBuf::from(".grok"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::agent_account_scope::test_support::TestRefreshScope;

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
        assert!(matches!(
            legacy_percentage_evidence(&config),
            LegacyPercentageEvidence::GrokBuild(percent) if (percent - 4.0).abs() < 0.01
        ));
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
        assert!(matches!(
            legacy_percentage_evidence(&config),
            LegacyPercentageEvidence::Credit(percent) if (percent - 12.5).abs() < 0.01
        ));
    }

    #[test]
    fn rejects_invalid_percentages_without_falling_back() {
        for invalid in [
            r#"{ "creditUsagePercent": 12.5, "productUsage": [{ "product": "GrokBuild", "usagePercent": 150.0 }] }"#,
            r#"{ "creditUsagePercent": 12.5, "productUsage": [{ "product": "GrokBuild", "usagePercent": -1.0 }] }"#,
            r#"{ "creditUsagePercent": 12.5, "productUsage": [{ "product": "GrokBuild", "usagePercent": -1e-400 }] }"#,
            r#"{ "creditUsagePercent": 12.5, "productUsage": [{ "product": "GrokBuild", "usagePercent": -1e-9999999999999999999999999999999999999999 }] }"#,
            r#"{ "creditUsagePercent": 12.5, "productUsage": [{ "product": "GrokBuild", "usagePercent": 100.0000000000000000001 }] }"#,
            r#"{ "creditUsagePercent": 12.5, "productUsage": [{ "product": "GrokBuild", "usagePercent": 1e400 }] }"#,
            r#"{ "creditUsagePercent": 12.5, "productUsage": [{ "product": "GrokBuild", "usagePercent": "NaN" }] }"#,
            r#"{ "creditUsagePercent": 12.5, "productUsage": [{ "product": "GrokBuild", "usagePercent": null }] }"#,
        ] {
            let config: BillingConfig = serde_json::from_str(invalid).unwrap();
            assert!(
                matches!(
                    legacy_percentage_evidence(&config),
                    LegacyPercentageEvidence::Invalid
                ),
                "invalid GrokBuild usage must not fall back to overall credit usage"
            );
        }

        for valid in [
            "-0.0",
            "99.9999999999999999999",
            "1e2",
            "0.100e3",
            "1e-9999999999999999999999999999999999999999",
            "0.01e-170141183460469231731687303715884105728",
        ] {
            let config: BillingConfig = serde_json::from_str(&format!(
                r#"{{ "creditUsagePercent": {valid} }}"#
            ))
            .unwrap();
            assert!(
                matches!(
                    legacy_percentage_evidence(&config),
                    LegacyPercentageEvidence::Credit(_)
                        | LegacyPercentageEvidence::CreditZero(_)
                ),
                "valid percentage {valid} must survive exact range validation"
            );
        }

        let config: BillingConfig = serde_json::from_str(
            r#"{ "creditUsagePercent": 12.5, "productUsage": [{ "product": "GrokChat" }] }"#,
        )
        .unwrap();
        assert!(matches!(
            legacy_percentage_evidence(&config),
            LegacyPercentageEvidence::Credit(percent) if percent == 12.5
        ));
    }

    fn map_test_config(config: &str) -> Result<GrokData, String> {
        let body = format!(r#"{{ "config": {config} }}"#);
        let credentials = GrokCredentials {
            auth_path: PathBuf::from("/tmp/unused"),
            entry_key: "k".into(),
            access_token: "t".into(),
            refresh_token: "r".into(),
            client_id: "c".into(),
            expires_at: None,
            email: None,
            raw_json: Value::Object(Default::default()),
        };
        let now = parse_timestamp("2026-07-11T12:00:00Z").unwrap();
        map_billing(
            &body,
            &credentials,
            now,
            Err(AccountScopeError::NoTrustedEvidence),
        )
    }

    #[test]
    fn accepts_bare_and_wrapped_numeric_amounts_for_included_and_extra_usage() {
        for (label, config, card_id) in [
            (
                "bare included",
                r#"{ "used": 25.0, "monthlyLimit": 100.0 }"#,
                "billing.monthly.v1",
            ),
            (
                "wrapped included",
                r#"{ "used": { "val": 25.0 }, "monthlyLimit": { "val": 100.0 } }"#,
                "billing.monthly.v1",
            ),
            (
                "bare extra",
                r#"{ "onDemandUsed": 25.0, "onDemandCap": 100.0 }"#,
                "extra_usage.v1",
            ),
            (
                "wrapped extra",
                r#"{ "onDemandUsed": { "val": 25.0 }, "onDemandCap": { "val": 100.0 } }"#,
                "extra_usage.v1",
            ),
        ] {
            let data = map_test_config(config).unwrap();
            assert_eq!(data.windows.len(), 1, "{label}");
            assert_eq!(data.windows[0].card_id_for_test(), card_id, "{label}");
            assert!(
                (data.windows[0].remaining_for_test() - 75.0).abs() < 0.01,
                "{label}"
            );
        }
    }

    #[test]
    fn rejects_bare_numeric_strings_for_included_and_extra_usage() {
        for (label, config) in [
            (
                "included used string",
                r#"{
                    "used": "25",
                    "monthlyLimit": 100.0,
                    "onDemandUsed": 0.0,
                    "onDemandCap": 0.0
                }"#,
            ),
            (
                "included limit string",
                r#"{
                    "used": 25.0,
                    "monthlyLimit": "100",
                    "onDemandUsed": 0.0,
                    "onDemandCap": 0.0
                }"#,
            ),
            (
                "extra string",
                r#"{ "onDemandUsed": "25", "onDemandCap": 100.0 }"#,
            ),
        ] {
            assert_eq!(
                map_test_config(config)
                    .err()
                    .expect("numeric strings must not become amount evidence"),
                "Grok billing response has no creditUsagePercent or GrokBuild usage.",
                "{label}"
            );
        }
    }

    #[test]
    fn default_zero_credit_uses_valid_unified_included_ratio() {
        let data = map_test_config(
            r#"{
                "creditUsagePercent": 0.0,
                "used": 25.0,
                "monthlyLimit": 100.0
            }"#,
        )
        .unwrap();

        assert_eq!(data.windows.len(), 1);
        assert_eq!(data.windows[0].card_id_for_test(), "billing.monthly.v1");
        assert!((data.windows[0].remaining_for_test() - 75.0).abs() < 0.01);
    }

    #[test]
    fn default_zero_credit_keeps_legacy_when_included_is_absent_or_invalid() {
        for (label, config) in [
            ("absent", r#"{ "creditUsagePercent": 0.0 }"#),
            (
                "invalid",
                r#"{
                    "creditUsagePercent": 0.0,
                    "used": "bad",
                    "monthlyLimit": 100.0
                }"#,
            ),
        ] {
            let data = map_test_config(config).unwrap();
            assert_eq!(data.windows.len(), 1, "{label}");
            assert_eq!(
                data.windows[0].card_id_for_test(),
                "row.billing.unknown.v1",
                "{label}"
            );
            assert!((data.windows[0].remaining_for_test() - 100.0).abs() < 0.01);
        }
    }

    #[test]
    fn syntactically_nonzero_credit_still_beats_unified_when_float_underflows() {
        let data = map_test_config(
            r#"{
                "creditUsagePercent": 1e-400,
                "used": 25.0,
                "monthlyLimit": 100.0
            }"#,
        )
        .unwrap();

        assert_eq!(data.windows.len(), 1);
        assert_eq!(data.windows[0].card_id_for_test(), "row.billing.unknown.v1");
        assert!((data.windows[0].remaining_for_test() - 100.0).abs() < 0.01);
    }

    #[test]
    fn grok_build_zero_remains_higher_confidence_than_unified_included() {
        let data = map_test_config(
            r#"{
                "creditUsagePercent": 50.0,
                "productUsage": [{ "product": "GrokBuild", "usagePercent": 0.0 }],
                "used": 25.0,
                "monthlyLimit": 100.0
            }"#,
        )
        .unwrap();

        assert_eq!(data.windows.len(), 1);
        assert_eq!(data.windows[0].card_id_for_test(), "row.billing.unknown.v1");
        assert!((data.windows[0].remaining_for_test() - 100.0).abs() < 0.01);
    }

    #[test]
    fn maps_unified_included_usage_with_period_semantics_before_disabled_extra() {
        let data = map_test_config(
            r#"{
                "currentPeriod": { "type": "DAILY" },
                "billingPeriodStart": "2026-07-01T00:00:00Z",
                "billingPeriodEnd": "2026-08-01T00:00:00Z",
                "used": { "val": 25.0 },
                "monthlyLimit": { "val": 100.0 },
                "onDemandUsed": { "val": 0.0 },
                "onDemandCap": { "val": 0.0 }
            }"#,
        )
        .unwrap();

        assert_eq!(data.windows.len(), 1);
        let monthly = &data.windows[0];
        assert_eq!(monthly.label_for_test(), "Monthly");
        assert_eq!(monthly.card_id_for_test(), "billing.monthly.v1");
        assert_eq!(monthly.pace_window_key_for_test(), Some("billing.monthly.v1"));
        assert_eq!(monthly.pace_reason_for_test(), None);
        let wire = serde_json::to_value(monthly).unwrap();
        assert_eq!(wire["usedPercent"], 25.0);
        assert_eq!(wire["paceStatus"]["durationSeconds"], 2_678_400);

        let weekly = map_test_config(
            r#"{
                "currentPeriod": {
                    "type": "WEEKLY",
                    "start": "2026-07-07T00:00:00Z",
                    "end": "2026-07-14T00:00:00Z"
                },
                "used": { "val": 25.0 },
                "monthlyLimit": { "val": 100.0 }
            }"#,
        )
        .unwrap();
        assert_eq!(weekly.windows[0].card_id_for_test(), "billing.weekly.v1");
    }

    #[test]
    fn maps_unified_included_ratio_boundaries_and_clamps_over_limit() {
        for (used, monthly_limit, expected) in [
            (0.0, 80.0, 0.0),
            (80.0, 80.0, 100.0),
            (160.0, 80.0, 100.0),
        ] {
            let config = format!(
                r#"{{
                    "used": {{ "val": {used} }},
                    "monthlyLimit": {{ "val": {monthly_limit} }}
                }}"#
            );
            let data = map_test_config(&config).unwrap();
            assert_eq!(data.windows.len(), 1);
            assert_eq!(data.windows[0].card_id_for_test(), "billing.monthly.v1");
            let wire = serde_json::to_value(&data.windows[0]).unwrap();
            assert_eq!(
                wire["usedPercent"], expected,
                "used={used}, monthlyLimit={monthly_limit}"
            );
        }
    }

    #[test]
    fn legacy_percentage_evidence_precedes_unified_included_usage() {
        for (label, config, expected_remaining) in [
            (
                "GrokBuild",
                r#"{
                    "creditUsagePercent": 50.0,
                    "productUsage": [{ "product": "GrokBuild", "usagePercent": 4.0 }],
                    "used": { "val": 25.0 },
                    "monthlyLimit": { "val": 100.0 }
                }"#,
                96.0,
            ),
            (
                "creditUsagePercent",
                r#"{
                    "creditUsagePercent": 12.5,
                    "used": { "val": 25.0 },
                    "monthlyLimit": { "val": 100.0 }
                }"#,
                87.5,
            ),
        ] {
            let data = map_test_config(config).unwrap();
            assert_eq!(data.windows.len(), 1, "{label}");
            assert_eq!(data.windows[0].card_id_for_test(), "row.billing.unknown.v1");
            assert!(
                (data.windows[0].remaining_for_test() - expected_remaining).abs() < 0.01,
                "{label}"
            );
        }
    }

    #[test]
    fn invalid_explicit_legacy_percentage_does_not_fall_through_to_unified() {
        for config in [
            r#"{
                "creditUsagePercent": 12.5,
                "productUsage": [{ "product": "GrokBuild", "usagePercent": "bad" }],
                "used": { "val": 25.0 },
                "monthlyLimit": { "val": 100.0 }
            }"#,
            r#"{
                "creditUsagePercent": "bad",
                "used": { "val": 25.0 },
                "monthlyLimit": { "val": 100.0 }
            }"#,
        ] {
            assert_eq!(
                map_test_config(config)
                    .err()
                    .expect("invalid legacy percentage must fail without extra usage"),
                "Grok billing response has no creditUsagePercent or GrokBuild usage."
            );
        }
    }

    #[test]
    fn malformed_unified_included_evidence_is_ignored_with_valid_legacy() {
        let data = map_test_config(
            r#"{
                "creditUsagePercent": 12.5,
                "used": { "val": "bad", "futureField": { "large": 1e400 } },
                "monthlyLimit": "bad",
                "onDemandUsed": { "val": 0.0 },
                "onDemandCap": { "val": 0.0 }
            }"#,
        )
        .unwrap();

        assert_eq!(data.windows.len(), 1);
        assert!((data.windows[0].remaining_for_test() - 87.5).abs() < 0.01);
    }

    #[test]
    fn invalid_unified_included_evidence_is_not_masked_by_disabled_extra() {
        for (label, included) in [
            ("used only", r#""used": { "val": 1.0 }"#),
            (
                "monthly limit only",
                r#""monthlyLimit": { "val": 10.0 }"#,
            ),
            (
                "wrong type",
                r#""used": { "val": "1" }, "monthlyLimit": { "val": 10.0 }"#,
            ),
            (
                "negative used",
                r#""used": { "val": -1.0 }, "monthlyLimit": { "val": 10.0 }"#,
            ),
            (
                "zero limit",
                r#""used": { "val": 1.0 }, "monthlyLimit": { "val": 0.0 }"#,
            ),
            (
                "overflow",
                r#""used": { "val": 1e400 }, "monthlyLimit": { "val": 10.0 }"#,
            ),
            (
                "used underflow",
                r#""used": { "val": 1e-400 }, "monthlyLimit": { "val": 10.0 }"#,
            ),
            (
                "limit underflow",
                r#""used": { "val": 1.0 }, "monthlyLimit": { "val": 1e-400 }"#,
            ),
        ] {
            let config = format!(
                r#"{{
                    {included},
                    "onDemandUsed": {{ "val": 0.0 }},
                    "onDemandCap": {{ "val": 0.0 }}
                }}"#
            );
            assert_eq!(
                map_test_config(&config)
                    .err()
                    .expect("invalid included evidence must beat disabled extra"),
                "Grok billing response has no creditUsagePercent or GrokBuild usage.",
                "{label}"
            );
        }
    }

    #[test]
    fn invalid_unified_included_evidence_allows_valid_positive_extra() {
        let data = map_test_config(
            r#"{
                "used": { "val": 1.0 },
                "onDemandUsed": { "val": 25.0 },
                "onDemandCap": { "val": 100.0 }
            }"#,
        )
        .unwrap();

        assert_eq!(data.windows.len(), 1);
        let extra = &data.windows[0];
        assert_eq!(extra.card_id_for_test(), "extra_usage.v1");
        assert_eq!(extra.pace_window_key_for_test(), Some("extra_usage.v1"));
        assert_eq!(extra.pace_reason_for_test(), Some("nonRecurring"));
        assert!((extra.remaining_for_test() - 75.0).abs() < 0.01);
    }

    #[test]
    fn maps_old_and_on_demand_windows_without_changing_precedence() {
        let data = map_test_config(
            r#"{
                "currentPeriod": {
                    "type": "WEEKLY",
                    "start": "2026-07-07T00:00:00Z",
                    "end": "2026-07-14T00:00:00Z"
                },
                "billingPeriodEnd": "2026-08-01T00:00:00Z",
                "creditUsagePercent": 50.0,
                "productUsage": [
                    { "product": "GrokChat", "usagePercent": 10.0 },
                    { "product": "GrokBuild", "usagePercent": 4.0 }
                ],
                "onDemandUsed": { "val": 25.0 },
                "onDemandCap": { "val": 100.0 }
            }"#,
        )
        .unwrap();

        assert_eq!(data.windows.len(), 2);
        assert_eq!(data.windows[0].card_id_for_test(), "billing.weekly.v1");
        assert!((data.windows[0].remaining_for_test() - 96.0).abs() < 0.01);

        let extra = &data.windows[1];
        assert_eq!(extra.label_for_test(), "Extra usage");
        assert_eq!(extra.card_id_for_test(), "extra_usage.v1");
        assert_eq!(extra.pace_window_key_for_test(), Some("extra_usage.v1"));
        assert_eq!(extra.pace_reason_for_test(), Some("nonRecurring"));
        let wire = serde_json::to_value(extra).unwrap();
        assert_eq!(wire["usedPercent"], 25.0);
        assert_eq!(wire["resetsAt"], "2026-08-01T00:00:00.000Z");
    }

    #[test]
    fn maps_on_demand_ratio_boundaries_and_clamps_over_cap() {
        for (used, cap, expected) in [
            (0.0, 80.0, 0.0),
            (80.0, 80.0, 100.0),
            (160.0, 80.0, 100.0),
        ] {
            let config = format!(
                r#"{{
                    "onDemandUsed": {{ "val": {used} }},
                    "onDemandCap": {{ "val": {cap} }}
                }}"#
            );
            let data = map_test_config(&config).unwrap();
            assert_eq!(data.windows.len(), 1);
            let wire = serde_json::to_value(&data.windows[0]).unwrap();
            assert_eq!(wire["usedPercent"], expected, "used={used}, cap={cap}");
            assert_eq!(data.windows[0].pace_reason_for_test(), Some("nonRecurring"));
        }
    }

    #[test]
    fn zero_on_demand_cap_is_recognized_but_disabled() {
        for cap in ["0", "0.0", "-0.0", "0e10", "-0.0e-10"] {
            let config = format!(
                r#"{{
                    "onDemandUsed": {{ "val": 5.0 }},
                    "onDemandCap": {{ "val": {cap} }}
                }}"#
            );
            let data = map_test_config(&config).unwrap();
            assert!(data.windows.is_empty(), "cap={cap}");
        }
    }

    #[test]
    fn invalid_on_demand_pairs_do_not_create_quota() {
        for (label, config) in [
            (
                "negative used",
                r#"{ "onDemandUsed": { "val": -1.0 }, "onDemandCap": { "val": 10.0 } }"#,
            ),
            (
                "negative cap",
                r#"{ "onDemandUsed": { "val": 1.0 }, "onDemandCap": { "val": -10.0 } }"#,
            ),
            (
                "negative used underflow",
                r#"{ "onDemandUsed": { "val": -1e-400 }, "onDemandCap": { "val": 10.0 } }"#,
            ),
            (
                "positive used underflow",
                r#"{ "onDemandUsed": { "val": 1e-400 }, "onDemandCap": { "val": 10.0 } }"#,
            ),
            (
                "positive cap underflow",
                r#"{ "onDemandUsed": { "val": 1.0 }, "onDemandCap": { "val": 1e-400 } }"#,
            ),
            ("missing used", r#"{ "onDemandCap": { "val": 10.0 } }"#),
            ("missing cap", r#"{ "onDemandUsed": { "val": 1.0 } }"#),
            (
                "wrong outer type",
                r#"{ "onDemandUsed": [1.0], "onDemandCap": { "val": 10.0 } }"#,
            ),
            (
                "wrong val type",
                r#"{ "onDemandUsed": { "val": "1" }, "onDemandCap": { "val": 10.0 } }"#,
            ),
            (
                "overflow used",
                r#"{ "onDemandUsed": { "val": 1e400 }, "onDemandCap": { "val": 10.0 } }"#,
            ),
            (
                "overflow cap",
                r#"{ "onDemandUsed": { "val": 1.0 }, "onDemandCap": { "val": 1e400 } }"#,
            ),
        ] {
            assert_eq!(
                map_test_config(config)
                    .err()
                    .expect("invalid on-demand pair must fail"),
                "Grok billing response has no creditUsagePercent or GrokBuild usage.",
                "{label}"
            );
        }
    }

    #[test]
    fn malformed_on_demand_does_not_break_old_subscription() {
        let data = map_test_config(
            r#"{
                "creditUsagePercent": 12.5,
                "onDemandUsed": {
                    "val": "not-a-number",
                    "futureField": { "large": 1e400 }
                },
                "onDemandCap": { "val": 100.0, "futureField": true }
            }"#,
        )
        .unwrap();

        assert_eq!(data.windows.len(), 1);
        assert!((data.windows[0].remaining_for_test() - 87.5).abs() < 0.01);
    }

    #[test]
    fn prepaid_balance_only_does_not_create_quota() {
        assert_eq!(
            map_test_config(
                r#"{
                    "prepaidBalance": { "val": 500.0 },
                    "topUpMethod": "manual",
                    "isUnifiedBillingUser": true
                }"#,
            )
            .err()
            .expect("prepaid balance alone must fail"),
            "Grok billing response has no creditUsagePercent or GrokBuild usage."
        );
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
        let data = map_billing(
            body,
            &credentials,
            now,
            Err(AccountScopeError::NoTrustedEvidence),
        )
        .unwrap();
        assert_eq!(data.windows.len(), 1);
        assert_eq!(data.windows[0].label_for_test(), "Weekly");
        assert_eq!(data.windows[0].card_id_for_test(), "billing.weekly.v1");
        assert_eq!(data.windows[0].pace_window_key_for_test(), Some("billing.weekly.v1"));
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
    fn stage4_period_evidence_routes_and_fails_closed() {
        let credentials = GrokCredentials {
            auth_path: PathBuf::from("/tmp/unused"),
            entry_key: "k".into(),
            access_token: "t".into(),
            refresh_token: "r".into(),
            client_id: "c".into(),
            expires_at: None,
            email: None,
            raw_json: Value::Object(Default::default()),
        };
        let map = |config: Value, now: DateTime<Utc>| {
            let body = serde_json::json!({ "config": config }).to_string();
            map_billing(
                &body,
                &credentials,
                now,
                Err(AccountScopeError::NoTrustedEvidence),
            )
            .unwrap()
            .windows
            .into_iter()
            .next()
            .unwrap()
        };

        let exact_weekly = map(
            serde_json::json!({
                "currentPeriod": {
                    "type": "usage_period_type_weekly",
                    "start": "2026-07-03T00:00:00.900Z",
                    "end": "2026-07-10T00:00:00.900Z"
                },
                "creditUsagePercent": 12.0
            }),
            parse_timestamp("2026-07-10T00:00:00.100Z").unwrap(),
        );
        let weekly_wire = serde_json::to_value(&exact_weekly).unwrap();
        assert_eq!(weekly_wire["cardId"], "billing.weekly.v1");
        assert_eq!(weekly_wire["paceStatus"]["windowKey"], "billing.weekly.v1");
        assert_eq!(weekly_wire["paceStatus"]["state"], "learningHistory");
        assert_eq!(weekly_wire["paceStatus"]["durationSeconds"], 604_800);
        assert_eq!(weekly_wire["paceStatus"]["durationSource"], "provider");
        assert_eq!(weekly_wire["resetsAt"], "2026-07-10T00:00:00.900Z");

        for (start, end, days) in [
            ("2023-02-01T00:00:00Z", "2023-03-01T00:00:00Z", 28),
            ("2024-02-01T00:00:00Z", "2024-03-01T00:00:00Z", 29),
            ("2024-04-01T00:00:00Z", "2024-05-01T00:00:00Z", 30),
            ("2024-05-01T00:00:00Z", "2024-06-01T00:00:00Z", 31),
        ] {
            let window = map(
                serde_json::json!({
                    "currentPeriod": {
                        "type": "USAGE_PERIOD_TYPE_MONTHLY",
                        "start": start,
                        "end": end
                    },
                    "creditUsagePercent": 12.0
                }),
                parse_timestamp(start).unwrap() + chrono::Duration::days(1),
            );
            let wire = serde_json::to_value(&window).unwrap();
            assert_eq!(wire["cardId"], "billing.monthly.v1");
            assert_eq!(wire["paceStatus"]["windowKey"], "billing.monthly.v1");
            assert_eq!(wire["paceStatus"]["durationSeconds"], days * 86_400);
            assert_eq!(wire["paceStatus"]["durationSource"], "provider");
            assert_eq!(wire["paceStatus"]["state"], "learningHistory");
        }

        let billing_fallback = map(
            serde_json::json!({
                "currentPeriod": { "type": "WEEKLY" },
                "billingPeriodStart": "2026-07-03T00:00:00Z",
                "billingPeriodEnd": "2026-07-10T00:00:00Z",
                "creditUsagePercent": 12.0
            }),
            parse_timestamp("2026-07-05T00:00:00Z").unwrap(),
        );
        let fallback_wire = serde_json::to_value(&billing_fallback).unwrap();
        assert_eq!(fallback_wire["paceStatus"]["durationSeconds"], 604_800);
        assert_eq!(fallback_wire["paceStatus"]["durationSource"], "provider");

        let end_only = map(
            serde_json::json!({
                "currentPeriod": {
                    "type": "weekly",
                    "end": "2026-07-24T00:00:00Z"
                },
                "creditUsagePercent": 12.0
            }),
            parse_timestamp("2026-07-17T00:00:00Z").unwrap(),
        );
        let end_only_wire = serde_json::to_value(&end_only).unwrap();
        assert_eq!(end_only_wire["paceStatus"]["state"], "learningDuration");
        assert_eq!(end_only_wire["paceStatus"]["durationSource"], "observed");
        assert!(end_only_wire["paceStatus"].get("durationSeconds").is_none());

        let unknown = map(
            serde_json::json!({
                "currentPeriod": {
                    "type": "DAILY",
                    "start": "2026-07-17T00:00:00Z",
                    "end": "2026-07-18T00:00:00Z"
                },
                "creditUsagePercent": 12.0
            }),
            parse_timestamp("2026-07-17T12:00:00Z").unwrap(),
        );
        let unknown_wire = serde_json::to_value(&unknown).unwrap();
        assert_eq!(unknown_wire["cardId"], "row.billing.unknown.v1");
        assert_eq!(unknown_wire["paceStatus"]["state"], "unavailable");
        assert_eq!(unknown_wire["paceStatus"]["reason"], "windowIdentity");
        assert!(unknown_wire["paceStatus"].get("windowKey").is_none());
        assert!(unknown_wire["paceStatus"].get("durationSeconds").is_none());
        for unknown_type in ["NOT_WEEKLY", "BIWEEKLY", "MONTHLYISH"] {
            let window = map(
                serde_json::json!({
                    "currentPeriod": {
                        "type": unknown_type,
                        "start": "2026-07-17T00:00:00Z",
                        "end": "2026-07-18T00:00:00Z"
                    },
                    "creditUsagePercent": 12.0
                }),
                parse_timestamp("2026-07-17T12:00:00Z").unwrap(),
            );
            let wire = serde_json::to_value(&window).unwrap();
            assert_eq!(wire["paceStatus"]["reason"], "windowIdentity");
        }

        for (start, end) in [
            ("2026-07-18T00:00:00Z", "2026-07-17T00:00:00Z"),
            ("not-a-date", "2026-07-24T00:00:00Z"),
            ("2026-07-17T00:00:00Z", "not-a-date"),
        ] {
            let window = map(
                serde_json::json!({
                    "currentPeriod": {
                        "type": "weekly",
                        "start": start,
                        "end": end
                    },
                    "billingPeriodStart": "2026-07-17T00:00:00Z",
                    "billingPeriodEnd": "2026-07-24T00:00:00Z",
                    "creditUsagePercent": 12.0
                }),
                parse_timestamp("2026-07-17T12:00:00Z").unwrap(),
            );
            let wire = serde_json::to_value(&window).unwrap();
            assert_eq!(wire["paceStatus"]["windowKey"], "billing.weekly.v1");
            assert_eq!(wire["paceStatus"]["state"], "unavailable");
            assert_eq!(wire["paceStatus"]["reason"], "invalidEvidence");
            assert!(wire["paceStatus"].get("durationSeconds").is_none());
        }

        let null_primary = map(
            serde_json::json!({
                "currentPeriod": {
                    "type": "weekly",
                    "start": null,
                    "end": "2026-07-24T00:00:00Z"
                },
                "billingPeriodStart": "2026-07-17T00:00:00Z",
                "creditUsagePercent": 12.0
            }),
            parse_timestamp("2026-07-17T12:00:00Z").unwrap(),
        );
        let null_wire = serde_json::to_value(&null_primary).unwrap();
        assert_eq!(null_wire["paceStatus"]["state"], "unavailable");
        assert_eq!(null_wire["paceStatus"]["reason"], "invalidEvidence");

        let malformed_start_only = map(
            serde_json::json!({
                "currentPeriod": {
                    "type": "weekly",
                    "start": "not-a-date"
                },
                "creditUsagePercent": 12.0
            }),
            parse_timestamp("2026-07-17T12:00:00Z").unwrap(),
        );
        let malformed_start_wire = serde_json::to_value(&malformed_start_only).unwrap();
        assert_eq!(malformed_start_wire["paceStatus"]["state"], "unavailable");
        assert_eq!(
            malformed_start_wire["paceStatus"]["reason"],
            "invalidEvidence"
        );

        let valid_start_only = map(
            serde_json::json!({
                "currentPeriod": {
                    "type": "weekly",
                    "start": "2026-07-17T00:00:00Z"
                },
                "creditUsagePercent": 12.0
            }),
            parse_timestamp("2026-07-17T12:00:00Z").unwrap(),
        );
        let start_only_wire = serde_json::to_value(&valid_start_only).unwrap();
        assert_eq!(start_only_wire["paceStatus"]["state"], "unavailable");
        assert_eq!(start_only_wire["paceStatus"]["reason"], "missingReset");
    }

    #[test]
    fn unknown_period_has_no_history_identity() {
        let config: BillingConfig = serde_json::from_str(
            r#"{ "currentPeriod": { "type": "USAGE_PERIOD_TYPE_OTHER" } }"#,
        )
        .unwrap();
        let period = period_details(&config);
        assert!(period.kind.is_none());
        assert!(period.end.is_none());
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
        // A foreign issuer, blank/nested client id, and shapeless key are rejected.
        assert!(!is_grok_auth_entry_key("https://auth.openai.com::deadbeef"));
        assert!(!is_grok_auth_entry_key("https://auth.x.ai::"));
        assert!(!is_grok_auth_entry_key("https://auth.x.ai::   "));
        assert!(!is_grok_auth_entry_key("https://auth.x.ai::client::extra"));
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

    const TEST_ENTRY: &str = "https://auth.x.ai::fixture-client";

    struct RecordingRefreshScope<'a> {
        inner: &'a TestRefreshScope,
        calls: std::cell::RefCell<Vec<(String, String, Vec<u8>, Option<Vec<u8>>)>>,
    }

    impl<'a> RecordingRefreshScope<'a> {
        fn new(inner: &'a TestRefreshScope) -> Self {
            Self {
                inner,
                calls: std::cell::RefCell::new(Vec::new()),
            }
        }
    }

    impl RefreshScopeTransaction for RecordingRefreshScope<'_> {
        fn resolve_current(
            &self,
            semantic_source: &str,
            canonical_location: &str,
            marker: &[u8],
        ) -> Result<AccountScope, AccountScopeError> {
            self.calls.borrow_mut().push((
                semantic_source.to_string(),
                canonical_location.to_string(),
                marker.to_vec(),
                None,
            ));
            self.inner
                .resolve_current(semantic_source, canonical_location, marker)
        }

        fn transfer(
            &self,
            semantic_source: &str,
            canonical_location: &str,
            old_marker: &[u8],
            new_marker: &[u8],
        ) -> Result<AccountScope, AccountScopeError> {
            self.calls.borrow_mut().push((
                semantic_source.to_string(),
                canonical_location.to_string(),
                old_marker.to_vec(),
                Some(new_marker.to_vec()),
            ));
            self.inner
                .transfer(semantic_source, canonical_location, old_marker, new_marker)
        }
    }

    fn write_test_auth(path: &Path, access_token: &str, refresh_token: &str, expires_at: &str) {
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(
            path,
            serde_json::to_vec_pretty(&serde_json::json!({
                (TEST_ENTRY): {
                    "key": access_token,
                    "refresh_token": refresh_token,
                    "oidc_client_id": "fixture-client",
                    "expires_at": expires_at,
                    "email": "grok-sensitive@example.com"
                },
                "https://auth.x.ai::sibling-client": {
                    "key": "sibling-access",
                    "refresh_token": "sibling-refresh",
                    "oidc_client_id": "sibling-client",
                    "expires_at": "1970-01-01T00:00:00Z"
                }
            }))
            .unwrap(),
        )
        .unwrap();
    }

    fn checkpoint_at(
        target: Option<RefreshCheckpoint>,
    ) -> impl FnMut(RefreshCheckpoint) -> Result<(), String> {
        move |checkpoint| {
            if Some(checkpoint) == target {
                Err("injected crash".to_string())
            } else {
                Ok(())
            }
        }
    }

    async fn grok_test_response(
        refresh_token: String,
        client_id: String,
    ) -> Result<TokenResponse, String> {
        assert_eq!(refresh_token, "grok-old-refresh");
        assert_eq!(client_id, "fixture-client");
        Ok(TokenResponse {
            access_token: "grok-new-access".to_string(),
            refresh_token: Some("  grok-new-refresh\n".to_string()),
            expires_in: Some(3_600),
        })
    }

    async fn unexpected_refresh_request(
        _refresh_token: String,
        _client_id: String,
    ) -> Result<TokenResponse, String> {
        panic!("refresh network request must be skipped")
    }

    fn setup_refresh(tag: &str) -> (TestRefreshScope, PathBuf, AccountScope, Vec<u8>, String) {
        let scope = TestRefreshScope::new("grok", tag);
        let path = scope.root().join("grok/auth.json");
        write_test_auth(
            &path,
            "grok-old-access",
            "grok-old-refresh",
            "1970-01-01T00:00:00Z",
        );
        let credentials = load_credentials_entry_from(&path, Some(TEST_ENTRY))
            .unwrap()
            .unwrap();
        let location = credentials.scope_location().unwrap();
        let old_scope = scope
            .resolve_current(
                "grok-auth-json",
                &location,
                credentials.scope_marker().unwrap(),
            )
            .unwrap();
        let metadata = scope.metadata_bytes();
        (scope, path, old_scope, metadata, location)
    }

    async fn run_refresh(
        scope: &TestRefreshScope,
        path: &Path,
        crash: Option<RefreshCheckpoint>,
    ) -> Result<(GrokCredentials, Result<AccountScope, AccountScopeError>), String> {
        refresh_credentials_with(
            path,
            TEST_ENTRY,
            true,
            scope,
            grok_test_response,
            save_credentials,
            checkpoint_at(crash),
        )
        .await
    }

    fn stored_credentials(path: &Path) -> GrokCredentials {
        load_credentials_entry_from(path, Some(TEST_ENTRY))
            .unwrap()
            .unwrap()
    }

    #[test]
    fn account_scope_uses_refresh_marker_and_canonical_entry_domain_without_leaks() {
        let scope = TestRefreshScope::new("grok", "grok-scope-domain");
        let path = scope.root().join("grok/auth.json");
        write_test_auth(
            &path,
            "grok-sensitive-access-token",
            "grok-sensitive-refresh-token",
            "1970-01-01T00:00:00Z",
        );
        let credentials = load_credentials_entry_from(&path, Some(TEST_ENTRY))
            .unwrap()
            .unwrap();
        assert_eq!(
            credentials.scope_marker(),
            Some(b"grok-sensitive-refresh-token".as_slice())
        );
        let mut spaced_credentials = credentials.clone();
        spaced_credentials.refresh_token = "  grok-sensitive-refresh-token\n".to_string();
        assert_eq!(
            spaced_credentials.scope_marker(),
            Some(b"grok-sensitive-refresh-token".as_slice())
        );
        let location = credentials.scope_location().unwrap();
        assert_eq!(
            location,
            agent_account_scope::canonical_file_location(&path, Some(TEST_ENTRY)).unwrap()
        );

        let account_scope = scope
            .resolve_current(
                "grok-auth-json",
                &location,
                credentials.scope_marker().unwrap(),
            )
            .unwrap();
        let spaced_scope = scope
            .resolve_current(
                "grok-auth-json",
                &location,
                spaced_credentials.scope_marker().unwrap(),
            )
            .unwrap();
        assert_eq!(spaced_scope, account_scope);
        let metadata = String::from_utf8_lossy(&scope.metadata_bytes()).into_owned();
        for raw in [
            "grok-sensitive@example.com",
            "grok-sensitive-access-token",
            "grok-sensitive-refresh-token",
            TEST_ENTRY,
            location.as_str(),
        ] {
            assert!(!metadata.contains(raw), "metadata leaked {raw}");
            assert!(!account_scope.as_str().contains(raw), "scope leaked {raw}");
        }
        let now = DateTime::parse_from_rfc3339("2026-07-18T00:00:00Z")
            .unwrap()
            .with_timezone(&Utc);
        let error = match map_billing(
            "not-json",
            &credentials,
            now,
            Err(AccountScopeError::NoTrustedEvidence),
        ) {
            Err(error) => error,
            Ok(_) => panic!("invalid billing payload must fail"),
        };
        for raw in [
            "grok-sensitive@example.com",
            "grok-sensitive-access-token",
            "grok-sensitive-refresh-token",
        ] {
            assert!(!error.contains(raw), "error leaked {raw}");
        }

        let mut blank_marker = credentials;
        blank_marker.refresh_token = " \t\n ".to_string();
        assert!(blank_marker.scope_marker().is_none());
        scope.cleanup();
    }

    #[tokio::test]
    async fn refresh_reloads_exact_entry_skips_redundant_network_and_honors_force() {
        let scope = TestRefreshScope::new("grok", "grok-refresh-reload");
        let path = scope.root().join("grok/auth.json");
        write_test_auth(
            &path,
            "stale-access",
            "stale-refresh",
            "1970-01-01T00:00:00Z",
        );
        let stale = load_credentials_entry_from(&path, Some(TEST_ENTRY))
            .unwrap()
            .unwrap();
        write_test_auth(
            &path,
            "grok-old-access",
            "grok-old-refresh",
            "2099-01-01T00:00:00Z",
        );
        let current = load_credentials_entry_from(&path, Some(TEST_ENTRY))
            .unwrap()
            .unwrap();
        assert_ne!(stale.refresh_token, current.refresh_token);
        let location = current.scope_location().unwrap();
        let expected_scope = scope
            .resolve_current("grok-auth-json", &location, current.scope_marker().unwrap())
            .unwrap();
        let before = scope.metadata_bytes();
        let recording = RecordingRefreshScope::new(&scope);
        let mut checkpoints = Vec::new();
        let (reloaded, scope_outcome) = refresh_credentials_with(
            &path,
            TEST_ENTRY,
            false,
            &recording,
            unexpected_refresh_request,
            |_| panic!("credential save must be skipped"),
            |checkpoint| {
                checkpoints.push(checkpoint);
                Ok(())
            },
        )
        .await
        .unwrap();
        assert_eq!(reloaded.access_token, "grok-old-access");
        assert_eq!(reloaded.refresh_token, "grok-old-refresh");
        assert_eq!(scope_outcome, Ok(expected_scope.clone()));
        assert_eq!(checkpoints, vec![RefreshCheckpoint::Reloaded]);
        assert_eq!(scope.metadata_bytes(), before);
        assert_eq!(
            recording.calls.borrow().as_slice(),
            &[(
                "grok-auth-json".to_string(),
                location.clone(),
                b"grok-old-refresh".to_vec(),
                None,
            )]
        );
        recording.calls.borrow_mut().clear();

        let mut forced_checkpoints = Vec::new();
        let (forced, forced_scope) = refresh_credentials_with(
            &path,
            TEST_ENTRY,
            true,
            &recording,
            grok_test_response,
            save_credentials,
            |checkpoint| {
                forced_checkpoints.push(checkpoint);
                Ok(())
            },
        )
        .await
        .unwrap();
        assert_eq!(forced.access_token, "grok-new-access");
        assert_eq!(forced.refresh_token, "grok-new-refresh");
        assert_eq!(forced_scope, Ok(expected_scope));
        assert_eq!(
            forced_checkpoints,
            vec![
                RefreshCheckpoint::Reloaded,
                RefreshCheckpoint::NetworkReturned,
                RefreshCheckpoint::MetadataHandled,
                RefreshCheckpoint::CredentialsPersisted,
            ]
        );
        assert_eq!(
            recording.calls.borrow().as_slice(),
            &[(
                "grok-auth-json".to_string(),
                location.clone(),
                b"grok-old-refresh".to_vec(),
                Some(b"grok-new-refresh".to_vec()),
            )]
        );
        assert_eq!(stored_credentials(&path).refresh_token, "grok-new-refresh");
        recording.calls.borrow_mut().clear();

        write_test_auth(&path, "usable-access", " \t\n ", "2099-01-01T00:00:00Z");
        let (_, blank_scope) = refresh_credentials_with(
            &path,
            TEST_ENTRY,
            false,
            &recording,
            unexpected_refresh_request,
            |_| panic!("credential save must be skipped"),
            |_| Ok(()),
        )
        .await
        .unwrap();
        assert_eq!(blank_scope, Err(AccountScopeError::NoTrustedEvidence));
        assert!(recording.calls.borrow().is_empty());
        scope.cleanup();
    }

    #[tokio::test]
    async fn refresh_transfer_gate_save_failure_and_crash_boundaries_are_fail_closed() {
        for boundary in [
            RefreshCheckpoint::Reloaded,
            RefreshCheckpoint::NetworkReturned,
            RefreshCheckpoint::MetadataHandled,
            RefreshCheckpoint::CredentialsPersisted,
        ] {
            let (scope, path, old_scope, before, location) = setup_refresh("grok-crash");
            assert_eq!(
                run_refresh(&scope, &path, Some(boundary))
                    .await
                    .unwrap_err(),
                "injected crash"
            );
            let stored = stored_credentials(&path);
            assert_eq!(
                stored.refresh_token,
                if boundary == RefreshCheckpoint::CredentialsPersisted {
                    "grok-new-refresh"
                } else {
                    "grok-old-refresh"
                }
            );
            if matches!(
                boundary,
                RefreshCheckpoint::Reloaded | RefreshCheckpoint::NetworkReturned
            ) {
                assert_eq!(scope.metadata_bytes(), before);
            } else {
                assert_ne!(scope.metadata_bytes(), before);
                assert_eq!(
                    scope
                        .resolve_current("grok-auth-json", &location, b"grok-old-refresh")
                        .unwrap(),
                    old_scope
                );
                assert_eq!(
                    scope
                        .resolve_current("grok-auth-json", &location, b"grok-new-refresh")
                        .unwrap(),
                    old_scope
                );
            }
            scope.cleanup();
        }

        let (scope, path, old_scope, before, location) = setup_refresh("grok-metadata-fail");
        scope.fail_metadata_save();
        let (refreshed, scope_outcome) = run_refresh(&scope, &path, None).await.unwrap();
        assert_eq!(refreshed.access_token, "grok-new-access");
        assert_eq!(refreshed.refresh_token, "grok-new-refresh");
        assert_eq!(scope_outcome, Err(AccountScopeError::MetadataWrite));
        assert_eq!(scope.metadata_bytes(), before);
        let stored = stored_credentials(&path);
        assert_eq!(stored.access_token, "grok-old-access");
        assert_eq!(stored.refresh_token, "grok-old-refresh");
        assert_eq!(
            scope
                .resolve_current("grok-auth-json", &location, stored.refresh_token.as_bytes())
                .unwrap(),
            old_scope
        );
        scope.cleanup();

        let (scope, path, old_scope, _, location) = setup_refresh("grok-save-fail");
        let mut checkpoints = Vec::new();
        let (refreshed, scope_outcome) = refresh_credentials_with(
            &path,
            TEST_ENTRY,
            true,
            &scope,
            grok_test_response,
            |_| Err("injected save failure".to_string()),
            |checkpoint| {
                checkpoints.push(checkpoint);
                Ok(())
            },
        )
        .await
        .unwrap();
        assert_eq!(refreshed.access_token, "grok-new-access");
        assert_eq!(refreshed.refresh_token, "grok-new-refresh");
        assert_eq!(scope_outcome, Ok(old_scope.clone()));
        assert_eq!(stored_credentials(&path).refresh_token, "grok-old-refresh");
        assert_eq!(
            checkpoints,
            vec![
                RefreshCheckpoint::Reloaded,
                RefreshCheckpoint::NetworkReturned,
                RefreshCheckpoint::MetadataHandled,
                RefreshCheckpoint::CredentialsPersisted,
            ]
        );
        assert_eq!(
            scope
                .resolve_current("grok-auth-json", &location, b"grok-old-refresh")
                .unwrap(),
            old_scope
        );
        assert_eq!(
            scope
                .resolve_current("grok-auth-json", &location, b"grok-new-refresh")
                .unwrap(),
            old_scope
        );
        scope.cleanup();
    }

    #[test]
    fn refreshed_scope_merge_is_sticky_and_conflicts_fail_closed() {
        let (scope, path, scope_a, _, location) = setup_refresh("grok-scope-merge");
        let scope_b = scope
            .resolve_current("grok-auth-json", &location, b"different-refresh")
            .unwrap();
        assert_ne!(scope_a, scope_b);
        let credentials = load_credentials_entry_from(&path, Some(TEST_ENTRY))
            .unwrap()
            .unwrap();
        let now = DateTime::parse_from_rfc3339("2026-07-11T12:00:00Z")
            .unwrap()
            .with_timezone(&Utc);
        let body = r#"{
            "config": {
                "creditUsagePercent": 4.0
            }
        }"#;

        let cases = vec![
            (
                "error then success keeps first failure",
                vec![Err(AccountScopeError::MetadataWrite), Ok(scope_a.clone())],
                Err(AccountScopeError::MetadataWrite),
            ),
            (
                "success then error stays failed",
                vec![Ok(scope_a.clone()), Err(AccountScopeError::MetadataRead)],
                Err(AccountScopeError::MetadataRead),
            ),
            (
                "matching successes keep scope",
                vec![Ok(scope_a.clone()), Ok(scope_a.clone())],
                Ok(scope_a.clone()),
            ),
            (
                "different successes fail closed",
                vec![Ok(scope_a.clone()), Ok(scope_b)],
                Err(AccountScopeError::MetadataConflict),
            ),
        ];

        for (label, outcomes, expected) in cases {
            let merged = outcomes
                .into_iter()
                .fold(None, merge_refreshed_scope)
                .unwrap();
            let mapped = map_billing(body, &credentials, now, merged).unwrap();
            assert_eq!(mapped.account_scope, expected, "{label}");
        }
        scope.cleanup();
    }
}
