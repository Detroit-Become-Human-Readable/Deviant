[CmdletBinding()]
param(
    [string] $Destination,
    [string] $PackageZip,
    [string] $SourceZip,
    [string] $StageDirectory,
    [string] $AudioToolsDirectory,
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Utility/Microsoft.PowerShell.Utility.psd1')
Add-Type -AssemblyName System.IO.Compression
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
if (-not $Destination) { $Destination = Join-Path $artifactRoot 'Deviant' }
if (-not $PackageZip) { $PackageZip = Join-Path $artifactRoot 'Deviant-win-x64.zip' }
if (-not $SourceZip) { $SourceZip = Join-Path $artifactRoot 'Deviant-source.zip' }
if (-not $StageDirectory) { $StageDirectory = [IO.Path]::GetTempPath() }

$destinationFull = [IO.Path]::GetFullPath($Destination)
$packageZipFull = [IO.Path]::GetFullPath($PackageZip)
$sourceZipFull = [IO.Path]::GetFullPath($SourceZip)
$releasePath = Join-Path $artifactRoot 'current-release.json'
$stageBase = [IO.Path]::GetFullPath($StageDirectory)
$artifactPrefix = $artifactRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

function Assert-ArtifactPath([string] $Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Published outputs must remain inside the repository artifacts directory.'
    }
    for ($ancestor = $full; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Published output paths cannot contain symbolic links or junctions.'
        }
    }
}

function Assert-NoReparseAncestors([string] $Path) {
    for ($ancestor = [IO.Path]::GetFullPath($Path); $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Staging paths cannot contain symbolic links or junctions.'
        }
    }
}

function Get-RelativePath([string] $Root, [string] $Path) {
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'A package path escaped its staging root.' }
    return $full.Substring($prefix.Length).Replace('\', '/')
}

function Write-Zip([string] $SourceRoot, [string] $ZipPath) {
    $archive = [IO.Compression.ZipFile]::Open($ZipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem -LiteralPath $SourceRoot -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
            $relative = Get-RelativePath $SourceRoot $_.FullName
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $archive.Dispose() }
}

