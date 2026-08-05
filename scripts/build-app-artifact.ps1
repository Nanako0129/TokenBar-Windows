#Requires -Version 7.0

<#
.SYNOPSIS
  Build and verify one private, unsigned TokenBar App package.

.DESCRIPTION
  The command is intentionally fail-closed. It validates the repository-owned
  version contract, restores the locked .NET graph, builds the RID-specific Rust
  DLL with --locked plus the existing BuildTbNative target, publishes the WinUI
  App, and emits a version/RID-labelled ZIP with sanitized evidence. It never
  uploads or signs an artifact and never removes an existing caller path.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Rid,

    [Parameter(Mandatory = $true)]
    [Alias("EvidenceRoot")]
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ExpectedSemanticVersion = "0.2.1"
$ExpectedAssemblyVersion = "0.2.1.0"
$ExpectedDotnetVersion = "10.0.301"
$ExpectedRustVersion = "1.96.1"

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

function Read-VersionContract {
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

    $semantic = Get-RepoProperty -Document $document -Name "TbSemanticVersion"
    $assembly = Get-RepoProperty -Document $document -Name "TbAssemblyVersion"
    $product = Get-RepoProperty -Document $document -Name "TbProductName"
    $version = Get-RepoProperty -Document $document -Name "Version"
    $package = Get-RepoProperty -Document $document -Name "PackageVersion"
    $informational = Get-RepoProperty -Document $document -Name "InformationalVersion"
    $assemblyProperty = Get-RepoProperty -Document $document -Name "AssemblyVersion"
    $file = Get-RepoProperty -Document $document -Name "FileVersion"
    $revision = Get-RepoProperty -Document $document -Name "IncludeSourceRevisionInInformationalVersion"
    $lock = Get-RepoProperty -Document $document -Name "RestorePackagesWithLockFile"

    if ([string]::IsNullOrWhiteSpace($product)) {
        throw "Directory.Build.props product-name contract is empty."
    }
    if ($semantic -ne $ExpectedSemanticVersion -or $assembly -ne $ExpectedAssemblyVersion) {
        throw "Directory.Build.props version contract drifted: expected $ExpectedSemanticVersion / $ExpectedAssemblyVersion."
    }
    if ($version -ne '$(TbSemanticVersion)' -or $package -ne '$(TbSemanticVersion)' -or
        $informational -ne '$(TbSemanticVersion)') {
        throw "Directory.Build.props must derive Version, PackageVersion, and InformationalVersion from TbSemanticVersion."
    }
    if ($assemblyProperty -ne '$(TbAssemblyVersion)' -or $file -ne '$(TbAssemblyVersion)') {
        throw "Directory.Build.props must derive AssemblyVersion and FileVersion from TbAssemblyVersion."
    }
    if ($revision -ne "false" -or $lock -ne "true") {
        throw "Directory.Build.props must disable source-revision suffixes and enable NuGet lock files."
    }

    return [pscustomobject]@{
        ProductName = $product
        SemanticVersion = $semantic
        AssemblyVersion = $assembly
        Document = $document
    }
}

function Read-ManifestVersion {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "App manifest is missing: $Path"
    }

    try {
        $manifest = [xml](Get-Content -LiteralPath $Path -Raw)
    }
    catch {
        throw "App manifest is not valid XML: $($_.Exception.Message)"
    }

    $identity = $manifest.SelectSingleNode("//*[local-name()='assemblyIdentity']")
    if ($null -eq $identity -or [string]::IsNullOrWhiteSpace($identity.version)) {
        throw "App manifest has no assemblyIdentity version."
    }

    return [string]$identity.version
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

