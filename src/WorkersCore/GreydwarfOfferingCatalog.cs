using System;
using System.Collections.Generic;
using System.Reflection;

namespace Valheim_Workers
{
    internal enum GreydwarfNestOfferingKind
    {
        None = 0,
        Gatherer = 1,
        Hauler = 2,
        Berserker = 3,
        Shaman = 4
    }

    internal sealed class GreydwarfOfferingDefinition
    {
        internal readonly string PrefabName;
        internal readonly int Progress;
        internal readonly string DisplayName;
        internal readonly GreydwarfNestOfferingKind Kind;

        internal GreydwarfOfferingDefinition(string prefabName, int progress, string displayName, GreydwarfNestOfferingKind kind)
        {
            PrefabName = prefabName;
            Progress = progress;
            DisplayName = displayName;
            Kind = kind;
        }
    }

    internal static class GreydwarfOfferingCatalog
    {
        private const int ProgressPerOffering = 5;

        private static readonly MethodInfo InventoryChangedTwoBools = typeof(Inventory).GetMethod(
            "Changed",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(bool), typeof(bool) },
            modifiers: null);

        private static readonly MethodInfo InventoryChangedNoArgs = typeof(Inventory).GetMethod(
            "Changed",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

        private static readonly MethodInfo InventoryAddItemItemData = typeof(Inventory).GetMethod(
            "AddItem",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(ItemDrop.ItemData) },
            modifiers: null);

        internal static bool TryClassifyOffering(ItemDrop.ItemData item, out GreydwarfOfferingDefinition offering)
        {
            offering = null;
            if (item == null || item.m_stack <= 0 || item.m_dropPrefab == null) return false;

            string prefab = SpeciesRoleRegistry.NormalizePrefabName(item.m_dropPrefab.name);
            if (string.IsNullOrEmpty(prefab) || string.Equals(prefab, GreydwarfNestSummoningRuntime.AncientSeedPrefab, StringComparison.OrdinalIgnoreCase))
                return false;

            // v0.5.403: the four offerings are intentionally exact. They are both the
            // creature's thematic favourite and the permanent weight used by the nest.
            if (string.Equals(prefab, "Raspberry", StringComparison.OrdinalIgnoreCase))
            {
                offering = new GreydwarfOfferingDefinition(prefab, ProgressPerOffering, "Himbeere -> Grauling", GreydwarfNestOfferingKind.Gatherer);
                return true;
            }

            if (string.Equals(prefab, "Blueberries", StringComparison.OrdinalIgnoreCase))
            {
                offering = new GreydwarfOfferingDefinition(prefab, ProgressPerOffering, "Blaubeere -> Grauzwerg", GreydwarfNestOfferingKind.Hauler);
                return true;
            }

            if (string.Equals(prefab, "Thistle", StringComparison.OrdinalIgnoreCase))
            {
                offering = new GreydwarfOfferingDefinition(prefab, ProgressPerOffering, "Distel -> Schamane", GreydwarfNestOfferingKind.Shaman);
                return true;
            }

            if (string.Equals(prefab, "Dandelion", StringComparison.OrdinalIgnoreCase))
            {
                offering = new GreydwarfOfferingDefinition(prefab, ProgressPerOffering, "Loewenzahn -> Berserker", GreydwarfNestOfferingKind.Berserker);
                return true;
            }

            return false;
        }

        internal static bool TryFindOffering(Inventory inventory, out ItemDrop.ItemData item, out GreydwarfOfferingDefinition offering)
        {
            item = null;
            offering = null;
            if (inventory == null) return false;

            var items = inventory.GetAllItems();
            if (items == null || items.Count == 0) return false;

            // E is only a convenience path. Directly using an item on the nest remains the
            // precise way to choose which permanent summon weight receives the donation.
            for (int priority = 1; priority <= 4; priority++)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    ItemDrop.ItemData candidate = items[i];
                    if (!TryClassifyOffering(candidate, out GreydwarfOfferingDefinition classified)) continue;
                    int candidatePriority = classified.Kind == GreydwarfNestOfferingKind.Gatherer ? 1 :
                                            classified.Kind == GreydwarfNestOfferingKind.Hauler ? 2 :
                                            classified.Kind == GreydwarfNestOfferingKind.Shaman ? 3 : 4;
                    if (candidatePriority != priority) continue;
                    item = candidate;
                    offering = classified;
                    return true;
                }
            }

            return false;
        }

        internal static int GetMaxUsefulItemCount(ZDO zdo, GreydwarfOfferingDefinition offering, int requested)
        {
            if (zdo == null || offering == null || requested <= 0) return 0;
            int required = Math.Max(1, SpawnerRecruitmentZdo.ReadRequired(zdo));
            int remaining = Math.Max(0, required - SpawnerRecruitmentZdo.ReadProgress(zdo));
            if (remaining <= 0) return 0;
            int perItem = Math.Max(1, offering.Progress);
            int needed = (remaining + perItem - 1) / perItem;
            return Math.Min(requested, needed);
        }

        internal static bool TryConsumeOne(Inventory inventory, ItemDrop.ItemData item, Action<string> debugLog)
        {
            return TryConsumeAmount(inventory, item, 1, debugLog);
        }

        internal static bool TryConsumeAmount(Inventory inventory, ItemDrop.ItemData item, int amount, Action<string> debugLog)
        {
            if (inventory == null || item == null || item.m_stack <= 0 || amount <= 0) return false;
            amount = Math.Min(amount, item.m_stack);

            if (amount >= item.m_stack)
            {
                inventory.RemoveItem(item);
                return true;
            }

            if (InventoryChangedTwoBools == null && InventoryChangedNoArgs == null)
            {
                debugLog?.Invoke("Offering inventory update blocked: Inventory.Changed signature was not found.");
                return false;
            }

            item.m_stack -= amount;
            if (NotifyChanged(inventory, debugLog)) return true;

            item.m_stack += amount;
            return false;
        }

        internal static bool TryRestoreConsumed(Inventory inventory, ItemDrop.ItemData item, int originalStack, Action<string> debugLog)
        {
            if (inventory == null || item == null || originalStack <= 0) return false;

            if (originalStack > 1)
            {
                item.m_stack = originalStack;
                return NotifyChanged(inventory, debugLog);
            }

            if (InventoryAddItemItemData == null)
            {
                debugLog?.Invoke("Offering refund blocked: Inventory.AddItem(ItemData) signature was not found.");
                return false;
            }

            try
            {
                item.m_stack = 1;
                object result = InventoryAddItemItemData.Invoke(inventory, new object[] { item });
                return result is bool added && added;
            }
            catch (Exception ex)
            {
                debugLog?.Invoke($"Offering refund failed. {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static bool NotifyChanged(Inventory inventory, Action<string> debugLog)
        {
            try
            {
                if (InventoryChangedTwoBools != null)
                    InventoryChangedTwoBools.Invoke(inventory, new object[] { false, false });
                else if (InventoryChangedNoArgs != null)
                    InventoryChangedNoArgs.Invoke(inventory, null);
                else
                    return false;
                return true;
            }
            catch (Exception ex)
            {
                debugLog?.Invoke($"Offering inventory update failed. {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        internal static string GetDisplayName(ItemDrop.ItemData item, GreydwarfOfferingDefinition offering)
        {
            return offering?.DisplayName ?? offering?.PrefabName ?? "Spende";
        }

        internal static string DescribeAcceptedOfferings() =>
            "Himbeere -> Grauling | Blaubeere -> Grauzwerg | Distel -> Schamane | Loewenzahn -> Berserker (je +5)";
    }
}
