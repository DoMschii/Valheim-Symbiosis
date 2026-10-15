using System;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Keeps a Worker work assignment physically tied to its real HomeAnchor.
    /// A Workzone is a local outpost: the Worker must sleep inside the same flag radius
    /// instead of commuting across the map every night.
    /// </summary>
    internal static class WorkerWorkAreaRules
    {
        internal static bool HasRealHome(ZDO workerZdo)
        {
            return WorkerZdo.IsWorker(workerZdo) &&
                   !string.IsNullOrEmpty(workerZdo.GetString(WorkerZdo.HomeAnchorType, string.Empty));
        }

        internal static bool IsPointInsideArea(Vector3 point, Vector3 center, float radius)
        {
            float r = Mathf.Max(0f, radius);
            float dx = point.x - center.x;
            float dz = point.z - center.z;
            return dx * dx + dz * dz <= r * r;
        }

        internal static bool TryValidateHomeForArea(ZDO workerZdo, Vector3 center, float radius, out string reason)
        {
            reason = string.Empty;
            if (!WorkerZdo.IsWorker(workerZdo))
            {
                reason = "kein gueltiger Worker";
                return false;
            }

            if (!HasRealHome(workerZdo))
            {
                reason = "kein Schlafplatz/HomeAnchor zugewiesen";
                return false;
            }

            Vector3 home = WorkerZdo.ReadHome(workerZdo, workerZdo.GetPosition());
            if (!IsPointInsideArea(home, center, radius))
            {
                float distance = HorizontalDistance(home, center);
                reason = $"Schlafplatz liegt ausserhalb der Workzone ({distance:0.0} m vom Fahnenzentrum, Radius {radius:0} m)";
                return false;
            }

            return true;
        }

        internal static bool TryValidateProposedHomeForCurrentAssignment(ZDO workerZdo, Vector3 proposedHome, out string workAreaName, out string reason)
        {
            workAreaName = string.Empty;
            reason = string.Empty;
            if (!WorkerZdo.IsWorker(workerZdo)) return true;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius)) return true;

            workAreaName = WorkerZdo.ReadWorkAreaName(workerZdo);
            if (string.IsNullOrWhiteSpace(workAreaName)) workAreaName = "Workzone";
            if (IsPointInsideArea(proposedHome, center, radius)) return true;

            float distance = HorizontalDistance(proposedHome, center);
            reason = $"Schlafplatz muss innerhalb von {workAreaName} liegen ({distance:0.0} m vom Zentrum, Radius {radius:0} m)";
            return false;
        }

        internal static string DescribeHomeStatus(ZDO workerZdo, Vector3 center, float radius)
        {
            if (TryValidateHomeForArea(workerZdo, center, radius, out _)) return "Schlafplatz: OK (in dieser Workzone)";
            if (!HasRealHome(workerZdo)) return "Schlafplatz: FEHLT";
            Vector3 home = WorkerZdo.ReadHome(workerZdo, workerZdo.GetPosition());
            return $"Schlafplatz: AUSSERHALB ({HorizontalDistance(home, center):0.0} m / Radius {radius:0} m)";
        }

        internal static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
