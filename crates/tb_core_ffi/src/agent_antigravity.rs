//! Antigravity (Google Code Assist) usage/quota — ported from codexbar's
//! Antigravity provider. Antigravity 2.0 (the IDE and the `antigravity-cli`
//! that replaced `gemini-cli`) does NOT persist per-message token counts
//! locally, so a contribution-graph style breakdown isn't derivable; the quota
//! API is the only usable signal. We mirror codexbar's `auto` source:
//!
//! 1. **Local IDE API (`cli`)** — when Antigravity is running, find its
//!    `language_server` process (carrying a `--csrf_token`), discover its
//!    listening ports with platform-native process tools, and call the local
//!    Connect-RPC `GetUserStatus` over loopback TLS. Live, no token refresh,
//!    no disk writes.
//! 2. **OAuth remote (`oauth`)** — otherwise read the shared Google creds at
//!    `~/.gemini/oauth_creds.json`, refresh against Google (client id/secret
//!    scanned from the installed Antigravity.app binary), and hit the
//!    `cloudcode-pa.googleapis.com` Code Assist quota endpoints.
//!
//! Both yield per-model "remaining fraction + reset" which map to `UsageWindow`s.

use crate::agent_account_scope::{
    self, AccountScope, AccountScopeError, AuthoritativeIdKind, RefreshCheckpoint,
    RefreshScopeTransaction,
};
use crate::agent_usage::{clean_plan, parse_datetime, percent_encode, AgentIdentity, UsageWindow};
#[cfg(any(windows, test))]
use base64::Engine;
use chrono::{DateTime, Utc};
use serde::Deserialize;
use serde_json::{json, value::RawValue, Value};
use std::collections::{BTreeMap, BTreeSet};
#[cfg(windows)]
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::OnceLock;

const LANG_SERVICE: &str = "/exa.language_server_pb.LanguageServerService/GetUserStatus";
const CODE_ASSIST_BASE: &str = "https://cloudcode-pa.googleapis.com/v1internal";
const GOOGLE_TOKEN_URL: &str = "https://oauth2.googleapis.com/token";
const GOOGLE_USERINFO_URL: &str = "https://www.googleapis.com/oauth2/v2/userinfo";
const REFRESH_SAFETY_SECS: i64 = 60;

pub(crate) struct Fetched {
    pub source: String,
    pub identity: Option<AgentIdentity>,
    pub account_scope: Result<AccountScope, AccountScopeError>,
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
    let processes = discover_local_ide()?;

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

    let mut last_err = "Antigravity local IDE API not reachable".to_string();
    for (port, csrf) in local_api_candidates(processes) {
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
            Ok(mut fetched) if local_result_is_acceptable(&fetched) => {
                fetched.account_scope = resolve_local_account_scope(fetched.identity.as_ref());
                return Ok(fetched);
            }
            Ok(_) => last_err = "local API returned no model quotas".to_string(),
            Err(e) => last_err = e,
        }
    }
    Err(last_err)
}

fn local_result_is_acceptable(fetched: &Fetched) -> bool {
    !fetched.windows.is_empty()
}

fn local_api_candidates(processes: Vec<(ProcInfo, Vec<u16>)>) -> Vec<(u16, String)> {
    let mut candidates = Vec::new();
    for (proc, ports) in processes {
        // language-server ports use the language-server CSRF; the extension
        // server (if advertised) carries its own token.
        candidates.extend(
            ports
                .into_iter()
                .map(|port| (port, proc.csrf_token.clone())),
        );
        if let Some(port) = proc.extension_port {
            if let Some(csrf) = proc.extension_csrf.as_ref() {
                candidates.push((port, csrf.clone()));
            }
            candidates.push((port, proc.csrf_token.clone()));
        }
    }
    candidates
}

#[cfg(not(windows))]
fn discover_local_ide() -> Result<Vec<(ProcInfo, Vec<u16>)>, String> {
    let proc = detect_process()?;
    let ports = listening_ports(proc.pid)?;
    Ok(vec![(proc, ports)])
}

#[cfg(not(windows))]
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

#[cfg(not(windows))]
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

#[cfg(any(windows, test))]
const WINDOWS_DISCOVERY_PREFIX: &str = "ANTIGRAVITY_V1";

#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

#[cfg(windows)]
const WINDOWS_DISCOVERY_SCRIPT: &str = r#"
$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $processes = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'language_server.exe'")
    if ($processes.Count -eq 0) {
        [Console]::Out.WriteLine("ANTIGRAVITY_V1`tN")
        exit 0
    }

    $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop)
    foreach ($process in $processes) {
        $commandLine = [string]$process.CommandLine
        $encoded = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($commandLine))
        $ports = @(
            $listeners |
                Where-Object { $_.OwningProcess -eq [uint32]$process.ProcessId } |
                ForEach-Object { [uint16]$_.LocalPort } |
                Sort-Object -Unique
        )
        [Console]::Out.WriteLine(
            "ANTIGRAVITY_V1`tP`t$($process.ProcessId)`t$encoded`t$($ports -join ',')"
        )
    }
} catch {
    exit 1
}
exit 0
"#;

#[cfg(any(windows, test))]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum WindowsDiscoveryError {
    ProcessNotFound,
    TokenMissing,
    PortsMissing,
    PowerShellFailed,
    MalformedOutput,
}

#[cfg(any(windows, test))]
impl WindowsDiscoveryError {
    fn message(self) -> &'static str {
        match self {
            Self::ProcessNotFound => "Antigravity is not running",
            Self::TokenMissing => "Antigravity is running but no CSRF token was found",
            Self::PortsMissing => "no listening ports for Antigravity",
            Self::PowerShellFailed => "Antigravity PowerShell discovery failed",
            Self::MalformedOutput => "malformed Antigravity PowerShell discovery output",
        }
    }
}

#[cfg(windows)]
fn discover_local_ide() -> Result<Vec<(ProcInfo, Vec<u16>)>, String> {
    let system_root = std::env::var_os("SystemRoot").ok_or_else(|| {
        WindowsDiscoveryError::PowerShellFailed
            .message()
            .to_string()
    })?;
    let powershell = PathBuf::from(system_root)
        .join("System32")
        .join("WindowsPowerShell")
        .join("v1.0")
        .join("powershell.exe");
    let output = Command::new(powershell)
        .args([
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            WINDOWS_DISCOVERY_SCRIPT,
        ])
        .creation_flags(CREATE_NO_WINDOW)
        .output()
        .map_err(|_| {
            WindowsDiscoveryError::PowerShellFailed
                .message()
                .to_string()
        })?;
    if !output.status.success() {
        return Err(WindowsDiscoveryError::PowerShellFailed
            .message()
            .to_string());
    }
    let stdout = std::str::from_utf8(&output.stdout)
        .map_err(|_| WindowsDiscoveryError::MalformedOutput.message().to_string())?;
    parse_windows_discovery(stdout).map_err(|error| error.message().to_string())
}

#[cfg(any(windows, test))]
fn parse_windows_discovery(
    stdout: &str,
) -> Result<Vec<(ProcInfo, Vec<u16>)>, WindowsDiscoveryError> {
    let mut processes = Vec::new();
    let mut saw_valid_record = false;
    let mut saw_antigravity = false;
    let mut saw_csrf = false;

    for line in stdout.lines() {
        let fields: Vec<&str> = line.trim_end_matches('\r').split('\t').collect();
        match fields.as_slice() {
            [prefix, "N"] if *prefix == WINDOWS_DISCOVERY_PREFIX => {
                saw_valid_record = true;
            }
            [prefix, "P", pid, encoded_cmd, port_field] if *prefix == WINDOWS_DISCOVERY_PREFIX => {
                let Ok(pid) = pid.parse::<i32>() else {
                    continue;
                };
                let Some(ports) = parse_windows_ports(port_field) else {
                    continue;
                };
                let Ok(command_bytes) =
                    base64::engine::general_purpose::STANDARD.decode(encoded_cmd)
                else {
                    continue;
                };
                let Ok(cmd) = String::from_utf8(command_bytes) else {
                    continue;
                };
                if cmd.is_empty() {
                    continue;
                }
                saw_valid_record = true;
                let lower = cmd.to_lowercase();
                if !is_language_server(&lower) || !is_antigravity(&lower) {
                    continue;
                }
                saw_antigravity = true;
                let Some(csrf) = extract_flag(&cmd, "--csrf_token") else {
                    continue;
                };
                saw_csrf = true;
                if ports.is_empty() {
                    continue;
                }
                processes.push((
                    ProcInfo {
                        pid,
                        csrf_token: csrf,
                        extension_port: extract_flag(&cmd, "--extension_server_port")
                            .and_then(|value| value.parse().ok()),
                        extension_csrf: extract_flag(&cmd, "--extension_server_csrf_token"),
                    },
                    ports,
                ));
            }
            _ => {}
        }
    }

    if !processes.is_empty() {
        Ok(processes)
    } else if saw_csrf {
        Err(WindowsDiscoveryError::PortsMissing)
    } else if saw_antigravity {
        Err(WindowsDiscoveryError::TokenMissing)
    } else if saw_valid_record {
        Err(WindowsDiscoveryError::ProcessNotFound)
    } else {
        Err(WindowsDiscoveryError::MalformedOutput)
    }
}

