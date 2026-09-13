using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Dlna.Didl;
using Jellyfin.Plugin.Dlna.Localization;
using Jellyfin.Plugin.Dlna.Model;
using Jellyfin.Plugin.Dlna.Service;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using DeviceProfile = MediaBrowser.Model.Dlna.DeviceProfile;
using Genre = MediaBrowser.Controller.Entities.Genre;
using StreamInfo = MediaBrowser.Model.Dlna.StreamInfo;

namespace Jellyfin.Plugin.Dlna.ContentDirectory;

/// <summary>
/// Defines the <see cref="ControlHandler" />.
/// </summary>
public class ControlHandler : BaseControlHandler
{
    private const string NsDc = "http://purl.org/dc/elements/1.1/";
    private const string NsDidl = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
    private const string NsDlna = "urn:schemas-dlna-org:metadata-1-0/";
    private const string NsUpnp = "urn:schemas-upnp-org:metadata-1-0/upnp/";
    private const int MaxPageSize = 200;

    /// <summary>
    /// How many of the counts a listing needs are queried at once. The queries overlap only in
    /// part, so a wider fan out mostly adds contention for everything else using the library.
    /// </summary>
    private const int MaxCountConcurrency = 4;

    // Bound expensive media-source and DIDL work per response, not the total
    // result set. Preserve TotalMatches and StartingIndex so clients that page
    // using NumberReturned can retrieve the remaining entries.
    private const int MaximumVideoPageSize = 20;

    // Build playback plans concurrently so large letter folders do not spend
    // several seconds resolving one item at a time. Keep this bounded because
    // many TVs can browse the server at once.
    private const int StreamPlanningParallelism = 8;

    // Force the fleet clients to invalidate the older cached virtual hierarchy.
    // v8 previously used +800000; this purpose-built hierarchy deliberately moves
    // forward again while preserving Jellyfin's own update-id changes.
    private const int FleetHierarchyUpdateOffset = 900000;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly User? _user;
    private readonly IUserViewManager _userViewManager;
    private readonly ITVSeriesManager _tvSeriesManager;
    private readonly MovieLibraryQueryScope _movieQueryScope;

    private readonly int _systemUpdateId;

    private readonly DidlBuilder _didlBuilder;

    private readonly DlnaDeviceProfile _profile;

    /// <summary>
    /// Initializes a new instance of the <see cref="ControlHandler"/> class.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger"/>.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="profile">The <see cref="DeviceProfile"/>.</param>
    /// <param name="serverAddress">The server address.</param>
    /// <param name="accessToken">The access token.</param>
    /// <param name="imageProcessor">Instance of the <see cref="IImageProcessor"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="user">The <see cref="User"/>.</param>
    /// <param name="systemUpdateId">The system id.</param>
    /// <param name="localization">Instance of the <see cref="DlnaLocalization"/> class.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    /// <param name="userViewManager">Instance of the <see cref="IUserViewManager"/> interface.</param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    /// <param name="tvSeriesManager">Instance of the <see cref="ITVSeriesManager"/> interface.</param>
    public ControlHandler(
        ILogger logger,
        ILibraryManager libraryManager,
        DlnaDeviceProfile profile,
        string serverAddress,
        string? accessToken,
        IImageProcessor imageProcessor,
        IUserDataManager userDataManager,
        User? user,
        int systemUpdateId,
        DlnaLocalization localization,
        IMediaSourceManager mediaSourceManager,
        IUserViewManager userViewManager,
        IMediaEncoder mediaEncoder,
        ITVSeriesManager tvSeriesManager)
        : base(logger)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _user = user;
        _systemUpdateId = checked(systemUpdateId + FleetHierarchyUpdateOffset);
        _userViewManager = userViewManager;
        _tvSeriesManager = tvSeriesManager;
        _profile = profile;
        _movieQueryScope = new MovieLibraryQueryScope(libraryManager, logger);

