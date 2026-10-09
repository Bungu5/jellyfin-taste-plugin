using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Taste.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Taste.Services;

/// <summary>
/// One title in an export, identified by provider ids so it can be found again on another server.
/// </summary>
public sealed record ExportedTitle
{
    /// <summary>
    /// Gets <c>movie</c> or <c>tv</c>.
    /// </summary>
    public string MediaType { get; init; } = string.Empty;

    public string? Title { get; init; }

    public int? Year { get; init; }

    public string? Tmdb { get; init; }

    public string? Imdb { get; init; }

    public string? Tvdb { get; init; }

    /// <summary>
    /// Gets the 0–10 score, Jellyfin's scale (5 stars is 10).
    /// </summary>
    public double? Rating { get; init; }

    public int? Thumb { get; init; }

    public bool NotInterested { get; init; }

    /// <summary>
    /// Gets a value indicating whether the finish dialog has already asked about this title.
    /// </summary>
    public bool Asked { get; init; }
}

public sealed record ExportedCalibration(string DeckKey, string Answer);

/// <summary>
/// Everything one person told Taste, in a form another server can import.
/// </summary>
public sealed record TasteExport
{
    public const string FormatName = "taste-export";
    public const int CurrentVersion = 1;

    public string Format { get; init; } = FormatName;

    public int Version { get; init; } = CurrentVersion;

    public DateTime ExportedUtc { get; init; }

    public IReadOnlyList<ExportedTitle> Titles { get; init; } = [];

    public IReadOnlyList<ExportedCalibration> Calibration { get; init; } = [];
}

public sealed record ImportResult(int InLibrary, int Outside, int Skipped, int AlreadyRated, int Calibration);

/// <summary>
/// Moves a person's stars, thumbs, "not for me" titles, and calibration answers between servers.
/// Import only adds: anything already scored on this server is left alone.
/// </summary>
public sealed class TransferService
{
    public const int MaxTitles = 50_000;

    private static readonly string[] CalibrationAnswers = ["yes", "no", "skip"];

    private readonly LibraryIndex _index;
    private readonly IUserDataManager _userDataManager;
    private readonly TasteStore _store;
    private readonly ProfileService _profiles;

    public TransferService(LibraryIndex index, IUserDataManager userDataManager, TasteStore store, ProfileService profiles)
    {
        _index = index;
        _userDataManager = userDataManager;
        _store = store;
        _profiles = profiles;
    }

    public async Task<TasteExport> ExportAsync(User user, CancellationToken cancellationToken)
    {
        var snapshot = await _index.GetAsync(cancellationToken).ConfigureAwait(false);
        var answers = _store.GetAnswers(user.Id);
        var userData = _userDataManager.GetUserDataBatch(snapshot.Titles.Values.Select(t => t.Item).ToList(), user);
        var titles = new List<ExportedTitle>();

        foreach (var title in snapshot.Titles.Values)
        {
            userData.TryGetValue(title.Id, out var data);
            answers.TryGetValue(TasteStore.LibraryKey(title.Id), out var answer);
            var rating = data?.Rating;
            if (rating is null && !Meaningful(answer))
            {
                continue;
            }

            titles.Add(new ExportedTitle
            {
                MediaType = title.MediaType,
                Title = title.Name,
                Year = title.Year,
                Tmdb = title.TmdbId,
                Imdb = NullIfEmpty(title.Item.GetProviderId(MetadataProvider.Imdb)),
                Tvdb = NullIfEmpty(title.Item.GetProviderId(MetadataProvider.Tvdb)),
                Rating = rating,
                Thumb = answer?.Thumb,
                NotInterested = answer?.NotInterested ?? false,
                Asked = answer?.Asked ?? false,
            });
        }

        foreach (var answer in answers.Values.Where(Meaningful))
        {
            var libraryId = TasteStore.ParseLibraryKey(answer.TitleKey);
            if (libraryId.HasValue && snapshot.Titles.ContainsKey(libraryId.Value))
            {
                continue;
            }

            var (keyType, keyId) = TasteStore.ParseTmdbKey(answer.TitleKey);
            var mediaType = answer.MediaType ?? keyType;
            var tmdbId = answer.TmdbId ?? keyId;
            if (mediaType is not ("movie" or "tv") || tmdbId is null)
            {
                continue;
            }

            titles.Add(new ExportedTitle
            {
                MediaType = mediaType,
                Title = answer.Title,
                Year = answer.Year,
                Tmdb = tmdbId,
                Rating = answer.Stars * 2,
                Thumb = answer.Thumb,
                NotInterested = answer.NotInterested,
                Asked = answer.Asked,
            });
        }

        return new TasteExport
        {
            ExportedUtc = DateTime.UtcNow,
            Titles = titles,
            Calibration = _store.GetCalibrationShown(user.Id)
                .Select(pair => new ExportedCalibration(pair.Key, pair.Value))
                .ToList(),
        };
    }

