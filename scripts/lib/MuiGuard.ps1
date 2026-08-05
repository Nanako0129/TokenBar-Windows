#Requires -Version 7.0
# Shared desktop MUI presence guard for publish verification and unit tests.

function Get-MuiGuardRelativePath {
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

function Assert-RequiredDesktopMuiLocales {
    <#
    .SYNOPSIS
      Fail closed unless each required locale ships a non-empty desktop
      Microsoft.ui.xaml.dll.mui (not merely any .mui under that locale folder).
    #>
    param(
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][string[]]$RequiredLocales,
        [Parameter(Mandatory = $true)][System.IO.FileInfo[]]$MuiFiles
    )

    $requiredDesktopMuiFileName = "Microsoft.ui.xaml.dll.mui"
    $missing = [System.Collections.Generic.List[string]]::new()

    foreach ($locale in $RequiredLocales) {
        $expectedRel = (Join-Path $locale $requiredDesktopMuiFileName).Replace('/', '\')
        $matched = $false
        foreach ($mui in $MuiFiles) {
            if ($mui.Length -le 0) {
                continue
            }

            $rel = (Get-MuiGuardRelativePath -Root $PublishRoot -Path $mui.FullName).Replace('/', '\')
            if ([string]::Equals($rel, $expectedRel, [StringComparison]::OrdinalIgnoreCase)) {
                $matched = $true
                break
            }
        }

        if (-not $matched) {
            [void]$missing.Add(("{0}/{1}" -f $locale, $requiredDesktopMuiFileName))
        }
    }

    if ($missing.Count -gt 0) {
        throw ("Required desktop MUI file(s) missing from publish: {0}" -f ($missing -join ", "))
    }
}
