using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// v0.5.364: event-driven rolling logistics throughput for the Workzone economy.
    ///
    /// Physical/background Hauler and Drake routes tag the Workzone that the cargo really came from.
    /// Only successfully delivered amounts are booked as exports. The economy display then treats the
    /// last ten Valheim days of completed deliveries as an average daily external consumption rate.
    /// This smooths bursty routes (for example one large pickup after a 10-day berry cycle) without
    /// introducing any polling/world scan.
    /// </summary>
    internal static class WorkzoneLogisticsFlowRuntime
    {
        internal const string CargoSourceAreaKey = "vw_econ_flow_source_area";
        private const string FlowManifestKey = "vw_econ_export_flow_v1";
        private const int WindowDays = 10;
        private static readonly long DayTicks = WorkerSustenanceRuntime.MealInterval.Ticks;

        private sealed class FlowState
        {
            internal bool Loaded;
            internal readonly Dictionary<string, Dictionary<long, int>> ByPrefab =
                new Dictionary<string, Dictionary<long, int>>(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly Dictionary<string, FlowState> States =
            new Dictionary<string, FlowState>(StringComparer.Ordinal);
        private static readonly List<string> TrimPrefabScratch = new List<string>(16);
        private static readonly List<long> TrimBucketScratch = new List<long>(16);

        internal static int AveragingWindowDays => WindowDays;

        internal static void TagCargoSource(ZDO workerZdo, Container source, string fallbackAreaId = null)
        {
            if (workerZdo == null) return;
            ZDO sourceZdo = source != null ? WorkerContainerRoleRuntime.GetZdo(source) : null;
            Vector3 position = source != null ? source.transform.position : workerZdo.GetPosition();
            TagCargoSource(workerZdo, sourceZdo, position, fallbackAreaId);
        }

        internal static void TagCargoSource(ZDO workerZdo, ZDO sourceZdo, Vector3 sourcePosition, string fallbackAreaId = null)
        {
            if (workerZdo == null) return;
            string area = ResolveSourceArea(sourceZdo, sourcePosition, fallbackAreaId);
            workerZdo.Set(CargoSourceAreaKey, area ?? string.Empty);
        }

        internal static void TagCargoSourceArea(ZDO workerZdo, string areaId)
        {
            if (workerZdo == null) return;
            workerZdo.Set(CargoSourceAreaKey, NormalizeArea(areaId));
        }

        internal static void ClearCargoSource(ZDO workerZdo)
        {
            if (workerZdo == null) return;
            workerZdo.Set(CargoSourceAreaKey, string.Empty);
        }

        internal static void RecordTaggedExport(ZDO workerZdo, string prefab, int amount, string fallbackAreaId = null)
        {
            if (workerZdo == null || amount <= 0 || string.IsNullOrWhiteSpace(prefab)) return;
            string area = workerZdo.GetString(CargoSourceAreaKey, string.Empty);
            if (string.IsNullOrWhiteSpace(area)) area = NormalizeArea(fallbackAreaId);
            RecordExport(area, prefab, amount);
        }

        internal static void RecordExport(string areaId, string prefab, int amount)
        {
            if (amount <= 0 || string.IsNullOrWhiteSpace(prefab)) return;
            string area = NormalizeArea(areaId);
            string item = WorkerStorageRuntime.NormalizeObjectName(prefab ?? string.Empty);
            if (string.IsNullOrWhiteSpace(item)) return;

            FlowState state = GetState(area);
            long today = CurrentDayBucket();
            Trim(state, today);
            if (!state.ByPrefab.TryGetValue(item, out Dictionary<long, int> buckets))
                state.ByPrefab[item] = buckets = new Dictionary<long, int>();
            buckets.TryGetValue(today, out int old);
            long combined = (long)old + amount;
            buckets[today] = combined >= int.MaxValue ? int.MaxValue : (int)combined;

            Persist(area, state, today);
            WorkzoneEconomyRuntime.Invalidate(area);
        }

        internal static double GetAverageExportPerDay(string areaId, string prefab)
        {
            if (string.IsNullOrWhiteSpace(prefab)) return 0.0;
            string area = NormalizeArea(areaId);
            string item = WorkerStorageRuntime.NormalizeObjectName(prefab ?? string.Empty);
            if (string.IsNullOrWhiteSpace(item)) return 0.0;

            FlowState state = GetState(area);
            long today = CurrentDayBucket();
            if (!state.ByPrefab.TryGetValue(item, out Dictionary<long, int> buckets) || buckets == null)
                return 0.0;

            long oldest = today - (WindowDays - 1);
            long total = 0L;
            foreach (KeyValuePair<long, int> pair in buckets)
            {
                if (pair.Key < oldest || pair.Key > today || pair.Value <= 0) continue;
                total += pair.Value;
                if (total >= int.MaxValue) { total = int.MaxValue; break; }
            }
            return Math.Max(0.0, total / (double)WindowDays);
        }

        internal static void CollectExportPrefabs(string areaId, ISet<string> output)
        {
            if (output == null) return;
            string area = NormalizeArea(areaId);
            FlowState state = GetState(area);
            long today = CurrentDayBucket();
            long oldest = today - (WindowDays - 1);
            foreach (KeyValuePair<string, Dictionary<long, int>> item in state.ByPrefab)
            {
                if (item.Value == null) continue;
                bool active = false;
                foreach (KeyValuePair<long, int> pair in item.Value)
                {
                    if (pair.Key >= oldest && pair.Key <= today && pair.Value > 0) { active = true; break; }
                }
                if (active && !string.IsNullOrWhiteSpace(item.Key)) output.Add(item.Key);
            }
        }

        internal static void Clear()
        {
            States.Clear();
            TrimPrefabScratch.Clear();
            TrimBucketScratch.Clear();
        }

        private static FlowState GetState(string area)
        {
            if (!States.TryGetValue(area, out FlowState state) || state == null)
                States[area] = state = new FlowState();
            if (state.Loaded) return state;

            // Do not permanently mark a real Workzone as loaded before its persistent flag mirror
            // exists. During world startup the economy UI/cache can wake slightly earlier than the
            // WorkFlag registry; keeping Loaded=false lets the durable 10-day history hydrate on
            // the next demand-driven query instead of silently starting from zero.
            if (TryResolveFlag(area, out ZDO flagZdo) && flagZdo != null)
            {
                ParseManifest(flagZdo.GetString(FlowManifestKey, string.Empty), state);
                state.Loaded = true;
            }
            else if (string.Equals(area, "base", StringComparison.Ordinal))
            {
                // Literal base is only a last-resort attribution when no WorkFlag can be resolved.
                // It has no durable WorkFlag ZDO to hydrate from.
                state.Loaded = true;
            }
            return state;
        }

        private static void Persist(string area, FlowState state, long today)
        {
            if (state == null) return;
            Trim(state, today);
            if (!TryResolveFlag(area, out ZDO flagZdo) || flagZdo == null) return;
            flagZdo.Set(FlowManifestKey, WriteManifest(state, today));
            state.Loaded = true;
        }

        private static void Trim(FlowState state, long today)
        {
            if (state == null) return;
            long oldest = today - (WindowDays - 1);

            TrimPrefabScratch.Clear();
            foreach (KeyValuePair<string, Dictionary<long, int>> item in state.ByPrefab)
            {
                Dictionary<long, int> buckets = item.Value;
                if (buckets == null)
                {
                    TrimPrefabScratch.Add(item.Key);
                    continue;
                }

                TrimBucketScratch.Clear();
                foreach (KeyValuePair<long, int> pair in buckets)
                    if (pair.Key < oldest || pair.Key > today || pair.Value <= 0)
                        TrimBucketScratch.Add(pair.Key);

                for (int i = 0; i < TrimBucketScratch.Count; i++)
                    buckets.Remove(TrimBucketScratch[i]);

                if (buckets.Count == 0)
                    TrimPrefabScratch.Add(item.Key);
            }

            for (int i = 0; i < TrimPrefabScratch.Count; i++)
                state.ByPrefab.Remove(TrimPrefabScratch[i]);

            TrimBucketScratch.Clear();
        }

        private static void ParseManifest(string raw, FlowState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(raw)) return;
            string[] entries = raw.Split(';');
            long today = CurrentDayBucket();
            long oldest = today - (WindowDays - 1);
            for (int i = 0; i < entries.Length; i++)
            {
                string[] fields = entries[i].Split('|');
                if (fields.Length != 3) continue;
                if (!long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long day)) continue;
                if (day < oldest || day > today) continue;
                string prefab = WorkerStorageRuntime.NormalizeObjectName(fields[1] ?? string.Empty);
                if (string.IsNullOrWhiteSpace(prefab)) continue;
                if (!int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount) || amount <= 0) continue;
                if (!state.ByPrefab.TryGetValue(prefab, out Dictionary<long, int> buckets))
                    state.ByPrefab[prefab] = buckets = new Dictionary<long, int>();
                buckets.TryGetValue(day, out int old);
                long combined = (long)old + amount;
                buckets[day] = combined >= int.MaxValue ? int.MaxValue : (int)combined;
            }
        }

        private static string WriteManifest(FlowState state, long today)
        {
            if (state == null || state.ByPrefab.Count == 0) return string.Empty;
            long oldest = today - (WindowDays - 1);
            var b = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, Dictionary<long, int>> item in state.ByPrefab)
            {
                if (item.Value == null || string.IsNullOrWhiteSpace(item.Key)) continue;
                foreach (KeyValuePair<long, int> pair in item.Value)
                {
                    if (pair.Key < oldest || pair.Key > today || pair.Value <= 0) continue;
                    if (b.Length > 0) b.Append(';');
                    b.Append(pair.Key.ToString(CultureInfo.InvariantCulture)).Append('|')
                        .Append(item.Key).Append('|')
                        .Append(pair.Value.ToString(CultureInfo.InvariantCulture));
                }
            }
            return b.ToString();
        }

        private static string ResolveSourceArea(ZDO sourceZdo, Vector3 sourcePosition, string fallbackAreaId)
        {
            try
            {
                if (sourceZdo != null)
                {
                    string explicitArea = FieldDepotWorkzoneRuntime.GetAssignedWorkAreaId(sourceZdo);
                    if (!string.IsNullOrWhiteSpace(explicitArea)) return explicitArea;
                    sourcePosition = sourceZdo.GetPosition();
                }
            }
            catch { }

            try
            {
                if (WorkFlagRegistry.TryFindPersistentContainingArea(sourcePosition, 30f, out string containing) &&
                    !string.IsNullOrWhiteSpace(containing))
                    return containing;
            }
            catch { }

            return NormalizeArea(fallbackAreaId);
        }

        private static string NormalizeArea(string areaId)
        {
            return string.IsNullOrWhiteSpace(areaId) ? "base" : areaId;
        }

        private static long CurrentDayBucket()
        {
            long ticks = Math.Max(TimeSpan.FromMinutes(30).Ticks, DayTicks);
            return DateTime.UtcNow.Ticks / ticks;
        }

        private static bool TryResolveFlag(string areaId, out ZDO flagZdo)
        {
            flagZdo = null;
            if (string.IsNullOrWhiteSpace(areaId) || string.Equals(areaId, "base", StringComparison.Ordinal)) return false;
            return WorkFlagRegistry.TryGetPersistentState(areaId, out flagZdo) && flagZdo != null;
        }
    }
}
