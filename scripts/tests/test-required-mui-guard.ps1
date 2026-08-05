#Requires -Version 7.0
<#
.SYNOPSIS
  Proves the required-desktop-MUI guard fails when only Phone .mui survives.
#>
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $pathFull = [System.IO.Path]::GetFullPath($Path)
    if (-not $pathFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$Path' is not under root '$Root'."
    }
    return $pathFull.Substring($rootFull.Length)
}

function Test-RequiredDesktopMuiPresent {
    param(
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][string[]]$RequiredLocales
    )

    $muiFiles = @(Get-ChildItem -LiteralPath $PublishRoot -Recurse -File -Filter "*.mui" -ErrorAction SilentlyContinue)
    $requiredDesktopMuiFileName = "Microsoft.ui.xaml.dll.mui"
    $missingLocales = [System.Collections.Generic.List[string]]::new()
    foreach ($locale in $RequiredLocales) {
        $expectedRel = Join-Path $locale $requiredDesktopMuiFileName
        $matched = $false
        foreach ($mui in $muiFiles) {
            $rel = Get-RelativePath -Root $PublishRoot -Path $mui.FullName
            $normRel = $rel.Replace('/', '\')
            $normExpected = $expectedRel.Replace('/', '\')
            if ([string]::Equals($normRel, $normExpected, [StringComparison]::OrdinalIgnoreCase)) {
                $matched = $true
                break
            }
        }
        if (-not $matched) {
            [void]$missingLocales.Add(("{0}/{1}" -f $locale, $requiredDesktopMuiFileName))
        }
    }
    if ($missingLocales.Count -gt 0) {
        throw ("Required desktop MUI file(s) missing from publish: {0}" -f ($missingLocales -join ", "))
    }
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("tb-mui-guard-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    # Positive: both locales have desktop MUI.
    $zh = Join-Path $root "zh-TW"
    $ja = Join-Path $root "ja-JP"
    New-Item -ItemType Directory -Path $zh, $ja | Out-Null
    Set-Content -LiteralPath (Join-Path $zh "Microsoft.ui.xaml.dll.mui") -Value "desktop" -Encoding ascii
    Set-Content -LiteralPath (Join-Path $zh "Microsoft.UI.Xaml.Phone.dll.mui") -Value "phone" -Encoding ascii
    Set-Content -LiteralPath (Join-Path $ja "Microsoft.ui.xaml.dll.mui") -Value "desktop" -Encoding ascii
    Set-Content -LiteralPath (Join-Path $ja "Microsoft.UI.Xaml.Phone.dll.mui") -Value "phone" -Encoding ascii
    Test-RequiredDesktopMuiPresent -PublishRoot $root -RequiredLocales @("zh-TW", "ja-JP")

    # Negative: remove desktop MUI for zh-TW, leave Phone only — guard must fail.
    Remove-Item -LiteralPath (Join-Path $zh "Microsoft.ui.xaml.dll.mui") -Force
    $failed = $false
    try {
        Test-RequiredDesktopMuiPresent -PublishRoot $root -RequiredLocales @("zh-TW", "ja-JP")
    }
    catch {
        $failed = $true
        $msg = "$_"
        if ($msg -notmatch "zh-TW/Microsoft\.ui\.xaml\.dll\.mui") {
            throw "Negative guard throw missing exact desktop path: $msg"
        }
    }
    if (-not $failed) {
        throw "Guard passed with only Phone .mui present; expected failure."
    }

    Write-Output "PASS required-desktop-MUI guard (positive + Phone-only negative)"
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
