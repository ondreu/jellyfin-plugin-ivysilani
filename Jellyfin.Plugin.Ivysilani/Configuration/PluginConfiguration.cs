using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Ivysilani.Configuration;

/// <summary>
/// Plugin configuration for iVysílání.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        ShowUrls = string.Empty;
        CacheTtlHours = 3;
    }

    /// <summary>
    /// Gets or sets the newline-separated list of ceskatelevize.cz show/episode URLs
    /// the user wants exposed in the channel.
    /// </summary>
    public string ShowUrls { get; set; }

    /// <summary>
    /// Gets or sets how long (in hours) resolved show/episode-list data is cached before
    /// being refreshed from ceskatelevize.cz.
    /// </summary>
    public int CacheTtlHours { get; set; }
}
