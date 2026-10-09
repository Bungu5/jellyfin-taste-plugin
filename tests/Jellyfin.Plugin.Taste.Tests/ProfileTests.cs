using Jellyfin.Plugin.Taste.Api;
using Jellyfin.Plugin.Taste.Scoring;
using Jellyfin.Plugin.Taste.Services;

namespace Jellyfin.Plugin.Taste.Tests;

public class ProfileTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A library of about a thousand titles: genres, language, and decade are common; names and themes are rare.
    /// </summary>
    private static readonly Dictionary<string, double> Idf = new()
    {
        ["g:drama"] = 1.25,
        ["g:comedy"] = 1.5,
        ["g:horror"] = 2.4,
        ["g:thriller"] = 2.0,
        ["g:romance"] = 2.2,
        ["l:en"] = 0.8,
        ["y:2010s"] = 1.45,
        ["y:2020s"] = 1.6,
    };

    [Fact]
    public void GenresOutweighRareNamesInATitle()
    {
        var vector = Vector(Title(["Horror", "Drama"], ["a1", "a2", "a3", "a4", "a5"], "dir", ["haunted house", "grief"]));

        var horror = vector["g:horror"];
        Assert.All(vector.Where(p => Features.Category(p.Key) == Features.Actor), p => Assert.True(p.Value < horror / 2));
        Assert.True(horror > vector["d:dir"]);
        Assert.True(vector["g:drama"] > vector["l:en"]);
    }

    [Fact]
    public void CommonLanguagesCountLessThanRareOnes()
    {
        var english = Vector(new TitleMetadata { Genres = ["Drama"], OriginalLanguage = "en" });
        var korean = Vector(new TitleMetadata { Genres = ["Drama"], OriginalLanguage = "ko" });

        Assert.True(korean["l:ko"] > english["l:en"]);
    }

    [Fact]
    public void JapaneseAnimationIsTaggedAnime()
    {
        var anime = Features.Build(new TitleMetadata { Genres = ["Animation"], OriginalLanguage = "ja" });
        var cartoon = Features.Build(new TitleMetadata { Genres = ["Animation"], OriginalLanguage = "en" });

        Assert.Contains("g:anime", anime.Keys);
        Assert.DoesNotContain("g:anime", cartoon.Keys);
    }

    [Fact]
    public void OneDislikedTitleLandsOnWhatItIsNotWhoIsInIt()
    {
        var profile = Profile(
            (2, Title(["Drama"], ["b1", "b2"], "liked director", ["family"])),
            (-2.5, Title(["Horror", "Thriller"], ["x1", "x2", "x3", "x4", "x5"], "disliked director", ["haunted house", "grief"])));

        Assert.True(profile.Vector["g:horror"] < 0);
        Assert.True(profile.Vector["k:haunted house"] < 0);
        Assert.DoesNotContain(profile.Vector, p => p.Value < 0 && !Features.IsSubject(Features.Category(p.Key)));

        var summary = TasteController.Summarize(profile);
        Assert.Equal(["horror", "thriller"], summary.Dislikes.Select(d => d.Label).Order());
        Assert.All(summary.Dislikes, d => Assert.Equal("genre", d.Category));
    }

    [Fact]
    public void APersonInTwoSeparateDislikesCountsWeakly()
    {
        var profile = Profile(
            (2, Title(["Drama"], ["b1"], "liked director", [])),
            (-2.5, Title(["Horror"], ["x1", "shared actor"], "d1", ["slasher"])),
            (-2.5, Title(["Thriller"], ["x2", "shared actor"], "d2", ["slasher"])));

        var actor = profile.Vector["a:shared actor"];
        Assert.True(actor < 0);
        Assert.True(Math.Abs(actor) < Math.Abs(profile.Vector["g:horror"]));
        Assert.DoesNotContain("a:x1", profile.Vector.Keys);

        var dislikes = TasteController.Summarize(profile).Dislikes;
        Assert.Contains(dislikes, d => d is { Label: "slasher", Category: "theme" });
        var people = dislikes.Select((d, i) => (d, i)).Where(p => p.d.Category == "cast").ToList();
        Assert.Single(people);
        Assert.True(people[0].i > dislikes.ToList().FindLastIndex(d => d.Category is "genre" or "theme"));
    }

    [Fact]
    public void TheSameTitleRatedTwiceIsOneDislike()
    {
        var title = Vector(Title(["Horror"], ["x1"], "d1", []));
        var (vector, support) = TasteMath.Profile([("movie:1", -2.5, title), ("movie:1", -0.6, title)]);

        Assert.DoesNotContain("a:x1", vector.Keys);
        Assert.Equal(1, support["g:horror"].Disliked);
    }

    [Fact]
    public void OneLovedTitleDoesNotPutItsCastInTheLikes()
    {
        var single = Profile((5, Title(["Drama"], ["star"], "auteur", ["courtroom"])));
        var repeated = Profile(
            (5, Title(["Drama"], ["star"], "auteur", ["courtroom"])),
            (3, Title(["Comedy"], ["star"], "other", ["courtroom"])));

        var likes = TasteController.Summarize(single).Likes;
        Assert.Contains(likes, l => l.Label == "drama");
        Assert.DoesNotContain(likes, l => l.Category is "cast" or "director" or "theme");

        var repeatedLikes = TasteController.Summarize(repeated).Likes;
        Assert.Contains(repeatedLikes, l => l is { Label: "star", Category: "cast" });
        Assert.Contains(repeatedLikes, l => l is { Label: "courtroom", Category: "theme" });
    }

    [Fact]
    public void ADislikedTitleDoesNotBuryTitlesThatShareOneActor()
    {
        var profile = Profile(
            (2, Title(["Drama"], ["b1"], "liked director", [])),
            (-2.5, Title(["Horror"], ["shared actor"], "d1", [])));
        var withActor = Vector(Title(["Drama"], ["shared actor"], "someone", []));
        var withoutActor = Vector(Title(["Drama"], ["somebody else"], "someone", []));

        var a = RecommendationService.Score(profile, withActor, 7f, null, null, Now);
        var b = RecommendationService.Score(profile, withoutActor, 7f, null, null, Now);

        Assert.True(a > 0);
        Assert.Equal(b, a, 9);
    }

    [Fact]
    public void DislikedGenresPushTitlesDown()
    {
        var profile = Profile(
            (2, Title(["Drama"], ["b1"], "liked director", [])),
            (-2.5, Title(["Horror"], ["x1"], "d1", [])));
        var drama = Vector(Title(["Drama"], ["c1"], "c", []));
        var horrorDrama = Vector(Title(["Drama", "Horror"], ["c1"], "c", []));

        Assert.True(
            RecommendationService.Score(profile, drama, 7f, null, null, Now)
            > RecommendationService.Score(profile, horrorDrama, 7f, null, null, Now));
    }

    [Fact]
    public void OnlyDislikesGiveNoTaste()
    {
        var profile = Profile((-2.5, Title(["Horror"], ["x1"], "d1", [])));

        Assert.False(profile.HasTaste);
        Assert.Empty(TasteController.Summarize(profile).Likes);
        Assert.Contains(TasteController.Summarize(profile).Dislikes, d => d.Label == "horror");
    }

    [Fact]
    public void DiscoveryVersionIgnoresFadingButNotNewAnswers()
    {
        var vector = Vector(Title(["Drama"], ["b1"], "d", []));
        UserProfile Make(double weight, int? thumb) => Profile([new Signal { TitleKey = "item:1", Title = "One", Weight = weight, Thumb = thumb, Vector = vector }]);

        Assert.Equal(DiscoveryService.Version(Make(2.0, 1)), DiscoveryService.Version(Make(1.7, 1)));
        Assert.NotEqual(DiscoveryService.Version(Make(2.0, 1)), DiscoveryService.Version(Make(-2.5, -1)));
    }

    [Fact]
    public void NegativeOverlapLowersTheDiscoveryBlend()
    {
        Assert.True(
            DiscoveryService.Blend(0.5, -0.5, 0.6, 3000, "movie", null, 0, false, Now)
            < DiscoveryService.Blend(0.5, 0, 0.6, 3000, "movie", null, 0, false, Now));
    }

    private static TitleMetadata Title(string[] genres, string[] actors, string director, string[] keywords)
        => new()
        {
            Genres = genres,
            Actors = actors,
            Directors = [director],
            Keywords = keywords,
            OriginalLanguage = "en",
            Year = 2018,
        };

    private static Dictionary<string, double> Vector(TitleMetadata metadata)
        => TasteMath.Weighted(Features.Build(metadata), Idf, 6.9);

    private static UserProfile Profile(params (double Weight, TitleMetadata Title)[] signals)
        => Profile(signals.Select((s, i) => new Signal { TitleKey = "item:" + i, Title = "T" + i, Weight = s.Weight, Vector = Vector(s.Title) }).ToList());

    private static UserProfile Profile(IReadOnlyList<Signal> signals)
    {
        var (vector, support) = TasteMath.Profile(signals.Select(s => (s.TitleKey, s.Weight, s.Vector)));
        return new UserProfile
        {
            UserId = Guid.NewGuid(),
            Signals = signals,
            Vector = vector,
            Support = support,
            SeenLibrary = new HashSet<Guid>(),
            SeenTmdb = new HashSet<string>(),
            Excluded = new HashSet<string>(),
            BuiltUtc = Now,
        };
    }
}
