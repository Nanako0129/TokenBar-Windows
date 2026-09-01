//! Window usage for the quota lens: per-message rows inside an absolute interval.
//!
//! Returns the messages inside an absolute [from, until) window, one row each.
//! No bucketing: a quota window is a tiny slice of history, so the consumer
//! folds it however the UI wants without another round trip. Attribution is a
//! C#-side declaration, so it is deliberately NOT applied here.
//!
//! Ported from TokenBar-Native's `window_usage.rs` (crates/tb_core_ffi, macOS)
//! with the cache and minute quantisation kept — they exist because a full
//! window scan is not a call any UI thread can make. macOS's own probe
//! records 14.93 days and 109,278 messages at 67 seconds. Windows differs
//! from the macOS source in the cache mechanics: this crate has no
//! generation-gated root registry (`root_generation`/`invalidate_scan_caches`
//! on macOS), so publication here is unconditional — the same clear-then-
//! insert-one-entry shape, without a generation check — and it reuses the
//! source-context token probe `graph_cached` already uses instead of the
//! plain (non-source-context) probe the macOS module calls.

use serde::Serialize;
use serde_json::Value;
use std::collections::HashMap;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{LazyLock, Mutex};
use std::time::{Duration, Instant};

pub(crate) type CacheKey = (i64, i64);
type CacheEntry = (Instant, u64, Value);

const MINUTE_MS: i64 = 60_000;
static SCAN_COUNT: AtomicUsize = AtomicUsize::new(0);

/// key → (computed-at, source token, mapped window payload). Same role as
/// `GRAPH_CACHE` in lib.rs. `until_ms` is quantised to the minute in
/// `cache_key`, so a consumer polling every 60s with `until = now` mints a
/// key it will never ask for again — nothing in this map is ever a stale
/// answer being reused past its minute, only a fresh one being re-served
/// inside it. `publish` therefore clears before inserting: one entry, not a
/// history, so a window scan (tens of seconds on a large store) left resident
/// forever is one window's messages, not one per minute the lens stayed open.
static WINDOW_USAGE_CACHE: LazyLock<Mutex<HashMap<CacheKey, CacheEntry>>> =
    LazyLock::new(|| Mutex::new(HashMap::new()));

pub(crate) fn cache_key(from_ms: i64, until_ms: i64) -> CacheKey {
    // Saturation keeps the extreme negative i64 input from overflowing while
    // preserving the minute floor for normal timestamps.
    (
        from_ms,
        until_ms.saturating_sub(until_ms.rem_euclid(MINUTE_MS)),
    )
}

// Only read from tests (asserting the cache actually avoids a re-scan); no
// production consumer needs the count.
#[cfg(test)]
pub(crate) fn scan_count() -> usize {
    SCAN_COUNT.load(Ordering::Relaxed)
}

pub(crate) fn cached(
    context: &crate::LocalSourceContext,
    from_ms: i64,
    until_ms: i64,
) -> Result<Value, String> {
    let key = cache_key(from_ms, until_ms);
    let cached = {
        let cache = WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        cache.get(&key).map(|(at, token, data)| {
            (
                at.elapsed() <= Duration::from_secs(crate::ONESHOT_MAX_AGE_SECS),
                *token,
                data.clone(),
            )
        })
    };
    let Some((fresh_enough, token, data)) = cached else {
        return compute(context, from_ms, until_ms, key);
    };
    if fresh_enough {
        return Ok(data);
    }

    // Probe with the cache lock released, matching graph_cached. An unchanged
    // source only refreshes the timestamp; it does not re-run the scan.
    if let Ok(probe_token) = tokscale_core::local_source_change_token_with_source_context(
        context.resolved(),
        &context.parse_options(None, None),
    ) {
        if probe_token == token {
            let mut cache =
                WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
            if let Some(entry) = cache.get_mut(&key) {
                entry.0 = Instant::now();
            }
            return Ok(data);
        }
    }

    compute(context, from_ms, until_ms, key)
}

/// Held across a scan so overlapping callers share one, instead of each
/// starting its own.
///
/// A plain mutex rather than a per-key future: the second caller waits, then
/// re-checks the cache and finds what the first published. Scans are CPU-bound
/// and already serialise inside tokscale-core's rayon pool, so making them
/// queue costs nothing that running them concurrently was buying.
static COMPUTE: Mutex<()> = Mutex::new(());

fn compute(
    context: &crate::LocalSourceContext,
    from_ms: i64,
    until_ms: i64,
    key: CacheKey,
) -> Result<Value, String> {
    let _serialised = COMPUTE.lock().unwrap_or_else(|p| p.into_inner());
    // Re-check under the lock. A caller that queued behind another's scan is
    // asking a question that scan may have just answered; running a second one
    // to produce the same bytes is the duplicate this lock exists to remove.
    {
        let cache = WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        if let Some((at, _, data)) = cache.get(&key) {
            if at.elapsed() <= Duration::from_secs(crate::ONESHOT_MAX_AGE_SECS) {
                return Ok(data.clone());
            }
        }
    }
    let token = tokscale_core::local_source_change_token_with_source_context(
        context.resolved(),
        &context.parse_options(None, None),
    )
    .unwrap_or(0);
    let data = run(context, from_ms, until_ms)?;
    publish(key, (Instant::now(), token, data.clone()));
    Ok(data)
}

