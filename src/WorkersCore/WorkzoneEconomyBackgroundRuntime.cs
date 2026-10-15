using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// v0.5.363: Workzone-level unloaded simulation.
    ///
    /// Stationary workers still do not run individual AI while their Workzone is unloaded. Instead
    /// one persistent WorkFlag ledger advances the small set of economy models that are safe to
    /// aggregate today:
    ///   - Workers WildGrowth yield (with one physical respawn cycle reserved to avoid duplicating
    ///     the bush that Valheim may present when the zone becomes physical again),
    ///   - Worker sustenance, settled as one zone pass only when the next meal boundary is due.
    ///
    /// Produced items are staged in the Workzone FieldDepot's existing background-delivery manifest.
    /// That makes the stock visible to AirFreight/Drakes immediately while the physical Container is
    /// unloaded; the normal WorkerBackgroundDeliveryApplier materialises it into the real chest later.
    /// No Physics/world scan is performed by this runtime.
    /// </summary>
    internal static class WorkzoneEconomyBackgroundRuntime
    {
        private sealed class ActiveZone
        {
            internal string AreaId;
            internal ZDO FlagZdo;
        }

        private const int StateVersion = 363;
        private const string VersionKey = "vw_economy_bg_version";
        private const string ActiveKey = "vw_economy_bg_active";
        private const string LastTicksKey = "vw_economy_bg_last_ticks";
        private const string RateManifestKey = "vw_economy_bg_rates";
        private const string AccruedManifestKey = "vw_economy_bg_accrued";
        private const string PhysicalReserveManifestKey = "vw_economy_bg_reserve";
        private const string NextFoodCheckTicksKey = "vw_economy_bg_food_next";
        private const string HarvestReadyKey = "vw_economy_bg_harvest_ready";
        private const string StatusKey = "vw_economy_bg_status";
        private const string LastDeliveredKey = "vw_economy_bg_last_delivered";

        private const float SchedulerIntervalSeconds = 0.50f;
        private const int ZonesPerSlice = 1;
        private const double MaxAccruedUnitsPerPrefab = 1000000.0;
        // A starved zone does not need a 30-second historical polling loop. Retry on the normal
        // Valheim-day meal cadence; real stock changes wake the zone immediately through
        // NotifyWorkerSupplyChanged below. This keeps multi-day catch-up O(days), not O(seconds).
        private static readonly long StarvedRetryTicks = WorkerSustenanceRuntime.MealInterval.Ticks;
        private static readonly Dictionary<string, ActiveZone> Active =
            new Dictionary<string, ActiveZone>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> PendingActivation =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly List<string> TickKeys = new List<string>(32);
        private static readonly List<string> FlushKeyScratch = new List<string>(32);
        private static readonly Dictionary<string, int> WildGrowthSeedScratch =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static int _tickCursor;
        private static float _nextTick;

        internal static int ActiveZoneCount => Active.Count;

        internal static void NoteWorkerBackgroundHandoff(ZDO workerZdo, long nowTicks)
        {
            if (workerZdo == null || !WorkerZdo.IsWorker(workerZdo)) return;
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrWhiteSpace(areaId) || string.Equals(areaId, "base", StringComparison.Ordinal)) return;
            TryActivateArea(areaId, nowTicks);
        }

        internal static void RegisterRemoteWorker(ZDO workerZdo, long nowTicks)
        {
            if (workerZdo == null || !WorkerZdo.IsWorker(workerZdo)) return;
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrWhiteSpace(areaId) || string.Equals(areaId, "base", StringComparison.Ordinal)) return;

            if (WorkFlagRegistry.TryGetPersistentState(areaId, out ZDO flagZdo) && flagZdo != null &&
                flagZdo.GetBool(ActiveKey, false))
            {
                Active[areaId] = new ActiveZone { AreaId = areaId, FlagZdo = flagZdo };
                return;
            }

            // Migration/startup path for a v0.5.362 save that is already remote when this build is
            // first loaded. Debounce activation for two seconds so the persistent Worker bootstrap
            // can register the whole Workzone first; otherwise the first discovered Worker would
            // build an incomplete food/harvest model for a 100-Worker settlement.
            PendingActivation[areaId] = Time.realtimeSinceStartup + 2f;
        }

        internal static void StopForLoadedWorker(ZDO workerZdo, long nowTicks)
        {
            if (workerZdo == null || !WorkerZdo.IsWorker(workerZdo)) return;
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrWhiteSpace(areaId) || string.Equals(areaId, "base", StringComparison.Ordinal)) return;
            if (!TryResolveFlag(areaId, out ZDO flagZdo)) return;
            if (!flagZdo.GetBool(ActiveKey, false))
            {
                Active.Remove(areaId);
                return;
            }

            // Normally the scheduler has already kept this ledger current. On a direct game-load
            // into an outpost there may still be historical catch-up left, so finish it before
            // physical AI is allowed to take authority. One Advance call handles 32 meal
            // boundaries; the outer guard covers more than 80 real-time days of persisted
            // Valheim-day meals while still preventing a corrupt timestamp from producing an
            // unbounded loop.
            bool caughtUp = false;
            int finalizeGuard = 0;
            while (!caughtUp && finalizeGuard++ < 128)
                caughtUp = Advance(flagZdo, areaId, nowTicks, 32);

            if (!caughtUp)
            {
                // Corrupt/absurdly old clocks must never freeze the main thread. Preserve the
                // already-settled state, drop only the impossible remainder and make it visible
                // in diagnostics instead of replaying millions of boundaries.
                flagZdo.Set(StatusKey, "physical: economy catchup guard reached; remainder skipped");
            }

            flagZdo.Set(ActiveKey, false);
            flagZdo.Set(LastTicksKey, nowTicks);
            if (caughtUp)
            {
                int lastDelivered = Math.Max(0, flagZdo.GetInt(LastDeliveredKey, 0));
                flagZdo.Set(StatusKey, lastDelivered > 0
                    ? $"physical: economy ledger stopped • letzter Transfer +{lastDelivered}"
                    : "physical: economy ledger materialised/stopped");
            }
            Active.Remove(areaId);
            PendingActivation.Remove(areaId);
            WorkzoneEconomyRuntime.Invalidate(areaId);
            BackgroundMonitorRuntime.Note($"Economy zone {ShortArea(areaId)} -> physical; virtual ledger stopped.", writeLog: false);
        }

        internal static void Tick()
        {
            float nowRealtime = Time.realtimeSinceStartup;
            if (nowRealtime < _nextTick) return;
            _nextTick = nowRealtime + SchedulerIntervalSeconds;

            if (PendingActivation.Count > 0)
            {
                string dueArea = null;
                foreach (KeyValuePair<string, float> pair in PendingActivation)
                {
                    if (nowRealtime >= pair.Value) { dueArea = pair.Key; break; }
                }
                if (!string.IsNullOrEmpty(dueArea))
                {
                    PendingActivation.Remove(dueArea);
                    TryActivateArea(dueArea, DateTime.UtcNow.Ticks);
                }
            }

            if (Active.Count == 0)
            {
                TickKeys.Clear();
                _tickCursor = 0;
                return;
            }

            if (TickKeys.Count == 0 || _tickCursor >= TickKeys.Count)
            {
                TickKeys.Clear();
                foreach (string areaId in Active.Keys) TickKeys.Add(areaId);
                _tickCursor = 0;
            }

            long nowTicks = DateTime.UtcNow.Ticks;
            int processed = 0;
            while (_tickCursor < TickKeys.Count && processed < ZonesPerSlice)
            {
                string areaId = TickKeys[_tickCursor++];
                processed++;
                if (!Active.TryGetValue(areaId, out ActiveZone zone) || zone?.FlagZdo == null)
                {
                    Active.Remove(areaId);
                    continue;
                }

                ZDO flagZdo = zone.FlagZdo;
                if (!flagZdo.GetBool(ActiveKey, false))
                {
                    Active.Remove(areaId);
                    continue;
                }

                Advance(flagZdo, areaId, nowTicks, 32);
            }
        }

        internal static void NotifyWorkerSupplyChanged(ZDO workerZdo)
        {
            if (workerZdo == null || !WorkerZdo.IsWorker(workerZdo)) return;
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrWhiteSpace(areaId)) return;
            if (!TryResolveFlag(areaId, out ZDO flagZdo) || flagZdo == null || !flagZdo.GetBool(ActiveKey, false)) return;

            long nowTicks = DateTime.UtcNow.Ticks;
            long current = flagZdo.GetLong(NextFoodCheckTicksKey, 0L);
            if (current <= 0L || current > nowTicks)
                flagZdo.Set(NextFoodCheckTicksKey, nowTicks);
        }

        internal static string GetStatus(string areaId)
        {
            if (string.IsNullOrWhiteSpace(areaId)) return "inaktiv";
            if (!TryResolveFlag(areaId, out ZDO flagZdo)) return "keine persistente Fahne";
            string status = flagZdo.GetString(StatusKey, string.Empty);
            if (flagZdo.GetBool(ActiveKey, false))
            {
                string accrued = flagZdo.GetString(AccruedManifestKey, string.Empty);
                string staged = string.IsNullOrEmpty(accrued) ? "kein virtueller Uebertrag offen" : "virtueller Uebertrag wartet";
                return string.IsNullOrWhiteSpace(status) ? $"aktiv ({staged})" : $"aktiv • {status} • {staged}";
            }
            return string.IsNullOrWhiteSpace(status) ? "inaktiv" : status;
        }

        internal static void Clear()
        {
            Active.Clear();
            PendingActivation.Clear();
            TickKeys.Clear();
            FlushKeyScratch.Clear();
            _tickCursor = 0;
            _nextTick = 0f;
        }

        private static bool TryActivateArea(string areaId, long nowTicks)
        {
            if (string.IsNullOrWhiteSpace(areaId) || !TryResolveFlag(areaId, out ZDO flagZdo)) return false;

            if (flagZdo.GetBool(ActiveKey, false))
            {
                Active[areaId] = new ActiveZone { AreaId = areaId, FlagZdo = flagZdo };
                return true;
            }

            if (!BackgroundWorkerScheduler.IsWorkzoneFullyBackground(areaId)) return false;

            // Background economy currently simulates only deterministic WildGrowth + sustenance.
            // Do not build the full UI economy snapshot during world startup. That snapshot also
            // resolves stock/history/display rows and caused a multi-second load hitch on large saves.
            Dictionary<string, double> rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, double> reserve = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            WildGrowthSeedScratch.Clear();
            try
            {
                GardenReplantPlanRuntime.CollectCompletedWildGrowth(areaId, WildGrowthSeedScratch);
                foreach (KeyValuePair<string, int> pair in WildGrowthSeedScratch)
                {
                    if (pair.Value <= 0 || string.IsNullOrWhiteSpace(pair.Key)) continue;
                    string prefab = WorkerStorageRuntime.NormalizeObjectName(pair.Key);
                    if (string.IsNullOrWhiteSpace(prefab)) continue;

                    if (!WorkersWildFloraRuntime.TryGetExpectedYieldPerValheimDay(
                            prefab, out double perPlantPerDay, out int yieldPerCycle, out _))
                        continue;

                    double production = Math.Max(0.0, pair.Value * perPlantPerDay);
                    if (production <= 0.000001) continue;

                    rates[prefab] = production;
                    reserve[prefab] = Math.Max(0.0, pair.Value * Math.Max(1, yieldPerCycle));
                }
            }
            catch
            {
                WildGrowthSeedScratch.Clear();
            }

            // Keep UI snapshots demand-driven; invalidate only, never rebuild from this scheduler path.
            WorkzoneEconomyRuntime.Invalidate(areaId);

            long nextFood;
            int sustenanceWorkers;
            int harvestWorkers;
            int readyHarvestWorkers;
            SettleZoneMeals(areaId, nowTicks, out nextFood, out sustenanceWorkers, out harvestWorkers, out readyHarvestWorkers);

            if (rates.Count == 0 && sustenanceWorkers <= 0)
                return false;

            flagZdo.Set(VersionKey, StateVersion);
            flagZdo.Set(RateManifestKey, WriteDoubleManifest(rates));
            flagZdo.Set(PhysicalReserveManifestKey, WriteDoubleManifest(reserve));
            // Carry sub-item fractions from an earlier session; they are valid Workzone economy
            // debt and should not be thrown away merely because the player visited the zone.
            if (string.IsNullOrWhiteSpace(flagZdo.GetString(AccruedManifestKey, string.Empty)))
                flagZdo.Set(AccruedManifestKey, string.Empty);
            flagZdo.Set(NextFoodCheckTicksKey, nextFood);
            flagZdo.Set(HarvestReadyKey, harvestWorkers > 0 && readyHarvestWorkers > 0);
            flagZdo.Set(LastTicksKey, nowTicks);
            flagZdo.Set(ActiveKey, true);
            flagZdo.Set(StatusKey,
                $"background: rates={rates.Count}, food={sustenanceWorkers}, harvest={readyHarvestWorkers}/{harvestWorkers}");

            Active[areaId] = new ActiveZone { AreaId = areaId, FlagZdo = flagZdo };
            PendingActivation.Remove(areaId);
            BackgroundMonitorRuntime.Note(
                $"Economy zone {ShortArea(areaId)} active: rates={rates.Count}, food={sustenanceWorkers}, harvest={readyHarvestWorkers}/{harvestWorkers}.",
                writeLog: false);
            return true;
        }

        private static bool Advance(ZDO flagZdo, string areaId, long nowTicks, int maxBoundaries)
        {
            if (flagZdo == null || nowTicks <= 0L) return true;

            long lastTicks = flagZdo.GetLong(LastTicksKey, nowTicks);
            if (lastTicks <= 0L || lastTicks > nowTicks) lastTicks = nowTicks;

            Dictionary<string, double> rates = ParseDoubleManifest(flagZdo.GetString(RateManifestKey, string.Empty));
            Dictionary<string, double> accrued = ParseDoubleManifest(flagZdo.GetString(AccruedManifestKey, string.Empty));
            Dictionary<string, double> reserve = ParseDoubleManifest(flagZdo.GetString(PhysicalReserveManifestKey, string.Empty));

            long cursor = lastTicks;
            int boundaryGuard = 0;
            int boundaryLimit = Math.Max(1, maxBoundaries);
            while (cursor < nowTicks && boundaryGuard++ < boundaryLimit)
            {
                long nextFood = flagZdo.GetLong(NextFoodCheckTicksKey, 0L);
                if (nextFood > 0L && nextFood <= cursor)
                {
                    int sustenanceWorkers;
                    int harvestWorkers;
                    int readyHarvestWorkers;
                    SettleZoneMeals(areaId, cursor, out long newNextFood, out sustenanceWorkers, out harvestWorkers, out readyHarvestWorkers);
                    flagZdo.Set(NextFoodCheckTicksKey, newNextFood);
                    flagZdo.Set(HarvestReadyKey, harvestWorkers > 0 && readyHarvestWorkers > 0);
                    continue;
                }

                long end = nowTicks;
                if (nextFood > cursor && nextFood < end) end = nextFood;

                if (end > cursor && flagZdo.GetBool(HarvestReadyKey, false))
                    AccrueProduction(rates, reserve, accrued, TimeSpan.FromTicks(end - cursor).TotalSeconds);
                cursor = end;

                if (nextFood > 0L && cursor >= nextFood && cursor <= nowTicks)
                {
                    int sustenanceWorkers;
                    int harvestWorkers;
                    int readyHarvestWorkers;
                    SettleZoneMeals(areaId, cursor, out long newNextFood, out sustenanceWorkers, out harvestWorkers, out readyHarvestWorkers);
                    flagZdo.Set(NextFoodCheckTicksKey, newNextFood);
                    flagZdo.Set(HarvestReadyKey, harvestWorkers > 0 && readyHarvestWorkers > 0);
                }
            }

            // Very long offline intervals are intentionally amortised over scheduler slices.
            // Never collapse hundreds of meal boundaries into one frame: if the guard is reached,
            // persist the cursor and continue from exactly there on the next Workzone slice.
            bool catchupPending = cursor < nowTicks;

            int delivered = FlushWholeUnits(flagZdo, areaId, accrued);
            flagZdo.Set(AccruedManifestKey, WriteDoubleManifest(accrued));
            flagZdo.Set(PhysicalReserveManifestKey, WriteDoubleManifest(reserve));
            flagZdo.Set(LastTicksKey, cursor);
            if (delivered > 0) flagZdo.Set(LastDeliveredKey, delivered);

            string harvest = flagZdo.GetBool(HarvestReadyKey, false) ? "Ernte aktiv" : "Ernte wartet";
            flagZdo.Set(StatusKey, $"background: {harvest}, staged+={delivered}" + (catchupPending ? " • catchup..." : string.Empty));
            return !catchupPending;
        }

        private static void AccrueProduction(Dictionary<string, double> rates,
            Dictionary<string, double> reserve,
            Dictionary<string, double> accrued,
            double deltaSeconds)
        {
            if (rates == null || rates.Count == 0 || deltaSeconds <= 0.0) return;
            double daySeconds = Math.Max(1.0, WorkerSustenanceRuntime.MealInterval.TotalSeconds);

            foreach (KeyValuePair<string, double> pair in rates)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value <= 0.0) continue;
                double generated = pair.Value * (deltaSeconds / daySeconds);
                if (generated <= 0.0) continue;

                reserve.TryGetValue(pair.Key, out double held);
                if (held > 0.0)
                {
                    double absorbed = Math.Min(held, generated);
                    held -= absorbed;
                    generated -= absorbed;
                    if (held <= 0.000001) reserve.Remove(pair.Key);
                    else reserve[pair.Key] = held;
                }

                if (generated <= 0.0) continue;
                accrued.TryGetValue(pair.Key, out double old);
                accrued[pair.Key] = Math.Min(MaxAccruedUnitsPerPrefab, Math.Max(0.0, old) + generated);
            }
        }

        private static int FlushWholeUnits(ZDO flagZdo, string areaId, Dictionary<string, double> accrued)
        {
            if (flagZdo == null || accrued == null || accrued.Count == 0) return 0;
            if (!TryFindDestinationDepot(flagZdo, areaId, out ZDO depotZdo) || depotZdo == null) return 0;

            int delivered = 0;
            FlushKeyScratch.Clear();
            foreach (string key in accrued.Keys)
                FlushKeyScratch.Add(key);

            for (int i = 0; i < FlushKeyScratch.Count; i++)
            {
                string prefab = FlushKeyScratch[i];
                if (!accrued.TryGetValue(prefab, out double value) || value < 1.0) continue;
                int whole = (int)Math.Min(int.MaxValue, Math.Floor(value));
                if (whole <= 0) continue;

                AdvancedFieldDepotRuntime.QueueBackgroundDelivery(depotZdo, prefab, whole);
                ProductionMetricsRuntime.RecordProduced(areaId, prefab, whole);
                delivered += whole;

                double left = value - whole;
                if (left <= 0.000001) accrued.Remove(prefab);
                else accrued[prefab] = left;
            }

            if (delivered > 0)
            {
                PersistentContainerRegistryRuntime.Remember(depotZdo);
                WorkzoneEconomyRuntime.Invalidate(areaId);
                BackgroundMonitorRuntime.Note($"Economy zone {ShortArea(areaId)} staged +{delivered} item(s) in FieldDepot.", writeLog: false);
            }
            return delivered;
        }

        private static bool TryFindDestinationDepot(ZDO flagZdo, string areaId, out ZDO depotZdo)
        {
            depotZdo = null;
            if (flagZdo == null || string.IsNullOrWhiteSpace(areaId)) return false;

            long ownerId = WorkFlagZdo.ReadOwner(flagZdo);
            Vector3 center = flagZdo.GetPosition();
            float radius = WorkFlagZdo.ReadRadius(flagZdo, 30f);
            float maxFallbackSqr = (radius + 4f) * (radius + 4f);
            int bestPriority = int.MaxValue;
            float bestSqr = float.MaxValue;
            ZDO best = null;

            PersistentContainerRegistryRuntime.ForEachRemembered((candidate, role, owner, position) =>
            {
                if (candidate == null || role != WorkerContainerRole.FieldDepot) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;

                string assigned = FieldDepotWorkzoneRuntime.GetAssignedWorkAreaId(candidate);
                int priority;
                if (string.Equals(assigned, areaId, StringComparison.Ordinal))
                {
                    priority = 0;
                }
                else
                {
                    if (!string.IsNullOrEmpty(assigned)) return;
                    float fx = position.x - center.x;
                    float fz = position.z - center.z;
                    if (fx * fx + fz * fz > maxFallbackSqr) return;
                    priority = 1;
                }

                float dx = position.x - center.x;
                float dz = position.z - center.z;
                float sqr = dx * dx + dz * dz;
                if (priority > bestPriority || (priority == bestPriority && sqr >= bestSqr)) return;
                bestPriority = priority;
                bestSqr = sqr;
                best = candidate;
            });

            depotZdo = best;
            return best != null;
        }

        private static void SettleZoneMeals(string areaId, long throughTicks,
            out long nextFoodCheckTicks,
            out int sustenanceWorkers,
            out int harvestWorkers,
            out int readyHarvestWorkers)
        {
            long nextFoodLocal = 0L;
            int sustenanceLocal = 0;
            int harvestLocal = 0;
            int readyHarvestLocal = 0;
            bool waitingForSupply = false;

            BackgroundWorkerScheduler.ForEachRegisteredWorker(workerZdo =>
            {
                if (workerZdo == null || !WorkerZdo.IsWorker(workerZdo)) return;
                string workerArea = WorkerZdo.ReadWorkAreaId(workerZdo);
                if (!string.Equals(workerArea, areaId, StringComparison.Ordinal)) return;

                WorkerRole role = WorkerJobProfileRuntime.NormalizeRuntimeRole(workerZdo);
                if (role == WorkerRole.Hauler) return; // live logistics keeps its own exact meal gate

                bool harvestCapable = false;
                try
                {
                    harvestCapable = WorkerJobProfileRuntime.CanHarvest(workerZdo) &&
                                     !GatherHarvestCategoryFilterRuntime.HasCustomHarvestFilter(workerZdo);
                }
                catch { }

                if (harvestCapable) harvestLocal++;

                bool hasManagedMeal = WorkerRestRuntime.HasAssignedHome(workerZdo) &&
                                      WorkerSustenanceRuntime.TryGetRequirement(workerZdo, out _, out _, out _);
                bool ready = true;
                if (hasManagedMeal)
                {
                    sustenanceLocal++;
                    int consumed = WorkerSustenanceRuntime.SettleBackgroundMeals(workerZdo, throughTicks, 64, out bool waiting);
                    if (consumed > 0) WorkzoneEconomyRuntime.Invalidate(areaId);

                    long next = workerZdo.GetLong(WorkerSustenanceRuntime.NextMealTicksKey, 0L);
                    if (waiting && next > 0L && next <= throughTicks)
                    {
                        waitingForSupply = true;
                        long retry = throughTicks + StarvedRetryTicks;
                        if (nextFoodLocal <= 0L || retry < nextFoodLocal) nextFoodLocal = retry;
                    }
                    else if (next > throughTicks && (nextFoodLocal <= 0L || next < nextFoodLocal))
                    {
                        nextFoodLocal = next;
                    }

                    ready = !WorkerSustenanceRuntime.IsHungry(workerZdo, throughTicks);
                }

                if (harvestCapable && ready) readyHarvestLocal++;
            });

            if (waitingForSupply && nextFoodLocal <= throughTicks)
                nextFoodLocal = throughTicks + StarvedRetryTicks;

            nextFoodCheckTicks = nextFoodLocal;
            sustenanceWorkers = sustenanceLocal;
            harvestWorkers = harvestLocal;
            readyHarvestWorkers = readyHarvestLocal;
        }

        private static bool TryResolveFlag(string areaId, out ZDO flagZdo)
        {
            flagZdo = null;
            if (string.IsNullOrWhiteSpace(areaId)) return false;
            return WorkFlagRegistry.TryGetPersistentState(areaId, out flagZdo) && flagZdo != null;
        }

        private static Dictionary<string, double> ParseDoubleManifest(string raw)
        {
            Dictionary<string, double> result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw)) return result;

            string[] entries = raw.Split(';');
            for (int i = 0; i < entries.Length; i++)
            {
                int eq = entries[i].IndexOf('=');
                if (eq <= 0) continue;
                string key = WorkerStorageRuntime.NormalizeObjectName(entries[i].Substring(0, eq));
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (!double.TryParse(entries[i].Substring(eq + 1), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double value) || value <= 0.0000001) continue;
                result[key] = Math.Min(MaxAccruedUnitsPerPrefab, value);
            }
            return result;
        }

        private static string WriteDoubleManifest(Dictionary<string, double> manifest)
        {
            if (manifest == null || manifest.Count == 0) return string.Empty;
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, double> pair in manifest)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value <= 0.0000001) continue;
                if (b.Length > 0) b.Append(';');
                b.Append(pair.Key).Append('=').Append(pair.Value.ToString("0.########", CultureInfo.InvariantCulture));
            }
            return b.ToString();
        }

        private static string ShortArea(string areaId)
        {
            if (string.IsNullOrWhiteSpace(areaId)) return "-";
            string name = WorkFlagRegistry.TryGetDisplayName(areaId, string.Empty);
            if (!string.IsNullOrWhiteSpace(name)) return name;
            return areaId.Length <= 8 ? areaId : areaId.Substring(0, 8);
        }
    }
}
