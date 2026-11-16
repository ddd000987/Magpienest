namespace Magpienest.Plugins.Catalogue;

/// <summary>The button a store card shows.</summary>
internal enum StoreCardAction
{
    /// <summary>"Install".</summary>
    Install,

    /// <summary>"Update".</summary>
    Update,

    /// <summary>"Installed", disabled.</summary>
    Installed,

    /// <summary>"Needs a newer Magpienest", disabled.</summary>
    Incompatible,

    /// <summary>"Removed after restart", disabled.</summary>
    PendingRemoval,

    /// <summary>"Cancel" while this bundle is being downloaded.</summary>
    Cancel,
}

/// <summary>What the store page shows (phase 8 T8.2). Pure logic, no UI.</summary>
internal static class StoreCards
{
    /// <summary>The button of a card.</summary>
    /// <param name="state">The bundle's state on this computer.</param>
    /// <param name="installingThis">This bundle is being installed right now.</param>
    public static StoreCardAction ActionFor(StoreBundleState state, bool installingThis) =>
        installingThis
            ? StoreCardAction.Cancel
            : state switch
            {
                StoreBundleState.NotInstalled => StoreCardAction.Install,
                StoreBundleState.UpdateAvailable => StoreCardAction.Update,
                StoreBundleState.Incompatible => StoreCardAction.Incompatible,
                StoreBundleState.PendingRemoval => StoreCardAction.PendingRemoval,
                _ => StoreCardAction.Installed,
            };

    /// <summary>Whether the button can be pressed. Only one bundle is installed at a time.</summary>
    /// <param name="action">The card's button.</param>
    /// <param name="anotherInstalling">A different bundle is being installed right now.</param>
    public static bool CanAct(StoreCardAction action, bool anotherInstalling) => action switch
    {
        StoreCardAction.Cancel => true,
        StoreCardAction.Install or StoreCardAction.Update => !anotherInstalling,
        _ => false,
    };

    /// <summary>Localization key of the button text.</summary>
    public static string ButtonKey(StoreCardAction action) => "Catalogue.Page.Action." + action;

    /// <summary>
    /// Localization key of the text above the cards, or null when there is nothing to say (the cards speak for
    /// themselves).
    /// </summary>
    /// <param name="refreshing">The catalogue is being fetched.</param>
    /// <param name="error">Why the last fetch failed, or null.</param>
    /// <param name="fetched">A catalogue has been fetched at least once.</param>
    /// <param name="bundleCount">Number of bundles in that catalogue.</param>
    public static string? StatusKey(bool refreshing, StoreError? error, bool fetched, int bundleCount)
    {
        if (refreshing)
        {
            return "Catalogue.Page.Loading";
        }

        if (error is { } code)
        {
            return StoreService.MessageKey(code);
        }

        return fetched && bundleCount == 0 ? "Catalogue.Page.Empty" : null;
    }
}
