using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TokenBar.Interop;

namespace TokenBar.Core;

public enum GraphSnapshotReadStatus
{
    Hit,
    Missing,
    InvalidInput,
    UnsafePath,
    InvalidData,
    IncompatibleSchema,
    ContextMismatch,
    QueryMismatch,
    TooLarge,
    IoFailure,
}

public enum GraphSnapshotWriteStatus
{
    Written,
    InvalidInput,
    UnsafePath,
    InvalidData,
    TooLarge,
    IoFailure,
}

public sealed record GraphSnapshotReadResult(
    GraphSnapshotReadStatus Status,
    UsagePayload? Payload = null,
    DateTimeOffset? CapturedAt = null);

/// <summary>
/// Strict graph-only cache. The path and opaque source identity come from the
/// caller; this type neither derives source roots nor chooses a profile path.
/// Expected cache and I/O failures are returned as closed, redacted statuses.
/// </summary>
public sealed class GraphSnapshotStore
{
    public const int SchemaVersion = 1;
    public const int MaxFileBytes = 16 * 1024 * 1024;
    public const int MaxDepth = 16;
    public const int MaxTokens = 1_000_000;
    public const int MaxStringBytes = 8 * 1024;
    public const int MaxSourceContextBytes = 4 * 1024;
    public const int MaxYears = 256;
    public const int MaxContributions = 100_000;
    public const int MaxSummaryItems = 16_384;
    public const int MaxItemsPerContribution = 16_384;
    public const int MaxNestedItems = 500_000;

    private readonly string _path;

    public GraphSnapshotStore(string path) => _path = path;

    public GraphSnapshotReadResult Read(string sourceContextId, string? year)
    {
        if (!TryNormalizeInputs(sourceContextId, year, out var normalizedYear))
        {
            return new(GraphSnapshotReadStatus.InvalidInput);
        }

        if (!TryResolvePath(out var path, out var directory))
        {
            return new(GraphSnapshotReadStatus.InvalidInput);
        }

        if (!Directory.Exists(directory))
        {
            return new(GraphSnapshotReadStatus.Missing);
        }

        try
        {
            var target = InspectTarget(path);
            if (target == TargetState.Missing)
            {
                return new(GraphSnapshotReadStatus.Missing);
            }
            if (target == TargetState.Unsafe)
            {
                return new(GraphSnapshotReadStatus.UnsafePath);
            }

            var bytes = ReadBounded(path);
            var envelope = SnapshotCodec.Read(bytes);
            if (envelope.SchemaVersion != SchemaVersion)
            {
                return new(GraphSnapshotReadStatus.IncompatibleSchema);
            }
            if (!string.Equals(envelope.SourceContextId, sourceContextId, StringComparison.Ordinal))
            {
                return new(GraphSnapshotReadStatus.ContextMismatch);
            }
            if (!string.Equals(envelope.Year, normalizedYear, StringComparison.Ordinal))
            {
                return new(GraphSnapshotReadStatus.QueryMismatch);
            }

            SnapshotValidator.Validate(normalizedYear, envelope.CapturedAt, envelope.Payload);
            return new(GraphSnapshotReadStatus.Hit, envelope.Payload, envelope.CapturedAt);
        }
        catch (SnapshotTooLargeException)
        {
            return new(GraphSnapshotReadStatus.TooLarge);
        }
        catch (SnapshotSchemaException)
        {
            return new(GraphSnapshotReadStatus.IncompatibleSchema);
        }
        catch (SnapshotFormatException)
        {
            return new(GraphSnapshotReadStatus.InvalidData);
        }
        catch (JsonException)
        {
            return new(GraphSnapshotReadStatus.InvalidData);
        }
        catch (Exception ex) when (IsExpectedFileFailure(ex))
        {
            return new(GraphSnapshotReadStatus.IoFailure);
        }
    }

