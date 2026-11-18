using System.Globalization;
using Magpienest.Sdk.Library;

namespace Magpienest.Plugins.Library.Browser;

/// <summary>
/// Sort memory per folder. The "All folders" root keeps the kind's sort ("sort", "descending"); a folder whose sort
/// was changed keeps its own in "folderSorts"; any other folder uses its nearest folder above that has one, else the root's.
/// </summary>
internal sealed partial class BrowserViewModel
{
    /// <summary>Above this many remembered folders, those no longer in the tree are forgotten.</summary>
    private const int FolderSortPruneThreshold = 500;

    private const string FolderSortsKey = "folderSorts";

    /// <summary>The sort a folder shows: its own, else its nearest remembered folder above, else the root's.</summary>
    private (ItemSort Sort, bool Descending) SortFor(FolderNode? folder) =>
        folder is { IsRoot: false } ? SortFor(folder, FolderSorts()) : RootSort();

    private (ItemSort Sort, bool Descending) RootSort()
    {
        var stored = Config("sort", Options.DefaultSort.ToString());
        var sort = Enum.TryParse<ItemSort>(stored, out var parsed) && SortOptions.Any(o => o.Value == parsed) ? parsed : SortOptions[0].Value;
        return (sort, Config("descending", false));
    }

    /// <summary>Shows <paramref name="folder"/>'s remembered sort without storing anything (no query is started).</summary>
    private void ApplySortFor(FolderNode? folder)
    {
        var (sort, descending) = SortFor(folder);
        var wasRestoring = _restoring;
        _restoring = true;
        try
        {
            SelectedSort = SortOptions.FirstOrDefault(o => o.Value == sort) ?? SelectedSort;
            Descending = descending;
        }
        finally
        {
            _restoring = wasRestoring;
        }
    }

    /// <summary>Stores the current sort for the selected folder (the root stores the kind's sort).</summary>
    private void RememberSort()
    {
        var folder = SelectedFolder;
        if (folder is not { IsRoot: false, Path: { } path })
        {
            SetConfig("sort", SelectedSort.Value.ToString());
            SetConfig("descending", Descending);
            return;
        }

        var sorts = new Dictionary<string, string>(FolderSorts(), StringComparer.OrdinalIgnoreCase);
        sorts.Remove(path);
        var inherited = SortFor(folder, sorts);
        if (inherited != (SelectedSort.Value, Descending))
        {
            sorts[path] = FormatSort(SelectedSort.Value, Descending);
        }

        if (sorts.Count > FolderSortPruneThreshold && FolderRoots.FirstOrDefault() is { } root)
        {
            var known = root.SelfAndDescendants().Select(n => n.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in sorts.Keys.Where(k => !known.Contains(k)).ToList())
            {
                sorts.Remove(key);
            }
        }

        SetConfig(FolderSortsKey, sorts);
    }

    private (ItemSort Sort, bool Descending) SortFor(FolderNode folder, IReadOnlyDictionary<string, string> sorts)
    {
        for (var current = folder.Path; !string.IsNullOrEmpty(current); current = System.IO.Path.GetDirectoryName(current))
        {
            if (sorts.TryGetValue(current, out var stored) && TryParseSort(stored, out var remembered))
            {
                return remembered;
            }
        }

        return RootSort();
    }

    private Dictionary<string, string> FolderSorts()
    {
        var stored = Config<Dictionary<string, string>?>(FolderSortsKey, null);
        return stored is null ? new(StringComparer.OrdinalIgnoreCase) : new(stored, StringComparer.OrdinalIgnoreCase);
    }

    private static string FormatSort(ItemSort sort, bool descending) =>
        string.Create(CultureInfo.InvariantCulture, $"{sort}|{(descending ? "desc" : "asc")}");

    private bool TryParseSort(string text, out (ItemSort Sort, bool Descending) result)
    {
        result = default;
        var parts = text.Split('|');
        if (parts.Length != 2 || !Enum.TryParse<ItemSort>(parts[0], out var sort) || SortOptions.All(o => o.Value != sort))
        {
            return false;
        }

        result = (sort, string.Equals(parts[1], "desc", StringComparison.Ordinal));
        return true;
    }
}
