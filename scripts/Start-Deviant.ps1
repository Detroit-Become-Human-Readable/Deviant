[CmdletBinding()]
param([switch] $VerifyOnly)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $workspaceRoot 'artifacts'
$releasePath = Join-Path $artifactRoot 'current-release.json'
if (!(Test-Path -LiteralPath $releasePath -PathType Leaf)) { throw 'No current package is selected. Publish a verified package first.' }
$release = Get-Content -LiteralPath $releasePath -Raw | ConvertFrom-Json
if ($release.formatVersion -ne 1 -or [IO.Path]::IsPathRooted($release.package) -or $release.package -match ':' -or @($release.package -split '[\\/]' | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) {
    throw 'The current package selection is invalid.'
}
$packageRoot = [IO.Path]::GetFullPath((Join-Path $artifactRoot $release.package))
$executable = Join-Path $packageRoot 'Deviant.exe'
$manifestPath = Join-Path $packageRoot 'package-manifest.json'
foreach ($path in @($packageRoot, $executable, $manifestPath)) {
    if (!(Test-Path -LiteralPath $path)) { throw 'The current package is incomplete.' }
    for ($ancestor = $path; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The package path contains a link or junction.' }
    }
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$entry = @($manifest.files | Where-Object file -eq 'Deviant.exe')
$hash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
if ($entry.Count -ne 1 -or $entry[0].sha256 -ne $hash -or $release.executableSha256 -ne $hash) {
    throw 'The current executable differs from its verified package.'
}
if ($VerifyOnly) { Write-Output $executable; return }
Start-Process -FilePath $executable -WorkingDirectory $packageRoot -WindowStyle Normal
