using System.Diagnostics;
using System.Text;

namespace Magpienest.Plugins.Settings;

/// <summary>
/// Builds the process that starts Magpienest again. The host waits for <c>--wait-for-pid</c> to exit before it
/// takes the single-instance lock, so the new process can be started before the running one shuts down.
/// </summary>
internal static class RestartCommand
{
    /// <summary>The arguments of the new process: wait for the old one, keep the data folder, then everything the host did not consume.</summary>
    /// <param name="processId">Id of the running instance.</param>
    /// <param name="dataDirectory">The data folder of the running instance (so the same one is used again).</param>
    /// <param name="commandLineArgs">The arguments the running instance was given, without --data-dir.</param>
    public static IReadOnlyList<string> AppArguments(int processId, string dataDirectory, IReadOnlyList<string> commandLineArgs) =>
        ["--wait-for-pid", processId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--data-dir", dataDirectory, .. commandLineArgs];

    /// <summary>Creates the start info that runs the exe again.</summary>
    /// <param name="exePath">Path of the exe to start again.</param>
    /// <param name="appArguments">See <see cref="AppArguments"/>.</param>
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
