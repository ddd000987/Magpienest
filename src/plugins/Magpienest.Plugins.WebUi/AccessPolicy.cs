namespace Magpienest.Plugins.WebUi;

/// <summary>Who can connect (setting <c>access</c>).</summary>
internal enum AccessMode
{
    /// <summary>This PC only (default).</summary>
    Local,

    /// <summary>This PC and the home network.</summary>
    Home,

    /// <summary>Also from the internet.</summary>
    Internet,
}

/// <summary>What happens to a request.</summary>
internal enum AccessDecision
{
    /// <summary>Serve it.</summary>
    Allow,

    /// <summary>A password session is needed: the sign-in page (pages) or 401 (API).</summary>
    SignIn,

    /// <summary>The access link from the app is needed (this PC, no password set).</summary>
    OpenFromApp,

    /// <summary>403: the request could get in without a password but its <c>Host</c> is not one of ours.</summary>
    Forbidden,

    /// <summary>Not served at all: the address is outside what the current mode allows.</summary>
    Drop,
}

/// <summary>Everything the decision depends on.</summary>
/// <param name="Mode">Access mode.</param>
/// <param name="From">Class of the remote address.</param>
/// <param name="HostAllowed">Whether the Host header passes <see cref="RequestGuards.IsAllowedHost"/>.</param>
/// <param name="HasToken">Whether the request carries the valid access token.</param>
/// <param name="HasSession">Whether the request carries a valid password session.</param>
/// <param name="PasswordSet">Whether a password exists.</param>
/// <param name="RequirePasswordAtHome">The "require a password on the home network" setting.</param>
internal sealed record AccessRequest(AccessMode Mode, AddressClass From, bool HostAllowed, bool HasToken, bool HasSession,
                                     bool PasswordSet, bool RequirePasswordAtHome);

/// <summary>The one decision function of phase 7 "How a request is allowed in".</summary>
internal static class AccessPolicy
{
    /// <summary>Decides what happens to a request.</summary>
    /// <param name="r">The request's facts.</param>
    public static AccessDecision Decide(AccessRequest r)
    {
        // Outside the mode's reach: nothing at all.
        if ((r.From == AddressClass.Internet && r.Mode != AccessMode.Internet) ||
            (r.From == AddressClass.Home && r.Mode == AccessMode.Local))
        {
            return AccessDecision.Drop;
        }

        // A password session works from anywhere the mode allows, whatever the Host.
        if (r.HasSession)
        {
            return AccessDecision.Allow;
        }

        switch (r.From)
        {
            case AddressClass.Loopback:
                if (r.HasToken)
                {
                    return r.HostAllowed ? AccessDecision.Allow : AccessDecision.Forbidden;
                }

                return r.PasswordSet ? AccessDecision.SignIn : AccessDecision.OpenFromApp;

            case AddressClass.Home:
                if (r.RequirePasswordAtHome)
                {
                    return AccessDecision.SignIn;
                }

                return r.HostAllowed ? AccessDecision.Allow : AccessDecision.Forbidden;

            default:
                return AccessDecision.SignIn;
        }
    }
}
