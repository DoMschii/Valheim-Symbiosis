using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    // Second species-specific home. It deliberately follows the already-proven Greydwarf pattern:
    // Jotunn is reflection-only, the vanilla bed is only a build/collider base, and visuals borrow
    // real Valheim prefab meshes with their matching materials.
    internal sealed class TrollHomePieceRuntime
    {
        internal const string PrefabName = "ValheimWorkers_TrollCamp";
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

        internal TrollHomePieceRuntime(Action<string> debugLog, Action<string> infoLog)
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

                PropertyInfo instanceProperty = pieceManagerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public);
                object manager = instanceProperty?.GetValue(null);
                if (manager == null)
                {
                    _status = "Jotunn PieceManager.Instance not ready";
                    return;
                }

                MethodInfo getPiece = pieceManagerType.GetMethod("GetPiece", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                if (getPiece != null && getPiece.Invoke(manager, new object[] { PrefabName }) != null)
                {
                    _registered = true;
                    _status = "already registered via Jotunn";
                    return;
                }

                object config = Activator.CreateInstance(pieceConfigType);
                SetProperty(config, "Name", "Troll-Lehnplatz");
                SetProperty(config, "Description", "Ein primitiver Troll-Rastplatz aus drei massiven Baumstaemmen. Der Troll bleibt mit beiden Fuessen am Boden und lehnt sich beim Ruhen nur an die grobe Rueckenwand; keine Armlehnen und keine kuenstliche Sitzanimation.");
                SetProperty(config, "PieceTable", "Hammer");
                SetProperty(config, "Category", "Furniture");
                SetProperty(config, "CraftingStation", "piece_workbench");
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

                addRequirement.Invoke(config, new object[] { "Wood", 1, true });

                ConstructorInfo ctor = customPieceType.GetConstructor(new[] { typeof(string), typeof(string), pieceConfigType });
                if (ctor == null)
                {
                    _status = "Jotunn CustomPiece(name,baseName,PieceConfig) constructor not found";
                    return;
                }

                object customPiece = ctor.Invoke(new object[] { PrefabName, BasePrefabName, config });
                PropertyInfo prefabProperty = customPieceType.GetProperty("PiecePrefab", BindingFlags.Instance | BindingFlags.Public);
                GameObject prefab = prefabProperty?.GetValue(customPiece) as GameObject;
                if (prefab == null)
                {
                    _status = "Jotunn created no PiecePrefab";
                    return;
                }

                Component[] components = prefab.GetComponentsInChildren<Component>(true);
                for (int i = 0; i < components.Length; i++)
                {
                    Component component = components[i];
                    if (component == null) continue;
                    string typeName = component.GetType().Name;
                    if (string.Equals(typeName, "Bed", StringComparison.Ordinal) || string.Equals(typeName, "Chair", StringComparison.Ordinal))
                        UnityEngine.Object.DestroyImmediate(component);
                }

                if (prefab.GetComponent<TrollHomeCampInteractable>() == null)
                    prefab.AddComponent<TrollHomeCampInteractable>();

                ApplyTrollCampVisual(prefab, jotunn);
                WorkerRestStationFactory.Attach(
                    prefab,
                    RestStationType.Troll,
                    new Vector3(0f, 0f, -4.0f),
                    new Vector3(0f, 0.06f, 0.48f),
                    new Vector3(2.7f, 0f, -3.0f),
                    new Vector3(0f, 180f, 0f));
                // v0.5.169: keep the solid placement/interaction collider behind the back-rest.
                // The previous 1.95 m deep collider overlapped the Troll RestPoint at z=0.48 and
                // physically pushed the Troll out while the rest presentation snapped it back in,
                // creating a visible jitter loop. This thin rear collider still covers the trunks
                // for placement/interaction without touching the resting creature.
                WorkerRestStationFactory.EnsurePlacementCollider(prefab, new Vector3(0f, 1.40f, 1.94f), new Vector3(3.65f, 2.70f, 0.34f));

                // v0.5.173: native Troll foot/ground IK can visibly tremble on uneven terrain even
                // while the root transform itself is perfectly still.  Give the idle pose a small
                // invisible, horizontal floor under both feet.  Its top sits at y=0.055, just below
                // the authored RestPoint at y=0.06, so the Troll still stands at the exact same
                // visual height while Valheim's per-foot ground probes see one stable plane.
                WorkerRestStationFactory.EnsureRestSupportCollider(
                    prefab,
                    new Vector3(0f, 0.015f, 0.30f),
                    new Vector3(2.75f, 0.08f, 2.20f));

                MethodInfo addPiece = pieceManagerType.GetMethod("AddPiece", BindingFlags.Instance | BindingFlags.Public, null, new[] { customPieceType }, null);
                if (addPiece == null)
                {
                    _status = "Jotunn PieceManager.AddPiece(CustomPiece) not found";
                    return;
                }

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
                _infoLog?.Invoke("[RestStation] Registered TrollLeanRest");
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                _status = $"Jotunn Troll home-piece registration pending: {inner.GetType().Name}: {inner.Message}";
                _debugLog?.Invoke(_status);
            }
            catch (Exception ex)
            {
                _status = $"Troll home-piece registration pending: {ex.GetType().Name}: {ex.Message}";
                _debugLog?.Invoke(_status);
            }
        }

        private void ApplyTrollCampVisual(GameObject prefab, Assembly jotunn)
        {
            if (prefab == null) return;

            try
            {
                Renderer[] originalRenderers = prefab.GetComponentsInChildren<Renderer>(true);

                string trunkSourceName;
                GameObject trunkSource = ResolveFirstVisualPrefab(
                    jotunn,
                    out trunkSourceName,
                    "FirTree_log", "FirTree_log_half",
                    "PineTree_log", "PineTree_log_half",
                    "Beech_log", "Beech_log_half",
                    "Birch_log", "Birch_log_half",
                    "Oak_log", "Oak_log_half",
                    "wood_pole");

                string restRugName;
                GameObject restRugSource = ResolveFirstVisualPrefab(
                    jotunn,
                    out restRugName,
                    "rug_Bjorn",
                    "rug_wolf",
                    "rug_deer");
                GameObject boneSource = ResolvePrefab(jotunn, "BoneFragments");

                if (trunkSource == null)
                {
                    _debugLog?.Invoke("Troll-Lehnplatz visual kept technical wood_pole base: no usable trunk visual prefab found.");
                    return;
                }

                GameObject visualRoot = new GameObject("Workers_TrollLeanRestVisual_v6");
                visualRoot.transform.SetParent(prefab.transform, false);

                int structuralVisuals = 0;

                // v0.5.185: Move the three lean trunks 0.28 m farther behind the Troll.
                // The previous layout still sat slightly too far forward and clipped into his back/hips.
                // Keep the same silhouette, scales and angles; only the depth changes.
                // v0.5.165: Return to the strong back-rest silhouette from v0.5.163, but
                // remove every arm/side rail. A Troll does not need a human chair: three huge,
                // slightly crooked whole trunks form a primitive leaning wall. The Troll simply
                // stands close to it in its native idle pose; the front and both sides stay open.
                Vector3[] trunkPositions =
                {
                    new Vector3(-0.96f, 1.52f, 1.74f),
                    new Vector3( 0.00f, 1.78f, 1.88f),
                    new Vector3( 0.98f, 1.50f, 1.72f)
                };

                Vector3[] trunkScales =
                {
                    new Vector3(0.94f, 1.18f, 0.94f),
                    new Vector3(1.04f, 1.32f, 1.04f),
                    new Vector3(0.92f, 1.16f, 0.92f)
                };

                Vector3[] trunkEuler =
                {
                    new Vector3(-6f, -10f,  5f),
                    new Vector3( 3f,   8f, -2f),
                    new Vector3(-5f,  18f, -6f)
                };

                for (int i = 0; i < trunkPositions.Length; i++)
                {
                    GameObject part = CreateVanillaVisualClone(
                        trunkSource,
                        visualRoot.transform,
                        "LeanTrunk_" + i,
                        trunkPositions[i],
                        trunkScales[i],
                        trunkEuler[i]);
                    if (HasEnabledRenderer(part)) structuralVisuals++;
                }

                // v0.5.166: the Troll rests on an animal rug, never on Troll hide.
                // Prefer the 1.0 bear rug; wolf/deer are compatibility fallbacks. Rug prefabs
                // already lie flat in Valheim, so keep them horizontal and only lift them a
                // couple of centimetres to prevent z-fighting with uneven terrain/floors. Keep the
                // original 1:1 Vanilla rug size; the previous 1.75x scale made the pelt dwarf the Troll.
                if (restRugSource != null)
                {
                    GameObject rug = CreateVanillaVisualClone(
                        restRugSource,
                        visualRoot.transform,
                        "RestRug_Ground",
                        new Vector3(0f, 0.018f, 0.34f),
                        Vector3.one,
                        new Vector3(0f, 180f, 0f));

                    if (!HasEnabledRenderer(rug))
                        _debugLog?.Invoke($"Troll-Lehnplatz rug visual unavailable: source={restRugName}.");
                }

                // Small asymmetrical trophies keep it primitive instead of looking like player furniture.
                if (boneSource != null)
                {
                    CreateVanillaVisualClone(
                        boneSource,
                        visualRoot.transform,
                        "BoneToken_Left",
                        new Vector3(-1.46f, 1.02f, 0.70f),
                        new Vector3(1.65f, 1.65f, 1.65f),
                        new Vector3(10f, 42f, 0f));
                }

                Renderer[] replacementRenderers = visualRoot.GetComponentsInChildren<Renderer>(true);
                bool hasVisibleReplacement = replacementRenderers.Any(r =>
                    r != null && r.enabled && r.gameObject.activeSelf);

                if (!hasVisibleReplacement || structuralVisuals == 0)
                {
                    UnityEngine.Object.DestroyImmediate(visualRoot);
                    _debugLog?.Invoke($"Troll-Lehnplatz visual v6 fallback: source={trunkSourceName}, structuralParts={structuralVisuals}, renderers={replacementRenderers.Length}.");
                    return;
                }

                for (int i = 0; i < originalRenderers.Length; i++)
                {
                    Renderer renderer = originalRenderers[i];
                    if (renderer != null) renderer.enabled = false;
                }

                _infoLog?.Invoke($"[RestStation] Troll-Lehnplatz visual v8 ACTIVE: trunks={trunkSourceName}, rug={(string.IsNullOrEmpty(restRugName) ? "none" : restRugName)}, {structuralVisuals} back-rest trunks; rug vanilla-scale 1:1 + grounded + no TrollHide.");
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Troll-Lehnplatz visual fallback to technical base: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static GameObject ResolveFirstVisualPrefab(Assembly jotunn, out string resolvedName, params string[] candidates)
        {
            resolvedName = string.Empty;
            if (candidates == null) return null;

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                GameObject prefab = ResolvePrefab(jotunn, candidate);
                if (prefab == null) continue;

                Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
                if (renderers == null || renderers.Length == 0) continue;

                resolvedName = candidate;
                return prefab;
            }

            return null;
        }

        private static bool HasEnabledRenderer(GameObject go)
        {
            if (go == null) return false;
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer != null && renderer.enabled && renderer.gameObject.activeSelf)
                    return true;
            }
            return false;
        }

        private static GameObject CreateVanillaVisualClone(
            GameObject source,
            Transform parent,
            string name,
            Vector3 localPosition,
            Vector3 localScale,
            Vector3 localEuler)
        {
            if (source == null || parent == null) return null;

            GameObject clone = StaticVisualCloneRuntime.Create(source, parent, name + "_Visual");
            clone.name = name;
            clone.transform.SetParent(parent, false);
            clone.transform.localPosition = localPosition;
            clone.transform.localRotation = Quaternion.Euler(localEuler);
            clone.transform.localScale = localScale;

            Component[] components = clone.GetComponentsInChildren<Component>(true);
            for (int i = components.Length - 1; i >= 0; i--)
            {
                Component component = components[i];
                if (component == null || component is Transform || component is MeshFilter || component is MeshRenderer || component is SkinnedMeshRenderer)
                    continue;

                UnityEngine.Object.DestroyImmediate(component);
            }

            Renderer[] renderers = clone.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                renderer.gameObject.SetActive(true);
                renderer.enabled = true;
            }

            clone.SetActive(true);
            return clone;
        }

        private static GameObject ResolvePrefab(Assembly jotunn, string prefabName)
        {
            if (jotunn == null || string.IsNullOrEmpty(prefabName)) return null;

            try
            {
                Type prefabManagerType = jotunn.GetType("Jotunn.Managers.PrefabManager", false);
                if (prefabManagerType == null) return null;

                PropertyInfo instanceProperty = prefabManagerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public);
                object manager = instanceProperty?.GetValue(null);
                if (manager == null) return null;

                MethodInfo getPrefab = prefabManagerType.GetMethod("GetPrefab", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                return getPrefab?.Invoke(manager, new object[] { prefabName }) as GameObject;
            }
            catch
            {
                return null;
            }
        }

        private static void SetProperty(object target, string name, object value)
        {
            if (target == null) return;
            PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.CanWrite)
                property.SetValue(target, value);
        }
    }
}
