using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Ivysilani.CtApi;

/// <summary>
/// A pořad (show) resolved from a ceskatelevize.cz "/porady/{id}-..." page, with its full
/// (fully paginated) episode list.
/// </summary>
public sealed record CtShowInfo(
    string ShowId,
    string Title,
    string? ShortDescription,
    string? ImageUrl,
    IReadOnlyList<CtEpisode> Episodes);

/// <summary>
/// One episode of a show, as returned by the episodesPreviewFind GraphQL query.
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
    int? EpisodeCount,
    string? SeasonTitle,
    int? SeasonNumber);

/// <summary>
/// The playable interval reported by the stream-data resolve endpoint.
/// </summary>
public sealed record CtPlayability(bool IsPlayable, DateTimeOffset? PlayableFrom, DateTimeOffset? PlayableTo);

/// <summary>
/// An external subtitle track for an episode.
/// </summary>
public sealed record CtSubtitle(string Language, string Url);

/// <summary>
/// The result of resolving an episode's (or movie's) playable HLS stream via the ČT
/// stream-data API.
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

/// <summary>
/// A top-level iVysílání catalog category/genre (e.g. "Seriály", "Filmy"), as linked from the
/// main catalog navigation ("/ivysilani/kategorie/{categoryId}-{slug}/").
/// </summary>
public sealed record CtCategory(string CategoryId, string Slug, string Title);

/// <summary>
/// One show (or movie) as listed in a catalog category page (full catalog, not the curated
/// list the user configures by hand). <see cref="Slug"/> already contains the show id prefix
/// (e.g. "12745900949-docent"). <see cref="Idec"/> is a playable content id belonging to this
/// show - for a movie it resolves the movie itself directly; for a series it seeds the full
/// episode lookup (<c>episodesPreviewFind</c>).
/// </summary>
public sealed record CtCatalogShow(
    string ShowId,
    string Slug,
    string Title,
    string? ShortDescription,
    bool Playable,
    string? ImageUrl,
    string ShowType,
    string Idec,
    int? DurationSeconds);

/// <summary>
/// One page (or, once fully paginated, the complete set) of a category's show listing
/// (GraphQL <c>category.programmeFind</c>), with the total count reported by the API.
/// </summary>
public sealed record CtCatalogPage(int TotalCount, IReadOnlyList<CtCatalogShow> Items);
