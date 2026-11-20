using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Taste.Services;

/// <summary>
/// Remembers which upstream failures have already been written at warning, so the same path is not logged again
/// for every person. A later success clears it, and the next failure of that path is logged once more.
/// </summary>
internal static class FailureLog
{
    private static readonly ConcurrentDictionary<string, byte> Seen = new(StringComparer.Ordinal);

    public static bool TryBegin(string key) => Seen.TryAdd(key, 0);

    public static void Clear(string keyPrefix)
    {
        foreach (var key in Seen.Keys)
        {
            if (key.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                Seen.TryRemove(key, out _);
            }
        }
    }
}
