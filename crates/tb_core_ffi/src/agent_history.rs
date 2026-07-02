//! Historical weekly-usage tracking for Codex, mirroring codexbar's
//! `CodexHistoricalPaceEvaluator`. The naive linear pace (`elapsed/duration`)
//! assumes quota is spent evenly across the week; real usage is lumpy
//! (typically front-loaded), so linear pace can read "in deficit" when, against
//! your own historical curve, you're actually "in reserve".
//!
//! We persist periodic samples of the Codex weekly window and, once a couple of
//! completed weeks have accrued, derive an *expected* used-percent at the
//! current point in the week from the median of past weeks — plus a coarse
//! run-out probability (share of past weeks that hit the cap before reset).
//!
//! Pure logic (`should_accept` / `record_sample` / `evaluate_samples`) is split
//! from disk I/O so it can be unit-tested without touching the filesystem.

use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::fs;
use std::path::PathBuf;

/// Don't persist a fresh sample unless ~5 minutes passed since the last one for
/// this window (or the reset / usage moved meaningfully). Matches codexbar's
/// write throttle so the store doesn't churn on every poll.
const WRITE_INTERVAL_SECS: i64 = 300;
/// Accept an off-cadence write if usage moved at least this many points.
const WRITE_DELTA_PERCENT: f64 = 0.5;
/// Keep roughly this many weeks of history; prune older samples.
const PRUNE_WEEKS: i64 = 8;
/// Need at least this many *completed* past weeks before a historical curve is
/// trustworthy; below this the caller falls back to linear pace.
const MIN_COMPLETED_WEEKS: usize = 2;
/// A past week "ran out" if its peak usage reached this percent before reset.
const RUNOUT_THRESHOLD_PERCENT: f64 = 99.0;
/// Half-width (in window fraction) of the band around the query point whose
/// samples feed the median expected-usage estimate.
const EXPECTED_BAND: f64 = 0.075;

