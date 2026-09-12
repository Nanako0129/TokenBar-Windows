using TokenBar.Interop;

namespace TokenBar.Core;

// Card-view label qualification — port of TokenBarCore/AgentUsage.swift's
// `uniqueCardWindows` / `qualifyingRepeatedLabels` / `windowPeriod` (issue
// #286).
//
// Codex reports its Spark allowance as one additional limit carrying both a
// primary and a secondary window — 5 hours and 7 days — and the engine names
// both of them "Codex Spark" because the label belongs to the limit, not the
// window. The two rows are different windows with different card IDs, reset
// schedules and histories, so every surface that drew the label alone offered
// two identical, indistinguishable choices.
//
// Defined here rather than alongside <see cref="AgentUsageSnapshot"/> in
// TokenBar.Interop: composing a qualified label needs the localization
// vocabulary (<see cref="Localization"/>, <see cref="UsagePace"/>), and
// TokenBar.Interop does not and should not depend on TokenBar.Core. A C# 14
// extension property keeps the call site identical to an instance property —
// none of the six render sites that already read
// <c>agent.UniqueCardWindows</c> need to change.
public static class AgentUsageQualifier
{
    extension(AgentUsageSnapshot agent)
    {
        /// <summary>
        /// The card view every consumer draws from: <see
        /// cref="AgentUsageSnapshot.RawCardWindows"/> with a label repeated
        /// across windows disambiguated by <see cref="QualifyingRepeatedLabels"/>.
        /// No card ID, window key or persisted selection changes — only the
        /// <c>Label</c> text a repeated-label window carries.
        /// </summary>
        public IReadOnlyList<UsageWindow> UniqueCardWindows =>
            QualifyingRepeatedLabels(agent.RawCardWindows, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Appends a qualifier to a label that another window in the same card
    /// view also carries. A label that appears once is returned untouched, so
    /// every existing single-window presentation is unchanged.
    ///
    /// Three sources of evidence, tried in order, because the FIRST one can be
    /// withdrawn by the provider at exactly the moment the rows are otherwise
    /// indistinguishable:
    ///
    /// 1. <c>DurationSeconds</c> — the window's own length, named in the app's
    ///    vocabulary (Session, Weekly, or the span).
    /// 2. <c>ResetsAt</c> — the span until this row's own reset. A Codex
    ///    window with no usage yet resolves to <c>unavailable(invalidEvidence)</c>,
    ///    which clears the duration AND window minutes, so a pair reported at
    ///    100% remaining — the state issue #286 was filed from — carries no
    ///    length at all. The countdown is what the row already displays, and
    ///    it is true by construction rather than a period inferred from one.
    /// 3. Position in the card view — a deterministic ordinal. Reached only
    ///    when a group has neither lengths nor resets that separate it, and
    ///    present because #286 requires that unusable duration evidence still
    ///    yields a unique name rather than the identical pair it reports.
    ///
    /// A tier is taken only when it names EVERY window of the group and names
    /// them all differently; two windows of one period would otherwise be
    /// handed a distinction that is not there. The reservation set spans the
    /// whole card view, not just the group being qualified: a label of a
    /// window that is NOT being qualified is reserved too, and the ordinal
    /// walks past anything already used, because a provider really can emit a
    /// separator-shaped label that collides with a generated one.
    /// </summary>
    internal static IReadOnlyList<UsageWindow> QualifyingRepeatedLabels(
        IReadOnlyList<UsageWindow> windows, DateTimeOffset now)
    {
        var counts = new Dictionary<string, int>();
        foreach (var window in windows)
        {
            counts[window.Label] = counts.GetValueOrDefault(window.Label) + 1;
        }
        if (!counts.Values.Any(count => count > 1))
        {
            return windows;
        }

        // What a window that is NOT being qualified will render as. A
        // generated name has to avoid these too: a snapshot holding two "Foo"
        // windows and one already labelled "Foo · 1" would otherwise be given
        // a second "Foo · 1", and uniqueness inside the group says nothing
        // about that.
        var untouched = new HashSet<string>(
            windows.Where(window => counts[window.Label] == 1).Select(window => window.Label));

        string Compose(string label, string qualifier) =>
            "{0} · {1}".Localized(label.Localized(), qualifier);

        Dictionary<string, List<string>> Tier(Func<UsageWindow, string?> candidate)
        {
            var byLabel = new Dictionary<string, List<string>>();
            foreach (var window in windows)
            {
                if (counts[window.Label] <= 1)
                {
                    continue;
                }
                var value = candidate(window);
                if (value is null)
                {
                    byLabel[window.Label] = [];
                    continue;
                }
                if (byLabel.TryGetValue(window.Label, out var empty) && empty.Count == 0)
                {
                    continue;
                }
                if (!byLabel.TryGetValue(window.Label, out var list))
                {
                    list = [];
                    byLabel[window.Label] = list;
                }
                list.Add(value);
            }

            return byLabel
                .Where(entry =>
                    entry.Value.Count == counts[entry.Key] &&
                    entry.Value.Distinct().Count() == entry.Value.Count &&
                    entry.Value.All(value => !untouched.Contains(Compose(entry.Key, value))))
                .ToDictionary(entry => entry.Key, entry => entry.Value);
        }

        var byLength = Tier(window =>
            window.DurationSeconds is { } duration && duration > 0 ? WindowPeriod(duration) : null);

        // The SAME span the countdown in the row prints, rounding included:
        // UsagePace.DurationText alone rounds to the nearest minute while the
        // countdown takes minutes up, which put "4h 59m" in a name beside
        // "Resets in 5h" in the same row for the first half of every minute.
        var byReset = Tier(window =>
            window.ResetsAt is { } resetsAt && UsagePace.ParseRfc3339(resetsAt) is { } reset
                ? UsagePace.SpanText(reset, now)
                : null);

        // The occurrence index within its own repeated-label group: the
        // position each tier's candidates were collected at, and the seed for
        // the ordinal the last tier falls back to. The ordinal walks forward
        // past anything already on screen, so it is unique against the whole
        // output rather than only against its own group.
        var taken = new Dictionary<string, int>();
        var used = new HashSet<string>(untouched);
        var result = new List<UsageWindow>(windows.Count);
        foreach (var window in windows)
        {
            if (counts[window.Label] <= 1)
            {
                result.Add(window);
                continue;
            }

            var index = taken.GetValueOrDefault(window.Label);
            taken[window.Label] = index + 1;

            string? name = null;
            if (byLength.TryGetValue(window.Label, out var lengthNames))
            {
                name = Compose(window.Label, lengthNames[index]);
            }
            else if (byReset.TryGetValue(window.Label, out var resetNames))
            {
                name = Compose(window.Label, resetNames[index]);
            }

            if (name is null || used.Contains(name))
            {
                var ordinal = index + 1;
                while (used.Contains(Compose(window.Label, ordinal.ToString())))
                {
                    ordinal += 1;
                }
                name = Compose(window.Label, ordinal.ToString());
            }

            used.Add(name);
            result.Add(window with { Label = name });
        }

        return result;
    }

    /// <summary>
    /// What kind of window this is, in the vocabulary the app already uses.
    ///
    /// The engine names Codex's MAIN rate limit from exactly these two
    /// lengths — 18,000s => "Session", 604,800s => "Weekly" in
    /// <c>codex_windows</c> — and deliberately keys on the length rather than
    /// on which slot carried it. A Spark allowance arrives in the same two
    /// shapes, so it reads with the same two words rather than in a second
    /// vocabulary of its own; both are already translated. Any other length
    /// falls back to the span itself, through the same formatter used
    /// elsewhere — deliberately not a truncation to the largest unit, which
    /// would render one hour and ninety minutes identically and rebuild the
    /// ambiguity being removed.
    /// </summary>
    private static string WindowPeriod(long seconds) => seconds switch
    {
        18_000 => "Session".Localized(),
        604_800 => "Weekly".Localized(),
        _ => UsagePace.DurationText(seconds),
    };
}
