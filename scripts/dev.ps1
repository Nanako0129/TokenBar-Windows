# Windows-side one-shot: build Rust core + .NET solution, run tests and the
# P/Invoke smoke. Prereqs: Rust (MSVC toolchain) + .NET 10 SDK.
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

cargo build --release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build src/TokenBar.slnx -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test src/TokenBar.slnx -c Release --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project src/TokenBar.Smoke -c Release --no-build
exit $LASTEXITCODE
