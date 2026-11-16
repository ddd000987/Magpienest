using System.Diagnostics.CodeAnalysis;
using System.Windows.Controls;
using Magpienest.Plugins.Settings;
using Magpienest.Sdk;
using Magpienest.Sdk.Settings;

namespace Magpienest.Plugins.Settings.Tests;

/// <summary>The dots set through <see cref="ISettingsService.SetBadge"/>.</summary>
public sealed class BadgeTests
{
    [Fact]
    public void DotShowsWhileAnyOwnerHasItOn()
    {
        var badges = new BadgeSet();
        var store = new Owner();
        var other = new Owner();

        badges.Set(store, "store", true);
        badges.Set(other, "store", true);
        badges.Set(store, "store", false);
        Assert.True(badges.Has("store"));

        badges.Set(other, "store", false);
        Assert.False(badges.Has("store"));
        Assert.False(badges.Any);
    }

    [Fact]
    public void ChangedIsRaisedOnlyWhenTheDotAppearsOrDisappears()
    {
        var badges = new BadgeSet();
        var changes = new List<string>();
        badges.Changed += changes.Add;
        var owner = new Owner();

        badges.Set(owner, "store", true);
        badges.Set(owner, "store", true);
        badges.Set(owner, "store", false);
        badges.Set(owner, "store", false);

        Assert.Equal(["store", "store"], changes);
    }

    [Fact]
    public void DeactivatingTheOwnerClearsItsDotsAndTracksOnlyOnce()
    {
        var badges = new BadgeSet();
        var owner = new Owner();
        badges.Set(owner, "store", true);
        badges.Set(owner, "store", false);
        badges.Set(owner, "store", true);
        badges.Set(owner, "plugins", true);

        Assert.Equal(2, owner.Effects.Count);
        owner.Deactivate();

        Assert.False(badges.Any);
    }

    [Fact]
    public void SectionAddedLaterShowsAnEarlierDot()
    {
        var page = new SettingsPageViewModel(new NullLog());
        page.Badges.Set(new Owner(), "store", true);

        page.Add(Section("store"));
        page.Add(Section("general"));

        Assert.True(page.Sections.Single(s => s.Section.Id == "store").HasBadge);
        Assert.False(page.Sections.Single(s => s.Section.Id == "general").HasBadge);
    }

    [Fact]
    public void RefreshBadgeFollowsTheSet()
    {
        var page = new SettingsPageViewModel(new NullLog());
        page.Add(Section("store"));
        var owner = new Owner();

        page.Badges.Set(owner, "store", true);
        page.RefreshBadge("store");
        Assert.True(page.Sections[0].HasBadge);

        page.Badges.Set(owner, "store", false);
        page.RefreshBadge("store");
        Assert.False(page.Sections[0].HasBadge);
    }

    private static SettingsSection Section(string id) => new() { Id = id, TitleKey = id, CreateView = () => new Grid() };

    private sealed class NullLog : ILog
    {
        public void Debug(string message) { }

        public void Info(string message) { }

        public void Warn(string message, Exception? ex = null) { }

        public void Error(string message, Exception? ex = null) { }
    }

    /// <summary>Only <see cref="Track(Action)"/> is used by the badge code.</summary>
    private sealed class Owner : IPluginContext
    {
        public List<Action> Effects { get; } = [];

        public PluginManifest Manifest => throw new NotSupportedException();

        public string PluginDirectory => throw new NotSupportedException();

        public string DataDirectory => throw new NotSupportedException();

        public IAppEnvironment Environment => throw new NotSupportedException();

        public ILog Log => throw new NotSupportedException();

        public IPluginConfig Config => throw new NotSupportedException();

        public IUiDispatcher Ui => throw new NotSupportedException();

        public CancellationToken Deactivating => CancellationToken.None;

        public void Deactivate()
        {
            for (var i = Effects.Count - 1; i >= 0; i--)
            {
                Effects[i]();
            }
        }

        public T Get<T>() where T : class => throw new NotSupportedException();

        public bool TryGet<T>([NotNullWhen(true)] out T? service) where T : class => throw new NotSupportedException();

        public void Provide<T>(T implementation) where T : class => throw new NotSupportedException();

        public void On<TEvent>(Action<TEvent> handler) => throw new NotSupportedException();

        public void Emit<TEvent>(TEvent evt) => throw new NotSupportedException();

        public void Track(IDisposable effect) => Effects.Add(effect.Dispose);

        public void Track(Action undo) => Effects.Add(undo);
    }
}
