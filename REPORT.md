# REPORT.md — iVysílání (Jellyfin channel plugin)

Datum: 2026-10-03 (v1.0.0 i v1.1.0 postaveny a testovány týž den). Build +
test proběhly v tomto sandboxu (bez přístupu k produkčnímu Jellyfin
serveru); server nebyl měněn ani restartován, jak vyžaduje SPEC.md.

## Shrnutí v jedné větě

MVP ze SPEC.md je hotové a zkompilované (v1.0.0), a na žádost uživatele
rozšířené o procházení **celého katalogu iVysílání podle kategorií**
(v1.1.0, místo jen ručně vypsaného seznamu URL); proti reálnému API (ne
mock) proběhly oba testy úspěšně — viz sekce „v1.1.0" a „Test harness" níže.

## v1.1.0 — Celý katalog iVysílání (změna zadání)

Uživatel po v1.0.0 změnil zadání: nechce jen kurátorovaný seznam URL, chce
v channelu procházet **celý katalog** iVysílání (všechny pořady/filmy),
stejně jako na `ceskatelevize.cz/ivysilani/`. Ruční seznam (`ShowUrls`)
zůstal zachován jako složka **„Oblíbené"**.

### Průzkum: jak iVysílání enumeruje všechny pořady

Stránka `ceskatelevize.cz/porady/` (1,28 MB, ~145 odkazů) je ve skutečnosti
jen **homepage** (interně `/ivysilani/`, Next.js SSR) s 24 kurátorovanými
karusely (`HomepageBlock`) — žádná skutečná paginace, `?stranka=2` nic
nezmění, protože SSR výstup na query parametrech vůbec nezávisí (ověřeno:
identický `Content-Length` pro `?strana=2`, `?stranka=2`, `?page=2`,
`?offset=80`, `?from=80`). To přesně odpovídá tomu, co uživatel pozoroval.

Skutečná navigace do kategorií je `/ivysilani/kategorie/{id}-{slug}/` (14
top-level kategorií — Seriály, Filmy, Dokumenty, Zpravodajství, Sport, Pro
děti, Zábava, Historie, Kultura, Rady a recepty, Příroda, Společnost,
Spiritualita, Archiv — nalezeno v nav odkazech
`data-focus-id="nav-{Title}" href="/ivysilani/kategorie/{id}-{slug}/"` na
homepage). I tahle stránka ale v SSR HTML obsahuje jen částečný výpis
(u velké kategorie "České" seriály bylo `totalCount:161` ale v HTML jen 80
položek) — **skutečná paginace běží přes GraphQL na klientovi**, ne přes
URL.

GraphQL endpoint (`https://api.ceskatelevize.cz/graphql/`) byl nalezen v
`runtimeConfig.GRAPHQL_SERVER_URI` vraceném přímo v `__NEXT_DATA__`
stránky. Přímé HTTP volání na tento endpoint s ad-hoc query/introspekcí
vrací pouze obfuskovanou chybu (`GRAPHQL_VALIDATION_FAILED`, generická
česká hláška, žádné detaily) — schéma tedy nejde "oťukat" chybovými
hláškami ani introspekcí (ta je také blokovaná). Skutečný dotaz šel najít
jen rozebráním produkčního JS bundlu webu: stažením `_next/static/chunks/
*.js` (servírováno z `ctfs.ceskatelevize.cz`, ne z `www...`) a nalezením
operace `GetCategoryById` (modul webpacku obsahující `programmeFind` pole)
pomocí `grep`/Node.js evalu přes AST reprezentaci (`graphql-tag` compile-time
AST), ze které šel přečíst čistý text dotazu i z chybové zprávy při pokusu
o plný `eval` (AST obsahoval i pole `loc.source.body` s původním GraphQL
textem). Z toho vznikl vlastní, zjednodušený dotaz:

```graphql
query IvysilaniCatalog($limit:PaginationAmount!$offset:Int!$categoryId:String!){
  category(categoryId:$categoryId){
    programmeFind(limit:$limit offset:$offset){
      totalCount
      items{id slug title shortDescription isPlayable images{card(width:480)}}
    }
  }
}
```

Ověřeno reálnými HTTP voláními (ne tipem):
- `categoryId` funguje **jak pro leaf podžánry** (`3983` Krimi, totalCount
  68), **tak pro top-level kategorie** (`3976` Seriály, totalCount 246;
  `4003` Dokumenty, totalCount **2643** — proto je skutečná paginace
  nezbytná, ne kosmetická).
- `limit`/`offset` je reálná offset-pagination (stránka 1 a 2 "Dokumentů" —
  0 překryvů, ověřeno v test harnessu).
