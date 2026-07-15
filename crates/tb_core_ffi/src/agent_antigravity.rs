//! Antigravity (Google Code Assist) usage/quota — ported from codexbar's
//! Antigravity provider. Antigravity 2.0 (the IDE and the `antigravity-cli`
//! that replaced `gemini-cli`) does NOT persist per-message token counts
//! locally, so a contribution-graph style breakdown isn't derivable; the quota
//! API is the only usable signal. We mirror codexbar's `auto` source:
//!
//! 1. **Local IDE API (`cli`)** — when Antigravity is running, find its
//!    `language_server` process (carrying a `--csrf_token`), discover its
//!    listening port via `lsof`, and call the local Connect-RPC `GetUserStatus`
//!    over loopback TLS. Live, no token refresh, no disk writes.
//! 2. **OAuth remote (`oauth`)** — otherwise read the shared Google creds at
//!    `~/.gemini/oauth_creds.json`, refresh against Google (client id/secret
//!    scanned from the installed Antigravity.app binary), and hit the
//!    `cloudcode-pa.googleapis.com` Code Assist quota endpoints.
//!
//! Both yield per-model "remaining fraction + reset" which map to `UsageWindow`s.

use crate::agent_usage::{clean_plan, parse_datetime, percent_encode, AgentIdentity, UsageWindow};
use chrono::{DateTime, Utc};
use serde::Deserialize;
use serde_json::{json, Value};
use std::collections::BTreeSet;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::OnceLock;

const LANG_SERVICE: &str = "/exa.language_server_pb.LanguageServerService/GetUserStatus";
const CODE_ASSIST_BASE: &str = "https://cloudcode-pa.googleapis.com/v1internal";
const GOOGLE_TOKEN_URL: &str = "https://oauth2.googleapis.com/token";
const REFRESH_SAFETY_SECS: i64 = 60;

pub(crate) struct Fetched {
    pub source: String,
    pub identity: Option<AgentIdentity>,
    pub windows: Vec<UsageWindow>,
}

/// Auto: prefer the live Local IDE API; fall back to the OAuth remote API.
pub(crate) async fn fetch(now: DateTime<Utc>) -> Result<Fetched, String> {
    match fetch_local_ide(now).await {
        Ok(local) if !local.windows.is_empty() => Ok(local),
        local_result => {
            let local_err = local_result.err();
            match fetch_oauth_remote(now).await {
                Ok(remote) => Ok(remote),
                Err(remote_err) => Err(local_err
                    .map(|le| format!("{remote_err} (local IDE: {le})"))
                    .unwrap_or(remote_err)),
            }
        }
    }
}

// ── Local IDE API ───────────────────────────────────────────────────────────

struct ProcInfo {
    pid: i32,
    csrf_token: String,
    extension_port: Option<u16>,
    extension_csrf: Option<String>,
}

async fn fetch_local_ide(now: DateTime<Utc>) -> Result<Fetched, String> {
    let proc = detect_process()?;
    let ports = listening_ports(proc.pid)?;

    let client = reqwest::Client::builder()
        .danger_accept_invalid_certs(true) // loopback language_server uses a self-signed cert
        .timeout(std::time::Duration::from_secs(8))
        .build()
        .map_err(|e| format!("build Antigravity local client: {e}"))?;
    let body = json!({
        "metadata": {
            "ideName": "antigravity",
            "extensionName": "antigravity",
            "ideVersion": "unknown",
            "locale": "en",
        }
    });

    // language-server ports use the language-server CSRF; the extension server
    // (if advertised) carries its own token.
    let mut candidates: Vec<(u16, String)> =
        ports.iter().map(|p| (*p, proc.csrf_token.clone())).collect();
    if let Some(port) = proc.extension_port {
        if let Some(csrf) = proc.extension_csrf.as_ref() {
            candidates.push((port, csrf.clone()));
        }
        candidates.push((port, proc.csrf_token.clone()));
    }

    let mut last_err = "Antigravity local IDE API not reachable".to_string();
    for (port, csrf) in candidates {
        let url = format!("https://127.0.0.1:{port}{LANG_SERVICE}");
        let resp = client
            .post(&url)
            .header("X-Codeium-Csrf-Token", &csrf)
            .header("Connect-Protocol-Version", "1")
            .header(reqwest::header::CONTENT_TYPE, "application/json")
            .json(&body)
            .send()
            .await;
        let resp = match resp {
            Ok(r) => r,
            Err(e) => {
                last_err = format!("local request failed: {e}");
                continue;
            }
        };
        if !resp.status().is_success() {
            last_err = format!("local API returned {}", resp.status().as_u16());
            continue;
        }
        let Ok(text) = resp.text().await else {
            continue;
        };
        match parse_user_status(&text, now) {
            Ok(fetched) if !fetched.windows.is_empty() || fetched.identity.is_some() => {
                return Ok(fetched)
            }
            Ok(_) => last_err = "local API returned no model quotas".to_string(),
            Err(e) => last_err = e,
        }
    }
    Err(last_err)
}

