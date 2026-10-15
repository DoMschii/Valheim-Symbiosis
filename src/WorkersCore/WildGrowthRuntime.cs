using System;

namespace Valheim_Workers
{
    /// <summary>
    /// Species-exclusive Greydwarf Shaman planting path for persistent wild flora.
    /// WildGrowth deliberately bypasses vegetable seed-production policy: the exact harvested
    /// wild item is the planting resource. v0.5.314 uses Workers-owned VW_Flora_* prefabs;
    /// SmartReplant is intentionally not part of this execution path.
    /// </summary>
    internal static class WildGrowthRuntime
    {
        internal static int LastPendingCount { get; private set; }
        internal static int LastResolvedCount { get; private set; }
        internal static int LastMissingResourceCount { get; private set; }
        internal static string LastStatus { get; private set; } = "idle";

        internal static void BeginEvaluation()
        {
            LastPendingCount = 0;
            LastResolvedCount = 0;
            LastMissingResourceCount = 0;
            LastStatus = "idle";
        }

        internal static bool IsWildGrowthSlot(GardenReplantSlot slot) =>
            GardenerTaskFilterRuntime.IsWildGrowthIdentity(slot.HarvestPrefab, null, null);

        internal static bool TryBuildPlan(GardenReplantSlot slot, out WorkerGardenPlan plan)
        {
            plan = default;
            if (!IsWildGrowthSlot(slot)) return false;
            LastPendingCount++;

            // v0.5.316: WildGrowth is fully Workers-owned. Legacy world slots created by old
            // SmartReplant-compatible builds are canonicalized directly to VW_Flora_* and must
            // never fall back through GardenWorkRuntime's SmartReplant recipe registry.
            if (WorkersWildFloraRuntime.TryCanonicalizeLegacyIdentity(slot.HarvestPrefab, out WorkersWildFloraRuntime.Definition def))
            {
                // Use the registered Workers clone as the physical plant. v0.5.325 equips that
                // clone with explicit ZNet persistence and a harvest-visual guard, so the bush
                // body survives both world reloads and RPC_Pick while the slot identity stays exact.
                plan = new WorkerGardenPlan(slot.SlotId, def.PrefabName, def.PrefabName, def.ResourcePrefab,
                    def.Cost, def.Spacing, slot.Position, false, 0, 0, slot.RotationY);
                LastResolvedCount++;
                LastStatus = $"resolved#{slot.SlotId}:{def.ResourcePrefab}->{def.PrefabName}";
                return true;
            }

            LastStatus = $"unresolved#{slot.SlotId}:{slot.HarvestPrefab}";
            return false;
        }

        internal static void MarkMissingResource(WorkerGardenPlan plan)
        {
            LastMissingResourceCount++;
            LastStatus = $"needs-resource#{plan.SlotId}:{plan.ResourcePrefab}x{plan.ResourceAmount}";
        }

        internal static void MarkSelected(WorkerGardenPlan plan)
        {
            LastStatus = $"selected#{plan.SlotId}:{plan.ResourcePrefab}->{plan.PlantPrefab}";
        }

        private static string CanonicalResource(string identity, string resolved)
        {
            string value = GardenWorkRuntime.NormalizePrefabName(identity ?? string.Empty);
            if (value.IndexOf("Raspberry", StringComparison.OrdinalIgnoreCase) >= 0) return "Raspberry";
            if (value.IndexOf("Blueberr", StringComparison.OrdinalIgnoreCase) >= 0) return "Blueberries";
            if (value.IndexOf("Cloudberr", StringComparison.OrdinalIgnoreCase) >= 0) return "Cloudberry";
            if (value.IndexOf("Thistle", StringComparison.OrdinalIgnoreCase) >= 0) return "Thistle";
            if (value.IndexOf("Dandelion", StringComparison.OrdinalIgnoreCase) >= 0) return "Dandelion";
            if (value.IndexOf("MushroomBlue", StringComparison.OrdinalIgnoreCase) >= 0) return "MushroomBlue";
            if (value.IndexOf("MushroomYellow", StringComparison.OrdinalIgnoreCase) >= 0) return "MushroomYellow";
            if (value.IndexOf("Mushroom", StringComparison.OrdinalIgnoreCase) >= 0) return "Mushroom";
            return GardenWorkRuntime.NormalizePrefabName(resolved ?? string.Empty);
        }

        internal static string GetDebugSummary() =>
            $"pending={LastPendingCount}; resolved={LastResolvedCount}; missingResource={LastMissingResourceCount}; status={LastStatus}";
    }
}
