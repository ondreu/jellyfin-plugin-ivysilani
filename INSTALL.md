# Instalace

## Co je potřeba

- běžící Jellyfin12.1 (kontejner `jellyfin/jellyfin`)
- dva soubory z [release](../../releases): `Jellyfin.Plugin.Ivysilani.dll`
  a `meta.json`

## Kam s nimi

Jellyfin čte pluginy ze složky `<config>/plugins/`. Pod Dockerem je to
nejčastěji něco jako `/config/plugins/`, na NASu s bindem `/volume1/jellyfin/`
to pak vychází na:

```
/volume1/jellyfin/iVysilani/Jellyfin.Plugin.Ivysilani.dll
/volume1/jellyfin/iVysilani/meta.json
```

Název složky je jedno — Jellyfin projde každou podsložku a manifest si přečte
z `meta.json`. Jen se vyvaruj diakritice v názvu, na souborových systémech
NASu to někdy dělá neplechu. Jinak se nic nerozbaluje ani neinstaluje, plugin
nemá žádné externí závislosti kromě toho, co už Jellyfin obsahuje.

## Restart

Pluginy se čtou při startu procesu, takže je potřeba **restartovat celý
kontejner** (ne jen reload konfigurace):

```bash
docker restart jellyfin
```

nebo „Restart" přes Synology Container Manager / Portainer. Trvá to pár
sekund. Žádné migrace ani jiné zásahy do dat neprobíhají.

## Ověření

1. **Dashboard → Plugins** — měl by tam být **iVysílání**, stav *Active*,
   verze odpovídající staženému release. Pokud je *Malfunctioned* nebo
   *NotSupported*, server hlásí jinou verzi API než12.1 — viz řešení níže.
2. V menu se objeví **Channels** → iVysílání. (U některých verzí webu je
   položka Channels schovaná; dá se zapnout v nastavení uživatele.)
3. Otevři iVysílání — mělo by se ukázat14 kategorií + složka Oblíbené.
   Rozklikni kategorii → seznam pořadů, pořad → jeho sezóny a díly.
4. Přehrávání: díl by se měl spustit rovnou, bez transkódování (pokud to
   klient a síť zvládnou).

## Nastavení — Oblíbené

Dashboard → **Plugins → iVysílání** (ozubené kolečko) → textové pole na
seznam URL, jeden odkaz na řádek:

```
https://www.ceskatelevize.cz/porady/16208367858-na-telo/224512120130001
```

Uložit a pořad se objeví ve složce Oblíbené.

## Aktualizace a odebrání

- **Aktualizace**: přepiš DLL (a `meta.json`) ve stejné složce a restartuj
  kontejner. Nastavení (URL Oblíbených) leží jinde
  (`<config>/data/plugins/configurations/`) a přepisem DLL se neztratí.
- **Odebrání**: smaž složku pluginu a restartuj kontejner.

## Když se plugin nenačte

- Zkontroluj verzi serveru: `/System/Info/Public` musí hlásit `12.1.0`.
  Jiná verze = nutné přebuildovat proti odpovídajícím balíčkům
  `Jellyfin.Controller`/`Jellyfin.Model` (jiný `targetAbi`, možná i jiný
  .NET).
- Zkontroluj, že soubory ve složce pluginu jsou čitelné pro uživatele, pod
  kterým kontejner běží.
- Logy: `<config>/log/jellyfin*.log`, hledej `iVysílání` nebo
  `Ivysilani`. Chyby pluginu jsou tam se jménem pluginu.

## Chyby a nápady

Piš do [GitHub Issues](../../issues) — ideálně s verzí serveru, verzí
pluginu a výřezem logu.
