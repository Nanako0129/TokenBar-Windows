using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenBar.Interop;

// OAuth quota cards (`AgentUsagePayload` in the Tauri frontend's
// src/lib/agentUsage.ts; Swift port: TokenBarCore/AgentUsage.swift).

public sealed record AgentIdentity(
    string? Email = null,
    string? Plan = null);

public sealed record HistoricalPace(
    double ExpectedUsedPercent,
    double? EtaSeconds = null,
    bool WillLastToReset = false,
    double? RunOutProbability = null);

public enum UsagePaceState
{
    LearningDuration,
    LearningHistory,
    Available,
    Unavailable,
    // Internal marker used when the complete paceStatus key is absent.
    LegacyMissing,
}

public enum UsagePaceDurationSource
{
    Provider,
    Contract,
    Observed,
}

public enum UsagePaceUnavailableReason
{
    WindowIdentity,
    MissingReset,
    InvalidEvidence,
    AccountScope,
    StoreCapacity,
    History,
    NonRecurring,
}

public sealed record PaceStatus(
    UsagePaceState State,
    string? WindowKey = null,
    long? DurationSeconds = null,
    UsagePaceDurationSource? DurationSource = null,
    long CompleteCycles = 0,
    UsagePaceUnavailableReason? Reason = null)
{
    public static PaceStatus LegacyMissing { get; } = new(UsagePaceState.LegacyMissing);
}

[JsonConverter(typeof(UsageWindowJsonConverter))]
public sealed record UsageWindow
{
    public const string LegacyMissingPresentationId = "legacy.missing.v1";

    public string CardId { get; init; }
    public string Label { get; init; }
    public double UsedPercent { get; init; }
    public double RemainingPercent { get; init; }
    public string? ResetsAt { get; init; }
    public string? ResetText { get; init; }
    /// <summary>Compatibility mirror; v3 pace uses <see cref="DurationSeconds"/>.</summary>
    public long? WindowMinutes { get; init; }
    public PaceStatus PaceStatus { get; init; }
    public HistoricalPace? HistoricalPace { get; init; }
    /// <summary>Derived only from <see cref="PaceStatus.DurationSeconds"/>.</summary>
    public long? DurationSeconds { get; init; }

    // Nullable params carry = null defaults so existing C# callers keep their
    // pre-v3 construction shape while the decoder enforces the strict wire.
    public UsageWindow(
        string Label,
        double UsedPercent,
        double RemainingPercent,
        string? ResetsAt = null,
        string? ResetText = null,
        long? WindowMinutes = null,
        string? CardId = null,
        PaceStatus? PaceStatus = null,
        HistoricalPace? HistoricalPace = null,
        long? DurationSeconds = null)
    {
        var resolvedPaceStatus = PaceStatus ?? global::TokenBar.Interop.PaceStatus.LegacyMissing;
        if (resolvedPaceStatus.State == UsagePaceState.LegacyMissing && DurationSeconds is not null)
        {
            throw new ArgumentException("legacy pace cannot carry DurationSeconds", nameof(DurationSeconds));
        }
        if (resolvedPaceStatus.State != UsagePaceState.LegacyMissing &&
            DurationSeconds is not null && DurationSeconds != resolvedPaceStatus.DurationSeconds)
        {
            throw new ArgumentException(
                "DurationSeconds must match nested PaceStatus.DurationSeconds",
                nameof(DurationSeconds));
        }

        this.CardId = CardId ?? LegacyMissingPresentationId;
        this.Label = Label;
        this.UsedPercent = UsedPercent;
        this.RemainingPercent = RemainingPercent;
        this.ResetsAt = ResetsAt;
        this.ResetText = ResetText;
        this.WindowMinutes = WindowMinutes;
        this.PaceStatus = resolvedPaceStatus;
        this.HistoricalPace = HistoricalPace;
        this.DurationSeconds = resolvedPaceStatus.State == UsagePaceState.LegacyMissing
            ? null
            : resolvedPaceStatus.DurationSeconds;
    }
}

public sealed class UsageWindowJsonConverter : JsonConverter<UsageWindow>
{
    private const long MaxDurationSeconds = 400L * 86_400L;

    public override bool HandleNull => true;

