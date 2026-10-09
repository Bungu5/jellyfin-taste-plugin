using System.Text.Json;
using Jellyfin.Plugin.Taste.Scoring;
using Jellyfin.Plugin.Taste.Services;

namespace Jellyfin.Plugin.Taste.Tests;

public class DiscoveryTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FewVotesStayNearThePrior()
    {
        var handful = DiscoveryService.BayesianRating(9.5, 4, "tv");
        var hundreds = DiscoveryService.BayesianRating(9.5, 800, "tv");

        Assert.InRange(handful, 6.5, 7.0);
        Assert.InRange(hundreds, 9.3, 9.5);
    }

    [Fact]
    public void MoviesNeedMoreVotesThanShowsToMoveTheRating()
    {
        Assert.True(DiscoveryService.BayesianRating(9, 50, "tv") > DiscoveryService.BayesianRating(9, 50, "movie"));
    }

    [Fact]
    public void FansAlsoLikedOutweighsGenreOverlapAlone()
    {
        var likedByFans = DiscoveryService.Blend(NeighbourService.Strength(1.2), 0.4, 0.6, 3000, "tv", null, 0, false, Now);
        var genreMatch = DiscoveryService.Blend(0, 1.0, 0.6, 3000, "tv", null, 0, false, Now);

        Assert.True(likedByFans > genreMatch);
    }

    [Fact]
    public void ANewWellRatedSmallShowBeatsAnOldPopularOneWithTheSameFit()
    {
        var smallNew = DiscoveryService.Blend(0.5, 0.6, 0.7, 120, "tv", Now.AddDays(-20), 1, true, Now);
        var bigOld = DiscoveryService.Blend(0.5, 0.6, 0.7, 5000, "tv", Now.AddDays(-140), 1, true, Now);

        Assert.True(smallNew > bigOld);
    }

    [Fact]
    public void FreshnessOnlyCountsInTheNewRow()
    {
        var released = Now.AddDays(-3);

        Assert.True(
            DiscoveryService.Blend(0.3, 0.5, 0.5, 500, "movie", released, 0, true, Now)
            > DiscoveryService.Blend(0.3, 0.5, 0.5, 500, "movie", released, 0, false, Now));
    }

    [Theory]
    [InlineData("tv", true, 50, 7.5, 70.0, 400, true)]
    [InlineData("tv", true, 50, 7.5, 55.0, 400, false)]
    [InlineData("tv", true, 5, 7.5, 80.0, 400, false)]
    [InlineData("movie", false, 900, 7.5, 80.0, 900, false)]
    [InlineData("movie", false, 900, 7.5, 80.0, 40000, true)]
    public void QualityGateUsesMdbListWhenItKnowsTheTitle(string type, bool fresh, int tmdbVotes, double bayes, double score, int imdbVotes, bool expected)
    {
        var mdb = new MdbScore(score, score / 10, imdbVotes, null);

        Assert.Equal(expected, DiscoveryService.PassesQuality(type, fresh, tmdbVotes, bayes, mdb));
    }

    [Fact]
    public void QualityGateFallsBackToTheTmdbRating()
    {
        Assert.True(DiscoveryService.PassesQuality("tv", true, 40, 6.9, null));
        Assert.False(DiscoveryService.PassesQuality("tv", true, 40, 6.4, null));
    }

    [Fact]
    public void NeighbourContributionsRewardTopSpotsAndAgreement()
    {
        var tmdb = new[] { Hit("tv", [18], "en") with { TmdbId = "1" }, Hit("tv", [18], "en") with { TmdbId = "2" } };
        var trakt = new[] { Hit("tv", [18], "en") with { TmdbId = "2" } };

        var parts = NeighbourService.Contributions(tmdb, trakt);

        Assert.Equal(1.0, parts["tv:1"], 6);
        Assert.Equal(1.25, parts["tv:2"], 6);
        Assert.InRange(NeighbourService.Strength(1), 0.6, 0.65);
        Assert.Equal(0, NeighbourService.Strength(-1));
    }

    [Fact]
    public void LanguagesKeepOnlyRealShares()
    {
        var profile = Profile(
            (10, new() { ["g:drama"] = 1, ["l:en"] = 1 }),
            (1, new() { ["g:drama"] = 1, ["l:ko"] = 1 }));

        Assert.Equal(["en"], DiscoveryService.Languages(DiscoveryService.Shape(profile)));
    }

    [Fact]
    public void ParsesTraktChartsAndRelatedLists()
    {
        using var chart = JsonDocument.Parse("""
            [{"watchers":40,"show":{"title":"Slow Horses","year":2022,"ids":{"trakt":1,"tmdb":95480},
              "first_aired":"2022-04-01T00:00:00.000Z","genres":["drama","thriller"],"language":"en","rating":8.1,"votes":3000}},
             {"watchers":3,"show":{"title":"No tmdb","ids":{"trakt":2}}}]
            """);
        using var related = JsonDocument.Parse("""
            [{"title":"Frieren","year":2023,"ids":{"trakt":3,"tmdb":209867},"genres":["anime","fantasy"],"language":"ja"}]
            """);

        var show = Assert.Single(TraktClient.Parse("tv", chart.RootElement));
        var anime = Assert.Single(TraktClient.Parse("tv", related.RootElement));

        Assert.Equal("95480", show.TmdbId);
        Assert.Equal(2022, show.Date!.Value.Year);
        Assert.Contains(18, show.GenreIds!);
        Assert.Equal(3000, show.VoteCount);
        Assert.Contains(16, anime.GenreIds!);
        Assert.Equal("ja", anime.OriginalLanguage);
    }

    [Fact]
    public void TraktListsSkipEntriesOfTheWrongShape()
    {
        using var odd = JsonDocument.Parse("""
            [42, "text", {"show": {"ids": 7}},
             {"title":"Dark","ids":{"tmdb":70523},"genres":["drama", 5]}]
            """);

        var hit = Assert.Single(TraktClient.Parse("tv", odd.RootElement));

        Assert.Equal("70523", hit.TmdbId);
        Assert.Contains(18, hit.GenreIds!);
    }

    [Fact]
    public void ParsesMdbListScores()
    {
        using var doc = JsonDocument.Parse("""
            [{"title":"Severance","ids":{"imdb":"tt11280740","tmdb":95396},"score":88,"score_average":86,
              "ratings":[{"source":"imdb","value":8.7,"score":87,"votes":250000},
                         {"source":"tomatoes","value":97,"score":97,"votes":120},
                         {"source":"metacritic","value":83,"score":83}]},
             {"title":"Unknown","tmdb_id":"42","score":null,"ratings":[]}]
            """);

        var scores = MdbListClient.ParseScores(doc.RootElement);

        var severance = scores["95396"];
        Assert.Equal(86, severance.Score);
        Assert.Equal(8.7, severance.ImdbRating);
        Assert.Equal(250000, severance.ImdbVotes);
        Assert.Equal(97, severance.Critics);
        Assert.Null(scores["42"].Quality);
    }

    [Fact]
    public void ParsesMdbListCatalog()
    {
        using var doc = JsonDocument.Parse("""
            {"shows":[{"title":"The Pitt","year":2025,"released":"2025-01-09","ids":{"tmdb":250307},"score":85,
                       "language":"en","genres":[{"id":1,"title":"Drama"}]}],
             "pagination":{"next_cursor":null}}
            """);

        var hit = Assert.Single(MdbListClient.ParseCatalog("tv", doc.RootElement));

        Assert.Equal("250307", hit.TmdbId);
        Assert.Equal(8.5, hit.VoteAverage);
        Assert.Contains(18, hit.GenreIds!);
    }

    [Fact]
    public void AnimeIsLeftOutForSomeoneWhoBarelyWatchesIt()
    {
        var profile = Profile(
            (10, new() { ["g:drama"] = 1, ["l:en"] = 1 }),
            (8, new() { ["g:crime"] = 1, ["l:en"] = 1 }),
            (1, new() { ["g:animation"] = 1, ["l:ja"] = 1 }));
        var shape = DiscoveryService.Shape(profile);

        Assert.False(DiscoveryService.Fits(shape, profile, Hit("tv", [16, 10765], "ja")));
        Assert.True(DiscoveryService.Fits(shape, profile, Hit("tv", [18, 80], "en")));
        Assert.True(shape.Languages.GetValueOrDefault("ja") < DiscoveryService.MinLanguageShare);
    }

    [Fact]
    public void AnimeStaysForSomeoneWhoWatchesALotOfIt()
    {
        var profile = Profile(
            (6, new() { ["g:drama"] = 1, ["l:en"] = 1 }),
            (5, new() { ["g:animation"] = 1, ["l:ja"] = 1 }),
            (4, new() { ["g:animation"] = 1, ["g:action"] = 1, ["l:ja"] = 1 }));
        var shape = DiscoveryService.Shape(profile);

        Assert.True(DiscoveryService.Fits(shape, profile, Hit("tv", [16, 10759], "ja")));
        Assert.True(shape.Languages["ja"] >= DiscoveryService.MinLanguageShare);
    }

    [Fact]
    public void RealityAndTalkShowsNeedThatGenreInTheProfile()
    {
        var profile = Profile((5, new() { ["g:drama"] = 1 }));
        var shape = DiscoveryService.Shape(profile);

        Assert.False(DiscoveryService.Fits(shape, profile, Hit("tv", [10764], "en")));
    }

    [Fact]
    public void SavedRowsSurviveARoundTrip()
    {
        var rec = new Recommendation("tmdb:tv:1", "Show", 2026, "tv", null, "1", "https://img", "Plot", "Because", 0.4, "Series", "New · Sep 2026", null, "https://tmdb");
        var empty = new RecommendationRow([], "Nothing");
        var rows = new DiscoveryService.Rows(Now, "v1", false, empty, new RecommendationRow([rec], null), empty, empty);

        var back = JsonSerializer.Deserialize<DiscoveryService.Rows>(JsonSerializer.Serialize(rows))!;

        Assert.Equal("v1", back.Version);
        Assert.Equal(rec, Assert.Single(back.Get(true, "tv").Items));
        Assert.Equal("Nothing", back.Get(true, "movie").Notice);
        Assert.Equal("Nothing", back.Get(false, "tv").Notice);
    }

    [Fact]
    public void TmdbGenresMapToProfileIngredients()
    {
        Assert.Equal(["science fiction", "fantasy"], TmdbGenres.Names("tv", 10765));
        Assert.Equal(["science fiction"], TmdbGenres.Names("movie", 878));
        Assert.Contains(10765, TmdbGenres.Ids("tv", "science fiction"));
        Assert.Contains(878, TmdbGenres.Ids("movie", "science fiction"));
        Assert.Empty(TmdbGenres.Names("movie", 999999));
    }

    [Fact]
    public void ParsesSeerrDiscoverResults()
    {
        using var doc = JsonDocument.Parse("""
            {"page":1,"results":[
              {"id":1396,"mediaType":"tv","name":"Breaking Bad","firstAirDate":"2008-01-20","posterPath":"/bb.jpg",
               "voteAverage":8.9,"voteCount":15000,"popularity":300.5,"genreIds":[18,80],"originalLanguage":"en"}
            ]}
            """);

        var show = Assert.Single(TmdbClient.ParseSeerrResults("tv", doc.RootElement));
        Assert.Equal("1396", show.TmdbId);
        Assert.Equal("Breaking Bad", show.Title);
        Assert.Equal(2008, show.Year);
        Assert.Equal(15000, show.VoteCount);
        Assert.Equal([18, 80], show.GenreIds);
        Assert.Equal("en", show.OriginalLanguage);
    }

    [Fact]
    public void ParsesSeerrDetailsIntoIngredients()
    {
        using var doc = JsonDocument.Parse("""
            {"id":95396,"name":"Severance","firstAirDate":"2022-02-17","posterPath":"/sev.jpg","voteAverage":8.4,
             "voteCount":2100,"overview":"Work-life balance.","originalLanguage":"en",
             "genres":[{"id":18,"name":"Drama"},{"id":10765,"name":"Sci-Fi & Fantasy"}],
             "keywords":[{"id":1,"name":"dystopia"}],
             "createdBy":[{"name":"Dan Erickson"}],
             "networks":[{"name":"Apple TV+"}],
             "credits":{"cast":[{"name":"Britt Lower","order":1},{"name":"Adam Scott","order":0}],
                        "crew":[{"job":"Writer","name":"Dan Erickson"}]}}
            """);

        var title = TmdbClient.ParseSeerrTitle("tv", doc.RootElement);

        Assert.Equal("Severance", title.Title);
        Assert.Equal(2022, title.Year);
        Assert.Equal("Apple TV+", title.Network);
        Assert.Equal(["Adam Scott", "Britt Lower"], title.Metadata.Actors);
        Assert.Contains("Dan Erickson", title.Metadata.Directors);
        Assert.Equal(["dystopia"], title.Metadata.Keywords);
        Assert.Equal(["Apple TV+"], title.Metadata.Studios);
    }

    [Fact]
    public void ParsesTmdbDetailsWithShowKeywordsUnderResults()
    {
        using var doc = JsonDocument.Parse("""
            {"id":95396,"name":"Severance","first_air_date":"2022-02-17","vote_average":8.4,"vote_count":2100,
             "genres":[{"name":"Drama"}],"keywords":{"results":[{"name":"office"}]},
             "networks":[{"name":"Apple TV+"}],"created_by":[{"name":"Dan Erickson"}]}
            """);

        var title = TmdbClient.ParseTmdbTitle("tv", doc.RootElement);

        Assert.Equal(["office"], title.Metadata.Keywords);
        Assert.Equal(2100, title.VoteCount);
        Assert.Contains("Dan Erickson", title.Metadata.Directors);
    }

    private static UserProfile Profile(params (double Weight, Dictionary<string, double> Vector)[] signals)
    {
        var list = signals.Select((s, i) => new Signal { TitleKey = "t" + i, Title = "T" + i, Weight = s.Weight, Vector = s.Vector }).ToList();
        var vector = new Dictionary<string, double>();
        foreach (var signal in list)
        {
            foreach (var (key, value) in signal.Vector)
            {
                vector[key] = vector.GetValueOrDefault(key) + (value * signal.Weight);
            }
        }

        return new UserProfile
        {
            UserId = Guid.NewGuid(),
            Signals = list,
            Vector = vector,
            SeenLibrary = new HashSet<Guid>(),
            SeenTmdb = new HashSet<string>(),
            Excluded = new HashSet<string>(),
            BuiltUtc = DateTime.UtcNow,
        };
    }

    private static TmdbRef Hit(string mediaType, int[] genres, string language)
        => new(mediaType, "1", "Title", 2026, null, 8, 100, genres, language, Now);
}
