using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// T3 Troll recruitment deliberately converts the ONE natural Troll that lives in a Troll Cave.
    /// Nothing is spawned or cloned: if that Troll dies before/after recruitment, it is genuinely gone.
    ///
    /// Vanilla dungeon interiors live high above the overworld (normally around y=5000). Recruitment
    /// is therefore exposed only while both player and Troll are inside a dungeon interior. A Troll
    /// cannot naturally enter another dungeon type, so this is a cheap interaction-time Troll-Cave gate
    /// without any world scanner or polling loop.
    /// </summary>
    internal static class TrollCaveRecruitmentRuntime
    {
        internal const string ProgressKey = "vw_troll_cave_recruit_progress";
        internal const string RecruiterKey = "vw_troll_cave_recruit_player";
        internal const string CompletedKey = "vw_troll_cave_recruit_complete";
        internal const string DeerStewPrefab = "DeerStew";
        internal const int RequiredMeals = 10;
        private const float InteractDistance = 6.5f;
        private const float DungeonInteriorMinY = 3000f;

        private static readonly MethodInfo ClaimOwnershipMethod = typeof(ZNetView).GetMethod(
            "ClaimOwnership",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);

        internal static bool IsTroll(Character character)
        {
            if (character == null || character is Player) return false;
            return string.Equals(
                SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name),
                "Troll",
                StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryHandleInteract(Character troll, Player player, bool hold)
        {
            if (hold || troll == null || player == null || !IsTroll(troll)) return false;
            if ((troll.transform.position - player.transform.position).sqrMagnitude > InteractDistance * InteractDistance) return false;

            ZNetView view = troll.GetComponent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null || WorkerZdo.IsWorker(zdo)) return false;

            int progress = Mathf.Clamp(zdo.GetInt(ProgressKey, 0), 0, RequiredMeals);
            long playerId = player.GetPlayerID();
            long recruiter = zdo.GetLong(RecruiterKey, 0L);
            if (progress > 0 && recruiter != 0L && recruiter != playerId)
            {
                Notify(player, "Dieser Troll fasst bereits Vertrauen zu einem anderen Spieler.");
                return true;
            }

            if (!IsInsideTrollCave(troll, player))
            {
                if (progress > 0)
                {
                    Notify(player, "Der Troll nimmt Geschenke nur in seiner Hoehle an.");
                    return true;
                }
                return false;
            }

            if (!EnsureOwnership(view))
            {
                Notify(player, "Der Troll kann gerade nicht uebernommen werden (Network Owner fehlt).");
                return true;
            }

            Inventory inventory = player.GetInventory();
            ItemDrop.ItemData stew = FindItem(inventory, DeerStewPrefab);
            if (stew == null)
            {
                Notify(player, $"Der Troll erwartet Hirschragout. ({progress}/{RequiredMeals})");
                return true;
            }

            if (!GreydwarfOfferingCatalog.TryConsumeOne(inventory, stew, Plugin.Instance?.DebugLogPublic))
            {
                Notify(player, "Das Hirschragout konnte nicht uebergeben werden.");
                return true;
            }

            progress = Mathf.Clamp(progress + 1, 0, RequiredMeals);
            zdo.Set(ProgressKey, progress);
            zdo.Set(RecruiterKey, playerId);

            // The first real cooked meal pacifies this specific cave Troll so the remaining T3
            // courtship is possible without cheesing ten E presses between club swings. It is not
            // a Worker yet and has no job/home until all ten meals were earned.
            if (!troll.IsTamed()) troll.SetTamed(true);
            WorkerCombatStanceRuntime.ClearWorkAttackTargets(troll.GetComponent<MonsterAI>());

            if (progress < RequiredMeals)
            {
                Notify(player, $"Hirschragout angenommen. Vertrauen {progress}/{RequiredMeals}");
                return true;
            }

            return FinishRecruitment(troll, player, view, zdo);
        }

        internal static string BuildHover(Character troll)
        {
            if (!IsTroll(troll)) return string.Empty;
            ZNetView view = troll.GetComponent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null || WorkerZdo.IsWorker(zdo)) return string.Empty;

            int progress = Mathf.Clamp(zdo.GetInt(ProgressKey, 0), 0, RequiredMeals);
            Player player = Player.m_localPlayer;
            long recruiter = zdo.GetLong(RecruiterKey, 0L);
            if (progress > 0 && recruiter != 0L && player != null && recruiter != player.GetPlayerID())
                return $"<color=#D7B37A><b>Troll</b></color> • besaenftigt\nVertrauen: <color=#EFC85B>{progress}/{RequiredMeals}</color>\n<color=#9FA8B8>Dieser Troll fasst bereits Vertrauen zu einem anderen Spieler.</color>";

            bool inCave = player != null && IsInsideTrollCave(troll, player);
            if (!inCave && progress <= 0) return string.Empty;

            if (!inCave)
                return $"<color=#D7B37A><b>Troll</b></color> • besaenftigt\nVertrauen: <color=#EFC85B>{progress}/{RequiredMeals}</color>\n<color=#9FA8B8>Weitere Geschenke nur in seiner Trollhoehle.</color>";

            return $"<color=#D7B37A><b>Troll</b></color> • Hoehlenbewohner\n" +
                   $"Vertrauen: <color=#EFC85B>{progress}/{RequiredMeals}</color>\n" +
                   "[<color=yellow><b>E</b></color>] Hirschragout schenken";
        }

        private static bool FinishRecruitment(Character troll, Player player, ZNetView view, ZDO zdo)
        {
            if (!SpeciesRoleRegistry.TryGetRole("Troll", out WorkerRole role))
            {
                Notify(player, "Der Troll hat noch keine Worker-Rolle.");
                return true;
            }

            Vector3 home = troll.transform.position;
            NaturalRecruitmentCompletionRuntime.Complete(
                troll,
                zdo,
                player.GetPlayerID(),
                role,
                "Troll",
                home,
                40f,
                minimumDefensive: true,
                followPlayer: player);
            zdo.Set(ProgressKey, RequiredMeals);
            zdo.Set(RecruiterKey, player.GetPlayerID());
            zdo.Set(CompletedKey, true);

            Notify(player, "Der Troll schliesst sich dir an und folgt dir. Fuehre ihn zu deiner Base und weise ihm dort einen Troll-Lehnplatz zu.");
            Plugin.Log?.LogInfo($"Natural Troll Cave Troll recruited as T3 worker: player={player.GetPlayerID()}, role={role}, pos={home}.");
            return true;
        }

        private static bool IsInsideTrollCave(Character troll, Player player)
        {
            if (troll == null || player == null) return false;

            // Vanilla interiors are physically generated far above the surface. Requiring both
            // actors there avoids converting overworld Trolls standing outside a cave entrance.
            return troll.transform.position.y >= DungeonInteriorMinY &&
                   player.transform.position.y >= DungeonInteriorMinY;
        }

        private static bool EnsureOwnership(ZNetView view)
        {
            if (view == null || !view.IsValid()) return false;
            if (view.IsOwner()) return true;
            try { ClaimOwnershipMethod?.Invoke(view, null); }
            catch { }
            return view.IsOwner();
        }

        private static ItemDrop.ItemData FindItem(Inventory inventory, string prefab)
        {
            if (inventory == null || string.IsNullOrEmpty(prefab)) return null;
            List<ItemDrop.ItemData> items = inventory.GetAllItems();
            if (items == null) return null;
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (item == null || item.m_stack <= 0 || item.m_dropPrefab == null) continue;
                string normalized = SpeciesRoleRegistry.NormalizePrefabName(item.m_dropPrefab.name);
                if (string.Equals(normalized, prefab, StringComparison.OrdinalIgnoreCase)) return item;
            }
            return null;
        }

        private static void Notify(Player player, string text)
        {
            if (player == null || string.IsNullOrEmpty(text)) return;
            try { player.Message(MessageHud.MessageType.Center, text); }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Player), "Interact", typeof(GameObject), typeof(bool), typeof(bool))]
    internal static class TrollCaveRecruitmentInteractPatch
    {
        [HarmonyPriority(Priority.High)]
        private static bool Prefix(Player __instance, GameObject go, bool hold, bool alt)
        {
            if (__instance == null || go == null || __instance != Player.m_localPlayer) return true;
            Character troll = go.GetComponentInParent<Character>();
            if (troll == null || !TrollCaveRecruitmentRuntime.IsTroll(troll)) return true;

            ZNetView view = troll.GetComponent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (WorkerZdo.IsWorker(zdo)) return true;

            return !TrollCaveRecruitmentRuntime.TryHandleInteract(troll, __instance, hold);
        }
    }

    [HarmonyPatch(typeof(Character), "GetHoverText")]
    internal static class TrollCaveRecruitmentHoverPatch
    {
        [HarmonyPriority(Priority.High)]
        private static void Postfix(Character __instance, ref string __result)
        {
            if (__instance == null || !TrollCaveRecruitmentRuntime.IsTroll(__instance)) return;
            ZNetView view = __instance.GetComponent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (WorkerZdo.IsWorker(zdo)) return;

            string recruitment = TrollCaveRecruitmentRuntime.BuildHover(__instance);
            if (!string.IsNullOrEmpty(recruitment)) __result = recruitment;
        }
    }
}
