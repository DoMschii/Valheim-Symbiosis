using System;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// First physical logistics loop. A Hauler moves one real item type at a time from a marked
    /// FieldDepot to a marked Import chest. Source and destination are ordinary Valheim Containers;
    /// cargo is persisted on the Worker ZDO while in transit.
    /// </summary>
    internal static class HaulerWorkRuntime
    {
        internal static int GetCarryCapacity(Character worker)
        {
            WorkerCargoLimits limits = WorkerCargoRuntime.GetLimits(worker);
            return Mathf.Max(1, limits.Slots);
        }

        internal static int GetCarryCapacity(Character worker, string prefabName, int available = int.MaxValue)
        {
            return WorkerCargoRuntime.GetMaxSinglePrefabLoad(worker, prefabName, available);
        }
    }
}
