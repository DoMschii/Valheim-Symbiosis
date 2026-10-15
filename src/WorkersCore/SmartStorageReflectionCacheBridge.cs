using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Valheim_Workers
{
    /// <summary>
    /// Compatibility fix for SmartStorage 0.5.39 on Valheim 1.0.17.
    /// SmartStorage's StationReflection.GetMember helper can repeatedly probe optional
    /// Fireplace members that do not exist in this Valheim build (m_conversion / m_conversions).
    /// Harmony's AccessTools emits a warning and repeats reflection work on every probe.
    ///
    /// This patch memoizes SmartStorage's own reflection helper, including negative lookups.
    /// A missing member is therefore resolved once and remains a cached miss instead of being
    /// reflected/logged hundreds of times. No Fireplace behavior is changed.
    /// </summary>
    internal static class SmartStorageReflectionCacheBridge
    {
        private static bool _installed;
        private static bool _gaveUp;
        private static float _nextAttempt;
        private static readonly Dictionary<string, object> Hits = new Dictionary<string, object>(StringComparer.Ordinal);
        private static readonly HashSet<string> Misses = new HashSet<string>(StringComparer.Ordinal);

        internal static void TryInstall(Harmony harmony)
        {
            if (_installed || _gaveUp || harmony == null) return;
            if (UnityEngine.Time.unscaledTime < _nextAttempt) return;
            _nextAttempt = UnityEngine.Time.unscaledTime + 2f;

            Type stationReflection = FindType("Valheim_SmartStorage.Stations.StationReflection");
            if (stationReflection == null) return; // SmartStorage loads after Workers; retry later.

            MethodInfo prefix = typeof(SmartStorageReflectionCacheBridge).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo postfix = typeof(SmartStorageReflectionCacheBridge).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic);
            if (prefix == null || postfix == null)
            {
                _gaveUp = true;
                return;
            }

            int patched = 0;
            MethodInfo[] methods;
            try { methods = stationReflection.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
            catch { _gaveUp = true; return; }

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method == null || method.Name != "GetMember") continue;
                if (method.ReturnType == typeof(void) || method.ReturnType.IsValueType) continue;
                try
                {
                    harmony.Patch(method, new HarmonyMethod(prefix), new HarmonyMethod(postfix));
                    patched++;
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning("SmartStorage reflection-cache patch skipped one GetMember overload: " + ex.Message);
                }
            }

            if (patched > 0)
            {
                _installed = true;
                Plugin.Log?.LogInfo("SmartStorage StationReflection cache installed; repeated optional-member misses are now memoized.");
            }
            else
            {
                _gaveUp = true;
                Plugin.Log?.LogWarning("SmartStorage StationReflection.GetMember was found, but no reference-returning overload could be patched.");
            }
        }

        private static Type FindType(string fullName)
        {
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    Assembly asm = assemblies[i];
                    if (asm == null) continue;
                    Type type = asm.GetType(fullName, false);
                    if (type != null) return type;
                }
            }
            catch { }
            return null;
        }

        // Harmony supplies the original method and boxed arguments. We only short-circuit a
        // lookup after SmartStorage itself has already resolved that exact argument set once.
        private static bool Prefix(MethodBase __originalMethod, object[] __args, ref object __result)
        {
            string key = BuildKey(__originalMethod, __args);
            if (string.IsNullOrEmpty(key)) return true;

            if (Hits.TryGetValue(key, out object cached))
            {
                __result = cached;
                return false;
            }
            if (Misses.Contains(key))
            {
                __result = null;
                return false;
            }
            return true;
        }

        private static void Postfix(MethodBase __originalMethod, object[] __args, object __result)
        {
            string key = BuildKey(__originalMethod, __args);
            if (string.IsNullOrEmpty(key)) return;
            if (__result != null) Hits[key] = __result;
            else Misses.Add(key); // negative cache is the important part for missing Fireplace fields.
        }

        private static string BuildKey(MethodBase method, object[] args)
        {
            if (method == null) return null;
            try
            {
                string key = method.DeclaringType?.FullName + "::" + method.Name;
                if (args == null) return key;
                for (int i = 0; i < args.Length; i++)
                {
                    object arg = args[i];
                    if (arg is Type t) key += "|T:" + t.AssemblyQualifiedName;
                    else if (arg is string s) key += "|S:" + s;
                    else if (arg is BindingFlags f) key += "|F:" + ((int)f).ToString();
                    else if (arg == null) key += "|null";
                    else key += "|" + arg.GetType().FullName + ":" + arg;
                }
                return key;
            }
            catch { return null; }
        }
    }
}
