using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    internal enum DraugrHonorPath
    {
        Warrior = 0,
        Hunter = 1,
        Elite = 2
    }

    /// <summary>
    /// Draugr recruitment via a trophy-offering ritual performed at a player-built ancestor shrine
    /// near a natural Spawner_DraugrPile. State belongs to the Body Pile ZDO, not the shrine, so
    /// destroying/rebuilding the shrine cannot reroll or reset the ritual.
    /// </summary>
    internal static class DraugrHonorRecruitmentRuntime
    {
        internal const string BodyPilePrefab = "Spawner_DraugrPile";
        internal const int RequiredHonor = 100;
        internal const float ShrineLinkRadius = 22f;

        private const string HonorKey = "vw_draugr_honor";
        private const string WarriorKey = "vw_draugr_honor_warrior";
        private const string HunterKey = "vw_draugr_honor_hunter";
        private const string EliteKey = "vw_draugr_honor_elite";
        private const string PacifiedKey = "vw_draugr_pile_pacified";
        private const string ConsumedKey = "vw_draugr_pile_consumed";
        private const string SelectedSpeciesKey = "vw_draugr_pile_selected";
        private const string RecruiterKey = "vw_draugr_pile_recruiter";

        private static readonly List<SpawnArea> BodyPileScratch = new List<SpawnArea>(32);
        private static readonly Collider[] CandidateHits = new Collider[128];
        private static readonly MethodInfo ClaimOwnershipMethod = typeof(ZNetView).GetMethod("ClaimOwnership", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static MethodInfo _getPrefabMethod;
        private static bool _getPrefabResolved;

        internal static string BuildHover(Vector3 shrinePosition)
        {
            if (!TryFindNearestBodyPile(shrinePosition, out SpawnArea pile, out float distance))
            {
                return "Draugr-Ahnenmal\n" +
                       $"<color=#9FA8B8>Kein natuerlicher Leichenhaufen innerhalb {ShrineLinkRadius:0} m.</color>\n" +
                       "Errichte das Ahnenmal nahe einem Draugr-Leichenhaufen.";
            }

            ZDO zdo = GetZdo(pile);
            if (zdo == null)
                return $"Draugr-Ahnenmal\nLeichenhaufen: {distance:0.0} m\n<color=#9FA8B8>Netzwerkstatus noch nicht bereit.</color>";

            int honor = Mathf.Clamp(zdo.GetInt(HonorKey, 0), 0, RequiredHonor);
            int warrior = Mathf.Max(0, zdo.GetInt(WarriorKey, 0));
            int hunter = Mathf.Max(0, zdo.GetInt(HunterKey, 0));
            int elite = Mathf.Max(0, zdo.GetInt(EliteKey, 0));
            bool pacified = zdo.GetBool(PacifiedKey, false);
            bool consumed = zdo.GetBool(ConsumedKey, false);
            long recruiter = zdo.GetLong(RecruiterKey, 0L);
            Player local = Player.m_localPlayer;
            bool claimedByOther = (honor > 0 || pacified) && recruiter != 0L && local != null && recruiter != local.GetPlayerID();

            if (consumed)
            {
                string species = zdo.GetString(SelectedSpeciesKey, "Draugr");
                return "Draugr-Ahnenmal\n" +
                       "<color=#7ED47E>Der Leichenhaufen ist befriedet.</color>\n" +
                       $"Verbündeter: {GetSpeciesLabel(species)}";
            }

            string chances = BuildChanceText(warrior, hunter, elite);
            if (claimedByOther)
            {
                return "Draugr-Ahnenmal\n" +
                       $"Ehre: <color=#EFC85B>{honor}/{RequiredHonor}</color>\n" +
                       chances + "\n" +
                       "<color=#9FA8B8>Dieses Ahnenritual wurde bereits von einem anderen Spieler begonnen.</color>";
            }

            if (pacified || honor >= RequiredHonor)
            {
                return "Draugr-Ahnenmal\n" +
                       $"Ehre: <color=#EFC85B>{RequiredHonor}/{RequiredHonor}</color>\n" +
                       chances + "\n" +
                       "[<color=yellow><b>$KEY_Use</b></color>] Bindung abschliessen";
            }

            return "Draugr-Ahnenmal\n" +
                   $"Leichenhaufen: {distance:0.0} m\n" +
                   $"Ehre: <color=#EFC85B>{honor}/{RequiredHonor}</color>\n" +
                   chances + "\n" +
                   "<color=#9FA8B8>Benutze eine fremde Trophäe am Ahnenmal. Draugr-Trophäen werden nicht angenommen.</color>";
        }

        internal static bool TryInteract(Vector3 shrinePosition, Player player)
        {
            if (player == null) return false;
            if (!TryFindNearestBodyPile(shrinePosition, out SpawnArea pile, out _))
            {
                Notify(player, $"Kein natuerlicher Draugr-Leichenhaufen innerhalb {ShrineLinkRadius:0} m.");
                return true;
            }

            ZNetView view = GetNetworkView(pile);
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null)
            {
                Notify(player, "Der Leichenhaufen ist noch nicht netzwerkbereit.");
                return true;
            }

            if (zdo.GetBool(ConsumedKey, false))
            {
                Notify(player, "Die Ahnen dieses Leichenhaufens haben bereits einen Verbündeten entsandt.");
                return true;
            }

            int honor = Mathf.Clamp(zdo.GetInt(HonorKey, 0), 0, RequiredHonor);
            long recruiter = zdo.GetLong(RecruiterKey, 0L);
            if ((honor > 0 || zdo.GetBool(PacifiedKey, false)) && recruiter != 0L && recruiter != player.GetPlayerID())
            {
                Notify(player, "Dieses Ahnenritual wurde bereits von einem anderen Spieler begonnen.");
                return true;
            }

            if (honor >= RequiredHonor || zdo.GetBool(PacifiedKey, false))
            {
                if (!EnsureOwnership(view))
                {
                    Notify(player, "Der Leichenhaufen wird gerade von einem anderen Netzwerkbesitzer verwaltet.");
                    return true;
                }
                TryFinalize(pile, player, zdo);
                return true;
            }

            Notify(player, "Opfere am Ahnenmal Trophäen besiegter Feinde. Tier-/Jagdtrophäen locken eher einen Draugr-Jaeger, schwere Beute eher einen Elite-Draugr. Eigene Draugr-Trophäen gelten als Frevel.");
            return true;
        }

        internal static bool TryOfferTrophy(Vector3 shrinePosition, Player player, ItemDrop.ItemData item)
        {
            if (player == null || item == null || item.m_dropPrefab == null) return false;

            string trophyPrefab = SpeciesRoleRegistry.NormalizePrefabName(item.m_dropPrefab.name);
            if (!TryDescribeTrophy(trophyPrefab, out int value, out DraugrHonorPath path))
            {
                if (IsDraugrTrophy(trophyPrefab))
                {
                    Notify(player, "Die eigenen Schaedel der Draugr sind keine Ehrengabe. Das Ahnenmal weist die Trophäe zurueck.");
                    return true;
                }
                return false;
            }

            if (!TryFindNearestBodyPile(shrinePosition, out SpawnArea pile, out _))
            {
                Notify(player, $"Das Ahnenmal steht zu weit von einem natuerlichen Draugr-Leichenhaufen entfernt (max. {ShrineLinkRadius:0} m).");
                return true;
            }

            ZNetView view = GetNetworkView(pile);
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null)
            {
                Notify(player, "Der Leichenhaufen ist noch nicht netzwerkbereit.");
                return true;
            }
            if (!EnsureOwnership(view))
            {
                Notify(player, "Der Leichenhaufen wird gerade von einem anderen Netzwerkbesitzer verwaltet.");
                return true;
            }
            if (zdo.GetBool(ConsumedKey, false))
            {
                Notify(player, "Dieser Leichenhaufen hat seinen Verbündeten bereits entsandt.");
                return true;
            }

            int oldHonor = Mathf.Clamp(zdo.GetInt(HonorKey, 0), 0, RequiredHonor);
            long recruiter = zdo.GetLong(RecruiterKey, 0L);
            if ((oldHonor > 0 || zdo.GetBool(PacifiedKey, false)) && recruiter != 0L && recruiter != player.GetPlayerID())
            {
                Notify(player, "Dieses Ahnenritual wurde bereits von einem anderen Spieler begonnen.");
                return true;
            }

            if (oldHonor >= RequiredHonor || zdo.GetBool(PacifiedKey, false))
            {
                TryFinalize(pile, player, zdo);
                return true;
            }

            Inventory inventory = player.GetInventory();
            if (inventory == null || item.m_stack <= 0)
            {
                Notify(player, "Die Trophäe ist nicht mehr im Inventar.");
                return true;
            }

            int credited = Mathf.Min(value, RequiredHonor - oldHonor);
            if (credited <= 0) return true;

            inventory.RemoveItem(item, 1);

            int honor = oldHonor + credited;
            zdo.Set(HonorKey, honor);
            zdo.Set(RecruiterKey, player.GetPlayerID());
            AddPathScore(zdo, path, credited);

            int warrior = Mathf.Max(0, zdo.GetInt(WarriorKey, 0));
            int hunter = Mathf.Max(0, zdo.GetInt(HunterKey, 0));
            int elite = Mathf.Max(0, zdo.GetInt(EliteKey, 0));

            if (honor < RequiredHonor)
            {
                Notify(player, $"Trophäe geopfert: +{credited} Ehre ({honor}/{RequiredHonor}). {BuildChanceTextPlain(warrior, hunter, elite)}");
                return true;
            }

            zdo.Set(PacifiedKey, true);
            DraugrBodyPileSuppressionRuntime.TrySuppress(pile);
            Notify(player, $"Die Ahnen sind geehrt: {RequiredHonor}/{RequiredHonor}. Der Leichenhaufen verstummt.");
            TryFinalize(pile, player, zdo);
            return true;
        }

        internal static bool IsBodyPile(SpawnArea spawner)
        {
            if (spawner == null) return false;
            string own = SpeciesRoleRegistry.NormalizePrefabName(spawner.gameObject.name);
            if (string.Equals(own, BodyPilePrefab, StringComparison.OrdinalIgnoreCase)) return true;
            ZNetView view = GetNetworkView(spawner);
            string root = view != null ? SpeciesRoleRegistry.NormalizePrefabName(view.gameObject.name) : string.Empty;
            return string.Equals(root, BodyPilePrefab, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsPacified(ZDO zdo) => zdo != null && zdo.GetBool(PacifiedKey, false);

        private static bool TryFinalize(SpawnArea pile, Player player, ZDO zdo)
        {
            if (pile == null || player == null || zdo == null) return false;
            if (zdo.GetBool(ConsumedKey, false)) return true;
            if (zdo.GetInt(HonorKey, 0) < RequiredHonor && !zdo.GetBool(PacifiedKey, false)) return false;

            zdo.Set(PacifiedKey, true);
            DraugrBodyPileSuppressionRuntime.TrySuppress(pile);

            string selected = zdo.GetString(SelectedSpeciesKey, string.Empty);
            if (string.IsNullOrEmpty(selected))
            {
                selected = ChooseSpecies(zdo);
                zdo.Set(SelectedSpeciesKey, selected);
            }

            if (!TryRecruitOrSpawnSelected(pile, player, selected, out Character worker, out string failure))
            {
                Notify(player, $"Der Leichenhaufen ist befriedet, aber der Verbündete konnte noch nicht erscheinen: {failure}. Druecke E am Ahnenmal zum erneuten Versuch.");
                Plugin.Log?.LogWarning($"Draugr honor ritual finalization pending: selected={selected}, failure={failure}, pile={pile.transform.position}.");
                return false;
            }

            zdo.Set(ConsumedKey, true);
            zdo.Set(RecruiterKey, player.GetPlayerID());
            Notify(player, $"{GetSpeciesLabel(selected)} erkennt deine Ehre an und schliesst sich dir an!");
            Plugin.Log?.LogInfo($"Draugr Body Pile honor ritual complete: species={selected}, player={player.GetPlayerID()}, pile={pile.transform.position}, worker={worker?.transform.position}.");
            return true;
        }

        private static bool TryRecruitOrSpawnSelected(SpawnArea pile, Player player, string species, out Character worker, out string failure)
        {
            worker = FindExistingCandidate(pile.transform.position, species);
            if (worker == null)
            {
                GameObject prefab = ResolveNetworkPrefab(species);
                if (prefab == null)
                {
                    failure = $"Prefab {species} nicht gefunden";
                    return false;
                }

                Vector3 spawn = ChooseSpawnPosition(pile.transform.position, player.transform.position);
                GameObject instance = UnityEngine.Object.Instantiate(prefab, spawn, Quaternion.identity);
                if (instance == null)
                {
                    failure = $"{species} konnte nicht instanziert werden";
                    return false;
                }
                worker = instance.GetComponent<Character>() ?? instance.GetComponentInChildren<Character>();
                if (worker == null)
                {
                    UnityEngine.Object.Destroy(instance);
                    failure = $"{species} besitzt keine Character-Komponente";
                    return false;
                }
            }

            ZNetView view = worker.GetComponent<ZNetView>();
            if (view == null || !view.IsValid() || view.GetZDO() == null)
            {
                failure = "Draugr besitzt keinen gueltigen ZDO";
                return false;
            }
            if (!EnsureOwnership(view))
            {
                failure = "Draugr ist nicht lokal im Netzwerkbesitz";
                return false;
            }

            ZDO workerZdo = view.GetZDO();
            if (WorkerZdo.IsWorker(workerZdo))
            {
                failure = "ausgewaehlter Draugr ist bereits Worker";
                return false;
            }
            if (!SpeciesRoleRegistry.TryGetRole(species, out WorkerRole role))
            {
                failure = $"keine Worker-Rolle fuer {species}";
                return false;
            }

            Vector3 home = pile.transform.position;
            NaturalRecruitmentCompletionRuntime.Complete(
                worker,
                workerZdo,
                player.GetPlayerID(),
                role,
                species,
                home,
                35f,
                minimumDefensive: false,
                followPlayer: player);
            failure = string.Empty;
            return true;
        }

        private static Character FindExistingCandidate(Vector3 center, string species)
        {
            int count = Physics.OverlapSphereNonAlloc(center, 20f, CandidateHits);
            Character best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                Collider hit = CandidateHits[i];
                if (hit == null) continue;
                Character character = hit.GetComponentInParent<Character>();
                if (character == null || character is Player || character.IsDead()) continue;
                string candidateSpecies = SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name);
                if (!string.Equals(candidateSpecies, species, StringComparison.OrdinalIgnoreCase)) continue;

                ZNetView view = character.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo == null || WorkerZdo.IsWorker(zdo)) continue;

                float sqr = (character.transform.position - center).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    best = character;
                    bestSqr = sqr;
                }
            }
            return best;
        }

        private static string ChooseSpecies(ZDO zdo)
        {
            int warrior = Mathf.Max(0, zdo.GetInt(WarriorKey, 0));
            int hunter = Mathf.Max(0, zdo.GetInt(HunterKey, 0));
            int elite = Mathf.Max(0, zdo.GetInt(EliteKey, 0));
            int total = warrior + hunter + elite;
            if (total <= 0)
            {
                // Vanilla Body Pile fallback ratio: normal/ranged are common, elite is rare.
                warrior = 43;
                hunter = 43;
                elite = 14;
                total = 100;
            }

            int roll = UnityEngine.Random.Range(0, total);
            if (roll < hunter) return "Draugr_Ranged";
            roll -= hunter;
            if (roll < elite) return "Draugr_Elite";
            return "Draugr";
        }

        private static void AddPathScore(ZDO zdo, DraugrHonorPath path, int amount)
        {
            string key = path == DraugrHonorPath.Hunter ? HunterKey : path == DraugrHonorPath.Elite ? EliteKey : WarriorKey;
            zdo.Set(key, Mathf.Max(0, zdo.GetInt(key, 0)) + Mathf.Max(0, amount));
        }

        private static string BuildChanceText(int warrior, int hunter, int elite)
        {
            GetPercents(warrior, hunter, elite, out int w, out int h, out int e);
            return $"Rufchancen: Krieger <color=#D7B37A>{w}%</color> | Jaeger <color=#8FC7E8>{h}%</color> | Elite <color=#D98787>{e}%</color>";
        }

        private static string BuildChanceTextPlain(int warrior, int hunter, int elite)
        {
            GetPercents(warrior, hunter, elite, out int w, out int h, out int e);
            return $"Rufchancen: Krieger {w}% | Jaeger {h}% | Elite {e}%";
        }

        private static void GetPercents(int warrior, int hunter, int elite, out int warriorPct, out int hunterPct, out int elitePct)
        {
            int total = Mathf.Max(0, warrior) + Mathf.Max(0, hunter) + Mathf.Max(0, elite);
            if (total <= 0)
            {
                warriorPct = 43;
                hunterPct = 43;
                elitePct = 14;
                return;
            }

            hunterPct = Mathf.RoundToInt(hunter * 100f / total);
            elitePct = Mathf.RoundToInt(elite * 100f / total);
            warriorPct = Mathf.Max(0, 100 - hunterPct - elitePct);
        }

        private static bool TryDescribeTrophy(string prefab, out int honor, out DraugrHonorPath path)
        {
            honor = 0;
            path = DraugrHonorPath.Warrior;
            if (string.IsNullOrEmpty(prefab) || !prefab.StartsWith("Trophy", StringComparison.OrdinalIgnoreCase)) return false;
            if (IsDraugrTrophy(prefab)) return false;

            if (ContainsAny(prefab, "Eikthyr", "Elder", "Bonemass", "DragonQueen", "GoblinKing", "SeekerQueen", "Fader"))
            {
                honor = 50;
                path = DraugrHonorPath.Elite;
                return true;
            }

            if (ContainsAny(prefab, "Abomination", "Troll", "SGolem", "StoneGolem", "Fenring", "Wraith", "GoblinBrute", "FulingBerserker", "Serpent", "Gjall", "SeekerBrute", "Morgen"))
            {
                honor = 25;
                path = DraugrHonorPath.Elite;
                return true;
            }

            if (ContainsAny(prefab, "Deer", "Boar", "Neck", "Wolf", "Hare", "Chicken", "Hen", "Drake", "Deathsquito", "Leech", "Lox", "Bat", "Asksvin", "Volture", "Bonemaw", "Seeker", "Tick"))
            {
                honor = ContainsAny(prefab, "Lox", "Serpent", "Asksvin", "Bonemaw") ? 15 : 10;
                path = DraugrHonorPath.Hunter;
                return true;
            }

            honor = ContainsAny(prefab, "GreydwarfBrute", "GreydwarfShaman", "Goblin", "Fuling", "SkeletonPoison", "Cultist", "Charred") ? 10 : 5;
            path = DraugrHonorPath.Warrior;
            return true;
        }

        private static bool IsDraugrTrophy(string prefab)
        {
            return !string.IsNullOrEmpty(prefab) && prefab.StartsWith("TrophyDraugr", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsAny(string value, params string[] tokens)
        {
            if (string.IsNullOrEmpty(value) || tokens == null) return false;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (!string.IsNullOrEmpty(tokens[i]) && value.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static string GetSpeciesLabel(string species)
        {
            if (string.Equals(species, "Draugr_Ranged", StringComparison.OrdinalIgnoreCase)) return "Draugr-Jaeger";
            if (string.Equals(species, "Draugr_Elite", StringComparison.OrdinalIgnoreCase)) return "Draugr-Elite";
            return "Draugr-Krieger";
        }

        private static bool TryFindNearestBodyPile(Vector3 center, out SpawnArea best, out float distance)
        {
            best = null;
            distance = float.MaxValue;

            WorkerSpawnerIndexRuntime.CopySpawnAreasInRadius(center, ShrineLinkRadius, BodyPileScratch);
            float bestSqr = ShrineLinkRadius * ShrineLinkRadius;

            for (int i = 0; i < BodyPileScratch.Count; i++)
            {
                SpawnArea pile = BodyPileScratch[i];
                if (pile == null || !IsBodyPile(pile)) continue;

                float sqr = (pile.transform.position - center).sqrMagnitude;
                if (sqr > bestSqr) continue;
                bestSqr = sqr;
                best = pile;
            }

            BodyPileScratch.Clear();
            if (best == null) return false;
            distance = Mathf.Sqrt(bestSqr);
            return true;
        }

        private static ZNetView GetNetworkView(SpawnArea pile)
        {
            if (pile == null) return null;
            return pile.GetComponent<ZNetView>() ?? pile.GetComponentInParent<ZNetView>();
        }

        private static ZDO GetZdo(SpawnArea pile)
        {
            ZNetView view = GetNetworkView(pile);
            return view != null && view.IsValid() ? view.GetZDO() : null;
        }

        private static bool EnsureOwnership(ZNetView view)
        {
            if (view == null || !view.IsValid()) return false;
            if (view.IsOwner()) return true;
            try { ClaimOwnershipMethod?.Invoke(view, null); } catch { }
            return view.IsOwner();
        }

        private static GameObject ResolveNetworkPrefab(string prefabName)
        {
            ZNetScene scene = ZNetScene.instance;
            if (scene == null || string.IsNullOrEmpty(prefabName)) return null;
            if (!_getPrefabResolved)
            {
                _getPrefabResolved = true;
                _getPrefabMethod = typeof(ZNetScene).GetMethod("GetPrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
            }
            if (_getPrefabMethod == null) return null;
            try { return _getPrefabMethod.Invoke(scene, new object[] { prefabName }) as GameObject; }
            catch { return null; }
        }

        private static Vector3 ChooseSpawnPosition(Vector3 pile, Vector3 player)
        {
            Vector3 away = pile - player;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
            away.Normalize();
            return pile + away * 2.75f + Vector3.up * 0.35f;
        }

        private static void Notify(Player player, string text)
        {
            if (player == null || string.IsNullOrEmpty(text)) return;
            try { player.Message(MessageHud.MessageType.Center, text); } catch { }
        }
    }

    /// <summary>Persistent no-respawn gate for Body Piles completed through the honor ritual.</summary>
    internal static class DraugrBodyPileSuppressionRuntime
    {
        internal static bool ShouldSuppress(SpawnArea spawner)
        {
            if (!DraugrHonorRecruitmentRuntime.IsBodyPile(spawner)) return false;
            ZNetView view = spawner.GetComponent<ZNetView>() ?? spawner.GetComponentInParent<ZNetView>();
            if (view == null || !view.IsValid()) return false;
            return DraugrHonorRecruitmentRuntime.IsPacified(view.GetZDO());
        }

        internal static bool TrySuppress(SpawnArea spawner)
        {
            if (!ShouldSuppress(spawner)) return false;
            if (spawner.enabled)
            {
                spawner.CancelInvoke();
                spawner.enabled = false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(SpawnArea), "Awake")]
    internal static class DraugrBodyPileAwakeSuppressionPatch
    {
        private static void Postfix(SpawnArea __instance)
        {
            DraugrBodyPileSuppressionRuntime.TrySuppress(__instance);
        }
    }

    [HarmonyPatch(typeof(SpawnArea), "UpdateSpawn")]
    internal static class DraugrBodyPileUpdateSuppressionPatch
    {
        private static bool Prefix(SpawnArea __instance)
        {
            return !DraugrBodyPileSuppressionRuntime.TrySuppress(__instance);
        }
    }
}
