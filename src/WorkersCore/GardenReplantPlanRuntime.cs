using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    internal readonly struct GardenReplantSlot
    {
        internal readonly int SlotId;
        internal readonly string HarvestPrefab;
        internal readonly Vector3 Position;
        internal readonly float RotationY;
        internal readonly bool PersistentPlan;
        internal readonly int FieldId;

        internal GardenReplantSlot(int slotId, string harvestPrefab, Vector3 position, float rotationY, bool persistentPlan = false, int fieldId = 0)
        {
            SlotId = slotId;
            HarvestPrefab = harvestPrefab ?? string.Empty;
            Position = position;
            RotationY = rotationY;
            PersistentPlan = persistentPlan;
            FieldId = Mathf.Max(0, fieldId);
        }
    }

    internal readonly struct GardenWildGrowthAuditSlot
    {
        internal readonly int SlotId;
        internal readonly string HarvestPrefab;
        internal readonly string ResourcePrefab;
        internal readonly Vector3 Position;
        internal readonly bool Active;

        internal GardenWildGrowthAuditSlot(int slotId, string harvestPrefab, string resourcePrefab, Vector3 position, bool active)
        {
            SlotId = slotId;
            HarvestPrefab = harvestPrefab ?? string.Empty;
            ResourcePrefab = resourcePrefab ?? string.Empty;
            Position = position;
            Active = active;
        }
    }

    internal readonly struct GardenPersistentVisualSlot
    {
        internal readonly string VisualKey;
        internal readonly int SlotId;
        internal readonly int FieldId;
        internal readonly string HarvestPrefab;
        internal readonly Vector3 Position;
        internal readonly float RotationY;

        internal GardenPersistentVisualSlot(string visualKey, int slotId, int fieldId, string harvestPrefab, Vector3 position, float rotationY)
        {
            VisualKey = visualKey ?? string.Empty;
            SlotId = slotId;
            FieldId = fieldId;
            HarvestPrefab = harvestPrefab ?? string.Empty;
            Position = position;
            RotationY = rotationY;
        }
    }

    internal readonly struct GardenPersistentPlanSelection
    {
        internal readonly string VisualKey;
        internal readonly int SlotId;
        internal readonly int FieldId;
        internal readonly string HarvestPrefab;
        internal readonly Vector3 Position;
        internal readonly float RotationY;
        internal readonly bool Active;
        internal readonly float Distance;

        internal GardenPersistentPlanSelection(string visualKey, int slotId, int fieldId, string harvestPrefab, Vector3 position,
            float rotationY, bool active, float distance)
        {
            VisualKey = visualKey ?? string.Empty;
            SlotId = slotId;
            FieldId = fieldId;
            HarvestPrefab = harvestPrefab ?? string.Empty;
            Position = position;
            RotationY = rotationY;
            Active = active;
            Distance = distance;
        }

        internal bool IsValid => SlotId > 0 && !string.IsNullOrEmpty(HarvestPrefab);
    }

    /// <summary>
    /// Persistent replant queue stored on the work flag ZDO. Gatherers add the exact mature crop
    /// position after a successful harvest; Gardeners consume the slot only after a real replacement
    /// plant was spawned. This survives save/reload and keeps harvesting and planting decoupled.
    /// </summary>
    internal static class GardenReplantPlanRuntime
    {
        private const int MaxSlots = 512;
        private const string NextIdKey = "vw_garden_plan_next_id";
        private const string NextFieldIdKey = "vw_garden_plan_next_field_id";
        private const string CountKey = "vw_garden_plan_count";
        private const string WildGrowthLifecycleMigration329Key = "vw_garden_wildgrowth_lifecycle_329";
        private static readonly MethodInfo ClaimOwnershipMethod = typeof(ZNetView).GetMethod(
            "ClaimOwnership", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        private static readonly List<WorkFlagInteractable> LoadedGardenFlags = new List<WorkFlagInteractable>();
        private static readonly HashSet<ZDOID> WildGrowthRecoveryChecked = new HashSet<ZDOID>();
        private static readonly List<Character> PlanningWorkerScratch = new List<Character>(16);

        internal static int LastPendingCount { get; private set; }
        internal static string LastStatus { get; private set; } = "idle";

        // v0.5.387: a persistent slot must never pull a worker outside the Workzone that owns
        // the worker. Old planner builds could leave a slot on the wrong WorkFlag when zones
        // overlapped or were moved/resized. Treat those records as stale for this worker instead
        // of handing Navigator a 100m+ target. The slot itself is preserved so the player can
        // remove/re-plan it, or a worker assigned to the correct zone can still use it.
        internal static bool IsPositionInsideAssignedWorkArea(ZDO workerZdo, Vector3 position, float tolerance = 2f)
        {
            if (workerZdo == null || !WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius))
                return true;
            float allowed = Mathf.Max(5f, radius) + Mathf.Max(0f, tolerance);
            Vector3 delta = position - center;
            delta.y = 0f;
            return delta.sqrMagnitude <= allowed * allowed;
        }

        internal static bool EnqueueFromWorker(ZDO workerZdo, string harvestPrefab, Vector3 position, float rotationY)
        {
            if (workerZdo == null || string.IsNullOrEmpty(harvestPrefab)) return false;
            if (!TryGetFlagForHarvest(workerZdo, position, true, out _, out ZDO flagZdo)) { LastStatus = "garden-flag-unavailable"; return false; }

            // Permanent player-planned slots are re-activated by a later harvest instead of
            // creating a second queue entry at the same physical position.
            for (int i = 0; i < MaxSlots; i++)
            {
                bool persistent = flagZdo.GetBool(Key(i, "persistent"), false);
                if (!persistent) continue;
                Vector3 existing = ReadPosition(flagZdo, i);
                if ((existing - position).sqrMagnitude > 0.25f * 0.25f) continue;

                bool active = flagZdo.GetBool(Key(i, "active"), false);
                string plannedHarvest = flagZdo.GetString(Key(i, "harvest"), string.Empty);

                // v0.5.383: Workers WildGrowth is planted once and then behaves like natural flora.
                // Harvesting mushroom/thistle/dandelion/bush yield must NEVER reactivate its
                // permanent plan slot. This guard is intentionally in the queue itself as a second
                // line of defence even if a future Pickable/name compatibility path misclassifies
                // the harvested object before reaching EnqueueFromWorker.
                if (WorkersWildFloraRuntime.TryCanonicalizeLegacyIdentity(plannedHarvest, out _) ||
                    WorkersWildFloraRuntime.TryCanonicalizeLegacyIdentity(harvestPrefab, out _))
                {
                    flagZdo.Set(Key(i, "rot"), rotationY);
                    LastPendingCount = Mathf.Max(0, flagZdo.GetInt(CountKey, 0));
                    LastStatus = $"persistent-wildgrowth-respawn#{flagZdo.GetInt(Key(i, "id"), 0)}";
                    return false;
                }

                // A permanent player-planned slot keeps its original crop identity. Seed-production
                // plants are only a temporary production cycle; harvesting one must not convert a
                // planned carrot/onion/turnip slot into a permanent seed-crop slot.
                flagZdo.Set(Key(i, "rot"), rotationY);
                if (!active)
                {
                    flagZdo.Set(Key(i, "active"), true);
                    int restored = Mathf.Clamp(flagZdo.GetInt(CountKey, 0) + 1, 0, MaxSlots);
                    flagZdo.Set(CountKey, restored);
                    LastPendingCount = restored;
                    LastStatus = $"reactivated-planned#{flagZdo.GetInt(Key(i, "id"), 0)}";
                }
                else
                {
                    LastPendingCount = flagZdo.GetInt(CountKey, 0);
                    LastStatus = "duplicate-planned-slot";
                }
                return true;
            }

            // Duplicate protection: one physical crop position must only create one pending slot.
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "active"), false)) continue;
                Vector3 existing = ReadPosition(flagZdo, i);
                if ((existing - position).sqrMagnitude <= 0.25f * 0.25f)
                {
                    LastPendingCount = flagZdo.GetInt(CountKey, 0);
                    LastStatus = "duplicate-slot";
                    return true;
                }
            }

            // v0.3.46: Gardeners are plan-driven only. Harvesting an arbitrary/unplanned crop must
            // never create a new replant job. Only an already persistent player-planned ghost slot
            // may be re-activated above. This keeps the Shaman strictly on planned field positions.
            LastPendingCount = Mathf.Max(0, flagZdo.GetInt(CountKey, 0));
            LastStatus = $"harvest-unplanned-no-replant:{Normalize(harvestPrefab)}";
            return false;
        }

        internal static bool EnqueuePlannedSlot(long ownerPlayerId, string harvestPrefab, Vector3 position, float rotationY, out int slotId)
        {
            slotId = 0;
            if (string.IsNullOrEmpty(harvestPrefab)) { LastStatus = "planned-invalid"; return false; }
            if (!TryResolvePlanningWorkArea(ownerPlayerId, position, 30f, out ZNetView nview, out ZDO flagZdo, out string workAreaSource))
            {
                LastStatus = "planned-no-workzone";
                return false;
            }
            if (!EnsureWritable(nview)) { LastStatus = "planned-flag-readonly"; return false; }
            int fieldId = AllocateFieldId(flagZdo);
            bool stored = UpsertPlannedSlot(flagZdo, harvestPrefab, position, rotationY, fieldId, out slotId);
            if (stored)
            {
                int woke = WakeAssignedGardeners(flagZdo);
                LastStatus += $"; source={workAreaSource}; woke={woke}";
            }
            return stored;
        }

        /// <summary>
        /// Atomically stores one complete player-planned field/grid on a single Garden work flag.
        /// The whole grid is validated before any ZDO slot is changed, preventing half-created fields
        /// when part of the grid leaves the workzone or the persistent slot table is full.
        /// </summary>
        internal static bool EnqueuePlannedField(long ownerPlayerId, string harvestPrefab, IList<Vector3> positions,
            float rotationY, out int firstSlotId, out int plannedCount)
        {
            firstSlotId = 0;
            plannedCount = 0;
            if (string.IsNullOrEmpty(harvestPrefab) || positions == null || positions.Count < 1)
            {
                LastStatus = "planned-field-invalid";
                return false;
            }

            // v0.5.371: resolve the complete field as one atomic geometry against loaded
            // Workzones. The old path selected a Workzone from slot 1 and only afterwards tested
            // the remaining slots. In overlapping zones this could choose a smaller/nearer flag
            // and reject an otherwise completely valid field with outside-workzone:2/15.
            if (!TryResolvePlanningWorkAreaForField(ownerPlayerId, positions, 30f,
                    out ZNetView nview, out ZDO flagZdo, out string workAreaSource))
            {
                LastStatus = "planned-field-no-single-workzone";
                return false;
            }
            if (!EnsureWritable(nview)) { LastStatus = "planned-field-flag-readonly"; return false; }

            int fieldId = AllocateFieldId(flagZdo);

            int requiredNew = 0;
            for (int i = 0; i < positions.Count; i++)
                if (FindPersistentSlot(flagZdo, positions[i]) < 0)
                    requiredNew++;

            int freeCount = 0;
            for (int i = 0; i < MaxSlots; i++)
                if (!flagZdo.GetBool(Key(i, "active"), false) && !flagZdo.GetBool(Key(i, "persistent"), false))
                    freeCount++;

            if (requiredNew > freeCount)
            {
                LastStatus = $"planned-field-queue-full:need={requiredNew},free={freeCount}";
                return false;
            }

            for (int i = 0; i < positions.Count; i++)
            {
                if (!UpsertPlannedSlot(flagZdo, harvestPrefab, positions[i], rotationY, fieldId, out int slotId))
                {
                    // Capacity and workzone were prevalidated, so this should only happen if the
                    // ZDO changed concurrently. Report it rather than silently creating a partial field.
                    LastStatus = $"planned-field-write-failed:{i + 1}/{positions.Count}";
                    return false;
                }
                if (firstSlotId <= 0) firstSlotId = slotId;
                plannedCount++;
            }

            int wokeGardeners = WakeAssignedGardeners(flagZdo);
            LastStatus = $"planned-field:{plannedCount}:field#{fieldId}; source={workAreaSource}; woke={wokeGardeners}";
            return plannedCount == positions.Count && firstSlotId > 0;
        }

        private static bool UpsertPlannedSlot(ZDO flagZdo, string harvestPrefab, Vector3 position, float rotationY, int fieldId, out int slotId)
        {
            slotId = 0;
            if (flagZdo == null || string.IsNullOrEmpty(harvestPrefab)) { LastStatus = "planned-invalid"; return false; }

            int existingIndex = FindPersistentSlot(flagZdo, position);
            if (existingIndex >= 0)
            {
                slotId = flagZdo.GetInt(Key(existingIndex, "id"), 0);
                flagZdo.Set(Key(existingIndex, "harvest"), Normalize(harvestPrefab));
                flagZdo.Set(Key(existingIndex, "rot"), rotationY);
                if (flagZdo.GetInt(Key(existingIndex, "field"), 0) <= 0 && fieldId > 0)
                    flagZdo.Set(Key(existingIndex, "field"), fieldId);
                if (!flagZdo.GetBool(Key(existingIndex, "active"), false))
                {
                    flagZdo.Set(Key(existingIndex, "active"), true);
                    int count = Mathf.Clamp(flagZdo.GetInt(CountKey, 0) + 1, 0, MaxSlots);
                    flagZdo.Set(CountKey, count);
                    LastPendingCount = count;
                }
                else
                {
                    LastPendingCount = flagZdo.GetInt(CountKey, 0);
                }
                LastStatus = $"planned-updated#{slotId}";
                return slotId > 0;
            }

            int free = -1;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "active"), false) && !flagZdo.GetBool(Key(i, "persistent"), false))
                {
                    free = i;
                    break;
                }
            }
            if (free < 0) { LastStatus = "planned-queue-full"; return false; }

            slotId = Mathf.Max(1, flagZdo.GetInt(NextIdKey, 1));
            flagZdo.Set(NextIdKey, slotId == int.MaxValue ? 1 : slotId + 1);
            flagZdo.Set(Key(free, "active"), true);
            flagZdo.Set(Key(free, "persistent"), true);
            flagZdo.Set(Key(free, "id"), slotId);
            flagZdo.Set(Key(free, "harvest"), Normalize(harvestPrefab));
            flagZdo.Set(Key(free, "x"), position.x);
            flagZdo.Set(Key(free, "y"), position.y);
            flagZdo.Set(Key(free, "z"), position.z);
            flagZdo.Set(Key(free, "rot"), rotationY);
            flagZdo.Set(Key(free, "field"), Mathf.Max(0, fieldId));
            int pending = Mathf.Clamp(flagZdo.GetInt(CountKey, 0) + 1, 0, MaxSlots);
            flagZdo.Set(CountKey, pending);
            LastPendingCount = pending;
            LastStatus = $"planned#{slotId}:{Normalize(harvestPrefab)}";
            return true;
        }

        private static int FindPersistentSlot(ZDO flagZdo, Vector3 position)
        {
            if (flagZdo == null) return -1;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "persistent"), false)) continue;
                Vector3 existing = ReadPosition(flagZdo, i);
                if ((existing - position).sqrMagnitude <= 0.25f * 0.25f)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// v0.5.328 migration/self-heal for WildGrowth slots that old background simulation
        /// converted into virtual crops. Wild flora has no virtual Plant lifecycle: a slot must
        /// remain active until a real VW_Flora_* network Pickable is physically spawned.
        /// Re-activating here restores the saved ghost after reload and prevents double resource
        /// charging by dropping the obsolete background budget/debt state.
        /// </summary>
        /// <summary>
        /// v0.5.329 one-time reset into the slot-owned WildGrowth lifecycle. Older builds could
        /// mark a persistent WildGrowth plan inactive merely because any matching Pickable was
        /// seen near the point. Re-open those legacy slots once; new v0.5.329 completions remain
        /// inactive because the migration bit is written on the WorkFlag before normal work resumes.
        /// </summary>
        internal static int MigrateWildGrowthLifecycle329(ZDO flagZdo)
        {
            if (flagZdo == null || flagZdo.GetBool(WildGrowthLifecycleMigration329Key, false)) return 0;
            int reopened = 0;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "persistent"), false)) continue;
                string harvest = flagZdo.GetString(Key(i, "harvest"), string.Empty);
                if (!WorkersWildFloraRuntime.TryCanonicalizeLegacyIdentity(harvest, out WorkersWildFloraRuntime.Definition def)) continue;

                flagZdo.Set(Key(i, "harvest"), def.PrefabName);
                if (!flagZdo.GetBool(Key(i, "active"), false))
                {
                    flagZdo.Set(Key(i, "active"), true);
                    reopened++;
                }
                flagZdo.Set(Key(i, "bg_virtual"), false);
                flagZdo.Set(Key(i, "bg_state"), 0);
                flagZdo.Set(Key(i, "bg_ready"), 0L);
                flagZdo.Set(Key(i, "bg_resource_budget"), 0);
                flagZdo.Set(Key(i, "bg_resource_debt"), 0);
            }
            flagZdo.Set(WildGrowthLifecycleMigration329Key, true);
            RecountPending(flagZdo);
            if (reopened > 0) LastStatus = $"migrated-wildgrowth-329:{reopened}";
            return reopened;
        }

        internal static int RestoreBackgroundVirtualizedWildGrowth(ZDO flagZdo)
        {
            if (flagZdo == null) return 0;
            int restored = 0;
            bool touched = false;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "persistent"), false)) continue;
                string harvest = flagZdo.GetString(Key(i, "harvest"), string.Empty);
                if (!WorkersWildFloraRuntime.TryCanonicalizeLegacyIdentity(harvest, out WorkersWildFloraRuntime.Definition def)) continue;

                bool virtualized = flagZdo.GetBool(Key(i, "bg_virtual"), false) ||
                                   flagZdo.GetInt(Key(i, "bg_state"), 0) > 0;
                if (!virtualized) continue;

                bool wasActive = flagZdo.GetBool(Key(i, "active"), false);
                flagZdo.Set(Key(i, "harvest"), def.PrefabName);
                flagZdo.Set(Key(i, "active"), true);
                flagZdo.Set(Key(i, "bg_virtual"), false);
                flagZdo.Set(Key(i, "bg_state"), 0);
                flagZdo.Set(Key(i, "bg_ready"), 0L);
                flagZdo.Set(Key(i, "bg_resource_budget"), 0);
                flagZdo.Set(Key(i, "bg_resource_debt"), 0);
                touched = true;
                if (!wasActive) restored++;
            }

            if (touched)
            {
                RecountPending(flagZdo);
                LastStatus = restored > 0 ? $"restored-bg-wildgrowth:{restored}" : "cleared-bg-wildgrowth-state";
            }
            return restored;
        }

        internal static int CollectLoadedPersistentActiveSlots(long ownerPlayerId, List<GardenPersistentVisualSlot> slots, int maxSlots = 512)
        {
            if (slots == null) return 0;
            slots.Clear();
            int limit = Mathf.Clamp(maxSlots, 1, 1024);
            WorkFlagRegistry.CollectLoadedFlags(ownerPlayerId, WorkOrder.Garden, 30f, LoadedGardenFlags);

            for (int f = 0; f < LoadedGardenFlags.Count && slots.Count < limit; f++)
            {
                WorkFlagInteractable flag = LoadedGardenFlags[f];
                if (flag == null) continue;
                ZNetView view = flag.GetNetworkView();
                ZDO flagZdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (flagZdo == null) continue;

                // Repair v0.5.327-and-older offline/background virtualization before deciding
                // which saved persistent slots should be rendered as ghosts.
                if (view != null && view.IsValid() && EnsureWritable(view))
                {
                    MigrateWildGrowthLifecycle329(flagZdo);
                    RestoreBackgroundVirtualizedWildGrowth(flagZdo);
                }

                string flagId = WorkFlagZdo.ReadId(flagZdo);
                if (string.IsNullOrEmpty(flagId)) flagId = "loaded-" + flag.GetInstanceID();
                for (int i = 0; i < MaxSlots && slots.Count < limit; i++)
                {
                    if (!flagZdo.GetBool(Key(i, "persistent"), false) || !flagZdo.GetBool(Key(i, "active"), false)) continue;
                    int id = flagZdo.GetInt(Key(i, "id"), 0);
                    string harvest = flagZdo.GetString(Key(i, "harvest"), string.Empty);
                    if (id <= 0 || string.IsNullOrEmpty(harvest)) continue;
                    string key = flagId + ":" + id;
                    slots.Add(new GardenPersistentVisualSlot(key, id, flagZdo.GetInt(Key(i, "field"), 0), harvest,
                        ReadPosition(flagZdo, i), flagZdo.GetFloat(Key(i, "rot"), 0f)));
                }
            }

            LoadedGardenFlags.Clear();
            return slots.Count;
        }

        /// <summary>
        /// Counts completed persistent WildGrowth slots for one work area/resource. Completed
        /// means the saved player plan is inactive because its physical bush has been confirmed.
        /// This is a cheap ZDO-only query used by production displays; it never scans the world.
        /// </summary>
        /// <summary>
        /// Marks one player-planned Workers WildGrowth slot fulfilled from its durable plant ZDO
        /// marker. This is event-driven (plant spawn/load/harvest), not a world scan. The slot stays
        /// saved as a permanent blueprint but inactive forever while the physical Pickable handles
        /// its own vanilla picked/respawn lifecycle.
        /// </summary>
        internal static bool ConfirmPersistentWildGrowthSlot(string workAreaId, int slotId)
        {
            if (string.IsNullOrWhiteSpace(workAreaId) || slotId <= 0) return false;
            if (!WorkFlagRegistry.TryGetPersistentState(workAreaId, out ZDO flagZdo) || flagZdo == null) return false;

            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "persistent"), false) || flagZdo.GetInt(Key(i, "id"), 0) != slotId) continue;
                string harvest = flagZdo.GetString(Key(i, "harvest"), string.Empty);
                if (!WorkersWildFloraRuntime.TryCanonicalizeLegacyIdentity(harvest, out WorkersWildFloraRuntime.Definition def))
                    return false;

                if (!string.Equals(harvest, def.PrefabName, StringComparison.OrdinalIgnoreCase))
                    flagZdo.Set(Key(i, "harvest"), def.PrefabName);

                if (flagZdo.GetBool(Key(i, "active"), false))
                {
                    flagZdo.Set(Key(i, "active"), false);
                    RecountPending(flagZdo);
                    LastStatus = $"wildgrowth-respawn-owned#{slotId}";
                }
                else
                {
                    LastPendingCount = Mathf.Max(0, flagZdo.GetInt(CountKey, 0));
                    LastStatus = $"wildgrowth-respawn-already-owned#{slotId}";
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// v0.5.384: read-only WildGrowth plan snapshot for the explicit Workzone audit button.
        /// No world/physics scan happens here; this only reads the persistent Garden slots from
        /// the selected WorkFlag so the audit can compare them with the already loaded Pickable index.
        /// </summary>
        internal static int CollectWildGrowthAuditSlots(string workAreaId, List<GardenWildGrowthAuditSlot> output)
        {
            if (output == null) return 0;
            output.Clear();
            if (string.IsNullOrWhiteSpace(workAreaId)) return 0;
            if (!WorkFlagRegistry.TryGetPersistentState(workAreaId, out ZDO flagZdo) || flagZdo == null) return 0;

            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "persistent"), false)) continue;
                int slotId = flagZdo.GetInt(Key(i, "id"), 0);
                if (slotId <= 0) continue;
                string harvest = flagZdo.GetString(Key(i, "harvest"), string.Empty);
                if (!WorkersWildFloraRuntime.TryCanonicalizeLegacyIdentity(harvest, out WorkersWildFloraRuntime.Definition def) || def == null)
                    continue;

                output.Add(new GardenWildGrowthAuditSlot(
                    slotId,
                    def.PrefabName,
                    WorkerStorageRuntime.NormalizeObjectName(def.ResourcePrefab ?? string.Empty),
                    ReadPosition(flagZdo, i),
                    flagZdo.GetBool(Key(i, "active"), false)));
            }
            return output.Count;
        }

        internal static int CountCompletedWildGrowth(string workAreaId, string resourcePrefab)
        {
            if (string.IsNullOrWhiteSpace(workAreaId) || string.IsNullOrWhiteSpace(resourcePrefab)) return 0;
            if (!WorkFlagRegistry.TryGetPersistentState(workAreaId, out ZDO flagZdo) || flagZdo == null) return 0;

            string resource = WorkerStorageRuntime.NormalizeObjectName(resourcePrefab);
            int count = 0;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "persistent"), false)) continue;
                if (flagZdo.GetBool(Key(i, "active"), false)) continue;
                string harvest = flagZdo.GetString(Key(i, "harvest"), string.Empty);
                if (!WorkersWildFloraRuntime.TryCanonicalizeLegacyIdentity(harvest, out WorkersWildFloraRuntime.Definition def)) continue;
                if (!string.Equals(def.ResourcePrefab, resource, StringComparison.OrdinalIgnoreCase)) continue;
                count++;
            }
            return count;
        }

        /// <summary>
        /// v0.5.362: aggregate completed WildGrowth slots in one pass for the central Workzone
        /// economy cache. This avoids rescanning every persistent garden slot once per resource.
        /// </summary>
        internal static void CollectCompletedWildGrowth(string workAreaId, Dictionary<string, int> output)
        {
            if (output == null) return;
            output.Clear();
            if (string.IsNullOrWhiteSpace(workAreaId)) return;
            if (!WorkFlagRegistry.TryGetPersistentState(workAreaId, out ZDO flagZdo) || flagZdo == null) return;

            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "persistent"), false)) continue;
                if (flagZdo.GetBool(Key(i, "active"), false)) continue;
                string harvest = flagZdo.GetString(Key(i, "harvest"), string.Empty);
                if (!WorkersWildFloraRuntime.TryCanonicalizeLegacyIdentity(harvest, out WorkersWildFloraRuntime.Definition def)) continue;
                string resource = WorkerStorageRuntime.NormalizeObjectName(def.ResourcePrefab ?? string.Empty);
                if (string.IsNullOrWhiteSpace(resource)) continue;
                output.TryGetValue(resource, out int count);
                output[resource] = count + 1;
            }
        }

        internal static bool TryFindNearestPersistentPlan(long ownerPlayerId, Vector3 position, float maxDistance,
            out GardenPersistentPlanSelection selection)
        {
            selection = default;
            float maxSqr = Mathf.Max(0.15f, maxDistance) * Mathf.Max(0.15f, maxDistance);
            float bestSqr = maxSqr;
            bool found = false;

            WorkFlagRegistry.CollectLoadedFlags(ownerPlayerId, WorkOrder.Garden, 30f, LoadedGardenFlags);
            for (int f = 0; f < LoadedGardenFlags.Count; f++)
            {
                WorkFlagInteractable flag = LoadedGardenFlags[f];
                if (flag == null) continue;
                ZNetView view = flag.GetNetworkView();
                ZDO flagZdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (flagZdo == null) continue;

                string flagId = WorkFlagZdo.ReadId(flagZdo);
                if (string.IsNullOrEmpty(flagId)) flagId = "loaded-" + flag.GetInstanceID();
                for (int i = 0; i < MaxSlots; i++)
                {
                    if (!flagZdo.GetBool(Key(i, "persistent"), false)) continue;
                    int id = flagZdo.GetInt(Key(i, "id"), 0);
                    string harvest = flagZdo.GetString(Key(i, "harvest"), string.Empty);
                    if (id <= 0 || string.IsNullOrEmpty(harvest)) continue;

                    Vector3 slotPosition = ReadPosition(flagZdo, i);
                    Vector3 delta = slotPosition - position;
                    delta.y = 0f;
                    float sqr = delta.sqrMagnitude;
                    if (sqr > bestSqr) continue;

                    bestSqr = sqr;
                    selection = new GardenPersistentPlanSelection(
                        flagId + ":" + id,
                        id,
                        flagZdo.GetInt(Key(i, "field"), 0),
                        harvest,
                        slotPosition,
                        flagZdo.GetFloat(Key(i, "rot"), 0f),
                        flagZdo.GetBool(Key(i, "active"), false),
                        Mathf.Sqrt(sqr));
                    found = true;
                }
            }

            LoadedGardenFlags.Clear();
            return found;
        }

        internal static bool TryRemovePlannedAt(long ownerPlayerId, Vector3 position, float maxDistance, bool wholeField,
            out int removedCount, out int targetSlotId, out int fieldId)
        {
            removedCount = 0;
            targetSlotId = 0;
            fieldId = 0;

            float maxSqr = Mathf.Max(0.15f, maxDistance) * Mathf.Max(0.15f, maxDistance);
            int bestIndex = -1;
            float bestSqr = maxSqr;
            ZNetView bestView = null;
            ZDO bestZdo = null;

            WorkFlagRegistry.CollectLoadedFlags(ownerPlayerId, WorkOrder.Garden, 30f, LoadedGardenFlags);
            for (int f = 0; f < LoadedGardenFlags.Count; f++)
            {
                WorkFlagInteractable flag = LoadedGardenFlags[f];
                if (flag == null) continue;
                ZNetView view = flag.GetNetworkView();
                ZDO flagZdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (flagZdo == null) continue;

                for (int i = 0; i < MaxSlots; i++)
                {
                    if (!flagZdo.GetBool(Key(i, "persistent"), false)) continue;
                    Vector3 delta = ReadPosition(flagZdo, i) - position;
                    delta.y = 0f;
                    float sqr = delta.sqrMagnitude;
                    if (sqr > bestSqr) continue;
                    bestSqr = sqr;
                    bestIndex = i;
                    bestView = view;
                    bestZdo = flagZdo;
                }
            }
            LoadedGardenFlags.Clear();

            if (bestIndex < 0 || bestZdo == null || bestView == null)
            {
                LastStatus = "planned-remove-no-slot";
                return false;
            }
            if (!EnsureWritable(bestView))
            {
                LastStatus = "planned-remove-flag-readonly";
                return false;
            }

            targetSlotId = bestZdo.GetInt(Key(bestIndex, "id"), 0);
            fieldId = bestZdo.GetInt(Key(bestIndex, "field"), 0);
            List<int> remove = new List<int>();

            if (!wholeField)
            {
                remove.Add(bestIndex);
            }
            else if (fieldId > 0)
            {
                for (int i = 0; i < MaxSlots; i++)
                    if (bestZdo.GetBool(Key(i, "persistent"), false) && bestZdo.GetInt(Key(i, "field"), 0) == fieldId)
                        remove.Add(i);
            }
            else
            {
                CollectLegacyConnectedField(bestZdo, bestIndex, remove);
            }

            for (int r = 0; r < remove.Count; r++)
                if (ClearPersistentSlot(bestZdo, remove[r])) removedCount++;

            RecountPending(bestZdo);
            LastStatus = removedCount > 0
                ? (wholeField ? $"planned-field-removed:{removedCount}" : $"planned-slot-removed#{targetSlotId}")
                : "planned-remove-nothing";
            return removedCount > 0;
        }

        internal static bool PersistentSlotExistsForWorker(ZDO workerZdo, int slotId)
        {
            if (workerZdo == null || slotId <= 0 || !TryGetFlag(workerZdo, false, out _, out ZDO flagZdo)) return false;
            for (int i = 0; i < MaxSlots; i++)
                if (flagZdo.GetBool(Key(i, "persistent"), false) && flagZdo.GetInt(Key(i, "id"), 0) == slotId)
                    return true;
            return false;
        }

        private static int AllocateFieldId(ZDO flagZdo)
        {
            if (flagZdo == null) return 0;
            int fieldId = Mathf.Max(1, flagZdo.GetInt(NextFieldIdKey, 1));
            flagZdo.Set(NextFieldIdKey, fieldId == int.MaxValue ? 1 : fieldId + 1);
            return fieldId;
        }

        private static bool ClearPersistentSlot(ZDO zdo, int index)
        {
            if (zdo == null || index < 0 || index >= MaxSlots || !zdo.GetBool(Key(index, "persistent"), false)) return false;
            zdo.Set(Key(index, "active"), false);
            zdo.Set(Key(index, "persistent"), false);
            zdo.Set(Key(index, "id"), 0);
            zdo.Set(Key(index, "field"), 0);
            zdo.Set(Key(index, "harvest"), string.Empty);
            zdo.Set(Key(index, "x"), 0f);
            zdo.Set(Key(index, "y"), 0f);
            zdo.Set(Key(index, "z"), 0f);
            zdo.Set(Key(index, "rot"), 0f);
            return true;
        }

        private static void RecountPending(ZDO zdo)
        {
            if (zdo == null) return;
            int count = 0;
            for (int i = 0; i < MaxSlots; i++)
                if (zdo.GetBool(Key(i, "active"), false)) count++;
            zdo.Set(CountKey, count);
            LastPendingCount = count;
        }

        private static void CollectLegacyConnectedField(ZDO zdo, int seedIndex, List<int> output)
        {
            output.Clear();
            if (zdo == null || seedIndex < 0 || seedIndex >= MaxSlots || !zdo.GetBool(Key(seedIndex, "persistent"), false)) return;

            string harvest = zdo.GetString(Key(seedIndex, "harvest"), string.Empty);
            Vector3 seed = ReadPosition(zdo, seedIndex);
            float nearest = float.MaxValue;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (i == seedIndex || !zdo.GetBool(Key(i, "persistent"), false) ||
                    !string.Equals(zdo.GetString(Key(i, "harvest"), string.Empty), harvest, StringComparison.OrdinalIgnoreCase)) continue;
                Vector3 delta = ReadPosition(zdo, i) - seed;
                delta.y = 0f;
                float distance = delta.magnitude;
                if (distance > 0.15f && distance < nearest) nearest = distance;
            }

            if (nearest == float.MaxValue)
            {
                output.Add(seedIndex);
                return;
            }

            float connectDistance = Mathf.Clamp(nearest * 1.45f, 0.85f, 2.6f);
            float connectSqr = connectDistance * connectDistance;
            bool[] visited = new bool[MaxSlots];
            Queue<int> queue = new Queue<int>();
            visited[seedIndex] = true;
            queue.Enqueue(seedIndex);

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                output.Add(current);
                Vector3 currentPosition = ReadPosition(zdo, current);
                for (int i = 0; i < MaxSlots; i++)
                {
                    if (visited[i] || !zdo.GetBool(Key(i, "persistent"), false) ||
                        !string.Equals(zdo.GetString(Key(i, "harvest"), string.Empty), harvest, StringComparison.OrdinalIgnoreCase)) continue;
                    Vector3 delta = ReadPosition(zdo, i) - currentPosition;
                    delta.y = 0f;
                    if (delta.sqrMagnitude > connectSqr) continue;
                    visited[i] = true;
                    queue.Enqueue(i);
                }
            }
        }

        internal static bool TryGetPendingAtForWorker(ZDO workerZdo, Vector3 position, float maxDistance, out GardenReplantSlot slot)
        {
            slot = default;
            if (workerZdo == null || !TryGetFlag(workerZdo, false, out _, out ZDO flagZdo)) return false;

            float radius = Mathf.Max(0.15f, maxDistance);
            float bestSqr = radius * radius;
            int best = -1;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "active"), false) || !flagZdo.GetBool(Key(i, "persistent"), false)) continue;
                int id = flagZdo.GetInt(Key(i, "id"), 0);
                string harvest = flagZdo.GetString(Key(i, "harvest"), string.Empty);
                if (id <= 0 || string.IsNullOrEmpty(harvest)) continue;

                Vector3 slotPosition = ReadPosition(flagZdo, i);
                if (!IsPositionInsideAssignedWorkArea(workerZdo, slotPosition)) continue;
                Vector3 delta = slotPosition - position;
                delta.y = 0f;
                float sqr = delta.sqrMagnitude;
                if (sqr > bestSqr) continue;
                bestSqr = sqr;
                best = i;
            }

            if (best < 0) return false;
            slot = new GardenReplantSlot(
                flagZdo.GetInt(Key(best, "id"), 0),
                flagZdo.GetString(Key(best, "harvest"), string.Empty),
                ReadPosition(flagZdo, best),
                flagZdo.GetFloat(Key(best, "rot"), 0f),
                true,
                flagZdo.GetInt(Key(best, "field"), 0));
            return slot.SlotId > 0;
        }

        internal static bool TryPeekForWorker(ZDO workerZdo, out GardenReplantSlot slot)
        {
            slot = default;
            if (workerZdo == null || !TryGetFlag(workerZdo, false, out _, out ZDO flagZdo)) { LastStatus = "flag-unavailable"; return false; }
            LastPendingCount = Mathf.Max(0, flagZdo.GetInt(CountKey, 0));
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "active"), false)) continue;
                int id = flagZdo.GetInt(Key(i, "id"), 0);
                string harvest = flagZdo.GetString(Key(i, "harvest"), string.Empty);
                if (id <= 0 || string.IsNullOrEmpty(harvest)) continue;
                slot = new GardenReplantSlot(id, harvest, ReadPosition(flagZdo, i), flagZdo.GetFloat(Key(i, "rot"), 0f), flagZdo.GetBool(Key(i, "persistent"), false), flagZdo.GetInt(Key(i, "field"), 0));
                LastStatus = $"pending#{id}:{harvest}";
                return true;
            }
            LastStatus = "queue-empty";
            return false;
        }



        internal static int RemoveLegacyTransientSlotsForWorker(ZDO workerZdo)
        {
            if (workerZdo == null || !TryGetFlag(workerZdo, true, out _, out ZDO flagZdo)) return 0;
            int removed = 0;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "active"), false) || flagZdo.GetBool(Key(i, "persistent"), false)) continue;
                flagZdo.Set(Key(i, "active"), false);
                flagZdo.Set(Key(i, "id"), 0);
                flagZdo.Set(Key(i, "harvest"), string.Empty);
                flagZdo.Set(Key(i, "x"), 0f);
                flagZdo.Set(Key(i, "y"), 0f);
                flagZdo.Set(Key(i, "z"), 0f);
                flagZdo.Set(Key(i, "rot"), 0f);
                flagZdo.Set(Key(i, "field"), 0);
                removed++;
            }
            if (removed > 0)
            {
                RecountPending(flagZdo);
                LastStatus = $"removed-legacy-transient:{removed}";
            }
            return removed;
        }

        internal static int GetPendingForWorker(ZDO workerZdo, List<GardenReplantSlot> slots, int maxSlots = 32)
        {
            if (slots == null) return 0;
            slots.Clear();
            if (workerZdo == null || !TryGetFlag(workerZdo, false, out ZNetView flagView, out ZDO flagZdo)) { LastStatus = "flag-unavailable"; return 0; }
            if (flagView != null && flagView.IsValid() && EnsureWritable(flagView))
            {
                MigrateWildGrowthLifecycle329(flagZdo);
                RestoreBackgroundVirtualizedWildGrowth(flagZdo);
            }
            // v0.5.324: never reactivate an inactive persistent WildGrowth slot merely because a
            // Physics lookup cannot see its plant during sector/zone loading. v0.5.322's one-shot
            // recovery could run before neighbouring Pickables were instantiated and turn many
            // already fulfilled slots back into ghosts. Native physical WildGrowth prefabs remove
            // the original missing-custom-prefab problem, so automatic recovery is intentionally
            // disabled. Active slots still self-reconcile against both VW_Flora_* and vanilla names.
            int recovered = 0;
            LastPendingCount = Mathf.Max(0, flagZdo.GetInt(CountKey, 0));
            int limit = Mathf.Clamp(maxSlots, 1, MaxSlots);
            bool canMigrate = flagView != null && flagView.IsValid() && EnsureWritable(flagView);
            int migrated = 0;
            int outsideWorkArea = 0;
            for (int i = 0; i < MaxSlots && slots.Count < limit; i++)
            {
                if (!flagZdo.GetBool(Key(i, "active"), false)) continue;
                int id = flagZdo.GetInt(Key(i, "id"), 0);
                string harvest = flagZdo.GetString(Key(i, "harvest"), string.Empty);
                if (id <= 0 || string.IsNullOrEmpty(harvest)) continue;

                if (WorkersWildFloraRuntime.TryCanonicalizeLegacyIdentity(harvest, out WorkersWildFloraRuntime.Definition wildDef) &&
                    !string.Equals(harvest, wildDef.PrefabName, StringComparison.OrdinalIgnoreCase))
                {
                    harvest = wildDef.PrefabName;
                    if (canMigrate)
                    {
                        flagZdo.Set(Key(i, "harvest"), harvest);
                        migrated++;
                    }
                }

                Vector3 slotPosition = ReadPosition(flagZdo, i);
                if (!IsPositionInsideAssignedWorkArea(workerZdo, slotPosition))
                {
                    outsideWorkArea++;
                    continue;
                }

                slots.Add(new GardenReplantSlot(id, harvest, slotPosition, flagZdo.GetFloat(Key(i, "rot"), 0f), flagZdo.GetBool(Key(i, "persistent"), false), flagZdo.GetInt(Key(i, "field"), 0)));
            }
            if (migrated > 0 || recovered > 0 || outsideWorkArea > 0)
                LastStatus = $"pending={slots.Count}/{LastPendingCount}; outside-workzone={outsideWorkArea}; migrated-wildgrowth={migrated}; recovered-missing-wildgrowth={recovered}";
            else
                LastStatus = slots.Count > 0 ? $"pending={slots.Count}/{LastPendingCount}" : "queue-empty";
            return slots.Count;
        }

        // v0.5.322 recovery for the v0.5.314-v0.5.321 registration bug: CreateClonedPrefab
        // was used without PrefabManager.AddPrefab, so saved VW_Flora_* ZDOs could be destroyed
        // as invalid on world load. Permanent plan slots survive separately on the WorkFlag.
        // Re-open only inactive persistent WildGrowth slots whose expected physical plant is
        // actually absent. This runs once per loaded flag/session to avoid turning recovery into
        // a periodic physics scan. The normal HasExpectedPlantAt guard still prevents duplicates.
        private static int RecoverMissingPersistentWildGrowthSlots(ZNetView flagView, ZDO flagZdo)
        {
            if (flagView == null || flagZdo == null || !flagView.IsValid() || ZNetScene.instance == null) return 0;
            ZDOID uid = flagZdo.m_uid;
            if (WildGrowthRecoveryChecked.Contains(uid)) return 0;
            if (!EnsureWritable(flagView)) return 0;

            int recovered = 0;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "persistent"), false) || flagZdo.GetBool(Key(i, "active"), false)) continue;
                int id = flagZdo.GetInt(Key(i, "id"), 0);
                string harvest = flagZdo.GetString(Key(i, "harvest"), string.Empty);
                if (id <= 0 || string.IsNullOrEmpty(harvest)) continue;
                if (!WorkersWildFloraRuntime.TryCanonicalizeLegacyIdentity(harvest, out WorkersWildFloraRuntime.Definition def)) continue;

                Vector3 position = ReadPosition(flagZdo, i);
                if (GardenTargetFinder.HasExpectedPlantAt(position, def.Spacing, def.PrefabName)) continue;

                flagZdo.Set(Key(i, "harvest"), def.PrefabName);
                flagZdo.Set(Key(i, "active"), true);
                recovered++;
            }

            if (recovered > 0)
                RecountPending(flagZdo);
            WildGrowthRecoveryChecked.Add(uid);
            return recovered;
        }

        internal static int GetPendingBatchForWorker(ZDO workerZdo, int anchorSlotId, string harvestPrefab, Vector3 anchorPosition,
            float maxDistance, List<GardenReplantSlot> slots, int maxAdditional = 8)
        {
            if (slots == null) return 0;
            slots.Clear();
            if (workerZdo == null || anchorSlotId <= 0 || string.IsNullOrEmpty(harvestPrefab)) return 0;
            if (!TryGetFlag(workerZdo, false, out _, out ZDO flagZdo)) return 0;

            int anchorFieldId = 0;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "persistent"), false)) continue;
                if (flagZdo.GetInt(Key(i, "id"), 0) != anchorSlotId) continue;
                anchorFieldId = Mathf.Max(0, flagZdo.GetInt(Key(i, "field"), 0));
                break;
            }

            float radius = Mathf.Max(0.5f, maxDistance);
            float maxSqr = radius * radius;
            int limit = Mathf.Clamp(maxAdditional, 1, 8);
            List<GardenReplantSlot> candidates = new List<GardenReplantSlot>(16);
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "active"), false) || !flagZdo.GetBool(Key(i, "persistent"), false)) continue;
                int id = flagZdo.GetInt(Key(i, "id"), 0);
                if (id <= 0 || id == anchorSlotId) continue;
                int fieldId = Mathf.Max(0, flagZdo.GetInt(Key(i, "field"), 0));
                if (anchorFieldId > 0 && fieldId != anchorFieldId) continue;
                string harvest = flagZdo.GetString(Key(i, "harvest"), string.Empty);
                if (!string.Equals(harvest, harvestPrefab, StringComparison.OrdinalIgnoreCase)) continue;
                Vector3 position = ReadPosition(flagZdo, i);
                Vector3 delta = position - anchorPosition; delta.y = 0f;
                if (delta.sqrMagnitude > maxSqr) continue;
                candidates.Add(new GardenReplantSlot(id, harvest, position, flagZdo.GetFloat(Key(i, "rot"), 0f), true, fieldId));
            }

            candidates.Sort((a, b) =>
            {
                Vector3 da = a.Position - anchorPosition; da.y = 0f;
                Vector3 db = b.Position - anchorPosition; db.y = 0f;
                return da.sqrMagnitude.CompareTo(db.sqrMagnitude);
            });
            for (int i = 0; i < candidates.Count && slots.Count < limit; i++) slots.Add(candidates[i]);
            return slots.Count;
        }

        internal static bool CompleteForWorker(ZDO workerZdo, int slotId)
        {
            if (workerZdo == null || slotId <= 0 || !TryGetFlag(workerZdo, true, out _, out ZDO flagZdo)) return false;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "active"), false) || flagZdo.GetInt(Key(i, "id"), 0) != slotId) continue;
                bool persistent = flagZdo.GetBool(Key(i, "persistent"), false);
                flagZdo.Set(Key(i, "active"), false);
                if (!persistent)
                {
                    flagZdo.Set(Key(i, "id"), 0);
                    flagZdo.Set(Key(i, "harvest"), string.Empty);
                }
                int count = Mathf.Max(0, flagZdo.GetInt(CountKey, 0) - 1);
                flagZdo.Set(CountKey, count);
                LastPendingCount = count;
                LastStatus = $"completed#{slotId}";
                return true;
            }
            LastStatus = $"slot-missing#{slotId}";
            return false;
        }

        private static bool TryGetFlagForHarvest(ZDO workerZdo, Vector3 harvestPosition, bool requireWrite, out ZNetView nview, out ZDO flagZdo)
        {
            nview = null; flagZdo = null;
            string id = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (!string.IsNullOrEmpty(id) && WorkFlagRegistry.TryGetNetworkState(id, 30f, out nview, out flagZdo) && WorkFlagZdo.HasOrder(flagZdo, WorkOrder.Garden))
                return !requireWrite || EnsureWritable(nview);

            long owner = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            if (!WorkFlagRegistry.TryFindContainingFlag(owner, WorkOrder.Garden, harvestPosition, 30f, out nview, out flagZdo)) return false;
            return !requireWrite || EnsureWritable(nview);
        }

        private static bool TryGetFlag(ZDO workerZdo, bool requireWrite, out ZNetView nview, out ZDO flagZdo)
        {
            nview = null; flagZdo = null;
            if (workerZdo == null) return false;

            string id = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (!string.IsNullOrEmpty(id) && WorkFlagRegistry.TryGetNetworkState(id, 30f, out nview, out flagZdo))
                return !requireWrite || EnsureWritable(nview);

            // v0.5.313: player planning can recover/store a field on a live WorkArea even when the
            // worker still carries an empty/stale vw_work_area_id after older saves or hot reloads.
            // Job pickup must resolve the same spatial WorkArea or the Shaman sees zero pending slots
            // and remains in FindJob although the planner just reported GESPEICHERT.
            Vector3 workerPosition = workerZdo.GetPosition();
            if (!WorkFlagRegistry.TryFindContainingWorkAreaForPlanning(workerPosition, 30f, out nview, out flagZdo))
            {
                nview = null; flagZdo = null;
                return false;
            }

            // Do not let a worker borrow another player's WorkArea when ownership metadata exists.
            long workerOwner = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            long flagOwner = WorkFlagZdo.ReadOwner(flagZdo);
            if (workerOwner != 0L && flagOwner != 0L && workerOwner != flagOwner)
            {
                nview = null; flagZdo = null;
                return false;
            }

            LastStatus = $"worker-workarea-recovered:{WorkFlagZdo.ReadId(flagZdo)}";
            return !requireWrite || EnsureWritable(nview);
        }

        /// <summary>
        /// Player planning is an explicit local action. SmartReplant custom-flora placement ghosts
        /// can expose a wrapper/root transform that is not the same spatial anchor as the visible
        /// flora preview. Therefore a failed lookup at the slot anchor gets one safe fallback: use
        /// the local player's actual position to select the containing WorkArea, then the caller
        /// still validates every planned slot against that WorkArea radius before writing anything.
        /// This cannot leak plans outside a zone; it only prevents a bad custom ghost anchor from
        /// making a visibly in-zone planning click report planned-no-workzone.
        /// </summary>
        private static bool TryResolvePlanningWorkArea(long ownerPlayerId, Vector3 slotPosition, float defaultRadius,
            out ZNetView nview, out ZDO flagZdo, out string source)
        {
            // v0.5.327: a planning click must land on the SAME WorkFlag that the loaded gardener
            // is assigned to. The old nearest-flag lookup ignored worker assignment (and even
            // ownership), so overlapping Workzones could store perfectly visible ghosts on a
            // different flag while the Shaman's own flag stayed empty. The worker then correctly
            // reported WaitAtHome forever because, from its assigned WorkArea, there was no job.
            if (TryResolveAssignedGardenerWorkArea(ownerPlayerId, slotPosition, defaultRadius, out nview, out flagZdo))
            {
                source = "assigned-gardener-workzone";
                return true;
            }

            source = "owner-slot";
            if (WorkFlagRegistry.TryFindContainingWorkArea(ownerPlayerId, slotPosition, defaultRadius, out nview, out flagZdo))
                return true;

            // Keep the old hot-reload repair path only as a last resort, and never accept a flag
            // owned by someone else when the planning player has a real owner id.
            if (WorkFlagRegistry.TryFindContainingWorkAreaForPlanning(slotPosition, defaultRadius, out nview, out flagZdo))
            {
                long flagOwner = WorkFlagZdo.ReadOwner(flagZdo);
                if (ownerPlayerId == 0L || flagOwner == 0L || flagOwner == ownerPlayerId)
                {
                    source = "slot-recovery";
                    return true;
                }
            }

            Player local = Player.m_localPlayer;
            if (local != null)
            {
                if (TryResolveAssignedGardenerWorkArea(ownerPlayerId, local.transform.position, defaultRadius, out nview, out flagZdo))
                {
                    source = "assigned-gardener-player-fallback";
                    return true;
                }
                if (WorkFlagRegistry.TryFindContainingWorkArea(ownerPlayerId, local.transform.position, defaultRadius, out nview, out flagZdo))
                {
                    source = "owner-player-position-fallback";
                    return true;
                }
            }

            nview = null;
            flagZdo = null;
            source = "none";
            return false;
        }

        private static bool TryResolvePlanningWorkAreaForField(long ownerPlayerId, IList<Vector3> positions, float defaultRadius,
            out ZNetView nview, out ZDO flagZdo, out string source)
        {
            nview = null;
            flagZdo = null;
            source = "none";
            if (positions == null || positions.Count < 1) return false;

            Vector3 centroid = Vector3.zero;
            for (int i = 0; i < positions.Count; i++) centroid += positions[i];
            centroid /= positions.Count;

            // Preserve the preferred assigned-gardener Workzone when it really contains the
            // complete grid. If it only contains the anchor/centroid, fall through and find the
            // actual Workzone that contains every slot.
            if (TryResolveAssignedGardenerWorkArea(ownerPlayerId, centroid, defaultRadius, out ZNetView assignedView, out ZDO assignedZdo) &&
                FieldFitsWorkArea(assignedZdo, positions, defaultRadius))
            {
                nview = assignedView;
                flagZdo = assignedZdo;
                source = "assigned-gardener-whole-field";
                return true;
            }

            if (WorkFlagRegistry.TryFindContainingFlagForField(ownerPlayerId, WorkOrder.Garden, positions, defaultRadius,
                    out nview, out flagZdo))
            {
                source = "owner-whole-field";
                return true;
            }

            // v0.5.373: planning is a player-facing blueprint operation, not a precision collision
            // test. The visible Workzone ring is approximate and tiny terrain-projected grids can
            // end up a fraction outside its mathematical radius. Resolve the field by its center
            // and accept a small 2 m edge tolerance. This still rejects a field whose center is
            // outside the Workzone or which materially extends beyond it, but removes the repeated
            // no-single-workzone failures for obviously in-zone mushroom grids. This path executes
            // only on the explicit planning click and adds no polling or background scan.
            if (TryResolvePlanningWorkArea(ownerPlayerId, centroid, defaultRadius, out nview, out flagZdo, out string centroidSource) &&
                FieldFitsWorkArea(flagZdo, positions, defaultRadius, 2f))
            {
                source = centroidSource + "+field-edge-tolerance";
                return true;
            }

            return false;
        }

        private static bool FieldFitsWorkArea(ZDO flagZdo, IList<Vector3> positions, float defaultRadius, float edgeTolerance = 0f)
        {
            if (flagZdo == null || positions == null || positions.Count < 1) return false;
            float radius = WorkFlagZdo.ReadRadius(flagZdo, defaultRadius) + Mathf.Max(0f, edgeTolerance);
            float radiusSqr = radius * radius;
            Vector3 center = WorkFlagRegistry.ResolveRuntimeCenter(flagZdo, defaultRadius);
            for (int i = 0; i < positions.Count; i++)
            {
                Vector3 delta = positions[i] - center;
                delta.y = 0f;
                if (delta.sqrMagnitude > radiusSqr + 0.01f) return false;
            }
            return true;
        }

        private static bool TryResolveAssignedGardenerWorkArea(long ownerPlayerId, Vector3 position, float defaultRadius,
            out ZNetView bestView, out ZDO bestZdo)
        {
            bestView = null;
            bestZdo = null;
            float bestSqr = float.MaxValue;

            PlanningWorkerScratch.Clear();
            WorkerFastLookupRuntime.CopyKnownWorkers(PlanningWorkerScratch);
            for (int i = 0; i < PlanningWorkerScratch.Count; i++)
            {
                Character worker = PlanningWorkerScratch[i];
                if (worker == null || worker.IsDead()) continue;
                if (!WorkerFastLookupRuntime.TryGetWorker(worker, false, out _, out ZDO workerZdo) || workerZdo == null) continue;

                WorkerRole role = (WorkerRole)workerZdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
                if (role != WorkerRole.Gardener) continue;
                if ((WorkerJobProfileRuntime.ResolveEffectiveGardenerTasks(workerZdo) & WorkerTaskFlags.Garden) == 0) continue;

                long workerOwner = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
                if (ownerPlayerId != 0L && workerOwner != 0L && ownerPlayerId != workerOwner) continue;

                string flagId = WorkerZdo.ReadWorkAreaId(workerZdo);
                if (string.IsNullOrEmpty(flagId)) continue;
                if (!WorkFlagRegistry.TryGetNetworkState(flagId, defaultRadius, out ZNetView view, out ZDO candidate) || candidate == null) continue;
                if (!WorkFlagZdo.IsEnabled(candidate)) continue;

                long flagOwner = WorkFlagZdo.ReadOwner(candidate);
                if (ownerPlayerId != 0L && flagOwner != 0L && ownerPlayerId != flagOwner) continue;

                float radius = WorkFlagZdo.ReadRadius(candidate, defaultRadius);
                Vector3 delta = WorkFlagRegistry.ResolveRuntimeCenter(candidate, defaultRadius) - position;
                delta.y = 0f;
                float sqr = delta.sqrMagnitude;
                if (sqr > radius * radius || sqr >= bestSqr) continue;

                bestSqr = sqr;
                bestView = view;
                bestZdo = candidate;
            }
            PlanningWorkerScratch.Clear();
            return bestView != null && bestZdo != null;
        }

        private static int WakeAssignedGardeners(ZDO flagZdo)
        {
            if (flagZdo == null) return 0;
            string flagId = WorkFlagZdo.ReadId(flagZdo);
            if (string.IsNullOrEmpty(flagId)) return 0;

            int woke = 0;
            PlanningWorkerScratch.Clear();
            WorkerFastLookupRuntime.CopyKnownWorkers(PlanningWorkerScratch);
            for (int i = 0; i < PlanningWorkerScratch.Count; i++)
            {
                Character worker = PlanningWorkerScratch[i];
                if (worker == null || worker.IsDead()) continue;
                if (!WorkerFastLookupRuntime.TryGetWorker(worker, false, out _, out ZDO workerZdo) || workerZdo == null) continue;
                if ((WorkerRole)workerZdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None) != WorkerRole.Gardener) continue;
                if ((WorkerJobProfileRuntime.ResolveEffectiveGardenerTasks(workerZdo) & WorkerTaskFlags.Garden) == 0) continue;
                if (!string.Equals(WorkerZdo.ReadWorkAreaId(workerZdo), flagId, StringComparison.Ordinal)) continue;

                // A newly planned field is an explicit player-created Garden job. If Garden was
                // temporarily backed off because an older slot lacked resources, re-arm it now so
                // the scheduler evaluates the new field immediately. No world scan is introduced.
                WorkerResourceBackoffRuntime.WakeTask(workerZdo, WorkerTaskFlags.Garden);

                WorkerState state = (WorkerState)workerZdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
                if (state != WorkerState.WaitAtHome && state != WorkerState.ReturnHome &&
                    state != WorkerState.Idle && state != WorkerState.FindJob)
                    continue;

                WorkerZdo.ClearTarget(workerZdo);
                workerZdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                woke++;
            }
            PlanningWorkerScratch.Clear();
            return woke;
        }

        private static bool EnsureWritable(ZNetView nview)
        {
            if (nview == null || !nview.IsValid()) return false;
            if (nview.IsOwner()) return true;
            try { ClaimOwnershipMethod?.Invoke(nview, null); return nview.IsOwner(); }
            catch { return false; }
        }

        private static Vector3 ReadPosition(ZDO zdo, int index) => new Vector3(
            zdo.GetFloat(Key(index, "x"), 0f), zdo.GetFloat(Key(index, "y"), 0f), zdo.GetFloat(Key(index, "z"), 0f));
        private static string Key(int index, string field) => $"vw_garden_slot_{index}_{field}";
        private static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            int clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
            return (clone >= 0 ? name.Substring(0, clone) : name).Trim();
        }
    }
}
