// The exclusion masks below are adapted from Playnite/source/Playnite/Common/Programs.cs
// (scanFileExclusionMasks). Copyright (c) 2020 Josef Nemec, MIT licence.

using System.Text;
using System.Text.RegularExpressions;
using Magpienest.Sdk.Library;

namespace Magpienest.Plugins.Game;

/// <summary>What the import dialog can do with a folder.</summary>
internal enum GameCandidateStatus
{
    /// <summary>It has a program and can be imported.</summary>
    Ready,

    /// <summary>No usable executable was found in it.</summary>
    NoExecutable,

    /// <summary>Its program or its folder already belongs to a game in the library.</summary>
    AlreadyInLibrary,
}

/// <summary>One executable found below a candidate folder.</summary>
/// <param name="FullPath">Absolute path.</param>
/// <param name="RelativePath">Path relative to the candidate folder.</param>
/// <param name="Size">File size in bytes.</param>
/// <param name="Score">How likely it is the game's main program.</param>
internal sealed record GameExecutable(string FullPath, string RelativePath, long Size, int Score);

/// <summary>A folder that may hold one game.</summary>
/// <param name="Folder">Absolute path.</param>
/// <param name="Name">The folder's name (the default title).</param>
/// <param name="Status">What the import dialog does with it.</param>
/// <param name="Executables">Usable executables, best first; empty for <see cref="GameCandidateStatus.NoExecutable"/>.</param>
internal sealed record GameCandidate(string Folder, string Name, GameCandidateStatus Status, IReadOnlyList<GameExecutable> Executables)
{
    /// <summary>The best executable, or null.</summary>
    public GameExecutable? Default => Executables.Count > 0 ? Executables[0] : null;
}

/// <summary>The games already in the library, as the scanner needs to know them.</summary>
internal sealed class GameLibraryIndex
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>An index with no games.</summary>
    public static GameLibraryIndex Empty { get; } = new([], []);

    /// <summary>Creates an index from executable paths and install folders.</summary>
    /// <param name="executables">Executable paths of the games.</param>
    /// <param name="installFolders">Install folders of the games.</param>
    public GameLibraryIndex(IEnumerable<string> executables, IEnumerable<string> installFolders)
    {
        Executables = executables.Select(Normalize).ToHashSet(Comparer);
        InstallFolders = installFolders.Select(Normalize).ToHashSet(Comparer);
    }

    private HashSet<string> Executables { get; }

    private HashSet<string> InstallFolders { get; }

    /// <summary>Builds the index from the library's game items (a game without an install folder lives in its executable's folder).</summary>
    /// <param name="games">Game items.</param>
    public static GameLibraryIndex From(IEnumerable<LibraryItem> games)
    {
        var list = games.ToList();
        return new GameLibraryIndex(list.Select(g => g.Path), list.Select(GameExtra.EffectiveInstallDir));
    }

    /// <summary>Whether the candidate's main program or folder is already a game.</summary>
    /// <param name="folder">Candidate folder.</param>
    /// <param name="executable">Candidate's default executable.</param>
    public bool Contains(string folder, string executable) =>
        Executables.Contains(Normalize(executable)) || InstallFolders.Contains(Normalize(folder));

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

/// <summary>
/// Finds games in a folder of games (T4.2): each sub-folder is a candidate, its executables are filtered
/// and scored, the best one is the default. Pure and synchronous; call it on a background thread.
/// </summary>
internal sealed class GameFolderScanner(Func<string, string?>? productName = null)
{
    /// <summary>How many folder levels below a candidate are searched.</summary>
    public const int MaxDepth = 4;

    /// <summary>How many executables are collected per candidate.</summary>
    public const int MaxFiles = 300;

    /// <summary>Product names are read for this many of the best-scoring executables only (reading is slow).</summary>
    private const int ProductNameLimit = 40;