function Get-PeMachine {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "PE file is missing: $Path"
    }

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 0x40 -or
        [BitConverter]::ToUInt16($bytes, 0) -ne 0x5a4d) {
        throw "Invalid DOS header: $Path"
    }

    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ($peOffset -lt 0 -or $peOffset + 6 -gt $bytes.Length -or
        [BitConverter]::ToUInt32($bytes, $peOffset) -ne 0x00004550) {
        throw "Invalid PE header: $Path"
    }

    return [uint16][BitConverter]::ToUInt16($bytes, $peOffset + 4)
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    return [System.IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
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

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$appProject = Join-Path $repoRoot "src\TokenBar.App\TokenBar.App.csproj"
$smokeProject = Join-Path $repoRoot "src\TokenBar.Smoke\TokenBar.Smoke.csproj"
$propsPath = Join-Path $repoRoot "Directory.Build.props"
$manifestPath = Join-Path $repoRoot "src\TokenBar.App\app.manifest"
$versionContract = Read-VersionContract -Path $propsPath
$manifestVersion = Read-ManifestVersion -Path $manifestPath
if ($manifestVersion -ne $versionContract.AssemblyVersion) {
    throw "App manifest version '$manifestVersion' does not match Directory.Build.props numeric version '$($versionContract.AssemblyVersion)'."
}

if ($Rid -eq "win-x64") {
    $platform = "x64"
    $rustTarget = "x86_64-pc-windows-msvc"
    $expectedMachine = [uint16]0x8664
}
else {
    $platform = "ARM64"
    $rustTarget = "aarch64-pc-windows-msvc"
    $expectedMachine = [uint16]0xaa64
}

$outputRootResolved = Assert-NewOutputRoot -Path $OutputRoot
$appAssemblyName = "{0}.App" -f $versionContract.ProductName
$appExecutableName = "$appAssemblyName.exe"
$appPriName = "$appAssemblyName.pri"
$artifactName = "{0}-App-{1}-{2}" -f $versionContract.ProductName, $versionContract.SemanticVersion, $Rid
$artifactRoot = Join-Path $outputRootResolved $artifactName
if (Test-Path -LiteralPath $artifactRoot) {
    throw "Artifact output already exists; choose a new OutputRoot: $artifactRoot"
}
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
$publishRoot = Join-Path $artifactRoot "publish"
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

Push-Location $repoRoot
try {
    $dotnetVersion = ((& dotnet --version) | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $dotnetVersion -ne $ExpectedDotnetVersion) {
        throw "dotnet SDK drifted: expected $ExpectedDotnetVersion, got '$dotnetVersion'."
    }

    $rustVerbose = @(& rustc --version --verbose)
    if ($LASTEXITCODE -ne 0) {
        throw "rustc --version --verbose failed."
    }
    $rustRelease = ($rustVerbose | Where-Object { $_ -match '^release:\s*(.+)$' } |
        Select-Object -First 1) -replace '^release:\s*', ''
    $rustCommit = ($rustVerbose | Where-Object { $_ -match '^commit-hash:\s*(.+)$' } |
        Select-Object -First 1) -replace '^commit-hash:\s*', ''
    if ([string]::IsNullOrWhiteSpace($rustRelease) -or
        [string]::IsNullOrWhiteSpace($rustCommit) -or $rustRelease -ne $ExpectedRustVersion -or
        $rustCommit -eq "unknown") {
        throw "rustc toolchain drifted: expected release $ExpectedRustVersion with a concrete commit hash."
    }

    $gitStatus = @(& git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect git checkout state."
    }
    if ($gitStatus.Count -ne 0) {
        throw "Artifact builds require a clean git checkout so gitSha identifies the exact source."
    }

    $gitSha = ((& git rev-parse --verify HEAD) | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $gitSha -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Unable to resolve a sanitized 40-character git SHA."
    }

    Invoke-Captured -Command "cargo" -Arguments @(
        "build", "--release", "--locked", "--target", $rustTarget
    ) -FailureMessage "Cargo locked build failed"

    foreach ($lockPath in @(
        "src\TokenBar.App\packages.lock.json",
        "src\TokenBar.Smoke\packages.lock.json",
        "src\TokenBar.Core\packages.lock.json",
        "src\TokenBar.Interop\packages.lock.json"
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $lockPath) -PathType Leaf)) {
            throw "Missing generated NuGet lock file: $lockPath"
        }
    }
    Invoke-Captured -Command "dotnet" -Arguments @(
        "restore", $appProject, "--locked-mode"
    ) -FailureMessage "Locked App restore failed"
    Invoke-Captured -Command "dotnet" -Arguments @(
        "restore", $smokeProject, "--locked-mode"
    ) -FailureMessage "Locked Smoke restore failed"

    # Keep the existing repository-owned Platform/RID mapping and verifier in
    # the build path. TbNativeCargoLocked makes that target's own Cargo
    # invocation fail closed instead of relying on the preceding build.
    Invoke-Captured -Command "dotnet" -Arguments @(
        "msbuild", $smokeProject, "-t:BuildTbNative", "-p:Configuration=Release",
        "-p:Platform=$platform", "-p:RuntimeIdentifier=$Rid",
        "-p:TbNativeCargoLocked=true", "-p:RestoreLockedMode=true"
    ) -FailureMessage "BuildTbNative failed"

    Invoke-Captured -Command "dotnet" -Arguments @(
        "publish", $appProject, "-c", "Release", "-r", $Rid, "--self-contained",
        "-p:Platform=$platform", "-p:RuntimeIdentifier=$Rid", "--no-restore",
        "-o", $publishRoot
    ) -FailureMessage "Locked App publish failed"
}
finally {
    Pop-Location
}

