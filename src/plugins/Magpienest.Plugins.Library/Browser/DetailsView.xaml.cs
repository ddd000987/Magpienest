using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Magpienest.Plugins.Library.Browser;

/// <summary>The details panel (DataContext: <see cref="DetailsViewModel"/>).</summary>
internal sealed partial class DetailsView : UserControl
{
    public DetailsView() => InitializeComponent();

    private DetailsViewModel? Vm => DataContext as DetailsViewModel;

    private BrowserViewModel? Browser =>
        (FindAncestor<BrowserView>(this))?.DataContext as BrowserViewModel;

    private void TitleBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => Vm?.CommitTitle();

    private void TitleBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Vm?.CommitTitle();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Vm?.Item is { } item)
        {
            Vm.Title = item.Title;
            e.Handled = true;
        }
    }

    private void BulkTagBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddBulkTag();
            e.Handled = true;
        }
    }

    private void BulkTagAdd_Click(object sender, RoutedEventArgs e) => AddBulkTag();

    private void AddBulkTag()
    {
        var text = BulkTagBox.Text;
        BulkTagBox.Text = "";
        Browser?.AddTagToSelectionCommand.Execute(text);
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        var current = System.Windows.Media.VisualTreeHelper.GetParent(start);
        while (current is not null and not T)
        {
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return current as T;
    }
}
