//! Per-agent (sub-agent) usage breakdown for the popover, backed by
//! tokscale-core's `get_agents_report`. Mirrors tokscale's TUI "Agents" view,
//! where named sub-agents are ranked by cost; messages with no agent
//! attribution fold into a single "Main" bucket so every message is accounted
//! for.
//!
//! `get_agents_report` folds the SAME deduped, per-client-gated, priced stream
//! as the model/graph/hourly reports (`scan_messages_streaming`), so the agents
//! report agrees with them on copilot/codebuff/kimi/cursor/warp totals
//! (issue #6). Like the other reports, it drives the async core on a
//! short-lived current-thread runtime (callers run it inside `spawn_blocking`)
//! and maps the result onto a camelCase JSON shape the frontend consumes.

use serde::Serialize;
use serde_json::Value;

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AgentEntry {
    agent: String,
    clients: Vec<String>,
    input: i64,
    output: i64,
    cache_read: i64,
    cache_write: i64,
    reasoning: i64,
    total: i64,
    cost: f64,
    messages: i32,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AgentsReportData {
    entries: Vec<AgentEntry>,
    total_cost: f64,
    total_messages: i32,
}

/// Build the per-agent report for `year` (empty string = all time).
pub fn run(year: &str) -> Result<Value, String> {
    let year = normalize_year(year)?;

    let options = tokscale_core::ReportOptions {
        year,
        ..Default::default()
    };

    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .map_err(|e| format!("build runtime: {}", e))?;
    let report = runtime.block_on(tokscale_core::get_agents_report(options))?;

    let data = map_report(report);
    serde_json::to_value(data).map_err(|e| format!("serialize agents report: {}", e))
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

fn map_report(report: tokscale_core::AgentReport) -> AgentsReportData {
    AgentsReportData {
        entries: report
            .entries
            .into_iter()
            .map(|e| {
                // Single source of the `total` formula; it MUST stay identical
                // to the token-total used for the cost-then-total sort in
                // tokscale-core's get_agents_report. The core preserves order,
                // so this mapper does NOT re-sort.
                let total = e.input + e.output + e.cache_read + e.cache_write + e.reasoning;
                AgentEntry {
                    agent: e.agent,
                    clients: e.clients,
                    input: e.input,
                    output: e.output,
                    cache_read: e.cache_read,
                    cache_write: e.cache_write,
                    reasoning: e.reasoning,
                    total,
                    cost: e.cost,
                    messages: e.messages,
                }
            })
            .collect(),
        total_cost: report.total_cost,
        total_messages: report.total_messages,
    }
}
