using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ivysilani.CtApi;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ivysilani.Channel;

/// <summary>
/// Jellyfin channel exposing a user-curated set of ceskatelevize.cz (iVysílání) shows.
/// Browsing hits only metadata/HTML pages; actual video is streamed directly from ČT's
/// CDN via an HLS URL resolved at playback time. No media is ever downloaded by this plugin.
/// </summary>
public sealed class IvysilaniChannel : IChannel, IRequiresMediaInfoCallback
{
    private const string PlaybackUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private const string PlaybackReferer = "https://www.ceskatelevize.cz/";

    // Root folder ids. Category/show folders are namespaced below so GetChannelItems can tell
    // them apart without a second round trip: "cat:{categoryId}" and "show:{slug}" come from the
    // full catalog (lazily browsed per-category), "fav"/"fav:{showId}" from the user's curated list.
    private const string FavoritesFolderId = "fav";
    private const string CategoryFolderPrefix = "cat:";
    private const string FavoriteShowFolderPrefix = "fav:";
    private const string CatalogShowFolderPrefix = "show:";

    private readonly CtApiClient _ctApiClient;
    private readonly ILogger<IvysilaniChannel> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IvysilaniChannel"/> class.
    /// </summary>
    /// <param name="ctApiClient">Client used to talk to ceskatelevize.cz/api.ceskatelevize.cz.</param>
    /// <param name="logger">Logger.</param>
    public IvysilaniChannel(CtApiClient ctApiClient, ILogger<IvysilaniChannel> logger)
    {
        _ctApiClient = ctApiClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "iVysílání";

    /// <inheritdoc />
    public string Description => "Vybrané pořady z iVysílání (ceskatelevize.cz), streamované přímo ze serverů ČT.";

    /// <inheritdoc />
    public string DataVersion => "1";

    /// <inheritdoc />
    public string HomePageUrl => "https://www.ceskatelevize.cz/ivysilani/";

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
            ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Episode, ChannelMediaContentType.Movie },
            DefaultSortFields = new List<ChannelItemSortField> { ChannelItemSortField.Name, ChannelItemSortField.PremiereDate },
            SupportsSortOrderToggle = false,
            SupportsContentDownloading = false,
            MaxPageSize = CtApiClient.CatalogPageSize
        };
    }

    /// <inheritdoc />
    public bool IsEnabledFor(string userId) => true;

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => Enumerable.Empty<ImageType>();

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        return Task.FromResult(new DynamicImageResponse { HasImage = false });
    }

    /// <inheritdoc />
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var ttlHours = Math.Clamp(config.CacheTtlHours <= 0 ? 3 : config.CacheTtlHours, 1, 24);
        var ttl = TimeSpan.FromHours(ttlHours);
        var folderId = query.FolderId;

        if (string.IsNullOrEmpty(folderId))
        {
            return await GetRootAsync(ttl, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(folderId, FavoritesFolderId, StringComparison.Ordinal))
        {
            var showUrls = ParseConfiguredShowUrls(config.ShowUrls);
            return await GetFavoriteShowsAsync(showUrls, ttl, cancellationToken).ConfigureAwait(false);
        }

        if (folderId.StartsWith(CategoryFolderPrefix, StringComparison.Ordinal))
        {
            var categoryId = folderId[CategoryFolderPrefix.Length..];
            return await GetCategoryShowsPageAsync(categoryId, query.StartIndex, query.Limit, ttl, cancellationToken)
                .ConfigureAwait(false);
        }

        if (folderId.StartsWith(FavoriteShowFolderPrefix, StringComparison.Ordinal))
        {
            var showId = folderId[FavoriteShowFolderPrefix.Length..];
            var showUrls = ParseConfiguredShowUrls(config.ShowUrls);
            var sourceUrl = showUrls.FirstOrDefault(
                u => string.Equals(CtApiClient.ParseShowId(u), showId, StringComparison.Ordinal));
            if (sourceUrl is null)
            {
                _logger.LogWarning("Requested favorite show folder '{ShowId}' is not in the configured show list.", showId);
                return new ChannelItemResult { Items = Array.Empty<ChannelItemInfo>() };
            }

            return await GetShowEpisodesAsync(sourceUrl, ttl, cancellationToken).ConfigureAwait(false);
        }

        if (folderId.StartsWith(CatalogShowFolderPrefix, StringComparison.Ordinal))
        {
            var slug = folderId[CatalogShowFolderPrefix.Length..];
            var sourceUrl = $"https://www.ceskatelevize.cz/porady/{slug}/";
            return await GetShowEpisodesAsync(sourceUrl, ttl, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning("Unrecognized channel folder id '{FolderId}'.", folderId);
        return new ChannelItemResult { Items = Array.Empty<ChannelItemInfo>() };
    }

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        var resolved = await _ctApiClient.ResolveStreamAsync(id, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            _logger.LogWarning("Could not resolve a stream for episode '{EpisodeId}'", id);
            return Array.Empty<MediaSourceInfo>();
        }

        if (resolved.Playability is { IsPlayable: false })
        {
            _logger.LogInformation("Episode '{EpisodeId}' is no longer playable on ČT, hiding its stream.", id);
            return Array.Empty<MediaSourceInfo>();
        }

        var mediaSource = new MediaSourceInfo
        {
            Id = id,
            Path = resolved.HlsUrl,
            Protocol = MediaProtocol.Http,
            Container = "hls",
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            IsInfiniteStream = false,
            RequiresOpening = false,
            RequiresClosing = false,
            RequiredHttpHeaders = new Dictionary<string, string>
            {
                ["User-Agent"] = PlaybackUserAgent,
                ["Referer"] = PlaybackReferer
            }
        };

        if (resolved.DurationSeconds.HasValue)
        {
            mediaSource.RunTimeTicks = (long)(resolved.DurationSeconds.Value * TimeSpan.TicksPerSecond);
        }

        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264", IsDefault = true },
            new() { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", Language = "cze", IsDefault = true }
        };

        var subtitleIndex = 2;
        foreach (var subtitle in resolved.Subtitles)
        {
            streams.Add(new MediaStream
            {
                Type = MediaStreamType.Subtitle,
                Index = subtitleIndex++,
                Codec = "vtt",
                Language = subtitle.Language,
                IsExternal = true,
                DeliveryMethod = SubtitleDeliveryMethod.External,
                DeliveryUrl = subtitle.Url
            });
        }

        mediaSource.MediaStreams = streams;

        return new[] { mediaSource };
    }

    private static List<string> ParseConfiguredShowUrls(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<string>();
        }

        return raw
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();
    }

    /// <summary>
    /// Root of the channel: the user's curated "Oblíbené" folder plus one folder per full-catalog
    /// category (Seriály, Filmy, ...). Cheap - only fetches the category nav, never show lists.
    /// </summary>
    private async Task<ChannelItemResult> GetRootAsync(TimeSpan ttl, CancellationToken cancellationToken)
    {
        var items = new List<ChannelItemInfo>
        {
            new()
            {
                Id = FavoritesFolderId,
                Name = "Oblíbené",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                Overview = "Vlastní seznam pořadů nastavený v konfiguraci pluginu."
            }
        };

        var categories = await _ctApiClient.GetTopLevelCategoriesAsync(ttl, cancellationToken).ConfigureAwait(false);
        if (categories.Count == 0)
        {
            _logger.LogWarning("No catalog categories available - only the 'Oblíbené' folder will be shown.");
        }

        foreach (var category in categories)
        {
            items.Add(new ChannelItemInfo
            {
                Id = CategoryFolderPrefix + category.CategoryId,
                Name = category.Title,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container
            });
        }

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    /// <summary>
    /// The user's hand-curated show list (plugin configuration), same behavior as plugin v1.0.
    /// </summary>
    private async Task<ChannelItemResult> GetFavoriteShowsAsync(List<string> showUrls, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var items = new List<ChannelItemInfo>();
        var seenShowIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var url in showUrls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var showId = CtApiClient.ParseShowId(url);
            if (showId is null || !seenShowIds.Add(showId))
            {
                continue;
            }

            var show = await _ctApiClient.GetShowAsync(url, ttl, cancellationToken).ConfigureAwait(false);
            if (show is null || show.Episodes.Count == 0)
            {
                _logger.LogWarning("Show '{ShowId}' ({Url}) has no episodes or could not be loaded, skipping.", showId, url);
                continue;
            }

            if (!show.Episodes.Any(e => e.Playable))
            {
                _logger.LogInformation("Show '{ShowId}' has no currently playable episodes, skipping.", showId);
                continue;
            }

            items.Add(new ChannelItemInfo
            {
                Id = FavoriteShowFolderPrefix + show.ShowId,
                Name = show.Title,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Series,
                Overview = show.ShortDescription,
                ImageUrl = show.ImageUrl
            });
        }

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    /// <summary>
    /// One page of a full-catalog category's shows (e.g. all 2600+ "Dokumenty"), fetched lazily
    /// and only for the page Jellyfin actually asked for via <see cref="InternalChannelItemQuery"/>.
    /// </summary>
    private async Task<ChannelItemResult> GetCategoryShowsPageAsync(
        string categoryId,
        int? startIndex,
        int? limit,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        var offset = Math.Max(startIndex ?? 0, 0);
        var pageSize = limit is > 0 ? limit.Value : CtApiClient.CatalogPageSize;

        var page = await _ctApiClient.GetCategoryShowsAsync(categoryId, pageSize, offset, ttl, cancellationToken)
            .ConfigureAwait(false);
        if (page is null)
        {
            _logger.LogWarning("Could not load category '{CategoryId}' (offset {Offset}).", categoryId, offset);
            return new ChannelItemResult { Items = Array.Empty<ChannelItemInfo>() };
        }

        var items = page.Items
            .Where(s => s.Playable)
            .Select(s => new ChannelItemInfo
            {
                Id = CatalogShowFolderPrefix + s.Slug,
                Name = s.Title,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Series,
                Overview = s.ShortDescription,
                ImageUrl = s.ImageUrl
            })
            .ToList<ChannelItemInfo>();

        return new ChannelItemResult { Items = items, TotalRecordCount = page.TotalCount };
    }

    /// <summary>
    /// Episode listing for one show, regardless of whether it was reached via "Oblíbené" or via
    /// a catalog category - both resolve to a plain ceskatelevize.cz show URL by this point.
    /// </summary>
    private async Task<ChannelItemResult> GetShowEpisodesAsync(string sourceUrl, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var show = await _ctApiClient.GetShowAsync(sourceUrl, ttl, cancellationToken).ConfigureAwait(false);
        if (show is null)
        {
            return new ChannelItemResult { Items = Array.Empty<ChannelItemInfo>() };
        }

        var items = show.Episodes
            .Where(e => e.Playable)
            .Select(e => new ChannelItemInfo
            {
                Id = e.Id,
                Name = e.Title,
                SeriesName = e.ShowTitle,
                Overview = string.IsNullOrEmpty(e.Description) ? e.ShowTitle : e.Description,
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Episode,
                ImageUrl = e.ImageUrl,
                IndexNumber = e.EpisodeIndex,
                PremiereDate = e.BroadcastDate?.UtcDateTime,
                DateCreated = e.BroadcastDate?.UtcDateTime,
                RunTimeTicks = e.DurationSeconds.HasValue
                    ? TimeSpan.FromSeconds(e.DurationSeconds.Value).Ticks
                    : null
            })
            .ToList<ChannelItemInfo>();

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }
}
