using UnityEngine;

namespace Valheim_Workers
{
    internal sealed class SurtlingHomeInteractable : MonoBehaviour, Hoverable, Interactable
    {
        public string GetHoverText()
        {
            Plugin plugin = Plugin.Instance;
            return plugin != null ? plugin.GetSurtlingHomeHoverText(this) : string.Empty;
        }

        public string GetHoverName() => "Surtling-Ruhestätte";
        public float GetHoverOffset() => 0f;

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold) return false;
            Plugin plugin = Plugin.Instance;
            if (plugin == null) return false;

            bool menuRequest = alt || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (menuRequest)
                return plugin.TryOpenAssignedWorkerMenuFromHome(GetNetworkView(), transform.position, user, GetHoverName());

            // v0.5.134: this is a dedicated Worker rest station, not a refillable vanilla campfire.
            // Plain E therefore owns the targeted Home assignment flow just like every other home.
            return plugin.TryInteractWithSurtlingHome(this, user, false);
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
