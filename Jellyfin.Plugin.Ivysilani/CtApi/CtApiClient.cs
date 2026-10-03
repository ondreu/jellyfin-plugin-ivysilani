using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ivysilani.CtApi;

/// <summary>
/// Talks to the public (unauthenticated) ceskatelevize.cz / api.ceskatelevize.cz endpoints:
/// parses a show's "/porady/{id}-..." page for its episode list, and resolves an episode's
/// playable HLS stream via the stream-data API. Never downloads media, only metadata/playlist URLs.
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

    // Matches the page's own default page size for category listings (confirmed against the
    // live site's bundled JS); kept as the default/clamp for our own category paging too.
    private const int DefaultCatalogPageSize = 80;

    private const int MaxRetryAttempts = 3;

    // The ČT catalog GraphQL query does not expose an "all shows" or "list every category" field;
    // this is the equivalent of the query the website itself sends for a category's flat show
    // listing (category.programmeFind), reverse-engineered from the site's own JS bundle - see
    // REPORT.md "v1.1" for how it was found. Trimmed to just the fields this plugin needs.
    private const string CategoryShowsQuery =
        "query IvysilaniCatalog($limit:PaginationAmount!$offset:Int!$categoryId:String!){"
        + "category(categoryId:$categoryId){"
        + "programmeFind(limit:$limit offset:$offset){"
        + "totalCount items{id slug title shortDescription isPlayable images{card(width:480)}}"
        + "}}}";

    private static readonly TimeSpan StreamCacheTtl = TimeSpan.FromMinutes(20);

    private static readonly Regex NextDataRegex = new(
        "<script id=\"__NEXT_DATA__\" type=\"application/json\">(.*?)</script>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ShowIdRegex = new(@"/porady/(\d+)(?:-|/|$)", RegexOptions.Compiled);

    private static readonly Regex EpisodeIndexRegex = new(@"(\d+)\s*/\s*(\d+)", RegexOptions.Compiled);

    private static readonly Regex SingleNumberRegex = new(@"\d+", RegexOptions.Compiled);

    private static readonly Regex ImageWidthRegex = new("\"width\":(\\d+)", RegexOptions.Compiled);

    // Top category nav links look like:
    // <a ... data-focus-id="nav-Seriály" href="/ivysilani/kategorie/3976-serialy/">Seriály</a>
    private static readonly Regex CategoryNavRegex = new(
        "data-focus-id=\"nav-([^\"]+)\" href=\"/ivysilani/kategorie/(\\d+)-([a-z0-9-]+)/\"",
        RegexOptions.Compiled);

    /// <summary>
    /// Default number of shows to return per catalog category page, matching the ČT website's
    /// own default. Exposed so the channel can advertise it as its preferred page size.
    /// </summary>
    public static int CatalogPageSize => DefaultCatalogPageSize;

    private readonly HttpClient _httpClient;
    private readonly ILogger<CtApiClient> _logger;

    // Guards against hammering ceskatelevize.cz/api.ceskatelevize.cz if several channel folders
    // are browsed/refreshed at once - keeps us well below anything that would trip the WAF.
    private readonly SemaphoreSlim _requestGate = new(2, 2);

    private readonly ConcurrentDictionary<string, CacheEntry<CtShowInfo>> _showCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheEntry<CtStreamResolveResult>> _streamCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheEntry<IReadOnlyList<CtCategory>>> _categoriesCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheEntry<CtCatalogPage>> _categoryPageCache = new(StringComparer.Ordinal);

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
    /// Fetches and parses a show's page, returning its title and currently-listed episodes.
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
            var show = ParseShowPage(html, showId);
            if (show is not null)
            {
                _showCache[showId] = new CacheEntry<CtShowInfo>(show, DateTime.UtcNow.Add(ttl));
            }

            return show;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to fetch/parse show page for '{Url}'", sourceUrl);
            return null;
        }
    }

    /// <summary>
    /// Resolves an episode's current playable HLS master playlist URL and playability window.
    /// Results are cached briefly in-process since the returned URL contains a short-lived token.
    /// </summary>
    /// <param name="episodeId">The ČT episode id (idec).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolve result, or <c>null</c> if the episode has no playable stream right now.</returns>
    public async Task<CtStreamResolveResult?> ResolveStreamAsync(string episodeId, CancellationToken cancellationToken)
    {
        if (_streamCache.TryGetValue(episodeId, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached.Value;
        }

        var url = string.Format(CultureInfo.InvariantCulture, StreamResolveUrlTemplate, Uri.EscapeDataString(episodeId));
        try
        {
            var json = await FetchStringAsync(url, cancellationToken).ConfigureAwait(false);
            var result = ParseStreamResolve(json, episodeId);
            if (result is not null)
            {
                _streamCache[episodeId] = new CacheEntry<CtStreamResolveResult>(result, DateTime.UtcNow.Add(StreamCacheTtl));
            }

            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to resolve stream for episode '{EpisodeId}'", episodeId);
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
    /// Fetches one page of a catalog category's full show listing via the public GraphQL API
    /// (<c>category.programmeFind</c>). Results are cached in-process for <paramref name="ttl"/>.
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
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(title))
                {
                    continue;
                }

                var playable = item.TryGetProperty("isPlayable", out var playableEl)
                    && playableEl.ValueKind == JsonValueKind.True;

                string? imageUrl = null;
                if (item.TryGetProperty("images", out var imagesEl) && imagesEl.ValueKind == JsonValueKind.Object
                    && imagesEl.TryGetProperty("card", out var cardEl) && cardEl.ValueKind == JsonValueKind.String)
                {
                    imageUrl = cardEl.GetString();
                }

                items.Add(new CtCatalogShow(id, slug, title, GetString(item, "shortDescription"), playable, imageUrl));
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
    /// never running more than two of these requests against ceskatelevize.cz concurrently.
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

    private CtShowInfo? ParseShowPage(string html, string showId)
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

        string? showTitle = null;
        string? shortDescription = null;
        string? showImageUrl = null;

        if (apollo.TryGetProperty("Show:" + showId, out var showEl))
        {
            showTitle = GetString(showEl, "title");
            shortDescription = GetString(showEl, "shortDescription");
            if (showEl.TryGetProperty("images", out var showImages))
            {
                showImageUrl = ExtractBestImageUrl(showImages, "card(", "poster(");
            }
        }

        var episodes = new List<CtEpisode>();
        foreach (var prop in apollo.EnumerateObject())
        {
            if (!prop.Name.StartsWith("EpisodePreview:", StringComparison.Ordinal))
            {
                continue;
            }

            var episode = ParseEpisodePreview(prop.Value, showId, showTitle);
            if (episode is not null)
            {
                episodes.Add(episode);
            }
        }

        var sortedEpisodes = episodes
            .OrderBy(e => e.EpisodeIndex ?? int.MaxValue)
            .ThenBy(e => e.BroadcastDate ?? DateTimeOffset.MaxValue)
            .ThenBy(e => e.Title, StringComparer.Ordinal)
            .ToList();

        if (showTitle is null && sortedEpisodes.Count > 0)
        {
            showTitle = sortedEpisodes[0].ShowTitle;
        }

        return new CtShowInfo(showId, showTitle ?? showId, shortDescription, showImageUrl, sortedEpisodes);
    }

    private CtEpisode? ParseEpisodePreview(JsonElement episodeElement, string showId, string? fallbackShowTitle)
    {
        var episodeShowId = GetString(episodeElement, "showId");
        if (!string.Equals(episodeShowId, showId, StringComparison.Ordinal))
        {
            return null;
        }

        var id = GetString(episodeElement, "id");
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var title = GetString(episodeElement, "title") ?? id;
        var description = GetString(episodeElement, "description");
        var playable = episodeElement.TryGetProperty("playable", out var playableEl)
            && playableEl.ValueKind == JsonValueKind.True;
        var showTitle = GetString(episodeElement, "showTitle") ?? fallbackShowTitle ?? showId;

        int? durationSeconds = null;
        if (episodeElement.TryGetProperty("duration", out var durationEl) && durationEl.ValueKind == JsonValueKind.Number)
        {
            durationSeconds = durationEl.GetInt32();
        }

        string? imageUrl = null;
        if (episodeElement.TryGetProperty("images", out var imagesEl))
        {
            imageUrl = ExtractBestImageUrl(imagesEl, "card(");
        }

        DateTimeOffset? broadcastDate = null;
        if (episodeElement.TryGetProperty("date", out var dateEl)
            && dateEl.ValueKind == JsonValueKind.Object
            && dateEl.TryGetProperty("datetime", out var dateTimeEl)
            && dateTimeEl.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(dateTimeEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
        {
            broadcastDate = parsedDate;
        }

        var (episodeIndex, episodeCount) = ParseEpisodeIndex(title);

        return new CtEpisode(
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
            episodeCount);
    }

    private static (int? Index, int? Count) ParseEpisodeIndex(string title)
    {
        var fraction = EpisodeIndexRegex.Match(title);
        if (fraction.Success)
        {
            return (
                int.Parse(fraction.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(fraction.Groups[2].Value, CultureInfo.InvariantCulture));
        }

        var single = SingleNumberRegex.Match(title);
        return single.Success ? (int.Parse(single.Value, CultureInfo.InvariantCulture), (int?)null) : (null, null);
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

    private CtStreamResolveResult? ParseStreamResolve(string json, string episodeId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("streams", out var streamsEl) || streamsEl.ValueKind != JsonValueKind.Array
            || streamsEl.GetArrayLength() == 0)
        {
            _logger.LogInformation("No streams returned for episode '{EpisodeId}'", episodeId);
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
            episodeId,
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
