using TokenBar.Interop;

// Minimal end-to-end check of the Rust↔C# seam: load the cdylib, call
// tb_probe, free the buffer. The macOS counterpart is `swift run TokenBar --smoke`.
// TB_SMOKE_MIN_MESSAGES lets CI assert against a hermetic session fixture
// (HOME pointed at a synthetic ~/.claude/projects tree) instead of just "no crash".
var min = long.TryParse(
    Environment.GetEnvironmentVariable("TB_SMOKE_MIN_MESSAGES"), out var m) ? m : 0;
var probe = TbCore.Probe();
Console.WriteLine($"tb_probe -> ok={probe.Ok} messages={probe.Messages} (expected >= {min})");
return probe.Ok && probe.Messages >= min ? 0 : 1;
