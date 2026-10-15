using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// v0.5.365: event-driven rolling production/consumption throughput for the central Workzone economy.
    ///
    /// Every already-existing ProductionMetricsRuntime event contributes to a ten-Valheim-day rolling
    /// history. No worker/container/world scan is added. Writes to the WorkFlag ZDO are throttled and
    /// piggy-back only on real production/consumption events, so fast recipes cannot turn the economy
    /// layer into a new hot loop.
    ///
    /// The central economy uses these observed averages only as a conservative fallback/upper bound:
    /// explicit deterministic models (currently WildGrowth and Worker sustenance) remain authoritative
    /// when they predict a larger rate. This avoids double-counting the same berry harvest or meal while
    /// still making cooking/baking/smelting and other instrumented chains visible in the Workzone balance.
    /// </summary>
    internal static class WorkzoneActivityFlowRuntime
    {
        private const string FlowManifestKey = "vw_econ_activity_flow_v1";
        private const int WindowDays = 10;
        private const float PersistThrottleSeconds = 12f;
        private static readonly long DayTicks = WorkerSustenanceRuntime.MealInterval.Ticks;

        private sealed class Bucket
        {
            internal int Produced;
            internal int Consumed;
        }

        private sealed class FlowState
        {
            internal bool Loaded;
            internal bool Dirty;
            internal float NextPersistAt;
            internal long LastTrimDay = long.MinValue;
            internal ZDO FlagZdo;
            internal readonly Dictionary<string, Dictionary<long, Bucket>> ByPrefab =
                new Dictionary<string, Dictionary<long, Bucket>>(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly Dictionary<string, FlowState> States =
            new Dictionary<string, FlowState>(StringComparer.Ordinal);
        private static readonly List<string> TrimPrefabScratch = new List<string>(16);
        private static readonly List<long> TrimBucketScratch = new List<long>(16);

        internal static int AveragingWindowDays => WindowDays;

        internal static void Record(string areaId, string prefab, int amount, bool produced)
        {
            if (amount <= 0 || string.IsNullOrWhiteSpace(prefab)) return;
            string area = NormalizeArea(areaId);
            string item = WorkerStorageRuntime.NormalizeObjectName(prefab ?? string.Empty);
            if (string.IsNullOrWhiteSpace(item)) return;

            FlowState state = GetState(area);
            long today = CurrentDayBucket();
            Trim(state, today);
            if (!state.ByPrefab.TryGetValue(item, out Dictionary<long, Bucket> buckets))
                state.ByPrefab[item] = buckets = new Dictionary<long, Bucket>();
            if (!buckets.TryGetValue(today, out Bucket bucket) || bucket == null)
                buckets[today] = bucket = new Bucket();

            if (produced)
            {
                long total = (long)bucket.Produced + amount;
                bucket.Produced = total >= int.MaxValue ? int.MaxValue : (int)total;
            }
            else
            {
                long total = (long)bucket.Consumed + amount;
                bucket.Consumed = total >= int.MaxValue ? int.MaxValue : (int)total;
            }

            state.Dirty = true;
            float now = Time.realtimeSinceStartup;
            if (now >= state.NextPersistAt)
            {
                Persist(area, state, today);
                state.NextPersistAt = now + PersistThrottleSeconds;
            }
        }

        internal static double GetAverageProducedPerDay(string areaId, string prefab)
        {
            return GetAverage(areaId, prefab, true);
        }

        internal static double GetAverageConsumedPerDay(string areaId, string prefab)
        {
            return GetAverage(areaId, prefab, false);
        }

        internal static void CollectActivityPrefabs(string areaId, ISet<string> output)
        {
            if (output == null) return;
            string area = NormalizeArea(areaId);
            FlowState state = GetState(area);
            long today = CurrentDayBucket();
            long oldest = today - (WindowDays - 1);
            foreach (KeyValuePair<string, Dictionary<long, Bucket>> item in state.ByPrefab)
            {
                if (item.Value == null || string.IsNullOrWhiteSpace(item.Key)) continue;
                bool active = false;
                foreach (KeyValuePair<long, Bucket> pair in item.Value)
                {
                    Bucket bucket = pair.Value;
                    if (pair.Key < oldest || pair.Key > today || bucket == null) continue;
                    if (bucket.Produced > 0 || bucket.Consumed > 0) { active = true; break; }
                }
                if (active) output.Add(item.Key);
            }
        }

        internal static void Clear()
        {
            long today = CurrentDayBucket();
            foreach (KeyValuePair<string, FlowState> pair in States)
            {
                FlowState state = pair.Value;
                if (state == null || !state.Dirty) continue;
                try { Persist(pair.Key, state, today); }
                catch { }
            }
            States.Clear();
            TrimPrefabScratch.Clear();
            TrimBucketScratch.Clear();
        }

        private static double GetAverage(string areaId, string prefab, bool produced)
        {
            if (string.IsNullOrWhiteSpace(prefab)) return 0.0;
            string area = NormalizeArea(areaId);
            string item = WorkerStorageRuntime.NormalizeObjectName(prefab ?? string.Empty);
            if (string.IsNullOrWhiteSpace(item)) return 0.0;

            FlowState state = GetState(area);
            long today = CurrentDayBucket();
            if (!state.ByPrefab.TryGetValue(item, out Dictionary<long, Bucket> buckets) || buckets == null)
                return 0.0;

            long oldest = today - (WindowDays - 1);
            long total = 0L;
            foreach (KeyValuePair<long, Bucket> pair in buckets)
            {
                Bucket bucket = pair.Value;
                if (pair.Key < oldest || pair.Key > today || bucket == null) continue;
                int value = produced ? bucket.Produced : bucket.Consumed;
                if (value <= 0) continue;
                total += value;
                if (total >= int.MaxValue) { total = int.MaxValue; break; }
            }
            return Math.Max(0.0, total / (double)WindowDays);
        }

        private static FlowState GetState(string area)
        {
            if (!States.TryGetValue(area, out FlowState state) || state == null)
                States[area] = state = new FlowState();
            if (state.Loaded) return state;

            if (TryResolveFlag(area, out ZDO flagZdo) && flagZdo != null)
            {
                state.FlagZdo = flagZdo;
                ParseManifest(flagZdo.GetString(FlowManifestKey, string.Empty), state);
                state.Loaded = true;
            }
            else if (string.Equals(area, "base", StringComparison.Ordinal))
            {
                // Literal base has no durable WorkFlag mirror; keep a session-local rolling view.
                state.Loaded = true;
            }
            return state;
        }

        private static void Persist(string area, FlowState state, long today)
        {
            if (state == null || !state.Dirty) return;
            Trim(state, today);

            ZDO flagZdo = state.FlagZdo;
            if (flagZdo == null && TryResolveFlag(area, out ZDO resolved) && resolved != null)
            {
                flagZdo = resolved;
                state.FlagZdo = resolved;
            }
            if (flagZdo == null) return;

            flagZdo.Set(FlowManifestKey, WriteManifest(state, today));
            state.Loaded = true;
            state.Dirty = false;
        }

        private static void Trim(FlowState state, long today)
        {
            if (state == null || state.LastTrimDay == today) return;
            state.LastTrimDay = today;
            long oldest = today - (WindowDays - 1);

            TrimPrefabScratch.Clear();
            foreach (KeyValuePair<string, Dictionary<long, Bucket>> item in state.ByPrefab)
            {
                Dictionary<long, Bucket> buckets = item.Value;
                if (buckets == null)
                {
                    TrimPrefabScratch.Add(item.Key);
                    continue;
                }

                TrimBucketScratch.Clear();
                foreach (KeyValuePair<long, Bucket> pair in buckets)
                {
                    Bucket bucket = pair.Value;
                    if (pair.Key < oldest || pair.Key > today || bucket == null ||
                        (bucket.Produced <= 0 && bucket.Consumed <= 0))
                        TrimBucketScratch.Add(pair.Key);
                }

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
                if (fields.Length != 4) continue;
                if (!long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long day)) continue;
                if (day < oldest || day > today) continue;
                string prefab = WorkerStorageRuntime.NormalizeObjectName(fields[1] ?? string.Empty);
                if (string.IsNullOrWhiteSpace(prefab)) continue;
                if (!int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int made) || made < 0) continue;
                if (!int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int used) || used < 0) continue;
                if (made <= 0 && used <= 0) continue;

                if (!state.ByPrefab.TryGetValue(prefab, out Dictionary<long, Bucket> buckets))
                    state.ByPrefab[prefab] = buckets = new Dictionary<long, Bucket>();
                if (!buckets.TryGetValue(day, out Bucket bucket) || bucket == null)
                    buckets[day] = bucket = new Bucket();

                long combinedMade = (long)bucket.Produced + made;
                long combinedUsed = (long)bucket.Consumed + used;
                bucket.Produced = combinedMade >= int.MaxValue ? int.MaxValue : (int)combinedMade;
                bucket.Consumed = combinedUsed >= int.MaxValue ? int.MaxValue : (int)combinedUsed;
            }
        }

        private static string WriteManifest(FlowState state, long today)
        {
            if (state == null || state.ByPrefab.Count == 0) return string.Empty;
            long oldest = today - (WindowDays - 1);
            var b = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, Dictionary<long, Bucket>> item in state.ByPrefab)
            {
                if (item.Value == null || string.IsNullOrWhiteSpace(item.Key)) continue;
                foreach (KeyValuePair<long, Bucket> pair in item.Value)
                {
                    Bucket bucket = pair.Value;
                    if (pair.Key < oldest || pair.Key > today || bucket == null) continue;
                    int made = Math.Max(0, bucket.Produced);
                    int used = Math.Max(0, bucket.Consumed);
                    if (made <= 0 && used <= 0) continue;
                    if (b.Length > 0) b.Append(';');
                    b.Append(pair.Key.ToString(CultureInfo.InvariantCulture)).Append('|')
                        .Append(item.Key).Append('|')
                        .Append(made.ToString(CultureInfo.InvariantCulture)).Append('|')
                        .Append(used.ToString(CultureInfo.InvariantCulture));
                }
            }
            return b.ToString();
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
