namespace Jellyfin.Plugin.Taste.Scoring;

/// <summary>
/// One episode of a series, as the finale check sees it.
/// </summary>
public sealed record EpisodeSlot(Guid Id, int Season, int Number, bool Played);

/// <summary>
/// Decides whether leaving the player should open the rating dialog.
/// </summary>
public static class PromptRules
{
    private static readonly long Tolerance = TimeSpan.FromSeconds(2).Ticks;

    /// <summary>
    /// Gets a value indicating whether playback stopped inside the credits.
    /// A detected outro is the only window once it exists; otherwise the last <paramref name="fallbackPercent"/> of the runtime counts.
    /// </summary>
    public static bool InCredits(long positionTicks, long? runtimeTicks, long? outroStartTicks, int fallbackPercent)
    {
        if (runtimeTicks is > 0 && positionTicks >= runtimeTicks.Value - Tolerance)
        {
            return true;
        }

        if (outroStartTicks.HasValue)
        {
            return positionTicks >= outroStartTicks.Value - Tolerance;
        }

        if (runtimeTicks is not > 0)
        {
            return false;
        }

        var percent = Math.Clamp(fallbackPercent, 1, 50);
        return positionTicks >= runtimeTicks.Value * (100 - percent) / 100;
    }

    /// <summary>
    /// Gets a value indicating whether finishing <paramref name="episodeId"/> finishes the show:
    /// it is the last regular episode, or every other regular episode is already played. Specials never count.
    /// </summary>
    public static bool IsFinale(Guid episodeId, IReadOnlyList<EpisodeSlot> episodes)
    {
        var regular = episodes
            .Where(e => e.Season > 0)
            .OrderBy(e => e.Season)
            .ThenBy(e => e.Number)
            .ToList();

        if (!regular.Any(e => e.Id == episodeId))
        {
            return false;
        }

        if (regular[^1].Id == episodeId)
        {
            return true;
        }

        return regular.Where(e => e.Id != episodeId).All(e => e.Played);
    }
}
