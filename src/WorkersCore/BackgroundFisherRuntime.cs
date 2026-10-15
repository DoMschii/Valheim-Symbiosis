using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// v0.5.380: cheap unloaded Fisher simulation.
    ///
    /// The physical Neck remains authoritative while its Workzone is loaded and still catches real
    /// Valheim Fish ItemDrops. When the zone is handed to the background scheduler we snapshot only
    /// fish species that were actually observed locally. The unloaded adapter may then convert elapsed
    /// work time into catches of those remembered species and stage them directly in the persistent
    /// FieldDepot ledger. No unloaded Fish/Physics/world scan and no Character AI are used.
    /// </summary>
    internal static class BackgroundFisherRuntime
    {
        private const string SpeciesKey = "vw_bg_fish_species_v1";
        private const string SpeciesAreaKey = "vw_bg_fish_species_area_v1";
        private const string ProgressKey = "vw_bg_fish_progress";
        private const string CursorKey = "vw_bg_fish_cursor";
        private const int MaxRememberedSpecies = 12;
        private const int MaxParsedSpeciesCacheEntries = 128;

        private static readonly HashSet<string> EmptySpecies =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, HashSet<string>> ParsedSpeciesCache =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private static readonly List<string> AllowedSpeciesScratch =
            new List<string>(MaxRememberedSpecies);
        private static readonly List<string> SpeciesWriteScratch =
            new List<string>(MaxRememberedSpecies);

        // One Valheim worker day is WorkerSustenanceRuntime.MealInterval (currently 30 real minutes).
        // Ten base catches/day become about eight catches/day at the normal 80% offline efficiency.
        private const float FishPerValheimDay = 10f;

        internal static void CaptureLoadedSnapshot(Character worker, ZDO workerZdo)
        {
            if (worker == null || workerZdo == null) return;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out WorkOrder order, out _, out float radius)) return;
            if (order != WorkOrder.Fish) return;

            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            string storedArea = workerZdo.GetString(SpeciesAreaKey, string.Empty);
            bool changedArea = !string.IsNullOrEmpty(storedArea) &&
                               !string.Equals(storedArea, areaId ?? string.Empty, StringComparison.Ordinal);
            HashSet<string> species = changedArea
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : ReadSpeciesCopy(workerZdo, areaId);
            if (changedArea) workerZdo.Set(SpeciesKey, string.Empty);
            workerZdo.Set(SpeciesAreaKey, areaId ?? string.Empty);
            float scanRadius = Mathf.Clamp(radius, 8f, 40f);
            WorkerWorldIndex.ForEachItemDrop(worker.transform.position, scanRadius, drop =>
            {
                if (!FisherWorkRuntime.IsLiveFish(drop)) return;
                string prefab = GathererTargetFinder.GetCargoPrefabName(drop);
                if (string.IsNullOrWhiteSpace(prefab)) return;
                species.Add(WorkerStorageRuntime.NormalizeObjectName(prefab));
            });

            WriteSpecies(workerZdo, species);
        }

        internal static void RememberObservedFish(ZDO workerZdo, string prefab)
        {
            if (workerZdo == null || string.IsNullOrWhiteSpace(prefab)) return;
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            string storedArea = workerZdo.GetString(SpeciesAreaKey, string.Empty);
            bool changedArea = !string.IsNullOrEmpty(storedArea) &&
                               !string.Equals(storedArea, areaId ?? string.Empty, StringComparison.Ordinal);
            HashSet<string> species = changedArea
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : ReadSpeciesCopy(workerZdo, areaId);
            if (changedArea) workerZdo.Set(SpeciesKey, string.Empty);
            workerZdo.Set(SpeciesAreaKey, areaId ?? string.Empty);
            species.Add(WorkerStorageRuntime.NormalizeObjectName(prefab));
            WriteSpecies(workerZdo, species);
        }

        internal static bool HasBackgroundWork(ZDO workerZdo)
        {
            if (workerZdo == null) return false;
            HashSet<string> species = ReadSpeciesReadOnly(workerZdo, WorkerZdo.ReadWorkAreaId(workerZdo));
            if (species.Count == 0) return false;
            if (!WorkerLogisticsRuntime.TryFindPersistentFieldDepotForWorkArea(workerZdo, out ZDO depotZdo, out _) || depotZdo == null)
                return false;

            foreach (string prefab in species)
            {
                if (!ProductionDemandRuntime.AllowsPrefab(workerZdo, prefab)) continue;
                if (CanStage(depotZdo, prefab)) return true;
            }
            return false;
        }

        internal static string TickBackground(ZDO workerZdo, double deltaSeconds, float efficiency)
        {
            if (workerZdo == null) return "fishing:no-worker";
            HashSet<string> remembered = ReadSpeciesReadOnly(workerZdo, WorkerZdo.ReadWorkAreaId(workerZdo));
            if (remembered.Count == 0)
            {
                workerZdo.Set(ProgressKey, 0f);
                return "fishing:waiting-no-observed-fish";
            }

            if (!WorkerLogisticsRuntime.TryFindPersistentFieldDepotForWorkArea(workerZdo, out ZDO depotZdo, out _) || depotZdo == null)
                return "fishing:waiting-no-field-depot";

            AllowedSpeciesScratch.Clear();
            foreach (string prefab in remembered)
            {
                if (string.IsNullOrWhiteSpace(prefab)) continue;
                if (!ProductionDemandRuntime.AllowsPrefab(workerZdo, prefab)) continue;
                AllowedSpeciesScratch.Add(prefab);
            }
            AllowedSpeciesScratch.Sort(StringComparer.OrdinalIgnoreCase);
            if (AllowedSpeciesScratch.Count == 0)
            {
                workerZdo.Set(ProgressKey, 0f);
                return "fishing:waiting-stock-max";
            }

            double daySeconds = Math.Max(1.0, WorkerSustenanceRuntime.MealInterval.TotalSeconds);
            double secondsPerCatch = daySeconds / Math.Max(1.0, FishPerValheimDay);
            float progress = Mathf.Max(0f, workerZdo.GetFloat(ProgressKey, 0f));
            progress += (float)(Math.Max(0.0, deltaSeconds) * Mathf.Clamp(efficiency, 0.05f, 1f));
            int completed = (int)Math.Floor(progress / secondsPerCatch);
            if (completed <= 0)
            {
                workerZdo.Set(ProgressKey, progress);
                return $"fishing:tracking {Mathf.Clamp01((float)(progress / secondsPerCatch)) * 100f:0}%";
            }

            int cursor = Math.Abs(workerZdo.GetInt(CursorKey, 0));
            int delivered = 0;
            int attempts = 0;
            int maxAttempts = Math.Max(completed * Math.Max(1, AllowedSpeciesScratch.Count), AllowedSpeciesScratch.Count);
            while (delivered < completed && attempts < maxAttempts)
            {
                string prefab = AllowedSpeciesScratch[cursor % AllowedSpeciesScratch.Count];
                cursor++;
                attempts++;
                if (!CanStage(depotZdo, prefab)) continue;

                AdvancedFieldDepotRuntime.QueueBackgroundDelivery(depotZdo, prefab, 1);
                ProductionMetricsRuntime.RecordProduced(workerZdo, prefab, 1);
                delivered++;
            }

            if (delivered > 0)
            {
                progress -= (float)(delivered * secondsPerCatch);
                workerZdo.Set(CursorKey, cursor);
                WorkzoneEconomyRuntime.Invalidate(WorkerZdo.ReadWorkAreaId(workerZdo));
            }

            // Never bank more than roughly two future catches while the depot is blocked/full.
            workerZdo.Set(ProgressKey, Mathf.Min(progress, (float)(secondsPerCatch * 2.0)));
            return delivered > 0
                ? $"fishing:simulated {delivered} catch(es) -> field depot"
                : "fishing:waiting-field-depot-full";
        }

        private static bool CanStage(ZDO depotZdo, string prefab)
        {
            if (depotZdo == null || string.IsNullOrWhiteSpace(prefab)) return false;
            if (!ContainerLedgerRuntime.TryGetPersistentFreeCapacity(depotZdo, prefab, out int capacity)) return false;
            int pending = AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(depotZdo, prefab);
            return Math.Max(0, capacity - pending) >= 1;
        }

        private static HashSet<string> ReadSpeciesReadOnly(ZDO workerZdo, string currentAreaId)
        {
            if (workerZdo == null) return EmptySpecies;

            string storedArea = workerZdo.GetString(SpeciesAreaKey, string.Empty);
            string activeArea = currentAreaId ?? string.Empty;
            if (!string.IsNullOrEmpty(storedArea) &&
                !string.Equals(storedArea, activeArea, StringComparison.Ordinal))
                return EmptySpecies;

            string raw = workerZdo.GetString(SpeciesKey, string.Empty);
            if (string.IsNullOrWhiteSpace(raw)) return EmptySpecies;

            if (ParsedSpeciesCache.TryGetValue(raw, out HashSet<string> cached))
                return cached;

            HashSet<string> parsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] parts = raw.Split(new[] { '\u001f' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length && parsed.Count < MaxRememberedSpecies; i++)
            {
                string prefab = WorkerStorageRuntime.NormalizeObjectName(parts[i]);
                if (!string.IsNullOrWhiteSpace(prefab)) parsed.Add(prefab);
            }

            if (ParsedSpeciesCache.Count >= MaxParsedSpeciesCacheEntries)
                ParsedSpeciesCache.Clear();

            ParsedSpeciesCache[raw] = parsed;
            return parsed;
        }

        private static HashSet<string> ReadSpeciesCopy(ZDO workerZdo, string currentAreaId)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> cached = ReadSpeciesReadOnly(workerZdo, currentAreaId);
            foreach (string prefab in cached)
                result.Add(prefab);
            return result;
        }

        internal static void ClearCaches()
        {
            ParsedSpeciesCache.Clear();
            AllowedSpeciesScratch.Clear();
            SpeciesWriteScratch.Clear();
        }

        private static void WriteSpecies(ZDO workerZdo, HashSet<string> species)
        {
            if (workerZdo == null || species == null || species.Count == 0) return;

            SpeciesWriteScratch.Clear();
            foreach (string item in species)
            {
                string prefab = WorkerStorageRuntime.NormalizeObjectName(item);
                if (string.IsNullOrWhiteSpace(prefab) || SpeciesWriteScratch.Contains(prefab)) continue;
                SpeciesWriteScratch.Add(prefab);
                if (SpeciesWriteScratch.Count >= MaxRememberedSpecies) break;
            }

            SpeciesWriteScratch.Sort(StringComparer.OrdinalIgnoreCase);
            workerZdo.Set(SpeciesKey, string.Join("\u001f", SpeciesWriteScratch));
        }
    }
}
