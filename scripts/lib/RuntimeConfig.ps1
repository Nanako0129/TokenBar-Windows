# Shared runtimeconfig.json helpers for build-app-artifact / package-velopack.
# Dot-source this file; do not execute it directly.

function Get-RuntimeConfigFrameworkEntries {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "runtimeconfig.json is missing: $Path"
    }

    try {
        $document = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "runtimeconfig.json is not valid JSON: $($_.Exception.Message)"
    }

    $runtimeOptions = $document.PSObject.Properties["runtimeOptions"]
    if ($null -eq $runtimeOptions -or $null -eq $runtimeOptions.Value) {
        throw "runtimeconfig.json is missing runtimeOptions."
    }

    $opts = $runtimeOptions.Value
    $entries = [System.Collections.Generic.List[object]]::new()

    function Add-FrameworkEntry {
        param($Node)
        if ($null -eq $Node) { return }
        $nameProp = $Node.PSObject.Properties["name"]
        $versionProp = $Node.PSObject.Properties["version"]
        if ($null -eq $nameProp -or [string]::IsNullOrWhiteSpace([string]$nameProp.Value)) {
            throw "runtimeconfig.json framework entry is missing a name."
        }
        if ($null -eq $versionProp -or [string]::IsNullOrWhiteSpace([string]$versionProp.Value)) {
            throw ("runtimeconfig.json framework '{0}' is missing a version." -f [string]$nameProp.Value)
        }
        $entries.Add([pscustomobject]@{
            Name = [string]$nameProp.Value
            Version = [string]$versionProp.Value
        })
    }

    $frameworkProp = $opts.PSObject.Properties["framework"]
    if ($null -ne $frameworkProp -and $null -ne $frameworkProp.Value) {
        Add-FrameworkEntry -Node $frameworkProp.Value
    }

    $frameworksProp = $opts.PSObject.Properties["frameworks"]
    if ($null -ne $frameworksProp -and $null -ne $frameworksProp.Value) {
        foreach ($item in @($frameworksProp.Value)) {
            Add-FrameworkEntry -Node $item
        }
    }

    if ($entries.Count -lt 1) {
        throw "runtimeconfig.json does not list any frameworks."
    }

    return @($entries)
}

function Get-FrameworkVersionMajor {
    param([Parameter(Mandatory = $true)][string]$Version)

    $trimmed = $Version.Trim()
    if ($trimmed -notmatch '^(?<major>\d+)(\.|$)') {
        throw "runtimeconfig.json framework version is not parseable: $Version"
    }
    return [int]$Matches['major']
}

function Assert-Net10FrameworkEntries {
    param(
        [Parameter(Mandatory = $true)][object[]]$Entries,
        [Parameter(Mandatory = $false)][string[]]$AllowedNames = @(
            "Microsoft.NETCore.App",
            "Microsoft.WindowsDesktop.App"
        )
    )

    $allowed = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $AllowedNames) { [void]$allowed.Add($name) }

    foreach ($entry in $Entries) {
        if (-not $allowed.Contains([string]$entry.Name)) {
            throw ("runtimeconfig.json lists unexpected framework '{0}' (version {1}). Allowed: {2}." -f `
                $entry.Name, $entry.Version, ($AllowedNames -join ", "))
        }
        $major = Get-FrameworkVersionMajor -Version ([string]$entry.Version)
        if ($major -ne 10) {
            throw ("runtimeconfig.json framework '{0}' version '{1}' is not .NET 10.x (major={2}). Packaging refuses to emit a net10-* Velopack prerequisite for a non-10 host." -f `
                $entry.Name, $entry.Version, $major)
        }
    }
}

function Get-RuntimeConfigFrameworkFamily {
    param([Parameter(Mandatory = $true)][string]$Path)

    $entries = @(Get-RuntimeConfigFrameworkEntries -Path $Path)
    Assert-Net10FrameworkEntries -Entries $entries

    $names = @($entries | ForEach-Object { [string]$_.Name })
    if ($names -contains "Microsoft.WindowsDesktop.App") {
        return "desktop"
    }
    if ($names -contains "Microsoft.NETCore.App") {
        return "runtime"
    }

    throw "runtimeconfig.json does not name Microsoft.NETCore.App or Microsoft.WindowsDesktop.App."
}

function Get-VelopackFrameworkSpecFromFamily {
    param(
        [Parameter(Mandatory = $true)][string]$Family,
        [Parameter(Mandatory = $true)][string]$Rid,
        # Major is fixed to 10 because Assert-Net10FrameworkEntries already refused anything else.
        # Callers must validate runtimeconfig before constructing a pack token.
        [Parameter(Mandatory = $false)][int]$Major = 10
    )

    if ($Family -notin @("desktop", "runtime")) {
        throw "Unknown framework family: $Family"
    }
    if ($Major -ne 10) {
        throw "Velopack framework major must be 10 for this product (got $Major)."
    }

    $archToken = if ($Rid -eq "win-x64") { "x64" } else { "arm64" }
    return "net{0}-{1}-{2}" -f $Major, $archToken, $Family
}
