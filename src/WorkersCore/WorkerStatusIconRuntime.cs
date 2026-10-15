using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Draws a tiny real texture next to loaded Workers in world space. This intentionally avoids
    /// Unicode/emoji font support: the hut and red X are pixels in a runtime Texture2D.
    /// </summary>
    internal static class WorkerStatusIconRuntime
    {
        private static Texture2D _homeTexture;
        private static Texture2D _noHomeTexture;

        internal static void Ensure(Character worker)
        {
            if (worker == null || worker is Player) return;
            if (worker.GetComponent<WorkerStatusIconBillboard>() == null)
                worker.gameObject.AddComponent<WorkerStatusIconBillboard>();
        }

        internal static Texture2D Get(bool hasHome)
        {
            if (_homeTexture == null) _homeTexture = Build(false);
            if (_noHomeTexture == null) _noHomeTexture = Build(true);
            return hasHome ? _homeTexture : _noHomeTexture;
        }

        private static Texture2D Build(bool crossed)
        {
            const int size = 20;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.name = crossed ? "Workers_HomeMissing" : "Workers_HomeAssigned";
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            var transparent = new Color32(0, 0, 0, 0);
            var wall = new Color32(226, 196, 112, 255);
            var roof = new Color32(120, 84, 49, 255);
            var door = new Color32(76, 53, 34, 255);
            var red = new Color32(225, 55, 55, 255);
            var pixels = new Color32[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = transparent;

            void Set(int x, int y, Color32 c)
            {
                if (x < 0 || y < 0 || x >= size || y >= size) return;
                pixels[y * size + x] = c;
            }

            // Roof triangle.
            for (int y = 10; y <= 16; y++)
            {
                int half = 16 - y + 1;
                for (int x = 10 - half; x <= 10 + half; x++) Set(x, y, roof);
            }
            // Walls and door.
            for (int y = 3; y <= 10; y++)
                for (int x = 5; x <= 15; x++) Set(x, y, wall);
            for (int y = 3; y <= 7; y++)
                for (int x = 9; x <= 11; x++) Set(x, y, door);

            if (crossed)
            {
                for (int i = 2; i <= 17; i++)
                {
                    Set(i, i, red); Set(i, i + 1, red);
                    Set(19 - i, i, red); Set(19 - i, i + 1, red);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }
    }

    internal sealed class WorkerStatusIconBillboard : MonoBehaviour
    {
        private Character _worker;
        private ZNetView _view;
        private ZDO _zdo;
        private float _headHeight = 2.0f;
        private static int _frameCache = -1;
        private static Player _framePlayer;
        private static Camera _frameCamera;

        private void Awake()
        {
            _worker = GetComponent<Character>() ?? GetComponentInParent<Character>();
            if (_worker != null)
            {
                _view = _worker.GetComponent<ZNetView>();
                _zdo = _view != null && _view.IsValid() ? _view.GetZDO() : null;
            }
            RefreshHeight();
        }

        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || _worker == null || _worker.IsDead()) return;

            // Camera.main performs a tagged-object lookup. With one IMGUI component per loaded
            // Worker that lookup used to repeat for every icon. Cache player/camera once per frame.
            if (_frameCache != Time.frameCount)
            {
                _frameCache = Time.frameCount;
                _framePlayer = Player.m_localPlayer;
                _frameCamera = Camera.main;
            }
            Player player = _framePlayer;
            Camera camera = _frameCamera;
            if (player == null || camera == null) return;

            if (_view == null || !_view.IsValid())
            {
                _view = _worker.GetComponent<ZNetView>();
                _zdo = _view != null && _view.IsValid() ? _view.GetZDO() : null;
            }
            else if (_zdo == null)
            {
                _zdo = _view.GetZDO();
            }
            if (!WorkerZdo.IsWorker(_zdo)) return;

            Vector3 delta = _worker.transform.position - player.transform.position;
            float distanceSqr = delta.sqrMagnitude;
            if (distanceSqr > 324f || distanceSqr < 0.0225f) return;
            float distance = Mathf.Sqrt(distanceSqr);
            Vector3 toWorker = delta / distance;
            if (Vector3.Dot(camera.transform.forward, toWorker) < 0.94f) return;

            // v0.5.356: Worker creature bounds are effectively static. Re-scanning every child
            // Renderer on every loaded Worker every three seconds synchronized all icon components
            // into another periodic allocation/Renderer.bounds spike. Height is resolved once in
            // Awake instead.

            Vector3 world = _worker.transform.position + Vector3.up * _headHeight;
            Vector3 screen = camera.WorldToScreenPoint(world);
            if (screen.z <= 0f) return;

            bool hasHome = WorkerRestRuntime.HasAssignedHome(_zdo);
            Texture2D texture = WorkerStatusIconRuntime.Get(hasHome);
            const float size = 18f;
            // Offset slightly to the right of the projected head/name area.
            Rect rect = new Rect(screen.x + 28f, Screen.height - screen.y - 9f, size, size);
            GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, true);
        }

        private void RefreshHeight()
        {
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0) { _headHeight = 2f; return; }
            bool have = false;
            Bounds bounds = default;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled) continue;
                if (!have) { bounds = renderer.bounds; have = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            _headHeight = have ? Mathf.Clamp(bounds.max.y - transform.position.y + 0.2f, 1.2f, 5f) : 2f;
        }
    }
}
