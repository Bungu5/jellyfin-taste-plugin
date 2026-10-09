using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Jellyfin.Plugin.Taste.Data;
using Jellyfin.Plugin.Taste.Scoring;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Taste.Services;

/// <summary>
/// A TMDB search, discover, or recommendation hit.
/// </summary>
public sealed record TmdbRef(
    string MediaType,
    string TmdbId,
    string Title,
    int? Year,
    string? PosterPath,
    double VoteAverage,
    int VoteCount = 0,
    IReadOnlyList<int>? GenreIds = null,
    string? OriginalLanguage = null,
    DateTime? Date = null,
    int? SeerrStatus = null);

/// <summary>
/// TMDB details with ingredients.
/// </summary>
public sealed record TmdbTitle(
    string MediaType,
    string TmdbId,
    string Title,
    int? Year,
    DateTime? ReleaseDate,
    string? PosterPath,
    double VoteAverage,
    string? Overview,
    TitleMetadata Metadata,
    int VoteCount = 0,
    string? Network = null);

/// <summary>
/// A TMDB discover query. Genre ids are OR-ed.
/// </summary>
public sealed record DiscoverQuery(string MediaType)
{
    public IReadOnlyCollection<int> Genres { get; init; } = [];

    public DateTime? ReleasedAfter { get; init; }

    public DateTime? ReleasedBefore { get; init; }

    public double? MinVoteAverage { get; init; }

    public int? MinVoteCount { get; init; }

    public string SortBy { get; init; } = "popularity.desc";
}

/// <summary>
/// Reads TMDB through the plugin's SQLite cache: directly when an admin set a TMDB key,
/// otherwise through Seerr's own TMDB proxy, so a Seerr connection is enough.
/// </summary>
public sealed class TmdbClient
{
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private static readonly TimeSpan DetailsMaxAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan ListMaxAge = TimeSpan.FromHours(12);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TasteStore _store;
    private readonly SeerrClient _seerr;
    private readonly ILogger<TmdbClient> _logger;

    public TmdbClient(IHttpClientFactory httpClientFactory, TasteStore store, SeerrClient seerr, ILogger<TmdbClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _store = store;
        _seerr = seerr;
        _logger = logger;
    }

    public bool IsConfigured => HasKey || _seerr.IsConfigured;

    /// <summary>
    /// Gets where title data comes from: TMDB, Seerr, or nothing.
    /// </summary>
    public string? Source => HasKey ? "TMDB" : _seerr.IsConfigured ? "Seerr" : null;

    private static bool HasKey => ApiKey.Length > 0;

    private static string ApiKey => Plugin.Instance?.Configuration.TmdbApiKey?.Trim() ?? string.Empty;

    public static string? PosterUrl(string? posterPath)
        => string.IsNullOrEmpty(posterPath) ? null : "https://image.tmdb.org/t/p/w342" + posterPath;

    public static string Link(string mediaType, string tmdbId) => $"https://www.themoviedb.org/{mediaType}/{tmdbId}";

    public async Task<TmdbTitle?> GetTitleAsync(string mediaType, string tmdbId, CancellationToken cancellationToken)
    {
        if (HasKey)
        {
            using var document = await GetTmdbAsync(
                $"/{mediaType}/{tmdbId}",
                new Dictionary<string, string> { ["append_to_response"] = "keywords,credits" },
                DetailsMaxAge,
                cancellationToken).ConfigureAwait(false);
            return document is null ? null : ParseTmdbTitle(mediaType, document.RootElement);
        }

        using var seerr = await GetSeerrAsync($"/api/v1/{mediaType}/{tmdbId}", null, DetailsMaxAge, cancellationToken)
            .ConfigureAwait(false);
        return seerr is null ? null : ParseSeerrTitle(mediaType, seerr.RootElement);
    }

    public Task<IReadOnlyList<TmdbRef>> GetRecommendationsAsync(string mediaType, string tmdbId, CancellationToken cancellationToken)
        => GetRelatedAsync(mediaType, tmdbId, "recommendations", cancellationToken);