- Neplatné `categoryId` → `errors[0].extensions.code == "NOT_FOUND"`,
  `data: null`, HTTP 200 — klient to musí poznat z `errors`, ne ze status
  kódu.
- Max `limit` nebyl přesně dohledán (web sám používá `80`, což je i náš
  default/clamp) — nebylo potřeba hledat přesnou hranici, `80` evidentně
  funguje na všech testovaných kategoriích.
- Item má `slug` tvaru `{id}-{code}` (např. `12745900949-docent`) — stačí
  k přímému sestavení URL pořadu (`/porady/{slug}/`), žádný další lookup.

### Co se implementovalo

- `CtApiClient`: `GetTopLevelCategoriesAsync` (scrape nav z `/ivysilani/`,
  cache dle `CacheTtlHours`) + `GetCategoryShowsAsync(categoryId, limit,
  offset, ttl, ct)` (GraphQL POST, cache per `categoryId:offset:limit`).
- `IvysilaniChannel`: nový strom ID — kořen vrací `"fav"` (Oblíbené) +
  `"cat:{categoryId}"` pro každou kategorii; `"cat:*"` vrací stránkovaný
  seznam pořadů (`"show:{slug}"`); `"fav:{showId}"` a `"show:{slug}"` oba
  vedou do nezměněné logiky výpisu epizod (sdílená metoda
  `GetShowEpisodesAsync(sourceUrl, ...)`). `GetChannelFeatures().MaxPageSize`
  nastaveno na `80`, aby Jellyfin znal preferovanou velikost stránky.
- **Lazy/no-overfetch**: kořen channelu dělá jen **jedno** HTTP volání
  (seznam kategorií) bez ohledu na to, kolik je kategorií/pořadů — žádné
  volání na pořad/kategorii neproběhne, dokud uživatel danou složku
  neotevře. Filtr "nehrající se nezobrazí" na úrovni kategorie používá
  `isPlayable` flag, který GraphQL vrací rovnou v listingu (žádný extra
  request na pořad jen pro filtrování).
- **Odolnost / rate-limit**: `SendWithRetryAsync` v `CtApiClient` — až 3
  pokusy s exponenciálním backoffem (500 ms, 1500 ms) pro `403`/`429`/`5xx`
  a síťové výjimky; `SemaphoreSlim(2)` limituje počet **souběžných**
  požadavků na ceskatelevize.cz/api.ceskatelevize.cz na 2, ať čímkoliv je
  spouštěno (Jellyfin prohlížení více složek najednou, test harness, …).
- Verze bumpnuta na `1.1.0.0` v `Jellyfin.Plugin.Ivysilani.csproj`
  (`<Version>`, čte ho `BasePlugin.Version` přes reflexi assembly —
  `Plugin.cs` samo žádný hardcoded string verze nemá), `build.yaml`,
  `meta.json`.

### Omezení v1.1.0 (navíc k těm z v1.0.0 níže)

- Pokud Jellyfinův web klient pro channel foldery **nepoužívá** paging
  (`StartIndex`/`Limit`), uvidí uživatel jen první stránku (80 pořadů) u
  velkých kategorií jako Dokumenty (2643 pořadů) — `TotalRecordCount` je ale
  vyplněný korektně, takže klienti respektující paging uvidí vše. Nemohl
  jsem to ověřit v živém webovém klientovi (nemám přístup k serveru).
  **Doporučení pro Fázi 2**, pokud se ukáže jako problém: přidat
  alfabetické/žánrové dílčí složky uvnitř velkých kategorií, aby se první
  stránka vešla do rozumného počtu položek i bez client-side pagingu.
- "ID schéma" položek se v1.1.0 změnilo (`fav:{id}` místo starého holého
  `{id}` pro oblíbené pořady) — staré `1.0.0` instalace by po upgradu mohly
  ztratit sledovaný postup/oblíbenost u již zobrazených epizod v Jellyfinu
  (ne u přehrávání samotného, to je bez dopadu). U nenasazeného MVP to nemá
  praktický dopad.
- `GetCategoryShowsAsync`/`GetTopLevelCategoriesAsync` cachují i prázdný
  výsledek jen implicitně (negativní cache chybí) — opakovaný dotaz na
  neexistující kategorii nebo na kategorii, jejíž první fetch selhal, znovu
  zavolá síť (ne vážný problém, jen bez optimalizace).

## Co je hotové z v1.0.0 (odpovídá bodům 1–7 ve SPEC.md „Rozsah")

1. **Plugin** `Jellyfin.Plugin.Ivysilani` (`Jellyfin.Plugin.Ivysilani/`),
   jméno „iVysílání", GUID `50151328-7f55-42fe-b7d2-a789212ef6fc`, verze
   nyní `1.1.0.0`, `build.yaml` + `meta.json` hotové.
