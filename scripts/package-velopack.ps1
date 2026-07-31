#Requires -Version 7.0

<#
.SYNOPSIS
  Build and verify one private, unsigned Velopack release package.

.DESCRIPTION
  The command is intentionally fail-closed. It delegates App structure and
  version checks to build-app-artifact.ps1, restores the repository-owned vpk
  tool, packs one RID-specific channel, and verifies the resulting nuspec and
  application entry. It never signs, uploads, or publishes an artifact.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Rid,

    [Parameter(Mandatory = $true)]
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-RepoProperty {
    param(
        [Parameter(Mandatory = $true)]
        [xml]$Document,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    foreach ($group in @($Document.Project.PropertyGroup)) {
        $node = $group.SelectSingleNode($Name)
        if ($null -ne $node) {
            return $node.InnerText
        }
    }

    throw "Directory.Build.props is missing required property '$Name'."
}

function Read-PackProperties {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Version contract is missing: Directory.Build.props"
    }

    try {
        $document = [xml](Get-Content -LiteralPath $Path -Raw)
    }
    catch {
        throw "Directory.Build.props is not valid XML: $($_.Exception.Message)"
    }

    $product = Get-RepoProperty -Document $document -Name "TbProductName"
    $semantic = Get-RepoProperty -Document $document -Name "TbSemanticVersion"
    if ([string]::IsNullOrWhiteSpace($product) -or [string]::IsNullOrWhiteSpace($semantic)) {
        throw "Directory.Build.props packaging properties cannot be empty."
    }

    return [pscustomobject]@{
        ProductName = $product
        SemanticVersion = $semantic
    }
}

function Invoke-Captured {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage (exit code $LASTEXITCODE)."
    }
}

function Assert-NewOutputRoot {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "OutputRoot cannot be empty."
    }

    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath $resolved) {
        if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
            throw "OutputRoot is not a directory: $Path"
        }

        if (@(Get-ChildItem -LiteralPath $resolved -Force).Count -ne 0) {
            throw "OutputRoot must be an existing empty directory or a new path: $Path"
        }
    }
    else {
        New-Item -ItemType Directory -Path $resolved -Force | Out-Null
    }

    return $resolved
}

function Get-NuspecValue {
    param(
        [Parameter(Mandatory = $true)]
        [xml]$Document,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $path = "/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='{0}']" -f $Name
    $node = $Document.SelectSingleNode($path)
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw "Velopack nuspec metadata is missing '$Name'."
    }

    return $node.InnerText.Trim()
}

function Assert-VelopackPackage {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$PackId,
        [Parameter(Mandatory = $true)][string]$SemanticVersion,
        [Parameter(Mandatory = $true)][string]$MainExe,
        [Parameter(Mandatory = $true)][string]$Rid,
        [Parameter(Mandatory = $true)][string]$MachineArchitecture
    )

    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
        throw "Velopack package is missing: $PackagePath"
    }

    $archive = $null
    $reader = $null
    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
        $nuspecEntries = @($archive.Entries | Where-Object {
                -not $_.FullName.EndsWith("/") -and $_.FullName -match '(?i)\.nuspec$'
            })
        if ($nuspecEntries.Count -ne 1) {
            throw "Expected exactly one nuspec entry, found $($nuspecEntries.Count)."
        }

        $reader = [System.IO.StreamReader]::new($nuspecEntries[0].Open())
        $nuspecText = $reader.ReadToEnd()
        $document = [xml]$nuspecText
        $reader.Dispose()
        $reader = $null

        $expected = [ordered]@{
            id = $PackId
            version = $SemanticVersion
            mainExe = $MainExe
            machineArchitecture = $MachineArchitecture
            channel = $Rid
        }
        foreach ($item in $expected.GetEnumerator()) {
            $actual = Get-NuspecValue -Document $document -Name $item.Key
            if ($actual -cne [string]$item.Value) {
                throw "Velopack nuspec '$($item.Key)' mismatch: expected '$($item.Value)', got '$actual'."
            }
        }

        $appEntry = "lib/app/$MainExe"
        $matchingEntries = @($archive.Entries | Where-Object { $_.FullName -ceq $appEntry })
        if ($matchingEntries.Count -ne 1) {
            throw "Velopack package is missing required application entry '$appEntry'."
        }
    }
    catch {
        throw "Velopack package validation failed for '$PackagePath': $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
        if ($null -ne $archive) {
            $archive.Dispose()
        }
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$propsPath = Join-Path $repoRoot "Directory.Build.props"
$buildScript = Join-Path $repoRoot "scripts\build-app-artifact.ps1"
$outputRootResolved = Assert-NewOutputRoot -Path $OutputRoot
$packProperties = Read-PackProperties -Path $propsPath
# Keep the update identity stable across product renames so installed clients
# continue to find the same release lineage.
$packId = "Nyanako.TokenBar"
$appExecutableName = "{0}.App.exe" -f $packProperties.ProductName
$machineArchitecture = if ($Rid -eq "win-x64") { "x64" } else { "arm64" }

# Keep build artifacts separate from channel-scoped release files so the
# publish directory passed to vpk remains the one verified by the build step.
$buildOutputRoot = Join-Path $outputRootResolved "app-artifact"
# Called directly rather than through Invoke-Captured: that helper exists for
# native commands and checks $LASTEXITCODE, which a PowerShell script does not
# set. Splatting an argument array at a script also binds "-Rid" positionally
# instead of as a parameter name. This script throws on failure and
# $ErrorActionPreference is Stop, so a failure propagates on its own.
& $buildScript -Rid $Rid -OutputRoot $buildOutputRoot

$artifactName = "{0}-App-{1}-{2}" -f $packProperties.ProductName, $packProperties.SemanticVersion, $Rid
$publishRoot = Join-Path $buildOutputRoot (Join-Path $artifactName "publish")
if (-not (Test-Path -LiteralPath $publishRoot -PathType Container)) {
    throw "Verified publish directory is missing: $publishRoot"
}

$releasesRoot = Join-Path $outputRootResolved "releases"
New-Item -ItemType Directory -Path $releasesRoot -Force | Out-Null

Push-Location $repoRoot
try {
    Invoke-Captured -Command "dotnet" -Arguments @(
        "tool", "restore"
    ) -FailureMessage "Local .NET tool restore failed"

    Invoke-Captured -Command "dotnet" -Arguments @(
        "vpk", "pack",
        "--packId", $packId,
        "--packVersion", $packProperties.SemanticVersion,
        "--packDir", $publishRoot,
        "--mainExe", $appExecutableName,
        "--packTitle", $packProperties.ProductName,
        "--runtime", $Rid,
        "--channel", $Rid,
        "--outputDir", $releasesRoot
    ) -FailureMessage "Velopack pack failed"
}
finally {
    Pop-Location
}

$packageName = "{0}-{1}-{2}-full.nupkg" -f $packId, $packProperties.SemanticVersion, $Rid
$packagePath = Join-Path $releasesRoot $packageName
Assert-VelopackPackage -PackagePath $packagePath -PackId $packId `
    -SemanticVersion $packProperties.SemanticVersion -MainExe $appExecutableName `
    -Rid $Rid -MachineArchitecture $machineArchitecture

$releaseFiles = @(Get-ChildItem -LiteralPath $releasesRoot -File | Sort-Object Name)
Write-Output "Phase 11 Velopack package verified: $packageName"
Write-Output "Release files:"
foreach ($file in $releaseFiles) {
    Write-Output ("  {0} ({1} bytes)" -f $file.Name, $file.Length)
}
