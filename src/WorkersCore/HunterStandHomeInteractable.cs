using UnityEngine;

namespace Valheim_Workers
{
    internal sealed class HunterStandHomeInteractable : MonoBehaviour, Hoverable, Interactable
    {
        public string GetHoverText()
        {
            Plugin plugin = Plugin.Instance;
            return plugin != null ? plugin.GetHunterStandHomeHoverText(this) : string.Empty;
        }

        public string GetHoverName() => "Draugr-Jagdplattform";
        public float GetHoverOffset() => 0f;

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold) return false;
            Plugin plugin = Plugin.Instance;
            if (plugin == null) return false;
            bool menuRequest = alt || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (menuRequest) return plugin.TryOpenAssignedWorkerMenuFromHome(GetNetworkView(), transform.position, user, GetHoverName());
            return plugin.TryInteractWithHunterStandHome(this, user, false);
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;

        internal ZNetView GetNetworkView()
        {
            ZNetView nview = GetComponent<ZNetView>();
            if (nview == null) nview = GetComponentInParent<ZNetView>();
            return nview;
        }

        internal HunterStandMarker GetMarker()
        {
            HunterStandMarker marker = GetComponent<HunterStandMarker>();
            if (marker == null) marker = GetComponentInParent<HunterStandMarker>();
            return marker;
        }

        internal bool IsElevatedEnough(out float elevation)
        {
            elevation = 0f;
            HunterStandMarker marker = GetMarker();
            return marker != null && marker.IsValidElevation(out elevation);
        }

        internal Vector3 GetHomePoint()
        {
            HunterStandMarker marker = GetMarker();
            return marker != null && marker.StandPoint != null ? marker.StandPoint.position : transform.position;
        }
    }
}
