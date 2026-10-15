using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    internal readonly struct ForestryPlanSlot
    {
        internal readonly int SlotId;
        internal readonly int FieldId;
        internal readonly string SeedPrefab;
        internal readonly string SaplingPrefab;
        internal readonly Vector3 Position;
        internal readonly float RotationY;

        internal ForestryPlanSlot(int slotId, int fieldId, string seedPrefab, string saplingPrefab, Vector3 position, float rotationY)
        {
            SlotId = slotId;
            FieldId = fieldId;
            SeedPrefab = seedPrefab ?? string.Empty;
            SaplingPrefab = saplingPrefab ?? string.Empty;
            Position = position;
            RotationY = rotationY;
        }
    }


    internal readonly struct ForestryBackgroundSlot
    {
        internal readonly int SlotId;
        internal readonly string SeedPrefab;
        internal readonly string SaplingPrefab;
        internal readonly Vector3 Position;
        internal readonly float RotationY;
        internal readonly bool Active;
        internal readonly int State; // 0=pending/physical, 1=growing, 2=mature
        internal readonly long ReadyTicks;
        internal readonly int SeedBudget;
        internal readonly int SeedDebt;
        internal readonly bool VirtualPlant;

        internal ForestryBackgroundSlot(int slotId, string seedPrefab, string saplingPrefab, Vector3 position,
            float rotationY, bool active, int state, long readyTicks, int seedBudget, int seedDebt, bool virtualPlant)
        {
            SlotId = slotId;
            SeedPrefab = seedPrefab ?? string.Empty;
            SaplingPrefab = saplingPrefab ?? string.Empty;
            Position = position;
            RotationY = rotationY;
            Active = active;
            State = state;
            ReadyTicks = readyTicks;
            SeedBudget = seedBudget;
            SeedDebt = seedDebt;
            VirtualPlant = virtualPlant;
        }
    }

    /// <summary>
    /// Persistent player-planned forestry slots stored on a Forestry-enabled WorkFlag.
    /// A planned slot stays persistent after planting and is merely toggled inactive. When a
    /// Lumberjack later fells the tree at that position, the slot is reactivated for the Forester.
    /// </summary>
    internal static class ForestryPlanRuntime
    {
        private const int MaxSlots = 512;
        private const string NextIdKey = "vw_forestry_plan_next_id";
        private const string NextFieldIdKey = "vw_forestry_plan_next_field_id";
        private const string CountKey = "vw_forestry_plan_count";
        private const int FieldIdBase = 1000000; // keeps Garden and Forestry group ids distinct in shared ghost UI

        private static readonly MethodInfo ClaimOwnershipMethod = typeof(ZNetView).GetMethod(
            "ClaimOwnership", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        private static readonly List<WorkFlagInteractable> LoadedFlags = new List<WorkFlagInteractable>();

        internal static int LastPendingCount { get; private set; }
        internal static int LastCheckedCount { get; private set; }
        internal static int LastBlockedCount { get; private set; }
        internal static string LastStatus { get; private set; } = "idle";

        internal static bool EnqueuePlannedSlot(long ownerPlayerId, string seedPrefab, string saplingPrefab,
            Vector3 position, float rotationY, out int slotId)
        {
            slotId = 0;
            if (string.IsNullOrEmpty(seedPrefab) || string.IsNullOrEmpty(saplingPrefab))
            {
                LastStatus = "planned-invalid";
                return false;
            }
            if (!WorkFlagRegistry.TryFindContainingFlag(ownerPlayerId, WorkOrder.Forestry, position, 30f, out ZNetView nview, out ZDO flagZdo))
            {
                LastStatus = "planned-no-forestry-workzone";
                return false;
            }
            if (!EnsureWritable(nview))
            {
                LastStatus = "planned-flag-readonly";
                return false;
            }
            int fieldId = AllocateFieldId(flagZdo);
            return UpsertPlannedSlot(flagZdo, seedPrefab, saplingPrefab, position, rotationY, fieldId, out slotId);
        }

        internal static bool EnqueuePlannedField(long ownerPlayerId, string seedPrefab, string saplingPrefab,
            IList<Vector3> positions, float rotationY, out int firstSlotId, out int plannedCount)
        {
            firstSlotId = 0;
            plannedCount = 0;
            if (string.IsNullOrEmpty(seedPrefab) || string.IsNullOrEmpty(saplingPrefab) || positions == null || positions.Count < 1)
            {
                LastStatus = "planned-field-invalid";
                return false;
            }

            // v0.5.371: choose one Forestry Workzone that contains the complete grid instead
            // of selecting a zone from slot 1 and rejecting later slots against that choice.
            if (!WorkFlagRegistry.TryFindContainingFlagForField(ownerPlayerId, WorkOrder.Forestry, positions, 30f,
                    out ZNetView nview, out ZDO flagZdo))
            {
                LastStatus = "planned-field-no-single-forestry-workzone";
                return false;
            }
            if (!EnsureWritable(nview))
            {
                LastStatus = "planned-field-flag-readonly";
                return false;
            }

            int requiredNew = 0;
            for (int i = 0; i < positions.Count; i++)
                if (FindPersistentSlot(flagZdo, positions[i]) < 0)
                    requiredNew++;

            int freeCount = 0;
            for (int i = 0; i < MaxSlots; i++)
                if (!flagZdo.GetBool(Key(i, "persistent"), false))
                    freeCount++;

            if (requiredNew > freeCount)
            {
                LastStatus = $"planned-field-queue-full:need={requiredNew},free={freeCount}";
                return false;
            }

            int fieldId = AllocateFieldId(flagZdo);
            for (int i = 0; i < positions.Count; i++)
            {
                if (!UpsertPlannedSlot(flagZdo, seedPrefab, saplingPrefab, positions[i], rotationY, fieldId, out int slotId))
                {
                    LastStatus = $"planned-field-write-failed:{i + 1}/{positions.Count}";
                    return false;
                }
                if (firstSlotId <= 0) firstSlotId = slotId;
                plannedCount++;
            }

            LastStatus = $"planned-field:{plannedCount}:field#{fieldId}";
            return plannedCount == positions.Count && firstSlotId > 0;
        }

        internal static bool TryGetNearestPendingForWorker(Character worker, ZDO workerZdo, out ForestryPlanSlot slot)
        {
            slot = default;
            if (worker == null || workerZdo == null || !TryGetFlag(workerZdo, false, out _, out ZDO flagZdo))
            {
                LastPendingCount = 0;
                LastCheckedCount = 0;
                LastBlockedCount = 0;
                LastStatus = "flag-unavailable";
                return false;
            }

            LastPendingCount = Mathf.Max(0, flagZdo.GetInt(CountKey, 0));
            LastCheckedCount = 0;
            LastBlockedCount = 0;
            int tierBlocked = 0;
            int firstRequiredTier = 0;
            string firstTierSapling = string.Empty;
            float best = float.MaxValue;
            bool found = false;
            string firstBlocker = string.Empty;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "persistent"), false) || !flagZdo.GetBool(Key(i, "active"), false)) continue;
                int id = flagZdo.GetInt(Key(i, "id"), 0);
                string seed = flagZdo.GetString(Key(i, "seed"), string.Empty);
                string sapling = flagZdo.GetString(Key(i, "sapling"), string.Empty);
                if (id <= 0 || string.IsNullOrEmpty(seed) || string.IsNullOrEmpty(sapling)) continue;
                LastCheckedCount++;
                if (!GardenerTaskFilterRuntime.IsForestrySpeciesAllowed(workerZdo, sapling))
                {
                    tierBlocked++;
                    continue;
                }
                if (!ForestryWorkRuntime.CanWorkerPlant(worker, seed, sapling, out int requiredTier))
                {
                    tierBlocked++;
                    if (firstRequiredTier <= 0)
                    {
                        firstRequiredTier = requiredTier;
                        firstTierSapling = sapling;
                    }
                    continue;
                }
                Vector3 position = ReadPosition(flagZdo, i);
                // Player-planned forestry slots are authoritative. Only reject a slot when the
                // actual planting point is physically occupied; do not veto the whole plan because
                // another tree or building happens to be several metres away.
                if (!ForestryTargetFinder.IsPlannedSlotPhysicallyFree(position))
                {
                    LastBlockedCount++;
                    if (string.IsNullOrEmpty(firstBlocker))
                        firstBlocker = ForestryTargetFinder.LastPhysicalBlocker;
                    continue;
                }
                float sqr = (position - worker.transform.position).sqrMagnitude;
                if (sqr >= best) continue;
                best = sqr;
                slot = new ForestryPlanSlot(id, flagZdo.GetInt(Key(i, "field"), 0), seed, sapling,
                    position, flagZdo.GetFloat(Key(i, "rot"), 0f));
                found = true;
            }

            LastStatus = found ? $"pending#{slot.SlotId}:{slot.SaplingPrefab}" :
                (LastPendingCount > 0
                    ? (tierBlocked > 0 && tierBlocked + LastBlockedCount >= LastCheckedCount
                        ? $"pending-tier-blocked:{tierBlocked}/{LastCheckedCount}; first={firstTierSapling}; requires-tier={firstRequiredTier}; physical-blocked={LastBlockedCount}"
                        : $"pending-blocked:{LastBlockedCount}/{LastCheckedCount}; tier-blocked={tierBlocked}; first={firstBlocker}")
                    : "queue-empty");
            return found;
        }

        // Resource-aware selector used only when the currently latched forestry slot cannot
        // obtain its seed. This prevents one missing seed type (for example BirchSeeds) from
        // starving other player-planned slots whose seeds are already available locally.
        // It does not replace the normal nearest-slot selection and therefore preserves the
        // established Forestry ordering whenever the current plan can actually run.
        internal static bool TryGetNearestPendingWithAvailableSeedForWorker(Character worker, ZDO workerZdo,
            int excludeSlotId, Func<string, bool> seedAvailable, out ForestryPlanSlot slot)
        {
            slot = default;
            if (worker == null || workerZdo == null || seedAvailable == null ||
                !TryGetFlag(workerZdo, false, out _, out ZDO flagZdo))
                return false;

            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "persistent"), false) || !flagZdo.GetBool(Key(i, "active"), false)) continue;
                int id = flagZdo.GetInt(Key(i, "id"), 0);
                if (id <= 0 || id == excludeSlotId) continue;

                string seed = flagZdo.GetString(Key(i, "seed"), string.Empty);
                string sapling = flagZdo.GetString(Key(i, "sapling"), string.Empty);
                if (string.IsNullOrEmpty(seed) || string.IsNullOrEmpty(sapling)) continue;
                if (!GardenerTaskFilterRuntime.IsForestrySpeciesAllowed(workerZdo, sapling)) continue;
                if (!ForestryWorkRuntime.CanWorkerPlant(worker, seed, sapling, out _)) continue;
                if (!seedAvailable(seed)) continue;

                Vector3 position = ReadPosition(flagZdo, i);
                if (!ForestryTargetFinder.IsPlannedSlotPhysicallyFree(position)) continue;

                float sqr = (position - worker.transform.position).sqrMagnitude;
                if (sqr >= best) continue;
                best = sqr;
                slot = new ForestryPlanSlot(id, flagZdo.GetInt(Key(i, "field"), 0), seed, sapling,
                    position, flagZdo.GetFloat(Key(i, "rot"), 0f));
                found = true;
            }

            return found;
        }

        internal static bool PersistentSlotExistsForWorker(ZDO workerZdo, int slotId)
        {
            if (workerZdo == null || slotId <= 0 || !TryGetFlag(workerZdo, false, out _, out ZDO flagZdo)) return false;
            for (int i = 0; i < MaxSlots; i++)
                if (flagZdo.GetBool(Key(i, "persistent"), false) && flagZdo.GetInt(Key(i, "id"), 0) == slotId)
                    return true;
            return false;
        }

        internal static bool CompleteForWorker(ZDO workerZdo, int slotId)
        {
            if (workerZdo == null || slotId <= 0 || !TryGetFlag(workerZdo, true, out _, out ZDO flagZdo)) return false;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "persistent"), false) || flagZdo.GetInt(Key(i, "id"), 0) != slotId) continue;
                if (flagZdo.GetBool(Key(i, "active"), false))
                {
                    flagZdo.Set(Key(i, "active"), false);
                    int count = Mathf.Max(0, flagZdo.GetInt(CountKey, 0) - 1);
                    flagZdo.Set(CountKey, count);
                    LastPendingCount = count;
                }
                string sapling = flagZdo.GetString(Key(i, "sapling"), string.Empty);
                double growSeconds = ForestryWorkRuntime.EstimateGrowthSeconds(sapling);
                flagZdo.Set(Key(i, "bg_state"), 1);
                flagZdo.Set(Key(i, "bg_ready"), DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(growSeconds).Ticks);
                flagZdo.Set(Key(i, "bg_virtual"), false);
                flagZdo.Set(Key(i, "physical_planted_at"), DateTime.UtcNow.Ticks);
                LastStatus = $"completed#{slotId}";
                return true;
            }
            LastStatus = $"slot-missing#{slotId}";
            return false;
        }

        internal static bool ReactivateNearest(long ownerPlayerId, Vector3 treePosition, float maxDistance = 1.8f)
        {
            if (!WorkFlagRegistry.TryFindContainingFlag(ownerPlayerId, WorkOrder.Forestry, treePosition, 30f,
                    out ZNetView nview, out ZDO flagZdo)) return false;
            if (!EnsureWritable(nview)) return false;

            float maxSqr = Mathf.Max(0.5f, maxDistance) * Mathf.Max(0.5f, maxDistance);
            int bestIndex = -1;
            float best = maxSqr;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "persistent"), false)) continue;
                Vector3 delta = ReadPosition(flagZdo, i) - treePosition;
                delta.y = 0f;
                float sqr = delta.sqrMagnitude;
                if (sqr > best) continue;
                best = sqr;
                bestIndex = i;
            }
            if (bestIndex < 0) return false;
            if (!flagZdo.GetBool(Key(bestIndex, "active"), false))
            {
                flagZdo.Set(Key(bestIndex, "active"), true);
                int count = Mathf.Clamp(flagZdo.GetInt(CountKey, 0) + 1, 0, MaxSlots);
                flagZdo.Set(CountKey, count);
                LastPendingCount = count;
            }
            flagZdo.Set(Key(bestIndex, "bg_state"), 0);
            flagZdo.Set(Key(bestIndex, "bg_ready"), 0L);
            flagZdo.Set(Key(bestIndex, "bg_virtual"), false);
            flagZdo.Set(Key(bestIndex, "physical_planted_at"), 0L);
            LastStatus = $"reactivated#{flagZdo.GetInt(Key(bestIndex, "id"), 0)}";
            return true;
        }

        /// <summary>
        /// Appends persistent forestry slots that are currently fulfilled (inactive = a sapling/tree was planted).
        /// Lumberjacks use this as a lightweight management hint so mature planned trees are preferred
        /// over unrelated wild trees without changing the normal fallback clear-cut behavior.
        /// </summary>
        internal static int AppendFulfilledSlotPositionsForWorker(ZDO workerZdo, List<Vector3> positions, int maxTotal = 192)
        {
            if (workerZdo == null || positions == null || positions.Count >= maxTotal) return 0;
            if (!TryGetFlag(workerZdo, false, out _, out ZDO flagZdo)) return 0;

            int start = positions.Count;
            for (int i = 0; i < MaxSlots && positions.Count < maxTotal; i++)
            {
                if (!flagZdo.GetBool(Key(i, "persistent"), false)) continue;
                if (flagZdo.GetBool(Key(i, "active"), false)) continue;
                positions.Add(ReadPosition(flagZdo, i));
            }
            return positions.Count - start;
        }

        internal static int AppendLoadedPersistentActiveSlots(long ownerPlayerId, List<GardenPersistentVisualSlot> slots, int maxTotal = 512)
        {
            if (slots == null || slots.Count >= maxTotal) return 0;
            int start = slots.Count;
            WorkFlagRegistry.CollectLoadedFlags(ownerPlayerId, WorkOrder.Forestry, 30f, LoadedFlags);
            for (int f = 0; f < LoadedFlags.Count && slots.Count < maxTotal; f++)
            {
                WorkFlagInteractable flag = LoadedFlags[f];
                if (flag == null) continue;
                ZNetView view = flag.GetNetworkView();
                ZDO flagZdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (flagZdo == null) continue;
                string flagId = WorkFlagZdo.ReadId(flagZdo);
                if (string.IsNullOrEmpty(flagId)) flagId = "loaded-" + flag.GetInstanceID();

                for (int i = 0; i < MaxSlots && slots.Count < maxTotal; i++)
                {
                    if (!flagZdo.GetBool(Key(i, "persistent"), false) || !flagZdo.GetBool(Key(i, "active"), false)) continue;
                    int id = flagZdo.GetInt(Key(i, "id"), 0);
                    string sapling = flagZdo.GetString(Key(i, "sapling"), string.Empty);
                    if (id <= 0 || string.IsNullOrEmpty(sapling)) continue;
                    slots.Add(new GardenPersistentVisualSlot("forestry:" + flagId + ":" + id, id,
                        flagZdo.GetInt(Key(i, "field"), 0), sapling, ReadPosition(flagZdo, i), flagZdo.GetFloat(Key(i, "rot"), 0f)));
                }
            }
            LoadedFlags.Clear();
            return slots.Count - start;
        }

        internal static bool TryRemovePlannedAt(long ownerPlayerId, Vector3 position, float maxDistance, bool wholeField,
            out int removedCount, out int targetSlotId, out int fieldId)
        {
            removedCount = 0;
            targetSlotId = 0;
            fieldId = 0;
            float maxSqr = Mathf.Max(0.15f, maxDistance) * Mathf.Max(0.15f, maxDistance);
            int bestIndex = -1;
            float best = maxSqr;
            ZNetView bestView = null;
            ZDO bestZdo = null;

            WorkFlagRegistry.CollectLoadedFlags(ownerPlayerId, WorkOrder.Forestry, 30f, LoadedFlags);
            for (int f = 0; f < LoadedFlags.Count; f++)
            {
                WorkFlagInteractable flag = LoadedFlags[f];
                if (flag == null) continue;
                ZNetView view = flag.GetNetworkView();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo == null) continue;
                for (int i = 0; i < MaxSlots; i++)
                {
                    if (!zdo.GetBool(Key(i, "persistent"), false)) continue;
                    Vector3 delta = ReadPosition(zdo, i) - position;
                    delta.y = 0f;
                    float sqr = delta.sqrMagnitude;
                    if (sqr > best) continue;
                    best = sqr;
                    bestIndex = i;
                    bestView = view;
                    bestZdo = zdo;
                }
            }
            LoadedFlags.Clear();

            if (bestIndex < 0 || bestZdo == null || !EnsureWritable(bestView))
            {
                LastStatus = "remove-target-missing";
                return false;
            }

            targetSlotId = bestZdo.GetInt(Key(bestIndex, "id"), 0);
            fieldId = bestZdo.GetInt(Key(bestIndex, "field"), 0);
            if (wholeField && fieldId > 0)
            {
                for (int i = 0; i < MaxSlots; i++)
                    if (bestZdo.GetBool(Key(i, "persistent"), false) && bestZdo.GetInt(Key(i, "field"), 0) == fieldId)
                        if (ClearSlot(bestZdo, i)) removedCount++;
            }
            else
            {
                if (ClearSlot(bestZdo, bestIndex)) removedCount = 1;
            }

            RecountPending(bestZdo);
            LastStatus = removedCount > 0 ? $"removed:{removedCount}" : "remove-none";
            return removedCount > 0;
        }

        private static bool UpsertPlannedSlot(ZDO zdo, string seedPrefab, string saplingPrefab, Vector3 position,
            float rotationY, int fieldId, out int slotId)
        {
            slotId = 0;
            int existing = FindPersistentSlot(zdo, position);
            if (existing >= 0)
            {
                slotId = zdo.GetInt(Key(existing, "id"), 0);
                zdo.Set(Key(existing, "seed"), Normalize(seedPrefab));
                zdo.Set(Key(existing, "sapling"), Normalize(saplingPrefab));
                zdo.Set(Key(existing, "rot"), rotationY);
                if (zdo.GetInt(Key(existing, "field"), 0) <= 0) zdo.Set(Key(existing, "field"), fieldId);
                if (!zdo.GetBool(Key(existing, "active"), false))
                {
                    zdo.Set(Key(existing, "active"), true);
                    zdo.Set(CountKey, Mathf.Clamp(zdo.GetInt(CountKey, 0) + 1, 0, MaxSlots));
                }
                LastPendingCount = zdo.GetInt(CountKey, 0);
                LastStatus = $"planned-updated#{slotId}";
                return slotId > 0;
            }

            int free = -1;
            for (int i = 0; i < MaxSlots; i++)
                if (!zdo.GetBool(Key(i, "persistent"), false)) { free = i; break; }
            if (free < 0)
            {
                LastStatus = "planned-queue-full";
                return false;
            }

            slotId = Mathf.Max(1, zdo.GetInt(NextIdKey, 1));
            zdo.Set(NextIdKey, slotId == int.MaxValue ? 1 : slotId + 1);
            zdo.Set(Key(free, "persistent"), true);
            zdo.Set(Key(free, "active"), true);
            zdo.Set(Key(free, "id"), slotId);
            zdo.Set(Key(free, "field"), fieldId);
            zdo.Set(Key(free, "seed"), Normalize(seedPrefab));
            zdo.Set(Key(free, "sapling"), Normalize(saplingPrefab));
            zdo.Set(Key(free, "x"), position.x);
            zdo.Set(Key(free, "y"), position.y);
            zdo.Set(Key(free, "z"), position.z);
            zdo.Set(Key(free, "rot"), rotationY);
            zdo.Set(Key(free, "bg_state"), 0);
            zdo.Set(Key(free, "bg_ready"), 0L);
            zdo.Set(Key(free, "bg_seed_budget"), 0);
            zdo.Set(Key(free, "bg_seed_debt"), 0);
            zdo.Set(Key(free, "bg_virtual"), false);
            zdo.Set(Key(free, "physical_planted_at"), 0L);
            int count = Mathf.Clamp(zdo.GetInt(CountKey, 0) + 1, 0, MaxSlots);
            zdo.Set(CountKey, count);
            LastPendingCount = count;
            LastStatus = $"planned#{slotId}:{Normalize(saplingPrefab)}";
            return true;
        }


        internal static int ReconcileLoadedPersistentSlots(ZDO workerZdo)
        {
            // The player's forestry plan is permanent until explicitly deleted. Planting a tree
            // only fulfils/deactivates the slot temporarily. If the living Plant/TreeBase later
            // disappears (player chop, worker chop, damage, reload edge case, etc.), reactivate
            // the same stored position/species automatically. This is a local loaded-workzone
            // reconciliation; it never scans the world.
            if (workerZdo == null || !TryGetFlag(workerZdo, true, out _, out ZDO flagZdo)) return 0;

            long nowTicks = DateTime.UtcNow.Ticks;
            long graceTicks = TimeSpan.FromSeconds(5).Ticks;
            int reactivated = 0;

            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "persistent"), false) ||
                    flagZdo.GetBool(Key(i, "active"), false))
                    continue;

                // Background-only trees intentionally have no physical object until they are
                // materialized when the loaded workzone becomes active again.
                if (flagZdo.GetBool(Key(i, "bg_virtual"), false)) continue;

                long plantedAt = flagZdo.GetLong(Key(i, "physical_planted_at"), 0L);
                if (plantedAt > 0L && nowTicks - plantedAt < graceTicks) continue;

                Vector3 position = ReadPosition(flagZdo, i);
                if (ForestryTargetFinder.HasLivingPlannedOccupant(position)) continue;

                flagZdo.Set(Key(i, "active"), true);
                flagZdo.Set(Key(i, "bg_state"), 0);
                flagZdo.Set(Key(i, "bg_ready"), 0L);
                flagZdo.Set(Key(i, "bg_virtual"), false);
                flagZdo.Set(Key(i, "physical_planted_at"), 0L);
                reactivated++;
            }

            if (reactivated > 0)
            {
                RecountPending(flagZdo);
                LastStatus = $"persistent-replant-reactivated:{reactivated}";
            }
            return reactivated;
        }


        internal static int CollectBackgroundSlots(ZDO workerZdo, List<ForestryBackgroundSlot> slots)
        {
            if (slots == null) return 0;
            slots.Clear();
            if (!TryGetBackgroundFlag(workerZdo, out ZDO flagZdo)) return 0;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "persistent"), false)) continue;
                int id = flagZdo.GetInt(Key(i, "id"), 0);
                string seed = flagZdo.GetString(Key(i, "seed"), string.Empty);
                string sapling = flagZdo.GetString(Key(i, "sapling"), string.Empty);
                if (id <= 0 || string.IsNullOrEmpty(seed) || string.IsNullOrEmpty(sapling)) continue;
                slots.Add(new ForestryBackgroundSlot(
                    id,
                    seed,
                    sapling,
                    ReadPosition(flagZdo, i),
                    flagZdo.GetFloat(Key(i, "rot"), 0f),
                    flagZdo.GetBool(Key(i, "active"), false),
                    Mathf.Clamp(flagZdo.GetInt(Key(i, "bg_state"), 0), 0, 2),
                    flagZdo.GetLong(Key(i, "bg_ready"), 0L),
                    Mathf.Max(0, flagZdo.GetInt(Key(i, "bg_seed_budget"), 0)),
                    Mathf.Max(0, flagZdo.GetInt(Key(i, "bg_seed_debt"), 0)),
                    flagZdo.GetBool(Key(i, "bg_virtual"), false)));
            }
            return slots.Count;
        }

        internal static void EnsureLoadedBackgroundBaseline(ZDO workerZdo, long nowTicks)
        {
            if (!TryGetBackgroundFlag(workerZdo, out ZDO flagZdo)) return;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "persistent"), false)) continue;
                bool active = flagZdo.GetBool(Key(i, "active"), false);
                int state = Mathf.Clamp(flagZdo.GetInt(Key(i, "bg_state"), 0), 0, 2);
                if (active)
                {
                    if (!flagZdo.GetBool(Key(i, "bg_virtual"), false))
                    {
                        flagZdo.Set(Key(i, "bg_state"), 0);
                        flagZdo.Set(Key(i, "bg_ready"), 0L);
                    }
                    continue;
                }
                if (state != 0) continue;
                string sapling = flagZdo.GetString(Key(i, "sapling"), string.Empty);
                double growSeconds = ForestryWorkRuntime.EstimateGrowthSeconds(sapling);
                flagZdo.Set(Key(i, "bg_state"), 1);
                flagZdo.Set(Key(i, "bg_ready"), nowTicks + TimeSpan.FromSeconds(growSeconds).Ticks);
            }
        }

        internal static int BackgroundPlantAvailableSlots(ZDO workerZdo, long nowTicks, float efficiency)
        {
            if (!TryGetBackgroundFlag(workerZdo, out ZDO flagZdo)) return 0;
            long nextPlant = flagZdo.GetLong("vw_forestry_bg_next_plant", 0L);
            if (nextPlant > nowTicks) return 0;

            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "persistent"), false) ||
                    !flagZdo.GetBool(Key(i, "active"), false)) continue;
                int budget = Mathf.Max(0, flagZdo.GetInt(Key(i, "bg_seed_budget"), 0));
                if (budget <= 0) continue;

                string sapling = flagZdo.GetString(Key(i, "sapling"), string.Empty);
                if (string.IsNullOrEmpty(sapling)) continue;
                double growSeconds = ForestryWorkRuntime.EstimateGrowthSeconds(sapling);

                flagZdo.Set(Key(i, "active"), false);
                flagZdo.Set(Key(i, "bg_seed_budget"), budget - 1);
                flagZdo.Set(Key(i, "bg_seed_debt"), Mathf.Max(0, flagZdo.GetInt(Key(i, "bg_seed_debt"), 0)) + 1);
                flagZdo.Set(Key(i, "bg_virtual"), true);
                flagZdo.Set(Key(i, "physical_planted_at"), 0L);
                flagZdo.Set(Key(i, "bg_state"), 1);
                flagZdo.Set(Key(i, "bg_ready"), nowTicks + TimeSpan.FromSeconds(growSeconds).Ticks);
                flagZdo.Set(CountKey, Mathf.Max(0, flagZdo.GetInt(CountKey, 0) - 1));

                double interval = 8.0 / Math.Max(0.1, Mathf.Clamp01(efficiency));
                flagZdo.Set("vw_forestry_bg_next_plant", nowTicks + TimeSpan.FromSeconds(interval).Ticks);
                LastStatus = $"background-planted#{flagZdo.GetInt(Key(i, "id"), 0)}:{sapling}";
                return 1;
            }
            return 0;
        }

        internal static int AdvanceBackgroundGrowth(ZDO workerZdo, long nowTicks, out int growing, out int mature)
        {
            growing = 0;
            mature = 0;
            int newlyMature = 0;
            if (!TryGetBackgroundFlag(workerZdo, out ZDO flagZdo)) return 0;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "persistent"), false) ||
                    flagZdo.GetBool(Key(i, "active"), false)) continue;
                int state = Mathf.Clamp(flagZdo.GetInt(Key(i, "bg_state"), 0), 0, 2);
                if (state == 1)
                {
                    long ready = flagZdo.GetLong(Key(i, "bg_ready"), 0L);
                    if (ready > 0L && ready <= nowTicks)
                    {
                        state = 2;
                        flagZdo.Set(Key(i, "bg_state"), 2);
                        newlyMature++;
                    }
                }
                if (state == 1) growing++;
                else if (state == 2) mature++;
            }
            return newlyMature;
        }

        internal static bool TryHarvestMatureBackground(ZDO workerZdo, out ForestryBackgroundSlot harvested)
        {
            harvested = default;
            if (!TryGetBackgroundFlag(workerZdo, out ZDO flagZdo)) return false;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!flagZdo.GetBool(Key(i, "persistent"), false) ||
                    flagZdo.GetBool(Key(i, "active"), false) ||
                    flagZdo.GetInt(Key(i, "bg_state"), 0) != 2) continue;

                int id = flagZdo.GetInt(Key(i, "id"), 0);
                string seed = flagZdo.GetString(Key(i, "seed"), string.Empty);
                string sapling = flagZdo.GetString(Key(i, "sapling"), string.Empty);
                harvested = new ForestryBackgroundSlot(
                    id, seed, sapling, ReadPosition(flagZdo, i), flagZdo.GetFloat(Key(i, "rot"), 0f),
                    false, 2, flagZdo.GetLong(Key(i, "bg_ready"), 0L),
                    Mathf.Max(0, flagZdo.GetInt(Key(i, "bg_seed_budget"), 0)),
                    Mathf.Max(0, flagZdo.GetInt(Key(i, "bg_seed_debt"), 0)),
                    flagZdo.GetBool(Key(i, "bg_virtual"), false));

                flagZdo.Set(Key(i, "active"), true);
                flagZdo.Set(Key(i, "bg_state"), 0);
                flagZdo.Set(Key(i, "bg_ready"), 0L);
                flagZdo.Set(Key(i, "bg_virtual"), false);
                flagZdo.Set(Key(i, "physical_planted_at"), 0L);
                flagZdo.Set(CountKey, Mathf.Clamp(flagZdo.GetInt(CountKey, 0) + 1, 0, MaxSlots));
                LastStatus = $"background-harvested#{id}";
                return true;
            }
            return false;
        }

        internal static void SetBackgroundSeedBudget(ZDO workerZdo, int slotId, int budget)
        {
            if (!TryGetBackgroundFlag(workerZdo, out ZDO flagZdo)) return;
            int i = FindSlotById(flagZdo, slotId);
            if (i < 0) return;
            flagZdo.Set(Key(i, "bg_seed_budget"), Mathf.Max(0, budget));
        }

        internal static void ReduceBackgroundSeedDebt(ZDO workerZdo, int slotId, int amount)
        {
            if (amount <= 0 || !TryGetBackgroundFlag(workerZdo, out ZDO flagZdo)) return;
            int i = FindSlotById(flagZdo, slotId);
            if (i < 0) return;
            int debt = Mathf.Max(0, flagZdo.GetInt(Key(i, "bg_seed_debt"), 0));
            flagZdo.Set(Key(i, "bg_seed_debt"), Mathf.Max(0, debt - amount));
        }

        internal static void MarkVirtualPlantMaterialized(ZDO workerZdo, int slotId)
        {
            if (!TryGetBackgroundFlag(workerZdo, out ZDO flagZdo)) return;
            int i = FindSlotById(flagZdo, slotId);
            if (i < 0) return;
            flagZdo.Set(Key(i, "bg_virtual"), false);
            flagZdo.Set(Key(i, "physical_planted_at"), DateTime.UtcNow.Ticks);
            // Physical Plant now owns growth; clear the virtual maturity clock.
            flagZdo.Set(Key(i, "bg_state"), 0);
            flagZdo.Set(Key(i, "bg_ready"), 0L);
        }

        internal static int GetBackgroundPendingCount(ZDO workerZdo)
        {
            if (!TryGetBackgroundFlag(workerZdo, out ZDO flagZdo)) return 0;
            return Mathf.Max(0, flagZdo.GetInt(CountKey, 0));
        }

        internal static int GetBackgroundGrowingCount(ZDO workerZdo)
        {
            return CountBackgroundState(workerZdo, 1);
        }

        internal static int GetBackgroundMatureCount(ZDO workerZdo)
        {
            return CountBackgroundState(workerZdo, 2);
        }

        internal static int GetBackgroundSeedBudgetTotal(ZDO workerZdo)
        {
            return SumBackgroundField(workerZdo, "bg_seed_budget");
        }

        internal static int GetBackgroundSeedDebtTotal(ZDO workerZdo)
        {
            return SumBackgroundField(workerZdo, "bg_seed_debt");
        }

        private static int CountBackgroundState(ZDO workerZdo, int wanted)
        {
            if (!TryGetBackgroundFlag(workerZdo, out ZDO flagZdo)) return 0;
            int count = 0;
            for (int i = 0; i < MaxSlots; i++)
                if (flagZdo.GetBool(Key(i, "persistent"), false) &&
                    !flagZdo.GetBool(Key(i, "active"), false) &&
                    flagZdo.GetInt(Key(i, "bg_state"), 0) == wanted)
                    count++;
            return count;
        }

        private static int SumBackgroundField(ZDO workerZdo, string field)
        {
            if (!TryGetBackgroundFlag(workerZdo, out ZDO flagZdo)) return 0;
            int total = 0;
            for (int i = 0; i < MaxSlots; i++)
                if (flagZdo.GetBool(Key(i, "persistent"), false))
                    total += Mathf.Max(0, flagZdo.GetInt(Key(i, field), 0));
            return total;
        }

        private static int FindSlotById(ZDO flagZdo, int slotId)
        {
            if (flagZdo == null || slotId <= 0) return -1;
            for (int i = 0; i < MaxSlots; i++)
                if (flagZdo.GetBool(Key(i, "persistent"), false) &&
                    flagZdo.GetInt(Key(i, "id"), 0) == slotId)
                    return i;
            return -1;
        }

        private static bool TryGetBackgroundFlag(ZDO workerZdo, out ZDO flagZdo)
        {
            flagZdo = null;
            if (workerZdo == null) return false;
            string id = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(id)) return false;
            if (!WorkFlagRegistry.TryGetPersistentState(id, out flagZdo) || flagZdo == null)
            {
                if (!WorkFlagRegistry.TryGetNetworkState(id, 30f, out _, out flagZdo) || flagZdo == null)
                    return false;
                WorkFlagRegistry.RememberPersistent(flagZdo);
            }
            return WorkFlagZdo.HasOrder(flagZdo, WorkOrder.Forestry);
        }

        private static int FindPersistentSlot(ZDO zdo, Vector3 position)
        {
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!zdo.GetBool(Key(i, "persistent"), false)) continue;
                Vector3 delta = ReadPosition(zdo, i) - position;
                delta.y = 0f;
                if (delta.sqrMagnitude <= 0.45f * 0.45f) return i;
            }
            return -1;
        }

        private static int AllocateFieldId(ZDO zdo)
        {
            int raw = Mathf.Max(1, zdo.GetInt(NextFieldIdKey, 1));
            zdo.Set(NextFieldIdKey, raw == int.MaxValue ? 1 : raw + 1);
            return FieldIdBase + raw;
        }

        private static bool ClearSlot(ZDO zdo, int i)
        {
            if (zdo == null || i < 0 || i >= MaxSlots || !zdo.GetBool(Key(i, "persistent"), false)) return false;
            zdo.Set(Key(i, "persistent"), false);
            zdo.Set(Key(i, "active"), false);
            zdo.Set(Key(i, "id"), 0);
            zdo.Set(Key(i, "field"), 0);
            zdo.Set(Key(i, "seed"), string.Empty);
            zdo.Set(Key(i, "sapling"), string.Empty);
            zdo.Set(Key(i, "x"), 0f); zdo.Set(Key(i, "y"), 0f); zdo.Set(Key(i, "z"), 0f); zdo.Set(Key(i, "rot"), 0f);
            zdo.Set(Key(i, "bg_state"), 0);
            zdo.Set(Key(i, "bg_ready"), 0L);
            zdo.Set(Key(i, "bg_seed_budget"), 0);
            zdo.Set(Key(i, "bg_seed_debt"), 0);
            zdo.Set(Key(i, "bg_virtual"), false);
            zdo.Set(Key(i, "physical_planted_at"), 0L);
            return true;
        }

        private static void RecountPending(ZDO zdo)
        {
            int count = 0;
            for (int i = 0; i < MaxSlots; i++)
                if (zdo.GetBool(Key(i, "persistent"), false) && zdo.GetBool(Key(i, "active"), false)) count++;
            zdo.Set(CountKey, count);
            LastPendingCount = count;
        }

        private static bool TryGetFlag(ZDO workerZdo, bool requireWrite, out ZNetView nview, out ZDO zdo)
        {
            nview = null;
            zdo = null;
            string id = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(id) || !WorkFlagRegistry.TryGetNetworkState(id, 30f, out nview, out zdo)) return false;
            if (!WorkFlagZdo.HasOrder(zdo, WorkOrder.Forestry)) return false;
            return !requireWrite || EnsureWritable(nview);
        }

        private static bool EnsureWritable(ZNetView nview)
        {
            if (nview == null || !nview.IsValid()) return false;
            if (nview.IsOwner()) return true;
            try
            {
                ClaimOwnershipMethod?.Invoke(nview, null);
                return nview.IsOwner();
            }
            catch { return false; }
        }

        private static Vector3 ReadPosition(ZDO zdo, int i) => new Vector3(
            zdo.GetFloat(Key(i, "x"), 0f), zdo.GetFloat(Key(i, "y"), 0f), zdo.GetFloat(Key(i, "z"), 0f));
        private static string Key(int i, string field) => $"vw_forestry_slot_{i}_{field}";
        private static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            int clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
            return (clone >= 0 ? name.Substring(0, clone) : name).Trim();
        }
    }
}