pub struct HistoricalPace {
    pub expected_percent: f64,
    /// 0..1 share of past weeks that hit the cap before reset, if derivable.
    pub run_out_probability: Option<f64>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct Sample {
    account_key: String,
    /// Unix seconds of this weekly window's reset (doubles as the week id).
    resets_at: i64,
    window_minutes: i64,
    used_percent: f64,
    /// Unix seconds the sample was taken.
    sampled_at: i64,
}

#[derive(Debug, Default, Serialize, Deserialize)]
struct Store {
    samples: Vec<Sample>,
}

/// Record the current Codex weekly reading and return a historical pace if a
/// trustworthy curve exists yet. Disk errors are swallowed (best-effort store).
pub fn record_and_evaluate(
    account_key: &str,
    resets_at: i64,
    window_minutes: i64,
    used_percent: f64,
    now: i64,
) -> Option<HistoricalPace> {
    let mut store = load_store();
    let sample = Sample {
        account_key: account_key.to_string(),
        resets_at,
        window_minutes,
        used_percent,
        sampled_at: now,
    };
    let prune_before = now - PRUNE_WEEKS * 7 * 86_400;
    record_sample(&mut store.samples, sample, prune_before);
    let _ = save_store(&store);
    evaluate_samples(&store.samples, account_key, resets_at, window_minutes, now)
}

/// Should `next` be persisted given the existing samples? Accept when there's no
/// prior reading for this window, the window reset (new week), enough time
/// passed, or usage moved meaningfully.
fn should_accept(samples: &[Sample], next: &Sample) -> bool {
    let prior = samples
        .iter()
        .filter(|s| s.account_key == next.account_key && s.window_minutes == next.window_minutes)
        .max_by_key(|s| s.sampled_at);
    match prior {
        None => true,
        Some(prior) => {
            prior.resets_at != next.resets_at
                || next.sampled_at - prior.sampled_at >= WRITE_INTERVAL_SECS
                || (next.used_percent - prior.used_percent).abs() >= WRITE_DELTA_PERCENT
        }
    }
}

fn record_sample(samples: &mut Vec<Sample>, next: Sample, prune_before: i64) {
    if should_accept(samples, &next) {
        samples.push(next);
    }
    samples.retain(|s| s.sampled_at >= prune_before);
    samples.sort_by(|a, b| {
        a.account_key
            .cmp(&b.account_key)
            .then(a.resets_at.cmp(&b.resets_at))
            .then(a.sampled_at.cmp(&b.sampled_at))
    });
}

/// Derive the expected used-percent (and run-out probability) at the current
/// point in the in-progress week from past completed weeks. Returns `None`
/// unless the window is valid, in-progress, and at least `MIN_COMPLETED_WEEKS`
/// completed weeks of history exist.
fn evaluate_samples(
    samples: &[Sample],
    account_key: &str,
    resets_at: i64,
    window_minutes: i64,
    now: i64,
) -> Option<HistoricalPace> {
    let duration = window_minutes.checked_mul(60)?;
    if duration <= 0 {
        return None;
    }
    let time_until_reset = resets_at - now;
    if time_until_reset <= 0 || time_until_reset > duration {
        return None;
    }
    let fraction = 1.0 - (time_until_reset as f64 / duration as f64);

    // Group past *completed* weeks (reset already passed, not the current week)
    // for this account+window. Each week id is its reset timestamp.
    let mut weeks: BTreeMap<i64, Vec<&Sample>> = BTreeMap::new();
    for sample in samples {
        if sample.account_key != account_key || sample.window_minutes != window_minutes {
            continue;
        }
        if sample.resets_at == resets_at || sample.resets_at > now {
            continue;
        }
        weeks.entry(sample.resets_at).or_default().push(sample);
    }
    if weeks.len() < MIN_COMPLETED_WEEKS {
        return None;
    }

    let mut points: Vec<(f64, f64)> = Vec::new();
    let mut runout_hits = 0usize;
    for (week_reset, week_samples) in &weeks {
        let mut week_peak = 0.0f64;
        for sample in week_samples {
            let f = 1.0 - ((week_reset - sample.sampled_at) as f64 / duration as f64);
            if (0.0..=1.0).contains(&f) {
                points.push((f, sample.used_percent.clamp(0.0, 100.0)));
            }
            week_peak = week_peak.max(sample.used_percent);
        }
        if week_peak >= RUNOUT_THRESHOLD_PERCENT {
            runout_hits += 1;
        }
    }

    let expected = expected_at(&points, fraction)?;
    Some(HistoricalPace {
        expected_percent: expected.clamp(0.0, 100.0),
        run_out_probability: Some(runout_hits as f64 / weeks.len() as f64),
    })
}

/// Median used-percent of past-week samples near `fraction`, falling back to the
/// single nearest sample when the band is empty.
fn expected_at(points: &[(f64, f64)], fraction: f64) -> Option<f64> {
    if points.is_empty() {
        return None;
    }
    let mut in_band: Vec<f64> = points
        .iter()
        .filter(|(f, _)| (f - fraction).abs() <= EXPECTED_BAND)
        .map(|(_, used)| *used)
        .collect();
    if in_band.is_empty() {
        let nearest = points
            .iter()
            .min_by(|a, b| {
                (a.0 - fraction)
                    .abs()
                    .partial_cmp(&(b.0 - fraction).abs())
                    .unwrap_or(std::cmp::Ordering::Equal)
            })?;
        return Some(nearest.1);
    }
    Some(median(&mut in_band))
}

fn median(values: &mut [f64]) -> f64 {
    values.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
    let n = values.len();
    if n % 2 == 1 {
        values[n / 2]
    } else {
        (values[n / 2 - 1] + values[n / 2]) / 2.0
    }
}

fn store_path() -> Option<PathBuf> {
    // Same file the Tauri app writes (`dirs::data_dir()` resolves to
    // ~/Library/Application Support on macOS), so the accumulated weekly pace
    // history carries over to the native app unchanged.
    Some(dirs::data_dir()?.join("com.nyanako.tokenbar/codex-weekly-history.json"))
}

fn load_store() -> Store {
    let Some(path) = store_path() else {
        return Store::default();
    };
    match fs::read_to_string(&path) {
        Ok(raw) => serde_json::from_str(&raw).unwrap_or_default(),
        Err(_) => Store::default(),
    }
}

fn save_store(store: &Store) -> std::io::Result<()> {
    let Some(path) = store_path() else {
        return Ok(());
    };
    if let Some(dir) = path.parent() {
        fs::create_dir_all(dir)?;
    }
    let data = serde_json::to_vec_pretty(store).unwrap_or_default();
    fs::write(path, data)
}

#[cfg(test)]
mod tests {
    use super::*;

    const WEEK_SECS: i64 = 7 * 86_400;
    const WEEK_MINUTES: i64 = 10_080;

    fn sample(account: &str, resets_at: i64, used: f64, sampled_at: i64) -> Sample {
        Sample {
            account_key: account.to_string(),
            resets_at,
            window_minutes: WEEK_MINUTES,
            used_percent: used,
            sampled_at,
        }
    }

