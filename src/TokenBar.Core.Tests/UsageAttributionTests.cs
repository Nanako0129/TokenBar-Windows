using System.Text;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

/// <summary>The codec and its fail-closed read boundary, ported from
/// TokenBarCore/UsageAttribution.swift.</summary>
public class UsageAttributionTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tokenbar-tests", Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }

        GC.SuppressFinalize(this);
    }

    private void SeedRawFile(string keyValueJson)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(
            StorePath,
            "{\"" + UsageAttribution.ConfirmedKey + "\": " + keyValueJson + "}");
    }

    // MARK: - resolve

    [Fact]
    public void ModelSpecificRecordWinsOverProviderLevelRecord()
    {
        // UsageAttribution.swift:76-81 — the model override is consulted first.
        UsageAttribution.Record[] records =
        [
            new("claude", "anthropic", null, UsageAttribution.State.Excluded),
            new("claude", "anthropic", "claude-fable-5", UsageAttribution.State.Assigned("claude")),
        ];

        Assert.Equal(
            UsageAttribution.State.Assigned("claude"),
            UsageAttribution.Resolve("claude", "anthropic", "claude-fable-5", records));
        Assert.Equal(
            UsageAttribution.State.Excluded,
            UsageAttribution.Resolve("claude", "anthropic", "some-other-model", records));
        Assert.Equal(
            UsageAttribution.State.Unassigned,
            UsageAttribution.Resolve("codex", "openai", "gpt-5", records));
    }

    [Fact]
    public void ProviderLevelAndEmptyStringModelStayDistinctThroughTheCodec()
    {
        // The porting decision: null Model means "the whole provider", and the
        // empty string is a model id the wire really emits. A sentinel would
        // collapse them.
        UsageAttribution.Record[] records =
        [
            new("claude", "anthropic", null, UsageAttribution.State.Excluded),
            new("claude", "anthropic", "", UsageAttribution.State.Assigned("claude")),
        ];

        var raw = UsageAttribution.ConfirmedRaw(UsageAttribution.StoredValue.Absent, records);
        Assert.NotNull(raw);
        var table = UsageAttribution.ParseState(UsageAttribution.StoredValue.Present(raw));

        Assert.True(table.IsWritable);
        Assert.Equal(2, table.Records.Count);
        // Provider-level sorts first (UsageAttribution.swift:281-286).
        Assert.Null(table.Records[0].Model);
        Assert.Equal("", table.Records[1].Model);
        Assert.Equal(
            UsageAttribution.State.Assigned("claude"),
            UsageAttribution.Resolve("claude", "anthropic", "", table.Records));
        Assert.Equal(
            UsageAttribution.State.Excluded,
            UsageAttribution.Resolve("claude", "anthropic", "gpt-5", table.Records));
    }

    [Fact]
    public void UnassignedRemovesTheDeclarationRatherThanBeingSerialized()
    {
        var seeded = UsageAttribution.ConfirmedRaw(
            UsageAttribution.StoredValue.Absent,
            new UsageAttribution.Record("claude", "anthropic", UsageAttribution.State.Excluded));
        Assert.NotNull(seeded);

        var cleared = UsageAttribution.ConfirmedRaw(
            UsageAttribution.StoredValue.Present(seeded),
            new UsageAttribution.Record("claude", "anthropic", UsageAttribution.State.Unassigned));

        Assert.Equal("[]", cleared);
    }

    [Fact]
    public void AssignmentTargetMustBeARegisteredClientButTheSourceNeedNotBe()
    {
        // UsageAttribution.swift:250-260 — cc-mirror/* is a real observed source.
        Assert.NotNull(UsageAttribution.ConfirmedRaw(
            UsageAttribution.StoredValue.Absent,
            new UsageAttribution.Record("cc-mirror/x", "anthropic", UsageAttribution.State.Assigned("claude"))));
        Assert.Null(UsageAttribution.ConfirmedRaw(
            UsageAttribution.StoredValue.Absent,
            new UsageAttribution.Record("claude", "anthropic", UsageAttribution.State.Assigned("not-a-client"))));
    }

    // MARK: - the three read outcomes

    [Fact]
    public void StoreReportsPresenceAndValueKindSeparately()
    {
        var store = new SettingsStore(StorePath);
        Assert.False(store.TryGetString("tokenbar.absent", out var absent));
        Assert.Null(absent);

        store.SetString("tokenbar.present", "hello");
        Assert.True(store.TryGetString("tokenbar.present", out var present));
        Assert.Equal("hello", present);

        store.SetInt("tokenbar.number", 3);
        Assert.True(store.TryGetString("tokenbar.number", out var foreign));
        Assert.Null(foreign);
    }

    [Fact]
    public void AbsentValueIsAnEmptyWritableTableAndTheWriteLandsOnDisk()
    {
        var store = new SettingsStore(StorePath);
        var table = UsageAttribution.Confirmed(store);
        Assert.Empty(table.Records);
        Assert.True(table.IsWritable);

        var failure = UsageAttributionSettings.WriteRecords(
            store,
            UsageAttribution.ConfirmedKey,
            [new UsageAttribution.Record("claude", "anthropic", UsageAttribution.State.Excluded)]);

        Assert.Null(failure);
        var reloaded = UsageAttribution.Confirmed(new SettingsStore(StorePath));
        Assert.True(reloaded.IsWritable);
        Assert.Equal(
            new UsageAttribution.Record("claude", "anthropic", null, UsageAttribution.State.Excluded),
            Assert.Single(reloaded.Records));
    }

    [Fact]
    public void ParseableValueReadsBackWhatWasWritten()
    {
        var store = new SettingsStore(StorePath);
        UsageAttribution.Record[] written =
        [
            new("claude", "anthropic", null, UsageAttribution.State.Assigned("claude")),
            new("opencode", "openai", "gpt-5", UsageAttribution.State.Excluded),
        ];

        Assert.Null(UsageAttributionSettings.WriteRecords(store, UsageAttribution.ConfirmedKey, written));

        var reloaded = UsageAttribution.Confirmed(new SettingsStore(StorePath));
        Assert.True(reloaded.IsWritable);
        Assert.Equal(written.OrderBy(r => r.Client, StringComparer.Ordinal), reloaded.Records);
    }

    [Theory]
    // The non-string case is the one GetString cannot see: it and an absent key
    // both return the fallback, and collapsing them replaces a foreign writer's
    // data with an empty declaration set.
    [InlineData("{\"a\": 1}")]
    [InlineData("\"not json at all\"")]
    [InlineData("\"[{\\\"client\\\":\\\"claude\\\"}]\"")]
    public void ForeignOrMalformedValueIsNotWritableAndSurvivesAClassificationWriteByteForByte(
        string storedJson)
    {
        SeedRawFile(storedJson);
        var before = File.ReadAllBytes(StorePath);

        var store = new SettingsStore(StorePath);
        var table = UsageAttribution.Confirmed(store);
        Assert.Empty(table.Records);
        Assert.False(table.IsWritable);

        var failure = UsageAttributionSettings.WriteRecords(
            store,
            UsageAttribution.ConfirmedKey,
            [new UsageAttribution.Record("claude", "anthropic", UsageAttribution.State.Excluded)]);

        Assert.Equal(UsageAttributionSettings.WriteFailure.InvalidExistingValue, failure);
        Assert.Equal(before, File.ReadAllBytes(StorePath));
    }

    [Fact]
    public void SuggestionReplacementAlsoRefusesAForeignValue()
    {
        var foreignSuggestions = UsageAttribution.StoredValue.Present(null);
        Assert.Null(UsageAttribution.SuggestionsRawReplacing(foreignSuggestions, []));
        Assert.Equal("[]", UsageAttribution.SuggestionsRawReplacing(UsageAttribution.StoredValue.Absent, []));
    }

    [Fact]
    public void OversizeAndDuplicateInputsAreRefusedRatherThanTruncated()
    {
        var duplicate = new[]
        {
            new UsageAttribution.Record("claude", "anthropic", null, UsageAttribution.State.Excluded),
            new UsageAttribution.Record("claude", "anthropic", null, UsageAttribution.State.Excluded),
        };
        // Two updates with the same source key: the second replaces the first, so
        // this is legal (it is the canonical encoder's duplicate guard that must
        // not fire). What must be refused is a stored array carrying duplicates.
        Assert.NotNull(UsageAttribution.ConfirmedRaw(UsageAttribution.StoredValue.Absent, duplicate));

        var duplicated = "[" + string.Join(',', Enumerable.Repeat(
            "{\"client\":\"claude\",\"model\":null,\"provider\":\"anthropic\",\"state\":\"excluded\"}", 2)) + "]";
        Assert.False(UsageAttribution.ParseState(UsageAttribution.StoredValue.Present(duplicated)).IsWritable);

        var oversize = new string('x', UsageAttribution.MaxRawBytes + 1);
        Assert.False(UsageAttribution.ParseState(UsageAttribution.StoredValue.Present(oversize)).IsWritable);
        Assert.True(Encoding.UTF8.GetByteCount(oversize) > UsageAttribution.MaxRawBytes);

        var tooMany = Enumerable.Range(0, UsageAttribution.MaxEntries + 1)
            .Select(i => new UsageAttribution.Record($"c{i}", "anthropic", UsageAttribution.State.Excluded))
            .ToList();
        Assert.Null(UsageAttribution.ConfirmedRaw(UsageAttribution.StoredValue.Absent, tooMany));
        Assert.Equal(
            UsageAttributionSettings.WriteFailure.EntryLimit,
            UsageAttributionSettings.DiagnoseWriteFailure(UsageAttribution.Table.Empty, tooMany, null));
    }

    [Fact]
    public void ExtraOrMissingKeysRejectTheWholeStoredValue()
    {
        // Swift compares the key set exactly (UsageAttribution.swift:225, :232).
        const string extraKey =
            "[{\"client\":\"claude\",\"model\":null,\"provider\":\"anthropic\",\"state\":\"excluded\",\"x\":1}]";
        const string missingModel =
            "[{\"client\":\"claude\",\"provider\":\"anthropic\",\"state\":\"excluded\"}]";
        const string numericModel =
            "[{\"client\":\"claude\",\"model\":7,\"provider\":\"anthropic\",\"state\":\"excluded\"}]";

        foreach (var raw in new[] { extraKey, missingModel, numericModel })
        {
            Assert.False(UsageAttribution.ParseState(UsageAttribution.StoredValue.Present(raw)).IsWritable);
        }
    }
}
