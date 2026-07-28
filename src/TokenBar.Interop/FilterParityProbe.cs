using System.Globalization;
using System.Text.Json.Serialization;

namespace TokenBar.Interop;

public enum FilterParityStatus
{
    [JsonStringEnumMemberName("match")]
    Match,
    [JsonStringEnumMemberName("mismatch")]
    Mismatch,
    [JsonStringEnumMemberName("sourceChanged")]
    SourceChanged,
    [JsonStringEnumMemberName("tokenUnavailable")]
    TokenUnavailable,
}

public sealed record FilterParityAggregate(
    long EntryCount,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long Reasoning,
    long TotalTokens,
    long MessageCount,
    double TotalCost);

public sealed record FilterParityReport(
    FilterParityStatus Status,
    FilterParityAggregate? Unfiltered = null,
    FilterParityAggregate? Full = null,
    FilterParityAggregate? Delta = null)
{
    public string SmokeSummary
    {
        get
        {
            var label = Status switch
            {
                FilterParityStatus.Match => "MATCH",
                FilterParityStatus.Mismatch => "MISMATCH",
                FilterParityStatus.SourceChanged => "SOURCE_CHANGED / SKIP",
                FilterParityStatus.TokenUnavailable => "TOKEN_UNAVAILABLE / SKIP",
                _ => throw new InvalidOperationException("unknown filter parity status"),
            };
            if (Status is not (FilterParityStatus.Match or FilterParityStatus.Mismatch) ||
                Delta is null)
            {
                return label;
            }

            var cost = Delta.TotalCost.ToString("F2", CultureInfo.InvariantCulture);
            return $"{label} entriesΔ={Delta.EntryCount} tokensΔ={Delta.TotalTokens} " +
                $"messagesΔ={Delta.MessageCount} costΔ={cost}";
        }
    }
}

public sealed record FilterParityProbe(
    FilterParityReport Hourly,
    FilterParityReport Agents,
    long PresentClientCount)
{
    public string SmokeSummary =>
        $"hourly={Hourly.SmokeSummary}; agents={Agents.SmokeSummary}";
}
