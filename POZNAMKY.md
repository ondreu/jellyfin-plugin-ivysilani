# Technické poznámky

Poznámky pro toho, kdo se bude do pluginu dívat. Ne příliš do hloubky —
spíš na co si dát pozor a proč to je tak, jak to je.

## Jak je plugin pospojovaný

```
IvysilaniChannel (IChannel)
 ├─ kořen: kategorie (scrape navigace na ceskatelevize.cz/ivysilani/) + Oblíbené
 ├─ kategorie: programy přes GraphQL (viz níže), stránkováno po80
 ├─ pořad: díly ze stránky pořadu (__NEXT_DATA__)
 │   └─ sezónní složky (ChannelFolderType.Season) → díly
 └─ přehrávání: resolve streamu až v okamžiku přehrání → MediaSourceInfo (HLS)
```

`CtApiClient` má tři role: navigace/kategorie, seznam dílů u pořadu a resolve
adresy streamu. Všechno je lazy — plugin nic nedělá, dokud klient něco
neotevře.

## ČT API

Ověřeno2026-10, funguje bez přihlášení. Vždy posílej prohlížečový
`User-Agent` a `Referer`/`Origin` na `ceskatelevize.cz`, jinak ČT vrací
chyby.

**Kategorie (GraphQL)** — endpoint `https://api.ceskatelevize.cz/graphql/`,
nalezen v `runtimeConfig.GRAPHQL_SERVER_URI` v `__NEXT_DATA__` homepage.
Introspekce je blokovaná, schéma šlo vyčíst jen z produkčního JS bundlu
(operace `GetCategoryById`, pole `programmeFind`):

```graphql
query($limit:PaginationAmount!,$offset:Int!,$categoryId:String!) {
  category(categoryId: $categoryId) {
    programmeFind(limit: $limit, offset: $offset) {
      totalCount
      items { id slug title shortDescription isPlayable images { card(width:480) } }
    }
  }
}
```

- `categoryId` funguje pro top-level kategorie i podžánry.
- Limit ber na80 (stejně jako web), offset je klasická paginace —
  ověřeno na kategorii s2643 položkami, žádné překryvy.
- Chyby chodí jako HTTP200 s `errors[0].extensions.code` (třeba
  `NOT_FOUND`), ne jako špatný status kód.
- `slug` tvaru `{id}-{code}` stačí k sestavení URL pořadu.

Starší postup přes `?stranka=` na stránce kategorie **nefunguje** — SSR
výstup na query parametrech nezávisí, web stránkuje až klientem.

**Seznam dílů** — data jsou v `props.apolloState` v `__NEXT_DATA__` stránky
pořadu. Pole `isPlayable` / `playableInterval` určují, jestli se díl smí
zobrazit (když ČT díl stáhne, prostě zmizí).

**Resolve streamu** — pro každý díl až v okamžiku přehrání:

```
GET https://api.ceskatelevize.cz/video/v1/playlist-vod/v1/stream-data/media/external/{EPISODE_ID}
    ?canPlayDrm=false&streamType=hls&quality=web&maxQualityCount=5
    &origin=ivysilani&client=iVysilaniWeb&clientVersion=0.37.6
```

Odpověď obsahuje `streams[0].url` (master playlist, varianty až1080p),
`duration`, případně `subtitles` (vtt/ttml) a `playability`. DRM žádné
(`drmOnly=false`), takže stačí předat HLS adresu jako zdroj a Jellyfin to
vezme direct play.

## Na co si dát v Jellyfinu pozor

**`MediaSourceInfo.Id` se záměrně NEvyplňuje (od v1.3.1).** Dřív plugin
posílal vlastní md5-Guid (předtím vůbec desítkové ID ČT, na které server
spadal s `System.FormatException: Unrecognized Guid format`). Jenže server
vrací media source na dvou místech a Id se musí shodovat: v
`GET /Items/{id}?fields=MediaSources` je to placeholder s `Id = item.Id`,
zatímco v `POST /Items/{id}/PlaybackInfo` se bere z `GetChannelItemMediaInfo`.
Android TV klient si bere `MediaSourceId` z toho prvního místa a posílá ho
zpátko do PlaybackInfo — vlastní md5 tam hledání zdroje nenašlo →
`errorCode=NoCompatibleStream` → jellyfin-androidtv tenhle případ v ne-LiveTV
větvi jen `Timber.e` loguje (bez hlášky i bez zavření přehrávače) → **černá
obrazovka s časem nahoře a ničím dalším**. Prázdné Id nechá
`ChannelManager.NormalizeMediaSources` doplnit na `item.Id` (tvar `N`), takže
obě cesty sedí a je to pořád GUID-kompatibilní pro `Guid.Parse` v HLS cestě.
Mobil `MediaSourceId` neposílá, proto mu to jelo — proto bug prošel.

**Bez sezónních složek seriály nepřehraješ.** `Series.GetEpisodes()` v
Jellyfinu prochází jen `OfType<Season>()` — epizody nalepené přímo pod
složkou seriálu jsou pro tento endpoint neviditelné, vrací se200 s prázdným
seznamem a web/appka to ukončí ještě před `PlaybackInfo` s hláškou „Nelze
najít platný zdroj médií k přehrání". Proto plugin vydává
`Series → Season N → Episode` (od v1.3.0, `DataVersion "3"` — tato změna
nutí Jellyfin přepsat uloženou strukturu channelu). Díly musí mít
`ParentIndexNumber` i `SeriesPresentationUniqueKey` správně, jinak je
endpoint zase neposbírá.

**Titulky zatím ne.** Plugin umí zjistit adresu `.vtt`, ale Jellyfin12.1 na
žádost o externí titulky bez `Path` spadne (`SubtitleEncoder.GetReadableFile`,
`NullReferenceException`) a přítomnost titulkové stopy zavede klienta na
variantu přehrávání, která vrací500. Takže titulky z `MediaSourceInfo`
záměrně vynecháváme. Vrátit, až to Jellyfin napraví.

**Cache channelu.** Jellyfin si výpis channelu ukládá do souboru v
`<config>/cache/channels/` na3 hodiny — je vlastněný rootem, takže když
potřebuješ vymazat (třeba po upgrade pluginu, aby se přepsala struktura),
musí to jít přes root (`docker exec -u0 …`). Stačí ale i to, že se zvedne
`DataVersion` a Jellyfin sám udělá nový průchod.

**Relay (mimo tento repo)** — živé kanály (Kick + ČT live) řeší samostatný
kontejner `kick-relay`; ten má vlastní merge ČT audia a videa přes ffmpeg,
protože ČT streamuje obě složky s rozdílnými časovými osami. Tenhle plugin
s tím nemá nic společného.

## Testování

```bash
cd TestHarness && dotnet run -c Release
```

Harness mluví s reálným ČT API — kontroluje navigaci kategorií, stránkování
(programFind s offsety), parse dílů u pořadu a resolve streamu. Mocky
nepoužíváme záměrně: ČT API se mění a mock by to nezachytil.

## Stav repozitáře

- `artifacts/` — poslední build (DLL + meta.json), z něj se dělají releasy.
- CI (`.github/workflows/build.yml`) staví na každý push — mělo by být zelené.
- Verze se musí shodovat na třech místech: `.csproj`, `meta.json`, `build.yaml`.
