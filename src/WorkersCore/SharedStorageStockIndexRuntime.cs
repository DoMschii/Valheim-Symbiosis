using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Event-driven loaded-storage stock index shared by Workers production displays and future
    /// storage consumers. Container inventories are read only when a container awakens/saves or
    /// during the rare safety reconciliation. Displays never scan container inventories themselves.
    /// </summary>
    internal static class SharedStorageStockIndexRuntime
    {
        private sealed class ContainerSnapshot
        {
            internal Container Container;
            internal Vector3 Position;
            internal readonly Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class AreaState
        {
            internal Vector3 Center;
            internal float Radius;
            internal float RadiusSqr;
            internal bool Initialized;
            internal float NextReconcile;
            internal readonly Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        internal readonly struct CachedItemSource
        {
            internal readonly Container Container;
            internal readonly string Prefab;
            internal readonly int Count;
            internal readonly Vector3 Position;

            internal CachedItemSource(Container container, string prefab, int count, Vector3 position)
            {
                Container = container;
                Prefab = prefab ?? string.Empty;
                Count = Math.Max(0, count);
                Position = position;
            }
        }

        private static readonly Dictionary<int, ContainerSnapshot> Containers = new Dictionary<int, ContainerSnapshot>();
        private static readonly Dictionary<string, AreaState> Areas = new Dictionary<string, AreaState>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> ScratchCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // v0.5.337: reverse item -> loaded-container index. Logistics can now ask "where are
        // Blueberries?" without walking every normal chest. The index is maintained by Container.Awake
        // and Container.Save; a targeted rescan is only allowed after a cache miss/stale location.
        private static readonly Dictionary<string, HashSet<int>> ItemLocations =
            new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<int> LocationScratch = new List<int>(32);
        private static readonly Dictionary<string, float> MissRescanUntil =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<int> StaleContainerKeys = new List<int>();
        private static bool _bootstrapped;
        private const float ReconcileSeconds = 30f * 60f;
        private const float MissRescanCooldownSeconds = 15f;

        internal static int IndexedContainerCount => Containers.Count;
        internal static int RegisteredAreaCount => Areas.Count;

        internal static void OnContainerAwake(Container container)
        {
            CaptureContainer(container);
            WorkerStorageRuntime.InvalidateCookingStockSnapshots();
        }

        internal static void OnContainerSaved(Container container)
        {
            CaptureContainer(container);
            WorkerStorageRuntime.InvalidateCookingStockSnapshots();
        }

        internal static int GetStock(string areaId, Vector3 center, float radius, string prefab)
        {
            if (string.IsNullOrWhiteSpace(prefab)) return 0;
            AreaState area = EnsureArea(areaId, center, radius);
            string key = WorkerStorageRuntime.NormalizeObjectName(prefab);
            return area.Counts.TryGetValue(key, out int count) ? Math.Max(0, count) : 0;
        }

        internal static void CollectItems(string areaId, Vector3 center, float radius, HashSet<string> output)
        {
            if (output == null) return;
            AreaState area = EnsureArea(areaId, center, radius);
            foreach (KeyValuePair<string, int> pair in area.Counts)
            {
                if (pair.Value > 0 && !string.IsNullOrWhiteSpace(pair.Key)) output.Add(pair.Key);
            }
        }

        /// <summary>
        /// v0.5.337: Find an item source from the reverse item-location cache. Only containers that
        /// were last known to contain this exact prefab are touched. If all cached locations are stale,
        /// one bounded area refresh is allowed and then negatively cached for a short time.
        /// </summary>
        internal static bool TryFindItemSource(string prefab, Vector3 center, float radius, Vector3 preferNear,
            Func<Container, bool> acceptContainer, Func<Container, int> protectedAmount,
            out Container source, out int usable, out Vector3 position)
        {
            return TryFindItemSource(prefab, center, radius, preferNear, 1, acceptContainer, protectedAmount,
                out source, out usable, out position);
        }

        internal static bool TryFindItemSource(string prefab, Vector3 center, float radius, Vector3 preferNear,
            int preferredAmount, Func<Container, bool> acceptContainer, Func<Container, int> protectedAmount,
            out Container source, out int usable, out Vector3 position)
        {
            source = null;
            usable = 0;
            position = default;
            if (string.IsNullOrWhiteSpace(prefab)) return false;
            EnsureBootstrapped();

            string key = WorkerStorageRuntime.NormalizeObjectName(prefab);
            float safeRadius = Mathf.Max(4f, radius);
            int wanted = Math.Max(1, preferredAmount);
            bool found = TryFindIndexedItemSource(key, center, safeRadius, preferNear, wanted,
                acceptContainer, protectedAmount, out source, out usable, out int totalKnownUsable, out position);
            // One chest can satisfy the batch, or several already-cached locations together can.
            // In the latter case we deliberately do NOT rescan the world: take from the best known
            // source now and let the remaining demand select the next cached source afterwards.
            if (found && (usable >= wanted || totalKnownUsable >= wanted)) return true;

            // Cached stock is genuinely insufficient (or absent). Only now is one bounded source
            // refresh allowed. This is the exact "trust cache first, rescan only when insufficient"
            // behaviour requested for Sorter/Versorger logistics.
            if (!MayRescanMiss(key, center, safeRadius)) return found;
            RefreshArea(center, safeRadius);
            MarkMissRescan(key, center, safeRadius);
            return TryFindIndexedItemSource(key, center, safeRadius, preferNear, wanted,
                acceptContainer, protectedAmount, out source, out usable, out _, out position);
        }

        /// <summary>
        /// Cache-only source lookup. Unlike TryFindItemSource this never performs the bounded
        /// miss refresh. Multi-option systems (for example Worker food priorities) use this to
        /// test several acceptable prefabs without turning one demand into several area rescans.
        /// </summary>
        internal static bool TryFindCachedItemSource(string prefab, Vector3 center, float radius, Vector3 preferNear,
            int preferredAmount, Func<Container, bool> acceptContainer, Func<Container, int> protectedAmount,
            out Container source, out int usable, out Vector3 position)
        {
            source = null;
            usable = 0;
            position = default;
            if (string.IsNullOrWhiteSpace(prefab)) return false;
            EnsureBootstrapped();

            string key = WorkerStorageRuntime.NormalizeObjectName(prefab);
            float safeRadius = Mathf.Max(4f, radius);
            int wanted = Math.Max(1, preferredAmount);
            return TryFindIndexedItemSource(key, center, safeRadius, preferNear, wanted,
                acceptContainer, protectedAmount, out source, out usable, out _, out position);
        }

        /// <summary>
        /// Same reverse index, but for marker-style normal storage destinations. A matching chest
        /// must already contain the item and pass the caller's room/access predicate.
        /// </summary>
        internal static bool TryFindItemDestination(string prefab, Vector3 center, float radius, Vector3 preferNear,
            Func<Container, bool> acceptContainer, out Container destination, out Vector3 position)
        {
            destination = null;
            position = default;
            if (string.IsNullOrWhiteSpace(prefab)) return false;
            EnsureBootstrapped();

            string key = WorkerStorageRuntime.NormalizeObjectName(prefab);
            float safeRadius = Mathf.Max(4f, radius);
            if (TryFindIndexedItemDestination(key, center, safeRadius, preferNear, acceptContainer,
                    out destination, out position))
                return true;

            if (!MayRescanMiss(key, center, safeRadius)) return false;
            RefreshArea(center, safeRadius);
            MarkMissRescan(key, center, safeRadius);
            return TryFindIndexedItemDestination(key, center, safeRadius, preferNear, acceptContainer,
                out destination, out position);
        }

        /// <summary>
        /// v0.5.346: exposes the already-maintained container snapshots to endpoint consumers such
        /// as Import -> Base sorting. This never opens inventories and never performs a world scan.
        /// A non-positive radius means "all currently indexed/loaded containers".
        /// </summary>
        internal static void CollectCachedItemSources(Vector3 center, float radius,
            Func<Container, bool> acceptContainer, List<CachedItemSource> output)
        {
            if (output == null) return;
            output.Clear();
            EnsureBootstrapped();

            bool bounded = radius > 0f;
            float radiusSqr = bounded ? radius * radius : 0f;
            StaleContainerKeys.Clear();
            foreach (KeyValuePair<int, ContainerSnapshot> pair in Containers)
            {
                ContainerSnapshot snapshot = pair.Value;
                Container candidate = snapshot?.Container;
                if (snapshot == null || candidate == null || candidate.gameObject == null || !candidate.gameObject.activeInHierarchy)
                {
                    StaleContainerKeys.Add(pair.Key);
                    continue;
                }

                Vector3 position = candidate.transform.position;
                snapshot.Position = position;
                if (bounded && (position - center).sqrMagnitude > radiusSqr) continue;
                if (acceptContainer != null && !acceptContainer(candidate)) continue;

                foreach (KeyValuePair<string, int> stock in snapshot.Counts)
                {
                    if (stock.Value <= 0 || string.IsNullOrWhiteSpace(stock.Key)) continue;
                    output.Add(new CachedItemSource(candidate, stock.Key, stock.Value, position));
                }
            }

            for (int i = 0; i < StaleContainerKeys.Count; i++)
            {
                int id = StaleContainerKeys[i];
                if (Containers.TryGetValue(id, out ContainerSnapshot stale) && stale != null)
                    RemoveSnapshot(id, stale);
            }
            StaleContainerKeys.Clear();
        }


        /// <summary>
        /// v0.5.354: aggregate already-cached container snapshots for one Worker storage domain.
        /// This is used by cooking/production availability probes so they never open every chest
        /// inventory merely to answer "how much stock exists?". The two circles mirror the legacy
        /// Home + WorkArea lookup and are evaluated in one pass, so overlapping areas never double-count.
        /// </summary>
        internal static void BuildCachedStockSnapshot(Vector3 firstCenter, float firstRadius,
            bool hasSecondArea, Vector3 secondCenter, float secondRadius,
            Func<Container, bool> acceptContainer, Dictionary<string, int> output)
        {
            if (output == null) return;
            output.Clear();
            EnsureBootstrapped();

            float firstSafeRadius = Mathf.Max(0f, firstRadius);
            float firstRadiusSqr = firstSafeRadius * firstSafeRadius;
            float secondSafeRadius = Mathf.Max(0f, secondRadius);
            float secondRadiusSqr = secondSafeRadius * secondSafeRadius;

            StaleContainerKeys.Clear();
            foreach (KeyValuePair<int, ContainerSnapshot> pair in Containers)
            {
                ContainerSnapshot snapshot = pair.Value;
                Container candidate = snapshot?.Container;
                if (snapshot == null || candidate == null || candidate.gameObject == null || !candidate.gameObject.activeInHierarchy)
                {
                    StaleContainerKeys.Add(pair.Key);
                    continue;
                }

                Vector3 position = candidate.transform.position;
                snapshot.Position = position;
                bool insideFirst = firstSafeRadius > 0f && (position - firstCenter).sqrMagnitude <= firstRadiusSqr;
                bool insideSecond = hasSecondArea && secondSafeRadius > 0f &&
                                    (position - secondCenter).sqrMagnitude <= secondRadiusSqr;
                if (!insideFirst && !insideSecond) continue;
                if (acceptContainer != null && !acceptContainer(candidate)) continue;

                foreach (KeyValuePair<string, int> stock in snapshot.Counts)
                {
                    if (stock.Value <= 0 || string.IsNullOrWhiteSpace(stock.Key)) continue;
                    output.TryGetValue(stock.Key, out int existing);
                    long next = (long)existing + stock.Value;
                    output[stock.Key] = next >= int.MaxValue ? int.MaxValue : (int)next;
                }
            }

            for (int i = 0; i < StaleContainerKeys.Count; i++)
            {
                int id = StaleContainerKeys[i];
                if (Containers.TryGetValue(id, out ContainerSnapshot stale) && stale != null)
                    RemoveSnapshot(id, stale);
            }
            StaleContainerKeys.Clear();
        }

        internal static int CountCachedContainers(Func<Container, bool> acceptContainer)
        {
            EnsureBootstrapped();
            int count = 0;
            StaleContainerKeys.Clear();
            foreach (KeyValuePair<int, ContainerSnapshot> pair in Containers)
            {
                ContainerSnapshot snapshot = pair.Value;
                Container candidate = snapshot?.Container;
                if (snapshot == null || candidate == null || candidate.gameObject == null || !candidate.gameObject.activeInHierarchy)
                {
                    StaleContainerKeys.Add(pair.Key);
                    continue;
                }
                if (acceptContainer == null || acceptContainer(candidate)) count++;
            }
            for (int i = 0; i < StaleContainerKeys.Count; i++)
            {
                int id = StaleContainerKeys[i];
                if (Containers.TryGetValue(id, out ContainerSnapshot stale) && stale != null)
                    RemoveSnapshot(id, stale);
            }
            StaleContainerKeys.Clear();
            return count;
        }

        internal static int GetCachedLocationCount(string prefab)
        {
            if (string.IsNullOrWhiteSpace(prefab)) return 0;
            EnsureBootstrapped();
            string key = WorkerStorageRuntime.NormalizeObjectName(prefab);
            return ItemLocations.TryGetValue(key, out HashSet<int> locations) && locations != null ? locations.Count : 0;
        }

        private static bool TryFindIndexedItemSource(string prefab, Vector3 center, float radius, Vector3 preferNear,
            int preferredAmount, Func<Container, bool> acceptContainer, Func<Container, int> protectedAmount,
            out Container source, out int usable, out int totalKnownUsable, out Vector3 position)
        {
            source = null;
            usable = 0;
            totalKnownUsable = 0;
            position = default;
            if (!ItemLocations.TryGetValue(prefab, out HashSet<int> locations) || locations == null || locations.Count == 0)
                return false;

            LocationScratch.Clear();
            foreach (int id in locations) LocationScratch.Add(id);
            float radiusSqr = radius * radius;
            float bestSqr = float.MaxValue;
            int bestMovable = 0;
            bool bestMeetsPreferred = false;

            for (int i = 0; i < LocationScratch.Count; i++)
            {
                int id = LocationScratch[i];
                if (!Containers.TryGetValue(id, out ContainerSnapshot snapshot) || snapshot == null || snapshot.Container == null)
                {
                    RemoveLocation(prefab, id);
                    continue;
                }

                Container candidate = snapshot.Container;
                if (candidate.gameObject == null || !candidate.gameObject.activeInHierarchy)
                {
                    RemoveSnapshot(id, snapshot);
                    continue;
                }
                Vector3 candidatePosition = candidate.transform.position;
                if ((candidatePosition - center).sqrMagnitude > radiusSqr) continue;
                if (acceptContainer != null && !acceptContainer(candidate)) continue;

                Inventory inventory = null;
                try { inventory = candidate.GetInventory(); } catch { }
                if (inventory == null) continue;
                int actual = WorkerStorageRuntime.CountPrefab(inventory, prefab);
                snapshot.Counts.TryGetValue(prefab, out int cached);
                if (actual != cached) CaptureContainer(candidate);
                if (actual <= 0) continue;

                int protect = protectedAmount != null ? Math.Max(0, protectedAmount(candidate)) : 0;
                int movable = Math.Max(0, actual - protect);
                if (movable <= 0) continue;
                long combined = (long)totalKnownUsable + movable;
                totalKnownUsable = combined >= int.MaxValue ? int.MaxValue : (int)combined;

                float sqr = (candidatePosition - preferNear).sqrMagnitude;
                bool meetsPreferred = movable >= preferredAmount;
                bool better = source == null ||
                              (meetsPreferred && !bestMeetsPreferred) ||
                              (meetsPreferred == bestMeetsPreferred &&
                               (meetsPreferred ? sqr < bestSqr : movable > bestMovable || (movable == bestMovable && sqr < bestSqr)));
                if (!better) continue;
                bestSqr = sqr;
                bestMovable = movable;
                bestMeetsPreferred = meetsPreferred;
                source = candidate;
                usable = movable;
                position = candidatePosition;
            }

            LocationScratch.Clear();
            return source != null && usable > 0;
        }

        private static bool TryFindIndexedItemDestination(string prefab, Vector3 center, float radius, Vector3 preferNear,
            Func<Container, bool> acceptContainer, out Container destination, out Vector3 position)
        {
            destination = null;
            position = default;
            if (!ItemLocations.TryGetValue(prefab, out HashSet<int> locations) || locations == null || locations.Count == 0)
                return false;

            LocationScratch.Clear();
            foreach (int id in locations) LocationScratch.Add(id);
            float radiusSqr = radius * radius;
            float bestSqr = float.MaxValue;

            for (int i = 0; i < LocationScratch.Count; i++)
            {
                int id = LocationScratch[i];
                if (!Containers.TryGetValue(id, out ContainerSnapshot snapshot) || snapshot == null || snapshot.Container == null)
                {
                    RemoveLocation(prefab, id);
                    continue;
                }
                Container candidate = snapshot.Container;
                if (candidate.gameObject == null || !candidate.gameObject.activeInHierarchy)
                {
                    RemoveSnapshot(id, snapshot);
                    continue;
                }
                Vector3 candidatePosition = candidate.transform.position;
                if ((candidatePosition - center).sqrMagnitude > radiusSqr) continue;

                Inventory inventory = null;
                try { inventory = candidate.GetInventory(); } catch { }
                if (inventory == null) continue;
                int actual = WorkerStorageRuntime.CountPrefab(inventory, prefab);
                snapshot.Counts.TryGetValue(prefab, out int cached);
                if (actual != cached) CaptureContainer(candidate);
                if (actual <= 0) continue;
                if (acceptContainer != null && !acceptContainer(candidate)) continue;

                float sqr = (candidatePosition - preferNear).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                destination = candidate;
                position = candidatePosition;
            }

            LocationScratch.Clear();
            return destination != null;
        }

        private static bool MayRescanMiss(string prefab, Vector3 center, float radius)
        {
            string key = BuildMissKey(prefab, center, radius);
            return !MissRescanUntil.TryGetValue(key, out float until) || Time.unscaledTime >= until;
        }

        private static void MarkMissRescan(string prefab, Vector3 center, float radius)
        {
            MissRescanUntil[BuildMissKey(prefab, center, radius)] = Time.unscaledTime + MissRescanCooldownSeconds;
        }

        private static string BuildMissKey(string prefab, Vector3 center, float radius)
        {
            // Coarse cells deliberately merge nearby Workers asking for the same missing item.
            int x = Mathf.RoundToInt(center.x / 32f);
            int z = Mathf.RoundToInt(center.z / 32f);
            int r = Mathf.RoundToInt(radius / 16f);
            return prefab + "|" + x + "|" + z + "|" + r;
        }

        private static void RefreshArea(Vector3 center, float radius)
        {
            try
            {
                WorkerWorldIndex.ForEachContainer(center, radius, candidate =>
                {
                    if (candidate != null) CaptureContainer(candidate);
                });
            }
            catch { }
        }

        private static AreaState EnsureArea(string areaId, Vector3 center, float radius)
        {
            EnsureBootstrapped();
            string areaKey = string.IsNullOrWhiteSpace(areaId) ? "base" : areaId;
            float safeRadius = Mathf.Clamp(radius, 8f, 160f);
            if (!Areas.TryGetValue(areaKey, out AreaState area))
            {
                area = new AreaState();
                Areas[areaKey] = area;
            }

            bool geometryChanged = !area.Initialized ||
                                   (area.Center - center).sqrMagnitude > 1f ||
                                   Mathf.Abs(area.Radius - safeRadius) > 0.5f;
            if (geometryChanged)
            {
                area.Center = center;
                area.Radius = safeRadius;
                area.RadiusSqr = safeRadius * safeRadius;
                RebuildArea(area);
                area.Initialized = true;
                area.NextReconcile = Time.unscaledTime + ReconcileSeconds;
            }
            else if (Time.unscaledTime >= area.NextReconcile)
            {
                // Rare safety net for external mods that mutate inventories without calling
                // Container.Save. Normal operation is entirely event-driven.
                ReconcileLoadedContainers();
                RebuildArea(area);
                area.NextReconcile = Time.unscaledTime + ReconcileSeconds;
            }

            return area;
        }

        private static void EnsureBootstrapped()
        {
            if (_bootstrapped) return;
            _bootstrapped = true;
            // Normal world loading reaches Container.Awake and has already populated the index.
            // Only use the one-shot loaded-container bootstrap when no lifecycle event was seen
            // (hot reload / unusual mod load order). Any partially missing item is repaired later by
            // the item-specific bounded miss scan instead of paying a full startup scan here.
            if (Containers.Count > 0) return;
            try { WorkerWorldIndex.ForEachLoadedContainer(CaptureContainer); }
            catch { }
        }

        private static void ReconcileLoadedContainers()
        {
            try { WorkerWorldIndex.ForEachLoadedContainer(CaptureContainer); }
            catch { }

            StaleContainerKeys.Clear();
            foreach (KeyValuePair<int, ContainerSnapshot> pair in Containers)
            {
                ContainerSnapshot snapshot = pair.Value;
                if (snapshot == null || snapshot.Container == null || snapshot.Container.gameObject == null)
                    StaleContainerKeys.Add(pair.Key);
            }
            for (int i = 0; i < StaleContainerKeys.Count; i++)
            {
                int key = StaleContainerKeys[i];
                if (!Containers.TryGetValue(key, out ContainerSnapshot old) || old == null) continue;
                RemoveSnapshot(key, old);
            }
            StaleContainerKeys.Clear();
        }

        private static void CaptureContainer(Container container)
        {
            if (container == null || container.gameObject == null) return;
            int key = container.GetInstanceID();
            if (LivestockFeedingRuntime.IsFeedingStation(container))
            {
                if (Containers.TryGetValue(key, out ContainerSnapshot feedingOld) && feedingOld != null)
                    RemoveSnapshot(key, feedingOld);
                return;
            }

            Inventory inventory = null;
            try { inventory = container.GetInventory(); }
            catch { }
            if (inventory == null) return;

            ScratchCounts.Clear();
            try
            {
                List<ItemDrop.ItemData> items = inventory.GetAllItems();
                if (items != null)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        ItemDrop.ItemData item = items[i];
                        if (item == null || item.m_stack <= 0) continue;
                        string prefab = WorkerStorageRuntime.NormalizeObjectName(
                            item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared?.m_name);
                        if (string.IsNullOrWhiteSpace(prefab)) continue;
                        ScratchCounts.TryGetValue(prefab, out int existing);
                        ScratchCounts[prefab] = existing + item.m_stack;
                    }
                }
            }
            catch
            {
                ScratchCounts.Clear();
                return;
            }

            Containers.TryGetValue(key, out ContainerSnapshot oldSnapshot);
            if (oldSnapshot != null)
            {
                RemoveSnapshotFromAreas(oldSnapshot);
                RemoveSnapshotFromItemLocations(key, oldSnapshot);
            }

            ContainerSnapshot snapshot = oldSnapshot ?? new ContainerSnapshot();
            snapshot.Container = container;
            snapshot.Position = container.transform.position;
            snapshot.Counts.Clear();
            foreach (KeyValuePair<string, int> pair in ScratchCounts)
                snapshot.Counts[pair.Key] = pair.Value;
            Containers[key] = snapshot;

            AddSnapshotToAreas(snapshot);
            AddSnapshotToItemLocations(key, snapshot);
            ScratchCounts.Clear();
        }

        private static void AddSnapshotToItemLocations(int id, ContainerSnapshot snapshot)
        {
            if (snapshot == null) return;
            foreach (KeyValuePair<string, int> pair in snapshot.Counts)
            {
                if (pair.Value <= 0 || string.IsNullOrWhiteSpace(pair.Key)) continue;
                if (!ItemLocations.TryGetValue(pair.Key, out HashSet<int> locations) || locations == null)
                {
                    locations = new HashSet<int>();
                    ItemLocations[pair.Key] = locations;
                }
                locations.Add(id);
            }
        }

        private static void RemoveSnapshotFromItemLocations(int id, ContainerSnapshot snapshot)
        {
            if (snapshot == null) return;
            foreach (KeyValuePair<string, int> pair in snapshot.Counts)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                RemoveLocation(pair.Key, id);
            }
        }

        private static void RemoveLocation(string prefab, int id)
        {
            if (string.IsNullOrWhiteSpace(prefab)) return;
            if (!ItemLocations.TryGetValue(prefab, out HashSet<int> locations) || locations == null) return;
            locations.Remove(id);
            if (locations.Count == 0) ItemLocations.Remove(prefab);
        }

        private static void RemoveSnapshot(int id, ContainerSnapshot snapshot)
        {
            if (snapshot != null)
            {
                RemoveSnapshotFromAreas(snapshot);
                RemoveSnapshotFromItemLocations(id, snapshot);
            }
            Containers.Remove(id);
        }

        private static void RebuildArea(AreaState area)
        {
            area.Counts.Clear();
            foreach (KeyValuePair<int, ContainerSnapshot> pair in Containers)
            {
                ContainerSnapshot snapshot = pair.Value;
                if (!IsInside(area, snapshot)) continue;
                ApplyCounts(area.Counts, snapshot.Counts, +1);
            }
        }

        private static void AddSnapshotToAreas(ContainerSnapshot snapshot)
        {
            foreach (KeyValuePair<string, AreaState> pair in Areas)
            {
                AreaState area = pair.Value;
                if (area == null || !area.Initialized || !IsInside(area, snapshot)) continue;
                ApplyCounts(area.Counts, snapshot.Counts, +1);
            }
        }

        private static void RemoveSnapshotFromAreas(ContainerSnapshot snapshot)
        {
            foreach (KeyValuePair<string, AreaState> pair in Areas)
            {
                AreaState area = pair.Value;
                if (area == null || !area.Initialized || !IsInside(area, snapshot)) continue;
                ApplyCounts(area.Counts, snapshot.Counts, -1);
            }
        }

        private static bool IsInside(AreaState area, ContainerSnapshot snapshot)
        {
            return area != null && snapshot != null && (snapshot.Position - area.Center).sqrMagnitude <= area.RadiusSqr;
        }

        private static void ApplyCounts(Dictionary<string, int> target, Dictionary<string, int> source, int sign)
        {
            foreach (KeyValuePair<string, int> pair in source)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == 0) continue;
                target.TryGetValue(pair.Key, out int old);
                int next = old + pair.Value * sign;
                if (next <= 0) target.Remove(pair.Key);
                else target[pair.Key] = next;
            }
        }
    }
}
