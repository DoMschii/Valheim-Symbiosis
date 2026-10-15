using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Shared proximity/approach rules for worker interactions with world objects such as storage
    /// and production machines. This intentionally reuses the already-stable storage interaction
    /// cylinder and its ground sampling instead of changing the navigator itself.
    /// </summary>
    internal static class WorkerInteractionRuntime
    {
        internal static float AccessRadius => WorkerStorageRuntime.ContainerAccessRadius;
        internal static float ApproachStopDistance => WorkerStorageRuntime.ContainerApproachStopDistance;

        internal static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            return WorkerStorageRuntime.HorizontalDistance(a, b);
        }

        internal static bool IsInsideAura(Vector3 workerPosition, Vector3 targetPosition)
        {
            return HorizontalDistance(workerPosition, targetPosition) <= AccessRadius;
        }

        internal static Vector3 GetApproachPoint(Character worker, Vector3 targetPosition)
        {
            return WorkerStorageRuntime.GetContainerApproachPoint(worker, targetPosition);
        }

        internal static void ClearApproach(Character worker)
        {
            WorkerStorageRuntime.ClearContainerApproach(worker);
        }
    }
}
