using System.Text.Json;

namespace TokenBar.Core;

/// <summary>
/// The UserDefaults stand-in: a flat JSON dictionary of `tokenbar.*` keys.
/// Unpackaged WinUI apps have no ApplicationDataContainer, and a plain JSON
/// file is debuggable over SSH; the path is injected so tests (and macOS
/// test runs) never touch a real profile. Writes are atomic
/// (temp file + rename) so a crash mid-save can't tear the store.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, JsonElement> _values;

    /// <summary>Fires after a key's value actually changes (set to the same
    /// value is a no-op, mirroring the signature-gated observers the macOS
    /// side uses to avoid re-render loops). Raised on the writing thread.</summary>
    public event Action<string>? Changed;

    public SettingsStore(string path, Action<string>? log = null)
    {
        _path = path;
        _log = log;
        _values = Load(path);
    }

    private static Dictionary<string, JsonElement> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    File.ReadAllText(path)) ?? [];
            }
        }
        catch
        {
            // A corrupt store must not take the app down; settings reset to
            // defaults and the next write rebuilds the file.
        }

        return [];
    }

    public string? GetString(string key, string? fallback = null)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : fallback;
        }
    }

    public bool GetBool(string key, bool fallback)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var v)
                && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean() : fallback;
        }
    }

    public int GetInt(string key, int fallback)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetInt32(out var n) ? n : fallback;
        }
    }

    public double GetDouble(string key, double fallback)
    {
        lock (_gate)
        {
            // TryGetDouble, not GetDouble: a hand-edited out-of-range literal
            // (1e9999) parses into the store but throws on GetDouble.
            return _values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetDouble(out var d) ? d : fallback;
        }
    }

    public void SetString(string key, string value) =>
        Set(key, JsonSerializer.SerializeToElement(value));

    public void SetBool(string key, bool value) =>
        Set(key, JsonSerializer.SerializeToElement(value));

    public void SetInt(string key, int value) =>
        Set(key, JsonSerializer.SerializeToElement(value));

    public void SetDouble(string key, double value) =>
        Set(key, JsonSerializer.SerializeToElement(value));

    /// <summary>UserDefaults removeObject parity: reverts the key to
    /// whatever fallback its readers pass (e.g. the year filter's "all").</summary>
    public void Remove(string key)
    {
        lock (_gate)
        {
            if (!_values.Remove(key))
            {
                return;
            }

            Save();
        }

        Changed?.Invoke(key);
    }

    private void Set(string key, JsonElement value)
    {
        lock (_gate)
        {
            if (_values.TryGetValue(key, out var old)
                && old.ValueKind == value.ValueKind
                && old.GetRawText() == value.GetRawText())
            {
                return;
            }

            _values[key] = value;
            Save();
        }

        Changed?.Invoke(key);
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                new SortedDictionary<string, JsonElement>(_values), WriteOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Disk trouble (AV scan or a sync tool holding the file) must
            // not crash the caller: memory keeps the new value and the next
            // successful save heals the file.
            _log?.Invoke($"settings save failed: {ex.Message}");
        }
    }
}
