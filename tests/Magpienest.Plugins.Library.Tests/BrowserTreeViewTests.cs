using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using Magpienest.Plugins.Library.Browser;
using Magpienest.Sdk.Library;

namespace Magpienest.Plugins.Library.Tests;

/// <summary>The folder tree bound to a real WPF TreeView, as the browser view binds it.</summary>
public sealed class BrowserTreeViewTests
{
    private const string Kind = "test";

    private const string TreeXaml = """
        <TreeView xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  ItemsSource="{Binding FolderRoots}">
          <TreeView.ItemContainerStyle>
            <Style TargetType="TreeViewItem">
              <Setter Property="IsExpanded" Value="{Binding IsExpanded, Mode=TwoWay}" />
              <Setter Property="IsSelected" Value="{Binding IsSelected, Mode=TwoWay}" />
            </Style>
          </TreeView.ItemContainerStyle>
          <TreeView.ItemTemplate>
            <HierarchicalDataTemplate ItemsSource="{Binding Children}">
              <TextBlock Text="{Binding Name}" />
            </HierarchicalDataTemplate>
          </TreeView.ItemTemplate>
        </TreeView>
        """;

    [Fact]
    public void ALibraryChangeKeepsTheSelectedFolderInTheQuery()
    {
        RunOnSta(async () =>
        {
            using var lib = new TestLibrary();
            var folder = lib.NewFolder("pics");
            var scanner = new FakeScanner();
            scanner.Entries.Add(FakeScanner.Entry(Path.Combine(folder, "a.png")));
            scanner.Entries.Add(FakeScanner.Entry(Path.Combine(folder, "sub", "b.png")));
            scanner.Entries.Add(FakeScanner.Entry(Path.Combine(folder, "other", "c.png")));
            lib.RegisterScanner(Kind, scanner);
            var source = await lib.Service.AddSourceAsync(Kind, folder);
            await lib.Service.ScanAsync(source.Id, ct: CancellationToken.None);

            using var vm = new BrowserViewModel(BrowserHarness.Services(lib.Service), lib.Context, BrowserHarness.Options());
            await vm.Initialization;

            var tree = (TreeView)XamlReader.Parse(TreeXaml);
            tree.DataContext = vm;
            var window = new Window
            {
                Content = tree,
                Width = 300,
                Height = 400,
                Left = -20000,
                Top = -20000,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
            };
            window.Show();
            try
            {
                vm.FolderRoots[0].IsExpanded = true;
                vm.FolderRoots[0].Children.Single().IsExpanded = true;
                await Idle();
                var sub = vm.FolderRoots[0].Children.Single().Children.Single(n => n.Name == "sub");
                sub.IsSelected = true;
                await vm.LastRefresh;
                await Idle();
                var expected = Path.Combine(source.Path, "sub");
                Assert.Equal(expected, vm.BuildQuery().Directory);

                // Opening an item records it, which reloads the tree (the viewer case).
                var item = vm.Items.Single();
                await lib.Service.UpdateAsync(item.Item.Id, new ItemPatch { MarkOpened = true });
                await vm.ReloadAllAsync();
                await Idle();
                await vm.LastRefresh;
                await Idle();

                Assert.Equal(expected, vm.BuildQuery().Directory);
                Assert.Equal(expected, vm.SelectedFolder?.Path);
                Assert.Equal(vm.SelectedFolder, tree.SelectedItem);
                Assert.Single(vm.Items);

                // A scan that adds a folder rebuilds the tree; the folder stays selected.
                scanner.Entries.Add(FakeScanner.Entry(Path.Combine(folder, "new", "d.png")));
                await lib.Service.ScanAsync(source.Id, ct: CancellationToken.None);
                await vm.ReloadAllAsync();
                await Idle();
                await vm.LastRefresh;
                await Idle();

                Assert.Equal(expected, vm.BuildQuery().Directory);
                Assert.Equal(vm.SelectedFolder, tree.SelectedItem);
                Assert.Single(vm.FolderRoots[0].SelfAndDescendants(), n => n.IsSelected);
                Assert.Single(vm.Items);

                // The root can still be chosen afterwards.
                vm.FolderRoots[0].IsSelected = true;
                await vm.LastRefresh;
                Assert.Null(vm.BuildQuery().Directory);
                Assert.Equal(4, vm.Items.Count);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static async Task Idle()
    {
        for (var i = 0; i < 3; i++)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    }

    private static void RunOnSta(Func<Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await body();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }
}
