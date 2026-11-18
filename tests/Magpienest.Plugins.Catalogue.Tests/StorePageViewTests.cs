using Magpienest.Plugins.Catalogue;

namespace Magpienest.Plugins.Catalogue.Tests;

/// <summary>The store page's XAML loads (a parse error would only show when the section is opened).</summary>
public sealed class StorePageViewTests
{
    [Fact]
    public void ThePageLoads()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = new StorePageView();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }
}