#[cfg(any(windows, test))]
fn parse_windows_ports(field: &str) -> Option<Vec<u16>> {
    if field.is_empty() {
        return Some(Vec::new());
    }
    let mut ports = BTreeSet::new();
    for value in field.split(',') {
        let port = value.parse::<u16>().ok()?;
        if port == 0 {
            return None;
        }
        ports.insert(port);
    }
    Some(ports.into_iter().collect())
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
    client_model_configs: Option<Vec<Box<RawValue>>>,
}

#[derive(Debug, Deserialize)]
struct AvailableModelsResponse {
    #[serde(default)]
    models: BTreeMap<String, Box<RawValue>>,
}

#[derive(Debug, Deserialize)]
struct QuotaBucketsResponse {
    #[serde(default)]
    buckets: Vec<Box<RawValue>>,
}

#[derive(Debug)]
struct ModelCandidate {
    model_id: Option<String>,
    fraction: f64,
    reset: Option<DateTime<Utc>>,
    reset_was_supplied: bool,
    source_index: usize,
    label: String,
}

fn valid_remaining_fraction(fraction: f64) -> bool {
    fraction.is_finite() && (0.0..=1.0).contains(&fraction)
}

fn quota_window(
    label: String,
    fraction: f64,
    reset: Option<DateTime<Utc>>,
    reset_was_supplied: bool,
    now: DateTime<Utc>,
    card_id: String,
    window_key: Option<String>,
) -> Option<UsageWindow> {
    valid_remaining_fraction(fraction).then(|| {
        UsageWindow::from_fraction(label, fraction, reset, now)
            .with_identity(card_id, window_key)
            .with_observed_duration_evidence(now, reset_was_supplied)
    })
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
    let mut selected: BTreeMap<String, ModelCandidate> = BTreeMap::new();
    let mut missing_model = Vec::new();
    for (source_index, config) in configs.into_iter().enumerate() {
        let Ok(config) = serde_json::from_str::<Value>(config.get()) else {
            continue;
        };
        let Some(quota) = config.get("quotaInfo") else {
            continue;
        };
        let Some(fraction) = quota.get("remainingFraction").and_then(Value::as_f64) else {
            continue;
        };
        let reset_was_supplied = quota
            .get("resetTime")
            .is_some_and(|value| !value.is_null());
        let reset = quota
            .get("resetTime")
            .and_then(Value::as_str)
            .and_then(parse_datetime);
        let model_id = config
            .pointer("/modelOrAlias/model")
            .and_then(Value::as_str)
            .map(str::trim)
            .filter(|model| !model.is_empty())
            .map(str::to_string);
        let label = config
            .get("label")
            .and_then(Value::as_str)
            .filter(|s| !s.trim().is_empty())
            .map(str::to_string)
            .or_else(|| model_id.clone())
            .unwrap_or_else(|| "Model".to_string());
        let candidate = ModelCandidate {
            model_id: model_id.clone(),
            fraction,
            reset,
            reset_was_supplied,
            source_index,
            label,
        };
        let Some(model_id) = model_id else {
            missing_model.push(candidate);
            continue;
        };
        match selected.get(&model_id) {
            Some(current)
                if !binding_candidate_is_better(
                    candidate.fraction,
                    candidate.reset,
                    candidate.source_index,
                    current.fraction,
                    current.reset,
                    current.source_index,
                    now,
                ) => {}
            _ => {
                selected.insert(model_id, candidate);
            }
        }
    }
    let mut candidates: Vec<ModelCandidate> = selected.into_values().collect();
    candidates.extend(missing_model);
    candidates.sort_by_key(|candidate| candidate.source_index);
    let windows: Vec<UsageWindow> = candidates
        .into_iter()
        .filter_map(|candidate| {
            let (card_id, window_key) = match candidate.model_id {
                Some(model_id) => {
                    let key = format!("model.{model_id}.v1");
                    (key.clone(), Some(key))
                }
                None => (format!("row.cli.config.{}.v1", candidate.source_index), None),
            };
            quota_window(
                candidate.label,
                candidate.fraction,
                candidate.reset,
                candidate.reset_was_supplied,
                now,
                card_id,
                window_key,
            )
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
        // Parsing stays pure. The authenticated loopback fetch resolves this only
        // after accepting the response row.
        account_scope: Err(AccountScopeError::NoTrustedEvidence),
        windows,
    })
}

fn resolve_local_account_scope(
    identity: Option<&AgentIdentity>,
) -> Result<AccountScope, AccountScopeError> {
    resolve_local_account_scope_with(identity, resolve_email_account_scope)
}

fn resolve_email_account_scope(email: &str) -> Result<AccountScope, AccountScopeError> {
    agent_account_scope::resolve_authoritative("antigravity", AuthoritativeIdKind::Email, email)
}

fn resolve_local_account_scope_with<T>(
    identity: Option<&AgentIdentity>,
    resolve: impl FnOnce(&str) -> Result<T, AccountScopeError>,
) -> Result<T, AccountScopeError> {
    let email = identity
        .and_then(|identity| identity.email.as_deref())
        .filter(|email| !email.trim().is_empty())
        .ok_or(AccountScopeError::NoTrustedEvidence)?;
    resolve(email)
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

#[derive(Deserialize)]
struct GoogleUserInfo {
    email: Option<String>,
    #[serde(default)]
    verified_email: bool,
}

struct RemoteAuth<T> {
    access_token: String,
    account_scope: Result<T, AccountScopeError>,
}

async fn fetch_oauth_remote(now: DateTime<Utc>) -> Result<Fetched, String> {
    let creds_path = gemini_home()
        .map(|home| home.join("oauth_creds.json"))
        .ok_or_else(|| "Could not resolve ~/.gemini".to_string())?;
    let creds = load_remote_credentials(&creds_path)?;
    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(30))
        .build()
        .map_err(|e| format!("build Antigravity client: {e}"))?;
    let auth = prepare_remote_auth_with(
        &creds,
        now,
        || refresh_access_token(&creds_path, now),
        |access_token| fetch_remote_account_scope(&client, access_token),
    )
    .await?;

    let code_assist_body = code_assist_post(
        &client,
        "loadCodeAssist",
        &json!({
            "metadata": { "ideType": "ANTIGRAVITY", "platform": "PLATFORM_UNSPECIFIED", "pluginType": "GEMINI" }
        }),
        &auth.access_token,
    )
    .await?;
    let code_assist: Value = serde_json::from_str(&code_assist_body)
        .map_err(|e| format!("decode Antigravity loadCodeAssist: {e}"))?;
    let project = project_id(&code_assist);
    let plan = resolve_remote_plan(&code_assist);
    let windows = fetch_model_quotas(&client, &auth.access_token, project.as_deref(), now).await?;

    Ok(remote_fetched(plan, auth.account_scope, windows))
}

async fn prepare_remote_auth_with<T, Refresh, RefreshFuture, Resolve, ResolveFuture>(
    creds: &Value,
    now: DateTime<Utc>,
    refresh: Refresh,
    resolve_account_scope: Resolve,
) -> Result<RemoteAuth<T>, String>
where
    Refresh: FnOnce() -> RefreshFuture,
    RefreshFuture: std::future::Future<
        Output = Result<(Value, String, Result<AccountScope, AccountScopeError>), String>,
    >,
    Resolve: FnOnce(String) -> ResolveFuture,
    ResolveFuture: std::future::Future<Output = Result<T, AccountScopeError>>,
{
    let mut access_token = remote_access_token(creds)?;
    if remote_credentials_need_refresh(creds, now) {
        // Credential lineage protects refresh rotation only; it is never history identity.
        let (_, final_access_token, _credential_lineage) = refresh().await?;
        access_token = final_access_token;
    }
    let account_scope = resolve_account_scope(access_token.clone()).await;
    Ok(RemoteAuth {
        access_token,
        account_scope,
    })
}

async fn fetch_remote_account_scope(
    client: &reqwest::Client,
    access_token: String,
) -> Result<AccountScope, AccountScopeError> {
    let response = client
        .get(GOOGLE_USERINFO_URL)
        .bearer_auth(&access_token)
        .send()
        .await
        .map_err(|_| AccountScopeError::NoTrustedEvidence)?;
    let status = response.status();
    require_google_user_info_success(status)?;
    let body = response
        .text()
        .await
        .map_err(|_| AccountScopeError::NoTrustedEvidence)?;
    resolve_google_user_info_response_with(status, &body, resolve_email_account_scope)
}

fn require_google_user_info_success(
    status: reqwest::StatusCode,
) -> Result<(), AccountScopeError> {
    status
        .is_success()
        .then_some(())
        .ok_or(AccountScopeError::NoTrustedEvidence)
}

fn resolve_google_user_info_response_with<T>(
    status: reqwest::StatusCode,
    body: &str,
    resolve: impl FnOnce(&str) -> Result<T, AccountScopeError>,
) -> Result<T, AccountScopeError> {
    require_google_user_info_success(status)?;
    let user_info: GoogleUserInfo =
        serde_json::from_str(body).map_err(|_| AccountScopeError::NoTrustedEvidence)?;
    if !user_info.verified_email {
        return Err(AccountScopeError::NoTrustedEvidence);
    }
    let email = user_info
        .email
        .filter(|email| !email.trim().is_empty())
        .ok_or(AccountScopeError::NoTrustedEvidence)?;
    resolve(&email)
}