    public GraphSnapshotWriteStatus Write(
        string sourceContextId,
        string? year,
        DateTimeOffset capturedAt,
        UsagePayload payload)
    {
        if (payload is null || !TryNormalizeInputs(sourceContextId, year, out var normalizedYear))
        {
            return GraphSnapshotWriteStatus.InvalidInput;
        }

        if (!TryResolvePath(out var path, out var directory) || !Directory.Exists(directory))
        {
            return GraphSnapshotWriteStatus.InvalidInput;
        }

        byte[] encoded;
        try
        {
            SnapshotValidator.Validate(normalizedYear, capturedAt, payload);
            encoded = SnapshotCodec.Write(new SnapshotEnvelope(
                SchemaVersion,
                sourceContextId,
                normalizedYear,
                capturedAt.ToUniversalTime(),
                payload));
            // Exercise the same token/depth/string/count decoder before the
            // filesystem is touched, so writer and reader accept one contract.
            _ = SnapshotCodec.Read(encoded);
        }
        catch (SnapshotTooLargeException)
        {
            return GraphSnapshotWriteStatus.TooLarge;
        }
        catch (SnapshotFormatException)
        {
            return GraphSnapshotWriteStatus.InvalidData;
        }
        catch (Exception ex) when (ex is
            ArgumentException or
            EncoderFallbackException or
            NullReferenceException)
        {
            // Nullable annotations are not a runtime trust boundary. A caller
            // can still construct an invalid graph with a required nested
            // object/list set to null; reject it before any filesystem access.
            return GraphSnapshotWriteStatus.InvalidData;
        }

        string? temp = null;
        try
        {
            if (InspectTarget(path) == TargetState.Unsafe)
            {
                return GraphSnapshotWriteStatus.UnsafePath;
            }

            var fileName = Path.GetFileName(path);
            temp = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.SequentialScan))
            {
                stream.Write(encoded);
                stream.Flush(flushToDisk: true);
            }

