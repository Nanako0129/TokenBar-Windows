using System.Text.Json;

namespace TokenBar.Core;

/// <summary>UI string lookup. The English source text <em>is</em> the key, so a
/// missing entry renders exactly as it did before i18n — there is no
/// half-translated state, and adding <c>Localized()</c> to a call site is safe
/// before its translation exists. Same contract as the macOS
/// <c>String.localized</c> this ports.
///
/// Lives in Core rather than App because pace copy is assembled here, the same
/// reason macOS puts its helper in TokenBarCore.</summary>
public static class Localization
{
    private static readonly object Gate = new();
    private static IReadOnlyDictionary<string, string>? _table;
    private static string _loadedTag = string.Empty;

    /// <summary>The tag <see cref="Load"/> was last called with. "en" loads no
    /// table at all — the keys are already English — so this reporting "en"
    /// and reporting a tag whose file was missing look the same from here on
    /// purpose: both render English, and neither is an error.</summary>
    public static string CurrentTag
    {
        get { lock (Gate) return _loadedTag; }
    }

    /// <summary>Load the table for a BCP-47 tag, or clear it for English. Read
    /// once at startup: macOS reads AppleLanguages once per launch and requires
    /// a relaunch to change language, and this port keeps that contract rather
    /// than rebuilding every view against a swapped table.</summary>
    public static void Load(string tag, string assetDirectory)
    {
        lock (Gate)
        {
            _loadedTag = tag;
            _table = null;
            if (string.IsNullOrEmpty(tag) || tag == "en")
            {
                return;
            }

            try
            {
                // Dash, not dot: WinAppSDK's PRI generator reads dot-separated
                // filename segments as resource qualifiers, and "zh-Hant" is not
                // a valid one — strings.zh-Hant.json produced a PRI249 warning
                // on every build.
                var path = Path.Combine(assetDirectory, $"strings-{tag}.json");
                if (!File.Exists(path))
                {
                    return; // fail soft: every key renders as its English source
                }

                _table = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(path));
            }
            catch
            {
                // A malformed or unreadable table must not take the UI with it;
                // English is always a correct rendering.
                _table = null;
            }
        }
    }

    /// <summary>Look this English source string up in the loaded table.</summary>
    public static string Localized(this string source)
    {
        lock (Gate)
        {
            // IsNullOrEmpty, not Length: the dictionary is declared with a
            // non-nullable value type, but that is an annotation — System.Text
            // .Json will happily deserialize a JSON null into it, and reading
            // .Length on that would throw. A table entry must never be able to
            // take the UI down; English is always a correct rendering.
            return _table is not null
                && _table.TryGetValue(source, out var value)
                && !string.IsNullOrEmpty(value)
                ? value
                : source;
        }
    }

    /// <summary><see cref="Localized(string)"/> plus <c>string.Format</c>, for
    /// entries carrying positional placeholders.</summary>
    public static string Localized(this string source, params object[] args) =>
        string.Format(
            System.Globalization.CultureInfo.CurrentCulture, source.Localized(), args);

    /// <summary>Lookup for the rare key whose English rendering is not the key
    /// itself, matching macOS's <c>localized(default:)</c>. Bare "now" is too
    /// generic to be safe as a table key, so the pace duration text keys on
    /// "duration.now" and supplies its English here.
    ///
    /// Deliberately not an overload: a <c>Localized(string, string)</c> would
    /// beat the params form in resolution, and <c>"{0}% left".Localized(text)</c>
    /// would silently return <paramref name="fallback"/> instead of
    /// formatting.</summary>
    public static string LocalizedKey(this string key, string fallback)
    {
        lock (Gate)
        {
            return _table is not null
                && _table.TryGetValue(key, out var value)
                && !string.IsNullOrEmpty(value)
                ? value
                : fallback;
        }
    }
}
