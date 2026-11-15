using Magpienest.Sdk;

namespace Magpienest.Core.Tests;

/// <summary>The 22 lifecycle cases of phase-0 T0.3, one test each.</summary>
public sealed class KernelLifecycleTests
{
    [Fact]
    public void T01_PluginWithoutRequirementsBecomesActive()
    {
        using var h = new Harness().Add<P1>("t.p1");
        h.Start();
        Assert.Equal(PluginState.Active, h.State("t.p1"));
    }

    [Fact]
    public void T02_RequiredServiceFromLaterIdStillActivates()
    {
        using var h = new Harness().Add<NeedsA>("a.consumer").Add<P1>("z.provider");
        h.OnActivate<P1>(ctx => ctx.Provide<IServiceA>(new ServiceA()));
        h.Start();
        Assert.Equal(PluginState.Active, h.State("a.consumer"));
        Assert.Equal(PluginState.Active, h.State("z.provider"));
    }

    [Fact]
    public void T03_UndeclaredGetAndTryGetThrow()
    {
        using var h = new Harness().Add<P1>("t.p1");
        Exception? get = null, tryGet = null;
        IPluginManager? manager = null;
        h.OnActivate<P1>(ctx =>
        {
            get = Record.Exception(() => ctx.Get<IServiceA>());
            tryGet = Record.Exception(() => ctx.TryGet<IServiceA>(out _));
            manager = ctx.Get<IPluginManager>();
        });
        h.Start();
        var getError = Assert.IsType<InvalidOperationException>(get);
        Assert.Equal("Plugin 't.p1' must declare [Requires(typeof(IServiceA))] to use IServiceA.", getError.Message);
        Assert.IsType<InvalidOperationException>(tryGet);
        Assert.NotNull(manager);
    }

    [Fact]
    public void T04_SecondProviderFailsFirstUnaffected()
    {
        using var h = new Harness().Add<P1>("t.a1").Add<P2>("t.a2").Add<NeedsA>("t.consumer");
        var first = new ServiceA();
        IServiceA? seen = null;
        h.OnActivate<P1>(ctx => ctx.Provide<IServiceA>(first));
        h.OnActivate<P2>(ctx => ctx.Provide<IServiceA>(new ServiceA()));
        h.OnActivate<NeedsA>(ctx => seen = ctx.Get<IServiceA>());
        h.Start();
        Assert.Equal(PluginState.Active, h.State("t.a1"));
        Assert.Equal(PluginState.Failed, h.State("t.a2"));
        Assert.Contains("already provided", h.Info("t.a2").Error);
        Assert.Equal(PluginState.Active, h.State("t.consumer"));
        Assert.Same(first, seen);
    }

    [Fact]
    public void T05_ActivateThrowsRollsBackEverything()
    {
        using var h = new Harness().Add<P1>("t.bad").Add<NeedsA>("t.consumer").Add<P2>("t.listener");
        var effectRan = false;
        var handled = 0;
        h.OnActivate<P1>(ctx =>
        {
            ctx.Provide<IServiceA>(new ServiceA());
            ctx.On<TestEvent>(_ => handled++);
            ctx.Track(() => effectRan = true);
            throw new InvalidOperationException("boom");
        });
        var kernel = h.Start();
        var info = h.Info("t.bad");
        Assert.Equal(PluginState.Failed, info.State);
        Assert.Equal("boom", info.Error);
        Assert.True(effectRan);
        Assert.Equal(PluginState.Waiting, h.State("t.consumer"));
        kernel.Emit(new TestEvent("x"));
        Assert.Equal(0, handled);
    }

    [Fact]
    public void T06_DisablingProviderDeactivatesDependentsFirst()
    {
        using var h = new Harness().Add<P1>("t.provider").Add<NeedsA>("t.middle").Add<NeedsB>("t.leaf");
        h.OnActivate<P1>(ctx => ctx.Provide<IServiceA>(new ServiceA()));
        h.OnActivate<NeedsA>(ctx => ctx.Provide<IServiceB>(new ServiceB()));
        var kernel = h.Start();
        Assert.All(new[] { "t.provider", "t.middle", "t.leaf" }, id => Assert.Equal(PluginState.Active, h.State(id)));

        h.Log.Clear();
        kernel.Plugins.SetEnabled("t.provider", false);
        Assert.Equal(["deactivate:t.leaf", "deactivate:t.middle", "deactivate:t.provider"], h.Log);
        Assert.Equal(PluginState.Disabled, h.State("t.provider"));
        Assert.Equal(PluginState.Waiting, h.State("t.middle"));
        Assert.Equal(["IServiceA"], h.Info("t.middle").MissingServices);
        Assert.Equal(PluginState.Waiting, h.State("t.leaf"));
        Assert.Equal(["IServiceB"], h.Info("t.leaf").MissingServices);

        kernel.Plugins.SetEnabled("t.provider", true);
        Assert.All(new[] { "t.provider", "t.middle", "t.leaf" }, id => Assert.Equal(PluginState.Active, h.State(id)));
    }

