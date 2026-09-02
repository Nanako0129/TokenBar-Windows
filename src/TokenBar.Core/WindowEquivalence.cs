using TokenBar.Interop;

namespace TokenBar.Core;

// Port of TokenBarCore/WindowEquivalence.swift, comments included: they record
// the defect each rule exists to prevent.
//
// "10% of this subscription's quota is worth roughly this much local usage."
//
// Computed live from the window's own samples every time. There is no
// token-per-percent coefficient anywhere in this file, and there must never be
// one: the same subscription measured 21x apart between its session window and
// its weekly window on the same day.

public static class WindowEquivalence
{
    /// <summary>
    /// Three facts a single <c>bool attempted</c> collapsed into one: never
    /// asked, asked and the fetch threw, asked and it landed. <see cref="LiveRow"/>
    /// used to take a <c>bool</c> here, and <c>DashboardModel.FetchLazyWanted</c>
    /// published <c>WindowUsageAttempted = true</c> with <c>WindowUsage</c> still
    /// null when the fetch threw — so a caller reading only "attempted" saw an
    /// empty message list and reported <see cref="Row.Unaccounted"/> ("no usage
    /// was recorded"), which is false about a scan that never ran to
    /// completion. <see cref="Failed"/> exists so that call site does not have
    /// to remember to also check whether the data actually arrived — the type
    /// itself carries the distinction.
    /// <para>
    /// Also fixes an unrelated defect the old two-bool call
    /// (<c>LiveRow(declared, attempted, …)</c>) invited: <c>declared</c> and
    /// <c>attempted</c> were both <c>bool</c>, so a caller that transposed them
    /// still compiled — silently turning "loading" into "unclassified" or vice
    /// versa. <c>declared</c> stays a <c>bool</c>; this is a distinct enum, so
    /// the two parameters can no longer be swapped without a compile error.
    /// </para>
    /// </summary>
    public enum FetchOutcome
    {
        /// <summary>The fetch has not been asked for yet, or is still in
        /// flight.</summary>
        NotAttempted,

        /// <summary>Asked, and the fetch threw — distinct from
        /// <see cref="NotAttempted"/> (still waiting for a first answer) and
        /// from <see cref="Succeeded"/> with an empty result (asked, and the
        /// answer was "nothing here"). An empty <c>messages</c> list under
        /// this outcome means "we do not know", not "there is nothing".</summary>
        Failed,

        /// <summary>Asked, and the fetch returned — <c>messages</c> is the
        /// real answer, even when it is empty.</summary>
        Succeeded,
    }

    /// <summary>
    /// The token count a quota ratio may divide, for ONE message.
    /// <para>
    /// A function rather than a convention, because this ratio is computed in
    /// two places — <see cref="LiveRow"/> for the live window and
    /// <see cref="QuotaEquivalenceFold"/>'s cycle span for the pooled history —
    /// and they are rendered one above the other in the Quota lens. When the
    /// basis was a convention rather than a name, the two disagreed: fixing the
    /// pooled path alone left the card on top showing the old inflated price
    /// beside the corrected history directly below it.
    /// </para>
    /// <para>
    /// The FULL count, cache reads included, because the cost it is divided
    /// into is <see cref="WindowMessage.Cost"/> — the message's whole priced
    /// cost — and that cannot be narrowed to match a smaller count:
    /// <see cref="WindowMessage"/> carries one cost, not one per token class.
    /// Excluding cache reads therefore priced one set of tokens and counted
    /// another, and on a Claude Code workload the excluded share is most of the
    /// volume (issue #237).
    /// </para>
    /// <para>
    /// Deliberately NOT the bars' basis. The card geometry that draws bars
    /// sizes them from <c>TokensExCacheRead</c> because including cache reads
    /// decouples them from the quota line. Different question, different
    /// basis — but each stated where it is used, so a third caller has
    /// something to call rather than a precedent to copy.
    /// </para>
    /// </summary>
    public static long RatioTokens(WindowMessage message) => message.Tokens;

    /// <summary>The provider reports whole percents, so a measured Δ carries
    /// ±0.5.</summary>
    public const double QuantisationHalfStep = 0.5;