fn detect_process() -> Result<ProcInfo, String> {
    let output = Command::new("/bin/ps")
        .args(["-ax", "-o", "pid=,command="])
        .output()
        .map_err(|e| format!("run ps: {e}"))?;
    let stdout = String::from_utf8_lossy(&output.stdout);

    let mut saw_antigravity = false;
    for line in stdout.lines() {
        let line = line.trim_start();
        let Some((pid_str, cmd)) = line.split_once(' ') else {
            continue;
        };
        let Ok(pid) = pid_str.trim().parse::<i32>() else {
            continue;
        };
        let lower = cmd.to_lowercase();
        if !is_language_server(&lower) || !is_antigravity(&lower) {
            continue;
        }
        saw_antigravity = true;
        let Some(csrf) = extract_flag(cmd, "--csrf_token") else {
            continue;
        };
        return Ok(ProcInfo {
            pid,
            csrf_token: csrf,
            extension_port: extract_flag(cmd, "--extension_server_port").and_then(|s| s.parse().ok()),
            extension_csrf: extract_flag(cmd, "--extension_server_csrf_token"),
        });
    }
    if saw_antigravity {
        Err("Antigravity is running but no CSRF token was found".to_string())
    } else {
        Err("Antigravity is not running".to_string())
    }
}

fn is_language_server(lower_cmd: &str) -> bool {
    lower_cmd.contains("language_server")
}

fn is_antigravity(lower_cmd: &str) -> bool {
    (lower_cmd.contains("--app_data_dir") && lower_cmd.contains("antigravity"))
        || lower_cmd.contains("/antigravity/")
}

/// Value of `flag` in a command line, accepting either `flag value` or `flag=value`.
fn extract_flag(cmd: &str, flag: &str) -> Option<String> {
    let idx = cmd.find(flag)?;
    let rest = &cmd[idx + flag.len()..];
    let rest = rest.trim_start_matches(['=', ' ']);
    let value: String = rest.chars().take_while(|c| !c.is_whitespace()).collect();
    (!value.is_empty()).then_some(value)
}

fn listening_ports(pid: i32) -> Result<Vec<u16>, String> {
    let lsof = ["/usr/sbin/lsof", "/usr/bin/lsof"]
        .into_iter()
        .find(|p| Path::new(p).exists())
        .ok_or_else(|| "lsof not available".to_string())?;
    let output = Command::new(lsof)
        .args(["-nP", "-iTCP", "-sTCP:LISTEN", "-a", "-p", &pid.to_string()])
        .output()
        .map_err(|e| format!("run lsof: {e}"))?;
    let stdout = String::from_utf8_lossy(&output.stdout);
    let ports: BTreeSet<u16> = stdout.lines().filter_map(parse_listen_port).collect();
    if ports.is_empty() {
        Err("no listening ports for Antigravity".to_string())
    } else {
        Ok(ports.into_iter().collect())
    }
}

