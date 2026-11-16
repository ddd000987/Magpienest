# Writing a Magpienest plugin

Every feature of Magpienest is a plugin: the main window, Settings, the
language support, the library, even the four media pages. A plugin you write
has exactly the same powers as those, and the same rules. This guide takes you
from nothing to an installable `.zip`.

- [The mental model](#the-mental-model)
- [Quick start](#quick-start)
- [The manifest (`plugin.json`)](#the-manifest-pluginjson)
- [Project setup](#project-setup)
- [Localization files](#localization-files)
- [The rules](#the-rules)
- [Services you can use](#services-you-can-use)
- [Adding a new media kind in about 100 lines](#adding-a-new-media-kind-in-about-100-lines)
- [Packaging and installing](#packaging-and-installing)
- [Debugging](#debugging)

A complete, buildable example lives in [`samples/HelloPlugin`](../samples/HelloPlugin).
Every code sample below was compiled against the real `Magpienest.Sdk`.

## The mental model

Magpienest is a tiny **kernel** plus plugins. The kernel only loads plugins,
lets them publish and use **services**, delivers **events**, and undoes
everything a plugin did when the plugin is switched off.

**Plugin.** A public class that derives from `Magpienest.Sdk.Plugin`. A new
instance is created every time the plugin is activated.

```csharp
[Requires(typeof(IShellService))]
public sealed class HelloPlugin : Plugin
{
    public override void Activate(IPluginContext ctx) { /* register things */ }
    public override void Deactivate() { }   // optional; effects are undone for you afterwards
}
```

**Context (`IPluginContext`).** Handed to `Activate`. Every side effect goes
through it so the kernel can undo it. It gives you:

| Member | Purpose |
|---|---|
| `Manifest`, `PluginDirectory` | your `plugin.json` and the (read-only) folder you were installed in |
| `DataDirectory` | `<data>\plugin-data\<id>\`, yours to write to; created on first use |
| `Config` | small key/value settings, saved to `<data>\config\<id>.json` |
| `Log` | writes to the Magpienest log file |
| `Ui` | `IUiDispatcher` for hopping back to the UI thread |
| `Environment` | app version, folders, command line, `RequestShutdown` |
| `Deactivating` | a `CancellationToken` cancelled when your plugin is being switched off; pass it to all background work |
| `Get<T>()` / `TryGet<T>()` | look up a service |
| `Provide<T>()` | publish a service (see [below](#providing-a-service)) |
| `On<TEvent>()` / `Emit()` | subscribe to / publish events |
| `Track(...)` | record your own undo action |

**Services.** A service is an object registered under an interface that lives in
`Magpienest.Sdk` (`IShellService`, `ILibraryService`, ...). At most one plugin
provides each interface at a time. Plugins talk to each other **only** through
these interfaces.

Declare what you use on the plugin class:

- `[Requires(typeof(IX), ...)]` - the plugin stays in the *Waiting* state (shown
  in Settings with the missing service names) until every required service is
  available, and is deactivated again if one disappears.
- `[Optional(typeof(IY), ...)]` - you may call `ctx.TryGet<IY>(out var y)`. The
  plugin is re-activated whenever an optional service appears or disappears, so
  you only decide once, in `Activate`.

`ctx.Get<T>()` throws if `T` is not declared. Three **kernel services** are
always available without declaring them: `IPluginManager`, `IAppEnvironment`
and `IUiDispatcher`.

**The owner convention.** Every service method that *registers* something (a
page, a kind, a provider, a settings section, a resource dictionary) takes
`IPluginContext owner` as its first parameter and returns `void`. The service
itself calls `owner.Track(...)` to schedule the removal. You never write
cleanup code for these, and "disable plugin" is always complete and immediate.

```csharp
shell.AddPage(ctx, new PageDescriptor { ... });          // gone when your plugin is disabled
library.RegisterKind(ctx, new ItemKindDescriptor { ... });
```

If you create something the SDK does not know about (a timer, a file watcher,
an event subscription on a plain .NET object), undo it yourself with
`ctx.Track`:

```csharp
var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
timer.Tick += (_, _) => ctx.Log.Debug("tick");
timer.Start();
ctx.Track(timer.Stop);          // also accepts an IDisposable; effects run in reverse order
```

**Lifecycle.** `Activate` runs on the UI thread once every required service is
available. If it throws, everything it registered so far is rolled back and the
plugin shows as *Failed* (with the message) until the user toggles it or
restarts the app. Disabling the plugin (or losing a required service) cancels
`ctx.Deactivating`, calls `Deactivate()`, then runs your tracked effects in
reverse order. A plugin that loses or regains an optional service goes through
the same cycle automatically.

## Quick start

1. Build or install Magpienest so that `Magpienest.Sdk.dll` exists on disk.
2. Copy [`samples/HelloPlugin`](../samples/HelloPlugin), rename it, change the
   `id` in `plugin.json`.
3. `dotnet build -p:MagpienestDir=<folder with Magpienest.Sdk.dll>`
4. `.\pack.ps1` produces a `.zip`; install it from Settings, Plugins.

## The manifest (`plugin.json`)

Next to your dll, always:

```json
{
  "id": "sample.hello",
  "version": "1.0.0",
  "apiVersion": 1,
  "assembly": "HelloPlugin.dll",
  "name": { "en": "Hello", "zh-TW": "哈囉" },
  "description": {
    "en": "A sample plugin that adds one page with a greeting.",
    "zh-TW": "範例外掛，新增一個顯示問候語的頁面。"
  },
  "author": "Your Name",
  "essential": false,
  "enabledByDefault": true
}
```

| Field | Meaning |
|---|---|
| `id` | Unique. Lower-case letters, digits and dots only (`[a-z0-9.]+`), e.g. `yourname.notes`. The `magpienest.` prefix is for built-in plugins; an id that matches a built-in plugin is rejected at install time. The folder name does not matter. |
| `version` | Your plugin's version, shown in Settings. Free text, but use `major.minor.patch`. |
| `apiVersion` | The plugin API version you built against. It must equal the host's (currently `1`); otherwise the plugin shows as *Failed* with "Plugin API version N is not supported". |
| `assembly` | File name of your main dll, relative to `plugin.json`. Must exist in the package. |
| `entryType` | Optional full type name of the `Plugin` subclass. Leave it out if the assembly has exactly one public, non-abstract `Plugin` subclass (zero or several is an error). |
| `name`, `description` | Maps from language code to text. Provide at least `en` and `zh-TW`. Lookup: current language, then `en`, then the first entry. |
| `author` | Shown in the install confirmation and in Settings. |
| `essential` | Leave `false`. Only the plugins of Magpienest's core bundle are essential (cannot be turned off). |
| `enabledByDefault` | Whether a fresh install starts enabled. The user's choice is remembered afterwards. |
| `bundle` | Optional. Plugins with the same bundle id are shown, switched and removed together as one card in Settings. Leave it out and your plugin gets a card of its own. |

The file is read with `System.Text.Json`: property names are case-insensitive;
comments and trailing commas are allowed.

## Project setup

A plugin project is a plain WPF class library that references **only**
`Magpienest.Sdk.dll`, as a compile-time reference that is never copied:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <MagpienestDir Condition="'$(MagpienestDir)' == ''">C:\Program Files\Magpienest</MagpienestDir>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Magpienest.Sdk">
      <HintPath>$(MagpienestDir)\Magpienest.Sdk.dll</HintPath>
      <Private>false</Private>   <!-- never ship the SDK -->
    </Reference>
    <Content Include="plugin.json" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="lang\*.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

`MagpienestDir` is any folder that contains `Magpienest.Sdk.dll` of the
Magpienest version you target (a repository build, or an installed copy). Pass
another one with `-p:MagpienestDir=...`.

Things to know:

- **Do not reference** another plugin or `Magpienest.Core`, and never ship
  `Magpienest.Sdk.dll`. The host loads your plugin into its own
  `AssemblyLoadContext` and always supplies its own copy of the SDK (and of
  WPF); that keeps SDK types identical for every plugin. The installer rejects
  packages that contain `Magpienest.Sdk.dll`, `Magpienest.Core.dll`,
  `Magpienest.exe`, `Magpienest.dll` or `Magpienest.App.*`.
- **Your own NuGet packages are fine.** Their dlls are loaded from your plugin
  folder (keep the generated `*.deps.json` next to your dll). If the host
  already ships an assembly of that name, the host's copy wins, so avoid
  packages that clash with the host's own.
- **XAML works normally.** Use assembly-qualified resource URIs, for example
  `pack://application:,,,/HelloPlugin;component/Images/logo.png` and
  `/HelloPlugin;component/Themes/Generic.xaml`.
- The sample project is deliberately not part of the Magpienest solution; its
  `samples\Directory.Build.props` stops the repository's build settings from
  applying to it.
- Assemblies are never unloaded. Disabling a plugin undoes its effects but its
  code stays in memory, so after rebuilding you must restart Magpienest to try
  the new dll.

## Localization files

Put one JSON file per language in `lang\`:

```
lang\en.json
lang\zh-TW.json
```

```json
{
  "Hello.Page.Title": "Hello",
  "Hello.Greeting": "Hello from a plugin!"
}
```

- Each file is a flat `"Key": "text"` object.
- **Both `en.json` and `zh-TW.json` should exist and have identical keys.**
  Missing keys fall back to the parent culture, then `en`, then the key itself
  (which is visible in the UI, so a missing key is easy to spot).
- Keys are global. Use `<PluginShortName>.<Area>.<Name>`, where the short name
  is the last segment of your id in PascalCase (`sample.hello` becomes
  `Hello.`).
- Magpienest loads your `lang\` folder automatically when the plugin
  activates, and unloads it when the plugin goes away. Users can add a whole
  new language, or override your text, with `<data>\lang\<code>.json`.
- **No user-visible text in XAML or C#.** In XAML:

  ```xml
  xmlns:sdk="clr-namespace:Magpienest.Sdk.Localization;assembly=Magpienest.Sdk"
  ...
  <TextBlock Text="{sdk:Loc Hello.Greeting}" />
  ```

  This is a live binding: the text changes the moment the user switches
  language. In code use `Loc.Get("Key")` or `Loc.Format("Key", arg0, arg1)`
  (`{0}` placeholders). Text built in code is not live; rebuild it in a
  `LanguageChanged` handler, or prefer XAML bindings.
- Properties named `...Key` on SDK descriptors (`TitleKey`, `DisplayNameKey`) take
  a key. Parameters named `title`, `message` or `text` take already-localized
  text.
- Log and exception messages are English and not localized.

## The rules

1. **No mutable static state.** A plugin's assembly survives disable and
   enable, so statics would leak across activations. Keep state in the plugin
   instance, view models, or `ctx.Config`. (`Magpienest.Sdk.Loc` is the one
   sanctioned static.)
2. **Never block the UI thread.** `Activate`, `Deactivate`, every registration
   method and every event handler run on the UI thread; the UI thread must not
   stall for more than about 16 ms. Anything that touches disk, a database,
   image decoding or the network is `async` or runs in `Task.Run`. Never call
   `.Result`, `.Wait()` or `.GetAwaiter().GetResult()` on the UI thread.
   Observe `ctx.Deactivating` in every background loop, and stop using a
   service once it is cancelled.
3. **Theme brushes only.** Colors come from theme resources through
   `DynamicResource`, so light and dark themes both work and switch live.
   Never hard-code a color. Useful keys: `TextFillColorPrimaryBrush`,
   `TextFillColorSecondaryBrush`, `TextFillColorDisabledBrush`,
   `AccentFillColorDefaultBrush`, `CardBackgroundFillColorDefaultBrush`,
   `CardStrokeColorDefaultBrush`, `ControlFillColorDefaultBrush`,
   `ControlStrokeColorDefaultBrush`, `SubtleFillColorSecondaryBrush`,
   `LayerFillColorDefaultBrush`, `ApplicationBackgroundBrush`,
   `DividerStrokeColorDefaultBrush`, `SystemFillColorCriticalBrush`,
   `SystemFillColorSuccessBrush`, `SystemFillColorCautionBrush`,
   `ControlCornerRadius`, and the text styles `BodyTextBlockStyle`,
   `CaptionTextBlockStyle`, `SubtitleTextBlockStyle`, `TitleTextBlockStyle`,
   `AccentButtonStyle`. Icons are font glyphs
   (`FontFamily="{DynamicResource SymbolThemeFontFamily}"`, Segoe Fluent Icons).
4. **Reference only `Magpienest.Sdk`.** Share behavior with other plugins only
   through SDK interfaces.
5. **Register through the owner convention.** Every registration takes `ctx`
   first. Do not keep registrations you cannot undo.
6. **Never move, rename, modify or delete the user's media files.** Reading is
   fine. Your own files go in `ctx.DataDirectory` (or `ctx.Environment.CacheDirectory`
   for things that can be regenerated).
7. **Report failures to the user, not just the log.** Use
   `shell.Notify(Loc.Get("..."), NotifySeverity.Error)` and put the technical
   detail in `ctx.Log`.
8. **Keyboard and accessibility.** Every interactive element gets a localized
   `ToolTip` or text and must be reachable with the keyboard. Spacing is a
   multiple of 4, page padding is 16.
9. **Views show, services decide.** Logic that a web page would also need
   (queries, listings, progress rules) belongs in a service or a plain class,
   not only in a view model.

## Services you can use

Add the interface to `[Requires]` (or `[Optional]`) first. Each example below
is a fragment of an `Activate` method or of an `async` method it calls;
`ctx` is the `IPluginContext`. Extra detail for every member is in the XML
documentation that ships next to `Magpienest.Sdk.dll`.

### Kernel services (no declaration needed)

```csharp
// IPluginManager: what is installed and in which state
foreach (var p in ctx.Get<IPluginManager>().Plugins.Where(p => p.State == PluginState.Failed))
    ctx.Log.Warn($"{p.Manifest.Id}: {p.Error}");

// IAppEnvironment
var version = ctx.Environment.AppVersion;      // also AppDirectory, DataDirectory, CacheDirectory, CommandLineArgs

// IUiDispatcher: run something on the UI thread from a background thread
ctx.Ui.Post(() => ctx.Log.Info("on the UI thread"));
var title = await ctx.Ui.InvokeAsync(() => someWindow.Title);
```

`ctx.Config` is a small persistent dictionary (saved about 500 ms after the
last change and on deactivation):

```csharp
var enabled = ctx.Config.Get("enabled", true);   // default when the key is missing
ctx.Config.Set("enabled", false);
var stateFile = Path.Combine(ctx.DataDirectory, "state.json");   // bigger data: your own folder
```

### Shell (`IShellService`, namespace `Magpienest.Sdk.Shell`)

The main window: pages, overlays, toasts, dialogs, theme.

```csharp
shell.AddPage(ctx, new PageDescriptor
{
    Id = "hello",                      // unique page id
    TitleKey = "Hello.Page.Title",     // localization key
    Glyph = "\uE80F",                  // Segoe Fluent Icons code point
    Order = 500,                       // ascending; built-ins use 100..400 (media), 900 About, 1000 Settings
    Placement = NavPlacement.Main,     // or Footer
    CreateView = () => new HelloView(),    // called once, lazily; the view is kept
});

shell.Notify(Loc.Get("Hello.Saved"), NotifySeverity.Success);     // toast; any thread

using (var task = shell.BeginTask(ctx, Loc.Get("Hello.Importing")))   // status-bar entry
{
    task.Report(0.5, Loc.Format("Hello.Step", 5, 10));            // null fraction = indeterminate
}

if (await shell.ConfirmAsync(Loc.Get("Hello.Confirm.Title"), Loc.Get("Hello.Confirm.Text"))) { /* yes */ }
```

A page view (or its `DataContext`) may implement `INavigationAware` to hear
`OnNavigatedTo` / `OnNavigatedFrom`. Other members: `Navigate(pageId)`,
`ShowOverlay(owner, view)` / `CloseOverlay`, `ShowMessageAsync`,
`ShowDialogAsync(DialogDescriptor)`, `AddResources(owner, dictionary)` (merges
a `ResourceDictionary` into the application), `Theme`, `IsFullScreen`.

### Settings (`ISettingsService`, `Magpienest.Sdk.Settings`)

Adds a section to the Settings page.

```csharp
ctx.Get<ISettingsService>().AddSection(ctx, new SettingsSection
{
    Id = "hello",
    TitleKey = "Hello.Settings.Title",
    Glyph = "\uE713",
    Order = 500,                          // general = 0, library = 100, plugins = 900
    CreateView = () => new HelloSettingsView(ctx.Config),
});
```

### Localization (`ILocalizationService`, `Magpienest.Sdk.Localization`)

Usually `Loc` and `{sdk:Loc ...}` are all you need. For text that must be
rebuilt on a language switch, or an extra language folder:

```csharp
ctx.On<LanguageChanged>(e => RebuildText());          // e.Language is "en", "zh-TW", ...
ctx.Get<ILocalizationService>().AddDirectory(ctx, Path.Combine(ctx.PluginDirectory, "extra-lang"));
```

### Events

Events are typed records delivered on the UI thread; handlers are removed
automatically on deactivation.

```csharp
ctx.On<ThemeChanged>(e => ctx.Log.Info($"Theme is now {e.Theme}"));
ctx.On<AppStarted>(_ => ctx.Log.Info("Startup finished"));
ctx.On<PluginStateChanged>(e => ctx.Log.Debug($"{e.Plugin.Manifest.Id} is {e.Plugin.State}"));
ctx.On<LibraryChanged>(e => ctx.Log.Debug($"{e.Kind}: {e.Type} {e.ItemIds.Count} item(s)"));
ctx.Emit(new MyEvent("x"));      // any thread; only plugins that can see your type can subscribe
```

### Library (`ILibraryService`, `Magpienest.Sdk.Library`)

The collection database: kinds, folders, items, tags, progress. Never touches
the user's files.

```csharp
var library = ctx.Get<ILibraryService>();

var items = await library.QueryAsync(new ItemQuery
{
    Kind = "illustration", Text = "cat", MinRating = 4,
    Sort = ItemSort.Rating, Descending = true, Limit = 20,
}, ctx.Deactivating);

await library.UpdateAsync(items[0].Id, new ItemPatch { IsFavorite = true });   // only non-null members apply
await library.SetTagsAsync(items[0].Id, ["cute", "cat"]);
```

Also available: sources (`GetSourcesAsync`, `AddSourceAsync`, `ScanAsync`),
`AddAsync`, `GetAsync`, `RemoveAsync` (database only), `GetTagsAsync`,
`RegisterKind` (next section). Kind-specific data goes in `LibraryItem.Extra`
(JSON) via `item.GetExtra<T>()` and `LibraryItemExtensions.SerializeExtra`.

### Library browser (`ILibraryBrowserFactory`)

A complete browser for one kind (toolbar, folder tree, virtualized tile grid,
details panel, folder management). You supply what is special about the kind:

```csharp
CreateView = () => ctx.Get<ILibraryBrowserFactory>().CreateBrowser(ctx, new BrowserOptions
{
    Kind = "notes",
    OpenItem = request => OpenAsync(request.Item),       // double-click / Enter
    TileAspectRatio = 1.0,
    Sorts = [ItemSort.Title, ItemSort.AddedAt, ItemSort.Rating],
    Subtitle = item => item.Directory,
    Commands = [new BrowserCommand("Notes.Reveal", "\uE838", items => RevealAsync(items))],
}),
```

### Thumbnails (`IThumbnailService`, `Magpienest.Sdk.Thumbnails`)

Cached tile images. You provide one `IThumbnailProvider` per kind (see the next
section); to *use* thumbnails:

```csharp
ImageSource? thumb = await ctx.Get<IThumbnailService>().GetAsync(item, ctx.Deactivating);   // null = no thumbnail
// GetShellImageAsync(path, size, iconOnly, ct): the Windows thumbnail or icon of any file
```

### Imaging (`IImagingService`, `Magpienest.Sdk.Imaging`)

Off-thread decoding with EXIF orientation applied. Do not decode on the UI
thread and do not use `new BitmapImage(uri)` (it locks the file).

```csharp
await using var stream = File.OpenRead(path);
BitmapSource bmp = await imaging.DecodeAsync(stream, new DecodeOptions(MaxPixelWidth: 800, MaxPixelHeight: 800), ctx.Deactivating);
```

Extra formats: implement `IImageDecoder` (`Extensions`, `Decode`, `ReadInfo`)
and call `imaging.RegisterDecoder(ctx, new WebpDecoder())`.

### Archives (`IArchiveService`, `Magpienest.Sdk.Archives`)

Reads zip/cbz/rar/cbr/7z/cb7.

```csharp
await using var archive = await ctx.Get<IArchiveService>().OpenAsync(path, ctx.Deactivating);
var first = archive.Entries.First(e => imaging.IsSupported(e.Key));
await using var stream = await archive.OpenEntryAsync(first, ctx.Deactivating);   // seekable
```

It throws `ArchiveEncryptedException` for password-protected archives and
`IOException` for corrupt ones.

### Image viewer (`IImageViewerService`, `Magpienest.Sdk.Viewing`)

Opens the built-in full-window viewer over a list of pages.

```csharp
ctx.Get<IImageViewerService>().Open(new ViewerRequest
{
    Pages = new FilePageSource([.. items.Select(i => i.Path)]),   // or your own IPageSource
    StartIndex = 0,
    Title = Loc.Get("Hello.Viewer.Title"),
});
```

`ViewerMode.Book` adds reading direction and spread options; `OnProgress`
reports the page being read. `IImageWindowService` (a borderless single-image
window) and `IMediaEngine` (video playback and probing) follow the same
pattern; declare them `[Optional]` when your plugin works without them.

### Providing a service

`ctx.Provide<T>(implementation)` publishes your object under an SDK interface so
that other plugins can use it, and removes it when you deactivate. It throws
`InvalidOperationException` when another plugin already provides `T`. This is how
a plugin replaces a built-in implementation: for example a different
`IMediaEngine`. Because plugins must not reference each other, you can only
provide interfaces that exist in `Magpienest.Sdk`.

## Adding a new media kind in about 100 lines

A **kind** is a type of collectible. A kind plugin supplies three small things
and the Library does the rest (folders, scanning, database, tags, rating,
search, sorting, the tile grid):

1. a **scanner** that lists the items under a folder,
2. a **thumbnail provider** that draws one tile,
3. a **page** that hosts the shared browser.

This complete plugin collects Markdown notes and opens them in whatever
program the user has associated with `.md` files. It is modelled on the
built-in Illustration plugin.

```csharp
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using Magpienest.Sdk;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Shell;
using Magpienest.Sdk.Thumbnails;

namespace NotesPlugin;

[Requires(typeof(IShellService), typeof(ILibraryService), typeof(ILibraryBrowserFactory), typeof(IThumbnailService))]
public sealed class NotesPlugin : Plugin
{
    public const string Kind = "notes";

    public override void Activate(IPluginContext ctx)
    {
        var shell = ctx.Get<IShellService>();
        var library = ctx.Get<ILibraryService>();
        var browsers = ctx.Get<ILibraryBrowserFactory>();
        var thumbnails = ctx.Get<IThumbnailService>();

        // 1. The kind: a name plus a scanner. The Library plugin does the rest.
        library.RegisterKind(ctx, new ItemKindDescriptor
        {
            Id = Kind,
            DisplayNameKey = "Notes.Kind",
            Scanner = new NotesScanner(),
        });

        // 2. How to draw a tile for one item.
        thumbnails.RegisterProvider(ctx, Kind, new NotesThumbnails(thumbnails));

        // 3. A page that is a ready-made browser for the kind.
        shell.AddPage(ctx, new PageDescriptor
        {
            Id = Kind,
            TitleKey = "Notes.Page.Title",
            Glyph = "\uE70B", // Segoe Fluent Icons: Edit
            Order = 500,
            CreateView = () => browsers.CreateBrowser(ctx, new BrowserOptions
            {
                Kind = Kind,
                OpenItem = request => OpenAsync(request.Item, library, ctx.Log),
            }),
        });
    }

    private static async Task OpenAsync(LibraryItem item, ILibraryService library, ILog log)
    {
        try
        {
            // Process.Start can stall for a moment, so keep it off the UI thread.
            await Task.Run(() => Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }));
            await library.UpdateAsync(item.Id, new ItemPatch { MarkOpened = true });
        }
        catch (Exception ex)
        {
            log.Warn($"Could not open {item.Path}.", ex);
        }
    }
}

/// <summary>Every .md file below a library folder. Runs on a background thread.</summary>
internal sealed class NotesScanner : IItemScanner
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    public IEnumerable<ScannedEntry> Scan(string root, CancellationToken ct)
    {
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.md", Options))
        {
            ct.ThrowIfCancellationRequested();
            yield return new ScannedEntry(file.FullName, Path.GetFileNameWithoutExtension(file.Name),
                                          file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
        }
    }
}

/// <summary>Uses the Windows icon of the file as its thumbnail.</summary>
internal sealed class NotesThumbnails(IThumbnailService thumbnails) : IThumbnailProvider
{
    public string GetStamp(LibraryItem item) => ThumbnailDefaults.DefaultStamp(item);

    public Task<BitmapSource?> CreateAsync(LibraryItem item, int maxPixelSize, CancellationToken ct) =>
        thumbnails.GetShellImageAsync(item.Path, maxPixelSize, iconOnly: true, ct);
}
```

Add `Notes.Kind` and `Notes.Page.Title` to both language files and you are done:
the page shows the standard browser with an "add folder" button; adding a folder
scans it with `NotesScanner`, and the tiles appear as they are thumbnailed.

Notes on each part:

- **Scanner.** Called on a background thread, once per library folder, on every
  scan. Yield one `ScannedEntry(path, title, size, modified)` per item, skip
  unreadable files and folders silently, and call `ct.ThrowIfCancellationRequested()`
  often. The Library compares results with its database (new, changed,
  missing). A scanner never modifies files.
- **Thumbnail provider.** `GetStamp` returns a string that changes whenever the
  cached thumbnail must be regenerated (`ThumbnailDefaults.DefaultStamp` uses file
  size, modified time and cover). `CreateAsync` runs on a background thread
  and returns a frozen bitmap no larger than `maxPixelSize` on its long edge,
  or `null` for a placeholder. Caching is done for you. Image-like kinds
  decode with `IImagingService.DecodeAsync` (see the built-in Illustration
  plugin); video kinds grab a frame with `IMediaEngine.CaptureFrameAsync`.
- **Page.** `BrowserOptions` has more knobs when you need them: `ShowStatus`
  (status filter and editor), `AddItem` (an "Add..." toolbar button for manual
  items), `ProgressFraction`, `CreateDetails` (a kind-specific details block)
  and `Commands` (extra context-menu entries).
- **Page-based kinds** (comics) can also set `ItemKindDescriptor.OpenPages`, so
  any front end can open a book without knowing how your plugin lists its pages.

## Packaging and installing

### Package format

A `.zip` (or a plain folder) with `plugin.json` at its root, or inside exactly
one top-level folder, plus everything the plugin needs next to it: the dll,
`*.deps.json`, private dependencies, and the `lang\` folder.

```
HelloPlugin.zip
  plugin.json
  HelloPlugin.dll
  HelloPlugin.deps.json
  lang/en.json
  lang/zh-TW.json
```

The sample's [`pack.ps1`](../samples/HelloPlugin/pack.ps1) builds the project
and produces exactly this (entry names use forward slashes, which every zip
reader accepts; Windows PowerShell 5.1's `Compress-Archive` would store
backslashes, which is why the script uses `System.IO.Compression`).

The installer rejects a package when:

- `plugin.json` is missing, unreadable, or its `id` is not `[a-z0-9.]+`;
- `apiVersion` is not supported or the `assembly` file is not in the package;
- the `id` belongs to a built-in plugin;
- it contains `Magpienest.Sdk.dll`, `Magpienest.Core.dll`, `Magpienest.exe`, `Magpienest.dll` or `Magpienest.App.*`;
- an entry would extract outside the plugin folder;
- it is larger than 500 MB uncompressed or has more than 5000 entries.

On failure the user sees a clear message and nothing changes.

### Installing

In Magpienest: **Settings, Plugins, Install plugin...** (choose the `.zip`), or
**Install from folder...**. Magpienest first checks the package; if it is
invalid, a message explains why (for example "No plugin.json was found in this
package.") and nothing changes. Otherwise a confirmation dialog shows the
plugin's name, version and author (and "This updates version X to version Y"
when it is already installed) and reminds the user that plugins run with the
same access to the computer as Magpienest itself, so only install plugins you
trust. **Install** extracts it; a toast confirms.

- **New plugin:** extracted to `<data>\plugins\<id>\` and active immediately; no
  restart.
- **Update (same id, already loaded):** staged, applied the next time
  Magpienest starts. The running version keeps working until then, and
  Settings shows a bar, "Restart Magpienest to finish updating.", with a
  **Restart now** button. (Assemblies cannot be unloaded.)
- **Remove:** built-in plugins show a "Built-in" label; installed plugins show
  a **Remove** button (after a confirmation). The plugin is deactivated
  immediately and listed as "Will be removed after restart" with an **Undo**
  button, and the same restart bar appears; its folder is deleted at the next
  start. The plugin's configuration
  (`<data>\config\<id>.json`) and `<data>\plugin-data\<id>\` are **kept**, so
  installing it again restores its settings.
- Enable and disable work like for any other plugin.

`<data>` is the data folder: `--data-dir <path>`, else the
`MAGPIENEST_DATA_DIR` environment variable, else `data\` next to the exe when a
`portable.txt` file sits beside it, else `%APPDATA%\Magpienest`. The About page
shows the current one.

### A quick development loop

Run a separate copy of the data so that you never disturb your real library:

```
Magpienest.exe --data-dir C:\dev\magpienest-data --verbose
```

Install your zip into it, or copy your build output folder to
`C:\dev\magpienest-data\plugins\<your id>\`. Because assemblies are never
unloaded, restart Magpienest after each rebuild.

## Debugging

- **Log file.** `<data>\logs\magpienest-YYYYMMDD.log`, one file per day, the
  last 14 are kept. About, Open log folder opens it. Lines look like
  `14:03:22.101 INFO  [sample.hello] Hello plugin activated.`: time, level,
  your plugin id, message (a failure is followed by the full exception). Write to
  it with `ctx.Log.Debug/Info/Warn/Error`; use `Warn(message, ex)` and
  `Error(message, ex)` to include an exception.
- **`--verbose`.** Debug-level messages are written only when Magpienest is
  started with `--verbose`.
- **Plugin state.** Settings, Plugins lists every plugin with its state:
  - *Failed*: `Activate` threw (or the dll could not be loaded, or the API
    version is wrong); the message is shown and the full exception is in the log.
    Toggle the plugin off and on, or restart, to retry.
  - *Waiting*: a `[Requires]` service has no provider; the missing interface
    names are listed. Turn on the plugin that provides it.
  - A key shown in the UI instead of text (for example `Hello.Greeting`) means
    the key is missing from `lang\*.json`, or the files were not copied.
- **Does it start at all?** `Magpienest.exe --smoke-test --data-dir <dir>`
  starts the app, waits for startup, writes `<dir>\smoke.json` with every
  plugin's state, error and missing services, and exits with code 0 only if no
  plugin failed and every enabled plugin is active.
- **`--safe-mode`.** Starts with built-in plugins only; plugins in
  `<data>\plugins` are not discovered. Use it to get back into the app when an
  installed plugin breaks startup, then remove that plugin in Settings. The
  message shown when Magpienest cannot start mentions this option.
- Unhandled exceptions on the UI thread, in background tasks and in plugin
  callbacks (`Activate`, `Deactivate`, effects, event handlers) are caught and
  logged; one plugin failing never takes down another plugin or the app.