    /// <summary>How much relative error the displayed ratio may carry.</summary>
    public const double Tolerance = 0.10;

    /// <summary>Derived, not chosen: ±0.5/Δ ≤ tolerance ⇒ Δ ≥ 5.</summary>
    public static double MinimumDelta => QuantisationHalfStep / Tolerance;

    /// <summary>
    /// One row as displayed. An abstract record with sealed nested cases —
    /// Swift's closed enum has no direct C# equivalent, and this shape keeps
    /// value equality (record) and exhaustive-by-convention pattern matching
    /// (a private base constructor, so no case can be declared outside this
    /// file) without a tag-plus-nullable-fields struct: the cases below carry
    /// different, non-overlapping data, and cramming all of it into one shape
    /// would make illegal states representable again.
    /// <para>
    /// Named <c>Row</c> rather than lowercase to match C# type conventions;
    /// the live-window computation Swift names <c>row(samples:messages:)</c> is
    /// <see cref="LiveRow"/> here for the same reason — C# has one namespace
    /// for types and members, so the two cannot share a name the way Swift's
    /// case-sensitive <c>Row</c> type and <c>row</c> function do.
    /// </para>
    /// </summary>
    public abstract record Row
    {
        private Row()
        {
        }

        /// <summary>Tokens and cost equivalent to one tenth of the window's
        /// quota.</summary>
        public sealed record Ratio(long TokensPerTenth, double CostPerTenth, int ErrorPercent) : Row;

        /// <summary>Quota moved, but not far enough to survive the 1%
        /// quantisation. <c>DeltaPercent</c> is always &gt; 0 here, so
        /// <c>ErrorPercent</c> is defined.</summary>
        public sealed record Insufficient(double DeltaPercent, int ErrorPercent) : Row;

        /// <summary>Two or more samples, but the reading never moved. Kept
        /// separate from <see cref="Insufficient"/> because the error term is
        /// 0.5/delta — undefined here, and a caller that folded the two would
        /// divide by zero.</summary>
        public sealed record NotMoved : Row;

        /// <summary>Quota moved and this machine saw none of it — a zero here
        /// would read as "1% is free", when it means "we cannot see
        /// it".</summary>
        public sealed record Unaccounted(double DeltaPercent) : Row;

        /// <summary>Fewer than two samples inside the window, so no Δ exists at
        /// all.</summary>
        public sealed record Unavailable : Row;

        /// <summary>
        /// Money estimated, tokens not — the mirror of <see cref="TokensOnly"/>.
        /// <para>
        /// Reachable when enough cycles carry a price and too few carry
        /// tokens, which the supported cost-only row shape makes ordinary
        /// rather than exotic. Splitting the two estimates without splitting
        /// the threshold that guards them let a token figure derived from a
        /// single cycle sit beside a properly supported money figure, on one
        /// line, with one error bar that described only the money.
        /// </para>
        /// </summary>
        public sealed record CostOnly(double CostPerTenth, int ErrorPercent) : Row;

        /// <summary>
        /// Tokens estimated, money not — the admitted cycles carry usage the
        /// pricing tables could not value.
        /// <para>
        /// Kept apart from <see cref="Ratio"/> rather than reported with a zero
        /// dollar figure, which would read as "this quota is free". Unlike
        /// <see cref="Ratio"/> this is not gated on the tolerance: the spread
        /// row exists to avoid quoting one money figure the cycles disagree
        /// about, and here there is no money figure at all, so the token
        /// estimate with its own error bar is the entire answer rather than
        /// half of one.
        /// </para>
        /// </summary>
        public sealed record TokensOnly(long TokensPerTenth, int ErrorPercent) : Row;

        /// <summary>
        /// The cycles are fine; there are not enough of them yet.
        /// <para>
        /// Distinct from <see cref="Insufficient"/>, which it was folded into
        /// and which says the readings are too coarse to measure. On live data
        /// two cycles of 35% and 97% rendered as "quota moved only 132% — too
        /// little to estimate (+/-1%)": every clause false, and the one number
        /// the reader could check contradicted the sentence around it. Nothing
        /// about that window is too small. There are two of it.
        /// </para>
        /// </summary>
        public sealed record TooFewCycles(int Count, int Needed) : Row;

