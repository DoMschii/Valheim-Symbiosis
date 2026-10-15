using System;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Top-down enclosure radius editor plus an optional local build-guide ring for the
    /// Tierpflege sign. The ring is visual only; the authoritative radius remains in the sign ZDO.
    /// </summary>
    internal sealed class LivestockPenBirdViewRuntime
    {
        private readonly Action<string> _log;
        private LivestockFeedSignMarker _sign;
        private Action<LivestockFeedSignMarker, bool, float> _finished;
        private float _radius;
        private float _zoomSize;
        private Camera _camera;
        private Vector3 _savedCameraPosition;
        private Quaternion _savedCameraRotation;
        private bool _savedOrthographic;
        private float _savedOrthographicSize;
        private Behaviour _gameCameraController;
        private bool _gameCameraControllerWasEnabled;
        private Rigidbody _playerBody;
        private RigidbodyConstraints _savedPlayerConstraints;
        private bool _playerFrozen;
        private bool _savedCursorVisible;
        private CursorLockMode _savedCursorLock;
        private bool _cursorCaptured;
        private GameObject _editorRingObject;
        private LineRenderer _editorRing;
        private Material _editorRingMaterial;
        private GameObject _editorWallObject;
        private MeshFilter _editorWallFilter;
        private MeshRenderer _editorWallRenderer;
        private Material _editorWallMaterial;
        private bool _startedInputGuard;
        private float _startedAt;

        private LivestockFeedSignMarker _previewSign;
        private GameObject _previewRingObject;
        private LineRenderer _previewRing;
        private Material _previewRingMaterial;
        private GameObject _previewWallObject;
        private MeshFilter _previewWallFilter;
        private MeshRenderer _previewWallRenderer;
        private Material _previewWallMaterial;
        private float _previewRadius = -1f;
        private Vector3 _previewPosition;
        private float _nextPreviewRefresh;

        internal bool Active => _sign != null;

        internal LivestockPenBirdViewRuntime(Action<string> log)
        {
            _log = log;
        }

        internal bool Begin(LivestockFeedSignMarker sign, float radius, Action<LivestockFeedSignMarker, bool, float> finished)
        {
            if (sign == null || Player.m_localPlayer == null) return false;
            ShutdownEditor();

            _sign = sign;
            _finished = finished;
            _radius = Mathf.Clamp(Mathf.Round(radius), LivestockPenRuntime.MinPenRadius, LivestockPenRuntime.MaxPenRadius);
            _zoomSize = Mathf.Clamp(Mathf.Max(18f, _radius * 1.35f), 14f, 100f);
            _startedAt = Time.unscaledTime;
            _startedInputGuard = true;

            _camera = Camera.main;
            if (_camera == null)
            {
                _sign = null;
                _finished = null;
                return false;
            }

            _savedCameraPosition = _camera.transform.position;
            _savedCameraRotation = _camera.transform.rotation;
            _savedOrthographic = _camera.orthographic;
            _savedOrthographicSize = _camera.orthographicSize;

            MonoBehaviour[] cameraBehaviours = _camera.GetComponentsInParent<MonoBehaviour>(true);
            for (int i = 0; i < cameraBehaviours.Length; i++)
            {
                MonoBehaviour behaviour = cameraBehaviours[i];
                if (behaviour == null || !string.Equals(behaviour.GetType().Name, "GameCamera", StringComparison.Ordinal)) continue;
                _gameCameraController = behaviour;
                _gameCameraControllerWasEnabled = behaviour.enabled;
                behaviour.enabled = false;
                break;
            }
            if (_gameCameraController == null)
            {
                MonoBehaviour[] allBehaviours = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
                for (int i = 0; i < allBehaviours.Length; i++)
                {
                    MonoBehaviour behaviour = allBehaviours[i];
                    if (behaviour == null || !string.Equals(behaviour.GetType().Name, "GameCamera", StringComparison.Ordinal)) continue;
                    _gameCameraController = behaviour;
                    _gameCameraControllerWasEnabled = behaviour.enabled;
                    behaviour.enabled = false;
                    break;
                }
            }

            _savedCursorVisible = Cursor.visible;
            _savedCursorLock = Cursor.lockState;
            _cursorCaptured = true;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            _playerBody = Player.m_localPlayer.GetComponent<Rigidbody>();
            if (_playerBody != null)
            {
                _savedPlayerConstraints = _playerBody.constraints;
                _playerBody.constraints = RigidbodyConstraints.FreezeAll;
                _playerFrozen = true;
            }

            CreateEditorRing();
            UpdateEditorRing();
            SnapCameraTarget(false);
            _log?.Invoke($"Livestock pen bird view started: radius={_radius:0}m sign={sign.name}");
            return true;
        }

        internal void Tick()
        {
            TickPersistentPreview();
            if (!Active) return;
            if (_sign == null || Player.m_localPlayer == null || _camera == null)
            {
                Finish(false);
                return;
            }

            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            if (_startedInputGuard && Time.unscaledTime - _startedAt > 0.18f) _startedInputGuard = false;

            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f)
                _zoomSize = Mathf.Clamp(_zoomSize - wheel * 5f, 12f, 120f);

            SnapCameraTarget(true);
            if (_startedInputGuard) return;
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1)) Finish(false);
        }

        internal void OnGUI()
        {
            if (!Active) return;

            const float width = 520f;
            const float height = 176f;
            Rect rect = new Rect(Mathf.Max(12f, (Screen.width - width) * 0.5f), 24f, width, height);
            Color old = GUI.color;
            GUI.color = new Color(0.08f, 0.06f, 0.035f, 0.96f);
            GUI.Box(rect, GUIContent.none);
            GUI.color = old;

            GUILayout.BeginArea(new Rect(rect.x + 14f, rect.y + 10f, rect.width - 28f, rect.height - 20f));
            GUIStyle title = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 16 };
            title.normal.textColor = new Color(1f, 0.62f, 0.08f, 1f);
            GUIStyle label = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleCenter };
            label.normal.textColor = new Color(1f, 0.72f, 0.18f, 1f);
            GUIStyle button = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, fontSize = 13 };
            button.normal.textColor = new Color(1f, 0.72f, 0.18f, 1f);

            GUILayout.Label($"GEHEGE-RADIUS  •  {_radius:0} m", title);
            GUILayout.Label("Mausrad = Kamera-Zoom   •   Slider = Gehegegroesse", label);
            float nextRadius = Mathf.Round(GUILayout.HorizontalSlider(_radius, LivestockPenRuntime.MinPenRadius, LivestockPenRuntime.MaxPenRadius, GUILayout.Height(22f)));
            if (Mathf.Abs(nextRadius - _radius) >= 1f)
            {
                _radius = nextRadius;
                UpdateEditorRing();
            }
            GUILayout.Label($"Radius: {_radius:0} m   •   Durchmesser: {_radius * 2f:0} m", label);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Speichern", button, GUILayout.Height(30f))) { Finish(true); GUIUtility.ExitGUI(); }
            if (GUILayout.Button("Abbrechen", button, GUILayout.Height(30f))) { Finish(false); GUIUtility.ExitGUI(); }
            GUILayout.EndHorizontal();
            GUILayout.Label("Rechtsklick oder ESC = abbrechen", label);
            GUILayout.EndArea();
        }

        internal bool IsPreviewVisible(LivestockFeedSignMarker sign) => sign != null && _previewSign == sign && _previewRingObject != null;

        internal void TogglePreview(LivestockFeedSignMarker sign)
        {
            if (sign == null) return;
            if (IsPreviewVisible(sign))
            {
                HidePreview();
                return;
            }
            _previewSign = sign;
            _previewRadius = -1f;
            _previewPosition = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            EnsurePreviewRing();
            RefreshPersistentPreview(true);
        }

        internal void NotifyRadiusChanged(LivestockFeedSignMarker sign)
        {
            if (IsPreviewVisible(sign)) RefreshPersistentPreview(true);
        }

        internal void Shutdown()
        {
            ShutdownEditor();
            HidePreview();
        }

        private void TickPersistentPreview()
        {
            if (_previewSign == null || _previewSign.gameObject == null || !_previewSign.gameObject.activeInHierarchy)
            {
                HidePreview();
                return;
            }
            if (Time.unscaledTime < _nextPreviewRefresh) return;
            _nextPreviewRefresh = Time.unscaledTime + 0.35f;
            RefreshPersistentPreview(false);
        }

        private void RefreshPersistentPreview(bool force)
        {
            if (_previewSign == null) return;
            EnsurePreviewRing();
            float radius = LivestockPenRuntime.GetPenRadius(_previewSign);
            Vector3 position = _previewSign.transform.position;
            Vector3 flat = position - _previewPosition; flat.y = 0f;
            if (!force && Mathf.Abs(radius - _previewRadius) < 0.01f && flat.sqrMagnitude < 0.01f) return;
            _previewRadius = radius;
            _previewPosition = position;
            UpdateRing(_previewRing, position, radius, 0.25f);
            UpdateWall(_previewWallFilter, position, radius, 0.12f, 2.35f);
        }

        private void EnsurePreviewRing()
        {
            if (_previewRingObject != null && _previewRing != null) return;
            _previewRingObject = new GameObject("Valheim_Workers_LivestockPenBuildGuide");
            _previewRingObject.hideFlags = HideFlags.HideAndDontSave;
            _previewRing = ConfigureRing(_previewRingObject, out _previewRingMaterial, 0.22f, new Color(0.18f, 0.95f, 0.30f, 0.28f));

            _previewWallObject = new GameObject("Valheim_Workers_LivestockPenBuildGuideWall");
            _previewWallObject.hideFlags = HideFlags.HideAndDontSave;
            ConfigureWall(_previewWallObject, out _previewWallFilter, out _previewWallRenderer, out _previewWallMaterial, new Color(0.10f, 0.95f, 0.24f, 0.13f));
        }

        private void HidePreview()
        {
            if (_previewRingObject != null) UnityEngine.Object.Destroy(_previewRingObject);
            if (_previewRingMaterial != null) UnityEngine.Object.Destroy(_previewRingMaterial);
            if (_previewWallObject != null) UnityEngine.Object.Destroy(_previewWallObject);
            if (_previewWallMaterial != null) UnityEngine.Object.Destroy(_previewWallMaterial);
            _previewRingObject = null;
            _previewRing = null;
            _previewRingMaterial = null;
            _previewWallObject = null;
            _previewWallFilter = null;
            _previewWallRenderer = null;
            _previewWallMaterial = null;
            _previewSign = null;
            _previewRadius = -1f;
        }

        private void Finish(bool confirmed)
        {
            LivestockFeedSignMarker sign = _sign;
            Action<LivestockFeedSignMarker, bool, float> callback = _finished;
            float radius = _radius;
            RestoreLocalState();
            _sign = null;
            _finished = null;
            callback?.Invoke(sign, confirmed, radius);
        }

        private void ShutdownEditor()
        {
            if (!Active && _camera == null && _editorRingObject == null) return;
            RestoreLocalState();
            _sign = null;
            _finished = null;
        }

        private void RestoreLocalState()
        {
            if (_editorRingObject != null) UnityEngine.Object.Destroy(_editorRingObject);
            if (_editorRingMaterial != null) UnityEngine.Object.Destroy(_editorRingMaterial);
            if (_editorWallObject != null) UnityEngine.Object.Destroy(_editorWallObject);
            if (_editorWallMaterial != null) UnityEngine.Object.Destroy(_editorWallMaterial);
            _editorRingObject = null;
            _editorRing = null;
            _editorRingMaterial = null;
            _editorWallObject = null;
            _editorWallFilter = null;
            _editorWallRenderer = null;
            _editorWallMaterial = null;

            if (_playerFrozen && _playerBody != null) _playerBody.constraints = _savedPlayerConstraints;
            _playerFrozen = false;
            _playerBody = null;

            if (_camera != null)
            {
                _camera.transform.position = _savedCameraPosition;
                _camera.transform.rotation = _savedCameraRotation;
                _camera.orthographic = _savedOrthographic;
                _camera.orthographicSize = _savedOrthographicSize;
            }
            if (_gameCameraController != null) _gameCameraController.enabled = _gameCameraControllerWasEnabled;
            _gameCameraController = null;
            _camera = null;

            if (_cursorCaptured)
            {
                Cursor.visible = _savedCursorVisible;
                Cursor.lockState = _savedCursorLock;
                _cursorCaptured = false;
            }
        }

        private void CreateEditorRing()
        {
            _editorRingObject = new GameObject("Valheim_Workers_LivestockPenEditorPreview");
            _editorRingObject.hideFlags = HideFlags.HideAndDontSave;
            _editorRing = ConfigureRing(_editorRingObject, out _editorRingMaterial, 0.28f, new Color(1f, 0.58f, 0.08f, 0.34f));

            _editorWallObject = new GameObject("Valheim_Workers_LivestockPenEditorPreviewWall");
            _editorWallObject.hideFlags = HideFlags.HideAndDontSave;
            ConfigureWall(_editorWallObject, out _editorWallFilter, out _editorWallRenderer, out _editorWallMaterial, new Color(0.10f, 0.95f, 0.24f, 0.12f));
        }

        private static LineRenderer ConfigureRing(GameObject owner, out Material material, float width, Color color)
        {
            LineRenderer ring = owner.AddComponent<LineRenderer>();
            ring.useWorldSpace = true;
            ring.loop = true;
            ring.positionCount = 96;
            ring.startWidth = width;
            ring.endWidth = width;
            ring.numCornerVertices = 2;
            ring.numCapVertices = 2;
            ring.startColor = color;
            ring.endColor = color;
            material = null;
            // Hidden/Internal-Colored is intentionally used here because it exposes the
            // depth/blend state we need for a true build-guide overlay.  The ring should
            // remain readable through roofs, walls, rocks and other scene geometry.
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader != null)
            {
                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                material.color = color;
                if (material.HasProperty("_SrcBlend")) material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                if (material.HasProperty("_DstBlend")) material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                if (material.HasProperty("_Cull")) material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 0);
                if (material.HasProperty("_ZTest")) material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                material.renderQueue = 5000;
                ring.material = material;
            }
            // Keep the LineRenderer itself in the foreground as well. Sorting does not
            // replace ZTest Always, but avoids other transparent effects drawing over it.
            ring.sortingOrder = short.MaxValue;
            return ring;
        }

        private void UpdateEditorRing()
        {
            if (_editorRing == null || _sign == null) return;
            UpdateRing(_editorRing, _sign.transform.position, _radius, 0.24f);
            UpdateWall(_editorWallFilter, _sign.transform.position, _radius, 0.12f, 2.35f);
        }

        private static void UpdateRing(LineRenderer ring, Vector3 center, float radius, float yOffset)
        {
            if (ring == null) return;
            int count = ring.positionCount;
            for (int i = 0; i < count; i++)
            {
                float angle = (i / (float)count) * Mathf.PI * 2f;
                Vector3 point = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                // Query Valheim's terrain height directly instead of raycasting the full scene.
                // A normal raycast hits roofs/floors/rocks first and creates the tall spikes
                // that were visible whenever the enclosure crossed an existing building.
                if (ZoneSystem.instance != null)
                    point.y = ZoneSystem.instance.GetGroundHeight(point) + yOffset;
                else
                    point.y = center.y + yOffset;
                ring.SetPosition(i, point);
            }
        }


        private static void ConfigureWall(GameObject owner, out MeshFilter filter, out MeshRenderer renderer, out Material material, Color color)
        {
            filter = owner.AddComponent<MeshFilter>();
            renderer = owner.AddComponent<MeshRenderer>();
            filter.sharedMesh = new Mesh { name = owner.name + "_Mesh", hideFlags = HideFlags.HideAndDontSave };
            material = null;

            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader != null)
            {
                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                material.color = color;
                if (material.HasProperty("_SrcBlend")) material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                if (material.HasProperty("_DstBlend")) material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                if (material.HasProperty("_Cull")) material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 0);
                if (material.HasProperty("_ZTest")) material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                material.renderQueue = 4999;
                renderer.sharedMaterial = material;
            }
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sortingOrder = short.MaxValue - 1;
        }

        private static void UpdateWall(MeshFilter filter, Vector3 center, float radius, float groundOffset, float wallHeight)
        {
            if (filter == null || filter.sharedMesh == null) return;
            const int segments = 96;
            Vector3[] vertices = new Vector3[segments * 2];
            int[] triangles = new int[segments * 6];

            for (int i = 0; i < segments; i++)
            {
                float angle = (i / (float)segments) * Mathf.PI * 2f;
                Vector3 p = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                float groundY = ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(p) : center.y;
                vertices[i * 2] = new Vector3(p.x, groundY + groundOffset, p.z);
                vertices[i * 2 + 1] = new Vector3(p.x, groundY + wallHeight, p.z);

                int next = (i + 1) % segments;
                int v0 = i * 2;
                int v1 = v0 + 1;
                int v2 = next * 2;
                int v3 = v2 + 1;
                int t = i * 6;
                triangles[t] = v0;
                triangles[t + 1] = v1;
                triangles[t + 2] = v3;
                triangles[t + 3] = v0;
                triangles[t + 4] = v3;
                triangles[t + 5] = v2;
            }

            Mesh mesh = filter.sharedMesh;
            mesh.Clear();
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
        }

        private void SnapCameraTarget(bool smooth)
        {
            if (_camera == null || _sign == null) return;
            Vector3 center = _sign.transform.position;
            float height = Mathf.Clamp(58f + _zoomSize * 0.55f, 64f, 135f);
            Vector3 desiredPosition = center + Vector3.up * height;
            float yaw = _savedCameraRotation.eulerAngles.y;
            Quaternion desiredRotation = Quaternion.Euler(90f, yaw, 0f);
            _camera.orthographic = true;
            float targetZoom = Mathf.Clamp(_zoomSize, 12f, 120f);
            if (!smooth)
            {
                _camera.orthographicSize = targetZoom;
                _camera.transform.position = desiredPosition;
                _camera.transform.rotation = desiredRotation;
                return;
            }
            float t = 1f - Mathf.Exp(-6f * Time.unscaledDeltaTime);
            _camera.orthographicSize = Mathf.Lerp(_camera.orthographicSize, targetZoom, t);
            _camera.transform.position = Vector3.Lerp(_camera.transform.position, desiredPosition, t);
            _camera.transform.rotation = Quaternion.Slerp(_camera.transform.rotation, desiredRotation, t);
        }
    }
}
