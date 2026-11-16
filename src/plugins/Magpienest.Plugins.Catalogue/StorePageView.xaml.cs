using System.Windows;
using System.Windows.Controls;

namespace Magpienest.Plugins.Catalogue;

/// <summary>View of the "store" Settings section. Fetches the catalogue each time it is shown.</summary>
internal sealed partial class StorePageView : UserControl
{
    public StorePageView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => (DataContext as StorePageViewModel)?.OnShown();
}
