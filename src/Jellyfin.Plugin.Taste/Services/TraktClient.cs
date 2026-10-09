using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.Taste.Data;
using Jellyfin.Plugin.Taste.Scoring;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Taste.Services;

/// <summary>
/// Reads public Trakt lists with an app client ID (no user sign-in): "related" titles for a show or movie,
/// and trending or popular titles with filters such as year, language, and IMDb rating. Responses are cached.
/// </summary>
public sealed class TraktClient
{
    private const string BaseUrl = "https://api.trakt.tv";
    private static readonly TimeSpan ListMaxAge = TimeSpan.FromHours(12);
    private static readonly TimeSpan IdMaxAge = TimeSpan.FromDays(30);
    private static readonly string UserAgent = "Jellyfin-Plugin-Taste/" + typeof(TraktClient).Assembly.GetName().Version;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TasteStore _store;
    private readonly ILogger<TraktClient> _logger;

    public TraktClient(IHttpClientFactory httpClientFactory, TasteStore store, ILogger<TraktClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _store = store;
        _logger = logger;
    }

    public bool IsConfigured => ClientId.Length > 0;

    private static string ClientId => Plugin.Instance?.Configuration.TraktClientId?.Trim() ?? string.Empty;

    /// <summary>
    /// Gets titles Trakt relates to a TMDB title (based on what Trakt members watch and list together).
    /// </summary>
    public async Task<IReadOnlyList<TmdbRef>> GetRelatedAsync(string mediaType, string tmdbId, CancellationToken cancellationToken)
    {
        var traktType = TraktType(mediaType);
        using var search = await GetAsync($"/search/tmdb/{tmdbId}", $"type={traktType}", IdMaxAge, cancellationToken).ConfigureAwait(false);
        var traktId = search is null ? null : FirstTraktId(search.RootElement, traktType);
        if (traktId is null)
        {
            return [];
        }

        using var related = await GetAsync($"/{traktType}s/{traktId}/related", "extended=full&limit=20", ListMaxAge, cancellationToken)
            .ConfigureAwait(false);
        return related is null ? [] : Parse(mediaType, related.RootElement);
    }

    /// <summary>
    /// Gets a Trakt chart: <c>trending</c>, <c>popular</c>, or <c>anticipated</c>, with optional filters such as
    /// <c>years=2025-2026</c>, <c>languages=en,ko</c>, <c>imdb_ratings=7.0-10.0</c>, or <c>imdb_votes=300-50000</c>.
    /// </summary>
    public async Task<IReadOnlyList<TmdbRef>> GetChartAsync(
        string mediaType,
        string chart,
        IReadOnlyDictionary<string, string> filters,
        CancellationToken cancellationToken)
    {
        var query = string.Join(
            '&',
            new[] { "extended=full", "limit=60" }
                .Concat(filters.OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));
        using var document = await GetAsync($"/{TraktType(mediaType)}s/{chart}", query, ListMaxAge, cancellationToken).ConfigureAwait(false);
        return document is null ? [] : Parse(mediaType, document.RootElement);
    }

    /// <summary>
    /// Parses a Trakt list. Chart entries wrap the title (<c>{"watchers": 12, "show": {...}}</c>); related and
    /// popular lists are bare titles. Genres become TMDB genre ids so the same taste gates apply.
    /// </summary>
    internal static List<TmdbRef> Parse(string mediaType, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var traktType = TraktType(mediaType);
        var hits = new List<TmdbRef>();
        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var item = entry.TryGetProperty(traktType, out var wrapped) && wrapped.ValueKind == JsonValueKind.Object ? wrapped : entry;
            if (!item.TryGetProperty("ids", out var ids)
                || ids.ValueKind != JsonValueKind.Object
                || !ids.TryGetProperty("tmdb", out var tmdb)
                || tmdb.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var date = Date(Text(item, mediaType == "movie" ? "released" : "first_aired"));
            var genres = item.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array
                ? g.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString())
                    .OfType<string>()
                    .SelectMany(slug => GenreIds(mediaType, slug))
                    .Distinct()
                    .ToList()
                : [];
            hits.Add(new TmdbRef(
                mediaType,
                tmdb.GetInt32().ToString(CultureInfo.InvariantCulture),
                Text(item, "title") ?? string.Empty,
                item.TryGetProperty("year", out var year) && year.ValueKind == JsonValueKind.Number ? year.GetInt32() : date?.Year,
                null,
                Number(item, "rating"),
                (int)Number(item, "votes"),
                genres,
                Text(item, "language"),
                date));
        }

        return hits;
    }

    internal static IEnumerable<int> GenreIds(string mediaType, string slug)
    {
        if (slug == "anime")
        {
            return [16];
        }

        var tvNiche = mediaType == "tv"
            ? slug switch
            {
                "reality" => 10764,
                "talk-show" => 10767,
                "news" => 10763,
                "soap" => 10766,
                _ => 0,
            }
            : 0;
        if (tvNiche != 0)
        {
            return [tvNiche];
        }

        return Features.NormalizeGenre(slug).SelectMany(name => TmdbGenres.Ids(mediaType, name)).Distinct();
    }

    private static string TraktType(string mediaType) => mediaType == "movie" ? "movie" : "show";

    private static string? FirstTraktId(JsonElement root, string traktType)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty(traktType, out var item)
                && item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("ids", out var ids)
                && ids.ValueKind == JsonValueKind.Object
                && ids.TryGetProperty("trakt", out var id)
                && id.ValueKind == JsonValueKind.Number)
            {
                return id.GetInt32().ToString(CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static string? Text(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double Number(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    private static DateTime? Date(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;

    private async Task<JsonDocument?> GetAsync(string path, string query, TimeSpan maxAge, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var pathAndQuery = path + "?" + query;
        var cacheKey = "trakt:" + pathAndQuery;
        var cached = _store.GetCached(cacheKey, maxAge);
        if (cached is not null)
        {
            return JsonDocument.Parse(cached);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + pathAndQuery);
            request.Headers.Add("trakt-api-version", "2");
            request.Headers.Add("trakt-api-key", ClientId);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Trakt {Path} returned {Status}", path, (int)response.StatusCode);
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
            _logger.LogWarning(ex, "Trakt {Path} failed", path);
            return null;
        }
    }
}
