# Changelog

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
