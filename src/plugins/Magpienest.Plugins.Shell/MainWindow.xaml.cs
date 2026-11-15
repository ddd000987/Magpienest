using System.Windows;
using System.Windows.Controls;

namespace Magpienest.Plugins.Shell;

/// <summary>The main window. Behaviour lives in <see cref="ShellService"/>.</summary>
internal partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>Full-window layer for overlays.</summary>
    public Grid Overlays => OverlayHost;

    /// <summary>The root element (rendered by DevTools screenshots).</summary>
    public FrameworkElement RootElement => Root;
}
