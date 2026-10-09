using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Taste.Data;
using Jellyfin.Plugin.Taste.Scoring;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Taste.Services;

/// <summary>
/// Finds titles that are not in the library: brand-new ones ("new") and older ones ("worth requesting"), with
/// separate rows for movies and shows.
/// </summary>
/// <remarks>
/// <para>Candidates: titles that fans of the person's favourites also liked (TMDB recommendations, Trakt related),
/// curated pools of good recent titles (Trakt trending/popular filtered by IMDb rating, MDBList's catalog by
/// score), and TMDB discover by genre as a fallback.</para>
/// <para>Each passes taste gates (anime, animation, languages, niche TV), then a quality gate (MDBList's merged
/// IMDb/RT/Metacritic score when available, else a vote-count-aware TMDB rating). Survivors are scored on the
/// fans-also-liked strength, ingredient overlap, and quality.</para>
/// <para>Rows are kept per person (in memory and in the plugin database) and served straight away; a stale row is
/// rebuilt in the background while the old one shows.</para>
/// </remarks>
public sealed class DiscoveryService
{
    public const int NewWindowDays = 150;

    internal const double MinLanguageShare = 0.15;
    internal const double MinAnimeShare = 0.15;
    internal const double MinAnimationShare = 0.05;

    private const string Algorithm = "v6";
    private const int RowSize = 20;
    private const int DetailLimit = 40;
    private const int Concurrency = 10;
    private const int AnimationGenre = 16;
    private const string StoreKeyPrefix = "discover-rows:";
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(2);

    private readonly LibraryIndex _index;
    private readonly ProfileService _profiles;
    private readonly TmdbClient _tmdb;
    private readonly SeerrClient _seerr;
    private readonly TraktClient _trakt;
    private readonly MdbListClient _mdb;
    private readonly NeighbourService _neighbours;
    private readonly TasteStore _store;
    private readonly ILogger<DiscoveryService> _logger;
    private readonly ConcurrentDictionary<Guid, Rows> _rows = new();
    private readonly ConcurrentDictionary<Guid, Lazy<Task<Rows>>> _building = new();

