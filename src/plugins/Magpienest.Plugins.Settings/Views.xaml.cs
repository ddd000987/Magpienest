using System.Windows.Controls;

namespace Magpienest.Plugins.Settings;

/// <summary>The settings page.</summary>
internal partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();
}

/// <summary>General section: language and theme.</summary>
internal partial class GeneralSectionView : UserControl
{
    public GeneralSectionView() => InitializeComponent();
}

/// <summary>Plugins section: one card per plugin.</summary>
internal partial class PluginsSectionView : UserControl
{
    public PluginsSectionView() => InitializeComponent();
}
