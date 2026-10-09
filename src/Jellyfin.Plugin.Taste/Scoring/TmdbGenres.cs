namespace Jellyfin.Plugin.Taste.Scoring;

/// <summary>
/// TMDB's genre ids. They are stable, so they are listed here instead of fetched,
/// which keeps discovery working the same through TMDB or through Seerr.
/// </summary>
public static class TmdbGenres
{
    private static readonly Dictionary<int, string> Movie = new()
    {
        [28] = "Action",
        [12] = "Adventure",
        [16] = "Animation",
        [35] = "Comedy",
        [80] = "Crime",
        [99] = "Documentary",
        [18] = "Drama",
        [10751] = "Family",
        [14] = "Fantasy",
        [36] = "History",
        [27] = "Horror",
        [10402] = "Music",
        [9648] = "Mystery",
        [10749] = "Romance",
        [878] = "Science Fiction",
        [10770] = "TV Movie",
        [53] = "Thriller",
        [10752] = "War",
        [37] = "Western",
    };

    private static readonly Dictionary<int, string> Tv = new()
    {
        [10759] = "Action & Adventure",
        [16] = "Animation",
        [35] = "Comedy",
        [80] = "Crime",
        [99] = "Documentary",
        [18] = "Drama",
        [10751] = "Family",
        [10762] = "Kids",
        [9648] = "Mystery",
        [10763] = "News",
        [10764] = "Reality",
        [10765] = "Sci-Fi & Fantasy",
        [10766] = "Soap",
        [10767] = "Talk",
        [10768] = "War & Politics",
        [37] = "Western",
    };

    /// <summary>
    /// TV genres that are almost never what a "you might like this show" row is for, unless the person likes them.
    /// </summary>
    public static readonly IReadOnlySet<int> TvNiche = new HashSet<int> { 10763, 10764, 10766, 10767 };

    /// <summary>
    /// Gets the profile ingredient names (for example <c>science fiction</c>) for a TMDB genre id.
    /// </summary>
    public static IEnumerable<string> Names(string mediaType, int id)
    {
        var map = mediaType == "tv" ? Tv : Movie;
        return map.TryGetValue(id, out var name) ? Features.NormalizeGenre(name) : [];
    }

    /// <summary>
    /// Gets the TMDB genre ids that carry a profile ingredient such as <c>science fiction</c>.
    /// </summary>
    public static IReadOnlyList<int> Ids(string mediaType, string ingredient)
    {
        var map = mediaType == "tv" ? Tv : Movie;
        return map.Where(p => Features.NormalizeGenre(p.Value).Contains(ingredient, StringComparer.Ordinal))
            .Select(p => p.Key)
            .ToList();
    }
}