        _didlBuilder = new DidlBuilder(
            profile,
            user,
            imageProcessor,
            serverAddress,
            accessToken,
            userDataManager,
            localization,
            mediaSourceManager,
            Logger,
            mediaEncoder,
            libraryManager);
    }

    /// <inheritdoc />
    protected override void WriteResult(
        string methodName,
        IReadOnlyDictionary<string, string> methodParams,
        XmlWriter xmlWriter)
    {
        ArgumentNullException.ThrowIfNull(xmlWriter);
        ArgumentNullException.ThrowIfNull(methodParams);
        _movieQueryScope.Reset();

        const string DeviceId = "test";

        if (string.Equals(methodName, "GetSearchCapabilities", StringComparison.OrdinalIgnoreCase))
        {
            HandleGetSearchCapabilities(xmlWriter);
            return;
        }

        if (string.Equals(methodName, "GetSortCapabilities", StringComparison.OrdinalIgnoreCase))
        {
            HandleGetSortCapabilities(xmlWriter);
            return;
        }

        if (
            string.Equals(
                methodName,
                "GetSortExtensionCapabilities",
                StringComparison.OrdinalIgnoreCase)
        )
        {
            HandleGetSortExtensionCapabilities(xmlWriter);
            return;
        }

        if (string.Equals(methodName, "GetSystemUpdateID", StringComparison.OrdinalIgnoreCase))
        {
            HandleGetSystemUpdateID(xmlWriter);
            return;
        }

        if (string.Equals(methodName, "Browse", StringComparison.OrdinalIgnoreCase))
        {
            HandleBrowse(xmlWriter, methodParams, DeviceId);
            return;
        }

        if (string.Equals(methodName, "X_GetFeatureList", StringComparison.OrdinalIgnoreCase))
        {
            HandleXGetFeatureList(xmlWriter);
            return;
        }

        if (string.Equals(methodName, "GetFeatureList", StringComparison.OrdinalIgnoreCase))
        {
            HandleGetFeatureList(xmlWriter);
            return;
        }

        if (string.Equals(methodName, "X_SetBookmark", StringComparison.OrdinalIgnoreCase))
        {
            HandleXSetBookmark(methodParams);
            return;
        }

        if (string.Equals(methodName, "Search", StringComparison.OrdinalIgnoreCase))
        {
            HandleSearch(xmlWriter, methodParams, DeviceId);
            return;
        }

        if (string.Equals(methodName, "X_BrowseByLetter", StringComparison.OrdinalIgnoreCase))
        {
            HandleXBrowseByLetter(xmlWriter, methodParams, DeviceId);
            return;
        }

        throw new ResourceNotFoundException("Unexpected control request name: " + methodName);
    }

    /// <summary>
    /// Adds a "XSetBookmark" element to the xml document.
    /// </summary>
    /// <param name="sparams">The method parameters.</param>
    private void HandleXSetBookmark(IReadOnlyDictionary<string, string> sparams)
    {
        if (_user is null)
        {
            return;
        }

        var id = sparams["ObjectID"];

        var serverItem = GetItemFromObjectId(id);

        var item = serverItem.Item;

        var newbookmark = int.Parse(sparams["PosSecond"], CultureInfo.InvariantCulture);

        var userdata = _userDataManager.GetUserData(_user, item)!;

        userdata.PlaybackPositionTicks = TimeSpan.FromSeconds(newbookmark).Ticks;

        _userDataManager.SaveUserData(
            _user,
            item,
            userdata,
            UserDataSaveReason.TogglePlayed,
            CancellationToken.None);
    }

    /// <summary>
    /// Adds the "SearchCaps" element to the xml document.
    /// </summary>
    /// <param name="xmlWriter">The <see cref="XmlWriter"/>.</param>
    private static void HandleGetSearchCapabilities(XmlWriter xmlWriter)
    {
        xmlWriter.WriteElementString(
            "SearchCaps",
            "res@resolution,res@size,res@duration,dc:title,dc:creator,upnp:actor,upnp:artist,upnp:genre,upnp:album,dc:date,upnp:class,@id,@refID,@protocolInfo,upnp:author,dc:description,pv:avKeywords");
    }

    /// <summary>
    /// Adds the "SortCaps" element to the xml document.
    /// </summary>
    /// <param name="xmlWriter">The <see cref="XmlWriter"/>.</param>
    private static void HandleGetSortCapabilities(XmlWriter xmlWriter)
    {
        // Only what SortCriteria can actually order by, so a client is never handed a listing in a
        // different order than the one it asked for
        xmlWriter.WriteElementString("SortCaps", SortCriteria.GetSortCapabilities());
    }

    /// <summary>
    /// Adds the "SortExtensionCaps" element to the xml document.
    /// </summary>
    /// <param name="xmlWriter">The <see cref="XmlWriter"/>.</param>
    private static void HandleGetSortExtensionCapabilities(XmlWriter xmlWriter)
    {
        xmlWriter.WriteElementString(
            "SortExtensionCaps",
            "res@duration,res@size,res@bitrate,dc:date,dc:title,dc:size,upnp:album,upnp:artist,upnp:albumArtist,upnp:episodeNumber,upnp:genre,upnp:originalTrackNumber,upnp:rating");
    }

    /// <summary>
    /// Adds the "Id" element to the xml document.
    /// </summary>
    /// <param name="xmlWriter">The <see cref="XmlWriter"/>.</param>
    private void HandleGetSystemUpdateID(XmlWriter xmlWriter)
    {
        xmlWriter.WriteElementString("Id", _systemUpdateId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Adds the "FeatureList" element to the xml document.
    /// </summary>
    /// <param name="xmlWriter">The <see cref="XmlWriter"/>.</param>
    private static void HandleGetFeatureList(XmlWriter xmlWriter)
    {
        xmlWriter.WriteElementString("FeatureList", WriteFeatureListXml());
    }

    /// <summary>
    /// Adds the "FeatureList" element to the xml document.
    /// </summary>
    /// <param name="xmlWriter">The <see cref="XmlWriter"/>.</param>
    private static void HandleXGetFeatureList(XmlWriter xmlWriter) =>
        HandleGetFeatureList(xmlWriter);

    /// <summary>
    /// Builds a static feature list.
    /// </summary>
    /// <returns>The xml feature list.</returns>
    private static string WriteFeatureListXml()
    {
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<Features xmlns=\"urn:schemas-upnp-org:av:avs\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:schemaLocation=\"urn:schemas-upnp-org:av:avs http://www.upnp.org/schemas/av/avs.xsd\">"
            + "<Feature name=\"samsung.com_BASICVIEW\" version=\"1\">"
            + "<container id=\"0\" type=\"object.item.imageItem\"/>"
            + "<container id=\"0\" type=\"object.item.audioItem\"/>"
            + "<container id=\"0\" type=\"object.item.videoItem\"/>"
            + "</Feature>"
            + "</Features>";
    }

    /// <summary>
    /// Builds the "Browse" xml response.
    /// </summary>
    /// <param name="xmlWriter">The <see cref="XmlWriter"/>.</param>
    /// <param name="sparams">The method parameters.</param>
    /// <param name="deviceId">The device Id to use.</param>
    private void HandleBrowse(
        XmlWriter xmlWriter,
        IReadOnlyDictionary<string, string> sparams,
        string deviceId)
    {
        var id = sparams["ObjectID"];
        var flag = sparams["BrowseFlag"];
        var requestTimer = Stopwatch.StartNew();
        long lookupMilliseconds = 0;
        Logger.LogDebug(
            "DLNA browse v3 Browse request: object={ObjectId}, flag={BrowseFlag}, user={UserId}.",
            id,
            flag,
            _user?.Id);
        var filter = new Filter(sparams.GetValueOrDefault("Filter", "*"));
        var sortCriteria = new SortCriteria(
            sparams.GetValueOrDefault("SortCriteria", string.Empty));

        var provided = 0;

        int? requestedCount = MaxPageSize;
        int? start = 0;

        if (
            sparams.ContainsKey("RequestedCount")
            && int.TryParse(sparams["RequestedCount"], out var requestedVal)
            && requestedVal > 0
        )
        {
            requestedCount = Math.Min(requestedVal, MaxPageSize);
        }

        if (
            sparams.ContainsKey("StartingIndex")
            && int.TryParse(sparams["StartingIndex"], out var startVal)
            && startVal > 0
        )
        {
            start = startVal;
        }

        var originalRequestedCount = requestedCount;
        int totalCount;

        var settings = new XmlWriterSettings
        {
            Encoding = Encoding.UTF8,
            CloseOutput = false,
            OmitXmlDeclaration = true,
            ConformanceLevel = ConformanceLevel.Fragment,
        };

        using (StringWriter builder = new StringWriterWithEncoding(Encoding.UTF8))
        using (var writer = XmlWriter.Create(builder, settings))
        {
            writer.WriteStartElement(string.Empty, "DIDL-Lite", NsDidl);

            writer.WriteAttributeString("xmlns", "dc", null, NsDc);
            writer.WriteAttributeString("xmlns", "dlna", null, NsDlna);
            writer.WriteAttributeString("xmlns", "upnp", null, NsUpnp);

            DidlBuilder.WriteXmlRootAttributes(_profile, writer);

            var serverItem = GetItemFromObjectId(id);
            var item = serverItem.Item;

            if (string.Equals(flag, "BrowseMetadata", StringComparison.Ordinal))
            {
                totalCount = 1;

                if (IsWrittenAsContainer(serverItem))
                {
                    var childCount = GetFolderChildCount(serverItem, sortCriteria);
                    lookupMilliseconds = requestTimer.ElapsedMilliseconds;

                    var metadataParentStub = GetVirtualParentStub(serverItem.StubType);
                    BaseItem? metadataContext = IsFleetVirtualStub(serverItem.StubType)
                        ? item
                        : metadataParentStub is null
                            ? null
                            : item;
                    string? metadataContextSuffix = null;

                    _didlBuilder.WriteFolderElement(
                        writer,
                        item,
                        serverItem.StubType,
                        metadataContext,
                        childCount,
                        filter,
                        requestedId: id,
                        ancestorId: serverItem.AncestorId,
                        virtualFolderName: serverItem.VirtualFolderName,
                        idSuffix: serverItem.IdSuffix,
                        contextStubType: metadataParentStub,
                        contextIdSuffix: metadataContextSuffix);
                }
                else
                {
                    _didlBuilder.WriteItemElement(
                        writer,
                        item,
                        _user,
                        null,
                        null,
                        deviceId,
                        filter,
                        partNumber: GetPartNumber(item));
                }

                provided++;
            }
            else
            {
                if (IsVideoPage(serverItem))
                {
                    requestedCount = GetVideoPageLimit(requestedCount);
                }

                var childrenResult = GetUserItemsWithParts(
                    item,
                    serverItem.StubType,
                    _user,
                    sortCriteria,
                    start,
                    requestedCount,
                    serverItem.AncestorId,
                    serverItem.IdSuffix);
                totalCount = childrenResult.TotalRecordCount;

                provided = childrenResult.Items.Count;
                lookupMilliseconds = requestTimer.ElapsedMilliseconds;
                Logger.LogDebug(
                    "DLNA browse v3 Browse query: object={ObjectId}, resolvedType={ItemType}, returned={Returned}, total={Total}, start={Start}, limit={Limit}.",
                    id,
                    item.GetType().Name,
                    provided,
                    totalCount,
                    start,
                    requestedCount);

                provided = WriteServerItems(
                    writer,
                    childrenResult.Items,
                    item,
                    serverItem.StubType,
                    serverItem.IdSuffix,
                    serverItem.AncestorId,
                    sortCriteria,
                    filter,
                    deviceId);
            }

            writer.WriteFullEndElement();
            writer.Flush();
            xmlWriter.WriteElementString("Result", builder.ToString());
        }

        requestTimer.Stop();
        Logger.LogInformation(
            "DLNA browse v3 Browse: profile={Profile}, object={ObjectId}, flag={Flag}, start={Start}, requested={Requested}, limit={Limit}, returned={Returned}, total={Total}, lookupMs={LookupMs}, renderMs={RenderMs}, elapsedMs={ElapsedMs}.",
            _profile.Name,
            id,
            flag,
            start,
            originalRequestedCount ?? 0,
            requestedCount,
            provided,
            totalCount,
            lookupMilliseconds,
            requestTimer.ElapsedMilliseconds - lookupMilliseconds,
            requestTimer.ElapsedMilliseconds);
        xmlWriter.WriteElementString(
            "NumberReturned",
            provided.ToString(CultureInfo.InvariantCulture));
        xmlWriter.WriteElementString(
            "TotalMatches",
            totalCount.ToString(CultureInfo.InvariantCulture));
        xmlWriter.WriteElementString(
            "UpdateID",
            _systemUpdateId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Builds the response to the "X_BrowseByLetter request.
    /// </summary>
    /// <param name="xmlWriter">The <see cref="XmlWriter"/>.</param>
    /// <param name="sparams">The method parameters.</param>
    /// <param name="deviceId">The device id.</param>
    private void HandleXBrowseByLetter(
        XmlWriter xmlWriter,
        IReadOnlyDictionary<string, string> sparams,
        string deviceId)
    {
        // Devices disagree on the name of the argument carrying the letter
        var letter = sparams.GetValueOrDefault("StartingLetter")
                     ?? sparams.GetValueOrDefault("Letter")
                     ?? sparams.GetValueOrDefault("StartingCharacter");

        HandleSearch(xmlWriter, sparams, deviceId, string.IsNullOrWhiteSpace(letter) ? null : letter);
    }

    private int WriteServerItems(
        XmlWriter writer,
        IReadOnlyList<ServerItem> serverItems,
        BaseItem context,
        StubType? contextStubType,
        string? contextIdSuffix,
        Guid? contextAncestorId,
        SortCriteria sort,
        Filter filter,
        string deviceId)
    {
        var streamInfos = PrepareVideoStreamInfos(serverItems, deviceId);
        _didlBuilder.PreloadPeople([.. serverItems.Select(i => i.Item)]);

        var written = 0;
        foreach (var serverItem in serverItems)
        {
            var childItem = serverItem.Item;
            if (childItem.IsDisplayedAsFolder || serverItem.StubType.HasValue)
            {
                _didlBuilder.WriteFolderElement(
                    writer,
                    childItem,
                    serverItem.StubType,
                    context,
                    GetFolderChildCount(serverItem, sort),
                    filter,
                    ancestorId: serverItem.AncestorId,
                    virtualFolderName: serverItem.VirtualFolderName,
                    idSuffix: serverItem.IdSuffix,
                    contextStubType: contextStubType,
                    contextIdSuffix: contextIdSuffix);
                written++;
                continue;
            }

            streamInfos.TryGetValue(childItem.Id, out var streamInfo);
            try
            {
                _didlBuilder.WriteItemElement(
                    writer,
                    childItem,
                    _user,
                    context,
                    contextStubType,
                    deviceId,
                    filter,
                    streamInfo,
                    partNumber: serverItem.PartNumber,
                    contextIdSuffix: contextIdSuffix,
                    contextAncestorId: contextAncestorId);
                written++;
            }
            catch (InvalidOperationException ex)
            {
                Logger.LogWarning(
                    ex,
                    "DLNA fleet skipped item {ItemId} ({ItemName}) because no playable stream could be built for profile {Profile}.",
                    childItem.Id,
                    childItem.Name,
                    _profile.Name);
            }
            catch (NullReferenceException ex)
            {
                Logger.LogWarning(
                    ex,
                    "DLNA fleet skipped item {ItemId} ({ItemName}) because Jellyfin's stream planner returned incomplete metadata for profile {Profile}.",
                    childItem.Id,
                    childItem.Name,
                    _profile.Name);
            }
        }

        return written;
    }

    private Dictionary<Guid, StreamInfo> PrepareVideoStreamInfos(
        IReadOnlyList<ServerItem> serverItems,
        string deviceId)
    {
        return serverItems
            .Where(serverItem =>
                !serverItem.StubType.HasValue
                && !serverItem.Item.IsDisplayedAsFolder
                && serverItem.Item.MediaType == MediaType.Video)
            .AsParallel()
            .WithDegreeOfParallelism(StreamPlanningParallelism)
            .Select(serverItem =>
                (
                    ItemId: serverItem.Item.Id,
                    StreamInfo: TryPrepareVideoStreamInfo(serverItem.Item, deviceId)
                ))
            .Where(result => result.StreamInfo is not null)
            .ToDictionary(result => result.ItemId, result => result.StreamInfo!);
    }

    private StreamInfo? TryPrepareVideoStreamInfo(BaseItem item, string deviceId)
    {
        try
        {
            return _didlBuilder.GetOptimalVideoStream(item, deviceId);
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogWarning(
                ex,
                "DLNA fleet could not pre-plan item {ItemId} ({ItemName}) for profile {Profile}.",
                item.Id,
                item.Name,
                _profile.Name);
            return null;
        }
        catch (NullReferenceException ex)
        {
            Logger.LogWarning(
                ex,
                "DLNA fleet could not pre-plan item {ItemId} ({ItemName}) because Jellyfin returned incomplete stream metadata for profile {Profile}.",
                item.Id,
                item.Name,
                _profile.Name);
            return null;
        }
    }

    /// <summary>
    /// Builds a response to the "Search" request.
    /// </summary>
    /// <param name="xmlWriter">The xmlWriter<see cref="XmlWriter"/>.</param>
    /// <param name="sparams">The method parameters.</param>
    /// <param name="deviceId">The deviceId<see cref="string"/>.</param>
    /// <param name="nameStartsWith">The letter the results have to start with, if any.</param>
    private void HandleSearch(
        XmlWriter xmlWriter,
        IReadOnlyDictionary<string, string> sparams,
        string deviceId,
        string? nameStartsWith = null)
    {
        var requestedServerItem = GetItemFromObjectId(sparams["ContainerID"]);
        if (IsFleetVideoContext(requestedServerItem))
        {
            HandleFleetVideoSearch(xmlWriter, sparams, deviceId);
            return;
        }

        var requestTimer = Stopwatch.StartNew();
        long lookupMilliseconds;
        Logger.LogDebug(
            "DLNA browse v3 Search request: object={ObjectId}, user={UserId}.",
            sparams.GetValueOrDefault("ContainerID", string.Empty),
            _user?.Id);
        var searchText = sparams.GetValueOrDefault("SearchCriteria", "*");
        var searchCriteria = new SearchCriteria(
            string.IsNullOrWhiteSpace(searchText) ? "*" : searchText);
        var sortCriteria = new SortCriteria(
            sparams.GetValueOrDefault("SortCriteria", string.Empty));
        var filter = new Filter(sparams.GetValueOrDefault("Filter", "*"));

        // sort example: dc:title, dc:date

        int? requestedCount = MaxPageSize;
        int? start = 0;

        if (
            sparams.ContainsKey("RequestedCount")
            && int.TryParse(sparams["RequestedCount"], out var requestedVal)
            && requestedVal > 0
        )
        {
            requestedCount = Math.Min(requestedVal, MaxPageSize);
        }

        if (
            sparams.ContainsKey("StartingIndex")
            && int.TryParse(sparams["StartingIndex"], out var startVal)
            && startVal > 0
        )
        {
            start = startVal;
        }

        var originalRequestedCount = requestedCount;
        QueryResult<ServerItem> childrenResult;
        var settings = new XmlWriterSettings
        {
            Encoding = Encoding.UTF8,
            CloseOutput = false,
            OmitXmlDeclaration = true,
            ConformanceLevel = ConformanceLevel.Fragment,
        };

        using (StringWriter builder = new StringWriterWithEncoding(Encoding.UTF8))
        using (var writer = XmlWriter.Create(builder, settings))
        {
            writer.WriteStartElement(string.Empty, "DIDL-Lite", NsDidl);
            writer.WriteAttributeString("xmlns", "dc", null, NsDc);
            writer.WriteAttributeString("xmlns", "dlna", null, NsDlna);
            writer.WriteAttributeString("xmlns", "upnp", null, NsUpnp);

            DidlBuilder.WriteXmlRootAttributes(_profile, writer);

            var serverItem = GetItemFromObjectId(sparams["ContainerID"]);

            var item = serverItem.Item;
            if (searchCriteria.SearchType == SearchType.Video || IsVideoPage(serverItem))
            {
                requestedCount = GetVideoPageLimit(requestedCount);
            }

            childrenResult = GetSearchResultWithParts(
                serverItem,
                _user,
                searchCriteria,
                sortCriteria,
                start,
                requestedCount,
                nameStartsWith);
            lookupMilliseconds = requestTimer.ElapsedMilliseconds;
            _didlBuilder.PreloadPeople([.. childrenResult.Items.Select(i => i.Item)]);
            Logger.LogDebug(
                "DLNA browse v3 Search query: object={ObjectId}, returned={Returned}, total={Total}, searchType={SearchType}.",
                sparams["ContainerID"],
                childrenResult.Items.Count,
                childrenResult.TotalRecordCount,
                searchCriteria.SearchType);
            foreach (var searchItem in childrenResult.Items)
            {
                var i = searchItem.Item;

                // Video searches within a series bucket return episodes from its shows.
                // Those episodes still belong to their real season, not directly to A/B/etc.
                var isSeriesDescendant = serverItem.StubType == StubType.SeriesLetter
                    && i.GetBaseItemKind() == BaseItemKind.Episode;
                var context = isSeriesDescendant ? null : item;
                var contextStubType = isSeriesDescendant ? null : serverItem.StubType;
                var contextIdSuffix = isSeriesDescendant ? null : serverItem.IdSuffix;

                if (i.IsDisplayedAsFolder || i is Genre)
                {
                    var isGenreListing = i is Genre && serverItem.StubType == StubType.Genres;
                    StubType? resultStubType = isGenreListing ? StubType.Folder : null;
                    string? resultIdSuffix = null;
                    Guid? resultAncestorId = isGenreListing ? item.Id : null;

                    // Genre existence is already established by GetGenres; avoid another
                    // expensive count before the client can display the list. Shows from a
                    // letter folder still use their direct child count.
                    var childCount = isGenreListing
                        ? 1
                        : serverItem.StubType == StubType.SeriesLetter
                            ? GetUserItems(i, null, _user, sortCriteria, null, 0).TotalRecordCount
                            : GetChildrenSorted(
                                new ServerItem(i, null),
                                _user,
                                searchCriteria,
                                sortCriteria,
                                null,
                                0,
                                nameStartsWith).TotalRecordCount;

                    _didlBuilder.WriteFolderElement(
                        writer,
                        i,
                        resultStubType,
                        context,
                        childCount,
                        filter,
                        idSuffix: resultIdSuffix,
                        contextStubType: contextStubType,
                        contextIdSuffix: contextIdSuffix,
                        ancestorId: resultAncestorId);
                }
                else
                {
                    _didlBuilder.WriteItemElement(
                        writer,
                        i,
                        _user,
                        context,
                        contextStubType,
                        deviceId,
                        filter,
                        partNumber: searchItem.PartNumber,
                        contextIdSuffix: contextIdSuffix,
                        contextAncestorId: serverItem.AncestorId);
                }
            }

            writer.WriteFullEndElement();
            writer.Flush();
            xmlWriter.WriteElementString("Result", builder.ToString());
        }

        requestTimer.Stop();
        // Root searches are continuous on this network. Keep them out of the
        // information log while retaining diagnostics for individual folders.
        var logLevel = string.Equals(sparams["ContainerID"], "0", StringComparison.Ordinal)
            ? LogLevel.Debug
            : LogLevel.Information;
        Logger.Log(
            logLevel,
            "DLNA browse v3 Search: profile={Profile}, object={ObjectId}, start={Start}, requested={Requested}, limit={Limit}, returned={Returned}, total={Total}, lookupMs={LookupMs}, renderMs={RenderMs}, elapsedMs={ElapsedMs}.",
            _profile.Name,
            sparams["ContainerID"],
            start,
            originalRequestedCount ?? 0,
            requestedCount,
            childrenResult.Items.Count,
            childrenResult.TotalRecordCount,
            lookupMilliseconds,
            requestTimer.ElapsedMilliseconds - lookupMilliseconds,
            requestTimer.ElapsedMilliseconds);
        xmlWriter.WriteElementString(
            "NumberReturned",
            childrenResult.Items.Count.ToString(CultureInfo.InvariantCulture));
        xmlWriter.WriteElementString(
            "TotalMatches",
            childrenResult.TotalRecordCount.ToString(CultureInfo.InvariantCulture));
        xmlWriter.WriteElementString(
            "UpdateID",
            _systemUpdateId.ToString(CultureInfo.InvariantCulture));
    }

    private static bool IsFleetVirtualStub(StubType? stubType) =>
        stubType is StubType.All
            or StubType.VideoLatest
            or StubType.VideoGenres
            or StubType.MovieLetter
            or StubType.SeriesLetter
            or StubType.MovieGenre
            or StubType.SeriesGenre;

    private static bool IsFleetVideoContext(ServerItem serverItem)
    {
        if (serverItem.Item is not IHasCollectionType collection)
        {
            return false;
        }

        return collection.CollectionType == CollectionType.movies
            || collection.CollectionType == CollectionType.tvshows;
    }

    private void HandleFleetVideoSearch(
        XmlWriter xmlWriter,
        IReadOnlyDictionary<string, string> searchParams,
        string deviceId)
    {
        var browseParams = new Dictionary<string, string>(searchParams, StringComparer.OrdinalIgnoreCase)
        {
            ["ObjectID"] = searchParams["ContainerID"],
            ["BrowseFlag"] = "BrowseDirectChildren",
        };

        Logger.LogDebug(
            "DLNA fleet Search is using Browse semantics for container {ContainerId}.",
            searchParams["ContainerID"]);
        HandleBrowse(xmlWriter, browseParams, deviceId);
    }

    private static int GetVideoPageLimit(int? requestedCount) =>
        Math.Min(requestedCount ?? MaximumVideoPageSize, MaximumVideoPageSize);

    private static bool IsVideoPage(ServerItem serverItem) =>
        serverItem.StubType is StubType.MovieLetter
            or StubType.SeriesLetter
            or StubType.VideoLatest
            or StubType.MovieGenre
            or StubType.SeriesGenre;

    private int GetFolderChildCount(ServerItem serverItem, SortCriteria sort)
    {
        // Exact child counts are expensive and unnecessary for the fleet's virtual
        // navigation containers. A non-zero value is sufficient for Samsung/LG
        // clients to expose the folder; the real TotalMatches is returned when it
        // is opened. This deliberately avoids per-show/per-season count queries.
        if (
            serverItem.StubType is StubType.All
                or StubType.VideoLatest
                or StubType.VideoGenres
                or StubType.MovieLetter
                or StubType.SeriesLetter
                or StubType.MovieGenre
                or StubType.SeriesGenre
            || serverItem.Item is Genre
            || serverItem.Item.GetBaseItemKind() is BaseItemKind.Series
                or BaseItemKind.Season
                or BaseItemKind.BoxSet
        )
        {
            return 1;
        }

        return GetUserItems(
            serverItem.Item,
            serverItem.StubType,
            _user,
            sort,
            null,
            0,
            serverItem.AncestorId,
            serverItem.IdSuffix).TotalRecordCount;
    }

    /// <summary>
    /// Returns the child items meeting the criteria.
    /// </summary>
    /// <param name="serverItem">The requested item, including its virtual-folder context.</param>
    /// <param name="user">The <see cref="User"/>.</param>
    /// <param name="search">The <see cref="SearchCriteria"/>.</param>
    /// <param name="sort">The <see cref="SortCriteria"/>.</param>
    /// <param name="startIndex">The start index.</param>
    /// <param name="limit">The maximum number to return.</param>
    /// <param name="nameStartsWith">The letter the results have to start with, if any.</param>
    /// <returns>The <see cref="QueryResult{BaseItem}"/>.</returns>
    private QueryResult<BaseItem> GetChildrenSorted(
        ServerItem serverItem,
        User? user,
        SearchCriteria search,
        SortCriteria sort,
        int? startIndex,
        int? limit,
        string? nameStartsWith = null)
    {
        if (serverItem.StubType is StubType.MovieLetter or StubType.SeriesLetter)
        {
            return GetAlphabetSearchItems(serverItem, user, search, sort, startIndex, limit);
        }

        // Some DLNA clients use Search rather than Browse when opening a virtual
        // Genres container. Return Genre containers here instead of recursively
        // searching the library for media items, which the client then discards.
        if (serverItem.StubType == StubType.Genres && user is not null)
        {
            var genreQuery = new InternalItemsQuery(user)
            {
                StartIndex = startIndex,
                Limit = limit,
                OrderBy = [],
                AncestorIds = [serverItem.Item.Id],
                EnableTotalRecordCount = true,
            };
            var genres = _libraryManager.GetGenres(genreQuery);
            Logger.LogInformation(
                "DLNA generic genre Search: library={LibraryId}, returned={Returned}, total={Total}.",
                serverItem.Item.Id,
                genres.Items.Count,
                genres.TotalRecordCount);
            return new QueryResult<BaseItem>(
                startIndex,
                genres.TotalRecordCount,
                genres.Items.Select(i => i.Item).ToArray());
        }

        // A client can also Search inside a genre container. Genre is not a Folder,
        // so handle it before the generic Folder cast and retain its library scope.
        if (serverItem.Item is Genre && user is not null)
        {
            var result = GetGenreItems(
                serverItem.Item,
                user,
                sort,
                startIndex,
                limit,
                serverItem.AncestorId);
            return new QueryResult<BaseItem>(
                startIndex,
                result.TotalRecordCount,
                result.Items.Select(i => i.Item).ToArray());
        }

        var folder = (Folder)serverItem.Item;

        MediaType[] mediaTypes = [];
        BaseItemKind[] itemTypes = [];
        bool? isFolder = null;

        switch (search.SearchType)
        {
            case SearchType.Audio:
                mediaTypes = [MediaType.Audio];
                isFolder = false;
                break;
            case SearchType.Video:
                mediaTypes = [MediaType.Video];
                isFolder = false;
                break;
            case SearchType.Image:
                mediaTypes = [MediaType.Photo];
                isFolder = false;
                break;

            // Naming the kind, where matching every folder would answer a search for playlists
            // with every season, album and collection in the library as well
            case SearchType.Playlist:
                itemTypes = [BaseItemKind.Playlist];
                isFolder = true;
                break;
            case SearchType.MusicAlbum:
                itemTypes = [BaseItemKind.MusicAlbum];
                isFolder = true;
                break;
        }

        var query = new InternalItemsQuery(user)
        {
            Limit = limit,
            StartIndex = startIndex,
            OrderBy = GetOrderBy(sort, folder.IsPreSorted),
            Recursive = true,
            IsMissing = false,
            ExcludeItemTypes = [BaseItemKind.Book],
            IsFolder = isFolder,
            MediaTypes = mediaTypes,
            IncludeItemTypes = itemTypes,
            DtoOptions = GetDtoOptions(),
            NameStartsWith = nameStartsWith,
        };

        if (_movieQueryScope.TryApply(folder, query))
        {
            query.IncludeItemTypes = [BaseItemKind.Movie];
            if (serverItem.StubType == StubType.Favorites)
            {
                query.IsFavorite = true;
            }
            else if (serverItem.StubType == StubType.ContinueWatching)
            {
                query.IsResumable = true;
            }
            else if (serverItem.StubType == StubType.Latest)
            {
                query.OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending), (ItemSortBy.SortName, SortOrder.Ascending)];
            }

            return _libraryManager.GetItemsResult(query);
        }

        return folder.GetItems(query);
    }

    /// <summary>
    /// Searches an alphabetical virtual folder without losing its letter or library scope.
    /// </summary>
    /// <param name="serverItem">The alphabetical virtual folder.</param>
    /// <param name="user">The user whose library is being searched.</param>
    /// <param name="search">The requested media type.</param>
    /// <param name="sort">The sort criteria.</param>
    /// <param name="startIndex">The start index within the filtered results.</param>
    /// <param name="limit">The maximum number to return.</param>
    /// <returns>The matching items and their unpaged count.</returns>
    private QueryResult<BaseItem> GetAlphabetSearchItems(
        ServerItem serverItem,
        User? user,
        SearchCriteria search,
        SortCriteria sort,
        int? startIndex,
        int? limit)
    {
        if (search.SearchType is SearchType.Audio or SearchType.Image
            or SearchType.Playlist or SearchType.MusicAlbum)
        {
            return new QueryResult<BaseItem>(startIndex, 0, Array.Empty<BaseItem>());
        }

        var query = new InternalItemsQuery(user)
        {
            StartIndex = startIndex,
            Limit = limit,
            OrderBy = GetOrderBy(sort, false),
            IsMissing = false,
            IsVirtualItem = false,
            IsPlaceHolder = false,
            DtoOptions = GetDtoOptions(),
            EnableTotalRecordCount = true,
        };

        if (serverItem.StubType == StubType.SeriesLetter && search.SearchType == SearchType.Video)
        {
            // Filter by the SHOW's sort name, not the episode's title. Do not page the
            // shows first: StartingIndex/RequestedCount apply to the resulting episodes.
            var seriesQuery = new InternalItemsQuery(user)
            {
                EnableTotalRecordCount = false,
            };
            var series = GetChildrenByLetter(
                serverItem.Item,
                seriesQuery,
                BaseItemKind.Series,
                serverItem.IdSuffix);
            if (series.Items.Count == 0)
            {
                // An empty ancestor filter would otherwise search outside this bucket.
                return new QueryResult<BaseItem>(startIndex, 0, Array.Empty<BaseItem>());
            }

            query.Recursive = true;
            query.AncestorIds = series.Items.Select(i => i.Item.Id).ToArray();
            query.IncludeItemTypes = [BaseItemKind.Episode];
            query.IsFolder = false;
            query.MediaTypes = [MediaType.Video];
            return _libraryManager.GetItemsResult(query);
        }

        var itemType = serverItem.StubType == StubType.MovieLetter
            ? BaseItemKind.Movie
            : BaseItemKind.Series;
        var result = GetChildrenByLetter(serverItem.Item, query, itemType, serverItem.IdSuffix);
        return new QueryResult<BaseItem>(
            startIndex,
            result.TotalRecordCount,
            result.Items.Select(i => i.Item).ToArray());
    }

    /// <summary>
    /// Returns a new DtoOptions object.
    /// </summary>
    /// <returns>The <see cref="DtoOptions"/>.</returns>
    private static DtoOptions GetDtoOptions()
    {
        return new DtoOptions(true);
    }

    /// <summary>
    /// Returns the User items meeting the criteria.
    /// </summary>
    /// <param name="item">The <see cref="BaseItem"/>.</param>
    /// <param name="stubType">The <see cref="StubType"/>.</param>
    /// <param name="user">The <see cref="User"/>.</param>
    /// <param name="sort">The <see cref="SortCriteria"/>.</param>
    /// <param name="startIndex">The start index.</param>
    /// <param name="limit">The maximum number to return.</param>
    /// <param name="ancestorId">The library to scope globally shared named items such as genres to.</param>
    /// <param name="idSuffix">The virtual folder ID suffix.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private QueryResult<ServerItem> GetUserItems(
        BaseItem item,
        StubType? stubType,
        User? user,
        SortCriteria sort,
        int? startIndex,
        int? limit,
        Guid? ancestorId = null,
        string? idSuffix = null)
    {
        if (user is not null)
        {
            if (item is UserRootFolder)
            {
                return GetUserViews(user, startIndex, limit);
            }

            switch (item)
            {
                case MusicGenre:
                    return GetMusicGenreItems(item, user, sort, startIndex, limit, ancestorId);
                case MusicArtist:
                    return GetMusicArtistItems(item, user, sort, startIndex, limit, ancestorId);
                case Genre:
                    return GetGenreItems(item, user, sort, startIndex, limit, ancestorId);
            }

            if (item is IHasCollectionType collectionFolder)
            {
                switch (collectionFolder.CollectionType)
                {
                    case CollectionType.music when stubType != StubType.Folder:
                        return GetMusicFolders(item, user, stubType, sort, startIndex, limit);
                    case CollectionType.movies:
                        return GetMovieFolders(
                            item,
                            user,
                            stubType == StubType.Folder ? null : stubType,
                            idSuffix,
                            sort,
                            startIndex,
                            limit);
                    case CollectionType.tvshows:
                        return GetTvFolders(
                            item,
                            user,
                            stubType == StubType.Folder ? null : stubType,
                            idSuffix,
                            sort,
                            startIndex,
                            limit);
                    case CollectionType.folders when stubType != StubType.Folder:
                        return GetFolders(user, startIndex, limit);
                    case CollectionType.livetv when stubType != StubType.Folder:
                        return GetLiveTvChannels(user, sort, startIndex, limit);
                }
            }
        }

        if (stubType.HasValue && stubType.Value != StubType.Folder)
        {
            // TODO should this be doing something?
            return new QueryResult<ServerItem>();
        }

        var folder = (Folder)item;

        var query = new InternalItemsQuery(user)
        {
            Limit = limit,
            StartIndex = startIndex,
            IsVirtualItem = false,
            ExcludeItemTypes = [BaseItemKind.Book],
            IsPlaceHolder = false,
            DtoOptions = GetDtoOptions(),
            OrderBy = GetOrderBy(sort, folder.IsPreSorted),
        };

        var queryResult = folder.GetItems(query);

        return ToResult(startIndex, queryResult);
    }

    /// <summary>
    /// Gets a value indicating whether a listing entry is written as a container.
    /// </summary>
    /// <param name="serverItem">The <see cref="ServerItem"/>.</param>
    /// <returns><c>true</c> if the entry is written as a container.</returns>
    private static bool IsWrittenAsContainer(ServerItem serverItem)
        => serverItem.Item.IsDisplayedAsFolder || serverItem.StubType.HasValue;

    /// <summary>
    /// Gets a value indicating whether the children of a listing entry are the folder's own
    /// children, so that they can be counted without listing them.
    /// </summary>
    /// <param name="serverItem">The <see cref="ServerItem"/>.</param>
    /// <returns><c>true</c> if the entry is a folder listing its own children.</returns>
    private bool ListsOwnChildren(ServerItem serverItem)
    {
        // A user view holds no children of its own, it delegates to the libraries behind it, and a
        // container that aggregates by name lists what a query returns rather than any children
        if (serverItem.Item is not Folder
            || serverItem.Item is UserView
            || GetNameItemKind(serverItem.Item) is not null
            || serverItem.StubType is not (null or StubType.Folder))
        {
            return false;
        }

        // Mirrors the dispatching in GetUserItems: a collection folder is listed as a set of stub
        // containers rather than as its own children, unless it is browsed as a plain folder
        return serverItem.StubType == StubType.Folder
            || _user is null
            || serverItem.Item is not IHasCollectionType
            {
                CollectionType: CollectionType.music
                    or CollectionType.movies
                    or CollectionType.tvshows
                    or CollectionType.folders
                    or CollectionType.livetv
            };
    }

    /// <summary>
    /// Determines the child count of every container in a listing, without listing the content of
    /// each of them.
    /// </summary>
    /// <param name="children">The listing.</param>
    /// <param name="parent">The <see cref="BaseItem"/> being browsed, if any.</param>
    /// <param name="sort">The <see cref="SortCriteria"/>.</param>
    /// <param name="ancestorIds">Receives the library each entry is scoped to, if any.</param>
    /// <returns>The child counts, in the order of <paramref name="children"/>.</returns>
    private int[] GetChildCounts(IReadOnlyList<ServerItem> children, BaseItem? parent, SortCriteria sort, Guid?[]? ancestorIds = null)
    {
        var counts = new int[children.Count];

        // Folders are counted in a single batched query instead of one query per entry
        List<Guid> folderIds = [];
        List<int> folderIndexes = [];

        // Stub containers cannot be batched into one query, so they are counted side by side
        List<(int Index, ServerItem Child, Guid? AncestorId)> stubs = [];

        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index];

            // A genre or artist aggregates content from every library. An id browsed into already
            // names the library to stay within; one being listed takes it from its parent.
            var ancestorId = child.AncestorId
                             ?? (parent is not null && child.Item is IItemByName ? parent.Id : null);
            if (ancestorIds is not null)
            {
                ancestorIds[index] = ancestorId;
            }

            if (!IsWrittenAsContainer(child))
            {
                continue;
            }

            var genreCount = GetGenreChildCount(child);
            if (genreCount.HasValue)
            {
                counts[index] = genreCount.Value;
            }
            else if (ListsOwnChildren(child))
            {
                folderIds.Add(child.Item.Id);
                folderIndexes.Add(index);
            }
            else
            {
                // A stub container lists the result of a query rather than children of its own, so
                // it needs a query of its own to be counted. Collected and run together below.
                stubs.Add((index, child, ancestorId));
            }
        }

        if (folderIds.Count > 0)
        {
            var folderCounts = _libraryManager.GetChildCountBatch(folderIds, _user);
            for (var i = 0; i < folderIds.Count; i++)
            {
                counts[folderIndexes[i]] = folderCounts.GetValueOrDefault(folderIds[i]);
            }
        }

        if (stubs.Count > 0)
        {
            // The counts of a menu are independent of each other, so running them side by side
            // makes a menu cost about as much as its slowest entry rather than the sum of them all.
            // Bounded, so browsing cannot fan a single request out across the whole library at once.
            Parallel.ForEach(
                stubs,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(stubs.Count, MaxCountConcurrency) },
                stub =>
                {
                    // A stub that lists its query as it comes reports that query's own total, so
                    // asking for a single row is enough to learn the count. One that assembles its
                    // listing has to be listed, because the total of the query behind it counts
                    // content the listing never shows.
                    counts[stub.Index] = IsListedInMemory(stub.Child)
                        ? GetUserItemsWithParts(stub.Child.Item, stub.Child.StubType, _user, sort, null, null, stub.AncestorId)
                            .TotalRecordCount
                        : GetUserItems(stub.Child.Item, stub.Child.StubType, _user, sort, 0, 1, stub.AncestorId)
                            .TotalRecordCount;
                });
        }

        return counts;
    }

    /// <summary>
    /// Gets a value indicating whether the listing of a stub is assembled in memory rather than
    /// taken from a query as it comes, so that its count is the length of that listing.
    /// </summary>
    /// <param name="item">The <see cref="ServerItem"/>.</param>
    /// <returns><c>true</c> if only listing it reports its count.</returns>
    /// <remarks>
    /// These listings cap themselves, so asking their query for a total reports how much content
    /// exists rather than how much the listing shows, and a client is told a count it can never
    /// browse to. All of them are small, so listing them to count them is cheap. A genre or artist
    /// carries no stub type of its own but lists a plain query, which reports its own total.
    /// </remarks>
    private static bool IsListedInMemory(ServerItem item)
        => item.StubType is StubType.ContinueWatching
            or StubType.NextUp
            or StubType.Latest
            || (item.StubType is null && GetNameItemKind(item.Item) is null);

    private static int? GetGenreChildCount(ServerItem child)
        => child.ItemCounts is null
            ? null
            : child.Item switch
            {
                MusicGenre => child.ItemCounts.AlbumCount,
                Genre => child.ItemCounts.MovieCount + child.ItemCounts.SeriesCount,
                _ => null
            };

    /// <summary>
    /// Gets the kind a container aggregating content by name is counted as.
    /// </summary>
    /// <param name="item">The <see cref="BaseItem"/>.</param>
    /// <returns>The <see cref="BaseItemKind"/>, or <c>null</c> if the item is not one.</returns>
    private static BaseItemKind? GetNameItemKind(BaseItem item)
        => item switch
        {
            MusicGenre => BaseItemKind.MusicGenre,
            MusicArtist => BaseItemKind.MusicArtist,
            Genre => BaseItemKind.Genre,
            _ => null
        };

    /// <summary>
    /// Returns the User items meeting the criteria, with every stacked (multi-part) video replaced
    /// by one item per part.
    /// </summary>
    /// <param name="item">The <see cref="BaseItem"/>.</param>
    /// <param name="stubType">The <see cref="StubType"/>.</param>
    /// <param name="user">The <see cref="User"/>.</param>
    /// <param name="sort">The <see cref="SortCriteria"/>.</param>
    /// <param name="startIndex">The start index.</param>
    /// <param name="limit">The maximum number to return.</param>
    /// <param name="ancestorId">The library to scope the listing to, if any.</param>
    /// <param name="idSuffix">The virtual folder ID suffix.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private QueryResult<ServerItem> GetUserItemsWithParts(BaseItem item, StubType? stubType, User? user, SortCriteria sort, int? startIndex, int? limit, Guid? ancestorId = null, string? idSuffix = null)
    {
        // A listing that is assembled rather than queried has to be read whole and paged here.
        if (IsAssembledListing(item, stubType))
        {
            return ApplyPaging(ExpandStackedVideos(GetUserItems(item, stubType, user, sort, null, null, ancestorId, idSuffix).Items, user), startIndex, limit);
        }

        // Everything else is a query that can return the page on its own, where reading every row
        // of a library to hand back one page of it costs the same whether a client asked for
        // twenty rows or for all of them. The parts of a multi-part video are expanded inside the
        // page that carries the video, and the page is trimmed again so that a client never gets
        // back more rows than it asked for.
        var result = GetUserItems(item, stubType, user, sort, startIndex, limit, ancestorId, idSuffix);

        var expanded = ExpandStackedVideos(result.Items, user);

        return new QueryResult<ServerItem>(
            startIndex,
            result.TotalRecordCount,
            limit.HasValue && expanded.Length > limit.Value ? expanded[..limit.Value] : expanded);
    }

    /// <summary>
    /// Gets a value indicating whether a listing is assembled in memory rather than taken from a
    /// query, so that it has to be read whole before a page of it can be handed back.
    /// </summary>
    /// <param name="item">The <see cref="BaseItem"/> being listed.</param>
    /// <param name="stubType">The <see cref="StubType"/>, if any.</param>
    /// <returns><c>true</c> if the listing is assembled.</returns>
    /// <remarks>
    /// This mirrors the dispatching in GetUserItems. The root and a library browsed as its menu of
    /// stubs are fixed arrays, and Latest, Continue Watching and Next Up cap or renumber theirs.
    /// All of them are small, so reading them whole costs little.
    /// </remarks>
    private static bool IsAssembledListing(BaseItem item, StubType? stubType)
    {
        if (item is UserRootFolder
            || stubType is StubType.Latest or StubType.ContinueWatching or StubType.NextUp)
        {
            return true;
        }

        // A library with no stub type of its own is listed as its menu. The folders and live tv
        // views are not, they list their content straight from a query.
        return stubType is null
            && item is IHasCollectionType
            {
                CollectionType: CollectionType.music or CollectionType.movies or CollectionType.tvshows
            };
    }

    /// <summary>
    /// Returns the search results meeting the criteria, with every stacked (multi-part) video
    /// replaced by one item per part.
    /// </summary>
    /// <param name="serverItem">The searched item and its Fleet virtual-folder context.</param>
    /// <param name="user">The <see cref="User"/>.</param>
    /// <param name="search">The <see cref="SearchCriteria"/>.</param>
    /// <param name="sort">The <see cref="SortCriteria"/>.</param>
    /// <param name="startIndex">The start index.</param>
    /// <param name="limit">The maximum number to return.</param>
    /// <param name="nameStartsWith">The letter the results have to start with, if any.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private QueryResult<ServerItem> GetSearchResultWithParts(ServerItem serverItem, User? user, SearchCriteria search, SortCriteria sort, int? startIndex, int? limit, string? nameStartsWith = null)
    {
        // A search spans a whole library, so the page has to come out of the query: reading every
        // match to hand back one page of it costs the same whether the client wants 20 rows or all
        // of them. The parts of a multi-part video are expanded within the page that carries the
        // video, and the page is trimmed again afterwards so that a client never gets back more
        // rows than it asked for.
        var result = GetChildrenSorted(serverItem, user, search, sort, startIndex, limit, nameStartsWith);

        var expanded = ExpandStackedVideos(ToResult(startIndex, result).Items, user);

        return new QueryResult<ServerItem>(
            startIndex,
            result.TotalRecordCount,
            limit.HasValue && expanded.Length > limit.Value ? expanded[..limit.Value] : expanded);
    }

    /// <summary>
    /// Replaces every stacked (multi-part) video in a listing by one item per part, so that the
    /// parts beyond the first one can be browsed and played as well.
    /// </summary>
    /// <param name="items">The listing to expand.</param>
    /// <param name="user">The <see cref="User"/>.</param>
    /// <returns>The expanded listing.</returns>
    private static ServerItem[] ExpandStackedVideos(IReadOnlyList<ServerItem> items, User? user)
    {
        var stacked = items.Any(IsStackedVideo);
        if (!stacked)
        {
            return items as ServerItem[] ?? [.. items];
        }

        List<ServerItem> expanded = [];
        foreach (var serverItem in items)
        {
            if (!IsStackedVideo(serverItem))
            {
                expanded.Add(serverItem);
                continue;
            }

            var video = (Video)serverItem.Item;
            var partNumber = 1;

            expanded.Add(new ServerItem(
                video,
                serverItem.StubType,
                serverItem.VirtualFolderName,
                serverItem.IdSuffix,
                serverItem.AncestorId,
                partNumber,
                serverItem.ItemCounts));
            foreach (var part in video.GetAdditionalParts(user))
            {
                expanded.Add(new ServerItem(part, null, partNumber: ++partNumber));
            }
        }

        return [.. expanded];
    }

    /// <summary>
    /// Gets a value indicating whether a listing entry is a stacked (multi-part) video.
    /// </summary>
    /// <param name="serverItem">The <see cref="ServerItem"/>.</param>
    /// <returns><c>true</c> if the entry is a stacked video.</returns>
    private static bool IsStackedVideo(ServerItem serverItem)
        => serverItem.Item is Video { IsStacked: true } && !serverItem.StubType.HasValue;

    /// <summary>
    /// Gets the one based part number of a video that belongs to a stacked (multi-part) video.
    /// </summary>
    /// <param name="item">The <see cref="BaseItem"/>.</param>
    /// <returns>The part number, or <c>null</c> when the item is not part of a stack.</returns>
    private int? GetPartNumber(BaseItem item)
    {
        if (item is not Video video)
        {
            return null;
        }

        if (video.IsStacked)
        {
            return 1;
        }

        if (video.OwnerId.Equals(default) || video.GetOwner() is not Video { IsStacked: true } owner)
        {
            return null;
        }

        var partNumber = 1;
        foreach (var part in owner.GetAdditionalParts(_user))
        {
            partNumber++;
            if (part.Id.Equals(video.Id))
            {
                return partNumber;
            }
        }

        return null;
    }

    /// <summary>
    /// Applies the requested paging window to a listing.
    /// </summary>
    /// <param name="serverItems">The full listing.</param>
    /// <param name="startIndex">The start index.</param>
    /// <param name="limit">The maximum number to return.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private static QueryResult<ServerItem> ApplyPaging(ServerItem[] serverItems, int? startIndex, int? limit)
        => new(startIndex, serverItems.Length, GetTrimmedServerItemsArray(serverItems, startIndex, limit));

    /// <summary>
    /// Returns the Live Tv Channels meeting the criteria.
    /// </summary>
    /// <param name="user">The <see cref="User"/>.</param>
    /// <param name="sort">The <see cref="SortCriteria"/>.</param>
    /// <param name="startIndex">The start index.</param>
    /// <param name="limit">The maximum number to return.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private QueryResult<ServerItem> GetLiveTvChannels(
        User user,
        SortCriteria sort,
        int? startIndex,
        int? limit)
    {
        var query = new InternalItemsQuery(user)
        {
            StartIndex = startIndex,
            Limit = limit,
            IncludeItemTypes = [BaseItemKind.LiveTvChannel],
            OrderBy = GetOrderBy(sort, false),
        };

        var result = _libraryManager.GetItemsResult(query);

        return ToResult(startIndex, result);
    }

    /// <summary>
    /// Returns the music folders meeting the criteria.
    /// </summary>
    /// <param name="item">The <see cref="BaseItem"/>.</param>
    /// <param name="user">The <see cref="User"/>.</param>
    /// <param name="stubType">The <see cref="StubType"/>.</param>
    /// <param name="sort">The <see cref="SortCriteria"/>.</param>
    /// <param name="startIndex">The start index.</param>
    /// <param name="limit">The maximum number to return.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private QueryResult<ServerItem> GetMusicFolders(
        BaseItem item,
        User user,
        StubType? stubType,
        SortCriteria sort,
        int? startIndex,
        int? limit)
    {
        var query = new InternalItemsQuery(user)
        {
            StartIndex = startIndex,
            Limit = limit,
            OrderBy = GetOrderBy(sort, false),
        };

        switch (stubType)
        {
            case StubType.Latest:
                return GetLatest(item, query, BaseItemKind.Audio);
            case StubType.Playlists:
                return GetMusicPlaylists(query);
            case StubType.Albums:
                return GetChildrenOfItem(item, query, BaseItemKind.MusicAlbum);
            case StubType.Artists:
                return GetMusicArtists(item, query);
            case StubType.AlbumArtists:
                return GetMusicAlbumArtists(item, query);
            case StubType.FavoriteAlbums:
                return GetChildrenOfItem(item, query, BaseItemKind.MusicAlbum, true);
            case StubType.FavoriteArtists:
                return GetFavoriteArtists(item, query);
            case StubType.FavoriteSongs:
                return GetChildrenOfItem(item, query, BaseItemKind.Audio, true);
            case StubType.Songs:
                return GetChildrenOfItem(item, query, BaseItemKind.Audio);
            case StubType.Genres:
                return GetMusicGenres(item, query);
        }

        var serverItems = new ServerItem[]
        {
            new(item, StubType.Latest),
            new(item, StubType.Playlists),
            new(item, StubType.Albums),
            new(item, StubType.AlbumArtists),
            new(item, StubType.Artists),
            new(item, StubType.Songs),
            new(item, StubType.Genres),
            new(item, StubType.FavoriteArtists),
            new(item, StubType.FavoriteAlbums),
            new(item, StubType.FavoriteSongs),
        };

        serverItems = GetTrimmedServerItemsArray(serverItems, startIndex, limit);
        return new QueryResult<ServerItem>(startIndex, serverItems.Length, serverItems);
    }

    /// <summary>
    /// Returns the movie folders meeting the criteria.
    /// </summary>
    /// <param name="item">The <see cref="BaseItem"/>.</param>
    /// <param name="user">The <see cref="User"/>.</param>
    /// <param name="stubType">The <see cref="StubType"/>.</param>
    /// <param name="idSuffix">The alphabetical bucket encoded in the object ID.</param>
    /// <param name="sort">The <see cref="SortCriteria"/>.</param>
    /// <param name="startIndex">The start index.</param>
    /// <param name="limit">The maximum number to return.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private QueryResult<ServerItem> GetMovieFolders(
        BaseItem item,
        User user,
        StubType? stubType,
        string? idSuffix,
        SortCriteria sort,
        int? startIndex,
        int? limit)
    {
        var query = new InternalItemsQuery(user)
        {
            StartIndex = startIndex,
            Limit = limit,
            OrderBy = GetOrderBy(sort, false),
        };

        switch (stubType)
        {
            case StubType.All:
                return GetAlphabetFolders(item, StubType.MovieLetter, startIndex, limit);
            case StubType.MovieLetter:
                return GetChildrenByLetter(item, query, BaseItemKind.Movie, idSuffix);
            case StubType.VideoLatest:
                return GetVideoLatest(item, query, BaseItemKind.Movie);
            case StubType.VideoGenres:
                return GetVideoGenreFolders(item, query, StubType.MovieGenre);
            case StubType.MovieGenre:
                return GetVideoGenreItems(
                    item,
                    query,
                    BaseItemKind.Movie,
                    idSuffix);
            case StubType.Movies:
                return GetAlphabetFolders(item, StubType.MovieLetter, startIndex, limit);
            case StubType.Latest:
                return GetVideoLatest(item, query, BaseItemKind.Movie);
            case StubType.Genres:
                return GetVideoGenreFolders(item, query, StubType.MovieGenre);
            case StubType.ContinueWatching:
                return GetMovieContinueWatching(item, query);
            case StubType.Collections:
                return GetMovieCollections(query);
            case StubType.Favorites:
                return GetChildrenOfItem(item, query, BaseItemKind.Movie, true);
        }

        var array = new ServerItem[]
        {
            new(item, StubType.All),
            new(item, StubType.VideoLatest),
            new(item, StubType.VideoGenres),
        };

        var totalRecordCount = array.Length;
        array = GetTrimmedServerItemsArray(array, startIndex, limit);
        return new QueryResult<ServerItem>(startIndex, totalRecordCount, array);
    }

    /// <summary>
    /// Returns the user views, which is the top level listing every other client is served.
    /// </summary>
    /// <param name="user">The <see cref="User"/>.</param>
    /// <param name="startIndex">The start index.</param>
    /// <param name="limit">The maximum number to return.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    /// <remarks>
    /// Listing the raw children of the user root folder instead would skip library grouping, the
    /// hidden library and display order preferences, and the synthetic views such as "Folders".
    /// </remarks>
    private QueryResult<ServerItem> GetUserViews(User user, int? startIndex, int? limit)
    {
        var views = _userViewManager.GetUserViews(new UserViewQuery { User = user });

        return ApplyPaging([.. views.Select(i => new ServerItem(i, null))], startIndex, limit);
    }

    /// <summary>
    /// Returns the folders meeting the criteria.
    /// </summary>
    /// <param name="user">The <see cref="User"/>.</param>
    /// <param name="startIndex">The start index.</param>
    /// <param name="limit">The maximum number to return.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private QueryResult<ServerItem> GetFolders(User user, int? startIndex, int? limit)
    {
        var folders = _libraryManager.GetUserRootFolder().GetChildren(user, true);
        var totalRecordCount = folders.Count;
        // Handle paging
        var items = folders
            .OrderBy(i => i.SortName)
            .Skip(startIndex ?? 0)
            .Take(limit ?? int.MaxValue)
            .Select(i => new ServerItem(i, StubType.Folder))
            .ToArray();

        return new QueryResult<ServerItem>(startIndex, totalRecordCount, items);
    }

    /// <summary>
    /// Returns the TV folders meeting the criteria.
    /// </summary>
    /// <param name="item">The <see cref="BaseItem"/>.</param>
    /// <param name="user">The <see cref="User"/>.</param>
    /// <param name="stubType">The <see cref="StubType"/>.</param>
    /// <param name="idSuffix">The alphabetical bucket encoded in the object ID.</param>
    /// <param name="sort">The <see cref="SortCriteria"/>.</param>
    /// <param name="startIndex">The start index.</param>
    /// <param name="limit">The maximum number to return.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private QueryResult<ServerItem> GetTvFolders(
        BaseItem item,
        User user,
        StubType? stubType,
        string? idSuffix,
        SortCriteria sort,
        int? startIndex,
        int? limit)
    {
        var query = new InternalItemsQuery(user)
        {
            StartIndex = startIndex,
            Limit = limit,
            OrderBy = GetOrderBy(sort, false),
        };

        switch (stubType)
        {
            case StubType.All:
                return GetAlphabetFolders(item, StubType.SeriesLetter, startIndex, limit);
            case StubType.SeriesLetter:
                return GetChildrenByLetter(item, query, BaseItemKind.Series, idSuffix);
            case StubType.VideoLatest:
                return GetVideoLatest(item, query, BaseItemKind.Episode);
            case StubType.VideoGenres:
                return GetVideoGenreFolders(item, query, StubType.SeriesGenre);
            case StubType.SeriesGenre:
                return GetVideoGenreItems(
                    item,
                    query,
                    BaseItemKind.Series,
                    idSuffix);
            case StubType.Series:
                return GetAlphabetFolders(item, StubType.SeriesLetter, startIndex, limit);
            case StubType.Latest:
                return GetVideoLatest(item, query, BaseItemKind.Episode);
            case StubType.Genres:
                return GetVideoGenreFolders(item, query, StubType.SeriesGenre);
            case StubType.ContinueWatching:
                return GetMovieContinueWatching(item, query);
            case StubType.NextUp:
                return GetNextUp(item, query);
            case StubType.FavoriteSeries:
                return GetChildrenOfItem(item, query, BaseItemKind.Series, true);
            case StubType.FavoriteEpisodes:
                return GetChildrenOfItem(item, query, BaseItemKind.Episode, true);
        }

        var serverItems = new ServerItem[]
        {
            new(item, StubType.All),
            new(item, StubType.VideoLatest),
            new(item, StubType.VideoGenres),
        };

        var totalRecordCount = serverItems.Length;
        serverItems = GetTrimmedServerItemsArray(serverItems, startIndex, limit);
        return new QueryResult<ServerItem>(startIndex, totalRecordCount, serverItems);
    }

    /// <summary>
    /// Returns the Movies that are part watched that meet the criteria.
    /// </summary>
    /// <param name="parent">The <see cref="BaseItem"/>.</param>
    /// <param name="query">The <see cref="InternalItemsQuery"/>.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private QueryResult<ServerItem> GetMovieContinueWatching(
        BaseItem parent,
        InternalItemsQuery query)
    {
        query.Recursive = true;
        query.Parent = parent;

        query.OrderBy =
        [
            (ItemSortBy.DatePlayed, SortOrder.Descending),
            (ItemSortBy.SortName, SortOrder.Ascending),
        ];

        query.IsResumable = true;
        query.Limit ??= 10;
        if (_movieQueryScope.TryApply(parent, query))
        {
            query.IncludeItemTypes = [BaseItemKind.Movie];
        }

        var result = _libraryManager.GetItemsResult(query);

        return ToResult(query.StartIndex, result);
    }

    /// <summary>
    /// Returns the Movie collections meeting the criteria.
    /// </summary>
    /// <param name="query">The see cref="InternalItemsQuery"/>.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private QueryResult<ServerItem> GetMovieCollections(InternalItemsQuery query)
    {
        query.Recursive = true;
        query.IncludeItemTypes = [BaseItemKind.BoxSet];

        var result = _libraryManager.GetItemsResult(query);

        return ToResult(query.StartIndex, result);
    }

    /// <summary>
    /// Returns the alphabetical virtual folders.
    /// </summary>
    /// <param name="parent">The parent library item.</param>
    /// <param name="stubType">The virtual folder type.</param>
    /// <param name="startIndex">The start index.</param>
    /// <param name="limit">The maximum number to return.</param>
    /// <returns>The alphabetical virtual folders.</returns>
    private static QueryResult<ServerItem> GetAlphabetFolders(
        BaseItem parent,
        StubType stubType,
        int? startIndex,
        int? limit)
    {
        var buckets = new[] { "#" }
            .Concat(Enumerable.Range('A', 26).Select(value => ((char)value).ToString()))
            .Append("Other")
            .Select(bucket => new ServerItem(
                parent,
                stubType,
                bucket,
                EncodeAlphabetBucket(bucket)))
            .ToArray();

        var items = GetTrimmedServerItemsArray(buckets, startIndex, limit);
        return new QueryResult<ServerItem>(startIndex, buckets.Length, items);
    }

    private QueryResult<ServerItem> GetChildrenByLetter(
        BaseItem parent,
        InternalItemsQuery query,
        BaseItemKind itemType,
        string? encodedBucket)
    {
        var bucket = DecodeAlphabetBucket(encodedBucket);
        if (bucket is null)
        {
            Logger.LogWarning(
                "DLNA alphabetical folder has an invalid bucket {Bucket} for {ParentId}.",
                encodedBucket,
                parent.Id);
            return new QueryResult<ServerItem>(query.StartIndex, 0, Array.Empty<ServerItem>());
        }

        query.IsMissing = false;
        query.IsVirtualItem = false;
        query.IsPlaceHolder = false;
        query.DtoOptions = GetDtoOptions();

        if (bucket == "#")
        {
            query.NameLessThan = "a";
        }
        else if (bucket == "Other")
        {
            query.NameStartsWithOrGreater = "{";
        }
        else
        {
            // Use Jellyfin's explicit SortName prefix filter. A pair of name-range
            // comparisons has differed across server versions and database collations.
            query.NameStartsWith = bucket.ToLowerInvariant();
        }

        var result = GetChildrenOfItem(parent, query, itemType);
        Logger.LogDebug(
            "DLNA alphabetical {ItemType} bucket {Bucket} in {ParentId}: returned {Returned} of {Total} (start {Start}, limit {Limit}).",
            itemType,
            bucket,
            parent.Id,
            result.Items.Count,
            result.TotalRecordCount,
            query.StartIndex,
            query.Limit);
        return result;
    }

    private static string EncodeAlphabetBucket(string bucket)
    {
        if (bucket == "#")
        {
            return "0-9";
        }

        return string.Equals(bucket, "Other", StringComparison.OrdinalIgnoreCase)
            ? "other"
            : bucket;
    }

    private static string? DecodeAlphabetBucket(string? encodedBucket)
    {
        if (string.IsNullOrWhiteSpace(encodedBucket))
        {
            return null;
        }

        if (string.Equals(encodedBucket, "0-9", StringComparison.OrdinalIgnoreCase))
        {
            return "#";
        }

        if (string.Equals(encodedBucket, "other", StringComparison.OrdinalIgnoreCase))
        {
            return "Other";
        }

        var bucket = encodedBucket.ToUpperInvariant();
        return bucket.Length == 1 && bucket[0] is >= 'A' and <= 'Z' ? bucket : null;
    }

    private static string? GetVirtualFolderName(StubType? stubType, string? idSuffix) =>
        stubType switch
        {
            StubType.MovieLetter or StubType.SeriesLetter => DecodeAlphabetBucket(idSuffix),
            StubType.MovieGenre or StubType.SeriesGenre => DecodeVirtualValue(idSuffix),
            _ => null,
        };

    private static StubType? GetVirtualParentStub(StubType? stubType) =>
        stubType switch
        {
            StubType.MovieLetter or StubType.SeriesLetter => StubType.All,
            StubType.MovieGenre or StubType.SeriesGenre => StubType.VideoGenres,
            _ => null,
        };

    private static string EncodeVirtualValue(string value) =>
        Convert.ToHexString(Encoding.UTF8.GetBytes(value)).ToLowerInvariant();

    private static string? DecodeVirtualValue(string? encodedValue)
    {
        if (string.IsNullOrWhiteSpace(encodedValue))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromHexString(encodedValue));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private QueryResult<ServerItem> GetChildrenOfItem(
        BaseItem parent,
        InternalItemsQuery query,
        BaseItemKind itemType,
        bool isFavorite = false)
    {
        query.Recursive = true;
        query.Parent = parent;
        query.IncludeItemTypes = [itemType];
        if (itemType == BaseItemKind.Movie)
        {
            _movieQueryScope.TryApply(parent, query);
        }

        if (isFavorite)
        {
            query.IsFavorite = true;
        }

        if (isFavorite)
        {
            query.IsFavorite = true;
        }

        var result = _libraryManager.GetItemsResult(query);

        return ToResult(query.StartIndex, result);
    }

    /// <summary>
    /// Returns synthetic genre folders scoped directly to a video library.
    /// </summary>
    /// <param name="parent">The video library.</param>
    /// <param name="query">The genre-list query.</param>
    /// <param name="genreStubType">The synthetic movie or series genre stub.</param>
    /// <returns>The synthetic genre folders.</returns>
    private QueryResult<ServerItem> GetVideoGenreFolders(
        BaseItem parent,
        InternalItemsQuery query,
        StubType genreStubType)
    {
        var startIndex = query.StartIndex ?? 0;
        var limit = query.Limit ?? int.MaxValue;

        if (query.OrderBy.Count == 0)
        {
            query.OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)];
        }

        // Genre lists are tiny compared with the media library. Ask Jellyfin for
        // the complete scoped list once and page it in memory; this avoids a
        // separate COUNT query and avoids client-specific genre paging quirks.
        query.StartIndex = null;
        query.Limit = null;
        query.AncestorIds = [parent.Id];
        query.EnableTotalRecordCount = false;

        var genresResult = _libraryManager.GetGenres(query);
        var allGenreNames = genresResult.Items
            .Select(entry => entry.Item.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (allGenreNames.Length == 0)
        {
            var itemType = genreStubType == StubType.MovieGenre
                ? BaseItemKind.Movie
                : BaseItemKind.Series;
            var fallbackQuery = new InternalItemsQuery(query.User)
            {
                Recursive = true,
                IsMissing = false,
                IsVirtualItem = false,
                IsPlaceHolder = false,
                DtoOptions = new DtoOptions(false),
                OrderBy = [],
                EnableTotalRecordCount = false,
            };
            var fallbackItems = GetChildrenOfItem(parent, fallbackQuery, itemType);
            allGenreNames = fallbackItems.Items
                .SelectMany(serverItem => serverItem.Item.Genres)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Logger.LogWarning(
                "DLNA fleet genre index used item-derived fallback for library {LibraryId}; genres={GenreCount}.",
                parent.Id,
                allGenreNames.Length);
        }

        var totalRecordCount = allGenreNames.Length;
        var genreNames = allGenreNames
            .Skip(startIndex)
            .Take(limit)
            .ToArray();

        var serverItems = genreNames
            .Select(name => new ServerItem(
                parent,
                genreStubType,
                name,
                EncodeVirtualValue(name)))
            .ToArray();

        Logger.LogInformation(
            "DLNA fleet genres: profile={Profile}, library={LibraryId}, type={GenreType}, returned={Returned}, total={Total}.",
            _profile.Name,
            parent.Id,
            genreStubType,
            serverItems.Length,
            totalRecordCount);

        return new QueryResult<ServerItem>(
            startIndex,
            totalRecordCount,
            serverItems);
    }

    /// <summary>
    /// Returns movies or series from a synthetic genre folder.
    /// </summary>
    /// <param name="parent">The source video library.</param>
    /// <param name="query">The item query.</param>
    /// <param name="itemType">The movie or series item type.</param>
    /// <param name="encodedGenre">The encoded genre name from the virtual object id.</param>
    /// <returns>The matching video items.</returns>
    private QueryResult<ServerItem> GetVideoGenreItems(
        BaseItem parent,
        InternalItemsQuery query,
        BaseItemKind itemType,
        string? encodedGenre)
    {
        var genre = DecodeVirtualValue(encodedGenre);
        if (string.IsNullOrWhiteSpace(genre))
        {
            Logger.LogWarning(
                "DLNA fleet genre folder has an invalid genre token {GenreToken} for library {LibraryId}.",
                encodedGenre,
                parent.Id);
            return new QueryResult<ServerItem>(
                query.StartIndex,
                0,
                Array.Empty<ServerItem>());
        }

        query.Genres = [genre];
        query.IsMissing = false;
        query.IsVirtualItem = false;
        query.IsPlaceHolder = false;
        query.DtoOptions = GetDtoOptions();
        return GetChildrenOfItem(parent, query, itemType);
    }

    /// <summary>
    /// Returns the music genres meeting the criteria.
    /// </summary>
    /// <param name="parent">The <see cref="BaseItem"/>.</param>
    /// <param name="query">The <see cref="InternalItemsQuery"/>.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private QueryResult<ServerItem> GetMusicGenres(BaseItem parent, InternalItemsQuery query)
    {
        // Don't sort
        query.OrderBy = [];
        query.AncestorIds = [parent.Id];
        var genresResult = _libraryManager.GetMusicGenres(query);

        return ToResult(query.StartIndex, genresResult);
    }

    /// <summary>
    /// Returns the music albums by artist that meet the criteria.
    /// </summary>
    /// <param name="parent">The <see cref="BaseItem"/>.</param>
    /// <param name="query">The <see cref="InternalItemsQuery"/>.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private QueryResult<ServerItem> GetMusicAlbumArtists(BaseItem parent, InternalItemsQuery query)
    {
        // Don't sort
        query.OrderBy = [];
        query.AncestorIds = [parent.Id];
        var artists = _libraryManager.GetAlbumArtists(query);

        return ToResult(query.StartIndex, artists);
    }

    /// <summary>
    /// Returns the music artists meeting the criteria.
    /// </summary>
    /// <param name="parent">The <see cref="BaseItem"/>.</param>
    /// <param name="query">The <see cref="InternalItemsQuery"/>.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private QueryResult<ServerItem> GetMusicArtists(BaseItem parent, InternalItemsQuery query)
    {
        // Don't sort
        query.OrderBy = [];
        query.AncestorIds = [parent.Id];
        var artists = _libraryManager.GetArtists(query);
        return ToResult(query.StartIndex, artists);
    }

    /// <summary>
    /// Returns the artists tagged as favourite that meet the criteria.
    /// </summary>
    /// <param name="parent">The <see cref="BaseItem"/>.</param>
    /// <param name="query">The <see cref="InternalItemsQuery"/>.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private QueryResult<ServerItem> GetFavoriteArtists(BaseItem parent, InternalItemsQuery query)
    {
        // Don't sort
        query.OrderBy = [];
        query.AncestorIds = [parent.Id];
        query.IsFavorite = true;
        var artists = _libraryManager.GetArtists(query);
        return ToResult(query.StartIndex, artists);
    }

    /// <summary>
    /// Returns the music playlists meeting the criteria.
    /// </summary>
    /// <param name="query">The query<see cref="InternalItemsQuery"/>.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private QueryResult<ServerItem> GetMusicPlaylists(InternalItemsQuery query)
    {
        query.Parent = null;
        query.IncludeItemTypes = [BaseItemKind.Playlist];
        query.Recursive = true;

        var result = _libraryManager.GetItemsResult(query);

        return ToResult(query.StartIndex, result);
    }

    /// <summary>
    /// Returns the next up item meeting the criteria.
    /// </summary>
    /// <param name="parent">The <see cref="BaseItem"/>.</param>
    /// <param name="query">The <see cref="InternalItemsQuery"/>.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private QueryResult<ServerItem> GetNextUp(BaseItem parent, InternalItemsQuery query)
    {
        query.OrderBy = [];

        var result = _tvSeriesManager.GetNextUp(
            new NextUpQuery
            {
                Limit = query.Limit,
                StartIndex = query.StartIndex,
                // User cannot be null here as the caller has set it
                User = query.User!,
            },
            [parent],
            query.DtoOptions);

        return ToResult(query.StartIndex, result);
    }

    /// <summary>
    /// Returns recently added video items directly from the selected library.
    /// </summary>
    /// <param name="parent">The source video library.</param>
    /// <param name="query">The item query.</param>
    /// <param name="itemType">The movie or episode item type.</param>
    /// <returns>The recently added items.</returns>
    private QueryResult<ServerItem> GetVideoLatest(
        BaseItem parent,
        InternalItemsQuery query,
        BaseItemKind itemType)
    {
        query.OrderBy =
        [
            (ItemSortBy.DateCreated, SortOrder.Descending),
            (ItemSortBy.SortName, SortOrder.Ascending),
        ];
        query.IsMissing = false;
        query.IsVirtualItem = false;
        query.IsPlaceHolder = false;
        query.DtoOptions = GetDtoOptions();
        return GetChildrenOfItem(parent, query, itemType);
    }

    /// <summary>
    /// Returns the latest items of [itemType] meeting the criteria.
    /// </summary>
    /// <param name="parent">The <see cref="BaseItem"/>.</param>
    /// <param name="query">The <see cref="InternalItemsQuery"/>.</param>
    /// <param name="itemType">The item type.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private QueryResult<ServerItem> GetLatest(
        BaseItem parent,
        InternalItemsQuery query,
        BaseItemKind itemType)
    {
        if (itemType == BaseItemKind.Movie && _movieQueryScope.TryApply(parent, query))
        {
            query.IncludeItemTypes = [BaseItemKind.Movie];
            query.IsVirtualItem = false;
            query.DtoOptions = GetDtoOptions();
            query.OrderBy =
            [
                (ItemSortBy.DateCreated, SortOrder.Descending),
                (ItemSortBy.SortName, SortOrder.Descending),
                (ItemSortBy.ProductionYear, SortOrder.Descending),
            ];
            return ToResult(query.StartIndex, _libraryManager.GetItemsResult(query));
        }

        query.OrderBy = [];

        int limit;

        if (itemType == BaseItemKind.Movie)
        {
            // Use the existing default of 50 recent movies as a stable logical
            // view. Keep its total independent of the serialization page size,
            // while retaining Jellyfin's latest-item and user-preference logic.
            limit = 50;
        }
        else if (query.StartIndex > 0)
        {
            limit =
                (query.Limit <= 0) ? int.MaxValue : (query.StartIndex.Value + (query.Limit ?? 50));
        }
        else
        {
            limit = query.Limit ?? 50;
        }

        var items = _userViewManager
            .GetLatestItems(
                new LatestItemsQuery
                {
                    // User cannot be null here as the caller has set it
                    User = query.User!,
                    Limit = limit,
                    IncludeItemTypes = [itemType],
                    ParentId = parent?.Id ?? Guid.Empty,
                    GroupItems = true,
                },
                query.DtoOptions)
            .Select(i => i.Item1 ?? i.Item2.FirstOrDefault())
            .OfType<BaseItem>()
            .ToArray();

        var totalCount = items.Length;
        if (query.StartIndex > 0)
        {
            items = (items.Length <= query.StartIndex) ? [] : items[query.StartIndex.Value..];
        }

        if (itemType == BaseItemKind.Movie)
        {
            var page = items.Take(query.Limit ?? 50)
                .Select(item => new ServerItem(item, null))
                .ToArray();
            return new QueryResult<ServerItem>(query.StartIndex, totalCount, page);
        }

        return ToResult(query.StartIndex, items);
    }

    /// <summary>
    /// Returns music artist items that meet the criteria.
    /// </summary>
    /// <param name="item">The <see cref="BaseItem"/>.</param>
    /// <param name="user">The <see cref="User"/>.</param>
    /// <param name="sort">The <see cref="SortCriteria"/>.</param>
    /// <param name="startIndex">The start index.</param>
    /// <param name="limit">The maximum number to return.</param>
    /// <param name="ancestorId">The library to scope the artist to, if any.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private QueryResult<ServerItem> GetMusicArtistItems(BaseItem item, User user, SortCriteria sort, int? startIndex, int? limit, Guid? ancestorId)
    {
        var query = new InternalItemsQuery(user)
        {
            Recursive = true,
            ArtistIds = [item.Id],
            ParentId = ancestorId ?? Guid.Empty,
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            Limit = limit,
            StartIndex = startIndex,
            DtoOptions = GetDtoOptions(),
            OrderBy = GetOrderBy(sort, false),
        };

        var result = _libraryManager.GetItemsResult(query);

        return ToResult(startIndex, result);
    }

    /// <summary>
    /// Returns the genre items meeting the criteria.
    /// </summary>
    /// <param name="item">The <see cref="BaseItem"/>.</param>
    /// <param name="user">The <see cref="User"/>.</param>
    /// <param name="sort">The <see cref="SortCriteria"/>.</param>
    /// <param name="startIndex">The start index.</param>
    /// <param name="limit">The maximum number to return.</param>
    /// <param name="ancestorId">The library to scope the genre to, if any.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private QueryResult<ServerItem> GetGenreItems(BaseItem item, User user, SortCriteria sort, int? startIndex, int? limit, Guid? ancestorId)
    {
        var query = new InternalItemsQuery(user)
        {
            Recursive = true,
            GenreIds = [item.Id],
            ParentId = ancestorId ?? Guid.Empty,
            IncludeItemTypes =
            [
                BaseItemKind.Movie,
                BaseItemKind.Series
            ],
            Limit = limit,
            StartIndex = startIndex,
            EnableTotalRecordCount = true,
            DtoOptions = limit == 0 ? new DtoOptions(false) : GetDtoOptions(),
            OrderBy = limit == 0 ? [] : GetOrderBy(sort, false),
        };

        if (ancestorId.HasValue)
        {
            query.AncestorIds = [ancestorId.Value];
        }

        var result = _libraryManager.GetItemsResult(query);
        return ToResult(startIndex, result);
    }

    /// <summary>
    /// Returns the music genre items meeting the criteria.
    /// </summary>
    /// <param name="item">The <see cref="BaseItem"/>.</param>
    /// <param name="user">The <see cref="User"/>.</param>
    /// <param name="sort">The <see cref="SortCriteria"/>.</param>
    /// <param name="startIndex">The start index.</param>
    /// <param name="limit">The maximum number to return.</param>
    /// <param name="ancestorId">The library to scope the genre to, if any.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private QueryResult<ServerItem> GetMusicGenreItems(BaseItem item, User user, SortCriteria sort, int? startIndex, int? limit, Guid? ancestorId)
    {
        var query = new InternalItemsQuery(user)
        {
            Recursive = true,
            GenreIds = [item.Id],
            ParentId = ancestorId ?? Guid.Empty,
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            Limit = limit,
            StartIndex = startIndex,
            DtoOptions = GetDtoOptions(),
            OrderBy = GetOrderBy(sort, false),
        };

        var result = _libraryManager.GetItemsResult(query);

        return ToResult(startIndex, result);
    }

    /// <summary>
    /// Converts <see cref="IReadOnlyCollection{BaseItem}"/> into a <see cref="QueryResult{ServerItem}"/>.
    /// </summary>
    /// <param name="startIndex">The start index.</param>
    /// <param name="result">An array of <see cref="BaseItem"/>.</param>
    /// <returns>A <see cref="QueryResult{ServerItem}"/>.</returns>
    private static QueryResult<ServerItem> ToResult(int? startIndex, BaseItem[]? result)
    {
        var serverItems = result?.Select(i => new ServerItem(i, null)).ToArray();

        return new QueryResult<ServerItem>(startIndex, result?.Length ?? 0, serverItems ?? []);
    }

    /// <summary>
    /// Converts a <see cref="QueryResult{BaseItem}"/> to a <see cref="QueryResult{ServerItem}"/>.
    /// </summary>
    /// <param name="startIndex">The index the result started at.</param>
    /// <param name="result">A <see cref="QueryResult{BaseItem}"/>.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private static QueryResult<ServerItem> ToResult(int? startIndex, QueryResult<BaseItem> result)
    {
        var length = result.Items.Count;
        var serverItems = new ServerItem[length];
        for (var i = 0; i < length; i++)
        {
            serverItems[i] = new ServerItem(result.Items[i], null);
        }

        return new QueryResult<ServerItem>(startIndex, result.TotalRecordCount, serverItems);
    }

    /// <summary>
    /// Converts a query result to a <see cref="QueryResult{ServerItem}"/>.
    /// </summary>
    /// <param name="startIndex">The start index.</param>
    /// <param name="result">A <see cref="QueryResult{BaseItem}"/>.</param>
    /// <returns>The <see cref="QueryResult{ServerItem}"/>.</returns>
    private static QueryResult<ServerItem> ToResult(
        int? startIndex,
        QueryResult<(BaseItem Item, ItemCounts ItemCounts)> result)
    {
        var length = result.Items.Count;
        var serverItems = new ServerItem[length];
        for (var i = 0; i < length; i++)
        {
            serverItems[i] = new ServerItem(result.Items[i].Item, null, itemCounts: result.Items[i].ItemCounts);
        }

        return new QueryResult<ServerItem>(startIndex, result.TotalRecordCount, serverItems);
    }

    /// <summary>
    /// Gets the sorting method on a query.
    /// </summary>
    /// <param name="sort">The <see cref="SortCriteria"/>.</param>
    /// <param name="isPreSorted">True if pre-sorted.</param>
    private static (ItemSortBy SortName, SortOrder SortOrder)[] GetOrderBy(
        SortCriteria sort,
        bool isPreSorted)
    {
        // An explicit request wins over the natural order of a pre-sorted folder: a control point
        // that asked for its tracks by album and track number pages them expecting that order, and
        // groups what it gets, so handing back a different order breaks the listing it builds.
        if (sort.Fields.Count > 0)
        {
            return [.. sort.Fields];
        }

        return isPreSorted ? Array.Empty<(ItemSortBy, SortOrder)>() : [(ItemSortBy.SortName, sort.SortOrder)];
    }

    /// <summary>
    /// Retrieves the ServerItem id.
    /// </summary>
    /// <param name="id">The id<see cref="string"/>.</param>
    /// <returns>The <see cref="ServerItem"/>.</returns>
    private ServerItem GetItemFromObjectId(string id)
    {
        return DidlBuilder.IsIdRoot(id)
            ? new ServerItem(_libraryManager.GetUserRootFolder(), null)
            : ParseItemId(id);
    }

    /// <summary>
    /// Parses the item id into a <see cref="ServerItem"/>.
    /// </summary>
    /// <param name="id">The <see cref="string"/>.</param>
    /// <returns>The corresponding <see cref="ServerItem"/>.</returns>
    private ServerItem ParseItemId(string id)
    {
        StubType? stubType = null;
        string? idSuffix = null;

        // After using PlayTo, MediaMonkey sends a request to the server trying to get item info
        const string ParamsSrch = "Params=";
        var paramsIndex = id.IndexOf(ParamsSrch, StringComparison.OrdinalIgnoreCase);
        if (paramsIndex != -1)
        {
            id = id[(paramsIndex + ParamsSrch.Length)..];

            var parts = id.Split(';');

            // Anything else carrying the marker is not the request this handles, and indexing
            // into it blindly would fault the whole browse
            const int ItemIdPart = 23;
            if (parts.Length <= ItemIdPart)
            {
                Logger.LogError("Unexpected item Id: {Id}. Returning user root folder.", id);

                return new ServerItem(_libraryManager.GetUserRootFolder(), null);
            }

            id = parts[ItemIdPart];
        }

        var dividerIndex = id.IndexOf('_', StringComparison.Ordinal);
        if (dividerIndex != -1)
        {
            var virtualId = id.AsSpan(0, dividerIndex);
            var suffixIndex = virtualId.IndexOf('-');
            var stubName = suffixIndex == -1 ? virtualId : virtualId[..suffixIndex];

            if (Enum.TryParse<StubType>(stubName, true, out var parsedStubType))
            {
                if (suffixIndex != -1)
                {
                    idSuffix = virtualId[(suffixIndex + 1)..].ToString();
                }

                id = id[(dividerIndex + 1)..];
                stubType = parsedStubType;
            }
        }

        // A scoped object can carry the library it was browsed from after the
        // item GUID: folder_<genre-guid>_<library-guid>. Letter folders keep
        // their existing movieletter-a_<library-guid> format and therefore do
        // not enter this branch after the virtual prefix has been removed.
        Guid? ancestorId = null;
        var ancestorIndex = id.IndexOf('_', StringComparison.Ordinal);
        if (
            ancestorIndex != -1
            && Guid.TryParse(id.AsSpan(ancestorIndex + 1), out var parsedAncestorId)
        )
        {
            ancestorId = parsedAncestorId;
            id = id[..ancestorIndex];
        }

        if (Guid.TryParse(id, out var itemId))
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item is not null)
            {
                return new ServerItem(
                    item,
                    stubType,
                    GetVirtualFolderName(stubType, idSuffix),
                    idSuffix,
                    ancestorId);
            }
        }

        Logger.LogError("Error parsing item Id: {Id}. Returning user root folder.", id);

        return new ServerItem(_libraryManager.GetUserRootFolder(), null);
    }

    /// <summary>
    /// Discards elements before startIndex and elements after startIndex+limit from an array of <see cref="ServerItem"/>.
    /// </summary>
    /// <param name="serverItems">An array of <see cref="ServerItem"/>.</param>
    /// <param name="startIndex">The start index.</param>
    /// <param name="limit">The maximum number to return.</param>
    /// <returns>The corresponding trimmed array of <see cref="ServerItem"/>.</returns>
    private static ServerItem[] GetTrimmedServerItemsArray(ServerItem[] serverItems, int? startIndex, int? limit)
    {
        if (startIndex >= serverItems.Length)
        {
            return [];
        }

        if (startIndex > 0)
        {
            serverItems = serverItems[startIndex.Value..];
        }

        if (limit < serverItems.Length)
        {
            serverItems = serverItems[..limit.Value];
        }

        return serverItems;
    }
}
