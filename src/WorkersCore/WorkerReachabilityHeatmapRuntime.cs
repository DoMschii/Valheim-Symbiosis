using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Debug-only reachability visualization inspired by management-game traversal heatmaps.
    /// It never changes the Workers navigator. The scan is multi-level: every X/Z column can
    /// contain several walkable surfaces (ground floor, stairs, upper floors, roofs, etc.).
    /// Reachability is flood-filled across neighboring surfaces using creature-sized clearance.
    /// </summary>
    internal sealed class WorkerReachabilityHeatmapRuntime
    {
        private sealed class CreatureProfile
        {
            internal readonly string Name;
            internal readonly float Radius;
            internal readonly float Height;
            internal readonly float StepHeight;
            internal readonly float MaxSlope;

            internal CreatureProfile(string name, float radius, float height, float stepHeight, float maxSlope)
            {
                Name = name;
                Radius = radius;
                Height = height;
                StepHeight = stepHeight;
                MaxSlope = maxSlope;
            }
        }

        private sealed class SurfaceNode
        {
            internal int X;
            internal int Z;
            internal Vector3 Ground;
            internal Collider Support;
            internal bool Walkable;
            internal bool Tight;
            internal bool Reachable;
        }

        private static readonly CreatureProfile[] Profiles =
        {
            new CreatureProfile("Greyling", 0.42f, 1.35f, 0.65f, 48f),
            new CreatureProfile("Greydwarf", 0.52f, 1.75f, 0.70f, 47f),
            new CreatureProfile("Greydwarf Brute", 0.78f, 2.20f, 0.80f, 44f),
            new CreatureProfile("Greydwarf Shaman", 0.58f, 1.85f, 0.70f, 46f),
            new CreatureProfile("Draugr", 0.66f, 2.00f, 0.75f, 45f),
            new CreatureProfile("Troll", 1.35f, 4.10f, 1.05f, 40f),
            new CreatureProfile("Stone Golem", 1.22f, 3.20f, 0.95f, 40f),
            new CreatureProfile("Surtling", 0.48f, 1.50f, 0.70f, 48f),
            new CreatureProfile("Lox", 1.70f, 3.10f, 1.05f, 38f),
        };

        private readonly Action<string> _log;
        private readonly Action<string> _status;
        private readonly List<WorkAreaSnapshot> _zones = new List<WorkAreaSnapshot>();
        private readonly Collider[] _overlapBuffer = new Collider[96];
        private readonly List<Character> _workerScratch = new List<Character>();
        private WorkAreaSnapshot _zone;
        private bool _active;
        private bool _dropdownOpen;
        private int _profileIndex = 8;
        private float _cellSize = 2.0f;
        private GameObject _meshObject;
        private Mesh _mesh;
        private Material _material;
        private int _reachableCount;
        private int _tightCount;
        private int _blockedCount;
        private int _surfaceCount;
        private float _lastBuildMs;
        private bool _hPressed;
        private bool _hHoldTriggered;
        private bool _hWasDown;
        private bool _tapStartedActive;
        private float _hPressedAt;
        private const float HoldSeconds = 0.38f;
        private const float ColumnTopOffset = 28f;
        private const float RayDistance = 90f;
        private const float MinSurfaceSeparation = 0.42f;
        private const int MaxSurfacesPerColumn = 8;

        internal bool Active => _active;
        internal bool CapturingInput => _dropdownOpen;
        internal string SelectedProfileName => Profiles[Mathf.Clamp(_profileIndex, 0, Profiles.Length - 1)].Name;

        internal WorkerReachabilityHeatmapRuntime(Action<string> log, Action<string> status)
        {
            _log = log;
            _status = status;
        }

        internal bool BeginNearest(float defaultRadius)
        {
            Player player = Player.m_localPlayer;
            if (player == null) return false;

            WorkFlagRegistry.CollectOwnedSnapshots(player.GetPlayerID(), defaultRadius, player.transform.position, _zones);
            if (_zones.Count == 0)
            {
                _status?.Invoke("Heatmap: keine geladene eigene Workzone gefunden.");
                return false;
            }

            WorkAreaSnapshot nearest = _zones[0];
            for (int i = 1; i < _zones.Count; i++)
                if (_zones[i].Distance < nearest.Distance) nearest = _zones[i];

            _zone = nearest;
            _active = true;
            _dropdownOpen = false;
            BuildHeatmap();
            _status?.Invoke($"Reachability Heatmap: {_zone.DisplayName} / {SelectedProfileName}");
            return true;
        }

        internal void Tick(float defaultRadius)
        {
            bool hDownNow = Input.GetKey(KeyCode.H);
            if (hDownNow && !_hWasDown)
            {
                _hWasDown = true;
                _hPressed = true;
                _hHoldTriggered = false;
                _tapStartedActive = _active;
                _hPressedAt = Time.unscaledTime;

                if (!_active)
                {
                    bool started = BeginNearest(defaultRadius);
                    _status?.Invoke(started
                        ? $"Heatmap AN (H) — {SelectedProfileName}"
                        : "Heatmap H erkannt, aber keine geladene eigene Workzone gefunden.");
                }
            }

            if (_hPressed && hDownNow && !_hHoldTriggered && Time.unscaledTime - _hPressedAt >= HoldSeconds)
            {
                if (!_active) BeginNearest(defaultRadius);
                if (_active)
                {
                    _dropdownOpen = true;
                    _hHoldTriggered = true;
                    _status?.Invoke("Heatmap: Kreaturenauswahl offen (H loslassen zum Schliessen).");
                }
            }

            if (!hDownNow && _hWasDown)
            {
                if (_hHoldTriggered) _dropdownOpen = false;
                else if (_tapStartedActive && _active)
                {
                    Shutdown();
                    _status?.Invoke("Heatmap AUS (H)");
                }

                _hWasDown = false;
                _hPressed = false;
                _hHoldTriggered = false;
                _tapStartedActive = false;
            }

            if (!_active) return;
            if (Player.m_localPlayer == null || _zone.Flag == null)
            {
                Shutdown();
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (_dropdownOpen) _dropdownOpen = false;
                else Shutdown();
            }
        }

        internal void OnGUI()
        {
            if (!_active) return;

            GUIStyle title = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 15 };
            title.normal.textColor = new Color(1f, 0.64f, 0.12f, 1f);
            GUIStyle label = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
            label.normal.textColor = new Color(1f, 0.80f, 0.35f, 1f);

            if (!_dropdownOpen)
            {
                Rect hud = new Rect(18f, Mathf.Max(18f, Screen.height - 128f), 620f, 104f);
                Color previous = GUI.color;
                GUI.color = new Color(0.06f, 0.05f, 0.035f, 0.78f);
                GUI.Box(hud, GUIContent.none);
                GUI.color = previous;
                GUI.Label(new Rect(hud.x + 12f, hud.y + 8f, hud.width - 24f, 22f), $"HEATMAP — {SelectedProfileName}", title);
                GUI.Label(new Rect(hud.x + 12f, hud.y + 31f, hud.width - 24f, 66f),
                    $"{_zone.DisplayName} • Grün erreichbar • Gelb eng • Rot = lokale Blockade/abgetrennte Fläche\nMehrstöckiger Scan: {_surfaceCount} Flächen • Erreichbar {_reachableCount} • Eng {_tightCount} • Rot {_blockedCount}\nH = aus/ein   •   H halten = Kreatur wählen   •   Laufen/Kamera bleiben aktiv", label);
                return;
            }

            const float width = 530f;
            const float height = 475f;
            Rect rect = new Rect(Mathf.Max(12f, (Screen.width - width) * 0.5f), 22f, width, height);
            Color oldColor = GUI.color;
            GUI.color = new Color(0.06f, 0.05f, 0.035f, 0.96f);
            GUI.Box(rect, GUIContent.none);
            GUI.color = oldColor;

            GUILayout.BeginArea(new Rect(rect.x + 14f, rect.y + 10f, rect.width - 28f, rect.height - 20f));
            GUIStyle button = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, fontSize = 13 };

            GUILayout.Label("WORKER REACHABILITY HEATMAP", title);
            GUILayout.Label($"Workzone: {_zone.DisplayName}  •  Radius: {_zone.Radius:0} m", label);
            GUILayout.Label("Kreatur wählen — H loslassen schließt die Auswahl", label);

            GUILayout.BeginVertical(GUI.skin.box);
            for (int i = 0; i < Profiles.Length; i++)
            {
                int candidate = i;
                string prefix = i == _profileIndex ? "✓ " : string.Empty;
                if (GUILayout.Button(prefix + Profiles[i].Name, button, GUILayout.Height(26f)))
                {
                    _profileIndex = candidate;
                    BuildHeatmap();
                    GUIUtility.ExitGUI();
                }
            }
            GUILayout.EndVertical();

            CreatureProfile profile = Profiles[_profileIndex];
            GUILayout.Label($"Clearance: Ø {(profile.Radius * 2f):0.0} m  •  Höhe {profile.Height:0.0} m  •  Stufe {profile.StepHeight:0.0} m", label);
            GUILayout.Label($"Flächen: {_surfaceCount} • Erreichbar: {_reachableCount} • Eng: {_tightCount} • Rot: {_blockedCount} • Scan {_lastBuildMs:0} ms", label);
            GUILayout.EndArea();
        }

        internal void Shutdown()
        {
            _active = false;
            _dropdownOpen = false;
            DestroyMesh();
        }

        private void BuildHeatmap()
        {
            if (!_active || _zone.Flag == null) return;
            float started = Time.realtimeSinceStartup;
            DestroyMesh();

            CreatureProfile profile = Profiles[_profileIndex];
            float radius = Mathf.Clamp(_zone.Radius, 10f, 100f);
            int half = Mathf.CeilToInt(radius / _cellSize);
            int size = half * 2 + 1;
            List<SurfaceNode>[,] columns = new List<SurfaceNode>[size, size];
            Vector3 center = _zone.Center;
            _surfaceCount = 0;

            for (int x = 0; x < size; x++)
            {
                for (int z = 0; z < size; z++)
                {
                    float ox = (x - half) * _cellSize;
                    float oz = (z - half) * _cellSize;
                    if (ox * ox + oz * oz > radius * radius) continue;

                    Vector3 origin = new Vector3(center.x + ox, center.y + ColumnTopOffset, center.z + oz);
                    RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, RayDistance, ~0, QueryTriggerInteraction.Ignore);
                    if (hits == null || hits.Length == 0) continue;
                    Array.Sort(hits, (a, b) => b.point.y.CompareTo(a.point.y));

                    List<SurfaceNode> list = new List<SurfaceNode>(Mathf.Min(hits.Length, MaxSurfacesPerColumn));
                    float lastY = float.PositiveInfinity;
                    for (int h = 0; h < hits.Length && list.Count < MaxSurfacesPerColumn; h++)
                    {
                        RaycastHit hit = hits[h];
                        if (hit.collider == null) continue;
                        if (lastY - hit.point.y < MinSurfaceSeparation) continue;
                        if (hit.normal.y <= 0.05f) continue;

                        float slope = Vector3.Angle(hit.normal, Vector3.up);
                        if (slope > profile.MaxSlope) continue;
                        if (IsIgnoredSurface(hit.collider)) continue;

                        SurfaceNode node = new SurfaceNode
                        {
                            X = x,
                            Z = z,
                            Ground = hit.point,
                            Support = hit.collider
                        };
                        node.Walkable = !HasObstacle(hit.point, hit.collider, profile, 1.00f);
                        node.Tight = node.Walkable && HasObstacle(hit.point, hit.collider, profile, 1.28f);
                        list.Add(node);
                        lastY = hit.point.y;
                        _surfaceCount++;
                    }

                    if (list.Count > 0) columns[x, z] = list;
                }
            }

            Vector3 seedOrigin = FindSeedOrigin(profile);
            SurfaceNode seed = FindBestSeed(columns, size, center, half, seedOrigin, profile);
            if (seed == null)
            {
                _status?.Invoke("Heatmap: kein begehbarer Startpunkt für diese Kreatur gefunden.");
                Render(columns, size, seedOrigin.y);
                _lastBuildMs = (Time.realtimeSinceStartup - started) * 1000f;
                return;
            }

            Queue<SurfaceNode> queue = new Queue<SurfaceNode>();
            seed.Reachable = true;
            queue.Enqueue(seed);
            int[] dx = { 1, -1, 0, 0 };
            int[] dz = { 0, 0, 1, -1 };

            while (queue.Count > 0)
            {
                SurfaceNode current = queue.Dequeue();
                for (int i = 0; i < 4; i++)
                {
                    int nx = current.X + dx[i];
                    int nz = current.Z + dz[i];
                    if (nx < 0 || nz < 0 || nx >= size || nz >= size) continue;
                    List<SurfaceNode> nextColumn = columns[nx, nz];
                    if (nextColumn == null) continue;

                    SurfaceNode best = null;
                    float bestDelta = float.MaxValue;
                    for (int n = 0; n < nextColumn.Count; n++)
                    {
                        SurfaceNode candidate = nextColumn[n];
                        if (!candidate.Walkable || candidate.Reachable) continue;
                        float dy = candidate.Ground.y - current.Ground.y;
                        if (dy > profile.StepHeight) continue;
                        // Dropping is allowed a little farther than stepping up, but not between floors.
                        if (dy < -Mathf.Max(profile.StepHeight * 1.65f, 1.15f)) continue;
                        float abs = Mathf.Abs(dy);
                        if (abs < bestDelta && TransitionHasClearance(current.Ground, candidate.Ground, profile))
                        {
                            best = candidate;
                            bestDelta = abs;
                        }
                    }

                    if (best != null)
                    {
                        best.Reachable = true;
                        queue.Enqueue(best);
                    }
                }
            }

            Render(columns, size, seed.Ground.y);
            _lastBuildMs = (Time.realtimeSinceStartup - started) * 1000f;
            _log?.Invoke($"Reachability heatmap rebuilt: zone={_zone.DisplayName}, profile={profile.Name}, surfaces={_surfaceCount}, reachable={_reachableCount}, tight={_tightCount}, blocked={_blockedCount}, ms={_lastBuildMs:0}");
        }

        private Vector3 FindSeedOrigin(CreatureProfile profile)
        {
            Player player = Player.m_localPlayer;
            long ownerId = player != null ? player.GetPlayerID() : 0L;
            Character best = null;
            float bestDistance = float.MaxValue;

            WorkerFastLookupRuntime.CopyKnownWorkers(_workerScratch);
            for (int i = 0; i < _workerScratch.Count; i++)
            {
                Character character = _workerScratch[i];
                if (character == null || character.IsDead()) continue;
                if (!WorkerFastLookupRuntime.TryGetWorker(character, false, out _, out ZDO zdo)) continue;
                if (ownerId != 0L && zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != ownerId) continue;
                if (!string.Equals(WorkerZdo.ReadWorkAreaId(zdo), _zone.Id, StringComparison.Ordinal)) continue;
                if (!MatchesProfile(character.gameObject.name, profile.Name)) continue;

                float distance = HorizontalDistance(character.transform.position, _zone.Center);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = character;
                }
            }

            if (best != null) return best.transform.position;
            if (player != null && HorizontalDistance(player.transform.position, _zone.Center) <= _zone.Radius + 5f)
                return player.transform.position;
            return _zone.Center;
        }

        private static bool MatchesProfile(string objectName, string profileName)
        {
            string name = CleanName(objectName).ToLowerInvariant();
            string profile = profileName.ToLowerInvariant();
            if (profile == "greydwarf brute") return name.Contains("greydwarf_elite") || name.Contains("greydwarf brute");
            if (profile == "greydwarf shaman") return name.Contains("greydwarf_shaman") || name.Contains("greydwarf shaman");
            if (profile == "stone golem") return name.Contains("stonegolem") || name.Contains("stone_golem") || name.Contains("golem");
            return name.Contains(profile.Replace(" ", string.Empty)) || name.Contains(profile.Replace(" ", "_")) || name.Contains(profile);
        }

        private static string CleanName(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            int clone = value.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase);
            if (clone >= 0) value = value.Substring(0, clone);
            return value.Trim();
        }

        private SurfaceNode FindBestSeed(List<SurfaceNode>[,] columns, int size, Vector3 center, int half, Vector3 origin, CreatureProfile profile)
        {
            int sx = Mathf.Clamp(Mathf.RoundToInt((origin.x - center.x) / _cellSize) + half, 0, size - 1);
            int sz = Mathf.Clamp(Mathf.RoundToInt((origin.z - center.z) / _cellSize) + half, 0, size - 1);
            SurfaceNode seed = FindClosestWalkable(columns[sx, sz], origin.y, profile.Height + 2.0f);
            if (seed != null) return seed;

            for (int r = 1; r <= 5; r++)
            {
                SurfaceNode best = null;
                float bestScore = float.MaxValue;
                for (int x = sx - r; x <= sx + r; x++)
                {
                    for (int z = sz - r; z <= sz + r; z++)
                    {
                        if (x < 0 || z < 0 || x >= size || z >= size) continue;
                        List<SurfaceNode> list = columns[x, z];
                        if (list == null) continue;
                        for (int i = 0; i < list.Count; i++)
                        {
                            SurfaceNode node = list[i];
                            if (!node.Walkable) continue;
                            float score = HorizontalDistance(node.Ground, origin) + Mathf.Abs(node.Ground.y - origin.y) * 1.6f;
                            if (score < bestScore)
                            {
                                bestScore = score;
                                best = node;
                            }
                        }
                    }
                }
                if (best != null) return best;
            }
            return null;
        }

        private static SurfaceNode FindClosestWalkable(List<SurfaceNode> list, float y, float maxVertical)
        {
            if (list == null) return null;
            SurfaceNode best = null;
            float bestDelta = float.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                SurfaceNode node = list[i];
                if (!node.Walkable) continue;
                float delta = Mathf.Abs(node.Ground.y - y);
                if (delta <= maxVertical && delta < bestDelta)
                {
                    best = node;
                    bestDelta = delta;
                }
            }
            return best;
        }

        private bool HasObstacle(Vector3 ground, Collider support, CreatureProfile profile, float clearanceScale)
        {
            float radius = Mathf.Max(0.22f, profile.Radius * clearanceScale);
            float bottom = ground.y + radius + 0.12f;
            float top = Mathf.Max(bottom, ground.y + profile.Height - radius * 0.85f);
            Vector3 p0 = new Vector3(ground.x, bottom, ground.z);
            Vector3 p1 = new Vector3(ground.x, top, ground.z);
            int count = Physics.OverlapCapsuleNonAlloc(p0, p1, radius, _overlapBuffer, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Collider collider = _overlapBuffer[i];
                if (ShouldIgnoreClearanceCollider(collider, support, ground.y)) continue;
                return true;
            }
            return false;
        }

        private bool TransitionHasClearance(Vector3 from, Vector3 to, CreatureProfile profile)
        {
            Vector3 a = from + Vector3.up * Mathf.Max(0.45f, profile.Height * 0.48f);
            Vector3 b = to + Vector3.up * Mathf.Max(0.45f, profile.Height * 0.48f);
            Vector3 delta = b - a;
            float distance = delta.magnitude;
            if (distance < 0.05f) return true;
            float radius = Mathf.Max(0.20f, profile.Radius * 0.82f);
            RaycastHit[] hits = Physics.SphereCastAll(a, radius, delta.normalized, distance, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider == null) continue;
                if (_zone.Flag != null && collider.transform.IsChildOf(_zone.Flag.transform)) continue;
                if (collider.GetComponentInParent<Character>() != null) continue;
                if (collider.GetComponentInParent<ItemDrop>() != null) continue;
                if (collider.GetComponentInParent<Heightmap>() != null) continue;
                if (collider.GetComponentInParent<Terrain>() != null) continue;
                if (collider.bounds.max.y <= Mathf.Max(from.y, to.y) + 0.28f) continue;
                return false;
            }
            return true;
        }

        private bool ShouldIgnoreClearanceCollider(Collider collider, Collider support, float groundY)
        {
            if (collider == null) return true;
            if (collider == support) return true;
            if (_zone.Flag != null && collider.transform.IsChildOf(_zone.Flag.transform)) return true;
            if (collider.GetComponentInParent<Character>() != null) return true;
            if (collider.GetComponentInParent<ItemDrop>() != null) return true;
            if (collider.GetComponentInParent<Heightmap>() != null) return true;
            if (collider.GetComponentInParent<Terrain>() != null) return true;
            // Adjacent floor pieces often overlap the creature capsule slightly. Anything whose
            // top is essentially at foot level is support/floor, not an obstacle.
            if (collider.bounds.max.y <= groundY + 0.28f) return true;
            return false;
        }

        private bool IsIgnoredSurface(Collider collider)
        {
            if (collider == null) return true;
            if (collider.GetComponentInParent<Character>() != null) return true;
            if (collider.GetComponentInParent<ItemDrop>() != null) return true;
            return false;
        }

        private void Render(List<SurfaceNode>[,] columns, int size, float seedY)
        {
            List<Vector3> vertices = new List<Vector3>(size * size * 4);
            List<int> triangles = new List<int>(size * size * 6);
            List<Color> colors = new List<Color>(size * size * 4);
            _reachableCount = 0;
            _tightCount = 0;
            _blockedCount = 0;
            float halfCell = _cellSize * 0.46f;

            for (int x = 0; x < size; x++)
            {
                for (int z = 0; z < size; z++)
                {
                    List<SurfaceNode> list = columns[x, z];
                    if (list == null || list.Count == 0) continue;

                    // Prefer the reachable level in this X/Z column. This prevents an unreachable
                    // roof above a reachable indoor floor from hiding the useful floor result.
                    SurfaceNode display = null;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (!list[i].Reachable) continue;
                        if (display == null || Mathf.Abs(list[i].Ground.y - seedY) < Mathf.Abs(display.Ground.y - seedY))
                            display = list[i];
                    }

                    if (display == null)
                    {
                        // No reachable surface here: show the locally walkable/blocked surface
                        // closest to the active level, rather than blindly showing the roof.
                        float best = float.MaxValue;
                        for (int i = 0; i < list.Count; i++)
                        {
                            float score = Mathf.Abs(list[i].Ground.y - seedY);
                            if (score < best)
                            {
                                best = score;
                                display = list[i];
                            }
                        }
                    }

                    if (display == null) continue;
                    Color color;
                    if (display.Reachable && display.Tight)
                    {
                        color = new Color(1f, 0.78f, 0.08f, 0.42f);
                        _tightCount++;
                    }
                    else if (display.Reachable)
                    {
                        color = new Color(0.15f, 1f, 0.25f, 0.36f);
                        _reachableCount++;
                    }
                    else
                    {
                        color = new Color(1f, 0.12f, 0.10f, 0.24f);
                        _blockedCount++;
                    }

                    Vector3 p = display.Ground + Vector3.up * 0.075f;
                    int start = vertices.Count;
                    vertices.Add(new Vector3(p.x - halfCell, p.y, p.z - halfCell));
                    vertices.Add(new Vector3(p.x + halfCell, p.y, p.z - halfCell));
                    vertices.Add(new Vector3(p.x + halfCell, p.y, p.z + halfCell));
                    vertices.Add(new Vector3(p.x - halfCell, p.y, p.z + halfCell));
                    colors.Add(color); colors.Add(color); colors.Add(color); colors.Add(color);
                    triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 1);
                    triangles.Add(start); triangles.Add(start + 3); triangles.Add(start + 2);
                }
            }

            _meshObject = new GameObject("ValheimWorkers_ReachabilityHeatmap");
            _meshObject.hideFlags = HideFlags.HideAndDontSave;
            MeshFilter filter = _meshObject.AddComponent<MeshFilter>();
            MeshRenderer renderer = _meshObject.AddComponent<MeshRenderer>();
            _mesh = new Mesh { name = "ValheimWorkers_ReachabilityHeatmapMesh" };
            _mesh.indexFormat = vertices.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            _mesh.SetVertices(vertices);
            _mesh.SetTriangles(triangles, 0);
            _mesh.SetColors(colors);
            _mesh.RecalculateBounds();
            filter.sharedMesh = _mesh;

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("UI/Default");
            if (shader != null)
            {
                _material = new Material(shader) { name = "ValheimWorkers_ReachabilityHeatmapMaterial", hideFlags = HideFlags.HideAndDontSave };
                _material.renderQueue = 3100;
                renderer.sharedMaterial = _material;
            }
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private void DestroyMesh()
        {
            if (_meshObject != null) UnityEngine.Object.Destroy(_meshObject);
            _meshObject = null;
            if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
            _mesh = null;
            if (_material != null) UnityEngine.Object.Destroy(_material);
            _material = null;
        }
    }
}
