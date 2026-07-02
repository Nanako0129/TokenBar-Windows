using TokenBar.Interop;

// Minimal end-to-end check of the Rust↔C# seam: load the cdylib, call
// tb_probe, free the buffer. The macOS counterpart is `swift run TokenBar --smoke`.
// TB_SMOKE_MIN_MESSAGES lets CI assert against a hermetic session fixture
// (HOME pointed at a synthetic ~/.claude/projects tree) instead of just "no crash".
var minRaw = Environment.GetEnvironmentVariable("TB_SMOKE_MIN_MESSAGES");
long min = 0;
if (!string.IsNullOrEmpty(minRaw) && !long.TryParse(minRaw, out min))
{
    // A set-but-unparseable expectation must fail loudly, not silently
    // degrade the hermetic assertion to "no crash".
    Console.Error.WriteLine($"TB_SMOKE_MIN_MESSAGES is not a number: '{minRaw}'");
    return 2;
}
var probe = TbCore.Probe();
Console.WriteLine($"tb_probe -> ok={probe.Ok} messages={probe.Messages} (expected >= {min})");
return probe.Ok && probe.Messages >= min ? 0 : 1;
