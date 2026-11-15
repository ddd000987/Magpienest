using Magpienest.Sdk.Viewing;

namespace Magpienest.Sdk.Tests;

public sealed class FrameLayoutTests
{
    private static Func<int, bool> Wide(params int[] wide) => i => wide.Contains(i);

    /// <summary>Frames met when paging forward from page 0 to the end.</summary>
    private static List<PageFrame> WalkForward(int count, SpreadMode spread, bool coverAlone, Func<int, bool> isWide)
    {
        var frames = new List<PageFrame> { FrameLayout.Forward(0, count, spread, coverAlone, isWide) };
        while (frames[^1].Last + 1 < count)
        {
            frames.Add(FrameLayout.Forward(frames[^1].Last + 1, count, spread, coverAlone, isWide));
        }

        return frames;
    }

    /// <summary>Frames met when paging back from <paramref name="start"/> to page 0.</summary>
    private static List<PageFrame> WalkBackward(PageFrame start, int count, SpreadMode spread, bool coverAlone, Func<int, bool> isWide)
    {
        var frames = new List<PageFrame> { start };
        while (frames[^1].First > 0)
        {
            frames.Add(FrameLayout.Backward(frames[^1].First - 1, count, spread, coverAlone, isWide));
        }

        return frames;
    }

    private static string Show(IEnumerable<PageFrame> frames) =>
        string.Join(" ", frames.Select(f => f.First == f.Last ? $"[{f.First}]" : $"[{f.First},{f.Last}]"));

    [Fact]
    public void AllPortraitWithoutCoverAlone()
    {
        Assert.Equal("[0,1] [2,3] [4,5]", Show(WalkForward(6, SpreadMode.Double, false, Wide())));
        Assert.Equal("[0,1] [2,3] [4]", Show(WalkForward(5, SpreadMode.Double, false, Wide())));
    }

    [Fact]
    public void AllPortraitWithCoverAlone()
    {
        Assert.Equal("[0] [1,2] [3,4] [5]", Show(WalkForward(6, SpreadMode.Double, true, Wide())));
        Assert.Equal("[0] [1,2] [3,4]", Show(WalkForward(5, SpreadMode.Double, true, Wide())));
    }

    [Fact]
    public void WidePageInTheMiddleStandsAloneAndLaterPagesStillPair()
    {
        Assert.Equal("[0,1] [2] [3] [4,5] [6]", Show(WalkForward(7, SpreadMode.Double, false, Wide(3))));
        Assert.Equal("[0] [1,2] [3] [4,5] [6]", Show(WalkForward(7, SpreadMode.Double, true, Wide(3))));
    }

    [Fact]
    public void WideFirstAndLastPages()
    {
        Assert.Equal("[0] [1,2] [3,4]", Show(WalkForward(5, SpreadMode.Double, false, Wide(0))));
        Assert.Equal("[0,1] [2,3] [4]", Show(WalkForward(5, SpreadMode.Double, false, Wide(4))));
        Assert.Equal("[0,1] [2] [3]", Show(WalkForward(4, SpreadMode.Double, false, Wide(3))));
    }

    [Fact]
    public void SingleModeIsOnePagePerFrame()
    {
        Assert.Equal("[0] [1] [2]", Show(WalkForward(3, SpreadMode.Single, true, Wide())));
        Assert.Equal(new PageFrame(1, 1), FrameLayout.Backward(1, 3, SpreadMode.Single, false, Wide()));
    }

    [Fact]
    public void BackwardFrames()
    {
        Assert.Equal(new PageFrame(4, 5), FrameLayout.Backward(5, 6, SpreadMode.Double, false, Wide()));
        Assert.Equal(new PageFrame(1, 1), FrameLayout.Backward(1, 6, SpreadMode.Double, true, Wide())); // cover stays alone
        Assert.Equal(new PageFrame(0, 0), FrameLayout.Backward(0, 6, SpreadMode.Double, false, Wide()));
        Assert.Equal(new PageFrame(4, 4), FrameLayout.Backward(4, 6, SpreadMode.Double, false, Wide(3)));
        Assert.Equal(new PageFrame(3, 3), FrameLayout.Backward(3, 6, SpreadMode.Double, false, Wide(3)));
    }

    [Fact]
    public void OutOfRangeThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameLayout.Forward(3, 3, SpreadMode.Double, false, Wide()));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameLayout.Backward(-1, 3, SpreadMode.Double, false, Wide()));
    }

    public static TheoryData<int, bool, int> Patterns()
    {
        var data = new TheoryData<int, bool, int>();
        foreach (var count in new[] { 1, 2, 3, 4, 7, 10, 25 })
        {
            foreach (var cover in new[] { false, true })
            {
                for (var seed = 0; seed < 8; seed++)
                {
                    data.Add(count, cover, seed);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Patterns))]
    public void WalkingForwardAndBackNeverSkipsOrRepeatsAPage(int count, bool coverAlone, int seed)
    {
        var random = new Random(seed);
        var wide = Enumerable.Range(0, count).Where(_ => random.NextDouble() < 0.2).ToArray();
        foreach (var spread in new[] { SpreadMode.Single, SpreadMode.Double })
        {
            var forward = WalkForward(count, spread, coverAlone, Wide(wide));
            AssertCovers(forward, count, ascending: true);

            var backward = WalkBackward(forward[^1], count, spread, coverAlone, Wide(wide));
            AssertCovers(backward, count, ascending: false);

            for (var p = 0; p < count; p++)
            {
                var frame = FrameLayout.Forward(p, count, spread, coverAlone, Wide(wide)); // GoTo(p)
                Assert.Equal(p, frame.First);
                Assert.InRange(frame.Last, p, Math.Min(p + 1, count - 1));
                if (frame.Last > frame.First)
                {
                    Assert.False(wide.Contains(frame.First) || wide.Contains(frame.Last), "a wide page was paired");
                }

                var back = FrameLayout.Backward(p, count, spread, coverAlone, Wide(wide));
                Assert.Equal(p, back.Last);
                Assert.InRange(back.First, Math.Max(0, p - 1), p);
            }
        }
    }

    /// <summary>Consecutive frames touch without a gap or overlap and together cover every page.</summary>
    private static void AssertCovers(List<PageFrame> frames, int count, bool ascending)
    {
        var ordered = ascending ? frames : Enumerable.Reverse(frames).ToList();
        Assert.Equal(0, ordered[0].First);
        Assert.Equal(count - 1, ordered[^1].Last);
        for (var i = 1; i < ordered.Count; i++)
        {
            Assert.Equal(ordered[i - 1].Last + 1, ordered[i].First);
        }

        Assert.All(ordered, f => Assert.InRange(f.Last - f.First, 0, 1));
    }
}
