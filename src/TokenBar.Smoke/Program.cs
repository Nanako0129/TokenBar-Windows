using TokenBar.Interop;

// Minimal end-to-end check of the Rust↔C# seam: load the cdylib, call
// tb_probe, free the buffer. The macOS counterpart is `swift run TokenBar --smoke`.
var probe = TbCore.Probe();
Console.WriteLine($"tb_probe -> ok={probe.Ok} messages={probe.Messages}");
return probe.Ok ? 0 : 1;
