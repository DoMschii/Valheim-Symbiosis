using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// v0.5.379: conservative offscreen Smith metal production.
    /// Real loaded smelter recipes + station production orders are snapshotted at Workzone handoff.
    /// While unloaded, persistent container ledgers provide ore/fuel and a persistent FieldDepot
    /// receives finished bars. No physics/world scan runs in background mode.
    /// </summary>
    internal static class BackgroundSmithMetalRuntime
    {
        private const string InitKey = "vw_bg_smith_metal_init";
        private const string CountKey = "vw_bg_smith_metal_count";
        private const string ProgressKey = "vw_bg_smith_metal_progress";
        private const string DebugKey = "vw_bg_smith_metal_debug";
        private const int MaxRecipes = 24;
        private const int MaxBatchesPerTick = 32;

        private static readonly Collider[] ScanBuffer = new Collider[768];
        private static readonly HashSet<Smelter> Seen = new HashSet<Smelter>();
        private static readonly FieldInfo SecondsPerProductField = typeof(Smelter).GetField("m_secPerProduct", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo FuelPerProductField = typeof(Smelter).GetField("m_fuelPerProduct", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly List<RemovalReservation> Reservations = new List<RemovalReservation>(2);
        private static readonly Dictionary<ZDOID, DemandCache> DemandCaches = new Dictionary<ZDOID, DemandCache>();
        private static readonly long DemandCacheTicks = TimeSpan.FromSeconds(2).Ticks;

        private sealed class RemovalReservation
        {
            internal ZDO Source;
            internal string Tx;
            internal int Amount;
        }

        private sealed class DemandCache
        {
            internal long ExpiresAt;
            internal bool HasDemand;
            internal ZDO Flag;
            internal RecipeSnapshot Recipe;
            internal ZDO Station;
            internal ZDO Destination;
            internal int Needed;
        }

        private readonly struct RecipeSnapshot
        {
            internal readonly ZDOID StationId;
            internal readonly string Input;
            internal readonly string Output;
            internal readonly string Fuel;
            internal readonly float Seconds;
            internal readonly float FuelPerProduct;
            internal readonly int QueuedCredit;

            internal RecipeSnapshot(ZDOID stationId, string input, string output, string fuel, float seconds, float fuelPerProduct, int queuedCredit)
            {
                StationId = stationId;
                Input = input ?? string.Empty;
                Output = output ?? string.Empty;
                Fuel = fuel ?? string.Empty;
                Seconds = Mathf.Max(1f, seconds);
                FuelPerProduct = Mathf.Max(0f, fuelPerProduct);
                QueuedCredit = Mathf.Max(0, queuedCredit);
            }
        }

        internal static void CaptureLoadedSnapshot(Character worker, ZDO workerZdo)
        {
            if (worker == null || workerZdo == null) return;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius)) return;
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId)) return;
            if (!WorkFlagRegistry.TryGetNetworkState(areaId, Mathf.Max(8f, radius), out _, out ZDO flagZdo) || flagZdo == null) return;

            Seen.Clear();
            var recipes = new List<RecipeSnapshot>(12);
            int hits = Physics.OverlapSphereNonAlloc(center, Mathf.Clamp(radius, 6f, 180f), ScanBuffer);
            for (int i = 0; i < hits && recipes.Count < MaxRecipes; i++)
            {
                Collider c = ScanBuffer[i];
                if (c == null) continue;
                Smelter smelter = c.GetComponentInParent<Smelter>();
                if (smelter == null || !Seen.Add(smelter) || !FirekeeperWorkRuntime.IsSmithMetalMachine(smelter)) continue;

                ZNetView view = smelter.GetComponent<ZNetView>() ?? smelter.GetComponentInParent<ZNetView>() ?? smelter.GetComponentInChildren<ZNetView>();
                ZDO stationZdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (stationZdo == null) continue;

                float seconds = ReadFloat(SecondsPerProductField, smelter, 30f);
                float fuelPerProduct = ReadFloat(FuelPerProductField, smelter, 0f);
                FirekeeperWorkRuntime.TryGetFuelPrefab(smelter, out string fuel);
                string[] outputs = FirekeeperWorkRuntime.GetMetalOutputs(smelter);
                for (int o = 0; o < outputs.Length && recipes.Count < MaxRecipes; o++)
                {
                    string output = outputs[o];
                    if (string.IsNullOrEmpty(output) || !ProductionOrderRuntime.HasOrderFor(stationZdo, output)) continue;
                    if (!FirekeeperWorkRuntime.TryGetMetalInputForOutput(smelter, output, out string input) || string.IsNullOrEmpty(input)) continue;
                    int queuedCredit = FirekeeperWorkRuntime.GetQueuedMetalOutputAmount(smelter, output);
                    recipes.Add(new RecipeSnapshot(stationZdo.m_uid, input, output, fuel, seconds, fuelPerProduct, queuedCredit));
                }
            }

            flagZdo.Set(CountKey, recipes.Count);
            flagZdo.Set(InitKey, true);
            for (int i = 0; i < recipes.Count; i++)
            {
                RecipeSnapshot r = recipes[i];
                flagZdo.Set(Key("station", i), r.StationId);
                flagZdo.Set(Key("input", i), r.Input);
                flagZdo.Set(Key("output", i), r.Output);
                flagZdo.Set(Key("fuel", i), r.Fuel);
                flagZdo.Set(Key("seconds", i), r.Seconds);
                flagZdo.Set(Key("fuelpp", i), r.FuelPerProduct);
                flagZdo.Set(Key("queued", i), r.QueuedCredit);
            }
            flagZdo.Set(DebugKey, recipes.Count > 0 ? $"smith-metal:snapshot {recipes.Count}" : "smith-metal:no-controlled-orders");
            WorkFlagRegistry.RememberPersistent(flagZdo);
        }

        internal static bool HasDemand(ZDO workerZdo)
        {
            return TryGetDemandCache(workerZdo, out DemandCache cache) && cache != null && cache.HasDemand;
        }

        /// <summary>Consumes at most assignedSeconds and returns the actually used time.</summary>
        internal static double ConsumeBudget(ZDO workerZdo, double assignedSeconds, out string status)
        {
            status = "metal:no-demand";
            if (workerZdo == null || assignedSeconds <= 0.0001) return 0.0;
            if (BackgroundMachineProductionRuntime.HasActiveMachineFirekeeper(workerZdo))
            {
                status = "metal:handled-by-firekeeper";
                return 0.0;
            }
            if (!TryGetDemandCache(workerZdo, out DemandCache cache) || cache == null || !cache.HasDemand || cache.Station == null || cache.Destination == null)
                return 0.0;
            ZDO flagZdo = cache.Flag;
            RecipeSnapshot recipe = cache.Recipe;
            ZDO stationZdo = cache.Station;
            ZDO destination = cache.Destination;

            float startProgress = Mathf.Clamp(workerZdo.GetFloat(ProgressKey, 0f), 0f, recipe.Seconds * 2f);
            // Bound one call so an hour-long catch-up cannot create an unbounded loop. Any unused
            // assigned time is returned to the shared budget and can be offered again next pass.
            double maxProcessable = Math.Max(0.0, MaxBatchesPerTick * recipe.Seconds + (recipe.Seconds - 0.001f) - startProgress);
            double offered = Math.Min(assignedSeconds, maxProcessable);
            float projectedProgress = startProgress + (float)offered;
            int ready = Mathf.Min(Mathf.Max(0, cache.Needed), Mathf.Min(MaxBatchesPerTick, Mathf.FloorToInt(projectedProgress / recipe.Seconds)));
            if (ready <= 0)
            {
                workerZdo.Set(ProgressKey, projectedProgress);
                status = $"metal:{ProductionOrderRuntime.DisplayProduct(recipe.Output)} {Mathf.Clamp01(projectedProgress / recipe.Seconds) * 100f:0}%";
                return offered;
            }

            int completed = 0;
            for (int i = 0; i < ready; i++)
            {
                if (!CanStage(destination, recipe.Output, 1)) break;
                if (!ProductionOrderClaimRuntime.TryClaim(stationZdo, workerZdo, recipe.Output, 1, out _)) break;

                string batchId = $"smithbg:{workerZdo.m_uid}:{stationZdo.m_uid}:{recipe.Output}:{DateTime.UtcNow.Ticks}:{i}";
                Reservations.Clear();
                bool ok = TryReserve(workerZdo, recipe.Input, 1, batchId + ":ore", Reservations);
                int fuelAmount = string.IsNullOrEmpty(recipe.Fuel) ? 0 : Mathf.Max(0, Mathf.CeilToInt(recipe.FuelPerProduct));
                if (ok && fuelAmount > 0) ok = TryReserve(workerZdo, recipe.Fuel, fuelAmount, batchId + ":fuel", Reservations);
                if (!ok)
                {
                    Rollback(Reservations);
                    ProductionOrderClaimRuntime.ReleaseClaimsForWorker(workerZdo);
                    InvalidateDemandCaches(stationZdo.m_uid, recipe.Output);
                    break;
                }

                bool committed = true;
                for (int r = 0; r < Reservations.Count; r++)
                {
                    RemovalReservation rr = Reservations[r];
                    if (!ContainerLedgerRuntime.TryCommitRemoval(rr.Source, rr.Tx, out ContainerLedgerRuntime.TransactionInfo tx, out _) || tx == null || tx.Amount < rr.Amount)
                    {
                        committed = false;
                        break;
                    }
                }
                if (!committed)
                {
                    Rollback(Reservations);
                    ProductionOrderClaimRuntime.ReleaseClaimsForWorker(workerZdo);
                    InvalidateDemandCaches(stationZdo.m_uid, recipe.Output);
                    break;
                }

                AdvancedFieldDepotRuntime.QueueBackgroundDelivery(destination, recipe.Output, 1);
                for (int r = 0; r < Reservations.Count; r++) ContainerLedgerRuntime.CompleteTransaction(Reservations[r].Source, Reservations[r].Tx);
                ProductionOrderRuntime.RegisterProduced(stationZdo, recipe.Output, 1);
                ProductionMetricsRuntime.RecordConsumed(workerZdo, recipe.Input, 1);
                if (fuelAmount > 0) ProductionMetricsRuntime.RecordConsumed(workerZdo, recipe.Fuel, fuelAmount);
                ProductionMetricsRuntime.RecordProduced(workerZdo, recipe.Output, 1);
                ProductionOrderClaimRuntime.ReleaseClaimsForWorker(workerZdo);
                completed++;
            }

            if (completed > 0) InvalidateDemandCaches(stationZdo.m_uid, recipe.Output);

            double used;
            float finalProgress;
            if (completed >= ready)
            {
                used = offered;
                finalProgress = projectedProgress - completed * recipe.Seconds;
            }
            else if (completed > 0)
            {
                // Work after the last successfully committed bar is returned to the shared budget.
                used = Math.Min(offered, Math.Max(0.0, completed * recipe.Seconds - startProgress));
                finalProgress = Mathf.Max(0f, startProgress + (float)used - completed * recipe.Seconds);
            }
            else
            {
                used = 0.0;
                finalProgress = startProgress;
            }
            Reservations.Clear();
            workerZdo.Set(ProgressKey, Mathf.Clamp(finalProgress, 0f, recipe.Seconds * 2f));
            status = completed > 0
                ? $"metal:simulated {completed}x {ProductionOrderRuntime.DisplayProduct(recipe.Output)}"
                : $"metal:waiting-input-output {ProductionOrderRuntime.DisplayProduct(recipe.Output)}";
            flagZdo.Set(DebugKey, status);
            return used;
        }

        private static bool TryGetDemandCache(ZDO workerZdo, out DemandCache cache)
        {
            cache = null;
            if (workerZdo == null) return false;
            long now = DateTime.UtcNow.Ticks;
            if (DemandCaches.TryGetValue(workerZdo.m_uid, out cache) && cache != null && cache.ExpiresAt > now)
                return cache.HasDemand;

            cache = cache ?? new DemandCache();
            bool found = TryResolveDemand(workerZdo, out ZDO flag, out RecipeSnapshot recipe, out ZDO station, out ZDO destination, out int needed);
            cache.ExpiresAt = now + DemandCacheTicks;
            cache.HasDemand = found;
            cache.Flag = flag;
            cache.Recipe = recipe;
            cache.Station = station;
            cache.Destination = destination;
            cache.Needed = Math.Max(0, needed);
            DemandCaches[workerZdo.m_uid] = cache;
            return found;
        }

        private static void InvalidateDemandCaches(ZDOID stationId, string output)
        {
            if (DemandCaches.Count == 0) return;
            foreach (DemandCache cache in DemandCaches.Values)
            {
                if (cache == null || cache.Station == null) continue;
                if (!cache.Station.m_uid.Equals(stationId)) continue;
                if (!string.Equals(cache.Recipe.Output, output, StringComparison.OrdinalIgnoreCase)) continue;
                cache.ExpiresAt = 0L;
            }
        }

        private static bool TryResolveDemand(ZDO workerZdo, out ZDO flagZdo, out RecipeSnapshot selected, out ZDO stationZdo, out ZDO destination, out int needed)
        {
            flagZdo = null; selected = default; stationZdo = null; destination = null; needed = 0;
            if (workerZdo == null || BackgroundMachineProductionRuntime.HasActiveMachineFirekeeper(workerZdo)) return false;
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId) || !WorkFlagRegistry.TryGetPersistentState(areaId, out flagZdo) || flagZdo == null) return false;
            if (!flagZdo.GetBool(InitKey, false)) return false;
            if (!WorkerLogisticsRuntime.TryFindPersistentFieldDepotForWorkArea(workerZdo, out destination, out _) || destination == null) return false;

            int count = Mathf.Clamp(flagZdo.GetInt(CountKey, 0), 0, MaxRecipes);
            for (int i = 0; i < count; i++)
            {
                RecipeSnapshot recipe = Read(flagZdo, i);
                if (recipe.StationId.Equals(ZDOID.None) || string.IsNullOrEmpty(recipe.Input) || string.IsNullOrEmpty(recipe.Output)) continue;
                ZDO station = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(recipe.StationId) : null;
                if (station == null) continue;
                if (!TryGetDemandForRecipe(workerZdo, recipe, station, out ProductionDemand demand) || demand.Needed <= 0) continue;
                if (CountAvailable(workerZdo, recipe.Input) < 1) continue;
                int fuelAmount = string.IsNullOrEmpty(recipe.Fuel) ? 0 : Mathf.Max(0, Mathf.CeilToInt(recipe.FuelPerProduct));
                if (fuelAmount > 0 && CountAvailable(workerZdo, recipe.Fuel) < fuelAmount) continue;
                if (!CanStage(destination, recipe.Output, 1)) continue;
                selected = recipe; stationZdo = station; needed = Math.Max(0, demand.Needed); return true;
            }
            return false;
        }

        private static bool TryGetDemandForRecipe(ZDO workerZdo, RecipeSnapshot recipe, ZDO stationZdo, out ProductionDemand demand)
        {
            demand = default;
            Func<string, int> stock = output =>
            {
                int physical = CountStock(workerZdo, output);
                if (!string.Equals(output, recipe.Output, StringComparison.OrdinalIgnoreCase)) return physical;
                long withQueued = (long)physical + recipe.QueuedCredit;
                return withQueued >= int.MaxValue ? int.MaxValue : (int)withQueued;
            };
            Func<string, bool> canRun = output => string.Equals(output, recipe.Output, StringComparison.OrdinalIgnoreCase);
            return ProductionOrderRuntime.TryGetNextDemandForWorker(stationZdo, stock, canRun, workerZdo, out demand) && demand.Needed > 0;
        }

        private static RecipeSnapshot Read(ZDO flag, int i)
        {
            return new RecipeSnapshot(
                flag.GetZDOID(Key("station", i)),
                flag.GetString(Key("input", i), string.Empty),
                flag.GetString(Key("output", i), string.Empty),
                flag.GetString(Key("fuel", i), string.Empty),
                flag.GetFloat(Key("seconds", i), 30f),
                flag.GetFloat(Key("fuelpp", i), 0f),
                flag.GetInt(Key("queued", i), 0));
        }

        private static string Key(string field, int i) => "vw_bg_smith_metal_" + field + "_" + i;

        private static float ReadFloat(FieldInfo field, object instance, float fallback)
        {
            if (field == null || instance == null) return fallback;
            try { return Convert.ToSingle(field.GetValue(instance)); } catch { return fallback; }
        }

        private static bool TryReserve(ZDO workerZdo, string prefab, int amount, string txPrefix, List<RemovalReservation> reservations)
        {
            if (amount <= 0) return true;
            int remaining = amount;
            int index = 0;
            ForEachEligible(workerZdo, zdo =>
            {
                if (remaining <= 0 || zdo == null) return;
                int keep = WorkerContainerRoleRuntime.ProtectLastItem(zdo) ? 1 : 0;
                if (!ContainerLedgerRuntime.TryGetAvailable(zdo, prefab, keep, out int available) || available <= 0) return;
                int request = Math.Min(remaining, available);
                string tx = txPrefix + ":" + index++;
                if (!ContainerLedgerRuntime.TryReserveRemoval(zdo, tx, prefab, request, keep, "background-smith", out int reserved, out _) || reserved <= 0) return;
                reservations.Add(new RemovalReservation { Source = zdo, Tx = tx, Amount = reserved });
                remaining -= reserved;
            });
            return remaining <= 0;
        }

        private static void Rollback(List<RemovalReservation> reservations)
        {
            for (int i = 0; i < reservations.Count; i++)
                if (reservations[i]?.Source != null && !string.IsNullOrEmpty(reservations[i].Tx))
                    ContainerLedgerRuntime.RollbackTransaction(reservations[i].Source, reservations[i].Tx);
        }

        private static int CountAvailable(ZDO workerZdo, string prefab)
        {
            int total = 0;
            ForEachEligible(workerZdo, zdo =>
            {
                int keep = WorkerContainerRoleRuntime.ProtectLastItem(zdo) ? 1 : 0;
                if (ContainerLedgerRuntime.TryGetAvailable(zdo, prefab, keep, out int available) && available > 0) total += available;
            });
            return total;
        }

        private static int CountStock(ZDO workerZdo, string prefab)
        {
            int total = 0;
            ForEachEligible(workerZdo, zdo => total += Mathf.Max(0, ContainerLedgerRuntime.GetSnapshotAmount(zdo, prefab)));
            if (WorkerLogisticsRuntime.TryFindPersistentFieldDepotForWorkArea(workerZdo, out ZDO depot, out _) && depot != null)
                total += AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(depot, prefab);
            return total;
        }

        private static void ForEachEligible(ZDO workerZdo, Action<ZDO> visitor)
        {
            if (workerZdo == null || visitor == null) return;
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Vector3 home = WorkerZdo.ReadHome(workerZdo, workerZdo.GetPosition());
            float homeRadius = WorkerStorageRuntime.GetSearchRadius(workerZdo);
            bool hasArea = WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out _);
            float workRadius = WorkerStorageRuntime.GetWorkAreaStorageSearchRadius(workerZdo);
            PersistentContainerRegistryRuntime.ForEachPersistent((zdo, role, owner, pos) =>
            {
                if (zdo == null || role == WorkerContainerRole.Import || role == WorkerContainerRole.Export) return;
                if (owner != 0L && ownerId != 0L && owner != ownerId) return;
                bool inHome = HorizontalSqr(pos, home) <= homeRadius * homeRadius;
                bool inArea = hasArea && HorizontalSqr(pos, center) <= workRadius * workRadius;
                if (!inHome && !inArea) return;
                visitor(zdo);
            });
        }

        private static bool CanStage(ZDO destination, string prefab, int amount)
        {
            if (destination == null || amount <= 0) return false;
            if (!ContainerLedgerRuntime.TryGetPersistentFreeCapacity(destination, prefab, out int capacity)) return true;
            int pending = AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(destination, prefab);
            return Math.Max(0, capacity - pending) >= amount;
        }

        private static float HorizontalSqr(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x, z = a.z - b.z;
            return x * x + z * z;
        }
    }
}
