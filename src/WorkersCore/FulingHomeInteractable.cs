using UnityEngine;

namespace Valheim_Workers
{
    internal sealed class FulingHomeInteractable : MonoBehaviour, Hoverable, Interactable
    {
        [SerializeField] private string _displayName = "Fuling-Bett";

        internal void ConfigureDisplayName(string displayName)
        {
            _displayName = string.IsNullOrWhiteSpace(displayName) ? "Fuling-Ruhestätte" : displayName;
        }

        internal string DisplayName => string.IsNullOrWhiteSpace(_displayName) ? "Fuling-Ruhestätte" : _displayName;

        public string GetHoverText()
        {
            Plugin plugin = Plugin.Instance;
            return plugin != null ? plugin.GetFulingHomeHoverText(this) : string.Empty;
        }

        public string GetHoverName() => DisplayName;
        public float GetHoverOffset() => 0f;

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold) return false;
            Plugin plugin = Plugin.Instance;
            if (plugin == null) return false;
            bool menuRequest = alt || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (menuRequest) return plugin.TryOpenAssignedWorkerMenuFromHome(GetNetworkView(), transform.position, user, GetHoverName());
            return plugin.TryInteractWithFulingHome(this, user, false);
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;

        internal ZNetView GetNetworkView()
        {
            ZNetView nview = GetComponent<ZNetView>();
            if (nview == null) nview = GetComponentInParent<ZNetView>();
            return nview;
        }
    }
}
