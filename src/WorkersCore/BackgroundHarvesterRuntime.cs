using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Dedicated unloaded-zone adapter for the explicit Harvest work order. It only advances the
    /// existing Garden background harvest ledger. Loose ItemDrops remain owned by GatherResources.
    /// </summary>
    internal static class BackgroundHarvesterRuntime
    {
        internal static void TickLoaded(Character worker, ZDO workerZdo)
        {
            if (worker == null || workerZdo == null || !WorkerZdo.IsWorker(workerZdo)) return;
            if (!WorkerJobProfileRuntime.CanHarvest(workerZdo)) return;
            if (GatherHarvestCategoryFilterRuntime.HasCustomHarvestFilter(workerZdo)) return;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out _, out _, out _)) return;
            GardenBackgroundRuntime.FlushPendingHarvest(worker, workerZdo);
        }

        internal static string TickBackground(ZDO workerZdo, double deltaSeconds, float efficiency)
        {
            if (workerZdo == null) return "harvest:no-worker";
            if (GatherHarvestCategoryFilterRuntime.HasCustomHarvestFilter(workerZdo)) return "harvest:custom-filter-physical-only";
            return GardenBackgroundRuntime.TickBackgroundHarvest(workerZdo, deltaSeconds, efficiency);
        }

        internal static bool HasBackgroundWork(ZDO workerZdo)
        {
            if (workerZdo == null) return false;
            if (GatherHarvestCategoryFilterRuntime.HasCustomHarvestFilter(workerZdo)) return false;
            return GardenBackgroundRuntime.HasBackgroundHarvestWork(workerZdo);
        }
    }
}
