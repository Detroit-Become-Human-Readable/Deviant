# Dependencies and reference boundaries

The generic archive reader, catalog and exporter have no DetroitAudio dependency
and include no game assets, audio tools or code from the five reference
repositories. The Phase B desktop application uses the .NET Windows Desktop
runtime and standard WPF components.

| Component | Version | Terms | Source |
|---|---|---|---|
| .NET runtime | .NET 10 | MIT and runtime notices | https://github.com/dotnet/runtime |
| .NET Windows Desktop runtime | .NET 10 | MIT and runtime notices | https://github.com/dotnet/wpf |
| Microsoft.Data.Sqlite | 10.0.10 | MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw bundle/provider/native packages | 2.1.12 | Apache-2.0 managed code; SQLite public domain | https://github.com/ericsink/SQLitePCL.raw |
| Microsoft.NET.Test.Sdk | 18.0.1 | MIT | https://github.com/microsoft/vstest |
| xUnit | 2.9.3 | Apache-2.0 | https://github.com/xunit/xunit |
| xUnit Visual Studio runner | 3.1.4 | Apache-2.0 | https://github.com/xunit/visualstudio.xunit |

Test dependencies are not part of the published CLI. Native SQLite version is
checked by a real SQL query in the synthetic test suite. NuGet vulnerability
auditing remains enabled; warnings are not suppressed.

The self-contained Windows publish includes notices for the resolved .NET and
Windows Desktop runtimes and package metadata in its `notices` directory. This
local build does not grant redistribution permission for the research reference
tools. Codec distribution material is included with the verified desktop bundle.
# Audio components

The desktop bundle includes audio readers and processing services from Detroit Audio Extractor under the MIT license, included in `notices/DetroitAudio-MIT.txt`. Playback uses NAudio under the MIT license.

Separate codec tools include FFmpeg (LGPL-2.1-or-later), vgmstream (ISC-style with bundled component notices), ww2ogg (BSD-3-Clause), ReVorb (ISC-style), libogg and libvorbis (BSD-3-Clause). Exact component versions, hashes, sources and build material are included under `tools`. FFmpeg uses shared libraries and disables GPL and nonfree components. The sources used for all codec components are distributed with the tools.

## Asset format components

| Component | Version | Terms | Source |
|---|---|---|---|
| BCnEncoder.Net | 2.3.0 | MIT or Unlicense | https://github.com/Nominom/BCnEncoder.NET |
| CommunityToolkit.HighPerformance | Resolved through BCnEncoder | MIT | https://github.com/CommunityToolkit/dotnet |
| SharpGLTF.Core / Toolkit / Runtime | 1.0.7 | MIT | https://github.com/vpenades/SharpGLTF |
| HelixToolkit.Wpf.SharpDX / SharpDX / Maths | 3.1.2 | MIT | https://github.com/helix-toolkit/helix-toolkit/tree/v3.1.2 |
| SharpDX Direct3D / DXGI / D3DCompiler | 4.2.0 | MIT | https://github.com/sharpdx/SharpDX |

BCnEncoder supports managed DDS pixel decoding.
Full BCnEncoder license texts are included in `notices/BCnEncoder-MIT.txt`
and `notices/BCnEncoder-Unlicense.txt` from its pinned package source commit.
SharpGLTF is used by the generic
model exporter in the source suite. Publishing copies license/notice files and
package metadata for dependencies present in the desktop build. The source suite
uses synthetic public fixtures; game samples are excluded from distribution.

The shared scene/model viewport uses HelixToolkit. Its adapted shader layouts
retain the upstream copyright notice; the full license is included in
`notices/HelixToolkit-MIT.txt`.
