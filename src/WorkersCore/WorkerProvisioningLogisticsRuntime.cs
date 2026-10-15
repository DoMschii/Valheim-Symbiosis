using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Automatic Worker provisioning through the existing Base logistics network.
    ///
    /// v0.5.221 splits provisioning from sorting into its own Versorger task. A Versorger takes
    /// the exact thematic supply requested by a rest station from ordinary storage, carries real
    /// persistent Worker cargo to that HomeAnchor and increments the durable rest-station supply
    /// stock. No items are created for free.
    ///
    /// v0.5.222 extends that rule to remote Outposts: local normal chests / FieldDepots are consumed
    /// first. Only the shortage that cannot be covered locally is published to the Drake air network.
    ///
    /// The implementation intentionally uses the loaded container index + BackgroundWorkerScheduler
    /// registry and never performs a broad scene/world scan. Provisioning treats the assigned Base
    /// WorkArea as the logistics trust boundary, so ordinary chests in a shared Base remain usable
    /// even when an older/different character created the Piece.
    /// </summary>
    internal static class WorkerProvisioningLogisticsRuntime
    {
        internal const string RouteProvisionHome = "worker_provision_home";
        internal const string SourceTargetKind = "worker_provision_source";
        internal const string HomeDepositTargetKind = "worker_provision_home_deposit";
        internal const string ProvisionHomeZdoIdKey = "vw_provision_home_zdoid";

        // v0.5.219: durable outpost provisioning demand. This lives directly on the Outpost
        // AirFreight hub so the Base-side air network can see worker food/fuel shortages even while
        // the entire outpost is unloaded.
        internal const string OutpostAirProvisionDemandKey = "vw_worker_provision_air_demand_v1";
        // Ground-transport copy of Worker sustenance demand. This lives on a normal FieldDepot in
        // the Outpost WorkArea so early-game Greydwarf transporters can feed remote outposts before
        // Drake/AirFreight exists. It is deliberately separate from manual desired-stock rules.
        internal const string GroundProvisionDemandKey = "vw_worker_provision_ground_demand_v1";
        // Exact Base-side copy of Worker sustenance demand. This is deliberately separate from
        // the generic AirFreight manifest so a Versorger never mistakes a seed/material request
        // for food just because both happen to use the same prefab (e.g. Barley).
        internal const string BaseProvisionOrdersKey = "vw_worker_provision_base_orders_v1";
        // v0.5.246: Human-readable durable status on each Outpost AirFreight hub. This is kept
        // separate from the manifests so cartography/status UIs can explain whether food is
        // requested, already waiting in the depot, or merely waiting for a local Versorger.
        internal const string OutpostProvisionStatusKey = "vw_worker_provision_outpost_status_v1";
        internal const string OutpostProvisionWaitingDistributorKey = "vw_worker_provision_waiting_distributor_v1";
        private const string BackgroundProvisionNextTicksKey = "vw_worker_provision_bg_next_ticks";
        private const string BackgroundProvisionStatusKey = "vw_worker_provision_bg_status";
        private static readonly long BackgroundProvisionTripTicks = TimeSpan.FromSeconds(15.0).Ticks;
        // v0.5.229: Provisioning is event-driven. A complete Worker/container/outpost audit is
        // only a safety reconciliation once per Valheim day (~30 real-time minutes). Normal food
        // consumption updates the affected demand entry directly and never triggers a broad scan.
        private const float PersistentProvisionAuditSeconds = 30f * 60f;
        private const float CachedProvisionServiceSeconds = 4f;
        // Do not let dozens of rest stations whose 15 s clocks line up perform real ledger
        // transactions in one frame. One transfer per cached service pass is more than enough
        // for ~1 meal/worker/day and turns a periodic burst into tiny staggered work.
        private const int MaxBackgroundProvisionTransfersPerService = 1;
        // A local outpost should be able to sustain itself from storage built around its own hub.
        // Keep this deliberately much smaller than the global Base logistics radius so neighbouring
        // outposts cannot silently share supplies just because both are present in the world index.
        private const float PersistentLocalOutpostStorageRadius = 160f;
        private static float _nextPersistentProvisionAudit;
        private static float _nextCachedProvisionService;
        private static bool _persistentProvisionAuditRequested = true;

        // v0.5.228 introduced shared snapshots. v0.5.229 removes their short polling cadence:
        // the Worker-demand snapshot receives targeted updates when one Home changes and is only
        // fully reconciled once per Valheim day. Base-order snapshots remain small known-hub data.
        private const float DemandSnapshotAuditSeconds = 30f * 60f;
        private const float BaseOrderSnapshotAuditSeconds = 30f * 60f;
        private static float _nextDemandSnapshotAudit;
        private static float _nextBaseOrderSnapshotAudit;
        private static bool _demandSnapshotInitialized;
        private static bool _demandSnapshotAuditRequested = true;
        private static bool _baseOrderSnapshotInitialized;
        private static bool _baseOrderSnapshotAuditRequested = true;

        private sealed class OutpostHubState
        {
            internal ZDO Hub;
            internal Vector3 Position;
            internal long OwnerId;
            // Demand is the raw sum of rest-station deficits. AirDemand is the published remainder
            // after known local Outpost stock is reserved, so event updates never mutate the raw cache.
            internal readonly Dictionary<string, int> Demand = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, int> AirDemand = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, int> LocalSupply = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            internal readonly List<PersistentSupplySource> LocalSources = new List<PersistentSupplySource>(24);
        }

        private sealed class BaseHubState
        {
            internal ZDO Hub;
            internal long OwnerId;
        }

        private sealed class PersistentSupplySource
        {
            internal ZDO Zdo;
            internal WorkerContainerRole Role;
            internal long OwnerId;
            internal Vector3 Position;
            internal float DistanceSqr;
        }

        private sealed class PersistentHomeDemand
        {
            internal ZDO Worker;
            internal ZDO Home;
            internal OutpostHubState Hub;
            internal string Prefab;
            internal string Display;
            internal int Stock;
            internal int Target;
            internal bool Hungry;
        }

        private static readonly List<OutpostHubState> PersistentHubScratch = new List<OutpostHubState>(16);
        private static readonly List<BaseHubState> PersistentBaseHubScratch = new List<BaseHubState>(8);
        private static readonly List<PersistentHomeDemand> PersistentHomeDemandScratch = new List<PersistentHomeDemand>(64);
        private static readonly List<PersistentSupplySource> PersistentLocalSourceScratch = new List<PersistentSupplySource>(128);
        private static readonly HashSet<ZDOID> PersistentServicedHubScratch = new HashSet<ZDOID>();

        private sealed class ProvisionDemandSnapshotEntry
        {
            internal ZDO Worker;
            internal ZDO Home;
            internal ZDOID HomeId;
            internal long OwnerId;
            internal long HomeOwnerId;
            internal Vector3 HomePosition;
            internal string Prefab;
            internal string Display;
            internal int Stock;
            internal int Target;
            internal bool Hungry;
        }

        private sealed class BaseProvisionOrderSnapshotEntry
        {
            internal ZDO Hub;
            internal long OwnerId;
            internal Vector3 Position;
            internal Dictionary<string, int> Orders;
            internal Dictionary<string, int> GenericOrders;
        }

        private static readonly List<ProvisionDemandSnapshotEntry> ProvisionDemandSnapshot = new List<ProvisionDemandSnapshotEntry>(128);
        private static readonly List<BaseProvisionOrderSnapshotEntry> BaseProvisionOrderSnapshot = new List<BaseProvisionOrderSnapshotEntry>(16);
        private static readonly Dictionary<string, int> GroundProvisionDemandScratch = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ZDO> GroundProvisionAreaWorkerScratch = new Dictionary<string, ZDO>(StringComparer.Ordinal);

        private sealed class DemandCandidate
        {
            // Worker owning the rest-station demand. Kept explicitly because the source planner
            // needs the worker home fallback when an older/temporarily unloaded Home ZDO cannot
            // provide a position directly.
            internal ZDO Worker;
            internal ZDO Home;
            internal ZDOID HomeId;
            internal string Prefab;
            internal string Display;
            internal int Stock;
            internal int Target;
            internal bool Hungry;
            internal float DistanceSqr;
        }

        private static readonly List<DemandCandidate> DemandScratch = new List<DemandCandidate>(32);

        internal static void ClearProvisionTarget(ZDO logisticsZdo)
        {
            if (logisticsZdo == null) return;
            logisticsZdo.Set(ProvisionHomeZdoIdKey, ZDOID.None);
        }

        private static bool IsDirectProvisionDemandInSameWorkArea(ZDO logisticsZdo, ZDO demandWorker, Vector3 demandHomePosition)
        {
            if (logisticsZdo == null || demandWorker == null) return false;

            string logisticsAreaId = WorkerZdo.ReadWorkAreaId(logisticsZdo);
            string demandAreaId = WorkerZdo.ReadWorkAreaId(demandWorker);

            // v0.5.248: Physical Versorger delivery is strictly local to one assigned WorkArea.
            // Remote WorkAreas/outposts publish their shortage to transport logistics instead of
            // making a Base Versorger walk across the world to the remote rest station.
            if (!string.IsNullOrEmpty(logisticsAreaId) || !string.IsNullOrEmpty(demandAreaId))
                return !string.IsNullOrEmpty(logisticsAreaId) &&
                       !string.IsNullOrEmpty(demandAreaId) &&
                       string.Equals(logisticsAreaId, demandAreaId, StringComparison.Ordinal);

            // Legacy/migrated workers without an area id keep a conservative spatial fallback.
            if (WorkerZdo.TryReadWorkArea(logisticsZdo, out _, out Vector3 workCenter, out float workRadius))
            {
                float localRadius = Mathf.Max(15f, workRadius + 15f);
                return (demandHomePosition - workCenter).sqrMagnitude <= localRadius * localRadius;
            }

            Vector3 home = WorkerZdo.ReadHome(logisticsZdo, logisticsZdo.GetPosition());
            const float legacyLocalRadius = 60f;
            return (demandHomePosition - home).sqrMagnitude <= legacyLocalRadius * legacyLocalRadius;
        }

        internal static bool HasPendingDemand(Character logisticsWorker, ZDO logisticsZdo)
        {
            if (logisticsWorker == null || logisticsZdo == null) return false;

            long ownerId = logisticsZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);

            // Job selection only needs a yes/no. Do not build/sort DemandCandidate objects here;
            // the exact source/home selection happens once the Provision job actually starts.
            EnsureProvisionDemandSnapshot();
            for (int i = 0; i < ProvisionDemandSnapshot.Count; i++)
            {
                ProvisionDemandSnapshotEntry entry = ProvisionDemandSnapshot[i];
                if (entry == null || entry.Home == null || entry.Target <= 0 || entry.Stock >= entry.Target) continue;
                if (ownerId != 0L && entry.OwnerId != 0L && entry.OwnerId != ownerId) continue;
                if (ownerId != 0L && entry.HomeOwnerId != 0L && entry.HomeOwnerId != ownerId) continue;
                if (!IsDirectProvisionDemandInSameWorkArea(logisticsZdo, entry.Worker, entry.HomePosition)) continue;
                return true;
            }

            // Remote/outpost staging is TRANSPORT work, not local provisioning. A Worker with
            // only Versorgen enabled must never leave its WorkArea just because an outpost order
            // exists at the Base. Only expose the Base-side AirFreight staging demand when the
            // same worker also has Transport enabled.
            WorkerTaskFlags haulerTasks = WorkerJobProfileRuntime.ResolveEffectiveHaulerTasks(logisticsZdo);
            if ((haulerTasks & WorkerTaskFlags.Transport) == 0) return false;

            Vector3 center = WorkerZdo.ReadHome(logisticsZdo, logisticsWorker.transform.position);
            if (WorkerZdo.TryReadWorkArea(logisticsZdo, out _, out Vector3 workCenter, out _))
                center = workCenter;
            return TryFindBaseProvisioningOrder(logisticsZdo, center, out _, out _, out _, out _);
        }

        // Background equivalent used only for choosing the Provision module. It deliberately checks
        // durable rest-station demand only and does not scan containers; the actual transfer remains
        // owned by the persistent outpost provisioning tick / loaded physical Versorger.
        internal static bool HasPersistentDemand(ZDO logisticsZdo)
        {
            if (logisticsZdo == null) return false;
            long ownerId = logisticsZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Vector3 center = logisticsZdo.GetPosition();
            if (WorkerZdo.TryReadWorkArea(logisticsZdo, out _, out Vector3 workCenter, out _))
                center = workCenter;
            else
                center = WorkerZdo.ReadHome(logisticsZdo, center);

            EnsureProvisionDemandSnapshot();
            for (int i = 0; i < ProvisionDemandSnapshot.Count; i++)
            {
                ProvisionDemandSnapshotEntry entry = ProvisionDemandSnapshot[i];
                if (entry == null || entry.Worker == logisticsZdo || entry.Home == null || entry.Target <= 0 || entry.Stock >= entry.Target) continue;
                if (ownerId != 0L && entry.OwnerId != 0L && entry.OwnerId != ownerId) continue;
                if (!IsDirectProvisionDemandInSameWorkArea(logisticsZdo, entry.Worker, entry.HomePosition)) continue;
                return true;
            }

            // As above, Base-side AirFreight staging belongs to Transport. Pure Versorger workers
            // only service their local WorkArea, including while unloaded/background simulated.
            WorkerTaskFlags haulerTasks = WorkerJobProfileRuntime.ResolveEffectiveHaulerTasks(logisticsZdo);
            if ((haulerTasks & WorkerTaskFlags.Transport) == 0) return false;
            return TryFindBaseProvisioningOrder(logisticsZdo, center, out _, out _, out _, out _);
        }

        private static void EnsureProvisionDemandSnapshot()
        {
            float now = Time.realtimeSinceStartup;
            if (_demandSnapshotInitialized && !_demandSnapshotAuditRequested && now < _nextDemandSnapshotAudit) return;

            ProvisionDemandSnapshot.Clear();
            BackgroundWorkerScheduler.ForEachRegisteredWorker(workerZdo => AddOrReplaceDemandSnapshotEntry(workerZdo));

            _demandSnapshotInitialized = true;
            _demandSnapshotAuditRequested = false;
            _nextDemandSnapshotAudit = now + DemandSnapshotAuditSeconds;
            SyncAllGroundProvisionDemands();
        }

        private static void AddOrReplaceDemandSnapshotEntry(ZDO workerZdo)
        {
            if (!WorkerZdo.IsWorker(workerZdo)) return;
            if (!WorkerRestRuntime.HasAssignedHome(workerZdo)) return;
            if (!WorkerSustenanceRuntime.TryGetRequirement(workerZdo, out string prefab, out string display, out _)) return;

            ZDO home = WorkerSustenanceRuntime.ResolveHomeZdo(workerZdo);
            if (home == null) return;
            WorkerSustenanceRuntime.EnsureInitialized(workerZdo);
            int target = WorkerSustenanceRuntime.GetTargetStock(workerZdo);
            int stock = WorkerSustenanceRuntime.GetStock(workerZdo);
            if (target <= 0 || stock >= target) return;

            ProvisionDemandSnapshot.Add(new ProvisionDemandSnapshotEntry
            {
                Worker = workerZdo,
                Home = home,
                HomeId = home.m_uid,
                OwnerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L),
                HomeOwnerId = home.GetLong(WorkerHomeAnchorZdo.OwnerPlayerId, 0L),
                HomePosition = home.GetPosition(),
                Prefab = prefab,
                Display = display,
                Stock = stock,
                Target = target,
                Hungry = WorkerSustenanceRuntime.IsHungry(workerZdo)
            });
        }

        /// <summary>
        /// Targeted event path for one Worker/Home. This is the normal runtime path after eating,
        /// manual stock changes or a physical delivery: update one cached demand entry only.
        /// requestPersistentAudit is reserved for real topology changes where endpoint discovery
        /// may have changed. Stock/target edits and daily consumption stay fully event-driven and
        /// leave the broad persistent audit on its once-per-day cadence.
        /// </summary>
        internal static void NotifyWorkerSupplyChanged(ZDO workerZdo, bool requestPersistentAudit = false)
        {
            if (workerZdo == null) return;
            WorkzoneEconomyRuntime.Invalidate(WorkerZdo.ReadWorkAreaId(workerZdo));
            WorkzoneEconomyBackgroundRuntime.NotifyWorkerSupplyChanged(workerZdo);
            if (!_demandSnapshotInitialized)
            {
                _demandSnapshotAuditRequested = true;
            }
            else
            {
                ZDOID id = workerZdo.m_uid;
                for (int i = ProvisionDemandSnapshot.Count - 1; i >= 0; i--)
                {
                    ProvisionDemandSnapshotEntry entry = ProvisionDemandSnapshot[i];
                    if (entry?.Worker != null && entry.Worker.m_uid.Equals(id))
                        ProvisionDemandSnapshot.RemoveAt(i);
                }
                AddOrReplaceDemandSnapshotEntry(workerZdo);
            }

            UpdateCachedPersistentDemandForWorker(workerZdo);
            SyncGroundProvisionDemandForWorkArea(workerZdo);
            if (requestPersistentAudit) RequestPersistentProvisionAudit();
        }

        internal static void NotifyHomeSupplyChanged(ZDO homeZdo, bool requestPersistentAudit = false)
        {
            if (homeZdo == null) return;
            ZDO worker = null;
            ZDOID homeId = homeZdo.m_uid;

            for (int i = 0; i < ProvisionDemandSnapshot.Count && worker == null; i++)
            {
                ProvisionDemandSnapshotEntry entry = ProvisionDemandSnapshot[i];
                if (entry != null && entry.HomeId.Equals(homeId)) worker = entry.Worker;
            }
            for (int i = 0; i < PersistentHomeDemandScratch.Count && worker == null; i++)
            {
                PersistentHomeDemand entry = PersistentHomeDemandScratch[i];
                if (entry?.Home != null && entry.Home.m_uid.Equals(homeId)) worker = entry.Worker;
            }

            if (worker != null) NotifyWorkerSupplyChanged(worker, requestPersistentAudit);
            else if (requestPersistentAudit) RequestPersistentProvisionAudit();
        }

        internal static void NotifyWorkerTopologyChanged(ZDO workerZdo)
        {
            if (workerZdo != null) NotifyWorkerSupplyChanged(workerZdo, false);
            _demandSnapshotAuditRequested = true;
            RequestPersistentProvisionAudit();
        }

        internal static void RequestPersistentProvisionAudit()
        {
            _persistentProvisionAuditRequested = true;
            _baseOrderSnapshotAuditRequested = true;
        }

        /// <summary>
        /// Cached Base AirFreight hubs discovered by the daily/topology audit. Background air
        /// staging uses this instead of walking the entire persistent container registry every
        /// four seconds.
        /// </summary>
        internal static void ForEachCachedBaseAirHub(Action<ZDO, long, Vector3> visitor)
        {
            if (visitor == null) return;
            for (int i = 0; i < PersistentBaseHubScratch.Count; i++)
            {
                BaseHubState state = PersistentBaseHubScratch[i];
                if (state?.Hub == null) continue;
                visitor(state.Hub, state.OwnerId, state.Hub.GetPosition());
            }
        }

        /// <summary>
        /// Cached Outpost AirFreight hubs from the same daily/topology audit. Hot air-logistics
        /// paths use this list instead of rediscovering hubs through the persistent registry.
        /// </summary>
        internal static void ForEachCachedOutpostAirHub(Action<ZDO, long, Vector3> visitor)
        {
            if (visitor == null) return;
            for (int i = 0; i < PersistentHubScratch.Count; i++)
            {
                OutpostHubState state = PersistentHubScratch[i];
                if (state?.Hub == null) continue;
                visitor(state.Hub, state.OwnerId, state.Position);
            }
        }

        /// <summary>
        /// Cached non-air FieldDepots. Supply-target manifests change through explicit logistics
        /// events, while endpoint discovery is reconciled on topology changes / the daily audit.
        /// </summary>
        internal static void ForEachCachedFieldDepot(Action<ZDO, long, Vector3> visitor)
        {
            if (visitor == null) return;
            for (int i = 0; i < PersistentLocalSourceScratch.Count; i++)
            {
                PersistentSupplySource state = PersistentLocalSourceScratch[i];
                if (state?.Zdo == null || state.Role != WorkerContainerRole.FieldDepot) continue;
                visitor(state.Zdo, state.OwnerId, state.Position);
            }
        }

        /// <summary>
        /// All known FieldDepot endpoints, including the AirFreight variants. This preserves the
        /// legacy "nearest FieldDepot" behaviour for background jobs without a registry scan.
        /// </summary>
        internal static void ForEachCachedAnyFieldDepot(Action<ZDO, long, Vector3> visitor)
        {
            if (visitor == null) return;
            ForEachCachedFieldDepot(visitor);
            ForEachCachedOutpostAirHub(visitor);
            ForEachCachedBaseAirHub(visitor);
        }

        /// <summary>
        /// Finds real tracked Base stock from the already-cached persistent source list. Reads
        /// current ledger quantities, but performs no registry/world discovery scan.
        /// </summary>
        internal static bool TryFindCachedTrackedNormalSource(long ownerId, Vector3 basePosition, string prefab,
            out ZDO source, out int available)
        {
            source = null;
            available = 0;
            if (string.IsNullOrEmpty(prefab)) return false;

            float maxSqr = WorkerStorageRuntime.LogisticsNetworkRadius * WorkerStorageRuntime.LogisticsNetworkRadius;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < PersistentLocalSourceScratch.Count; i++)
            {
                PersistentSupplySource candidate = PersistentLocalSourceScratch[i];
                if (candidate?.Zdo == null || candidate.Role != WorkerContainerRole.Normal) continue;
                if (!candidate.Zdo.GetBool(ContainerLedgerRuntime.SorterTrackedKey, false)) continue;
                long trackedOwner = candidate.Zdo.GetLong(ContainerLedgerRuntime.SorterTrackedOwnerKey, candidate.OwnerId);
                if (trackedOwner != 0L && ownerId != 0L && trackedOwner != ownerId) continue;

                Vector3 position = candidate.Zdo.GetPosition();
                float sqr = (position - basePosition).sqrMagnitude;
                if (sqr > maxSqr) continue;
                int keepOverride = candidate.Zdo.GetInt(WorkerContainerRoleRuntime.KeepOneOverrideKey, -1);
                int minimumRemain = keepOverride == 0 ? 0 : 1;
                if (!ContainerLedgerRuntime.TryGetAvailable(candidate.Zdo, prefab, minimumRemain, out int movable) || movable <= 0) continue;
                if (movable < available && sqr >= bestSqr) continue;
                if (movable > available || sqr < bestSqr)
                {
                    source = candidate.Zdo;
                    available = movable;
                    bestSqr = sqr;
                }
            }
            return source != null && available > 0;
        }

        private static void AdjustCachedHubDemand(OutpostHubState hub, string prefab, int delta)
        {
            if (hub == null || string.IsNullOrWhiteSpace(prefab) || delta == 0) return;
            hub.Demand.TryGetValue(prefab, out int oldValue);
            long next = (long)oldValue + delta;
            if (next <= 0L) hub.Demand.Remove(prefab);
            else hub.Demand[prefab] = next >= int.MaxValue ? int.MaxValue : (int)next;
        }

        /// <summary>
        /// Updates the cached persistent Outpost demand for exactly one Worker. This is the key
        /// event-driven path: eating one berry changes one demand record and one hub manifest in
        /// memory; it never walks all Workers or all containers. The next cheap service pass
        /// publishes the revised manifest to the Outpost/Base AirFreight hubs.
        /// </summary>
        private static void UpdateCachedPersistentDemandForWorker(ZDO workerZdo)
        {
            if (workerZdo == null || PersistentHubScratch.Count == 0) return;

            PersistentHomeDemand existing = null;
            for (int i = 0; i < PersistentHomeDemandScratch.Count; i++)
            {
                PersistentHomeDemand candidate = PersistentHomeDemandScratch[i];
                if (candidate?.Worker != null && candidate.Worker.m_uid.Equals(workerZdo.m_uid))
                {
                    existing = candidate;
                    break;
                }
            }

            int oldDeficit = existing == null ? 0 : Math.Max(0, existing.Target - existing.Stock);
            OutpostHubState oldHub = existing?.Hub;
            string oldPrefab = existing?.Prefab ?? string.Empty;

            if (!WorkerZdo.IsWorker(workerZdo) ||
                !WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius) ||
                !WorkerRestRuntime.HasAssignedHome(workerZdo) ||
                !WorkerSustenanceRuntime.TryGetRequirement(workerZdo, out string prefab, out string display, out _))
            {
                if (oldDeficit > 0) AdjustCachedHubDemand(oldHub, oldPrefab, -oldDeficit);
                if (existing != null) { existing.Stock = 0; existing.Target = 0; }
                return;
            }

            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            OutpostHubState hub = FindHubForWorkArea(ownerId, center, radius);
            if (hub == null)
            {
                if (oldDeficit > 0) AdjustCachedHubDemand(oldHub, oldPrefab, -oldDeficit);
                if (existing != null) { existing.Stock = 0; existing.Target = 0; }
                return;
            }

            ZDO home = WorkerSustenanceRuntime.ResolveHomeZdo(workerZdo);
            if (home == null) return;
            WorkerSustenanceRuntime.EnsureInitialized(workerZdo);
            int target = WorkerSustenanceRuntime.GetTargetStock(workerZdo);
            int stock = WorkerSustenanceRuntime.GetStock(workerZdo);
            int newDeficit = target <= 0 ? 0 : Math.Max(0, target - stock);

            if (existing != null && (existing.Hub != hub || !string.Equals(existing.Prefab, prefab, StringComparison.OrdinalIgnoreCase)))
            {
                if (oldDeficit > 0) AdjustCachedHubDemand(oldHub, oldPrefab, -oldDeficit);
                oldDeficit = 0;
            }

            if (existing == null)
            {
                existing = new PersistentHomeDemand
                {
                    Worker = workerZdo,
                    Home = home,
                    Hub = hub,
                    Prefab = prefab,
                    Display = display
                };
                PersistentHomeDemandScratch.Add(existing);
            }

            int delta = newDeficit - oldDeficit;
            if (delta != 0) AdjustCachedHubDemand(hub, prefab, delta);

            existing.Worker = workerZdo;
            existing.Home = home;
            existing.Hub = hub;
            existing.Prefab = prefab;
            existing.Display = display;
            existing.Stock = stock;
            existing.Target = target;
            existing.Hungry = WorkerSustenanceRuntime.IsHungry(workerZdo);
        }

        private static void RebuildBaseProvisionOrderSnapshotFromCurrentHubScratch(float now)
        {
            BaseProvisionOrderSnapshot.Clear();
            for (int i = 0; i < PersistentBaseHubScratch.Count; i++)
            {
                BaseHubState state = PersistentBaseHubScratch[i];
                if (state?.Hub == null) continue;
                Dictionary<string, int> orders = ParseManifest(state.Hub.GetString(BaseProvisionOrdersKey, string.Empty));
                if (orders.Count == 0) continue;
                BaseProvisionOrderSnapshot.Add(new BaseProvisionOrderSnapshotEntry
                {
                    Hub = state.Hub,
                    OwnerId = state.OwnerId,
                    Position = state.Hub.GetPosition(),
                    Orders = orders,
                    GenericOrders = null
                });
            }
            // This is rebuilt from already-cached Base hubs; no persistent registry scan.
            _baseOrderSnapshotInitialized = true;
            _baseOrderSnapshotAuditRequested = false;
            _nextBaseOrderSnapshotAudit = now + BaseOrderSnapshotAuditSeconds;
        }

        internal static void RefreshBaseOrderGenericSnapshot()
        {
            for (int i = 0; i < BaseProvisionOrderSnapshot.Count; i++)
            {
                BaseProvisionOrderSnapshotEntry entry = BaseProvisionOrderSnapshot[i];
                if (entry?.Hub == null) continue;
                entry.Position = entry.Hub.GetPosition();
                entry.GenericOrders = ParseManifest(entry.Hub.GetString(WorkerLogisticsRuntime.BaseAirSupplyOrdersKey, string.Empty));
            }
            // Known hubs only; do not move the once-per-day registry audit deadline.
        }

        private static void EnsureBaseProvisionOrderSnapshot()
        {
            float now = Time.realtimeSinceStartup;
            if (_baseOrderSnapshotInitialized && !_baseOrderSnapshotAuditRequested && now < _nextBaseOrderSnapshotAudit) return;
            BaseProvisionOrderSnapshot.Clear();

            // Base hub discovery belongs to the provisioning topology audit. Job selection may run
            // many times per second, so it must never rediscover containers on its own.
            for (int i = 0; i < PersistentBaseHubScratch.Count; i++)
            {
                BaseHubState state = PersistentBaseHubScratch[i];
                if (state?.Hub == null) continue;
                Dictionary<string, int> orders = ParseManifest(state.Hub.GetString(BaseProvisionOrdersKey, string.Empty));
                if (orders.Count == 0) continue;
                BaseProvisionOrderSnapshot.Add(new BaseProvisionOrderSnapshotEntry
                {
                    Hub = state.Hub,
                    OwnerId = state.OwnerId,
                    Position = state.Hub.GetPosition(),
                    Orders = orders,
                    GenericOrders = ParseManifest(state.Hub.GetString(WorkerLogisticsRuntime.BaseAirSupplyOrdersKey, string.Empty))
                });
            }
            _baseOrderSnapshotInitialized = true;
            _baseOrderSnapshotAuditRequested = false;
            _nextBaseOrderSnapshotAudit = now + BaseOrderSnapshotAuditSeconds;
        }

        /// <summary>
        /// v0.5.219+: Publishes exact Worker-rest shortages onto the matching Outpost AirFreight
        /// hub and, while the outpost is outside the player simulation bubble, lets a locally
        /// assigned Versorger distribute real cargo to rest stations.
        /// v0.5.222 is local-first: ordinary Outpost storage is consumed before air freight is
        /// requested, allowing sufficiently developed outposts to become self-sustaining.
        /// Local storage -> Versorger -> rest station. Remote supply can arrive by ground Transporter
        /// first; Drake/AirFreight is the later high-speed transport tier.
        /// </summary>
        internal static void TickPersistentOutpostProvisioning()
        {
            float realtime = Time.realtimeSinceStartup;
            bool runAudit = _persistentProvisionAuditRequested || realtime >= _nextPersistentProvisionAudit;
            if (!runAudit && realtime < _nextCachedProvisionService) return;

            if (runAudit)
            {
                _persistentProvisionAuditRequested = false;
                _nextPersistentProvisionAudit = realtime + PersistentProvisionAuditSeconds;
                RebuildPersistentProvisionAudit(realtime);
            }

            _nextCachedProvisionService = realtime + CachedProvisionServiceSeconds;
            if (PersistentHubScratch.Count == 0) return;
            ServiceCachedPersistentProvisioning(realtime);
        }

        private static void RebuildPersistentProvisionAudit(float realtime)
        {
            PersistentHubScratch.Clear();
            PersistentBaseHubScratch.Clear();
            PersistentHomeDemandScratch.Clear();
            PersistentLocalSourceScratch.Clear();
            PersistentServicedHubScratch.Clear();

            // Expensive registry reconciliation: once per Valheim day, plus rare explicit topology
            // or manual changes. Normal meals/deliveries do not enter this path.
            PersistentContainerRegistryRuntime.ForEachPersistent((candidate, role, owner, position) =>
            {
                if (candidate == null) return;

                if (role == WorkerContainerRole.FieldDepot &&
                    candidate.GetBool(WorkerContainerRoleRuntime.AirTransportKey, false))
                {
                    AirDepotMode mode = (AirDepotMode)Mathf.Clamp(candidate.GetInt(WorkerContainerRoleRuntime.AirDepotModeKey, 0), 0, 1);
                    if (mode == AirDepotMode.Outpost)
                    {
                        PersistentHubScratch.Add(new OutpostHubState
                        {
                            Hub = candidate,
                            Position = position,
                            OwnerId = owner
                        });
                    }
                    else
                    {
                        PersistentBaseHubScratch.Add(new BaseHubState
                        {
                            Hub = candidate,
                            OwnerId = owner
                        });
                    }
                    return;
                }

                if (role != WorkerContainerRole.Normal && role != WorkerContainerRole.FieldDepot) return;
                // Empty FieldDepots can still carry desired-stock / AirFreight request policy and
                // therefore belong in the cached logistics topology. Ordinary chests are useful as
                // sources only after they have a real persistent inventory payload.
                if (role == WorkerContainerRole.Normal && !ContainerLedgerRuntime.HasPersistentInventoryPayload(candidate)) return;
                long effectiveOwner = owner;
                if (role == WorkerContainerRole.Normal && effectiveOwner == 0L)
                    effectiveOwner = candidate.GetLong(ContainerLedgerRuntime.SorterTrackedOwnerKey, 0L);
                PersistentLocalSourceScratch.Add(new PersistentSupplySource
                {
                    Zdo = candidate,
                    Role = role,
                    OwnerId = effectiveOwner,
                    Position = position
                });
            });

            PrepareCachedBaseNormalSources();

            if (PersistentHubScratch.Count == 0)
            {
                for (int i = 0; i < PersistentBaseHubScratch.Count; i++)
                {
                    ZDO baseHub = PersistentBaseHubScratch[i]?.Hub;
                    if (baseHub == null) continue;
                    baseHub.Set(BaseProvisionOrdersKey, string.Empty);
                    PersistentContainerRegistryRuntime.Remember(baseHub);
                }
                BaseProvisionOrderSnapshot.Clear();
                _baseOrderSnapshotInitialized = true;
                _baseOrderSnapshotAuditRequested = false;
                _nextBaseOrderSnapshotAudit = realtime + BaseOrderSnapshotAuditSeconds;
                return;
            }

            // One Worker pass discovers Outpost Versorger capability and builds the durable demand
            // cache. Between audits individual Home changes update this cache directly.
            BackgroundWorkerScheduler.ForEachRegisteredWorker(workerZdo =>
            {
                if (!WorkerZdo.IsWorker(workerZdo)) return;
                if (!WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius)) return;

                long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
                OutpostHubState hub = FindHubForWorkArea(ownerId, center, radius);
                if (hub == null) return;

                if (WorkerJobProfileRuntime.NormalizeRuntimeRole(workerZdo) == WorkerRole.Hauler &&
                    (WorkerJobProfileRuntime.ResolveEffectiveHaulerTasks(workerZdo) & WorkerTaskFlags.Provision) != 0)
                    PersistentServicedHubScratch.Add(hub.Hub.m_uid);

                if (!WorkerRestRuntime.HasAssignedHome(workerZdo)) return;
                if (!WorkerSustenanceRuntime.TryGetRequirement(workerZdo, out string prefab, out string display, out _)) return;

                ZDO home = WorkerSustenanceRuntime.ResolveHomeZdo(workerZdo);
                if (home == null) return;
                WorkerSustenanceRuntime.EnsureInitialized(workerZdo);
                int target = WorkerSustenanceRuntime.GetTargetStock(workerZdo);
                if (target <= 0) return;
                int stock = WorkerSustenanceRuntime.GetStock(workerZdo);
                int deficit = Math.Max(0, target - stock);
                if (deficit <= 0) return;

                AdjustCachedHubDemand(hub, prefab, deficit);
                PersistentHomeDemandScratch.Add(new PersistentHomeDemand
                {
                    Worker = workerZdo,
                    Home = home,
                    Hub = hub,
                    Prefab = prefab,
                    Display = display,
                    Stock = stock,
                    Target = target,
                    Hungry = WorkerSustenanceRuntime.IsHungry(workerZdo)
                });
            });

            BuildPersistentLocalOutpostSupplySnapshot();
        }

        private static void PrepareCachedBaseNormalSources()
        {
            if (PersistentBaseHubScratch.Count == 0 || PersistentLocalSourceScratch.Count == 0) return;
            float maxSqr = WorkerStorageRuntime.LogisticsNetworkRadius * WorkerStorageRuntime.LogisticsNetworkRadius;

            for (int i = 0; i < PersistentLocalSourceScratch.Count; i++)
            {
                PersistentSupplySource source = PersistentLocalSourceScratch[i];
                if (source?.Zdo == null || source.Role != WorkerContainerRole.Normal) continue;

                BaseHubState bestHub = null;
                float bestSqr = maxSqr;
                Vector3 sourcePosition = source.Zdo.GetPosition();
                for (int h = 0; h < PersistentBaseHubScratch.Count; h++)
                {
                    BaseHubState hub = PersistentBaseHubScratch[h];
                    if (hub?.Hub == null) continue;
                    float sqr = (hub.Hub.GetPosition() - sourcePosition).sqrMagnitude;
                    if (sqr > bestSqr) continue;
                    bestSqr = sqr;
                    bestHub = hub;
                }
                if (bestHub == null) continue;

                long ownerId = bestHub.OwnerId;
                if (ContainerLedgerRuntime.TryCaptureSorterStorageFromPersistentZdo(source.Zdo, ownerId, false))
                {
                    source.OwnerId = ownerId != 0L ? ownerId : source.OwnerId;
                    source.Position = sourcePosition;
                }
            }
        }

        private static void ServiceCachedPersistentProvisioning(float realtime)
        {
            // Small cached list only; no Worker/container/world scan. Keep urgent homes first.
            // Real inventory mutations are deliberately budgeted so many homes cannot synchronize
            // into one 15-second main-thread burst.
            int transferBudget = MaxBackgroundProvisionTransfersPerService;
            PersistentHomeDemandScratch.Sort((a, b) =>
            {
                if (a == null) return 1;
                if (b == null) return -1;
                if (a.Hungry != b.Hungry) return a.Hungry ? -1 : 1;
                long left = (long)a.Stock * Math.Max(1, b.Target);
                long right = (long)b.Stock * Math.Max(1, a.Target);
                return left.CompareTo(right);
            });

            long nowTicks = DateTime.UtcNow.Ticks;
            for (int i = 0; i < PersistentHomeDemandScratch.Count; i++)
            {
                PersistentHomeDemand demand = PersistentHomeDemandScratch[i];
                if (demand?.Home == null || demand.Hub?.Hub == null || demand.Target <= 0) continue;
                if (!PersistentServicedHubScratch.Contains(demand.Hub.Hub.m_uid))
                {
                    // No local Versorger must NOT suppress the order. Air freight is allowed to
                    // fill the Outpost depot first; the cargo simply waits there until a
                    // Provision-capable Hauler exists to carry it to the rest station.
                    int noHaulerStock = demand.Worker != null ? WorkerSustenanceRuntime.GetStock(demand.Worker) : WorkerSustenanceRuntime.GetHomeStock(demand.Home);
                    int noHaulerTarget = Math.Max(0, demand.Home.GetInt(WorkerSustenanceRuntime.HomeSupplyTargetKey, demand.Target));
                    int noHaulerNeed = Math.Max(0, noHaulerTarget - noHaulerStock);
                    int noHaulerLocalAvailable = GetCachedLocalOutpostSupplyAvailable(demand.Hub, demand.Prefab);
                    int depotAvailable = GetPersistentOutpostSupplyAvailable(demand.Hub.Hub, demand.Prefab);
                    int inTransit = BackgroundWorkerScheduler.GetAirOutboundInTransitAmount(demand.Hub.OwnerId, demand.Prefab, demand.Hub.Position);

                    if (depotAvailable > 0)
                        SetStringIfChanged(demand.Home, BackgroundProvisionStatusKey,
                            $"Lieferung wartet im Luftfrachtdepot: {demand.Display} x{depotAvailable}; kein Versorger");
                    else if (noHaulerLocalAvailable > 0)
                        SetStringIfChanged(demand.Home, BackgroundProvisionStatusKey,
                            $"Lokale Ware wartet auf Verteilung: {demand.Display} x{noHaulerLocalAvailable}; kein Versorger");
                    else if (noHaulerNeed > 0)
                        SetStringIfChanged(demand.Home, BackgroundProvisionStatusKey,
                            $"Luftfracht angefordert: {demand.Display} x{noHaulerNeed}" + (inTransit > 0 ? $" (unterwegs {inTransit})" : string.Empty) + "; kein Versorger");
                    continue;
                }

                if (BackgroundWorkerScheduler.ShouldUsePhysicalSimulation(demand.Home.GetPosition()) ||
                    BackgroundWorkerScheduler.ShouldUsePhysicalSimulation(demand.Hub.Position))
                    continue;

                long nextTicks = demand.Home.GetLong(BackgroundProvisionNextTicksKey, 0L);
                if (nextTicks > nowTicks) continue;

                int currentStock = demand.Worker != null ? WorkerSustenanceRuntime.GetStock(demand.Worker) : WorkerSustenanceRuntime.GetHomeStock(demand.Home);
                int currentTarget = Math.Max(0, demand.Home.GetInt(WorkerSustenanceRuntime.HomeSupplyTargetKey, demand.Target));
                demand.Stock = currentStock;
                demand.Target = currentTarget;
                int need = Math.Max(0, currentTarget - currentStock);
                if (need <= 0) continue;

                // Prefer the configured P1 food, but if an already-cached local/depot stock of
                // another enabled food exists, the unloaded Outpost may use that instead. The
                // fallback checks only known Home/Hub/source ZDOs and never discovers containers.
                TryResolveCachedOutpostFood(demand, out string deliveryPrefab, out string deliveryDisplay,
                    out int localAvailable, out int airHubAvailable);
                long combinedAvailableLong = (long)localAvailable + airHubAvailable;
                int available = (int)Math.Min(int.MaxValue, combinedAvailableLong);
                if (available <= 0)
                {
                    SetStringIfChanged(demand.Home, BackgroundProvisionStatusKey, $"Aussenposten wartet auf lokale Ware / Luftfracht: {demand.Display}");
                    continue;
                }

                if (transferBudget <= 0) continue;

                int requested = Math.Min(10, Math.Min(need, available));
                int movedLocal = TakePersistentLocalOutpostSupply(demand.Hub, deliveryPrefab, requested);
                int remainingRequest = Math.Max(0, requested - movedLocal);
                int movedAir = remainingRequest > 0
                    ? TakePersistentOutpostSupply(demand.Hub.Hub, deliveryPrefab, remainingRequest)
                    : 0;
                int moved = movedLocal + movedAir;
                if (moved <= 0) continue;

                transferBudget--;
                WorkerSustenanceRuntime.AddHomeSupply(demand.Home, deliveryPrefab, moved);
                NotifyWorkerSupplyChanged(demand.Worker, false);
                demand.Home.Set(BackgroundProvisionNextTicksKey, nowTicks + BackgroundProvisionTripTicks);
                string sourceText = movedLocal > 0 && movedAir > 0 ? "lokal + Luftfracht" : movedLocal > 0 ? "lokales Lager" : "Luftfracht";
                SetStringIfChanged(demand.Home, BackgroundProvisionStatusKey,
                    $"Aussenposten versorgt ({sourceText}): {deliveryDisplay} +{moved} ({currentStock + moved}/{currentTarget})");
            }

            // Publish only cached state. This is cheap and lets a single meal event become a Base
            // order within a few seconds without a Worker/container scan.
            for (int i = 0; i < PersistentHubScratch.Count; i++)
            {
                OutpostHubState hub = PersistentHubScratch[i];
                if (hub?.Hub == null) continue;

                hub.AirDemand.Clear();
                bool hasDistributor = PersistentServicedHubScratch.Contains(hub.Hub.m_uid);
                List<string> statusParts = new List<string>(hub.Demand.Count);
                foreach (KeyValuePair<string, int> pair in hub.Demand)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value <= 0) continue;

                    // Local chest stock is only useful for the rest station when a Versorger can
                    // physically distribute it. Without one, preserve the full rest-station
                    // deficit as an AirFreight request so a Drake may stage the food in the hub.
                    int local = GetCachedLocalOutpostSupplyAvailable(hub, pair.Key);
                    int depot = GetPersistentOutpostSupplyAvailable(hub.Hub, pair.Key);
                    int net = pair.Value;
                    if (hasDistributor && local > 0) net = Math.Max(0, net - local);
                    if (net > 0) hub.AirDemand[pair.Key] = net;

                    int transportNeed = Math.Max(0, net - depot);
                    int inTransit = BackgroundWorkerScheduler.GetAirOutboundInTransitAmount(hub.OwnerId, pair.Key, hub.Position);
                    string part = $"{pair.Key}: Bedarf {pair.Value}, lokal {local}, Depot {depot}";
                    if (transportNeed > 0) part += $", Luft {Math.Max(0, transportNeed - inTransit)}";
                    if (inTransit > 0) part += $", unterwegs {inTransit}";
                    statusParts.Add(part);
                }

                string serializedAirDemand = SerializeManifest(hub.AirDemand);
                bool changed = SetStringIfChanged(hub.Hub, OutpostAirProvisionDemandKey, serializedAirDemand);

                string status;
                if (statusParts.Count == 0)
                    status = "Versorgung gedeckt";
                else if (hasDistributor)
                    status = "Versorgung aktiv: " + string.Join(" | ", statusParts);
                else
                    status = "Kein Versorger - Luftfracht liefert trotzdem ins Depot: " + string.Join(" | ", statusParts);

                if (SetStringIfChanged(hub.Hub, OutpostProvisionStatusKey, status)) changed = true;
                bool waitingDistributor = !hasDistributor && statusParts.Count > 0;
                if (hub.Hub.GetBool(OutpostProvisionWaitingDistributorKey, false) != waitingDistributor)
                {
                    hub.Hub.Set(OutpostProvisionWaitingDistributorKey, waitingDistributor);
                    changed = true;
                }
                if (changed) PersistentContainerRegistryRuntime.Remember(hub.Hub);
            }

            for (int b = 0; b < PersistentBaseHubScratch.Count; b++)
            {
                BaseHubState baseState = PersistentBaseHubScratch[b];
                if (baseState?.Hub == null) continue;
                Dictionary<string, int> aggregate = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int h = 0; h < PersistentHubScratch.Count; h++)
                {
                    OutpostHubState outpost = PersistentHubScratch[h];
                    if (outpost?.Hub == null) continue;
                    if (baseState.OwnerId != 0L && outpost.OwnerId != 0L && baseState.OwnerId != outpost.OwnerId) continue;
                    foreach (KeyValuePair<string, int> pair in outpost.AirDemand)
                    {
                        if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value <= 0) continue;
                        aggregate.TryGetValue(pair.Key, out int existing);
                        long total = (long)existing + pair.Value;
                        aggregate[pair.Key] = total >= int.MaxValue ? int.MaxValue : (int)total;
                    }
                }
                string serializedBaseOrders = SerializeManifest(aggregate);
                if (SetStringIfChanged(baseState.Hub, BaseProvisionOrdersKey, serializedBaseOrders))
                    PersistentContainerRegistryRuntime.Remember(baseState.Hub);
            }

            RebuildBaseProvisionOrderSnapshotFromCurrentHubScratch(realtime);
        }

        private static bool TryResolveCachedOutpostFood(PersistentHomeDemand demand, out string prefab, out string display,
            out int localAvailable, out int depotAvailable)
        {
            prefab = demand?.Prefab ?? string.Empty;
            display = demand?.Display ?? prefab;
            localAvailable = 0;
            depotAvailable = 0;
            if (demand?.Hub == null || string.IsNullOrWhiteSpace(prefab)) return false;

            localAvailable = GetCachedLocalOutpostSupplyAvailable(demand.Hub, prefab);
            depotAvailable = GetPersistentOutpostSupplyAvailable(demand.Hub.Hub, prefab);
            if (localAvailable > 0 || depotAvailable > 0) return true;
            if (demand.Worker == null) return false;

            List<WorkerSustenanceRuntime.FoodOption> options = WorkerSustenanceRuntime.GetFoodOptions(demand.Worker);
            for (int i = 0; i < options.Count; i++)
            {
                WorkerSustenanceRuntime.FoodOption option = options[i];
                if (option == null || !option.Enabled || string.IsNullOrWhiteSpace(option.Prefab) ||
                    string.Equals(option.Prefab, prefab, StringComparison.OrdinalIgnoreCase))
                    continue;

                int local = GetCachedLocalOutpostSupplyAvailable(demand.Hub, option.Prefab);
                int depot = GetPersistentOutpostSupplyAvailable(demand.Hub.Hub, option.Prefab);
                if (local <= 0 && depot <= 0) continue;
                prefab = option.Prefab;
                display = string.IsNullOrWhiteSpace(option.DisplayName) ? option.Prefab : option.DisplayName;
                localAvailable = local;
                depotAvailable = depot;
                return true;
            }
            return false;
        }

        internal static int GetGroundProvisionTarget(ZDO depotZdo, string prefab)
        {
            if (depotZdo == null || string.IsNullOrWhiteSpace(prefab)) return 0;
            Dictionary<string, int> manifest = ParseManifest(depotZdo.GetString(GroundProvisionDemandKey, string.Empty));
            return manifest.TryGetValue(prefab, out int amount) ? Math.Max(0, amount) : 0;
        }

        internal static void CollectGroundProvisionPrefabs(ZDO depotZdo, List<string> target)
        {
            if (target == null) return;
            target.Clear();
            if (depotZdo == null) return;
            Dictionary<string, int> manifest = ParseManifest(depotZdo.GetString(GroundProvisionDemandKey, string.Empty));
            foreach (KeyValuePair<string, int> pair in manifest)
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value > 0) target.Add(pair.Key);
        }

        private static void SyncAllGroundProvisionDemands()
        {
            bool changed = false;

            // Full snapshot rebuilds are rare (startup/topology/daily safety audit). Clear only the
            // dedicated provisioning manifest; manual FieldDepot desired-stock policy is untouched.
            PersistentContainerRegistryRuntime.ForEachPersistent((candidate, role, owner, position) =>
            {
                if (candidate == null || role != WorkerContainerRole.FieldDepot ||
                    candidate.GetBool(WorkerContainerRoleRuntime.AirTransportKey, false)) return;
                if (string.IsNullOrEmpty(candidate.GetString(GroundProvisionDemandKey, string.Empty))) return;
                candidate.Set(GroundProvisionDemandKey, string.Empty);
                PersistentContainerRegistryRuntime.Remember(candidate);
                FieldSupplyDemandCacheRuntime.NotifyRequestChanged(candidate);
                changed = true;
            });

            GroundProvisionAreaWorkerScratch.Clear();
            for (int i = 0; i < ProvisionDemandSnapshot.Count; i++)
            {
                ProvisionDemandSnapshotEntry entry = ProvisionDemandSnapshot[i];
                if (entry?.Worker == null || entry.Target <= entry.Stock) continue;
                string areaId = WorkerZdo.ReadWorkAreaId(entry.Worker);
                if (string.IsNullOrWhiteSpace(areaId)) continue;
                long ownerId = entry.OwnerId;
                string key = ownerId.ToString() + "|" + areaId;
                if (!GroundProvisionAreaWorkerScratch.ContainsKey(key))
                    GroundProvisionAreaWorkerScratch[key] = entry.Worker;
            }

            foreach (ZDO worker in GroundProvisionAreaWorkerScratch.Values)
                if (SyncGroundProvisionDemandForWorkArea(worker, requestRescan: false)) changed = true;
            GroundProvisionAreaWorkerScratch.Clear();

            if (changed) BackgroundWorkerScheduler.RequestPersistentLogisticsRescan();
        }

        private static bool SyncGroundProvisionDemandForWorkArea(ZDO workerZdo, bool requestRescan = true)
        {
            if (workerZdo == null || !WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius)) return false;
            string workAreaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrWhiteSpace(workAreaId)) return false;
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);

            GroundProvisionDemandScratch.Clear();
            for (int i = 0; i < ProvisionDemandSnapshot.Count; i++)
            {
                ProvisionDemandSnapshotEntry entry = ProvisionDemandSnapshot[i];
                if (entry?.Worker == null || entry.Target <= entry.Stock || string.IsNullOrWhiteSpace(entry.Prefab)) continue;
                if (ownerId != 0L && entry.OwnerId != 0L && ownerId != entry.OwnerId) continue;
                string entryArea = WorkerZdo.ReadWorkAreaId(entry.Worker);
                if (!string.Equals(workAreaId, entryArea, StringComparison.Ordinal)) continue;
                int need = Math.Max(0, entry.Target - entry.Stock);
                if (need <= 0) continue;
                GroundProvisionDemandScratch.TryGetValue(entry.Prefab, out int previous);
                long total = (long)previous + need;
                GroundProvisionDemandScratch[entry.Prefab] = total >= int.MaxValue ? int.MaxValue : (int)total;
            }

            float allowed = Mathf.Max(4f, radius) + 15f;
            float allowedSqr = allowed * allowed;
            ZDO selected = null;
            float selectedSqr = float.MaxValue;
            List<ZDO> members = new List<ZDO>(4);

            PersistentContainerRegistryRuntime.ForEachPersistent((candidate, role, owner, position) =>
            {
                if (candidate == null || role != WorkerContainerRole.FieldDepot) return;
                if (candidate.GetBool(WorkerContainerRoleRuntime.AirTransportKey, false)) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                string depotArea = FieldDepotWorkzoneRuntime.GetAssignedWorkAreaId(candidate);
                if (!string.Equals(depotArea, workAreaId, StringComparison.Ordinal)) return;
                float dx = position.x - center.x;
                float dz = position.z - center.z;
                float sqr = dx * dx + dz * dz;
                if (sqr > allowedSqr) return;
                members.Add(candidate);
                if (sqr >= selectedSqr) return;
                selectedSqr = sqr;
                selected = candidate;
            });

            if (selected == null)
            {
                GroundProvisionDemandScratch.Clear();
                return false;
            }

            string serialized = SerializeManifest(GroundProvisionDemandScratch);
            bool changed = false;
            for (int i = 0; i < members.Count; i++)
            {
                ZDO member = members[i];
                if (member == null) continue;
                string desired = member == selected ? serialized : string.Empty;
                if (string.Equals(member.GetString(GroundProvisionDemandKey, string.Empty), desired, StringComparison.Ordinal)) continue;
                member.Set(GroundProvisionDemandKey, desired);
                PersistentContainerRegistryRuntime.Remember(member);
                FieldSupplyDemandCacheRuntime.NotifyRequestChanged(member);
                changed = true;
            }
            GroundProvisionDemandScratch.Clear();
            if (changed && requestRescan) BackgroundWorkerScheduler.RequestPersistentLogisticsRescan();
            return changed;
        }

        internal static int GetPersistentAirDemand(ZDO outpostHubZdo, string prefab)
        {
            if (outpostHubZdo == null || string.IsNullOrWhiteSpace(prefab)) return 0;
            Dictionary<string, int> manifest = ParseManifest(outpostHubZdo.GetString(OutpostAirProvisionDemandKey, string.Empty));
            return manifest.TryGetValue(prefab, out int amount) ? Math.Max(0, amount) : 0;
        }

        internal static void CollectPersistentAirDemandPrefabs(ZDO outpostHubZdo, List<string> target)
        {
            if (target == null) return;
            target.Clear();
            if (outpostHubZdo == null) return;
            Dictionary<string, int> manifest = ParseManifest(outpostHubZdo.GetString(OutpostAirProvisionDemandKey, string.Empty));
            foreach (KeyValuePair<string, int> pair in manifest)
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value > 0) target.Add(pair.Key);
        }

        internal static int GetPersistentAirTransportNeed(ZDO outpostHubZdo, string prefab)
        {
            int raw = GetPersistentAirDemand(outpostHubZdo, prefab);
            if (raw <= 0) return 0;
            int localAvailable = GetPersistentOutpostSupplyAvailable(outpostHubZdo, prefab);
            return Math.Max(0, raw - localAvailable);
        }

        private static OutpostHubState FindHubForWorkArea(long ownerId, Vector3 center, float radius)
        {
            float maxDistance = Mathf.Max(4f, radius) + 15f;
            float bestSqr = maxDistance * maxDistance;
            OutpostHubState best = null;
            for (int i = 0; i < PersistentHubScratch.Count; i++)
            {
                OutpostHubState hub = PersistentHubScratch[i];
                if (hub?.Hub == null) continue;
                if (hub.OwnerId != 0L && ownerId != 0L && hub.OwnerId != ownerId) continue;
                Vector3 delta = hub.Position - center;
                float sqr = delta.x * delta.x + delta.z * delta.z;
                if (sqr > bestSqr) continue;
                bestSqr = sqr;
                best = hub;
            }
            return best;
        }

        private static void BuildPersistentLocalOutpostSupplySnapshot()
        {
            for (int i = 0; i < PersistentHubScratch.Count; i++)
            {
                PersistentHubScratch[i].LocalSupply.Clear();
                PersistentHubScratch[i].LocalSources.Clear();
            }

            float maxSqr = PersistentLocalOutpostStorageRadius * PersistentLocalOutpostStorageRadius;
            for (int sourceIndex = 0; sourceIndex < PersistentLocalSourceScratch.Count; sourceIndex++)
            {
                PersistentSupplySource source = PersistentLocalSourceScratch[sourceIndex];
                ZDO candidate = source?.Zdo;
                if (candidate == null) continue;

                OutpostHubState bestHub = null;
                float bestSqr = maxSqr;
                for (int i = 0; i < PersistentHubScratch.Count; i++)
                {
                    OutpostHubState hub = PersistentHubScratch[i];
                    if (hub?.Hub == null || hub.Demand.Count == 0) continue;
                    if (hub.OwnerId != 0L && source.OwnerId != 0L && hub.OwnerId != source.OwnerId) continue;
                    Vector3 delta = source.Position - hub.Position;
                    float sqr = delta.x * delta.x + delta.z * delta.z;
                    if (sqr > bestSqr) continue;
                    bestSqr = sqr;
                    bestHub = hub;
                }
                if (bestHub == null) continue;

                source.DistanceSqr = bestSqr;
                bestHub.LocalSources.Add(source);

                foreach (KeyValuePair<string, int> demand in bestHub.Demand)
                {
                    if (demand.Value <= 0) continue;
                    int available = GetPersistentLocalSourceAvailable(candidate, source.Role, demand.Key);
                    if (available <= 0) continue;
                    bestHub.LocalSupply.TryGetValue(demand.Key, out int existing);
                    long total = (long)existing + available;
                    bestHub.LocalSupply[demand.Key] = total >= int.MaxValue ? int.MaxValue : (int)total;
                }
            }
        }

        private static int GetCachedLocalOutpostSupplyAvailable(OutpostHubState hub, string prefab)
        {
            if (hub == null || string.IsNullOrEmpty(prefab)) return 0;
            if (hub.LocalSupply.TryGetValue(prefab, out int cached) && cached > 0) return cached;

            // No world/registry scan: if the cached amount reached zero, re-check only the already
            // known local source ZDOs so newly added stock becomes visible between daily audits.
            long total = 0L;
            for (int i = 0; i < hub.LocalSources.Count; i++)
            {
                PersistentSupplySource source = hub.LocalSources[i];
                if (source?.Zdo == null) continue;
                total += Math.Max(0, GetPersistentLocalSourceAvailable(source.Zdo, source.Role, prefab));
                if (total >= int.MaxValue) { total = int.MaxValue; break; }
            }

            int amount = (int)total;
            if (amount > 0) hub.LocalSupply[prefab] = amount;
            return amount;
        }

        private static int GetPersistentLocalSourceAvailable(ZDO source, WorkerContainerRole role, string prefab)
        {
            if (source == null || string.IsNullOrEmpty(prefab)) return 0;
            int minimumRemain = WorkerContainerRoleRuntime.ProtectLastItem(source) ? 1 : 0;
            if (role == WorkerContainerRole.FieldDepot)
            {
                int requested = WorkerLogisticsRuntime.GetRequestedStock(source, prefab);
                int reserve = FieldDepotStockRuleRuntime.GetTransportReserve(source, prefab);
                minimumRemain = Math.Max(minimumRemain, Math.Max(requested, reserve));
            }
            return ContainerLedgerRuntime.TryGetAvailable(source, prefab, minimumRemain, out int available)
                ? Math.Max(0, available)
                : 0;
        }

        private static int TakePersistentLocalOutpostSupply(OutpostHubState hub, string prefab, int requested)
        {
            if (hub == null || string.IsNullOrEmpty(prefab) || requested <= 0) return 0;
            int remaining = requested;
            int taken = 0;

            for (int i = 0; i < hub.LocalSources.Count && remaining > 0; i++)
            {
                PersistentSupplySource source = hub.LocalSources[i];
                if (source?.Zdo == null) continue;
                int available = GetPersistentLocalSourceAvailable(source.Zdo, source.Role, prefab);
                if (available <= 0) continue;

                int want = Math.Min(remaining, available);
                int minimumRemain = WorkerContainerRoleRuntime.ProtectLastItem(source.Zdo) ? 1 : 0;
                if (source.Role == WorkerContainerRole.FieldDepot)
                {
                    int requestedStock = WorkerLogisticsRuntime.GetRequestedStock(source.Zdo, prefab);
                    int reserve = FieldDepotStockRuleRuntime.GetTransportReserve(source.Zdo, prefab);
                    minimumRemain = Math.Max(minimumRemain, Math.Max(requestedStock, reserve));
                }

                string tx = ContainerLedgerRuntime.CreateTransactionId();
                if (!ContainerLedgerRuntime.TryReserveRemoval(source.Zdo, tx, prefab, want, minimumRemain,
                        "background-local-outpost-provision", out int reserved, out _) || reserved <= 0)
                    continue;

                if (!ContainerLedgerRuntime.TryCommitRemoval(source.Zdo, tx,
                        out ContainerLedgerRuntime.TransactionInfo committed, out _) || committed == null || committed.Amount <= 0)
                {
                    ContainerLedgerRuntime.RollbackTransaction(source.Zdo, tx);
                    continue;
                }

                ContainerLedgerRuntime.CompleteTransaction(source.Zdo, tx);
                taken += committed.Amount;
                remaining -= committed.Amount;
            }

            if (taken > 0 && hub.LocalSupply.TryGetValue(prefab, out int cached))
            {
                int left = Math.Max(0, cached - taken);
                if (left > 0) hub.LocalSupply[prefab] = left;
                else hub.LocalSupply.Remove(prefab);
            }
            return taken;
        }

        private static int GetPersistentOutpostSupplyAvailable(ZDO hub, string prefab)
        {
            if (hub == null || string.IsNullOrEmpty(prefab)) return 0;
            int snapshot = 0;
            ContainerLedgerRuntime.TryGetCount(hub, prefab, out snapshot);
            int pending = AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(hub, prefab);
            int protectedAmount = GetPersistentProtectedAmount(hub, prefab);
            long total = (long)Math.Max(0, snapshot) + Math.Max(0, pending);
            return (int)Math.Min(int.MaxValue, Math.Max(0L, total - protectedAmount));
        }

        private static int TakePersistentOutpostSupply(ZDO hub, string prefab, int requested)
        {
            if (hub == null || string.IsNullOrEmpty(prefab) || requested <= 0) return 0;
            int available = GetPersistentOutpostSupplyAvailable(hub, prefab);
            int wanted = Math.Min(requested, available);
            if (wanted <= 0) return 0;

            int taken = 0;
            int pendingBefore = AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(hub, prefab);
            int pendingTake = Math.Min(wanted, pendingBefore);
            if (pendingTake > 0 && AdvancedFieldDepotRuntime.TryTakePendingBackgroundDelivery(hub, prefab, pendingTake, out int fromPending))
                taken += Math.Max(0, fromPending);

            int remaining = wanted - taken;
            if (remaining <= 0) return taken;

            int pendingAfter = AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(hub, prefab);
            int protectedAmount = GetPersistentProtectedAmount(hub, prefab);
            int minimumRemainInSnapshot = Math.Max(0, protectedAmount - pendingAfter);
            string tx = ContainerLedgerRuntime.CreateTransactionId();
            if (!ContainerLedgerRuntime.TryReserveRemoval(hub, tx, prefab, remaining, minimumRemainInSnapshot,
                    "background-outpost-provision", out int reserved, out _) || reserved <= 0)
                return taken;

            if (!ContainerLedgerRuntime.TryCommitRemoval(hub, tx,
                    out ContainerLedgerRuntime.TransactionInfo committed, out _) || committed == null || committed.Amount <= 0)
            {
                ContainerLedgerRuntime.RollbackTransaction(hub, tx);
                return taken;
            }

            taken += committed.Amount;
            ContainerLedgerRuntime.CompleteTransaction(hub, tx);
            return taken;
        }

        private static int GetPersistentProtectedAmount(ZDO depotZdo, string prefab)
        {
            if (depotZdo == null || string.IsNullOrEmpty(prefab)) return 0;
            int keep = WorkerContainerRoleRuntime.ProtectLastItem(depotZdo) ? 1 : 0;
            int requested = WorkerLogisticsRuntime.GetRequestedStock(depotZdo, prefab);
            int reserve = FieldDepotStockRuleRuntime.GetTransportReserve(depotZdo, prefab);
            return Math.Max(keep, Math.Max(requested, reserve));
        }

        private static bool SetStringIfChanged(ZDO zdo, string key, string value)
        {
            if (zdo == null || string.IsNullOrEmpty(key)) return false;
            string next = value ?? string.Empty;
            string previous = zdo.GetString(key, string.Empty);
            if (string.Equals(previous, next, StringComparison.Ordinal)) return false;
            zdo.Set(key, next);
            return true;
        }

        private static Dictionary<string, int> ParseManifest(string raw)
        {
            Dictionary<string, int> result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw)) return result;
            string[] entries = raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < entries.Length; i++)
            {
                int eq = entries[i].IndexOf('=');
                if (eq <= 0 || eq >= entries[i].Length - 1) continue;
                string prefab = entries[i].Substring(0, eq).Trim();
                if (string.IsNullOrWhiteSpace(prefab)) continue;
                if (!int.TryParse(entries[i].Substring(eq + 1), out int amount) || amount <= 0) continue;
                result[prefab] = amount;
            }
            return result;
        }

        private static string SerializeManifest(Dictionary<string, int> manifest)
        {
            if (manifest == null || manifest.Count == 0) return string.Empty;
            List<string> keys = new List<string>(manifest.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            System.Text.StringBuilder sb = new System.Text.StringBuilder(keys.Count * 18);
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                if (string.IsNullOrWhiteSpace(key) || !manifest.TryGetValue(key, out int amount) || amount <= 0) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(key).Append('=').Append(amount);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Finds a Base AirFreight order that specifically originates from Worker sustenance.
        /// Generic seeds/material requests stay Sorter work; food/fuel requests may additionally
        /// be claimed by a Versorger. The authoritative order already lives on the Base AirFreight
        /// hub, so this does not create a second queue or duplicate stock.
        /// </summary>
        private static bool TryFindBaseProvisioningOrder(ZDO logisticsZdo, Vector3 center,
            out ZDO baseHub, out Vector3 baseHubPosition, out string prefab, out int missing)
        {
            baseHub = null;
            baseHubPosition = default;
            prefab = string.Empty;
            missing = 0;
            if (logisticsZdo == null) return false;

            long ownerId = logisticsZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            float maxDistance = Mathf.Max(30f, WorkerStorageRuntime.LogisticsNetworkRadius);
            float maxDistanceSqr = maxDistance * maxDistance;
            float bestHubSqr = float.MaxValue;

            // Never capture out/ref parameters in the registry callback. Besides fixing CS1628,
            // using locals keeps the query allocation-free and lets the shared Base-order snapshot
            // avoid a full persistent-container walk for every Versorger decision.
            ZDO bestHub = null;
            Vector3 bestHubPosition = default;
            string bestPrefab = string.Empty;
            int bestMissing = 0;

            EnsureBaseProvisionOrderSnapshot();
            for (int i = 0; i < BaseProvisionOrderSnapshot.Count; i++)
            {
                BaseProvisionOrderSnapshotEntry entry = BaseProvisionOrderSnapshot[i];
                ZDO candidate = entry?.Hub;
                if (candidate == null || entry.Orders == null || entry.Orders.Count == 0) continue;
                if (entry.OwnerId != 0L && ownerId != 0L && entry.OwnerId != ownerId) continue;

                float hubSqr = (entry.Position - center).sqrMagnitude;
                if (hubSqr > maxDistanceSqr) continue;

                foreach (KeyValuePair<string, int> pair in entry.Orders)
                {
                    string requestedPrefab = pair.Key;
                    if (string.IsNullOrWhiteSpace(requestedPrefab) || pair.Value <= 0) continue;
                    if (!WorkerSustenanceRuntime.IsProvisioningPrefab(requestedPrefab)) continue;

                    int genericOrdered = 0;
                    if (entry.GenericOrders != null) entry.GenericOrders.TryGetValue(requestedPrefab, out genericOrdered);
                    int ordered = Mathf.Min(pair.Value, genericOrdered);
                    if (ordered <= 0) continue;
                    int staged = 0;
                    ContainerLedgerRuntime.TryGetCount(candidate, requestedPrefab, out staged);
                    staged += AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(candidate, requestedPrefab);
                    int need = Mathf.Max(0, ordered - staged);
                    if (need <= 0) continue;

                    if (need < bestMissing) continue;
                    if (need == bestMissing && hubSqr >= bestHubSqr) continue;
                    bestMissing = need;
                    bestPrefab = requestedPrefab;
                    bestHub = candidate;
                    bestHubPosition = entry.Position;
                    bestHubSqr = hubSqr;
                }
            }

            baseHub = bestHub;
            baseHubPosition = bestHubPosition;
            prefab = bestPrefab;
            missing = bestMissing;
            return baseHub != null && missing > 0 && !string.IsNullOrEmpty(prefab);
        }

        internal static bool IsBaseProvisioningOrder(ZDO baseHubZdo, string prefab)
        {
            if (baseHubZdo == null || string.IsNullOrWhiteSpace(prefab)) return false;
            Dictionary<string, int> orders = ParseManifest(baseHubZdo.GetString(BaseProvisionOrdersKey, string.Empty));
            return orders.TryGetValue(prefab, out int amount) && amount > 0;
        }

        internal static bool TryFindBaseProvisioningSource(Character logisticsWorker, ZDO logisticsZdo,
            out string prefab, out int amount, out Vector3 sourcePosition, out Vector3 baseHubPosition)
        {
            prefab = string.Empty;
            amount = 0;
            sourcePosition = default;
            baseHubPosition = default;
            if (logisticsWorker == null || logisticsZdo == null) return false;

            Vector3 center = WorkerZdo.ReadHome(logisticsZdo, logisticsWorker.transform.position);
            if (WorkerZdo.TryReadWorkArea(logisticsZdo, out _, out Vector3 workCenter, out _))
                center = workCenter;

            if (!TryFindBaseProvisioningOrder(logisticsZdo, center,
                    out _, out Vector3 hubPosition, out string requestedPrefab, out int missing))
                return false;

            if (!WorkerLogisticsRuntime.TryFindNormalSource(logisticsWorker, logisticsZdo, requestedPrefab, missing,
                    out _, out int usable, out Vector3 normalSourcePosition) || usable <= 0)
                return false;

            int destinationCapacity = AirHaulerWorkRuntime.GetSpecificAirDepotFreeCapacity(
                logisticsWorker, logisticsZdo, requestedPrefab, hubPosition);
            if (destinationCapacity <= 0) return false;

            int carry = HaulerWorkRuntime.GetCarryCapacity(logisticsWorker, requestedPrefab,
                Mathf.Min(missing, Mathf.Min(usable, destinationCapacity)));
            int take = Mathf.Min(missing, Mathf.Min(usable, Mathf.Min(destinationCapacity, carry)));
            if (take <= 0) return false;

            prefab = requestedPrefab;
            amount = take;
            sourcePosition = normalSourcePosition;
            baseHubPosition = hubPosition;
            return true;
        }

        internal static bool TryFindSource(Character logisticsWorker, ZDO logisticsZdo,
            out string prefab, out int amount, out Vector3 sourcePosition, out Vector3 homePosition,
            out ZDOID homeId, out string display)
        {
            prefab = string.Empty;
            amount = 0;
            sourcePosition = default;
            homePosition = default;
            homeId = ZDOID.None;
            display = string.Empty;

            if (!TryFindBestDemand(logisticsWorker, logisticsZdo, requireSource: true,
                out DemandCandidate demand, out Container source, out int usable,
                out Vector3 sourcePos, out int plannedAmount, out _))
                return false;

            prefab = demand.Prefab;
            amount = plannedAmount;
            sourcePosition = sourcePos;
            homePosition = demand.Home != null ? demand.Home.GetPosition() : WorkerZdo.ReadHome(demand.Worker, default);
            homeId = demand.HomeId;
            display = demand.Display;

            logisticsZdo.Set(ProvisionHomeZdoIdKey, homeId);
            WorkerZdo.SetCargoRoute(logisticsZdo, RouteProvisionHome, homePosition);
            return source != null && usable > 0 && amount > 0;
        }

        private static bool TryFindBestDemand(Character logisticsWorker, ZDO logisticsZdo, bool requireSource,
            out DemandCandidate selected, out Container source, out int usable, out Vector3 sourcePosition,
            out int plannedAmount, out string status)
        {
            selected = null;
            source = null;
            usable = 0;
            sourcePosition = default;
            plannedAmount = 0;
            status = string.Empty;
            if (logisticsWorker == null || logisticsZdo == null) return false;

            long ownerId = logisticsZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Vector3 networkCenter = WorkerZdo.TryReadWorkArea(logisticsZdo, out _, out Vector3 workCenter, out _)
                ? workCenter
                : WorkerZdo.ReadHome(logisticsZdo, logisticsWorker.transform.position);

            DemandScratch.Clear();
            EnsureProvisionDemandSnapshot();
            for (int i = 0; i < ProvisionDemandSnapshot.Count; i++)
            {
                ProvisionDemandSnapshotEntry entry = ProvisionDemandSnapshot[i];
                if (entry == null || entry.Worker == null || entry.Home == null) continue;
                if (ownerId != 0L && entry.OwnerId != 0L && entry.OwnerId != ownerId) continue;
                if (ownerId != 0L && entry.HomeOwnerId != 0L && entry.HomeOwnerId != ownerId) continue;
                if (entry.Target <= 0 || entry.Stock >= entry.Target) continue;
                if (!IsDirectProvisionDemandInSameWorkArea(logisticsZdo, entry.Worker, entry.HomePosition)) continue;

                float sqr = (entry.HomePosition - networkCenter).sqrMagnitude;

                DemandScratch.Add(new DemandCandidate
                {
                    Worker = entry.Worker,
                    Home = entry.Home,
                    HomeId = entry.HomeId,
                    Prefab = entry.Prefab,
                    Display = entry.Display,
                    Stock = entry.Stock,
                    Target = entry.Target,
                    Hungry = entry.Hungry,
                    DistanceSqr = sqr
                });
            }

            if (DemandScratch.Count == 0) return false;

            DemandScratch.Sort((a, b) =>
            {
                if (a.Hungry != b.Hungry) return a.Hungry ? -1 : 1;
                // Lowest fill percentage first. Cross multiplication avoids float churn.
                long left = (long)a.Stock * Math.Max(1, b.Target);
                long right = (long)b.Stock * Math.Max(1, a.Target);
                int fill = left.CompareTo(right);
                if (fill != 0) return fill;
                return a.DistanceSqr.CompareTo(b.DistanceSqr);
            });

            // A missing food type must not block all other homes. Try the most urgent demands in
            // order, but avoid broad repeated work: only already-indexed normal storage is queried.
            for (int i = 0; i < DemandScratch.Count; i++)
            {
                DemandCandidate candidate = DemandScratch[i];
                if (!requireSource)
                {
                    selected = candidate;
                    status = candidate.Hungry
                        ? $"hungry worker needs {candidate.Display}"
                        : $"rest supply low: {candidate.Display} {candidate.Stock}/{candidate.Target}";
                    return true;
                }

                int need = Math.Max(0, candidate.Target - candidate.Stock);
                if (need <= 0) continue;

                // v0.5.369: a Worker may accept several thematic foods. First test the reverse
                // item-location cache for ALL enabled choices in priority order. This is strictly
                // cache-only and cannot trigger one area rescan per food option. If none is known,
                // allow exactly one bounded miss-refresh for priority 1 (the same fallback the old
                // single-food system already had). No new polling/world scanner is introduced.
                System.Collections.Generic.List<WorkerSustenanceRuntime.FoodOption> foodOptions = WorkerSustenanceRuntime.GetFoodOptions(candidate.Worker);
                WorkerSustenanceRuntime.FoodOption preferredFood = null;
                for (int foodIndex = 0; foodIndex < foodOptions.Count; foodIndex++)
                {
                    WorkerSustenanceRuntime.FoodOption food = foodOptions[foodIndex];
                    if (food == null || !food.Enabled || string.IsNullOrWhiteSpace(food.Prefab)) continue;
                    if (preferredFood == null) preferredFood = food;
                    if (!TryFindProvisioningSource(logisticsWorker, logisticsZdo, food.Prefab, need, false,
                            out Container candidateSource, out int candidateUsable, out Vector3 candidateSourcePosition))
                        continue;

                    int carry = HaulerWorkRuntime.GetCarryCapacity(logisticsWorker, food.Prefab, Math.Min(need, candidateUsable));
                    int take = Math.Min(need, Math.Min(candidateUsable, carry));
                    if (take <= 0) continue;

                    candidate.Prefab = food.Prefab;
                    candidate.Display = food.DisplayName;
                    selected = candidate;
                    source = candidateSource;
                    usable = candidateUsable;
                    sourcePosition = candidateSourcePosition;
                    plannedAmount = take;
                    status = $"{candidate.Display} {candidate.Stock}/{candidate.Target} -> +{take}";
                    return true;
                }

                if (preferredFood != null &&
                    TryFindProvisioningSource(logisticsWorker, logisticsZdo, preferredFood.Prefab, need, true,
                        out Container refreshedSource, out int refreshedUsable, out Vector3 refreshedPosition))
                {
                    int carry = HaulerWorkRuntime.GetCarryCapacity(logisticsWorker, preferredFood.Prefab, Math.Min(need, refreshedUsable));
                    int take = Math.Min(need, Math.Min(refreshedUsable, carry));
                    if (take > 0)
                    {
                        candidate.Prefab = preferredFood.Prefab;
                        candidate.Display = preferredFood.DisplayName;
                        selected = candidate;
                        source = refreshedSource;
                        usable = refreshedUsable;
                        sourcePosition = refreshedPosition;
                        plannedAmount = take;
                        status = $"{candidate.Display} {candidate.Stock}/{candidate.Target} -> +{take}";
                        return true;
                    }
                }
            }

            return false;
        }

        internal static bool TryCollect(Character worker, ZDO logisticsZdo)
        {
            if (worker == null || logisticsZdo == null || WorkerZdo.HasCargo(logisticsZdo)) return false;
            if (!WorkerZdo.TryReadTarget(logisticsZdo, out string prefab, out string kind, out Vector3 targetPosition) ||
                !string.Equals(kind, SourceTargetKind, StringComparison.Ordinal))
                return false;

            ZDO home = ResolveProvisionHome(logisticsZdo);
            if (home == null)
            {
                CancelAndFindJob(logisticsZdo);
                return true;
            }

            WorkerSustenanceRuntime.EnsureHomeSupplyIdentity(home, prefab);
            int stock = WorkerSustenanceRuntime.GetHomeStock(home);
            int target = Math.Max(0, home.GetInt(WorkerSustenanceRuntime.HomeSupplyTargetKey, WorkerSustenanceRuntime.DefaultTargetStock));
            int need = Math.Max(0, target - stock);
            if (need <= 0)
            {
                CancelAndFindJob(logisticsZdo);
                return true;
            }

            Container source = FindSourceNear(worker, logisticsZdo, targetPosition, prefab);
            if (source == null)
            {
                CancelAndFindJob(logisticsZdo);
                WorkerStorageRuntime.SetDebug(worker, $"Versorgung: Quelle fuer {prefab} nicht mehr verfuegbar");
                return true;
            }

            // Once a physical Versorger actually uses an ordinary chest, keep a durable ledger
            // snapshot for it. That lets the same outpost continue feeding Workers after unloading.
            if (WorkerContainerRoleRuntime.GetRole(source) == WorkerContainerRole.Normal)
                ContainerLedgerRuntime.MarkSorterStorage(source, logisticsZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L));

            if (!WorkerStorageRuntime.TryOwn(source)) return true;
            Inventory inv = source.GetInventory();
            if (inv == null) return true;
            if (!WorkerInventoryMutationGuard.CanWorkersMutate(inv))
            {
                logisticsZdo.Set(WorkerZdo.State, (int)WorkerState.Collect);
                WorkerStorageRuntime.SetDebug(worker, $"Versorgung wartet: Lager wird gerade benutzt ({prefab})");
                return true;
            }

            int total = WorkerStorageRuntime.CountPrefab(inv, prefab);
            int protectedCount = GetProvisioningProtectedAmount(source, prefab);
            int removable = Math.Max(0, total - protectedCount);
            int carry = HaulerWorkRuntime.GetCarryCapacity(worker, prefab, Math.Min(removable, need));
            int requested = Math.Min(need, Math.Min(removable, carry));
            int removed = RemoveItems(inv, prefab, requested);
            if (removed <= 0)
            {
                CancelAndFindJob(logisticsZdo);
                return true;
            }

            WorkerStorageRuntime.PersistContainer(source, inv);
            if (ContainerLedgerRuntime.ShouldTrack(source))
                ContainerLedgerRuntime.SyncFromPhysical(source, "physical-provision-source");
            WorkerZdo.SetCargo(logisticsZdo, prefab, removed);
            WorkerZdo.SetCargoRoute(logisticsZdo, RouteProvisionHome, home.GetPosition());
            WorkerZdo.ClearTarget(logisticsZdo);
            logisticsZdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
            WorkerStorageRuntime.SetDebug(worker, $"Versorgung aufgenommen: {prefab} x{removed} -> Ruhestaette");
            return true;
        }

        internal static bool TryPrepareDeliveryTarget(Character worker, ZDO logisticsZdo, string prefab, int amount)
        {
            if (worker == null || logisticsZdo == null || string.IsNullOrEmpty(prefab) || amount <= 0) return false;
            ZDO home = ResolveProvisionHome(logisticsZdo);
            if (home == null)
                return ReturnCargoToBase(worker, logisticsZdo, prefab, "Versorgungs-Ruhestaette nicht mehr verfuegbar");

            WorkerSustenanceRuntime.EnsureHomeSupplyIdentity(home, prefab);
            if (!WorkerSustenanceRuntime.IsHomeSupplyEnabled(home, prefab))
                return ReturnCargoToBase(worker, logisticsZdo, prefab, "Versorgungsnahrung wurde an der Ruhestaette deaktiviert");
            int stock = WorkerSustenanceRuntime.GetHomeStock(home);
            int target = Math.Max(0, home.GetInt(WorkerSustenanceRuntime.HomeSupplyTargetKey, WorkerSustenanceRuntime.DefaultTargetStock));
            if (stock >= target)
                return ReturnCargoToBase(worker, logisticsZdo, prefab, "Versorgungsziel bereits voll");

            Vector3 position = home.GetPosition();
            WorkerZdo.WriteTarget(logisticsZdo, prefab, position, HomeDepositTargetKind);
            logisticsZdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
            return true;
        }

        internal static bool TryDeposit(Character worker, ZDO logisticsZdo)
        {
            if (worker == null || logisticsZdo == null) return false;
            if (!WorkerZdo.TryReadCargo(logisticsZdo, out string prefab, out int amount) || amount <= 0) return false;
            if (!WorkerZdo.TryReadTarget(logisticsZdo, out _, out string kind, out _) ||
                !string.Equals(kind, HomeDepositTargetKind, StringComparison.Ordinal))
                return false;

            ZDO home = ResolveProvisionHome(logisticsZdo);
            if (home == null)
                return ReturnCargoToBase(worker, logisticsZdo, prefab, "Ruhestaette beim Abliefern nicht verfuegbar");

            WorkerSustenanceRuntime.EnsureHomeSupplyIdentity(home, prefab);
            if (!WorkerSustenanceRuntime.IsHomeSupplyEnabled(home, prefab))
                return ReturnCargoToBase(worker, logisticsZdo, prefab, "Versorgungsnahrung wurde an der Ruhestaette deaktiviert");
            int stock = WorkerSustenanceRuntime.GetHomeStock(home);
            int target = Math.Max(0, home.GetInt(WorkerSustenanceRuntime.HomeSupplyTargetKey, WorkerSustenanceRuntime.DefaultTargetStock));
            int accepted = Math.Min(amount, Math.Max(0, target - stock));
            if (accepted > 0)
            {
                WorkerSustenanceRuntime.AddHomeSupply(home, prefab, accepted);
                NotifyHomeSupplyChanged(home, false);
            }

            if (accepted >= amount)
            {
                WorkerZdo.ClearCargo(logisticsZdo);
                WorkerZdo.ClearTarget(logisticsZdo);
                ClearProvisionTarget(logisticsZdo);
                logisticsZdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                WorkerStorageRuntime.SetDebug(worker, $"Versorgung geliefert: {prefab} x{accepted}; Ruhestaette {stock + accepted}/{target}");
                return true;
            }

            int remaining = amount - accepted;
            WorkerZdo.SetCargo(logisticsZdo, prefab, remaining);
            WorkerStorageRuntime.SetDebug(worker, $"Versorgung teilweise geliefert: {prefab} x{accepted}; Rest x{remaining} zurueck ins Lager");
            return ReturnCargoToBase(worker, logisticsZdo, prefab, "Versorgungsueberschuss");
        }

        private static bool ReturnCargoToBase(Character worker, ZDO logisticsZdo, string prefab, string reason)
        {
            ClearProvisionTarget(logisticsZdo);
            if (TryFindProvisioningDestination(worker, logisticsZdo, prefab, out _, out Vector3 position))
            {
                // Reuse the proven sorter inbound delivery route to put any stale/excess real cargo
                // back into matching NORMAL storage instead of ever deleting or dropping it.
                WorkerZdo.SetCargoRoute(logisticsZdo, WorkerLogisticsRuntime.RouteSortImport, position);
                WorkerZdo.WriteTarget(logisticsZdo, prefab, position, WorkerStorageRuntime.SortBaseDepositTargetKind);
                logisticsZdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
                WorkerStorageRuntime.SetDebug(worker, $"{reason}: {prefab} zurueck ins Basislager");
                return true;
            }

            WorkerZdo.ClearTarget(logisticsZdo);
            logisticsZdo.Set(WorkerZdo.State, (int)WorkerState.NeedsResource);
            WorkerStorageRuntime.SetDebug(worker, $"{reason}: {prefab} bleibt sicher auf dem Worker; kein passendes Basislager frei");
            return true;
        }

        private static ZDO ResolveProvisionHome(ZDO logisticsZdo)
        {
            if (logisticsZdo == null || ZDOMan.instance == null) return null;
            ZDOID id = logisticsZdo.GetZDOID(ProvisionHomeZdoIdKey);
            if (id.Equals(ZDOID.None)) return null;
            try { return ZDOMan.instance.GetZDO(id); }
            catch { return null; }
        }

        private static readonly HashSet<Container> ProvisioningContainerScratch = new HashSet<Container>();

        /// <summary>
        /// Finds food/fuel for Worker rest-station provisioning in ordinary Base chests.
        /// Unlike the generic sorter helper this searches around the assigned Base WorkArea first,
        /// then the Worker's HomeAnchor. The WorkArea is the explicit player-assigned trust boundary:
        /// normal chests there may be shared/legacy chests created by another character identity.
        /// Dedicated feeding stations are never treated as Worker supply storage.
        /// </summary>
        private static bool TryFindProvisioningSource(Character worker, ZDO logisticsZdo, string prefab, int preferredAmount, bool allowMissRefresh,
            out Container source, out int usable, out Vector3 position)
        {
            source = null;
            usable = 0;
            position = default;
            if (worker == null || logisticsZdo == null || string.IsNullOrEmpty(prefab)) return false;

            long ownerId = logisticsZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Vector3 workerPosition = worker.transform.position;
            Vector3 home = WorkerZdo.ReadHome(logisticsZdo, workerPosition);
            bool hasWorkArea = WorkerZdo.TryReadWorkArea(logisticsZdo, out _, out Vector3 workCenter, out float workRadius);
            float localRadius = hasWorkArea ? Mathf.Max(15f, workRadius + 15f) : 60f;
            Vector3 center = hasWorkArea ? workCenter : home;

            // v0.5.337: item-location cache. A Versorger requesting Blueberries now checks only
            // the cached Blueberry chests (plus the Outpost AirFreight buffer when local), instead
            // of scanning every chest once per hungry Home. A real area scan is permitted only on
            // a cache miss and is negatively cached for 15 seconds.
            bool found = allowMissRefresh
                ? SharedStorageStockIndexRuntime.TryFindItemSource(
                    prefab,
                    center,
                    localRadius,
                    workerPosition,
                    Math.Max(1, preferredAmount),
                    c => CanUseProvisioningBaseChest(c, ownerId, hasWorkArea, workCenter, localRadius),
                    c => GetProvisioningProtectedAmount(c, prefab),
                    out source,
                    out usable,
                    out position)
                : SharedStorageStockIndexRuntime.TryFindCachedItemSource(
                    prefab,
                    center,
                    localRadius,
                    workerPosition,
                    Math.Max(1, preferredAmount),
                    c => CanUseProvisioningBaseChest(c, ownerId, hasWorkArea, workCenter, localRadius),
                    c => GetProvisioningProtectedAmount(c, prefab),
                    out source,
                    out usable,
                    out position);

            if (found)
            {
                WorkerStorageRuntime.SetDebug(worker,
                    $"Versorgung: {prefab} aus Item-Cache; {SharedStorageStockIndexRuntime.GetCachedLocationCount(prefab)} bekannte Lagerorte");
                return true;
            }

            WorkerStorageRuntime.SetDebug(worker,
                allowMissRefresh
                    ? $"Versorgung: kein {prefab} im Item-Cache/gezielten Nachscan (Keep-One wird respektiert)"
                    : $"Versorgung: kein {prefab} im Item-Cache (alternative Nahrung ohne Nachscan)");
            return false;
        }

        private static bool TryFindProvisioningDestination(Character worker, ZDO logisticsZdo, string prefab,
            out Container destination, out Vector3 position)
        {
            destination = null;
            position = default;
            if (worker == null || logisticsZdo == null || string.IsNullOrEmpty(prefab)) return false;

            long ownerId = logisticsZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Vector3 workerPosition = worker.transform.position;
            Vector3 home = WorkerZdo.ReadHome(logisticsZdo, workerPosition);
            bool hasWorkArea = WorkerZdo.TryReadWorkArea(logisticsZdo, out _, out Vector3 workCenter, out float workRadius);
            float localRadius = hasWorkArea ? Mathf.Max(15f, workRadius + 15f) : 60f;
            Vector3 center = hasWorkArea ? workCenter : home;

            return SharedStorageStockIndexRuntime.TryFindItemDestination(
                prefab,
                center,
                localRadius,
                workerPosition,
                c =>
                {
                    if (!CanUseProvisioningBaseChest(c, ownerId, hasWorkArea, workCenter, localRadius)) return false;
                    if (WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.Normal) return false;
                    Inventory inv = c.GetInventory();
                    return inv != null && WorkerStorageRuntime.HasLikelyRoomFor(inv, prefab);
                },
                out destination,
                out position);
        }

        private static bool CanUseProvisioningBaseChest(Container candidate, long ownerId,
            bool hasWorkArea, Vector3 workCenter, float networkRadius)
        {
            if (candidate == null || candidate.GetInventory() == null) return false;
            if (LivestockFeedingRuntime.IsFeedingStation(candidate)) return false;

            WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(candidate);

            // v0.5.219: at remote outposts the AirFreight chest is the actual import buffer. A local
            // Versorger must be able to take Worker food/fuel from it and carry it to the
            // rest station. Base AirHubs are intentionally excluded so Base provisioning continues
            // to prefer ordinary storage and cannot drain staged outbound cargo.
            if (role == WorkerContainerRole.FieldDepot &&
                WorkerContainerRoleRuntime.SupportsAirTransport(candidate) &&
                WorkerContainerRoleRuntime.GetAirDepotMode(candidate) == AirDepotMode.Outpost)
            {
                if (!hasWorkArea) return false;
                if ((candidate.transform.position - workCenter).sqrMagnitude > networkRadius * networkRadius) return false;
                ZDO zdo = WorkerContainerRoleRuntime.GetZdo(candidate);
                if (zdo == null) return false;
                long containerOwner = zdo.GetLong(WorkerContainerRoleRuntime.OwnerKey, 0L);
                return containerOwner == 0L || ownerId == 0L || containerOwner == ownerId;
            }

            if (role != WorkerContainerRole.Normal) return false;

            // Normal ownership/access rules remain the preferred path.
            if (WorkerStorageRuntime.CanUseContainer(candidate, ownerId)) return true;

            // Shared bases and migrated saves often contain perfectly ordinary chests created by a
            // different character identity. When the player explicitly assigned this Sorter to the
            // Base WorkArea, accept such normal chests inside that logistics network as well.
            if (!hasWorkArea) return false;
            if ((candidate.transform.position - workCenter).sqrMagnitude > networkRadius * networkRadius) return false;
            return WorkerContainerRoleRuntime.GetZdo(candidate) != null;
        }

        private static int GetProvisioningProtectedAmount(Container container, string prefab)
        {
            if (container == null || string.IsNullOrEmpty(prefab)) return 0;
            int keep = WorkerContainerRoleRuntime.ProtectLastItem(container) ? 1 : 0;
            if (WorkerContainerRoleRuntime.GetRole(container) != WorkerContainerRole.FieldDepot) return keep;
            int requested = WorkerLogisticsRuntime.GetRequestedStock(container, prefab);
            int reserve = FieldDepotStockRuleRuntime.GetTransportReserve(container, prefab);
            return Math.Max(keep, Math.Max(requested, reserve));
        }

        private static Container FindSourceNear(Character worker, ZDO logisticsZdo, Vector3 center, string prefab)
        {
            if (worker == null || logisticsZdo == null) return null;
            long ownerId = logisticsZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            bool hasWorkArea = WorkerZdo.TryReadWorkArea(logisticsZdo, out _, out Vector3 workCenter, out float workRadius);
            float localRadius = hasWorkArea ? Mathf.Max(15f, workRadius + 15f) : 60f;
            Container best = null;
            float bestSqr = float.MaxValue;
            WorkerWorldIndex.ForEachContainer(center, 10f, candidate =>
            {
                if (!CanUseProvisioningBaseChest(candidate, ownerId, hasWorkArea, workCenter, localRadius)) return;
                Inventory inv = candidate.GetInventory();
                if (inv == null) return;
                int total = WorkerStorageRuntime.CountPrefab(inv, prefab);
                int protectedCount = GetProvisioningProtectedAmount(candidate, prefab);
                if (total - protectedCount <= 0) return;
                float sqr = (candidate.transform.position - center).sqrMagnitude;
                if (sqr >= bestSqr) return;
                best = candidate;
                bestSqr = sqr;
            });
            return best;
        }

        private static int RemoveItems(Inventory inv, string prefab, int requested)
        {
            if (inv == null || requested <= 0 || string.IsNullOrEmpty(prefab)) return 0;
            List<ItemDrop.ItemData> items = inv.GetAllItems();
            int remaining = requested;
            int removed = 0;
            for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
            {
                ItemDrop.ItemData item = items[i];
                if (item == null || item.m_stack <= 0 || !WorkerStorageRuntime.ItemMatchesPrefab(item, prefab)) continue;
                int take = Math.Min(remaining, item.m_stack);
                try
                {
                    using (WorkerInventoryMutationGuard.EnterWorkerMutation())
                        inv.RemoveItem(item, take);
                    removed += take;
                    remaining -= take;
                }
                catch { }
            }
            return removed;
        }

        private static void CancelAndFindJob(ZDO logisticsZdo)
        {
            if (logisticsZdo == null) return;
            WorkerZdo.ClearTarget(logisticsZdo);
            ClearProvisionTarget(logisticsZdo);
            logisticsZdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
        }
    }
}
