# Valheim Symbiosis

Gameplay repository for Valheim Symbiosis.

This repository intentionally contains **only the Valheim gameplay modules**. The automatic updater and GitHub/release automation are maintained separately so their versions and lifecycles do not have to follow gameplay releases.

## Repository layout

- `src/Mount` - symbiosis runtime, movement, combat and cameras
- `src/DrakeForm` - creature necklaces, capture and form activation
- `src/Ego` - player first-person integration
- `docs` - release documentation and archived development notes

## Development install

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Setup.ps1
```

## Separate components

- **Valheim Symbiosis Updater** - separate BepInEx plugin and versioning
- **Valheim Symbiosis ReleaseTools** - local developer tooling for GitHub publishing and installer creation; not shipped as gameplay source

## Player requirements

The final standalone package can bundle our own Workers Core, Mount, DrakeForm, Ego and Updater binaries. External requirements remain BepInEx/Thunderstore and Jotunn.