/// Pull the port out of an `lsof` LISTEN line, e.g. `... TCP 127.0.0.1:54321 (LISTEN)`.
fn parse_listen_port(line: &str) -> Option<u16> {
    let idx = line.find("(LISTEN)")?;
    let before = line[..idx].trim_end();
    let colon = before.rfind(':')?;
    before[colon + 1..].trim().parse().ok()
}

#[derive(Debug, Deserialize)]
struct UserStatusResponse {
    #[serde(rename = "userStatus")]
    user_status: Option<UserStatus>,
}

#[derive(Debug, Deserialize)]
struct UserStatus {
    email: Option<String>,
    #[serde(rename = "planStatus")]
    plan_status: Option<PlanStatus>,
    #[serde(rename = "cascadeModelConfigData")]
    cascade_model_config_data: Option<ModelConfigData>,
    #[serde(rename = "userTier")]
    user_tier: Option<NamedTier>,
}

#[derive(Debug, Deserialize)]
struct PlanStatus {
    #[serde(rename = "planInfo")]
    plan_info: Option<LocalPlanInfo>,
}

#[derive(Debug, Deserialize)]
struct LocalPlanInfo {
    #[serde(rename = "planDisplayName")]
    plan_display_name: Option<String>,
    #[serde(rename = "displayName")]
    display_name: Option<String>,
    #[serde(rename = "productName")]
    product_name: Option<String>,
    #[serde(rename = "planName")]
    plan_name: Option<String>,
}

#[derive(Debug, Deserialize)]
struct NamedTier {
    name: Option<String>,
}

#[derive(Debug, Deserialize)]
struct ModelConfigData {
    #[serde(rename = "clientModelConfigs")]
    client_model_configs: Option<Vec<ModelConfig>>,
}

#[derive(Debug, Deserialize)]
struct ModelConfig {
    label: Option<String>,
    #[serde(rename = "modelOrAlias")]
    model_or_alias: Option<ModelAlias>,
    #[serde(rename = "quotaInfo")]
    quota_info: Option<QuotaInfo>,
}

#[derive(Debug, Deserialize)]
struct ModelAlias {
    model: Option<String>,
}

#[derive(Debug, Deserialize)]
struct QuotaInfo {
    #[serde(rename = "remainingFraction")]
    remaining_fraction: Option<f64>,
    #[serde(rename = "resetTime")]
    reset_time: Option<String>,
}

fn parse_user_status(body: &str, now: DateTime<Utc>) -> Result<Fetched, String> {
    let response: UserStatusResponse =
        serde_json::from_str(body).map_err(|e| format!("decode GetUserStatus: {e}"))?;
    let status = response
        .user_status
        .ok_or_else(|| "GetUserStatus missing userStatus".to_string())?;

    let configs = status
        .cascade_model_config_data
        .and_then(|d| d.client_model_configs)
        .unwrap_or_default();
    let windows: Vec<UsageWindow> = configs
        .into_iter()
        .filter_map(|config| {
            let quota = config.quota_info?;
            let fraction = quota.remaining_fraction?;
            let reset = quota.reset_time.as_deref().and_then(parse_datetime);
            let label = config
                .label
                .filter(|s| !s.trim().is_empty())
                .or_else(|| config.model_or_alias.and_then(|m| m.model))
                .unwrap_or_else(|| "Model".to_string());
            Some(UsageWindow::from_fraction(label, fraction, reset, now))
        })
        .collect();

    let plan = status
        .user_tier
        .and_then(|t| t.name)
        .filter(|s| !s.trim().is_empty())
        .or_else(|| status.plan_status.and_then(|p| p.plan_info).and_then(local_plan_name));

    Ok(Fetched {
        source: "cli".to_string(),
        identity: Some(AgentIdentity {
            email: status.email.filter(|s| !s.trim().is_empty()),
            plan,
        }),
        windows,
    })
}

fn local_plan_name(info: LocalPlanInfo) -> Option<String> {
    [
        info.plan_display_name,
        info.display_name,
        info.product_name,
        info.plan_name,
    ]
    .into_iter()
    .flatten()
    .map(|s| s.trim().to_string())
    .find(|s| !s.is_empty())
}

