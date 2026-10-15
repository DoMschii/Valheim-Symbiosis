using System;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Resolves a cheap, deterministic idle-on-duty post for loaded workers. This is presentation
    /// only: job availability/priorities remain owned by WorkerAiController/WorkerJobProfileRuntime.
    /// No world-wide object scan is performed; the Sorter uses the existing loaded container index.
    /// </summary>
    internal static class WorkerStandbyRuntime
    {
        internal const string TargetKind = "worker_standby";

        internal static bool TryResolve(Character worker, ZDO zdo, WorkerRole role, WorkOrder order,
            Vector3 workCenter, float workRadius, out Vector3 point, out string label)
        {
            point = worker != null ? worker.transform.position : workCenter;
            label = "Bereitschaft";
            if (worker == null || zdo == null) return false;

            // A Base Sorter should remain near the actual logistics endpoints instead of walking
            // back to a decorative bed after every empty queue. Import is preferred, then Export,
            // then the Base AirFreight hub. The query is local and uses WorkerWorldIndex.
            if (role == WorkerRole.Hauler && order == WorkOrder.Sort &&
                TryFindSorterBasePost(worker, zdo, workCenter, workRadius, out point, out label))
                return true;

            // Hunter/Guard Home is the center of the assigned HunterStand/guard platform. The same
            // rule also gives Firekeepers their dedicated hearth and every other specialist its
            // species rest station without inventing a second placement system.
            if (WorkerWorkAreaRules.HasRealHome(zdo))
            {
                point = WorkerZdo.ReadHome(zdo, worker.transform.position);
                switch (role)
                {
                    case WorkerRole.Hunter: label = order == WorkOrder.Guard ? "Jagdplattform / Wache" : "Jagdplattform"; break;
                    case WorkerRole.Guard: label = "Wachposten"; break;
                    case WorkerRole.Firekeeper: label = "Feuerwache"; break;
                    case WorkerRole.Gardener: label = "Gaertner-Ruhestaette"; break;
                    case WorkerRole.Butcher: label = "Metzger-Ruhestaette"; break;
                    case WorkerRole.Smith: label = "Schmied-Ruhestaette"; break;
                    case WorkerRole.Lumberjack: label = "Holzfaeller-Ruhestaette"; break;
                    case WorkerRole.Stonemason: label = "Steinmetz-Ruhestaette"; break;
                    default: label = "Ruhestaette"; break;
                }
                return true;
            }

            // Fail-soft for legacy workers whose real HomeAnchor has not been repaired yet. Keep
            // them inside the assigned Workzone, but never fabricate a persistent home assignment.
            if (workRadius > 0f)
            {
                point = workCenter;
                label = "Workzone-Mitte";
                return true;
            }

            return false;
        }

        internal static float GetStopDistance(ZDO zdo, WorkerRole role, WorkOrder order)
        {
            if (order == WorkOrder.Guard && zdo != null && GuardBehaviorRuntime.Read(zdo) == GuardBehaviorMode.Deploy)
                return GuardPatrolRuntime.StopDistance;
            if (role == WorkerRole.Hauler && order == WorkOrder.Sort)
                return 1.0f;
            if (zdo != null && WorkerWorkAreaRules.HasRealHome(zdo))
                return WorkerNavigationRuntime.GetHomeStopDistance(zdo);
            return 1.0f;
        }

        private static bool TryFindSorterBasePost(Character worker, ZDO workerZdo, Vector3 workCenter, float workRadius,
            out Vector3 point, out string label)
        {
            point = Vector3.zero;
            label = string.Empty;
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            float radius = Mathf.Clamp(Mathf.Max(60f, workRadius * 2f), 60f, WorkerStorageRuntime.LogisticsNetworkRadius);
            float bestScore = float.MaxValue;
            Container best = null;
            string bestLabel = string.Empty;

            WorkerStorageRuntime.ForEachContainerNear(workCenter, radius, candidate =>
            {
                if (candidate == null || !WorkerContainerRoleRuntime.MatchesOwner(candidate, ownerId)) return;

                WorkerContainerRole containerRole = WorkerContainerRoleRuntime.GetRole(candidate);
                int rank;
                string candidateLabel;
                if (containerRole == WorkerContainerRole.Import)
                {
                    rank = 0;
                    candidateLabel = "Import / Sortierposten";
                }
                else if (containerRole == WorkerContainerRole.Export)
                {
                    rank = 1;
                    candidateLabel = "Export / Sortierposten";
                }
                else if (containerRole == WorkerContainerRole.FieldDepot &&
                         WorkerContainerRoleRuntime.SupportsAirTransport(candidate) &&
                         WorkerContainerRoleRuntime.GetAirDepotMode(candidate) == AirDepotMode.BaseHub)
                {
                    rank = 2;
                    candidateLabel = "Base-Luftfracht / Sortierposten";
                }
                else return;

                Vector3 delta = candidate.transform.position - workCenter;
                delta.y = 0f;
                float score = rank * 1000000f + delta.sqrMagnitude;
                if (score >= bestScore) return;
                bestScore = score;
                best = candidate;
                bestLabel = candidateLabel;
            });

            if (best == null) return false;
            point = WorkerStorageRuntime.GetContainerApproachPoint(worker, best.transform.position);
            label = bestLabel;
            return true;
        }
    }
}
