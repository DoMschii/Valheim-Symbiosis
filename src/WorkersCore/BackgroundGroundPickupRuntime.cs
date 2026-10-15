using System;
using System.Globalization;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Safe first background-pickup layer for ground haulers. While the worker and its source are
    /// loaded we snapshot the next real logistics pickup. Once the worker unloads, the scheduler may
    /// take cargo from either the still-authoritative loaded source container or the v0.2.82
    /// persistent container ledger when that source is unloaded. Ledger pickup uses resumable
    /// reservation/commit transactions so retries cannot debit the same source twice.
    /// </summary>
    internal static class BackgroundGroundPickupRuntime
    {
        private const string PlanKey = "vw_bg_ground_pickup_plan";
        private const string DebugKey = "vw_bg_ground_pickup_debug";
        private const string SourceIdKey = "vw_bg_ground_pickup_source_id";
        private const string DeliveryIdKey = "vw_bg_ground_pickup_delivery_id";
        private const string DeliveryWorkAreaIdKey = "vw_bg_ground_pickup_delivery_work_area_id";
        private const string TransactionKey = "vw_bg_ground_pickup_tx";
        private const float SnapshotInterval = 4f;
        private static readonly System.Collections.Generic.Dictionary<ZDOID, float> NextSnapshot =
            new System.Collections.Generic.Dictionary<ZDOID, float>();

        private static readonly List<string> PersistentInboundPrefabScratch = new List<string>(32);
        private static readonly List<string> RequestedPrefabScratch = new List<string>(24);
        private static readonly List<string> ConfiguredPrefabScratch = new List<string>(24);
        private static readonly List<string> ProvisionPrefabScratch = new List<string>(24);
        private static readonly HashSet<string> DesiredPrefabScratch =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private sealed class PickupPlan
        {
            internal string Prefab = string.Empty;
            internal ZDOID SourceId = ZDOID.None;
            internal WorkerContainerRole SourceRole;
            internal Vector3 SourcePosition;
            internal int Amount;
            internal int MinimumRemain;
            internal string Route = string.Empty;
            internal Vector3 RouteTarget;
            internal string DeliveryTargetKind = string.Empty;
            internal Vector3 DeliveryPosition;
            internal ZDOID DeliveryId = ZDOID.None;
            internal string DeliveryWorkAreaId = string.Empty;
        }

        internal static void TickLoaded(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null || !WorkerZdo.IsWorker(zdo)) return;
            if (!WorkerZdo.TryReadWorkArea(zdo, out WorkOrder order, out _, out _) || order != WorkOrder.Haul) return;
            if (string.Equals(SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name), "Drake", StringComparison.OrdinalIgnoreCase)) return;
            if (WorkerZdo.HasCargo(zdo)) { Save(zdo, null); return; }

            if (NextSnapshot.TryGetValue(zdo.m_uid, out float next) && Time.realtimeSinceStartup < next) return;
            NextSnapshot[zdo.m_uid] = Time.realtimeSinceStartup + SnapshotInterval;

            PickupPlan plan = BuildPlan(worker, zdo);
            Save(zdo, plan);
            zdo.Set(DebugKey, plan == null ? "ground-pickup:loaded/no-plan" : BuildSummary("loaded", plan));
        }

        internal static bool TryStartBackgroundPickup(ZDO zdo, out string status)
        {
            status = "ground:no-cargo; no pickup snapshot";
            if (zdo == null || WorkerZdo.HasCargo(zdo)) return false;
            PickupPlan plan = Load(zdo);
            if (plan == null || string.IsNullOrEmpty(plan.Prefab) || plan.Amount <= 0)
            {
                // A restart can bootstrap a Hauler while its outpost/Base is completely unloaded.
                // Older builds needed one loaded TickLoaded() first to write a pickup snapshot, so
                // ground transport appeared dead until the player visited the worker. Build the
                // same conservative plan directly from persistent endpoint ledgers instead.
                plan = BuildPersistentPlan(zdo);
                if (plan == null)
                {
                    status = "ground:no-cargo; no loaded/persistent logistics plan";
                    zdo.Set(DebugKey, status);
                    return false;
                }
                Save(zdo, plan);
                zdo.Set(DebugKey, BuildSummary("persistent", plan));
            }

            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Container source = null;
            float bestSqr = float.MaxValue;
            WorkerStorageRuntime.ForEachContainerNear(plan.SourcePosition, 8f, c =>
            {
                if (c == null || !WorkerStorageRuntime.CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(c) != plan.SourceRole) return;
                Inventory inv = c.GetInventory();
                if (inv == null) return;
                int stock = WorkerStorageRuntime.CountPrefab(inv, plan.Prefab);
                if (stock <= plan.MinimumRemain) return;
                float sqr = (c.transform.position - plan.SourcePosition).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                source = c;
            });

            if (source == null)
            {
                if (plan.SourceId.Equals(ZDOID.None) || ZDOMan.instance == null)
                {
                    status = $"ground:pickup-queued {plan.Prefab} x{plan.Amount}; source-unloaded/no-id";
                    zdo.Set(DebugKey, status);
                    return false;
                }

                ZDO sourceZdo = ZDOMan.instance.GetZDO(plan.SourceId);
                if (sourceZdo == null || !ContainerLedgerRuntime.HasSnapshot(sourceZdo))
                {
                    status = $"ground:pickup-queued {plan.Prefab} x{plan.Amount}; source-unloaded/no-ledger";
                    zdo.Set(DebugKey, status);
                    return false;
                }

                string txId = zdo.GetString(TransactionKey, string.Empty);
                if (string.IsNullOrEmpty(txId))
                {
                    txId = ContainerLedgerRuntime.CreateTransactionId();
                    zdo.Set(TransactionKey, txId);
                }

                // Crash/reload recovery: a committed source transaction is completed into the same
                // worker cargo instead of debiting the source a second time.
                if (ContainerLedgerRuntime.TryGetTransaction(sourceZdo, txId, out ContainerLedgerRuntime.TransactionInfo existing))
                {
                    if (existing.State == ContainerLedgerRuntime.TransactionState.SourceCommitted ||
                        existing.State == ContainerLedgerRuntime.TransactionState.Completed)
                    {
                        if (!WorkerZdo.HasCargo(zdo))
                        {
                            WorkerZdo.SetCargo(zdo, existing.Prefab, existing.Amount);
                            WorkzoneLogisticsFlowRuntime.TagCargoSource(zdo, sourceZdo, plan.SourcePosition,
                                string.Equals(plan.Route, WorkerLogisticsRuntime.RouteHaulInbound, StringComparison.Ordinal)
                                    ? WorkerZdo.ReadWorkAreaId(zdo) : "base");
                            WorkerZdo.SetCargoRoute(zdo, plan.Route, plan.RouteTarget, plan.DeliveryId, plan.DeliveryWorkAreaId);
                            WorkerZdo.WriteTarget(zdo, existing.Prefab, plan.DeliveryPosition, plan.DeliveryTargetKind);
                            zdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
                        }
                        ContainerLedgerRuntime.CompleteTransaction(sourceZdo, txId);
                        zdo.Set(TransactionKey, string.Empty);
                        Save(zdo, null);
                        status = $"ground:background-pickup recovered {existing.Prefab} x{existing.Amount}; ledger committed";
                        zdo.Set(DebugKey, status);
                        return true;
                    }
                    if (existing.State == ContainerLedgerRuntime.TransactionState.RolledBack)
                    {
                        zdo.Set(TransactionKey, string.Empty);
                        txId = ContainerLedgerRuntime.CreateTransactionId();
                        zdo.Set(TransactionKey, txId);
                    }
                }

                string ownerTag = zdo.m_uid.ToString();
                if (!ContainerLedgerRuntime.TryReserveRemoval(sourceZdo, txId, plan.Prefab, plan.Amount, plan.MinimumRemain, ownerTag, out int reserved, out string ledgerStatus) || reserved <= 0)
                {
                    status = $"ground:pickup-queued {plan.Prefab} x{plan.Amount}; {ledgerStatus}";
                    zdo.Set(DebugKey, status);
                    return false;
                }
                if (!ContainerLedgerRuntime.TryCommitRemoval(sourceZdo, txId, out ContainerLedgerRuntime.TransactionInfo committed, out ledgerStatus) || committed == null || committed.Amount <= 0)
                {
                    ContainerLedgerRuntime.RollbackTransaction(sourceZdo, txId);
                    zdo.Set(TransactionKey, string.Empty);
                    status = $"ground:pickup-cancelled {plan.Prefab}; {ledgerStatus}";
                    zdo.Set(DebugKey, status);
                    return false;
                }

                WorkerZdo.SetCargo(zdo, committed.Prefab, committed.Amount);
                WorkzoneLogisticsFlowRuntime.TagCargoSource(zdo, sourceZdo, plan.SourcePosition,
                    string.Equals(plan.Route, WorkerLogisticsRuntime.RouteHaulInbound, StringComparison.Ordinal)
                        ? WorkerZdo.ReadWorkAreaId(zdo) : "base");
                WorkerZdo.SetCargoRoute(zdo, plan.Route, plan.RouteTarget, plan.DeliveryId, plan.DeliveryWorkAreaId);
                WorkerZdo.WriteTarget(zdo, committed.Prefab, plan.DeliveryPosition, plan.DeliveryTargetKind);
                zdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
                ContainerLedgerRuntime.CompleteTransaction(sourceZdo, txId);
                zdo.Set(TransactionKey, string.Empty);
                Save(zdo, null);
                status = $"ground:background-pickup {committed.Prefab} x{committed.Amount}; unloaded ledger committed";
                zdo.Set(DebugKey, status);
                return true;
            }
            if (!WorkerStorageRuntime.TryOwn(source))
            {
                status = $"ground:pickup-queued {plan.Prefab} x{plan.Amount}; source-ownership";
                zdo.Set(DebugKey, status);
                return false;
            }

            ContainerLedgerRuntime.TryReconcileLoaded(source, out _);
            ContainerLedgerRuntime.SyncFromPhysical(source, "ground-loaded-before-pickup");
            Inventory inventory = source.GetInventory();
            int stockNow = WorkerStorageRuntime.CountPrefab(inventory, plan.Prefab);
            int available = Mathf.Max(0, stockNow - Mathf.Max(0, plan.MinimumRemain));
            int take = Mathf.Min(plan.Amount, available);
            if (take <= 0)
            {
                status = $"ground:pickup-cancelled {plan.Prefab}; protected/empty";
                Save(zdo, null);
                zdo.Set(DebugKey, status);
                return false;
            }

            int removed = 0;
            while (removed < take)
            {
                ItemDrop.ItemData item = WorkerStorageRuntime.FindPrefab(inventory, plan.Prefab);
                if (item == null || !WorkerStorageRuntime.TryRemoveOne(inventory, item)) break;
                removed++;
            }
            if (removed <= 0)
            {
                status = $"ground:pickup-queued {plan.Prefab}; remove-failed";
                zdo.Set(DebugKey, status);
                return false;
            }

            WorkerStorageRuntime.PersistContainer(source, inventory);
            ContainerLedgerRuntime.SyncFromPhysical(source, "ground-loaded-after-pickup");
            WorkerZdo.SetCargo(zdo, plan.Prefab, removed);
            WorkzoneLogisticsFlowRuntime.TagCargoSource(zdo, WorkerContainerRoleRuntime.GetZdo(source), plan.SourcePosition,
                string.Equals(plan.Route, WorkerLogisticsRuntime.RouteHaulInbound, StringComparison.Ordinal)
                    ? WorkerZdo.ReadWorkAreaId(zdo) : "base");
            WorkerZdo.SetCargoRoute(zdo, plan.Route, plan.RouteTarget, plan.DeliveryId, plan.DeliveryWorkAreaId);
            WorkerZdo.WriteTarget(zdo, plan.Prefab, plan.DeliveryPosition, plan.DeliveryTargetKind);
            zdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
            Save(zdo, null);
            status = $"ground:background-pickup {plan.Prefab} x{removed}; source committed";
            zdo.Set(DebugKey, status);
            return true;
        }

        internal static string GetDebugSummary(ZDO zdo)
        {
            return zdo == null ? "ground-pickup:none" : zdo.GetString(DebugKey, "ground-pickup:not-started");
        }

        private static PickupPlan BuildPlan(Character worker, ZDO zdo)
        {
            bool baseTransporter = WorkerLogisticsRuntime.IsBaseTransporter(zdo);
            bool atOutpost = !baseTransporter && WorkerLogisticsRuntime.IsInsideAssignedOutpost(zdo, worker.transform.position, 5f);

            if (atOutpost)
            {
                if (WorkerStorageRuntime.TryFindLocalReturnHaulSource(worker, zdo, 22f, out Container local, out string prefab, out int amount, out Vector3 sourcePos) &&
                    TryFindImportDestination(worker, zdo, prefab, out Vector3 importPos))
                {
                    return CreateInbound(local, prefab, amount, sourcePos, importPos);
                }

                if (WorkerLogisticsRuntime.TryFindExportHaulSource(worker, zdo, out Container export, out string outPrefab, out int outAmount, out Vector3 exportPos, out Vector3 depotPos, out ZDOID depotId, out string depotWorkAreaId))
                {
                    ContainerLedgerRuntime.SyncFromPhysical(export, "ground-plan-export");
                    return new PickupPlan
                    {
                        Prefab = outPrefab,
                        SourceId = WorkerContainerRoleRuntime.GetZdo(export)?.m_uid ?? ZDOID.None,
                        SourceRole = WorkerContainerRole.Export,
                        SourcePosition = exportPos,
                        Amount = outAmount,
                        MinimumRemain = 0,
                        Route = WorkerLogisticsRuntime.RouteHaulOutbound,
                        RouteTarget = depotPos,
                        DeliveryTargetKind = WorkerStorageRuntime.SupplyFieldDepotDepositTargetKind,
                        DeliveryPosition = depotPos,
                        DeliveryId = string.IsNullOrEmpty(depotWorkAreaId) ? depotId : ZDOID.None,
                        DeliveryWorkAreaId = depotWorkAreaId
                    };
                }

                if (WorkerStorageRuntime.TryFindHaulSource(worker, zdo, out Container field, out string fieldPrefab, out int fieldAmount, out Vector3 fieldPos) &&
                    TryFindImportDestination(worker, zdo, fieldPrefab, out Vector3 importPos2))
                {
                    return CreateInbound(field, fieldPrefab, fieldAmount, fieldPos, importPos2);
                }
                return null;
            }

            if (WorkerLogisticsRuntime.TryFindExportHaulSource(worker, zdo, out Container baseExport, out string basePrefab, out int baseAmount, out Vector3 baseExportPos, out Vector3 baseDepotPos, out ZDOID baseDepotId, out string baseDepotWorkAreaId))
            {
                ContainerLedgerRuntime.SyncFromPhysical(baseExport, "ground-plan-export");
                return new PickupPlan
                {
                    Prefab = basePrefab,
                    SourceId = WorkerContainerRoleRuntime.GetZdo(baseExport)?.m_uid ?? ZDOID.None,
                    SourceRole = WorkerContainerRole.Export,
                    SourcePosition = baseExportPos,
                    Amount = baseAmount,
                    MinimumRemain = 0,
                    Route = WorkerLogisticsRuntime.RouteHaulOutbound,
                    RouteTarget = baseDepotPos,
                    DeliveryTargetKind = WorkerStorageRuntime.SupplyFieldDepotDepositTargetKind,
                    DeliveryPosition = baseDepotPos,
                    DeliveryId = string.IsNullOrEmpty(baseDepotWorkAreaId) ? baseDepotId : ZDOID.None,
                    DeliveryWorkAreaId = baseDepotWorkAreaId
                };
            }
            return null;
        }

        internal static bool HasPersistentPickupWork(ZDO workerZdo)
        {
            // Modular Greydwarfs may currently be persisted as Sort while the whole zone is
            // unloaded. Probe the same ledger-backed Haul planner without requiring a prior
            // physical visit so the background scheduler can wake Transport autonomously.
            return BuildPersistentPlan(workerZdo, allowSortOrder: true) != null;
        }

        private static PickupPlan BuildPersistentPlan(ZDO workerZdo, bool allowSortOrder = false)
        {
            if (workerZdo == null || !WorkerZdo.TryReadWorkArea(workerZdo, out WorkOrder order, out Vector3 center, out float radius))
                return null;
            if (order != WorkOrder.Haul && !(allowSortOrder && order == WorkOrder.Sort))
                return null;

            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Vector3 workerPosition = workerZdo.GetPosition();
            bool baseTransporter = WorkerLogisticsRuntime.IsBaseTransporter(workerZdo);
            bool atOutpost = !baseTransporter && WorkerLogisticsRuntime.IsInsideAssignedOutpost(workerZdo, workerPosition, 5f);

            // Preserve the physical Hauler priority: when standing at the outpost, return local
            // surplus to Base Import first. This is derived entirely from the persistent ledger.
            if (atOutpost && TryBuildPersistentInbound(workerZdo, ownerId, center, radius, out PickupPlan inbound))
                return inbound;

            // Missing local stock may be fetched from the global Base Export whether the worker is
            // currently at the outpost or already waiting at the Base after its previous trip.
            if (TryBuildPersistentOutbound(workerZdo, ownerId, center, radius, out PickupPlan outbound))
                return outbound;

            return null;
        }

        private static bool TryBuildPersistentInbound(ZDO workerZdo, long ownerId, Vector3 center, float radius, out PickupPlan plan)
        {
            plan = null;

            float allowed = Mathf.Max(4f, radius) + 4f;
            float allowedSqr = allowed * allowed;
            int bestAvailable = 0;
            ZDO bestSource = null;
            Vector3 bestSourcePosition = default;
            string bestPrefab = string.Empty;
            int bestMinimumRemain = 0;
            Vector3 bestImportPosition = default;
            int bestImportCapacity = 0;
            PersistentInboundPrefabScratch.Clear();

            PersistentContainerRegistryRuntime.ForEachPersistent((candidate, role, owner, position) =>
            {
                if (candidate == null || role != WorkerContainerRole.FieldDepot || !ContainerLedgerRuntime.HasSnapshot(candidate)) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                float dx = position.x - center.x;
                float dz = position.z - center.z;
                if (dx * dx + dz * dz > allowedSqr) return;

                ContainerLedgerRuntime.CollectSnapshotPrefabs(candidate, PersistentInboundPrefabScratch);
                for (int i = 0; i < PersistentInboundPrefabScratch.Count; i++)
                {
                    string prefab = PersistentInboundPrefabScratch[i];
                    int effectiveTarget = WorkerLogisticsRuntime.GetEffectiveFieldDepotTarget(candidate, prefab);
                    int keepOne = WorkerContainerRoleRuntime.ProtectLastItem(candidate) ? 1 : 0;
                    int minimumRemain = Mathf.Max(keepOne, effectiveTarget);
                    if (!ContainerLedgerRuntime.TryGetAvailable(candidate, prefab, minimumRemain, out int available) || available <= 0) continue;
                    bool managedImportBuffer = ContainerStockLimitRuntime.HasConfiguredImportLimit(ownerId, prefab);
                    // Legacy items still require real downstream Base storage. A configured Import
                    // Min/Max rule intentionally turns Import into a bounded overflow buffer instead.
                    if (!managedImportBuffer && !HasPersistentNormalCapacity(ownerId, prefab)) continue;
                    if (!ContainerStockLimitRuntime.TryFindPersistentImportWithCapacity(ownerId, center, prefab, out _, out Vector3 importPosition, out int importCapacity)) continue;
                    int movable = Mathf.Min(available, importCapacity);
                    if (movable <= bestAvailable) continue;
                    bestAvailable = movable;
                    bestSource = candidate;
                    bestSourcePosition = position;
                    bestPrefab = prefab;
                    bestMinimumRemain = minimumRemain;
                    bestImportPosition = importPosition;
                    bestImportCapacity = importCapacity;
                }
            });

            if (bestSource == null || string.IsNullOrEmpty(bestPrefab) || bestAvailable <= 0) return false;
            plan = new PickupPlan
            {
                Prefab = bestPrefab,
                SourceId = bestSource.m_uid,
                SourceRole = WorkerContainerRole.FieldDepot,
                SourcePosition = bestSourcePosition,
                Amount = Mathf.Min(10, Mathf.Min(bestAvailable, bestImportCapacity)),
                MinimumRemain = bestMinimumRemain,
                Route = WorkerLogisticsRuntime.RouteHaulInbound,
                RouteTarget = bestSourcePosition,
                DeliveryTargetKind = WorkerStorageRuntime.ImportDepositTargetKind,
                DeliveryPosition = bestImportPosition
            };
            return true;
        }

        private static bool HasPersistentNormalCapacity(long ownerId, string prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return false;
            bool found = false;
            PersistentContainerRegistryRuntime.ForEachPersistent((candidate, role, owner, position) =>
            {
                if (found || candidate == null || role != WorkerContainerRole.Normal) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                if (!candidate.GetBool(ContainerLedgerRuntime.SorterTrackedKey, false)) return;
                if (ContainerLedgerRuntime.GetSnapshotAmount(candidate, prefab) <= 0) return;
                if (ContainerLedgerRuntime.TryGetPersistentFreeCapacity(candidate, prefab, out int free) && free > 0) found = true;
            });
            return found;
        }

        private static bool TryBuildPersistentOutbound(ZDO workerZdo, long ownerId, Vector3 center, float radius, out PickupPlan plan)
        {
            plan = null;
            float allowed = Mathf.Max(4f, radius) + 4f;
            float allowedSqr = allowed * allowed;
            string workerWorkAreaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            bool baseTransporter = WorkerLogisticsRuntime.IsBaseTransporter(workerZdo);
            ZDO requestedDepot = null;
            Vector3 requestedPosition = default;
            string requestedPrefab = string.Empty;
            int requestedNeed = 0;
            RequestedPrefabScratch.Clear();
            ConfiguredPrefabScratch.Clear();
            ProvisionPrefabScratch.Clear();
            DesiredPrefabScratch.Clear();

            PersistentContainerRegistryRuntime.ForEachPersistent((candidate, role, owner, position) =>
            {
                if (candidate == null || role != WorkerContainerRole.FieldDepot) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                if (candidate.GetBool(WorkerContainerRoleRuntime.AirTransportKey, false)) return;
                string candidateWorkAreaId = FieldDepotWorkzoneRuntime.GetAssignedWorkAreaId(candidate);
                if (baseTransporter)
                {
                    // Base transporters serve every other ground WorkArea, never their own Base zone.
                    if (!string.IsNullOrEmpty(workerWorkAreaId) && !string.IsNullOrEmpty(candidateWorkAreaId) &&
                        string.Equals(workerWorkAreaId, candidateWorkAreaId, StringComparison.Ordinal)) return;
                }
                else
                {
                    if (!string.IsNullOrEmpty(workerWorkAreaId) && !string.IsNullOrEmpty(candidateWorkAreaId) &&
                        !string.Equals(workerWorkAreaId, candidateWorkAreaId, StringComparison.Ordinal)) return;
                    float dx = position.x - center.x;
                    float dz = position.z - center.z;
                    if (dx * dx + dz * dz > allowedSqr) return;
                }

                DesiredPrefabScratch.Clear();
                WorkerLogisticsRuntime.CollectRequestedPrefabs(candidate, RequestedPrefabScratch);
                FieldDepotStockRuleRuntime.CollectConfiguredPrefabs(candidate, ConfiguredPrefabScratch);
                WorkerProvisioningLogisticsRuntime.CollectGroundProvisionPrefabs(candidate, ProvisionPrefabScratch);
                for (int i = 0; i < RequestedPrefabScratch.Count; i++) DesiredPrefabScratch.Add(RequestedPrefabScratch[i]);
                for (int i = 0; i < ConfiguredPrefabScratch.Count; i++) DesiredPrefabScratch.Add(ConfiguredPrefabScratch[i]);
                for (int i = 0; i < ProvisionPrefabScratch.Count; i++) DesiredPrefabScratch.Add(ProvisionPrefabScratch[i]);
                foreach (string prefab in DesiredPrefabScratch)
                {
                    int target = WorkerLogisticsRuntime.GetEffectiveFieldDepotTarget(candidate, prefab);
                    if (target <= 0) continue;
                    int current = 0;
                    ContainerLedgerRuntime.TryGetCount(candidate, prefab, out current);
                    current += AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(candidate, prefab);
                    int inTransit = BackgroundWorkerScheduler.GetGroundOutboundInTransitAmount(ownerId, prefab, position);
                    int need = Mathf.Max(0, target - current - inTransit);
                    if (need <= requestedNeed) continue;
                    requestedDepot = candidate;
                    requestedPosition = position;
                    requestedPrefab = prefab;
                    requestedNeed = need;
                }
            });

            if (requestedDepot == null || string.IsNullOrEmpty(requestedPrefab) || requestedNeed <= 0) return false;

            ZDO bestExport = null;
            Vector3 bestExportPosition = default;
            int bestAvailable = 0;
            int bestMinimumRemain = 0;
            PersistentContainerRegistryRuntime.ForEachPersistent((candidate, role, owner, position) =>
            {
                if (candidate == null || role != WorkerContainerRole.Export || !ContainerLedgerRuntime.HasSnapshot(candidate)) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                int keepOne = WorkerContainerRoleRuntime.ProtectLastItem(candidate) ? 1 : 0;
                if (!ContainerLedgerRuntime.TryGetAvailable(candidate, requestedPrefab, keepOne, out int available) || available <= 0) return;
                if (available <= bestAvailable) return;
                bestExport = candidate;
                bestExportPosition = position;
                bestAvailable = available;
                bestMinimumRemain = keepOne;
            });

            if (bestExport == null || bestAvailable <= 0) return false;
            plan = new PickupPlan
            {
                Prefab = requestedPrefab,
                SourceId = bestExport.m_uid,
                SourceRole = WorkerContainerRole.Export,
                SourcePosition = bestExportPosition,
                Amount = Mathf.Min(10, Mathf.Min(requestedNeed, bestAvailable)),
                MinimumRemain = bestMinimumRemain,
                Route = WorkerLogisticsRuntime.RouteHaulOutbound,
                RouteTarget = requestedPosition,
                DeliveryTargetKind = WorkerStorageRuntime.SupplyFieldDepotDepositTargetKind,
                DeliveryPosition = requestedPosition,
                DeliveryId = string.IsNullOrEmpty(FieldDepotWorkzoneRuntime.GetAssignedWorkAreaId(requestedDepot)) ? requestedDepot.m_uid : ZDOID.None,
                DeliveryWorkAreaId = FieldDepotWorkzoneRuntime.GetAssignedWorkAreaId(requestedDepot)
            };
            return true;
        }

        private static PickupPlan CreateInbound(Container source, string prefab, int amount, Vector3 sourcePos, Vector3 importPos)
        {
            ContainerLedgerRuntime.SyncFromPhysical(source, "ground-plan-field-depot");
            int stock = source != null ? WorkerStorageRuntime.CountPrefab(source.GetInventory(), prefab) : amount;
            int minimumRemain = Mathf.Max(0, stock - Mathf.Max(0, amount));
            return new PickupPlan
            {
                Prefab = prefab,
                SourceId = WorkerContainerRoleRuntime.GetZdo(source)?.m_uid ?? ZDOID.None,
                SourceRole = WorkerContainerRole.FieldDepot,
                SourcePosition = sourcePos,
                Amount = amount,
                MinimumRemain = minimumRemain,
                Route = WorkerLogisticsRuntime.RouteHaulInbound,
                RouteTarget = sourcePos,
                DeliveryTargetKind = WorkerStorageRuntime.ImportDepositTargetKind,
                DeliveryPosition = importPos
            };
        }

        private static bool TryFindImportDestination(Character worker, ZDO zdo, string prefab, out Vector3 position)
        {
            position = default;
            if (worker == null || zdo == null || string.IsNullOrEmpty(prefab)) return false;
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Container best = null;
            float bestSqr = float.MaxValue;
            // Import is the global Base destination. Use the loaded container registry rather than
            // the outpost HomeAnchor radius so background handoff and physical hauling agree.
            WorkerStorageRuntime.ForEachLoadedContainer(c =>
            {
                if (c == null || !WorkerStorageRuntime.CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.Import) return;
                Inventory inv = c.GetInventory();
                if (inv == null) return;
                int physical = WorkerStorageRuntime.GetAvailableCapacityForPrefab(inv, prefab);
                if (ContainerStockLimitRuntime.GetAcceptCapacity(c, prefab, physical) <= 0) return;
                float sqr = (c.transform.position - worker.transform.position).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                best = c;
            });
            if (best != null)
            {
                position = best.transform.position;
                return true;
            }

            if (ContainerStockLimitRuntime.TryFindPersistentImportWithCapacity(ownerId, worker.transform.position, prefab, out _, out Vector3 persistentPosition, out _))
            {
                position = persistentPosition;
                return true;
            }
            return false;
        }

        internal static void ClearCaches()
        {
            NextSnapshot.Clear();
            PersistentInboundPrefabScratch.Clear();
            RequestedPrefabScratch.Clear();
            ConfiguredPrefabScratch.Clear();
            ProvisionPrefabScratch.Clear();
            DesiredPrefabScratch.Clear();
        }

        private static string BuildSummary(string mode, PickupPlan p)
        {
            return $"ground-pickup:{mode}; {p.Prefab} x{p.Amount}; {p.SourceRole} -> {p.DeliveryTargetKind}";
        }

        private static void Save(ZDO zdo, PickupPlan p)
        {
            if (zdo == null) return;
            if (p == null) { zdo.Set(PlanKey, string.Empty); zdo.Set(SourceIdKey, ZDOID.None); zdo.Set(DeliveryIdKey, ZDOID.None); zdo.Set(DeliveryWorkAreaIdKey, string.Empty); return; }
            zdo.Set(SourceIdKey, p.SourceId);
            zdo.Set(DeliveryIdKey, p.DeliveryId);
            zdo.Set(DeliveryWorkAreaIdKey, p.DeliveryWorkAreaId ?? string.Empty);
            string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
            zdo.Set(PlanKey, string.Join("|", new[]
            {
                p.Prefab ?? string.Empty,
                ((int)p.SourceRole).ToString(CultureInfo.InvariantCulture),
                F(p.SourcePosition.x), F(p.SourcePosition.y), F(p.SourcePosition.z),
                p.Amount.ToString(CultureInfo.InvariantCulture),
                p.MinimumRemain.ToString(CultureInfo.InvariantCulture),
                p.Route ?? string.Empty,
                F(p.RouteTarget.x), F(p.RouteTarget.y), F(p.RouteTarget.z),
                p.DeliveryTargetKind ?? string.Empty,
                F(p.DeliveryPosition.x), F(p.DeliveryPosition.y), F(p.DeliveryPosition.z)
            }));
        }

        private static PickupPlan Load(ZDO zdo)
        {
            if (zdo == null) return null;
            string raw = zdo.GetString(PlanKey, string.Empty);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string[] a = raw.Split('|');
            if (a.Length < 15) return null;
            try
            {
                float PF(int i) => float.Parse(a[i], CultureInfo.InvariantCulture);
                int PI(int i) => int.Parse(a[i], CultureInfo.InvariantCulture);
                return new PickupPlan
                {
                    Prefab = a[0],
                    SourceId = zdo.GetZDOID(SourceIdKey),
                    SourceRole = (WorkerContainerRole)PI(1),
                    SourcePosition = new Vector3(PF(2), PF(3), PF(4)),
                    Amount = PI(5),
                    MinimumRemain = PI(6),
                    Route = a[7],
                    RouteTarget = new Vector3(PF(8), PF(9), PF(10)),
                    DeliveryTargetKind = a[11],
                    DeliveryPosition = new Vector3(PF(12), PF(13), PF(14)),
                    DeliveryId = zdo.GetZDOID(DeliveryIdKey),
                    DeliveryWorkAreaId = zdo.GetString(DeliveryWorkAreaIdKey, string.Empty)
                };
            }
            catch { return null; }
        }
    }
}