        /// <summary>
        /// Nothing has been declared, so no usage can be charged to this
        /// subscription and the ratio has no numerator.
        /// <para>
        /// Kept apart from <see cref="Unaccounted"/>, which it would otherwise
        /// be indistinguishable from: that one says the quota moved and this
        /// machine recorded nothing, which for an undeclared user is false and
        /// alarming. The machine recorded plenty; the app has not been told
        /// whose it is. Most users never open that Settings page, so this is
        /// the common case, not an edge one.
        /// </para>
        /// </summary>
        public sealed record Undeclared : Row;

        /// <summary>
        /// The message scan this row's evidence would come from has not
        /// landed yet — distinct from <see cref="Unaccounted"/>, which claims
        /// the scan ran and found nothing.
        /// <para>
        /// Reachable only from <see cref="LiveRow"/>: the live card's quota
        /// samples and its message export are two separate fetches, so a
        /// window can already be charting a running cycle while the export
        /// that would prove usage is still in flight. An empty message list
        /// at that moment means "not looked yet", not "nothing there", and
        /// <see cref="Unaccounted"/>'s copy ("no usage was recorded") is false
        /// about a scan that has not run.
        /// </para>
        /// </summary>
        public sealed record Loading : Row;

        /// <summary>
        /// The message scan this row's evidence would come from was
        /// attempted and threw — distinct from both <see cref="Loading"/>
        /// (still waiting for a first answer) and <see cref="Unaccounted"/>
        /// (the scan ran to completion and genuinely found nothing).
        /// <para>
        /// Reachable only from <see cref="LiveRow"/> with
        /// <see cref="FetchOutcome.Failed"/>: <c>DashboardModel</c> publishes
        /// completion even when the underlying fetch throws (so a lens
        /// waiting on "not yet" does not wait forever), and that completion
        /// carries no messages. Reporting that as <see cref="Unaccounted"/>
        /// told a user who declared everything that none of their usage was
        /// recorded, when in fact nothing had been read at all.
        /// </para>
        /// </summary>
        public sealed record ScanFailed : Row;

        /// <summary>
        /// The cycles disagree by more than the tolerance, so there is no
        /// single figure to give — only the span they cover.
        /// <para>
        /// This is not a softer <see cref="Insufficient"/>. That one means the
        /// readings are too coarse to measure; this means they were measured
        /// fine and the underlying rate genuinely moved. A plan change does
        /// exactly that, and the store keys its series on (provider, account,
        /// window) with no plan recorded, so a Plus-to-Pro upgrade lands in one
        /// series with the ratio changing partway and nothing marking where.
        /// </para>
        /// </summary>
        public sealed record Spread(
            long LowPerTenth, long HighPerTenth, double LowCostPerTenth, double HighCostPerTenth) : Row;

        /// <summary>Whether an estimate was actually produced. Named so
        /// assertions can state the admission rule without re-deriving it from
        /// a pattern match.</summary>
        public bool IsRatio => this is Ratio;
    }

    /// <summary>The row as displayed. A pure function because the alternative —
    /// format strings inline in the view layer — has no seam to assert, and
    /// macOS paid for that seam once: a literal <c>%</c> in one of the raw
    /// format strings segfaulted the app under <c>String(format:)</c>. C#'s
    /// <c>string.Format</c> has no such trap, but the pure-function shape and
    /// the localization keys are kept identical anyway, so this file stays the
    /// single place either platform's row copy is asserted from.</summary>
    public static string Text(Row row, Func<long, string> tokens, Func<double, string> money) => row switch
    {
        Row.Ratio r => "10% of quota ~ {0} · {1} API-equivalent, ±{2}%".Localized(
            tokens(r.TokensPerTenth), money(r.CostPerTenth), r.ErrorPercent),
        Row.Insufficient r => "Quota moved only {0}% — too little to estimate (±{1}%)".Localized(
            RoundedInt(r.DeltaPercent), r.ErrorPercent),
        Row.NotMoved => "Quota has not moved yet".Localized(),
        Row.Unaccounted r => "Quota moved {0}%, none of it recorded on this machine".Localized(
            RoundedInt(r.DeltaPercent)),
        Row.Unavailable => "Not enough quota readings yet".Localized(),
        Row.CostOnly r => "10% of quota ~ {0} API-equivalent, tokens unavailable, ±{1}%".Localized(
            money(r.CostPerTenth), r.ErrorPercent),
        Row.TokensOnly r => "10% of quota ~ {0}, unpriced models, ±{1}%".Localized(
            tokens(r.TokensPerTenth), r.ErrorPercent),
        Row.TooFewCycles r => "{0} of {1} windows recorded — the estimate needs that many".Localized(
            r.Count, r.Needed),
        Row.Undeclared => "Classify your usage in Settings to see what this window is worth".Localized(),
        Row.Loading => "Reading local usage…".Localized(),
        Row.ScanFailed => "Local usage could not be read.".Localized(),
        Row.Spread r => "10% of quota ~ {0}-{1} · {2}-{3} API-equivalent".Localized(
            tokens(r.LowPerTenth), tokens(r.HighPerTenth), money(r.LowCostPerTenth), money(r.HighCostPerTenth)),
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Unhandled WindowEquivalence.Row case"),
    };

