namespace TokenBar.Core;

/// <summary>
/// The one place a quota window is named on screen — the strip card's row header
/// and the heatmap picker's entries.
/// <para>
/// Named static functions rather than composition inlined into the two cards,
/// which is how macOS writes them and for the stated reason: <em>"static, and a
/// named function rather than composed inline, so the tests can assert the
/// string directly"</em> (<c>QuotaHeatmapCard.swift:130-132</c>). The cards live
/// in <c>DashboardView.xaml.cs</c>, which no test project compiles.
/// </para>
/// <para>
/// macOS qualifies the name by account label as well. Windows has no source for
/// one, so two scopes of a client render alike here — deliberately, per this
/// slice's plan. They stay distinct in <see cref="QuotaWindowIdentity"/>, which
/// is the half that has to be right.
/// </para>
/// </summary>
public static class QuotaLabels
{
    public static string RowLabel(QuotaWindowSummary summary) =>
        Compose(summary.Id, summary.WindowLabel);

    public static string PickerLabel(QuotaHeatmapWindow window) =>
        Compose(window.Id, window.WindowLabel);

    /// <summary>
    /// <c>"&lt;client&gt; · &lt;window&gt;"</c>.
    /// <para>
    /// The window label comes from a join against the live agent-usage payload
    /// that can miss, so it can be absent — and a missing half must not leave a
    /// dangling <c>" · "</c> on screen. A no-match series keeps its identity and
    /// loses only its label, so the fallback is the series' own raw
    /// <c>WindowKey</c>: <c>session.v1</c> is meaningful and stable, and it is
    /// the value the join was looking for in the first place. Not run through
    /// <c>Localized()</c> — it is a store key, not a translation key.
    /// </para>
    /// </summary>
    private static string Compose(QuotaWindowIdentity id, string? label)
    {
        var name = ClientRegistry.Style(id.ProviderId).DisplayName;
        var window = string.IsNullOrWhiteSpace(label) ? id.WindowKey : label.Localized();
        return string.IsNullOrWhiteSpace(window) ? name : $"{name} · {window}";
    }
}
