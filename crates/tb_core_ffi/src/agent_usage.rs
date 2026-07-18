use crate::agent_account_scope::{
    self, AccountScope, AccountScopeError, AuthoritativeIdKind, RefreshCheckpoint,
    RefreshScopeTransaction,
};
use crate::agent_antigravity;
use crate::agent_copilot;
use crate::agent_grok;
use crate::agent_quota_duration::{
    resolve_duration, valid_duration, DurationEvidence, DurationResolution, DurationSource,
    DurationUnavailableReason,
};
use crate::agent_quota_history::{
    BatchObservationResult, HistoricalPace, HistoryError, HistoryOutcome, QuotaObservation,
    SeriesKey,
};
use chrono::{DateTime, SecondsFormat, TimeZone, Utc};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

const CODEX_USAGE_URL: &str = "https://chatgpt.com/backend-api/wham/usage";
const CODEX_REFRESH_URL: &str = "https://auth.openai.com/oauth/token";
const CODEX_CLIENT_ID: &str = "app_EMoamEEZ73f0CkXaXp7hrann";
const CLAUDE_USAGE_URL: &str = "https://api.anthropic.com/api/oauth/usage";
const CLAUDE_REFRESH_URL: &str = "https://platform.claude.com/v1/oauth/token";
const CLAUDE_CLIENT_ID: &str = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
const CLAUDE_KEYCHAIN_SERVICE: &str = "Claude Code-credentials";
// Minimal-request endpoint whose response headers carry the unified rate-limit
// windows. Used as a fallback for inference-only `claude setup-token` tokens,
// which get HTTP 403 on the oauth/usage endpoint (it requires user:profile).
const CLAUDE_MESSAGES_URL: &str = "https://api.anthropic.com/v1/messages";
// Cheapest model for the header probe. Alias (not a dated snapshot) so it
// outlives model retirements.
const CLAUDE_PROBE_MODEL: &str = "claude-haiku-4-5";
// Keychain generic-password service holding a RAW setup-token (`sk-ant-oat01-…`),
// the launch-method-independent way to hand TokenBar a token for the limits card:
//   security add-generic-password -a "$USER" -s tokenbar-claude-oauth-token -w "<token>"
const CLAUDE_RAW_TOKEN_KEYCHAIN_SERVICE: &str = "tokenbar-claude-oauth-token";

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentUsagePayload {
    generated_at: String,
    agents: Vec<AgentUsageSnapshot>,
    /// Subscription-type providers opencode is authenticated against (its
    /// `auth.json` `type: "oauth"` entries), e.g. ["Codex", "Copilot"]. Surfaced
    /// so the user can see which agent subscriptions opencode also draws on.
    #[serde(skip_serializing_if = "Vec::is_empty")]
    opencode_subscriptions: Vec<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentUsageSnapshot {
    client_id: String,
    source: String,
    updated_at: String,
    identity: Option<AgentIdentity>,
    #[serde(skip)]
    pub(crate) account_scope: Result<AccountScope, AccountScopeError>,
    windows: Vec<UsageWindow>,
    credits: Option<CreditsSnapshot>,
    error: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentIdentity {
    pub(crate) email: Option<String>,
    pub(crate) plan: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HistoricalPacePayload {
    pub(crate) expected_used_percent: f64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub(crate) eta_seconds: Option<f64>,
    pub(crate) will_last_to_reset: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub(crate) run_out_probability: Option<f64>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
enum PaceState {
    LearningDuration,
    LearningHistory,
    Available,
    Unavailable,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct PaceStatusPayload {
    state: PaceState,
    #[serde(skip_serializing_if = "Option::is_none")]
    window_key: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    duration_seconds: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    duration_source: Option<DurationSource>,
    complete_cycles: usize,
    #[serde(skip_serializing_if = "Option::is_none")]
    reason: Option<String>,
}

#[derive(Debug, Clone)]
pub struct UsageWindow {
    card_id: String,
    label: String,
    used_percent: f64,
    remaining_percent: f64,
    resets_at: Option<String>,
    /// Exact provider reset retained independently from millisecond wire formatting.
    reset_at_evidence: Option<DateTime<Utc>>,
    reset_text: Option<String>,
    /// Resolved provider/contract evidence retained only to validate the nested wire state.
    duration_evidence: Option<(DurationEvidence, DurationSource)>,
    pace_status: PaceStatusPayload,
    historical_pace: Option<HistoricalPacePayload>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct UsageWindowWire<'a> {
    card_id: &'a str,
    label: &'a str,
    used_percent: f64,
    remaining_percent: f64,
    #[serde(skip_serializing_if = "Option::is_none")]
    resets_at: Option<&'a str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    reset_text: Option<&'a str>,
    /// Compatibility mirror; it is never an independent source of duration.
    #[serde(skip_serializing_if = "Option::is_none")]
    window_minutes: Option<i64>,
    pace_status: &'a PaceStatusPayload,
    #[serde(skip_serializing_if = "Option::is_none")]
    historical_pace: Option<&'a HistoricalPacePayload>,
}

impl Serialize for UsageWindow {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: serde::Serializer,
    {
        self.validate_wire().map_err(serde::ser::Error::custom)?;
        UsageWindowWire {
            card_id: &self.card_id,
            label: &self.label,
            used_percent: self.used_percent,
            remaining_percent: self.remaining_percent,
            resets_at: self.resets_at.as_deref(),
            reset_text: self.reset_text.as_deref(),
            window_minutes: self
                .pace_status
                .duration_seconds
                .map(|seconds| seconds / 60),
            pace_status: &self.pace_status,
            historical_pace: self.historical_pace.as_ref(),
        }
        .serialize(serializer)
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CreditsSnapshot {
    remaining: Option<f64>,
    unlimited: bool,
}

impl UsageWindow {
    /// Builds a display window before its provider assigns stable presentation
    /// identity. Stage 3A deliberately does not infer duration from a label.
    pub(crate) fn from_fraction(
        label: String,
        remaining_fraction: f64,
        resets_at: Option<DateTime<Utc>>,
        now: DateTime<Utc>,
    ) -> Self {
        Self::from_used_percent(label, (1.0 - remaining_fraction) * 100.0, resets_at, now)
    }

    pub(crate) fn from_used_percent(
        label: String,
        used_percent: f64,
        resets_at: Option<DateTime<Utc>>,
        now: DateTime<Utc>,
    ) -> Self {
        let used = used_percent.clamp(0.0, 100.0);
        let remaining = (100.0 - used).clamp(0.0, 100.0);
        Self {
            card_id: "row.unassigned.v1".to_string(),
            label,
            used_percent: used,
            remaining_percent: remaining,
            resets_at: resets_at.map(|d| d.to_rfc3339_opts(SecondsFormat::Millis, true)),
            reset_at_evidence: resets_at,
            reset_text: resets_at.map(|d| reset_text(d, now)),
            duration_evidence: None,
            pace_status: PaceStatusPayload {
                state: PaceState::Unavailable,
                window_key: None,
                duration_seconds: None,
                duration_source: None,
                complete_cycles: 0,
                reason: Some("windowIdentity".to_string()),
            },
            historical_pace: None,
        }
    }

    pub(crate) fn with_identity(
        mut self,
        card_id: impl Into<String>,
        window_key: Option<String>,
    ) -> Self {
        self.card_id = card_id.into();
        self.duration_evidence = None;
        self.pace_status = match (window_key, self.resets_at.is_some()) {
            (None, _) => PaceStatusPayload {
                state: PaceState::Unavailable,
                window_key: None,
                duration_seconds: None,
                duration_source: None,
                complete_cycles: 0,
                reason: Some("windowIdentity".to_string()),
            },
            (Some(window_key), false) => PaceStatusPayload {
                state: PaceState::Unavailable,
                window_key: Some(window_key),
                duration_seconds: None,
                duration_source: None,
                complete_cycles: 0,
                reason: Some("missingReset".to_string()),
            },
            (Some(window_key), true) => PaceStatusPayload {
                state: PaceState::LearningDuration,
                window_key: Some(window_key),
                duration_seconds: None,
                duration_source: None,
                complete_cycles: 0,
                reason: None,
            },
        };
        self.historical_pace = None;
        self
    }

    /// Resolve exact provider/contract duration only for adapters with explicit
    /// duration semantics. Other providers keep using `with_identity`.
    fn with_duration_evidence(
        mut self,
        now: DateTime<Utc>,
        reset_was_supplied: bool,
        provider: Option<DurationEvidence>,
        contract: Option<DurationEvidence>,
    ) -> Self {
        if self.pace_status.window_key.is_none() {
            return self;
        }
        let parsed_reset = self.resets_at.as_deref().and_then(parse_datetime);
        let exact_reset = match (parsed_reset, self.reset_at_evidence) {
            (Some(_), Some(reset)) => Some(reset),
            _ => None,
        };
        if reset_was_supplied != exact_reset.is_some() {
            return self.with_unavailable_reason("invalidEvidence");
        }
        let reset_at = match exact_reset {
            Some(reset) if reset > now => {
                Some(reset.timestamp().max(now.timestamp().saturating_add(1)))
            }
            Some(_) => return self.with_unavailable_reason("invalidEvidence"),
            None => None,
        };
        match resolve_duration(now.timestamp(), reset_at, provider, contract, None) {
            DurationResolution::Ready {
                duration_seconds,
                source,
            } => {
                let evidence = match source {
                    DurationSource::Provider => provider,
                    DurationSource::Contract => contract,
                    DurationSource::Observed => None,
                };
                let Some(evidence) = evidence else {
                    return self.with_unavailable_reason("invalidEvidence");
                };
                self.duration_evidence = Some((evidence, source));
                self.pace_status.state = PaceState::LearningHistory;
                self.pace_status.duration_seconds = Some(duration_seconds);
                self.pace_status.duration_source = Some(source);
                self.pace_status.complete_cycles = 0;
                self.pace_status.reason = None;
                self.historical_pace = None;
                self
            }
            DurationResolution::LearningDuration => self,
            DurationResolution::Unavailable(reason) => self.with_unavailable_reason(match reason {
                DurationUnavailableReason::MissingReset => "missingReset",
                DurationUnavailableReason::InvalidEvidence => "invalidEvidence",
            }),
        }
    }

    pub(crate) fn with_observed_duration_evidence(
        self,
        now: DateTime<Utc>,
        reset_was_supplied: bool,
    ) -> Self {
        let mut window = self.with_duration_evidence(now, reset_was_supplied, None, None);
        if window.pace_status.state == PaceState::LearningDuration {
            window.pace_status.duration_source = Some(DurationSource::Observed);
        }
        window
    }

    fn with_unavailable_reason(mut self, reason: &str) -> Self {
        self.unavailable(reason);
        self
    }

    fn unavailable(&mut self, reason: &str) {
        self.duration_evidence = None;
        self.pace_status = PaceStatusPayload {
            state: PaceState::Unavailable,
            window_key: self.pace_status.window_key.clone(),
            duration_seconds: None,
            duration_source: None,
            complete_cycles: 0,
            reason: Some(reason.to_string()),
        };
        self.historical_pace = None;
    }

    fn validate_wire(&self) -> Result<(), String> {
        if self.card_id.trim().is_empty() {
            return Err("pace cardId must be non-empty".to_string());
        }
        if !self.used_percent.is_finite()
            || !self.remaining_percent.is_finite()
            || !(0.0..=100.0).contains(&self.used_percent)
            || !(0.0..=100.0).contains(&self.remaining_percent)
            || ((self.used_percent + self.remaining_percent) - 100.0).abs() > 1e-6
        {
            return Err("usage percentages must be finite, bounded, and complementary".to_string());
        }
        let has_window_key = self
            .pace_status
            .window_key
            .as_deref()
            .is_some_and(|key| !key.trim().is_empty());
        if self.pace_status.window_key.is_some() && !has_window_key {
            return Err("pace windowKey must be non-empty".to_string());
        }
        let identity_unavailable = self.pace_status.state == PaceState::Unavailable
            && self.pace_status.reason.as_deref() == Some("windowIdentity");
        if has_window_key == identity_unavailable {
            return Err("pace window identity invariant failed".to_string());
        }
        let reset_at = self
            .resets_at
            .as_deref()
            .and_then(parse_datetime)
            .map(|reset| reset.timestamp());
        let valid_reset = reset_at.is_some();
        if self.resets_at.is_some() && !valid_reset {
            return Err("pace resetsAt must be a valid timestamp".to_string());
        }
        let has_duration = self.pace_status.duration_seconds.is_some();
        let observed_learning_source = self.pace_status.state == PaceState::LearningDuration
            && self.pace_status.duration_source == Some(DurationSource::Observed);
        if self
            .pace_status
            .duration_seconds
            .is_some_and(|duration| !valid_duration(duration))
            || (has_duration != self.pace_status.duration_source.is_some()
                && !observed_learning_source)
        {
            return Err("pace duration invariant failed".to_string());
        }
        match self.duration_evidence {
            Some((evidence, source)) => {
                let reset_is_coherent = match source {
                    DurationSource::Provider => evidence.reset_at == reset_at,
                    DurationSource::Contract => evidence.reset_at.is_none(),
                    DurationSource::Observed => false,
                };
                if !reset_is_coherent
                    || self.pace_status.duration_seconds != Some(evidence.duration_seconds)
                    || self.pace_status.duration_source != Some(source)
                {
                    return Err("pace duration evidence and state differ".to_string());
                }
            }
            None if matches!(
                self.pace_status.duration_source,
                Some(DurationSource::Provider | DurationSource::Contract)
            ) =>
            {
                return Err("pace duration source lacks retained evidence".to_string());
            }
            None => {}
        }
        match self.pace_status.state {
            PaceState::LearningDuration => {
                if !valid_reset
                    || has_duration
                    || self.historical_pace.is_some()
                    || self.pace_status.reason.is_some()
                {
                    return Err("learningDuration pace invariant failed".to_string());
                }
            }
            PaceState::LearningHistory => {
                if !valid_reset
                    || !has_duration
                    || self.historical_pace.is_some()
                    || self.pace_status.reason.is_some()
                {
                    return Err("learningHistory pace invariant failed".to_string());
                }
            }
            PaceState::Available => {
                if !valid_reset
                    || !has_duration
                    || self.historical_pace.is_none()
                    || self.pace_status.reason.is_some()
                {
                    return Err("available pace invariant failed".to_string());
                }
            }
            PaceState::Unavailable => {
                if has_duration
                    || self.pace_status.duration_source.is_some()
                    || self.historical_pace.is_some()
                    || self.pace_status.reason.is_none()
                    || (self.pace_status.reason.as_deref() == Some("missingReset")
                        && self.resets_at.is_some())
                {
                    return Err("unavailable pace invariant failed".to_string());
                }
            }
        }
        if let Some(historical) = &self.historical_pace {
            if !historical.expected_used_percent.is_finite()
                || !(0.0..=100.0).contains(&historical.expected_used_percent)
                || historical
                    .eta_seconds
                    .is_some_and(|eta| !eta.is_finite() || eta < 0.0)
                || historical
                    .run_out_probability
                    .is_some_and(|risk| !risk.is_finite() || !(0.0..=1.0).contains(&risk))
                || (historical.eta_seconds.is_none() != historical.will_last_to_reset)
            {
                return Err("historicalPace contains contradictory values".to_string());
            }
        }
        Ok(())
    }

    #[cfg(test)]
    pub(crate) fn label_for_test(&self) -> &str {
        &self.label
    }

    #[cfg(test)]
    pub(crate) fn remaining_for_test(&self) -> f64 {
        self.remaining_percent
    }

    #[cfg(test)]
    pub(crate) fn card_id_for_test(&self) -> &str {
        &self.card_id
    }

    #[cfg(test)]
    pub(crate) fn pace_window_key_for_test(&self) -> Option<&str> {
        self.pace_status.window_key.as_deref()
    }

    #[cfg(test)]
    pub(crate) fn pace_reason_for_test(&self) -> Option<&str> {
        self.pace_status.reason.as_deref()
    }
}

#[derive(Debug, Clone)]
struct CredentialSlot {
    semantic_source: &'static str,
    canonical_location: String,
}

#[derive(Debug, Clone)]
struct ResolvedClaudeToken {
    access_token: String,
    scope_slot: CredentialSlot,
}

#[derive(Debug, Clone)]
struct CodexCredentials {
    access_token: String,
    refresh_token: Option<String>,
    id_token: Option<String>,
    account_id: Option<String>,
    last_refresh: Option<DateTime<Utc>>,
    auth_path: PathBuf,
    raw_json: Value,
    scope_slot: CredentialSlot,
}

impl CodexCredentials {
    fn scope_marker(&self) -> &[u8] {
        self.refresh_token
            .as_deref()
            .map(str::trim)
            .filter(|token| !token.is_empty())
            .unwrap_or_else(|| self.access_token.trim())
            .as_bytes()
    }
}

#[derive(Debug, Clone)]
struct ClaudeCredentials {
    access_token: String,
    refresh_token: Option<String>,
    expires_at: Option<DateTime<Utc>>,
    scopes: Vec<String>,
    rate_limit_tier: Option<String>,
    subscription_type: Option<String>,
    /// Where the credentials were read from, so a rotated token can be written
    /// back to the same place (the Claude CLI shares this store).
    source: ClaudeCredentialSource,
    /// Full credentials JSON as loaded, so a write-back preserves fields we
    /// don't model (merge-update rather than overwrite).
    raw_root: Option<Value>,
    scope_slot: CredentialSlot,
}

impl ClaudeCredentials {
    fn scope_marker(&self) -> Option<&[u8]> {
        match self.source {
            ClaudeCredentialSource::Keychain | ClaudeCredentialSource::File => self
                .refresh_token
                .as_deref()
                .filter(|token| !token.is_empty())
                .map(str::as_bytes),
            ClaudeCredentialSource::Environment => Some(self.access_token.as_bytes()),
        }
    }

    fn resolve_account_scope(&self) -> Result<AccountScope, AccountScopeError> {
        let marker = self
            .scope_marker()
            .ok_or(AccountScopeError::NoTrustedEvidence)?;
        agent_account_scope::resolve_credential(
            "claude",
            self.scope_slot.semantic_source,
            &self.scope_slot.canonical_location,
            marker,
        )
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ClaudeCredentialSource {
    Keychain,
    File,
    /// Token injected via env var — read-only, has no refresh token.
    Environment,
}

#[derive(Debug, Deserialize)]
struct ClaudeCredentialsRoot {
    #[serde(default, rename = "claudeAiOauth")]
    claude_ai_oauth: Option<ClaudeCredentialsOauth>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ClaudeCredentialsOauth {
    access_token: Option<String>,
    refresh_token: Option<String>,
    expires_at: Option<f64>,
    scopes: Option<Vec<String>>,
    rate_limit_tier: Option<String>,
    subscription_type: Option<String>,
}

#[derive(Debug, Deserialize)]
struct CodexUsageResponse {
    #[serde(default)]
    plan_type: Option<String>,
    #[serde(default)]
    rate_limit: Option<CodexRateLimit>,
    #[serde(default)]
    additional_rate_limits: Option<Vec<CodexAdditionalRateLimit>>,
    #[serde(default)]
    credits: Option<CodexCredits>,
}

#[derive(Debug, Deserialize)]
struct CodexRateLimit {
    #[serde(default)]
    primary_window: Option<CodexWindow>,
    #[serde(default)]
    secondary_window: Option<CodexWindow>,
}

#[derive(Debug, Clone, Deserialize)]
struct CodexWindow {
    used_percent: f64,
    reset_at: i64,
    limit_window_seconds: i64,
}

#[derive(Debug, Deserialize)]
struct CodexAdditionalRateLimit {
    #[serde(default)]
    limit_name: Option<String>,
    #[serde(default)]
    metered_feature: Option<String>,
    #[serde(default)]
    rate_limit: Option<CodexRateLimit>,
}

#[derive(Debug, Deserialize)]
struct CodexCredits {
    #[serde(default)]
    unlimited: bool,
    #[serde(default, deserialize_with = "deserialize_optional_f64")]
    balance: Option<f64>,
}

#[derive(Debug, Deserialize, Default)]
struct ClaudeUsageResponse {
    #[serde(default)]
    five_hour: Option<ClaudeWindow>,
    #[serde(default)]
    seven_day: Option<ClaudeWindow>,
    #[serde(default)]
    seven_day_oauth_apps: Option<ClaudeWindow>,
    #[serde(default)]
    seven_day_opus: Option<ClaudeWindow>,
    #[serde(default)]
    seven_day_sonnet: Option<ClaudeWindow>,
    #[serde(default)]
    seven_day_design: Option<ClaudeWindow>,
    #[serde(default)]
    seven_day_claude_design: Option<ClaudeWindow>,
    #[serde(default)]
    claude_design: Option<ClaudeWindow>,
    #[serde(default)]
    design: Option<ClaudeWindow>,
    #[serde(default)]
    seven_day_omelette: Option<ClaudeWindow>,
    #[serde(default)]
    omelette: Option<ClaudeWindow>,
    #[serde(default)]
    omelette_promotional: Option<ClaudeWindow>,
    #[serde(default)]
    seven_day_routines: Option<ClaudeWindow>,
    #[serde(default)]
    seven_day_claude_routines: Option<ClaudeWindow>,
    #[serde(default)]
    claude_routines: Option<ClaudeWindow>,
    #[serde(default)]
    routines: Option<ClaudeWindow>,
    #[serde(default)]
    routine: Option<ClaudeWindow>,
    #[serde(default)]
    seven_day_cowork: Option<ClaudeWindow>,
    #[serde(default)]
    cowork: Option<ClaudeWindow>,
    #[serde(default)]
    extra_usage: Option<ClaudeExtraUsage>,
}

#[derive(Debug, Clone, Deserialize)]
struct ClaudeWindow {
    #[serde(default, deserialize_with = "deserialize_optional_f64")]
    utilization: Option<f64>,
    #[serde(default)]
    resets_at: Option<String>,
}

impl ClaudeWindow {
    fn has_valid_utilization(&self) -> bool {
        self.utilization
            .is_some_and(|used| used.is_finite() && (0.0..=100.0).contains(&used))
    }
}

#[derive(Debug, Deserialize)]
struct ClaudeExtraUsage {
    #[serde(default)]
    is_enabled: bool,
    #[serde(default, deserialize_with = "deserialize_optional_f64")]
    monthly_limit: Option<f64>,
    #[serde(default, deserialize_with = "deserialize_optional_f64")]
    used_credits: Option<f64>,
    #[serde(default, deserialize_with = "deserialize_optional_f64")]
    utilization: Option<f64>,
    #[serde(default)]
    currency: Option<String>,
}

#[derive(Debug, Deserialize)]
struct ClaudeRefreshResponse {
    access_token: String,
    #[serde(default, deserialize_with = "deserialize_optional_non_empty_string")]
    refresh_token: Option<String>,
    expires_in: i64,
}

pub async fn run() -> AgentUsagePayload {
    let generated_at = Utc::now().to_rfc3339_opts(SecondsFormat::Millis, true);
    let (codex, claude, antigravity, copilot, grok) = tokio::join!(
        fetch_codex(),
        fetch_claude(),
        fetch_antigravity(),
        fetch_copilot(),
        fetch_grok()
    );
    let mut agents = vec![codex, claude, antigravity];
    // Copilot only appears when signed in (via opencode); skip a bare not-signed-in error card.
    if let Some(copilot) = copilot {
        agents.push(copilot);
    }
    // Grok only appears when ~/.grok/auth.json has credentials.
    if let Some(grok) = grok {
        agents.push(grok);
    }
    for snapshot in &mut agents {
        retain_unique_windows(&mut snapshot.windows);
    }
    AgentUsagePayload {
        generated_at,
        agents,
        opencode_subscriptions: crate::opencode_integrations::detect_subscriptions(),
    }
}

fn retain_unique_windows(windows: &mut Vec<UsageWindow>) {
    let mut card_ids = HashSet::new();
    let mut window_keys = HashSet::new();
    windows.retain(|window| {
        let key = window.pace_status.window_key.as_ref();
        if card_ids.contains(&window.card_id) || key.is_some_and(|key| window_keys.contains(key)) {
            return false;
        }
        card_ids.insert(window.card_id.clone());
        if let Some(key) = key {
            window_keys.insert(key.clone());
        }
        true
    });
}

fn enrich_snapshot(snapshot: &mut AgentUsageSnapshot, now: i64) {
    enrich_snapshot_with(snapshot, now, |active_keys, observations, now| {
        crate::agent_quota_history::record_observations_and_evaluate(active_keys, observations, now)
    });
}

fn enrich_snapshot_with<F>(snapshot: &mut AgentUsageSnapshot, now: i64, mut record: F)
where
    F: FnMut(
        &[SeriesKey],
        &[QuotaObservation],
        i64,
    ) -> Result<Vec<BatchObservationResult>, HistoryError>,
{
    retain_unique_windows(&mut snapshot.windows);

    let Ok(account_scope) = snapshot.account_scope.as_ref() else {
        for window in &mut snapshot.windows {
            if window.pace_status.window_key.is_some()
                && window.pace_status.reason.as_deref() != Some("nonRecurring")
            {
                window.unavailable("accountScope");
            }
        }
        return;
    };
    let provider_id = snapshot.client_id.clone();
    let account_scope = account_scope.as_str().to_string();
    let mut active_keys = Vec::new();
    let mut observations = Vec::new();
    let mut mapped_indices = Vec::new();

    for (index, window) in snapshot.windows.iter_mut().enumerate() {
        if window.pace_status.reason.as_deref() == Some("nonRecurring") {
            continue;
        }
        let Some(window_key) = window.pace_status.window_key.as_deref() else {
            continue;
        };
        let key = SeriesKey::new(provider_id.clone(), account_scope.clone(), window_key);
        active_keys.push(key.clone());
        if window.pace_status.state == PaceState::Unavailable {
            continue;
        }

        let reset_at = window
            .resets_at
            .as_deref()
            .and_then(parse_datetime)
            .map(|reset| reset.timestamp());
        let valid_reset = reset_at.is_some_and(|reset_at| reset_at > now);
        let valid_percent =
            window.used_percent.is_finite() && (0.0..=100.0).contains(&window.used_percent);
        if !valid_percent {
            window.used_percent = if window.used_percent.is_finite() {
                window.used_percent.clamp(0.0, 100.0)
            } else {
                0.0
            };
        }
        window.remaining_percent = 100.0 - window.used_percent;
        if window.resets_at.is_some() && reset_at.is_none() {
            window.resets_at = None;
            window.reset_at_evidence = None;
            window.reset_text = None;
        }
        if !valid_reset || !valid_percent {
            window.unavailable("invalidEvidence");
            continue;
        }
        let Some(reset_at) = reset_at else {
            window.unavailable("invalidEvidence");
            continue;
        };
        let (provider, contract) = match window.duration_evidence {
            Some((evidence, DurationSource::Provider)) => (Some(evidence), None),
            Some((evidence, DurationSource::Contract)) => (None, Some(evidence)),
            Some((_, DurationSource::Observed)) => {
                window.unavailable("invalidEvidence");
                continue;
            }
            None => (None, None),
        };
        observations.push(QuotaObservation {
            key,
            reset_at: Some(reset_at),
            used_percent: window.used_percent,
            provider,
            contract,
        });
        mapped_indices.push(index);
    }

    if active_keys.is_empty() {
        return;
    }

    let results = match record(&active_keys, &observations, now) {
        Ok(results) if results.len() == mapped_indices.len() => results,
        Ok(_) => {
            for index in mapped_indices {
                snapshot.windows[index].unavailable("history");
            }
            return;
        }
        Err(error) => {
            let reason = history_error_reason(error);
            for index in mapped_indices {
                snapshot.windows[index].unavailable(reason);
            }
            return;
        }
    };

    for (index, result) in mapped_indices.into_iter().zip(results) {
        let window = &mut snapshot.windows[index];
        match result {
            Err(error) => window.unavailable(history_error_reason(error)),
            Ok((
                HistoryOutcome::Ready {
                    duration_seconds,
                    source,
                    ..
                },
                historical,
                complete_cycles,
            )) => {
                let reset_at = window
                    .resets_at
                    .as_deref()
                    .and_then(parse_datetime)
                    .map(|reset| reset.timestamp());
                if !reset_at.is_some_and(|reset_at| {
                    history_duration_is_coherent(window, reset_at, now, duration_seconds, source)
                }) {
                    window.unavailable("history");
                    continue;
                }
                match historical {
                    Some(pace) if historical_pace_is_coherent(&pace) => {
                        window.pace_status = PaceStatusPayload {
                            state: PaceState::Available,
                            window_key: window.pace_status.window_key.clone(),
                            duration_seconds: Some(duration_seconds),
                            duration_source: Some(source),
                            complete_cycles,
                            reason: None,
                        };
                        window.historical_pace = Some(historical_pace_payload(pace));
                    }
                    Some(_) => window.unavailable("history"),
                    None => {
                        window.pace_status = PaceStatusPayload {
                            state: PaceState::LearningHistory,
                            window_key: window.pace_status.window_key.clone(),
                            duration_seconds: Some(duration_seconds),
                            duration_source: Some(source),
                            complete_cycles,
                            reason: None,
                        };
                        window.historical_pace = None;
                    }
                }
            }
            Ok((HistoryOutcome::LearningDuration, None, 0))
                if window.duration_evidence.is_none() =>
            {
                window.pace_status = PaceStatusPayload {
                    state: PaceState::LearningDuration,
                    window_key: window.pace_status.window_key.clone(),
                    duration_seconds: None,
                    duration_source: Some(DurationSource::Observed),
                    complete_cycles: 0,
                    reason: None,
                };
                window.historical_pace = None;
            }
            Ok((HistoryOutcome::Unavailable(reason), None, 0)) => {
                if reason == DurationUnavailableReason::MissingReset && window.resets_at.is_some() {
                    window.unavailable("history");
                } else {
                    window.unavailable(duration_unavailable_reason(reason));
                }
            }
            Ok(_) => window.unavailable("history"),
        }
    }
}

fn history_duration_is_coherent(
    window: &UsageWindow,
    reset_at: i64,
    now: i64,
    duration_seconds: i64,
    source: DurationSource,
) -> bool {
    let (provider, contract) = match window.duration_evidence {
        Some((evidence, DurationSource::Provider)) => (Some(evidence), None),
        Some((evidence, DurationSource::Contract)) => (None, Some(evidence)),
        Some((_, DurationSource::Observed)) => return false,
        None => (None, None),
    };
    if source == DurationSource::Observed && window.duration_evidence.is_some() {
        return false;
    }
    let observed = (source == DurationSource::Observed)
        .then(|| DurationEvidence::observed(reset_at, duration_seconds));
    matches!(
        resolve_duration(now, Some(reset_at), provider, contract, observed),
        DurationResolution::Ready {
            duration_seconds: resolved,
            source: resolved_source,
        } if resolved == duration_seconds && resolved_source == source
    )
}

fn history_error_reason(error: HistoryError) -> &'static str {
    if error == HistoryError::StoreCapacity {
        "storeCapacity"
    } else {
        "history"
    }
}

fn duration_unavailable_reason(reason: DurationUnavailableReason) -> &'static str {
    match reason {
        DurationUnavailableReason::MissingReset => "missingReset",
        DurationUnavailableReason::InvalidEvidence => "invalidEvidence",
    }
}

fn historical_pace_is_coherent(pace: &HistoricalPace) -> bool {
    pace.expected_percent.is_finite()
        && (0.0..=100.0).contains(&pace.expected_percent)
        && pace
            .eta_seconds
            .is_none_or(|eta| eta.is_finite() && eta >= 0.0)
        && pace
            .run_out_probability
            .is_none_or(|probability| probability.is_finite() && (0.0..=1.0).contains(&probability))
        && (pace.eta_seconds.is_none() == pace.will_last_to_reset)
}

fn historical_pace_payload(pace: HistoricalPace) -> HistoricalPacePayload {
    HistoricalPacePayload {
        expected_used_percent: pace.expected_percent,
        eta_seconds: pace.eta_seconds,
        will_last_to_reset: pace.will_last_to_reset,
        run_out_probability: pace.run_out_probability,
    }
}

async fn fetch_grok() -> Option<AgentUsageSnapshot> {
    let now = Utc::now();
    match agent_grok::fetch(now).await? {
        Ok(data) => Some(AgentUsageSnapshot {
            client_id: "grok".to_string(),
            source: "oauth".to_string(),
            updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
            identity: data.identity,
            account_scope: Err(AccountScopeError::NoTrustedEvidence),
            windows: data.windows,
            credits: None,
            error: None,
        }),
        Err(error) => Some(AgentUsageSnapshot {
            client_id: "grok".to_string(),
            source: "oauth".to_string(),
            updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
            identity: None,
            account_scope: Err(AccountScopeError::NoTrustedEvidence),
            windows: Vec::new(),
            credits: None,
            error: Some(error),
        }),
    }
}

async fn fetch_copilot() -> Option<AgentUsageSnapshot> {
    // No opencode Copilot auth → no card at all (rather than an error row).
    crate::opencode_integrations::github_copilot_token()?;
    let now = Utc::now();
    Some(match agent_copilot::fetch(now).await {
        Ok(data) => AgentUsageSnapshot {
            client_id: "copilot".to_string(),
            source: "oauth".to_string(),
            updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
            identity: data.identity,
            account_scope: Err(AccountScopeError::NoTrustedEvidence),
            windows: data.windows,
            credits: None,
            error: None,
        },
        Err(error) => AgentUsageSnapshot {
            client_id: "copilot".to_string(),
            source: "oauth".to_string(),
            updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
            identity: None,
            account_scope: Err(AccountScopeError::NoTrustedEvidence),
            windows: Vec::new(),
            credits: None,
            error: Some(error),
        },
    })
}

async fn fetch_antigravity() -> AgentUsageSnapshot {
    let now = Utc::now();
    match agent_antigravity::fetch(now).await {
        Ok(fetched) => AgentUsageSnapshot {
            client_id: "antigravity".to_string(),
            source: fetched.source,
            updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
            identity: fetched.identity,
            account_scope: fetched.account_scope,
            windows: fetched.windows,
            credits: None,
            error: None,
        },
        Err(error) => AgentUsageSnapshot {
            client_id: "antigravity".to_string(),
            source: "oauth".to_string(),
            updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
            identity: None,
            account_scope: Err(AccountScopeError::NoTrustedEvidence),
            windows: Vec::new(),
            credits: None,
            error: Some(error),
        },
    }
}

async fn fetch_codex() -> AgentUsageSnapshot {
    match fetch_codex_inner().await {
        Ok(snapshot) => snapshot,
        Err(error) => AgentUsageSnapshot {
            client_id: "codex".to_string(),
            source: "oauth".to_string(),
            updated_at: Utc::now().to_rfc3339_opts(SecondsFormat::Millis, true),
            identity: None,
            account_scope: Err(AccountScopeError::NoTrustedEvidence),
            windows: Vec::new(),
            credits: None,
            error: Some(error),
        },
    }
}

/// Claude's `/api/oauth/usage` rate-limits aggressively (and the budget is
/// shared with any other monitor on the account, e.g. codexbar). Modeled on
/// codexbar's ClaudeOAuthUsageRateLimitGate: after a 429, stop hitting the
/// endpoint until Retry-After (default 5 min) and serve the last good
/// snapshot so the card keeps its data instead of flashing an error.
struct ClaudeUsageGate {
    blocked_until: Option<DateTime<Utc>>,
    last_good: Option<AgentUsageSnapshot>,
}

static CLAUDE_USAGE_GATE: Mutex<ClaudeUsageGate> = Mutex::new(ClaudeUsageGate {
    blocked_until: None,
    last_good: None,
});

/// Lock the gate, recovering from a poisoned mutex instead of panicking. Under
/// the release profile's unwind + FFI-boundary `catch_unwind` (see `guarded` in
/// lib.rs), a panic caught mid-section poisons this static; `into_inner()` keeps
/// the 429 gate working for the rest of the process instead of wedging every
/// later `tb_agent_usage` call — same stance as the live-tail lock in lib.rs.
fn lock_gate() -> std::sync::MutexGuard<'static, ClaudeUsageGate> {
    CLAUDE_USAGE_GATE
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

fn claude_gate_blocked_until(now: DateTime<Utc>) -> Option<DateTime<Utc>> {
    let mut gate = lock_gate();
    match gate.blocked_until {
        Some(until) if until > now => Some(until),
        Some(_) => {
            gate.blocked_until = None;
            None
        }
        None => None,
    }
}

fn claude_gate_record_rate_limit(retry_after: Option<DateTime<Utc>>, now: DateTime<Utc>) {
    let blocked_until = retry_after
        .filter(|until| *until > now)
        .unwrap_or_else(|| now + chrono::Duration::minutes(5));
    lock_gate().blocked_until = Some(blocked_until);
}

fn claude_gate_record_success(snapshot: &AgentUsageSnapshot) {
    let mut gate = lock_gate();
    gate.blocked_until = None;
    gate.last_good = Some(snapshot.clone());
}

/// While the gate is closed, prefer the cached snapshot (its `updated_at`
/// stays honest); with nothing cached yet, surface a countdown error.
fn claude_gate_fallback(blocked_until: DateTime<Utc>, now: DateTime<Utc>) -> AgentUsageSnapshot {
    if let Some(mut snapshot) = lock_gate().last_good.clone() {
        // A cached 429 response is not current account-scope evidence. Keeping
        // the stale scope would attribute a later poll to an unauthenticated account.
        snapshot.account_scope = Err(AccountScopeError::NoTrustedEvidence);
        return snapshot;
    }
    let wait_secs = (blocked_until - now).num_seconds().max(0);
    AgentUsageSnapshot {
        client_id: "claude".to_string(),
        source: "oauth".to_string(),
        updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
        identity: None,
        account_scope: Err(AccountScopeError::NoTrustedEvidence),
        windows: Vec::new(),
        credits: None,
        error: Some(format!(
            "Claude OAuth usage endpoint is rate limited. Retrying automatically in ~{}s.",
            wait_secs
        )),
    }
}

fn parse_retry_after(value: Option<&reqwest::header::HeaderValue>) -> Option<DateTime<Utc>> {
    let raw = value?.to_str().ok()?.trim();
    if raw.is_empty() {
        return None;
    }
    if let Ok(seconds) = raw.parse::<i64>() {
        return (seconds >= 0).then(|| Utc::now() + chrono::Duration::seconds(seconds));
    }
    DateTime::parse_from_rfc2822(raw)
        .ok()
        .map(|t| t.with_timezone(&Utc))
}

async fn fetch_claude() -> AgentUsageSnapshot {
    let now = Utc::now();
    if let Some(blocked_until) = claude_gate_blocked_until(now) {
        return claude_gate_fallback(blocked_until, now);
    }
    match fetch_claude_inner().await {
        Ok(mut snapshot) => {
            enrich_snapshot(&mut snapshot, now.timestamp());
            // Cache the display-ready snapshot. A later 429 fallback returns it
            // without another enrichment pass or history write.
            claude_gate_record_success(&snapshot);
            snapshot
        }
        Err(error) => {
            // A 429 inside fetch_claude_inner arms the gate; fall back to the
            // cached, already-enriched snapshot rather than blanking the card.
            let now = Utc::now();
            if let Some(blocked_until) = claude_gate_blocked_until(now) {
                return claude_gate_fallback(blocked_until, now);
            }
            // "unconfigured" == no credential at all, so the UI shows a setup
            // prompt; every other error is a real failure of a present credential.
            let source = if error.as_str() == CLAUDE_UNCONFIGURED_ERROR {
                "unconfigured"
            } else {
                "oauth"
            };
            AgentUsageSnapshot {
                client_id: "claude".to_string(),
                source: source.to_string(),
                updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
                identity: None,
                account_scope: Err(AccountScopeError::NoTrustedEvidence),
                windows: Vec::new(),
                credits: None,
                error: Some(error),
            }
        }
    }
}

async fn fetch_codex_inner() -> Result<AgentUsageSnapshot, String> {
    let mut credentials = load_codex_credentials()?;
    let mut refreshed_scope = None;
    if credentials_needs_refresh(credentials.last_refresh) {
        if credentials
            .refresh_token
            .as_deref()
            .unwrap_or("")
            .is_empty()
        {
            return Err(
                "Codex OAuth token needs refresh but auth.json has no refresh token.".to_string(),
            );
        }
        let refreshed = refresh_codex_credentials(&credentials.auth_path).await?;
        credentials = refreshed.0;
        refreshed_scope = Some(refreshed.1);
    }

    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(30))
        .build()
        .map_err(|e| format!("build Codex OAuth client: {}", e))?;

    let mut request = client
        .get(CODEX_USAGE_URL)
        .bearer_auth(&credentials.access_token)
        .header(reqwest::header::ACCEPT, "application/json")
        .header(reqwest::header::USER_AGENT, "TokenBar");
    let request_account_id = credentials
        .account_id
        .as_deref()
        .map(str::trim)
        .filter(|value| !value.is_empty());
    if let Some(account_id) = request_account_id {
        request = request.header("ChatGPT-Account-Id", account_id);
    }

    let response = request
        .send()
        .await
        .map_err(|e| format!("Codex OAuth request failed: {}", e))?;
    let status = response.status();
    let body = response
        .text()
        .await
        .map_err(|e| format!("read Codex OAuth response: {}", e))?;

    if status == reqwest::StatusCode::UNAUTHORIZED || status == reqwest::StatusCode::FORBIDDEN {
        return Err(
            "Codex OAuth token expired or invalid. Run `codex` to log in again.".to_string(),
        );
    }
    if !status.is_success() {
        return Err(format!("Codex usage API returned {}.", status.as_u16()));
    }

    let usage: CodexUsageResponse =
        serde_json::from_str(&body).map_err(|e| format!("decode Codex usage response: {}", e))?;
    let now = Utc::now();
    let account_scope = resolve_codex_account_scope(
        refreshed_scope,
        request_account_id,
        |account_id| {
            agent_account_scope::resolve_authoritative(
                "codex",
                AuthoritativeIdKind::OpaqueId,
                account_id,
            )
        },
        || {
            agent_account_scope::resolve_credential(
                "codex",
                credentials.scope_slot.semantic_source,
                &credentials.scope_slot.canonical_location,
                credentials.scope_marker(),
            )
        },
    );
    let identity = Some(AgentIdentity {
        email: credentials.id_token.as_deref().and_then(jwt_email),
        plan: usage.plan_type.as_deref().map(clean_plan).or_else(|| {
            credentials
                .id_token
                .as_deref()
                .and_then(jwt_plan)
                .map(clean_plan)
        }),
    });
    let windows = codex_windows(
        usage.rate_limit.as_ref(),
        usage.additional_rate_limits.as_deref(),
        now,
    );
    if windows.is_empty() && usage.credits.as_ref().and_then(|c| c.balance).is_none() {
        return Err("Codex usage API returned no rate-limit windows.".to_string());
    }

    // Only the non-empty account ID actually attached to this successful request
    // may authorize legacy migration. Migration remains best-effort so the live
    // v3 observation still records if import fails.
    maybe_migrate_codex_v2_with(
        request_account_id,
        &account_scope,
        now.timestamp(),
        crate::agent_quota_history::migrate_codex_v2,
    );

    let mut snapshot = AgentUsageSnapshot {
        client_id: "codex".to_string(),
        source: "oauth".to_string(),
        updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
        identity,
        account_scope,
        windows,
        credits: usage.credits.map(|credits| CreditsSnapshot {
            remaining: credits.balance,
            unlimited: credits.unlimited,
        }),
        error: None,
    };
    enrich_snapshot(&mut snapshot, now.timestamp());
    Ok(snapshot)
}

fn resolve_codex_account_scope<ResolveAuthoritative, ResolveCredential>(
    refreshed_scope: Option<Result<AccountScope, AccountScopeError>>,
    request_account_id: Option<&str>,
    resolve_authoritative: ResolveAuthoritative,
    resolve_credential: ResolveCredential,
) -> Result<AccountScope, AccountScopeError>
where
    ResolveAuthoritative: FnOnce(&str) -> Result<AccountScope, AccountScopeError>,
    ResolveCredential: FnOnce() -> Result<AccountScope, AccountScopeError>,
{
    if let Some(Err(error)) = refreshed_scope.as_ref() {
        return Err(*error);
    }
    if let Some(account_id) = request_account_id {
        return resolve_authoritative(account_id);
    }
    refreshed_scope.unwrap_or_else(resolve_credential)
}

fn maybe_migrate_codex_v2_with<F, T>(
    request_account_id: Option<&str>,
    account_scope: &Result<AccountScope, AccountScopeError>,
    now: i64,
    migrate: F,
) where
    F: FnOnce(&str, &str, i64) -> Result<T, HistoryError>,
{
    let Some(request_account_id) = request_account_id
        .map(str::trim)
        .filter(|account_id| !account_id.is_empty())
    else {
        return;
    };
    let Ok(account_scope) = account_scope else {
        return;
    };
    let _ = migrate(request_account_id, account_scope.as_str(), now);
}

async fn fetch_claude_inner() -> Result<AgentUsageSnapshot, String> {
    // Mirror Claude Code's auth precedence: CLAUDE_CODE_OAUTH_TOKEN (our env, or
    // harvested from the user's ~/.zshrc) outranks a stored subscription /login,
    // because Claude Code itself consumes that token first. So TokenBar reports
    // the account Claude Code is actually spending against, read from the
    // ratelimit headers. (This is why the harvest runs even for /login users.)
    if let Some(token) = resolve_claude_code_oauth_token().await {
        return claude_header_snapshot(
            &claude_credentials_from_access_token(token),
            Utc::now(),
            None,
        )
        .await;
    }

    // A stored full login (TokenBar env override / Keychain / file) uses the
    // richer oauth/usage endpoint. Any failure -- a login that can't refresh, or
    // a credentials file that exists but can't be read (permissions / I/O) -- is
    // deferred: we still try the tokenbar Keychain setup-token below, and surface
    // the error only if that misses too. So a stale login / read error never
    // strands a working setup-token, yet a genuine failure isn't masked by the
    // generic "unconfigured" setup prompt.
    let deferred_error: Option<String> = match load_claude_login_credentials() {
        Ok(Some(credentials)) => match fetch_claude_oauth_usage(credentials).await {
            Ok(snapshot) => return Ok(snapshot),
            Err(login_error) => Some(login_error),
        },
        Ok(None) => None,
        Err(read_error) => Some(read_error),
    };

    // Last resort: the tokenbar-claude-oauth-token Keychain item reads limits
    // straight from the ratelimit headers (no oauth/usage GET, no 429 gate).
    if let Some(token) = resolve_claude_keychain_token() {
        return claude_header_snapshot(
            &claude_credentials_from_access_token(token),
            Utc::now(),
            None,
        )
        .await;
    }

    Err(deferred_error.unwrap_or_else(|| CLAUDE_UNCONFIGURED_ERROR.to_string()))
}

async fn fetch_claude_oauth_usage(
    mut credentials: ClaudeCredentials,
) -> Result<AgentUsageSnapshot, String> {
    let mut refreshed_scope = None;
    if claude_credentials_expired(&credentials) {
        let refreshed = refresh_claude_credentials(&credentials).await?;
        credentials = refreshed.0;
        refreshed_scope = Some(refreshed.1);
    }

    if !credentials.scopes.is_empty()
        && !credentials
            .scopes
            .iter()
            .any(|scope| scope == "user:profile")
    {
        // Inference-only token declared explicit non-user:profile scopes — skip
        // the (guaranteed-403) oauth/usage GET and read limits from headers.
        return claude_header_snapshot(&credentials, Utc::now(), refreshed_scope).await;
    }

    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(30))
        .build()
        .map_err(|e| format!("build Claude OAuth client: {}", e))?;

    let response = client
        .get(CLAUDE_USAGE_URL)
        .bearer_auth(&credentials.access_token)
        .header(reqwest::header::ACCEPT, "application/json")
        .header(reqwest::header::CONTENT_TYPE, "application/json")
        .header(reqwest::header::USER_AGENT, claude_user_agent())
        .header("anthropic-beta", "oauth-2025-04-20")
        .send()
        .await
        .map_err(|e| format!("Claude OAuth request failed: {}", e))?;
    let status = response.status();
    let retry_after = if status == reqwest::StatusCode::TOO_MANY_REQUESTS {
        parse_retry_after(response.headers().get(reqwest::header::RETRY_AFTER))
    } else {
        None
    };
    let body = response
        .text()
        .await
        .map_err(|e| format!("read Claude OAuth response: {}", e))?;

    if status == reqwest::StatusCode::UNAUTHORIZED {
        return Err(
            "Claude OAuth token expired or invalid. Run `claude` to re-authenticate.".to_string(),
        );
    }
    if status == reqwest::StatusCode::FORBIDDEN {
        // oauth/usage requires user:profile. An inference-only token (e.g.
        // `claude setup-token`) is denied *specifically* for that scope — fall
        // back to the unified rate-limit headers, which it *is* allowed to read.
        // Any other 403 keeps the actionable re-auth error (and skips the probe,
        // so we don't spend an inference call on an unrelated denial).
        if body.contains("user:profile") {
            return claude_header_snapshot(&credentials, Utc::now(), refreshed_scope).await;
        }
        return Err(
            "Claude OAuth usage was denied. Run `claude logout && claude login` to grant user:profile."
                .to_string(),
        );
    }
    if status == reqwest::StatusCode::TOO_MANY_REQUESTS {
        claude_gate_record_rate_limit(retry_after, Utc::now());
        return Err(
            "Claude OAuth usage endpoint is rate limited. Backing off automatically.".to_string(),
        );
    }
    if !status.is_success() {
        return Err(format!("Claude usage API returned {}.", status.as_u16()));
    }

    let usage: ClaudeUsageResponse =
        serde_json::from_str(&body).map_err(|e| format!("decode Claude usage response: {}", e))?;
    let now = Utc::now();
    let windows = claude_windows(&usage, now);
    if windows.is_empty() {
        return Err("Claude usage API returned no rate-limit windows.".to_string());
    }
    let account_scope = refreshed_scope.unwrap_or_else(|| credentials.resolve_account_scope());

    Ok(AgentUsageSnapshot {
        client_id: "claude".to_string(),
        source: "oauth".to_string(),
        updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
        identity: Some(AgentIdentity {
            email: None,
            plan: first_non_empty([
                credentials.subscription_type.as_deref(),
                credentials.rate_limit_tier.as_deref(),
            ])
            .map(clean_plan),
        }),
        account_scope,
        windows,
        credits: claude_credits(usage.extra_usage.as_ref()),
        error: None,
    })
}

/// Fallback for inference-only tokens (`claude setup-token`): the oauth/usage
/// endpoint requires `user:profile`, but a minimal `/v1/messages` request the
/// token *can* make returns `anthropic-ratelimit-unified-*` headers carrying the
/// same Session/Weekly windows. Reads headers on 200 AND 429 (an over-limit
/// token still returns them). Does NOT arm the oauth/usage rate-limit gate.
/// Cache for the header-probe windows. The probe is a real `/v1/messages`
/// inference (it spends the very budget it measures), so reuse the result across
/// the frequent quota polls (60s popover / 300s tray) instead of probing on
/// every refresh. Keyed on the token so a changed token re-probes.
/// `(fetched_at, token, windows)` — the token keys the entry so a changed token
/// re-probes rather than serving another account's cached windows.
type ClaudeHeaderCacheEntry = (DateTime<Utc>, String, Vec<UsageWindow>);
static CLAUDE_HEADER_CACHE: Mutex<Option<ClaudeHeaderCacheEntry>> = Mutex::new(None);
const CLAUDE_HEADER_TTL_SECS: i64 = 300;

/// Refresh the relative `reset_text` on cached header windows so a 300s-cached
/// probe doesn't show a frozen countdown. Returns None if any window's reset has
/// already passed — the cache is then stale, so the caller re-probes for fresh
/// utilization instead of serving post-reset numbers.
fn refresh_cached_windows(windows: &[UsageWindow], now: DateTime<Utc>) -> Option<Vec<UsageWindow>> {
    let mut refreshed = Vec::with_capacity(windows.len());
    for window in windows {
        let mut window = window.clone();
        if let Some(reset) = window.resets_at.as_deref().and_then(parse_datetime) {
            if now >= reset {
                return None;
            }
            window.reset_text = Some(reset_text(reset, now));
        }
        refreshed.push(window);
    }
    Some(refreshed)
}

async fn fetch_claude_via_headers(access_token: &str) -> Result<Vec<UsageWindow>, String> {
    {
        let now = Utc::now();
        let guard = CLAUDE_HEADER_CACHE
            .lock()
            .unwrap_or_else(|e| e.into_inner());
        if let Some((fetched_at, token, windows)) = guard.as_ref() {
            if token == access_token && (now - *fetched_at).num_seconds() < CLAUDE_HEADER_TTL_SECS {
                if let Some(refreshed) = refresh_cached_windows(windows, now) {
                    return Ok(refreshed);
                }
            }
        }
    }

    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(30))
        .build()
        .map_err(|e| format!("build Claude header-probe client: {}", e))?;

    let response = client
        .post(CLAUDE_MESSAGES_URL)
        .bearer_auth(access_token)
        .header(reqwest::header::ACCEPT, "application/json")
        .header(reqwest::header::CONTENT_TYPE, "application/json")
        .header(reqwest::header::USER_AGENT, claude_user_agent())
        .header("anthropic-version", "2023-06-01")
        .header("anthropic-beta", "oauth-2025-04-20")
        .json(&serde_json::json!({
            "model": CLAUDE_PROBE_MODEL,
            "max_tokens": 1,
            "messages": [{ "role": "user", "content": "hi" }],
        }))
        .send()
        .await
        .map_err(|e| format!("Claude header probe failed: {}", e))?;

    let status = response.status();
    // Read headers before consuming the body — this returns an owned Vec, ending
    // the borrow of `response`.
    let windows = parse_unified_ratelimit_windows(response.headers(), Utc::now());

    if status.is_success() || status == reqwest::StatusCode::TOO_MANY_REQUESTS {
        if windows.is_empty() {
            return Err("Claude header probe returned no unified rate-limit headers.".to_string());
        }
        {
            let mut guard = CLAUDE_HEADER_CACHE
                .lock()
                .unwrap_or_else(|e| e.into_inner());
            *guard = Some((Utc::now(), access_token.to_string(), windows.clone()));
        }
        return Ok(windows);
    }

    let body = response.text().await.unwrap_or_default();
    Err(format!(
        "Claude header probe returned {} ({}).",
        status.as_u16(),
        body.chars().take(200).collect::<String>()
    ))
}

/// Build a Claude snapshot from the unified rate-limit headers. Shared by the
/// scope-guard and HTTP-403 branches of `fetch_claude_inner`. `source` is
/// `"setup-token"` — it doubles as the limits-card badge, so it names the auth
/// method the user recognizes rather than the fetch mechanism, and still lets
/// telemetry tell it apart from the richer oauth/usage path.
async fn claude_header_snapshot(
    credentials: &ClaudeCredentials,
    now: DateTime<Utc>,
    account_scope: Option<Result<AccountScope, AccountScopeError>>,
) -> Result<AgentUsageSnapshot, String> {
    let windows = fetch_claude_via_headers(&credentials.access_token).await?;
    let account_scope = account_scope.unwrap_or_else(|| credentials.resolve_account_scope());
    Ok(AgentUsageSnapshot {
        client_id: "claude".to_string(),
        source: "setup-token".to_string(),
        updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
        identity: Some(AgentIdentity {
            email: None,
            plan: first_non_empty([
                credentials.subscription_type.as_deref(),
                credentials.rate_limit_tier.as_deref(),
            ])
            .map(clean_plan),
        }),
        account_scope,
        windows,
        credits: None,
        error: None,
    })
}

fn load_codex_credentials() -> Result<CodexCredentials, String> {
    load_codex_credentials_from(&codex_home().join("auth.json"))
}

fn load_codex_credentials_from(auth_path: &Path) -> Result<CodexCredentials, String> {
    let raw = fs::read_to_string(auth_path)
        .map_err(|_| "Codex auth.json not found. Run `codex` to log in.".to_string())?;
    let raw_json: Value =
        serde_json::from_str(&raw).map_err(|e| format!("decode Codex auth.json: {}", e))?;

    if raw_json
        .get("OPENAI_API_KEY")
        .and_then(Value::as_str)
        .is_some_and(|key| !key.trim().is_empty())
    {
        return Err(
            "Codex is using API-key auth; OAuth usage limits require `codex login`.".to_string(),
        );
    }

    let tokens = raw_json
        .get("tokens")
        .and_then(Value::as_object)
        .ok_or_else(|| "Codex auth.json exists but contains no OAuth tokens.".to_string())?;
    let access_token = string_key(tokens, "access_token", "accessToken")
        .ok_or_else(|| "Codex auth.json has no access token.".to_string())?;
    let refresh_token = string_key(tokens, "refresh_token", "refreshToken");
    let id_token = string_key(tokens, "id_token", "idToken");
    let account_id = string_key(tokens, "account_id", "accountId");
    let last_refresh = raw_json
        .get("last_refresh")
        .and_then(Value::as_str)
        .and_then(parse_datetime);

    Ok(CodexCredentials {
        access_token,
        refresh_token,
        id_token,
        account_id,
        last_refresh,
        auth_path: auth_path.to_path_buf(),
        raw_json,
        scope_slot: CredentialSlot {
            semantic_source: "codex-auth-json",
            canonical_location: agent_account_scope::canonical_file_location(
                auth_path,
                Some("tokens"),
            )
            .map_err(|_| "Codex auth location cannot be scoped safely.".to_string())?,
        },
    })
}

/// Marker error for "no Claude credential is configured at all" (as opposed to a
/// credential that exists but failed). `fetch_claude` turns this into a snapshot
/// with `source == "unconfigured"`, so the UI shows a setup prompt rather than a
/// red error.
const CLAUDE_UNCONFIGURED_ERROR: &str = "Claude OAuth credentials not found. Run `claude` to authenticate, or set CLAUDE_CODE_OAUTH_TOKEN / add a `tokenbar-claude-oauth-token` Keychain item to use a setup-token.";

/// Full-login credentials: structured `claudeAiOauth` blobs (Keychain
/// `Claude Code-credentials`, then `~/.claude/.credentials.json`) plus the
/// TokenBar env override. These carry refresh tokens / scopes / expiry and go
/// through the richer oauth/usage endpoint. A present-but-logged-out entry (has
/// `claudeAiOauth` but no `accessToken` — the #26 daily-logout state) or an
/// unparseable blob is skipped, not treated as a hard error, so a configured
/// setup-token can still take over.
fn load_claude_login_credentials() -> Result<Option<ClaudeCredentials>, String> {
    if let Some(credentials) = load_claude_credentials_from_environment()? {
        return Ok(Some(credentials));
    }
    if let Some(raw) = load_claude_credentials_from_keychain()? {
        if let Ok(credentials) =
            parse_claude_credentials_data(&raw, ClaudeCredentialSource::Keychain)
        {
            return Ok(Some(credentials));
        }
    }
    match fs::read_to_string(claude_credentials_path()) {
        Ok(raw) => {
            if let Ok(credentials) =
                parse_claude_credentials_data(&raw, ClaudeCredentialSource::File)
            {
                return Ok(Some(credentials));
            }
            // Parsed but unusable (logged-out / no accessToken): fall through.
            Ok(None)
        }
        // Absent is normal (no file login). A genuine read failure (permissions /
        // I/O) is a real problem — return it so the caller can surface the
        // actionable error after setup-token fallbacks miss, rather than the
        // generic "unconfigured" setup prompt.
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(error) => Err(format!(
            "read Claude credentials file {}: {}",
            claude_credentials_path().display(),
            error
        )),
    }
}

/// `CLAUDE_CODE_OAUTH_TOKEN` as Claude Code itself resolves it: this process's
/// own environment (covers `launchctl setenv` / terminal launch), then a
/// login-shell harvest of the user's `~/.zshrc` (so a plain export a
/// Finder-launched GUI app never inherits is still found). Per Claude Code's
/// auth precedence this outranks a stored subscription `/login`.
async fn resolve_claude_code_oauth_token() -> Option<ResolvedClaudeToken> {
    if let Some(access_token) = claude_direct_env_token() {
        return Some(ResolvedClaudeToken {
            access_token,
            scope_slot: CredentialSlot {
                semantic_source: "claude-code-environment",
                canonical_location: "CLAUDE_CODE_OAUTH_TOKEN".to_string(),
            },
        });
    }
    harvest_shell_env_token()
        .await
        .map(|access_token| ResolvedClaudeToken {
            access_token,
            scope_slot: CredentialSlot {
                semantic_source: "claude-code-login-shell",
                canonical_location: "CLAUDE_CODE_OAUTH_TOKEN".to_string(),
            },
        })
}

/// The `tokenbar-claude-oauth-token` Keychain item (a TokenBar-specific setup
/// token). A last-resort fallback, below the stored `/login`.
fn resolve_claude_keychain_token() -> Option<ResolvedClaudeToken> {
    load_claude_raw_token_from_keychain()
        .ok()
        .flatten()
        .map(|access_token| ResolvedClaudeToken {
            access_token,
            scope_slot: CredentialSlot {
                semantic_source: "claude-setup-keychain",
                canonical_location: CLAUDE_RAW_TOKEN_KEYCHAIN_SERVICE.to_string(),
            },
        })
}

fn load_claude_credentials_from_environment() -> Result<Option<ClaudeCredentials>, String> {
    let token = [
        "TOKENBAR_CLAUDE_OAUTH_TOKEN",
        "TOKCAT_CLAUDE_OAUTH_TOKEN",
        "CODEXBAR_CLAUDE_OAUTH_TOKEN",
    ]
    .into_iter()
    .find_map(|name| {
        std::env::var(name)
            .ok()
            .map(|value| value.trim().to_string())
            .filter(|value| !value.is_empty())
            .map(|value| (name, value))
    });
    let Some((source_name, access_token)) = token else {
        return Ok(None);
    };
    let scopes = std::env::var("TOKENBAR_CLAUDE_OAUTH_SCOPES")
        .or_else(|_| std::env::var("TOKCAT_CLAUDE_OAUTH_SCOPES"))
        .or_else(|_| std::env::var("CODEXBAR_CLAUDE_OAUTH_SCOPES"))
        .unwrap_or_default()
        .split([',', ' '])
        .map(str::trim)
        .filter(|scope| !scope.is_empty())
        .map(str::to_string)
        .collect();
    Ok(Some(ClaudeCredentials {
        access_token,
        refresh_token: None,
        expires_at: None,
        scopes,
        rate_limit_tier: None,
        subscription_type: None,
        source: ClaudeCredentialSource::Environment,
        raw_root: None,
        scope_slot: CredentialSlot {
            semantic_source: "claude-environment",
            canonical_location: source_name.to_string(),
        },
    }))
}

fn parse_claude_credentials_data(
    raw: &str,
    source: ClaudeCredentialSource,
) -> Result<ClaudeCredentials, String> {
    let raw_root: Value =
        serde_json::from_str(raw).map_err(|e| format!("decode Claude OAuth credentials: {}", e))?;
    let root: ClaudeCredentialsRoot =
        serde_json::from_str(raw).map_err(|e| format!("decode Claude OAuth credentials: {}", e))?;
    let oauth = root
        .claude_ai_oauth
        .ok_or_else(|| "Claude OAuth credentials are missing claudeAiOauth.".to_string())?;
    let access_token = oauth
        .access_token
        .map(|token| token.trim().to_string())
        .filter(|token| !token.is_empty())
        .ok_or_else(|| "Claude OAuth credentials have no access token.".to_string())?;
    let expires_at = oauth
        .expires_at
        .and_then(|millis| Utc.timestamp_millis_opt(millis as i64).single());
    Ok(ClaudeCredentials {
        access_token,
        refresh_token: oauth
            .refresh_token
            .map(|token| token.trim().to_string())
            .filter(|token| !token.is_empty()),
        expires_at,
        scopes: oauth.scopes.unwrap_or_default(),
        rate_limit_tier: oauth.rate_limit_tier,
        subscription_type: oauth.subscription_type,
        source,
        raw_root: Some(raw_root),
        scope_slot: claude_login_scope_slot(source)?,
    })
}

fn claude_login_scope_slot(source: ClaudeCredentialSource) -> Result<CredentialSlot, String> {
    match source {
        ClaudeCredentialSource::Keychain => Ok(CredentialSlot {
            semantic_source: "claude-login-keychain",
            canonical_location: CLAUDE_KEYCHAIN_SERVICE.to_string(),
        }),
        ClaudeCredentialSource::File => Ok(CredentialSlot {
            semantic_source: "claude-login-file",
            canonical_location: agent_account_scope::canonical_file_location(
                &claude_credentials_path(),
                Some("claudeAiOauth"),
            )
            .map_err(|_| "Claude credential location cannot be scoped safely.".to_string())?,
        }),
        ClaudeCredentialSource::Environment => {
            Err("environment credentials require an explicit account-scope slot".to_string())
        }
    }
}

#[cfg(target_os = "macos")]
fn load_claude_credentials_from_keychain() -> Result<Option<String>, String> {
    let output = std::process::Command::new("/usr/bin/security")
        .args(["find-generic-password", "-s", CLAUDE_KEYCHAIN_SERVICE, "-w"])
        .output()
        .map_err(|e| format!("read Claude Keychain credentials: {}", e))?;
    if !output.status.success() {
        return Ok(None);
    }
    let raw = String::from_utf8(output.stdout)
        .map_err(|_| "Claude Keychain credentials are not UTF-8 JSON.".to_string())?;
    let raw = raw.trim_matches(['\r', '\n']).to_string();
    if raw.trim().is_empty() {
        return Ok(None);
    }
    Ok(Some(raw))
}

#[cfg(not(target_os = "macos"))]
fn load_claude_credentials_from_keychain() -> Result<Option<String>, String> {
    Ok(None)
}

/// Build credentials from a bare access token (no refresh/expiry/scope metadata).
/// Used by the setup-token delivery paths (env var, shell harvest, raw keychain);
/// empty scopes make `fetch_claude_inner` skip the scope guard and reach the
/// header fallback on the resulting oauth/usage 403.
fn claude_credentials_from_access_token(token: ResolvedClaudeToken) -> ClaudeCredentials {
    ClaudeCredentials {
        access_token: token.access_token,
        refresh_token: None,
        expires_at: None,
        scopes: Vec::new(),
        rate_limit_tier: None,
        subscription_type: None,
        // A bare setup-token has no refresh token and no backing store to write
        // to, so treat it as read-only — save_claude_credentials skips it.
        source: ClaudeCredentialSource::Environment,
        raw_root: None,
        scope_slot: token.scope_slot,
    }
}

/// C — `CLAUDE_CODE_OAUTH_TOKEN` from this process's own environment (covers
/// `launchctl setenv` and terminal-launched runs).
fn claude_direct_env_token() -> Option<String> {
    claude_token_from_lookup(|key| std::env::var(key).ok())
}

fn claude_token_from_lookup(lookup: impl Fn(&str) -> Option<String>) -> Option<String> {
    lookup("CLAUDE_CODE_OAUTH_TOKEN")
        .map(|value| value.trim().to_string())
        .filter(|value| !value.is_empty())
}

/// Cache for the shell-harvested token — harvesting spawns a full interactive
/// login shell, so we do it at most once per TTL rather than per poll.
static CLAUDE_HARVEST_CACHE: Mutex<Option<(DateTime<Utc>, Option<String>)>> = Mutex::new(None);
// A found token rarely changes → cache it for an hour. Because the harvest now
// runs for every user (to mirror Claude Code's CLAUDE_CODE_OAUTH_TOKEN-before-
// /login precedence), a miss is also cached for a while so we don't re-spawn a
// login shell on every poll; a freshly-added `~/.zshrc` export is picked up
// within this window, or immediately on app restart (which clears the cache).
const CLAUDE_HARVEST_TTL_SECS: i64 = 3600;
const CLAUDE_HARVEST_NEGATIVE_TTL_SECS: i64 = 1800;

/// D — harvest `CLAUDE_CODE_OAUTH_TOKEN` from the user's login shell, so a plain
/// `~/.zshrc` export is picked up even though a Finder/login-item GUI app does
/// not inherit shell environments. Cached; returns None on timeout/miss so the
/// keychain fallback can still fire.
async fn harvest_shell_env_token() -> Option<String> {
    // Scope the guard so it is dropped before the `.await` below (never hold a
    // std Mutex across an await). Recover a poisoned lock (like `lock_gate`) so a
    // stray panic can't permanently disable the cache and reintroduce a per-poll
    // shell spawn.
    {
        let guard = CLAUDE_HARVEST_CACHE
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        if let Some((fetched_at, token)) = guard.as_ref() {
            let ttl = if token.is_some() {
                CLAUDE_HARVEST_TTL_SECS
            } else {
                CLAUDE_HARVEST_NEGATIVE_TTL_SECS
            };
            if (Utc::now() - *fetched_at).num_seconds() < ttl {
                return token.clone();
            }
        }
    }
    let token = harvest_shell_env_token_uncached().await;
    {
        let mut guard = CLAUDE_HARVEST_CACHE
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        *guard = Some((Utc::now(), token.clone()));
    }
    token
}

#[cfg(target_os = "macos")]
async fn harvest_shell_env_token_uncached() -> Option<String> {
    // Interactive (-i) so ~/.zshrc is sourced (login -l alone runs ~/.zprofile
    // only). Null-delimited markers isolate the value from any rc stdout chatter;
    // rc noise (p10k/gitstatus warnings) goes to stderr, which we discard.
    let shell = detect_login_shell();
    let script = "printf '\\0__TB_OAT_S__\\0%s\\0__TB_OAT_E__\\0' \"$CLAUDE_CODE_OAUTH_TOKEN\"";
    let future = tokio::process::Command::new(&shell)
        .args(["-l", "-i", "-c", script])
        .stdin(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        // On the 5s timeout the future is dropped; kill the child so a hanging rc
        // (e.g. a blocking prompt) doesn't leave an orphaned login shell running.
        .kill_on_drop(true)
        .output();
    let output = tokio::time::timeout(std::time::Duration::from_secs(5), future)
        .await
        .ok()?
        .ok()?;
    let stdout = String::from_utf8_lossy(&output.stdout);
    let start_marker = "\0__TB_OAT_S__\0";
    let end_marker = "\0__TB_OAT_E__\0";
    let start = stdout.find(start_marker)? + start_marker.len();
    let rest = &stdout[start..];
    let end = rest.find(end_marker)?;
    let token = rest[..end].trim().to_string();
    (!token.is_empty()).then_some(token)
}

#[cfg(not(target_os = "macos"))]
async fn harvest_shell_env_token_uncached() -> Option<String> {
    None
}

/// Resolve the user's login shell for the harvest. `$SHELL` is usually unset for
/// a launchd-spawned GUI app, so fall back to Directory Services.
#[cfg(target_os = "macos")]
fn detect_login_shell() -> String {
    if let Ok(shell) = std::env::var("SHELL") {
        let shell = shell.trim();
        if !shell.is_empty() {
            return shell.to_string();
        }
    }
    if let Some(user) = current_username() {
        if let Ok(output) = std::process::Command::new("/usr/bin/dscl")
            .args([".", "-read", &format!("/Users/{}", user), "UserShell"])
            .output()
        {
            if output.status.success() {
                if let Ok(text) = String::from_utf8(output.stdout) {
                    // "UserShell: /bin/zsh"
                    if let Some(path) = text.split_whitespace().nth(1) {
                        if !path.is_empty() {
                            return path.to_string();
                        }
                    }
                }
            }
        }
    }
    "/bin/zsh".to_string()
}

#[cfg(target_os = "macos")]
fn current_username() -> Option<String> {
    if let Ok(user) = std::env::var("USER") {
        let user = user.trim();
        if !user.is_empty() {
            return Some(user.to_string());
        }
    }
    let output = std::process::Command::new("/usr/bin/id")
        .arg("-un")
        .output()
        .ok()?;
    if !output.status.success() {
        return None;
    }
    let user = String::from_utf8(output.stdout).ok()?.trim().to_string();
    (!user.is_empty()).then_some(user)
}

/// B — a RAW setup-token stored in the `tokenbar-claude-oauth-token` Keychain
/// service. Works regardless of launch method (unlike the env var), which is why
/// it's the reliable fallback for a Finder/login-item GUI app.
#[cfg(target_os = "macos")]
fn load_claude_raw_token_from_keychain() -> Result<Option<String>, String> {
    let output = std::process::Command::new("/usr/bin/security")
        .args([
            "find-generic-password",
            "-s",
            CLAUDE_RAW_TOKEN_KEYCHAIN_SERVICE,
            "-w",
        ])
        .output()
        .map_err(|e| format!("read TokenBar Claude token from Keychain: {}", e))?;
    if !output.status.success() {
        return Ok(None);
    }
    let raw = String::from_utf8(output.stdout)
        .map_err(|_| "TokenBar Claude Keychain token is not UTF-8.".to_string())?;
    let raw = raw.trim().to_string();
    if raw.is_empty() {
        return Ok(None);
    }
    Ok(Some(raw))
}

#[cfg(not(target_os = "macos"))]
fn load_claude_raw_token_from_keychain() -> Result<Option<String>, String> {
    Ok(None)
}

async fn refresh_codex_credentials(
    auth_path: &Path,
) -> Result<(CodexCredentials, Result<AccountScope, AccountScopeError>), String> {
    let refresh = agent_account_scope::begin_refresh("codex")
        .map_err(|_| "Codex credential refresh lock is unavailable.".to_string())?;
    refresh_codex_credentials_with(
        auth_path,
        &refresh,
        request_codex_refresh,
        save_codex_credentials,
        |_| Ok(()),
    )
    .await
}

async fn request_codex_refresh(refresh_token: String) -> Result<Value, String> {
    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(30))
        .build()
        .map_err(|e| format!("build Codex refresh client: {}", e))?;
    let body = serde_json::json!({
        "client_id": CODEX_CLIENT_ID,
        "grant_type": "refresh_token",
        "refresh_token": refresh_token,
        "scope": "openid profile email"
    });
    let response = client
        .post(CODEX_REFRESH_URL)
        .header(reqwest::header::CONTENT_TYPE, "application/json")
        .json(&body)
        .send()
        .await
        .map_err(|e| format!("Codex token refresh failed: {}", e))?;
    let status = response.status();
    let body = response
        .text()
        .await
        .map_err(|e| format!("read Codex refresh response: {}", e))?;
    if !status.is_success() {
        return Err("Codex OAuth refresh failed. Run `codex` to log in again.".to_string());
    }
    serde_json::from_str(&body).map_err(|e| format!("decode Codex refresh response: {}", e))
}

async fn refresh_codex_credentials_with<R, Request, RequestFuture, Save, Checkpoint>(
    auth_path: &Path,
    refresh: &R,
    request: Request,
    save: Save,
    mut checkpoint: Checkpoint,
) -> Result<(CodexCredentials, Result<AccountScope, AccountScopeError>), String>
where
    R: RefreshScopeTransaction + ?Sized,
    Request: FnOnce(String) -> RequestFuture,
    RequestFuture: std::future::Future<Output = Result<Value, String>>,
    Save: FnOnce(&CodexCredentials) -> Result<(), String>,
    Checkpoint: FnMut(RefreshCheckpoint) -> Result<(), String>,
{
    // Another TokenBar process may have refreshed while this caller waited.
    // Reload the request-bearing record only after the refresh lock is held.
    let credentials = load_codex_credentials_from(auth_path)?;
    checkpoint(RefreshCheckpoint::Reloaded)?;
    if !credentials_needs_refresh(credentials.last_refresh) {
        let scope = refresh.resolve_current(
            credentials.scope_slot.semantic_source,
            &credentials.scope_slot.canonical_location,
            credentials.scope_marker(),
        );
        return Ok((credentials, scope));
    }

    let refresh_token = credentials
        .refresh_token
        .as_deref()
        .map(str::trim)
        .filter(|token| !token.is_empty())
        .ok_or_else(|| "Codex auth.json has no refresh token.".to_string())?
        .to_string();
    let old_marker = credentials.scope_marker().to_vec();
    let json = request(refresh_token).await?;
    checkpoint(RefreshCheckpoint::NetworkReturned)?;

    let response = json.as_object();
    let refreshed = CodexCredentials {
        access_token: response
            .and_then(|tokens| string_key(tokens, "access_token", "accessToken"))
            .unwrap_or(credentials.access_token),
        refresh_token: response
            .and_then(|tokens| string_key(tokens, "refresh_token", "refreshToken"))
            .or(credentials.refresh_token),
        id_token: response
            .and_then(|tokens| string_key(tokens, "id_token", "idToken"))
            .or(credentials.id_token),
        account_id: credentials.account_id,
        last_refresh: Some(Utc::now()),
        auth_path: credentials.auth_path,
        raw_json: credentials.raw_json,
        scope_slot: credentials.scope_slot,
    };
    let marker_rotated = refreshed.scope_marker() != old_marker.as_slice();
    let scope = refresh.transfer(
        refreshed.scope_slot.semantic_source,
        &refreshed.scope_slot.canonical_location,
        &old_marker,
        refreshed.scope_marker(),
    );
    checkpoint(RefreshCheckpoint::MetadataHandled)?;
    // A rotated marker may reach disk only after its lineage transfer is durable.
    // The refreshed access token remains usable in memory for this poll.
    if marker_rotated && scope.is_err() {
        return Ok((refreshed, scope));
    }
    save(&refreshed)?;
    checkpoint(RefreshCheckpoint::CredentialsPersisted)?;
    Ok((refreshed, scope))
}

async fn refresh_claude_credentials(
    original: &ClaudeCredentials,
) -> Result<(ClaudeCredentials, Result<AccountScope, AccountScopeError>), String> {
    let refresh = agent_account_scope::begin_refresh("claude")
        .map_err(|_| "Claude credential refresh lock is unavailable.".to_string())?;
    refresh_claude_credentials_with(
        original,
        &refresh,
        reload_claude_credentials,
        request_claude_refresh,
        save_claude_credentials,
        |_| Ok(()),
    )
    .await
}

async fn request_claude_refresh(refresh_token: String) -> Result<ClaudeRefreshResponse, String> {
    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(30))
        .build()
        .map_err(|e| format!("build Claude refresh client: {}", e))?;
    let response = client
        .post(CLAUDE_REFRESH_URL)
        .header(reqwest::header::ACCEPT, "application/json")
        .header(
            reqwest::header::CONTENT_TYPE,
            "application/x-www-form-urlencoded",
        )
        .body(form_urlencoded(&[
            ("grant_type", "refresh_token"),
            ("refresh_token", &refresh_token),
            ("client_id", CLAUDE_CLIENT_ID),
        ]))
        .send()
        .await
        .map_err(|e| format!("Claude OAuth refresh failed: {}", e))?;
    let status = response.status();
    let body = response
        .text()
        .await
        .map_err(|e| format!("read Claude refresh response: {}", e))?;
    if !status.is_success() {
        return Err("Claude OAuth refresh failed. Run `claude` to re-authenticate.".to_string());
    }
    serde_json::from_str(&body).map_err(|e| format!("decode Claude refresh response: {}", e))
}

async fn refresh_claude_credentials_with<R, Reload, Request, RequestFuture, Save, Checkpoint>(
    original: &ClaudeCredentials,
    refresh: &R,
    reload: Reload,
    request: Request,
    save: Save,
    mut checkpoint: Checkpoint,
) -> Result<(ClaudeCredentials, Result<AccountScope, AccountScopeError>), String>
where
    R: RefreshScopeTransaction + ?Sized,
    Reload: FnOnce(&ClaudeCredentials) -> Result<ClaudeCredentials, String>,
    Request: FnOnce(String) -> RequestFuture,
    RequestFuture: std::future::Future<Output = Result<ClaudeRefreshResponse, String>>,
    Save: FnOnce(&ClaudeCredentials) -> Result<(), String>,
    Checkpoint: FnMut(RefreshCheckpoint) -> Result<(), String>,
{
    let credentials = reload(original)?;
    checkpoint(RefreshCheckpoint::Reloaded)?;
    if !claude_credentials_expired(&credentials) {
        let scope = match credentials.scope_marker() {
            Some(marker) => refresh.resolve_current(
                credentials.scope_slot.semantic_source,
                &credentials.scope_slot.canonical_location,
                marker,
            ),
            None => Err(AccountScopeError::NoTrustedEvidence),
        };
        return Ok((credentials, scope));
    }

    let refresh_token = credentials
        .refresh_token
        .as_deref()
        .filter(|token| !token.is_empty())
        .ok_or_else(|| {
            "Claude OAuth token is expired and has no refresh token. Run `claude`.".to_string()
        })?
        .to_string();
    let old_marker = refresh_token.as_bytes().to_vec();
    let token_response = request(refresh_token).await?;
    checkpoint(RefreshCheckpoint::NetworkReturned)?;
    let refreshed = ClaudeCredentials {
        access_token: token_response.access_token,
        refresh_token: token_response
            .refresh_token
            .as_deref()
            .map(str::trim)
            .filter(|token| !token.is_empty())
            .map(str::to_string)
            .or_else(|| credentials.refresh_token.clone()),
        expires_at: Some(Utc::now() + chrono::Duration::seconds(token_response.expires_in)),
        scopes: credentials.scopes.clone(),
        rate_limit_tier: credentials.rate_limit_tier.clone(),
        subscription_type: credentials.subscription_type.clone(),
        source: credentials.source,
        raw_root: credentials.raw_root.clone(),
        scope_slot: credentials.scope_slot.clone(),
    };
    let new_marker = refreshed.scope_marker();
    let marker_rotated = new_marker.is_some_and(|marker| marker != old_marker.as_slice());
    let scope = match new_marker {
        Some(new_marker) => refresh.transfer(
            refreshed.scope_slot.semantic_source,
            &refreshed.scope_slot.canonical_location,
            &old_marker,
            new_marker,
        ),
        None => Err(AccountScopeError::NoTrustedEvidence),
    };
    checkpoint(RefreshCheckpoint::MetadataHandled)?;
    // A rotated marker may reach the shared provider store only after its
    // lineage transfer is durable. The new access token remains usable in
    // memory for this poll.
    if marker_rotated && scope.is_err() {
        return Ok((refreshed, scope));
    }
    if let Err(error) = save(&refreshed) {
        eprintln!("tb_core_ffi: failed to persist refreshed Claude credentials: {error}");
    }
    checkpoint(RefreshCheckpoint::CredentialsPersisted)?;
    Ok((refreshed, scope))
}

fn reload_claude_credentials(original: &ClaudeCredentials) -> Result<ClaudeCredentials, String> {
    match original.source {
        ClaudeCredentialSource::Keychain => {
            let raw = load_claude_credentials_from_keychain()?.ok_or_else(|| {
                "Claude Keychain credentials disappeared during refresh.".to_string()
            })?;
            parse_claude_credentials_data(&raw, ClaudeCredentialSource::Keychain)
        }
        ClaudeCredentialSource::File => {
            let raw = fs::read_to_string(claude_credentials_path())
                .map_err(|e| format!("reload Claude credentials file: {e}"))?;
            parse_claude_credentials_data(&raw, ClaudeCredentialSource::File)
        }
        ClaudeCredentialSource::Environment => {
            Err("Claude environment credentials cannot be refreshed in place.".to_string())
        }
    }
}

/// Merge the rotated access/refresh tokens back into the credentials store they
/// came from, preserving every other field the Claude CLI wrote.
fn save_claude_credentials(credentials: &ClaudeCredentials) -> Result<(), String> {
    match credentials.source {
        ClaudeCredentialSource::Keychain => {
            save_claude_credentials_to_keychain(&merge_claude_credentials_json(credentials)?)
        }
        ClaudeCredentialSource::File => {
            save_claude_credentials_to_file(credentials, &claude_credentials_path())
        }
        ClaudeCredentialSource::Environment => Ok(()),
    }
}

fn save_claude_credentials_to_file(
    credentials: &ClaudeCredentials,
    path: &Path,
) -> Result<(), String> {
    atomic_write(path, &merge_claude_credentials_json(credentials)?)
}

/// Replace `path` atomically: write a sibling temp file, then rename over the
/// target. A crash or partial write leaves the original credentials intact
/// rather than a truncated file that would break both TokenBar and the Claude
/// CLI (the rename is atomic within one filesystem).
fn atomic_write(path: &Path, data: &str) -> Result<(), String> {
    let parent = path.parent().ok_or_else(|| {
        format!(
            "credentials path {} has no parent directory",
            path.display()
        )
    })?;
    fs::create_dir_all(parent).map_err(|e| format!("create {}: {}", parent.display(), e))?;

    let file_name = path
        .file_name()
        .and_then(|n| n.to_str())
        .unwrap_or("credentials");
    // Per-write-unique temp name (pid + a monotonic seq). The O_EXCL open below
    // must never collide with an orphan a crashed earlier write left at a fixed
    // path, or every later write-back in this long-lived process would fail with
    // AlreadyExists and silently stop persisting rotated tokens.
    static TMP_SEQ: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
    let seq = TMP_SEQ.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    let tmp = parent.join(format!(".{}.tmp.{}.{}", file_name, std::process::id(), seq));

    // Stage into the temp, fsync it, then rename over the target. Create with
    // O_EXCL + 0600 up front: the mode-at-creation closes the umask-default
    // window a write-then-chmod leaves the secret readable in, and O_EXCL
    // refuses to follow a symlink pre-seeded at the temp path.
    let staged = (|| -> Result<(), String> {
        use std::io::Write as _;
        let mut opts = fs::OpenOptions::new();
        opts.write(true).create_new(true);
        #[cfg(unix)]
        {
            use std::os::unix::fs::OpenOptionsExt as _;
            opts.mode(0o600);
        }
        let mut file = opts
            .open(&tmp)
            .map_err(|e| format!("create {}: {}", tmp.display(), e))?;
        file.write_all(data.as_bytes())
            .map_err(|e| format!("write {}: {}", tmp.display(), e))?;
        // Flush data to disk before the rename so a power loss can't leave the
        // renamed file pointing at never-written blocks — the crash-safety this
        // function's doc-comment promises.
        file.sync_all()
            .map_err(|e| format!("sync {}: {}", tmp.display(), e))
    })();
    // Any failure after the temp exists removes it, so a transient write error
    // can't strand an orphan that wedges the next write.
    if let Err(error) = staged {
        let _ = fs::remove_file(&tmp);
        return Err(error);
    }
    if let Err(error) = fs::rename(&tmp, path) {
        let _ = fs::remove_file(&tmp);
        return Err(format!("replace {}: {}", path.display(), error));
    }
    // Persist the rename itself so it survives a power loss right afterward.
    #[cfg(unix)]
    if let Ok(dir) = fs::File::open(parent) {
        let _ = dir.sync_all();
    }
    Ok(())
}

/// Merge the rotated tokens into the loaded credentials JSON, preserving any
/// other fields, and return it serialized. Pure so it's unit-testable.
fn merge_claude_credentials_json(credentials: &ClaudeCredentials) -> Result<String, String> {
    let mut root = credentials
        .raw_root
        .clone()
        .unwrap_or_else(|| serde_json::json!({ "claudeAiOauth": {} }));
    let oauth = root
        .get_mut("claudeAiOauth")
        .and_then(Value::as_object_mut)
        .ok_or_else(|| "Claude credentials JSON has no claudeAiOauth object.".to_string())?;
    oauth.insert(
        "accessToken".to_string(),
        Value::String(credentials.access_token.clone()),
    );
    if let Some(refresh) = &credentials.refresh_token {
        oauth.insert("refreshToken".to_string(), Value::String(refresh.clone()));
    }
    if let Some(expires_at) = credentials.expires_at {
        oauth.insert(
            "expiresAt".to_string(),
            Value::Number(expires_at.timestamp_millis().into()),
        );
    }
    serde_json::to_string(&root).map_err(|e| format!("encode Claude credentials: {}", e))
}

#[cfg(target_os = "macos")]
fn save_claude_credentials_to_keychain(data: &str) -> Result<(), String> {
    // Fail closed: only update the item once we can confirm the exact account
    // the Claude CLI stored it under. `add-generic-password -U` matches on
    // (service, account), so updating with the wrong or an empty account would
    // create a SECOND "Claude Code-credentials" item and confuse the store the
    // CLI shares — worse than not persisting. If the account can't be read,
    // skip the write-back (the caller logs it); the next refresh retries.
    let account = claude_keychain_account().ok_or_else(|| {
        "could not resolve the Claude Keychain account; skipping write-back to avoid a duplicate item"
            .to_string()
    })?;
    // NOTE: `-w <data>` puts the credential JSON on the argv, briefly visible via
    // `ps` to same-user processes. security(1) has no stdin form for
    // add-generic-password (only an interactive `-w` prompt, unusable from a
    // background app) and the item is already same-user-readable once the
    // keychain is unlocked, so on a single-user Mac this narrow window is an
    // accepted trade-off; move to the SecItem API if that assumption changes.
    let status = std::process::Command::new("/usr/bin/security")
        .args([
            "add-generic-password",
            "-U",
            "-s",
            CLAUDE_KEYCHAIN_SERVICE,
            "-a",
            &account,
            "-w",
            data,
        ])
        .status()
        .map_err(|e| format!("write Claude Keychain credentials: {}", e))?;
    if !status.success() {
        return Err("security add-generic-password failed for Claude credentials.".to_string());
    }
    Ok(())
}

#[cfg(not(target_os = "macos"))]
fn save_claude_credentials_to_keychain(_data: &str) -> Result<(), String> {
    Err("Keychain writes are only supported on macOS.".to_string())
}

/// Read the account name the Claude Keychain item is stored under so the
/// write-back updates that same item instead of creating a duplicate.
#[cfg(target_os = "macos")]
fn claude_keychain_account() -> Option<String> {
    let output = std::process::Command::new("/usr/bin/security")
        .args(["find-generic-password", "-s", CLAUDE_KEYCHAIN_SERVICE])
        .output()
        .ok()?;
    if !output.status.success() {
        return None;
    }
    let text = String::from_utf8_lossy(&output.stdout);
    // Attribute line looks like: `    "acct"<blob>="alice"`
    for line in text.lines() {
        let line = line.trim_start();
        if let Some(rest) = line.strip_prefix("\"acct\"") {
            if let Some(eq) = rest.find('=') {
                let value = rest[eq + 1..].trim();
                // security renders a non-printable acct as `0x<hex>  "ascii"`;
                // the string-scrape can't recover the real bytes, so treat it as
                // unresolved (fail closed) rather than returning a corrupt
                // account that `add-generic-password -U` would spawn a duplicate
                // "Claude Code-credentials" item under.
                if value.starts_with("0x") {
                    return None;
                }
                let value = value.trim_matches('"');
                if !value.is_empty() && value != "<NULL>" {
                    return Some(value.to_string());
                }
            }
        }
    }
    None
}

fn save_codex_credentials(credentials: &CodexCredentials) -> Result<(), String> {
    let mut raw = credentials.raw_json.clone();
    raw["tokens"]["access_token"] = Value::String(credentials.access_token.clone());
    if let Some(refresh_token) = &credentials.refresh_token {
        raw["tokens"]["refresh_token"] = Value::String(refresh_token.clone());
    }
    if let Some(id_token) = &credentials.id_token {
        raw["tokens"]["id_token"] = Value::String(id_token.clone());
    }
    if let Some(account_id) = &credentials.account_id {
        raw["tokens"]["account_id"] = Value::String(account_id.clone());
    }
    raw["last_refresh"] = Value::String(Utc::now().to_rfc3339_opts(SecondsFormat::Millis, true));
    let data =
        serde_json::to_vec_pretty(&raw).map_err(|e| format!("encode Codex auth.json: {}", e))?;
    fs::write(&credentials.auth_path, data).map_err(|e| format!("save Codex auth.json: {}", e))
}

fn codex_windows(
    rate_limit: Option<&CodexRateLimit>,
    additional_rate_limits: Option<&[CodexAdditionalRateLimit]>,
    now: DateTime<Utc>,
) -> Vec<UsageWindow> {
    let mut windows = Vec::new();
    if let Some(rate_limit) = rate_limit {
        let mut main = [
            ("primary", rate_limit.primary_window.clone()),
            ("secondary", rate_limit.secondary_window.clone()),
        ];
        main.sort_by_key(|(_, window)| {
            match window.as_ref().map(|window| window.limit_window_seconds) {
                Some(18_000) => 0,
                Some(604_800) => 1,
                _ => 2,
            }
        });
        for (slot, window) in main
            .into_iter()
            .filter_map(|(slot, window)| window.map(|window| (slot, window)))
        {
            let (label, card_id, window_key) = match window.limit_window_seconds {
                18_000 => (
                    "Session",
                    "main.session.v1".to_string(),
                    Some("main.session.v1".to_string()),
                ),
                604_800 => (
                    "Weekly",
                    "main.weekly.v1".to_string(),
                    Some("main.weekly.v1".to_string()),
                ),
                _ => ("Unknown", format!("row.main.{slot}.v1"), None),
            };
            if let Some(window) = map_window_with_identity(label, window, now, card_id, window_key)
            {
                windows.push(window);
            }
        }
    }

    for extra in additional_rate_limits.unwrap_or(&[]) {
        let Some(rate_limit) = extra.rate_limit.as_ref() else {
            continue;
        };
        let (slot, window) = match (
            rate_limit.primary_window.clone(),
            rate_limit.secondary_window.clone(),
        ) {
            (Some(window), _) => ("primary", window),
            (None, Some(window)) => ("secondary", window),
            (None, None) => continue,
        };
        let source = additional_limit_source(extra);
        let (label, card_id, window_key) = match source {
            Some(source) => {
                let key = format!("additional.{}.{slot}.v1", sha256_hex(&source));
                (additional_limit_label(extra), key.clone(), Some(key))
            }
            None => (
                "Unknown".to_string(),
                format!("row.additional.unknown.{slot}.v1"),
                None,
            ),
        };
        if let Some(window) = map_window_with_identity(&label, window, now, card_id, window_key) {
            windows.push(window);
        }
    }
    windows
}

fn claude_windows(usage: &ClaudeUsageResponse, now: DateTime<Utc>) -> Vec<UsageWindow> {
    let mut windows = Vec::new();
    push_claude_window(
        &mut windows,
        "Session",
        "session.v1",
        DurationEvidence::contract(18_000),
        usage.five_hour.as_ref(),
        now,
    );
    push_claude_window(
        &mut windows,
        "Weekly",
        "weekly.v1",
        DurationEvidence::contract(604_800),
        usage.seven_day.as_ref(),
        now,
    );
    push_claude_window(
        &mut windows,
        "OAuth Apps",
        "oauth_apps.weekly.v1",
        DurationEvidence::contract(604_800),
        usage.seven_day_oauth_apps.as_ref(),
        now,
    );
    push_claude_window(
        &mut windows,
        "Sonnet",
        "sonnet.weekly.v1",
        DurationEvidence::contract(604_800),
        usage.seven_day_sonnet.as_ref(),
        now,
    );
    push_claude_window(
        &mut windows,
        "Opus",
        "opus.weekly.v1",
        DurationEvidence::contract(604_800),
        usage.seven_day_opus.as_ref(),
        now,
    );
    push_claude_window(
        &mut windows,
        "Designs",
        "design.weekly.v1",
        DurationEvidence::contract(604_800),
        usage.design_window(),
        now,
    );
    push_claude_window(
        &mut windows,
        "Daily Routines",
        "routines.weekly.v1",
        DurationEvidence::contract(604_800),
        usage.routines_window(),
        now,
    );
    if let Some(extra) = claude_extra_usage_window(usage.extra_usage.as_ref(), now) {
        windows.push(extra);
    }
    windows
}

impl ClaudeUsageResponse {
    fn design_window(&self) -> Option<&ClaudeWindow> {
        [
            self.seven_day_design.as_ref(),
            self.seven_day_claude_design.as_ref(),
            self.claude_design.as_ref(),
            self.design.as_ref(),
            self.seven_day_omelette.as_ref(),
            self.omelette.as_ref(),
            self.omelette_promotional.as_ref(),
        ]
        .into_iter()
        .flatten()
        .find(|window| window.has_valid_utilization())
    }

    fn routines_window(&self) -> Option<&ClaudeWindow> {
        [
            self.seven_day_routines.as_ref(),
            self.seven_day_claude_routines.as_ref(),
            self.claude_routines.as_ref(),
            self.routines.as_ref(),
            self.routine.as_ref(),
            self.seven_day_cowork.as_ref(),
            self.cowork.as_ref(),
        ]
        .into_iter()
        .flatten()
        .find(|window| window.has_valid_utilization())
    }
}

fn push_claude_window(
    windows: &mut Vec<UsageWindow>,
    label: &str,
    window_key: &str,
    contract: DurationEvidence,
    window: Option<&ClaudeWindow>,
    now: DateTime<Utc>,
) {
    if let Some(mapped) =
        window.and_then(|window| map_claude_window(label, window_key, contract, window, now))
    {
        windows.push(mapped);
    }
}

fn map_claude_window(
    label: &str,
    window_key: &str,
    contract: DurationEvidence,
    window: &ClaudeWindow,
    now: DateTime<Utc>,
) -> Option<UsageWindow> {
    if !window.has_valid_utilization() {
        return None;
    }
    let used = window.utilization?;
    let reset_was_supplied = window.resets_at.is_some();
    let resets_at = window.resets_at.as_deref().and_then(parse_datetime);
    Some(
        UsageWindow::from_used_percent(label.to_string(), used, resets_at, now)
            .with_identity(window_key, Some(window_key.to_string()))
            .with_duration_evidence(now, reset_was_supplied, None, Some(contract)),
    )
}

/// Parse the `anthropic-ratelimit-unified-{5h,7d}-{utilization,reset}` response
/// headers into Session/Weekly usage windows. Pure — no network or I/O.
///
/// Unlike the oauth/usage JSON body (`utilization` 0..100, RFC3339 reset), these
/// headers use a 0..1 fraction and a Unix-epoch-seconds reset. This is the
/// fallback source for inference-only `claude setup-token` tokens.
fn parse_unified_ratelimit_windows(
    headers: &reqwest::header::HeaderMap,
    now: DateTime<Utc>,
) -> Vec<UsageWindow> {
    let read_f64 = |name: &str| -> Option<f64> {
        headers.get(name)?.to_str().ok()?.trim().parse::<f64>().ok()
    };
    let read_i64 = |name: &str| -> Option<i64> {
        headers.get(name)?.to_str().ok()?.trim().parse::<i64>().ok()
    };
    let mut windows = Vec::new();
    if let Some(window) = unified_ratelimit_window_with_identity(
        "Session",
        "session.v1",
        DurationEvidence::contract(18_000),
        read_f64("anthropic-ratelimit-unified-5h-utilization"),
        read_i64("anthropic-ratelimit-unified-5h-reset"),
        headers.contains_key("anthropic-ratelimit-unified-5h-reset"),
        now,
    ) {
        windows.push(window);
    }
    if let Some(window) = unified_ratelimit_window_with_identity(
        "Weekly",
        "weekly.v1",
        DurationEvidence::contract(604_800),
        read_f64("anthropic-ratelimit-unified-7d-utilization"),
        read_i64("anthropic-ratelimit-unified-7d-reset"),
        headers.contains_key("anthropic-ratelimit-unified-7d-reset"),
        now,
    ) {
        windows.push(window);
    }
    windows
}

/// Build one window from a unified-ratelimit header pair. Gated on utilization
/// (mirrors `map_claude_window`); reset is optional. `utilization_fraction` is
/// 0..1 (scaled ×100); `reset_epoch_seconds` is Unix seconds (like the Codex
/// `map_window` epoch handling).
fn unified_ratelimit_window_with_identity(
    label: &str,
    window_key: &str,
    contract: DurationEvidence,
    utilization_fraction: Option<f64>,
    reset_epoch_seconds: Option<i64>,
    reset_was_supplied: bool,
    now: DateTime<Utc>,
) -> Option<UsageWindow> {
    let fraction = utilization_fraction?;
    if !fraction.is_finite() || !(0.0..=1.0).contains(&fraction) {
        return None;
    }
    let used = fraction * 100.0;
    let resets_at = reset_epoch_seconds
        .filter(|seconds| *seconds > 0)
        .and_then(|seconds| Utc.timestamp_opt(seconds, 0).single());
    Some(
        UsageWindow::from_used_percent(label.to_string(), used, resets_at, now)
            .with_identity(window_key, Some(window_key.to_string()))
            .with_duration_evidence(now, reset_was_supplied, None, Some(contract)),
    )
}

#[cfg(test)]
fn unified_ratelimit_window(
    label: &str,
    utilization_fraction: Option<f64>,
    reset_epoch_seconds: Option<i64>,
    now: DateTime<Utc>,
) -> Option<UsageWindow> {
    let (key, contract) = if label.eq_ignore_ascii_case("Session") {
        ("session.v1", DurationEvidence::contract(18_000))
    } else {
        ("weekly.v1", DurationEvidence::contract(604_800))
    };
    unified_ratelimit_window_with_identity(
        label,
        key,
        contract,
        utilization_fraction,
        reset_epoch_seconds,
        reset_epoch_seconds.is_some(),
        now,
    )
}

fn claude_extra_usage_window(
    extra: Option<&ClaudeExtraUsage>,
    now: DateTime<Utc>,
) -> Option<UsageWindow> {
    let extra = extra?;
    if !extra.is_enabled {
        return None;
    }
    let used = extra.utilization.or_else(|| {
        let used = extra.used_credits?;
        let limit = extra.monthly_limit?;
        if limit > 0.0 {
            Some((used / limit) * 100.0)
        } else {
            None
        }
    })?;
    if !used.is_finite() || !(0.0..=100.0).contains(&used) {
        return None;
    }
    let reset_text = match (extra.used_credits, extra.monthly_limit) {
        (Some(used), Some(limit)) => Some(format!(
            "Monthly cap: {} / {}",
            format_currency_minor_units(used, extra.currency.as_deref()),
            format_currency_minor_units(limit, extra.currency.as_deref())
        )),
        _ => None,
    };
    let mut window = UsageWindow::from_used_percent("Extra usage".to_string(), used, None, now)
        .with_identity("extra_usage.v1", Some("extra_usage.v1".to_string()))
        .with_unavailable_reason("nonRecurring");
    window.reset_text = reset_text;
    Some(window)
}

fn claude_credits(extra: Option<&ClaudeExtraUsage>) -> Option<CreditsSnapshot> {
    let extra = extra?;
    if !extra.is_enabled {
        return None;
    }
    let remaining = match (extra.monthly_limit, extra.used_credits) {
        (Some(limit), Some(used)) => Some(((limit - used) / 100.0).max(0.0)),
        _ => None,
    };
    Some(CreditsSnapshot {
        remaining,
        unlimited: false,
    })
}

fn format_currency_minor_units(value: f64, currency: Option<&str>) -> String {
    let major = value / 100.0;
    match currency.unwrap_or("USD").trim().to_uppercase().as_str() {
        "USD" => format!("${:.2}", major),
        code if !code.is_empty() => format!("{:.2} {}", major, code),
        _ => format!("${:.2}", major),
    }
}

fn additional_limit_label(limit: &CodexAdditionalRateLimit) -> String {
    let source = first_non_empty([
        limit.limit_name.as_deref(),
        limit.metered_feature.as_deref(),
    ])
    .unwrap_or("Codex extra limit");
    let lower = source.to_lowercase();
    if lower.contains("spark") {
        return "Codex Spark".to_string();
    }
    clean_limit_label(source)
}

fn first_non_empty(values: [Option<&str>; 2]) -> Option<&str> {
    values
        .into_iter()
        .flatten()
        .map(str::trim)
        .find(|value| !value.is_empty())
}

fn clean_limit_label(value: &str) -> String {
    value
        .replace(['_', '-'], " ")
        .split_whitespace()
        .map(|part| {
            if part.eq_ignore_ascii_case("gpt") {
                "GPT".to_string()
            } else if part.eq_ignore_ascii_case("codex") {
                "Codex".to_string()
            } else {
                let mut chars = part.chars();
                match chars.next() {
                    Some(first) => format!("{}{}", first.to_uppercase(), chars.as_str()),
                    None => String::new(),
                }
            }
        })
        .collect::<Vec<_>>()
        .join(" ")
}

fn map_window_with_identity(
    label: &str,
    window: CodexWindow,
    now: DateTime<Utc>,
    card_id: impl Into<String>,
    window_key: Option<String>,
) -> Option<UsageWindow> {
    let resets_at = (window.reset_at > 0)
        .then(|| Utc.timestamp_opt(window.reset_at, 0).single())
        .flatten();
    let provider = DurationEvidence::provider(window.reset_at, window.limit_window_seconds);
    (window.used_percent.is_finite() && (0.0..=100.0).contains(&window.used_percent)).then(|| {
        UsageWindow::from_used_percent(label.to_string(), window.used_percent, resets_at, now)
            .with_identity(card_id, window_key)
            .with_duration_evidence(now, true, Some(provider), None)
    })
}

fn additional_limit_source(limit: &CodexAdditionalRateLimit) -> Option<String> {
    first_non_empty([
        limit.metered_feature.as_deref(),
        limit.limit_name.as_deref(),
    ])
    .map(str::to_string)
}

fn sha256_hex(value: &str) -> String {
    Sha256::digest(value.trim().as_bytes())
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect()
}

pub(crate) fn reset_text(reset: DateTime<Utc>, now: DateTime<Utc>) -> String {
    let seconds = (reset - now).num_seconds();
    if seconds <= 0 {
        return "Resets now".to_string();
    }
    let minutes = (seconds + 59) / 60;
    if minutes < 60 {
        return format!("Resets in {}m", minutes);
    }
    let hours = minutes / 60;
    let mins = minutes % 60;
    // Anything spanning a day or more reads in days+hours so the weekly windows
    // stay consistent across agents (Claude reported 47h, Codex 2d — unify both
    // to days); sub-day windows (sessions) keep the hours/minutes form.
    if hours < 24 {
        if mins > 0 {
            return format!("Resets in {}h {}m", hours, mins);
        }
        return format!("Resets in {}h", hours);
    }
    let days = hours / 24;
    let rem_hours = hours % 24;
    if rem_hours > 0 {
        format!("Resets in {}d {}h", days, rem_hours)
    } else {
        format!("Resets in {}d", days)
    }
}

fn codex_home() -> PathBuf {
    std::env::var_os("CODEX_HOME")
        .map(PathBuf::from)
        .filter(|p| !p.as_os_str().is_empty())
        .or_else(|| std::env::var_os("HOME").map(|home| PathBuf::from(home).join(".codex")))
        .unwrap_or_else(|| PathBuf::from(".codex"))
}

fn claude_credentials_path() -> PathBuf {
    std::env::var_os("HOME")
        .map(|home| PathBuf::from(home).join(".claude/.credentials.json"))
        .unwrap_or_else(|| PathBuf::from(".claude/.credentials.json"))
}

fn credentials_needs_refresh(last_refresh: Option<DateTime<Utc>>) -> bool {
    let Some(last_refresh) = last_refresh else {
        return true;
    };
    (Utc::now() - last_refresh).num_days() > 8
}

fn claude_credentials_expired(credentials: &ClaudeCredentials) -> bool {
    credentials
        .expires_at
        .is_some_and(|expires_at| Utc::now() >= expires_at)
}

pub(crate) fn parse_datetime(value: &str) -> Option<DateTime<Utc>> {
    DateTime::parse_from_rfc3339(value)
        .map(|dt| dt.with_timezone(&Utc))
        .ok()
}

fn claude_user_agent() -> String {
    std::process::Command::new("claude")
        .arg("--version")
        .output()
        .ok()
        .and_then(|output| {
            if output.status.success() {
                String::from_utf8(output.stdout).ok()
            } else {
                None
            }
        })
        .and_then(|stdout| stdout.split_whitespace().next().map(str::to_string))
        .filter(|version| !version.is_empty())
        .map(|version| format!("claude-code/{}", version))
        .unwrap_or_else(|| "claude-code/2.1.0".to_string())
}

fn form_urlencoded(params: &[(&str, &str)]) -> String {
    params
        .iter()
        .map(|(key, value)| format!("{}={}", percent_encode(key), percent_encode(value)))
        .collect::<Vec<_>>()
        .join("&")
}

pub(crate) fn percent_encode(value: &str) -> String {
    let mut encoded = String::new();
    for byte in value.bytes() {
        match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                encoded.push(byte as char);
            }
            b' ' => encoded.push('+'),
            _ => encoded.push_str(&format!("%{:02X}", byte)),
        }
    }
    encoded
}

fn string_key(
    map: &serde_json::Map<String, Value>,
    snake_case: &str,
    camel_case: &str,
) -> Option<String> {
    [snake_case, camel_case]
        .into_iter()
        .filter_map(|key| map.get(key).and_then(Value::as_str))
        .map(str::trim)
        .find(|value| !value.is_empty())
        .map(str::to_string)
}

fn jwt_payload(token: &str) -> Option<Value> {
    let payload = token.split('.').nth(1)?;
    let mut encoded = payload.replace('-', "+").replace('_', "/");
    while encoded.len() % 4 != 0 {
        encoded.push('=');
    }
    use base64::Engine;
    let data = base64::engine::general_purpose::STANDARD
        .decode(encoded)
        .ok()?;
    serde_json::from_slice(&data).ok()
}

fn jwt_email(token: &str) -> Option<String> {
    let payload = jwt_payload(token)?;
    payload
        .get("email")
        .and_then(Value::as_str)
        .or_else(|| {
            payload
                .get("https://api.openai.com/profile")
                .and_then(Value::as_object)
                .and_then(|profile| profile.get("email"))
                .and_then(Value::as_str)
        })
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(str::to_string)
}

fn jwt_plan(token: &str) -> Option<String> {
    let payload = jwt_payload(token)?;
    payload
        .get("chatgpt_plan_type")
        .and_then(Value::as_str)
        .or_else(|| {
            payload
                .get("https://api.openai.com/auth")
                .and_then(Value::as_object)
                .and_then(|auth| auth.get("chatgpt_plan_type"))
                .and_then(Value::as_str)
        })
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(str::to_string)
}

pub(crate) fn clean_plan(value: impl AsRef<str>) -> String {
    value
        .as_ref()
        .split(['_', '-'])
        .filter(|part| !part.is_empty())
        .map(|part| {
            let mut chars = part.chars();
            match chars.next() {
                Some(first) => format!("{}{}", first.to_uppercase(), chars.as_str()),
                None => String::new(),
            }
        })
        .collect::<Vec<_>>()
        .join(" ")
}

fn deserialize_optional_non_empty_string<'de, D>(
    deserializer: D,
) -> Result<Option<String>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    let value = Option::<Value>::deserialize(deserializer)?;
    Ok(value
        .as_ref()
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(str::to_string))
}

