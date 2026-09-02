//! Window usage for the quota lens: per-message rows inside an absolute interval.
//!
//! Returns the messages inside an absolute [from, until) window, one row each.
//! No bucketing: a quota window is a tiny slice of history, so the consumer
//! folds it however the UI wants without another round trip. Attribution is a
//! C#-side declaration, so it is deliberately NOT applied here.
//!
//! Ported from TokenBar-Native's `window_usage.rs` (crates/tb_core_ffi, macOS)
//! with the cache kept — it exists because a full window scan is not a call
//! any UI thread can make. macOS's own probe records 14.93 days and 109,278
//! messages at 67 seconds. Windows differs from the macOS source in the cache
//! mechanics: this crate has no generation-gated root registry
//! (`root_generation`/`invalidate_scan_caches` on macOS), so publication here
//! is unconditional — the same clear-then-insert-one-entry shape, without a
//! generation check — and it reuses the source-context token probe
//! `graph_cached` already uses instead of the plain (non-source-context)
//! probe the macOS module calls.
//!
//! The key is `from_ms` alone, not `(from_ms, until_ms)`. macOS's own module
//! quantises `until_ms` to the minute instead and carries the same defect:
//! `DashboardModel` requests this export every 60s with `until_ms = now`, so
//! a minute-floored key changes on almost every poll (a poll rarely lands
//! exactly on the boundary the previous one floored to), each miss bypasses
//! the source-token check entirely, and `compute` re-runs the full scan.
//! Keying by `from_ms` and trusting the source-change token instead fixes
//! this on both counts: a request whose `until_ms` grew past what is cached
//! reuses that scan's data when the source token is unchanged — nothing new
//! can have been written to storage without it changing — and a genuine
//! source change (or a shrinking `until_ms`, which no production caller
//! produces) still falls through to a fresh scan.

use serde::Serialize;
use serde_json::Value;
use std::collections::HashMap;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{LazyLock, Mutex};
use std::time::{Duration, Instant};

pub(crate) type CacheKey = i64;
/// (computed-at, source token, the `until_ms` this entry was scanned through,
/// mapped window payload).
type CacheEntry = (Instant, u64, i64, Value);

static SCAN_COUNT: AtomicUsize = AtomicUsize::new(0);

/// key → cache entry. Same role as `GRAPH_CACHE` in lib.rs. The key is the
/// window's stable lower bound; see the module doc comment for why `until_ms`
/// is not part of it. `publish` clears before inserting: one entry, not a
/// history, so a window scan (tens of seconds on a large store) left resident
/// forever is one window's messages, not one per poll the lens stayed open.
static WINDOW_USAGE_CACHE: LazyLock<Mutex<HashMap<CacheKey, CacheEntry>>> =
    LazyLock::new(|| Mutex::new(HashMap::new()));

