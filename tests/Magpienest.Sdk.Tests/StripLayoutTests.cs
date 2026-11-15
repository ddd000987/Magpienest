using Magpienest.Sdk.Viewing;

namespace Magpienest.Sdk.Tests;

public sealed class StripLayoutTests
{
    private static StripLayout Known(StripAxis axis, params (int W, int H)[] sizes)
    {
        var layout = new StripLayout(sizes.Length, axis);
        for (var i = 0; i < sizes.Length; i++)
        {
            layout.SetSize(i, sizes[i].W, sizes[i].H);
        }

        return layout;
    }

    [Fact]
    public void HorizontalLengthsAreWidthsAtUnitHeight()
    {
        var layout = Known(StripAxis.Horizontal, (100, 200), (300, 100), (50, 100));
        Assert.Equal(0.5, layout.LengthOf(0), 9);
        Assert.Equal(3.0, layout.LengthOf(1), 9);
        Assert.Equal(0.0, layout.OffsetOf(0), 9);
        Assert.Equal(0.5, layout.OffsetOf(1), 9);
        Assert.Equal(3.5, layout.OffsetOf(2), 9);
        Assert.Equal(4.0, layout.Extent, 9);
        Assert.Equal(4.0, layout.OffsetOf(3), 9);
    }

    [Fact]
    public void VerticalLengthsAreHeightsAtUnitWidth()
    {
        var layout = Known(StripAxis.Vertical, (100, 200), (300, 100));
        Assert.Equal(2.0, layout.LengthOf(0), 9);
        Assert.Equal(1.0 / 3, layout.LengthOf(1), 9);
        Assert.Equal(2.0 + 1.0 / 3, layout.Extent, 9);
    }

    [Fact]
    public void UnknownPagesUsePortraitThenTheMedian()
    {
        var h = new StripLayout(4, StripAxis.Horizontal);
        Assert.Equal(0.7, h.LengthOf(2), 9);
        Assert.Equal(2.8, h.Extent, 9);
        Assert.Equal(1 / 0.7, new StripLayout(1, StripAxis.Vertical).LengthOf(0), 9);

        h.SetSize(0, 100, 100);   // 1
        h.SetSize(1, 300, 100);   // 3
        Assert.False(h.HasSize(2));
        Assert.Equal(2.0, h.LengthOf(2), 9);   // median of 1 and 3
        h.SetSize(3, 150, 100);   // 1.5
        Assert.Equal(1.5, h.LengthOf(2), 9);   // median of 1, 1.5, 3
        Assert.Equal(1 + 3 + 1.5 + 1.5, h.Extent, 9);
    }

    [Fact]
    public void InvalidSizesAreIgnored()
    {
        var layout = new StripLayout(1, StripAxis.Horizontal);
        layout.SetSize(0, 0, 100);
        Assert.False(layout.HasSize(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.SetSize(1, 1, 1));
    }

    [Fact]
    public void IndexAtFindsThePageAndClamps()
    {
        var layout = Known(StripAxis.Horizontal, (100, 100), (200, 100), (100, 100)); // 0..1, 1..3, 3..4
        Assert.Equal(0, layout.IndexAt(-5));
        Assert.Equal(0, layout.IndexAt(0));
        Assert.Equal(0, layout.IndexAt(0.999));
        Assert.Equal(1, layout.IndexAt(1));
        Assert.Equal(1, layout.IndexAt(2.5));
        Assert.Equal(2, layout.IndexAt(3));
        Assert.Equal(2, layout.IndexAt(4));
        Assert.Equal(2, layout.IndexAt(100));
        Assert.Equal(-1, new StripLayout(0, StripAxis.Horizontal).IndexAt(0));
    }

    [Fact]
    public void VisibleIncludesMarginsAndExcludesPagesThatOnlyTouch()
    {
        var layout = Known(StripAxis.Horizontal, (100, 100), (100, 100), (100, 100), (100, 100), (100, 100));
        Assert.Equal(new PageFrame(1, 2), layout.Visible(1, 2, 0));         // [1, 3): page 3 starts at 3 → not visible
        Assert.Equal(new PageFrame(1, 3), layout.Visible(1.5, 2, 0));
        Assert.Equal(new PageFrame(0, 3), layout.Visible(1, 2, 0.5));
        Assert.Equal(new PageFrame(0, 4), layout.Visible(-10, 100, 0));
        Assert.Equal(new PageFrame(0, -1), new StripLayout(0, StripAxis.Horizontal).Visible(0, 1, 1));
    }

    [Fact]
    public void AnchorsRoundTrip()
    {
        var layout = Known(StripAxis.Horizontal, (100, 100), (200, 100), (100, 100));
        foreach (var offset in new[] { 0.0, 0.5, 1.0, 2.0, 3.25 })
        {
            Assert.Equal(offset, layout.OffsetOf(layout.AnchorAt(offset)), 9);
        }

        Assert.Equal(new StripAnchor(1, 0.5), layout.AnchorAt(2));
    }

    [Fact]
    public void AnEarlierPageChangingSizeKeepsTheAnchoredPage()
    {
        var layout = new StripLayout(10, StripAxis.Horizontal);
        var anchor = layout.AnchorAt(layout.OffsetOf(8) + 0.35);   // placeholder 0.7 → page 8, halfway
        Assert.Equal(8, anchor.Index);
        Assert.Equal(0.5, anchor.Fraction, 9);

        layout.SetSize(2, 400, 100);         // an earlier page turns out wide
        var moved = layout.OffsetOf(anchor);
        Assert.Equal(layout.OffsetOf(8) + 0.5 * layout.LengthOf(8), moved, 9);
        Assert.Equal(8, layout.AnchorAt(moved).Index);
        Assert.Equal(0.5, layout.AnchorAt(moved).Fraction, 9);
    }

    [Fact]
    public void ManyPagesAreFast()
    {
        var layout = new StripLayout(10_000, StripAxis.Horizontal);
        for (var i = 0; i < 10_000; i += 3)
        {
            layout.SetSize(i, 100 + i % 50, 140);
        }

        var extent = layout.Extent;
        Assert.True(extent > 0);
        for (var i = 0; i < 10_000; i++)
        {
            Assert.Equal(i, layout.IndexAt(layout.OffsetOf(i) + layout.LengthOf(i) / 2));
        }
    }
}
