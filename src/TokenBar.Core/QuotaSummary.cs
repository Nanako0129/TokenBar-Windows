using TokenBar.Interop;

namespace TokenBar.Core;

// Overview quota summary — port of TokenBarCore/QuotaSummary.swift.
//
// The one-line-of-cards answer the all-agent Overview owes the user: of
// everything you are subscribed to, what is closest to running out, and is
// anything burning faster than its schedule allows. A per-client window card
// cannot answer this on its own — it only ever sees one subscription.

/// <summary>Which window of one client is burning fastest, and by how much.
/// See <see cref="QuotaSummary.TightestAccountKey"/> for why
/// <paramref name="AccountKey"/> exists even though it is null on every
/// build today.</summary>
public sealed record BurnWarning(
    string ClientId,
    string? AccountKey,
    string Label,
    // Actual used percent minus the expected one. Always positive here — a
    // window running under its schedule is not a warning.
    double AheadPercent,
    // Borrowed verbatim from UsagePace.Presentation, the same strings the
    // Agent-limits card shows, so the summary cannot phrase a projection
    // differently from the card the user opens to check it.
    string? EtaText,
    string? RiskText);

public sealed record QuotaSummary(
    string TightestClient,
    // Which account of TightestClient the tightest window belongs to — null
    // for the primary account. Windows has no multi-account support yet, so
    // this is null on every build today; it is carried through so that slice
    // does not have to revisit identity here. Needed to tell the tightest
    // window apart from a second account's window of the same client and
    // card id when folding OtherWindows/OthersComfortable below.
    string? TightestAccountKey,
    string TightestLabel,
    double RemainingPercent,
    string? ResetsAt,
    // Windows other than the tightest that carry a usable reading.
    int OtherWindows,
    // How many of those sit at or above ComfortablePercent. Reported rather
    // than derived by the caller so "4 others, all comfortable" and "4
    // others, two of them tight" cannot render the same.
    int OthersComfortable,
    // The window furthest past its expected burn line, if any is. A second
    // question, not a restatement of the first: "tightest" is a level, this
    // is a rate.
    BurnWarning? Burning,
    // How many windows the burn check actually evaluated. Burning == null
    // has two causes that look identical from outside: every window was
    // measured and none is ahead, or none could be measured at all —
    // UsagePace.Compute returns null with pace off, while the historical
    // basis is still learning, and when a window reports no duration.
    // Without this count a caller cannot tell "checked, all clear" from
    // "nothing was asked".
    int PaceCheckedWindows)
{
    public bool AllOthersComfortable => OtherWindows > 0 && OthersComfortable == OtherWindows;
}

public static class QuotaSummaryFold
{
    /// <summary>The line between "worth mentioning" and "fine". Not a
    /// warning threshold — the Agent-limits card already owns those — just
    /// the point past which a window is not worth a line on the landing
    /// tab.</summary>
    public const double ComfortablePercent = 60;

    /// <summary>Reuses <see cref="QuotaResolver"/> for the tightest window
    /// rather than re-deriving it, so this summary and the tray quota can
    /// never name different subscriptions. The remaining counts are computed
    /// against the same eligibility rule the resolver applies: a client with
    /// an error is not evidence, and a non-finite percentage is not a
    /// reading.</summary>
    public static QuotaSummary? Build(
        AgentUsagePayload? payload,
        IReadOnlySet<string>? excluding = null,
        PaceMode paceMode = PaceMode.Historical,
        DateTimeOffset? now = null)
    {
        if (payload is null)
        {
            return null;
        }

        var tightest = QuotaResolver.Resolve(payload, QuotaResolver.Auto, excluding);
        if (tightest is null)
        {
            return null;
        }

        var nowValue = now ?? DateTimeOffset.Now;
        var others = new List<double>();
        BurnWarning? burning = null;
        var paceChecked = 0;
        foreach (var agent in payload.Agents)
        {
            if (agent.Error is not null || excluding?.Contains(agent.ClientId) == true)
            {
                continue;
            }

            foreach (var window in agent.UniqueCardWindows)
            {
                if (!double.IsFinite(window.RemainingPercent))
                {
                    continue;
                }

                // The burn check covers EVERY eligible window including the
                // tightest, because the tightest one may also be the fastest
                // and skipping it would drop the most urgent case.
                var pace = UsagePace.Compute(window, paceMode, nowValue);
                if (pace is not null)
                {
                    paceChecked++;
                }

                if (pace is not null && pace.Stage.IsDeficit()
                    && pace.DeltaPercent > (burning?.AheadPercent ?? 0))
                {
                    var shown = UsagePace.Presentation(window, paceMode, pace);
                    burning = new BurnWarning(
                        agent.ClientId, AccountKey: null, window.Label,
                        pace.DeltaPercent, shown.EtaText, shown.RiskText);
                }

                // Identity is (clientId, accountKey, cardId), not the label
                // or the client-cardId pair alone: two subscriptions can
                // both call a window "Weekly", and (once Windows has
                // multi-account) two accounts of the SAME client could both
                // offer a "session.v1" card id — either collapse would drop
                // the other one's window from the tally, or worse, skip it
                // here and never count it as tightest either. AccountKey is
                // always null today, so this compares only clientId+cardId
                // for now, matching the resolver's own current identity.
                if (agent.ClientId == tightest.ClientId
                    && window.CardId == tightest.Window.CardId)
                {
                    continue;
                }

                others.Add(window.RemainingPercent);
            }
        }

        return new QuotaSummary(
            TightestClient: tightest.ClientId,
            TightestAccountKey: null,
            TightestLabel: tightest.Window.Label,
            RemainingPercent: tightest.Window.RemainingPercent,
            ResetsAt: tightest.Window.ResetsAt,
            OtherWindows: others.Count,
            OthersComfortable: others.Count(v => v >= ComfortablePercent),
            Burning: burning,
            PaceCheckedWindows: paceChecked);
    }
}
