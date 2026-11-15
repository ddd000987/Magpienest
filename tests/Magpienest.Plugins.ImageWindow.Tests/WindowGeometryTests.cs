using System.Windows;
using Magpienest.Plugins.ImageWindow;
using G = Magpienest.Plugins.ImageWindow.WindowGeometry;

namespace Magpienest.Plugins.ImageWindow.Tests;

public sealed class WindowGeometryTests
{
    private static readonly Rect Work = new(0, 0, 1920, 1040);

    [Fact]
    public void SmallImageOpensAtActualSizeCentredOnTheCursor()
    {
        var r = G.InitialBounds(new Size(400, 300), Work, new Point(1000, 500), 0);
        Assert.Equal(new Rect(800, 350, 400, 300), r);
    }

    [Fact]
    public void LargeImageIsCappedAt80PercentAndKeepsItsAspect()
    {
        var r = G.InitialBounds(new Size(8000, 4000), Work, new Point(960, 520), 0);
        Assert.Equal(1920 * 0.8, r.Width, 3);
        Assert.Equal(r.Width / 2, r.Height, 3);
        Assert.True(r.Left >= Work.Left && r.Right <= Work.Right && r.Top >= Work.Top && r.Bottom <= Work.Bottom);
    }

    [Fact]
    public void TallImageIsLimitedByHeight()
    {
        var r = G.InitialBounds(new Size(1000, 5000), Work, new Point(960, 520), 0);
        Assert.Equal(1040 * 0.8, r.Height, 3);
        Assert.Equal(r.Height / 5, r.Width, 3);
    }

    [Fact]
    public void WindowNearTheCornerIsMovedOnScreen()
    {
        var r = G.InitialBounds(new Size(400, 300), Work, new Point(1910, 1030), 0);
        Assert.Equal(1920 - 400, r.Left);
        Assert.Equal(1040 - 300, r.Top);

        var other = G.InitialBounds(new Size(400, 300), new Rect(-1920, 0, 1920, 1080), new Point(-1915, 5), 0);
        Assert.Equal(-1920, other.Left);
        Assert.Equal(0, other.Top);
    }

    [Fact]
    public void BatchesAreCascaded()
    {
        var first = G.InitialBounds(new Size(400, 300), Work, new Point(1000, 500), 0);
        var third = G.InitialBounds(new Size(400, 300), Work, new Point(1000, 500), 2);
        Assert.Equal(first.Left + 48, third.Left);
        Assert.Equal(first.Top + 48, third.Top);
    }

    [Fact]
    public void TinyImageGetsTheMinimumShortSide()
    {
        var size = G.FitSize(new Size(16, 32), Work);
        Assert.Equal(96, size.Width, 3);
        Assert.Equal(192, size.Height, 3);
    }

    [Theory]
    [InlineData(G.Left)]
    [InlineData(G.Right)]
    [InlineData(G.Top)]
    [InlineData(G.Bottom)]
    [InlineData(G.TopLeft)]
    [InlineData(G.TopRight)]
    [InlineData(G.BottomLeft)]
    [InlineData(G.BottomRight)]
    public void SizingKeepsTheAspectAndTheOppositeEdges(int edge)
    {
        var image = new Size(400, 200);
        var start = new Rect(100, 100, 400, 200);
        // The user drags the given edge outwards by 100 px in its direction(s).
        var dragged = edge switch
        {
            G.Left => new Rect(0, 100, 500, 200),
            G.Right => new Rect(100, 100, 500, 200),
            G.Top => new Rect(100, 0, 400, 300),
            G.Bottom => new Rect(100, 100, 400, 300),
            G.TopLeft => new Rect(0, 0, 500, 300),
            G.TopRight => new Rect(100, 0, 500, 300),
            G.BottomLeft => new Rect(0, 100, 500, 300),
            _ => new Rect(100, 100, 500, 300),
        };

        var r = G.ConstrainSizing(dragged, edge, image);
        Assert.Equal(2.0, r.Width / r.Height, 6);

        var movesLeft = edge is G.Left or G.TopLeft or G.BottomLeft;
        var movesTop = edge is G.Top or G.TopLeft or G.TopRight;
        if (movesLeft)
            Assert.Equal(start.Right, r.Right, 6);
        else
            Assert.Equal(start.Left, r.Left, 6);
        if (movesTop)
            Assert.Equal(start.Bottom, r.Bottom, 6);
        else
            Assert.Equal(start.Top, r.Top, 6);

        if (edge is G.Top or G.Bottom)
            Assert.Equal(300, r.Height, 6);
        else
            Assert.Equal(500, r.Width, 6);
    }

    [Fact]
    public void SizingStopsAtTheMinimum()
    {
        var r = G.ConstrainSizing(new Rect(100, 100, 20, 10), G.BottomRight, new Size(400, 200));
        Assert.Equal(96, r.Height, 6);
        Assert.Equal(192, r.Width, 6);
    }

    [Fact]
    public void WheelScalingKeepsThePointUnderTheCursorAndRespectsLimits()
    {
        var image = new Size(400, 200);
        var bounds = new Rect(100, 100, 400, 200);
        var cursor = new Point(200, 150);   // a quarter in from the left, a quarter down
        var r = G.ScaleAround(bounds, cursor, 2, image);
        Assert.Equal(new Rect(0, 50, 800, 400), r);

        var huge = G.ScaleAround(bounds, cursor, 1000, image);
        Assert.Equal(400 * G.MaxScale, huge.Width, 6);

        var tiny = G.ScaleAround(bounds, cursor, 0.001, image);
        Assert.Equal(G.MinShortSide, tiny.Height, 6);
    }
}
