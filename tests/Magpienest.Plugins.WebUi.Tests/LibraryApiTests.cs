using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Magpienest.Plugins.WebUi;
using Magpienest.Sdk;
using Magpienest.Sdk.Imaging;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Thumbnails;
using Magpienest.Sdk.Viewing;

namespace Magpienest.Plugins.WebUi.Tests;

/// <summary>The library endpoints on a real server, with a fake library and temp files.</summary>
public sealed class LibraryApiTests : IAsyncLifetime
{
    private const string Token = "test-token-0123456789";
    private const int PageCount = 6;
    private const int WidePage = 2;

    private static readonly byte[] JpegMarker = "converted-jpeg"u8.ToArray();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mn-webui-lib-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    private readonly FakeClock _clock = new();
    private FakeLibrary _library = null!;
    private LibraryApi _api = null!;
    private WebUiServer? _server;
    private ushort _port;

    private string Base => $"http://127.0.0.1:{_port}";

    public async ValueTask InitializeAsync()
    {
        var www = Path.Combine(_root, "www");
        var pages = Path.Combine(_root, "book");
        Directory.CreateDirectory(www);
        Directory.CreateDirectory(pages);
        await File.WriteAllBytesAsync(Path.Combine(_root, "a.png"), [.. Enumerable.Range(0, 1000).Select(i => (byte)i)]);
        await File.WriteAllBytesAsync(Path.Combine(_root, "b.tif"), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(_root, "c.svg"), "<svg/>");
        await File.WriteAllBytesAsync(Path.Combine(_root, "v.mp4"), new byte[10_000]);
        await File.WriteAllBytesAsync(Path.Combine(_root, "game.exe"), [0x4D, 0x5A]);
        for (var i = 0; i < PageCount; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(pages, $"p{i}.png"), i == WidePage ? "W" : "T");
        }

        _library = new FakeLibrary(pages);
        _library.Add("illustration", Path.Combine(_root, "a.png"), "Alpha");
        _library.Add("illustration", Path.Combine(_root, "b.tif"), "Beta");
        _library.Add("illustration", Path.Combine(_root, "c.svg"), "Gamma");
        _library.Add("video", Path.Combine(_root, "v.mp4"), "Video", total: 1000);
        _library.Add("comic", pages, "Book", extra: """{"direction":"ltr","custom":5}""");
        _library.Add("game", Path.Combine(_root, "game.exe"), "Game");

        _api = new LibraryApi(_library, new FakeThumbnails(), new FakeImaging(), _clock);
        _port = WebUiServer.FreePort();
        _server = new WebUiServer([new WebBinding(IPAddress.Loopback, _port)],
                                  new WebUiHandlerOptions { WwwRoot = www, Token = () => Token, MachineName = "TESTPC", Library = _api },
                                  new NullBackend(), new WebEventHub(), new NullLog());
        Assert.True(await _server.StartAsync());
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        await _api.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private long Id(string title) => _library.Items.Single(i => i.Title == title).Id;

    [Fact]
    public async Task Items_are_paged_from_one_cached_query()
    {
        var first = await GetJsonAsync("/api/items?kind=illustration&limit=2");
        Assert.Equal(3, first.GetProperty("total").GetInt32());
        Assert.Equal(["Alpha", "Beta"], first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()));
        var item = first.GetProperty("items")[0];
        Assert.Equal("none", item.GetProperty("status").GetString());
        Assert.False(item.TryGetProperty("path", out _));

        var second = await GetJsonAsync("/api/items?kind=illustration&limit=2&offset=2");
        Assert.Equal("Gamma", Assert.Single(second.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());
        Assert.Equal(1, _library.QueryCalls);

        _clock.Advance(LibraryApi.QueryCacheLifetime + TimeSpan.FromSeconds(1));
        await GetJsonAsync("/api/items?kind=illustration&limit=2");
        Assert.Equal(2, _library.QueryCalls);

        // A library change forgets the cache at once.
        _api.Invalidate();
        await GetJsonAsync("/api/items?kind=illustration&limit=2");
        Assert.Equal(3, _library.QueryCalls);
    }