    [Fact]
    public void T07_EffectsRunLifoAndAThrowingEffectDoesNotStopOthers()
    {
        using var h = new Harness().Add<P1>("t.p1");
        var order = new List<int>();
        h.OnActivate<P1>(ctx =>
        {
            ctx.Track(() => order.Add(1));
            ctx.Track(() =>
            {
                order.Add(2);
                throw new InvalidOperationException("effect failed");
            });
            ctx.Track(new DisposableAction(() => order.Add(3)));
        });
        var kernel = h.Start();
        kernel.Plugins.SetEnabled("t.p1", false);
        Assert.Equal([3, 2, 1], order);
        Assert.Equal(PluginState.Disabled, h.State("t.p1"));
    }

    [Fact]
    public void T08_OptionalServiceRestartsPluginWhenAvailabilityChanges()
    {
        using var h = new Harness().Add<OptionalA>("t.optional").Add<P1>("t.provider", enabledByDefault: false);
        var seen = new List<bool>();
        h.OnActivate<P1>(ctx => ctx.Provide<IServiceA>(new ServiceA()));
        h.OnActivate<OptionalA>(ctx => seen.Add(ctx.TryGet<IServiceA>(out _)));
        var kernel = h.Start();
        Assert.Equal([false], seen);

        kernel.Plugins.SetEnabled("t.provider", true);
        Assert.Equal([false, true], seen);
        Assert.Equal(PluginState.Active, h.State("t.optional"));

        kernel.Plugins.SetEnabled("t.provider", false);
        Assert.Equal([false, true, false], seen);
        Assert.Equal(3, h.Activations["t.optional"]);
        Assert.Equal(PluginState.Active, h.State("t.optional"));
    }

    [Fact]
    public void T09_EssentialPluginCannotBeDisabled()
    {
        using var h = new Harness().Add<P1>("t.essential", essential: true);
        var kernel = h.Start();
        Assert.Throws<InvalidOperationException>(() => kernel.Plugins.SetEnabled("t.essential", false));
        Assert.Equal(PluginState.Active, h.State("t.essential"));
    }

    [Fact]
    public void T10_SetEnabledIsPersistedAcrossKernels()
    {
        using var h = new Harness().Add<P1>("t.p1").Add<P2>("t.p2", enabledByDefault: false);
        var first = h.Start();
        first.Plugins.SetEnabled("t.p1", false);
        first.Plugins.SetEnabled("t.p2", true);
        first.Shutdown();

        var second = h.CreateKernel();
        second.Start();
        var infos = second.Plugins.Plugins.ToDictionary(p => p.Manifest.Id);
        Assert.Equal(PluginState.Disabled, infos["t.p1"].State);
        Assert.False(infos["t.p1"].IsEnabled);
        Assert.Equal(PluginState.Active, infos["t.p2"].State);
        second.Shutdown();
    }

    [Fact]
    public void T11_HandlersRemovedOnDeactivationAndThrowingHandlerIsolated()
    {
        using var h = new Harness().Add<P1>("t.thrower").Add<P2>("t.listener");
        var received = new List<string>();
        h.OnActivate<P1>(ctx => ctx.On<TestEvent>(_ => throw new InvalidOperationException("handler failed")));
        h.OnActivate<P2>(ctx => ctx.On<TestEvent>(e => received.Add(e.Value)));
        var kernel = h.Start();

        kernel.Emit(new TestEvent("one"));
        Assert.Equal(["one"], received);

        kernel.Plugins.SetEnabled("t.listener", false);
        kernel.Emit(new TestEvent("two"));
        Assert.Equal(["one"], received);
    }

