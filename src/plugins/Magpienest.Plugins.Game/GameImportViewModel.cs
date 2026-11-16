using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpienest.Sdk.Localization;

namespace Magpienest.Plugins.Game;

/// <summary>One folder in the import dialog.</summary>
internal sealed partial class GameImportRow : ObservableObject
{
    public GameImportRow(GameCandidate candidate)
    {
        Candidate = candidate;
        Title = candidate.Name;
        SelectedExecutable = candidate.Default;
        IsChecked = candidate.Status == GameCandidateStatus.Ready;
    }

    public GameCandidate Candidate { get; }

    public IReadOnlyList<GameExecutable> Executables => Candidate.Executables;

    /// <summary>Only folders that are ready can be chosen.</summary>
    public bool IsSelectable => Candidate.Status == GameCandidateStatus.Ready;

    public string StatusText => Candidate.Status switch
    {
        GameCandidateStatus.Ready => Loc.Get("Game.Import.Status.Ready"),
        GameCandidateStatus.NoExecutable => Loc.Get("Game.Import.Status.NoExecutable"),
        _ => Loc.Get("Game.Import.Status.AlreadyInLibrary"),
    };

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial GameExecutable? SelectedExecutable { get; set; }

    /// <summary>Checked, with a title and a program: it will be imported.</summary>
    public bool WillImport => IsSelectable && IsChecked && SelectedExecutable is not null && !string.IsNullOrWhiteSpace(Title);

    partial void OnIsCheckedChanged(bool value) => Changed?.Invoke();

    partial void OnTitleChanged(string value) => Changed?.Invoke();

    partial void OnSelectedExecutableChanged(GameExecutable? value) => Changed?.Invoke();

    internal event Action? Changed;
}

/// <summary>The rows of the import dialog and what the "Import" button needs to know.</summary>
internal sealed partial class GameImportViewModel : ObservableObject
{
    public GameImportViewModel(string root, IReadOnlyList<GameCandidate> candidates)
    {
        Root = root;
        foreach (var candidate in candidates)
        {
            var row = new GameImportRow(candidate);
            row.Changed += () => OnPropertyChanged(nameof(CanAccept));
            Rows.Add(row);
        }
    }

    public string Root { get; }

    public ObservableCollection<GameImportRow> Rows { get; } = [];

    public string Summary => Loc.Format("Game.Import.Summary", Rows.Count, Root);

    public bool CanAccept => Rows.Any(r => r.WillImport);

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var row in Rows.Where(r => r.IsSelectable))
        {
            row.IsChecked = true;
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var row in Rows)
        {
            row.IsChecked = false;
        }
    }
}
