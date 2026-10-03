# REPORT.md — iVysílání (Jellyfin channel plugin)

Datum: 2026-10-03. Build + test proběhly v tomto sandboxu (bez přístupu k
produkčnímu Jellyfin serveru); server nebyl měněn ani restartován, jak
vyžaduje SPEC.md.

## Shrnutí v jedné větě

MVP ze SPEC.md je hotové a zkompilované: plugin zobrazí uživatelem
vybrané pořady z iVysílání jako složky s epizodami a přehrává je přímo
přes HLS ze serverů ČT; proti reálnému API (ne mock) proběhl test
úspěšně — viz sekce „Test harness" níže.

## Co je hotové (odpovídá bodům 1–7 ve SPEC.md „Rozsah")

1. **Plugin** `Jellyfin.Plugin.Ivysilani` (`Jellyfin.Plugin.Ivysilani/`),
   jméno „iVysílání", GUID `50151328-7f55-42fe-b7d2-a789212ef6fc`, verze
   `1.0.0.0`, `build.yaml` + `meta.json` hotové.
2. **Konfigurace**: stránka v Dashboard → Plugins → iVysílání s textarea
   (`ShowUrls`, jedna URL na řádek) a číselníkem TTL cache v hodinách
   (`CacheTtlHours`, 1–24, default 3). Uloženo přes standardní
   `BasePluginConfiguration`/`IPluginConfiguration`.
3. **Channel obsah**: kořen channelu vrací složku na pořad
   (`ChannelFolderType.Series`), uvnitř epizody (`ChannelItemType.Media`,
   `ChannelMediaContentType.Episode`). Podporuje jak URL s konkrétním dílem,
   tak holou URL pořadu (`CtApiClient.ParseShowId`, regex na
   `/porady/{id}(-|/|$)`) — ověřeno testem pro obě varianty.
   Epizody jsou řazené podle rozpoznaného pořadí (`Epizoda N/M` v titulku),
   dál podle data vysílání, dál podle názvu. Epizody, které ČT nemá
   momentálně oznacené jako `playable`, se vůbec nezobrazí (filtr přímo
   v `IvysilaniChannel.GetShowEpisodesAsync`/`GetRootFoldersAsync`).
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

Výsledné artefakty v `artifacts/`:

| Soubor | Velikost | SHA-256 |
|---|---|---|
| `Jellyfin.Plugin.Ivysilani.dll` | 55 296 B | `a5a662f5357b7334a288baef5d7179d82b31bd0fc69f728523a30d284eac39c4` |
| `meta.json` | 741 B | — |

Build je reprodukovatelný — druhý `dotnet publish` z čistého stromu (po
smazání `bin/`/`obj/`) vyprodukoval bitově identický DLL (stejný SHA-256).

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

Spuštěno 2026-10-03 19:14:14 +02:00, proti živému `ceskatelevize.cz` /
`api.ceskatelevize.cz` (žádný mock, žádné nahrané fixture). Plný výstup je
v `TestHarness/test-run-output.log`, reprodukovaný zde celý:

```
=== iVysílání plugin — test harness proti reálnému ČT API ===
Spuštěno: 2026-10-03 19:14:14 +02:00

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
    HLS master URL (prvních 140 znaků): https://ivys-nw-cdn.o2tv.cz/cdn/uri/get/?token=aaf635f43dbf57f5b1898d507ef95f1cd1c2f69e&userId=e3e5778d-4ec9-475b-b20f-d7013ff07da1&contentI…
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
    HLS master URL (prvních 140 znaků): https://ivys-nw-cdn.o2tv.cz/cdn/uri/get/?token=6f20db2a4062607cf10fa500d273869bdcd8f96a&userId=951bd874-b280-4ca4-a6cf-d71d1eafdb59&contentI…
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
    HLS master URL (prvních 140 znaků): https://ivys-nw-cdn.o2tv.cz/cdn/uri/get/?token=b10e3e44e4e306f6f2403460ad908d41303b921e&userId=c00bae5a-dc50-45f5-8f45-c72d3439afa2&contentI…
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
    HLS master URL (prvních 140 znaků): https://ivys-nw-cdn.o2tv.cz/cdn/uri/get/?token=37f9f5452c58ec770eda492ea04265a915d9cf61&userId=5da9431f-2107-4833-aaf8-8f6f14e32bca&contentI…
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
    HLS master URL (prvních 140 znaků): https://ivys-nw-cdn.o2tv.cz/cdn/uri/get/?token=57d1f1c5b7077ada19744fdff66b903b02e32a7f&userId=75e04005-4ba0-4d4f-a7fa-615cec62198b&contentI…
    Playlist fetch: OK, 2019 bajtů, #EXTM3U header = True

=== Shrnutí ===
VŠECHNY TESTY PROŠLY (0 chyb).
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
