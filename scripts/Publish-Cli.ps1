[CmdletBinding()]
param([string] $Destination)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $repositoryRoot 'artifacts'
if (-not $Destination) { $Destination = Join-Path $artifactRoot 'Deviant.Cli' }
$destinationFull = [IO.Path]::GetFullPath($Destination)
$allowedPrefix = [IO.Path]::GetFullPath($artifactRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $destinationFull.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish destination must be a subdirectory of this repository artifacts directory.'
}
for ($ancestor = $destinationFull; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Publish destination cannot contain symbolic links or junctions.'
    }
}
$project = Join-Path $repositoryRoot 'src/Deviant.Cli/Deviant.Cli.csproj'
$publishRoot = Join-Path $artifactRoot ('.publish-' + [Guid]::NewGuid().ToString('N'))
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
foreach ($name in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot $name) -Destination (Join-Path $publishRoot $name)
}
$assets = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/Deviant.Cli/obj/project.assets.json') -Raw | ConvertFrom-Json
$noticeRoot = Join-Path $publishRoot 'notices'
New-Item -ItemType Directory -Path $noticeRoot -Force | Out-Null
foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
    $runtimeRoot = Join-Path $folder 'microsoft.netcore.app.runtime.win-x64'
    if (Test-Path -LiteralPath $runtimeRoot) {
        $runtimeVersion = (Get-Content -LiteralPath (Join-Path $publishRoot 'Deviant.Cli.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks[0].version
        foreach ($name in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
            $notice = Join-Path (Join-Path $runtimeRoot $runtimeVersion) $name
            if (Test-Path -LiteralPath $notice) { Copy-Item -LiteralPath $notice -Destination (Join-Path $noticeRoot ('dotnet-' + $name)) }
        }
    }
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package') { continue }
        $packagePath = Join-Path $folder $library.Value.path
        if (-not (Test-Path -LiteralPath $packagePath)) { continue }
        $metadataRoot = Join-Path $noticeRoot ($library.Name.Replace('/', '-'))
        New-Item -ItemType Directory -Path $metadataRoot -Force | Out-Null
        Get-ChildItem -LiteralPath $packagePath -File | Where-Object { $_.Name -match '(?i)license|notice|\.nuspec$' } |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $metadataRoot }
    }
}
$packageFiles = @(Get-ChildItem -LiteralPath $publishRoot -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{ file = [IO.Path]::GetRelativePath($publishRoot, $_.FullName).Replace('\', '/'); length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
[ordered]@{ version = 1; rid = 'win-x64'; selfContained = $true; files = $packageFiles } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $publishRoot 'package-manifest.json') -Encoding utf8
# Only explicitly verified artifact paths are moved; previous builds remain recoverable.
foreach ($target in @($destinationFull, $publishRoot)) {
    if (-not ([IO.Path]::GetFullPath($target)).StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Artifact path escaped its root.' }
}
if (Test-Path -LiteralPath $destinationFull) {
    $backup = Join-Path $artifactRoot ('.previous-' + [Guid]::NewGuid().ToString('N'))
    if (-not ([IO.Path]::GetFullPath($backup)).StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Backup path escaped its root.' }
    Move-Item -LiteralPath $destinationFull -Destination $backup
}
Move-Item -LiteralPath $publishRoot -Destination $destinationFull
Write-Output $destinationFull
