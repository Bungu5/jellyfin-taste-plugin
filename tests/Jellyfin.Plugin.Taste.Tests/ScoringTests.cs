using Jellyfin.Plugin.Taste.Scoring;

namespace Jellyfin.Plugin.Taste.Tests;

public class ScoringTests
{
    [Theory]
    [InlineData(10, 3.0)]
    [InlineData(8, 1.5)]
    [InlineData(6, 0.2)]
    [InlineData(4, -1.5)]
    [InlineData(2, -3.0)]
    public void StarsMapToSignedWeights(double rating, double expected)
    {
        Assert.Equal(expected, TasteMath.StarWeight(rating));
    }

    [Fact]
    public void ThumbDownOutweighsThumbUp()
    {
        Assert.True(Math.Abs(TasteMath.ThumbWeight(-1)) > TasteMath.ThumbWeight(1));
    }

    [Fact]
    public void DecayHalvesAfterOneHalfLife()
    {
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(0.5, TasteMath.Decay(now.AddDays(-TasteMath.HalfLifeDays), now, 0), 3);
        Assert.Equal(1.0, TasteMath.Decay(null, now, 0));
        Assert.Equal(TasteMath.ExplicitDecayFloor, TasteMath.Decay(now.AddYears(-5), now, TasteMath.ExplicitDecayFloor));
    }

    [Fact]
    public void CosineOfIdenticalVectorsIsOne()
    {
        var v = new Dictionary<string, double> { ["g:drama"] = 1, ["d:someone"] = 2 };

        Assert.Equal(1.0, TasteMath.Cosine(v, v), 6);
        Assert.Equal(0.0, TasteMath.Cosine(v, new Dictionary<string, double> { ["g:comedy"] = 1 }));
        Assert.Equal(0.0, TasteMath.Cosine(v, new Dictionary<string, double>()));
    }

    [Fact]
    public void WeightedAppliesIdfAndNormalizes()
    {
        var raw = new Dictionary<string, double> { ["g:drama"] = 1, ["d:rare"] = 1 };
        var idf = new Dictionary<string, double> { ["g:drama"] = 0.1, ["d:rare"] = 3 };

        var vector = TasteMath.Weighted(raw, idf, 1);

        Assert.Equal(1.0, Math.Sqrt(vector.Values.Sum(x => x * x)), 6);
        Assert.True(vector["d:rare"] > vector["g:drama"]);
    }

    [Fact]
    public void TopSharedIgnoresNegativeOverlap()
    {
        var profile = new Dictionary<string, double> { ["g:horror"] = -1, ["d:a"] = 0.8, ["g:drama"] = 0.3 };
        var title = new Dictionary<string, double> { ["g:horror"] = 1, ["d:a"] = 0.5, ["g:drama"] = 0.5 };

        var shared = TasteMath.TopShared(profile, title, 5);

        Assert.Equal(["d:a", "g:drama"], shared.Select(s => s.Key));
    }

    [Fact]
    public void DiversifyAvoidsNearDuplicates()
    {
        var sameDirector = new Dictionary<string, double> { ["d:x"] = 1 };
        var different = new Dictionary<string, double> { ["d:y"] = 1 };
        var ranked = new List<(string Name, double Score, Dictionary<string, double> Vector)>
        {
            ("first", 1.0, sameDirector),
            ("sequel", 0.95, sameDirector),
            ("other", 0.8, different),
        };

        var picked = TasteMath.Diversify(ranked, r => r.Score, r => r.Vector, 2);

        Assert.Equal(["first", "other"], picked.Select(p => p.Name));
    }

    [Fact]
    public void FeaturesSplitCombinedGenresAndBucketDecades()
    {
        var raw = Features.Build(new TitleMetadata
        {
            Genres = ["Sci-Fi & Fantasy", "Drama"],
            Directors = [" Denis Villeneuve "],
            Actors = ["a", "b", "c", "d", "e", "f", "g"],
            OriginalLanguage = "EN",
            Year = 2016,
        });

        Assert.Contains("g:science fiction", raw.Keys);
        Assert.Contains("g:fantasy", raw.Keys);
        Assert.Contains("g:drama", raw.Keys);
        Assert.Contains("d:denis villeneuve", raw.Keys);
        Assert.Contains("l:en", raw.Keys);
        Assert.Contains("y:2010s", raw.Keys);
        Assert.Equal(5, raw.Keys.Count(k => Features.Category(k) == Features.Actor));
    }

    [Fact]
    public void PeopleKeepTheirOriginalSpellingForDisplay()
    {
        Features.Build(new TitleMetadata { Actors = ["Leonardo DiCaprio"] });

        Assert.Equal("Leonardo DiCaprio", Services.ReasonWriter.Display("a:leonardo dicaprio"));
        Assert.Equal("Someone Unseen", Services.ReasonWriter.Display("a:someone unseen"));
    }

    [Fact]
    public void FeatureKeyHelpers()
    {
        Assert.Equal("d", Features.Category("d:someone"));
        Assert.Equal("someone", Features.Label("d:someone"));
        Assert.Equal("director", Features.Noun("d"));
        Assert.Equal(string.Empty, Features.Category("nokey"));
    }
}
