using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Persistent forestry replants are driven by the real stump lifecycle instead of a periodic
    /// physics scan. When a real tree stump is actually destroyed, reactivate the nearest stored
    /// forestry slot at that position. Scene unloads do not call Destructible.Destroy(), so merely
    /// streaming a workzone out does not create a false replant request.
    /// </summary>
    [HarmonyPatch]
    internal static class ForestryStumpDestroyedPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.DeclaredMethod(typeof(Destructible), "Destroy");
        }

        private static bool Prepare()
        {
            // Fail closed on a future Valheim API change instead of breaking Plugin.PatchAll().
            return TargetMethod() != null;
        }

        private static void Prefix(Destructible __instance)
        {
            if (__instance == null || __instance.gameObject == null) return;
            if (!TreeTargetFinder.IsTreeStump(__instance)) return;

            Vector3 position = __instance.transform.position;
            if (!ForestryPlanRuntime.ReactivateNearest(0L, position, 2.6f)) return;

            Plugin.Log?.LogDebug($"Forestry persistent slot reactivated from real stump destruction at {position}.");
        }
    }
}
