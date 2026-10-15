using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Small shoreline cage/trap used to recruit a Neck without directly feeding a hostile animal.
    /// The functional piece is a wood-floor base; the cage rails are renderer-only copies of the
    /// vanilla wood pole and simple BoxColliders keep the captured Neck inside until release.
    /// </summary>
    internal sealed class NeckTrapPieceRuntime
    {
        internal const string PrefabName = "ValheimWorkers_NeckTrap";
        internal const string BasePrefabName = "wood_floor";

        private readonly Action<string> _debugLog;
        private readonly Action<string> _infoLog;
        private float _nextAttempt;
        private float _attemptDeadline;
        private bool _registered;
        private bool _gaveUp;
        private string _status = "waiting for Jotunn PieceManager";

        internal string Status => _registered ? "registered" : _status;

        internal NeckTrapPieceRuntime(Action<string> debugLog, Action<string> infoLog)
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
                _status = "Jotunn reflection registration unavailable; Neck trap disabled";
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
                SetProperty(config, "Name", "Nixenfalle");
                SetProperty(config, "Description", "Kleine Uferfalle fuer eine Nixe. Lege einen Fisch als Koeder hinein, warte auf einen Fang und gewinne die gefangene Nixe mit weiterem rohen Fisch fuer dich.");
                SetProperty(config, "PieceTable", "Hammer");
                SetProperty(config, "Category", "Furniture");
                SetProperty(config, "CraftingStation", "piece_workbench");
                SetProperty(config, "AllowedInDungeons", false);
                SetProperty(config, "Enabled", true);

                MethodInfo addReq = pieceConfigType.GetMethod("AddRequirement", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(int), typeof(bool) }, null);
                if (addReq == null) { _status = "AddRequirement unavailable"; return; }
                addReq.Invoke(config, new object[] { "Wood", 8, true });
                addReq.Invoke(config, new object[] { "LeatherScraps", 2, true });

                ConstructorInfo ctor = customPieceType.GetConstructor(new[] { typeof(string), typeof(string), pieceConfigType });
                if (ctor == null) { _status = "CustomPiece ctor unavailable"; return; }
                object customPiece = ctor.Invoke(new object[] { PrefabName, BasePrefabName, config });
                GameObject prefab = customPieceType.GetProperty("PiecePrefab", BindingFlags.Instance | BindingFlags.Public)?.GetValue(customPiece) as GameObject;
                if (prefab == null) { _status = "no PiecePrefab"; return; }

                AddCageVisuals(prefab);
                AddCageColliders(prefab);
                if (prefab.GetComponent<NeckTrapInteractable>() == null) prefab.AddComponent<NeckTrapInteractable>();
                if (prefab.GetComponent<NeckTrapRecruitmentRuntime>() == null) prefab.AddComponent<NeckTrapRecruitmentRuntime>();

                MethodInfo addPiece = pieceManagerType.GetMethod("AddPiece", BindingFlags.Instance | BindingFlags.Public, null, new[] { customPieceType }, null);
                if (addPiece == null) { _status = "AddPiece unavailable"; return; }
                addPiece.Invoke(manager, new[] { customPiece });

                _registered = true;
                _status = "registered";
                _infoLog?.Invoke("[Recruitment] Registered Nixenfalle");
            }
            catch (Exception ex)
            {
                Exception root = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                _status = $"Neck trap registration pending: {root.GetType().Name}: {root.Message}";
                _debugLog?.Invoke(_status);
            }
        }

        private static void AddCageVisuals(GameObject prefab)
        {
            if (prefab == null || ZNetScene.instance == null) return;
            GameObject pole = null;
            try { pole = ZNetScene.instance.GetPrefab("wood_pole"); } catch { }
            if (pole == null) return;

            Vector3[] corners =
            {
                new Vector3(-0.78f, 0.15f, -0.78f), new Vector3(0.78f, 0.15f, -0.78f),
                new Vector3(-0.78f, 0.15f, 0.78f),  new Vector3(0.78f, 0.15f, 0.78f)
            };
            for (int i = 0; i < corners.Length; i++)
            {
                GameObject bar = StaticVisualCloneRuntime.Create(pole, prefab.transform, "Workers_NeckTrap_Vertical_" + i);
                if (bar == null) continue;
                bar.transform.localPosition = corners[i];
                bar.transform.localScale = new Vector3(0.65f, 0.72f, 0.65f);
            }

            AddHorizontal(pole, prefab.transform, "FrontLow", new Vector3(0f, 0.45f, -0.78f), new Vector3(0.65f, 0.72f, 0.65f), Quaternion.Euler(0f, 0f, 90f));
            AddHorizontal(pole, prefab.transform, "FrontHigh", new Vector3(0f, 1.05f, -0.78f), new Vector3(0.65f, 0.72f, 0.65f), Quaternion.Euler(0f, 0f, 90f));
            AddHorizontal(pole, prefab.transform, "BackLow", new Vector3(0f, 0.45f, 0.78f), new Vector3(0.65f, 0.72f, 0.65f), Quaternion.Euler(0f, 0f, 90f));
            AddHorizontal(pole, prefab.transform, "BackHigh", new Vector3(0f, 1.05f, 0.78f), new Vector3(0.65f, 0.72f, 0.65f), Quaternion.Euler(0f, 0f, 90f));
            AddHorizontal(pole, prefab.transform, "LeftLow", new Vector3(-0.78f, 0.45f, 0f), new Vector3(0.65f, 0.72f, 0.65f), Quaternion.Euler(90f, 0f, 0f));
            AddHorizontal(pole, prefab.transform, "LeftHigh", new Vector3(-0.78f, 1.05f, 0f), new Vector3(0.65f, 0.72f, 0.65f), Quaternion.Euler(90f, 0f, 0f));
            AddHorizontal(pole, prefab.transform, "RightLow", new Vector3(0.78f, 0.45f, 0f), new Vector3(0.65f, 0.72f, 0.65f), Quaternion.Euler(90f, 0f, 0f));
            AddHorizontal(pole, prefab.transform, "RightHigh", new Vector3(0.78f, 1.05f, 0f), new Vector3(0.65f, 0.72f, 0.65f), Quaternion.Euler(90f, 0f, 0f));
        }

        private static void AddHorizontal(GameObject source, Transform parent, string name, Vector3 pos, Vector3 scale, Quaternion rotation)
        {
            GameObject bar = StaticVisualCloneRuntime.Create(source, parent, "Workers_NeckTrap_" + name);
            if (bar == null) return;
            bar.transform.localPosition = pos;
            bar.transform.localRotation = rotation;
            bar.transform.localScale = scale;
        }

        private static void AddCageColliders(GameObject prefab)
        {
            AddWall(prefab.transform, "Workers_NeckTrapCollider_Front", new Vector3(0f, 0.75f, -0.82f), new Vector3(1.75f, 1.5f, 0.14f));
            AddWall(prefab.transform, "Workers_NeckTrapCollider_Back", new Vector3(0f, 0.75f, 0.82f), new Vector3(1.75f, 1.5f, 0.14f));
            AddWall(prefab.transform, "Workers_NeckTrapCollider_Left", new Vector3(-0.82f, 0.75f, 0f), new Vector3(0.14f, 1.5f, 1.75f));
            AddWall(prefab.transform, "Workers_NeckTrapCollider_Right", new Vector3(0.82f, 0.75f, 0f), new Vector3(0.14f, 1.5f, 1.75f));
        }

        private static void AddWall(Transform parent, string name, Vector3 localPosition, Vector3 size)
        {
            GameObject wall = new GameObject(name);
            wall.transform.SetParent(parent, false);
            wall.transform.localPosition = localPosition;
            BoxCollider collider = wall.AddComponent<BoxCollider>();
            collider.size = size;
        }

        private static void SetProperty(object target, string name, object value)
        {
            PropertyInfo p = target?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (p != null && p.CanWrite) p.SetValue(target, value);
        }
    }
}
