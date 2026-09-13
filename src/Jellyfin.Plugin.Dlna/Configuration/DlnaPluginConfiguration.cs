using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Dlna.Configuration;

/// <summary>
/// Defines the <see cref="DlnaPluginConfiguration" />.
/// </summary>
public class DlnaPluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the maximum number of video items prepared in one DLNA response.
    /// </summary>
    public int MaximumVideoPageSize { get; set; } = 20;

    /// <summary>
    /// Gets or sets the maximum number of video stream plans built in parallel.
    /// </summary>
    public int StreamPlanningParallelism { get; set; } = 8;

    /// <summary>
    /// Gets or sets the maximum number of independent virtual-folder count queries run in parallel.
    /// </summary>
    public int CountQueryParallelism { get; set; } = 4;

    /// <summary>
    /// Gets or sets the maximum number of items represented by a Latest folder.
    /// </summary>
    public int LatestItemsLimit { get; set; } = 50;

    /// <summary>
    /// Gets or sets a value indicating whether gets or sets a value to indicate the status of the dlna playTo subsystem.
    /// </summary>
    public bool EnablePlayTo { get; set; } = true;

    /// <summary>
    /// Gets or sets the ssdp client discovery interval time (in seconds).
    /// This is the time after which the server will send a ssdp search request.
    /// </summary>
    public int ClientDiscoveryIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets a value indicating whether to blast alive messages.
    /// </summary>
    public bool BlastAliveMessages { get; set; } = true;

    /// <summary>
    /// Gets or sets the frequency at which ssdp alive notifications are transmitted.
    /// </summary>
    public int AliveMessageIntervalSeconds { get; set; } = 180;

    /// <summary>
    /// Gets or sets a value indicating whether to send only matched host.
    /// </summary>
    public bool SendOnlyMatchedHost { get; set; } = true;

    /// <summary>
    /// Gets or sets the legacy default user account.
    /// Fleet DLNA ignores this option and automatically uses an administrator account.
    /// </summary>
    public Guid? DefaultUserId { get; set; }
}
