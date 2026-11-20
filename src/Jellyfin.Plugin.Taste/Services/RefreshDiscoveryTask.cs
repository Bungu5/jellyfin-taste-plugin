using Jellyfin.Plugin.Taste.Data;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Taste.Services;

/// <summary>
/// Keeps everyone's "new" and "worth requesting" rows ready. A person is rebuilt only when their taste or the
/// library's title and ingredient counts have changed. Cached HTTP responses older than a month are dropped;
/// saved discovery rows are not.
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

    public string Description => "Updates titles outside the library when someone's taste or the library counts have changed, so the Taste rows open instantly.";

    public string Category => "Taste";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var pruned = _store.PruneCache(DateTime.UtcNow - CacheKeepFor);
        if (pruned > 0)
        {
            _logger.LogInformation("Taste: dropped {Count} old cache entries", pruned);
        }

        var users = _userManager.GetUsers().ToList();
        var rebuilt = 0;
        for (var i = 0; i < users.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await _discovery.RefreshAsync(users[i], cancellationToken).ConfigureAwait(false))
                {
                    rebuilt++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Taste: discovery refresh for {User} failed", users[i].Username);
            }

            progress.Report(100.0 * (i + 1) / users.Count);
        }

        _logger.LogInformation(
            "Taste: discovery refresh rebuilt {Rebuilt} of {Count} people",
            rebuilt,
            users.Count);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => [new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks }];
}
