# Shared runtimeconfig.json helpers for build-app-artifact / package-velopack.
# Dot-source this file; do not execute it directly.

function Get-RuntimeConfigFrameworkFamily {
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

    $names = New-Object System.Collections.Generic.List[string]
    $runtimeOptions = $document.PSObject.Properties["runtimeOptions"]
    if ($null -eq $runtimeOptions -or $null -eq $runtimeOptions.Value) {
        throw "runtimeconfig.json is missing runtimeOptions."
    }

    $opts = $runtimeOptions.Value
    $frameworkProp = $opts.PSObject.Properties["framework"]
    if ($null -ne $frameworkProp -and $null -ne $frameworkProp.Value) {
        $nameProp = $frameworkProp.Value.PSObject.Properties["name"]
        if ($null -ne $nameProp -and -not [string]::IsNullOrWhiteSpace([string]$nameProp.Value)) {
            $names.Add([string]$nameProp.Value)
        }
    }

    $frameworksProp = $opts.PSObject.Properties["frameworks"]
    if ($null -ne $frameworksProp -and $null -ne $frameworksProp.Value) {
        foreach ($item in @($frameworksProp.Value)) {
            $nameProp = $item.PSObject.Properties["name"]
            if ($null -ne $nameProp -and -not [string]::IsNullOrWhiteSpace([string]$nameProp.Value)) {
                $names.Add([string]$nameProp.Value)
            }
        }
    }

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
        [Parameter(Mandatory = $true)][string]$Rid
    )

    if ($Family -notin @("desktop", "runtime")) {
        throw "Unknown framework family: $Family"
    }

    $archToken = if ($Rid -eq "win-x64") { "x64" } else { "arm64" }
    return "net10-{0}-{1}" -f $archToken, $Family
}
