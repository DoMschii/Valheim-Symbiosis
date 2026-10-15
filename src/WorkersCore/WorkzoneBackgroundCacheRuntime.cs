using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// v0.5.358: one cheap handoff snapshot per Workzone/task instead of running every
    /// background ledger adapter for every Worker that crosses the player bubble.
    ///
    /// The actual unloaded production remains lazy: no stationary Worker AI runs while the
    /// Workzone is unloaded. Existing persistent ledgers on the WorkFlag remember the last
    /// physically observed budgets/slots and are reconciled once when the zone becomes loaded
    /// again. Transport/Sort/Provision are intentionally not converted into static production.
    /// </summary>
    internal static class WorkzoneBackgroundCacheRuntime
    {
        private const float HandoffDedupSeconds = 3f;
        private const string CacheVersionKey = "vw_bg_zone_cache_version";
        private const string CacheTicksKey = "vw_bg_zone_cache_ticks";
        private const string CacheOrderKey = "vw_bg_zone_cache_order";
        private const string CacheRoleKey = "vw_bg_zone_cache_role";
        private const int CacheVersion = 380;

        private static readonly Dictionary<string, float> NextCapture =
            new Dictionary<string, float>(StringComparer.Ordinal);

        internal static void CaptureRelevantSnapshot(Character worker, ZDO workerZdo)
        {
            if (worker == null || workerZdo == null || !WorkerZdo.IsWorker(workerZdo)) return;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out WorkOrder order, out _, out float radius)) return;

            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId)) return;

            WorkerRole role = WorkerJobProfileRuntime.NormalizeRuntimeRole(workerZdo);

            // Haul and Provision already use persistent cargo/container ledgers and must stay live.
            // Sort needs one loaded snapshot when it is the active module, but should not trigger
            // every unrelated production scanner as the old generic handoff did.
            if (role == WorkerRole.Hauler && order != WorkOrder.Sort)
            {
                Stamp(areaId, radius, order, role);
                return;
            }

            string dedupKey = areaId + "\u001f" + ((int)order).ToString();
            float now = Time.realtimeSinceStartup;
            if (NextCapture.TryGetValue(dedupKey, out float next) && now < next)
            {
                Stamp(areaId, radius, order, role);
                return;
            }
            NextCapture[dedupKey] = now + HandoffDedupSeconds;

            switch (order)
            {
                case WorkOrder.FellTrees:
                    WoodResourceLedgerRuntime.TickLoaded(worker, workerZdo);
                    break;
                case WorkOrder.Stonework:
                    StoneResourceLedgerRuntime.TickLoaded(worker, workerZdo);
                    break;
                case WorkOrder.Garden:
                    GardenBackgroundRuntime.TickLoaded(worker, workerZdo);
                    break;
                case WorkOrder.Forestry:
                    ForestryBackgroundRuntime.TickLoaded(worker, workerZdo);
                    break;
                case WorkOrder.GatherResources:
                    BackgroundGathererRuntime.TickLoaded(worker, workerZdo);
                    break;
                case WorkOrder.Harvest:
                    BackgroundHarvesterRuntime.TickLoaded(worker, workerZdo);
                    break;
                case WorkOrder.KeepFires:
                    BackgroundMachineProductionRuntime.TickLoaded(worker, workerZdo);
                    break;
                case WorkOrder.Sort:
                    BackgroundSorterRuntime.TickLoaded(worker, workerZdo);
                    break;
                case WorkOrder.Fish:
                    BackgroundFisherRuntime.CaptureLoadedSnapshot(worker, workerZdo);
                    break;
                case WorkOrder.Smith:
                    // One handoff snapshot feeds BOTH Smith modules. Repair records the finite
                    // damaged-piece backlog; metal records only real machine conversions that have
                    // an explicit station production order. No unloaded scanner is installed.
                    BackgroundMaintenanceRuntime.CaptureSmithSnapshot(worker, workerZdo);
                    BackgroundSmithMetalRuntime.CaptureLoadedSnapshot(worker, workerZdo);
                    break;
                // Bake uses persistent station orders + persistent storage ledgers directly.
                // Hunt/Guard currently have no loaded Workzone production snapshot.
                // Butcher needs no loaded production snapshot; its catch-up reconciles real animals.
            }

            Stamp(areaId, radius, order, role);
        }

        private static void Stamp(string areaId, float radius, WorkOrder order, WorkerRole role)
        {
            try
            {
                if (!WorkFlagRegistry.TryGetNetworkState(areaId, Mathf.Max(8f, radius), out _, out ZDO flagZdo) || flagZdo == null)
                    return;
                WorkFlagRegistry.RememberPersistent(flagZdo);
                flagZdo.Set(CacheVersionKey, CacheVersion);
                flagZdo.Set(CacheTicksKey, DateTime.UtcNow.Ticks);
                flagZdo.Set(CacheOrderKey, (int)order);
                flagZdo.Set(CacheRoleKey, (int)role);
            }
            catch
            {
                // Cache metadata is diagnostic only. A failed stamp must never block handoff.
            }
        }
    }
}
