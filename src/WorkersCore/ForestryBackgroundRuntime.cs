
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    internal static class ForestryBackgroundRuntime
    {
        private const float LoadedSnapshotInterval = 83f;
        private static readonly Dictionary<string, float> NextLoadedSnapshot =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReconciledLoadedAreas =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<ForestryBackgroundSlot> Slots = new List<ForestryBackgroundSlot>(192);
        private static readonly Dictionary<string, int> AvailableBySeed =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<int>> SlotIndicesBySeed =
            new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        internal static void TickLoaded(Character worker, ZDO workerZdo)
        {
            if (worker == null || workerZdo == null || !WorkerZdo.IsWorker(workerZdo)) return;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out WorkOrder order, out _, out _)) return;
            if (order != WorkOrder.Forestry && order != WorkOrder.FellTrees) return;

            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId)) return;

            // Safety reconciliation is intentionally one-shot per loaded workzone/session. Normal
            // replanting is event-driven when the real stump is destroyed, so persistent forestry
            // slots are not physics-checked every snapshot interval. The one-shot pass only repairs
            // legacy/save/reload edge cases where an old build missed the destruction event.
            if (ReconciledLoadedAreas.Add(areaId))
            {
                int restoredSlots = ForestryPlanRuntime.ReconcileLoadedPersistentSlots(workerZdo);
                if (restoredSlots > 0)
                    Debug.Log($"Valheim_Workers Forestry persistent plan one-shot repaired {restoredSlots} missing tree slot(s) in loaded workzone {areaId}.");
            }

            if (NextLoadedSnapshot.TryGetValue(areaId, out float next) && Time.realtimeSinceStartup < next) return;
            NextLoadedSnapshot[areaId] = Time.realtimeSinceStartup + LoadedSnapshotInterval;

            Slots.Clear();
            if (ForestryPlanRuntime.CollectBackgroundSlots(workerZdo, Slots) <= 0) return;

            for (int i = 0; i < Slots.Count; i++)
            {
                ForestryBackgroundSlot slot = Slots[i];
                if (slot.SeedDebt <= 0 || string.IsNullOrEmpty(slot.SeedPrefab)) continue;
                int removed = 0;
                while (removed < slot.SeedDebt &&
                       WorkerStorageRuntime.TryConsumeBackgroundResource(worker, workerZdo, slot.SeedPrefab, 1))
                    removed++;
                if (removed > 0)
                    ForestryPlanRuntime.ReduceBackgroundSeedDebt(workerZdo, slot.SlotId, removed);
            }

            Slots.Clear();
            ForestryPlanRuntime.CollectBackgroundSlots(workerZdo, Slots);

            for (int i = 0; i < Slots.Count; i++)
            {
                ForestryBackgroundSlot slot = Slots[i];
                if (!slot.VirtualPlant || slot.Active || slot.SeedDebt > 0) continue;
                GameObject prefab = ForestryWorkRuntime.ResolveSaplingPrefab(slot.SaplingPrefab);
                if (prefab == null) continue;
                if (!ForestryTargetFinder.IsPlannedSlotPhysicallyFree(slot.Position)) continue;

                try
                {
                    GameObject instance = UnityEngine.Object.Instantiate(
                        prefab,
                        slot.Position,
                        Quaternion.Euler(0f, slot.RotationY, 0f));
                    if (instance != null)
                        ForestryPlanRuntime.MarkVirtualPlantMaterialized(workerZdo, slot.SlotId);
                }
                catch
                {
                }
            }

            AvailableBySeed.Clear();
            SlotIndicesBySeed.Clear();
            Slots.Clear();
            ForestryPlanRuntime.CollectBackgroundSlots(workerZdo, Slots);
            for (int i = 0; i < Slots.Count; i++)
            {
                string seed = Slots[i].SeedPrefab;
                if (string.IsNullOrEmpty(seed)) continue;
                if (!SlotIndicesBySeed.TryGetValue(seed, out List<int> indices))
                {
                    indices = new List<int>();
                    SlotIndicesBySeed[seed] = indices;
                    int total = WorkerStorageRuntime.CountGardenResourceInStorage(worker, workerZdo, seed);
                    AvailableBySeed[seed] = Mathf.Max(0, total - 1);
                }
                indices.Add(i);
            }

            foreach (KeyValuePair<string, List<int>> pair in SlotIndicesBySeed)
            {
                int available = AvailableBySeed.TryGetValue(pair.Key, out int count) ? count : 0;
                int debt = 0;
                for (int n = 0; n < pair.Value.Count; n++)
                    debt += Mathf.Max(0, Slots[pair.Value[n]].SeedDebt);
                available = Mathf.Max(0, available - debt);

                int slotCount = Mathf.Max(1, pair.Value.Count);
                int baseBudget = available / slotCount;
                int remainder = available % slotCount;
                for (int n = 0; n < pair.Value.Count; n++)
                {
                    ForestryBackgroundSlot slot = Slots[pair.Value[n]];
                    int budget = baseBudget + (n < remainder ? 1 : 0);
                    if (slot.SeedDebt > 0) budget = 0;
                    ForestryPlanRuntime.SetBackgroundSeedBudget(workerZdo, slot.SlotId, budget);
                }
            }

            ForestryPlanRuntime.EnsureLoadedBackgroundBaseline(workerZdo, DateTime.UtcNow.Ticks);
        }

        internal static string TickBackground(ZDO workerZdo, double deltaSeconds, float efficiency)
        {
            if (workerZdo == null) return "forestry:no-worker";
            EnsureBackgroundSupplyRequests(workerZdo);
            if (deltaSeconds <= 0.0) return "forestry:waiting";

            long nowTicks = DateTime.UtcNow.Ticks;
            int planted = ForestryPlanRuntime.BackgroundPlantAvailableSlots(workerZdo, nowTicks, Mathf.Clamp01(efficiency));
            int matured = ForestryPlanRuntime.AdvanceBackgroundGrowth(workerZdo, nowTicks, out int growing, out int mature);
            int pending = ForestryPlanRuntime.GetBackgroundPendingCount(workerZdo);
            int budget = ForestryPlanRuntime.GetBackgroundSeedBudgetTotal(workerZdo);
            int debt = ForestryPlanRuntime.GetBackgroundSeedDebtTotal(workerZdo);

            return $"forestry:bg planted={planted}, matured+={matured}, growing={growing}, mature={mature}, pending={pending}, seedBudget={budget}, seedDebt={debt}";
        }

        internal static bool HasBackgroundWork(ZDO workerZdo)
        {
            if (workerZdo == null) return false;
            return ForestryPlanRuntime.GetBackgroundPendingCount(workerZdo) > 0 ||
                   ForestryPlanRuntime.GetBackgroundGrowingCount(workerZdo) > 0;
        }

        private static void EnsureBackgroundSupplyRequests(ZDO workerZdo)
        {
            if (workerZdo == null) return;
            if (!WorkerLogisticsRuntime.TryFindPersistentFieldDepotForWorkArea(workerZdo, out ZDO depotZdo, out _)) return;

            Slots.Clear();
            if (ForestryPlanRuntime.CollectBackgroundSlots(workerZdo, Slots) <= 0) return;
            Dictionary<string, int> wanted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Slots.Count; i++)
            {
                ForestryBackgroundSlot slot = Slots[i];
                // Active means this persistent forestry slot is pending and needs to be planted.
                // v0.3.52 accidentally skipped active slots here, so unloaded outposts never
                // generated seed demand for newly planned trees.
                if (!slot.Active || string.IsNullOrEmpty(slot.SeedPrefab)) continue;
                wanted.TryGetValue(slot.SeedPrefab, out int current);
                wanted[slot.SeedPrefab] = current + 1;
            }

            foreach (KeyValuePair<string, int> pair in wanted)
            {
                int target = Mathf.Max(WorkerLogisticsRuntime.DefaultFieldSupplyTarget, pair.Value);
                WorkerLogisticsRuntime.RequestSupplyForWorkArea(workerZdo, pair.Key, target);
            }
        }

        internal static string GetDebugSummary(ZDO workerZdo)
        {
            if (workerZdo == null) return "forestry-bg:none";
            return $"forestry-bg pending={ForestryPlanRuntime.GetBackgroundPendingCount(workerZdo)}, growing={ForestryPlanRuntime.GetBackgroundGrowingCount(workerZdo)}, mature={ForestryPlanRuntime.GetBackgroundMatureCount(workerZdo)}, seedBudget={ForestryPlanRuntime.GetBackgroundSeedBudgetTotal(workerZdo)}, seedDebt={ForestryPlanRuntime.GetBackgroundSeedDebtTotal(workerZdo)}";
        }
    }
}
