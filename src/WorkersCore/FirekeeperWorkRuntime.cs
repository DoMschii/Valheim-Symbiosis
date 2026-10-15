using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    internal static class FirekeeperWorkRuntime
    {
        internal const string FuelSourceTargetKind = "firekeeper_fuel_source";
        internal const string SmelterTargetKind = "firekeeper_smelter";
        internal const string InputSourceTargetKind = "firekeeper_input_source";
        internal const string SmelterInputTargetKind = "firekeeper_smelter_input";

        private const string SmithClaimStationKey = "vw_smith_prod_claim_station";
        private const string SmithClaimOutputKey = "vw_smith_prod_claim_output";
        private const string SmithClaimInputKey = "vw_smith_prod_claim_input";

        private static readonly Collider[] ScanBuffer = new Collider[1024];
        private static readonly HashSet<Smelter> Seen = new HashSet<Smelter>();
        private static readonly MethodInfo GetFuelMethod = typeof(Smelter).GetMethod(
            "GetFuel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        private static readonly MethodInfo GetQueueSizeMethod = typeof(Smelter).GetMethod(
            "GetQueueSize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        // v0.5.361: Prefer the native per-slot queue accessor when Valheim exposes it. A single
        // aggregate queue size cannot distinguish e.g. queued Tin from queued Copper on the same
        // smelter and could therefore make one MaintainStock order look fulfilled by another metal.
        // Reflection keeps this compatible with builds where the method is private or absent.
        private static readonly MethodInfo GetQueueItemMethod = typeof(Smelter).GetMethod(
            "GetQueueItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
        private static readonly FieldInfo ConversionField = typeof(Smelter).GetField(
            "m_conversion", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo MaxOreField = typeof(Smelter).GetField(
            "m_maxOre", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo SmelterNviewField = typeof(Smelter).GetField(
            "m_nview", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo IsItemAllowedMethod = typeof(Smelter).GetMethod(
            "IsItemAllowed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
        private static readonly MethodInfo RpcAddOreMethod =
            typeof(Smelter).GetMethod("RPC_AddOre", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(long), typeof(string), typeof(bool) }, null) ??
            typeof(Smelter).GetMethod("RPC_AddOre", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(long), typeof(string) }, null);
        private static readonly MethodInfo ClaimOwnershipMethod = typeof(ZNetView).GetMethod(
            "ClaimOwnership", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        private static readonly MethodInfo GetMyIdMethod = typeof(ZDOMan).GetMethod(
            "GetMyID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        private static readonly Dictionary<Type, FieldInfo> ConversionFromFieldCache = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, FieldInfo> ConversionToFieldCache = new Dictionary<Type, FieldInfo>();
        private static readonly List<OutputSignature> OutputSignatures = new List<OutputSignature>();
        private static readonly string[] KnownMetalInputs =
        {
            "CopperOre", "TinOre", "IronScrap", "SilverOre", "BlackMetalScrap", "FlametalOre", "FlametalOreNew"
        };
        private static readonly string[] KnownMetalOutputs =
        {
            "Copper", "Tin", "Iron", "Silver", "BlackMetal", "Flametal", "FlametalNew"
        };
        private static readonly HashSet<Smelter> LoadedSmelters = new HashSet<Smelter>();
        private static readonly List<Smelter> SmelterScratch = new List<Smelter>(64);
        private static readonly List<Smelter> StaleSmelterScratch = new List<Smelter>(16);
        private static float _nextLocalSmelterRepairAt;
        private const float LocalSmelterRepairCooldownSeconds = 2f;

        private sealed class MagicFuelState
        {
            internal float NextActionAt;
            internal bool AimPrimed;
            internal float AimReadyAt;
            internal Vector3 AimPoint;
        }
        private static readonly Dictionary<int, MagicFuelState> MagicFuelStates = new Dictionary<int, MagicFuelState>();

        private static string _lastDebug = "not scanned";
        internal static string GetDebugStatus() => _lastDebug;

        internal static void RefreshSmithProductionClaim(ZDO workerZdo)
        {
            if (workerZdo == null) return;
            ZDOID stationId = workerZdo.GetZDOID(SmithClaimStationKey);
            string output = workerZdo.GetString(SmithClaimOutputKey, string.Empty);
            if (stationId.Equals(ZDOID.None) || string.IsNullOrEmpty(output) || ZDOMan.instance == null) return;

            ZDO stationZdo = ZDOMan.instance.GetZDO(stationId);
            if (stationZdo == null) return;
            ProductionOrderClaimRuntime.EnsureClaim(stationZdo, workerZdo, output, 1);
        }

        internal static void ReleaseSmithProductionClaim(ZDO workerZdo)
        {
            if (workerZdo == null) return;
            ProductionOrderClaimRuntime.ReleaseClaimsForWorker(workerZdo);
            workerZdo.Set(SmithClaimStationKey, ZDOID.None);
            workerZdo.Set(SmithClaimOutputKey, string.Empty);
            workerZdo.Set(SmithClaimInputKey, string.Empty);
        }

        private static void WriteSmithProductionClaim(ZDO workerZdo, ZDO stationZdo, string output, string input)
        {
            if (workerZdo == null || stationZdo == null || string.IsNullOrEmpty(output)) return;
            workerZdo.Set(SmithClaimStationKey, stationZdo.m_uid);
            workerZdo.Set(SmithClaimOutputKey, output);
            workerZdo.Set(SmithClaimInputKey, input ?? string.Empty);
        }

        private static bool TryReadSmithProductionClaim(ZDO workerZdo, out ZDOID stationId, out string output, out string input)
        {
            stationId = ZDOID.None;
            output = string.Empty;
            input = string.Empty;
            if (workerZdo == null) return false;
            stationId = workerZdo.GetZDOID(SmithClaimStationKey);
            output = workerZdo.GetString(SmithClaimOutputKey, string.Empty);
            input = workerZdo.GetString(SmithClaimInputKey, string.Empty);
            return !stationId.Equals(ZDOID.None) && !string.IsNullOrEmpty(output);
        }

        private static bool TryClaimSmithProduction(ZDO workerZdo, ZDO stationZdo, string output, string input)
        {
            if (workerZdo == null || stationZdo == null || string.IsNullOrEmpty(output)) return false;
            if (!ProductionOrderClaimRuntime.TryClaim(stationZdo, workerZdo, output, 1, out int claimed) || claimed <= 0)
                return false;
            WriteSmithProductionClaim(workerZdo, stationZdo, output, input);
            return true;
        }

        // Smelter/BlastFurnace discovery must not depend on their colliders being returned by a
        // default Physics query.  Vanilla production pieces can expose trigger/child colliders,
        // which made the Smith report smelters=0 even while standing next to a blast furnace.
        // The existing Smelter.Awake Harmony patch feeds this cheap lifecycle index.  A throttled
        // local Physics repair remains only as a safety net for already-loaded/missed instances.
        internal static void RegisterLoadedSmelter(Smelter smelter)
        {
            if (smelter == null || smelter.gameObject == null) return;
            LoadedSmelters.Add(smelter);
        }

        private static void CollectSmeltersNear(Vector3 center, float radius, List<Smelter> result)
        {
            result.Clear();
            Seen.Clear();
            StaleSmelterScratch.Clear();

            float r = Mathf.Max(3f, radius);
            float rSqr = r * r;
            foreach (Smelter candidate in LoadedSmelters)
            {
                if (candidate == null || candidate.gameObject == null ||
                    !candidate.gameObject.scene.IsValid() || !candidate.gameObject.activeInHierarchy)
                {
                    StaleSmelterScratch.Add(candidate);
                    continue;
                }

                Vector3 delta = candidate.transform.position - center;
                delta.y = 0f;
                if (delta.sqrMagnitude > rSqr || !Seen.Add(candidate)) continue;
                result.Add(candidate);
            }

            for (int i = 0; i < StaleSmelterScratch.Count; i++)
                LoadedSmelters.Remove(StaleSmelterScratch[i]);
            StaleSmelterScratch.Clear();

            float now = Time.unscaledTime;
            if (now < _nextLocalSmelterRepairAt) return;
            _nextLocalSmelterRepairAt = now + LocalSmelterRepairCooldownSeconds;

            int hits = 0;
            try
            {
                hits = Physics.OverlapSphereNonAlloc(center, r, ScanBuffer, ~0, QueryTriggerInteraction.Collide);
            }
            catch
            {
                hits = 0;
            }

            for (int i = 0; i < hits && i < ScanBuffer.Length; i++)
            {
                Collider hit = ScanBuffer[i];
                ScanBuffer[i] = null;
                if (hit == null) continue;

                Smelter candidate = hit.GetComponent<Smelter>() ??
                                    hit.GetComponentInParent<Smelter>() ??
                                    hit.GetComponentInChildren<Smelter>();
                if (candidate == null) continue;
                RegisterLoadedSmelter(candidate);

                Vector3 delta = candidate.transform.position - center;
                delta.y = 0f;
                if (delta.sqrMagnitude > rSqr || !Seen.Add(candidate)) continue;
                result.Add(candidate);
            }
        }


        private sealed class OutputSignature
        {
            internal string Prefab;
            internal Vector3 MachinePosition;
        }

        internal static bool TryFindNearestOutputDrop(
            Vector3 center,
            float radius,
            Vector3 from,
            out ItemDrop drop,
            out string outputPrefab,
            out Vector3 position)
        {
            drop = null;
            outputPrefab = string.Empty;
            position = Vector3.zero;
            OutputSignatures.Clear();
            CollectSmeltersNear(center, radius, SmelterScratch);

            for (int i = 0; i < SmelterScratch.Count; i++)
            {
                Smelter smelter = SmelterScratch[i];
                if (smelter == null || !IsSmithMetalMachine(smelter)) continue;

                List<string> outputs = new List<string>();
                GetProducedOutputs(smelter, outputs);
                for (int o = 0; o < outputs.Count; o++)
                {
                    string prefab = outputs[o];
                    if (string.IsNullOrEmpty(prefab)) continue;
                    OutputSignatures.Add(new OutputSignature { Prefab = prefab, MachinePosition = smelter.transform.position });
                }
            }

            if (OutputSignatures.Count == 0)
            {
                _lastDebug = "no machine output signatures in work area";
                return false;
            }

            float bestSqr = float.MaxValue;
            int candidates = 0;
            ItemDrop selectedDrop = null;
            string selectedOutputPrefab = null;
            Vector3 selectedPosition = Vector3.zero;
            WorkerWorldIndex.ForEachItemDrop(center, Mathf.Max(5f, radius), candidate =>
            {
                if (!GathererTargetFinder.IsEligible(candidate)) return;
                string prefab = GathererTargetFinder.GetCargoPrefabName(candidate);
                if (string.IsNullOrEmpty(prefab)) return;

                bool nearProducer = false;
                Vector3 candidatePosition = candidate.transform.position;
                for (int i = 0; i < OutputSignatures.Count; i++)
                {
                    OutputSignature sig = OutputSignatures[i];
                    if (!string.Equals(sig.Prefab, prefab, StringComparison.OrdinalIgnoreCase)) continue;
                    Vector3 a = candidatePosition; a.y = 0f;
                    Vector3 b = sig.MachinePosition; b.y = 0f;
                    if ((a - b).sqrMagnitude <= 36f) { nearProducer = true; break; }
                }
                if (!nearProducer) return;

                candidates++;
                float sqr = (candidatePosition - from).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                selectedDrop = candidate;
                selectedOutputPrefab = prefab;
                selectedPosition = candidatePosition;
            });

            drop = selectedDrop;
            outputPrefab = selectedOutputPrefab;
            position = selectedPosition;
            _lastDebug = $"machine outputs={OutputSignatures.Count}, looseOutputCandidates={candidates}, selected={(drop != null ? outputPrefab : "none")}";
            return drop != null;
        }

        private static void GetProducedOutputs(Smelter smelter, List<string> result)
        {
            result.Clear();
            if (smelter == null || ConversionField == null) return;
            object raw;
            try { raw = ConversionField.GetValue(smelter); } catch { return; }
            if (!(raw is IEnumerable enumerable)) return;

            foreach (object conversion in enumerable)
            {
                if (conversion == null) continue;
                Type t = conversion.GetType();
                if (!ConversionToFieldCache.TryGetValue(t, out FieldInfo toField))
                {
                    toField = t.GetField("m_to", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    ConversionToFieldCache[t] = toField;
                }
                if (toField == null) continue;

                object value;
                try { value = toField.GetValue(conversion); } catch { continue; }
                ItemDrop item = value as ItemDrop;
                if (item == null && value is GameObject go) item = go.GetComponent<ItemDrop>();
                if (item == null) continue;
                string prefab = SpeciesRoleRegistry.NormalizePrefabName(item.gameObject.name);
                if (string.IsNullOrEmpty(prefab) || result.Contains(prefab)) continue;
                result.Add(prefab);
            }
        }

        internal static string[] GetMetalOutputs(Smelter smelter)
        {
            if (smelter == null || !IsSmithMetalMachine(smelter)) return Array.Empty<string>();
            var result = new List<string>(8);
            if (ConversionField == null) return result.ToArray();

            try
            {
                IEnumerable list = ConversionField.GetValue(smelter) as IEnumerable;
                if (list == null) return result.ToArray();
                foreach (object conversion in list)
                {
                    if (!TryReadMetalConversion(conversion, out _, out string output)) continue;
                    if (string.IsNullOrEmpty(output) || result.Contains(output)) continue;
                    result.Add(output);
                }
            }
            catch { }

            // Compatibility for upgraded furnaces that extend IsItemAllowed dynamically without
            // exposing the extra conversion in m_conversion. Only advertise known pairs whose
            // input the live machine actually accepts.
            for (int i = 0; i < KnownMetalInputs.Length && i < KnownMetalOutputs.Length; i++)
            {
                if (!AcceptsInput(smelter, KnownMetalInputs[i])) continue;
                string output = KnownMetalOutputs[i];
                if (ObjectDB.instance != null && ObjectDB.instance.GetItemPrefab(output) == null) continue;
                if (!result.Contains(output)) result.Add(output);
            }
            return result.ToArray();
        }

        internal static bool TryGetMetalInputForOutput(Smelter smelter, string outputPrefab, out string inputPrefab)
        {
            inputPrefab = string.Empty;
            if (smelter == null || string.IsNullOrEmpty(outputPrefab) || ConversionField == null) return false;
            try
            {
                IEnumerable list = ConversionField.GetValue(smelter) as IEnumerable;
                if (list == null) return false;
                foreach (object conversion in list)
                {
                    if (!TryReadMetalConversion(conversion, out string input, out string output)) continue;
                    if (!string.Equals(output, outputPrefab, StringComparison.OrdinalIgnoreCase)) continue;
                    inputPrefab = input;
                    return !string.IsNullOrEmpty(inputPrefab);
                }
            }
            catch { }

            for (int i = 0; i < KnownMetalInputs.Length && i < KnownMetalOutputs.Length; i++)
            {
                if (!string.Equals(KnownMetalOutputs[i], outputPrefab, StringComparison.OrdinalIgnoreCase)) continue;
                if (!AcceptsInput(smelter, KnownMetalInputs[i])) continue;
                inputPrefab = KnownMetalInputs[i];
                return true;
            }
            return false;
        }

        internal static bool TryGetMetalOutputForInput(Smelter smelter, string inputPrefab, out string outputPrefab)
        {
            outputPrefab = string.Empty;
            if (smelter == null || string.IsNullOrEmpty(inputPrefab) || ConversionField == null) return false;
            try
            {
                IEnumerable list = ConversionField.GetValue(smelter) as IEnumerable;
                if (list == null) return false;
                foreach (object conversion in list)
                {
                    if (!TryReadMetalConversion(conversion, out string input, out string output)) continue;
                    if (!string.Equals(input, inputPrefab, StringComparison.OrdinalIgnoreCase)) continue;
                    outputPrefab = output;
                    return !string.IsNullOrEmpty(outputPrefab);
                }
            }
            catch { }

            for (int i = 0; i < KnownMetalInputs.Length && i < KnownMetalOutputs.Length; i++)
            {
                if (!string.Equals(KnownMetalInputs[i], inputPrefab, StringComparison.OrdinalIgnoreCase)) continue;
                if (!AcceptsInput(smelter, inputPrefab)) continue;
                outputPrefab = KnownMetalOutputs[i];
                return true;
            }
            return false;
        }

        private static bool TryReadMetalConversion(object conversion, out string inputPrefab, out string outputPrefab)
        {
            inputPrefab = string.Empty;
            outputPrefab = string.Empty;
            if (conversion == null) return false;

            Type t = conversion.GetType();
            if (!ConversionFromFieldCache.TryGetValue(t, out FieldInfo fromField))
            {
                fromField = t.GetField("m_from", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                ConversionFromFieldCache[t] = fromField;
            }
            if (!ConversionToFieldCache.TryGetValue(t, out FieldInfo toField))
            {
                toField = t.GetField("m_to", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                ConversionToFieldCache[t] = toField;
            }
            if (fromField == null || toField == null) return false;

            ItemDrop from = null;
            ItemDrop to = null;
            try
            {
                object fromRaw = fromField.GetValue(conversion);
                from = fromRaw as ItemDrop;
                if (from == null && fromRaw is GameObject fromGo) from = fromGo.GetComponent<ItemDrop>();

                object toRaw = toField.GetValue(conversion);
                to = toRaw as ItemDrop;
                if (to == null && toRaw is GameObject toGo) to = toGo.GetComponent<ItemDrop>();
            }
            catch { return false; }

            if (from == null || to == null) return false;
            string inputName = SpeciesRoleRegistry.NormalizePrefabName(
                from.m_itemData != null && from.m_itemData.m_dropPrefab != null ? from.m_itemData.m_dropPrefab.name : from.gameObject.name);
            string outputName = SpeciesRoleRegistry.NormalizePrefabName(
                to.m_itemData != null && to.m_itemData.m_dropPrefab != null ? to.m_itemData.m_dropPrefab.name : to.gameObject.name);
            if (string.IsNullOrEmpty(inputName) || string.IsNullOrEmpty(outputName) || !IsSmithMetalInput(inputName)) return false;

            inputPrefab = inputName;
            outputPrefab = outputName;
            return true;
        }

        private static bool HasMetalProductionOrders(Smelter smelter, out ZDO stationZdo)
        {
            stationZdo = null;
            ZNetView view = ResolveSmelterNetworkView(smelter);
            if (view == null || !view.IsValid() || view.GetZDO() == null) return false;
            stationZdo = view.GetZDO();

            string[] outputs = GetMetalOutputs(smelter);
            for (int i = 0; i < outputs.Length; i++)
                if (ProductionOrderRuntime.HasOrderFor(stationZdo, outputs[i])) return true;
            return false;
        }

        private static bool TryGetControlledMetalDemand(
            Character worker,
            ZDO workerZdo,
            Smelter smelter,
            Dictionary<string, int> stockSnapshot,
            out bool controlled,
            out ZDO stationZdo,
            out ProductionDemand demand,
            out string inputPrefab)
        {
            controlled = HasMetalProductionOrders(smelter, out stationZdo);
            demand = default;
            inputPrefab = string.Empty;
            if (!controlled || stationZdo == null || worker == null || workerZdo == null) return false;

            Func<string, int> stockCounter = output =>
            {
                int stock = WorkerStorageRuntime.CountStockSnapshot(stockSnapshot, output);
                // A queued ore is already committed production. Since v0.5.361 the queue is
                // counted per requested OUTPUT whenever Valheim exposes GetQueueItem(int). This
                // prevents Tin/Copper/etc. already queued in the same smelter from satisfying the
                // wrong MaintainStock target. Older/changed Valheim builds safely fall back to the
                // previous aggregate queue accounting rather than risking an overfill.
                if (TryGetMetalInputForOutput(smelter, output, out _))
                {
                    int queuedForOutput = GetQueuedMetalOutputAmount(smelter, output);
                    long withQueue = (long)stock + Math.Max(0, queuedForOutput);
                    return withQueue >= int.MaxValue ? int.MaxValue : (int)withQueue;
                }
                return stock;
            };

            Func<string, bool> canRun = output =>
                TryGetMetalInputForOutput(smelter, output, out string input) &&
                !string.IsNullOrEmpty(input) &&
                IsSmithMetalInput(input);

            if (!ProductionOrderRuntime.TryGetNextDemandForWorker(stationZdo, stockCounter, canRun, workerZdo, out demand) || demand.Needed <= 0)
                return false;
            return TryGetMetalInputForOutput(smelter, demand.Output, out inputPrefab) && !string.IsNullOrEmpty(inputPrefab);
        }

        private static bool TryFindInputSourceForPrefab(
            Character worker,
            ZDO zdo,
            string inputPrefab,
            out Container container,
            out Vector3 position)
        {
            container = null;
            position = Vector3.zero;
            if (worker == null || zdo == null || string.IsNullOrEmpty(inputPrefab)) return false;

            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Func<Container, bool> accept = candidate =>
            {
                if (candidate == null || !WorkerStorageRuntime.CanUseContainer(candidate, ownerId) ||
                    !WorkerContainerRoleRuntime.MatchesOwner(candidate, ownerId)) return false;
                WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(candidate);
                return role == WorkerContainerRole.Normal || role == WorkerContainerRole.FieldDepot;
            };
            Func<Container, int> protect = candidate =>
                candidate != null && WorkerContainerRoleRuntime.ProtectLastItem(candidate) ? 1 : 0;

            bool hasWorkArea = WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 workCenter, out float workRadius);
            if (hasWorkArea &&
                SharedStorageStockIndexRuntime.TryFindItemSource(inputPrefab, workCenter, Mathf.Clamp(workRadius, 6f, 150f),
                    worker.transform.position, 1, accept, protect, out container, out _, out position))
                return container != null;

            Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
            float homeRadius = Mathf.Clamp(workRadius > 0f ? workRadius : 30f, 12f, 150f);
            return SharedStorageStockIndexRuntime.TryFindItemSource(inputPrefab, home, homeRadius,
                worker.transform.position, 1, accept, protect, out container, out _, out position) && container != null;
        }

        internal static bool TryFindNearestSmithFuelJob(
            Character worker,
            ZDO zdo,
            Vector3 center,
            float radius,
            Vector3 from,
            string requiredFuelPrefab,
            out Smelter smelter,
            out Vector3 position,
            out string fuelPrefab)
        {
            smelter = null;
            position = Vector3.zero;
            fuelPrefab = string.Empty;
            if (worker == null || zdo == null) return false;

            CollectSmeltersNear(center, radius, SmelterScratch);
            Dictionary<string, int> snapshot = WorkerStorageRuntime.BuildGardenResourceStockSnapshot(worker, zdo);
            float best = float.MaxValue;
            for (int i = 0; i < SmelterScratch.Count; i++)
            {
                Smelter candidate = SmelterScratch[i];
                if (candidate == null || !IsSmithMetalMachine(candidate)) continue;
                if (!TryGetFuelPrefab(candidate, out string candidateFuel)) continue;
                if (!string.IsNullOrEmpty(requiredFuelPrefab) &&
                    !string.Equals(candidateFuel, requiredFuelPrefab, StringComparison.OrdinalIgnoreCase)) continue;
                if (!NeedsFuel(candidate)) continue;

                bool controlled;
                bool hasDemand = TryGetControlledMetalDemand(worker, zdo, candidate, snapshot,
                    out controlled, out _, out _, out _);
                if (controlled && !hasDemand && GetQueueSize(candidate) <= 0) continue;

                float sqr = (candidate.transform.position - from).sqrMagnitude;
                if (sqr >= best) continue;
                best = sqr;
                smelter = candidate;
                position = candidate.transform.position;
                fuelPrefab = candidateFuel;
            }
            return smelter != null;
        }


        internal static bool TryFindNearestNeedingFuel(
            Vector3 center,
            float radius,
            Vector3 from,
            string requiredFuelPrefab,
            out Smelter smelter,
            out Vector3 position,
            out string fuelPrefab)
        {
            smelter = null;
            position = Vector3.zero;
            fuelPrefab = string.Empty;
            CollectSmeltersNear(center, radius, SmelterScratch);

            float best = float.MaxValue;
            int candidates = 0;
            int needsFuel = 0;

            for (int i = 0; i < SmelterScratch.Count; i++)
            {
                Smelter candidate = SmelterScratch[i];
                if (candidate == null) continue;
                candidates++;
                if (!IsSmithMetalMachine(candidate)) continue;

                if (!TryGetFuelPrefab(candidate, out string candidateFuel)) continue;
                if (!string.IsNullOrEmpty(requiredFuelPrefab) &&
                    !string.Equals(candidateFuel, requiredFuelPrefab, StringComparison.OrdinalIgnoreCase)) continue;
                if (!NeedsFuel(candidate)) continue;
                needsFuel++;

                Vector3 p = candidate.transform.position;
                float sqr = (p - from).sqrMagnitude;
                if (sqr >= best) continue;
                best = sqr;
                smelter = candidate;
                position = p;
                fuelPrefab = candidateFuel;
            }

            _lastDebug = $"smelters={candidates}, needingFuel={needsFuel}, selected={(smelter != null ? SpeciesRoleRegistry.NormalizePrefabName(smelter.gameObject.name) : "none")}";
            return smelter != null;
        }


        internal static bool TryFindNearestNeedingInput(
            Vector3 center,
            float radius,
            Vector3 from,
            string requiredInputPrefab,
            out Smelter smelter,
            out Vector3 position,
            out string inputPrefab)
        {
            smelter = null;
            position = Vector3.zero;
            inputPrefab = string.Empty;
            CollectSmeltersNear(center, radius, SmelterScratch);

            float best = float.MaxValue;
            int candidates = 0;
            int needingInput = 0;

            // Smith/Metalworker must never claim generic Smelter-based production stations such
            // as the windmill (Barley), spinning wheel (Flax) or charcoal kiln (Wood). Valheim
            // reuses the Smelter component for those machines, so component type alone is not a
            // valid metal-station discriminator.
            if (!string.IsNullOrEmpty(requiredInputPrefab) && !IsSmithMetalInput(requiredInputPrefab))
            {
                _lastDebug = $"smith rejected non-metal input {requiredInputPrefab}";
                return false;
            }

            for (int i = 0; i < SmelterScratch.Count; i++)
            {
                Smelter candidate = SmelterScratch[i];
                if (candidate == null) continue;
                candidates++;
                if (!IsSmithMetalMachine(candidate) || !NeedsInput(candidate)) continue;

                string accepted = string.Empty;
                if (!string.IsNullOrEmpty(requiredInputPrefab))
                {
                    if (!AcceptsInput(candidate, requiredInputPrefab)) continue;
                    accepted = requiredInputPrefab;
                }
                else if (!TryGetFirstAcceptedInput(candidate, out accepted))
                {
                    continue;
                }

                needingInput++;
                Vector3 p = candidate.transform.position;
                float sqr = (p - from).sqrMagnitude;
                if (sqr >= best) continue;
                best = sqr;
                smelter = candidate;
                position = p;
                inputPrefab = accepted;
            }

            _lastDebug = $"smelters={candidates}, needingInput={needingInput}, selected={(smelter != null ? SpeciesRoleRegistry.NormalizePrefabName(smelter.gameObject.name) : "none")}";
            return smelter != null;
        }

        internal static bool TryFindNearestNeedingInputForSmithCargo(
            Character worker,
            ZDO zdo,
            Vector3 center,
            float radius,
            Vector3 from,
            string requiredInputPrefab,
            out Smelter smelter,
            out Vector3 position,
            out string inputPrefab)
        {
            smelter = null;
            position = Vector3.zero;
            inputPrefab = string.Empty;
            if (worker == null || zdo == null || string.IsNullOrEmpty(requiredInputPrefab) || !IsSmithMetalInput(requiredInputPrefab))
                return false;

            bool hasPersistedClaim = TryReadSmithProductionClaim(zdo, out ZDOID claimedStationId, out string claimedOutput, out string claimedInput);
            if (hasPersistedClaim && !string.IsNullOrEmpty(claimedInput) &&
                !string.Equals(claimedInput, requiredInputPrefab, StringComparison.OrdinalIgnoreCase))
            {
                ReleaseSmithProductionClaim(zdo);
                hasPersistedClaim = false;
                claimedStationId = ZDOID.None;
                claimedOutput = string.Empty;
            }

            CollectSmeltersNear(center, radius, SmelterScratch);
            Dictionary<string, int> stockSnapshot = WorkerStorageRuntime.BuildGardenResourceStockSnapshot(worker, zdo);
            float best = float.MaxValue;
            ZDO selectedStationZdo = null;
            string selectedOutput = string.Empty;
            bool selectedControlled = false;

            for (int i = 0; i < SmelterScratch.Count; i++)
            {
                Smelter candidate = SmelterScratch[i];
                if (candidate == null || !IsSmithMetalMachine(candidate) || !NeedsInput(candidate) ||
                    !AcceptsInput(candidate, requiredInputPrefab))
                    continue;

                ZNetView candidateView = ResolveSmelterNetworkView(candidate);
                ZDO candidateZdo = candidateView != null && candidateView.IsValid() ? candidateView.GetZDO() : null;
                if (hasPersistedClaim && (candidateZdo == null || !candidateZdo.m_uid.Equals(claimedStationId)))
                    continue;

                bool hasDemand = TryGetControlledMetalDemand(worker, zdo, candidate, stockSnapshot,
                    out bool controlled, out ZDO stationZdo, out ProductionDemand demand, out string demandedInput);
                if (controlled && (!hasDemand || !string.Equals(demandedInput, requiredInputPrefab, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (hasPersistedClaim && controlled &&
                    !string.IsNullOrEmpty(claimedOutput) && !string.Equals(demand.Output, claimedOutput, StringComparison.OrdinalIgnoreCase))
                    continue;

                float sqr = (candidate.transform.position - from).sqrMagnitude;
                if (sqr >= best) continue;
                best = sqr;
                smelter = candidate;
                position = candidate.transform.position;
                inputPrefab = requiredInputPrefab;
                selectedControlled = controlled;
                selectedStationZdo = stationZdo;
                selectedOutput = controlled ? demand.Output : string.Empty;
            }

            if (smelter == null)
            {
                if (hasPersistedClaim) ReleaseSmithProductionClaim(zdo);
                return false;
            }

            if (selectedControlled && selectedStationZdo != null && !string.IsNullOrEmpty(selectedOutput))
            {
                if (hasPersistedClaim)
                {
                    ProductionOrderClaimRuntime.EnsureClaim(selectedStationZdo, zdo, selectedOutput, 1);
                    WriteSmithProductionClaim(zdo, selectedStationZdo, selectedOutput, requiredInputPrefab);
                }
                else if (!TryClaimSmithProduction(zdo, selectedStationZdo, selectedOutput, requiredInputPrefab))
                {
                    smelter = null;
                    position = Vector3.zero;
                    inputPrefab = string.Empty;
                    return false;
                }
            }
            else if (hasPersistedClaim)
            {
                ReleaseSmithProductionClaim(zdo);
            }

            return smelter != null;
        }


        internal static bool TryFindNearestInputJob(
            Character worker,
            ZDO zdo,
            Vector3 center,
            float radius,
            Vector3 from,
            out Smelter smelter,
            out Container source,
            out Vector3 sourcePosition,
            out string inputPrefab)
        {
            smelter = null;
            source = null;
            sourcePosition = Vector3.zero;
            inputPrefab = string.Empty;
            if (worker == null || zdo == null) return false;

            // A fresh Smith job starts from a clean reservation state. A live travelling/collecting
            // job never enters this planner, so releasing here cannot steal the worker's own claim.
            ReleaseSmithProductionClaim(zdo);
            CollectSmeltersNear(center, radius, SmelterScratch);
            Dictionary<string, int> stockSnapshot = WorkerStorageRuntime.BuildGardenResourceStockSnapshot(worker, zdo);
            float best = float.MaxValue;
            int needing = 0;
            int withSource = 0;
            int controlledIdle = 0;
            ZDO selectedStationZdo = null;
            string selectedOutput = string.Empty;
            bool selectedControlled = false;

            // v0.5.352: a Smelter/BlastFurnace with explicit production orders is demand-driven.
            // Only the raw metal that creates the currently missing target output is fetched.
            // Machines without any production order keep the legacy "fill with compatible ore"
            // behaviour so existing worlds are not silently disabled.
            for (int i = 0; i < SmelterScratch.Count; i++)
            {
                Smelter candidate = SmelterScratch[i];
                if (candidate == null || !IsSmithMetalMachine(candidate) || !NeedsInput(candidate)) continue;
                needing++;

                Container candidateSource;
                Vector3 candidateSourcePosition;
                string candidatePrefab;

                bool hasDemand = TryGetControlledMetalDemand(worker, zdo, candidate, stockSnapshot,
                    out bool controlled, out ZDO stationZdo, out ProductionDemand demand, out string demandedInput);
                if (controlled)
                {
                    if (!hasDemand || string.IsNullOrEmpty(demandedInput))
                    {
                        controlledIdle++;
                        continue;
                    }

                    candidatePrefab = demandedInput;
                    if (!TryFindInputSourceForPrefab(worker, zdo, candidatePrefab,
                            out candidateSource, out candidateSourcePosition))
                        continue;
                }
                else
                {
                    if (!TryFindInputSource(worker, zdo, candidate, out candidateSource,
                            out candidateSourcePosition, out candidatePrefab))
                        continue;
                }

                withSource++;
                float machineSqr = (candidate.transform.position - from).sqrMagnitude;
                float sourceSqr = (candidateSourcePosition - from).sqrMagnitude;
                float score = machineSqr + sourceSqr * 0.15f;
                if (score >= best) continue;
                best = score;
                smelter = candidate;
                source = candidateSource;
                sourcePosition = candidateSourcePosition;
                inputPrefab = candidatePrefab;
                selectedControlled = controlled;
                selectedStationZdo = stationZdo;
                selectedOutput = controlled ? demand.Output : string.Empty;

                if (controlled)
                    _lastDebug = $"targeted {ProductionOrderRuntime.DisplayProduct(demand.Output)}: need {demand.Needed}, input={candidatePrefab}";
            }

            if (smelter == null)
                _lastDebug = $"smelters={SmelterScratch.Count}, needingInput={needing}, targetMet={controlledIdle}, withCompatibleStock={withSource}, selected=none";
            else if (_lastDebug == null || _lastDebug.IndexOf("targeted ", StringComparison.Ordinal) < 0)
                _lastDebug = $"smelters={SmelterScratch.Count}, needingInput={needing}, targetMet={controlledIdle}, withCompatibleStock={withSource}, selected={SpeciesRoleRegistry.NormalizePrefabName(smelter.gameObject.name)} <- {inputPrefab}";

            if (smelter != null && source != null && !string.IsNullOrEmpty(inputPrefab) && selectedControlled)
            {
                if (selectedStationZdo == null || string.IsNullOrEmpty(selectedOutput) ||
                    !TryClaimSmithProduction(zdo, selectedStationZdo, selectedOutput, inputPrefab))
                {
                    _lastDebug = "targeted metal demand could not be reserved; another Smith may already own it";
                    smelter = null;
                    source = null;
                    sourcePosition = Vector3.zero;
                    inputPrefab = string.Empty;
                    return false;
                }
            }

            return smelter != null && source != null && !string.IsNullOrEmpty(inputPrefab);
        }

        internal static bool TryFindInputSource(Character worker, ZDO zdo, Smelter smelter, out Container container, out Vector3 position, out string inputPrefab)
        {
            container = null;
            position = Vector3.zero;
            inputPrefab = string.Empty;
            if (worker == null || zdo == null || smelter == null) return false;

            // Do not assume that m_conversion is the complete truth. Several upgraded/modded
            // furnaces extend Smelter.IsItemAllowed at runtime without adding another visible
            // conversion entry. We still keep the conversion list for diagnostics, but choose
            // the real source item by asking the live machine about the actual prefab in storage.
            List<string> accepted = new List<string>();
            GetAcceptedInputs(smelter, accepted);

            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            bool hasWorkArea = WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 workCenter, out float workRadius);
            Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
            float homeRadius = Mathf.Clamp(workRadius > 0f ? workRadius : 30f, 12f, 150f);
            float bestScore = float.MaxValue;
            Container best = null;
            string bestPrefab = string.Empty;

            Action<Container> consider = candidate =>
            {
                if (candidate == null || !WorkerStorageRuntime.CanUseContainer(candidate, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(candidate, ownerId)) return;
                WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(candidate);
                if (role != WorkerContainerRole.Normal && role != WorkerContainerRole.FieldDepot) return;
                Inventory inv = candidate.GetInventory();
                if (inv == null) return;
                int protect = WorkerContainerRoleRuntime.ProtectLastItem(candidate) ? 1 : 0;
                List<ItemDrop.ItemData> items = inv.GetAllItems();
                if (items == null) return;

                for (int i = 0; i < items.Count; i++)
                {
                    ItemDrop.ItemData item = items[i];
                    string prefab = WorkerStorageRuntime.GetPrefabName(item);
                    if (string.IsNullOrEmpty(prefab) || !IsSmithMetalInput(prefab)) continue;
                    if (WorkerStorageRuntime.CountPrefab(inv, prefab) <= protect) continue;
                    if (!AcceptsInput(smelter, prefab)) continue;

                    float sqr = (candidate.transform.position - worker.transform.position).sqrMagnitude;
                    float score = sqr + (role == WorkerContainerRole.Normal ? 0f : 2500f);
                    if (score >= bestScore) continue;
                    bestScore = score;
                    best = candidate;
                    bestPrefab = prefab;
                }
            };

            if (hasWorkArea) WorkerStorageRuntime.ForEachContainerNear(workCenter, Mathf.Clamp(workRadius, 6f, 150f), consider);
            WorkerStorageRuntime.ForEachContainerNear(home, homeRadius, consider);

            if (best == null)
            {
                string advertised = accepted.Count > 0 ? string.Join(",", accepted) : "dynamic/native filter";
                _lastDebug = $"no local chest/FieldDepot has input accepted by {SpeciesRoleRegistry.NormalizePrefabName(smelter.gameObject.name)} ({advertised})";
                return false;
            }

            container = best;
            position = best.transform.position;
            inputPrefab = bestPrefab;
            _lastDebug = $"input source={WorkerContainerRoleRuntime.DisplayName(WorkerContainerRoleRuntime.GetRole(best))} {SpeciesRoleRegistry.NormalizePrefabName(best.gameObject.name)} -> {inputPrefab}";
            return true;
        }

        internal static bool TryCollectInput(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return false;
            if (!WorkerZdo.TryReadTarget(zdo, out string inputPrefab, out string kind, out Vector3 sourcePosition)) return false;
            if (!string.Equals(kind, InputSourceTargetKind, StringComparison.Ordinal) || string.IsNullOrEmpty(inputPrefab)) return false;
            if (!IsSmithMetalInput(inputPrefab))
            {
                // Self-heal stale v0.5.99 targets (for example Barley selected from a windmill)
                // without ever removing the item from storage.
                ReleaseSmithProductionClaim(zdo);
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                _lastDebug = $"discarded stale non-metal smith target {inputPrefab}";
                return true;
            }

            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Container source = null;
            float best = float.MaxValue;
            WorkerStorageRuntime.ForEachContainerNear(sourcePosition, 3.0f, candidate =>
            {
                if (candidate == null || !WorkerStorageRuntime.CanUseContainer(candidate, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(candidate, ownerId)) return;
                WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(candidate);
                if (role != WorkerContainerRole.Normal && role != WorkerContainerRole.FieldDepot) return;
                Inventory inv = candidate.GetInventory();
                int protect = WorkerContainerRoleRuntime.ProtectLastItem(candidate) ? 1 : 0;
                if (inv == null || WorkerStorageRuntime.CountPrefab(inv, inputPrefab) <= protect) return;
                float sqr = (candidate.transform.position - sourcePosition).sqrMagnitude;
                if (sqr >= best) return;
                best = sqr; source = candidate;
            });

            if (source == null)
            {
                ReleaseSmithProductionClaim(zdo);
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                _lastDebug = $"input source vanished/moved: {inputPrefab}; re-scan requested";
                return true;
            }
            bool localFallback = LocalWorkFallbackRuntime.IsActive(zdo, inputPrefab, kind);
            if (WorkerStorageRuntime.HorizontalDistance(worker.transform.position, source.transform.position) > WorkerStorageRuntime.ContainerAccessRadius + 0.5f && !localFallback) return false;
            if (!WorkerStorageRuntime.TryOwn(source)) return false;

            Inventory inventory = source.GetInventory();
            WorkerContainerRole sourceRole = WorkerContainerRoleRuntime.GetRole(source);
            int protectedCount = WorkerContainerRoleRuntime.ProtectLastItem(source) ? 1 : 0;
            ItemDrop.ItemData item = inventory != null ? WorkerStorageRuntime.FindPrefab(inventory, inputPrefab) : null;
            if (item == null || WorkerStorageRuntime.CountPrefab(inventory, inputPrefab) <= protectedCount || !WorkerStorageRuntime.TryRemoveOne(inventory, item))
            {
                ReleaseSmithProductionClaim(zdo);
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                _lastDebug = $"input unavailable in {WorkerContainerRoleRuntime.DisplayName(sourceRole)}: {inputPrefab}";
                return true;
            }

            WorkerStorageRuntime.PersistContainer(source, inventory);
            WorkerZdo.SetCargo(zdo, inputPrefab, 1);
            RefreshSmithProductionClaim(zdo);
            ProductionOrderClaimRuntime.TouchWorkerClaims(zdo);
            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            _lastDebug = $"picked up input {inputPrefab} x1 from {WorkerContainerRoleRuntime.DisplayName(sourceRole)}";
            return true;
        }

        internal static bool TryAddInput(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return false;
            if (!WorkerZdo.TryReadCargo(zdo, out string cargoPrefab, out int amount) || amount <= 0) return false;
            if (!IsSmithMetalInput(cargoPrefab))
            {
                // Never feed production materials such as Barley/Flax/Wood through the Smith.
                // Keep the real cargo intact so the normal cargo branch can return it to storage.
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                _lastDebug = $"smith retained/reroutes non-metal cargo {cargoPrefab}";
                return false;
            }
            if (!WorkerZdo.TryReadTarget(zdo, out _, out string kind, out Vector3 targetPosition)) return false;
            if (!string.Equals(kind, SmelterInputTargetKind, StringComparison.Ordinal)) return false;

            if (!TryFindNearestNeedingInputForSmithCargo(worker, zdo, targetPosition, 4f, worker.transform.position,
                    cargoPrefab, out Smelter smelter, out _, out _))
            {
                ReleaseSmithProductionClaim(zdo);
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                _lastDebug = $"machine no longer accepts/needs {cargoPrefab}; cargo retained";
                return false;
            }

            // A target-controlled furnace may have reached its goal while the Smith was walking.
            // Re-check immediately before committing the real ore. If the target is now covered,
            // keep the cargo and let the normal cargo branch return/re-route it.
            Dictionary<string, int> currentStock = WorkerStorageRuntime.BuildGardenResourceStockSnapshot(worker, zdo);
            bool stillNeeded = TryGetControlledMetalDemand(worker, zdo, smelter, currentStock,
                out bool controlled, out ZDO controlledStationZdo, out ProductionDemand controlledDemand, out string demandedInput);
            if (controlled && (!stillNeeded || !string.Equals(demandedInput, cargoPrefab, StringComparison.OrdinalIgnoreCase)))
            {
                ReleaseSmithProductionClaim(zdo);
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                _lastDebug = $"metal target already covered; {cargoPrefab} retained for storage/re-route";
                return false;
            }
            if (controlled && controlledStationZdo != null && stillNeeded)
            {
                ProductionOrderClaimRuntime.EnsureClaim(controlledStationZdo, zdo, controlledDemand.Output, 1);
                WriteSmithProductionClaim(zdo, controlledStationZdo, controlledDemand.Output, cargoPrefab);
            }

            bool localFallback = LocalWorkFallbackRuntime.IsActive(zdo,
                SpeciesRoleRegistry.NormalizePrefabName(smelter.gameObject.name), kind);
            if (!WorkerInteractionRuntime.IsInsideAura(worker.transform.position, smelter.transform.position) && !localFallback) return false;

            ZNetView nview = ResolveSmelterNetworkView(smelter);
            if (nview == null || !nview.IsValid())
            {
                ReleaseSmithProductionClaim(zdo);
                _lastDebug = $"{SpeciesRoleRegistry.NormalizePrefabName(smelter.gameObject.name)} has no valid Smelter ZNetView; cargo retained";
                return false;
            }

            // The native RPC receiver only mutates the queue on the owning peer. Claim the loaded
            // production piece first so we can call the real handler synchronously and verify that
            // one queue slot was actually created before deleting the Worker's logical cargo.
            if (!TryOwnSmelter(nview))
            {
                _lastDebug = $"smelter ownership pending for {SpeciesRoleRegistry.NormalizePrefabName(smelter.gameObject.name)}; {cargoPrefab} retained";
                return false;
            }

            if (!AcceptsInput(smelter, cargoPrefab) || !NeedsInput(smelter))
            {
                ReleaseSmithProductionClaim(zdo);
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                _lastDebug = $"{SpeciesRoleRegistry.NormalizePrefabName(smelter.gameObject.name)} rejected/no longer needs {cargoPrefab}; cargo retained";
                return false;
            }

            int queueBefore = GetQueueSize(smelter);
            try
            {
                if (!InvokeAddOreNative(smelter, cargoPrefab, false, out string invokeDetail))
                {
                    _lastDebug = $"smelter input unsupported; {cargoPrefab} retained ({invokeDetail})";
                    return false;
                }

                int queueAfter = GetQueueSize(smelter);
                if (queueBefore >= 0 && queueAfter <= queueBefore)
                {
                    // Most importantly: never make the real ore disappear just because a custom
                    // upgraded furnace ignored the request. Retry/re-route with the cargo intact.
                    _lastDebug = $"{SpeciesRoleRegistry.NormalizePrefabName(smelter.gameObject.name)} did not queue {cargoPrefab}; cargo retained ({queueBefore}->{queueAfter})";
                    return false;
                }

                if (TryGetMetalOutputForInput(smelter, cargoPrefab, out string queuedOutput))
                {
                    ZDO stationZdo = nview.GetZDO();
                    if (stationZdo != null)
                    {
                        // MakeAmount is committed when the ore is successfully queued. Stock-based
                        // orders are instead protected by the live queue count in demand evaluation.
                        ProductionOrderRuntime.RegisterProduced(stationZdo, queuedOutput, 1);
                    }

                    // v0.5.365 economy throughput: a successfully queued ore is a real committed
                    // conversion. Count the input and its exact mapped metal output once here.
                    ProductionMetricsRuntime.RecordConsumed(zdo, cargoPrefab, 1);
                    ProductionMetricsRuntime.RecordProduced(zdo, queuedOutput, 1);
                }

                ReleaseSmithProductionClaim(zdo);
                if (amount <= 1) WorkerZdo.ClearCargo(zdo);
                else WorkerZdo.SetCargo(zdo, cargoPrefab, amount - 1);
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                _lastDebug = $"queued {cargoPrefab} x1 in {SpeciesRoleRegistry.NormalizePrefabName(smelter.gameObject.name)} ({queueBefore}->{queueAfter})";
                if (localFallback) LocalWorkFallbackRuntime.Clear(zdo);
                return true;
            }
            catch (Exception ex)
            {
                _lastDebug = $"RPC_AddOre failed; cargo retained: {ex.GetBaseException().Message}";
                return false;
            }
        }

        internal static bool AcceptsInput(Smelter smelter, string prefab)
        {
            if (smelter == null || string.IsNullOrEmpty(prefab)) return false;

            // Use the live machine's native predicate first. This is the compatibility point used
            // by upgraded smelter mods that add/restrict inputs at runtime (for example allowing
            // BlackMetalScrap without replacing the whole Smelter component).
            if (IsItemAllowedMethod != null)
            {
                try
                {
                    object allowed = IsItemAllowedMethod.Invoke(smelter, new object[] { prefab });
                    if (allowed is bool nativeAllowed) return nativeAllowed;
                }
                catch { }
            }

            List<string> accepted = new List<string>();
            GetAcceptedInputs(smelter, accepted);
            for (int i = 0; i < accepted.Count; i++)
                if (string.Equals(accepted[i], prefab, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool TryGetFirstAcceptedInput(Smelter smelter, out string prefab)
        {
            prefab = string.Empty;
            List<string> accepted = new List<string>();
            GetAcceptedInputs(smelter, accepted);
            for (int i = 0; i < accepted.Count; i++)
            {
                if (!IsSmithMetalInput(accepted[i])) continue;
                prefab = accepted[i];
                return true;
            }
            return false;
        }

        internal static bool IsSmithMetalInput(string prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return false;
            string normalized = SpeciesRoleRegistry.NormalizePrefabName(prefab);
            if (string.IsNullOrEmpty(normalized)) normalized = prefab;
            string lower = normalized.ToLowerInvariant();

            // Vanilla and common modded raw-metal names are consistently ore/scrap based.
            // BlackMetalScrap is intentionally covered here. This excludes Barley, Flax, Wood,
            // SoftTissue and other generic Smelter-component production inputs.
            if (lower.Contains("ore") || lower.Contains("scrap")) return true;
            if (lower == "blackmetalraw" || lower == "flametalraw") return true;
            return false;
        }

        internal static bool IsSmithMetalMachine(Smelter smelter)
        {
            if (smelter == null) return false;

            List<string> accepted = new List<string>();
            GetAcceptedInputs(smelter, accepted);
            for (int i = 0; i < accepted.Count; i++)
                if (IsSmithMetalInput(accepted[i])) return true;

            // Compatibility for upgraded furnaces that extend IsItemAllowed dynamically without
            // exposing the added conversion in m_conversion.
            string[] probes = { "CopperOre", "TinOre", "IronScrap", "SilverOre", "BlackMetalScrap", "FlametalOre" };
            for (int i = 0; i < probes.Length; i++)
                if (AcceptsInput(smelter, probes[i])) return true;

            // Last-resort compatibility for furnace replacement mods with incomplete conversion
            // metadata. Do not use the generic word "oven" here: food ovens are not Smith jobs.
            string machine = SpeciesRoleRegistry.NormalizePrefabName(smelter.gameObject != null ? smelter.gameObject.name : string.Empty);
            string lower = (machine ?? string.Empty).ToLowerInvariant();
            return lower.Contains("smelter") || lower.Contains("blastfurnace") || lower.Contains("blast_furnace") || lower.Contains("furnace");
        }

        private static void GetAcceptedInputs(Smelter smelter, List<string> result)
        {
            result.Clear();
            if (smelter == null || ConversionField == null) return;
            try
            {
                IEnumerable list = ConversionField.GetValue(smelter) as IEnumerable;
                if (list == null) return;
                foreach (object conversion in list)
                {
                    if (conversion == null) continue;
                    Type t = conversion.GetType();
                    if (!ConversionFromFieldCache.TryGetValue(t, out FieldInfo fromField))
                    {
                        fromField = t.GetField("m_from", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        ConversionFromFieldCache[t] = fromField;
                    }
                    ItemDrop from = fromField != null ? fromField.GetValue(conversion) as ItemDrop : null;
                    if (from == null) continue;
                    GameObject drop = from.m_itemData != null ? from.m_itemData.m_dropPrefab : null;
                    string name = SpeciesRoleRegistry.NormalizePrefabName(drop != null ? drop.name : from.gameObject.name);
                    if (!string.IsNullOrEmpty(name) && !result.Contains(name)) result.Add(name);
                }
            }
            catch { }
        }

        private static ZNetView ResolveSmelterNetworkView(Smelter smelter)
        {
            if (smelter == null) return null;
            try
            {
                ZNetView cached = SmelterNviewField?.GetValue(smelter) as ZNetView;
                if (cached != null && cached.IsValid()) return cached;
            }
            catch { }

            ZNetView view = smelter.GetComponent<ZNetView>();
            if (view == null) view = smelter.GetComponentInParent<ZNetView>();
            if (view == null) view = smelter.GetComponentInChildren<ZNetView>();
            return view;
        }

        private static bool TryOwnSmelter(ZNetView view)
        {
            if (view == null || !view.IsValid()) return false;
            if (view.IsOwner()) return true;
            if (ClaimOwnershipMethod == null) return false;
            try
            {
                ClaimOwnershipMethod.Invoke(view, null);
                return view.IsOwner();
            }
            catch { return false; }
        }

        private static long GetLocalRpcSender()
        {
            try
            {
                if (ZDOMan.instance == null || GetMyIdMethod == null) return 0L;
                object value = GetMyIdMethod.Invoke(ZDOMan.instance, null);
                return value != null ? Convert.ToInt64(value) : 0L;
            }
            catch { return 0L; }
        }

        private static bool InvokeAddOreNative(Smelter smelter, string prefab, bool cheated, out string detail)
        {
            detail = string.Empty;
            if (smelter == null || string.IsNullOrEmpty(prefab))
            {
                detail = "invalid smelter/input";
                return false;
            }
            if (RpcAddOreMethod == null)
            {
                detail = "Smelter.RPC_AddOre not found";
                return false;
            }

            ParameterInfo[] parameters = RpcAddOreMethod.GetParameters();
            object[] args;
            if (parameters.Length == 3 && parameters[0].ParameterType == typeof(long) &&
                parameters[1].ParameterType == typeof(string) && parameters[2].ParameterType == typeof(bool))
            {
                args = new object[] { GetLocalRpcSender(), prefab, cheated };
            }
            else if (parameters.Length == 2 && parameters[0].ParameterType == typeof(long) &&
                     parameters[1].ParameterType == typeof(string))
            {
                // Legacy fallback for older Smelter layouts.
                args = new object[] { GetLocalRpcSender(), prefab };
            }
            else
            {
                detail = $"unexpected RPC_AddOre signature ({parameters.Length} params)";
                return false;
            }

            RpcAddOreMethod.Invoke(smelter, args);
            detail = $"native RPC_AddOre/{parameters.Length}";
            return true;
        }

        private static int GetQueueSize(Smelter smelter)
        {
            if (smelter == null || GetQueueSizeMethod == null) return -1;
            try
            {
                object value = GetQueueSizeMethod.Invoke(smelter, null);
                return value != null ? Convert.ToInt32(value) : -1;
            }
            catch { return -1; }
        }

        /// <summary>
        /// Returns how many already queued ore entries will produce the requested metal output.
        /// Valheim's native GetQueueItem(int) is used when available, so mixed smelter queues are
        /// accounted by product identity instead of by total queue length. If a future/older game
        /// build does not expose that accessor, keep the conservative legacy aggregate behaviour:
        /// it may delay a different metal briefly, but it will not silently overfill a stock target.
        /// </summary>
        internal static int GetQueuedMetalOutputAmount(Smelter smelter, string outputPrefab)
        {
            if (smelter == null || string.IsNullOrWhiteSpace(outputPrefab)) return 0;
            int totalQueue = Math.Max(0, GetQueueSize(smelter));
            if (totalQueue <= 0) return 0;

            if (GetQueueItemMethod != null)
            {
                try
                {
                    int matched = 0;
                    for (int i = 0; i < totalQueue; i++)
                    {
                        object raw = GetQueueItemMethod.Invoke(smelter, new object[] { i });
                        string input = raw as string;
                        if (string.IsNullOrWhiteSpace(input)) continue;
                        input = SpeciesRoleRegistry.NormalizePrefabName(input);
                        if (!TryGetMetalOutputForInput(smelter, input, out string queuedOutput)) continue;
                        if (string.Equals(queuedOutput, outputPrefab, StringComparison.OrdinalIgnoreCase)) matched++;
                    }
                    return matched;
                }
                catch
                {
                    // Fall through to the conservative aggregate queue count below.
                }
            }

            return totalQueue;
        }

        internal static bool NeedsInput(Smelter smelter)
        {
            if (smelter == null || GetQueueSizeMethod == null || MaxOreField == null) return false;
            try
            {
                int queued = GetQueueSize(smelter);
                int max = Convert.ToInt32(MaxOreField.GetValue(smelter));
                return max > 0 && queued >= 0 && queued < max;
            }
            catch { return false; }
        }

        internal static bool TryFindFuelSource(Character worker, ZDO zdo, string fuelPrefab, out Container container, out Vector3 position)
        {
            container = null;
            position = Vector3.zero;
            if (worker == null || zdo == null || string.IsNullOrEmpty(fuelPrefab)) return false;

            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            bool hasWorkArea = WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 workCenter, out float workRadius);
            Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
            float homeRadius = Mathf.Clamp(workRadius > 0f ? workRadius : 30f, 12f, 150f);
            float bestScore = float.MaxValue;
            Container best = null;

            Action<Container> consider = candidate =>
            {
                if (candidate == null || !WorkerStorageRuntime.CanUseContainer(candidate, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(candidate, ownerId)) return;
                WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(candidate);
                if (role != WorkerContainerRole.Normal && role != WorkerContainerRole.FieldDepot) return;
                Inventory inv = candidate.GetInventory();
                if (inv == null) return;
                int protect = WorkerContainerRoleRuntime.ProtectLastItem(candidate) ? 1 : 0;
                if (WorkerStorageRuntime.CountPrefab(inv, fuelPrefab) <= protect) return;

                float sqr = (candidate.transform.position - worker.transform.position).sqrMagnitude;
                // Local ordinary storage is preferred for base production. FieldDepots remain a valid fallback.
                float score = sqr + (role == WorkerContainerRole.Normal ? 0f : 2500f);
                if (score >= bestScore) return;
                bestScore = score;
                best = candidate;
            };

            if (hasWorkArea) WorkerStorageRuntime.ForEachContainerNear(workCenter, Mathf.Clamp(workRadius, 6f, 150f), consider);
            WorkerStorageRuntime.ForEachContainerNear(home, homeRadius, consider);

            if (best == null)
            {
                _lastDebug = $"no local chest/FieldDepot has spare {fuelPrefab} (keep-one protected)";
                return false;
            }

            container = best;
            position = best.transform.position;
            _lastDebug = $"fuel source={WorkerContainerRoleRuntime.DisplayName(WorkerContainerRoleRuntime.GetRole(best))} {SpeciesRoleRegistry.NormalizePrefabName(best.gameObject.name)}";
            return true;
        }

        internal static bool TryCollectFuel(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return false;
            if (!WorkerZdo.TryReadTarget(zdo, out string fuelPrefab, out string kind, out _)) return false;
            if (!string.Equals(kind, FuelSourceTargetKind, StringComparison.Ordinal)) return false;
            if (string.IsNullOrEmpty(fuelPrefab)) return false;

            if (!WorkerZdo.TryReadTarget(zdo, out _, out _, out Vector3 sourcePosition)) return false;

            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Container source = null;
            float best = float.MaxValue;
            WorkerStorageRuntime.ForEachContainerNear(sourcePosition, 3.0f, candidate =>
            {
                if (candidate == null || !WorkerStorageRuntime.CanUseContainer(candidate, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(candidate, ownerId)) return;
                WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(candidate);
                if (role != WorkerContainerRole.Normal && role != WorkerContainerRole.FieldDepot) return;
                Inventory inv = candidate.GetInventory();
                int protect = WorkerContainerRoleRuntime.ProtectLastItem(candidate) ? 1 : 0;
                if (inv == null || WorkerStorageRuntime.CountPrefab(inv, fuelPrefab) <= protect) return;
                float sqr = (candidate.transform.position - sourcePosition).sqrMagnitude;
                if (sqr >= best) return;
                best = sqr; source = candidate;
            });

            if (source == null)
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                _lastDebug = $"fuel source vanished/moved: {fuelPrefab}; re-scan requested";
                return true;
            }

            bool localFallback = LocalWorkFallbackRuntime.IsActive(zdo, fuelPrefab, kind);
            if (WorkerStorageRuntime.HorizontalDistance(worker.transform.position, source.transform.position) > WorkerStorageRuntime.ContainerAccessRadius + 0.5f && !localFallback)
                return false;
            if (!WorkerStorageRuntime.TryOwn(source))
            {
                _lastDebug = $"fuel source ownership pending: {SpeciesRoleRegistry.NormalizePrefabName(source.gameObject.name)}";
                return false;
            }

            Inventory inventory = source.GetInventory();
            WorkerContainerRole sourceRole = WorkerContainerRoleRuntime.GetRole(source);
            int protectedCount = WorkerContainerRoleRuntime.ProtectLastItem(source) ? 1 : 0;
            ItemDrop.ItemData item = inventory != null ? WorkerStorageRuntime.FindPrefab(inventory, fuelPrefab) : null;
            if (item == null || WorkerStorageRuntime.CountPrefab(inventory, fuelPrefab) <= protectedCount || !WorkerStorageRuntime.TryRemoveOne(inventory, item))
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                _lastDebug = $"fuel unavailable in {WorkerContainerRoleRuntime.DisplayName(sourceRole)}: {fuelPrefab}";
                return true;
            }

            WorkerStorageRuntime.PersistContainer(source, inventory);
            WorkerZdo.SetCargo(zdo, fuelPrefab, 1);
            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            _lastDebug = $"picked up {fuelPrefab} x1 from {WorkerContainerRoleRuntime.DisplayName(sourceRole)}";
            return true;
        }

        internal static bool TryRefuel(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return false;
            if (!WorkerZdo.TryReadCargo(zdo, out string cargoPrefab, out int amount) || amount <= 0) return false;
            if (!WorkerZdo.TryReadTarget(zdo, out _, out string kind, out Vector3 targetPosition)) return false;
            if (!string.Equals(kind, SmelterTargetKind, StringComparison.Ordinal)) return false;

            if (!TryFindNearestNeedingFuel(targetPosition, 4f, worker.transform.position, cargoPrefab,
                    out Smelter smelter, out _, out string fuelPrefab))
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                _lastDebug = $"machine no longer needs {cargoPrefab}; cargo retained";
                return false;
            }

            bool localFallback = LocalWorkFallbackRuntime.IsActive(zdo,
                SpeciesRoleRegistry.NormalizePrefabName(smelter.gameObject.name), kind);
            if (!WorkerInteractionRuntime.IsInsideAura(worker.transform.position, smelter.transform.position) && !localFallback)
                return false;

            ZNetView nview = ResolveSmelterNetworkView(smelter);
            if (nview == null || !nview.IsValid())
            {
                _lastDebug = "smelter has no valid ZNetView; cargo retained";
                return false;
            }

            // Keep the real fuel conservative: only delete one cargo unit after the loaded machine
            // actually accepted it. Claiming ownership makes the native RPC deterministic on the
            // local peer and avoids consuming logical cargo while a remote owner ignored the call.
            if (!TryOwnSmelter(nview))
            {
                _lastDebug = $"smelter ownership pending for {SpeciesRoleRegistry.NormalizePrefabName(smelter.gameObject.name)}; {cargoPrefab} retained";
                return false;
            }

            float fuelBefore = GetFuel(smelter);
            try
            {
                nview.InvokeRPC("RPC_AddFuel", Array.Empty<object>());
                float fuelAfter = GetFuel(smelter);
                if (fuelBefore < float.MaxValue && fuelAfter <= fuelBefore + 0.001f)
                {
                    _lastDebug = $"{SpeciesRoleRegistry.NormalizePrefabName(smelter.gameObject.name)} did not accept {cargoPrefab}; cargo retained ({fuelBefore:0.##}->{fuelAfter:0.##})";
                    return false;
                }

                if (amount <= 1) WorkerZdo.ClearCargo(zdo);
                else WorkerZdo.SetCargo(zdo, cargoPrefab, amount - 1);
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                _lastDebug = $"refueled {SpeciesRoleRegistry.NormalizePrefabName(smelter.gameObject.name)} with {fuelPrefab} x1 ({fuelBefore:0.##}->{fuelAfter:0.##})";
                if (localFallback) LocalWorkFallbackRuntime.Clear(zdo);
                return true;
            }
            catch (Exception ex)
            {
                _lastDebug = $"RPC_AddFuel failed; cargo retained: {ex.GetBaseException().Message}";
                return false;
            }
        }

        internal static bool TryMagicallyRefuel(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return false;
            if (!WorkerZdo.TryReadTarget(zdo, out _, out string kind, out Vector3 targetPosition) ||
                !string.Equals(kind, SmelterTargetKind, StringComparison.Ordinal)) return false;

            if (!TryFindNearestNeedingFuel(targetPosition, 4f, worker.transform.position, null,
                    out Smelter smelter, out _, out _))
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                _lastDebug = "metal furnace no longer needs Surtling heat";
                return true;
            }

            bool localFallback = LocalWorkFallbackRuntime.IsActive(zdo,
                SpeciesRoleRegistry.NormalizePrefabName(smelter.gameObject.name), kind);
            if (!WorkerInteractionRuntime.IsInsideAura(worker.transform.position, smelter.transform.position) && !localFallback)
                return false;

            ZNetView nview = ResolveSmelterNetworkView(smelter);
            if (nview == null || !nview.IsValid())
            {
                _lastDebug = "metal furnace has no valid ZNetView; magical heat retrying";
                return false;
            }

            float fuelBefore = GetFuel(smelter);
            float maxFuel = smelter.m_maxFuel;
            if (maxFuel <= 0.01f || fuelBefore >= maxFuel - 0.01f)
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return true;
            }

            int key = worker.GetInstanceID();
            if (!MagicFuelStates.TryGetValue(key, out MagicFuelState state))
            {
                state = new MagicFuelState();
                MagicFuelStates[key] = state;
            }

            Vector3 aimPoint = GetMachineAimPoint(smelter.gameObject);
            FaceMachine(worker, aimPoint);
            if (Time.time < state.NextActionAt) return true;

            if (!state.AimPrimed || (state.AimPoint - aimPoint).sqrMagnitude > 0.04f)
            {
                state.AimPrimed = true;
                state.AimPoint = aimPoint;
                state.AimReadyAt = Time.time + 0.22f;
                return true;
            }

            if (Time.time < state.AimReadyAt) return true;
            FaceMachine(worker, aimPoint);
            FirekeeperProjectileAimRuntime.Arm(worker, aimPoint);

            // The fireball is visual feedback only. Older builds made successful refuelling
            // depend on StartAttack() returning true; friendly/recruited Surtlings can legitimately
            // have their native attack temporarily unavailable, which left a reachable furnace
            // unfuelled forever. Try the animation, but never gate the firekeeper work on it.
            bool fired = worker.StartAttack(null, false);
            state.AimPrimed = false;

            bool rpcSent = false;
            try
            {
                // RPC_AddFuel advances the machine fuel counter. Vanilla player code removes the
                // inventory item before this call; the Surtling intentionally skips that inventory
                // step because endless heat is this worker's unique capability.
                nview.InvokeRPC("RPC_AddFuel", Array.Empty<object>());
                rpcSent = true;
            }
            catch (Exception ex)
            {
                _lastDebug = $"magical RPC_AddFuel failed: {ex.GetBaseException().Message}; trying owner fallback";
            }

            // Verify the write. If the RPC did not advance the loaded machine and we can own it,
            // apply the same +1 fuel delta directly to the authoritative ZDO. No item is consumed.
            float fuelAfter = GetFuel(smelter);
            if (fuelAfter <= fuelBefore + 0.001f && TryOwnSmelter(nview))
            {
                ZDO machineZdo = nview.GetZDO();
                if (machineZdo != null)
                {
                    float current = machineZdo.GetFloat("fuel", fuelBefore);
                    float next = Mathf.Min(maxFuel, Mathf.Max(current, fuelBefore) + 1f);
                    machineZdo.Set("fuel", next);
                    fuelAfter = GetFuel(smelter);
                    if (fuelAfter <= fuelBefore + 0.001f) fuelAfter = next;
                }
            }

            if (fuelAfter <= fuelBefore + 0.001f)
            {
                state.NextActionAt = Time.time + 0.75f;
                _lastDebug = $"Surtling heat write not accepted by {SpeciesRoleRegistry.NormalizePrefabName(smelter.gameObject.name)} ({fuelBefore:0.##}->{fuelAfter:0.##}, rpc={(rpcSent ? "sent" : "failed")}, fireball={(fired ? "yes" : "no")}); retrying";
                return true;
            }

            state.NextActionAt = Time.time + 1.55f;
            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            if (localFallback) LocalWorkFallbackRuntime.Clear(zdo);
            _lastDebug = $"Surtling heat fueled {SpeciesRoleRegistry.NormalizePrefabName(smelter.gameObject.name)} for free ({fuelBefore:0.##}->{fuelAfter:0.##}/{maxFuel:0.##}); fireball={(fired ? "yes" : "visual unavailable")}; no wood/coal consumed";
            return true;
        }

        private static Vector3 GetMachineAimPoint(GameObject target)
        {
            if (target == null) return Vector3.zero;
            Bounds? combined = null;
            try
            {
                Collider[] colliders = target.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Length; i++)
                {
                    Collider c = colliders[i];
                    if (c == null || !c.enabled) continue;
                    if (!combined.HasValue) combined = c.bounds;
                    else
                    {
                        Bounds b = combined.Value;
                        b.Encapsulate(c.bounds);
                        combined = b;
                    }
                }
            }
            catch { }

            if (combined.HasValue)
            {
                Bounds b = combined.Value;
                return new Vector3(b.center.x, b.center.y + b.extents.y * 0.35f, b.center.z);
            }
            return target.transform.position + Vector3.up * 0.8f;
        }

        private static void FaceMachine(Character worker, Vector3 target)
        {
            if (worker == null) return;
            Vector3 dir = target - worker.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.001f) return;
            worker.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        internal static bool TryGetFuelPrefab(Smelter smelter, out string fuelPrefab)
        {
            fuelPrefab = string.Empty;
            if (smelter == null || smelter.m_maxFuel <= 0 || smelter.m_fuelItem == null) return false;
            GameObject drop = smelter.m_fuelItem.m_itemData != null ? smelter.m_fuelItem.m_itemData.m_dropPrefab : null;
            fuelPrefab = SpeciesRoleRegistry.NormalizePrefabName(drop != null ? drop.name : smelter.m_fuelItem.gameObject.name);
            return !string.IsNullOrEmpty(fuelPrefab);
        }

        internal static bool NeedsFuel(Smelter smelter)
        {
            if (smelter == null || smelter.m_maxFuel <= 0 || smelter.m_fuelItem == null) return false;
            float fuel = GetFuel(smelter);
            return fuel + 0.01f < smelter.m_maxFuel;
        }

        private static float GetFuel(Smelter smelter)
        {
            if (smelter == null || GetFuelMethod == null) return float.MaxValue;
            try
            {
                object value = GetFuelMethod.Invoke(smelter, null);
                return value != null ? Convert.ToSingle(value) : float.MaxValue;
            }
            catch { return float.MaxValue; }
        }
    }
}