    public async Task<IReadOnlyList<TmdbRef>> DiscoverAsync(DiscoverQuery query, CancellationToken cancellationToken)
    {
        var isMovie = query.MediaType == "movie";
        string Date(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (HasKey)
        {
            var dateField = isMovie ? "primary_release_date" : "first_air_date";
            var parameters = new Dictionary<string, string>
            {
                ["sort_by"] = query.SortBy,
                ["include_adult"] = "false",
            };
            AddCommon(parameters, query, "with_genres", "vote_average.gte", "vote_count.gte");
            if (query.ReleasedAfter.HasValue)
            {
                parameters[dateField + ".gte"] = Date(query.ReleasedAfter.Value);
            }

            if (query.ReleasedBefore.HasValue)
            {
                parameters[dateField + ".lte"] = Date(query.ReleasedBefore.Value);
            }

            using var document = await GetTmdbAsync($"/discover/{query.MediaType}", parameters, ListMaxAge, cancellationToken)
                .ConfigureAwait(false);
            return document is null ? [] : ParseTmdbResults(query.MediaType, document.RootElement);
        }

        var seerrParameters = new Dictionary<string, string>
        {
            ["sortBy"] = query.SortBy,
        };
        AddCommon(seerrParameters, query, "genre", "voteAverageGte", "voteCountGte");
        var seerrDate = isMovie ? "primaryReleaseDate" : "firstAirDate";
        if (query.ReleasedAfter.HasValue)
        {
            seerrParameters[seerrDate + "Gte"] = Date(query.ReleasedAfter.Value);
        }

        if (query.ReleasedBefore.HasValue)
        {
            seerrParameters[seerrDate + "Lte"] = Date(query.ReleasedBefore.Value);
        }

        using var seerr = await GetSeerrAsync(
            isMovie ? "/api/v1/discover/movies" : "/api/v1/discover/tv",
            seerrParameters,
            ListMaxAge,
            cancellationToken).ConfigureAwait(false);
        return seerr is null ? [] : ParseSeerrResults(query.MediaType, seerr.RootElement);
    }

    private static void AddCommon(
        Dictionary<string, string> parameters,
        DiscoverQuery query,
        string genres,
        string voteAverage,
        string voteCount)
    {
        if (query.Genres.Count > 0)
        {
            parameters[genres] = string.Join('|', query.Genres);
        }

        if (query.MinVoteAverage.HasValue)
        {
            parameters[voteAverage] = query.MinVoteAverage.Value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        if (query.MinVoteCount.HasValue)
        {
            parameters[voteCount] = query.MinVoteCount.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private async Task<IReadOnlyList<TmdbRef>> GetRelatedAsync(string mediaType, string tmdbId, string list, CancellationToken cancellationToken)
    {
        if (HasKey)
        {
            using var document = await GetTmdbAsync($"/{mediaType}/{tmdbId}/{list}", null, ListMaxAge, cancellationToken)
                .ConfigureAwait(false);
            return document is null ? [] : ParseTmdbResults(mediaType, document.RootElement);
        }

        using var seerr = await GetSeerrAsync($"/api/v1/{mediaType}/{tmdbId}/{list}", null, ListMaxAge, cancellationToken)
            .ConfigureAwait(false);
        return seerr is null ? [] : ParseSeerrResults(mediaType, seerr.RootElement);
    }

    internal static TmdbTitle ParseTmdbTitle(string mediaType, JsonElement root)
    {
        var isMovie = mediaType == "movie";
        var date = ParseDate(String(root, isMovie ? "release_date" : "first_air_date"));
        var credits = root.TryGetProperty("credits", out var c) ? c : default;
        var keywords = root.TryGetProperty("keywords", out var k)
            ? Names(k, isMovie ? "keywords" : "results")
            : [];
        var directors = Crew(credits, "Director");
        if (!isMovie)
        {
            directors.AddRange(Names(root, "created_by"));
        }

        var networks = isMovie ? [] : Names(root, "networks");
        return new TmdbTitle(
            mediaType,
            Id(root),
            String(root, isMovie ? "title" : "name") ?? string.Empty,
            date?.Year,
            date,
            String(root, "poster_path"),
            Number(root, "vote_average"),
            String(root, "overview"),
            new TitleMetadata
            {
                Genres = Names(root, "genres"),
                Keywords = keywords,
                Actors = Cast(credits),
                Directors = directors.Distinct().ToList(),
                Writers = Crew(credits, "Screenplay", "Writer").Distinct().ToList(),
                Studios = isMovie ? Names(root, "production_companies") : networks,
                OriginalLanguage = String(root, "original_language"),
                Year = date?.Year,
            },
            (int)Number(root, "vote_count"),
            networks.FirstOrDefault());
    }

    internal static TmdbTitle ParseSeerrTitle(string mediaType, JsonElement root)
    {
        var isMovie = mediaType == "movie";
        var date = ParseDate(String(root, isMovie ? "releaseDate" : "firstAirDate"));
        var credits = root.TryGetProperty("credits", out var c) ? c : default;
        var directors = Crew(credits, "Director");
        if (!isMovie)
        {
            directors.AddRange(Names(root, "createdBy"));
        }

        var networks = isMovie ? [] : Names(root, "networks");
        return new TmdbTitle(
            mediaType,
            Id(root),
            String(root, isMovie ? "title" : "name") ?? string.Empty,
            date?.Year,
            date,
            String(root, "posterPath"),
            Number(root, "voteAverage"),
            String(root, "overview"),
            new TitleMetadata
            {
                Genres = Names(root, "genres"),
                Keywords = Names(root, "keywords"),
                Actors = Cast(credits),
                Directors = directors.Distinct().ToList(),
                Writers = Crew(credits, "Screenplay", "Writer").Distinct().ToList(),
                Studios = isMovie ? Names(root, "productionCompanies") : networks,
                OriginalLanguage = String(root, "originalLanguage"),
                Year = date?.Year,
            },
            (int)Number(root, "voteCount"),
            networks.FirstOrDefault());
    }

    internal static List<TmdbRef> ParseTmdbResults(string mediaType, JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var isMovie = mediaType == "movie";
        return results.EnumerateArray()
            .Where(r => r.TryGetProperty("id", out _))
            .Select(r =>
            {
                var date = ParseDate(String(r, isMovie ? "release_date" : "first_air_date"));
                return new TmdbRef(
                    mediaType,
                    Id(r),
                    String(r, isMovie ? "title" : "name") ?? string.Empty,
                    date?.Year,
                    String(r, "poster_path"),
                    Number(r, "vote_average"),
                    (int)Number(r, "vote_count"),
                    Ints(r, "genre_ids"),
                    String(r, "original_language"),
                    date);
            })
            .ToList();
    }

    internal static List<TmdbRef> ParseSeerrResults(string mediaType, JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var isMovie = mediaType == "movie";
        return results.EnumerateArray()
            .Where(r => r.TryGetProperty("id", out _))
            .Select(r =>
            {
                var date = ParseDate(String(r, isMovie ? "releaseDate" : "firstAirDate"));
                return new TmdbRef(
                    mediaType,
                    Id(r),
                    String(r, isMovie ? "title" : "name") ?? string.Empty,
                    date?.Year,
                    String(r, "posterPath"),
                    Number(r, "voteAverage"),
                    (int)Number(r, "voteCount"),
                    Ints(r, "genreIds"),
                    String(r, "originalLanguage"),
                    date,
                    r.TryGetProperty("mediaInfo", out var info)
                    && info.ValueKind == JsonValueKind.Object
                    && info.TryGetProperty("status", out var status)
                    && status.ValueKind == JsonValueKind.Number
                        ? status.GetInt32()
                        : null);
            })
            .ToList();
    }

    private static List<string> Cast(JsonElement credits)
    {
        if (credits.ValueKind != JsonValueKind.Object || !credits.TryGetProperty("cast", out var cast) || cast.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return cast.EnumerateArray()
            .OrderBy(m => m.TryGetProperty("order", out var order) && order.ValueKind == JsonValueKind.Number ? order.GetInt32() : int.MaxValue)
            .Select(m => String(m, "name"))
            .OfType<string>()
            .ToList();
    }

    private static List<string> Crew(JsonElement credits, params string[] jobs)
    {
        if (credits.ValueKind != JsonValueKind.Object || !credits.TryGetProperty("crew", out var crew) || crew.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return crew.EnumerateArray()
            .Where(m => jobs.Contains(String(m, "job")))
            .Select(m => String(m, "name"))
            .OfType<string>()
            .ToList();
    }

    private static List<string> Names(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(property, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray().Select(e => String(e, "name")).OfType<string>().ToList();
    }

    private static List<int> Ints(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetInt32()).ToList();
    }

    private static string Id(JsonElement element) => element.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture);

    private static double Number(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private static string? String(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTime? ParseDate(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;

    private static string QueryString(IReadOnlyDictionary<string, string>? query)
        => query is null
            ? string.Empty
            : string.Join('&', query.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

    private async Task<JsonDocument?> GetSeerrAsync(
        string path,
        IReadOnlyDictionary<string, string>? query,
        TimeSpan maxAge,
        CancellationToken cancellationToken)
    {
        var queryString = QueryString(query);
        var pathAndQuery = queryString.Length > 0 ? path + "?" + queryString : path;
        var cacheKey = "seerr:" + pathAndQuery;
        var cached = _store.GetCached(cacheKey, maxAge);
        if (cached is not null)
        {
            return JsonDocument.Parse(cached);
        }

        var json = await _seerr.GetRawAsync(pathAndQuery, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        try
        {
            var document = JsonDocument.Parse(json);
            _store.PutCached(cacheKey, json);
            return document;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Seerr {Path} returned something that is not JSON", path);
            return null;
        }
    }

    private async Task<JsonDocument?> GetTmdbAsync(
        string path,
        IReadOnlyDictionary<string, string>? query,
        TimeSpan maxAge,
        CancellationToken cancellationToken)
    {
        var queryString = QueryString(query);
        var cacheKey = path + "?" + queryString;

        var cached = _store.GetCached(cacheKey, maxAge);
        if (cached is not null)
        {
            return JsonDocument.Parse(cached);
        }

        var key = ApiKey;
        var useBearer = key.StartsWith("eyJ", StringComparison.Ordinal);
        var url = BaseUrl + path + "?" + queryString;
        if (!useBearer)
        {
            url += (queryString.Length > 0 ? "&" : string.Empty) + "api_key=" + Uri.EscapeDataString(key);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (useBearer)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TMDB {Path} returned {Status}", path, (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var document = JsonDocument.Parse(json);
            _store.PutCached(cacheKey, json);
            return document;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "TMDB {Path} failed", path);
            return null;
        }
    }
}
