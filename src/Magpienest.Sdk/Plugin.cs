namespace Magpienest.Sdk;

/// <summary>
/// Base class of every plugin. A new instance is created for every activation.
/// </summary>
public abstract class Plugin
{
    /// <summary>
    /// Called on the UI thread once every <see cref="RequiresAttribute"/> service is available.
    /// Register everything through <paramref name="ctx"/>. Must not block (no .Wait()/.Result, no long I/O).
    /// </summary>
    /// <param name="ctx">The context of this activation; every side effect goes through it.</param>
    public abstract void Activate(IPluginContext ctx);

    /// <summary>
    /// Optional hook, called on the UI thread right before the context's effects are disposed.
    /// </summary>
    public virtual void Deactivate() { }
}

/// <summary>
/// Declares services the plugin cannot work without. The plugin is activated only when all are available.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class RequiresAttribute : Attribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="services">Service interface types.</param>
    public RequiresAttribute(params Type[] services) => Services = services;

    /// <summary>The required service interface types.</summary>
    public Type[] Services { get; }
}

/// <summary>
/// Declares services the plugin uses when available. The plugin is restarted when their availability changes.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class OptionalAttribute : Attribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="services">Service interface types.</param>
    public OptionalAttribute(params Type[] services) => Services = services;

    /// <summary>The optional service interface types.</summary>
    public Type[] Services { get; }
}
