param(
    [Parameter(Mandatory=$true)][string]$SourceDirectory,
    [Parameter(Mandatory=$true)][string]$BuildDirectory,
    [ValidateSet('Debug','Release')][string]$Configuration='Release'
)
$ErrorActionPreference='Stop'
$source=(Resolve-Path -LiteralPath $SourceDirectory).Path
$build=[IO.Path]::GetFullPath($BuildDirectory)
$cmakeCommand=Get-Command cmake -ErrorAction SilentlyContinue
$cmakePath=if($cmakeCommand){$cmakeCommand.Source}else{$null}
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if(-not (Test-Path -LiteralPath $vswhere)){throw 'Visual Studio C++ build tools are required for the model atlas library.'}
$installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | ConvertFrom-Json
if(-not $installation){throw 'Visual Studio C++ build tools are required for the model atlas library.'}
if(-not $cmakePath){$cmakePath=Join-Path $installation.installationPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'}
if(-not (Test-Path -LiteralPath $cmakePath)){throw 'CMake is required for the model atlas library.'}
$vcvars=Join-Path $installation.installationPath 'VC\Auxiliary\Build\vcvars64.bat'
if(-not (Test-Path -LiteralPath $vcvars)){throw 'The C++ compiler environment is unavailable.'}
$compilerEnvironment=& $env:ComSpec /d /s /c "call `"$vcvars`" >nul && set"
if($LASTEXITCODE -ne 0){throw 'The C++ compiler environment could not be initialized.'}
foreach($entry in $compilerEnvironment){
    if($entry -notmatch '^([^=]+)=(.*)$'){continue}
    $name=$matches[1];$value=$matches[2]
    if($name -in @('HOME','CODEX_HOME')){continue}
    [Environment]::SetEnvironmentVariable($name,$value,'Process')
}
& $cmakePath -S $source -B $build -G 'NMake Makefiles' "-DCMAKE_BUILD_TYPE=$Configuration"
if($LASTEXITCODE -ne 0){throw 'Model atlas configuration failed.'}
& $cmakePath --build $build --config $Configuration
if($LASTEXITCODE -ne 0){throw 'Model atlas compilation failed.'}