/// Cache a freshly scanned window. Unlike the macOS source, there is no root
/// generation to check here (this crate has no dynamic root registry), so
/// publication is unconditional — clear then insert, keeping only the newest
/// entry (see the `WINDOW_USAGE_CACHE` doc comment for why).
fn publish(key: CacheKey, entry: CacheEntry) {
    let mut cache = WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    cache.clear();
    cache.insert(key, entry);
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct Message {
    timestamp: i64,
    client: String,
    provider_id: String,
    model_id: String,
    input: i64,
    output: i64,
    cache_read: i64,
    cache_write: i64,
    reasoning: i64,
    cost: f64,
    is_turn_start: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct WindowData {
    messages: Vec<Message>,
    undated_count: u32,
    processing_time_ms: u32,
}

pub(crate) fn run(
    context: &crate::LocalSourceContext,
    from_ms: i64,
    until_ms: i64,
) -> Result<Value, String> {
    let options = context.report_options(None, None);

    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .map_err(|e| format!("build runtime: {}", e))?;
    SCAN_COUNT.fetch_add(1, Ordering::Relaxed);
    let usage = runtime.block_on(tokscale_core::get_window_usage_with_source_context(
        context.resolved(),
        options,
        from_ms,
        until_ms,
    ))?;

    let data = WindowData {
        messages: usage
            .messages
            .into_iter()
            .map(|m| Message {
                timestamp: m.timestamp,
                client: m.client,
                provider_id: m.provider_id,
                model_id: m.model_id,
                input: m.input,
                output: m.output,
                cache_read: m.cache_read,
                cache_write: m.cache_write,
                reasoning: m.reasoning,
                cost: m.cost,
                is_turn_start: m.is_turn_start,
            })
            .collect(),
        undated_count: usage.undated_count,
        processing_time_ms: usage.processing_time_ms,
    };
    serde_json::to_value(data).map_err(|e| format!("serialize window usage: {}", e))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::{LazyLock, Mutex as StdMutex};

    static TEST_LOCK: LazyLock<StdMutex<()>> = LazyLock::new(|| StdMutex::new(()));

    fn test_context(label: &str) -> crate::LocalSourceContext {
        let home = std::env::temp_dir().join(format!(
            "tokenbar-window-usage-{label}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        crate::LocalSourceContext::capture(Some(home), false, tokscale_core::ScannerSettings::default())
            .unwrap()
    }

    #[test]
    fn quantised_window_calls_scan_once() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let from_ms = 1_700_000_000_000;
        let until_a = 1_700_000_060_001;
        let until_b = 1_700_000_060_999;
        let key = cache_key(from_ms, until_a);
        WINDOW_USAGE_CACHE
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .remove(&key);
        let before = scan_count();
        let context = test_context("quantised");

        cached(&context, from_ms, until_a).expect("first window scan");
        cached(&context, from_ms, until_b).expect("quantised cache hit");

        assert_eq!(cache_key(from_ms, until_a), cache_key(from_ms, until_b));
        assert_eq!(scan_count(), before + 1);
        // If until_ms is no longer quantised, these two calls use different
        // keys and this assertion catches the accidental 0%-hit-rate change.
    }

    #[test]
    fn cache_keeps_only_the_newest_window() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let context = test_context("newest-only");
        let from_ms = 1_700_000_000_000;
        WINDOW_USAGE_CACHE
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .clear();

        // Three different minutes: exactly what a poll every 60s produces, and
        // what would otherwise leave three whole scans resident for ever.
        for minute in 0..3 {
            cached(&context, from_ms, from_ms + 60_000 * (minute + 1)).expect("window scan");
        }
        let cache = WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        assert_eq!(
            cache.len(),
            1,
            "the map is a process-lifetime static with no eviction, so anything \
             it keeps beyond the newest entry is kept until the app exits"
        );
    }

    #[test]
    fn different_minute_uses_different_key() {
        assert_ne!(
            cache_key(1_700_000_000_000, 1_700_000_060_001),
            cache_key(1_700_000_000_000, 1_700_000_120_001)
        );
    }

    #[test]
    fn empty_range_returns_empty_list_not_an_error() {
        let context = test_context("empty-range");
        // from > until: no message's timestamp can ever satisfy the filter.
        let value = run(&context, 1_700_000_060_000, 1_700_000_000_000).expect("empty window");
        assert_eq!(value["messages"].as_array().unwrap().len(), 0);
    }
}
