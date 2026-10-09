using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net.Mime;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Taste.Data;
using Jellyfin.Plugin.Taste.Scoring;
using Jellyfin.Plugin.Taste.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Taste.Api;

public sealed class RateBody
{
    [Required]
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets 1–5 stars, or null to leave the score alone.
    /// </summary>
    [Range(1, 5)]
    public int? Stars { get; set; }

    /// <summary>
    /// Gets or sets 1 for thumbs up, -1 for thumbs down, or null.
    /// </summary>
    [Range(-1, 1)]
    public int? Thumb { get; set; }
}

public sealed class SeenAnswerBody
{
    [Required]
    public string DeckKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets true for seen, false for not seen, null for skip.
    /// </summary>
    public bool? Seen { get; set; }

    [Range(1, 5)]
    public int? Stars { get; set; }

    [Range(-1, 1)]
    public int? Thumb { get; set; }

    public string? Title { get; set; }

    public int? Year { get; set; }
}

public sealed class TitleKeyBody
{
    [Required]
    public string Key { get; set; } = string.Empty;

    public string? Title { get; set; }

    public int? Year { get; set; }
}

/// <summary>
/// A title someone marked "not for me".
/// </summary>
public sealed record DismissedTitle(string Key, string Title, int? Year, string? MediaType, string? ItemId, string? ImageUrl);

public sealed class SeerrRequestBody
{
    [Required]
    public string MediaType { get; set; } = string.Empty;

    [Required]
    public int TmdbId { get; set; }
}

public sealed record ProfileFeature(string Label, string Category, double Weight);

public sealed record ProfileSummary(
    int Signals,
    int Scored,
    int Finished,
    IReadOnlyList<ProfileFeature> Likes,
    IReadOnlyList<ProfileFeature> Dislikes);

public sealed record TasteStatus(bool Discover, string? DiscoverNotice, bool Seerr, string? Source, bool HomeRows);

/// <summary>
/// Endpoints for the rating dialog and the Taste page.
/// </summary>
[ApiController]
[Route("Taste")]
public class TasteController : ControllerBase
{
    private const string UserIdClaim = "Jellyfin-UserId";
    private const double MinChipWeight = 0.02;

    private readonly IUserManager _userManager;
    private readonly PromptService _prompts;
    private readonly RatingService _ratings;
    private readonly ProfileService _profiles;
    private readonly RecommendationService _recommendations;
    private readonly DiscoveryService _discovery;
    private readonly CalibrationService _calibration;
    private readonly SeerrClient _seerr;
    private readonly TmdbClient _tmdb;
    private readonly LibraryIndex _index;

    public TasteController(
        IUserManager userManager,
        PromptService prompts,
        RatingService ratings,
        ProfileService profiles,
        RecommendationService recommendations,
        DiscoveryService discovery,
        CalibrationService calibration,
        SeerrClient seerr,
        TmdbClient tmdb,
        LibraryIndex index)
    {
        _userManager = userManager;
        _prompts = prompts;
        _ratings = ratings;
        _profiles = profiles;
        _recommendations = recommendations;
        _discovery = discovery;
        _calibration = calibration;
        _seerr = seerr;
        _tmdb = tmdb;
        _index = index;
    }

    [HttpGet("client.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult Client() => Embedded("Web.client.js", "application/javascript");

    [HttpGet("Page")]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Text.Html)]
    public ActionResult Page() => Embedded("Web.page.html", MediaTypeNames.Text.Html);

    [HttpGet("Status")]
    [Authorize]
    public ActionResult<TasteStatus> Status()
    {
        var config = Plugin.Instance!.Configuration;
        string? notice = !config.EnableDiscover ? "switched off"
            : !_tmdb.IsConfigured ? "needs Seerr or a TMDB key"
            : null;
        return new TasteStatus(notice is null, notice, _seerr.IsConfigured, _tmdb.Source, config.ShowHomeRows);
    }

    /// <summary>
    /// Called by the player script after playback stops.
    /// </summary>
    [HttpGet("Prompt")]
    [Authorize]
    public async Task<ActionResult<PromptDecision>> Prompt([FromQuery, Required] Guid itemId, [FromQuery] long? positionTicks)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        return await _prompts.DecideAsync(user, itemId, positionTicks).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves the dialog answer. Sending neither stars nor a thumb counts as "not now"; the title is still marked asked.
    /// </summary>
    [HttpPost("Rate")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Rate([FromBody] RateBody body)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        return _ratings.Rate(user, body.ItemId, body.Stars, body.Thumb)
            ? NoContent()
            : BadRequest("Only movies and series can be rated.");
    }