    public DiscoveryService(
        LibraryIndex index,
        ProfileService profiles,
        TmdbClient tmdb,
        SeerrClient seerr,
        TraktClient trakt,
        MdbListClient mdb,
        NeighbourService neighbours,
        TasteStore store,
        ILogger<DiscoveryService> logger)
    {
        _index = index;
        _profiles = profiles;
        _tmdb = tmdb;
        _seerr = seerr;
        _trakt = trakt;
        _mdb = mdb;
        _neighbours = neighbours;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Marks a person's rows for a background rebuild. The current rows keep showing until it finishes.
    /// </summary>
    public void Invalidate(Guid userId)
    {
        if (_rows.TryGetValue(userId, out var rows))
        {
            _rows[userId] = rows with { Stale = true };
        }
    }

    /// <param name="user">The signed-in person.</param>
    /// <param name="fresh">True for titles released in the last <see cref="NewWindowDays"/> days, false for older ones.</param>
    /// <param name="mediaType"><c>movie</c> or <c>tv</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<RecommendationRow> GetAsync(User user, bool fresh, string mediaType, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.EnableDiscover != true)
        {
            return new RecommendationRow([], "Titles outside the library are switched off in the Taste settings.");
        }

        if (!_tmdb.IsConfigured)
        {
            return new RecommendationRow([], "An admin needs to connect Seerr (or add a TMDB key) in the Taste plugin settings.");
        }

        var profile = await _profiles.GetAsync(user, cancellationToken).ConfigureAwait(false);
        if (!profile.HasTaste)
        {
            return new RecommendationRow([], "Rate a few titles first, then this row fills in.");
        }

        var version = Version(profile);
        var rows = Cached(user.Id);
        if (rows is null || !rows.Version.StartsWith(Algorithm + ":", StringComparison.Ordinal))
        {
            rows = await BuildShared(user, profile, version).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (rows.Stale || rows.Version != version || DateTime.UtcNow - rows.Built > FreshFor)
        {
            _ = BuildShared(user, profile, version);
        }

        return await OverlayAsync(rows.Get(fresh, mediaType), profile, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rebuilds a person's rows if they are missing or out of date. Used by the scheduled task.
    /// </summary>
    public async Task RefreshAsync(User user, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.EnableDiscover != true || !_tmdb.IsConfigured)
        {
            return;
        }

        var profile = await _profiles.GetAsync(user, cancellationToken).ConfigureAwait(false);
        if (!profile.HasTaste)
        {
            return;
        }

        var version = Version(profile);
        var rows = Cached(user.Id);
        if (rows is null || rows.Stale || rows.Version != version || DateTime.UtcNow - rows.Built > FreshFor)
        {
            await BuildShared(user, profile, version).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A rating that trusts a handful of votes less than hundreds: it starts at <paramref name="prior"/> and moves
    /// towards the real average as votes come in.
    /// </summary>
    internal static double BayesianRating(double average, int votes, string mediaType, double prior = 6.5)
    {
        var weight = mediaType == "tv" ? 25.0 : 50.0;
        return ((votes * average) + (weight * prior)) / (votes + weight);
    }

    /// <summary>
    /// 0–1 quality: MDBList's merged score (55 → 0, 90 → 1) when known, else the vote-count-aware TMDB rating (6 → 0, 9 → 1).
    /// </summary>
    internal static double Quality(double? mdbQuality, double bayes)
        => mdbQuality.HasValue ? Math.Clamp((mdbQuality.Value - 55) / 35, 0, 1) : Math.Clamp((bayes - 6) / 3, 0, 1);

    /// <summary>
    /// The score: fans-also-liked strength, overlap relative to the best candidate, and
    /// quality, plus small nudges for freshness (new row), several lists agreeing, and well-rated smaller titles.
    /// </summary>
    internal static double Blend(
        double strength,
        double relativeOverlap,
        double quality,
        int votes,
        string mediaType,
        DateTime? released,
        int listHits,
        bool fresh,
        DateTime now)
    {
        var score = (0.40 * strength) + (0.30 * relativeOverlap) + (0.25 * quality);
        if (fresh && released.HasValue)
        {
            score += 0.05 * Math.Exp(-Math.Max(0, (now - released.Value).TotalDays) / 75);
        }

        var popularVotes = mediaType == "tv" ? 400 : 1500;
        if (votes < popularVotes && quality >= 0.5)
        {
            score += 0.03;
        }

        return score + (0.02 * Math.Min(listHits, 3));
    }

    /// <summary>
    /// Shares of the person's positive weight that come from animation, Japanese animation, and each original language.
    /// </summary>
    internal static TasteShape Shape(UserProfile profile)
    {
        var positive = profile.Signals.Where(s => s.Weight > 0).ToList();
        var total = positive.Sum(s => s.Weight);
        if (total <= 0)
        {
            return new TasteShape(0, 0, new Dictionary<string, double>());
        }

        static bool Has(Signal signal, string key) => signal.Vector.TryGetValue(key, out var value) && value > 0;
        var animation = Features.Genre + ":animation";
        var japanese = Features.Language + ":ja";
        var languages = positive
            .SelectMany(s => s.Vector
                .Where(p => p.Value > 0 && Features.Category(p.Key) == Features.Language)
                .Select(p => (Language: Features.Label(p.Key), s.Weight)))
            .GroupBy(p => p.Language, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Weight) / total, StringComparer.Ordinal);
        return new TasteShape(
            positive.Where(s => Has(s, animation)).Sum(s => s.Weight) / total,
            positive.Where(s => Has(s, animation) && Has(s, japanese)).Sum(s => s.Weight) / total,
            languages);
    }

    /// <summary>
    /// Leaves out anime and animation for people who do not watch them, and TV news, reality, soaps, and talk
    /// shows unless the person likes that genre.
    /// </summary>
    internal static bool Fits(TasteShape shape, UserProfile profile, TmdbRef hit)
    {
        var genres = hit.GenreIds ?? [];
        var animated = genres.Contains(AnimationGenre);
        if (animated && hit.OriginalLanguage == "ja" && shape.Anime < MinAnimeShare)
        {
            return false;
        }

        if (animated && shape.Animation < MinAnimationShare)
        {
            return false;
        }

        return hit.MediaType != "tv"
               || !genres.Any(id => TmdbGenres.TvNiche.Contains(id)
                                    && !TmdbGenres.Names("tv", id).Any(name => profile.Vector.GetValueOrDefault(Features.Genre + ":" + name) > 0));
    }

    /// <summary>
    /// Languages that make up a real part of what the person likes, English included when it does.
    /// </summary>
    internal static IReadOnlyList<string> Languages(TasteShape shape)
    {
        var languages = shape.Languages.Where(p => p.Value >= MinLanguageShare).OrderByDescending(p => p.Value).Select(p => p.Key).ToList();
        return languages.Count > 0 ? languages : ["en"];
    }

    /// <summary>
    /// Whether a detailed candidate is good enough to show: enough TMDB votes, then MDBList's score (or IMDb votes)
    /// when MDBList knows it, else the vote-count-aware TMDB rating.
    /// </summary>
    internal static bool PassesQuality(string mediaType, bool fresh, int tmdbVotes, double bayes, MdbScore? mdb)
    {
        if (tmdbVotes < MinVotes(mediaType, fresh))
        {
            return false;
        }

        if (mdb?.Quality is { } quality)
        {
            if (quality < (fresh ? 62 : 66))
            {
                return false;
            }

            var minImdbVotes = (mediaType, fresh) switch
            {
                ("tv", true) => 100,
                (_, true) => 300,
                ("tv", false) => 800,
                _ => 3000,
            };
            return mdb.ImdbVotes == 0 || mdb.ImdbVotes >= minImdbVotes;
        }

        return bayes >= 6.6;
    }

    /// <summary>
    /// Changes when the person says something new, not as their signals slowly fade.
    /// </summary>
    internal static string Version(UserProfile profile)
    {
        var said = string.Join(
            '\n',
            profile.Signals
                .Select(s => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{s.TitleKey}|{s.Rating}|{s.Thumb}|{s.Heart}|{s.Finished}|{s.Requested}|{Math.Sign(s.Weight)}"))
                .Concat(profile.Excluded.Select(key => "x|" + key))
                .Order(StringComparer.Ordinal));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(said)), 0, 8);
        return string.Create(CultureInfo.InvariantCulture, $"{Algorithm}:{profile.Signals.Count}:{hash}");
    }

    private static int MinVotes(string mediaType, bool fresh) => (mediaType, fresh) switch
    {
        ("tv", true) => 10,
        (_, true) => 20,
        ("tv", false) => 80,
        _ => 250,
    };

    private Rows? Cached(Guid userId)
    {
        if (_rows.TryGetValue(userId, out var rows))
        {
            return DateTime.UtcNow - rows.Built < KeepFor ? rows : null;
        }

        var json = _store.GetCached(StoreKeyPrefix + userId.ToString("N"), KeepFor);
        if (json is null)
        {
            return null;
        }

        try
        {
            rows = JsonSerializer.Deserialize<Rows>(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (rows is not null)
        {
            _rows.TryAdd(userId, rows);
        }

        return rows;
    }

    private Task<Rows> BuildShared(User user, UserProfile profile, string version)
        => _building.GetOrAdd(
            user.Id,
            _ => new Lazy<Task<Rows>>(() => Task.Run(() => BuildAndKeepAsync(user, profile, version)))).Value;

    private async Task<Rows> BuildAndKeepAsync(User user, UserProfile profile, string version)
    {
        try
        {
            var started = DateTime.UtcNow;
            var rows = await BuildAsync(profile, version, CancellationToken.None).ConfigureAwait(false);
            _rows[user.Id] = rows;
            _store.PutCached(StoreKeyPrefix + user.Id.ToString("N"), JsonSerializer.Serialize(rows));
            _logger.LogInformation(
                "Taste: discovery rows for {User}: new {NewMovies} movies / {NewShows} shows, older {OlderMovies} movies / {OlderShows} shows in {Seconds:0.0}s (titles via {Source}; Trakt {Trakt}, MDBList {Mdb})",
                user.Username,
                rows.NewMovies.Items.Count,
                rows.NewShows.Items.Count,
                rows.OlderMovies.Items.Count,
                rows.OlderShows.Items.Count,
                (DateTime.UtcNow - started).TotalSeconds,
                _tmdb.Source,
                _trakt.IsConfigured ? "on" : "off",
                _mdb.IsConfigured ? "on" : "off");
            return rows;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Taste: discovery rows for {User} failed", user.Username);
            throw;
        }
        finally
        {
            _building.TryRemove(user.Id, out _);
        }
    }

    /// <summary>
    /// Applies what changed since the row was built: titles now on disk, seen, or dismissed drop out, and
    /// titles requested in Seerr show as requested.
    /// </summary>
    private async Task<RecommendationRow> OverlayAsync(RecommendationRow row, UserProfile profile, CancellationToken cancellationToken)
    {
        var snapshot = await _index.GetAsync(cancellationToken).ConfigureAwait(false);
        var requested = await _seerr.GetRequestedAsync(cancellationToken).ConfigureAwait(false);
        var items = row.Items
            .Where(i => i.TmdbId is not null
                        && !profile.Excluded.Contains(i.Key)
                        && !profile.SeenTmdb.Contains(i.MediaType + ":" + i.TmdbId)
                        && snapshot.FindByTmdb(i.MediaType, i.TmdbId) is null)
            .Select(i => requested.Contains(i.MediaType + ":" + i.TmdbId) ? i with { Status = "requested" } : i)
            .ToList();
        return new RecommendationRow(items, items.Count > 0 ? null : row.Notice ?? "Nothing outside the library fits yet.");
    }

    private async Task<Rows> BuildAsync(UserProfile profile, string version, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var since = now.Date.AddDays(-NewWindowDays);
        var shape = Shape(profile);
        var snapshot = await _index.GetAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await GatherAsync(profile, shape, since, now, cancellationToken).ConfigureAwait(false);

        var maxGenre = profile.Vector
            .Where(p => Features.Category(p.Key) == Features.Genre)
            .Select(p => Math.Abs(p.Value))
            .DefaultIfEmpty(1)
            .Max();
        var usable = candidates.Values
            .Where(c => c.Ref.SeerrStatus is not (4 or 5 or 6))
            .Where(c => c.Support >= 0)
            .Where(c => snapshot.FindByTmdb(c.Ref.MediaType, c.Ref.TmdbId) is null)
            .Where(c => !profile.SeenTmdb.Contains(c.Ref.MediaType + ":" + c.Ref.TmdbId))
            .Where(c => !profile.Excluded.Contains(TasteStore.TmdbKey(c.Ref.MediaType, c.Ref.TmdbId)))
            .Where(c => Fits(shape, profile, c.Ref))
            .ToList();

        using var throttle = new SemaphoreSlim(Concurrency);
        Task<RecommendationRow> Rank(bool fresh, string mediaType)
            => RankAsync(usable.Where(c => c.Ref.MediaType == mediaType).ToList(), fresh, since, now, profile, shape, snapshot, maxGenre, throttle, cancellationToken);
        var rows = await Task.WhenAll(Rank(true, "movie"), Rank(true, "tv"), Rank(false, "movie"), Rank(false, "tv")).ConfigureAwait(false);
        return new Rows(DateTime.UtcNow, version, false, rows[0], rows[1], rows[2], rows[3]);
    }

    private async Task<RecommendationRow> RankAsync(
        IReadOnlyList<Candidate> usable,
        bool fresh,
        DateTime since,
        DateTime now,
        UserProfile profile,
        TasteShape shape,
        LibrarySnapshot snapshot,
        double maxGenre,
        SemaphoreSlim throttle,
        CancellationToken cancellationToken)
    {
        var shortlist = usable
            .Where(c => InWindow(c.Ref.Date, fresh, since, now))
            .OrderByDescending(c => Prelim(profile, shape, c, maxGenre))
            .Take(DetailLimit)
            .ToList();

        var detailed = (await Task.WhenAll(shortlist.Select(async c =>
            {
                await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return (Candidate: c, Details: await _tmdb.GetTitleAsync(c.Ref.MediaType, c.Ref.TmdbId, cancellationToken).ConfigureAwait(false));
                }
                finally
                {
                    throttle.Release();
                }
            })).ConfigureAwait(false))
            .Where(d => d.Details is not null && InWindow(d.Details.ReleaseDate ?? d.Candidate.Ref.Date, fresh, since, now))
            .Select(d => (d.Candidate, Details: d.Details!))
            .ToList();

        var scores = await _mdb.GetScoresAsync(detailed.Select(d => (d.Details.MediaType, d.Details.TmdbId)).ToList(), cancellationToken)
            .ConfigureAwait(false);

        var gated = detailed
            .Select(d =>
            {
                var details = d.Details;
                var vector = snapshot.Vectorize(Features.Build(details.Metadata));
                var mdb = scores.GetValueOrDefault(details.MediaType + ":" + details.TmdbId);
                var bayes = BayesianRating(details.VoteAverage, details.VoteCount, details.MediaType);
                return (d.Candidate, Details: details, Vector: (IReadOnlyDictionary<string, double>)vector, Mdb: mdb, Bayes: bayes,
                    Overlap: TasteMath.Cosine(profile.Vector, profile.Length, vector));
            })
            .Where(p => PassesQuality(p.Details.MediaType, fresh, p.Details.VoteCount, p.Bayes, p.Mdb))
            .Where(p => p.Overlap > 0.02 || p.Candidate.Support > 0)
            .ToList();

        var bestOverlap = Math.Max(0.05, gated.Select(p => p.Overlap).DefaultIfEmpty(0).Max());
        var scored = gated
            .Select(p => (p.Candidate, p.Details, p.Vector, p.Mdb, Score: Blend(
                NeighbourService.Strength(p.Candidate.Support),
                Math.Clamp(p.Overlap / bestOverlap, -1, 1),
                Quality(p.Mdb?.Quality, p.Bayes),
                p.Mdb is { ImdbVotes: > 0 } m ? m.ImdbVotes : p.Details.VoteCount,
                p.Details.MediaType,
                p.Details.ReleaseDate,
                p.Candidate.ListHits,
                fresh,
                now)))
            .OrderByDescending(p => p.Score)
            .ToList();

        var picked = TasteMath.Diversify(scored, p => p.Score, p => p.Vector, RowSize);
        var items = picked.Select(p => new Recommendation(
            TasteStore.TmdbKey(p.Details.MediaType, p.Details.TmdbId),
            p.Details.Title,
            p.Details.Year,
            p.Details.MediaType,
            null,
            p.Details.TmdbId,
            TmdbClient.PosterUrl(p.Details.PosterPath),
            p.Details.Overview,
            Reason(profile, p.Candidate, p.Vector),
            Math.Round(p.Score, 4),
            Meta(p.Details, p.Mdb),
            fresh ? Badge(p.Details.ReleaseDate) : null,
            null,
            TmdbClient.Link(p.Details.MediaType, p.Details.TmdbId))).ToList();

        var notice = items.Count > 0
            ? null
            : fresh
                ? "Nothing new in the last few months fits yet. Rate a few more titles and check back."
                : "Nothing outside the library fits yet.";
        return new RecommendationRow(items, notice);
    }

    private static string Reason(UserProfile profile, Candidate candidate, IReadOnlyDictionary<string, double> vector)
        => candidate.Because.Count == 0 ? ReasonWriter.Explain(profile, vector) : ReasonWriter.FansAlsoLiked(candidate.Because);

    private async Task<Dictionary<string, Candidate>> GatherAsync(
        UserProfile profile,
        TasteShape shape,
        DateTime since,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var neighbours = await _neighbours.GetAsync(profile, cancellationToken).ConfigureAwait(false);
        foreach (var (key, neighbour) in neighbours)
        {
            candidates[key] = new Candidate(neighbour.Ref) { Support = neighbour.Support, Because = neighbour.Because };
        }

        var languages = Languages(shape);
        var queries = new List<Func<Task<IReadOnlyList<TmdbRef>>>>();
        var year = now.Year;
        var curated = _trakt.IsConfigured || _mdb.IsConfigured;

        foreach (var mediaType in new[] { "tv", "movie" })
        {
            if (_trakt.IsConfigured)
            {
                var recent = new Dictionary<string, string>
                {
                    ["years"] = $"{year - 1}-{year}",
                    ["languages"] = string.Join(',', languages),
                    ["imdb_ratings"] = "6.5-10.0",
                };
                queries.Add(() => _trakt.GetChartAsync(mediaType, "trending", recent, cancellationToken));
                queries.Add(() => _trakt.GetChartAsync(mediaType, "popular", recent, cancellationToken));
                queries.Add(() => _trakt.GetChartAsync(
                    mediaType,
                    "popular",
                    new Dictionary<string, string>
                    {
                        ["years"] = $"{year - 15}-{year - 1}",
                        ["languages"] = string.Join(',', languages),
                        ["imdb_ratings"] = "7.5-10.0",
                        ["imdb_votes"] = mediaType == "tv" ? "1000-150000" : "5000-300000",
                    },
                    cancellationToken));
            }

            if (_mdb.IsConfigured)
            {
                queries.Add(() => _mdb.GetCatalogAsync(mediaType, since, now.Date, 62, languages, cancellationToken));
                queries.Add(() => _mdb.GetCatalogAsync(mediaType, null, since, 75, languages, cancellationToken));
            }

            var genres = profile.Vector
                .Where(p => p.Value > 0 && Features.Category(p.Key) == Features.Genre)
                .OrderByDescending(p => p.Value)
                .Take(curated ? 2 : 3)
                .SelectMany(p => TmdbGenres.Ids(mediaType, Features.Label(p.Key)))
                .Distinct()
                .Take(curated ? 2 : 4)
                .ToList();
            var newFloor = MinVotes(mediaType, true);
            var oldFloor = MinVotes(mediaType, false);
            var recentQuery = new DiscoverQuery(mediaType) { ReleasedAfter = since, ReleasedBefore = now.Date, MinVoteCount = newFloor };
            var olderQuery = new DiscoverQuery(mediaType) { ReleasedBefore = since, MinVoteCount = oldFloor * 2, SortBy = "vote_average.desc", MinVoteAverage = 7.0 };
            foreach (var genreId in genres)
            {
                queries.Add(() => _tmdb.DiscoverAsync(recentQuery with { Genres = [genreId], MinVoteAverage = 6.5 }, cancellationToken));
                queries.Add(() => _tmdb.DiscoverAsync(olderQuery with { Genres = [genreId] }, cancellationToken));
            }

            if (!curated)
            {
                queries.Add(() => _tmdb.DiscoverAsync(
                    recentQuery with { SortBy = "vote_average.desc", MinVoteCount = newFloor * 3, MinVoteAverage = 7.0 },
                    cancellationToken));
            }
        }

        var gate = new Lock();
        using var throttle = new SemaphoreSlim(Concurrency);
        await Task.WhenAll(queries.Select(async query =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<TmdbRef> hits;
            try
            {
                hits = await query().ConfigureAwait(false);
            }
            finally
            {
                throttle.Release();
            }

            lock (gate)
            {
                foreach (var hit in hits)
                {
                    var key = hit.MediaType + ":" + hit.TmdbId;
                    if (candidates.TryGetValue(key, out var existing))
                    {
                        existing.ListHits++;
                        if (existing.Ref.Date is null && hit.Date is not null)
                        {
                            existing.Ref = existing.Ref with { Date = hit.Date };
                        }
                    }
                    else
                    {
                        candidates[key] = new Candidate(hit) { ListHits = 1 };
                    }
                }
            }
        })).ConfigureAwait(false);

        return candidates;
    }

    private static bool InWindow(DateTime? released, bool fresh, DateTime since, DateTime now)
    {
        if (released is null)
        {
            return false;
        }

        return fresh ? released.Value >= since && released.Value <= now : released.Value < since;
    }

    /// <summary>
    /// A cheap rank from list data alone, so only the most promising get a details lookup. Fans-also-liked
    /// strength counts most; then genres, language, how many lists agree, and the list rating.
    /// </summary>
    private static double Prelim(UserProfile profile, TasteShape shape, Candidate candidate, double maxGenre)
    {
        var hit = candidate.Ref;
        var genreFit = (hit.GenreIds ?? [])
            .SelectMany(id => TmdbGenres.Names(hit.MediaType, id))
            .Distinct()
            .Sum(name => profile.Vector.GetValueOrDefault(Features.Genre + ":" + name)) / Math.Max(maxGenre, 1e-9);
        var languageShare = hit.OriginalLanguage is null ? 0 : shape.Languages.GetValueOrDefault(hit.OriginalLanguage);
        var languageFit = hit.OriginalLanguage is null or "en" ? 0
            : languageShare >= MinLanguageShare ? 0.5
            : languageShare > 0.03 ? 0
            : -0.75;
        var bayes = BayesianRating(hit.VoteAverage, hit.VoteCount, hit.MediaType);
        return (2.5 * NeighbourService.Strength(candidate.Support))
               + Math.Clamp(genreFit, -1, 2)
               + languageFit
               + (0.3 * Math.Min(candidate.ListHits, 3))
               + (0.5 * Math.Clamp((bayes - 6.0) / 1.5, 0, 2));
    }

    private static string Meta(TmdbTitle title, MdbScore? mdb)
    {
        var parts = new List<string> { title.MediaType == "tv" ? "Series" : "Movie" };
        if (!string.IsNullOrEmpty(title.Network))
        {
            parts.Add(title.Network);
        }

        if (mdb?.ImdbRating is { } imdb)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"IMDb {imdb:0.0}"));
            if (mdb.Critics is { } critics)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"critics {critics:0}%"));
            }
        }
        else if (title.VoteCount > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"★ {title.VoteAverage:0.0} ({title.VoteCount} votes)"));
        }

        return string.Join(" · ", parts);
    }

    private static string? Badge(DateTime? released)
        => released is null ? null : "New · " + released.Value.ToString("MMM yyyy", CultureInfo.InvariantCulture);

    internal sealed record TasteShape(double Animation, double Anime, IReadOnlyDictionary<string, double> Languages);

    internal sealed record Rows(
        DateTime Built,
        string Version,
        bool Stale,
        RecommendationRow NewMovies,
        RecommendationRow NewShows,
        RecommendationRow OlderMovies,
        RecommendationRow OlderShows)
    {
        public RecommendationRow Get(bool fresh, string mediaType) => (fresh, mediaType) switch
        {
            (true, "tv") => NewShows,
            (true, _) => NewMovies,
            (false, "tv") => OlderShows,
            _ => OlderMovies,
        };
    }

    private sealed class Candidate(TmdbRef hit)
    {
        public TmdbRef Ref { get; set; } = hit;

        public double Support { get; init; }

        public IReadOnlyList<string> Because { get; init; } = [];

        public int ListHits { get; set; }
    }
}
