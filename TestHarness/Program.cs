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
Console.WriteLine("=== v1.2 fix #1: MediaSourceInfo.Id musí být Guid-parseable ===");

var sampleContentId = "219381482650009";
var guid1 = CtApiClient.ToMediaSourceGuid(sampleContentId).ToString("N");
var guid2 = CtApiClient.ToMediaSourceGuid(sampleContentId).ToString("N");
var reparsed = Guid.TryParse(guid1, out _);
Console.WriteLine($"ToMediaSourceGuid(\"{sampleContentId}\") => {guid1}");
Console.WriteLine($"  Guid.TryParse OK: {reparsed}");
Console.WriteLine($"  Deterministický (stejný vstup => stejný výstup): {guid1 == guid2}");
if (!reparsed || guid1 != guid2)
{
    Console.WriteLine("  CHYBA: výsledek není platný/stabilní Guid string.");
    failures++;
}

Console.WriteLine();
Console.WriteLine("=== v1.2 fix #2: kategorie musí vrátit SKUTEČNÝ celkový počet, ne jen první dávku ===");

const string seriesCategoryId = "3976"; // Seriály - known from v1.1 harness run to have 246+ shows
var fullSeries = await client.GetFullCategoryAsync(seriesCategoryId, TimeSpan.FromHours(3), default).ConfigureAwait(false);
if (fullSeries is null)
{
    Console.WriteLine($"CHYBA: GetFullCategoryAsync({seriesCategoryId}) vrátilo null.");
    failures++;
}
else
{
    Console.WriteLine($"Kategorie {seriesCategoryId} (\"Seriály\"): totalCount={fullSeries.TotalCount}, skutečně načteno={fullSeries.Items.Count}");
    var distinctIds = fullSeries.Items.Select(s => s.ShowId).Distinct(StringComparer.Ordinal).Count();
    Console.WriteLine($"  Unikátních showId: {distinctIds}");

    if (fullSeries.Items.Count != fullSeries.TotalCount)
    {
        Console.WriteLine($"  CHYBA: načteno {fullSeries.Items.Count} položek, ale totalCount={fullSeries.TotalCount} (dřívější bug: oříznuto na 80/první stránku).");
        failures++;
    }

    if (fullSeries.TotalCount <= CtApiClient.CatalogPageSize)
    {
        Console.WriteLine($"  CHYBA: čekal jsem u \"Seriály\" víc než {CtApiClient.CatalogPageSize} položek (jinak tento test nic neprokazuje).");
        failures++;
    }

    if (distinctIds != fullSeries.Items.Count)
    {
        Console.WriteLine("  CHYBA: v plném výpisu kategorie jsou duplicitní showId (stránkování se překrývá).");
        failures++;
    }
}

Console.WriteLine();
Console.WriteLine("=== v1.2 fix #3: Arabela - plný seznam epizod (13), ne jen první dávka (10) ===");

CtCatalogShow? arabela = fullSeries?.Items.FirstOrDefault(s => string.Equals(s.Title, "Arabela", StringComparison.Ordinal));
if (arabela is null)
{
    Console.WriteLine("CHYBA: 'Arabela' nebyla v kategorii Seriály nalezena (API/katalog se mohl změnit).");
    failures++;
}
else
{
    Console.WriteLine($"Arabela: id={arabela.ShowId} idec={arabela.Idec} showType={arabela.ShowType}");
    var arabelaEpisodes = await client.GetEpisodesAsync(arabela.Idec, TimeSpan.FromHours(3), default).ConfigureAwait(false);
    if (arabelaEpisodes is null)
    {
        Console.WriteLine("  CHYBA: GetEpisodesAsync vrátilo null.");
        failures++;
    }
    else
    {
        Console.WriteLine($"  Počet epizod: {arabelaEpisodes.Count} (web ukazuje 'N/13' v titulcích)");
        foreach (var ep in arabelaEpisodes)
        {
            Console.WriteLine(
                $"    idx={(ep.EpisodeIndex?.ToString(CultureInfo.InvariantCulture) ?? "?"),3} "
                + $"season={ep.SeasonNumber?.ToString(CultureInfo.InvariantCulture) ?? "-"} "
                + $"id={ep.Id,-16} \"{ep.Title}\"");
        }

        if (arabelaEpisodes.Count != 13)
        {
            Console.WriteLine($"  CHYBA: čekalo se 13 epizod (1/13..13/13), nalezeno {arabelaEpisodes.Count} (dřívější bug: oříznuto na první dávku z __NEXT_DATA__).");
            failures++;
        }

        var distinctEpIds = arabelaEpisodes.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count();
        if (distinctEpIds != arabelaEpisodes.Count)
        {
            Console.WriteLine("  CHYBA: duplicitní episode id ve výsledku.");
            failures++;
        }
    }
}

