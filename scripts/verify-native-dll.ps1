[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedMachine,
    [string]$CompareTo
)

$ErrorActionPreference = "Stop"

function ConvertTo-MachineValue {
    param([string]$Value)

    try {
        if ($Value.StartsWith("0x", [System.StringComparison]::OrdinalIgnoreCase)) {
            $number = [Convert]::ToUInt32($Value.Substring(2), 16)
        }
        else {
            $number = [Convert]::ToUInt32($Value, 10)
        }
    }
    catch {
        throw "Invalid PE machine value '$Value'. Use decimal or 0x-prefixed hexadecimal."
    }

    if ($number -gt 0xffff) {
        throw "PE machine value '$Value' is outside the COFF UInt16 range."
    }

    return [uint16]$number
}

function Get-PeMachine {
    param([string]$FilePath)

    if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
        throw "Native DLL missing: $FilePath"
    }

    $stream = $null
    $reader = $null
    try {
        $stream = [System.IO.FileStream]::new(
            $FilePath,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read)
        $reader = [System.IO.BinaryReader]::new($stream)

        if ($stream.Length -lt 0x40) {
            throw "PE file is too short to contain a DOS header: $FilePath"
        }

        if ($reader.ReadUInt16() -ne 0x5a4d) {
            throw "Invalid DOS signature in native DLL: $FilePath"
        }

        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or [int64]$peOffset -gt ($stream.Length - 6)) {
            throw "Invalid DOS e_lfanew in native DLL: $FilePath"
        }

        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "Invalid PE signature in native DLL: $FilePath"
        }

        $coffHeaderStart = [int64]$peOffset + 4
        $coffHeaderEnd = $coffHeaderStart + 20
        if ($coffHeaderEnd -gt $stream.Length) {
            throw "PE COFF header is truncated in native DLL: $FilePath"
        }

        $machine = [uint16]$reader.ReadUInt16()
        $numberOfSections = [uint16]$reader.ReadUInt16()
        if ($numberOfSections -eq 0) {
            throw "PE file has no sections in native DLL: $FilePath"
        }

        $stream.Position = $coffHeaderStart + 16
        $sizeOfOptionalHeader = [uint16]$reader.ReadUInt16()
        if ($sizeOfOptionalHeader -lt 2) {
            throw "PE optional header is missing or too short in native DLL: $FilePath"
        }
        if (($machine -eq 0x8664 -or $machine -eq 0xaa64) -and
            $sizeOfOptionalHeader -lt 0xf0) {
            throw "PE32+ optional header is truncated in native DLL: $FilePath"
        }

        $optionalHeaderStart = $coffHeaderEnd
        $optionalHeaderEnd = $optionalHeaderStart + [int64]$sizeOfOptionalHeader
        if ($optionalHeaderEnd -gt $stream.Length) {
            throw "PE optional header is truncated in native DLL: $FilePath"
        }

        $sectionTableEnd = $optionalHeaderEnd + ([int64]$numberOfSections * 40)
        if ($sectionTableEnd -gt $stream.Length) {
            throw "PE section table is truncated in native DLL: $FilePath"
        }

        $stream.Position = $optionalHeaderStart
        $optionalHeaderMagic = $reader.ReadUInt16()
        if (($machine -eq 0x8664 -or $machine -eq 0xaa64) -and $optionalHeaderMagic -ne 0x020b) {
            throw ("PE optional header magic mismatch in native DLL '{0}': expected PE32+ 0x020B, got 0x{1:X4}" -f $FilePath, $optionalHeaderMagic)
        }

        return $machine
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

$expected = ConvertTo-MachineValue $ExpectedMachine
$machine = [uint16](Get-PeMachine $Path)
if ($machine -ne $expected) {
    throw ("PE machine mismatch for '{0}': expected 0x{1:X4}, got 0x{2:X4}" -f $Path, $expected, $machine)
}

if ($CompareTo) {
    $compareMachine = [uint16](Get-PeMachine $CompareTo)
    if ($compareMachine -ne $expected) {
        throw ("PE machine mismatch for comparison file '{0}': expected 0x{1:X4}, got 0x{2:X4}" -f $CompareTo, $expected, $compareMachine)
    }

    $utilityModulePath = Join-Path $PSHOME "Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1"
    Import-Module $utilityModulePath -ErrorAction Stop
    $pathHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    $compareHash = (Get-FileHash -LiteralPath $CompareTo -Algorithm SHA256).Hash
    if ($pathHash -ne $compareHash) {
        throw ("Native DLL SHA256 mismatch: '{0}' ({1}) != '{2}' ({3})" -f $Path, $pathHash, $CompareTo, $compareHash)
    }
}

Write-Output ("Verified PE machine 0x{0:X4}: {1}" -f $machine, $Path)
