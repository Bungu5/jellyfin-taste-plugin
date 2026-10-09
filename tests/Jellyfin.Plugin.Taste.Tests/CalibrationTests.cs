using Jellyfin.Plugin.Taste.Services;

namespace Jellyfin.Plugin.Taste.Tests;

public class CalibrationTests
{
    [Fact]
    public void SpreadByGenrePrefersOnePerGenreThenFills()
    {
        var items = new List<(string Name, string Genre)>
        {
            ("a", "drama"),
            ("b", "drama"),
            ("c", "comedy"),
            ("d", "horror"),
        };

        var picked = CalibrationService.SpreadByGenre(items, i => i.Genre, 4);

        Assert.Equal(["a", "c", "d", "b"], picked.Select(p => p.Name));
        Assert.Equal(2, CalibrationService.SpreadByGenre(items, i => i.Genre, 2).Count);
    }

    [Fact]
    public void FixedDeckIsEmbeddedAndWellFormed()
    {
        var deck = CalibrationService.FixedDeck;

        Assert.True(deck.Count >= 60);
        Assert.All(deck, entry =>
        {
            Assert.Contains(entry.MediaType, new[] { "movie", "tv" });
            Assert.True(int.TryParse(entry.TmdbId, out _));
            Assert.False(string.IsNullOrWhiteSpace(entry.Title));
            Assert.NotEmpty(entry.Genres);
        });
        Assert.Equal(deck.Count, deck.Select(e => e.MediaType + ":" + e.TmdbId).Distinct().Count());
    }
}