    public override UsageWindow Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("usage window must be a JSON object");
        }

        var label = RequiredString(root, "label");
        var usedPercent = RequiredDouble(root, "usedPercent");
        var remainingPercent = RequiredDouble(root, "remainingPercent");
        ValidatePercentages(usedPercent, remainingPercent);
        var resetsAt = OptionalString(root, "resetsAt");
        var resetText = OptionalString(root, "resetText");
        var windowMinutes = OptionalInt64(root, "windowMinutes");
        var historicalPace = OptionalHistoricalPace(root);

        if (root.TryGetProperty("paceStatus", out var paceStatusElement))
        {
            var cardId = RequiredString(root, "cardId");
            if (string.IsNullOrWhiteSpace(cardId))
            {
                throw Invalid("v3 pace requires a non-empty cardId");
            }

            // Required rather than decode-if-present: present null must fail.
            var paceStatus = ParsePaceStatus(paceStatusElement);
            ValidateV3Window(windowMinutes, paceStatus, historicalPace);
            return new UsageWindow(
                Label: label,
                UsedPercent: usedPercent,
                RemainingPercent: remainingPercent,
                ResetsAt: resetsAt,
                ResetText: resetText,
                WindowMinutes: windowMinutes,
                CardId: cardId,
                PaceStatus: paceStatus,
                HistoricalPace: historicalPace,
                DurationSeconds: paceStatus.DurationSeconds);
        }

        string? legacyCardId = null;
        if (root.TryGetProperty("cardId", out var legacyCardIdElement))
        {
            if (legacyCardIdElement.ValueKind != JsonValueKind.String)
            {
                throw Invalid("legacy pace cardId must be a string");
            }
            legacyCardId = legacyCardIdElement.GetString();
            if (string.IsNullOrWhiteSpace(legacyCardId))
            {
                throw Invalid("legacy pace cardId must be non-empty");
            }
        }

        return new UsageWindow(
            Label: label,
            UsedPercent: usedPercent,
            RemainingPercent: remainingPercent,
            ResetsAt: resetsAt,
            ResetText: resetText,
            WindowMinutes: windowMinutes,
            CardId: legacyCardId,
            PaceStatus: global::TokenBar.Interop.PaceStatus.LegacyMissing,
            HistoricalPace: historicalPace);
    }

    public override void Write(
        Utf8JsonWriter writer,
        UsageWindow value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("cardId", value.CardId);
        writer.WriteString("label", value.Label);
        writer.WriteNumber("usedPercent", value.UsedPercent);
        writer.WriteNumber("remainingPercent", value.RemainingPercent);
        if (value.ResetsAt is not null) writer.WriteString("resetsAt", value.ResetsAt);
        if (value.ResetText is not null) writer.WriteString("resetText", value.ResetText);
        if (value.PaceStatus.State == UsagePaceState.LegacyMissing)
        {
            if (value.WindowMinutes is { } windowMinutes)
            {
                writer.WriteNumber("windowMinutes", windowMinutes);
            }
        }
        else if (value.PaceStatus.DurationSeconds is { } durationSeconds)
        {
            // The nested status is the source of truth; never serialize a
            // caller-supplied compatibility mirror that disagrees with it.
            writer.WriteNumber("windowMinutes", durationSeconds / 60);
        }

        if (value.PaceStatus.State == UsagePaceState.LegacyMissing)
        {
            // Preserve legacy serialization without ever emitting the internal
            // legacyMissing enum value onto the wire.
            if (value.HistoricalPace is not null)
            {
                writer.WritePropertyName("historicalPace");
                WriteHistoricalPace(writer, value.HistoricalPace);
            }
        }
        else
        {
            writer.WritePropertyName("paceStatus");
            WritePaceStatus(writer, value.PaceStatus);
            if (value.HistoricalPace is not null)
            {
                writer.WritePropertyName("historicalPace");
                WriteHistoricalPace(writer, value.HistoricalPace);
            }
        }

        writer.WriteEndObject();
    }

    private static PaceStatus ParsePaceStatus(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("paceStatus must be a JSON object");
        }

        var state = ParseState(RequiredString(element, "state"));
        var windowKey = OptionalString(element, "windowKey");
        var durationSeconds = OptionalInt64(element, "durationSeconds");
        var durationSource = OptionalDurationSource(element, "durationSource");
        var completeCycles = RequiredInt64(element, "completeCycles");
        var reason = OptionalReason(element, "reason");

        ValidatePaceStatus(
            state,
            windowKey,
            durationSeconds,
            durationSource,
            completeCycles,
            reason);
        return new PaceStatus(
            State: state,
            WindowKey: windowKey,
            DurationSeconds: durationSeconds,
            DurationSource: durationSource,
            CompleteCycles: completeCycles,
            Reason: reason);
    }

    private static HistoricalPace? OptionalHistoricalPace(JsonElement window)
    {
        if (!window.TryGetProperty("historicalPace", out var element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("historicalPace must be a JSON object or null");
        }

        var expected = RequiredDouble(element, "expectedUsedPercent");
        var eta = OptionalDouble(element, "etaSeconds");
        var willLast = RequiredBoolean(element, "willLastToReset");
        var probability = OptionalDouble(element, "runOutProbability");
        if (!double.IsFinite(expected) || expected is < 0 or > 100)
        {
            throw Invalid("historical expectedUsedPercent is out of range");
        }
        if (eta is { } etaValue && (!double.IsFinite(etaValue) || etaValue < 0))
        {
            throw Invalid("historical etaSeconds is invalid");
        }
        if (probability is { } probabilityValue &&
            (!double.IsFinite(probabilityValue) || probabilityValue is < 0 or > 1))
        {
            throw Invalid("historical runOutProbability is invalid");
        }
        if ((eta is null) != willLast)
        {
            throw Invalid("historical etaSeconds and willLastToReset contradict");
        }

        return new HistoricalPace(expected, eta, willLast, probability);
    }

    private static void ValidatePaceStatus(
        UsagePaceState state,
        string? windowKey,
        long? durationSeconds,
        UsagePaceDurationSource? durationSource,
        long completeCycles,
        UsagePaceUnavailableReason? reason)
    {
        if (completeCycles < 0)
        {
            throw Invalid("pace completeCycles must be non-negative");
        }
        if (windowKey is { } key && string.IsNullOrWhiteSpace(key))
        {
            throw Invalid("pace windowKey must be non-empty");
        }

        var identityUnavailable =
            state == UsagePaceState.Unavailable && reason == UsagePaceUnavailableReason.WindowIdentity;
        if ((windowKey is null) != identityUnavailable)
        {
            throw Invalid("pace windowKey identity invariant failed");
        }

        if (durationSeconds is { } duration)
        {
            if (duration is < 1 or > MaxDurationSeconds)
            {
                throw Invalid("pace durationSeconds is out of range");
            }
            if (durationSource is null)
            {
                throw Invalid("pace durationSource is required with durationSeconds");
            }
        }
        else if (durationSource is not null &&
                 !(state == UsagePaceState.LearningDuration &&
                   durationSource == UsagePaceDurationSource.Observed))
        {
            throw Invalid("pace durationSource requires a duration");
        }

        switch (state)
        {
            case UsagePaceState.LearningDuration:
                if (durationSeconds is not null || reason is not null)
                {
                    throw Invalid("learningDuration pace invariant failed");
                }
                break;
            case UsagePaceState.LearningHistory:
            case UsagePaceState.Available:
                if (durationSeconds is null || durationSource is null || reason is not null)
                {
                    throw Invalid("duration-ready pace invariant failed");
                }
                break;
            case UsagePaceState.Unavailable:
                if (reason is null)
                {
                    throw Invalid("unavailable pace requires a reason");
                }
                break;
            case UsagePaceState.LegacyMissing:
                throw Invalid("legacy pace status is not a v3 wire state");
            default:
                throw Invalid("unknown pace state");
        }

        if (state != UsagePaceState.Unavailable && reason is not null)
        {
            throw Invalid("non-unavailable pace cannot have a reason");
        }
    }

    private static void ValidateV3Window(
        long? windowMinutes,
        PaceStatus paceStatus,
        HistoricalPace? historicalPace)
    {
        if (paceStatus.DurationSeconds is { } durationSeconds)
        {
            if (windowMinutes != durationSeconds / 60)
            {
                throw Invalid("pace windowMinutes must derive from durationSeconds");
            }
        }
        else if (windowMinutes is not null)
        {
            throw Invalid("pace windowMinutes requires durationSeconds");
        }

        switch (paceStatus.State)
        {
            case UsagePaceState.Available when historicalPace is null:
                throw Invalid("available pace requires historicalPace");
            case UsagePaceState.LearningHistory when historicalPace is not null:
            case UsagePaceState.LearningDuration when historicalPace is not null:
            case UsagePaceState.Unavailable when historicalPace is not null:
                throw Invalid("pace state and historicalPace contradict");
            case UsagePaceState.LegacyMissing:
                throw Invalid("legacy pace status cannot appear in v3 wire");
        }
    }

    private static UsagePaceState ParseState(string value) => value switch
    {
        "learningDuration" => UsagePaceState.LearningDuration,
        "learningHistory" => UsagePaceState.LearningHistory,
        "available" => UsagePaceState.Available,
        "unavailable" => UsagePaceState.Unavailable,
        _ => throw Invalid("unknown or internal pace state"),
    };

    private static UsagePaceDurationSource? OptionalDurationSource(JsonElement obj, string name)
    {
        var value = OptionalString(obj, name);
        return value switch
        {
            null => null,
            "provider" => UsagePaceDurationSource.Provider,
            "contract" => UsagePaceDurationSource.Contract,
            "observed" => UsagePaceDurationSource.Observed,
            _ => throw Invalid($"unknown pace duration source '{value}'"),
        };
    }

    private static UsagePaceUnavailableReason? OptionalReason(JsonElement obj, string name)
    {
        var value = OptionalString(obj, name);
        return value switch
        {
            null => null,
            "windowIdentity" => UsagePaceUnavailableReason.WindowIdentity,
            "missingReset" => UsagePaceUnavailableReason.MissingReset,
            "invalidEvidence" => UsagePaceUnavailableReason.InvalidEvidence,
            "accountScope" => UsagePaceUnavailableReason.AccountScope,
            "storeCapacity" => UsagePaceUnavailableReason.StoreCapacity,
            "history" => UsagePaceUnavailableReason.History,
            "nonRecurring" => UsagePaceUnavailableReason.NonRecurring,
            _ => throw Invalid($"unknown pace unavailable reason '{value}'"),
        };
    }

    private static string RequiredString(JsonElement obj, string name)
    {
        var element = RequiredProperty(obj, name);
        if (element.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"'{name}' must be a string");
        }
        return element.GetString()!;
    }

    private static string? OptionalString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (element.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"'{name}' must be a string or null");
        }
        return element.GetString();
    }

    private static double RequiredDouble(JsonElement obj, string name)
    {
        var element = RequiredProperty(obj, name);
        return NumberAsDouble(element, name);
    }

    private static double? OptionalDouble(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return NumberAsDouble(element, name);
    }

    private static double NumberAsDouble(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Number)
        {
            throw Invalid($"'{name}' must be a number");
        }
        try
        {
            return element.GetDouble();
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            throw Invalid($"'{name}' must be a finite JSON number");
        }
    }

    private static bool RequiredBoolean(JsonElement obj, string name)
    {
        var element = RequiredProperty(obj, name);
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid($"'{name}' must be a boolean");
        }
        return element.GetBoolean();
    }

    private static long RequiredInt64(JsonElement obj, string name)
    {
        var element = RequiredProperty(obj, name);
        return NumberAsInt64(element, name);
    }

    private static long? OptionalInt64(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return NumberAsInt64(element, name);
    }

    private static long NumberAsInt64(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Number)
        {
            throw Invalid($"'{name}' must be an integer or null");
        }
        try
        {
            return element.GetInt64();
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            throw Invalid($"'{name}' must be an Int64");
        }
    }

    private static JsonElement RequiredProperty(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var element))
        {
            throw Invalid($"missing required '{name}'");
        }
        return element;
    }

    private static void ValidatePercentages(double usedPercent, double remainingPercent)
    {
        if (!double.IsFinite(usedPercent) || !double.IsFinite(remainingPercent) ||
            usedPercent is < 0 or > 100 || remainingPercent is < 0 or > 100)
        {
            throw Invalid("usage percentages are out of range");
        }
        if (Math.Abs(usedPercent + remainingPercent - 100) >= 1e-6)
        {
            throw Invalid("usage percentages must sum to 100");
        }
    }

    private static void WritePaceStatus(Utf8JsonWriter writer, PaceStatus status)
    {
        writer.WriteStartObject();
        writer.WriteString("state", status.State switch
        {
            UsagePaceState.LearningDuration => "learningDuration",
            UsagePaceState.LearningHistory => "learningHistory",
            UsagePaceState.Available => "available",
            UsagePaceState.Unavailable => "unavailable",
            _ => throw Invalid("legacy or unknown pace state cannot be serialized")
        });
        if (status.WindowKey is not null) writer.WriteString("windowKey", status.WindowKey);
        if (status.DurationSeconds is { } duration) writer.WriteNumber("durationSeconds", duration);
        if (status.DurationSource is { } source)
        {
            writer.WriteString("durationSource", source switch
            {
                UsagePaceDurationSource.Provider => "provider",
                UsagePaceDurationSource.Contract => "contract",
                UsagePaceDurationSource.Observed => "observed",
                _ => throw Invalid("unknown pace duration source cannot be serialized")
            });
        }
        writer.WriteNumber("completeCycles", status.CompleteCycles);
        if (status.Reason is { } reason)
        {
            writer.WriteString("reason", reason switch
            {
                UsagePaceUnavailableReason.WindowIdentity => "windowIdentity",
                UsagePaceUnavailableReason.MissingReset => "missingReset",
                UsagePaceUnavailableReason.InvalidEvidence => "invalidEvidence",
                UsagePaceUnavailableReason.AccountScope => "accountScope",
                UsagePaceUnavailableReason.StoreCapacity => "storeCapacity",
                UsagePaceUnavailableReason.History => "history",
                UsagePaceUnavailableReason.NonRecurring => "nonRecurring",
                _ => throw Invalid("unknown pace unavailable reason cannot be serialized")
            });
        }
        writer.WriteEndObject();
    }

    private static void WriteHistoricalPace(Utf8JsonWriter writer, HistoricalPace pace)
    {
        writer.WriteStartObject();
        writer.WriteNumber("expectedUsedPercent", pace.ExpectedUsedPercent);
        if (pace.EtaSeconds is { } eta) writer.WriteNumber("etaSeconds", eta);
        writer.WriteBoolean("willLastToReset", pace.WillLastToReset);
        if (pace.RunOutProbability is { } probability)
        {
            writer.WriteNumber("runOutProbability", probability);
        }
        writer.WriteEndObject();
    }

    private static JsonException Invalid(string message) => new(message);
}

