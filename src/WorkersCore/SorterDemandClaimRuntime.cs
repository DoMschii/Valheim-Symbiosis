using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Lightweight in-memory reservations for physical Sorters that are staging one FieldDepot
    /// demand from normal Base storage into Export. This closes the race where two Sorters can
    /// observe the same still-open DemandJob before either of them has physically moved an item.
    ///
    /// Claims never scan the world. They are keyed by worker ZDO and demand address
    /// (owner + Workzone/legacy depot + item), expire automatically, and are mirrored onto the
    /// worker ZDO only so an in-flight claim can be restored after a reload.
    /// </summary>
    internal static class SorterDemandClaimRuntime
    {
        private const string ActiveKey = "vw_sort_demand_claim_active";
        private const string PrefabKey = "vw_sort_demand_claim_prefab";
        private const string WorkAreaKey = "vw_sort_demand_claim_workarea";
        private const string DepotKey = "vw_sort_demand_claim_depot";
        private const string AmountKey = "vw_sort_demand_claim_amount";
        private const string OwnerKey = "vw_sort_demand_claim_owner";
        private const string StatusKey = "vw_sort_demand_claim_status";
        private const float ClaimTimeoutSeconds = 75f;
        private const float CleanupIntervalSeconds = 5f;

        private sealed class Claim
        {
            internal ZDO WorkerZdo;
            internal ZDOID WorkerId;
            internal long OwnerId;
            internal string Prefab = string.Empty;
            internal string WorkAreaId = string.Empty;
            internal ZDOID DepotId = ZDOID.None;
            internal int Amount;
            internal float ExpiresAt;
        }

        private static readonly Dictionary<ZDOID, Claim> Claims = new Dictionary<ZDOID, Claim>();
        private static readonly List<ZDOID> RemoveScratch = new List<ZDOID>(8);
        private static bool _bootstrapped;
        private static float _nextCleanup;

        internal static int ActiveClaimCount
        {
            get
            {
                EnsureBootstrapped();
                CleanupExpired(false);
                return Claims.Count;
            }
        }

        internal static bool TryAcquire(ZDO workerZdo, WorkerLogisticsRuntime.SupplyRequest request,
            int stageNeed, int desiredAmount, out int claimedAmount, out string status)
        {
            claimedAmount = 0;
            status = "claim:no-worker";
            if (workerZdo == null || string.IsNullOrWhiteSpace(request.Prefab) || stageNeed <= 0 || desiredAmount <= 0)
                return false;

            EnsureBootstrapped();
            CleanupExpired(false);

            ZDOID workerId = workerZdo.m_uid;
            string prefab = Normalize(request.Prefab);
            string workAreaId = request.WorkAreaId ?? string.Empty;
            ZDOID depotId = request.DepotId;
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);

            if (Claims.TryGetValue(workerId, out Claim existing))
            {
                if (SameDemand(existing, ownerId, prefab, workAreaId, depotId))
                {
                    int otherClaims = GetClaimedAmountInternal(ownerId, prefab, workAreaId, depotId, workerId);
                    int available = Mathf.Max(0, stageNeed - otherClaims);
                    int keep = Mathf.Min(Mathf.Max(0, existing.Amount), Mathf.Min(available, desiredAmount));
                    if (keep > 0)
                    {
                        existing.Amount = keep;
                        existing.ExpiresAt = Time.realtimeSinceStartup + ClaimTimeoutSeconds;
                        Mirror(existing, "active");
                        claimedAmount = keep;
                        status = $"claim:reused {prefab} x{keep}";
                        return true;
                    }
                }
                ReleaseInternal(workerId, "replaced");
            }

            int claimedByOthers = GetClaimedAmountInternal(ownerId, prefab, workAreaId, depotId, workerId);
            int unclaimed = Mathf.Max(0, stageNeed - claimedByOthers);
            int amount = Mathf.Min(Mathf.Max(0, desiredAmount), unclaimed);
            if (amount <= 0)
            {
                status = $"claim:already-covered {prefab} stageNeed={stageNeed} other={claimedByOthers}";
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
                Amount = amount,
                ExpiresAt = Time.realtimeSinceStartup + ClaimTimeoutSeconds
            };
            Claims[workerId] = claim;
            Mirror(claim, "active");
            claimedAmount = amount;
            status = $"claim:acquired {prefab} x{amount}";
            return true;
        }

        internal static int GetClaimedAmount(long ownerId, string prefab, string workAreaId, ZDOID depotId)
        {
            EnsureBootstrapped();
            CleanupExpired(false);
            return GetClaimedAmountInternal(ownerId, Normalize(prefab), workAreaId ?? string.Empty, depotId, ZDOID.None);
        }

        internal static int GetClaimedAmountForPrefab(long ownerId, string prefab)
        {
            EnsureBootstrapped();
            CleanupExpired(false);
            string normalized = Normalize(prefab);
            long total = 0L;
            foreach (Claim claim in Claims.Values)
            {
                if (claim == null || claim.Amount <= 0) continue;
                if (ownerId != 0L && claim.OwnerId != 0L && ownerId != claim.OwnerId) continue;
                if (!string.Equals(claim.Prefab, normalized, StringComparison.OrdinalIgnoreCase)) continue;
                total += claim.Amount;
                if (total >= int.MaxValue) return int.MaxValue;
            }
            return (int)Math.Max(0L, total);
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

        internal static void Touch(ZDO workerZdo)
        {
            if (workerZdo == null) return;
            EnsureBootstrapped();
            if (!Claims.TryGetValue(workerZdo.m_uid, out Claim claim) || claim == null) return;
            claim.ExpiresAt = Time.realtimeSinceStartup + ClaimTimeoutSeconds;
        }

        internal static void LimitTo(ZDO workerZdo, int amount)
        {
            if (workerZdo == null) return;
            EnsureBootstrapped();
            if (!Claims.TryGetValue(workerZdo.m_uid, out Claim claim) || claim == null) return;
            int limited = Mathf.Min(Mathf.Max(0, amount), Mathf.Max(0, claim.Amount));
            if (limited <= 0)
            {
                ReleaseInternal(workerZdo.m_uid, "empty-pickup");
                return;
            }
            claim.Amount = limited;
            claim.ExpiresAt = Time.realtimeSinceStartup + ClaimTimeoutSeconds;
            Mirror(claim, "cargo");
        }

        internal static void Consume(ZDO workerZdo, int amount)
        {
            if (workerZdo == null || amount <= 0) return;
            EnsureBootstrapped();
            if (!Claims.TryGetValue(workerZdo.m_uid, out Claim claim) || claim == null) return;
            claim.Amount = Mathf.Max(0, claim.Amount - amount);
            if (claim.Amount <= 0)
            {
                ReleaseInternal(workerZdo.m_uid, "completed");
                return;
            }
            claim.ExpiresAt = Time.realtimeSinceStartup + ClaimTimeoutSeconds;
            Mirror(claim, "partial");
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
                if (amount <= 0 || string.IsNullOrEmpty(prefab))
                {
                    ClearMirror(workerZdo, "stale-bootstrap");
                    return;
                }

                bool relevant = false;
                WorkerState state = (WorkerState)workerZdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
                if ((state == WorkerState.MoveToTarget || state == WorkerState.Collect) &&
                    WorkerZdo.TryReadTarget(workerZdo, out _, out string targetKind, out _) &&
                    string.Equals(targetKind, WorkerStorageRuntime.SortBaseSourceTargetKind, StringComparison.Ordinal))
                    relevant = true;
                if (WorkerZdo.TryReadCargoRoute(workerZdo, out string route, out _) &&
                    string.Equals(route, WorkerLogisticsRuntime.RouteSortExport, StringComparison.Ordinal) &&
                    WorkerZdo.HasCargo(workerZdo))
                    relevant = true;
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
            zdo.Set(AmountKey, 0);
            zdo.Set(StatusKey, status ?? string.Empty);
        }
    }
}