Console.WriteLine();
Console.WriteLine("=== v1.2 fix #5 (drobnost): duplicitní názvy epizod napříč řadami se musí odlišit ===");

var babylonShow = fullSeries?.Items.FirstOrDefault(s => s.Title.Contains("Babylon Berl", StringComparison.Ordinal));
if (babylonShow is null)
{
    Console.WriteLine("Babylon Berlín nebyl v kategorii Seriály nalezen - test přeskočen (není chyba, jen není v aktuálním katalogu na stejném místě).");
}
else
{
    var babylonEpisodes = await client.GetEpisodesAsync(babylonShow.Idec, TimeSpan.FromHours(3), default).ConfigureAwait(false);
    if (babylonEpisodes is null)
    {
        Console.WriteLine("  CHYBA: GetEpisodesAsync(Babylon Berlín) vrátilo null.");
        failures++;
    }
    else
    {
        var byTitle = babylonEpisodes.GroupBy(e => e.Title, StringComparer.Ordinal).Where(g => g.Count() > 1).ToList();
        Console.WriteLine($"  Babylon Berlín: {babylonEpisodes.Count} epizod, duplicitních názvů PO odlišení: {byTitle.Count} (má být 0)");
        foreach (var ep in babylonEpisodes.Where(e => e.Title.Contains("Epizoda 1/", StringComparison.Ordinal)))
        {
            Console.WriteLine($"    id={ep.Id} \"{ep.Title}\" season={ep.SeasonTitle}");
        }

        if (byTitle.Count > 0)
        {
            Console.WriteLine("  CHYBA: po disambiguaci stále existují duplicitní zobrazované názvy epizod.");
            failures++;
        }
    }
}

Console.WriteLine();
Console.WriteLine("=== v1.2 fix #4: film z kategorie Filmy musí jít přehrát přímo (ne jako prázdná složka) ===");

const string moviesCategoryId = "3947"; // Filmy
var fullMovies = await client.GetFullCategoryAsync(moviesCategoryId, TimeSpan.FromHours(3), default).ConfigureAwait(false);
if (fullMovies is null)
{
    Console.WriteLine($"CHYBA: GetFullCategoryAsync({moviesCategoryId}) vrátilo null.");
    failures++;
}
else
{
    var movie = fullMovies.Items.FirstOrDefault(s => string.Equals(s.ShowType, "movie", StringComparison.OrdinalIgnoreCase) && s.Playable);
    if (movie is null)
    {
        Console.WriteLine("  CHYBA: v kategorii Filmy nebyl nalezen žádný showType=='movie' záznam.");
        failures++;
    }
    else
    {
        Console.WriteLine($"  Film: \"{movie.Title}\" id={movie.ShowId} idec={movie.Idec} duration={movie.DurationSeconds}s");
        var movieResolved = await client.ResolveStreamAsync(movie.Idec, default).ConfigureAwait(false);
        if (movieResolved is null)
        {
            Console.WriteLine("  CHYBA: ResolveStreamAsync(film.idec) vrátilo null - film by se v katalogu zobrazil jako nehratelný/prázdný.");
            failures++;
        }
        else
        {
            Console.WriteLine($"  Resolve OK: isPlayable={movieResolved.Playability?.IsPlayable}, duration={movieResolved.DurationSeconds}s");
            Console.WriteLine($"  HLS master URL (prvních 120 znaků): {Truncate(movieResolved.HlsUrl, 120)}");
            if (movieResolved.Playability is { IsPlayable: false })
            {
                Console.WriteLine("  CHYBA: film vybraný jako isPlayable=true v katalogu, ale resolve říká, že není hratelný.");
                failures++;
            }
        }

        var mediaSourceId = CtApiClient.ToMediaSourceGuid(movie.Idec).ToString("N");
        Console.WriteLine($"  MediaSourceInfo.Id, který by šel do GetChannelItemMediaInfo: {mediaSourceId} (Guid-parseable: {Guid.TryParse(mediaSourceId, out _)})");
    }
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
