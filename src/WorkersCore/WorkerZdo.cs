using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    internal readonly struct WorkerGardenPlan
    {
        internal readonly int SlotId;
        internal readonly string HarvestPrefab;
        internal readonly string PlantPrefab;
        internal readonly string ResourcePrefab;
        internal readonly int ResourceAmount;
        internal readonly float Spacing;
        internal readonly Vector3 Position;
        internal readonly float RotationY;
        internal readonly bool SeedProduction;
        internal readonly int SeedStock;
        internal readonly int SeedReserve;

        internal WorkerGardenPlan(int slotId, string harvestPrefab, string plantPrefab, string resourcePrefab, int resourceAmount, float spacing, Vector3 position, bool seedProduction, int seedStock, int seedReserve, float rotationY = 0f)
        {
            SlotId = slotId; HarvestPrefab = harvestPrefab ?? string.Empty; PlantPrefab = plantPrefab ?? string.Empty;
            ResourcePrefab = resourcePrefab ?? string.Empty; ResourceAmount = resourceAmount; Spacing = spacing; Position = position; RotationY = rotationY;
            SeedProduction = seedProduction; SeedStock = seedStock; SeedReserve = seedReserve;
        }
    }


    internal readonly struct WorkerForestryPlan
    {
        internal readonly int SlotId;
        internal readonly string SeedPrefab;
        internal readonly string SaplingPrefab;
        internal readonly Vector3 Position;
        internal readonly float RotationY;

        internal WorkerForestryPlan(int slotId, string seedPrefab, string saplingPrefab, Vector3 position, float rotationY)
        {
            SlotId = slotId; SeedPrefab = seedPrefab ?? string.Empty; SaplingPrefab = saplingPrefab ?? string.Empty; Position = position; RotationY = rotationY;
        }
    }

    internal static class WorkerZdo
    {
        internal const int CurrentSchema = 22;
        internal const string Recruited = "vw_recruited";
        internal const string OwnerPlayerId = "vw_owner_player_id";
        internal const string Role = "vw_role";
        internal const string State = "vw_state";
        internal const string HomeX = "vw_home_x";
        internal const string HomeY = "vw_home_y";
        internal const string HomeZ = "vw_home_z";
        internal const string WorkRadius = "vw_work_radius";
        internal const string TargetName = "vw_target_name";
        internal const string TargetKind = "vw_target_kind";
        internal const string TargetX = "vw_target_x";
        internal const string TargetY = "vw_target_y";
        internal const string TargetZ = "vw_target_z";
        internal const string Schema = "vw_schema";
        internal const string HomeAnchorType = "vw_home_anchor_type";
        internal const string HomeAnchorZdoId = "vw_home_anchor_zdoid";
        internal const string RestReason = "vw_rest_reason";
        internal const string Stance = "vw_stance";
        internal const string GuardBehavior = "vw_guard_behavior";
        internal const string WorkOrderKey = "vw_work_order";
        internal const string WorkAreaId = "vw_work_area_id";
        internal const string WorkAreaName = "vw_work_area_name";
        internal const string WorkAreaX = "vw_work_area_x";
        internal const string WorkAreaY = "vw_work_area_y";
        internal const string WorkAreaZ = "vw_work_area_z";
        internal const string WorkAreaRadius = "vw_work_area_radius";
        internal const string CargoPrefab = "vw_cargo_prefab";
        internal const string CargoAmount = "vw_cargo_amount";
        internal const string CargoManifest = "vw_cargo_manifest";
        internal const string CargoRoute = "vw_cargo_route";
        internal const string CargoTargetX = "vw_cargo_target_x";
        internal const string CargoTargetY = "vw_cargo_target_y";
        internal const string CargoTargetZ = "vw_cargo_target_z";
        internal const string CargoTargetZdoId = "vw_cargo_target_zdoid";
        internal const string CargoTargetWorkAreaId = "vw_cargo_target_work_area_id";
        internal const string CargoDropPending = "vw_cargo_drop_pending";
        internal const string CargoDropReason = "vw_cargo_drop_reason";
        internal const string GardenPlanSlotId = "vw_garden_plan_slot_id";
        internal const string GardenPlanHarvestPrefab = "vw_garden_plan_harvest";
        internal const string GardenPlanPlantPrefab = "vw_garden_plan_plant";
        internal const string GardenPlanResourcePrefab = "vw_garden_plan_resource";
        internal const string GardenPlanResourceAmount = "vw_garden_plan_amount";
        internal const string GardenPlanSpacing = "vw_garden_plan_spacing";
        internal const string GardenPlanX = "vw_garden_plan_x";
        internal const string GardenPlanY = "vw_garden_plan_y";
        internal const string GardenPlanZ = "vw_garden_plan_z";
        internal const string GardenPlanRotationY = "vw_garden_plan_rot_y";
        internal const string GardenPlanSeedProduction = "vw_garden_plan_seed_mode";
        internal const string GardenPlanSeedStock = "vw_garden_plan_seed_stock";
        internal const string GardenPlanSeedReserve = "vw_garden_plan_seed_reserve";
        internal const string ForestryPlanSlotId = "vw_forestry_plan_slot_id";
        internal const string ForestryPlanSeedPrefab = "vw_forestry_plan_seed";
        internal const string ForestryPlanSaplingPrefab = "vw_forestry_plan_sapling";
        internal const string ForestryPlanX = "vw_forestry_plan_x";
        internal const string ForestryPlanY = "vw_forestry_plan_y";
        internal const string ForestryPlanZ = "vw_forestry_plan_z";
        internal const string ForestryPlanRotationY = "vw_forestry_plan_rot_y";
        internal const string FireAnchorZdoId = "vw_fire_anchor_zdoid";
        internal const string FireAnchorX = "vw_fire_anchor_x";
        internal const string FireAnchorY = "vw_fire_anchor_y";
        internal const string FireAnchorZ = "vw_fire_anchor_z";
        internal const string LastDecisionReason = "vw_last_decision_reason";
        internal const string LastDecisionTicks = "vw_last_decision_ticks";
        internal const string SpeciesKey = "vw_worker_species";
        internal const string ProgressionEligible = "vw_recruit_progression_eligible";

        internal static bool IsWorker(ZDO zdo) => zdo != null && zdo.GetBool(Recruited, false);

        internal static void WriteInitialState(ZDO zdo, long ownerPlayerId, WorkerRole role, Vector3 home, float radius, bool progressionEligible = true)
        {
            zdo.Set(Schema, CurrentSchema);
            zdo.Set(Recruited, true);
            zdo.Set(ProgressionEligible, progressionEligible);
            zdo.Set(OwnerPlayerId, ownerPlayerId);
            zdo.Set(Role, (int)role);
            zdo.Set(State, (int)WorkerState.Idle);
            zdo.Set(HomeX, home.x);
            zdo.Set(HomeY, home.y);
            zdo.Set(HomeZ, home.z);
            zdo.Set(WorkRadius, radius);
            zdo.Set(HomeAnchorType, string.Empty);
            zdo.Set(HomeAnchorZdoId, ZDOID.None);
            zdo.Set(RestReason, (int)WorkerRestReason.None);
            zdo.Set(Stance, (int)WorkerCombatStanceRuntime.DefaultStance);
            zdo.Set(GuardBehavior, (int)GuardBehaviorMode.Stationary);
            ClearWorkArea(zdo);
            ClearCargo(zdo);
            ClearGardenPlan(zdo);
            ClearForestryPlan(zdo);
            ClearFireAnchor(zdo);
            ClearTarget(zdo);
            // v0.5.211 sustenance is lazily initialized once the species/home identity exists.
            zdo.Set(WorkerSustenanceRuntime.NextMealTicksKey, 0L);
        }


        internal static void AssignHome(ZDO zdo, Vector3 home, string anchorType)
        {
            AssignHome(zdo, home, anchorType, ZDOID.None);
        }

        internal static void AssignHome(ZDO zdo, Vector3 home, string anchorType, ZDOID anchorZdoId)
        {
            if (zdo == null || !IsWorker(zdo)) return;
            zdo.Set(Schema, CurrentSchema);
            zdo.Set(HomeX, home.x);
            zdo.Set(HomeY, home.y);
            zdo.Set(HomeZ, home.z);
            zdo.Set(HomeAnchorType, anchorType ?? string.Empty);
            zdo.Set(HomeAnchorZdoId, anchorZdoId);
            zdo.Set(RestReason, (int)WorkerRestReason.None);
            zdo.Set(State, (int)WorkerState.ReturnHome);
            ClearTarget(zdo);
            WorkerProvisioningLogisticsRuntime.NotifyWorkerTopologyChanged(zdo);
        }

        internal static ZDOID ReadHomeAnchorZdoId(ZDO zdo)
        {
            return zdo == null ? ZDOID.None : zdo.GetZDOID(HomeAnchorZdoId);
        }

        internal static void ClearHomeAssignment(ZDO zdo)
        {
            if (zdo == null || !IsWorker(zdo)) return;
            Vector3 current = zdo.GetPosition();
            zdo.Set(Schema, CurrentSchema);
            zdo.Set(HomeX, current.x);
            zdo.Set(HomeY, current.y);
            zdo.Set(HomeZ, current.z);
            zdo.Set(HomeAnchorType, string.Empty);
            zdo.Set(HomeAnchorZdoId, ZDOID.None);
            zdo.Set(RestReason, (int)WorkerRestReason.None);
            ClearTarget(zdo);
            zdo.Set(State, TryReadWorkArea(zdo, out _, out _, out _) ? (int)WorkerState.MoveToWorkArea : (int)WorkerState.Idle);
            WorkerProvisioningLogisticsRuntime.NotifyWorkerTopologyChanged(zdo);
        }


        internal static void AssignWorkArea(ZDO zdo, string workAreaId, string workAreaName, WorkOrder order, Vector3 center, float radius)
        {
            if (zdo == null || !IsWorker(zdo)) return;

            // v0.5.414 performance hotfix: TickWorker refreshes a loaded WorkFlag assignment on
            // every normal decision. The old implementation treated that harmless refresh as a
            // topology change every two seconds, which invalidated the provisioning demand cache
            // and forced the next multi-job Hauler to rebuild it. In a large base this showed up
            // as repeatable ~1 second PERF WORKER stalls while an idle Greydwarf waited at home.
            // Only dirty network/cache state when the persisted assignment actually changed.
            string nextAreaId = workAreaId ?? string.Empty;
            string nextAreaName = workAreaName ?? string.Empty;
            float nextRadius = Mathf.Max(5f, radius);

            string previousAreaId = zdo.GetString(WorkAreaId, string.Empty);
            string previousAreaName = zdo.GetString(WorkAreaName, string.Empty);
            WorkOrder previousOrder = (WorkOrder)zdo.GetInt(WorkOrderKey, (int)WorkOrder.None);
            Vector3 previousCenter = new Vector3(
                zdo.GetFloat(WorkAreaX, center.x),
                zdo.GetFloat(WorkAreaY, center.y),
                zdo.GetFloat(WorkAreaZ, center.z));
            float previousRadius = zdo.GetFloat(WorkAreaRadius, nextRadius);

            bool hadAssignment = previousOrder != WorkOrder.None || !string.IsNullOrEmpty(previousAreaId);
            bool identityChanged = previousOrder != order ||
                                   !string.Equals(previousAreaId, nextAreaId, System.StringComparison.Ordinal);
            bool geometryChanged = (previousCenter - center).sqrMagnitude > 0.0025f ||
                                   Mathf.Abs(previousRadius - nextRadius) > 0.05f;
            bool nameChanged = !string.Equals(previousAreaName, nextAreaName, System.StringComparison.Ordinal);
            bool topologyChanged = identityChanged || geometryChanged;

            if (hadAssignment && identityChanged && HasCargo(zdo))
                MarkCargoDropPending(zdo, $"assignment changed: {previousOrder}/{previousAreaId} -> {order}/{nextAreaId}");

            if (identityChanged || !hadAssignment)
            {
                zdo.Set(WorkAreaId, nextAreaId);
                zdo.Set(WorkOrderKey, (int)order);
            }
            if (nameChanged) zdo.Set(WorkAreaName, nextAreaName);
            if (geometryChanged || !hadAssignment)
            {
                zdo.Set(WorkAreaX, center.x);
                zdo.Set(WorkAreaY, center.y);
                zdo.Set(WorkAreaZ, center.z);
                zdo.Set(WorkAreaRadius, nextRadius);
            }

            if (!topologyChanged && hadAssignment) return;

            if (!string.IsNullOrEmpty(previousAreaId)) WorkzoneEconomyRuntime.Invalidate(previousAreaId);
            if (!string.IsNullOrEmpty(nextAreaId)) WorkzoneEconomyRuntime.Invalidate(nextAreaId);
            WorkerProvisioningLogisticsRuntime.NotifyWorkerTopologyChanged(zdo);
        }

        internal static void AssignWorkArea(ZDO zdo, string workAreaId, WorkOrder order, Vector3 center, float radius)
        {
            AssignWorkArea(zdo, workAreaId, string.Empty, order, center, radius);
        }

        internal static void AssignWorkArea(ZDO zdo, WorkOrder order, Vector3 center, float radius)
        {
            AssignWorkArea(zdo, string.Empty, string.Empty, order, center, radius);
        }

        internal static string ReadWorkAreaId(ZDO zdo)
        {
            return zdo?.GetString(WorkAreaId, string.Empty) ?? string.Empty;
        }

        internal static string ReadWorkAreaName(ZDO zdo)
        {
            return zdo?.GetString(WorkAreaName, string.Empty) ?? string.Empty;
        }

        internal static void ClearWorkArea(ZDO zdo)
        {
            if (zdo == null) return;
            WorkOrder previousOrder = (WorkOrder)zdo.GetInt(WorkOrderKey, (int)WorkOrder.None);
            string previousAreaId = zdo.GetString(WorkAreaId, string.Empty);
            bool hadAssignment = previousOrder != WorkOrder.None || !string.IsNullOrEmpty(previousAreaId);
            if (hadAssignment && HasCargo(zdo))
                MarkCargoDropPending(zdo, $"work assignment cleared: {previousOrder}/{previousAreaId}");
            zdo.Set(WorkAreaId, string.Empty);
            zdo.Set(WorkAreaName, string.Empty);
            zdo.Set(WorkOrderKey, (int)WorkOrder.None);
            zdo.Set(WorkAreaRadius, 0f);
            ClearGardenPlan(zdo);
            ClearForestryPlan(zdo);
            if (!string.IsNullOrEmpty(previousAreaId)) WorkzoneEconomyRuntime.Invalidate(previousAreaId);
            if (hadAssignment && IsWorker(zdo)) WorkerProvisioningLogisticsRuntime.NotifyWorkerTopologyChanged(zdo);
        }

        internal static bool TryReadWorkArea(ZDO zdo, out WorkOrder order, out Vector3 center, out float radius)
        {
            order = WorkOrder.None;
            center = default;
            radius = 0f;
            if (zdo == null || !IsWorker(zdo)) return false;
            order = (WorkOrder)zdo.GetInt(WorkOrderKey, (int)WorkOrder.None);
            radius = zdo.GetFloat(WorkAreaRadius, 0f);
            if (order == WorkOrder.None || radius <= 0f) return false;
            center = new Vector3(
                zdo.GetFloat(WorkAreaX, 0f),
                zdo.GetFloat(WorkAreaY, 0f),
                zdo.GetFloat(WorkAreaZ, 0f));
            return true;
        }


        internal static void AssignFireAnchor(ZDO zdo, ZDOID fireZdoId, Vector3 position)
        {
            if (zdo == null || !IsWorker(zdo)) return;
            zdo.Set(FireAnchorZdoId, fireZdoId);
            zdo.Set(FireAnchorX, position.x);
            zdo.Set(FireAnchorY, position.y);
            zdo.Set(FireAnchorZ, position.z);
        }

        internal static bool TryReadFireAnchor(ZDO zdo, out ZDOID fireZdoId, out Vector3 position)
        {
            fireZdoId = ZDOID.None;
            position = Vector3.zero;
            if (zdo == null || !IsWorker(zdo)) return false;
            fireZdoId = zdo.GetZDOID(FireAnchorZdoId);
            if (fireZdoId.Equals(ZDOID.None)) return false;
            position = new Vector3(
                zdo.GetFloat(FireAnchorX, 0f),
                zdo.GetFloat(FireAnchorY, 0f),
                zdo.GetFloat(FireAnchorZ, 0f));
            return true;
        }

        internal static void ClearFireAnchor(ZDO zdo)
        {
            if (zdo == null) return;
            zdo.Set(FireAnchorZdoId, ZDOID.None);
            zdo.Set(FireAnchorX, 0f);
            zdo.Set(FireAnchorY, 0f);
            zdo.Set(FireAnchorZ, 0f);
        }


        internal static void SetGardenPlan(ZDO zdo, WorkerGardenPlan plan)
        {
            if (zdo == null) return;
            zdo.Set(GardenPlanSlotId, plan.SlotId);
            zdo.Set(GardenPlanHarvestPrefab, plan.HarvestPrefab ?? string.Empty);
            zdo.Set(GardenPlanPlantPrefab, plan.PlantPrefab ?? string.Empty);
            zdo.Set(GardenPlanResourcePrefab, plan.ResourcePrefab ?? string.Empty);
            zdo.Set(GardenPlanResourceAmount, Mathf.Max(0, plan.ResourceAmount));
            zdo.Set(GardenPlanSpacing, Mathf.Max(0f, plan.Spacing));
            zdo.Set(GardenPlanX, plan.Position.x); zdo.Set(GardenPlanY, plan.Position.y); zdo.Set(GardenPlanZ, plan.Position.z);
            zdo.Set(GardenPlanRotationY, plan.RotationY);
            zdo.Set(GardenPlanSeedProduction, plan.SeedProduction);
            zdo.Set(GardenPlanSeedStock, Mathf.Max(0, plan.SeedStock));
            zdo.Set(GardenPlanSeedReserve, Mathf.Max(0, plan.SeedReserve));
        }

        internal static bool TryReadGardenPlan(ZDO zdo, out WorkerGardenPlan plan)
        {
            plan = default;
            if (zdo == null) return false;
            int slotId = zdo.GetInt(GardenPlanSlotId, 0);
            string plant = zdo.GetString(GardenPlanPlantPrefab, string.Empty);
            string resource = zdo.GetString(GardenPlanResourcePrefab, string.Empty);
            int amount = zdo.GetInt(GardenPlanResourceAmount, 0);
            if (slotId <= 0 || string.IsNullOrEmpty(plant) || string.IsNullOrEmpty(resource) || amount <= 0) return false;
            plan = new WorkerGardenPlan(slotId, zdo.GetString(GardenPlanHarvestPrefab, string.Empty), plant, resource, amount,
                zdo.GetFloat(GardenPlanSpacing, 1f),
                new Vector3(zdo.GetFloat(GardenPlanX, 0f), zdo.GetFloat(GardenPlanY, 0f), zdo.GetFloat(GardenPlanZ, 0f)),
                zdo.GetBool(GardenPlanSeedProduction, false), zdo.GetInt(GardenPlanSeedStock, 0), zdo.GetInt(GardenPlanSeedReserve, 0),
                zdo.GetFloat(GardenPlanRotationY, 0f));
            return true;
        }

        internal static void ClearGardenPlan(ZDO zdo)
        {
            if (zdo == null) return;
            zdo.Set(GardenPlanSlotId, 0);
            zdo.Set(GardenPlanHarvestPrefab, string.Empty); zdo.Set(GardenPlanPlantPrefab, string.Empty); zdo.Set(GardenPlanResourcePrefab, string.Empty);
            zdo.Set(GardenPlanResourceAmount, 0); zdo.Set(GardenPlanSpacing, 0f); zdo.Set(GardenPlanRotationY, 0f); zdo.Set(GardenPlanSeedProduction, false);
            zdo.Set(GardenPlanSeedStock, 0); zdo.Set(GardenPlanSeedReserve, 0);
        }

        internal static void SetForestryPlan(ZDO zdo, WorkerForestryPlan plan)
        {
            if (zdo == null) return;
            zdo.Set(ForestryPlanSlotId, plan.SlotId);
            zdo.Set(ForestryPlanSeedPrefab, plan.SeedPrefab ?? string.Empty);
            zdo.Set(ForestryPlanSaplingPrefab, plan.SaplingPrefab ?? string.Empty);
            zdo.Set(ForestryPlanX, plan.Position.x); zdo.Set(ForestryPlanY, plan.Position.y); zdo.Set(ForestryPlanZ, plan.Position.z);
            zdo.Set(ForestryPlanRotationY, plan.RotationY);
        }

        internal static bool TryReadForestryPlan(ZDO zdo, out WorkerForestryPlan plan)
        {
            plan = default;
            if (zdo == null) return false;
            int slotId = zdo.GetInt(ForestryPlanSlotId, 0);
            string seed = zdo.GetString(ForestryPlanSeedPrefab, string.Empty);
            string sapling = zdo.GetString(ForestryPlanSaplingPrefab, string.Empty);
            if (slotId <= 0 || string.IsNullOrEmpty(seed) || string.IsNullOrEmpty(sapling)) return false;
            plan = new WorkerForestryPlan(slotId, seed, sapling, new Vector3(zdo.GetFloat(ForestryPlanX,0f), zdo.GetFloat(ForestryPlanY,0f), zdo.GetFloat(ForestryPlanZ,0f)), zdo.GetFloat(ForestryPlanRotationY,0f));
            return true;
        }

        internal static void ClearForestryPlan(ZDO zdo)
        {
            if (zdo == null) return;
            zdo.Set(ForestryPlanSlotId, 0); zdo.Set(ForestryPlanSeedPrefab, string.Empty); zdo.Set(ForestryPlanSaplingPrefab, string.Empty);
            zdo.Set(ForestryPlanX, 0f); zdo.Set(ForestryPlanY, 0f); zdo.Set(ForestryPlanZ, 0f); zdo.Set(ForestryPlanRotationY, 0f);
        }

        internal readonly struct CargoStack
        {
            internal readonly string Prefab;
            internal readonly int Amount;
            internal CargoStack(string prefab, int amount) { Prefab = prefab ?? string.Empty; Amount = Mathf.Max(0, amount); }
        }

        internal static void SetCargo(ZDO zdo, string prefabName, int amount)
        {
            if (zdo == null) return;
            int safeAmount = Mathf.Max(0, amount);
            string safePrefab = prefabName ?? string.Empty;
            if (safeAmount <= 0 || string.IsNullOrEmpty(safePrefab))
            {
                ClearCargo(zdo);
                return;
            }
            zdo.Set(CargoManifest, safePrefab + "=" + safeAmount);
            zdo.Set(CargoPrefab, safePrefab);
            zdo.Set(CargoAmount, safeAmount);
        }

        internal static List<CargoStack> ReadCargoStacks(ZDO zdo)
        {
            List<CargoStack> result = new List<CargoStack>();
            if (zdo == null) return result;
            string manifest = zdo.GetString(CargoManifest, string.Empty);
            if (!string.IsNullOrEmpty(manifest))
            {
                string[] entries = manifest.Split('|');
                for (int i = 0; i < entries.Length; i++)
                {
                    string entry = entries[i];
                    int sep = entry.LastIndexOf('=');
                    if (sep <= 0 || sep >= entry.Length - 1) continue;
                    string prefab = entry.Substring(0, sep);
                    if (!int.TryParse(entry.Substring(sep + 1), out int amount) || amount <= 0 || string.IsNullOrEmpty(prefab)) continue;
                    result.Add(new CargoStack(prefab, amount));
                }
                if (result.Count > 0) return result;
            }

            string legacyPrefab = zdo.GetString(CargoPrefab, string.Empty);
            int legacyAmount = Mathf.Max(0, zdo.GetInt(CargoAmount, 0));
            if (!string.IsNullOrEmpty(legacyPrefab) && legacyAmount > 0)
                result.Add(new CargoStack(legacyPrefab, legacyAmount));
            return result;
        }

        internal static int GetCargoAmount(ZDO zdo, string prefabName)
        {
            if (zdo == null || string.IsNullOrEmpty(prefabName)) return 0;
            List<CargoStack> stacks = ReadCargoStacks(zdo);
            for (int i = 0; i < stacks.Count; i++)
                if (string.Equals(stacks[i].Prefab, prefabName, System.StringComparison.OrdinalIgnoreCase)) return stacks[i].Amount;
            return 0;
        }

        internal static void AddCargoAmount(ZDO zdo, string prefabName, int amount)
        {
            if (zdo == null || string.IsNullOrEmpty(prefabName) || amount <= 0) return;
            List<CargoStack> stacks = ReadCargoStacks(zdo);
            bool found = false;
            for (int i = 0; i < stacks.Count; i++)
            {
                if (!string.Equals(stacks[i].Prefab, prefabName, System.StringComparison.OrdinalIgnoreCase)) continue;
                stacks[i] = new CargoStack(stacks[i].Prefab, stacks[i].Amount + amount);
                found = true;
                break;
            }
            if (!found) stacks.Add(new CargoStack(prefabName, amount));
            WriteCargoStacks(zdo, stacks);
        }

        internal static void RemoveCargoAmount(ZDO zdo, string prefabName, int amount)
        {
            if (zdo == null || string.IsNullOrEmpty(prefabName) || amount <= 0) return;
            List<CargoStack> stacks = ReadCargoStacks(zdo);
            for (int i = stacks.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(stacks[i].Prefab, prefabName, System.StringComparison.OrdinalIgnoreCase)) continue;
                int left = Mathf.Max(0, stacks[i].Amount - amount);
                if (left <= 0) stacks.RemoveAt(i);
                else stacks[i] = new CargoStack(stacks[i].Prefab, left);
                break;
            }
            WriteCargoStacks(zdo, stacks);
        }

        internal static void WriteCargoStacks(ZDO zdo, List<CargoStack> stacks)
        {
            if (zdo == null) return;
            if (stacks == null || stacks.Count == 0)
            {
                zdo.Set(CargoManifest, string.Empty);
                zdo.Set(CargoPrefab, string.Empty);
                zdo.Set(CargoAmount, 0);
                return;
            }
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            for (int i = 0; i < stacks.Count; i++)
            {
                if (stacks[i].Amount <= 0 || string.IsNullOrEmpty(stacks[i].Prefab)) continue;
                if (b.Length > 0) b.Append('|');
                b.Append(stacks[i].Prefab).Append('=').Append(stacks[i].Amount);
            }
            zdo.Set(CargoManifest, b.ToString());
            CargoStack first = stacks[0];
            zdo.Set(CargoPrefab, first.Prefab);
            zdo.Set(CargoAmount, first.Amount);
        }

        internal static void MarkCargoDropPending(ZDO zdo, string reason)
        {
            if (zdo == null || !HasCargo(zdo)) return;
            zdo.Set(CargoDropPending, true);
            zdo.Set(CargoDropReason, reason ?? string.Empty);
        }

        internal static bool TryReadCargoDropPending(ZDO zdo, out string reason)
        {
            reason = string.Empty;
            if (zdo == null || !zdo.GetBool(CargoDropPending, false)) return false;
            reason = zdo.GetString(CargoDropReason, string.Empty);
            return true;
        }

        internal static void ClearCargoDropPending(ZDO zdo)
        {
            if (zdo == null) return;
            zdo.Set(CargoDropPending, false);
            zdo.Set(CargoDropReason, string.Empty);
        }

        internal static void SetCargoRoute(ZDO zdo, string route, Vector3 target)
        {
            SetCargoRoute(zdo, route, target, ZDOID.None, string.Empty);
        }

        internal static void SetCargoRoute(ZDO zdo, string route, Vector3 target, ZDOID targetZdoId)
        {
            SetCargoRoute(zdo, route, target, targetZdoId, string.Empty);
        }

        internal static void SetCargoRoute(ZDO zdo, string route, Vector3 target, ZDOID targetZdoId, string targetWorkAreaId)
        {
            if (zdo == null) return;
            zdo.Set(CargoRoute, route ?? string.Empty);
            zdo.Set(CargoTargetX, target.x);
            zdo.Set(CargoTargetY, target.y);
            zdo.Set(CargoTargetZ, target.z);
            zdo.Set(CargoTargetZdoId, targetZdoId);
            zdo.Set(CargoTargetWorkAreaId, targetWorkAreaId ?? string.Empty);
        }

        internal static ZDOID ReadCargoRouteTargetZdoId(ZDO zdo)
        {
            return zdo == null ? ZDOID.None : zdo.GetZDOID(CargoTargetZdoId);
        }

        internal static string ReadCargoRouteTargetWorkAreaId(ZDO zdo)
        {
            return zdo == null ? string.Empty : zdo.GetString(CargoTargetWorkAreaId, string.Empty);
        }

        internal static bool TryReadCargoRoute(ZDO zdo, out string route, out Vector3 target)
        {
            route = string.Empty;
            target = default;
            if (zdo == null) return false;
            route = zdo.GetString(CargoRoute, string.Empty);
            if (string.IsNullOrEmpty(route)) return false;
            target = new Vector3(zdo.GetFloat(CargoTargetX, 0f), zdo.GetFloat(CargoTargetY, 0f), zdo.GetFloat(CargoTargetZ, 0f));
            return true;
        }

        internal static void ClearCargoRoute(ZDO zdo)
        {
            if (zdo == null) return;
            zdo.Set(CargoRoute, string.Empty);
            zdo.Set(CargoTargetX, 0f); zdo.Set(CargoTargetY, 0f); zdo.Set(CargoTargetZ, 0f);
            zdo.Set(CargoTargetZdoId, ZDOID.None);
            zdo.Set(CargoTargetWorkAreaId, string.Empty);
        }

        internal static bool HasCargo(ZDO zdo)
        {
            return ReadCargoStacks(zdo).Count > 0;
        }

        internal static bool TryReadCargo(ZDO zdo, out string prefabName, out int amount)
        {
            prefabName = string.Empty;
            amount = 0;
            if (zdo == null) return false;
            List<CargoStack> stacks = ReadCargoStacks(zdo);
            if (stacks.Count <= 0) return false;
            prefabName = stacks[0].Prefab;
            amount = stacks[0].Amount;
            return !string.IsNullOrEmpty(prefabName) && amount > 0;
        }

        internal static void ClearCargo(ZDO zdo)
        {
            if (zdo == null) return;
            zdo.Set(CargoManifest, string.Empty);
            zdo.Set(CargoPrefab, string.Empty);
            zdo.Set(CargoAmount, 0);
            ClearCargoDropPending(zdo);
            ClearCargoRoute(zdo);
        }

        internal static Vector3 ReadHome(ZDO zdo, Vector3 fallback)
        {
            if (zdo == null || !IsWorker(zdo)) return fallback;
            return new Vector3(
                zdo.GetFloat(HomeX, fallback.x),
                zdo.GetFloat(HomeY, fallback.y),
                zdo.GetFloat(HomeZ, fallback.z));
        }

        internal static void WriteTarget(ZDO zdo, string targetName, Vector3 position, string targetKind)
        {
            if (zdo == null) return;
            zdo.Set(Schema, CurrentSchema);
            zdo.Set(TargetName, targetName ?? string.Empty);
            zdo.Set(TargetKind, targetKind ?? string.Empty);
            zdo.Set(TargetX, position.x);
            zdo.Set(TargetY, position.y);
            zdo.Set(TargetZ, position.z);
        }

        internal static void ClearTarget(ZDO zdo)
        {
            if (zdo == null) return;
            if (!string.IsNullOrEmpty(zdo.GetString(TargetName, string.Empty)))
                zdo.Set(TargetName, string.Empty);
            if (!string.IsNullOrEmpty(zdo.GetString(TargetKind, string.Empty)))
                zdo.Set(TargetKind, string.Empty);
        }

        internal static bool TryReadTarget(ZDO zdo, out string targetName, out string targetKind, out Vector3 position)
        {
            targetName = string.Empty;
            targetKind = string.Empty;
            position = default;
            if (zdo == null) return false;

            targetName = zdo.GetString(TargetName, string.Empty);
            if (string.IsNullOrEmpty(targetName)) return false;

            targetKind = zdo.GetString(TargetKind, string.Empty);
            position = new Vector3(
                zdo.GetFloat(TargetX, 0f),
                zdo.GetFloat(TargetY, 0f),
                zdo.GetFloat(TargetZ, 0f));
            return true;
        }
    }
}