fn deserialize_optional_f64<'de, D>(deserializer: D) -> Result<Option<f64>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    let value = Option::<Value>::deserialize(deserializer)?;
    Ok(match value {
        Some(Value::Number(n)) => n.as_f64(),
        Some(Value::String(s)) => s.parse::<f64>().ok(),
        _ => None,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::agent_account_scope::test_support::TestRefreshScope;

    fn enrichment_scope(tag: &str) -> (TestRefreshScope, AccountScope) {
        let scope = TestRefreshScope::new("fixture", tag);
        let account_scope = scope
            .resolve_current("fixture", tag, tag.as_bytes())
            .unwrap();
        (scope, account_scope)
    }

    fn enrichment_snapshot(
        account_scope: Result<AccountScope, AccountScopeError>,
        windows: Vec<UsageWindow>,
    ) -> AgentUsageSnapshot {
        AgentUsageSnapshot {
            client_id: "fixture".to_string(),
            source: "fixture".to_string(),
            updated_at: String::new(),
            identity: None,
            account_scope,
            windows,
            credits: None,
            error: None,
        }
    }

    fn enrichment_window(
        now: DateTime<Utc>,
        card_id: &str,
        window_key: &str,
        used_percent: f64,
        duration_source: Option<DurationSource>,
    ) -> UsageWindow {
        let reset = now + chrono::Duration::days(1);
        let window =
            UsageWindow::from_used_percent(card_id.to_string(), used_percent, Some(reset), now)
                .with_identity(card_id, Some(window_key.to_string()));
        match duration_source {
            Some(DurationSource::Provider) => window.with_duration_evidence(
                now,
                true,
                Some(DurationEvidence::provider(reset.timestamp(), 86_400)),
                None,
            ),
            Some(DurationSource::Contract) => window.with_duration_evidence(
                now,
                true,
                None,
                Some(DurationEvidence::contract(86_400)),
            ),
            Some(DurationSource::Observed) => panic!("observed duration is never retained"),
            None => window,
        }
    }

    fn enrichment_failure_windows(now: DateTime<Utc>) -> Vec<UsageWindow> {
        vec![
            enrichment_window(now, "first.v1", "first.v1", 10.0, None),
            enrichment_window(now, "second.v1", "second.v1", 20.0, None),
            UsageWindow::from_used_percent("Missing".to_string(), 30.0, None, now)
                .with_identity("missing.v1", Some("missing.v1".to_string())),
            UsageWindow::from_used_percent("Non-recurring".to_string(), 40.0, None, now)
                .with_identity("non-recurring.v1", Some("non-recurring.v1".to_string()))
                .with_unavailable_reason("nonRecurring"),
        ]
    }

    #[test]
    fn serializes_stage3a_pace_states_without_legacy_scalars() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let ready = UsageWindow::from_used_percent(
            "Known".to_string(),
            20.0,
            Some(now + chrono::Duration::hours(1)),
            now,
        )
        .with_identity("known.v1", Some("known.v1".to_string()));
        let missing_reset = UsageWindow::from_used_percent("No reset".to_string(), 20.0, None, now)
            .with_identity("no_reset.v1", Some("no_reset.v1".to_string()));
        let unknown = UsageWindow::from_used_percent(
            "Unknown".to_string(),
            20.0,
            Some(now + chrono::Duration::hours(1)),
            now,
        )
        .with_identity("row.unknown.v1", None);

        let values = [ready, missing_reset, unknown]
            .into_iter()
            .map(|window| serde_json::to_value(window).unwrap())
            .collect::<Vec<_>>();
        assert_eq!(values[0]["cardId"], "known.v1");
        assert_eq!(values[0]["paceStatus"]["state"], "learningDuration");
        assert_eq!(values[1]["paceStatus"]["state"], "unavailable");
        assert_eq!(values[1]["paceStatus"]["reason"], "missingReset");
        assert_eq!(values[2]["paceStatus"]["state"], "unavailable");
        assert_eq!(values[2]["paceStatus"]["reason"], "windowIdentity");
        for value in values {
            assert!(value.get("paceStatus").is_some());
            assert!(value.get("historicalPace").is_none());
            assert!(value.get("windowMinutes").is_none());
            assert!(value.get("historicalExpectedPercent").is_none());
            assert!(value.get("runOutProbability").is_none());
        }
    }

    #[test]
    fn observed_duration_evidence_preserves_reset_presence() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let future_reset = now + chrono::Duration::hours(1);
        let past_reset = now - chrono::Duration::hours(1);

        let absent = UsageWindow::from_used_percent("Absent".to_string(), 20.0, None, now)
            .with_identity("absent.v1", Some("absent.v1".to_string()))
            .with_observed_duration_evidence(now, false);
        assert_eq!(absent.pace_reason_for_test(), Some("missingReset"));

        let mut malformed = UsageWindow::from_used_percent(
            "Malformed".to_string(),
            20.0,
            Some(future_reset),
            now,
        )
        .with_identity("malformed.v1", Some("malformed.v1".to_string()));
        malformed.resets_at = Some("bogus".to_string());
        malformed = malformed.with_observed_duration_evidence(now, true);
        assert_eq!(malformed.pace_reason_for_test(), Some("invalidEvidence"));

        let past = UsageWindow::from_used_percent("Past".to_string(), 20.0, Some(past_reset), now)
            .with_identity("past.v1", Some("past.v1".to_string()))
            .with_observed_duration_evidence(now, true);
        assert_eq!(past.pace_reason_for_test(), Some("invalidEvidence"));

        let future = UsageWindow::from_used_percent(
            "Future".to_string(),
            20.0,
            Some(future_reset),
            now,
        )
        .with_identity("future.v1", Some("future.v1".to_string()))
        .with_observed_duration_evidence(now, true);
        assert_eq!(future.pace_reason_for_test(), None);
        assert_eq!(future.pace_status.state, PaceState::LearningDuration);
        assert_eq!(
            future.pace_status.duration_source,
            Some(DurationSource::Observed)
        );

        let subsecond_now = DateTime::parse_from_rfc3339("2026-07-10T00:00:00.000500Z")
            .unwrap()
            .with_timezone(&Utc);
        let subsecond_reset = DateTime::parse_from_rfc3339("2026-07-10T00:00:00.000900Z")
            .unwrap()
            .with_timezone(&Utc);
        let subsecond = UsageWindow::from_used_percent(
            "Subsecond".to_string(),
            20.0,
            Some(subsecond_reset),
            subsecond_now,
        )
        .with_identity("subsecond.v1", Some("subsecond.v1".to_string()))
        .with_observed_duration_evidence(subsecond_now, true);
        assert_eq!(subsecond.pace_status.state, PaceState::LearningDuration);
        assert_eq!(
            subsecond.pace_status.duration_source,
            Some(DurationSource::Observed)
        );

        let mut missing_identity = UsageWindow::from_used_percent(
            "Missing identity".to_string(),
            20.0,
            Some(future_reset),
            now,
        )
        .with_identity("row.missing.v1", None);
        missing_identity.resets_at = Some("bogus".to_string());
        missing_identity = missing_identity.with_observed_duration_evidence(now, true);
        assert_eq!(missing_identity.pace_reason_for_test(), Some("windowIdentity"));
    }

    #[test]
    fn rejects_malformed_usage_window_wire() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let mut window = UsageWindow::from_used_percent(
            "Known".to_string(),
            20.0,
            Some(now + chrono::Duration::hours(1)),
            now,
        )
        .with_identity("known.v1", Some("known.v1".to_string()));
        window.card_id.clear();
        assert!(serde_json::to_value(&window).is_err());
        window.card_id = "known.v1".to_string();
        window.used_percent = f64::NAN;
        assert!(serde_json::to_value(&window).is_err());
    }

    #[test]
    fn serialized_duration_mirror_is_nested_and_state_coherent() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let window = map_window_with_identity(
            "Session",
            CodexWindow {
                used_percent: 20.0,
                reset_at: now.timestamp() + 3_600,
                limit_window_seconds: 18_000,
            },
            now,
            "main.session.v1",
            Some("main.session.v1".to_string()),
        )
        .unwrap();
        let wire = serde_json::to_value(&window).unwrap();
        assert_eq!(wire["paceStatus"]["durationSeconds"], 18_000);
        assert_eq!(wire["paceStatus"]["durationSource"], "provider");
        assert_eq!(wire["windowMinutes"], 300);
        assert_eq!(
            wire["windowMinutes"].as_i64(),
            wire["paceStatus"]["durationSeconds"]
                .as_i64()
                .map(|seconds| seconds / 60)
        );

        let mut contradictory = window;
        contradictory.pace_status.state = PaceState::Unavailable;
        contradictory.pace_status.reason = Some("invalidEvidence".to_string());
        assert!(serde_json::to_value(&contradictory).is_err());
        contradictory.pace_status.state = PaceState::LearningHistory;
        contradictory.pace_status.reason = None;
        contradictory.pace_status.duration_source = None;
        assert!(serde_json::to_value(&contradictory).is_err());
        contradictory.pace_status.duration_source = Some(DurationSource::Provider);
        contradictory.pace_status.duration_seconds = Some(604_800);
        assert!(serde_json::to_value(&contradictory).is_err());
    }

    #[test]
    fn retain_unique_windows_drops_later_card_or_window_identity() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let first = UsageWindow::from_used_percent(
            "First".to_string(),
            20.0,
            Some(now + chrono::Duration::hours(1)),
            now,
        )
        .with_identity("card.a.v1", Some("window.a.v1".to_string()));
        let duplicate_card = UsageWindow::from_used_percent(
            "Later card".to_string(),
            30.0,
            Some(now + chrono::Duration::hours(1)),
            now,
        )
        .with_identity("card.a.v1", Some("window.b.v1".to_string()));
        let duplicate_key = UsageWindow::from_used_percent(
            "Later key".to_string(),
            40.0,
            Some(now + chrono::Duration::hours(1)),
            now,
        )
        .with_identity("card.c.v1", Some("window.a.v1".to_string()));
        let mut windows = vec![first, duplicate_card, duplicate_key];
        retain_unique_windows(&mut windows);
        assert_eq!(windows.len(), 1);
        assert_eq!(windows[0].label_for_test(), "First");
    }

    #[test]
    fn enrichment_scope_failure_preserves_identity_and_non_recurring_rows() {
        let (scope, _) = enrichment_scope("enrichment-scope-failure");
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let stable = enrichment_window(
            now,
            "stable.v1",
            "stable.v1",
            20.0,
            Some(DurationSource::Contract),
        );
        let identity = UsageWindow::from_used_percent(
            "Identity".to_string(),
            30.0,
            Some(now + chrono::Duration::days(1)),
            now,
        )
        .with_identity("identity.v1", None);
        let non_recurring =
            UsageWindow::from_used_percent("Non-recurring".to_string(), 40.0, None, now)
                .with_identity("non-recurring.v1", Some("non-recurring.v1".to_string()))
                .with_unavailable_reason("nonRecurring");
        let mut snapshot = enrichment_snapshot(
            Err(AccountScopeError::MetadataWrite),
            vec![stable, identity, non_recurring],
        );
        let calls = std::cell::Cell::new(0);

        enrich_snapshot_with(&mut snapshot, now.timestamp(), |_, _, _| {
            calls.set(calls.get() + 1);
            Ok(Vec::new())
        });

        assert_eq!(calls.get(), 0);
        assert_eq!(
            snapshot.windows[0].pace_reason_for_test(),
            Some("accountScope")
        );
        assert_eq!(
            snapshot.windows[1].pace_reason_for_test(),
            Some("windowIdentity")
        );
        assert_eq!(
            snapshot.windows[2].pace_reason_for_test(),
            Some("nonRecurring")
        );
        assert!(serde_json::to_value(&snapshot).is_ok());
        scope.cleanup();
    }

    #[test]
    fn enrichment_filters_duplicate_card_and_window_keys_before_batching() {
        let (scope, account_scope) = enrichment_scope("enrichment-duplicates");
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let mut snapshot = enrichment_snapshot(
            Ok(account_scope),
            vec![
                enrichment_window(now, "shared-card.v1", "first.v1", 10.0, None),
                enrichment_window(now, "second-card.v1", "first.v1", 20.0, None),
                enrichment_window(now, "shared-card.v1", "third.v1", 30.0, None),
            ],
        );

        enrich_snapshot_with(
            &mut snapshot,
            now.timestamp(),
            |active, observations, batch_now| {
                assert_eq!(batch_now, now.timestamp());
                assert_eq!(active.len(), 1);
                assert_eq!(active[0].window_key, "first.v1");
                assert_eq!(observations.len(), 1);
                assert_eq!(observations[0].used_percent, 10.0);
                Ok(vec![Ok((HistoryOutcome::LearningDuration, None, 0))])
            },
        );

        assert_eq!(snapshot.windows.len(), 1);
        assert_eq!(snapshot.windows[0].card_id_for_test(), "shared-card.v1");
        assert_eq!(
            snapshot.windows[0].pace_status.duration_source,
            Some(DurationSource::Observed)
        );
        assert!(serde_json::to_value(&snapshot).is_ok());
        scope.cleanup();
    }

    #[test]
    fn enrichment_builds_active_keys_observations_and_coherent_results() {
        let (scope, account_scope) = enrichment_scope("enrichment-batch-map");
        let expected_scope = account_scope.as_str().to_string();
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let reset = now + chrono::Duration::days(1);
        let mut invalid_reset =
            enrichment_window(now, "invalid-reset.v1", "invalid-reset.v1", 50.0, None);
        invalid_reset.resets_at = Some("not-rfc3339".to_string());
        invalid_reset.used_percent = f64::NAN;
        invalid_reset.remaining_percent = f64::NAN;
        let expired = UsageWindow::from_used_percent(
            "Expired".to_string(),
            60.0,
            Some(now - chrono::Duration::seconds(1)),
            now,
        )
        .with_identity("expired.v1", Some("expired.v1".to_string()));
        let mut invalid_percent =
            enrichment_window(now, "invalid-percent.v1", "invalid-percent.v1", 70.0, None);
        invalid_percent.used_percent = f64::NAN;
        invalid_percent.remaining_percent = f64::NAN;
        let missing = UsageWindow::from_used_percent("Missing".to_string(), 30.0, None, now)
            .with_identity("missing.v1", Some("missing.v1".to_string()));
        let non_recurring =
            UsageWindow::from_used_percent("Non-recurring".to_string(), 40.0, None, now)
                .with_identity("non-recurring.v1", Some("non-recurring.v1".to_string()))
                .with_unavailable_reason("nonRecurring");
        let mut snapshot = enrichment_snapshot(
            Ok(account_scope),
            vec![
                enrichment_window(
                    now,
                    "provider.v1",
                    "provider.v1",
                    10.0,
                    Some(DurationSource::Provider),
                ),
                enrichment_window(
                    now,
                    "contract.v1",
                    "contract.v1",
                    20.0,
                    Some(DurationSource::Contract),
                ),
                enrichment_window(now, "observed.v1", "observed.v1", 25.0, None),
                missing,
                non_recurring,
                invalid_reset,
                expired,
                invalid_percent,
            ],
        );

        enrich_snapshot_with(&mut snapshot, now.timestamp(), |active, observations, _| {
            assert_eq!(
                active,
                &[
                    SeriesKey::new("fixture", &expected_scope, "provider.v1"),
                    SeriesKey::new("fixture", &expected_scope, "contract.v1"),
                    SeriesKey::new("fixture", &expected_scope, "observed.v1"),
                    SeriesKey::new("fixture", &expected_scope, "missing.v1"),
                    SeriesKey::new("fixture", &expected_scope, "invalid-reset.v1"),
                    SeriesKey::new("fixture", &expected_scope, "expired.v1"),
                    SeriesKey::new("fixture", &expected_scope, "invalid-percent.v1"),
                ]
            );
            assert_eq!(observations.len(), 3);
            assert_eq!(observations[0].reset_at, Some(reset.timestamp()));
            assert_eq!(
                observations[0].provider,
                Some(DurationEvidence::provider(reset.timestamp(), 86_400))
            );
            assert_eq!(observations[0].contract, None);
            assert_eq!(observations[1].provider, None);
            assert_eq!(
                observations[1].contract,
                Some(DurationEvidence::contract(86_400))
            );
            assert_eq!(observations[2].provider, None);
            assert_eq!(observations[2].contract, None);
            Ok(vec![
                Ok((
                    HistoryOutcome::Ready {
                        duration_seconds: 86_400,
                        source: DurationSource::Provider,
                        sampled: true,
                    },
                    None,
                    2,
                )),
                Ok((
                    HistoryOutcome::Ready {
                        duration_seconds: 86_400,
                        source: DurationSource::Contract,
                        sampled: true,
                    },
                    Some(HistoricalPace {
                        expected_percent: 42.0,
                        eta_seconds: Some(900.0),
                        will_last_to_reset: false,
                        run_out_probability: Some(0.25),
                    }),
                    4,
                )),
                Ok((HistoryOutcome::LearningDuration, None, 0)),
            ])
        });

        assert_eq!(
            snapshot.windows[0].pace_status.state,
            PaceState::LearningHistory
        );
        assert_eq!(snapshot.windows[0].pace_status.complete_cycles, 2);
        assert_eq!(snapshot.windows[1].pace_status.state, PaceState::Available);
        assert_eq!(snapshot.windows[1].pace_status.complete_cycles, 4);
        let historical = snapshot.windows[1].historical_pace.as_ref().unwrap();
        assert_eq!(historical.expected_used_percent, 42.0);
        assert_eq!(historical.eta_seconds, Some(900.0));
        assert!(!historical.will_last_to_reset);
        assert_eq!(historical.run_out_probability, Some(0.25));
        assert_eq!(
            snapshot.windows[2].pace_status.state,
            PaceState::LearningDuration
        );
        assert_eq!(
            snapshot.windows[2].pace_status.duration_source,
            Some(DurationSource::Observed)
        );
        assert_eq!(
            snapshot.windows[3].pace_reason_for_test(),
            Some("missingReset")
        );
        assert_eq!(
            snapshot.windows[4].pace_reason_for_test(),
            Some("nonRecurring")
        );
        for window in &snapshot.windows[5..] {
            assert_eq!(window.pace_reason_for_test(), Some("invalidEvidence"));
        }
        let wire = serde_json::to_value(&snapshot).unwrap();
        assert_eq!(wire["windows"][0]["windowMinutes"], 1_440);
        assert_eq!(wire["windows"][1]["windowMinutes"], 1_440);
        assert!(wire["windows"][2].get("windowMinutes").is_none());
        scope.cleanup();
    }

    #[test]
    fn enrichment_maps_unavailable_and_rejects_contradictory_results() {
        let (scope, account_scope) = enrichment_scope("enrichment-result-validation");
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let mut snapshot = enrichment_snapshot(
            Ok(account_scope),
            vec![
                enrichment_window(now, "missing.v1", "missing.v1", 10.0, None),
                enrichment_window(now, "invalid.v1", "invalid.v1", 20.0, None),
                enrichment_window(
                    now,
                    "learning-conflict.v1",
                    "learning-conflict.v1",
                    30.0,
                    Some(DurationSource::Contract),
                ),
                enrichment_window(
                    now,
                    "historical-conflict.v1",
                    "historical-conflict.v1",
                    40.0,
                    Some(DurationSource::Contract),
                ),
                enrichment_window(
                    now,
                    "source-conflict.v1",
                    "source-conflict.v1",
                    50.0,
                    Some(DurationSource::Contract),
                ),
                enrichment_window(
                    now,
                    "unavailable-conflict.v1",
                    "unavailable-conflict.v1",
                    60.0,
                    None,
                ),
                enrichment_window(
                    now,
                    "nonfinite-history.v1",
                    "nonfinite-history.v1",
                    70.0,
                    Some(DurationSource::Contract),
                ),
            ],
        );
        let historical = HistoricalPace {
            expected_percent: 42.0,
            eta_seconds: Some(900.0),
            will_last_to_reset: false,
            run_out_probability: Some(0.25),
        };

        enrich_snapshot_with(&mut snapshot, now.timestamp(), |_, observations, _| {
            assert_eq!(observations.len(), 7);
            Ok(vec![
                Ok((
                    HistoryOutcome::Unavailable(DurationUnavailableReason::MissingReset),
                    None,
                    0,
                )),
                Ok((
                    HistoryOutcome::Unavailable(DurationUnavailableReason::InvalidEvidence),
                    None,
                    0,
                )),
                Ok((HistoryOutcome::LearningDuration, None, 0)),
                Ok((
                    HistoryOutcome::Ready {
                        duration_seconds: 86_400,
                        source: DurationSource::Contract,
                        sampled: true,
                    },
                    Some(HistoricalPace {
                        will_last_to_reset: true,
                        ..historical.clone()
                    }),
                    3,
                )),
                Ok((
                    HistoryOutcome::Ready {
                        duration_seconds: 86_400,
                        source: DurationSource::Provider,
                        sampled: true,
                    },
                    None,
                    2,
                )),
                Ok((
                    HistoryOutcome::Unavailable(DurationUnavailableReason::InvalidEvidence),
                    Some(historical.clone()),
                    0,
                )),
                Ok((
                    HistoryOutcome::Ready {
                        duration_seconds: 86_400,
                        source: DurationSource::Contract,
                        sampled: true,
                    },
                    Some(HistoricalPace {
                        expected_percent: f64::NAN,
                        ..historical.clone()
                    }),
                    3,
                )),
            ])
        });

        assert_eq!(snapshot.windows[0].pace_reason_for_test(), Some("history"));
        assert_eq!(
            snapshot.windows[1].pace_reason_for_test(),
            Some("invalidEvidence")
        );
        for window in &snapshot.windows[2..] {
            assert_eq!(window.pace_reason_for_test(), Some("history"));
        }
        assert!(serde_json::to_value(&snapshot).is_ok());
        scope.cleanup();
    }

    #[test]
    fn enrichment_maps_global_row_and_count_failures_only_to_observations() {
        let (scope, account_scope) = enrichment_scope("enrichment-errors");
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();

        let mut global_capacity =
            enrichment_snapshot(Ok(account_scope.clone()), enrichment_failure_windows(now));
        enrich_snapshot_with(
            &mut global_capacity,
            now.timestamp(),
            |active, observations, _| {
                assert_eq!(active.len(), 3);
                assert_eq!(observations.len(), 2);
                Err(HistoryError::StoreCapacity)
            },
        );
        assert_eq!(
            global_capacity.windows[0].pace_reason_for_test(),
            Some("storeCapacity")
        );
        assert_eq!(
            global_capacity.windows[1].pace_reason_for_test(),
            Some("storeCapacity")
        );
        assert_eq!(
            global_capacity.windows[2].pace_reason_for_test(),
            Some("missingReset")
        );
        assert_eq!(
            global_capacity.windows[3].pace_reason_for_test(),
            Some("nonRecurring")
        );

        let mut global_history =
            enrichment_snapshot(Ok(account_scope.clone()), enrichment_failure_windows(now));
        enrich_snapshot_with(&mut global_history, now.timestamp(), |_, _, _| {
            Err(HistoryError::Read)
        });
        assert_eq!(
            global_history.windows[0].pace_reason_for_test(),
            Some("history")
        );
        assert_eq!(
            global_history.windows[1].pace_reason_for_test(),
            Some("history")
        );
        assert_eq!(
            global_history.windows[2].pace_reason_for_test(),
            Some("missingReset")
        );
        assert_eq!(
            global_history.windows[3].pace_reason_for_test(),
            Some("nonRecurring")
        );

        let mut count_mismatch =
            enrichment_snapshot(Ok(account_scope.clone()), enrichment_failure_windows(now));
        enrich_snapshot_with(&mut count_mismatch, now.timestamp(), |_, _, _| {
            Ok(vec![Ok((HistoryOutcome::LearningDuration, None, 0))])
        });
        assert_eq!(
            count_mismatch.windows[0].pace_reason_for_test(),
            Some("history")
        );
        assert_eq!(
            count_mismatch.windows[1].pace_reason_for_test(),
            Some("history")
        );
        assert_eq!(
            count_mismatch.windows[2].pace_reason_for_test(),
            Some("missingReset")
        );
        assert_eq!(
            count_mismatch.windows[3].pace_reason_for_test(),
            Some("nonRecurring")
        );

        let mut row_errors =
            enrichment_snapshot(Ok(account_scope), enrichment_failure_windows(now));
        enrich_snapshot_with(&mut row_errors, now.timestamp(), |_, _, _| {
            Ok(vec![
                Err(HistoryError::StoreCapacity),
                Err(HistoryError::Read),
            ])
        });
        assert_eq!(
            row_errors.windows[0].pace_reason_for_test(),
            Some("storeCapacity")
        );
        assert_eq!(
            row_errors.windows[1].pace_reason_for_test(),
            Some("history")
        );
        assert_eq!(
            row_errors.windows[2].pace_reason_for_test(),
            Some("missingReset")
        );
        assert_eq!(
            row_errors.windows[3].pace_reason_for_test(),
            Some("nonRecurring")
        );
        for snapshot in [global_capacity, global_history, count_mismatch, row_errors] {
            assert!(serde_json::to_value(snapshot).is_ok());
        }
        scope.cleanup();
    }

    #[test]
    fn parses_retry_after_seconds_and_http_date() {
        let header = reqwest::header::HeaderValue::from_static("120");
        let parsed = parse_retry_after(Some(&header)).unwrap();
        let delta = (parsed - Utc::now()).num_seconds();
        assert!((118..=120).contains(&delta), "delta was {}", delta);

        let header = reqwest::header::HeaderValue::from_static("Fri, 21 Nov 2025 09:00:00 GMT");
        let parsed = parse_retry_after(Some(&header)).unwrap();
        assert_eq!(parsed.timestamp(), 1_763_715_600);

        let header = reqwest::header::HeaderValue::from_static("bogus");
        assert!(parse_retry_after(Some(&header)).is_none());
        assert!(parse_retry_after(None).is_none());
    }

    #[test]
    fn string_key_uses_first_valid_snake_or_camel_alias() {
        let cases = [
            (
                "snake priority",
                serde_json::json!({
                    "snake_key": " snake-value ",
                    "camelKey": "camel-value"
                }),
                Some("snake-value"),
            ),
            (
                "snake missing",
                serde_json::json!({ "camelKey": " camel-value " }),
                Some("camel-value"),
            ),
            (
                "snake null",
                serde_json::json!({ "snake_key": null, "camelKey": "camel-value" }),
                Some("camel-value"),
            ),
            (
                "snake empty",
                serde_json::json!({ "snake_key": "", "camelKey": "camel-value" }),
                Some("camel-value"),
            ),
            (
                "snake whitespace",
                serde_json::json!({ "snake_key": " \t\n ", "camelKey": "camel-value" }),
                Some("camel-value"),
            ),
            (
                "snake non-string",
                serde_json::json!({
                    "snake_key": { "unexpected": true },
                    "camelKey": "camel-value"
                }),
                Some("camel-value"),
            ),
            (
                "both invalid",
                serde_json::json!({ "snake_key": false, "camelKey": "   " }),
                None,
            ),
        ];

        for (label, value, expected) in cases {
            let map = value.as_object().unwrap();
            assert_eq!(
                string_key(map, "snake_key", "camelKey").as_deref(),
                expected,
                "{label}"
            );
        }
    }

    #[test]
    fn claude_refresh_response_ignores_invalid_optional_refresh_token() {
        let cases = [
            (
                "valid",
                serde_json::json!({
                    "access_token": "new-access",
                    "refresh_token": " new-refresh ",
                    "expires_in": 3600
                }),
                Some("new-refresh"),
            ),
            (
                "missing",
                serde_json::json!({ "access_token": "new-access", "expires_in": 3600 }),
                None,
            ),
            (
                "null",
                serde_json::json!({
                    "access_token": "new-access",
                    "refresh_token": null,
                    "expires_in": 3600
                }),
                None,
            ),
            (
                "empty",
                serde_json::json!({
                    "access_token": "new-access",
                    "refresh_token": "",
                    "expires_in": 3600
                }),
                None,
            ),
            (
                "whitespace",
                serde_json::json!({
                    "access_token": "new-access",
                    "refresh_token": " \t\n ",
                    "expires_in": 3600
                }),
                None,
            ),
            (
                "non-string",
                serde_json::json!({
                    "access_token": "new-access",
                    "refresh_token": { "unexpected": true },
                    "expires_in": 3600
                }),
                None,
            ),
        ];

        for (label, value, expected) in cases {
            let response: ClaudeRefreshResponse = serde_json::from_value(value).unwrap();
            assert_eq!(response.access_token, "new-access", "{label}");
            assert_eq!(response.expires_in, 3_600, "{label}");
            assert_eq!(response.refresh_token.as_deref(), expected, "{label}");
        }
    }

    #[test]
    fn account_scope_and_credential_markers_never_reach_the_wire() {
        let scope_store = TestRefreshScope::new("codex", "agent-usage-wire-privacy");
        let marker = b"sensitive-refresh-token-marker";
        let account_scope = scope_store
            .resolve_current("codex-auth-json", "fixture-location", marker)
            .unwrap();
        let opaque_scope = account_scope.as_str().to_string();
        let snapshot = AgentUsageSnapshot {
            client_id: "codex".to_string(),
            source: "oauth".to_string(),
            updated_at: "2026-07-18T00:00:00.000Z".to_string(),
            identity: None,
            account_scope: Ok(account_scope),
            windows: Vec::new(),
            credits: None,
            error: None,
        };

        let wire = serde_json::to_string(&snapshot).unwrap();
        assert!(!wire.contains("accountScope"));
        assert!(!wire.contains(String::from_utf8_lossy(marker).as_ref()));
        assert!(!wire.contains(&opaque_scope));
        scope_store.cleanup();
    }

    #[test]
    fn credential_markers_and_locations_follow_the_canonical_routes() {
        let scope_store = TestRefreshScope::new("codex", "agent-usage-locations");
        let auth_path = scope_store.root().join("codex/auth.json");
        fs::create_dir_all(auth_path.parent().unwrap()).unwrap();
        fs::write(
            &auth_path,
            serde_json::json!({
                "tokens": {
                    "access_token": " codex-access ",
                    "refresh_token": " codex-refresh "
                }
            })
            .to_string(),
        )
        .unwrap();
        let codex = load_codex_credentials_from(&auth_path).unwrap();
        assert_eq!(codex.scope_slot.semantic_source, "codex-auth-json");
        assert_eq!(
            codex.scope_slot.canonical_location,
            agent_account_scope::canonical_file_location(&auth_path, Some("tokens")).unwrap()
        );
        assert_eq!(codex.scope_marker(), b"codex-refresh");
        let mut codex_access_only = codex.clone();
        codex_access_only.refresh_token = None;
        assert_eq!(codex_access_only.scope_marker(), b"codex-access");

        let claude_file_slot = claude_login_scope_slot(ClaudeCredentialSource::File).unwrap();
        assert_eq!(claude_file_slot.semantic_source, "claude-login-file");
        assert_eq!(
            claude_file_slot.canonical_location,
            agent_account_scope::canonical_file_location(
                &claude_credentials_path(),
                Some("claudeAiOauth")
            )
            .unwrap()
        );
        let claude_keychain_slot =
            claude_login_scope_slot(ClaudeCredentialSource::Keychain).unwrap();
        assert_eq!(
            claude_keychain_slot.semantic_source,
            "claude-login-keychain"
        );
        assert_eq!(
            claude_keychain_slot.canonical_location,
            CLAUDE_KEYCHAIN_SERVICE
        );

        let claude_login = ClaudeCredentials {
            access_token: "claude-access".to_string(),
            refresh_token: Some("claude-refresh".to_string()),
            expires_at: None,
            scopes: Vec::new(),
            rate_limit_tier: None,
            subscription_type: None,
            source: ClaudeCredentialSource::File,
            raw_root: None,
            scope_slot: claude_file_slot,
        };
        assert_eq!(
            claude_login.scope_marker(),
            Some(b"claude-refresh".as_slice())
        );
        let mut login_without_refresh = claude_login.clone();
        login_without_refresh.refresh_token = None;
        assert_eq!(login_without_refresh.scope_marker(), None);

        let claude_setup = claude_credentials_from_access_token(ResolvedClaudeToken {
            access_token: "claude-setup-access".to_string(),
            scope_slot: CredentialSlot {
                semantic_source: "claude-code-environment",
                canonical_location: "CLAUDE_CODE_OAUTH_TOKEN".to_string(),
            },
        });
        assert_eq!(
            claude_setup.scope_marker(),
            Some(b"claude-setup-access".as_slice())
        );
        assert_eq!(
            claude_setup.scope_slot.semantic_source,
            "claude-code-environment"
        );
        assert_eq!(
            claude_setup.scope_slot.canonical_location,
            "CLAUDE_CODE_OAUTH_TOKEN"
        );
        scope_store.cleanup();
    }

    #[test]
    fn codex_scope_precedence_keeps_refresh_failure_sticky() {
        let scope_store = TestRefreshScope::new("codex", "codex-scope-precedence");
        let refresh_scope = scope_store
            .resolve_current("fixture", "refresh", b"refresh-marker")
            .unwrap();
        let authoritative_scope = scope_store
            .resolve_current("fixture", "authoritative", b"authoritative-marker")
            .unwrap();
        let credential_scope = scope_store
            .resolve_current("fixture", "credential", b"credential-marker")
            .unwrap();
        let authoritative_calls = std::cell::Cell::new(0);
        let credential_calls = std::cell::Cell::new(0);

        let resolved = resolve_codex_account_scope(
            Some(Err(AccountScopeError::MetadataWrite)),
            Some("acct-id"),
            |_| {
                authoritative_calls.set(authoritative_calls.get() + 1);
                Ok(authoritative_scope.clone())
            },
            || {
                credential_calls.set(credential_calls.get() + 1);
                Ok(credential_scope.clone())
            },
        );
        assert_eq!(resolved, Err(AccountScopeError::MetadataWrite));
        assert_eq!(authoritative_calls.get(), 0);
        assert_eq!(credential_calls.get(), 0);

        let resolved = resolve_codex_account_scope(
            Some(Err(AccountScopeError::MetadataRead)),
            None,
            |_| {
                authoritative_calls.set(authoritative_calls.get() + 1);
                Ok(authoritative_scope.clone())
            },
            || {
                credential_calls.set(credential_calls.get() + 1);
                Ok(credential_scope.clone())
            },
        );
        assert_eq!(resolved, Err(AccountScopeError::MetadataRead));
        assert_eq!(authoritative_calls.get(), 0);
        assert_eq!(credential_calls.get(), 0);

        let resolved = resolve_codex_account_scope(
            Some(Ok(refresh_scope.clone())),
            Some("acct-id"),
            |_| {
                authoritative_calls.set(authoritative_calls.get() + 1);
                Ok(authoritative_scope.clone())
            },
            || {
                credential_calls.set(credential_calls.get() + 1);
                Ok(credential_scope.clone())
            },
        );
        assert_eq!(resolved.unwrap(), authoritative_scope);
        assert_eq!(authoritative_calls.get(), 1);
        assert_eq!(credential_calls.get(), 0);

        let resolved = resolve_codex_account_scope(
            Some(Ok(refresh_scope.clone())),
            None,
            |_| {
                authoritative_calls.set(authoritative_calls.get() + 1);
                Ok(authoritative_scope.clone())
            },
            || {
                credential_calls.set(credential_calls.get() + 1);
                Ok(credential_scope.clone())
            },
        );
        assert_eq!(resolved.unwrap(), refresh_scope);
        assert_eq!(authoritative_calls.get(), 1);
        assert_eq!(credential_calls.get(), 0);

        let resolved = resolve_codex_account_scope(
            None,
            None,
            |_| {
                authoritative_calls.set(authoritative_calls.get() + 1);
                Ok(authoritative_scope.clone())
            },
            || {
                credential_calls.set(credential_calls.get() + 1);
                Ok(credential_scope.clone())
            },
        );
        assert_eq!(resolved.unwrap(), credential_scope);
        assert_eq!(authoritative_calls.get(), 1);
        assert_eq!(credential_calls.get(), 1);
        scope_store.cleanup();
    }

    #[test]
    fn codex_v2_migration_requires_request_id_and_scope_and_is_best_effort() {
        let scope_store = TestRefreshScope::new("codex", "codex-v2-migration-gate");
        let account_scope = scope_store
            .resolve_current("fixture", "codex-v2", b"codex-v2-marker")
            .unwrap();
        let opaque_scope = account_scope.as_str().to_string();
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let calls = std::cell::RefCell::new(Vec::new());

        maybe_migrate_codex_v2_with(
            Some("request-account"),
            &Ok(account_scope.clone()),
            now.timestamp(),
            |request_account_id, scope, call_now| {
                calls.borrow_mut().push((
                    request_account_id.to_string(),
                    scope.to_string(),
                    call_now,
                ));
                Ok(())
            },
        );
        assert_eq!(
            calls.borrow().as_slice(),
            &[(
                "request-account".to_string(),
                opaque_scope.clone(),
                now.timestamp(),
            )]
        );

        let skipped_calls = std::cell::Cell::new(0);
        maybe_migrate_codex_v2_with(
            None,
            &Ok(account_scope.clone()),
            now.timestamp(),
            |_, _, _| {
                skipped_calls.set(skipped_calls.get() + 1);
                Ok(())
            },
        );
        maybe_migrate_codex_v2_with(
            Some(" \t"),
            &Ok(account_scope.clone()),
            now.timestamp(),
            |_, _, _| {
                skipped_calls.set(skipped_calls.get() + 1);
                Ok(())
            },
        );
        maybe_migrate_codex_v2_with(
            Some("request-account"),
            &Err(AccountScopeError::MetadataRead),
            now.timestamp(),
            |_, _, _| {
                skipped_calls.set(skipped_calls.get() + 1);
                Ok(())
            },
        );
        assert_eq!(skipped_calls.get(), 0);

        let migration_error_calls = std::cell::Cell::new(0);
        maybe_migrate_codex_v2_with(
            Some("request-account"),
            &Ok(account_scope.clone()),
            now.timestamp(),
            |_, _, _| {
                migration_error_calls.set(migration_error_calls.get() + 1);
                Err::<(), _>(HistoryError::AtomicSave)
            },
        );

        let mut snapshot = enrichment_snapshot(
            Ok(account_scope),
            vec![enrichment_window(
                now,
                "weekly.v1",
                "weekly.v1",
                20.0,
                Some(DurationSource::Contract),
            )],
        );
        let record_calls = std::cell::Cell::new(0);
        enrich_snapshot_with(&mut snapshot, now.timestamp(), |_, observations, _| {
            record_calls.set(record_calls.get() + 1);
            assert_eq!(observations.len(), 1);
            Ok(vec![Ok((
                HistoryOutcome::Ready {
                    duration_seconds: 86_400,
                    source: DurationSource::Contract,
                    sampled: true,
                },
                None,
                1,
            ))])
        });
        assert_eq!(migration_error_calls.get(), 1);
        assert_eq!(record_calls.get(), 1);
        assert_eq!(
            snapshot.windows[0].pace_status.state,
            PaceState::LearningHistory
        );
        scope_store.cleanup();
    }

    // Single test for the whole gate lifecycle — the gate is a process-wide
    // static, so split tests would race under the parallel test runner.
    #[test]
    fn claude_gate_blocks_then_clears() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        assert!(claude_gate_blocked_until(now).is_none());

        // 429 with no Retry-After → default 5-minute cooldown.
        claude_gate_record_rate_limit(None, now);
        let until = claude_gate_blocked_until(now).unwrap();
        assert_eq!((until - now).num_seconds(), 300);

        // No cached snapshot yet → countdown error.
        let fallback = claude_gate_fallback(until, now);
        assert!(fallback.error.unwrap().contains("~300s"));
        assert!(fallback.windows.is_empty());

        // Cooldown expiry clears the gate lazily.
        let later = now + chrono::Duration::seconds(301);
        assert!(claude_gate_blocked_until(later).is_none());

        // Success caches the display-ready snapshot; a later 429 returns those
        // rows unchanged, without another enrichment/history pass, while dropping
        // stale account evidence from the earlier authenticated poll.
        let scope_store = TestRefreshScope::new("claude", "cached-429");
        let account_scope = scope_store
            .resolve_current("fixture", "cached-429", b"cached-429-marker")
            .unwrap();
        let reset = now + chrono::Duration::days(1);
        let mut snapshot = AgentUsageSnapshot {
            client_id: "claude".to_string(),
            source: "oauth".to_string(),
            updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
            identity: None,
            account_scope: Ok(account_scope),
            windows: vec![UsageWindow::from_used_percent(
                "Session".to_string(),
                20.0,
                Some(reset),
                now,
            )
            .with_identity("session.v1", Some("session.v1".to_string()))
            .with_duration_evidence(
                now,
                true,
                None,
                Some(DurationEvidence::contract(86_400)),
            )],
            credits: None,
            error: None,
        };
        let record_calls = std::cell::Cell::new(0);
        enrich_snapshot_with(
            &mut snapshot,
            now.timestamp(),
            |active, observations, batch_now| {
                record_calls.set(record_calls.get() + 1);
                assert_eq!(batch_now, now.timestamp());
                assert_eq!(active.len(), 1);
                assert_eq!(observations.len(), 1);
                Ok(vec![Ok((
                    HistoryOutcome::Ready {
                        duration_seconds: 86_400,
                        source: DurationSource::Contract,
                        sampled: true,
                    },
                    Some(HistoricalPace {
                        expected_percent: 35.0,
                        eta_seconds: Some(1_800.0),
                        will_last_to_reset: false,
                        run_out_probability: Some(0.42),
                    }),
                    6,
                ))])
            },
        );
        assert_eq!(record_calls.get(), 1);
        assert_eq!(snapshot.windows[0].pace_status.state, PaceState::Available);
        claude_gate_record_success(&snapshot);
        assert!(claude_gate_blocked_until(later).is_none());
        claude_gate_record_rate_limit(Some(later + chrono::Duration::seconds(60)), later);
        let until = claude_gate_blocked_until(later).unwrap();
        let fallback = claude_gate_fallback(until, later);
        assert_eq!(record_calls.get(), 1);
        assert!(fallback.error.is_none());
        assert_eq!(fallback.windows.len(), 1);
        assert_eq!(fallback.windows[0].label, "Session");
        assert_eq!(fallback.windows[0].card_id, "session.v1");
        assert_eq!(fallback.windows[0].used_percent, 20.0);
        assert_eq!(fallback.windows[0].remaining_percent, 80.0);
        assert_eq!(fallback.windows[0].pace_status.state, PaceState::Available);
        assert_eq!(fallback.windows[0].pace_status.complete_cycles, 6);
        assert_eq!(
            fallback.windows[0]
                .historical_pace
                .as_ref()
                .map(|pace| pace.expected_used_percent),
            Some(35.0)
        );
        assert!(matches!(
            &fallback.account_scope,
            Err(AccountScopeError::NoTrustedEvidence)
        ));

        // Leave the gate clean for any other test touching the static.
        claude_gate_record_success(&snapshot);
        scope_store.cleanup();
    }

    #[test]
    fn maps_codex_primary_and_secondary_windows() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let rate_limit = CodexRateLimit {
            primary_window: Some(CodexWindow {
                used_percent: 8.0,
                reset_at: 1_700_005_400,
                limit_window_seconds: 18_000,
            }),
            secondary_window: Some(CodexWindow {
                used_percent: 35.0,
                reset_at: 1_700_172_800,
                limit_window_seconds: 604_800,
            }),
        };
        let windows = codex_windows(Some(&rate_limit), None, now);
        assert_eq!(windows.len(), 2);
        assert_eq!(windows[0].label, "Session");
        assert_eq!(windows[0].card_id_for_test(), "main.session.v1");
        assert_eq!(
            windows[0].pace_window_key_for_test(),
            Some("main.session.v1")
        );
        assert_eq!(windows[0].remaining_percent, 92.0);
        assert_eq!(windows[0].pace_status.state, PaceState::LearningHistory);
        assert_eq!(windows[0].pace_status.duration_seconds, Some(18_000));
        assert_eq!(
            windows[0].pace_status.duration_source,
            Some(DurationSource::Provider)
        );
        assert_eq!(windows[1].label, "Weekly");
        assert_eq!(windows[1].card_id_for_test(), "main.weekly.v1");
        assert_eq!(
            windows[1].pace_window_key_for_test(),
            Some("main.weekly.v1")
        );
        assert_eq!(windows[1].remaining_percent, 65.0);
        assert_eq!(windows[1].pace_status.state, PaceState::LearningHistory);
        assert_eq!(windows[1].pace_status.duration_seconds, Some(604_800));
        assert_eq!(
            windows[1].pace_status.duration_source,
            Some(DurationSource::Provider)
        );
    }

    #[test]
    fn agent_usage_payload_omits_legacy_history_fields() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let rate_limit = CodexRateLimit {
            primary_window: Some(CodexWindow {
                used_percent: 35.0,
                reset_at: 1_700_172_800,
                limit_window_seconds: 604_800,
            }),
            secondary_window: None,
        };
        let payload = AgentUsagePayload {
            generated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
            agents: vec![AgentUsageSnapshot {
                client_id: "codex".to_string(),
                source: "oauth".to_string(),
                updated_at: now.to_rfc3339_opts(SecondsFormat::Millis, true),
                identity: None,
                account_scope: Err(AccountScopeError::NoTrustedEvidence),
                windows: codex_windows(Some(&rate_limit), None, now),
                credits: None,
                error: None,
            }],
            opencode_subscriptions: Vec::new(),
        };
        let serialized = serde_json::to_value(payload).unwrap();
        let weekly = serialized["agents"][0]["windows"]
            .as_array()
            .unwrap()
            .iter()
            .find(|window| window["label"] == "Weekly")
            .expect("normal Codex Weekly mapping");
        let object = weekly.as_object().unwrap();
        assert_eq!(object["cardId"], "main.weekly.v1");
        assert_eq!(object["usedPercent"], 35.0);
        assert_eq!(object["remainingPercent"], 65.0);
        assert_eq!(object["paceStatus"]["state"], "learningHistory");
        assert_eq!(object["paceStatus"]["windowKey"], "main.weekly.v1");
        assert_eq!(object["paceStatus"]["durationSeconds"], 604_800);
        assert_eq!(object["paceStatus"]["durationSource"], "provider");
        assert_eq!(object["windowMinutes"], 10_080);
        assert!(!object.contains_key("historicalExpectedPercent"));
        assert!(!object.contains_key("runOutProbability"));
        assert!(!object.contains_key("historicalPace"));
    }

    #[test]
    fn maps_codex_additional_model_limits() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let extra = CodexAdditionalRateLimit {
            limit_name: Some("gpt-5.2-codex-spark".to_string()),
            metered_feature: None,
            rate_limit: Some(CodexRateLimit {
                primary_window: Some(CodexWindow {
                    used_percent: 41.0,
                    reset_at: 1_700_003_600,
                    limit_window_seconds: 18_000,
                }),
                secondary_window: None,
            }),
        };
        let windows = codex_windows(None, Some(&[extra]), now);
        assert_eq!(windows.len(), 1);
        assert_eq!(windows[0].label, "Codex Spark");
        assert_eq!(
            windows[0].card_id_for_test(),
            format!(
                "additional.{}.primary.v1",
                sha256_hex("gpt-5.2-codex-spark")
            )
        );
        assert_eq!(windows[0].remaining_percent, 59.0);
        assert_eq!(windows[0].pace_status.state, PaceState::LearningHistory);
        assert_eq!(windows[0].pace_status.duration_seconds, Some(18_000));
        assert_eq!(
            windows[0].pace_status.duration_source,
            Some(DurationSource::Provider)
        );
    }

    #[test]
    fn invalid_codex_duration_or_reset_evidence_fails_closed() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        for (case, reset_at, duration_seconds) in [
            ("duration", now.timestamp() + 3_600, 0),
            ("reset", 0, 3_600),
        ] {
            let window = map_window_with_identity(
                "Additional",
                CodexWindow {
                    used_percent: 25.0,
                    reset_at,
                    limit_window_seconds: duration_seconds,
                },
                now,
                "additional.stable.primary.v1",
                Some("additional.stable.primary.v1".to_string()),
            )
            .unwrap();
            assert_eq!(window.pace_status.state, PaceState::Unavailable, "{case}");
            assert_eq!(
                window.pace_status.reason.as_deref(),
                Some("invalidEvidence"),
                "{case}"
            );
            assert!(window.pace_status.duration_seconds.is_none(), "{case}");
            assert!(window.pace_status.duration_source.is_none(), "{case}");
            assert!(
                serde_json::to_value(window)
                    .unwrap()
                    .get("windowMinutes")
                    .is_none(),
                "{case}"
            );
        }

        let unknown = map_window_with_identity(
            "Unknown",
            CodexWindow {
                used_percent: 25.0,
                reset_at: 0,
                limit_window_seconds: 0,
            },
            now,
            "row.additional.unknown.primary.v1",
            None,
        )
        .unwrap();
        assert_eq!(unknown.pace_reason_for_test(), Some("windowIdentity"));

        let reset = now + chrono::Duration::hours(1);
        let mismatched =
            UsageWindow::from_used_percent("Additional".to_string(), 25.0, Some(reset), now)
                .with_identity(
                    "additional.stable.primary.v1",
                    Some("additional.stable.primary.v1".to_string()),
                )
                .with_duration_evidence(
                    now,
                    true,
                    Some(DurationEvidence::provider(reset.timestamp() + 1, 3_600)),
                    None,
                );
        assert_eq!(
            mismatched.pace_status.reason.as_deref(),
            Some("invalidEvidence")
        );
    }

    #[test]
    fn parses_claude_credentials_file() {
        let raw = r#"{
            "claudeAiOauth": {
                "accessToken": "access",
                "refreshToken": "refresh",
                "expiresAt": 1700000000000,
                "scopes": ["user:profile"],
                "rateLimitTier": "max",
                "subscriptionType": "pro"
            }
        }"#;
        let credentials = parse_claude_credentials_data(raw, ClaudeCredentialSource::File).unwrap();
        assert_eq!(credentials.access_token, "access");
        assert_eq!(credentials.refresh_token.as_deref(), Some("refresh"));
        assert_eq!(credentials.scopes, vec!["user:profile"]);
        assert_eq!(credentials.subscription_type.as_deref(), Some("pro"));
    }

    #[test]
    fn merge_claude_credentials_rotates_tokens_and_preserves_other_fields() {
        let raw = r#"{
            "claudeAiOauth": {
                "accessToken": "old-access",
                "refreshToken": "old-refresh",
                "expiresAt": 1700000000000,
                "scopes": ["user:profile"],
                "subscriptionType": "pro"
            }
        }"#;
        let mut credentials =
            parse_claude_credentials_data(raw, ClaudeCredentialSource::File).unwrap();
        credentials.access_token = "new-access".to_string();
        credentials.refresh_token = Some("new-refresh".to_string());
        credentials.expires_at = Utc.timestamp_millis_opt(1_700_009_999_000).single();

        let merged = merge_claude_credentials_json(&credentials).unwrap();
        let reparsed =
            parse_claude_credentials_data(&merged, ClaudeCredentialSource::File).unwrap();
        assert_eq!(reparsed.access_token, "new-access");
        assert_eq!(reparsed.refresh_token.as_deref(), Some("new-refresh"));
        assert_eq!(
            reparsed.expires_at,
            Utc.timestamp_millis_opt(1_700_009_999_000).single()
        );
        // Untouched fields the Claude CLI wrote survive the merge.
        assert_eq!(reparsed.subscription_type.as_deref(), Some("pro"));
        assert_eq!(reparsed.scopes, vec!["user:profile"]);
    }

    #[test]
    fn atomic_write_replaces_existing_file_contents() {
        let dir = std::env::temp_dir().join(format!("tb_atomic_{}", std::process::id()));
        fs::create_dir_all(&dir).unwrap();
        let path = dir.join(".credentials.json");
        fs::write(&path, "old").unwrap();

        atomic_write(&path, "new").unwrap();
        assert_eq!(fs::read_to_string(&path).unwrap(), "new");
        // No temp turds left in the directory.
        let leftovers: Vec<_> = fs::read_dir(&dir)
            .unwrap()
            .filter_map(|e| e.ok())
            .filter(|e| e.file_name().to_string_lossy().contains(".tmp."))
            .collect();
        assert!(leftovers.is_empty(), "temp file not cleaned up");

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn maps_claude_oauth_windows() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let usage = ClaudeUsageResponse {
            five_hour: Some(ClaudeWindow {
                utilization: Some(8.0),
                resets_at: Some("2023-11-14T23:13:20Z".to_string()),
            }),
            seven_day: Some(ClaudeWindow {
                utilization: Some(23.0),
                resets_at: Some("2023-11-17T22:13:20Z".to_string()),
            }),
            seven_day_oauth_apps: None,
            seven_day_opus: None,
            seven_day_sonnet: Some(ClaudeWindow {
                utilization: Some(3.0),
                resets_at: None,
            }),
            seven_day_design: Some(ClaudeWindow {
                utilization: Some(0.0),
                resets_at: None,
            }),
            seven_day_routines: None,
            extra_usage: None,
            ..Default::default()
        };
        let windows = claude_windows(&usage, now);
        assert_eq!(windows.len(), 4);
        assert_eq!(windows[0].label, "Session");
        assert_eq!(windows[0].card_id_for_test(), "session.v1");
        assert_eq!(windows[0].pace_window_key_for_test(), Some("session.v1"));
        assert_eq!(windows[0].remaining_percent, 92.0);
        assert_eq!(windows[0].pace_status.state, PaceState::LearningHistory);
        assert_eq!(windows[0].pace_status.duration_seconds, Some(18_000));
        assert_eq!(
            windows[0].pace_status.duration_source,
            Some(DurationSource::Contract)
        );
        assert_eq!(windows[1].label, "Weekly");
        assert_eq!(windows[1].card_id_for_test(), "weekly.v1");
        assert_eq!(windows[1].pace_window_key_for_test(), Some("weekly.v1"));
        assert_eq!(windows[1].remaining_percent, 77.0);
        assert_eq!(windows[1].pace_status.state, PaceState::LearningHistory);
        assert_eq!(windows[1].pace_status.duration_seconds, Some(604_800));
        assert_eq!(
            windows[1].pace_status.duration_source,
            Some(DurationSource::Contract)
        );
        assert_eq!(windows[2].label, "Sonnet");
        assert_eq!(windows[2].card_id_for_test(), "sonnet.weekly.v1");
        assert_eq!(windows[2].pace_reason_for_test(), Some("missingReset"));
        assert_eq!(windows[2].remaining_percent, 97.0);
        assert_eq!(windows[3].label, "Designs");
        assert_eq!(windows[3].card_id_for_test(), "design.weekly.v1");
        assert_eq!(windows[3].remaining_percent, 100.0);
    }

    #[test]
    fn claude_body_and_aliases_reject_invalid_utilization() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let reset = (now + chrono::Duration::hours(1)).to_rfc3339_opts(SecondsFormat::Secs, true);
        for invalid in [-1.0, 150.0, f64::NAN, f64::INFINITY] {
            assert!(map_claude_window(
                "Session",
                "session.v1",
                DurationEvidence::contract(18_000),
                &ClaudeWindow {
                    utilization: Some(invalid),
                    resets_at: Some(reset.clone()),
                },
                now,
            )
            .is_none());
        }

        let usage = ClaudeUsageResponse {
            five_hour: Some(ClaudeWindow {
                utilization: Some(150.0),
                resets_at: Some(reset.clone()),
            }),
            seven_day: Some(ClaudeWindow {
                utilization: Some(20.0),
                resets_at: Some(reset.clone()),
            }),
            seven_day_design: Some(ClaudeWindow {
                utilization: Some(f64::NAN),
                resets_at: Some(reset.clone()),
            }),
            design: Some(ClaudeWindow {
                utilization: Some(30.0),
                resets_at: Some(reset.clone()),
            }),
            seven_day_routines: Some(ClaudeWindow {
                utilization: Some(150.0),
                resets_at: Some(reset.clone()),
            }),
            routines: Some(ClaudeWindow {
                utilization: Some(40.0),
                resets_at: Some(reset),
            }),
            ..Default::default()
        };
        let windows = claude_windows(&usage, now);
        assert_eq!(
            windows
                .iter()
                .map(|window| (window.label.as_str(), window.used_percent))
                .collect::<Vec<_>>(),
            vec![
                ("Weekly", 20.0),
                ("Designs", 30.0),
                ("Daily Routines", 40.0)
            ]
        );
    }

    #[test]
    fn claude_extra_usage_is_explicitly_non_recurring() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let window = claude_extra_usage_window(
            Some(&ClaudeExtraUsage {
                is_enabled: true,
                monthly_limit: Some(10_000.0),
                used_credits: Some(2_500.0),
                utilization: None,
                currency: Some("USD".to_string()),
            }),
            now,
        )
        .unwrap();
        assert_eq!(window.card_id, "extra_usage.v1");
        assert_eq!(window.used_percent, 25.0);
        assert_eq!(
            window.reset_text.as_deref(),
            Some("Monthly cap: $25.00 / $100.00")
        );
        assert_eq!(window.pace_status.state, PaceState::Unavailable);
        assert_eq!(window.pace_reason_for_test(), Some("nonRecurring"));
        let wire = serde_json::to_value(window).unwrap();
        assert_eq!(wire["paceStatus"]["reason"], "nonRecurring");
        assert!(wire.get("windowMinutes").is_none());
    }

    #[test]
    fn invalid_claude_extra_usage_does_not_poison_valid_windows() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let usage = ClaudeUsageResponse {
            five_hour: Some(ClaudeWindow {
                utilization: Some(20.0),
                resets_at: Some(
                    (now + chrono::Duration::hours(1)).to_rfc3339_opts(SecondsFormat::Secs, true),
                ),
            }),
            extra_usage: Some(ClaudeExtraUsage {
                is_enabled: true,
                monthly_limit: Some(10_000.0),
                used_credits: Some(2_500.0),
                utilization: Some(f64::NAN),
                currency: Some("USD".to_string()),
            }),
            ..Default::default()
        };

        let windows = claude_windows(&usage, now);
        assert_eq!(windows.len(), 1);
        assert_eq!(windows[0].card_id, "session.v1");
        assert!(serde_json::to_value(windows).is_ok());
    }

    #[test]
    fn decodes_claude_alias_windows_without_duplicate_error() {
        let raw = r#"{
            "five_hour": { "utilization": 5, "resets_at": "2026-05-28T14:00:00Z" },
            "seven_day": { "utilization": 23, "resets_at": "2026-05-31T14:00:00Z" },
            "seven_day_sonnet": { "utilization": 3, "resets_at": null },
            "seven_day_omelette": { "utilization": 0, "resets_at": null },
            "omelette_promotional": { "utilization": 0, "resets_at": null },
            "seven_day_cowork": { "utilization": 0, "resets_at": null }
        }"#;
        let usage: ClaudeUsageResponse = serde_json::from_str(raw).unwrap();
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let windows = claude_windows(&usage, now);
        assert_eq!(
            windows.iter().map(|w| w.label.as_str()).collect::<Vec<_>>(),
            vec!["Session", "Weekly", "Sonnet", "Designs", "Daily Routines"]
        );
    }

    #[test]
    fn claude_named_and_alias_windows_use_exact_contract_durations() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let reset = (now + chrono::Duration::hours(1)).to_rfc3339_opts(SecondsFormat::Secs, true);
        let window = |utilization| ClaudeWindow {
            utilization: Some(utilization),
            resets_at: Some(reset.clone()),
        };
        let usage = ClaudeUsageResponse {
            five_hour: Some(window(5.0)),
            seven_day: Some(window(10.0)),
            seven_day_oauth_apps: Some(window(15.0)),
            seven_day_sonnet: Some(window(20.0)),
            seven_day_opus: Some(window(25.0)),
            seven_day_omelette: Some(window(30.0)),
            seven_day_cowork: Some(window(35.0)),
            ..Default::default()
        };
        let windows = claude_windows(&usage, now);
        let expected = [
            ("session.v1", 18_000),
            ("weekly.v1", 604_800),
            ("oauth_apps.weekly.v1", 604_800),
            ("sonnet.weekly.v1", 604_800),
            ("opus.weekly.v1", 604_800),
            ("design.weekly.v1", 604_800),
            ("routines.weekly.v1", 604_800),
        ];
        assert_eq!(windows.len(), expected.len());
        for (window, (key, duration)) in windows.iter().zip(expected) {
            assert_eq!(window.card_id, key);
            assert_eq!(window.pace_status.state, PaceState::LearningHistory);
            assert_eq!(window.pace_status.duration_seconds, Some(duration));
            assert_eq!(
                window.pace_status.duration_source,
                Some(DurationSource::Contract)
            );
        }

        for (alias, label, key) in [
            ("seven_day_design", "Designs", "design.weekly.v1"),
            ("seven_day_claude_design", "Designs", "design.weekly.v1"),
            ("claude_design", "Designs", "design.weekly.v1"),
            ("design", "Designs", "design.weekly.v1"),
            ("seven_day_omelette", "Designs", "design.weekly.v1"),
            ("omelette", "Designs", "design.weekly.v1"),
            ("omelette_promotional", "Designs", "design.weekly.v1"),
            ("seven_day_routines", "Daily Routines", "routines.weekly.v1"),
            (
                "seven_day_claude_routines",
                "Daily Routines",
                "routines.weekly.v1",
            ),
            ("claude_routines", "Daily Routines", "routines.weekly.v1"),
            ("routines", "Daily Routines", "routines.weekly.v1"),
            ("routine", "Daily Routines", "routines.weekly.v1"),
            ("seven_day_cowork", "Daily Routines", "routines.weekly.v1"),
            ("cowork", "Daily Routines", "routines.weekly.v1"),
        ] {
            let raw = format!(r#"{{"{alias}":{{"utilization":12,"resets_at":"{reset}"}}}}"#);
            let usage: ClaudeUsageResponse = serde_json::from_str(&raw).unwrap();
            let windows = claude_windows(&usage, now);
            assert_eq!(windows.len(), 1, "{alias}");
            assert_eq!(windows[0].label, label, "{alias}");
            assert_eq!(windows[0].card_id, key, "{alias}");
            assert_eq!(
                windows[0].pace_status.duration_seconds,
                Some(604_800),
                "{alias}"
            );
            assert_eq!(
                windows[0].pace_status.duration_source,
                Some(DurationSource::Contract),
                "{alias}"
            );
            assert_eq!(
                windows[0].pace_status.state,
                PaceState::LearningHistory,
                "{alias}"
            );
        }
    }

    fn header_map(pairs: &[(&'static str, &'static str)]) -> reqwest::header::HeaderMap {
        let mut headers = reqwest::header::HeaderMap::new();
        for (name, value) in pairs {
            headers.insert(
                reqwest::header::HeaderName::from_static(name),
                reqwest::header::HeaderValue::from_static(value),
            );
        }
        headers
    }

    #[test]
    fn parses_unified_ratelimit_headers() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let headers = header_map(&[
            ("anthropic-ratelimit-unified-5h-utilization", "0.11"),
            ("anthropic-ratelimit-unified-5h-reset", "1700003600"),
            ("anthropic-ratelimit-unified-7d-utilization", "0.6"),
            ("anthropic-ratelimit-unified-7d-reset", "1700172800"),
        ]);
        let windows = parse_unified_ratelimit_windows(&headers, now);
        assert_eq!(windows.len(), 2);
        assert_eq!(windows[0].label, "Session");
        assert_eq!(windows[0].card_id_for_test(), "session.v1");
        assert_eq!(windows[0].pace_window_key_for_test(), Some("session.v1"));
        assert!((windows[0].used_percent - 11.0).abs() < 1e-9);
        assert!((windows[0].remaining_percent - 89.0).abs() < 1e-9);
        assert!(windows[0].resets_at.is_some());
        assert!(windows[0].reset_text.is_some());
        assert_eq!(windows[0].pace_status.state, PaceState::LearningHistory);
        assert_eq!(windows[0].pace_status.duration_seconds, Some(18_000));
        assert_eq!(
            windows[0].pace_status.duration_source,
            Some(DurationSource::Contract)
        );
        assert_eq!(windows[1].label, "Weekly");
        assert_eq!(windows[1].card_id_for_test(), "weekly.v1");
        assert_eq!(windows[1].pace_window_key_for_test(), Some("weekly.v1"));
        assert!((windows[1].used_percent - 60.0).abs() < 1e-9);
        assert!((windows[1].remaining_percent - 40.0).abs() < 1e-9);
        assert_eq!(windows[1].pace_status.state, PaceState::LearningHistory);
        assert_eq!(windows[1].pace_status.duration_seconds, Some(604_800));
        assert_eq!(
            windows[1].pace_status.duration_source,
            Some(DurationSource::Contract)
        );
    }

    #[test]
    fn unified_reset_text_is_relative() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let reset = 1_700_000_000 + 3600; // now + 1h
        let window = unified_ratelimit_window("Session", Some(0.5), Some(reset), now).unwrap();
        assert!((window.used_percent - 50.0).abs() < 1e-9);
        assert!(window.reset_text.as_deref().unwrap().contains("1h"));
    }

    #[test]
    fn unified_windows_skip_missing_and_unparseable() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        // empty -> nothing
        assert!(parse_unified_ratelimit_windows(&header_map(&[]), now).is_empty());

        // only 5h -> just Session
        let windows = parse_unified_ratelimit_windows(
            &header_map(&[("anthropic-ratelimit-unified-5h-utilization", "0.2")]),
            now,
        );
        assert_eq!(windows.len(), 1);
        assert_eq!(windows[0].label, "Session");

        // unparseable 5h + valid 7d -> just Weekly
        let windows = parse_unified_ratelimit_windows(
            &header_map(&[
                ("anthropic-ratelimit-unified-5h-utilization", "abc"),
                ("anthropic-ratelimit-unified-7d-utilization", "0.4"),
            ]),
            now,
        );
        assert_eq!(windows.len(), 1);
        assert_eq!(windows[0].label, "Weekly");

        // utilization present, reset absent -> window with no reset fields
        let window = unified_ratelimit_window("Weekly", Some(0.4), None, now).unwrap();
        assert!(window.resets_at.is_none());
        assert!(window.reset_text.is_none());
        assert_eq!(window.pace_reason_for_test(), Some("missingReset"));

        let invalid_reset = parse_unified_ratelimit_windows(
            &header_map(&[
                ("anthropic-ratelimit-unified-5h-utilization", "0.2"),
                ("anthropic-ratelimit-unified-5h-reset", "bogus"),
            ]),
            now,
        );
        assert_eq!(invalid_reset.len(), 1);
        assert_eq!(
            invalid_reset[0].pace_reason_for_test(),
            Some("invalidEvidence")
        );
    }

    #[test]
    fn unified_window_accepts_boundaries_and_rejects_invalid_fraction() {
        let now = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let zero = unified_ratelimit_window("Session", Some(0.0), None, now).unwrap();
        assert!((zero.used_percent - 0.0).abs() < 1e-9);
        assert!((zero.remaining_percent - 100.0).abs() < 1e-9);
        let full = unified_ratelimit_window("Session", Some(1.0), None, now).unwrap();
        assert!((full.used_percent - 100.0).abs() < 1e-9);
        assert!((full.remaining_percent - 0.0).abs() < 1e-9);
        for invalid in [-0.1, 1.5, f64::NAN, f64::INFINITY] {
            assert!(unified_ratelimit_window("Session", Some(invalid), None, now).is_none());
        }
        assert!(parse_unified_ratelimit_windows(
            &header_map(&[
                ("anthropic-ratelimit-unified-5h-utilization", "1.5"),
                ("anthropic-ratelimit-unified-7d-utilization", "NaN"),
            ]),
            now,
        )
        .is_empty());
        assert!(unified_ratelimit_window("Session", None, Some(1_783_111_200), now).is_none());
    }

    #[test]
    fn reads_claude_code_oauth_token_via_lookup() {
        let token = claude_token_from_lookup(|key| match key {
            "CLAUDE_CODE_OAUTH_TOKEN" => Some("  sk-ant-oat01-test  ".to_string()),
            _ => None,
        });
        assert_eq!(token.as_deref(), Some("sk-ant-oat01-test"));
        assert!(claude_token_from_lookup(|_| None).is_none());
        assert!(claude_token_from_lookup(|_| Some("   ".to_string())).is_none());
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

    async fn codex_test_response(refresh_token: String) -> Result<Value, String> {
        assert_eq!(refresh_token, "codex-old-refresh");
        Ok(serde_json::json!({
            "access_token": "codex-new-access",
            "refresh_token": "codex-new-refresh"
        }))
    }

    fn setup_codex_refresh(
        tag: &str,
    ) -> (TestRefreshScope, PathBuf, AccountScope, Vec<u8>, String) {
        let scope = TestRefreshScope::new("codex", tag);
        let path = scope.root().join("codex/auth.json");
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(
            &path,
            serde_json::to_vec_pretty(&serde_json::json!({
                "tokens": {
                    "access_token": " codex-old-access ",
                    "refresh_token": " codex-old-refresh ",
                    "id_token": " codex-old-id "
                }
            }))
            .unwrap(),
        )
        .unwrap();
        let credentials = load_codex_credentials_from(&path).unwrap();
        let location = credentials.scope_slot.canonical_location.clone();
        let old_scope = scope
            .resolve_current(
                credentials.scope_slot.semantic_source,
                &location,
                credentials.scope_marker(),
            )
            .unwrap();
        let metadata = scope.metadata_bytes();
        (scope, path, old_scope, metadata, location)
    }

    async fn run_codex_refresh(
        scope: &TestRefreshScope,
        path: &Path,
        crash: Option<RefreshCheckpoint>,
    ) -> Result<(CodexCredentials, Result<AccountScope, AccountScopeError>), String> {
        refresh_codex_credentials_with(
            path,
            scope,
            codex_test_response,
            save_codex_credentials,
            checkpoint_at(crash),
        )
        .await
    }

    #[tokio::test]
    async fn codex_refresh_transfer_and_crash_boundaries_use_production_sequence() {
        for boundary in [
            RefreshCheckpoint::Reloaded,
            RefreshCheckpoint::NetworkReturned,
            RefreshCheckpoint::MetadataHandled,
            RefreshCheckpoint::CredentialsPersisted,
        ] {
            let (scope, path, old_scope, before, location) = setup_codex_refresh("codex-crash");
            assert_eq!(
                run_codex_refresh(&scope, &path, Some(boundary))
                    .await
                    .unwrap_err(),
                "injected crash"
            );
            let stored = load_codex_credentials_from(&path).unwrap();
            assert_eq!(
                stored.refresh_token.as_deref(),
                Some(if boundary == RefreshCheckpoint::CredentialsPersisted {
                    "codex-new-refresh"
                } else {
                    "codex-old-refresh"
                })
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
                        .resolve_current("codex-auth-json", &location, b"codex-old-refresh")
                        .unwrap(),
                    old_scope
                );
                assert_eq!(
                    scope
                        .resolve_current("codex-auth-json", &location, b"codex-new-refresh")
                        .unwrap(),
                    old_scope
                );
            }
            scope.cleanup();
        }

        let (scope, path, old_scope, before, location) = setup_codex_refresh("codex-metadata-fail");
        scope.fail_metadata_save();
        let (refreshed, scope_outcome) = run_codex_refresh(&scope, &path, None).await.unwrap();
        assert_eq!(refreshed.access_token, "codex-new-access");
        assert_eq!(scope_outcome, Err(AccountScopeError::MetadataWrite));
        assert_eq!(scope.metadata_bytes(), before);
        let persisted = load_codex_credentials_from(&path).unwrap();
        assert_eq!(persisted.access_token, "codex-old-access");
        assert_eq!(
            persisted.refresh_token.as_deref(),
            Some("codex-old-refresh")
        );
        assert_eq!(
            scope
                .resolve_current("codex-auth-json", &location, persisted.scope_marker())
                .unwrap(),
            old_scope
        );
        scope.cleanup();

        let (scope, path, _old_scope, before, _) =
            setup_codex_refresh("codex-metadata-fail-unchanged");
        scope.fail_metadata_save();
        let (refreshed, scope_outcome) = refresh_codex_credentials_with(
            &path,
            &scope,
            |refresh_token| async move {
                assert_eq!(refresh_token, "codex-old-refresh");
                Ok(serde_json::json!({ "access_token": "codex-new-access" }))
            },
            save_codex_credentials,
            checkpoint_at(None),
        )
        .await
        .unwrap();
        assert_eq!(scope_outcome, Err(AccountScopeError::MetadataWrite));
        assert_eq!(scope.metadata_bytes(), before);
        assert_eq!(
            refreshed.refresh_token.as_deref(),
            Some("codex-old-refresh")
        );
        let persisted = load_codex_credentials_from(&path).unwrap();
        assert_eq!(persisted.access_token, "codex-new-access");
        assert_eq!(
            persisted.refresh_token.as_deref(),
            Some("codex-old-refresh")
        );
        scope.cleanup();

        let (scope, path, old_scope, _, location) = setup_codex_refresh("codex-success");
        let (_, scope_outcome) = run_codex_refresh(&scope, &path, None).await.unwrap();
        assert_eq!(scope_outcome.unwrap(), old_scope);
        assert_eq!(
            scope
                .resolve_current("codex-auth-json", &location, b"codex-new-refresh")
                .unwrap(),
            old_scope
        );
        assert_eq!(
            load_codex_credentials_from(&path)
                .unwrap()
                .refresh_token
                .as_deref(),
            Some("codex-new-refresh")
        );
        scope.cleanup();
    }

    async fn claude_test_response(refresh_token: String) -> Result<ClaudeRefreshResponse, String> {
        assert_eq!(refresh_token, "claude-old-refresh");
        Ok(ClaudeRefreshResponse {
            access_token: "claude-new-access".to_string(),
            refresh_token: Some("claude-new-refresh".to_string()),
            expires_in: 3_600,
        })
    }

    fn setup_claude_refresh(
        tag: &str,
    ) -> (
        TestRefreshScope,
        PathBuf,
        ClaudeCredentials,
        AccountScope,
        Vec<u8>,
        String,
    ) {
        let scope = TestRefreshScope::new("claude", tag);
        let path = scope.root().join("claude/.credentials.json");
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        let raw = serde_json::json!({
            "claudeAiOauth": {
                "accessToken": "claude-old-access",
                "refreshToken": "claude-old-refresh",
                "expiresAt": 0
            }
        })
        .to_string();
        fs::write(&path, &raw).unwrap();
        let mut credentials =
            parse_claude_credentials_data(&raw, ClaudeCredentialSource::File).unwrap();
        credentials.scope_slot = CredentialSlot {
            semantic_source: "claude-login-file",
            canonical_location: agent_account_scope::canonical_file_location(
                &path,
                Some("claudeAiOauth"),
            )
            .unwrap(),
        };
        let location = credentials.scope_slot.canonical_location.clone();
        let old_scope = scope
            .resolve_current(
                credentials.scope_slot.semantic_source,
                &location,
                credentials.scope_marker().unwrap(),
            )
            .unwrap();
        let metadata = scope.metadata_bytes();
        (scope, path, credentials, old_scope, metadata, location)
    }

    async fn run_claude_refresh(
        scope: &TestRefreshScope,
        path: &Path,
        original: &ClaudeCredentials,
        crash: Option<RefreshCheckpoint>,
    ) -> Result<(ClaudeCredentials, Result<AccountScope, AccountScopeError>), String> {
        let reload_path = path.to_path_buf();
        let save_path = path.to_path_buf();
        refresh_claude_credentials_with(
            original,
            scope,
            move |template| {
                let raw = fs::read_to_string(&reload_path)
                    .map_err(|error| format!("reload Claude test credentials: {error}"))?;
                let mut credentials =
                    parse_claude_credentials_data(&raw, ClaudeCredentialSource::File)?;
                credentials.scope_slot = template.scope_slot.clone();
                Ok(credentials)
            },
            claude_test_response,
            move |credentials| save_claude_credentials_to_file(credentials, &save_path),
            checkpoint_at(crash),
        )
        .await
    }

    fn stored_claude_credentials(path: &Path) -> ClaudeCredentials {
        parse_claude_credentials_data(
            &fs::read_to_string(path).unwrap(),
            ClaudeCredentialSource::File,
        )
        .unwrap()
    }

    #[tokio::test]
    async fn claude_refresh_invalid_new_marker_preserves_old_lineage_and_store() {
        for (tag, refresh_value) in [
            ("empty", serde_json::json!("")),
            ("non-string", serde_json::json!({ "unexpected": true })),
        ] {
            let (scope, path, original, old_scope, _, location) =
                setup_claude_refresh(&format!("claude-invalid-refresh-{tag}"));
            let response: ClaudeRefreshResponse = serde_json::from_value(serde_json::json!({
                "access_token": "claude-new-access",
                "refresh_token": refresh_value,
                "expires_in": 3600
            }))
            .unwrap();
            let reload_path = path.clone();
            let save_path = path.clone();
            let (refreshed, scope_outcome) = refresh_claude_credentials_with(
                &original,
                &scope,
                move |template| {
                    let raw = fs::read_to_string(&reload_path)
                        .map_err(|error| format!("reload Claude test credentials: {error}"))?;
                    let mut credentials =
                        parse_claude_credentials_data(&raw, ClaudeCredentialSource::File)?;
                    credentials.scope_slot = template.scope_slot.clone();
                    Ok(credentials)
                },
                move |refresh_token| async move {
                    assert_eq!(refresh_token, "claude-old-refresh");
                    Ok(response)
                },
                move |credentials| save_claude_credentials_to_file(credentials, &save_path),
                checkpoint_at(None),
            )
            .await
            .unwrap();

            assert_eq!(refreshed.access_token, "claude-new-access", "{tag}");
            assert_eq!(
                refreshed.refresh_token.as_deref(),
                Some("claude-old-refresh"),
                "{tag}"
            );
            assert_eq!(scope_outcome.unwrap(), old_scope, "{tag}");
            assert_eq!(
                scope
                    .resolve_current("claude-login-file", &location, b"claude-old-refresh")
                    .unwrap(),
                old_scope,
                "{tag}"
            );
            let stored = stored_claude_credentials(&path);
            assert_eq!(stored.access_token, "claude-new-access", "{tag}");
            assert_eq!(
                stored.refresh_token.as_deref(),
                Some("claude-old-refresh"),
                "{tag}"
            );
            scope.cleanup();
        }
    }

    #[tokio::test]
    async fn claude_refresh_transfer_and_crash_boundaries_use_production_sequence() {
        for boundary in [
            RefreshCheckpoint::Reloaded,
            RefreshCheckpoint::NetworkReturned,
            RefreshCheckpoint::MetadataHandled,
            RefreshCheckpoint::CredentialsPersisted,
        ] {
            let (scope, path, original, old_scope, before, location) =
                setup_claude_refresh("claude-crash");
            assert_eq!(
                run_claude_refresh(&scope, &path, &original, Some(boundary))
                    .await
                    .unwrap_err(),
                "injected crash"
            );
            assert_eq!(
                stored_claude_credentials(&path).refresh_token.as_deref(),
                Some(if boundary == RefreshCheckpoint::CredentialsPersisted {
                    "claude-new-refresh"
                } else {
                    "claude-old-refresh"
                })
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
                        .resolve_current("claude-login-file", &location, b"claude-old-refresh")
                        .unwrap(),
                    old_scope
                );
                assert_eq!(
                    scope
                        .resolve_current("claude-login-file", &location, b"claude-new-refresh")
                        .unwrap(),
                    old_scope
                );
            }
            scope.cleanup();
        }

        let (scope, path, original, old_scope, before, location) =
            setup_claude_refresh("claude-metadata-fail");
        scope.fail_metadata_save();
        let (refreshed, scope_outcome) = run_claude_refresh(&scope, &path, &original, None)
            .await
            .unwrap();
        assert_eq!(refreshed.access_token, "claude-new-access");
        assert_eq!(scope_outcome, Err(AccountScopeError::MetadataWrite));
        assert_eq!(scope.metadata_bytes(), before);
        assert_eq!(
            stored_claude_credentials(&path).refresh_token.as_deref(),
            Some("claude-old-refresh")
        );
        assert_eq!(
            scope
                .resolve_current("claude-login-file", &location, b"claude-old-refresh")
                .unwrap(),
            old_scope
        );
        scope.cleanup();

        let (scope, path, original, _old_scope, before, _) =
            setup_claude_refresh("claude-metadata-fail-unchanged");
        scope.fail_metadata_save();
        let reload_path = path.clone();
        let save_path = path.clone();
        let (refreshed, scope_outcome) = refresh_claude_credentials_with(
            &original,
            &scope,
            move |template| {
                let raw = fs::read_to_string(&reload_path)
                    .map_err(|error| format!("reload Claude test credentials: {error}"))?;
                let mut credentials =
                    parse_claude_credentials_data(&raw, ClaudeCredentialSource::File)?;
                credentials.scope_slot = template.scope_slot.clone();
                Ok(credentials)
            },
            |refresh_token| async move {
                assert_eq!(refresh_token, "claude-old-refresh");
                Ok(ClaudeRefreshResponse {
                    access_token: "claude-new-access".to_string(),
                    refresh_token: None,
                    expires_in: 3_600,
                })
            },
            move |credentials| save_claude_credentials_to_file(credentials, &save_path),
            checkpoint_at(None),
        )
        .await
        .unwrap();
        assert_eq!(scope_outcome, Err(AccountScopeError::MetadataWrite));
        assert_eq!(scope.metadata_bytes(), before);
        assert_eq!(
            refreshed.refresh_token.as_deref(),
            Some("claude-old-refresh")
        );
        let persisted = stored_claude_credentials(&path);
        assert_eq!(persisted.access_token, "claude-new-access");
        assert_eq!(
            persisted.refresh_token.as_deref(),
            Some("claude-old-refresh")
        );
        scope.cleanup();

        let (scope, path, original, old_scope, _, location) =
            setup_claude_refresh("claude-success");
        let (_, scope_outcome) = run_claude_refresh(&scope, &path, &original, None)
            .await
            .unwrap();
        assert_eq!(scope_outcome.unwrap(), old_scope);
        assert_eq!(
            scope
                .resolve_current("claude-login-file", &location, b"claude-new-refresh")
                .unwrap(),
            old_scope
        );
        assert_eq!(
            stored_claude_credentials(&path).refresh_token.as_deref(),
            Some("claude-new-refresh")
        );
        scope.cleanup();
    }

    #[test]
    fn refreshes_or_expires_cached_windows() {
        let base = Utc.timestamp_opt(1_700_000_000, 0).single().unwrap();
        let window =
            unified_ratelimit_window("Session", Some(0.2), Some(1_700_000_000 + 3600), base)
                .unwrap();

        // 30 min later, still before the reset: reset_text recomputed to the
        // shorter countdown (not the frozen original).
        let later = base + chrono::Duration::seconds(1800);
        let refreshed = refresh_cached_windows(std::slice::from_ref(&window), later).unwrap();
        assert_eq!(refreshed.len(), 1);
        assert!(refreshed[0].reset_text.as_deref().unwrap().contains("30m"));

        // Past the reset: stale -> expire (None) so the caller re-probes.
        let after = base + chrono::Duration::seconds(3700);
        assert!(refresh_cached_windows(std::slice::from_ref(&window), after).is_none());
    }
}
