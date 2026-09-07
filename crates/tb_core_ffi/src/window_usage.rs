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
//! the real wall clock at the moment the FFI call that produced it started
//! — call that "the bound was present". A message cannot be written before
//! the clock reaches its own timestamp, so nothing in the gap could have
//! existed yet when that call started; anything found there later must have
//! been written AFTER, which the unchanged token already rules out. A
//! bounded historical request has no such property — its own bound sits
//! fixed, far below the real clock at scan time — and always falls through
//! to a real rescan bounded by the new, wider `until_ms`. See the
//! widening-soundness comment on `cached` below for the full argument,
//! including why this is decided once at publish time and stored as a bool
//! rather than compared live from two independently-read clocks.
//!
//! That bool is `bound_was_present`, and what feeds it changed in this
//! commit — the fifth on this gate, after four rounds (`9918d7e`,
//! `f019fe5`, `39553f5`, and the one that landed
//! `WIDENING_ENTRY_SLACK_MS = 25`) that each refined a clock-comparison
//! guess without ever measuring it end-to-end across the FFI boundary. A
//! P/Invoke harness against the real 5.3 GB store on the x64 host measured
//! it: at `39553f5` and at the slack-tuned HEAD it replaced, a widening call
//! that hit the fast path cost ~1450-1740ms and one that fell through to a
//! rescan cost ~6300-6500ms, and which one happened was intermittent between
//! runs of the very same binary — the first process run of a pair typically
//! missed the fast path, the second typically hit it. The likely mechanism
//! is OS file-cache warmth changing the latency the slack constant was
//! trying to outlast, which means the gate was least reliable exactly on a
//! cold start — backwards from what it exists to protect, on a branch that
//! already had an open cold-start complaint. No constant closes that gap: it
//! spans P/Invoke marshalling plus however long `LocalSourceContext::process`
//! takes on a disk state this module cannot see coming.
//!
//! So this module stopped inferring `bound_was_present` from a clock
//! comparison and started reading it from the caller. `until_ms` reaching
//! this module already began life as `DateTimeOffset.UtcNow` on the C# side
//! (`DashboardModel.cs:570`) — the caller always knew whether its own bound
//! was "now"; the previous design threw that fact away at the FFI boundary
//! and tried to reconstruct an approximation of it from two independently
//! read clocks. `tb_window_usage` now takes that fact as an explicit
//! `bound_is_now` parameter instead. A caller that does not pass it gets
//! `false` — the safe, no-widening-reuse behaviour — never the fast path by
//! default.
//!
//! A caller's claim is not the whole story, though: the finding that killed
//! the clock-comparison design's contended case (a caller queued behind
//! another's in-progress scan reads "now" before it waits, so by the time
//! its own scan actually runs, that "now" is stale) still applies to a
//! caller-declared flag exactly as it applied to a caller-computed
//! timestamp — `bound_is_now = true` says the bound was current when the
//! caller read it, not when this module's scan actually executes, and those
//! can drift apart by however long the caller waited on `COMPUTE`. Fixing
//! that no longer needs a clock read at all: `compute` below distinguishes
//! an immediate `COMPUTE.try_lock()` (nothing was in progress; the scan runs
//! essentially when the caller asked) from one that had to block, and
//! downgrades a contended acquisition's `bound_was_present` to `false`
//! regardless of what the caller declared — the caller's claim was made
//! before an indeterminate wait, so it is no longer trusted once that wait
//! actually happened. This is a strictly cheaper and more precise test than
//! any slack constant could be: it asks whether a wait occurred at all,
//! never how long, and needs no wall-clock read anywhere in this path (the
//! module's remaining `SystemTime` use is test scaffolding only — see the
//! `#[cfg(test)]` block below). A shrinking `until_ms` (no production caller
//! produces one today) does NOT trigger a
//! rescan — the wider cached scan already covers it — but it must never hand
//! back messages past its own narrower bound either, so a cache hit whose
//! entry was scanned further than the request is asking for is trimmed to
//! `[from_ms, until_ms)` before it is returned (see `narrow_to_request`).
//!
//! Two preconditions this design rests on, both newly load-bearing as of
//! round 11 and both currently implicit anywhere else in this module:
//!
//! - Every message timestamp on disk was produced by (and never exceeds) the
//!   wall clock of the machine running this scan. A provider-supplied,
//!   future-dated, or clock-skewed timestamp (a synced or cloud-mirrored
//!   store, say) sitting on disk at scan time is excluded by the bounded
//!   scan and then excluded from every widened serve for as long as the
//!   token holds unchanged — a silent undercount. It self-heals on the next
//!   write that actually changes the token and forces a real rescan; it is
//!   not caught any sooner.
//! - The wall clock does not step backwards across a scan boundary (NTP
//!   step, manual clock change). A backward step makes a later `bound was
//!   present` decision look sound when it was not; this module does not
//!   defend against it.
//!
//! Both are accepted trade-offs, not oversights: catching either would mean
//! validating every on-disk timestamp against the scan-time clock, which is
//! exactly the full rescan this cache exists to avoid.

