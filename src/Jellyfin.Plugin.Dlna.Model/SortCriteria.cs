using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;

namespace Jellyfin.Plugin.Dlna.Model;

/// <summary>
/// Defines the <see cref="SortCriteria" />.
/// </summary>
public class SortCriteria
{
    /// <summary>
    /// The sort fields this understands, which is what GetSortCapabilities has to advertise: a
    /// control point is entitled to expect a listing back in the order it asked for.
    /// </summary>
    private static readonly Dictionary<string, ItemSortBy[]> _sortFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dc:title"] = [ItemSortBy.SortName],
        ["dc:date"] = [ItemSortBy.PremiereDate],
        ["upnp:album"] = [ItemSortBy.Album],
        ["upnp:artist"] = [ItemSortBy.AlbumArtist],
        ["upnp:albumArtist"] = [ItemSortBy.AlbumArtist],

        // The disc comes first, or the tracks of a multi disc album interleave: every disc of it
        // numbers its tracks from one again
        ["upnp:originalTrackNumber"] = [ItemSortBy.ParentIndexNumber, ItemSortBy.IndexNumber],
        ["upnp:episodeNumber"] = [ItemSortBy.ParentIndexNumber, ItemSortBy.IndexNumber],
        ["upnp:rating"] = [ItemSortBy.CommunityRating],
        ["res@duration"] = [ItemSortBy.Runtime]
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="SortCriteria"/> class.
    /// </summary>
    /// <param name="sortCriteria">The sort criteria of the request.</param>
    public SortCriteria(string sortCriteria)
    {
        // ContentDirectory:1 section 2.3.14: a comma separated list of properties, each prefixed
        // with "+" for ascending or "-" for descending. A bare sort order is not part of that, but
        // was the only thing this used to read, so it is still accepted.
        if (Enum.TryParse<SortOrder>(sortCriteria, true, out var sortOrderValue))
        {
            SortOrder = sortOrderValue;
            return;
        }

        SortOrder = SortOrder.Ascending;

        List<(ItemSortBy SortBy, SortOrder SortOrder)> fields = [];

        foreach (var criterion in (sortCriteria ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var descending = criterion[0] == '-';
            var name = criterion[0] is '+' or '-' ? criterion[1..].Trim() : criterion;

            // A property this cannot order by is skipped rather than failing the request, so a
            // client asking for one supported field and one unsupported one still gets the first.
            if (_sortFields.TryGetValue(name, out var sortBy))
            {
                foreach (var field in sortBy)
                {
                    fields.Add((field, descending ? SortOrder.Descending : SortOrder.Ascending));
                }
            }
        }

        if (fields.Count > 0)
        {
            SortOrder = fields[0].SortOrder;
            Fields = [.. fields];
        }
    }

    /// <summary>
    /// Gets the sort order of the first field, kept for callers that order by one property.
    /// </summary>
    public SortOrder SortOrder { get; }

    /// <summary>
    /// Gets the properties to order by, in the order they were requested. Empty when the request
    /// named none this understands.
    /// </summary>
    public IReadOnlyList<(ItemSortBy SortBy, SortOrder SortOrder)> Fields { get; } = [];

    /// <summary>
    /// Gets the sort fields a request may ask for, as a ContentDirectory SortCaps list.
    /// </summary>
    /// <returns>The comma separated property names.</returns>
    public static string GetSortCapabilities() => string.Join(',', _sortFields.Keys);
}
