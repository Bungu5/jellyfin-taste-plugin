using System.Net;
using System.Net.Http;
using Jellyfin.Plugin.Taste.Data;
using Jellyfin.Plugin.Taste.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Taste.Tests;

/// <summary>
/// One refresh of 11 people used to call each upstream URL once per person. Failures are now fetched once and shared.
/// </summary>
public sealed class UpstreamCacheTests : IDisposable
{
    private const int People = 11;
    private readonly string _path = Path.Combine(Path.GetTempPath(), "taste-upstream-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = _path + suffix;
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Theory]
    [InlineData("/api/v1/movie/603", true)]
    [InlineData("/api/v1/tv/1399", true)]
    [InlineData("/api/v1/movie/603/recommendations", false)]
    [InlineData("/api/v1/discover/movies?sortBy=popularity.desc", false)]
    [InlineData("/api/v1/user?take=1000&skip=0", false)]
    public void SeerrTitleLookupsAreTheDetailPathsOnly(string path, bool title)
        => Assert.Equal(title, SeerrClient.IsTitleLookup(path));

    [Fact]
    public async Task MdbListCatalogIsFetchedOncePerUrlAndA403IsNotRetried()
    {
        var handler = new CountingHandler(HttpStatusCode.Forbidden);
        var warnings = new WarningCount<MdbListClient>();
        var client = new MdbListClient(new ClientFactory(handler), new TasteStore(_path), warnings, () => "key");

        await Task.WhenAll(Enumerable.Range(0, People).Select(_ => FetchCatalogs(client)));

        Assert.Equal(4, handler.Calls);
        Assert.Equal(4, warnings.Count);
        Assert.False((await OneCatalog(client)).Available);

        await Task.WhenAll(Enumerable.Range(0, People).Select(_ => FetchCatalogs(client)));

        Assert.Equal(4, handler.Calls);
        Assert.Equal(4, warnings.Count);
    }

    [Fact]
    public async Task MdbListEmptyCatalogIsStillOn()
    {
        var handler = new CountingHandler(HttpStatusCode.OK) { Body = "[]" };
        var client = new MdbListClient(new ClientFactory(handler), new TasteStore(_path), new WarningCount<MdbListClient>(), () => "key");

        var first = await client.GetCatalogAsync("movie", new DateTime(2026, 1, 2), new DateTime(2026, 2, 2), 80, ["en"], CancellationToken.None);
        var second = await client.GetCatalogAsync("movie", new DateTime(2026, 1, 2), new DateTime(2026, 2, 2), 80, ["en"], CancellationToken.None);

        Assert.True(first.Available);
        Assert.True(second.Available);
        Assert.Empty(first.Titles);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SeerrTitleFailureIsFetchedOnceAndOtherPathsAreNotCached()
    {
        var handler = new CountingHandler(HttpStatusCode.InternalServerError);
        var warnings = new WarningCount<SeerrClient>();
        var client = new SeerrClient(
            new ClientFactory(handler),
            new TasteStore(_path),
            warnings,
            () => true,
            () => "http://seerr",
            () => "key");
        var titles = new[] { "/api/v1/movie/101", "/api/v1/movie/102", "/api/v1/tv/201", "/api/v1/tv/202" };

        await Task.WhenAll(Enumerable.Range(0, People).Select(_ => Fetch(client, titles)));
        Assert.Equal(4, handler.Calls);
        Assert.Equal(4, warnings.Count);

        await Task.WhenAll(Enumerable.Range(0, People).Select(_ => Fetch(client, titles)));
        Assert.Equal(4, handler.Calls);
        Assert.Equal(4, warnings.Count);

        for (var i = 0; i < People; i++)
        {
            await client.GetRawAsync("/api/v1/user?take=1000&skip=0", CancellationToken.None);
        }

        Assert.Equal(4 + People, handler.Calls);
        Assert.Equal(5, warnings.Count);
    }

    private static async Task FetchCatalogs(MdbListClient client)
    {
        var from = new DateTime(2026, 6, 23);
        var to = new DateTime(2026, 11, 20);
        string[] languages = ["en"];
        await client.GetCatalogAsync("movie", from, to, 62, languages, CancellationToken.None);
        await client.GetCatalogAsync("movie", null, from, 75, languages, CancellationToken.None);
        await client.GetCatalogAsync("tv", from, to, 62, languages, CancellationToken.None);
        await client.GetCatalogAsync("tv", null, from, 75, languages, CancellationToken.None);
    }

    private static Task<MdbCatalog> OneCatalog(MdbListClient client)
        => client.GetCatalogAsync("movie", new DateTime(2026, 6, 23), new DateTime(2026, 11, 20), 62, ["en"], CancellationToken.None);

    private static Task Fetch(SeerrClient client, IEnumerable<string> paths)
        => Task.WhenAll(paths.Select(path => client.GetRawAsync(path, CancellationToken.None)));

    private sealed class CountingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls;

        public string Body { get; init; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(status) { Content = new StringContent(Body) };
        }
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class WarningCount<T> : ILogger<T>
    {
        public int Count;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (level >= LogLevel.Warning)
            {
                Interlocked.Increment(ref Count);
            }
        }
    }
}
