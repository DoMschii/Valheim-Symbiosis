using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Conservative background simulation for player-planned Garden slots.
    /// Only virtual crops planted by this adapter are advanced/harvested while unloaded; physical
    /// crops that already existed before unload remain owned by Valheim and are never duplicated.
    /// </summary>
    internal static class GardenBackgroundRuntime
    {
        private const int MaxSlots = 512;
        private const string CountKey = "vw_garden_plan_count";
        private const string NextPlantTicks = "vw_garden_bg_next_plant";
        private const string HarvestFraction = "vw_garden_bg_harvest_fraction";
        private const float LoadedSnapshotInterval = 67f;
        private const float HarvestsPerHourAtFullEfficiency = 120f;
        private const double DefaultGrowthSeconds = 3600.0;

        private static readonly Dictionary<string, float> NextLoadedSnapshot =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> AvailableByResource =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<int>> SlotIndicesByResource =
            new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Collider[] PlantPointOverlapBuffer = new Collider[64];

        private static bool IsSlotPlantingAllowedForWorker(ZDO workerZdo, ZDO flagZdo, int index)
        {
            if (workerZdo == null || flagZdo == null) return false;
            string harvestPrefab = flagZdo.GetString(Key(index, "harvest"), string.Empty);
            string plantPrefab = flagZdo.GetString(Key(index, "plant"), string.Empty);
            string resourcePrefab = flagZdo.GetString(Key(index, "resource"), string.Empty);
            return !GardenerTaskFilterRuntime.IsWildGrowthIdentity(harvestPrefab, plantPrefab, resourcePrefab) ||
                   GardenerTaskFilterRuntime.CanUseWildGrowth(workerZdo);
        }

        internal static void TickLoaded(Character worker, ZDO workerZdo)
        {
            if (worker == null || workerZdo == null || !WorkerZdo.IsWorker(workerZdo)) return;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out WorkOrder order, out _, out _) || order != WorkOrder.Garden) return;
            if (!TryGetFlag(workerZdo, out ZDO flagZdo)) return;

            // v0.5.328: WildGrowth is a physical-only planting job. Older background simulation
            // could turn a saved VW_Flora_* ghost into a virtual growing crop while the zone was
            // unloaded (including the offline catch-up during world load). That set active=false,
            // so the persistent ghost vanished immediately after loading even though no real bush
            // existed. Heal that legacy state before any loaded-zone debt/materialisation work.
            GardenReplantPlanRuntime.MigrateWildGrowthLifecycle329(flagZdo);
            GardenReplantPlanRuntime.RestoreBackgroundVirtualizedWildGrowth(flagZdo);

            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId)) return;
            if (NextLoadedSnapshot.TryGetValue(areaId, out float next) && Time.realtimeSinceStartup < next) return;
            NextLoadedSnapshot[areaId] = Time.realtimeSinceStartup + LoadedSnapshotInterval;

            // First settle real planting-resource debt accumulated by virtual planting.
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!IsPersistent(flagZdo, i)) continue;
                if (!IsSlotPlantingAllowedForWorker(workerZdo, flagZdo, i)) continue;
                int debt = Mathf.Max(0, flagZdo.GetInt(Key(i, "bg_resource_debt"), 0));
                if (debt <= 0) continue;
                if (!TryResolvePlan(flagZdo, i, out string resourcePrefab, out _, out int requiredAmount)) continue;

                int removed = 0;
                while (removed < debt && WorkerStorageRuntime.TryConsumeBackgroundResource(worker, workerZdo, resourcePrefab, 1))
                    removed++;
                if (removed > 0)
                    flagZdo.Set(Key(i, "bg_resource_debt"), Mathf.Max(0, debt - removed));
            }

            // If a virtual crop was still growing when the zone became physical again, hand it
            // back to Valheim by materialising the actual Plant prefab. Mature virtual crops stay
            // abstract until a Gatherer harvests them, avoiding a fake mature-prefab spawn.
            long nowTicks = DateTime.UtcNow.Ticks;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!IsPersistent(flagZdo, i) || !flagZdo.GetBool(Key(i, "bg_virtual"), false)) continue;
                if (!IsSlotPlantingAllowedForWorker(workerZdo, flagZdo, i)) continue;
                int state = Mathf.Clamp(flagZdo.GetInt(Key(i, "bg_state"), 0), 0, 2);
                int debt = Mathf.Max(0, flagZdo.GetInt(Key(i, "bg_resource_debt"), 0));
                if (state != 1 || debt > 0) continue;

                if (!TryResolvePlan(flagZdo, i, out _, out string plantPrefab, out _)) continue;
                GameObject prefab = GardenWorkRuntime.ResolvePlantPrefab(plantPrefab);
                if (prefab == null) continue;
                Vector3 position = ReadPosition(flagZdo, i);
                if (!IsPlantPointFree(position)) continue;

                try
                {
                    GameObject instance = UnityEngine.Object.Instantiate(prefab, position,
                        Quaternion.Euler(0f, flagZdo.GetFloat(Key(i, "rot"), 0f), 0f));
                    if (instance == null) continue;
                    flagZdo.Set(Key(i, "bg_virtual"), false);
                    flagZdo.Set(Key(i, "bg_state"), 0);
                    flagZdo.Set(Key(i, "bg_ready"), 0L);
                }
                catch { }
            }

            // Refresh a finite per-resource budget from the real storages. Keep-one remains in force.
            AvailableByResource.Clear();
            SlotIndicesByResource.Clear();
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!IsPersistent(flagZdo, i) || !flagZdo.GetBool(Key(i, "active"), false)) continue;
                if (!IsSlotPlantingAllowedForWorker(workerZdo, flagZdo, i)) continue;
                if (!TryResolvePlan(flagZdo, i, out string resourcePrefab, out _, out int requiredAmount)) continue;
                if (string.IsNullOrEmpty(resourcePrefab) || requiredAmount <= 0) continue;
                if (!SlotIndicesByResource.TryGetValue(resourcePrefab, out List<int> list))
                {
                    list = new List<int>();
                    SlotIndicesByResource[resourcePrefab] = list;
                    int total = WorkerStorageRuntime.CountGardenResourceInStorage(worker, workerZdo, resourcePrefab);
                    AvailableByResource[resourcePrefab] = Mathf.Max(0, total - 1);
                }
                list.Add(i);
            }

            foreach (KeyValuePair<string, List<int>> pair in SlotIndicesByResource)
            {
                int available = AvailableByResource.TryGetValue(pair.Key, out int count) ? count : 0;
                int totalDebt = 0;
                for (int n = 0; n < pair.Value.Count; n++)
                    totalDebt += Mathf.Max(0, flagZdo.GetInt(Key(pair.Value[n], "bg_resource_debt"), 0));
                available = Mathf.Max(0, available - totalDebt);

                // Distribute actual item units, not abstract plant counts. Each slot can therefore
                // complete several cycles during a long absence when the loaded snapshot had stock.
                int slotCount = Mathf.Max(1, pair.Value.Count);
                int baseBudget = available / slotCount;
                int remainder = available % slotCount;
                for (int n = 0; n < pair.Value.Count; n++)
                {
                    int i = pair.Value[n];
                    int budget = baseBudget + (n < remainder ? 1 : 0);
                    if (flagZdo.GetInt(Key(i, "bg_resource_debt"), 0) > 0) budget = 0;
                    flagZdo.Set(Key(i, "bg_resource_budget"), Mathf.Max(0, budget));
                }
            }

            // Never assign a virtual baseline to already-existing physical crops. Their lifecycle
            // remains purely physical until the Gatherer reactivates the persistent slot.
            flagZdo.Set("vw_garden_bg_last_loaded", nowTicks);
        }

        internal static string TickBackground(ZDO workerZdo, double deltaSeconds, float efficiency)
        {
            if (workerZdo == null) return "garden:no-worker";
            if (!TryGetFlag(workerZdo, out ZDO flagZdo)) return "garden:no-flag";
            GardenReplantPlanRuntime.MigrateWildGrowthLifecycle329(flagZdo);
            int restoredWildGrowth = GardenReplantPlanRuntime.RestoreBackgroundVirtualizedWildGrowth(flagZdo);
            EnsureBackgroundSupplyRequests(workerZdo, flagZdo);
            if (deltaSeconds <= 0.0) return restoredWildGrowth > 0 ? $"garden:restored-wildgrowth={restoredWildGrowth}" : "garden:waiting";

            long nowTicks = DateTime.UtcNow.Ticks;
            int planted = BackgroundPlantOne(workerZdo, flagZdo, nowTicks, Mathf.Clamp01(efficiency));
            if (planted > 0)
                WorkerJobProfileRuntime.NoteCompletedAction(workerZdo, WorkerRole.Gardener, WorkOrder.Garden);
            int matured = AdvanceVirtualGrowth(flagZdo, nowTicks, out int growing, out int mature);
            int pending = CountPending(flagZdo);
            int budget = SumField(flagZdo, "bg_resource_budget");
            int debt = SumField(flagZdo, "bg_resource_debt");
            return $"garden:bg planted={planted}, restoredWildGrowth={restoredWildGrowth}, matured+={matured}, growing={growing}, mature={mature}, pending={pending}, resourceBudget={budget}, resourceDebt={debt}";
        }

        internal static string TickBackgroundHarvest(ZDO gathererZdo, double deltaSeconds, float efficiency)
        {
            if (gathererZdo == null || deltaSeconds <= 0.0) return "garden-harvest:waiting";
            if (!TryGetFlag(gathererZdo, out ZDO flagZdo)) return "garden-harvest:no-garden-flag";

            if (!HasBackgroundHarvestWork(gathererZdo)) return "garden-harvest:waiting-stock-max";

            float fraction = Mathf.Max(0f, flagZdo.GetFloat(HarvestFraction, 0f));
            fraction += (float)(deltaSeconds * (HarvestsPerHourAtFullEfficiency / 3600.0) * Mathf.Clamp01(efficiency));
            int units = Mathf.FloorToInt(fraction);
            int harvested = 0;

            while (units > 0)
            {
                if (!TryHarvestOneVirtualMature(gathererZdo, flagZdo, out _)) break;
                units--;
                fraction -= 1f;
                harvested++;
            }

            flagZdo.Set(HarvestFraction, Mathf.Max(0f, fraction));
            if (harvested > 0)
            {
                WorkerRole harvestRole = (WorkerRole)gathererZdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
                if (harvestRole == WorkerRole.Gardener || harvestRole == WorkerRole.Gatherer)
                    WorkerJobProfileRuntime.NoteCompletedAction(gathererZdo, harvestRole, WorkOrder.Harvest);
            }
            int mature = CountState(flagZdo, 2);
            int pendingOutput = SumField(flagZdo, "bg_output_pending");
            return $"garden-harvest:bg harvested={harvested}, mature={mature}, pendingOutput={pendingOutput}";
        }

        internal static bool HasBackgroundHarvestWork(ZDO workerZdo)
        {
            if (workerZdo == null || !TryGetFlag(workerZdo, out ZDO flagZdo)) return false;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!IsPersistent(flagZdo, i) || !flagZdo.GetBool(Key(i, "bg_virtual"), false) ||
                    flagZdo.GetInt(Key(i, "bg_state"), 0) != 2) continue;
                string harvestPrefab = flagZdo.GetString(Key(i, "harvest"), string.Empty);
                if (!GardenWorkRuntime.TryResolveGatherableItemPrefab(harvestPrefab, out string output) || string.IsNullOrEmpty(output))
                    return true;
                if (ProductionDemandRuntime.AllowsPrefab(workerZdo, output)) return true;
            }
            return false;
        }

        internal static bool HasBackgroundGardenWork(ZDO workerZdo)
        {
            if (workerZdo == null || !TryGetFlag(workerZdo, out ZDO flagZdo)) return false;
            if (CountPending(flagZdo) > 0 || CountState(flagZdo, 1) > 0) return true;
            for (int i = 0; i < MaxSlots; i++)
                if (IsPersistent(flagZdo, i) && flagZdo.GetBool(Key(i, "active"), false) &&
                    IsSlotPlantingAllowedForWorker(workerZdo, flagZdo, i)) return true;
            return false;
        }

        internal static int FlushPendingHarvest(Character gatherer, ZDO gathererZdo)
        {
            if (gatherer == null || gathererZdo == null || !TryGetFlag(gathererZdo, out ZDO flagZdo)) return 0;
            int moved = 0;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!IsPersistent(flagZdo, i)) continue;
                int pending = Mathf.Max(0, flagZdo.GetInt(Key(i, "bg_output_pending"), 0));
                string output = flagZdo.GetString(Key(i, "bg_output_prefab"), string.Empty);
                if (pending <= 0 || string.IsNullOrEmpty(output)) continue;

                if (!WorkerStorageRuntime.TryFindFieldDepotForCargo(gatherer, gathererZdo, output, out Container depot, out _) || depot == null) continue;
                if (!WorkerStorageRuntime.TryOwn(depot)) continue;
                Inventory inventory = depot.GetInventory();
                if (inventory == null) continue;
                GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(output) : null;
                ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (drop?.m_itemData == null) continue;

                int stackMax = Mathf.Max(1, drop.m_itemData.m_shared?.m_maxStackSize ?? 1);
                int left = pending;
                while (left > 0)
                {
                    int chunk = Mathf.Min(left, stackMax);
                    ItemDrop.ItemData data = drop.m_itemData.Clone();
                    data.m_dropPrefab = prefab;
                    data.m_stack = chunk;
                    if (!WorkerStorageRuntime.TryAddItem(inventory, data)) break;
                    left -= chunk;
                    moved += chunk;
                }
                if (left != pending)
                {
                    WorkerStorageRuntime.PersistContainer(depot, inventory);
                    flagZdo.Set(Key(i, "bg_output_pending"), left);
                    if (left <= 0) flagZdo.Set(Key(i, "bg_output_prefab"), string.Empty);
                }
            }
            return moved;
        }

        internal static string GetDebugSummary(ZDO workerZdo)
        {
            if (workerZdo == null || !TryGetFlag(workerZdo, out ZDO flagZdo)) return "garden-bg:none";
            return $"garden-bg pending={CountPending(flagZdo)}, growing={CountState(flagZdo, 1)}, mature={CountState(flagZdo, 2)}, resourceBudget={SumField(flagZdo, "bg_resource_budget")}, resourceDebt={SumField(flagZdo, "bg_resource_debt")}, output={SumField(flagZdo, "bg_output_pending")}";
        }

        private static void EnsureBackgroundSupplyRequests(ZDO workerZdo, ZDO flagZdo)
        {
            if (workerZdo == null || flagZdo == null) return;
            if (!WorkerLogisticsRuntime.TryFindPersistentFieldDepotForWorkArea(workerZdo, out _, out _)) return;

            Dictionary<string, int> wanted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> wildWanted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!IsPersistent(flagZdo, i) || !flagZdo.GetBool(Key(i, "active"), false)) continue;
                if (!IsSlotPlantingAllowedForWorker(workerZdo, flagZdo, i)) continue;

                // v0.5.388: legacy planner records outside this worker's current Workzone must not
                // create background FieldDepot demand. Loaded Garden already ignores those slots;
                // the unloaded path now uses the same spatial rule.
                Vector3 position = ReadPosition(flagZdo, i);
                if (!GardenReplantPlanRuntime.IsPositionInsideAssignedWorkArea(workerZdo, position)) continue;

                string harvestPrefab = flagZdo.GetString(Key(i, "harvest"), string.Empty);
                string plantPrefab = flagZdo.GetString(Key(i, "plant"), string.Empty);
                if (!TryResolvePlan(flagZdo, i, out string resourcePrefab, out _, out int requiredAmount)) continue;
                if (string.IsNullOrEmpty(resourcePrefab) || requiredAmount <= 0) continue;

                bool wild = GardenerTaskFilterRuntime.IsWildGrowthIdentity(harvestPrefab, plantPrefab, resourcePrefab);
                Dictionary<string, int> bucket = wild ? wildWanted : wanted;
                bucket.TryGetValue(resourcePrefab, out int current);
                bucket[resourcePrefab] = current + Mathf.Max(1, requiredAmount);
            }

            // WildGrowth uses an exact snapshot so old 147-Thistle / 60-Mushroom targets shrink as
            // ghosts are fulfilled and disappear entirely when the last slot is planted. This also
            // keeps unloaded and loaded Workzones on the same demand model.
            WorkerLogisticsRuntime.SyncWildGrowthSupplyForWorkArea(workerZdo, wildWanted);

            foreach (KeyValuePair<string, int> pair in wanted)
            {
                // Normal crops retain the existing useful-buffer behaviour.
                int target = Mathf.Max(WorkerLogisticsRuntime.DefaultFieldSupplyTarget, pair.Value);
                WorkerLogisticsRuntime.RequestSupplyForWorkArea(workerZdo, pair.Key, target);
            }
        }

        private static int BackgroundPlantOne(ZDO workerZdo, ZDO flagZdo, long nowTicks, float efficiency)
        {
            long nextPlant = flagZdo.GetLong(NextPlantTicks, 0L);
            if (nextPlant > nowTicks) return 0;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!IsPersistent(flagZdo, i) || !flagZdo.GetBool(Key(i, "active"), false)) continue;
                if (!IsSlotPlantingAllowedForWorker(workerZdo, flagZdo, i)) continue;
                string harvestPrefab = flagZdo.GetString(Key(i, "harvest"), string.Empty);

                // v0.5.328: never virtualise Workers WildGrowth. These are persistent Pickables,
                // not normal seed->Plant growth cycles. Their saved slot must stay active/visible
                // until the physical Shaman reaches it and spawns the actual network prefab.
                if (WorkersWildFloraRuntime.TryCanonicalizeLegacyIdentity(harvestPrefab, out _)) continue;

                if (GardenWorkRuntime.TryResolveGatherableItemPrefab(harvestPrefab, out string plannedOutput) &&
                    !string.IsNullOrEmpty(plannedOutput) &&
                    !ProductionDemandRuntime.AllowsPrefab(workerZdo, plannedOutput)) continue;
                if (!TryResolvePlan(flagZdo, i, out _, out string plantPrefab, out int requiredAmount)) continue;
                int budget = Mathf.Max(0, flagZdo.GetInt(Key(i, "bg_resource_budget"), 0));
                if (budget < requiredAmount) continue;

                double growSeconds = EstimateGrowthSeconds(plantPrefab);
                flagZdo.Set(Key(i, "active"), false);
                flagZdo.Set(Key(i, "bg_resource_budget"), budget - requiredAmount);
                flagZdo.Set(Key(i, "bg_resource_debt"), Mathf.Max(0, flagZdo.GetInt(Key(i, "bg_resource_debt"), 0)) + requiredAmount);
                flagZdo.Set(Key(i, "bg_virtual"), true);
                flagZdo.Set(Key(i, "bg_state"), 1);
                flagZdo.Set(Key(i, "bg_ready"), nowTicks + TimeSpan.FromSeconds(growSeconds).Ticks);
                flagZdo.Set(CountKey, Mathf.Max(0, flagZdo.GetInt(CountKey, 0) - 1));
                double interval = 8.0 / Math.Max(0.1, efficiency);
                flagZdo.Set(NextPlantTicks, nowTicks + TimeSpan.FromSeconds(interval).Ticks);
                return 1;
            }
            return 0;
        }

        private static int AdvanceVirtualGrowth(ZDO flagZdo, long nowTicks, out int growing, out int mature)
        {
            growing = 0;
            mature = 0;
            int newlyMature = 0;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!IsPersistent(flagZdo, i) || !flagZdo.GetBool(Key(i, "bg_virtual"), false)) continue;
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

        private static bool TryHarvestOneVirtualMature(ZDO workerZdo, ZDO flagZdo, out string outputPrefab)
        {
            outputPrefab = string.Empty;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!IsPersistent(flagZdo, i) || !flagZdo.GetBool(Key(i, "bg_virtual"), false) ||
                    flagZdo.GetInt(Key(i, "bg_state"), 0) != 2) continue;
                string harvestPrefab = flagZdo.GetString(Key(i, "harvest"), string.Empty);
                if (!GardenWorkRuntime.TryResolveGatherableItemPrefab(harvestPrefab, out outputPrefab) || string.IsNullOrEmpty(outputPrefab))
                    continue;
                if (!ProductionDemandRuntime.AllowsPrefab(workerZdo, outputPrefab))
                    continue;

                int yield = EstimateHarvestYield(harvestPrefab);
                flagZdo.Set(Key(i, "bg_output_prefab"), outputPrefab);
                flagZdo.Set(Key(i, "bg_output_pending"), Mathf.Max(0, flagZdo.GetInt(Key(i, "bg_output_pending"), 0)) + Mathf.Max(1, yield));
                flagZdo.Set(Key(i, "active"), true);
                flagZdo.Set(Key(i, "bg_virtual"), false);
                flagZdo.Set(Key(i, "bg_state"), 0);
                flagZdo.Set(Key(i, "bg_ready"), 0L);
                flagZdo.Set(CountKey, Mathf.Clamp(flagZdo.GetInt(CountKey, 0) + 1, 0, MaxSlots));
                return true;
            }
            return false;
        }

        private static bool TryResolvePlan(ZDO flagZdo, int index, out string resourcePrefab, out string plantPrefab, out int requiredAmount)
        {
            resourcePrefab = string.Empty;
            plantPrefab = string.Empty;
            requiredAmount = 0;
            string harvest = flagZdo.GetString(Key(index, "harvest"), string.Empty);
            if (string.IsNullOrEmpty(harvest)) return false;

            if (SmartReplantBridge.TryBuildGardenPlan(harvest, 999999, out SmartReplantGardenPlan smart))
            {
                resourcePrefab = smart.ResourcePrefab;
                plantPrefab = smart.PlantPrefab;
                requiredAmount = Mathf.Max(1, smart.ResourceAmount);
                return !string.IsNullOrEmpty(resourcePrefab) && !string.IsNullOrEmpty(plantPrefab);
            }
            if (GardenWorkRuntime.TryGetFallbackReplantPlan(harvest, out resourcePrefab, out plantPrefab, out requiredAmount, out _))
            {
                requiredAmount = Mathf.Max(1, requiredAmount);
                return !string.IsNullOrEmpty(resourcePrefab) && !string.IsNullOrEmpty(plantPrefab);
            }
            return false;
        }

        private static double EstimateGrowthSeconds(string plantPrefab)
        {
            GameObject prefab = GardenWorkRuntime.ResolvePlantPrefab(plantPrefab);
            Plant plant = prefab != null ? prefab.GetComponent<Plant>() : null;
            if (plant == null) return DefaultGrowthSeconds;
            try
            {
                Type t = plant.GetType();
                float min = ReadFloatField(t, plant, "m_growTime");
                float max = ReadFloatField(t, plant, "m_growTimeMax");
                if (max <= 0f) max = ReadFloatField(t, plant, "m_growTimeMin");
                float seconds = Mathf.Max(min, max);
                return seconds > 1f ? seconds : DefaultGrowthSeconds;
            }
            catch { return DefaultGrowthSeconds; }
        }

        private static int EstimateHarvestYield(string harvestPrefab)
        {
            try
            {
                GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(harvestPrefab) : null;
                Pickable pickable = prefab != null ? (prefab.GetComponent<Pickable>() ?? prefab.GetComponentInChildren<Pickable>(true)) : null;
                if (pickable == null) return 1;
                FieldInfo f = pickable.GetType().GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null)
                {
                    object value = f.GetValue(pickable);
                    if (value is int i) return Mathf.Max(1, i);
                }
            }
            catch { }
            return 1;
        }

        private static float ReadFloatField(Type type, object instance, string name)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null) return 0f;
            object value = field.GetValue(instance);
            if (value is float f) return f;
            if (value is int i) return i;
            return 0f;
        }

        private static bool IsPlantPointFree(Vector3 position)
        {
            // Narrow overlap only: avoid spawning a duplicate if a physical crop already survived.
            int count = Physics.OverlapSphereNonAlloc(
                position + Vector3.up * 0.5f,
                0.35f,
                PlantPointOverlapBuffer,
                ~0,
                QueryTriggerInteraction.Collide);

            bool free = true;
            for (int i = 0; i < count; i++)
            {
                Collider hit = PlantPointOverlapBuffer[i];
                PlantPointOverlapBuffer[i] = null;
                if (hit == null) continue;
                if (hit.GetComponentInParent<Plant>() != null || hit.GetComponentInParent<Pickable>() != null)
                {
                    free = false;
                    break;
                }
            }

            for (int i = 0; i < count; i++)
                PlantPointOverlapBuffer[i] = null;

            return free;
        }

        private static bool TryGetFlag(ZDO workerZdo, out ZDO flagZdo)
        {
            flagZdo = null;
            if (workerZdo == null) return false;
            string id = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(id)) return false;
            if (!WorkFlagRegistry.TryGetPersistentState(id, out flagZdo) || flagZdo == null)
            {
                if (!WorkerZdo.TryReadWorkArea(workerZdo, out _, out _, out float radius) ||
                    !WorkFlagRegistry.TryGetNetworkState(id, radius, out _, out flagZdo) || flagZdo == null)
                    return false;
                WorkFlagRegistry.RememberPersistent(flagZdo);
            }
            return WorkFlagZdo.HasOrder(flagZdo, WorkOrder.Garden);
        }

        private static bool IsPersistent(ZDO zdo, int index) => zdo != null && zdo.GetBool(Key(index, "persistent"), false);
        private static int CountPending(ZDO zdo) => Mathf.Max(0, zdo.GetInt(CountKey, 0));
        private static int CountState(ZDO zdo, int wanted)
        {
            int count = 0;
            for (int i = 0; i < MaxSlots; i++)
                if (IsPersistent(zdo, i) && zdo.GetBool(Key(i, "bg_virtual"), false) && zdo.GetInt(Key(i, "bg_state"), 0) == wanted)
                    count++;
            return count;
        }
        private static int SumField(ZDO zdo, string field)
        {
            int total = 0;
            for (int i = 0; i < MaxSlots; i++)
                if (IsPersistent(zdo, i)) total += Mathf.Max(0, zdo.GetInt(Key(i, field), 0));
            return total;
        }
        private static Vector3 ReadPosition(ZDO zdo, int i) => new Vector3(
            zdo.GetFloat(Key(i, "x"), 0f), zdo.GetFloat(Key(i, "y"), 0f), zdo.GetFloat(Key(i, "z"), 0f));
        private static string Key(int i, string field) => $"vw_garden_slot_{i}_{field}";
    }
}
