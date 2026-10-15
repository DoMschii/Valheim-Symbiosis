# Workers Core v0.6.0 RC1 release gate

Before stable promotion:

1. Build and start through the normal Thunderstore profile.
2. Load the existing large save.
3. Let the base run for at least 10-15 minutes and watch for periodic frame freezes.
4. Smoke-test Garden/Harvester, Sorter + Ground Hauler + FieldDepot, one production role,
   one resting worker, Fuling diplomacy/recruitment and save/reload.
5. Inspect BepInEx logs for repeating exceptions.
6. Continue normal long-session play. Bugs that only emerge after hours are valid RC findings.
7. Publish `stable.json` only after critical regressions are resolved.

Navigator v5 is intentionally unchanged during this gate.
