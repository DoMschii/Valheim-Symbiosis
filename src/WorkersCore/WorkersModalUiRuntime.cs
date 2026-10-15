using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// One shared modal-input gate for every Workers IMGUI popup.
    /// The UI itself stays in Plugin; these Harmony guards only prevent Valheim from
    /// consuming movement/action/camera input while a Workers window owns the mouse.
    /// </summary>
    internal static class WorkersModalUiRuntime
    {
        internal static bool Active { get; private set; }

        internal static void SetActive(bool active)
        {
            Active = active;
        }
    }

    [HarmonyPatch(typeof(Player), "TakeInput")]
    internal static class WorkersModalPlayerTakeInputPatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (!WorkersModalUiRuntime.Active) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerController), "TakeInput")]
    internal static class WorkersModalPlayerControllerTakeInputPatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (!WorkersModalUiRuntime.Active) return true;
            __result = false;
            return false;
        }
    }
}

namespace Valheim_Workers
{
    // Hard action guards: Valheim can still reach attack/interact code paths even while
    // TakeInput is suppressed (notably IMGUI mouse clicks and the Workzone bird-view).
    // Keep the local player completely passive while a Workers modal UI owns the mouse.
    [HarmonyLib.HarmonyPatch(typeof(Character), "StartAttack", new System.Type[] { typeof(Character), typeof(bool) })]
    internal static class WorkersModalCharacterStartAttackPatch
    {
        private static bool Prefix(Character __instance, ref bool __result)
        {
            if (!WorkersModalUiRuntime.Active || __instance != Player.m_localPlayer) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(Player), "Interact", new System.Type[] { typeof(GameObject), typeof(bool), typeof(bool) })]
    internal static class WorkersModalPlayerInteractPatch
    {
        [HarmonyLib.HarmonyPriority(HarmonyLib.Priority.First)]
        private static bool Prefix(Player __instance)
        {
            if (!WorkersModalUiRuntime.Active || __instance != Player.m_localPlayer) return true;
            // Player.Interact(GameObject,bool,bool) returns void in current Valheim.
            // Returning false from the Harmony prefix is enough to suppress the original call;
            // a __result parameter here would make Harmony fail the entire PatchAll.
            return false;
        }
    }
}
