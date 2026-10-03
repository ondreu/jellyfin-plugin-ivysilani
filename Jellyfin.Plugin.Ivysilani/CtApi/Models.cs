using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Ivysilani.CtApi;

/// <summary>
/// A pořad (show) resolved from a ceskatelevize.cz "/porady/{id}-..." page, with its episode list.
/// </summary>
public sealed record CtShowInfo(
    string ShowId,
    string Title,
    string? ShortDescription,
    string? ImageUrl,
    IReadOnlyList<CtEpisode> Episodes);

/// <summary>
/// One episode of a show, as listed on the show's page (before stream resolution).
/// </summary>
public sealed record CtEpisode(
    string Id,
    string ShowId,
    string ShowTitle,
    string Title,
    string? Description,
    bool Playable,
    int? DurationSeconds,
    string? ImageUrl,
    DateTimeOffset? BroadcastDate,
    int? EpisodeIndex,
    int? EpisodeCount);

/// <summary>
/// The playable interval reported by the stream-data resolve endpoint.
/// </summary>
public sealed record CtPlayability(bool IsPlayable, DateTimeOffset? PlayableFrom, DateTimeOffset? PlayableTo);

/// <summary>
/// An external subtitle track for an episode.
/// </summary>
public sealed record CtSubtitle(string Language, string Url);

/// <summary>
/// The result of resolving an episode's playable HLS stream via the ČT stream-data API.
/// </summary>
public sealed record CtStreamResolveResult(
    string ExternalId,
    string? Title,
    string? EpisodeTitle,
    string? ShowTitle,
    double? DurationSeconds,
    string HlsUrl,
    string? PreviewImageUrl,
    CtPlayability? Playability,
    IReadOnlyList<CtSubtitle> Subtitles);
