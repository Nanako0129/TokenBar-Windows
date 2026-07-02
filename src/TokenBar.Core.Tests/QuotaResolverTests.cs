using System.Text.Json;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Ported from SelfTest.swift's quota-resolver block: the payload builds via
// JSON, same as the Swift fixture (the snapshot types have no builders).
public class QuotaResolverTests
{
    private static readonly AgentUsagePayload Payload = JsonSerializer.Deserialize<AgentUsagePayload>(
        """
        {"generatedAt":"now","agents":[
          {"clientId":"codex","source":"oauth","updatedAt":"now",
           "windows":[{"label":"Session","usedPercent":20,"remainingPercent":80},
                      {"label":"Weekly","usedPercent":65,"remainingPercent":35}]},
          {"clientId":"claude","source":"oauth","updatedAt":"now",
           "windows":[{"label":"Session","usedPercent":88,"remainingPercent":12},
                      {"label":"Weekly","usedPercent":10,"remainingPercent":90}]},
          {"clientId":"broken","source":"oauth","updatedAt":"now",
           "windows":[{"label":"Session","usedPercent":99,"remainingPercent":1}],
           "error":"401"}
        ]}
        """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void AutoResolvesTheTightestHealthyWindow()
    {
        var pick = QuotaResolver.Resolve(Payload, "auto");
        Assert.NotNull(pick);
        Assert.Equal("claude", pick.ClientId);
        Assert.Equal("Session", pick.Window.Label); // broken agent's 1% is skipped
    }

    [Fact]
    public void ExplicitSelectionResolves()
    {
        var pick = QuotaResolver.Resolve(Payload, "codex|Weekly");
        Assert.NotNull(pick);
        Assert.Equal(35, pick.Window.RemainingPercent);
    }

    [Fact]
    public void UnknownSelectionIsNull() =>
        Assert.Null(QuotaResolver.Resolve(Payload, "nope|Session"));

    [Fact]
    public void NoPayloadIsNull() =>
        Assert.Null(QuotaResolver.Resolve(null, "auto"));

    [Fact]
    public void EmptySelectionActsAsAuto() =>
        Assert.Equal("claude", QuotaResolver.Resolve(Payload, "")!.ClientId);
}
