using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Dlna.ContentDirectory;

/// <summary>
/// Defines the <see cref="ServerItem" />.
/// </summary>
internal sealed class ServerItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ServerItem"/> class.
    /// </summary>
    /// <param name="item">The underlying item.</param>
    /// <param name="stubType">The virtual folder type.</param>
    /// <param name="virtualFolderName">The displayed name of the virtual folder.</param>
    /// <param name="idSuffix">The optional suffix encoded in the DLNA object ID.</param>
    /// <param name="ancestorId">The library the client browsed in from, for globally shared named items such as genres.</param>
    /// <param name="partNumber">The one based part number of a stacked (multi-part) video.</param>
    /// <param name="itemCounts">The counts the listing reported for the item, if any.</param>
    public ServerItem(
        BaseItem item,
        StubType? stubType,
        string? virtualFolderName = null,
        string? idSuffix = null,
        Guid? ancestorId = null,
        int? partNumber = null,
        ItemCounts? itemCounts = null)
    {
        Item = item;
        VirtualFolderName = virtualFolderName;
        IdSuffix = idSuffix;
        AncestorId = ancestorId;
        PartNumber = partNumber;
        ItemCounts = itemCounts;

        if (stubType.HasValue)
        {
            StubType = stubType;
        }
        else if (item is IItemByName and not Folder)
        {
            StubType = ContentDirectory.StubType.Folder;
        }
    }

    /// <summary>
    /// Gets the underlying base item.
    /// </summary>
    public BaseItem Item { get; }

    /// <summary>
    /// Gets the DLNA item type.
    /// </summary>
    public StubType? StubType { get; }

    /// <summary>
    /// Gets the display name for a virtual folder.
    /// </summary>
    public string? VirtualFolderName { get; }

    /// <summary>
    /// Gets the suffix appended to the virtual folder object id.
    /// </summary>
    public string? IdSuffix { get; }

    /// <summary>
    /// Gets the library the client browsed in from, for globally shared named items such as genres.
    /// </summary>
    public Guid? AncestorId { get; }

    /// <summary>
    /// Gets the one based part number when the item is one part of a stacked (multi-part) video.
    /// </summary>
    public int? PartNumber { get; }

    /// <summary>
    /// Gets the counts the listing this item came from reported for it, if it reported any. They
    /// carry the scope of that listing, so a genre listed under a library is counted within it.
    /// </summary>
    public ItemCounts? ItemCounts { get; }
}
