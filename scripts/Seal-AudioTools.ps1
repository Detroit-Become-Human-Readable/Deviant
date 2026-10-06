[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($Directory)
$rootPrefix=$root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$components=@(
    [ordered]@{ name='ffmpeg'; version='8.1.3 / 330caae0c1acccd2222edc52a05940c574561ce5'; source='https://github.com/FFmpeg/FFmpeg/tree/330caae0c1acccd2222edc52a05940c574561ce5'; sourceArchive='sources/ffmpeg.zip'; license='LGPL-2.1-or-later'; buildMaterial='build/Build-AudioTools.ps1'; profile='Shared libraries; PCM WAV, Ogg Vorbis and audio filters; GPL/nonfree/version3 disabled' },
    [ordered]@{ name='vgmstream'; version='7dc938fa2f210943b37c7b6511852b516ef432ab'; source='https://github.com/vgmstream/vgmstream/tree/7dc938fa2f210943b37c7b6511852b516ef432ab'; sourceArchive='sources/vgmstream.zip'; license='ISC-style and bundled notices'; buildMaterial='build/Build-Vgmstream.ps1'; profile='Built-in codecs and Vorbis; MPEG/FFmpeg/G7221/G719/ATRAC9/CELT/Speex disabled; MSVC /MT /O2 /DNDEBUG' },
    [ordered]@{ name='ww2ogg'; version='0.24 / 4b4ca8ec4079a42488aae6344c3c52165e210eec'; source='https://github.com/hcs64/ww2ogg/tree/4b4ca8ec4079a42488aae6344c3c52165e210eec'; sourceArchive='sources/ww2ogg.zip'; license='BSD-3-Clause'; buildMaterial='build/Build-RemuxTools.ps1'; profile='MinGW x64, static runtime; bundled codebooks from the same source archive' },
    [ordered]@{ name='revorb'; version='v1.0 / 3b69589891a2200feb16beb9f082d31afed18044'; source='https://github.com/ItsBranK/ReVorb/tree/3b69589891a2200feb16beb9f082d31afed18044'; sourceArchive='sources/revorb.zip'; license='ISC-style'; buildMaterial='build/Build-RemuxTools.ps1'; profile='MinGW x64, static runtime; compiler force-includes cstdint; libogg 1.3.6 and libvorbis 1.3.7' },
    [ordered]@{ name='libogg'; version='1.3.6'; source='https://github.com/xiph/ogg/tree/v1.3.6'; sourceArchive='sources/ogg.zip'; license='BSD-3-Clause'; buildMaterial='build/Build-AudioTools.ps1'; profile='Static dependency for FFmpeg, ReVorb and the vgmstream Vorbis DLL' },
    [ordered]@{ name='libvorbis'; version='1.3.7'; source='https://github.com/xiph/vorbis/tree/v1.3.7'; sourceArchive='sources/vorbis.zip'; license='BSD-3-Clause'; buildMaterial='build/Build-AudioTools.ps1'; profile='Static dependency for FFmpeg/ReVorb; rebuilt DLL exports for vgmstream' }
)
foreach ($component in $components) {
    $source=Join-Path $root $component.sourceArchive
    if (!(Test-Path -LiteralPath $source)) { throw ('Missing codec source: '+$component.name) }
    $component.sourceSha256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
}
$files=@(Get-ChildItem -LiteralPath $root -File -Recurse | Where-Object Name -ne 'manifest.json' | Sort-Object FullName | ForEach-Object {
    [ordered]@{ file=$_.FullName.Substring($rootPrefix.Length).Replace('\','/'); length=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
[ordered]@{ formatVersion=1; components=$components; buildToolchain='w64devkit 2.10.0 (GCC 16.2.0, CMake 4.4.3); vgmstream CLI MSVC 19.51.36260.0, CMake 4.3.1'; files=$files } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'manifest.json') -Encoding utf8
