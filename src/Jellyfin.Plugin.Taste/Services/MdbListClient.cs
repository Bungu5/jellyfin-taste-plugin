using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Plugin.Taste.Data;
using Jellyfin.Plugin.Taste.Scoring;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Taste.Services;

/// <summary>
/// Scores from IMDb, TMDB, Trakt, Letterboxd, Rotten Tomatoes, and Metacritic, merged by MDBList.
/// </summary>
/// <param name="Score">MDBList's 0–100 score, or null when it has none.</param>
/// <param name="ImdbRating">IMDb 0–10 rating.</param>
/// <param name="ImdbVotes">IMDb vote count.</param>
/// <param name="Critics">Rotten Tomatoes critics or Metacritic, 0–100, when either exists.</param>
public sealed record MdbScore(double? Score, double? ImdbRating, int ImdbVotes, double? Critics)
{
    /// <summary>
    /// Gets a 0–100 quality figure: MDBList's score, else IMDb ×10.
    /// </summary>
    public double? Quality => Score ?? (ImdbRating.HasValue ? ImdbRating * 10 : null);
}

/// <summary>
/// Reads MDBList with a personal API key (free tier: 1,000 calls a day). Batch score lookups count as one call
/// each, and every title's scores are cached for three days, so a full rebuild costs a handful of calls.
/// </summary>
public sealed class MdbListClient
{
    private const string BaseUrl = "https://api.mdblist.com";
    private const int BatchSize = 100;
    private static readonly TimeSpan ScoreMaxAge = TimeSpan.FromDays(3);
    private static readonly TimeSpan CatalogMaxAge = TimeSpan.FromHours(12);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TasteStore _store;
    private readonly ILogger<MdbListClient> _logger;

    public MdbListClient(IHttpClientFactory httpClientFactory, TasteStore store, ILogger<MdbListClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _store = store;
        _logger = logger;
    }

    public bool IsConfigured => ApiKey.Length > 0;

    private static string ApiKey => Plugin.Instance?.Configuration.MdbListApiKey?.Trim() ?? string.Empty;

    /// <summary>
    /// Gets scores for TMDB titles, keyed <c>movie:123</c> / <c>tv:456</c>. Titles MDBList does not know are missing.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, MdbScore>> GetScoresAsync(
        IReadOnlyCollection<(string MediaType, string TmdbId)> titles,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, MdbScore>(StringComparer.Ordinal);
        if (!IsConfigured || titles.Count == 0)
        {
            return result;
        }

        var missing = new List<(string MediaType, string TmdbId)>();
        foreach (var title in titles.Distinct())
        {
            var cached = _store.GetCached(ScoreKey(title.MediaType, title.TmdbId), ScoreMaxAge);
            if (cached is null)
            {
                missing.Add(title);
            }
            else if (cached != "null")
            {
                result[title.MediaType + ":" + title.TmdbId] = JsonSerializer.Deserialize<MdbScore>(cached)!;
            }
        }