    public async Task<ImportResult> ImportAsync(User user, TasteExport export, CancellationToken cancellationToken)
    {
        var snapshot = await _index.GetAsync(cancellationToken).ConfigureAwait(false);
        var byProvider = new Dictionary<string, LibraryTitle>(StringComparer.OrdinalIgnoreCase);
        foreach (var title in snapshot.Titles.Values)
        {
            foreach (var provider in new[] { MetadataProvider.Imdb, MetadataProvider.Tvdb })
            {
                var id = NullIfEmpty(title.Item.GetProviderId(provider));
                if (id is not null)
                {
                    byProvider.TryAdd(ProviderKey(title.MediaType, provider, id), title);
                }
            }
        }

        var inLibrary = 0;
        var outside = 0;
        var skipped = 0;
        var alreadyRated = 0;
        foreach (var incoming in export.Titles.Select(Clean))
        {
            if (incoming is null)
            {
                skipped++;
                continue;
            }

            var match = snapshot.FindByTmdb(incoming.MediaType, incoming.Tmdb)
                        ?? Lookup(byProvider, incoming.MediaType, MetadataProvider.Imdb, incoming.Imdb)
                        ?? Lookup(byProvider, incoming.MediaType, MetadataProvider.Tvdb, incoming.Tvdb);
            if (match is not null)
            {
                if (ImportLibraryTitle(user, match, incoming))
                {
                    inLibrary++;
                }
                else
                {
                    alreadyRated++;
                }
            }
            else if (incoming.Tmdb is not null)
            {
                _store.Update(user.Id, TasteStore.TmdbKey(incoming.MediaType, incoming.Tmdb), answer => Merge(answer, incoming, false) with
                {
                    Title = answer.Title ?? incoming.Title,
                    Year = answer.Year ?? incoming.Year,
                    MediaType = incoming.MediaType,
                    TmdbId = incoming.Tmdb,
                });
                outside++;
            }
            else
            {
                skipped++;
            }
        }

        var shown = _store.GetCalibrationShown(user.Id);
        var calibration = 0;
        foreach (var answer in export.Calibration)
        {
            if (!string.IsNullOrWhiteSpace(answer.DeckKey)
                && CalibrationAnswers.Contains(answer.Answer)
                && !shown.ContainsKey(answer.DeckKey))
            {
                _store.MarkCalibrationShown(user.Id, answer.DeckKey, answer.Answer);
                calibration++;
            }
        }

        _profiles.Invalidate(user.Id);
        return new ImportResult(inLibrary, outside, skipped, alreadyRated, calibration);
    }

    /// <summary>
    /// Fills in what this server does not know yet; never replaces an existing score, thumb, or "not for me".
    /// </summary>
    internal static Answer Merge(Answer existing, ExportedTitle incoming, bool starsLiveInJellyfin) => existing with
    {
        Stars = starsLiveInJellyfin ? existing.Stars : existing.Stars ?? ToStars(incoming.Rating),
        Thumb = existing.Thumb ?? incoming.Thumb,
        NotInterested = existing.NotInterested || incoming.NotInterested,
        Asked = existing.Asked || incoming.Asked || incoming.Rating.HasValue || incoming.Thumb.HasValue,
    };

    internal static int? ToStars(double? rating)
        => rating is > 0 ? Math.Clamp((int)Math.Round(rating.Value / 2, MidpointRounding.AwayFromZero), 1, 5) : null;

    /// <summary>
    /// Drops values the rest of the plugin would never write; null when the title cannot be identified.
    /// </summary>
    internal static ExportedTitle? Clean(ExportedTitle title)
    {
        if (title.MediaType is not ("movie" or "tv"))
        {
            return null;
        }

        var cleaned = title with
        {
            Tmdb = NullIfEmpty(title.Tmdb),
            Imdb = NullIfEmpty(title.Imdb),
            Tvdb = NullIfEmpty(title.Tvdb),
            Rating = title.Rating is > 0 and <= 10 ? title.Rating : null,
            Thumb = title.Thumb is 1 or -1 ? title.Thumb : null,
        };
        return cleaned.Tmdb is null && cleaned.Imdb is null && cleaned.Tvdb is null ? null : cleaned;
    }

    private static bool Meaningful(Answer? answer)
        => answer is not null && (answer.Stars.HasValue || answer.Thumb.HasValue || answer.NotInterested || answer.Asked);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ProviderKey(string mediaType, MetadataProvider provider, string id) => $"{mediaType}:{provider}:{id}";

    private static LibraryTitle? Lookup(Dictionary<string, LibraryTitle> byProvider, string mediaType, MetadataProvider provider, string? id)
        => id is not null && byProvider.TryGetValue(ProviderKey(mediaType, provider, id), out var title) ? title : null;

    /// <returns>False when this server already had everything the import carries for the title.</returns>
    private bool ImportLibraryTitle(User user, LibraryTitle title, ExportedTitle incoming)
    {
        var changed = false;
        var data = _userDataManager.GetUserData(user, title.Item);
        if (incoming.Rating.HasValue && data?.Rating is null)
        {
            _userDataManager.SaveUserData(
                user,
                title.Item,
                new UpdateUserItemDataDto { Rating = incoming.Rating },
                UserDataSaveReason.UpdateUserRating);
            changed = true;
        }

        var key = TasteStore.LibraryKey(title.Id);
        var existing = _store.GetAnswer(user.Id, key);
        var merged = Merge(existing, incoming, true);
        if (merged != existing)
        {
            _store.Update(user.Id, key, answer => Merge(answer, incoming, true) with
            {
                Title = answer.Title ?? title.Name,
                Year = answer.Year ?? title.Year,
                MediaType = title.MediaType,
                TmdbId = answer.TmdbId ?? title.TmdbId,
            });
            changed = true;
        }

        return changed;
    }
}
