using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    internal static class LivestockButcherRuntime
    {
        internal const string TargetKind = "ButcherLivestock";
        internal const float WorkRange = 2.8f;
        private const int DefaultMin = 2;
        private const int DefaultMax = 6;
        // Livestock counts do not need frame-level freshness. Keeping these caches slightly longer
        // prevents a butcher UI/AI pass from turning into a periodic full Character hot path.
        private const float CharacterCacheSeconds = 5.0f;
        private const float ZoneCacheSeconds = 4.0f;
        private static readonly Dictionary<int, Character> Targets = new Dictionary<int, Character>();
        private static readonly Dictionary<int, float> NextHitAt = new Dictionary<int, float>();
        private static readonly List<Character> ActiveCharacterCache = new List<Character>();
        private static readonly Dictionary<ZDO, ZoneSnapshot> ZoneSnapshots = new Dictionary<ZDO, ZoneSnapshot>();
        private static readonly Dictionary<int, ZoneSnapshot> PenSnapshots = new Dictionary<int, ZoneSnapshot>();
        private static readonly Dictionary<ZDO, List<string>> FeedPrefabCache = new Dictionary<ZDO, List<string>>();
        private static readonly Dictionary<ZDO, float> FeedPrefabCacheExpiry = new Dictionary<ZDO, float>();
        private static readonly Dictionary<int, LivestockMeta> LivestockMetaCache = new Dictionary<int, LivestockMeta>();
        private static readonly List<int> StaleLivestockMetaKeys = new List<int>();
        private static float NextCharacterCacheRefreshAt;
        private static float NextLivestockMetaCleanupAt;

        private sealed class LivestockMeta
        {
            internal Character Character;
            internal bool Candidate;
            internal bool Juvenile;
            internal string Species;
            internal int Level;
        }

        private sealed class AnimalRecord
        {
            internal Character Character;
            internal string Species;
            internal bool Juvenile;
            internal int Level;
        }

        private struct SpeciesCount
        {
            internal int Total;
            internal int Adults;
            internal int Young;
        }

        private sealed class ZoneSnapshot
        {
            internal float ExpiresAt;
            internal Vector3 Center;
            internal float Radius;
            internal readonly List<AnimalRecord> Animals = new List<AnimalRecord>();
            internal readonly Dictionary<string, SpeciesCount> Counts = new Dictionary<string, SpeciesCount>(StringComparer.OrdinalIgnoreCase);
            internal int Active;
            internal int Tamed;
            internal int Workers;
            internal int InZone;
        }

        // Population rules belong to the enclosure, not to one individual caretaker.  When a
        // feeding trough exists in the assigned Workzone, Min/Max are stored on that trough ZDO so
        // every Butcher/Tierpfleger assigned to the same pen shares one authoritative rule set.
        // Old saves without a trough keep using the worker ZDO as a compatibility fallback.
        internal static int GetMin(ZDO zdo, string species)
        {
            ZDO store = ResolveRuleStore(zdo);
            MigrateLegacyRuleIfNeeded(zdo, store, species);
            return ReadMin(store, species);
        }

        internal static int GetMax(ZDO zdo, string species)
        {
            ZDO store = ResolveRuleStore(zdo);
            MigrateLegacyRuleIfNeeded(zdo, store, species);
            return ReadMax(store, species);
        }

        internal static void SetRule(ZDO zdo, string species, int min, int max)
        {
            ZDO store = ResolveRuleStore(zdo);
            MigrateLegacyRuleIfNeeded(zdo, store, species);
            WriteRule(store, species, min, max);
        }

        // v0.5.381: read-only population-rule view used by the explicit Workzone audit.
        // Do not run legacy migration from a diagnostic button; gameplay code keeps using GetMin/GetMax.
        internal static void GetRuleReadOnly(ZDO zdo, string species, out int min, out int max)
        {
            ZDO store = ResolveRuleStore(zdo);
            min = ReadMin(store, species);
            max = ReadMax(store, species);
        }

        internal static int GetPenMin(LivestockFeedSignMarker sign, string species) => ReadMin(GetPenZdo(sign), species);
        internal static int GetPenMax(LivestockFeedSignMarker sign, string species) => ReadMax(GetPenZdo(sign), species);

        internal static bool SetPenRule(LivestockFeedSignMarker sign, string species, int min, int max, out string error)
        {
            error = string.Empty;
            if (sign == null || string.IsNullOrWhiteSpace(species)) { error = "Ungueltiges Gehege/Tier."; return false; }
            ZNetView view = sign.GetNetworkView();
            if (view == null || !view.IsValid() || view.GetZDO() == null) { error = "Futtertrog ist nicht netzwerkbereit."; return false; }
            if (!view.IsOwner()) { error = "Tierbestand kann nur vom Netzwerkbesitzer geaendert werden."; return false; }
            WriteRule(view.GetZDO(), species, min, max);
            return true;
        }

        private static int ReadMin(ZDO store, string species) => store == null ? DefaultMin : Mathf.Max(0, store.GetInt(MinKey(species), DefaultMin));
        private static int ReadMax(ZDO store, string species) => store == null ? DefaultMax : Mathf.Max(ReadMin(store, species), store.GetInt(MaxKey(species), DefaultMax));

        private static void WriteRule(ZDO store, string species, int min, int max)
        {
            if (store == null || string.IsNullOrWhiteSpace(species)) return;
            min = Mathf.Max(0, min);
            max = Mathf.Max(min, max);
            store.Set(MinKey(species), min);
            store.Set(MaxKey(species), max);
        }

        private const int UnsetRuleSentinel = -2147483000;

        private static void MigrateLegacyRuleIfNeeded(ZDO workerZdo, ZDO store, string species)
        {
            if (workerZdo == null || store == null || store == workerZdo || string.IsNullOrWhiteSpace(species)) return;
            string minKey = MinKey(species);
            string maxKey = MaxKey(species);
            int penMin = store.GetInt(minKey, UnsetRuleSentinel);
            int penMax = store.GetInt(maxKey, UnsetRuleSentinel);
            if (penMin != UnsetRuleSentinel || penMax != UnsetRuleSentinel) return;

            int legacyMin = workerZdo.GetInt(minKey, UnsetRuleSentinel);
            int legacyMax = workerZdo.GetInt(maxKey, UnsetRuleSentinel);
            if (legacyMin == UnsetRuleSentinel && legacyMax == UnsetRuleSentinel) return;
            int min = legacyMin == UnsetRuleSentinel ? DefaultMin : Mathf.Max(0, legacyMin);
            int max = legacyMax == UnsetRuleSentinel ? Mathf.Max(min, DefaultMax) : Mathf.Max(min, legacyMax);
            store.Set(minKey, min);
            store.Set(maxKey, max);
        }

        private static ZDO ResolveRuleStore(ZDO workerZdo)
        {
            if (workerZdo == null) return null;
            try
            {
                if (WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius) &&
                    LivestockFeedSignRegistry.TryFindNearest(center, Mathf.Max(5f, radius), out LivestockFeedSignMarker sign) && sign != null)
                {
                    ZDO penZdo = GetPenZdo(sign);
                    if (penZdo != null) return penZdo;
                }
            }
            catch { }
            return workerZdo;
        }

        private static ZDO GetPenZdo(LivestockFeedSignMarker sign)
        {
            try
            {
                ZNetView view = sign != null ? sign.GetNetworkView() : null;
                return view != null && view.IsValid() ? view.GetZDO() : null;
            }
            catch { return null; }
        }

        internal static List<string> GetRuleSpecies(Character worker, ZDO zdo)
        {
            // Only show species that are actually present as tamed livestock in this Workzone.
            // The previous always-on Boar/Wolf/Lox/Hen rows made the UI look like those animals
            // had been discovered even when the zone contained none of them.
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            ZoneSnapshot snapshot = GetSnapshot(zdo);
            if (snapshot != null)
            {
                foreach (string species in snapshot.Counts.Keys)
                {
                    if (!string.IsNullOrWhiteSpace(species)) names.Add(species);
                }
            }

            List<string> result = new List<string>(names);
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        internal static int CountTamedInAssignedArea(ZDO zdo, string species, out int adults, out int young)
        {
            adults = 0;
            young = 0;
            ZoneSnapshot snapshot = GetSnapshot(zdo);
            if (snapshot == null || string.IsNullOrWhiteSpace(species)) return 0;
            if (!snapshot.Counts.TryGetValue(species, out SpeciesCount count)) return 0;
            adults = count.Adults;
            young = count.Young;
            return count.Total;
        }

        internal static string GetAdultStarSummary(ZDO zdo, string species)
        {
            ZoneSnapshot snapshot = GetSnapshot(zdo);
            if (snapshot == null || string.IsNullOrWhiteSpace(species)) return "-";
            return BuildAdultStarSummary(snapshot, species);
        }

        private static string BuildAdultStarSummary(ZoneSnapshot snapshot, string species)
        {
            SortedDictionary<int, int> byStars = new SortedDictionary<int, int>();
            for (int i = 0; i < snapshot.Animals.Count; i++)
            {
                AnimalRecord record = snapshot.Animals[i];
                if (record == null || record.Character == null || record.Juvenile ||
                    !string.Equals(record.Species, species, StringComparison.OrdinalIgnoreCase)) continue;
                int stars = Mathf.Max(0, record.Level - 1);
                byStars.TryGetValue(stars, out int count);
                byStars[stars] = count + 1;
            }

            if (byStars.Count == 0) return "-";
            List<string> parts = new List<string>();
            foreach (KeyValuePair<int, int> pair in byStars) parts.Add($"{pair.Key}★ x{pair.Value}");
            return string.Join(", ", parts.ToArray());
        }

        internal static List<string> GetPenRuleSpecies(LivestockFeedSignMarker sign)
        {
            ZoneSnapshot snapshot = GetPenSnapshot(sign);
            List<string> result = new List<string>();
            if (snapshot == null) return result;
            foreach (string species in snapshot.Counts.Keys)
                if (!string.IsNullOrWhiteSpace(species)) result.Add(species);
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        internal static int CountTamedInPen(LivestockFeedSignMarker sign, string species, out int adults, out int young)
        {
            adults = 0;
            young = 0;
            ZoneSnapshot snapshot = GetPenSnapshot(sign);
            if (snapshot == null || string.IsNullOrWhiteSpace(species)) return 0;
            if (!snapshot.Counts.TryGetValue(species, out SpeciesCount count)) return 0;
            adults = count.Adults;
            young = count.Young;
            return count.Total;
        }

        internal static string GetAdultStarSummaryInPen(LivestockFeedSignMarker sign, string species)
        {
            ZoneSnapshot snapshot = GetPenSnapshot(sign);
            if (snapshot == null || string.IsNullOrWhiteSpace(species)) return "-";
            return BuildAdultStarSummary(snapshot, species);
        }

        internal static List<string> GetAcceptedFoodPrefabs(ZDO zdo)
        {
            if (zdo == null) return new List<string>();
            float now = Time.realtimeSinceStartup;
            if (FeedPrefabCache.TryGetValue(zdo, out List<string> cached) &&
                FeedPrefabCacheExpiry.TryGetValue(zdo, out float expires) && expires > now)
                return new List<string>(cached);

            HashSet<string> foods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ZoneSnapshot snapshot = GetSnapshot(zdo);
            if (snapshot == null) return new List<string>();

            for (int i = 0; i < snapshot.Animals.Count; i++)
            {
                Character animal = snapshot.Animals[i]?.Character;
                if (animal == null) continue;
                MonsterAI ai = null;
                try
                {
                    ai = animal.GetComponent<MonsterAI>();
                    if (ai == null) ai = animal.GetComponentInChildren<MonsterAI>(true);
                }
                catch { }
                if (ai == null) continue;

                try
                {
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    FieldInfo field = ai.GetType().GetField("m_consumeItems", flags);
                    object value = field != null ? field.GetValue(ai) : null;
                    IEnumerable enumerable = value as IEnumerable;
                    if (enumerable == null) continue;

                    foreach (object entry in enumerable)
                    {
                        string prefab = string.Empty;
                        if (entry is ItemDrop drop && drop != null)
                            prefab = SpeciesRoleRegistry.NormalizePrefabName(drop.gameObject.name);
                        else if (entry is GameObject go && go != null)
                            prefab = SpeciesRoleRegistry.NormalizePrefabName(go.name);
                        else if (entry is ItemDrop.ItemData data && data != null)
                            prefab = WorkerStorageRuntime.GetPrefabName(data);

                        if (!string.IsNullOrWhiteSpace(prefab)) foods.Add(prefab);
                    }
                }
                catch { }
            }

            List<string> result = new List<string>(foods);
            result.Sort(StringComparer.OrdinalIgnoreCase);
            FeedPrefabCache[zdo] = new List<string>(result);
            FeedPrefabCacheExpiry[zdo] = now + ZoneCacheSeconds;
            return result;
        }

        internal static bool TryFindSurplusTarget(Character worker, ZDO zdo, Vector3 center, float radius, out Character target, out string reason)
        {
            target = null;
            reason = "Tierbestand innerhalb Min/Max";
            if (worker == null || zdo == null) return false;

            int workerKey = worker.GetInstanceID();
            if (Targets.TryGetValue(workerKey, out Character existing) && IsValidTarget(existing, zdo, center, radius))
            {
                target = existing;
                string existingSpecies = GetSpeciesName(existing);
                reason = $"Überschuss {existingSpecies}: niedrigste Sternstufe wird geschlachtet";
                return true;
            }
            Targets.Remove(workerKey);

            ZoneSnapshot snapshot = GetSnapshot(zdo);
            if (snapshot == null) return false;

            Character best = null;
            string bestSpecies = null;
            int bestLevel = int.MaxValue;
            float bestDistance = float.MaxValue;
            string breedingReserveReason = null;

            for (int i = 0; i < snapshot.Animals.Count; i++)
            {
                AnimalRecord record = snapshot.Animals[i];
                if (record == null || record.Character == null || record.Juvenile) continue;
                if (!snapshot.Counts.TryGetValue(record.Species, out SpeciesCount count)) continue;

                int min = GetMin(zdo, record.Species);
                int max = GetMax(zdo, record.Species);

                // Max is the complete pen population (adults + juveniles), while Min is the
                // protected ADULT breeding stock.  The old total>min check could slaughter one of
                // the final breeding adults when a large batch of juveniles pushed Total over Max.
                // Only an adult above the configured breeding reserve may ever become a target.
                if (count.Total <= max) continue;
                if (count.Adults <= min)
                {
                    if (breedingReserveReason == null)
                        breedingReserveReason = $"{record.Species}: {count.Total}/{max}, aber nur {count.Adults}/{min} erwachsene Zuchttiere - kein Schlachten";
                    continue;
                }

                float distance = (record.Character.transform.position - worker.transform.position).sqrMagnitude;
                if (best == null || record.Level < bestLevel || (record.Level == bestLevel && distance < bestDistance))
                {
                    best = record.Character;
                    bestSpecies = record.Species;
                    bestLevel = record.Level;
                    bestDistance = distance;
                }
            }

            if (best == null)
            {
                if (!string.IsNullOrEmpty(breedingReserveReason)) reason = breedingReserveReason;
                return false;
            }
            Targets[workerKey] = best;
            target = best;
            int total = CountTamedInAssignedArea(zdo, bestSpecies, out _, out _);
            reason = $"{bestSpecies}: {total}/{GetMax(zdo, bestSpecies)} -> erwachsenes Tier Level {bestLevel} abbauen";
            return true;
        }

        internal static bool TryWork(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return false;
            int key = worker.GetInstanceID();
            if (!Targets.TryGetValue(key, out Character target) || target == null || target.IsDead())
            {
                Targets.Remove(key);
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return false;
            }

            if (!WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 center, out float radius) || !IsValidTarget(target, zdo, center, radius))
            {
                Targets.Remove(key);
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return false;
            }

            Vector3 delta = target.transform.position - worker.transform.position;
            delta.y = 0f;
            if (delta.magnitude > WorkRange && !LocalWorkFallbackRuntime.IsActive(zdo, GetSpeciesName(target), TargetKind))
            {
                WorkerZdo.WriteTarget(zdo, GetSpeciesName(target), target.transform.position, TargetKind);
                zdo.Set(WorkerZdo.State, (int)WorkerState.MoveToTarget);
                return true;
            }

            if (Time.time < (NextHitAt.TryGetValue(key, out float next) ? next : 0f)) return true;
            NextHitAt[key] = Time.time + 1.2f;

            Vector3 aim = target.transform.position - worker.transform.position;
            if (aim.sqrMagnitude > 0.001f) worker.SetLookDir(aim.normalized, 0f);
            worker.StartAttack(target, false);

            HitData hit = new HitData();
            hit.m_damage.m_slash = Mathf.Max(500f, target.GetMaxHealth() * 5f);
            hit.m_point = target.transform.position + Vector3.up * 0.7f;
            hit.m_dir = aim.sqrMagnitude > 0.001f ? aim.normalized : worker.transform.forward;
            hit.SetAttacker(worker);
            target.Damage(hit);

            if (target == null || target.IsDead())
            {
                Targets.Remove(key);
                InvalidateSnapshot(zdo);
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            }
            return true;
        }

        /// <summary>
        /// v0.5.378 unloaded-time materialisation. The background scheduler accrues only a
        /// slaughter capacity number; once the Workzone is physical again this method applies
        /// that capacity to the CURRENT real population. This deliberately reuses the exact same
        /// Min/Max, juvenile and lowest-star target selection as the visible butcher job, so
        /// offspring that appeared while the zone was away are reconciled without maintaining a
        /// second fake animal population. No world scan is introduced: GetSnapshot only inspects
        /// loaded Characters inside the assigned Workzone/pen and is called only while catch-up
        /// quota exists.
        /// </summary>
        internal static int RunBackgroundCatchup(Character worker, ZDO zdo, int maxAnimals, out string status)
        {
            status = "Metzger-Catchup: kein Auftrag";
            if (worker == null || zdo == null || maxAnimals <= 0) return 0;
            if (!WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 center, out float radius))
            {
                status = "Metzger-Catchup: keine Workzone";
                return 0;
            }

            int workerKey = worker.GetInstanceID();
            int culled = 0;
            int limit = Mathf.Clamp(maxAnimals, 0, 256);
            string lastReason = "Tierbestand innerhalb Min/Max";

            for (int i = 0; i < limit; i++)
            {
                // Each successful kill changes the authoritative population, so invalidate only
                // this short-lived cached snapshot before choosing the next adult surplus animal.
                Targets.Remove(workerKey);
                InvalidateSnapshot(zdo);

                if (!TryFindSurplusTarget(worker, zdo, center, radius, out Character target, out string reason) || target == null)
                {
                    lastReason = reason;
                    break;
                }

                lastReason = reason;
                Vector3 aim = target.transform.position - worker.transform.position;
                HitData hit = new HitData();
                hit.m_damage.m_slash = Mathf.Max(500f, target.GetMaxHealth() * 5f);
                hit.m_point = target.transform.position + Vector3.up * 0.7f;
                hit.m_dir = aim.sqrMagnitude > 0.001f ? aim.normalized : worker.transform.forward;
                hit.SetAttacker(worker);

                try { target.Damage(hit); }
                catch (Exception ex)
                {
                    status = $"Metzger-Catchup abgebrochen: {ex.GetType().Name}";
                    break;
                }

                // Damage normally resolves synchronously for locally authoritative loaded
                // livestock. If another peer owns the animal, do not hammer it repeatedly in the
                // same frame; leave the remaining quota pending for the throttled retry path.
                bool dead = false;
                try { dead = target == null || target.IsDead(); } catch { }
                Targets.Remove(workerKey);
                InvalidateSnapshot(zdo);
                if (!dead)
                {
                    status = $"Metzger-Catchup wartet auf Netzwerkbesitz ({lastReason})";
                    break;
                }

                culled++;
            }

            status = culled > 0
                ? $"Metzger-Catchup: {culled} erwachsene(s) Ueberschusstier(e) geschlachtet"
                : $"Metzger-Catchup: {lastReason}";
            return culled;
        }

        private static bool IsValidTarget(Character candidate, ZDO zdo, Vector3 center, float radius)
        {
            if (candidate == null || candidate.IsDead()) return false;
            ZoneSnapshot snapshot = GetSnapshot(zdo);
            if (snapshot == null) return false;

            AnimalRecord record = null;
            for (int i = 0; i < snapshot.Animals.Count; i++)
            {
                AnimalRecord current = snapshot.Animals[i];
                if (current != null && current.Character == candidate)
                {
                    record = current;
                    break;
                }
            }
            if (record == null || record.Juvenile) return false;
            if (!snapshot.Counts.TryGetValue(record.Species, out SpeciesCount count)) return false;
            // Min protects adult breeding animals; juveniles may push Total over Max but must never
            // cause the keeper to cull below that adult reserve.
            return count.Total > GetMax(zdo, record.Species) && count.Adults > GetMin(zdo, record.Species);
        }

        private static ZoneSnapshot GetSnapshot(ZDO zdo)
        {
            if (zdo == null || !WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 center, out float radius)) return null;

            // Once an enclosure trough exists, population accounting is pen-specific instead of
            // counting every tame animal anywhere in a much larger Workzone.  Escaped animals are
            // handled first by LivestockPenRuntime and return into this radius before culling runs.
            if (LivestockFeedSignRegistry.TryFindNearest(center, Mathf.Max(5f, radius), out LivestockFeedSignMarker pen) && pen != null)
            {
                center = pen.transform.position;
                radius = LivestockPenRuntime.GetPenRadius(pen);
            }

            float now = Time.realtimeSinceStartup;
            float effectiveRadius = Mathf.Max(5f, radius);

            if (ZoneSnapshots.TryGetValue(zdo, out ZoneSnapshot cached) && cached != null && cached.ExpiresAt > now)
            {
                Vector3 centerDelta = cached.Center - center;
                centerDelta.y = 0f;
                if (centerDelta.sqrMagnitude < 0.01f && Mathf.Abs(cached.Radius - effectiveRadius) < 0.01f)
                    return cached;
            }

            long perfScan = WorkersPerformanceProfiler.Begin();
            RefreshActiveCharacterCache(now);

            ZoneSnapshot snapshot = cached ?? new ZoneSnapshot();
            snapshot.ExpiresAt = now + ZoneCacheSeconds;
            snapshot.Center = center;
            snapshot.Radius = effectiveRadius;
            snapshot.Animals.Clear();
            snapshot.Counts.Clear();
            snapshot.Active = 0;
            snapshot.Tamed = 0;
            snapshot.Workers = 0;
            snapshot.InZone = 0;

            float radiusSqr = effectiveRadius * effectiveRadius;
            for (int i = 0; i < ActiveCharacterCache.Count; i++)
            {
                Character c = ActiveCharacterCache[i];
                if (c == null || c is Player || c.IsDead()) continue;
                snapshot.Active++;

                // Cheap spatial rejection FIRST. The old order resolved ZNetView/ZDO/tame state
                // for every loaded creature in the world even when the Workzone was only 30 m.
                Vector3 delta = c.transform.position - center;
                delta.y = 0f;
                if (delta.sqrMagnitude > radiusSqr) continue;

                ZNetView view = null;
                ZDO animalZdo = null;
                try
                {
                    view = c.GetComponent<ZNetView>();
                    animalZdo = view != null && view.IsValid() ? view.GetZDO() : null;
                }
                catch { }

                if (WorkerZdo.IsWorker(animalZdo))
                {
                    snapshot.Workers++;
                    continue;
                }

                // Component shape/species/level are stable for the lifetime of a Character.
                // Cache them per instance so a large farm does not repeat GetComponentInChildren,
                // Growup reflection and prefab-name reflection on every butcher/UI snapshot.
                LivestockMeta meta = GetLivestockMeta(c);
                if (meta == null || !meta.Candidate || !IsTamedCompat(c, animalZdo)) continue;
                snapshot.Tamed++;
                snapshot.InZone++;

                bool juvenile = meta.Juvenile;
                string species = meta.Species;
                if (string.IsNullOrWhiteSpace(species)) continue;

                AnimalRecord record = new AnimalRecord
                {
                    Character = c,
                    Species = species,
                    Juvenile = juvenile,
                    Level = meta.Level
                };
                snapshot.Animals.Add(record);

                snapshot.Counts.TryGetValue(species, out SpeciesCount count);
                count.Total++;
                if (juvenile) count.Young++; else count.Adults++;
                snapshot.Counts[species] = count;
            }

            ZoneSnapshots[zdo] = snapshot;
            WorkersPerformanceProfiler.End(WorkersPerfBucket.LivestockScan, perfScan);
            return snapshot;
        }

        private static ZoneSnapshot GetPenSnapshot(LivestockFeedSignMarker sign)
        {
            if (sign == null || sign.gameObject == null || !sign.gameObject.activeInHierarchy) return null;
            float now = Time.realtimeSinceStartup;
            Vector3 center = sign.transform.position;
            float effectiveRadius = Mathf.Max(5f, LivestockPenRuntime.GetPenRadius(sign));
            int key = sign.GetInstanceID();

            if (PenSnapshots.TryGetValue(key, out ZoneSnapshot cached) && cached != null && cached.ExpiresAt > now)
            {
                Vector3 centerDelta = cached.Center - center;
                centerDelta.y = 0f;
                if (centerDelta.sqrMagnitude < 0.01f && Mathf.Abs(cached.Radius - effectiveRadius) < 0.01f) return cached;
            }

            RefreshActiveCharacterCache(now);
            ZoneSnapshot snapshot = cached ?? new ZoneSnapshot();
            snapshot.ExpiresAt = now + ZoneCacheSeconds;
            snapshot.Center = center;
            snapshot.Radius = effectiveRadius;
            snapshot.Animals.Clear();
            snapshot.Counts.Clear();
            snapshot.Active = 0;
            snapshot.Tamed = 0;
            snapshot.Workers = 0;
            snapshot.InZone = 0;

            float radiusSqr = effectiveRadius * effectiveRadius;
            for (int i = 0; i < ActiveCharacterCache.Count; i++)
            {
                Character c = ActiveCharacterCache[i];
                if (c == null || c is Player || c.IsDead()) continue;
                snapshot.Active++;
                Vector3 delta = c.transform.position - center;
                delta.y = 0f;
                if (delta.sqrMagnitude > radiusSqr) continue;

                ZNetView view = null;
                ZDO animalZdo = null;
                try { view = c.GetComponent<ZNetView>(); animalZdo = view != null && view.IsValid() ? view.GetZDO() : null; } catch { }
                if (WorkerZdo.IsWorker(animalZdo)) { snapshot.Workers++; continue; }

                LivestockMeta meta = GetLivestockMeta(c);
                if (meta == null || !meta.Candidate || !IsTamedCompat(c, animalZdo)) continue;
                snapshot.Tamed++;
                snapshot.InZone++;
                if (string.IsNullOrWhiteSpace(meta.Species)) continue;

                snapshot.Animals.Add(new AnimalRecord
                {
                    Character = c,
                    Species = meta.Species,
                    Juvenile = meta.Juvenile,
                    Level = meta.Level
                });
                snapshot.Counts.TryGetValue(meta.Species, out SpeciesCount count);
                count.Total++;
                if (meta.Juvenile) count.Young++; else count.Adults++;
                snapshot.Counts[meta.Species] = count;
            }

            PenSnapshots[key] = snapshot;
            return snapshot;
        }

        private static void RefreshActiveCharacterCache(float now)
        {
            if (NextCharacterCacheRefreshAt > now) return;
            NextCharacterCacheRefreshAt = now + CharacterCacheSeconds;
            ActiveCharacterCache.Clear();

            List<Character> registered = WorkerCharacterIndexRuntime.GetLiveNonPlayers();

            for (int i = 0; i < registered.Count; i++)
            {
                Character c = registered[i];
                if (c == null || c.gameObject == null || !c.gameObject.activeInHierarchy) continue;
                ActiveCharacterCache.Add(c);
            }
        }


        internal static void ClearCaches()
        {
            Targets.Clear();
            NextHitAt.Clear();
            ActiveCharacterCache.Clear();
            ZoneSnapshots.Clear();
            PenSnapshots.Clear();
            FeedPrefabCache.Clear();
            FeedPrefabCacheExpiry.Clear();
            LivestockMetaCache.Clear();
            StaleLivestockMetaKeys.Clear();
            NextCharacterCacheRefreshAt = 0f;
            NextLivestockMetaCleanupAt = 0f;
        }

        private static void InvalidateSnapshot(ZDO zdo)
        {
            if (zdo != null && ZoneSnapshots.TryGetValue(zdo, out ZoneSnapshot snapshot) && snapshot != null) snapshot.ExpiresAt = 0f;
            foreach (ZoneSnapshot pen in PenSnapshots.Values) if (pen != null) pen.ExpiresAt = 0f;
            if (zdo != null) FeedPrefabCacheExpiry[zdo] = 0f;
        }

        private static bool IsTamedCompat(Character c, ZDO zdo)
        {
            if (c == null) return false;
            try { if (c.IsTamed()) return true; } catch { }

            // Compatibility fallback is deliberately restricted to livestock-shaped Characters.
            // This keeps old/modded tameables working without interpreting an unrelated actor's
            // generic ZDO flag as farm livestock.
            if (!IsLivestockCandidate(c)) return false;
            try { return zdo != null && zdo.GetBool("tamed", false); } catch { return false; }
        }

        private static bool IsLivestockCandidate(Character c)
        {
            LivestockMeta meta = GetLivestockMeta(c);
            return meta != null && meta.Candidate;
        }

        private static LivestockMeta GetLivestockMeta(Character c)
        {
            if (c == null) return null;
            int key = c.GetInstanceID();
            if (LivestockMetaCache.TryGetValue(key, out LivestockMeta cached) && cached != null && cached.Character == c)
                return cached;

            bool tameable = false;
            try
            {
                tameable = c.GetComponent<Tameable>() != null || c.GetComponentInChildren<Tameable>(true) != null;
            }
            catch { }

            bool juvenile = FindGrowup(c) != null;
            LivestockMeta meta = new LivestockMeta
            {
                Character = c,
                Candidate = tameable || juvenile,
                Juvenile = juvenile,
                Species = GetSpeciesName(c, juvenile),
                Level = SafeLevel(c)
            };
            LivestockMetaCache[key] = meta;
            CleanupLivestockMetaIfDue();
            return meta;
        }

        private static void CleanupLivestockMetaIfDue()
        {
            float now = Time.realtimeSinceStartup;
            if (now < NextLivestockMetaCleanupAt && LivestockMetaCache.Count < 2048) return;
            NextLivestockMetaCleanupAt = now + 30f;
            StaleLivestockMetaKeys.Clear();
            foreach (KeyValuePair<int, LivestockMeta> pair in LivestockMetaCache)
            {
                LivestockMeta meta = pair.Value;
                if (meta == null || meta.Character == null || meta.Character.gameObject == null)
                    StaleLivestockMetaKeys.Add(pair.Key);
            }
            for (int i = 0; i < StaleLivestockMetaKeys.Count; i++)
                LivestockMetaCache.Remove(StaleLivestockMetaKeys[i]);
            StaleLivestockMetaKeys.Clear();
        }

        private static string GetSpeciesName(Character c)
        {
            return GetSpeciesName(c, IsJuvenile(c));
        }

        private static string GetSpeciesName(Character c, bool juvenile)
        {
            if (c == null) return string.Empty;
            string raw = c.gameObject != null ? c.gameObject.name : string.Empty;

            try
            {
                ZNetView view = c.GetComponent<ZNetView>();
                if (view != null)
                {
                    MethodInfo method = view.GetType().GetMethod("GetPrefabName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (method != null && method.ReturnType == typeof(string))
                    {
                        string prefab = method.Invoke(view, null) as string;
                        if (!string.IsNullOrWhiteSpace(prefab)) raw = prefab;
                    }
                }
            }
            catch { }

            string normalized = SpeciesRoleRegistry.NormalizePrefabName(raw);
            if (!juvenile) return normalized;

            string grownSpecies = TryResolveGrownSpecies(c);
            if (!string.IsNullOrWhiteSpace(grownSpecies)) return grownSpecies;

            // Compatibility fallback for vanilla babies if Growup internals change.
            if (normalized.IndexOf("boar", StringComparison.OrdinalIgnoreCase) >= 0) return "Boar";
            if (normalized.IndexOf("wolf", StringComparison.OrdinalIgnoreCase) >= 0) return "Wolf";
            if (normalized.IndexOf("lox", StringComparison.OrdinalIgnoreCase) >= 0) return "Lox";
            if (normalized.IndexOf("chicken", StringComparison.OrdinalIgnoreCase) >= 0 || normalized.IndexOf("hen", StringComparison.OrdinalIgnoreCase) >= 0) return "Hen";
            return normalized;
        }

        private static string TryResolveGrownSpecies(Character c)
        {
            Growup growup = FindGrowup(c);
            if (growup == null) return null;
            try
            {
                FieldInfo field = growup.GetType().GetField("m_grownPrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                GameObject grown = field != null ? field.GetValue(growup) as GameObject : null;
                if (grown != null && !string.IsNullOrWhiteSpace(grown.name))
                    return SpeciesRoleRegistry.NormalizePrefabName(grown.name);
            }
            catch { }
            return null;
        }

        internal static string GetScanDebug(ZDO zdo)
        {
            if (!WorkerZdo.TryReadWorkArea(zdo, out _, out _, out _))
                return "Tier-Scan: keine Workzone zugewiesen";

            ZoneSnapshot snapshot = GetSnapshot(zdo);
            if (snapshot == null) return "Tier-Scan: keine Daten";
            return $"Gezähmte Tiere in Workzone: {snapshot.InZone}";
        }

        private static Growup FindGrowup(Character c)
        {
            if (c == null) return null;
            try
            {
                Growup growup = c.GetComponent<Growup>();
                if (growup != null) return growup;
                return c.GetComponentInChildren<Growup>(true);
            }
            catch { return null; }
        }

        private static bool IsJuvenile(Character c) => FindGrowup(c) != null;
        private static int SafeLevel(Character c) { try { return Mathf.Max(1, c.GetLevel()); } catch { return 1; } }
        private static string SafeKey(string species)
        {
            if (string.IsNullOrEmpty(species)) return "Unknown";
            char[] chars = species.ToCharArray();
            for (int i = 0; i < chars.Length; i++) if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_') chars[i] = '_';
            return new string(chars);
        }
        private static string MinKey(string species) => "vw_butcher_min_" + SafeKey(species);
        private static string MaxKey(string species) => "vw_butcher_max_" + SafeKey(species);
    }
}
