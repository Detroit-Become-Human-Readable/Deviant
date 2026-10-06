[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $IndexPath,
    [Parameter(Mandatory = $true)] [string] $DependencyHeaderPath,
    [string] $PackageDirectory,
    [string] $VolumeBaseName,
    [string] $ReportPath,
    [switch] $RequireGoldenProfile,
    [switch] $HashPackages
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-BeUInt32([byte[]] $Bytes, [int] $Offset) {
    return ([uint64]$Bytes[$Offset] * 16777216) + ([uint64]$Bytes[$Offset + 1] * 65536) + ([uint64]$Bytes[$Offset + 2] * 256) + [uint64]$Bytes[$Offset + 3]
}
function Read-Bytes([string] $Path) { [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Path).Path) }
function Get-Sha256([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Assert-That([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
public sealed class GoldenIndexScan {
    public int UniqueTypeIdKeys { get; set; }
    public SortedSet<uint> Packages { get; } = new SortedSet<uint>();
    public SortedDictionary<uint, long> Flags { get; } = new SortedDictionary<uint, long>();
}
public sealed class GoldenDependencyScan {
    public ulong ReferenceCount { get; set; }
    public int KeyMismatches { get; set; }
    public long EndOffset { get; set; }
}
public static class GoldenArchiveScanner {
    private static uint U32(byte[] b, int p) => ((uint)b[p] << 24) | ((uint)b[p+1] << 16) | ((uint)b[p+2] << 8) | b[p+3];
    public static GoldenIndexScan Index(byte[] b, int count) {
        var result = new GoldenIndexScan();
        var ids = new HashSet<ulong>();
        for (int i=0, p=105; i<count; i++, p+=28) {
            uint type=U32(b,p), flag=U32(b,p+4), id=U32(b,p+8), package=U32(b,p+24);
            ids.Add(((ulong)type << 32) | id);
            result.Packages.Add(package);
            if (!result.Flags.ContainsKey(flag)) result.Flags[flag]=0;
            result.Flags[flag]++;
        }
        result.UniqueTypeIdKeys=ids.Count;
        return result;
    }
    public static GoldenDependencyScan Dependency(byte[] dep, byte[] idx, int rows) {
        var result = new GoldenDependencyScan();
        long p=28;
        for (int i=0; i<rows; i++) {
            if (p+16 > dep.LongLength) throw new ArgumentException("Dependency row header extends past end of file at row " + i);
            int d=(int)p, x=105+i*28;
            if (U32(dep,d)!=U32(idx,x) || U32(dep,d+4)!=U32(idx,x+4) || U32(dep,d+8)!=U32(idx,x+8)) result.KeyMismatches++;
            uint refs=U32(dep,d+12);
            result.ReferenceCount += refs;
            p += 16L + (long)refs*20L;
            if (p > dep.LongLength) throw new ArgumentException("Dependency references extend past end of file at row " + i);
        }
        result.EndOffset=p;
        return result;
    }
}
'@

$idxPathFull = (Resolve-Path -LiteralPath $IndexPath).Path
$depPathFull = (Resolve-Path -LiteralPath $DependencyHeaderPath).Path
$idx = Read-Bytes $idxPathFull
$dep = Read-Bytes $depPathFull
$magic = [System.Text.Encoding]::ASCII.GetBytes('QUANTICDREAMTABINDEX')
Assert-That ($idx.Length -ge 105) 'Index is shorter than its 105-byte header.'
Assert-That ([Convert]::ToBase64String($idx[0..19]) -eq [Convert]::ToBase64String($magic)) 'Index magic does not match QUANTICDREAMTABINDEX.'
$indexVersion = Read-BeUInt32 $idx 20
$indexUnknown = Read-BeUInt32 $idx 24
$indexCount = Read-BeUInt32 $idx 101
Assert-That ($indexVersion -eq 18) "Expected index version 18, found $indexVersion."
Assert-That ($idx[100] -eq 0) 'Index terminator byte at offset 100 is not NUL.'
$indexExpectedLength = [uint64]105 + ([uint64]$indexCount * 28)
Assert-That ([uint64]$idx.Length -eq $indexExpectedLength) "Index length $($idx.Length) does not equal 105 + count*28 ($indexExpectedLength)."

$indexScan = [GoldenArchiveScanner]::Index($idx, [int]$indexCount)
$packages = $indexScan.Packages
$flags = $indexScan.Flags

Assert-That ($dep.Length -ge 28) 'Dependency header is shorter than 28 bytes.'
$depMagic = [System.Text.Encoding]::ASCII.GetBytes('QUANTICDREAMTABINDEX')
Assert-That ([Convert]::ToBase64String($dep[0..19]) -eq [Convert]::ToBase64String($depMagic)) 'Dependency magic does not match QUANTICDREAMTABINDEX.'
$depVersion = Read-BeUInt32 $dep 20
$depRows = Read-BeUInt32 $dep 24
Assert-That ($depVersion -eq 18) "Expected dependency version 18, found $depVersion."
Assert-That ($depRows -eq $indexCount) 'Dependency row count differs from the index record count; ordinal key comparison is not valid.'
$dependencyScan = [GoldenArchiveScanner]::Dependency($dep, $idx, [int]$depRows)
$dependencyReferenceCount = $dependencyScan.ReferenceCount
$depExpectedLength = $dependencyScan.EndOffset
Assert-That ([uint64]$dep.Length -eq $depExpectedLength) "Dependency length $($dep.Length) does not equal the end offset computed from interleaved rows and references ($depExpectedLength)."
Assert-That ($dependencyScan.KeyMismatches -eq 0) "Dependency row keys differ from corresponding index rows ($($dependencyScan.KeyMismatches) mismatches)."

$packageChecks = @()
if ($PackageDirectory) {
    $packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
    $volumeStem = if ([string]::IsNullOrWhiteSpace($VolumeBaseName)) { [System.IO.Path]::GetFileNameWithoutExtension($idxPathFull) } else { $VolumeBaseName }
    foreach ($package in ($packages | Sort-Object)) {
        $name = if ($package -eq 0) { "$volumeStem.dat" } else { "$volumeStem.d{0:D2}" -f $package }
        $path = Join-Path $packageRoot $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required package volume missing: $name" }
        $packageHash = if ($HashPackages) { Get-Sha256 $path } else { $null }
        $packageChecks += [pscustomobject]@{ package = $package; name = $name; length = (Get-Item -LiteralPath $path).Length; sha256 = $packageHash }
    }
}

$isGoldenProfile = ($indexCount -eq 373748 -and $depRows -eq 373748 -and $dependencyReferenceCount -eq 1002543 -and $idx.Length -eq 10465049 -and $dep.Length -eq 26030856 -and $packages.Count -eq 30 -and $indexScan.UniqueTypeIdKeys -eq 373748 -and $flags.Count -eq 1 -and $flags.ContainsKey([uint32]1))
if ($RequireGoldenProfile) { Assert-That $isGoldenProfile 'Supplied files do not match the documented golden profile observations.' }
$flagHistogram = [ordered]@{}
foreach ($flag in $flags.Keys) { $flagHistogram[[string]$flag] = $flags[$flag] }
$report = [ordered]@{
    format = 'deviant-golden-archive-evidence-v1'
    index = [ordered]@{ pathLabel = 'caller-supplied IndexPath'; sha256 = Get-Sha256 $idxPathFull; length = $idx.Length; magic = 'QUANTICDREAMTABINDEX'; version = $indexVersion; unknownWordAt24 = $indexUnknown; terminatorAt100 = [int]$idx[100]; countAt101 = $indexCount; recordSize = 28; uniqueTypeIdKeys = $indexScan.UniqueTypeIdKeys; packageIds = @($packages); flagHistogram = $flagHistogram; expectedLength = $indexExpectedLength }
    dependency = [ordered]@{ pathLabel = 'caller-supplied DependencyHeaderPath'; sha256 = Get-Sha256 $depPathFull; length = $dep.Length; magic = 'QUANTICDREAMTABINDEX'; version = $depVersion; rowCount = $depRows; rowSize = 16; referenceCountFromRows = $dependencyReferenceCount; referenceSize = 20; rowFourthWordSemantics = 'big-endian reference count'; indexRowKeyMismatches = $dependencyScan.KeyMismatches; expectedLength = $depExpectedLength }
    packageChecks = $packageChecks
    matchesGoldenProfileObservations = $isGoldenProfile
    limitations = @('Matching the golden profile describes one observed corpus; it is not a parser requirement.', 'Dependency reference semantics and targets outside the top-level index remain unresolved.', 'Structural byte-count checks do not establish runtime behavior or complete archive extraction.')
}
$json = $report | ConvertTo-Json -Depth 8
if ($ReportPath) { $json | Set-Content -LiteralPath $ReportPath -Encoding UTF8 }
$json
