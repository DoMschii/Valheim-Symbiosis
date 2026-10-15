using System;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// v0.5.379: shared unloaded work-time arbitration. Numeric task priority remains authoritative;
    /// equal-priority modules split the currently available time budget. Callers may feed unused
    /// time back through another pass so a completed/blocked module never steals the rest of a day.
    /// </summary>
    internal static class BackgroundWorkBudgetRuntime
    {
        internal static double EffectiveSeconds(double deltaSeconds, float efficiency)
        {
            if (deltaSeconds <= 0.0) return 0.0;
            return Math.Max(0.0, deltaSeconds) * Mathf.Clamp(efficiency, 0.05f, 1f);
        }

        internal static void AllocateTwo(
            ZDO workerZdo,
            WorkerRole role,
            WorkerTaskFlags taskA,
            bool demandA,
            WorkerTaskFlags taskB,
            bool demandB,
            double availableSeconds,
            out double secondsA,
            out double secondsB)
        {
            secondsA = 0.0;
            secondsB = 0.0;
            if (workerZdo == null || availableSeconds <= 0.0001) return;

            if (!demandA && !demandB) return;
            if (demandA && !demandB) { secondsA = availableSeconds; return; }
            if (!demandA && demandB) { secondsB = availableSeconds; return; }

            int priorityA = WorkerJobProfileRuntime.GetPriority(workerZdo, role, taskA);
            int priorityB = WorkerJobProfileRuntime.GetPriority(workerZdo, role, taskB);
            if (priorityA < priorityB) { secondsA = availableSeconds; return; }
            if (priorityB < priorityA) { secondsB = availableSeconds; return; }

            // Equal explicit priority = equal first-refusal. If one module finishes before its half,
            // the caller loops and the survivor receives the returned remainder immediately.
            secondsA = availableSeconds * 0.5;
            secondsB = availableSeconds - secondsA;
        }
    }
}
