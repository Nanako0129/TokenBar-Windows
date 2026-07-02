using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenBar.Interop;

public sealed class TbCoreException(string message) : Exception(message);

/// <summary>
/// Typed facade over the tb_core_ffi C ABI. Mirrors TBCore.swift in the macOS
/// repo: every entry point returns heap JSON that must be freed with tb_free
/// exactly once, wrapped in the envelope {"ok":true,"data":…} /
/// {"ok":false,"err":…} (tb_probe keeps its legacy top-level shape).
/// All calls are blocking — invoke off the UI thread; AgentUsage() is also
/// network-bound (~30s worst case).
/// </summary>
public static class TbCore
{
    // Required-parameter enforcement mirrors Swift Decodable's strictness:
    // a wire payload missing a non-optional field fails the decode loudly
    // instead of silently binding 0/null (nullable DTO params carry `= null`
    // defaults, so omitted optionals still decode like Swift optionals).
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    public static ProbeResult Probe()
    {
        var json = TakeString(NativeMethods.tb_probe());
        var probe = JsonSerializer.Deserialize<ProbeResult>(json, JsonOpts)
            ?? throw new TbCoreException("tb_probe returned null JSON");
        if (!probe.Ok)
        {
            throw new TbCoreException(probe.Err ?? "tb_probe reported ok=false");
        }

        return probe;
    }

    /// <summary>Contribution graph for <paramref name="year"/> (null = all
    /// time). Served from a &lt;=30s cache inside the cdylib when warm.</summary>
    public static UsagePayload Graph(string? year = null) =>
        Unwrap<UsagePayload>(NativeMethods.tb_graph(year));

    /// <summary>Contribution graph, always recomputed.</summary>
    public static UsagePayload RefreshGraph(string? year = null) =>
        Unwrap<UsagePayload>(NativeMethods.tb_refresh_graph(year));

    public static ModelReport ModelReport(string? year = null) =>
        Unwrap<ModelReport>(NativeMethods.tb_model_report(year));

    public static HourlyReport HourlyReport(string? year = null) =>
        Unwrap<HourlyReport>(NativeMethods.tb_hourly_report(year));

    public static AgentsReport AgentsReport(string? year = null) =>
        Unwrap<AgentsReport>(NativeMethods.tb_agents_report(year));

    /// <summary>Live trace buckets over the trailing window (lazily re-parses
    /// at most every 10s inside the cdylib).</summary>
    public static IReadOnlyList<TraceBucket> UsageTrace(long windowSecs) =>
        Unwrap<IReadOnlyList<TraceBucket>>(NativeMethods.tb_usage_trace(windowSecs));

    /// <summary>Live tokens/min estimate (10-minute-window average).</summary>
    public static double TokensPerMin() =>
        Unwrap<TokensPerMin>(NativeMethods.tb_tokens_per_min()).Value;

    /// <summary>OAuth quota cards for codex/claude/antigravity/copilot.
    /// Network-bound; per-provider failures are reported in each snapshot's
    /// Error, not thrown.</summary>
    public static AgentUsagePayload AgentUsage() =>
        Unwrap<AgentUsagePayload>(NativeMethods.tb_agent_usage());

    /// <summary>
    /// Decodes the standard FFI envelope, returning the payload or throwing
    /// the embedded error. Pure logic, split out (like TBCore.decodeEnvelope)
    /// so the contract is unit-testable without the native library.
    /// </summary>
    public static T DecodeEnvelope<T>(string json)
    {
        using var doc = ParseOrThrow(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("ok", out var ok) ||
            ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new TbCoreException("FFI envelope missing boolean 'ok'");
        }

        if (ok.ValueKind == JsonValueKind.False)
        {
            var err = root.TryGetProperty("err", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString()!
                : "unknown FFI error";
            throw new TbCoreException(err);
        }

        if (!root.TryGetProperty("data", out var data))
        {
            throw new TbCoreException("FFI envelope ok=true but missing 'data'");
        }

        return data.Deserialize<T>(JsonOpts)
            ?? throw new TbCoreException("FFI envelope 'data' decoded to null");
    }

    private static T Unwrap<T>(nint ptr) => DecodeEnvelope<T>(TakeString(ptr));

    private static JsonDocument ParseOrThrow(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new TbCoreException($"FFI returned malformed JSON: {ex.Message}");
        }
    }

    // Reads the returned heap JSON and frees it exactly once (TBCore.swift's
    // takeBytes counterpart) — the single legal consumer of a tb_* pointer.
    private static string TakeString(nint ptr)
    {
        if (ptr == 0)
        {
            throw new TbCoreException("FFI returned NULL");
        }

        try
        {
            return Marshal.PtrToStringUTF8(ptr)
                ?? throw new TbCoreException("FFI returned invalid UTF-8");
        }
        finally
        {
            NativeMethods.tb_free(ptr);
        }
    }
}

public sealed record ProbeResult(
    [property: JsonPropertyName("ok")] bool Ok,
    // The legacy error shape {"ok":false,"err":…} carries no messages field.
    [property: JsonPropertyName("messages")] long Messages = 0,
    [property: JsonPropertyName("err")] string? Err = null);
