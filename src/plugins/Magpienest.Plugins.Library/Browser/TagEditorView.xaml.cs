using System.Windows.Controls;
using System.Windows.Input;

namespace Magpienest.Plugins.Library.Browser;

/// <summary>Tag chips with an add box (DataContext: <see cref="TagEditorViewModel"/>).</summary>
internal sealed partial class TagEditorView : UserControl
{
    public TagEditorView() => InitializeComponent();

    private void AddBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is TagEditorViewModel vm)
        {
            vm.NewTag = AddBox.Text;
            vm.AddCommand.Execute(null);
            AddBox.Text = "";
            e.Handled = true;
        }
    }
}