    [Fact]
    public void T12_DeactivatingTokenIsCancelledDuringDeactivation()
    {
        using var h = new Harness().Add<P1>("t.p1");
        CancellationToken token = default;
        bool? cancelledInDeactivate = null;
        h.OnActivate<P1>(ctx => token = ctx.Deactivating);
        h.OnDeactivate<P1>(ctx => cancelledInDeactivate = ctx.Deactivating.IsCancellationRequested);
        var kernel = h.Start();
        Assert.False(token.IsCancellationRequested);

        kernel.Plugins.SetEnabled("t.p1", false);
        Assert.True(cancelledInDeactivate);
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void T13_EachActivationGetsANewInstance()
    {
        using var h = new Harness().Add<P1>("t.p1");
        var kernel = h.Start();
        kernel.Plugins.SetEnabled("t.p1", false);
        kernel.Plugins.SetEnabled("t.p1", true);
        Assert.Equal(2, h.Instances.Count);
        Assert.NotSame(h.Instances[0], h.Instances[1]);
    }

    [Fact]
    public void T14_ShutdownEmitsFirstThenDeactivatesInReverseOrder()
    {
        using var h = new Harness().Add<P1>("t.a").Add<P2>("t.b").Add<P3>("t.c");
        h.OnActivate<P3>(ctx => ctx.On<AppShuttingDown>(_ => Harness.Current.Log.Add("event:AppShuttingDown")));
        var kernel = h.Start();
        h.Log.Clear();
        kernel.Shutdown();
        Assert.Equal(["event:AppShuttingDown", "deactivate:t.c", "deactivate:t.b", "deactivate:t.a"], h.Log);
    }

    [Fact]
    public void T15_MutualRequirementsStayWaitingWithoutHanging()
    {
        using var h = new Harness().Add<NeedsA>("t.needs-a").Add<NeedsB>("t.needs-b");
        h.OnActivate<NeedsA>(ctx => ctx.Provide<IServiceB>(new ServiceB()));
        h.OnActivate<NeedsB>(ctx => ctx.Provide<IServiceA>(new ServiceA()));
        h.Start();
        Assert.Equal(PluginState.Waiting, h.State("t.needs-a"));
        Assert.Equal(PluginState.Waiting, h.State("t.needs-b"));
        Assert.Equal(["IServiceA"], h.Info("t.needs-a").MissingServices);
    }

    [Fact]
    public void T16_FailedIsStickyUntilDisableAndEnable()
    {
        using var h = new Harness().Add<P1>("t.flaky").Add<P2>("t.other");
        var attempts = 0;
        h.OnActivate<P1>(_ =>
        {
            if (++attempts == 1)
            {
                throw new InvalidOperationException("first attempt fails");
            }
        });
        var kernel = h.Start();
        Assert.Equal(PluginState.Failed, h.State("t.flaky"));

        kernel.Plugins.SetEnabled("t.other", false);
        kernel.Plugins.SetEnabled("t.other", true);
        Assert.Equal(PluginState.Failed, h.State("t.flaky"));
        Assert.Equal(1, attempts);

        kernel.Plugins.SetEnabled("t.flaky", false);
        kernel.Plugins.SetEnabled("t.flaky", true);
        Assert.Equal(PluginState.Active, h.State("t.flaky"));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void T17_ManifestProblems()
    {
        using var h = new Harness();

        // Invalid JSON: skipped by the directory source, the other plugins load.
        var root = Path.Combine(h.DataDirectory, "disk-plugins");
        Directory.CreateDirectory(Path.Combine(root, "broken"));
        File.WriteAllText(Path.Combine(root, "broken", "plugin.json"), "{ this is not json");
        Directory.CreateDirectory(Path.Combine(root, "missing-id"));
        File.WriteAllText(Path.Combine(root, "missing-id", "plugin.json"), """{ "version": "1.0.0", "assembly": "X.dll" }""");
        Directory.CreateDirectory(Path.Combine(root, "good"));
        File.WriteAllText(Path.Combine(root, "good", "plugin.json"), """
            // comments and trailing commas are allowed
            { "id": "t.disk", "version": "1.0.0", "assembly": "Missing.dll", "name": { "en": "Disk", }, }
            """);
        var disk = new DirectoryPluginSource(root, isBuiltIn: false).Discover(new NullLog()).ToList();
        Assert.Equal(["t.disk"], disk.Select(c => c.Manifest.Id));
        Assert.Equal("Disk", disk[0].Manifest.Name["EN"]);

        // Duplicate id: the built-in copy wins regardless of order. Wrong API version: Failed.
        h.Add<P2>("t.dup", builtIn: false).Add<P1>("t.dup", builtIn: true).Add<P3>("t.future", apiVersion: 2);
        h.Start();
        Assert.Single(h.Kernel!.Plugins.Plugins, p => p.Manifest.Id == "t.dup");
        Assert.True(h.Info("t.dup").IsBuiltIn);
        Assert.IsType<P1>(Assert.Single(h.Instances));
        var future = h.Info("t.future");
        Assert.Equal(PluginState.Failed, future.State);
        Assert.Equal("Plugin API version 2 is not supported (host: 1)", future.Error);
    }

    [Fact]
    public void T18_PluginActivatingBeforeActivateAndStateChangedAfterTransitions()
    {
        using var h = new Harness().Add<P1>("t.a.observer").Add<P2>("t.b.target");
        h.OnActivate<P1>(ctx =>
        {
            ctx.On<PluginActivating>(e => Harness.Current.Log.Add("activating:" + e.Manifest.Id));
            ctx.On<PluginStateChanged>(e => Harness.Current.Log.Add($"state:{e.Plugin.Manifest.Id}:{e.Plugin.State}"));
        });
        var kernel = h.Start();
        var startLog = h.Log.SkipWhile(l => l != "activating:t.b.target").ToList();
        Assert.Equal(["activating:t.b.target", "activate:t.b.target", "state:t.b.target:Active"], startLog);

        h.Log.Clear();
        kernel.Plugins.SetEnabled("t.b.target", false);
        Assert.Equal(["deactivate:t.b.target", "state:t.b.target:Disabled"], h.Log);

        h.Log.Clear();
        kernel.Plugins.SetEnabled("t.b.target", true);
        Assert.Equal(["state:t.b.target:Waiting", "activating:t.b.target", "activate:t.b.target", "state:t.b.target:Active"], h.Log);
    }

    [Fact]
    public void T19_PluginConfigDefaultsPersistenceAndCorruptRecovery()
    {
        using var h = new Harness().Add<P1>("t.config");
        var values = new List<int>();
        h.OnActivate<P1>(ctx =>
        {
            values.Add(ctx.Config.Get("count", 5));
            ctx.Config.Set("count", 7);
        });

        var first = h.Start();
        first.Shutdown();
        var second = h.CreateKernel();
        second.Start();
        second.Shutdown();
        Assert.Equal([5, 7], values);

        var path = Path.Combine(h.DataDirectory, "config", "t.config.json");
        File.WriteAllText(path, "{ corrupt");
        var third = h.CreateKernel();
        third.Start();
        third.Shutdown();
        Assert.Equal([5, 7, 5], values);
        Assert.Single(Directory.GetFiles(Path.Combine(h.DataDirectory, "config"), "t.config.json.corrupt-*"));
    }

    [Fact]
    public void T20_OwnerConventionRemovesRegistrationOfDisabledPlugin()
    {
        using var h = new Harness().Add<P1>("t.registry").Add<UsesRegistry>("t.user");
        var registry = new Registry();
        h.OnActivate<P1>(ctx => ctx.Provide<IRegistry>(registry));
        h.OnActivate<UsesRegistry>(ctx => ctx.Get<IRegistry>().Add(ctx, "item"));
        var kernel = h.Start();
        Assert.Equal(["item"], registry.Items);

        kernel.Plugins.SetEnabled("t.user", false);
        Assert.Empty(registry.Items);
        Assert.Equal(PluginState.Active, h.State("t.registry"));
    }

    [Fact]
    public void T21_EmitFromBackgroundThreadReachesHandlersOnUiThread()
    {
        using var h = new Harness().Add<P1>("t.listener");
        var uiThread = Environment.CurrentManagedThreadId;
        var received = new List<(string Value, int Thread)>();
        h.OnActivate<P1>(ctx => ctx.On<TestEvent>(e => received.Add((e.Value, Environment.CurrentManagedThreadId))));
        var kernel = h.Start();

        var background = new Thread(() => kernel.Emit(new TestEvent("bg")));
        background.Start();
        background.Join();
        Assert.Empty(received);
        Assert.Equal(1, h.Dispatcher.RunPending());
        Assert.Equal([("bg", uiThread)], received);
    }

    [Fact]
    public void T22_ProvideThenThrowLeavesNoServiceRegistered()
    {
        using var h = new Harness().Add<P1>("t.bad").Add<NeedsA>("t.consumer").Add<P2>("t.late");
        h.OnActivate<P1>(ctx =>
        {
            ctx.Provide<IServiceA>(new ServiceA());
            throw new InvalidOperationException("after provide");
        });
        h.OnActivate<P2>(ctx => ctx.Provide<IServiceA>(new ServiceA()));
        h.Start();
        Assert.Equal(PluginState.Failed, h.State("t.bad"));

        // t.late could register the same service, so the failed plugin left nothing behind;
        // the consumer then activates against t.late's service.
        Assert.Equal(PluginState.Active, h.State("t.late"));
        Assert.Equal(PluginState.Active, h.State("t.consumer"));
    }

    private sealed class DisposableAction(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private sealed class NullLog : ILog
    {
        public void Debug(string message) { }

        public void Info(string message) { }

        public void Warn(string message, Exception? ex = null) { }

        public void Error(string message, Exception? ex = null) { }
    }
}
