using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Persistent conservative background adapter for Surtling machine tending.
    /// While loaded, the workzone snapshots real Smelter recipes and real storage budgets.
    /// While unloaded, only snapshotted real input budgets can be scheduled as production.
    /// The Surtling is the heat source, so wood/coal fuel is never budgeted or consumed. On reload
    /// only real machine input is removed before output is committed to a real FieldDepot.
    /// </summary>
    internal static class BackgroundMachineProductionRuntime
    {
        private const string Initialized = "vw_bg_machine_init";
        private const string Ledger = "vw_bg_machine_ledger";
        private const string LastSimTicks = "vw_bg_machine_last_sim_ticks";
        private const string LastSnapshotTicks = "vw_bg_machine_snapshot_ticks";
        private const string DebugKey = "vw_bg_machine_debug";
        private const string ActiveWorkerKey = "vw_bg_machine_worker";
        private const float LoadedSnapshotInterval = 73f;

        private static readonly Collider[] ScanBuffer = new Collider[768];
        private static readonly HashSet<Smelter> Seen = new HashSet<Smelter>();
        private static readonly Dictionary<string, float> NextLoadedSnapshot = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, RecipeState> Aggregated = new Dictionary<string, RecipeState>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<RecipeState> Recipes = new List<RecipeState>(32);

        private static readonly FieldInfo ConversionField = typeof(Smelter).GetField("m_conversion", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo SecondsPerProductField = typeof(Smelter).GetField("m_secPerProduct", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo FuelPerProductField = typeof(Smelter).GetField("m_fuelPerProduct", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly Dictionary<Type, FieldInfo> FromFieldCache = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, FieldInfo> ToFieldCache = new Dictionary<Type, FieldInfo>();

        private sealed class RecipeState
        {
            internal string Input = string.Empty;
            internal string Output = string.Empty;
            internal string Fuel = string.Empty;
            internal float SecondsPerProduct = 30f;
            internal float FuelPerProduct = 0f;
            internal int Machines = 1;
            internal int InputBudget;
            internal int FuelBudget;
            internal int PendingBatches;
            internal int CommittedOutput;
            internal float Fraction;

            internal string Key => Input + ">" + Output + ">" + Fuel + ">" + SecondsPerProduct.ToString("0.###", CultureInfo.InvariantCulture) + ">" + FuelPerProduct.ToString("0.###", CultureInfo.InvariantCulture);
        }

        internal static void TickLoaded(Character worker, ZDO workerZdo)
        {
            if (worker == null || workerZdo == null || !WorkerZdo.IsWorker(workerZdo)) return;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out WorkOrder order, out Vector3 center, out float radius) || order != WorkOrder.KeepFires) return;

            // A specifically assigned fireplace is the Surtling's exclusive duty.
            if (WorkerZdo.TryReadFireAnchor(workerZdo, out ZDOID fireId, out _) && !fireId.Equals(ZDOID.None)) return;

            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId)) return;
            if (!WorkFlagRegistry.TryGetNetworkState(areaId, radius, out _, out ZDO flagZdo) || flagZdo == null) return;
            WorkFlagRegistry.RememberPersistent(flagZdo);
            // Remember which Firekeeper owns this machine simulation so the Smith background
            // adapter can avoid simulating the same furnace a second time. The worker ZDO itself
            // remains authoritative, so a deleted/reassigned Firekeeper automatically invalidates it.
            flagZdo.Set(ActiveWorkerKey, workerZdo.m_uid);

            // First settle previously simulated work against real inventories.
            Load(flagZdo, Recipes);
            if (Recipes.Count > 0)
            {
                FlushCommittedOutput(worker, workerZdo, Recipes);
                CommitPendingBatches(worker, workerZdo, Recipes);
                FlushCommittedOutput(worker, workerZdo, Recipes);
                Save(flagZdo, Recipes);
            }

            if (NextLoadedSnapshot.TryGetValue(areaId, out float next) && Time.realtimeSinceStartup < next) return;
            NextLoadedSnapshot[areaId] = Time.realtimeSinceStartup + LoadedSnapshotInterval;

            SnapshotRecipes(center, Mathf.Max(6f, radius), worker, workerZdo, Recipes);
            flagZdo.Set(Initialized, Recipes.Count > 0);
            flagZdo.Set(LastSnapshotTicks, DateTime.UtcNow.Ticks);
            flagZdo.Set(LastSimTicks, DateTime.UtcNow.Ticks);
            Save(flagZdo, Recipes);
            flagZdo.Set(DebugKey, BuildSummary("loaded", Recipes));
        }

        internal static string TickBackground(ZDO workerZdo, double deltaSeconds, float efficiency)
        {
            if (workerZdo == null) return "machines:no-worker";
            if (WorkerZdo.TryReadFireAnchor(workerZdo, out ZDOID fireId, out _) && !fireId.Equals(ZDOID.None))
                return FirekeeperAssignedFireRuntime.DescribeBackground(workerZdo);

            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId)) return "machines:no-workzone";
            if (!WorkFlagRegistry.TryGetPersistentState(areaId, out ZDO flagZdo) || flagZdo == null) return "machines:no-ledger-flag";
            if (!flagZdo.GetBool(Initialized, false)) return "machines:no-snapshot";

            Load(flagZdo, Recipes);
            if (Recipes.Count == 0) return "machines:no-recipes";

            long now = DateTime.UtcNow.Ticks;
            long last = flagZdo.GetLong(LastSimTicks, 0L);
            double dt = last > 0L ? TimeSpan.FromTicks(Math.Max(0L, now - last)).TotalSeconds : Math.Max(0.0, deltaSeconds);
            dt = Math.Min(3600.0, dt);
            flagZdo.Set(LastSimTicks, now);
            if (dt <= 0.0) return BuildSummary("background", Recipes);

            float eff = Mathf.Clamp01(efficiency);
            for (int i = 0; i < Recipes.Count; i++)
            {
                RecipeState r = Recipes[i];
                if (string.IsNullOrEmpty(r.Input) || string.IsNullOrEmpty(r.Output)) continue;

                float seconds = Mathf.Max(1f, r.SecondsPerProduct);
                float produced = (float)(dt / seconds) * Mathf.Max(1, r.Machines) * eff;
                r.Fraction += produced;
                int wanted = Mathf.FloorToInt(r.Fraction);
                if (wanted <= 0) continue;

                int maxByInput = Mathf.Max(0, r.InputBudget);
                int batches = Mathf.Min(wanted, maxByInput);
                if (batches <= 0) continue;

                r.InputBudget -= batches;
                // No fuel budget is consumed: the Surtling supplies the heat itself.
                r.PendingBatches += batches;
                r.Fraction -= batches;
            }

            Save(flagZdo, Recipes);
            string summary = BuildSummary("background", Recipes);
            flagZdo.Set(DebugKey, summary);
            return summary;
        }


        internal static bool HasActiveMachineFirekeeper(ZDO anyWorkerZdo)
        {
            if (anyWorkerZdo == null) return false;
            string areaId = WorkerZdo.ReadWorkAreaId(anyWorkerZdo);
            if (string.IsNullOrEmpty(areaId) || !WorkFlagRegistry.TryGetPersistentState(areaId, out ZDO flagZdo) || flagZdo == null)
                return false;
            ZDOID workerId = flagZdo.GetZDOID(ActiveWorkerKey);
            if (workerId.Equals(ZDOID.None) || ZDOMan.instance == null) return false;
            ZDO firekeeper = ZDOMan.instance.GetZDO(workerId);
            if (firekeeper == null || !WorkerZdo.IsWorker(firekeeper)) return false;
            if (!string.Equals(WorkerZdo.ReadWorkAreaId(firekeeper), areaId, StringComparison.Ordinal)) return false;
            WorkerRole role = WorkerJobProfileRuntime.NormalizeRuntimeRole(firekeeper);
            if (role != WorkerRole.Firekeeper || !WorkerJobProfileRuntime.IsOrderEnabled(firekeeper, role, WorkOrder.KeepFires)) return false;
            if (WorkerZdo.TryReadFireAnchor(firekeeper, out ZDOID fireId, out _) && !fireId.Equals(ZDOID.None)) return false;
            return true;
        }

        internal static string GetDebugSummary(ZDO workerZdo)
        {
            if (workerZdo == null) return "machines:none";
            string areaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            if (string.IsNullOrEmpty(areaId) || !WorkFlagRegistry.TryGetPersistentState(areaId, out ZDO flagZdo) || flagZdo == null)
                return "machines:no-flag";
            return flagZdo.GetString(DebugKey, flagZdo.GetBool(Initialized, false) ? "machines:ready" : "machines:no-snapshot");
        }

        private static void SnapshotRecipes(Vector3 center, float radius, Character worker, ZDO workerZdo, List<RecipeState> result)
        {
            // Preserve pending/committed work from the previous ledger while refreshing physical budgets.
            Dictionary<string, RecipeState> old = new Dictionary<string, RecipeState>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < result.Count; i++) old[result[i].Key] = result[i];

            Aggregated.Clear();
            Seen.Clear();
            int hits = Physics.OverlapSphereNonAlloc(center, radius, ScanBuffer);
            for (int i = 0; i < hits; i++)
            {
                Collider c = ScanBuffer[i];
                if (c == null) continue;
                Smelter smelter = c.GetComponentInParent<Smelter>();
                if (smelter == null || !Seen.Add(smelter)) continue;

                // Firekeeper background work only applies to actual fuel/heat-driven machines.
                // This excludes windmills, spinning wheels and charcoal kilns that merely reuse
                // Valheim's Smelter component but do not have a fuel tank.
                if (smelter.m_maxFuel <= 0.01f || smelter.m_fuelItem == null) continue;

                float seconds = ReadFloat(SecondsPerProductField, smelter, 30f);
                float fuelPerProduct = ReadFloat(FuelPerProductField, smelter, 0f);
                string fuel = string.Empty;
                FirekeeperWorkRuntime.TryGetFuelPrefab(smelter, out fuel);

                if (ConversionField == null) continue;
                object raw;
                try { raw = ConversionField.GetValue(smelter); } catch { continue; }
                if (!(raw is IEnumerable conversions)) continue;

                foreach (object conversion in conversions)
                {
                    if (!TryReadConversion(conversion, out string input, out string output)) continue;
                    RecipeState proto = new RecipeState
                    {
                        Input = input,
                        Output = output,
                        Fuel = fuel,
                        SecondsPerProduct = Mathf.Max(1f, seconds),
                        FuelPerProduct = Mathf.Max(0f, fuelPerProduct),
                        Machines = 1
                    };
                    string key = proto.Key;
                    if (Aggregated.TryGetValue(key, out RecipeState existing)) existing.Machines++;
                    else Aggregated[key] = proto;
                }
            }

            result.Clear();
            foreach (RecipeState r in Aggregated.Values)
            {
                r.InputBudget = Mathf.Max(0, WorkerStorageRuntime.CountGardenResourceInStorage(worker, workerZdo, r.Input) - 1);
                r.FuelBudget = int.MaxValue; // informational only; Surtling heat is free
                if (old.TryGetValue(r.Key, out RecipeState prior))
                {
                    r.PendingBatches = Mathf.Max(0, prior.PendingBatches);
                    r.CommittedOutput = Mathf.Max(0, prior.CommittedOutput);
                    r.Fraction = Mathf.Max(0f, prior.Fraction);
                }
                result.Add(r);
            }
        }

        private static void CommitPendingBatches(Character worker, ZDO workerZdo, List<RecipeState> recipes)
        {
            for (int i = 0; i < recipes.Count; i++)
            {
                RecipeState r = recipes[i];
                if (r.PendingBatches <= 0) continue;
                int committed = 0;
                while (committed < r.PendingBatches)
                {
                    int inputAvailable = WorkerStorageRuntime.CountGardenResourceInStorage(worker, workerZdo, r.Input);
                    if (inputAvailable <= 1) break;
                    if (!WorkerStorageRuntime.TryConsumeBackgroundResource(worker, workerZdo, r.Input, 1)) break;
                    // No wood/coal removal here: the Firekeeper's endless heat powers the machine.
                    committed++;
                    r.CommittedOutput++;
                }
                r.PendingBatches -= committed;
                if (committed > 0)
                {
                    // v0.5.365: only batches whose real input was actually consumed enter the
                    // rolling Workzone throughput. Pending speculative time alone is not counted.
                    ProductionMetricsRuntime.RecordConsumed(workerZdo, r.Input, committed);
                    ProductionMetricsRuntime.RecordProduced(workerZdo, r.Output, committed);
                }
            }
        }

        private static void FlushCommittedOutput(Character worker, ZDO workerZdo, List<RecipeState> recipes)
        {
            for (int i = 0; i < recipes.Count; i++)
            {
                RecipeState r = recipes[i];
                if (r.CommittedOutput <= 0 || string.IsNullOrEmpty(r.Output)) continue;
                int inserted = TryAddOutput(worker, workerZdo, r.Output, r.CommittedOutput);
                if (inserted > 0) r.CommittedOutput -= inserted;
            }
        }

        private static int TryAddOutput(Character worker, ZDO workerZdo, string prefabName, int amount)
        {
            if (amount <= 0 || string.IsNullOrEmpty(prefabName)) return 0;
            if (!WorkerStorageRuntime.TryFindFieldDepotForCargo(worker, workerZdo, prefabName, out Container depot, out _) || depot == null) return 0;
            Inventory inv = depot.GetInventory();
            GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (inv == null || drop?.m_itemData == null) return 0;

            int left = amount;
            int stackMax = Mathf.Max(1, drop.m_itemData.m_shared?.m_maxStackSize ?? 1);
            while (left > 0)
            {
                int chunk = Mathf.Min(left, stackMax);
                ItemDrop.ItemData data = drop.m_itemData.Clone();
                data.m_dropPrefab = prefab;
                data.m_stack = chunk;
                if (!WorkerStorageRuntime.TryAddItem(inv, data)) break;
                left -= chunk;
            }
            if (left != amount) WorkerStorageRuntime.PersistContainer(depot, inv);
            return amount - left;
        }

        private static bool TryReadConversion(object conversion, out string input, out string output)
        {
            input = string.Empty; output = string.Empty;
            if (conversion == null) return false;
            Type t = conversion.GetType();
            if (!FromFieldCache.TryGetValue(t, out FieldInfo fromField))
            {
                fromField = t.GetField("m_from", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                FromFieldCache[t] = fromField;
            }
            if (!ToFieldCache.TryGetValue(t, out FieldInfo toField))
            {
                toField = t.GetField("m_to", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                ToFieldCache[t] = toField;
            }
            if (fromField == null || toField == null) return false;
            ItemDrop from = ResolveItemDrop(fromField.GetValue(conversion));
            ItemDrop to = ResolveItemDrop(toField.GetValue(conversion));
            if (from == null || to == null) return false;
            input = SpeciesRoleRegistry.NormalizePrefabName(from.m_itemData?.m_dropPrefab != null ? from.m_itemData.m_dropPrefab.name : from.gameObject.name);
            output = SpeciesRoleRegistry.NormalizePrefabName(to.m_itemData?.m_dropPrefab != null ? to.m_itemData.m_dropPrefab.name : to.gameObject.name);
            return !string.IsNullOrEmpty(input) && !string.IsNullOrEmpty(output);
        }

        private static ItemDrop ResolveItemDrop(object value)
        {
            if (value is ItemDrop item) return item;
            if (value is GameObject go) return go.GetComponent<ItemDrop>();
            return null;
        }

        private static float ReadFloat(FieldInfo field, object instance, float fallback)
        {
            if (field == null || instance == null) return fallback;
            try
            {
                object value = field.GetValue(instance);
                return value != null ? Convert.ToSingle(value, CultureInfo.InvariantCulture) : fallback;
            }
            catch { return fallback; }
        }

        private static string BuildSummary(string mode, List<RecipeState> recipes)
        {
            int machines = 0, pending = 0, committed = 0, inputBudget = 0;
            for (int i = 0; i < recipes.Count; i++)
            {
                machines += Mathf.Max(1, recipes[i].Machines);
                pending += Mathf.Max(0, recipes[i].PendingBatches);
                committed += Mathf.Max(0, recipes[i].CommittedOutput);
                inputBudget += Mathf.Max(0, recipes[i].InputBudget);
            }
            return $"machines:{mode} recipes={recipes.Count}, machines~{machines}, inputBudget={inputBudget}, pending={pending}, outputWaiting={committed}";
        }

        private static void Load(ZDO flagZdo, List<RecipeState> result)
        {
            result.Clear();
            if (flagZdo == null) return;
            string raw = flagZdo.GetString(Ledger, string.Empty);
            if (string.IsNullOrEmpty(raw)) return;
            string[] entries = raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < entries.Length; i++)
            {
                string[] p = entries[i].Split('|');
                if (p.Length < 11) continue;
                if (!float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds)) seconds = 30f;
                if (!float.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float fuelPerProduct)) fuelPerProduct = 0f;
                int.TryParse(p[5], out int machines);
                int.TryParse(p[6], out int inputBudget);
                int.TryParse(p[7], out int fuelBudget);
                int.TryParse(p[8], out int pending);
                int.TryParse(p[9], out int committed);
                if (!float.TryParse(p[10], NumberStyles.Float, CultureInfo.InvariantCulture, out float fraction)) fraction = 0f;
                result.Add(new RecipeState
                {
                    Input = p[0], Output = p[1], Fuel = p[2], SecondsPerProduct = Mathf.Max(1f, seconds), FuelPerProduct = Mathf.Max(0f, fuelPerProduct),
                    Machines = Mathf.Max(1, machines), InputBudget = Mathf.Max(0, inputBudget), FuelBudget = Mathf.Max(0, fuelBudget),
                    PendingBatches = Mathf.Max(0, pending), CommittedOutput = Mathf.Max(0, committed), Fraction = Mathf.Max(0f, fraction)
                });
            }
        }

        private static void Save(ZDO flagZdo, List<RecipeState> recipes)
        {
            if (flagZdo == null) return;
            List<string> entries = new List<string>(recipes.Count);
            for (int i = 0; i < recipes.Count; i++)
            {
                RecipeState r = recipes[i];
                entries.Add(string.Join("|", new[]
                {
                    r.Input ?? string.Empty, r.Output ?? string.Empty, r.Fuel ?? string.Empty,
                    r.SecondsPerProduct.ToString("0.###", CultureInfo.InvariantCulture),
                    r.FuelPerProduct.ToString("0.###", CultureInfo.InvariantCulture),
                    r.Machines.ToString(CultureInfo.InvariantCulture), r.InputBudget.ToString(CultureInfo.InvariantCulture),
                    r.FuelBudget.ToString(CultureInfo.InvariantCulture), r.PendingBatches.ToString(CultureInfo.InvariantCulture),
                    r.CommittedOutput.ToString(CultureInfo.InvariantCulture), r.Fraction.ToString("0.###", CultureInfo.InvariantCulture)
                }));
            }
            flagZdo.Set(Ledger, string.Join(";", entries));
        }
    }
}
