using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Generic physical cooking loop for recipes discovered from the live Food Preparation Table
    /// and Cauldron recipe lists. Ingredients are fetched one type at a time, so even recipes with
    /// three or four different ingredients work with the Workers two-slot cargo limit.
    /// </summary>
    internal static class GenericCookingWorkRuntime
    {
        private const string OutputKey = "vw_cook_output";
        private const string StationKindKey = "vw_cook_station_kind";
        private const string StationNameKey = "vw_cook_station_name";
        private const string StationXKey = "vw_cook_station_x";
        private const string StationYKey = "vw_cook_station_y";
        private const string StationZKey = "vw_cook_station_z";
        private const string CollectedKey = "vw_cook_collected";
        private const string PlannedInputKey = "vw_cook_input";
        private const string PlannedInputAmountKey = "vw_cook_input_amount";
        private const string ReadyTicksKey = "vw_cook_ready_ticks";
        private const string DebugKey = "vw_baker_debug"; // shared worker UI line

        internal static bool HasPlan(ZDO zdo) => zdo != null && !string.IsNullOrWhiteSpace(zdo.GetString(OutputKey, string.Empty));

        internal static bool HasAvailableWork(Character worker, ZDO zdo, Vector3 center, float radius)
        {
            if (worker == null || zdo == null) return false;
            Dictionary<string, int> stockSnapshot = WorkerStorageRuntime.BuildGardenResourceStockSnapshot(worker, zdo);
            Func<string, int> stock = prefab => WorkerStorageRuntime.CountStockSnapshot(stockSnapshot, prefab);
            Func<string, bool> canRun = output => CookingRecipeRuntime.TryGetAnyRecipe(output, out CookingRecipeSpec recipe) &&
                                                  CookingWorkerTierRuntime.CanUseRecipe(worker, zdo, recipe) &&
                                                  HasAllIngredients(recipe, stock);
            return ProductionStationRuntime.TryFindDemand(center, radius, worker.transform.position, stock, canRun, out _, out _);
        }

        internal static bool TryPlanNext(Character worker, ZDO zdo, Vector3 center, float radius, out string reason)
        {
            reason = "kein Kochauftrag an einer Station";
            if (worker == null || zdo == null) return false;
            if (WorkerZdo.HasCargo(zdo)) return false;

            if (HasPlan(zdo))
            {
                // v0.5.243: an in-flight generic recipe owns the cooking worker until it either
                // resumes or is explicitly invalidated. If the next ingredient is temporarily
                // missing, keep the recipe buffer pinned instead of falling through into the
                // legacy flour/dough/bread planner and letting two production state machines
                // overwrite each other's target/plan fields.
                if (TryResumePlan(worker, zdo, out reason)) return true;
                if (HasPlan(zdo))
                {
                    WorkerZdo.ClearTarget(zdo);
                    zdo.Set(WorkerZdo.State, (int)WorkerState.NeedsResource);
                    zdo.Set(DebugKey, reason);
                    return true;
                }
                // Invalid/stale plans are cleared by TryResumePlan; continue and look for a new
                // runnable station bill in the same scheduler pass.
            }

            Dictionary<string, int> stockSnapshot = WorkerStorageRuntime.BuildGardenResourceStockSnapshot(worker, zdo);
            Func<string, int> stock = prefab => WorkerStorageRuntime.CountStockSnapshot(stockSnapshot, prefab);
            Func<string, bool> canRun = output => CookingRecipeRuntime.TryGetAnyRecipe(output, out CookingRecipeSpec recipe) &&
                                                  CookingWorkerTierRuntime.CanUseRecipe(worker, zdo, recipe) &&
                                                  HasAllIngredients(recipe, stock);

            if (!ProductionStationRuntime.TryFindDemand(center, radius, worker.transform.position, stock, canRun,
                out ProductionStationRef station, out ProductionDemand demand))
            {
                // Give a useful reason when a food order exists but ingredients are missing.
                if (ProductionStationRuntime.TryFindDemand(center, radius, worker.transform.position, stock,
                    output => CookingRecipeRuntime.TryGetAnyRecipe(output, out CookingRecipeSpec candidateRecipe) &&
                              CookingWorkerTierRuntime.CanUseRecipe(worker, zdo, candidateRecipe),
                    out ProductionStationRef waitingStation, out ProductionDemand waitingDemand) &&
                    CookingRecipeRuntime.TryGetRecipe(waitingStation.Kind, waitingDemand.Output, out CookingRecipeSpec waitingRecipe))
                {
                    reason = waitingStation.DisplayName + ": " + MissingIngredients(waitingRecipe, stock);
                    zdo.Set(DebugKey, reason);
                }
                return false;
            }

            if (!CookingRecipeRuntime.TryGetRecipe(station.Kind, demand.Output, out CookingRecipeSpec recipeSpec)) return false;
            if (!CookingWorkerTierRuntime.CanUseRecipe(worker, zdo, recipeSpec))
            {
                reason = station.DisplayName + ": Kochstufe reicht nicht (" + CookingWorkerTierRuntime.DescribeTier(worker, zdo) + ")";
                zdo.Set(DebugKey, reason);
                return false;
            }

            if (!ProductionOrderClaimRuntime.TryClaim(station.Zdo, zdo, demand.Output, Math.Max(1, recipeSpec.OutputAmount), out _))
            {
                reason = station.DisplayName + ": Kochauftrag konnte nicht reserviert werden";
                zdo.Set(DebugKey, reason);
                return false;
            }

            WritePlan(zdo, station, recipeSpec.OutputPrefab);
            if (!TryQueueNextInput(worker, zdo, recipeSpec, out reason))
            {
                // The availability probe and the actual pickup plan are separated by a frame and
                // another worker/player can consume the ingredient in between. Do not leave a stale
                // recipe plan pinned forever in that race; release it so another runnable station
                // bill can be selected on the next scheduler pass.
                ClearPlan(zdo);
                zdo.Set(DebugKey, reason);
                return false;
            }
            zdo.Set(DebugKey, reason);
            return true;
        }

        internal static bool TryResumeCargo(Character worker, ZDO zdo, out string reason)
        {
            reason = string.Empty;
            if (worker == null || zdo == null || !HasPlan(zdo) || !WorkerZdo.HasCargo(zdo)) return false;
            if (!TryReadPlan(zdo, out ProductionStationKind kind, out string output, out string stationName, out Vector3 stationPosition) ||
                !CookingRecipeRuntime.TryGetRecipe(kind, output, out CookingRecipeSpec recipe) ||
                !CookingWorkerTierRuntime.CanUseRecipe(worker, zdo, recipe))
            {
                // v0.5.244 recovery: a stale/invalid generic plan plus persisted cargo used to be
                // impossible to recover. TryPlanNext refuses to plan while cargo exists, while this
                // method returned false without clearing the bad plan. Route the real cargo back to
                // storage and release the invalid recipe state instead of leaving the cook parked.
                ClearPlan(zdo);
                if (WorkerZdo.TryReadCargo(zdo, out string staleCargoPrefab, out int staleCargoAmount) && staleCargoAmount > 0)
                {
                    BakerWorkRuntime.TryAssignCargoDelivery(worker, zdo, staleCargoPrefab, staleCargoAmount, out reason);
                    zdo.Set(DebugKey, reason);
                    return true;
                }
                return false;
            }

            if (ProductionStationRuntime.TryFindNear(output, stationPosition, 5f, out ProductionStationRef resumeStation) && resumeStation?.Zdo != null)
                ProductionOrderClaimRuntime.EnsureClaim(resumeStation.Zdo, zdo, output, Math.Max(1, recipe.OutputAmount));
            else
                ProductionOrderClaimRuntime.TouchWorkerClaims(zdo);

            string planned = zdo.GetString(PlannedInputKey, string.Empty);
            int plannedAmount = Mathf.Max(0, zdo.GetInt(PlannedInputAmountKey, 0));
            if (!string.IsNullOrEmpty(planned) && plannedAmount > 0 && WorkerZdo.GetCargoAmount(zdo, planned) > 0)
            {
                WorkerZdo.WriteTarget(zdo, stationName, stationPosition, BakerWorkRuntime.TargetKind);
                zdo.Set(WorkerZdo.State, (int)WorkerState.MoveToTarget);
                reason = $"{stationName}: {CookingRecipeRuntime.DisplayIngredient(planned)} wird zur Kochstation getragen";
                zdo.Set(DebugKey, reason);
                return true;
            }

            // v0.5.236 recovery: older/in-flight states can contain more than one recipe ingredient
            // in the two physical cargo slots (e.g. Sausages: Entrails + RawMeat + Thistle).
            // Any cargo that belongs to the active recipe must be brought to the station and unloaded
            // into the recipe buffer instead of being treated as unrelated cargo. This makes cooking
            // independent from the Worker's generic two-slot inventory size.
            if (HasRecipeIngredientCargo(zdo, recipe))
            {
                WorkerZdo.WriteTarget(zdo, stationName, stationPosition, BakerWorkRuntime.TargetKind);
                zdo.Set(WorkerZdo.State, (int)WorkerState.MoveToTarget);
                reason = $"{stationName}: vorhandene Kochzutaten werden zur Station gebracht";
                zdo.Set(DebugKey, reason);
                return true;
            }

            // Unexpected cargo is real inventory: return it safely instead of consuming it as food.
            if (WorkerZdo.TryReadCargo(zdo, out string cargoPrefab, out int cargoAmount) && cargoAmount > 0)
            {
                ClearPlan(zdo);
                BakerWorkRuntime.TryAssignCargoDelivery(worker, zdo, cargoPrefab, cargoAmount, out reason);
                zdo.Set(DebugKey, reason);
                return true;
            }
            return false;
        }

        internal static bool TryCollectInput(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null || !HasPlan(zdo)) return false;
            if (!WorkerZdo.TryReadTarget(zdo, out string targetInput, out string targetKind, out Vector3 targetPosition) ||
                !string.Equals(targetKind, BakerWorkRuntime.InputSourceTargetKind, StringComparison.Ordinal)) return false;

            if (!TryReadPlan(zdo, out ProductionStationKind kind, out string output, out string stationName, out Vector3 stationPosition) ||
                !CookingRecipeRuntime.TryGetRecipe(kind, output, out CookingRecipeSpec recipe) ||
                !CookingWorkerTierRuntime.CanUseRecipe(worker, zdo, recipe))
            {
                ClearPlan(zdo);
                return true;
            }

            string plannedInput = zdo.GetString(PlannedInputKey, string.Empty);
            int plannedAmount = Mathf.Max(1, zdo.GetInt(PlannedInputAmountKey, 1));
            if (!string.Equals(targetInput, plannedInput, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryQueueNextInput(worker, zdo, recipe, out string reroute))
                    SetWaitingForIngredient(zdo, reroute);
                return true;
            }

            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Container source = null;
            int availableAtSource = 0;
            float best = float.MaxValue;
            WorkerStorageRuntime.ForEachContainerNear(targetPosition, Mathf.Max(4f, WorkerStorageRuntime.ContainerAccessRadius + 1f), candidate =>
            {
                if (candidate == null || !WorkerStorageRuntime.CanUseContainer(candidate, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(candidate, ownerId)) return;
                WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(candidate);
                if (role == WorkerContainerRole.Import || role == WorkerContainerRole.Export) return;
                Inventory inv = candidate.GetInventory();
                int reserve = WorkerContainerRoleRuntime.ProtectLastItem(candidate) ? 1 : 0;
                int available = Mathf.Max(0, WorkerStorageRuntime.CountPrefab(inv, plannedInput) - reserve);
                if (available <= 0) return;
                float sqr = (candidate.transform.position - targetPosition).sqrMagnitude;
                if (sqr >= best) return;
                best = sqr;
                source = candidate;
                availableAtSource = available;
            });

            if (source == null)
            {
                // The selected chest can disappear, be emptied by the player, or stream out while
                // the cook is walking to it. If no alternate source exists, leave Collect and park
                // in NeedsResource. Otherwise WorkerNavigation would call this expensive source scan
                // every frame forever against the stale target.
                if (!TryQueueNextInput(worker, zdo, recipe, out string reroute))
                    SetWaitingForIngredient(zdo, reroute);
                return true;
            }
            if (!WorkerStorageRuntime.TryOwn(source))
            {
                zdo.Set(DebugKey, $"warte auf Besitz der Zutaten-Kiste ({CookingRecipeRuntime.DisplayIngredient(plannedInput)})");
                return true;
            }

            int take = Mathf.Min(plannedAmount, availableAtSource);
            Inventory inventory = source.GetInventory();
            int removed = 0;
            while (removed < take)
            {
                ItemDrop.ItemData item = WorkerStorageRuntime.FindPrefab(inventory, plannedInput);
                if (item == null || !WorkerStorageRuntime.TryRemoveOne(inventory, item)) break;
                removed++;
            }
            if (removed <= 0)
            {
                if (!TryQueueNextInput(worker, zdo, recipe, out string reroute))
                    SetWaitingForIngredient(zdo, reroute);
                return true;
            }

            WorkerStorageRuntime.PersistContainer(source, inventory);
            WorkerZdo.SetCargo(zdo, plannedInput, removed);
            zdo.Set(PlannedInputAmountKey, removed);
            WorkerZdo.WriteTarget(zdo, stationName, stationPosition, BakerWorkRuntime.TargetKind);
            zdo.Set(WorkerZdo.State, (int)WorkerState.MoveToTarget);
            zdo.Set(DebugKey, $"{stationName}: traegt {CookingRecipeRuntime.DisplayIngredient(plannedInput)} x{removed} zur Station");
            return true;
        }

        internal static bool TryWork(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null || !HasPlan(zdo)) return false;
            if (!WorkerZdo.TryReadTarget(zdo, out _, out string targetKind, out Vector3 position) ||
                !string.Equals(targetKind, BakerWorkRuntime.TargetKind, StringComparison.Ordinal)) return false;
            if (!WorkerInteractionRuntime.IsInsideAura(worker.transform.position, position) &&
                !LocalWorkFallbackRuntime.IsActive(zdo, zdo.GetString(StationNameKey, string.Empty), targetKind)) return false;

            if (!TryReadPlan(zdo, out ProductionStationKind kind, out string output, out string stationName, out Vector3 stationPosition) ||
                !CookingRecipeRuntime.TryGetRecipe(kind, output, out CookingRecipeSpec recipe) ||
                !CookingWorkerTierRuntime.CanUseRecipe(worker, zdo, recipe))
            {
                ClearPlan(zdo);
                return true;
            }

            if (!ProductionStationRuntime.TryFindNear(output, stationPosition, 5f, out ProductionStationRef station) || station.Zdo == null)
            {
                ProductionOrderClaimRuntime.TouchWorkerClaims(zdo);
                zdo.Set(DebugKey, stationName + ": Kochstation nicht geladen/verfuegbar; Zutaten bleiben reserviert beim Workerplan");
                zdo.Set(WorkerZdo.State, (int)WorkerState.NeedsResource);
                return true;
            }

            ProductionOrderClaimRuntime.EnsureClaim(station.Zdo, zdo, output, Math.Max(1, recipe.OutputAmount));

            // v0.5.236: unload every carried ingredient that belongs to this recipe, not just the
            // single currently planned stack. The station-side recipe buffer (CollectedKey) is the
            // authoritative ingredient basket; generic Worker cargo is only the transport leg.
            // This prevents 3+ ingredient recipes from deadlocking on the two-slot Worker inventory.
            DepositRecipeIngredientCargo(zdo, recipe);
            zdo.Set(PlannedInputKey, string.Empty);
            zdo.Set(PlannedInputAmountKey, 0);

            if (!AllCollected(zdo, recipe))
            {
                if (TryQueueNextInput(worker, zdo, recipe, out string nextReason))
                {
                    zdo.Set(DebugKey, nextReason);
                    return true;
                }
                zdo.Set(DebugKey, station.DisplayName + ": " + nextReason);
                zdo.Set(WorkerZdo.State, (int)WorkerState.NeedsResource);
                return true;
            }

            long now = DateTime.UtcNow.Ticks;
            long ready = zdo.GetLong(ReadyTicksKey, 0L);
            if (ready <= 0L)
            {
                zdo.Set(ReadyTicksKey, now + TimeSpan.FromSeconds(Mathf.Max(1f, recipe.WorkSeconds)).Ticks);
                zdo.Set(DebugKey, $"{station.DisplayName}: {recipe.DisplayName} wird zubereitet...");
                return true;
            }
            if (now < ready) return true;

            // Finish the already gathered batch even if the player removed the bill mid-cycle.
            // No new batch will be planned afterwards because the station order is gone.
            WorkerZdo.AddCargoAmount(zdo, recipe.OutputPrefab, recipe.OutputAmount);
            if (ProductionOrderRuntime.HasOrderFor(station.Zdo, output))
                ProductionOrderRuntime.RegisterProduced(station.Zdo, output, recipe.OutputAmount);
                ProductionMetricsRuntime.RecordProduced(zdo, output, recipe.OutputAmount);
                for (int metricI = 0; metricI < recipe.Ingredients.Count; metricI++)
                {
                    CookingIngredientSpec metricIng = recipe.Ingredients[metricI];
                    if (metricIng != null && metricIng.Amount > 0) ProductionMetricsRuntime.RecordConsumed(zdo, metricIng.Prefab, metricIng.Amount);
                }
            if (LocalWorkFallbackRuntime.IsActive(zdo, stationName, targetKind)) LocalWorkFallbackRuntime.Clear(zdo);

            string cookedName = recipe.DisplayName;
            int cookedAmount = recipe.OutputAmount;
            string cookedPrefab = recipe.OutputPrefab;
            ClearPlan(zdo);
            if (!BakerWorkRuntime.TryAssignCargoDelivery(worker, zdo, cookedPrefab, cookedAmount, out string deliveryReason))
            {
                zdo.Set(DebugKey, $"{station.DisplayName}: {cookedName} x{cookedAmount} fertig; keine Zielkiste frei");
                return true;
            }
            zdo.Set(DebugKey, $"{station.DisplayName}: {cookedName} x{cookedAmount} fertig -> {deliveryReason}");
            return true;
        }

        private static bool TryResumePlan(Character worker, ZDO zdo, out string reason)
        {
            reason = string.Empty;
            if (!TryReadPlan(zdo, out ProductionStationKind kind, out string output, out string stationName, out Vector3 stationPosition) ||
                !CookingRecipeRuntime.TryGetRecipe(kind, output, out CookingRecipeSpec recipe) ||
                !CookingWorkerTierRuntime.CanUseRecipe(worker, zdo, recipe))
            {
                ClearPlan(zdo);
                return false;
            }

            if (ProductionStationRuntime.TryFindNear(output, stationPosition, 5f, out ProductionStationRef claimStation) && claimStation?.Zdo != null)
                ProductionOrderClaimRuntime.EnsureClaim(claimStation.Zdo, zdo, output, Math.Max(1, recipe.OutputAmount));
            else
                ProductionOrderClaimRuntime.TouchWorkerClaims(zdo);

            if (AllCollected(zdo, recipe))
            {
                WorkerZdo.WriteTarget(zdo, stationName, stationPosition, BakerWorkRuntime.TargetKind);
                reason = $"{stationName}: Zutaten komplett, {recipe.DisplayName} zubereiten";
                return true;
            }
            return TryQueueNextInput(worker, zdo, recipe, out reason);
        }

        private static bool TryQueueNextInput(Character worker, ZDO zdo, CookingRecipeSpec recipe, out string reason)
        {
            reason = "Zutaten fehlen";
            if (worker == null || zdo == null || recipe == null) return false;
            Dictionary<string, int> collected = ReadCollected(zdo);
            for (int i = 0; i < recipe.Ingredients.Count; i++)
            {
                CookingIngredientSpec ing = recipe.Ingredients[i];
                int have = collected.TryGetValue(ing.Prefab, out int current) ? current : 0;
                int remaining = Mathf.Max(0, ing.Amount - have);
                if (remaining <= 0) continue;

                int maxCarry = Mathf.Max(1, WorkerCargoRuntime.GetMaxSinglePrefabLoad(worker, ing.Prefab, remaining));
                if (!TryFindInputSourceChunk(worker, zdo, ing.Prefab, maxCarry, out Container source, out Vector3 sourcePosition, out int amount) || source == null || amount <= 0)
                {
                    reason = $"wartet auf {ing.DisplayName} ({have}/{ing.Amount})";
                    return false;
                }

                zdo.Set(PlannedInputKey, ing.Prefab);
                zdo.Set(PlannedInputAmountKey, amount);
                WorkerZdo.WriteTarget(zdo, ing.Prefab, sourcePosition, BakerWorkRuntime.InputSourceTargetKind);
                zdo.Set(WorkerZdo.State, (int)WorkerState.MoveToTarget);
                reason = $"{zdo.GetString(StationNameKey, "Kochstation")}: hole {ing.DisplayName} x{amount} ({have}/{ing.Amount} bereit)";
                return true;
            }
            return false;
        }



        private static void SetWaitingForIngredient(ZDO zdo, string reason)
        {
            if (zdo == null) return;
            zdo.Set(PlannedInputKey, string.Empty);
            zdo.Set(PlannedInputAmountKey, 0);
            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.State, (int)WorkerState.NeedsResource);
            zdo.Set(DebugKey, string.IsNullOrEmpty(reason) ? "wartet auf Kochzutat" : reason);
        }

        private static bool HasRecipeIngredientCargo(ZDO zdo, CookingRecipeSpec recipe)
        {
            if (zdo == null || recipe == null) return false;
            List<WorkerZdo.CargoStack> stacks = WorkerZdo.ReadCargoStacks(zdo);
            for (int i = 0; i < stacks.Count; i++)
            {
                WorkerZdo.CargoStack stack = stacks[i];
                if (stack.Amount <= 0 || string.IsNullOrEmpty(stack.Prefab)) continue;
                for (int j = 0; j < recipe.Ingredients.Count; j++)
                {
                    CookingIngredientSpec ing = recipe.Ingredients[j];
                    if (ing != null && string.Equals(ing.Prefab, stack.Prefab, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        private static void DepositRecipeIngredientCargo(ZDO zdo, CookingRecipeSpec recipe)
        {
            if (zdo == null || recipe == null) return;
            Dictionary<string, int> collected = ReadCollected(zdo);
            List<WorkerZdo.CargoStack> stacks = WorkerZdo.ReadCargoStacks(zdo);
            for (int i = 0; i < recipe.Ingredients.Count; i++)
            {
                CookingIngredientSpec ing = recipe.Ingredients[i];
                if (ing == null || string.IsNullOrEmpty(ing.Prefab) || ing.Amount <= 0) continue;
                int have = collected.TryGetValue(ing.Prefab, out int current) ? current : 0;
                int needed = Mathf.Max(0, ing.Amount - have);
                if (needed <= 0) continue;
                int carried = WorkerZdo.GetCargoAmount(zdo, ing.Prefab);
                int moved = Mathf.Min(needed, carried);
                if (moved <= 0) continue;
                WorkerZdo.RemoveCargoAmount(zdo, ing.Prefab, moved);
                collected[ing.Prefab] = have + moved;
            }
            WriteCollected(zdo, collected);
        }

        private static bool TryFindInputSourceChunk(Character worker, ZDO zdo, string prefab, int desired,
            out Container container, out Vector3 position, out int amount)
        {
            container = null;
            position = default;
            amount = 0;
            if (worker == null || zdo == null || string.IsNullOrEmpty(prefab) || desired <= 0) return false;

            Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
            float homeRadius = WorkerStorageRuntime.GetSearchRadius(zdo);
            bool hasWorkArea = WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 workCenter, out _);
            float workRadius = WorkerStorageRuntime.GetWorkAreaStorageSearchRadius(zdo);
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Vector3 searchCenter = hasWorkArea ? workCenter : home;
            float searchRadius = hasWorkArea ? workRadius : homeRadius;

            bool found = SharedStorageStockIndexRuntime.TryFindItemSource(
                prefab, searchCenter, searchRadius, worker.transform.position, desired,
                candidate =>
                {
                    if (candidate == null || !WorkerStorageRuntime.CanUseContainer(candidate, ownerId) ||
                        !WorkerContainerRoleRuntime.MatchesOwner(candidate, ownerId)) return false;
                    WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(candidate);
                    return role != WorkerContainerRole.Import && role != WorkerContainerRole.Export;
                },
                candidate => WorkerContainerRoleRuntime.ProtectLastItem(candidate) ? 1 : 0,
                out container, out int usable, out position);

            if (!found || container == null || usable <= 0) return false;
            amount = Mathf.Min(desired, usable);
            if (WorkerContainerRoleRuntime.GetRole(container) == WorkerContainerRole.Normal)
                ContainerLedgerRuntime.MarkSorterStorage(container, ownerId);
            return amount > 0;
        }

        private static bool HasAllIngredients(CookingRecipeSpec recipe, Func<string, int> stock)
        {
            if (recipe == null || stock == null) return false;
            for (int i = 0; i < recipe.Ingredients.Count; i++)
            {
                CookingIngredientSpec ing = recipe.Ingredients[i];
                if (ing == null || ing.Amount <= 0) continue;
                if (stock(ing.Prefab) < ing.Amount) return false;
            }
            return recipe.Ingredients.Count > 0;
        }

        private static string MissingIngredients(CookingRecipeSpec recipe, Func<string, int> stock)
        {
            if (recipe == null) return "unbekanntes Kochrezept";
            var missing = new List<string>();
            for (int i = 0; i < recipe.Ingredients.Count; i++)
            {
                CookingIngredientSpec ing = recipe.Ingredients[i];
                int have = stock != null ? Mathf.Max(0, stock(ing.Prefab)) : 0;
                if (have < ing.Amount) missing.Add($"{ing.DisplayName} {have}/{ing.Amount}");
            }
            return missing.Count == 0 ? "Zutaten bereit" : "wartet auf " + string.Join(", ", missing);
        }

        private static bool AllCollected(ZDO zdo, CookingRecipeSpec recipe)
        {
            Dictionary<string, int> collected = ReadCollected(zdo);
            for (int i = 0; i < recipe.Ingredients.Count; i++)
            {
                CookingIngredientSpec ing = recipe.Ingredients[i];
                int have = collected.TryGetValue(ing.Prefab, out int amount) ? amount : 0;
                if (have < ing.Amount) return false;
            }
            return true;
        }

        private static void AddCollected(ZDO zdo, string prefab, int amount)
        {
            if (zdo == null || string.IsNullOrEmpty(prefab) || amount <= 0) return;
            Dictionary<string, int> collected = ReadCollected(zdo);
            collected[prefab] = (collected.TryGetValue(prefab, out int old) ? old : 0) + amount;
            WriteCollected(zdo, collected);
        }

        private static Dictionary<string, int> ReadCollected(ZDO zdo)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (zdo == null) return result;
            string raw = zdo.GetString(CollectedKey, string.Empty);
            if (string.IsNullOrEmpty(raw)) return result;
            string[] rows = raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < rows.Length; i++)
            {
                int eq = rows[i].LastIndexOf('=');
                if (eq <= 0 || eq >= rows[i].Length - 1) continue;
                string prefab = rows[i].Substring(0, eq);
                if (int.TryParse(rows[i].Substring(eq + 1), out int amount) && amount > 0) result[prefab] = amount;
            }
            return result;
        }

        private static void WriteCollected(ZDO zdo, Dictionary<string, int> values)
        {
            if (zdo == null) return;
            if (values == null || values.Count == 0) { zdo.Set(CollectedKey, string.Empty); return; }
            var rows = new List<string>(values.Count);
            foreach (KeyValuePair<string, int> pair in values)
                if (!string.IsNullOrEmpty(pair.Key) && pair.Value > 0) rows.Add(pair.Key + "=" + pair.Value);
            zdo.Set(CollectedKey, string.Join(";", rows));
        }

        private static void WritePlan(ZDO zdo, ProductionStationRef station, string output)
        {
            zdo.Set(OutputKey, output ?? string.Empty);
            zdo.Set(StationKindKey, (int)station.Kind);
            zdo.Set(StationNameKey, station.DisplayName ?? "Kochstation");
            zdo.Set(StationXKey, station.Position.x);
            zdo.Set(StationYKey, station.Position.y);
            zdo.Set(StationZKey, station.Position.z);
            zdo.Set(CollectedKey, string.Empty);
            zdo.Set(PlannedInputKey, string.Empty);
            zdo.Set(PlannedInputAmountKey, 0);
            zdo.Set(ReadyTicksKey, 0L);
        }

        private static bool TryReadPlan(ZDO zdo, out ProductionStationKind kind, out string output, out string stationName, out Vector3 stationPosition)
        {
            kind = ProductionStationKind.None;
            output = string.Empty;
            stationName = string.Empty;
            stationPosition = default;
            if (zdo == null) return false;
            output = zdo.GetString(OutputKey, string.Empty);
            kind = (ProductionStationKind)zdo.GetInt(StationKindKey, 0);
            stationName = zdo.GetString(StationNameKey, string.Empty);
            stationPosition = new Vector3(zdo.GetFloat(StationXKey, 0f), zdo.GetFloat(StationYKey, 0f), zdo.GetFloat(StationZKey, 0f));
            return !string.IsNullOrEmpty(output) && kind != ProductionStationKind.None && !string.IsNullOrEmpty(stationName);
        }

        private static void ClearPlan(ZDO zdo)
        {
            if (zdo == null) return;
            ProductionOrderClaimRuntime.ReleaseClaimsForWorker(zdo);
            zdo.Set(OutputKey, string.Empty);
            zdo.Set(StationKindKey, 0);
            zdo.Set(StationNameKey, string.Empty);
            zdo.Set(StationXKey, 0f);
            zdo.Set(StationYKey, 0f);
            zdo.Set(StationZKey, 0f);
            zdo.Set(CollectedKey, string.Empty);
            zdo.Set(PlannedInputKey, string.Empty);
            zdo.Set(PlannedInputAmountKey, 0);
            zdo.Set(ReadyTicksKey, 0L);
            WorkerZdo.ClearTarget(zdo);
        }
    }
}