        foreach (var group in missing.GroupBy(t => t.MediaType))
        {
            foreach (var chunk in group.Chunk(BatchSize))
            {
                var ids = chunk.Select(t => int.TryParse(t.TmdbId, CultureInfo.InvariantCulture, out var id) ? id : 0).Where(id => id > 0).ToList();
                var found = await PostBatchAsync(group.Key, ids, cancellationToken).ConfigureAwait(false);
                if (found is null)
                {
                    continue;
                }

                foreach (var (tmdbId, mediaType) in chunk.Select(t => (t.TmdbId, t.MediaType)))
                {
                    var score = found.GetValueOrDefault(tmdbId);
                    _store.PutCached(ScoreKey(mediaType, tmdbId), score is null ? "null" : JsonSerializer.Serialize(score));
                    if (score is not null)
                    {
                        result[mediaType + ":" + tmdbId] = score;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Gets MDBList's catalog: titles released in a date range with at least a given score, best first.
    /// </summary>
    public async Task<IReadOnlyList<TmdbRef>> GetCatalogAsync(
        string mediaType,
        DateTime? releasedFrom,
        DateTime? releasedTo,
        int minScore,
        IReadOnlyCollection<string> languages,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return [];
        }

        string Date(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var query = new List<string>
        {
            "sort=score",
            "limit=100",
            "append_to_response=genres,ratings",
            "score_min=" + minScore.ToString(CultureInfo.InvariantCulture),
        };
        if (releasedFrom.HasValue)
        {
            query.Add("released_from=" + Date(releasedFrom.Value));
        }

        if (releasedTo.HasValue)
        {
            query.Add("released_to=" + Date(releasedTo.Value));
        }

        if (languages.Count > 0)
        {
            query.Add("language=" + Uri.EscapeDataString(string.Join(',', languages.Order(StringComparer.Ordinal))));
        }

        var path = $"/catalog/{(mediaType == "movie" ? "movie" : "show")}?" + string.Join('&', query);
        var json = _store.GetCached("mdb:" + path, CatalogMaxAge);
        if (json is null)
        {
            json = await SendAsync(new HttpRequestMessage(HttpMethod.Get, BaseUrl + path + "&apikey=" + Uri.EscapeDataString(ApiKey)), path, cancellationToken)
                .ConfigureAwait(false);
            if (json is null)
            {
                return [];
            }

            _store.PutCached("mdb:" + path, json);
        }

        using var document = JsonDocument.Parse(json);
        return ParseCatalog(mediaType, document.RootElement);
    }

    internal static Dictionary<string, MdbScore> ParseScores(JsonElement root)
    {
        var result = new Dictionary<string, MdbScore>(StringComparer.Ordinal);
        foreach (var item in Items(root))
        {
            if (TmdbId(item) is { } tmdbId)
            {
                result[tmdbId] = ParseScore(item);
            }
        }

        return result;
    }

    internal static MdbScore ParseScore(JsonElement item)
    {
        double? imdb = null, critics = null;
        var imdbVotes = 0;
        if (item.TryGetProperty("ratings", out var ratings) && ratings.ValueKind == JsonValueKind.Array)
        {
            foreach (var rating in ratings.EnumerateArray())
            {
                var source = Text(rating, "source");
                var value = Number(rating, "value");
                var score = Number(rating, "score");
                switch (source)
                {
                    case "imdb":
                        imdb = value;
                        imdbVotes = (int)(Number(rating, "votes") ?? 0);
                        break;
                    case "tomatoes" or "metacritic":
                        critics ??= score ?? value;
                        break;
                }
            }
        }

        var overall = Number(item, "score_average") ?? Number(item, "score");
        return new MdbScore(overall is > 0 ? overall : null, imdb is > 0 ? imdb : null, imdbVotes, critics is > 0 ? critics : null);
    }

    internal static List<TmdbRef> ParseCatalog(string mediaType, JsonElement root)
    {
        var hits = new List<TmdbRef>();
        foreach (var item in Items(root))
        {
            if (TmdbId(item) is not { } tmdbId)
            {
                continue;
            }

            var date = Date(Text(item, "released") ?? Text(item, "release_date") ?? Text(item, "first_air_date"));
            var genres = item.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array
                ? g.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : Text(e, "title") ?? Text(e, "name"))
                    .OfType<string>()
                    .SelectMany(name => TraktClient.GenreIds(mediaType, name.ToLowerInvariant().Replace(' ', '-')))
                    .Distinct()
                    .ToList()
                : [];
            var score = ParseScore(item);
            hits.Add(new TmdbRef(
                mediaType,
                tmdbId,
                Text(item, "title") ?? string.Empty,
                item.TryGetProperty("year", out var year) && year.ValueKind == JsonValueKind.Number ? year.GetInt32() : date?.Year,
                null,
                (score.Quality ?? 0) / 10,
                score.ImdbVotes,
                genres,
                Text(item, "language"),
                date));
        }

        return hits;
    }

    private static IEnumerable<JsonElement> Items(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().ToList();
        }

        var lists = new[] { "movies", "shows", "items", "results" }
            .Where(name => root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
            .SelectMany(name => root.GetProperty(name).EnumerateArray())
            .ToList();
        return lists.Count > 0 || root.ValueKind != JsonValueKind.Object ? lists : [root];
    }

    /// <summary>
    /// Finds the TMDB id, which MDBList puts in <c>ids.tmdb</c> (or <c>tmdb_id</c> / <c>tmdbid</c> on some routes).
    /// </summary>
    private static string? TmdbId(JsonElement item)
    {
        if (item.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Object && IdText(ids, "tmdb") is { } nested)
        {
            return nested;
        }

        return IdText(item, "tmdb_id") ?? IdText(item, "tmdbid") ?? IdText(item, "tmdb");
    }

    private static string? IdText(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt64().ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String when long.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var id) => id.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static string ScoreKey(string mediaType, string tmdbId) => $"mdb:score:{mediaType}:{tmdbId}";

    private static string? Text(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? Number(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static DateTime? Date(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;

    private async Task<Dictionary<string, MdbScore>?> PostBatchAsync(string mediaType, IReadOnlyList<int> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<string, MdbScore>(StringComparer.Ordinal);
        }

        var path = $"/tmdb/{(mediaType == "movie" ? "movie" : "show")}";
        var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path + "?apikey=" + Uri.EscapeDataString(ApiKey))
        {
            Content = JsonContent.Create(new { ids }),
        };
        var json = await SendAsync(request, path, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var scores = ParseScores(document.RootElement);
        if (scores.Count == 0 && ids.Count > 0)
        {
            _logger.LogWarning("MDBList {Path} returned no titles Taste could read: {Sample}", path, json.Length > 300 ? json[..300] : json);
        }

        return scores;
    }

    private async Task<string?> SendAsync(HttpRequestMessage request, string path, CancellationToken cancellationToken)
    {
        using (request)
        {
            try
            {
                var client = _httpClientFactory.CreateClient(NamedClient.Default);
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("MDBList {Path} returned {Status}", path, (int)response.StatusCode);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var check = JsonDocument.Parse(json);
                return json;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                _logger.LogWarning(ex, "MDBList {Path} failed", path);
                return null;
            }
        }
    }
}
