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
/// Jellyfin channel exposing the full iVysílání catalog (by category) plus a user-curated
/// "Oblíbené" list. Browsing hits only metadata/GraphQL calls; actual video is streamed
/// directly from ČT's CDN via an HLS URL resolved at playback time. No media is ever
/// downloaded by this plugin.
/// </summary>
public sealed class IvysilaniChannel : IChannel, IRequiresMediaInfoCallback
{
    private const string PlaybackUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private const string PlaybackReferer = "https://www.ceskatelevize.cz/";

    private const string MovieShowType = "movie";

    // Root folder ids. Category/show folders are namespaced below so GetChannelItems can tell
    // them apart without a second round trip: "cat:{categoryId}" and "show:{idec}" come from the
    // full catalog, "fav"/"fav:{showId}" from the user's curated list.
    private const string FavoritesFolderId = "fav";
    private const string CategoryFolderPrefix = "cat:";
    private const string FavoriteShowFolderPrefix = "fav:";
    private const string CatalogShowFolderPrefix = "show:";

    // Jellyfin's server-side episode listing (GET /Shows/{id}/Episodes) only walks a series
    // through its SEASON children (Series.GetEpisodes -> allItems.OfType<Season>().SelectMany),
    // so a series folder with episodes attached directly always yields an empty list. The
    // clients (web AND the Android app share the same playbackmanager code) build the play
    // queue from that endpoint, get [] and abort with "Unable to find a valid media source"
    // BEFORE ever calling PlaybackInfo. We therefore expose a season layer:
    // show folder -> "{showFolderId}#s{seasonNumber}" -> episodes.
    private const string SeasonFolderMarker = "#s";

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
    public string Description => "Celý katalog iVysílání (ceskatelevize.cz) podle kategorií, streamovaný přímo ze serverů ČT.";