$exePath = Join-Path $publishRoot $appExecutableName
$nativePath = Join-Path $publishRoot "tb_core_ffi.dll"
$priPath = Join-Path $publishRoot $appPriName
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "Published App executable is missing: $appExecutableName"
}
if (-not (Test-Path -LiteralPath $nativePath -PathType Leaf)) {
    throw "Published native DLL is missing: tb_core_ffi.dll"
}
if (-not (Test-Path -LiteralPath $priPath -PathType Leaf)) {
    throw "Published PRI is missing: $appPriName"
}

$xbfFiles = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Filter "*.xbf")
if ($xbfFiles.Count -eq 0) {
    throw "Published WinUI resources contain no XBF files."
}

$muiFiles = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Filter "*.mui")
if ($muiFiles.Count -eq 0) {
    throw "Published output must retain .mui locale files; found zero."
}

# Required locale tags (checked-in manifest). Compare case-insensitively; reject
# duplicate entries after case normalization; fail with exact missing locale names.
$muiManifestPath = Join-Path $repoRoot "scripts\required-mui-locales.txt"
if (-not (Test-Path -LiteralPath $muiManifestPath -PathType Leaf)) {
    throw "Missing required MUI locale manifest: scripts/required-mui-locales.txt"
}
$rawLocales = @(
    Get-Content -LiteralPath $muiManifestPath |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith("#") }
)
if ($rawLocales.Count -lt 1) {
    throw "MUI locale manifest is empty."
}
$requiredLocales = [System.Collections.Generic.List[string]]::new()
$seenLocaleKeys = @{}
foreach ($locale in $rawLocales) {
    $key = $locale.ToLowerInvariant()
    if ($seenLocaleKeys.ContainsKey($key)) {
        throw "Duplicate MUI locale entry after case normalization: $locale"
    }
    $seenLocaleKeys[$key] = $true
    [void]$requiredLocales.Add($locale)
}
$missingLocales = [System.Collections.Generic.List[string]]::new()
foreach ($locale in $requiredLocales) {
    $matched = $false
    foreach ($mui in $muiFiles) {
        $rel = Get-RelativePath -Root $publishRoot -Path $mui.FullName
        if ($rel -match "(?i)(^|[/\\])$([regex]::Escape($locale))([/\\]|$)") {
            $matched = $true
            break
        }
    }
    if (-not $matched) {
        [void]$missingLocales.Add($locale)
    }
}
if ($missingLocales.Count -gt 0) {
    throw ("Required MUI locale(s) missing from publish: {0}" -f ($missingLocales -join ", "))
}

