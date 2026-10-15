using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Conservative first-pass wood ledger for unloaded lumberjack workzones.
    /// Loaded zones snapshot real standing TreeBase targets that the assigned lumberjack tier can fell.
    /// Unloaded zones consume that finite tree count at a conservative rate and stage ordinary Wood.
    /// Species-aware FineWood/CoreWood output and forestry regrowth are intentionally left for the
    /// dedicated forestry-background pass; this adapter never pretends those systems are complete.
    /// </summary>
    internal static class WoodResourceLedgerRuntime
    {
        private const string Initialized = "vw_bg_wood_init";
        private const string SnapshotTrees = "vw_bg_wood_snapshot_trees";
        private const string RemainingTrees = "vw_bg_wood_remaining_trees";
        private const string PendingWood = "vw_bg_wood_pending";
        private const string FractionalTrees = "vw_bg_wood_fraction_trees";
        private const string LastObservedTrees = "vw_bg_wood_observed_trees";
        private const string LastManagedTrees = "vw_bg_wood_managed_trees";
        private const string LastSnapshotTicks = "vw_bg_wood_snapshot_ticks";

        // Conservative defaults for the first ledger pass. Physical work remains authoritative.
        // 8 ordinary Wood per completed standing-tree unit, 10 tree units/hour at 100%.
        // With the shared 80% background efficiency this yields ~64 Wood/hour/worker.
        internal const float TreesPerHourAtFullEfficiency = 10f;
        private const int EstimatedWoodPerTree = 8;
        private const float LoadedSnapshotInterval = 89f;

        private static readonly Dictionary<string, float> NextLoadedSnapshot = new Dictionary<string, float>(StringComparer.Ordinal);

        internal static void TickLoaded(Character worker, ZDO workerZdo)
        {
            if (worker == null || workerZdo == null || !WorkerZdo.IsWorker(workerZdo)) return;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out WorkOrder order, out Vector3 center, out float radius) || order != WorkOrder.FellTrees) return;
            if (LumberjackTreeFilterRuntime.HasCustomFilter(workerZdo))
            {
                // v0.4.64 stability rule: the legacy background wood ledger is shared by WorkFlag
                // and has no per-species accounting. Do not let a filtered worker corrupt that
                // shared count. Its selected tree species remain fully authoritative while loaded.
                TryFlushPending(worker, workerZdo);
                return;
            }
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId)) return;

            if (NextLoadedSnapshot.TryGetValue(areaId, out float next) && Time.realtimeSinceStartup < next)
            {
                TryFlushPending(worker, workerZdo);
                return;
            }
            NextLoadedSnapshot[areaId] = Time.realtimeSinceStartup + LoadedSnapshotInterval;

            if (!WorkFlagRegistry.TryGetNetworkState(areaId, radius, out _, out ZDO flagZdo) || flagZdo == null) return;
            WorkFlagRegistry.RememberPersistent(flagZdo);

            int tier = Mathf.Max(1, WorkerSkillCatalog.GetWoodcutTier(worker));
            WoodResourceScan scan = TreeTargetFinder.ScanStandingTrees(center, radius, tier, workerZdo);
            int observed = Mathf.Max(0, scan.StandingTrees);

            if (!flagZdo.GetBool(Initialized, false))
            {
                flagZdo.Set(Initialized, true);
                flagZdo.Set(SnapshotTrees, observed);
                flagZdo.Set(RemainingTrees, observed);
                flagZdo.Set(PendingWood, 0);
                flagZdo.Set(FractionalTrees, 0f);
            }
            else
            {
                int remaining = Mathf.Max(0, flagZdo.GetInt(RemainingTrees, 0));
                // Never refill a background-depleted ledger from a later visual reload. A dedicated
                // forestry reconciliation pass will own sustainable regrowth instead.
                if (observed < remaining) flagZdo.Set(RemainingTrees, observed);
                flagZdo.Set(SnapshotTrees, Mathf.Max(flagZdo.GetInt(SnapshotTrees, observed), observed));
            }

            flagZdo.Set(LastObservedTrees, observed);
            flagZdo.Set(LastManagedTrees, Mathf.Max(0, scan.ManagedStandingTrees));
            flagZdo.Set(LastSnapshotTicks, DateTime.UtcNow.Ticks);
            TryFlushPending(worker, workerZdo);
        }

        internal static string TickBackground(ZDO workerZdo, double deltaSeconds, float efficiency)
        {
            if (workerZdo == null || deltaSeconds <= 0.0) return "wood:waiting";
            if (LumberjackTreeFilterRuntime.HasCustomFilter(workerZdo))
                return "wood:custom-tree-filter-physical-only";
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId)) return "wood:no-workzone";
            if (!WorkFlagRegistry.TryGetPersistentState(areaId, out ZDO flagZdo) || flagZdo == null)
                return "wood:no-ledger-flag";
            if (!flagZdo.GetBool(Initialized, false)) return "wood:no-snapshot";
            if (!ProductionDemandRuntime.AllowsPrefab(workerZdo, "Wood")) return "wood:waiting-stock-max";

            int remaining = Mathf.Max(0, flagZdo.GetInt(RemainingTrees, 0));
            int matureManaged = ForestryPlanRuntime.GetBackgroundMatureCount(workerZdo);
            if (remaining <= 0 && matureManaged <= 0) return "wood:depleted";

            float fraction = Mathf.Max(0f, flagZdo.GetFloat(FractionalTrees, 0f));
            fraction += (float)(deltaSeconds * (TreesPerHourAtFullEfficiency / 3600.0) * Mathf.Clamp01(efficiency));
            int wholeTrees = Mathf.FloorToInt(fraction);
            int managedFelled = 0;
            int wildFelled = 0;
            if (wholeTrees > 0)
            {
                int pending = Mathf.Max(0, flagZdo.GetInt(PendingWood, 0));

                // Planned forestry is preferred over unrelated wild forest, mirroring the physical
                // lumberjack priority. Each mature managed slot completes one abstract tree cycle
                // and is reactivated for the Forester.
                while (wholeTrees > 0 && ForestryPlanRuntime.TryHarvestMatureBackground(workerZdo, out _))
                {
                    wholeTrees--;
                    fraction -= 1f;
                    managedFelled++;
                    pending += EstimatedWoodPerTree;
                }

                if (wholeTrees > 0 && remaining > 0)
                {
                    wildFelled = Mathf.Min(remaining, wholeTrees);
                    remaining -= wildFelled;
                    fraction -= wildFelled;
                    pending += wildFelled * EstimatedWoodPerTree;
                    flagZdo.Set(RemainingTrees, remaining);
                }

                flagZdo.Set(PendingWood, pending);
            }
            flagZdo.Set(FractionalTrees, Mathf.Max(0f, fraction));

            int staged = Mathf.Max(0, flagZdo.GetInt(PendingWood, 0));
            float woodPerHour = TreesPerHourAtFullEfficiency * EstimatedWoodPerTree * Mathf.Clamp01(efficiency);
            int matureAfter = ForestryPlanRuntime.GetBackgroundMatureCount(workerZdo);
            return $"wood:bg wild~{remaining}, managedMature={matureAfter}, felled(managed/wild)={managedFelled}/{wildFelled}, pendingWood={staged}, rate~{woodPerHour:0.0}/h";
        }

        internal static bool TryGetRemainingTrees(ZDO workerZdo, out int remaining)
        {
            remaining = -1;
            if (workerZdo == null) return false;
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId)) return false;
            ZDO flagZdo = null;
            if (!WorkFlagRegistry.TryGetPersistentState(areaId, out flagZdo) || flagZdo == null)
            {
                if (!WorkerZdo.TryReadWorkArea(workerZdo, out _, out _, out float radius) ||
                    !WorkFlagRegistry.TryGetNetworkState(areaId, radius, out _, out flagZdo) || flagZdo == null)
                    return false;
                WorkFlagRegistry.RememberPersistent(flagZdo);
            }
            if (!flagZdo.GetBool(Initialized, false)) return false;
            remaining = Mathf.Max(0, flagZdo.GetInt(RemainingTrees, 0));
            return true;
        }

        internal static string GetDebugSummary(ZDO workerZdo)
        {
            if (workerZdo == null) return "wood-ledger:none";
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId) || !WorkFlagRegistry.TryGetPersistentState(areaId, out ZDO flagZdo) || flagZdo == null)
                return "wood-ledger:no-flag";
            if (!flagZdo.GetBool(Initialized, false)) return "wood-ledger:no-snapshot";
            return $"wood-ledger observedTrees~{flagZdo.GetInt(LastObservedTrees, 0)}, managed~{flagZdo.GetInt(LastManagedTrees, 0)}, remainingTrees~{flagZdo.GetInt(RemainingTrees, 0)}, pendingWood={flagZdo.GetInt(PendingWood, 0)}";
        }

        private static void TryFlushPending(Character worker, ZDO workerZdo)
        {
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId) || !WorkFlagRegistry.TryGetPersistentState(areaId, out ZDO flagZdo) || flagZdo == null) return;
            int pending = Mathf.Max(0, flagZdo.GetInt(PendingWood, 0));
            if (pending <= 0) return;

            if (!WorkerStorageRuntime.TryFindFieldDepotForCargo(worker, workerZdo, "Wood", out Container depot, out _) || depot == null) return;
            Inventory inventory = depot.GetInventory();
            if (inventory == null) return;
            GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Wood") : null;
            ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop?.m_itemData == null) return;

            int left = pending;
            int stackMax = Mathf.Max(1, drop.m_itemData.m_shared?.m_maxStackSize ?? 1);
            while (left > 0)
            {
                int chunk = Mathf.Min(left, stackMax);
                ItemDrop.ItemData data = drop.m_itemData.Clone();
                data.m_dropPrefab = prefab;
                data.m_stack = chunk;
                if (!WorkerStorageRuntime.TryAddItem(inventory, data)) break;
                left -= chunk;
            }
            if (left != pending)
            {
                WorkerStorageRuntime.PersistContainer(depot, inventory);
                flagZdo.Set(PendingWood, left);
            }
        }
    }
}
