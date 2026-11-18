using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;

namespace Magpienest.Plugins.Library.Browser;

/// <summary>The library browser view. Only view concerns live here: selection sync, scroll keeping, menus, keys.</summary>
internal sealed partial class BrowserView : UserControl
{
    private readonly BrowserViewModel _vm;
    private bool _syncingSelection;
    private ScrollViewer? _scroll;

    public BrowserView(BrowserViewModel vm)
    {
        _vm = vm;

        // BAML resolves "assembly=VirtualizingWrapPanel" among already-loaded assemblies only; it never asks the
        // plugin's load context. Touching the type loads it into this plugin's context first.
        _ = typeof(WpfToolkit.Controls.VirtualizingWrapPanel).Assembly;
        InitializeComponent();
        DataContext = vm;
        vm.ItemsReplaced += OnItemsReplaced;
        PreviewKeyDown += OnPreviewKeyDown;
        if (vm.Items.Count > 0)
        {
            Tiles.ItemsSource = vm.Items;
        }
    }

    private ScrollViewer? Scroll => _scroll ??= FindChild<ScrollViewer>(Tiles);

    private void OnItemsReplaced(ItemsReplacedArgs e)
    {
        var scroll = Scroll;
        var old = Tiles.ItemsSource as IReadOnlyList<TileViewModel>;
        var perRow = 1;
        long? anchorId = null;
        double anchorDelta = 0, oldOffset = 0;
        var rowHeight = Math.Max(1, _vm.CellHeight);
        if (e.PreserveScroll && scroll is not null && old is { Count: > 0 })
        {
            perRow = Math.Max(1, (int)(scroll.ViewportWidth / Math.Max(1, _vm.CellWidth)));
            oldOffset = scroll.VerticalOffset;
            var topRow = (int)(oldOffset / rowHeight);
            anchorId = old[Math.Min(topRow * perRow, old.Count - 1)].Id;
            anchorDelta = oldOffset - (topRow * rowHeight);
        }

        _syncingSelection = true;
        try
        {
            Tiles.ItemsSource = e.Items;
            Tiles.SetSelection(e.Selected);
        }
        finally
        {
            _syncingSelection = false;
        }

        if (scroll is null)
        {
            return;
        }

        if (!e.PreserveScroll)
        {
            scroll.ScrollToTop();
            return;
        }

        if (anchorId is not { } id)
        {
            return;
        }

        var target = oldOffset;
        for (var i = 0; i < e.Items.Count; i++)
        {
            if (e.Items[i].Id == id)
            {
                target = (i / perRow * rowHeight) + anchorDelta;
                break;
            }
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => scroll.ScrollToVerticalOffset(target));
    }

    private TileViewModel? CurrentTile() =>
        (Keyboard.FocusedElement as ListBoxItem)?.DataContext as TileViewModel ?? Tiles.SelectedItem as TileViewModel;

