using Jellyfin.Plugin.Taste.Data;
using Jellyfin.Plugin.Taste.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Taste;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<TasteStore>();
        serviceCollection.AddSingleton<LibraryIndex>();
        serviceCollection.AddSingleton<TmdbClient>();
        serviceCollection.AddSingleton<TraktClient>();
        serviceCollection.AddSingleton<MdbListClient>();
        serviceCollection.AddSingleton<NeighbourService>();
        serviceCollection.AddSingleton<DiscoveryService>();
        serviceCollection.AddSingleton<SeerrClient>();
        serviceCollection.AddSingleton<ProfileService>();
        serviceCollection.AddSingleton<RatingService>();
        serviceCollection.AddSingleton<PromptService>();
        serviceCollection.AddSingleton<RecommendationService>();
        serviceCollection.AddSingleton<CalibrationService>();
        serviceCollection.AddSingleton<TransferService>();
        serviceCollection.AddHostedService<StartupService>();
        serviceCollection.AddSingleton<IStartupFilter, WebInjectionFilter>();
    }
}
