using System.Text.Json;
using Jellyfin.Plugin.Taste.Data;
using Jellyfin.Plugin.Taste.Services;

namespace Jellyfin.Plugin.Taste.Tests;

public sealed class TransferTests
{
    private static Answer Empty() => new() { UserId = "u", TitleKey = TasteStore.TmdbKey("movie", "603") };

    [Theory]
    [InlineData(10.0, 5)]
    [InlineData(8.0, 4)]
    [InlineData(7.0, 4)]
    [InlineData(2.0, 1)]
    [InlineData(0.5, 1)]
    public void RatingsBecomeStars(double rating, int stars)
    {
        Assert.Equal(stars, TransferService.ToStars(rating));
    }

    [Fact]
    public void NoRatingMeansNoStars()
    {
        Assert.Null(TransferService.ToStars(null));
        Assert.Null(TransferService.ToStars(0));
    }

    [Fact]
    public void MergeFillsGapsOnly()
    {
        var existing = Empty() with { Stars = 2, Thumb = -1 };
        var incoming = new ExportedTitle { MediaType = "movie", Tmdb = "603", Rating = 10, Thumb = 1, NotInterested = true };

        var merged = TransferService.Merge(existing, incoming, false);

        Assert.Equal(2, merged.Stars);
        Assert.Equal(-1, merged.Thumb);
        Assert.True(merged.NotInterested);
        Assert.True(merged.Asked);
    }

    [Fact]
    public void MergeTakesIncomingWhenNothingIsKnown()
    {
        var incoming = new ExportedTitle { MediaType = "movie", Tmdb = "603", Rating = 8, Thumb = 1 };

        var merged = TransferService.Merge(Empty(), incoming, false);

        Assert.Equal(4, merged.Stars);
        Assert.Equal(1, merged.Thumb);
        Assert.False(merged.NotInterested);
    }

    [Fact]
    public void LibraryStarsStayInJellyfin()
    {
        var incoming = new ExportedTitle { MediaType = "tv", Tmdb = "1399", Rating = 10 };

        var merged = TransferService.Merge(Empty(), incoming, true);

        Assert.Null(merged.Stars);
        Assert.True(merged.Asked);
    }

    [Fact]
    public void MergeOfNothingChangesNothing()
    {
        var existing = Empty() with { Thumb = 1, Asked = true };
        var incoming = new ExportedTitle { MediaType = "movie", Tmdb = "603" };

        Assert.Equal(existing, TransferService.Merge(existing, incoming, true));
    }

    [Fact]
    public void CleanDropsWhatThePluginWouldNeverWrite()
    {
        var cleaned = TransferService.Clean(new ExportedTitle
        {
            MediaType = "movie",
            Tmdb = " 603 ",
            Imdb = "",
            Rating = 42,
            Thumb = 7,
        });

        Assert.NotNull(cleaned);
        Assert.Equal("603", cleaned!.Tmdb);
        Assert.Null(cleaned.Imdb);
        Assert.Null(cleaned.Rating);
        Assert.Null(cleaned.Thumb);
    }

    [Fact]
    public void CleanRejectsUnknownTypesAndUnidentifiedTitles()
    {
        Assert.Null(TransferService.Clean(new ExportedTitle { MediaType = "book", Tmdb = "1" }));
        Assert.Null(TransferService.Clean(new ExportedTitle { MediaType = "movie", Title = "No ids" }));
        Assert.NotNull(TransferService.Clean(new ExportedTitle { MediaType = "tv", Tvdb = "121361" }));
    }

    [Fact]
    public void ExportFilesRoundTripThroughJson()
    {
        var export = new TasteExport
        {
            ExportedUtc = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc),
            Titles = [new ExportedTitle { MediaType = "movie", Title = "Arrival", Year = 2016, Tmdb = "329865", Imdb = "tt2543164", Rating = 10, Thumb = 1, Asked = true }],
            Calibration = [new ExportedCalibration("movie:603", "yes")],
        };

        var back = JsonSerializer.Deserialize<TasteExport>(JsonSerializer.Serialize(export));

        Assert.NotNull(back);
        Assert.Equal(TasteExport.FormatName, back!.Format);
        Assert.Equal(export.Titles[0], back.Titles[0]);
        Assert.Equal(export.Calibration[0], back.Calibration[0]);
    }
}
