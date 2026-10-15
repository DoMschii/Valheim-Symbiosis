using System;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// A selected Drake mount is a player companion, not a Workzone worker.
    /// Keep the core generic by enforcing this rule entirely from the optional Mount add-on.
    /// </summary>
    [HarmonyPatch(typeof(WorkerZdo), nameof(WorkerZdo.AssignWorkArea), new Type[]
    {
        typeof(ZDO), typeof(string), typeof(string), typeof(WorkOrder), typeof(Vector3), typeof(float)
    })]
    internal static class DrakeMountWorkAreaBlockPatch
    {
        private static bool Prefix(ZDO zdo)
        {
            if (!DrakeMountAssignmentRuntime.IsAssignedMount(zdo)) return true;
            WorkerZdo.ClearWorkArea(zdo);
            WorkerZdo.ClearTarget(zdo);
            if ((WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle) != WorkerState.Follow)
                zdo.Set(WorkerZdo.State, (int)WorkerState.Follow);
            return false;
        }
    }
}