    private static int RoundedInt(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>A <see cref="double"/> back to <see cref="long"/> without
    /// trapping.
    /// <para>
    /// A cast outside <see cref="long"/>'s range or from NaN is undefined in
    /// C# (unchecked context truncates to an unspecified value rather than
    /// trapping the way Swift's <c>Int64(_:)</c> does, but the values here are
    /// ratios: a saturated token count over a small quota delta scales past
    /// <see cref="long"/> long before it means anything, and an unspecified
    /// truncated value is exactly as wrong as a trap). Bounded explicitly here
    /// so the result is always a defined, honest saturation.
    /// </para>
    /// </summary>
    public static long Clamped(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }

        if (value >= long.MaxValue)
        {
            return long.MaxValue;
        }

        if (value <= long.MinValue)
        {
            return long.MinValue;
        }

        return (long)value;
    }

    /// <summary>One quota reading, the minimum <see cref="LiveRow"/> needs:
    /// when and how much of the allowance had been used. Swift's
    /// <c>QuotaSample</c> (<c>WindowCardGeometry.swift</c>) is not ported here —
    /// this slice does not touch the card geometry it belongs to — so a small
    /// local shape carries the same two fields for this function's
    /// signature.</summary>
    public readonly record struct Sample(long AtMs, double UsedPercent);

    /// <summary>
    /// <paramref name="messages"/> must already be filtered to this
    /// subscription's attributed usage. <paramref name="samples"/> must be the
    /// ones inside the window, in time order.
    /// <para>
    /// <paramref name="declared"/> and <paramref name="attempt"/> are
    /// required, not optional, for the same reason <see cref="Aggregate"/>
    /// takes <c>declared</c>: an empty <paramref name="messages"/> list is
    /// ambiguous on its own — "filtered to nothing because nothing is
    /// declared", "not scanned yet", "the scan threw" and "scanned, nothing
    /// there" are four different facts that arrive as the same empty list,
    /// and only the caller knows which one it has. A caller that skips
    /// either parameter does not compile, which is the point: round 4 added
    /// a caller that skipped both, silently, because neither was asked for.
    /// </para>
    /// </summary>
    public static Row LiveRow(
        bool declared, FetchOutcome attempt, IReadOnlyList<Sample> samples, IReadOnlyList<WindowMessage> messages)
    {
        if (!declared)
        {
            return new Row.Undeclared();
        }

        switch (attempt)
        {
            case FetchOutcome.NotAttempted:
                return new Row.Loading();
            case FetchOutcome.Failed:
                return new Row.ScanFailed();
        }

        if (samples.Count < 2)
        {
            return new Row.Unavailable();
        }

        var first = samples[0];
        var last = samples[^1];
        var delta = last.UsedPercent - first.UsedPercent;
        if (delta <= 0)
        {
            return new Row.NotMoved();
        }

        // Numerator and denominator must cover the same interval or the ratio
        // means nothing — hence the span between samples, not the whole
        // window.
        long tokens = 0;
        var cost = 0.0;
        foreach (var message in messages)
        {
            if (message.Timestamp <= first.AtMs || message.Timestamp > last.AtMs)
            {
                continue;
            }

            tokens = tokens.SaturatingAdd(RatioTokens(message));
            cost += message.Cost;
        }

        var error = RoundedInt(QuantisationHalfStep / delta * 100);

        // "Recorded" means either kind of evidence. A provider row can carry a
        // cost with no token components, and the pooled path already admits
        // one; requiring tokens here made the single-window footer say "none
        // of it recorded on this machine" about usage sitting in the same
        // scan.
        if (tokens <= 0 && cost <= 0)
        {
            return new Row.Unaccounted(delta);
        }

        if (delta < MinimumDelta)
        {
            return new Row.Insufficient(delta, error);
        }

        // Clamping, not truncating: a saturated token count over a small
        // delta scales past `long`, and a plain cast is undefined outside the
        // range.
        var perTenth = Clamped(Math.Round(tokens / delta * 10, MidpointRounding.AwayFromZero));

        // Same split as the pooled path, for the same reason. Admitting
        // either kind of evidence above and then always answering `.Ratio`
        // presented the MISSING metric as a measured zero: an unpriced window
        // read as a $0 API equivalent, and a cost-only one as 0 tokens. Both
        // are unavailable, which is a different claim.
        if (cost <= 0)
        {
            return new Row.TokensOnly(perTenth, error);
        }

        if (tokens <= 0)
        {
            return new Row.CostOnly(cost / delta * 10, error);
        }

        return new Row.Ratio(perTenth, cost / delta * 10, error);
    }

