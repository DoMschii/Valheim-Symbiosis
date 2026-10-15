using UnityEngine;

namespace Valheim_Workers
{
    internal sealed class DrakeEggNestInteractable : MonoBehaviour, Hoverable, Interactable
    {
        public string GetHoverName() => "Drachenbrutnest";

        public string GetHoverText()
        {
            DrakeEggRecruitmentRuntime runtime = GetComponent<DrakeEggRecruitmentRuntime>() ??
                                                GetComponentInParent<DrakeEggRecruitmentRuntime>();
            return runtime != null ? runtime.BuildHover() : "Drachenbrutnest\nNetzwerkstatus nicht verfügbar";
        }

        public float GetHoverOffset() => 0f;

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold || user == null) return false;
            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer) return false;
            DrakeEggRecruitmentRuntime runtime = GetComponent<DrakeEggRecruitmentRuntime>() ??
                                                GetComponentInParent<DrakeEggRecruitmentRuntime>();
            return runtime != null && runtime.TryInteract(player);
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;
    }
}
