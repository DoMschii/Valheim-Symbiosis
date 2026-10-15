using System;
using System.Collections;
using System.Reflection;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Conservative background adapter for the Sort job. While loaded it snapshots the next real
    /// physical sorter transfer (source, destination, item and one or more carry batches). While the
    /// zone is unloaded it only advances time and schedules that already validated transfer. On the
    /// next load the transfer is reconciled against the real inventories before anything is moved.
    /// No real source stock => no transfer, no destination room => no transfer.
    /// </summary>
    internal static class BackgroundSorterRuntime
    {
        private const string Initialized = "vw_bg_sort_init";
        private const string PlanKey = "vw_bg_sort_plan";
        private const string DebugKey = "vw_bg_sort_debug";
        private const string LastSimTicks = "vw_bg_sort_last_sim_ticks";
        private const float LoadedSnapshotInterval = 6f;
        private const float SecondsPerTrip = 15f;
        private const string AirStageFractionKey = "vw_bg_sort_air_fraction";
        private const string AirHubStageFractionKey = "vw_bg_air_hub_stage_fraction";
        private const string AirHubStageLastTickKey = "vw_bg_air_hub_stage_last_ticks";
        private const string AirHubStageStatusKey = "vw_bg_air_hub_stage_status";
        private const float AirHubPhysicalActivityRadius = 180f;

        private static readonly Dictionary<string, float> NextSnapshot =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<ZDOID, float> NextRemoteBaseStorageBootstrap = new Dictionary<ZDOID, float>();
        // One completed remote Base bootstrap is enough for the current world session. Container
        // lifecycle hooks keep newly loaded/changed chests current afterwards. Re-running the same
        // sector query walked ~10k ZDOs for the observed Base even though the result was unchanged.
        private static readonly HashSet<ZDOID> CompletedRemoteBaseStorageBootstrap = new HashSet<ZDOID>();
        private static readonly List<ZDO> RemoteSectorNearbyScratch = new List<ZDO>(256);
        private static readonly List<ZDO> RemoteSectorDistantScratch = new List<ZDO>(256);
        private static readonly HashSet<ZDOID> RemoteSectorSeenScratch = new HashSet<ZDOID>();
        private static float _nextPersistentAirStagingRealtime;
        private static readonly List<Character> PhysicalWorkerScratch = new List<Character>();
        // A completed remote-storage scan is a durable negative cache. Loaded chest changes are
        // registered immediately through Container.Awake/inventory reconciliation, so repeatedly
        // rescanning an unchanged unloaded Base every 20 seconds only creates frame spikes.
        private const float RemoteBaseStorageRetrySeconds = 120f;
        private const float PersistentAirStagingTickSeconds = 4f;

        private sealed class SortPlan
        {
            internal string Prefab = string.Empty;
            internal string SourceKind = string.Empty;
            internal WorkerContainerRole SourceRole;
            internal WorkerContainerRole DestinationRole;
            internal bool SourceAir;
            internal AirDepotMode SourceAirMode;
            internal bool DestinationAir;
            internal AirDepotMode DestinationAirMode;
            internal bool ProtectOne;
            internal Vector3 SourcePosition;
            internal Vector3 DestinationPosition;
            internal int BatchSize;
            internal int Budget;
            internal int Pending;
            internal float Fraction;
        }

        private sealed class RemoteStorageScanStats
        {
            internal int Visited;
            internal int NormalCandidates;
            internal int Captured;
            internal int RequestedSnapshots;
            internal int RequestedTotal;
            internal int RequestedMovable;
            internal string RequestedPrefab = string.Empty;
            internal bool UsedSectorQuery;
            internal bool Throttled;
            internal bool Deferred;
            internal ZDO RequestedSource;
            internal readonly HashSet<ZDOID> Seen = new HashSet<ZDOID>();

            public override string ToString()
            {
                string requested = string.IsNullOrEmpty(RequestedPrefab)
                    ? string.Empty
                    : $", {RequestedPrefab}:snapshots={RequestedSnapshots}, total={RequestedTotal}, movable={RequestedMovable}";
                string state = Throttled ? ", throttled=true" : Deferred ? ", deferred=true" : string.Empty;
                return $"scan={(UsedSectorQuery ? "sector" : "fallback")}, visited={Visited}, inventoryCandidates={NormalCandidates}, captured={Captured}{requested}{state}";
            }
        }

        internal static void CaptureLoadedNormalStorageNearBaseHub(Container hub)
        {
            if (hub == null || !WorkerContainerRoleRuntime.SupportsAirTransport(hub) ||
                WorkerContainerRoleRuntime.GetAirDepotMode(hub) != AirDepotMode.BaseHub) return;

            ZDO hubZdo = WorkerContainerRoleRuntime.GetZdo(hub);
            if (hubZdo == null) return;
            long ownerId = hubZdo.GetLong(WorkerContainerRoleRuntime.OwnerKey, 0L);
            Vector3 center = hub.transform.position;

            // Persist real snapshots while the Base is physically loaded. The unloaded simulation
            // can then consume those exact stocks later without needing a physical Sorter to wake up.
            WorkerStorageRuntime.ForEachContainerNear(center, WorkerStorageRuntime.LogisticsNetworkRadius, c =>
            {
                if (c == null || c == hub || WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.Normal) return;
                if (!WorkerStorageRuntime.CanUseContainer(c, ownerId)) return;
                ContainerLedgerRuntime.MarkSorterStorage(c, ownerId);
            });
        }

        internal static void TickLoaded(Character worker, ZDO workerZdo)
        {
            if (worker == null || workerZdo == null || !WorkerZdo.IsWorker(workerZdo)) return;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out WorkOrder order, out _, out _) || order != WorkOrder.Sort) return;

            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId)) return;

            CaptureNormalBaseStorage(worker, workerZdo);
            if (!WorkFlagRegistry.TryGetNetworkState(areaId, 200f, out _, out ZDO flagZdo) || flagZdo == null) return;
            WorkFlagRegistry.RememberPersistent(flagZdo);

            SortPlan existing = Load(flagZdo);
            if (existing != null && existing.Pending > 0)
            {
                int moved = ReconcilePending(worker, workerZdo, existing);
                if (moved > 0) existing.Pending = Mathf.Max(0, existing.Pending - moved);
                Save(flagZdo, existing);
            }

            if (NextSnapshot.TryGetValue(areaId, out float next) && Time.realtimeSinceStartup < next)
            {
                flagZdo.Set(DebugKey, existing != null ? BuildSummary("loaded", existing) : "sorting:loaded/no-plan");
                return;
            }
            NextSnapshot[areaId] = Time.realtimeSinceStartup + LoadedSnapshotInterval;

            // Physical AI owns loaded zones. We only snapshot a future safe transfer here.
            SortPlan plan = BuildPlan(worker, workerZdo);
            flagZdo.Set(Initialized, plan != null);
            flagZdo.Set(LastSimTicks, DateTime.UtcNow.Ticks);
            Save(flagZdo, plan);
            flagZdo.Set(DebugKey, plan != null ? BuildSummary("loaded", plan) : "sorting:loaded/no-work");
        }

        internal static string TickBackground(ZDO workerZdo, double deltaSeconds, float efficiency)
        {
            if (workerZdo == null) return "sorting:no-worker";

            // Air-supply staging is now driven once per scheduler tick by the persistent Base
            // AirFreight hub (TickPersistentAirStaging). Do not also stage it from an individual
            // background Sorter worker or two simulation paths could advance the same order.
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);

            // Generic deferred SortPlan simulation still belongs to a concrete WorkFlag because
            // that flag owns the persisted plan/snapshot state. Only this legacy path is gated by
            // the flag now.
            if (string.IsNullOrEmpty(areaId)) return "sorting:no-workzone";
            if (!WorkFlagRegistry.TryGetPersistentState(areaId, out ZDO flagZdo) || flagZdo == null) return "sorting:no-ledger-flag";
            if (!flagZdo.GetBool(Initialized, false)) return "sorting:no-snapshot";

            SortPlan plan = Load(flagZdo);
            if (plan == null || string.IsNullOrEmpty(plan.Prefab) || plan.Budget <= 0 || plan.BatchSize <= 0)
                return "sorting:no-plan";

            long now = DateTime.UtcNow.Ticks;
            long last = flagZdo.GetLong(LastSimTicks, 0L);
            double dt = last > 0L ? TimeSpan.FromTicks(Math.Max(0L, now - last)).TotalSeconds : Math.Max(0.0, deltaSeconds);
            dt = Math.Min(3600.0, dt);
            flagZdo.Set(LastSimTicks, now);

            int unscheduled = Mathf.Max(0, plan.Budget - plan.Pending);
            if (unscheduled > 0 && dt > 0.0)
            {
                plan.Fraction += (float)(dt * Mathf.Clamp01(efficiency) / SecondsPerTrip);
                int trips = Mathf.FloorToInt(plan.Fraction);
                if (trips > 0)
                {
                    long units = (long)trips * Mathf.Max(1, plan.BatchSize);
                    int scheduled = Mathf.Min(unscheduled, units > int.MaxValue ? int.MaxValue : (int)units);
                    plan.Pending += scheduled;
                    plan.Fraction -= trips;
                }
            }

            Save(flagZdo, plan);
            string summary = BuildSummary("background", plan);
            flagZdo.Set(DebugKey, summary);
            return summary;
        }

        internal static bool HasPersistentSortWork(ZDO workerZdo)
        {
            if (workerZdo == null) return false;
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId)) return false;
            if (!WorkFlagRegistry.TryGetPersistentState(areaId, out ZDO flagZdo) || flagZdo == null) return false;
            if (!flagZdo.GetBool(Initialized, false)) return false;

            SortPlan plan = Load(flagZdo);
            return plan != null &&
                   !string.IsNullOrEmpty(plan.Prefab) &&
                   plan.BatchSize > 0 &&
                   plan.Budget > 0;
        }

        internal static string GetDebugSummary(ZDO workerZdo)
        {
            if (workerZdo == null) return "sorting:none";
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId) || !WorkFlagRegistry.TryGetPersistentState(areaId, out ZDO flagZdo) || flagZdo == null)
                return "sorting:no-flag";
            return flagZdo.GetString(DebugKey, flagZdo.GetBool(Initialized, false) ? "sorting:ready" : "sorting:no-snapshot");
        }

        internal static void TickPersistentAirStaging()
        {
            float nowRealtime = Time.realtimeSinceStartup;
            if (nowRealtime < _nextPersistentAirStagingRealtime) return;
            _nextPersistentAirStagingRealtime = nowRealtime + PersistentAirStagingTickSeconds;

            long nowTicks = DateTime.UtcNow.Ticks;
            bool heavyAirOrderClaimed = false;

            // v0.5.229: Base hubs come from the provisioning/logistics cache that is rebuilt
            // on topology changes and by the daily safety audit. Do not walk every persistent
            // container every four seconds just to rediscover the same handful of Base hubs.
            WorkerProvisioningLogisticsRuntime.ForEachCachedBaseAirHub((hub, ownerId, hubPosition) =>
            {
                if (hub == null) return;

                // Physical simulation wins only when a real loaded Sorter is actually present and
                // under player-near physical authority. Player distance to the hub alone is not
                // sufficient: the Character may still be unloaded, which previously disabled both
                // physical and background staging at the same time.
                if (HasActivePhysicalSorter(hubPosition, ownerId))
                {
                    hub.Set(AirHubStageLastTickKey, nowTicks);
                    hub.Set(AirHubStageFractionKey, 0f);
                    SetAirHubStageStatus(hub, "sorting:bg hub/physical-zone-active");
                    return;
                }

                long lastTicks = hub.GetLong(AirHubStageLastTickKey, 0L);
                double deltaSeconds = lastTicks > 0L
                    ? TimeSpan.FromTicks(Math.Max(0L, nowTicks - lastTicks)).TotalSeconds
                    : 1.0;
                deltaSeconds = Math.Min(30.0, Math.Max(0.0, deltaSeconds));
                hub.Set(AirHubStageLastTickKey, nowTicks);

                if (!WorkerLogisticsRuntime.TryFindBaseAirStagingOrder(ownerId, hubPosition,
                        out ZDO selectedHub, out Vector3 selectedHubPosition, out string prefab, out int missing) ||
                    selectedHub == null || selectedHub.m_uid != hub.m_uid || missing <= 0 || string.IsNullOrEmpty(prefab))
                {
                    hub.Set(AirHubStageFractionKey, 0f);
                    SetAirHubStageStatus(hub, "sorting:bg hub/no-air-order");
                    return;
                }

                // Multiple Air hubs can share a workspace. Never let several source lookups /
                // ledger commits land in the same frame; the next cached staging pass picks up
                // the next active hub/order.
                if (heavyAirOrderClaimed)
                {
                    SetAirHubStageStatus(hub, $"sorting:bg queued {prefab} x{missing}");
                    return;
                }
                heavyAirOrderClaimed = true;

                // A loaded physical Versorger now owns Worker-food/fuel staging visibly. Yield the
                // global background hub simulation for that exact provisioning order so it cannot
                // silently move the berries before the Worker reaches the chest. Generic air cargo
                // remains unchanged and is still handled by Sorter/background staging.
                if (WorkerProvisioningLogisticsRuntime.IsBaseProvisioningOrder(selectedHub, prefab) &&
                    HasActivePhysicalProvisioner(hubPosition, ownerId))
                {
                    hub.Set(AirHubStageLastTickKey, nowTicks);
                    hub.Set(AirHubStageFractionKey, 0f);
                    SetAirHubStageStatus(hub, $"provision:bg yielded to physical Versorger ({prefab} x{missing})");
                    return;
                }

                FindTrackedNormalSource(ownerId, selectedHubPosition, prefab, out ZDO bestSource, out int bestAvailable);
                RemoteStorageScanStats scanStats = null;
                if (bestSource == null || bestAvailable <= 0)
                {
                    scanStats = TryBootstrapRemoteNormalStorage(selectedHub, selectedHubPosition, ownerId, prefab);
                    if (scanStats?.RequestedSource != null && scanStats.RequestedMovable > 0)
                    {
                        bestSource = scanStats.RequestedSource;
                        bestAvailable = scanStats.RequestedMovable;
                    }
                    else
                    {
                        FindTrackedNormalSource(ownerId, selectedHubPosition, prefab, out bestSource, out bestAvailable);
                    }
                }

                if (bestSource == null || bestAvailable <= 0)
                {
                    string scanDetail = scanStats != null ? $"; {scanStats}" : string.Empty;
                    SetAirHubStageStatus(hub, $"sorting:bg air-order {prefab} x{missing}; no Base storage contains item / snapshot unavailable{scanDetail}");
                    return;
                }

                float fraction = hub.GetFloat(AirHubStageFractionKey, 0f);
                fraction += (float)(deltaSeconds / SecondsPerTrip);
                int trips = Mathf.FloorToInt(fraction);
                if (trips <= 0)
                {
                    hub.Set(AirHubStageFractionKey, fraction);
                    SetAirHubStageStatus(hub, $"sorting:bg air-order {prefab} x{missing}; source-ready");
                    return;
                }

                fraction -= trips;
                hub.Set(AirHubStageFractionKey, fraction);
                int requested = Mathf.Min(missing, Mathf.Min(bestAvailable, Mathf.Max(1, trips) * 10));
                string tx = ContainerLedgerRuntime.CreateTransactionId();
                int keepOverride = bestSource.GetInt(WorkerContainerRoleRuntime.KeepOneOverrideKey, -1);
                int minRemain = keepOverride == 0 ? 0 : 1;
                if (!ContainerLedgerRuntime.TryReserveRemoval(bestSource, tx, prefab, requested, minRemain,
                        "background-sorter-air-hub", out int reserved, out string reserveStatus) || reserved <= 0)
                {
                    SetAirHubStageStatus(hub, $"sorting:bg air-order {prefab}; reserve-failed:{reserveStatus}");
                    return;
                }

                if (!ContainerLedgerRuntime.TryCommitRemoval(bestSource, tx,
                        out ContainerLedgerRuntime.TransactionInfo committed, out string commitStatus) ||
                    committed == null || committed.Amount <= 0)
                {
                    ContainerLedgerRuntime.RollbackTransaction(bestSource, tx);
                    SetAirHubStageStatus(hub, $"sorting:bg air-order {prefab}; commit-failed:{commitStatus}");
                    return;
                }

                // This is still conservation-based transport: exactly the committed real source
                // amount becomes pending delivery on the Base AirFreight hub. No item is minted.
                AdvancedFieldDepotRuntime.QueueBackgroundDelivery(selectedHub, prefab, committed.Amount);
                ContainerLedgerRuntime.CompleteTransaction(bestSource, tx);
                SetAirHubStageStatus(hub, $"sorting:bg staged {prefab} x{committed.Amount} -> Base AirFreight");
            });
        }

        internal static string GetPersistentAirStageDebug(ZDO workerZdo)
        {
            if (workerZdo == null) return "no-worker";
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Vector3 origin = workerZdo.GetPosition();
            if (WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out _))
                origin = center;

            if (!WorkerLogisticsRuntime.TryFindPersistentAirDepot(origin, ownerId, AirDepotMode.BaseHub, out ZDO hub, out _)
                || hub == null)
                return "BaseHub not found";

            string status = hub.GetString(AirHubStageStatusKey, "not-started");
            string orders = hub.GetString(WorkerLogisticsRuntime.BaseAirSupplyOrdersKey, string.Empty);
            if (string.IsNullOrEmpty(orders)) orders = "none";
            return $"{status}; orders={orders}";
        }

        private static bool HasActivePhysicalSorter(Vector3 hubPosition, long ownerId)
        {
            try
            {
                float maxSqr = WorkerStorageRuntime.LogisticsNetworkRadius * WorkerStorageRuntime.LogisticsNetworkRadius;
                WorkerFastLookupRuntime.CopyKnownWorkers(PhysicalWorkerScratch);
                for (int i = 0; i < PhysicalWorkerScratch.Count; i++)
                {
                    Character character = PhysicalWorkerScratch[i];
                    if ((character.transform.position - hubPosition).sqrMagnitude > maxSqr) continue;
                    if (!WorkerFastLookupRuntime.TryGetWorker(character, false, out _, out ZDO workerZdo)) continue;
                    long workerOwner = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
                    if (workerOwner != 0L && ownerId != 0L && workerOwner != ownerId) continue;
                    if (!WorkerZdo.TryReadWorkArea(workerZdo, out WorkOrder order, out _, out _) || order != WorkOrder.Sort) continue;
                    if (!BackgroundWorkerScheduler.IsLoadedCharacterPhysical(character)) continue;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static bool HasActivePhysicalProvisioner(Vector3 hubPosition, long ownerId)
        {
            try
            {
                float maxSqr = WorkerStorageRuntime.LogisticsNetworkRadius * WorkerStorageRuntime.LogisticsNetworkRadius;
                WorkerFastLookupRuntime.CopyKnownWorkers(PhysicalWorkerScratch);
                for (int i = 0; i < PhysicalWorkerScratch.Count; i++)
                {
                    Character character = PhysicalWorkerScratch[i];
                    if ((character.transform.position - hubPosition).sqrMagnitude > maxSqr) continue;
                    if (!WorkerFastLookupRuntime.TryGetWorker(character, false, out _, out ZDO workerZdo)) continue;
                    long workerOwner = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
                    if (workerOwner != 0L && ownerId != 0L && workerOwner != ownerId) continue;
                    WorkerTaskFlags tasks = WorkerJobProfileRuntime.ReadTasks(workerZdo, WorkerRole.Hauler);
                    if ((tasks & WorkerTaskFlags.Provision) == 0) continue;
                    WorkOrder order = (WorkOrder)workerZdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None);
                    if (order != WorkOrder.Provision) continue;
                    if (!BackgroundWorkerScheduler.IsLoadedCharacterPhysical(character)) continue;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static void SetAirHubStageStatus(ZDO hub, string status)
        {
            if (hub == null) return;
            string next = status ?? string.Empty;
            string previous = hub.GetString(AirHubStageStatusKey, string.Empty);
            if (string.Equals(previous, next, StringComparison.Ordinal)) return;
            hub.Set(AirHubStageStatusKey, next);

            // Log state transitions only, not every scheduler tick. These messages make a remote
            // supply chain diagnosable without requiring the player to visit the Base.
            if (next.IndexOf("air-order", StringComparison.OrdinalIgnoreCase) >= 0 ||
                next.IndexOf("staged", StringComparison.OrdinalIgnoreCase) >= 0)
                BackgroundMonitorRuntime.Note(next);
        }

        internal static void GetAirHubMonitorLines(List<string> destination, int max = 6)
        {
            if (destination == null) return;
            destination.Clear();
            int limit = Math.Max(1, max);
            PersistentContainerRegistryRuntime.ForEachRemembered((hub, role, ownerId, position) =>
            {
                if (destination.Count >= limit || hub == null || role != WorkerContainerRole.FieldDepot) return;
                if (!hub.GetBool(WorkerContainerRoleRuntime.AirTransportKey, false)) return;
                AirDepotMode mode = (AirDepotMode)Mathf.Clamp(hub.GetInt(WorkerContainerRoleRuntime.AirDepotModeKey, 0), 0, 1);
                string status = hub.GetString(AirHubStageStatusKey, "idle");
                destination.Add($"{mode} @ {position.x:0}/{position.z:0}: {status}");
            });
        }

        private static void FindTrackedNormalSource(long ownerId, Vector3 basePosition, string prefab, out ZDO source, out int available)
        {
            WorkerProvisioningLogisticsRuntime.TryFindCachedTrackedNormalSource(
                ownerId, basePosition, prefab, out source, out available);
        }

        private static RemoteStorageScanStats TryBootstrapRemoteNormalStorage(ZDO baseHub, Vector3 basePosition, long ownerId, string requestedPrefab)
        {
            RemoteStorageScanStats stats = new RemoteStorageScanStats { RequestedPrefab = WorkerStorageRuntime.NormalizeObjectName(requestedPrefab) };
            if (baseHub == null) return stats;

            // Durable per-session negative/positive cache. Once the Base neighborhood was scanned
            // successfully, repeating the same spatial ZDO query cannot discover a chest that was
            // created while the area was unloaded. Loaded chest lifecycle hooks register changes.
            if (CompletedRemoteBaseStorageBootstrap.Contains(baseHub.m_uid))
            {
                stats.Throttled = true;
                return stats;
            }

            float now = Time.realtimeSinceStartup;
            if (NextRemoteBaseStorageBootstrap.TryGetValue(baseHub.m_uid, out float next) && now < next)
            {
                stats.Throttled = true;
                return stats;
            }

            // Do not burn the normal 20-second retry window before ZDOMan is actually ready.
            // This was the reason diagnostics could report visited=0 immediately after world load
            // and then sit idle even though the Base ZDOs became available a moment later.
            ZDOMan man = ZDOMan.instance;
            if (man == null)
            {
                stats.Deferred = true;
                NextRemoteBaseStorageBootstrap[baseHub.m_uid] = now + 2f;
                return stats;
            }

            float maxSqr = WorkerStorageRuntime.LogisticsNetworkRadius * WorkerStorageRuntime.LogisticsNetworkRadius;
            bool capturedAny = false;
            try
            {
                // Prefer ZDOMan's sector query. It is spatially bounded around the Base hub and is
                // dramatically cheaper than reflecting over every world ZDO. This is exactly the
                // data needed by unloaded Base simulation: real container ZDOs near the Air hub.
                capturedAny = TryHarvestRemoteNormalStorageBySector(man, basePosition, maxSqr, ownerId, stats);

                // Compatibility fallback for Valheim builds where FindSectorObjects/GetZone changed.
                // This path is strongly throttled by RemoteBaseStorageRetrySeconds and only runs
                // while a real air order cannot find source stock.
                if (!stats.UsedSectorQuery)
                {
                    FieldInfo[] fields = typeof(ZDOMan).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    for (int i = 0; i < fields.Length; i++)
                    {
                        object value;
                        try { value = fields[i].GetValue(man); }
                        catch { continue; }
                        capturedAny |= HarvestRemoteNormalStorage(value, 0, basePosition, maxSqr, ownerId, stats);
                    }
                }

                // Only a real completed scan receives the normal throttle. An empty scan is
                // usually a world-start timing issue, so retry it quickly instead of hiding the
                // Base for another 20 seconds.
                NextRemoteBaseStorageBootstrap[baseHub.m_uid] = now + (stats.Visited > 0 ? RemoteBaseStorageRetrySeconds : 2f);
                if (stats.Visited > 0) CompletedRemoteBaseStorageBootstrap.Add(baseHub.m_uid);

                if (capturedAny)
                    BackgroundMonitorRuntime.Note($"Remote Base storage bootstrap refreshed persistent storage for AirFreight hub {baseHub.m_uid}; {stats}.");
                else if (stats.Visited > 0)
                    BackgroundMonitorRuntime.Note($"Remote Base storage scan found no capturable normal storage for AirFreight hub {baseHub.m_uid}; {stats}.");
            }
            catch (Exception ex)
            {
                NextRemoteBaseStorageBootstrap[baseHub.m_uid] = now + 2f;
                Plugin.Log?.LogDebug($"[Background] Remote Base storage scan deferred: {ex.GetType().Name}: {ex.Message}");
            }
            return stats;
        }

        private static bool TryHarvestRemoteNormalStorageBySector(ZDOMan man, Vector3 basePosition, float maxSqr, long ownerId, RemoteStorageScanStats stats)
        {
            if (man == null || ZoneSystem.instance == null || stats == null) return false;
            try
            {
                // Current Valheim uses (Vector2s, SimulationDistance, List<ZDO>, List<ZDO>) rather
                // than the older reflected int-radius signature. Calling the real API keeps this
                // query spatially bounded and avoids the 8k+ object reflection fallback.
                int sectorRadius = Mathf.Max(1, Mathf.CeilToInt(WorkerStorageRuntime.LogisticsNetworkRadius / 64f) + 1);
                Vector2s sector = ZoneSystem.GetZone(basePosition);
                SimulationDistance distance = new SimulationDistance(sectorRadius, 0, false);
                RemoteSectorNearbyScratch.Clear();
                RemoteSectorDistantScratch.Clear();
                RemoteSectorSeenScratch.Clear();

                man.FindSectorObjects(sector, distance, RemoteSectorNearbyScratch, RemoteSectorDistantScratch);
                stats.UsedSectorQuery = true;

                bool captured = false;
                for (int i = 0; i < RemoteSectorNearbyScratch.Count; i++)
                {
                    ZDO zdo = RemoteSectorNearbyScratch[i];
                    if (zdo == null || !RemoteSectorSeenScratch.Add(zdo.m_uid)) continue;
                    captured |= TryCaptureRemoteNormal(zdo, basePosition, maxSqr, ownerId, stats);
                }
                for (int i = 0; i < RemoteSectorDistantScratch.Count; i++)
                {
                    ZDO zdo = RemoteSectorDistantScratch[i];
                    if (zdo == null || !RemoteSectorSeenScratch.Add(zdo.m_uid)) continue;
                    captured |= TryCaptureRemoteNormal(zdo, basePosition, maxSqr, ownerId, stats);
                }

                RemoteSectorSeenScratch.Clear();
                return captured;
            }
            catch
            {
                stats.UsedSectorQuery = false;
                return false;
            }
        }

        private static bool HarvestRemoteNormalStorage(object value, int depth, Vector3 basePosition, float maxSqr, long ownerId, RemoteStorageScanStats stats)
        {
            if (value == null || depth > 2) return false;
            bool captured = false;
            if (value is ZDO single)
            {
                return TryCaptureRemoteNormal(single, basePosition, maxSqr, ownerId, stats);
            }
            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value is ZDO zdo) captured |= TryCaptureRemoteNormal(zdo, basePosition, maxSqr, ownerId, stats);
                    else if (depth < 2) captured |= HarvestRemoteNormalStorage(entry.Value, depth + 1, basePosition, maxSqr, ownerId, stats);
                }
                return captured;
            }
            if (value is IEnumerable enumerable && !(value is string))
            {
                int guard = 0;
                foreach (object item in enumerable)
                {
                    if (++guard > 200000) break;
                    if (item is ZDO zdo) captured |= TryCaptureRemoteNormal(zdo, basePosition, maxSqr, ownerId, stats);
                    else if (depth < 2) captured |= HarvestRemoteNormalStorage(item, depth + 1, basePosition, maxSqr, ownerId, stats);
                }
            }
            return captured;
        }

        private static bool TryCaptureRemoteNormal(ZDO zdo, Vector3 basePosition, float maxSqr, long ownerId, RemoteStorageScanStats stats)
        {
            if (zdo == null) return false;
            if ((zdo.GetPosition() - basePosition).sqrMagnitude > maxSqr) return false;
            if (stats != null)
            {
                // ZDOMan exposes the same ZDO through several internal dictionaries/lists. The
                // old reflection fallback could therefore parse one chest many times and cause
                // visible frame hitches. Count/process each persistent ZDO only once per scan.
                if (!stats.Seen.Add(zdo.m_uid)) return false;
                stats.Visited++;
            }
            if ((WorkerContainerRole)Mathf.Clamp(zdo.GetInt(WorkerContainerRoleRuntime.RoleKey, 0), 0, 3) != WorkerContainerRole.Normal) return false;

            // Missing Workers role data defaults to Normal on every world piece. Only consider ZDOs
            // that actually carry a persisted inventory (or an existing ledger snapshot), otherwise
            // a fallback scan walks thousands of irrelevant building pieces and causes visible stalls.
            if (!ContainerLedgerRuntime.HasPersistentInventoryPayload(zdo)) return false;
            if (stats != null) stats.NormalCandidates++;

            bool captured = ContainerLedgerRuntime.TryCaptureSorterStorageFromPersistentZdo(zdo, ownerId);
            if (!captured) return false;
            if (stats != null)
            {
                stats.Captured++;
                if (!string.IsNullOrEmpty(stats.RequestedPrefab))
                {
                    int total = ContainerLedgerRuntime.GetSnapshotAmount(zdo, stats.RequestedPrefab);
                    if (total > 0)
                    {
                        stats.RequestedSnapshots++;
                        stats.RequestedTotal += total;
                        int keepOverride = zdo.GetInt(WorkerContainerRoleRuntime.KeepOneOverrideKey, -1);
                        int minRemain = keepOverride == 0 ? 0 : 1;
                        if (ContainerLedgerRuntime.TryGetAvailable(zdo, stats.RequestedPrefab, minRemain, out int movable) && movable > 0)
                        {
                            stats.RequestedMovable += movable;
                            if (stats.RequestedSource == null) stats.RequestedSource = zdo;
                        }
                    }
                }
            }
            return true;
        }

        private static void CaptureNormalBaseStorage(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return;
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
            WorkerStorageRuntime.ForEachContainerNear(home, WorkerStorageRuntime.LogisticsNetworkRadius, c =>
            {
                if (c == null || WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.Normal) return;
                if (!WorkerStorageRuntime.CanUseContainer(c, ownerId)) return;
                ContainerLedgerRuntime.MarkSorterStorage(c, ownerId);
            });
        }

        private static bool TryStageAirOrderInBackground(ZDO workerZdo, double deltaSeconds, float efficiency, out string status)
        {
            status = "sorting:bg/no-air-order";
            if (workerZdo == null) return false;
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Vector3 baseOrigin = WorkerZdo.ReadHome(workerZdo, workerZdo.GetPosition());
            if (!WorkerLogisticsRuntime.TryFindBaseAirStagingOrder(ownerId, baseOrigin, out ZDO baseHub, out Vector3 basePosition, out string prefab, out int missing))
                return false;

            FindTrackedNormalSource(ownerId, basePosition, prefab, out ZDO bestSource, out int bestAvailable);
            if (bestSource == null || bestAvailable <= 0)
            {
                // v0.5.357: an unloaded Base is cache-only. Never walk thousands of persistent
                // world ZDOs on the gameplay thread just because one requested item is missing
                // from the last loaded snapshot. The cache is refreshed when the Base is loaded
                // again; until then the remote order simply waits.
                status = $"sorting:bg air-order {prefab} x{missing}; waiting-for-loaded-base-cache";
                return true;
            }

            float fraction = workerZdo.GetFloat(AirStageFractionKey, 0f);
            fraction += (float)(Math.Max(0.0, deltaSeconds) * Mathf.Clamp01(efficiency) / SecondsPerTrip);
            int trips = Mathf.FloorToInt(fraction);
            if (trips <= 0)
            {
                workerZdo.Set(AirStageFractionKey, fraction);
                status = $"sorting:bg air-order {prefab} x{missing}; source-ready";
                return true;
            }

            fraction -= trips;
            workerZdo.Set(AirStageFractionKey, fraction);
            int requested = Mathf.Min(missing, Mathf.Min(bestAvailable, Mathf.Max(1, trips) * 10));
            string tx = ContainerLedgerRuntime.CreateTransactionId();
            int keepOverride = bestSource.GetInt(WorkerContainerRoleRuntime.KeepOneOverrideKey, -1);
            int minRemain = keepOverride == 0 ? 0 : 1;
            if (!ContainerLedgerRuntime.TryReserveRemoval(bestSource, tx, prefab, requested, minRemain, "background-sorter-air", out int reserved, out string reserveStatus) || reserved <= 0)
            {
                status = $"sorting:bg air-order {prefab}; reserve-failed:{reserveStatus}";
                return true;
            }
            if (!ContainerLedgerRuntime.TryCommitRemoval(bestSource, tx, out ContainerLedgerRuntime.TransactionInfo committed, out string commitStatus) || committed == null || committed.Amount <= 0)
            {
                ContainerLedgerRuntime.RollbackTransaction(bestSource, tx);
                status = $"sorting:bg air-order {prefab}; commit-failed:{commitStatus}";
                return true;
            }

            AdvancedFieldDepotRuntime.QueueBackgroundDelivery(baseHub, prefab, committed.Amount);
            ContainerLedgerRuntime.CompleteTransaction(bestSource, tx);
            status = $"sorting:bg staged {prefab} x{committed.Amount} -> Base AirFreight";
            return true;
        }

        private static SortPlan BuildPlan(Character worker, ZDO zdo)
        {
            if (!WorkerLogisticsRuntime.TryFindSorterSource(worker, zdo, out Container source, out string prefab, out int amount, out string sourceKind, out Vector3 sourcePosition))
                return null;
            if (source == null || string.IsNullOrEmpty(prefab) || amount <= 0) return null;

            SortPlan plan = new SortPlan
            {
                Prefab = prefab,
                SourceKind = sourceKind,
                SourcePosition = sourcePosition,
                BatchSize = Mathf.Max(1, amount),
                Budget = Mathf.Max(1, amount),
                Pending = 0,
                Fraction = 0f
            };

            if (string.Equals(sourceKind, WorkerStorageRuntime.SortImportSourceTargetKind, StringComparison.Ordinal))
            {
                if (!WorkerLogisticsRuntime.TryFindNormalDestination(worker, zdo, prefab, out Container dest, out Vector3 destPos)) return null;
                plan.SourceRole = WorkerContainerRole.Import;
                plan.DestinationRole = WorkerContainerRole.Normal;
                plan.ProtectOne = WorkerContainerRoleRuntime.ProtectLastItem(source);
                plan.DestinationPosition = destPos;
                int sourceCount = WorkerStorageRuntime.CountPrefab(source.GetInventory(), prefab);
                int usable = Mathf.Max(0, sourceCount - (plan.ProtectOne ? 1 : 0));
                plan.Budget = Mathf.Min(usable, WorkerStorageRuntime.GetAvailableCapacityForPrefab(dest.GetInventory(), prefab));
            }
            else if (string.Equals(sourceKind, AirHaulerWorkRuntime.SortInboundSourceTargetKind, StringComparison.Ordinal))
            {
                if (!WorkerLogisticsRuntime.TryFindNormalDestination(worker, zdo, prefab, out Container dest, out Vector3 destPos)) return null;
                plan.SourceRole = WorkerContainerRole.FieldDepot;
                plan.SourceAir = true;
                plan.SourceAirMode = AirDepotMode.BaseHub;
                plan.DestinationRole = WorkerContainerRole.Normal;
                plan.ProtectOne = WorkerContainerRoleRuntime.ProtectLastItem(source);
                plan.DestinationPosition = destPos;
                int sourceCount = WorkerStorageRuntime.CountPrefab(source.GetInventory(), prefab);
                long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
                int protectedAmount = AirHaulerWorkRuntime.GetSorterInboundProtectedAmount(source, ownerId, prefab);
                int usable = Mathf.Max(0, sourceCount - protectedAmount);
                plan.Budget = Mathf.Min(usable, WorkerStorageRuntime.GetAvailableCapacityForPrefab(dest.GetInventory(), prefab));
            }
            else if (string.Equals(sourceKind, WorkerStorageRuntime.SortBaseSourceTargetKind, StringComparison.Ordinal))
            {
                if (!WorkerLogisticsRuntime.TryFindExportDestination(worker, zdo, prefab, out Container dest, out Vector3 destPos)) return null;
                plan.SourceRole = WorkerContainerRole.Normal;
                plan.DestinationRole = WorkerContainerRole.Export;
                plan.ProtectOne = WorkerContainerRoleRuntime.ProtectLastItem(source);
                plan.DestinationPosition = destPos;
                int usable = Mathf.Max(0, WorkerStorageRuntime.CountPrefab(source.GetInventory(), prefab) - (plan.ProtectOne ? 1 : 0));
                plan.Budget = Mathf.Min(usable, Mathf.Min(plan.BatchSize, WorkerStorageRuntime.GetAvailableCapacityForPrefab(dest.GetInventory(), prefab)));
            }
            else if (string.Equals(sourceKind, AirHaulerWorkRuntime.SortOutboundSourceTargetKind, StringComparison.Ordinal))
            {
                if (!WorkerZdo.TryReadCargoRoute(zdo, out string route, out Vector3 routeTarget) || !string.Equals(route, AirHaulerWorkRuntime.RouteSortOutbound, StringComparison.Ordinal)) return null;
                Container dest = FindContainerNear(worker, zdo, routeTarget, WorkerContainerRole.FieldDepot, true, AirDepotMode.BaseHub, prefab, true);
                if (dest == null) return null;
                plan.SourceRole = WorkerContainerRole.Normal;
                plan.DestinationRole = WorkerContainerRole.FieldDepot;
                plan.DestinationAir = true;
                plan.DestinationAirMode = AirDepotMode.BaseHub;
                plan.ProtectOne = WorkerContainerRoleRuntime.ProtectLastItem(source);
                plan.DestinationPosition = dest.transform.position;
                int usable = Mathf.Max(0, WorkerStorageRuntime.CountPrefab(source.GetInventory(), prefab) - (plan.ProtectOne ? 1 : 0));
                plan.Budget = Mathf.Min(usable, Mathf.Min(plan.BatchSize, WorkerStorageRuntime.GetAvailableCapacityForPrefab(dest.GetInventory(), prefab)));
                WorkerZdo.ClearCargoRoute(zdo);
            }
            else return null;

            plan.Budget = Mathf.Max(0, plan.Budget);
            return plan.Budget > 0 ? plan : null;
        }

        private static int ReconcilePending(Character worker, ZDO zdo, SortPlan plan)
        {
            if (plan == null || plan.Pending <= 0) return 0;
            Container source = FindContainerNear(worker, zdo, plan.SourcePosition, plan.SourceRole, plan.SourceAir, plan.SourceAirMode, plan.Prefab, false);
            Container dest = FindContainerNear(worker, zdo, plan.DestinationPosition, plan.DestinationRole, plan.DestinationAir, plan.DestinationAirMode, plan.Prefab, true);
            if (source == null || dest == null) return 0;

            Inventory sourceInv = source.GetInventory();
            Inventory destInv = dest.GetInventory();
            if (sourceInv == null || destInv == null) return 0;
            // Never reconcile a background sorter transfer while another inventory system has
            // just touched either chest. SmartStorage QuickStack verifies deposits atomically.
            if (!WorkerInventoryMutationGuard.CanWorkersMutate(sourceInv) || !WorkerInventoryMutationGuard.CanWorkersMutate(destInv))
            {
                return 0;
            }
            int stock = WorkerStorageRuntime.CountPrefab(sourceInv, plan.Prefab);
            int protectedAmount = plan.ProtectOne ? 1 : 0;
            if (string.Equals(plan.SourceKind, AirHaulerWorkRuntime.SortInboundSourceTargetKind, StringComparison.Ordinal))
            {
                long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
                protectedAmount = AirHaulerWorkRuntime.GetSorterInboundProtectedAmount(source, ownerId, plan.Prefab);
            }
            int usable = Mathf.Max(0, stock - protectedAmount);
            int room = WorkerStorageRuntime.GetAvailableCapacityForPrefab(destInv, plan.Prefab);
            int move = Mathf.Min(plan.Pending, Mathf.Min(usable, room));
            if (move <= 0) return 0;

            if (!WorkerStorageRuntime.TryOwn(source) || !WorkerStorageRuntime.TryOwn(dest)) return 0;
            int removed = RemoveItems(sourceInv, plan.Prefab, move);
            if (removed <= 0) return 0;
            if (!AddItems(destInv, plan.Prefab, removed))
            {
                AddItems(sourceInv, plan.Prefab, removed);
                WorkerStorageRuntime.PersistContainer(source, sourceInv);
                return 0;
            }

            WorkerStorageRuntime.PersistContainer(source, sourceInv);
            WorkerStorageRuntime.PersistContainer(dest, destInv);
            return removed;
        }

        private static Container FindContainerNear(Character worker, ZDO zdo, Vector3 position, WorkerContainerRole role, bool requireAir, AirDepotMode airMode, string prefab, bool requireRoom)
        {
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Container best = null;
            float bestSqr = float.MaxValue;
            WorkerStorageRuntime.ForEachContainerNear(position, 8f, c =>
            {
                if (c == null || !WorkerStorageRuntime.CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(c) != role) return;
                if (requireAir && (!WorkerContainerRoleRuntime.SupportsAirTransport(c) || WorkerContainerRoleRuntime.GetAirDepotMode(c) != airMode)) return;
                Inventory inv = c.GetInventory();
                if (inv == null) return;
                if (role == WorkerContainerRole.Normal && requireRoom && !WorkerStorageRuntime.InventoryContainsPrefab(inv, prefab)) return;
                if (requireRoom && WorkerStorageRuntime.GetAvailableCapacityForPrefab(inv, prefab) <= 0) return;
                float sqr = (c.transform.position - position).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                best = c;
            });
            return best;
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

        private static bool AddItems(Inventory inv, string prefab, int amount)
        {
            if (inv == null || string.IsNullOrEmpty(prefab) || amount <= 0) return false;
            GameObject go = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null;
            if (go == null && ZNetScene.instance != null) go = ZNetScene.instance.GetPrefab(prefab);
            ItemDrop template = go != null ? go.GetComponent<ItemDrop>() : null;
            if (template?.m_itemData == null) return false;
            ItemDrop.ItemData data = template.m_itemData.Clone();
            data.m_dropPrefab = go;
            data.m_stack = amount;
            return WorkerStorageRuntime.TryAddItem(inv, data);
        }

        private static string BuildSummary(string mode, SortPlan p)
        {
            return $"sorting:{mode}; {p.SourceKind}; {p.Prefab}; batch={p.BatchSize}; budget={p.Budget}; pending={p.Pending}";
        }

        private static void Save(ZDO flagZdo, SortPlan p)
        {
            if (flagZdo == null) return;
            if (p == null)
            {
                flagZdo.Set(PlanKey, string.Empty);
                return;
            }
            string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
            string raw = string.Join("|", new[]
            {
                p.Prefab ?? string.Empty,
                p.SourceKind ?? string.Empty,
                ((int)p.SourceRole).ToString(CultureInfo.InvariantCulture),
                ((int)p.DestinationRole).ToString(CultureInfo.InvariantCulture),
                p.SourceAir ? "1" : "0",
                ((int)p.SourceAirMode).ToString(CultureInfo.InvariantCulture),
                p.DestinationAir ? "1" : "0",
                ((int)p.DestinationAirMode).ToString(CultureInfo.InvariantCulture),
                p.ProtectOne ? "1" : "0",
                F(p.SourcePosition.x), F(p.SourcePosition.y), F(p.SourcePosition.z),
                F(p.DestinationPosition.x), F(p.DestinationPosition.y), F(p.DestinationPosition.z),
                p.BatchSize.ToString(CultureInfo.InvariantCulture),
                p.Budget.ToString(CultureInfo.InvariantCulture),
                p.Pending.ToString(CultureInfo.InvariantCulture),
                F(p.Fraction)
            });
            flagZdo.Set(PlanKey, raw);
        }

        private static SortPlan Load(ZDO flagZdo)
        {
            if (flagZdo == null) return null;
            string raw = flagZdo.GetString(PlanKey, string.Empty);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string[] a = raw.Split('|');
            if (a.Length < 19) return null;
            try
            {
                float PF(int i) => float.Parse(a[i], CultureInfo.InvariantCulture);
                int PI(int i) => int.Parse(a[i], CultureInfo.InvariantCulture);
                return new SortPlan
                {
                    Prefab = a[0],
                    SourceKind = a[1],
                    SourceRole = (WorkerContainerRole)PI(2),
                    DestinationRole = (WorkerContainerRole)PI(3),
                    SourceAir = a[4] == "1",
                    SourceAirMode = (AirDepotMode)PI(5),
                    DestinationAir = a[6] == "1",
                    DestinationAirMode = (AirDepotMode)PI(7),
                    ProtectOne = a[8] == "1",
                    SourcePosition = new Vector3(PF(9), PF(10), PF(11)),
                    DestinationPosition = new Vector3(PF(12), PF(13), PF(14)),
                    BatchSize = PI(15),
                    Budget = PI(16),
                    Pending = PI(17),
                    Fraction = PF(18)
                };
            }
            catch { return null; }
        }
    }
}
