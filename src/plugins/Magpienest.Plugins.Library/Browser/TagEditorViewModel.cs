using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Magpienest.Plugins.Library.Browser;

/// <summary>Tag chips plus an add box with suggestions. Used by the details panel and the "Edit tags" dialog.</summary>
internal sealed partial class TagEditorViewModel : ObservableObject
{
    public ObservableCollection<string> Tags { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string> Suggestions { get; set; } = [];

    [ObservableProperty]
    public partial string NewTag { get; set; } = "";

    /// <summary>Raised after the user added or removed a tag.</summary>
    public event Action? Changed;

    public void Reset(IEnumerable<string> tags)
    {
        Tags.Clear();
        foreach (var tag in tags)
        {
            Tags.Add(tag);
        }
    }

    [RelayCommand]
    private void Add()
    {
        var tag = (NewTag ?? "").Trim();
        NewTag = "";
        if (tag.Length == 0 || Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Tags.Add(tag);
        Changed?.Invoke();
    }

    [RelayCommand]
    private void Remove(string? tag)
    {
        if (tag is not null && Tags.Remove(tag))
        {
            Changed?.Invoke();
        }
    }
}
