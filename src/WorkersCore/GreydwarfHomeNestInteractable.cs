using UnityEngine;

namespace Valheim_Workers
{
    // Networked build-piece interaction. Persistent state lives on the piece ZDO and worker ZDO;
    // this MonoBehaviour only provides Valheim's normal Hoverable/Interactable surface.
    internal sealed class GreydwarfHomeNestInteractable : MonoBehaviour, Hoverable, Interactable
    {
        public string GetHoverText()
        {
            Plugin plugin = Plugin.Instance;
            return plugin != null ? plugin.GetGreydwarfHomeNestHoverText(this) : string.Empty;
        }

        public string GetHoverName() => "Grauzwerg-Wurzelnest";

        public float GetHoverOffset() => 0f;

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold) return false;
            Plugin plugin = Plugin.Instance;
            if (plugin == null) return false;
            bool menuRequest = alt || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (menuRequest) return plugin.TryOpenAssignedWorkerMenuFromHome(GetNetworkView(), transform.position, user, GetHoverName());
            return plugin.TryInteractWithGreydwarfHomeNest(this, user, false);
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item)
        {
            Plugin plugin = Plugin.Instance;
            return plugin != null && plugin.TryUseItemOnGreydwarfHomeNest(this, user, item);
        }

        internal ZNetView GetNetworkView()
        {
            ZNetView nview = GetComponent<ZNetView>();
            if (nview == null) nview = GetComponentInParent<ZNetView>();
            return nview;
        }
    }
}
