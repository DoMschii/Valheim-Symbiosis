using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Plains recruitment is intentionally relationship-driven instead of a one-button conversion.
    /// Stage 1: feed/gift a specific loaded Fuling settlement until the local group trusts the player.
    /// Stage 2: once that settlement is friendly, convince one individual with its preferred food.
    ///
    /// Village state is mirrored onto every nearby Fuling ZDO when a gift is accepted. That makes the
    /// relationship persistent without introducing a synthetic world object and also lets any villager
    /// act as the interaction surface for the same settlement.
    /// </summary>
    internal static class FulingVillageDiplomacyRuntime
    {
        internal const string VillageTrustKey = "vw_fuling_village_trust";
        internal const string VillageIdKey = "vw_fuling_village_id";
        internal const string PersonalTrustKey = "vw_fuling_personal_trust";
        internal const string PersonalRecruiterKey = "vw_fuling_personal_recruiter";
        internal const string LoreStoneSpawnedKey = "vw_fuling_lore_stone_v405";
        internal const int ToleratedThreshold = 60;
        internal const int FriendlyThreshold = 100;
        internal const int PersonalThreshold = 100;
        private const float VillageRadius = 85f;
        private const float VillageVisitRadius = 55f;
        private const float InteractDistance = 4.5f;
        private const float ToleratedWarningDelay = 30f;
        private const float DiplomacyTickInterval = 0.75f;
        private const float ThrownGiftSearchRadius = 8f;
        private const float ThrownGiftVillageSearchRadius = 45f;
        private const float ThrownGiftCooldown = 1.25f;

        private static float _nextDiplomacyTick;
        private static string _activeVisitVillageId = string.Empty;
        private static Vector3 _activeVisitCenter;
        private static float _activeVisitStartedAt;
        private static bool _activeVisitWarned;
        private static readonly Dictionary<int, float> ActionPenaltyCooldown = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> ThrownGiftCooldowns = new Dictionary<int, float>();
        private static readonly Dictionary<string, float> LoreRetryAt = new Dictionary<string, float>(StringComparer.Ordinal);

        private static readonly List<Character> NearbyFulingScratch = new List<Character>(32);
        private static readonly List<int> ExpiredIntScratch = new List<int>(32);
        private static readonly List<string> ExpiredStringScratch = new List<string>(16);
        private static float _nextCooldownCleanup;

        private sealed class VillageGift
        {
            internal readonly string Prefab;
            internal readonly string Display;
            internal readonly int Trust;
            internal readonly int Priority;

            internal VillageGift(string prefab, string display, int trust, int priority)
            {
                Prefab = prefab;
                Display = display;
                Trust = trust;
                Priority = priority;
            }
        }

        private static readonly VillageGift[] VillageGifts =
        {
            // Processed food is deliberately worth more than raw field produce. It makes befriending a
            // late-game village feel like an actual gift economy while still allowing a player who has
            // just reached the Plains to start with food that naturally exists there.
            new VillageGift("Bread", "Brot", 6, 1),
            new VillageGift("CookedLoxMeat", "gekochtes Loxfleisch", 6, 2),
            new VillageGift("Cloudberry", "Moltebeeren", 3, 3),
            new VillageGift("Barley", "Gerste", 3, 4)
        };

        internal static bool IsFuling(Character character)
        {
            if (character == null || character is Player) return false;
            string species = SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name);
            return species.StartsWith("Fuling", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryHandleInteract(Character character, Humanoid user, bool hold)
        {
            if (hold || character == null || user == null || !IsFuling(character)) return false;
            Player player = user as Player;
            if (player == null) return false;

            ZDO zdo = GetZdo(character);
            if (zdo == null || WorkerZdo.IsWorker(zdo)) return false;
            if ((player.transform.position - character.transform.position).sqrMagnitude >
                InteractDistance * InteractDistance) return false;

            int villageTrust = SynchronizeVillageTrust(character);
            if (villageTrust < FriendlyThreshold)
            {
                // At 60+ the village tolerates the player physically entering, but recruitment remains
                // locked until the full 100-point friendship threshold has been reached.
                if (villageTrust >= ToleratedThreshold && !character.IsTamed()) character.SetTamed(true);
                return TryOfferVillageGift(character, player, villageTrust);
            }

            // The village is already friendly. Reassert the vanilla friendly faction flag because the
            // custom trust record is authoritative if another mod or a reload temporarily reset it.
            if (!character.IsTamed()) character.SetTamed(true);
            return TryOfferPersonalGift(character, player);
        }

        internal static string BuildHover(Character character)
        {
            if (character == null || !IsFuling(character)) return string.Empty;
            ZDO zdo = GetZdo(character);
            if (zdo == null || WorkerZdo.IsWorker(zdo)) return string.Empty;

            int villageTrust = SynchronizeVillageTrust(character);
            string species = SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name);
            string name = SpeciesDisplay(species);
            if (villageTrust < FriendlyThreshold)
            {
                string relation = villageTrust >= ToleratedThreshold
                    ? "<color=#EFC85B>geduldet</color>"
                    : "<color=#E4866E>misstrauisch</color>";
                string action = villageTrust < ToleratedThreshold
                    ? "Geschenke in die Nähe der Fulings werfen  <color=#9FA8B8>(Brot/Loxfleisch/Moltebeeren/Gerste)</color>"
                    : "[<color=yellow><b>E</b></color>] Geschenk geben  <color=#9FA8B8>(Brot/Loxfleisch/Moltebeeren/Gerste)</color>";
                return $"<color=#E9D38B><b>{name}</b></color> • {relation}\n" +
                       $"Dorf-Vertrauen: <color=#EFC85B>{villageTrust}/{FriendlyThreshold}</color>\n" + action;
            }

            if (!character.IsTamed()) character.SetTamed(true);
            int personal = Mathf.Clamp(zdo.GetInt(PersonalTrustKey, 0), 0, PersonalThreshold);
            long personalRecruiter = zdo.GetLong(PersonalRecruiterKey, 0L);
            Player local = Player.m_localPlayer;
            bool claimedByOther = personal > 0 && personalRecruiter != 0L && local != null && personalRecruiter != local.GetPlayerID();
            string preferred = PersonalGiftDisplay(species);
            return $"<color=#8FD18F><b>{name}</b> • Dorf freundlich</color>\n" +
                   $"Persönliches Vertrauen: <color=#8FD18F>{personal}/{PersonalThreshold}</color>\n" +
                   (claimedByOther
                       ? "<color=#9FA8B8>Dieser Fuling fasst bereits Vertrauen zu einem anderen Spieler.</color>"
                       : $"[<color=yellow><b>E</b></color>] {preferred} schenken");
        }

        private static bool TryOfferVillageGift(Character target, Player player, int currentTrust)
        {
            Inventory inventory = player.GetInventory();
            if (!TryFindVillageGift(inventory, out ItemDrop.ItemData item, out VillageGift gift))
            {
                Notify(player, "Das Dorf erwartet ein Geschenk: Brot, gekochtes Loxfleisch, Moltebeeren oder Gerste.");
                return true;
            }

            if (!GreydwarfOfferingCatalog.TryConsumeOne(inventory, item, Plugin.Instance?.DebugLogPublic))
            {
                Notify(player, "Das Geschenk konnte nicht übergeben werden.");
                return true;
            }

            int next = Mathf.Clamp(currentTrust + gift.Trust, 0, FriendlyThreshold);
            ApplyVillageTrust(target, next);
            string villageId = ResolveVillageId(target);

            if (next >= FriendlyThreshold)
            {
                Notify(player, $"Geschenk angenommen (+{gift.Trust}%) • Das Fuling-Dorf nimmt dich als Freund an!");
                Plugin.Log?.LogInfo($"Fuling village befriended: id={villageId}, player={player.GetPlayerID()}.");
            }
            else
            {
                Notify(player, $"Geschenk angenommen (+{gift.Trust}%) • Dorf-Vertrauen {next}%");
            }
            return true;
        }

        private static bool TryOfferPersonalGift(Character target, Player player)
        {
            ZDO zdo = GetZdo(target);
            if (zdo == null) return true;

            string species = SpeciesRoleRegistry.NormalizePrefabName(target.gameObject.name);
            string preferredPrefab = PersonalGiftPrefab(species);
            string preferredDisplay = PersonalGiftDisplay(species);
            int currentPersonal = Mathf.Clamp(zdo.GetInt(PersonalTrustKey, 0), 0, PersonalThreshold);
            long personalRecruiter = zdo.GetLong(PersonalRecruiterKey, 0L);
            long playerId = player.GetPlayerID();
            if (currentPersonal > 0 && personalRecruiter != 0L && personalRecruiter != playerId)
            {
                Notify(player, "Dieser Fuling fasst bereits Vertrauen zu einem anderen Spieler.");
                return true;
            }

            Inventory inventory = player.GetInventory();
            ItemDrop.ItemData item = FindItem(inventory, preferredPrefab);
            if (item == null)
            {
                Notify(player, $"{SpeciesDisplay(species)} möchte {preferredDisplay}.");
                return true;
            }

            if (!GreydwarfOfferingCatalog.TryConsumeOne(inventory, item, Plugin.Instance?.DebugLogPublic))
            {
                Notify(player, "Das Geschenk konnte nicht übergeben werden.");
                return true;
            }

            int trust = Mathf.Clamp(currentPersonal + 25, 0, PersonalThreshold);
            zdo.Set(PersonalTrustKey, trust);
            zdo.Set(PersonalRecruiterKey, playerId);
            if (trust < PersonalThreshold)
            {
                Notify(player, $"{preferredDisplay} geschenkt • Persönliches Vertrauen {trust}/{PersonalThreshold}");
                return true;
            }

            if (!SpeciesRoleRegistry.TryGetRole(species, out WorkerRole role))
            {
                Notify(player, "Dieser Fuling hat noch keine Worker-Rolle.");
                return true;
            }

            if (WorkerZdo.IsWorker(zdo)) return true;
            Vector3 home = target.transform.position;
            NaturalRecruitmentCompletionRuntime.Complete(
                target,
                zdo,
                playerId,
                role,
                species,
                home,
                40f,
                minimumDefensive: false,
                followPlayer: player);
            // The old provisional position-only FulingCamp assignment is intentionally gone.
            // A real Fuling rest piece must now be built/assigned just like every other Worker family.
            Notify(player, $"{SpeciesDisplay(species)} schließt sich dir an und folgt dir. Weise ihm in deiner Base eine Fuling-Ruhestätte zu!");
            Plugin.Log?.LogInfo($"Fuling recruited through diplomacy: species={species}, role={role}, player={player.GetPlayerID()}, village={zdo.GetString(VillageIdKey, string.Empty)}.");
            return true;
        }

        private static int SynchronizeVillageTrust(Character center)
        {
            ZDO centerZdo = GetZdo(center);
            if (centerZdo == null) return 0;
            int max = Mathf.Clamp(centerZdo.GetInt(VillageTrustKey, 0), 0, FriendlyThreshold);
            string villageId = centerZdo.GetString(VillageIdKey, string.Empty);
            List<Character> members = CollectNearbyFulings(center.transform.position, VillageRadius);

            for (int i = 0; i < members.Count; i++)
            {
                ZDO zdo = GetZdo(members[i]);
                if (zdo == null || WorkerZdo.IsWorker(zdo)) continue;
                int trust = Mathf.Clamp(zdo.GetInt(VillageTrustKey, 0), 0, FriendlyThreshold);
                if (trust > max)
                {
                    max = trust;
                    string candidateId = zdo.GetString(VillageIdKey, string.Empty);
                    if (!string.IsNullOrEmpty(candidateId)) villageId = candidateId;
                }
            }

            if (max <= 0) return 0;
            if (string.IsNullOrEmpty(villageId)) villageId = ResolveVillageId(center);
            for (int i = 0; i < members.Count; i++)
            {
                ZDO zdo = GetZdo(members[i]);
                if (zdo == null || WorkerZdo.IsWorker(zdo)) continue;
                if (zdo.GetInt(VillageTrustKey, 0) < max) zdo.Set(VillageTrustKey, max);
                if (string.IsNullOrEmpty(zdo.GetString(VillageIdKey, string.Empty))) zdo.Set(VillageIdKey, villageId);
                if (max >= ToleratedThreshold && !members[i].IsTamed()) members[i].SetTamed(true);
                else if (max < ToleratedThreshold && members[i].IsTamed()) members[i].SetTamed(false);
            }
            return max;
        }

        private static List<Character> CollectNearbyFulings(Vector3 center, float radius)
        {
            NearbyFulingScratch.Clear();

            // v0.5.459: diplomacy runs every 0.75 s. The old path allocated an OverlapSphere
            // collider array + List<Character> + HashSet<int> for every village synchronization.
            // WorkerCharacterIndexRuntime already provides one unique loaded Character list.
            List<Character> loaded = WorkerCharacterIndexRuntime.GetLiveNonPlayers();
            float radiusSqr = radius * radius;

            for (int i = 0; i < loaded.Count; i++)
            {
                Character character = loaded[i];
                if (!IsFuling(character)) continue;
                if ((character.transform.position - center).sqrMagnitude > radiusSqr) continue;
                NearbyFulingScratch.Add(character);
            }

            return NearbyFulingScratch;
        }

        internal static void Tick()
        {
            if (Time.unscaledTime < _nextDiplomacyTick) return;
            _nextDiplomacyTick = Time.unscaledTime + DiplomacyTickInterval;

            CleanupExpiredStateIfDue();

            Player player = Player.m_localPlayer;
            if (player == null)
            {
                EndToleratedVisit();
                return;
            }

            // Hostile first contact happens through world drops: the player can throw a suitable
            // gift toward a village without having to walk into melee range. This is intentionally
            // checked before the tolerated-visit logic so 0..59 trust villages can progress safely.
            TryAcceptThrownVillageGift(player);

            Character nearby = FindNearestVillageFuling(player.transform.position, VillageVisitRadius);
            if (nearby == null)
            {
                EndToleratedVisit();
                return;
            }

            ZDO zdo = GetZdo(nearby);
            if (zdo == null || WorkerZdo.IsWorker(zdo))
            {
                EndToleratedVisit();
                return;
            }

            // v0.5.405: use the already-running village contact tick to place exactly one
            // world-only diplomacy tablet for a real local Fuling cluster. This adds no new
            // scheduler or world scan; failed/lone-patrol candidates back off for 30 seconds.
            TryEnsureVillageLoreStone(nearby);

            int trust = SynchronizeVillageTrust(nearby);
            if (trust < ToleratedThreshold)
            {
                EndToleratedVisit();
                return;
            }

            string villageId = ResolveVillageId(nearby);
            if (trust >= FriendlyThreshold)
            {
                SetVillageTamed(nearby.transform.position, true);
                _activeVisitVillageId = string.Empty;
                _activeVisitWarned = false;
                return;
            }

            // 60..99 trust: temporary village access. The player is physically tolerated, but the
            // villagers have not yet accepted them as a permanent friend or recruiter.
            SetVillageTamed(nearby.transform.position, true);
            if (!string.Equals(_activeVisitVillageId, villageId, StringComparison.Ordinal))
            {
                _activeVisitVillageId = villageId;
                _activeVisitCenter = nearby.transform.position;
                _activeVisitStartedAt = Time.unscaledTime;
                _activeVisitWarned = false;
            }

            if (!_activeVisitWarned && Time.unscaledTime - _activeVisitStartedAt >= ToleratedWarningDelay)
            {
                _activeVisitWarned = true;
                Notify(player, "Die Fulings werden misstrauisch, verschwinde lieber.");
            }
        }

        private static void CleanupExpiredStateIfDue()
        {
            float now = Time.unscaledTime;
            if (now < _nextCooldownCleanup) return;
            _nextCooldownCleanup = now + 60f;

            ExpiredIntScratch.Clear();
            foreach (KeyValuePair<int, float> pair in ActionPenaltyCooldown)
                if (pair.Value <= now) ExpiredIntScratch.Add(pair.Key);
            for (int i = 0; i < ExpiredIntScratch.Count; i++)
                ActionPenaltyCooldown.Remove(ExpiredIntScratch[i]);

            ExpiredIntScratch.Clear();
            foreach (KeyValuePair<int, float> pair in ThrownGiftCooldowns)
                if (pair.Value <= now) ExpiredIntScratch.Add(pair.Key);
            for (int i = 0; i < ExpiredIntScratch.Count; i++)
                ThrownGiftCooldowns.Remove(ExpiredIntScratch[i]);

            ExpiredStringScratch.Clear();
            foreach (KeyValuePair<string, float> pair in LoreRetryAt)
                if (pair.Value <= now) ExpiredStringScratch.Add(pair.Key);
            for (int i = 0; i < ExpiredStringScratch.Count; i++)
                LoreRetryAt.Remove(ExpiredStringScratch[i]);

            ExpiredIntScratch.Clear();
            ExpiredStringScratch.Clear();
        }

        internal static void ClearCaches()
        {
            _nextDiplomacyTick = 0f;
            _nextCooldownCleanup = 0f;
            _activeVisitVillageId = string.Empty;
            _activeVisitCenter = Vector3.zero;
            _activeVisitStartedAt = 0f;
            _activeVisitWarned = false;

            ActionPenaltyCooldown.Clear();
            ThrownGiftCooldowns.Clear();
            LoreRetryAt.Clear();
            NearbyFulingScratch.Clear();
            ExpiredIntScratch.Clear();
            ExpiredStringScratch.Clear();
        }

        private static void TryEnsureVillageLoreStone(Character center)
        {
            if (center == null) return;
            ZDO centerZdo = GetZdo(center);
            if (centerZdo == null || WorkerZdo.IsWorker(centerZdo)) return;
            if (centerZdo.GetBool(LoreStoneSpawnedKey, false)) return;

            string villageId = ResolveVillageId(center);
            if (LoreRetryAt.TryGetValue(villageId, out float retryAt) && Time.unscaledTime < retryAt) return;

            ZNetView centerView = center.GetComponent<ZNetView>();
            if (centerView == null || !centerView.IsValid() || !centerView.IsOwner())
            {
                LoreRetryAt[villageId] = Time.unscaledTime + 30f;
                return;
            }

            List<Character> members = CollectNearbyFulings(center.transform.position, VillageRadius);
            if (members.Count < 3)
            {
                // Do not turn a lone Plains patrol into a "village" discovery marker.
                LoreRetryAt[villageId] = Time.unscaledTime + 30f;
                return;
            }

            // A sibling may already carry the durable flag even if the particular Fuling nearest
            // to the player was not loaded when the tablet was first created. Mirror that state.
            for (int i = 0; i < members.Count; i++)
            {
                ZDO memberZdo = GetZdo(members[i]);
                if (memberZdo == null || !memberZdo.GetBool(LoreStoneSpawnedKey, false)) continue;
                for (int j = 0; j < members.Count; j++)
                {
                    ZDO z = GetZdo(members[j]);
                    if (z == null || WorkerZdo.IsWorker(z)) continue;
                    z.Set(LoreStoneSpawnedKey, true);
                    if (string.IsNullOrEmpty(z.GetString(VillageIdKey, string.Empty))) z.Set(VillageIdKey, villageId);
                }
                centerZdo.Set(LoreStoneSpawnedKey, true);
                LoreRetryAt.Remove(villageId);
                return;
            }

            if (!WorkersLoreRuntime.TryGetWorldLorePrefab(WorkersLorePage.FulingDiplomacy, out GameObject prefab) || prefab == null)
            {
                LoreRetryAt[villageId] = Time.unscaledTime + 10f;
                return;
            }

            Vector3 villageCenter = Vector3.zero;
            int count = 0;
            for (int i = 0; i < members.Count; i++)
            {
                if (members[i] == null) continue;
                villageCenter += members[i].transform.position;
                count++;
            }
            if (count <= 0) return;
            villageCenter /= count;

            int seed = StableVillageSeed(villageId);
            float baseAngle = (Positive(seed ^ unchecked((int)0x51C3A7D9)) % 36000) / 100f;
            float radius = Mathf.Lerp(16f, 30f, (Positive(seed) % 10000) / 9999f);
            GameObject instance = null;

            for (int i = 0; i < 10; i++)
            {
                float angle = (baseAngle + i * 57f) * Mathf.Deg2Rad;
                float candidateRadius = radius + (i % 3) * 2f;
                Vector3 point = villageCenter + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * candidateRadius;
                if (ZoneSystem.instance != null)
                {
                    float ground = ZoneSystem.instance.GetGroundHeight(point);
                    if (ground <= ZoneSystem.instance.m_waterLevel + 0.35f) continue;
                    point.y = ground + 0.03f;
                }
                else
                {
                    point.y = villageCenter.y;
                }

                Quaternion rotation = Quaternion.Euler(0f, (baseAngle + 180f + i * 19f) % 360f, 0f);
                GameObject candidate = UnityEngine.Object.Instantiate(prefab, point, rotation);
                if (candidate == null) continue;
                ZNetView spawnedView = candidate.GetComponent<ZNetView>() ?? candidate.GetComponentInChildren<ZNetView>();
                if (spawnedView == null || !spawnedView.IsValid())
                {
                    UnityEngine.Object.Destroy(candidate);
                    continue;
                }
                instance = candidate;
                break;
            }

            if (instance == null)
            {
                LoreRetryAt[villageId] = Time.unscaledTime + 30f;
                return;
            }

            for (int i = 0; i < members.Count; i++)
            {
                ZDO memberZdo = GetZdo(members[i]);
                if (memberZdo == null || WorkerZdo.IsWorker(memberZdo)) continue;
                memberZdo.Set(VillageIdKey, villageId);
                memberZdo.Set(LoreStoneSpawnedKey, true);
            }
            centerZdo.Set(LoreStoneSpawnedKey, true);
            LoreRetryAt.Remove(villageId);
            Plugin.Log?.LogInfo($"Fuling diplomacy lore tablet placed: village={villageId}, members={members.Count}.");
        }

        private static int StableVillageSeed(string value)
        {
            unchecked
            {
                int hash = (int)2166136261;
                string text = value ?? string.Empty;
                for (int i = 0; i < text.Length; i++)
                {
                    hash ^= text[i];
                    hash *= 16777619;
                }
                return hash;
            }
        }

        private static int Positive(int value)
        {
            if (value == int.MinValue) return int.MaxValue;
            return Math.Abs(value);
        }

        private static void TryAcceptThrownVillageGift(Player player)
        {
            if (player == null) return;

            // Cheap gate before touching the ItemDrop index. Diplomacy ticks run everywhere, not
            // only in the Plains, so worlds without a nearby village should do essentially no work.
            Character nearbyVillageFuling = FindNearestVillageFuling(player.transform.position, ThrownGiftVillageSearchRadius);
            if (nearbyVillageFuling == null) return;

            // v0.5.243: inspect actual loaded world drops around the player and bind a gift to the
            // Fuling that is physically closest to that drop. The old implementation only checked
            // drops around the single Fuling nearest to the player, so a perfectly valid gift thrown
            // to a different villager in the same camp could be ignored.
            ItemDrop bestDrop = null;
            VillageGift bestGift = null;
            Character bestFuling = null;
            float bestSqr = float.MaxValue;
            float searchRadius = ThrownGiftVillageSearchRadius + ThrownGiftSearchRadius;

            WorkerWorldIndex.ForEachItemDrop(player.transform.position, searchRadius, drop =>
            {
                if (drop == null || drop.m_itemData == null || drop.m_itemData.m_stack <= 0 || drop.m_itemData.m_dropPrefab == null) return;
                int instanceId = drop.GetInstanceID();
                if (ThrownGiftCooldowns.TryGetValue(instanceId, out float until) && until > Time.unscaledTime) return;

                string prefab = SpeciesRoleRegistry.NormalizePrefabName(drop.m_itemData.m_dropPrefab.name);
                VillageGift gift = FindVillageGift(prefab);
                if (gift == null) return;

                Character fuling = FindNearestVillageFuling(drop.transform.position, ThrownGiftSearchRadius);
                if (fuling == null) return;
                ZDO fulingZdo = GetZdo(fuling);
                if (fulingZdo == null || WorkerZdo.IsWorker(fulingZdo)) return;
                int candidateTrust = SynchronizeVillageTrust(fuling);
                if (candidateTrust >= FriendlyThreshold) return;

                float sqr = (drop.transform.position - fuling.transform.position).sqrMagnitude;
                if (bestGift != null && gift.Priority > bestGift.Priority) return;
                if (bestGift != null && gift.Priority == bestGift.Priority && sqr >= bestSqr) return;
                bestDrop = drop;
                bestGift = gift;
                bestFuling = fuling;
                bestSqr = sqr;
            });

            if (bestDrop == null || bestGift == null || bestFuling == null) return;
            int trust = SynchronizeVillageTrust(bestFuling);
            if (trust >= FriendlyThreshold) return;

            ThrownGiftCooldowns[bestDrop.GetInstanceID()] = Time.unscaledTime + ThrownGiftCooldown;
            if (!bestDrop.RemoveOne()) return;

            int next = Mathf.Clamp(trust + bestGift.Trust, 0, FriendlyThreshold);
            ApplyVillageTrust(bestFuling, next);
            Notify(player, $"Geschenk angenommen (+{bestGift.Trust}%) • Dorf-Vertrauen {next}%");

            string villageId = ResolveVillageId(bestFuling);
            Plugin.Log?.LogInfo($"Fuling thrown gift accepted: id={villageId}, gift={bestGift.Prefab}, trust={trust}->{next}, player={player.GetPlayerID()}.");
            if (next >= FriendlyThreshold)
                Notify(player, "Das Fuling-Dorf nimmt dich als Freund an!");
        }

        private static VillageGift FindVillageGift(string prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return null;
            for (int i = 0; i < VillageGifts.Length; i++)
                if (string.Equals(VillageGifts[i].Prefab, prefab, StringComparison.OrdinalIgnoreCase)) return VillageGifts[i];
            return null;
        }

        private static void ApplyVillageTrust(Character center, int trust)
        {
            if (center == null) return;
            int clamped = Mathf.Clamp(trust, 0, FriendlyThreshold);
            string villageId = ResolveVillageId(center);
            List<Character> members = CollectNearbyFulings(center.transform.position, VillageRadius);
            for (int i = 0; i < members.Count; i++)
            {
                ZDO memberZdo = GetZdo(members[i]);
                if (memberZdo == null || WorkerZdo.IsWorker(memberZdo)) continue;
                memberZdo.Set(VillageIdKey, villageId);
                memberZdo.Set(VillageTrustKey, clamped);
                if (clamped >= ToleratedThreshold)
                {
                    if (!members[i].IsTamed()) members[i].SetTamed(true);
                }
                else if (members[i].IsTamed())
                {
                    members[i].SetTamed(false);
                }
            }
        }

        internal static bool IsVillageFriendlyAt(Vector3 actionPosition)
        {
            Character center = FindNearestVillageFuling(actionPosition, VillageRadius);
            return center != null && SynchronizeVillageTrust(center) >= FriendlyThreshold;
        }

        internal static void PenalizeVillageAction(Vector3 actionPosition, Player player, int penalty, string reason, UnityEngine.Object actionObject)
        {
            if (player == null || penalty <= 0) return;
            Character center = FindNearestVillageFuling(actionPosition, VillageRadius);
            if (center == null) return;

            int current = SynchronizeVillageTrust(center);
            if (current <= 0) return;

            int cooldownId = actionObject != null ? actionObject.GetInstanceID() : 0;
            if (cooldownId != 0)
            {
                // Separate action classes use different penalty sizes, which keeps the same object's
                // harvest/open/damage events from suppressing one another accidentally.
                int key = unchecked(cooldownId * 397) ^ penalty;
                if (ActionPenaltyCooldown.TryGetValue(key, out float until) && until > Time.unscaledTime) return;
                ActionPenaltyCooldown[key] = Time.unscaledTime + 4f;
            }

            int next = Mathf.Clamp(current - penalty, 0, FriendlyThreshold);
            string villageId = ResolveVillageId(center);
            List<Character> members = CollectNearbyFulings(center.transform.position, VillageRadius);
            for (int i = 0; i < members.Count; i++)
            {
                ZDO memberZdo = GetZdo(members[i]);
                if (memberZdo == null || WorkerZdo.IsWorker(memberZdo)) continue;
                string memberVillage = memberZdo.GetString(VillageIdKey, string.Empty);
                if (!string.IsNullOrEmpty(memberVillage) && !string.Equals(memberVillage, villageId, StringComparison.Ordinal)) continue;
                memberZdo.Set(VillageIdKey, villageId);
                memberZdo.Set(VillageTrustKey, next);
                if (next < ToleratedThreshold && members[i].IsTamed()) members[i].SetTamed(false);
            }

            if (next < ToleratedThreshold && string.Equals(_activeVisitVillageId, villageId, StringComparison.Ordinal))
            {
                _activeVisitVillageId = string.Empty;
                _activeVisitWarned = false;
            }

            Notify(player, $"Dorf-Vertrauen -{penalty} ({reason}) • {next}/{FriendlyThreshold}");
            Plugin.Log?.LogInfo($"Fuling village trust penalty: id={villageId}, reason={reason}, penalty={penalty}, trust={current}->{next}, player={player.GetPlayerID()}.");
        }

        private static Character FindNearestVillageFuling(Vector3 center, float radius)
        {
            List<Character> loaded = WorkerCharacterIndexRuntime.GetLiveNonPlayers();
            Character best = null;
            float bestSqr = radius * radius;

            for (int i = 0; i < loaded.Count; i++)
            {
                Character character = loaded[i];
                if (!IsFuling(character)) continue;

                float sqr = (character.transform.position - center).sqrMagnitude;
                if (sqr > bestSqr) continue;

                ZDO zdo = GetZdo(character);
                if (zdo == null || WorkerZdo.IsWorker(zdo)) continue;

                best = character;
                bestSqr = sqr;
            }

            return best;
        }

        private static void SetVillageTamed(Vector3 center, bool tamed)
        {
            List<Character> members = CollectNearbyFulings(center, VillageRadius);
            for (int i = 0; i < members.Count; i++)
            {
                ZDO zdo = GetZdo(members[i]);
                if (zdo == null || WorkerZdo.IsWorker(zdo)) continue;
                int trust = Mathf.Clamp(zdo.GetInt(VillageTrustKey, 0), 0, FriendlyThreshold);
                if (trust >= FriendlyThreshold)
                {
                    if (!members[i].IsTamed()) members[i].SetTamed(true);
                    continue;
                }
                if (tamed && trust >= ToleratedThreshold)
                {
                    if (!members[i].IsTamed()) members[i].SetTamed(true);
                }
                else if (!tamed && members[i].IsTamed())
                {
                    members[i].SetTamed(false);
                }
            }
        }

        private static void EndToleratedVisit()
        {
            if (!string.IsNullOrEmpty(_activeVisitVillageId))
                SetVillageTamed(_activeVisitCenter, false);
            _activeVisitVillageId = string.Empty;
            _activeVisitWarned = false;
            _activeVisitStartedAt = 0f;
        }

        private static bool TryFindVillageGift(Inventory inventory, out ItemDrop.ItemData item, out VillageGift gift)
        {
            item = null;
            gift = null;
            if (inventory == null) return false;
            for (int p = 1; p <= 4; p++)
            {
                for (int i = 0; i < VillageGifts.Length; i++)
                {
                    if (VillageGifts[i].Priority != p) continue;
                    ItemDrop.ItemData found = FindItem(inventory, VillageGifts[i].Prefab);
                    if (found == null) continue;
                    item = found;
                    gift = VillageGifts[i];
                    return true;
                }
            }
            return false;
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

        private static string ResolveVillageId(Character character)
        {
            ZDO zdo = GetZdo(character);
            string existing = zdo != null ? zdo.GetString(VillageIdKey, string.Empty) : string.Empty;
            if (!string.IsNullOrEmpty(existing)) return existing;
            Vector3 p = character.transform.position;
            // 160 m cells are only a bootstrap identifier. As soon as the first gift is accepted the
            // same exact id is mirrored to all Fulings physically belonging to the local 85 m group.
            int x = Mathf.RoundToInt(p.x / 160f);
            int z = Mathf.RoundToInt(p.z / 160f);
            return $"plains_{x}_{z}";
        }

        private static ZDO GetZdo(Character character)
        {
            if (character == null) return null;
            ZNetView nview = character.GetComponent<ZNetView>();
            return nview != null && nview.IsValid() ? nview.GetZDO() : null;
        }

        private static string PersonalGiftPrefab(string species)
        {
            if (string.Equals(species, "Fuling_Shaman", StringComparison.OrdinalIgnoreCase)) return "Bread";
            if (string.Equals(species, "Fuling_Berserker", StringComparison.OrdinalIgnoreCase)) return "CookedLoxMeat";
            return "Cloudberry";
        }

        private static string PersonalGiftDisplay(string species)
        {
            if (string.Equals(species, "Fuling_Shaman", StringComparison.OrdinalIgnoreCase)) return "Brot";
            if (string.Equals(species, "Fuling_Berserker", StringComparison.OrdinalIgnoreCase)) return "gekochtes Loxfleisch";
            return "Moltebeeren";
        }

        private static string SpeciesDisplay(string species)
        {
            if (string.Equals(species, "Fuling_Shaman", StringComparison.OrdinalIgnoreCase)) return "Fuling-Schamane";
            if (string.Equals(species, "Fuling_Berserker", StringComparison.OrdinalIgnoreCase)) return "Fuling-Berserker";
            return "Fuling";
        }

        private static void Notify(Player player, string text)
        {
            if (player == null || string.IsNullOrEmpty(text)) return;
            try { player.Message(MessageHud.MessageType.Center, text); } catch { }
        }
    }

    [HarmonyPatch(typeof(Player), "Interact", typeof(GameObject), typeof(bool), typeof(bool))]
    internal static class FulingDiplomacyInteractPatch
    {
        [HarmonyPriority(Priority.High)]
        private static bool Prefix(Player __instance, GameObject go, bool hold, bool alt)
        {
            if (__instance == null || go == null) return true;
            Character character = go.GetComponentInParent<Character>();
            if (character == null || !FulingVillageDiplomacyRuntime.IsFuling(character)) return true;

            ZNetView nview = character.GetComponent<ZNetView>();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (WorkerZdo.IsWorker(zdo)) return true;

            bool handled = FulingVillageDiplomacyRuntime.TryHandleInteract(character, __instance, hold);
            if (!handled) return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Character), "GetHoverText")]
    internal static class FulingDiplomacyHoverPatch
    {
        [HarmonyPriority(Priority.High)]
        private static void Postfix(Character __instance, ref string __result)
        {
            if (__instance == null || !FulingVillageDiplomacyRuntime.IsFuling(__instance)) return;
            ZNetView nview = __instance.GetComponent<ZNetView>();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (WorkerZdo.IsWorker(zdo)) return;

            string diplomacy = FulingVillageDiplomacyRuntime.BuildHover(__instance);
            if (!string.IsNullOrEmpty(diplomacy)) __result = diplomacy;
        }
    }

    [HarmonyPatch(typeof(Player), "Interact", typeof(GameObject), typeof(bool), typeof(bool))]
    internal static class FulingVillageHarvestPenaltyPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(Player __instance, GameObject go, bool hold, bool alt, out bool __state)
        {
            __state = false;
            if (hold || __instance == null || go == null || __instance != Player.m_localPlayer) return;
            Pickable pickable = go.GetComponentInParent<Pickable>();
            if (pickable == null) return;
            try { __state = pickable.GetPicked(); }
            catch { __state = false; }
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Player __instance, GameObject go, bool hold, bool alt, bool __state)
        {
            // Player.Interact(GameObject,bool,bool) returns void in Valheim 1.0.16, so success has
            // to be inferred from the Pickable's real state transition. Merely pressing E on an
            // unripe/blocked/already-picked plant must never damage village trust.
            if (hold || __instance == null || go == null || __instance != Player.m_localPlayer) return;
            Pickable pickable = go.GetComponentInParent<Pickable>();
            if (pickable == null || __state) return;
            bool pickedNow;
            try { pickedNow = pickable.GetPicked(); }
            catch { return; }
            if (!pickedNow) return;

            // At full friendship the village shares normal harvests with the player.
            if (FulingVillageDiplomacyRuntime.IsVillageFriendlyAt(pickable.transform.position)) return;
            FulingVillageDiplomacyRuntime.PenalizeVillageAction(pickable.transform.position, __instance, 5, "Ernte genommen", pickable);
        }
    }

    [HarmonyPatch(typeof(Container), "Interact", typeof(Humanoid), typeof(bool), typeof(bool))]
    internal static class FulingVillageContainerPenaltyPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Container __instance, Humanoid character, bool hold, bool alt, bool __result)
        {
            if (!__result || hold || __instance == null || character == null) return;
            Player player = character as Player;
            if (player == null || player != Player.m_localPlayer) return;

            Piece piece = __instance.GetComponent<Piece>() ?? __instance.GetComponentInParent<Piece>();
            if (piece != null)
            {
                try
                {
                    long creator = piece.GetCreator();
                    if (creator != 0L && creator == player.GetPlayerID()) return; // own chest inside/near the village
                }
                catch { }
            }

            // At 100% village trust chest access is permitted. Property damage remains a trust violation.
            if (FulingVillageDiplomacyRuntime.IsVillageFriendlyAt(__instance.transform.position)) return;
            FulingVillageDiplomacyRuntime.PenalizeVillageAction(__instance.transform.position, player, 10, "Kiste geöffnet", __instance);
        }
    }

    [HarmonyPatch]
    internal static class FulingVillagePropertyDamagePenaltyPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            string[] names = { "Damage", "RPC_Damage" };
            return AccessTools.GetDeclaredMethods(typeof(WearNTear))
                .Where(method => names.Contains(method.Name) &&
                                 method.GetParameters().Any(parameter => parameter.ParameterType == typeof(HitData)))
                .Cast<MethodBase>();
        }

        [HarmonyPriority(Priority.Last)]
        private static void Prefix(WearNTear __instance, object[] __args)
        {
            if (__instance == null || __args == null) return;
            HitData hit = null;
            for (int i = 0; i < __args.Length; i++)
            {
                hit = __args[i] as HitData;
                if (hit != null) break;
            }
            if (hit == null) return;

            Character attacker;
            try { attacker = hit.GetAttacker(); }
            catch { return; }
            Player player = attacker as Player;
            if (player == null || player != Player.m_localPlayer) return;

            Piece piece = __instance.GetComponent<Piece>() ?? __instance.GetComponentInParent<Piece>();
            if (piece != null)
            {
                try
                {
                    long creator = piece.GetCreator();
                    if (creator != 0L && creator == player.GetPlayerID()) return;
                }
                catch { }
            }

            FulingVillageDiplomacyRuntime.PenalizeVillageAction(__instance.transform.position, player, 15, "Eigentum beschädigt", __instance);
        }
    }

}
