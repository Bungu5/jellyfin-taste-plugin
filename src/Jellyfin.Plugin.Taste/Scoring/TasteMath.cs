namespace Jellyfin.Plugin.Taste.Scoring;

/// <summary>
/// Sparse-vector math and signal weights for the taste profile.
/// </summary>
public static class TasteMath
{
    /// <summary>
    /// Watching and ratings lose half their weight over a year: recent taste leads, but last year still counts.
    /// </summary>
    public const double HalfLifeDays = 365;
    public const double ThumbUpWeight = 2.0;
    public const double ThumbDownWeight = -2.5;
    public const double HeartWeight = 1.5;

    /// <summary>
    /// Finishing something is only a light hint: people mostly pick what they already expect to like.
    /// </summary>
    public const double FinishedWeight = 0.4;
    public const double RequestedWeight = 0.4;

    /// <summary>
    /// "Not for me": a mild dislike, weaker than a thumbs down (-2.5) or one star.
    /// </summary>
    public const double NotInterestedWeight = -0.6;

    /// <summary>
    /// Explicit scores fade to half their weight, never to nothing.
    /// </summary>
    public const double ExplicitDecayFloor = 0.5;

    /// <summary>
    /// Finished, favourited, or "not for me" titles never fade below a quarter of their weight.
    /// </summary>
    public const double ImplicitDecayFloor = 0.25;

    /// <summary>
    /// Maps Jellyfin's 0–10 rating to a signed taste weight. The dialog writes 2, 4, 6, 8, or 10.
    /// </summary>
    public static double StarWeight(double rating) => rating switch
    {
        >= 9 => 3.0,
        >= 7 => 1.5,
        >= 5 => 0.2,
        >= 3 => -1.5,
        _ => -3.0,
    };

    /// <summary>
    /// A person, studio, language, or era counts against titles only once this many separate dislikes share it.
    /// </summary>
    public const int RepeatedDislikes = 2;

    /// <summary>
    /// Even then it counts weakly: the dislike is mostly about what the titles were.
    /// </summary>
    public const double RepeatedDislikeScale = 0.35;

    /// <summary>
    /// The IDF of a value found in about one title in twenty. A category whose values are more common than that
    /// (English, drama, the 2010s) gets a smaller share of the title's vector.
    /// </summary>
    private static readonly double ReferenceIdf = Math.Log(21);

    public static double ThumbWeight(int thumb) => thumb > 0 ? ThumbUpWeight : ThumbDownWeight;

    public static double Decay(DateTime? when, DateTime now, double floor)
    {
        if (when is null)
        {
            return 1.0;
        }

        var ageDays = Math.Max(0, (now - when.Value).TotalDays);
        var decay = Math.Pow(0.5, ageDays / HalfLifeDays);
        return Math.Max(floor, decay);
    }

    /// <summary>
    /// Turns raw ingredients into a unit vector. Each category gets its <see cref="Features.CategoryWeight"/> share,
    /// less when its values are common in the library; inside a category, rarer values weigh more.
    /// </summary>
    public static Dictionary<string, double> Weighted(
        IReadOnlyDictionary<string, double> raw,
        IReadOnlyDictionary<string, double> idf,
        double unknownIdf)
    {
        var vector = new Dictionary<string, double>(raw.Count, StringComparer.Ordinal);
        foreach (var category in raw.GroupBy(p => Features.Category(p.Key), StringComparer.Ordinal))
        {
            var weighted = category
                .Select(p => (p.Key, Raw: p.Value, Idf: idf.GetValueOrDefault(p.Key, unknownIdf)))
                .Where(p => p.Raw > 0 && p.Idf > 0)
                .ToList();
            var length = Math.Sqrt(weighted.Sum(p => p.Raw * p.Idf * p.Raw * p.Idf));
            if (length <= 0)
            {
                continue;
            }

            var meanIdf = length / Math.Sqrt(weighted.Sum(p => p.Raw * p.Raw));
            var share = Features.CategoryWeight(category.Key) * Math.Min(1, meanIdf / ReferenceIdf);
            foreach (var (key, rawWeight, keyIdf) in weighted)
            {
                vector[key] = share * rawWeight * keyIdf / length;
            }
        }

        return Normalize(vector);
    }

    public static double Length(IReadOnlyDictionary<string, double> vector) => Math.Sqrt(vector.Values.Sum(v => v * v));

