using System.Globalization;
using System.Text.Json;
using TokenBar.Core;
using TokenBar.Interop;

// C# side of the Swift↔C# fixture cross-check. Contract: crosscheck/README.md.
// Reads <fixturesDir>/{usage-pace,format}.json, writes <outDir>/*.actual.json.

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: TokenBar.CrossCheck <fixtures-dir> <out-dir>");
    return 2;
}

// The today*/relativeTime cases are local-tz dependent; the contract pins the
// run to Asia/Taipei so both harnesses agree.
if (TimeZoneInfo.Local.Id != "Asia/Taipei")
{
    Console.Error.WriteLine($"TZ must be Asia/Taipei (got '{TimeZoneInfo.Local.Id}') — run with TZ=Asia/Taipei");
    return 2;
}

var fixturesDir = args[0];
var outDir = args[1];
Directory.CreateDirectory(outDir);

// Same options TbCore uses for the wire: Rust serde camelCase.
var wire = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var outOpts = new JsonSerializerOptions { WriteIndented = true };

WriteResults("usage-pace", RunUsagePace(Load("usage-pace")));
WriteResults("format", RunFormat(Load("format")));
return 0;

JsonDocument Load(string name) =>
    JsonDocument.Parse(File.ReadAllText(Path.Combine(fixturesDir, name + ".json")));

void WriteResults(string name, Dictionary<string, object?> results) =>
    File.WriteAllText(
        Path.Combine(outDir, name + ".actual.json"),
        JsonSerializer.Serialize(results, outOpts));

// RFC3339 `now` — harness-side parse, strict invariant.
static DateTimeOffset ParseNow(JsonElement c) =>
    DateTimeOffset.Parse(
        c.GetProperty("now").GetString()!,
        CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind);

static string StageName(PaceStage stage)
{
    var s = stage.ToString();
    return char.ToLowerInvariant(s[0]) + s[1..];
}

Dictionary<string, object?> RunUsagePace(JsonDocument doc)
{
    var results = new Dictionary<string, object?>();
    foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
    {
        var name = c.GetProperty("name").GetString()!;
        var window = c.GetProperty("window").Deserialize<UsageWindow>(wire)!;
        switch (c.GetProperty("kind").GetString())
        {
            case "runOutRisk":
                results[name] = UsagePace.RunOutRiskLabel(window);
                break;
            case "compute":
                var mode = c.GetProperty("mode").GetString() switch
                {
                    "linear" => PaceMode.Linear,
                    "historical" => PaceMode.Historical,
                    "off" => PaceMode.Off,
                    var m => throw new InvalidOperationException($"{name}: unknown mode '{m}'"),
                };
                var pace = UsagePace.Compute(window, mode, ParseNow(c));
                results[name] = pace is null ? null : new Dictionary<string, object?>
                {
                    ["stage"] = StageName(pace.Stage),
                    ["deltaPercent"] = pace.DeltaPercent,
                    ["expectedUsedPercent"] = pace.ExpectedUsedPercent,
                    ["actualUsedPercent"] = pace.ActualUsedPercent,
                    ["etaSeconds"] = pace.EtaSeconds,
                    ["willLastToReset"] = pace.WillLastToReset,
                    ["label"] = pace.Label,
                    ["etaText"] = pace.EtaText,
                };
                break;
            case var k:
                throw new InvalidOperationException($"{name}: unknown kind '{k}'");
        }
    }

    return results;
}

Dictionary<string, object?> RunFormat(JsonDocument doc)
{
    var graph = doc.RootElement.GetProperty("graph").Deserialize<UsagePayload>(wire)!;
    var results = new Dictionary<string, object?>();
    foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
    {
        var name = c.GetProperty("name").GetString()!;
        // today* take a local wall-clock DateTime in production; mirror that.
        DateTime LocalNow() => ParseNow(c).ToLocalTime().DateTime;
        results[name] = c.GetProperty("fn").GetString() switch
        {
            "compactTokens" => Format.CompactTokens(c.GetProperty("arg").GetInt64()),
            "exactTokens" => Format.ExactTokens(c.GetProperty("arg").GetInt64()),
            "usd" => Format.Usd(c.GetProperty("arg").GetDouble()),
            "monthDay" => Format.MonthDay(c.GetProperty("arg").GetString()!),
            "mmdd" => Format.Mmdd(c.GetProperty("arg").GetString()!),
            "relativeTime" => Format.RelativeTime(c.GetProperty("arg").GetUInt64(), ParseNow(c)),
            "todayKey" => Format.TodayKey(LocalNow()),
            "todayTokens" => Format.TodayTokens(graph, LocalNow()),
            "todayCost" => Format.TodayCost(graph, LocalNow()),
            "paceDurationText" => UsagePace.DurationText(c.GetProperty("arg").GetDouble()),
            var fn => throw new InvalidOperationException($"{name}: unknown fn '{fn}'"),
        };
    }

    return results;
}
