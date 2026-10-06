[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $CliAssemblyPath,
    [Parameter(Mandatory = $true)] [string] $IndexPath,
    [Parameter(Mandatory = $true)] [string] $DependencyHeaderPath,
    [Parameter(Mandatory = $true)] [string] $OutputDirectory,
    [string] $VolumeBaseName,
    [string] $DotNetCommand = 'dotnet'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-PathWithin([string] $Candidate, [string] $Root) {
    $candidateFull = [System.IO.Path]::GetFullPath($Candidate).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    return $candidateFull.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase) -or
        $candidateFull.Equals($rootFull.TrimEnd([System.IO.Path]::DirectorySeparatorChar), [System.StringComparison]::OrdinalIgnoreCase)
}

$cli = (Resolve-Path -LiteralPath $CliAssemblyPath).Path
$index = (Resolve-Path -LiteralPath $IndexPath).Path
$dependency = (Resolve-Path -LiteralPath $DependencyHeaderPath).Path
$sourceRoot = Split-Path -Parent $index
$outputFull = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-PathWithin $outputFull $sourceRoot) { throw 'OutputDirectory must be outside the selected archive directory.' }
if (Test-Path -LiteralPath $outputFull) {
    if ((Get-ChildItem -LiteralPath $outputFull -Force | Measure-Object).Count -gt 0) { throw "OutputDirectory must be new or empty: $outputFull" }
} else {
    New-Item -ItemType Directory -Force -Path $outputFull | Out-Null
}
$cache = Join-Path $outputFull 'cache'
if (Test-PathWithin $cache $sourceRoot) { throw 'The CLI cache must be outside the selected archive directory.' }
New-Item -ItemType Directory -Force -Path $cache | Out-Null
$boundsPath = Join-Path $outputFull 'bounds.json'
$stderrPath = Join-Path $outputFull 'bounds.stderr.txt'
$structurePath = Join-Path $outputFull 'golden-evidence.json'
$summaryPath = Join-Path $outputFull 'summary.json'

$start = [System.Diagnostics.ProcessStartInfo]::new()
$start.FileName = $DotNetCommand
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in @($cli, 'verify', '--input', $index, '--cache', $cache, '--json')) { [void]$start.ArgumentList.Add($argument) }
$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $start
if (-not $process.Start()) { throw "Could not start $DotNetCommand." }
$stdoutTask = $process.StandardOutput.ReadToEndAsync()
$stderrTask = $process.StandardError.ReadToEndAsync()
$process.WaitForExit()
$stdout = $stdoutTask.GetAwaiter().GetResult()
$stderr = $stderrTask.GetAwaiter().GetResult()
$stdout | Set-Content -LiteralPath $boundsPath -Encoding UTF8
$stderr | Set-Content -LiteralPath $stderrPath -Encoding UTF8
if ($process.ExitCode -ne 0) { throw "Deviant verify exited with code $($process.ExitCode). See bounds.stderr.txt and bounds.json in the supplied output directory." }

try { $bounds = $stdout | ConvertFrom-Json -ErrorAction Stop }
catch { throw "Deviant verify did not emit one valid JSON document. See bounds.json in the supplied output directory. $($_.Exception.Message)" }
if ($bounds.mode -ne 'bounds-summary') { throw "Unexpected CLI verify mode: $($bounds.mode)" }
if ($bounds.total -ne 373748 -or $bounds.valid -ne 373748 -or $bounds.invalid -ne 0) { throw 'CLI bounds summary does not match the golden record totals.' }
if (@($bounds.volumes).Count -ne 30 -or @($bounds.volumes | Where-Object { -not $_.exists }).Count -gt 0) { throw 'CLI bounds summary does not report all 30 package volumes present.' }
if (@($bounds.diagnostics).Count -ne 0) { throw 'CLI reported diagnostics for the golden corpus.' }

$structureArgs = @{ IndexPath = $index; DependencyHeaderPath = $dependency; PackageDirectory = $sourceRoot; RequireGoldenProfile = $true; ReportPath = $structurePath }
if (-not [string]::IsNullOrWhiteSpace($VolumeBaseName)) { $structureArgs.VolumeBaseName = $VolumeBaseName }
$structureText = & (Join-Path $PSScriptRoot 'Test-GoldenArchiveEvidence.ps1') @structureArgs
$structure = $structureText | ConvertFrom-Json -ErrorAction Stop
if (-not $structure.matchesGoldenProfileObservations -or $structure.dependency.indexRowKeyMismatches -ne 0) { throw 'Structural golden archive evidence check did not pass.' }

$summary = [ordered]@{
    passed = $true
    cliExitCode = $process.ExitCode
    cliMode = $bounds.mode
    resources = [ordered]@{ total = $bounds.total; valid = $bounds.valid; invalid = $bounds.invalid }
    volumeCount = @($bounds.volumes).Count
    diagnostics = @($bounds.diagnostics).Count
    indexSha256 = $structure.index.sha256
    dependencySha256 = $structure.dependency.sha256
    dependencyReferences = $structure.dependency.referenceCountFromRows
    dependencyOrdinalKeyMismatches = $structure.dependency.indexRowKeyMismatches
    matchesGoldenProfileObservations = $structure.matchesGoldenProfileObservations
}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
$summary | ConvertTo-Json -Depth 5
