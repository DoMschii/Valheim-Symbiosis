using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Lightweight Palworld-style hunter simulation for unloaded/out-of-bubble workers.
    /// No Character/AI/pathfinding is touched. Time is converted into deterministic hunting
    /// batches and the resulting resources are staged on the Workzone FieldDepot. The physical
    /// Hunter remains authoritative while the player is inside the simulation bubble.
    /// </summary>
    internal static class BackgroundHunterRuntime
    {
        private const string ProgressKey = "vw_bg_hunter_progress";
        private const string CursorKey = "vw_bg_hunter_cursor";
        private const float SecondsPerHunt = 45f;

        private readonly struct LootEntry
        {
            internal readonly string PreyKey;
            internal readonly string PreyPrefab;
            internal readonly string[] Prefabs;
            internal readonly int[] Amounts;

            internal LootEntry(string preyKey, string preyPrefab, string[] prefabs, int[] amounts)
            {
                PreyKey = preyKey;
                PreyPrefab = preyPrefab;
                Prefabs = prefabs;
                Amounts = amounts;
            }
        }

        private static readonly List<LootEntry> AllowedLootScratch = new List<LootEntry>(8);

        private static readonly LootEntry[] Loot =
        {
            new LootEntry("deer", "Deer", new[] { "DeerMeat", "DeerHide" }, new[] { 2, 2 }),
            new LootEntry("boar", "Boar", new[] { "RawMeat", "LeatherScraps" }, new[] { 2, 2 }),
            new LootEntry("neck", "Neck", new[] { "NeckTail" }, new[] { 1 }),
            new LootEntry("leech", "Leech", new[] { "Bloodbag" }, new[] { 1 }),
            new LootEntry("swamp_slime", "Blob", new[] { "Ooze" }, new[] { 2 })
        };

        internal static bool HasBackgroundWork(ZDO workerZdo)
        {
            if (workerZdo == null) return false;
            if (!WorkerLogisticsRuntime.TryFindPersistentFieldDepotForWorkArea(workerZdo, out ZDO depotZdo, out _) || depotZdo == null)
                return false;

            for (int i = 0; i < Loot.Length; i++)
            {
                HunterPreyOption option = FindOption(Loot[i].PreyKey);
                if (string.IsNullOrEmpty(option.Key)) continue;
                if (!HunterPreyFilterRuntime.IsOptionAvailableForWorker(workerZdo, option)) continue;
                if (!HunterPreyFilterRuntime.IsPreyAllowed(workerZdo, Loot[i].PreyPrefab)) continue;
                if (!ProductionDemandRuntime.AllowsHunterPrey(workerZdo, Loot[i].PreyPrefab)) continue;
                if (CanStage(depotZdo, Loot[i])) return true;
            }

            return false;
        }

        internal static string TickBackground(ZDO workerZdo, double deltaSeconds, float efficiency)
        {
            if (workerZdo == null) return "hunting:no-worker";
            if (!WorkerLogisticsRuntime.TryFindPersistentFieldDepotForWorkArea(workerZdo, out ZDO depotZdo, out _ ) || depotZdo == null)
                return "hunting:waiting-no-field-depot";

            AllowedLootScratch.Clear();
            for (int i = 0; i < Loot.Length; i++)
            {
                HunterPreyOption option = FindOption(Loot[i].PreyKey);
                if (string.IsNullOrEmpty(option.Key)) continue;
                if (!HunterPreyFilterRuntime.IsOptionAvailableForWorker(workerZdo, option)) continue;
                if (!HunterPreyFilterRuntime.IsPreyAllowed(workerZdo, Loot[i].PreyPrefab)) continue;
                if (!ProductionDemandRuntime.AllowsHunterPrey(workerZdo, Loot[i].PreyPrefab)) continue;
                AllowedLootScratch.Add(Loot[i]);
            }
            if (AllowedLootScratch.Count == 0)
            {
                workerZdo.Set(ProgressKey, 0f);
                return "hunting:waiting-no-allowed-prey-or-stock-max";
            }

            float progress = Mathf.Max(0f, workerZdo.GetFloat(ProgressKey, 0f));
            progress += (float)Math.Max(0.0, deltaSeconds) * Mathf.Clamp(efficiency, 0.05f, 1f);
            int completed = Mathf.FloorToInt(progress / SecondsPerHunt);
            if (completed <= 0)
            {
                workerZdo.Set(ProgressKey, progress);
                return $"hunting:tracking {Mathf.Clamp01(progress / SecondsPerHunt) * 100f:0}%";
            }

            int cursor = Math.Abs(workerZdo.GetInt(CursorKey, 0));
            int deliveredHunts = 0;
            for (int h = 0; h < completed; h++)
            {
                LootEntry entry = AllowedLootScratch[cursor % AllowedLootScratch.Count];
                if (!CanStage(depotZdo, entry)) break;
                for (int j = 0; j < entry.Prefabs.Length && j < entry.Amounts.Length; j++)
                    AdvancedFieldDepotRuntime.QueueBackgroundDelivery(depotZdo, entry.Prefabs[j], entry.Amounts[j]);
                cursor++;
                deliveredHunts++;
            }

            if (deliveredHunts > 0)
            {
                progress -= deliveredHunts * SecondsPerHunt;
                workerZdo.Set(CursorKey, cursor);
            }
            workerZdo.Set(ProgressKey, Mathf.Min(progress, SecondsPerHunt * 2f));
            return deliveredHunts > 0
                ? $"hunting:simulated {deliveredHunts} hunt(s) -> field depot"
                : "hunting:waiting-field-depot-full";
        }

        internal static void ClearCaches()
        {
            AllowedLootScratch.Clear();
        }

        private static HunterPreyOption FindOption(string key)
        {
            for (int i = 0; i < HunterPreyFilterRuntime.Options.Length; i++)
            {
                HunterPreyOption option = HunterPreyFilterRuntime.Options[i];
                if (string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase)) return option;
            }
            return default;
        }

        private static bool CanStage(ZDO depotZdo, LootEntry entry)
        {
            for (int i = 0; i < entry.Prefabs.Length && i < entry.Amounts.Length; i++)
            {
                string prefab = entry.Prefabs[i];
                int amount = Math.Max(0, entry.Amounts[i]);
                if (amount <= 0) continue;
                if (!ContainerLedgerRuntime.TryGetPersistentFreeCapacity(depotZdo, prefab, out int capacity)) continue;
                int pending = AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(depotZdo, prefab);
                if (Math.Max(0, capacity - pending) < amount) return false;
            }
            return true;
        }
    }
}
