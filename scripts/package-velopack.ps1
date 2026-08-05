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
    [string]$OutputRoot,

    [Parameter(Mandatory = $false)]
    [ValidateSet("Full", "Lite")]
    [string]$DeploymentMode = "Full"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "lib\RuntimeConfig.ps1")

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

function Get-NuspecNodes {
    param(
        [Parameter(Mandatory = $true)][xml]$Document,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $path = "/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='{0}']" -f $Name
    return @($Document.SelectNodes($path))
}

function Get-NuspecValue {
    param(
        [Parameter(Mandatory = $true)][xml]$Document,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $nodes = @(Get-NuspecNodes -Document $Document -Name $Name)
    if ($nodes.Count -ne 1 -or [string]::IsNullOrWhiteSpace($nodes[0].InnerText)) {
        throw "Velopack nuspec metadata must contain exactly one non-empty '$Name' element."
    }

    return $nodes[0].InnerText.Trim()
}

function Get-VelopackFrameworkSpec {
    param(
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][string]$AppAssemblyName,
        [Parameter(Mandatory = $true)][string]$Rid
    )

    $runtimeConfigPath = Join-Path $PublishRoot ("{0}.runtimeconfig.json" -f $AppAssemblyName)
    $family = Get-RuntimeConfigFrameworkFamily -Path $runtimeConfigPath
    return Get-VelopackFrameworkSpecFromFamily -Family $family -Rid $Rid
}

function Assert-VelopackPackage {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$PackId,
        [Parameter(Mandatory = $true)][string]$ProductName,
        [Parameter(Mandatory = $true)][string]$SemanticVersion,
        [Parameter(Mandatory = $true)][string]$MainExe,
        [Parameter(Mandatory = $true)][string]$MachineArchitecture,
        [Parameter(Mandatory = $true)][string]$Channel,
        [Parameter(Mandatory = $true)][string]$DeploymentMode,
        [Parameter(Mandatory = $false)][string]$ExpectedRuntimeDependency = ""
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
            title = $ProductName
            version = $SemanticVersion
            mainExe = $MainExe
            machineArchitecture = $MachineArchitecture
            channel = $Channel
        }
        foreach ($item in $expected.GetEnumerator()) {
            $actual = Get-NuspecValue -Document $document -Name $item.Key
            if ($actual -cne [string]$item.Value) {
                throw "Velopack nuspec '$($item.Key)' mismatch: expected '$($item.Value)', got '$actual'."
            }
        }

        $runtimeNodes = @(Get-NuspecNodes -Document $document -Name "runtimeDependencies")
        if ($DeploymentMode -eq "Lite") {
            if ([string]::IsNullOrWhiteSpace($ExpectedRuntimeDependency)) {
                throw "Lite package validation requires ExpectedRuntimeDependency from runtimeconfig.json."
            }
            if ($runtimeNodes.Count -ne 1 -or
                [string]::IsNullOrWhiteSpace($runtimeNodes[0].InnerText)) {
                throw "Lite package must contain exactly one non-empty runtimeDependencies element."
            }
            $runtimeDependency = $runtimeNodes[0].InnerText.Trim()
            if ($runtimeDependency -cne $ExpectedRuntimeDependency) {
                throw "Velopack nuspec 'runtimeDependencies' mismatch: expected '$ExpectedRuntimeDependency', got '$runtimeDependency'."
            }
        }
        else {
            if (-not [string]::IsNullOrWhiteSpace($ExpectedRuntimeDependency)) {
                throw "Full package validation must not receive a Lite runtime prerequisite."
            }
            if ($runtimeNodes.Count -ne 0) {
                throw "Full package must omit runtimeDependencies entirely; found $($runtimeNodes.Count) element(s)."
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
$packId = "Nyanako.Syrtis"
$appExecutableName = "{0}.App.exe" -f $packProperties.ProductName
$machineArchitecture = if ($Rid -eq "win-x64") { "x64" } else { "arm64" }
$requiredLiteFramework = if ($Rid -eq "win-x64") {
    "net10-x64-runtime"
} else {
    "net10-arm64-runtime"
}
$channel = if ($DeploymentMode -eq "Lite") { "$Rid-lite" } else { $Rid }

# Keep build artifacts separate from channel-scoped release files so the
# publish directory passed to vpk remains the one verified by the build step.
$buildOutputRoot = Join-Path $outputRootResolved "app-artifact"
# Called directly rather than through Invoke-Captured: that helper exists for
# native commands and checks $LASTEXITCODE, which a PowerShell script does not
# set. Splatting an argument array at a script also binds "-Rid" positionally
# instead of as a parameter name. This script throws on failure and
# $ErrorActionPreference is Stop, so a failure propagates on its own.
& $buildScript -Rid $Rid -OutputRoot $buildOutputRoot -DeploymentMode $DeploymentMode

$artifactName = "{0}-App-{1}-{2}" -f $packProperties.ProductName, $packProperties.SemanticVersion, $Rid
$publishRoot = Join-Path $buildOutputRoot (Join-Path $artifactName "publish")
if (-not (Test-Path -LiteralPath $publishRoot -PathType Container)) {
    throw "Verified publish directory is missing: $publishRoot"
}

$appAssemblyName = "{0}.App" -f $packProperties.ProductName
$frameworkSpec = ""
if ($DeploymentMode -eq "Lite") {
    $frameworkSpec = Get-VelopackFrameworkSpec `
        -PublishRoot $publishRoot `
        -AppAssemblyName $appAssemblyName `
        -Rid $Rid
    if ($frameworkSpec -cne $requiredLiteFramework) {
        throw "Lite runtimeconfig must resolve to '$requiredLiteFramework', got '$frameworkSpec'."
    }
    Write-Output "Lite Velopack framework from runtimeconfig.json: $frameworkSpec"
}

$releasesRoot = Join-Path $outputRootResolved "releases"
New-Item -ItemType Directory -Path $releasesRoot -Force | Out-Null

Push-Location $repoRoot
try {
    Invoke-Captured -Command "dotnet" -Arguments @(
        "tool", "restore"
    ) -FailureMessage "Local .NET tool restore failed"

    $vpkArgs = @(
        "vpk", "pack",
        "--packId", $packId,
        "--packVersion", $packProperties.SemanticVersion,
        "--packDir", $publishRoot,
        "--mainExe", $appExecutableName,
        "--packTitle", $packProperties.ProductName,
        "--runtime", $Rid,
        "--channel", $channel,
        "--outputDir", $releasesRoot
    )
    if ($DeploymentMode -eq "Lite") {
        $vpkArgs += @("--framework", $frameworkSpec)
    }

    Invoke-Captured -Command "dotnet" -Arguments $vpkArgs -FailureMessage "Velopack pack failed"
}
finally {
    Pop-Location
}

$packageName = "{0}-{1}-{2}-full.nupkg" -f $packId, $packProperties.SemanticVersion, $channel
$packagePath = Join-Path $releasesRoot $packageName
Assert-VelopackPackage -PackagePath $packagePath -PackId $packId `
    -ProductName $packProperties.ProductName `
    -SemanticVersion $packProperties.SemanticVersion -MainExe $appExecutableName `
    -MachineArchitecture $machineArchitecture -Channel $channel `
    -DeploymentMode $DeploymentMode -ExpectedRuntimeDependency $frameworkSpec

$releaseFiles = @(Get-ChildItem -LiteralPath $releasesRoot -File | Sort-Object Name)
Write-Output "Phase 11 Velopack package verified: $packageName"
Write-Output "Release files:"
foreach ($file in $releaseFiles) {
    Write-Output ("  {0} ({1} bytes)" -f $file.Name, $file.Length)
}

# Measured per-mode/per-RID size budgets for nupkg and Setup.exe (bytes).
# ~5% above clean pinned measurements; increase requires explicit source edit.
$nupkgSetupBudgetByModeRid = @{
    "Full|win-x64|nupkg"   = [int64]130000000
    "Full|win-x64|setup"   = [int64]140000000
    "Full|win-arm64|nupkg" = [int64]130000000
    "Full|win-arm64|setup" = [int64]140000000
    "Lite|win-x64|nupkg"   = [int64]80000000
    "Lite|win-x64|setup"   = [int64]90000000
    "Lite|win-arm64|nupkg" = [int64]80000000
    "Lite|win-arm64|setup" = [int64]90000000
}
function Assert-SizeBudget {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][int64]$Measured,
        [Parameter(Mandatory = $true)][int64]$Budget
    )
    if ($Measured -gt $Budget) {
        $over = $Measured - $Budget
        $measuredMiB = [math]::Round($Measured / 1MB, 3)
        $budgetMiB = [math]::Round($Budget / 1MB, 3)
        $overMiB = [math]::Round($over / 1MB, 3)
        throw ("{0} exceeds size budget for {1}/{2}: measured={3} bytes ({4} MiB), budget={5} bytes ({6} MiB), over by {7} bytes ({8} MiB)." -f `
            $Label, $DeploymentMode, $Rid, $Measured, $measuredMiB, $Budget, $budgetMiB, $over, $overMiB)
    }
}
$nupkgBytes = [int64](Get-Item -LiteralPath $packagePath).Length
$nupkgKey = "{0}|{1}|nupkg" -f $DeploymentMode, $Rid
if (-not $nupkgSetupBudgetByModeRid.ContainsKey($nupkgKey)) {
    throw "No nupkg size budget for $nupkgKey"
}
Assert-SizeBudget -Label "nupkg" -Measured $nupkgBytes -Budget ([int64]$nupkgSetupBudgetByModeRid[$nupkgKey])

$setupCandidates = @(
    Get-ChildItem -LiteralPath $releasesRoot -File -Filter "*Setup*.exe" -ErrorAction SilentlyContinue
)
if ($setupCandidates.Count -lt 1) {
    throw "Velopack releases missing Setup.exe for size budget check."
}
$setupFile = $setupCandidates | Sort-Object Length -Descending | Select-Object -First 1
$setupBytes = [int64]$setupFile.Length
$setupKey = "{0}|{1}|setup" -f $DeploymentMode, $Rid
if (-not $nupkgSetupBudgetByModeRid.ContainsKey($setupKey)) {
    throw "No Setup.exe size budget for $setupKey"
}
Assert-SizeBudget -Label "Setup.exe" -Measured $setupBytes -Budget ([int64]$nupkgSetupBudgetByModeRid[$setupKey])
Write-Output ("Size budgets ok: nupkg={0} setup={1} ({2})" -f $nupkgBytes, $setupBytes, $setupFile.Name)
