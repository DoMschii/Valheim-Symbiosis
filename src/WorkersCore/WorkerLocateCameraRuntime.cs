using System;
using UnityEngine;

namespace Valheim_Workers
{
    internal sealed class WorkerLocateCameraRuntime
    {
        private readonly Action<string> _log;
        private Character _worker;
        private ZDO _workerZdo;
        private Camera _camera;
        private Vector3 _savedPos;
        private Quaternion _savedRot;
        private Behaviour _gameCameraController;
        private bool _gameCameraWasEnabled;
        private Rigidbody _playerBody;
        private RigidbodyConstraints _savedConstraints;
        private bool _playerFrozen;
        private bool _savedCursorVisible;
        private CursorLockMode _savedCursorLock;
        private float _distance = 12f;
        private float _height = 6f;
        private float _yaw;
        private float _startedAt;

        internal bool Active => _worker != null && _workerZdo != null && _camera != null;

        internal WorkerLocateCameraRuntime(Action<string> log) { _log = log; }

        internal bool Begin(Character worker, ZDO workerZdo)
        {
            if (worker == null || workerZdo == null || Player.m_localPlayer == null) return false;
            Shutdown();
            _worker = worker;
            _workerZdo = workerZdo;
            _camera = Camera.main;
            if (_camera == null) { _worker = null; _workerZdo = null; return false; }

            _savedPos = _camera.transform.position;
            _savedRot = _camera.transform.rotation;
            Vector3 flat = _camera.transform.position - worker.transform.position;
            flat.y = 0f;
            if (flat.sqrMagnitude > 0.1f) _yaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
            _startedAt = Time.unscaledTime;

            MonoBehaviour[] behaviours = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour b = behaviours[i];
                if (b == null || !string.Equals(b.GetType().Name, "GameCamera", StringComparison.Ordinal)) continue;
                _gameCameraController = b;
                _gameCameraWasEnabled = b.enabled;
                b.enabled = false;
                break;
            }

            _playerBody = Player.m_localPlayer.GetComponent<Rigidbody>();
            if (_playerBody != null)
            {
                _savedConstraints = _playerBody.constraints;
                _playerBody.constraints = RigidbodyConstraints.FreezeAll;
                _playerFrozen = true;
            }

            _savedCursorVisible = Cursor.visible;
            _savedCursorLock = Cursor.lockState;
            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;
            Snap(true);
            _log?.Invoke("Worker locate camera started.");
            return true;
        }

        internal void Tick()
        {
            if (!Active) return;
            if (_worker == null || _worker.IsDead() || Player.m_localPlayer == null)
            {
                Shutdown();
                return;
            }

            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f) _distance = Mathf.Clamp(_distance - wheel * 1.5f, 4f, 30f);
            if (Input.GetMouseButton(0))
            {
                _yaw += Input.GetAxis("Mouse X") * 3.5f;
                _height = Mathf.Clamp(_height - Input.GetAxis("Mouse Y") * 0.7f, 2f, 18f);
            }
            Snap(false);

            if (Time.unscaledTime - _startedAt > 0.2f && (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1)))
                Shutdown();
        }

        internal void OnGUI()
        {
            if (!Active) return;
            string species = SpeciesRoleRegistry.NormalizePrefabName(_worker.gameObject.name);
            WorkerRole role = (WorkerRole)_workerZdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
            GUIStyle style = new GUIStyle(GUI.skin.box) { fontSize = 14, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            GUI.Box(new Rect((Screen.width - 520f) * 0.5f, 18f, 520f, 62f), $"{species} • {role}\nMausrad = Zoom | Linke Maustaste + Maus = drehen | ESC/Rechtsklick = zurueck", style);
        }

        internal void Shutdown()
        {
            if (_camera != null)
            {
                _camera.transform.position = _savedPos;
                _camera.transform.rotation = _savedRot;
            }
            if (_gameCameraController != null) _gameCameraController.enabled = _gameCameraWasEnabled;
            if (_playerFrozen && _playerBody != null) _playerBody.constraints = _savedConstraints;
            _playerFrozen = false;
            _playerBody = null;
            _gameCameraController = null;
            _camera = null;
            _worker = null;
            _workerZdo = null;
            Cursor.visible = _savedCursorVisible;
            Cursor.lockState = _savedCursorLock;
        }

        private void Snap(bool immediate)
        {
            if (_camera == null || _worker == null) return;
            Vector3 target = _worker.transform.position + Vector3.up * 1.2f;
            Quaternion orbit = Quaternion.Euler(0f, _yaw, 0f);
            Vector3 desired = target + orbit * new Vector3(0f, _height, _distance);
            float t = immediate ? 1f : 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime);
            _camera.transform.position = Vector3.Lerp(_camera.transform.position, desired, t);
            Quaternion look = Quaternion.LookRotation(target - _camera.transform.position, Vector3.up);
            _camera.transform.rotation = Quaternion.Slerp(_camera.transform.rotation, look, t);
        }
    }
}
