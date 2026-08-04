#Requires -Version 7.0
<#
.SYNOPSIS
  Verify a Velopack release directory and emit sanitized package metadata.

.DESCRIPTION
  Re-opens the generated nupkg, validates the deployment-mode/channel/runtime
  contract, and writes names, sizes, and SHA-256 hashes only. It never copies
  Setup.exe, nupkg, or other release bytes to an artifact staging directory.
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

function Get-NuspecElement {
    param(
        [Parameter(Mandatory = $true)][xml]$Document,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $false)][switch]$Optional
    )

    $path = "/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='{0}']" -f $Name
    $nodes = @($Document.SelectNodes($path))
    if ($nodes.Count -eq 0 -and $Optional) {
        return $null
    }
    if ($nodes.Count -ne 1) {
        throw "Velopack nuspec must contain exactly one '$Name' element; found $($nodes.Count)."
    }

    return $nodes[0]
}

if (-not (Test-Path -LiteralPath $ReleasesRoot -PathType Container)) {
    throw "Releases root missing: $ReleasesRoot"
}

$channel = if ($DeploymentMode -eq "Lite") { "$Rid-lite" } else { $Rid }
$architecture = if ($Rid -eq "win-x64") { "x64" } else { "arm64" }
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

$nupkgEntries = @($entries | Where-Object { $_.kind -eq "nupkg" })
$setupEntries = @($entries | Where-Object { $_.kind -eq "setup" })
if ($nupkgEntries.Count -ne 1) {
    throw "Expected exactly one nupkg in releases; found $($nupkgEntries.Count)."
}
if ($setupEntries.Count -ne 1) {
    throw "Expected exactly one Setup.exe in releases; found $($setupEntries.Count)."
}

$nupkg = $nupkgEntries[0]
$setup = $setupEntries[0]
if ($nupkg.name -notlike "*-$channel-full.nupkg") {
    throw "Nupkg name does not embed expected channel '$channel': $($nupkg.name)"
}
if ($setup.name -notlike "*-$channel-Setup.exe") {
    throw "Setup name does not embed expected channel '$channel': $($setup.name)"
}

$maxPackageBytes = if ($DeploymentMode -eq "Lite") { 60MB } else { 110MB }
$maxSetupBytes = if ($DeploymentMode -eq "Lite") { 75MB } else { 125MB }
if ([int64]$nupkg.bytes -gt $maxPackageBytes) {
    throw "Nupkg exceeds size budget: $($nupkg.bytes) > $maxPackageBytes"
}
if ([int64]$setup.bytes -gt $maxSetupBytes) {
    throw "Setup exceeds size budget: $($setup.bytes) > $maxSetupBytes"
}

$nupkgPath = Join-Path $ReleasesRoot $nupkg.name
$archive = $null
$reader = $null
try {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($nupkgPath)
    $nuspecEntries = @($archive.Entries | Where-Object {
            -not $_.FullName.EndsWith("/") -and $_.FullName -match '(?i)\.nuspec$'
        })
    if ($nuspecEntries.Count -ne 1) {
        throw "Expected exactly one nuspec in nupkg; found $($nuspecEntries.Count)."
    }

    $reader = [System.IO.StreamReader]::new($nuspecEntries[0].Open())
    $document = [xml]$reader.ReadToEnd()
    $reader.Dispose()
    $reader = $null

    $nuspecChannel = (Get-NuspecElement -Document $document -Name "channel").InnerText.Trim()
    $nuspecArchitecture = (Get-NuspecElement -Document $document -Name "machineArchitecture").InnerText.Trim()
    if ($nuspecChannel -cne $channel) {
        throw "Nuspec channel mismatch: expected '$channel', got '$nuspecChannel'."
    }
    if ($nuspecArchitecture -cne $architecture) {
        throw "Nuspec architecture mismatch: expected '$architecture', got '$nuspecArchitecture'."
    }

    $runtimeNode = Get-NuspecElement -Document $document -Name "runtimeDependencies" -Optional
    $runtimeDependency = if ($null -eq $runtimeNode) { "" } else { $runtimeNode.InnerText.Trim() }
    if ($DeploymentMode -eq "Lite") {
        if ([string]::IsNullOrWhiteSpace($ExpectedFramework)) {
            throw "Lite package evidence requires ExpectedFramework."
        }
        if ($runtimeDependency -cne $ExpectedFramework) {
            throw "Lite runtime dependency mismatch: expected '$ExpectedFramework', got '$runtimeDependency'."
        }
    }
    else {
        if (-not [string]::IsNullOrWhiteSpace($ExpectedFramework)) {
            throw "Full package evidence must not be given a Lite framework prerequisite."
        }
        if (-not [string]::IsNullOrWhiteSpace($runtimeDependency)) {
            throw "Full package must not contain runtimeDependencies; got '$runtimeDependency'."
        }
    }
}
finally {
    if ($null -ne $reader) {
        $reader.Dispose()
    }
    if ($null -ne $archive) {
        $archive.Dispose()
    }
}

$evidence = [ordered]@{
    schema = "phase11-package-evidence.v2"
    deploymentMode = $DeploymentMode
    rid = $Rid
    architecture = $architecture
    channel = $channel
    runtimeDependency = if ($runtimeDependency) { $runtimeDependency } else { $null }
    nupkgName = $nupkg.name
    nupkgBytes = [int64]$nupkg.bytes
    nupkgSha256 = $nupkg.sha256
    setupName = $setup.name
    setupBytes = [int64]$setup.bytes
    setupSha256 = $setup.sha256
    maxPackageBytesBudget = [int64]$maxPackageBytes
    maxSetupBytesBudget = [int64]$maxSetupBytes
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
