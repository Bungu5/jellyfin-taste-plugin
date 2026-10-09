using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Taste.Services;

/// <summary>
/// Adds the Taste loader to Jellyfin Web's index.html as it is served, so the dialog, home rows, and header button
/// load on every page view without JavaScript Injector or File Transformation, and without touching files on disk.
/// </summary>
public sealed class WebInjectionFilter : IStartupFilter
{
    internal const string Marker = "<!-- Taste plugin -->";

    private readonly ILogger<WebInjectionFilter> _logger;
    private readonly string _block;
    private int _logged;

    public WebInjectionFilter(ILogger<WebInjectionFilter> logger)
    {
        _logger = logger;
        var version = typeof(WebInjectionFilter).Assembly.GetName().Version?.ToString() ?? "0";
        _block = Marker + "<script>" + LoaderScript(version) + "</script>";
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(InvokeAsync);
        next(app);
    };

    /// <summary>
    /// Waits until someone is signed in, then loads client.js once per page.
    /// </summary>
    internal static string LoaderScript(string version)
        => "(function(){if(window.__tasteLoader){return;}window.__tasteLoader=true;" +
           "var t=setInterval(function(){var c=window.ApiClient;" +
           "if(!c||typeof c.getCurrentUserId!=='function'||!c.getCurrentUserId()){return;}" +
           "clearInterval(t);if(window.TasteUI){return;}" +
           "var s=document.createElement('script');" +
           $"s.src=c.getUrl('Taste/client.js')+'?v={version}';" +
           "document.head.appendChild(s);},300);})();";

    /// <summary>
    /// Returns the page with the loader before its closing body tag, or null when it needs no change.
    /// </summary>
    internal static string? Inject(string html, string block)
    {
        if (html.Contains(Marker, StringComparison.Ordinal))
        {
            return null;
        }

        var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return bodyClose < 0 ? null : html[..bodyClose] + block + html[bodyClose..];
    }

    internal static bool IsIndexRequest(string? path)
        => !string.IsNullOrEmpty(path)
           && (path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/web", StringComparison.OrdinalIgnoreCase));

    private async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || !IsIndexRequest(context.Request.Path.Value))
        {
            await next().ConfigureAwait(false);
            return;
        }

        // A compressed or partial body can't be rewritten, so ask for the whole page in plain text.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("Range");
        context.Request.Headers.Remove("If-Range");

        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }

        buffer.Position = 0;
        var isHtml = context.Response.StatusCode == StatusCodes.Status200OK
                     && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);
        string? injected = null;
        if (isHtml)
        {
            try
            {
                injected = Inject(Encoding.UTF8.GetString(buffer.ToArray()), _block);
            }
            catch (Exception ex) when (ex is ArgumentException or DecoderFallbackException)
            {
                _logger.LogWarning(ex, "Taste: could not add the web loader; serving the page unchanged");
            }
        }

        if (injected is null)
        {
            await buffer.CopyToAsync(original, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (Interlocked.Exchange(ref _logged, 1) == 0)
        {
            _logger.LogInformation("Taste: added the web loader to Jellyfin Web");
        }

        var bytes = Encoding.UTF8.GetBytes(injected);
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");
        await original.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }
}
