using System.Globalization;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Taste.Data;

/// <summary>
/// One person's answer about one title. Library stars live in Jellyfin's UserData.Rating, not here.
/// </summary>
public sealed record Answer
{
    public required string UserId { get; init; }

    /// <summary>
    /// Gets the title key: <c>item:{guid}</c> for library titles, <c>tmdb:movie:{id}</c> or <c>tmdb:tv:{id}</c> otherwise.
    /// </summary>
    public required string TitleKey { get; init; }

    /// <summary>
    /// Gets the 1–5 star score for titles that are not in the library.
    /// </summary>
    public int? Stars { get; init; }

    /// <summary>
    /// Gets the thumb: 1 is up, -1 is down.
    /// </summary>
    public int? Thumb { get; init; }

    /// <summary>
    /// Gets a value indicating whether the finish dialog has already been shown for this title.
    /// </summary>
    public bool Asked { get; init; }

    public bool NotInterested { get; init; }

    public string? Title { get; init; }

    public string? MediaType { get; init; }

    public string? TmdbId { get; init; }

    public int? Year { get; init; }

    public DateTime UpdatedUtc { get; init; }
}

/// <summary>
/// The plugin's SQLite database in Jellyfin's data directory.
/// </summary>
public sealed class TasteStore
{
    private readonly string _connectionString;
    private readonly Lock _writeLock = new();

    public TasteStore(IApplicationPaths paths)
        : this(Path.Combine(paths.DataPath, "taste.db"))
    {
    }

