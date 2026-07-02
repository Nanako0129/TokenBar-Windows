//! Detect which subscription-type providers opencode is authenticated against.
//!
//! opencode can sign in to providers via OAuth (a shared subscription, e.g.
//! "Sign in with ChatGPT" = the Codex/ChatGPT plan) or via API keys (metered).
//! Its `~/.local/share/opencode/auth.json` records each provider with a `type`.
//! We surface the `type: "oauth"` providers so the user can see which agent
//! subscriptions opencode also draws on (its usage counts against those plans).

use serde::Deserialize;
use std::collections::BTreeMap;
use std::path::PathBuf;

#[derive(Debug, Deserialize)]
struct AuthEntry {
    #[serde(default)]
    r#type: Option<String>,
}

/// Friendly subscription labels for opencode's OAuth providers, in a stable order.
pub fn detect_subscriptions() -> Vec<String> {
    let Some(path) = auth_path() else {
        return Vec::new();
    };
    let Ok(raw) = std::fs::read_to_string(path) else {
        return Vec::new();
    };
    let Ok(entries) = serde_json::from_str::<BTreeMap<String, AuthEntry>>(&raw) else {
        return Vec::new();
    };
    let mut labels: Vec<String> = entries
        .into_iter()
        .filter(|(_, entry)| {
            entry
                .r#type
                .as_deref()
                .is_some_and(|t| t.eq_ignore_ascii_case("oauth"))
        })
        .map(|(provider, _)| subscription_label(&provider))
        .collect();
    labels.sort();
    labels.dedup();
    labels
}

fn subscription_label(provider: &str) -> String {
    match provider.to_lowercase().as_str() {
        "openai" => "Codex".to_string(),
        "anthropic" => "Claude".to_string(),
        "github-copilot" | "copilot" => "Copilot".to_string(),
        "google" | "gemini" => "Gemini".to_string(),
        other => {
            let mut chars = other.chars();
            match chars.next() {
                Some(first) => format!("{}{}", first.to_uppercase(), chars.as_str()),
                None => provider.to_string(),
            }
        }
    }
}

fn auth_path() -> Option<PathBuf> {
    std::env::var_os("HOME")
        .map(|home| PathBuf::from(home).join(".local/share/opencode/auth.json"))
}

/// The durable GitHub OAuth token opencode stored for its github-copilot login
/// (its `refresh` field), used to query Copilot quota. `None` if opencode isn't
/// authed against Copilot.
pub fn github_copilot_token() -> Option<String> {
    let raw = std::fs::read_to_string(auth_path()?).ok()?;
    let json = serde_json::from_str::<serde_json::Value>(&raw).ok()?;
    let entry = json.get("github-copilot")?;
    if entry.get("type").and_then(|t| t.as_str()) != Some("oauth") {
        return None;
    }
    entry
        .get("refresh")
        .or_else(|| entry.get("access"))
        .and_then(|t| t.as_str())
        .map(str::trim)
        .filter(|t| !t.is_empty())
        .map(str::to_string)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn labels_oauth_providers_only() {
        assert_eq!(subscription_label("openai"), "Codex");
        assert_eq!(subscription_label("github-copilot"), "Copilot");
        assert_eq!(subscription_label("anthropic"), "Claude");
        assert_eq!(subscription_label("minimax-coding-plan"), "Minimax-coding-plan");
    }
}