use serde::Serialize;
use serde_json::Value;
use std::collections::HashMap;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{LazyLock, Mutex};
use std::time::{Duration, Instant};

pub(crate) type CacheKey = i64;
/// (published-at [`Instant`, an immutable anchor set once by `publish` and
/// never refreshed on a later hit — see `WINDOW_USAGE_STALE_CEILING_SECS`
/// below for why it must stay immutable], source token, the `until_ms` this
/// entry was scanned through, whether the caller declared that bound to be
/// "now" AND this scan ran without waiting behind another caller's
/// [`bound_was_present`, decided once by `compute` from the FFI-supplied
/// `bound_is_now` flag and its own lock-contention check — see the
/// widening-soundness comment on `cached` below], mapped window payload).
type CacheEntry = (Instant, u64, i64, bool, Value);

/// A single scan result served by the token-verified fast path (the `cached`
/// branch below that does not require `until_ms <= cached_until`) is bounded
/// to this age regardless of how many times the token still matches,
/// independent of `ONESHOT_MAX_AGE_SECS`. That constant (30s) is smaller
/// than `DashboardModel`'s own polling cadence — `RefreshSlow`'s 60s
/// `_slowTimer` tick publishes a graph, which triggers the lazy refresh that
/// calls `tb_window_usage` (`DashboardModel.cs:682`, confirmed by tracing
/// `RequestLazyRefresh`'s callers) — so reusing it here would force a
/// rescan on every single ordinary poll, recreating the exact regression
/// this cache exists to avoid. This ceiling is instead set to comfortably
/// outlast many consecutive polls while still guaranteeing a scan is never
/// served indefinitely.
const WINDOW_USAGE_STALE_CEILING_SECS: u64 = 900;

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
    bound_is_now: bool,
) -> Result<Value, String> {
    let key = cache_key(from_ms);
    let cached = {
        let cache = WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        cache.get(&key).map(|(published_at, token, cached_until, bound_was_present, data)| {
            (
                // A cached scan only speaks for messages up to `cached_until`,
                // so it is trusted without a token probe only when it also
                // covers the requested `until_ms` — matching the old exact-key
                // trust window, generalised to "covers a smaller-or-equal
                // request" now that `until_ms` is not part of the key.
                published_at.elapsed() <= Duration::from_secs(crate::ONESHOT_MAX_AGE_SECS)
                    && *cached_until >= until_ms,
                published_at.elapsed(),
                *token,
                *cached_until,
                *bound_was_present,
                data.clone(),
            )
        })
    };
    let Some((fresh_enough_and_covers, age, token, cached_until, bound_was_present, data)) = cached
    else {
        return compute(context, from_ms, until_ms, key, bound_is_now);
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
    //   the moment this caller actually acquired `COMPUTE` and began its own
    //   scan (`bound_was_present`, decided once by `compute`). That fact is
    //   now supplied by the caller as `bound_is_now` rather than inferred
    //   from a clock comparison at all — `until_ms` reaching this module was
    //   always `DateTimeOffset.UtcNow` on the C# side, so the caller already
    //   knows whether its own bound is "now"; asking it to say so directly
    //   removed a P/Invoke-boundary clock race that a Rust-side reading of
    //   its own wall clock could never fully account for (measured
    //   end-to-end: intermittent between successive runs of the very same
    //   binary against a real store — see the module doc comment). A bounded
    //   historical request declares `bound_is_now = false`, and it always
    //   falls through to a real rescan below. A caller's `true` is still not
    //   the whole story: it was read before this caller ever waited on
    //   `COMPUTE`, so `compute` also checks whether this scan's own
    //   `COMPUTE.try_lock()` was immediate — a caller that had to queue
    //   behind another's in-progress scan has its declared "now" go stale
    //   during the wait, and `compute` downgrades `bound_was_present` to
    //   `false` for it regardless of what was declared. That needs no clock
    //   read either: whether an acquisition blocked is a fact about the lock,
    //   not the clock.
    //
    // Bounded independently of both: `age` alone would let a token match
    // keep this branch reachable forever (nothing here refreshes
    // `published_at`), so `stale` puts a ceiling on how long a single scan
    // result can be served this way at all — see
    // `WINDOW_USAGE_STALE_CEILING_SECS`.
    let widening = until_ms > cached_until;
    let stale = age > Duration::from_secs(WINDOW_USAGE_STALE_CEILING_SECS);
    if !stale && (!widening || bound_was_present) {
        if let Ok(probe_token) = tokscale_core::local_source_change_token_with_source_context(
            context.resolved(),
            &context.parse_options(None, None),
        ) {
            if probe_token == token {
                return Ok(narrow_to_request(data, until_ms, cached_until));
            }
        }
    }

    compute(context, from_ms, until_ms, key, bound_is_now)
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
    bound_is_now: bool,
) -> Result<Value, String> {
    // `try_lock` first, not `lock`, so this caller learns whether it had to
    // wait for another caller's scan already in progress — the fact
    // `bound_was_present` below needs and, unlike the clock-comparison design
    // this replaced, the ONLY fact it needs: no wall-clock read is taken
    // anywhere in this function. `Poisoned` here means the mutex was free
    // (nobody else was holding it) but marked poisoned by an earlier panic —
    // an immediate, uncontended acquisition exactly like `Ok`, just carrying
    // stale interior state from that panic, which this cache tolerates the
    // same way every other lock in this module does (`unwrap_or_else`).
    let (contended, _serialised) = match COMPUTE.try_lock() {
        Ok(guard) => (false, guard),
        Err(std::sync::TryLockError::Poisoned(poisoned)) => (false, poisoned.into_inner()),
        Err(std::sync::TryLockError::WouldBlock) => {
            (true, COMPUTE.lock().unwrap_or_else(|poisoned| poisoned.into_inner()))
        }
    };
    // Re-check under the lock. A caller that queued behind another's scan is
    // asking a question that scan may have just answered; running a second one
    // to produce the same bytes is the duplicate this lock exists to remove.
    let hit = {
        let cache = WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        cache.get(&key).and_then(|(published_at, _, cached_until, _bound_was_present, data)| {
            (*cached_until >= until_ms
                && published_at.elapsed() <= Duration::from_secs(crate::ONESHOT_MAX_AGE_SECS))
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
            // Read BEFORE `run` below, not after — load-bearing, not
            // incidental: a write landing mid-scan changes the token read
            // here strictly before it could have been observed, so the next
            // caller's probe (in `cached` above) correctly sees a mismatch
            // and forces a rescan. Reading it after `run` would let a write
            // that happened during the scan silently match the token this
            // scan publishes, and the fast path would then serve stale data
            // past that write forever (until some later, unrelated change
            // moved the token again).
            let token = tokscale_core::local_source_change_token_with_source_context(
                context.resolved(),
                &context.parse_options(None, None),
            )
            .unwrap_or(0);
            let data = run(context, from_ms, until_ms)?;
            // Whether this scan's own bound had already reached the wall
            // clock at the moment the FFI call that produced it started —
            // decided once, here, from two facts, neither of them a clock
            // read: `bound_is_now`, the caller's own declaration that
            // `until_ms` was "now" when the caller read it (`until_ms`
            // reaching this module began life as `DateTimeOffset.UtcNow` on
            // the C# side, so the caller already knows this — see the module
            // doc comment for why inferring it here instead, from two
            // independently read clocks either side of the FFI boundary,
            // measured intermittent end-to-end and was abandoned); and
            // `contended`, whether this call's own `COMPUTE.try_lock()`
            // above had to wait. A caller's declaration was made before it
            // ever queued on `COMPUTE`, so it goes stale during a wait behind
            // another caller's in-progress scan exactly the way a captured
            // clock reading used to — a message written during the wait
            // lands inside the freshly-read token but outside `until_ms`,
            // and trusting the declaration anyway would let a later widening
            // request silently serve an incomplete payload for as long as
            // the token holds. `contended` catches that with no timing
            // measurement at all: an uncontended acquisition means this
            // scan ran essentially when the caller asked, so its declaration
            // still describes the scan that actually ran.
            let bound_was_present = bound_is_now && !contended;
            publish(key, (Instant::now(), token, until_ms, bound_was_present, data.clone()));
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

    // Test scaffolding only — production code takes no wall-clock reading
    // anywhere in this module (see the module doc comment for why). Tests
    // still need a genuine "now" to build realistic historical-vs-present
    // fixtures and to exercise `compute`'s own lock-contention path with a
    // caller-declared `bound_is_now` that is actually true when declared.
    fn wall_clock_now_ms() -> i64 {
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_millis() as i64)
            .unwrap_or(i64::MAX)
    }

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

        cached(&context, from_ms, from_ms + 60_000, false).expect("first window scan");
        cached(&context, from_ms, from_ms + 60_000, false)
            .expect("second request, same bound, cache reused");

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
    // timestamp and both calls below declare `bound_is_now = false`, so the
    // soundness gate must reject the widen and fall through to a rescan
    // regardless of what the real wall clock reads years later. Breaks if
    // the widening path is ever entered without that gate (a return to
    // round 10's over-broad first fix, or removing the gate entirely).
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

        cached(&context, from_ms, from_ms + 60_000, false).expect("first window scan, cache [from, A)");
        // Same (unchanged) source, but the request's until_ms grew past what
        // was scanned — B > A. Both fixed 2023 timestamps, not real "now"
        // reads, so bound_is_now is false — this is the genuinely historical
        // case the soundness gate must always reject regardless.
        cached(&context, from_ms, from_ms + 65_000, false).expect("widened request, unchanged source");

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
    // been written afterward — exactly what the token rules out.
    //
    // Fixtures `bound_was_present` directly, which is legitimate here in a
    // way the round-11 predecessor of this test was not: that version
    // fixtured `cached_until >= scanned_at_ms`, a relationship between two
    // fields `compute` itself could never produce (see
    // `production_widening_drift_reuses_one_scan` below, which proves that
    // with two real `cached()` calls and would have caught it). This test
    // is narrower on purpose — it isolates "given the flag is true, does the
    // gate in `cached` correctly trust it" from "does `compute` correctly
    // set the flag", which the production-shaped test covers instead.
    // Breaks if the widening fast path stops reusing here (round 10's
    // over-broad first fix: no widening at all, ever) or if the soundness
    // gate stops reading `bound_was_present`.
    #[test]
    fn a_scan_whose_own_bound_reached_its_own_wall_clock_reuses_on_drift() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let from_ms = 1_700_000_000_000;
        let key = cache_key(from_ms);
        let context = test_context("polling-drift-sound");
        let cached_until = from_ms + 60_000;
        let token = tokscale_core::local_source_change_token_with_source_context(
            context.resolved(),
            &context.parse_options(None, None),
        )
        .unwrap_or(0);
        let scanned = serde_json::json!({
            "fromMs": from_ms,
            "untilMs": cached_until,
            "messages": [],
            "undatedCount": 0,
            "processingTimeMs": 0,
        });
        {
            let mut cache =
                WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
            cache.insert(key, (Instant::now(), token, cached_until, true, scanned));
        }
        let before = scan_count();

        // Ordinary polling drift: until_ms grew a few seconds past what was
        // scanned, source unchanged. bound_is_now here is irrelevant to the
        // outcome — the fixtured entry's own bound_was_present=true already
        // grants the fast path, so this call must never reach compute()
        // (and therefore never consult its own bound_is_now) at all.
        let result = cached(&context, from_ms, cached_until + 5_000, true)
            .expect("drifted poll of a scan whose bound already reached its own wall clock");

        assert_eq!(
            scan_count(),
            before,
            "an unchanged source, polled past a scan whose own bound already reached the wall \
             clock it ran at, must not trigger a rescan"
        );
        assert_eq!(
            result["untilMs"].as_i64(),
            Some(cached_until + 5_000),
            "the widened payload's own untilMs must say what it now covers, not the stale, \
             narrower bound the entry was actually scanned through"
        );
    }

    // Task 1's production-shaped test: two REAL `cached()` calls against a
    // REAL `compute()`, both passing `until_ms = wall_clock_now_ms()` at
    // their own call moment and `bound_is_now = true` (exactly the
    // `DashboardModel` polling shape), source unchanged, with a deliberate
    // drift between them. No hand-set `CacheEntry` field anywhere — this is
    // the only test in this module that exercises `bound_was_present` as
    // `compute` itself sets it, not as a fixture asserts it should be set.
    //
    // This is what every prior fix lacked, all the way through the
    // slack-constant design this file's own history describes: each was
    // either a correct unit test of a gate's arithmetic on a hand-set entry
    // (zero evidence about the behaviour the gate exists to protect — a real
    // `compute()` call was never in the loop) or, in the slack-constant
    // version, unmeasurable end-to-end from inside this crate at all — the
    // intermittency that finally killed it only showed up in a P/Invoke
    // harness against a real store, outside what any test in this module can
    // reach. This test still cannot measure across that boundary, but it
    // remains the one exercise of the real `compute()` path with a genuine,
    // undeclared-by-fixture `bound_is_now`, and it must keep passing under
    // the caller-declared design the same way it did under every design
    // before it.
    #[test]
    fn production_widening_drift_reuses_one_scan() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let from_ms = wall_clock_now_ms() - 3_600_000;
        let key = cache_key(from_ms);
        WINDOW_USAGE_CACHE
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .remove(&key);
        let context = test_context("production-widening-drift");
        let before = scan_count();

        // First poll: until_ms = now, exactly DashboardModel.cs:570's shape,
        // and bound_is_now = true because it genuinely is: this is a real
        // caller declaring a real "now" read, not a fixture.
        cached(&context, from_ms, wall_clock_now_ms(), true).expect("first poll");

        // The smallest sleep that buys a real, measurable drift between two
        // wall-clock reads a real caller a moment apart would also see — not
        // simulating a 60s poll interval, just proving the gate tolerates
        // genuine elapsed time between two real calls, not only a
        // zero-drift or hand-set one.
        std::thread::sleep(Duration::from_millis(50));

        // Second poll: until_ms grew past what was cached (ordinary drift),
        // same unchanged source, again a real "now" read.
        cached(&context, from_ms, wall_clock_now_ms(), true).expect("second poll, drifted wider");

        assert_eq!(
            scan_count(),
            before + 1,
            "two real polls of an unchanged source, until_ms = now both times, must share one \
             scan — this is what both f019fe5's zero-tolerance clock gate and 9918d7e's outright \
             deletion of the widening fast path broke for the only production caller"
        );
    }

    // The finding this round fixes: a caller queued behind another's
    // in-progress scan must not publish `bound_was_present = true` for a
    // `until_ms` that was only "now" back when it first called, not when its
    // scan actually ran — the same property the clock-comparison design used
    // to protect with `call_entry_ms`, now protected with no clock read at
    // all by `compute`'s own `COMPUTE.try_lock()` contention check. Drives
    // REAL contention on the private `COMPUTE` mutex (reachable directly —
    // this test is in the same module) rather than faking the property by
    // construction: caller A holds `COMPUTE` itself (standing in for a long
    // scan in progress); caller B declares `bound_is_now = true` — genuinely
    // true when it calls, exactly `DashboardModel.cs:570`'s shape — BEFORE A
    // releases, then blocks on `COMPUTE`; A releases only after B is provably
    // queued. If `compute` trusted a caller's declaration without checking
    // whether its own `try_lock()` was contended, B's stale pre-wait
    // declaration would still publish `bound_was_present = true`, and the
    // immediate post-B widen below would wrongly reuse B's cache entry
    // instead of rescanning.
    #[test]
    fn contended_caller_does_not_publish_bound_was_present_for_a_stale_until_ms() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let from_ms = wall_clock_now_ms() - 3_600_000;
        let key = cache_key(from_ms);
        WINDOW_USAGE_CACHE
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .remove(&key);
        let context = test_context("contended-caller");
        let before = scan_count();

        // Caller A: hold COMPUTE directly, simulating another caller's scan
        // already in progress when B calls in.
        let hold = COMPUTE.lock().unwrap_or_else(|p| p.into_inner());

        // Caller B: reads until_ms = now and declares bound_is_now = true
        // HERE, before it has any idea A is holding the lock — exactly
        // DashboardModel.cs:570's shape, and exactly the declaration the
        // finding says must not be trusted once B has to wait behind A.
        let stale_until_ms = wall_clock_now_ms();
        let context_b = context.clone();
        let b = std::thread::spawn(move || cached(&context_b, from_ms, stale_until_ms, true));

        // Give B time to actually reach and block on COMPUTE.lock() before A
        // releases — proving this holds for a genuine, non-instantaneous
        // contended wait, not just a `try_lock()` that happens to fail and
        // immediately succeed.
        std::thread::sleep(Duration::from_millis(200));
        drop(hold);

        b.join().expect("caller B thread").expect("caller B's scan");

        // Immediately widen past stale_until_ms, source unchanged. If B's
        // entry published bound_was_present = true despite B's own
        // COMPUTE.try_lock() having been contended, this reuses B's entry
        // with no rescan — the silent incomplete-payload bug the finding
        // describes. Correct behaviour is a rescan, because B's own scan
        // genuinely started well after stale_until_ms.
        cached(&context, from_ms, stale_until_ms + 1_000, true)
            .expect("widen after contended publish");

        assert_eq!(
            scan_count(),
            before + 2,
            "a caller that waited behind COMPUTE must not publish bound_was_present = true for \
             a until_ms that was only \"now\" before it started waiting — a subsequent widen must \
             force a real rescan, not silently reuse the contended entry's stale-bounded token"
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
                    // bound_was_present: irrelevant here — this is not a
                    // widen (request until_ms == cached_until below), and the
                    // mismatched token rejects the probe before the
                    // widening-soundness check would ever be reached.
                    false,
                    sentinel,
                ),
            );
        }
        let before = scan_count();

        let result = cached(&context, from_ms, from_ms + 60_000, false)
            .expect("rescan after stale mismatched token");

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
            // bound_was_present: irrelevant here too — narrow_until <
            // wide_until, so this is a shrinking request, not a widen, and
            // the soundness check never applies to it.
            cache.insert(key, (Instant::now(), token, wide_until, false, wide));
        }
        let before = scan_count();

        let result = cached(&context, from_ms, narrow_until, false)
            .expect("narrower request served from cache");

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
            // bound_was_present: irrelevant — `compute`'s own
            // recheck-under-lock does not consult it at all (see the
            // `_bound_was_present` in its destructuring above).
            cache.insert(key, (Instant::now(), token, wide_until, false, wide));
        }
        let before = scan_count();

        // `compute` directly, not `cached`: this is exactly the call the
        // narrower of two concurrent callers makes once it decides there is
        // no covering entry yet (`cached`'s own two exits already narrow
        // correctly and are not what this test is asserting about).
        // `bound_is_now` is irrelevant here too — the recheck-under-lock hit
        // branch returns before `bound_was_present` would ever be computed.
        let result = compute(&context, from_ms, narrow_until, key, false)
            .expect("recheck hit, narrowed");

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
