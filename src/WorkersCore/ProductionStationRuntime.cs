using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    internal enum ProductionStationKind
    {
        None = 0,
        Windmill = 1,
        Preparation = 2,
        Oven = 3,
        Cauldron = 4,
        Smelter = 5,
        BlastFurnace = 6
    }

    internal sealed class ProductionStationRef
    {
        internal GameObject Root;
        internal ZNetView View;
        internal ZDO Zdo;
        internal ProductionStationKind Kind;
        internal Vector3 Position;
        internal string DisplayName;
    }

    /// <summary>
    /// Production orders belong to the physical station that performs the recipe.
    /// Work flags only define the Baker workzone. Missing upstream products are never
    /// auto-produced: every intermediate product needs its own explicit station order.
    /// </summary>
    internal static class ProductionStationRuntime
    {
        private static readonly Collider[] ScanBuffer = new Collider[512];
        private static readonly Dictionary<ZDOID, GameObject> LoadedStations = new Dictionary<ZDOID, GameObject>();
        private sealed class PersistentStationRecord
        {
            internal ZDO Zdo;
            internal ProductionStationKind Kind;
            internal Vector3 Position;
            internal string DisplayName;
        }
        private static readonly Dictionary<ZDOID, PersistentStationRecord> PersistentStations = new Dictionary<ZDOID, PersistentStationRecord>();
        private static readonly List<GameObject> StationQueryScratch = new List<GameObject>(64);
        private static readonly List<ZDOID> StationCleanupScratch = new List<ZDOID>(16);
        private static float _nextLocalRepairAt;
        private const float LocalRepairCooldownSeconds = 20f;
        private static readonly string[] FlourOutput = { ProductionOrderRuntime.BarleyFlour };
        private static readonly string[] DoughOutput = { ProductionOrderRuntime.BreadDough };
        private static readonly string[] BreadOutput = { ProductionOrderRuntime.Bread };
        private static readonly string[] EmptyOutputs = Array.Empty<string>();

        internal static string DisplayName(ProductionStationKind kind)
        {
            switch (kind)
            {
                case ProductionStationKind.Windmill: return "Windmuehle";
                case ProductionStationKind.Preparation: return "Zubereitungsstation";
                case ProductionStationKind.Oven: return "Steinofen";
                case ProductionStationKind.Cauldron: return "Kessel";
                case ProductionStationKind.Smelter: return "Schmelzofen";
                case ProductionStationKind.BlastFurnace: return "Hochofen";
                default: return "Produktionsstation";
            }
        }

        internal static string OperatorHint(ProductionStationKind kind)
        {
            switch (kind)
            {
                case ProductionStationKind.Oven:
                    return "Koch / Produktion: Rezepte | Feuerwart: kostenlose Hitze/Brennstoff";
                case ProductionStationKind.Smelter:
                case ProductionStationKind.BlastFurnace:
                    return "Schmied: Erz/Schrott + Produktion | Feuerwart: kostenlose Hitze/Brennstoff";
                case ProductionStationKind.Windmill:
                case ProductionStationKind.Preparation:
                case ProductionStationKind.Cauldron:
                    return "Koch / Produktion";
                default:
                    return string.Empty;
            }
        }

        internal static string[] SupportedOutputs(ProductionStationKind kind)
        {
            switch (kind)
            {
                case ProductionStationKind.Windmill: return FlourOutput;
                case ProductionStationKind.Preparation:
                {
                    string[] discovered = CookingRecipeRuntime.GetOutputs(kind);
                    return discovered.Length > 0 ? discovered : DoughOutput;
                }
                case ProductionStationKind.Oven: return BreadOutput;
                case ProductionStationKind.Cauldron:
                {
                    string[] discovered = CookingRecipeRuntime.GetOutputs(kind);
                    return discovered.Length > 0 ? discovered : EmptyOutputs;
                }
                default: return EmptyOutputs;
            }
        }

        internal static string[] SupportedOutputs(ProductionStationRef station)
        {
            if (station == null) return EmptyOutputs;
            if (station.Kind != ProductionStationKind.Smelter && station.Kind != ProductionStationKind.BlastFurnace)
                return SupportedOutputs(station.Kind);

            Smelter smelter = station.Root != null
                ? (station.Root.GetComponent<Smelter>() ?? station.Root.GetComponentInParent<Smelter>() ?? station.Root.GetComponentInChildren<Smelter>())
                : null;
            return FirekeeperWorkRuntime.GetMetalOutputs(smelter);
        }

        internal static bool SupportsOutput(ProductionStationRef station, string output)
        {
            if (station == null || string.IsNullOrEmpty(output)) return false;
            string[] outputs = SupportedOutputs(station);
            for (int i = 0; i < outputs.Length; i++)
                if (string.Equals(outputs[i], output, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static int DefaultTarget(string output)
        {
            if (string.Equals(output, ProductionOrderRuntime.BarleyFlour, StringComparison.OrdinalIgnoreCase)) return 30;
            if (string.Equals(output, ProductionOrderRuntime.BreadDough, StringComparison.OrdinalIgnoreCase)) return 20;
            if (string.Equals(output, ProductionOrderRuntime.Bread, StringComparison.OrdinalIgnoreCase)) return 30;
            return 10;
        }

        internal static bool TryResolve(GameObject go, out ProductionStationRef station)
        {
            if (!TryResolveInternal(go, out station)) return false;
            RememberLoaded(station);
            return true;
        }

        private static bool TryResolveInternal(GameObject go, out ProductionStationRef station)
        {
            station = null;
            if (go == null) return false;

            Transform current = go.transform;
            for (int depth = 0; current != null && depth < 12; depth++, current = current.parent)
            {
                ProductionStationKind kind = Classify(current.gameObject);
                if (kind == ProductionStationKind.None) continue;

                ZNetView view = current.GetComponent<ZNetView>() ?? current.GetComponentInParent<ZNetView>() ?? current.GetComponentInChildren<ZNetView>();
                if (view == null || !view.IsValid() || view.GetZDO() == null) continue;

                station = new ProductionStationRef
                {
                    Root = current.gameObject,
                    View = view,
                    Zdo = view.GetZDO(),
                    Kind = kind,
                    Position = current.position,
                    DisplayName = DisplayName(kind)
                };
                return true;
            }

            Smelter smelter = go.GetComponent<Smelter>() ?? go.GetComponentInParent<Smelter>();
            if (smelter != null && TryResolveComponent(smelter.gameObject, out station)) return true;

            CraftingStation crafting = go.GetComponent<CraftingStation>() ?? go.GetComponentInParent<CraftingStation>();
            if (crafting != null && TryResolveComponent(crafting.gameObject, out station)) return true;
            return false;
        }

        private static void RememberLoaded(ProductionStationRef station)
        {
            if (station?.Zdo == null || station.Root == null) return;
            LoadedStations[station.Zdo.m_uid] = station.Root;
            PersistentStations[station.Zdo.m_uid] = new PersistentStationRecord
            {
                Zdo = station.Zdo,
                Kind = station.Kind,
                Position = station.Position,
                DisplayName = station.DisplayName
            };
        }

        internal static bool TryFindPersistentDemand(Vector3 center, float radius, Vector3 from,
            Func<string, int> stockCounter, Func<string, bool> canRunOutput,
            out ZDO stationZdo, out ProductionStationKind stationKind, out ProductionDemand demand)
        {
            stationZdo = null;
            stationKind = ProductionStationKind.None;
            demand = default;
            if (stockCounter == null) return false;

            float r = Mathf.Max(6f, radius);
            float rSqr = r * r;
            float best = float.MaxValue;
            List<ZDOID> stale = null;
            foreach (KeyValuePair<ZDOID, PersistentStationRecord> pair in PersistentStations)
            {
                PersistentStationRecord record = pair.Value;
                if (record == null || record.Zdo == null)
                {
                    if (stale == null) stale = new List<ZDOID>();
                    stale.Add(pair.Key);
                    continue;
                }

                record.Position = record.Zdo.GetPosition();
                Vector3 zoneDelta = record.Position - center;
                zoneDelta.y = 0f;
                if (zoneDelta.sqrMagnitude > rSqr) continue;

                // A station has one authoritative next bill. Older code iterated every recipe
                // output and re-parsed the same ZDO bill list (plus re-counted storage) for each
                // recipe. A Food Preparation Table can expose dozens of recipes, turning one idle
                // worker probe into hundreds/thousands of container scans. Resolve the next demand
                // exactly once per station, then validate that single output.
                Func<string, bool> stationCanRun = output =>
                    SupportsOutput(record.Kind, output) && (canRunOutput == null || canRunOutput(output));
                if (!ProductionOrderRuntime.TryGetNextDemand(record.Zdo, stockCounter, stationCanRun, out ProductionDemand candidate) || candidate.Needed <= 0) continue;
                float score = (record.Position - from).sqrMagnitude;
                if (score >= best) continue;
                best = score;
                stationZdo = record.Zdo;
                stationKind = record.Kind;
                demand = candidate;
            }
            if (stale != null)
                for (int i = 0; i < stale.Count; i++) PersistentStations.Remove(stale[i]);
            return stationZdo != null && demand.Needed > 0;
        }

        internal static void RegisterLoaded(GameObject go)
        {
            if (go == null || !go.activeInHierarchy) return;
            if (TryResolveInternal(go, out ProductionStationRef station)) RememberLoaded(station);
        }

        private static void CollectRegisteredRoots()
        {
            StationQueryScratch.Clear();
            StationCleanupScratch.Clear();
            foreach (KeyValuePair<ZDOID, GameObject> pair in LoadedStations)
            {
                GameObject root = pair.Value;
                if (root == null || !root.activeInHierarchy)
                {
                    StationCleanupScratch.Add(pair.Key);
                    continue;
                }
                StationQueryScratch.Add(root);
            }
            for (int i = 0; i < StationCleanupScratch.Count; i++) LoadedStations.Remove(StationCleanupScratch[i]);
            StationCleanupScratch.Clear();
        }

        internal static bool TryFindDemand(Vector3 center, float radius, Vector3 from, Func<string, int> stockCounter, Func<string, bool> canRunOutput,
            out ProductionStationRef station, out ProductionDemand demand)
        {
            station = null;
            demand = default;
            if (stockCounter == null) return false;

            float best = float.MaxValue;
            float r = Mathf.Max(6f, radius);
            float rSqr = r * r;
            var seen = new HashSet<ZDO>();

            // Normal path: Smelter-based stations are lifecycle-indexed from Smelter.Awake, while
            // CraftingStation-based stations are remembered as soon as they are resolved by the
            // station UI or the throttled Workzone repair below. CraftingStation has no patchable
            // Awake method in the current Valheim build, so we deliberately do not patch it.
            CollectRegisteredRoots();
            for (int i = 0; i < StationQueryScratch.Count; i++)
                Consider(StationQueryScratch[i], center, rSqr, from, stockCounter, canRunOutput, seen, ref best, ref station, ref demand);
            if (station != null && demand.Needed > 0) return true;

            // Stream-repair path: if a station missed lifecycle registration, probe ONLY the active
            // Workzone and only at a throttled cadence. Never scan the whole loaded world. Any
            // resolved station is remembered so subsequent Baker ticks stay on the cheap index.
            float now = Time.unscaledTime;
            if (now >= _nextLocalRepairAt)
            {
                _nextLocalRepairAt = now + LocalRepairCooldownSeconds;
                int hits = 0;
                try { hits = Physics.OverlapSphereNonAlloc(center, r, ScanBuffer, ~0, QueryTriggerInteraction.Collide); }
                catch { hits = 0; }
                for (int i = 0; i < hits && i < ScanBuffer.Length; i++)
                {
                    Collider collider = ScanBuffer[i];
                    ScanBuffer[i] = null;
                    if (collider == null) continue;
                    Consider(collider.gameObject, center, rSqr, from, stockCounter, canRunOutput, seen, ref best, ref station, ref demand);
                }
            }
            return station != null && demand.Needed > 0;
        }

        internal static bool TryFindNear(string output, Vector3 targetPosition, float maxDistance, out ProductionStationRef station)
        {
            station = null;
            float distanceLimit = Mathf.Max(1f, maxDistance);
            float best = distanceLimit * distanceLimit;
            var seen = new HashSet<ZDO>();

            CollectRegisteredRoots();
            for (int i = 0; i < StationQueryScratch.Count; i++)
                ConsiderNear(StationQueryScratch[i], output, targetPosition, seen, ref best, ref station);
            if (station != null) return true;

            int hits = 0;
            try { hits = Physics.OverlapSphereNonAlloc(targetPosition, distanceLimit, ScanBuffer, ~0, QueryTriggerInteraction.Collide); }
            catch { hits = 0; }
            for (int i = 0; i < hits && i < ScanBuffer.Length; i++)
            {
                Collider collider = ScanBuffer[i];
                ScanBuffer[i] = null;
                if (collider == null) continue;
                ConsiderNear(collider.gameObject, output, targetPosition, seen, ref best, ref station);
            }
            return station != null;
        }

        internal static bool SupportsOutput(ProductionStationKind kind, string output)
        {
            string[] outputs = SupportedOutputs(kind);
            for (int i = 0; i < outputs.Length; i++)
                if (string.Equals(outputs[i], output, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void Consider(GameObject go, Vector3 center, float radiusSquared, Vector3 from, Func<string, int> stockCounter, Func<string, bool> canRunOutput,
            HashSet<ZDO> seen, ref float best, ref ProductionStationRef bestStation, ref ProductionDemand bestDemand)
        {
            if (go == null || !TryResolve(go, out ProductionStationRef candidate) || candidate.Zdo == null || !seen.Add(candidate.Zdo)) return;
            Vector3 zoneDelta = candidate.Position - center;
            zoneDelta.y = 0f;
            if (zoneDelta.sqrMagnitude > radiusSquared) return;

            // Resolve one next bill per station. Do not iterate every supported recipe and run
            // the same order/storage query again for each output.
            Func<string, bool> stationCanRun = output =>
                SupportsOutput(candidate.Kind, output) && (canRunOutput == null || canRunOutput(output));
            if (!ProductionOrderRuntime.TryGetNextDemand(candidate.Zdo, stockCounter, stationCanRun, out ProductionDemand candidateDemand) || candidateDemand.Needed <= 0) return;

            float distance = (candidate.Position - from).sqrMagnitude;
            if (distance >= best) return;
            best = distance;
            bestStation = candidate;
            bestDemand = candidateDemand;
        }

        private static void ConsiderNear(GameObject go, string output, Vector3 targetPosition, HashSet<ZDO> seen,
            ref float best, ref ProductionStationRef bestStation)
        {
            if (go == null || !TryResolve(go, out ProductionStationRef candidate) || candidate.Zdo == null || !seen.Add(candidate.Zdo)) return;
            if (!SupportsOutput(candidate.Kind, output)) return;
            float distance = (candidate.Position - targetPosition).sqrMagnitude;
            if (distance >= best) return;
            best = distance;
            bestStation = candidate;
        }

        private static bool TryResolveComponent(GameObject go, out ProductionStationRef station)
        {
            station = null;
            if (go == null) return false;
            ProductionStationKind kind = Classify(go);
            if (kind == ProductionStationKind.None)
            {
                Transform current = go.transform.parent;
                for (int depth = 0; current != null && depth < 8 && kind == ProductionStationKind.None; depth++, current = current.parent)
                    kind = Classify(current.gameObject);
            }
            if (kind == ProductionStationKind.None) return false;

            ZNetView view = go.GetComponent<ZNetView>() ?? go.GetComponentInParent<ZNetView>() ?? go.GetComponentInChildren<ZNetView>();
            if (view == null || !view.IsValid() || view.GetZDO() == null) return false;
            station = new ProductionStationRef
            {
                Root = go,
                View = view,
                Zdo = view.GetZDO(),
                Kind = kind,
                Position = go.transform.position,
                DisplayName = DisplayName(kind)
            };
            return true;
        }

        private static ProductionStationKind Classify(GameObject go)
        {
            if (go == null) return ProductionStationKind.None;
            string name = SpeciesRoleRegistry.NormalizePrefabName(go.name).ToLowerInvariant();
            if (name.Contains("windmill")) return ProductionStationKind.Windmill;
            if (name == "piece_oven" || name.Contains("stoneoven") || name.Contains("stone_oven")) return ProductionStationKind.Oven;
            if (name.Contains("blastfurnace") || name.Contains("blast_furnace")) return ProductionStationKind.BlastFurnace;
            if (name == "smelter" || name.Contains("smelter")) return ProductionStationKind.Smelter;
            if (name.Contains("cauldron")) return ProductionStationKind.Cauldron;
            if (name.Contains("foodprep") || name.Contains("food_prep") ||
                name.Contains("preptable") || name.Contains("prep_table") || name.Contains("preparation"))
                return ProductionStationKind.Preparation;
            return ProductionStationKind.None;
        }
    }

    [HarmonyPatch(typeof(Smelter), "Awake")]
    internal static class ProductionStationSmelterIndexPatch
    {
        private static void Postfix(Smelter __instance)
        {
            if (__instance == null) return;
            ProductionStationRuntime.RegisterLoaded(__instance.gameObject);
            FirekeeperWorkRuntime.RegisterLoadedSmelter(__instance);
        }
    }

    [HarmonyPatch(typeof(Player), "Interact", typeof(GameObject), typeof(bool), typeof(bool))]
    internal static class ProductionStationPlayerInteractPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Player __instance, GameObject go, bool hold, bool alt)
        {
            try
            {
                if (__instance == null || __instance != Player.m_localPlayer || go == null || hold) return true;
                if (WorkersModalUiRuntime.Active) return true;
                if (!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift)) return true;
                if (!ProductionStationRuntime.TryResolve(go, out ProductionStationRef station)) return true;

                // v0.5.350: every supported production station exposes the same Shift+E target
                // menu. Preparation/Cauldron keep their inline craft-window control as a quick
                // shortcut, but Shift+E is now the authoritative full station overview too.
                Plugin plugin = Plugin.Instance;
                if (plugin == null) return true;
                return !plugin.TryOpenProductionStationMenu(go, __instance);
            }
            catch
            {
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(Switch), "GetHoverText")]
    internal static class ProductionStationHoverPatch
    {
        private static void Postfix(Switch __instance, ref string __result)
        {
            try
            {
                if (__instance == null || Plugin.Instance == null) return;
                if (!ProductionStationRuntime.TryResolve(__instance.gameObject, out ProductionStationRef station)) return;
                string hint = $"[<color=yellow><b>Shift+E</b></color>] {station.DisplayName}: Produktionsziele";
                if (string.IsNullOrEmpty(__result)) __result = hint;
                else if (__result.IndexOf("Produktionsziele", StringComparison.OrdinalIgnoreCase) < 0) __result += "\n" + hint;
            }
            catch { }
        }
    }
}
