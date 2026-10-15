using System;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Temporary top-down editor for a WorkFlag radius. It intentionally owns only the
    /// local camera/UI preview; the authoritative ZDO write happens in Plugin after confirm.
    /// </summary>
    internal sealed class WorkZoneBirdViewRuntime
    {
        private readonly Action<string> _log;
        private WorkFlagInteractable _flag;
        private Action<WorkFlagInteractable, bool, float> _finished;
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
        private GameObject _ringObject;
        private LineRenderer _ring;
        private Material _ringMaterial;
        private bool _startedInputGuard;
        private float _startedAt;

        internal bool Active => _flag != null;

        internal WorkZoneBirdViewRuntime(Action<string> log)
        {
            _log = log;
        }

        internal bool Begin(WorkFlagInteractable flag, float radius, Action<WorkFlagInteractable, bool, float> finished)
        {
            if (flag == null || Player.m_localPlayer == null) return false;
            Shutdown();

            _flag = flag;
            _finished = finished;
            _radius = Mathf.Clamp(Mathf.Round(radius), 10f, 80f);
            _zoomSize = Mathf.Clamp(Mathf.Max(24f, _radius * 1.18f), 18f, 180f);
            _startedAt = Time.unscaledTime;
            _startedInputGuard = true;

            _camera = Camera.main;
            if (_camera == null)
            {
                _flag = null;
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
                if (behaviour == null) continue;
                Type type = behaviour.GetType();
                if (!string.Equals(type.Name, "GameCamera", StringComparison.Ordinal)) continue;
                _gameCameraController = behaviour;
                _gameCameraControllerWasEnabled = behaviour.enabled;
                behaviour.enabled = false;
                break;
            }
            // Some Valheim builds keep GameCamera on a separate camera-root object.
            // This fallback runs only when the editor is opened, never in the normal worker loop.
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

            CreateRing();
            UpdateRing();
            SnapCameraTarget(true);
            _log?.Invoke($"Workzone bird view started: radius={_radius:0}m flag={flag.name}");
            return true;
        }

        internal void Tick()
        {
            if (!Active) return;
            if (_flag == null || Player.m_localPlayer == null || _camera == null)
            {
                Finish(false);
                return;
            }

            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            // The click that opened the mode must never immediately confirm it.
            if (_startedInputGuard && Time.unscaledTime - _startedAt > 0.18f)
                _startedInputGuard = false;

            // Mouse wheel controls only the camera zoom. Radius editing lives exclusively
            // in the on-screen slider so viewing the Workzone can never accidentally change it.
            float wheel = UnityEngine.Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f)
                _zoomSize = Mathf.Clamp(_zoomSize - wheel * 7f, 14f, 220f);

            SnapCameraTarget(true);

            if (_startedInputGuard) return;
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || UnityEngine.Input.GetMouseButtonDown(1))
            {
                Finish(false);
                return;
            }
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

            GUILayout.Label($"WORKZONE-RADIUS  •  {_radius:0} m", title);
            GUILayout.Label("Mausrad = Kamera-Zoom   •   Slider = Arbeitsradius", label);

            float nextRadius = Mathf.Round(GUILayout.HorizontalSlider(_radius, 10f, 80f, GUILayout.Height(22f)));
            if (Mathf.Abs(nextRadius - _radius) >= 1f)
            {
                _radius = nextRadius;
                UpdateRing();
            }
            GUILayout.Label($"Radius: {_radius:0} m   •   Zoom: {_zoomSize:0}", label);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Speichern", button, GUILayout.Height(30f)))
            {
                Finish(true);
                GUIUtility.ExitGUI();
            }
            if (GUILayout.Button("Abbrechen", button, GUILayout.Height(30f)))
            {
                Finish(false);
                GUIUtility.ExitGUI();
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Rechtsklick oder ESC = abbrechen", label);
            GUILayout.EndArea();
        }

        internal void Shutdown()
        {
            if (!Active && _camera == null && _ringObject == null) return;
            RestoreLocalState();
            _flag = null;
            _finished = null;
        }

        private void Finish(bool confirmed)
        {
            WorkFlagInteractable flag = _flag;
            Action<WorkFlagInteractable, bool, float> callback = _finished;
            float radius = _radius;
            RestoreLocalState();
            _flag = null;
            _finished = null;
            callback?.Invoke(flag, confirmed, radius);
        }

        private void RestoreLocalState()
        {
            if (_ringObject != null) UnityEngine.Object.Destroy(_ringObject);
            _ringObject = null;
            _ring = null;
            if (_ringMaterial != null) UnityEngine.Object.Destroy(_ringMaterial);
            _ringMaterial = null;

            if (_playerFrozen && _playerBody != null)
                _playerBody.constraints = _savedPlayerConstraints;
            _playerFrozen = false;
            _playerBody = null;

            if (_camera != null)
            {
                _camera.transform.position = _savedCameraPosition;
                _camera.transform.rotation = _savedCameraRotation;
                _camera.orthographic = _savedOrthographic;
                _camera.orthographicSize = _savedOrthographicSize;
            }
            if (_gameCameraController != null)
                _gameCameraController.enabled = _gameCameraControllerWasEnabled;
            _gameCameraController = null;
            _camera = null;

            if (_cursorCaptured)
            {
                Cursor.visible = _savedCursorVisible;
                Cursor.lockState = _savedCursorLock;
                _cursorCaptured = false;
            }
        }

        private void CreateRing()
        {
            _ringObject = new GameObject("Valheim_Workers_WorkzonePreview");
            _ringObject.hideFlags = HideFlags.HideAndDontSave;
            _ring = _ringObject.AddComponent<LineRenderer>();
            _ring.useWorldSpace = true;
            _ring.loop = true;
            _ring.positionCount = 96;
            _ring.startWidth = 0.28f;
            _ring.endWidth = 0.28f;
            _ring.numCornerVertices = 2;
            _ring.numCapVertices = 2;
            _ring.startColor = new Color(1f, 0.58f, 0.08f, 0.95f);
            _ring.endColor = new Color(1f, 0.58f, 0.08f, 0.95f);

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader != null)
            {
                _ringMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                _ringMaterial.color = new Color(1f, 0.58f, 0.08f, 0.95f);
                _ring.material = _ringMaterial;
            }
        }

        private void UpdateRing()
        {
            if (_ring == null || _flag == null) return;
            Vector3 center = _flag.transform.position;
            int count = _ring.positionCount;
            for (int i = 0; i < count; i++)
            {
                float angle = (i / (float)count) * Mathf.PI * 2f;
                Vector3 point = center + new Vector3(Mathf.Cos(angle) * _radius, 0f, Mathf.Sin(angle) * _radius);
                Vector3 rayOrigin = point + Vector3.up * 90f;
                if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 180f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    point.y = hit.point.y + 0.18f;
                else
                    point.y = center.y + 0.18f;
                _ring.SetPosition(i, point);
            }
        }

        private void SnapCameraTarget(bool smooth)
        {
            if (_camera == null || _flag == null) return;
            Vector3 center = _flag.transform.position;
            float height = Mathf.Clamp(70f + _zoomSize * 0.45f, 78f, 185f);
            Vector3 desiredPosition = center + Vector3.up * height;
            float yaw = _savedCameraRotation.eulerAngles.y;
            Quaternion desiredRotation = Quaternion.Euler(90f, yaw, 0f);

            // Orthographic top-down keeps the radius measurable. Zoom is deliberately
            // independent from the Workzone radius so inspecting the area never edits it.
            _camera.orthographic = true;
            float targetZoom = Mathf.Clamp(_zoomSize, 14f, 220f);
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
