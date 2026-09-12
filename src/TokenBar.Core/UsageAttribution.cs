using System.Buffers;
using System.Text;
using System.Text.Json;
using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>
/// User-declared billing attribution for provider-split model report rows.
/// Port of TokenBarCore/UsageAttribution.swift.
///
/// The confirmed and suggestion stores intentionally share a codec but never a
/// read path: consulting suggestions here would silently turn a proposal into
/// billing truth.
/// </summary>
public static class UsageAttribution
{
    public const string ConfirmedKey = "tokenbar.usage.attribution.confirmed";
    public const string SuggestionsKey = "tokenbar.usage.attribution.suggestions";
    public const int MaxEntries = 128;
    public const int MaxRawBytes = 64 * 1024;

    public enum StateKind
    {
        Unassigned,
        Assigned,
        Excluded,
    }

    /// <summary>Swift's three-case <c>State</c> enum. A record struct rather than
    /// a class hierarchy: <c>default(State)</c> is <see cref="Unassigned"/>,
    /// which is the same "nothing declared" the Swift resolve falls back to, and
    /// value equality comes for free.</summary>
    public readonly record struct State(StateKind Kind, string? Target)
    {
        public static State Assigned(string target) => new(StateKind.Assigned, target);

        public static State Excluded => new(StateKind.Excluded, null);

        public static State Unassigned => new(StateKind.Unassigned, null);
    }

    /// <summary>One declaration.
    ///
    /// PORTING DECISION — <c>Model</c> is <c>string?</c> and null is not a
    /// sentinel for "any". Swift's <c>Record.model</c> is <c>String?</c> where
    /// nil means "this record covers the whole provider, not one model", and
    /// <see cref="Resolve"/> checks the model-specific override first. Windows'
    /// <see cref="ModelReportEntry.Model"/> is non-nullable, so an entry always
    /// asks a model-specific question; only a stored record may carry null.
    /// Deliberately NOT an empty-string sentinel: the wire legitimately emits an
    /// empty model id, and collapsing the two would make a provider-level record
    /// indistinguishable from a record about the unnamed model.</summary>
    public sealed record Record(string Client, string Provider, string? Model, State State)
    {
        public Record(string client, string provider, State state)
            : this(client, provider, null, state)
        {
        }
    }

    /// <summary>The read seam's three outcomes, which
    /// <see cref="SettingsStore.GetString"/> alone cannot express: it returns its
    /// fallback both for an absent key and for a present non-string value.
    /// Mirrors what Swift gets from <c>UserDefaults.object(forKey:)</c> typed as
    /// <c>Any?</c>.</summary>
    public readonly record struct StoredValue(bool IsPresent, string? Text)
    {
        public static StoredValue Absent => new(false, null);

        /// <summary>A present value: <paramref name="text"/> null means present
        /// but not a string, i.e. foreign to this codec.</summary>
        public static StoredValue Present(string? text) => new(true, text);

        public static StoredValue From(SettingsStore store, string key) =>
            store.TryGetString(key, out var text) ? Present(text) : Absent;
    }

    public sealed record Table(IReadOnlyList<Record> Records, bool IsWritable)
    {
        /// <summary>An absent value: no declarations, and writing is allowed.</summary>
        public static Table Empty { get; } = new([], true);

        /// <summary>A present value this codec does not understand. False
        /// <see cref="IsWritable"/> prevents a rejected read from being written
        /// back as a new empty declaration set.</summary>
        public static Table Rejected { get; } = new([], false);

        public State StateFor(ModelReportEntry entry) =>
            Resolve(entry.Client, entry.Provider, entry.Model, Records);
    }

    public static Table Confirmed(SettingsStore store) =>
        ParseState(StoredValue.From(store, ConfirmedKey));

    public static Table Suggestions(SettingsStore store) =>
        ParseState(StoredValue.From(store, SuggestionsKey));

    /// <summary>Effective state reads only confirmed declarations. Suggestions
    /// remain available to an acceptance UI but cannot affect report totals.</summary>
    public static State EffectiveState(ModelReportEntry entry, SettingsStore store) =>
        Confirmed(store).StateFor(entry);

