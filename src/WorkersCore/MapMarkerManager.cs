using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace Valheim_Workers
{
    /// <summary>
    /// Client-side projection of recruited workers and their homes onto Valheim's Minimap.
    /// Pins are transient (save=false): worker ZDO state remains authoritative and the player's
    /// normal map/save data is not polluted with permanent mod pins.
    /// </summary>
    internal sealed class MapMarkerManager : IDisposable
    {
        internal const float RefreshSeconds = 5f;
        // v0.5.354: the background scheduler already owns the authoritative persistent Worker
        // registry. Map markers mirror that cheap registry instead of independently reflecting over
        // every ZDO in the world. A short sync interval is therefore inexpensive and hitch-free.
        private const float PersistentWorkerScanSeconds = 30f;

        private readonly Action<string> _debugLog;
        private readonly Dictionary<int, MarkerRecord> _workerPins = new Dictionary<int, MarkerRecord>();
        private readonly Dictionary<int, MarkerRecord> _homePins = new Dictionary<int, MarkerRecord>();
        private readonly Dictionary<int, MarkerRecord> _transportPins = new Dictionary<int, MarkerRecord>();
        private readonly Dictionary<int, MarkerRecord> _outpostAreaPins = new Dictionary<int, MarkerRecord>();
        private readonly List<GameObject> _outpostAreaSegments = new List<GameObject>();
        private float _outpostAreaExpireAt;
        private Vector3 _outpostAreaCenter;
        private float _outpostAreaRadius;
        private int _outpostAreaFocusFrames;
        private bool _areaOverlayApiResolved;
        private bool _areaOverlayApiAvailable;
        private FieldInfo _mapImageLargeField;
        private FieldInfo _pinRootLargeField;
        private FieldInfo _mapOffsetField;
        private FieldInfo _moveInertiaField;
        private FieldInfo _largeZoomField;
        private Vector3 _outpostAreaLastMapOffset = new Vector3(float.NaN, float.NaN, float.NaN);
        private float _outpostAreaLastZoom = float.NaN;
        private MethodInfo _worldToMapPoint;
        private MethodInfo _mapPointToLocalGuiPos;
        private MethodInfo _setMapMode;
        private MethodInfo _centerMap;
        private readonly HashSet<int> _seenWorkers = new HashSet<int>();
        private readonly HashSet<int> _seenTransports = new HashSet<int>();
        private readonly Dictionary<ZDOID, ZDO> _persistentWorkers = new Dictionary<ZDOID, ZDO>();
        private readonly Dictionary<ZDOID, ZDO> _persistentWorkerScan = new Dictionary<ZDOID, ZDO>();
        private readonly HashSet<ZDOID> _backgroundTransportWorkers = new HashSet<ZDOID>();
        private readonly List<BackgroundWorkerScheduler.TransportMapSnapshot> _transportSnapshots = new List<BackgroundWorkerScheduler.TransportMapSnapshot>();
        private readonly List<string> _transportStatusLines = new List<string>();
        private readonly List<Character> _knownWorkerScratch = new List<Character>();

        private Minimap _boundMinimap;
        private float _nextRefresh;
        private float _nextPersistentWorkerScan;
        private bool _apiResolved;
        private bool _apiAvailable;
        private bool _reportedUnavailable;
        private MethodInfo _addPin;
        private MethodInfo _removePin;
        private FieldInfo _pinPositionField;
        private FieldInfo _pinNameField;
        private FieldInfo _pinIconField;
        private readonly Dictionary<string, Sprite> _speciesDotSprites = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Texture2D> _ownedDotTextures = new List<Texture2D>();

        internal int WorkerPinCount => _workerPins.Count;
        internal int HomePinCount => _homePins.Count;
        internal int TransportPinCount => _transportPins.Count;
        internal int OutpostAreaPinCount => _outpostAreaPins.Count;
        internal IReadOnlyList<string> TransportStatusLines => _transportStatusLines;

        internal MapMarkerManager(Action<string> debugLog)
        {
            _debugLog = debugLog;
        }

        internal void Tick(bool showWorkers, bool showHomes, bool showNames)
        {
            Minimap minimap = Minimap.instance;
            TickOutpostAreaOverlay(minimap);

            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + RefreshSeconds;

            if (minimap == null)
            {
                ResetBindingWithoutRemoval();
                return;
            }

            if (Player.m_localPlayer == null)
            {
                // During respawn Player can temporarily be null while the same Minimap survives.
                // Remove our transient pins now so they cannot be duplicated when the player returns.
                if (ReferenceEquals(_boundMinimap, minimap)) ClearAll();
                else ResetBindingWithoutRemoval();
                return;
            }

            if (!ReferenceEquals(_boundMinimap, minimap))
            {
                // Scene/world changes create a new Minimap instance. Old PinData and cached ZDO
                // references belong to the previous world, so discard both before rebuilding.
                _workerPins.Clear();
                _homePins.Clear();
                _transportPins.Clear();
                _outpostAreaPins.Clear();
                DestroyOutpostAreaOverlay();
                _outpostAreaExpireAt = 0f;
                _persistentWorkers.Clear();
                _persistentWorkerScan.Clear();
                _backgroundTransportWorkers.Clear();
                _nextPersistentWorkerScan = 0f;
                _boundMinimap = minimap;
            }

            if (!ResolveApi()) return;

            if (!showWorkers && !showHomes)
            {
                ClearPins(_workerPins);
                ClearPins(_homePins);
                ClearPins(_transportPins);
                return;
            }

            _seenWorkers.Clear();
            _seenTransports.Clear();
            _backgroundTransportWorkers.Clear();
            _transportStatusLines.Clear();

            // The old implementation only iterated Character.GetAllCharacters(), so workers
            // disappeared from the map as soon as Valheim unloaded their GameObject. Keep a
            // persistent ZDO cache instead. ZDO.GetPosition() remains valid map-wide and is also
            // updated by background transport simulation.
            RememberLoadedWorkers();
            RefreshPersistentWorkerCacheIfDue();

            if (showWorkers)
            {
                BackgroundWorkerScheduler.GetTransportMapSnapshots(_transportSnapshots);
                for (int i = 0; i < _transportSnapshots.Count; i++)
                    _backgroundTransportWorkers.Add(_transportSnapshots[i].Id);
            }
            else
            {
                _transportSnapshots.Clear();
            }

            foreach (KeyValuePair<ZDOID, ZDO> pair in _persistentWorkers)
            {
                ZDO zdo = pair.Value;
                if (zdo == null || !WorkerZdo.IsWorker(zdo)) continue;

                ZDOID workerId = pair.Key;
                int key = workerId.GetHashCode();
                _seenWorkers.Add(key);

                Vector3 position = zdo.GetPosition();
                WorkerRole role = (WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
                string species = ResolvePersistentSpecies(zdo);

                if (showWorkers)
                {
                    // Active background transports already get the richer transport/ETA pin below.
                    // Suppress the ordinary worker pin for the same ZDO so the map never shows a
                    // duplicate marker during physical/background handoff.
                    if (_backgroundTransportWorkers.Contains(workerId))
                    {
                        RemoveByKey(_workerPins, key);
                    }
                    else
                    {
                        if (role == WorkerRole.Hauler && WorkerZdo.TryReadWorkArea(zdo, out WorkOrder mapOrder, out _, out _) && mapOrder == WorkOrder.Haul &&
                            WorkerZdo.TryReadTarget(zdo, out _, out _, out Vector3 liveTarget))
                        {
                            float remaining = Vector3.Distance(position, liveTarget);
                            float speed = Mathf.Max(0.1f, BackgroundWorkerScheduler.GetEstimatedTransportSpeed(species));
                            int etaSeconds = Mathf.Max(0, Mathf.CeilToInt(remaining / speed));
                            int etaMin = etaSeconds / 60;
                            int etaSec = etaSeconds % 60;
                            _transportStatusLines.Add($"{species} — PHYSICAL — {remaining:0} m — ETA {etaMin}:{etaSec:00}");
                        }
                        Upsert(_workerPins, key, position, Minimap.PinType.Icon4, string.Empty, species);
                    }
                }
                else
                {
                    RemoveByKey(_workerPins, key);
                }

                if (showHomes)
                {
                    Vector3 home = WorkerZdo.ReadHome(zdo, position);
                    Upsert(_homePins, key, home, Minimap.PinType.Icon3, string.Empty, species + "|home");
                }
                else
                {
                    RemoveByKey(_homePins, key);
                }
            }

            if (showWorkers)
            {
                for (int i = 0; i < _transportSnapshots.Count; i++)
                {
                    BackgroundWorkerScheduler.TransportMapSnapshot snap = _transportSnapshots[i];
                    int key = snap.Id.GetHashCode() ^ unchecked((int)0x80000000);
                    _seenTransports.Add(key);
                    int pct = Mathf.RoundToInt(snap.Progress01 * 100f);
                    int min = snap.EtaSeconds / 60;
                    int sec = snap.EtaSeconds % 60;
                    string species = string.IsNullOrEmpty(snap.Species) ? "Worker" : snap.Species;
                    _transportStatusLines.Add($"{species} — BACKGROUND — {pct}% — ETA {min}:{sec:00}");
                    Upsert(_transportPins, key, snap.Position, Minimap.PinType.Icon4, string.Empty, species);
                }
            }
            else
            {
                ClearPins(_transportPins);
            }

            RemoveUnseen(_workerPins, showWorkers);
            RemoveUnseen(_homePins, showHomes);
            RemoveUnseenTransports();
        }

        private void RememberLoadedWorkers()
        {
            try
            {
                WorkerFastLookupRuntime.CopyKnownWorkers(_knownWorkerScratch);
                for (int i = 0; i < _knownWorkerScratch.Count; i++)
                {
                    Character character = _knownWorkerScratch[i];
                    if (!WorkerFastLookupRuntime.TryGetWorker(character, false, out _, out ZDO zdo)) continue;
                    _persistentWorkers[zdo.m_uid] = zdo;
                }
            }
            catch { }
        }

        private void RefreshPersistentWorkerCacheIfDue()
        {
            if (Time.unscaledTime < _nextPersistentWorkerScan) return;
            _nextPersistentWorkerScan = Time.unscaledTime + PersistentWorkerScanSeconds;

            _persistentWorkerScan.Clear();
            try
            {
                BackgroundWorkerScheduler.ForEachRegisteredWorker(zdo =>
                {
                    if (zdo == null || !WorkerZdo.IsWorker(zdo)) return;
                    _persistentWorkerScan[zdo.m_uid] = zdo;
                });

                // Loaded workers are remembered independently every marker refresh. Merge rather
                // than clearing to avoid a brief pin blink during the scheduler's startup bootstrap.
                foreach (KeyValuePair<ZDOID, ZDO> pair in _persistentWorkerScan)
                    _persistentWorkers[pair.Key] = pair.Value;

                // Remove stale persistent entries only when the scheduler registry is known to be
                // populated. This keeps hot-reload/startup robust without any ZDOMan reflection.
                if (BackgroundWorkerScheduler.RegisteredWorkerCount > 0)
                {
                    var stale = new List<ZDOID>();
                    foreach (KeyValuePair<ZDOID, ZDO> pair in _persistentWorkers)
                    {
                        if (_persistentWorkerScan.ContainsKey(pair.Key)) continue;
                        ZDO zdo = pair.Value;
                        if (zdo == null || !WorkerZdo.IsWorker(zdo)) stale.Add(pair.Key);
                    }
                    for (int i = 0; i < stale.Count; i++) _persistentWorkers.Remove(stale[i]);
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Persistent worker map sync failed; keeping previous pins. {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void HarvestPersistentWorkers(object value, int depth)
        {
            if (value == null || depth > 2) return;
            if (value is ZDO single)
            {
                RememberPersistentWorker(single);
                return;
            }

            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value is ZDO zdo) RememberPersistentWorker(zdo);
                    else if (depth < 2) HarvestPersistentWorkers(entry.Value, depth + 1);
                }
                return;
            }

            if (value is IEnumerable enumerable && !(value is string))
            {
                int guard = 0;
                foreach (object item in enumerable)
                {
                    if (++guard > 200000) break;
                    if (item is ZDO zdo) RememberPersistentWorker(zdo);
                    else if (depth < 2) HarvestPersistentWorkers(item, depth + 1);
                }
            }
        }

        private void RememberPersistentWorker(ZDO zdo)
        {
            if (zdo == null || !WorkerZdo.IsWorker(zdo)) return;
            _persistentWorkerScan[zdo.m_uid] = zdo;
        }

        private static string ResolvePersistentSpecies(ZDO zdo)
        {
            string species = SpeciesRoleRegistry.NormalizePrefabName(zdo?.GetString(WorkerZdo.SpeciesKey, string.Empty) ?? string.Empty);
            return string.IsNullOrEmpty(species) ? "Worker" : species;
        }

        internal void ClearWorkerPins()
        {
            ClearPins(_workerPins);
        }

        internal void ClearHomePins()
        {
            ClearPins(_homePins);
        }

        internal void ClearTransportPins()
        {
            ClearPins(_transportPins);
        }

        internal void ClearOutpostAreaPins()
        {
            ClearPins(_outpostAreaPins);
            DestroyOutpostAreaOverlay();
            _outpostAreaExpireAt = 0f;
            _outpostAreaFocusFrames = 0;
        }

        internal bool IsOutpostAreaMapVisible()
        {
            Minimap minimap = Minimap.instance;
            if (minimap == null || !ResolveAreaOverlayApi()) return false;

            try
            {
                object mapImage = _mapImageLargeField?.GetValue(minimap);
                if (mapImage is Component component)
                    return component.gameObject.activeInHierarchy;
                if (mapImage is GameObject go)
                    return go.activeInHierarchy;
            }
            catch { }
            return false;
        }

        internal void CloseOutpostAreaMap()
        {
            Minimap minimap = Minimap.instance;
            try
            {
                if (minimap != null && ResolveAreaOverlayApi() && _setMapMode != null)
                {
                    Type enumType = _setMapMode.GetParameters()[0].ParameterType;
                    object smallMode = null;
                    string[] names = Enum.GetNames(enumType);
                    for (int i = 0; i < names.Length; i++)
                    {
                        if (string.Equals(names[i], "Small", StringComparison.OrdinalIgnoreCase))
                        {
                            smallMode = Enum.Parse(enumType, names[i]);
                            break;
                        }
                    }
                    if (smallMode == null) smallMode = Enum.ToObject(enumType, 1);
                    _setMapMode.Invoke(minimap, new[] { smallMode });
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Cartography map close failed: {ex.GetType().Name}: {ex.Message}");
            }

            ClearOutpostAreaPins();
        }

        internal bool ShowOutpostArea(Vector3 center, float radius, string displayName)
        {
            Minimap minimap = Minimap.instance;
            if (minimap == null) return false;

            if (!ReferenceEquals(_boundMinimap, minimap))
            {
                _workerPins.Clear();
                _homePins.Clear();
                _transportPins.Clear();
                _outpostAreaPins.Clear();
                DestroyOutpostAreaOverlay();
                _persistentWorkers.Clear();
                _persistentWorkerScan.Clear();
                _backgroundTransportWorkers.Clear();
                _nextPersistentWorkerScan = 0f;
                _boundMinimap = minimap;
            }

            if (!ResolveAreaOverlayApi()) return false;

            // The cartography preview is deliberately a real UI ring, not dozens of map pins.
            // It follows Valheim's own map transform, so zooming/panning keeps the circle aligned.
            ClearPins(_outpostAreaPins);
            DestroyOutpostAreaOverlay();
            _outpostAreaCenter = center;
            _outpostAreaRadius = Mathf.Clamp(radius, 10f, 100f);
            _outpostAreaExpireAt = Time.unscaledTime + 120f;
            _outpostAreaFocusFrames = 4; // SetMapMode can reset the offset during the first frame.
            _outpostAreaLastMapOffset = new Vector3(float.NaN, float.NaN, float.NaN);
            _outpostAreaLastZoom = float.NaN;

            FocusOutpostMap(minimap);
            UpdateOutpostAreaOverlay(minimap);
            return true;
        }

        private void TickOutpostAreaOverlay(Minimap minimap)
        {
            if (_outpostAreaExpireAt <= 0f) return;

            if (Time.unscaledTime >= _outpostAreaExpireAt)
            {
                ClearOutpostAreaPins();
                return;
            }

            if (minimap == null)
            {
                DestroyOutpostAreaOverlay();
                return;
            }

            if (!ReferenceEquals(_boundMinimap, minimap))
            {
                // Let the normal 5-second world/scene binding path reset all persistent caches.
                // The overlay must never take ownership of _boundMinimap by itself.
                DestroyOutpostAreaOverlay();
                return;
            }

            if (!ResolveAreaOverlayApi()) return;

            bool forceRefresh = false;
            if (_outpostAreaFocusFrames > 0)
            {
                FocusOutpostMap(minimap);
                _outpostAreaFocusFrames--;
                forceRefresh = true;
            }

            if (forceRefresh || OutpostMapTransformChanged(minimap))
                UpdateOutpostAreaOverlay(minimap);
        }

        private bool ResolveAreaOverlayApi()
        {
            if (_areaOverlayApiResolved) return _areaOverlayApiAvailable;
            _areaOverlayApiResolved = true;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = typeof(Minimap);
            _mapImageLargeField = type.GetField("m_mapImageLarge", flags);
            _pinRootLargeField = type.GetField("m_pinRootLarge", flags);
            _mapOffsetField = type.GetField("m_mapOffset", flags);
            _moveInertiaField = type.GetField("m_moveInertia", flags);
            _largeZoomField = type.GetField("m_largeZoom", flags);

            MethodInfo[] methods = type.GetMethods(flags);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                ParameterInfo[] p = method.GetParameters();
                if (method.Name == "WorldToMapPoint" && p.Length == 3 && p[0].ParameterType == typeof(Vector3))
                    _worldToMapPoint = method;
                else if (method.Name == "MapPointToLocalGuiPos" && p.Length == 3 && p[0].ParameterType == typeof(float) && p[1].ParameterType == typeof(float))
                    _mapPointToLocalGuiPos = method;
                else if (method.Name == "SetMapMode" && p.Length == 1 && p[0].ParameterType.IsEnum)
                    _setMapMode = method;
                else if (method.Name == "CenterMap" && p.Length == 1 && p[0].ParameterType == typeof(Vector3))
                    _centerMap = method;
            }

            _areaOverlayApiAvailable = _mapImageLargeField != null && _pinRootLargeField != null &&
                                       _worldToMapPoint != null && _mapPointToLocalGuiPos != null;
            if (!_areaOverlayApiAvailable)
                _debugLog?.Invoke("Cartography workzone circle API unavailable; map preview disabled.");
            return _areaOverlayApiAvailable;
        }

        private bool OutpostMapTransformChanged(Minimap minimap)
        {
            if (minimap == null) return false;
            try
            {
                Vector3 offset = _mapOffsetField != null ? (Vector3)_mapOffsetField.GetValue(minimap) : Vector3.zero;
                float zoom = _largeZoomField != null ? Convert.ToSingle(_largeZoomField.GetValue(minimap)) : 0f;
                if (float.IsNaN(_outpostAreaLastZoom) ||
                    float.IsNaN(_outpostAreaLastMapOffset.x) ||
                    (offset - _outpostAreaLastMapOffset).sqrMagnitude > 0.0001f ||
                    Mathf.Abs(zoom - _outpostAreaLastZoom) > 0.00001f)
                {
                    _outpostAreaLastMapOffset = offset;
                    _outpostAreaLastZoom = zoom;
                    return true;
                }
            }
            catch
            {
                return true;
            }
            return false;
        }

        private void FocusOutpostMap(Minimap minimap)
        {
            if (minimap == null) return;
            try
            {
                if (_setMapMode != null)
                {
                    Type enumType = _setMapMode.GetParameters()[0].ParameterType;
                    _setMapMode.Invoke(minimap, new[] { Enum.ToObject(enumType, 2) });
                }

                // CenterMap is the vanilla path. The direct offset write is an additional safeguard
                // because opening the large map can overwrite the offset during the same frame.
                _centerMap?.Invoke(minimap, new object[] { _outpostAreaCenter });
                if (_mapOffsetField != null && Player.m_localPlayer != null)
                    _mapOffsetField.SetValue(minimap, _outpostAreaCenter - Player.m_localPlayer.transform.position);
                if (_moveInertiaField != null)
                    _moveInertiaField.SetValue(minimap, Vector3.zero);
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Outpost map focus failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void UpdateOutpostAreaOverlay(Minimap minimap)
        {
            if (minimap == null || !ResolveAreaOverlayApi()) return;

            try
            {
                RectTransform parent = _pinRootLargeField.GetValue(minimap) as RectTransform;
                object mapImage = _mapImageLargeField.GetValue(minimap);
                if (parent == null || mapImage == null) return;

                const int segmentCount = 96;
                EnsureOutpostAreaSegments(parent, segmentCount);

                Vector2 first = default(Vector2);
                Vector2 previous = default(Vector2);
                bool havePrevious = false;
                for (int i = 0; i <= segmentCount; i++)
                {
                    int sample = i == segmentCount ? 0 : i;
                    float angle = (sample / (float)segmentCount) * Mathf.PI * 2f;
                    Vector3 world = _outpostAreaCenter + new Vector3(Mathf.Cos(angle) * _outpostAreaRadius, 0f, Mathf.Sin(angle) * _outpostAreaRadius);
                    if (!TryGetMapLocalPosition(minimap, mapImage, world, out Vector2 current))
                    {
                        HideOutpostAreaSegments();
                        return;
                    }

                    if (i == 0)
                    {
                        first = current;
                        previous = current;
                        havePrevious = true;
                        continue;
                    }

                    Vector2 end = (i == segmentCount) ? first : current;
                    SetOutpostAreaSegment(_outpostAreaSegments[i - 1], previous, end);
                    previous = current;
                }

                if (havePrevious)
                {
                    for (int i = 0; i < _outpostAreaSegments.Count; i++)
                        if (_outpostAreaSegments[i] != null) _outpostAreaSegments[i].SetActive(true);
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Cartography workzone circle update failed: {ex.GetType().Name}: {ex.Message}");
                HideOutpostAreaSegments();
            }
        }

        private bool TryGetMapLocalPosition(Minimap minimap, object mapImage, Vector3 world, out Vector2 local)
        {
            local = default(Vector2);
            try
            {
                object[] worldArgs = { world, 0f, 0f };
                _worldToMapPoint.Invoke(minimap, worldArgs);
                float mx = Convert.ToSingle(worldArgs[1]);
                float my = Convert.ToSingle(worldArgs[2]);
                object result = _mapPointToLocalGuiPos.Invoke(minimap, new object[] { mx, my, mapImage });
                if (result is Vector2 v)
                {
                    local = v;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private void EnsureOutpostAreaSegments(RectTransform parent, int count)
        {
            while (_outpostAreaSegments.Count < count)
            {
                GameObject go = new GameObject("VW_WorkzoneMapRing", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                go.hideFlags = HideFlags.HideAndDontSave;
                go.transform.SetParent(parent, false);
                Image image = go.GetComponent<Image>();
                image.raycastTarget = false;
                image.color = new Color(1f, 0.58f, 0.08f, 0.95f);
                _outpostAreaSegments.Add(go);
            }

            for (int i = 0; i < _outpostAreaSegments.Count; i++)
            {
                GameObject go = _outpostAreaSegments[i];
                if (go == null) continue;
                if (go.transform.parent != parent) go.transform.SetParent(parent, false);
                go.SetActive(i < count);
            }
        }

        private static void SetOutpostAreaSegment(GameObject go, Vector2 a, Vector2 b)
        {
            if (go == null) return;
            RectTransform rt = go.GetComponent<RectTransform>();
            if (rt == null) return;
            Vector2 delta = b - a;
            float length = delta.magnitude;
            if (length < 0.05f)
            {
                go.SetActive(false);
                return;
            }

            // MapPointToLocalGuiPos returns coordinates in the map image's bottom-left based
            // local space (0..width / 0..height), exactly like vanilla pin positioning.
            // Using centered anchors adds half the map size again and pushes the ring toward
            // the upper-right corner. Match the vanilla pin coordinate origin instead.
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = (a + b) * 0.5f;
            rt.sizeDelta = new Vector2(length + 1.5f, 2.8f);
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            go.SetActive(true);
        }

        private void HideOutpostAreaSegments()
        {
            for (int i = 0; i < _outpostAreaSegments.Count; i++)
                if (_outpostAreaSegments[i] != null) _outpostAreaSegments[i].SetActive(false);
        }

        private void DestroyOutpostAreaOverlay()
        {
            for (int i = 0; i < _outpostAreaSegments.Count; i++)
            {
                GameObject go = _outpostAreaSegments[i];
                if (go != null) UnityEngine.Object.Destroy(go);
            }
            _outpostAreaSegments.Clear();
            _outpostAreaLastMapOffset = new Vector3(float.NaN, float.NaN, float.NaN);
            _outpostAreaLastZoom = float.NaN;
        }

        internal void ClearAll()
        {
            ClearWorkerPins();
            ClearHomePins();
            ClearTransportPins();
            ClearOutpostAreaPins();
            _outpostAreaExpireAt = 0f;
        }

        public void Dispose()
        {
            ClearAll();
            ResetBindingWithoutRemoval();
            foreach (Sprite sprite in _speciesDotSprites.Values) if (sprite != null) UnityEngine.Object.Destroy(sprite);
            for (int i = 0; i < _ownedDotTextures.Count; i++) if (_ownedDotTextures[i] != null) UnityEngine.Object.Destroy(_ownedDotTextures[i]);
            _speciesDotSprites.Clear();
            _ownedDotTextures.Clear();
        }

        private void Upsert(Dictionary<int, MarkerRecord> collection, int key, Vector3 position, Minimap.PinType type, string label, string visualKey = null)
        {
            if (!collection.TryGetValue(key, out MarkerRecord record) || record.Pin == null)
            {
                Minimap.PinData pin = AddPin(position, type, label);
                if (pin != null)
                {
                    collection[key] = new MarkerRecord(pin, position, label, visualKey);
                    ApplyDotVisual(pin, visualKey);
                }
                return;
            }

            bool positionChanged = (record.LastPosition - position).sqrMagnitude > 0.25f;
            bool labelChanged = !string.Equals(record.LastLabel, label, StringComparison.Ordinal);
            bool visualChanged = !string.Equals(record.LastVisualKey, visualKey, StringComparison.OrdinalIgnoreCase);
            if (!positionChanged && !labelChanged && !visualChanged) return;

            bool updatedInPlace = true;
            try
            {
                if (positionChanged)
                {
                    if (_pinPositionField != null)
                        _pinPositionField.SetValue(record.Pin, position);
                    else
                        updatedInPlace = false;
                }

                if (labelChanged)
                {
                    if (_pinNameField != null)
                        _pinNameField.SetValue(record.Pin, label);
                    else
                        updatedInPlace = false;
                }
                if (visualChanged)
                    ApplyDotVisual(record.Pin, visualKey);
            }
            catch (Exception ex)
            {
                updatedInPlace = false;
                _debugLog?.Invoke($"Map marker in-place update failed; recreating pin. {ex.GetType().Name}: {ex.Message}");
            }

            if (!updatedInPlace)
            {
                RemovePin(record.Pin);
                Minimap.PinData replacement = AddPin(position, type, label);
                if (replacement == null)
                {
                    collection.Remove(key);
                    return;
                }
                record.Pin = replacement;
                ApplyDotVisual(record.Pin, visualKey);
            }

            record.LastPosition = position;
            record.LastLabel = label;
            record.LastVisualKey = visualKey;
        }

        private void RemoveUnseen(Dictionary<int, MarkerRecord> collection, bool featureEnabled)
        {
            if (!featureEnabled)
            {
                ClearPins(collection);
                return;
            }

            if (collection.Count == 0) return;
            List<int> stale = null;
            foreach (KeyValuePair<int, MarkerRecord> pair in collection)
            {
                if (_seenWorkers.Contains(pair.Key)) continue;
                if (stale == null) stale = new List<int>();
                stale.Add(pair.Key);
            }

            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++)
                RemoveByKey(collection, stale[i]);
        }

        private void RemoveUnseenTransports()
        {
            if (_transportPins.Count == 0) return;
            List<int> stale = null;
            foreach (KeyValuePair<int, MarkerRecord> pair in _transportPins)
            {
                if (_seenTransports.Contains(pair.Key)) continue;
                if (stale == null) stale = new List<int>();
                stale.Add(pair.Key);
            }
            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++) RemoveByKey(_transportPins, stale[i]);
        }

        private void RemoveByKey(Dictionary<int, MarkerRecord> collection, int key)
        {
            if (!collection.TryGetValue(key, out MarkerRecord record)) return;
            RemovePin(record.Pin);
            collection.Remove(key);
        }

        private void ClearPins(Dictionary<int, MarkerRecord> collection)
        {
            if (_boundMinimap != null && _apiAvailable)
            {
                foreach (MarkerRecord record in collection.Values)
                    RemovePin(record.Pin);
            }
            collection.Clear();
        }

        private Minimap.PinData AddPin(Vector3 position, Minimap.PinType type, string label)
        {
            if (_boundMinimap == null || _addPin == null) return null;

            try
            {
                ParameterInfo[] parameters = _addPin.GetParameters();
                object[] args;
                if (parameters.Length == 7)
                {
                    object author = CreateDefaultValue(parameters[6].ParameterType);
                    args = new object[] { position, type, label, false, false, 0L, author };
                }
                else
                {
                    args = new object[] { position, type, label, false, false, 0L };
                }

                return _addPin.Invoke(_boundMinimap, args) as Minimap.PinData;
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Map marker AddPin failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static object CreateDefaultValue(Type type)
        {
            if (type == typeof(string)) return string.Empty;
            try
            {
                return Activator.CreateInstance(type);
            }
            catch
            {
                ConstructorInfo stringCtor = type.GetConstructor(new[] { typeof(string) });
                if (stringCtor != null)
                {
                    try { return stringCtor.Invoke(new object[] { string.Empty }); }
                    catch { }
                }
                return null;
            }
        }

        private void RemovePin(Minimap.PinData pin)
        {
            if (pin == null || _boundMinimap == null || _removePin == null) return;
            try
            {
                _removePin.Invoke(_boundMinimap, new object[] { pin });
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Map marker RemovePin failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private bool ResolveApi()
        {
            if (_apiResolved) return _apiAvailable;
            _apiResolved = true;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo[] methods = typeof(Minimap).GetMethods(flags);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name == "AddPin")
                {
                    ParameterInfo[] p = method.GetParameters();
                    if ((p.Length == 6 || p.Length == 7) &&
                        p[0].ParameterType == typeof(Vector3) &&
                        p[1].ParameterType == typeof(Minimap.PinType) &&
                        p[2].ParameterType == typeof(string) &&
                        p[3].ParameterType == typeof(bool) &&
                        p[4].ParameterType == typeof(bool) &&
                        p[5].ParameterType == typeof(long) &&
                        typeof(Minimap.PinData).IsAssignableFrom(method.ReturnType))
                    {
                        _addPin = method;
                        break;
                    }
                }
            }

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != "RemovePin") continue;
                ParameterInfo[] p = method.GetParameters();
                if (p.Length == 1 && p[0].ParameterType == typeof(Minimap.PinData))
                {
                    _removePin = method;
                    break;
                }
            }

            // These are only an optimization. If a future Valheim build renames either field,
            // markers are recreated instead of spamming failed reflection every refresh.
            _pinPositionField = typeof(Minimap.PinData).GetField("m_pos", flags);
            _pinNameField = typeof(Minimap.PinData).GetField("m_name", flags);
            _pinIconField = typeof(Minimap.PinData).GetField("m_icon", flags);

            _apiAvailable = _addPin != null && _removePin != null;
            if (!_apiAvailable && !_reportedUnavailable)
            {
                _reportedUnavailable = true;
                _debugLog?.Invoke("Minimap marker API not available; worker map markers disabled for this runtime.");
            }
            else if (_apiAvailable)
            {
                _debugLog?.Invoke($"Minimap marker API resolved: AddPin args={_addPin.GetParameters().Length}, inPlacePosition={_pinPositionField != null}, inPlaceName={_pinNameField != null}");
            }

            return _apiAvailable;
        }

        private void ApplyDotVisual(Minimap.PinData pin, string visualKey)
        {
            if (pin == null || _pinIconField == null || string.IsNullOrEmpty(visualKey)) return;
            try
            {
                Sprite sprite = GetSpeciesDotSprite(visualKey);
                if (sprite != null) _pinIconField.SetValue(pin, sprite);
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Map worker dot visual failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private Sprite GetSpeciesDotSprite(string visualKey)
        {
            if (_speciesDotSprites.TryGetValue(visualKey, out Sprite cached) && cached != null) return cached;
            bool home = visualKey.EndsWith("|home", StringComparison.OrdinalIgnoreCase);
            string species = home ? visualKey.Substring(0, visualKey.Length - 5) : visualKey;
            Color c = SpeciesDotColor(species);
            if (home) c.a = 0.55f;
            const int size = 18;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.name = "VW_WorkerDot_" + species + (home ? "_Home" : string.Empty);
            tex.hideFlags = HideFlags.HideAndDontSave;
            Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
            float radius = home ? 5.2f : 6.8f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), center);
                if (d <= radius - 1f) tex.SetPixel(x, y, c);
                else if (d <= radius) tex.SetPixel(x, y, new Color(0.05f, 0.05f, 0.05f, c.a));
                else tex.SetPixel(x, y, Color.clear);
            }
            tex.Apply(false, true);
            Sprite sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 18f);
            sprite.name = tex.name;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            _ownedDotTextures.Add(tex);
            _speciesDotSprites[visualKey] = sprite;
            return sprite;
        }

        private static Color SpeciesDotColor(string species)
        {
            species = SpeciesRoleRegistry.NormalizePrefabName(species ?? string.Empty);
            if (species.IndexOf("Greyling", StringComparison.OrdinalIgnoreCase) >= 0) return new Color(0.72f, 0.90f, 0.48f, 1f);
            if (species.IndexOf("Greydwarf_Shaman", StringComparison.OrdinalIgnoreCase) >= 0) return new Color(0.22f, 0.95f, 0.38f, 1f);
            if (species.IndexOf("Greydwarf_Elite", StringComparison.OrdinalIgnoreCase) >= 0) return new Color(0.95f, 0.43f, 0.18f, 1f);
            if (species.IndexOf("Greydwarf", StringComparison.OrdinalIgnoreCase) >= 0) return new Color(0.55f, 0.78f, 0.28f, 1f);
            if (species.IndexOf("Draugr", StringComparison.OrdinalIgnoreCase) >= 0) return new Color(0.35f, 0.82f, 0.72f, 1f);
            if (species.IndexOf("Troll", StringComparison.OrdinalIgnoreCase) >= 0) return new Color(0.25f, 0.48f, 0.96f, 1f);
            if (species.IndexOf("StoneGolem", StringComparison.OrdinalIgnoreCase) >= 0) return new Color(0.75f, 0.88f, 1.00f, 1f);
            if (species.IndexOf("Neck", StringComparison.OrdinalIgnoreCase) >= 0) return new Color(0.25f, 0.95f, 0.85f, 1f);
            if (species.IndexOf("Wolf", StringComparison.OrdinalIgnoreCase) >= 0) return new Color(0.90f, 0.90f, 0.96f, 1f);
            if (species.IndexOf("Lox", StringComparison.OrdinalIgnoreCase) >= 0) return new Color(0.70f, 0.44f, 0.22f, 1f);
            if (species.IndexOf("Drake", StringComparison.OrdinalIgnoreCase) >= 0) return new Color(0.42f, 0.78f, 1.00f, 1f);
            if (species.IndexOf("Surtling", StringComparison.OrdinalIgnoreCase) >= 0) return new Color(1.00f, 0.24f, 0.08f, 1f);
            if (species.IndexOf("Fuling_Shaman", StringComparison.OrdinalIgnoreCase) >= 0) return new Color(0.86f, 0.35f, 0.98f, 1f);
            if (species.IndexOf("Fuling_Berserker", StringComparison.OrdinalIgnoreCase) >= 0) return new Color(0.92f, 0.68f, 0.18f, 1f);
            if (species.IndexOf("Fuling", StringComparison.OrdinalIgnoreCase) >= 0) return new Color(0.95f, 0.86f, 0.20f, 1f);
            if (species.IndexOf("Dverger", StringComparison.OrdinalIgnoreCase) >= 0) return new Color(0.78f, 0.45f, 1.00f, 1f);
            return new Color(1f, 1f, 1f, 1f);
        }

        private void ResetBindingWithoutRemoval()
        {
            _workerPins.Clear();
            _homePins.Clear();
            _transportPins.Clear();
            _outpostAreaPins.Clear();
            DestroyOutpostAreaOverlay();
            _outpostAreaExpireAt = 0f;
            _outpostAreaFocusFrames = 0;
            _areaOverlayApiResolved = false;
            _areaOverlayApiAvailable = false;
            _seenWorkers.Clear();
            _persistentWorkers.Clear();
            _persistentWorkerScan.Clear();
            _backgroundTransportWorkers.Clear();
            _nextPersistentWorkerScan = 0f;
            _boundMinimap = null;
        }

        private sealed class MarkerRecord
        {
            internal Minimap.PinData Pin;
            internal Vector3 LastPosition;
            internal string LastLabel;
            internal string LastVisualKey;

            internal MarkerRecord(Minimap.PinData pin, Vector3 lastPosition, string lastLabel, string lastVisualKey = null)
            {
                Pin = pin;
                LastPosition = lastPosition;
                LastLabel = lastLabel;
                LastVisualKey = lastVisualKey;
            }
        }
    }
}
