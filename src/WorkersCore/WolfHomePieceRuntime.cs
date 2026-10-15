using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Species-specific home for the Wolf Guard. The vanilla deer rug supplies a Valheim-native
    /// visual and collider base while Workers adds the persistent HomeAnchor/rest-station contract.
    /// </summary>
    internal sealed class WolfHomePieceRuntime
    {
        internal const string PrefabName = "ValheimWorkers_WolfDen";
        internal const string BasePrefabName = "rug_deer";

        private readonly Action<string> _debugLog;
        private readonly Action<string> _infoLog;
        private float _nextAttempt;
        private float _attemptDeadline;
        private bool _registered;
        private bool _gaveUp;
        private string _status = "waiting for Jotunn PieceManager";

        internal string Status => _registered ? "registered" : _status;

        internal WolfHomePieceRuntime(Action<string> debugLog, Action<string> infoLog)
        {
            _debugLog = debugLog;
            _infoLog = infoLog;
        }

        internal void Begin()
        {
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
            if (float.IsPositiveInfinity(_attemptDeadline)) _attemptDeadline = Time.unscaledTime + 15f;
            if (Time.unscaledTime > _attemptDeadline)
            {
                _gaveUp = true;
                _status = "Jotunn registration unavailable";
                return;
            }
            if (Time.unscaledTime < _nextAttempt) return;
            _nextAttempt = Time.unscaledTime + 1.5f;
            TryRegister();
        }

        private void TryRegister()
        {
            try
            {
                Assembly jotunn = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "Jotunn", StringComparison.OrdinalIgnoreCase));
                if (jotunn == null) { _status = "Jotunn not loaded"; return; }

                Type pieceConfigType = jotunn.GetType("Jotunn.Configs.PieceConfig", false);
                Type customPieceType = jotunn.GetType("Jotunn.Entities.CustomPiece", false);
                Type pieceManagerType = jotunn.GetType("Jotunn.Managers.PieceManager", false);
                if (pieceConfigType == null || customPieceType == null || pieceManagerType == null)
                {
                    _status = "Jotunn custom-piece API types missing";
                    return;
                }

                object manager = pieceManagerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                if (manager == null) { _status = "PieceManager not ready"; return; }

                MethodInfo getPiece = pieceManagerType.GetMethod("GetPiece", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                if (getPiece != null && getPiece.Invoke(manager, new object[] { PrefabName }) != null)
                {
                    _registered = true;
                    _status = "already registered";
                    return;
                }

                object config = Activator.CreateInstance(pieceConfigType);
                SetProperty(config, "Name", "Wolfsbau");
                SetProperty(config, "Description", "Felllager und feste Ruhestaette fuer einen rekrutierten Wolf-Waechter.");
                SetProperty(config, "PieceTable", "Hammer");
                SetProperty(config, "Category", "Furniture");
                SetProperty(config, "CraftingStation", "piece_workbench");
                SetProperty(config, "AllowedInDungeons", false);
                SetProperty(config, "Enabled", true);

                MethodInfo addReq = pieceConfigType.GetMethod("AddRequirement", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(int), typeof(bool) }, null);
                if (addReq == null) { _status = "AddRequirement unavailable"; return; }
                addReq.Invoke(config, new object[] { "DeerHide", 2, true });
                addReq.Invoke(config, new object[] { "Wood", 1, true });

                ConstructorInfo ctor = customPieceType.GetConstructor(new[] { typeof(string), typeof(string), pieceConfigType });
                if (ctor == null) { _status = "CustomPiece ctor unavailable"; return; }
                object customPiece = ctor.Invoke(new object[] { PrefabName, BasePrefabName, config });
                GameObject prefab = customPieceType.GetProperty("PiecePrefab", BindingFlags.Instance | BindingFlags.Public)?.GetValue(customPiece) as GameObject;
                if (prefab == null) { _status = "no PiecePrefab"; return; }

                if (prefab.GetComponent<WolfHomeInteractable>() == null)
                    prefab.AddComponent<WolfHomeInteractable>();

                WorkerRestStationFactory.Attach(
                    prefab,
                    RestStationType.Wolf,
                    new Vector3(0f, 0.05f, -1.55f),
                    new Vector3(0f, 0.07f, 0f),
                    new Vector3(0f, 0.05f, 1.55f),
                    Vector3.zero);

                // A low interaction/placement surface keeps the ray target reliable without
                // creating a wall that can block the Wolf's approach to the rug.
                WorkerRestStationFactory.EnsurePlacementCollider(
                    prefab,
                    new Vector3(0f, 0.06f, 0f),
                    new Vector3(1.55f, 0.12f, 1.85f));

                MethodInfo addPiece = pieceManagerType.GetMethod("AddPiece", BindingFlags.Instance | BindingFlags.Public, null, new[] { customPieceType }, null);
                if (addPiece == null) { _status = "AddPiece unavailable"; return; }
                addPiece.Invoke(manager, new[] { customPiece });

                _registered = true;
                _status = "registered";
                _infoLog?.Invoke("[RestStation] Registered Wolfsbau");
            }
            catch (Exception ex)
            {
                Exception root = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                _status = $"Wolf home registration pending: {root.GetType().Name}: {root.Message}";
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
