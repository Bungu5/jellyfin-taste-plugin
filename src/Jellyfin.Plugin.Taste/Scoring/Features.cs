using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Taste.Scoring;

/// <summary>
/// Turns title metadata into weighted ingredient keys such as <c>g:science fiction</c> or <c>d:denis villeneuve</c>.
/// Jellyfin's library metadata and TMDB details go through the same normalizer, so the keys line up.
/// </summary>
public static class Features
{
    public const string Genre = "g";
    public const string Keyword = "k";
    public const string Actor = "a";
    public const string Director = "d";
    public const string Writer = "w";
    public const string Studio = "s";
    public const string Language = "l";
    public const string Decade = "y";

    private const int MaxKeywords = 12;
    private const int MaxActors = 5;
    private const int MaxWriters = 3;
    private const int MaxStudios = 3;
    private const int MaxRememberedNames = 200_000;

    private static readonly ConcurrentDictionary<string, string> OriginalNames = new(StringComparer.Ordinal);

    /// <summary>
    /// Each category's share of a title's vector. IDF only ranks values inside a category, so five rare
    /// actor names cannot outweigh the genres just by being rare.
    /// </summary>
    private static readonly Dictionary<string, double> CategoryWeights = new(StringComparer.Ordinal)
    {
        [Genre] = 1.0,
        [Keyword] = 0.7,
        [Actor] = 0.45,
        [Director] = 0.5,
        [Writer] = 0.25,
        [Studio] = 0.25,
        [Language] = 0.4,
        [Decade] = 0.3,
    };

    private static readonly Dictionary<string, string[]> GenreSplits = new(StringComparer.Ordinal)
    {
        ["sci-fi & fantasy"] = ["science fiction", "fantasy"],
        ["action & adventure"] = ["action", "adventure"],
        ["war & politics"] = ["war", "politics"],
        ["sci-fi"] = ["science fiction"],
        ["science-fiction"] = ["science fiction"],
        ["kids"] = ["family"],
    };

    private static readonly Dictionary<string, string> CategoryNouns = new(StringComparer.Ordinal)
    {
        [Genre] = "genre",
        [Keyword] = "themes",
        [Actor] = "cast",
        [Director] = "director",
        [Writer] = "writer",
        [Studio] = "studio",
        [Language] = "language",
        [Decade] = "era",
    };

    public static string Category(string key) => key.Length > 1 && key[1] == ':' ? key[..1] : string.Empty;

    public static string Label(string key) => key.Length > 2 ? key[2..] : key;

    public static string Noun(string category) => CategoryNouns.GetValueOrDefault(category, "style");

    public static double CategoryWeight(string category) => CategoryWeights.GetValueOrDefault(category, 1.0);

    /// <summary>
    /// Gets a value indicating whether the category says what a title is (genre, themes) rather than who made it
    /// or when and where.
    /// </summary>
    public static bool IsSubject(string category) => category is Genre or Keyword;

    public static IEnumerable<string> NormalizeGenre(string genre)
    {
        var normalized = Normalize(genre);
        return GenreSplits.TryGetValue(normalized, out var split) ? split : [normalized];
    }

    public static string Normalize(string value) => value.Trim().ToLowerInvariant();

    /// <summary>
    /// Builds the raw (pre-IDF) ingredient weights for one title.
    /// </summary>
    public static Dictionary<string, double> Build(TitleMetadata metadata)
    {
        var raw = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var genre in metadata.Genres.SelectMany(NormalizeGenre))
        {
            Add(raw, Genre, genre);
        }

        if (raw.ContainsKey(Genre + ":animation") && Normalize(metadata.OriginalLanguage ?? string.Empty) == "ja")
        {
            Add(raw, Genre, "anime");
        }

        foreach (var keyword in metadata.Keywords.Take(MaxKeywords))
        {
            Add(raw, Keyword, Normalize(keyword));
        }

        foreach (var actor in metadata.Actors.Take(MaxActors))
        {
            AddName(raw, Actor, actor);
        }

        foreach (var director in metadata.Directors)
        {
            AddName(raw, Director, director);
        }

        foreach (var writer in metadata.Writers.Take(MaxWriters))
        {
            AddName(raw, Writer, writer);
        }

        foreach (var studio in metadata.Studios.Take(MaxStudios))
        {
            AddName(raw, Studio, studio);
        }

        if (!string.IsNullOrWhiteSpace(metadata.OriginalLanguage))
        {
            Add(raw, Language, Normalize(metadata.OriginalLanguage));
        }

        if (metadata.Year is > 1800)
        {
            Add(raw, Decade, (metadata.Year.Value / 10 * 10) + "s");
        }

        return raw;
    }

    /// <summary>
    /// Gets the name as it was first spelled in metadata (for example "Leonardo DiCaprio"), if it has been seen.
    /// </summary>
    public static string? OriginalName(string key) => OriginalNames.TryGetValue(key, out var name) ? name : null;

    private static void AddName(Dictionary<string, double> raw, string category, string name)
    {
        var label = Normalize(name);
        Add(raw, category, label);
        if (label.Length > 0 && OriginalNames.Count < MaxRememberedNames)
        {
            OriginalNames.TryAdd(category + ":" + label, name.Trim());
        }
    }

    private static void Add(Dictionary<string, double> raw, string category, string label)
    {
        if (label.Length == 0)
        {
            return;
        }

        raw[category + ":" + label] = 1.0;
    }
}

/// <summary>
/// Metadata shared by library titles and TMDB titles.
/// </summary>
public sealed record TitleMetadata
{
    public IReadOnlyList<string> Genres { get; init; } = [];

    public IReadOnlyList<string> Keywords { get; init; } = [];

    public IReadOnlyList<string> Actors { get; init; } = [];

    public IReadOnlyList<string> Directors { get; init; } = [];

    public IReadOnlyList<string> Writers { get; init; } = [];

    public IReadOnlyList<string> Studios { get; init; } = [];

    public string? OriginalLanguage { get; init; }

    public int? Year { get; init; }
}
