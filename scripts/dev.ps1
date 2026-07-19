# Windows-side one-shot: build the explicit Rust target, then the managed
# solution, WinUI app, tests, and P/Invoke smoke. Prereqs: Rust (MSVC
# toolchain) + .NET 10 SDK.
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

# Directory.Build.targets owns the Platform/RID to Rust-target mapping.
dotnet msbuild src/TokenBar.Smoke/TokenBar.Smoke.csproj -t:BuildTbNative -p:Configuration=Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build src/TokenBar.Core.Tests/TokenBar.Core.Tests.csproj -c Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test src/TokenBar.Core.Tests/TokenBar.Core.Tests.csproj -c Release -p:Platform=x64 --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build src/TokenBar.App/TokenBar.App.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build src/TokenBar.Smoke/TokenBar.Smoke.csproj -c Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project src/TokenBar.Smoke -c Release -p:Platform=x64 --no-build
exit $LASTEXITCODE
