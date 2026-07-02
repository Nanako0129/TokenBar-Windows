using TokenBar.Interop;

// End-to-end check of the Rust↔C# seam: load the cdylib and exercise every
// FFI entry point, printing a one-line summary each. The macOS counterpart is
// `swift run TokenBar --smoke`. Exit 0 only if every exercised entry decoded.
//
// Env knobs:
//   TB_SMOKE_MIN_MESSAGES  fail unless tb_probe parses at least N messages
//                          (CI points HOME at a hermetic fixture tree)
//   TB_SMOKE_SKIP_NETWORK  "1" skips tb_agent_usage (network-bound, ~30s
//                          worst case; useless on hosts with no credentials)

var minRaw = Environment.GetEnvironmentVariable("TB_SMOKE_MIN_MESSAGES");
long min = 0;
if (!string.IsNullOrEmpty(minRaw) && !long.TryParse(minRaw, out min))
{
    // A set-but-unparseable expectation must fail loudly, not silently
    // degrade the hermetic assertion to "no crash".
    Console.Error.WriteLine($"TB_SMOKE_MIN_MESSAGES is not a number: '{minRaw}'");
    return 2;
}

var skipNetwork = Environment.GetEnvironmentVariable("TB_SMOKE_SKIP_NETWORK") == "1";
var failed = 0;

void Step(string name, Func<string> body)
{
    try
    {
        Console.WriteLine($"{name,-18} {body()}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"{name,-18} FAILED: {ex.Message}");
    }
}

long probeMessages = -1;
Step("tb_probe", () =>
{
    var probe = TbCore.Probe();
    probeMessages = probe.Messages;
    return $"ok messages={probe.Messages}";
});

Step("tb_graph", () =>
{
    var g = TbCore.Graph();
    return $"ok days={g.Contributions.Count} activeDays={g.Summary.ActiveDays} " +
           $"totalTokens={g.Summary.TotalTokens} totalCost={g.Summary.TotalCost:F2}";
});

Step("tb_refresh_graph", () =>
{
    var g = TbCore.RefreshGraph();
    return $"ok days={g.Contributions.Count} totalTokens={g.Summary.TotalTokens}";
});

Step("tb_model_report", () =>
{
    var r = TbCore.ModelReport();
    return $"ok entries={r.Entries.Count} totalCost={r.TotalCost:F2} " +
           $"pricingUpdatedAt={(r.PricingUpdatedAt?.ToString() ?? "-")}";
});

Step("tb_hourly_report", () =>
{
    var r = TbCore.HourlyReport();
    return $"ok entries={r.Entries.Count} totalCost={r.TotalCost:F2}";
});

Step("tb_agents_report", () =>
{
    var r = TbCore.AgentsReport();
    return $"ok entries={r.Entries.Count} totalMessages={r.TotalMessages}";
});

Step("tb_usage_trace", () =>
{
    var buckets = TbCore.UsageTrace(600);
    return $"ok buckets={buckets.Count}";
});

Step("tb_tokens_per_min", () => $"ok rate={TbCore.TokensPerMin():F1}");

if (skipNetwork)
{
    Console.WriteLine($"{"tb_agent_usage",-18} skipped (TB_SMOKE_SKIP_NETWORK=1)");
}
else
{
    Step("tb_agent_usage", () =>
    {
        var payload = TbCore.AgentUsage();
        var agents = string.Join(", ", payload.Agents.Select(a =>
            $"{a.ClientId}:{(a.Error is null ? $"{a.Windows.Count}win" : "err")}"));
        return $"ok agents=[{agents}]";
    });
}

if (failed > 0)
{
    Console.Error.WriteLine($"{failed} entry point(s) failed");
    return 1;
}

if (probeMessages < min)
{
    Console.Error.WriteLine($"tb_probe messages={probeMessages} < expected {min}");
    return 1;
}

return 0;