    [HttpGet("Profile")]
    [Authorize]
    public async Task<ActionResult<ProfileSummary>> Profile(CancellationToken cancellationToken)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var profile = await _profiles.GetAsync(user, cancellationToken).ConfigureAwait(false);
        return Summarize(profile);
    }

    /// <summary>
    /// Picks the profile chips. Genres show from one title; themes, people, studios, language, and era only once
    /// two separate titles back them. Dislikes list what the titles were before who made them.
    /// </summary>
    internal static ProfileSummary Summarize(UserProfile profile)
    {
        var features = profile.Vector
            .Where(p => Math.Abs(p.Value) >= MinChipWeight)
            .Where(p =>
            {
                var support = profile.Support.GetValueOrDefault(p.Key);
                var titles = p.Value > 0 ? support.Liked : support.Disliked;
                return titles >= (Features.Category(p.Key) == Features.Genre ? 1 : 2);
            })
            .Select(p => (p.Key, p.Value, Subject: Features.IsSubject(Features.Category(p.Key))))
            .ToList();

        ProfileFeature Chip((string Key, double Value, bool Subject) f)
        {
            var category = Features.Category(f.Key);
            return new ProfileFeature(
                ReasonWriter.Display(f.Key),
                category == Features.Keyword ? "theme" : Features.Noun(category),
                Math.Round(f.Value, 3));
        }

        var likes = features
            .Where(f => f.Value > 0)
            .OrderByDescending(f => f.Value)
            .Select(Chip)
            .DistinctBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .Take(18)
            .ToList();
        var disliked = features.Where(f => f.Value < 0).OrderBy(f => f.Value).ToList();
        var dislikes = disliked.Where(f => f.Subject).Take(8)
            .Concat(disliked.Where(f => !f.Subject).Take(3))
            .Select(Chip)
            .DistinctBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ProfileSummary(
            profile.Signals.Count,
            profile.Signals.Count(s => s.IsExplicit),
            profile.Signals.Count(s => s.Finished),
            likes,
            dislikes);
    }

    /// <summary>
    /// Gets a row: <c>movies</c> or <c>shows</c> from the library; <c>new-movies</c> / <c>new-shows</c> (released in
    /// the last few months) or <c>request-movies</c> / <c>request-shows</c> (older) for titles not on disk.
    /// </summary>
    [HttpGet("Recommendations")]
    [Authorize]
    public async Task<ActionResult<RecommendationRow>> Recommendations([FromQuery, Required] string shelf, CancellationToken cancellationToken)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        return shelf switch
        {
            "movies" => await _recommendations.LibraryAsync(user, false, cancellationToken).ConfigureAwait(false),
            "shows" => await _recommendations.LibraryAsync(user, true, cancellationToken).ConfigureAwait(false),
            "new-movies" => await _discovery.GetAsync(user, true, "movie", cancellationToken).ConfigureAwait(false),
            "new-shows" => await _discovery.GetAsync(user, true, "tv", cancellationToken).ConfigureAwait(false),
            "request-movies" => await _discovery.GetAsync(user, false, "movie", cancellationToken).ConfigureAwait(false),
            "request-shows" => await _discovery.GetAsync(user, false, "tv", cancellationToken).ConfigureAwait(false),
            _ => BadRequest("shelf must be movies, shows, new-movies, new-shows, request-movies, or request-shows"),
        };
    }

    [HttpPost("NotInterested")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult NotInterested([FromBody] TitleKeyBody body)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        if (!body.Key.StartsWith("item:", StringComparison.Ordinal) && !body.Key.StartsWith("tmdb:", StringComparison.Ordinal))
        {
            return BadRequest("Unknown title key.");
        }

        _ratings.NotInterested(user, body.Key, body.Title, body.Year);
        _discovery.Invalidate(user.Id);
        return NoContent();
    }

    [HttpPost("NotInterested/Undo")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult UndoNotInterested([FromBody] TitleKeyBody body)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        _ratings.Undo(user, body.Key);
        _discovery.Invalidate(user.Id);
        return NoContent();
    }

    [HttpGet("NotInterested")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DismissedTitle>>> GetNotInterested(CancellationToken cancellationToken)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var snapshot = await _index.GetAsync(cancellationToken).ConfigureAwait(false);
        var answers = _ratings.Dismissed(user).Take(100).ToList();
        var titles = await Task.WhenAll(answers.Select(async answer =>
        {
            var itemId = TasteStore.ParseLibraryKey(answer.TitleKey);
            if (itemId is { } id)
            {
                return snapshot.Titles.TryGetValue(id, out var item)
                    ? new DismissedTitle(answer.TitleKey, item.Name, item.Year, item.MediaType, id.ToString("N", CultureInfo.InvariantCulture), null)
                    : null;
            }

            var (keyType, keyId) = TasteStore.ParseTmdbKey(answer.TitleKey);
            var mediaType = answer.MediaType ?? keyType;
            var tmdbId = answer.TmdbId ?? keyId;
            if (mediaType is null || tmdbId is null)
            {
                return null;
            }

            var details = await _tmdb.GetTitleAsync(mediaType, tmdbId, cancellationToken).ConfigureAwait(false);
            return new DismissedTitle(
                answer.TitleKey,
                answer.Title ?? details?.Title ?? "Unknown title",
                answer.Year ?? details?.Year,
                mediaType,
                null,
                TmdbClient.PosterUrl(details?.PosterPath));
        })).ConfigureAwait(false);
        return Ok(titles.OfType<DismissedTitle>().ToList());
    }

    [HttpGet("Calibration/Seen")]
    [Authorize]
    public async Task<ActionResult<CalibrationSet>> CalibrationSeen(CancellationToken cancellationToken)
    {
        var user = CurrentUser();
        return user is null ? Unauthorized() : await _calibration.SeenDeckAsync(user, cancellationToken).ConfigureAwait(false);
    }

    [HttpPost("Calibration/Seen")]
    [Authorize]
    public async Task<ActionResult<int>> AnswerSeen([FromBody] SeenAnswerBody body, CancellationToken cancellationToken)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        try
        {
            return await _calibration
                .AnswerSeenAsync(user, body.DeckKey, body.Seen, body.Stars, body.Thumb, body.Title, body.Year, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("Calibration/Finished")]
    [Authorize]
    public async Task<ActionResult<CalibrationSet>> CalibrationFinished(CancellationToken cancellationToken)
    {
        var user = CurrentUser();
        return user is null ? Unauthorized() : await _calibration.FinishedAsync(user, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Requests a title in Seerr as this person. Seerr's quotas and approval rules decide what happens next.
    /// </summary>
    [HttpPost("Request")]
    [Authorize]
    public async Task<ActionResult<SeerrRequestResult>> RequestInSeerr([FromBody] SeerrRequestBody body, CancellationToken cancellationToken)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        if (body.MediaType is not ("movie" or "tv"))
        {
            return BadRequest("mediaType must be movie or tv");
        }

        if (!_seerr.IsConfigured)
        {
            return new SeerrRequestResult(false, "Seerr is not set up in the Taste plugin settings.");
        }

        var seerrUserId = await _seerr.FindUserIdAsync(user, cancellationToken).ConfigureAwait(false);
        if (seerrUserId is null)
        {
            return new SeerrRequestResult(false, "Sign in to Seerr once with this Jellyfin account, then try again.");
        }

        var result = await _seerr.RequestAsync(seerrUserId.Value, body.MediaType, body.TmdbId, cancellationToken).ConfigureAwait(false);
        if (result.Ok)
        {
            _profiles.Invalidate(user.Id);
            _discovery.Invalidate(user.Id);
        }

        return result;
    }

    private User? CurrentUser()
    {
        var claim = User.FindFirst(UserIdClaim)?.Value;
        return Guid.TryParse(claim, CultureInfo.InvariantCulture, out var id) ? _userManager.GetUserById(id) : null;
    }

    private FileStreamResult Embedded(string resource, string contentType)
    {
        var stream = GetType().Assembly.GetManifestResourceStream("Jellyfin.Plugin.Taste." + resource)
                     ?? throw new FileNotFoundException(resource);
        Response.Headers.CacheControl = "no-cache";
        return File(stream, contentType);
    }
}
