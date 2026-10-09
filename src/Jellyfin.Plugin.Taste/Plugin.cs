using Jellyfin.Plugin.Taste.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Taste;

/// <summary>
/// Asks for stars and a thumb at the credits or a series finale, and recommends from the resulting taste profile.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginGuid = "a405b6d2-6f8d-4f15-bcac-0f4ca3ce59ec";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Taste";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description =>
        "Stars and a thumb when a movie ends in the credits or a series ends, and recommendations from that taste profile.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            },
        ];
    }
}