pub(crate) fn cache_key(from_ms: i64) -> CacheKey {
    from_ms
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
    let key = cache_key(from_ms);
    let cached = {
        let cache = WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        cache.get(&key).map(|(at, token, cached_until, data)| {
            (
                // A cached scan only speaks for messages up to `cached_until`,
                // so it is trusted without a token probe only when it also
                // covers the requested `until_ms` — matching the old exact-key
                // trust window, generalised to "covers a smaller-or-equal
                // request" now that `until_ms` is not part of the key.
                at.elapsed() <= Duration::from_secs(crate::ONESHOT_MAX_AGE_SECS)
                    && *cached_until >= until_ms,
                *token,
                data.clone(),
            )
        })
    };
    let Some((fresh_enough_and_covers, token, data)) = cached else {
        return compute(context, from_ms, until_ms, key);
    };
    if fresh_enough_and_covers {
        return Ok(data);
    }

    // Either the cache is stale, or the request grew past what was scanned.
    // Probe with the cache lock released, matching graph_cached: an unchanged
    // source cannot have produced messages in [cached_until, until_ms) that a
    // rescan would find, so the already-scanned data still answers a wider
    // request too, and the probe only refreshes the timestamp (and, when the
    // request grew, the covered bound) rather than re-running the scan.
    if let Ok(probe_token) = tokscale_core::local_source_change_token_with_source_context(
        context.resolved(),
        &context.parse_options(None, None),
    ) {
        if probe_token == token {
            let mut cache =
                WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
            if let Some(entry) = cache.get_mut(&key) {
                entry.0 = Instant::now();
                if until_ms > entry.2 {
                    entry.2 = until_ms;
                }
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
        if let Some((at, _, cached_until, data)) = cache.get(&key) {
            if *cached_until >= until_ms && at.elapsed() <= Duration::from_secs(crate::ONESHOT_MAX_AGE_SECS) {
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
    publish(key, (Instant::now(), token, until_ms, data.clone()));
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

    // The bug this module exists to fix: DashboardModel polls every 60s with
    // `until_ms = now` for a fixed `from_ms`, so two consecutive requests
    // differ only by ordinary polling drift (a few seconds to a minute, not a
    // source change). Both must be answered from one scan.
    #[test]
    fn polling_drift_reuses_one_scan() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let from_ms = 1_700_000_000_000;
        let key = cache_key(from_ms);
        WINDOW_USAGE_CACHE
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .remove(&key);
        let before = scan_count();
        let context = test_context("polling-drift");

        cached(&context, from_ms, from_ms + 60_000).expect("first window scan");
        // A later poll's until_ms grew past what was scanned, unlike the old
        // minute-floored key where this landed inside the same bucket by
        // coincidence — this is the case the fix has to cover.
        cached(&context, from_ms, from_ms + 65_000).expect("drifted poll, cache reused");

        assert_eq!(
            scan_count(),
            before + 1,
            "an unchanged source between two ordinary polls must not re-run the scan"
        );
    }

    // The other half of the same fix: a cache that reuses on every request
    // regardless of the source is a different bug in the same place. Force a
    // stale, mismatched-token entry into the cache directly (real time can't
    // be made to elapse ONESHOT_MAX_AGE_SECS inside a test) and confirm the
    // probe rejects it.
    #[test]
    fn genuine_source_change_forces_a_rescan() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let from_ms = 1_700_000_000_000;
        let key = cache_key(from_ms);
        let context = test_context("source-change");
        let stale_at = Instant::now()
            .checked_sub(Duration::from_secs(crate::ONESHOT_MAX_AGE_SECS + 5))
            .expect("test clock has enough headroom to backdate");
        let sentinel = serde_json::json!({"sentinel": true});
        {
            let mut cache =
                WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
            cache.insert(
                key,
                (stale_at, /* token that cannot match the real source */ u64::MAX, from_ms + 60_000, sentinel),
            );
        }
        let before = scan_count();

        let result = cached(&context, from_ms, from_ms + 60_000).expect("rescan after stale mismatched token");

        assert_eq!(
            scan_count(),
            before + 1,
            "a stale entry whose token no longer matches the source must be rescanned, \
             not served"
        );
        assert_ne!(result, serde_json::json!({"sentinel": true}));
    }

    #[test]
    fn different_from_uses_different_key() {
        assert_ne!(cache_key(1_700_000_000_000), cache_key(1_700_000_060_000));
    }

    #[test]
    fn empty_range_returns_empty_list_not_an_error() {
        // Guarded like the other tests here even though it doesn't touch the
        // cache map: it calls `run` directly, which bumps the shared
        // `SCAN_COUNT` atomic that `polling_drift_reuses_one_scan` and
        // `genuine_source_change_forces_a_rescan` read before/after deltas of
        // — unguarded, a parallel run of this test could land inside that
        // window and make the delta look like an extra scan happened.
        let _guard = TEST_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let context = test_context("empty-range");
        // from > until: no message's timestamp can ever satisfy the filter.
        let value = run(&context, 1_700_000_060_000, 1_700_000_000_000).expect("empty window");
        assert_eq!(value["messages"].as_array().unwrap().len(), 0);
    }
}
