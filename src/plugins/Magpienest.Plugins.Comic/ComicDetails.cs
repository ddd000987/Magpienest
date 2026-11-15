using System.Windows;
using System.Windows.Controls;
using Magpienest.Sdk;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Viewing;

namespace Magpienest.Plugins.Comic;

/// <summary>Kind-specific block of the details panel: pages, progress, reading time, per-book reading overrides.</summary>
internal static class ComicDetails
{
    public static FrameworkElement Create(LibraryItem item, ILibraryService library, ILog log)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var unknown = Loc.Get("Comic.Details.Unknown");
        AddRow(grid, "Comic.Details.Pages", Text(item.ProgressTotal > 0 ? item.ProgressTotal.ToString() : unknown));
        AddRow(grid, "Comic.Details.Progress", Text(ComicBooks.Subtitle(item) ?? unknown));
        AddRow(grid, "Comic.Details.Time", Text(FormatTime(item.TimeSpentSeconds)));

        var extra = item.GetExtra<ComicExtra>();
        var direction = Choice(
            [(null, "Comic.Details.Default"), ("rtl", "Comic.Details.RightToLeft"), ("ltr", "Comic.Details.LeftToRight")],
            extra.Direction);
        // "Pages shown" is one choice, like the viewer's button: one page, two pages or a strip.
        var spread = Choice(
            [(null, "Comic.Details.Default"), ("single", "Comic.Details.Single"), ("double", "Comic.Details.Double"),
             ("strip", "Comic.Details.Strip")],
            extra.Flow == "strip" ? "strip" : extra.Spread);
        AddRow(grid, "Comic.Details.Direction", direction);
        AddRow(grid, "Comic.Details.Spread", spread);

        void Save(object sender, SelectionChangedEventArgs e)
        {
            var shown = spread.SelectedValue as string;
            var updated = new ComicExtra
            {
                Direction = (direction.SelectedValue as string),
                Spread = shown == "strip" ? extra.Spread : shown,   // a strip keeps the stored spread
                CoverAlone = extra.CoverAlone,
                Flow = shown switch { null => null, "strip" => "strip", _ => "paged" },
            };
            _ = SaveAsync(library, item.Id, updated, log);
        }

        direction.SelectionChanged += Save;
        spread.SelectionChanged += Save;
        return grid;
    }

    private static async Task SaveAsync(ILibraryService library, long id, ComicExtra extra, ILog log)
    {
        try
        {
            await library.UpdateAsync(id, new ItemPatch { Extra = LibraryItemExtensions.SerializeExtra(extra) });
        }
        catch (Exception ex)
        {
            log.Warn("Saving the reading settings failed.", ex);
        }
    }

    internal static string FormatTime(long seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return Loc.Format("Comic.Details.TimeValue", (int)time.TotalHours, time.Minutes);
    }

    private static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };

    private static ComboBox Choice(IReadOnlyList<(string? Value, string Key)> options, string? current)
    {
        var box = new ComboBox { SelectedValuePath = "Value", DisplayMemberPath = "Label", Margin = new Thickness(0, 2, 0, 2), MinWidth = 120 };
        box.ItemsSource = options.Select(o => new Option(o.Value, Loc.Get(o.Key))).ToList();
        box.SelectedIndex = Math.Max(0, options.ToList().FindIndex(o => o.Value == current));
        return box;
    }

    private static void AddRow(Grid grid, string labelKey, FrameworkElement value)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock
        {
            Text = Loc.Get(labelKey),
            Margin = new Thickness(0, 2, 12, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Grid.SetRow(label, row);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(label);
        grid.Children.Add(value);
    }

    private sealed record Option(string? Value, string Label);
}
