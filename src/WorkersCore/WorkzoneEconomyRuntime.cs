using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    internal sealed class WorkzoneEconomyResourceRow
    {
        internal string Prefab = string.Empty;
        internal int Stock;
        internal int ProducerCount;
        internal int ConsumerCount;
        internal bool HasProductionModel;
        internal double ProductionPerDay;
        internal double LocalConsumptionPerDay;
        internal double DeliveryConsumptionPerDay;
        internal double ConsumptionPerDay;
        internal int ActualProduced;
        internal int ActualConsumed;

        internal double BalancePerDay => ProductionPerDay - ConsumptionPerDay;
        internal bool HasForecastActivity => HasProductionModel || ConsumptionPerDay > 0.0001;
    }

    internal sealed class WorkzoneEconomySnapshot
    {
        internal string AreaId = string.Empty;
        internal Vector3 Center;
        internal float Radius;
        internal int WorkerCount;
        internal int SustenanceWorkerCount;
        internal int SupplyReadyWorkerCount;
        internal int SupplyAutoDisabledWorkerCount;
        internal bool HasForecastDeficit;
        internal readonly List<WorkzoneEconomyResourceRow> Rows = new List<WorkzoneEconomyResourceRow>();

        internal bool TryGetRow(string prefab, out WorkzoneEconomyResourceRow row)
        {
            string wanted = WorkerStorageRuntime.NormalizeObjectName(prefab ?? string.Empty);
            for (int i = 0; i < Rows.Count; i++)
            {
                WorkzoneEconomyResourceRow candidate = Rows[i];
                if (candidate != null && string.Equals(candidate.Prefab, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    row = candidate;
                    return true;
                }
            }
            row = null;
            return false;
        }
    }

    /// <summary>
    /// v0.5.362: one cached economy view per Workzone. UI and production displays no longer build
    /// their own Worker/WildGrowth forecasts independently. The snapshot is demand-driven and only
    /// rebuilt when queried after its cache expires; there is no polling loop or per-frame scan.
    ///
    /// This economy layer intentionally uses only durable/cached state: completed WildGrowth yield,
    /// Worker sustenance demand, current cached storage, completed inter-zone deliveries and (since
    /// v0.5.365) rolling real production/consumption throughput. Later background simulation can add
    /// more deterministic producers/consumers here without changing the UI contract.
    /// </summary>
    internal static class WorkzoneEconomyRuntime
    {
        private sealed class CacheEntry
        {
            internal float ExpiresAt;
            internal Vector3 Center;
            internal float Radius;
            internal WorkzoneEconomySnapshot Snapshot;
        }

        private const float CacheSeconds = 30f;
        private static readonly Dictionary<string, CacheEntry> Cache =
            new Dictionary<string, CacheEntry>(StringComparer.Ordinal);

        internal static WorkzoneEconomySnapshot GetSnapshot(string areaId, Vector3 center, float radius)
        {
            string area = string.IsNullOrWhiteSpace(areaId) ? "base" : areaId;
            float safeRadius = Mathf.Max(4f, radius);
            string cacheKey = GetCacheKey(area, center, safeRadius);
            float now = Time.unscaledTime;
            if (Cache.TryGetValue(cacheKey, out CacheEntry cached) && cached != null && cached.Snapshot != null &&
                now < cached.ExpiresAt && (cached.Center - center).sqrMagnitude < 0.25f &&
                Mathf.Abs(cached.Radius - safeRadius) < 0.25f)
                return cached.Snapshot;

            WorkzoneEconomySnapshot snapshot = BuildSnapshot(area, center, safeRadius);
            if (Cache.Count > 128) Cache.Clear();
            Cache[cacheKey] = new CacheEntry
            {
                ExpiresAt = now + CacheSeconds,
                Center = center,
                Radius = safeRadius,
                Snapshot = snapshot
            };
            return snapshot;
        }

        internal static ProductionDailyEstimate EstimateResource(string areaId, Vector3 center, float radius, string prefab, int currentStock)
        {
            string resource = WorkerStorageRuntime.NormalizeObjectName(prefab ?? string.Empty);
            if (string.IsNullOrWhiteSpace(resource)) return default;

            WorkzoneEconomySnapshot snapshot = GetSnapshot(areaId, center, radius);
            if (snapshot != null && snapshot.TryGetRow(resource, out WorkzoneEconomyResourceRow row) && row != null)
            {
                double coverage = row.ConsumptionPerDay > 0.0001
                    ? Math.Max(0, currentStock) / row.ConsumptionPerDay
                    : 0.0;
                return new ProductionDailyEstimate(row.HasProductionModel, row.ProducerCount, row.ConsumerCount,
                    row.ProductionPerDay, row.ConsumptionPerDay, coverage);
            }

            // Preserve the old display semantics for a WildGrowth-capable resource even when the
            // zone currently owns zero matching plants and has zero stock/demand.
            bool hasModel = false;
            try { hasModel = WorkersWildFloraRuntime.TryGetExpectedYieldPerValheimDay(resource, out _, out _, out _); }
            catch { }
            return new ProductionDailyEstimate(hasModel, 0, 0, 0.0, 0.0, 0.0);
        }

        internal static void Invalidate(string areaId)
        {
            string area = string.IsNullOrWhiteSpace(areaId) ? "base" : areaId;
            if (!string.Equals(area, "base", StringComparison.Ordinal))
            {
                Cache.Remove(area);
                return;
            }

            // Base/unassigned displays use positional cache keys so several displays in different
            // bases cannot invalidate/rebuild each other every frame. Invalidate all base buckets.
            var remove = new List<string>();
            foreach (string key in Cache.Keys)
                if (key.StartsWith("base@", StringComparison.Ordinal)) remove.Add(key);
            for (int i = 0; i < remove.Count; i++) Cache.Remove(remove[i]);
        }

        internal static void Clear()
        {
            Cache.Clear();
        }


        private static string GetCacheKey(string area, Vector3 center, float radius)
        {
            if (!string.Equals(area, "base", StringComparison.Ordinal)) return area;
            int x = Mathf.RoundToInt(center.x / 25f);
            int z = Mathf.RoundToInt(center.z / 25f);
            int r = Mathf.RoundToInt(radius / 5f);
            return $"base@{x}:{z}:{r}";
        }

        private static WorkzoneEconomySnapshot BuildSnapshot(string area, Vector3 center, float radius)
        {
            var snapshot = new WorkzoneEconomySnapshot
            {
                AreaId = area,
                Center = center,
                Radius = radius
            };

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var consumers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var wildGrowth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var persistentStock = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            try
            {
                List<string> indexed = ProductionMetricsRuntime.GetItems(area, center, radius);
                for (int i = 0; i < indexed.Count; i++)
                {
                    string prefab = WorkerStorageRuntime.NormalizeObjectName(indexed[i] ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(prefab)) candidates.Add(prefab);
                }
            }
            catch { }

            try
            {
                GardenReplantPlanRuntime.CollectCompletedWildGrowth(area, wildGrowth);
                foreach (KeyValuePair<string, int> pair in wildGrowth)
                    if (pair.Value > 0 && !string.IsNullOrWhiteSpace(pair.Key)) candidates.Add(pair.Key);
            }
            catch { }

            // v0.5.364: completed inter-zone deliveries form a rolling 10-Valheim-day external
            // consumption rate. Pull only already-recorded flow prefabs; this never scans containers
            // or workers and therefore keeps opening the Workzone UI demand-driven.
            try { WorkzoneLogisticsFlowRuntime.CollectExportPrefabs(area, candidates); }
            catch { }

            // v0.5.365: real production/consumption events from cooking, baking, harvesting,
            // smelting and future ProductionMetrics users are also exposed as a rolling average.
            // Reading this history is dictionary/ZDO-manifest based and performs no scene scan.
            try { WorkzoneActivityFlowRuntime.CollectActivityPrefabs(area, candidates); }
            catch { }

            try
            {
                BackgroundWorkerScheduler.ForEachRegisteredWorker(workerZdo =>
                {
                    if (!WorkerZdo.IsWorker(workerZdo)) return;
                    string workerArea = WorkerZdo.ReadWorkAreaId(workerZdo);
                    if (string.IsNullOrWhiteSpace(workerArea)) workerArea = "base";
                    if (!string.Equals(workerArea, area, StringComparison.Ordinal)) return;

                    snapshot.WorkerCount++;
                    if (!WorkerSustenanceRuntime.TryGetRequirement(workerZdo, out _, out _, out _)) return;
                    if (!WorkerSustenanceRuntime.TryGetExpectedConsumptionPrefab(workerZdo, out string expectedFood)) return;

                    string resource = WorkerStorageRuntime.NormalizeObjectName(expectedFood ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(resource)) return;
                    snapshot.SustenanceWorkerCount++;
                    candidates.Add(resource);
                    consumers.TryGetValue(resource, out int count);
                    consumers[resource] = count + 1;

                    int target = 0;
                    int homeStock = 0;
                    long nextMealTicks = 0L;
                    try { target = WorkerSustenanceRuntime.GetTargetStock(workerZdo); } catch { }
                    try { homeStock = WorkerSustenanceRuntime.GetStock(workerZdo); } catch { }
                    try { nextMealTicks = workerZdo.GetLong(WorkerSustenanceRuntime.NextMealTicksKey, 0L); } catch { }
                    if (target <= 0) snapshot.SupplyAutoDisabledWorkerCount++;
                    if (homeStock > 0 || nextMealTicks > DateTime.UtcNow.Ticks) snapshot.SupplyReadyWorkerCount++;
                });
            }
            catch { }

            // Persistent ledgers are used as a conservative fallback for unloaded zones. We only
            // iterate already-remembered containers here: opening the economy UI must never trigger
            // a fresh full-world ZDO reflection scan. Loaded storage remains authoritative and the
            // larger of loaded/persistent counts is used below to avoid double-counting the same chest.
            try
            {
                var prefabs = new List<string>(32);
                PersistentContainerRegistryRuntime.ForEachRemembered((containerZdo, role, owner, position) =>
                {
                    if (containerZdo == null || !ContainerLedgerRuntime.HasSnapshot(containerZdo)) return;

                    string explicitArea = FieldDepotWorkzoneRuntime.GetAssignedWorkAreaId(containerZdo);
                    if (!string.IsNullOrWhiteSpace(explicitArea))
                    {
                        if (!string.Equals(explicitArea, area, StringComparison.Ordinal)) return;
                    }
                    else
                    {
                        Vector3 delta = position - center;
                        delta.y = 0f;
                        if (delta.sqrMagnitude > radius * radius) return;
                    }

                    ContainerLedgerRuntime.CollectSnapshotPrefabs(containerZdo, prefabs);
                    for (int i = 0; i < prefabs.Count; i++)
                    {
                        string prefab = WorkerStorageRuntime.NormalizeObjectName(prefabs[i] ?? string.Empty);
                        if (string.IsNullOrWhiteSpace(prefab)) continue;
                        int amount = Math.Max(0, ContainerLedgerRuntime.GetSnapshotAmount(containerZdo, prefab));
                        if (amount <= 0) continue;
                        candidates.Add(prefab);
                        persistentStock.TryGetValue(prefab, out int old);
                        persistentStock[prefab] = old + amount;
                    }

                    // v0.5.363: Workzone economy production can live in the existing background
                    // delivery manifest while the physical chest is unloaded. Count that staged
                    // stock as part of the persistent Workzone inventory so the outpost/economy UI
                    // and transport decisions see the same virtual stock before materialisation.
                    AdvancedFieldDepotRuntime.CollectPendingBackgroundPrefabs(containerZdo, prefabs);
                    for (int i = 0; i < prefabs.Count; i++)
                    {
                        string prefab = WorkerStorageRuntime.NormalizeObjectName(prefabs[i] ?? string.Empty);
                        if (string.IsNullOrWhiteSpace(prefab)) continue;
                        int amount = Math.Max(0, AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(containerZdo, prefab));
                        if (amount <= 0) continue;
                        candidates.Add(prefab);
                        persistentStock.TryGetValue(prefab, out int old);
                        persistentStock[prefab] = old + amount;
                    }
                });
            }
            catch { }

            foreach (string prefab in candidates)
            {
                if (string.IsNullOrWhiteSpace(prefab)) continue;
                int loadedStock = 0;
                try { loadedStock = ProductionMetricsRuntime.GetStock(area, center, radius, prefab); } catch { }
                persistentStock.TryGetValue(prefab, out int savedStock);

                int producerCount = 0;
                wildGrowth.TryGetValue(prefab, out producerCount);
                int consumerCount = 0;
                consumers.TryGetValue(prefab, out consumerCount);

                bool hasProductionModel = false;
                double production = 0.0;
                try
                {
                    if (WorkersWildFloraRuntime.TryGetExpectedYieldPerValheimDay(prefab, out double perPlant, out _, out _))
                    {
                        hasProductionModel = true;
                        production = Math.Max(0.0, producerCount * perPlant);
                    }
                }
                catch { }

                // Deterministic models remain authoritative, but observed real throughput can
                // extend the economy to chains that do not yet have a closed-form forecast. Using
                // max() rather than addition is intentional: Worker meals and WildGrowth harvests
                // are already recorded as real events, so summing would count the same activity twice.
                double observedProduction = 0.0;
                double observedConsumption = 0.0;
                try
                {
                    observedProduction = Math.Max(0.0, WorkzoneActivityFlowRuntime.GetAverageProducedPerDay(area, prefab));
                    observedConsumption = Math.Max(0.0, WorkzoneActivityFlowRuntime.GetAverageConsumedPerDay(area, prefab));
                }
                catch { }

                if (observedProduction > 0.0001)
                {
                    production = Math.Max(production, observedProduction);
                    hasProductionModel = true;
                }

                double localConsumption = Math.Max(Math.Max(0, consumerCount), observedConsumption);
                double deliveryConsumption = 0.0;
                try { deliveryConsumption = Math.Max(0.0, WorkzoneLogisticsFlowRuntime.GetAverageExportPerDay(area, prefab)); }
                catch { }

                int actualProduced = 0;
                int actualConsumed = 0;
                try { ProductionMetricsRuntime.GetMetric(area, prefab, out actualProduced, out actualConsumed); } catch { }

                var row = new WorkzoneEconomyResourceRow
                {
                    Prefab = prefab,
                    Stock = Math.Max(Math.Max(0, loadedStock), Math.Max(0, savedStock)),
                    ProducerCount = Math.Max(0, producerCount),
                    ConsumerCount = Math.Max(0, consumerCount),
                    HasProductionModel = hasProductionModel,
                    ProductionPerDay = Math.Max(0.0, production),
                    LocalConsumptionPerDay = localConsumption,
                    DeliveryConsumptionPerDay = deliveryConsumption,
                    ConsumptionPerDay = localConsumption + deliveryConsumption,
                    ActualProduced = Math.Max(0, actualProduced),
                    ActualConsumed = Math.Max(0, actualConsumed)
                };

                if (row.ConsumptionPerDay > 0.0001 && row.BalancePerDay < -0.0001)
                    snapshot.HasForecastDeficit = true;
                snapshot.Rows.Add(row);
            }

            snapshot.Rows.Sort((a, b) =>
            {
                bool aActive = a != null && a.HasForecastActivity;
                bool bActive = b != null && b.HasForecastActivity;
                if (aActive != bActive) return aActive ? -1 : 1;
                double aMagnitude = a != null ? Math.Abs(a.BalancePerDay) : 0.0;
                double bMagnitude = b != null ? Math.Abs(b.BalancePerDay) : 0.0;
                int magnitude = bMagnitude.CompareTo(aMagnitude);
                if (magnitude != 0) return magnitude;
                string an = a != null ? ProductionMetricsRuntime.DisplayName(a.Prefab) : string.Empty;
                string bn = b != null ? ProductionMetricsRuntime.DisplayName(b.Prefab) : string.Empty;
                return string.Compare(an, bn, StringComparison.CurrentCultureIgnoreCase);
            });

            return snapshot;
        }
    }
}
