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
//! Keying by `from_ms` instead fixes the miss-every-poll half of that: an
//! ordinary poll whose `until_ms` did NOT grow past what is already cached
//! reuses that scan's data once the source-change token confirms storage is
//! unchanged.
//!
//! A request whose `until_ms` grew past what is cached (a widening request)
//! is more delicate. The token alone does NOT justify reusing the cached
//! data for it — the token proves only that storage has not changed since
//! the cached scan ran, and that scan was itself bounded by its own
//! `until_ms` (`run` passes it straight into
//! `tokscale_core::get_window_usage_with_source_context`), so it says
//! nothing about messages in `[cached_until, until_ms)` a WIDER request asks
//! for; those could have been on disk the whole time, simply past the
//! earlier scan's own requested bound rather than absent from the source
//! (round 10's finding — an earlier version of this cache widened
//! unconditionally and was wrong). The one case where widening past
//! `cached_until` IS sound: the cached scan's own bound had already reached
//! the real wall clock at the moment it ran (`cached_until >=` the scan's
//! own wall-clock timestamp) — which is exactly what `DashboardModel`'s
//! `until_ms = now` polling produces. A message cannot be written before the
//! clock reaches its own timestamp, so nothing in the gap could have existed
//! yet when that scan ran; anything found there later must have been
//! written AFTER the scan, which the unchanged token already rules out. A
//! bounded historical request has no such property — its own bound sits
//! fixed, far below the real clock at scan time — and always falls through
//! to a real rescan bounded by the new, wider `until_ms`. See the
//! widening-soundness comment on `cached` below for the full argument. A
//! shrinking `until_ms` (no production caller produces one today) does NOT
//! trigger a rescan — the wider cached scan already covers it — but it must
//! never hand back messages past its own narrower bound either, so a cache
//! hit whose entry was scanned further than the request is asking for is
//! trimmed to `[from_ms, until_ms)` before it is returned (see
//! `narrow_to_request`).

use serde::Serialize;
use serde_json::Value;
use std::collections::HashMap;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{LazyLock, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

pub(crate) type CacheKey = i64;
/// (computed-at [`Instant`, for the age check], source token, the `until_ms`
/// this entry was scanned through, the wall-clock unix-ms moment the scan
/// itself ran [`Instant` cannot be compared against `until_ms`, hence a
/// separate field — see `wall_clock_now_ms`], mapped window payload).
type CacheEntry = (Instant, u64, i64, i64, Value);

/// Real (unix-ms) wall clock, distinct from the monotonic `Instant` the entry
/// also carries. Needed to tell a scan whose own bound already reached "now"
/// when it ran (an ordinary poll) from one bounded to a fixed point in the
/// past (a historical request) — see the widening-soundness comment in
/// `cached` below.
fn wall_clock_now_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

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
        cache.get(&key).map(|(at, token, cached_until, scanned_at_ms, data)| {
            (
                // A cached scan only speaks for messages up to `cached_until`,
                // so it is trusted without a token probe only when it also
                // covers the requested `until_ms` — matching the old exact-key
                // trust window, generalised to "covers a smaller-or-equal
                // request" now that `until_ms` is not part of the key.
                at.elapsed() <= Duration::from_secs(crate::ONESHOT_MAX_AGE_SECS)
                    && *cached_until >= until_ms,
                *token,
                *cached_until,
                *scanned_at_ms,
                data.clone(),
            )
        })
    };
    let Some((fresh_enough_and_covers, token, cached_until, scanned_at_ms, data)) = cached else {
        return compute(context, from_ms, until_ms, key);
    };
    if fresh_enough_and_covers {
        return Ok(narrow_to_request(data, until_ms, cached_until));
    }

    // Either the cache is stale, or the request grew past what was scanned.
    // Two different reasons the token probe below is allowed to stand in for
    // a rescan, and only one of them extends trust past `cached_until`:
    //
    // - Not a widen (`until_ms <= cached_until`): the cache is merely old
    //   (age > ONESHOT_MAX_AGE_SECS). Nothing new is being claimed — the
    //   answer is narrowed to `until_ms`, which the entry already covered —
    //   so an unchanged token unconditionally justifies reusing it.
    // - A widen (`until_ms > cached_until`): the token only proves storage
    //   has not changed since THIS entry's OWN scan ran — it says nothing
    //   about what a scan bounded by a wider `until_ms` would have found,
    //   because `run` passes `until_ms` straight into
    //   `tokscale_core::get_window_usage_with_source_context` as the scan's
    //   own upper bound. A message timestamped in `[cached_until, until_ms)`
    //   can have been on disk the whole time, simply past the earlier scan's
    //   requested bound, not past what the source contained — UNLESS that
    //   earlier scan's own bound had already reached the real wall clock at
    //   the moment it ran (`cached_until >= scanned_at_ms`): then nothing
    //   past `cached_until` could have existed yet when the scan ran (a
    //   message cannot be written before the clock reaches its own
    //   timestamp), so anything in the gap must have been written AFTER the
    //   scan — exactly what the unchanged token already rules out. That is
    //   the ordinary polling caller (`DashboardModel` requests `until_ms =
    //   now` every 60s, so each scan's own bound IS the wall clock it ran
    //   at). A bounded historical request has `cached_until` fixed far below
    //   `scanned_at_ms`, fails this test, and always falls through to a real
    //   rescan below.
    let widening = until_ms > cached_until;
    if !widening || cached_until >= scanned_at_ms {
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
                return Ok(narrow_to_request(data, until_ms, cached_until));
            }
        }
    }

    compute(context, from_ms, until_ms, key)
}