foreach ($path in @($destinationFull, $packageZipFull, $sourceZipFull, $releasePath)) { Assert-ArtifactPath $path }
if ($destinationFull -eq $packageZipFull -or $destinationFull -eq $sourceZipFull -or $packageZipFull -eq $sourceZipFull) {
    throw 'The desktop directory, binary zip, and source zip must have distinct destinations.'
}
if ([IO.Path]::GetFileName($packageZipFull).Equals([IO.Path]::GetFileName($sourceZipFull), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The binary zip and source zip must have different file names.'
}
foreach ($pair in @(@($destinationFull, $packageZipFull), @($destinationFull, $sourceZipFull), @($packageZipFull, $sourceZipFull))) {
    $left = $pair[0].TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $right = $pair[1].TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($pair[0].StartsWith($right, [StringComparison]::OrdinalIgnoreCase) -or $pair[1].StartsWith($left, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Published outputs cannot be nested inside one another.'
    }
}
Assert-NoReparseAncestors $stageBase
New-Item -ItemType Directory -Path $stageBase -Force | Out-Null

$runId = [Guid]::NewGuid().ToString('N')
$stageRoot = Join-Path $stageBase ('deviant-desktop-publish-' + $runId)
$commitRoot = Join-Path $artifactRoot ('.desktop-package-' + $runId)
$backupRoot = Join-Path $artifactRoot ('.previous-desktop-' + $runId)
$publishRoot = Join-Path $stageRoot 'publish'
$buildRoot = Join-Path $stageRoot 'build'
$sourceRoot = Join-Path $stageRoot 'source'
$packageRoot = Join-Path $commitRoot 'Deviant'
$packageZipStage = Join-Path $commitRoot ([IO.Path]::GetFileName($packageZipFull))
$sourceZipStage = Join-Path $commitRoot ([IO.Path]::GetFileName($sourceZipFull))
$project = Join-Path $repositoryRoot 'src/Deviant.Desktop/Deviant.Desktop.csproj'
$projectAssets = Join-Path $buildRoot 'obj/Deviant.Desktop/project.assets.json'
$releaseStagePath = Join-Path $commitRoot 'current-release.json'
$publishedTargets = @(
    [ordered]@{ Stage = $packageRoot; Final = $destinationFull },
    [ordered]@{ Stage = $packageZipStage; Final = $packageZipFull },
    [ordered]@{ Stage = $sourceZipStage; Final = $sourceZipFull },
    [ordered]@{ Stage = $releaseStagePath; Final = $releasePath }
)
$backedUpTargets = [Collections.Generic.List[object]]::new()
$promotedTargets = [Collections.Generic.List[object]]::new()

try {
    Assert-NoReparseAncestors $stageRoot
    Assert-ArtifactPath $commitRoot
    Assert-ArtifactPath $backupRoot
    New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
    & dotnet publish $project -c $Configuration -r win-x64 --self-contained true --nologo -o $publishRoot `
        --artifacts-path $buildRoot -p:GenerateDesktopArtifacts=false -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }

    if (!$AudioToolsDirectory -and $env:DEVIANT_AUDIO_TOOLS) { $AudioToolsDirectory = $env:DEVIANT_AUDIO_TOOLS }
    if (!$AudioToolsDirectory) {
        $AudioToolsDirectory = Join-Path $repositoryRoot '.local/audio-tools'
        if (!(Test-Path -LiteralPath (Join-Path $AudioToolsDirectory 'manifest.json') -PathType Leaf)) {
            Write-Host 'Building audio tools for the first desktop package. Later builds reuse them.'
            & (Join-Path $PSScriptRoot 'Build-CiAudioTools.ps1') -Destination $AudioToolsDirectory `
                -BuildDirectory (Join-Path ([IO.Path]::GetTempPath()) 'deviant-audio-build')
        }
    }
    $audioTools = [IO.Path]::GetFullPath($AudioToolsDirectory)
    $toolManifestPath = Join-Path $audioTools 'manifest.json'
    if (!(Test-Path -LiteralPath $toolManifestPath)) { throw 'Prepare the verified audio tool bundle before publishing.' }
    $toolManifest = Get-Content -LiteralPath $toolManifestPath -Raw | ConvertFrom-Json
    foreach ($entry in $toolManifest.files) {
        if ([IO.Path]::IsPathRooted($entry.file) -or $entry.file.Contains('..') -or $entry.file.Contains(':')) { throw 'Unsafe codec manifest path.' }
        $toolFile = Join-Path $audioTools $entry.file
        if (!(Test-Path -LiteralPath $toolFile -PathType Leaf) -or (Get-FileHash -LiteralPath $toolFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { throw ('Codec bundle verification failed: '+$entry.file) }
    }
    foreach ($name in @('ffmpeg','vgmstream','ww2ogg','revorb')) {
        $component = @($toolManifest.components | Where-Object name -eq $name)
        if ($component.Count -ne 1 -or !$component[0].sourceArchive -or !$component[0].sourceSha256 -or !$component[0].buildMaterial) { throw ('Missing corresponding codec material: '+$name) }
    }
    Assert-NoReparseAncestors $audioTools
    Copy-Item -LiteralPath $audioTools -Destination (Join-Path $publishRoot 'tools') -Recurse

    foreach ($name in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot $name) -Destination (Join-Path $publishRoot $name)
    }

    $applicationExecutables = @(Get-ChildItem -LiteralPath $publishRoot -File -Filter '*.exe')
    $looseRuntimeFiles = @(Get-ChildItem -LiteralPath $publishRoot -File | Where-Object {
        $_.Extension -in @('.dll', '.pdb') -or $_.Name -like '*.deps.json' -or $_.Name -like '*.runtimeconfig.json'
    })
    if ($applicationExecutables.Count -ne 1 -or $applicationExecutables[0].Name -ne 'Deviant.exe' -or $looseRuntimeFiles.Count -ne 0) {
        throw 'The desktop package must contain Deviant.exe with its application runtime bundled inside.'
    }

    $assets = Get-Content -LiteralPath $projectAssets -Raw | ConvertFrom-Json
    $noticeRoot = Join-Path $publishRoot 'notices'
    New-Item -ItemType Directory -Path $noticeRoot -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'notices') -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $noticeRoot
    }
    $runtimeConfigs = @(Get-ChildItem -LiteralPath (Join-Path $buildRoot 'bin/Deviant.Desktop') -Filter 'Deviant.runtimeconfig.json' -File -Recurse)
    if ($runtimeConfigs.Count -ne 1) { throw 'Could not locate the desktop runtime configuration.' }
    $runtimeConfig = Get-Content -LiteralPath $runtimeConfigs[0].FullName -Raw | ConvertFrom-Json
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
            $runtimePackage = switch ($framework.name) {
                'Microsoft.NETCore.App' { 'microsoft.netcore.app.runtime.win-x64' }
                'Microsoft.WindowsDesktop.App' { 'microsoft.windowsdesktop.app.runtime.win-x64' }
                default { $null }
            }
            if (-not $runtimePackage) { continue }
            $runtimePath = Join-Path (Join-Path $folder $runtimePackage) $framework.version
            if (Test-Path -LiteralPath $runtimePath) {
                Get-ChildItem -LiteralPath $runtimePath -File -Recurse | Where-Object { $_.Name -match '(?i)^license|^third-party-notices' } | ForEach-Object {
                    $targetName = $runtimePackage + '-' + $_.Name
                    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $noticeRoot $targetName) -Force
                }
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
        [ordered]@{ file = (Get-RelativePath $publishRoot $_.FullName); length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $applicationMetadata = (Get-Item -LiteralPath (Join-Path $publishRoot 'Deviant.exe')).VersionInfo
    [ordered]@{ product = 'Deviant'; version = $applicationMetadata.ProductVersion; publisher = $applicationMetadata.CompanyName; formatVersion = 1; targetFramework = 'net10.0-windows'; rid = 'win-x64'; configuration = $Configuration; selfContained = $true; files = $packageFiles } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $publishRoot 'package-manifest.json') -Encoding utf8

    New-Item -ItemType Directory -Path $sourceRoot -Force | Out-Null
    $excludedSegments = @('.git', '.local', '.codex', '.agents', '.aws', '.vs', 'bin', 'obj', 'artifacts', 'TestResults', 'assets', 'node_modules')
    $excludedExtensions = @('.user', '.suo', '.db', '.db-wal', '.db-shm', '.dll', '.exe', '.pdb', '.cache', '.log', '.tmp', '.trx', '.nupkg', '.zip')
    $publicRootFiles = @('.gitignore', 'Directory.Build.props', 'Deviant.slnx', 'LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.md')
    $publicDirectories = @('scripts', 'src', 'tools', 'notices')
    $productAssets = @('src/Deviant.Desktop/Assets/app-icon.png', 'src/Deviant.Desktop/Assets/app-icon.ico', 'src/Deviant.Desktop/Assets/processing.gif')
    $sourceFiles = [Collections.Generic.List[IO.FileInfo]]::new()
    foreach ($name in $publicRootFiles) {
        $path = Join-Path $repositoryRoot $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { $sourceFiles.Add((Get-Item -LiteralPath $path)) }
    }
    foreach ($directory in $publicDirectories) {
        $path = Join-Path $repositoryRoot $directory
        if (-not (Test-Path -LiteralPath $path -PathType Container)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $path -File -Recurse -Force) {
            $relative = Get-RelativePath $repositoryRoot $file.FullName
            $segments = $relative.Split('/')
            if (($segments | Where-Object { $excludedSegments -icontains $_ }) -and $productAssets -notcontains $relative) { continue }
            if ($excludedExtensions -icontains $file.Extension) { continue }
            $sourceFiles.Add($file)
        }
    }
    foreach ($file in $sourceFiles) {
        $relative = Get-RelativePath $repositoryRoot $file.FullName
        $target = Join-Path $sourceRoot $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    $sourceManifestFiles = @(Get-ChildItem -LiteralPath $sourceRoot -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{ file = (Get-RelativePath $sourceRoot $_.FullName); length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    [ordered]@{ product = 'Deviant source'; formatVersion = 1; excludedPathSegments = $excludedSegments; includedProductAssets = $productAssets; files = $sourceManifestFiles } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $sourceRoot 'source-manifest.json') -Encoding utf8

    New-Item -ItemType Directory -Path $commitRoot -Force | Out-Null
    Copy-Item -LiteralPath $publishRoot -Destination $packageRoot -Recurse
    $executableHash = (Get-FileHash -LiteralPath (Join-Path $packageRoot 'Deviant.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    [ordered]@{ formatVersion = 1; package = (Get-RelativePath $artifactRoot $destinationFull); executableSha256 = $executableHash } |
        ConvertTo-Json | Set-Content -LiteralPath $releaseStagePath -Encoding utf8
    Write-Zip $packageRoot $packageZipStage
    Write-Zip $sourceRoot $sourceZipStage

    try {
        foreach ($target in $publishedTargets) {
            Assert-ArtifactPath $target.Final
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target.Final)) -Force | Out-Null
            Assert-ArtifactPath $target.Final
            if (-not ([IO.Path]::GetFullPath($target.Stage)).StartsWith([IO.Path]::GetFullPath($commitRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'A staged publish path escaped its generated artifact directory.'
            }
            if (Test-Path -LiteralPath $target.Final) {
                New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
                $backupPath = Join-Path $backupRoot ([IO.Path]::GetFileName($target.Final))
                Move-Item -LiteralPath $target.Final -Destination $backupPath
                $backedUpTargets.Add([ordered]@{ Final = $target.Final; Backup = $backupPath })
            }
        }
        foreach ($target in $publishedTargets) {
            Move-Item -LiteralPath $target.Stage -Destination $target.Final
            $promotedTargets.Add($target)
        }
    }
    catch {
        foreach ($target in $promotedTargets) {
            if (Test-Path -LiteralPath $target.Final) { Move-Item -LiteralPath $target.Final -Destination ($target.Stage + '.recovery') }
        }
        foreach ($target in $backedUpTargets) {
            if (Test-Path -LiteralPath $target.Backup) { Move-Item -LiteralPath $target.Backup -Destination $target.Final }
        }
        throw
    }
    Write-Output $destinationFull
    Write-Output $packageZipFull
    Write-Output $sourceZipFull
    if ($backedUpTargets.Count -gt 0) { Write-Output ("Previous outputs preserved at " + $backupRoot) }
}
finally {
    foreach ($temporary in @($stageRoot, $commitRoot)) {
        if (Test-Path -LiteralPath $temporary) {
            $full = [IO.Path]::GetFullPath($temporary)
            if ($full.StartsWith([IO.Path]::GetFullPath($stageBase).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                $full.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                Assert-NoReparseAncestors $full
                Remove-Item -LiteralPath $full -Recurse -Force
            }
        }
    }
}