2. **Konfigurace**: stránka v Dashboard → Plugins → iVysílání s textarea
   (`ShowUrls`, jedna URL na řádek) a číselníkem TTL cache v hodinách
   (`CacheTtlHours`, 1–24, default 3). Uloženo přes standardní
   `BasePluginConfiguration`/`IPluginConfiguration`.
3. **Channel obsah** (úroveň pořad → epizody, nezměněno v1.1.0; úroveň
   kořen → kategorie/Oblíbené je nová, viz sekce „v1.1.0" výše): uvnitř
   složky pořadu jsou epizody (`ChannelItemType.Media`,
   `ChannelMediaContentType.Episode`). Podporuje jak URL s konkrétním dílem,
   tak holou URL pořadu (`CtApiClient.ParseShowId`, regex na
   `/porady/{id}(-|/|$)`) — ověřeno testem pro obě varianty.
   Epizody jsou řazené podle rozpoznaného pořadí (`Epizoda N/M` v titulku),
   dál podle data vysílání, dál podle názvu. Epizody, které ČT nemá
   momentálně oznacené jako `playable`, se vůbec nezobrazí (filtr přímo
   v `IvysilaniChannel.GetShowEpisodesAsync`, resp. `Playable` flag z
   katalogu pro filtrování pořadů v `GetCategoryShowsPageAsync`).
4. **Metadata**: název epizody, `SeriesName` = název pořadu, `Overview` =
   popis dílu (fallback na název pořadu), `ImageUrl` z `card(...)` obrázku,
   `RunTimeTicks` z `duration` (sekundy → ticks), `PremiereDate`/`DateCreated`
   z data vysílání.
5. **Přehrávání**: implementováno přes `IRequiresMediaInfoCallback.
   GetChannelItemMediaInfo` (viz „Technické rozhodnutí: lazy resolve" níže) —
   vrací `MediaSourceInfo { Path = streams[0].url, Container = "hls",
   Protocol = Http }` + 2 `MediaStream` (video h264, audio aac) + externí
   `.vtt` titulkovou stopu jako `MediaStream { Type = Subtitle, IsExternal =
   true, DeliveryMethod = External, DeliveryUrl = <vtt url> }`, pokud ČT
   titulky nabízí (v testovaných datech ano, 1 track, `cs`).
   `RequiredHttpHeaders` obsahuje `User-Agent`/`Referer`, aby je případný
   ffmpeg probe/transkódovací krok na serveru poslal taky.
6. **Cache**: `CtApiClient` má dvě nezávislé in-process TTL cache
   (`ConcurrentDictionary`, bez závislosti na `IMemoryCache` z DI — stejný
   vzor jako referenční youtube plugin): seznam epizod pořadu podle
   configurovatelného TTL (1–24 h, default 3 h), resolve streamu (HLS token)
   vždy jen 20 minut nezávisle na tom nastavení, protože token v URL má
   vlastní krátkou expiraci. Žádné médium se nikdy neukládá na disk — jen
   JSON/HTML metadata a (v test harnessu navíc, pro ověření) samotný text
   `.m3u8` manifestu.
7. **Chyby**: všechny síťové/JSON výjimky (`HttpRequestException`,
   `TaskCanceledException`, `JsonException`) jsou odchyceny v `CtApiClient`,
   zalogovány přes `ILogger` a vrací se `null`/prázdný seznam místo shození
   volajícího kódu — channel metody je dál převádí na prázdné
   `ChannelItemResult`/`Array.Empty<MediaSourceInfo>()`, takže chyba u
   jednoho pořadu nespadne celý channel.

## Architektonická rozhodnutí a proč

- **`net10.0` / `Jellyfin.Controller` `12.1.0`, ne `net9.0` z generického
  template.** Stažený NuGet balíček `Jellyfin.Controller 12.1.0` (verze
  odpovídající `/System/Info/Public` → `12.1.0` z cílového serveru) má
  `targetFramework: net10.0` — ověřeno rozbalením skutečného `.nupkg`, ne
  odhadem. `jellyfin-plugin-template` na GitHubu je starší a cílí
  `net9.0`/`Jellyfin.Controller 10.11.5`, což by na serveru 12.1.0 vůbec
  nenaběhlo. Proto byla navíc nainstalována .NET **10** SDK (vedle 8, pro
  jistotu) — viz „Prostředí" níže.
- **`IChannel` v 12.1.0 stále existuje**, ale fyzicky žije v
  `Jellyfin.LiveTv` modulu serveru (`src/Jellyfin.LiveTv/Channels/
ChannelManager.cs` ve zdrojácích `jellyfin/jellyfin`), ne v
  `Emby.Server.Implementations` jako dřív. Referenční
  `kingschnulli/jellyfin-youtube-plugin` z tohoto důvodu (pravděpodobně)
  `IChannel` už nepoužívá a jde přes NFO/library-sync přístup — tenhle plugin
  ale SPEC.md explicitně chce postavit na `IChannel`, což jsem ověřil
  reflexí přes skutečné `MediaBrowser.Controller.dll`/`MediaBrowser.Model.dll`
  12.1.0 (signatury metod, enum hodnoty) a i přes `ChannelManager`
  constructor (`IEnumerable<IChannel> channels` injektované DI
  kontejnerem) — funguje to přesně jako dřív, jen se plugin musí
  zaregistrovat přes `IPluginServiceRegistrator.RegisterServices(...
  serviceCollection.AddSingleton<IChannel, IvysilaniChannel>())`, přesně
  jak to dělá referenční youtube plugin pro svoje jiné služby.
- **Lazy stream resolve (`IRequiresMediaInfoCallback`), ne eager při
  listingu.** `IChannel.GetChannelItems` vrací jen metadata (žádný
  `MediaSources` na epizodě); skutečné volání `stream-data/media/external/
  {id}` (to, co vrací podepsanou/časově omezenou HLS URL) se stane až když
  Jellyfin zavolá `GetChannelItemMediaInfo(id, ct)` pro konkrétní epizodu,
  těsně před přehráním. Důvod: (a) listing jednoho pořadu s desítkami
  epizod by jinak znamenal desítky zbytečných API volání jen pro zobrazení
  seznamu, (b) HLS token v odpovědi má `expiry` v řádu hodin — nemá smysl ho
  získávat dřív, než je potřeba. Filtrace „nehrající se nezobrazuje" na
  úrovni listingu je proto založená na levnějším signálu —
  `EpisodePreview.playable` boolean, který je součástí stránky pořadu a
  nevyžaduje extra request.
- **Vlastní `ConcurrentDictionary` TTL cache, ne `IMemoryCache` z DI.**
  Stejný vzor jako `SimpleResolveCache` v referenčním youtube pluginu —
  nespoléhá se na to, že `IMemoryCache` je v Jellyfinově DI kontejneru
  zaregistrované (nebylo to nikde explicitně potvrzené), takže je to
  robustnější k runtime chybám při startu pluginu.
- **Bitrate na `MediaSourceInfo` zůstává nevyplněný.** Předává se ČT's
  master HLS playlist (obsahuje všech 5 kvalitních variant + víc audio
  stop), ne jedna konkrétní varianta — SPEC.md sám potvrzuje (sekce
  „Ověřená přehratelnost"), že předání master playlistu jako
  `MediaSourceInfo.Path` je bezpečné pro direct play i transcode a
  klient/ffmpeg si ABR vyřeší sám. Natvrdo vyplnit `Bitrate` jedním číslem
  by bylo zavádějící.

## Co je omezené / co je Fáze 2

- **Nepotvrzená úplnost seznamu epizod u dlouhých pořadů.** `__NEXT_DATA__`
  na stránce pořadu obsahuje `EpisodePreview:*` záznamy jen pro aktuálně
  hratelné epizody — u testovaného pořadu „Na tělo" to bylo přesně 5 záznamů
  a `Show.playableEpisodeCount` v datech byl taky `5`, takže se to **zdá**
  být kompletní seznam, ne jen oříznutý výřez/karusel. **Nebylo to ale
  ověřeno na pořadu s desítkami/stovkami epizod** (typicky dlouhodobé
  seriály/soap opery) — pokusy najít takový pořad bez znalosti konkrétní
  URL skončily na WAF 403 (nešlo bezpečně "hádat" ID). Pokud ČT u takových
  pořadů stránku paginuje přes samostatný GraphQL request, který se
  nenačte z prvního HTML, aktuální implementace by viděla jen první dávku.
  **Doporučení pro Fázi 2**: ověřit na konkrétním dlouhém pořadu (Iris má
  přístup k běžnému prohlížeči → najde URL) a případně dořešit paginaci.
- **Season/episode číslování je heuristika.** Parsuje se vzor `Epizoda N/M`
  v titulku regexem; pořady s jiným formátem titulku dostanou epizody bez
  `IndexNumber` a seřadí se podle data vysílání/názvu. Žádné season
  podsložky — SPEC MVP to nevyžaduje, všechny epizody jednoho pořadu jsou v
  jedné ploché složce.
- **`ParentalRating` je statický placeholder** (`GeneralAudience`). ČT v
  datech vrací bohatší PEGI/varovné labely (`PEGI_15`, `FEAR`,
  `SUBSTANCES`, `LANGUAGE` v testovaných datech), ale Jellyfinův
  `ChannelParentalRating` enum má jen 5 obecných hodnot a je navíc na
  úrovni celého *channelu*, ne jednotlivé epizody — mapování by
  nedávalo moc smysl bez rozsáhlejší úpravy a nebylo v MVP scope.
  Žádné blokování obsahu podle PEGI tedy neprobíhá.
- **Channel nemá vlastní ikonu/obrázek** (`GetSupportedChannelImages` vrací
  prázdný výčet). Fáze 2: embednout logo iVysílání jako `EmbeddedResource`.
- **Žádný proaktivní cleanup joby.** Spoléhá se na to, že Jellyfin sám
  pravidelně znovu zavolá `GetChannelItems`/`GetChannelItemMediaInfo` (jeho
  vlastní `ChannelManager` má interní `CacheLength = 3 h`, ověřeno ve
  zdrojácích) — až se to stane a epizoda už nebude `playable`, zmizí. Žádný
  dedikovaný `IScheduledTask`, který by aktivně mazal věci dřív, nebyl
  implementován (SPEC to nevyžaduje explicitně).
- **Žádné přihlašování/DRM.** Přesně podle scope SPEC.md — jen
  neautentizované, nechráněné API.
- **UI umístění "Channels" v moderním Jellyfin web klientovi neověřeno
  naživo** — nemám přístup k běžícímu serveru. V `INSTALL.md` je krok pro
  Iris, ať to po restartu zkontroluje.

## Build

```
$ dotnet build -c Release     # Jellyfin.Plugin.Ivysilani/
Build succeeded.
    0 Warning(s)
    0 Error(s)

$ dotnet publish -c Release -o <out>
Jellyfin.Plugin.Ivysilani -> .../Jellyfin.Plugin.Ivysilani.dll
```

Výsledné artefakty v `artifacts/` (aktuální, v1.1.0):

| Soubor | Velikost | SHA-256 |
|---|---|---|
| `Jellyfin.Plugin.Ivysilani.dll` | 76 288 B | `bf935e96f24a0afbcd26d66b063859329e5475d39291f826f90ef45bc06b0e9f` |
| `meta.json` | 932 B | — |

Build je reprodukovatelný — druhý `dotnet publish` z čistého stromu (po
smazání `bin/`/`obj/`) vyprodukoval bitově identický DLL (stejný SHA-256).

(v1.0.0 artefakt pro referenci: `Jellyfin.Plugin.Ivysilani.dll` 55 296 B,
sha256 `a5a662f5357b7334a288baef5d7179d82b31bd0fc69f728523a30d284eac39c4` —
nahrazen výše, nebyl ponechán v `artifacts/`.)

## Test harness (SPEC.md výstup #4 — povinný reálný výstup)

`TestHarness/` je samostatná konzolová `net8.0` aplikace, která **přímo
kompiluje stejné soubory** jako plugin (`CtApi/CtApiClient.cs`,
`CtApi/Models.cs`, linknuté do projektu — žádná duplicitní reimplementace),
takže testuje přesně tu logiku, co běží v `artifacts/*.dll`. Nic nemockuje:

1. Rozparsuje show id ze dvou tvarů URL (plný díl i holá URL pořadu).
2. Stáhne a rozparsuje živou stránku pořadu `ceskatelevize.cz/porady/...`
   (`__NEXT_DATA__` → `apolloState`).
3. Pro nalezené hratelné epizody zavolá živý
   `api.ceskatelevize.cz/.../stream-data/media/external/{id}` resolve a
   vypíše HLS URL, `playability`, titulky.
4. Navíc (nad rámec SPEC, pro jistotu) skutečně stáhne text master `.m3u8`
   playlistu a ověří, že začíná `#EXTM3U` — tedy že vrácená URL je opravdu
   platný přehratelný HLS manifest, ne jen libovolný string v JSONu.
5. **(v1.1.0)** Zavolá `GetTopLevelCategoriesAsync` a vypíše všech 14
   nalezených kategorií.
6. **(v1.1.0)** Zavolá `GetCategoryShowsAsync` na malou leaf kategorii
   (Krimi, `categoryId=3983`) a vypíše `totalCount`/vzorek pořadů.
7. **(v1.1.0)** Zavolá `GetCategoryShowsAsync` dvakrát na velkou top-level
   kategorii (Dokumenty, `categoryId=4003`, `totalCount=2643`) s
   `offset=0` a `offset=80`, a ověří, že se stránky nepřekrývají (reálná
   offset-pagination, ne jen dvakrát stejná první stránka).
8. **(v1.1.0)** Zavolá `GetCategoryShowsAsync` s neplatným `categoryId` a
   ověří, že klient vrátí `null` (graceful), ne výjimku.

Spuštěno 2026-10-03 19:54:05 +02:00, proti živému `ceskatelevize.cz` /
`api.ceskatelevize.cz` (žádný mock, žádné nahrané fixture). Plný výstup je
v `TestHarness/test-run-output.log`, reprodukovaný zde celý:

```
=== iVysílání plugin — test harness proti reálnému ČT API ===
Spuštěno: 2026-10-03 19:54:05 +02:00

--- Vstupní URL: https://www.ceskatelevize.cz/porady/16208367858-na-telo/224512120130001
    ParseShowId => 16208367858
    Pořad:   Na tělo  (showId=16208367858)
    Popis:   Vrásky přibyly, životní jistoty ubyly. Sedm spolužáků vyráží po třiceti letech na školní sraz, který…
    Obrázek: https://ctfs.ceskatelevize.cz/img/YbYtCvM9R5EGNy3GLsydAH7ZOV1bAIHUtTUymVqCRwY/rs:fit:480:270/sh:0.5/plain/s3/porady-s3/66602233ca665c5f500e72d3/card/na-telo.8f2d7.jpg
    Epizody (5):
      [playable] idx=  1 id=224512120130001  dur=00:53:38 date=2026-09-04  "Epizoda 1/9"
      [playable] idx=  2 id=224512120130002  dur=00:50:13 date=2026-09-11  "Epizoda 2/9"
      [playable] idx=  3 id=224512120130003  dur=00:52:24 date=2026-09-18  "Epizoda 3/9"
      [playable] idx=  4 id=224512120130004  dur=00:52:01 date=2026-09-25  "Epizoda 4/9"
      [playable] idx=  5 id=224512120130005  dur=00:50:40 date=2026-10-02  "Epizoda 5/9"

--- Vstupní URL: https://www.ceskatelevize.cz/porady/16208367858-na-telo/
    ParseShowId => 16208367858
    Pořad:   Na tělo  (showId=16208367858)
    Popis:   Vrásky přibyly, životní jistoty ubyly. Sedm spolužáků vyráží po třiceti letech na školní sraz, který…
    Obrázek: https://ctfs.ceskatelevize.cz/img/YbYtCvM9R5EGNy3GLsydAH7ZOV1bAIHUtTUymVqCRwY/rs:fit:480:270/sh:0.5/plain/s3/porady-s3/66602233ca665c5f500e72d3/card/na-telo.8f2d7.jpg
    Epizody (5):
      [playable] idx=  1 id=224512120130001  dur=00:53:38 date=2026-09-04  "Epizoda 1/9"
      [playable] idx=  2 id=224512120130002  dur=00:50:13 date=2026-09-11  "Epizoda 2/9"
      [playable] idx=  3 id=224512120130003  dur=00:52:24 date=2026-09-18  "Epizoda 3/9"
      [playable] idx=  4 id=224512120130004  dur=00:52:01 date=2026-09-25  "Epizoda 4/9"
      [playable] idx=  5 id=224512120130005  dur=00:50:40 date=2026-10-02  "Epizoda 5/9"

=== Resolve streamu pro přehrávání (GetChannelItemMediaInfo ekvivalent) ===
--- Resolve episodeId=224512120130001 ("Epizoda 1/9")
    title:         Na tělo | Na tělo | 1
    showTitle:     Na tělo
    episodeTitle:  Na tělo | 1
    duration (s):  3217.92
    isPlayable:    True
    playableFrom:  06/05/2024 00:00:00 +02:00
    playableTo:
    previewImage:  https://ctfs.ceskatelevize.cz/porady-s3/episode/66602233ca665c5f500e72d4/card/epizoda-1-9.ef33f.jpeg
    subtitles:     1 track(s) (lang=ces)
    HLS master URL (prvních 140 znaků): https://ivys-nw-cdn.o2tv.cz/cdn/uri/get/?token=b77a1b1d368676958c8a1924f78a9ebba3cb54b2&userId=0bc23ce4-6b6c-4909-8302-e317ee549c99&contentI…
    Playlist fetch: OK, 6502 bajtů, #EXTM3U header = True

--- Resolve episodeId=224512120130002 ("Epizoda 2/9")
    title:         Na tělo | Na tělo | 2
    showTitle:     Na tělo
    episodeTitle:  Na tělo | 2
    duration (s):  3012.84
    isPlayable:    True
    playableFrom:  06/05/2024 00:00:00 +02:00
    playableTo:
    previewImage:  https://ctfs.ceskatelevize.cz/porady-s3/episode/666034801041b402c00f09b2/card/epizoda-2-9.3928e.jpeg
    subtitles:     1 track(s) (lang=ces)
    HLS master URL (prvních 140 znaků): https://ivys-nw-cdn.o2tv.cz/cdn/uri/get/?token=f8b30ff8f420963916dacdafeef23e88bbdf209d&userId=b8bb1570-045c-4e9e-ba4b-0a8e004cc94c&contentI…
    Playlist fetch: OK, 2019 bajtů, #EXTM3U header = True

--- Resolve episodeId=224512120130003 ("Epizoda 3/9")
    title:         Na tělo | Na tělo | 3
    showTitle:     Na tělo
    episodeTitle:  Na tělo | 3
    duration (s):  3144
    isPlayable:    True
    playableFrom:  06/05/2024 00:00:00 +02:00
    playableTo:
    previewImage:  https://ctfs.ceskatelevize.cz/porady-s3/episode/666043808ebfdd0cc90a85a2/card/epizoda-3-9.ea00d.jpeg
    subtitles:     1 track(s) (lang=ces)
    HLS master URL (prvních 140 znaků): https://ivys-nw-cdn.o2tv.cz/cdn/uri/get/?token=f6364c955a3616f639a1140f22a869e9112d6f71&userId=4ff612b4-3e53-4d01-b28b-840a2c31a038&contentI…
    Playlist fetch: OK, 2019 bajtů, #EXTM3U header = True

--- Resolve episodeId=224512120130004 ("Epizoda 4/9")
    title:         Na tělo | Na tělo | 4
    showTitle:     Na tělo
    episodeTitle:  Na tělo | 4
    duration (s):  3120.92
    isPlayable:    True
    playableFrom:  06/05/2024 00:00:00 +02:00
    playableTo:
    previewImage:  https://ctfs.ceskatelevize.cz/porady-s3/episode/666049238ebfdd0cc90a85a3/card/epizoda-4-9.73da8.jpeg
    subtitles:     1 track(s) (lang=ces)
    HLS master URL (prvních 140 znaků): https://ivys-nw-cdn.o2tv.cz/cdn/uri/get/?token=3ccf22cdf07df54f961f5322f7fbcd4bf063d1c4&userId=0ebb1438-b4ef-44ae-abd8-f7d79a10c93e&contentI…
    Playlist fetch: OK, 2019 bajtů, #EXTM3U header = True

--- Resolve episodeId=224512120130005 ("Epizoda 5/9")
    title:         Na tělo | Na tělo | 5
    showTitle:     Na tělo
    episodeTitle:  Na tělo | 5
    duration (s):  3039.72
    isPlayable:    True
    playableFrom:  06/05/2024 00:00:00 +02:00
    playableTo:
    previewImage:  https://ctfs.ceskatelevize.cz/porady-s3/episode/666053e8fd0e9262b50ae369/card/epizoda-5-9.4551e.jpeg
    subtitles:     1 track(s) (lang=ces)
    HLS master URL (prvních 140 znaků): https://ivys-nw-cdn.o2tv.cz/cdn/uri/get/?token=161e71fb6a126c81cffb47f2b26e3a029ab36fc2&userId=41697c3e-f0ec-4b53-b0fb-565f54fcfceb&contentI…
    Playlist fetch: OK, 2019 bajtů, #EXTM3U header = True

=== Katalog: top-level kategorie (nový kořen channelu v1.1) ===
Nalezeno 14 kategorií:
  cat:3976   Seriály              slug=3976-serialy
  cat:3947   Filmy                slug=3947-filmy
  cat:4003   Dokumenty            slug=4003-dokumenty
  cat:4124   Zpravodajství        slug=4124-zpravodajstvi
  cat:4142   Sport                slug=4142-sport
  cat:4118   Pro děti             slug=4118-pro-deti
  cat:4068   Zábava               slug=4068-zabava
  cat:4079   Historie             slug=4079-historie
  cat:4029   Kultura              slug=4029-kultura
  cat:4055   Rady a recepty       slug=4055-rady-a-recepty
  cat:4106   Příroda              slug=4106-priroda
  cat:4093   Společnost           slug=4093-spolecnost
  cat:4191   Spiritualita         slug=4191-spiritualita
  cat:4732   Archiv               slug=4732-archiv

=== Katalog: stránkovaný výpis pořadů v kategorii (malá kategorie, jedna stránka) ===
Kategorie 3983: totalCount=68, vráceno=50
  show:12745900949-docent                            playable=True  "Docent"
  show:15109660246-komisarka-florence                playable=True  "Komisařka Florence"
  show:10354229723-pripady-1-oddeleni                playable=True  "Případy 1. oddělení"
  show:17054374070-ve-vykonu-sluzby                  playable=True  "Ve výkonu služby"
  show:13478071605-oktopus                           playable=True  "OKTOPUS"
  show:15693801551-vysetruje-imma-tataranni          playable=True  "Vyšetřuje Imma Tataranni"
  show:12549377416-misto-zlocinu-ostrava             playable=True  "Místo zločinu Ostrava"
  show:899735-maly-pitaval-z-velkeho-mesta           playable=True  "Malý pitaval z velkého města"
  ...

=== Katalog: stránkování přes offset na velké kategorii (bez duplicit/mezer) ===
Kategorie 4003 ("Dokumenty"): totalCount=2643
  Stránka 1 (offset=0,  limit=80): 80 pořadů, první="Legendy kriminalistiky", poslední="Přísně tajné vraždy"
  Stránka 2 (offset=80, limit=80): 80 pořadů, první="René - vězeň svobody", poslední="Ocelot a já"
  Překryv mezi stránkami (mělo by být 0): 0

=== Katalog: neexistující categoryId se má ztratit potichu (graceful), ne spadnout ===
GetCategoryShowsAsync("999999999") => null (OK, očekáváno)

=== Shrnutí ===
VŠECHNY TESTY PROŠLY (0 chyb).
19:54:07 info: Jellyfin.Plugin.Ivysilani.CtApi.CtApiClient[0] GraphQL error for category '999999999': Obsah stránky nebyl nalezen.
```

Spustit znovu (reprodukce):

```
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1   # jen pokud chybí ICU, viz "Prostředí"
cd TestHarness
dotnet run -c Release
```

## Prostředí (Hermes box, pro případ dalšího buildu)

- `.NET SDK` nainstalováno **user-space** do `/opt/data/home/.dotnet` (bez
  root), přes `dotnet-install.sh` staženo z `raw.githubusercontent.com`
  (přímý `dotnet.microsoft.com/.../dotnet-install.sh` vracel v tomto
  prostředí HTML stránku místo skriptu, zřejmě nějaký proxy/WAF zásah — není
  to problém samotného skriptu).
- Nainstalovány **dvě** verze SDK: `8.0.425` (podle instrukcí ze SPEC.md) a
  `10.0.401` (protože `Jellyfin.Controller 12.1.0` reálně vyžaduje
  `net10.0`, zjištěno rozbalením `.nupkg`, ne odhadem). **Plugin samotný se
  staví s `net10.0`**, `net8.0` zůstal nainstalovaný, protože SPEC o něj
  explicitně žádal a nic mu nebrání existovat souběžně.
- Tento box nemá nainstalované `libicu` (a není root), takže holé `dotnet`
  padalo na `Couldn't find a valid ICU package`. Obešito proměnnou
  `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`. **Tohle se netýká produkčního
  Jellyfin serveru** — oficiální `jellyfin/jellyfin` Docker image má ICU
  součástí image, takže na NASu žádný podobný zásah není potřeba.
- Reference (`jellyfin/jellyfin-plugin-template`,
  `kingschnulli/jellyfin-youtube-plugin`, a `jellyfin/jellyfin` zdrojáky pro
  ověření `IChannel`/`ChannelManager`) byly klonovány do
  `/opt/data/workspace/ivysilani-plugin/.refs/` (uvnitř workspace, jak
  vyžaduje SPEC.md), `gitignore`ováno a po dokončení práce **smazáno** —
  nejsou součástí finálního stromu.
- Pro v1.1.0 průzkum GraphQL API bylo použito i `node` (v26.5.1, byl na
  boxu předinstalovaný) — k vyhodnocení minifikovaného JS/GraphQL-AST z
  produkčního bundlu `ceskatelevize.cz`, čistě jako jednorázový
  research nástroj v `/tmp`, nic z toho není součástí pluginu ani repa.

## Struktura repozitáře

```
Jellyfin.Plugin.Ivysilani/     # zdrojáky pluginu
  Plugin.cs                    # BasePlugin + IPluginServiceRegistrator
  Configuration/                # PluginConfiguration + configPage.html
  CtApi/                        # CtApiClient (HTTP+parsing+cache) + Models
  Channel/IvysilaniChannel.cs   # IChannel + IRequiresMediaInfoCallback
  build.yaml, meta.json
TestHarness/                   # samostatná konzolová app, viz výše
artifacts/                     # postavený .dll + meta.json pro instalaci
INSTALL.md                     # kroky pro Iris
REPORT.md                      # tento soubor
SPEC.md                        # původní zadání
```
