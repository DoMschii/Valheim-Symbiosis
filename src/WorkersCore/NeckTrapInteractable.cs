using UnityEngine;

namespace Valheim_Workers
{
    internal sealed class NeckTrapInteractable : MonoBehaviour, Hoverable, Interactable
    {
        public string GetHoverName() => "Nixenfalle";

        public string GetHoverText()
        {
            NeckTrapRecruitmentRuntime runtime = GetComponent<NeckTrapRecruitmentRuntime>() ??
                                               GetComponentInParent<NeckTrapRecruitmentRuntime>();
            return runtime != null ? runtime.BuildHover() : "Nixenfalle\nNetzwerkstatus nicht verfügbar";
        }

        public float GetHoverOffset() => 0f;

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold || alt || user == null) return false;
            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer) return false;
            NeckTrapRecruitmentRuntime runtime = GetComponent<NeckTrapRecruitmentRuntime>() ??
                                               GetComponentInParent<NeckTrapRecruitmentRuntime>();
            return runtime != null && runtime.TryInteract(player);
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;
    }
}
