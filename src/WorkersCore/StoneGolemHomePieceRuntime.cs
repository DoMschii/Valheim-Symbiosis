using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    // Stone/Ice Golem rest platform. It keeps the established home-anchor prefab identity while
    // replacing the old cheap test visual with a large, crystal-lined mountain-biome altar.
    internal sealed class StoneGolemHomePieceRuntime
    {
        internal const string PrefabName = "ValheimWorkers_StoneGolemHome";
        internal const string BasePrefabName = "bed";

        private readonly Action<string> _debugLog;
        private readonly Action<string> _infoLog;
        private float _nextAttempt;
        private float _attemptDeadline;
        private bool _registered;
        private bool _gaveUp;
        private string _status = "waiting for Jotunn PieceManager";

        internal bool IsRegistered => _registered;
        internal string Status => _registered ? "registered" : _status;

        internal StoneGolemHomePieceRuntime(Action<string> debugLog, Action<string> infoLog)
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
                SetProperty(config, "Name", "Golem-Felsruhestätte");
                SetProperty(config, "Description", "Ein natürlicher Felskranz für Stone- und Ice-Golems. Im Ruhezustand wirkt der zusammengesunkene Golem wie der mittlere Fels; beim Aufstehen bleibt dort bewusst eine Lücke.");
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

                Component[] components = prefab.GetComponentsInChildren<Component>(true);
                for (int i = 0; i < components.Length; i++)
                {
                    Component component = components[i];
                    if (component != null && string.Equals(component.GetType().Name, "Bed", StringComparison.Ordinal))
                        UnityEngine.Object.DestroyImmediate(component);
                }

                if (prefab.GetComponent<StoneGolemHomeInteractable>() == null)
                    prefab.AddComponent<StoneGolemHomeInteractable>();

                ApplyCrystalRestVisual(prefab, jotunn);
                WorkerRestStationFactory.Attach(
                    prefab,
                    RestStationType.Golem,
                    new Vector3(0f, 0f, -4.8f),
                    // v0.5.174: the middle of the rock ring is intentionally empty.  The Stone Golem's
                    // native sleeping/dormant pose becomes the "missing" centre boulder while resting.
                    new Vector3(0f, 0.08f, 0.15f),
                    new Vector3(3.7f, 0f, -3.9f),
                    new Vector3(0f, 180f, 0f));
                // Keep the build/placement collider below the visual ground plane so the dormant Golem
                // cannot collide with the furniture itself.  The surrounding visual boulders are static
                // shells only; navigation remains fully owned by Navigator v5.
                WorkerRestStationFactory.EnsurePlacementCollider(prefab, new Vector3(0f, -0.16f, 0.10f), new Vector3(6.4f, 0.28f, 5.8f));
                WorkerRestStationFactory.EnsureRestSupportCollider(prefab, new Vector3(0f, 0.015f, 0.15f), new Vector3(2.35f, 0.08f, 2.35f));

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
                _infoLog?.Invoke("[RestStation] Registered GolemCrystalRest");
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                _status = $"Jotunn Stone Golem home-piece registration pending: {inner.GetType().Name}: {inner.Message}";
                _debugLog?.Invoke(_status);
            }
            catch (Exception ex)
            {
                _status = $"Stone Golem home-piece registration pending: {ex.GetType().Name}: {ex.Message}";
                _debugLog?.Invoke(_status);
            }
        }

        private void ApplyCrystalRestVisual(GameObject prefab, Assembly jotunn)
        {
            try
            {
                Renderer[] original = prefab.GetComponentsInChildren<Renderer>(true);
                GameObject stone = ResolvePrefab(jotunn, "Stone");
                GameObject crystal = ResolvePrefab(jotunn, "Crystal");
                GameObject freezeGland = ResolvePrefab(jotunn, "FreezeGland");
                if (stone == null)
                {
                    _debugLog?.Invoke("[RestStation] GolemCrystalRest visual source missing: Stone; keeping vanilla base fail-safe.");
                    return;
                }

                GameObject root = new GameObject("Workers_GolemCrystalRestVisual_v2");
                root.transform.SetParent(prefab.transform, false);
                // v0.5.174: natural mountain rock ring.  There is deliberately NO centre stone.
                // A sleeping Stone Golem fills that gap and visually disappears into the formation;
                // once it wakes for work the empty middle makes the "rock came alive" illusion readable.
                Vector3[] platformPositions =
                {
                    new Vector3(-2.35f, 0.18f, -1.25f),
                    new Vector3(-1.35f, 0.22f, -2.05f),
                    new Vector3(0.10f, 0.16f, -2.35f),
                    new Vector3(1.48f, 0.20f, -1.98f),
                    new Vector3(2.38f, 0.24f, -1.05f),
                    new Vector3(2.55f, 0.28f, 0.55f),
                    new Vector3(1.65f, 0.26f, 1.70f),
                    new Vector3(0.15f, 0.21f, 2.10f),
                    new Vector3(-1.45f, 0.25f, 1.82f),
                    new Vector3(-2.50f, 0.22f, 0.65f)
                };
                Vector3[] platformScales =
                {
                    new Vector3(3.0f, 2.25f, 2.75f), new Vector3(2.55f, 2.00f, 2.85f),
                    new Vector3(2.70f, 1.85f, 2.55f), new Vector3(2.55f, 2.10f, 2.75f),
                    new Vector3(3.05f, 2.30f, 2.70f), new Vector3(2.65f, 2.15f, 2.60f),
                    new Vector3(2.85f, 2.25f, 2.75f), new Vector3(2.50f, 1.90f, 2.55f),
                    new Vector3(2.80f, 2.20f, 2.80f), new Vector3(3.00f, 2.30f, 2.70f)
                };
                for (int i = 0; i < platformPositions.Length; i++)
                    CreateVisualClone(stone, root.transform, "RestBoulder_" + i, platformPositions[i],
                        platformScales[i], new Vector3((i * 7f) % 19f - 9f, i * 37f, (i * 11f) % 17f - 8f));

                if (crystal != null)
                {
                    Vector3[] crystalPositions =
                    {
                        new Vector3(-2.58f, 0.72f, 1.05f),
                        new Vector3(2.48f, 0.68f, 0.96f),
                        new Vector3(-0.72f, 0.62f, 2.24f),
                        new Vector3(0.92f, 0.60f, 2.18f)
                    };
                    for (int i = 0; i < crystalPositions.Length; i++)
                        CreateVisualClone(crystal, root.transform, "FrostCrystal_" + i, crystalPositions[i],
                            new Vector3(0.85f + (i % 2) * 0.15f, 1.35f + (i % 2) * 0.18f, 0.85f),
                            new Vector3(i % 2 == 0 ? -8f : 9f, i * 53f, i % 2 == 0 ? -6f : 7f));
                }
                else
                {
                    _debugLog?.Invoke("[RestStation] GolemCrystalRest optional visual source missing: Crystal; stone platform remains usable.");
                }

                if (freezeGland != null)
                {
                    CreateVisualClone(freezeGland, root.transform, "FrostCore_Left", new Vector3(-2.05f, 0.52f, -1.52f), Vector3.one * 0.95f, new Vector3(0f, 35f, 0f));
                    CreateVisualClone(freezeGland, root.transform, "FrostCore_Right", new Vector3(2.02f, 0.50f, -1.48f), Vector3.one * 0.95f, new Vector3(0f, 210f, 0f));
                }

                if (root.GetComponentsInChildren<Renderer>(true).Length == 0)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                    return;
                }
                for (int i = 0; i < original.Length; i++) if (original[i] != null) original[i].enabled = false;
                _debugLog?.Invoke("Golem-Felsruhestätte visual v3: natural boulder ring with intentionally empty centre for the native dormant Stone Golem pose.");
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Golem-Steinlager visual fallback to vanilla base: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static GameObject CreateVisualClone(GameObject source, Transform parent, string name, Vector3 pos, Vector3 scale, Vector3 euler)
        {
            GameObject clone = StaticVisualCloneRuntime.Create(source, parent, name + "_Visual");
            clone.name = name;
            clone.transform.SetParent(parent, false);
            clone.transform.localPosition = pos;
            clone.transform.localRotation = Quaternion.Euler(euler);
            clone.transform.localScale = scale;
            Component[] components = clone.GetComponentsInChildren<Component>(true);
            for (int i = components.Length - 1; i >= 0; i--)
            {
                Component component = components[i];
                if (component == null || component is Transform || component is MeshFilter || component is MeshRenderer || component is SkinnedMeshRenderer) continue;
                UnityEngine.Object.DestroyImmediate(component);
            }
            Renderer[] renderers = clone.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++) if (renderers[i] != null) { renderers[i].gameObject.SetActive(true); renderers[i].enabled = true; }
            clone.SetActive(true);
            return clone;
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
