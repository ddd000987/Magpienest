namespace Magpienest.Sdk.Viewing;

/// <summary>Direction of a strip.</summary>
public enum StripAxis
{
    /// <summary>Pages side by side; lengths are widths at a common height.</summary>
    Horizontal,
    /// <summary>Pages stacked; lengths are heights at a common width.</summary>
    Vertical,
}

/// <summary>A position in a strip: a page and how far into it (0 ≤ fraction &lt; 1).</summary>
/// <param name="Index">Page index.</param>
/// <param name="Fraction">Share of the page's length before the position.</param>
public readonly record struct StripAnchor(int Index, double Fraction);

/// <summary>
/// Pages placed edge to edge along one axis (lives in the SDK so every front end lays strips out the same
/// way). Lengths and offsets are in units of the cross size and measured from the reading start (page 0 at 0).
/// Not thread-safe; one instance per open strip. Specified in phase-2b T2.7.
/// </summary>
public sealed class StripLayout
{
    /// <summary>Width / height of a page whose size is not known while no size is known at all.</summary>
    public const double DefaultAspect = 0.7;

    private readonly double[] _length;   // NaN = unknown
    private readonly double[] _offset;   // prefix sums, valid when !_dirty; Count + 1 entries
    private int _known;
    private bool _dirty = true;
    private double _placeholder;

    /// <summary>Creates a layout where every page is still unknown.</summary>
    /// <param name="count">Page count (≥ 0).</param>
    /// <param name="axis">Strip direction.</param>
    public StripLayout(int count, StripAxis axis)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Count = count;
        Axis = axis;
        _length = new double[count];
        Array.Fill(_length, double.NaN);
        _offset = new double[count + 1];
    }

    /// <summary>Page count.</summary>
    public int Count { get; }

    /// <summary>Strip direction.</summary>
    public StripAxis Axis { get; }

    /// <summary>Total length of the strip.</summary>
    public double Extent
    {
        get
        {
            Rebuild();
            return _offset[Count];
        }
    }

    /// <summary>Records a page's pixel size; ignored when either side is not positive.</summary>
    /// <param name="index">Page index.</param>
    /// <param name="pixelWidth">Oriented width.</param>
    /// <param name="pixelHeight">Oriented height.</param>
    public void SetSize(int index, int pixelWidth, int pixelHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            return;
        }

        var length = Axis == StripAxis.Horizontal ? (double)pixelWidth / pixelHeight : (double)pixelHeight / pixelWidth;
        if (double.IsNaN(_length[index]))
        {
            _known++;
        }
        else if (_length[index] == length)
        {
            return;
        }

        _length[index] = length;
        _dirty = true;
    }

    /// <summary>Whether the page's size was recorded.</summary>
    /// <param name="index">Page index.</param>
    public bool HasSize(int index) => !double.IsNaN(_length[index]);

    /// <summary>Length of a page (its own, or the placeholder).</summary>
    /// <param name="index">Page index.</param>
    public double LengthOf(int index)
    {
        Rebuild();
        return double.IsNaN(_length[index]) ? _placeholder : _length[index];
    }

    /// <summary>Offset of a page's reading-start edge.</summary>
    /// <param name="index">Page index (Count gives the extent).</param>
    public double OffsetOf(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, Count);
        Rebuild();
        return _offset[index];
    }

    /// <summary>The page at <paramref name="offset"/>, clamped to 0..Count-1 (-1 when there are no pages).</summary>
    /// <param name="offset">Offset along the strip.</param>
    public int IndexAt(double offset)
    {
        if (Count == 0)
        {
            return -1;
        }

        Rebuild();
        if (offset <= 0)
        {
            return 0;
        }

        // Last index whose start is ≤ offset.
        int lo = 0, hi = Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_offset[mid] <= offset)
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo;
    }

    /// <summary>Pages overlapping [offset - margin, offset + length + margin], as an inclusive range.</summary>
    /// <param name="offset">Start of the window.</param>
    /// <param name="length">Window length.</param>
    /// <param name="margin">Extra length on both sides.</param>
    public PageFrame Visible(double offset, double length, double margin)
    {
        if (Count == 0)
        {
            return new PageFrame(0, -1);
        }

        var first = IndexAt(offset - margin);
        var end = offset + length + margin;
        var last = IndexAt(end);
        if (last > first && OffsetOf(last) >= end)
        {
            last--; // touches the window's end only at its edge
        }

        return new PageFrame(first, last);
    }

    /// <summary>The anchor at <paramref name="offset"/>.</summary>
    /// <param name="offset">Offset along the strip.</param>
    public StripAnchor AnchorAt(double offset)
    {
        if (Count == 0)
        {
            return new StripAnchor(0, 0);
        }

        var index = IndexAt(offset);
        var length = LengthOf(index);
        var fraction = length <= 0 ? 0 : Math.Clamp((offset - OffsetOf(index)) / length, 0, 1);
        return new StripAnchor(index, fraction);
    }

    /// <summary>The offset of an anchor under the current sizes.</summary>
    /// <param name="anchor">The anchor.</param>
    public double OffsetOf(StripAnchor anchor)
    {
        if (Count == 0)
        {
            return 0;
        }

        var index = Math.Clamp(anchor.Index, 0, Count - 1);
        return OffsetOf(index) + anchor.Fraction * LengthOf(index);
    }

    private void Rebuild()
    {
        if (!_dirty)
        {
            return;
        }

        _placeholder = _known == 0
            ? (Axis == StripAxis.Horizontal ? DefaultAspect : 1 / DefaultAspect)
            : Median();
        var sum = 0.0;
        for (var i = 0; i < Count; i++)
        {
            _offset[i] = sum;
            sum += double.IsNaN(_length[i]) ? _placeholder : _length[i];
        }

        _offset[Count] = sum;
        _dirty = false;
    }

    private double Median()
    {
        var known = new double[_known];
        var n = 0;
        foreach (var length in _length)
        {
            if (!double.IsNaN(length))
            {
                known[n++] = length;
            }
        }

        Array.Sort(known);
        return n % 2 == 1 ? known[n / 2] : (known[n / 2 - 1] + known[n / 2]) / 2;
    }
}
