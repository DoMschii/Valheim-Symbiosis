VALHEIM SYMBIOSIS - GITHUB AUTO RELEASE
=======================================

EINMALIG:
1. Setup-GitHub.bat starten.
2. Bei GitHub anmelden.
3. Owner/Repository bestaetigen.
   Das Script kann das Repository automatisch erstellen.

NACH EINER FINAL GETESTETEN VERSION:
1. Publish-Final.bat starten.
2. Mit JA bestaetigen.
3. Das Script:
   - baut/installiert die aktuelle Source,
   - erzeugt den Standalone Installer,
   - erzeugt SHA256,
   - erstellt automatisch den GitHub Release,
   - laedt ZIP + SHA256 hoch.

AUTO-UPDATER:
- Valheim_Symbiosis_Updater.dll prueft beim Start GitHub Releases.
- Neue Stable-Versionen werden heruntergeladen.
- Die Installation erfolgt nach dem Beenden von Valheim, damit geladene DLLs nicht ersetzt werden muessen.
- Beim naechsten Start laeuft die neue Version.

Externe Voraussetzung fuer Spieler bleibt Jotunn + BepInEx/Thunderstore.
Workers Core und alle eigenen Module werden in unserem Release gebuendelt.