// ── OAuth remote (Google Code Assist) ─────────────────────────────────────────

async fn fetch_oauth_remote(now: DateTime<Utc>) -> Result<Fetched, String> {
    let creds_path = gemini_home()
        .map(|h| h.join("oauth_creds.json"))
        .ok_or_else(|| "Could not resolve ~/.gemini".to_string())?;
    let raw = std::fs::read_to_string(&creds_path)
        .map_err(|_| "Antigravity not logged in (no ~/.gemini/oauth_creds.json)".to_string())?;
    let mut creds: Value =
        serde_json::from_str(&raw).map_err(|e| format!("decode oauth_creds.json: {e}"))?;

    let mut access_token = creds
        .get("access_token")
        .and_then(Value::as_str)
        .map(str::to_string)
        .filter(|s| !s.is_empty())
        .ok_or_else(|| "Antigravity creds have no access token".to_string())?;

    let expiry_ms = creds.get("expiry_date").and_then(Value::as_f64);
    let now_ms = now.timestamp_millis() as f64;
    if expiry_ms.is_none_or(|exp| exp <= now_ms + (REFRESH_SAFETY_SECS * 1000) as f64) {
        let refresh_token = creds
            .get("refresh_token")
            .and_then(Value::as_str)
            .filter(|s| !s.is_empty())
            .ok_or_else(|| "Antigravity access token expired and no refresh token".to_string())?
            .to_string();
        access_token = refresh_access_token(&refresh_token, &mut creds, now, &creds_path).await?;
    }

    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(30))
        .build()
        .map_err(|e| format!("build Antigravity client: {e}"))?;

    let code_assist = code_assist_post(
        &client,
        "loadCodeAssist",
        &json!({
            "metadata": { "ideType": "ANTIGRAVITY", "platform": "PLATFORM_UNSPECIFIED", "pluginType": "GEMINI" }
        }),
        &access_token,
    )
    .await?;
    let project = project_id(&code_assist);
    let plan = resolve_remote_plan(&code_assist);

    let windows = fetch_model_quotas(&client, &access_token, project.as_deref(), now).await?;
    let email = gemini_active_email();

    Ok(Fetched {
        source: "oauth".to_string(),
        identity: Some(AgentIdentity { email, plan }),
        windows,
    })
}

async fn refresh_access_token(
    refresh_token: &str,
    creds: &mut Value,
    now: DateTime<Utc>,
    creds_path: &Path,
) -> Result<String, String> {
    let client = resolve_oauth_client()
        .ok_or_else(|| "Antigravity OAuth client not found. Install Antigravity.app or set ANTIGRAVITY_OAUTH_CLIENT_ID/SECRET.".to_string())?;
    let http = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(30))
        .build()
        .map_err(|e| format!("build refresh client: {e}"))?;
    let form = format!(
        "client_id={}&client_secret={}&refresh_token={}&grant_type=refresh_token",
        percent_encode(&client.0),
        percent_encode(&client.1),
        percent_encode(refresh_token),
    );
    let response = http
        .post(GOOGLE_TOKEN_URL)
        .header(
            reqwest::header::CONTENT_TYPE,
            "application/x-www-form-urlencoded",
        )
        .body(form)
        .send()
        .await
        .map_err(|e| format!("Antigravity token refresh failed: {e}"))?;
    if !response.status().is_success() {
        return Err("Antigravity token refresh rejected. Re-login in Antigravity.".to_string());
    }
    let json: Value = response
        .json()
        .await
        .map_err(|e| format!("decode refresh response: {e}"))?;
    let access_token = json
        .get("access_token")
        .and_then(Value::as_str)
        .ok_or_else(|| "refresh response missing access_token".to_string())?
        .to_string();

    // Persist back to ~/.gemini/oauth_creds.json so we share a single source of
    // truth with Antigravity. Preserve every original field; only touch the ones
    // the refresh changed. A write failure is non-fatal (use the token in-memory).
    if let Some(obj) = creds.as_object_mut() {
        obj.insert("access_token".into(), Value::String(access_token.clone()));
        if let Some(expires_in) = json.get("expires_in").and_then(Value::as_f64) {
            let expiry = now.timestamp_millis() as f64 + expires_in * 1000.0;
            obj.insert("expiry_date".into(), json!(expiry));
        }
        if let Some(id_token) = json.get("id_token").and_then(Value::as_str) {
            obj.insert("id_token".into(), Value::String(id_token.to_string()));
        }
    }
    let _ = write_creds_atomic(creds_path, creds);
    Ok(access_token)
}