    /// <summary>A cycle's contribution to the pooled estimate.</summary>
    /// <param name="DeltaPercent">The cycle's own <see cref="QuotaCycle.UsedPercent"/>.</param>
    /// <param name="SpanTokens">Restricted to the cycle's observed sample
    /// span — the only interval the delta describes.</param>
    /// <param name="SpanCost">Same restriction as <paramref name="SpanTokens"/>.</param>
    /// <param name="ObservedFraction">The cycle's own
    /// <see cref="QuotaCycle.ObservedFraction"/>.</param>
    public sealed record Cycle(double DeltaPercent, long SpanTokens, double SpanCost, double ObservedFraction);

    /// <summary>Below this the cycle was barely witnessed, so its delta
    /// describes a stretch the app mostly missed. <see cref="QuotaCycle"/>
    /// already carried the fraction and said so in its own comment; nothing
    /// downstream was gating on it.</summary>
    public const double MinimumObservedFraction = 0.5;

    /// <summary>Fewer than this and the leave-one-out spread is not computable
    /// in any meaningful way.</summary>
    public const int MinimumCycles = 3;

    // Both gates above also exclude a one-sample cycle, and they do it
    // structurally rather than by where the numbers are set: such a cycle's
    // delta is `max - min` over a single reading and its observed fraction is
    // the span between first and last sample, so BOTH are exactly zero
    // (QuotaHistoryFold.Cycles). Lowering either constant therefore does not
    // start admitting them — worth knowing before anyone assumes it does.

