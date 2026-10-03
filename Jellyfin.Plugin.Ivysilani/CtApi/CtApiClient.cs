using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
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

    private static readonly TimeSpan StreamCacheTtl = TimeSpan.FromMinutes(20);

    private static readonly Regex NextDataRegex = new(
        "<script id=\"__NEXT_DATA__\" type=\"application/json\">(.*?)</script>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ShowIdRegex = new(@"/porady/(\d+)(?:-|/|$)", RegexOptions.Compiled);

    private static readonly Regex EpisodeIndexRegex = new(@"(\d+)\s*/\s*(\d+)", RegexOptions.Compiled);

    private static readonly Regex SingleNumberRegex = new(@"\d+", RegexOptions.Compiled);

    private static readonly Regex ImageWidthRegex = new("\"width\":(\\d+)", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly ILogger<CtApiClient> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry<CtShowInfo>> _showCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheEntry<CtStreamResolveResult>> _streamCache = new(StringComparer.Ordinal);

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

    private async Task<string> FetchStringAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