    internal TasteStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
        }.ToString();
        Initialize();
    }

    public static string LibraryKey(Guid itemId) => "item:" + itemId.ToString("N", CultureInfo.InvariantCulture);

    public static string TmdbKey(string mediaType, string tmdbId) => $"tmdb:{mediaType}:{tmdbId}";

    /// <summary>
    /// Splits <c>tmdb:tv:123</c> into its media type and id; nulls for any other key.
    /// </summary>
    public static (string? MediaType, string? TmdbId) ParseTmdbKey(string key)
    {
        var parts = key.Split(':');
        return parts.Length == 3 && parts[0] == "tmdb" && parts[1] is "movie" or "tv" && parts[2].Length > 0
            ? (parts[1], parts[2])
            : (null, null);
    }

    /// <summary>
    /// Gets the library item id from <c>item:{guid}</c>; null for any other key.
    /// </summary>
    public static Guid? ParseLibraryKey(string key)
        => key.StartsWith("item:", StringComparison.Ordinal) && Guid.TryParse(key.AsSpan(5), out var id) ? id : null;

    public IReadOnlyDictionary<string, Answer> GetAnswers(Guid userId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT title_key, stars, thumb, asked, not_interested, title, media_type, tmdb_id, year, updated_utc " +
            "FROM answers WHERE user_id = $user";
        command.Parameters.AddWithValue("$user", UserKey(userId));
        var result = new Dictionary<string, Answer>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var answer = ReadAnswer(userId, reader);
            result[answer.TitleKey] = answer;
        }

        return result;
    }

    public Answer GetAnswer(Guid userId, string titleKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT title_key, stars, thumb, asked, not_interested, title, media_type, tmdb_id, year, updated_utc " +
            "FROM answers WHERE user_id = $user AND title_key = $key";
        command.Parameters.AddWithValue("$user", UserKey(userId));
        command.Parameters.AddWithValue("$key", titleKey);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? ReadAnswer(userId, reader)
            : new Answer { UserId = UserKey(userId), TitleKey = titleKey };
    }

    /// <summary>
    /// Reads, changes, and writes one answer as a single step.
    /// </summary>
    public Answer Update(Guid userId, string titleKey, Func<Answer, Answer> change)
    {
        lock (_writeLock)
        {
            var updated = change(GetAnswer(userId, titleKey)) with { UpdatedUtc = DateTime.UtcNow };
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT OR REPLACE INTO answers " +
                "(user_id, title_key, stars, thumb, asked, not_interested, title, media_type, tmdb_id, year, updated_utc) " +
                "VALUES ($user, $key, $stars, $thumb, $asked, $ni, $title, $type, $tmdb, $year, $updated)";
            command.Parameters.AddWithValue("$user", UserKey(userId));
            command.Parameters.AddWithValue("$key", titleKey);
            command.Parameters.AddWithValue("$stars", (object?)updated.Stars ?? DBNull.Value);
            command.Parameters.AddWithValue("$thumb", (object?)updated.Thumb ?? DBNull.Value);
            command.Parameters.AddWithValue("$asked", updated.Asked ? 1 : 0);
            command.Parameters.AddWithValue("$ni", updated.NotInterested ? 1 : 0);
            command.Parameters.AddWithValue("$title", (object?)updated.Title ?? DBNull.Value);
            command.Parameters.AddWithValue("$type", (object?)updated.MediaType ?? DBNull.Value);
            command.Parameters.AddWithValue("$tmdb", (object?)updated.TmdbId ?? DBNull.Value);
            command.Parameters.AddWithValue("$year", (object?)updated.Year ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated", updated.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
            return updated;
        }
    }

    /// <summary>
    /// Gets calibration deck titles already shown to a user, with their answer: yes, no, or skip.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetCalibrationShown(Guid userId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT deck_key, answer FROM calibration_shown WHERE user_id = $user";
        command.Parameters.AddWithValue("$user", UserKey(userId));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    public void MarkCalibrationShown(Guid userId, string deckKey, string answer)
    {
        lock (_writeLock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT OR REPLACE INTO calibration_shown (user_id, deck_key, answer, shown_utc) " +
                "VALUES ($user, $key, $answer, $now)";
            command.Parameters.AddWithValue("$user", UserKey(userId));
            command.Parameters.AddWithValue("$key", deckKey);
            command.Parameters.AddWithValue("$answer", answer);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
    }

    public string? GetCached(string cacheKey, TimeSpan maxAge)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json, fetched_utc FROM tmdb_cache WHERE cache_key = $key";
        command.Parameters.AddWithValue("$key", cacheKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var fetched = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return DateTime.UtcNow - fetched > maxAge ? null : reader.GetString(0);
    }

    public void PutCached(string cacheKey, string json)
    {
        lock (_writeLock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT OR REPLACE INTO tmdb_cache (cache_key, json, fetched_utc) VALUES ($key, $json, $now)";
            command.Parameters.AddWithValue("$key", cacheKey);
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Deletes cached responses fetched before <paramref name="cutoffUtc"/>.
    /// </summary>
    /// <returns>How many entries were deleted.</returns>
    public int PruneCache(DateTime cutoffUtc)
    {
        lock (_writeLock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM tmdb_cache WHERE fetched_utc < $cutoff";
            command.Parameters.AddWithValue("$cutoff", cutoffUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            return command.ExecuteNonQuery();
        }
    }

    private static string UserKey(Guid userId) => userId.ToString("N", CultureInfo.InvariantCulture);

    private static Answer ReadAnswer(Guid userId, SqliteDataReader reader)
    {
        return new Answer
        {
            UserId = UserKey(userId),
            TitleKey = reader.GetString(0),
            Stars = reader.IsDBNull(1) ? null : reader.GetInt32(1),
            Thumb = reader.IsDBNull(2) ? null : reader.GetInt32(2),
            Asked = reader.GetInt32(3) != 0,
            NotInterested = reader.GetInt32(4) != 0,
            Title = reader.IsDBNull(5) ? null : reader.GetString(5),
            MediaType = reader.IsDBNull(6) ? null : reader.GetString(6),
            TmdbId = reader.IsDBNull(7) ? null : reader.GetString(7),
            Year = reader.IsDBNull(8) ? null : reader.GetInt32(8),
            UpdatedUtc = DateTime.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        };
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS answers (
                user_id TEXT NOT NULL,
                title_key TEXT NOT NULL,
                stars INTEGER,
                thumb INTEGER,
                asked INTEGER NOT NULL DEFAULT 0,
                not_interested INTEGER NOT NULL DEFAULT 0,
                title TEXT,
                media_type TEXT,
                tmdb_id TEXT,
                year INTEGER,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (user_id, title_key)
            );
            CREATE TABLE IF NOT EXISTS calibration_shown (
                user_id TEXT NOT NULL,
                deck_key TEXT NOT NULL,
                answer TEXT NOT NULL,
                shown_utc TEXT NOT NULL,
                PRIMARY KEY (user_id, deck_key)
            );
            CREATE TABLE IF NOT EXISTS tmdb_cache (
                cache_key TEXT PRIMARY KEY,
                json TEXT NOT NULL,
                fetched_utc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }
}
