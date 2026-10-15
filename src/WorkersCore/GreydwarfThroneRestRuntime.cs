using System;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Compatibility shim for the old Greydwarf throne presentation.
    ///
    /// v0.5.150 deliberately stops forcing a synthetic sit/perch pose. Greyling-family workers now
    /// use the shared WorkerRestStation presentation and stand naturally inside a root stump while
    /// their normal creature idle/rest animator keeps running. The class/anchor name stay in place
    /// so existing saves and callers do not need migration.
    /// </summary>
    internal static class GreydwarfThroneRestRuntime
    {
        internal const string SeatPointName = "Workers_GreydwarfRestSeat";

        private static Action<string> _debugLog;

        internal static void Initialize(Action<string> debugLog)
        {
            _debugLog = debugLog;
            _debugLog?.Invoke("Greydwarf root-stump rest: native standing/idle presentation active; synthetic throne pose disabled.");
        }

        internal static void Shutdown()
        {
            _debugLog = null;
        }

        internal static void ReleaseIfNeeded(Character worker, ZDO zdo, WorkerState state)
        {
            // No attach/procedural pose exists anymore. The shared rest presentation only moves the
            // Character while it is in a rest/home state and therefore needs no detach bookkeeping.
        }

        internal static bool TryMaintainSeat(Character worker, ZDO zdo)
        {
            // Returning false hands presentation to WorkerRestStationPresentationRuntime, which
            // places the creature at the exact 1:1 RestPoint and leaves its native animator alone.
            return false;
        }

        internal static string GetDebugStatus(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return "inactive";
            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state != WorkerState.Resting && state != WorkerState.Recover && state != WorkerState.WaitAtHome)
                return "inactive";

            string anchor = zdo.GetString(WorkerZdo.HomeAnchorType, string.Empty);
            if (!string.Equals(anchor, GreydwarfHomeAnchorZdo.GreydwarfNestKind, StringComparison.Ordinal))
                return "inactive";

            WorkerRestStation station = WorkerRestStation.ResolveLoadedForWorker(zdo);
            return station != null && station.RestPoint != null
                ? "Wurzelnest: native Idle-Pose am festen RestPoint"
                : "Wurzelnest: RestPoint nicht geladen";
        }
    }
}