fn write_creds_atomic(path: &Path, creds: &Value) -> std::io::Result<()> {
    let data = serde_json::to_vec_pretty(creds).unwrap_or_default();
    let tmp = path.with_extension("json.tmp");
    std::fs::write(&tmp, &data)?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        let _ = std::fs::set_permissions(&tmp, std::fs::Permissions::from_mode(0o600));
    }
    std::fs::rename(&tmp, path)
}

async fn code_assist_post(
    client: &reqwest::Client,
    method: &str,
    body: &Value,
    access_token: &str,
) -> Result<Value, String> {
    let resp = client
        .post(format!("{CODE_ASSIST_BASE}:{method}"))
        .bearer_auth(access_token)
        .header(reqwest::header::USER_AGENT, "antigravity")
        .header(reqwest::header::CONTENT_TYPE, "application/json")
        .json(body)
        .send()
        .await
        .map_err(|e| format!("Antigravity {method} request failed: {e}"))?;
    let status = resp.status();
    if status == reqwest::StatusCode::UNAUTHORIZED {
        return Err("Antigravity Google auth expired. Re-login in Antigravity.".to_string());
    }
    if status == reqwest::StatusCode::FORBIDDEN {
        return Err(format!("Antigravity {method} permission denied"));
    }
    if !status.is_success() {
        return Err(format!("Antigravity {method} returned {}", status.as_u16()));
    }
    resp.json()
        .await
        .map_err(|e| format!("decode Antigravity {method}: {e}"))
}

async fn fetch_model_quotas(
    client: &reqwest::Client,
    access_token: &str,
    project: Option<&str>,
    now: DateTime<Utc>,
) -> Result<Vec<UsageWindow>, String> {
    let body = match project {
        Some(p) => json!({ "project": p }),
        None => json!({}),
    };
    // Primary: fetchAvailableModels (per-model quotaInfo). Fall back to
    // retrieveUserQuota buckets if the catalog endpoint is denied.
    match code_assist_post(client, "fetchAvailableModels", &body, access_token).await {
        Ok(value) => {
            let windows = models_from_available(&value, now);
            if windows.is_empty() {
                let quota = code_assist_post(client, "retrieveUserQuota", &body, access_token).await?;
                Ok(buckets_from_quota(&quota, now))
            } else {
                Ok(windows)
            }
        }
        Err(_) => {
            let quota = code_assist_post(client, "retrieveUserQuota", &body, access_token).await?;
            Ok(buckets_from_quota(&quota, now))
        }
    }
}

fn project_id(code_assist: &Value) -> Option<String> {
    match code_assist.get("cloudaicompanionProject") {
        Some(Value::String(s)) if !s.trim().is_empty() => Some(s.trim().to_string()),
        Some(Value::Object(obj)) => obj
            .get("value")
            .or_else(|| obj.get("id"))
            .and_then(Value::as_str)
            .map(|s| s.trim().to_string())
            .filter(|s| !s.is_empty()),
        _ => None,
    }
}

