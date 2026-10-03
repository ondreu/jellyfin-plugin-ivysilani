[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/B7K822EW68)

# Jellyfin.Plugin.Ivysilani

Channel plugin pro Jellyfin, který do knihovny přidá **vybrané pořady z České
televize (iVysílání)** a přehrává je **přímo ze serverů ČT** — žádné stahování,
žádné DRM, žádné přihlašování.

> Neoficiální projekt, nespojený s Českou televizí. Plugin jen používá veřejné
> endpointy přehrávače iVysílání a **pouze streamuje** (nic neukládá na disk).

## Co umí

- **Seznam tvých pořadů** — v nastavení pluginu vložíš URL na pořady (jeden na
  řádek). Žádný celý katalog, jen to, co chceš.
- **Složka → epizody** — každý pořad se v channelu objeví jako složka
  s epizodami (název, popis, obrázek z ČT, délka, datum vysílání).
- **Přehrávání přímo z ČT** — resolve na podepsanou HLS URL (až **1080p**) až
  těsně před přehráním; direct play i transkód jsou ověřené.
- **Titulky** — externí `.vtt` stopa, pokud ji ČT nabízí.
- **Mizí, když zmizí z iVysílání** — epizoda se přestane zobrazovat, jakmile ČT
  přestane označovat díl jako přehratelný (cache 1–24 h, default 3 h).
- **Žádné stažené médium** — na disku končí jen metadata (JSON/HTML).

## Instalace

1. Stáhni `Jellyfin.Plugin.Ivysilani.dll` + `meta.json` z
   [posledního release](../../releases) (nebo si postav zdrojáky).
2. Vlož do `<config>/plugins/iVysilani/` (u Dockeru třeba
   `/volume1/jellyfin/iVysilani/`).
3. **Restartuj Jellyfin server.**
4. Dashboard → **Plugins → iVysílání** → vlož URL pořadů → **Uložit**.
5. V menu se objeví sekce **Channels** → iVysílání.

Detaily (ověření, aktualizace, odebrání, co dělat když se plugin nenačte):
viz [`INSTALL.md`](INSTALL.md).

## Sestavení ze zdrojů

```bash
# vyžaduje .NET 10 SDK (Jellyfin 12.1 API cílí net10.0)
dotnet build -c Release Jellyfin.Plugin.Ivysilani
dotnet publish -c Release -o artifacts Jellyfin.Plugin.Ivysilani
```

Test proti **reálnému ČT API** (žádné mocky):

```bash
cd TestHarness && dotnet run -c Release
```

Poslední běh: `VŠECHNY TESTY PROŠLY (0 chyb)` — viz
[`REPORT.md`](REPORT.md), celý výstup v
`TestHarness/test-run-output.log`.

## Známá omezení

- Seznam epizod se čte z `__NEXT_DATA__` stránky pořadu — u **dlouhých**
  pořadů (desítky/stovky dílů) není ověřeno, zda ČT stránku nepaginuje.
- Season/episode číslování je heuristika (vzor `Epizoda N/M` v titulku);
  jinak řazení podle data. Zatím plochá složka bez sezónních podsložek.
- Channel nemá vlastní ikonu, PEGI hodnocení je placeholder.

Kompletní přehled v [`REPORT.md`](REPORT.md) — sekce „Co je omezené".

## Disclaimer

- Oficiálně **neoficiální**, žádná afilace s ČT ani s Jellyfin Project.
- Používá veřejné, neautentizované endpointy přehrávače iVysílání; mohou se
  kdykoli změnit (stejně jako to dřív udělali s yt-dlp).
- Plugin **nestahuje ani neukládá obsah**, jen streamuje pro vlastní
  přehrávání. Respektuj podmínky používání iVysílání.

## License

[GPLv3](LICENSE)