    // ---- keyboard ------------------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SearchBox.Text.Length > 0)
        {
            _vm.ClearSearchCommand.Execute(null);
            e.Handled = true;
        }
    }
    private void Tiles_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && CurrentTile() is { } tile)
        {
            _ = _vm.OpenAsync(tile);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && _vm.SelectedTiles.Count > 0)
        {
            _vm.RemoveSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ---- grid ----------------------------------------------------------------------------------

    private void Tiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSelection)
        {
            _vm.SetSelection([.. Tiles.SelectedItems.Cast<TileViewModel>()]);
        }
    }

    private void Tile_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && sender is ListBoxItem { DataContext: TileViewModel tile })
        {
            _ = _vm.OpenAsync(tile);
            e.Handled = true;
        }
    }

    private void Tile_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TileViewModel tile })
        {
            tile.Attach();
        }
    }

    private void Tile_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TileViewModel tile })
        {
            tile.Detach();
        }
    }

    private void Tile_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_vm.Options.TileHovered is { } hovered && sender is FrameworkElement { DataContext: TileViewModel tile } element)
        {
            hovered(element, tile.Item);
        }
    }

    private void Tile_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        (e.OldValue as TileViewModel)?.Detach();
        if (sender is FrameworkElement { IsLoaded: true } && e.NewValue is TileViewModel tile)
        {
            tile.Attach();
        }
    }

    private void FoldersButton_Click(object sender, RoutedEventArgs e)
    {
        if (FoldersButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = FoldersButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.DataContext = _vm;
            menu.IsOpen = true;
        }
    }

    /// <summary>Right-click on a folder (not "All"): edit its tags and rating without selecting it.</summary>
    // ---- folder pane ---------------------------------------------------------------------------

    /// <summary>Room kept free at the right of a folder header (vertical scroll bar, padding).</summary>
    private const double FolderHeaderSlack = 20;

    private readonly HashSet<FrameworkElement> _folderHeaders = [];

    private void FolderHeader_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement header)
        {
            _folderHeaders.Add(header);
            FitFolderHeader(header);
            Dispatcher.BeginInvoke(() => FitFolderHeader(header), DispatcherPriority.Loaded);
        }
    }

    private void FolderHeader_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement header)
        {
            _folderHeaders.Remove(header);
        }
    }

    private void Folders_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        foreach (var header in _folderHeaders)
        {
            FitFolderHeader(header);
        }
    }

    /// <summary>Limits a header to the tree's width minus its indent, so a long name is cut instead of widening the tree.</summary>
    private void FitFolderHeader(FrameworkElement header)
    {
        if (!header.IsLoaded || !Folders.IsAncestorOf(header))
        {
            return;
        }

        var left = header.TransformToAncestor(Folders).Transform(default).X;
        header.MaxWidth = Math.Max(40, Folders.ActualWidth - left - FolderHeaderSlack);
    }

    private void FolderPaneEdge_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) =>
        _vm.ResizeFolderPane(_vm.FolderPaneWidth + e.HorizontalChange, save: false);

    private void FolderPaneEdge_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        _vm.ResizeFolderPane(_vm.FolderPaneWidth, save: true);

    private void Folders_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var container = e.OriginalSource as DependencyObject;
        while (container is not null and not TreeViewItem)
        {
            container = container is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(container)
                : LogicalTreeHelper.GetParent(container);
        }

        if (container is not TreeViewItem { DataContext: FolderNode { IsRoot: false } node })
        {
            e.Handled = true;
            return;
        }

        FolderMenu.Items.Clear();
        FolderMenu.Items.Add(Item("Library.Folder.EditLabels", "", () => _vm.EditFolderLabelsCommand.Execute(node)));
    }

    private void Tiles_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_vm.SelectedTiles.Count == 0)
        {
            e.Handled = true;
            return;
        }

        TileMenu.Items.Clear();
        var current = CurrentTile();
        if (current is not null)
        {
            TileMenu.Items.Add(Item("Library.Menu.Open", "", () => _ = _vm.OpenAsync(current), "Enter"));
        }

        TileMenu.Items.Add(Item("Library.Menu.ShowInExplorer", "", () => _vm.ShowInExplorerCommand.Execute(null)));
        TileMenu.Items.Add(new Separator());

        var allFavorite = _vm.SelectionIsAllFavorite;
        var favorite = Item("Library.Menu.Favorite", null, () => _vm.SetFavoriteCommand.Execute(!allFavorite));
        favorite.IsCheckable = true;
        favorite.IsChecked = allFavorite;
        TileMenu.Items.Add(favorite);

        var rating = Item("Library.Menu.Rating", "", null);
        for (var stars = 0; stars <= 5; stars++)
        {
            var value = stars;
            var entry = new MenuItem
            {
                Header = stars == 0 ? Loc.Get("Library.Rating.None") : new string('★', stars) + new string('☆', 5 - stars),
                IsCheckable = true,
                IsChecked = _vm.SelectedTiles.All(t => t.Item.Rating == value),
            };
            entry.Click += (_, _) => _vm.SetRatingCommand.Execute(value);
            rating.Items.Add(entry);
        }

        TileMenu.Items.Add(rating);

        if (_vm.ShowStatus)
        {
            var status = Item("Library.Menu.Status", null, null);
            foreach (var value in Enum.GetValues<ItemStatus>())
            {
                var entry = new MenuItem
                {
                    Header = Loc.Get($"Library.Status.{value}"),
                    IsCheckable = true,
                    IsChecked = _vm.SelectedTiles.All(t => t.Item.Status == value),
                };
                entry.Click += (_, _) => _vm.SetStatusCommand.Execute(value);
                status.Items.Add(entry);
            }

            TileMenu.Items.Add(status);
        }

        TileMenu.Items.Add(Item("Library.Menu.EditTags", "", () => _vm.EditTagsCommand.Execute(null)));

        var commands = _vm.ApplicableCommands();
        if (commands.Count > 0)
        {
            TileMenu.Items.Add(new Separator());
            foreach (var command in commands)
            {
                TileMenu.Items.Add(Item(command.TitleKey, command.Glyph, () => _vm.RunCommandCommand.Execute(command)));
            }
        }

        TileMenu.Items.Add(new Separator());
        TileMenu.Items.Add(Item("Library.Menu.Remove", "", () => _vm.RemoveSelectedCommand.Execute(null), "Del"));
    }

    private MenuItem Item(string key, string? glyph, Action? click, string? gesture = null)
    {
        var item = new MenuItem { Header = Loc.Get(key), InputGestureText = gesture ?? "" };
        if (glyph is not null)
        {
            item.Icon = new TextBlock { Text = glyph, Style = (Style)Resources["Glyph"], FontSize = 14 };
        }

        if (click is not null)
        {
            item.Click += (_, _) => click();
        }

        return item;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindChild<T>(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}

/// <summary>A list box whose selection can be replaced in one batch (thousands of items without per-item events).</summary>
internal sealed class TileListBox : ListBox
{
    public void SetSelection(IReadOnlyList<TileViewModel> tiles)
    {
        if (tiles.Count == 0)
        {
            UnselectAll();
            return;
        }

        SetSelectedItems(tiles);
    }
}
