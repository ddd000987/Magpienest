using CommunityToolkit.Mvvm.ComponentModel;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;

namespace Magpienest.Plugins.Library.Browser;

/// <summary>One node of the folder tree: the "All" root, a source folder, or a folder below a source.</summary>
internal sealed partial class FolderNode : ObservableObject
{
    public FolderNode(string name, string? path, long? sourceId)
    {
        Name = name;
        Path = path;
        SourceId = sourceId;
    }

    /// <summary>Display name (the "All" root gets a localized name from the caller).</summary>
    public string Name { get; }

    /// <summary>Full folder path; null for the "All" root.</summary>
    public string? Path { get; }

    /// <summary>Set on source nodes.</summary>
    public long? SourceId { get; }

    public bool IsRoot => Path is null;

    /// <summary>Key for expansion and selection state: the path, or "" for the root.</summary>
    public string Key => Path ?? "";

    /// <summary>The folder's own rating, 0 = none.</summary>
    public int Rating { get; private set; }

    /// <summary>The folder's own tags.</summary>
    public IReadOnlyList<string> Tags { get; private set; } = [];

    public bool HasRating => Rating > 0;

    public bool HasTags => Tags.Count > 0;

    /// <summary>The path, then the folder's rating and tags when it has them.</summary>
    public string ToolTip
    {
        get
        {
            var lines = new List<string> { Path ?? Name };
            if (HasRating)
            {
                lines.Add(Loc.Format("Library.Folder.ToolTipRating", Rating));
            }

            if (HasTags)
            {
                lines.Add(Loc.Format("Library.Folder.ToolTipTags", string.Join(Loc.Get("Library.Folder.TagSeparator"), Tags)));
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>Shows a folder's labels (set while the tree is built).</summary>
    public void SetLabels(int rating, IReadOnlyList<string> tags)
    {
        if (Rating == rating && Tags.SequenceEqual(tags, StringComparer.Ordinal))
        {
            return;
        }

        Rating = rating;
        Tags = tags;
        OnPropertyChanged(nameof(Rating));
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(HasRating));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(ToolTip));
    }

    public List<FolderNode> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Called when the node becomes selected.</summary>
    internal Action<FolderNode>? Selected { get; set; }

    public IEnumerable<FolderNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var node in child.SelfAndDescendants())
            {
                yield return node;
            }
        }
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            Selected?.Invoke(this);
        }
    }
}

/// <summary>Builds the folder tree in memory from the sources and the distinct item directories.</summary>
internal static class FolderTreeBuilder
{
    /// <param name="rootName">Name of the "All" node.</param>
    /// <param name="sources">Sources of the kind.</param>
    /// <param name="directories">Distinct directories of the kind's items (normalised paths).</param>
    /// <param name="expansion">
    /// Expansion state of the previous tree by key. Nodes not in it get the default: sources expanded, folders collapsed.
    /// </param>
    public static FolderNode Build(string rootName, IReadOnlyList<LibrarySource> sources, IReadOnlyList<string> directories,
                                   IReadOnlyDictionary<string, bool>? expansion)
    {
        var root = new FolderNode(rootName, null, null) { IsExpanded = true };
        var byPath = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
        var sourceNodes = new List<(LibrarySource Source, FolderNode Node)>();

        foreach (var source in sources)
        {
            var node = new FolderNode(DisplayName(source.Path), source.Path, source.Id)
            {
                IsExpanded = expansion?.GetValueOrDefault(source.Path, true) ?? true,
            };
            root.Children.Add(node);
            byPath[source.Path] = node;
            sourceNodes.Add((source, node));
        }

        foreach (var directory in directories)
        {
            var owner = sourceNodes.FirstOrDefault(s => LibraryRules.IsSameOrInside(directory, s.Source.Path));
            if (owner.Node is null)
            {
                continue; // a manual item outside every source: only reachable through "All"
            }

            var relative = directory.Length > owner.Source.Path.Length ? directory[owner.Source.Path.Length..] : "";
            var parent = owner.Node;
            var current = owner.Source.Path;
            foreach (var segment in relative.Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = System.IO.Path.Combine(current, segment);
                if (!byPath.TryGetValue(current, out var child))
                {
                    child = new FolderNode(segment, current, null) { IsExpanded = expansion?.GetValueOrDefault(current, false) ?? false };
                    byPath[current] = child;
                    parent.Children.Add(child);
                }

                parent = child;
            }
        }

        SortRecursive(root);
        return root;
    }

    private static string DisplayName(string path)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static void SortRecursive(FolderNode node)
    {
        node.Children.Sort((a, b) => LibraryRules.Natural.Compare(a.Name, b.Name));
        foreach (var child in node.Children)
        {
            SortRecursive(child);
        }
    }
}
