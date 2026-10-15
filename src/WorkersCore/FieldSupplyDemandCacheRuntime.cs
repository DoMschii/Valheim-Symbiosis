using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Event-driven FieldDepot demand/index layer.
    ///
    /// v0.5.342: demands are addressed to a Workzone, not permanently to one chest. Every loaded
    /// FieldDepot publishes its per-item target and inventory snapshot. Depots assigned to the same
    /// Workzone are aggregated into one logical pool. A worker only resolves the concrete destination
    /// chest when it is about to execute the delivery.
    /// </summary>
    internal static class FieldSupplyDemandCacheRuntime
    {
        private sealed class DemandEntry
        {
            internal Container Depot;
            internal ZDO Zdo;
            internal int ContainerId;
            internal long OwnerId;
            internal string Prefab;
            internal string WorkAreaId;
            internal int Target;
        }

        private sealed class DepotState
        {
            internal Container Depot;
            internal ZDO Zdo;
            internal int ContainerId;
            internal long OwnerId;
            internal string WorkAreaId;
            internal readonly Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class DemandGroup
        {
            internal long OwnerId;
            internal string Prefab;
            internal string WorkAreaId;
            internal Container Representative;
            internal Vector3 Position;
            internal float Radius;
            internal int Target;
            internal int Current;
            internal bool LegacyStandalone;
        }

        private sealed class PersistentDemandGroup
        {
            internal long OwnerId;
            internal string Prefab;
            internal string WorkAreaId;
            internal ZDO Representative;
            internal Vector3 Position;
            internal float Radius;
            internal int Target;
            internal int Current;
            internal bool LegacyStandalone;
        }

        private static readonly Dictionary<string, DemandEntry> Entries =
            new Dictionary<string, DemandEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<ZDOID, Container> LoadedDepots =
            new Dictionary<ZDOID, Container>();
        private static readonly Dictionary<int, DepotState> DepotStates = new Dictionary<int, DepotState>();
        private static readonly Dictionary<string, DemandGroup> GroupScratch =
            new Dictionary<string, DemandGroup>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, PersistentDemandGroup> PersistentGroupScratch =
            new Dictionary<string, PersistentDemandGroup>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> PersistentRequestedScratch = new List<string>(16);
        private static readonly List<string> PersistentConfiguredScratch = new List<string>(16);
        private static readonly List<string> PersistentProvisionScratch = new List<string>(16);
        private static readonly HashSet<string> PersistentPrefabSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<int> TrackedDepotIds = new HashSet<int>();
        private static readonly List<string> EntryKeyScratch = new List<string>(32);
        private static readonly List<string> PrefabScratchA = new List<string>(16);
        private static readonly List<string> PrefabScratchB = new List<string>(16);
        private static readonly List<string> PrefabScratchC = new List<string>(16);
        private static readonly HashSet<string> PrefabSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _bootstrapped;

        internal static int PolicyEntryCount => Entries.Count;

        internal static void OnContainerAwake(Container container) => RefreshContainer(container);
        internal static void OnContainerSaved(Container container) => RefreshContainer(container);
        internal static void OnRoleOrPolicyChanged(Container container) => RefreshContainer(container);

        internal static void NotifyRequestChanged(ZDO depotZdo)
        {
            if (depotZdo == null) return;
            if (LoadedDepots.TryGetValue(depotZdo.m_uid, out Container loaded) && loaded != null)
                RefreshContainer(loaded);
        }

        internal static int GetLoadedDepotCountForWorkArea(string workAreaId, long ownerId = 0L)
        {
            if (string.IsNullOrEmpty(workAreaId)) return 0;
            EnsureBootstrapped();
            int count = 0;
            foreach (DepotState state in DepotStates.Values)
            {
                if (!IsLiveState(state)) continue;
                if (!string.Equals(state.WorkAreaId, workAreaId, StringComparison.Ordinal)) continue;
                if (ownerId != 0L && state.OwnerId != 0L && ownerId != state.OwnerId) continue;
                count++;
            }
            return count;
        }

        internal static int GetPooledStock(string workAreaId, long ownerId, string prefab)
        {
            if (string.IsNullOrEmpty(workAreaId) || string.IsNullOrWhiteSpace(prefab)) return 0;
            EnsureBootstrapped();
            string key = WorkerStorageRuntime.NormalizeObjectName(prefab);
            long total = 0L;
            foreach (DepotState state in DepotStates.Values)
            {
                if (!IsLiveState(state)) continue;
                if (!string.Equals(state.WorkAreaId, workAreaId, StringComparison.Ordinal)) continue;
                if (ownerId != 0L && state.OwnerId != 0L && ownerId != state.OwnerId) continue;
                if (state.Counts.TryGetValue(key, out int count)) total += Mathf.Max(0, count);
                if (total >= int.MaxValue) return int.MaxValue;
            }
            return (int)Math.Max(0L, total);
        }

        internal struct PoolDemandSnapshot
        {
            internal string Prefab;
            internal int Target;
            internal int Current;
            internal int InTransit;
            internal int StagingClaimed;
            internal int OpenNeed;
        }

        private static readonly Dictionary<string, int> PoolTargetScratch =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> PoolCurrentScratch =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> PoolPrefabScratch = new List<string>(24);

        /// <summary>
        /// Returns an already-cached, zone-wide logistics view for UI/debugging. This method never
        /// scans the world or enumerates arbitrary Containers; it only reads the event-maintained
        /// FieldDepot demand/state indexes plus the existing in-transit scheduler snapshot.
        /// </summary>
        internal static void CollectPoolDemandSnapshots(string workAreaId, long ownerId, List<PoolDemandSnapshot> output)
        {
            if (output == null) return;
            output.Clear();
            if (string.IsNullOrEmpty(workAreaId)) return;
            EnsureBootstrapped();

            PoolTargetScratch.Clear();
            PoolCurrentScratch.Clear();
            PoolPrefabScratch.Clear();

            foreach (DemandEntry entry in Entries.Values)
            {
                if (entry == null || entry.Target <= 0) continue;
                if (!string.Equals(entry.WorkAreaId, workAreaId, StringComparison.Ordinal)) continue;
                if (ownerId != 0L && entry.OwnerId != 0L && ownerId != entry.OwnerId) continue;
                string prefab = WorkerStorageRuntime.NormalizeObjectName(entry.Prefab);
                if (string.IsNullOrWhiteSpace(prefab)) continue;
                if (!PoolTargetScratch.ContainsKey(prefab)) PoolPrefabScratch.Add(prefab);
                PoolTargetScratch.TryGetValue(prefab, out int previous);
                PoolTargetScratch[prefab] = Mathf.Max(Mathf.Max(0, previous), Mathf.Max(0, entry.Target));
            }

            foreach (DepotState state in DepotStates.Values)
            {
                if (!IsLiveState(state)) continue;
                if (!string.Equals(state.WorkAreaId, workAreaId, StringComparison.Ordinal)) continue;
                if (ownerId != 0L && state.OwnerId != 0L && ownerId != state.OwnerId) continue;
                foreach (KeyValuePair<string, int> pair in state.Counts)
                {
                    if (!PoolTargetScratch.ContainsKey(pair.Key)) continue;
                    PoolCurrentScratch.TryGetValue(pair.Key, out int previous);
                    PoolCurrentScratch[pair.Key] = SafeAdd(previous, pair.Value);
                }
            }

            Vector3 fallbackPosition = Vector3.zero;
            float fallbackRadius = 24f;
            if (WorkFlagRegistry.TryFindById(workAreaId, 40f, out WorkAreaSnapshot zone))
            {
                fallbackPosition = zone.Center;
                fallbackRadius = Mathf.Max(24f, zone.Radius);
            }

            for (int i = 0; i < PoolPrefabScratch.Count; i++)
            {
                string prefab = PoolPrefabScratch[i];
                int target = PoolTargetScratch.TryGetValue(prefab, out int t) ? Mathf.Max(0, t) : 0;
                int current = PoolCurrentScratch.TryGetValue(prefab, out int c) ? Mathf.Max(0, c) : 0;
                int physicalInTransit = BackgroundWorkerScheduler.GetGroundOutboundInTransitAmount(ownerId, prefab, workAreaId, fallbackPosition, fallbackRadius);
                int pickupClaimed = HaulerDemandClaimRuntime.GetClaimedAmount(ownerId, prefab, workAreaId, ZDOID.None);
                int inTransit = SafeAdd(physicalInTransit, pickupClaimed);
                int stagingClaimed = SorterDemandClaimRuntime.GetClaimedAmount(ownerId, prefab, workAreaId, ZDOID.None);
                int effective = SafeAdd(SafeAdd(current, inTransit), stagingClaimed);
                output.Add(new PoolDemandSnapshot
                {
                    Prefab = prefab,
                    Target = target,
                    Current = current,
                    InTransit = Mathf.Max(0, inTransit),
                    StagingClaimed = Mathf.Max(0, stagingClaimed),
                    OpenNeed = Mathf.Max(0, target - effective)
                });
            }

            PoolTargetScratch.Clear();
            PoolCurrentScratch.Clear();
            PoolPrefabScratch.Clear();
        }


        internal static int GetTotalOpenGroundNeed(long ownerId, string prefab)
        {
            if (string.IsNullOrWhiteSpace(prefab)) return 0;
            EnsureBootstrapped();
            string normalized = WorkerStorageRuntime.NormalizeObjectName(prefab);
            GroupScratch.Clear();

            foreach (DemandEntry entry in Entries.Values)
            {
                if (entry == null || entry.Depot == null || entry.Target <= 0) continue;
                if (!string.Equals(WorkerStorageRuntime.NormalizeObjectName(entry.Prefab), normalized, StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.Depot.gameObject == null || !entry.Depot.gameObject.activeInHierarchy) continue;
                if (WorkerContainerRoleRuntime.GetRole(entry.Depot) != WorkerContainerRole.FieldDepot) continue;
                if (entry.OwnerId != 0L && ownerId != 0L && entry.OwnerId != ownerId) continue;
                if (WorkerContainerRoleRuntime.SupportsAirTransport(entry.Depot)) continue;
                if (AirHaulerWorkRuntime.IsLinkedToOutpostAirHub(ownerId, entry.Depot.transform.position)) continue;

                bool pooled = !string.IsNullOrEmpty(entry.WorkAreaId);
                string groupKey = pooled
                    ? entry.OwnerId.ToString() + "|zone|" + entry.WorkAreaId + "|" + normalized
                    : entry.OwnerId.ToString() + "|legacy|" + entry.ContainerId + "|" + normalized;
                if (!GroupScratch.TryGetValue(groupKey, out DemandGroup group))
                {
                    group = new DemandGroup
                    {
                        OwnerId = entry.OwnerId,
                        Prefab = normalized,
                        WorkAreaId = entry.WorkAreaId ?? string.Empty,
                        Representative = entry.Depot,
                        Position = entry.Depot.transform.position,
                        Radius = 24f,
                        LegacyStandalone = !pooled
                    };
                    if (pooled && WorkFlagRegistry.TryFindById(entry.WorkAreaId, 40f, out WorkAreaSnapshot zone))
                    {
                        group.Position = zone.Center;
                        group.Radius = Mathf.Max(12f, zone.Radius);
                    }
                    GroupScratch[groupKey] = group;
                }
                group.Target = Mathf.Max(group.Target, entry.Target);
            }

            long totalNeed = 0L;
            foreach (DemandGroup group in GroupScratch.Values)
            {
                if (group == null || group.Target <= 0) continue;
                int current;
                if (group.LegacyStandalone)
                {
                    int containerId = group.Representative != null ? group.Representative.GetInstanceID() : 0;
                    current = containerId != 0 && DepotStates.TryGetValue(containerId, out DepotState state) && state != null
                        ? GetStateCount(state, group.Prefab)
                        : 0;
                }
                else
                {
                    current = GetPooledStock(group.WorkAreaId, group.OwnerId, group.Prefab);
                }

                int inTransit = group.LegacyStandalone
                    ? BackgroundWorkerScheduler.GetGroundOutboundInTransitAmount(group.OwnerId, group.Prefab, group.Position, 24f)
                    : BackgroundWorkerScheduler.GetGroundOutboundInTransitAmount(group.OwnerId, group.Prefab, group.WorkAreaId, group.Position, Mathf.Max(24f, group.Radius));
                int need = Mathf.Max(0, group.Target - SafeAdd(current, inTransit));
                totalNeed += need;
                if (totalNeed >= int.MaxValue)
                {
                    GroupScratch.Clear();
                    return int.MaxValue;
                }
            }
            GroupScratch.Clear();
            return (int)Math.Max(0L, totalNeed);
        }

        internal static bool TryFindPersistentGroundRequest(ZDO workerZdo, bool restrictToAssignedOutpost,
            bool sorterClaimAware, bool haulerClaimAware, bool excludeAssignedWorkArea,
            out WorkerLogisticsRuntime.SupplyRequest request)
        {
            request = default;
            if (workerZdo == null) return false;

            long workerOwnerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            string workerWorkAreaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            Vector3 workerPosition = workerZdo.GetPosition();
            PersistentGroupScratch.Clear();

            WorkerProvisioningLogisticsRuntime.ForEachCachedFieldDepot((candidate, ownerId, position) =>
            {
                if (candidate == null) return;
                if (ownerId != 0L && workerOwnerId != 0L && ownerId != workerOwnerId) return;
                string areaId = FieldDepotWorkzoneRuntime.GetAssignedWorkAreaId(candidate);
                if (excludeAssignedWorkArea && !string.IsNullOrEmpty(workerWorkAreaId) && !string.IsNullOrEmpty(areaId) &&
                    string.Equals(workerWorkAreaId, areaId, StringComparison.Ordinal)) return;
                if (restrictToAssignedOutpost)
                {
                    if (!string.IsNullOrEmpty(workerWorkAreaId) && !string.IsNullOrEmpty(areaId))
                    {
                        if (!string.Equals(workerWorkAreaId, areaId, StringComparison.Ordinal)) return;
                    }
                    else if (!WorkerLogisticsRuntime.IsInsideAssignedOutpost(workerZdo, position, 4f)) return;
                }

                PersistentRequestedScratch.Clear();
                PersistentConfiguredScratch.Clear();
                PersistentProvisionScratch.Clear();
                PersistentPrefabSet.Clear();
                WorkerLogisticsRuntime.CollectRequestedPrefabs(candidate, PersistentRequestedScratch);
                FieldDepotStockRuleRuntime.CollectConfiguredPrefabs(candidate, PersistentConfiguredScratch);
                WorkerProvisioningLogisticsRuntime.CollectGroundProvisionPrefabs(candidate, PersistentProvisionScratch);
                for (int i = 0; i < PersistentRequestedScratch.Count; i++) PersistentPrefabSet.Add(PersistentRequestedScratch[i]);
                for (int i = 0; i < PersistentConfiguredScratch.Count; i++) PersistentPrefabSet.Add(PersistentConfiguredScratch[i]);
                for (int i = 0; i < PersistentProvisionScratch.Count; i++) PersistentPrefabSet.Add(PersistentProvisionScratch[i]);

                foreach (string prefab in PersistentPrefabSet)
                {
                    if (string.IsNullOrWhiteSpace(prefab)) continue;
                    int target = WorkerLogisticsRuntime.GetEffectiveFieldDepotTarget(candidate, prefab);
                    if (target <= 0) continue;
                    bool pooled = !string.IsNullOrEmpty(areaId);
                    string key = pooled
                        ? ownerId.ToString() + "|zone|" + areaId + "|" + prefab
                        : ownerId.ToString() + "|legacy|" + candidate.m_uid.ToString() + "|" + prefab;
                    if (!PersistentGroupScratch.TryGetValue(key, out PersistentDemandGroup group))
                    {
                        group = new PersistentDemandGroup
                        {
                            OwnerId = ownerId,
                            Prefab = prefab,
                            WorkAreaId = areaId ?? string.Empty,
                            Representative = candidate,
                            Position = position,
                            Radius = 24f,
                            LegacyStandalone = !pooled
                        };
                        if (pooled && WorkFlagRegistry.TryFindById(areaId, 40f, out WorkAreaSnapshot zone))
                        {
                            group.Position = zone.Center;
                            group.Radius = Mathf.Max(12f, zone.Radius);
                        }
                        else if (pooled && WorkFlagRegistry.TryGetPersistentState(areaId, out ZDO flagZdo) && flagZdo != null)
                        {
                            group.Position = flagZdo.GetPosition();
                            group.Radius = Mathf.Max(12f, WorkFlagZdo.ReadRadius(flagZdo, 24f));
                        }
                        PersistentGroupScratch[key] = group;
                    }
                    group.Target = Mathf.Max(group.Target, target);
                    int current = 0;
                    ContainerLedgerRuntime.TryGetCount(candidate, prefab, out current);
                    current += AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(candidate, prefab);
                    group.Current = SafeAdd(group.Current, current);
                }
            });

            PersistentDemandGroup best = null;
            int bestNeed = 0;
            int bestEffectiveCurrent = 0;
            float bestDistance = float.MaxValue;
            foreach (PersistentDemandGroup group in PersistentGroupScratch.Values)
            {
                if (group == null || group.Target <= 0) continue;
                int inTransit = group.LegacyStandalone
                    ? BackgroundWorkerScheduler.GetGroundOutboundInTransitAmount(group.OwnerId, group.Prefab, group.Position, 24f)
                    : BackgroundWorkerScheduler.GetGroundOutboundInTransitAmount(group.OwnerId, group.Prefab, group.WorkAreaId, group.Position, Mathf.Max(24f, group.Radius));
                int effectiveCurrent = SafeAdd(group.Current, inTransit);
                int need = Mathf.Max(0, group.Target - effectiveCurrent);
                if (need <= 0) continue;

                ZDOID legacyId = group.LegacyStandalone && group.Representative != null ? group.Representative.m_uid : ZDOID.None;
                int selectableNeed = need;
                if (sorterClaimAware)
                    selectableNeed = Mathf.Max(0, selectableNeed - SorterDemandClaimRuntime.GetClaimedAmount(group.OwnerId, group.Prefab, group.WorkAreaId, legacyId));
                if (haulerClaimAware)
                    selectableNeed = Mathf.Max(0, selectableNeed - HaulerDemandClaimRuntime.GetClaimedAmount(group.OwnerId, group.Prefab, group.WorkAreaId, legacyId));
                if (selectableNeed <= 0) continue;

                float sqr = (group.Position - workerPosition).sqrMagnitude;
                bool better = best == null || selectableNeed > bestNeed || (selectableNeed == bestNeed && sqr < bestDistance);
                if (!better) continue;
                best = group;
                bestNeed = selectableNeed;
                bestEffectiveCurrent = effectiveCurrent;
                bestDistance = sqr;
            }

            if (best == null || bestNeed <= 0)
            {
                PersistentGroupScratch.Clear();
                return false;
            }

            if (!best.LegacyStandalone && !string.IsNullOrEmpty(best.WorkAreaId))
                request = new WorkerLogisticsRuntime.SupplyRequest(best.WorkAreaId, best.Position, best.Prefab, best.Target, bestEffectiveCurrent);
            else
                request = new WorkerLogisticsRuntime.SupplyRequest(best.Representative != null ? best.Representative.m_uid : ZDOID.None, best.Position, best.Prefab, best.Target, bestEffectiveCurrent);
            PersistentGroupScratch.Clear();
            return request.Need > 0;
        }

        internal static int GetPersistentTotalOpenGroundNeed(long ownerId, string prefab)
        {
            if (string.IsNullOrWhiteSpace(prefab)) return 0;
            long total = 0L;
            PersistentGroupScratch.Clear();
            WorkerProvisioningLogisticsRuntime.ForEachCachedFieldDepot((candidate, owner, position) =>
            {
                if (candidate == null || (owner != 0L && ownerId != 0L && owner != ownerId)) return;
                int target = WorkerLogisticsRuntime.GetEffectiveFieldDepotTarget(candidate, prefab);
                if (target <= 0) return;
                string areaId = FieldDepotWorkzoneRuntime.GetAssignedWorkAreaId(candidate);
                bool pooled = !string.IsNullOrEmpty(areaId);
                string key = pooled ? owner.ToString() + "|zone|" + areaId : owner.ToString() + "|legacy|" + candidate.m_uid.ToString();
                if (!PersistentGroupScratch.TryGetValue(key, out PersistentDemandGroup group))
                {
                    group = new PersistentDemandGroup
                    {
                        OwnerId = owner,
                        Prefab = prefab,
                        WorkAreaId = areaId ?? string.Empty,
                        Representative = candidate,
                        Position = position,
                        Radius = 24f,
                        Target = 0,
                        Current = 0,
                        LegacyStandalone = !pooled
                    };
                    if (pooled && WorkFlagRegistry.TryGetPersistentState(areaId, out ZDO flagZdo) && flagZdo != null)
                    {
                        group.Position = flagZdo.GetPosition();
                        group.Radius = Mathf.Max(12f, WorkFlagZdo.ReadRadius(flagZdo, 24f));
                    }
                    PersistentGroupScratch[key] = group;
                }
                group.Target = Mathf.Max(group.Target, target);
                int current = 0;
                ContainerLedgerRuntime.TryGetCount(candidate, prefab, out current);
                current += AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(candidate, prefab);
                group.Current = SafeAdd(group.Current, current);
            });

            foreach (PersistentDemandGroup group in PersistentGroupScratch.Values)
            {
                int inTransit = group.LegacyStandalone
                    ? BackgroundWorkerScheduler.GetGroundOutboundInTransitAmount(group.OwnerId, prefab, group.Position, 24f)
                    : BackgroundWorkerScheduler.GetGroundOutboundInTransitAmount(group.OwnerId, prefab, group.WorkAreaId, group.Position, Mathf.Max(24f, group.Radius));
                total += Mathf.Max(0, group.Target - SafeAdd(group.Current, inTransit));
                if (total >= int.MaxValue) { total = int.MaxValue; break; }
            }
            PersistentGroupScratch.Clear();
            return (int)Math.Max(0L, total);
        }

        internal static bool TryFindGroundRequest(Character worker, ZDO workerZdo,
            out WorkerLogisticsRuntime.SupplyRequest request)
        {
            return TryFindGroundRequestInternal(worker, workerZdo, false, false, false, false, out request);
        }

        internal static bool TryFindGroundRequestForAssignedOutpost(Character worker, ZDO workerZdo,
            out WorkerLogisticsRuntime.SupplyRequest request)
        {
            return TryFindGroundRequestInternal(worker, workerZdo, true, false, false, false, out request);
        }

        internal static bool TryFindGroundRequestForSorter(Character worker, ZDO workerZdo,
            out WorkerLogisticsRuntime.SupplyRequest request)
        {
            return TryFindGroundRequestInternal(worker, workerZdo, false, true, false, false, out request);
        }

        internal static bool TryFindGroundRequestForAssignedOutpostClaimAware(Character worker, ZDO workerZdo,
            out WorkerLogisticsRuntime.SupplyRequest request)
        {
            return TryFindGroundRequestInternal(worker, workerZdo, true, false, true, false, out request);
        }

        internal static bool TryFindGroundRequestForBaseTransporter(Character worker, ZDO workerZdo, bool claimAware,
            out WorkerLogisticsRuntime.SupplyRequest request)
        {
            return TryFindGroundRequestInternal(worker, workerZdo, false, false, claimAware, true, out request);
        }

        private static bool TryFindGroundRequestInternal(Character worker, ZDO workerZdo,
            bool restrictToAssignedOutpost, bool sorterClaimAware, bool haulerClaimAware, bool excludeAssignedWorkArea, out WorkerLogisticsRuntime.SupplyRequest request)
        {
            request = default;
            if (worker == null || workerZdo == null) return false;
            EnsureBootstrapped();

            long workerOwnerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            string workerWorkAreaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            GroupScratch.Clear();

            foreach (DemandEntry entry in Entries.Values)
            {
                if (entry == null || entry.Depot == null || entry.Target <= 0) continue;
                if (entry.Depot.gameObject == null || !entry.Depot.gameObject.activeInHierarchy) continue;
                if (WorkerContainerRoleRuntime.GetRole(entry.Depot) != WorkerContainerRole.FieldDepot) continue;
                if (entry.OwnerId != 0L && workerOwnerId != 0L && entry.OwnerId != workerOwnerId) continue;
                if (excludeAssignedWorkArea && !string.IsNullOrEmpty(workerWorkAreaId) &&
                    !string.IsNullOrEmpty(entry.WorkAreaId) &&
                    string.Equals(workerWorkAreaId, entry.WorkAreaId, StringComparison.Ordinal)) continue;

                if (restrictToAssignedOutpost)
                {
                    if (!string.IsNullOrEmpty(workerWorkAreaId) && !string.IsNullOrEmpty(entry.WorkAreaId))
                    {
                        if (!string.Equals(workerWorkAreaId, entry.WorkAreaId, StringComparison.Ordinal)) continue;
                    }
                    else if (!WorkerLogisticsRuntime.IsInsideAssignedOutpost(workerZdo, entry.Depot.transform.position, 4f))
                    {
                        continue;
                    }
                }

                // AirFreight deficits are staged through the Air logistics path, not ground Export.
                if (WorkerContainerRoleRuntime.SupportsAirTransport(entry.Depot)) continue;
                if (AirHaulerWorkRuntime.IsLinkedToOutpostAirHub(workerOwnerId, entry.Depot.transform.position)) continue;

                bool pooled = !string.IsNullOrEmpty(entry.WorkAreaId);
                string groupKey = pooled
                    ? entry.OwnerId.ToString() + "|zone|" + entry.WorkAreaId + "|" + entry.Prefab
                    : entry.OwnerId.ToString() + "|legacy|" + entry.ContainerId + "|" + entry.Prefab;

                if (!GroupScratch.TryGetValue(groupKey, out DemandGroup group))
                {
                    group = new DemandGroup
                    {
                        OwnerId = entry.OwnerId,
                        Prefab = entry.Prefab,
                        WorkAreaId = entry.WorkAreaId ?? string.Empty,
                        Representative = entry.Depot,
                        Position = entry.Depot.transform.position,
                        Radius = 24f,
                        LegacyStandalone = !pooled
                    };
                    if (pooled && WorkFlagRegistry.TryFindById(entry.WorkAreaId, 40f, out WorkAreaSnapshot zone))
                    {
                        group.Position = zone.Center;
                        group.Radius = Mathf.Max(12f, zone.Radius);
                    }
                    GroupScratch[groupKey] = group;
                }
                group.Target = Mathf.Max(group.Target, entry.Target);
            }

            DemandGroup best = null;
            int bestNeed = 0;
            int bestEffectiveCurrent = 0;
            float bestDistance = float.MaxValue;
            foreach (DemandGroup group in GroupScratch.Values)
            {
                if (group == null || group.Target <= 0) continue;
                if (group.LegacyStandalone)
                {
                    int containerId = group.Representative != null ? group.Representative.GetInstanceID() : 0;
                    if (containerId != 0 && DepotStates.TryGetValue(containerId, out DepotState state) && state != null)
                        group.Current = GetStateCount(state, group.Prefab);
                }
                else
                {
                    group.Current = GetPooledStock(group.WorkAreaId, group.OwnerId, group.Prefab);
                }

                int inTransit = group.LegacyStandalone
                    ? BackgroundWorkerScheduler.GetGroundOutboundInTransitAmount(group.OwnerId, group.Prefab, group.Position, 24f)
                    : BackgroundWorkerScheduler.GetGroundOutboundInTransitAmount(group.OwnerId, group.Prefab, group.WorkAreaId, group.Position, Mathf.Max(24f, group.Radius));
                int effectiveCurrent = SafeAdd(group.Current, inTransit);
                int need = Mathf.Max(0, group.Target - effectiveCurrent);
                if (need <= 0) continue;

                // v0.5.347: physical Sorters reserve an open staging slice before they leave Base
                // storage. Exclude already claimed slices only for Sorter job selection so a second
                // Sorter can pick another DemandJob instead of duplicating the same one. Haulers
                // intentionally ignore these staging claims because the real demand remains open
                // until physical Export/transport stock actually reaches the outpost.
                int selectableNeed = need;
                ZDOID legacyDepotId = ZDOID.None;
                if (group.LegacyStandalone && group.Representative != null)
                {
                    ZDO legacyZdo = WorkerContainerRoleRuntime.GetZdo(group.Representative);
                    if (legacyZdo != null) legacyDepotId = legacyZdo.m_uid;
                }
                if (sorterClaimAware)
                {
                    int claimed = SorterDemandClaimRuntime.GetClaimedAmount(group.OwnerId, group.Prefab,
                        group.WorkAreaId, legacyDepotId);
                    selectableNeed = Mathf.Max(0, need - claimed);
                    if (selectableNeed <= 0) continue;
                }
                if (haulerClaimAware)
                {
                    int claimed = HaulerDemandClaimRuntime.GetClaimedAmount(group.OwnerId, group.Prefab,
                        group.WorkAreaId, legacyDepotId);
                    selectableNeed = Mathf.Max(0, need - claimed);
                    if (selectableNeed <= 0) continue;
                }

                float sqr = (group.Position - worker.transform.position).sqrMagnitude;
                bool better = best == null || selectableNeed > bestNeed || (selectableNeed == bestNeed && sqr < bestDistance);
                if (!better) continue;
                best = group;
                bestNeed = selectableNeed;
                bestEffectiveCurrent = effectiveCurrent;
                bestDistance = sqr;
            }

            if (best == null || bestNeed <= 0) return false;
            if (!best.LegacyStandalone && !string.IsNullOrEmpty(best.WorkAreaId))
                request = new WorkerLogisticsRuntime.SupplyRequest(best.WorkAreaId, best.Position, best.Prefab, best.Target, bestEffectiveCurrent);
            else
                request = new WorkerLogisticsRuntime.SupplyRequest(best.Representative, best.Prefab, best.Target, bestEffectiveCurrent, string.Empty);
            return request.Need > 0;
        }

        internal static bool TryFindDestinationInWorkArea(Character worker, ZDO workerZdo, string prefab,
            string workAreaId, out Container destination, out Vector3 position)
        {
            destination = null;
            position = default;
            if (worker == null || workerZdo == null || string.IsNullOrEmpty(workAreaId) || string.IsNullOrWhiteSpace(prefab)) return false;
            EnsureBootstrapped();
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);

            // The Workzone target is pooled, so the concrete chest must not be biased toward the
            // one depot that happens to hold the canonical request manifest. Prefer completing an
            // existing partial stack first; otherwise use the least occupied compatible depot.
            // This keeps stacks compact while letting several FieldDepots actually share the load.
            bool bestHasPartial = false;
            int bestPartialFill = -1;
            int bestOccupiedSlots = int.MaxValue;
            int bestPrefabCount = int.MaxValue;
            int bestRoom = -1;
            float bestDistance = float.MaxValue;

            foreach (DepotState state in DepotStates.Values)
            {
                if (!IsLiveState(state) || state.Depot == null) continue;
                if (!string.Equals(state.WorkAreaId, workAreaId, StringComparison.Ordinal)) continue;
                if (ownerId != 0L && state.OwnerId != 0L && ownerId != state.OwnerId) continue;
                Container candidate = state.Depot;
                if (WorkerContainerRoleRuntime.SupportsAirTransport(candidate)) continue;
                if (!WorkerStorageRuntime.CanUseContainer(candidate, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(candidate, ownerId)) continue;
                Inventory inv = null;
                try { inv = candidate.GetInventory(); } catch { }
                if (inv == null) continue;
                int room = WorkerStorageRuntime.GetAvailableCapacityForPrefab(inv, prefab);
                if (room <= 0) continue;

                int partialFill = WorkerStorageRuntime.GetFullestPartialStackAmount(inv, prefab);
                bool hasPartial = partialFill > 0;
                int occupiedSlots = WorkerStorageRuntime.GetOccupiedSlotCount(inv);
                int current = GetStateCount(state, prefab);
                float sqr = (candidate.transform.position - worker.transform.position).sqrMagnitude;

                bool better = destination == null ||
                              (hasPartial && !bestHasPartial) ||
                              (hasPartial == bestHasPartial && hasPartial && partialFill > bestPartialFill) ||
                              (hasPartial == bestHasPartial && (!hasPartial || partialFill == bestPartialFill) && occupiedSlots < bestOccupiedSlots) ||
                              (hasPartial == bestHasPartial && (!hasPartial || partialFill == bestPartialFill) && occupiedSlots == bestOccupiedSlots && current < bestPrefabCount) ||
                              (hasPartial == bestHasPartial && (!hasPartial || partialFill == bestPartialFill) && occupiedSlots == bestOccupiedSlots && current == bestPrefabCount && room > bestRoom) ||
                              (hasPartial == bestHasPartial && (!hasPartial || partialFill == bestPartialFill) && occupiedSlots == bestOccupiedSlots && current == bestPrefabCount && room == bestRoom && sqr < bestDistance);
                if (!better) continue;

                destination = candidate;
                position = candidate.transform.position;
                bestHasPartial = hasPartial;
                bestPartialFill = partialFill;
                bestOccupiedSlots = occupiedSlots;
                bestPrefabCount = current;
                bestRoom = room;
                bestDistance = sqr;
            }
            return destination != null;
        }

        internal static int GetFreeCapacityInWorkArea(Character worker, ZDO workerZdo, string prefab, string workAreaId)
        {
            if (worker == null || workerZdo == null || string.IsNullOrEmpty(workAreaId) || string.IsNullOrWhiteSpace(prefab)) return 0;
            EnsureBootstrapped();
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            long total = 0L;
            foreach (DepotState state in DepotStates.Values)
            {
                if (!IsLiveState(state) || state.Depot == null) continue;
                if (!string.Equals(state.WorkAreaId, workAreaId, StringComparison.Ordinal)) continue;
                if (ownerId != 0L && state.OwnerId != 0L && ownerId != state.OwnerId) continue;
                Container candidate = state.Depot;
                if (WorkerContainerRoleRuntime.SupportsAirTransport(candidate)) continue;
                if (!WorkerStorageRuntime.CanUseContainer(candidate, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(candidate, ownerId)) continue;
                Inventory inv = null;
                try { inv = candidate.GetInventory(); } catch { }
                if (inv == null) continue;
                total += Mathf.Max(0, WorkerStorageRuntime.GetAvailableCapacityForPrefab(inv, prefab));
                if (total >= int.MaxValue) return int.MaxValue;
            }
            return (int)Math.Max(0L, total);
        }

        private static void EnsureBootstrapped()
        {
            if (_bootstrapped) return;
            _bootstrapped = true;
            if (LoadedDepots.Count > 0) return;
            try
            {
                WorkerWorldIndex.ForEachLoadedContainer(container =>
                {
                    if (container != null && WorkerContainerRoleRuntime.GetRole(container) == WorkerContainerRole.FieldDepot)
                        RefreshContainer(container);
                });
            }
            catch { }
        }

        private static void RefreshContainer(Container container)
        {
            if (container == null || container.gameObject == null) return;
            int containerId = container.GetInstanceID();
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(container);
            WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(container);

            if (zdo == null || role != WorkerContainerRole.FieldDepot)
            {
                if (TrackedDepotIds.Remove(containerId)) RemoveEntriesForContainer(containerId);
                DepotStates.Remove(containerId);
                if (zdo != null) LoadedDepots.Remove(zdo.m_uid);
                return;
            }

            TrackedDepotIds.Add(containerId);
            RemoveEntriesForContainer(containerId);
            LoadedDepots[zdo.m_uid] = container;
            Inventory inventory = null;
            try { inventory = container.GetInventory(); } catch { }
            if (inventory == null) return;

            long ownerId = zdo.GetLong(WorkerContainerRoleRuntime.OwnerKey, 0L);
            string workAreaId = FieldDepotWorkzoneRuntime.GetAssignedWorkAreaId(zdo);
            DepotState state = DepotStates.TryGetValue(containerId, out DepotState existing) && existing != null ? existing : new DepotState();
            state.Depot = container;
            state.Zdo = zdo;
            state.ContainerId = containerId;
            state.OwnerId = ownerId;
            state.WorkAreaId = workAreaId ?? string.Empty;
            state.Counts.Clear();
            try
            {
                List<ItemDrop.ItemData> items = inventory.GetAllItems();
                if (items != null)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        ItemDrop.ItemData item = items[i];
                        if (item == null || item.m_stack <= 0) continue;
                        string prefab = WorkerStorageRuntime.GetPrefabName(item);
                        if (string.IsNullOrWhiteSpace(prefab)) continue;
                        state.Counts.TryGetValue(prefab, out int oldCount);
                        state.Counts[prefab] = SafeAdd(oldCount, item.m_stack);
                    }
                }
            }
            catch { state.Counts.Clear(); }
            DepotStates[containerId] = state;

            PrefabSet.Clear();
            PrefabScratchA.Clear();
            PrefabScratchB.Clear();
            PrefabScratchC.Clear();
            WorkerLogisticsRuntime.CollectRequestedPrefabs(container, PrefabScratchA);
            FieldDepotStockRuleRuntime.CollectConfiguredPrefabs(container, PrefabScratchB);
            WorkerProvisioningLogisticsRuntime.CollectGroundProvisionPrefabs(zdo, PrefabScratchC);
            for (int i = 0; i < PrefabScratchA.Count; i++)
                if (!string.IsNullOrWhiteSpace(PrefabScratchA[i])) PrefabSet.Add(PrefabScratchA[i]);
            for (int i = 0; i < PrefabScratchB.Count; i++)
                if (!string.IsNullOrWhiteSpace(PrefabScratchB[i])) PrefabSet.Add(PrefabScratchB[i]);
            for (int i = 0; i < PrefabScratchC.Count; i++)
                if (!string.IsNullOrWhiteSpace(PrefabScratchC[i])) PrefabSet.Add(PrefabScratchC[i]);

            foreach (string prefab in PrefabSet)
            {
                int requested = WorkerLogisticsRuntime.GetRequestedStock(container, prefab);
                int reserve = FieldDepotStockRuleRuntime.GetTransportReserve(container, prefab);
                int provision = WorkerProvisioningLogisticsRuntime.GetGroundProvisionTarget(zdo, prefab);
                int target = Mathf.Max(provision, Mathf.Max(requested, reserve));
                if (target <= 0) continue;
                string key = BuildKey(containerId, prefab);
                Entries[key] = new DemandEntry
                {
                    Depot = container,
                    Zdo = zdo,
                    ContainerId = containerId,
                    OwnerId = ownerId,
                    Prefab = prefab,
                    WorkAreaId = workAreaId,
                    Target = target
                };
            }

            PrefabSet.Clear();
            PrefabScratchA.Clear();
            PrefabScratchB.Clear();
            PrefabScratchC.Clear();
        }

        private static bool IsLiveState(DepotState state)
        {
            return state != null && state.Depot != null && state.Depot.gameObject != null && state.Depot.gameObject.activeInHierarchy &&
                   WorkerContainerRoleRuntime.GetRole(state.Depot) == WorkerContainerRole.FieldDepot;
        }

        private static int GetStateCount(DepotState state, string prefab)
        {
            if (state == null || string.IsNullOrWhiteSpace(prefab)) return 0;
            string key = WorkerStorageRuntime.NormalizeObjectName(prefab);
            return state.Counts.TryGetValue(key, out int count) ? Mathf.Max(0, count) : 0;
        }

        private static int SafeAdd(int a, int b)
        {
            long value = (long)Mathf.Max(0, a) + Mathf.Max(0, b);
            return value >= int.MaxValue ? int.MaxValue : (int)value;
        }

        private static void RemoveEntriesForContainer(int containerId)
        {
            EntryKeyScratch.Clear();
            foreach (KeyValuePair<string, DemandEntry> pair in Entries)
            {
                DemandEntry entry = pair.Value;
                if (entry != null && entry.ContainerId == containerId) EntryKeyScratch.Add(pair.Key);
            }
            for (int i = 0; i < EntryKeyScratch.Count; i++) Entries.Remove(EntryKeyScratch[i]);
            EntryKeyScratch.Clear();
        }

        private static string BuildKey(int containerId, string prefab)
        {
            return containerId.ToString() + "|" + (prefab ?? string.Empty);
        }
    }
}
