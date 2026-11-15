using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.Shell;

/// <summary>Modal dialog: content plus primary/cancel buttons. Enter = primary, Esc = cancel.</summary>
internal sealed class DialogWindow : Window
{
    private readonly DispatcherTimer? _poll;

    public DialogWindow(DialogDescriptor dialog)
    {
        Title = dialog.Title;
        Width = dialog.Width;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        var primary = new Button
        {
            Content = dialog.PrimaryText ?? Loc.Get("Shell.Dialog.Ok"),
            IsDefault = true,
            MinWidth = 96,
            Margin = new Thickness(8, 0, 0, 0),
        };
        primary.SetResourceReference(StyleProperty, "AccentButtonStyle");
        primary.Click += (_, _) =>
        {
            if (dialog.CanAccept?.Invoke() ?? true)
            {
                DialogResult = true;
            }
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(primary);
        if (dialog.CancelText != string.Empty)
        {
            var cancel = new Button
            {
                Content = dialog.CancelText ?? Loc.Get("Shell.Dialog.Cancel"),
                IsCancel = true,
                MinWidth = 96,
                Margin = new Thickness(8, 0, 0, 0),
            };
            buttons.Children.Add(cancel);
        }
        else
        {
            // No cancel button: Esc still closes (as "not accepted").
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    DialogResult = false;
                }
            };
        }

        var footer = new Border { Padding = new Thickness(24, 16, 24, 16), Child = buttons };
        footer.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new Border { Padding = new Thickness(24, 20, 24, 20), Child = dialog.Content });
        Content = root;

        if (dialog.CanAccept is { } canAccept)
        {
            primary.IsEnabled = canAccept();
            _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _poll.Tick += (_, _) => primary.IsEnabled = canAccept();
            _poll.Start();
            Closed += (_, _) => _poll.Stop();
        }

        Loaded += (_, _) => MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    public static FrameworkElement MessageContent(string message) =>
        new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
}
