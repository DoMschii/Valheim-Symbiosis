using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// T3 Stone Golems are recruited through a combat trial plus one crystal binding.
    /// The player must perfect-parry ten attacks from the same natural Golem. Any normal/failed
    /// block, an unblocked hit from that Golem, or attacking the Golem before the trial is complete
    /// breaks the streak. At 10/10 the exact Golem becomes peaceful and accepts one Crystal to
    /// complete recruitment. No replacement creature is spawned.
    ///
    /// No scheduler/world scan is added. Everything runs only from vanilla combat/interaction hooks.
    /// </summary>
    internal static class StoneGolemRecruitmentRuntime
    {
        // Keep the old crystal key so v0.5.406 test saves can be migrated safely.
        internal const string ProgressKey = "vw_stonegolem_recruit_progress";
        internal const string ParryProgressKey = "vw_stonegolem_parry_progress";
        internal const string RecruiterKey = "vw_stonegolem_recruit_player";
        internal const string RespectCompleteKey = "vw_stonegolem_parry_complete";
        internal const string CompletedKey = "vw_stonegolem_recruit_complete";
        internal const string LegacyMigratedKey = "vw_stonegolem_parry_migrated_407";
        internal const string CrystalPrefab = "Crystal";
        internal const int RequiredParries = 10;
        internal const int RequiredCrystals = 1;
        internal const float PerfectParryWindow = 0.25f;
        private const float InteractDistance = 7.5f;

        // A successful perfect block can still flow through Character.Damage with fully/mostly
        // mitigated damage. Remember that exact combat frame so the damage hook cannot reset the
        // streak we just incremented.
        private static int _lastPerfectParryFrame = -1000;
        private static int _lastPerfectParryAttackerId;

        private static readonly MethodInfo ClaimOwnershipMethod = typeof(ZNetView).GetMethod(
            "ClaimOwnership",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);

        private static readonly MethodInfo GetCurrentBlockerMethod = AccessTools.Method(typeof(Humanoid), "GetCurrentBlocker");

        internal static bool IsStoneGolem(Character character)
        {
            if (character == null || character is Player) return false;
            string species = SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name);
            return string.Equals(species, "StoneGolem", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(species, "IceGolem", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryHandleInteract(Character golem, Player player, bool hold)
        {
            if (hold || golem == null || player == null || !IsStoneGolem(golem)) return false;
            if ((golem.transform.position - player.transform.position).sqrMagnitude > InteractDistance * InteractDistance) return false;

            ZNetView view = golem.GetComponent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null || WorkerZdo.IsWorker(zdo)) return false;

            if (!EnsureOwnership(view))
            {
                Notify(player, "Der Steingolem kann gerade nicht gebunden werden (Network Owner fehlt).");
                return true;
            }

            MigrateLegacyCrystalTrial(golem, zdo);

            int parries = Mathf.Clamp(zdo.GetInt(ParryProgressKey, 0), 0, RequiredParries);
            bool respectComplete = zdo.GetBool(RespectCompleteKey, false) || parries >= RequiredParries;
            long recruiter = zdo.GetLong(RecruiterKey, 0L);
            long playerId = player.GetPlayerID();
            if ((parries > 0 || respectComplete) && recruiter != 0L && recruiter != playerId)
            {
                Notify(player, "Dieser Steingolem prueft bereits die Staerke eines anderen Spielers.");
                return true;
            }

            if (!respectComplete)
            {
                Notify(player, $"Pruefung der Staerke: {parries}/{RequiredParries} perfekte Paraden. Erst danach akzeptiert der Golem die Kristallbindung.");
                return true;
            }

            Inventory inventory = player.GetInventory();
            ItemDrop.ItemData crystal = FindItem(inventory, CrystalPrefab);
            if (crystal == null)
            {
                Notify(player, "Der Steingolem erkennt deine Staerke an. Jetzt fehlt 1 Kristall fuer die endgueltige Bindung.");
                return true;
            }

            if (!GreydwarfOfferingCatalog.TryConsumeOne(inventory, crystal, Plugin.Instance?.DebugLogPublic))
            {
                Notify(player, "Der Kristall konnte nicht fuer die Bindung geopfert werden.");
                return true;
            }

            zdo.Set(ProgressKey, RequiredCrystals);
            zdo.Set(RecruiterKey, player.GetPlayerID());
            return FinishRecruitment(golem, player, zdo);
        }

        internal static string BuildHover(Character golem)
        {
            if (!IsStoneGolem(golem)) return string.Empty;
            ZNetView view = golem.GetComponent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null || WorkerZdo.IsWorker(zdo)) return string.Empty;

            int parries = Mathf.Clamp(zdo.GetInt(ParryProgressKey, 0), 0, RequiredParries);
            bool respectComplete = zdo.GetBool(RespectCompleteKey, false) || parries >= RequiredParries;
            Player local = Player.m_localPlayer;
            long recruiter = zdo.GetLong(RecruiterKey, 0L);
            if ((parries > 0 || respectComplete) && recruiter != 0L && local != null && recruiter != local.GetPlayerID())
            {
                return "<color=#9ED6FF><b>Steingolem</b></color> • Pruefung der Staerke\n" +
                       $"Perfekte Paraden: <color=#D8F3FF>{parries}/{RequiredParries}</color>\n" +
                       "<color=#BBBBBB>Dieser Golem prueft bereits die Staerke eines anderen Spielers.</color>";
            }

            if (!respectComplete)
            {
                return "<color=#9ED6FF><b>Steingolem</b></color> • Pruefung der Staerke\n" +
                       $"Perfekte Paraden: <color=#D8F3FF>{parries}/{RequiredParries}</color>\n" +
                       "<color=#BBBBBB>10 Angriffe perfekt parieren. Treffer, Fehlblock oder eigener Angriff bricht die Serie.</color>";
            }

            return "<color=#9ED6FF><b>Steingolem</b></color> • Respekt erworben\n" +
                   $"Perfekte Paraden: <color=#7CFF8A>{RequiredParries}/{RequiredParries}</color>\n" +
                   "[<color=yellow><b>E</b></color>] 1 Kristall zur Bindung opfern";
        }

        internal static void HandleBlockResult(Player player, Character attacker, bool blockSucceeded, float blockTimer)
        {
            if (player == null || player != Player.m_localPlayer || attacker == null || !IsStoneGolem(attacker)) return;

            ZNetView view = attacker.GetComponent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null || WorkerZdo.IsWorker(zdo) || zdo.GetBool(RespectCompleteKey, false)) return;
            if (!EnsureOwnership(view)) return;

            MigrateLegacyCrystalTrial(attacker, zdo);

            bool perfect = blockSucceeded && blockTimer >= 0f && blockTimer < PerfectParryWindow && HasTimedParryCapability(player);
            if (!perfect)
            {
                ResetStreak(zdo, player, "Die Parade war nicht perfekt. Die Golem-Pruefung beginnt wieder bei 0/10.");
                return;
            }

            long playerId = player.GetPlayerID();
            int progress = Mathf.Clamp(zdo.GetInt(ParryProgressKey, 0), 0, RequiredParries);
            long recruiter = zdo.GetLong(RecruiterKey, 0L);
            if (progress > 0 && recruiter != 0L && recruiter != playerId)
            {
                Notify(player, "Dieser Steingolem befindet sich bereits in der Pruefung eines anderen Spielers.");
                return;
            }

            progress = Mathf.Clamp(progress + 1, 0, RequiredParries);
            zdo.Set(ParryProgressKey, progress);
            zdo.Set(RecruiterKey, playerId);
            _lastPerfectParryFrame = Time.frameCount;
            _lastPerfectParryAttackerId = attacker.GetInstanceID();

            if (progress < RequiredParries)
            {
                Notify(player, $"Perfekte Parade! Golem-Respekt {progress}/{RequiredParries}");
                return;
            }

            zdo.Set(RespectCompleteKey, true);
            if (!attacker.IsTamed()) attacker.SetTamed(true);
            WorkerCombatStanceRuntime.ClearWorkAttackTargets(attacker.GetComponent<MonsterAI>());
            Notify(player, "Pruefung bestanden: 10/10. Der Steingolem respektiert dich. Gib ihm jetzt 1 Kristall zur Bindung.");
            Plugin.Log?.LogInfo($"Stone Golem combat trial completed by player={playerId}, pos={attacker.transform.position}.");
        }

        internal static void HandlePlayerDamaged(Player player, HitData hit)
        {
            if (player == null || player != Player.m_localPlayer || hit == null) return;

            Character attacker;
            try { attacker = hit.GetAttacker(); }
            catch { return; }
            if (attacker == null || !IsStoneGolem(attacker)) return;

            if (Time.frameCount == _lastPerfectParryFrame && attacker.GetInstanceID() == _lastPerfectParryAttackerId)
                return;

            ZNetView view = attacker.GetComponent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null || WorkerZdo.IsWorker(zdo) || zdo.GetBool(RespectCompleteKey, false)) return;

            int progress = Mathf.Clamp(zdo.GetInt(ParryProgressKey, 0), 0, RequiredParries);
            long recruiter = zdo.GetLong(RecruiterKey, 0L);
            if (progress > 0 && recruiter != 0L && recruiter != player.GetPlayerID()) return;

            ResetStreak(zdo, player, "Der Steingolem hat dich getroffen. Die Pruefung beginnt wieder bei 0/10.");
        }

        internal static void HandleGolemDamagedByPlayer(Character golem, HitData hit)
        {
            if (golem == null || hit == null || !IsStoneGolem(golem)) return;
            ZNetView view = golem.GetComponent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null || WorkerZdo.IsWorker(zdo) || zdo.GetBool(RespectCompleteKey, false)) return;

            Character attacker;
            try { attacker = hit.GetAttacker(); }
            catch { return; }
            Player player = attacker as Player;
            if (player == null || player != Player.m_localPlayer) return;

            int progress = Mathf.Clamp(zdo.GetInt(ParryProgressKey, 0), 0, RequiredParries);
            long recruiter = zdo.GetLong(RecruiterKey, 0L);
            if (progress > 0 && recruiter != 0L && recruiter != player.GetPlayerID()) return;

            ResetStreak(zdo, player, "Du hast den Steingolem angegriffen. Die Pruefung beginnt wieder bei 0/10.");
        }

        private static void ResetStreak(ZDO zdo, Player player, string message)
        {
            if (zdo == null || zdo.GetBool(RespectCompleteKey, false)) return;
            int old = Mathf.Clamp(zdo.GetInt(ParryProgressKey, 0), 0, RequiredParries);
            if (old <= 0) return;
            zdo.Set(ParryProgressKey, 0);
            // Broken streak = no active claimant. The next successful first parry may start a new trial.
            zdo.Set(RecruiterKey, 0L);
            if (player != null) Notify(player, message);
        }

        private static void MigrateLegacyCrystalTrial(Character golem, ZDO zdo)
        {
            if (golem == null || zdo == null || WorkerZdo.IsWorker(zdo) || zdo.GetBool(LegacyMigratedKey, false)) return;

            int legacy = zdo.GetInt(ProgressKey, 0);
            if (legacy > 0 && !zdo.GetBool(CompletedKey, false))
            {
                // v0.5.406 pacified the Golem on the first of eight crystals. The new design is a
                // combat test, so a half-finished old ritual must become hostile again or the ten
                // required parries would be impossible to perform.
                zdo.Set(ProgressKey, 0);
                zdo.Set(ParryProgressKey, 0);
                zdo.Set(RespectCompleteKey, false);
                try { if (golem.IsTamed()) golem.SetTamed(false); } catch { }
            }
            zdo.Set(LegacyMigratedKey, true);
        }

        private static bool FinishRecruitment(Character golem, Player player, ZDO zdo)
        {
            string species = SpeciesRoleRegistry.NormalizePrefabName(golem.gameObject.name);
            if (!SpeciesRoleRegistry.TryGetRole(species, out WorkerRole role))
            {
                species = "StoneGolem";
                if (!SpeciesRoleRegistry.TryGetRole(species, out role))
                {
                    Notify(player, "Der Steingolem hat noch keine Worker-Rolle.");
                    return true;
                }
            }

            Vector3 home = golem.transform.position;
            NaturalRecruitmentCompletionRuntime.Complete(
                golem,
                zdo,
                player.GetPlayerID(),
                role,
                species,
                home,
                40f,
                minimumDefensive: true,
                followPlayer: player);
            zdo.Set(ParryProgressKey, RequiredParries);
            zdo.Set(RespectCompleteKey, true);
            zdo.Set(ProgressKey, RequiredCrystals);
            zdo.Set(RecruiterKey, player.GetPlayerID());
            zdo.Set(CompletedKey, true);

            Notify(player, "Die Kristallbindung ist vollstaendig. Der Steingolem folgt dir. Fuehre ihn zur Base und weise ihm dort ein Golem-Steinlager zu.");
            Plugin.Log?.LogInfo($"Natural Stone Golem recruited after 10 perfect parries + 1 crystal: player={player.GetPlayerID()}, role={role}, pos={home}.");
            return true;
        }

        private static bool HasTimedParryCapability(Humanoid humanoid)
        {
            if (humanoid == null) return false;
            if (GetCurrentBlockerMethod == null) return true; // defensive fallback across game builds
            try
            {
                ItemDrop.ItemData blocker = GetCurrentBlockerMethod.Invoke(humanoid, null) as ItemDrop.ItemData;
                return blocker != null && blocker.m_shared != null && blocker.m_shared.m_timedBlockBonus > 1f;
            }
            catch
            {
                return true;
            }
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
            var items = inventory.GetAllItems();
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
    internal static class StoneGolemRecruitmentInteractPatch
    {
        [HarmonyPriority(Priority.High)]
        private static bool Prefix(Player __instance, GameObject go, bool hold, bool alt)
        {
            if (__instance == null || go == null || __instance != Player.m_localPlayer) return true;
            Character golem = go.GetComponentInParent<Character>();
            if (golem == null || !StoneGolemRecruitmentRuntime.IsStoneGolem(golem)) return true;

            ZNetView view = golem.GetComponent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (WorkerZdo.IsWorker(zdo)) return true;

            return !StoneGolemRecruitmentRuntime.TryHandleInteract(golem, __instance, hold);
        }
    }

    [HarmonyPatch(typeof(Character), "GetHoverText")]
    internal static class StoneGolemRecruitmentHoverPatch
    {
        [HarmonyPriority(Priority.High)]
        private static void Postfix(Character __instance, ref string __result)
        {
            if (__instance == null || !StoneGolemRecruitmentRuntime.IsStoneGolem(__instance)) return;
            ZNetView view = __instance.GetComponent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (WorkerZdo.IsWorker(zdo)) return;

            string recruitment = StoneGolemRecruitmentRuntime.BuildHover(__instance);
            if (!string.IsNullOrEmpty(recruitment)) __result = recruitment;
        }
    }

    [HarmonyPatch(typeof(Humanoid), "BlockAttack")]
    internal static class StoneGolemCombatTrialBlockPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Humanoid __instance, Character attacker, bool __result, float ___m_blockTimer)
        {
            Player player = __instance as Player;
            if (player == null || player != Player.m_localPlayer) return;
            StoneGolemRecruitmentRuntime.HandleBlockResult(player, attacker, __result, ___m_blockTimer);
        }
    }

    [HarmonyPatch(typeof(Character), "RPC_Damage")]
    internal static class StoneGolemCombatTrialDamagePatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Character __instance, HitData hit)
        {
            if (__instance == null || hit == null) return;

            Player player = __instance as Player;
            if (player != null)
            {
                // RPC_Damage postfix runs after Humanoid.BlockAttack. Therefore a real perfect
                // parry has already recorded the current frame and will not be mistaken for a hit.
                StoneGolemRecruitmentRuntime.HandlePlayerDamaged(player, hit);
                return;
            }

            if (StoneGolemRecruitmentRuntime.IsStoneGolem(__instance))
                StoneGolemRecruitmentRuntime.HandleGolemDamagedByPlayer(__instance, hit);
        }
    }
}
