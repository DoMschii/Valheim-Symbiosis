using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Livestock feeding independent from SmartStorage.
    /// A Tierpflege-Schild inside the assigned workzone stores structured feed targets in its ZDO.
    /// The Butcher consumes real items from normal storage and drops protected world ItemDrops at
    /// the sign. Gatherers ignore those marked drops so the animals can eat them normally.
    /// v0.5.81 keeps the old sign-text parser only as a one-way compatibility fallback.
    /// </summary>
    internal static class LivestockFeedingRuntime
    {
        internal const string ProtectedFeedZdoKey = "vw_livestock_feed";
        internal const string FeedRulesZdoKey = "vw_livestock_feed_rules_v2";
        internal const string FeedRulesConfiguredZdoKey = "vw_livestock_feed_rules_set_v2";
        private const int MaxRules = 12;
        private const int DefaultTarget = 20;
        private const int MaxTarget = 200;
        private const int MaxTransferPerPass = 5;
        private const float RetrySeconds = 1.5f;
        private const float FeedCountRadius = 8f;
        private const float AutomaticFeedSliceSeconds = 0.40f;
        private const float AutomaticFeedReleaseCooldown = 1.25f;
        private const float AutomaticFeedAnimalRadius = 6f;
        private const int AutomaticFeedStationsPerSlice = 2;
        private static readonly Dictionary<ZDO, float> NextPassAt = new Dictionary<ZDO, float>();
        private static readonly Dictionary<int, float> NextStationReleaseAt = new Dictionary<int, float>();
        private static float NextAutomaticFeedCheckAt;
        private static int AutomaticFeedStationCursor;
        private static readonly List<LivestockFeedingStationMarker> AutomaticFeedStationScratch = new List<LivestockFeedingStationMarker>();
        private static readonly Dictionary<string, string[]> AcceptedFeedByCreature =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex LeadingCount = new Regex(@"^\s*(\d+)\s*[xX:]?\s*(.+?)\s*$", RegexOptions.Compiled);
        private static readonly Regex TrailingCount = new Regex(@"^\s*(.+?)\s*[xX:=]?\s*(\d+)\s*$", RegexOptions.Compiled);
        private static readonly Dictionary<string, string> ItemAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static ObjectDB _cachedDb;
        private static int _cachedSelectableDbItemCount = -1;
        private static int _cachedSelectableSceneItemCount = -1;
        private static readonly List<FeedItemOption> CachedSelectableItems = new List<FeedItemOption>();
        private static readonly Dictionary<int, FeedingStationCacheEntry> FeedingStationCache = new Dictionary<int, FeedingStationCacheEntry>();
        private static readonly List<int> StaleFeedingStationKeys = new List<int>();
        private static float NextFeedingStationCacheCleanupAt;

        private sealed class FeedingStationCacheEntry
        {
            internal Container Container;
            internal bool IsStation;
        }

        private sealed class ProtectedLivestockFeedDrop : MonoBehaviour { }

        private sealed class FeedRule
        {
            internal string Prefab;
            internal int Target;
            internal string Label;
        }

        internal sealed class FeedRuleSnapshot
        {
            internal string Prefab;
            internal string Label;
            internal int Target;
            internal int Current;
        }

        internal sealed class FeedItemOption
        {
            internal string Prefab;
            internal string Label;
        }

        internal static bool TryMaintain(Character worker, ZDO zdo, out string reason)
        {
            reason = string.Empty;
            if (worker == null || zdo == null) return false;
            if (!WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 center, out float radius)) return false;

            float now = Time.realtimeSinceStartup;
            if (NextPassAt.TryGetValue(zdo, out float next) && next > now) return false;
            NextPassAt[zdo] = now + RetrySeconds;

            // v0.5.194: SmartStorage owns the feeding prefabs, while Workers uses a real
            // feeding trough as the primary enclosure marker and fills its Container inventory.
            // The old Tierpflege sign remains a compatibility fallback for existing worlds.
            List<string> animalFood = LivestockButcherRuntime.GetAcceptedFoodPrefabs(zdo);
            LivestockFeedSignRegistry.TryFindNearest(center, radius, out LivestockFeedSignMarker sign);
            List<FeedRule> configuredRules = sign != null ? ReadRules(sign) : new List<FeedRule>();
            List<FeedRule> effectiveRules = BuildEffectiveRules(configuredRules, animalFood);

            Container penTrough = sign != null ? sign.GetContainer() : null;
            if (!IsFeedingTrough(penTrough)) penTrough = null;

            int stationCount = GetStationCount(zdo);
            if (stationCount > 0)
            {
                if (effectiveRules.Count == 0)
                {
                    reason = animalFood.Count == 0
                        ? "Futterstation vorhanden, aber keine passende Nahrung fuer gezaehmte Tiere erkannt"
                        : "Futterstation vorhanden, aber keine gueltigen Futterregeln";
                    return false;
                }

                if (TryMaintainFeedingStation(worker, zdo, center, radius, effectiveRules, penTrough, out reason))
                    return true;

                if (!string.IsNullOrEmpty(reason)) return false;
                reason = "Futtertrog/Futterspender ausreichend gefuellt";
                return false;
            }

            if (sign == null)
            {
                reason = "Kein Futtertrog/Futterspender in der Workzone";
                return false;
            }

            if (effectiveRules.Count == 0)
            {
                reason = configuredRules.Count == 0
                    ? "Gehege-Marker ohne Futterziele (Shift+E am Futtertrog)"
                    : "Gehege-Marker: kein konfiguriertes Futter passt zu den gezaehmten Tieren";
                return false;
            }

            for (int i = 0; i < effectiveRules.Count; i++)
            {
                FeedRule rule = effectiveRules[i];
                int current = CountProtectedFeed(sign.transform.position, rule.Prefab);
                int missing = Mathf.Max(0, rule.Target - current);
                if (missing <= 0) continue;

                int transfer = Mathf.Min(MaxTransferPerPass, missing);
                if (!WorkerStorageRuntime.TryConsumeBackgroundResource(worker, zdo, rule.Prefab, transfer))
                {
                    reason = $"Tierfutter fehlt im Lager: {rule.Label}";
                    continue;
                }

                if (!TrySpawnProtectedFeed(sign, rule.Prefab, transfer))
                {
                    // Fail closed: do not silently duplicate; the storage consume already happened,
                    // so immediately return the same amount to the Worker's persistent cargo. The
                    // shared cargo system will route it back instead of losing the items.
                    WorkerZdo.AddCargoAmount(zdo, rule.Prefab, transfer);
                    reason = $"Tierfutter konnte nicht ausgeworfen werden: {rule.Label}";
                    return false;
                }

                reason = $"Tierpflege: {rule.Label} x{transfer} ins Gehege ({current + transfer}/{rule.Target})";
                return true;
            }

            reason = "Futterbestand am Gehege-Marker erreicht";
            return false;
        }

        internal static void ClearCaches()
        {
            NextPassAt.Clear();
            NextStationReleaseAt.Clear();
            NextAutomaticFeedCheckAt = 0f;
            AutomaticFeedStationCursor = 0;
            AutomaticFeedStationScratch.Clear();
            AcceptedFeedByCreature.Clear();
            FeedingStationCache.Clear();
            StaleFeedingStationKeys.Clear();
            NextFeedingStationCacheCleanupAt = 0f;
            CachedSelectableItems.Clear();
            _cachedDb = null;
            _cachedSelectableDbItemCount = -1;
            _cachedSelectableSceneItemCount = -1;
            ItemAliases.Clear();
        }


        internal static void TickAutomaticFeedRelease()
        {
            float now = Time.realtimeSinceStartup;
            if (now < NextAutomaticFeedCheckAt) return;
            NextAutomaticFeedCheckAt = now + AutomaticFeedSliceSeconds;

            long perfStart = WorkersPerformanceProfiler.Begin();
            try
            {
                LivestockFeedingStationRegistry.CopyLoaded(AutomaticFeedStationScratch);
                int stationCount = AutomaticFeedStationScratch.Count;
                if (stationCount == 0)
                {
                    AutomaticFeedStationCursor = 0;
                    return;
                }

                if (AutomaticFeedStationCursor < 0 || AutomaticFeedStationCursor >= stationCount)
                    AutomaticFeedStationCursor = 0;

                List<Character> automaticFeedAnimals = WorkerCharacterIndexRuntime.GetTamedNonWorkers();
                if (automaticFeedAnimals.Count == 0) return;

                int inspected = 0;
                int processed = 0;
                while (inspected < stationCount && processed < AutomaticFeedStationsPerSlice)
                {
                    if (AutomaticFeedStationCursor >= stationCount) AutomaticFeedStationCursor = 0;
                    LivestockFeedingStationMarker stationMarker = AutomaticFeedStationScratch[AutomaticFeedStationCursor++];
                    inspected++;
                    processed++;

                    if (stationMarker == null) continue;
                    Container station = stationMarker.GetContainer();
                    if (station == null || !station.gameObject.activeInHierarchy) continue;

                    int stationKey = station.GetInstanceID();
                    if (NextStationReleaseAt.TryGetValue(stationKey, out float releaseAt) && releaseAt > now) continue;

                    Inventory inventory;
                    try { inventory = station.GetInventory(); }
                    catch { continue; }
                    if (inventory == null || inventory.GetAllItems() == null || inventory.GetAllItems().Count == 0) continue;

                    if (!TryResolvePenMarkerForStation(station, out LivestockFeedSignMarker penMarker)) continue;
                    HashSet<string> enabledFood = GetEnabledFeedSet(penMarker);
                    if (enabledFood.Count == 0) continue;

                    Character hungryAnimal = null;
                    HashSet<string> accepted = null;
                    float radiusSqr = AutomaticFeedAnimalRadius * AutomaticFeedAnimalRadius;

                    for (int i = 0; i < automaticFeedAnimals.Count; i++)
                    {
                        Character animal = automaticFeedAnimals[i];
                        if (animal == null || animal.gameObject == null || !animal.gameObject.activeInHierarchy || animal.IsDead()) continue;

                        Vector3 delta = animal.transform.position - station.transform.position;
                        delta.y = 0f;
                        if (delta.sqrMagnitude > radiusSqr) continue;

                        MonsterAI ai = animal.GetComponent<MonsterAI>() ?? animal.GetComponentInChildren<MonsterAI>(true);
                        if (ai == null || !TryReadHungryState(animal, ai, out bool hungry) || !hungry) continue;

                        HashSet<string> animalFoods = GetAcceptedFeedSet(ai);
                        if (animalFoods.Count == 0) continue;
                        animalFoods.IntersectWith(enabledFood);
                        if (animalFoods.Count == 0) continue;
                        if (!InventoryContainsAny(inventory, animalFoods)) continue;

                        hungryAnimal = animal;
                        accepted = animalFoods;
                        break;
                    }

                    if (hungryAnimal == null || accepted == null) continue;
                    if (!TrySelectStationFeed(inventory, accepted, out ItemDrop.ItemData sourceItem, out string prefab)) continue;

                    if (CountProtectedFeed(station.transform.position, prefab) > 0)
                    {
                        NextStationReleaseAt[stationKey] = now + AutomaticFeedReleaseCooldown;
                        continue;
                    }

                    if (!WorkerStorageRuntime.TryOwn(station)) continue;

                    ItemDrop.ItemData one = sourceItem.Clone();
                    one.m_stack = 1;
                    if (!WorkerStorageRuntime.TryRemoveOne(inventory, sourceItem)) continue;

                    if (!TrySpawnProtectedFeedAt(station.transform, prefab, 1))
                    {
                        WorkerStorageRuntime.TryAddItem(inventory, one);
                        WorkerStorageRuntime.PersistContainer(station, inventory);
                        NextStationReleaseAt[stationKey] = now + AutomaticFeedReleaseCooldown;
                        continue;
                    }

                    WorkerStorageRuntime.PersistContainer(station, inventory);
                    NextStationReleaseAt[stationKey] = now + AutomaticFeedReleaseCooldown;
                }
            }
            finally
            {
                WorkersPerformanceProfiler.End(WorkersPerfBucket.LivestockScan, perfStart);
            }
        }

        private static bool TryReadHungryState(Character animal, MonsterAI ai, out bool hungry)
        {
            hungry = false;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            object[] targets =
            {
                animal != null ? animal.GetComponent<Tameable>() : null,
                ai,
                animal
            };

            for (int i = 0; i < targets.Length; i++)
            {
                object target = targets[i];
                if (target == null) continue;
                try
                {
                    MethodInfo method = target.GetType().GetMethod("IsHungry", flags, null, Type.EmptyTypes, null);
                    if (method == null || method.ReturnType != typeof(bool)) continue;
                    hungry = (bool)method.Invoke(target, null);
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static HashSet<string> GetAcceptedFeedSet(MonsterAI ai)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ai == null) return result;

            string creatureKey = SpeciesRoleRegistry.NormalizePrefabName(ai.gameObject != null ? ai.gameObject.name : string.Empty);
            if (string.IsNullOrWhiteSpace(creatureKey)) creatureKey = ai.GetType().FullName ?? ai.GetType().Name;

            if (AcceptedFeedByCreature.TryGetValue(creatureKey, out string[] cached))
            {
                for (int i = 0; i < cached.Length; i++)
                    if (!string.IsNullOrWhiteSpace(cached[i])) result.Add(cached[i]);
                return result;
            }

            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo field = ai.GetType().GetField("m_consumeItems", flags);
                IEnumerable consume = field?.GetValue(ai) as IEnumerable;
                if (consume != null)
                {
                    foreach (object entry in consume)
                    {
                        string prefab = string.Empty;
                        if (entry is ItemDrop drop && drop != null) prefab = SpeciesRoleRegistry.NormalizePrefabName(drop.gameObject.name);
                        else if (entry is GameObject go && go != null) prefab = SpeciesRoleRegistry.NormalizePrefabName(go.name);
                        else if (entry is ItemDrop.ItemData data && data != null) prefab = WorkerStorageRuntime.GetPrefabName(data);
                        if (!string.IsNullOrWhiteSpace(prefab)) result.Add(prefab);
                    }
                }
            }
            catch { }

            string[] snapshot = new string[result.Count];
            result.CopyTo(snapshot);
            AcceptedFeedByCreature[creatureKey] = snapshot;
            return result;
        }

        private static bool InventoryContainsAny(Inventory inventory, HashSet<string> accepted)
        {
            if (inventory == null || accepted == null || accepted.Count == 0) return false;
            try
            {
                List<ItemDrop.ItemData> items = inventory.GetAllItems();
                for (int i = 0; i < items.Count; i++)
                {
                    ItemDrop.ItemData item = items[i];
                    if (item == null || item.m_stack <= 0) continue;
                    string prefab = WorkerStorageRuntime.GetPrefabName(item);
                    if (accepted.Contains(prefab)) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool TrySelectStationFeed(Inventory inventory, HashSet<string> accepted, out ItemDrop.ItemData item, out string prefab)
        {
            item = null;
            prefab = string.Empty;
            if (inventory == null || accepted == null || accepted.Count == 0) return false;
            try
            {
                List<ItemDrop.ItemData> items = inventory.GetAllItems();
                for (int i = 0; i < items.Count; i++)
                {
                    ItemDrop.ItemData candidate = items[i];
                    if (candidate == null || candidate.m_stack <= 0) continue;
                    string candidatePrefab = WorkerStorageRuntime.GetPrefabName(candidate);
                    if (!accepted.Contains(candidatePrefab)) continue;
                    item = candidate;
                    prefab = candidatePrefab;
                    return true;
                }
            }
            catch { }
            return false;
        }

        internal static bool IsProtectedFeedDrop(ItemDrop drop)
        {
            if (drop == null || drop.gameObject == null) return false;
            if (drop.GetComponent<ProtectedLivestockFeedDrop>() != null) return true;
            try
            {
                ZNetView view = drop.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                return zdo != null && zdo.GetBool(ProtectedFeedZdoKey, false);
            }
            catch { return false; }
        }

        internal static string GetDebug(ZDO zdo)
        {
            if (zdo == null || !WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 center, out float radius))
                return "Tierpflege: keine Workzone";

            int stations = GetStationCount(zdo);
            int signs = LivestockFeedSignRegistry.CountInside(center, radius);
            List<string> animalFood = LivestockButcherRuntime.GetAcceptedFoodPrefabs(zdo);
            string accepted = animalFood.Count == 0 ? "-" : string.Join(", ", animalFood.ToArray());

            if (stations > 0)
                return $"Tierpflege: Futterstationen {stations} | Tierfutter {accepted}";

            if (!LivestockFeedSignRegistry.TryFindNearest(center, radius, out LivestockFeedSignMarker sign) || sign == null)
                return $"Tierpflege: Futterstationen 0 | Marker {signs} | kein aktiver Futterpunkt";

            List<FeedRule> rules = BuildEffectiveRules(ReadRules(sign), animalFood);
            if (rules.Count == 0) return $"Gehege-Marker: keine passenden Futterziele | Tierfutter {accepted}";
            List<string> parts = new List<string>();
            for (int i = 0; i < rules.Count; i++)
            {
                FeedRule rule = rules[i];
                parts.Add($"{rule.Label} {CountFeedAtMarker(sign, rule.Prefab)}/{rule.Target}");
            }
            return $"Gehege-Marker: {string.Join(" | ", parts.ToArray())}";
        }

        internal static int GetStationCount(ZDO zdo)
        {
            if (zdo == null || !WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 center, out float radius)) return 0;
            int count = 0;
            WorkerStorageRuntime.ForEachContainerNear(center, Mathf.Max(5f, radius), candidate =>
            {
                if (IsFeedingStation(candidate)) count++;
            });
            return count;
        }

        private static List<FeedRule> BuildEffectiveRules(List<FeedRule> configured, List<string> acceptedFood)
        {
            List<FeedRule> result = new List<FeedRule>();
            HashSet<string> accepted = new HashSet<string>(acceptedFood ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (configured != null)
            {
                for (int i = 0; i < configured.Count && result.Count < MaxRules; i++)
                {
                    FeedRule rule = configured[i];
                    if (rule == null || string.IsNullOrWhiteSpace(rule.Prefab) || rule.Target <= 0) continue;
                    if (accepted.Count > 0 && !accepted.Contains(rule.Prefab)) continue;
                    if (!seen.Add(rule.Prefab)) continue;
                    result.Add(new FeedRule { Prefab = rule.Prefab, Target = rule.Target, Label = GetItemDisplayName(rule.Prefab) });
                }
            }

            // v0.5.202: strict whitelist. Nothing is stocked automatically just because a nearby
            // tameable can eat it. The player must explicitly enable every food type in the pen menu.
            // An empty list therefore means: do not stock or dispense any food.
            return result;
        }

        private static bool TryMaintainFeedingStation(Character worker, ZDO zdo, Vector3 center, float radius, List<FeedRule> rules, Container preferredTrough, out string reason)
        {
            reason = string.Empty;
            if (worker == null || zdo == null || rules == null || rules.Count == 0) return false;
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);

            Container bestStation = null;
            FeedRule bestRule = null;
            int bestCurrent = 0;
            int bestMissing = 0;
            float bestDistance = float.MaxValue;

            WorkerStorageRuntime.ForEachContainerNear(center, Mathf.Max(5f, radius), candidate =>
            {
                if (!IsFeedingStation(candidate) || !WorkerStorageRuntime.CanUseContainer(candidate, ownerId)) return;
                Inventory inv = candidate.GetInventory();
                if (inv == null) return;

                // v0.5.202: a feeding station may hold several explicitly enabled foods. Pick the
                // selected rule with the largest shortage instead of locking the station to whichever
                // food type happened to be inserted first.
                FeedRule selected = null;
                int current = 0;
                int missing = 0;
                for (int i = 0; i < rules.Count; i++)
                {
                    FeedRule rule = rules[i];
                    if (rule == null || rule.Target <= 0) continue;
                    int ruleCurrent = WorkerStorageRuntime.CountPrefab(inv, rule.Prefab);
                    int ruleMissing = Mathf.Max(0, rule.Target - ruleCurrent);
                    if (ruleMissing <= 0) continue;
                    int ruleCapacity = WorkerStorageRuntime.GetAvailableCapacityForPrefab(inv, rule.Prefab);
                    if (ruleCapacity <= 0) continue;
                    if (selected != null && ruleMissing <= missing) continue;
                    selected = rule;
                    current = ruleCurrent;
                    missing = ruleMissing;
                }
                if (selected == null) return;

                float sqr = (candidate.transform.position - worker.transform.position).sqrMagnitude;
                bool candidatePreferred = preferredTrough != null && candidate == preferredTrough;
                bool bestPreferred = preferredTrough != null && bestStation == preferredTrough;
                if (bestStation != null)
                {
                    if (bestPreferred && !candidatePreferred) return;
                    if (!candidatePreferred && (missing < bestMissing || (missing == bestMissing && sqr >= bestDistance))) return;
                }
                bestStation = candidate;
                bestRule = selected;
                bestCurrent = current;
                bestMissing = missing;
                bestDistance = sqr;
            });

            if (bestStation == null || bestRule == null) return false;

            // For an empty station, the first configured feed might not exist in storage. Probe rules in
            // priority order until one has a real movable source stack.
            Container source = FindFoodSource(worker, zdo, bestStation, bestRule.Prefab, ownerId, rules, out FeedRule selectedRule, out int movable);
            if (source == null || selectedRule == null || movable <= 0)
            {
                reason = $"Tierfutter fehlt im Lager fuer {DisplayStationName(bestStation)}";
                return false;
            }

            Inventory targetInventory = bestStation.GetInventory();
            Inventory sourceInventory = source.GetInventory();
            if (targetInventory == null || sourceInventory == null) return false;
            if (!WorkerStorageRuntime.TryOwn(bestStation) || !WorkerStorageRuntime.TryOwn(source))
            {
                reason = "Futterstation/Lager derzeit nicht beschreibbar";
                return false;
            }

            int currentSelected = WorkerStorageRuntime.CountPrefab(targetInventory, selectedRule.Prefab);
            int need = Mathf.Max(0, selectedRule.Target - currentSelected);
            int capacitySelected = WorkerStorageRuntime.GetAvailableCapacityForPrefab(targetInventory, selectedRule.Prefab);
            int transfer = Mathf.Min(MaxTransferPerPass, need, movable, capacitySelected);
            if (transfer <= 0) return false;

            int moved = TransferTransactional(source, bestStation, selectedRule.Prefab, transfer);
            if (moved <= 0)
            {
                reason = $"Futtertransfer blockiert: {selectedRule.Label}";
                return false;
            }

            reason = $"Tierpflege: {selectedRule.Label} x{moved} -> {DisplayStationName(bestStation)} ({currentSelected + moved}/{selectedRule.Target})";
            return true;
        }

        internal static bool IsFeedingTrough(Container container)
        {
            if (container == null) return false;
            LivestockFeedingStationMarker marker = container.GetComponent<LivestockFeedingStationMarker>()
                ?? container.GetComponentInParent<LivestockFeedingStationMarker>();
            if (marker != null) return marker.IsTrough;

            string[] tokens = { "feedingtrough", "feedtrough", "trough", "futtertrog" };
            Func<string, bool> matches = value =>
            {
                if (string.IsNullOrEmpty(value)) return false;
                string normalized = value.Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
                for (int i = 0; i < tokens.Length; i++) if (normalized.Contains(tokens[i])) return true;
                return false;
            };
            Transform t = container.transform;
            for (int depth = 0; t != null && depth < 4; depth++, t = t.parent)
                if (matches(t.name)) return true;
            try
            {
                Piece piece = container.GetComponentInParent<Piece>();
                if (piece != null && matches(piece.gameObject.name)) return true;
            }
            catch { }
            return false;
        }

        internal static bool IsFeedingStation(Container container)
        {
            if (container == null) return false;
            LivestockFeedingStationMarker marker = container.GetComponent<LivestockFeedingStationMarker>()
                ?? container.GetComponentInParent<LivestockFeedingStationMarker>();
            if (marker != null) return true;

            int key = container.GetInstanceID();
            if (FeedingStationCache.TryGetValue(key, out FeedingStationCacheEntry cached) && cached != null && cached.Container == container)
                return cached.IsStation;

            bool result = ComputeIsFeedingStation(container);
            FeedingStationCache[key] = new FeedingStationCacheEntry { Container = container, IsStation = result };
            CleanupFeedingStationCacheIfDue();
            return result;
        }

        private static bool ComputeIsFeedingStation(Container container)
        {
            if (container == null) return false;
            string[] tokens =
            {
                "feedingtrough", "feedtrough", "trough", "futtertrog",
                "feedingdispenser", "feeddispenser", "dispenser", "futterspender"
            };

            Func<string, bool> matches = value =>
            {
                if (string.IsNullOrEmpty(value)) return false;
                string normalized = value.Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
                for (int i = 0; i < tokens.Length; i++) if (normalized.Contains(tokens[i])) return true;
                return false;
            };

            Transform t = container.transform;
            for (int depth = 0; t != null && depth < 4; depth++, t = t.parent)
                if (matches(t.name)) return true;

            try
            {
                Piece piece = container.GetComponentInParent<Piece>();
                if (piece != null && matches(piece.gameObject.name)) return true;
            }
            catch { }

            // Component-name fallback only runs once per live Container because the result is cached.
            // This keeps compatibility with SmartStorage implementation changes without a 1.5-second
            // GetComponentsInChildren hot path across every storage container in the base.
            try
            {
                Component[] components = container.GetComponentsInParent<Component>(true);
                for (int i = 0; i < components.Length; i++)
                {
                    Component component = components[i];
                    if (component == null) continue;
                    string typeName = component.GetType().FullName ?? component.GetType().Name;
                    if (matches(typeName)) return true;
                }
                components = container.GetComponentsInChildren<Component>(true);
                for (int i = 0; i < components.Length; i++)
                {
                    Component component = components[i];
                    if (component == null) continue;
                    string typeName = component.GetType().FullName ?? component.GetType().Name;
                    if (matches(typeName)) return true;
                }
            }
            catch { }
            return false;
        }

        private static void CleanupFeedingStationCacheIfDue()
        {
            float now = Time.realtimeSinceStartup;
            if (now < NextFeedingStationCacheCleanupAt && FeedingStationCache.Count < 2048) return;
            NextFeedingStationCacheCleanupAt = now + 30f;
            StaleFeedingStationKeys.Clear();
            foreach (KeyValuePair<int, FeedingStationCacheEntry> pair in FeedingStationCache)
            {
                FeedingStationCacheEntry entry = pair.Value;
                if (entry == null || entry.Container == null || entry.Container.gameObject == null)
                    StaleFeedingStationKeys.Add(pair.Key);
            }
            for (int i = 0; i < StaleFeedingStationKeys.Count; i++) FeedingStationCache.Remove(StaleFeedingStationKeys[i]);
            StaleFeedingStationKeys.Clear();
        }

        private static Container FindFoodSource(Character worker, ZDO zdo, Container feeder, string preferredFood, long ownerId, List<FeedRule> rules, out FeedRule selectedRule, out int movable)
        {
            selectedRule = null;
            movable = 0;
            if (worker == null || zdo == null || rules == null || rules.Count == 0) return null;

            List<FeedRule> ordered = new List<FeedRule>(rules);
            if (!string.IsNullOrEmpty(preferredFood))
            {
                int preferredIndex = ordered.FindIndex(r => string.Equals(r.Prefab, preferredFood, StringComparison.OrdinalIgnoreCase));
                if (preferredIndex > 0)
                {
                    FeedRule preferred = ordered[preferredIndex];
                    ordered.RemoveAt(preferredIndex);
                    ordered.Insert(0, preferred);
                }
            }

            Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
            float homeRadius = Mathf.Max(WorkerStorageRuntime.MinStorageSearchRadius, WorkerStorageRuntime.LogisticsNetworkRadius);
            bool hasArea = WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 workCenter, out float workRadius);

            for (int foodIndex = 0; foodIndex < ordered.Count; foodIndex++)
            {
                FeedRule rule = ordered[foodIndex];
                Container best = null;
                int bestMovable = 0;
                float bestSqr = float.MaxValue;

                Action<Container> consider = candidate =>
                {
                    if (candidate == null || candidate == feeder || IsFeedingStation(candidate)) return;
                    if (!WorkerStorageRuntime.CanUseContainer(candidate, ownerId)) return;
                    WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(candidate);
                    if (role == WorkerContainerRole.Import || role == WorkerContainerRole.Export) return;
                    Inventory inv = candidate.GetInventory();
                    if (inv == null) return;
                    int present = WorkerStorageRuntime.CountPrefab(inv, rule.Prefab);
                    int keep = WorkerContainerRoleRuntime.ProtectLastItem(candidate) ? 1 : 0;
                    int available = Mathf.Max(0, present - keep);
                    if (available <= 0) return;
                    float sqr = (candidate.transform.position - worker.transform.position).sqrMagnitude;
                    if (best != null && sqr >= bestSqr) return;
                    best = candidate;
                    bestMovable = available;
                    bestSqr = sqr;
                };

                WorkerStorageRuntime.ForEachContainerNear(home, homeRadius, consider);
                if (hasArea) WorkerStorageRuntime.ForEachContainerNear(workCenter, Mathf.Max(5f, workRadius), consider);
                if (best != null)
                {
                    selectedRule = rule;
                    movable = bestMovable;
                    return best;
                }
            }
            return null;
        }

        private static int TransferTransactional(Container source, Container target, string prefab, int amount)
        {
            if (source == null || target == null || string.IsNullOrWhiteSpace(prefab) || amount <= 0) return 0;
            Inventory src = source.GetInventory();
            Inventory dst = target.GetInventory();
            if (src == null || dst == null) return 0;

            int moved = 0;
            for (int i = 0; i < amount; i++)
            {
                ItemDrop.ItemData original = WorkerStorageRuntime.FindPrefab(src, prefab);
                if (original == null) break;
                ItemDrop.ItemData one = original.Clone();
                one.m_stack = 1;

                if (!WorkerStorageRuntime.TryRemoveOne(src, original)) break;
                if (!WorkerStorageRuntime.TryAddItem(dst, one))
                {
                    WorkerStorageRuntime.TryAddItem(src, one);
                    break;
                }
                moved++;
            }

            if (moved > 0)
            {
                WorkerStorageRuntime.PersistContainer(source, src);
                WorkerStorageRuntime.PersistContainer(target, dst);
            }
            return moved;
        }

        private static string DisplayStationName(Container container)
        {
            if (container == null) return "Futterstation";
            string name = container.gameObject != null ? container.gameObject.name : string.Empty;
            return string.IsNullOrEmpty(name) ? "Futterstation" : SpeciesRoleRegistry.NormalizePrefabName(name);
        }

        internal static List<FeedRuleSnapshot> GetConfiguredRules(LivestockFeedSignMarker sign)
        {
            List<FeedRuleSnapshot> result = new List<FeedRuleSnapshot>();
            if (sign == null) return result;
            List<FeedRule> rules = ReadRules(sign);
            for (int i = 0; i < rules.Count; i++)
            {
                FeedRule rule = rules[i];
                result.Add(new FeedRuleSnapshot
                {
                    Prefab = rule.Prefab,
                    Label = GetItemDisplayName(rule.Prefab),
                    Target = rule.Target,
                    Current = CountFeedAtMarker(sign, rule.Prefab)
                });
            }
            return result;
        }

        internal static bool IsFeedEnabled(LivestockFeedSignMarker sign, string prefab)
        {
            if (sign == null || string.IsNullOrWhiteSpace(prefab)) return false;
            List<FeedRule> rules = ReadRules(sign);
            for (int i = 0; i < rules.Count; i++)
                if (rules[i] != null && rules[i].Target > 0 && string.Equals(rules[i].Prefab, prefab, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        internal static HashSet<string> GetEnabledFeedSet(LivestockFeedSignMarker sign)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sign == null) return result;
            List<FeedRule> rules = ReadRules(sign);
            for (int i = 0; i < rules.Count; i++)
            {
                FeedRule rule = rules[i];
                if (rule != null && rule.Target > 0 && !string.IsNullOrWhiteSpace(rule.Prefab)) result.Add(rule.Prefab);
            }
            return result;
        }

        internal static bool SetFeedEnabled(LivestockFeedSignMarker sign, string prefab, bool enabled, out string error)
        {
            int target = enabled ? DefaultTarget : 0;
            if (enabled)
            {
                List<FeedRule> existing = ReadRules(sign);
                FeedRule rule = existing.Find(r => r != null && string.Equals(r.Prefab, prefab, StringComparison.OrdinalIgnoreCase));
                if (rule != null && rule.Target > 0) target = rule.Target;
            }
            return SetFeedTarget(sign, prefab, target, out error);
        }

        private static bool TryResolvePenMarkerForStation(Container station, out LivestockFeedSignMarker marker)
        {
            marker = null;
            if (station == null) return false;
            if (IsFeedingTrough(station))
            {
                marker = station.GetComponent<LivestockFeedSignMarker>()
                    ?? station.GetComponentInParent<LivestockFeedSignMarker>();
                return marker != null;
            }
            return LivestockFeedSignRegistry.TryFindNearest(station.transform.position, 40f, out marker) && marker != null;
        }

        private static int CountFeedAtMarker(LivestockFeedSignMarker marker, string prefab)
        {
            if (marker == null || string.IsNullOrWhiteSpace(prefab)) return 0;
            Container trough = marker.GetContainer();
            if (IsFeedingTrough(trough))
            {
                try
                {
                    Inventory inv = trough.GetInventory();
                    if (inv != null) return WorkerStorageRuntime.CountPrefab(inv, prefab);
                }
                catch { }
            }
            return CountProtectedFeed(marker.transform.position, prefab);
        }

        internal static bool SetFeedTarget(LivestockFeedSignMarker sign, string prefab, int target, out string error)
        {
            error = string.Empty;
            if (sign == null || string.IsNullOrWhiteSpace(prefab)) { error = "Ungueltiger Futtereintrag"; return false; }
            GameObject item = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null;
            if (item == null || item.GetComponent<ItemDrop>() == null) { error = "Item nicht im ObjectDB gefunden"; return false; }
            if (!TryGetWritableSignZdo(sign, out ZDO zdo)) { error = "Futtertrog/Gehege-Marker ist nicht beschreibbar"; return false; }

            List<FeedRule> rules = ReadRules(sign);
            int clamped = Mathf.Clamp(target, 0, MaxTarget);
            FeedRule existing = rules.Find(r => string.Equals(r.Prefab, prefab, StringComparison.OrdinalIgnoreCase));
            if (clamped <= 0)
            {
                rules.RemoveAll(r => string.Equals(r.Prefab, prefab, StringComparison.OrdinalIgnoreCase));
            }
            else if (existing != null)
            {
                existing.Target = clamped;
                existing.Label = GetItemDisplayName(prefab);
            }
            else
            {
                if (rules.Count >= MaxRules) { error = $"Maximal {MaxRules} Futtereintraege"; return false; }
                rules.Add(new FeedRule { Prefab = prefab, Target = clamped, Label = GetItemDisplayName(prefab) });
            }

            SaveStructuredRules(zdo, rules);
            return true;
        }

        internal static List<FeedItemOption> GetSelectableFeedItems(string search, int maxResults = 80)
        {
            EnsureSelectableItemCache();
            string needle = NormalizeLabel(search ?? string.Empty);
            List<FeedItemOption> result = new List<FeedItemOption>();
            for (int i = 0; i < CachedSelectableItems.Count && result.Count < Mathf.Max(1, maxResults); i++)
            {
                FeedItemOption option = CachedSelectableItems[i];
                if (!string.IsNullOrEmpty(needle) &&
                    !NormalizeLabel(option.Label).Contains(needle) &&
                    !NormalizeLabel(option.Prefab).Contains(needle)) continue;
                result.Add(option);
            }
            return result;
        }

        internal static List<FeedItemOption> GetNearbyAcceptedFeedItems(LivestockFeedSignMarker sign, float radius = 80f)
        {
            Dictionary<string, FeedItemOption> found = new Dictionary<string, FeedItemOption>(StringComparer.OrdinalIgnoreCase);
            if (sign == null) return new List<FeedItemOption>();
            float rSqr = Mathf.Max(5f, radius) * Mathf.Max(5f, radius);
            try
            {
                List<Character> chars = WorkerCharacterIndexRuntime.GetTamedNonWorkers();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                for (int i = 0; i < chars.Count; i++)
                {
                    Character animal = chars[i];
                    if (animal == null || animal.IsDead()) continue;
                    if ((animal.transform.position - sign.transform.position).sqrMagnitude > rSqr) continue;

                    MonsterAI ai = animal.GetComponent<MonsterAI>() ?? animal.GetComponentInChildren<MonsterAI>(true);
                    if (ai == null) continue;
                    FieldInfo field = ai.GetType().GetField("m_consumeItems", flags);
                    IEnumerable consume = field?.GetValue(ai) as IEnumerable;
                    if (consume == null) continue;
                    foreach (object entry in consume)
                    {
                        string prefab = string.Empty;
                        if (entry is ItemDrop drop && drop != null) prefab = SpeciesRoleRegistry.NormalizePrefabName(drop.gameObject.name);
                        else if (entry is GameObject go && go != null) prefab = SpeciesRoleRegistry.NormalizePrefabName(go.name);
                        else if (entry is ItemDrop.ItemData data && data != null) prefab = WorkerStorageRuntime.GetPrefabName(data);
                        if (string.IsNullOrWhiteSpace(prefab) || found.ContainsKey(prefab)) continue;
                        found[prefab] = new FeedItemOption { Prefab = prefab, Label = GetItemDisplayName(prefab) };
                    }
                }
            }
            catch { }

            List<FeedItemOption> result = new List<FeedItemOption>(found.Values);
            result.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        internal static string GetItemDisplayName(string prefab)
        {
            if (string.IsNullOrWhiteSpace(prefab)) return string.Empty;
            try
            {
                GameObject go = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null;
                if (go == null && ZNetScene.instance != null)
                {
                    try { go = ZNetScene.instance.GetPrefab(prefab); } catch { }
                }
                ItemDrop drop = go != null ? go.GetComponent<ItemDrop>() : null;
                string key = drop?.m_itemData?.m_shared?.m_name;
                string localized = TryLocalize(key);
                if (!string.IsNullOrWhiteSpace(localized) && !localized.StartsWith("$", StringComparison.Ordinal)) return localized;
                if (!string.IsNullOrWhiteSpace(key) && !key.StartsWith("$", StringComparison.Ordinal)) return key;
            }
            catch { }
            return prefab;
        }

        private static List<FeedRule> ReadRules(LivestockFeedSignMarker sign)
        {
            List<FeedRule> structured = ReadStructuredRules(sign);
            if (HasStructuredRuleState(sign)) return structured;

            // v0.5.80 compatibility: if somebody already typed rules into the vanilla sign,
            // keep accepting them until the first structured menu edit is saved.
            List<FeedRule> result = new List<FeedRule>();
            string text = ReadSignText(sign);
            if (string.IsNullOrWhiteSpace(text)) return result;

            EnsureItemAliases();
            string[] entries = text.Replace("\r", "\n").Split(new[] { ',', ';', '\n', '|' }, StringSplitOptions.RemoveEmptyEntries);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Length; i++)
            {
                string entry = StripRichText(entries[i]).Trim();
                if (entry.Length == 0) continue;

                int target = DefaultTarget;
                string label = entry;
                Match match = LeadingCount.Match(entry);
                if (match.Success)
                {
                    int.TryParse(match.Groups[1].Value, out target);
                    label = match.Groups[2].Value.Trim();
                }
                else
                {
                    match = TrailingCount.Match(entry);
                    if (match.Success)
                    {
                        label = match.Groups[1].Value.Trim();
                        int.TryParse(match.Groups[2].Value, out target);
                    }
                }

                target = Mathf.Clamp(target <= 0 ? 0 : target, 0, MaxTarget);
                if (target <= 0 || string.IsNullOrWhiteSpace(label)) continue;
                if (!TryResolveItem(label, out string prefab)) continue;
                if (!seen.Add(prefab)) continue;
                result.Add(new FeedRule { Prefab = prefab, Target = target, Label = GetItemDisplayName(prefab) });
                if (result.Count >= MaxRules) break;
            }
            return result;
        }

        private static List<FeedRule> ReadStructuredRules(LivestockFeedSignMarker sign)
        {
            List<FeedRule> result = new List<FeedRule>();
            if (sign == null) return result;
            try
            {
                ZNetView view = sign.GetNetworkView();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                string raw = zdo?.GetString(FeedRulesZdoKey, string.Empty) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(raw)) return result;
                string[] entries = raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < entries.Length && result.Count < MaxRules; i++)
                {
                    int split = entries[i].LastIndexOf(':');
                    if (split <= 0 || split >= entries[i].Length - 1) continue;
                    string prefab = entries[i].Substring(0, split).Trim();
                    if (!int.TryParse(entries[i].Substring(split + 1), out int target)) continue;
                    target = Mathf.Clamp(target, 0, MaxTarget);
                    if (target <= 0 || !seen.Add(prefab)) continue;
                    GameObject item = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null;
                    if (item == null || item.GetComponent<ItemDrop>() == null) continue;
                    result.Add(new FeedRule { Prefab = prefab, Target = target, Label = GetItemDisplayName(prefab) });
                }
            }
            catch { }
            return result;
        }

        private static bool HasStructuredRuleState(LivestockFeedSignMarker sign)
        {
            if (sign == null) return false;
            try
            {
                ZNetView view = sign.GetNetworkView();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                return zdo != null && zdo.GetBool(FeedRulesConfiguredZdoKey, false);
            }
            catch { return false; }
        }

        private static void SaveStructuredRules(ZDO zdo, List<FeedRule> rules)
        {
            if (zdo == null) return;
            zdo.Set(FeedRulesConfiguredZdoKey, true);
            List<string> entries = new List<string>();
            for (int i = 0; i < rules.Count && entries.Count < MaxRules; i++)
            {
                FeedRule rule = rules[i];
                if (rule == null || string.IsNullOrWhiteSpace(rule.Prefab) || rule.Target <= 0) continue;
                entries.Add(rule.Prefab + ":" + Mathf.Clamp(rule.Target, 1, MaxTarget));
            }
            zdo.Set(FeedRulesZdoKey, string.Join("|", entries.ToArray()));
        }

        private static bool TryGetWritableSignZdo(LivestockFeedSignMarker sign, out ZDO zdo)
        {
            zdo = null;
            if (sign == null) return false;
            try
            {
                ZNetView view = sign.GetNetworkView();
                if (view == null || !view.IsValid()) return false;
                if (!view.IsOwner())
                {
                    MethodInfo claim = typeof(ZNetView).GetMethod("ClaimOwnership", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    claim?.Invoke(view, null);
                }
                if (!view.IsOwner()) return false;
                zdo = view.GetZDO();
                return zdo != null;
            }
            catch { return false; }
        }

        private static void EnsureSelectableItemCache()
        {
            if (ObjectDB.instance == null)
            {
                CachedSelectableItems.Clear();
                _cachedSelectableDbItemCount = -1;
                _cachedSelectableSceneItemCount = -1;
                return;
            }

            FieldInfo itemsField = typeof(ObjectDB).GetField("m_items", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object rawItems = itemsField?.GetValue(ObjectDB.instance);
            IEnumerable items = rawItems as IEnumerable;
            if (items == null)
            {
                CachedSelectableItems.Clear();
                _cachedSelectableDbItemCount = -1;
                _cachedSelectableSceneItemCount = -1;
                return;
            }

            int currentCount = rawItems is ICollection collection ? collection.Count : -1;
            int sceneCount = GetZNetScenePrefabCount();
            if (_cachedDb == ObjectDB.instance && CachedSelectableItems.Count > 0 &&
                currentCount >= 0 && currentCount == _cachedSelectableDbItemCount &&
                sceneCount == _cachedSelectableSceneItemCount) return;

            EnsureItemAliases();
            CachedSelectableItems.Clear();
            try
            {
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AddSelectableItems(items, seen);

                // Some valid food items (for example BoarMeat in current Valheim builds) are
                // referenced by tameable MonsterAI but are not guaranteed to be present in
                // ObjectDB.m_items. Include ItemDrop prefabs registered in ZNetScene as well.
                IEnumerable scenePrefabs = GetZNetScenePrefabs();
                if (scenePrefabs != null) AddSelectableItems(scenePrefabs, seen);

                CachedSelectableItems.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase));
                _cachedSelectableDbItemCount = currentCount >= 0 ? currentCount : CachedSelectableItems.Count;
                _cachedSelectableSceneItemCount = sceneCount;
            }
            catch
            {
                _cachedSelectableDbItemCount = -1;
                _cachedSelectableSceneItemCount = -1;
            }
        }

        private static void AddSelectableItems(IEnumerable items, HashSet<string> seen)
        {
            if (items == null || seen == null) return;
            foreach (object obj in items)
            {
                GameObject go = obj as GameObject;
                if (go == null && obj is DictionaryEntry entry) go = entry.Value as GameObject;
                ItemDrop drop = go != null ? go.GetComponent<ItemDrop>() : null;
                if (drop?.m_itemData?.m_shared == null) continue;
                string prefab = SpeciesRoleRegistry.NormalizePrefabName(go.name);
                if (string.IsNullOrWhiteSpace(prefab) || !seen.Add(prefab)) continue;
                CachedSelectableItems.Add(new FeedItemOption { Prefab = prefab, Label = GetItemDisplayName(prefab) });
            }
        }

        private static int GetZNetScenePrefabCount()
        {
            try
            {
                IEnumerable prefabs = GetZNetScenePrefabs();
                if (prefabs is ICollection collection) return collection.Count;
                if (prefabs == null) return -1;
                int count = 0;
                foreach (object _ in prefabs) count++;
                return count;
            }
            catch { return -1; }
        }

        private static IEnumerable GetZNetScenePrefabs()
        {
            if (ZNetScene.instance == null) return null;
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo prefabsField = typeof(ZNetScene).GetField("m_prefabs", flags);
                IEnumerable prefabs = prefabsField?.GetValue(ZNetScene.instance) as IEnumerable;
                if (prefabs != null) return prefabs;

                FieldInfo namedField = typeof(ZNetScene).GetField("m_namedPrefabs", flags);
                object named = namedField?.GetValue(ZNetScene.instance);
                if (named is IDictionary dictionary) return dictionary.Values;
            }
            catch { }
            return null;
        }

        private static string ReadSignText(LivestockFeedSignMarker marker)
        {
            if (marker == null) return string.Empty;
            try
            {
                Component[] components = marker.GetComponents<Component>();
                for (int i = 0; i < components.Length; i++)
                {
                    Component component = components[i];
                    if (component == null || !string.Equals(component.GetType().Name, "Sign", StringComparison.Ordinal)) continue;
                    MethodInfo getText = component.GetType().GetMethod("GetText", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    if (getText != null)
                    {
                        string value = getText.Invoke(component, null) as string;
                        if (!string.IsNullOrWhiteSpace(value)) return value;
                    }
                }
            }
            catch { }

            try
            {
                ZNetView view = marker.GetNetworkView();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo != null)
                {
                    string value = zdo.GetString("text", string.Empty);
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
            catch { }
            return string.Empty;
        }

        private static bool TryResolveItem(string label, out string prefab)
        {
            prefab = string.Empty;
            if (string.IsNullOrWhiteSpace(label) || ObjectDB.instance == null) return false;
            string key = NormalizeLabel(label);
            if (ItemAliases.TryGetValue(key, out prefab) && ObjectDB.instance.GetItemPrefab(prefab) != null) return true;

            GameObject direct = ObjectDB.instance.GetItemPrefab(label.Trim());
            if (direct != null)
            {
                prefab = SpeciesRoleRegistry.NormalizePrefabName(direct.name);
                return !string.IsNullOrEmpty(prefab);
            }
            return false;
        }

        private static void EnsureItemAliases()
        {
            if (ObjectDB.instance == null) return;
            if (_cachedDb == ObjectDB.instance && ItemAliases.Count > 0) return;
            _cachedDb = ObjectDB.instance;
            ItemAliases.Clear();

            // Common German/English livestock feed aliases remain useful even if Localization
            // changes or ObjectDB reflection differs between game builds.
            AddAlias("Karotte", "Carrot"); AddAlias("Karotten", "Carrot"); AddAlias("Carrots", "Carrot");
            AddAlias("Ruebe", "Turnip"); AddAlias("Rueben", "Turnip"); AddAlias("Rübe", "Turnip"); AddAlias("Rüben", "Turnip"); AddAlias("Turnips", "Turnip");
            AddAlias("Zwiebel", "Onion"); AddAlias("Zwiebeln", "Onion"); AddAlias("Onions", "Onion");
            AddAlias("Himbeere", "Raspberry"); AddAlias("Himbeeren", "Raspberry"); AddAlias("Raspberries", "Raspberry");
            AddAlias("Blaubeere", "Blueberries"); AddAlias("Blaubeeren", "Blueberries"); AddAlias("Blueberry", "Blueberries");
            AddAlias("Moltebeere", "Cloudberry"); AddAlias("Moltebeeren", "Cloudberry"); AddAlias("Cloudberries", "Cloudberry");
            AddAlias("Pilz", "Mushroom"); AddAlias("Pilze", "Mushroom"); AddAlias("Mushrooms", "Mushroom");
            AddAlias("Gerste", "Barley"); AddAlias("Barley", "Barley");
            AddAlias("Flachs", "Flax"); AddAlias("Flax", "Flax");
            AddAlias("Löwenzahn", "Dandelion"); AddAlias("Loewenzahn", "Dandelion");

            try
            {
                FieldInfo itemsField = typeof(ObjectDB).GetField("m_items", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                IEnumerable items = itemsField?.GetValue(ObjectDB.instance) as IEnumerable;
                if (items == null) return;
                foreach (object obj in items)
                {
                    GameObject go = obj as GameObject;
                    if (go == null) continue;
                    ItemDrop drop = go.GetComponent<ItemDrop>();
                    if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null) continue;
                    string canonical = SpeciesRoleRegistry.NormalizePrefabName(go.name);
                    if (string.IsNullOrEmpty(canonical)) continue;
                    AddAlias(canonical, canonical);
                    string sharedName = drop.m_itemData.m_shared.m_name;
                    if (!string.IsNullOrWhiteSpace(sharedName))
                    {
                        AddAlias(sharedName.TrimStart('$'), canonical);
                        string localized = TryLocalize(sharedName);
                        if (!string.IsNullOrWhiteSpace(localized)) AddAlias(localized, canonical);
                    }
                }
            }
            catch { }
        }

        private static void AddAlias(string alias, string prefab)
        {
            if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(prefab)) return;
            ItemAliases[NormalizeLabel(alias)] = prefab;
        }

        private static string TryLocalize(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return string.Empty;
            try
            {
                Type type = null;
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length && type == null; i++)
                {
                    try { type = assemblies[i].GetType("Localization", false); } catch { }
                }
                if (type == null) return string.Empty;

                const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                object instance = type.GetProperty("instance", staticFlags)?.GetValue(null)
                    ?? type.GetProperty("Instance", staticFlags)?.GetValue(null)
                    ?? type.GetField("instance", staticFlags)?.GetValue(null)
                    ?? type.GetField("m_instance", staticFlags)?.GetValue(null);
                if (instance == null) return string.Empty;

                const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                MethodInfo localize = type.GetMethod("Localize", instanceFlags, null, new[] { typeof(string) }, null);
                if (localize == null) return string.Empty;
                return localize.Invoke(instance, new object[] { key }) as string ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string NormalizeLabel(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            StringBuilder sb = new StringBuilder(normalized.Length);
            for (int i = 0; i < normalized.Length; i++)
            {
                char c = normalized[i];
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        private static string StripRichText(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return Regex.Replace(value, "<.*?>", string.Empty);
        }

        private static int CountProtectedFeed(Vector3 center, string prefab)
        {
            int total = 0;
            WorkerWorldIndex.ForEachItemDrop(center, FeedCountRadius, drop =>
            {
                if (!IsProtectedFeedDrop(drop) || drop.m_itemData == null) return;
                if (!string.Equals(GathererTargetFinder.GetCargoPrefabName(drop), prefab, StringComparison.OrdinalIgnoreCase)) return;
                total += Mathf.Max(0, drop.m_itemData.m_stack);
            });
            return total;
        }

        private static bool TrySpawnProtectedFeed(LivestockFeedSignMarker sign, string prefab, int amount)
        {
            return sign != null && TrySpawnProtectedFeedAt(sign.transform, prefab, amount);
        }

        private static bool TrySpawnProtectedFeedAt(Transform origin, string prefab, int amount)
        {
            if (origin == null || string.IsNullOrEmpty(prefab) || amount <= 0 || ObjectDB.instance == null) return false;
            GameObject itemPrefab = ObjectDB.instance.GetItemPrefab(prefab);
            ItemDrop template = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
            if (itemPrefab == null || template == null || template.m_itemData == null) return false;

            Vector3 forward = origin.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();
            Vector3 position = origin.position - forward * 0.80f + Vector3.up * 0.32f;

            ItemDrop.ItemData data = template.m_itemData.Clone();
            data.m_dropPrefab = itemPrefab;
            data.m_stack = amount;

            object spawned = null;
            try
            {
                MethodInfo method = typeof(ItemDrop).GetMethod("DropItem", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(Vector3), typeof(Quaternion) }, null);
                if (method == null) return false;
                spawned = method.Invoke(null, new object[] { data, amount, position, Quaternion.identity });
            }
            catch { return false; }

            ItemDrop drop = spawned as ItemDrop;
            if (drop == null && spawned is GameObject go) drop = go.GetComponent<ItemDrop>();
            if (drop == null)
            {
                // Some game builds return void. Find the newly-created matching drop at the exact
                // placement point; this is a tiny indexed query, not a scene-wide scan.
                float best = 1.5f * 1.5f;
                WorkerWorldIndex.ForEachItemDrop(position, 1.5f, candidate =>
                {
                    if (candidate == null || candidate.m_itemData == null || IsProtectedFeedDrop(candidate)) return;
                    if (!string.Equals(GathererTargetFinder.GetCargoPrefabName(candidate), prefab, StringComparison.OrdinalIgnoreCase)) return;
                    float sqr = (candidate.transform.position - position).sqrMagnitude;
                    if (sqr >= best) return;
                    best = sqr;
                    drop = candidate;
                });
            }
            if (drop == null) return false;

            if (drop.GetComponent<ProtectedLivestockFeedDrop>() == null) drop.gameObject.AddComponent<ProtectedLivestockFeedDrop>();
            try
            {
                ZNetView view = drop.GetComponent<ZNetView>();
                ZDO dropZdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (dropZdo != null) dropZdo.Set(ProtectedFeedZdoKey, true);
            }
            catch { }
            return true;
        }
    }
}
