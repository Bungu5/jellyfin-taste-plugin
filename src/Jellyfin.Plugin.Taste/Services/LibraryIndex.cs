using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Taste.Scoring;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Taste.Services;

/// <summary>
/// A movie or series in the Jellyfin library, with its ingredient vector.
/// </summary>
public sealed class LibraryTitle
{
    public required BaseItem Item { get; init; }

    public Guid Id => Item.Id;

    public required bool IsSeries { get; init; }

    public string Name => Item.Name;

    public int? Year => Item.ProductionYear;

    public string MediaType => IsSeries ? "tv" : "movie";

    public string? TmdbId { get; init; }

    public required IReadOnlyDictionary<string, double> RawFeatures { get; init; }

    public IReadOnlyDictionary<string, double> Vector { get; set; } = new Dictionary<string, double>();

    public int EpisodeCount { get; set; }
}

public sealed class LibrarySnapshot
{
    public required IReadOnlyDictionary<Guid, LibraryTitle> Titles { get; init; }

    /// <summary>
    /// Gets library title ids by <c>movie:{tmdb}</c> or <c>tv:{tmdb}</c>.
    /// </summary>
    public required IReadOnlyDictionary<string, Guid> ByTmdb { get; init; }

    public required IReadOnlyDictionary<string, double> Idf { get; init; }

    public required double UnknownIdf { get; init; }

    public required DateTime BuiltUtc { get; init; }

    public Dictionary<string, double> Vectorize(IReadOnlyDictionary<string, double> raw)
        => TasteMath.Weighted(raw, Idf, UnknownIdf);

    public LibraryTitle? FindByTmdb(string mediaType, string? tmdbId)
        => tmdbId is not null && ByTmdb.TryGetValue(mediaType + ":" + tmdbId, out var id) ? Titles[id] : null;
}

/// <summary>
/// Caches every movie and series with its ingredients. The cache stays in place while the title count and the
/// ingredient count are unchanged. A movie or series being added, removed, or edited can rebuild it, and a scan
/// waits at least two minutes so it does not rebuild once per item. When the new counts match, the existing
/// snapshot is kept.
/// </summary>
public sealed class LibraryIndex : IDisposable
{
    private static readonly TimeSpan MinAgeWhenChanged = TimeSpan.FromMinutes(2);
    private static readonly ItemUpdateType MetadataChange =
        ItemUpdateType.MetadataImport | ItemUpdateType.MetadataDownload | ItemUpdateType.MetadataEdit;

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryIndex> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _episodeCounts = new();
    private volatile LibrarySnapshot? _snapshot;
    private volatile bool _changed;

    public LibraryIndex(ILibraryManager libraryManager, ILogger<LibraryIndex> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemRemoved += OnItemRemoved;
        _libraryManager.ItemUpdated += OnItemUpdated;
    }

