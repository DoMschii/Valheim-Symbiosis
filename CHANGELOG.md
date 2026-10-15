# Changelog

## Workers Core 0.6.0 RC1

- Promotes the optimized/final-cleanup Workers code line from 0.5.462 to numeric version 0.6.0.
- Fifteen performance/stability passes completed before this release candidate.
- Major recurring world scans, polling loops and avoidable GC hotpaths were moved to lifecycle indexes, caches, bounded background ticks or NonAlloc physics where safe.
- Final cleanup retained only intentional bootstrap, repair, compatibility, manual/debug and one-time discovery paths.
- Navigator v5 remains frozen.
- No new gameplay feature was intentionally added during the RC promotion.
- Long-session testing remains the release gate before `stable.json` is published.

## 4.5.2 - Ego backpedal facing stabilizer

- Fixes intermittent 180-degree body-facing flicker while holding Backward/S, especially during combat and building.
- Backward intent is sampled from both Valheim ZInput and the physical S key, with a short grace latch so temporary input consumption cannot release the camera-facing lock for one frame.
- W cancels the latch immediately.
- Symbiosis Third-Person movement/facing remains untouched and owned by Workers Mount.

## Repository architecture split

- Gameplay source is now isolated from the updater and release/GitHub automation.
- Updater uses independent versioning.
- Release tools are no longer part of the gameplay repository or player source package.
- No gameplay changes.

## 4.5.1 - Repository cleanup / release tooling

- Clean repository structure (`src`, `release`, `docs`).
- Historical test plans and changelogs moved out of the repository root.
- Release scripts updated for the new structure.
- Standalone builder now derives suite version from the Mount project instead of hardcoding it.
- Added repository sync helper for the already-created GitHub repository.
- No gameplay changes.

## 4.5.0 - Auto update / release infrastructure

- Added GitHub Releases publishing workflow.
- Added standalone updater module and installer packaging.
