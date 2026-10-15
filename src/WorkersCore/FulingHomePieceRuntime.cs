using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    // Shared rest place for the whole Fuling worker family. Keep the first version deliberately
    // simple and reliable: one persistent home anchor per piece, with species-specific visuals.
    internal sealed class FulingHomePieceRuntime
    {
        internal const string PrefabName = "ValheimWorkers_FulingHome";
        internal const string BasePrefabName = "bed";

        private readonly Action<string> _debugLog;
        private readonly Action<string> _infoLog;
        private float _nextAttempt;
        private float _attemptDeadline;
        private bool _registered;
        private bool _gaveUp;
        private string _status = "waiting for Jotunn PieceManager";

        internal string Status => _registered ? "registered" : _status;

        internal FulingHomePieceRuntime(Action<string> debugLog, Action<string> infoLog)
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
                if (jotunn == null) { _status = "Jotunn not loaded"; return; }

                Type pieceConfigType = jotunn.GetType("Jotunn.Configs.PieceConfig", false);
                Type customPieceType = jotunn.GetType("Jotunn.Entities.CustomPiece", false);
                Type pieceManagerType = jotunn.GetType("Jotunn.Managers.PieceManager", false);
                if (pieceConfigType == null || customPieceType == null || pieceManagerType == null)
                {
                    _status = "Jotunn custom-piece API types not found";
                    return;
                }

                object manager = pieceManagerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                if (manager == null) { _status = "Jotunn PieceManager.Instance not ready"; return; }

                MethodInfo getPiece = pieceManagerType.GetMethod("GetPiece", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                if (getPiece != null && getPiece.Invoke(manager, new object[] { PrefabName }) != null)
                {
                    _registered = true;
                    _status = "already registered via Jotunn";
                    return;
                }

                object config = Activator.CreateInstance(pieceConfigType);
                SetProperty(config, "Name", "Fuling-Bett");
                SetProperty(config, "Description", "Das originale Fuling-Bett aus den Ebenen fuer Fuling, Fuling-Schamane und Fuling-Berserker.");
                SetProperty(config, "PieceTable", "Hammer");
                SetProperty(config, "Category", "Furniture");
                SetProperty(config, "CraftingStation", "piece_workbench");
                SetProperty(config, "AllowedInDungeons", false);
                SetProperty(config, "Enabled", true);

                MethodInfo addRequirement = pieceConfigType.GetMethod("AddRequirement", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(int), typeof(bool) }, null);
                if (addRequirement == null) { _status = "Jotunn PieceConfig.AddRequirement(string,int,bool) not found"; return; }
                addRequirement.Invoke(config, new object[] { "Wood", 4, true });

                ConstructorInfo ctor = customPieceType.GetConstructor(new[] { typeof(string), typeof(string), pieceConfigType });
                if (ctor == null) { _status = "Jotunn CustomPiece(name,baseName,PieceConfig) constructor not found"; return; }

                object customPiece = ctor.Invoke(new object[] { PrefabName, BasePrefabName, config });
                GameObject prefab = customPieceType.GetProperty("PiecePrefab", BindingFlags.Instance | BindingFlags.Public)?.GetValue(customPiece) as GameObject;
                if (prefab == null) { _status = "Jotunn created no PiecePrefab"; return; }

                StripFurnitureInteraction(prefab);
                ApplyFulingVisual(prefab, jotunn);
                FulingHomeInteractable interactable = prefab.GetComponent<FulingHomeInteractable>();
                if (interactable == null) interactable = prefab.AddComponent<FulingHomeInteractable>();
                interactable.ConfigureDisplayName("Fuling-Bett");
                WorkerRestStationFactory.Attach(
                    prefab,
                    RestStationType.Fuling,
                    new Vector3(0f, 0f, -1.85f),
                    new Vector3(0f, 0.45f, 0.05f),
                    new Vector3(1.45f, 0f, -1.55f),
                    new Vector3(0f, 180f, 0f));
                WorkerRestStationFactory.EnsurePlacementCollider(prefab, new Vector3(0f, 0.35f, 0f), new Vector3(2.05f, 0.75f, 2.65f));

                MethodInfo addPiece = pieceManagerType.GetMethod("AddPiece", BindingFlags.Instance | BindingFlags.Public, null, new[] { customPieceType }, null);
                if (addPiece == null) { _status = "Jotunn PieceManager.AddPiece(CustomPiece) not found"; return; }
                object result = addPiece.Invoke(manager, new[] { customPiece });
                if (result is bool ok && !ok)
                {
                    if (getPiece != null && getPiece.Invoke(manager, new object[] { PrefabName }) != null)
                    {
                        _registered = true;
                        _status = "already registered via Jotunn";
                        return;
                    }
                    _status = "Jotunn rejected custom piece (duplicate or invalid)";
                    return;
                }

                _registered = true;
                _status = "registered via optional Jotunn reflection";
                _infoLog?.Invoke("[RestStation] Registered FulingHome");
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                _status = $"Fuling home-piece registration pending: {inner.GetType().Name}: {inner.Message}";
                _debugLog?.Invoke(_status);
            }
            catch (Exception ex)
            {
                _status = $"Fuling home-piece registration pending: {ex.GetType().Name}: {ex.Message}";
                _debugLog?.Invoke(_status);
            }
        }

        private void ApplyFulingVisual(GameObject prefab, Assembly jotunn)
        {
            try
            {
                // The Plains villages already contain the exact bed the Fulings use. Reuse only
                // its renderer hierarchy as a static visual while the hidden vanilla player bed
                // remains the reliable build-piece/network base. This deliberately avoids cloning
                // goblin_bed gameplay components (ZNetView/WearNTear/collider).
                GameObject goblinBed = ResolvePrefab(jotunn, "goblin_bed");
                if (goblinBed == null)
                {
                    _debugLog?.Invoke("[RestStation] Fuling-Bett visual source goblin_bed missing; keeping vanilla bed fail-safe.");
                    return;
                }

                Renderer[] original = prefab.GetComponentsInChildren<Renderer>(true);
                GameObject root = StaticVisualCloneRuntime.Create(goblinBed, prefab.transform, "Workers_FulingBedVisual_v2");
                if (root == null || root.GetComponentsInChildren<Renderer>(true).Length == 0)
                {
                    if (root != null) UnityEngine.Object.DestroyImmediate(root);
                    _debugLog?.Invoke("[RestStation] Fuling-Bett goblin_bed clone had no renderers; keeping vanilla bed fail-safe.");
                    return;
                }

                root.transform.localPosition = Vector3.zero;
                root.transform.localRotation = Quaternion.identity;
                root.transform.localScale = Vector3.one;
                root.SetActive(true);

                Renderer[] copied = root.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < copied.Length; i++)
                {
                    if (copied[i] == null) continue;
                    copied[i].gameObject.SetActive(true);
                    copied[i].enabled = true;
                }

                // Only after a valid village-bed visual exists do we hide the cloned player-bed
                // renderer. Its Piece/ZNetView infrastructure remains untouched.
                for (int i = 0; i < original.Length; i++)
                    if (original[i] != null) original[i].enabled = false;

                _infoLog?.Invoke("[RestStation] Fuling-Bett uses authentic vanilla goblin_bed visual.");
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Fuling-Bett visual fallback to vanilla base: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void StripFurnitureInteraction(GameObject prefab)
        {
            Component[] components = prefab.GetComponentsInChildren<Component>(true);
            for (int i = components.Length - 1; i >= 0; i--)
            {
                Component component = components[i];
                if (component == null) continue;
                string typeName = component.GetType().Name;
                if (string.Equals(typeName, "Bed", StringComparison.Ordinal) || string.Equals(typeName, "Chair", StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(component);
            }
        }

        private static GameObject ResolveFirst(Assembly jotunn, params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                GameObject prefab = ResolvePrefab(jotunn, names[i]);
                if (prefab != null) return prefab;
            }
            return null;
        }

        private static GameObject ResolvePrefab(Assembly jotunn, string prefabName)
        {
            try
            {
                Type type = jotunn.GetType("Jotunn.Managers.PrefabManager", false);
                object manager = type?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                MethodInfo method = type?.GetMethod("GetPrefab", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                return method?.Invoke(manager, new object[] { prefabName }) as GameObject;
            }
            catch { return null; }
        }

        private static void SetProperty(object target, string name, object value)
        {
            PropertyInfo property = target?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.CanWrite) property.SetValue(target, value);
        }
    }
}
