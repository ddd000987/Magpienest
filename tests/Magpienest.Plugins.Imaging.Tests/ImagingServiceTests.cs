using System.Windows.Media;
using System.Windows.Media.Imaging;
using Magpienest.Sdk.Imaging;

namespace Magpienest.Plugins.Imaging.Tests;

public sealed class ImagingServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "magpienest-tests", Guid.NewGuid().ToString("N"));
    private readonly ImagingService _service = new(new NullLog());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public static TheoryData<string> Formats() => ["png", "jpg", "bmp", "gif", "tif"];

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task DecodesEveryFormat(string format)
    {
        var source = TestImages.Quadrants(40, 20);
        var bytes = format switch
        {
            "png" => TestImages.Png(source),
            "jpg" => TestImages.Jpeg(source),
            "bmp" => TestImages.Bmp(source),
            "gif" => TestImages.Gif(source),
            _ => TestImages.Tiff(source),
        };

        await using var stream = new MemoryStream(bytes);
        var result = await _service.DecodeAsync(stream, new DecodeOptions(), Ct);

        Assert.True(result.IsFrozen);
        Assert.Contains(result.Format, new[] { PixelFormats.Pbgra32, PixelFormats.Bgr32 });
        Assert.Equal(40, result.PixelWidth);
        Assert.Equal(20, result.PixelHeight);
        Assert.Equal("RGBY", TestImages.Corners(result));

        await using var again = new MemoryStream(bytes);
        var info = await _service.ReadInfoAsync(again, Ct);
        Assert.Equal(new ImageInfo(40, 20), info);
    }

    [Theory]
    [InlineData(1, "RGBY", 32, 16)]
    [InlineData(2, "GRYB", 32, 16)]
    [InlineData(3, "YBGR", 32, 16)]
    [InlineData(4, "BYRG", 32, 16)]
    [InlineData(5, "RBGY", 16, 32)]
    [InlineData(6, "BRYG", 16, 32)]
    [InlineData(7, "YGBR", 16, 32)]
    [InlineData(8, "GYRB", 16, 32)]
    public async Task AppliesExifOrientation(int orientation, string corners, int width, int height)
    {
        var bytes = TestImages.JpegWithOrientation(TestImages.Quadrants(32, 16), (ushort)orientation);

        await using var stream = new MemoryStream(bytes);
        var result = await _service.DecodeAsync(stream, new DecodeOptions(), Ct);
        Assert.True(result.IsFrozen);
        Assert.Equal(width, result.PixelWidth);
        Assert.Equal(height, result.PixelHeight);
        Assert.Equal(corners, TestImages.Corners(result));

        await using var again = new MemoryStream(bytes);
        Assert.Equal(new ImageInfo(width, height), await _service.ReadInfoAsync(again, Ct));
    }

    [Theory]
    [InlineData(1000, 0, 1000, 750)]
    [InlineData(0, 600, 800, 600)]
    [InlineData(1000, 300, 400, 300)]
    [InlineData(300, 1000, 300, 225)]
    [InlineData(5000, 5000, 4000, 3000)]
    public async Task RespectsLimits(int maxWidth, int maxHeight, int expectedWidth, int expectedHeight)
    {
        await using var stream = new MemoryStream(TestImages.LargeJpeg());
        var result = await _service.DecodeAsync(stream, new DecodeOptions(maxWidth, maxHeight), Ct);

        Assert.InRange(result.PixelWidth, expectedWidth - 1, expectedWidth + 1);
        Assert.InRange(result.PixelHeight, expectedHeight - 1, expectedHeight + 1);
        if (maxWidth > 0)
        {
            Assert.True(result.PixelWidth <= maxWidth);
        }

        if (maxHeight > 0)
        {
            Assert.True(result.PixelHeight <= maxHeight);
        }
    }

    [Fact]
    public async Task LimitsApplyToTheOrientedImage()
    {
        // 4000x3000 raw pixels, shown as 3000x4000 (portrait).
        var bytes = TestImages.JpegWithOrientation(TestImages.Gradient(4000, 3000), 6);
        await using var stream = new MemoryStream(bytes);

        var result = await _service.DecodeAsync(stream, new DecodeOptions(500, 500), Ct);

        Assert.True(result.PixelWidth <= 500);
        Assert.True(result.PixelHeight <= 500);
        Assert.InRange(result.PixelHeight, 499, 500);
        Assert.InRange(result.PixelWidth, 374, 376);
        Assert.True(result.PixelHeight > result.PixelWidth);

        stream.Position = 0;
        var wide = await _service.DecodeAsync(stream, new DecodeOptions(MaxPixelWidth: 600), Ct);
        Assert.InRange(wide.PixelWidth, 599, 600);
        Assert.InRange(wide.PixelHeight, 799, 801);
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(16, 16)]
    public async Task NeverUpscales(int maxWidth, int maxHeight)
    {
        await using var stream = new MemoryStream(TestImages.Png(TestImages.Quadrants(16, 16)));
        var result = await _service.DecodeAsync(stream, new DecodeOptions(maxWidth, maxHeight), Ct);

        Assert.Equal(16, result.PixelWidth);
        Assert.Equal(16, result.PixelHeight);
    }

    [Fact]
    public async Task BrokenFileThrowsImageDecodeException()
    {
        var path = Path.Combine(Directory.CreateDirectory(_root).FullName, "broken.jpg");
        await File.WriteAllTextAsync(path, "this is not a jpeg at all", Ct);

        await using (var stream = File.OpenRead(path))
        {
            var exception = await Assert.ThrowsAsync<ImageDecodeException>(() => _service.DecodeAsync(stream, new DecodeOptions(), Ct));
            Assert.NotNull(exception.InnerException);
        }

        await using var again = File.OpenRead(path);
        Assert.Null(await _service.ReadInfoAsync(again, Ct));
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await using var stream = new MemoryStream(TestImages.Png(TestImages.Quadrants(8, 8)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.DecodeAsync(stream, new DecodeOptions(), cts.Token));
    }

    [Fact]
    public async Task EncodeJpegProducesAJpegAndFlattensTransparencyOnWhite()
    {
        await using var png = new MemoryStream(TestImages.Png(TestImages.WithTransparency(20, 10)));
        var decoded = await _service.DecodeAsync(png, new DecodeOptions(), Ct);

        var jpeg = _service.EncodeJpeg(decoded, 90);

        Assert.True(jpeg.Length > 4);
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);

        await using var stream = new MemoryStream(jpeg);
        var round = await _service.DecodeAsync(stream, new DecodeOptions(), Ct);
        Assert.Equal(20, round.PixelWidth);
        Assert.Equal(10, round.PixelHeight);
        var converted = new FormatConvertedBitmap(round, PixelFormats.Bgra32, null, 0);
        var transparentSide = new byte[4];
        converted.CopyPixels(new System.Windows.Int32Rect(2, 5, 1, 1), transparentSide, 4, 0);
        Assert.True(transparentSide[0] >= 250 && transparentSide[1] >= 250 && transparentSide[2] >= 250, "transparent area is not white");
        var opaqueSide = new byte[4];
        converted.CopyPixels(new System.Windows.Int32Rect(17, 5, 1, 1), opaqueSide, 4, 0);
        Assert.True(opaqueSide[0] > 200 && opaqueSide[2] < 60, "opaque blue area changed");
    }

    [Fact]
    public void IsSupportedAcceptsPathsAndExtensionsCaseInsensitively()
    {
        Assert.True(_service.IsSupported(".jpg"));
        Assert.True(_service.IsSupported(".JPG"));
        Assert.True(_service.IsSupported("jpg"));
        Assert.True(_service.IsSupported(@"C:\pics\Holiday.PNG"));
        Assert.True(_service.IsSupported("a/b/c.webp"));
        Assert.False(_service.IsSupported(@"C:\pics\notes.txt"));
        Assert.False(_service.IsSupported(@"C:\pics\noextension"));
        Assert.False(_service.IsSupported(string.Empty));
        Assert.Contains(".jxl", _service.SupportedExtensions);
    }

    [Fact]
    public async Task RegisteredDecoderWinsForItsExtensionAndDisappearsWithItsOwner()
    {
        var folder = Directory.CreateDirectory(_root).FullName;
        var fakePath = Path.Combine(folder, "picture.fake");
        await File.WriteAllBytesAsync(fakePath, [1, 2, 3], Ct);
        var pngPath = Path.Combine(folder, "real.png");
        await File.WriteAllBytesAsync(pngPath, TestImages.Png(TestImages.Quadrants(16, 16)), Ct);

        var owner = new FakePluginContext();
        _service.RegisterDecoder(owner, new FakeDecoder(".fake", ".PNG"));

        Assert.Contains(".fake", _service.SupportedExtensions);
        Assert.True(_service.IsSupported("x.fake"));

        // By file name: it wins even over the built-in .png.
        await using (var stream = File.OpenRead(fakePath))
        {
            var result = await _service.DecodeAsync(stream, new DecodeOptions(), Ct);
            Assert.Equal(5, result.PixelWidth);
            Assert.Equal(7, result.PixelHeight);
            Assert.True(result.IsFrozen);
            Assert.Equal(PixelFormats.Pbgra32, result.Format);
        }

        await using (var stream = File.OpenRead(pngPath))
        {
            var result = await _service.DecodeAsync(stream, new DecodeOptions(), Ct);
            Assert.Equal(5, result.PixelWidth);
        }

        // Without a file name the decoder is found by probing its ReadInfo.
        await using (var stream = new MemoryStream("FAKE data"u8.ToArray()))
        {
            Assert.Equal(new ImageInfo(5, 7), await _service.ReadInfoAsync(stream, Ct));
            var result = await _service.DecodeAsync(stream, new DecodeOptions(), Ct);
            Assert.Equal(7, result.PixelHeight);
        }

        owner.Dispose();

        Assert.DoesNotContain(".fake", _service.SupportedExtensions);
        Assert.False(_service.IsSupported("x.fake"));
        Assert.True(_service.IsSupported("x.png"));
        await using (var stream = File.OpenRead(fakePath))
        {
            await Assert.ThrowsAsync<ImageDecodeException>(() => _service.DecodeAsync(stream, new DecodeOptions(), Ct));
        }

        await using (var stream = File.OpenRead(pngPath))
        {
            var result = await _service.DecodeAsync(stream, new DecodeOptions(), Ct);
            Assert.Equal(16, result.PixelWidth);
        }
    }

    [Fact]
    public async Task DecoderFailureIsWrapped()
    {
        var owner = new FakePluginContext();
        _service.RegisterDecoder(owner, new FakeDecoder(".boom") { Throw = true });
        var path = Path.Combine(Directory.CreateDirectory(_root).FullName, "x.boom");
        await File.WriteAllBytesAsync(path, [0], Ct);

        await using var stream = File.OpenRead(path);
        var exception = await Assert.ThrowsAsync<ImageDecodeException>(() => _service.DecodeAsync(stream, new DecodeOptions(), Ct));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        owner.Dispose();
    }

    private sealed class FakeDecoder(params string[] extensions) : IImageDecoder
    {
        public bool Throw { get; init; }

        public IReadOnlyCollection<string> Extensions { get; } = extensions;

        public BitmapSource Decode(Stream seekable, DecodeOptions options, CancellationToken ct)
        {
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }

            var bitmap = BitmapSource.Create(5, 7, 96, 96, PixelFormats.Pbgra32, null, new byte[5 * 7 * 4], 5 * 4);
            bitmap.Freeze();
            return bitmap;
        }

        public ImageInfo? ReadInfo(Stream seekable)
        {
            var header = new byte[4];
            var read = seekable.Read(header, 0, 4);
            return read == 4 && header.SequenceEqual("FAKE"u8.ToArray()) ? new ImageInfo(5, 7) : null;
        }
    }
}
