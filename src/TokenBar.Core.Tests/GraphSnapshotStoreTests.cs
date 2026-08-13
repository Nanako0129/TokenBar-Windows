using System.Text;
using System.Text.Json;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

public sealed class GraphSnapshotStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tokenbar-tests", "graph-snapshot-" + Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "graph-v1.json");

    public GraphSnapshotStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void RoundTripsFrozenGraphSchemaAndNumericEdges()
    {
        var store = new GraphSnapshotStore(StorePath);
        var capturedAt = new DateTimeOffset(2026, 8, 13, 3, 4, 5, TimeSpan.FromHours(8));
        var payload = Payload(
            "2025-12-31",
            "2026-01-01",
            turns: new Dictionary<string, long> { ["claude"] = long.MinValue },
            input: long.MaxValue,
            messages: -7,
            totalCost: -0.25);

        Assert.Equal(
            GraphSnapshotWriteStatus.Written,
            store.Write("opaque-context", null, capturedAt, payload));

        var result = new GraphSnapshotStore(StorePath).Read("opaque-context", "  ");
        Assert.Equal(GraphSnapshotReadStatus.Hit, result.Status);
        Assert.Equal(capturedAt.ToUniversalTime(), result.CapturedAt);
        Assert.Equivalent(payload, result.Payload, strict: true);

        using var doc = JsonDocument.Parse(File.ReadAllBytes(StorePath));
        Assert.Equal(
            ["schemaVersion", "sourceContextId", "query", "capturedAt", "payload"],
            doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["meta", "summary", "years", "contributions"],
            doc.RootElement.GetProperty("payload").EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("quota", File.ReadAllText(StorePath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trace", File.ReadAllText(StorePath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("report", File.ReadAllText(StorePath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RoundTripsSpecificYearEmptyAndNullTurns()
    {
        var store = new GraphSnapshotStore(StorePath);
        var empty = EmptyPayload();
        Assert.Equal(
            GraphSnapshotWriteStatus.Written,
            store.Write("ctx", " 0042 ", DateTimeOffset.UnixEpoch, empty));
        Assert.Equal(GraphSnapshotReadStatus.Hit, store.Read("ctx", "0042").Status);
        Assert.Equal(GraphSnapshotReadStatus.QueryMismatch, store.Read("ctx", null).Status);

        var oneDay = Payload("2026-01-01", turns: null);
        Assert.Equal(
            GraphSnapshotWriteStatus.Written,
            store.Write("ctx", "2026", DateTimeOffset.UnixEpoch, oneDay));
        var result = store.Read("ctx", "2026");
        Assert.Equal(GraphSnapshotReadStatus.Hit, result.Status);
        Assert.Null(result.Payload!.Contributions[0].TurnsByClient);
    }

    [Fact]
    public void RejectsWrongContextQueryAndSchemaWithoutLeakingInputs()
    {
        var store = new GraphSnapshotStore(StorePath);
        Assert.Equal(
            GraphSnapshotWriteStatus.Written,
            store.Write("context-secret", "2026", DateTimeOffset.UnixEpoch, Payload("2026-01-01")));

        var context = store.Read("other-secret", "2026");
        var query = store.Read("context-secret", "2025");
        Assert.Equal(GraphSnapshotReadStatus.ContextMismatch, context.Status);
        Assert.Equal(GraphSnapshotReadStatus.QueryMismatch, query.Status);
        Assert.DoesNotContain("secret", context.ToString(), StringComparison.OrdinalIgnoreCase);

        ReplaceOnce("\"schemaVersion\":1", "\"schemaVersion\":2");
        Assert.Equal(GraphSnapshotReadStatus.IncompatibleSchema, store.Read("context-secret", "2026").Status);
    }

    [Theory]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")]
    [InlineData("\"schemaVersion\":1", "\"SchemaVersion\":1")]
    [InlineData("\"query\":{\"year\":\"2026\"}", "\"query\":{\"year\":\"2026\",\"Year\":\"2026\"}")]
    [InlineData("\"meta\":{", "\"meta\":{\"unknown\":0,")]
    [InlineData("\"summary\":{", "\"summary\":{\"unknown\":0,")]
    [InlineData("\"date\":\"2026-01-01\"", "\"date\":\"2026-01-01\",\"Date\":\"2026-01-01\"")]
    [InlineData("\"client\":\"claude\"", "\"client\":\"claude\",\"unknown\":0")]
    [InlineData("\"input\":9223372036854775807", "\"input\":9223372036854775807,\"Input\":0")]
    public void StrictReaderRejectsUnknownDuplicateAndCaseAliases(string oldValue, string newValue)
    {
        WriteGood();
        ReplaceOnce(oldValue, newValue);
        Assert.Equal(GraphSnapshotReadStatus.InvalidData, new GraphSnapshotStore(StorePath).Read("ctx", "2026").Status);
    }

    [Theory]
    [InlineData("\"generatedAt\":\"g\",", "")]
    [InlineData("\"messages\":-7", "\"messages\":\"-7\"")]
    [InlineData("\"turnsByClient\":{\"claude\":1}", "\"turnsByClient\":[]")]
    public void RejectsMissingAndWrongTypeMembers(string oldValue, string newValue)
    {
        WriteGood();
        ReplaceOnce(oldValue, newValue);
        Assert.Equal(GraphSnapshotReadStatus.InvalidData, ReadGood().Status);
    }

    [Fact]
    public void RejectsUnknownEnumsNonFiniteAndNonCanonicalCapturedAt()
    {
        WriteGood();
        ReplaceOnce("\"bestEffort\"", "\"futureMode\"");
        Assert.Equal(GraphSnapshotReadStatus.InvalidData, ReadGood().Status);

        WriteGood();
        ReplaceOnce("\"totalCost\":-0.25", "\"totalCost\":1e9999");
        Assert.Equal(GraphSnapshotReadStatus.InvalidData, ReadGood().Status);

        WriteGood();
        var canonical = DateTimeOffset.UnixEpoch.ToString("O").Replace("+", "\\u002B", StringComparison.Ordinal);
        ReplaceOnce(canonical, "1970-01-01T00:00:00+00:00");
        Assert.Equal(GraphSnapshotReadStatus.InvalidData, ReadGood().Status);
    }

    [Theory]
    [InlineData("date-order")]
    [InlineData("meta-range")]
    [InlineData("year-range")]
    [InlineData("query-year")]
    [InlineData("empty-shape")]
    public void RejectsQueryPayloadSemanticContradictions(string scenario)
    {
        var store = new GraphSnapshotStore(StorePath);
        var payload = scenario switch
        {
            "date-order" => Payload("2026-01-02", "2026-01-01"),
            "meta-range" => Payload("2026-01-01") with
            {
                Meta = new UsageMeta("g", "v", new DateRange("2026-01-02", "2026-01-02"),
                    PricingMode.BestEffort, CostCoverage.Complete),
            },
            "year-range" => Payload("2026-01-01") with
            {
                Years = [new YearMeta("2026", 1, 1, new DateRange("2026-01-02", "2026-01-02"))],
            },
            "query-year" => Payload("2025-01-01"),
            _ => EmptyPayload() with
            {
                Meta = new UsageMeta("g", "v", new DateRange("2026-01-01", "2026-01-01"),
                    PricingMode.BestEffort, CostCoverage.Complete),
            },
        };

        Assert.Equal(
            GraphSnapshotWriteStatus.InvalidData,
            store.Write("ctx", "2026", DateTimeOffset.UnixEpoch, payload));
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void OversizeMalformedAndTruncatedFilesAreReadOnlyMisses()
    {
        var store = new GraphSnapshotStore(StorePath);
        File.WriteAllBytes(StorePath, new byte[GraphSnapshotStore.MaxFileBytes + 1]);
        Assert.Equal(GraphSnapshotReadStatus.TooLarge, store.Read("ctx", null).Status);

        File.WriteAllText(StorePath, "{not-json");
        var before = File.ReadAllBytes(StorePath);
        Assert.Equal(GraphSnapshotReadStatus.InvalidData, store.Read("ctx", null).Status);
        Assert.Equal(before, File.ReadAllBytes(StorePath));
        Assert.False(File.Exists(StorePath + ".corrupt"));

        File.WriteAllText(StorePath, "{\"schemaVersion\":1");
        Assert.Equal(GraphSnapshotReadStatus.InvalidData, store.Read("ctx", null).Status);
    }

    [Fact]
    public void DepthAndStringLimitsStopBeforeAcceptance()
    {
        File.WriteAllText(StorePath, new string('[', GraphSnapshotStore.MaxDepth + 2)
            + "0" + new string(']', GraphSnapshotStore.MaxDepth + 2));
        Assert.Equal(GraphSnapshotReadStatus.InvalidData, new GraphSnapshotStore(StorePath).Read("ctx", null).Status);

        WriteGood();
        ReplaceOnce("\"generatedAt\":\"g\"", "\"generatedAt\":\"" + new string('x', GraphSnapshotStore.MaxStringBytes + 1) + "\"");
        Assert.Equal(GraphSnapshotReadStatus.TooLarge, ReadGood().Status);

        var tooLong = Payload("2026-01-01") with
        {
            Meta = new UsageMeta(new string('x', GraphSnapshotStore.MaxStringBytes + 1), "v",
                new DateRange("2026-01-01", "2026-01-01"), PricingMode.BestEffort, CostCoverage.Complete),
        };
        Assert.Equal(
            GraphSnapshotWriteStatus.TooLarge,
            new GraphSnapshotStore(StorePath).Write("ctx", "2026", DateTimeOffset.UnixEpoch, tooLong));
    }

    [Fact]
    public void PerContributionAndAggregateBoundsRejectBeforePublication()
    {
        var clients = Enumerable.Range(0, GraphSnapshotStore.MaxItemsPerContribution + 1)
            .Select(i => Client("c" + i))
            .ToArray();
        var payload = Payload("2026-01-01") with
        {
            Contributions = [Day("2026-01-01", clients, null)],
        };
        Assert.Equal(
            GraphSnapshotWriteStatus.TooLarge,
            new GraphSnapshotStore(StorePath).Write("ctx", "2026", DateTimeOffset.UnixEpoch, payload));
        Assert.False(File.Exists(StorePath));

        WriteGood();
        var goodBytes = File.ReadAllBytes(StorePath);
        var tooManySummaryItems = Payload("2026-01-01") with
        {
            Summary = new UsageSummary(1, 1, 1, 1, 1, 1,
                Enumerable.Repeat("c", GraphSnapshotStore.MaxSummaryItems + 1).ToArray(), ["m"]),
        };
        Assert.Equal(
            GraphSnapshotWriteStatus.TooLarge,
            new GraphSnapshotStore(StorePath).Write("ctx", "2026", DateTimeOffset.UnixEpoch, tooManySummaryItems));
        Assert.Equal(goodBytes, File.ReadAllBytes(StorePath));
    }

    [Fact]
    public void RuntimeNestedNullsReturnInvalidDataAndPreserveLastGood()
    {
        var store = new GraphSnapshotStore(StorePath);
        var valid = Payload("2026-01-01");
        Assert.Equal(GraphSnapshotWriteStatus.Written,
            store.Write("ctx", "2026", DateTimeOffset.UnixEpoch, valid));
        var lastGood = File.ReadAllBytes(StorePath);

        UsagePayload[] invalid =
        [
            valid with { Meta = null! },
            valid with { Summary = null! },
            valid with { Years = null! },
            valid with { Contributions = null! },
            valid with { Meta = valid.Meta with { DateRange = null! } },
            valid with { Summary = valid.Summary with { Clients = null! } },
            valid with { Summary = valid.Summary with { Models = null! } },
            valid with { Years = [valid.Years[0] with { Range = null! }] },
            valid with { Contributions = [valid.Contributions[0] with { Totals = null! }] },
            valid with { Contributions = [valid.Contributions[0] with { TokenBreakdown = null! }] },
            valid with { Contributions = [valid.Contributions[0] with { Clients = null! }] },
            valid with
            {
                Contributions =
                [
                    valid.Contributions[0] with
                    {
                        Clients = [valid.Contributions[0].Clients[0] with { Tokens = null! }],
                    },
                ],
            },
        ];

        foreach (var payload in invalid)
        {
            Assert.Equal(GraphSnapshotWriteStatus.InvalidData,
                store.Write("ctx", "2026", DateTimeOffset.UnixEpoch, payload));
            Assert.Equal(lastGood, File.ReadAllBytes(StorePath));
        }
    }

    [Fact]
    public void FailedWritesPreserveLastGoodAndSuccessfulReplacePublishesNew()
    {
        var store = new GraphSnapshotStore(StorePath);
        Assert.Equal(GraphSnapshotWriteStatus.Written,
            store.Write("ctx", "2026", DateTimeOffset.UnixEpoch, Payload("2026-01-01")));
        var oldBytes = File.ReadAllBytes(StorePath);

        Assert.Equal(GraphSnapshotWriteStatus.InvalidData,
            store.Write("ctx", "2026", DateTimeOffset.UnixEpoch,
                Payload("2026-01-01") with
                {
                    Summary = new UsageSummary(1, double.NaN, 1, 1, 1, 1, ["claude"], ["m"]),
                }));
        Assert.Equal(oldBytes, File.ReadAllBytes(StorePath));

        var newer = Payload("2026-02-01");
        Assert.Equal(GraphSnapshotWriteStatus.Written,
            store.Write("ctx", "2026", DateTimeOffset.UnixEpoch.AddDays(1), newer));
        Assert.Equivalent(newer, new GraphSnapshotStore(StorePath).Read("ctx", "2026").Payload, strict: true);
        Assert.Empty(Directory.EnumerateFiles(_dir, ".*.tmp"));
    }

    [Fact]
    public void ConcurrentReadersOnlyObserveCompleteOldOrNewPayloads()
    {
        var store = new GraphSnapshotStore(StorePath);
        var oldPayload = Payload("2026-01-01");
        var newPayload = Payload("2026-02-01");
        Assert.Equal(GraphSnapshotWriteStatus.Written,
            store.Write("ctx", "2026", DateTimeOffset.UnixEpoch, oldPayload));

        var unexpected = new List<GraphSnapshotReadResult>();
        Parallel.For(0, 200, i =>
        {
            if (i % 10 == 0)
            {
                store.Write("ctx", "2026", DateTimeOffset.UnixEpoch.AddSeconds(i),
                    i % 20 == 0 ? newPayload : oldPayload);
                return;
            }

            var result = new GraphSnapshotStore(StorePath).Read("ctx", "2026");
            if (result.Status == GraphSnapshotReadStatus.Hit
                && !HasDate(result.Payload, "2026-01-01")
                && !HasDate(result.Payload, "2026-02-01")
                || result.Status is not (GraphSnapshotReadStatus.Hit or GraphSnapshotReadStatus.IoFailure))
            {
                lock (unexpected) unexpected.Add(result);
            }
        });

        Assert.Empty(unexpected);
    }

    [Fact]
    public void FinalSymbolicLinkIsNeverFollowed()
    {
        var external = Path.Combine(_dir, "external.json");
        File.WriteAllText(external, "external-secret");
        try
        {
            File.CreateSymbolicLink(StorePath, external);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Fail($"Windows reparse acceptance could not create the test symlink: {ex.GetType().Name}");
            }
            return;
        }

        var store = new GraphSnapshotStore(StorePath);
        Assert.Equal(GraphSnapshotReadStatus.UnsafePath, store.Read("ctx", null).Status);
        Assert.Equal(GraphSnapshotWriteStatus.UnsafePath,
            store.Write("ctx", null, DateTimeOffset.UnixEpoch, EmptyPayload()));
        Assert.Equal("external-secret", File.ReadAllText(external));
    }

    [Fact]
    public void ReadDeniedIsARedactedMissAndDoesNotMutateTarget()
    {
        WriteGood();
        var before = File.ReadAllBytes(StorePath);
        try
        {
            using var held = new FileStream(StorePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var result = new GraphSnapshotStore(StorePath).Read("ctx", "2026");
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(GraphSnapshotReadStatus.IoFailure, result.Status);
            }
        }
        finally
        {
            Assert.Equal(before, File.ReadAllBytes(StorePath));
        }
    }

    [Fact]
    public void MissingAndAbsentParentDoNotCreateFiles()
    {
        Assert.Equal(GraphSnapshotReadStatus.Missing, new GraphSnapshotStore(StorePath).Read("ctx", null).Status);
        var absent = Path.Combine(_dir, "absent", "graph.json");
        Assert.Equal(GraphSnapshotWriteStatus.InvalidInput,
            new GraphSnapshotStore(absent).Write("ctx", null, DateTimeOffset.UnixEpoch, EmptyPayload()));
        Assert.False(Directory.Exists(Path.GetDirectoryName(absent)));
    }

    private static bool HasDate(UsagePayload? payload, string date) =>
        payload is { Contributions.Count: 1 }
        && string.Equals(payload.Contributions[0].Date, date, StringComparison.Ordinal);

    private void WriteGood() => Assert.Equal(
        GraphSnapshotWriteStatus.Written,
        new GraphSnapshotStore(StorePath).Write("ctx", "2026", DateTimeOffset.UnixEpoch, Payload("2026-01-01")));

    private GraphSnapshotReadResult ReadGood() => new GraphSnapshotStore(StorePath).Read("ctx", "2026");

    private void ReplaceOnce(string oldValue, string newValue)
    {
        var json = File.ReadAllText(StorePath);
        Assert.Contains(oldValue, json, StringComparison.Ordinal);
        File.WriteAllText(StorePath, json.Replace(oldValue, newValue, StringComparison.Ordinal));
    }

    private static UsagePayload EmptyPayload() => new(
        new UsageMeta("g", "v", new DateRange("", ""), PricingMode.LocalOnly, CostCoverage.None),
        new UsageSummary(0, 0, 0, 0, 0, 0, [], []),
        [],
        []);

    private static UsagePayload Payload(
        params string[] dates) => Payload(dates, new Dictionary<string, long> { ["claude"] = 1 });

    private static UsagePayload Payload(
        string date,
        IReadOnlyDictionary<string, long>? turns) => Payload([date], turns);

    private static UsagePayload Payload(
        string first,
        string second,
        IReadOnlyDictionary<string, long>? turns,
        long input,
        int messages,
        double totalCost) => Payload([first, second], turns, input, messages, totalCost);

    private static UsagePayload Payload(
        IReadOnlyList<string> dates,
        IReadOnlyDictionary<string, long>? turns,
        long input = long.MaxValue,
        int messages = -7,
        double totalCost = -0.25)
    {
        var days = dates.Select(date => Day(date, [Client("claude", input, messages, totalCost)], turns)).ToArray();
        var years = dates
            .GroupBy(date => date[..4], StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new YearMeta(group.Key, 1, totalCost,
                new DateRange(group.First(), group.Last())))
            .ToArray();
        return new UsagePayload(
            new UsageMeta("g", "v", new DateRange(dates[0], dates[^1]), PricingMode.BestEffort, CostCoverage.Complete),
            new UsageSummary(1, totalCost, dates.Count, dates.Count, 1, totalCost, ["claude"], ["m"]),
            years,
            days);
    }

    private static Contribution Day(
        string date,
        IReadOnlyList<ContributionClient> clients,
        IReadOnlyDictionary<string, long>? turns) => new(
        date,
        new ContributionTotals(1, clients.Sum(c => c.Cost), clients.Sum(c => c.Messages)),
        -1,
        new TokenBreakdown(long.MinValue, 2, -3, 4, -5),
        clients,
        turns);

    private static ContributionClient Client(
        string id,
        long input = long.MaxValue,
        int messages = -7,
        double cost = -0.25) => new(
        id,
        "m",
        "p",
        new TokenBreakdown(input, long.MinValue, -3, 4, -5),
        cost,
        messages);
}
