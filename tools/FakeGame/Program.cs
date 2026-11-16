using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace FakeGame;

// A test tool, not part of the application: its window text is a literal on purpose and is not localized.
internal static class Program
{
    /// <summary>"--spawn-child" starts a copy of this program (without the option) and exits at once, the way a launcher does.</summary>
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--spawn-child", StringComparer.OrdinalIgnoreCase))
        {
            if (Environment.ProcessPath is { } self)
            {
                Process.Start(new ProcessStartInfo { FileName = self, UseShellExecute = false })?.Dispose();
            }

            return;
        }

        var window = new Window
        {
            Title = "Fake Game",
            Width = 360,
            Height = 200,
            Content = new TextBlock
            {
                Text = "Fake Game",
                FontSize = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        new Application().Run(window);
    }
}
