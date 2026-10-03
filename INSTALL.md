# INSTALL.md — instalace pluginu iVysílání

Tento dokument je pro **Iris** (má přístup k produkčnímu Jellyfin serveru/NASu).
Agent, který plugin stavěl, server **neměnil ani nerestartoval** — pouze
připravil soubory v `artifacts/`.

## Co nakopírovat

Z `artifacts/` v tomto repozitáři:

- `Jellyfin.Plugin.Ivysilani.dll` (55 296 B, sha256
  `a5a662f5357b7334a288baef5d7179d82b31bd0fc69f728523a30d284eac39c4`)
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
   verze 1.0.0.0. Pokud se objeví jako *Malfunctioned*/*NotSupported*,
   zkontroluj v Jellyfin logu (`/config/log/*.log`) chybovou hlášku — s
   `targetAbi: 12.1.0.0` by to na serveru hlásícím `Version 12.1.0` mělo sedět
   přesně.
2. **Dashboard → Plugins → iVysílání** (ikona ozubeného kolečka) → otevře se
   konfigurační stránka s textarea pro seznam URL. Vlož tam alespoň jednu
   adresu pořadu, např.:
   ```
   https://www.ceskatelevize.cz/porady/16208367858-na-telo/224512120130001
   ```
   a klikni **Uložit**.
3. V levém menu Jellyfinu by se měla objevit sekce **Channels** (u některých
   verzí webového klienta je skrytá v Dashboardu pod "Channels" nebo se musí
   v nastavení uživatele zapnout zobrazení "Channels" v domovské obrazovce —
   **toto jsem nemohl ověřit naživo, protože nemám přístup k běžícímu
   serveru**, zkontroluj to prosím po restartu a dej mi vědět, pokud se
   nezobrazí).
4. V channelu "iVysílání" by se měla objevit složka `Na tělo` a v ní 5 dílů.
   Přehrání by mělo jít přímo (HLS direct play) bez nutnosti transkódování.

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
