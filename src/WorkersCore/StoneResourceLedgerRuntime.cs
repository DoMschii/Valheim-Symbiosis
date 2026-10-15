using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// First persistent resource-ledger adapter for unloaded workzones.
    /// The flag owns the ledger so multiple stonemasons share one finite stock.
    /// Loaded zones periodically snapshot real stone targets; unloaded zones consume that
    /// persisted stock at a conservative rate. Produced Stone is staged on the flag and is
    /// flushed into the real FieldDepot the next time that outpost is loaded.
    /// </summary>
    internal static class StoneResourceLedgerRuntime
    {
        private const string Initialized = "vw_bg_stone_init";
        private const string SnapshotUnits = "vw_bg_stone_snapshot";
        private const string RemainingUnits = "vw_bg_stone_remaining";
        private const string PendingStone = "vw_bg_stone_pending";
        private const string FractionalStone = "vw_bg_stone_fraction";
        private const string LastSnapshotTicks = "vw_bg_stone_snapshot_ticks";
        private const string LastObservedUnits = "vw_bg_stone_observed";

        // User design target: roughly 1000 stone / 12 h at 100% simulation speed.
        internal const float StonePerHourAtFullEfficiency = 1000f / 12f;
        private const int EstimatedStonePerUsablePatch = 6;
        private const float LoadedSnapshotInterval = 97f;

        private static readonly Dictionary<string, float> NextLoadedSnapshot = new Dictionary<string, float>(StringComparer.Ordinal);

        internal static void TickLoaded(Character worker, ZDO workerZdo)
        {
            if (worker == null || workerZdo == null || !WorkerZdo.IsWorker(workerZdo)) return;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out WorkOrder order, out Vector3 center, out float radius) || order != WorkOrder.Stonework) return;
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

            int tier = Mathf.Max(1, WorkerSkillCatalog.GetStoneworkTier(worker));
            StoneTargetResult scan = StoneTargetFinder.FindNearest(center, radius, center, tier);
            int observed = Mathf.Max(0, scan.Candidates * EstimatedStonePerUsablePatch);

            if (!flagZdo.GetBool(Initialized, false))
            {
                flagZdo.Set(Initialized, true);
                flagZdo.Set(SnapshotUnits, observed);
                flagZdo.Set(RemainingUnits, observed);
                flagZdo.Set(PendingStone, 0);
                flagZdo.Set(FractionalStone, 0f);
            }
            else
            {
                // v0.4.43: Stone patches cannot yet be removed from the real world while the zone is
                // unloaded. Therefore a background-depleted ledger must not permanently block a
                // physically loaded quarry. While the zone is loaded, the real scan is authoritative.
                // Background stone production is paused below until persistent world depletion exists.
                flagZdo.Set(RemainingUnits, observed);
                flagZdo.Set(SnapshotUnits, observed);
            }

            flagZdo.Set(LastObservedUnits, observed);
            flagZdo.Set(LastSnapshotTicks, DateTime.UtcNow.Ticks);
            TryFlushPending(worker, workerZdo);
        }

        internal static string TickBackground(ZDO workerZdo, double deltaSeconds, float efficiency)
        {
            if (workerZdo == null || deltaSeconds <= 0.0) return "stone:waiting";
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId)) return "stone:no-workzone";
            if (!WorkFlagRegistry.TryGetPersistentState(areaId, out ZDO flagZdo) || flagZdo == null)
                return "stone:no-ledger-flag";
            if (!flagZdo.GetBool(Initialized, false)) return "stone:no-snapshot";

            int remaining = Mathf.Max(0, flagZdo.GetInt(RemainingUnits, 0));
            int staged = Mathf.Max(0, flagZdo.GetInt(PendingStone, 0));

            // v0.4.43 safety: do not manufacture Stone offscreen while the corresponding physical
            // MineRock/MineRock5 patches remain untouched in the world. Otherwise the same quarry can
            // be harvested a second time when it loads. Resume background quarrying only once we have
            // persistent per-rock depletion/removal.
            flagZdo.Set(FractionalStone, 0f);
            return $"stone:bg paused-physical-depletion remaining~{remaining}, pending={staged}";
        }

        internal static bool TryGetRemaining(ZDO workerZdo, out int remaining)
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
            remaining = Mathf.Max(0, flagZdo.GetInt(RemainingUnits, 0));
            return true;
        }

        internal static string GetDebugSummary(ZDO workerZdo)
        {
            if (workerZdo == null) return "stone-ledger:none";
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId) || !WorkFlagRegistry.TryGetPersistentState(areaId, out ZDO flagZdo) || flagZdo == null)
                return "stone-ledger:no-flag";
            if (!flagZdo.GetBool(Initialized, false)) return "stone-ledger:no-snapshot";
            return $"stone-ledger observed~{flagZdo.GetInt(LastObservedUnits, 0)}, remaining~{flagZdo.GetInt(RemainingUnits, 0)}, pending={flagZdo.GetInt(PendingStone, 0)}";
        }

        private static void TryFlushPending(Character worker, ZDO workerZdo)
        {
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId) || !WorkFlagRegistry.TryGetPersistentState(areaId, out ZDO flagZdo) || flagZdo == null) return;
            int pending = Mathf.Max(0, flagZdo.GetInt(PendingStone, 0));
            if (pending <= 0) return;

            if (!WorkerStorageRuntime.TryFindFieldDepotForCargo(worker, workerZdo, "Stone", out Container depot, out _) || depot == null) return;
            Inventory inventory = depot.GetInventory();
            if (inventory == null) return;
            GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Stone") : null;
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
                flagZdo.Set(PendingStone, left);
            }
        }
    }
}
