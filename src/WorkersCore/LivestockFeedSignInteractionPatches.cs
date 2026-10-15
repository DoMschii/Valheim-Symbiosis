using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    [HarmonyPatch(typeof(Sign), "Interact", typeof(Humanoid), typeof(bool), typeof(bool))]
    internal static class LivestockFeedSignInteractPatch
    {
        private static bool Prefix(Sign __instance, Humanoid __0, bool __1, bool __2, ref bool __result)
        {
            if (__instance == null || __1) return true;
            LivestockFeedSignMarker marker = __instance.GetComponent<LivestockFeedSignMarker>()
                ?? __instance.GetComponentInParent<LivestockFeedSignMarker>();
            if (marker == null) return true;

            bool menuRequest = __2 || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!menuRequest) return true; // legacy sign keeps vanilla text editing on plain E

            Plugin plugin = Plugin.Instance;
            __result = plugin != null && plugin.TryOpenLivestockFeedMenu(marker, __0);
            return false;
        }
    }

    [HarmonyPatch(typeof(Sign), "GetHoverText")]
    internal static class LivestockFeedSignHoverPatch
    {
        private static void Postfix(Sign __instance, ref string __result)
        {
            if (__instance == null) return;
            LivestockFeedSignMarker marker = __instance.GetComponent<LivestockFeedSignMarker>()
                ?? __instance.GetComponentInParent<LivestockFeedSignMarker>();
            if (marker == null) return;
            __result = "Tierpflege-Schild\n[<color=yellow><b>Shift+E</b></color>] Futter verwalten\n[<color=yellow><b>E</b></color>] Schildtext";
        }
    }

    /// <summary>
    /// Dedicated feeding stations use a Container only as hidden networked storage.  They are not
    /// player chests: E opens livestock/pen configuration (trough, or the nearest trough when E is
    /// used on a dispenser) and never falls through to vanilla/SmartStorage chest interaction.
    /// </summary>
    [HarmonyPatch(typeof(Container), "Interact", typeof(Humanoid), typeof(bool), typeof(bool))]
    internal static class LivestockFeedTroughInteractPatch
    {
        [HarmonyPriority(Priority.First + 200)]
        private static bool Prefix(Container __instance, Humanoid __0, bool __1, bool __2, ref bool __result)
        {
            if (__instance == null || !LivestockFeedingRuntime.IsFeedingStation(__instance)) return true;

            // Feeding stations are automated devices. Their Container is deliberately never opened
            // as a normal chest, including hold/alternate interactions.
            __result = true;
            if (__1) return false;
            if (__0 == null || Player.m_localPlayer == null || __0 != Player.m_localPlayer) return false;

            LivestockFeedSignMarker marker = null;
            if (LivestockFeedingRuntime.IsFeedingTrough(__instance))
            {
                marker = __instance.GetComponent<LivestockFeedSignMarker>()
                    ?? __instance.GetComponentInParent<LivestockFeedSignMarker>();
                if (marker == null) marker = __instance.gameObject.AddComponent<LivestockFeedSignMarker>();
            }
            else
            {
                // A dispenser belongs to the nearest pen/trough. Opening it routes to that pen's
                // configuration instead of exposing the technical inventory.
                LivestockFeedSignRegistry.TryFindNearest(__instance.transform.position, 40f, out marker);
            }

            Plugin plugin = Plugin.Instance;
            if (plugin != null && marker != null)
                __result = plugin.TryOpenLivestockFeedMenu(marker, __0);
            return false;
        }
    }

    [HarmonyPatch(typeof(Container), "GetHoverText")]
    internal static class LivestockFeedTroughHoverPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Container __instance, ref string __result)
        {
            if (__instance == null || !LivestockFeedingRuntime.IsFeedingStation(__instance)) return;

            if (LivestockFeedingRuntime.IsFeedingTrough(__instance))
            {
                __result = "Futtertrog\n[<color=yellow><b>E</b></color>] Gehege / Futter verwalten\nWird vom Tierpfleger/Metzger befuellt\nGibt bei hungrigen Tieren automatisch Futter aus";
            }
            else
            {
                __result = "Futterspender\n[<color=yellow><b>E</b></color>] Gehege / Futter verwalten\nWird vom Tierpfleger/Metzger befuellt\nGibt bei hungrigen Tieren automatisch Futter aus";
            }
        }
    }
}
