using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Magpienest.Plugins.Library.Browser;

/// <summary>Rating stars and tag chips of one folder (DataContext: <see cref="FolderLabelsViewModel"/>).</summary>
internal sealed partial class FolderLabelsView : UserControl
{
    public FolderLabelsView() => InitializeComponent();
}

/// <summary>The folder labels being edited in the dialog.</summary>
internal sealed partial class FolderLabelsViewModel : ObservableObject
{
    public FolderLabelsViewModel(int rating, IReadOnlyList<string> tags, IReadOnlyList<string> suggestions)
    {
        Rating = rating;
        Tags = new TagEditorViewModel { Suggestions = suggestions };
        Tags.Reset(tags);
    }

    [ObservableProperty]
    public partial int Rating { get; set; }

    public TagEditorViewModel Tags { get; }

    /// <summary>Clicking the current rating again clears it, as in the details panel.</summary>
    [RelayCommand]
    private void SetRating(int value) => Rating = Rating == value ? 0 : value;
}
