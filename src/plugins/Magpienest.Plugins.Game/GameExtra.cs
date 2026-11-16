using System.Diagnostics;
using System.Globalization;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;

namespace Magpienest.Plugins.Game;

/// <summary>The kind-specific extra JSON of a game. <c>item.Path</c> is the executable.</summary>
internal sealed record GameExtra
{
    /// <summary>The game's folder; null means the executable's folder.</summary>
    public string? InstallDir { get; init; }

    /// <summary>Command-line arguments.</summary>
    public string Arguments { get; init; } = "";

    /// <summary>The working folder; null means the executable's folder.</summary>
    public string? WorkingDir { get; init; }

    /// <summary>The install folder to show and open.</summary>
    public static string EffectiveInstallDir(LibraryItem item) =>
        NullIfBlank(item.GetExtra<GameExtra>().InstallDir) ?? item.Directory;

    /// <summary>The folder the game is started in.</summary>
    public static string EffectiveWorkingDir(LibraryItem item) =>
        NullIfBlank(item.GetExtra<GameExtra>().WorkingDir) ?? item.Directory;

    /// <summary>Stores a folder only when the user chose one.</summary>
    public static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Texts and rules shown to the user; pure so they can be tested.</summary>
internal static class GameFormat
{
    /// <summary>Turns a localization key and arguments into text; tests replace it.</summary>
    internal delegate string Formatter(string key, params object?[] args);

    /// <summary>"3 h 20 min", "&lt;1 min", or "Never played" (nothing recorded and never started).</summary>
    public static string PlayTime(long seconds, int openCount, Formatter? format = null)
    {
        format ??= Loc.Format;
        if (seconds <= 0)
        {
            return openCount > 0 ? format("Game.PlayTime.LessThanMinute") : format("Game.PlayTime.Never");
        }

        var minutes = seconds / 60;
        if (minutes < 1)
        {
            return format("Game.PlayTime.LessThanMinute");
        }

        var hours = minutes / 60;
        return hours > 0
            ? format("Game.PlayTime.HoursMinutes", hours, minutes % 60)
            : format("Game.PlayTime.Minutes", minutes);
    }

    /// <summary>Length of the running session as "h:mm:ss" (hours are not wrapped at 24).</summary>
    /// <param name="time">Session time.</param>
    public static string SessionTime(TimeSpan time)
    {
        var total = (long)Math.Max(0, time.TotalSeconds);
        return string.Create(CultureInfo.InvariantCulture, $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}");
    }

    /// <summary>
    /// The title offered for a new game: the executable's ProductName when it has one, else the name of the
    /// folder it sits in (the same rule as Playnite). Reads the file, so call it off the UI thread.
    /// </summary>
    public static string DefaultTitle(string exePath, Func<string, string?>? productName = null)
    {
        var name = (productName ?? ProductNameOf)(exePath);
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(exePath) ?? ""));
        return string.IsNullOrWhiteSpace(folder) ? Path.GetFileNameWithoutExtension(exePath) : folder;
    }

    /// <summary>The ProductName stored in the executable, or null when there is none or it cannot be read.</summary>
    public static string? ProductNameOf(string exePath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(exePath).ProductName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
