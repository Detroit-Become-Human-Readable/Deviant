[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceArchive,[Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][string]$CMakePath,[string]$BuildDirectory)
$ErrorActionPreference='Stop'
if (!$BuildDirectory) { $BuildDirectory = Join-Path ([IO.Path]::GetTempPath()) ('deviant-vgmstream-'+[Guid]::NewGuid().ToString('N')) }
$buildRoot=[IO.Path]::GetFullPath($BuildDirectory)
$destinationPath=[IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $buildRoot,$destinationPath -Force | Out-Null
$tree=Join-Path $buildRoot 'source'
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (!(Test-Path -LiteralPath $tree)) { [IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($SourceArchive),$tree) }
$source=(Get-ChildItem -LiteralPath $tree -Directory | Select-Object -First 1).FullName
$flags=@('-DBUILD_CLI=ON','-DBUILD_FB2K=OFF','-DBUILD_WINAMP=OFF','-DBUILD_XMPLAY=OFF','-DUSE_VORBIS=ON',
'-DUSE_MPEG=OFF','-DUSE_FFMPEG=OFF','-DUSE_G7221=OFF','-DUSE_G719=OFF','-DUSE_ATRAC9=OFF','-DUSE_CELT=OFF',
'-DUSE_SPEEX=OFF','-DCMAKE_C_FLAGS_RELEASE=/MT /O2 /DNDEBUG','-DCMAKE_CXX_FLAGS_RELEASE=/MT /O2 /DNDEBUG')
# MSVC flags and the cli/Release output path require a Visual Studio generator.
& $CMakePath -S $source -B (Join-Path $buildRoot 'build') -G 'Visual Studio 17 2022' -A x64 @flags
if ($LASTEXITCODE -ne 0) { throw 'vgmstream configuration failed' }
& $CMakePath --build (Join-Path $buildRoot 'build') --config Release --parallel 4
if ($LASTEXITCODE -ne 0) { throw 'vgmstream build failed' }
Copy-Item -LiteralPath (Join-Path $buildRoot 'build/cli/Release/vgmstream-cli.exe') -Destination $destinationPath
