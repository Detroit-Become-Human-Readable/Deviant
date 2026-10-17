# Deviant
<p align="center">
  <img
    src="https://github.com/user-attachments/assets/b6f4a03f-a6ba-4240-aafd-710982a67787"
    alt="Deviant header"
    width="1280"
  />
</p>

<p align="center">
    [WIP] Browse, preview, and export game assets from Quantic Dream's *Detroit: Become Human*
</p>

## Requirements

1. Windows (64-bit).
2. A legal copy of *Detroit: Become Human*

## Getting started

1. Download the Windows package and extract the ZIP.
2. Run `Deviant.exe`.
3. Select your installation on Home, or open an archive index from Advanced.

The download includes the runtime and audio utilities. Keep the extracted files together, including the `tools` folder.

## Features

1. Browse archives and search or filter resources.
2. Play audio and export original files, WAV, or OGG.
3. Search strings, compare languages, and export JSON or CSV.
4. Preview textures and DDS images, inspect channels and mip levels, and export PNG or original files.
5. View supported scenes and models in 3D.
6. Follow resource references in a list or graph.
7. Inspect shaders, animation records, and raw data.
8. Save bookmarks, collections, aliases, and filters.

## Using the app

After indexing finishes, choose a section on Home or in Explorer, then select an item to preview or inspect it. Use its export actions to save the files you want.

For the full archive list, open Advanced:

1. Choose **Open index** or **Open folder**. A folder must contain exactly one index.
2. Filter by package, type, ID, or text.
3. Select resources, then choose **Export selected** or **Export all matching**.
4. Review the file count and size, then choose an output folder.

Existing files are kept unless you choose to replace them. Cancelling an export keeps completed files. The export report lists completed, failed, cancelled, and skipped files.

In Audio, press Space to play or pause and Left/Right to seek. Save alias edits when you're done.

Cache settings let you change the cache location, limit preview storage, and clear or rebuild cached data. Saved aliases stay in your library.

## Command Line

Pass an `.idx` file or a folder containing exactly one `.idx` file with `--input`.

```text
deviant list --input <index-or-directory> [filters] [--offset 0] [--limit 100] [--json]
deviant inspect --input <index-or-directory> [filters] [--ordinal <n[,n...]>] [--json]
deviant verify --input <index-or-directory> [--full-content] [--json]
deviant extract --input <index-or-directory> --output <directory> (--all | filters) [--ordinal <n[,n...]>] [--overwrite] [--json]
```

`--type`, `--id`, `--package`
Filter by a decimal or `0x` hexadecimal value.

`--search <text>`
Search catalog metadata and volume names.

`--ordinal <n[,n...]>`
Select records by their zero-based ordinal. You can repeat this option or separate values with commas.

`--all`
Extract every resource. Otherwise, provide at least one filter or ordinal.

`--overwrite`
Replace existing output files.

`--full-content`
Hash the index, referenced volumes, and present sidecars during verification.

`--cache <directory>`
Choose a cache location. The default is `%LOCALAPPDATA%/Deviant/cache`. Keep cache and export folders outside the archive source folder.

`--json`
Print machine-readable output.

`--cancel-after <milliseconds>`
Cancel after the given time. Ctrl+C also cancels an operation.

Exit codes are `0` for success, `1` for usage or source errors, `2` for partial or invalid results, and `130` for a cancelled export with a completed manifest.

## Building from source

Requires the .NET 10 SDK and Windows.

```powershell
dotnet build Deviant.slnx -c Release
```

The build creates:

- `artifacts/Deviant/Deviant.exe`
- `artifacts/Deviant-win-x64.zip`
- `artifacts/Deviant-source.zip`

The first source build downloads and builds the audio utilities. Later builds reuse them. To compile without packaging, add `-p:GenerateDesktopArtifacts=false`.

To build and run the tests without packaging:

```powershell
dotnet build Deviant.slnx -c Release -p:GenerateDesktopArtifacts=false
dotnet test Deviant.slnx -c Release --no-build -p:GenerateDesktopArtifacts=false
```

To publish the CLI:

```powershell
./scripts/Publish-Cli.ps1
```

## AI Disclaimer

Deviant was developed with assistance from AI tools.

## Important note

This tool is not affiliated with Quantic Dream. We don't provide any of *Detroit: Become Human*'s assets, you must own a purchased copy of the game to use this tool properly.
