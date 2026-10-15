using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Late-safe compatibility bridge for SmartStorage feeding pieces.
    /// v0.5.201 no longer assumes one exact prefab name: it discovers loaded SmartStorage
    /// feeding pieces by prefab/display/component metadata and re-exposes the original prefab.
    /// If SmartStorage reports feeding enabled but does not actually create a buildable feeding
    /// prefab, Workers creates a network-safe Jotunn clone of a vanilla container as fallback so
    /// the livestock system always has a buildable trough/dispenser.
    /// </summary>
    internal sealed class SmartStorageFeedingPieceBridge
    {
        private const string ExactTrough = "SmartStorage_FeedingTrough";
        private const string ExactDispenser = "SmartStorage_FeedingDispenser";
        private const string FallbackTrough = "ValheimWorkers_FeedingTrough";
        private const string FallbackDispenser = "ValheimWorkers_FeedingDispenser";

        private static SmartStorageFeedingPieceBridge Active;

        private readonly Action<string> _debugLog;
        private readonly Action<string> _infoLog;
        private Delegate _prefabsRegisteredDelegate;
        private EventInfo _prefabsRegisteredEvent;
        private bool _hookAttempted;
        private bool _registered;
        private bool _fallbackAttempted;
        private float _nextAttempt;
        private float _deadline;
        private string _status = "waiting for SmartStorage/Jotunn";

        internal string Status => _registered ? "ready: " + _status : _status;

        internal SmartStorageFeedingPieceBridge(Action<string> debugLog, Action<string> infoLog)
        {
            _debugLog = debugLog;
            _infoLog = infoLog;
        }

        internal void Begin()
        {
            _deadline = Time.unscaledTime + 75f;
            _nextAttempt = Time.unscaledTime + 2.5f;
            TryHookPrefabsRegistered();

            // v0.5.457: heal already-loaded troughs exactly once, then switch to the
            // WorkerWorldIndex Container.Awake lifecycle for all future containers.
            RefreshLoadedTroughMarkers();
            Active = this;
        }

        internal void Tick()
        {
            if (_registered) return;
            if (Time.unscaledTime > _deadline)
            {
                _status = "not found after startup window";
                return;
            }
            if (Time.unscaledTime < _nextAttempt) return;
            _nextAttempt = Time.unscaledTime + 2f;
            TryRegister();
        }

        internal void Shutdown()
        {
            if (ReferenceEquals(Active, this)) Active = null;

            if (_prefabsRegisteredEvent != null && _prefabsRegisteredDelegate != null)
            {
                try { _prefabsRegisteredEvent.RemoveEventHandler(null, _prefabsRegisteredDelegate); }
                catch { }
            }
            _prefabsRegisteredEvent = null;
            _prefabsRegisteredDelegate = null;
        }

        internal static void NotifyContainerLoaded(Container container)
        {
            SmartStorageFeedingPieceBridge active = Active;
            if (active == null || container == null) return;
            active.EnsureLoadedFeedingMarker(container);
        }

        private void TryHookPrefabsRegistered()
        {
            if (_hookAttempted) return;
            _hookAttempted = true;
            try
            {
                Assembly jotunn = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "Jotunn", StringComparison.OrdinalIgnoreCase));
                Type prefabManagerType = jotunn?.GetType("Jotunn.Managers.PrefabManager", false);
                EventInfo evt = prefabManagerType?.GetEvent("OnPrefabsRegistered", BindingFlags.Static | BindingFlags.Public);
                if (evt == null || evt.EventHandlerType == null)
                {
                    _status = "Jotunn prefab event unavailable; delayed discovery active";
                    return;
                }

                Action callback = OnPrefabsRegistered;
                _prefabsRegisteredDelegate = Delegate.CreateDelegate(evt.EventHandlerType, callback.Target, callback.Method);
                evt.AddEventHandler(null, _prefabsRegisteredDelegate);
                _prefabsRegisteredEvent = evt;
                _status = "waiting for SmartStorage prefab registration";
            }
            catch (Exception ex)
            {
                _status = $"prefab hook failed: {ex.GetType().Name}";
                _debugLog?.Invoke($"[SmartStorageBridge] Prefab event hook failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void OnPrefabsRegistered()
        {
            _nextAttempt = Mathf.Min(_nextAttempt, Time.unscaledTime + 0.25f);
            _status = "prefabs registered; discovering feeding pieces";
        }

        private void TryRegister()
        {
            try
            {
                Assembly jotunn = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "Jotunn", StringComparison.OrdinalIgnoreCase));
                if (jotunn == null) { _status = "Jotunn not loaded"; return; }

                Type prefabManagerType = jotunn.GetType("Jotunn.Managers.PrefabManager", false);
                Type pieceManagerType = jotunn.GetType("Jotunn.Managers.PieceManager", false);
                if (prefabManagerType == null || pieceManagerType == null) { _status = "Jotunn managers unavailable"; return; }

                object prefabManager = prefabManagerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                object pieceManager = pieceManagerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                if (prefabManager == null || pieceManager == null) { _status = "Jotunn managers not ready"; return; }

                MethodInfo getPrefab = prefabManagerType.GetMethod("GetPrefab", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                MethodInfo getPieceTable = pieceManagerType.GetMethod("GetPieceTable", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                MethodInfo registerPiece = pieceManagerType.GetMethod("RegisterPieceInPieceTable", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(GameObject), typeof(string), typeof(string) }, null);
                if (getPieceTable == null || registerPiece == null) { _status = "required Jotunn PieceManager API unavailable"; return; }

                PieceTable hammer = getPieceTable.Invoke(pieceManager, new object[] { "Hammer" }) as PieceTable;
                if (hammer == null || hammer.m_pieces == null) { _status = "Hammer table not ready"; return; }

                GameObject trough = FindExact(getPrefab, prefabManager, ExactTrough) ?? FindExact(getPrefab, prefabManager, FallbackTrough);
                GameObject dispenser = FindExact(getPrefab, prefabManager, ExactDispenser) ?? FindExact(getPrefab, prefabManager, FallbackDispenser);

                DiscoverFeedingPrefabs(ref trough, ref dispenser, hammer);

                // SmartStorage 0.5.39 in the user's current log says feeding is enabled, but no feeding
                // registration appears at all. If no originals materialize after startup, create safe
                // fallbacks instead of leaving the feature impossible to build.
                if ((trough == null || dispenser == null) && !_fallbackAttempted && Time.unscaledTime > 8f)
                {
                    _fallbackAttempted = true;
                    TryCreateFallbacks(prefabManagerType, prefabManager, hammer, ref trough, ref dispenser);
                }

                int present = 0;
                if (trough != null && EnsureInHammer(trough, true, hammer, pieceManager, registerPiece)) present++;
                if (dispenser != null && EnsureInHammer(dispenser, false, hammer, pieceManager, registerPiece)) present++;

                string troughName = trough != null ? SpeciesRoleRegistry.NormalizePrefabName(trough.name ?? string.Empty) : "missing";
                string dispenserName = dispenser != null ? SpeciesRoleRegistry.NormalizePrefabName(dispenser.name ?? string.Empty) : "missing";
                _status = $"trough={troughName} dispenser={dispenserName} hammer={present}/2";
                _debugLog?.Invoke($"[SmartStorageBridge] feeding discovery: {_status}");
                _registered = present == 2;
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex.InnerException ?? ex;
                _status = $"registration failed: {root.GetType().Name}";
                _debugLog?.Invoke($"[SmartStorageBridge] Registration failed: {root.GetType().Name}: {root.Message}");
            }
            catch (Exception ex)
            {
                _status = $"registration failed: {ex.GetType().Name}";
                _debugLog?.Invoke($"[SmartStorageBridge] Registration failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private GameObject FindExact(MethodInfo getPrefab, object prefabManager, string prefabName)
        {
            GameObject prefab = null;
            if (getPrefab != null)
            {
                try { prefab = getPrefab.Invoke(prefabManager, new object[] { prefabName }) as GameObject; }
                catch { }
            }
            if (prefab == null) prefab = TryGetZNetScenePrefab(prefabName);
            return prefab;
        }

        private void DiscoverFeedingPrefabs(ref GameObject trough, ref GameObject dispenser, PieceTable hammer)
        {
            var candidates = new List<GameObject>();
            var seen = new HashSet<int>();

            Action<GameObject> consider = go =>
            {
                if (go == null || !seen.Add(go.GetInstanceID())) return;
                Piece p = go.GetComponent<Piece>() ?? go.GetComponentInChildren<Piece>(true);
                Container c = go.GetComponent<Container>() ?? go.GetComponentInChildren<Container>(true);
                if (p == null || c == null) return;
                string meta = ((go.name ?? string.Empty) + " " + (p.m_name ?? string.Empty) + " " + (p.m_description ?? string.Empty)).ToLowerInvariant();
                string compact = meta.Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty);
                bool feedish = compact.Contains("feeding") || compact.Contains("feed") || compact.Contains("futter") || compact.Contains("trough") || compact.Contains("trog") || compact.Contains("dispenser") || compact.Contains("spender");
                if (!feedish) return;
                candidates.Add(go);
            };

            try
            {
                if (hammer?.m_pieces != null)
                    for (int i = 0; i < hammer.m_pieces.Count; i++) consider(hammer.m_pieces[i]);
            }
            catch { }

            try
            {
                GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < all.Length; i++) consider(all[i]);
            }
            catch { }

            for (int i = 0; i < candidates.Count; i++)
            {
                GameObject go = candidates[i];
                Piece p = go.GetComponent<Piece>() ?? go.GetComponentInChildren<Piece>(true);
                string meta = ((go.name ?? string.Empty) + " " + (p?.m_name ?? string.Empty)).ToLowerInvariant();
                string compact = meta.Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty);
                if (trough == null && (compact.Contains("trough") || compact.Contains("trog"))) trough = go;
                if (dispenser == null && (compact.Contains("dispenser") || compact.Contains("spender"))) dispenser = go;
            }

            if (candidates.Count > 0)
                _debugLog?.Invoke("[SmartStorageBridge] feeding candidates: " + string.Join(", ", candidates.Select(x => x.name).Distinct().Take(12).ToArray()));
        }

        private void TryCreateFallbacks(Type prefabManagerType, object prefabManager, PieceTable hammer, ref GameObject trough, ref GameObject dispenser)
        {
            try
            {
                MethodInfo clone = prefabManagerType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "CreateClonedPrefab" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(string) && m.GetParameters()[1].ParameterType == typeof(string));
                if (clone == null)
                {
                    _debugLog?.Invoke("[SmartStorageBridge] fallback unavailable: CreateClonedPrefab API not found.");
                    return;
                }

                GameObject source = FindContainerTemplate(hammer);
                if (source == null)
                {
                    _debugLog?.Invoke("[SmartStorageBridge] fallback unavailable: no buildable Container template in Hammer table.");
                    return;
                }
                string sourceName = SpeciesRoleRegistry.NormalizePrefabName(source.name ?? string.Empty);

                if (trough == null)
                {
                    trough = clone.Invoke(prefabManager, new object[] { FallbackTrough, sourceName }) as GameObject;
                    PrepareFallback(trough, true);
                    if (trough != null) _infoLog?.Invoke($"[SmartStorageBridge] Created fallback Futtertrog from {sourceName}.");
                }
                if (dispenser == null)
                {
                    dispenser = clone.Invoke(prefabManager, new object[] { FallbackDispenser, sourceName }) as GameObject;
                    PrepareFallback(dispenser, false);
                    if (dispenser != null) _infoLog?.Invoke($"[SmartStorageBridge] Created fallback Futterspender from {sourceName}.");
                }
            }
            catch (Exception ex)
            {
                Exception root = ex is TargetInvocationException tie ? (tie.InnerException ?? tie) : ex;
                _debugLog?.Invoke($"[SmartStorageBridge] fallback creation failed: {root.GetType().Name}: {root.Message}");
            }
        }

        private static GameObject FindContainerTemplate(PieceTable hammer)
        {
            if (hammer?.m_pieces == null) return null;
            GameObject firstContainer = null;
            for (int i = 0; i < hammer.m_pieces.Count; i++)
            {
                GameObject go = hammer.m_pieces[i];
                if (go == null) continue;
                Container c = go.GetComponent<Container>() ?? go.GetComponentInChildren<Container>(true);
                Piece p = go.GetComponent<Piece>() ?? go.GetComponentInChildren<Piece>(true);
                if (c == null || p == null) continue;
                string n = (go.name ?? string.Empty).ToLowerInvariant();
                if (n.Contains("chest") || n.Contains("chest_wood") || n.Contains("piece_chest")) return go;
                if (firstContainer == null) firstContainer = go;
            }
            return firstContainer;
        }

        private static void PrepareFallback(GameObject prefab, bool trough)
        {
            if (prefab == null) return;
            Piece piece = prefab.GetComponent<Piece>() ?? prefab.GetComponentInChildren<Piece>(true);
            if (piece != null)
            {
                piece.m_enabled = true;
                piece.m_name = trough ? "Futtertrog" : "Futterspender";
                piece.m_description = trough
                    ? "Automatischer Gehege-Futtertrog fuer den Tierpfleger/Metzger. E = Gehege/Futter. Das interne Lager ist nicht als Truhe gedacht."
                    : "Automatischer Futterspender fuer den Tierpfleger/Metzger. E = Gehege/Futter. Gibt Futter an hungrige Tiere aus.";
            }

            // Container hover text comes from Container rather than Piece on vanilla chests.
            // Rename it as well so the player never sees "Truhe" on a feeding station.
            TryRenameContainer(prefab, trough ? "Futtertrog" : "Futterspender");

            // The fallback starts from a vanilla chest only to inherit its network-safe Container,
            // ZNetView and placement setup. It must never LOOK like a chest. Replace only the
            // renderers with a purpose-built wooden trough/dispenser while keeping the container
            // machinery and interaction collider intact.
            BuildDedicatedFeedingVisual(prefab, trough);

            LivestockFeedingStationMarker stationMarker = prefab.GetComponent<LivestockFeedingStationMarker>();
            if (stationMarker == null) stationMarker = prefab.AddComponent<LivestockFeedingStationMarker>();
            stationMarker.Configure(trough);

            if (trough && prefab.GetComponent<LivestockFeedSignMarker>() == null)
                prefab.AddComponent<LivestockFeedSignMarker>();
        }

        private static void TryRenameContainer(GameObject prefab, string displayName)
        {
            if (prefab == null || string.IsNullOrEmpty(displayName)) return;
            try
            {
                Container container = prefab.GetComponent<Container>() ?? prefab.GetComponentInChildren<Container>(true);
                if (container == null) return;
                FieldInfo field = typeof(Container).GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.FieldType == typeof(string)) field.SetValue(container, displayName);
            }
            catch { }
        }

        private static void BuildDedicatedFeedingVisual(GameObject prefab, bool trough)
        {
            if (prefab == null) return;
            Transform oldVisual = prefab.transform.Find("WorkersFeedingVisual");
            if (oldVisual != null)
            {
                try { UnityEngine.Object.DestroyImmediate(oldVisual.gameObject); } catch { }
            }

            // Hide every renderer inherited from the technical chest clone.  The container remains
            // fully functional, but no chest mesh/material may leak into the feeding-station look.
            try
            {
                Renderer[] existing = prefab.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < existing.Length; i++)
                {
                    Renderer r = existing[i];
                    if (r != null) r.enabled = false;
                }
            }
            catch { }

            GameObject root = new GameObject("WorkersFeedingVisual");
            root.transform.SetParent(prefab.transform, false);

            // v0.5.200: no Unity primitive cubes and no chest material reuse.  Build the visible
            // object only from actual Valheim meshes/materials, so it reads like a native farm prop.
            GameObject board = ResolveVisualPrefab("wood_floor", "wood_floor_1x1", "wood_wall_half", "woodwall");
            GameObject beam = ResolveVisualPrefab("wood_beam_26", "wood_beam", "wood_pole2", "wood_pole");
            GameObject log = ResolveVisualPrefab("FirTree_log_half", "FirTree_log", "PineTree_log_half", "PineTree_log");
            if (board == null) board = beam ?? log;
            if (beam == null) beam = board ?? log;
            if (log == null) log = beam ?? board;

            if (trough)
            {
                // Long, low open trough.  Broad floor planks form the base while rough beams/logs
                // form the rim.  The silhouette is intentionally asymmetric and agricultural.
                AddVanillaVisual(board, root.transform, "TroughBottomA", new Vector3(-0.58f, 0.22f, 0f), new Vector3(0.78f, 0.18f, 0.42f), new Vector3(0f, 0f, 0f));
                AddVanillaVisual(board, root.transform, "TroughBottomB", new Vector3( 0.58f, 0.22f, 0f), new Vector3(0.78f, 0.18f, 0.42f), new Vector3(0f, 180f, 0f));

                AddVanillaVisual(log, root.transform, "TroughRimLeft",  new Vector3(0f, 0.44f, -0.48f), new Vector3(0.34f, 0.34f, 1.36f), new Vector3(0f, 0f, 90f));
                AddVanillaVisual(log, root.transform, "TroughRimRight", new Vector3(0f, 0.43f,  0.48f), new Vector3(0.32f, 0.32f, 1.34f), new Vector3(0f, 180f, 90f));
                AddVanillaVisual(beam, root.transform, "TroughEndA", new Vector3(-1.40f, 0.40f, 0f), new Vector3(0.30f, 0.55f, 0.62f), new Vector3(0f, 90f, 2f));
                AddVanillaVisual(beam, root.transform, "TroughEndB", new Vector3( 1.40f, 0.39f, 0f), new Vector3(0.30f, 0.53f, 0.62f), new Vector3(0f, 90f, -3f));

                // Low cross-feet; deliberately not perfectly mirrored.
                AddVanillaVisual(beam, root.transform, "TroughFootA", new Vector3(-0.86f, 0.08f, 0.02f), new Vector3(0.22f, 0.55f, 0.22f), new Vector3(0f, 90f, 88f));
                AddVanillaVisual(beam, root.transform, "TroughFootB", new Vector3( 0.91f, 0.08f,-0.02f), new Vector3(0.22f, 0.55f, 0.22f), new Vector3(0f, 90f, 92f));

                // The trough itself stays visually empty. Real food appears only when the station
                // actually releases an ItemDrop for a hungry animal.
            }
            else
            {
                // Compact raised hopper.  Four rough legs, a narrow wooden bin, a tapered outlet
                // and one small feeding tray.  No roof slab and no broad chest-like box.
                AddVanillaVisual(beam, root.transform, "HopperLegFL", new Vector3(-0.34f, 0.46f, -0.20f), new Vector3(0.20f, 0.74f, 0.20f), new Vector3(0f, 0f, -2f));
                AddVanillaVisual(beam, root.transform, "HopperLegFR", new Vector3( 0.34f, 0.46f, -0.20f), new Vector3(0.20f, 0.74f, 0.20f), new Vector3(0f, 0f,  3f));
                AddVanillaVisual(beam, root.transform, "HopperLegBL", new Vector3(-0.34f, 0.46f,  0.22f), new Vector3(0.20f, 0.74f, 0.20f), new Vector3(0f, 0f,  2f));
                AddVanillaVisual(beam, root.transform, "HopperLegBR", new Vector3( 0.34f, 0.46f,  0.22f), new Vector3(0.20f, 0.74f, 0.20f), new Vector3(0f, 0f, -3f));

                AddVanillaVisual(board, root.transform, "HopperBack",  new Vector3(0f, 1.25f,  0.27f), new Vector3(0.46f, 0.62f, 0.16f), new Vector3(90f, 0f, 0f));
                AddVanillaVisual(board, root.transform, "HopperFront", new Vector3(0f, 1.25f, -0.27f), new Vector3(0.46f, 0.62f, 0.16f), new Vector3(90f, 180f, 0f));
                AddVanillaVisual(board, root.transform, "HopperLeft",  new Vector3(-0.39f, 1.25f, 0f), new Vector3(0.33f, 0.62f, 0.16f), new Vector3(90f, 90f, 0f));
                AddVanillaVisual(board, root.transform, "HopperRight", new Vector3( 0.39f, 1.25f, 0f), new Vector3(0.33f, 0.62f, 0.16f), new Vector3(90f,-90f, 0f));

                // Tapered lower funnel made from inward-leaning boards.
                AddVanillaVisual(board, root.transform, "FunnelLeft",  new Vector3(-0.23f, 0.82f, 0f), new Vector3(0.25f, 0.30f, 0.18f), new Vector3(90f, 90f, -22f));
                AddVanillaVisual(board, root.transform, "FunnelRight", new Vector3( 0.23f, 0.82f, 0f), new Vector3(0.25f, 0.30f, 0.18f), new Vector3(90f,-90f,  22f));
                AddVanillaVisual(beam, root.transform, "Outlet", new Vector3(0f, 0.65f, -0.23f), new Vector3(0.20f, 0.25f, 0.20f), new Vector3(15f, 0f, 90f));

                // One small open tray, visually separated from the reservoir. Real food is
                // spawned here by the automatic hungry-animal release runtime.
                AddVanillaVisual(board, root.transform, "FeedTray", new Vector3(0f, 0.34f, -0.57f), new Vector3(0.48f, 0.18f, 0.28f), new Vector3(4f, 0f, 0f));
                AddVanillaVisual(beam, root.transform, "TrayLipL", new Vector3(-0.42f, 0.41f, -0.57f), new Vector3(0.16f, 0.30f, 0.16f), new Vector3(0f, 0f, 90f));
                AddVanillaVisual(beam, root.transform, "TrayLipR", new Vector3( 0.42f, 0.41f, -0.57f), new Vector3(0.16f, 0.30f, 0.16f), new Vector3(0f, 0f, 90f));
            }
        }

        private static GameObject ResolveVisualPrefab(params string[] names)
        {
            if (names == null) return null;
            for (int n = 0; n < names.Length; n++)
            {
                string wanted = names[n];
                if (string.IsNullOrEmpty(wanted)) continue;
                GameObject znet = TryGetZNetScenePrefab(wanted);
                if (znet != null) return znet;
                try
                {
                    GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
                    for (int i = 0; i < all.Length; i++)
                    {
                        GameObject go = all[i];
                        if (go == null) continue;
                        string normalized = SpeciesRoleRegistry.NormalizePrefabName(go.name ?? string.Empty);
                        if (string.Equals(normalized, wanted, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(go.name, wanted, StringComparison.OrdinalIgnoreCase))
                            return go;
                    }
                }
                catch { }
            }
            return null;
        }

        private static void AddVanillaVisual(GameObject source, Transform parent, string name, Vector3 localPosition, Vector3 localScale, Vector3 localEuler)
        {
            if (source == null || parent == null) return;
            try
            {
                GameObject clone = StaticVisualCloneRuntime.Create(source, parent, name + "_Visual");
                if (clone == null) return;
                clone.name = name;
                clone.transform.localPosition = localPosition;
                clone.transform.localRotation = Quaternion.Euler(localEuler);
                clone.transform.localScale = localScale;
                Renderer[] renderers = clone.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] == null) continue;
                    renderers[i].gameObject.SetActive(true);
                    renderers[i].enabled = true;
                }
            }
            catch { }
        }

        private static Material CloneTintedMaterial(Material source, Color tint)
        {
            if (source == null) return null;
            try
            {
                Material clone = new Material(source);
                clone.name = source.name + "_WorkersFeed";
                if (clone.HasProperty("_Color")) clone.color = tint;
                return clone;
            }
            catch { return source; }
        }

        private static void AddWoodBlock(Transform parent, string name, Vector3 localPosition, Vector3 localScale, Vector3 localEuler, Material material)
        {
            GameObject block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = name;
            block.transform.SetParent(parent, false);
            block.transform.localPosition = localPosition;
            block.transform.localRotation = Quaternion.Euler(localEuler);
            block.transform.localScale = localScale;

            Collider col = block.GetComponent<Collider>();
            if (col != null)
            {
                try { UnityEngine.Object.DestroyImmediate(col); } catch { col.enabled = false; }
            }
            Renderer renderer = block.GetComponent<Renderer>();
            if (renderer != null && material != null) renderer.sharedMaterial = material;
        }

        private bool EnsureInHammer(GameObject prefab, bool trough, PieceTable hammer, object pieceManager, MethodInfo registerPiece)
        {
            if (prefab == null) return false;
            Piece piece = prefab.GetComponent<Piece>() ?? prefab.GetComponentInChildren<Piece>(true);
            if (piece == null)
            {
                _debugLog?.Invoke($"[SmartStorageBridge] {prefab.name} has no Piece component.");
                return false;
            }
            try { piece.m_enabled = true; } catch { }

            LivestockFeedingStationMarker stationMarker = prefab.GetComponent<LivestockFeedingStationMarker>();
            if (stationMarker == null) stationMarker = prefab.AddComponent<LivestockFeedingStationMarker>();
            stationMarker.Configure(trough);

            if (trough && prefab.GetComponent<LivestockFeedSignMarker>() == null)
                prefab.AddComponent<LivestockFeedSignMarker>();

            string prefabName = SpeciesRoleRegistry.NormalizePrefabName(prefab.name ?? string.Empty);
            if (!ContainsPiece(hammer, prefabName))
            {
                try { registerPiece.Invoke(pieceManager, new object[] { prefab, "Hammer", "Furniture" }); }
                catch (Exception ex) { _debugLog?.Invoke($"[SmartStorageBridge] Jotunn registration for {prefabName} failed: {ex.GetType().Name}: {ex.Message}"); }
            }
            if (!ContainsPiece(hammer, prefabName))
            {
                hammer.m_pieces.Add(prefab);
                _debugLog?.Invoke($"[SmartStorageBridge] Forced prefab into live Hammer table: {prefabName}");
            }
            if (ContainsPiece(hammer, prefabName))
            {
                _infoLog?.Invoke($"[SmartStorageBridge] Hammer feeding piece ready: {prefabName}");
                return true;
            }
            return false;
        }

        private void RefreshLoadedTroughMarkers()
        {
            try
            {
                WorkerStorageRuntime.ForEachLoadedContainer(EnsureLoadedFeedingMarker);
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"[SmartStorageBridge] trough marker refresh failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void EnsureLoadedFeedingMarker(Container container)
        {
            if (container == null || !LivestockFeedingRuntime.IsFeedingStation(container)) return;

            bool trough = LivestockFeedingRuntime.IsFeedingTrough(container);
            LivestockFeedingStationMarker station = container.GetComponent<LivestockFeedingStationMarker>()
                ?? container.GetComponentInParent<LivestockFeedingStationMarker>();
            if (station == null) station = container.gameObject.AddComponent<LivestockFeedingStationMarker>();
            station.Configure(trough);

            if (!trough) return;

            LivestockFeedSignMarker marker = container.GetComponent<LivestockFeedSignMarker>()
                ?? container.GetComponentInParent<LivestockFeedSignMarker>();
            if (marker != null) return;

            container.gameObject.AddComponent<LivestockFeedSignMarker>();
            _debugLog?.Invoke($"[SmartStorageBridge] Feeding trough registered as livestock pen marker: {container.gameObject.name}");
        }

        private static GameObject TryGetZNetScenePrefab(string prefabName)
        {
            try
            {
                ZNetScene scene = ZNetScene.instance;
                if (scene == null) return null;
                MethodInfo method = scene.GetType().GetMethod("GetPrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                if (method != null) return method.Invoke(scene, new object[] { prefabName }) as GameObject;
            }
            catch { }
            return null;
        }

        private static bool ContainsPiece(PieceTable table, string prefabName)
        {
            if (table?.m_pieces == null || string.IsNullOrEmpty(prefabName)) return false;
            for (int i = 0; i < table.m_pieces.Count; i++)
            {
                GameObject go = table.m_pieces[i];
                if (go == null) continue;
                string name = SpeciesRoleRegistry.NormalizePrefabName(go.name ?? string.Empty);
                if (string.Equals(name, prefabName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
