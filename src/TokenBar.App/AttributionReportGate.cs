namespace TokenBar.App;

/// <summary>
/// The Usage-attribution page's model-report fetch guard, split out of
/// <c>SettingsWindow</c> so it can be exercised by <c>dotnet test</c> without a
/// WinUI host — that file itself compiles into no test project.
/// <para>
/// <c>SettingsWindow</c> is a singleton reused across hide/show (macOS
/// <c>isReleasedWhenClosed=false</c> parity): closing it hides it rather than
/// destroying it, so a bare "fetch once per process" guard on the report
/// request meant a provider first observed after that one fetch could never
/// appear on the page — not until the app restarted. <see cref="Reset"/> is
/// the fix: called when the window hides, it clears the guard so the next
/// visit fetches again, while leaving whatever the caller already cached
/// alone. The already-fetched report stays in place across a reset, so the
/// page keeps showing it — real rows, not "unavailable" — for as long as the
/// fresh fetch this triggers is still in flight.
/// </para>
/// </summary>
public sealed class AttributionReportGate
{
    /// <summary>True once a fetch has been asked for and not yet reset. Named
    /// separately from <see cref="Settled"/>: a fetch can be requested and
    /// still in flight, which is exactly the state a reset must be able to
    /// happen in without losing track of the outstanding request.</summary>
    public bool Requested { get; private set; }

    /// <summary>True once a fetch has returned — successfully or not — so the
    /// page can tell "no report yet" apart from "asked and it failed".
    /// Deliberately NOT cleared by <see cref="Reset"/>: the point of a reset is
    /// to ask again, not to forget the answer the last ask produced.</summary>
    public bool Settled { get; private set; }

    /// <summary>Call before starting a fetch. Returns true the first time this
    /// is called after construction or after <see cref="Reset"/>, and false on
    /// every call in between — the caller starts a fetch only when this
    /// returns true, exactly the shape the request-guard existed for.</summary>
    public bool ShouldFetch()
    {
        if (Requested)
        {
            return false;
        }

        Requested = true;
        return true;
    }

    /// <summary>Call when the fetch this gate guarded has returned.</summary>
    public void Settle() => Settled = true;

    /// <summary>Call when the settings window hides. Clears the guard so the
    /// next page visit fetches again; <see cref="Settled"/> and whatever report
    /// the caller cached are left untouched, so the page has real data to show
    /// while that fresh fetch is out.</summary>
    public void Reset() => Requested = false;
}
