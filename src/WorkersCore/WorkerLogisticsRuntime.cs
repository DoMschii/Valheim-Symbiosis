using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Shared physical logistics layer:
    /// - FieldDepots are local bidirectional buffers with desired stock targets/reserves.
    /// - Sorters move Import -> normal Base storage and normal Base storage -> Export.
    /// - Haulers move FieldDepot surplus -> Base Import and Base Export -> deficient FieldDepot.
    /// All transfers use real Container inventories and Worker ZDO cargo; no item teleportation.
    /// </summary>
    internal static class WorkerLogisticsRuntime
    {
        internal const string RouteHaulInbound = "haul_inbound";
        internal const string RouteHaulOutbound = "haul_outbound";
        internal const string RouteSortImport = "sort_import";
        internal const string RouteSortExport = "sort_export";
        internal const string SupplyRequestsKey = "vw_supply_requests";
        internal const string SupplyAirHubBindingKey = "vw_supply_air_hub_binding";
        // Actual air-supply orders live on the Base AirFreight hub. Outpost FieldDepots only
        // describe local desired stock/delivery targets.
        internal const string BaseAirSupplyOrdersKey = "vw_base_air_supply_orders";
        internal const int DefaultFieldSupplyTarget = 20;

        private static readonly List<ZDO> CachedBaseHubLookup = new List<ZDO>(8);
        private static readonly List<SharedStorageStockIndexRuntime.CachedItemSource> ImportSourceScratch =
            new List<SharedStorageStockIndexRuntime.CachedItemSource>(32);
        private static readonly List<ZDO> WorkAreaDepotScratch = new List<ZDO>(8);
        private sealed class TransportScopeCacheEntry
        {
            internal string WorkAreaId = string.Empty;
            internal bool IsBase;
            internal float ExpiresAt;
        }
        private static readonly Dictionary<ZDOID, TransportScopeCacheEntry> TransportScopeCache =
            new Dictionary<ZDOID, TransportScopeCacheEntry>();
        private static float _nextBaseHubLookupRefresh;

        internal readonly struct SupplyRequest
        {
            internal readonly Container Depot;
            internal readonly string Prefab;
            internal readonly int TargetStock;
            internal readonly int CurrentStock;
            internal readonly int Need;
            internal readonly Vector3 Position;
            internal readonly ZDOID DepotId;
            internal readonly string WorkAreaId;
            internal readonly bool WorkAreaPooled;

            internal SupplyRequest(Container depot, string prefab, int targetStock, int currentStock, string workAreaId)
            {
                Depot = depot;
                Prefab = prefab ?? string.Empty;
                TargetStock = Mathf.Max(1, targetStock);
                CurrentStock = Mathf.Max(0, currentStock);
                Need = Mathf.Max(0, TargetStock - CurrentStock);
                Position = depot != null ? depot.transform.position : default;
                ZDO depotZdo = depot != null ? WorkerContainerRoleRuntime.GetZdo(depot) : null;
                DepotId = depotZdo?.m_uid ?? ZDOID.None;
                WorkAreaId = workAreaId ?? string.Empty;
                WorkAreaPooled = false;
            }

            internal SupplyRequest(string workAreaId, Vector3 workAreaPosition, string prefab, int targetStock, int currentStock)
            {
                Depot = null;
                Prefab = prefab ?? string.Empty;
                TargetStock = Mathf.Max(1, targetStock);
                CurrentStock = Mathf.Max(0, currentStock);
                Need = Mathf.Max(0, TargetStock - CurrentStock);
                Position = workAreaPosition;
                DepotId = ZDOID.None;
                WorkAreaId = workAreaId ?? string.Empty;
                WorkAreaPooled = !string.IsNullOrEmpty(WorkAreaId);
            }

            internal SupplyRequest(ZDOID depotId, Vector3 depotPosition, string prefab, int targetStock, int currentStock)
            {
                Depot = null;
                Prefab = prefab ?? string.Empty;
                TargetStock = Mathf.Max(1, targetStock);
                CurrentStock = Mathf.Max(0, currentStock);
                Need = Mathf.Max(0, TargetStock - CurrentStock);
                Position = depotPosition;
                DepotId = depotId;
                WorkAreaId = string.Empty;
                WorkAreaPooled = false;
            }
        }

        internal static bool RequestSupply(Container depot, string prefab, int targetStock)
        {
            if (depot == null || string.IsNullOrWhiteSpace(prefab) || targetStock <= 0) return false;
            if (WorkerContainerRoleRuntime.GetRole(depot) != WorkerContainerRole.FieldDepot) return false;
            if (!WorkerStorageRuntime.TryOwn(depot)) return false;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            if (zdo == null) return false;

            Dictionary<string, int> requests = ParseRequests(zdo.GetString(SupplyRequestsKey, string.Empty));
            bool changed = !requests.TryGetValue(prefab, out int existing) || targetStock > existing;
            if (changed)
                requests[prefab] = targetStock;
            if (changed)
            {
                zdo.Set(SupplyRequestsKey, SerializeRequests(requests));
                PersistentContainerRegistryRuntime.Remember(zdo);
                BackgroundWorkerScheduler.RequestPersistentLogisticsRescan();
                PublishCurrentAirNeedToBase(zdo, prefab);
            }
            FieldSupplyDemandCacheRuntime.NotifyRequestChanged(zdo);
            // Legacy mirror cleanup now uses only cached Outpost hubs, so it is safe even when a
            // background producer repeats the same target request.
            RemoveLegacyMirroredSupplyRequest(zdo, prefab, targetStock);
            return true;
        }

        // Background workers cannot rely on a loaded Container component. The FieldDepot ZDO is
        // the durable logistics endpoint, so unloaded Garden/Forestry jobs write the exact same
        // supply-request manifest directly to that persistent endpoint.
        internal static bool RequestSupply(ZDO depotZdo, string prefab, int targetStock)
        {
            if (depotZdo == null || string.IsNullOrWhiteSpace(prefab) || targetStock <= 0) return false;
            WorkerContainerRole role = (WorkerContainerRole)Mathf.Clamp(depotZdo.GetInt(WorkerContainerRoleRuntime.RoleKey, 0), 0, 3);
            if (role != WorkerContainerRole.FieldDepot) return false;
            Dictionary<string, int> requests = ParseRequests(depotZdo.GetString(SupplyRequestsKey, string.Empty));
            bool changed = !requests.TryGetValue(prefab, out int existing) || targetStock > existing;
            if (changed)
                requests[prefab] = targetStock;
            if (changed)
            {
                depotZdo.Set(SupplyRequestsKey, SerializeRequests(requests));
                PersistentContainerRegistryRuntime.Remember(depotZdo);
                BackgroundWorkerScheduler.RequestPersistentLogisticsRescan();
                PublishCurrentAirNeedToBase(depotZdo, prefab);
            }
            FieldSupplyDemandCacheRuntime.NotifyRequestChanged(depotZdo);
            // Keep this request on this exact persistent container only.
            RemoveLegacyMirroredSupplyRequest(depotZdo, prefab, targetStock);
            return true;
        }

        private static void RemoveLegacyMirroredSupplyRequest(ZDO sourceDepotZdo, string prefab, int targetStock)
        {
            if (sourceDepotZdo == null || string.IsNullOrWhiteSpace(prefab) || targetStock <= 0) return;
            if (sourceDepotZdo.GetBool(WorkerContainerRoleRuntime.AirTransportKey, false) &&
                (AirDepotMode)Mathf.Clamp(sourceDepotZdo.GetInt(WorkerContainerRoleRuntime.AirDepotModeKey, 0), 0, 1) == AirDepotMode.Outpost)
                return;

            long sourceOwner = sourceDepotZdo.GetLong(WorkerContainerRoleRuntime.OwnerKey, 0L);
            Vector3 sourcePosition = sourceDepotZdo.GetPosition();
            const float LegacyMirrorDistance = 120f;
            float maxSqr = LegacyMirrorDistance * LegacyMirrorDistance;
            float bestSqr = float.MaxValue;
            ZDO best = null;

            // Legacy cleanup is not allowed to turn every repeated RequestSupply() call into a
            // persistent-registry walk. The topology cache contains the same Outpost Air hubs.
            WorkerProvisioningLogisticsRuntime.ForEachCachedOutpostAirHub((candidate, owner, position) =>
            {
                if (candidate == null || candidate == sourceDepotZdo) return;
                if (owner != 0L && sourceOwner != 0L && owner != sourceOwner) return;
                float sqr = (position - sourcePosition).sqrMagnitude;
                if (sqr > maxSqr || sqr >= bestSqr) return;
                bestSqr = sqr;
                best = candidate;
            });

            if (best == null) return;
            Dictionary<string, int> requests = ParseRequests(best.GetString(SupplyRequestsKey, string.Empty));
            if (!requests.TryGetValue(prefab, out int mirrored) || mirrored != targetStock) return;
            requests.Remove(prefab);
            best.Set(SupplyRequestsKey, SerializeRequests(requests));
            PersistentContainerRegistryRuntime.Remember(best);
            FieldSupplyDemandCacheRuntime.NotifyRequestChanged(best);
            BackgroundMonitorRuntime.Note($"field-policy: removed legacy mirrored {prefab} x{targetStock} from neighbouring AirDepot", false);
        }

        // Writes local desired stock to the worker's assigned outpost. The outpost state is only
        // the delivery target. RequestSupply() then publishes the actual air-supply order onto a
        // persistent Base AirFreight hub, where the Base sorter can see it while the outpost is unloaded.
        internal static bool RequestSupplyForWorkArea(ZDO workerZdo, string prefab, int targetStock)
        {
            if (workerZdo == null || string.IsNullOrWhiteSpace(prefab) || targetStock <= 0) return false;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius)) return false;

            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            string workerWorkAreaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            float allowed = Mathf.Max(4f, radius) + 15f;
            float allowedSqr = allowed * allowed;

            // v0.5.386: a Workzone is one logical FieldDepot pool, therefore one resource must
            // have exactly one authoritative request manifest inside that pool. Older code picked
            // the nearest cached depot and left stale copies on siblings. The demand cache then
            // either summed duplicate targets or, worse, a later clear removed the wrong chest's
            // request and the Sorter stopped delivering Thistle/Mushrooms entirely.
            ZDO bestExact = null;
            float bestExactSqr = float.MaxValue;
            ZDO bestUnassigned = null;
            float bestUnassignedSqr = float.MaxValue;
            WorkAreaDepotScratch.Clear();

            WorkerProvisioningLogisticsRuntime.ForEachCachedAnyFieldDepot((candidate, owner, position) =>
            {
                if (candidate == null) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                float dx = position.x - center.x;
                float dz = position.z - center.z;
                float sqr = dx * dx + dz * dz;
                if (sqr > allowedSqr) return;

                string depotWorkAreaId = FieldDepotWorkzoneRuntime.GetAssignedWorkAreaId(candidate);
                if (!string.IsNullOrEmpty(workerWorkAreaId) &&
                    string.Equals(workerWorkAreaId, depotWorkAreaId, StringComparison.Ordinal))
                {
                    WorkAreaDepotScratch.Add(candidate);
                    if (sqr < bestExactSqr)
                    {
                        bestExactSqr = sqr;
                        bestExact = candidate;
                    }
                    return;
                }

                // Compatibility only for old/unassigned depots. Never prefer one over an explicit
                // Workzone member, and never borrow a depot explicitly assigned to another zone.
                if (!string.IsNullOrEmpty(depotWorkAreaId)) return;
                if (sqr < bestUnassignedSqr)
                {
                    bestUnassignedSqr = sqr;
                    bestUnassigned = candidate;
                }
            });

            ZDO selected = bestExact ?? bestUnassigned;
            if (selected == null) return false;

            bool cleanedStale = false;
            if (bestExact != null)
            {
                // Remove same-resource manifests from sibling depots in the same pool. Their stock
                // still counts toward the pooled target; only the policy/request is canonicalized.
                for (int i = 0; i < WorkAreaDepotScratch.Count; i++)
                {
                    ZDO member = WorkAreaDepotScratch[i];
                    if (member == null || member == selected) continue;
                    Dictionary<string, int> siblingRequests = ParseRequests(member.GetString(SupplyRequestsKey, string.Empty));
                    if (!siblingRequests.Remove(prefab)) continue;
                    member.Set(SupplyRequestsKey, SerializeRequests(siblingRequests));
                    if (siblingRequests.Count == 0) member.Set(SupplyAirHubBindingKey, string.Empty);
                    PersistentContainerRegistryRuntime.Remember(member);
                    FieldSupplyDemandCacheRuntime.NotifyRequestChanged(member);
                    cleanedStale = true;
                }
            }

            int previousTarget = GetRequestedStock(selected, prefab);
            string previousBinding = selected.GetString(SupplyAirHubBindingKey, string.Empty);
            bool wrote = RequestSupply(selected, prefab, targetStock);

            if (wrote)
            {
                string binding = string.Empty;
                if (TryFindNearestOutpostAirHub(ownerId, center, allowed, out ZDO boundHub))
                    binding = boundHub.m_uid.ToString();
                if (!string.Equals(previousBinding, binding, StringComparison.Ordinal))
                {
                    selected.Set(SupplyAirHubBindingKey, binding);
                    PersistentContainerRegistryRuntime.Remember(selected);
                }

                // Repeated background Garden/Forestry checks often ask for the same target. Only a
                // real policy/binding/canonicalization change needs an immediate manifest rebuild;
                // stock changes are picked up by the cheap cached AirFreight service pass.
                bool policyChanged = targetStock > previousTarget ||
                                     !string.Equals(previousBinding, binding, StringComparison.Ordinal) ||
                                     cleanedStale;
                if (policyChanged)
                {
                    int outstanding = AirHaulerWorkRuntime.GetOutpostAirOutstandingNeed(ownerId, prefab);
                    PublishBaseAirSupplyOrder(ownerId, center, prefab, outstanding);
                    SyncBaseAirSupplyOrdersFromOutposts(ownerId, center);
                    BackgroundWorkerScheduler.RequestPersistentLogisticsRescan();
                }
            }
            WorkAreaDepotScratch.Clear();
            return wrote;
        }


        /// <summary>
        /// v0.5.388: Reconciles the complete dynamic WildGrowth desired-stock set for one Workzone.
        /// Unlike RequestSupplyForWorkArea(), this is an exact snapshot: targets may shrink as
        /// persistent mushroom/thistle/dandelion/bush slots are completed, and stale WildGrowth
        /// requests disappear once no matching plan remains. Other (non-WildGrowth) requests and
        /// manual FieldDepot stock rules are preserved. The reconciliation is driven only by the
        /// already-built Garden pending-slot list/background handoff; it never scans the world.
        /// </summary>
        internal static bool SyncWildGrowthSupplyForWorkArea(ZDO workerZdo, IDictionary<string, int> desiredTargets)
        {
            if (workerZdo == null) return false;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius)) return false;

            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            string workerWorkAreaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            float allowed = Mathf.Max(4f, radius) + 15f;
            float allowedSqr = allowed * allowed;

            ZDO bestExact = null;
            float bestExactSqr = float.MaxValue;
            ZDO bestUnassigned = null;
            float bestUnassignedSqr = float.MaxValue;
            WorkAreaDepotScratch.Clear();

            WorkerProvisioningLogisticsRuntime.ForEachCachedAnyFieldDepot((candidate, owner, position) =>
            {
                if (candidate == null) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                float dx = position.x - center.x;
                float dz = position.z - center.z;
                float sqr = dx * dx + dz * dz;
                if (sqr > allowedSqr) return;

                string depotWorkAreaId = FieldDepotWorkzoneRuntime.GetAssignedWorkAreaId(candidate);
                if (!string.IsNullOrEmpty(workerWorkAreaId) &&
                    string.Equals(workerWorkAreaId, depotWorkAreaId, StringComparison.Ordinal))
                {
                    WorkAreaDepotScratch.Add(candidate);
                    if (sqr < bestExactSqr)
                    {
                        bestExactSqr = sqr;
                        bestExact = candidate;
                    }
                    return;
                }

                if (!string.IsNullOrEmpty(depotWorkAreaId)) return;
                if (sqr < bestUnassignedSqr)
                {
                    bestUnassignedSqr = sqr;
                    bestUnassigned = candidate;
                }
            });

            ZDO selected = bestExact ?? bestUnassigned;
            if (selected == null)
            {
                WorkAreaDepotScratch.Clear();
                return false;
            }

            bool changed = false;
            // One Workzone pool owns one canonical request manifest. Remove old WildGrowth entries
            // from sibling depots so Sorter/Hauler demand can never be split across several chests.
            if (bestExact != null)
            {
                for (int i = 0; i < WorkAreaDepotScratch.Count; i++)
                {
                    ZDO member = WorkAreaDepotScratch[i];
                    if (member == null || member == selected) continue;
                    Dictionary<string, int> sibling = ParseRequests(member.GetString(SupplyRequestsKey, string.Empty));
                    List<string> remove = new List<string>();
                    foreach (KeyValuePair<string, int> pair in sibling)
                    {
                        if (!WorkersWildFloraRuntime.TryGetDefinitionByResource(pair.Key, out _)) continue;
                        remove.Add(pair.Key);
                    }
                    if (remove.Count == 0) continue;
                    for (int n = 0; n < remove.Count; n++)
                    {
                        sibling.Remove(remove[n]);
                    }
                    member.Set(SupplyRequestsKey, SerializeRequests(sibling));
                    if (sibling.Count == 0) member.Set(SupplyAirHubBindingKey, string.Empty);
                    PersistentContainerRegistryRuntime.Remember(member);
                    FieldSupplyDemandCacheRuntime.NotifyRequestChanged(member);
                    changed = true;
                }
            }

            Dictionary<string, int> requests = ParseRequests(selected.GetString(SupplyRequestsKey, string.Empty));
            List<string> stale = new List<string>();
            foreach (KeyValuePair<string, int> pair in requests)
            {
                if (!WorkersWildFloraRuntime.TryGetDefinitionByResource(pair.Key, out _)) continue;
                int desired = 0;
                if (desiredTargets != null && desiredTargets.TryGetValue(pair.Key, out int raw)) desired = Mathf.Max(0, raw);
                if (desired <= 0) stale.Add(pair.Key);
            }
            for (int i = 0; i < stale.Count; i++)
            {
                requests.Remove(stale[i]);
                changed = true;
            }

            if (desiredTargets != null)
            {
                foreach (KeyValuePair<string, int> pair in desiredTargets)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value <= 0) continue;
                    if (!WorkersWildFloraRuntime.TryGetDefinitionByResource(pair.Key, out _)) continue;
                    int target = Mathf.Max(1, pair.Value);
                        if (!requests.TryGetValue(pair.Key, out int existing) || existing != target)
                    {
                        requests[pair.Key] = target;
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                selected.Set(SupplyRequestsKey, SerializeRequests(requests));
                if (requests.Count == 0) selected.Set(SupplyAirHubBindingKey, string.Empty);
                else
                {
                    string binding = string.Empty;
                    if (TryFindNearestOutpostAirHub(ownerId, center, allowed, out ZDO boundHub))
                        binding = boundHub.m_uid.ToString();
                    selected.Set(SupplyAirHubBindingKey, binding);
                }
                PersistentContainerRegistryRuntime.Remember(selected);
                FieldSupplyDemandCacheRuntime.NotifyRequestChanged(selected);

                // Rebuild once for the complete exact snapshot instead of creating one rescan per
                // completed plant. This is the important performance property of the v0.5.388 path.
                SyncBaseAirSupplyOrdersFromOutposts(ownerId, center);
                BackgroundWorkerScheduler.RequestPersistentLogisticsRescan();
            }

            WorkAreaDepotScratch.Clear();
            return true;
        }

        private static bool TryFindNearestOutpostAirHub(long ownerId, Vector3 center, float maxDistance, out ZDO hub)
        {
            ZDO best = null;
            float clampedDistance = Mathf.Max(4f, maxDistance);
            float bestSqr = clampedDistance * clampedDistance;
            WorkerProvisioningLogisticsRuntime.ForEachCachedOutpostAirHub((candidate, owner, position) =>
            {
                if (candidate == null) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                float sqr = (position - center).sqrMagnitude;
                if (sqr > bestSqr) return;
                bestSqr = sqr;
                best = candidate;
            });
            // Rare creation-time fallback: a newly placed AirFreight chest can be used before the
            // scheduler has consumed the topology-dirty event. This is never the 4-second hot path.
            if (best == null)
            {
                PersistentContainerRegistryRuntime.ForEachPersistent((candidate, role, owner, position) =>
                {
                    if (candidate == null || role != WorkerContainerRole.FieldDepot) return;
                    if (!candidate.GetBool(WorkerContainerRoleRuntime.AirTransportKey, false)) return;
                    if ((AirDepotMode)Mathf.Clamp(candidate.GetInt(WorkerContainerRoleRuntime.AirDepotModeKey, 0), 0, 1) != AirDepotMode.Outpost) return;
                    if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                    float sqr = (position - center).sqrMagnitude;
                    if (sqr > bestSqr) return;
                    bestSqr = sqr;
                    best = candidate;
                });
            }
            hub = best;
            return hub != null;
        }

        internal static bool IsSupplyBoundToAirHub(ZDO depotZdo, ZDO outpostHubZdo)
        {
            if (depotZdo == null || outpostHubZdo == null) return false;
            string binding = depotZdo.GetString(SupplyAirHubBindingKey, string.Empty);
            return !string.IsNullOrEmpty(binding) &&
                   string.Equals(binding, outpostHubZdo.m_uid.ToString(), StringComparison.Ordinal);
        }

        /// <summary>
        /// Removes a stale desired-stock request for one prefab from the worker's assigned outpost.
        /// This is used when a Gardener can recover seeds locally: local seed production must win
        /// over an older Base/AirFreight request. Only FieldDepots inside the assigned WorkArea are
        /// touched, then the central Base air manifest is rebuilt from the remaining real requests.
        /// </summary>
        internal static bool ClearSupplyRequestForWorkArea(ZDO workerZdo, string prefab)
        {
            if (workerZdo == null || string.IsNullOrWhiteSpace(prefab)) return false;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius)) return false;

            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            string workAreaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            float allowed = Mathf.Max(4f, radius) + 15f;
            float allowedSqr = allowed * allowed;
            bool changed = false;
            bool foundExactMember = false;
            ZDO nearestUnassigned = null;
            float nearestUnassignedSqr = float.MaxValue;

            // v0.5.386: clear the resource policy from the whole logical Workzone pool, not from
            // whichever chest happens to be nearest the flag. This both removes legacy duplicates
            // and prevents a neighbouring/unassigned depot from stealing another zone's request.
            WorkerProvisioningLogisticsRuntime.ForEachCachedAnyFieldDepot((candidate, owner, position) =>
            {
                if (candidate == null) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                float dx = position.x - center.x;
                float dz = position.z - center.z;
                float sqr = dx * dx + dz * dz;
                if (sqr > allowedSqr) return;

                string depotWorkAreaId = FieldDepotWorkzoneRuntime.GetAssignedWorkAreaId(candidate);
                bool exact = !string.IsNullOrEmpty(workAreaId) &&
                             string.Equals(workAreaId, depotWorkAreaId, StringComparison.Ordinal);
                if (exact)
                {
                    foundExactMember = true;
                    Dictionary<string, int> requests = ParseRequests(candidate.GetString(SupplyRequestsKey, string.Empty));
                    if (!requests.Remove(prefab)) return;
                    candidate.Set(SupplyRequestsKey, SerializeRequests(requests));
                    if (requests.Count == 0) candidate.Set(SupplyAirHubBindingKey, string.Empty);
                    PersistentContainerRegistryRuntime.Remember(candidate);
                    FieldSupplyDemandCacheRuntime.NotifyRequestChanged(candidate);
                    changed = true;
                    return;
                }

                if (!string.IsNullOrEmpty(depotWorkAreaId)) return;
                if (sqr < nearestUnassignedSqr)
                {
                    nearestUnassignedSqr = sqr;
                    nearestUnassigned = candidate;
                }
            });

            // Legacy fallback only when the Workzone has no explicitly assigned depot at all.
            if (!foundExactMember && nearestUnassigned != null)
            {
                Dictionary<string, int> requests = ParseRequests(nearestUnassigned.GetString(SupplyRequestsKey, string.Empty));
                if (requests.Remove(prefab))
                {
                    nearestUnassigned.Set(SupplyRequestsKey, SerializeRequests(requests));
                    if (requests.Count == 0) nearestUnassigned.Set(SupplyAirHubBindingKey, string.Empty);
                    PersistentContainerRegistryRuntime.Remember(nearestUnassigned);
                    FieldSupplyDemandCacheRuntime.NotifyRequestChanged(nearestUnassigned);
                    changed = true;
                }
            }

            if (changed)
            {
                SyncBaseAirSupplyOrdersFromOutposts(ownerId, center);
                BackgroundWorkerScheduler.RequestPersistentLogisticsRescan();
            }
            return changed;
        }

        private static void PublishCurrentAirNeedToBase(ZDO sourceDepotZdo, string prefab)
        {
            if (sourceDepotZdo == null || string.IsNullOrWhiteSpace(prefab)) return;
            long ownerId = sourceDepotZdo.GetLong(WorkerContainerRoleRuntime.OwnerKey, 0L);
            int outstanding = AirHaulerWorkRuntime.GetOutpostAirOutstandingNeed(ownerId, prefab);
            PublishBaseAirSupplyOrder(ownerId, sourceDepotZdo.GetPosition(), prefab, outstanding);
        }

        private static bool PublishBaseAirSupplyOrder(long ownerId, Vector3 requestOrigin, string prefab, int amount)
        {
            if (string.IsNullOrWhiteSpace(prefab)) return false;
            ZDO selected = null;
            float bestSqr = float.MaxValue;
            List<ZDO> hubs = new List<ZDO>();

            WorkerProvisioningLogisticsRuntime.ForEachCachedBaseAirHub((candidate, owner, position) =>
            {
                if (candidate == null) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                hubs.Add(candidate);
                float sqr = (position - requestOrigin).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                selected = candidate;
            });

            // Same rare creation-time fallback as the Outpost binding above. Normal scheduler
            // staging never enters this registry scan.
            if (hubs.Count == 0)
            {
                PersistentContainerRegistryRuntime.ForEachPersistent((candidate, role, owner, position) =>
                {
                    if (candidate == null || role != WorkerContainerRole.FieldDepot) return;
                    if (!candidate.GetBool(WorkerContainerRoleRuntime.AirTransportKey, false)) return;
                    if ((AirDepotMode)Mathf.Clamp(candidate.GetInt(WorkerContainerRoleRuntime.AirDepotModeKey, 0), 0, 1) != AirDepotMode.BaseHub) return;
                    if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                    hubs.Add(candidate);
                    float sqr = (position - requestOrigin).sqrMagnitude;
                    if (sqr >= bestSqr) return;
                    bestSqr = sqr;
                    selected = candidate;
                });
            }

            if (hubs.Count == 0)
            {
                WorkerProvisioningLogisticsRuntime.RequestPersistentProvisionAudit();
                PersistentContainerRegistryRuntime.RequestPersistentRescan();
                return false;
            }

            bool newlyActivated = false;
            for (int i = 0; i < hubs.Count; i++)
            {
                ZDO currentHub = hubs[i];
                Dictionary<string, int> orders = ParseRequests(currentHub.GetString(BaseAirSupplyOrdersKey, string.Empty));
                int previous = orders.TryGetValue(prefab, out int oldAmount) ? Mathf.Max(0, oldAmount) : 0;
                if (currentHub == selected && amount > 0)
                {
                    orders[prefab] = amount;
                    if (previous <= 0) newlyActivated = true;
                }
                else orders.Remove(prefab);
                currentHub.Set(BaseAirSupplyOrdersKey, SerializeRequests(orders));
                PersistentContainerRegistryRuntime.Remember(currentHub);
            }

            if (newlyActivated)
                BackgroundWorkerScheduler.RequestPersistentLogisticsRescan();

            return selected != null && amount > 0;
        }

        // Rebuild the Base-side air order manifest from persistent outpost desired-stock state.
        // This is intentionally idempotent and can be called by both loaded and background Sorters.
        // It closes the gap where an outpost writes its request while the Base scene is unloaded.
        internal static bool SyncBaseAirSupplyOrdersFromOutposts(long ownerId, Vector3 requestOrigin)
        {
            List<string> demandPrefabs = new List<string>(32);
            AirHaulerWorkRuntime.CollectOutpostAirDemandPrefabs(ownerId, demandPrefabs);

            ZDO selected = null;
            float bestSqr = float.MaxValue;
            List<ZDO> hubs = new List<ZDO>(8);
            WorkerProvisioningLogisticsRuntime.ForEachCachedBaseAirHub((candidate, owner, position) =>
            {
                if (candidate == null) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                hubs.Add(candidate);
                float sqr = (position - requestOrigin).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                selected = candidate;
            });
            if (selected == null) return false;

            Dictionary<string, int> rebuilt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < demandPrefabs.Count; i++)
            {
                string demandPrefab = demandPrefabs[i];
                int need = AirHaulerWorkRuntime.GetOutpostAirOutstandingNeed(ownerId, demandPrefab);
                if (need > 0) rebuilt[demandPrefab] = need;
            }

            string manifest = SerializeRequests(rebuilt);
            for (int i = 0; i < hubs.Count; i++)
            {
                ZDO currentHub = hubs[i];
                currentHub.Set(BaseAirSupplyOrdersKey, currentHub == selected ? manifest : string.Empty);
                PersistentContainerRegistryRuntime.Remember(currentHub);
            }
            return rebuilt.Count > 0;
        }

        internal static int GetBaseAirSupplyOrder(ZDO baseHubZdo, string prefab)
        {
            if (baseHubZdo == null || string.IsNullOrEmpty(prefab)) return 0;
            Dictionary<string, int> orders = ParseRequests(baseHubZdo.GetString(BaseAirSupplyOrdersKey, string.Empty));
            return orders.TryGetValue(prefab, out int amount) ? Mathf.Max(0, amount) : 0;
        }

        internal static int GetBaseAirSupplyOrder(long ownerId, string prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return 0;
            int best = 0;
            WorkerProvisioningLogisticsRuntime.ForEachCachedBaseAirHub((zdo, owner, position) =>
            {
                if (zdo == null) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                best = Mathf.Max(best, GetBaseAirSupplyOrder(zdo, prefab));
            });
            return best;
        }

        internal static void CollectBaseAirSupplyOrderPrefabs(ZDO baseHubZdo, List<string> target)
        {
            if (target == null) return;
            target.Clear();
            if (baseHubZdo == null) return;
            Dictionary<string, int> orders = ParseRequests(baseHubZdo.GetString(BaseAirSupplyOrdersKey, string.Empty));
            foreach (string prefab in orders.Keys)
                if (!string.IsNullOrWhiteSpace(prefab)) target.Add(prefab);
        }

        internal static bool TryFindBaseAirStagingOrder(long ownerId, Vector3 baseOrigin, out ZDO baseHubZdo, out Vector3 baseHubPosition, out string prefab, out int missing)
        {
            baseHubZdo = null;
            baseHubPosition = default;
            prefab = string.Empty;
            missing = 0;

            // The manifest is refreshed from cached endpoint/request topology. No persistent
            // container discovery scan is allowed on this four-second staging path.
            SyncBaseAirSupplyOrdersFromOutposts(ownerId, baseOrigin);

            ZDO selectedHub = null;
            Vector3 selectedHubPosition = default;
            float bestHubSqr = float.MaxValue;
            WorkerProvisioningLogisticsRuntime.ForEachCachedBaseAirHub((candidate, owner, position) =>
            {
                if (candidate == null) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                float sqr = (position - baseOrigin).sqrMagnitude;
                if (sqr >= bestHubSqr) return;
                bestHubSqr = sqr;
                selectedHub = candidate;
                selectedHubPosition = position;
            });
            if (selectedHub == null) return false;

            Dictionary<string, int> orders = ParseRequests(selectedHub.GetString(BaseAirSupplyOrdersKey, string.Empty));
            string bestPrefab = string.Empty;
            int bestMissing = 0;
            foreach (KeyValuePair<string, int> pair in orders)
            {
                string candidatePrefab = pair.Key;
                int outstanding = Mathf.Max(0, pair.Value);
                if (string.IsNullOrWhiteSpace(candidatePrefab) || outstanding <= 0) continue;

                int staged = 0;
                ContainerLedgerRuntime.TryGetCount(selectedHub, candidatePrefab, out staged);
                staged += AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(selectedHub, candidatePrefab);
                int need = Mathf.Max(0, outstanding - staged);
                if (need <= bestMissing) continue;
                bestPrefab = candidatePrefab;
                bestMissing = need;
            }

            if (bestMissing <= 0 || string.IsNullOrEmpty(bestPrefab)) return false;

            baseHubZdo = selectedHub;
            baseHubPosition = selectedHubPosition;
            prefab = bestPrefab;
            missing = bestMissing;
            return true;
        }

        internal static bool TryFindPersistentAirDepot(Vector3 near, long ownerId, AirDepotMode mode, out ZDO depotZdo, out Vector3 position)
        {
            ZDO found = null;
            Vector3 foundPosition = default;
            float best = float.MaxValue;
            Action<ZDO, long, Vector3> consider = (candidate, owner, p) =>
            {
                if (candidate == null) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                float sqr = (p - near).sqrMagnitude;
                if (sqr >= best) return;
                best = sqr;
                found = candidate;
                foundPosition = p;
            };

            if (mode == AirDepotMode.BaseHub)
                WorkerProvisioningLogisticsRuntime.ForEachCachedBaseAirHub(consider);
            else
                WorkerProvisioningLogisticsRuntime.ForEachCachedOutpostAirHub(consider);

            depotZdo = found;
            position = foundPosition;
            return found != null;
        }

        internal static bool TryGetBaseAirHubOwnerNear(Vector3 position, float radius, out long ownerId)
        {
            long foundOwner = 0L;
            float best = Mathf.Max(1f, radius) * Mathf.Max(1f, radius);
            bool found = false;

            // v0.5.250: Container.Awake can run for 100+ Base chests almost at once. Iterating the
            // full remembered-container dictionary for every chest made Base entry effectively
            // O(chests²). Cache the handful of Base Air hubs and refresh that tiny list at most
            // once per second while the Base is loading.
            float now = Time.realtimeSinceStartup;
            if (now >= _nextBaseHubLookupRefresh || CachedBaseHubLookup.Count == 0)
            {
                _nextBaseHubLookupRefresh = now + 1f;
                CachedBaseHubLookup.Clear();
                PersistentContainerRegistryRuntime.ForEachRemembered((candidate, role, owner, p) =>
                {
                    if (candidate == null || role != WorkerContainerRole.FieldDepot) return;
                    if (!candidate.GetBool(WorkerContainerRoleRuntime.AirTransportKey, false)) return;
                    if ((AirDepotMode)Mathf.Clamp(candidate.GetInt(WorkerContainerRoleRuntime.AirDepotModeKey, 0), 0, 1) != AirDepotMode.BaseHub) return;
                    CachedBaseHubLookup.Add(candidate);
                });
            }

            for (int i = CachedBaseHubLookup.Count - 1; i >= 0; i--)
            {
                ZDO candidate = CachedBaseHubLookup[i];
                if (candidate == null)
                {
                    CachedBaseHubLookup.RemoveAt(i);
                    continue;
                }
                if (!candidate.GetBool(WorkerContainerRoleRuntime.AirTransportKey, false) ||
                    (AirDepotMode)Mathf.Clamp(candidate.GetInt(WorkerContainerRoleRuntime.AirDepotModeKey, 0), 0, 1) != AirDepotMode.BaseHub)
                    continue;
                Vector3 p = candidate.GetPosition();
                float sqr = (p - position).sqrMagnitude;
                if (sqr > best) continue;
                best = sqr;
                foundOwner = candidate.GetLong(WorkerContainerRoleRuntime.OwnerKey, 0L);
                found = true;
            }

            ownerId = foundOwner;
            return found;
        }

        internal static int GetRequestedStock(ZDO depotZdo, string prefab)
        {
            if (depotZdo == null || string.IsNullOrEmpty(prefab)) return 0;
            Dictionary<string, int> requests = ParseRequests(depotZdo.GetString(SupplyRequestsKey, string.Empty));
            return requests.TryGetValue(prefab, out int target) ? Mathf.Max(0, target) : 0;
        }

        internal static void CollectRequestedPrefabs(ZDO depotZdo, List<string> target)
        {
            if (target == null) return;
            target.Clear();
            if (depotZdo == null) return;
            Dictionary<string, int> requests = ParseRequests(depotZdo.GetString(SupplyRequestsKey, string.Empty));
            foreach (string prefab in requests.Keys)
                if (!string.IsNullOrWhiteSpace(prefab)) target.Add(prefab);
        }

        internal static bool TryFindPersistentFieldDepotForWorkArea(ZDO workerZdo, out ZDO depotZdo, out Vector3 position)
        {
            ZDO found = null;
            Vector3 foundPosition = default;
            if (workerZdo == null || !WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius))
            {
                depotZdo = null;
                position = default;
                return false;
            }
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            float allowed = Mathf.Max(4f, radius) + 2f;
            float allowedSqr = allowed * allowed;
            float best = float.MaxValue;
            WorkerProvisioningLogisticsRuntime.ForEachCachedAnyFieldDepot((zdo, owner, p) =>
            {
                if (zdo == null || (owner != 0L && ownerId != 0L && owner != ownerId)) return;
                if (!FieldDepotWorkzoneRuntime.IsCompatibleWithWorkerWorkArea(zdo, workerZdo)) return;
                float dx = p.x - center.x;
                float dz = p.z - center.z;
                float areaSqr = dx * dx + dz * dz;
                if (areaSqr > allowedSqr || areaSqr >= best) return;
                best = areaSqr;
                found = zdo;
                foundPosition = p;
            });
            depotZdo = found;
            position = foundPosition;
            return found != null;
        }

        internal static int GetRequestedStock(Container depot, string prefab)
        {
            if (depot == null || string.IsNullOrEmpty(prefab)) return 0;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            if (zdo == null) return 0;
            Dictionary<string, int> requests = ParseRequests(zdo.GetString(SupplyRequestsKey, string.Empty));
            return requests.TryGetValue(prefab, out int target) ? Mathf.Max(0, target) : 0;
        }

        internal static int GetEffectiveFieldDepotTarget(ZDO depotZdo, string prefab)
        {
            if (depotZdo == null || string.IsNullOrWhiteSpace(prefab)) return 0;
            int requested = GetRequestedStock(depotZdo, prefab);
            int reserve = FieldDepotStockRuleRuntime.GetTransportReserve(depotZdo, prefab);
            int provisioning = WorkerProvisioningLogisticsRuntime.GetGroundProvisionTarget(depotZdo, prefab);
            return Mathf.Max(provisioning, Mathf.Max(requested, reserve));
        }

        internal static int GetEffectiveFieldDepotTarget(Container depot, string prefab)
        {
            if (depot == null || string.IsNullOrWhiteSpace(prefab)) return 0;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            if (zdo == null) return 0;
            return GetEffectiveFieldDepotTarget(zdo, prefab);
        }

        /// <summary>
        /// A ground Transporter assigned to a WorkArea containing Base Import/Export endpoints is a
        /// Base transporter. It may serve ground demand from every other Outpost. A Transporter in
        /// an Outpost WorkArea remains scoped to that one WorkArea. Classification is cached so the
        /// normal AI decision path never repeatedly walks the persistent endpoint registry.
        /// </summary>
        internal static bool IsBaseTransporter(ZDO workerZdo)
        {
            if (workerZdo == null || !WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius)) return false;

            string workAreaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrWhiteSpace(workAreaId)) return false;
            float now = Time.realtimeSinceStartup;
            if (TransportScopeCache.TryGetValue(workerZdo.m_uid, out TransportScopeCacheEntry cached) && cached != null &&
                string.Equals(cached.WorkAreaId, workAreaId, StringComparison.Ordinal) && now < cached.ExpiresAt)
                return cached.IsBase;

            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            float allowed = Mathf.Max(12f, radius + 18f);
            float allowedSqr = allowed * allowed;
            bool hasBaseEndpoint = false;

            PersistentContainerRegistryRuntime.ForEachPersistent((candidate, role, owner, position) =>
            {
                if (hasBaseEndpoint || candidate == null) return;
                if (role != WorkerContainerRole.Import && role != WorkerContainerRole.Export) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                float dx = position.x - center.x;
                float dz = position.z - center.z;
                if (dx * dx + dz * dz <= allowedSqr) hasBaseEndpoint = true;
            });

            TransportScopeCache[workerZdo.m_uid] = new TransportScopeCacheEntry
            {
                WorkAreaId = workAreaId,
                IsBase = hasBaseEndpoint,
                ExpiresAt = now + 20f
            };
            return hasBaseEndpoint;
        }

        internal static void CollectRequestedPrefabs(Container depot, List<string> target)
        {
            if (target == null) return;
            target.Clear();
            if (depot == null) return;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            if (zdo == null) return;
            Dictionary<string, int> requests = ParseRequests(zdo.GetString(SupplyRequestsKey, string.Empty));
            foreach (string prefab in requests.Keys)
                if (!string.IsNullOrWhiteSpace(prefab)) target.Add(prefab);
        }

        /// <summary>
        /// A FieldDepot is a local bidirectional buffer. Its desired stock is the highest explicit
        /// supply request or configured transport reserve for each prefab. Stock above this target
        /// is exportable to Base Import; stock below it is supplied from Base Export.
        /// </summary>
        private static Dictionary<string, int> BuildDesiredFieldStock(Container depot)
        {
            Dictionary<string, int> desired = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (depot == null) return desired;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            if (zdo == null) return desired;

            Dictionary<string, int> requests = ParseRequests(zdo.GetString(SupplyRequestsKey, string.Empty));
            foreach (KeyValuePair<string, int> pair in requests)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                desired[pair.Key] = Mathf.Max(1, pair.Value);
            }

            List<string> configured = new List<string>();
            FieldDepotStockRuleRuntime.CollectConfiguredPrefabs(depot, configured);
            for (int i = 0; i < configured.Count; i++)
            {
                string prefab = configured[i];
                if (string.IsNullOrWhiteSpace(prefab)) continue;
                int reserve = FieldDepotStockRuleRuntime.GetTransportReserve(depot, prefab);
                if (!desired.TryGetValue(prefab, out int existing) || reserve > existing)
                    desired[prefab] = Mathf.Max(1, reserve);
            }
            return desired;
        }

        internal static bool TryGetFieldDepotSeedPolicy(Character worker, ZDO workerZdo, string seedPrefab, int fallbackReserve,
            out Container depot, out int seedStock, out int transportReserve, out int productionTrigger,
            out bool seedProductionMode, out bool customPolicy)
        {
            depot = null;
            seedStock = 0;
            transportReserve = 0;
            productionTrigger = 0;
            seedProductionMode = false;
            customPolicy = false;
            if (worker == null || workerZdo == null || string.IsNullOrEmpty(seedPrefab)) return false;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius)) return false;

            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            float bestAnySqr = float.MaxValue;
            float bestPolicySqr = float.MaxValue;
            Container bestAny = null;
            Container bestPolicy = null;
            long pooledStock = 0L;
            bool foundDepot = false;
            bool hasConfiguredReserve = false;
            bool hasConfiguredTrigger = false;
            int maxConfiguredReserve = 0;
            int maxConfiguredTrigger = 0;

            // v0.5.394: seed stock and thresholds are Workzone-pooled. Previously this method only
            // inspected the FieldDepot nearest the WorkFlag center. With several depots, seeds could
            // sit in chest B while chest A read zero, permanently forcing carrot/turnip/onion seed
            // plants even though the Workzone already held plenty of seeds. Resolve the whole pool
            // in the same bounded local registry walk used for other concrete Garden jobs.
            WorkerStorageRuntime.ForEachContainerNear(center, Mathf.Clamp(radius + 4f, 12f, 180f), c =>
            {
                if (c == null || !IsInsideAssignedOutpost(workerZdo, c.transform.position, 2f)) return;
                if (!WorkerStorageRuntime.CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.FieldDepot) return;
                if (!FieldDepotWorkzoneRuntime.IsCompatibleWithWorkerWorkArea(c, workerZdo)) return;
                Inventory inv = c.GetInventory();
                if (inv == null) return;

                foundDepot = true;
                pooledStock += Mathf.Max(0, WorkerStorageRuntime.CountPrefab(inv, seedPrefab));
                if (pooledStock > int.MaxValue) pooledStock = int.MaxValue;

                bool localReserveConfigured = FieldDepotStockRuleRuntime.HasTransportReserve(c, seedPrefab);
                bool localTriggerConfigured = FieldDepotStockRuleRuntime.HasProductionTrigger(c, seedPrefab);
                if (localReserveConfigured)
                {
                    hasConfiguredReserve = true;
                    maxConfiguredReserve = Mathf.Max(maxConfiguredReserve, FieldDepotStockRuleRuntime.GetTransportReserve(c, seedPrefab));
                }
                if (localTriggerConfigured)
                {
                    hasConfiguredTrigger = true;
                    maxConfiguredTrigger = Mathf.Max(maxConfiguredTrigger, FieldDepotStockRuleRuntime.GetProductionTrigger(c, seedPrefab));
                }

                float sqr = (c.transform.position - center).sqrMagnitude;
                if (sqr < bestAnySqr)
                {
                    bestAnySqr = sqr;
                    bestAny = c;
                }
                if ((localReserveConfigured || localTriggerConfigured) && sqr < bestPolicySqr)
                {
                    bestPolicySqr = sqr;
                    bestPolicy = c;
                }
            });

            if (!foundDepot) return false;
            depot = bestPolicy ?? bestAny;
            seedStock = pooledStock >= int.MaxValue ? int.MaxValue : (int)Math.Max(0L, pooledStock);

            // Multiple depots in one Workzone are one logical buffer. A copied policy therefore
            // describes one shared target, not N independent targets. In the rare case old chests
            // contain conflicting values, the highest configured reserve/trigger wins safely.
            transportReserve = hasConfiguredReserve
                ? Mathf.Max(0, maxConfiguredReserve)
                : Mathf.Max(0, fallbackReserve);
            productionTrigger = hasConfiguredTrigger ? Mathf.Max(0, maxConfiguredTrigger) : 0;
            if (transportReserve > 0) productionTrigger = Mathf.Clamp(productionTrigger, 0, Mathf.Max(0, transportReserve - 1));
            else productionTrigger = 0;

            customPolicy = hasConfiguredReserve || hasConfiguredTrigger || fallbackReserve > 0;
            if (customPolicy && depot != null)
            {
                seedProductionMode = FieldDepotStockRuleRuntime.EvaluateSeedProductionModeResolved(
                    depot, seedPrefab, seedStock, transportReserve, productionTrigger);
            }
            return true;
        }

        internal static bool TryFindFieldDepotForWorkArea(Character worker, ZDO workerZdo, out Container depot, out Vector3 position)
        {
            depot = null; position = default;
            if (worker == null || workerZdo == null) return false;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius)) return false;
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            float best = float.MaxValue;
            Container bestDepot = null;
            Vector3 bestPosition = default;
            WorkerStorageRuntime.ForEachContainerNear(center, Mathf.Clamp(radius + 4f, 12f, 180f), c =>
            {
                // FieldDepots are outpost-local. Never let a worker bind to a neighbouring
                // outpost merely because its broad container scan overlaps that zone.
                if (c == null || !IsInsideAssignedOutpost(workerZdo, c.transform.position, 2f)) return;
                if (!WorkerStorageRuntime.CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.FieldDepot) return;
                if (!FieldDepotWorkzoneRuntime.IsCompatibleWithWorkerWorkArea(c, workerZdo)) return;
                float sqr = (c.transform.position - center).sqrMagnitude;
                if (sqr >= best) return;
                best = sqr; bestDepot = c; bestPosition = c.transform.position;
            });
            depot = bestDepot; position = bestPosition;
            return depot != null;
        }

        /// <summary>
        /// v0.5.382: returns the stock of one item across ALL FieldDepots assigned to the worker's
        /// Workzone. This is intentionally an on-demand local registry walk used only while a worker
        /// is resolving a concrete job; it is not a polling/background scan. Protected-last-item
        /// reserves are reported separately so pooled depots behave like one logical inventory.
        /// </summary>
        internal static bool TryGetFieldDepotPoolStock(Character worker, ZDO workerZdo, string prefab,
            out int totalStock, out int usableStock, out int protectedReserve)
        {
            totalStock = 0;
            usableStock = 0;
            protectedReserve = 0;
            if (worker == null || workerZdo == null || string.IsNullOrEmpty(prefab)) return false;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius)) return false;

            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            bool foundDepot = false;
            int pooledTotal = 0;
            int pooledUsable = 0;
            int pooledProtected = 0;
            WorkerStorageRuntime.ForEachContainerNear(center, Mathf.Clamp(radius + 4f, 12f, 180f), c =>
            {
                if (c == null || !IsInsideAssignedOutpost(workerZdo, c.transform.position, 2f)) return;
                if (!WorkerStorageRuntime.CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.FieldDepot) return;
                if (!FieldDepotWorkzoneRuntime.IsCompatibleWithWorkerWorkArea(c, workerZdo)) return;
                Inventory inv = c.GetInventory();
                if (inv == null) return;

                foundDepot = true;
                int count = Mathf.Max(0, WorkerStorageRuntime.CountPrefab(inv, prefab));
                int protect = WorkerContainerRoleRuntime.ProtectLastItem(c) && count > 0 ? 1 : 0;
                pooledTotal += count;
                pooledProtected += protect;
                pooledUsable += Mathf.Max(0, count - protect);
            });
            totalStock = pooledTotal;
            usableStock = pooledUsable;
            protectedReserve = pooledProtected;
            return foundDepot;
        }

        /// <summary>
        /// Resolves the shared desired stock for one prefab across all FieldDepots assigned to the
        /// worker's Workzone. Because the depots form one logical pool, copied per-chest policies
        /// are merged by MAX, never summed.
        /// </summary>
        internal static bool TryGetFieldDepotPoolTarget(Character worker, ZDO workerZdo, string prefab, out int pooledTarget)
        {
            pooledTarget = 0;
            if (worker == null || workerZdo == null || string.IsNullOrEmpty(prefab)) return false;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius)) return false;

            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            bool foundDepot = false;
            int target = 0;
            WorkerStorageRuntime.ForEachContainerNear(center, Mathf.Clamp(radius + 4f, 12f, 180f), c =>
            {
                if (c == null || !IsInsideAssignedOutpost(workerZdo, c.transform.position, 2f)) return;
                if (!WorkerStorageRuntime.CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.FieldDepot) return;
                if (!FieldDepotWorkzoneRuntime.IsCompatibleWithWorkerWorkArea(c, workerZdo)) return;
                foundDepot = true;
                int effectiveTarget = GetEffectiveFieldDepotTarget(c, prefab);
                target = Mathf.Max(target, effectiveTarget);
            });
            pooledTarget = Mathf.Max(0, target);
            return foundDepot;
        }

        internal static bool TryFindFieldDepotResource(Character worker, ZDO workerZdo, string prefab, int amount, out Container depot, out Vector3 position)
        {
            depot = null; position = default;
            if (worker == null || workerZdo == null || string.IsNullOrEmpty(prefab) || amount <= 0) return false;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius)) return false;
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            float best = float.MaxValue;
            Container bestDepot = null;
            Vector3 bestPosition = default;
            WorkerStorageRuntime.ForEachContainerNear(center, Mathf.Clamp(radius + 4f, 12f, 180f), c =>
            {
                if (c == null || !IsInsideAssignedOutpost(workerZdo, c.transform.position, 2f)) return;
                if (!WorkerStorageRuntime.CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.FieldDepot) return;
                if (!FieldDepotWorkzoneRuntime.IsCompatibleWithWorkerWorkArea(c, workerZdo)) return;
                Inventory inv = c.GetInventory();
                if (inv == null) return;
                int protect = WorkerContainerRoleRuntime.ProtectLastItem(c) ? 1 : 0;
                if (WorkerStorageRuntime.CountPrefab(inv, prefab) < amount + protect) return;
                float sqr = (c.transform.position - worker.transform.position).sqrMagnitude;
                if (sqr >= best) return;
                best = sqr; bestDepot = c; bestPosition = c.transform.position;
            });
            depot = bestDepot; position = bestPosition;
            return depot != null;
        }

        internal static bool TryFindForestrySeedMarker(Character worker, ZDO workerZdo, out Container depot, out string seedPrefab)
        {
            depot = null; seedPrefab = string.Empty;
            if (!TryFindFieldDepotForWorkArea(worker, workerZdo, out Container candidate, out _)) return false;
            Inventory inv = candidate.GetInventory();
            if (inv == null) return false;
            List<ItemDrop.ItemData> items = inv.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (item == null || item.m_stack <= 0) continue;
                string prefab = WorkerStorageRuntime.GetPrefabName(item);
                if (!ForestryWorkRuntime.TryGetSaplingPrefab(prefab, out _)) continue;
                depot = candidate; seedPrefab = prefab; return true;
            }
            return false;
        }

        internal static bool TryFindGardenResourceMarker(Character worker, ZDO workerZdo, out Container depot, out string resourcePrefab, out int requiredAmount)
        {
            depot = null; resourcePrefab = string.Empty; requiredAmount = 0;
            if (!TryFindFieldDepotForWorkArea(worker, workerZdo, out Container candidate, out _)) return false;
            Inventory inv = candidate.GetInventory();
            if (inv == null) return false;
            List<ItemDrop.ItemData> items = inv.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (item == null || item.m_stack <= 0) continue;
                string prefab = WorkerStorageRuntime.GetPrefabName(item);
                if (!GardenWorkRuntime.TryGetPlantingData(prefab, out _, out int amount, out _)) continue;
                depot = candidate; resourcePrefab = prefab; requiredAmount = Mathf.Max(1, amount); return true;
            }
            return false;
        }

        internal static bool IsInsideAssignedOutpost(ZDO workerZdo, Vector3 position, float padding = 4f)
        {
            if (workerZdo == null || !WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius)) return false;
            float allowed = Mathf.Max(4f, radius) + Mathf.Max(0f, padding);
            float dx = position.x - center.x;
            float dz = position.z - center.z;
            return dx * dx + dz * dz <= allowed * allowed;
        }

        internal static bool TryFindAssignedOutpostFieldDepot(Character worker, ZDO workerZdo, out Container depot, out Vector3 position)
        {
            depot = null; position = default;
            if (worker == null || workerZdo == null) return false;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius)) return false;
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            string workerWorkAreaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            float best = float.MaxValue;
            Container bestDepot = null;
            Vector3 bestPosition = default;
            WorkerStorageRuntime.ForEachContainerNear(center, Mathf.Clamp(radius + 8f, 16f, 180f), c =>
            {
                if (c == null || !IsInsideAssignedOutpost(workerZdo, c.transform.position, 4f)) return;
                if (!WorkerStorageRuntime.CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.FieldDepot) return;
                string depotWorkAreaId = FieldDepotWorkzoneRuntime.GetAssignedWorkAreaId(c);
                if (!string.IsNullOrEmpty(workerWorkAreaId) && !string.IsNullOrEmpty(depotWorkAreaId) &&
                    !string.Equals(workerWorkAreaId, depotWorkAreaId, StringComparison.Ordinal)) return;
                float sqr = (c.transform.position - center).sqrMagnitude;
                if (sqr >= best) return;
                best = sqr; bestDepot = c; bestPosition = c.transform.position;
            });
            depot = bestDepot;
            position = bestPosition;
            return depot != null;
        }

        /// <summary>
        /// Outpost Transporters stay scoped to their own WorkArea. Base Transporters are allowed
        /// to claim demand from any other ground Outpost, so one Base worker may serve several
        /// remote farms/forestry camps while each Outpost transporter remains dedicated locally.
        /// </summary>
        internal static bool TryFindOutstandingRequestForHaulerOutpost(Character worker, ZDO workerZdo, out SupplyRequest request)
        {
            bool baseTransporter = IsBaseTransporter(workerZdo);
            bool loaded = baseTransporter
                ? FieldSupplyDemandCacheRuntime.TryFindGroundRequestForBaseTransporter(worker, workerZdo, false, out request)
                : FieldSupplyDemandCacheRuntime.TryFindGroundRequestForAssignedOutpost(worker, workerZdo, out request);
            if (loaded) return true;
            return FieldSupplyDemandCacheRuntime.TryFindPersistentGroundRequest(workerZdo,
                restrictToAssignedOutpost: !baseTransporter, sorterClaimAware: false, haulerClaimAware: false,
                excludeAssignedWorkArea: baseTransporter, out request);
        }

        internal static bool TryFindOutstandingRequest(Character worker, ZDO workerZdo, out SupplyRequest request)
        {
            // Loaded demand is event-driven. If the remote Outpost is currently unloaded, use the
            // persistent FieldDepot endpoint cache so the Base sorter still stages its request.
            if (FieldSupplyDemandCacheRuntime.TryFindGroundRequest(worker, workerZdo, out request)) return true;
            return FieldSupplyDemandCacheRuntime.TryFindPersistentGroundRequest(workerZdo,
                restrictToAssignedOutpost: false, sorterClaimAware: false, haulerClaimAware: false,
                excludeAssignedWorkArea: false, out request);
        }

        internal static bool TryFindExportHaulSource(Character worker, ZDO zdo, out Container export, out string prefab, out int amount, out Vector3 sourcePosition, out Vector3 depotPosition, out ZDOID depotId, out string depotWorkAreaId)
        {
            return TryFindExportHaulSourceCore(worker, zdo, false, out export, out prefab, out amount, out sourcePosition, out depotPosition, out depotId, out depotWorkAreaId);
        }

        internal static bool TryFindExportHaulSourceForPhysical(Character worker, ZDO zdo, out Container export, out string prefab, out int amount, out Vector3 sourcePosition, out Vector3 depotPosition, out ZDOID depotId, out string depotWorkAreaId)
        {
            return TryFindExportHaulSourceCore(worker, zdo, true, out export, out prefab, out amount, out sourcePosition, out depotPosition, out depotId, out depotWorkAreaId);
        }

        private static bool TryFindExportHaulSourceCore(Character worker, ZDO zdo, bool claimAware, out Container export, out string prefab, out int amount, out Vector3 sourcePosition, out Vector3 depotPosition, out ZDOID depotId, out string depotWorkAreaId)
        {
            export = null; prefab = string.Empty; amount = 0; sourcePosition = default; depotPosition = default; depotId = ZDOID.None; depotWorkAreaId = string.Empty;
            SupplyRequest request;
            bool baseTransporter = IsBaseTransporter(zdo);
            bool hasRequest;
            if (claimAware)
            {
                hasRequest = baseTransporter
                    ? FieldSupplyDemandCacheRuntime.TryFindGroundRequestForBaseTransporter(worker, zdo, true, out request)
                    : FieldSupplyDemandCacheRuntime.TryFindGroundRequestForAssignedOutpostClaimAware(worker, zdo, out request);
                if (!hasRequest)
                    hasRequest = FieldSupplyDemandCacheRuntime.TryFindPersistentGroundRequest(zdo,
                        restrictToAssignedOutpost: !baseTransporter, sorterClaimAware: false, haulerClaimAware: true,
                        excludeAssignedWorkArea: baseTransporter, out request);
            }
            else
            {
                hasRequest = TryFindOutstandingRequestForHaulerOutpost(worker, zdo, out request);
            }
            if (!hasRequest) return false;

            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            float bestFullSqr = float.MaxValue;
            float bestPartialSqr = float.MaxValue;
            Container bestFull = null;
            Container bestPartial = null;
            Vector3 bestFullSource = default;
            Vector3 bestPartialSource = default;
            int bestFullAvailable = 0;
            int bestPartialAvailable = 0;
            ZDOID bestFullId = ZDOID.None;
            ZDOID bestPartialId = ZDOID.None;
            int desiredLoad = Mathf.Min(request.Need, HaulerWorkRuntime.GetCarryCapacity(worker, request.Prefab, request.Need));

            // Export is a Base endpoint. Claims reserve both the DemandJob slice and the concrete
            // staged Export stock, so two physical transporters cannot walk toward the same items.
            WorkerStorageRuntime.ForEachLoadedContainer(c =>
            {
                if (!WorkerStorageRuntime.CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.Export) return;
                Inventory inv = c.GetInventory();
                if (inv == null) return;
                int stock = WorkerStorageRuntime.CountPrefab(inv, request.Prefab);
                if (stock <= 0) return;
                ZDO sourceZdo = WorkerContainerRoleRuntime.GetZdo(c);
                if (sourceZdo == null) return;
                int claimedHere = claimAware ? HaulerDemandClaimRuntime.GetClaimedAmountForSource(ownerId, request.Prefab, sourceZdo.m_uid, zdo) : 0;
                int available = Mathf.Max(0, stock - claimedHere);
                if (available <= 0) return;
                float sqr = (c.transform.position - worker.transform.position).sqrMagnitude;
                if (available >= desiredLoad && sqr < bestFullSqr)
                {
                    bestFullSqr = sqr;
                    bestFull = c;
                    bestFullSource = c.transform.position;
                    bestFullAvailable = available;
                    bestFullId = sourceZdo.m_uid;
                    return;
                }
                bool betterPartial = bestPartial == null || available > bestPartialAvailable ||
                                     (available == bestPartialAvailable && sqr < bestPartialSqr);
                if (!betterPartial) return;
                bestPartialSqr = sqr;
                bestPartial = c;
                bestPartialSource = c.transform.position;
                bestPartialAvailable = available;
                bestPartialId = sourceZdo.m_uid;
            });

            Container chosen = bestFull ?? bestPartial;
            if (chosen == null) return false;
            bool full = bestFull != null;
            int availableAtSource = full ? bestFullAvailable : bestPartialAvailable;
            ZDOID sourceId = full ? bestFullId : bestPartialId;
            int desiredAmount = Mathf.Min(request.Need, HaulerWorkRuntime.GetCarryCapacity(worker, request.Prefab, availableAtSource));
            if (desiredAmount <= 0) return false;

            int selectedAmount = desiredAmount;
            if (claimAware)
            {
                if (!HaulerDemandClaimRuntime.TryAcquire(zdo, request, sourceId, WorkerStorageRuntime.CountPrefab(chosen.GetInventory(), request.Prefab), desiredAmount, out selectedAmount, out _))
                    return false;
            }

            export = chosen;
            prefab = request.Prefab;
            amount = selectedAmount;
            sourcePosition = full ? bestFullSource : bestPartialSource;
            depotPosition = request.Position;
            depotId = request.DepotId;
            depotWorkAreaId = request.WorkAreaId;
            return amount > 0;
        }

        internal static bool TryFindSorterSource(Character worker, ZDO zdo, out Container source, out string prefab, out int amount, out string targetKind, out Vector3 position)
        {
            return TryFindSorterSourceCore(worker, zdo, false, out source, out prefab, out amount, out targetKind, out position, out _);
        }

        internal static bool TryFindSorterSourceForPhysical(Character worker, ZDO zdo, out Container source, out string prefab, out int amount, out string targetKind, out Vector3 position, out SupplyRequest demandRequest)
        {
            return TryFindSorterSourceCore(worker, zdo, true, out source, out prefab, out amount, out targetKind, out position, out demandRequest);
        }

        private static bool TryFindSorterSourceCore(Character worker, ZDO zdo, bool claimAware, out Container source, out string prefab, out int amount, out string targetKind, out Vector3 position, out SupplyRequest demandRequest)
        {
            source = null; prefab = string.Empty; amount = 0; targetKind = string.Empty; position = default; demandRequest = default;
            if (worker == null || zdo == null) return false;
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
            int capacity = HaulerWorkRuntime.GetCarryCapacity(worker);

            // Priority 0: keep the local Base logistics flowing. v0.5.346 no longer opens every
            // Import inventory on every Sorter decision. Container.Awake/Save already maintains the
            // shared stock snapshots, so this hot path only reads cached item/count pairs.
            SharedStorageStockIndexRuntime.CollectCachedItemSources(default, 0f, import =>
                import != null &&
                WorkerStorageRuntime.CanUseContainer(import, ownerId) &&
                WorkerContainerRoleRuntime.MatchesOwner(import, ownerId) &&
                WorkerContainerRoleRuntime.GetRole(import) == WorkerContainerRole.Import,
                ImportSourceScratch);

            float bestImportSqr = float.MaxValue;
            SharedStorageStockIndexRuntime.CachedItemSource bestImport = default;
            int bestImportMovable = 0;
            for (int i = 0; i < ImportSourceScratch.Count; i++)
            {
                SharedStorageStockIndexRuntime.CachedItemSource candidate = ImportSourceScratch[i];
                if (candidate.Container == null || string.IsNullOrEmpty(candidate.Prefab)) continue;
                int protectedCount = WorkerContainerRoleRuntime.ProtectLastItem(candidate.Container) ? 1 : 0;
                int movable = Mathf.Max(0, candidate.Count - protectedCount);
                if (movable <= 0) continue;

                // Normal storage belongs to the Base cluster around the Import endpoint, not to the
                // worker's nest/home. Destination lookup is also cache-first since v0.5.337.
                if (!TryFindNormalDestinationNear(worker, zdo, candidate.Position, candidate.Prefab, out _, out _)) continue;
                float sqr = (candidate.Position - worker.transform.position).sqrMagnitude;
                if (sqr >= bestImportSqr) continue;
                bestImportSqr = sqr;
                bestImport = candidate;
                bestImportMovable = movable;
            }

            if (bestImport.Container != null && bestImportMovable > 0)
            {
                source = bestImport.Container;
                prefab = bestImport.Prefab;
                amount = HaulerWorkRuntime.GetCarryCapacity(worker, bestImport.Prefab, bestImportMovable);
                targetKind = WorkerStorageRuntime.SortImportSourceTargetKind;
                position = bestImport.Position;
                ImportSourceScratch.Clear();
                return amount > 0;
            }
            ImportSourceScratch.Clear();

            // Priority 1: empty the Base-side Advanced FieldDepot into normal storage. This is
            // the AirTransport equivalent of Import and lets one upgraded chest serve both inbound
            // and outbound Drake freight without adding another chest family.
            if (AirHaulerWorkRuntime.TryFindSorterInboundSource(worker, zdo, out Container airInbound, out string airInboundPrefab, out int airInboundAmount, out Vector3 airInboundPosition))
            {
                source = airInbound; prefab = airInboundPrefab; amount = airInboundAmount;
                targetKind = AirHaulerWorkRuntime.SortInboundSourceTargetKind; position = airInboundPosition;
                return amount > 0;
            }

            // Priority 2: stage requested AirTransport supplies into the Base-side Advanced
            // FieldDepot. The Drake then carries them directly to the upgraded outpost depot.
            if (AirHaulerWorkRuntime.TryFindSorterOutboundSource(worker, zdo, out Container airNormalSource, out string airOutboundPrefab, out int airOutboundAmount, out Vector3 airNormalPosition, out Vector3 airDepotPosition))
            {
                WorkerZdo.SetCargoRoute(zdo, AirHaulerWorkRuntime.RouteSortOutbound, airDepotPosition);
                source = airNormalSource; prefab = airOutboundPrefab; amount = airOutboundAmount;
                targetKind = AirHaulerWorkRuntime.SortOutboundSourceTargetKind; position = airNormalPosition;
                return amount > 0;
            }

            // Priority 3: stage missing ground-transport field supplies into Export.
            SupplyRequest request;
            bool hasRequest = claimAware
                ? FieldSupplyDemandCacheRuntime.TryFindGroundRequestForSorter(worker, zdo, out request)
                : TryFindOutstandingRequest(worker, zdo, out request);
            if (!hasRequest && claimAware)
                hasRequest = FieldSupplyDemandCacheRuntime.TryFindPersistentGroundRequest(zdo,
                    restrictToAssignedOutpost: false, sorterClaimAware: true, haulerClaimAware: false,
                    excludeAssignedWorkArea: false, out request);
            if (!hasRequest) return false;
            demandRequest = request;
            int exportStock = CountRoleStock(worker, zdo, WorkerContainerRole.Export, request.Prefab);
            int loadedNeed = FieldSupplyDemandCacheRuntime.GetTotalOpenGroundNeed(ownerId, request.Prefab);
            int persistentNeed = FieldSupplyDemandCacheRuntime.GetPersistentTotalOpenGroundNeed(ownerId, request.Prefab);
            int totalOpenNeed = Mathf.Max(loadedNeed, persistentNeed);
            int alreadyClaimed = claimAware ? SorterDemandClaimRuntime.GetClaimedAmountForPrefab(ownerId, request.Prefab) : 0;
            int stageNeed = Mathf.Max(0, totalOpenNeed - exportStock - alreadyClaimed);
            if (stageNeed <= 0) return false;
            if (!TryFindNormalSource(worker, zdo, request.Prefab, stageNeed, out Container normalSource, out int usable, out Vector3 normalPosition)) return false;
            source = normalSource; prefab = request.Prefab; amount = HaulerWorkRuntime.GetCarryCapacity(worker, request.Prefab, Mathf.Min(stageNeed, usable)); targetKind = WorkerStorageRuntime.SortBaseSourceTargetKind; position = normalPosition;
            return amount > 0;
        }

        internal static string DescribeSorterIdle(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return "sorter: worker unavailable";
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);

            // Diagnose the local Base pipeline first. A blocked/full Import chest is more important
            // than any remote supply request because it also stops inbound transporters.
            int importChests = SharedStorageStockIndexRuntime.CountCachedContainers(import =>
                import != null &&
                WorkerStorageRuntime.CanUseContainer(import, ownerId) &&
                WorkerContainerRoleRuntime.MatchesOwner(import, ownerId) &&
                WorkerContainerRoleRuntime.GetRole(import) == WorkerContainerRole.Import);
            int importUnits = 0;
            int importMovable = 0;
            string blockedPrefab = string.Empty;
            Vector3 blockedImportPosition = default;
            SharedStorageStockIndexRuntime.CollectCachedItemSources(default, 0f, import =>
                import != null &&
                WorkerStorageRuntime.CanUseContainer(import, ownerId) &&
                WorkerContainerRoleRuntime.MatchesOwner(import, ownerId) &&
                WorkerContainerRoleRuntime.GetRole(import) == WorkerContainerRole.Import,
                ImportSourceScratch);
            for (int i = 0; i < ImportSourceScratch.Count; i++)
            {
                SharedStorageStockIndexRuntime.CachedItemSource candidate = ImportSourceScratch[i];
                importUnits += Mathf.Max(0, candidate.Count);
                int protectedCount = candidate.Container != null && WorkerContainerRoleRuntime.ProtectLastItem(candidate.Container) ? 1 : 0;
                importMovable += Mathf.Max(0, candidate.Count - protectedCount);
                if (string.IsNullOrEmpty(blockedPrefab))
                {
                    blockedPrefab = candidate.Prefab;
                    blockedImportPosition = candidate.Position;
                }
            }
            ImportSourceScratch.Clear();

            if (importUnits > 0)
            {
                if (importMovable <= 0)
                    return $"sorter waiting: Import has {importUnits} unit(s), but Keep-One protects all remaining stock";

                string destinationState = DescribeNormalDestinationNear(worker, zdo, blockedImportPosition, blockedPrefab);
                return $"sorter waiting: Import has {importUnits} unit(s){(string.IsNullOrEmpty(blockedPrefab) ? string.Empty : " incl. " + blockedPrefab)}; {destinationState}";
            }

            if (TryFindBaseAirStagingOrder(ownerId, home, out _, out _, out string airPrefab, out int airMissing))
            {
                if (!TryFindNormalSource(worker, zdo, airPrefab, out _, out int usable, out _))
                    return $"sorter waiting: Base air request {airPrefab} x{airMissing}; no movable stock in normal Base storage (empty/Keep-One protected)";
                return $"sorter waiting: Base air request {airPrefab} x{airMissing}; source has {usable}, but Base AirFreight is unavailable/full";
            }

            if (TryFindOutstandingRequest(worker, zdo, out SupplyRequest groundRequest))
            {
                if (!TryFindNormalSource(worker, zdo, groundRequest.Prefab, out _, out int usable, out _))
                    return $"sorter waiting: ground request {groundRequest.Prefab} x{groundRequest.Need}; no movable stock in normal Base storage";
                int exportRoom = GetRoleFreeCapacity(worker, zdo, WorkerContainerRole.Export, groundRequest.Prefab, requireExisting: false);
                return exportRoom <= 0
                    ? $"sorter waiting: ground request {groundRequest.Prefab} x{groundRequest.Need}; Export missing/full"
                    : $"sorter waiting: ground request {groundRequest.Prefab} x{groundRequest.Need}; source has {usable}, retrying";
            }

            if (importChests == 0)
                return "sorter idle: no Import chest registered/loaded and no outbound supply request";
            return "sorter idle: Import empty and no outbound supply request";
        }

        internal static bool HasDownstreamNormalStorageCapacity(Character worker, ZDO zdo, string prefab)
        {
            if (zdo == null || string.IsNullOrEmpty(prefab)) return false;
            if (worker != null && TryFindNormalDestination(worker, zdo, prefab, out _, out _)) return true;

            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            bool found = false;
            PersistentContainerRegistryRuntime.ForEachPersistent((candidate, role, owner, position) =>
            {
                if (found || candidate == null || role != WorkerContainerRole.Normal) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                if (!candidate.GetBool(ContainerLedgerRuntime.SorterTrackedKey, false)) return;
                if (!ContainerLedgerRuntime.TryGetPersistentFreeCapacity(candidate, prefab, out int free) || free <= 0) return;
                if (ContainerLedgerRuntime.GetSnapshotAmount(candidate, prefab) <= 0) return;
                found = true;
            });
            return found;
        }

        internal static bool TryFindNormalDestination(Character worker, ZDO zdo, string prefab, out Container destination, out Vector3 position)
        {
            Vector3 center = worker != null && zdo != null ? WorkerZdo.ReadHome(zdo, worker.transform.position) : default;
            return TryFindNormalDestinationNear(worker, zdo, center, prefab, out destination, out position);
        }

        internal static bool TryFindNormalDestinationNear(Character worker, ZDO zdo, Vector3 center, string prefab, out Container destination, out Vector3 position)
        {
            destination = null;
            position = default;
            if (worker == null || zdo == null || string.IsNullOrEmpty(prefab)) return false;
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);

            // v0.5.337: marker storage is resolved from the shared reverse item index. Normal
            // operation touches only chests already known to contain this prefab; a bounded rescan
            // happens only after a cache miss/stale entry.
            return SharedStorageStockIndexRuntime.TryFindItemDestination(
                prefab,
                center,
                WorkerStorageRuntime.LogisticsNetworkRadius,
                center,
                c =>
                {
                    if (c == null || !WorkerStorageRuntime.CanUseContainer(c, ownerId)) return false;
                    if (WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.Normal) return false;
                    Inventory inv = c.GetInventory();
                    return inv != null && WorkerStorageRuntime.HasLikelyRoomFor(inv, prefab);
                },
                out destination,
                out position);
        }

        private static string DescribeNormalDestinationNear(Character worker, ZDO zdo, Vector3 center, string prefab)
        {
            if (worker == null || zdo == null || string.IsNullOrEmpty(prefab)) return "invalid normal-storage diagnostic";
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            int normalChests = 0;
            int matchingChests = 0;
            int matchingWithRoom = 0;
            WorkerStorageRuntime.ForEachContainerNear(center, WorkerStorageRuntime.LogisticsNetworkRadius, c =>
            {
                if (!WorkerStorageRuntime.CanUseContainer(c, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.Normal) return;
                normalChests++;
                Inventory inv = c.GetInventory();
                if (inv == null || !WorkerStorageRuntime.InventoryContainsPrefab(inv, prefab)) return;
                matchingChests++;
                if (WorkerStorageRuntime.HasLikelyRoomFor(inv, prefab)) matchingWithRoom++;
            });

            if (normalChests == 0) return $"no normal Base storage found within {WorkerStorageRuntime.LogisticsNetworkRadius:0} m of Import";
            if (matchingChests == 0) return $"no normal Base chest contains {prefab} (marker item missing)";
            if (matchingWithRoom == 0) return $"{matchingChests} matching normal chest(s) for {prefab} are full";
            return $"matching normal destination for {prefab} exists but pickup could not start";
        }

        internal static bool TryFindNormalSource(Character worker, ZDO zdo, string prefab, out Container source, out int usable, out Vector3 position)
        {
            return TryFindNormalSource(worker, zdo, prefab, 1, out source, out usable, out position);
        }

        internal static bool TryFindNormalSource(Character worker, ZDO zdo, string prefab, int preferredAmount,
            out Container source, out int usable, out Vector3 position)
        {
            source = null;
            usable = 0;
            position = default;
            if (worker == null || zdo == null || string.IsNullOrEmpty(prefab)) return false;
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);

            return SharedStorageStockIndexRuntime.TryFindItemSource(
                prefab,
                home,
                WorkerStorageRuntime.LogisticsNetworkRadius,
                worker.transform.position,
                Math.Max(1, preferredAmount),
                c =>
                {
                    if (c == null || !WorkerStorageRuntime.CanUseContainer(c, ownerId)) return false;
                    if (WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.Normal) return false;
                    ZDO sourceZdo = WorkerContainerRoleRuntime.GetZdo(c);
                    if (sourceZdo != null && sourceZdo.GetBool(ContainerLedgerRuntime.DirtyKey, false) &&
                        !ContainerLedgerRuntime.TryReconcileLoaded(c, out _)) return false;
                    return true;
                },
                c => WorkerContainerRoleRuntime.ProtectLastItem(c) ? 1 : 0,
                out source,
                out usable,
                out position);
        }

        internal static bool TryFindExportDestination(Character worker, ZDO zdo, string prefab, out Container destination, out Vector3 position)
        {
            return TryFindRoleDestination(worker, zdo, prefab, WorkerContainerRole.Export, out destination, out position);
        }

        internal static bool TryFindRequestedFieldDepotDestination(Character worker, ZDO zdo, string prefab, Vector3 requestedPosition, out Container destination, out Vector3 position)
        {
            return TryFindRequestedFieldDepotDestination(worker, zdo, prefab, requestedPosition, ZDOID.None, out destination, out position);
        }

        internal static bool TryFindRequestedFieldDepotDestination(Character worker, ZDO zdo, string prefab, Vector3 requestedPosition, ZDOID requestedDepotId, out Container destination, out Vector3 position)
        {
            return TryFindRequestedFieldDepotDestination(worker, zdo, prefab, requestedPosition, requestedDepotId, string.Empty, out destination, out position);
        }

        internal static bool TryFindRequestedFieldDepotDestination(Character worker, ZDO zdo, string prefab, Vector3 requestedPosition, ZDOID requestedDepotId, string requestedWorkAreaId, out Container destination, out Vector3 position)
        {
            destination = null; position = default;
            if (worker == null || zdo == null) return false;
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);

            // v0.5.342: the Workzone is the durable delivery address. Resolve a suitable linked
            // FieldDepot only now, so a full/removed chest cannot invalidate the whole zone job.
            if (!string.IsNullOrEmpty(requestedWorkAreaId))
            {
                return FieldSupplyDemandCacheRuntime.TryFindDestinationInWorkArea(worker, zdo, prefab, requestedWorkAreaId, out destination, out position);
            }

            // v0.5.341 legacy: an older DemandJob may still carry one concrete FieldDepot ZDO.
            // the delivery endpoint. Do not silently fall over to a neighbouring depot just because
            // it happens to sit inside the old 18 m proximity window.
            if (!requestedDepotId.Equals(ZDOID.None))
            {
                Container exact = null;
                WorkerStorageRuntime.ForEachLoadedContainer(c =>
                {
                    if (exact != null || c == null) return;
                    ZDO candidateZdo = WorkerContainerRoleRuntime.GetZdo(c);
                    if (candidateZdo == null || !candidateZdo.m_uid.Equals(requestedDepotId)) return;
                    if (!WorkerStorageRuntime.CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                    if (WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.FieldDepot) return;
                    if (!WorkerStorageRuntime.HasLikelyRoomFor(c.GetInventory(), prefab)) return;
                    if (WorkerContainerRoleRuntime.SupportsAirTransport(c))
                    {
                        int physical = WorkerStorageRuntime.GetAvailableCapacityForPrefab(c.GetInventory(), prefab);
                        if (ContainerStockLimitRuntime.GetAcceptCapacity(c, prefab, physical) <= 0) return;
                    }
                    exact = c;
                });
                if (exact == null) return false;
                destination = exact;
                position = exact.transform.position;
                return true;
            }

            // Legacy cargo from older saves has no endpoint id. Keep the previous spatial resolver
            // only for that migration case. New jobs never use this ambiguous path.
            float best = float.MaxValue;
            Container bestDestination = null;
            Vector3 bestPosition = default;
            WorkerStorageRuntime.ForEachContainerNear(requestedPosition, 18f, c =>
            {
                if (!WorkerStorageRuntime.CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.FieldDepot) return;
                if (!WorkerStorageRuntime.HasLikelyRoomFor(c.GetInventory(), prefab)) return;
                if (WorkerContainerRoleRuntime.SupportsAirTransport(c))
                {
                    int physical = WorkerStorageRuntime.GetAvailableCapacityForPrefab(c.GetInventory(), prefab);
                    if (ContainerStockLimitRuntime.GetAcceptCapacity(c, prefab, physical) <= 0) return;
                }
                float sqr = (c.transform.position - requestedPosition).sqrMagnitude;
                if (sqr >= best) return;
                best = sqr; bestDestination = c; bestPosition = c.transform.position;
            });
            destination = bestDestination; position = bestPosition;
            return destination != null;
        }

        internal static bool TryCollectLogisticsCargo(Character worker, ZDO zdo, string sourceKind)
        {
            if (worker == null || zdo == null || WorkerZdo.HasCargo(zdo)) return false;
            if (!WorkerZdo.TryReadTarget(zdo, out string desiredPrefab, out string kind, out Vector3 targetPosition) || !string.Equals(kind, sourceKind, StringComparison.Ordinal)) return false;
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            WorkerContainerRole expectedRole;
            string route;
            Vector3 routeTarget = default;
            ZDOID routeTargetId = WorkerZdo.ReadCargoRouteTargetZdoId(zdo);
            string routeTargetWorkAreaId = WorkerZdo.ReadCargoRouteTargetWorkAreaId(zdo);
            if (sourceKind == WorkerStorageRuntime.ExportHaulSourceTargetKind) { expectedRole = WorkerContainerRole.Export; route = RouteHaulOutbound; WorkerZdo.TryReadCargoRoute(zdo, out _, out routeTarget); }
            else if (sourceKind == WorkerStorageRuntime.SortImportSourceTargetKind) { expectedRole = WorkerContainerRole.Import; route = RouteSortImport; routeTarget = targetPosition; }
            else if (sourceKind == WorkerStorageRuntime.SortBaseSourceTargetKind) { expectedRole = WorkerContainerRole.Normal; route = RouteSortExport; }
            else if (sourceKind == AirHaulerWorkRuntime.SortInboundSourceTargetKind) { expectedRole = WorkerContainerRole.FieldDepot; route = AirHaulerWorkRuntime.RouteSortInbound; routeTarget = targetPosition; }
            else if (sourceKind == AirHaulerWorkRuntime.SortOutboundSourceTargetKind) { expectedRole = WorkerContainerRole.Normal; route = AirHaulerWorkRuntime.RouteSortOutbound; WorkerZdo.TryReadCargoRoute(zdo, out _, out routeTarget); }
            else return false;

            int sorterDemandClaimLimit = int.MaxValue;
            int haulerDemandClaimLimit = int.MaxValue;
            if (sourceKind == WorkerStorageRuntime.SortBaseSourceTargetKind)
            {
                sorterDemandClaimLimit = SorterDemandClaimRuntime.GetClaimedAmountForWorker(zdo, desiredPrefab);
                if (sorterDemandClaimLimit <= 0)
                {
                    WorkerZdo.ClearTarget(zdo);
                    zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                    WorkerStorageRuntime.SetDebug(worker, $"sorting outbound replanning: no active DemandJob claim for {desiredPrefab}");
                    return true;
                }
                SorterDemandClaimRuntime.Touch(zdo);
            }
            else if (sourceKind == WorkerStorageRuntime.ExportHaulSourceTargetKind)
            {
                haulerDemandClaimLimit = HaulerDemandClaimRuntime.GetClaimedAmountForWorker(zdo, desiredPrefab);
                if (haulerDemandClaimLimit <= 0)
                {
                    WorkerZdo.ClearTarget(zdo);
                    zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                    WorkerStorageRuntime.SetDebug(worker, $"haul outbound replanning: no active transport claim for {desiredPrefab}");
                    return true;
                }
                HaulerDemandClaimRuntime.Touch(zdo);
            }

            // Resolve the real source first, then honor that chest's actual Keep-One toggle.
            // Older code hard-coded Normal/FieldDepot=1 and Import/Export=0 here, so switching
            // Keep-One off in the UI could still leave the final item immovable.
            bool localFallback = LocalWorkFallbackRuntime.IsActive(zdo, desiredPrefab, kind);
            Container source = FindNearbyRoleContainer(worker, targetPosition, ownerId, expectedRole, desiredPrefab, 1, allowRemoteFallback: localFallback);
            if (source != null && sourceKind == AirHaulerWorkRuntime.SortInboundSourceTargetKind && (!WorkerContainerRoleRuntime.SupportsAirTransport(source) || WorkerContainerRoleRuntime.GetAirDepotMode(source) != AirDepotMode.BaseHub)) source = null;
            if (source == null)
            {
                if (sourceKind == WorkerStorageRuntime.SortBaseSourceTargetKind)
                    SorterDemandClaimRuntime.Release(zdo, "source-unavailable");
                if (sourceKind == WorkerStorageRuntime.ExportHaulSourceTargetKind)
                    HaulerDemandClaimRuntime.Release(zdo, "source-unavailable");
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return true;
            }
            if (sourceKind == WorkerStorageRuntime.ExportHaulSourceTargetKind)
            {
                ZDO sourceZdo = WorkerContainerRoleRuntime.GetZdo(source);
                if (sourceZdo == null || !HaulerDemandClaimRuntime.MatchesSource(zdo, sourceZdo.m_uid))
                {
                    HaulerDemandClaimRuntime.Release(zdo, "source-changed");
                    WorkerZdo.ClearTarget(zdo);
                    zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                    WorkerStorageRuntime.SetDebug(worker, $"haul outbound replanning: reserved Export source changed for {desiredPrefab}");
                    return true;
                }
            }

            int destinationCapacity;
            if (route == RouteHaulOutbound)
                destinationCapacity = GetRequestedFieldDepotFreeCapacity(worker, zdo, desiredPrefab, routeTarget, routeTargetId, routeTargetWorkAreaId);
            else if (route == RouteSortImport || route == AirHaulerWorkRuntime.RouteSortInbound)
            {
                // Use the same Base cluster that was accepted when the Sort job was created.
                // The previous generic capacity check searched around the worker HomeAnchor,
                // so planning could find a valid marker chest beside Import/AirFreight and the
                // actual pickup could immediately cancel because a different search center was used.
                bool foundNormalDestination = TryFindNormalDestinationNear(worker, zdo, routeTarget, desiredPrefab, out Container normalDestination, out _);
                if (!foundNormalDestination)
                    foundNormalDestination = TryFindNormalDestination(worker, zdo, desiredPrefab, out normalDestination, out _);
                Inventory normalInventory = foundNormalDestination && normalDestination != null ? normalDestination.GetInventory() : null;
                destinationCapacity = normalInventory != null
                    ? WorkerStorageRuntime.GetAvailableCapacityForPrefab(normalInventory, desiredPrefab)
                    : 0;
            }
            else if (route == AirHaulerWorkRuntime.RouteSortOutbound)
                destinationCapacity = AirHaulerWorkRuntime.GetSpecificAirDepotFreeCapacity(worker, zdo, desiredPrefab, routeTarget);
            else
                destinationCapacity = GetRoleFreeCapacity(worker, zdo, WorkerContainerRole.Export, desiredPrefab, requireExisting: false);

            if (destinationCapacity <= 0)
            {
                if (sourceKind == WorkerStorageRuntime.SortBaseSourceTargetKind)
                    SorterDemandClaimRuntime.Release(zdo, "destination-full");
                if (sourceKind == WorkerStorageRuntime.ExportHaulSourceTargetKind)
                    HaulerDemandClaimRuntime.Release(zdo, "destination-full");
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                WorkerStorageRuntime.SetDebug(worker, $"logistics pickup skipped: destination full/no matching space for {desiredPrefab}; route={route}");
                return true;
            }

            if (!WorkerStorageRuntime.TryOwn(source)) return true;
            Inventory inv = source.GetInventory();
            if (inv == null) return true;
            if (!WorkerInventoryMutationGuard.CanWorkersMutate(inv))
            {
                zdo.Set(WorkerZdo.State, (int)WorkerState.Collect);
                WorkerStorageRuntime.SetDebug(worker, $"logistics pickup waiting: inventory busy for {desiredPrefab}; route={route}; quiet={WorkerInventoryMutationGuard.GetRemainingQuietTime(inv):0.00}s");
                return true;
            }
            int total = WorkerStorageRuntime.CountPrefab(inv, desiredPrefab);

            // Honor the per-container Keep-One toggle at the actual pickup moment. Base AirFreight
            // is bidirectional, so active outbound demand additionally protects staged stock from
            // being sorted back into normal storage.
            int protectedAmount = WorkerContainerRoleRuntime.ProtectLastItem(source) ? 1 : 0;
            if (sourceKind == AirHaulerWorkRuntime.SortInboundSourceTargetKind)
                protectedAmount = AirHaulerWorkRuntime.GetSorterInboundProtectedAmount(source, ownerId, desiredPrefab);

            int removable = Mathf.Max(0, total - protectedAmount);
            int max = Mathf.Min(destinationCapacity, HaulerWorkRuntime.GetCarryCapacity(worker, desiredPrefab, removable));
            if (sourceKind == WorkerStorageRuntime.SortBaseSourceTargetKind)
                max = Mathf.Min(max, sorterDemandClaimLimit);
            if (sourceKind == WorkerStorageRuntime.ExportHaulSourceTargetKind)
                max = Mathf.Min(max, haulerDemandClaimLimit);
            int removed = RemoveItems(inv, desiredPrefab, max);
            if (removed <= 0)
            {
                if (!WorkerInventoryMutationGuard.CanWorkersMutate(inv))
                {
                    zdo.Set(WorkerZdo.State, (int)WorkerState.Collect);
                    WorkerStorageRuntime.SetDebug(worker, $"logistics pickup deferred: inventory changed externally for {desiredPrefab}; route={route}");
                    return true;
                }
                if (sourceKind == WorkerStorageRuntime.SortBaseSourceTargetKind)
                    SorterDemandClaimRuntime.Release(zdo, "pickup-empty");
                if (sourceKind == WorkerStorageRuntime.ExportHaulSourceTargetKind)
                    HaulerDemandClaimRuntime.Release(zdo, "pickup-empty");
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return true;
            }
            if (sourceKind == WorkerStorageRuntime.SortBaseSourceTargetKind)
                SorterDemandClaimRuntime.LimitTo(zdo, removed);
            WorkerStorageRuntime.PersistContainer(source, inv);
            WorkerZdo.SetCargo(zdo, desiredPrefab, removed);
            if (string.Equals(route, RouteHaulOutbound, StringComparison.Ordinal))
                WorkzoneLogisticsFlowRuntime.TagCargoSource(zdo, source, "base");
            WorkerZdo.SetCargoRoute(zdo, route, routeTarget, routeTargetId, routeTargetWorkAreaId);
            // Once cargo exists on the worker, BackgroundWorkerScheduler counts it as real
            // haul_outbound in-transit stock. Drop the pre-pickup claim immediately to avoid
            // double-counting the same items in the Workzone demand balance.
            if (sourceKind == WorkerStorageRuntime.ExportHaulSourceTargetKind)
                HaulerDemandClaimRuntime.Release(zdo, "cargo-in-transit");
            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
            WorkerStorageRuntime.SetDebug(worker, $"logistics collected: {desiredPrefab} x{removed}; route={route}");
            return true;
        }

        internal static bool TryDepositLogisticsCargo(Character worker, ZDO zdo, string expectedTargetKind)
        {
            if (worker == null || zdo == null) return false;
            if (!WorkerZdo.TryReadCargo(zdo, out string prefab, out int amount)) return false;
            if (!WorkerZdo.TryReadTarget(zdo, out string targetName, out string kind, out Vector3 targetPosition) || !string.Equals(kind, expectedTargetKind, StringComparison.Ordinal)) return false;
            bool localFallback = LocalWorkFallbackRuntime.IsActive(zdo, targetName, kind);
            WorkerContainerRole role = expectedTargetKind == WorkerStorageRuntime.SortBaseDepositTargetKind ? WorkerContainerRole.Normal :
                                       expectedTargetKind == WorkerStorageRuntime.ExportDepositTargetKind ? WorkerContainerRole.Export : WorkerContainerRole.FieldDepot;
            if (expectedTargetKind == WorkerStorageRuntime.ExportDepositTargetKind)
                SorterDemandClaimRuntime.Touch(zdo);
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Container destination = null;
            ZDOID exactRouteTargetId = WorkerZdo.ReadCargoRouteTargetZdoId(zdo);
            string routeTargetWorkAreaId = WorkerZdo.ReadCargoRouteTargetWorkAreaId(zdo);
            if (expectedTargetKind == WorkerStorageRuntime.SupplyFieldDepotDepositTargetKind && !string.IsNullOrEmpty(routeTargetWorkAreaId))
            {
                FieldSupplyDemandCacheRuntime.TryFindDestinationInWorkArea(worker, zdo, prefab, routeTargetWorkAreaId, out destination, out _);
            }
            else if (expectedTargetKind == WorkerStorageRuntime.SupplyFieldDepotDepositTargetKind && !exactRouteTargetId.Equals(ZDOID.None))
            {
                WorkerStorageRuntime.ForEachLoadedContainer(c =>
                {
                    if (destination != null || c == null) return;
                    ZDO candidateZdo = WorkerContainerRoleRuntime.GetZdo(c);
                    if (candidateZdo == null || !candidateZdo.m_uid.Equals(exactRouteTargetId)) return;
                    if (!WorkerStorageRuntime.CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                    if (WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.FieldDepot) return;
                    Inventory inv = c.GetInventory();
                    if (inv == null || !WorkerStorageRuntime.HasLikelyRoomFor(inv, prefab)) return;
                    destination = c;
                });
            }
            else
            {
                destination = FindNearbyRoleContainer(worker, targetPosition, ownerId, role, prefab, 0, requireRoom:true, requireExisting: role == WorkerContainerRole.Normal, allowRemoteFallback: localFallback);
            }
            if (destination != null && expectedTargetKind == AirHaulerWorkRuntime.SortBaseDepositTargetKind && (!WorkerContainerRoleRuntime.SupportsAirTransport(destination) || WorkerContainerRoleRuntime.GetAirDepotMode(destination) != AirDepotMode.BaseHub)) destination = null;
            if (destination == null) { WorkerZdo.ClearTarget(zdo); zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob); return true; }
            if (!WorkerStorageRuntime.TryOwn(destination)) return true;
            Inventory destinationInventory = destination.GetInventory();
            if (destinationInventory == null) return true;
            if (!WorkerInventoryMutationGuard.CanWorkersMutate(destinationInventory))
            {
                zdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
                WorkerStorageRuntime.SetDebug(worker, $"logistics deposit waiting: destination inventory busy for {prefab}; quiet={WorkerInventoryMutationGuard.GetRemainingQuietTime(destinationInventory):0.00}s");
                return true;
            }
            int deposited = AddCargoMeasured(destination, prefab, amount);
            if (deposited <= 0)
            {
                zdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
                WorkerStorageRuntime.SetDebug(worker, $"logistics deposit blocked: {prefab} x{amount} still secured as worker cargo");
                return true;
            }

            // Cargo is authoritative until the destination inventory proves that the items arrived.
            // This prevents a full/changed chest or a partial AddItem operation from deleting the
            // remainder of a large stack. Only the measured destination delta is acknowledged.
            if (expectedTargetKind == WorkerStorageRuntime.ExportDepositTargetKind)
                SorterDemandClaimRuntime.Consume(zdo, deposited);
            bool trackedOutboundExport = WorkerZdo.TryReadCargoRoute(zdo, out string flowRoute, out _) &&
                                         string.Equals(flowRoute, RouteHaulOutbound, StringComparison.Ordinal) &&
                                         expectedTargetKind == WorkerStorageRuntime.SupplyFieldDepotDepositTargetKind;
            if (trackedOutboundExport)
                WorkzoneLogisticsFlowRuntime.RecordTaggedExport(zdo, prefab, deposited, "base");
            WorkerZdo.RemoveCargoAmount(zdo, prefab, deposited);
            if (WorkerZdo.HasCargo(zdo))
            {
                int remaining = WorkerZdo.GetCargoAmount(zdo, prefab);
                zdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
                WorkerStorageRuntime.SetDebug(worker, $"logistics partial deposit: {prefab} x{deposited} delivered; x{remaining} remains secured as worker cargo");
                return true;
            }

            WorkerZdo.ClearCargo(zdo);
            if (trackedOutboundExport) WorkzoneLogisticsFlowRuntime.ClearCargoSource(zdo);
            WorkerZdo.ClearTarget(zdo); zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            WorkerStorageRuntime.SetDebug(worker, $"logistics deposited: {prefab} x{deposited} -> {WorkerContainerRoleRuntime.DisplayName(role)}");
            return true;
        }

        internal static int GetRoleFreeCapacity(Character worker, ZDO zdo, WorkerContainerRole role, string prefab, bool requireExisting)
        {
            if (worker == null || zdo == null || string.IsNullOrEmpty(prefab)) return 0;
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
            long total = 0;
            HashSet<string> loadedIds = new HashSet<string>(StringComparer.Ordinal);
            Action<Container> count = c =>
            {
                if (!WorkerStorageRuntime.CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(c) != role) return;
                Inventory inv = c.GetInventory();
                if (inv == null) return;
                if (requireExisting && !WorkerStorageRuntime.InventoryContainsPrefab(inv, prefab)) return;
                ZDO endpointZdo = WorkerContainerRoleRuntime.GetZdo(c);
                if (endpointZdo != null) loadedIds.Add(endpointZdo.m_uid.ToString());
                int physical = WorkerStorageRuntime.GetAvailableCapacityForPrefab(inv, prefab);
                int accepted = role == WorkerContainerRole.Import
                    ? ContainerStockLimitRuntime.GetAcceptCapacity(c, prefab, physical)
                    : physical;
                total += Mathf.Max(0, accepted);
            };

            // Import/Export are map-wide logistics endpoints. Import differs from the legacy path:
            // configured Min/Max buffers have a real persistent capacity and must not become
            // int.MaxValue merely because the endpoint is unloaded.
            if (role == WorkerContainerRole.Import || role == WorkerContainerRole.Export)
            {
                WorkerStorageRuntime.ForEachLoadedContainer(count);

                bool hasPersistentEndpoint = false;
                bool hasUnboundedPersistentEndpoint = false;
                long persistentCapacity = 0;
                PersistentContainerRegistryRuntime.ForEachPersistent((candidate, candidateRole, owner, position) =>
                {
                    if (candidate == null || candidateRole != role) return;
                    if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                    hasPersistentEndpoint = true;
                    if (loadedIds.Contains(candidate.m_uid.ToString())) return;

                    if (role == WorkerContainerRole.Export)
                    {
                        hasUnboundedPersistentEndpoint = true;
                        return;
                    }

                    if (!ContainerStockLimitRuntime.HasRule(candidate, prefab))
                    {
                        // Preserve legacy behaviour for Import items without a configured rule:
                        // an unloaded endpoint may receive the carried load and make the hauler wait
                        // physically if it turns out to be full once the Base loads.
                        hasUnboundedPersistentEndpoint = true;
                        return;
                    }

                    persistentCapacity += ContainerStockLimitRuntime.GetPersistentAcceptCapacity(candidate, prefab);
                });

                if (hasUnboundedPersistentEndpoint) return int.MaxValue;
                total += persistentCapacity;
                if (total <= 0 && !hasPersistentEndpoint) return 0;
            }
            else
            {
                WorkerStorageRuntime.ForEachContainerNear(home, WorkerStorageRuntime.LogisticsNetworkRadius, count);
            }
            return total >= int.MaxValue ? int.MaxValue : (int)total;
        }

        internal static int GetRequestedFieldDepotFreeCapacity(Character worker, ZDO zdo, string prefab, Vector3 requestedPosition)
        {
            return GetRequestedFieldDepotFreeCapacity(worker, zdo, prefab, requestedPosition, ZDOID.None);
        }

        internal static int GetRequestedFieldDepotFreeCapacity(Character worker, ZDO zdo, string prefab, Vector3 requestedPosition, ZDOID requestedDepotId)
        {
            return GetRequestedFieldDepotFreeCapacity(worker, zdo, prefab, requestedPosition, requestedDepotId, string.Empty);
        }

        internal static int GetRequestedFieldDepotFreeCapacity(Character worker, ZDO zdo, string prefab, Vector3 requestedPosition, ZDOID requestedDepotId, string requestedWorkAreaId)
        {
            if (worker == null || zdo == null || string.IsNullOrEmpty(prefab)) return 0;
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);

            if (!string.IsNullOrEmpty(requestedWorkAreaId))
                return FieldSupplyDemandCacheRuntime.GetFreeCapacityInWorkArea(worker, zdo, prefab, requestedWorkAreaId);

            if (!requestedDepotId.Equals(ZDOID.None))
            {
                int exactCapacity = 0;
                WorkerStorageRuntime.ForEachLoadedContainer(c =>
                {
                    if (exactCapacity > 0 || c == null) return;
                    ZDO candidateZdo = WorkerContainerRoleRuntime.GetZdo(c);
                    if (candidateZdo == null || !candidateZdo.m_uid.Equals(requestedDepotId)) return;
                    if (!WorkerStorageRuntime.CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                    if (WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.FieldDepot) return;
                    Inventory inv = c.GetInventory();
                    if (inv == null) return;
                    int physical = WorkerStorageRuntime.GetAvailableCapacityForPrefab(inv, prefab);
                    exactCapacity = WorkerContainerRoleRuntime.SupportsAirTransport(c)
                        ? ContainerStockLimitRuntime.GetAcceptCapacity(c, prefab, physical)
                        : physical;
                });
                return Mathf.Max(0, exactCapacity);
            }

            long total = 0;
            Dictionary<string, int> groupPhysical = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, Container> groupRepresentative = new Dictionary<string, Container>(StringComparer.Ordinal);
            WorkerStorageRuntime.ForEachContainerNear(requestedPosition, 18f, c =>
            {
                if (!WorkerStorageRuntime.CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.FieldDepot) return;
                Inventory inv = c.GetInventory();
                if (inv == null) return;
                int physical = WorkerStorageRuntime.GetAvailableCapacityForPrefab(inv, prefab);
                if (WorkerContainerRoleRuntime.SupportsAirTransport(c) &&
                    AirFreightGroupRuntime.TryGetGroupId(c, out string groupId) && !string.IsNullOrEmpty(groupId))
                {
                    groupPhysical.TryGetValue(groupId, out int currentPhysical);
                    long combined = (long)currentPhysical + Mathf.Max(0, physical);
                    groupPhysical[groupId] = combined >= int.MaxValue ? int.MaxValue : (int)combined;
                    if (!groupRepresentative.ContainsKey(groupId)) groupRepresentative[groupId] = c;
                }
                else
                {
                    total += physical;
                }
            });

            foreach (KeyValuePair<string, int> pair in groupPhysical)
            {
                if (!groupRepresentative.TryGetValue(pair.Key, out Container representative) || representative == null) continue;
                total += ContainerStockLimitRuntime.GetAcceptCapacity(representative, prefab, pair.Value);
                if (total >= int.MaxValue) return int.MaxValue;
            }
            return total >= int.MaxValue ? int.MaxValue : (int)total;
        }

        internal static int CountRoleStock(Character worker, ZDO zdo, WorkerContainerRole role, string prefab)
        {
            if (worker == null || zdo == null || string.IsNullOrEmpty(prefab)) return 0;
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
            int total = 0;
            Action<Container> count = c =>
            {
                if (!WorkerStorageRuntime.CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(c) != role) return;
                total += WorkerStorageRuntime.CountPrefab(c.GetInventory(), prefab);
            };
            if (role == WorkerContainerRole.Import || role == WorkerContainerRole.Export)
                WorkerStorageRuntime.ForEachLoadedContainer(count);
            else
                WorkerStorageRuntime.ForEachContainerNear(home, WorkerStorageRuntime.LogisticsNetworkRadius, count);
            return total;
        }

        private static bool TryFindRoleDestination(Character worker, ZDO zdo, string prefab, WorkerContainerRole role, out Container destination, out Vector3 position)
        {
            destination = null; position = default;
            if (worker == null || zdo == null) return false;
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
            float best = float.MaxValue;
            Container bestDestination = null;
            Vector3 bestPosition = default;
            Action<Container> consider = c =>
            {
                if (!WorkerStorageRuntime.CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(c) != role || !WorkerStorageRuntime.HasLikelyRoomFor(c.GetInventory(), prefab)) return;
                if (role == WorkerContainerRole.Import)
                {
                    Inventory inv = c.GetInventory();
                    int physical = inv != null ? WorkerStorageRuntime.GetAvailableCapacityForPrefab(inv, prefab) : 0;
                    if (ContainerStockLimitRuntime.GetAcceptCapacity(c, prefab, physical) <= 0) return;
                }
                float sqr = (c.transform.position - worker.transform.position).sqrMagnitude;
                if (sqr >= best) return;
                best = sqr; bestDestination = c; bestPosition = c.transform.position;
            };
            if (role == WorkerContainerRole.Import || role == WorkerContainerRole.Export)
                WorkerStorageRuntime.ForEachLoadedContainer(consider);
            else
                WorkerStorageRuntime.ForEachContainerNear(home, WorkerStorageRuntime.LogisticsNetworkRadius, consider);
            destination = bestDestination; position = bestPosition;
            return destination != null;
        }

        private static Container FindNearbyRoleContainer(Character worker, Vector3 center, long ownerId, WorkerContainerRole role, string prefab, int minimumCount, bool requireRoom=false, bool requireExisting=false, bool allowRemoteFallback=false)
        {
            Container best = null; float bestSqr = float.MaxValue;
            WorkerStorageRuntime.ForEachContainerNear(center, Mathf.Max(6f, WorkerStorageRuntime.ContainerAccessRadius + 2f), c =>
            {
                if (!WorkerStorageRuntime.CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(c) != role) return;
                Inventory inv = c.GetInventory();
                if (inv == null) return;
                if (minimumCount > 0 && WorkerStorageRuntime.CountPrefab(inv, prefab) < minimumCount) return;
                if (requireExisting && !WorkerStorageRuntime.InventoryContainsPrefab(inv, prefab)) return;
                if (requireRoom && !WorkerStorageRuntime.HasLikelyRoomFor(inv, prefab)) return;
                if (requireRoom && role == WorkerContainerRole.Import)
                {
                    int physical = WorkerStorageRuntime.GetAvailableCapacityForPrefab(inv, prefab);
                    if (ContainerStockLimitRuntime.GetAcceptCapacity(c, prefab, physical) <= 0) return;
                }
                if (!allowRemoteFallback && !WorkerStorageRuntime.IsInsideContainerAura(worker.transform.position, c.transform.position)) return;
                float sqr = (c.transform.position - center).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr; best = c;
            });
            return best;
        }

        internal static bool TryRollbackSorterCargo(Character worker, ZDO zdo, string prefab, int amount, string route, Vector3 sourcePosition, out string status)
        {
            status = string.Empty;
            if (worker == null || zdo == null || string.IsNullOrEmpty(prefab) || amount <= 0) return false;

            WorkerContainerRole sourceRole;
            bool requireAir = false;
            if (string.Equals(route, RouteSortImport, StringComparison.Ordinal))
                sourceRole = WorkerContainerRole.Import;
            else if (string.Equals(route, AirHaulerWorkRuntime.RouteSortInbound, StringComparison.Ordinal))
            {
                sourceRole = WorkerContainerRole.FieldDepot;
                requireAir = true;
            }
            else
                return false;

            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Container best = null;
            float bestSqr = float.MaxValue;
            WorkerStorageRuntime.ForEachContainerNear(sourcePosition, 12f, c =>
            {
                if (c == null || !WorkerStorageRuntime.CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(c) != sourceRole) return;
                if (requireAir && (!WorkerContainerRoleRuntime.SupportsAirTransport(c) || WorkerContainerRoleRuntime.GetAirDepotMode(c) != AirDepotMode.BaseHub)) return;
                Inventory inv = c.GetInventory();
                if (inv == null || WorkerStorageRuntime.GetAvailableCapacityForPrefab(inv, prefab) < amount) return;
                float sqr = (c.transform.position - sourcePosition).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                best = c;
            });

            if (best == null || !WorkerStorageRuntime.TryOwn(best)) return false;
            Inventory destinationInventory = best.GetInventory();
            if (destinationInventory == null || !WorkerInventoryMutationGuard.CanWorkersMutate(destinationInventory)) return false;
            int returned = AddCargoMeasured(best, prefab, amount);
            if (returned <= 0) return false;

            WorkerZdo.RemoveCargoAmount(zdo, prefab, returned);
            if (WorkerZdo.HasCargo(zdo))
            {
                int remaining = WorkerZdo.GetCargoAmount(zdo, prefab);
                status = $"sorter rollback partial: returned {prefab} x{returned}; x{remaining} remains secured as worker cargo";
                WorkerStorageRuntime.SetDebug(worker, status);
                return true;
            }

            WorkerZdo.ClearCargo(zdo);
            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            status = $"sorter rollback: returned {prefab} x{returned} to source because destination was unavailable/full";
            WorkerStorageRuntime.SetDebug(worker, status);
            return true;
        }

        private static int RemoveItems(Inventory inv, string prefab, int amount)
        {
            int removed = 0;
            while (removed < amount)
            {
                ItemDrop.ItemData item = WorkerStorageRuntime.FindPrefab(inv, prefab);
                if (item == null || !WorkerStorageRuntime.TryRemoveOne(inv, item)) break;
                removed++;
            }
            return removed;
        }

        private static int AddCargoMeasured(Container destination, string prefab, int amount)
        {
            if (destination == null || string.IsNullOrEmpty(prefab) || amount <= 0) return 0;
            GameObject go = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null;
            if (go == null && ZNetScene.instance != null) go = ZNetScene.instance.GetPrefab(prefab);
            ItemDrop template = go != null ? go.GetComponent<ItemDrop>() : null;
            if (template?.m_itemData == null) return 0;
            Inventory inv = destination.GetInventory();
            if (inv == null) return 0;

            if (ContainerStockLimitRuntime.SupportsStockLimits(destination))
            {
                int physical = WorkerStorageRuntime.GetAvailableCapacityForPrefab(inv, prefab);
                int accepted = ContainerStockLimitRuntime.GetAcceptCapacity(destination, prefab, physical);
                amount = Mathf.Min(amount, accepted);
                if (amount <= 0) return 0;
            }

            int before = WorkerStorageRuntime.CountPrefab(inv, prefab);
            ItemDrop.ItemData data = template.m_itemData.Clone();
            data.m_dropPrefab = go;
            data.m_stack = amount;

            // Inventory.AddItem can legally merge only part of a requested stack when capacity
            // changes between planning and mutation. Measure the real inventory delta instead of
            // trusting the boolean return value.
            WorkerStorageRuntime.TryAddItem(inv, data);
            int after = WorkerStorageRuntime.CountPrefab(inv, prefab);
            int added = Mathf.Clamp(after - before, 0, amount);
            if (added <= 0) return 0;

            WorkerStorageRuntime.PersistContainer(destination, inv);
            return added;
        }

        private static Dictionary<string, int> ParseRequests(string raw)
        {
            Dictionary<string, int> result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw)) return result;
            string[] entries = raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < entries.Length; i++)
            {
                int eq = entries[i].LastIndexOf('=');
                if (eq <= 0) continue;
                string prefab = entries[i].Substring(0, eq).Trim();
                if (string.IsNullOrEmpty(prefab) || !int.TryParse(entries[i].Substring(eq + 1), out int target) || target <= 0) continue;
                result[prefab] = target;
            }
            return result;
        }

        private static string SerializeRequests(Dictionary<string, int> requests)
        {
            if (requests == null || requests.Count == 0) return string.Empty;
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> pair in requests)
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value > 0) parts.Add(pair.Key + "=" + pair.Value);
            return string.Join(";", parts);
        }
    }
}
