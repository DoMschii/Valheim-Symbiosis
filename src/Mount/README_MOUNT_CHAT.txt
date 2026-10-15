VALHEIM WORKERS MOUNT - SEPARATE DEVELOPMENT RULE

This ZIP is the only source package the Mount/Symbiosis chat should edit.
Do NOT copy the Valheim_Workers core source into this project and do NOT build a replacement Valheim_Workers.dll here.

Assembly:
  Valheim_Workers_Mount.dll
Plugin GUID:
  germa.valheim.workers.mount
Hard dependency:
  germa.valheim.workers / Valheim_Workers.dll >= 0.5.436

Owned by this add-on:
- Drake necklace symbiosis / possession controls
- First/third person mount camera
- local + multiplayer remote player visual handoff while in symbiosis
- dismount / death / portal / world-exit / crash-recovery safety
- Symbiosis activation is handled by the bound Drake necklace in Valheim_Workers_DrakeForm; Mount owns flight/camera/combat runtime
- symbiosis player damage safety

Owned by Workers core, do not edit from the Mount chat:
- Worker AI/scheduler/jobs
- Worker navigation implementation
- AirHauler logistics
- ZDO worker schema
- recruitment, homes, workzones, storage

The core exposes one stable WorkerExternalControlBridge. Mount registers there at runtime; future Mount work should remain inside this project.
