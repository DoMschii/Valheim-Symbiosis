using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Valheim_Workers
{
    internal enum WorkersPerfBucket
    {
        MainUpdate = 0,
        WorkerAI = 1,
        Background = 2,
        Ownership = 3,
        MapMarkers = 4,
        SpawnerProbe = 5,
        Planning = 6,
        GardenGhosts = 7,
        LivestockScan = 8,
        OnGUI = 9,
        LateAir = 10,
        PersistentBootstrap = 11,
        PersistentAirStaging = 12,
        BackgroundWorkerSlice = 13,
        IndependentAir = 14,
        Provisioning = 15,
        RuntimePieces = 16,
        ProductionDisplay = 17,
        Count = 18
    }

    /// <summary>
    /// Allocation-free timing counters for the major Workers subsystems. Timings are collected
    /// continuously but rolled into a small 5-second snapshot, so diagnostics do not become the
    /// performance problem they are meant to find.
    /// </summary>
    internal static class WorkersPerformanceProfiler
    {
        private const float WindowSeconds = 5f;
        private static readonly string[] Names =
        {
            "Workers Update",
            "Worker AI",
            "Background",
            "Ownership",
            "Map markers",
            "Spawner probe",
            "Planning/UI runtime",
            "Garden ghosts",
            "Livestock scan",
            "Workers OnGUI",
            "Drake LateAir",
            "Persistent bootstrap",
            "Air staging",
            "Background worker slice",
            "Independent Drake steering",
            "Persistent provisioning",
            "Piece/runtime ticks",
            "Production display"
        };

        private static readonly long[] WindowTicks = new long[(int)WorkersPerfBucket.Count];
        private static readonly long[] WindowMaxTicks = new long[(int)WorkersPerfBucket.Count];
        private static readonly int[] WindowCalls = new int[(int)WorkersPerfBucket.Count];
        private static readonly float[] LastMsPerSecond = new float[(int)WorkersPerfBucket.Count];
        private static readonly float[] LastMaxMs = new float[(int)WorkersPerfBucket.Count];
        private static readonly float[] LastCallsPerSecond = new float[(int)WorkersPerfBucket.Count];
        private static float _windowStartedAt = -1f;
        private static string _compactSummary = "Perf: sammelt 5 s Daten ...";

        internal static long Begin()
        {
            return Stopwatch.GetTimestamp();
        }

        internal static double ElapsedMilliseconds(long startedAt)
        {
            long elapsed = Stopwatch.GetTimestamp() - startedAt;
            if (elapsed <= 0L) return 0.0;
            return elapsed * 1000.0 / Stopwatch.Frequency;
        }

        internal static void End(WorkersPerfBucket bucket, long startedAt)
        {
            long elapsed = Stopwatch.GetTimestamp() - startedAt;
            if (elapsed < 0L) return;
            int index = (int)bucket;
            if (index < 0 || index >= WindowTicks.Length) return;
            WindowTicks[index] += elapsed;
            WindowCalls[index]++;
            if (elapsed > WindowMaxTicks[index]) WindowMaxTicks[index] = elapsed;
        }

        internal static void TickSnapshot()
        {
            float now = Time.realtimeSinceStartup;
            if (_windowStartedAt < 0f)
            {
                _windowStartedAt = now;
                return;
            }

            float elapsedSeconds = now - _windowStartedAt;
            if (elapsedSeconds < WindowSeconds) return;
            if (elapsedSeconds <= 0.01f) elapsedSeconds = WindowSeconds;

            double tickToMs = 1000.0 / Stopwatch.Frequency;
            int topA = -1;
            int topB = -1;
            int topC = -1;
            for (int i = 0; i < WindowTicks.Length; i++)
            {
                LastMsPerSecond[i] = (float)(WindowTicks[i] * tickToMs / elapsedSeconds);
                LastMaxMs[i] = (float)(WindowMaxTicks[i] * tickToMs);
                LastCallsPerSecond[i] = WindowCalls[i] / elapsedSeconds;

                if (topA < 0 || LastMsPerSecond[i] > LastMsPerSecond[topA])
                {
                    topC = topB;
                    topB = topA;
                    topA = i;
                }
                else if (topB < 0 || LastMsPerSecond[i] > LastMsPerSecond[topB])
                {
                    topC = topB;
                    topB = i;
                }
                else if (topC < 0 || LastMsPerSecond[i] > LastMsPerSecond[topC])
                {
                    topC = i;
                }

                WindowTicks[i] = 0L;
                WindowMaxTicks[i] = 0L;
                WindowCalls[i] = 0;
            }

            _windowStartedAt = now;
            _compactSummary = BuildCompact(topA, topB, topC);

            // v0.5.273: only log real Workers-originated hitches.
            int spikeIndex = -1;
            float spikeMs = 0f;
            for (int i = 0; i < LastMaxMs.Length; i++)
            {
                if (LastMaxMs[i] > spikeMs)
                {
                    spikeMs = LastMaxMs[i];
                    spikeIndex = i;
                }
            }
            if (spikeIndex >= 0 && spikeMs >= 20f)
            {
                BackgroundMonitorRuntime.Note(
                    $"PERF HITCH: {Names[spikeIndex]} spike={spikeMs:0.0}ms; " +
                    $"top={FormatCompactMetric(topA)} | {FormatCompactMetric(topB)} | {FormatCompactMetric(topC)}");
            }
        }

        private static string BuildCompact(int a, int b, int c)
        {
            string first = FormatCompactMetric(a);
            string second = FormatCompactMetric(b);
            string third = FormatCompactMetric(c);
            return $"Perf: {first} | {second} | {third}";
        }

        private static string FormatCompactMetric(int index)
        {
            if (index < 0 || index >= Names.Length) return "n/a";
            return $"{Names[index]} {LastMsPerSecond[index]:0.0}ms/s (spike {LastMaxMs[index]:0.00}ms)";
        }

        internal static string CompactSummary => _compactSummary;

        internal static void CopySummaryLines(List<string> destination, int maxLines = 8)
        {
            if (destination == null) return;
            destination.Clear();
            maxLines = Mathf.Clamp(maxLines, 1, Names.Length);

            // Sort only the tiny fixed bucket index set when the monitor asks for display data.
            List<int> order = new List<int>(Names.Length);
            for (int i = 0; i < Names.Length; i++) order.Add(i);
            order.Sort((a, b) => LastMsPerSecond[b].CompareTo(LastMsPerSecond[a]));

            int shown = 0;
            for (int i = 0; i < order.Count && shown < maxLines; i++)
            {
                int index = order[i];
                if (LastCallsPerSecond[index] <= 0f && LastMsPerSecond[index] <= 0f) continue;
                destination.Add($"{Names[index]}: {LastMsPerSecond[index]:0.00} ms/s | spike {LastMaxMs[index]:0.00} ms | {LastCallsPerSecond[index]:0.0} calls/s");
                shown++;
            }

            if (destination.Count == 0)
                destination.Add("Noch keine abgeschlossene 5-s-Messung.");
        }

        internal static void Reset()
        {
            Array.Clear(WindowTicks, 0, WindowTicks.Length);
            Array.Clear(WindowMaxTicks, 0, WindowMaxTicks.Length);
            Array.Clear(WindowCalls, 0, WindowCalls.Length);
            Array.Clear(LastMsPerSecond, 0, LastMsPerSecond.Length);
            Array.Clear(LastMaxMs, 0, LastMaxMs.Length);
            Array.Clear(LastCallsPerSecond, 0, LastCallsPerSecond.Length);
            _windowStartedAt = -1f;
            _compactSummary = "Perf: sammelt 5 s Daten ...";
        }
    }
}
