using UnityEngine;

namespace Valheim_Workers
{
    internal sealed class DraugrHonorShrineInteractable : MonoBehaviour, Hoverable, Interactable
    {
        public string GetHoverName() => "Draugr-Ahnenmal";

        public string GetHoverText()
        {
            return DraugrHonorRecruitmentRuntime.BuildHover(transform.position);
        }

        public float GetHoverOffset() => 0f;

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold || alt) return false;
            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer) return false;
            return DraugrHonorRecruitmentRuntime.TryInteract(transform.position, player);
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item)
        {
            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer || item == null) return false;
            return DraugrHonorRecruitmentRuntime.TryOfferTrophy(transform.position, player, item);
        }
    }
}
