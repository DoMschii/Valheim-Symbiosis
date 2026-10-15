using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Palworld-style local fallback for a physically loaded Worker: navigation stays the preferred
    /// presentation, but a job may finish in-place after repeated route failure. The real target and
    /// real damage/resource path remain authoritative; only the proximity gate is temporarily relaxed.
    /// </summary>
    internal static class LocalWorkFallbackRuntime
    {
        private const string ActiveKey = "vw_local_work_fallback";
        private const string TargetNameKey = "vw_local_work_fallback_name";
        private const string TargetKindKey = "vw_local_work_fallback_kind";
        private const string UntilTicksKey = "vw_local_work_fallback_until";


        private sealed class NavigationProbe
        {
            internal string TargetName = string.Empty;
            internal string TargetKind = string.Empty;
            internal float BestDistance;
            internal float LastProgressAt;
            internal Vector3 LastPosition;
            internal float LastMovementAt;
        }

        private static readonly Dictionary<int, NavigationProbe> NavigationProbes = new Dictionary<int, NavigationProbe>();

        internal static bool ShouldActivateForNavigation(Character worker, ZDO zdo, string targetName, string targetKind, Vector3 targetPosition)
        {
            if (worker == null || zdo == null) return false;

            int key = worker.GetInstanceID();
            float distance = Vector3.Distance(worker.transform.position, targetPosition);
            if (!NavigationProbes.TryGetValue(key, out NavigationProbe probe) ||
                !string.Equals(probe.TargetName, targetName ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(probe.TargetKind, targetKind ?? string.Empty, StringComparison.Ordinal))
            {
                NavigationProbes[key] = new NavigationProbe
                {
                    TargetName = targetName ?? string.Empty,
                    TargetKind = targetKind ?? string.Empty,
                    BestDistance = distance,
                    LastProgressAt = Time.time,
                    LastPosition = worker.transform.position,
                    LastMovementAt = Time.time
                };
                return false;
            }

            if (distance < probe.BestDistance - 0.5f)
            {
                probe.BestDistance = distance;
                probe.LastProgressAt = Time.time;
            }

            Vector3 delta = worker.transform.position - probe.LastPosition;
            delta.y = 0f;
            if (delta.sqrMagnitude >= 0.75f * 0.75f)
            {
                probe.LastPosition = worker.transform.position;
                probe.LastMovementAt = Time.time;
            }

            string nav = WorkerNavigationRuntime.GetDebugStatus(worker) ?? string.Empty;
            bool navBlocked = nav.IndexOf("BLOCKED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              nav.IndexOf("path miss", StringComparison.OrdinalIgnoreCase) >= 0;
            bool stalled = Time.time - probe.LastProgressAt >= 10f && Time.time - probe.LastMovementAt >= 10f;
            bool noProgress = Time.time - probe.LastProgressAt >= 20f;
            bool blockedLongEnough = navBlocked && Time.time - probe.LastProgressAt >= 8f;

            if (!blockedLongEnough && !stalled && !noProgress) return false;

            Activate(zdo, targetName, targetKind, 30.0);
            NavigationProbes.Remove(key);
            return true;
        }

        internal static void ResetNavigationProbe(Character worker)
        {
            if (worker == null) return;
            NavigationProbes.Remove(worker.GetInstanceID());
        }

        internal static void Activate(ZDO zdo, string targetName, string targetKind, double seconds = 20.0)
        {
            if (zdo == null) return;
            zdo.Set(ActiveKey, true);
            zdo.Set(TargetNameKey, targetName ?? string.Empty);
            zdo.Set(TargetKindKey, targetKind ?? string.Empty);
            zdo.Set(UntilTicksKey, DateTime.UtcNow.AddSeconds(Math.Max(5.0, seconds)).Ticks);
        }

        internal static bool IsActive(ZDO zdo, string targetName, string targetKind)
        {
            if (zdo == null || !zdo.GetBool(ActiveKey, false)) return false;
            long until = zdo.GetLong(UntilTicksKey, 0L);
            if (until <= DateTime.UtcNow.Ticks)
            {
                Clear(zdo);
                return false;
            }

            string savedKind = zdo.GetString(TargetKindKey, string.Empty);
            string savedName = zdo.GetString(TargetNameKey, string.Empty);
            if (!string.IsNullOrEmpty(savedKind) && !string.Equals(savedKind, targetKind ?? string.Empty, StringComparison.Ordinal)) return false;
            if (!string.IsNullOrEmpty(savedName) && !string.Equals(savedName, targetName ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        internal static void Clear(ZDO zdo)
        {
            if (zdo == null) return;
            zdo.Set(ActiveKey, false);
            zdo.Set(TargetNameKey, string.Empty);
            zdo.Set(TargetKindKey, string.Empty);
            zdo.Set(UntilTicksKey, 0L);
        }
    }
}
