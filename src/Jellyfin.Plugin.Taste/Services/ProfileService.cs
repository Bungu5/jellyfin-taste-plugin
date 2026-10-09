using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Taste.Data;
using Jellyfin.Plugin.Taste.Scoring;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Taste.Services;

/// <summary>
/// One thing a person told us about a title, directly or by watching it.
/// </summary>
public sealed class Signal
{
    public required string TitleKey { get; init; }

    public required string Title { get; init; }

    public Guid? LibraryId { get; init; }

    public string? MediaType { get; init; }

    public string? TmdbId { get; init; }

    public int? Year { get; init; }

    /// <summary>
    /// Gets the 0–10 score: Jellyfin's rating for library titles, stars × 2 for outside titles.
    /// </summary>
    public double? Rating { get; init; }

    public int? Thumb { get; init; }

    public bool Heart { get; init; }

    public bool Finished { get; init; }

    public bool Requested { get; init; }

    public string? FirstGenre { get; init; }

    public DateTime? When { get; init; }

    /// <summary>
    /// Gets the signed weight after decay.
    /// </summary>
    public double Weight { get; init; }

    public required IReadOnlyDictionary<string, double> Vector { get; init; }

    public bool IsExplicit => Rating.HasValue || Thumb.HasValue;
}

public sealed class UserProfile
{
    private double? _length;

    public required Guid UserId { get; init; }

    public required IReadOnlyList<Signal> Signals { get; init; }

    public required IReadOnlyDictionary<string, double> Vector { get; init; }

    /// <summary>
    /// Gets how many separate liked and disliked titles back each ingredient in <see cref="Vector"/>.
    /// </summary>
    public IReadOnlyDictionary<string, FeatureSupport> Support { get; init; } = new Dictionary<string, FeatureSupport>();

    public double Length => _length ??= TasteMath.Length(Vector);

    /// <summary>
    /// Gets library titles they played, started, or scored.
    /// </summary>
    public required IReadOnlySet<Guid> SeenLibrary { get; init; }

    /// <summary>
    /// Gets <c>movie:{tmdb}</c> / <c>tv:{tmdb}</c> keys they have seen anywhere.
    /// </summary>
    public required IReadOnlySet<string> SeenTmdb { get; init; }

    /// <summary>
    /// Gets title keys marked not interested or thumbed down.
    /// </summary>
    public required IReadOnlySet<string> Excluded { get; init; }

    public required DateTime BuiltUtc { get; init; }

    public bool HasTaste => Signals.Any(s => s.Weight > 0 && s.Vector.Count > 0);
}

