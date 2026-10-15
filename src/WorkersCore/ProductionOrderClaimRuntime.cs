using System;
using System.Collections.Generic;

namespace Valheim_Workers
{
    /// <summary>
    /// Lightweight runtime reservations for station ProductionOrders.
    ///
    /// A station order is authoritative, but several physical/background cooks may evaluate the
    /// same bill in adjacent scheduler passes. Without a claim, every worker sees the same stock
    /// deficit and each can fetch a full recipe batch before the first batch is deposited. Claims
    /// reserve only the output quantity of an in-flight batch; ingredients remain real inventory
    /// and are still protected by the existing container-ledger/removal logic.
    ///
    /// Claims intentionally remain runtime-only. After a process/world reload they disappear and
    /// are recreated from persisted worker plans as soon as the station is resolved again. That
    /// avoids introducing another persistent migration format while still preventing duplicate
    /// work during normal play and background simulation.
    /// </summary>
    internal static class ProductionOrderClaimRuntime
    {
        private sealed class Claim
        {
            internal string StationKey;
            internal string WorkerKey;
            internal string Output;
            internal int Amount;
            internal long ExpiresAtTicks;
        }

        private static readonly Dictionary<string, Claim> Claims = new Dictionary<string, Claim>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> CleanupKeys = new List<string>(16);
        private static readonly long ClaimLifetimeTicks = TimeSpan.FromMinutes(10).Ticks;

        private static string StationKey(ZDO zdo) => zdo != null ? zdo.m_uid.ToString() : string.Empty;
        private static string WorkerKey(ZDO zdo) => zdo != null ? zdo.m_uid.ToString() : string.Empty;
        private static string Composite(string station, string worker, string output) =>
            station + "|" + worker + "|" + (output ?? string.Empty).ToLowerInvariant();

        private static void CleanupExpired()
        {
            if (Claims.Count == 0) return;
            long now = DateTime.UtcNow.Ticks;
            CleanupKeys.Clear();
            foreach (KeyValuePair<string, Claim> pair in Claims)
            {
                Claim claim = pair.Value;
                if (claim == null || claim.ExpiresAtTicks <= now) CleanupKeys.Add(pair.Key);
            }
            for (int i = 0; i < CleanupKeys.Count; i++) Claims.Remove(CleanupKeys[i]);
            CleanupKeys.Clear();
        }

        internal static int GetClaimedAmount(ZDO stationZdo, string output)
        {
            return GetClaimedAmountInternal(stationZdo, output, null);
        }

        internal static int GetClaimedAmountExceptWorker(ZDO stationZdo, string output, ZDO excludedWorkerZdo)
        {
            return GetClaimedAmountInternal(stationZdo, output, excludedWorkerZdo);
        }

        private static int GetClaimedAmountInternal(ZDO stationZdo, string output, ZDO excludedWorkerZdo)
        {
            if (stationZdo == null || string.IsNullOrWhiteSpace(output)) return 0;
            CleanupExpired();
            string station = StationKey(stationZdo);
            string excludedWorker = WorkerKey(excludedWorkerZdo);
            int total = 0;
            foreach (Claim claim in Claims.Values)
            {
                if (claim == null || !string.Equals(claim.StationKey, station, StringComparison.Ordinal)) continue;
                if (!string.Equals(claim.Output, output, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(excludedWorker) && string.Equals(claim.WorkerKey, excludedWorker, StringComparison.Ordinal)) continue;
                total += Math.Max(0, claim.Amount);
            }
            return Math.Max(0, total);
        }

        internal static bool TryClaim(ZDO stationZdo, ZDO workerZdo, string output, int amount, out int claimed)
        {
            claimed = 0;
            if (stationZdo == null || workerZdo == null || string.IsNullOrWhiteSpace(output) || amount <= 0) return false;
            CleanupExpired();

            string station = StationKey(stationZdo);
            string worker = WorkerKey(workerZdo);
            if (string.IsNullOrEmpty(station) || string.IsNullOrEmpty(worker)) return false;

            // One production worker owns at most one station batch at a time. This also clears a
            // stale claim if a persisted plan was invalidated and the worker immediately replans.
            ReleaseClaimsForWorker(workerZdo);

            Claim claim = new Claim
            {
                StationKey = station,
                WorkerKey = worker,
                Output = output,
                Amount = Math.Max(1, amount),
                ExpiresAtTicks = DateTime.UtcNow.Ticks + ClaimLifetimeTicks
            };
            Claims[Composite(station, worker, output)] = claim;
            claimed = claim.Amount;
            return true;
        }

        internal static bool EnsureClaim(ZDO stationZdo, ZDO workerZdo, string output, int amount)
        {
            if (stationZdo == null || workerZdo == null || string.IsNullOrWhiteSpace(output) || amount <= 0) return false;
            CleanupExpired();
            string station = StationKey(stationZdo);
            string worker = WorkerKey(workerZdo);
            string key = Composite(station, worker, output);
            if (Claims.TryGetValue(key, out Claim existing) && existing != null)
            {
                existing.Amount = Math.Max(1, amount);
                existing.ExpiresAtTicks = DateTime.UtcNow.Ticks + ClaimLifetimeTicks;
                return true;
            }
            return TryClaim(stationZdo, workerZdo, output, amount, out _);
        }

        internal static void TouchWorkerClaims(ZDO workerZdo)
        {
            if (workerZdo == null) return;
            CleanupExpired();
            string worker = WorkerKey(workerZdo);
            long expires = DateTime.UtcNow.Ticks + ClaimLifetimeTicks;
            foreach (Claim claim in Claims.Values)
                if (claim != null && string.Equals(claim.WorkerKey, worker, StringComparison.Ordinal))
                    claim.ExpiresAtTicks = expires;
        }

        internal static void ReleaseClaimsForWorker(ZDO workerZdo)
        {
            if (workerZdo == null || Claims.Count == 0) return;
            string worker = WorkerKey(workerZdo);
            CleanupKeys.Clear();
            foreach (KeyValuePair<string, Claim> pair in Claims)
                if (pair.Value != null && string.Equals(pair.Value.WorkerKey, worker, StringComparison.Ordinal))
                    CleanupKeys.Add(pair.Key);
            for (int i = 0; i < CleanupKeys.Count; i++) Claims.Remove(CleanupKeys[i]);
            CleanupKeys.Clear();
        }

        internal static void Clear()
        {
            Claims.Clear();
            CleanupKeys.Clear();
        }
    }
}
