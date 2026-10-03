[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/B7K822EW68)

# iVysílání pro Jellyfin

Channel plugin, který do Jellyfinu přidá katalog **iVysílání České televize** —
seriály, filmy, dokumenty, dětské pořady, zpravodajství. Procházíš ho přímo
v Jellyfinu a přehrává se **ze serverů ČT**, bez jakéhokoli stahování.

> Neoficiální projekt, nijak nespojený s Českou televizí ani s Jellyfin
> Projectem. Plugin používá veřejné endpointy přehrávače iVysílání. Když je ČT
> změní, může přestat fungovat — stejně jako kdysi změnila API, kvůli kterému
> přestal fungovat yt-dlp.

## Co umí

- **Celý katalog iVysílání** — kořen channelu nabídne14 kategorií (Seriály,
  Filmy, Dokumenty, Pro děti, Sport, …) a k tomu složku **Oblíbené**.
- **Oblíbené podle tebe** — v nastavení pluginu necháš seznam URL na pořady
  (jeden na řádek) a ty se objeví ve složce Oblíbené. Hodí se pro pořady,
  které chceš mít po ruce, aniž bys procházel celý katalog.
- **Přehrávání přímo z ČT** — HLS stream až1080p se vyřeší až v okamžiku
  přehrání, takže vždy dostaneš čerstvou, platnou adresu. Přehrává se rovnou,
  Jellyfin zbytečně nekóduje.
- **Obsah mizí s iVysíláním** — jakmile ČT díl přestane nabízet k přehrání,
  přestane se v knihovně zobrazovat (čte se z krátkodobé cache, výchozí3 hodiny).
- Na disku leží jen metadata. Žádná videa se nestahují a neukládají.

## Jak je channel uspořádaný

```
iVysílání
├── Seriály / Filmy / Dokumenty / …      (14 kategorií)
├── Oblíbené                             (tvoje URL z nastavení)
└── uvnitř pořadu:
    Prasátko Peppa
    ├── Season 2
    │   ├── Bruslení
    │   └── …
    └── Season 3
```

Ta vrstva sezón tam není pro parádu. Jellyfin si při přehrávání dílu dohledá
kompletní seznam dílů seriálu přes endpoint `/Shows/{id}/Episodes` a ten
umí projít **jen přes sezónní složky** — bez nich vrací prázdný seznam, klient
z toho usoudí, že nemá co přehrát, a ukončí přehrávání dřív, než vůbec požádá
o zdroj (hláška „Nelze najít platný zdroj médií k přehrání"). Sezónní složky
navíc v Jellyfinu vypadají správně — epizody mají číslo sezóny i dílu,
detail stránky ukazuje „More from Season2".

Díly se v sezónní složce materializují až ve chvíli, kdy ji otevřeš — to je
běžné chování channel pluginů. Prostě procházej složkami normálně, na nic
se nemusíš ptát.

## Instalace

1. Stáhni `Jellyfin.Plugin.Ivysilani.dll` a `meta.json` z
   [posledního release](../../releases).
2. Vlož je do složky pluginů — u běžného Dockeru třeba
   `/volume1/jellyfin/iVysilani/`. Jméno složky je libovolné, důležité jsou
   jména obou souborů.
3. Restartuj Jellyfin (pluginy se čtou při startu).
4. Dashboard → **Plugins → iVysílání** → nastav URL Oblíbených → Uložit.
5. V menu se objeví **Channels** → iVysílání.

Podrobnosti, aktualizace a co dělat, když se plugin nenačte, jsou v
[`INSTALL.md`](INSTALL.md).

## Sestavení ze zdrojů

```bash
# potřebuješ .NET10 SDK (plugin cílí na Jellyfin12.1 / net10.0)
dotnet build -c Release Jellyfin.Plugin.Ivysilani
dotnet publish -c Release -o artifacts Jellyfin.Plugin.Ivysilani
```

Testovací harness umí zkontrolovat parsování kategorií, stránkování i resolve
streamů proti **reálnému ČT API** (žádné nasimulované odpovědi):

```bash
cd TestHarness && dotnet run -c Release
```

## Co zatím nefunguje

- **Titulky.** ČT je v streamu nabízí a plugin umí zjistit jejich adresy,
  ale Jellyfin12.1 má v téhle cestě chybu (spadne na `NullReferenceException`
  při žádosti o titulky) a přítomnost titulkové stopy navíc navede klienta na
  přehrávací variantu, která u tohoto zdroje končí chybou500. Necháváme tedy
  zatím titulky být — spolehlivé přehrávání je důležitější. Až to Jellyfin
  opraví, vrátíme je.
- Seznam dílů se čte z dat stránky pořadu. U pořadů s desítkami dílů to
  funguje (ověřeno na stovce+), extrémní případy se stovkami dílů jsme
  netestovali.
- Sezónní číslování vychází z údajů ČT. U starších pořadů, kde ČT žádná
  čísla nemá, díly spadnou do jedné složky bez čísla.
- Channel nemá vlastní ikonu a PEGI hodnocení je placeholder.

Jak je plugin uvnitř pospojovaný a na jaké drobnosti si dát při údržbě pozor,
je v [`POZNAMKY.md`](POZNAMKY.md).

## Disclaimer

- Plugin oficiálně **neoficiální** — žádná afilace s ČT ani s Jellyfin.
- Používá veřejné neautentizované endpointy iVysílání; mohou se kdykoli
  změnit.
- Plugin nic nestahuje ani neukládá, jen streamuje pro osobní přehrávání.
  Respektuj podmínky používání iVysílání.

## License

[GPLv3](LICENSE)
