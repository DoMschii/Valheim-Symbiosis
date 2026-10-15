using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Persistent Baker simulation. Station orders remain authoritative on the real station ZDO;
    /// source ingredients are committed through the container ledger and finished products are
    /// staged on the Workzone FieldDepot. This means leaving the player bubble no longer freezes
    /// the Baker and no input can be consumed unless an output destination exists.
    /// </summary>
    internal static class BackgroundBakerRuntime
    {
        private const string ProgressKey = "vw_bg_baker_progress";
        private const string ProbeNextTicksKey = "vw_bg_baker_probe_next_ticks";
        private const string ProbeHasWorkKey = "vw_bg_baker_probe_has_work";
        private const int MaxBatchesPerTick = 32;
        private static readonly long ProbeIntervalTicks = TimeSpan.FromSeconds(4).Ticks;

        private sealed class RemovalReservation
        {
            internal ZDO Source;
            internal string TransactionId;
            internal int Amount;
        }

        internal static bool HasBackgroundWork(ZDO workerZdo)
        {
            if (workerZdo == null) return false;
            if (!CookingWorkerTierRuntime.CanUseAdvancedProduction(workerZdo)) return false;

            // A loaded cooking cycle may already have ingredients parked in the persistent
            // GenericCooking plan. Do not let background simulation start a second bill while that
            // physical cycle is unfinished; otherwise a leave/re-enter of the player bubble can
            // overshoot the station target with two concurrent batches.
            if (GenericCookingWorkRuntime.HasPlan(workerZdo)) return true;

            long now = DateTime.UtcNow.Ticks;
            long next = workerZdo.GetLong(ProbeNextTicksKey, 0L);
            if (next > now)
                return workerZdo.GetBool(ProbeHasWorkKey, false);

            bool result = false;
            try
            {
                if (WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius) &&
                    WorkerLogisticsRuntime.TryFindPersistentFieldDepotForWorkArea(workerZdo, out ZDO destination, out _) &&
                    destination != null)
                {
                    Func<string, int> stock = prefab => CountPersistentStock(workerZdo, prefab);
                    Func<string, bool> canRun = output => CanRunAnyRecipe(workerZdo, output);

                    result = ProductionStationRuntime.TryFindPersistentDemand(
                        center,
                        radius,
                        workerZdo.GetPosition(),
                        stock,
                        canRun,
                        out _,
                        out _,
                        out _);
                }
            }
            catch
            {
                result = false;
            }

            workerZdo.Set(ProbeHasWorkKey, result);
            workerZdo.Set(ProbeNextTicksKey, now + ProbeIntervalTicks);
            return result;
        }

        internal static string TickBackground(ZDO workerZdo, double deltaSeconds, float efficiency)
        {
            if (workerZdo == null) return "cooking:no-worker";
            if (!CookingWorkerTierRuntime.CanUseAdvancedProduction(workerZdo)) return "cooking:tier-no-background-production";
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out WorkOrder order, out Vector3 center, out float radius) || order != WorkOrder.Bake)
                return "baking:no-workzone";

            // Preserve a concrete physical recipe cycle across the bubble handoff. Its station-side
            // ingredient basket lives on the Worker ZDO and is intentionally not consumed twice by
            // the background scheduler. The cycle resumes as soon as the Worker becomes physical.
            if (GenericCookingWorkRuntime.HasPlan(workerZdo))
                return "baking:physical-recipe-plan-preserved";

            if (!WorkerLogisticsRuntime.TryFindPersistentFieldDepotForWorkArea(workerZdo, out ZDO destination, out _) || destination == null)
                return "baking:waiting-no-field-depot";

            Func<string, int> stock = prefab => CountPersistentStock(workerZdo, prefab);
            Func<string, bool> canRun = output => CanRunAnyRecipe(workerZdo, output);

            Vector3 from = workerZdo.GetPosition();
            if (!ProductionStationRuntime.TryFindPersistentDemand(center, radius, from, stock, canRun,
                out ZDO stationZdo, out ProductionStationKind stationKind, out ProductionDemand demand))
                return "baking:waiting-no-persistent-station-demand";

            if (!TryResolveRecipe(stationKind, demand.Output, out string outputPrefab, out int outputAmount,
                    out float secondsPerBatch, out CookingRecipeSpec cookingRecipe, out string legacyInput, out int legacyInputAmount))
                return "baking:unsupported-order";

            float progress = Mathf.Max(0f, workerZdo.GetFloat(ProgressKey, 0f));
            progress += (float)Math.Max(0.0, deltaSeconds) * Mathf.Clamp(efficiency, 0.05f, 1f);
            int ready = Mathf.Min(MaxBatchesPerTick, Mathf.FloorToInt(progress / secondsPerBatch));
            if (ready <= 0)
            {
                workerZdo.Set(ProgressKey, progress);
                return $"baking:{ProductionStationRuntime.DisplayName(stationKind)} {Mathf.Clamp01(progress / secondsPerBatch) * 100f:0}%";
            }

            int maxByDemand = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(1, demand.Needed) / (float)Mathf.Max(1, outputAmount)));
            ready = Mathf.Min(ready, maxByDemand);
            int completed = 0;
            for (int i = 0; i < ready; i++)
            {
                if (!CanStage(destination, outputPrefab, outputAmount)) break;
                if (!ProductionOrderClaimRuntime.TryClaim(stationZdo, workerZdo, outputPrefab, Math.Max(1, outputAmount), out _)) break;

                string batchId = $"baker:{workerZdo.m_uid}:{stationZdo.m_uid}:{outputPrefab}:{DateTime.UtcNow.Ticks}:{i}";
                var reservations = new List<RemovalReservation>(4);
                bool reservedAll = cookingRecipe != null
                    ? TryReserveCookingIngredients(workerZdo, cookingRecipe, batchId, reservations)
                    : TryReserveIngredient(workerZdo, legacyInput, legacyInputAmount, batchId + ":legacy", reservations);

                if (!reservedAll)
                {
                    RollbackReservations(reservations);
                    ProductionOrderClaimRuntime.ReleaseClaimsForWorker(workerZdo);
                    break;
                }

                bool committedAll = true;
                for (int r = 0; r < reservations.Count; r++)
                {
                    RemovalReservation reservation = reservations[r];
                    if (!ContainerLedgerRuntime.TryCommitRemoval(reservation.Source, reservation.TransactionId,
                            out ContainerLedgerRuntime.TransactionInfo committed, out _) || committed == null || committed.Amount < reservation.Amount)
                    {
                        committedAll = false;
                        break;
                    }
                }

                if (!committedAll)
                {
                    // Reserved-but-not-yet-committed entries are safe to roll back. A committed
                    // source is never minted back; physical reality remains authoritative.
                    RollbackReservations(reservations);
                    ProductionOrderClaimRuntime.ReleaseClaimsForWorker(workerZdo);
                    break;
                }

                AdvancedFieldDepotRuntime.QueueBackgroundDelivery(destination, outputPrefab, outputAmount);
                for (int r = 0; r < reservations.Count; r++)
                    ContainerLedgerRuntime.CompleteTransaction(reservations[r].Source, reservations[r].TransactionId);
                ProductionOrderRuntime.RegisterProduced(stationZdo, outputPrefab, outputAmount);
                ProductionOrderClaimRuntime.ReleaseClaimsForWorker(workerZdo);
                completed++;
            }

            if (completed > 0) progress -= completed * secondsPerBatch;
            workerZdo.Set(ProgressKey, Mathf.Min(Mathf.Max(0f, progress), secondsPerBatch * 2f));
            return completed > 0
                ? $"baking:simulated {completed} batch(es) {ProductionOrderRuntime.DisplayProduct(outputPrefab)} -> field depot"
                : $"baking:waiting-input-or-output ({ProductionOrderRuntime.DisplayProduct(outputPrefab)})";
        }

        private static bool CanRunAnyRecipe(ZDO workerZdo, string output)
        {
            if (workerZdo == null || string.IsNullOrWhiteSpace(output)) return false;

            if (string.Equals(output, ProductionOrderRuntime.BarleyFlour, StringComparison.OrdinalIgnoreCase))
                return CountAvailablePersistentInput(workerZdo, "Barley") >= 1;
            if (string.Equals(output, ProductionOrderRuntime.BreadDough, StringComparison.OrdinalIgnoreCase))
                return CountAvailablePersistentInput(workerZdo, ProductionOrderRuntime.BarleyFlour) >= 10;
            if (string.Equals(output, ProductionOrderRuntime.Bread, StringComparison.OrdinalIgnoreCase))
                return CountAvailablePersistentInput(workerZdo, ProductionOrderRuntime.BreadDough) >= 1;

            if (!CookingRecipeRuntime.TryGetAnyRecipe(output, out CookingRecipeSpec recipe) || recipe == null) return false;
            for (int i = 0; i < recipe.Ingredients.Count; i++)
            {
                CookingIngredientSpec ing = recipe.Ingredients[i];
                if (ing == null || string.IsNullOrEmpty(ing.Prefab) || ing.Amount <= 0) continue;
                if (CountAvailablePersistentInput(workerZdo, ing.Prefab) < ing.Amount) return false;
            }
            return recipe.Ingredients.Count > 0;
        }

        private static bool TryResolveRecipe(ProductionStationKind kind, string output,
            out string produced, out int producedAmount, out float workSeconds,
            out CookingRecipeSpec cookingRecipe, out string legacyInput, out int legacyInputAmount)
        {
            produced = output ?? string.Empty;
            producedAmount = 0;
            workSeconds = 8f;
            cookingRecipe = null;
            legacyInput = string.Empty;
            legacyInputAmount = 0;

            if (kind == ProductionStationKind.Windmill && string.Equals(output, ProductionOrderRuntime.BarleyFlour, StringComparison.OrdinalIgnoreCase))
            {
                legacyInput = "Barley";
                legacyInputAmount = 1;
                produced = ProductionOrderRuntime.BarleyFlour;
                producedAmount = 1;
                workSeconds = 8f;
                return true;
            }
            if (kind == ProductionStationKind.Oven && string.Equals(output, ProductionOrderRuntime.Bread, StringComparison.OrdinalIgnoreCase))
            {
                legacyInput = ProductionOrderRuntime.BreadDough;
                legacyInputAmount = 1;
                produced = ProductionOrderRuntime.Bread;
                producedAmount = 1;
                workSeconds = 8f;
                return true;
            }

            // Preparation/Cauldron use the real ObjectDB recipe catalog, so sausages and every
            // other vanilla/modded multi-ingredient recipe use the same ingredient definition as
            // the loaded Fuling Shaman instead of the old single-input bread-only shortcut.
            if ((kind == ProductionStationKind.Preparation || kind == ProductionStationKind.Cauldron) &&
                CookingRecipeRuntime.TryGetRecipe(kind, output, out CookingRecipeSpec discovered) && discovered != null)
            {
                cookingRecipe = discovered;
                produced = discovered.OutputPrefab;
                producedAmount = Mathf.Max(1, discovered.OutputAmount);
                workSeconds = Mathf.Max(1f, discovered.WorkSeconds);
                return discovered.Ingredients.Count > 0;
            }

            // Backwards-compatible fallback for worlds where ObjectDB recipe discovery is not yet
            // ready during the very first scheduler pass.
            if (kind == ProductionStationKind.Preparation && string.Equals(output, ProductionOrderRuntime.BreadDough, StringComparison.OrdinalIgnoreCase))
            {
                legacyInput = ProductionOrderRuntime.BarleyFlour;
                legacyInputAmount = 10;
                produced = ProductionOrderRuntime.BreadDough;
                producedAmount = 2;
                workSeconds = 4f;
                return true;
            }
            return false;
        }

        private static bool TryReserveCookingIngredients(ZDO workerZdo, CookingRecipeSpec recipe, string batchId,
            List<RemovalReservation> reservations)
        {
            if (workerZdo == null || recipe == null || reservations == null) return false;
            for (int i = 0; i < recipe.Ingredients.Count; i++)
            {
                CookingIngredientSpec ing = recipe.Ingredients[i];
                if (ing == null || string.IsNullOrEmpty(ing.Prefab) || ing.Amount <= 0) continue;
                if (!TryReserveIngredient(workerZdo, ing.Prefab, ing.Amount, batchId + ":" + i, reservations))
                    return false;
            }
            return true;
        }

        private static bool TryReserveIngredient(ZDO workerZdo, string prefab, int amount, string txPrefix,
            List<RemovalReservation> reservations)
        {
            if (workerZdo == null || string.IsNullOrEmpty(prefab) || amount <= 0 || reservations == null) return false;
            int remaining = amount;
            int sourceIndex = 0;
            ForEachEligiblePersistentContainer(workerZdo, (candidate, position) =>
            {
                if (remaining <= 0 || candidate == null) return;
                if (!ContainerLedgerRuntime.TryGetAvailable(candidate, prefab, 1, out int available) || available <= 0) return;
                int request = Math.Min(remaining, available);
                string tx = txPrefix + ":" + sourceIndex++;
                if (!ContainerLedgerRuntime.TryReserveRemoval(candidate, tx, prefab, request, 1,
                        "background-baker", out int reserved, out _) || reserved <= 0) return;
                reservations.Add(new RemovalReservation { Source = candidate, TransactionId = tx, Amount = reserved });
                remaining -= reserved;
            });
            return remaining <= 0;
        }

        private static void RollbackReservations(List<RemovalReservation> reservations)
        {
            if (reservations == null) return;
            for (int i = 0; i < reservations.Count; i++)
            {
                RemovalReservation reservation = reservations[i];
                if (reservation?.Source == null || string.IsNullOrEmpty(reservation.TransactionId)) continue;
                ContainerLedgerRuntime.RollbackTransaction(reservation.Source, reservation.TransactionId);
            }
        }

        private static int CountAvailablePersistentInput(ZDO workerZdo, string prefab)
        {
            if (workerZdo == null || string.IsNullOrEmpty(prefab)) return 0;
            int total = 0;
            ForEachEligiblePersistentContainer(workerZdo, (candidate, position) =>
            {
                if (candidate == null) return;
                if (ContainerLedgerRuntime.TryGetAvailable(candidate, prefab, 1, out int available) && available > 0)
                    total += available;
            });
            return total;
        }

        private static void ForEachEligiblePersistentContainer(ZDO workerZdo, Action<ZDO, Vector3> visitor)
        {
            if (workerZdo == null || visitor == null) return;
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Vector3 home = WorkerZdo.ReadHome(workerZdo, workerZdo.GetPosition());
            float homeRadius = WorkerStorageRuntime.GetSearchRadius(workerZdo);
            bool hasArea = WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out _);
            float workRadius = WorkerStorageRuntime.GetWorkAreaStorageSearchRadius(workerZdo);

            PersistentContainerRegistryRuntime.ForEachPersistent((candidate, role, owner, position) =>
            {
                if (candidate == null || role == WorkerContainerRole.Import || role == WorkerContainerRole.Export) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                bool inHome = HorizontalSqr(position, home) <= homeRadius * homeRadius;
                bool inArea = hasArea && HorizontalSqr(position, center) <= workRadius * workRadius;
                if (!inHome && !inArea) return;
                visitor(candidate, position);
            });
        }

        private static int CountPersistentStock(ZDO workerZdo, string prefab)
        {
            if (workerZdo == null || string.IsNullOrEmpty(prefab)) return 0;
            int total = 0;
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Vector3 home = WorkerZdo.ReadHome(workerZdo, workerZdo.GetPosition());
            float homeRadius = WorkerStorageRuntime.GetSearchRadius(workerZdo);
            bool hasArea = WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out _);
            float workRadius = WorkerStorageRuntime.GetWorkAreaStorageSearchRadius(workerZdo);
            PersistentContainerRegistryRuntime.ForEachPersistent((candidate, role, owner, position) =>
            {
                if (candidate == null || role == WorkerContainerRole.Import || role == WorkerContainerRole.Export) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                bool inHome = HorizontalSqr(position, home) <= homeRadius * homeRadius;
                bool inArea = hasArea && HorizontalSqr(position, center) <= workRadius * workRadius;
                if (!inHome && !inArea) return;
                total += Mathf.Max(0, ContainerLedgerRuntime.GetSnapshotAmount(candidate, prefab));
                if (role == WorkerContainerRole.FieldDepot)
                    total += AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(candidate, prefab);
            });
            return total;
        }

        private static bool CanStage(ZDO destination, string prefab, int amount)
        {
            if (destination == null || amount <= 0) return false;
            if (!ContainerLedgerRuntime.TryGetPersistentFreeCapacity(destination, prefab, out int capacity)) return true;
            int pending = AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(destination, prefab);
            return Math.Max(0, capacity - pending) >= amount;
        }

        private static float HorizontalSqr(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x;
            float z = a.z - b.z;
            return x * x + z * z;
        }
    }
}