fn remote_fetched(
    plan: Option<String>,
    account_scope: Result<AccountScope, AccountScopeError>,
    windows: Vec<UsageWindow>,
) -> Fetched {
    Fetched {
        source: "oauth".to_string(),
        identity: Some(remote_identity(plan)),
        account_scope,
        windows,
    }
}

fn remote_identity(plan: Option<String>) -> AgentIdentity {
    // UserInfo email is scope evidence only and must not enter the wire identity.
    AgentIdentity { email: None, plan }
}

fn load_remote_credentials(path: &Path) -> Result<Value, String> {
    let raw = std::fs::read_to_string(path)
        .map_err(|_| "Antigravity not logged in (no ~/.gemini/oauth_creds.json)".to_string())?;
    serde_json::from_str(&raw).map_err(|e| format!("decode oauth_creds.json: {e}"))
}

fn remote_access_token(creds: &Value) -> Result<String, String> {
    creds
        .get("access_token")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|token| !token.is_empty())
        .map(str::to_string)
        .ok_or_else(|| "Antigravity creds have no access token".to_string())
}

fn remote_refresh_marker(creds: &Value) -> Option<&[u8]> {
    creds
        .get("refresh_token")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|token| !token.is_empty())
        .map(str::as_bytes)
}

fn remote_credentials_need_refresh(creds: &Value, now: DateTime<Utc>) -> bool {
    let expiry_ms = creds.get("expiry_date").and_then(Value::as_f64);
    let now_ms = now.timestamp_millis() as f64;
    expiry_ms.is_none_or(|expiry| expiry <= now_ms + (REFRESH_SAFETY_SECS * 1000) as f64)
}

fn remote_scope_location(path: &Path) -> Result<String, AccountScopeError> {
    agent_account_scope::canonical_file_location(path, Some("refresh_token"))
}

async fn refresh_access_token(
    creds_path: &Path,
    now: DateTime<Utc>,
) -> Result<(Value, String, Result<AccountScope, AccountScopeError>), String> {
    let refresh = agent_account_scope::begin_refresh("antigravity")
        .map_err(|_| "Antigravity credential refresh lock is unavailable.".to_string())?;
    refresh_access_token_with(
        creds_path,
        now,
        &refresh,
        request_access_token,
        |creds| write_creds_atomic(creds_path, creds),
        |_| Ok(()),
    )
    .await
}

async fn request_access_token(refresh_token: String) -> Result<Value, String> {
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
        percent_encode(&refresh_token),
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
    response
        .json()
        .await
        .map_err(|e| format!("decode refresh response: {e}"))
}

async fn refresh_access_token_with<R, Request, RequestFuture, Save, Checkpoint>(
    creds_path: &Path,
    now: DateTime<Utc>,
    refresh: &R,
    request: Request,
    save: Save,
    mut checkpoint: Checkpoint,
) -> Result<(Value, String, Result<AccountScope, AccountScopeError>), String>
where
    R: RefreshScopeTransaction + ?Sized,
    Request: FnOnce(String) -> RequestFuture,
    RequestFuture: std::future::Future<Output = Result<Value, String>>,
    Save: FnOnce(&Value) -> std::io::Result<()>,
    Checkpoint: FnMut(RefreshCheckpoint) -> Result<(), String>,
{
    let mut creds = load_remote_credentials(creds_path)?;
    checkpoint(RefreshCheckpoint::Reloaded)?;
    let location = remote_scope_location(creds_path)
        .map_err(|_| "Antigravity auth location cannot be scoped safely.".to_string())?;
    if !remote_credentials_need_refresh(&creds, Utc::now()) {
        let access_token = remote_access_token(&creds)?;
        let scope = match remote_refresh_marker(&creds) {
            Some(marker) => refresh.resolve_current("google-oauth-creds", &location, marker),
            None => Err(AccountScopeError::NoTrustedEvidence),
        };
        return Ok((creds, access_token, scope));
    }

    let old_marker = remote_refresh_marker(&creds)
        .ok_or_else(|| "Antigravity access token expired and no refresh token".to_string())?
        .to_vec();
    let refresh_token = std::str::from_utf8(&old_marker)
        .map_err(|_| "Antigravity refresh credential is not valid text.".to_string())?
        .to_string();
    let json = request(refresh_token).await?;
    checkpoint(RefreshCheckpoint::NetworkReturned)?;
    let access_token = json
        .get("access_token")
        .and_then(Value::as_str)
        .ok_or_else(|| "refresh response missing access_token".to_string())?
        .to_string();

    if let Some(obj) = creds.as_object_mut() {
        obj.insert("access_token".into(), Value::String(access_token.clone()));
        if let Some(expires_in) = json.get("expires_in").and_then(Value::as_f64) {
            let expiry = now.timestamp_millis() as f64 + expires_in * 1000.0;
            obj.insert("expiry_date".into(), json!(expiry));
        }
        if let Some(id_token) = json.get("id_token").and_then(Value::as_str) {
            obj.insert("id_token".into(), Value::String(id_token.to_string()));
        }
        if let Some(replacement) = json
            .get("refresh_token")
            .and_then(Value::as_str)
            .map(str::trim)
            .filter(|token| !token.is_empty())
        {
            obj.insert(
                "refresh_token".into(),
                Value::String(replacement.to_string()),
            );
        }
    }
    let new_marker = remote_refresh_marker(&creds);
    let marker_rotated = new_marker.is_some_and(|marker| marker != old_marker.as_slice());
    let scope = match new_marker {
        Some(new_marker) => {
            refresh.transfer("google-oauth-creds", &location, &old_marker, new_marker)
        }
        None => Err(AccountScopeError::NoTrustedEvidence),
    };
    checkpoint(RefreshCheckpoint::MetadataHandled)?;
    if marker_rotated && scope.is_err() {
        return Ok((creds, access_token, scope));
    }
    let _ = save(&creds);
    checkpoint(RefreshCheckpoint::CredentialsPersisted)?;
    Ok((creds, access_token, scope))
}

fn write_creds_atomic(path: &Path, creds: &Value) -> std::io::Result<()> {
    use std::io::Write as _;
    use std::sync::atomic::{AtomicU64, Ordering};

    let data = serde_json::to_vec_pretty(creds).map_err(std::io::Error::other)?;
    let directory = path.parent().ok_or_else(|| {
        std::io::Error::new(
            std::io::ErrorKind::InvalidInput,
            "credential path has no parent",
        )
    })?;
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let tmp = directory.join(format!(
        ".oauth_creds.json.tokenbar.{}.{}",
        std::process::id(),
        COUNTER.fetch_add(1, Ordering::Relaxed)
    ));
    let staged = (|| {
        let mut options = std::fs::OpenOptions::new();
        options.write(true).create_new(true);
        #[cfg(unix)]
        {
            use std::os::unix::fs::OpenOptionsExt as _;
            options.mode(0o600);
        }
        let mut file = options.open(&tmp)?;
        file.write_all(&data)?;
        file.sync_all()
    })();
    if let Err(error) = staged {
        let _ = std::fs::remove_file(&tmp);
        return Err(error);
    }
    if let Err(error) = std::fs::rename(&tmp, path) {
        let _ = std::fs::remove_file(&tmp);
        return Err(error);
    }
    #[cfg(unix)]
    std::fs::File::open(directory)?.sync_all()?;
    Ok(())
}

