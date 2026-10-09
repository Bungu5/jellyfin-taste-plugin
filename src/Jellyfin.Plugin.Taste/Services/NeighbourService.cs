using System.Runtime.CompilerServices;

namespace Jellyfin.Plugin.Taste.Services;

/// <summary>
/// A title that fans of the person's favourites also liked.
/// </summary>
/// <param name="Ref">The title.</param>
/// <param name="Support">Summed strength: each favourite that lists it adds up to its own relative weight, more near
/// the top of that favourite's list; lists from disliked titles subtract.</param>
/// <param name="Because">The favourites that list it, strongest first.</param>
public sealed record Neighbour(TmdbRef Ref, double Support, IReadOnlyList<string> Because);

/// <summary>
/// "People who liked X also liked Y": TMDB recommendations (built from what TMDB members rate and list together)
/// and, with a Trakt client ID, Trakt's related titles, gathered for the person's strongest favourites. This
/// collaborative signal is what plain genre and keyword overlap misses.
/// </summary>
public sealed class NeighbourService
{
    private const int LikedSeeds = 10;
    private const int DislikedSeeds = 4;
    private const int Concurrency = 8;
    private const double BothSourcesBonus = 0.25;
    private const double DislikedScale = 0.6;

    private readonly TmdbClient _tmdb;
    private readonly TraktClient _trakt;
    private readonly ConditionalWeakTable<UserProfile, Task<IReadOnlyDictionary<string, Neighbour>>> _cache = new();

    public NeighbourService(TmdbClient tmdb, TraktClient trakt)
    {
        _tmdb = tmdb;
        _trakt = trakt;
    }

    public bool IsAvailable => _tmdb.IsConfigured || _trakt.IsConfigured;

    /// <summary>
    /// Maps support (0 to a few) onto 0–1 with diminishing returns: one strong favourite ≈ 0.63, two ≈ 0.86.
    /// </summary>
    internal static double Strength(double support) => support <= 0 ? 0 : 1 - Math.Exp(-support);

    /// <summary>
    /// Gets neighbours keyed <c>movie:123</c> / <c>tv:456</c>. Seeds themselves are left out. Worked out once per
    /// profile build and shared by every row that asks.
    /// </summary>
    public Task<IReadOnlyDictionary<string, Neighbour>> GetAsync(UserProfile profile, CancellationToken cancellationToken)
    {
        var task = _cache.GetValue(profile, p => BuildAsync(p, CancellationToken.None));
        if (task.IsFaulted || task.IsCanceled)
        {
            _cache.Remove(profile);
            task = _cache.GetValue(profile, p => BuildAsync(p, CancellationToken.None));
        }

        return task.WaitAsync(cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, Neighbour>> BuildAsync(UserProfile profile, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, Neighbour>(StringComparer.Ordinal);
        if (!IsAvailable)
        {
            return result;
        }

        bool Usable(Signal s) => s.TmdbId is not null && s.MediaType is "movie" or "tv" && !s.Requested;
        var liked = profile.Signals.Where(s => s.Weight > 0 && Usable(s)).OrderByDescending(s => s.Weight).Take(LikedSeeds).ToList();
        var disliked = profile.Signals.Where(s => s.Weight < 0 && s.IsExplicit && Usable(s)).OrderBy(s => s.Weight).Take(DislikedSeeds).ToList();
        if (liked.Count == 0)
        {
            return result;
        }

        var scale = liked.Max(s => s.Weight);
        var seeds = liked.Select(s => (Signal: s, Strength: s.Weight / scale))
            .Concat(disliked.Select(s => (Signal: s, Strength: -DislikedScale * Math.Min(1, Math.Abs(s.Weight) / scale))))
            .ToList();

        using var throttle = new SemaphoreSlim(Concurrency);
        async Task<IReadOnlyList<TmdbRef>> Throttled(Func<Task<IReadOnlyList<TmdbRef>>> call)
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await call().ConfigureAwait(false);
            }
            finally
            {
                throttle.Release();
            }
        }

        var lists = await Task.WhenAll(seeds.Select(async seed =>
        {
            var type = seed.Signal.MediaType!;
            var id = seed.Signal.TmdbId!;
            var tmdb = _tmdb.IsConfigured
                ? Throttled(() => _tmdb.GetRecommendationsAsync(type, id, cancellationToken))
                : Task.FromResult<IReadOnlyList<TmdbRef>>([]);
            var trakt = _trakt.IsConfigured
                ? Throttled(() => _trakt.GetRelatedAsync(type, id, cancellationToken))
                : Task.FromResult<IReadOnlyList<TmdbRef>>([]);
            return (seed.Signal, seed.Strength, Tmdb: await tmdb.ConfigureAwait(false), Trakt: await trakt.ConfigureAwait(false));
        })).ConfigureAwait(false);

        var seedKeys = profile.Signals.Where(s => s.TmdbId is not null).Select(s => s.MediaType + ":" + s.TmdbId).ToHashSet(StringComparer.Ordinal);
        var support = new Dictionary<string, (TmdbRef Ref, double Support, List<(string Title, double Part)> Because)>(StringComparer.Ordinal);
        foreach (var (signal, strength, tmdbList, traktList) in lists)
        {
            foreach (var (key, part) in Contributions(tmdbList, traktList))
            {
                if (seedKeys.Contains(key))
                {
                    continue;
                }

                var hit = tmdbList.Concat(traktList).First(r => r.MediaType + ":" + r.TmdbId == key);
                var entry = support.TryGetValue(key, out var existing) ? existing : (hit, 0, new List<(string, double)>());
                entry.Support += strength * part;
                if (strength > 0)
                {
                    entry.Because.Add((signal.Title, strength * part));
                }

                support[key] = entry;
            }
        }

        foreach (var (key, entry) in support)
        {
            result[key] = new Neighbour(
                entry.Ref,
                entry.Support,
                entry.Because.OrderByDescending(b => b.Part).Select(b => b.Title).Distinct().Take(3).ToList());
        }

        return result;
    }

    /// <summary>
    /// How much each title in one favourite's lists counts: 1 at the top of a list, falling to 0.5 at the bottom,
    /// plus a bonus when TMDB and Trakt both list it.
    /// </summary>
    internal static Dictionary<string, double> Contributions(IReadOnlyList<TmdbRef> tmdb, IReadOnlyList<TmdbRef> trakt)
    {
        static Dictionary<string, double> Ranked(IReadOnlyList<TmdbRef> list)
        {
            var ranked = new Dictionary<string, double>(StringComparer.Ordinal);
            for (var i = 0; i < list.Count; i++)
            {
                ranked.TryAdd(list[i].MediaType + ":" + list[i].TmdbId, 1 - (0.5 * i / Math.Max(1, list.Count)));
            }

            return ranked;
        }

        var fromTmdb = Ranked(tmdb);
        var fromTrakt = Ranked(trakt);
        var parts = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var key in fromTmdb.Keys.Union(fromTrakt.Keys))
        {
            var a = fromTmdb.GetValueOrDefault(key);
            var b = fromTrakt.GetValueOrDefault(key);
            parts[key] = Math.Max(a, b) + (a > 0 && b > 0 ? BothSourcesBonus : 0);
        }

        return parts;
    }
}
