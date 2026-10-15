using System;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    internal static class CartographyOutpostRuntime
    {
        internal static bool IsCartographyTable(GameObject go)
        {
            return FindTableTransform(go) != null;
        }

        internal static Vector3 ResolveTablePosition(GameObject go)
        {
            Transform table = FindTableTransform(go);
            return table != null ? table.position : (go != null ? go.transform.position : Vector3.zero);
        }

        private static Transform FindTableTransform(GameObject go)
        {
            if (go == null) return null;
            Transform current = go.transform;
            for (int depth = 0; current != null && depth < 10; depth++, current = current.parent)
            {
                string objectName = NormalizeName(current.gameObject.name);
                if (objectName.IndexOf("cartography", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    objectName.IndexOf("maptable", StringComparison.OrdinalIgnoreCase) >= 0)
                    return current;

                try
                {
                    MonoBehaviour[] behaviours = current.GetComponents<MonoBehaviour>();
                    for (int i = 0; i < behaviours.Length; i++)
                    {
                        MonoBehaviour behaviour = behaviours[i];
                        if (behaviour != null && string.Equals(behaviour.GetType().Name, "MapTable", StringComparison.Ordinal))
                            return current;
                    }
                }
                catch
                {
                    // Missing optional behaviours on a child must not break vanilla interaction.
                }
            }
            return null;
        }

        private static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            int clone = name.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase);
            return clone >= 0 ? name.Substring(0, clone) : name;
        }
    }

    [HarmonyPatch(typeof(Player), "Interact", typeof(GameObject), typeof(bool), typeof(bool))]
    internal static class CartographyOutpostInteractPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Player __instance, GameObject go, bool hold, bool alt)
        {
            try
            {
                if (__instance == null || __instance != Player.m_localPlayer || hold || go == null) return true;
                if (!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift)) return true;
                if (!CartographyOutpostRuntime.IsCartographyTable(go)) return true;

                Plugin plugin = Plugin.Instance;
                if (plugin == null) return true;
                if (!plugin.TryOpenOutpostManager(__instance, go)) return true;
                return false;
            }
            catch
            {
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(Switch), "GetHoverText")]
    internal static class CartographyOutpostHoverPatch
    {
        private static void Postfix(Switch __instance, ref string __result)
        {
            try
            {
                if (__instance == null || Plugin.Instance == null) return;
                if (!CartographyOutpostRuntime.IsCartographyTable(__instance.gameObject)) return;
                const string hint = "[<color=yellow><b>Shift+E</b></color>] Workers-Aussenposten verwalten";
                if (string.IsNullOrEmpty(__result)) __result = hint;
                else if (__result.IndexOf("Workers-Aussenposten", StringComparison.OrdinalIgnoreCase) < 0) __result += "\\n" + hint;
            }
            catch { }
        }
    }
}
