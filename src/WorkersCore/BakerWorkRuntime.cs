using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Physical consumer of station-owned ProductionOrders.
    ///
    /// The production order stays on the real station. A Baker first walks to a real storage
    /// container, takes only the direct input for that station, carries it to the station, waits
    /// for the local work step, then carries the produced output to real storage. Missing upstream
    /// products never create automatic flour/dough/bread orders elsewhere.
    /// </summary>
    internal static class BakerWorkRuntime
    {
        internal const string TargetKind = "BakerProductionStation";
        internal const string InputSourceTargetKind = "BakerInputSource";

        private const string StageKey = "vw_baker_stage";
        private const string OrderedOutputKey = "vw_baker_order_output";
        private const string ReadyTicksKey = "vw_baker_ready_ticks";
        private const string DebugKey = "vw_baker_debug";
        private const string StationNameKey = "vw_baker_station_name";
        private const string StationXKey = "vw_baker_station_x";
        private const string StationYKey = "vw_baker_station_y";
        private const string StationZKey = "vw_baker_station_z";

        private enum BakerStage
        {
            None = 0,
            BarleyToFlour = 1,
            FlourToDough = 2,
            DoughToBread = 3
        }

        // v0.5.110: availability probing is used by the unified per-worker priority scheduler.
        // Keep it throttled so a Fuling Shaman with Garden+Bake enabled does not rescan every
        // production station and storage container five times per second while idle.
        private static readonly Dictionary<int, float> NextAvailabilityProbeAt = new Dictionary<int, float>();
        private static readonly Dictionary<int, bool> AvailabilityProbeResult = new Dictionary<int, bool>();
        private const float AvailabilityProbeIntervalSeconds = 2f;

        internal static bool HasAvailableWork(Character worker, ZDO workerZdo, Vector3 center, float radius)
        {
            if (worker == null || workerZdo == null) return false;
            if (GenericCookingWorkRuntime.HasAvailableWork(worker, workerZdo, center, radius)) return true;
            if (!CookingWorkerTierRuntime.CanUseAdvancedProduction(worker, workerZdo)) return false;

            int key = worker.GetInstanceID();
            float now = Time.time;
            if (NextAvailabilityProbeAt.TryGetValue(key, out float next) && now < next &&
                AvailabilityProbeResult.TryGetValue(key, out bool cached))
                return cached;

            bool result = false;
            try
            {
                Dictionary<string, int> stockSnapshot = WorkerStorageRuntime.BuildGardenResourceStockSnapshot(worker, workerZdo);
                Func<string, int> stock = prefab => WorkerStorageRuntime.CountStockSnapshot(stockSnapshot, prefab);
                Func<string, bool> canRun = output =>
                {
                    BakerStage stage = StageForOutput(output);
                    if (!TryGetRecipe(stage, out string input, out int inputAmount, out _, out _)) return false;
                    return TryFindInputSource(worker, workerZdo, input, inputAmount, out _, out _);
                };

                result = ProductionStationRuntime.TryFindDemand(
                    center,
                    radius,
                    worker.transform.position,
                    stock,
                    canRun,
                    out _,
                    out _);
            }
            catch
            {
                result = false;
            }

            NextAvailabilityProbeAt[key] = now + AvailabilityProbeIntervalSeconds;
            AvailabilityProbeResult[key] = result;
            return result;
        }

        internal static bool TryPlanNext(Character worker, ZDO workerZdo, Vector3 center, float radius, out string reason)
        {
            reason = "kein Produktionsauftrag an einer Station";
            if (worker == null || workerZdo == null) return false;
            if (GenericCookingWorkRuntime.TryPlanNext(worker, workerZdo, center, radius, out string cookReason))
            {
                reason = cookReason;
                return true;
            }
            if (!CookingWorkerTierRuntime.CanUseAdvancedProduction(worker, workerZdo))
            {
                reason = CookingWorkerTierRuntime.DescribeTier(worker, workerZdo) + ": kein passender aktiver Kochauftrag";
                workerZdo.Set(DebugKey, reason);
                return false;
            }
            if (WorkerZdo.HasCargo(workerZdo))
            {
                reason = "Koch traegt noch Cargo";
                return false;
            }

            // WorkerAiController already resolved and validated this Baker's persistent Workzone
            // before entering the role runtime. Do not require the loaded WorkFlag registry a
            // second time here: streamed flags can briefly miss that lifecycle index even while
            // the physical Baker, its home and the passed Workzone are all valid. That redundant
            // gate could leave the Fuling permanently idle until a manual Dev Reload rebuilt the
            // flag registry. The station/material search below remains strictly bounded to the
            // supplied Workzone.
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId))
            {
                reason = "keine Produktions-Workzone zugewiesen";
                return false;
            }

            Dictionary<string, int> stockSnapshot = WorkerStorageRuntime.BuildGardenResourceStockSnapshot(worker, workerZdo);
            Func<string, int> stock = prefab => WorkerStorageRuntime.CountStockSnapshot(stockSnapshot, prefab);
            Func<string, bool> canRun = output =>
            {
                BakerStage candidateStage = StageForOutput(output);
                if (!TryGetRecipe(candidateStage, out string input, out int inputAmount, out _, out _)) return false;
                return TryFindInputSource(worker, workerZdo, input, inputAmount, out _, out _);
            };

            if (!ProductionStationRuntime.TryFindDemand(center, radius, worker.transform.position, stock, canRun, out ProductionStationRef station, out ProductionDemand demand))
            {
                // Distinguish "no station/order" from "valid order but direct input missing".
                // This is only a second indexed Workzone query and makes the worker debug useful
                // instead of silently reporting one generic idle state.
                if (ProductionStationRuntime.TryFindDemand(center, radius, worker.transform.position, stock, null,
                    out ProductionStationRef waitingStation, out ProductionDemand waitingDemand))
                {
                    reason = waitingStation.DisplayName + ": " + MissingMaterialReason(waitingDemand.Output, stock);
                }
                else
                {
                    reason = "kein aktiver Produktionsauftrag an einer geladenen Station in der Workzone";
                }
                workerZdo.Set(DebugKey, reason);
                return false;
            }

            BakerStage stage = StageForOutput(demand.Output);
            if (!TryGetRecipe(stage, out string inputPrefab, out int inputAmount, out _, out int outputAmount))
            {
                reason = "unbekannter Stationsauftrag";
                workerZdo.Set(DebugKey, reason);
                return false;
            }

            if (!TryFindInputSource(worker, workerZdo, inputPrefab, inputAmount, out Container source, out Vector3 sourcePosition) || source == null)
            {
                reason = MissingMaterialReason(demand.Output, stock);
                workerZdo.Set(DebugKey, reason);
                return false;
            }

            if (!ProductionOrderClaimRuntime.TryClaim(station.Zdo, workerZdo, demand.Output, Math.Max(1, outputAmount), out _))
            {
                reason = station.DisplayName + ": Produktionsauftrag konnte nicht reserviert werden";
                workerZdo.Set(DebugKey, reason);
                return false;
            }

            WritePlan(workerZdo, stage, demand.Output, station);
            WorkerZdo.WriteTarget(workerZdo, inputPrefab, sourcePosition, InputSourceTargetKind);
            workerZdo.Set(DebugKey,
                $"{station.DisplayName}: hole {DisplayInput(inputPrefab)} x{inputAmount} aus {WorkerContainerRoleRuntime.DisplayName(WorkerContainerRoleRuntime.GetRole(source))}");
            reason = workerZdo.GetString(DebugKey, string.Empty);
            return true;
        }

        /// <summary>
        /// Recovery path for a Baker that already has persisted cargo, e.g. after a save/reload or
        /// after a delivery target disappeared. Inputs resume the saved station leg; all other
        /// cargo is routed back to storage instead of being discarded.
        /// </summary>
        internal static bool TryResumeCargo(Character worker, ZDO zdo, out string reason)
        {
            reason = string.Empty;
            if (GenericCookingWorkRuntime.HasPlan(zdo) && GenericCookingWorkRuntime.TryResumeCargo(worker, zdo, out reason)) return true;
            if (worker == null || zdo == null || !WorkerZdo.TryReadCargo(zdo, out string cargoPrefab, out int cargoAmount) || cargoAmount <= 0)
                return false;

            BakerStage stage = (BakerStage)zdo.GetInt(StageKey, 0);
            if (TryGetRecipe(stage, out string inputPrefab, out int inputAmount, out _, out int outputAmount) &&
                WorkerZdo.GetCargoAmount(zdo, inputPrefab) >= inputAmount &&
                TryReadStation(zdo, out string stationName, out Vector3 stationPosition))
            {
                string orderedOutput = zdo.GetString(OrderedOutputKey, string.Empty);
                if (!string.IsNullOrEmpty(orderedOutput) &&
                    ProductionStationRuntime.TryFindNear(orderedOutput, stationPosition, 5f, out ProductionStationRef claimStation) && claimStation?.Zdo != null)
                    ProductionOrderClaimRuntime.EnsureClaim(claimStation.Zdo, zdo, orderedOutput, Math.Max(1, outputAmount));

                WorkerZdo.WriteTarget(zdo, stationName, stationPosition, TargetKind);
                zdo.Set(WorkerZdo.State, (int)WorkerState.MoveToTarget);
                zdo.Set(DebugKey, $"{stationName}: Produktionsinput {DisplayInput(inputPrefab)} x{inputAmount} wird getragen");
                reason = zdo.GetString(DebugKey, string.Empty);
                return true;
            }

            ClearPlanFields(zdo);
            TryAssignCargoDelivery(worker, zdo, cargoPrefab, cargoAmount, out reason);
            zdo.Set(DebugKey, reason);
            return true;
        }

        internal static bool TryCollectInput(Character worker, ZDO zdo)
        {
            if (GenericCookingWorkRuntime.HasPlan(zdo)) return GenericCookingWorkRuntime.TryCollectInput(worker, zdo);
            if (worker == null || zdo == null) return false;
            if (!WorkerZdo.TryReadTarget(zdo, out string targetInput, out string kind, out Vector3 targetPosition) ||
                !string.Equals(kind, InputSourceTargetKind, StringComparison.Ordinal)) return false;

            BakerStage stage = (BakerStage)zdo.GetInt(StageKey, 0);
            string orderedOutput = zdo.GetString(OrderedOutputKey, string.Empty);
            if (!TryGetRecipe(stage, out string inputPrefab, out int inputAmount, out _, out int outputAmount) ||
                !string.Equals(targetInput, inputPrefab, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(orderedOutput))
            {
                CancelPlan(zdo, "ungueltiger Produktionsinput");
                return true;
            }

            if (!TryReadStation(zdo, out string stationName, out Vector3 stationPosition))
            {
                CancelPlan(zdo, "Produktionsstation nicht mehr gespeichert");
                return true;
            }

            if (ProductionStationRuntime.TryFindNear(orderedOutput, stationPosition, 5f, out ProductionStationRef claimStation) && claimStation?.Zdo != null)
                ProductionOrderClaimRuntime.EnsureClaim(claimStation.Zdo, zdo, orderedOutput, Math.Max(1, outputAmount));
            else
                ProductionOrderClaimRuntime.TouchWorkerClaims(zdo);

            bool localFallback = LocalWorkFallbackRuntime.IsActive(zdo, targetInput, kind);
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Container source = null;
            float best = float.MaxValue;
            WorkerStorageRuntime.ForEachContainerNear(targetPosition, Mathf.Max(4f, WorkerStorageRuntime.ContainerAccessRadius + 1f), candidate =>
            {
                if (candidate == null ||
                    !WorkerStorageRuntime.CanUseContainer(candidate, ownerId) ||
                    !WorkerContainerRoleRuntime.MatchesOwner(candidate, ownerId) ||
                    (!localFallback && !WorkerStorageRuntime.IsInsideContainerAura(worker.transform.position, candidate.transform.position))) return;

                WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(candidate);
                if (role == WorkerContainerRole.Import || role == WorkerContainerRole.Export) return;
                Inventory inv = candidate.GetInventory();
                int reserve = WorkerContainerRoleRuntime.ProtectLastItem(candidate) ? 1 : 0;
                if (inv == null || WorkerStorageRuntime.CountPrefab(inv, inputPrefab) < inputAmount + reserve) return;
                float sqr = (candidate.transform.position - targetPosition).sqrMagnitude;
                if (sqr >= best) return;
                best = sqr;
                source = candidate;
            });

            if (source == null)
            {
                CancelPlan(zdo, $"Quelle fuer {DisplayInput(inputPrefab)} ist leer/weg; Auftrag wird neu gesucht");
                return true;
            }

            if (!WorkerStorageRuntime.TryOwn(source))
            {
                zdo.Set(DebugKey, $"warte auf Besitz der Input-Kiste ({DisplayInput(inputPrefab)})");
                return true;
            }

            Inventory inventory = source.GetInventory();
            int protectedCount = WorkerContainerRoleRuntime.ProtectLastItem(source) ? 1 : 0;
            if (inventory == null || WorkerStorageRuntime.CountPrefab(inventory, inputPrefab) < inputAmount + protectedCount)
            {
                CancelPlan(zdo, $"zu wenig {DisplayInput(inputPrefab)} an der gewaehlten Quelle");
                return true;
            }

            int removed = 0;
            while (removed < inputAmount)
            {
                ItemDrop.ItemData item = WorkerStorageRuntime.FindPrefab(inventory, inputPrefab);
                if (item == null || !WorkerStorageRuntime.TryRemoveOne(inventory, item)) break;
                removed++;
            }

            if (removed != inputAmount)
            {
                if (removed > 0)
                {
                    WorkerStorageRuntime.PersistContainer(source, inventory);
                    WorkerZdo.AddCargoAmount(zdo, inputPrefab, removed);
                    ClearPlanFields(zdo);
                    WorkerZdo.ClearTarget(zdo);
                    zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                    zdo.Set(DebugKey, $"Entnahme von {DisplayInput(inputPrefab)} unterbrochen ({removed}/{inputAmount}); Teilmenge bleibt als Cargo erhalten");
                }
                else
                {
                    CancelPlan(zdo, $"Entnahme von {DisplayInput(inputPrefab)} unterbrochen (0/{inputAmount})");
                }
                return true;
            }

            WorkerStorageRuntime.PersistContainer(source, inventory);
            WorkerZdo.SetCargo(zdo, inputPrefab, inputAmount);
            WorkerZdo.WriteTarget(zdo, stationName, stationPosition, TargetKind);
            zdo.Set(WorkerZdo.State, (int)WorkerState.MoveToTarget);
            zdo.Set(DebugKey, $"{stationName}: traegt {DisplayInput(inputPrefab)} x{inputAmount} zur Station");
            return true;
        }

        internal static bool TryWork(Character worker, ZDO workerZdo)
        {
            if (GenericCookingWorkRuntime.HasPlan(workerZdo)) return GenericCookingWorkRuntime.TryWork(worker, workerZdo);
            if (worker == null || workerZdo == null) return false;
            if (!WorkerZdo.TryReadTarget(workerZdo, out _, out string kind, out Vector3 position) ||
                !string.Equals(kind, TargetKind, StringComparison.Ordinal)) return false;

            bool localFallback = LocalWorkFallbackRuntime.IsActive(workerZdo,
                workerZdo.GetString(StationNameKey, string.Empty), kind);
            if (!WorkerInteractionRuntime.IsInsideAura(worker.transform.position, position) && !localFallback) return false;
            BakerStage stage = (BakerStage)workerZdo.GetInt(StageKey, 0);
            string orderedOutput = workerZdo.GetString(OrderedOutputKey, string.Empty);
            if (!TryGetRecipe(stage, out string inputPrefab, out int inputAmount, out string outputPrefab, out int outputAmount) ||
                string.IsNullOrEmpty(orderedOutput) ||
                !string.Equals(outputPrefab, orderedOutput, StringComparison.OrdinalIgnoreCase))
            {
                CancelPlan(workerZdo, "ungueltiger Stationsauftrag");
                return true;
            }

            if (!ProductionStationRuntime.TryFindNear(orderedOutput, position, 5f, out ProductionStationRef station) || station.Zdo == null)
            {
                ClearPlanFields(workerZdo);
                TryAssignCargoDelivery(worker, workerZdo, inputPrefab, WorkerZdo.GetCargoAmount(workerZdo, inputPrefab), out _);
                workerZdo.Set(DebugKey, "Produktionsstation nicht mehr verfuegbar; Input wird zurueckgebracht");
                return true;
            }

            ProductionOrderClaimRuntime.EnsureClaim(station.Zdo, workerZdo, orderedOutput, Math.Max(1, outputAmount));

            // The order may have been removed while the worker was fetching/carrying the input.
            if (!ProductionOrderRuntime.HasOrderFor(station.Zdo, orderedOutput))
            {
                ClearPlanFields(workerZdo);
                TryAssignCargoDelivery(worker, workerZdo, inputPrefab, WorkerZdo.GetCargoAmount(workerZdo, inputPrefab), out _);
                workerZdo.Set(DebugKey, "Stationsauftrag wurde entfernt; Input wird zurueckgebracht");
                return true;
            }

            if (WorkerZdo.GetCargoAmount(workerZdo, inputPrefab) < inputAmount)
            {
                CancelPlan(workerZdo, $"Produktionsinput fehlt im Cargo: {DisplayInput(inputPrefab)} {WorkerZdo.GetCargoAmount(workerZdo, inputPrefab)}/{inputAmount}");
                return true;
            }

            long now = DateTime.UtcNow.Ticks;
            long ready = workerZdo.GetLong(ReadyTicksKey, 0L);
            if (ready <= 0L)
            {
                double seconds = stage == BakerStage.FlourToDough ? 4.0 : 8.0;
                workerZdo.Set(ReadyTicksKey, now + TimeSpan.FromSeconds(seconds).Ticks);
                workerZdo.Set(DebugKey, station.DisplayName + ": " + StageDescription(stage) + " laeuft...");
                return true;
            }
            if (now < ready) return true;

            // Convert only the physically carried direct input. No storage is touched here.
            WorkerZdo.RemoveCargoAmount(workerZdo, inputPrefab, inputAmount);
            ProductionMetricsRuntime.RecordConsumed(workerZdo, inputPrefab, inputAmount);
            WorkerZdo.AddCargoAmount(workerZdo, outputPrefab, outputAmount);
            ProductionMetricsRuntime.RecordProduced(workerZdo, outputPrefab, outputAmount);
            ProductionOrderRuntime.RegisterProduced(station.Zdo, orderedOutput, outputAmount);
            if (localFallback) LocalWorkFallbackRuntime.Clear(workerZdo);

            ClearPlanFields(workerZdo);
            if (!TryAssignCargoDelivery(worker, workerZdo, outputPrefab, outputAmount, out string deliveryReason))
            {
                workerZdo.Set(DebugKey,
                    $"{station.DisplayName}: {ProductionOrderRuntime.DisplayProduct(outputPrefab)} x{outputAmount} fertig; keine Zielkiste frei");
                return true;
            }

            workerZdo.Set(DebugKey,
                $"{station.DisplayName}: {ProductionOrderRuntime.DisplayProduct(outputPrefab)} x{outputAmount} fertig -> {deliveryReason}");
            return true;
        }

        internal static string GetDebug(ZDO zdo) => zdo?.GetString(DebugKey, "baker:idle") ?? "baker:none";

        private static void WritePlan(ZDO zdo, BakerStage stage, string orderedOutput, ProductionStationRef station)
        {
            if (zdo == null || station == null) return;
            zdo.Set(StageKey, (int)stage);
            zdo.Set(OrderedOutputKey, orderedOutput ?? string.Empty);
            zdo.Set(ReadyTicksKey, 0L);
            zdo.Set(StationNameKey, station.DisplayName ?? "Produktionsstation");
            zdo.Set(StationXKey, station.Position.x);
            zdo.Set(StationYKey, station.Position.y);
            zdo.Set(StationZKey, station.Position.z);
        }

        private static bool TryReadStation(ZDO zdo, out string stationName, out Vector3 stationPosition)
        {
            stationName = string.Empty;
            stationPosition = default;
            if (zdo == null) return false;
            stationName = zdo.GetString(StationNameKey, string.Empty);
            float x = zdo.GetFloat(StationXKey, 0f);
            float y = zdo.GetFloat(StationYKey, 0f);
            float z = zdo.GetFloat(StationZKey, 0f);
            stationPosition = new Vector3(x, y, z);
            return !string.IsNullOrEmpty(stationName);
        }

        private static void ClearPlanFields(ZDO zdo)
        {
            if (zdo == null) return;
            ProductionOrderClaimRuntime.ReleaseClaimsForWorker(zdo);
            zdo.Set(StageKey, 0);
            zdo.Set(OrderedOutputKey, string.Empty);
            zdo.Set(ReadyTicksKey, 0L);
            zdo.Set(StationNameKey, string.Empty);
            zdo.Set(StationXKey, 0f);
            zdo.Set(StationYKey, 0f);
            zdo.Set(StationZKey, 0f);
        }

        private static void CancelPlan(ZDO zdo, string debug)
        {
            if (zdo == null) return;
            zdo.Set(DebugKey, debug ?? string.Empty);
            ClearPlanFields(zdo);
            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
        }

        internal static bool TryAssignCargoDelivery(Character worker, ZDO zdo, string cargoPrefab, int cargoAmount, out string reason)
        {
            reason = string.Empty;
            if (worker == null || zdo == null || string.IsNullOrEmpty(cargoPrefab) || cargoAmount <= 0)
            {
                WorkerZdo.ClearTarget(zdo);
                zdo?.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return false;
            }

            if (WorkerStorageRuntime.TryFindMatchingDepositContainer(worker, zdo, cargoPrefab, out _, out Vector3 matchingPosition))
            {
                WorkerZdo.WriteTarget(zdo, cargoPrefab, matchingPosition, WorkerStorageRuntime.DepositTargetKind);
                zdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
                reason = "passende Kiste";
                return true;
            }

            if (WorkerStorageRuntime.TryFindFieldDepotForCargo(worker, zdo, cargoPrefab, out _, out Vector3 depotPosition))
            {
                WorkerZdo.WriteTarget(zdo, cargoPrefab, depotPosition, WorkerStorageRuntime.FieldDepotDepositTargetKind);
                zdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
                reason = "FieldDepot";
                return true;
            }

            if (WorkerStorageRuntime.TryFindAnyNormalDepositContainer(worker, zdo, cargoPrefab, out _, out Vector3 overflowPosition))
            {
                WorkerZdo.WriteTarget(zdo, cargoPrefab, overflowPosition, WorkerStorageRuntime.OverflowDepositTargetKind);
                zdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
                reason = "freie normale Kiste";
                return true;
            }

            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.State, (int)WorkerState.NeedsResource);
            reason = $"keine freie Zielkiste fuer {cargoPrefab} x{cargoAmount}";
            return false;
        }

        private static bool TryFindInputSource(Character worker, ZDO zdo, string prefabName, int amount, out Container container, out Vector3 position)
        {
            container = null;
            position = default;
            if (worker == null || zdo == null || string.IsNullOrEmpty(prefabName) || amount <= 0) return false;

            Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
            float homeRadius = WorkerStorageRuntime.GetSearchRadius(zdo);
            bool hasWorkArea = WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 workCenter, out _);
            float workRadius = WorkerStorageRuntime.GetWorkAreaStorageSearchRadius(zdo);
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);

            Vector3 searchCenter = hasWorkArea ? workCenter : home;
            float searchRadius = hasWorkArea ? workRadius : homeRadius;
            bool found = SharedStorageStockIndexRuntime.TryFindItemSource(
                prefabName, searchCenter, searchRadius, worker.transform.position, amount,
                candidate =>
                {
                    if (candidate == null || !WorkerStorageRuntime.CanUseContainer(candidate, ownerId) ||
                        !WorkerContainerRoleRuntime.MatchesOwner(candidate, ownerId)) return false;
                    WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(candidate);
                    return role != WorkerContainerRole.Import && role != WorkerContainerRole.Export;
                },
                candidate => WorkerContainerRoleRuntime.ProtectLastItem(candidate) ? 1 : 0,
                out container, out _, out position);

            if (found && container != null && WorkerContainerRoleRuntime.GetRole(container) == WorkerContainerRole.Normal)
                ContainerLedgerRuntime.MarkSorterStorage(container, ownerId);
            return found && container != null;
        }

        private static BakerStage StageForOutput(string output)
        {
            if (string.Equals(output, ProductionOrderRuntime.BarleyFlour, StringComparison.OrdinalIgnoreCase)) return BakerStage.BarleyToFlour;
            if (string.Equals(output, ProductionOrderRuntime.BreadDough, StringComparison.OrdinalIgnoreCase)) return BakerStage.FlourToDough;
            if (string.Equals(output, ProductionOrderRuntime.Bread, StringComparison.OrdinalIgnoreCase)) return BakerStage.DoughToBread;
            return BakerStage.None;
        }

        private static bool TryGetRecipe(BakerStage stage, out string input, out int inputAmount, out string output, out int outputAmount)
        {
            input = string.Empty;
            output = string.Empty;
            inputAmount = 0;
            outputAmount = 0;
            switch (stage)
            {
                case BakerStage.BarleyToFlour:
                    input = "Barley"; inputAmount = 1; output = ProductionOrderRuntime.BarleyFlour; outputAmount = 1; return true;
                case BakerStage.FlourToDough:
                    input = ProductionOrderRuntime.BarleyFlour; inputAmount = 10; output = ProductionOrderRuntime.BreadDough; outputAmount = 2; return true;
                case BakerStage.DoughToBread:
                    input = ProductionOrderRuntime.BreadDough; inputAmount = 1; output = ProductionOrderRuntime.Bread; outputAmount = 1; return true;
                default:
                    return false;
            }
        }

        private static string MissingMaterialReason(string output, Func<string, int> stock)
        {
            if (string.Equals(output, ProductionOrderRuntime.BarleyFlour, StringComparison.OrdinalIgnoreCase))
                return "Windmuehle wartet auf Gerste";
            if (string.Equals(output, ProductionOrderRuntime.BreadDough, StringComparison.OrdinalIgnoreCase))
                return $"Zubereitungsstation wartet auf Gerstenmehl ({stock(ProductionOrderRuntime.BarleyFlour)}/10)";
            if (string.Equals(output, ProductionOrderRuntime.Bread, StringComparison.OrdinalIgnoreCase))
                return $"Steinofen wartet auf Brotteig ({stock(ProductionOrderRuntime.BreadDough)})";
            return "unbekanntes Produktionsrezept";
        }

        private static string DisplayInput(string prefab)
        {
            if (string.Equals(prefab, "Barley", StringComparison.OrdinalIgnoreCase)) return "Gerste";
            return ProductionOrderRuntime.DisplayProduct(prefab);
        }

        private static string StageDescription(BakerStage stage)
        {
            switch (stage)
            {
                case BakerStage.BarleyToFlour: return "Gerste -> Gerstenmehl";
                case BakerStage.FlourToDough: return "Gerstenmehl -> Brotteig";
                case BakerStage.DoughToBread: return "Brotteig -> Brot";
                default: return "keine Produktion";
            }
        }
    }
}
