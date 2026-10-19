[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceDirectory, [Parameter(Mandatory)][string]$ToolchainDirectory,
    [Parameter(Mandatory)][string]$Destination, [string]$BuildDirectory)
$ErrorActionPreference = 'Stop'
$sources = [IO.Path]::GetFullPath($SourceDirectory)
$toolchain = [IO.Path]::GetFullPath($ToolchainDirectory)
$destinationPath = [IO.Path]::GetFullPath($Destination)
if (!$BuildDirectory) { $BuildDirectory = Join-Path ([IO.Path]::GetTempPath()) ('deviant-codec-build-' + [Guid]::NewGuid().ToString('N')) }
$buildRoot = [IO.Path]::GetFullPath($BuildDirectory)
if ($buildRoot -match '\s') { throw 'FFmpeg requires a build directory without spaces.' }
New-Item -ItemType Directory -Path $buildRoot,$destinationPath -Force | Out-Null
$env:PATH = (Join-Path $toolchain 'bin') + ';' + $env:PATH
$cmake = Join-Path $toolchain 'bin/cmake.exe'
$compiler = (Join-Path $toolchain 'bin/gcc.exe').Replace('\','/')
$prefix = (Join-Path $buildRoot 'prefix').Replace('\','/')
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($name in @('ogg','vorbis','ffmpeg')) {
    $tree = Join-Path $buildRoot ($name + '-source')
    if (!(Test-Path -LiteralPath $tree)) { [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $sources ($name + '.zip')), $tree) }
    $source = (Get-ChildItem -LiteralPath $tree -Directory | Select-Object -First 1).FullName
    if ($name -ne 'ffmpeg') {
        $build = Join-Path $buildRoot ($name + '-build')
        & $cmake -S $source -B $build -G 'MinGW Makefiles' "-DCMAKE_C_COMPILER=$compiler" "-DCMAKE_INSTALL_PREFIX=$prefix" '-DCMAKE_BUILD_TYPE=Release' '-DCMAKE_POLICY_VERSION_MINIMUM=3.5' '-DBUILD_SHARED_LIBS=OFF' '-DBUILD_TESTING=OFF' "-DCMAKE_PREFIX_PATH=$prefix"
        if ($LASTEXITCODE -ne 0) { throw "$name configure failed" }
        & $cmake --build $build --parallel 4
        if ($LASTEXITCODE -ne 0) { throw "$name build failed" }
        & $cmake --install $build
        if ($LASTEXITCODE -ne 0) { throw "$name install failed" }
    } else { $ffmpegSource = $source }
}
$env:PKG_CONFIG_PATH = "$prefix/lib/pkgconfig"
$configuration = @('--disable-autodetect','--disable-gpl','--disable-nonfree','--disable-version3',
    '--disable-debug','--disable-doc','--disable-network','--disable-x86asm','--disable-static','--enable-shared',
    '--disable-everything','--enable-ffmpeg','--enable-ffprobe','--enable-avfilter','--enable-swresample',
    '--enable-libvorbis','--enable-encoder=libvorbis,pcm_s16le,pcm_s24le,pcm_f32le',
    '--enable-decoder=vorbis,pcm_s16le,pcm_s24le,pcm_s32le,pcm_f32le,pcm_f64le',
    '--enable-demuxer=wav,ogg','--enable-muxer=wav,ogg','--enable-parser=vorbis',
    '--enable-protocol=file,pipe','--enable-filter=anull,aformat,aresample,amix,adelay,atrim,asetpts,volume,apad,concat,pan,asetrate,aloop,afade',
    '--target-os=mingw32','--arch=x86_64','--cc=gcc','--cxx=g++','--prefix=./installed','--pkg-config-flags=--static')
$ffmpegBuild = Join-Path $buildRoot 'ffmpeg-build'
New-Item -ItemType Directory -Path $ffmpegBuild -Force | Out-Null
Push-Location $ffmpegBuild
try {
    & (Join-Path $toolchain 'bin/sh.exe') ($ffmpegSource.Replace('\','/') + '/configure') @configuration
    if ($LASTEXITCODE -ne 0) { throw 'FFmpeg configure failed' }
    & (Join-Path $toolchain 'bin/make.exe') -j4 'V=1' 'SHELL=sh.exe'
    if ($LASTEXITCODE -ne 0) { throw 'FFmpeg build failed' }
    & (Join-Path $toolchain 'bin/make.exe') install 'V=1' 'SHELL=sh.exe'
    if ($LASTEXITCODE -ne 0) { throw 'FFmpeg install failed' }
} finally { Pop-Location }
Get-ChildItem -LiteralPath (Join-Path $ffmpegBuild 'installed/bin') -File | Where-Object { $_.Extension -in '.exe','.dll' } | Copy-Item -Destination $destinationPath
$configuration | Set-Content -LiteralPath (Join-Path $destinationPath 'configure-arguments.txt')
Copy-Item -LiteralPath (Join-Path $ffmpegBuild 'config.h'),(Join-Path $ffmpegBuild 'config_components.h'),(Join-Path $ffmpegBuild 'ffbuild/config.mak') -Destination $destinationPath
Write-Output 'Audio tools built.'