    [Fact]
    public async Task Query_values_are_decoded_and_mapped()
    {
        await GetJsonAsync("/api/items?kind=comic&text=%E6%BC%AB+%E7%95%AB&tags=a,b&dir=D%3A%5Cx&favorites=true&minRating=3" +
                           "&status=inProgress&sort=addedAt&desc=1");
        var query = _library.LastQuery!;
        Assert.Equal("漫 畫", query.Text);
        Assert.Equal(["a", "b"], query.Tags);
        Assert.Equal(@"D:\x", query.Directory);
        Assert.True(query.FavoritesOnly);
        Assert.Equal(3, query.MinRating);
        Assert.Equal(ItemStatus.InProgress, query.Status);
        Assert.Equal(ItemSort.AddedAt, query.Sort);
        Assert.True(query.Descending);

        await GetJsonAsync("/api/items?kind=comic&text=1%2B1%3D2%25");
        Assert.Equal("1+1=2%", _library.LastQuery!.Text);
    }

    [Theory]
    [InlineData("kind=nothing")]
    [InlineData("")]
    [InlineData("kind=illustration&sort=bogus")]
    [InlineData("kind=illustration&status=finished")]
    [InlineData("kind=illustration&limit=0")]
    [InlineData("kind=illustration&limit=501")]
    [InlineData("kind=illustration&offset=-1")]
    [InlineData("kind=illustration&minRating=6")]
    [InlineData("kind=illustration&favorites=maybe")]
    public async Task Unknown_query_values_are_bad_requests(string query)
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/items?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Item_details_include_tags_notes_and_extra()
    {
        var json = await GetJsonAsync($"/api/items/{Id("Book")}");
        Assert.Equal("Book", json.GetProperty("title").GetString());
        Assert.Equal("ltr", json.GetProperty("extra").GetProperty("direction").GetString());
        Assert.Equal(["x"], json.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("note", json.GetProperty("notes").GetString());

        using var missing = await SendAsync(HttpMethod.Get, "/api/items/999");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Tags_and_directories_per_kind()
    {
        var tags = await GetJsonAsync("/api/tags?kind=comic");
        Assert.Equal("x", tags[0].GetProperty("name").GetString());
        var dirs = await GetJsonAsync("/api/dirs?kind=comic");
        Assert.Equal(_root, dirs[0].GetString());
        using var bad = await SendAsync(HttpMethod.Get, "/api/tags?kind=nothing");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Theory]
    [InlineData(null, 200, 0, 1000)]
    [InlineData("bytes=0-9", 206, 0, 10)]
    [InlineData("bytes=500-599", 206, 500, 100)]
    [InlineData("bytes=990-", 206, 990, 10)]
    [InlineData("bytes=-500", 206, 500, 500)]
    [InlineData("bytes=-5000", 206, 0, 1000)]
    [InlineData("bytes=900-5000", 206, 900, 100)]
    [InlineData("bytes=0-1,5-6", 200, 0, 1000)]
    public async Task File_supports_ranges(string? range, int status, int start, int length)
    {
        var ct = TestContext.Current.CancellationToken;
        using var request = Request(HttpMethod.Get, $"/api/file/{Id("Alpha")}");
        if (range is not null)
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        using var response = await _client.SendAsync(request, ct);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("bytes", Header(response, "Accept-Ranges"));
        var body = await response.Content.ReadAsByteArrayAsync(ct);
        Assert.Equal(Enumerable.Range(start, length).Select(i => (byte)i), body);
        if (status == 206)
        {
            Assert.Equal($"bytes {start}-{start + length - 1}/1000", Header(response, "Content-Range"));
        }
    }

    [Theory]
    [InlineData("bytes=1000-")]
    [InlineData("bytes=5-2")]
    [InlineData("bytes=-0")]
    [InlineData("bytes=abc")]
    [InlineData("items=0-1")]
    public async Task Unsatisfiable_ranges_are_416(string range)
    {
        using var request = Request(HttpMethod.Get, $"/api/file/{Id("Alpha")}");
        request.Headers.TryAddWithoutValidation("Range", range);
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal("bytes */1000", Header(response, "Content-Range"));
    }

    [Fact]
    public async Task Video_is_streamed_with_its_type()
    {
        using var request = Request(HttpMethod.Get, $"/api/file/{Id("Video")}");
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-");
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(10_000, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Non_native_image_is_converted_to_jpeg()
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await SendAsync(HttpMethod.Get, $"/api/file/{Id("Beta")}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(JpegMarker, await response.Content.ReadAsByteArrayAsync(ct));
    }

    [Theory]
    [InlineData("Gamma")] // .svg
    [InlineData("Book")] // a comic is a folder or an archive, never a file to send
    [InlineData("Game")] // an executable
    public async Task Svg_and_other_kinds_files_are_refused(string title)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/file/{Id(title)}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Thumbnail_has_an_etag_and_answers_304()
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await SendAsync(HttpMethod.Get, $"/api/thumb/{Id("Alpha")}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.Equal(TimeSpan.FromDays(1), response.Headers.CacheControl?.MaxAge);
        var tag = Header(response, "ETag");
        Assert.StartsWith("\"", tag);

        using var again = Request(HttpMethod.Get, $"/api/thumb/{Id("Alpha")}");
        again.Headers.TryAddWithoutValidation("If-None-Match", tag);
        using var cached = await _client.SendAsync(again, ct);
        Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);

        using var none = await SendAsync(HttpMethod.Get, $"/api/thumb/{Id("Game")}");
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    }

    [Fact]
    public async Task Patch_merges_extra_and_keeps_unknown_keys()
    {
        var id = Id("Book");
        using var response = await SendAsync(HttpMethod.Patch, $"/api/items/{id}",
                                             """{"favorite":true,"rating":4,"extra":{"spread":"single","coverAlone":null}}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = _library.Items.Single(i => i.Id == id);
        Assert.True(item.IsFavorite);
        Assert.Equal(4, item.Rating);
        var extra = JsonNode.Parse(item.Extra)!.AsObject();
        Assert.Equal("ltr", (string?)extra["direction"]);
        Assert.Equal(5, (int?)extra["custom"]);
        Assert.Equal("single", (string?)extra["spread"]);
        Assert.True(extra.ContainsKey("coverAlone"));
    }

    [Theory]
    [InlineData("""{"rating":6}""")]
    [InlineData("""{"status":"finished"}""")]
    [InlineData("""{"path":"C:\\evil"}""")]
    [InlineData("""{"extra":"text"}""")]
    [InlineData("""{"addTimeSpentSeconds":-1}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task Invalid_patches_are_bad_requests(string body)
    {
        using var response = await SendAsync(HttpMethod.Patch, $"/api/items/{Id("Book")}", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_library.Patches);
    }

    [Fact]
    public async Task Beacon_accepts_only_progress_fields()
    {
        var id = Id("Book");
        using (var refused = await SendAsync(HttpMethod.Post, $"/api/items/{id}/progress", """{"progressPosition":1,"favorite":true}"""))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        using var response = await SendAsync(HttpMethod.Post, $"/api/items/{id}/progress",
                                             """{"progressPosition":3,"progressTotal":6,"addTimeSpentSeconds":12,"markOpened":true}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var patch = Assert.Single(_library.Patches);
        Assert.Equal(3, patch.ProgressPosition);
        Assert.Equal(6, patch.ProgressTotal);
        Assert.Equal(12, patch.AddTimeSpentSeconds);
        Assert.True(patch.MarkOpened);
        Assert.Equal(ItemStatus.InProgress, patch.Status);
        Assert.Null(patch.IsFavorite);
    }

    [Fact]
    public async Task Video_past_95_percent_is_completed_but_on_hold_stays()
    {
        var id = Id("Video");
        using (await SendAsync(HttpMethod.Post, $"/api/items/{id}/progress", """{"progressPosition":960}"""))
        {
        }

        Assert.Equal(ItemStatus.Completed, _library.Items.Single(i => i.Id == id).Status);

        _library.Set(id, i => i with { Status = ItemStatus.OnHold });
        using (await SendAsync(HttpMethod.Post, $"/api/items/{id}/progress", """{"progressPosition":990,"reachedEnd":true}"""))
        {
        }

        Assert.Equal(ItemStatus.OnHold, _library.Items.Single(i => i.Id == id).Status);
    }

    [Fact]
    public async Task Book_lists_pages_and_reading_settings()
    {
        var id = Id("Book");
        _library.Set(id, i => i with { ProgressPosition = 4 });
        var json = await GetJsonAsync($"/api/books/{id}");
        Assert.Equal(PageCount, json.GetProperty("count").GetInt32());
        Assert.Equal("p0.png", json.GetProperty("names")[0].GetString());
        Assert.Equal("ltr", json.GetProperty("direction").GetString()); // from extra
        Assert.Equal("double", json.GetProperty("spread").GetString()); // default
        Assert.True(json.GetProperty("coverAlone").GetBoolean());
        Assert.Equal("paged", json.GetProperty("flow").GetString());
        Assert.Equal(4, json.GetProperty("position").GetInt32());

        _library.Set(id, i => i with { Status = ItemStatus.Completed });
        Assert.Equal(0, (await GetJsonAsync($"/api/books/{id}")).GetProperty("position").GetInt32());

        using var notBook = await SendAsync(HttpMethod.Get, $"/api/books/{Id("Alpha")}");
        Assert.Equal(HttpStatusCode.NotFound, notBook.StatusCode);
    }

    [Fact]
    public async Task Pages_are_served_by_index()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Id("Book");
        using var page = await SendAsync(HttpMethod.Get, $"/api/books/{id}/pages/{WidePage}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("image/png", page.Content.Headers.ContentType?.MediaType);
        Assert.Equal("W", await page.Content.ReadAsStringAsync(ct));

        using var outside = await SendAsync(HttpMethod.Get, $"/api/books/{id}/pages/{PageCount}");
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);

        // One open book, shared by the requests, closed after five idle minutes.
        Assert.Equal(1, _api.Books.Count);
        Assert.Equal(1, _library.PageSourcesOpened);
        _clock.Advance(BookCache.Idle);
        await _api.Books.SweepAsync();
        Assert.Equal(0, _api.Books.Count);
    }

    [Fact]
    public async Task Frame_matches_FrameLayout()
    {
        var id = Id("Book");
        static bool IsWide(int index) => index == WidePage;
        foreach (var spread in new[] { "single", "double" })
        {
            foreach (var coverAlone in new[] { true, false })
            {
                for (var at = 0; at < PageCount; at++)
                {
                    var mode = spread == "single" ? SpreadMode.Single : SpreadMode.Double;
                    var forward = await GetJsonAsync($"/api/books/{id}/frame?at={at}&dir=forward&spread={spread}&coverAlone={(coverAlone ? "true" : "false")}");
                    var expected = FrameLayout.Forward(at, PageCount, mode, coverAlone, IsWide);
                    Assert.Equal((expected.First, expected.Last), (forward.GetProperty("first").GetInt32(), forward.GetProperty("last").GetInt32()));

                    var backward = await GetJsonAsync($"/api/books/{id}/frame?at={at}&dir=backward&spread={spread}&coverAlone={(coverAlone ? "true" : "false")}");
                    expected = FrameLayout.Backward(at, PageCount, mode, coverAlone, IsWide);
                    Assert.Equal((expected.First, expected.Last), (backward.GetProperty("first").GetInt32(), backward.GetProperty("last").GetInt32()));
                }
            }
        }

        foreach (var bad in new[] { "at=6", "at=-1", "at=x", "", "at=0&dir=up", "at=0&spread=triple", "at=0&coverAlone=maybe" })
        {
            using var response = await SendAsync(HttpMethod.Get, $"/api/books/{id}/frame?{bad}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Library_endpoints_need_the_token()
    {
        using var response = await _client.GetAsync(Base + "/api/items?kind=illustration", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await SendAsync(HttpMethod.Get, path);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body = null)
    {
        var request = Request(method, path);
        if (body is not null)
        {
            request.Headers.Add("Origin", Base);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, Base + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(",", values)
            : "";

    /// <summary>The automatic status changes for progress reported by the web page.</summary>
    [Theory]
    [InlineData("comic", ItemStatus.None, false, ItemStatus.InProgress)]
    [InlineData("comic", ItemStatus.None, true, ItemStatus.Completed)]
    [InlineData("comic", ItemStatus.InProgress, false, null)]
    [InlineData("comic", ItemStatus.InProgress, true, ItemStatus.Completed)]
    [InlineData("comic", ItemStatus.Completed, false, null)]
    [InlineData("comic", ItemStatus.Completed, true, null)]
    [InlineData("comic", ItemStatus.OnHold, true, null)]
    [InlineData("comic", ItemStatus.Dropped, false, null)]
    [InlineData("comic", ItemStatus.Planned, true, null)]
    [InlineData("video", ItemStatus.None, false, ItemStatus.InProgress)]
    [InlineData("video", ItemStatus.Planned, false, null)]
    [InlineData("illustration", ItemStatus.None, true, null)]
    [InlineData("game", ItemStatus.None, false, null)]
    public void Status_rule_table(string kind, ItemStatus current, bool reachedEnd, ItemStatus? expected) =>
        Assert.Equal(expected, StatusRules.AfterProgress(kind, current, reachedEnd));

    [Theory]
    [InlineData("video", false, 949L, 1000L, false)]
    [InlineData("video", false, 950L, 1000L, true)]
    [InlineData("video", false, 950L, 0L, false)]
    [InlineData("video", true, null, 0L, true)]
    [InlineData("comic", false, 5L, 6L, false)]
    public void Reached_end(string kind, bool reported, long? position, long total, bool expected) =>
        Assert.Equal(expected, StatusRules.ReachedEnd(kind, reported, position, total));

    [Fact]
    public void Merge_extra_replaces_invalid_json() =>
        Assert.Equal("""{"a":1}""", LibraryRequests.MergeExtra("not json", new JsonObject { ["a"] = 1 }));

    /// <summary>An in-memory library.</summary>
    private sealed class FakeLibrary(string bookFolder) : ILibraryService
    {
        private readonly object _lock = new();
        private readonly List<LibraryItem> _items = [];
        private int _pageSourcesOpened;
        private int _queryCalls;

        public IReadOnlyList<LibraryItem> Items
        {
            get
            {
                lock (_lock)
                {
                    return [.. _items];
                }
            }
        }

        public List<ItemPatch> Patches { get; } = [];

        public int QueryCalls => Volatile.Read(ref _queryCalls);

        public int PageSourcesOpened => Volatile.Read(ref _pageSourcesOpened);

        public ItemQuery? LastQuery { get; private set; }

        public IReadOnlyList<ItemKindDescriptor> Kinds =>
        [
            new() { Id = "illustration", DisplayNameKey = "k.i" },
            new() { Id = "video", DisplayNameKey = "k.v" },
            new() { Id = "game", DisplayNameKey = "k.g" },
            new()
            {
                Id = "comic",
                DisplayNameKey = "k.c",
                OpenPages = (_, _) =>
                {
                    Interlocked.Increment(ref _pageSourcesOpened);
                    return Task.FromResult<IPageSource>(new FilePageSource(
                        [.. Directory.GetFiles(bookFolder).Order(StringComparer.Ordinal)]));
                },
            },
        ];

        public void Add(string kind, string path, string title, long total = 0, string extra = "{}")
        {
            lock (_lock)
            {
                _items.Add(new LibraryItem
                {
                    Id = _items.Count + 1,
                    Kind = kind,
                    Path = path,
                    Directory = Path.GetDirectoryName(path)!,
                    Title = title,
                    FileSize = File.Exists(path) ? new FileInfo(path).Length : 0,
                    ProgressTotal = total,
                    Extra = extra,
                    Notes = kind == "comic" ? "note" : "",
                    Tags = kind == "comic" ? ["x"] : [],
                });
            }
        }

        public void Set(long id, Func<LibraryItem, LibraryItem> change)
        {
            lock (_lock)
            {
                var index = _items.FindIndex(i => i.Id == id);
                _items[index] = change(_items[index]);
            }
        }

        public Task<IReadOnlyList<LibraryItem>> QueryAsync(ItemQuery query, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _queryCalls);
            LastQuery = query;
            return Task.FromResult<IReadOnlyList<LibraryItem>>(
                [.. Items.Where(i => i.Kind == query.Kind).OrderBy(i => i.Title, StringComparer.Ordinal)]);
        }

        public Task<LibraryItem?> GetAsync(long id) => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));

        public Task UpdateAsync(long id, ItemPatch patch)
        {
            lock (_lock)
            {
                Patches.Add(patch);
            }

            Set(id, i => i with
            {
                IsFavorite = patch.IsFavorite ?? i.IsFavorite,
                Rating = patch.Rating ?? i.Rating,
                Status = patch.Status ?? i.Status,
                ProgressPosition = patch.ProgressPosition ?? i.ProgressPosition,
                ProgressTotal = patch.ProgressTotal ?? i.ProgressTotal,
                Extra = patch.Extra ?? i.Extra,
            });
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TagCount>> GetTagsAsync(string kind) =>
            Task.FromResult<IReadOnlyList<TagCount>>([new TagCount("x", 1)]);

        public Task<IReadOnlyList<string>> GetDirectoriesAsync(string kind) =>
            Task.FromResult<IReadOnlyList<string>>([.. Items.Where(i => i.Kind == kind).Select(i => i.Directory).Distinct()]);

        public Task<IReadOnlyList<FolderLabels>> GetFolderLabelsAsync(string kind) => Task.FromResult<IReadOnlyList<FolderLabels>>([]);

        public Task SetFolderLabelsAsync(string kind, string path, int rating, IReadOnlyCollection<string> tags) => throw new NotSupportedException();

        public void RegisterKind(IPluginContext owner, ItemKindDescriptor kind) => throw new NotSupportedException();

        public Task<IReadOnlyList<LibrarySource>> GetSourcesAsync(string kind) => throw new NotSupportedException();

        public Task<LibrarySource> AddSourceAsync(string kind, string path) => throw new NotSupportedException();

        public Task RemoveSourceAsync(long sourceId) => throw new NotSupportedException();

        public Task<ScanResult> ScanAsync(long sourceId, IProgress<ScanProgress>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LibraryItem> AddAsync(NewItem item) => throw new NotSupportedException();

        public Task RemoveAsync(IReadOnlyCollection<long> ids) => throw new NotSupportedException();

        public Task SetTagsAsync(long id, IReadOnlyCollection<string> tags) => throw new NotSupportedException();
    }

    private sealed class FakeThumbnails : IThumbnailService
    {
        public Task<byte[]?> GetJpegAsync(LibraryItem item, CancellationToken ct = default) =>
            Task.FromResult(item.Kind == "game" ? null : new byte[] { 0xFF, 0xD8, 0xFF });

        public void RegisterProvider(IPluginContext owner, string kind, IThumbnailProvider provider) => throw new NotSupportedException();

        public Task<ImageSource?> GetAsync(LibraryItem item, CancellationToken ct = default) => throw new NotSupportedException();

        public Task InvalidateAsync(LibraryItem item) => throw new NotSupportedException();

        public Task ClearCacheAsync() => throw new NotSupportedException();

        public Task<BitmapSource?> GetShellImageAsync(string path, int pixelSize, bool iconOnly, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Supports ".tif" besides the native formats; a page whose first byte is 'W' is wide.</summary>
    private sealed class FakeImaging : IImagingService
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".tif" };

        public bool IsSupported(string pathOrExtension) => SupportedExtensions.Contains(Path.GetExtension(pathOrExtension));

        public Task<BitmapSource> DecodeAsync(Stream seekable, DecodeOptions options, CancellationToken ct = default)
        {
            Assert.Equal(LibraryApi.MaxConvertedSize, options.MaxPixelWidth);
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr32, null, new byte[4], 4);
            bitmap.Freeze();
            return Task.FromResult(bitmap);
        }

        public async Task<ImageInfo?> ReadInfoAsync(Stream seekable, CancellationToken ct = default)
        {
            var first = new byte[1];
            await seekable.ReadExactlyAsync(first, ct);
            return first[0] == 'W' ? new ImageInfo(200, 100) : new ImageInfo(100, 200);
        }

        public byte[] EncodeJpeg(BitmapSource source, int quality)
        {
            Assert.Equal(LibraryApi.ConvertedQuality, quality);
            return JpegMarker;
        }

        public void RegisterDecoder(IPluginContext owner, IImageDecoder decoder) => throw new NotSupportedException();
    }

    private sealed class NullBackend : IWebUiBackend
    {
        public Task<WebState> GetStateAsync() => Task.FromResult(new WebState("en", "light", []));

        public Task<IReadOnlyDictionary<string, string>> GetStringsAsync() =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    }
}
