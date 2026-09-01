using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Delta coverage for WindowUsage.swift → Interop/WindowUsage.cs: the
// saturating token folds on WindowMessage. Pure DTO logic, no native call.
public class WindowUsageDtoTests
{
    private static WindowMessage Message(long input, long output, long cacheRead, long cacheWrite, long reasoning) =>
        new(
            Timestamp: 1_700_000_000_000,
            Client: "claude",
            ProviderId: "anthropic",
            ModelId: "claude-3",
            Input: input,
            Output: output,
            CacheRead: cacheRead,
            CacheWrite: cacheWrite,
            Reasoning: reasoning,
            Cost: 0.0,
            IsTurnStart: true);

    [Fact]
    public void TokensSumsEveryLaneForNormalValues() =>
        Assert.Equal(31L, Message(1, 2, 4, 8, 16).Tokens);

    [Fact]
    public void TokensExCacheReadOmitsCacheReadForNormalValues() =>
        Assert.Equal(27L, Message(1, 2, 4, 8, 16).TokensExCacheRead);

    // A row whose counters would overflow long.MaxValue must saturate rather
    // than trap: these counters come from local session files this app does
    // not write, and a corrupt row must not be able to crash the always-on
    // menu bar.
    [Fact]
    public void TokensSaturatesInsteadOfOverflowing() =>
        Assert.Equal(long.MaxValue, Message(long.MaxValue, long.MaxValue, 0, 0, 0).Tokens);

    [Fact]
    public void TokensExCacheReadSaturatesInsteadOfOverflowing() =>
        Assert.Equal(long.MaxValue, Message(long.MaxValue, long.MaxValue, 0, 0, 0).TokensExCacheRead);

    // The documented double-wrongness of `Tokens - CacheRead` on a corrupt
    // row: TokensExCacheRead is folded independently from the components,
    // never derived by subtracting CacheRead from the saturated Tokens total
    // — so a saturated CacheRead does not zero out (or wrap negative) the
    // rest of the row's real, unrelated token counts.
    [Fact]
    public void TokensExCacheReadIsUnaffectedByAnOverflowingCacheRead() =>
        Assert.Equal(27L, Message(1, 2, long.MaxValue, 8, 16).TokensExCacheRead);
}
