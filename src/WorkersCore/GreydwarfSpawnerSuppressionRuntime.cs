using System;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Persistent safety gate for Greydwarf SpawnArea objects pacified by Workers.
    /// The previous scanner-based enforcement could run after vanilla's first spawn tick
    /// following a world load. This guard executes at SpawnArea lifecycle boundaries instead.
    /// </summary>
    internal static class GreydwarfSpawnerSuppressionRuntime
    {
        internal static bool ShouldSuppress(SpawnArea spawner)
        {
            if (spawner == null) return false;

            ZNetView nview = spawner.GetComponent<ZNetView>();
            if (nview == null) nview = spawner.GetComponentInParent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;

            ZDO zdo = nview.GetZDO();
            if (zdo == null) return false;

            // Pacification itself is enough to stop new hostile creatures. Consumed is checked
            // as well for forward/backward schema safety and makes the intent explicit.
            return SpawnerRecruitmentZdo.IsPacified(zdo) || SpawnerRecruitmentZdo.IsConsumed(zdo);
        }

        internal static bool TrySuppress(SpawnArea spawner, Action<string> debugLog = null)
        {
            if (!ShouldSuppress(spawner)) return false;

            if (spawner.enabled)
            {
                // Public Unity behaviour only: do not touch private SpawnArea timers/fields.
                // Cancelling invokes closes the window where an already-scheduled UpdateSpawn
                // callback could survive component shutdown after a world reload.
                spawner.CancelInvoke();
                spawner.enabled = false;
                debugLog?.Invoke($"Pacified Greydwarf SpawnArea suppressed: {spawner.gameObject.name}.");
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(SpawnArea), "Awake")]
    internal static class GreydwarfSpawnerAwakeSuppressionPatch
    {
        private static void Postfix(SpawnArea __instance)
        {
            // ZNetView/ZDO can already be valid here. If it is not, UpdateSpawnPrefix below is
            // still the authoritative guard and will retry before vanilla can spawn anything.
            GreydwarfSpawnerSuppressionRuntime.TrySuppress(__instance);
        }
    }

    [HarmonyPatch(typeof(SpawnArea), "UpdateSpawn")]
    internal static class GreydwarfSpawnerUpdateSpawnSuppressionPatch
    {
        private static bool Prefix(SpawnArea __instance)
        {
            // Returning false skips the original SpawnArea.UpdateSpawn entirely. This closes the
            // login/reload race even when the throttled nearby-nest scanner has not run yet.
            return !GreydwarfSpawnerSuppressionRuntime.TrySuppress(__instance);
        }
    }
}