    public void Dispose()
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _gate.Dispose();
    }

    /// <summary>
    /// True when a rebuild would describe the same library: both counts are present and neither moved.
    /// An empty library is not treated as settled, so a scan that has not loaded yet is tried again.
    /// </summary>
    internal static bool CountsUnchanged(int titles, int ingredients, int nextTitles, int nextIngredients)
        => titles > 0 && titles == nextTitles && ingredients == nextIngredients;

    public async Task<LibrarySnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var current = _snapshot;
        if (IsSettled(current))
        {
            return current!;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            current = _snapshot;
            if (IsSettled(current))
            {
                return current!;
            }

            _changed = false;
            var built = await Task.Run(() => Build(current), cancellationToken).ConfigureAwait(false);
            _snapshot = built;
            return built;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsSettled(LibrarySnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return false;
        }

        if (snapshot.Titles.Count == 0)
        {
            return !_changed && DateTime.UtcNow - snapshot.BuiltUtc < MinAgeWhenChanged;
        }

        return !_changed || DateTime.UtcNow - snapshot.BuiltUtc < MinAgeWhenChanged;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Movie or Series)
        {
            _changed = true;
            return;
        }

        if (e.Item is Episode episode)
        {
            AdjustEpisode(episode, 1);
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Movie or Series)
        {
            _changed = true;
            return;
        }

        if (e.Item is Episode episode)
        {
            AdjustEpisode(episode, -1);
        }
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Movie or Series && (e.UpdateReason & MetadataChange) != 0)
        {
            _changed = true;
        }
    }

    private void AdjustEpisode(Episode episode, int delta)
    {
        if ((episode.ParentIndexNumber ?? 1) <= 0)
        {
            return;
        }

        var snapshot = _snapshot;
        if (snapshot is null || !snapshot.Titles.TryGetValue(episode.SeriesId, out var series))
        {
            return;
        }

        lock (_episodeCounts)
        {
            series.EpisodeCount = Math.Max(0, series.EpisodeCount + delta);
        }
    }

    internal static TitleMetadata FromItem(BaseItem item, IReadOnlyList<PersonInfo> people)
    {
        return new TitleMetadata
        {
            Genres = item.Genres ?? [],
            Keywords = item.Tags ?? [],
            Studios = item.Studios ?? [],
            Actors = people
                .Where(p => p.Type == PersonKind.Actor)
                .OrderBy(p => p.SortOrder ?? int.MaxValue)
                .Select(p => p.Name)
                .ToList(),
            Directors = people
                .Where(p => p.Type is PersonKind.Director or PersonKind.Creator)
                .Select(p => p.Name)
                .ToList(),
            Writers = people.Where(p => p.Type == PersonKind.Writer).Select(p => p.Name).ToList(),
            OriginalLanguage = item.OriginalLanguage,
            Year = item.ProductionYear,
        };
    }

    private LibrarySnapshot Build(LibrarySnapshot? previous)
    {
        var started = DateTime.UtcNow;
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            IsVirtualItem = false,
        });

        var episodeCounts = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false,
            })
            .OfType<Episode>()
            .Where(e => (e.ParentIndexNumber ?? 1) > 0)
            .GroupBy(e => e.SeriesId)
            .ToDictionary(g => g.Key, g => g.Count());

        var titles = new Dictionary<Guid, LibraryTitle>();
        var byTmdb = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            var isSeries = item is Series;
            if (!isSeries && item is not Movie)
            {
                continue;
            }

            var raw = Features.Build(FromItem(item, _libraryManager.GetPeople(item)));
            var tmdbId = item.GetProviderId(MetadataProvider.Tmdb);
            var title = new LibraryTitle
            {
                Item = item,
                IsSeries = isSeries,
                TmdbId = string.IsNullOrWhiteSpace(tmdbId) ? null : tmdbId,
                RawFeatures = raw,
                EpisodeCount = isSeries ? episodeCounts.GetValueOrDefault(item.Id) : 0,
            };
            titles[item.Id] = title;
            if (title.TmdbId is not null)
            {
                byTmdb.TryAdd(title.MediaType + ":" + title.TmdbId, item.Id);
            }

            foreach (var key in raw.Keys)
            {
                documentFrequency[key] = documentFrequency.GetValueOrDefault(key) + 1;
            }
        }

        double count = Math.Max(1, titles.Count);
        var idf = documentFrequency.ToDictionary(
            pair => pair.Key,
            pair => Math.Log(1 + (count / (1 + pair.Value))),
            StringComparer.Ordinal);

        var snapshot = new LibrarySnapshot
        {
            Titles = titles,
            ByTmdb = byTmdb,
            Idf = idf,
            UnknownIdf = Math.Log(1 + count),
            BuiltUtc = DateTime.UtcNow,
        };

        foreach (var title in titles.Values)
        {
            title.Vector = snapshot.Vectorize(title.RawFeatures);
        }

        if (CountsUnchanged(previous?.Titles.Count ?? 0, previous?.Idf.Count ?? 0, titles.Count, idf.Count))
        {
            return previous!;
        }

        _logger.LogInformation(
            "Taste library index: {Count} titles ({Themed} with themes from tags), {Features} ingredients in {Seconds:F1}s",
            titles.Count,
            titles.Values.Count(t => t.RawFeatures.Keys.Any(k => Features.Category(k) == Features.Keyword)),
            idf.Count,
            (DateTime.UtcNow - started).TotalSeconds);
        return snapshot;
    }
}
