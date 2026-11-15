# Magpienest 鵲藏

English | [繁體中文](README.zh-TW.md)

Magpienest is a Windows desktop collection library for your **local** illustrations, comics, videos and games. Every feature is a plugin that you can switch on or off in Settings, with immediate effect.

## Status

Early development. The plugin kernel, main window, Settings page, live language switching (English / 繁體中文) and light/dark theme work. The media features are not built yet.

## Planned features

- **Illustrations**: folders of images, thumbnail wall, tags, ratings, favourites, search and a fast viewer.
- **Comics**: folders and zip/rar/7z archives as books; two-page spreads, right-to-left reading, remembers where you stopped.
- **Videos**: plays most formats, resumes playback, audio and subtitle track selection.
- **Games**: add a game by its exe or by scanning a folder of game folders; launch and track play time.
- Your files are never moved, renamed or changed: the library is only an index.
- Portable zip release for Windows 10/11 x64.

## Build and run

Requirements: Windows 10/11 x64 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build Magpienest.slnx -c Debug
dotnet test  Magpienest.slnx -c Debug
artifacts\bin\Debug\Magpienest.App.exe
```

Useful options: `--data-dir <folder>` keeps all data in that folder; `--smoke-test` starts, checks every plugin and exits; `--screenshot <folder>` saves a PNG of every page and exits.

## Plugin model

A tiny kernel loads plugins from `plugins/<id>/` folders, each with a `plugin.json` manifest. Plugins talk to each other only through service interfaces in `Magpienest.Sdk`, and everything a plugin registers is undone automatically when it is switched off. The main window, Settings and the language support are plugins too.

## Licence

[MIT](LICENSE). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Credits

The design studies two MIT-licensed projects: [NeeView](https://github.com/neelabo/NeeView) (image and book viewing) and [Playnite](https://github.com/JosefNemec/Playnite) (game library management).
