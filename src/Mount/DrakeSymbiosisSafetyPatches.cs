using HarmonyLib;

namespace Valheim_Workers
{
    [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
    internal static class DrakeSymbiosisPlayerDamagePatch
    {
        [HarmonyPriority(Priority.First + 1000)]
        private static bool Prefix(Character __instance, HitData hit)
        {
            // Troll native melee HitData can arrive on the input frame. Hold it until the visible
            // fist/log impact so floating damage and world reaction match the animation.
            if (DrakeMountRuntime.TryDeferTrollOutgoingHit(__instance, hit))
                return false;

            // Outgoing Werewolf claw/combo hits are star-scaled before the target resolves armour
            // and resistances. This is a no-op for every other form/attacker.
            DrakeMountRuntime.ApplyOutgoingSymbiosisDamage(__instance, hit);

            // In necklace symbiosis the temporary Drake is the player's visible collision/attack
            // body, not a second creature with its own HP. Route its incoming damage through the
            // real Player so player HP, armour, resistances, food and death remain authoritative.
            if (DrakeMountRuntime.TryForwardTemporaryAvatarDamage(__instance, hit))
                return false;

            // The hidden local Player occupies the same area as the Drake and could otherwise be
            // hit a second time by the same melee/AoE attack. Only forwarded avatar damage may
            // pass this guard while symbiosis is active.
            if (DrakeMountRuntime.BlockLocalPlayerDamage && __instance == Player.m_localPlayer)
                return false;
            return true;
        }
    }


    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem), new System.Type[] { typeof(ItemDrop.ItemData), typeof(bool) })]
    internal static class DrakeSymbiosisEquipmentLockPatch
    {
        [HarmonyPriority(Priority.First + 500)]
        private static bool Prefix(Humanoid __instance, ref bool __result)
        {
            if (!DrakeMountRuntime.Active || __instance != Player.m_localPlayer) return true;

            // The hidden human loadout is frozen while the player is the Drake. This prevents
            // number keys/inventory clicks from equipping swords, tools, bows, shields, etc.
            __result = false;
            return false;
        }
    }



    [HarmonyPatch(typeof(Player), "UseHotbarItem")]
    internal static class DrakeSymbiosisHotbarLockPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(Player __instance)
        {
            if (!DrakeMountRuntime.Active || __instance != Player.m_localPlayer) return true;

            // v0.7.11: Let the vanilla hotbar-use path continue while transformed. Equipment is
            // still blocked by DrakeSymbiosisEquipmentLockPatch and human attacks are blocked by
            // DrakeSymbiosisHumanAttackLockPatch, but consumables need to reach Humanoid.UseItem
            // so the real Player can eat food/drink mead while the creature body is active.
            // Bound necklaces are consumed earlier by DrakeForm's high-priority hotbar patch.
            return true;
        }
    }

    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UseItem), new[] { typeof(Inventory), typeof(ItemDrop.ItemData), typeof(bool) })]
    internal static class DrakeSymbiosisUseItemLockPatch
    {
        [HarmonyPriority(Priority.First + 400)]
        private static bool Prefix(Humanoid __instance, object[] __args)
        {
            if (!DrakeMountRuntime.Active || __instance != Player.m_localPlayer) return true;

            ItemDrop.ItemData item = null;
            if (__args != null)
            {
                for (int i = 0; i < __args.Length; ++i)
                {
                    ItemDrop.ItemData candidate = __args[i] as ItemDrop.ItemData;
                    if (candidate != null) { item = candidate; break; }
                }
            }

            // The real Player remains authoritative for food/HP/stamina during symbiosis, so vanilla
            // consumables must still be usable from either the hotbar or inventory. This includes food
            // and meads/potions. Equipment/tools/weapons remain frozen while transformed.
            if (item != null && item.m_shared != null &&
                item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable)
                return true;

            // Bound necklaces are intercepted by DrakeForm at a higher Harmony priority before this
            // guard executes, including the use-again action that leaves symbiosis.
            return false;
        }
    }

    [HarmonyPatch(typeof(Humanoid), "StartAttack", new System.Type[] { typeof(Character), typeof(bool) })]
    internal static class DrakeSymbiosisHumanAttackLockPatch
    {
        [HarmonyPriority(Priority.First + 1000)]
        private static bool Prefix(Humanoid __instance, ref bool __result)
        {
            if (!DrakeMountRuntime.Active || __instance != Player.m_localPlayer) return true;

            // Player weapons (especially a loaded crossbow) use Humanoid.StartAttack, not the
            // Character base implementation. The Drake owns mouse attacks during symbiosis, so
            // the hidden human must never create an Attack object/projectile in parallel.
            __result = false;
            return false;
        }
    }

    // v0.1.46: Do NOT patch Player.UpdateAttack here. Valheim 1.0.17 no longer
    // exposes that method on Player, and an undefined Harmony target aborts PatchAll,
    // which would disable the real Humanoid.StartAttack/Equip/UseItem locks as well.
    // Human weapon attacks are blocked at Humanoid.StartAttack and any pre-existing
    // Attack object is cancelled when symbiosis begins.

    // v1.9.1: the hidden real Player follows the Serpent body underwater so inventory,
    // food, HP, ownership and streaming remain authoritative. Vanilla therefore thinks the
    // human is actively swimming, which suppresses normal stamina regeneration (and may drain
    // swim stamina) even though locomotion is owned by the Serpent. During Serpent symbiosis
    // only, report the hidden local Player as not swimming. This lets Player.UpdateStats keep
    // Valheim's normal stamina regeneration, including food/status-effect modifiers, while the
    // visible Serpent continues to use the custom water motor. Activation still checks the real
    // swimming state before symbiosis becomes active, so the water-only entry rule is unchanged.
    [HarmonyPatch(typeof(Character), "IsSwimming")]
    internal static class SerpentHiddenPlayerSwimmingStatePatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Character __instance, ref bool __result)
        {
            Player player = __instance as Player;
            if (player == null || player != Player.m_localPlayer) return;
            if (!DrakeMountRuntime.IsLocalSerpentSymbiosis(player)) return;
            __result = false;
        }
    }

    [HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
    internal static class DrakeSymbiosisGameCameraOwnershipPatch
    {
        private static bool Prefix()
        {
            // While Drake symbiosis is active the mount owns Camera.main exclusively.
            // Letting vanilla GameCamera move the transform first and correcting it later in LateUpdate/render
            // produces severe motion-vector/TAA jitter in first person. Skip the vanilla camera update for
            // the duration of symbiosis; normal camera ownership resumes automatically on dismount.
            return !DrakeMountRuntime.Active;
        }
    }


    [HarmonyPatch(typeof(MonsterAI), "UpdateAI")]
    internal static class DrakeFullPossessionAiPatch
    {
        private static bool Prefix(MonsterAI __instance, float dt)
        {
            if (__instance == null) return true;
            Character character = __instance.GetComponent<Character>();
            if (!DrakeMountRuntime.IsMounted(character)) return true;

            // Full possession means exactly that: the controlled Drake has no autonomous
            // MonsterAI while the player is inside it. Manual steering is driven from
            // MountPlugin.FixedUpdate at physics cadence; tying it to MonsterAI.UpdateAI caused
            // visible stepwise turns whenever the AI update cadence was throttled.
            return false;
        }
    }

    [HarmonyPatch(typeof(BaseAI), "SetAlerted", new System.Type[] { typeof(bool) })]
    internal static class DrakeFullPossessionAlertPatch
    {
        private static bool Prefix(BaseAI __instance, bool alert)
        {
            if (!alert || __instance == null) return true;
            Character character = __instance.GetComponent<Character>();

            // Enemy proximity or another vanilla system must never re-alert a possessed Drake.
            // SetAlerted(false) remains allowed so takeover cleanup can explicitly clear any
            // alert state that existed before the transformation started.
            return !DrakeMountRuntime.IsMounted(character);
        }
    }



    // v0.7.2: do not Harmony-patch Character.FixedUpdate. Valheim 1.0.17 does not expose
    // that target, and an undefined target aborts Mount PatchAll. Werewolf post-native movement
    // authority now lives on WerewolfLocomotionAuthority, a late-execution component attached
    // only to the temporary Fenring symbiosis body.


}
