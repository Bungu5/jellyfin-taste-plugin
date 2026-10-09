using System.Globalization;
using Jellyfin.Plugin.Taste.Scoring;

namespace Jellyfin.Plugin.Taste.Services;

/// <summary>
/// Writes the one-line "why" under a recommendation, from the signal it most resembles.
/// </summary>
public static class ReasonWriter
{
    public static string Explain(UserProfile profile, IReadOnlyDictionary<string, double> candidate)
    {
        var best = profile.Signals
            .Where(s => s.Weight > 0 && s.Vector.Count > 0)
            .Select(s => (Signal: s, Strength: TasteMath.Dot(s.Vector, candidate) * s.Weight))
            .OrderByDescending(p => p.Strength)
            .FirstOrDefault();

        if (best.Signal is not null && best.Strength > 0.02)
        {
            var nouns = TasteMath.TopShared(best.Signal.Vector, candidate, 6)
                .Select(p => Features.Category(p.Key))
                .Distinct()
                .Take(2)
                .Select(Features.Noun)
                .ToList();
            return nouns.Count == 0
                ? Because(best.Signal) + "."
                : $"{Because(best.Signal)}, and this shares the {JoinAnd(nouns)}.";
        }

        var labels = TasteMath.TopShared(profile.Vector, candidate, 3).Select(p => Display(p.Key)).ToList();
        return labels.Count == 0 ? "Close to what you watch." : $"Matches your taste for {JoinAnd(labels)}.";
    }

    /// <summary>
    /// Gets "Fans of X and Y also liked this" from the favourites that list the title, strongest first.
    /// </summary>
    public static string FansAlsoLiked(IReadOnlyList<string> favourites)
        => "Fans of " + JoinAnd(favourites.Take(2).ToList()) + " also liked this";

    public static string Display(string key)
    {
        var label = Features.Label(key);
        return Features.Category(key) switch
        {
            Features.Actor or Features.Director or Features.Writer or Features.Studio
                => Features.OriginalName(key) ?? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(label),
            Features.Language => LanguageName(label),
            _ => label,
        };
    }

    public static string LanguageName(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    private static string Because(Signal signal)
    {
        var title = signal.Title;
        if (signal.Rating.HasValue)
        {
            var stars = (int)Math.Clamp(Math.Round(signal.Rating.Value / 2, MidpointRounding.AwayFromZero), 1, 5);
            var text = $"You gave {title} {stars} star{(stars == 1 ? string.Empty : "s")}";
            return signal.Thumb > 0 ? text + " and a thumbs up" : text;
        }

        if (signal.Thumb > 0)
        {
            return $"You gave {title} a thumbs up";
        }

        if (signal.Heart)
        {
            return $"You favourited {title}";
        }

        if (signal.Requested)
        {
            return $"You requested {title}";
        }

        return $"You finished {title}";
    }

    private static string JoinAnd(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => string.Empty,
        1 => parts[0],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };
}