    public static State Resolve(
        string client, string provider, string? model, IReadOnlyList<Record> records)
    {
        if (model is not null)
        {
            foreach (var record in records)
            {
                if (record.Client == client && record.Provider == provider && record.Model == model)
                {
                    return record.State;
                }
            }
        }

        foreach (var record in records)
        {
            if (record.Client == client && record.Provider == provider && record.Model is null)
            {
                return record.State;
            }
        }

        return State.Unassigned;
    }

    public static State Resolve(ModelReportEntry entry, IReadOnlyList<Record> records) =>
        Resolve(entry.Client, entry.Provider, entry.Model, records);

    /// <summary>Parse a stored value. Absent is an empty writable table;
    /// malformed or semantically invalid data is an empty read-only table so a
    /// later mutation cannot overwrite data this codec does not understand.</summary>
    public static Table ParseState(StoredValue stored)
    {
        if (!stored.IsPresent)
        {
            return Table.Empty;
        }

        // Absent means absent; every present non-string belongs to a foreign
        // writer and must not be replaced by this codec.
        if (stored.Text is not { } raw)
        {
            return Table.Rejected;
        }

        if (Encoding.UTF8.GetByteCount(raw) > MaxRawBytes)
        {
            return Table.Rejected;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return Table.Rejected;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() > MaxEntries)
            {
                return Table.Rejected;
            }

            var records = new List<Record>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object || ParseRecord(element) is not { } record)
                {
                    return Table.Rejected;
                }

