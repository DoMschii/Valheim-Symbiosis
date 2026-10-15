using UnityEngine;

namespace Valheim_Workers
{
    internal sealed class SurtlingSignalFireInteractable : MonoBehaviour, Hoverable, Interactable
    {
        public string GetHoverText()
        {
            ZNetView view = GetNetworkView();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            return zdo != null
                ? SurtlingSignalFireRecruitmentRuntime.BuildHover(this, zdo)
                : "Prächtiges Signalfeuer\nNetzwerkstatus nicht verfügbar";
        }

        public string GetHoverName() => "Prächtiges Signalfeuer";
        public float GetHoverOffset() => 0f;

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold || user == null) return false;
            bool summonRequest = alt || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (summonRequest)
                return SurtlingSignalFireRecruitmentRuntime.TrySummonAdditionalFirekeeper(this, user);
            return SurtlingSignalFireRecruitmentRuntime.TryForwardVanillaInteraction(this, user, hold, false);
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;

        internal ZNetView GetNetworkView()
        {
            ZNetView view = GetComponent<ZNetView>();
            if (view == null) view = GetComponentInParent<ZNetView>();
            return view;
        }
    }
}