    /// <inheritdoc />
    public string DataVersion => "3";

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
            SupportsContentDownloading = false
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
            return await GetCategoryFolderAsync(categoryId, ttl, cancellationToken).ConfigureAwait(false);
        }

        if (folderId.StartsWith(FavoriteShowFolderPrefix, StringComparison.Ordinal))
        {
            var (baseFolderId, season) = SplitSeasonFolderId(folderId);
            var showId = baseFolderId[FavoriteShowFolderPrefix.Length..];
            var showUrls = ParseConfiguredShowUrls(config.ShowUrls);
            var sourceUrl = showUrls.FirstOrDefault(
                u => string.Equals(CtApiClient.ParseShowId(u), showId, StringComparison.Ordinal));
            if (sourceUrl is null)
            {
                _logger.LogWarning("Requested favorite show folder '{ShowId}' is not in the configured show list.", showId);
                return new ChannelItemResult { Items = Array.Empty<ChannelItemInfo>() };
            }

            var show = await _ctApiClient.GetShowAsync(sourceUrl, ttl, cancellationToken).ConfigureAwait(false);
            var episodes = show?.Episodes ?? Array.Empty<CtEpisode>();
            return season.HasValue
                ? BuildEpisodesResult(episodes.Where(e => SeasonKey(e) == season.Value).ToList())
                : BuildSeasonFoldersResult(episodes, baseFolderId);
        }

        if (folderId.StartsWith(CatalogShowFolderPrefix, StringComparison.Ordinal))
        {
            var (baseFolderId, season) = SplitSeasonFolderId(folderId);
            var seedIdec = baseFolderId[CatalogShowFolderPrefix.Length..];
            var episodes = await _ctApiClient.GetEpisodesAsync(seedIdec, ttl, cancellationToken).ConfigureAwait(false)
                ?? Array.Empty<CtEpisode>();
            var result = season.HasValue
                ? BuildEpisodesResult(episodes.Where(e => SeasonKey(e) == season.Value).ToList())
                : BuildSeasonFoldersResult(episodes, baseFolderId);
            _logger.LogInformation(
                "SeasonQuery folderId='{FolderId}' base='{Base}' seed='{Seed}' season={Season} totalEpisodes={Total} returned={Returned} seasonKeys=[{Keys}]",
                folderId, baseFolderId, seedIdec, season, episodes.Count, result.Items.Count,
                string.Join(",", episodes.GroupBy(SeasonKey).Select(g => g.Key + ":" + g.Count())));
            return result;
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
            _logger.LogWarning("Could not resolve a stream for content id '{ContentId}'", id);
            return Array.Empty<MediaSourceInfo>();
        }

        if (resolved.Playability is { IsPlayable: false })
        {
            _logger.LogInformation("Content '{ContentId}' is no longer playable on ČT, hiding its stream.", id);
            return Array.Empty<MediaSourceInfo>();
        }

        var mediaSource = new MediaSourceInfo
        {
            // Id ZÁMĚRNĚ nevyplňujeme. Jellyfin 12.x (ChannelManager.NormalizeMediaSources) prázdné
            // Id doplní na item.Id ve tvaru "N" — a přesně tenhle Id má i placeholder media source,
            // který server vrací v GET /Items/{id}?fields=MediaSources. Android TV klient si bere
            // MediaSourceId právě odtud a pošle ho zpátky do POST /Items/{id}/PlaybackInfo; když jsme
            // kdysi posílali vlastní MD5-Guid, hledání zdroje podle toho Id selhalo →
            // errorCode=NoCompatibleStream → PlaybackException, který jellyfin-androidtv v ne-LiveTV
            // větvi jen Timber.e loguje → černá obrazovka bez hlášky (ověřeno 2026-10-04, TV vs mobil).
            // Prázdné Id je navíc pořád Guid-kompatibilní (item.Id), takže cesta
            // /videos/{id}/master.m3u8?MediaSourceId=… pořád projde Guid.Parse i přesnou shodou.
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

        // Titulky NEPODÁVÁME, přestože je ČT nabízí — důvod (ověřeno na reálném serveru):
        // jakmile je ve zdroji titulková stopa, StreamBuilder zvolí SubtitleMethod=Encode
        // a předá klientovi URL tvaru "/videos/{id}/stream.hls". Jellyfin 12.1 u téhle
        // cesty sestaví ffmpeg příkaz s výstupem "….hls" BEZ "-f hls" → ffmpeg skončí
        // ("Unable to choose an output format … use a standard extension", exit 234) →
        // HTTP 500 → klient hlásí „Nelze najít platný zdroj médií k přehrání".
        // Bez titulkové stopy zvolí DirectPlay (TranscodeReason=0) a klient přehrává
        // přímo z CDN ČT — CDN vrací Access-Control-Allow-Origin: *, takže to funguje
        // i z prohlížeče. Do vyřešení upstream cesty tedy titulky z MediaSource vynecháváme.

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
    /// The COMPLETE show listing of a full-catalog category (e.g. all ~2600 "Dokumenty").
    /// Jellyfin's ChannelManager calls GetChannelItems exactly once per folder with no paging
    /// parameters and syncs/paginates the full result itself - a per-request lazy page here
    /// would silently truncate every category to its first page (the v1.1.0 bug). Movies are
    /// emitted directly as playable media; series as sub-folders keyed by a seed episode id.
    /// </summary>
    private async Task<ChannelItemResult> GetCategoryFolderAsync(string categoryId, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var page = await _ctApiClient.GetFullCategoryAsync(categoryId, ttl, cancellationToken).ConfigureAwait(false);
        if (page is null)
        {
            _logger.LogWarning("Could not load category '{CategoryId}'.", categoryId);
            return new ChannelItemResult { Items = Array.Empty<ChannelItemInfo>() };
        }

        var items = new List<ChannelItemInfo>();
        foreach (var show in page.Items)
        {
            if (!show.Playable)
            {
                continue;
            }

            if (string.Equals(show.ShowType, MovieShowType, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new ChannelItemInfo
                {
                    Id = show.Idec,
                    Name = show.Title,
                    Type = ChannelItemType.Media,
                    MediaType = ChannelMediaType.Video,
                    ContentType = ChannelMediaContentType.Movie,
                    Overview = show.ShortDescription,
                    ImageUrl = show.ImageUrl,
                    RunTimeTicks = show.DurationSeconds.HasValue
                        ? TimeSpan.FromSeconds(show.DurationSeconds.Value).Ticks
                        : null
                });
            }
            else
            {
                items.Add(new ChannelItemInfo
                {
                    Id = CatalogShowFolderPrefix + show.Idec,
                    Name = show.Title,
                    Type = ChannelItemType.Folder,
                    FolderType = ChannelFolderType.Series,
                    Overview = show.ShortDescription,
                    ImageUrl = show.ImageUrl
                });
            }
        }

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    /// <summary>
    /// Splits a season folder id ("{showFolderId}#s{season}") into the owning show folder id and
    /// the season number. Non-season ids pass through unchanged with a null season.
    /// </summary>
    private static (string BaseFolderId, int? Season) SplitSeasonFolderId(string folderId)
    {
        var idx = folderId.LastIndexOf(SeasonFolderMarker, StringComparison.Ordinal);
        if (idx < 0)
        {
            return (folderId, null);
        }

        var rawSeason = folderId[(idx + SeasonFolderMarker.Length)..];
        return int.TryParse(rawSeason, out var season)
            ? (folderId[..idx], season)
            : (folderId, null);
    }

    private static int SeasonKey(CtEpisode episode) => episode.SeasonNumber ?? 0;

    /// <summary>
    /// The season layer of a show folder. Required by the server: GET /Shows/{id}/Episodes only
    /// enumerates episodes through Season children of the series, and both the web UI and the
    /// Android app build their playback queue from that endpoint — without seasons it returns
    /// an empty list and clients abort playback before ever reaching PlaybackInfo.
    /// </summary>
    private static ChannelItemResult BuildSeasonFoldersResult(IEnumerable<CtEpisode> episodes, string showFolderId)
    {
        var groups = episodes
            .Where(e => e.Playable)
            .GroupBy(SeasonKey)
            .OrderBy(g => g.Key)
            .ToList();

        var items = groups.Select(g => new ChannelItemInfo
        {
            Id = showFolderId + SeasonFolderMarker + g.Key,
            // Single all-null group = a show CT gives no season info for: call it Episodes.
            Name = g.Key >= 1 ? "Season " + g.Key : (groups.Count == 1 ? "Episodes" : "Specials"),
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Season,
            // Season.GetEpisodes matches episodes on ParentIndexNumber == season IndexNumber
            // (fallback: parent linkage), so the season number must travel with the folder.
            IndexNumber = g.Key,
            Overview = g.Count() + " epizod",
            ImageUrl = g.Select(e => e.ImageUrl).FirstOrDefault(u => !string.IsNullOrEmpty(u))
        }).ToList<ChannelItemInfo>();

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    /// <summary>
    /// Maps a show's full episode list to channel media items (shared for "Oblíbené" shows and
    /// catalog shows - both resolve to a plain <see cref="CtEpisode"/> list by this point).
    /// </summary>
    private static ChannelItemResult BuildEpisodesResult(IReadOnlyList<CtEpisode> episodes)
    {
        var items = episodes
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
                ParentIndexNumber = e.SeasonNumber,
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
