using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// RC1 permanent fix for Valheim 1.0.20 WearNTear.UpdateBiome crashes caused by
    /// Workers production displays.
    ///
    /// Root cause confirmed by diagnostics:
    ///   ValheimWorkers_ProductionDisplayLandscape(Clone)
    ///   ValheimWorkers_ProductionDisplayPortrait(Clone)
    /// keep the cloned sign's WearNTear, but their custom renderer setup has no valid
    /// WearNTear snow renderers (m_snow/m_snowWorn/m_snowBroken are null).
    ///
    /// Production displays do not need vanilla biome/snow visual switching, so only
    /// WearNTear.UpdateBiome is skipped for these two Workers prefabs. Damage, repair,
    /// support, health and all other WearNTear behavior remain active.
    ///
    /// The generic exception quarantine remains as a fail-safe for any unrelated
    /// malformed prefab, but it does no hierarchy scan on healthy objects.
    /// </summary>
    internal static class WearNTearBiomeGuardRuntime
    {
        private const string PortraitName = "ValheimWorkers_ProductionDisplayPortrait";
        private const string LandscapeName = "ValheimWorkers_ProductionDisplayLandscape";

        private static readonly Dictionary<int, WearNTear> Quarantined =
            new Dictionary<int, WearNTear>(16);

        private static readonly HashSet<int> LoggedProductionDisplays =
            new HashSet<int>();

        internal static bool ShouldRun(WearNTear value)
        {
            if (value == null || value.gameObject == null)
                return false;

            int id;
            try { id = value.GetInstanceID(); }
            catch { return true; }

            WearNTear known;
            if (Quarantined.TryGetValue(id, out known))
            {
                if (ReferenceEquals(known, value))
                    return false;

                Quarantined.Remove(id);
            }

            string objectName = value.gameObject.name ?? string.Empty;
            if (IsProductionDisplayName(objectName))
            {
                if (LoggedProductionDisplays.Add(id))
                {
                    try
                    {
                        if (Plugin.Log != null)
                            Plugin.Log.LogInfo("[WearNTearFix] Production display biome visuals disabled safely: " +
                                               objectName + " id=" + id + ".");
                    }
                    catch { }
                }

                return false;
            }

            return true;
        }

        internal static Exception HandleException(WearNTear value, Exception exception)
        {
            if (exception == null)
                return null;

            if (!(exception is NullReferenceException))
                return exception;

            string stack = exception.StackTrace ?? string.Empty;
            bool rendererBounds =
                stack.IndexOf("Renderer.get_bounds", StringComparison.OrdinalIgnoreCase) >= 0 ||
                stack.IndexOf("WearNTear.UpdateBiome", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!rendererBounds)
                return exception;

            Quarantine(value, "Renderer.get_bounds NullReferenceException", exception);
            return null;
        }

        private static bool IsProductionDisplayName(string objectName)
        {
            if (string.IsNullOrEmpty(objectName))
                return false;

            return objectName.StartsWith(PortraitName, StringComparison.OrdinalIgnoreCase) ||
                   objectName.StartsWith(LandscapeName, StringComparison.OrdinalIgnoreCase);
        }

        private static void Quarantine(WearNTear value, string reason, Exception exception)
        {
            if (value == null)
                return;

            int id;
            try { id = value.GetInstanceID(); }
            catch { return; }

            WearNTear existing;
            if (Quarantined.TryGetValue(id, out existing) && ReferenceEquals(existing, value))
                return;

            Quarantined[id] = value;

            try
            {
                GameObject go = value.gameObject;
                Piece piece = go != null ? go.GetComponent<Piece>() : null;

                // Expensive diagnostics only run after a real exception.
                Renderer[] renderers = go != null ? go.GetComponentsInChildren<Renderer>(true) : null;
                int total = renderers != null ? renderers.Length : 0;
                int valid = 0;
                int enabled = 0;
                string rendererNames = string.Empty;

                if (renderers != null)
                {
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        Renderer renderer = renderers[i];
                        if (renderer == null || renderer.gameObject == null)
                            continue;

                        valid++;
                        if (renderer.enabled && renderer.gameObject.activeInHierarchy)
                            enabled++;

                        if (rendererNames.Length < 700)
                        {
                            if (rendererNames.Length > 0) rendererNames += ", ";
                            rendererNames += renderer.GetType().Name + ":" + renderer.gameObject.name +
                                             "(enabled=" + renderer.enabled +
                                             ",active=" + renderer.gameObject.activeInHierarchy + ")";
                        }
                    }
                }

                string fieldState = DescribeRendererFields(value);
                string path = go != null ? GetTransformPath(go.transform) : "<no-gameobject>";
                string pieceName = piece != null ? piece.m_name : "<no-piece>";
                Vector3 pos = go != null ? go.transform.position : Vector3.zero;

                string message =
                    "[WearNTearGuard] QUARANTINED UpdateBiome | reason=" + reason +
                    " | id=" + id +
                    " | object=" + (go != null ? go.name : "<null>") +
                    " | piece=" + pieceName +
                    " | pos=(" + pos.x.ToString("0.0") + "," + pos.y.ToString("0.0") + "," + pos.z.ToString("0.0") + ")" +
                    " | path=" + path +
                    " | renderers total/valid/enabled=" + total + "/" + valid + "/" + enabled +
                    " | rendererFields=" + fieldState +
                    " | renderers=[" + rendererNames + "]";

                if (Plugin.Log != null)
                    Plugin.Log.LogWarning(message);
                else
                    Debug.LogWarning("[Valheim_Workers] " + message);

                if (exception != null && Plugin.Log != null)
                    Plugin.Log.LogWarning("[WearNTearGuard] Original UpdateBiome exception suppressed for this instance: " +
                                          exception.GetType().Name + ": " + exception.Message);
            }
            catch (Exception logException)
            {
                try
                {
                    if (Plugin.Log != null)
                        Plugin.Log.LogWarning("[WearNTearGuard] Quarantine diagnostics failed: " +
                                              logException.GetType().Name + ": " + logException.Message);
                }
                catch { }
            }
        }

        private static string DescribeRendererFields(WearNTear value)
        {
            try
            {
                FieldInfo[] fields = typeof(WearNTear).GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                string result = string.Empty;
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];
                    if (field == null || !typeof(Renderer).IsAssignableFrom(field.FieldType))
                        continue;

                    Renderer renderer = null;
                    try { renderer = field.GetValue(value) as Renderer; }
                    catch { }

                    if (result.Length > 0) result += ", ";
                    result += field.Name + "=" +
                              (renderer != null && renderer.gameObject != null
                                  ? renderer.gameObject.name
                                  : "<null>");
                }

                return result.Length > 0 ? result : "<no Renderer fields>";
            }
            catch (Exception ex)
            {
                return "<reflection failed: " + ex.GetType().Name + ">";
            }
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
                return "<null>";

            string path = transform.name;
            Transform current = transform.parent;
            int depth = 0;

            while (current != null && depth < 12)
            {
                path = current.name + "/" + path;
                current = current.parent;
                depth++;
            }

            return path;
        }
    }

    [HarmonyPatch(typeof(WearNTear), "UpdateBiome")]
    internal static class WearNTearUpdateBiomeGuardPatch
    {
        private static bool Prefix(WearNTear __instance)
        {
            return WearNTearBiomeGuardRuntime.ShouldRun(__instance);
        }

        private static Exception Finalizer(WearNTear __instance, Exception __exception)
        {
            return WearNTearBiomeGuardRuntime.HandleException(__instance, __exception);
        }
    }
}
