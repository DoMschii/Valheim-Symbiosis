using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// v0.5.132: demand gate shared by physical/background producers.
    /// A configured Min/Max rule is interpreted as an owner-wide demand signal for that item:
    /// - no configured rule => legacy/unlimited production
    /// - at least one configured destination can accept the item => production allowed
    /// - configured destinations exist but all are paused/full => production blocked until Min resumes it
    ///
    /// Decisions are cached briefly so target scans never walk the persistent container registry once
    /// per tree/rock/drop candidate.
    /// </summary>
    internal static class ProductionDemandRuntime
    {
        internal enum DemandState
        {
            Unmanaged = 0,
            Wanted = 1,
            Blocked = 2
        }

        private sealed class CacheEntry
        {
            internal DemandState State;
            internal float ValidUntil;
        }

        private static readonly Dictionary<string, CacheEntry> Cache =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        private const float CacheSeconds = 1.25f;

        internal static void InvalidateAll()
        {
            Cache.Clear();
        }

        internal static bool AllowsPrefab(ZDO workerZdo, string prefab)
        {
            if (workerZdo == null || string.IsNullOrWhiteSpace(prefab)) return true;
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            return GetState(ownerId, prefab) != DemandState.Blocked;
        }

        internal static DemandState GetState(ZDO workerZdo, string prefab)
        {
            if (workerZdo == null || string.IsNullOrWhiteSpace(prefab)) return DemandState.Unmanaged;
            return GetState(workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L), prefab);
        }

        internal static DemandState GetState(long ownerId, string prefab)
        {
            string normalized = NormalizePrefab(prefab);
            if (string.IsNullOrEmpty(normalized)) return DemandState.Unmanaged;

            string key = ownerId.ToString() + "|" + normalized;
            float now = Time.realtimeSinceStartup;
            if (Cache.TryGetValue(key, out CacheEntry cached) && cached != null && now < cached.ValidUntil)
                return cached.State;

            bool configured = false;
            bool wanted = false;
            PersistentContainerRegistryRuntime.ForEachPersistent((zdo, role, owner, position) =>
            {
                if (wanted || zdo == null) return;
                if (ownerId != 0L && owner != 0L && owner != ownerId) return;
                if (!ContainerStockLimitRuntime.SupportsStockLimits(zdo)) return;
                if (!ContainerStockLimitRuntime.HasRule(zdo, normalized)) return;

                configured = true;
                if (ContainerStockLimitRuntime.GetPersistentAcceptCapacity(zdo, normalized) > 0)
                    wanted = true;
            });

            DemandState state = !configured
                ? DemandState.Unmanaged
                : wanted ? DemandState.Wanted : DemandState.Blocked;

            Cache[key] = new CacheEntry { State = state, ValidUntil = now + CacheSeconds };
            return state;
        }

        /// <summary>
        /// Multi-output producers (trees, rocks, prey) must not create a configured by-product
        /// that is already at its Max. Unmanaged by-products do not force production by themselves.
        /// </summary>
        internal static bool AllowsOutputs(ZDO workerZdo, params string[] prefabs)
        {
            if (workerZdo == null || prefabs == null || prefabs.Length == 0) return true;
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            for (int i = 0; i < prefabs.Length; i++)
            {
                string prefab = NormalizePrefab(prefabs[i]);
                if (string.IsNullOrEmpty(prefab)) continue;
                if (GetState(ownerId, prefab) == DemandState.Blocked) return false;
            }
            return true;
        }

        internal static bool AllowsWoodTarget(ZDO workerZdo, WoodTarget target)
        {
            if (workerZdo == null || target == null || target.GameObject == null) return true;
            // Finish already-felled logs/stumps even if Max is reached mid-cycle. The demand gate
            // only prevents starting another standing tree, avoiding abandoned half-finished trees.
            if (target.Kind != WoodTargetKind.TreeBase) return true;
            string key = LumberjackTreeFilterRuntime.ResolveSpeciesKey(target.GameObject.name);
            switch ((key ?? string.Empty).ToLowerInvariant())
            {
                case "pine":
                    return AllowsOutputs(workerZdo, "Wood", "RoundLog");
                case "birch":
                case "oak":
                    return AllowsOutputs(workerZdo, "Wood", "FineWood");
                case "ancient":
                    return AllowsOutputs(workerZdo, "Wood", "ElderBark");
                case "yggdrasil":
                    return AllowsOutputs(workerZdo, "YggdrasilWood");
                case "ash":
                    return AllowsOutputs(workerZdo, "Blackwood");
                case "beech":
                case "fir":
                    return AllowsOutputs(workerZdo, "Wood");
                default:
                    // Unknown/modded wood stays legacy-safe rather than being incorrectly blocked.
                    return true;
            }
        }

        internal static bool AllowsStoneTarget(ZDO workerZdo, StoneTarget target)
        {
            if (workerZdo == null || target == null) return true;
            DropTable table = null;
            if (target.Rock5 != null) table = target.Rock5.m_dropItems;
            else if (target.Rock != null) table = target.Rock.m_dropItems;
            else if (target.Destructible != null) table = StoneTargetFinder.GetDestructibleDropTable(target.Destructible);

            if (table == null || table.m_drops == null || table.m_drops.Count == 0) return true;
            for (int i = 0; i < table.m_drops.Count; i++)
            {
                GameObject item = table.m_drops[i].m_item;
                if (item == null) continue;
                string prefab = NormalizePrefab(item.name);
                if (string.IsNullOrEmpty(prefab)) continue;
                if (GetState(workerZdo, prefab) == DemandState.Blocked) return false;
            }
            return true;
        }

        internal static bool AllowsHarvestTarget(ZDO workerZdo, Pickable pickable)
        {
            if (workerZdo == null || pickable == null) return true;
            string prefab = pickable.m_itemPrefab != null ? NormalizePrefab(pickable.m_itemPrefab.name) : string.Empty;
            if (string.IsNullOrEmpty(prefab))
            {
                string harvest = GardenHarvestTargetFinder.GetIdentityName(pickable);
                GardenWorkRuntime.TryResolveGatherableItemPrefab(harvest, out prefab);
            }
            return string.IsNullOrEmpty(prefab) || AllowsPrefab(workerZdo, prefab);
        }

        internal static bool AllowsGardenPlan(ZDO workerZdo, WorkerGardenPlan plan)
        {
            if (workerZdo == null) return true;
            string output = plan.HarvestPrefab;
            if (GardenWorkRuntime.TryResolveGatherableItemPrefab(output, out string resolved) && !string.IsNullOrEmpty(resolved))
                output = resolved;
            return string.IsNullOrEmpty(output) || AllowsPrefab(workerZdo, output);
        }

        internal static bool AllowsHunterPrey(ZDO workerZdo, string preyPrefab)
        {
            string prey = HunterPreyFilterRuntime.ResolvePreyKey(preyPrefab);
            switch (prey)
            {
                case "deer": return AllowsOutputs(workerZdo, "DeerMeat", "DeerHide");
                case "boar": return AllowsOutputs(workerZdo, "RawMeat", "LeatherScraps");
                case "neck": return AllowsOutputs(workerZdo, "NeckTail");
                case "leech": return AllowsOutputs(workerZdo, "Bloodbag");
                case "swamp_slime": return AllowsOutputs(workerZdo, "Ooze");
                default: return true;
            }
        }

        internal static string Describe(ZDO workerZdo, string prefab)
        {
            DemandState state = GetState(workerZdo, prefab);
            switch (state)
            {
                case DemandState.Wanted: return "BEDARF";
                case DemandState.Blocked: return "MAX/PAUSE";
                default: return "UNGEREGELT";
            }
        }

        private static string NormalizePrefab(string prefab)
        {
            if (string.IsNullOrWhiteSpace(prefab)) return string.Empty;
            string value = prefab.Trim();
            int clone = value.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase);
            if (clone >= 0) value = value.Substring(0, clone).Trim();
            return value;
        }
    }
}
