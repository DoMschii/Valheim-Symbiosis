using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Optional, reflection-only compatibility layer for SmartReplant 1.7.x.
    ///
    /// SmartReplant owns several manual planting paths:
    /// 1) Player.UpdatePlacement -> ManualFloraPatch.Place(Player, Piece)
    /// 2) multi-cell planting -> FieldPlantingService.Plan(...)
    /// 3) FieldPlantingService.Controls(...) changes its own field dimensions.
    ///
    /// While Workers planting planning is active, Workers must be the exclusive owner of the
    /// planting click. We therefore intercept both placement entry points and suspend SmartReplant's
    /// own field-size input. No compile-time SmartReplant reference is required.
    /// </summary>
    internal static class SmartReplantPlanningCompatibility
    {
        private const string ManualFloraPatchTypeName = "SmartReplant.ManualFloraPatch";
        private const string FieldPlantingServiceTypeName = "SmartReplant.FieldPlantingService";
        private const string SmartReplantPluginTypeName = "SmartReplant.SmartReplantPlugin";

        private static bool _manualPlaceInstalled;
        private static int _manualPlacementPatchCount;
        private static bool _fieldPlanInstalled;
        private static bool _fieldControlsInstalled;
        private static bool _reportedMissing;
        private static float _nextAttemptAt;

        // Cached reflection for cancelling an already-running SmartReplant field job.
        private static FieldInfo _pluginInstanceField;
        private static FieldInfo _pluginFieldsField;
        private static PropertyInfo _fieldActiveProperty;
        private static MethodInfo _fieldCancelMethod;

        internal static bool Installed => _manualPlaceInstalled && _fieldPlanInstalled && _fieldControlsInstalled;

        internal static bool TryInstall(Harmony harmony, Action<string> debugLog)
        {
            if (Installed) return true;
            if (harmony == null) return false;

            if (Time.unscaledTime < _nextAttemptAt) return Installed;
            _nextAttemptAt = Time.unscaledTime + 1.5f;

            try
            {
                Type manualType = AccessTools.TypeByName(ManualFloraPatchTypeName);
                Type fieldType = AccessTools.TypeByName(FieldPlantingServiceTypeName);

                if (manualType != null && !_manualPlaceInstalled)
                {
                    MethodInfo prefixMethod = AccessTools.Method(typeof(SmartReplantPlanningCompatibility), nameof(ManualPlacementEntryPrefix));
                    if (prefixMethod != null)
                    {
                        HarmonyMethod prefix = new HarmonyMethod(prefixMethod) { priority = Priority.First };
                        MethodInfo[] methods = manualType.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        for (int i = 0; i < methods.Length; i++)
                        {
                            MethodInfo target = methods[i];
                            if (target == null) continue;
                            if (!string.Equals(target.Name, "Place", StringComparison.Ordinal) &&
                                !string.Equals(target.Name, "TryPlace", StringComparison.Ordinal) &&
                                !string.Equals(target.Name, "TryPlacePiece", StringComparison.Ordinal))
                                continue;

                            ParameterInfo[] parameters = target.GetParameters();
                            bool hasPlayer = false;
                            bool hasPiece = false;
                            for (int p = 0; p < parameters.Length; p++)
                            {
                                Type parameterType = parameters[p].ParameterType;
                                if (parameterType == typeof(Player)) hasPlayer = true;
                                if (parameterType == typeof(Piece)) hasPiece = true;
                            }
                            if (!hasPlayer || !hasPiece) continue;

                            harmony.Patch(target, prefix: prefix);
                            _manualPlacementPatchCount++;
                            debugLog?.Invoke($"SmartReplant planning lock: {manualType.FullName}.{target.Name} intercepted ({target.ReturnType.Name}).");
                        }

                        _manualPlaceInstalled = _manualPlacementPatchCount > 0;
                    }
                }

                if (fieldType != null && !_fieldPlanInstalled)
                {
                    MethodInfo target = AccessTools.Method(fieldType, "Plan",
                        new[] { typeof(Player), typeof(Piece), typeof(Vector3), typeof(Quaternion), typeof(bool) });
                    MethodInfo prefixMethod = AccessTools.Method(typeof(SmartReplantPlanningCompatibility), nameof(FieldPlanPrefix));
                    if (target != null && prefixMethod != null)
                    {
                        HarmonyMethod prefix = new HarmonyMethod(prefixMethod) { priority = Priority.First };
                        harmony.Patch(target, prefix: prefix);
                        _fieldPlanInstalled = true;
                        debugLog?.Invoke("SmartReplant planning lock: FieldPlantingService.Plan intercepted.");
                    }
                }

                if (fieldType != null && !_fieldControlsInstalled)
                {
                    MethodInfo target = AccessTools.Method(fieldType, "Controls", new[] { typeof(Player) });
                    MethodInfo prefixMethod = AccessTools.Method(typeof(SmartReplantPlanningCompatibility), nameof(FieldControlsPrefix));
                    if (target != null && prefixMethod != null)
                    {
                        HarmonyMethod prefix = new HarmonyMethod(prefixMethod) { priority = Priority.First };
                        harmony.Patch(target, prefix: prefix);
                        _fieldControlsInstalled = true;
                        debugLog?.Invoke("SmartReplant planning lock: FieldPlantingService.Controls suspended while Workers planning is active.");
                    }
                }

                CacheCancellationReflection(fieldType);

                if (Installed)
                {
                    _reportedMissing = false;
                    debugLog?.Invoke("SmartReplant planning compatibility ACTIVE: Workers owns planting clicks exclusively while planning mode is enabled.");
                    return true;
                }

                if (SmartReplantBridge.IsInstalled && !_reportedMissing)
                {
                    _reportedMissing = true;
                    debugLog?.Invoke($"SmartReplant planning compatibility incomplete: manual={_manualPlaceInstalled}, fieldPlan={_fieldPlanInstalled}, fieldControls={_fieldControlsInstalled}. Native Workers TryPlacePiece gate remains active.");
                }
                return false;
            }
            catch (Exception error)
            {
                if (!_reportedMissing)
                {
                    _reportedMissing = true;
                    debugLog?.Invoke("SmartReplant planning compatibility failed safely: " + error.GetType().Name + ": " + error.Message);
                }
                return Installed;
            }
        }

        internal static void CancelActiveFieldJob(Action<string> debugLog)
        {
            try
            {
                Type fieldType = AccessTools.TypeByName(FieldPlantingServiceTypeName);
                CacheCancellationReflection(fieldType);
                if (_pluginInstanceField == null || _pluginFieldsField == null || _fieldCancelMethod == null)
                    return;

                object plugin = _pluginInstanceField.GetValue(null);
                if (plugin == null) return;

                object fields = _pluginFieldsField.GetValue(plugin);
                if (fields == null) return;

                bool active = false;
                if (_fieldActiveProperty != null)
                {
                    object value = _fieldActiveProperty.GetValue(fields, null);
                    if (value is bool boolValue) active = boolValue;
                }

                if (!active) return;
                _fieldCancelMethod.Invoke(fields, new object[] { "Workers planting planning enabled" });
                debugLog?.Invoke("SmartReplant planning lock: cancelled an already-running SmartReplant field placement job.");
            }
            catch (Exception error)
            {
                debugLog?.Invoke("SmartReplant field-job cancellation failed safely: " + error.GetType().Name + ": " + error.Message);
            }
        }

        private static void CacheCancellationReflection(Type fieldType)
        {
            if (_pluginInstanceField != null && _pluginFieldsField != null && _fieldCancelMethod != null)
                return;

            Type pluginType = AccessTools.TypeByName(SmartReplantPluginTypeName);
            if (pluginType == null || fieldType == null) return;

            _pluginInstanceField = _pluginInstanceField ?? AccessTools.Field(pluginType, "Instance");
            _pluginFieldsField = _pluginFieldsField ?? AccessTools.Field(pluginType, "Fields");
            _fieldActiveProperty = _fieldActiveProperty ?? AccessTools.Property(fieldType, "Active");
            _fieldCancelMethod = _fieldCancelMethod ?? AccessTools.Method(fieldType, "Cancel", new[] { typeof(string) });
        }

        // SmartReplant 1.7.x has changed this manual placement entry point across builds
        // (Place / TryPlace / TryPlacePiece and slightly different signatures). Patch every
        // Player+Piece overload and bind through __args so the planner does not depend on one
        // private method signature. Returning false suppresses the real SmartReplant placement;
        // bool-returning methods then naturally return false, while void methods are simply skipped.
        private static bool ManualPlacementEntryPrefix(object[] __args, MethodBase __originalMethod)
        {
            try
            {
                Player player = null;
                Piece piece = null;
                if (__args != null)
                {
                    for (int i = 0; i < __args.Length; i++)
                    {
                        if (player == null && __args[i] is Player p) player = p;
                        if (piece == null && __args[i] is Piece pc) piece = pc;
                    }
                }

                GardenPlanningRuntime runtime = Plugin.Instance?.GardenPlanning;
                if (runtime == null || player == null || piece == null || !runtime.TryInterceptPlacement(player, piece))
                    return true;

                Plugin.Log?.LogInfo($"Garden planning consumed SmartReplant manual placement via {__originalMethod?.DeclaringType?.Name}.{__originalMethod?.Name}.");
                return false;
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning("SmartReplant manual placement interception failed safely: " + error.GetType().Name + ": " + error.Message);
                return true;
            }
        }

        // Secondary fail-safe for SmartReplant's asynchronous/multi-cell field pipeline.
        // Only intercept real placement requests (place=true); preview-only calls remain untouched.
        private static bool FieldPlanPrefix(Player __0, Piece __1, Vector3 __2, Quaternion __3, bool __4, ref bool __result)
        {
            try
            {
                if (!__4) return true;

                GardenPlanningRuntime runtime = Plugin.Instance?.GardenPlanning;
                if (runtime == null || !runtime.TryInterceptExternalFieldPlan(__0, __1, __2, __3))
                    return true;

                __result = false;
                return false;
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning("SmartReplant FieldPlantingService.Plan interception failed safely: " + error.GetType().Name + ": " + error.Message);
                return true;
            }
        }

        // Prevent SmartReplant from modifying its own field extents while Workers planning owns
        // Right-Control + arrow input. Its preview may still refresh, which keeps UpdatePlacement's
        // custom flora ghost valid; only the competing controls are suppressed.
        private static bool FieldControlsPrefix()
        {
            try
            {
                return !(Plugin.Instance?.GardenPlanning?.Active ?? false);
            }
            catch
            {
                return true;
            }
        }
    }
}
