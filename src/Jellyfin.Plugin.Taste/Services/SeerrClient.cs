using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Taste.Services;

public sealed record SeerrRequest(string MediaType, string TmdbId, DateTime CreatedUtc);

public sealed record SeerrRequestResult(bool Ok, string Message);

/// <summary>
/// Talks to Seerr with the admin API key and <c>X-API-User</c>, so each request belongs to that person
/// and Seerr's own quotas and approval rules apply.
/// </summary>
public sealed class SeerrClient
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StatusCacheFor = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SeerrClient> _logger;
    private readonly Lock _cacheLock = new();
    private (DateTime Fetched, List<JsonElement> Users)? _users;
    private readonly Dictionary<int, (DateTime Fetched, IReadOnlyList<SeerrRequest> Requests)> _requests = new();
    private (DateTime Fetched, IReadOnlySet<string> Titles)? _requested;

    public SeerrClient(IHttpClientFactory httpClientFactory, ILogger<SeerrClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.SeerrUrl)
           && !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.SeerrApiKey);

    private static string BaseUrl => Plugin.Instance!.Configuration.SeerrUrl.Trim().TrimEnd('/');

    private static string ApiKey => Plugin.Instance!.Configuration.SeerrApiKey.Trim();

    /// <summary>
    /// Finds the Seerr user that signed in with this Jellyfin account.
    /// </summary>
    public async Task<int?> FindUserIdAsync(User user, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        List<JsonElement>? users;
        lock (_cacheLock)
        {
            users = _users is { } cached && DateTime.UtcNow - cached.Fetched < CacheFor ? cached.Users : null;
        }

        if (users is null)
        {
            using var document = await GetJsonAsync("/api/v1/user?take=1000&skip=0", cancellationToken).ConfigureAwait(false);
            if (document is null
                || document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            users = results.EnumerateArray().Where(e => Int(e, "id").HasValue).Select(e => e.Clone()).ToList();
            lock (_cacheLock)
            {
                _users = (DateTime.UtcNow, users);
            }
        }

        var jellyfinId = user.Id.ToString("N", CultureInfo.InvariantCulture);
        foreach (var candidate in users)
        {
            var linked = Text(candidate, "jellyfinUserId")?.Replace("-", string.Empty, StringComparison.Ordinal);
            if (string.Equals(linked, jellyfinId, StringComparison.OrdinalIgnoreCase))
            {
                return Int(candidate, "id");
            }
        }

        foreach (var candidate in users)
        {
            if (string.Equals(Text(candidate, "jellyfinUsername"), user.Username, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Text(candidate, "username"), user.Username, StringComparison.OrdinalIgnoreCase))
            {
                return Int(candidate, "id");
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<SeerrRequest>> GetRequestsAsync(int seerrUserId, CancellationToken cancellationToken)
    {
        lock (_cacheLock)
        {
            if (_requests.TryGetValue(seerrUserId, out var cached) && DateTime.UtcNow - cached.Fetched < CacheFor)
            {
                return cached.Requests;
            }
        }

        using var document = await GetJsonAsync(
                string.Create(CultureInfo.InvariantCulture, $"/api/v1/request?take=100&skip=0&filter=all&sort=added&requestedBy={seerrUserId}"),
                cancellationToken)
            .ConfigureAwait(false);
        var requests = new List<SeerrRequest>();
        if (document is not null
            && document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("results", out var results)
            && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var request in results.EnumerateArray())
            {
                if (request.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (request.TryGetProperty("requestedBy", out var by) && Int(by, "id") is { } byId && byId != seerrUserId)
                {
                    continue;
                }

                if (!request.TryGetProperty("media", out var media) || Int(media, "tmdbId") is not { } tmdbId)
                {
                    continue;
                }

                var created = DateTime.TryParse(
                    Text(request, "createdAt"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal,
                    out var parsed)
                    ? parsed
                    : DateTime.UtcNow;
                requests.Add(new SeerrRequest(
                    Text(media, "mediaType") ?? "movie",
                    tmdbId.ToString(CultureInfo.InvariantCulture),
                    created));
            }
        }

        lock (_cacheLock)
        {
            _requests[seerrUserId] = (DateTime.UtcNow, requests);
        }

        return requests;
    }

    public async Task<SeerrRequestResult> RequestAsync(int seerrUserId, string mediaType, int tmdbId, CancellationToken cancellationToken)
    {
        object body = mediaType == "tv"
            ? new
            {
                mediaType,
                mediaId = tmdbId,
                seasons = Plugin.Instance!.Configuration.SeerrRequestAllSeasons ? (object)"all" : new[] { 1 },
            }
            : new { mediaType, mediaId = tmdbId };

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var request = CreateRequest(HttpMethod.Post, "/api/v1/request", seerrUserId);
        request.Content = JsonContent.Create(body);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            lock (_cacheLock)
            {
                _requests.Remove(seerrUserId);
                _requested = null;
            }

            if (response.IsSuccessStatusCode)
            {
                return new SeerrRequestResult(true, "Requested in Seerr");
            }

            var message = TryMessage(text) ?? $"Seerr returned {(int)response.StatusCode}";
            return new SeerrRequestResult(false, message);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Seerr request failed");
            return new SeerrRequestResult(false, "Seerr is not reachable");
        }
    }

    /// <summary>
    /// Gets the titles Seerr is waiting on (pending approval or processing) as <c>movie:123</c> / <c>tv:456</c>.
    /// Two list calls, cached briefly, so a fresh request shows up quickly.
    /// </summary>
    public async Task<IReadOnlySet<string>> GetRequestedAsync(CancellationToken cancellationToken)
    {
        lock (_cacheLock)
        {
            if (_requested is { } cached && DateTime.UtcNow - cached.Fetched < StatusCacheFor)
            {
                return cached.Titles;
            }
        }

        var titles = new HashSet<string>(StringComparer.Ordinal);
        if (IsConfigured)
        {
            foreach (var filter in new[] { "pending", "processing" })
            {
                using var document = await GetJsonAsync($"/api/v1/media?take=1000&filter={filter}", cancellationToken).ConfigureAwait(false);
                if (document is null
                    || document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("results", out var results)
                    || results.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var media in results.EnumerateArray())
                {
                    if (Text(media, "mediaType") is { } type && Int(media, "tmdbId") is { } id)
                    {
                        titles.Add(type + ":" + id.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
        }

        lock (_cacheLock)
        {
            _requested = (DateTime.UtcNow, titles);
        }

        return titles;
    }

    /// <summary>
    /// Gets a Seerr API response body as text, or null on any failure.
    /// </summary>
    internal async Task<string?> GetRawAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var request = CreateRequest(HttpMethod.Get, pathAndQuery);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Seerr {Path} returned {Status}", pathAndQuery, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Seerr {Path} failed", pathAndQuery);
            return null;
        }
    }

    private static string? TryMessage(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return Text(document.RootElement, "message");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var number)
            ? number
            : null;

    private static HttpRequestMessage CreateRequest(HttpMethod method, string pathAndQuery, int? asUser = null)
    {
        var request = new HttpRequestMessage(method, BaseUrl + pathAndQuery);
        request.Headers.Add("X-Api-Key", ApiKey);
        if (asUser.HasValue)
        {
            request.Headers.Add("X-API-User", asUser.Value.ToString(CultureInfo.InvariantCulture));
        }

        return request;
    }

    private async Task<JsonDocument?> GetJsonAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        var json = await GetRawAsync(pathAndQuery, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Seerr {Path} returned something that is not JSON", pathAndQuery);
            return null;
        }
    }
}
