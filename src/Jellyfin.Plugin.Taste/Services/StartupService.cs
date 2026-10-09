using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Taste.Services;

/// <summary>
/// Registers the Taste page with Plugin Pages, and removes the script entry older versions left in JavaScript Injector
/// (the web script now comes from <see cref="WebInjectionFilter"/>).
/// Both live in other plugins' load contexts, so they are called through reflection.
/// </summary>
public sealed class StartupService : IHostedService, IDisposable
{
    private const string LegacyScriptId = "taste-plugin-client";
    private const string PageId = "taste-plugin-page";
    private const int Attempts = 12;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private readonly ILogger<StartupService> _logger;
    private readonly CancellationTokenSource _stopping = new();

    public StartupService(ILogger<StartupService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => RegisterAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _stopping.Dispose();

    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        var page = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = PageId,
            ["url"] = "/Taste/Page",
            ["displayText"] = "Taste",
            ["icon"] = "thumbs_up_down",
        });

        var legacyDone = false;
        var pageDone = false;
        for (var attempt = 1; attempt <= Attempts && !(legacyDone && pageDone); attempt++)
        {
            legacyDone = legacyDone || TryInvoke(
                "Jellyfin.Plugin.JavaScriptInjector",
                "Jellyfin.Plugin.JavaScriptInjector.PluginInterface",
                "UnregisterScript",
                _ => LegacyScriptId);
            pageDone = pageDone || TryInvoke(
                "Jellyfin.Plugin.PluginPages",
                "Jellyfin.Plugin.PluginPages.PluginInterface",
                "RegisterPage",
                payloadType => ParseJson(payloadType, page));

            if (!(legacyDone && pageDone))
            {
                try
                {
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        if (!pageDone)
        {
            _logger.LogInformation("Taste: Plugin Pages not found. The Taste page is still reachable from the header button.");
        }
    }

    private static object? ParseJson(Type payloadType, string json)
        => payloadType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)])?.Invoke(null, [json]);

    private bool TryInvoke(string assemblyName, string typeName, string methodName, Func<Type, object?> argument)
    {
        try
        {
            var assembly = AssemblyLoadContext.All
                .SelectMany(context => context.Assemblies)
                .FirstOrDefault(a => a.GetName().Name == assemblyName);
            var method = assembly?.GetType(typeName)?.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            var value = method is null ? null : argument(method.GetParameters()[0].ParameterType);
            if (value is null)
            {
                return false;
            }

            method!.Invoke(null, [value]);
            _logger.LogDebug("Taste: called {Plugin}.{Method}", assemblyName, methodName);
            return true;
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or ArgumentException)
        {
            _logger.LogDebug(ex, "Taste: {Plugin} not ready yet", assemblyName);
            return false;
        }
    }
}
