using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Wire-shape checks for the DTO layer: the envelope decoder plus the two
// naming quirks in the contract (TraceBucket is snake_case; tokensPerMin is
// a camelCase single field). Shapes mirror TokenBarCore's Decodable models.
public class DtoDecodeTests
{
    [Fact]
    public void TraceBucketDecodesSnakeCase()
    {
        var json = """
            {"ok":true,"data":[{"client":"claude","agent":"main","model":"claude-sonnet-4",
             "tokens":1234,"messages":5,"tokens_per_min":678.5}]}
            """;
        var buckets = TbCore.DecodeEnvelope<IReadOnlyList<TraceBucket>>(json);
        var b = Assert.Single(buckets);
        Assert.Equal("claude", b.Client);
        Assert.Equal(1234, b.Tokens);
        Assert.Equal(678.5, b.TokensPerMin);
    }

    [Fact]
    public void TokensPerMinDecodesCamelCase()
    {
        var v = TbCore.DecodeEnvelope<TokensPerMin>("""{"ok":true,"data":{"tokensPerMin":42.5}}""");
        Assert.Equal(42.5, v.Value);
    }

    [Fact]
    public void ModelReportDecodesLowercaseKQuirkAndOptionals()
    {
        var json = """
            {"ok":true,"data":{"entries":[{"client":"claude","model":"m","provider":"anthropic",
             "input":1,"output":2,"cacheRead":3,"cacheWrite":4,"reasoning":0,"total":10,
             "messageCount":1,"cost":0.5,"msPer1kTokens":12.5}],
             "totalInput":1,"totalOutput":2,"totalCacheRead":3,"totalCacheWrite":4,
             "totalMessages":1,"totalCost":0.5}}
            """;
        var r = TbCore.DecodeEnvelope<ModelReport>(json);
        Assert.Equal(12.5, r.Entries[0].MsPer1kTokens);
        Assert.Null(r.PricingUpdatedAt); // absent before the first pricing fetch
    }

    [Fact]
    public void AgentUsageDecodesOptionalFieldsAndOmittedSubscriptions()
    {
        var json = """
            {"ok":true,"data":{"generatedAt":"2026-07-02T00:00:00Z","agents":[
             {"clientId":"codex","source":"oauth","updatedAt":"2026-07-02T00:00:00Z",
              "identity":{"email":null,"plan":"plus"},
              "windows":[{"label":"Weekly","usedPercent":40.0,"remainingPercent":60.0,
                "resetsAt":null,"resetText":"Resets in 2d","windowMinutes":10080,
                "historicalExpectedPercent":55.5,"runOutProbability":0.1}],
              "credits":null,"error":null}]}}
            """;
        var p = TbCore.DecodeEnvelope<AgentUsagePayload>(json);
        var w = Assert.Single(Assert.Single(p.Agents).Windows);
        Assert.Equal(10080, w.WindowMinutes);
        Assert.Equal(55.5, w.HistoricalExpectedPercent);
        Assert.Null(p.OpencodeSubscriptions); // key omitted entirely when empty
    }

    [Fact]
    public void UsagePayloadDecodesNestedShape()
    {
        var json = """
            {"ok":true,"data":{
             "meta":{"generatedAt":"g","version":"v","dateRange":{"start":"2026-01-01","end":"2026-07-02"}},
             "summary":{"totalTokens":100,"totalCost":1.5,"totalDays":10,"activeDays":8,
               "averagePerDay":10.0,"maxCostInSingleDay":0.9,"clients":["claude"],"models":["m"]},
             "years":[{"year":"2026","totalTokens":100,"totalCost":1.5,
               "range":{"start":"2026-01-01","end":"2026-07-02"}}],
             "contributions":[{"date":"2026-07-01","totals":{"tokens":50,"cost":0.7,"messages":3},
               "intensity":2,"tokenBreakdown":{"input":10,"output":20,"cacheRead":15,"cacheWrite":5,"reasoning":0},
               "clients":[{"client":"claude","modelId":"m","providerId":"anthropic",
                 "tokens":{"input":10,"output":20,"cacheRead":15,"cacheWrite":5,"reasoning":0},
                 "cost":0.7,"messages":3}]}]}}
            """;
        var p = TbCore.DecodeEnvelope<UsagePayload>(json);
        Assert.Equal(8, p.Summary.ActiveDays);
        Assert.Equal("anthropic", p.Contributions[0].Clients[0].ProviderId);
        Assert.Equal(15, p.Contributions[0].TokenBreakdown.CacheRead);
    }
}
