using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ivysilani.CtApi;
using Microsoft.Extensions.Logging;

// Headless integration test harness required by SPEC.md step "Výstup" #4:
// hits the REAL ceskatelevize.cz / api.ceskatelevize.cz endpoints (no mocks),
// exercises the exact CtApiClient shipped in the plugin DLL, and prints what
// it found so the result can be captured as evidence. Never downloads media.
using var loggerFactory = LoggerFactory.Create(builder => builder
    .AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    })
    .SetMinimumLevel(LogLevel.Information));

var logger = loggerFactory.CreateLogger<CtApiClient>();
using var client = new CtApiClient(logger);

var testUrls = new[]
{
    // Full episode URL, as a user would paste from the browser address bar.
    "https://www.ceskatelevize.cz/porady/16208367858-na-telo/224512120130001",

    // Bare show URL (no specific episode) - SPEC requires both shapes to resolve
    // to the same show id ("pořad i dílčí epizoda — ber jako pořad").
    "https://www.ceskatelevize.cz/porady/16208367858-na-telo/",
};

var failures = 0;

Console.WriteLine("=== iVysílání plugin — test harness proti reálnému ČT API ===");
Console.WriteLine($"Spuštěno: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
Console.WriteLine();

foreach (var url in testUrls)
{
    Console.WriteLine($"--- Vstupní URL: {url}");

    var showId = CtApiClient.ParseShowId(url);
    Console.WriteLine($"    ParseShowId => {showId ?? "(null)"}");
    if (showId is null)
    {
        Console.WriteLine("    CHYBA: nepodařilo se rozparsovat show id z URL.");
        failures++;
        continue;
    }

    var show = await client.GetShowAsync(url, TimeSpan.FromHours(3), default).ConfigureAwait(false);
    if (show is null)
    {
        Console.WriteLine("    CHYBA: GetShowAsync vrátilo null (stránka se nenačetla nebo __NEXT_DATA__ chybí).");
        failures++;
        continue;
    }

    Console.WriteLine($"    Pořad:   {show.Title}  (showId={show.ShowId})");
    Console.WriteLine($"    Popis:   {Truncate(show.ShortDescription, 100)}");
    Console.WriteLine($"    Obrázek: {show.ImageUrl}");
    Console.WriteLine($"    Epizody ({show.Episodes.Count}):");

    foreach (var ep in show.Episodes)
    {
        Console.WriteLine(
            $"      [{(ep.Playable ? "playable" : "SKIP    ")}] "
            + $"idx={(ep.EpisodeIndex?.ToString(CultureInfo.InvariantCulture) ?? "?"),3} "
            + $"id={ep.Id,-16} "
            + $"dur={(ep.DurationSeconds.HasValue ? TimeSpan.FromSeconds(ep.DurationSeconds.Value).ToString(@"hh\:mm\:ss") : "?")} "
            + $"date={(ep.BroadcastDate?.ToString("yyyy-MM-dd") ?? "?")}  "
            + $"\"{ep.Title}\"");
    }

    Console.WriteLine();
}

Console.WriteLine("=== Resolve streamu pro přehrávání (GetChannelItemMediaInfo ekvivalent) ===");

var showForResolve = await client.GetShowAsync(testUrls[0], TimeSpan.FromHours(3), default).ConfigureAwait(false);
if (showForResolve is null)
{
    Console.WriteLine("CHYBA: nepodařilo se znovu načíst pořad pro test resolve streamu.");
    failures++;
}
else
{
    var toResolve = showForResolve.Episodes.Where(e => e.Playable).ToList();
    if (toResolve.Count == 0)
    {
        Console.WriteLine("CHYBA: pořad nemá žádnou epizodu oznacenou jako playable.");
        failures++;
    }

    foreach (var ep in toResolve)
    {
        Console.WriteLine($"--- Resolve episodeId={ep.Id} (\"{ep.Title}\")");
        var resolved = await client.ResolveStreamAsync(ep.Id, default).ConfigureAwait(false);
        if (resolved is null)
        {
            Console.WriteLine("    CHYBA: ResolveStreamAsync vrátilo null.");
            failures++;
            continue;
        }

        Console.WriteLine($"    title:         {resolved.Title}");
        Console.WriteLine($"    showTitle:     {resolved.ShowTitle}");
        Console.WriteLine($"    episodeTitle:  {resolved.EpisodeTitle}");
        Console.WriteLine($"    duration (s):  {resolved.DurationSeconds}");
        Console.WriteLine($"    isPlayable:    {resolved.Playability?.IsPlayable}");
        Console.WriteLine($"    playableFrom:  {resolved.Playability?.PlayableFrom}");
        Console.WriteLine($"    playableTo:    {resolved.Playability?.PlayableTo}");
        Console.WriteLine($"    previewImage:  {resolved.PreviewImageUrl}");
        Console.WriteLine($"    subtitles:     {resolved.Subtitles.Count} track(s)"
            + (resolved.Subtitles.Count > 0 ? $" (lang={resolved.Subtitles[0].Language})" : string.Empty));
        Console.WriteLine($"    HLS master URL (prvních 140 znaků): {Truncate(resolved.HlsUrl, 140)}");

        if (string.IsNullOrEmpty(resolved.HlsUrl) || !resolved.HlsUrl.StartsWith("https://", StringComparison.Ordinal))
        {
            Console.WriteLine("    CHYBA: HLS URL chybí nebo nevypadá jako platná https adresa.");
            failures++;
        }
        else
        {
            // Sanity-check the resolved playlist is actually fetchable HLS content.
            // This still does NOT download any media — only the small .m3u8 manifest text.
            using var http = new HttpClient();
            try
            {
                var playlist = await http.GetStringAsync(resolved.HlsUrl).ConfigureAwait(false);
                var looksLikeHls = playlist.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal);
                Console.WriteLine($"    Playlist fetch: OK, {playlist.Length} bajtů, #EXTM3U header = {looksLikeHls}");
                if (!looksLikeHls)
                {
                    failures++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    CHYBA při stažení master playlistu: {ex.Message}");
                failures++;
            }
        }

        Console.WriteLine();
    }
}

Console.WriteLine("=== Shrnutí ===");
Console.WriteLine(failures == 0 ? "VŠECHNY TESTY PROŠLY (0 chyb)." : $"NALEZENO {failures} CHYB.");

return failures == 0 ? 0 : 1;

static string Truncate(string? value, int maxLength)
{
    if (string.IsNullOrEmpty(value))
    {
        return "(none)";
    }

    return value.Length <= maxLength ? value : value[..maxLength] + "…";
}
