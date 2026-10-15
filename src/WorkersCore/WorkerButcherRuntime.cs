using HarmonyLib;

namespace Valheim_Workers
{
    /// <summary>
    /// Recruited Workers use Valheim's real tame flag. The vanilla Butcher Knife is a tame-only
    /// weapon path, so the safest compatibility layer is simply to reassert the Worker tame state
    /// immediately before a KnifeButcher hit is processed. We do not replace Character.Damage,
    /// force-kill the Worker, fabricate drops or touch other weapons.
    /// </summary>
    internal static class WorkerButcherRuntime
    {
        internal static bool IsOwnerButcherHit(Character target, HitData hit)
        {
            if (target == null || hit == null) return false;
            ZNetView nview = target.GetComponent<ZNetView>();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (!WorkerZdo.IsWorker(zdo)) return false;

            Player attacker = hit.GetAttacker() as Player;
            if (attacker == null) return false;
            long owner = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            if (owner != 0L && attacker.GetPlayerID() != owner) return false;

            ItemDrop.ItemData weapon = attacker.GetCurrentWeapon();
            string prefab = weapon?.m_dropPrefab != null ? SpeciesRoleRegistry.NormalizePrefabName(weapon.m_dropPrefab.name) : string.Empty;
            return prefab == "KnifeButcher";
        }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
    internal static class WorkerButcherDamagePatch
    {
        private static void Prefix(Character __instance, HitData hit)
        {
            if (!WorkerButcherRuntime.IsOwnerButcherHit(__instance, hit)) return;

            // Some Worker species are normally untameable monster prefabs, but Character's tame
            // state is network-persisted and is what the vanilla Butcher Knife damage gate needs.
            // Reassert it just before vanilla handles the hit, then leave damage/death/drops to Valheim.
            __instance.SetTamed(true);
        }
    }
}
