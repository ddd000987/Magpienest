using System.Windows;
using System.Windows.Controls;

namespace HelloPlugin;

/// <summary>The sample page: a greeting card and a button that shows a toast.</summary>
internal sealed partial class HelloView : UserControl
{
    private readonly Action _sayHello;

    public HelloView(Action sayHello)
    {
        _sayHello = sayHello;
        InitializeComponent();
    }

    private void OnSayHello(object sender, RoutedEventArgs e) => _sayHello();
}