fn resolve_remote_plan(code_assist: &Value) -> Option<String> {
    if let Some(plan_type) = code_assist
        .pointer("/planInfo/planType")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|s| !s.is_empty())
    {
        return Some(clean_plan(plan_type));
    }
    match code_assist
        .pointer("/currentTier/id")
        .and_then(Value::as_str)
        .map(str::trim)
    {
        Some("standard-tier") => Some("Paid".to_string()),
        Some("free-tier") => Some("Free".to_string()),
        Some("legacy-tier") => Some("Legacy".to_string()),
        _ => code_assist
            .pointer("/currentTier/name")
            .and_then(Value::as_str)
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .map(str::to_string),
    }
}

fn models_from_available(value: &Value, now: DateTime<Utc>) -> Vec<UsageWindow> {
    let Some(models) = value.get("models").and_then(Value::as_object) else {
        return Vec::new();
    };
    models
        .iter()
        .filter_map(|(id, model)| {
            let quota = model.get("quotaInfo")?;
            let fraction = quota.get("remainingFraction").and_then(Value::as_f64)?;
            let reset = quota
                .get("resetTime")
                .and_then(Value::as_str)
                .and_then(parse_datetime);
            let label = model
                .get("displayName")
                .and_then(Value::as_str)
                .filter(|s| !s.trim().is_empty())
                .or_else(|| {
                    model
                        .get("label")
                        .and_then(Value::as_str)
                        .filter(|s| !s.trim().is_empty())
                })
                .unwrap_or(id.as_str())
                .to_string();
            Some(UsageWindow::from_fraction(label, fraction, reset, now))
        })
        .collect()
}

fn buckets_from_quota(value: &Value, now: DateTime<Utc>) -> Vec<UsageWindow> {
    let Some(buckets) = value.get("buckets").and_then(Value::as_array) else {
        return Vec::new();
    };
    // Keep the lowest remaining fraction per model (the binding limit).
    let mut by_model: std::collections::BTreeMap<String, (f64, Option<DateTime<Utc>>)> =
        std::collections::BTreeMap::new();
    for bucket in buckets {
        let Some(model) = bucket
            .get("modelId")
            .and_then(Value::as_str)
            .map(str::trim)
            .filter(|s| !s.is_empty())
        else {
            continue;
        };
        let Some(fraction) = bucket.get("remainingFraction").and_then(Value::as_f64) else {
            continue;
        };
        let reset = bucket
            .get("resetTime")
            .and_then(Value::as_str)
            .and_then(parse_datetime);
        by_model
            .entry(model.to_string())
            .and_modify(|cur| {
                if fraction < cur.0 {
                    *cur = (fraction, reset);
                }
            })
            .or_insert((fraction, reset));
    }
    by_model
        .into_iter()
        .map(|(model, (fraction, reset))| UsageWindow::from_fraction(model, fraction, reset, now))
        .collect()
}

// ── OAuth client discovery (scan installed Antigravity.app) ───────────────────

fn resolve_oauth_client() -> Option<(String, String)> {
    if let (Ok(id), Ok(secret)) = (
        std::env::var("ANTIGRAVITY_OAUTH_CLIENT_ID"),
        std::env::var("ANTIGRAVITY_OAUTH_CLIENT_SECRET"),
    ) {
        let (id, secret) = (id.trim().to_string(), secret.trim().to_string());
        if !id.is_empty() && !secret.is_empty() {
            return Some((id, secret));
        }
    }
    static CACHE: OnceLock<Option<(String, String)>> = OnceLock::new();
    CACHE.get_or_init(discover_client_from_app).clone()
}

fn discover_client_from_app() -> Option<(String, String)> {
    for path in client_artifact_candidates() {
        let Ok(data) = std::fs::read(&path) else {
            continue;
        };
        let ids = scan_client_ids(&data);
        let secrets = scan_client_secrets(&data);
        if let Some(client) = preferred_client(&ids, &secrets) {
            return Some(client);
        }
    }
    None
}

