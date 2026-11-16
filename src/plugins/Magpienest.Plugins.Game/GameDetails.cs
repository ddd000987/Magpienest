using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;

namespace Magpienest.Plugins.Game;

/// <summary>Kind-specific block of the details panel: Play, play time, last played, launch count, paths, Edit.</summary>
internal static class GameDetails
{
    public static FrameworkElement Create(LibraryItem item, GameLauncher launcher, PlaySessionTracker tracker, Func<LibraryItem, Task> edit)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

        var play = new Button
        {
            Content = Loc.Get("Game.Details.Play"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 8),
        };
        play.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        play.Click += (_, _) => _ = tracker.IsTracking(item.Id) ? tracker.StopAsync(item.Id) : launcher.PlayAsync(item);
        panel.Children.Add(play);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var sessionValue = Text("");
        var sessionLabel = AddRow(grid, "Game.Details.Session", sessionValue);
        AddRow(grid, "Game.Details.PlayTime", Text(GameFormat.PlayTime(item.TimeSpentSeconds, item.OpenCount)));
        AddRow(grid, "Game.Details.LastPlayed", Text(item.LastOpenedAt is { } last
            ? last.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : Loc.Get("Game.Details.Never")));
        AddRow(grid, "Game.Details.Launches", Text(item.OpenCount.ToString(CultureInfo.CurrentCulture)));
        AddRow(grid, "Game.Details.Executable", ReadOnlyPath(item.Path));
        AddRow(grid, "Game.Details.InstallFolder", ReadOnlyPath(GameExtra.EffectiveInstallDir(item)));
        AddRow(grid, "Game.Details.WorkingFolder", ReadOnlyPath(GameExtra.EffectiveWorkingDir(item)));
        panel.Children.Add(grid);

        var editButton = new Button
        {
            Content = Loc.Get("Game.Details.Edit"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
        };
        editButton.Click += (_, _) => _ = edit(item);
        panel.Children.Add(editButton);

        // While the game is followed: the button stops tracking and the session time runs. The timer and the
        // tracker's event only exist while the block is on screen.
        void Refresh()
        {
            var time = tracker.SessionTime(item.Id);
            play.Content = Loc.Get(time is null ? "Game.Details.Play" : "Game.Details.StopTracking");
            var visibility = time is null ? Visibility.Collapsed : Visibility.Visible;
            sessionLabel.Visibility = visibility;
            sessionValue.Visibility = visibility;
            sessionValue.Text = time is { } span ? GameFormat.SessionTime(span) : "";
        }

        var timer = new DispatcherTimer(DispatcherPriority.Background, panel.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh();
        void OnSessionsChanged() => panel.Dispatcher.BeginInvoke(Refresh);
        panel.Loaded += (_, _) =>
        {
            tracker.SessionsChanged -= OnSessionsChanged;
            tracker.SessionsChanged += OnSessionsChanged;
            Refresh();
            timer.Start();
        };
        panel.Unloaded += (_, _) =>
        {
            timer.Stop();
            tracker.SessionsChanged -= OnSessionsChanged;
        };
        Refresh();
        return panel;
    }

    private static TextBlock Text(string value) =>
        new() { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };

    private static TextBox ReadOnlyPath(string path)
    {
        var box = new TextBox
        {
            Text = path,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 2, 0, 2),
            ToolTip = path,
        };
        return box;
    }

    private static TextBlock AddRow(Grid grid, string labelKey, FrameworkElement value)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock
        {
            Text = Loc.Get(labelKey),
            Margin = new Thickness(0, 2, 12, 2),
            VerticalAlignment = VerticalAlignment.Top,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Grid.SetRow(label, row);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(label);
        grid.Children.Add(value);
        return label;
    }
}
