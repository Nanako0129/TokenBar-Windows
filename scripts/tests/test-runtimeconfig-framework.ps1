#Requires -Version 7.0
# Unit test for scripts/lib/RuntimeConfig.ps1 (shipped helper, real path).
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$here = $PSScriptRoot
. (Join-Path $here "..\lib\RuntimeConfig.ps1")

function Assert-Throws {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Script,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$MustContain
    )
    $threw = $false
    try {
        & $Script | Out-Null
    }
    catch {
        $threw = $true
        $msg = [string]$_.Exception.Message
        if ($msg -notlike "*$MustContain*") {
            throw ("{0}: expected error containing '{1}', got: {2}" -f $Label, $MustContain, $msg)
        }
    }
    if (-not $threw) {
        throw ("{0}: expected throw containing '{1}'" -f $Label, $MustContain)
    }
}

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
    "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.9" }
  }
}
'@ | Set-Content -LiteralPath $runtimeOnly -Encoding utf8

    $wrongMajor = Join-Path $scratch "net11.runtimeconfig.json"
    @'
{
  "runtimeOptions": {
    "tfm": "net11.0",
    "framework": { "name": "Microsoft.NETCore.App", "version": "11.0.0" }
  }
}
'@ | Set-Content -LiteralPath $wrongMajor -Encoding utf8

    $extraFramework = Join-Path $scratch "extra.runtimeconfig.json"
    @'
{
  "runtimeOptions": {
    "tfm": "net10.0",
    "frameworks": [
      { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
      { "name": "Microsoft.AspNetCore.App", "version": "10.0.0" }
    ]
  }
}
'@ | Set-Content -LiteralPath $extraFramework -Encoding utf8

    $missingVersion = Join-Path $scratch "no-version.runtimeconfig.json"
    @'
{
  "runtimeOptions": {
    "framework": { "name": "Microsoft.NETCore.App" }
  }
}
'@ | Set-Content -LiteralPath $missingVersion -Encoding utf8

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

    Assert-Throws -Label "wrong major" -MustContain "not .NET 10.x" -Script {
        Get-RuntimeConfigFrameworkFamily -Path $wrongMajor
    }
    Assert-Throws -Label "unexpected framework" -MustContain "unexpected framework" -Script {
        Get-RuntimeConfigFrameworkFamily -Path $extraFramework
    }
    Assert-Throws -Label "missing version" -MustContain "missing a version" -Script {
        Get-RuntimeConfigFrameworkFamily -Path $missingVersion
    }

    Write-Output "PASS runtimeconfig framework helpers"
    exit 0
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
