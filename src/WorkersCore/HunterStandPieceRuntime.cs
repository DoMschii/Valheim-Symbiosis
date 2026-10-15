using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    internal sealed class HunterStandPieceRuntime
    {
        // Keep the prefab id stable so already placed hunter homes continue to resolve after the visual change.
        internal const string PrefabName = "ValheimWorkers_HunterStand";
        internal const string BasePrefabName = "wood_floor";
        private readonly Action<string> _debugLog;
        private readonly Action<string> _infoLog;
        private float _nextAttempt;
        private float _attemptDeadline;
        private bool _registered;
        private bool _gaveUp;
        private string _status = "waiting for Jotunn PieceManager";
        internal string Status => _registered ? "registered" : _status;

        internal HunterStandPieceRuntime(Action<string> debugLog, Action<string> infoLog)
        {
            _debugLog = debugLog;
            _infoLog = infoLog;
        }

        internal void Begin()
        {
            // Custom networked pieces must exist in Jotunn before ZNetScene.Awake loads world ZDOs.
            // Register exactly when vanilla prefabs become cloneable; late Update retries are only a fallback.
            _attemptDeadline = float.PositiveInfinity;
            if (JotunnPrefabLifecycleRuntime.Subscribe(TryRegister, _debugLog))
            {
                if (!_registered) _status = "waiting for Jotunn vanilla prefabs";
                return;
            }

            _attemptDeadline = Time.unscaledTime + 15f;
            TryRegister();
        }

        internal void Tick()
        {
            if (_registered || _gaveUp) return;
            if (JotunnPrefabLifecycleRuntime.WaitingForVanillaPrefabs) return;
            if (float.IsPositiveInfinity(_attemptDeadline))
                _attemptDeadline = Time.unscaledTime + 15f;
            if (Time.unscaledTime > _attemptDeadline) { _gaveUp = true; _status = "Jotunn registration unavailable"; return; }
            if (Time.unscaledTime < _nextAttempt) return;
            _nextAttempt = Time.unscaledTime + 1.5f;
            TryRegister();
        }

        private void TryRegister()
        {
            try
            {
                Assembly jotunn = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, "Jotunn", StringComparison.OrdinalIgnoreCase));
                if (jotunn == null) { _status = "Jotunn not loaded"; return; }
                Type pieceConfigType = jotunn.GetType("Jotunn.Configs.PieceConfig", false);
                Type customPieceType = jotunn.GetType("Jotunn.Entities.CustomPiece", false);
                Type pieceManagerType = jotunn.GetType("Jotunn.Managers.PieceManager", false);
                if (pieceConfigType == null || customPieceType == null || pieceManagerType == null) { _status = "Jotunn custom-piece API types missing"; return; }
                object manager = pieceManagerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                if (manager == null) { _status = "PieceManager not ready"; return; }
                MethodInfo getPiece = pieceManagerType.GetMethod("GetPiece", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                if (getPiece != null && getPiece.Invoke(manager, new object[] { PrefabName }) != null) { _registered = true; return; }

                object config = Activator.CreateInstance(pieceConfigType);
                SetProperty(config, "Name", "Draugr-Jagdplattform");
                SetProperty(config, "Description", "Funktionsplatte fuer einen Draugr-Jaeger. Baue den Hochsitz selbst und setze diese Plattform mindestens 2 m ueber den Boden. Sie ist zugleich Zuhause und Jagdstation.");
                SetProperty(config, "PieceTable", "Hammer");
                SetProperty(config, "Category", "Furniture");
                SetProperty(config, "CraftingStation", "piece_workbench");
                SetProperty(config, "AllowedInDungeons", false);
                SetProperty(config, "Enabled", true);
                MethodInfo addReq = pieceConfigType.GetMethod("AddRequirement", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(int), typeof(bool) }, null);
                if (addReq == null) { _status = "AddRequirement unavailable"; return; }
                addReq.Invoke(config, new object[] { "Wood", 1, true });

                ConstructorInfo ctor = customPieceType.GetConstructor(new[] { typeof(string), typeof(string), pieceConfigType });
                if (ctor == null) { _status = "CustomPiece ctor unavailable"; return; }
                object customPiece = ctor.Invoke(new object[] { PrefabName, BasePrefabName, config });
                GameObject prefab = customPieceType.GetProperty("PiecePrefab", BindingFlags.Instance | BindingFlags.Public)?.GetValue(customPiece) as GameObject;
                if (prefab == null) { _status = "no PiecePrefab"; return; }

                // Deliberately keep the piece as a plain vanilla wood floor. The player builds the
                // supports, stairs/ramp, roof and railings. Workers only supplies the functional plate.
                HunterStandMarker marker = prefab.GetComponent<HunterStandMarker>();
                if (marker == null) marker = prefab.AddComponent<HunterStandMarker>();
                Transform stand = prefab.transform.Find(HunterStandMarker.StandPointName);
                if (stand == null)
                {
                    GameObject point = new GameObject(HunterStandMarker.StandPointName);
                    stand = point.transform;
                    stand.SetParent(prefab.transform, false);
                }
                stand.localPosition = new Vector3(0f, 0.18f, 0f);

                if (prefab.GetComponent<HunterStandHomeInteractable>() == null) prefab.AddComponent<HunterStandHomeInteractable>();
                WorkerRestStationFactory.Attach(
                    prefab,
                    RestStationType.Draugr,
                    new Vector3(0f, 0.18f, 0f),
                    Vector3.zero,
                    new Vector3(0f, 0.18f, 0f),
                    Vector3.zero,
                    stand);

                MethodInfo addPiece = pieceManagerType.GetMethod("AddPiece", BindingFlags.Instance | BindingFlags.Public, null, new[] { customPieceType }, null);
                if (addPiece == null) { _status = "AddPiece unavailable"; return; }
                addPiece.Invoke(manager, new[] { customPiece });
                _registered = true;
                _status = "registered";
                _infoLog?.Invoke("[Hunter] Registered Draugr-Jagdplattform (player-built elevated stand)");
            }
            catch (Exception ex)
            {
                Exception root = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                _status = $"Hunter platform registration pending: {root.GetType().Name}: {root.Message}";
                _debugLog?.Invoke(_status);
            }
        }

        private static void SetProperty(object target, string name, object value)
        {
            PropertyInfo p = target?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (p != null && p.CanWrite) p.SetValue(target, value);
        }
    }
}
