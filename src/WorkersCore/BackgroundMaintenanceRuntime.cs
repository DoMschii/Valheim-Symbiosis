using System;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// v0.5.378: unloaded maintenance jobs use elapsed-time credit instead of remote Character AI.
    ///
    /// The credit is cheap scalar state on the Worker ZDO. Real livestock and structures are only
    /// touched once the Workzone becomes physical again; short retry windows cover streaming/network
    /// ownership without turning the feature into a permanent scanner.
    /// </summary>
    internal static class BackgroundMaintenanceRuntime
    {
        internal const float ButcherAnimalsPerValheimDay = 24f;
        internal const float RepairPiecesPerValheimDay = 100f;

        private const string ButcherFractionKey = "vw_bg_butcher_fraction";
        private const string ButcherPendingKey = "vw_bg_butcher_pending";
        private const string ButcherPendingUntilKey = "vw_bg_butcher_pending_until";
        private const string ButcherNextTryKey = "vw_bg_butcher_next_try";

        private const string RepairFractionKey = "vw_bg_repair_fraction";
        private const string RepairPendingKey = "vw_bg_repair_pending";
        private const string RepairPendingUntilKey = "vw_bg_repair_pending_until";
        private const string RepairNextTryKey = "vw_bg_repair_next_try";
        private const string RepairEmptyPassesKey = "vw_bg_repair_empty_passes";
        private const string RepairBacklogKey = "vw_bg_repair_backlog";

        private static readonly long MaterializeWindowTicks = TimeSpan.FromMinutes(3).Ticks;
        private static readonly long ButcherRetryTicks = TimeSpan.FromSeconds(1).Ticks;
        private static readonly long RepairRetryTicks = TimeSpan.FromSeconds(2).Ticks;

        internal static string TickButcherCatchup(Character worker, ZDO workerZdo, double deltaSeconds, float efficiency)
        {
            if (workerZdo == null) return "butcher:no-worker";

            int quota = AccrueQuota(workerZdo, ButcherFractionKey, deltaSeconds,
                ButcherAnimalsPerValheimDay, efficiency, 512);
            if (quota > 0)
                AddPending(workerZdo, ButcherPendingKey, ButcherPendingUntilKey, quota, 512);

            if (worker == null)
                return quota > 0 ? $"butcher:catchup queued {quota}" : "butcher:no-loaded-character";

            TryConsumePendingButcher(worker, workerZdo, out string status);
            return status;
        }

        internal static void CaptureSmithSnapshot(Character worker, ZDO workerZdo)
        {
            if (worker == null || workerZdo == null) return;
            WorkerTaskFlags tasks = WorkerJobProfileRuntime.ReadTasks(workerZdo, WorkerRole.Smith);
            bool repairEnabled = (tasks & WorkerTaskFlags.Repair) != 0;
            int pending = Mathf.Max(0, workerZdo.GetInt(RepairPendingKey, 0));
            int backlog = repairEnabled ? BaseRepairWorkRuntime.CountBackgroundRepairBacklog(worker, workerZdo) : 0;
            // Never erase quota that is still being materialised from a previous unloaded period.
            workerZdo.Set(RepairBacklogKey, Mathf.Max(backlog, pending));
            if (!repairEnabled)
            {
                workerZdo.Set(RepairFractionKey, 0f);
                ClearPending(workerZdo, RepairPendingKey, RepairPendingUntilKey, RepairNextTryKey);
            }
        }

        internal static string TickSmithCatchup(Character worker, ZDO workerZdo, double deltaSeconds, float efficiency)
        {
            if (workerZdo == null) return "smith:no-worker";

            WorkerTaskFlags tasks = WorkerJobProfileRuntime.ReadTasks(workerZdo, WorkerRole.Smith);
            bool metalEnabled = (tasks & WorkerTaskFlags.Smith) != 0;
            bool repairEnabled = (tasks & WorkerTaskFlags.Repair) != 0;
            if (!metalEnabled && !repairEnabled)
            {
                workerZdo.Set(RepairFractionKey, 0f);
                workerZdo.Set(RepairBacklogKey, 0);
                ClearPending(workerZdo, RepairPendingKey, RepairPendingUntilKey, RepairNextTryKey);
                return "smith:jobs-disabled";
            }
            if (!repairEnabled)
            {
                workerZdo.Set(RepairFractionKey, 0f);
                workerZdo.Set(RepairBacklogKey, 0);
                ClearPending(workerZdo, RepairPendingKey, RepairPendingUntilKey, RepairNextTryKey);
                workerZdo.Set(RepairEmptyPassesKey, 0);
            }

            double remaining = BackgroundWorkBudgetRuntime.EffectiveSeconds(deltaSeconds, efficiency);
            double initial = remaining;
            double repairUsed = 0.0;
            double metalUsed = 0.0;
            string metalStatus = metalEnabled ? "metal:no-demand" : "metal:disabled";

            // A few redistribution passes are enough for two modules: when the higher/equal-priority
            // module finishes its finite backlog early, its unused time immediately flows to the other.
            for (int pass = 0; pass < 4 && remaining > 0.0001; pass++)
            {
                bool repairDemand = repairEnabled && HasRepairBudgetDemand(workerZdo);
                bool metalDemand = metalEnabled && BackgroundSmithMetalRuntime.HasDemand(workerZdo);
                if (!repairDemand && !metalDemand) break;

                BackgroundWorkBudgetRuntime.AllocateTwo(workerZdo, WorkerRole.Smith,
                    WorkerTaskFlags.Repair, repairDemand, WorkerTaskFlags.Smith, metalDemand, remaining,
                    out double repairShare, out double metalShare);

                double consumedRepair = repairShare > 0.0 ? ConsumeRepairBudget(workerZdo, repairShare) : 0.0;
                double consumedMetal = metalShare > 0.0 ? BackgroundSmithMetalRuntime.ConsumeBudget(workerZdo, metalShare, out metalStatus) : 0.0;
                double consumed = consumedRepair + consumedMetal;
                repairUsed += consumedRepair;
                metalUsed += consumedMetal;
                remaining = Math.Max(0.0, remaining - consumed);
                if (consumed <= 0.0001) break;
            }

            string repairStatus = repairEnabled
                ? $"repair:backlog {Mathf.Max(0, workerZdo.GetInt(RepairBacklogKey, 0))}, pending {Mathf.Max(0, workerZdo.GetInt(RepairPendingKey, 0))}"
                : "repair:disabled";

            if (worker != null)
                TryConsumePendingRepair(worker, workerZdo, out repairStatus);

            if (initial <= 0.0001) return $"smith:{repairStatus} | {metalStatus}";
            int repairPct = Mathf.RoundToInt((float)(repairUsed / initial * 100.0));
            int metalPct = Mathf.RoundToInt((float)(metalUsed / initial * 100.0));
            return $"smith-budget repair={repairPct}% metal={metalPct}% | {repairStatus} | {metalStatus}";
        }

        private static bool HasRepairBudgetDemand(ZDO workerZdo)
        {
            if (workerZdo == null) return false;
            int backlog = Mathf.Max(0, workerZdo.GetInt(RepairBacklogKey, 0));
            int pending = Mathf.Max(0, workerZdo.GetInt(RepairPendingKey, 0));
            return backlog > pending;
        }

        private static double ConsumeRepairBudget(ZDO workerZdo, double assignedSeconds)
        {
            if (workerZdo == null || assignedSeconds <= 0.0001) return 0.0;
            int backlog = Mathf.Max(0, workerZdo.GetInt(RepairBacklogKey, 0));
            int pending = Mathf.Max(0, workerZdo.GetInt(RepairPendingKey, 0));
            int needed = Mathf.Max(0, backlog - pending);
            if (needed <= 0) return 0.0;

            double daySeconds = Math.Max(1.0, WorkerSustenanceRuntime.MealInterval.TotalSeconds);
            double rate = RepairPiecesPerValheimDay / daySeconds;
            double fraction = Math.Max(0.0, Math.Min(0.999999, workerZdo.GetFloat(RepairFractionKey, 0f)));
            double secondsToFinish = Math.Max(0.0, (needed - fraction) / Math.Max(0.000001, rate));
            double used = Math.Min(assignedSeconds, secondsToFinish);
            if (used <= 0.0001) return 0.0;

            double total = fraction + used * rate;
            int whole = Math.Min(needed, (int)Math.Floor(total));
            double remainder = total - Math.Floor(total);
            if (whole > 0) AddPending(workerZdo, RepairPendingKey, RepairPendingUntilKey, whole, 2000);
            if (whole >= needed) remainder = 0.0;
            workerZdo.Set(RepairFractionKey, (float)Math.Max(0.0, Math.Min(0.999999, remainder)));
            return used;
        }

        internal static bool TryConsumePendingButcher(Character worker, ZDO workerZdo, out string status)
        {
            status = string.Empty;
            if (worker == null || workerZdo == null) return false;
            int pending = Mathf.Max(0, workerZdo.GetInt(ButcherPendingKey, 0));
            if (pending <= 0) return false;

            long now = DateTime.UtcNow.Ticks;
            long until = workerZdo.GetLong(ButcherPendingUntilKey, 0L);
            if (until > 0L && now > until)
            {
                ClearPending(workerZdo, ButcherPendingKey, ButcherPendingUntilKey, ButcherNextTryKey);
                status = "Metzger-Catchup beendet: kein weiterer aktueller Ueberschuss";
                return false;
            }

            long nextTry = workerZdo.GetLong(ButcherNextTryKey, 0L);
            if (nextTry > now) return true;
            workerZdo.Set(ButcherNextTryKey, now + ButcherRetryTicks);

            int batch = Mathf.Min(12, pending);
            int culled = LivestockButcherRuntime.RunBackgroundCatchup(worker, workerZdo, batch, out status);
            if (culled > 0)
            {
                pending = Mathf.Max(0, pending - culled);
                workerZdo.Set(ButcherPendingKey, pending);
                WorkzoneEconomyRuntime.Invalidate(WorkerZdo.ReadWorkAreaId(workerZdo));
            }

            if (pending <= 0)
                ClearPending(workerZdo, ButcherPendingKey, ButcherPendingUntilKey, ButcherNextTryKey);

            workerZdo.Set("vw_bg_status", status ?? "butcher:catchup");
            return pending > 0 || culled > 0;
        }

        internal static bool TryConsumePendingRepair(Character worker, ZDO workerZdo, out string status)
        {
            status = string.Empty;
            if (worker == null || workerZdo == null) return false;
            int pending = Mathf.Max(0, workerZdo.GetInt(RepairPendingKey, 0));
            if (pending <= 0) return false;

            long now = DateTime.UtcNow.Ticks;
            long until = workerZdo.GetLong(RepairPendingUntilKey, 0L);
            if (until > 0L && now > until)
            {
                ClearPending(workerZdo, RepairPendingKey, RepairPendingUntilKey, RepairNextTryKey);
                workerZdo.Set(RepairEmptyPassesKey, 0);
                status = "Reparatur-Catchup beendet: kein weiterer aktueller Schaden";
                return false;
            }

            long nextTry = workerZdo.GetLong(RepairNextTryKey, 0L);
            if (nextTry > now) return true;
            workerZdo.Set(RepairNextTryKey, now + RepairRetryTicks);

            int batch = Mathf.Min(100, pending);
            int repaired = BaseRepairWorkRuntime.RunBackgroundCatchup(worker, workerZdo, batch,
                out int damagedSeen, out status);

            if (repaired > 0)
            {
                pending = Mathf.Max(0, pending - repaired);
                workerZdo.Set(RepairPendingKey, pending);
                workerZdo.Set(RepairBacklogKey, Mathf.Max(0, workerZdo.GetInt(RepairBacklogKey, 0) - repaired));
                workerZdo.Set(RepairEmptyPassesKey, 0);
                WorkzoneEconomyRuntime.Invalidate(WorkerZdo.ReadWorkAreaId(workerZdo));
            }
            else if (damagedSeen <= 0)
            {
                int emptyPasses = workerZdo.GetInt(RepairEmptyPassesKey, 0) + 1;
                workerZdo.Set(RepairEmptyPassesKey, emptyPasses);
                // Two empty loaded passes are enough to distinguish normal streaming from a truly
                // healthy base. Do not bank unused daily repair capacity into future damage.
                if (emptyPasses >= 2)
                {
                    pending = 0;
                    workerZdo.Set(RepairPendingKey, 0);
                    workerZdo.Set(RepairBacklogKey, 0);
                    workerZdo.Set(RepairFractionKey, 0f);
                }
            }

            if (pending <= 0)
            {
                ClearPending(workerZdo, RepairPendingKey, RepairPendingUntilKey, RepairNextTryKey);
                workerZdo.Set(RepairEmptyPassesKey, 0);
            }

            workerZdo.Set("vw_bg_status", status ?? "repair:catchup");
            return pending > 0 || repaired > 0;
        }

        private static int AccrueQuota(ZDO zdo, string fractionKey, double deltaSeconds,
            float perDay, float efficiency, int maxWhole)
        {
            if (zdo == null || deltaSeconds <= 0.0 || perDay <= 0f) return 0;
            double daySeconds = Math.Max(1.0, WorkerSustenanceRuntime.MealInterval.TotalSeconds);
            double oldFraction = Math.Max(0.0, Math.Min(0.999999, zdo.GetFloat(fractionKey, 0f)));
            double total = oldFraction + (Math.Max(0.0, deltaSeconds) / daySeconds) * perDay * Mathf.Clamp(efficiency, 0.05f, 1f);
            int whole = (int)Math.Min(maxWhole, Math.Floor(total));
            double remainder = total - Math.Floor(total);
            zdo.Set(fractionKey, (float)Math.Max(0.0, Math.Min(0.999999, remainder)));
            return Mathf.Max(0, whole);
        }

        private static void AddPending(ZDO zdo, string pendingKey, string untilKey, int amount, int max)
        {
            if (zdo == null || amount <= 0) return;
            int old = Mathf.Max(0, zdo.GetInt(pendingKey, 0));
            zdo.Set(pendingKey, Mathf.Clamp(old + amount, 0, max));
            zdo.Set(untilKey, DateTime.UtcNow.Ticks + MaterializeWindowTicks);
        }

        private static void ClearPending(ZDO zdo, string pendingKey, string untilKey, string nextTryKey)
        {
            if (zdo == null) return;
            zdo.Set(pendingKey, 0);
            zdo.Set(untilKey, 0L);
            zdo.Set(nextTryKey, 0L);
        }
    }
}
