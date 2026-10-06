[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SourceRoot,

    [Parameter(Mandatory = $true)]
    [string] $Destination
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

function Get-TreeState([string] $Root) {
    $listed = Invoke-GitRaw $Root @('ls-files', '--cached', '--others', '--exclude-standard', '-z')
    $pathSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $pathList = [System.Collections.Generic.List[string]]::new()
    foreach ($path in $listed.Split([char]0, [System.StringSplitOptions]::RemoveEmptyEntries)) {
        if ($pathSet.Add($path)) { $pathList.Add($path) }
    }
    $pathList.Sort([System.StringComparer]::Ordinal)
    $paths = $pathList.ToArray()
    $status = Invoke-GitRaw $Root @('status', '--porcelain=v2', '--branch')
    $head = (Invoke-GitRaw $Root @('rev-parse', 'HEAD')).Trim()
    $remotes = Invoke-GitRaw $Root @('remote', '-v')
    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($relative in $paths) {
        $full = [System.IO.Path]::GetFullPath((Join-Path $Root $relative))
        $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        if (-not $full.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Git returned an unsafe path: $relative" }
        $segments = $relative -split '[\\/]'
        $ancestor = $Root
        $blocked = $false
        for ($segmentIndex = 0; $segmentIndex -lt ($segments.Length - 1); $segmentIndex++) {
            $ancestor = Join-Path $ancestor $segments[$segmentIndex]
            $ancestorItem = Get-Item -LiteralPath $ancestor -Force -ErrorAction SilentlyContinue
            if ($null -ne $ancestorItem -and ($ancestorItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { $blocked = $true; break }
        }
        if ($blocked) {
            $hash = Get-ByteHash ([System.Text.Encoding]::UTF8.GetBytes($ancestor))
            $entries.Add([pscustomobject]@{ path = $relative; kind = 'reparse-ancestor'; length = $null; sha256 = $hash })
            continue
        }
        $item = Get-Item -LiteralPath $full -Force -ErrorAction SilentlyContinue
        if ($null -eq $item) {
            $entries.Add([pscustomobject]@{ path = $relative; kind = 'deleted'; length = $null; sha256 = $null })
            continue
        }
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            # Do not resolve or read through links/junctions. Preserve their presence as metadata.
            $target = ''
            if ($item.PSObject.Properties.Name -contains 'Target' -and $null -ne $item.Target) {
                $target = [string](@($item.Target) -join '|')
            }
            $hash = Get-ByteHash ([System.Text.Encoding]::UTF8.GetBytes($target))
            $entries.Add([pscustomobject]@{ path = $relative; kind = 'reparse-point'; length = $null; sha256 = $hash; target = $target })
            continue
        }
        $hash = Get-FileHash -LiteralPath $full -Algorithm SHA256
        $entries.Add([pscustomobject]@{ path = $relative; kind = 'file'; length = $item.Length; sha256 = $hash.Hash.ToLowerInvariant() })
    }
    $entries = $entries.ToArray()
    $canonical = [System.Text.StringBuilder]::new()
    [void]$canonical.Append($head).Append("`n")
    [void]$canonical.Append($remotes.Replace("`r`n", "`n").TrimEnd("`n")).Append("`n")
    [void]$canonical.Append($status.Replace("`r`n", "`n").TrimEnd("`n")).Append("`n")
    foreach ($entry in $entries) {
        [void]$canonical.Append($entry.path).Append("`t").Append($entry.kind).Append("`t").Append($entry.length).Append("`t").Append($entry.sha256)
        if ($entry.PSObject.Properties.Name -contains 'target') { [void]$canonical.Append("`t").Append($entry.target) }
        [void]$canonical.Append("`n")
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($canonical.ToString())
    $digest = Get-ByteHash $bytes
    [pscustomobject]@{ Status = $status; Head = $head; Remotes = $remotes; Entries = $entries; Digest = $digest }
}

$source = (Resolve-Path -LiteralPath $SourceRoot).Path.TrimEnd([System.IO.Path]::DirectorySeparatorChar)
$destParent = Split-Path -Parent $Destination
if (-not (Test-Path -LiteralPath $destParent)) { New-Item -ItemType Directory -Force -Path $destParent | Out-Null }
$destinationFull = [System.IO.Path]::GetFullPath($Destination)
if ($destinationFull.StartsWith($source + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Destination must be outside SourceRoot.'
}
if (Test-Path -LiteralPath $destinationFull) { throw "Destination already exists: $destinationFull" }

$before = Get-TreeState $source
$snapshotId = $before.Digest
New-Item -ItemType Directory -Path $destinationFull | Out-Null
foreach ($entry in $before.Entries) {
    if ($entry.kind -ne 'file') { continue }
    $sourcePath = Join-Path $source $entry.path
    $targetPath = Join-Path $destinationFull $entry.path
    $parent = Split-Path -Parent $targetPath
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination $targetPath
    $copyHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($copyHash -ne $entry.sha256) { throw "Copied file hash mismatch: $($entry.path)" }
}
$after = Get-TreeState $source
if ($before.Digest -ne $after.Digest) { throw 'Source tree changed during snapshot; discard this snapshot and retry.' }

$destinationManifest = foreach ($entry in $before.Entries) {
    if ($entry.kind -eq 'file') {
        $copy = Get-Item -LiteralPath (Join-Path $destinationFull $entry.path)
        $copyHash = (Get-FileHash -LiteralPath $copy.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($copyHash -ne $entry.sha256 -or $copy.Length -ne $entry.length) { throw "Snapshot verification failed: $($entry.path)" }
    }
    $entry
}
$manifest = [ordered]@{
    format = 'deviant-working-tree-snapshot-v1'
    snapshotId = $snapshotId
    sourceRootLabel = 'caller-supplied SourceRoot'
    createdUtc = [DateTime]::UtcNow.ToString('o')
    sourceStateSha256 = $before.Digest
    gitHead = $before.Head
    gitRemotes = $before.Remotes
    gitStatusPorcelainV2 = $before.Status
    pathCount = @($destinationManifest).Count
    entries = @($destinationManifest)
    exclusions = @('Git-ignored untracked paths, including ignored build/cache output, because git ls-files --exclude-standard excludes them.', 'Tracked files remain included even when an ignore rule matches their path.', 'Reparse points and paths beneath them are recorded as metadata and are never followed or copied.', 'Git administrative storage (.git) is never enumerated.')
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $destinationFull 'manifest.json') -Encoding UTF8
Write-Output "SnapshotId=$snapshotId"
Write-Output "Manifest=$(Join-Path $destinationFull 'manifest.json')"
Write-Output "Entries=$(@($destinationManifest).Count)"
