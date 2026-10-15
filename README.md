# Valheim Symbiosis

Valheim mod suite containing Workers Core integration, creature symbiosis forms, first-person support and automatic release/update tooling.

## Repository layout

- `src/Mount` - Symbiosis runtime, movement, combat and cameras
- `src/DrakeForm` - creature necklaces / capture / form activation
- `src/Ego` - player first-person integration
- `src/Updater` - automatic GitHub release updater
- `release` - GitHub setup, standalone installer builder and final-release publisher
- `docs` - current release documentation and archived development test plans

## Development install

Run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Setup.ps1
```

## Final release

After a version has been tested, run:

```text
release\Publish-Final.bat
```

The release tool builds the standalone installer, calculates SHA-256 and uploads the final ZIP to GitHub Releases.

## Player requirements

The standalone package includes our own Symbiosis modules and Workers Core. External requirements remain BepInEx/Thunderstore and Jotunn.