# Forbidden payload / diagnostics / unused SDK binaries (must be absent from installer payload).
$forbiddenExact = @(
    "onnxruntime.dll",
    "onnxruntime_providers_shared.dll",
    "DirectML.dll",
    "Microsoft.Windows.Widgets.dll",
    "Microsoft.Windows.Widgets.Projection.dll",
    "Microsoft.Windows.Widgets.winmd",
    "WinUIEdit.dll",
    "mscordaccore.dll",
    "mscordbi.dll"
)
$publishFiles = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File)
$pdbCount = @($publishFiles | Where-Object { $_.Extension -eq ".pdb" }).Count
if ($pdbCount -ne 0) {
    throw "Published output must contain zero PDB files; found $pdbCount."
}
foreach ($name in $forbiddenExact) {
    $hits = @($publishFiles | Where-Object {
            [string]::Equals($_.Name, $name, [StringComparison]::OrdinalIgnoreCase)
        })
    if ($hits.Count -gt 0) {
        throw "Forbidden file present in publish: $name"
    }
}
$prefixForbidden = @(
    @{ Prefix = "mscordaccore_"; Label = "mscordaccore_*" },
    @{ Prefix = "Microsoft.DiaSymReader.Native"; Label = "Microsoft.DiaSymReader.Native*" }
)
foreach ($rule in $prefixForbidden) {
    $hits = @($publishFiles | Where-Object {
            $_.Name.StartsWith($rule.Prefix, [StringComparison]::OrdinalIgnoreCase)
        })
    if ($hits.Count -gt 0) {
        throw "Forbidden file present in publish: $($rule.Label)"
    }
}

$assetCounts = [ordered]@{
    "Assets/anim-cat2" = 5
    "Assets/anim-cat2-light" = 5
    "Assets/anim-parrot" = 10
    "Assets/anim-parrot-light" = 10
}
foreach ($asset in $assetCounts.GetEnumerator()) {
    $assetDirectory = Join-Path $publishRoot $asset.Key
    if (-not (Test-Path -LiteralPath $assetDirectory -PathType Container)) {
        throw "Required asset directory is missing: $($asset.Key)"
    }
    for ($index = 0; $index -lt $asset.Value; $index++) {
        $assetPath = Join-Path $assetDirectory ("frame-{0:D2}.png" -f $index)
        if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
            throw "Required asset file is missing: $($asset.Key)/frame-$('{0:D2}' -f $index).png"
        }
    }
}

$exeMachine = Get-PeMachine -Path $exePath
$nativeMachine = Get-PeMachine -Path $nativePath
if ($exeMachine -ne $expectedMachine -or $nativeMachine -ne $expectedMachine) {
    throw "PE architecture mismatch for ${Rid}: expected 0x$('{0:X4}' -f $expectedMachine), exe=0x$('{0:X4}' -f $exeMachine), native=0x$('{0:X4}' -f $nativeMachine)."
}

