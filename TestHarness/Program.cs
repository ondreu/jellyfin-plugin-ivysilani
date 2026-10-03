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

Console.WriteLine("=== Katalog: top-level kategorie (nový kořen channelu v1.1) ===");

var categories = await client.GetTopLevelCategoriesAsync(TimeSpan.FromHours(3), default).ConfigureAwait(false);
if (categories.Count == 0)
{
    Console.WriteLine("CHYBA: GetTopLevelCategoriesAsync nevrátilo žádné kategorie.");
    failures++;
}
else
{
    Console.WriteLine($"Nalezeno {categories.Count} kategorií:");
    foreach (var cat in categories)
    {
        Console.WriteLine($"  cat:{cat.CategoryId,-6} {cat.Title,-20} slug={cat.Slug}");
    }
}

Console.WriteLine();
Console.WriteLine("=== Katalog: stránkovaný výpis pořadů v kategorii (malá kategorie, jedna stránka) ===");

const string smallCategoryId = "3983"; // Krimi (sub-genre of Seriály), ~68 shows total
var smallPage = await client.GetCategoryShowsAsync(smallCategoryId, 50, 0, TimeSpan.FromHours(3), default).ConfigureAwait(false);
if (smallPage is null)
{
    Console.WriteLine($"CHYBA: GetCategoryShowsAsync({smallCategoryId}) vrátilo null.");
    failures++;
}
else
{
    Console.WriteLine($"Kategorie {smallCategoryId}: totalCount={smallPage.TotalCount}, vráceno={smallPage.Items.Count}");
    foreach (var show in smallPage.Items.Take(8))
    {
        Console.WriteLine($"  show:{show.Slug,-45} playable={show.Playable,-5} \"{show.Title}\"");
    }

    Console.WriteLine("  ...");
}

Console.WriteLine();
Console.WriteLine("=== Katalog: stránkování přes offset na velké kategorii (bez duplicit/mezer) ===");

const string bigCategoryId = "4003"; // Dokumenty, several thousand shows - real pagination required
const int pageSize = 80;
var page1 = await client.GetCategoryShowsAsync(bigCategoryId, pageSize, 0, TimeSpan.FromHours(3), default).ConfigureAwait(false);
var page2 = await client.GetCategoryShowsAsync(bigCategoryId, pageSize, pageSize, TimeSpan.FromHours(3), default).ConfigureAwait(false);

if (page1 is null || page2 is null)
{
    Console.WriteLine($"CHYBA: GetCategoryShowsAsync({bigCategoryId}) vrátilo null pro stránku 1 nebo 2.");
    failures++;
}
else
{
    Console.WriteLine($"Kategorie {bigCategoryId} (\"Dokumenty\"): totalCount={page1.TotalCount}");
    Console.WriteLine($"  Stránka 1 (offset=0,  limit={pageSize}): {page1.Items.Count} pořadů, první=\"{page1.Items.FirstOrDefault()?.Title}\", poslední=\"{page1.Items.LastOrDefault()?.Title}\"");
    Console.WriteLine($"  Stránka 2 (offset={pageSize}, limit={pageSize}): {page2.Items.Count} pořadů, první=\"{page2.Items.FirstOrDefault()?.Title}\", poslední=\"{page2.Items.LastOrDefault()?.Title}\"");

    var page1Ids = page1.Items.Select(s => s.ShowId).ToHashSet(StringComparer.Ordinal);
    var overlap = page2.Items.Count(s => page1Ids.Contains(s.ShowId));
    Console.WriteLine($"  Překryv mezi stránkami (mělo by být 0): {overlap}");

    if (page1.TotalCount < 500)
    {
        Console.WriteLine($"  CHYBA: čekal jsem u \"Dokumenty\" řádově tisíce položek, totalCount={page1.TotalCount} je podezřele nízké.");
        failures++;
    }

    if (overlap != 0)
    {
        Console.WriteLine("  CHYBA: stránky se překrývají, offset pagination nefunguje jak má.");
        failures++;
    }

    if (page1.Items.Count == 0 || page2.Items.Count == 0)
    {
        Console.WriteLine("  CHYBA: některá ze stránek je prázdná.");
        failures++;
    }
}

Console.WriteLine();
Console.WriteLine("=== Katalog: neexistující categoryId se má ztratit potichu (graceful), ne spadnout ===");

var badPage = await client.GetCategoryShowsAsync("999999999", 10, 0, TimeSpan.FromHours(3), default).ConfigureAwait(false);
Console.WriteLine($"GetCategoryShowsAsync(\"999999999\") => {(badPage is null ? "null (OK, očekáváno)" : "NEOČEKÁVANĚ vrátilo data")}");
if (badPage is not null)
{
    Console.WriteLine("  CHYBA: neplatné categoryId by měl server odmítnout (NOT_FOUND) a klient to má vrátit jako null.");
    failures++;
}

Console.WriteLine();
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
