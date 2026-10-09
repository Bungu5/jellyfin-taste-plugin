using Jellyfin.Plugin.Taste.Services;

namespace Jellyfin.Plugin.Taste.Tests;

public sealed class WebInjectionTests
{
    private const string Block = WebInjectionFilter.Marker + "<script>x()</script>";

    [Fact]
    public void LoaderGoesBeforeTheLastBodyClose()
    {
        var html = "<html><body><div></div><script>'</body>'</script></body></html>";

        var injected = WebInjectionFilter.Inject(html, Block);

        Assert.Equal("<html><body><div></div><script>'</body>'</script>" + Block + "</body></html>", injected);
    }

    [Fact]
    public void InjectingTwiceChangesNothing()
    {
        var once = WebInjectionFilter.Inject("<body></body>", Block)!;

        Assert.Null(WebInjectionFilter.Inject(once, Block));
    }

    [Fact]
    public void PagesWithoutABodyAreLeftAlone()
    {
        Assert.Null(WebInjectionFilter.Inject("{\"not\":\"html\"}", Block));
    }

    [Theory]
    [InlineData("/web/index.html", true)]
    [InlineData("/web/", true)]
    [InlineData("/web", true)]
    [InlineData("/jellyfin/web/index.html", true)]
    [InlineData("/web/main.jellyfin.bundle.js", false)]
    [InlineData("/Taste/Page", false)]
    [InlineData(null, false)]
    public void OnlyTheWebShellIsRewritten(string? path, bool expected)
    {
        Assert.Equal(expected, WebInjectionFilter.IsIndexRequest(path));
    }

    [Fact]
    public void LoaderWaitsForASignedInUserAndLoadsTheVersionedScript()
    {
        var script = WebInjectionFilter.LoaderScript("1.2.3.4");

        Assert.Contains("getCurrentUserId()", script, StringComparison.Ordinal);
        Assert.Contains("Taste/client.js')+'?v=1.2.3.4'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("</script", script, StringComparison.OrdinalIgnoreCase);
    }
}
