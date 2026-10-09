using Jellyfin.Plugin.Taste.Data;

namespace Jellyfin.Plugin.Taste.Tests;

public sealed class TasteStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "taste-test-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }

    [Fact]
    public void AnswersRoundTripPerUser()
    {
        var store = new TasteStore(_path);
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var key = TasteStore.TmdbKey("movie", "603");

        store.Update(alice, key, a => a with { Stars = 5, Thumb = 1, Title = "The Matrix", Year = 1999 });

        var answer = store.GetAnswer(alice, key);
        Assert.Equal(5, answer.Stars);
        Assert.Equal(1, answer.Thumb);
        Assert.Equal("The Matrix", answer.Title);
        Assert.Empty(store.GetAnswers(bob));
        Assert.Single(store.GetAnswers(alice));
    }

    [Fact]
    public void UpdateKeepsUnchangedFields()
    {
        var store = new TasteStore(_path);
        var user = Guid.NewGuid();
        var key = TasteStore.LibraryKey(Guid.NewGuid());

        store.Update(user, key, a => a with { Thumb = -1 });
        store.Update(user, key, a => a with { Asked = true });

        var answer = store.GetAnswer(user, key);
        Assert.Equal(-1, answer.Thumb);
        Assert.True(answer.Asked);
        Assert.False(answer.NotInterested);
    }

    [Theory]
    [InlineData("tmdb:tv:1438", "tv", "1438")]
    [InlineData("tmdb:movie:603", "movie", "603")]
    [InlineData("tmdb:person:1", null, null)]
    [InlineData("tmdb:tv:", null, null)]
    [InlineData("item:0123456789abcdef0123456789abcdef", null, null)]
    public void TmdbKeysParseBack(string key, string? mediaType, string? tmdbId)
    {
        Assert.Equal((mediaType, tmdbId), TasteStore.ParseTmdbKey(key));
    }

    [Fact]
    public void LibraryKeysParseBack()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, TasteStore.ParseLibraryKey(TasteStore.LibraryKey(id)));
        Assert.Null(TasteStore.ParseLibraryKey("tmdb:tv:1438"));
        Assert.Null(TasteStore.ParseLibraryKey("item:nope"));
    }

    [Fact]
    public void NotInterestedCanBeUndone()
    {
        var store = new TasteStore(_path);
        var user = Guid.NewGuid();
        var key = TasteStore.TmdbKey("tv", "1438");

        store.Update(user, key, a => a with { NotInterested = true, Title = "The Wire", MediaType = "tv", TmdbId = "1438" });
        Assert.True(store.GetAnswer(user, key).NotInterested);

        store.Update(user, key, a => a with { NotInterested = false });
        var answer = store.GetAnswer(user, key);
        Assert.False(answer.NotInterested);
        Assert.Equal("The Wire", answer.Title);
        Assert.Equal("1438", answer.TmdbId);
    }

    [Fact]
    public void CalibrationShownIsRemembered()
    {
        var store = new TasteStore(_path);
        var user = Guid.NewGuid();

        store.MarkCalibrationShown(user, "movie:603", "yes");
        store.MarkCalibrationShown(user, "tv:1438", "no");
        store.MarkCalibrationShown(user, "tv:1438", "skip");

        var shown = store.GetCalibrationShown(user);
        Assert.Equal(2, shown.Count);
        Assert.Equal("skip", shown["tv:1438"]);
    }

    [Fact]
    public void CacheHonoursMaxAge()
    {
        var store = new TasteStore(_path);

        store.PutCached("k", "{\"a\":1}");

        Assert.Equal("{\"a\":1}", store.GetCached("k", TimeSpan.FromHours(1)));
        Assert.Null(store.GetCached("k", TimeSpan.Zero));
        Assert.Null(store.GetCached("missing", TimeSpan.FromHours(1)));
    }

    [Fact]
    public void PruneDropsOnlyOlderEntries()
    {
        var store = new TasteStore(_path);
        store.PutCached("a", "1");
        store.PutCached("b", "2");

        Assert.Equal(0, store.PruneCache(DateTime.UtcNow.AddHours(-1)));
        Assert.Equal("1", store.GetCached("a", TimeSpan.FromHours(1)));

        Assert.Equal(2, store.PruneCache(DateTime.UtcNow.AddMinutes(1)));
        Assert.Null(store.GetCached("a", TimeSpan.FromHours(1)));
    }

    [Fact]
    public void KeysAreStable()
    {
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

        Assert.Equal("item:0f8fad5bd9cb469fa16570867728950e", TasteStore.LibraryKey(id));
        Assert.Equal("tmdb:tv:1399", TasteStore.TmdbKey("tv", "1399"));
    }
}
