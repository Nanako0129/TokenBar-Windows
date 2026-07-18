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

pub(crate) struct GitHubCopilotCredential {
    pub(crate) request_token: String,
    pub(crate) marker: Vec<u8>,
    pub(crate) semantic_source: &'static str,
    pub(crate) canonical_location: String,
}

/// The durable GitHub OAuth credential opencode stored for its Copilot login.
/// The trimmed refresh token is preferred; a trimmed access token is the fallback.
pub(crate) fn github_copilot_credential() -> Option<GitHubCopilotCredential> {
    let path = auth_path()?;
    let raw = std::fs::read_to_string(&path).ok()?;
    let json = serde_json::from_str::<serde_json::Value>(&raw).ok()?;
    github_copilot_credential_from(&path, &json)
}

pub(crate) fn github_copilot_credential_from(
    path: &std::path::Path,
    json: &serde_json::Value,
) -> Option<GitHubCopilotCredential> {
    let entry = json.get("github-copilot")?;
    if entry.get("type").and_then(|t| t.as_str()) != Some("oauth") {
        return None;
    }
    let token = ["refresh", "access"]
        .into_iter()
        .filter_map(|key| entry.get(key).and_then(serde_json::Value::as_str))
        .map(str::trim)
        .find(|token| !token.is_empty())?
        .to_string();
    Some(GitHubCopilotCredential {
        request_token: token.clone(),
        marker: token.into_bytes(),
        semantic_source: "opencode-auth-json",
        canonical_location: crate::agent_account_scope::canonical_file_location(
            path,
            Some("github-copilot"),
        )
        .ok()?,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    use std::sync::atomic::{AtomicU64, Ordering};

    static TEMP_COUNTER: AtomicU64 = AtomicU64::new(0);

    fn temp_auth_path(tag: &str) -> PathBuf {
        let root = std::env::temp_dir().join(format!(
            "tb-opencode-{tag}-{}-{}",
            std::process::id(),
            TEMP_COUNTER.fetch_add(1, Ordering::Relaxed)
        ));
        std::fs::create_dir_all(&root).unwrap();
        root.join("auth.json")
    }

    fn fixture(path: &std::path::Path, json: &serde_json::Value) -> Option<GitHubCopilotCredential> {
        std::fs::write(path, serde_json::to_vec(json).unwrap()).unwrap();
        github_copilot_credential_from(path, json)
    }

    #[test]
    fn labels_oauth_providers_only() {
        assert_eq!(subscription_label("openai"), "Codex");
        assert_eq!(subscription_label("github-copilot"), "Copilot");
        assert_eq!(subscription_label("anthropic"), "Claude");
        assert_eq!(subscription_label("minimax-coding-plan"), "Minimax-coding-plan");
    }

    #[test]
    fn copilot_credential_uses_trimmed_refresh_then_access() {
        let path = temp_auth_path("token-precedence");
        let cases = [
            (
                json!({"github-copilot": {
                    "type": "oauth", "refresh": " refresh-token ", "access": "access-token"
                }}),
                Some("refresh-token"),
            ),
            (
                json!({"github-copilot": {
                    "type": "oauth", "refresh": "  ", "access": " access-token "
                }}),
                Some("access-token"),
            ),
            (
                json!({"github-copilot": {
                    "type": "oauth", "refresh": null, "access": "\t\n"
                }}),
                None,
            ),
        ];
        for (json, expected) in cases {
            let credential = fixture(&path, &json);
            match expected {
                Some(expected) => {
                    let credential = credential.unwrap();
                    assert_eq!(credential.request_token, expected);
                    assert_eq!(credential.marker, expected.as_bytes());
                }
                None => assert!(credential.is_none()),
            }
        }
        let _ = std::fs::remove_dir_all(path.parent().unwrap());
    }

    #[test]
    fn copilot_credential_requires_exact_oauth_entry_and_scopes_location() {
        let path = temp_auth_path("exact-entry");
        let credential = fixture(
            &path,
            &json!({
                "github-copilot": {"type": "oauth", "refresh": " exact-token "},
                "copilot": {"type": "oauth", "refresh": "sibling-token"},
                "GitHub-Copilot": {"type": "oauth", "refresh": "case-token"},
                "github-copilot-enterprise": {"type": "oauth", "refresh": "lookalike-token"},
                "github": {"type": "oauth", "refresh": "foreign-token"}
            }),
        )
        .unwrap();
        assert_eq!(credential.request_token, "exact-token");
        assert_eq!(credential.semantic_source, "opencode-auth-json");
        assert_eq!(
            credential.canonical_location,
            crate::agent_account_scope::canonical_file_location(&path, Some("github-copilot"))
                .unwrap()
        );
        assert_ne!(
            credential.canonical_location,
            crate::agent_account_scope::canonical_file_location(&path, None).unwrap()
        );
        assert_ne!(
            credential.canonical_location,
            crate::agent_account_scope::canonical_file_location(&path, Some("copilot")).unwrap()
        );
        assert!(fixture(
            &path,
            &json!({
                "github-copilot": {"type": "api", "refresh": "wrong-type"},
                "copilot": {"type": "oauth", "refresh": "sibling-token"},
                "GitHub-Copilot": {"type": "oauth", "refresh": "case-token"}
            })
        )
        .is_none());
        let other_path = temp_auth_path("other-location");
        let other = fixture(
            &other_path,
            &json!({"github-copilot": {"type": "oauth", "refresh": "exact-token"}}),
        )
        .unwrap();
        assert_ne!(credential.canonical_location, other.canonical_location);
        let _ = std::fs::remove_dir_all(path.parent().unwrap());
        let _ = std::fs::remove_dir_all(other_path.parent().unwrap());
    }
}
