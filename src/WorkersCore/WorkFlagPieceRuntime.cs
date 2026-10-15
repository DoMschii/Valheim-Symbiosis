using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    internal sealed class WorkFlagPieceRuntime
    {
        internal const string PrefabName = "ValheimWorkers_WorkFlag";
        internal const string BasePrefabName = "wood_pole";

        private readonly Action<string> _debugLog;
        private readonly Action<string> _infoLog;
        private float _nextAttempt;
        private float _attemptDeadline;
        private bool _registered;
        private bool _gaveUp;
        private string _status = "waiting for Jotunn PieceManager";

        internal bool IsRegistered => _registered;
        internal string Status => _registered ? "registered" : _status;

        internal WorkFlagPieceRuntime(Action<string> debugLog, Action<string> infoLog)
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
            if (Time.unscaledTime > _attemptDeadline)
            {
                _gaveUp = true;
                _status = "Jotunn reflection registration unavailable; other Workers features still active";
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
                if (jotunn == null)
                {
                    _status = "Jotunn not loaded";
                    return;
                }

                Type pieceConfigType = jotunn.GetType("Jotunn.Configs.PieceConfig", false);
                Type customPieceType = jotunn.GetType("Jotunn.Entities.CustomPiece", false);
                Type pieceManagerType = jotunn.GetType("Jotunn.Managers.PieceManager", false);
                if (pieceConfigType == null || customPieceType == null || pieceManagerType == null)
                {
                    _status = "Jotunn custom-piece API types not found";
                    return;
                }

                object manager = pieceManagerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                if (manager == null)
                {
                    _status = "Jotunn PieceManager.Instance not ready";
                    return;
                }

                MethodInfo getPiece = pieceManagerType.GetMethod("GetPiece", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                GameObject existingPiece = getPiece?.Invoke(manager, new object[] { PrefabName }) as GameObject;
                if (existingPiece != null)
                {
                    ClearCraftingStationRequirement(existingPiece);
                    _registered = true;
                    _status = "already registered via Jotunn; no Workbench required";
                    return;
                }

                object config = Activator.CreateInstance(pieceConfigType);
                SetProperty(config, "Name", "Workers-Arbeitsfahne");
                SetProperty(config, "Description", "Markiert ein Arbeitsgebiet. Mit E den Auftrag und den Arbeitsradius waehlen; passende Arbeiter laufen in dieses Gebiet.");
                SetProperty(config, "PieceTable", "Hammer");
                SetProperty(config, "Category", "Workers");
                SetProperty(config, "CraftingStation", null);
                SetProperty(config, "AllowedInDungeons", false);
                SetProperty(config, "Enabled", true);

                MethodInfo addRequirement = pieceConfigType.GetMethod(
                    "AddRequirement",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { typeof(string), typeof(int), typeof(bool) },
                    null);
                if (addRequirement == null)
                {
                    _status = "Jotunn PieceConfig.AddRequirement(string,int,bool) not found";
                    return;
                }

                addRequirement.Invoke(config, new object[] { "Wood", 5, true });
                addRequirement.Invoke(config, new object[] { "LeatherScraps", 2, true });

                ConstructorInfo ctor = customPieceType.GetConstructor(new[] { typeof(string), typeof(string), pieceConfigType });
                if (ctor == null)
                {
                    _status = "Jotunn CustomPiece(name,baseName,PieceConfig) constructor not found";
                    return;
                }

                object customPiece = ctor.Invoke(new object[] { PrefabName, BasePrefabName, config });
                GameObject prefab = customPieceType.GetProperty("PiecePrefab", BindingFlags.Instance | BindingFlags.Public)?.GetValue(customPiece) as GameObject;
                if (prefab == null)
                {
                    _status = "Jotunn created no PiecePrefab";
                    return;
                }

                // The work flag is an expedition marker, not a base structure.
                // Jotunn may preserve the base prefab's Piece.m_craftingStation when an empty
                // PieceConfig value is ignored, so clear the actual generated Piece as well.
                // Reflection keeps this defensive across Valheim/Jotunn versions and is only
                // performed once during piece registration.
                ClearCraftingStationRequirement(prefab);

                if (prefab.GetComponent<WorkFlagInteractable>() == null)
                    prefab.AddComponent<WorkFlagInteractable>();

                ApplyMarkerFlagVisual(prefab, jotunn);

                MethodInfo addPiece = pieceManagerType.GetMethod("AddPiece", BindingFlags.Instance | BindingFlags.Public, null, new[] { customPieceType }, null);
                if (addPiece == null)
                {
                    _status = "Jotunn PieceManager.AddPiece(CustomPiece) not found";
                    return;
                }

                object result = addPiece.Invoke(manager, new[] { customPiece });
                if (result is bool ok && !ok)
                {
                    existingPiece = getPiece?.Invoke(manager, new object[] { PrefabName }) as GameObject;
                    if (existingPiece != null)
                    {
                        ClearCraftingStationRequirement(existingPiece);
                        _registered = true;
                        _status = "already registered via Jotunn; no Workbench required";
                        return;
                    }
                    _status = "Jotunn rejected custom piece (duplicate or invalid)";
                    return;
                }

                // Apply once more after PieceManager registration in case Jotunn normalized
                // PieceConfig/base-prefab fields during AddPiece.
                ClearCraftingStationRequirement(prefab);
                _registered = true;
                _status = "registered via optional Jotunn reflection; no Workbench required";
                _infoLog?.Invoke($"Workers work flag registered: {PrefabName}, base={BasePrefabName}, cost=Wood:5 LeatherScraps:2.");
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                _status = $"Jotunn work-flag registration pending: {inner.GetType().Name}: {inner.Message}";
                _debugLog?.Invoke(_status);
            }
            catch (Exception ex)
            {
                _status = $"Work-flag registration pending: {ex.GetType().Name}: {ex.Message}";
                _debugLog?.Invoke(_status);
            }
        }



        private void ClearCraftingStationRequirement(GameObject prefab)
        {
            if (prefab == null) return;

            try
            {
                Piece piece = prefab.GetComponent<Piece>();
                if (piece == null) piece = prefab.GetComponentInChildren<Piece>(true);
                if (piece == null)
                {
                    _debugLog?.Invoke("Workers-Arbeitsfahne: generated prefab has no Piece component; crafting-station override skipped.");
                    return;
                }

                Type pieceType = piece.GetType();
                FieldInfo field = pieceType.GetField("m_craftingStation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    field.SetValue(piece, null);
                    _debugLog?.Invoke("Workers-Arbeitsfahne: crafting-station requirement cleared on generated Piece.");
                    return;
                }

                PropertyInfo property = pieceType.GetProperty("CraftingStation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(piece, null);
                    _debugLog?.Invoke("Workers-Arbeitsfahne: crafting-station requirement cleared via Piece property fallback.");
                    return;
                }

                _debugLog?.Invoke("Workers-Arbeitsfahne: no crafting-station member found on Piece; PieceConfig=null remains the only override.");
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Workers-Arbeitsfahne crafting-station override failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void ApplyMarkerFlagVisual(GameObject prefab, Assembly jotunn)
        {
            if (prefab == null || jotunn == null) return;
            GameObject bannerSource = ResolvePrefab(jotunn, "piece_banner05");
            if (bannerSource == null)
            {
                _debugLog?.Invoke("Workers-Arbeitsfahne: piece_banner05 visual unavailable; keeping the ground pole as functional fallback.");
                return;
            }

            try
            {
                GameObject wrapper = new GameObject("Workers_WorkFlagCloth");
                wrapper.transform.SetParent(prefab.transform, false);
                wrapper.transform.localPosition = new Vector3(0f, 0.72f, 0f);
                wrapper.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
                wrapper.transform.localScale = new Vector3(0.72f, 0.72f, 0.72f);

                GameObject clone = UnityEngine.Object.Instantiate(bannerSource);
                clone.name = "Workers_WorkFlagCloth_Visual";
                clone.transform.SetParent(wrapper.transform, false);
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = Quaternion.identity;
                clone.transform.localScale = Vector3.one;

                Component[] components = clone.GetComponentsInChildren<Component>(true);
                for (int i = components.Length - 1; i >= 0; i--)
                {
                    Component component = components[i];
                    if (component == null || component is Transform || component is MeshFilter || component is Renderer || component is LODGroup)
                        continue;
                    UnityEngine.Object.DestroyImmediate(component);
                }
                clone.SetActive(true);
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Workers-Arbeitsfahne visual fallback: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static GameObject ResolvePrefab(Assembly jotunn, string prefabName)
        {
            if (jotunn == null || string.IsNullOrEmpty(prefabName)) return null;
            try
            {
                Type prefabManagerType = jotunn.GetType("Jotunn.Managers.PrefabManager", false);
                object manager = prefabManagerType?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                MethodInfo getPrefab = prefabManagerType?.GetMethod("GetPrefab", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                return manager != null ? getPrefab?.Invoke(manager, new object[] { prefabName }) as GameObject : null;
            }
            catch
            {
                return null;
            }
        }

        private static void SetProperty(object instance, string propertyName, object value)
        {
            if (instance == null) return;
            PropertyInfo property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanWrite) return;
            property.SetValue(instance, value);
        }
    }
}