    // Installers, helpers and runtimes that are never the game itself (Playnite's list plus a few more).
    private static readonly Regex[] Masks = new[]
    {
        "uninst", "setup", @"unins\d+", "config", "dxsetup", "vc_redist", "vcredist", "redist",
        @"^notification_helper\.exe$", @"^python(w)?\.exe$", @"^zsync(make)?\.exe$",
        "update", "crashhandler", "crashreport", "crashpad", "helper",
        "^UE4PrereqSetup", "^UEPrereqSetup", "^QtWebEngineProcess", @"^CefSharp\.BrowserSubprocess",
        "^dotnetfx", "^oalinst", "^installer",
    }.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)).ToArray();

    private static readonly string[] Unwanted = ["editor", "server", "benchmark", "tool", "test"];

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
    };

    private readonly Func<string, string?> _productName = productName ?? GameFormat.ProductNameOf;

    /// <summary>Whether a file name (with extension) is an installer, helper or runtime to leave out.</summary>
    /// <param name="fileName">File name.</param>
    public static bool IsMasked(string fileName) => Masks.Any(m => m.IsMatch(fileName));

    /// <summary>Lower-case letters and digits only.</summary>
    /// <param name="text">Any text.</param>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>Analyses a folder of games.</summary>
    /// <param name="root">The folder the user picked.</param>
    /// <param name="library">The games already in the library.</param>
    /// <param name="ct">Cancellation.</param>
    public IReadOnlyList<GameCandidate> Analyze(string root, GameLibraryIndex library, CancellationToken ct = default)
    {
        var folders = SubFolders(root).ToList();
        if (folders.Count == 0)
        {
            // A folder of one game: the folder itself is the only candidate (if it has a program at all).
            return CollectExecutables(root, ct).Count > 0 ? [AnalyzeFolder(root, library, ct)] : [];
        }

        return [.. folders.Select(folder =>
        {
            ct.ThrowIfCancellationRequested();
            return AnalyzeFolder(folder, library, ct);
        })];
    }

    private GameCandidate AnalyzeFolder(string folder, GameLibraryIndex library, CancellationToken ct)
    {
        var name = new DirectoryInfo(folder).Name;
        var files = CollectExecutables(folder, ct).Where(f => !IsMasked(f.Name)).ToList();
        if (files.Count == 0)
        {
            return new GameCandidate(folder, name, GameCandidateStatus.NoExecutable, []);
        }

        var executables = Score(folder, name, files, ct);
        var status = library.Contains(folder, executables[0].FullPath)
            ? GameCandidateStatus.AlreadyInLibrary
            : GameCandidateStatus.Ready;
        return new GameCandidate(folder, name, status, executables);
    }

    private List<GameExecutable> Score(string folder, string folderName, List<FileInfo> files, CancellationToken ct)
    {
        var folderKey = Normalize(folderName);
        var largest = files.Max(f => f.Length);
        var entries = files.Select(file =>
        {
            var relative = Path.GetRelativePath(folder, file.FullName);
            var fileKey = Normalize(Path.GetFileNameWithoutExtension(file.Name));
            var score = 0;
            if (fileKey.Length > 0 && fileKey == folderKey)
            {
                score += 100;
            }
            else if (Contains(fileKey, folderKey) || Contains(folderKey, fileKey))
            {
                score += 60;
            }

            if (largest > 0 && file.Length == largest)
            {
                score += 20;
            }

            score -= 15 * Depth(relative);
            if (Unwanted.Any(w => file.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            {
                score -= 50;
            }

            return (File: file, Relative: relative, Score: score);
        }).ToList();

        // The product name needs the file to be opened; only the contenders are worth that.
        var contenders = entries.OrderByDescending(e => e.Score).Take(ProductNameLimit).Select(e => e.File.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<GameExecutable>(entries.Count);
        foreach (var (file, relative, score) in entries)
        {
            ct.ThrowIfCancellationRequested();
            var total = score;
            if (contenders.Contains(file.FullName) && _productName(file.FullName) is { } product &&
                Normalize(product) is { Length: > 0 } productKey && productKey == folderKey)
            {
                total += 30;
            }

            result.Add(new GameExecutable(file.FullName, relative, file.Length, total));
        }

        return [.. result
            .OrderByDescending(e => e.Score)
            .ThenBy(e => Depth(e.RelativePath))
            .ThenBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>"One contains the other" needs the contained text to be at least four characters long.</summary>
    private static bool Contains(string text, string part) => part.Length >= 4 && text.Contains(part, StringComparison.Ordinal);

    private static int Depth(string relativePath) => relativePath.Count(c => c == Path.DirectorySeparatorChar);

    private static IEnumerable<string> SubFolders(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateDirectories("*", Options)
                .Select(d => d.FullName)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Every .exe up to <see cref="MaxDepth"/> levels below the folder, shallow ones first, at most <see cref="MaxFiles"/>.</summary>
    private static List<FileInfo> CollectExecutables(string folder, CancellationToken ct)
    {
        var found = new List<FileInfo>();
        var level = new List<string> { folder };
        for (var depth = 0; depth <= MaxDepth && level.Count > 0 && found.Count < MaxFiles; depth++)
        {
            var next = new List<string>();
            foreach (var current in level)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var directory = new DirectoryInfo(current);
                    foreach (var file in directory.EnumerateFiles("*.exe", Options)
                                 .Where(f => f.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        if (found.Count >= MaxFiles)
                        {
                            return found;
                        }

                        found.Add(file);
                    }

                    if (depth < MaxDepth)
                    {
                        next.AddRange(directory.EnumerateDirectories("*", Options)
                            .Select(d => d.FullName)
                            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An unreadable folder is skipped.
                }
            }

            level = next;
        }

        return found;
    }
}
