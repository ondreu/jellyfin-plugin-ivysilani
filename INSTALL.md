# INSTALL.md — instalace pluginu iVysílání

Tento dokument je pro **Iris** (má přístup k produkčnímu Jellyfin serveru/NASu).
Agent, který plugin stavěl, server **neměnil ani nerestartoval** — pouze
připravil soubory v `artifacts/`.

## Co nakopírovat

Z `artifacts/` v tomto repozitáři:

- `Jellyfin.Plugin.Ivysilani.dll` (verze 1.1.0.0, 76 288 B, sha256
  `bf935e96f24a0afbcd26d66b063859329e5475d39291f826f90ef45bc06b0e9f`)
- `meta.json`

## Kam

Jellyfin na cílovém serveru čte pluginy z `/config/plugins/<libovolný název
složky>/`, což je na NASu bindnuté na `/volume1/jellyfin/`. Název složky je
Jellyfinu lhostejný (každou podsložku prohledá a manifest si přečte z
`meta.json`) — doporučuji ale bezdiakritický název, ať se nic nepoplete na
souborovém systému NASu:

```
/volume1/jellyfin/iVysilani/Jellyfin.Plugin.Ivysilani.dll
/volume1/jellyfin/iVysilani/meta.json
```

Tedy:

1. Vytvoř na NASu složku `/volume1/jellyfin/iVysilani/`.
2. Zkopíruj do ní oba soubory výše (zachovej přesně tato jména souborů).
3. Nic dalšího se nerozbaluje/instaluje — žádné další DLL nejsou potřeba,
   plugin nemá žádné externí závislosti mimo to, co už Jellyfin server sám
   obsahuje (`Jellyfin.Controller`/`Jellyfin.Model` 12.1.0 jsou referencované
   jen "compile-time", runtime verze dodává server).

## Restart

Jellyfin načítá pluginy při startu procesu, takže je potřeba **restartovat
celý jellyfin/jellyfin kontejner** (ne jen reload konfigurace):

```
docker restart <jméno-kontejneru-jellyfin>
```

nebo ekvivalentně přes Synology Container Manager / Portainer GUI — "Restart"
na daném kontejneru. Žádný jiný zásah (DB migrace, apod.) není potřeba.

## Ověření po restartu

1. **Dashboard → Plugins** → měl by se objevit **iVysílání**, stav *Active*,
   verze 1.1.0.0. Pokud se objeví jako *Malfunctioned*/*NotSupported*,
   zkontroluj v Jellyfin logu (`/config/log/*.log`) chybovou hlášku — s
   `targetAbi: 12.1.0.0` by to na serveru hlásícím `Version 12.1.0` mělo sedět
   přesně.
2. V levém menu Jellyfinu by se měla objevit sekce **Channels** (u některých
   verzí webového klienta je skrytá v Dashboardu pod "Channels" nebo se musí
   v nastavení uživatele zapnout zobrazení "Channels" v domovské obrazovce —
   **toto jsem nemohl ověřit naživo, protože nemám přístup k běžícímu
   serveru**, zkontroluj to prosím po restartu a dej mi vědět, pokud se
   nezobrazí).
3. Otevři channel "iVysílání" — kořen by měl nabídnout **14 kategorií**
   (Seriály, Filmy, Dokumenty, ...) + složku **"Oblíbené"** (prázdná, dokud
   do konfigurace nevložíš vlastní URL). Rozklikni libovolnou kategorii →
   měl by se zobrazit stránkovaný seznam pořadů dané kategorie (u velkých
   kategorií, např. Dokumenty, je to přes 2600 pořadů — pokud klient
   nenačítá další stránky automaticky, je to známé omezení, viz REPORT.md
   sekce "Omezení v1.1.0"). Rozklikni pořad → měly by se zobrazit jeho díly.
4. (Volitelně) **Dashboard → Plugins → iVysílání** (ikona ozubeného
   kolečka) → konfigurační stránka s textarea pro seznam URL do složky
   "Oblíbené". Vlož tam např.:
   ```
   https://www.ceskatelevize.cz/porady/16208367858-na-telo/224512120130001
   ```
   a klikni **Uložit** — pořad by se pak objevil ve složce "Oblíbené".
5. Přehrání dílu (z kategorie i z Oblíbených) by mělo jít přímo (HLS direct
   play) bez nutnosti transkódování.

## Odebrání / aktualizace

- **Odebrání**: smaž `/volume1/jellyfin/iVysilani/` a restartuj kontejner.
- **Aktualizace** (nová verze DLL): přepiš `Jellyfin.Plugin.Ivysilani.dll` (a
  `meta.json`, pokud se verze/popis změnily) ve stejné složce a restartuj
  kontejner. Konfigurace (seznam URL) je uložena samostatně Jellyfinem
  (`/config/data/plugins/configurations/`) a při aktualizaci DLL se neztratí.

## Co dělat, když se plugin nenačte

- Zkontroluj, že server skutečně běží na `12.1.0` (`/System/Info/Public`) —
  pro jinou verzi by bylo nutné přebuildovat proti odpovídajícím
  `Jellyfin.Controller`/`Jellyfin.Model` NuGet balíčkům (jiný `targetAbi` i
  případně jiný `net` TFM, viz REPORT.md).
- Zkontroluj oprávnění souborů v `/volume1/jellyfin/iVysilani/` (čitelné pro
  uživatele, pod kterým běží kontejner).
- Logy: `/config/log/jellyfinxxxx.log` v kontejneru, hledej `iVysílání` nebo
  `Ivysilani`.
