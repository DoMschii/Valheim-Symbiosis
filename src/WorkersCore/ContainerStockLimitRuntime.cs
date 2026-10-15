using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Per-item automatic-delivery limits.
    /// Import chests keep their historical per-container Min/Max behaviour.
    /// AirFreight chests inside the same Workzone share one Min/Max rule and one combined stock
    /// ceiling across every AirFreight chest in that Workzone.
    /// </summary>
    internal static class ContainerStockLimitRuntime
    {
        internal const string MinKey = "vw_item_stock_min";
        internal const string MaxKey = "vw_item_stock_max";
        internal const string PausedKey = "vw_item_stock_paused";

        internal static bool SupportsStockLimits(Container container)
        {
            if (container == null) return false;
            WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(container);
            return role == WorkerContainerRole.Import ||
                   (role == WorkerContainerRole.FieldDepot && WorkerContainerRoleRuntime.SupportsAirTransport(container));
        }

        internal static bool SupportsStockLimits(ZDO zdo)
        {
            if (zdo == null) return false;
            WorkerContainerRole role = (WorkerContainerRole)Mathf.Clamp(zdo.GetInt(WorkerContainerRoleRuntime.RoleKey, 0), 0, 3);
            return role == WorkerContainerRole.Import ||
                   (role == WorkerContainerRole.FieldDepot && zdo.GetBool(WorkerContainerRoleRuntime.AirTransportKey, false));
        }

        internal static bool HasRule(Container container, string prefab)
        {
            return container != null && HasRule(WorkerContainerRoleRuntime.GetZdo(container), prefab);
        }

        internal static bool HasRule(ZDO zdo, string prefab)
        {
            if (zdo == null || string.IsNullOrWhiteSpace(prefab)) return false;
            ZDO ruleZdo = ResolveRuleZdo(zdo, prefab, migrateLegacy: true);
            return HasLocalRule(ruleZdo, prefab);
        }

        internal static int GetMin(Container container, string prefab)
        {
            return GetMin(WorkerContainerRoleRuntime.GetZdo(container), prefab);
        }

        internal static int GetMin(ZDO zdo, string prefab)
        {
            if (zdo == null || string.IsNullOrWhiteSpace(prefab)) return 0;
            ZDO ruleZdo = ResolveRuleZdo(zdo, prefab, migrateLegacy: true);
            return GetLocalMin(ruleZdo, prefab);
        }

        internal static int GetMax(Container container, string prefab)
        {
            return GetMax(WorkerContainerRoleRuntime.GetZdo(container), prefab);
        }

        internal static int GetMax(ZDO zdo, string prefab)
        {
            if (zdo == null || string.IsNullOrWhiteSpace(prefab)) return 0;
            ZDO ruleZdo = ResolveRuleZdo(zdo, prefab, migrateLegacy: true);
            return GetLocalMax(ruleZdo, prefab);
        }

        internal static int GetCurrentStock(Container container, string prefab)
        {
            if (container == null || string.IsNullOrWhiteSpace(prefab)) return 0;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(container);
            if (zdo == null) return 0;
            ContainerLedgerRuntime.SyncFromPhysical(container, "stock-limit-ui");
            return GetCurrentAutomaticStock(zdo, prefab);
        }

        internal static string GetScopeDisplayName(Container container)
        {
            if (container != null && WorkerContainerRoleRuntime.SupportsAirTransport(container))
                return AirFreightGroupRuntime.DescribeGroup(container);
            return "diese Kiste";
        }

        internal static bool SetRule(Container container, string prefab, int minimum, int maximum)
        {
            if (container == null || string.IsNullOrWhiteSpace(prefab) || maximum <= 0) return false;
            if (!SupportsStockLimits(container)) return false;
            if (!WorkerStorageRuntime.TryOwn(container)) return false;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(container);
            if (zdo == null) return false;

            int max = Mathf.Max(1, maximum);
            int min = Mathf.Clamp(minimum, 0, Mathf.Max(0, max - 1));
            ZDO ruleZdo = ResolveRuleZdo(zdo, prefab, migrateLegacy: true) ?? zdo;
            ApplyRuleToZdo(ruleZdo, prefab, min, max);

            if (AirFreightGroupRuntime.IsAirFreight(zdo) && AirFreightGroupRuntime.TryResolveGroup(zdo, out _, out ZDO groupFlag))
            {
                if (groupFlag != null && groupFlag != ruleZdo) ApplyRuleToZdo(groupFlag, prefab, min, max);
                AirFreightGroupRuntime.ForEachGroupMember(zdo, member => ApplyRuleToZdo(member, prefab, min, max));
            }

            int current = AirFreightGroupRuntime.IsAirFreight(zdo)
                ? GetCurrentAutomaticStock(zdo, prefab)
                : WorkerStorageRuntime.CountPrefab(container.GetInventory(), prefab) + AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(zdo, prefab);
            bool paused = ReevaluatePause(ruleZdo, prefab, current, min, max);
            MirrorPausedStateToAirGroup(zdo, prefab, paused);
            PersistentContainerRegistryRuntime.Remember(zdo);
            BackgroundWorkerScheduler.RequestPersistentLogisticsRescan();
            ProductionDemandRuntime.InvalidateAll();
            return true;
        }

        internal static bool RemoveRule(Container container, string prefab)
        {
            if (container == null || string.IsNullOrWhiteSpace(prefab)) return false;
            if (!WorkerStorageRuntime.TryOwn(container)) return false;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(container);
            if (zdo == null) return false;

            bool changed = false;
            ZDO ruleZdo = ResolveRuleZdo(zdo, prefab, migrateLegacy: false) ?? zdo;
            changed |= RemoveRuleFromZdo(ruleZdo, prefab);

            if (AirFreightGroupRuntime.IsAirFreight(zdo) && AirFreightGroupRuntime.TryResolveGroup(zdo, out _, out ZDO groupFlag))
            {
                if (groupFlag != null && groupFlag != ruleZdo) changed |= RemoveRuleFromZdo(groupFlag, prefab);
                AirFreightGroupRuntime.ForEachGroupMember(zdo, member =>
                {
                    if (RemoveRuleFromZdo(member, prefab)) changed = true;
                });
            }

            if (!changed) return true;
            PersistentContainerRegistryRuntime.Remember(zdo);
            BackgroundWorkerScheduler.RequestPersistentLogisticsRescan();
            ProductionDemandRuntime.InvalidateAll();
            return true;
        }

        internal static bool IsPaused(Container container, string prefab)
        {
            if (container == null || string.IsNullOrWhiteSpace(prefab)) return false;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(container);
            if (zdo == null || !HasRule(zdo, prefab)) return false;
            ContainerLedgerRuntime.SyncFromPhysical(container, "stock-limit-pause");
            ZDO ruleZdo = ResolveRuleZdo(zdo, prefab, migrateLegacy: true) ?? zdo;
            int min = GetLocalMin(ruleZdo, prefab);
            int max = GetLocalMax(ruleZdo, prefab);
            int current = AirFreightGroupRuntime.IsAirFreight(zdo)
                ? GetCurrentAutomaticStock(zdo, prefab)
                : WorkerStorageRuntime.CountPrefab(container.GetInventory(), prefab) + AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(zdo, prefab);
            bool paused = ReevaluatePause(ruleZdo, prefab, current, min, max);
            MirrorPausedStateToAirGroup(zdo, prefab, paused);
            return paused;
        }

        internal static int GetAcceptCapacity(Container container, string prefab, int physicalFreeCapacity)
        {
            if (container == null || string.IsNullOrWhiteSpace(prefab)) return 0;
            int physical = Mathf.Max(0, physicalFreeCapacity);
            if (!SupportsStockLimits(container)) return physical;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(container);
            if (zdo == null || !HasRule(zdo, prefab)) return physical;

            ZDO ruleZdo = ResolveRuleZdo(zdo, prefab, migrateLegacy: true) ?? zdo;
            int min = GetLocalMin(ruleZdo, prefab);
            int max = GetLocalMax(ruleZdo, prefab);
            if (max <= 0) return physical;
            int current = AirFreightGroupRuntime.IsAirFreight(zdo)
                ? GetCurrentAutomaticStock(zdo, prefab)
                : WorkerStorageRuntime.CountPrefab(container.GetInventory(), prefab) + AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(zdo, prefab);
            bool paused = ReevaluatePause(ruleZdo, prefab, current, min, max);
            MirrorPausedStateToAirGroup(zdo, prefab, paused);
            if (paused) return 0;
            return Mathf.Min(physical, Mathf.Max(0, max - current));
        }

        internal static int GetPersistentAcceptCapacity(ZDO zdo, string prefab)
        {
            if (zdo == null || string.IsNullOrWhiteSpace(prefab)) return 0;
            if (!SupportsStockLimits(zdo)) return 0;

            bool configured = HasRule(zdo, prefab);
            int physical = int.MaxValue;
            if (ContainerLedgerRuntime.TryGetPersistentFreeCapacity(zdo, prefab, out int knownFree))
                physical = Mathf.Max(0, knownFree);
            if (!configured) return physical;

            ZDO ruleZdo = ResolveRuleZdo(zdo, prefab, migrateLegacy: true) ?? zdo;
            int min = GetLocalMin(ruleZdo, prefab);
            int max = GetLocalMax(ruleZdo, prefab);
            if (max <= 0) return physical;
            int current = GetCurrentAutomaticStock(zdo, prefab);
            bool paused = ReevaluatePause(ruleZdo, prefab, current, min, max);
            MirrorPausedStateToAirGroup(zdo, prefab, paused);
            if (paused) return 0;
            int ruleFree = Mathf.Max(0, max - current);
            return physical == int.MaxValue ? ruleFree : Mathf.Min(physical, ruleFree);
        }

        internal static bool HasConfiguredImportLimit(long ownerId, string prefab)
        {
            if (string.IsNullOrWhiteSpace(prefab)) return false;
            bool found = false;
            WorkerStorageRuntime.ForEachLoadedContainer(c =>
            {
                if (found || c == null || WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.Import) return;
                if (!WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                if (HasRule(c, prefab)) found = true;
            });
            if (found) return true;

            PersistentContainerRegistryRuntime.ForEachPersistent((zdo, role, owner, position) =>
            {
                if (found || zdo == null || role != WorkerContainerRole.Import) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                if (HasRule(zdo, prefab)) found = true;
            });
            return found;
        }

        internal static bool TryFindPersistentImportWithCapacity(long ownerId, Vector3 near, string prefab,
            out ZDO importZdo, out Vector3 position, out int capacity)
        {
            importZdo = null;
            position = default;
            capacity = 0;
            if (string.IsNullOrWhiteSpace(prefab)) return false;

            ZDO bestZdo = null;
            Vector3 bestPosition = default;
            int bestCapacity = 0;
            float bestSqr = float.MaxValue;
            PersistentContainerRegistryRuntime.ForEachPersistent((candidate, role, owner, p) =>
            {
                if (candidate == null || role != WorkerContainerRole.Import) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                int free = GetPersistentAcceptCapacity(candidate, prefab);
                if (free <= 0) return;
                float sqr = (p - near).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                bestZdo = candidate;
                bestPosition = p;
                bestCapacity = free;
            });

            importZdo = bestZdo;
            position = bestPosition;
            capacity = bestCapacity;
            return importZdo != null && capacity > 0;
        }

        internal static void CollectConfiguredPrefabs(Container container, List<string> output)
        {
            if (output == null) return;
            output.Clear();
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(container);
            if (zdo == null) return;
            ZDO ruleZdo = ResolveRuleZdo(zdo, null, migrateLegacy: false) ?? zdo;
            foreach (string prefab in ParseIntMap(ruleZdo.GetString(MaxKey, string.Empty)).Keys)
                output.Add(prefab);
            output.Sort(StringComparer.OrdinalIgnoreCase);
        }

        private static int GetCurrentAutomaticStock(ZDO zdo, string prefab)
        {
            if (zdo == null || string.IsNullOrWhiteSpace(prefab)) return 0;
            if (AirFreightGroupRuntime.IsAirFreight(zdo) && AirFreightGroupRuntime.TryGetGroupId(zdo, out _))
                return AirFreightGroupRuntime.GetCombinedStock(zdo, prefab);

            ContainerLedgerRuntime.EnsureSnapshotFromPersistentZdo(zdo);
            return Mathf.Max(0, ContainerLedgerRuntime.GetSnapshotAmount(zdo, prefab)) +
                   Mathf.Max(0, AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(zdo, prefab));
        }

        private static ZDO ResolveRuleZdo(ZDO zdo, string prefab, bool migrateLegacy)
        {
            if (zdo == null || !AirFreightGroupRuntime.IsAirFreight(zdo)) return zdo;
            if (!AirFreightGroupRuntime.TryResolveGroup(zdo, out _, out ZDO flagZdo)) return zdo;
            if (flagZdo == null) return zdo;

            if (string.IsNullOrWhiteSpace(prefab) || HasLocalRule(flagZdo, prefab)) return flagZdo;
            if (!migrateLegacy) return flagZdo;

            int legacyMin = 0;
            int legacyMax = 0;
            AirFreightGroupRuntime.ForEachGroupMember(zdo, member =>
            {
                if (legacyMax > 0 || !HasLocalRule(member, prefab)) return;
                legacyMin = GetLocalMin(member, prefab);
                legacyMax = GetLocalMax(member, prefab);
            });
            if (legacyMax > 0)
            {
                ApplyRuleToZdo(flagZdo, prefab, legacyMin, legacyMax);
                int current = GetCurrentAutomaticStock(zdo, prefab);
                bool paused = ReevaluatePause(flagZdo, prefab, current, legacyMin, legacyMax);
                MirrorPausedStateToAirGroup(zdo, prefab, paused);
            }
            return flagZdo;
        }

        private static bool HasLocalRule(ZDO zdo, string prefab)
        {
            if (zdo == null || string.IsNullOrWhiteSpace(prefab)) return false;
            return ParseIntMap(zdo.GetString(MaxKey, string.Empty)).ContainsKey(prefab);
        }

        private static int GetLocalMin(ZDO zdo, string prefab)
        {
            if (zdo == null || string.IsNullOrWhiteSpace(prefab)) return 0;
            Dictionary<string, int> values = ParseIntMap(zdo.GetString(MinKey, string.Empty));
            return values.TryGetValue(prefab, out int value) ? Mathf.Max(0, value) : 0;
        }

        private static int GetLocalMax(ZDO zdo, string prefab)
        {
            if (zdo == null || string.IsNullOrWhiteSpace(prefab)) return 0;
            Dictionary<string, int> values = ParseIntMap(zdo.GetString(MaxKey, string.Empty));
            return values.TryGetValue(prefab, out int value) ? Mathf.Max(1, value) : 0;
        }

        private static void ApplyRuleToZdo(ZDO zdo, string prefab, int minimum, int maximum)
        {
            if (zdo == null || string.IsNullOrWhiteSpace(prefab) || maximum <= 0) return;
            Dictionary<string, int> mins = ParseIntMap(zdo.GetString(MinKey, string.Empty));
            Dictionary<string, int> maxs = ParseIntMap(zdo.GetString(MaxKey, string.Empty));
            mins[prefab] = Mathf.Max(0, minimum);
            maxs[prefab] = Mathf.Max(1, maximum);
            zdo.Set(MinKey, SerializeIntMap(mins));
            zdo.Set(MaxKey, SerializeIntMap(maxs));
        }

        private static bool RemoveRuleFromZdo(ZDO zdo, string prefab)
        {
            if (zdo == null || string.IsNullOrWhiteSpace(prefab)) return false;
            Dictionary<string, int> mins = ParseIntMap(zdo.GetString(MinKey, string.Empty));
            Dictionary<string, int> maxs = ParseIntMap(zdo.GetString(MaxKey, string.Empty));
            HashSet<string> paused = ParseSet(zdo.GetString(PausedKey, string.Empty));
            bool changed = mins.Remove(prefab) | maxs.Remove(prefab) | paused.Remove(prefab);
            if (!changed) return false;
            zdo.Set(MinKey, SerializeIntMap(mins));
            zdo.Set(MaxKey, SerializeIntMap(maxs));
            zdo.Set(PausedKey, SerializeSet(paused));
            return true;
        }

        private static void MirrorPausedStateToAirGroup(ZDO zdo, string prefab, bool paused)
        {
            if (zdo == null || string.IsNullOrWhiteSpace(prefab) || !AirFreightGroupRuntime.IsAirFreight(zdo)) return;
            AirFreightGroupRuntime.ForEachGroupMember(zdo, member => SetPausedState(member, prefab, paused));
        }

        private static void SetPausedState(ZDO zdo, string prefab, bool paused)
        {
            if (zdo == null || string.IsNullOrWhiteSpace(prefab)) return;
            HashSet<string> values = ParseSet(zdo.GetString(PausedKey, string.Empty));
            bool changed = paused ? values.Add(prefab) : values.Remove(prefab);
            if (changed) zdo.Set(PausedKey, SerializeSet(values));
        }

        private static bool ReevaluatePause(ZDO zdo, string prefab, int current, int minimum, int maximum)
        {
            if (zdo == null || string.IsNullOrWhiteSpace(prefab) || maximum <= 0) return false;
            int min = Mathf.Clamp(minimum, 0, Mathf.Max(0, maximum - 1));
            int value = Mathf.Max(0, current);
            HashSet<string> paused = ParseSet(zdo.GetString(PausedKey, string.Empty));
            bool wasPaused = paused.Contains(prefab);
            bool nowPaused = wasPaused;
            if (value >= maximum) nowPaused = true;
            else if (wasPaused && value <= min) nowPaused = false;

            if (nowPaused != wasPaused)
            {
                if (nowPaused) paused.Add(prefab); else paused.Remove(prefab);
                zdo.Set(PausedKey, SerializeSet(paused));
                BackgroundWorkerScheduler.RequestPersistentLogisticsRescan();
                ProductionDemandRuntime.InvalidateAll();
            }
            return nowPaused;
        }

        private static Dictionary<string, int> ParseIntMap(string raw)
        {
            Dictionary<string, int> result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw)) return result;
            string[] entries = raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < entries.Length; i++)
            {
                int eq = entries[i].LastIndexOf('=');
                if (eq <= 0) continue;
                string prefab = entries[i].Substring(0, eq).Trim();
                if (string.IsNullOrEmpty(prefab) || !int.TryParse(entries[i].Substring(eq + 1), out int value)) continue;
                result[prefab] = Mathf.Max(0, value);
            }
            return result;
        }

        private static string SerializeIntMap(Dictionary<string, int> values)
        {
            if (values == null || values.Count == 0) return string.Empty;
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> pair in values)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                    parts.Add(pair.Key + "=" + Mathf.Max(0, pair.Value));
            }
            parts.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(";", parts);
        }

        private static HashSet<string> ParseSet(string raw)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw)) return result;
            string[] entries = raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < entries.Length; i++)
            {
                string prefab = entries[i].Trim();
                if (!string.IsNullOrEmpty(prefab)) result.Add(prefab);
            }
            return result;
        }

        private static string SerializeSet(HashSet<string> values)
        {
            if (values == null || values.Count == 0) return string.Empty;
            List<string> parts = new List<string>(values);
            parts.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(";", parts);
        }
    }
}