    /// <summary>
    /// Adds signals up into a taste vector and counts how many separate titles back each ingredient.
    /// A like credits everything in the title. A dislike lands on what the title is (genres, themes); people,
    /// studios, language, and era only count against it when <see cref="RepeatedDislikes"/> separate dislikes
    /// share them, and then weakly.
    /// </summary>
    /// <param name="signals">Each signal's title identity (the same title rated twice counts once), signed weight, and vector.</param>
    public static (Dictionary<string, double> Vector, Dictionary<string, FeatureSupport> Support) Profile(
        IEnumerable<(string Id, double Weight, IReadOnlyDictionary<string, double> Vector)> signals)
    {
        var list = signals.Where(s => s.Weight != 0 && s.Vector.Count > 0).ToList();
        var liked = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var disliked = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (id, weight, vector) in list)
        {
            var titles = weight > 0 ? liked : disliked;
            foreach (var (key, value) in vector)
            {
                if (value <= 0)
                {
                    continue;
                }

                if (!titles.TryGetValue(key, out var ids))
                {
                    titles[key] = ids = new HashSet<string>(StringComparer.Ordinal);
                }

                ids.Add(id);
            }
        }

        var profile = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (_, weight, vector) in list)
        {
            foreach (var (key, value) in vector)
            {
                var scale = 1.0;
                if (weight < 0 && !Features.IsSubject(Features.Category(key)))
                {
                    scale = disliked.TryGetValue(key, out var ids) && ids.Count >= RepeatedDislikes ? RepeatedDislikeScale : 0;
                }

                if (scale > 0)
                {
                    profile[key] = profile.GetValueOrDefault(key) + (value * weight * scale);
                }
            }
        }

        var support = profile.Keys.ToDictionary(
            key => key,
            key => new FeatureSupport(
                liked.TryGetValue(key, out var up) ? up.Count : 0,
                disliked.TryGetValue(key, out var down) ? down.Count : 0),
            StringComparer.Ordinal);
        return (profile, support);
    }

    public static Dictionary<string, double> Normalize(Dictionary<string, double> vector)
    {
        var length = Math.Sqrt(vector.Values.Sum(v => v * v));
        if (length <= 0)
        {
            return vector;
        }

        foreach (var key in vector.Keys.ToList())
        {
            vector[key] /= length;
        }

        return vector;
    }

    public static double Dot(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b)
    {
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        var sum = 0.0;
        foreach (var (key, value) in small)
        {
            if (large.TryGetValue(key, out var other))
            {
                sum += value * other;
            }
        }

        return sum;
    }

    public static double Cosine(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b)
        => Cosine(a, Length(a), b);

    /// <summary>
    /// Cosine with the first vector's length worked out once, for scoring many titles against one profile.
    /// </summary>
    public static double Cosine(IReadOnlyDictionary<string, double> a, double lengthA, IReadOnlyDictionary<string, double> b)
    {
        var lengthB = Length(b);
        return lengthA <= 0 || lengthB <= 0 ? 0 : Dot(a, b) / (lengthA * lengthB);
    }

    /// <summary>
    /// Gets the features that contribute most to the overlap of two vectors, largest first.
    /// </summary>
    public static IReadOnlyList<(string Key, double Contribution)> TopShared(
        IReadOnlyDictionary<string, double> a,
        IReadOnlyDictionary<string, double> b,
        int count)
    {
        return a.Where(pair => b.ContainsKey(pair.Key))
            .Select(pair => (pair.Key, Contribution: pair.Value * b[pair.Key]))
            .Where(pair => pair.Contribution > 0)
            .OrderByDescending(pair => pair.Contribution)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// Greedy maximal-marginal-relevance pick, so one franchise or director cannot fill a whole row.
    /// </summary>
    public static IReadOnlyList<T> Diversify<T>(
        IReadOnlyList<T> ranked,
        Func<T, double> score,
        Func<T, IReadOnlyDictionary<string, double>> vector,
        int take,
        double lambda = 0.35)
    {
        var remaining = ranked.ToList();
        var picked = new List<T>(Math.Min(take, remaining.Count));
        while (picked.Count < take && remaining.Count > 0)
        {
            var bestIndex = 0;
            var bestValue = double.NegativeInfinity;
            for (var i = 0; i < remaining.Count; i++)
            {
                var candidate = remaining[i];
                var redundancy = picked.Count == 0
                    ? 0
                    : picked.Max(p => Dot(vector(p), vector(candidate)));
                var value = score(candidate) - (lambda * redundancy);
                if (value > bestValue)
                {
                    bestValue = value;
                    bestIndex = i;
                }
            }

            picked.Add(remaining[bestIndex]);
            remaining.RemoveAt(bestIndex);
        }

        return picked;
    }
}

/// <summary>
/// How many separate liked and disliked titles carry an ingredient.
/// </summary>
public readonly record struct FeatureSupport(int Liked, int Disliked);
