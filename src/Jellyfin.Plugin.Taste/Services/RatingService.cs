using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Taste.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Taste.Services;

/// <summary>
/// Writes stars to Jellyfin's UserData.Rating and thumbs to the plugin database.
/// </summary>
public sealed class RatingService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly TasteStore _store;
    private readonly ProfileService _profiles;

    public RatingService(ILibraryManager libraryManager, IUserDataManager userDataManager, TasteStore store, ProfileService profiles)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _store = store;
        _profiles = profiles;
    }

    /// <summary>
    /// Saves a dialog or calibration answer for a library movie or series. Stars are 1–5 and stored as 2–10.
    /// </summary>
    /// <returns>False when the item is not a movie or series.</returns>
    public bool Rate(User user, Guid itemId, int? stars, int? thumb)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is not Movie and not Series)
        {
            return false;
        }

        if (stars is >= 1 and <= 5)
        {
            _userDataManager.SaveUserData(
                user,
                item,
                new UpdateUserItemDataDto { Rating = stars.Value * 2 },
                UserDataSaveReason.UpdateUserRating);
        }

        _store.Update(user.Id, TasteStore.LibraryKey(item.Id), answer => answer with
        {
            Thumb = thumb is 1 or -1 ? thumb : answer.Thumb,
            Asked = true,
            Title = item.Name,
            MediaType = item is Series ? "tv" : "movie",
            TmdbId = item.GetProviderId(MetadataProvider.Tmdb) ?? answer.TmdbId,
            Year = item.ProductionYear,
        });
        _profiles.Invalidate(user.Id);
        return true;
    }

    public void RateOutside(User user, string mediaType, string tmdbId, string? title, int? year, int? stars, int? thumb)
    {
        _store.Update(user.Id, TasteStore.TmdbKey(mediaType, tmdbId), answer => answer with
        {
            Stars = stars is >= 1 and <= 5 ? stars : answer.Stars,
            Thumb = thumb is 1 or -1 ? thumb : answer.Thumb,
            Title = title ?? answer.Title,
            Year = year ?? answer.Year,
            MediaType = mediaType,
            TmdbId = tmdbId,
        });
        _profiles.Invalidate(user.Id);
    }

    /// <summary>
    /// Hides a title from every row and counts it as a mild dislike. <see cref="Undo"/> reverses it.
    /// </summary>
    public void NotInterested(User user, string titleKey, string? title, int? year)
    {
        var (mediaType, tmdbId) = TasteStore.ParseTmdbKey(titleKey);
        _store.Update(user.Id, titleKey, answer => answer with
        {
            NotInterested = true,
            Title = title ?? answer.Title,
            Year = year ?? answer.Year,
            MediaType = mediaType ?? answer.MediaType,
            TmdbId = tmdbId ?? answer.TmdbId,
        });
        _profiles.Invalidate(user.Id);
    }

    public void Undo(User user, string titleKey)
    {
        _store.Update(user.Id, titleKey, answer => answer with { NotInterested = false });
        _profiles.Invalidate(user.Id);
    }

    /// <summary>
    /// Gets the titles a person marked "not for me", newest first.
    /// </summary>
    public IReadOnlyList<Answer> Dismissed(User user)
        => _store.GetAnswers(user.Id).Values.Where(a => a.NotInterested).OrderByDescending(a => a.UpdatedUtc).ToList();
}
