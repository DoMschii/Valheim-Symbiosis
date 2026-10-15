using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Coordinates registration of Workers custom networked pieces with Jotunn's prefab lifecycle.
    /// A placed custom piece can only survive a world reload when its prefab is already registered
    /// before ZNetScene.Awake resolves saved ZDO prefab hashes. Registering later from Update is too late.
    /// </summary>
    internal static class JotunnPrefabLifecycleRuntime
    {
        private static readonly List<Action> Pending = new List<Action>(16);
        private static readonly List<Action> DispatchScratch = new List<Action>(16);
        private static readonly object[] WorkbenchLookupArgs = new object[] { "piece_workbench" };
        private static bool _hookAttempted;
        private static bool _hooked;
        private static bool _vanillaPrefabsReady;
        private static Delegate _eventDelegate;
        private static Action<string> _debugLog;

        internal static bool WaitingForVanillaPrefabs => _hooked && !_vanillaPrefabsReady;

        internal static bool Subscribe(Action registerAction, Action<string> debugLog)
        {
            if (registerAction == null) return false;
            _debugLog ??= debugLog;

            if (_vanillaPrefabsReady)
            {
                SafeInvoke(registerAction);
                return true;
            }

            if (!Pending.Contains(registerAction)) Pending.Add(registerAction);
            if (!_hookAttempted) TryInstallHook();

            // If Workers itself loaded late, the event may already have fired. Detect that from
            // Jotunn's prefab cache and dispatch immediately instead of waiting forever.
            if (_hooked && !_vanillaPrefabsReady && AreVanillaPrefabsAvailable())
                DispatchReady();

            return _hooked;
        }

        private static void TryInstallHook()
        {
            _hookAttempted = true;
            try
            {
                Assembly jotunn = FindJotunnAssembly();
                Type prefabManagerType = jotunn?.GetType("Jotunn.Managers.PrefabManager", false);
                EventInfo evt = prefabManagerType?.GetEvent("OnVanillaPrefabsAvailable", BindingFlags.Static | BindingFlags.Public);
                if (evt == null || evt.EventHandlerType == null)
                {
                    _debugLog?.Invoke("[Pieces] Jotunn OnVanillaPrefabsAvailable unavailable; using legacy registration fallback.");
                    return;
                }

                Action callback = OnVanillaPrefabsAvailable;
                _eventDelegate = Delegate.CreateDelegate(evt.EventHandlerType, callback.Target, callback.Method);
                evt.AddEventHandler(null, _eventDelegate);
                _hooked = true;
                _debugLog?.Invoke("[Pieces] Waiting for Jotunn vanilla-prefab lifecycle before registering persistent custom pieces.");
            }
            catch (Exception ex)
            {
                _hooked = false;
                _debugLog?.Invoke($"[Pieces] Jotunn prefab lifecycle hook failed; using legacy fallback: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void OnVanillaPrefabsAvailable()
        {
            DispatchReady();
        }

        private static void DispatchReady()
        {
            if (_vanillaPrefabsReady) return;
            _vanillaPrefabsReady = true;

            DispatchScratch.Clear();
            for (int i = 0; i < Pending.Count; i++)
                DispatchScratch.Add(Pending[i]);
            Pending.Clear();

            _debugLog?.Invoke($"[Pieces] Jotunn vanilla prefabs ready; registering {DispatchScratch.Count} persistent Workers pieces before ZNetScene.");
            for (int i = 0; i < DispatchScratch.Count; i++)
                SafeInvoke(DispatchScratch[i]);

            DispatchScratch.Clear();
        }

        private static bool AreVanillaPrefabsAvailable()
        {
            try
            {
                Assembly jotunn = FindJotunnAssembly();
                Type type = jotunn?.GetType("Jotunn.Managers.PrefabManager", false);
                object manager = type?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                MethodInfo getPrefab = type?.GetMethod("GetPrefab", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                if (manager == null || getPrefab == null) return false;

                // piece_workbench exists in every normal world and is enough to prove the vanilla
                // prefab cache is populated for safe CustomPiece cloning.
                return getPrefab.Invoke(manager, WorkbenchLookupArgs) is GameObject;
            }
            catch
            {
                return false;
            }
        }

        private static Assembly FindJotunnAssembly()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];
                if (assembly != null &&
                    string.Equals(assembly.GetName().Name, "Jotunn", StringComparison.OrdinalIgnoreCase))
                    return assembly;
            }

            return null;
        }

        private static void SafeInvoke(Action callback)
        {
            try { callback?.Invoke(); }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"[Pieces] Persistent-piece registration callback failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