$nativeSourcePath = Join-Path $repoRoot ("target\{0}\release\tb_core_ffi.dll" -f $rustTarget)
if (-not (Test-Path -LiteralPath $nativeSourcePath -PathType Leaf)) {
    throw "RID-specific native source is missing: $rustTarget/tb_core_ffi.dll"
}
$nativeHash = (Get-FileHash -LiteralPath $nativePath -Algorithm SHA256).Hash.ToLowerInvariant()
$nativeSourceHash = (Get-FileHash -LiteralPath $nativeSourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($nativeHash -ne $nativeSourceHash) {
    throw "Published native DLL does not match the BuildTbNative source bytes."
}

$fileVersionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
if ($fileVersionInfo.FileVersion -ne $ExpectedAssemblyVersion) {
    throw "Published App FileVersion '$($fileVersionInfo.FileVersion)' does not match $ExpectedAssemblyVersion."
}
if ($fileVersionInfo.ProductVersion -ne $ExpectedSemanticVersion) {
    throw "Published App ProductVersion '$($fileVersionInfo.ProductVersion)' does not match $ExpectedSemanticVersion."
}

$inventory = @(
    Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Sort-Object FullName |
        ForEach-Object {
            $relative = Get-RelativePath -Root $publishRoot -Path $_.FullName
            [pscustomobject]@{
                path = $relative
                bytes = [int64]$_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
)

$zipName = "$artifactName.zip"
$zipPath = Join-Path $artifactRoot $zipName
if (Test-Path -LiteralPath $zipPath) {
    throw "Artifact ZIP already exists: $zipName"
}
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $publishRoot,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$hashName = "$zipName.sha256"
$hashPath = Join-Path $artifactRoot $hashName
Set-Content -LiteralPath $hashPath -Value "$zipHash  $zipName" -Encoding ascii -NoNewline
$zipBytes = [int64](Get-Item -LiteralPath $zipPath).Length

# Tight Full publish-zip budgets (bytes). Measured baselines + ~5% tolerance.
# A budget increase requires an explicit source edit and explanation.
# Placeholder zeros are replaced after clean measured publish on this branch.
$fullPublishZipBudgetByRid = @{
    "win-x64"   = [int64]78691000   # measured 74943809 (2026-08-05 clean Full) + ~5%
    "win-arm64" = [int64]82000000   # provisional pending arm64 measure; tighten in follow-up if needed
}
if (-not $fullPublishZipBudgetByRid.ContainsKey($Rid)) {
    throw "No publish-zip size budget configured for RID $Rid."
}
$maxZipBytes = [int64]$fullPublishZipBudgetByRid[$Rid]
if ($zipBytes -gt $maxZipBytes) {
    $over = $zipBytes - $maxZipBytes
    $measuredMiB = [math]::Round($zipBytes / 1MB, 3)
    $budgetMiB = [math]::Round($maxZipBytes / 1MB, 3)
    $overMiB = [math]::Round($over / 1MB, 3)
    throw ("Publish zip exceeds size budget for {0}: measured={1} bytes ({2} MiB), budget={3} bytes ({4} MiB), over by {5} bytes ({6} MiB)." -f `
        $Rid, $zipBytes, $measuredMiB, $maxZipBytes, $budgetMiB, $over, $overMiB)
}

# Separate version/RID-bound symbols/support artifact (NOT part of Setup.exe/nupkg).
# Collect PDBs and DAC/DBI diagnostics from intermediate build output so crash dumps
# remain analyzable even though the installer payload is strip-clean.
$symbolsStaging = Join-Path $artifactRoot "symbols-staging"
if (Test-Path -LiteralPath $symbolsStaging) {
    Remove-Item -LiteralPath $symbolsStaging -Recurse -Force
}
New-Item -ItemType Directory -Path $symbolsStaging | Out-Null
$symbolsPatterns = @("*.pdb", "mscordaccore.dll", "mscordbi.dll", "mscordaccore_*.dll", "Microsoft.DiaSymReader.Native*.dll")
$searchRoots = @(
    (Join-Path $repoRoot ("src\TokenBar.App\bin\{0}\Release" -f $platform)),
    (Join-Path $repoRoot ("src\TokenBar.App\obj\{0}\Release" -f $platform)),
    $publishRoot
)
$copiedSymbolNames = @{}
foreach ($root in $searchRoots) {
    if (-not (Test-Path -LiteralPath $root)) { continue }
    foreach ($pat in $symbolsPatterns) {
        Get-ChildItem -LiteralPath $root -Recurse -File -Filter $pat -ErrorAction SilentlyContinue |
            ForEach-Object {
                $destName = $_.Name
                if ($copiedSymbolNames.ContainsKey($destName.ToLowerInvariant())) { return }
                Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $symbolsStaging $destName) -Force
                $copiedSymbolNames[$destName.ToLowerInvariant()] = $true
            }
    }
}
$symbolFiles = @(Get-ChildItem -LiteralPath $symbolsStaging -File | Sort-Object Name)
$symbolInventory = @(
    $symbolFiles | ForEach-Object {
        [pscustomobject]@{
            path = $_.Name
            bytes = [int64]$_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
)
$symbolsMeta = [ordered]@{
    schema = "tokenbar-symbols-support.v1"
    productName = $versionContract.ProductName
    semanticVersion = $versionContract.SemanticVersion
    gitSha = $gitSha.ToLowerInvariant()
    rid = $Rid
    platform = $platform
    note = "Support/diagnostics archive for matching crash dumps. Not installed by Setup.exe or nupkg. Release operators archive this alongside the release transaction; PR CI uploads only sanitized hash metadata."
    files = $symbolInventory
}
$symbolsMetaPath = Join-Path $symbolsStaging "SYMBOLS-MANIFEST.json"
($symbolsMeta | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $symbolsMetaPath -Encoding utf8
$symbolsZipName = "{0}-Symbols-{1}-{2}.zip" -f $versionContract.ProductName, $versionContract.SemanticVersion, $Rid
$symbolsZipPath = Join-Path $artifactRoot $symbolsZipName
if (Test-Path -LiteralPath $symbolsZipPath) {
    throw "Symbols archive already exists: $symbolsZipName"
}
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $symbolsStaging,
    $symbolsZipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)
$symbolsZipHash = (Get-FileHash -LiteralPath $symbolsZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$symbolsZipBytes = [int64](Get-Item -LiteralPath $symbolsZipPath).Length
Set-Content -LiteralPath (Join-Path $artifactRoot "$symbolsZipName.sha256") -Value "$symbolsZipHash  $symbolsZipName" -Encoding ascii -NoNewline
# Drop staging tree; keep only the zip + sha256 next to the app artifact.
Remove-Item -LiteralPath $symbolsStaging -Recurse -Force
# Sanitized symbols evidence (hashes/sizes only) for PR CI upload surfaces.
$symbolsEvidence = [ordered]@{
    schema = "tokenbar-symbols-evidence.v1"
    productName = $versionContract.ProductName
    semanticVersion = $versionContract.SemanticVersion
    gitSha = $gitSha.ToLowerInvariant()
    rid = $Rid
    archive = $symbolsZipName
    archiveSha256 = $symbolsZipHash
    archiveBytes = $symbolsZipBytes
    fileCount = [int]$symbolInventory.Count
    files = $symbolInventory
}
($symbolsEvidence | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath (Join-Path $artifactRoot "symbols-evidence.json") -Encoding utf8

$evidence = [ordered]@{
    schema = "phase10-app-evidence.v1"
    status = "structure-version-pe-hash"
    productName = $versionContract.ProductName
    semanticVersion = $versionContract.SemanticVersion
    assemblyVersion = $versionContract.AssemblyVersion
    informationalVersion = $ExpectedSemanticVersion
    manifestVersion = $manifestVersion
    muiCount = [int]$muiFiles.Count
    requiredMuiLocales = [int]$requiredLocales.Count
    pdbCount = [int]$pdbCount
    forbiddenAbsent = $true
    maxZipBytesBudget = [int64]$maxZipBytes
    zipBytes = [int64]$zipBytes
    symbolsArchive = $symbolsZipName
    symbolsArchiveSha256 = $symbolsZipHash
    symbolsArchiveBytes = [int64]$symbolsZipBytes
    rid = $Rid
    platform = $platform
    expectedPeMachine = ("0x{0:X4}" -f $expectedMachine)
    gitSha = $gitSha.ToLowerInvariant()
    dotnetVersion = $dotnetVersion
    rustc = [ordered]@{
        release = $rustRelease
        commitHash = $rustCommit
    }
    native = [ordered]@{
        target = $rustTarget
        source = "target/$rustTarget/release/tb_core_ffi.dll"
        sha256 = $nativeHash
    }
    artifact = [ordered]@{
        zip = $zipName
        sha256 = $zipHash
        bytes = [int64](Get-Item -LiteralPath $zipPath).Length
        reproducibility = "not-claimed"
    }
    inventory = $inventory
    startupSmoke = [ordered]@{
        command = "$appExecutableName --startup-smoke <new-sentinel-path>"
        hostGate = "explicitly authorized non-disposable active user profile with outbound network blocked; not isolated"
        hostedCi = "structure/version/PE/hash checks only; no interactive WinUI startup claim"
    }
}

$evidenceJson = $evidence | ConvertTo-Json -Depth 10
if ($evidenceJson -match '(?i)([A-Z]:[\\/]|/Users/|/home/|\\Users\\)') {
    throw "Evidence contains an absolute path; refusing to write evidence.json."
}
$evidenceName = "evidence.json"
$evidencePath = Join-Path $artifactRoot $evidenceName
Set-Content -LiteralPath $evidencePath -Value $evidenceJson -Encoding utf8

Write-Output "Phase 10 App artifact verified: $zipName"
Write-Output "Evidence: $evidenceName; checksum: $hashName; symbols: $symbolsZipName"
