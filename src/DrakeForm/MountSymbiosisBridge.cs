using System;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers_DrakeForm
{
    /// <summary>
    /// Thin adapter into the already proven Valheim_Workers_Mount symbiosis runtime.
    /// DrakeForm owns only necklace/capture activation. Flight, transformation, camera,
    /// wingbeat and movement remain 100% owned by DrakeMountRuntime in the Mount add-on.
    /// </summary>
    internal static class MountSymbiosisBridge
    {
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const string RuntimeTypeName = "Valheim_Workers.DrakeMountRuntime, Valheim_Workers_Mount";

        private static Type _runtimeType;
        private static MethodInfo _tryMountRemoteAvatar;
        private static MethodInfo _tryMountBoundAvatar;
        private static MethodInfo _tryMountBoundForm;
        private static MethodInfo _tryToggleOff;
        private static PropertyInfo _activeProperty;
        private static PropertyInfo _activeFormProperty;
        private static PropertyInfo _egoActiveProperty;
        private static MethodInfo _trySetExternalEgoCameraMode;
        private static PropertyInfo _apiRevisionProperty;
        private static bool _resolved;


        internal static int ApiRevision
        {
            get
            {
                EnsureResolved();
                try { return _apiRevisionProperty != null ? (int)_apiRevisionProperty.GetValue(null, null) : 0; }
                catch { return 0; }
            }
        }

        internal static bool Active
        {
            get
            {
                EnsureResolved();
                try { return _activeProperty != null && (bool)_activeProperty.GetValue(null, null); }
                catch { return false; }
            }
        }

        internal static string ActiveForm
        {
            get
            {
                EnsureResolved();
                try { return _activeFormProperty != null ? (_activeFormProperty.GetValue(null, null) as string ?? string.Empty) : string.Empty; }
                catch { return string.Empty; }
            }
        }

        internal static bool EgoActive
        {
            get
            {
                EnsureResolved();
                try { return _egoActiveProperty != null && (bool)_egoActiveProperty.GetValue(null, null); }
                catch { return false; }
            }
        }

        internal static bool TrySetEgoMode(bool ego)
        {
            EnsureResolved();
            if (_trySetExternalEgoCameraMode == null) return false;
            try { return (bool)_trySetExternalEgoCameraMode.Invoke(null, new object[] { ego }); }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[Symbiosis] Camera handoff failed safely: " + ex.Message);
                return false;
            }
        }

        internal static bool TryEnter(Player player, ItemDrop.ItemData boundNecklace, out string message)
        {
            message = string.Empty;
            EnsureResolved();
            if (_tryMountBoundForm == null && _tryMountBoundAvatar == null && _tryMountRemoteAvatar == null)
            {
                message = "Mount-Symbiose ist nicht verfuegbar.";
                return false;
            }

            string formId = SymbiosisFormCatalog.Drake;
            string prefabName = "Hatchling";
            int level = 1;
            try
            {
                if (boundNecklace?.m_customData != null)
                {
                    if (boundNecklace.m_customData.TryGetValue(DrakeMedallionItemRuntime.FormKey, out string savedForm) && SymbiosisFormCatalog.IsKnown(savedForm))
                        formId = savedForm;
                    if (boundNecklace.m_customData.TryGetValue(DrakeMedallionItemRuntime.BoundPrefabKey, out string savedPrefab) && !string.IsNullOrWhiteSpace(savedPrefab))
                        prefabName = savedPrefab.Trim();
                    if (boundNecklace.m_customData.TryGetValue(DrakeMedallionItemRuntime.BoundLevelKey, out string savedLevel) && int.TryParse(savedLevel, out int parsedLevel))
                        level = Math.Max(1, parsedLevel);
                }
                if (string.Equals(formId, SymbiosisFormCatalog.Serpent, StringComparison.OrdinalIgnoreCase) && string.Equals(prefabName, "Hatchling", StringComparison.OrdinalIgnoreCase))
                    prefabName = "Serpent";
                if (string.Equals(formId, SymbiosisFormCatalog.Troll, StringComparison.OrdinalIgnoreCase) && string.Equals(prefabName, "Hatchling", StringComparison.OrdinalIgnoreCase))
                    prefabName = "Troll";
                if (string.Equals(formId, SymbiosisFormCatalog.Werewolf, StringComparison.OrdinalIgnoreCase) && string.Equals(prefabName, "Hatchling", StringComparison.OrdinalIgnoreCase))
                    prefabName = "Fenring";
            }
            catch { }

            try
            {
                if (_tryMountBoundForm != null)
                {
                    object[] args = { player, formId, prefabName, level, string.Empty };
                    _tryMountBoundForm.Invoke(null, args);
                    message = args[4] as string ?? string.Empty;
                    Plugin.Log?.LogInfo($"[Symbiosis] Bound necklace profile handed to Mount: form={formId}, prefab={prefabName}, level={level}.");
                }
                else if (_tryMountBoundAvatar != null && string.Equals(formId, SymbiosisFormCatalog.Drake, StringComparison.OrdinalIgnoreCase))
                {
                    object[] args = { player, prefabName, level, string.Empty };
                    _tryMountBoundAvatar.Invoke(null, args);
                    message = args[3] as string ?? string.Empty;
                    Plugin.Log?.LogInfo($"[DrakeForm] Bound Drake profile handed to legacy Mount: prefab={prefabName}, level={level}.");
                }
                else if (_tryMountRemoteAvatar != null && string.Equals(formId, SymbiosisFormCatalog.Drake, StringComparison.OrdinalIgnoreCase))
                {
                    object[] args = { player, string.Empty };
                    _tryMountRemoteAvatar.Invoke(null, args);
                    message = args[1] as string ?? string.Empty;
                    Plugin.Log?.LogWarning("[DrakeForm] Mount is older than v0.1.54; bound Drake prefab/level profile cannot be restored yet.");
                }
                else
                {
                    message = "Diese Symbioseform benoetigt Valheim_Workers_Mount v0.5.0 oder neuer.";
                    return false;
                }
                return Active;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                message = "Mount-Symbiose konnte nicht gestartet werden: " + inner.Message;
                Plugin.Log?.LogWarning("[DrakeForm] Mount bridge enter failed: " + inner);
                return false;
            }
            catch (Exception ex)
            {
                message = "Mount-Symbiose konnte nicht gestartet werden: " + ex.Message;
                Plugin.Log?.LogWarning("[DrakeForm] Mount bridge enter failed: " + ex);
                return false;
            }
        }

        internal static bool TryExit(Player player, out string message)
        {
            message = string.Empty;
            EnsureResolved();
            if (_tryToggleOff == null || !Active) return false;

            try
            {
                object[] args = { player, string.Empty };
                bool handled = (bool)_tryToggleOff.Invoke(null, args);
                message = args[1] as string ?? string.Empty;
                return handled;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                message = "Mount-Symbiose konnte nicht beendet werden: " + inner.Message;
                Plugin.Log?.LogWarning("[DrakeForm] Mount bridge exit failed: " + inner);
                return false;
            }
            catch (Exception ex)
            {
                message = "Mount-Symbiose konnte nicht beendet werden: " + ex.Message;
                Plugin.Log?.LogWarning("[DrakeForm] Mount bridge exit failed: " + ex);
                return false;
            }
        }

        private static void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            _runtimeType = Type.GetType(RuntimeTypeName, false);
            if (_runtimeType == null)
            {
                Plugin.Log?.LogError("[DrakeForm] Valheim_Workers_Mount DrakeMountRuntime was not found.");
                return;
            }

            _tryMountRemoteAvatar = _runtimeType.GetMethod("TryMountRemoteAvatar", StaticFlags, null, new[] { typeof(Player), typeof(string).MakeByRefType() }, null);
            _tryMountBoundAvatar = _runtimeType.GetMethod("TryMountBoundAvatar", StaticFlags, null, new[] { typeof(Player), typeof(string), typeof(int), typeof(string).MakeByRefType() }, null);
            _tryMountBoundForm = _runtimeType.GetMethod("TryMountBoundForm", StaticFlags, null, new[] { typeof(Player), typeof(string), typeof(string), typeof(int), typeof(string).MakeByRefType() }, null);
            _tryToggleOff = _runtimeType.GetMethod("TryToggleOff", StaticFlags, null, new[] { typeof(Player), typeof(string).MakeByRefType() }, null);
            _activeProperty = _runtimeType.GetProperty("Active", StaticFlags);
            _activeFormProperty = _runtimeType.GetProperty("ActiveFormId", StaticFlags);
            _egoActiveProperty = _runtimeType.GetProperty("IsEgoCameraActive", StaticFlags);
            _trySetExternalEgoCameraMode = _runtimeType.GetMethod("TrySetExternalEgoCameraMode", StaticFlags, null, new[] { typeof(bool) }, null);
            _apiRevisionProperty = _runtimeType.GetProperty("ApiRevision", StaticFlags);

            if ((_tryMountBoundForm == null && _tryMountBoundAvatar == null && _tryMountRemoteAvatar == null) || _tryToggleOff == null || _activeProperty == null)
                Plugin.Log?.LogError("[DrakeForm] Mount runtime API could not be resolved. Expected v0.1.37+ compatible DrakeMountRuntime.");
            else if (_tryMountBoundForm != null)
            {
                int revision = ApiRevision;
                Plugin.Log?.LogInfo($"[Symbiosis] Multi-form necklace activation connected to Mount (API revision {revision}).");
                if (revision > 0 && revision < 5)
                    Plugin.Log?.LogWarning("[Symbiosis] Mount API revision is older than the v0.6 Werewolf contract; update the bundled Mount add-on.");
            }
            else if (_tryMountBoundAvatar != null)
            {
                int revision = ApiRevision;
                Plugin.Log?.LogInfo($"[DrakeForm] Necklace activation connected to Mount with Drake profile handoff (API revision {revision}).");
            }
            else
                Plugin.Log?.LogInfo("[DrakeForm] Necklace activation connected to legacy Mount symbiosis/flight runtime.");
        }
    }
}
