using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    internal readonly struct WorkerCargoLimits
    {
        internal readonly int Slots;
        internal readonly float Weight;
        internal WorkerCargoLimits(int slots, float weight) { Slots = Mathf.Max(1, slots); Weight = Mathf.Max(1f, weight); }
    }

    internal static class WorkerCargoRuntime
    {
        internal const int InventorySlots = 2;

        internal static WorkerCargoLimits GetLimits(Character worker)
        {
            // v0.5.65: every Worker exposes the same deterministic two-slot cargo inventory.
            // Carry weight is informational only; slot count is the authoritative capacity.
            return new WorkerCargoLimits(InventorySlots, 999999f);
        }

        internal static int GetUsedSlots(ZDO zdo)
        {
            int slots = 0;
            List<WorkerZdo.CargoStack> cargo = WorkerZdo.ReadCargoStacks(zdo);
            for (int i = 0; i < cargo.Count; i++)
            {
                ResolveItem(cargo[i].Prefab, out int maxStack, out _);
                slots += Mathf.CeilToInt(cargo[i].Amount / (float)Mathf.Max(1, maxStack));
            }
            return slots;
        }

        internal static float GetWeight(ZDO zdo)
        {
            float weight = 0f;
            List<WorkerZdo.CargoStack> cargo = WorkerZdo.ReadCargoStacks(zdo);
            for (int i = 0; i < cargo.Count; i++)
            {
                ResolveItem(cargo[i].Prefab, out _, out float itemWeight);
                weight += cargo[i].Amount * Mathf.Max(0f, itemWeight);
            }
            return weight;
        }


        internal static int GetMaxSinglePrefabLoad(Character worker, string prefabName, int available = int.MaxValue)
        {
            if (worker == null || string.IsNullOrEmpty(prefabName) || available <= 0) return 0;
            WorkerCargoLimits limits = GetLimits(worker);
            ResolveItem(prefabName, out int maxStack, out float itemWeight);
            maxStack = Mathf.Max(1, maxStack);
            long bySlotsLong = (long)limits.Slots * maxStack;
            int bySlots = bySlotsLong > int.MaxValue ? int.MaxValue : (int)bySlotsLong;
            return Mathf.Max(0, Math.Min(available, bySlots));
        }

        internal static int GetMaxAddAmount(Character worker, ZDO zdo, string prefabName, int available)
        {
            if (worker == null) return 0;
            return GetMaxAddAmount(zdo, prefabName, available);
        }

        // v0.5.389: background AirHauler simulation needs the same deterministic two-slot
        // calculation even when the Character GameObject is unloaded. All Workers share the
        // same slot limit, so the persistent ZDO is sufficient here.
        internal static int GetMaxAddAmount(ZDO zdo, string prefabName, int available)
        {
            if (zdo == null || string.IsNullOrEmpty(prefabName) || available <= 0) return 0;
            ResolveItem(prefabName, out int maxStack, out _);
            maxStack = Mathf.Max(1, maxStack);

            int currentAmount = WorkerZdo.GetCargoAmount(zdo, prefabName);
            int usedSlots = GetUsedSlots(zdo);
            int partialRoom = currentAmount > 0 && currentAmount % maxStack != 0 ? maxStack - (currentAmount % maxStack) : 0;
            int freeSlots = Mathf.Max(0, InventorySlots - usedSlots);
            long slotUnits = (long)partialRoom + (long)freeSlots * maxStack;
            int bySlots = Math.Min(available, slotUnits > int.MaxValue ? int.MaxValue : (int)slotUnits);

            return Mathf.Max(0, Math.Min(available, bySlots));
        }

        internal static bool TryAdd(Character worker, ZDO zdo, string prefabName, int available, out int accepted)
        {
            accepted = GetMaxAddAmount(worker, zdo, prefabName, available);
            if (accepted <= 0) return false;
            WorkerZdo.AddCargoAmount(zdo, prefabName, accepted);
            return true;
        }

        internal static bool IsFull(Character worker, ZDO zdo)
        {
            return GetUsedSlots(zdo) >= InventorySlots;
        }

        internal static string GetSummary(Character worker, ZDO zdo)
        {
            return $"{GetUsedSlots(zdo)}/{InventorySlots} Slots";
        }

        internal static List<WorkerZdo.CargoStack> ReadInventorySlots(ZDO zdo)
        {
            List<WorkerZdo.CargoStack> slots = new List<WorkerZdo.CargoStack>(InventorySlots);
            List<WorkerZdo.CargoStack> cargo = WorkerZdo.ReadCargoStacks(zdo);
            for (int i = 0; i < cargo.Count; i++)
            {
                WorkerZdo.CargoStack stack = cargo[i];
                if (stack.Amount <= 0 || string.IsNullOrEmpty(stack.Prefab)) continue;
                ResolveItem(stack.Prefab, out int maxStack, out _);
                maxStack = Mathf.Max(1, maxStack);
                int remaining = stack.Amount;
                while (remaining > 0)
                {
                    int amount = Mathf.Min(maxStack, remaining);
                    slots.Add(new WorkerZdo.CargoStack(stack.Prefab, amount));
                    remaining -= amount;
                }
            }
            return slots;
        }

        internal static void EnsureTwoSlotInventory(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return;

            if (WorkerZdo.TryReadCargoDropPending(zdo, out string pendingReason))
            {
                if (!WorkerZdo.HasCargo(zdo))
                {
                    WorkerZdo.ClearCargoDropPending(zdo);
                }
                else if (DropAllCargo(worker, zdo, string.IsNullOrEmpty(pendingReason) ? "job changed" : pendingReason))
                {
                    WorkerZdo.ClearCargoDropPending(zdo);
                }
                return;
            }

            if (GetUsedSlots(zdo) <= InventorySlots) return;

            List<WorkerZdo.CargoStack> original = WorkerZdo.ReadCargoStacks(zdo);
            List<WorkerZdo.CargoStack> kept = new List<WorkerZdo.CargoStack>();
            List<WorkerZdo.CargoStack> overflow = new List<WorkerZdo.CargoStack>();
            int slotsLeft = InventorySlots;

            for (int i = 0; i < original.Count; i++)
            {
                WorkerZdo.CargoStack stack = original[i];
                if (stack.Amount <= 0 || string.IsNullOrEmpty(stack.Prefab)) continue;
                ResolveItem(stack.Prefab, out int maxStack, out _);
                maxStack = Mathf.Max(1, maxStack);
                int keepAmount = Mathf.Min(stack.Amount, slotsLeft * maxStack);
                if (keepAmount > 0)
                {
                    kept.Add(new WorkerZdo.CargoStack(stack.Prefab, keepAmount));
                    slotsLeft -= Mathf.CeilToInt(keepAmount / (float)maxStack);
                }
                int extra = stack.Amount - keepAmount;
                if (extra > 0) overflow.Add(new WorkerZdo.CargoStack(stack.Prefab, extra));
            }

            if (overflow.Count == 0)
            {
                WorkerZdo.WriteCargoStacks(zdo, kept);
                return;
            }

            // Fail closed: only trim persistent cargo after every overflow stack was materialized.
            if (!TryDropStacks(worker, overflow)) return;
            WorkerZdo.WriteCargoStacks(zdo, kept);
            if (!WorkerZdo.HasCargo(zdo)) WorkerZdo.ClearCargoRoute(zdo);
        }

        internal static bool DropAllCargo(Character worker, ZDO zdo, string reason = null)
        {
            if (worker == null || zdo == null) return false;
            List<WorkerZdo.CargoStack> cargo = WorkerZdo.ReadCargoStacks(zdo);
            if (cargo.Count == 0) return false;
            if (!TryDropStacks(worker, cargo)) return false;
            WorkerZdo.ClearCargo(zdo);
            WorkerZdo.ClearTarget(zdo);
            return true;
        }

        private static bool TryDropStacks(Character worker, List<WorkerZdo.CargoStack> cargo)
        {
            if (worker == null || cargo == null || cargo.Count == 0) return false;

            Vector3 forward = worker.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 origin = worker.transform.position + Vector3.up * 0.35f + forward * 0.75f;
            int visualIndex = 0;

            // Validate every prefab before dropping anything so a missing prefab can never cause
            // only part of the persistent inventory to be removed.
            List<GameObject> prefabs = new List<GameObject>(cargo.Count);
            for (int i = 0; i < cargo.Count; i++)
            {
                WorkerZdo.CargoStack stack = cargo[i];
                GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(stack.Prefab) : null;
                if (prefab == null && ZNetScene.instance != null) prefab = ZNetScene.instance.GetPrefab(stack.Prefab);
                ItemDrop template = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (stack.Amount <= 0 || string.IsNullOrEmpty(stack.Prefab) || template?.m_itemData == null) return false;
                prefabs.Add(prefab);
            }

            for (int i = 0; i < cargo.Count; i++)
            {
                WorkerZdo.CargoStack stack = cargo[i];
                GameObject prefab = prefabs[i];
                ItemDrop template = prefab.GetComponent<ItemDrop>();
                int maxStack = template.m_itemData.m_shared != null ? Mathf.Max(1, template.m_itemData.m_shared.m_maxStackSize) : stack.Amount;
                int remaining = stack.Amount;
                while (remaining > 0)
                {
                    int amount = Mathf.Min(remaining, maxStack);
                    ItemDrop.ItemData data = template.m_itemData.Clone();
                    data.m_dropPrefab = prefab;
                    float side = ((visualIndex % 3) - 1) * 0.35f;
                    float depth = (visualIndex / 3) * 0.18f;
                    Vector3 position = origin + right * side + forward * depth;
                    ItemDrop.DropItem(data, amount, position, Quaternion.identity);
                    remaining -= amount;
                    visualIndex++;
                }
            }
            return true;
        }

        internal static bool TryTransferCargoToPlayer(Character worker, ZDO zdo, Player player, string prefabName, int requestedAmount)
        {
            if (worker == null || zdo == null || player == null || string.IsNullOrEmpty(prefabName) || requestedAmount <= 0) return false;
            int available = WorkerZdo.GetCargoAmount(zdo, prefabName);
            int amount = Mathf.Min(available, requestedAmount);
            if (amount <= 0) return false;

            Inventory inventory = player.GetInventory();
            GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (inventory == null || drop?.m_itemData == null) return false;

            int moved = 0;
            int maxStack = drop.m_itemData.m_shared != null ? Mathf.Max(1, drop.m_itemData.m_shared.m_maxStackSize) : 1;
            while (moved < amount)
            {
                int stackAmount = Mathf.Min(maxStack, amount - moved);
                ItemDrop.ItemData data = drop.m_itemData.Clone();
                data.m_dropPrefab = prefab;
                data.m_stack = stackAmount;
                if (!inventory.AddItem(data)) break;
                moved += stackAmount;
            }
            if (moved <= 0) return false;
            WorkerZdo.RemoveCargoAmount(zdo, prefabName, moved);
            if (!WorkerZdo.HasCargo(zdo)) WorkerZdo.ClearCargoRoute(zdo);
            return moved == amount;
        }

        /// <summary>
        /// QoL helper for the worker inventory window. Moves as much of the current persistent
        /// cargo as the player inventory can accept, using the same proven per-item transfer path
        /// as the existing "1 nehmen" / "Stack" buttons. Partial transfer is intentional: if
        /// the player inventory becomes full, the remaining cargo stays safely on the Worker.
        /// </summary>
        internal static int TransferAllCargoToPlayer(Character worker, ZDO zdo, Player player)
        {
            if (worker == null || zdo == null || player == null) return 0;

            List<WorkerZdo.CargoStack> snapshot = WorkerZdo.ReadCargoStacks(zdo);
            if (snapshot.Count == 0) return 0;

            int movedTotal = 0;
            for (int i = 0; i < snapshot.Count; i++)
            {
                WorkerZdo.CargoStack stack = snapshot[i];
                if (stack.Amount <= 0 || string.IsNullOrEmpty(stack.Prefab)) continue;

                int before = WorkerZdo.GetCargoAmount(zdo, stack.Prefab);
                if (before <= 0) continue;

                // The existing transfer method already handles real stack sizes and stops safely
                // when the player's inventory cannot accept another stack. Its bool result only
                // indicates whether the complete requested amount moved, so measure the ZDO delta
                // to count partial transfers as well.
                TryTransferCargoToPlayer(worker, zdo, player, stack.Prefab, before);
                int after = WorkerZdo.GetCargoAmount(zdo, stack.Prefab);
                movedTotal += Mathf.Max(0, before - after);
            }

            return movedTotal;
        }

        /// <summary>
        /// Manual QoL variant of DropAllCargo. Reuses the existing fail-closed world-drop path,
        /// then returns the Worker to normal job selection so a stale delivery target cannot keep
        /// it walking after the player deliberately emptied its cargo.
        /// </summary>
        internal static bool DropAllCargoFromInventory(Character worker, ZDO zdo)
        {
            if (!DropAllCargo(worker, zdo, "manual inventory drop")) return false;

            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state != WorkerState.ReturnHomeForRest && state != WorkerState.Resting && state != WorkerState.Recover)
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            return true;
        }

        internal static bool TryTransferPlayerToCargo(Character worker, ZDO zdo, Player player, string prefabName, int requestedAmount)
        {
            if (worker == null || zdo == null || player == null || string.IsNullOrEmpty(prefabName) || requestedAmount <= 0) return false;
            Inventory inventory = player.GetInventory();
            if (inventory == null) return false;

            List<ItemDrop.ItemData> items = inventory.GetAllItems();
            int available = 0;
            for (int i = 0; i < items.Count; i++)
                if (WorkerStorageRuntime.ItemMatchesPrefab(items[i], prefabName)) available += Mathf.Max(0, items[i].m_stack);

            int accepted = GetMaxAddAmount(worker, zdo, prefabName, Mathf.Min(available, requestedAmount));
            if (accepted <= 0) return false;

            int remaining = accepted;
            for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
            {
                ItemDrop.ItemData item = items[i];
                if (!WorkerStorageRuntime.ItemMatchesPrefab(item, prefabName)) continue;
                int take = Mathf.Min(remaining, item.m_stack);
                inventory.RemoveItem(item, take);
                remaining -= take;
            }

            int moved = accepted - remaining;
            if (moved <= 0) return false;
            WorkerZdo.AddCargoAmount(zdo, prefabName, moved);
            return moved == requestedAmount;
        }

        private static void ResolveItem(string prefabName, out int maxStack, out float weight)
        {
            maxStack = 1;
            weight = 0f;
            if (string.IsNullOrEmpty(prefabName)) return;
            GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            if (prefab == null && ZNetScene.instance != null) prefab = ZNetScene.instance.GetPrefab(prefabName);
            ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            ItemDrop.ItemData.SharedData shared = drop?.m_itemData?.m_shared;
            if (shared == null) return;
            maxStack = Mathf.Max(1, shared.m_maxStackSize);
            weight = Mathf.Max(0f, shared.m_weight);
        }
    }
}
