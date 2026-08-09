//! Per-model usage breakdown for the popover, backed by tokscale-core's
//! `get_model_report`. Mirrors the design of tokscale's TUI "Models" view
//! (`crates/tokscale-cli/src/tui/ui/models.rs`): one row per exact
//! `(client, provider, model)` with the token breakdown, message count, cost,
//! and throughput (ms/1K), sorted by cost on the frontend.
//!
//! Like `usage_graph`, this drives the async core on a short-lived
//! current-thread runtime (callers run it inside `spawn_blocking`) and maps the
//! result onto a camelCase JSON shape the frontend consumes directly.

use serde::Serialize;
use serde_json::Value;

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ModelEntry {
    client: String,
    model: String,
    provider: String,
    input: i64,
    output: i64,
    cache_read: i64,
    cache_write: i64,
    reasoning: i64,
    total: i64,
    message_count: i32,
    cost: f64,
    /// Milliseconds per 1K tokens, when tokscale could time the model. `None`
    /// when no message in the rollup carried a usable duration.
    ms_per_1k_tokens: Option<f64>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ModelReportData {
    entries: Vec<ModelEntry>,
    total_input: i64,
    total_output: i64,
    total_cache_read: i64,
    total_cache_write: i64,
    total_messages: i32,
    total_cost: f64,
    /// Unix-seconds time the LiteLLM pricing dataset was last fetched from
    /// upstream (the on-disk cache write time). `None` before the first fetch.
    /// Surfaced as the "prices updated …" hint in the Models view.
    pricing_updated_at: Option<u64>,
}

/// Build the per-model report for `year` (empty string = all time).
pub(crate) fn run(context: &crate::LocalSourceContext, year: &str) -> Result<Value, String> {
    let year = normalize_year(year)?;
    let data = load_report(report_options(context, year))?;
    serde_json::to_value(data).map_err(|e| format!("serialize model report: {}", e))
}

fn report_options(
    context: &crate::LocalSourceContext,
    year: Option<String>,
) -> tokscale_core::ReportOptions {
    let mut options = context.report_options(year, None);
    // Group before the FFI boundary: ClientModel would comma-join providers and
    // C# cannot recover the original rows from that pre-aggregated value.
    options.group_by = tokscale_core::GroupBy::ClientProviderModel;
    options
}

fn load_report(options: tokscale_core::ReportOptions) -> Result<ModelReportData, String> {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .map_err(|e| format!("build runtime: {}", e))?;
    runtime
        .block_on(tokscale_core::get_model_report(options))
        .map(map_report)
}

fn normalize_year(year: &str) -> Result<Option<String>, String> {
    let trimmed = year.trim();
    if trimmed.is_empty() {
        return Ok(None);
    }
    if trimmed.len() == 4 && trimmed.chars().all(|c| c.is_ascii_digit()) {
        Ok(Some(trimmed.to_string()))
    } else {
        Err(format!("invalid year filter: {}", year))
    }
}

fn map_report(report: tokscale_core::ModelReport) -> ModelReportData {
    ModelReportData {
        entries: report
            .entries
            .into_iter()
            .map(|e| {
                // saturating_add so #766's i64::MAX-clamped buckets (corrupt
                // Antigravity DB) can't overflow this FFI-exposed total in
                // debug/release (see agents_report.rs's map_report for the
                // same pattern).
                let total = e
                    .input
                    .saturating_add(e.output)
                    .saturating_add(e.cache_read)
                    .saturating_add(e.cache_write)
                    .saturating_add(e.reasoning);
                ModelEntry {
                    client: e.client,
                    model: e.model,
                    provider: e.provider,
                    input: e.input,
                    output: e.output,
                    cache_read: e.cache_read,
                    cache_write: e.cache_write,
                    reasoning: e.reasoning,
                    total,
                    message_count: e.message_count,
                    cost: e.cost,
                    ms_per_1k_tokens: e.performance.ms_per_1k_tokens,
                }
            })
            .collect(),
        total_input: report.total_input,
        total_output: report.total_output,
        total_cache_read: report.total_cache_read,
        total_cache_write: report.total_cache_write,
        total_messages: report.total_messages,
        total_cost: report.total_cost,
        pricing_updated_at: tokscale_core::pricing::pricing_cached_at(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// #766 clamps corrupt Antigravity varints to `i64::MAX` per bucket. Two
    /// such buckets in one model entry must saturate the mapped `total`, not
    /// overflow it (a plain `+` panics in debug / wraps in release).
    fn entry(
        input: i64,
        output: i64,
        cache_read: i64,
        cache_write: i64,
        reasoning: i64,
    ) -> tokscale_core::ModelUsage {
        tokscale_core::ModelUsage {
            client: "antigravity_cli".to_string(),
            merged_clients: None,
            workspace_key: None,
            workspace_label: None,
            session_id: None,
            model: "gemini-3-pro".to_string(),
            provider: "antigravity".to_string(),
            input,
            output,
            cache_read,
            cache_write,
            reasoning,
            message_count: 1,
            cost: 0.0,
            performance: tokscale_core::ModelPerformance::default(),
        }
    }

    fn wrap(entries: Vec<tokscale_core::ModelUsage>) -> tokscale_core::ModelReport {
        tokscale_core::ModelReport {
            entries,
            total_input: 0,
            total_output: 0,
            total_cache_read: 0,
            total_cache_write: 0,
            total_messages: 1,
            total_cost: 0.0,
            processing_time_ms: 0,
        }
    }

    #[test]
    fn total_saturates_on_overlarge_buckets() {
        let report = wrap(vec![entry(i64::MAX, i64::MAX, 0, 0, 0)]);
        let mapped = map_report(report);
        assert_eq!(mapped.entries[0].total, i64::MAX);
    }

    /// The two-MAX-field case above only pins `input`/`output` into the fold.
    /// Pin the other three fields too: nonzero `input`/`output`/`reasoning`
    /// plus a clamped `cache_write`, so `cache_read`/`cache_write` inclusion
    /// is independently exercised, not just present-but-untested.
    #[test]
    fn total_saturates_when_cache_write_is_overlarge() {
        let report = wrap(vec![entry(10, 20, i64::MAX, i64::MAX, 5)]);
        let mapped = map_report(report);
        assert_eq!(mapped.entries[0].total, i64::MAX);
    }

    /// The saturating cases can't catch a dropped operand (another MAX field
    /// keeps the total at MAX), so pin every field's inclusion with distinct
    /// powers of two: omitting any one operand changes the exact sum.
    #[test]
    fn total_includes_every_token_field() {
        let report = wrap(vec![entry(1, 2, 4, 8, 16)]);
        let mapped = map_report(report);
        assert_eq!(mapped.entries[0].total, 31);
    }

    #[test]
    fn producer_groups_same_client_model_by_exact_provider() {
        const CHILD_HOME: &str = "TB_MODEL_REPORT_PROVIDER_FIXTURE_HOME";
        if let Some(home) = std::env::var_os(CHILD_HOME) {
            run_provider_fixture(std::path::Path::new(&home));
            return;
        }

        let root = std::env::temp_dir().join(format!(
            "tb-core-ffi-model-provider-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let fixture_dir = root.join(".mux/sessions/provider-fixture");
        std::fs::create_dir_all(&fixture_dir).unwrap();
        std::fs::write(
            fixture_dir.join("session-usage.json"),
            r#"{
                "version": 1,
                "byModel": {
                    "openai:same-model": {
                        "input": { "tokens": 1, "cost_usd": 0.1 },
                        "output": { "tokens": 2 }
                    },
                    "nvidia:same-model": {
                        "input": { "tokens": 3, "cost_usd": 0.2 },
                        "output": { "tokens": 4 }
                    },
                    "same-model": {
                        "input": { "tokens": 5, "cost_usd": 0.3 },
                        "output": { "tokens": 6 }
                    }
                },
                "lastRequest": { "timestamp": 1700000000000 }
            }"#,
        )
        .unwrap();

        let status = std::process::Command::new(std::env::current_exe().unwrap())
            .arg("model_report::tests::producer_groups_same_client_model_by_exact_provider")
            .arg("--exact")
            .env(CHILD_HOME, &root)
            .env("HOME", &root)
            .env("TOKSCALE_CONFIG_DIR", root.join("tokscale-config"))
            .env("TOKSCALE_PRICING_CACHE_ONLY", "1")
            .status()
            .unwrap();
        std::fs::remove_dir_all(&root).unwrap();
        assert!(status.success(), "isolated provider fixture failed");
    }

    fn run_provider_fixture(home: &std::path::Path) {
        let context = crate::LocalSourceContext {
            home_dir: Some(home.to_path_buf()),
        };
        let mut options = report_options(&context, None);
        options.use_env_roots = false;
        options.clients = Some(vec!["mux".to_string()]);
        assert_eq!(
            options.group_by,
            tokscale_core::GroupBy::ClientProviderModel
        );

        let report = load_report(options).unwrap();
        assert_eq!(report.entries.len(), 3);
        assert!(report.entries.iter().all(|entry| entry.client == "mux"
            && entry.model == "same-model"
            && !entry.provider.contains(',')));

        let by_provider: std::collections::HashMap<_, _> = report
            .entries
            .iter()
            .map(|entry| (entry.provider.as_str(), entry))
            .collect();
        assert_eq!(
            (
                by_provider["openai"].input,
                by_provider["openai"].output,
                by_provider["openai"].total,
                by_provider["openai"].message_count,
            ),
            (1, 2, 3, 1)
        );
        assert_eq!(
            (
                by_provider["nvidia"].input,
                by_provider["nvidia"].output,
                by_provider["nvidia"].total,
                by_provider["nvidia"].message_count,
            ),
            (3, 4, 7, 1)
        );
        assert_eq!(
            (
                by_provider[""].input,
                by_provider[""].output,
                by_provider[""].total,
                by_provider[""].message_count,
            ),
            (5, 6, 11, 1)
        );
        assert!((by_provider["openai"].cost - 0.1).abs() < f64::EPSILON);
        assert!((by_provider["nvidia"].cost - 0.2).abs() < f64::EPSILON);
        assert!((by_provider[""].cost - 0.3).abs() < f64::EPSILON);
        assert_eq!(
            report.entries.iter().map(|entry| entry.total).sum::<i64>(),
            21
        );
        assert_eq!(report.total_input, 9);
        assert_eq!(report.total_output, 12);
        assert_eq!(report.total_messages, 3);
        assert!((report.total_cost - 0.6).abs() < f64::EPSILON);
    }
}
