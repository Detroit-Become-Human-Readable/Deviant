[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Destination,
    [Parameter(Mandatory)][string] $BuildDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destinationRoot = [IO.Path]::GetFullPath($Destination)
$buildRoot = [IO.Path]::GetFullPath($BuildDirectory)
if ($buildRoot -match '\s') { throw 'Audio tool build directories cannot contain spaces.' }
New-Item -ItemType Directory -Path $destinationRoot,$buildRoot -Force | Out-Null

function Get-VerifiedDownload([string] $Uri, [string] $Path, [string] $Sha256) {
    Invoke-WebRequest -Uri $Uri -OutFile $Path
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256.ToLowerInvariant()) { throw "Download hash mismatch: $([IO.Path]::GetFileName($Path))" }
}

function Get-SourceArchive([string] $Uri, [string] $Path) {
    Invoke-WebRequest -Uri $Uri -OutFile $Path
    if (!(Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -eq 0) {
        throw "Source archive download failed: $([IO.Path]::GetFileName($Path))"
    }
}

$sevenZip = Join-Path $buildRoot '7zr.exe'
# Pin the release URL as well as the hash; the unversioned download changes on release.
Get-VerifiedDownload 'https://github.com/ip7z/7zip/releases/download/26.03/7zr.exe' $sevenZip 'ad4c82fadcbdf93c03b4fc440f300509c7d60c5c2f4d183e35d9d70d6957037d'
$toolchainArchive = Join-Path $buildRoot 'w64devkit-x64-2.10.0.7z.exe'
Get-VerifiedDownload 'https://github.com/skeeto/w64devkit/releases/download/v2.10.0/w64devkit-x64-2.10.0.7z.exe' $toolchainArchive '18d0a4c71a166f8401ab6305781bec5882b40b5e06ba9807c61cb5f3b3c6325e'
$toolchainParent = Join-Path $buildRoot 'toolchain'
New-Item -ItemType Directory -Path $toolchainParent -Force | Out-Null
& $sevenZip x $toolchainArchive "-o$toolchainParent" '-y' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not unpack the pinned Windows build toolchain.' }
$toolchain = Join-Path $toolchainParent 'w64devkit'
foreach ($tool in @('gcc.exe','g++.exe','cmake.exe','make.exe','sh.exe')) {
    if (!(Test-Path -LiteralPath (Join-Path $toolchain "bin/$tool") -PathType Leaf)) { throw "Build toolchain is missing $tool." }
}

$sources = Join-Path $buildRoot 'sources'
New-Item -ItemType Directory -Path $sources -Force | Out-Null
$archives = @(
    @{ file='ffmpeg.zip'; url='https://github.com/FFmpeg/FFmpeg/archive/330caae0c1acccd2222edc52a05940c574561ce5.zip' },
    @{ file='vgmstream.zip'; url='https://github.com/vgmstream/vgmstream/archive/7dc938fa2f210943b37c7b6511852b516ef432ab.zip' },
    @{ file='ww2ogg.zip'; url='https://github.com/hcs64/ww2ogg/archive/4b4ca8ec4079a42488aae6344c3c52165e210eec.zip' },
    @{ file='revorb.zip'; url='https://github.com/ItsBranK/ReVorb/archive/3b69589891a2200feb16beb9f082d31afed18044.zip' },
    @{ file='ogg.zip'; url='https://github.com/xiph/ogg/archive/refs/tags/v1.3.6.zip' },
    @{ file='vorbis.zip'; url='https://github.com/xiph/vorbis/archive/refs/tags/v1.3.7.zip' }
)
foreach ($archive in $archives) {
    Get-SourceArchive $archive.url (Join-Path $sources $archive.file)
}

$bundleSources = Join-Path $destinationRoot 'sources'
$bundleBuild = Join-Path $destinationRoot 'build'
New-Item -ItemType Directory -Path $bundleSources,$bundleBuild,(Join-Path $destinationRoot 'ffmpeg') -Force | Out-Null
Copy-Item -Path (Join-Path $sources '*') -Destination $bundleSources -Force
foreach ($name in @('Build-AudioTools.ps1','Build-RemuxTools.ps1','Build-Vgmstream.ps1','Seal-AudioTools.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $bundleBuild
}

$audioBuildRoot = Join-Path $buildRoot 'audio'
& (Join-Path $PSScriptRoot 'Build-AudioTools.ps1') -SourceDirectory $sources -ToolchainDirectory $toolchain `
    -Destination (Join-Path $destinationRoot 'ffmpeg') -BuildDirectory $audioBuildRoot

$vgmstreamCli = Join-Path $destinationRoot 'vgmstream/vgmstream-cli.exe'
& (Join-Path $PSScriptRoot 'Build-Vgmstream.ps1') -SourceArchive (Join-Path $sources 'vgmstream.zip') `
    -Destination (Join-Path $destinationRoot 'vgmstream') -CMakePath (Join-Path $toolchain 'bin/cmake.exe') `
    -BuildDirectory (Join-Path $buildRoot 'vgmstream')

& (Join-Path $PSScriptRoot 'Build-RemuxTools.ps1') -SourceDirectory $sources -ToolchainDirectory $toolchain `
    -CodecBuildDirectory $audioBuildRoot -Destination $destinationRoot -VgmstreamCli $vgmstreamCli `
    -BuildDirectory (Join-Path $buildRoot 'remux')

& (Join-Path $PSScriptRoot 'Seal-AudioTools.ps1') -Directory $destinationRoot
Write-Output $destinationRoot
