using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Taste.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the TMDB v3 API key or v4 read access token. Optional: without it, title data comes through Seerr.
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how close to the end (percent of runtime) counts as the credits when a title has no outro segment.
    /// </summary>
    public int FallbackWindowPercent { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether the rows of titles outside the library are shown.
    /// </summary>
    public bool EnableDiscover { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Taste rows are added to the Jellyfin Web home screen.
    /// </summary>
    public bool ShowHomeRows { get; set; } = true;

    /// <summary>
    /// Gets or sets the Trakt app client ID. Adds "fans also liked" lists and trending/popular new titles.
    /// </summary>
    public string TraktClientId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the MDBList API key. Adds IMDb, Rotten Tomatoes, Metacritic, and Trakt scores as a quality gate.
    /// </summary>
    public string MdbListApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Seerr (Jellyseerr or Overseerr) base URL, for example <c>http://seerr:5055</c>.
    /// </summary>
    public string SeerrUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Seerr API key from Seerr's Settings → General.
    /// </summary>
    public string SeerrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether a series request asks Seerr for every season instead of season 1.
    /// </summary>
    public bool SeerrRequestAllSeasons { get; set; }
}