fn client_artifact_candidates() -> Vec<PathBuf> {
    let relative = [
        "Contents/Resources/bin/language_server",
        "Contents/Resources/bin/language_server_macos",
        "Contents/Resources/app/extensions/antigravity/bin/language_server_macos_arm",
        "Contents/Resources/app/extensions/antigravity/bin/language_server_macos_x64",
        "Contents/Resources/app/extensions/antigravity/bin/language_server_macos",
        "Contents/Resources/app/out/main.js",
    ];
    let mut roots = vec![PathBuf::from("/Applications/Antigravity.app")];
    if let Some(home) = std::env::var_os("HOME") {
        roots.push(PathBuf::from(home).join("Applications/Antigravity.app"));
    }
    roots
        .iter()
        .flat_map(|root| relative.iter().map(move |r| root.join(r)))
        .collect()
}

fn is_token_byte(b: u8) -> bool {
    b.is_ascii_alphanumeric() || b == b'-' || b == b'_'
}

fn find_sub(haystack: &[u8], needle: &[u8]) -> Option<usize> {
    if needle.is_empty() || haystack.len() < needle.len() {
        return None;
    }
    haystack.windows(needle.len()).position(|w| w == needle)
}

fn scan_client_ids(data: &[u8]) -> Vec<String> {
    let suffix = b".apps.googleusercontent.com";
    let mut out: Vec<String> = Vec::new();
    let mut i = 0;
    while let Some(pos) = find_sub(&data[i..], suffix) {
        let end = i + pos + suffix.len();
        let mut start = i + pos;
        while start > 0 && is_token_byte(data[start - 1]) {
            start -= 1;
        }
        if let Ok(candidate) = std::str::from_utf8(&data[start..end]) {
            if valid_client_id(candidate) && !out.contains(&candidate.to_string()) {
                out.push(candidate.to_string());
            }
        }
        i = i + pos + suffix.len();
    }
    out
}

fn valid_client_id(s: &str) -> bool {
    s.ends_with(".apps.googleusercontent.com")
        && s.split_once('-')
            .is_some_and(|(head, _)| !head.is_empty() && head.bytes().all(|b| b.is_ascii_digit()))
}

fn scan_client_secrets(data: &[u8]) -> Vec<String> {
    let prefix = b"GOCSPX-";
    let total = prefix.len() + 28;
    let mut out: Vec<String> = Vec::new();
    let mut i = 0;
    while let Some(pos) = find_sub(&data[i..], prefix) {
        let abs = i + pos;
        if abs + total <= data.len() {
            let candidate = &data[abs..abs + total];
            if candidate[prefix.len()..].iter().all(|b| is_token_byte(*b)) {
                if let Ok(s) = std::str::from_utf8(candidate) {
                    if !out.contains(&s.to_string()) {
                        out.push(s.to_string());
                    }
                }
            }
        }
        i = abs + prefix.len();
    }
    out
}

/// codexbar's pairing heuristic for the (possibly multiple) ids/secrets baked
/// into the language_server binary.
fn preferred_client(ids: &[String], secrets: &[String]) -> Option<(String, String)> {
    if ids.is_empty() || secrets.is_empty() {
        return None;
    }
    if secrets.len() == 1 && ids.len() > 1 {
        return Some((ids[ids.len() - 1].clone(), secrets[0].clone()));
    }
    let secret = if secrets.len() == ids.len() && secrets.len() > 1 {
        secrets[secrets.len() - 1].clone()
    } else {
        secrets[0].clone()
    };
    Some((ids[0].clone(), secret))
}

// ── shared ────────────────────────────────────────────────────────────────────

fn gemini_home() -> Option<PathBuf> {
    std::env::var_os("HOME").map(|home| PathBuf::from(home).join(".gemini"))
}

