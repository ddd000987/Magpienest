using System.Diagnostics;
using Magpienest.Sdk;

namespace Magpienest.Plugins.Library.Browser;

/// <summary>Opens Windows Explorer at a file (selected) or, when the file is gone, at the nearest existing folder.</summary>
internal static class ExplorerLauncher
{
    public static void Show(string path, ILog log)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
                return;
            }

            var folder = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
            {
                folder = Path.GetDirectoryName(folder);
            }

            if (!string.IsNullOrEmpty(folder))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false });
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Explorer could not be started for {path}.", ex);
        }
    }
}