/// <summary>
/// Builds and caches each person's taste profile.
/// </summary>
public sealed class ProfileService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly LibraryIndex _index;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly TasteStore _store;
    private readonly TmdbClient _tmdb;
    private readonly SeerrClient _seerr;
    private readonly ILogger<ProfileService> _logger;
    private readonly ConcurrentDictionary<Guid, (UserProfile Profile, long Generation)> _cache = new();
    private readonly ConcurrentDictionary<Guid, long> _generations = new();

    public ProfileService(
        LibraryIndex index,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        TasteStore store,
        TmdbClient tmdb,
        SeerrClient seerr,
        ILogger<ProfileService> logger)
    {
        _index = index;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _store = store;
        _tmdb = tmdb;
        _seerr = seerr;
        _logger = logger;
    }

    /// <summary>
    /// Drops the cached profile. A build already running when this is called is not cached, since it may have
    /// read the old answers.
    /// </summary>
    public void Invalidate(Guid userId)
    {
        _generations.AddOrUpdate(userId, 1, (_, generation) => generation + 1);
        _cache.TryRemove(userId, out _);
    }

    public async Task<UserProfile> GetAsync(User user, CancellationToken cancellationToken)
    {
        var generation = _generations.GetValueOrDefault(user.Id);
        if (_cache.TryGetValue(user.Id, out var cached)
            && cached.Generation == generation
            && DateTime.UtcNow - cached.Profile.BuiltUtc < CacheFor)
        {
            return cached.Profile;
        }

        var profile = await BuildAsync(user, cancellationToken).ConfigureAwait(false);
        _cache[user.Id] = (profile, generation);
        return profile;
    }

    private async Task<UserProfile> BuildAsync(User user, CancellationToken cancellationToken)
    {
        var snapshot = await _index.GetAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var answers = _store.GetAnswers(user.Id);
        var signals = new List<Signal>();
        var seenLibrary = new HashSet<Guid>();
        var seenTmdb = new HashSet<string>(StringComparer.Ordinal);
        var excluded = new HashSet<string>(StringComparer.Ordinal);

        var titleData = _userDataManager.GetUserDataBatch(snapshot.Titles.Values.Select(t => t.Item).ToList(), user);
        var progress = SeriesProgress(user);

        foreach (var title in snapshot.Titles.Values)
        {
            var key = TasteStore.LibraryKey(title.Id);
            titleData.TryGetValue(title.Id, out var data);
            answers.TryGetValue(key, out var answer);

            var rating = data?.Rating;
            var thumb = answer?.Thumb;
            var heart = data?.IsFavorite ?? false;
            var lastPlayed = data?.LastPlayedDate;
            bool finished, started;

            if (title.IsSeries)
            {
                progress.TryGetValue(title.Id, out var p);
                started = p.Started > 0;
                finished = title.EpisodeCount > 0 && p.Played >= Math.Max(1, (int)Math.Ceiling(0.8 * title.EpisodeCount));
                lastPlayed = Max(lastPlayed, p.LastPlayed);
            }
            else
            {
                finished = data?.Played ?? false;
                started = finished || (data?.PlaybackPositionTicks ?? 0) > 0;
            }

            if (answer is { NotInterested: true } || thumb < 0)
            {
                excluded.Add(key);
            }

            if (started || finished || rating.HasValue || thumb.HasValue)
            {
                seenLibrary.Add(title.Id);
                if (title.TmdbId is not null)
                {
                    seenTmdb.Add(title.MediaType + ":" + title.TmdbId);
                }
            }

            var isExplicit = rating.HasValue || thumb.HasValue;
            var weight = 0.0;
            if (rating.HasValue)
            {
                weight += TasteMath.StarWeight(rating.Value);
            }

            if (thumb.HasValue)
            {
                weight += TasteMath.ThumbWeight(thumb.Value);
            }

            if (!isExplicit)
            {
                weight = heart ? TasteMath.HeartWeight
                    : answer is { NotInterested: true } ? TasteMath.NotInterestedWeight
                    : finished ? TasteMath.FinishedWeight
                    : 0;
            }

            if (weight == 0)
            {
                continue;
            }

            var when = (isExplicit || answer is { NotInterested: true }) && answer is not null ? Max(answer.UpdatedUtc, lastPlayed) : lastPlayed;
            signals.Add(new Signal
            {
                TitleKey = key,
                Title = title.Name,
                LibraryId = title.Id,
                MediaType = title.MediaType,
                TmdbId = title.TmdbId,
                Year = title.Year,
                Rating = rating,
                Thumb = thumb,
                Heart = heart,
                Finished = finished,
                FirstGenre = title.Item.Genres?.FirstOrDefault(),
                When = when,
                Weight = weight * TasteMath.Decay(when, now, isExplicit ? TasteMath.ExplicitDecayFloor : TasteMath.ImplicitDecayFloor),
                Vector = title.Vector,
            });
        }

        foreach (var answer in answers.Values.Where(a => a.TitleKey.StartsWith("tmdb:", StringComparison.Ordinal)))
        {
            if (answer.NotInterested || answer.Thumb < 0)
            {
                excluded.Add(answer.TitleKey);
            }

            var (keyType, keyId) = TasteStore.ParseTmdbKey(answer.TitleKey);
            var mediaType = answer.MediaType ?? keyType;
            var tmdbId = answer.TmdbId ?? keyId;
            if (mediaType is null || tmdbId is null)
            {
                continue;
            }

            var isExplicit = answer.Stars.HasValue || answer.Thumb.HasValue;
            if (!isExplicit && !answer.NotInterested)
            {
                continue;
            }

            if (isExplicit)
            {
                seenTmdb.Add(mediaType + ":" + tmdbId);
            }

            var owned = snapshot.FindByTmdb(mediaType, tmdbId);
            if (owned is not null)
            {
                var index = signals.FindIndex(s => s.LibraryId == owned.Id);
                if (index >= 0 && signals[index].IsExplicit)
                {
                    continue;
                }

                if (index >= 0)
                {
                    signals.RemoveAt(index);
                }
            }

            var vector = await OutsideVectorAsync(snapshot, mediaType, tmdbId, cancellationToken).ConfigureAwait(false);
            var weight = isExplicit
                ? (answer.Stars.HasValue ? TasteMath.StarWeight(answer.Stars.Value * 2) : 0)
                  + (answer.Thumb.HasValue ? TasteMath.ThumbWeight(answer.Thumb.Value) : 0)
                : TasteMath.NotInterestedWeight;
            signals.Add(new Signal
            {
                TitleKey = answer.TitleKey,
                Title = answer.Title ?? answer.TitleKey,
                MediaType = mediaType,
                TmdbId = tmdbId,
                Year = answer.Year,
                Rating = answer.Stars * 2,
                Thumb = answer.Thumb,
                When = answer.UpdatedUtc,
                Weight = weight * TasteMath.Decay(answer.UpdatedUtc, now, isExplicit ? TasteMath.ExplicitDecayFloor : TasteMath.ImplicitDecayFloor),
                Vector = vector,
            });
        }

        foreach (var (deckKey, response) in _store.GetCalibrationShown(user.Id))
        {
            if (response == "yes")
            {
                seenTmdb.Add(deckKey);
            }
        }

        await AddRequestSignalsAsync(user, snapshot, signals, seenLibrary, now, cancellationToken).ConfigureAwait(false);

        var (userVector, support) = TasteMath.Profile(signals.Select(s => (Identity(s), s.Weight, s.Vector)));

        return new UserProfile
        {
            UserId = user.Id,
            Signals = signals,
            Vector = userVector,
            Support = support,
            SeenLibrary = seenLibrary,
            SeenTmdb = seenTmdb,
            Excluded = excluded,
            BuiltUtc = DateTime.UtcNow,
        };
    }

    private async Task AddRequestSignalsAsync(
        User user,
        LibrarySnapshot snapshot,
        List<Signal> signals,
        HashSet<Guid> seenLibrary,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (!_seerr.IsConfigured)
        {
            return;
        }

        try
        {
            var seerrUserId = await _seerr.FindUserIdAsync(user, cancellationToken).ConfigureAwait(false);
            if (seerrUserId is null)
            {
                return;
            }

            var withSignal = signals.Where(s => s.LibraryId.HasValue).Select(s => s.LibraryId!.Value).ToHashSet();
            foreach (var request in await _seerr.GetRequestsAsync(seerrUserId.Value, cancellationToken).ConfigureAwait(false))
            {
                var title = snapshot.FindByTmdb(request.MediaType, request.TmdbId);
                if (title is null || seenLibrary.Contains(title.Id) || withSignal.Contains(title.Id))
                {
                    continue;
                }

                signals.Add(new Signal
                {
                    TitleKey = TasteStore.LibraryKey(title.Id),
                    Title = title.Name,
                    LibraryId = title.Id,
                    MediaType = title.MediaType,
                    TmdbId = title.TmdbId,
                    Year = title.Year,
                    Requested = true,
                    FirstGenre = title.Item.Genres?.FirstOrDefault(),
                    When = request.CreatedUtc,
                    Weight = TasteMath.RequestedWeight * TasteMath.Decay(request.CreatedUtc, now, 0),
                    Vector = title.Vector,
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Skipping Seerr request signals");
        }
    }

    private async Task<IReadOnlyDictionary<string, double>> OutsideVectorAsync(
        LibrarySnapshot snapshot,
        string mediaType,
        string tmdbId,
        CancellationToken cancellationToken)
    {
        var owned = snapshot.FindByTmdb(mediaType, tmdbId);
        if (owned is not null)
        {
            return owned.Vector;
        }

        var details = await _tmdb.GetTitleAsync(mediaType, tmdbId, cancellationToken).ConfigureAwait(false);
        return details is null
            ? new Dictionary<string, double>()
            : snapshot.Vectorize(Features.Build(details.Metadata));
    }

    private Dictionary<Guid, (int Played, int Started, DateTime? LastPlayed)> SeriesProgress(User user)
    {
        var played = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IsPlayed = true,
            IsVirtualItem = false,
        });
        var resumable = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IsResumable = true,
            IsVirtualItem = false,
        });

        var all = played.Concat(resumable).OfType<Episode>().Where(e => (e.ParentIndexNumber ?? 1) > 0).ToList();
        var data = _userDataManager.GetUserDataBatch(all.Cast<BaseItem>().ToList(), user);
        var playedIds = played.Select(e => e.Id).ToHashSet();

        return all
            .GroupBy(e => e.SeriesId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var distinct = g.DistinctBy(e => e.Id).ToList();
                    return (
                        Played: distinct.Count(e => playedIds.Contains(e.Id)),
                        Started: distinct.Count,
                        LastPlayed: distinct
                            .Select(e => data.TryGetValue(e.Id, out var d) ? d.LastPlayedDate : null)
                            .Max());
                });
    }

    private static string Identity(Signal signal)
        => signal.TmdbId is not null ? signal.MediaType + ":" + signal.TmdbId : signal.TitleKey;

    private static DateTime? Max(DateTime? a, DateTime? b)
        => a is null ? b : b is null ? a : (a > b ? a : b);
}
