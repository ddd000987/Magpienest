using System.Windows;
using System.Windows.Controls;

namespace Magpienest.Plugins.WebUi;

/// <summary>The "Web interface" settings section. Follows the server only while it is shown.</summary>
internal partial class WebUiSettingsView : UserControl
{
    public WebUiSettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as WebUiSettingsViewModel)?.Attach();
        Unloaded += (_, _) => (DataContext as WebUiSettingsViewModel)?.Detach();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        (e.OldValue as WebUiSettingsViewModel)?.Detach();
        if (IsLoaded)
        {
            (e.NewValue as WebUiSettingsViewModel)?.Attach();
        }
    }
}
