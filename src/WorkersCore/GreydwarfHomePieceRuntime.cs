using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    // Optional Jotunn integration by reflection. Workers keeps no compile dependency on Jotunn;
    // all non-home-anchor worker functionality remains usable if Jotunn is absent.
    internal sealed class GreydwarfHomePieceRuntime
    {
        internal const string PrefabName = "ValheimWorkers_GreydwarfHomeNest";
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

        internal GreydwarfHomePieceRuntime(Action<string> debugLog, Action<string> infoLog)
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
                SetProperty(config, "Name", "Grauzwerg-Wurzelnest");
                SetProperty(config, "Description", "Eine umgedrehte, lebende Wurzelkrone als Ruhestaette fuer die Grauzwerg-Familie. Der Arbeiter steht geschuetzt in der hohlen Mitte zwischen den Wurzeln.");
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

                // The vanilla throne is only our stable Piece/collider/placement base. This is NOT
                // a player chair: Workers owns the E interaction and the creature rest seat.
                Component[] components = prefab.GetComponentsInChildren<Component>(true);
                for (int i = 0; i < components.Length; i++)
                {
                    Component component = components[i];
                    if (component == null) continue;

                    string typeName = component.GetType().Name;
                    if (string.Equals(typeName, "Bed", StringComparison.Ordinal) ||
                        string.Equals(typeName, "Chair", StringComparison.Ordinal))
                    {
                        UnityEngine.Object.DestroyImmediate(component);
                    }
                }

                if (prefab.GetComponent<GreydwarfHomeNestInteractable>() == null)
                    prefab.AddComponent<GreydwarfHomeNestInteractable>();

                ApplyGreydwarfNestVisual(prefab, jotunn);
                WorkerRestStationFactory.Attach(
                    prefab,
                    RestStationType.Greydwarf,
                    new Vector3(0f, 0f, -1.55f),
                    new Vector3(0f, 0.04f, 0.04f),
                    new Vector3(1.15f, 0f, -1.25f),
                    new Vector3(0f, 180f, 0f),
                    prefab.transform.Find(GreydwarfThroneRestRuntime.SeatPointName));

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
                _infoLog?.Invoke("[RestStation] Registered GreydwarfRootNest");
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                _status = $"Jotunn home-piece registration pending: {inner.GetType().Name}: {inner.Message}";
                _debugLog?.Invoke(_status);
            }
            catch (Exception ex)
            {
                _status = $"Home-piece registration pending: {ex.GetType().Name}: {ex.Message}";
                _debugLog?.Invoke(_status);
            }
        }

        private void ApplyGreydwarfNestVisual(GameObject prefab, Assembly jotunn)
        {
            if (prefab == null) return;

            try
            {
                Renderer[] baseRenderers = prefab.GetComponentsInChildren<Renderer>(true);
                Collider[] baseColliders = prefab.GetComponentsInChildren<Collider>(true);

                string[] oldVisualNames =
                {
                    "Workers_GreydwarfRootStumpVisual_v1",
                    "Workers_GreydwarfRootStumpVisual_v2",
                    "Workers_GreydwarfRootStumpVisual_v3",
                    "Workers_GreydwarfRootNestVisual_v1",
                    "Workers_GreydwarfRootNestVisual_v2",
                    "Workers_GreydwarfRootNestVisual_v3",
                    "Workers_GreydwarfRootNestVisual_v4",
                    "Workers_GreydwarfRootNestVisual_v5",
                    "Workers_GreydwarfRootNestVisual_v6",
                    "Workers_GreydwarfRootNestVisual_v7",
                    "Workers_GreydwarfRootNestVisual_v9",
                    "Workers_GreydwarfRootNestVisual_v10"
                };
                for (int i = 0; i < oldVisualNames.Length; i++)
                {
                    Transform oldVisual = prefab.transform.Find(oldVisualNames[i]);
                    if (oldVisual != null) UnityEngine.Object.DestroyImmediate(oldVisual.gameObject);
                }

                CreateFallbackRestSeatPoint(prefab.transform);

                GameObject rootSource = ResolvePrefab(jotunn, "Greydwarf_Root");
                GameObject nestSource = ResolvePrefab(jotunn, "Spawner_GreydwarfNest");
                GameObject seedSource = ResolvePrefab(jotunn, GreydwarfHomeSeedRewardRuntime.AncientSeedPrefab);

                _infoLog?.Invoke($"[RestStation] GreydwarfRootNest visual v10 sources: root={(rootSource != null ? "Greydwarf_Root" : "<none>")}, nest={(nestSource != null ? "Spawner_GreydwarfNest" : "<none>")}");

                GameObject visualRoot = new GameObject("Workers_GreydwarfRootNestVisual_v10");
                visualRoot.transform.SetParent(prefab.transform, false);

                int structuralVisuals = 0;

                // v0.5.160: use actual Greydwarf nest/root geometry instead of reshaping wood_pole.
                // Greydwarf_Root uses an LODGroup, so clone its LOD0 mesh renderers directly and
                // keep the authored bark/root material. Arrange several of those real root pieces
                // around an empty centre where the worker can stand naturally.
                if (rootSource != null)
                {
                    const int outerRoots = 9;
                    for (int i = 0; i < outerRoots; i++)
                    {
                        float angle = (360f / outerRoots) * i;
                        float rad = angle * Mathf.Deg2Rad;
                        Vector3 radial = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));

                        float scale = 0.34f + (i % 3) * 0.025f;
                        Vector3 pos = radial * (0.62f + (i % 2) * 0.05f) + Vector3.up * 0.02f;
                        Vector3 euler = new Vector3(
                            (i % 2 == 0 ? 12f : -8f),
                            angle + 18f + (i % 3) * 9f,
                            (i % 3 - 1) * 8f);

                        if (CreateLodStaticVisualClone(
                            rootSource,
                            visualRoot.transform,
                            "RealRoot_Outer_" + i,
                            pos,
                            Vector3.one * scale,
                            euler) != null)
                        {
                            structuralVisuals++;
                        }
                    }

                    const int lowRoots = 6;
                    for (int i = 0; i < lowRoots; i++)
                    {
                        float angle = 30f + (360f / lowRoots) * i;
                        float rad = angle * Mathf.Deg2Rad;
                        Vector3 radial = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
                        Vector3 pos = radial * 0.78f + Vector3.up * 0.03f;
                        Vector3 euler = new Vector3(54f, angle + 90f, (i % 2 == 0 ? 10f : -10f));

                        if (CreateLodStaticVisualClone(
                            rootSource,
                            visualRoot.transform,
                            "RealRoot_Ground_" + i,
                            pos,
                            Vector3.one * 0.30f,
                            euler) != null)
                        {
                            structuralVisuals++;
                        }
                    }
                }

                // If the single Greydwarf_Root prefab cannot be rendered on a specific game build,
                // fall back to the visual shell of the natural Greydwarf spawner itself. This still
                // uses authentic Greydwarf-root geometry/materials and never falls back to fake poles.
                if (structuralVisuals == 0 && nestSource != null)
                {
                    if (CreateLodStaticVisualClone(
                        nestSource,
                        visualRoot.transform,
                        "NaturalGreydwarfNest",
                        new Vector3(0f, 0.02f, 0f),
                        Vector3.one * 0.46f,
                        new Vector3(0f, 17f, 0f)) != null)
                    {
                        structuralVisuals++;
                    }
                }

                if (seedSource != null)
                {
                    CreateSimpleVanillaVisualClone(
                        seedSource,
                        visualRoot.transform,
                        "AncientSeed_RootNestEmblem",
                        new Vector3(0f, 0.58f, -0.66f),
                        new Vector3(0.12f, 0.12f, 0.12f),
                        new Vector3(0f, 12f, 0f),
                        true);
                }

                Renderer[] replacementRenderers = visualRoot.GetComponentsInChildren<Renderer>(true);
                bool hasVisibleReplacement = replacementRenderers.Any(r =>
                    r != null && r.enabled && r.gameObject.activeSelf);

                if (!hasVisibleReplacement || structuralVisuals == 0)
                {
                    UnityEngine.Object.DestroyImmediate(visualRoot);
                    _infoLog?.Invoke($"[RestStation] GreydwarfRootNest visual v10 FAILED: realRootParts={structuralVisuals}, renderers={replacementRenderers.Length}; technical base kept visible.");
                    return;
                }

                for (int i = 0; i < baseRenderers.Length; i++)
                {
                    Renderer renderer = baseRenderers[i];
                    if (renderer != null) renderer.enabled = false;
                }

                for (int i = 0; i < baseColliders.Length; i++)
                {
                    Collider collider = baseColliders[i];
                    if (collider != null) collider.enabled = false;
                }

                WorkerRestStationFactory.EnsurePlacementCollider(
                    prefab,
                    new Vector3(0f, 0.04f, 0f),
                    new Vector3(1.95f, 0.08f, 1.95f));

                _infoLog?.Invoke($"[RestStation] GreydwarfRootNest visual v10 ACTIVE: {structuralVisuals} authentic root visual(s); hollow centre + native idle pose.");
            }
            catch (Exception ex)
            {
                _infoLog?.Invoke($"[RestStation] GreydwarfRootNest visual v10 EXCEPTION -> technical base: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static GameObject CreateLodStaticVisualClone(
            GameObject source,
            Transform parent,
            string name,
            Vector3 localPosition,
            Vector3 localScale,
            Vector3 localEuler)
        {
            if (source == null || parent == null) return null;

            Renderer[] sourceRenderers = null;
            LODGroup lodGroup = source.GetComponentInChildren<LODGroup>(true);
            if (lodGroup != null)
            {
                try
                {
                    LOD[] lods = lodGroup.GetLODs();
                    if (lods != null && lods.Length > 0 && lods[0].renderers != null && lods[0].renderers.Length > 0)
                        sourceRenderers = lods[0].renderers;
                }
                catch { }
            }

            if (sourceRenderers == null || sourceRenderers.Length == 0)
                sourceRenderers = source.GetComponentsInChildren<Renderer>(true);

            GameObject wrapper = new GameObject(name);
            wrapper.transform.SetParent(parent, false);
            wrapper.transform.localPosition = localPosition;
            wrapper.transform.localRotation = Quaternion.Euler(localEuler);
            wrapper.transform.localScale = localScale;

            int copied = 0;
            for (int i = 0; i < sourceRenderers.Length; i++)
            {
                MeshRenderer sourceRenderer = sourceRenderers[i] as MeshRenderer;
                if (sourceRenderer == null) continue;

                MeshFilter sourceFilter = sourceRenderer.GetComponent<MeshFilter>();
                if (sourceFilter == null || sourceFilter.sharedMesh == null) continue;

                GameObject part = new GameObject("RootMesh_" + copied);
                part.transform.SetParent(wrapper.transform, false);

                try
                {
                    Matrix4x4 relative = source.transform.worldToLocalMatrix * sourceRenderer.transform.localToWorldMatrix;
                    part.transform.localPosition = relative.GetColumn(3);
                    part.transform.localRotation = relative.rotation;
                    part.transform.localScale = relative.lossyScale;
                }
                catch
                {
                    part.transform.localPosition = sourceRenderer.transform.localPosition;
                    part.transform.localRotation = sourceRenderer.transform.localRotation;
                    part.transform.localScale = sourceRenderer.transform.localScale;
                }

                MeshFilter filter = part.AddComponent<MeshFilter>();
                filter.sharedMesh = sourceFilter.sharedMesh;

                MeshRenderer renderer = part.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = sourceRenderer.sharedMaterials;
                renderer.enabled = true;
                renderer.shadowCastingMode = sourceRenderer.shadowCastingMode;
                renderer.receiveShadows = sourceRenderer.receiveShadows;

                part.SetActive(true);
                copied++;
            }

            wrapper.SetActive(true);
            if (copied == 0)
            {
                UnityEngine.Object.DestroyImmediate(wrapper);
                return null;
            }

            return wrapper;
        }

        private static void CreateRestSeatPoint(Transform prefabRoot, GameObject throneSource, Vector3 visualPosition, Vector3 visualScale, Vector3 visualEuler)
        {
            if (prefabRoot == null) return;

            Vector3 seatPosition = new Vector3(0f, 0.58f, 0.03f);
            Quaternion seatRotation = Quaternion.identity;
            try
            {
                Component[] sourceComponents = throneSource != null ? throneSource.GetComponents<Component>() : Array.Empty<Component>();
                for (int i = 0; i < sourceComponents.Length; i++)
                {
                    Component component = sourceComponents[i];
                    if (component == null || !string.Equals(component.GetType().Name, "Chair", StringComparison.Ordinal)) continue;

                    FieldInfo field = component.GetType().GetField("m_attachPoint", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    Transform attachPoint = field?.GetValue(component) as Transform;
                    if (attachPoint == null) break;

                    Vector3 sourceLocalPosition = throneSource.transform.InverseTransformPoint(attachPoint.position);
                    Quaternion sourceLocalRotation = Quaternion.Inverse(throneSource.transform.rotation) * attachPoint.rotation;
                    Quaternion visualRotation = Quaternion.Euler(visualEuler);
                    seatPosition = visualPosition + visualRotation * Vector3.Scale(sourceLocalPosition, visualScale);
                    seatRotation = visualRotation * sourceLocalRotation;
                    break;
                }
            }
            catch { }

            GameObject seatObject = new GameObject(GreydwarfThroneRestRuntime.SeatPointName);
            seatObject.transform.SetParent(prefabRoot, false);
            seatObject.transform.localPosition = seatPosition;
            seatObject.transform.localRotation = seatRotation;
        }

        private static void CreateFallbackRestSeatPoint(Transform prefabRoot)
        {
            if (prefabRoot == null) return;
            GameObject seatObject = new GameObject(GreydwarfThroneRestRuntime.SeatPointName);
            seatObject.transform.SetParent(prefabRoot, false);
            seatObject.transform.localPosition = new Vector3(0f, 0.04f, 0.04f);
            seatObject.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        }

        private static bool HasUsableStaticVisual(GameObject source)
        {
            if (source == null) return false;
            MeshRenderer[] renderers = source.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                if (renderer == null) continue;
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null) return true;
            }
            return false;
        }

        private static GameObject CreateDirectStaticVisualClone(
            MeshFilter sourceFilter,
            MeshRenderer sourceRenderer,
            Transform parent,
            string name,
            Vector3 localPosition,
            Vector3 localScale,
            Vector3 localEuler)
        {
            if (sourceFilter == null || sourceFilter.sharedMesh == null || sourceRenderer == null || parent == null)
                return null;

            GameObject clone = new GameObject(name);
            clone.transform.SetParent(parent, false);
            clone.transform.localPosition = localPosition;
            clone.transform.localRotation = Quaternion.Euler(localEuler);
            clone.transform.localScale = localScale;

            MeshFilter filter = clone.AddComponent<MeshFilter>();
            filter.sharedMesh = sourceFilter.sharedMesh;

            MeshRenderer renderer = clone.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = sourceRenderer.sharedMaterials;
            renderer.enabled = true;
            renderer.shadowCastingMode = sourceRenderer.shadowCastingMode;
            renderer.receiveShadows = sourceRenderer.receiveShadows;

            clone.SetActive(true);
            return clone;
        }

        private static GameObject CreateReliableVanillaVisualClone(
            GameObject source,
            Transform parent,
            string name,
            Vector3 localPosition,
            Vector3 localScale,
            Vector3 localEuler)
        {
            if (source == null || parent == null) return null;

            GameObject clone = StaticVisualCloneRuntime.Create(source, parent, name + "_Visual");
            if (clone == null) return null;

            // StaticVisualCloneRuntime intentionally copied only child meshes so far. Several
            // Vanilla log/build prefabs (including the sources used by the root nest) keep their
            // visible MeshFilter/MeshRenderer directly on the prefab root. Copy that root visual
            // explicitly; otherwise the clone contains zero renderers and the nest falls back to
            // the tiny technical wood_pole. Keep this local to the root-nest helper so no existing
            // decoration in other Worker systems changes unexpectedly.
            MeshFilter sourceRootFilter = source.GetComponent<MeshFilter>();
            MeshRenderer sourceRootRenderer = source.GetComponent<MeshRenderer>();
            if (sourceRootFilter != null && sourceRootRenderer != null)
            {
                MeshFilter cloneRootFilter = clone.GetComponent<MeshFilter>();
                if (cloneRootFilter == null) cloneRootFilter = clone.AddComponent<MeshFilter>();
                cloneRootFilter.sharedMesh = sourceRootFilter.sharedMesh;

                MeshRenderer cloneRootRenderer = clone.GetComponent<MeshRenderer>();
                if (cloneRootRenderer == null) cloneRootRenderer = clone.AddComponent<MeshRenderer>();
                cloneRootRenderer.sharedMaterials = sourceRootRenderer.sharedMaterials;
                cloneRootRenderer.enabled = sourceRootRenderer.enabled;
                cloneRootRenderer.shadowCastingMode = sourceRootRenderer.shadowCastingMode;
                cloneRootRenderer.receiveShadows = sourceRootRenderer.receiveShadows;
            }

            clone.name = name;
            clone.transform.SetParent(parent, false);
            clone.transform.localPosition = localPosition;
            clone.transform.localRotation = Quaternion.Euler(localEuler);
            clone.transform.localScale = localScale;

            Component[] components = clone.GetComponentsInChildren<Component>(true);
            for (int i = components.Length - 1; i >= 0; i--)
            {
                Component component = components[i];
                if (component == null || component is Transform || component is MeshFilter || component is Renderer) continue;
                UnityEngine.Object.DestroyImmediate(component);
            }

            Renderer[] renderers = clone.GetComponentsInChildren<Renderer>(true);
            bool visible = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                renderer.gameObject.SetActive(true);
                renderer.enabled = true;
                visible = true;
            }
            clone.SetActive(true);

            if (!visible)
            {
                UnityEngine.Object.DestroyImmediate(clone);
                return null;
            }
            return clone;
        }

        private static GameObject CreateSimpleVanillaVisualClone(
            GameObject source,
            Transform parent,
            string name,
            Vector3 localPosition,
            Vector3 localScale,
            Vector3 localEuler,
            bool recenterVisibleBounds)
        {
            if (source == null || parent == null) return null;

            GameObject wrapper = new GameObject(name);
            wrapper.transform.SetParent(parent, false);
            wrapper.transform.localPosition = localPosition;
            wrapper.transform.localRotation = Quaternion.Euler(localEuler);
            wrapper.transform.localScale = localScale;

            GameObject clone = StaticVisualCloneRuntime.Create(source, wrapper.transform, name + "_Visual");
            clone.name = name + "_Visual";
            clone.transform.SetParent(wrapper.transform, false);
            clone.transform.localPosition = Vector3.zero;
            clone.transform.localRotation = Quaternion.identity;
            clone.transform.localScale = Vector3.one;

            // This helper is intentionally limited to the already proven throne/root/item visuals.
            // It mirrors the stable v0.1.27 approach and is not used for build-piece wood prefabs
            // with WearNTear state stacks (the source of the v0.1.29 white overdraw bug).
            Component[] components = clone.GetComponentsInChildren<Component>(true);
            for (int i = components.Length - 1; i >= 0; i--)
            {
                Component component = components[i];
                if (component == null ||
                    component is Transform ||
                    component is MeshFilter ||
                    component is MeshRenderer ||
                    component is SkinnedMeshRenderer)
                {
                    continue;
                }

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

            if (recenterVisibleBounds)
            {
                bool hasBounds = false;
                Bounds bounds = default;
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null || !renderer.enabled) continue;
                    if (!hasBounds)
                    {
                        bounds = renderer.bounds;
                        hasBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(renderer.bounds);
                    }
                }

                if (hasBounds)
                {
                    Vector3 localBoundsCenter = wrapper.transform.InverseTransformPoint(bounds.center);
                    clone.transform.localPosition -= localBoundsCenter;
                }
            }

            return wrapper;
        }

        private static GameObject CreateCenteredVanillaVisualClone(
            GameObject source,
            Transform parent,
            string name,
            Vector3 localPosition,
            Vector3 localScale,
            Vector3 localEuler)
        {
            if (source == null || parent == null) return null;

            // Place a clean wrapper at the requested throne-space transform, then keep the source
            // clone at identity underneath it. After stripping gameplay components we recenter the
            // actual renderer bounds on the wrapper. This is important for ItemDrop/world prefabs
            // whose root pivot is far away from the visible mesh (the old AncientSeed offset bug).
            GameObject wrapper = new GameObject(name);
            wrapper.transform.SetParent(parent, false);
            wrapper.transform.localPosition = localPosition;
            wrapper.transform.localRotation = Quaternion.Euler(localEuler);
            wrapper.transform.localScale = localScale;

            GameObject clone = StaticVisualCloneRuntime.Create(source, wrapper.transform, name + "_Visual");
            clone.name = name + "_Visual";
            clone.transform.SetParent(wrapper.transform, false);
            clone.transform.localPosition = Vector3.zero;
            clone.transform.localRotation = Quaternion.identity;
            clone.transform.localScale = Vector3.one;

            // Preserve the prefab's intended visual state before removing gameplay components.
            // Build pieces commonly carry New/Worn/Broken objects plus LOD renderers. v0.1.29
            // force-enabled every one of them and removed the LODGroup, so several meshes/material
            // variants rendered on top of each other as a huge white overbright block.
            Component[] components = clone.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null || !string.Equals(component.GetType().Name, "WearNTear", StringComparison.Ordinal))
                    continue;

                try
                {
                    FieldInfo newField = component.GetType().GetField("m_new", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    FieldInfo wornField = component.GetType().GetField("m_worn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    FieldInfo brokenField = component.GetType().GetField("m_broken", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    FieldInfo wetField = component.GetType().GetField("m_wet", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    GameObject newState = newField?.GetValue(component) as GameObject;
                    GameObject wornState = wornField?.GetValue(component) as GameObject;
                    GameObject brokenState = brokenField?.GetValue(component) as GameObject;
                    GameObject wetState = wetField?.GetValue(component) as GameObject;

                    if (newState != null) newState.SetActive(true);
                    if (wornState != null) wornState.SetActive(false);
                    if (brokenState != null) brokenState.SetActive(false);
                    if (wetState != null) wetState.SetActive(false);
                }
                catch { }
            }

            for (int i = components.Length - 1; i >= 0; i--)
            {
                Component component = components[i];
                if (component == null || component is Transform || component is MeshFilter || component is Renderer || component is LODGroup)
                    continue;

                UnityEngine.Object.DestroyImmediate(component);
            }

            // Activate only the cloned prefab root. Child active states and Renderer.enabled values
            // remain untouched so hidden wear states stay hidden and LODGroup keeps controlling LODs.
            clone.SetActive(true);

            Renderer[] renderers = clone.GetComponentsInChildren<Renderer>(true);
            bool hasBounds = false;
            Bounds bounds = default;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (!hasBounds)
            {
                UnityEngine.Object.DestroyImmediate(wrapper);
                return null;
            }

            // Recenter only the visible clone, not the wrapper. The wrapper remains our predictable
            // local throne coordinate while source-specific pivot offsets disappear.
            Vector3 localBoundsCenter = wrapper.transform.InverseTransformPoint(bounds.center);
            clone.transform.localPosition -= localBoundsCenter;
            return wrapper;
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
