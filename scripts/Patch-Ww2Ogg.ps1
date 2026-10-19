[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceDirectory, [Parameter(Mandatory)][string]$SourceArchive)
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath($SourceDirectory)
$archivePath = [IO.Path]::GetFullPath($SourceArchive)
$expectedArchiveHash = 'd562b85af4d8491e85ef6799bb57696efbcd86228692eb016c453a469beef501'
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ine $expectedArchiveHash) {
    throw 'The ww2ogg source archive differs from the pinned revision.'
}

function Replace-Once([string]$Text, [string]$Before, [string]$After) {
    $Text = $Text.Replace("`r`n", "`n")
    $Before = $Before.Replace("`r`n", "`n")
    $After = $After.Replace("`r`n", "`n")
    $at = $Text.IndexOf($Before, [StringComparison]::Ordinal)
    if ($at -lt 0 -or $Text.IndexOf($Before, $at + $Before.Length, [StringComparison]::Ordinal) -ge 0) {
        throw 'A ww2ogg source patch anchor is missing or repeated.'
    }
    return $Text.Substring(0, $at) + $After + $Text.Substring($at + $Before.Length)
}

function Patch-Main([string]$Text) {
    $includes = @'
#include "errors.h"
// deviant-ww2ogg-stdout-v1: binary output uses a separate stream from diagnostics.
#ifdef _WIN32
#include <stdio.h>
#include <io.h>
#include <fcntl.h>
#endif
'@
    $Text = Replace-Once $Text '#include "errors.h"' $includes
    $Text = Replace-Once $Text '    bool full_setup;' "    bool full_setup;`n    bool stdout_output;"
    $Text = Replace-Once $Text '                           full_setup(false),' "                           full_setup(false),`n                           stdout_output(false),"
    $Text = Replace-Once $Text '    bool get_full_setup(void) const {return full_setup;}' "    bool get_full_setup(void) const {return full_setup;}`n    bool get_stdout(void) const {return stdout_output;}"
    $Text = Replace-Once $Text 'input.wav [-o output.ogg]' 'input.wav [-o output.ogg | --stdout]'
    $main = @'
class Ww2OggStdoutDiagnostics
{
    streambuf * original;
public:
    explicit Ww2OggStdoutDiagnostics(bool enabled) : original(enabled ? cout.rdbuf(cerr.rdbuf()) : NULL) {}
    ~Ww2OggStdoutDiagnostics() { if (original) cout.rdbuf(original); }
};

int main(int argc, char **argv)
{
    bool binary_stdout = false;
    for (int i = 1; i < argc; ++i)
    {
        if (!strcmp(argv[i], "--stdout")) { binary_stdout = true; break; }
        if ((!strcmp(argv[i], "-o") || !strcmp(argv[i], "--pcb")) && i + 1 < argc) ++i;
    }
    streambuf * binary_buffer = cout.rdbuf();
    Ww2OggStdoutDiagnostics diagnostics(binary_stdout);
    ostream binary_output(binary_buffer);
#ifdef _WIN32
    if (binary_stdout && _setmode(_fileno(stdout), _O_BINARY) == -1)
    {
        cerr << "Could not set stdout to binary mode." << endl;
        return 1;
    }
#endif
'@
    $Text = Replace-Once $Text "int main(int argc, char **argv)`n{" $main
    $stdoutArgument = @'
        if (!strcmp(argv[i], "--stdout"))
        {
            if (stdout_output) throw Argument_error("--stdout may appear only once");
            stdout_output = true;
        }
        else if (!strcmp(argv[i], "-o"))
'@
    $Text = Replace-Once $Text '        if (!strcmp(argv[i], "-o"))' $stdoutArgument
    $conflict = @'
    if (stdout_output && set_output) throw Argument_error("--stdout cannot be combined with -o");

    if (!set_input)
'@
    $Text = Replace-Once $Text '    if (!set_input)' $conflict
    $Text = Replace-Once $Text '    if (!set_output)' '    if (!set_output && !stdout_output)'
    $Text = Replace-Once $Text '        cout << "Output: " << opt.get_out_filename() << endl;' '        cout << "Output: " << (opt.get_stdout() ? "stdout" : opt.get_out_filename()) << endl;'
    $before = @'
        ofstream of(opt.get_out_filename().c_str(), ios::binary);
        if (!of) throw File_open_error(opt.get_out_filename());

        ww.generate_ogg(of);
'@
    $after = @'
        if (opt.get_stdout())
        {
            ww.generate_ogg(binary_output);
            binary_output.flush();
            if (!binary_output)
            {
                cerr << "Could not write binary Ogg output." << endl;
                return 1;
            }
        }
        else
        {
            ofstream of(opt.get_out_filename().c_str(), ios::binary);
            if (!of) throw File_open_error(opt.get_out_filename());
            ww.generate_ogg(of);
        }
'@
    return Replace-Once $Text $before $after
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
$updates = [Collections.Generic.List[object]]::new()
try {
    foreach ($relative in @('src/ww2ogg.cpp', 'src/wwriff.cpp', 'src/wwriff.h')) {
        $members = @($archive.Entries | Where-Object { $_.FullName.EndsWith('/' + $relative, [StringComparison]::Ordinal) })
        if ($members.Count -ne 1) { throw 'The pinned ww2ogg source member is missing or repeated.' }
        $reader = [IO.StreamReader]::new($members[0].Open())
        try { $original = $reader.ReadToEnd().Replace("`r`n", "`n") } finally { $reader.Dispose() }
        $patched = switch ($relative) {
            'src/ww2ogg.cpp' { Patch-Main $original }
            'src/wwriff.cpp' { Replace-Once $original 'void Wwise_RIFF_Vorbis::generate_ogg(ofstream& of)' 'void Wwise_RIFF_Vorbis::generate_ogg(ostream& of)' }
            'src/wwriff.h' { Replace-Once $original '    void generate_ogg(ofstream& of);' '    void generate_ogg(ostream& of);' }
        }
        $target = [IO.Path]::GetFullPath((Join-Path $sourceRoot $relative))
        if (!$target.StartsWith($sourceRoot.TrimEnd([char[]]"\/") + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The ww2ogg patch target is outside its source directory.'
        }
        foreach ($candidate in @($sourceRoot, (Split-Path -Parent $target), $target)) {
            if ((Get-Item -LiteralPath $candidate -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'The ww2ogg source patch target cannot be a link.'
            }
        }
        $current = [IO.File]::ReadAllText($target)
        $normalized = $current.Replace("`r`n", "`n")
        if ($normalized -cne $original -and $normalized -cne $patched) {
            throw ('The ww2ogg source file has unexpected changes: ' + $relative)
        }
        if ($normalized -cne $patched) {
            $content = if ($current.Contains("`r`n")) { $patched.Replace("`n", "`r`n") } else { $patched }
            $updates.Add([PSCustomObject]@{ Path = $target; Content = $content })
        }
    }
} finally { $archive.Dispose() }
# Validate every source before editing any file. A partly applied prior run is safe to resume.
foreach ($update in $updates) { [IO.File]::WriteAllText($update.Path, $update.Content, [Text.UTF8Encoding]::new($false)) }
Write-Output 'ww2ogg binary stdout source patch is ready.'
