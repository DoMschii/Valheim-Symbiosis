using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Plains Shaman specialization: one physical planting stop may fill a compact 3x3 block
    /// from the same persistent planned field. It never invents resources and only completes
    /// real pending slots after a real networked plant was spawned.
    /// </summary>
    internal static class FulingShamanGardenRuntime
    {
        private const int MaxPlantsPerCast = 9;

        internal static bool IsBatchPlanter(Character worker)
        {
            if (worker == null) return false;
            return string.Equals(SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name),
                "Fuling_Shaman", StringComparison.OrdinalIgnoreCase);
        }

        internal static int GetDesiredPickupAmount(Character worker, ZDO workerZdo, WorkerGardenPlan plan, int availableAmount)
        {
            int perPlant = Mathf.Max(1, plan.ResourceAmount);
            int available = Mathf.Max(0, availableAmount);
            if (!IsBatchPlanter(worker) || workerZdo == null || plan.SlotId <= 0 || available < perPlant)
                return Mathf.Min(available, perPlant);

            List<GardenReplantSlot> nearby = new List<GardenReplantSlot>(MaxPlantsPerCast - 1);
            float batchRadius = Mathf.Max(3.25f, Mathf.Max(0.75f, plan.Spacing) * 3.2f);
            int additional = GardenReplantPlanRuntime.GetPendingBatchForWorker(workerZdo, plan.SlotId, plan.HarvestPrefab,
                plan.Position, batchRadius, nearby, MaxPlantsPerCast - 1);
            int desired = perPlant * Mathf.Clamp(1 + additional, 1, MaxPlantsPerCast);
            return Mathf.Clamp(Mathf.Min(desired, available), perPlant, desired);
        }

        internal static int TryPlantAdditional(Character worker, ZDO workerZdo, WorkerGardenPlan anchorPlan, GameObject plantPrefab)
        {
            if (!IsBatchPlanter(worker) || workerZdo == null || anchorPlan.SlotId <= 0 || plantPrefab == null) return 0;

            int perPlant = Mathf.Max(1, anchorPlan.ResourceAmount);
            if (WorkerZdo.GetCargoAmount(workerZdo, anchorPlan.ResourcePrefab) < perPlant) return 0;

            List<GardenReplantSlot> nearby = new List<GardenReplantSlot>(MaxPlantsPerCast - 1);
            float batchRadius = Mathf.Max(3.25f, Mathf.Max(0.75f, anchorPlan.Spacing) * 3.2f);
            GardenReplantPlanRuntime.GetPendingBatchForWorker(workerZdo, anchorPlan.SlotId, anchorPlan.HarvestPrefab,
                anchorPlan.Position, batchRadius, nearby, MaxPlantsPerCast - 1);

            int planted = 0;
            for (int i = 0; i < nearby.Count; i++)
            {
                if (WorkerZdo.GetCargoAmount(workerZdo, anchorPlan.ResourcePrefab) < perPlant) break;
                GardenReplantSlot slot = nearby[i];

                if (!GardenTargetFinder.IsPlantingPointStillClear(slot.Position, anchorPlan.Spacing))
                {
                    if (GardenTargetFinder.HasExpectedPlantAt(slot.Position, anchorPlan.Spacing, anchorPlan.PlantPrefab))
                        GardenReplantPlanRuntime.CompleteForWorker(workerZdo, slot.SlotId);
                    continue;
                }

                Quaternion rotation = Quaternion.Euler(0f, slot.RotationY, 0f);
                GameObject instance = UnityEngine.Object.Instantiate(plantPrefab, slot.Position, rotation);
                ZNetView plantedNview = instance != null ? instance.GetComponent<ZNetView>() : null;
                if (instance == null || plantedNview == null || !plantedNview.IsValid() || plantedNview.GetZDO() == null)
                {
                    if (instance != null) UnityEngine.Object.Destroy(instance);
                    continue;
                }

                GardenReplantPlanRuntime.CompleteForWorker(workerZdo, slot.SlotId);
                WorkerZdo.RemoveCargoAmount(workerZdo, anchorPlan.ResourcePrefab, perPlant);
                planted++;
            }

            return planted;
        }
    }
}
