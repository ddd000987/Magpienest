# HelloPlugin

The smallest useful Magpienest plugin: one page in the navigation rail with a
localized greeting and a button that shows a toast. It is a stand-alone project
and is not part of `Magpienest.slnx`.

The full guide is [`docs/plugin-development.md`](../../docs/plugin-development.md).

| File | Purpose |
|---|---|
| `plugin.json` | manifest: id, version, names, API version, assembly |
| `HelloPlugin.cs` | the `Plugin` class; registers the page through `IShellService` |
| `HelloView.xaml(.cs)` | the page (theme brushes only, text through `{sdk:Loc ...}`) |
| `lang/en.json`, `lang/zh-TW.json` | all user-visible text |
| `pack.ps1` | builds and produces `HelloPlugin.zip` |

## Build

The project compiles against `Magpienest.Sdk.dll` from a built or installed
Magpienest and never ships it. By default it looks in the repository's
`artifacts\bin\Debug` (build Magpienest first with
`dotnet build Magpienest.slnx -c Debug`). To use another folder:

```
dotnet build -p:MagpienestDir="C:\Program Files\Magpienest"
```

## Pack and install

```
.\pack.ps1                                   # Release build, creates HelloPlugin.zip
.\pack.ps1 -Configuration Debug -MagpienestDir "C:\Program Files\Magpienest"
```

Then in Magpienest open **Settings, Plugins, Install plugin...** and choose
`HelloPlugin.zip`. A "Hello" page appears immediately. Disable, enable and
remove it like any other plugin.

To try it without touching your real data, start Magpienest with a throw-away
data folder: `Magpienest.exe --data-dir C:\dev\magpienest-data`.
