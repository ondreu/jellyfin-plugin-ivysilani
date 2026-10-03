# SPEC — Jellyfin plugin „iVysílání" (ČT VOD jako knihovna)

## Cíl
Jellyfin **channel plugin** (C#), který v knihovně zobrazí **vybrané** pořady/filmy
z iVysílání (ceskatelevize.cz) a přehrává je **přímo ze serverů ČT (streamování,
žádné stahování)**. Obsah se v knihovně objeví, dokud je na iVysílání; když ČT
obsah odstraní, položka zmizí.

Uživatel si konfiguruje **seznam odkazů na pořady** (většina katalogu ho
nezajímá) → plugin vytvoří složku na pořad → v ní epizody → přehrávání.

## Cílový server (nasazený, reálný)
- Jellyfin image `jellyfin/jellyfin:latest`, `/System/Info/Public` → **Version 12.1.0**
- Pluginy se instalují do `/config/plugins` (na NASu je to bind `/volume1/jellyfin/`),
  tedy složka `/volume1/jellyfin/<NazevPluginu>/<dll>.dll`
- **Server NESMÍŠ měnit ani restartovat** — stavíš jen kód; instalaci a restart
  dělá Iris. Odevzdej hotový build + přesné instalační kroky.
- Interface `IChannel` v Jellyfin existuje: `MediaBrowser.Controller/Channels/IChannel.cs`,
  API `Jellyfin.Api/Controllers/ChannelsController.cs`. Ověř aktuální API pro
  verzi serveru (viz `jellyfin/jellyfin` repo, releasy, plugin template).

## Reference k architektuře
- `kingschnulli/jellyfin-youtube-plugin` — channel plugin, který streamuje YouTube;
  přesně stejný princip (resolve stream URL až při přehrávání). Studuj jeho strukturu.
- `jellyfin/jellyfin-plugin-template` (ověř přesný název) + JPRM (`build.yaml`) —
  standardní scaffold pluginů Jellyfinu.
- Channel plugin = `IChannel` + `IHasChanges`/`HasUpdates` (nebo obdoba) + třída
  `IChannelItem` provider; přehrávání přes `MediaSourceInfo` s `Path` = HLS URL.

## Data a API (OVĚŘENO 2026-10-03, funguje bez přihlášení)

### 1) Stránka pořadu (zdroj seznamu epizod)
`https://www.ceskatelevize.cz/porady/{SHOW_ID}/{EPISODE_ID}` (i bez konkrétní
epizody jde o stránku pořadu). V HTML je `__NEXT_DATA__` JSON:
- `props.pageProps.data.mediaMeta` — metadata pořadu (id, title…)
- `props.apolloState` — entity:
  - `Show:{SHOW_ID}` (např. `16208367858`), má `programmeId`
  - `EpisodePreview:{EPISODE_ID}` — `id`, `showId`, **seznam epizod pořadu**
    (prozkoumej skutečná pole — season/episode číslo, název, datum, dostupnost)
  - `MediumMeta:{EPISODE_ID}`
- Příklad: `/porady/16208367858-na-telo/224512120130001`
  → show id `16208367858`, episode id `224512120130001`
- Vždy posílej browser User-Agent + `Referer: https://www.ceskatelevize.cz/`.

### 2) Resolve streamu (jádro přehrávání)
```
GET https://api.ceskatelevize.cz/video/v1/playlist-vod/v1/stream-data/media/external/{EPISODE_ID}
    ?canPlayDrm=false&streamType=hls&quality=web&maxQualityCount=5
    &origin=ivysilani&client=iVysilaniWeb&clientVersion=0.37.6
Headers: User-Agent (browser), Referer/Origin ceskatelevize.cz, Accept: application/json
```
Odpověď (klíčová pole):
- `streams[0].url` — **HLS master playlist** (5 variant až 1080p/6,4 Mbps,
  AUDIO rendice: Czech default / Multiple languages / Czech (AD), SUBTITLES skupina)
- `streams[0].duration` (sekundy, např. 3217.92)
- `streams[0].subtitles[0].files[]` — `format`: `json` | `ttml` | `vtt` (vtt preferuj)
- `streams[0].availableQualities[]` — `quality` (180p…1080p), `codec`, `fps`
- `title`, `showTitle`, `episodeTitle`, `originalTitle`, `previewImageUrl`,
  `mediaType` ("video"), `duration`
- `playability` — `isPlayable…`, `playableInterval.playableFrom/playableTo`
  (`playableTo: null` = bez konce) → **položku zobrazuj jen pokud je playabilní**
- **Žádné DRM** (`drmOnly=false`, žádná license pole)

Ostatní tvary id (z bundle playeru, použij jen když se hodí):
`/stream-data/version/{id}`, `/stream-data/media/{id}`, `/stream-data/index/{id}`,
`/stream-data/bonus/BO-{id}`, `/stream-data/media/external/{id}` (+ `/unrestricted`).

### 3) Ověřená přehratelnost
- `ffmpeg -i {varianta} -c copy` → OK (h264+aac)
- I Jellyfinovsky výstupní režim (`-copyts -avoid_negative_ts disabled …`) nad tímhle
  HLS dává **video i zvuk zarovnané** (PTS v=1.440 / a=1.525) → **normální předání
  HLS URL jako MediaSource je bezpečné** (direct play i transcode).
- Headless testovací harness (konzolový projekt/Unit testy), který reálně zavolá
  API a ověří resolve + parsování stránky pořadu — **musíš doložit výstupem**.

## Rozsah (MVP, drž se ho)
1. **Plugin**: název `iVysílání`, překlad CS, `build.yaml`/manifest, verze 1.0.0.
2. **Konfigurace**: stránka nastavení pluginu — textarea se seznamem URL na pořady
   (jedna na řádek), uloží se přes `IPluginConfiguration`. Support URL tvarů
   `/porady/{id}-…` (pořad i dílčí epizoda — ber jako pořad).
3. **Channel obsah**: kořen channelu → složky podle pořadu → epizody (řazeny,
   season/episode čísla pokud je v datech najdeš; jinak datum/název).
   Položky bez dostupného streamu nezobrazuj.
4. **Metadata**: název (`episodeTitle`/`title`), popis/overview (klidně `showTitle`
   + titulek dílu), obrázek `previewImageUrl`, `RunTimeTicks` z `duration`.
5. **Přehrávání**: `MediaSourceInfo { Path = streams[0].url, Container = "hls",
   Protocol = Http, VideoCodec = h264, AudioCodec = aac, Bitrate dle varianty }`.
   Přidej externí titulky (`.vtt`) jako `MediaSource`/`MediaStream` Typ=External,
   pokud to API kanálu umožní jednoduše — jinak vynech a uveď to v reportu.
6. **Cache**: výsledky stránek/streamů cachej (TTL ~1–6 h), po vypršení refresuj;
   položky s `isPlayable=false` odstraň. Žádné stahování médií na disk.
7. **Chyby**: slušně loguj (ILogger), nikdy neshazuj server (try/catch, graceful).

## Softwarové požadavky na prostředí (Hermes box)
- `dotnet` **není nainstalovaný** → nainstaluj lokálně přes
  `https://dotnet.microsoft.com/download/dotnet/scripts/dotnet-install.sh`
  (user-space do `$HOME/.dotnet`, bez roota; přidej do PATH v rámci sezení).
  Cíli `net8.0` nebo co odpovídá Jellyfin 12.1 API balíčkům — ověř podle
  `jellyfin/jellyfin` releasů / NuGet `Jellyfin.Controller.*`.
- `git` je, internet je, `gh` CLI je na `/opt/data/.local/bin/gh` (GitHub API).
- Nestahuj cizí plugin repa do prostoru mimo `/opt/data/workspace/ivysilani-plugin/`.

## Výstup (povinný)
1. Zdrojáky + build v `/opt/data/workspace/ivysilani-plugin/` (git repo ok).
2. `artifacts/*.dll` (postavený plugin) + `INSTALL.md` (přesné kroky pro Iris:
   kam zkopírovat na NAS, co restartovat).
3. `REPORT.md`: co funguje, co omezeně, co chybí (fáze 2).
4. Dolož **reálný výstup** test harnessu (volání API, vypsané epizody, resolve streamu).
Neposílej hotový kód bez build logu.
