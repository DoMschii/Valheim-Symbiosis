using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Per-FieldDepot stock policy.
    /// TransportReserve protects local stock from Haulers only; local workers may still consume it.
    /// v0.5.395 removed stock-driven seed production. ProductionTrigger/SeedMode keys are retained
    /// only so old saves deserialize safely; active FieldDepot policy uses TransportReserve only.
    /// </summary>
    internal static class FieldDepotStockRuleRuntime
    {
        internal const string TransportReserveKey = "vw_transport_reserves";
        internal const string ProductionTriggerKey = "vw_production_triggers";
        internal const string SeedModeKey = "vw_seed_modes";

        internal sealed class PolicySnapshot
        {
            internal readonly string TransportReserves;
            internal readonly string ProductionTriggers;
            internal readonly int ConfiguredItemCount;

            internal PolicySnapshot(string transportReserves, string productionTriggers, int configuredItemCount)
            {
                TransportReserves = transportReserves ?? string.Empty;
                ProductionTriggers = productionTriggers ?? string.Empty;
                ConfiguredItemCount = Mathf.Max(0, configuredItemCount);
            }
        }

        internal static PolicySnapshot CapturePolicy(Container depot)
        {
            if (depot == null || WorkerContainerRoleRuntime.GetRole(depot) != WorkerContainerRole.FieldDepot) return null;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            if (zdo == null) return null;

            string reservesRaw = zdo.GetString(TransportReserveKey, string.Empty);
            HashSet<string> configured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in ParseIntMap(reservesRaw).Keys) configured.Add(key);
            return new PolicySnapshot(reservesRaw, string.Empty, configured.Count);
        }

        internal static bool ApplyPolicy(Container depot, PolicySnapshot snapshot)
        {
            if (depot == null || snapshot == null) return false;
            if (WorkerContainerRoleRuntime.GetRole(depot) != WorkerContainerRole.FieldDepot) return false;
            if (!WorkerStorageRuntime.TryOwn(depot)) return false;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            if (zdo == null) return false;

            // v0.5.395: only Keep Items is active policy. Clear obsolete seed threshold/state data
            // on paste so old settings cannot reappear through copied FieldDepot policies.
            zdo.Set(TransportReserveKey, snapshot.TransportReserves ?? string.Empty);
            zdo.Set(ProductionTriggerKey, string.Empty);
            zdo.Set(SeedModeKey, string.Empty);

            PersistentContainerRegistryRuntime.Remember(zdo);
            FieldSupplyDemandCacheRuntime.OnRoleOrPolicyChanged(depot);
            return true;
        }

        internal static int GetTransportReserve(Container depot, string prefab)
        {
            if (depot == null || string.IsNullOrWhiteSpace(prefab)) return 0;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            if (zdo == null) return 0;
            Dictionary<string, int> values = ParseIntMap(zdo.GetString(TransportReserveKey, string.Empty));
            return values.TryGetValue(prefab, out int value) ? Mathf.Max(0, value) : 0;
        }

        internal static int GetTransportReserve(ZDO zdo, string prefab)
        {
            if (zdo == null || string.IsNullOrWhiteSpace(prefab)) return 0;
            Dictionary<string, int> values = ParseIntMap(zdo.GetString(TransportReserveKey, string.Empty));
            return values.TryGetValue(prefab, out int value) ? Mathf.Max(0, value) : 0;
        }

        internal static bool HasTransportReserve(Container depot, string prefab)
        {
            if (depot == null || string.IsNullOrWhiteSpace(prefab)) return false;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            if (zdo == null) return false;
            return ParseIntMap(zdo.GetString(TransportReserveKey, string.Empty)).ContainsKey(prefab);
        }

        internal static bool SetTransportReserve(Container depot, string prefab, int value)
        {
            if (depot == null || string.IsNullOrWhiteSpace(prefab)) return false;
            if (WorkerContainerRoleRuntime.GetRole(depot) != WorkerContainerRole.FieldDepot) return false;
            if (!WorkerStorageRuntime.TryOwn(depot)) return false;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            if (zdo == null) return false;

            // 0 is a valid explicit per-item setting: the Hauler may remove the complete stock
            // of this item from this specific FieldDepot. Keep the zero entry instead of deleting it
            // so the policy can distinguish "not configured" from "configured: keep nothing".
            int reserve = Mathf.Max(0, value);
            Dictionary<string, int> values = ParseIntMap(zdo.GetString(TransportReserveKey, string.Empty));
            bool changed = !values.TryGetValue(prefab, out int previousReserve) || previousReserve != reserve;
            values[prefab] = reserve;
            zdo.Set(TransportReserveKey, SerializeIntMap(values));
            if (changed) BackgroundWorkerScheduler.RequestPersistentLogisticsRescan();

            // Seed-production thresholds are legacy-only as of v0.5.395 and are deliberately
            // not evaluated when Keep Items changes.
            FieldSupplyDemandCacheRuntime.OnRoleOrPolicyChanged(depot);
            return true;
        }

        internal static bool ApplyTransportReserveBatch(Container depot, IDictionary<string, int> updates)
        {
            if (depot == null || updates == null) return false;
            if (WorkerContainerRoleRuntime.GetRole(depot) != WorkerContainerRole.FieldDepot) return false;
            if (!WorkerStorageRuntime.TryOwn(depot)) return false;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            if (zdo == null) return false;

            Dictionary<string, int> values = ParseIntMap(zdo.GetString(TransportReserveKey, string.Empty));
            bool changed = false;
            foreach (KeyValuePair<string, int> pair in updates)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                int reserve = Mathf.Max(0, pair.Value);
                if (!values.TryGetValue(pair.Key, out int previous) || previous != reserve) changed = true;
                values[pair.Key] = reserve;
            }

            if (changed)
            {
                zdo.Set(TransportReserveKey, SerializeIntMap(values));
                PersistentContainerRegistryRuntime.Remember(zdo);
                BackgroundWorkerScheduler.RequestPersistentLogisticsRescan();
                FieldSupplyDemandCacheRuntime.OnRoleOrPolicyChanged(depot);
            }
            return true;
        }

        internal static int GetProductionTrigger(Container depot, string prefab)
        {
            if (depot == null || string.IsNullOrWhiteSpace(prefab)) return 0;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            if (zdo == null) return 0;
            Dictionary<string, int> values = ParseIntMap(zdo.GetString(ProductionTriggerKey, string.Empty));
            return values.TryGetValue(prefab, out int value) ? Mathf.Max(0, value) : 0;
        }

        internal static bool HasProductionTrigger(Container depot, string prefab)
        {
            if (depot == null || string.IsNullOrWhiteSpace(prefab)) return false;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            if (zdo == null) return false;
            return ParseIntMap(zdo.GetString(ProductionTriggerKey, string.Empty)).ContainsKey(prefab);
        }

        internal static bool SetProductionTrigger(Container depot, string prefab, int value)
        {
            if (depot == null || string.IsNullOrWhiteSpace(prefab)) return false;
            if (WorkerContainerRoleRuntime.GetRole(depot) != WorkerContainerRole.FieldDepot) return false;
            if (!WorkerStorageRuntime.TryOwn(depot)) return false;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            if (zdo == null) return false;

            int reserve = GetTransportReserve(depot, prefab);
            int trigger = Mathf.Clamp(value, 0, Mathf.Max(0, reserve - 1));
            Dictionary<string, int> values = ParseIntMap(zdo.GetString(ProductionTriggerKey, string.Empty));
            values[prefab] = trigger;
            zdo.Set(ProductionTriggerKey, SerializeIntMap(values));
            ReevaluateStoredSeedMode(zdo, depot, prefab);
            return true;
        }

        internal static bool EvaluateSeedProductionMode(Container depot, string seedPrefab, int currentStock,
            out int transportReserve, out int productionTrigger)
        {
            return EvaluateSeedProductionMode(depot, seedPrefab, currentStock, 0, out transportReserve, out productionTrigger);
        }

        /// <summary>
        /// Evaluates the persistent seed-production hysteresis for a local FieldDepot.
        /// A configured Transportreserve is the preferred upper target. If none exists, the
        /// caller may provide a data-driven fallback (SmartReplant reserve / Workers default).
        /// An unconfigured production trigger intentionally defaults to 0: emergency seed
        /// production starts when the last seed is gone and continues until the reserve is full.
        /// Explicit triggers (including 0) still override that default.
        /// </summary>
        internal static bool EvaluateSeedProductionMode(Container depot, string seedPrefab, int currentStock, int fallbackReserve,
            out int transportReserve, out int productionTrigger)
        {
            int configuredReserve = GetTransportReserve(depot, seedPrefab);
            // An explicit zero is meaningful: Keep Items = 0 disables the protected upper stock
            // for this particular depot/item. Only an entirely unconfigured item uses the fallback.
            transportReserve = HasTransportReserve(depot, seedPrefab)
                ? Mathf.Max(0, configuredReserve)
                : Mathf.Max(0, fallbackReserve);
            productionTrigger = HasProductionTrigger(depot, seedPrefab) ? GetProductionTrigger(depot, seedPrefab) : 0;

            return EvaluateSeedProductionModeResolved(depot, seedPrefab, currentStock, transportReserve, productionTrigger);
        }

        /// <summary>
        /// Evaluates seed-production hysteresis against thresholds that were already resolved by a
        /// Workzone-level caller. The state still lives on one authoritative FieldDepot, but the
        /// current stock may represent the complete pooled Workzone inventory.
        /// </summary>
        internal static bool EvaluateSeedProductionModeResolved(Container stateDepot, string seedPrefab, int currentStock,
            int transportReserve, int productionTrigger)
        {
            transportReserve = Mathf.Max(0, transportReserve);
            productionTrigger = Mathf.Max(0, productionTrigger);
            if (stateDepot == null || string.IsNullOrWhiteSpace(seedPrefab) || transportReserve <= 0 || productionTrigger >= transportReserve)
            {
                SetSeedMode(stateDepot, seedPrefab, false);
                return false;
            }

            bool active = GetSeedMode(stateDepot, seedPrefab);
            if (!active && currentStock <= productionTrigger)
                active = true;
            else if (active && currentStock >= transportReserve)
                active = false;

            SetSeedMode(stateDepot, seedPrefab, active);
            return active;
        }

        internal static void CollectConfiguredPrefabs(Container depot, List<string> output)
        {
            if (output == null) return;
            output.Clear();
            if (depot == null) return;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            if (zdo == null) return;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> reserves = ParseIntMap(zdo.GetString(TransportReserveKey, string.Empty));
            foreach (string key in reserves.Keys) if (seen.Add(key)) output.Add(key);
            output.Sort(StringComparer.OrdinalIgnoreCase);
        }

        internal static void CollectConfiguredPrefabs(ZDO zdo, List<string> output)
        {
            if (output == null) return;
            output.Clear();
            if (zdo == null) return;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> reserves = ParseIntMap(zdo.GetString(TransportReserveKey, string.Empty));
            foreach (string key in reserves.Keys) if (seen.Add(key)) output.Add(key);
            output.Sort(StringComparer.OrdinalIgnoreCase);
        }

        internal static bool GetSeedMode(Container depot, string prefab)
        {
            if (depot == null || string.IsNullOrWhiteSpace(prefab)) return false;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            if (zdo == null) return false;
            HashSet<string> active = ParseSet(zdo.GetString(SeedModeKey, string.Empty));
            return active.Contains(prefab);
        }

        private static void SetSeedMode(Container depot, string prefab, bool enabled)
        {
            if (depot == null || string.IsNullOrWhiteSpace(prefab)) return;
            if (!WorkerStorageRuntime.TryOwn(depot)) return;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            if (zdo == null) return;
            HashSet<string> active = ParseSet(zdo.GetString(SeedModeKey, string.Empty));
            bool changed = enabled ? active.Add(prefab) : active.Remove(prefab);
            if (changed) zdo.Set(SeedModeKey, SerializeSet(active));
        }

        private static void ReevaluateStoredSeedMode(ZDO zdo, Container depot, string prefab)
        {
            if (zdo == null || depot == null || string.IsNullOrWhiteSpace(prefab)) return;
            int current = WorkerStorageRuntime.CountPrefab(depot.GetInventory(), prefab);
            EvaluateSeedProductionMode(depot, prefab, current, out _, out _);
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
                if (!string.IsNullOrWhiteSpace(pair.Key)) parts.Add(pair.Key + "=" + Mathf.Max(0, pair.Value));
            return string.Join(";", parts);
        }

        private static HashSet<string> ParseSet(string raw)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw)) return result;
            string[] entries = raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < entries.Length; i++)
            {
                string value = entries[i].Trim();
                if (!string.IsNullOrEmpty(value)) result.Add(value);
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