public sealed record CreditsSnapshot(
    bool Unlimited,
    double? Remaining = null);

public sealed record AgentUsageSnapshot(
    string ClientId,
    string Source,
    string UpdatedAt,
    IReadOnlyList<UsageWindow> Windows,
    AgentIdentity? Identity = null,
    CreditsSnapshot? Credits = null,
    string? Error = null) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (ClientId is null || Source is null || UpdatedAt is null || Windows is null)
        {
            throw new JsonException("agent usage snapshot has null required fields");
        }
        if (Windows.Any(static window => window is null))
        {
            throw new JsonException("agent usage snapshot windows cannot contain null");
        }
    }

    /// <summary>Order-preserving unique card view; first duplicate wins.</summary>
    [JsonIgnore]
    public IReadOnlyList<UsageWindow> UniqueCardWindows
    {
        get
        {
            var seen = new HashSet<string>();
            var unique = new List<UsageWindow>(Windows.Count);
            foreach (var window in Windows)
            {
                if (seen.Add(window.CardId))
                {
                    unique.Add(window);
                }
            }
            return unique;
        }
    }
}

public sealed record AgentUsagePayload(
    string GeneratedAt,
    IReadOnlyList<AgentUsageSnapshot> Agents,
    // Subscription-type providers opencode is authed against (e.g. ["Codex"]).
    // Omitted from the JSON entirely when empty.
    IReadOnlyList<string>? OpencodeSubscriptions = null) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (GeneratedAt is null || Agents is null)
        {
            throw new JsonException("agent usage payload has null required fields");
        }
        if (Agents.Any(static agent => agent is null))
        {
            throw new JsonException("agent usage payload agents cannot contain null");
        }
        if (OpencodeSubscriptions?.Any(static subscription => subscription is null) == true)
        {
            throw new JsonException("opencode subscriptions cannot contain null");
        }
    }
}