                records.Add(record);
            }

            if (records.Select(SourceKey).Distinct().Count() != records.Count)
            {
                return Table.Rejected;
            }

            records.Sort(ComparePrecedence);
            return new Table(records, true);
        }
    }

    /// <summary>Replace or remove one source-key declaration while preserving the
    /// observed value's fail-closed boundary. Unassigned removes the key; it is
    /// never serialized as a record. Returns null for "do not write" — Windows'
    /// <see cref="SettingsStore.SetString"/> takes a non-nullable value, so the
    /// refusal has nowhere to live inside the call and must be decided before it
    /// (see <c>UsageAttributionSettings.WriteRecords</c>, which is the one place
    /// that turns this null into a skipped write plus a
    /// <c>WriteFailure</c>).</summary>
    public static string? ConfirmedRaw(StoredValue current, Record record) =>
        UpdatedRaw(current, [record]);

    public static string? ConfirmedRaw(StoredValue current, IReadOnlyList<Record> records) =>
        UpdatedRaw(current, records);

    public static string? SuggestionsRaw(StoredValue current, Record record) =>
        UpdatedRaw(current, [record]);

    public static string? SuggestionsRaw(StoredValue current, IReadOnlyList<Record> records) =>
        UpdatedRaw(current, records);

    /// <summary>Replace the generated suggestion set in one validated write. The
    /// current value still has to belong to this codec, so a foreign or malformed
    /// value remains untouched rather than being repaired as an empty table.</summary>
    public static string? SuggestionsRawReplacing(StoredValue current, IReadOnlyList<Record> records) =>
        ParseState(current).IsWritable ? CanonicalRaw(records) : null;

    private static string? UpdatedRaw(StoredValue current, IReadOnlyList<Record> updates)
    {
        var parsed = ParseState(current);
        if (!parsed.IsWritable || !updates.All(IsValidSource))
        {
            return null;
        }

        var records = parsed.Records.ToList();
        foreach (var update in updates)
        {
            records.RemoveAll(existing => SourceKey(existing) == SourceKey(update));
            if (update.State.Kind == StateKind.Unassigned)
            {
                continue;
            }

            records.Add(update);
        }

        return CanonicalRaw(records);
    }

    /// <summary>Keep canonical encoding private: public callers must update the
    /// observed value so malformed or foreign data cannot be replaced by
    /// serializing the empty table returned by a rejected read.</summary>
    private static string? CanonicalRaw(IReadOnlyList<Record> records)
    {
        if (records.Count > MaxEntries
            || !records.All(IsPersistable)
            || records.Select(SourceKey).Distinct().Count() != records.Count)
        {
            return null;
        }

        var sorted = records.ToList();
        sorted.Sort(ComparePrecedence);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var record in sorted)
            {
                // Keys in sorted order, matching JSONSerialization's
                // `.sortedKeys` on the macOS side.
                writer.WriteStartObject();
                writer.WriteString("client", record.Client);
                if (record.Model is { } model)
                {
                    writer.WriteString("model", model);
                }
                else
                {
                    writer.WriteNull("model");
                }

                writer.WriteString("provider", record.Provider);
                writer.WriteString("state", StateName(record.State));
                if (record.State.Kind == StateKind.Assigned)
                {
                    writer.WriteString("target", record.State.Target!);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return buffer.WrittenCount > MaxRawBytes
            ? null
            : Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static Record? ParseRecord(JsonElement element)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            keys.Add(property.Name);
        }

        if (StringProperty(element, "client") is not { } client
            || StringProperty(element, "provider") is not { } provider
            || StringProperty(element, "state") is not { } stateName
            || !element.TryGetProperty("model", out var modelValue))
        {
            return null;
        }

        string? model;
        if (modelValue.ValueKind == JsonValueKind.Null)
        {
            model = null;
        }
        else if (modelValue.ValueKind == JsonValueKind.String)
        {
            model = modelValue.GetString();
        }
        else
        {
            return null;
        }

        switch (stateName)
        {
            case "assigned":
                if (!keys.SetEquals(["client", "model", "provider", "state", "target"])
                    || StringProperty(element, "target") is not { } target)
                {
                    return null;
                }

                var assigned = new Record(client, provider, model, State.Assigned(target));
                return IsPersistable(assigned) ? assigned : null;
            case "excluded":
                if (!keys.SetEquals(["client", "model", "provider", "state"]))
                {
                    return null;
                }

                var excluded = new Record(client, provider, model, State.Excluded);
                return IsPersistable(excluded) ? excluded : null;
            default:
                return null;
        }
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Only the assignment <em>target</em> has to be a registered
    /// client: it names a quota bucket the app must be able to render. The source
    /// is whatever the report observed, and the report legitimately emits ids
    /// that are not in the registry — <c>cc-mirror/*</c> is produced during
    /// Claude-lane parsing rather than being a scanner lane. Constraining the
    /// source too rendered those rows on the page and then refused every
    /// classification made on them, which is a dead end rather than a
    /// safeguard.</summary>
    private static bool IsPersistable(Record record)
    {
        if (record.Client.Length == 0)
        {
            return false;
        }

        return record.State.Kind switch
        {
            StateKind.Assigned => ClientRegistry.AllIds.Contains(record.State.Target),
            StateKind.Excluded => true,
            _ => false,
        };
    }

    private static bool IsValidSource(Record record) =>
        record.Client.Length > 0
        && (record.State.Kind == StateKind.Unassigned || IsPersistable(record));

    private static (string Client, string Provider, string? Model) SourceKey(Record record) =>
        (record.Client, record.Provider, record.Model);

    private static string StateName(State state) => state.Kind switch
    {
        StateKind.Assigned => "assigned",
        StateKind.Excluded => "excluded",
        _ => "unassigned",
    };

    private static int ComparePrecedence(Record lhs, Record rhs)
    {
        var client = string.CompareOrdinal(lhs.Client, rhs.Client);
        if (client != 0)
        {
            return client;
        }

        var provider = string.CompareOrdinal(lhs.Provider, rhs.Provider);
        if (provider != 0)
        {
            return provider;
        }

        // Provider-level records (null model) sort ahead of model-specific ones.
        return (lhs.Model, rhs.Model) switch
        {
            (null, null) => 0,
            (null, _) => -1,
            (_, null) => 1,
            var (left, right) => string.CompareOrdinal(left, right),
        };
    }
}
