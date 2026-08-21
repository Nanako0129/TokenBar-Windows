using TokenBar.Core;

namespace TokenBar.App;

/// <summary>UI language override (macOS AppLanguage). `System` follows the OS;
/// the others pin a table. A change takes effect on the next launch — macOS
/// documents the same limitation, and live switching would mean rebuilding
/// every view against a swapped table for a setting flipped once.</summary>
public static class AppLanguage
{
    public const string StorageKey = "tokenbar.language";
    public const string System = "system";
    public const string English = "en";
    public const string TraditionalChinese = "zh-Hant";

    /// <summary>Options in settings order, with the label each renders. The two
    /// concrete languages name themselves in their own language, as macOS does
    /// — a reader who cannot read the current UI language still finds theirs.</summary>
    /// A property rather than a static readonly field: <see cref="Apply"/>
    /// triggers this class's initialiser, so a field would evaluate
    /// "System".Localized() before Load ever ran and pin the English label for
    /// the process lifetime.
    public static (string Value, string Label)[] Options =>
    [
        (System, "System".Localized()),
        (English, "English"),
        (TraditionalChinese, "繁體中文"),
    ];

    /// <summary>Selecting the current value again, or a malformed one, must not
    /// prompt for a relaunch.</summary>
    public static bool RequiresRelaunch(string current, string next) =>
        current != next && Options.Any(option => option.Value == next);

    /// <summary>Resolve `system` against the OS UI language. Anything other than
    /// a language we ship falls back to English, whose keys need no table.</summary>
    public static string Resolve(string stored)
    {
        var tag = stored == System || string.IsNullOrEmpty(stored)
            ? global::System.Globalization.CultureInfo.CurrentUICulture.Name
            : stored;

        // "zh-TW" / "zh-Hant-TW" / "zh-HK" all want the Traditional table.
        if (tag.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)
            || tag.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase)
            || tag.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase)
            || tag.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase))
        {
            return TraditionalChinese;
        }

        return English;
    }

    /// <summary>Called once during startup, before any view is built.</summary>
    public static void Apply(SettingsStore store, string assetDirectory) =>
        Localization.Load(
            Resolve(store.GetString(StorageKey, System) ?? System), assetDirectory);
}
