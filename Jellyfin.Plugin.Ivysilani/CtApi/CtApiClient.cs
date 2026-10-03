using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ivysilani.CtApi;

/// <summary>
/// Talks to the public (unauthenticated) ceskatelevize.cz / api.ceskatelevize.cz endpoints:
/// resolves a show's full episode list and the full catalog (categories + shows) via the site's
/// own GraphQL API, and resolves an episode's/movie's playable HLS stream via the stream-data
/// API. Never downloads media, only metadata/playlist URLs.
/// </summary>
public sealed class CtApiClient : IDisposable
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private const string RefererUrl = "https://www.ceskatelevize.cz/";

    private const string StreamResolveUrlTemplate =
        "https://api.ceskatelevize.cz/video/v1/playlist-vod/v1/stream-data/media/external/{0}"
        + "?canPlayDrm=false&streamType=hls&quality=web&maxQualityCount=5&origin=ivysilani&client=iVysilaniWeb&clientVersion=0.37.6";

    private const string CatalogHomeUrl = "https://www.ceskatelevize.cz/ivysilani/";

    private const string GraphQlUrl = "https://api.ceskatelevize.cz/graphql/";

    // Matches the page's own default page size for category/episode listings (confirmed against
    // the live site's bundled JS); kept as the page size for our own internal pagination loops.
    private const int DefaultCatalogPageSize = 80;

    private const int MaxRetryAttempts = 3;

    // Reverse-engineered from the live site's production JS bundle (see REPORT.md "v1.1"/"v1.2"
    // for how); category.programmeFind gives the FULL flat show list for a category (leaf or
    // top-level), including showType ("series"/"movie"), idec (a playable content id - directly
    // playable for a movie, or a seed episode id for a series) and duration (movies only).
    private const string CategoryShowsQuery =
        "query IvysilaniCatalog($limit:PaginationAmount!$offset:Int!$categoryId:String!){"
        + "category(categoryId:$categoryId){"
        + "programmeFind(limit:$limit offset:$offset){"
        + "totalCount items{id slug title shortDescription isPlayable showType idec duration images{card(width:480)}}"
        + "}}}";

    // episodesPreviewFind(idec:...) resolves ALL episodes of the show that idec belongs to
    // (any one episode id of the show works as the seed) - this is what the site itself calls to
    // page through a show's episode list; the show's own page only embeds the first batch in its
    // server-rendered HTML, which is NOT the full list for shows with more episodes than that.
    private const string EpisodesQuery =
        "query GetEpisodes($idec:String!$limit:PaginationAmount!$offset:Int!$orderBy:EpisodeOrderByType!$onlyPlayable:Boolean){"
        + "episodesPreviewFind(idec:$idec limit:$limit offset:$offset orderBy:$orderBy onlyPlayable:$onlyPlayable){"
        + "totalCount items{id title description playable duration showId showTitle season{title}images{card(width:480)}date{datetime}}"
        + "}}";

    private static readonly TimeSpan StreamCacheTtl = TimeSpan.FromMinutes(20);

    private static readonly Regex NextDataRegex = new(
        "<script id=\"__NEXT_DATA__\" type=\"application/json\">(.*?)</script>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ShowIdRegex = new(@"/porady/(\d+)(?:-|/|$)", RegexOptions.Compiled);

    private static readonly Regex EpisodeIndexRegex = new(@"(\d+)\s*/\s*(\d+)", RegexOptions.Compiled);

    private static readonly Regex ImageWidthRegex = new("\"width\":(\\d+)", RegexOptions.Compiled);

    private static readonly Regex SeasonArabicRegex = new(@"^\s*(\d+)\.?", RegexOptions.Compiled);

    // Longest-prefix-first so "III" is tried before "II"/"I" match as a false-positive prefix.
    private static readonly (string Roman, int Value)[] RomanNumerals =
    {
        ("XX", 20), ("XIX", 19), ("XVIII", 18), ("XVII", 17), ("XVI", 16), ("XV", 15), ("XIV", 14),
        ("XIII", 13), ("XII", 12), ("XI", 11), ("X", 10), ("IX", 9), ("VIII", 8), ("VII", 7),
        ("VI", 6), ("V", 5), ("IV", 4), ("III", 3), ("II", 2), ("I", 1)
    };

    // Top category nav links look like:
    // <a ... data-focus-id="nav-Seriály" href="/ivysilani/kategorie/3976-serialy/">Seriály</a>
    private static readonly Regex CategoryNavRegex = new(
        "data-focus-id=\"nav-([^\"]+)\" href=\"/ivysilani/kategorie/(\\d+)-([a-z0-9-]+)/\"",
        RegexOptions.Compiled);

    /// <summary>
    /// Default number of items to request per page in our own internal catalog/episode
    /// pagination loops, matching the ČT website's own default.
    /// </summary>
    public static int CatalogPageSize => DefaultCatalogPageSize;

    private readonly HttpClient _httpClient;
    private readonly ILogger<CtApiClient> _logger;

    // Guards against hammering ceskatelevize.cz/api.ceskatelevize.cz when a category/show with
    // many pages is fetched (fan-out of page requests) - keeps us well below anything that would
    // trip the WAF, while still being fast enough for categories with thousands of shows.
    private readonly SemaphoreSlim _requestGate = new(4, 4);

    private readonly ConcurrentDictionary<string, CacheEntry<CtShowInfo>> _showCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheEntry<CtStreamResolveResult>> _streamCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheEntry<IReadOnlyList<CtCategory>>> _categoriesCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheEntry<CtCatalogPage>> _categoryPageCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheEntry<CtCatalogPage>> _categoryFullCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheEntry<IReadOnlyList<CtEpisode>>> _episodesCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="CtApiClient"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public CtApiClient(ILogger<CtApiClient> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _httpClient.DefaultRequestHeaders.Referrer = new Uri(RefererUrl);
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/json;q=0.9,*/*;q=0.8");
    }

    private sealed record CacheEntry<T>(T Value, DateTime ExpiresAtUtc);

    /// <summary>
    /// Extracts the numeric show id from a "/porady/{id}-..." URL. Works for both a show's
    /// landing page and a specific episode page, since both URLs start with the show id.
    /// </summary>
    /// <param name="url">A ceskatelevize.cz show or episode URL.</param>
    /// <returns>The show id, or <c>null</c> if the URL doesn't match the expected shape.</returns>
    public static string? ParseShowId(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var match = ShowIdRegex.Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Computes a deterministic <see cref="Guid"/> for a ČT content id (episode id or movie
    /// idec). Jellyfin's HLS playback pipeline (<c>StreamingHelpers.GetStreamingState</c>) calls
    /// <c>Guid.Parse</c> on the requested <c>mediaSourceId</c> whenever it can't find an exact
    /// string match among the returned <see cref="MediaBrowser.Model.Dto.MediaSourceInfo"/> - a
    /// plain ČT id (e.g. "224512120130001") is not Guid-shaped, which crashes that call with
    /// <c>FormatException</c> (HTTP 500). Using this as <c>MediaSourceInfo.Id</c> keeps it
    /// Guid-parseable while staying stable/deterministic per content id.
    /// </summary>
    /// <param name="contentId">The ČT episode id or movie idec.</param>
    /// <returns>A deterministic GUID derived from <paramref name="contentId"/>.</returns>
    public static Guid ToMediaSourceGuid(string contentId)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(contentId));
        return new Guid(hash);
    }

    /// <summary>
    /// Fetches a show's metadata and full (fully paginated) episode list.
    /// Results are cached in-process for <paramref name="ttl"/>.
    /// </summary>
    /// <param name="sourceUrl">The show or episode URL as configured by the user.</param>
    /// <param name="ttl">How long to keep the parsed result cached.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed show, or <c>null</c> if the URL/page could not be parsed.</returns>
    public async Task<CtShowInfo?> GetShowAsync(string sourceUrl, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var showId = ParseShowId(sourceUrl);
        if (showId is null)
        {
            _logger.LogWarning("Could not extract a show id from configured URL '{Url}'", sourceUrl);
            return null;
        }

        if (_showCache.TryGetValue(showId, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached.Value;
        }

        try
        {
            var html = await FetchStringAsync(NormalizeUrl(sourceUrl), cancellationToken).ConfigureAwait(false);
            var meta = ParseShowMeta(html, showId);
            if (meta is null)
            {
                return null;
            }

            IReadOnlyList<CtEpisode> episodes = Array.Empty<CtEpisode>();
            if (!string.IsNullOrEmpty(meta.Value.Idec))
            {
                episodes = await GetEpisodesAsync(meta.Value.Idec!, ttl, cancellationToken).ConfigureAwait(false)
                    ?? Array.Empty<CtEpisode>();
            }
            else
            {
                _logger.LogWarning("Show '{ShowId}' has no idec in its page data, can't list episodes.", showId);
            }

            var show = new CtShowInfo(showId, meta.Value.Title, meta.Value.ShortDescription, meta.Value.ImageUrl, episodes);
            _showCache[showId] = new CacheEntry<CtShowInfo>(show, DateTime.UtcNow.Add(ttl));
            return show;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to fetch/parse show page for '{Url}'", sourceUrl);
            return null;
        }
    }

    /// <summary>
    /// Fetches the complete, fully-paginated episode list for the show that <paramref name="seedIdec"/>
    /// belongs to (any one episode id of the show works). Results are cached in-process for
    /// <paramref name="ttl"/>. Duplicate episode titles (the same "Epizoda N/M" number reused
    /// across seasons) are disambiguated by appending the season name or broadcast date.
    /// </summary>
    /// <param name="seedIdec">Any episode id (idec) belonging to the show.</param>
    /// <param name="ttl">How long to keep the parsed result cached.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The full episode list, or <c>null</c> if it could not be fetched.</returns>
    public async Task<IReadOnlyList<CtEpisode>?> GetEpisodesAsync(string seedIdec, TimeSpan ttl, CancellationToken cancellationToken)
    {
        if (_episodesCache.TryGetValue(seedIdec, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached.Value;
        }

        var firstPage = await FetchEpisodesPageAsync(seedIdec, DefaultCatalogPageSize, 0, cancellationToken).ConfigureAwait(false);
        if (firstPage is null)
        {
            return null;
        }

        var all = new List<CtEpisode>(firstPage.Value.Items);
        var remainingOffsets = new List<int>();
        for (var offset = DefaultCatalogPageSize; offset < firstPage.Value.TotalCount; offset += DefaultCatalogPageSize)
        {
            remainingOffsets.Add(offset);
        }

        if (remainingOffsets.Count > 0)
        {
            _logger.LogInformation(
                "Show (seed idec '{SeedIdec}') has {Total} episodes, fetching {Pages} more page(s).",
                seedIdec,
                firstPage.Value.TotalCount,
                remainingOffsets.Count);

            var pages = await Task.WhenAll(
                remainingOffsets.Select(offset => FetchEpisodesPageAsync(seedIdec, DefaultCatalogPageSize, offset, cancellationToken)))
                .ConfigureAwait(false);

            foreach (var page in pages)
            {
                if (page is not null)
                {
                    all.AddRange(page.Value.Items);
                }
            }
        }

        var finalList = FinalizeEpisodes(all);
        _episodesCache[seedIdec] = new CacheEntry<IReadOnlyList<CtEpisode>>(finalList, DateTime.UtcNow.Add(ttl));
        return finalList;
    }

    private async Task<(int TotalCount, List<CtEpisode> Items)?> FetchEpisodesPageAsync(
        string seedIdec,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        var requestBody = JsonSerializer.Serialize(new
        {
            operationName = "GetEpisodes",
            query = EpisodesQuery,
            variables = new { idec = seedIdec, limit, offset, orderBy = "oldest", onlyPlayable = true }
        });

        try
        {
            var json = await PostJsonAsync(GraphQlUrl, requestBody, cancellationToken).ConfigureAwait(false);
            return ParseEpisodesResponse(json, seedIdec);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to fetch episodes page (seed idec '{SeedIdec}', offset {Offset})", seedIdec, offset);
            return null;
        }
    }

    private (int TotalCount, List<CtEpisode> Items)? ParseEpisodesResponse(string json, string seedIdec)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("errors", out var errorsEl) && errorsEl.ValueKind == JsonValueKind.Array
            && errorsEl.GetArrayLength() > 0)
        {
            _logger.LogInformation(
                "GraphQL error fetching episodes for seed idec '{SeedIdec}': {Message}",
                seedIdec,
                GetString(errorsEl[0], "message"));
            return null;
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("episodesPreviewFind", out var find) || find.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var totalCount = find.TryGetProperty("totalCount", out var totalCountEl) && totalCountEl.ValueKind == JsonValueKind.Number
            ? totalCountEl.GetInt32()
            : 0;

        var items = new List<CtEpisode>();
        if (find.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsEl.EnumerateArray())
            {
                var id = GetString(item, "id");
                var showId = GetString(item, "showId");
                var title = GetString(item, "title");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(showId) || string.IsNullOrEmpty(title))
                {
                    continue;
                }

                var playable = item.TryGetProperty("playable", out var playableEl) && playableEl.ValueKind == JsonValueKind.True;
                var description = GetString(item, "description");
                var showTitle = GetString(item, "showTitle") ?? showId;

                int? durationSeconds = item.TryGetProperty("duration", out var durationEl) && durationEl.ValueKind == JsonValueKind.Number
                    ? durationEl.GetInt32()
                    : null;

                string? imageUrl = null;
                if (item.TryGetProperty("images", out var imagesEl) && imagesEl.ValueKind == JsonValueKind.Object
                    && imagesEl.TryGetProperty("card", out var cardEl) && cardEl.ValueKind == JsonValueKind.String)
                {
                    imageUrl = cardEl.GetString();
                }

                DateTimeOffset? broadcastDate = null;
                if (item.TryGetProperty("date", out var dateEl) && dateEl.ValueKind == JsonValueKind.Object)
                {
                    broadcastDate = GetDateTimeOffset(dateEl, "datetime");
                }

                string? seasonTitle = null;
                if (item.TryGetProperty("season", out var seasonEl) && seasonEl.ValueKind == JsonValueKind.Object)
                {
                    seasonTitle = GetString(seasonEl, "title");
                }

                var (episodeIndex, episodeCount) = ParseEpisodeIndex(title);
                var seasonNumber = ParseSeasonNumber(seasonTitle);

                items.Add(new CtEpisode(
                    id,
                    showId,
                    showTitle,
                    title,
                    description,
                    playable,
                    durationSeconds,
                    imageUrl,
                    broadcastDate,
                    episodeIndex,
                    episodeCount,
                    seasonTitle,
                    seasonNumber));
            }
        }

        return (totalCount, items);
    }

    /// <summary>
    /// Disambiguates duplicate episode titles (the same "Epizoda N/M" reused across seasons —
    /// e.g. Babylon Berlín has an "Epizoda 1/8" in both its 1st and 2nd season) and applies a
    /// final, deterministic sort (season, then episode-in-season, then broadcast date).
    /// </summary>
    private static List<CtEpisode> FinalizeEpisodes(List<CtEpisode> episodes)
    {
        var disambiguated = new List<CtEpisode>(episodes.Count);
        foreach (var group in episodes.GroupBy(e => e.Title, StringComparer.Ordinal))
        {
            if (group.Count() == 1)
            {
                disambiguated.Add(group.First());
                continue;
            }

            foreach (var episode in group)
            {
                var suffix = !string.IsNullOrEmpty(episode.SeasonTitle)
                    ? $" ({episode.SeasonTitle})"
                    : episode.BroadcastDate.HasValue
                        ? $" ({episode.BroadcastDate.Value:yyyy-MM-dd})"
                        : $" ({episode.Id})";
                disambiguated.Add(episode with { Title = episode.Title + suffix });
            }
        }

        return disambiguated
            .OrderBy(e => e.SeasonNumber ?? int.MaxValue)
            .ThenBy(e => e.EpisodeIndex ?? int.MaxValue)
            .ThenBy(e => e.BroadcastDate ?? DateTimeOffset.MaxValue)
            .ThenBy(e => e.Title, StringComparer.Ordinal)
            .ToList();
    }

    private static int? ParseSeasonNumber(string? seasonTitle)
    {
        if (string.IsNullOrWhiteSpace(seasonTitle))
        {
            return null;
        }

        var trimmed = seasonTitle.Trim();
        var arabicMatch = SeasonArabicRegex.Match(trimmed);
        if (arabicMatch.Success)
        {
            return int.Parse(arabicMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        foreach (var (roman, value) in RomanNumerals)
        {
            if (trimmed.StartsWith(roman + ".", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(roman + " ", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves an episode's or movie's current playable HLS master playlist URL and
    /// playability window. Results are cached briefly in-process since the returned URL
    /// contains a short-lived token.
    /// </summary>
    /// <param name="contentId">The ČT episode id or movie idec.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolve result, or <c>null</c> if there's no playable stream right now.</returns>
    public async Task<CtStreamResolveResult?> ResolveStreamAsync(string contentId, CancellationToken cancellationToken)
    {
        if (_streamCache.TryGetValue(contentId, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached.Value;
        }

        var url = string.Format(CultureInfo.InvariantCulture, StreamResolveUrlTemplate, Uri.EscapeDataString(contentId));
        try
        {
            var json = await FetchStringAsync(url, cancellationToken).ConfigureAwait(false);
            var result = ParseStreamResolve(json, contentId);
            if (result is not null)
            {
                _streamCache[contentId] = new CacheEntry<CtStreamResolveResult>(result, DateTime.UtcNow.Add(StreamCacheTtl));
            }

            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to resolve stream for content id '{ContentId}'", contentId);
            return null;
        }
    }

    /// <summary>
    /// Fetches the top-level iVysílání catalog categories (e.g. "Seriály", "Filmy", "Dokumenty")
    /// as linked from the main catalog navigation. Results are cached in-process for <paramref name="ttl"/>.
    /// </summary>
    /// <param name="ttl">How long to keep the parsed result cached.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The top-level categories, or an empty list if they could not be fetched/parsed.</returns>
    public async Task<IReadOnlyList<CtCategory>> GetTopLevelCategoriesAsync(TimeSpan ttl, CancellationToken cancellationToken)
    {
        const string cacheKey = "root";
        if (_categoriesCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached.Value;
        }

        try
        {
            var html = await FetchStringAsync(CatalogHomeUrl, cancellationToken).ConfigureAwait(false);
            var seen = new Dictionary<string, CtCategory>(StringComparer.Ordinal);
            foreach (Match match in CategoryNavRegex.Matches(html))
            {
                var title = match.Groups[1].Value;
                var categoryId = match.Groups[2].Value;
                var slug = match.Groups[2].Value + "-" + match.Groups[3].Value;
                seen[categoryId] = new CtCategory(categoryId, slug, title);
            }

            var categories = seen.Values.ToList();
            if (categories.Count > 0)
            {
                _categoriesCache[cacheKey] = new CacheEntry<IReadOnlyList<CtCategory>>(categories, DateTime.UtcNow.Add(ttl));
            }
            else
            {
                _logger.LogWarning("No catalog categories found while parsing '{Url}' - page layout may have changed.", CatalogHomeUrl);
            }

            return categories;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch catalog categories from '{Url}'", CatalogHomeUrl);
            return Array.Empty<CtCategory>();
        }
    }

    /// <summary>
    /// Fetches the COMPLETE show listing for a catalog category (leaf or top-level), paginating
    /// through all pages internally. Jellyfin's <c>ChannelManager</c> calls a channel's
    /// <c>GetChannelItems</c> exactly once per folder with no paging parameters and expects the
    /// full result in one call (it does its own paging afterwards against its library DB), so a
    /// per-request lazy page would silently truncate large categories (e.g. ~2600 "Dokumenty").
    /// Results are cached in-process, as one assembled list, for <paramref name="ttl"/>.
    /// </summary>
    /// <param name="categoryId">The numeric category id (e.g. "3976" for "Seriály").</param>
    /// <param name="ttl">How long to keep the assembled result cached.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The full category, or <c>null</c> if the category doesn't exist or the first page failed.</returns>
    public async Task<CtCatalogPage?> GetFullCategoryAsync(string categoryId, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var cacheKey = "full:" + categoryId;
        if (_categoryFullCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached.Value;
        }

        var firstPage = await GetCategoryShowsAsync(categoryId, DefaultCatalogPageSize, 0, ttl, cancellationToken).ConfigureAwait(false);
        if (firstPage is null)
        {
            return null;
        }

        var allItems = new List<CtCatalogShow>(firstPage.Items);
        var remainingOffsets = new List<int>();
        for (var offset = DefaultCatalogPageSize; offset < firstPage.TotalCount; offset += DefaultCatalogPageSize)
        {
            remainingOffsets.Add(offset);
        }

        if (remainingOffsets.Count > 0)
        {
            _logger.LogInformation(
                "Category '{CategoryId}' has {Total} shows, fetching {Pages} more page(s).",
                categoryId,
                firstPage.TotalCount,
                remainingOffsets.Count);

            var pages = await Task.WhenAll(
                remainingOffsets.Select(offset => GetCategoryShowsAsync(categoryId, DefaultCatalogPageSize, offset, ttl, cancellationToken)))
                .ConfigureAwait(false);

            foreach (var page in pages)
            {
                if (page is not null)
                {
                    allItems.AddRange(page.Items);
                }
            }
        }

        var result = new CtCatalogPage(firstPage.TotalCount, allItems);
        _categoryFullCache[cacheKey] = new CacheEntry<CtCatalogPage>(result, DateTime.UtcNow.Add(ttl));
        return result;
    }

    /// <summary>
    /// Fetches one page of a catalog category's show listing via the public GraphQL API
    /// (<c>category.programmeFind</c>). Low-level building block used by
    /// <see cref="GetFullCategoryAsync"/>; results are cached in-process for <paramref name="ttl"/>.
    /// </summary>
    /// <param name="categoryId">The numeric category id (e.g. "3976" for "Seriály").</param>
    /// <param name="limit">Page size, clamped to <see cref="CatalogPageSize"/>.</param>
    /// <param name="offset">Zero-based offset into the category's show list.</param>
    /// <param name="ttl">How long to keep the parsed result cached.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page, or <c>null</c> if the category doesn't exist or the call failed.</returns>
    public async Task<CtCatalogPage?> GetCategoryShowsAsync(
        string categoryId,
        int limit,
        int offset,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        var clampedLimit = Math.Clamp(limit <= 0 ? DefaultCatalogPageSize : limit, 1, DefaultCatalogPageSize);
        var cacheKey = string.Create(CultureInfo.InvariantCulture, $"{categoryId}:{offset}:{clampedLimit}");
        if (_categoryPageCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached.Value;
        }

        var requestBody = JsonSerializer.Serialize(new
        {
            operationName = "IvysilaniCatalog",
            query = CategoryShowsQuery,
            variables = new { limit = clampedLimit, offset, categoryId }
        });

        try
        {
            var json = await PostJsonAsync(GraphQlUrl, requestBody, cancellationToken).ConfigureAwait(false);
            var page = ParseCategoryShowsResponse(json, categoryId);
            if (page is not null)
            {
                _categoryPageCache[cacheKey] = new CacheEntry<CtCatalogPage>(page, DateTime.UtcNow.Add(ttl));
            }

            return page;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to fetch catalog category '{CategoryId}' (offset {Offset})", categoryId, offset);
            return null;
        }
    }

    private CtCatalogPage? ParseCategoryShowsResponse(string json, string categoryId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("errors", out var errorsEl) && errorsEl.ValueKind == JsonValueKind.Array
            && errorsEl.GetArrayLength() > 0)
        {
            var message = GetString(errorsEl[0], "message");
            _logger.LogInformation("GraphQL error for category '{CategoryId}': {Message}", categoryId, message);
            return null;
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("category", out var category) || category.ValueKind != JsonValueKind.Object
            || !category.TryGetProperty("programmeFind", out var programmeFind))
        {
            return null;
        }

        var totalCount = programmeFind.TryGetProperty("totalCount", out var totalCountEl)
            && totalCountEl.ValueKind == JsonValueKind.Number
                ? totalCountEl.GetInt32()
                : 0;

        var items = new List<CtCatalogShow>();
        if (programmeFind.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsEl.EnumerateArray())
            {
                var id = GetString(item, "id");
                var slug = GetString(item, "slug");
                var title = GetString(item, "title");
                var idec = GetString(item, "idec");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(title)
                    || string.IsNullOrEmpty(idec))
                {
                    continue;
                }

                var playable = item.TryGetProperty("isPlayable", out var playableEl)
                    && playableEl.ValueKind == JsonValueKind.True;
                var showType = GetString(item, "showType") ?? "series";

                int? durationSeconds = item.TryGetProperty("duration", out var durationEl) && durationEl.ValueKind == JsonValueKind.Number
                    ? durationEl.GetInt32()
                    : null;

                string? imageUrl = null;
                if (item.TryGetProperty("images", out var imagesEl) && imagesEl.ValueKind == JsonValueKind.Object
                    && imagesEl.TryGetProperty("card", out var cardEl) && cardEl.ValueKind == JsonValueKind.String)
                {
                    imageUrl = cardEl.GetString();
                }

                items.Add(new CtCatalogShow(
                    id,
                    slug,
                    title,
                    GetString(item, "shortDescription"),
                    playable,
                    imageUrl,
                    showType,
                    idec,
                    durationSeconds));
            }
        }

        return new CtCatalogPage(totalCount, items);
    }

    private static string NormalizeUrl(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + trimmed;
        }

        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "https://" + trimmed;
        }

        return trimmed;
    }

    private Task<string> FetchStringAsync(string url, CancellationToken cancellationToken)
    {
        return SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
    }

    private Task<string> PostJsonAsync(string url, string jsonBody, CancellationToken cancellationToken)
    {
        return SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            },
            cancellationToken);
    }

    /// <summary>
    /// Sends a request, retrying transient failures (429/403/5xx, timeouts) with backoff, and
    /// never running more than a handful of these requests against ceskatelevize.cz concurrently.
    /// </summary>
    private async Task<string> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; attempt <= MaxRetryAttempts; attempt++)
            {
                using var request = requestFactory();
                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (HttpRequestException) when (attempt < MaxRetryAttempts)
                {
                    await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                using (response)
                {
                    if (response.IsSuccessStatusCode)
                    {
                        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    }

                    var isTransient = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                        || (int)response.StatusCode >= 500;
                    if (!isTransient || attempt == MaxRetryAttempts)
                    {
                        response.EnsureSuccessStatusCode();
                    }

                    _logger.LogWarning(
                        "Transient HTTP {StatusCode} from ČT (attempt {Attempt}/{MaxAttempts}), retrying.",
                        (int)response.StatusCode,
                        attempt,
                        MaxRetryAttempts);
                }

                await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
            }

            // Unreachable: the loop above always either returns or throws on its last attempt.
            throw new HttpRequestException("Exhausted retries without a successful response.");
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(3, attempt - 1));
        return Task.Delay(delay, cancellationToken);
    }

    /// <summary>
    /// Extracts a show's title/description/image and its "idec" (a playable content id of the
    /// show, used to seed <see cref="GetEpisodesAsync"/>) from its page's embedded __NEXT_DATA__.
    /// </summary>
    private (string Title, string? ShortDescription, string? ImageUrl, string? Idec)? ParseShowMeta(string html, string showId)
    {
        var match = NextDataRegex.Match(html);
        if (!match.Success)
        {
            _logger.LogWarning("__NEXT_DATA__ block not found while parsing show '{ShowId}'", showId);
            return null;
        }

        using var doc = JsonDocument.Parse(match.Groups[1].Value);
        var root = doc.RootElement;
        if (!root.TryGetProperty("props", out var props) || !props.TryGetProperty("apolloState", out var apollo))
        {
            _logger.LogWarning("apolloState not found while parsing show '{ShowId}'", showId);
            return null;
        }

        if (!apollo.TryGetProperty("Show:" + showId, out var showEl))
        {
            _logger.LogWarning("Show:{ShowId} entity not found while parsing show page", showId);
            return null;
        }

        var title = GetString(showEl, "title") ?? showId;
        var shortDescription = GetString(showEl, "shortDescription");
        var idec = GetString(showEl, "idec");

        string? imageUrl = null;
        if (showEl.TryGetProperty("images", out var showImages))
        {
            imageUrl = ExtractBestImageUrl(showImages, "card(", "poster(");
        }

        return (title, shortDescription, imageUrl, idec);
    }

    private static (int? Index, int? Count) ParseEpisodeIndex(string title)
    {
        var fraction = EpisodeIndexRegex.Match(title);
        if (!fraction.Success)
        {
            return (null, null);
        }

        return (
            int.Parse(fraction.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(fraction.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? ExtractBestImageUrl(JsonElement imagesElement, params string[] keyPrefixes)
    {
        if (imagesElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? best = null;
        var bestWidth = -1;
        foreach (var prop in imagesElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var isCandidate = false;
            foreach (var prefix in keyPrefixes)
            {
                if (prop.Name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    isCandidate = true;
                    break;
                }
            }

            if (!isCandidate)
            {
                continue;
            }

            var value = prop.Value.GetString();
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var widthMatch = ImageWidthRegex.Match(prop.Name);
            var width = widthMatch.Success ? int.Parse(widthMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            if (width > bestWidth)
            {
                bestWidth = width;
                best = value;
            }
        }

        return best;
    }

    private CtStreamResolveResult? ParseStreamResolve(string json, string contentId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("streams", out var streamsEl) || streamsEl.ValueKind != JsonValueKind.Array
            || streamsEl.GetArrayLength() == 0)
        {
            _logger.LogInformation("No streams returned for content id '{ContentId}'", contentId);
            return null;
        }

        var stream0 = streamsEl[0];
        var hlsUrl = GetString(stream0, "url");
        if (string.IsNullOrEmpty(hlsUrl))
        {
            return null;
        }

        double? durationSeconds = null;
        if (stream0.TryGetProperty("duration", out var streamDuration) && streamDuration.ValueKind == JsonValueKind.Number)
        {
            durationSeconds = streamDuration.GetDouble();
        }
        else if (root.TryGetProperty("duration", out var rootDuration) && rootDuration.ValueKind == JsonValueKind.Number)
        {
            durationSeconds = rootDuration.GetDouble();
        }

        CtPlayability? playability = null;
        if (root.TryGetProperty("playability", out var playabilityEl) && playabilityEl.ValueKind == JsonValueKind.Object)
        {
            var isPlayable = playabilityEl.TryGetProperty("isPlayable", out var isPlayableEl)
                && isPlayableEl.ValueKind == JsonValueKind.True;

            DateTimeOffset? from = null;
            DateTimeOffset? to = null;
            if (playabilityEl.TryGetProperty("playableInterval", out var intervalEl) && intervalEl.ValueKind == JsonValueKind.Object)
            {
                from = GetDateTimeOffset(intervalEl, "playableFrom");
                to = GetDateTimeOffset(intervalEl, "playableTo");
            }

            playability = new CtPlayability(isPlayable, from, to);
        }

        var subtitles = new List<CtSubtitle>();
        if (stream0.TryGetProperty("subtitles", out var subtitlesEl) && subtitlesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var sub in subtitlesEl.EnumerateArray())
            {
                var language = GetString(sub, "language") ?? "ces";
                if (!sub.TryGetProperty("files", out var filesEl) || filesEl.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var file in filesEl.EnumerateArray())
                {
                    if (string.Equals(GetString(file, "format"), "vtt", StringComparison.Ordinal))
                    {
                        var vttUrl = GetString(file, "url");
                        if (!string.IsNullOrEmpty(vttUrl))
                        {
                            subtitles.Add(new CtSubtitle(language, vttUrl));
                        }

                        break;
                    }
                }
            }
        }

        return new CtStreamResolveResult(
            contentId,
            GetString(root, "title"),
            GetString(root, "episodeTitle"),
            GetString(root, "showTitle"),
            durationSeconds,
            hlsUrl,
            GetString(root, "previewImageUrl"),
            playability,
            subtitles);
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement element, string propertyName)
    {
        var raw = GetString(element, propertyName);
        return raw is not null && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