    /// <summary>
    /// One estimate pooled over several cycles.
    /// <para>
    /// Sum the deltas, sum the spend, divide ONCE. Not the average of
    /// per-cycle ratios: quantisation is an absolute ±0.5 on each delta, so a
    /// cycle's relative noise runs as 1/delta, and pooling weights each cycle
    /// by the evidence it carries while averaging gives a 5-point cycle the
    /// same say as a 98-point one.
    /// </para>
    /// <para>
    /// That distinction is the whole feature. Measured 2026-08-17 on 14 live
    /// cycles: the per-cycle ratios spread 2.7x (38% half-range), while the
    /// pooled estimate agreed to 1% between independent halves and carries a
    /// jackknife standard error of 5%. Reporting the per-cycle spread as the
    /// estimate's error — which this function used to do — overstated it by
    /// most of an order of magnitude and made a usable number look unusable.
    /// </para>
    /// <para>
    /// Denominated in money. Tokens are also returned, but cost is the
    /// steadier of the two here (5% against 7%), which is consistent with
    /// providers metering on something closer to cost than to a token count.
    /// <paramref name="declared"/> is whether the user has classified ANY
    /// source. It belongs here rather than at the call site because the
    /// distinction it makes is this type's own: with nothing declared every
    /// message resolves to unassigned, so every cycle arrives carrying zero
    /// spend, and the rules below would report that as "the quota moved and
    /// none of it was recorded on this machine". That sentence describes a
    /// data failure. The actual state is a missing declaration, and it is the
    /// state most users are in.
    /// </para>
    /// </summary>
    public static Row Aggregate(bool declared, IReadOnlyList<Cycle> cycles)
    {
        if (!declared)
        {
            return new Row.Undeclared();
        }

        // Admission is about EVIDENCE, not about money. Gating on `spanCost`
        // alone rejected every cycle whose models carry no price — the tokens
        // were recorded and the quota moved, and the caller then reported
        // that as "none of it recorded on this machine", which is false about
        // the one thing it could see.
        var admitted = cycles.Where(cycle =>
            cycle.DeltaPercent >= MinimumDelta
            && cycle.ObservedFraction >= MinimumObservedFraction
            && (cycle.SpanCost > 0 || cycle.SpanTokens > 0)).ToList();

        if (admitted.Count == 0)
        {
            var anyMovement = cycles.Sum(cycle => cycle.DeltaPercent);
            if (cycles.Count == 0)
            {
                return new Row.Unavailable();
            }

            if (anyMovement <= 0)
            {
                return new Row.NotMoved();
            }

            // Both kinds of evidence, like the admission gate above. This
            // classifier kept the old cost-only predicate, so cycles carrying
            // real tokens from unpriced models — none of them large enough to
            // be admitted — were still called usage nobody recorded.
            // Reachable: the history card calls `Aggregate` with no
            // prefilter, so that card could print "none of it recorded on
            // this machine" above rows listing the tokens it recorded.
            if (cycles.All(cycle => cycle.SpanCost <= 0 && cycle.SpanTokens <= 0))
            {
                return new Row.Unaccounted(anyMovement);
            }

            return new Row.Insufficient(
                anyMovement,
                RoundedInt(QuantisationHalfStep * cycles.Count / anyMovement * 100));
        }

        // Count, not size: these cycles each cleared `MinimumDelta` on their
        // own, so saying the quota "moved only" their sum is false, and the
        // error term computed from that sum is small precisely because the
        // movement was large. What is missing is cycles to compare.
        if (admitted.Count < MinimumCycles)
        {
            return new Row.TooFewCycles(admitted.Count, MinimumCycles);
        }

        static double Pooled(IReadOnlyList<Cycle> set, Func<Cycle, double> pick)
        {
            var delta = set.Sum(cycle => cycle.DeltaPercent);
            return delta > 0 ? set.Sum(pick) / delta : 0;
        }

        // Each estimate pools over the cycles that carry ITS evidence. A
        // cycle contributing quota delta to a denominator while contributing
        // nothing to the numerator drags that estimate down in proportion to
        // how much of the history it represents — true of unpriced cycles for
        // the money figure, and equally true of cost-only cycles for the
        // token figure. Filtering one and not the other was the same defect
        // twice, and only the first half was fixed.
        var priced = admitted.Where(cycle => cycle.SpanCost > 0).ToList();
        var tokenBearing = admitted.Where(cycle => cycle.SpanTokens > 0).ToList();
        var costRatio = Pooled(priced, cycle => cycle.SpanCost);
        var tokenRatio = Pooled(tokenBearing, cycle => cycle.SpanTokens);

        // Leave-one-out spread of a pooled estimate, relative to the
        // estimate.
        static double JackknifeRelative(IReadOnlyList<Cycle> set, Func<Cycle, double> pick)
        {
            var estimate = Pooled(set, pick);
            if (set.Count < MinimumCycles || estimate <= 0)
            {
                return double.PositiveInfinity;
            }

            var n = set.Count;
            var leaveOneOut = new List<double>(n);
            for (var index = 0; index < set.Count; index++)
            {
                var rest = new List<Cycle>(n - 1);
                for (var other = 0; other < set.Count; other++)
                {
                    if (other != index)
                    {
                        rest.Add(set[other]);
                    }
                }

                leaveOneOut.Add(Pooled(rest, pick));
            }

            var mean = leaveOneOut.Sum() / n;
            var variance = (double)(n - 1) / n * leaveOneOut.Sum(value => (value - mean) * (value - mean));
            return Math.Sqrt(variance) / estimate;
        }

        // No defensible money figure: report what IS known rather than a
        // dollar amount pooled over cycles that carry no price. And if
        // neither estimate has enough cycles behind it, say that instead of
        // inventing one from the handful that do.
        if (priced.Count < MinimumCycles)
        {
            if (tokenBearing.Count < MinimumCycles)
            {
                return new Row.TooFewCycles(Math.Max(priced.Count, tokenBearing.Count), MinimumCycles);
            }

            var tokenError = JackknifeRelative(tokenBearing, cycle => cycle.SpanTokens);
            return new Row.TokensOnly(
                Clamped(Math.Round(tokenRatio * 10, MidpointRounding.AwayFromZero)),
                double.IsFinite(tokenError) ? Math.Max(1, RoundedInt(tokenError * 100)) : 0);
        }

        // Jackknife: recompute the pooled estimate with each cycle left out
        // and take the spread of those. It measures the ESTIMATE's stability
        // rather than the observations' scatter, and it needs nothing beyond
        // what is already stored.
        //
        // It does NOT catch a plan change, and this comment used to claim it
        // did. macOS's own history contains a Pro-to-Plus downgrade and the
        // jackknife still reported 5%. That is structural, not bad luck: this
        // statistic reads how much the cycles disagree with each other,
        // while a plan change alters the UNIT each cycle's `DeltaPercent` is
        // denominated in — 3% of a Pro allowance and 3% of a Plus one are
        // different absolute quantities. The unit moves, the dispersion does
        // not, and an error bar built on dispersion cannot see it.
        //
        // So do not build anything else on the assumption that a widening
        // error bar will flag a regime change. Until the store records a plan
        // per sample and this fold refuses to pool across two of them, an
        // estimate spanning a plan change is silently wrong and nothing here
        // can tell.
        // The same threshold on the other estimate. Enough priced cycles
        // says nothing about how many carried tokens, and publishing a token
        // figure from fewer than `MinimumCycles` would breach the rule this
        // function enforces two guards above — quietly, because the error bar
        // beside it is the money's.
        if (tokenBearing.Count < MinimumCycles)
        {
            var costError = JackknifeRelative(priced, cycle => cycle.SpanCost);
            return new Row.CostOnly(
                costRatio * 10,
                double.IsFinite(costError) ? Math.Max(1, RoundedInt(costError * 100)) : 0);
        }

        // BOTH estimates are jackknifed, and the row is only a `.Ratio` if
        // both hold. The cost error alone used to decide it, and the row
        // then published a token figure under the cost's error bar: cycles
        // with stable cost per point but differently priced models have
        // unstable tokens per point, so "1% error" could sit beside a token
        // number the cycles disagreed about wildly. The bar was measured on
        // one quantity and printed beside two.
        var costErr = JackknifeRelative(priced, cycle => cycle.SpanCost);
        var tokenErr = JackknifeRelative(tokenBearing, cycle => cycle.SpanTokens);
        var relativeError = Math.Max(costErr, tokenErr);

        if (relativeError > Tolerance)
        {
            var ratios = priced.Select(cycle => cycle.SpanCost / cycle.DeltaPercent * 10).ToList();
            var tokensPerCycle =
                tokenBearing.Select(cycle => (double)cycle.SpanTokens / cycle.DeltaPercent * 10).ToList();
            return new Row.Spread(
                LowPerTenth: Clamped(Math.Round(tokensPerCycle.Min(), MidpointRounding.AwayFromZero)),
                HighPerTenth: Clamped(Math.Round(tokensPerCycle.Max(), MidpointRounding.AwayFromZero)),
                LowCostPerTenth: ratios.Min(),
                HighCostPerTenth: ratios.Max());
        }

        return new Row.Ratio(
            Clamped(Math.Round(tokenRatio * 10, MidpointRounding.AwayFromZero)),
            costRatio * 10,
            Math.Max(1, RoundedInt(relativeError * 100)));
    }
}
