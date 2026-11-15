using System.Windows;
using Magpienest.Plugins.ImageViewer;

namespace Magpienest.Plugins.ImageViewer.Tests;

public sealed class ViewTransformModelTests
{
    private const double Tolerance = 1e-6;

    private static ViewTransformModel Model(double contentW, double contentH, double viewW = 1000, double viewH = 800)
    {
        var model = new ViewTransformModel();
        model.SetViewport(new Size(viewW, viewH));
        model.SetContent(new Size(contentW, contentH));
        return model;
    }

    private static void Near(double expected, double actual) => Assert.InRange(actual, expected - Tolerance, expected + Tolerance);

    [Fact]
    public void FitScalesALandscapeImageDownByWidthAndCentresIt()
    {
        var model = Model(4000, 2000);

        Near(0.25, model.Scale); // min(1000/4000, 800/2000)
        Assert.Equal(ZoomMode.Fit, model.Mode);
        Near(0, model.Offset.X);
        Near((800 - 500) / 2.0, model.Offset.Y);
    }

    [Fact]
    public void FitScalesAPortraitImageDownByHeightAndCentresIt()
    {
        var model = Model(2000, 4000);

        Near(0.2, model.Scale); // min(1000/2000, 800/4000)
        Near((1000 - 400) / 2.0, model.Offset.X);
        Near(0, model.Offset.Y);
    }

    [Fact]
    public void FitNeverEnlargesUnlessStretchingIsOn()
    {
        var model = Model(200, 100);
        Near(1, model.Scale);
        Near(400, model.Offset.X);
        Near(350, model.Offset.Y);

        model.StretchSmall = true;
        model.Fit();
        Near(5, model.Scale);
    }

    [Fact]
    public void ZoomKeepsTheContentPointUnderTheAnchor()
    {
        var model = Model(4000, 3000);
        model.ActualSize(new Point(500, 400));
        var anchor = new Point(300, 200);
        var before = model.ToContent(anchor);

        model.ZoomBy(ViewTransformModel.Step, anchor);

        Near(1.2, model.Scale);
        var after = model.ToViewport(before);
        Near(anchor.X, after.X);
        Near(anchor.Y, after.Y);
        Assert.Equal(ZoomMode.Free, model.Mode);
    }

    [Fact]
    public void ZoomOutStillKeepsTheAnchorWhileContentIsLarger()
    {
        var model = Model(8000, 6000);
        model.ZoomTo(2, new Point(500, 400));
        var anchor = new Point(700, 300);
        var before = model.ToContent(anchor);

        model.ZoomBy(1 / ViewTransformModel.Step, anchor);

        var after = model.ToViewport(before);
        Near(anchor.X, after.X);
        Near(anchor.Y, after.Y);
    }

    [Fact]
    public void ZoomIsClampedBetweenTenAndSixteenHundredPercent()
    {
        var model = Model(1000, 800);
        model.ZoomTo(100);
        Near(ViewTransformModel.MaxScale, model.Scale);
        model.ZoomTo(0.0001);
        Near(ViewTransformModel.MinScale, model.Scale);
    }

    [Fact]
    public void PanIsClampedSoTheContentNeverLeavesTheViewport()
    {
        var model = Model(4000, 3000);
        model.ActualSize();

        model.Pan(new Vector(10_000, 10_000));
        Near(0, model.Offset.X);
        Near(0, model.Offset.Y);

        model.Pan(new Vector(-100_000, -100_000));
        Near(1000 - 4000, model.Offset.X);
        Near(800 - 3000, model.Offset.Y);
    }

    [Fact]
    public void AnAxisSmallerThanTheViewportStaysCentredWhilePanning()
    {
        var model = Model(3000, 400); // wide strip: taller viewport than content at 100 %
        model.ActualSize();

        model.Pan(new Vector(-500, 300));

        Near(-500 + ((1000 - 3000) / 2.0), model.Offset.X);
        Near((800 - 400) / 2.0, model.Offset.Y);
        Assert.True(model.CanPan);
    }

