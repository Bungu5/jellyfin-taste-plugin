using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Taste.Services;

/// <summary>
/// Registers the player script with JavaScript Injector and the Taste page with Plugin Pages.
/// Both live in other plugins' load contexts, so they are called through reflection with their own JObject type.
/// </summary>
public sealed class StartupService : IHostedService, IDisposable
{
    private const string ScriptId = "taste-plugin-client";
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

    internal static string LoaderScript(string version)
        => "(function(){if(window.__tasteLoader){return;}window.__tasteLoader=true;" +
           "var s=document.createElement('script');" +
           $"s.src=ApiClient.getUrl('Taste/client.js')+'?v={version}';" +
           "document.head.appendChild(s);})();";

    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        var version = typeof(StartupService).Assembly.GetName().Version?.ToString() ?? "0";
        var script = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = ScriptId,
            ["name"] = "Taste rating dialog",
            ["script"] = LoaderScript(version),
            ["enabled"] = true,
            ["requiresAuthentication"] = true,
            ["pluginId"] = Plugin.PluginGuid,
            ["pluginName"] = "Taste",
            ["pluginVersion"] = version,
        });
        var page = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = PageId,
            ["url"] = "/Taste/Page",
            ["displayText"] = "Taste",
            ["icon"] = "thumbs_up_down",
        });

        var scriptDone = false;
        var pageDone = false;
        for (var attempt = 1; attempt <= Attempts && !(scriptDone && pageDone); attempt++)
        {
            scriptDone = scriptDone || TryInvoke(
                "Jellyfin.Plugin.JavaScriptInjector",
                "Jellyfin.Plugin.JavaScriptInjector.PluginInterface",
                "RegisterScript",
                script);
            pageDone = pageDone || TryInvoke(
                "Jellyfin.Plugin.PluginPages",
                "Jellyfin.Plugin.PluginPages.PluginInterface",
                "RegisterPage",
                page);

            if (!(scriptDone && pageDone))
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

        if (!scriptDone)
        {
            _logger.LogWarning(
                "Taste: JavaScript Injector not found. Install it, or add this script to it by hand: {Script}",
                LoaderScript(version));
        }

        if (!pageDone)
        {
            _logger.LogInformation("Taste: Plugin Pages not found. The Taste page is still reachable from the header button.");
        }
    }

    private bool TryInvoke(string assemblyName, string typeName, string methodName, string json)
    {
        try
        {
            var assembly = AssemblyLoadContext.All
                .SelectMany(context => context.Assemblies)
                .FirstOrDefault(a => a.GetName().Name == assemblyName);
            var method = assembly?.GetType(typeName)?.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            if (method is null)
            {
                return false;
            }

            var payloadType = method.GetParameters()[0].ParameterType;
            var parse = payloadType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
            if (parse is null)
            {
                return false;
            }

            method.Invoke(null, [parse.Invoke(null, [json])]);
            _logger.LogInformation("Taste: registered with {Plugin}", assemblyName);
            return true;
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or ArgumentException)
        {
            _logger.LogDebug(ex, "Taste: {Plugin} not ready yet", assemblyName);
            return false;
        }
    }
}
