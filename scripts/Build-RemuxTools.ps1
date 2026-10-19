[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceDirectory, [Parameter(Mandatory)][string]$ToolchainDirectory,
    [Parameter(Mandatory)][string]$CodecBuildDirectory, [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][string]$VgmstreamCli, [string]$BuildDirectory)
$ErrorActionPreference = 'Stop'
if (!$BuildDirectory) { $BuildDirectory = Join-Path ([IO.Path]::GetTempPath()) ('deviant-remux-' + [Guid]::NewGuid().ToString('N')) }
$buildRoot = [IO.Path]::GetFullPath($BuildDirectory)
$sourceRoot = [IO.Path]::GetFullPath($SourceDirectory)
$destinationRoot = [IO.Path]::GetFullPath($Destination)
$toolchain = [IO.Path]::GetFullPath($ToolchainDirectory)
$prefix = Join-Path ([IO.Path]::GetFullPath($CodecBuildDirectory)) 'prefix'
New-Item -ItemType Directory -Path $buildRoot,$destinationRoot -Force | Out-Null
$env:PATH = (Join-Path $toolchain 'bin') + ';' + $env:PATH
Add-Type -AssemblyName System.IO.Compression.FileSystem
$trees = @{}
foreach ($name in @('ww2ogg','revorb','vgmstream')) {
    $tree = Join-Path $buildRoot ($name + '-source')
    if (!(Test-Path -LiteralPath $tree)) { [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $sourceRoot ($name + '.zip')),$tree) }
    $trees[$name] = (Get-ChildItem -LiteralPath $tree -Directory | Select-Object -First 1).FullName
    New-Item -ItemType Directory -Path (Join-Path $destinationRoot $name) -Force | Out-Null
}
$ww = $trees['ww2ogg']
& (Join-Path $PSScriptRoot 'Patch-Ww2Ogg.ps1') -SourceDirectory $ww -SourceArchive (Join-Path $sourceRoot 'ww2ogg.zip')
& (Join-Path $toolchain 'bin/gcc.exe') -O2 -c (Join-Path $ww 'src/crc.c') -o (Join-Path $buildRoot 'crc.o')
if ($LASTEXITCODE -ne 0) { throw 'ww2ogg CRC build failed' }
& (Join-Path $toolchain 'bin/g++.exe') -O2 -std=c++98 (Join-Path $ww 'src/ww2ogg.cpp') (Join-Path $ww 'src/wwriff.cpp') (Join-Path $ww 'src/codebook.cpp') (Join-Path $buildRoot 'crc.o') -static -o (Join-Path $destinationRoot 'ww2ogg/ww2ogg.exe')
if ($LASTEXITCODE -ne 0) { throw 'ww2ogg build failed' }
Copy-Item -LiteralPath (Join-Path $ww 'packed_codebooks.bin'),(Join-Path $ww 'packed_codebooks_aoTuV_603.bin') -Destination (Join-Path $destinationRoot 'ww2ogg')
& (Join-Path $toolchain 'bin/g++.exe') -O2 -include cstdint (Join-Path $trees['revorb'] 'revorb.cpp') (Join-Path $prefix 'lib/libvorbis.a') (Join-Path $prefix 'lib/libogg.a') -static -o (Join-Path $destinationRoot 'revorb/ReVorb.exe')
if ($LASTEXITCODE -ne 0) { throw 'ReVorb build failed' }
$def = Join-Path $trees['vgmstream'] 'ext_libs/libvorbis.def'
& (Join-Path $toolchain 'bin/gcc.exe') -O2 -shared $def '-Wl,--whole-archive' (Join-Path $prefix 'lib/libvorbisfile.a') (Join-Path $prefix 'lib/libvorbis.a') (Join-Path $prefix 'lib/libogg.a') '-Wl,--no-whole-archive' -o (Join-Path $destinationRoot 'vgmstream/libvorbis.dll')
if ($LASTEXITCODE -ne 0) { throw 'libvorbis DLL build failed' }
$vgmstreamDestination = Join-Path $destinationRoot 'vgmstream/vgmstream-cli.exe'
if (![IO.Path]::GetFullPath($VgmstreamCli).Equals($vgmstreamDestination, [StringComparison]::OrdinalIgnoreCase)) {
    Copy-Item -LiteralPath $VgmstreamCli -Destination $vgmstreamDestination
}
Write-Output 'Remux tools built.'