    #[test]
    fn accepts_first_sample_then_throttles_small_quick_writes() {
        let mut samples = Vec::new();
        record_sample(&mut samples, sample("acct", WEEK_SECS, 10.0, 1_000), 0);
        assert_eq!(samples.len(), 1);

        // Same window, only 60s later, +0.1% — throttled away.
        record_sample(&mut samples, sample("acct", WEEK_SECS, 10.1, 1_060), 0);
        assert_eq!(samples.len(), 1);

        // 5+ minutes later — accepted.
        record_sample(&mut samples, sample("acct", WEEK_SECS, 10.2, 1_400), 0);
        assert_eq!(samples.len(), 2);

        // Big jump within the interval — accepted.
        record_sample(&mut samples, sample("acct", WEEK_SECS, 14.0, 1_450), 0);
        assert_eq!(samples.len(), 3);
    }

    #[test]
    fn accepts_new_week_immediately() {
        let mut samples = Vec::new();
        record_sample(&mut samples, sample("acct", WEEK_SECS, 90.0, 1_000), 0);
        // New reset (next week), 60s later, similar usage — accepted as new week.
        record_sample(&mut samples, sample("acct", 2 * WEEK_SECS, 90.0, 1_060), 0);
        assert_eq!(samples.len(), 2);
    }

    #[test]
    fn prunes_samples_older_than_cutoff() {
        let mut samples = vec![sample("acct", WEEK_SECS, 5.0, 100)];
        record_sample(&mut samples, sample("acct", 5 * WEEK_SECS, 5.0, 10_000), 1_000);
        assert_eq!(samples.len(), 1);
        assert_eq!(samples[0].sampled_at, 10_000);
    }

    /// Two completed weeks both reaching ~76% at 62% through the week; the
    /// in-progress week queried at the same point should expect ~76% (so 71%
    /// used reads as reserve, not deficit) — the exact case from the screenshots.
    #[test]
    fn evaluates_expected_from_completed_weeks() {
        let now = 100 * WEEK_SECS;
        let current_reset = now + (64 * 3600); // 2d16h to reset → ~62% elapsed
        let mut samples = Vec::new();

        for week in 1..=2i64 {
            let reset = now - week * WEEK_SECS; // completed week resets in the past
            // sample at 62% through that week → used 76%
            let sampled = reset - (64 * 3600);
            samples.push(sample("acct", reset, 76.0, sampled));
            // an earlier point and a near-end point to flesh out the curve
            samples.push(sample("acct", reset, 30.0, reset - 5 * 86_400));
            samples.push(sample("acct", reset, 88.0, reset - 6 * 3600));
        }

        let pace = evaluate_samples(&samples, "acct", current_reset, WEEK_MINUTES, now)
            .expect("two completed weeks should yield a curve");
        assert!(
            (pace.expected_percent - 76.0).abs() < 1.0,
            "expected ~76%, got {}",
            pace.expected_percent
        );
        // Neither completed week hit the cap → 0 run-out risk.
        assert_eq!(pace.run_out_probability, Some(0.0));
    }

    #[test]
    fn returns_none_without_enough_history() {
        let now = 100 * WEEK_SECS;
        let current_reset = now + 3600;
        // Only one completed week.
        let samples = vec![sample("acct", now - WEEK_SECS, 50.0, now - WEEK_SECS - 3600)];
        assert!(evaluate_samples(&samples, "acct", current_reset, WEEK_MINUTES, now).is_none());
    }

    #[test]
    fn run_out_probability_counts_capped_weeks() {
        let now = 100 * WEEK_SECS;
        let current_reset = now + (84 * 3600);
        let mut samples = Vec::new();
        // Week A peaks at 100% (ran out); week B peaks at 60%.
        let reset_a = now - WEEK_SECS;
        samples.push(sample("acct", reset_a, 40.0, reset_a - 5 * 86_400));
        samples.push(sample("acct", reset_a, 100.0, reset_a - 3600));
        let reset_b = now - 2 * WEEK_SECS;
        samples.push(sample("acct", reset_b, 30.0, reset_b - 5 * 86_400));
        samples.push(sample("acct", reset_b, 60.0, reset_b - 3600));

        let pace =
            evaluate_samples(&samples, "acct", current_reset, WEEK_MINUTES, now).expect("curve");
        assert_eq!(pace.run_out_probability, Some(0.5));
    }
}