fn gemini_active_email() -> Option<String> {
    let path = gemini_home()?.join("google_accounts.json");
    let raw = std::fs::read_to_string(path).ok()?;
    let json: Value = serde_json::from_str(&raw).ok()?;
    json.get("active")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(str::to_string)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn extracts_flags_both_forms() {
        let cmd = "/x/language_server --app_data_dir /Users/me/.gemini/antigravity --csrf_token=ABC123 --extension_server_port 4567";
        assert_eq!(extract_flag(cmd, "--csrf_token").as_deref(), Some("ABC123"));
        assert_eq!(extract_flag(cmd, "--extension_server_port").as_deref(), Some("4567"));
        assert!(is_language_server(&cmd.to_lowercase()));
        assert!(is_antigravity(&cmd.to_lowercase()));
    }

    #[test]
    fn parses_lsof_listen_port() {
        let line = "language_ 123 nanako 30u IPv4 0x0 0t0 TCP 127.0.0.1:54321 (LISTEN)";
        assert_eq!(parse_listen_port(line), Some(54321));
        assert_eq!(parse_listen_port("... (ESTABLISHED)"), None);
    }

    #[test]
    fn scans_and_pairs_oauth_client_from_bytes() {
        let blob = b"junk\x00123-abcDEF_g.apps.googleusercontent.com\x00\x00GOCSPX-abcdefghijklmnopqrstuvwxyz12\x00tail";
        let ids = scan_client_ids(blob);
        let secrets = scan_client_secrets(blob);
        assert_eq!(ids, vec!["123-abcDEF_g.apps.googleusercontent.com".to_string()]);
        assert_eq!(secrets.len(), 1);
        let client = preferred_client(&ids, &secrets).unwrap();
        assert_eq!(client.0, "123-abcDEF_g.apps.googleusercontent.com");
        assert!(client.1.starts_with("GOCSPX-"));
    }

    #[test]
    fn prefers_last_id_when_single_secret() {
        let ids = vec!["1-a.apps.googleusercontent.com".into(), "2-b.apps.googleusercontent.com".into()];
        let secrets = vec!["GOCSPX-only".into()];
        assert_eq!(preferred_client(&ids, &secrets).unwrap().0, "2-b.apps.googleusercontent.com");
    }

    #[test]
    fn parses_local_user_status_quotas() {
        let now = Utc::now();
        let body = r#"{
            "userStatus": {
                "email": "me@gmail.com",
                "userTier": { "name": "Pro" },
                "cascadeModelConfigData": {
                    "clientModelConfigs": [
                        { "label": "Gemini 3 Pro", "modelOrAlias": {"model":"gemini-3-pro"},
                          "quotaInfo": { "remainingFraction": 0.42, "resetTime": "2026-06-09T00:00:00Z" } },
                        { "label": "No Quota", "modelOrAlias": {"model":"x"} }
                    ]
                }
            }
        }"#;
        let fetched = parse_user_status(body, now).unwrap();
        assert_eq!(fetched.source, "cli");
        assert_eq!(fetched.identity.as_ref().unwrap().email.as_deref(), Some("me@gmail.com"));
        assert_eq!(fetched.identity.as_ref().unwrap().plan.as_deref(), Some("Pro"));
        assert_eq!(fetched.windows.len(), 1);
        assert_eq!(fetched.windows[0].label_for_test(), "Gemini 3 Pro");
        assert!((fetched.windows[0].remaining_for_test() - 42.0).abs() < 0.01);
    }

    #[test]
    fn maps_available_models_and_quota_buckets() {
        let now = Utc::now();
        let models = json!({
            "models": {
                "gemini-3-pro": { "displayName": "Gemini 3 Pro", "quotaInfo": { "remainingFraction": 0.5 } }
            }
        });
        let w = models_from_available(&models, now);
        assert_eq!(w.len(), 1);

        let quota = json!({
            "buckets": [
                { "modelId": "claude", "remainingFraction": 0.8 },
                { "modelId": "claude", "remainingFraction": 0.3 }
            ]
        });
        let b = buckets_from_quota(&quota, now);
        assert_eq!(b.len(), 1);
        assert!((b[0].remaining_for_test() - 30.0).abs() < 0.01); // lowest kept
    }

    #[test]
    fn resolves_remote_plan_from_tier() {
        assert_eq!(resolve_remote_plan(&json!({"currentTier":{"id":"free-tier"}})).as_deref(), Some("Free"));
        assert_eq!(resolve_remote_plan(&json!({"planInfo":{"planType":"standard"}})).as_deref(), Some("Standard"));
    }
}
