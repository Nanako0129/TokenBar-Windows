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

function Get-NuspecElements {
    param(
        [Parameter(Mandatory = $true)][xml]$Document,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $path = "/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='{0}']" -f $Name
    return @($Document.SelectNodes($path))
}

function Get-RequiredNuspecValue {
    param(
        [Parameter(Mandatory = $true)][xml]$Document,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $nodes = @(Get-NuspecElements -Document $Document -Name $Name)
    if ($nodes.Count -ne 1 -or [string]::IsNullOrWhiteSpace($nodes[0].InnerText)) {
        throw "Velopack nuspec must contain exactly one non-empty '$Name' element."
    }

    return $nodes[0].InnerText.Trim()
}

if (-not (Test-Path -LiteralPath $ReleasesRoot -PathType Container)) {
    throw "Releases root missing: $ReleasesRoot"
}

$channel = if ($DeploymentMode -eq "Lite") { "$Rid-lite" } else { $Rid }
$architecture = if ($Rid -eq "win-x64") { "x64" } else { "arm64" }
$requiredLiteFramework = if ($Rid -eq "win-x64") {
    "net10-x64-runtime"
} else {
    "net10-arm64-runtime"
}
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
if ($nupkg.name -cnotlike "*-$channel-full.nupkg") {
    throw "Nupkg name does not embed expected channel '$channel': $($nupkg.name)"
}
if ($setup.name -cnotlike "*-$channel-Setup.exe") {
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

    $nuspecChannel = Get-RequiredNuspecValue -Document $document -Name "channel"
    $nuspecArchitecture = Get-RequiredNuspecValue -Document $document -Name "machineArchitecture"
    if ($nuspecChannel -cne $channel) {
        throw "Nuspec channel mismatch: expected '$channel', got '$nuspecChannel'."
    }
    if ($nuspecArchitecture -cne $architecture) {
        throw "Nuspec architecture mismatch: expected '$architecture', got '$nuspecArchitecture'."
    }

    $runtimeNodes = @(Get-NuspecElements -Document $document -Name "runtimeDependencies")
    if ($DeploymentMode -eq "Lite") {
        if ($ExpectedFramework -cne $requiredLiteFramework) {
            throw "Lite framework must be '$requiredLiteFramework' (runtime family), got '$ExpectedFramework'."
        }
        if ($runtimeNodes.Count -ne 1 -or
            [string]::IsNullOrWhiteSpace($runtimeNodes[0].InnerText)) {
            throw "Lite package must contain exactly one non-empty runtimeDependencies element."
        }
        $runtimeDependency = $runtimeNodes[0].InnerText.Trim()
        if ($runtimeDependency -cne $requiredLiteFramework) {
            throw "Lite runtime dependency mismatch: expected '$requiredLiteFramework', got '$runtimeDependency'."
        }
    }
    else {
        if (-not [string]::IsNullOrWhiteSpace($ExpectedFramework)) {
            throw "Full package evidence must not be given a Lite framework prerequisite."
        }
        if ($runtimeNodes.Count -ne 0) {
            throw "Full package must omit runtimeDependencies entirely; found $($runtimeNodes.Count) element(s)."
        }
        $runtimeDependency = ""
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
    expectedFramework = if ($ExpectedFramework) { $ExpectedFramework } else { $null }
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
