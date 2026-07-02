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
/// All calls are blocking — invoke off the UI thread.
/// </summary>
public static class TbCore
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static ProbeResult Probe()
    {
        var json = TakeString(NativeMethods.tb_probe());
        var probe = JsonSerializer.Deserialize<ProbeResult>(json, JsonOpts)
            ?? throw new TbCoreException("tb_probe returned null JSON");
        if (!probe.Ok)
        {
            throw new TbCoreException("tb_probe reported ok=false");
        }

        return probe;
    }

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
    // takeBytes counterpart).
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
    [property: JsonPropertyName("messages")] long Messages);
