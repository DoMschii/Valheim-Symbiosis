using System;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Lightweight loaded-zone patrol planner for Guard duty in "Ausruecken" mode.
    ///
    /// Important performance rule: this never scans the world, characters, colliders or pieces.
    /// It only advances between a small deterministic set of points inside the already assigned
    /// Workzone. Enemy acquisition remains owned by WorkerCombatStanceRuntime; combat interrupts
    /// Standby movement naturally and the patrol resumes afterward.
    /// </summary>
    internal static class GuardPatrolRuntime
    {
        private const string PatrolIndexKey = "vw_guard_patrol_index";
        private const string PatrolHoldUntilTicksKey = "vw_guard_patrol_hold_until_ticks";
        private const string PatrolLabelPrefix = "Wachpatrouille ";
        private const int PatrolPointCount = 8;
        private const double HoldSeconds = 4.0;

        internal const float StopDistance = 1.25f;

        internal static bool TryResolve(Character worker, ZDO zdo, Vector3 workCenter, float workRadius,
            out Vector3 patrolPoint, out string label)
        {
            patrolPoint = worker != null ? worker.transform.position : workCenter;
            label = "Wachpatrouille";
            if (worker == null || zdo == null) return false;
            if (!GuardBehaviorRuntime.IsGuardJob(zdo) || GuardBehaviorRuntime.Read(zdo) != GuardBehaviorMode.Deploy)
                return false;

            float radius = Mathf.Max(3f, workRadius);
            long nowTicks = DateTime.UtcNow.Ticks;

            // Keep the current patrol point while travelling. Once it has been reached, hold it for
            // a few seconds so the Guard visibly watches that sector instead of orbiting constantly.
            if (WorkerZdo.TryReadTarget(zdo, out string currentName, out string currentKind, out Vector3 currentPoint) &&
                string.Equals(currentKind, WorkerStandbyRuntime.TargetKind, StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(currentName) && currentName.StartsWith(PatrolLabelPrefix, StringComparison.Ordinal))
            {
                Vector3 horizontal = currentPoint - worker.transform.position;
                horizontal.y = 0f;
                if (horizontal.sqrMagnitude > (StopDistance + 0.20f) * (StopDistance + 0.20f))
                {
                    patrolPoint = currentPoint;
                    label = currentName;
                    return true;
                }

                long holdUntil = zdo.GetLong(PatrolHoldUntilTicksKey, 0L);
                if (holdUntil <= 0L)
                {
                    holdUntil = DateTime.UtcNow.AddSeconds(HoldSeconds).Ticks;
                    zdo.Set(PatrolHoldUntilTicksKey, holdUntil);
                }
                if (nowTicks < holdUntil)
                {
                    patrolPoint = currentPoint;
                    label = currentName;
                    return true;
                }
            }

            int index = zdo.GetInt(PatrolIndexKey, -1);
            index = (index + 1) % PatrolPointCount;
            if (index < 0) index = 0;
            zdo.Set(PatrolIndexKey, index);
            zdo.Set(PatrolHoldUntilTicksKey, 0L);

            // Stay comfortably inside the circular Workzone. Very large workzones deliberately cap
            // the patrol ring so a guard post does not turn into long-distance travel every cycle.
            float ring = Mathf.Min(24f, Mathf.Max(2.5f, radius * 0.48f));
            ring = Mathf.Min(ring, Mathf.Max(1.5f, radius - 2f));

            // Offset the eight-way ring by 22.5 degrees so points are not all aligned with the flag axes.
            float angle = (22.5f + index * (360f / PatrolPointCount)) * Mathf.Deg2Rad;
            patrolPoint = workCenter + new Vector3(Mathf.Cos(angle) * ring, 0f, Mathf.Sin(angle) * ring);

            // Only query terrain height for this single newly selected point. No physics/world scan.
            try
            {
                if (ZoneSystem.instance != null)
                    patrolPoint.y = ZoneSystem.instance.GetGroundHeight(patrolPoint) + 0.05f;
                else
                    patrolPoint.y = worker.transform.position.y;
            }
            catch
            {
                patrolPoint.y = worker.transform.position.y;
            }

            label = PatrolLabelPrefix + (index + 1).ToString() + "/" + PatrolPointCount.ToString();
            return true;
        }

        internal static void Reset(ZDO zdo)
        {
            if (zdo == null) return;
            zdo.Set(PatrolHoldUntilTicksKey, 0L);
        }
    }
}
