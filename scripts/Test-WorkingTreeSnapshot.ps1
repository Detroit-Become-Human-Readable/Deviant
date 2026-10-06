[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $SourceRoot,
    [Parameter(Mandatory = $true)] [string] $ManifestPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-GitRaw([string] $Root, [string[]] $Arguments) {
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'git'
    $start.WorkingDirectory = $Root
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $start.Arguments = ($Arguments | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
    }) -join ' '
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw 'Could not start git.' }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw "git $($Arguments -join ' ') failed: $stderr" }
    return $stdout
}
function Get-ByteHash([byte[]] $Bytes) {
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose() }
}

$root = (Resolve-Path -LiteralPath $SourceRoot).Path.TrimEnd([System.IO.Path]::DirectorySeparatorChar)
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$listed = Invoke-GitRaw $root @('ls-files', '--cached', '--others', '--exclude-standard', '-z')
$pathSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$pathList = [System.Collections.Generic.List[string]]::new()
foreach ($path in $listed.Split([char]0, [System.StringSplitOptions]::RemoveEmptyEntries)) {
    if ($pathSet.Add($path)) { $pathList.Add($path) }
}
$pathList.Sort([System.StringComparer]::Ordinal)
$paths = $pathList.ToArray()
$status = Invoke-GitRaw $root @('status', '--porcelain=v2', '--branch')
$head = (Invoke-GitRaw $root @('rev-parse', 'HEAD')).Trim()
$remotes = Invoke-GitRaw $root @('remote', '-v')
$expectedPaths = @($manifest.entries | ForEach-Object { $_.path })
$mismatches = [System.Collections.Generic.List[string]]::new()
if ($head -cne $manifest.gitHead) { $mismatches.Add('HEAD changed.') }
if ($remotes -cne $manifest.gitRemotes) { $mismatches.Add('Remote definitions changed.') }
if ($status -cne $manifest.gitStatusPorcelainV2) { $mismatches.Add('Git status changed.') }
if ($paths.Count -ne $expectedPaths.Count) { $mismatches.Add("Path count changed from $($expectedPaths.Count) to $($paths.Count).") }

$entryMap = @{}
foreach ($entry in $manifest.entries) { $entryMap[[string]$entry.path] = $entry }
foreach ($relative in $paths) {
    if (-not $entryMap.ContainsKey($relative)) { $mismatches.Add("New path: $relative"); continue }
    $entry = $entryMap[$relative]
    $full = [System.IO.Path]::GetFullPath((Join-Path $root $relative))
    $rootFull = $root + [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) { $mismatches.Add("Unsafe path in Git listing: $relative"); continue }
    $segments = $relative -split '[\\/]'
    $ancestor = $root
    $blocked = $false
    for ($segmentIndex = 0; $segmentIndex -lt ($segments.Length - 1); $segmentIndex++) {
        $ancestor = Join-Path $ancestor $segments[$segmentIndex]
        $ancestorItem = Get-Item -LiteralPath $ancestor -Force -ErrorAction SilentlyContinue
        if ($null -ne $ancestorItem -and ($ancestorItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { $blocked = $true; break }
    }
    if ($blocked) {
        if ($entry.kind -ne 'reparse-ancestor' -or (Get-ByteHash ([System.Text.Encoding]::UTF8.GetBytes($ancestor))) -cne $entry.sha256) { $mismatches.Add("Reparse ancestor changed: $relative") }
        continue
    }
    $item = Get-Item -LiteralPath $full -Force -ErrorAction SilentlyContinue
    if ($null -eq $item) {
        if ($entry.kind -ne 'deleted') { $mismatches.Add("Path disappeared: $relative") }
        continue
    }
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        if ($entry.kind -ne 'reparse-point') { $mismatches.Add("Path became a reparse point: $relative"); continue }
        $target = ''
        if ($item.PSObject.Properties.Name -contains 'Target' -and $null -ne $item.Target) { $target = [string](@($item.Target) -join '|') }
        if ((Get-ByteHash ([System.Text.Encoding]::UTF8.GetBytes($target))) -cne $entry.sha256) { $mismatches.Add("Reparse target changed: $relative") }
        continue
    }
    if ($entry.kind -ne 'file') { $mismatches.Add("Path kind changed: $relative"); continue }
    $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($item.Length -ne $entry.length -or $hash -cne $entry.sha256) { $mismatches.Add("Content changed: $relative") }
}

$result = [ordered]@{ matches = ($mismatches.Count -eq 0); snapshotId = $manifest.snapshotId; checkedPathCount = $paths.Count; mismatches = @($mismatches) }
$result | ConvertTo-Json -Depth 5
if ($mismatches.Count -ne 0) { exit 1 }
