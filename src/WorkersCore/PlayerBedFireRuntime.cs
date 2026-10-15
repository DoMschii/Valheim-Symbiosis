using System;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Valheim's Bed sleep check does not rely solely on the player's Fire EffectArea status.
    /// Treat a nearby loaded Worker rest station as satisfying the bed's fire requirement.
    /// This keeps the worker home itself as the single shared campfire/rest point without
    /// creating or consuming any extra vanilla fuel.
    /// </summary>
    internal static class PlayerBedFireRuntime
    {
        private const float RestStationFireRadius = 10f;

        internal static bool HasNearbyWorkerRestStation(Vector3 position)
        {
            return WorkerRestStation.HasPlacedStationWithin(position, RestStationFireRadius);
        }
    }

    [HarmonyPatch(typeof(Bed), "CheckFire")]
    internal static class BedCheckFireWorkerRestStationPatch
    {
        private static void Postfix(Bed __instance, ref bool __result)
        {
            if (__result || __instance == null) return;
            if (PlayerBedFireRuntime.HasNearbyWorkerRestStation(__instance.transform.position))
                __result = true;
        }
    }
}
