using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Taste.Data;

namespace Jellyfin.Plugin.Taste.Services;

public sealed record DeckEntry(string MediaType, string TmdbId, string Title, int? Year, IReadOnlyList<string> Genres)
{
    public string Key => MediaType + ":" + TmdbId;
}

public sealed record CalibrationCard(
    string Key,
    string Title,
    int? Year,
    string MediaType,
    string? TmdbId,
    Guid? ItemId,
    string? ImageUrl);

public sealed record CalibrationSet(IReadOnlyList<CalibrationCard> Cards, int OutsideAnswers, int OutsideTarget);

/// <summary>
/// The two short calibration passes: "have you seen these" and "rate five you finished".
/// </summary>
public sealed class CalibrationService
{
    public const int CardsPerPass = 5;
    public const int OutsideTarget = 10;

    private static readonly Lazy<IReadOnlyList<DeckEntry>> Deck = new(LoadDeck);

    private readonly LibraryIndex _index;
    private readonly ProfileService _profiles;
    private readonly TasteStore _store;
    private readonly RatingService _ratings;
    private readonly TmdbClient _tmdb;

    public CalibrationService(
        LibraryIndex index,
        ProfileService profiles,
        TasteStore store,
        RatingService ratings,
        TmdbClient tmdb)
    {
        _index = index;
        _profiles = profiles;
        _store = store;
        _ratings = ratings;
        _tmdb = tmdb;
    }

    public static IReadOnlyList<DeckEntry> FixedDeck => Deck.Value;

    /// <summary>
    /// Five well-known titles they have not played here, spread across genres.
    /// </summary>
    public async Task<CalibrationSet> SeenDeckAsync(User user, CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetAsync(user, cancellationToken).ConfigureAwait(false);
        var snapshot = await _index.GetAsync(cancellationToken).ConfigureAwait(false);
        var shown = _store.GetCalibrationShown(user.Id);

        var available = FixedDeck
            .DistinctBy(e => e.Key)
            .Where(e => !shown.ContainsKey(e.Key))
            .Where(e => !profile.SeenTmdb.Contains(e.Key))
            .Where(e => snapshot.FindByTmdb(e.MediaType, e.TmdbId) is not { } owned || !profile.SeenLibrary.Contains(owned.Id))
            .OrderBy(e => StableShuffle(user.Id, e.Key))
            .ToList();

        var picked = SpreadByGenre(available, e => e.Genres.FirstOrDefault(), CardsPerPass);
        var cards = new List<CalibrationCard>();
        foreach (var entry in picked)
        {
            var owned = snapshot.FindByTmdb(entry.MediaType, entry.TmdbId);
            string? image = null;
            if (owned is null && _tmdb.IsConfigured)
            {
                var details = await _tmdb.GetTitleAsync(entry.MediaType, entry.TmdbId, cancellationToken).ConfigureAwait(false);
                image = TmdbClient.PosterUrl(details?.PosterPath);
            }

            cards.Add(new CalibrationCard(entry.Key, entry.Title, entry.Year, entry.MediaType, entry.TmdbId, owned?.Id, image));
        }

        return new CalibrationSet(cards, OutsideAnswerCount(user), OutsideTarget);
    }

    /// <summary>
    /// Records a "have you seen this" answer. A yes with a score writes Jellyfin's rating when the title is in the library.
    /// </summary>
    public async Task<int> AnswerSeenAsync(
        User user,
        string deckKey,
        bool? seen,
        int? stars,
        int? thumb,
        string? title,
        int? year,
        CancellationToken cancellationToken)
    {
        var parts = deckKey.Split(':');
        if (parts.Length != 2 || parts[0] is not ("movie" or "tv") || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out _))
        {
            throw new ArgumentException("Deck key must look like movie:603 or tv:1396.", nameof(deckKey));
        }

        _store.MarkCalibrationShown(user.Id, deckKey, seen switch { true => "yes", false => "no", _ => "skip" });

        if (seen == true && (stars.HasValue || thumb.HasValue))
        {
            var snapshot = await _index.GetAsync(cancellationToken).ConfigureAwait(false);
            var owned = snapshot.FindByTmdb(parts[0], parts[1]);
            if (owned is not null)
            {
                _ratings.Rate(user, owned.Id, stars, thumb);
            }
            else
            {
                var entry = FixedDeck.FirstOrDefault(e => e.Key == deckKey);
                _ratings.RateOutside(user, parts[0], parts[1], entry?.Title ?? title, entry?.Year ?? year, stars, thumb);
            }
        }

        _profiles.Invalidate(user.Id);
        return OutsideAnswerCount(user);
    }

    /// <summary>
    /// Five titles they finished here but never scored, mixing recent and older watches across genres.
    /// Titles dismissed in the dialog land here too.
    /// </summary>
    public async Task<CalibrationSet> FinishedAsync(User user, CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetAsync(user, cancellationToken).ConfigureAwait(false);
        var unscored = profile.Signals
            .Where(s => s.LibraryId.HasValue && s.Finished && !s.IsExplicit && !profile.Excluded.Contains(s.TitleKey))
            .OrderByDescending(s => s.When ?? DateTime.MinValue)
            .ToList();

        var half = (unscored.Count + 1) / 2;
        var recent = unscored.Take(half).ToList();
        var older = unscored.Skip(half).ToList();
        var interleaved = new List<Signal>(unscored.Count);
        for (var i = 0; i < Math.Max(recent.Count, older.Count); i++)
        {
            if (i < recent.Count)
            {
                interleaved.Add(recent[i]);
            }

            if (i < older.Count)
            {
                interleaved.Add(older[i]);
            }
        }

        var picked = SpreadByGenre(interleaved, s => s.FirstGenre, CardsPerPass);
        return new CalibrationSet(
            picked.Select(s => new CalibrationCard(s.TitleKey, s.Title, s.Year, s.MediaType ?? "movie", s.TmdbId, s.LibraryId, null)).ToList(),
            OutsideAnswerCount(user),
            OutsideTarget);
    }

    internal static List<T> SpreadByGenre<T>(IReadOnlyList<T> ordered, Func<T, string?> genre, int take)
    {
        var picked = new List<T>(take);
        var usedGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ordered)
        {
            if (picked.Count == take)
            {
                return picked;
            }

            var g = genre(item) ?? string.Empty;
            if (usedGenres.Add(g))
            {
                picked.Add(item);
            }
        }

        foreach (var item in ordered)
        {
            if (picked.Count == take)
            {
                break;
            }

            if (!picked.Contains(item))
            {
                picked.Add(item);
            }
        }

        return picked;
    }

    private static long StableShuffle(Guid userId, string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(userId.ToString("N", CultureInfo.InvariantCulture) + key));
        return BitConverter.ToInt64(bytes, 0);
    }

    private static IReadOnlyList<DeckEntry> LoadDeck()
    {
        using var stream = typeof(CalibrationService).Assembly
            .GetManifestResourceStream("Jellyfin.Plugin.Taste.Data.calibration-deck.json")
            ?? throw new InvalidOperationException("calibration-deck.json is not embedded");
        var entries = JsonSerializer.Deserialize<List<DeckEntry>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return entries ?? [];
    }

    private int OutsideAnswerCount(User user)
        => _store.GetAnswers(user.Id).Values.Count(a =>
            a.TitleKey.StartsWith("tmdb:", StringComparison.Ordinal) && (a.Stars.HasValue || a.Thumb.HasValue));
}
