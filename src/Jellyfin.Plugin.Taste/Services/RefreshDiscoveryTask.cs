using Jellyfin.Plugin.Taste.Data;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Taste.Services;

/// <summary>
/// Rebuilds everyone's "new" and "worth requesting" rows ahead of time, so opening Taste does not wait on TMDB,
/// and drops cached responses no reader would still accept.
/// </summary>
public sealed class RefreshDiscoveryTask : IScheduledTask
{
    /// <summary>
    /// Longer than the longest cache max age (Trakt id lookups, 30 days).
    /// </summary>
    private static readonly TimeSpan CacheKeepFor = TimeSpan.FromDays(31);

    private readonly IUserManager _userManager;
    private readonly DiscoveryService _discovery;
    private readonly TasteStore _store;
    private readonly ILogger<RefreshDiscoveryTask> _logger;

    public RefreshDiscoveryTask(IUserManager userManager, DiscoveryService discovery, TasteStore store, ILogger<RefreshDiscoveryTask> logger)
    {
        _userManager = userManager;
        _discovery = discovery;
        _store = store;
        _logger = logger;
    }

    public string Name => "Refresh Taste discovery";

    public string Key => "TasteRefreshDiscovery";

    public string Description => "Finds new and older titles outside the library for each person, so the Taste rows open instantly.";

    public string Category => "Taste";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var pruned = _store.PruneCache(DateTime.UtcNow - CacheKeepFor);
        if (pruned > 0)
        {
            _logger.LogInformation("Taste: dropped {Count} old cache entries", pruned);
        }

        var users = _userManager.GetUsers().ToList();
        for (var i = 0; i < users.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _discovery.RefreshAsync(users[i], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Taste: discovery refresh for {User} failed", users[i].Username);
            }

            progress.Report(100.0 * (i + 1) / users.Count);
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => [new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks }];
}
