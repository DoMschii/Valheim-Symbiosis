using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>Registers the Draugr grave-rest as an optional Jotunn custom piece.</summary>
    internal sealed class DraugrRestPieceRuntime
    {
        internal const string PrefabName = "ValheimWorkers_DraugrRestBed";
        internal const string BasePrefabName = "bed";

        private readonly Action<string> _debugLog;
        private readonly Action<string> _infoLog;
        private float _nextAttempt;
        private float _attemptDeadline;
        private bool _registered;
        private bool _gaveUp;
        private string _status = "waiting for Jotunn PieceManager";

        internal string Status => _registered ? "registered" : _status;

        internal DraugrRestPieceRuntime(Action<string> debugLog, Action<string> infoLog)
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
                SetProperty(config, "Name", "Draugr-Ruhestätte");
                SetProperty(config, "Description", "Eine modrige Grabliege aus morschem Holz, alten Fellen und Knochen, an der ein Draugr ruhen und regenerieren kann.");
                SetProperty(config, "PieceTable", "Hammer");
                SetProperty(config, "Category", "Furniture");
                SetProperty(config, "CraftingStation", "piece_workbench");
                SetProperty(config, "AllowedInDungeons", false);
                SetProperty(config, "Enabled", true);

                MethodInfo addRequirement = pieceConfigType.GetMethod("AddRequirement", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(int), typeof(bool) }, null);
                if (addRequirement == null) { _status = "Jotunn PieceConfig.AddRequirement(string,int,bool) not found"; return; }
                addRequirement.Invoke(config, new object[] { "Wood", 1, true });

                ConstructorInfo ctor = customPieceType.GetConstructor(new[] { typeof(string), typeof(string), pieceConfigType });
                if (ctor == null) { _status = "Jotunn CustomPiece(name,baseName,PieceConfig) constructor not found"; return; }

                object customPiece = ctor.Invoke(new object[] { PrefabName, BasePrefabName, config });
                GameObject prefab = customPieceType.GetProperty("PiecePrefab", BindingFlags.Instance | BindingFlags.Public)?.GetValue(customPiece) as GameObject;
                if (prefab == null) { _status = "Jotunn created no PiecePrefab"; return; }

                StripFurnitureInteraction(prefab);
                ApplyDraugrVisual(prefab, jotunn);
                if (prefab.GetComponent<DraugrHomeInteractable>() == null) prefab.AddComponent<DraugrHomeInteractable>();
                WorkerRestStationFactory.Attach(
                    prefab,
                    RestStationType.Draugr,
                    new Vector3(0f, 0f, -1.85f),
                    new Vector3(0f, 0.48f, 0.05f),
                    new Vector3(1.35f, 0f, -1.55f),
                    new Vector3(0f, 180f, 90f));
                WorkerRestStationFactory.EnsurePlacementCollider(prefab, new Vector3(0f, 0.35f, 0f), new Vector3(1.65f, 0.7f, 2.45f));

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
                _infoLog?.Invoke("[RestStation] Registered DraugrRestBed");
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                _status = $"Draugr rest-piece registration pending: {inner.GetType().Name}: {inner.Message}";
                _debugLog?.Invoke(_status);
            }
            catch (Exception ex)
            {
                _status = $"Draugr rest-piece registration pending: {ex.GetType().Name}: {ex.Message}";
                _debugLog?.Invoke(_status);
            }
        }

        private void ApplyDraugrVisual(GameObject prefab, Assembly jotunn)
        {
            Renderer[] original = prefab.GetComponentsInChildren<Renderer>(true);
            GameObject log = ResolveFirst(jotunn, "beech_log_half", "FirTree_oldLog", "Wood");
            GameObject hide = ResolveFirst(jotunn, "DeerHide", "LeatherScraps");
            GameObject bone = ResolveFirst(jotunn, "BoneFragments");
            GameObject skull = ResolveFirst(jotunn, "Skull1", "TrophySkeleton");
            GameObject mushroom = ResolveFirst(jotunn, "MushroomBlue", "MushroomYellow");

            if (log == null)
            {
                _debugLog?.Invoke("[RestStation] DraugrRestBed visual source missing: beech_log_half/FirTree_oldLog/Wood; keeping vanilla bed fail-safe.");
                return;
            }

            GameObject root = new GameObject("Workers_DraugrRestVisual_v1");
            root.transform.SetParent(prefab.transform, false);
            CloneVisual(log, root.transform, "RottenLog_Left", new Vector3(-0.62f, 0.23f, 0f), new Vector3(0.30f, 0.30f, 0.78f), new Vector3(0f, 0f, 90f));
            CloneVisual(log, root.transform, "RottenLog_Right", new Vector3(0.62f, 0.23f, 0f), new Vector3(0.30f, 0.30f, 0.78f), new Vector3(0f, 180f, 90f));
            CloneVisual(log, root.transform, "RottenLog_Head", new Vector3(0f, 0.30f, 0.92f), new Vector3(0.24f, 0.24f, 0.48f), new Vector3(0f, 90f, 90f));
            CloneVisual(log, root.transform, "RottenLog_Foot", new Vector3(0f, 0.25f, -0.92f), new Vector3(0.22f, 0.22f, 0.48f), new Vector3(0f, 90f, 90f));
            if (hide != null)
            {
                CloneVisual(hide, root.transform, "TatteredHide_Left", new Vector3(-0.20f, 0.49f, 0.12f), new Vector3(1.5f, 1.5f, 1.5f), new Vector3(78f, 16f, 0f));
                CloneVisual(hide, root.transform, "TatteredHide_Right", new Vector3(0.22f, 0.50f, -0.15f), new Vector3(1.45f, 1.45f, 1.45f), new Vector3(78f, 194f, 0f));
            }
            if (bone != null)
            {
                CloneVisual(bone, root.transform, "Bones_Head", new Vector3(-0.54f, 0.47f, 0.72f), Vector3.one * 1.25f, new Vector3(12f, 35f, 18f));
                CloneVisual(bone, root.transform, "Bones_Foot", new Vector3(0.50f, 0.42f, -0.74f), Vector3.one, new Vector3(8f, 210f, 5f));
            }
            if (skull != null) CloneVisual(skull, root.transform, "DraugrSkull", new Vector3(0.48f, 0.55f, 0.70f), Vector3.one * 0.65f, new Vector3(8f, 205f, 0f));
            if (mushroom != null)
            {
                CloneVisual(mushroom, root.transform, "GlowMushroom_Left", new Vector3(-0.76f, 0.18f, -0.58f), Vector3.one * 0.70f, Vector3.zero);
                CloneVisual(mushroom, root.transform, "GlowMushroom_Right", new Vector3(0.78f, 0.16f, 0.48f), Vector3.one * 0.55f, new Vector3(0f, 125f, 0f));
            }

            if (root.GetComponentsInChildren<Renderer>(true).Length == 0)
            {
                UnityEngine.Object.DestroyImmediate(root);
                _debugLog?.Invoke("[RestStation] DraugrRestBed produced no renderers; keeping vanilla bed fail-safe.");
                return;
            }
            for (int i = 0; i < original.Length; i++) if (original[i] != null) original[i].enabled = false;
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

        private static GameObject CloneVisual(GameObject source, Transform parent, string name, Vector3 position, Vector3 scale, Vector3 euler)
        {
            if (source == null) return null;
            GameObject clone = StaticVisualCloneRuntime.Create(source, parent, name + "_Visual");
            clone.name = name;
            clone.transform.SetParent(parent, false);
            clone.transform.localPosition = position;
            clone.transform.localRotation = Quaternion.Euler(euler);
            clone.transform.localScale = scale;
            Component[] components = clone.GetComponentsInChildren<Component>(true);
            for (int i = components.Length - 1; i >= 0; i--)
            {
                Component component = components[i];
                if (component == null || component is Transform || component is MeshFilter || component is Renderer) continue;
                UnityEngine.Object.DestroyImmediate(component);
            }
            Renderer[] renderers = clone.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++) if (renderers[i] != null) { renderers[i].gameObject.SetActive(true); renderers[i].enabled = true; }
            clone.SetActive(true);
            return clone;
        }

        private static void SetProperty(object target, string name, object value)
        {
            PropertyInfo property = target?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.CanWrite) property.SetValue(target, value);
        }
    }
}
