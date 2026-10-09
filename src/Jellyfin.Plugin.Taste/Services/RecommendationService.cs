using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Taste.Data;
using Jellyfin.Plugin.Taste.Scoring;

namespace Jellyfin.Plugin.Taste.Services;

public sealed record Recommendation(
    string Key,
    string Title,
    int? Year,
    string MediaType,
    Guid? ItemId,
    string? TmdbId,
    string? ImageUrl,
    string? Overview,
    string Reason,
    double Score,
    string? Meta = null,
    string? Badge = null,
    string? Status = null,
    string? Link = null);

public sealed record RecommendationRow(IReadOnlyList<Recommendation> Items, string? Notice);

/// <summary>
/// Ranks library titles against a taste profile. Titles outside the library are <see cref="DiscoveryService"/>.
/// </summary>
public sealed class RecommendationService
{
    private const double QualityPrior = 0.05;
    private const double RecentReleaseNudge = 0.02;
    private const double RecentlyAddedNudge = 0.02;
    private const double NeighbourWeight = 0.25;
    private const int RowSize = 20;

    private readonly LibraryIndex _index;
    private readonly ProfileService _profiles;
    private readonly NeighbourService _neighbours;

    public RecommendationService(LibraryIndex index, ProfileService profiles, NeighbourService neighbours)
    {
        _index = index;
        _profiles = profiles;
        _neighbours = neighbours;
    }

    public async Task<RecommendationRow> LibraryAsync(User user, bool series, CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetAsync(user, cancellationToken).ConfigureAwait(false);
        if (!profile.HasTaste)
        {
            return new RecommendationRow([], "Rate a few titles first, then this row fills in.");
        }

        var snapshot = await _index.GetAsync(cancellationToken).ConfigureAwait(false);
        var neighbours = await _neighbours.GetAsync(profile, cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        Neighbour? NeighbourOf(LibraryTitle t) => t.TmdbId is null ? null : neighbours.GetValueOrDefault(t.MediaType + ":" + t.TmdbId);
        var ranked = snapshot.Titles.Values
            .Where(t => t.IsSeries == series)
            .Where(t => !profile.SeenLibrary.Contains(t.Id))
            .Where(t => t.TmdbId is null || !profile.SeenTmdb.Contains(t.MediaType + ":" + t.TmdbId))
            .Where(t => !profile.Excluded.Contains(TasteStore.LibraryKey(t.Id)))
            .Select(t => (Title: t, Neighbour: NeighbourOf(t)))
            .Select(p => (p.Title, p.Neighbour, Score: Score(
                profile,
                p.Title.Vector,
                p.Title.Item.CommunityRating,
                p.Title.Item.PremiereDate,
                p.Title.Item.DateCreated,
                now,
                p.Neighbour?.Support ?? 0)))
            .Where(p => p.Score > 0)
            .OrderByDescending(p => p.Score)
            .Take(200)
            .Where(p => p.Title.Item.IsVisibleStandalone(user))
            .Take(120)
            .ToList();

        var picked = TasteMath.Diversify(ranked, p => p.Score, p => p.Title.Vector, RowSize);
        var rows = picked
            .Select(p => (
                Key: TasteStore.LibraryKey(p.Title.Id),
                p.Title.Name,
                p.Title.Year,
                Reason: p.Neighbour is { Support: > 0, Because.Count: > 0 } n
                    ? ReasonWriter.FansAlsoLiked(n.Because)
                    : ReasonWriter.Explain(profile, p.Title.Vector)))
            .ToList();

        return new RecommendationRow(
            picked.Select((p, i) => new Recommendation(
                    rows[i].Key,
                    p.Title.Name,
                    p.Title.Year,
                    p.Title.MediaType,
                    p.Title.Id,
                    p.Title.TmdbId,
                    null,
                    p.Title.Item.Overview,
                    rows[i].Reason,
                    Math.Round(p.Score, 4)))
                .ToList(),
            picked.Count == 0 ? "Nothing in the library matches yet." : null);
    }

    /// <summary>
    /// Ingredient overlap plus a boost when fans of the person's favourites also liked the title; quality and
    /// recency only separate near-ties. Titles listed next to disliked ones are pushed down.
    /// </summary>
    internal static double Score(
        UserProfile profile,
        IReadOnlyDictionary<string, double> vector,
        float? communityRating,
        DateTime? released,
        DateTime? added,
        DateTime now,
        double neighbourSupport = 0)
    {
        var score = TasteMath.Cosine(profile.Vector, profile.Length, vector);
        score += neighbourSupport >= 0
            ? NeighbourWeight * NeighbourService.Strength(neighbourSupport)
            : NeighbourWeight * Math.Max(-1, neighbourSupport);
        score += QualityPrior * Math.Clamp((communityRating ?? 6f) / 10.0, 0, 1);
        if (released.HasValue && released.Value > now.AddYears(-3))
        {
            score += RecentReleaseNudge;
        }

        if (added.HasValue && added.Value > now.AddDays(-60))
        {
            score += RecentlyAddedNudge;
        }

        return score;
    }
}
