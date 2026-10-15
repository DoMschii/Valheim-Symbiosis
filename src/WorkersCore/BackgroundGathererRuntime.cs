using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Conservative unloaded-zone adapter for GatherResources.
    ///
    /// Loaded zones snapshot only real, already-existing loose ItemDrops that the physical Gatherer
    /// is allowed to collect. While the zone is unloaded, workers consume that finite snapshot at a
    /// time-based rate and stage a per-item pending pickup. Nothing is deposited yet.
    ///
    /// When the worker/zone is loaded again, every staged pickup must be reconciled against a real
    /// matching ItemDrop in the world. The item is removed from that world drop and only then added
    /// to a real FieldDepot. This prevents the background pass from duplicating resources that still
    /// exist physically after the zone reloads.
    ///
    /// Crop growth/harvest is deliberately not invented here. Mature plant production belongs to the
    /// Garden background adapter; this pass only models the Gatherer's existing loose-drop phase.
    /// </summary>
    internal static class BackgroundGathererRuntime
    {
        private const string Initialized = "vw_bg_gather_init";
        private const string SlotCount = "vw_bg_gather_slots";
        private const string FractionalItems = "vw_bg_gather_fraction";
        private const string LastSnapshotTicks = "vw_bg_gather_snapshot_ticks";
        private const string LastObservedItems = "vw_bg_gather_observed_items";
        private const string LastStatusKey = "vw_bg_gather_last_status";
        private const int MaxLedgerSlots = 24;
        private const float LoadedSnapshotInterval = 6f;

        // Physical collection is fast, but offline simulation should remain conservative and never
        // outperform an attended field. Shared 80% efficiency => ~144 item units/hour/worker.
        internal const float ItemUnitsPerHourAtFullEfficiency = 180f;

        private static readonly Dictionary<string, float> NextLoadedSnapshot = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> ScanCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        internal static void TickLoaded(Character worker, ZDO workerZdo)
        {
            if (worker == null || workerZdo == null || !WorkerZdo.IsWorker(workerZdo)) return;
            if ((WorkerJobProfileRuntime.ResolveEffectiveGathererTasks(workerZdo) & WorkerTaskFlags.Gather) == 0) return;
            if (GatherHarvestCategoryFilterRuntime.HasCustomGatherFilter(workerZdo)) return;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius)) return;
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId)) return;

            if (!WorkFlagRegistry.TryGetNetworkState(areaId, radius, out _, out ZDO flagZdo) || flagZdo == null) return;
            WorkFlagRegistry.RememberPersistent(flagZdo);

            // Reconcile staged background pickups first. Until pending debt is cleared, never refresh
            // the snapshot from the still-visible world drops or those same items could be counted twice.
            int pendingAfterReconcile = ReconcilePending(worker, workerZdo, flagZdo, center, radius);
            if (pendingAfterReconcile > 0)
            {
                flagZdo.Set(LastStatusKey, $"reconcile-pending:{pendingAfterReconcile}");
                return;
            }

            if (NextLoadedSnapshot.TryGetValue(areaId, out float next) && Time.realtimeSinceStartup < next) return;
            NextLoadedSnapshot[areaId] = Time.realtimeSinceStartup + LoadedSnapshotInterval;

            ScanCounts.Clear();
            int total = 0;
            WorkerWorldIndex.ForEachItemDrop(center, Mathf.Max(5f, radius), drop =>
            {
                if (!GathererTargetFinder.IsEligible(drop)) return;
                string prefab = GathererTargetFinder.GetCargoPrefabName(drop);
                if (string.IsNullOrEmpty(prefab) || drop.m_itemData == null) return;
                int amount = Mathf.Max(0, drop.m_itemData.m_stack);
                if (amount <= 0) return;
                if (ScanCounts.TryGetValue(prefab, out int existing)) ScanCounts[prefab] = existing + amount;
                else ScanCounts[prefab] = amount;
                total += amount;
            });

            WriteFreshSnapshot(flagZdo, ScanCounts);
            flagZdo.Set(Initialized, true);
            flagZdo.Set(LastObservedItems, total);
            flagZdo.Set(LastSnapshotTicks, DateTime.UtcNow.Ticks);
            flagZdo.Set(LastStatusKey, $"loaded-snapshot:{total}");
        }

        internal static string TickBackground(ZDO workerZdo, double deltaSeconds, float efficiency)
        {
            if (workerZdo == null || deltaSeconds <= 0.0) return "gather:waiting";
            if (GatherHarvestCategoryFilterRuntime.HasCustomGatherFilter(workerZdo)) return "gather:custom-filter-physical-only";
            if (WorkerZdo.HasCargo(workerZdo)) return "gather:worker-cargo-awaits-physical-delivery";

            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId)) return "gather:no-workzone";
            if (!WorkFlagRegistry.TryGetPersistentState(areaId, out ZDO flagZdo) || flagZdo == null) return "gather:no-ledger-flag";
            if (!flagZdo.GetBool(Initialized, false)) return "gather:no-loaded-snapshot";

            int available = CountRemaining(flagZdo);
            bool hasDemandedLooseItem = false;
            for (int i = 0; i < MaxLedgerSlots; i++)
            {
                string prefab = flagZdo.GetString(Key(i, "prefab"), string.Empty);
                int remaining = Mathf.Max(0, flagZdo.GetInt(Key(i, "remaining"), 0));
                if (remaining > 0 && !string.IsNullOrEmpty(prefab) && ProductionDemandRuntime.AllowsPrefab(workerZdo, prefab))
                {
                    hasDemandedLooseItem = true;
                    break;
                }
            }
            if (available > 0 && !hasDemandedLooseItem) return "gather:waiting-stock-max";

            float fraction = Mathf.Max(0f, flagZdo.GetFloat(FractionalItems, 0f));
            fraction += (float)(deltaSeconds * (ItemUnitsPerHourAtFullEfficiency / 3600.0) * Mathf.Clamp01(efficiency));
            int units = Mathf.FloorToInt(fraction);
            int collected = 0;

            if (units > 0)
            {
                for (int i = 0; i < MaxLedgerSlots && units > 0; i++)
                {
                    string prefab = flagZdo.GetString(Key(i, "prefab"), string.Empty);
                    if (string.IsNullOrEmpty(prefab)) continue;
                    if (!ProductionDemandRuntime.AllowsPrefab(workerZdo, prefab)) continue;
                    int remaining = Mathf.Max(0, flagZdo.GetInt(Key(i, "remaining"), 0));
                    if (remaining <= 0) continue;
                    int take = Mathf.Min(remaining, units);
                    remaining -= take;
                    units -= take;
                    collected += take;
                    fraction -= take;
                    flagZdo.Set(Key(i, "remaining"), remaining);
                    flagZdo.Set(Key(i, "pending"), Mathf.Max(0, flagZdo.GetInt(Key(i, "pending"), 0)) + take);
                }
            }

            flagZdo.Set(FractionalItems, Mathf.Max(0f, fraction));
            if (collected > 0)
                WorkerJobProfileRuntime.NoteCompletedAction(workerZdo, WorkerRole.Gatherer, WorkOrder.GatherResources);
            int left = CountRemaining(flagZdo);
            int pending = CountPending(flagZdo);
            float rate = ItemUnitsPerHourAtFullEfficiency * Mathf.Clamp01(efficiency);
            string status = $"gather:bg loose~{left}, collected={collected}, pending={pending}, rate~{rate:0}/h";
            flagZdo.Set(LastStatusKey, status);
            return status;
        }

        internal static bool HasBackgroundWork(ZDO workerZdo)
        {
            if (workerZdo == null) return false;
            if (GatherHarvestCategoryFilterRuntime.HasCustomGatherFilter(workerZdo)) return false;
            if (WorkerZdo.HasCargo(workerZdo)) return false;
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId)) return false;
            if (!WorkFlagRegistry.TryGetPersistentState(areaId, out ZDO flagZdo) || flagZdo == null) return false;
            if (!flagZdo.GetBool(Initialized, false)) return false;
            for (int i = 0; i < MaxLedgerSlots; i++)
            {
                string prefab = flagZdo.GetString(Key(i, "prefab"), string.Empty);
                int remaining = Mathf.Max(0, flagZdo.GetInt(Key(i, "remaining"), 0));
                if (remaining > 0 && !string.IsNullOrEmpty(prefab) && ProductionDemandRuntime.AllowsPrefab(workerZdo, prefab))
                    return true;
            }
            return false;
        }

        internal static string GetDebugSummary(ZDO workerZdo)
        {
            if (workerZdo == null) return "gather-ledger:none";
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId) || !WorkFlagRegistry.TryGetPersistentState(areaId, out ZDO flagZdo) || flagZdo == null)
                return "gather-ledger:no-flag";
            if (!flagZdo.GetBool(Initialized, false)) return "gather-ledger:no-snapshot";
            return $"gather-ledger observed={flagZdo.GetInt(LastObservedItems, 0)}, remaining={CountRemaining(flagZdo)}, pending={CountPending(flagZdo)}, status={flagZdo.GetString(LastStatusKey, "idle")}";
        }

        private static int ReconcilePending(Character worker, ZDO workerZdo, ZDO flagZdo, Vector3 center, float radius)
        {
            int totalPending = CountPending(flagZdo);
            if (totalPending <= 0) return 0;

            for (int i = 0; i < MaxLedgerSlots; i++)
            {
                string prefab = flagZdo.GetString(Key(i, "prefab"), string.Empty);
                int pending = Mathf.Max(0, flagZdo.GetInt(Key(i, "pending"), 0));
                if (string.IsNullOrEmpty(prefab) || pending <= 0) continue;

                if (!WorkerStorageRuntime.TryFindFieldDepotForCargo(worker, workerZdo, prefab, out Container depot, out _) || depot == null)
                    continue;
                if (!WorkerStorageRuntime.TryOwn(depot)) continue;
                Inventory inventory = depot.GetInventory();
                if (inventory == null) continue;

                while (pending > 0)
                {
                    ItemDrop drop = FindMatchingLooseDrop(center, radius, prefab);
                    if (drop == null || drop.m_itemData == null) break;
                    ZNetView nview = drop.GetComponent<ZNetView>();
                    if (nview == null || !nview.IsValid() || nview.GetZDO() == null) break;
                    if (!nview.IsOwner())
                    {
                        drop.RequestOwn();
                        break;
                    }

                    GameObject itemPrefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null;
                    if (itemPrefab == null && drop.m_itemData.m_dropPrefab != null) itemPrefab = drop.m_itemData.m_dropPrefab;
                    ItemDrop template = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
                    ItemDrop.ItemData source = template?.m_itemData ?? drop.m_itemData;
                    if (source == null) break;

                    ItemDrop.ItemData one = source.Clone();
                    if (itemPrefab != null) one.m_dropPrefab = itemPrefab;
                    one.m_stack = 1;

                    // Reserve destination space first. If world removal fails, roll the reservation
                    // back immediately before persisting the container.
                    if (!WorkerStorageRuntime.TryAddItem(inventory, one)) break;
                    if (!drop.RemoveOne())
                    {
                        ItemDrop.ItemData rollback = WorkerStorageRuntime.FindPrefab(inventory, prefab);
                        if (rollback != null) WorkerStorageRuntime.TryRemoveOne(inventory, rollback);
                        break;
                    }

                    pending--;
                    flagZdo.Set(Key(i, "pending"), pending);
                    WorkerStorageRuntime.PersistContainer(depot, inventory);
                }
            }

            return CountPending(flagZdo);
        }

        private static ItemDrop FindMatchingLooseDrop(Vector3 center, float radius, string wantedPrefab)
        {
            ItemDrop best = null;
            float bestSqr = float.MaxValue;
            WorkerWorldIndex.ForEachItemDrop(center, Mathf.Max(5f, radius), drop =>
            {
                if (!GathererTargetFinder.IsEligible(drop)) return;
                if (!string.Equals(GathererTargetFinder.GetCargoPrefabName(drop), wantedPrefab, StringComparison.OrdinalIgnoreCase)) return;
                float sqr = (drop.transform.position - center).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                best = drop;
            });
            return best;
        }

        private static void WriteFreshSnapshot(ZDO flagZdo, Dictionary<string, int> observed)
        {
            for (int i = 0; i < MaxLedgerSlots; i++)
            {
                flagZdo.Set(Key(i, "prefab"), string.Empty);
                flagZdo.Set(Key(i, "observed"), 0);
                flagZdo.Set(Key(i, "remaining"), 0);
                flagZdo.Set(Key(i, "pending"), 0);
            }

            int slot = 0;
            foreach (KeyValuePair<string, int> pair in observed)
            {
                if (slot >= MaxLedgerSlots) break;
                int amount = Mathf.Max(0, pair.Value);
                if (amount <= 0 || string.IsNullOrEmpty(pair.Key)) continue;
                flagZdo.Set(Key(slot, "prefab"), pair.Key);
                flagZdo.Set(Key(slot, "observed"), amount);
                flagZdo.Set(Key(slot, "remaining"), amount);
                slot++;
            }
            flagZdo.Set(SlotCount, slot);
            flagZdo.Set(FractionalItems, 0f);
        }

        private static int CountRemaining(ZDO flagZdo)
        {
            int total = 0;
            for (int i = 0; i < MaxLedgerSlots; i++) total += Mathf.Max(0, flagZdo.GetInt(Key(i, "remaining"), 0));
            return total;
        }

        private static int CountPending(ZDO flagZdo)
        {
            int total = 0;
            for (int i = 0; i < MaxLedgerSlots; i++) total += Mathf.Max(0, flagZdo.GetInt(Key(i, "pending"), 0));
            return total;
        }

        private static string Key(int slot, string field) => $"vw_bg_gather_{slot}_{field}";
    }
}