async fn code_assist_post(
    client: &reqwest::Client,
    method: &str,
    body: &Value,
    access_token: &str,
) -> Result<String, String> {
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
    resp.text()
        .await
        .map_err(|e| format!("read Antigravity {method}: {e}"))
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
    match code_assist_post(client, "fetchAvailableModels", &body, access_token)
        .await
        .and_then(|body| models_from_available(&body, now))
    {
        Ok(windows) if !windows.is_empty() => Ok(windows),
        _ => {
            let quota = code_assist_post(client, "retrieveUserQuota", &body, access_token).await?;
            buckets_from_quota(&quota, now)
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

fn models_from_available(body: &str, now: DateTime<Utc>) -> Result<Vec<UsageWindow>, String> {
    let response: AvailableModelsResponse = serde_json::from_str(body)
        .map_err(|e| format!("decode Antigravity fetchAvailableModels: {e}"))?;
    let mut selected: BTreeMap<String, ModelCandidate> = BTreeMap::new();
    let mut missing_model = Vec::new();
    for (source_index, (raw_id, model)) in response.models.into_iter().enumerate() {
        let Ok(model) = serde_json::from_str::<Value>(model.get()) else {
            continue;
        };
        let Some(quota) = model.get("quotaInfo") else {
            continue;
        };
        let Some(fraction) = quota.get("remainingFraction").and_then(Value::as_f64) else {
            continue;
        };
        let reset_was_supplied = quota
            .get("resetTime")
            .is_some_and(|value| !value.is_null());
        let reset = quota
            .get("resetTime")
            .and_then(Value::as_str)
            .and_then(parse_datetime);
        let model_id = raw_id.trim().to_string();
        let model_id = (!model_id.is_empty()).then_some(model_id);
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
            .unwrap_or(raw_id.as_str())
            .to_string();
        let candidate = ModelCandidate {
            model_id: model_id.clone(),
            fraction,
            reset,
            reset_was_supplied,
            source_index,
            label,
        };
        let Some(model_id) = model_id else {
            missing_model.push(candidate);
            continue;
        };
        match selected.get(&model_id) {
            Some(current)
                if !binding_candidate_is_better(
                    candidate.fraction,
                    candidate.reset,
                    candidate.source_index,
                    current.fraction,
                    current.reset,
                    current.source_index,
                    now,
                ) => {}
            _ => {
                selected.insert(model_id, candidate);
            }
        }
    }

    let mut candidates: Vec<ModelCandidate> = selected.into_values().collect();
    candidates.extend(missing_model);
    candidates.sort_by_key(|candidate| candidate.source_index);
    Ok(candidates
        .into_iter()
        .filter_map(|candidate| {
            let (card_id, window_key) = match candidate.model_id {
                Some(model_id) => {
                    let key = format!("model.{model_id}.v1");
                    (key.clone(), Some(key))
                }
                None => (format!("row.models.{}.v1", candidate.source_index), None),
            };
            quota_window(
                candidate.label,
                candidate.fraction,
                candidate.reset,
                candidate.reset_was_supplied,
                now,
                card_id,
                window_key,
            )
        })
        .collect())
}

#[derive(Debug)]
struct QuotaBucketCandidate {
    model_id: Option<String>,
    fraction: f64,
    reset: Option<DateTime<Utc>>,
    reset_was_supplied: bool,
    source_index: usize,
}

fn buckets_from_quota(body: &str, now: DateTime<Utc>) -> Result<Vec<UsageWindow>, String> {
    let response: QuotaBucketsResponse = serde_json::from_str(body)
        .map_err(|e| format!("decode Antigravity retrieveUserQuota: {e}"))?;
    let mut selected: BTreeMap<String, QuotaBucketCandidate> = BTreeMap::new();
    let mut missing = Vec::new();
    for (source_index, bucket) in response.buckets.into_iter().enumerate() {
        let Ok(bucket) = serde_json::from_str::<Value>(bucket.get()) else {
            continue;
        };
        let Some(fraction) = bucket.get("remainingFraction").and_then(Value::as_f64) else {
            continue;
        };
        let reset_was_supplied = bucket
            .get("resetTime")
            .is_some_and(|value| !value.is_null());
        let reset = bucket
            .get("resetTime")
            .and_then(Value::as_str)
            .and_then(parse_datetime);
        let model_id = bucket
            .get("modelId")
            .and_then(Value::as_str)
            .map(str::trim)
            .filter(|model| !model.is_empty())
            .map(str::to_string);
        let candidate = QuotaBucketCandidate {
            model_id: model_id.clone(),
            fraction,
            reset,
            reset_was_supplied,
            source_index,
        };
        let Some(model_id) = model_id else {
            missing.push(candidate);
            continue;
        };
        match selected.get(&model_id) {
            Some(current) if !bucket_candidate_is_better(&candidate, current, now) => {}
            _ => {
                selected.insert(model_id, candidate);
            }
        }
    }

    let mut chosen: Vec<QuotaBucketCandidate> = selected.into_values().collect();
    chosen.extend(missing);
    chosen.sort_by_key(|candidate| candidate.source_index);
    Ok(chosen
        .into_iter()
        .filter_map(|candidate| {
            let label = candidate
                .model_id
                .clone()
                .unwrap_or_else(|| "Model".to_string());
            let (card_id, window_key) = match candidate.model_id {
                Some(model_id) => {
                    let key = format!("model.{model_id}.v1");
                    (key.clone(), Some(key))
                }
                None => (
                    format!("row.quota.bucket.{}.v1", candidate.source_index),
                    None,
                ),
            };
            quota_window(
                label,
                candidate.fraction,
                candidate.reset,
                candidate.reset_was_supplied,
                now,
                card_id,
                window_key,
            )
        })
        .collect())
}

fn bucket_candidate_is_better(
    candidate: &QuotaBucketCandidate,
    current: &QuotaBucketCandidate,
    now: DateTime<Utc>,
) -> bool {
    binding_candidate_is_better(
        candidate.fraction,
        candidate.reset,
        candidate.source_index,
        current.fraction,
        current.reset,
        current.source_index,
        now,
    )
}

fn binding_candidate_is_better(
    candidate_fraction: f64,
    candidate_reset: Option<DateTime<Utc>>,
    candidate_index: usize,
    current_fraction: f64,
    current_reset: Option<DateTime<Utc>>,
    current_index: usize,
    now: DateTime<Utc>,
) -> bool {
    match (
        valid_remaining_fraction(candidate_fraction),
        valid_remaining_fraction(current_fraction),
    ) {
        (true, false) => return true,
        (false, true) => return false,
        _ => {}
    }
    if candidate_fraction < current_fraction {
        return true;
    }
    if candidate_fraction > current_fraction {
        return false;
    }
    let candidate_reset = candidate_reset.filter(|reset| *reset > now);
    let current_reset = current_reset.filter(|reset| *reset > now);
    match (candidate_reset, current_reset) {
        (Some(candidate), Some(current)) if candidate != current => return candidate < current,
        (Some(_), None) => return true,
        (None, Some(_)) => return false,
        _ => {}
    }
    candidate_index < current_index
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

#[cfg(target_os = "macos")]
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
    if let Some(home) = crate::user_home_dir() {
        roots.push(home.join("Applications/Antigravity.app"));
    }
    roots
        .iter()
        .flat_map(|root| relative.iter().map(move |r| root.join(r)))
        .collect()
}

#[cfg(any(windows, test))]
fn windows_client_artifact_candidates(
    local_app_data: Option<PathBuf>,
    home: Option<PathBuf>,
) -> Vec<PathBuf> {
    const RELATIVE: &str = "Programs/antigravity/resources/bin/language_server.exe";

    let mut candidates = Vec::new();
    if let Some(root) = local_app_data.filter(|path| !path.as_os_str().is_empty()) {
        candidates.push(root.join(RELATIVE));
    }
    if let Some(home) = home.filter(|path| !path.as_os_str().is_empty()) {
        let fallback = home.join("AppData/Local").join(RELATIVE);
        if candidates.last() != Some(&fallback) {
            candidates.push(fallback);
        }
    }
    candidates
}

#[cfg(windows)]
fn client_artifact_candidates() -> Vec<PathBuf> {
    windows_client_artifact_candidates(
        std::env::var_os("LOCALAPPDATA").map(PathBuf::from),
        crate::user_home_dir(),
    )
}

#[cfg(not(any(target_os = "macos", windows)))]
fn client_artifact_candidates() -> Vec<PathBuf> {
    Vec::new()
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
    crate::user_home_dir().map(|home| home.join(".gemini"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::agent_account_scope::test_support::TestRefreshScope;
    use sha2::{Digest as _, Sha256};

    fn fixture_authoritative_scope(email: &str) -> Result<[u8; 32], AccountScopeError> {
        let mut digest = Sha256::new();
        digest.update(b"antigravity\0email\0");
        digest.update(email.trim().to_ascii_lowercase().as_bytes());
        Ok(digest.finalize().into())
    }

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

    fn windows_process_fixture(pid: i32, command: &str, ports: &str) -> String {
        let encoded = base64::engine::general_purpose::STANDARD.encode(command);
        format!("{WINDOWS_DISCOVERY_PREFIX}\tP\t{pid}\t{encoded}\t{ports}")
    }

    #[test]
    fn parses_windows_discovery_fixture_and_ignores_malformed_rows() {
        let csrf = "fixture-primary-secret";
        let extension_csrf = "fixture-extension-secret";
        let command = format!(
            r#""C:\Program Files\Antigravity\language_server.exe" --app_data_dir antigravity --csrf_token={csrf} --extension_server_port 4567 --extension_server_csrf_token={extension_csrf}"#
        );
        let valid = windows_process_fixture(4242, &command, "61234,54321,61234");
        let fixture = format!(
            "garbage\n{WINDOWS_DISCOVERY_PREFIX}\tP\tnot-a-pid\tbad-base64\t80\n{valid}\r\n"
        );

        let processes = match parse_windows_discovery(&fixture) {
            Ok(processes) => processes,
            Err(_) => panic!("valid Windows discovery fixture was rejected"),
        };
        assert_eq!(processes.len(), 1);
        let (proc, ports) = &processes[0];
        assert_eq!(proc.pid, 4242);
        assert!(proc.csrf_token == csrf);
        assert_eq!(proc.extension_port, Some(4567));
        assert!(proc.extension_csrf.as_deref() == Some(extension_csrf));
        assert_eq!(ports.as_slice(), &[54321, 61234]);
    }

    #[test]
    fn retains_all_windows_processes_in_probe_order() {
        let first_csrf = "first-fixture-secret";
        let first_extension_csrf = "first-extension-fixture-secret";
        let second_csrf = "second-fixture-secret";
        let first = windows_process_fixture(
            1001,
            &format!(
                r#"C:\Antigravity\language_server.exe --app_data_dir antigravity --csrf_token={first_csrf} --extension_server_port=41999 --extension_server_csrf_token={first_extension_csrf}"#
            ),
            "41002,41001",
        );
        let second = windows_process_fixture(
            1002,
            &format!(
                r#"C:\Antigravity\language_server.exe --app_data_dir antigravity --csrf_token={second_csrf}"#
            ),
            "42000",
        );
        let fixture = format!("{first}\n{second}\n");

        let processes = match parse_windows_discovery(&fixture) {
            Ok(processes) => processes,
            Err(_) => panic!("multi-process Windows discovery fixture was rejected"),
        };
        assert_eq!(processes.len(), 2);
        assert_eq!(processes[0].0.pid, 1001);
        assert_eq!(processes[0].1.as_slice(), &[41001, 41002]);
        assert_eq!(processes[1].0.pid, 1002);
        assert_eq!(processes[1].1.as_slice(), &[42000]);

        let candidates = local_api_candidates(processes);
        let candidate_ports: Vec<u16> = candidates.iter().map(|candidate| candidate.0).collect();
        assert_eq!(candidate_ports, vec![41001, 41002, 41999, 41999, 42000]);
        assert!(candidates[0].1.as_str() == first_csrf);
        assert!(candidates[2].1.as_str() == first_extension_csrf);
        assert!(candidates[3].1.as_str() == first_csrf);
        assert!(candidates[4].1.as_str() == second_csrf);
    }

    #[test]
    fn classifies_windows_discovery_failures_without_exposing_token() {
        assert!(matches!(
            parse_windows_discovery(&format!("{WINDOWS_DISCOVERY_PREFIX}\tN\n")),
            Err(WindowsDiscoveryError::ProcessNotFound)
        ));

        let no_token = windows_process_fixture(
            4242,
            r#"C:\Antigravity\language_server.exe --app_data_dir antigravity"#,
            "54321",
        );
        assert!(matches!(
            parse_windows_discovery(&no_token),
            Err(WindowsDiscoveryError::TokenMissing)
        ));

        let csrf = "fixture-secret-that-must-not-leak";
        let no_ports = windows_process_fixture(
            4242,
            &format!(
                r#"C:\Antigravity\language_server.exe --app_data_dir antigravity --csrf_token={csrf}"#
            ),
            "",
        );
        let error = match parse_windows_discovery(&no_ports) {
            Err(error) => error,
            Ok(_) => panic!("missing ports should fail discovery"),
        };
        assert!(matches!(error, WindowsDiscoveryError::PortsMissing));
        assert!(!error.message().contains(csrf));

        assert!(matches!(
            parse_windows_discovery(&format!(
                "{WINDOWS_DISCOVERY_PREFIX}\tP\t4242\tnot-base64\t54321\n"
            )),
            Err(WindowsDiscoveryError::MalformedOutput)
        ));
        assert!(!WindowsDiscoveryError::PowerShellFailed
            .message()
            .contains(csrf));
    }

    #[test]
    fn maps_windows_oauth_client_artifact_roots_in_order_without_duplicates() {
        const RELATIVE: &str = "Programs/antigravity/resources/bin/language_server.exe";
        let home = PathBuf::from("/Users/example");
        let fallback = home.join("AppData/Local").join(RELATIVE);
        let local_app_data = PathBuf::from("/redirected/local");

        assert_eq!(
            windows_client_artifact_candidates(Some(local_app_data.clone()), Some(home.clone())),
            vec![local_app_data.join(RELATIVE), fallback.clone()]
        );
        assert_eq!(
            windows_client_artifact_candidates(
                Some(home.join("AppData/Local")),
                Some(home.clone()),
            ),
            vec![fallback.clone()]
        );
        assert_eq!(
            windows_client_artifact_candidates(None, Some(home)),
            vec![fallback]
        );
        assert!(windows_client_artifact_candidates(None, None).is_empty());
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
        assert_eq!(
            fetched.account_scope,
            Err(AccountScopeError::NoTrustedEvidence)
        );
        assert_eq!(fetched.windows.len(), 1);
        assert_eq!(fetched.windows[0].label_for_test(), "Gemini 3 Pro");
        assert_eq!(fetched.windows[0].card_id_for_test(), "model.gemini-3-pro.v1");
        assert_eq!(
            fetched.windows[0].pace_window_key_for_test(),
            Some("model.gemini-3-pro.v1")
        );
        assert!((fetched.windows[0].remaining_for_test() - 42.0).abs() < 0.01);
    }

    #[test]
    fn identity_only_local_response_is_not_accepted_for_scope_resolution() {
        let fetched = parse_user_status(
            r#"{
                "userStatus": {
                    "email": "user@example.com",
                    "cascadeModelConfigData": { "clientModelConfigs": [] }
                }
            }"#,
            Utc::now(),
        )
        .unwrap();

        assert!(fetched.identity.is_some());
        assert!(fetched.windows.is_empty());
        assert!(!local_result_is_acceptable(&fetched));
    }

    #[test]
    fn local_scope_resolver_uses_only_exact_non_empty_email() {
        let identity = AgentIdentity {
            email: Some("Exact.Email+tag@Example.COM".to_string()),
            plan: None,
        };
        assert_eq!(
            resolve_local_account_scope_with(Some(&identity), |email| {
                assert_eq!(email, "Exact.Email+tag@Example.COM");
                Ok("resolved")
            }),
            Ok("resolved")
        );

        let calls = std::cell::Cell::new(0);
        for email in [None, Some(" \t ")] {
            let identity = AgentIdentity {
                email: email.map(str::to_string),
                plan: None,
            };
            assert_eq!(
                resolve_local_account_scope_with(Some(&identity), |_| {
                    calls.set(calls.get() + 1);
                    Ok(())
                }),
                Err(AccountScopeError::NoTrustedEvidence)
            );
        }
        assert_eq!(calls.get(), 0);

        let identity = AgentIdentity {
            email: Some("failure@example.com".to_string()),
            plan: None,
        };
        assert_eq!(
            resolve_local_account_scope_with(Some(&identity), |email| {
                assert_eq!(email, "failure@example.com");
                Err::<(), _>(AccountScopeError::MetadataWrite)
            }),
            Err(AccountScopeError::MetadataWrite)
        );
    }

    #[test]
    fn local_config_without_model_has_row_identity() {
        let now = Utc::now();
        let body = r#"{
            "userStatus": {
                "cascadeModelConfigData": {
                    "clientModelConfigs": [
                        { "label": "Unnamed", "quotaInfo": { "remainingFraction": 0.5 } }
                    ]
                }
            }
        }"#;
        let fetched = parse_user_status(body, now).unwrap();
        assert_eq!(fetched.windows.len(), 1);
        assert_eq!(fetched.windows[0].card_id_for_test(), "row.cli.config.0.v1");
        assert!(fetched.windows[0].pace_window_key_for_test().is_none());
        assert_eq!(fetched.windows[0].pace_reason_for_test(), Some("windowIdentity"));
    }

    #[test]
    fn maps_available_models_and_quota_buckets() {
        let now = Utc::now();
        let models = json!({
            "models": {
                "gemini-3-pro": { "displayName": "Gemini 3 Pro", "quotaInfo": { "remainingFraction": 0.5 } }
            }
        });
        let w = models_from_available(&models.to_string(), now).unwrap();
        assert_eq!(w.len(), 1);
        assert_eq!(w[0].card_id_for_test(), "model.gemini-3-pro.v1");

        let quota = json!({
            "buckets": [
                { "modelId": "claude", "remainingFraction": 0.8 },
                { "modelId": "claude", "remainingFraction": 0.3 }
            ]
        });
        let b = buckets_from_quota(&quota.to_string(), now).unwrap();
        assert_eq!(b.len(), 1);
        assert!((b[0].remaining_for_test() - 30.0).abs() < 0.01); // lowest kept
    }

    #[test]
    fn stage3c3a_identity_and_duplicate_rules_are_deterministic() {
        let now = DateTime::parse_from_rfc3339("2026-07-10T00:00:00Z")
            .unwrap()
            .with_timezone(&Utc);
        let models = json!({
            "models": {
                "model-A": {
                    "displayName": "Trimmed loser",
                    "quotaInfo": { "remainingFraction": 0.5, "resetTime": "2026-07-12T00:00:00Z" }
                },
                "  model-A  ": {
                    "displayName": "Trimmed winner",
                    "quotaInfo": { "remainingFraction": 0.2, "resetTime": "2026-07-13T00:00:00Z" }
                },
                "model-B": {
                    "displayName": "Shared label",
                    "quotaInfo": { "remainingFraction": 0.4, "resetTime": "2026-07-12T00:00:00Z" }
                },
                "Model-Byte-Case": {
                    "displayName": "Shared label",
                    "quotaInfo": { "remainingFraction": 0.3, "resetTime": "2026-07-13T00:00:00Z" }
                }
            }
        });
        let windows = models_from_available(&models.to_string(), now).unwrap();
        assert_eq!(windows.len(), 3);
        let model_a = windows
            .iter()
            .find(|window| window.pace_window_key_for_test() == Some("model.model-A.v1"))
            .unwrap();
        assert_eq!(model_a.label_for_test(), "Trimmed winner");
        assert!((model_a.remaining_for_test() - 20.0).abs() < 0.01);
        assert_eq!(
            windows
                .iter()
                .filter(|window| window.label_for_test() == "Shared label")
                .count(),
            2,
            "display labels never merge distinct model IDs"
        );
        assert!(windows.iter().any(|window| {
            window.pace_window_key_for_test() == Some("model.Model-Byte-Case.v1")
        }));

        let cli = r#"{
            "userStatus": {
                "cascadeModelConfigData": {
                    "clientModelConfigs": [
                        {
                            "label": "CLI loser",
                            "modelOrAlias": { "model": " Model-X " },
                            "quotaInfo": { "remainingFraction": 0.6, "resetTime": "2026-07-12T00:00:00Z" }
                        },
                        {
                            "label": "CLI winner",
                            "modelOrAlias": { "model": "Model-X" },
                            "quotaInfo": { "remainingFraction": 0.2, "resetTime": "2026-07-13T00:00:00Z" }
                        },
                        { "label": "Config only", "quotaInfo": { "remainingFraction": 0.7 } }
                    ]
                }
            }
        }"#;
        let fetched = parse_user_status(cli, now).unwrap();
        assert_eq!(fetched.windows.len(), 2);
        assert_eq!(
            fetched.windows[0].pace_window_key_for_test(),
            Some("model.Model-X.v1")
        );
        assert_eq!(fetched.windows[0].label_for_test(), "CLI winner");
        let missing_wire = serde_json::to_value(&fetched.windows[1]).unwrap();
        assert_eq!(missing_wire["cardId"], "row.cli.config.2.v1");
        assert_eq!(missing_wire["paceStatus"]["reason"], "windowIdentity");

        let missing_remote = models_from_available(
            &json!({
                "models": {
                    "   ": {
                        "displayName": "Remote model",
                        "quotaInfo": { "remainingFraction": 0.7 }
                    }
                }
            })
            .to_string(),
            now,
        )
        .unwrap();
        let wire = serde_json::to_value(&missing_remote[0]).unwrap();
        assert_eq!(wire["cardId"], "row.models.0.v1");
        assert_eq!(wire["paceStatus"]["reason"], "windowIdentity");

        let missing_bucket = buckets_from_quota(
            &json!({
                "buckets": [
                    { "modelId": "   ", "remainingFraction": 0.7 }
                ]
            })
            .to_string(),
            now,
        )
        .unwrap();
        let wire = serde_json::to_value(&missing_bucket[0]).unwrap();
        assert_eq!(wire["cardId"], "row.quota.bucket.0.v1");
        assert_eq!(wire["paceStatus"]["reason"], "windowIdentity");

        let duplicate = json!({
            "buckets": [
                {
                    "modelId": "same-model",
                    "remainingFraction": 0.25,
                    "resetTime": "2026-07-09T00:00:00Z"
                },
                {
                    "modelId": "same-model",
                    "remainingFraction": 0.25,
                    "resetTime": "2026-07-12T00:00:00Z"
                },
                {
                    "modelId": "same-model",
                    "remainingFraction": 0.25,
                    "resetTime": "2026-07-11T00:00:00Z"
                }
            ]
        });
        let selected = buckets_from_quota(&duplicate.to_string(), now).unwrap();
        assert_eq!(selected.len(), 1);
        let selected_wire = serde_json::to_value(&selected[0]).unwrap();
        assert_eq!(
            selected_wire["resetsAt"],
            "2026-07-11T00:00:00.000Z",
            "future reset beats past reset, then earliest future reset wins"
        );
        let same_reset = parse_datetime("2026-07-11T00:00:00Z");
        assert!(!binding_candidate_is_better(
            0.25, same_reset, 1, 0.25, same_reset, 0, now
        ));

        let signed_zero = r#"{
            "buckets": [
                {
                    "modelId": "signed-zero",
                    "remainingFraction": 0.0,
                    "resetTime": "2026-07-11T00:00:00Z"
                },
                {
                    "modelId": "signed-zero",
                    "remainingFraction": -0.0
                }
            ]
        }"#;
        let selected = buckets_from_quota(signed_zero, now).unwrap();
        assert_eq!(selected.len(), 1);
        let selected_wire = serde_json::to_value(&selected[0]).unwrap();
        assert_eq!(selected_wire["resetsAt"], "2026-07-11T00:00:00.000Z");
        assert_eq!(selected_wire["paceStatus"]["state"], "learningDuration");
        assert_eq!(selected_wire["paceStatus"]["durationSource"], "observed");
    }

    #[test]
    fn stage3c3a_rejects_invalid_fractions_and_isolates_raw_rows() {
        let now = DateTime::parse_from_rfc3339("2026-07-10T00:00:00Z")
            .unwrap()
            .with_timezone(&Utc);
        let assert_valid_only = |windows: Vec<UsageWindow>| {
            assert_eq!(windows.len(), 1);
            assert_eq!(
                windows[0].pace_window_key_for_test(),
                Some("model.valid.v1")
            );
        };

        let cli = r#"{
            "userStatus": {
                "cascadeModelConfigData": {
                    "clientModelConfigs": [
                        {
                            "modelOrAlias": { "model": "valid" },
                            "quotaInfo": { "remainingFraction": 0.5 }
                        },
                        {
                            "modelOrAlias": { "model": "negative" },
                            "quotaInfo": { "remainingFraction": -0.1 }
                        },
                        {
                            "modelOrAlias": { "model": "over" },
                            "quotaInfo": { "remainingFraction": 1.1 }
                        },
                        {
                            "modelOrAlias": { "model": "missing" },
                            "quotaInfo": {}
                        }
                    ]
                }
            }
        }"#;
        assert_valid_only(parse_user_status(cli, now).unwrap().windows);

        let overflowing_cli = r#"{
            "userStatus": {
                "cascadeModelConfigData": {
                    "clientModelConfigs": [
                        {
                            "modelOrAlias": { "model": "same-model" },
                            "quotaInfo": { "remainingFraction": 1e400 }
                        },
                        {
                            "modelOrAlias": { "model": "same-model" },
                            "quotaInfo": { "remainingFraction": 0.5 }
                        }
                    ]
                }
            }
        }"#;
        let windows = parse_user_status(overflowing_cli, now).unwrap().windows;
        assert_eq!(windows.len(), 1);
        assert_eq!(windows[0].pace_window_key_for_test(), Some("model.same-model.v1"));
        assert!((windows[0].remaining_for_test() - 50.0).abs() < 0.01);

        assert_valid_only(
            models_from_available(
                &json!({
                    "models": {
                        "valid": { "quotaInfo": { "remainingFraction": 0.5 } },
                        "negative": { "quotaInfo": { "remainingFraction": -0.1 } },
                        "over": { "quotaInfo": { "remainingFraction": 1.1 } },
                        "missing": { "quotaInfo": {} }
                    }
                })
                .to_string(),
                now,
            )
            .unwrap(),
        );

        let overflowing_models = r#"{
            "models": {
                "same-model": {
                    "quotaInfo": { "remainingFraction": 1e400 }
                },
                " same-model ": {
                    "quotaInfo": { "remainingFraction": 0.5 }
                }
            }
        }"#;
        let windows = models_from_available(overflowing_models, now).unwrap();
        assert_eq!(windows.len(), 1);
        assert_eq!(windows[0].pace_window_key_for_test(), Some("model.same-model.v1"));
        assert!((windows[0].remaining_for_test() - 50.0).abs() < 0.01);

        assert_valid_only(
            buckets_from_quota(
                &json!({
                    "buckets": [
                        { "modelId": "valid", "remainingFraction": 0.5 },
                        { "modelId": "negative", "remainingFraction": -0.1 },
                        { "modelId": "over", "remainingFraction": 1.1 },
                        { "modelId": "missing" }
                    ]
                })
                .to_string(),
                now,
            )
            .unwrap(),
        );

        let overflowing_buckets = r#"{
            "buckets": [
                { "modelId": "same-model", "remainingFraction": 1e400 },
                { "modelId": "same-model", "remainingFraction": 0.5 }
            ]
        }"#;
        let windows = buckets_from_quota(overflowing_buckets, now).unwrap();
        assert_eq!(windows.len(), 1);
        assert_eq!(windows[0].pace_window_key_for_test(), Some("model.same-model.v1"));
        assert!((windows[0].remaining_for_test() - 50.0).abs() < 0.01);

        let malformed_cli_row = r#"{
            "userStatus": {
                "cascadeModelConfigData": {
                    "clientModelConfigs": [
                        {
                            "label": 1e400,
                            "modelOrAlias": { "model": "same-model" },
                            "quotaInfo": { "remainingFraction": 0.4 }
                        },
                        {
                            "modelOrAlias": { "model": "same-model" },
                            "quotaInfo": { "remainingFraction": 0.5 }
                        }
                    ]
                }
            }
        }"#;
        let windows = parse_user_status(malformed_cli_row, now).unwrap().windows;
        assert_eq!(windows.len(), 1);
        assert!((windows[0].remaining_for_test() - 50.0).abs() < 0.01);

        let malformed_model_row = r#"{
            "models": {
                "same-model": {
                    "displayName": 1e400,
                    "quotaInfo": { "remainingFraction": 0.4 }
                },
                " same-model ": {
                    "quotaInfo": { "remainingFraction": 0.5 }
                }
            }
        }"#;
        let windows = models_from_available(malformed_model_row, now).unwrap();
        assert_eq!(windows.len(), 1);
        assert!((windows[0].remaining_for_test() - 50.0).abs() < 0.01);

        let malformed_bucket_fields = r#"{
            "buckets": [
                { "modelId": 42, "remainingFraction": 0.4 },
                { "modelId": "valid", "remainingFraction": 0.5 }
            ]
        }"#;
        let windows = buckets_from_quota(malformed_bucket_fields, now).unwrap();
        assert_eq!(windows.len(), 2);
        assert!(windows
            .iter()
            .any(|window| window.pace_window_key_for_test() == Some("model.valid.v1")));
        let wire = serde_json::to_value(
            windows
                .iter()
                .find(|window| window.pace_window_key_for_test().is_none())
                .unwrap(),
        )
        .unwrap();
        assert_eq!(wire["paceStatus"]["reason"], "windowIdentity");

        assert!(!valid_remaining_fraction(f64::NAN));
        assert!(!valid_remaining_fraction(f64::INFINITY));
        assert!(!valid_remaining_fraction(-0.01));
        assert!(!valid_remaining_fraction(1.01));
        assert!(valid_remaining_fraction(0.0));
        assert!(valid_remaining_fraction(1.0));
    }

    #[test]
    fn stage3c3a_reset_presence_maps_to_typed_pace_reasons() {
        let now = DateTime::parse_from_rfc3339("2026-07-10T00:00:00Z")
            .unwrap()
            .with_timezone(&Utc);
        let body = r#"{
            "userStatus": {
                "cascadeModelConfigData": {
                    "clientModelConfigs": [
                        { "modelOrAlias": { "model": "absent" }, "quotaInfo": { "remainingFraction": 0.1 } },
                        { "modelOrAlias": { "model": "null" }, "quotaInfo": { "remainingFraction": 0.2, "resetTime": null } },
                        { "modelOrAlias": { "model": "malformed" }, "quotaInfo": { "remainingFraction": 0.3, "resetTime": "bogus" } },
                        { "modelOrAlias": { "model": "typed" }, "quotaInfo": { "remainingFraction": 0.4, "resetTime": 42 } },
                        { "modelOrAlias": { "model": "past" }, "quotaInfo": { "remainingFraction": 0.5, "resetTime": "2026-07-09T00:00:00Z" } },
                        { "modelOrAlias": { "model": "future" }, "quotaInfo": { "remainingFraction": 0.6, "resetTime": "2026-07-11T00:00:00Z" } },
                        { "label": "Missing identity", "quotaInfo": { "remainingFraction": 0.7, "resetTime": "bogus" } }
                    ]
                }
            }
        }"#;
        let windows = parse_user_status(body, now).unwrap().windows;
        assert_eq!(windows.len(), 7);
        for (index, expected) in [
            (0, Some("missingReset")),
            (1, Some("missingReset")),
            (2, Some("invalidEvidence")),
            (3, Some("invalidEvidence")),
            (4, Some("invalidEvidence")),
            (6, Some("windowIdentity")),
        ] {
            assert_eq!(windows[index].pace_reason_for_test(), expected);
        }
        assert_eq!(windows[5].pace_reason_for_test(), None);
        assert_eq!(windows[5].pace_window_key_for_test(), Some("model.future.v1"));
        let future_wire = serde_json::to_value(&windows[5]).unwrap();
        assert_eq!(future_wire["paceStatus"]["state"], "learningDuration");
        assert_eq!(future_wire["paceStatus"]["durationSource"], "observed");

        let subsecond_now = DateTime::parse_from_rfc3339("2026-07-10T00:00:00.000500Z")
            .unwrap()
            .with_timezone(&Utc);
        let subsecond = parse_user_status(
            r#"{
                "userStatus": {
                    "cascadeModelConfigData": {
                        "clientModelConfigs": [{
                            "modelOrAlias": { "model": "subsecond" },
                            "quotaInfo": {
                                "remainingFraction": 0.5,
                                "resetTime": "2026-07-10T00:00:00.000900Z"
                            }
                        }]
                    }
                }
            }"#,
            subsecond_now,
        )
        .unwrap();
        let wire = serde_json::to_value(&subsecond.windows[0]).unwrap();
        assert_eq!(wire["paceStatus"]["state"], "learningDuration");
        assert_eq!(wire["paceStatus"]["durationSource"], "observed");
    }

    #[test]
    fn verified_remote_user_info_shares_authoritative_scope_with_local_email() {
        let local_identity = AgentIdentity {
            email: Some(" Same.User@Example.COM ".to_string()),
            plan: None,
        };
        let local_scope =
            resolve_local_account_scope_with(Some(&local_identity), fixture_authoritative_scope)
                .unwrap();
        let remote_scope = resolve_google_user_info_response_with(
            reqwest::StatusCode::OK,
            r#"{"email":"same.user@example.com","verified_email":true}"#,
            fixture_authoritative_scope,
        )
        .unwrap();
        let other_remote_scope = resolve_google_user_info_response_with(
            reqwest::StatusCode::OK,
            r#"{"email":"other.user@example.com","verified_email":true}"#,
            fixture_authoritative_scope,
        )
        .unwrap();

        assert_eq!(remote_scope, local_scope);
        assert_ne!(other_remote_scope, local_scope);
    }

    #[tokio::test]
    async fn remote_user_info_uses_refreshed_final_token_and_ignores_cached_identity() {
        let scope_store = TestRefreshScope::new("antigravity", "antigravity-userinfo-token");
        let lineage_scope = scope_store
            .resolve_current(
                "google-oauth-creds",
                "fixture-location",
                b"fixture-refresh-marker",
            )
            .unwrap();
        let now = DateTime::parse_from_rfc3339("2026-07-17T00:00:00Z")
            .unwrap()
            .with_timezone(&Utc);
        let credentials = json!({
            "access_token": "fixture-stale-access",
            "refresh_token": "fixture-refresh-marker",
            "expiry_date": 0,
            "id_token": "stale-id-token-must-not-authorize-scope",
            "active_email": "stale-active-email@example.com"
        });
        let final_token_seen = std::cell::Cell::new(false);
        let verified_email_seen = std::cell::Cell::new(false);
        let auth = prepare_remote_auth_with(
            &credentials,
            now,
            || {
                std::future::ready(Ok((
                    json!({"access_token": "fixture-final-access"}),
                    "fixture-final-access".to_string(),
                    Ok(lineage_scope),
                )))
            },
            |access_token| {
                final_token_seen.set(access_token == "fixture-final-access");
                let scope = resolve_google_user_info_response_with(
                    reqwest::StatusCode::OK,
                    r#"{"email":"fresh.user@example.com","verified_email":true}"#,
                    |email| {
                        verified_email_seen.set(email == "fresh.user@example.com");
                        fixture_authoritative_scope(email)
                    },
                );
                std::future::ready(scope)
            },
        )
        .await
        .unwrap();

        assert!(final_token_seen.get());
        assert!(verified_email_seen.get());
        assert!(auth.access_token == "fixture-final-access");
        assert!(auth.account_scope.is_ok());
        scope_store.cleanup();
    }

    #[tokio::test]
    async fn untrusted_user_info_keeps_quota_and_never_falls_back_to_lineage() {
        let scope_store = TestRefreshScope::new("antigravity", "antigravity-userinfo-fail");
        let lineage_scope = scope_store
            .resolve_current(
                "google-oauth-creds",
                "fixture-location",
                b"fixture-refresh-marker",
            )
            .unwrap();
        let now = DateTime::parse_from_rfc3339("2026-07-17T00:00:00Z")
            .unwrap()
            .with_timezone(&Utc);
        let fixtures = [
            (
                reqwest::StatusCode::OK,
                r#"{"verified_email":true,"raw":"raw-response-must-not-leak"}"#,
            ),
            (
                reqwest::StatusCode::OK,
                r#"{"email":" \t ","verified_email":true,"raw":"raw-response-must-not-leak"}"#,
            ),
            (
                reqwest::StatusCode::OK,
                r#"{"email":"raw-email-must-not-leak@example.com","verified_email":false}"#,
            ),
            (
                reqwest::StatusCode::OK,
                r#"{"email":"raw-email-must-not-leak@example.com"}"#,
            ),
            (
                reqwest::StatusCode::OK,
                r#"{"email":"raw-email-must-not-leak@example.com","verified_email":"true","raw":"raw-response-must-not-leak"}"#,
            ),
            (
                reqwest::StatusCode::UNAUTHORIZED,
                r#"{"email":"raw-email-must-not-leak@example.com","verified_email":true}"#,
            ),
            (
                reqwest::StatusCode::FORBIDDEN,
                r#"{"email":"raw-email-must-not-leak@example.com","verified_email":true}"#,
            ),
            (
                reqwest::StatusCode::INTERNAL_SERVER_ERROR,
                "raw-response-must-not-leak",
            ),
        ];

        for (status, body) in fixtures {
            let credentials = json!({
                "access_token": "fixture-stale-access",
                "refresh_token": "fixture-refresh-marker",
                "expiry_date": 0,
                "id_token": "stale-id-token-must-not-authorize-scope"
            });
            let resolver_calls = std::cell::Cell::new(0);
            let auth: RemoteAuth<AccountScope> = prepare_remote_auth_with(
                &credentials,
                now,
                || {
                    std::future::ready(Ok((
                        json!({"access_token": "fixture-final-access"}),
                        "fixture-final-access".to_string(),
                        Ok(lineage_scope.clone()),
                    )))
                },
                |_| {
                    let scope = resolve_google_user_info_response_with(status, body, |_| {
                        resolver_calls.set(resolver_calls.get() + 1);
                        Err(AccountScopeError::MetadataWrite)
                    });
                    std::future::ready(scope)
                },
            )
            .await
            .unwrap();

            assert_eq!(resolver_calls.get(), 0);
            assert_eq!(
                auth.account_scope,
                Err(AccountScopeError::NoTrustedEvidence)
            );
            let error = auth.account_scope.as_ref().unwrap_err();
            let rendered = format!("{error} {error:?}");
            for sensitive in [
                "fixture-final-access",
                "fixture-refresh-marker",
                "stale-id-token-must-not-authorize-scope",
                "raw-email-must-not-leak@example.com",
                "raw-response-must-not-leak",
            ] {
                assert!(!rendered.contains(sensitive));
            }

            let window = quota_window(
                "Gemini fixture".to_string(),
                0.5,
                None,
                false,
                now,
                "model.fixture.v1".to_string(),
                Some("model.fixture.v1".to_string()),
            )
            .unwrap();
            let fetched = remote_fetched(
                Some("Paid".to_string()),
                auth.account_scope,
                vec![window],
            );
            assert_eq!(fetched.source, "oauth");
            assert_eq!(fetched.windows.len(), 1);
            assert!((fetched.windows[0].remaining_for_test() - 50.0).abs() < 0.01);
            assert_eq!(
                fetched.account_scope,
                Err(AccountScopeError::NoTrustedEvidence)
            );
            assert_eq!(
                fetched
                    .identity
                    .as_ref()
                    .and_then(|identity| identity.email.as_deref()),
                None
            );
        }
        scope_store.cleanup();
    }

    #[test]
    fn atomic_credential_write_replaces_existing_without_orphan_temp() {
        let scope = TestRefreshScope::new("antigravity", "antigravity-credential-write");
        let path = scope.root().join("antigravity/oauth_creds.json");
        let directory = path.parent().unwrap();
        std::fs::create_dir_all(directory).unwrap();
        std::fs::write(&path, b"old credentials").unwrap();

        let credentials = json!({
            "access_token": "replacement-access",
            "refresh_token": "replacement-refresh"
        });
        let expected = serde_json::to_vec_pretty(&credentials).unwrap();
        write_creds_atomic(&path, &credentials).unwrap();

        assert_eq!(std::fs::read(&path).unwrap(), expected);
        assert!(!std::fs::read_dir(directory).unwrap().any(|entry| {
            entry
                .ok()
                .is_some_and(|entry| {
                    entry
                        .file_name()
                        .to_string_lossy()
                        .starts_with(".oauth_creds.json.tokenbar.")
                })
        }));
        scope.cleanup();
    }

    #[test]
    fn resolves_remote_plan_from_tier() {
        assert_eq!(resolve_remote_plan(&json!({"currentTier":{"id":"free-tier"}})).as_deref(), Some("Free"));
        assert_eq!(resolve_remote_plan(&json!({"planInfo":{"planType":"standard"}})).as_deref(), Some("Standard"));
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

    async fn test_refresh_response(refresh_token: String) -> Result<Value, String> {
        assert_eq!(refresh_token, "antigravity-old-refresh");
        Ok(json!({
            "access_token": "antigravity-new-access",
            "refresh_token": "antigravity-new-refresh",
            "expires_in": 3600
        }))
    }

    fn setup_refresh(tag: &str) -> (TestRefreshScope, PathBuf, AccountScope, Vec<u8>, String) {
        let scope = TestRefreshScope::new("antigravity", tag);
        let path = scope.root().join("antigravity/oauth_creds.json");
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(
            &path,
            serde_json::to_vec_pretty(&json!({
                "access_token": "antigravity-old-access",
                "refresh_token": "antigravity-old-refresh",
                "expiry_date": 0
            }))
            .unwrap(),
        )
        .unwrap();
        let location = remote_scope_location(&path).unwrap();
        let old_scope = scope
            .resolve_current("google-oauth-creds", &location, b"antigravity-old-refresh")
            .unwrap();
        let metadata = scope.metadata_bytes();
        (scope, path, old_scope, metadata, location)
    }

    async fn run_refresh(
        scope: &TestRefreshScope,
        path: &Path,
        crash: Option<RefreshCheckpoint>,
    ) -> Result<(Value, String, Result<AccountScope, AccountScopeError>), String> {
        let now = DateTime::parse_from_rfc3339("2026-07-17T00:00:00Z")
            .unwrap()
            .with_timezone(&Utc);
        refresh_access_token_with(
            path,
            now,
            scope,
            test_refresh_response,
            |creds| write_creds_atomic(path, creds),
            checkpoint_at(crash),
        )
        .await
    }

    fn stored_refresh_token(path: &Path) -> String {
        let credentials = load_remote_credentials(path).unwrap();
        std::str::from_utf8(remote_refresh_marker(&credentials).unwrap())
            .unwrap()
            .to_string()
    }

    #[tokio::test]
    async fn refresh_crash_boundaries_and_scope_gate_use_production_sequence() {
        for boundary in [
            RefreshCheckpoint::Reloaded,
            RefreshCheckpoint::NetworkReturned,
            RefreshCheckpoint::MetadataHandled,
            RefreshCheckpoint::CredentialsPersisted,
        ] {
            let (scope, path, old_scope, before, location) = setup_refresh("antigravity-crash");
            assert_eq!(
                run_refresh(&scope, &path, Some(boundary))
                    .await
                    .unwrap_err(),
                "injected crash"
            );
            assert_eq!(
                stored_refresh_token(&path),
                if boundary == RefreshCheckpoint::CredentialsPersisted {
                    "antigravity-new-refresh"
                } else {
                    "antigravity-old-refresh"
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
                        .resolve_current(
                            "google-oauth-creds",
                            &location,
                            b"antigravity-old-refresh",
                        )
                        .unwrap(),
                    old_scope
                );
                assert_eq!(
                    scope
                        .resolve_current(
                            "google-oauth-creds",
                            &location,
                            b"antigravity-new-refresh",
                        )
                        .unwrap(),
                    old_scope
                );
            }
            scope.cleanup();
        }

        let (scope, path, old_scope, before, location) = setup_refresh("antigravity-metadata-fail");
        scope.fail_metadata_save();
        let (refreshed, access_token, scope_outcome) =
            run_refresh(&scope, &path, None).await.unwrap();
        assert_eq!(access_token, "antigravity-new-access");
        assert_eq!(remote_access_token(&refreshed).unwrap(), access_token);
        assert_eq!(scope_outcome, Err(AccountScopeError::MetadataWrite));
        assert_eq!(scope.metadata_bytes(), before);
        assert_eq!(stored_refresh_token(&path), "antigravity-old-refresh");
        assert_eq!(
            scope
                .resolve_current("google-oauth-creds", &location, b"antigravity-old-refresh")
                .unwrap(),
            old_scope
        );
        scope.cleanup();

        let (scope, path, _old_scope, before, _) =
            setup_refresh("antigravity-metadata-fail-unchanged");
        scope.fail_metadata_save();
        let now = DateTime::parse_from_rfc3339("2026-07-17T00:00:00Z")
            .unwrap()
            .with_timezone(&Utc);
        let save_path = path.clone();
        let (refreshed, access_token, scope_outcome) = refresh_access_token_with(
            &path,
            now,
            &scope,
            |refresh_token| async move {
                assert_eq!(refresh_token, "antigravity-old-refresh");
                Ok(json!({
                    "access_token": "antigravity-new-access",
                    "expires_in": 3600
                }))
            },
            move |credentials| write_creds_atomic(&save_path, credentials),
            checkpoint_at(None),
        )
        .await
        .unwrap();
        assert_eq!(scope_outcome, Err(AccountScopeError::MetadataWrite));
        assert_eq!(scope.metadata_bytes(), before);
        assert_eq!(access_token, "antigravity-new-access");
        assert_eq!(remote_access_token(&refreshed).unwrap(), access_token);
        let persisted = load_remote_credentials(&path).unwrap();
        assert_eq!(remote_access_token(&persisted).unwrap(), access_token);
        assert_eq!(stored_refresh_token(&path), "antigravity-old-refresh");
        scope.cleanup();

        let (scope, path, old_scope, _, location) = setup_refresh("antigravity-success");
        let (_, _, scope_outcome) = run_refresh(&scope, &path, None).await.unwrap();
        assert_eq!(scope_outcome.unwrap(), old_scope);
        assert_eq!(
            scope
                .resolve_current("google-oauth-creds", &location, b"antigravity-new-refresh")
                .unwrap(),
            old_scope
        );
        scope.cleanup();
    }
}
