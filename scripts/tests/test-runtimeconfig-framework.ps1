#Requires -Version 7.0
# Unit test for scripts/lib/RuntimeConfig.ps1 (shipped helper, real path).
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$here = $PSScriptRoot
. (Join-Path $here "..\lib\RuntimeConfig.ps1")

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("tb-runtimeconfig-" + [guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
try {
    $desktop = Join-Path $scratch "desktop.runtimeconfig.json"
    @'
{
  "runtimeOptions": {
    "tfm": "net10.0",
    "frameworks": [
      { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
      { "name": "Microsoft.WindowsDesktop.App", "version": "10.0.0" }
    ]
  }
}
'@ | Set-Content -LiteralPath $desktop -Encoding utf8

    $runtimeOnly = Join-Path $scratch "runtime.runtimeconfig.json"
    @'
{
  "runtimeOptions": {
    "tfm": "net10.0",
    "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" }
  }
}
'@ | Set-Content -LiteralPath $runtimeOnly -Encoding utf8

    $familyDesktop = Get-RuntimeConfigFrameworkFamily -Path $desktop
    if ($familyDesktop -cne "desktop") {
        throw "expected desktop, got $familyDesktop"
    }
    $specDesktop = Get-VelopackFrameworkSpecFromFamily -Family $familyDesktop -Rid "win-x64"
    if ($specDesktop -cne "net10-x64-desktop") {
        throw "expected net10-x64-desktop, got $specDesktop"
    }

    $familyRuntime = Get-RuntimeConfigFrameworkFamily -Path $runtimeOnly
    if ($familyRuntime -cne "runtime") {
        throw "expected runtime, got $familyRuntime"
    }
    $specArm = Get-VelopackFrameworkSpecFromFamily -Family $familyRuntime -Rid "win-arm64"
    if ($specArm -cne "net10-arm64-runtime") {
        throw "expected net10-arm64-runtime, got $specArm"
    }

    Write-Output "PASS runtimeconfig framework helpers"
    exit 0
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
