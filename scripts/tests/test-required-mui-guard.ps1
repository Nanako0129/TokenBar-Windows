#Requires -Version 7.0
<#
.SYNOPSIS
  Drives the production Assert-RequiredDesktopMuiLocales from scripts/lib/MuiGuard.ps1.
  Proves Phone-only and zero-byte desktop MUI trees fail; a healthy tree passes.
#>
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$lib = Join-Path $PSScriptRoot "..\lib\MuiGuard.ps1"
if (-not (Test-Path -LiteralPath $lib -PathType Leaf)) {
    throw "Missing production MUI guard: $lib"
}
. $lib

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("tb-mui-guard-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $zh = Join-Path $root "zh-TW"
    $ja = Join-Path $root "ja-JP"
    New-Item -ItemType Directory -Path $zh, $ja | Out-Null

    $zhDesktop = Join-Path $zh "Microsoft.ui.xaml.dll.mui"
    $zhPhone = Join-Path $zh "Microsoft.UI.Xaml.Phone.dll.mui"
    $jaDesktop = Join-Path $ja "Microsoft.ui.xaml.dll.mui"
    $jaPhone = Join-Path $ja "Microsoft.UI.Xaml.Phone.dll.mui"

    Set-Content -LiteralPath $zhDesktop -Value "desktop-zh" -Encoding ascii
    Set-Content -LiteralPath $zhPhone -Value "phone-zh" -Encoding ascii
    Set-Content -LiteralPath $jaDesktop -Value "desktop-ja" -Encoding ascii
    Set-Content -LiteralPath $jaPhone -Value "phone-ja" -Encoding ascii

    function Invoke-Guard {
        $mui = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter "*.mui")
        Assert-RequiredDesktopMuiLocales `
            -PublishRoot $root `
            -RequiredLocales @("zh-TW", "ja-JP") `
            -MuiFiles $mui
    }

    # Positive: non-empty desktop MUI for each locale.
    Invoke-Guard

    # Negative 1: Phone-only for zh-TW must fail via production function.
    Remove-Item -LiteralPath $zhDesktop -Force
    $failed = $false
    try { Invoke-Guard } catch {
        $failed = $true
        if ("$_" -notmatch "zh-TW/Microsoft\.ui\.xaml\.dll\.mui") {
            throw "Phone-only failure missing exact desktop path: $_"
        }
    }
    if (-not $failed) {
        throw "Production guard passed with only Phone .mui present."
    }

    # Restore desktop as zero-byte file — must still fail (Length > 0 required).
    New-Item -ItemType File -Path $zhDesktop -Force | Out-Null
    if ((Get-Item -LiteralPath $zhDesktop).Length -ne 0) {
        throw "expected zero-byte desktop MUI for mutation"
    }
    $failed = $false
    try { Invoke-Guard } catch {
        $failed = $true
        if ("$_" -notmatch "zh-TW/Microsoft\.ui\.xaml\.dll\.mui") {
            throw "Zero-byte failure missing exact desktop path: $_"
        }
    }
    if (-not $failed) {
        throw "Production guard passed with zero-byte desktop .mui."
    }

    Write-Output "PASS production MuiGuard (positive + Phone-only + zero-byte negatives)"
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