    [Fact]
    public void ResizeRefitsInFitModeAndReclampsOtherwise()
    {
        var model = Model(4000, 2000);
        model.SetViewport(new Size(2000, 1600));
        Near(0.5, model.Scale);

        model.ActualSize();
        model.Pan(new Vector(-100_000, 0));
        Near(2000 - 4000, model.Offset.X);

        model.SetViewport(new Size(3000, 1600));
        Near(1, model.Scale);
        Assert.InRange(model.Offset.X, 3000 - 4000 - Tolerance, 0 + Tolerance);

        model.SetViewport(new Size(5000, 2400));
        Near((5000 - 4000) / 2.0, model.Offset.X); // now smaller than the viewport → centred
        Near((2400 - 2000) / 2.0, model.Offset.Y);
    }

    [Fact]
    public void DoubleClickTogglesFitAndActualSize()
    {
        var model = Model(4000, 3000);

        model.ToggleFitActual(new Point(100, 100));
        Assert.Equal(ZoomMode.Actual, model.Mode);
        Near(1, model.Scale);

        model.ToggleFitActual(new Point(100, 100));
        Assert.Equal(ZoomMode.Fit, model.Mode);
        Near(model.FitScale, model.Scale);
    }

    [Fact]
    public void RotationSwapsTheContentSizeAndRefits()
    {
        var model = Model(4000, 2000);

        model.Rotate();

        Assert.Equal(90, model.Rotation);
        Assert.Equal(new Size(2000, 4000), model.Content);
        Near(0.2, model.Scale);
        model.Rotate();
        model.Rotate();
        model.Rotate();
        Assert.Equal(0, model.Rotation);
        Assert.Equal(new Size(4000, 2000), model.Content);
    }

    [Fact]
    public void NewContentResetsToFitAndRotationZero()
    {
        var model = Model(4000, 2000);
        model.Rotate();
        model.ZoomTo(3);

        model.SetContent(new Size(1000, 1000));

        Assert.Equal(ZoomMode.Fit, model.Mode);
        Assert.Equal(0, model.Rotation);
        Near(0.8, model.Scale);
    }
}

public sealed class PageCacheTests
{
    [Fact]
    public void EvictsLeastRecentlyUsedOverBudget()
    {
        var cache = new PageCache<string>(300);
        cache.Add(0, "a", 100);
        cache.Add(1, "b", 100);
        cache.Add(2, "c", 100);
        Assert.True(cache.TryGet(0, out _)); // 0 becomes most recent

        cache.Add(3, "d", 100);

        Assert.True(cache.Contains(0));
        Assert.False(cache.Contains(1));
        Assert.True(cache.Contains(2));
        Assert.True(cache.Contains(3));
        Assert.Equal(300, cache.TotalBytes);
    }

    [Fact]
    public void PinnedPagesAreNeverEvicted()
    {
        var cache = new PageCache<string>(200);
        cache.Add(0, "a", 150);
        cache.Pin([0]);

        cache.Add(1, "b", 150); // over budget: the only unpinned page goes

        Assert.True(cache.Contains(0));
        Assert.False(cache.Contains(1));
        Assert.Equal(150, cache.TotalBytes);

        cache.Add(2, "c", 40);
        cache.Pin([2]);
        Assert.True(cache.Contains(0));
        Assert.Equal(190, cache.TotalBytes);

        cache.Add(3, "d", 100); // 0 is no longer pinned and is the least recently used
        Assert.False(cache.Contains(0));
        Assert.True(cache.Contains(2));
        Assert.True(cache.Contains(3));
        Assert.Equal(140, cache.TotalBytes);
    }

    [Fact]
    public void ReplacingAPageKeepsTheByteCountRight()
    {
        var cache = new PageCache<string>(1000);
        cache.Add(0, "a", 100);
        cache.Add(0, "b", 250);

        Assert.Equal(1, cache.Count);
        Assert.Equal(250, cache.TotalBytes);
        Assert.True(cache.TryGet(0, out var value));
        Assert.Equal("b", value);

        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.TotalBytes);
    }
}