            // Same-directory rename is the platform primitive that preserves an
            // old-or-new target during normal execution; power-loss durability
            // is deliberately outside this reconstructible cache's contract.
            File.Move(temp, path, overwrite: true);
            temp = null;
            return GraphSnapshotWriteStatus.Written;
        }
        catch (Exception ex) when (IsExpectedFileFailure(ex))
        {
            return GraphSnapshotWriteStatus.IoFailure;
        }
        finally
        {
            if (temp is not null)
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                }
            }
        }
    }

    private static bool TryNormalizeInputs(
        string sourceContextId,
        string? year,
        out string? normalizedYear)
    {
        normalizedYear = null;
        if (string.IsNullOrWhiteSpace(sourceContextId)
            || sourceContextId.IndexOf('\0') >= 0
            || Utf8Length(sourceContextId) > MaxSourceContextBytes)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(year))
        {
            return true;
        }

        var trimmed = year.Trim();
        if (trimmed.Length != 4 || trimmed.Any(c => c is < '0' or > '9'))
        {
            return false;
        }

        normalizedYear = trimmed;
        return true;
    }

    private bool TryResolvePath(out string path, out string directory)
    {
        path = string.Empty;
        directory = string.Empty;
        if (string.IsNullOrWhiteSpace(_path) || _path.IndexOf('\0') >= 0)
        {
            return false;
        }

        try
        {
            path = Path.GetFullPath(_path);
            directory = Path.GetDirectoryName(path) ?? string.Empty;
            return directory.Length > 0 && Path.GetFileName(path).Length > 0;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static TargetState InspectTarget(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0
                ? TargetState.Unsafe
                : TargetState.Regular;
        }
        catch (FileNotFoundException)
        {
            return TargetState.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return TargetState.Missing;
        }
    }

    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > MaxFileBytes)
        {
            throw new SnapshotTooLargeException();
        }

        using var buffer = new MemoryStream((int)Math.Min(stream.Length, MaxFileBytes));
        var chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var total = 0;
            while (true)
            {
                var remaining = MaxFileBytes + 1 - total;
                if (remaining <= 0)
                {
                    throw new SnapshotTooLargeException();
                }

                var read = stream.Read(chunk, 0, Math.Min(chunk.Length, remaining));
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > MaxFileBytes)
                {
                    throw new SnapshotTooLargeException();
                }
                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private static bool IsExpectedFileFailure(Exception ex) => ex is
        IOException or
        UnauthorizedAccessException or
        System.Security.SecurityException or
        NotSupportedException or
        ArgumentException;

    private static int Utf8Length(string value)
    {
        try
        {
            return Encoding.UTF8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            return int.MaxValue;
        }
    }

    private enum TargetState
    {
        Missing,
        Regular,
        Unsafe,
    }

    private sealed record SnapshotEnvelope(
        int SchemaVersion,
        string SourceContextId,
        string? Year,
        DateTimeOffset CapturedAt,
        UsagePayload Payload);

    private static class SnapshotValidator
    {
        public static void Validate(string? year, DateTimeOffset capturedAt, UsagePayload payload)
        {
            var canonicalCapturedAt = capturedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            if (!TryReadCapturedAt(canonicalCapturedAt, out _))
            {
                throw new SnapshotFormatException();
            }

            ValidatePayloadBounds(payload);

            if (payload.Contributions.Count == 0)
            {
                if (payload.Years.Count != 0
                    || payload.Meta.DateRange.Start.Length != 0
                    || payload.Meta.DateRange.End.Length != 0)
                {
                    throw new SnapshotFormatException();
                }
                return;
            }

            var ranges = new Dictionary<string, (string First, string Last)>(StringComparer.Ordinal);
            string? previous = null;
            foreach (var contribution in payload.Contributions)
            {
                var date = ParseDate(contribution.Date);
                var canonical = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!string.Equals(canonical, contribution.Date, StringComparison.Ordinal)
                    || previous is not null && string.CompareOrdinal(previous, canonical) >= 0)
                {
                    throw new SnapshotFormatException();
                }

                var contributionYear = canonical[..4];
                if (year is not null && !string.Equals(year, contributionYear, StringComparison.Ordinal))
                {
                    throw new SnapshotFormatException();
                }

                ranges[contributionYear] = ranges.TryGetValue(contributionYear, out var range)
                    ? (range.First, canonical)
                    : (canonical, canonical);
                previous = canonical;
            }

            if (!string.Equals(payload.Meta.DateRange.Start, payload.Contributions[0].Date, StringComparison.Ordinal)
                || !string.Equals(payload.Meta.DateRange.End, payload.Contributions[^1].Date, StringComparison.Ordinal))
            {
                throw new SnapshotFormatException();
            }
            ParseRange(payload.Meta.DateRange);

            if (payload.Years.Count != ranges.Count)
            {
                throw new SnapshotFormatException();
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in payload.Years)
            {
                if (!IsYear(item.Year) || !seen.Add(item.Year)
                    || year is not null && !string.Equals(year, item.Year, StringComparison.Ordinal)
                    || !ranges.TryGetValue(item.Year, out var expected)
                    || !string.Equals(item.Range.Start, expected.First, StringComparison.Ordinal)
                    || !string.Equals(item.Range.End, expected.Last, StringComparison.Ordinal))
                {
                    throw new SnapshotFormatException();
                }
                ParseRange(item.Range);
            }
        }

        private static void ValidatePayloadBounds(UsagePayload payload)
        {
            if (payload.Years.Count > MaxYears
                || payload.Contributions.Count > MaxContributions
                || payload.Summary.Clients.Count > MaxSummaryItems
                || payload.Summary.Models.Count > MaxSummaryItems)
            {
                throw new SnapshotTooLargeException();
            }

            ValidateString(payload.Meta.GeneratedAt);
            ValidateString(payload.Meta.Version);
            ValidateString(payload.Meta.DateRange.Start);
            ValidateString(payload.Meta.DateRange.End);
            ValidateFinite(payload.Summary.TotalCost);
            ValidateFinite(payload.Summary.AveragePerDay);
            ValidateFinite(payload.Summary.MaxCostInSingleDay);
            foreach (var value in payload.Summary.Clients)
            {
                ValidateString(value);
            }
            foreach (var value in payload.Summary.Models)
            {
                ValidateString(value);
            }

            long nested = 0;
            foreach (var item in payload.Years)
            {
                ValidateString(item.Year);
                ValidateString(item.Range.Start);
                ValidateString(item.Range.End);
                ValidateFinite(item.TotalCost);
            }

            foreach (var contribution in payload.Contributions)
            {
                if (contribution.Clients.Count > MaxItemsPerContribution
                    || contribution.TurnsByClient is { Count: > MaxItemsPerContribution })
                {
                    throw new SnapshotTooLargeException();
                }

                nested += contribution.Clients.Count + (contribution.TurnsByClient?.Count ?? 0);
                if (nested > MaxNestedItems)
                {
                    throw new SnapshotTooLargeException();
                }

                ValidateString(contribution.Date);
                ValidateFinite(contribution.Totals.Cost);
                foreach (var client in contribution.Clients)
                {
                    ValidateString(client.Client);
                    ValidateString(client.ModelId);
                    ValidateString(client.ProviderId);
                    ValidateFinite(client.Cost);
                }

                if (contribution.TurnsByClient is not null)
                {
                    var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in contribution.TurnsByClient)
                    {
                        ValidateString(pair.Key);
                        if (!keys.Add(pair.Key))
                        {
                            throw new SnapshotFormatException();
                        }
                    }
                }
            }
        }

        private static void ValidateString(string value)
        {
            if (value is null || Utf8Length(value) > MaxStringBytes)
            {
                throw new SnapshotTooLargeException();
            }
        }

        private static void ValidateFinite(double value)
        {
            if (!double.IsFinite(value))
            {
                throw new SnapshotFormatException();
            }
        }

        private static DateOnly ParseDate(string value)
        {
            if (!DateOnly.TryParseExact(
                    value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date))
            {
                throw new SnapshotFormatException();
            }
            return date;
        }

        private static void ParseRange(DateRange range)
        {
            var start = ParseDate(range.Start);
            var end = ParseDate(range.End);
            if (start > end)
            {
                throw new SnapshotFormatException();
            }
        }

        private static bool IsYear(string value) =>
            value.Length == 4 && value.All(c => c is >= '0' and <= '9');
    }

    private static bool TryReadCapturedAt(string value, out DateTimeOffset capturedAt)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out capturedAt)
            || capturedAt.Offset != TimeSpan.Zero)
        {
            return false;
        }

        return string.Equals(
            capturedAt.ToString("O", CultureInfo.InvariantCulture),
            value,
            StringComparison.Ordinal);
    }

    private static class SnapshotCodec
    {
        public static SnapshotEnvelope Read(ReadOnlySpan<byte> bytes)
        {
            var reader = new StrictReader(bytes);
            reader.Begin();
            reader.StartObject();
            var seen = NewNames();
            int? schema = null;
            string? context = null;
            QueryValue? query = null;
            DateTimeOffset? capturedAt = null;
            UsagePayload? payload = null;

            while (reader.NextProperty(seen, out var name))
            {
                switch (name)
                {
                    case "schemaVersion": schema = reader.Int32(); break;
                    case "sourceContextId": context = reader.String(); break;
                    case "query": query = ReadQuery(ref reader); break;
                    case "capturedAt":
                        var raw = reader.String();
                        if (!TryReadCapturedAt(raw, out var parsed))
                        {
                            throw new SnapshotFormatException();
                        }
                        capturedAt = parsed;
                        break;
                    case "payload": payload = ReadPayload(ref reader); break;
                    default: throw new SnapshotFormatException();
                }
            }
            reader.Finish();

            if (schema is null || context is null || query is null || capturedAt is null || payload is null)
            {
                throw new SnapshotFormatException();
            }
            if (schema != SchemaVersion)
            {
                throw new SnapshotSchemaException();
            }
            if (!TryNormalizeInputs(context, query.Value.Year, out var storedYear)
                || !string.Equals(storedYear, query.Value.Year, StringComparison.Ordinal))
            {
                throw new SnapshotFormatException();
            }
            return new(schema.Value, context, query.Value.Year, capturedAt.Value, payload);
        }

        public static byte[] Write(SnapshotEnvelope envelope)
        {
            using var output = new MemoryStream();
            using var bounded = new BoundedWriteStream(output, MaxFileBytes);
            using (var writer = new Utf8JsonWriter(bounded))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", envelope.SchemaVersion);
                writer.WriteString("sourceContextId", envelope.SourceContextId);
                writer.WriteStartObject("query");
                if (envelope.Year is null) writer.WriteNull("year");
                else writer.WriteString("year", envelope.Year);
                writer.WriteEndObject();
                writer.WriteString("capturedAt", envelope.CapturedAt.ToString("O", CultureInfo.InvariantCulture));
                writer.WritePropertyName("payload");
                WritePayload(writer, envelope.Payload);
                writer.WriteEndObject();
                writer.Flush();
            }
            return output.ToArray();
        }

        private static QueryValue ReadQuery(ref StrictReader reader)
        {
            reader.StartObject();
            var seen = NewNames();
            var present = false;
            string? year = null;
            while (reader.NextProperty(seen, out var name))
            {
                if (name != "year") throw new SnapshotFormatException();
                year = reader.NullableString();
                present = true;
            }
            if (!present || year is not null && !IsYear(year)) throw new SnapshotFormatException();
            return new(year);
        }

        private static UsagePayload ReadPayload(ref StrictReader reader)
        {
            reader.StartObject();
            var seen = NewNames();
            UsageMeta? meta = null;
            UsageSummary? summary = null;
            IReadOnlyList<YearMeta>? years = null;
            IReadOnlyList<Contribution>? contributions = null;
            while (reader.NextProperty(seen, out var name))
            {
                switch (name)
                {
                    case "meta": meta = ReadMeta(ref reader); break;
                    case "summary": summary = ReadSummary(ref reader); break;
                    case "years": years = ReadYears(ref reader); break;
                    case "contributions": contributions = ReadContributions(ref reader); break;
                    default: throw new SnapshotFormatException();
                }
            }
            if (meta is null || summary is null || years is null || contributions is null)
                throw new SnapshotFormatException();
            return new(meta, summary, years, contributions);
        }

        private static UsageMeta ReadMeta(ref StrictReader reader)
        {
            reader.StartObject();
            var seen = NewNames();
            string? generatedAt = null;
            string? version = null;
            DateRange? range = null;
            PricingMode? pricing = null;
            CostCoverage? coverage = null;
            while (reader.NextProperty(seen, out var name))
            {
                switch (name)
                {
                    case "generatedAt": generatedAt = reader.String(); break;
                    case "version": version = reader.String(); break;
                    case "dateRange": range = ReadRange(ref reader); break;
                    case "pricingMode": pricing = reader.String() switch
                    {
                        "localOnly" => PricingMode.LocalOnly,
                        "bestEffort" => PricingMode.BestEffort,
                        _ => throw new SnapshotFormatException(),
                    }; break;
                    case "costCoverage": coverage = reader.String() switch
                    {
                        "complete" => CostCoverage.Complete,
                        "partial" => CostCoverage.Partial,
                        "none" => CostCoverage.None,
                        _ => throw new SnapshotFormatException(),
                    }; break;
                    default: throw new SnapshotFormatException();
                }
            }
            if (generatedAt is null || version is null || range is null || pricing is null || coverage is null)
                throw new SnapshotFormatException();
            return new(generatedAt, version, range, pricing.Value, coverage.Value);
        }

        private static UsageSummary ReadSummary(ref StrictReader reader)
        {
            reader.StartObject();
            var seen = NewNames();
            long? totalTokens = null;
            double? totalCost = null, average = null, maxCost = null;
            int? totalDays = null, activeDays = null;
            IReadOnlyList<string>? clients = null, models = null;
            while (reader.NextProperty(seen, out var name))
            {
                switch (name)
                {
                    case "totalTokens": totalTokens = reader.Int64(); break;
                    case "totalCost": totalCost = reader.Double(); break;
                    case "totalDays": totalDays = reader.Int32(); break;
                    case "activeDays": activeDays = reader.Int32(); break;
                    case "averagePerDay": average = reader.Double(); break;
                    case "maxCostInSingleDay": maxCost = reader.Double(); break;
                    case "clients": clients = ReadStrings(ref reader, MaxSummaryItems); break;
                    case "models": models = ReadStrings(ref reader, MaxSummaryItems); break;
                    default: throw new SnapshotFormatException();
                }
            }
            if (totalTokens is null || totalCost is null || totalDays is null || activeDays is null
                || average is null || maxCost is null || clients is null || models is null)
                throw new SnapshotFormatException();
            return new(totalTokens.Value, totalCost.Value, totalDays.Value, activeDays.Value,
                average.Value, maxCost.Value, clients, models);
        }

        private static IReadOnlyList<YearMeta> ReadYears(ref StrictReader reader)
        {
            reader.StartArray();
            var result = new List<YearMeta>();
            while (reader.NextArrayItem())
            {
                if (result.Count >= MaxYears) throw new SnapshotTooLargeException();
                result.Add(ReadYear(ref reader));
            }
            return result;
        }

        private static YearMeta ReadYear(ref StrictReader reader)
        {
            reader.StartObject();
            var seen = NewNames();
            string? year = null;
            long? tokens = null;
            double? cost = null;
            DateRange? range = null;
            while (reader.NextProperty(seen, out var name))
            {
                switch (name)
                {
                    case "year": year = reader.String(); break;
                    case "totalTokens": tokens = reader.Int64(); break;
                    case "totalCost": cost = reader.Double(); break;
                    case "range": range = ReadRange(ref reader); break;
                    default: throw new SnapshotFormatException();
                }
            }
            if (year is null || tokens is null || cost is null || range is null)
                throw new SnapshotFormatException();
            return new(year, tokens.Value, cost.Value, range);
        }

        private static IReadOnlyList<Contribution> ReadContributions(ref StrictReader reader)
        {
            reader.StartArray();
            var result = new List<Contribution>();
            while (reader.NextArrayItem())
            {
                if (result.Count >= MaxContributions) throw new SnapshotTooLargeException();
                result.Add(ReadContribution(ref reader));
            }
            return result;
        }

        private static Contribution ReadContribution(ref StrictReader reader)
        {
            reader.StartObject();
            var seen = NewNames();
            string? date = null;
            ContributionTotals? totals = null;
            int? intensity = null;
            TokenBreakdown? breakdown = null;
            IReadOnlyList<ContributionClient>? clients = null;
            IReadOnlyDictionary<string, long>? turns = null;
            var turnsPresent = false;
            while (reader.NextProperty(seen, out var name))
            {
                switch (name)
                {
                    case "date": date = reader.String(); break;
                    case "totals": totals = ReadTotals(ref reader); break;
                    case "intensity": intensity = reader.Int32(); break;
                    case "tokenBreakdown": breakdown = ReadBreakdown(ref reader); break;
                    case "clients": clients = ReadClients(ref reader); break;
                    case "turnsByClient":
                        turnsPresent = true;
                        turns = reader.IsNull() ? null : ReadTurns(ref reader);
                        break;
                    default: throw new SnapshotFormatException();
                }
            }
            if (date is null || totals is null || intensity is null || breakdown is null
                || clients is null || !turnsPresent)
                throw new SnapshotFormatException();
            return new(date, totals, intensity.Value, breakdown, clients, turns);
        }

        private static ContributionTotals ReadTotals(ref StrictReader reader)
        {
            reader.StartObject();
            var seen = NewNames();
            long? tokens = null;
            double? cost = null;
            int? messages = null;
            while (reader.NextProperty(seen, out var name))
            {
                switch (name)
                {
                    case "tokens": tokens = reader.Int64(); break;
                    case "cost": cost = reader.Double(); break;
                    case "messages": messages = reader.Int32(); break;
                    default: throw new SnapshotFormatException();
                }
            }
            if (tokens is null || cost is null || messages is null) throw new SnapshotFormatException();
            return new(tokens.Value, cost.Value, messages.Value);
        }

        private static TokenBreakdown ReadBreakdown(ref StrictReader reader)
        {
            reader.StartObject();
            var seen = NewNames();
            long? input = null, output = null, cacheRead = null, cacheWrite = null, reasoning = null;
            while (reader.NextProperty(seen, out var name))
            {
                switch (name)
                {
                    case "input": input = reader.Int64(); break;
                    case "output": output = reader.Int64(); break;
                    case "cacheRead": cacheRead = reader.Int64(); break;
                    case "cacheWrite": cacheWrite = reader.Int64(); break;
                    case "reasoning": reasoning = reader.Int64(); break;
                    default: throw new SnapshotFormatException();
                }
            }
            if (input is null || output is null || cacheRead is null || cacheWrite is null || reasoning is null)
                throw new SnapshotFormatException();
            return new(input.Value, output.Value, cacheRead.Value, cacheWrite.Value, reasoning.Value);
        }

        private static IReadOnlyList<ContributionClient> ReadClients(ref StrictReader reader)
        {
            reader.StartArray();
            var result = new List<ContributionClient>();
            while (reader.NextArrayItem())
            {
                if (result.Count >= MaxItemsPerContribution) throw new SnapshotTooLargeException();
                reader.AddNestedItem();
                result.Add(ReadClient(ref reader));
            }
            return result;
        }

        private static ContributionClient ReadClient(ref StrictReader reader)
        {
            reader.StartObject();
            var seen = NewNames();
            string? client = null, model = null, provider = null;
            TokenBreakdown? tokens = null;
            double? cost = null;
            int? messages = null;
            while (reader.NextProperty(seen, out var name))
            {
                switch (name)
                {
                    case "client": client = reader.String(); break;
                    case "modelId": model = reader.String(); break;
                    case "providerId": provider = reader.String(); break;
                    case "tokens": tokens = ReadBreakdown(ref reader); break;
                    case "cost": cost = reader.Double(); break;
                    case "messages": messages = reader.Int32(); break;
                    default: throw new SnapshotFormatException();
                }
            }
            if (client is null || model is null || provider is null || tokens is null || cost is null || messages is null)
                throw new SnapshotFormatException();
            return new(client, model, provider, tokens, cost.Value, messages.Value);
        }

        private static IReadOnlyDictionary<string, long> ReadTurns(ref StrictReader reader)
        {
            reader.StartObject();
            var names = NewNames();
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            while (reader.NextProperty(names, out var name))
            {
                if (result.Count >= MaxItemsPerContribution) throw new SnapshotTooLargeException();
                reader.AddNestedItem();
                result.Add(name, reader.Int64());
            }
            return result;
        }

        private static DateRange ReadRange(ref StrictReader reader)
        {
            reader.StartObject();
            var seen = NewNames();
            string? start = null, end = null;
            while (reader.NextProperty(seen, out var name))
            {
                switch (name)
                {
                    case "start": start = reader.String(); break;
                    case "end": end = reader.String(); break;
                    default: throw new SnapshotFormatException();
                }
            }
            if (start is null || end is null) throw new SnapshotFormatException();
            return new(start, end);
        }

        private static IReadOnlyList<string> ReadStrings(ref StrictReader reader, int max)
        {
            reader.StartArray();
            var result = new List<string>();
            while (reader.NextArrayItem())
            {
                if (result.Count >= max) throw new SnapshotTooLargeException();
                result.Add(reader.String());
            }
            return result;
        }

        private static void WritePayload(Utf8JsonWriter writer, UsagePayload payload)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("meta"); WriteMeta(writer, payload.Meta);
            writer.WritePropertyName("summary"); WriteSummary(writer, payload.Summary);
            writer.WriteStartArray("years");
            foreach (var item in payload.Years) WriteYear(writer, item);
            writer.WriteEndArray();
            writer.WriteStartArray("contributions");
            foreach (var item in payload.Contributions) WriteContribution(writer, item);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        private static void WriteMeta(Utf8JsonWriter writer, UsageMeta meta)
        {
            writer.WriteStartObject();
            writer.WriteString("generatedAt", meta.GeneratedAt);
            writer.WriteString("version", meta.Version);
            writer.WritePropertyName("dateRange"); WriteRange(writer, meta.DateRange);
            writer.WriteString("pricingMode", meta.PricingMode switch
            {
                PricingMode.LocalOnly => "localOnly",
                PricingMode.BestEffort => "bestEffort",
                _ => throw new SnapshotFormatException(),
            });
            writer.WriteString("costCoverage", meta.CostCoverage switch
            {
                CostCoverage.Complete => "complete",
                CostCoverage.Partial => "partial",
                CostCoverage.None => "none",
                _ => throw new SnapshotFormatException(),
            });
            writer.WriteEndObject();
        }

        private static void WriteSummary(Utf8JsonWriter writer, UsageSummary summary)
        {
            writer.WriteStartObject();
            writer.WriteNumber("totalTokens", summary.TotalTokens);
            writer.WriteNumber("totalCost", summary.TotalCost);
            writer.WriteNumber("totalDays", summary.TotalDays);
            writer.WriteNumber("activeDays", summary.ActiveDays);
            writer.WriteNumber("averagePerDay", summary.AveragePerDay);
            writer.WriteNumber("maxCostInSingleDay", summary.MaxCostInSingleDay);
            writer.WriteStartArray("clients"); foreach (var item in summary.Clients) writer.WriteStringValue(item); writer.WriteEndArray();
            writer.WriteStartArray("models"); foreach (var item in summary.Models) writer.WriteStringValue(item); writer.WriteEndArray();
            writer.WriteEndObject();
        }

        private static void WriteYear(Utf8JsonWriter writer, YearMeta item)
        {
            writer.WriteStartObject();
            writer.WriteString("year", item.Year);
            writer.WriteNumber("totalTokens", item.TotalTokens);
            writer.WriteNumber("totalCost", item.TotalCost);
            writer.WritePropertyName("range"); WriteRange(writer, item.Range);
            writer.WriteEndObject();
        }

        private static void WriteContribution(Utf8JsonWriter writer, Contribution item)
        {
            writer.WriteStartObject();
            writer.WriteString("date", item.Date);
            writer.WriteStartObject("totals");
            writer.WriteNumber("tokens", item.Totals.Tokens);
            writer.WriteNumber("cost", item.Totals.Cost);
            writer.WriteNumber("messages", item.Totals.Messages);
            writer.WriteEndObject();
            writer.WriteNumber("intensity", item.Intensity);
            writer.WritePropertyName("tokenBreakdown"); WriteBreakdown(writer, item.TokenBreakdown);
            writer.WriteStartArray("clients");
            foreach (var client in item.Clients)
            {
                writer.WriteStartObject();
                writer.WriteString("client", client.Client);
                writer.WriteString("modelId", client.ModelId);
                writer.WriteString("providerId", client.ProviderId);
                writer.WritePropertyName("tokens"); WriteBreakdown(writer, client.Tokens);
                writer.WriteNumber("cost", client.Cost);
                writer.WriteNumber("messages", client.Messages);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("turnsByClient");
            if (item.TurnsByClient is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartObject();
                foreach (var pair in item.TurnsByClient.OrderBy(p => p.Key, StringComparer.Ordinal))
                    writer.WriteNumber(pair.Key, pair.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }

        private static void WriteBreakdown(Utf8JsonWriter writer, TokenBreakdown value)
        {
            writer.WriteStartObject();
            writer.WriteNumber("input", value.Input);
            writer.WriteNumber("output", value.Output);
            writer.WriteNumber("cacheRead", value.CacheRead);
            writer.WriteNumber("cacheWrite", value.CacheWrite);
            writer.WriteNumber("reasoning", value.Reasoning);
            writer.WriteEndObject();
        }

        private static void WriteRange(Utf8JsonWriter writer, DateRange range)
        {
            writer.WriteStartObject();
            writer.WriteString("start", range.Start);
            writer.WriteString("end", range.End);
            writer.WriteEndObject();
        }

        private static HashSet<string> NewNames() => new(StringComparer.OrdinalIgnoreCase);
        private static bool IsYear(string value) => value.Length == 4 && value.All(c => c is >= '0' and <= '9');
        private readonly record struct QueryValue(string? Year);
    }

    private ref struct StrictReader
    {
        private Utf8JsonReader _reader;
        private int _tokens;
        private int _nestedItems;

        public StrictReader(ReadOnlySpan<byte> bytes)
        {
            _reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaxDepth,
            });
            _tokens = 0;
            _nestedItems = 0;
        }

        public void Begin() => Move();
        public void StartObject() => Require(JsonTokenType.StartObject);
        public void StartArray() => Require(JsonTokenType.StartArray);

        public bool NextProperty(HashSet<string> names, out string name)
        {
            Move();
            if (_reader.TokenType == JsonTokenType.EndObject)
            {
                name = string.Empty;
                return false;
            }
            if (_reader.TokenType != JsonTokenType.PropertyName) throw new SnapshotFormatException();
            name = ReadCurrentString();
            if (!names.Add(name)) throw new SnapshotFormatException();
            Move();
            return true;
        }

        public bool NextArrayItem()
        {
            Move();
            return _reader.TokenType != JsonTokenType.EndArray;
        }

        public string String()
        {
            if (_reader.TokenType != JsonTokenType.String) throw new SnapshotFormatException();
            return ReadCurrentString();
        }

        public string? NullableString()
        {
            if (_reader.TokenType == JsonTokenType.Null) return null;
            return String();
        }

        public bool IsNull() => _reader.TokenType == JsonTokenType.Null;

        public long Int64()
        {
            if (_reader.TokenType != JsonTokenType.Number || !_reader.TryGetInt64(out var value))
                throw new SnapshotFormatException();
            return value;
        }

        public int Int32()
        {
            if (_reader.TokenType != JsonTokenType.Number || !_reader.TryGetInt32(out var value))
                throw new SnapshotFormatException();
            return value;
        }

        public double Double()
        {
            if (_reader.TokenType != JsonTokenType.Number || !_reader.TryGetDouble(out var value) || !double.IsFinite(value))
                throw new SnapshotFormatException();
            return value;
        }

        public void AddNestedItem()
        {
            _nestedItems++;
            if (_nestedItems > MaxNestedItems) throw new SnapshotTooLargeException();
        }

        public void Finish()
        {
            if (_reader.Read()) throw new SnapshotFormatException();
        }

        private void Require(JsonTokenType type)
        {
            if (_reader.TokenType != type) throw new SnapshotFormatException();
        }

        private void Move()
        {
            if (!_reader.Read()) throw new SnapshotFormatException();
            _tokens++;
            if (_tokens > MaxTokens) throw new SnapshotTooLargeException();
        }

        private string ReadCurrentString()
        {
            var rawLength = _reader.HasValueSequence ? _reader.ValueSequence.Length : _reader.ValueSpan.Length;
            if (rawLength > MaxStringBytes) throw new SnapshotTooLargeException();
            var value = _reader.GetString() ?? throw new SnapshotFormatException();
            if (Utf8Length(value) > MaxStringBytes) throw new SnapshotTooLargeException();
            return value;
        }
    }

    private sealed class BoundedWriteStream(Stream inner, long limit) : Stream
    {
        private long _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count)
        {
            Ensure(count);
            inner.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Ensure(buffer.Length);
            inner.Write(buffer);
        }
        private void Ensure(int count)
        {
            if (_written + count > limit) throw new SnapshotTooLargeException();
            _written += count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private class SnapshotFormatException : Exception;
    private sealed class SnapshotSchemaException : SnapshotFormatException;
    private sealed class SnapshotTooLargeException : SnapshotFormatException;
}
