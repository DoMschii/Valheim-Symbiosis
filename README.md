# Valheim Symbiosis

Gameplay repository for **Valheim Workers / Symbiosis**.

The repository intentionally contains gameplay source only. The automatic preloader
updater and the developer-only GitHub release tooling use separate projects and
independent versioning.

## Current release candidate

**Workers Core v0.6.0 RC1** is published under the GitHub tag `v0.6.0-rc1`.

The Workers DLL reports numeric version `0.6.0`; `rc1` is a GitHub release-channel
label so BepInEx/System.Version compatibility stays clean.

The other gameplay modules retain their own versions:

- Workers Core: **0.6.0**
- Mount / Symbiosis runtime: **4.5.2**
- DrakeForm: **0.7.0**
- Ego integration: **1.6.50**
- Preloader Updater: separate repository, currently **2.0.0**

## Repository layout

- `src/WorkersCore` - jobs, workers, logistics, production, background simulation
- `src/Mount` - symbiosis runtime, movement, combat and cameras
- `src/DrakeForm` - creature necklaces, capture and form activation
- `src/Ego` - player first-person integration
- `docs` - release documentation and archived development notes
- `update` - release-channel manifests. `stable.json` is intentionally absent during RC1.

## Development install

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Setup.ps1
```

This builds/installs Workers Core first, followed by Mount, DrakeForm and Ego.

## Automatic update model

The separate **Valheim Symbiosis Updater** is a BepInEx 5 preloader patcher.
Its default channel is `stable`. During RC1 no `update/stable.json` is published,
so normal users are never silently moved to the release candidate.

`update/beta.json` is generated only when the RC GitHub release is successfully
published. Developers/testers can opt into it explicitly.

## External requirements

- Valheim
- BepInEx 5 / Thunderstore or r2modman profile
- Jotunn

## Release policy

RC releases are used for long-session testing because the mod is large enough that
some issues only surface after extended play. A stable manifest is published only
after the RC has passed the long-session smoke test.
