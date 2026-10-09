using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Taste.Data;
using Jellyfin.Plugin.Taste.Scoring;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;

namespace Jellyfin.Plugin.Taste.Services;

public sealed record PromptDecision(
    bool Prompt,
    string? Why,
    Guid? TargetId = null,
    string? Name = null,
    string? MediaType = null,
    int? Year = null,
    int? Stars = null,
    int? Thumb = null);

/// <summary>
/// Decides whether a stop should open the rating dialog: a movie left in the credits,
/// or the last episode of a show left in the credits. Each title is asked once.
/// </summary>
public sealed class PromptService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IMediaSegmentManager _segments;
    private readonly TasteStore _store;

    public PromptService(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IMediaSegmentManager segments,
        TasteStore store)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _segments = segments;
        _store = store;
    }

    public async Task<PromptDecision> DecideAsync(User user, Guid itemId, long? positionTicks)
    {
        var item = _libraryManager.GetItemById(itemId);
        BaseItem target;
        switch (item)
        {
            case Movie:
                target = item;
                break;
            case Episode episode:
                var series = episode.Series;
                if (series is null)
                {
                    return new PromptDecision(false, "episode has no series");
                }

                if (!PromptRules.IsFinale(episode.Id, EpisodeSlots(user, series)))
                {
                    return new PromptDecision(false, "not the last episode");
                }

                target = series;
                break;
            default:
                return new PromptDecision(false, "not a movie or episode");
        }

        var position = positionTicks ?? item.RunTimeTicks ?? 0;
        var outroStart = await OutroStartAsync(item).ConfigureAwait(false);
        var fallback = Plugin.Instance?.Configuration.FallbackWindowPercent ?? 10;
        if (!PromptRules.InCredits(position, item.RunTimeTicks, outroStart, fallback))
        {
            return new PromptDecision(false, "left before the credits");
        }

        var answer = _store.GetAnswer(user.Id, TasteStore.LibraryKey(target.Id));
        var rating = _userDataManager.GetUserData(user, target)?.Rating;
        if (answer.Asked)
        {
            return new PromptDecision(false, "already asked");
        }

        if (rating.HasValue && answer.Thumb.HasValue)
        {
            return new PromptDecision(false, "already rated");
        }

        return new PromptDecision(
            true,
            null,
            target.Id,
            target.Name,
            target is Series ? "Series" : "Movie",
            target.ProductionYear,
            rating.HasValue ? (int)Math.Clamp(Math.Round(rating.Value / 2, MidpointRounding.AwayFromZero), 1, 5) : null,
            answer.Thumb);
    }

    private List<EpisodeSlot> EpisodeSlots(User user, Series series)
    {
        var episodes = series.GetEpisodes(user, new DtoOptions(false), false)
            .OfType<Episode>()
            .Where(e => !e.IsVirtualItem)
            .ToList();
        var data = _userDataManager.GetUserDataBatch(episodes.Cast<BaseItem>().ToList(), user);
        return episodes
            .Select(e => new EpisodeSlot(
                e.Id,
                e.ParentIndexNumber ?? 1,
                e.IndexNumber ?? 0,
                data.TryGetValue(e.Id, out var d) && d.Played))
            .ToList();
    }

    private async Task<long?> OutroStartAsync(BaseItem item)
    {
        var segments = await _segments
            .GetSegmentsAsync(item, [MediaSegmentType.Outro], _libraryManager.GetLibraryOptions(item))
            .ConfigureAwait(false);
        var halfway = (item.RunTimeTicks ?? 0) / 2;
        return segments
            .Where(s => s.StartTicks >= halfway)
            .Select(s => (long?)s.StartTicks)
            .Min();
    }
}
