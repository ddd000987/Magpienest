using System.Diagnostics;

namespace Magpienest.Plugins.Catalogue;

/// <summary>
/// Builds the process that starts Magpienest again (the same as the Settings plugin's; plugins cannot share
/// code). The host waits for <c>--wait-for-pid</c> to exit before it takes the single-instance lock.
/// </summary>
internal static class RestartCommand
{
    /// <summary>The arguments of the new process: wait for the old one, keep the data folder, then everything the host did not consume.</summary>
    public static IReadOnlyList<string> AppArguments(int processId, string dataDirectory, IReadOnlyList<string> commandLineArgs) =>
        ["--wait-for-pid", processId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--data-dir", dataDirectory, .. commandLineArgs];

    /// <summary>Creates the start info that runs the exe again.</summary>
    public static ProcessStartInfo Create(string exePath, IReadOnlyList<string> appArguments)
    {
        var info = new ProcessStartInfo(exePath) { UseShellExecute = false };
        foreach (var argument in appArguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }
}
