using Magpienest.Sdk.Library;

namespace Magpienest.Plugins.Game;

/// <summary>The <c>--import-games</c> command line option; pure so it can be tested.</summary>
internal static class GameCommandLine
{
    /// <summary>The folder after <c>--import-games</c>, or null when the option is absent or has no value.</summary>
    /// <param name="args">Command-line arguments.</param>
    public static string? ImportFolder(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], "--import-games", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>The games to add for the analysed folders: every ready candidate, with its default program and folder name.</summary>
    /// <param name="candidates">Result of <see cref="GameFolderScanner.Analyze"/>.</param>
    public static IReadOnlyList<NewItem> SelectImports(IEnumerable<GameCandidate> candidates) =>
    [
        .. candidates
            .Where(c => c.Status == GameCandidateStatus.Ready && c.Default is not null)
            .Select(c => new NewItem
            {
                Kind = GamePlugin.Kind,
                Path = c.Default!.FullPath,
                Title = c.Name,
                Extra = LibraryItemExtensions.SerializeExtra(new GameExtra { InstallDir = c.Folder }),
            }),
    ];
}
