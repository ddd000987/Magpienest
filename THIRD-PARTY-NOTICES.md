# Third-party notices

Magpienest itself is released under the [MIT licence](LICENSE). The release zip and the plugin store
packages contain the third-party components below. Each licence was checked on the package's NuGet page (or in
its repository) for the exact version that is shipped.

Test-only packages (xUnit, Microsoft.NET.Test.Sdk) are not part of the release and are not listed.
Every plugin store package carries a copy of this file and of the Magpienest licence.

| Component | Version | Licence | Where it is used |
|---|---|---|---|
| [.NET runtime and libraries](https://github.com/dotnet/runtime) | 10 | MIT | Self-contained host (`Magpienest.exe`) |
| [Windows Presentation Foundation](https://github.com/dotnet/wpf) | 10 | MIT | User interface |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | MIT | `Magpienest.Sdk` and every plugin |
| [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) | 11.0.0 | MIT | Library and Thumbnails plugins |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) (core, provider, bundle) | 3.0.5 | Apache-2.0 | Loads SQLite for Microsoft.Data.Sqlite |
| [SQLite](https://sqlite.org/) (native `e_sqlite3` builds from the NuGet package `SQLite` 3.53.4) | 3.53.4 | Public domain | Library database and thumbnail cache |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | 0.50.9 | MIT | Archives plugin (zip, rar, 7z) |
| [VirtualizingWrapPanel](https://github.com/sbaeumlisberger/VirtualizingWrapPanel) | 2.5.4 | MIT | Library browser grid |

### Videos (plugin store package `magpienest.mediaengine.libvlc-<version>.zip`)

These ship only inside the media engine package of the Videos feature, not in the main zip.

| Component | Version | Licence | Where it is used |
|---|---|---|---|
| [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) and LibVLCSharp.WPF | 3.9.4 | LGPL-2.1-or-later | Media engine plugin (.NET bindings for LibVLC) |
| [LibVLC](https://www.videolan.org/vlc/libvlc.html), from the NuGet package [VideoLAN.LibVLC.Windows](https://code.videolan.org/videolan/libvlc-nuget) | 3.0.21 | LGPL-2.1-or-later | Media engine plugin (video and audio playback) |

### Web interface (plugin store package `magpienest.webui-<version>.zip`)

These ship only inside the web interface package, not in the main zip.

| Component | Version | Licence | Where it is used |
|---|---|---|---|
| [GenHTTP](https://github.com/Kaliumhexacyanoferrat/GenHTTP) (`GenHTTP.Engine.Internal` and the GenHTTP packages it brings) | 11.3.0 | MIT | Web server |
| [Glyph11](https://www.nuget.org/packages/Glyph11), [Cottle](https://github.com/r3c/cottle), [YamlDotNet](https://github.com/aaubry/YamlDotNet) | 0.4.0, 2.1.0, 18.1.0 | MIT | Dependencies of GenHTTP |
| [Microsoft.Extensions.*](https://github.com/dotnet/runtime) (Logging, Options, Configuration, DependencyInjection, ObjectPool, Primitives) | 10.0 | MIT | Dependencies of GenHTTP |
| [Microsoft Edge WebView2 SDK](https://www.nuget.org/packages/Microsoft.Web.WebView2) | 1.0.4314.35 | Microsoft WebView2 SDK licence (BSD-style, redistributable) | In-app "Web" page |
| [QRCoder](https://github.com/Shane32/QRCoder) | 1.8.1 | MIT | QR codes of the home-network links |

## Licences in detail

### MIT licence

The .NET runtime and libraries, WPF, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, SharpCompress and
VirtualizingWrapPanel are under the MIT licence. Copyright lines as published by each project:

- .NET and WPF: (c) .NET Foundation and Contributors (Microsoft). The .NET runtime ships its own
  list of third-party notices:
  <https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT>
- CommunityToolkit.Mvvm: (c) .NET Foundation and Contributors.
- Microsoft.Data.Sqlite: (c) Microsoft Corporation.
- SharpCompress: Copyright (c) 2014 Adam Hathcock.
- VirtualizingWrapPanel: Copyright (c) 2019 S. Baeumlisberger.

The MIT licence text: <https://opensource.org/license/mit>

### SQLitePCLRaw and SQLite

Microsoft.Data.Sqlite reaches SQLite through SQLitePCLRaw (Copyright 2014-2026 SourceGear, LLC,
Eric Sink), which is under the Apache License 2.0: <https://www.apache.org/licenses/LICENSE-2.0>.
SQLite itself is in the public domain: <https://sqlite.org/copyright.html>. The native
`e_sqlite3.dll` is built by the SQLitePCLRaw author and shipped in the NuGet package `SQLite`.

### LibVLCSharp and LibVLC (LGPL-2.1-or-later)

Video and audio playback uses LibVLC from the VideoLAN project, under the GNU Lesser General Public
License, version 2.1 or later: <https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html>.

- LibVLC is **dynamically linked**. It is not compiled into any Magpienest program file. It is a set
  of separate DLL files (`libvlc.dll`, `libvlccore.dll`) and plugin DLLs in
  `libvlc\win-x64\` of the installed media engine plugin (in a portable copy:
  `data\plugins\magpienest.mediaengine.libvlc\libvlc\win-x64\`), loaded at run time through LibVLCSharp.
- **You may replace the LibVLC files.** Put a compatible LibVLC 3.0.x build (the `libvlc.dll`,
  `libvlccore.dll`, `plugins` folder and the other files of a VLC 3.0 Windows x64 build) in that
  folder and Magpienest will use it. If you only want to turn video playback off, switch
  "Videos" off in Settings, Plugins.
- **Source code.** The source of the shipped LibVLC version (VLC 3.0.21) is available from
  VideoLAN:
  - release archive: <https://download.videolan.org/pub/videolan/vlc/3.0.21/vlc-3.0.21.tar.xz>
  - repository: <https://code.videolan.org/videolan/vlc>
  - the scripts that build the NuGet package: <https://code.videolan.org/videolan/libvlc-nuget>
  - LibVLCSharp source: <https://code.videolan.org/videolan/LibVLCSharp>
- Magpienest does not modify LibVLC or LibVLCSharp.

LibVLC also contains and loads codecs and other libraries of its own (for example FFmpeg), each under
its own licence; see the VLC project for the full list: <https://www.videolan.org/vlc/>.

## Acknowledgements

Magpienest was designed after studying two MIT-licensed projects. Neither is a dependency, and no
files were copied from either.

- **[NeeView](https://github.com/neelabo/NeeView)**, Copyright (c) 2016-2026 Ito Mitsuhiro (nee),
  MIT. Studied for how a book viewer lays out two-page spreads and handles reading direction.
  Magpienest re-implements these ideas in its own code; no NeeView code was used.
- **[Playnite](https://github.com/JosefNemec/Playnite)**, Copyright (c) 2020 Josef Nemec, MIT.
  Studied for how a game library adds games and finds the main program inside a game folder.
  One piece of data was adapted from it:
  - `src/plugins/Magpienest.Plugins.Game/GameFolderScanner.cs`: the list of file-name patterns for
    installers, updaters and helper programs that are never the game itself (the `Masks` list)
    is adapted from `scanFileExclusionMasks` in Playnite's `source/Playnite/Common/Programs.cs`,
    with a few more patterns added. The rest of the scanner (scoring, folder handling) is
    Magpienest's own code.

The MIT licence of both projects:

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
> associated documentation files (the "Software"), to deal in the Software without restriction,
> including without limitation the rights to use, copy, modify, merge, publish, distribute,
> sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions: the above copyright notice and this
> permission notice shall be included in all copies or substantial portions of the Software. The
> Software is provided "as is", without warranty of any kind.
