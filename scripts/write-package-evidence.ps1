#Requires -Version 7.0
<#
.SYNOPSIS
  Emit sanitized Velopack package metadata (no binary upload).

.DESCRIPTION
  Scans a releases directory produced by package-velopack.ps1 and writes
  package-evidence.json with names, sizes, and SHA-256 hashes only.
  Does not copy Setup.exe or nupkg bytes anywhere.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleasesRoot,
    [Parameter(Mandatory = $true)][string]$OutputJson,
    [Parameter(Mandatory = $true)][ValidateSet("Full", "Lite")][string]$DeploymentMode,
    [Parameter(Mandatory = $true)][ValidateSet("win-x64", "win-arm64")][string]$Rid,
    [Parameter(Mandatory = $false)][string]$ExpectedFramework = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $ReleasesRoot -PathType Container)) {
    throw "Releases root missing: $ReleasesRoot"
}

$channel = if ($DeploymentMode -eq "Lite") { "$Rid-lite" } else { $Rid }
$files = @(Get-ChildItem -LiteralPath $ReleasesRoot -File | Sort-Object Name)
if ($files.Count -lt 1) {
    throw "No release files under $ReleasesRoot"
}

$entries = foreach ($file in $files) {
    [ordered]@{
        name = $file.Name
        bytes = [int64]$file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        kind = if ($file.Name -like "*.nupkg") { "nupkg" }
            elseif ($file.Name -like "*Setup.exe") { "setup" }
            elseif ($file.Name -like "*Portable.zip") { "portable" }
            elseif ($file.Name -like "RELEASES*") { "releases-index" }
            elseif ($file.Name -like "*.json") { "json" }
            else { "other" }
    }
}

$nupkg = @($entries | Where-Object { $_.kind -eq "nupkg" })
if ($nupkg.Count -ne 1) {
    throw "Expected exactly one nupkg in releases; found $($nupkg.Count)."
}

# Channel appears in the Velopack full package name: {id}-{ver}-{channel}-full.nupkg
if ($nupkg[0].name -notmatch [regex]::Escape("-$channel-full.nupkg$") -and
    $nupkg[0].name -notlike "*-$channel-full.nupkg") {
    throw "Nupkg name does not embed expected channel '$channel': $($nupkg[0].name)"
}

$maxPackageBytes = if ($DeploymentMode -eq "Lite") { 60MB } else { 110MB }
if ([int64]$nupkg[0].bytes -gt $maxPackageBytes) {
    throw "Nupkg exceeds size budget: $($nupkg[0].bytes) > $maxPackageBytes"
}

$evidence = [ordered]@{
    schema = "phase11-package-evidence.v1"
    deploymentMode = $DeploymentMode
    rid = $Rid
    channel = $channel
    expectedFramework = if ($ExpectedFramework) { $ExpectedFramework } else { $null }
    nupkgName = $nupkg[0].name
    nupkgBytes = [int64]$nupkg[0].bytes
    nupkgSha256 = $nupkg[0].sha256
    maxPackageBytesBudget = [int64]$maxPackageBytes
    files = @($entries)
}

$json = $evidence | ConvertTo-Json -Depth 6
if ($json -match '(?i)([A-Z]:[\\/]|/Users/|/home/|\\Users\\)') {
    throw "Package evidence contains an absolute path; refusing to write."
}

$outDir = Split-Path -Parent $OutputJson
if ($outDir -and -not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}
Set-Content -LiteralPath $OutputJson -Value $json -Encoding utf8
Write-Output "Wrote package evidence: $OutputJson"
