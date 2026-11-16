using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Magpienest.Plugins.Settings;
using Magpienest.Sdk;

namespace Magpienest.Plugins.Settings.Tests;

/// <summary>T8.0: Settings → Plugins shows one card per bundle.</summary>
public sealed class PluginBundleTests
{
    private static readonly string Root = FindRepoRoot();

    [Fact]
    public void PluginsWithTheSameBundleShareOneCardAndOthersGetTheirOwn()
    {
        var bundles = Group(
            Plugin("a.video", bundle: "a.media"),
            Plugin("a.engine", bundle: "a.media"),
            Plugin("x.hello"));

        Assert.Equal(2, bundles.Count);
        var media = Assert.Single(bundles, b => b.BundleId == "a.media");
        Assert.Equal(["a.engine", "a.video"], media.Members.Select(m => m.Manifest.Id));
        var single = Assert.Single(bundles, b => b.BundleId is null);
        Assert.Equal("x.hello", Assert.Single(single.Members).Manifest.Id);
    }

    [Fact]
    public void EssentialBundlesComeFirstThenBuiltInThenInstalled()
    {
        var bundles = Group(
            Plugin("z.installed", builtIn: false),
            Plugin("b.viewer", bundle: "b.viewer"),
            Plugin("y.core", bundle: "y.core", essential: true));

        Assert.Equal(["y.core", "b.viewer", "z.installed"], bundles.Select(b => b.Lead.Manifest.Id));
    }

    [Fact]
    public void CoreHasNoSwitchAndNoRemove()
    {
        var core = Single(Plugin("c.shell", bundle: "c.core", essential: true), Plugin("c.library", bundle: "c.core", essential: true));
        Assert.True(core.IsEssential);
        Assert.False(core.CanToggle);
        Assert.False(core.CanRemove);
        Assert.True(core.IsEnabled);
    }

    [Fact]
    public void BuiltInBundleCanBeSwitchedButNotRemoved()
    {
        var viewer = Single(Plugin("v.comic", bundle: "v.viewer"), Plugin("v.archives", bundle: "v.viewer"));
        Assert.True(viewer.IsBuiltIn);
        Assert.True(viewer.CanToggle);
        Assert.False(viewer.CanRemove);
        Assert.Equal(["v.archives", "v.comic"], viewer.ToggleIds);
    }

    [Fact]
    public void InstalledBundleRemovesEveryMember()
    {
        var video = Single(Plugin("m.video", bundle: "m.video", builtIn: false), Plugin("m.engine", bundle: "m.video", builtIn: false));
        Assert.True(video.CanRemove);
        Assert.Equal(["m.engine", "m.video"], video.RemovableIds);
        Assert.False(video.CanUndo);
    }

    [Fact]
    public void PendingRemovalOffersUndoAndLocksTheSwitch()
    {
        var video = Single(
            Plugin("m.video", bundle: "m.video", builtIn: false, state: PluginState.PendingRemoval),
            Plugin("m.engine", bundle: "m.video", builtIn: false, state: PluginState.PendingRemoval));
        Assert.Equal(BundleState.PendingRemoval, video.State);
        Assert.True(video.CanUndo);
        Assert.False(video.CanToggle);
        Assert.False(video.CanRemove);
        Assert.Equal(["m.engine", "m.video"], video.PendingIds);
    }

    [Theory]
    [InlineData(PluginState.Active, PluginState.Active, "On")]
    [InlineData(PluginState.Disabled, PluginState.Disabled, "Off")]
    [InlineData(PluginState.Active, PluginState.Disabled, "PartlyOn")]
    [InlineData(PluginState.Active, PluginState.Waiting, "Waiting")]
    [InlineData(PluginState.Waiting, PluginState.Failed, "Failed")]
    [InlineData(PluginState.Active, PluginState.PendingRemoval, "On")]
    public void StateSumsUpTheMembers(PluginState first, PluginState second, string expected)
    {
        var bundle = Single(Plugin("s.one", bundle: "s.b", state: first), Plugin("s.two", bundle: "s.b", state: second));
        Assert.Equal(Enum.Parse<BundleState>(expected), bundle.State);
    }

    [Fact]
    public void SwitchShowsOffWhenAnyMemberIsOff()
    {
        var bundle = Single(Plugin("s.one", bundle: "s.b"), Plugin("s.two", bundle: "s.b", enabled: false, state: PluginState.Disabled));
        Assert.False(bundle.IsEnabled);
    }