/// Trims a cache hit's messages to the caller's own `until_ms` when the
/// cached entry was scanned further than that — the case a shrinking request
/// produces, since `fresh_enough_and_covers`/the token probe above both admit
/// `cached_until >= until_ms`, not `==`. Also the single exit that rewrites
/// the payload's own `untilMs` field to `until_ms`, in EVERY case, not only
/// the shrinking one: the soundness-gated widening path in `cached` above
/// can legitimately serve a `until_ms` past `cached_until` (it has already
/// proven no message exists in the gap), and the returned JSON must say what
/// it now covers rather than the stale, narrower bound the entry was
/// actually scanned through — leaving the two disagreeing was round 10's
/// second finding.
fn narrow_to_request(data: Value, until_ms: i64, cached_until: i64) -> Value {
    let mut data = data;
    if cached_until > until_ms {
        if let Some(object) = data.as_object_mut() {
            if let Some(Value::Array(messages)) = object.get_mut("messages") {
                messages.retain(|message| {
                    message
                        .get("timestamp")
                        .and_then(Value::as_i64)
                        .is_some_and(|timestamp| timestamp < until_ms)
                });
            }
        }
    }
    if let Some(object) = data.as_object_mut() {
        object.insert("untilMs".to_string(), Value::from(until_ms));
    }
    data
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
    let hit = {
        let cache = WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        cache.get(&key).and_then(|(at, _, cached_until, _scanned_at_ms, data)| {
            (*cached_until >= until_ms
                && at.elapsed() <= Duration::from_secs(crate::ONESHOT_MAX_AGE_SECS))
            .then(|| (data.clone(), *cached_until))
        })
    };
    // `hit` and the freshly-scanned branch below join at ONE exit
    // (`narrow_to_request` below, called exactly once) rather than each
    // returning its own narrowed value. The recheck-hit branch used to return
    // `data` as-is: two concurrent callers with no covering entry yet both
    // reach this function, the first publishes a scan through its own WIDER
    // `until_ms`, and the second — narrower — found that entry right here and
    // handed back every message up to the first caller's bound, past its own.
    // `cached()`'s two call sites already narrow correctly; this was the one
    // exit that did not.
    let (data, cached_until) = match hit {
        Some((data, cached_until)) => (data, cached_until),
        None => {
            let token = tokscale_core::local_source_change_token_with_source_context(
                context.resolved(),
                &context.parse_options(None, None),
            )
            .unwrap_or(0);
            // Captured before `run` (which can take tens of seconds on a
            // large store), not after: this stands in for "the wall clock at
            // the moment this scan was asked to run", which is what the
            // widening-soundness check in `cached` above needs to compare
            // against this scan's own `until_ms` bound. Capturing it after
            // `run` would make every slow first scan look artificially
            // historical relative to its own bound, permanently disabling
            // the ordinary-polling fast path for that entry's whole life.
            let scanned_at_ms = wall_clock_now_ms();
            let data = run(context, from_ms, until_ms)?;
            publish(key, (Instant::now(), token, until_ms, scanned_at_ms, data.clone()));
            (data, until_ms)
        }
    };
    Ok(narrow_to_request(data, until_ms, cached_until))
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
    /// The interval this payload actually speaks for — `[from_ms, until_ms)`
    /// — so a consumer can check what it got against what it asked for
    /// instead of trusting the request echoed the response. `from_ms` never
    /// changes after a scan (it is the cache key); `until_ms` is rewritten by
    /// `narrow_to_request` above to whichever bound this exact answer
    /// covers — usually the narrower bound a shrinking request asked for,
    /// but occasionally a wider one than what was actually scanned, when the
    /// soundness-gated polling fast path in `cached` served a widening
    /// request without a rescan (see that function's doc comment). Either
    /// way this field, not the request's own `until_ms`, is what a consumer
    /// should trust.
    from_ms: i64,
    until_ms: i64,
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
        from_ms,
        until_ms,
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

    // The half of the original bug that keying by `from_ms` alone still
    // fixes: macOS's minute-floored key changes key on almost every 60s poll
    // even when `until_ms` did not move past what was already scanned, so
    // every such poll bypassed the source-token check and rescanned. Keying
    // by `from_ms` means a repeated request for the SAME already-covered
    // `until_ms` (a poll landing on an unchanged window, or the source-token
    // probe path when nothing grew) is answered from the one scan.
    #[test]
    fn repeated_identical_request_reuses_one_scan() {
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
        cached(&context, from_ms, from_ms + 60_000).expect("second request, same bound, cache reused");

        assert_eq!(
            scan_count(),
            before + 1,
            "a repeated request for an already-covered until_ms must not re-run the scan"
        );
    }

    // Round-10 finding: a request whose `until_ms` grew past what the cached
    // entry was scanned through used to take the source-token fast path and
    // widen `entry.2` without ever rescanning — but the token only proves
    // storage is unchanged since the CACHED scan ran, and that scan was
    // itself bounded by its own (narrower) `until_ms`, so it says nothing
    // about messages in [cached_until, until_ms) a wider request asks for,
    // UNLESS that scan's own bound had already reached the wall clock it ran
    // at (see `a_scan_whose_own_bound_reached_its_own_wall_clock_reuses_on_
    // drift` below for that sound case). This fixture is genuinely
    // historical, not just arithmetically wider: `from_ms` is a fixed 2023
    // timestamp, so `compute`'s own `wall_clock_now_ms()` (the real clock,
    // years later) always lands far past `cached_until` here — the scan's
    // own bound never reaches its own wall clock, so the soundness gate
    // must reject it and fall through to a rescan. Breaks if the widening
    // path is ever entered without that gate (a return to round 10's
    // over-broad first fix, or removing the gate entirely).
    #[test]
    fn widening_request_forces_a_rescan() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let from_ms = 1_700_000_000_000;
        let key = cache_key(from_ms);
        WINDOW_USAGE_CACHE
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .remove(&key);
        let before = scan_count();
        let context = test_context("widening-request");

        cached(&context, from_ms, from_ms + 60_000).expect("first window scan, cache [from, A)");
        // Same (unchanged) source, but the request's until_ms grew past what
        // was scanned — B > A.
        cached(&context, from_ms, from_ms + 65_000).expect("widened request, unchanged source");

        assert_eq!(
            scan_count(),
            before + 2,
            "a historically-bounded request whose until_ms grew past what was cached must \
             trigger a fresh, wider-bounded rescan, not a token-refresh stand-in for one"
        );
    }

    // Restores the property `polling_drift_reuses_one_scan` protected before
    // round 10's first fix deleted it along with the genuinely unsound case
    // above — the coordinator's follow-up finding: that fix was correct but
    // too broad, and this is the narrower, sound case it must not have
    // regressed. A scan whose own bound already reached the wall clock it
    // ran at (the ordinary `DashboardModel` polling shape, `until_ms = now`
    // every 60s) can safely serve a later, drifted-wider request from the
    // same unchanged-token cache entry: nothing past `cached_until` could
    // have existed yet when that scan ran, so anything in the gap must have
    // been written afterward — exactly what the token rules out. Manually
    // fixtured (rather than calling `cached` twice for real) because a real
    // scan's `scanned_at_ms` is the actual test-run wall clock, which is far
    // ahead of any fixed 2023-era `from_ms`/`until_ms` literal — the fixture
    // has to set `cached_until >= scanned_at_ms` explicitly to model "the
    // request's own until_ms really was wall-clock now when the scan ran".
    // Breaks if the widening fast path stops reusing here (round 10's
    // over-broad first fix: no widening at all, ever) or if the soundness
    // gate's comparison direction/field is wrong.
    #[test]
    fn a_scan_whose_own_bound_reached_its_own_wall_clock_reuses_on_drift() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let from_ms = 1_700_000_000_000;
        let key = cache_key(from_ms);
        let context = test_context("polling-drift-sound");
        // The scan's own bound IS the wall clock it ran at — the polling
        // shape (`until_ms = now`), modelled directly rather than measured.
        let scanned_at_ms = from_ms + 60_000;
        let token = tokscale_core::local_source_change_token_with_source_context(
            context.resolved(),
            &context.parse_options(None, None),
        )
        .unwrap_or(0);
        let scanned = serde_json::json!({
            "fromMs": from_ms,
            "untilMs": scanned_at_ms,
            "messages": [],
            "undatedCount": 0,
            "processingTimeMs": 0,
        });
        {
            let mut cache =
                WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
            cache.insert(key, (Instant::now(), token, scanned_at_ms, scanned_at_ms, scanned));
        }
        let before = scan_count();

        // Ordinary polling drift: until_ms grew a few seconds past what was
        // scanned, source unchanged.
        let result = cached(&context, from_ms, scanned_at_ms + 5_000)
            .expect("drifted poll of a scan whose bound already reached its own wall clock");

        assert_eq!(
            scan_count(),
            before,
            "an unchanged source, polled past a scan whose own bound already reached the wall \
             clock it ran at, must not trigger a rescan"
        );
        assert_eq!(
            result["untilMs"].as_i64(),
            Some(scanned_at_ms + 5_000),
            "the widened payload's own untilMs must say what it now covers, not the stale, \
             narrower bound the entry was actually scanned through"
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
                (
                    stale_at,
                    /* token that cannot match the real source */ u64::MAX,
                    from_ms + 60_000,
                    // scanned_at_ms: irrelevant here — this is not a widen
                    // (request until_ms == cached_until below), and the
                    // mismatched token rejects the probe before the
                    // widening-soundness check would ever be reached.
                    from_ms,
                    sentinel,
                ),
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

    // The defect a false comment used to describe as already fixed: a cache
    // entry scanned through `cached_until` must not hand back messages past a
    // NARROWER request's own `until_ms`, even though it is served from cache
    // rather than rescanned.
    #[test]
    fn a_narrower_request_does_not_return_messages_past_its_own_until_ms() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let from_ms = 1_700_000_000_000;
        let key = cache_key(from_ms);
        let context = test_context("narrow-request");
        let wide_until = from_ms + 120_000;
        let narrow_until = from_ms + 60_000;
        let token = tokscale_core::local_source_change_token_with_source_context(
            context.resolved(),
            &context.parse_options(None, None),
        )
        .unwrap_or(0);
        let wide = serde_json::json!({
            "fromMs": from_ms,
            "untilMs": wide_until,
            "messages": [
                {"timestamp": from_ms + 10_000, "client": "a", "providerId": "p", "modelId": "m",
                 "input": 1, "output": 1, "cacheRead": 0, "cacheWrite": 0, "reasoning": 0,
                 "cost": 0.0, "isTurnStart": true},
                // Past the narrower request's until_ms: must never come back below.
                {"timestamp": from_ms + 90_000, "client": "a", "providerId": "p", "modelId": "m",
                 "input": 1, "output": 1, "cacheRead": 0, "cacheWrite": 0, "reasoning": 0,
                 "cost": 0.0, "isTurnStart": true},
            ],
            "undatedCount": 0,
            "processingTimeMs": 0,
        });
        {
            let mut cache =
                WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
            // scanned_at_ms: irrelevant here too — narrow_until < wide_until,
            // so this is a shrinking request, not a widen, and the
            // soundness check never applies to it.
            cache.insert(key, (Instant::now(), token, wide_until, from_ms, wide));
        }
        let before = scan_count();

        let result =
            cached(&context, from_ms, narrow_until).expect("narrower request served from cache");

        assert_eq!(
            scan_count(),
            before,
            "a narrower request that the cache already covers must not trigger a rescan"
        );
        let messages = result["messages"].as_array().expect("messages array");
        assert!(
            messages
                .iter()
                .all(|message| message["timestamp"].as_i64().unwrap() < narrow_until),
            "no returned message may carry a timestamp at or past the request's own until_ms: {messages:?}"
        );
        assert_eq!(messages.len(), 1);
        assert_eq!(result["untilMs"].as_i64(), Some(narrow_until));
    }

    #[test]
    fn narrow_to_request_is_a_noop_when_the_cache_did_not_scan_further() {
        let data = serde_json::json!({"messages": [], "untilMs": 100});
        let unchanged = narrow_to_request(data.clone(), 100, 100);
        assert_eq!(unchanged, data);
    }

    // Round-7 finding: `compute`'s own recheck-under-lock used to return the
    // hit unnarrowed. Simulates the second of two concurrent callers with no
    // covering entry yet: the first has already published a WIDER scan by the
    // time this one enters `compute` and finds it under the `COMPUTE` lock.
    #[test]
    fn compute_narrows_a_cache_hit_found_under_its_own_lock() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let from_ms = 1_700_000_000_000;
        let key = cache_key(from_ms);
        let context = test_context("compute-recheck-narrow");
        let wide_until = from_ms + 120_000;
        let narrow_until = from_ms + 60_000;
        let token = tokscale_core::local_source_change_token_with_source_context(
            context.resolved(),
            &context.parse_options(None, None),
        )
        .unwrap_or(0);
        let wide = serde_json::json!({
            "fromMs": from_ms,
            "untilMs": wide_until,
            "messages": [
                {"timestamp": from_ms + 10_000, "client": "a", "providerId": "p", "modelId": "m",
                 "input": 1, "output": 1, "cacheRead": 0, "cacheWrite": 0, "reasoning": 0,
                 "cost": 0.0, "isTurnStart": true},
                // Past the narrower caller's own until_ms: must never come back.
                {"timestamp": from_ms + 90_000, "client": "a", "providerId": "p", "modelId": "m",
                 "input": 1, "output": 1, "cacheRead": 0, "cacheWrite": 0, "reasoning": 0,
                 "cost": 0.0, "isTurnStart": true},
            ],
            "undatedCount": 0,
            "processingTimeMs": 0,
        });
        {
            let mut cache =
                WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
            // scanned_at_ms: irrelevant — `compute`'s own recheck-under-lock
            // does not consult it at all (see the `_scanned_at_ms` in its
            // destructuring above).
            cache.insert(key, (Instant::now(), token, wide_until, from_ms, wide));
        }
        let before = scan_count();

        // `compute` directly, not `cached`: this is exactly the call the
        // narrower of two concurrent callers makes once it decides there is
        // no covering entry yet (`cached`'s own two exits already narrow
        // correctly and are not what this test is asserting about).
        let result =
            compute(&context, from_ms, narrow_until, key).expect("recheck hit, narrowed");

        assert_eq!(
            scan_count(),
            before,
            "a hit found under the COMPUTE lock must not trigger a second scan"
        );
        let messages = result["messages"].as_array().expect("messages array");
        assert!(
            messages
                .iter()
                .all(|message| message["timestamp"].as_i64().unwrap() < narrow_until),
            "no returned message may carry a timestamp at or past the caller's own \
             until_ms: {messages:?}"
        );
        assert_eq!(messages.len(), 1);
        assert_eq!(result["untilMs"].as_i64(), Some(narrow_until));
    }
}
