using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Lightweight pre-pickup reservations for physical ground Haulers transporting staged
    /// Export cargo to one FieldDepot Workzone. A claim exists only while the empty Hauler is
    /// travelling to / waiting at the Base Export source. Once the items are physically on the
    /// Worker ZDO, normal haul_inbound/haul_outbound cargo accounting becomes authoritative and
    /// this claim is released immediately.
    ///
    /// Claims are demand-address aware (owner + Workzone/legacy depot + item) and source-chest
    /// aware, so two transporters cannot reserve the same DemandJob slice or the same staged
    /// Export stock before either has actually picked anything up.
    /// </summary>
    internal static class HaulerDemandClaimRuntime
    {
        private const string ActiveKey = "vw_haul_demand_claim_active";
        private const string PrefabKey = "vw_haul_demand_claim_prefab";
        private const string WorkAreaKey = "vw_haul_demand_claim_workarea";
        private const string DepotKey = "vw_haul_demand_claim_depot";
        private const string SourceKey = "vw_haul_demand_claim_source";
        private const string AmountKey = "vw_haul_demand_claim_amount";
        private const string OwnerKey = "vw_haul_demand_claim_owner";
        private const string StatusKey = "vw_haul_demand_claim_status";

        // Ground routes may legitimately take well over a minute. Claims are also mirrored to the
        // worker and restored after reload, so use a long safety timeout rather than a short UI-like
        // lease. Normal state transitions release claims immediately.
        private const float ClaimTimeoutSeconds = 300f;
        private const float CleanupIntervalSeconds = 5f;

        private sealed class Claim
        {
            internal ZDO WorkerZdo;
            internal ZDOID WorkerId;
            internal long OwnerId;
            internal string Prefab = string.Empty;
            internal string WorkAreaId = string.Empty;
            internal ZDOID DepotId = ZDOID.None;
            internal ZDOID SourceId = ZDOID.None;
            internal int Amount;
            internal float ExpiresAt;
        }

        private static readonly Dictionary<ZDOID, Claim> Claims = new Dictionary<ZDOID, Claim>();
        private static readonly List<ZDOID> RemoveScratch = new List<ZDOID>(8);
        private static bool _bootstrapped;
        private static float _nextCleanup;

        internal static bool TryAcquire(ZDO workerZdo, WorkerLogisticsRuntime.SupplyRequest request,
            ZDOID sourceId, int sourceStock, int desiredAmount, out int claimedAmount, out string status)
        {
            claimedAmount = 0;
            status = "haul-claim:no-worker";
            if (workerZdo == null || string.IsNullOrWhiteSpace(request.Prefab) ||
                sourceId.Equals(ZDOID.None) || sourceStock <= 0 || request.Need <= 0 || desiredAmount <= 0)
                return false;

            EnsureBootstrapped();
            CleanupExpired(false);

            ZDOID workerId = workerZdo.m_uid;
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            string prefab = Normalize(request.Prefab);
            string workAreaId = request.WorkAreaId ?? string.Empty;
            ZDOID depotId = request.DepotId;

            if (Claims.TryGetValue(workerId, out Claim existing))
            {
                if (SameDemand(existing, ownerId, prefab, workAreaId, depotId) && existing.SourceId.Equals(sourceId))
                {
                    int demandOthers = GetClaimedAmountInternal(ownerId, prefab, workAreaId, depotId, workerId);
                    int sourceOthers = GetClaimedAmountForSourceInternal(ownerId, prefab, sourceId, workerId);
                    int demandAvailable = Mathf.Max(0, request.Need - demandOthers);
                    int sourceAvailable = Mathf.Max(0, sourceStock - sourceOthers);
                    int keep = Mathf.Min(Mathf.Max(0, existing.Amount), Mathf.Min(desiredAmount, Mathf.Min(demandAvailable, sourceAvailable)));
                    if (keep > 0)
                    {
                        existing.Amount = keep;
                        existing.ExpiresAt = Time.realtimeSinceStartup + ClaimTimeoutSeconds;
                        Mirror(existing, "active");
                        claimedAmount = keep;
                        status = $"haul-claim:reused {prefab} x{keep}";
                        return true;
                    }
                }
                ReleaseInternal(workerId, "replaced");
            }

            int claimedDemand = GetClaimedAmountInternal(ownerId, prefab, workAreaId, depotId, workerId);
            int claimedSource = GetClaimedAmountForSourceInternal(ownerId, prefab, sourceId, workerId);
            int unclaimedDemand = Mathf.Max(0, request.Need - claimedDemand);
            int unclaimedSource = Mathf.Max(0, sourceStock - claimedSource);
            int amount = Mathf.Min(Mathf.Max(0, desiredAmount), Mathf.Min(unclaimedDemand, unclaimedSource));
            if (amount <= 0)
            {
                status = $"haul-claim:covered {prefab} demand={request.Need} source={sourceStock}";
                return false;
            }

            Claim claim = new Claim
            {
                WorkerZdo = workerZdo,
                WorkerId = workerId,
                OwnerId = ownerId,
                Prefab = prefab,
                WorkAreaId = workAreaId,
                DepotId = depotId,
                SourceId = sourceId,
                Amount = amount,
                ExpiresAt = Time.realtimeSinceStartup + ClaimTimeoutSeconds
            };
            Claims[workerId] = claim;
            Mirror(claim, "active");
            claimedAmount = amount;
            status = $"haul-claim:acquired {prefab} x{amount}";
            return true;
        }

        internal static int GetClaimedAmount(long ownerId, string prefab, string workAreaId, ZDOID depotId)
        {
            EnsureBootstrapped();
            CleanupExpired(false);
            return GetClaimedAmountInternal(ownerId, Normalize(prefab), workAreaId ?? string.Empty, depotId, ZDOID.None);
        }

        internal static int GetClaimedAmountForSource(long ownerId, string prefab, ZDOID sourceId, ZDO workerZdo = null)
        {
            EnsureBootstrapped();
            CleanupExpired(false);
            ZDOID exclude = workerZdo != null ? workerZdo.m_uid : ZDOID.None;
            return GetClaimedAmountForSourceInternal(ownerId, Normalize(prefab), sourceId, exclude);
        }

        internal static int GetClaimedAmountForWorker(ZDO workerZdo, string prefab = null)
        {
            if (workerZdo == null) return 0;
            EnsureBootstrapped();
            CleanupExpired(false);
            if (!Claims.TryGetValue(workerZdo.m_uid, out Claim claim) || claim == null) return 0;
            if (!string.IsNullOrWhiteSpace(prefab) && !string.Equals(claim.Prefab, Normalize(prefab), StringComparison.OrdinalIgnoreCase)) return 0;
            return Mathf.Max(0, claim.Amount);
        }

        internal static bool MatchesSource(ZDO workerZdo, ZDOID sourceId)
        {
            if (workerZdo == null || sourceId.Equals(ZDOID.None)) return false;
            EnsureBootstrapped();
            CleanupExpired(false);
            return Claims.TryGetValue(workerZdo.m_uid, out Claim claim) && claim != null && claim.SourceId.Equals(sourceId);
        }

        internal static void Touch(ZDO workerZdo)
        {
            if (workerZdo == null) return;
            EnsureBootstrapped();
            if (!Claims.TryGetValue(workerZdo.m_uid, out Claim claim) || claim == null) return;
            claim.ExpiresAt = Time.realtimeSinceStartup + ClaimTimeoutSeconds;
        }

        internal static void Release(ZDO workerZdo, string reason)
        {
            if (workerZdo == null) return;
            EnsureBootstrapped();
            ReleaseInternal(workerZdo.m_uid, reason ?? "released");
        }

        private static int GetClaimedAmountInternal(long ownerId, string prefab, string workAreaId, ZDOID depotId, ZDOID excludeWorker)
        {
            long total = 0L;
            foreach (Claim claim in Claims.Values)
            {
                if (claim == null || claim.Amount <= 0) continue;
                if (!excludeWorker.Equals(ZDOID.None) && claim.WorkerId.Equals(excludeWorker)) continue;
                if (ownerId != 0L && claim.OwnerId != 0L && ownerId != claim.OwnerId) continue;
                if (!string.Equals(claim.Prefab, prefab, StringComparison.OrdinalIgnoreCase)) continue;
                if (!SameDemandAddress(claim.WorkAreaId, claim.DepotId, workAreaId, depotId)) continue;
                total += claim.Amount;
                if (total >= int.MaxValue) return int.MaxValue;
            }
            return (int)Math.Max(0L, total);
        }

        private static int GetClaimedAmountForSourceInternal(long ownerId, string prefab, ZDOID sourceId, ZDOID excludeWorker)
        {
            if (sourceId.Equals(ZDOID.None)) return 0;
            long total = 0L;
            foreach (Claim claim in Claims.Values)
            {
                if (claim == null || claim.Amount <= 0) continue;
                if (!excludeWorker.Equals(ZDOID.None) && claim.WorkerId.Equals(excludeWorker)) continue;
                if (ownerId != 0L && claim.OwnerId != 0L && ownerId != claim.OwnerId) continue;
                if (!string.Equals(claim.Prefab, prefab, StringComparison.OrdinalIgnoreCase)) continue;
                if (!claim.SourceId.Equals(sourceId)) continue;
                total += claim.Amount;
                if (total >= int.MaxValue) return int.MaxValue;
            }
            return (int)Math.Max(0L, total);
        }

        private static bool SameDemand(Claim claim, long ownerId, string prefab, string workAreaId, ZDOID depotId)
        {
            if (claim == null) return false;
            if (ownerId != 0L && claim.OwnerId != 0L && ownerId != claim.OwnerId) return false;
            if (!string.Equals(claim.Prefab, prefab, StringComparison.OrdinalIgnoreCase)) return false;
            return SameDemandAddress(claim.WorkAreaId, claim.DepotId, workAreaId, depotId);
        }

        private static bool SameDemandAddress(string aWorkArea, ZDOID aDepot, string bWorkArea, ZDOID bDepot)
        {
            bool aPooled = !string.IsNullOrEmpty(aWorkArea);
            bool bPooled = !string.IsNullOrEmpty(bWorkArea);
            if (aPooled || bPooled)
                return aPooled && bPooled && string.Equals(aWorkArea, bWorkArea, StringComparison.Ordinal);
            return aDepot.Equals(bDepot);
        }

        private static string Normalize(string prefab)
        {
            return WorkerStorageRuntime.NormalizeObjectName(prefab ?? string.Empty);
        }

        private static void EnsureBootstrapped()
        {
            if (_bootstrapped) return;
            _bootstrapped = true;
            float now = Time.realtimeSinceStartup;
            BackgroundWorkerScheduler.ForEachRegisteredWorker(workerZdo =>
            {
                if (workerZdo == null || !workerZdo.GetBool(ActiveKey, false)) return;
                int amount = workerZdo.GetInt(AmountKey, 0);
                string prefab = Normalize(workerZdo.GetString(PrefabKey, string.Empty));
                ZDOID sourceId = workerZdo.GetZDOID(SourceKey);
                if (amount <= 0 || string.IsNullOrEmpty(prefab) || sourceId.Equals(ZDOID.None))
                {
                    ClearMirror(workerZdo, "stale-bootstrap");
                    return;
                }

                WorkerState state = (WorkerState)workerZdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
                bool relevant = !WorkerZdo.HasCargo(workerZdo) &&
                                (state == WorkerState.MoveToTarget || state == WorkerState.Collect) &&
                                WorkerZdo.TryReadTarget(workerZdo, out _, out string targetKind, out _) &&
                                string.Equals(targetKind, WorkerStorageRuntime.ExportHaulSourceTargetKind, StringComparison.Ordinal);
                if (!relevant)
                {
                    ClearMirror(workerZdo, "stale-bootstrap");
                    return;
                }

                Claim restored = new Claim
                {
                    WorkerZdo = workerZdo,
                    WorkerId = workerZdo.m_uid,
                    OwnerId = workerZdo.GetLong(OwnerKey, workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L)),
                    Prefab = prefab,
                    WorkAreaId = workerZdo.GetString(WorkAreaKey, string.Empty),
                    DepotId = workerZdo.GetZDOID(DepotKey),
                    SourceId = sourceId,
                    Amount = amount,
                    ExpiresAt = now + ClaimTimeoutSeconds
                };
                Claims[restored.WorkerId] = restored;
            });
        }

        private static void CleanupExpired(bool force)
        {
            float now = Time.realtimeSinceStartup;
            if (!force && now < _nextCleanup) return;
            _nextCleanup = now + CleanupIntervalSeconds;
            RemoveScratch.Clear();
            foreach (KeyValuePair<ZDOID, Claim> pair in Claims)
            {
                Claim claim = pair.Value;
                if (claim == null || claim.Amount <= 0 || claim.ExpiresAt < now)
                    RemoveScratch.Add(pair.Key);
            }
            for (int i = 0; i < RemoveScratch.Count; i++)
                ReleaseInternal(RemoveScratch[i], "expired");
            RemoveScratch.Clear();
        }

        private static void ReleaseInternal(ZDOID workerId, string reason)
        {
            if (!Claims.TryGetValue(workerId, out Claim claim)) return;
            Claims.Remove(workerId);
            if (claim?.WorkerZdo != null) ClearMirror(claim.WorkerZdo, reason);
        }

        private static void Mirror(Claim claim, string status)
        {
            if (claim?.WorkerZdo == null) return;
            ZDO zdo = claim.WorkerZdo;
            zdo.Set(ActiveKey, true);
            zdo.Set(PrefabKey, claim.Prefab ?? string.Empty);
            zdo.Set(WorkAreaKey, claim.WorkAreaId ?? string.Empty);
            zdo.Set(DepotKey, claim.DepotId);
            zdo.Set(SourceKey, claim.SourceId);
            zdo.Set(AmountKey, Mathf.Max(0, claim.Amount));
            zdo.Set(OwnerKey, claim.OwnerId);
            zdo.Set(StatusKey, status ?? string.Empty);
        }

        private static void ClearMirror(ZDO zdo, string status)
        {
            if (zdo == null) return;
            zdo.Set(ActiveKey, false);
            zdo.Set(PrefabKey, string.Empty);
            zdo.Set(WorkAreaKey, string.Empty);
            zdo.Set(DepotKey, ZDOID.None);
            zdo.Set(SourceKey, ZDOID.None);
            zdo.Set(AmountKey, 0);
            zdo.Set(StatusKey, status ?? string.Empty);
        }
    }
}