    [Fact]
    public void VersionIsTheHighestMemberVersion()
    {
        var bundle = Single(Plugin("s.one", bundle: "s.b", version: "0.9.0"), Plugin("s.two", bundle: "s.b", version: "0.10.0"));
        Assert.Equal("0.10.0", bundle.Version);
    }

    [Fact]
    public void UnknownBundleIsNamedAfterTheMemberWithItsId()
    {
        // No language service in tests: Loc returns the key, so no bundle name is "known".
        var bundle = Single(
            Plugin("n.engine", bundle: "n.video", name: "Engine"),
            Plugin("n.video", bundle: "n.video", name: "Videos"));
        Assert.Equal("Videos", BundleCardViewModel.NameOf(bundle, "en"));
    }

    [Fact]
    public async Task CardButtonsActOnTheWholeCard()
    {
        var toggled = new List<(string, bool)>();
        var removed = new List<string>();
        var card = new BundleCardViewModel(
            "bundle:m.video",
            (c, on) => toggled.AddRange(c.ToggleIds.Select(id => (id, on))),
            c =>
            {
                removed.AddRange(c.RemovableIds);
                return Task.CompletedTask;
            });
        card.Update(Single(Plugin("m.video", bundle: "m.video", builtIn: false), Plugin("m.engine", bundle: "m.video", builtIn: false)), "en");

        card.IsEnabled = false;
        await ((IAsyncRelayCommand)card.RemoveCommand).ExecuteAsync(null);

        Assert.Equal([("m.engine", false), ("m.video", false)], toggled);
        Assert.Equal(["m.engine", "m.video"], removed);
        Assert.True(card.ShowToggle);
        Assert.Equal(2, card.Members.Count);
    }

    [Fact]
    public void EveryBundleInTheRepositoryHasANameAndDescriptionInBothLanguages()
    {
        var bundles = Directory.GetFiles(Path.Combine(Root, "src", "plugins"), "plugin.json", SearchOption.AllDirectories)
            .Select(f => JsonDocument.Parse(File.ReadAllText(f)).RootElement)
            .Where(e => e.TryGetProperty("bundle", out _))
            .Select(e => e.GetProperty("bundle").GetString()!)
            .Distinct()
            .ToList();
        Assert.NotEmpty(bundles);

        foreach (var file in new[] { "en.json", "zh-TW.json" })
        {
            var keys = Keys(file);
            foreach (var bundle in bundles)
            {
                Assert.Contains(PluginBundles.NameKey(bundle), keys);
                Assert.Contains(PluginBundles.DescriptionKey(bundle), keys);
            }
        }
    }

    [Fact]
    public void EveryOfficialPluginIsInABundleAndCoreMembersAreEssential()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "src", "plugins"), "plugin.json", SearchOption.AllDirectories))
        {
            var manifest = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
            Assert.True(manifest.TryGetProperty("bundle", out var bundle), $"{file} has no bundle.");
            var essential = manifest.TryGetProperty("essential", out var flag) && flag.GetBoolean();
            Assert.Equal(bundle.GetString() == "magpienest.core", essential);
        }
    }

    private static PluginInfo Plugin(
        string id,
        string? bundle = null,
        bool builtIn = true,
        bool essential = false,
        bool enabled = true,
        PluginState state = PluginState.Active,
        string version = "1.0.0",
        string? name = null)
    {
        var manifest = new PluginManifest
        {
            Id = id,
            Version = version,
            Assembly = "T.dll",
            Bundle = bundle,
            Essential = essential,
            Name = new Dictionary<string, string> { ["en"] = name ?? id },
        };
        return new PluginInfo(manifest, state, enabled, builtIn, null, []);
    }

    private static IReadOnlyList<PluginBundle> Group(params PluginInfo[] plugins) =>
        PluginBundles.Group(plugins, b => b.Lead.Manifest.Id);

    private static PluginBundle Single(params PluginInfo[] plugins) => Assert.Single(Group(plugins));

    private static HashSet<string> Keys(string file)
    {
        var path = Path.Combine(Root, "src", "plugins", "Magpienest.Plugins.Settings", "lang", file);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Magpienest.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Magpienest.slnx not found above the test folder.");
    }
}
