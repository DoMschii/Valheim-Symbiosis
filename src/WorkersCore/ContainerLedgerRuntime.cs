using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Persistent inventory ledger for logistics containers. Physical loaded inventories remain
    /// authoritative. Background operations may only use a previously captured snapshot and must
    /// go through reservation/commit transactions. Committed background removals are reconciled
    /// against the real inventory before a loaded container is exposed to gameplay again.
    ///
    /// v0.2.82 intentionally starts with the removal side used by ground haulers. The transaction
    /// and snapshot layer is shared so Sorter, Drake and machines can migrate onto the same API in
    /// later versions without touching navigation or physical worker behaviour.
    /// </summary>
    internal static class ContainerLedgerRuntime
    {
        internal const string SnapshotKey = "vw_ledger_snapshot_v1";
        internal const string RevisionKey = "vw_ledger_revision";
        internal const string CapturedKey = "vw_ledger_captured";
        internal const string DirtyKey = "vw_ledger_bg_dirty";
        internal const string ReservedKey = "vw_ledger_reserved_v1";
        internal const string PendingRemoveKey = "vw_ledger_pending_remove_v1";
        internal const string TransactionsKey = "vw_ledger_transactions_v1";
        internal const string LastSyncTicksKey = "vw_ledger_last_sync_ticks";
        internal const string SorterTrackedKey = "vw_sorter_tracked_normal";
        internal const string SorterTrackedOwnerKey = "vw_sorter_tracked_owner";
        private const int MaxTransactions = 24;
        private const int MaxParsedCountCacheEntries = 512;

        private static readonly Dictionary<string, Dictionary<string, int>> ParsedCountCache =
            new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> EmptyCounts =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        internal enum TransactionState
        {
            None = 0,
            Reserved = 1,
            SourceCommitted = 2,
            Completed = 3,
            RolledBack = 4
        }

        internal sealed class TransactionInfo
        {
            internal string Id = string.Empty;
            internal string Prefab = string.Empty;
            internal int Amount;
            internal TransactionState State;
            internal string OwnerTag = string.Empty;
        }

        internal static bool ShouldTrack(Container container)
        {
            if (container == null) return false;
            WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(container);
            if (role == WorkerContainerRole.FieldDepot || role == WorkerContainerRole.Import || role == WorkerContainerRole.Export ||
                WorkerContainerRoleRuntime.SupportsAirTransport(container)) return true;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(container);
            return role == WorkerContainerRole.Normal && zdo != null && zdo.GetBool(SorterTrackedKey, false);
        }

        internal static bool MarkSorterStorage(Container container, long ownerId)
        {
            if (container == null || WorkerContainerRoleRuntime.GetRole(container) != WorkerContainerRole.Normal) return false;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(container);
            if (zdo == null) return false;
            bool wasTracked = zdo.GetBool(SorterTrackedKey, false);
            long previousOwner = zdo.GetLong(SorterTrackedOwnerKey, 0L);
            bool ownerChanged = ownerId != 0L && previousOwner != ownerId;
            if (!wasTracked) zdo.Set(SorterTrackedKey, true);
            if (ownerChanged) zdo.Set(SorterTrackedOwnerKey, ownerId);
            PersistentContainerRegistryRuntime.Remember(zdo);

            // v0.5.250: Entering a large Base wakes many chests in one frame. The old path
            // serialized every inventory and rewrote its ZDO even when it already had a clean,
            // current ledger snapshot. Container.Save keeps tracked chest snapshots current, so
            // only first registration / ownership changes / missing snapshots need a capture here.
            if (!wasTracked || ownerChanged || !HasSnapshot(zdo))
                SyncFromPhysical(container, "sorter-normal-storage-bootstrap");

            if (!wasTracked || ownerChanged)
                WorkerProvisioningLogisticsRuntime.RequestPersistentProvisionAudit();
            return true;
        }

        internal static bool HasPersistentInventoryPayload(ZDO zdo)
        {
            if (zdo == null) return false;
            if (HasSnapshot(zdo)) return true;
            byte[] bytes = TryReadSerializedInventoryBytes(zdo);
            if (bytes != null && bytes.Length > 0) return true;
            return !string.IsNullOrEmpty(TryReadSerializedInventoryString(zdo));
        }

        internal static bool TryGetPersistentFreeCapacity(ZDO zdo, string prefab, out int capacity)
        {
            capacity = 0;
            if (zdo == null || string.IsNullOrEmpty(prefab)) return false;
            try
            {
                Inventory inventory = null;
                byte[] bytes = TryReadSerializedInventoryBytes(zdo);
                if (bytes != null && bytes.Length > 0)
                {
                    var temp = new Inventory("vw_bg_capacity", null, 32, 32);
                    temp.Load(new ZPackage(bytes));
                    inventory = temp;
                }
                if (inventory == null)
                {
                    string encoded = TryReadSerializedInventoryString(zdo);
                    if (!string.IsNullOrEmpty(encoded))
                    {
                        try
                        {
                            ConstructorInfo ctor = typeof(ZPackage).GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                            if (ctor != null)
                            {
                                ZPackage package = ctor.Invoke(new object[] { encoded }) as ZPackage;
                                if (package != null)
                                {
                                    var temp = new Inventory("vw_bg_capacity", null, 32, 32);
                                    temp.Load(package);
                                    inventory = temp;
                                }
                            }
                        }
                        catch { }
                        if (inventory == null)
                        {
                            try
                            {
                                byte[] decoded = Convert.FromBase64String(encoded);
                                if (decoded.Length > 0)
                                {
                                    var temp = new Inventory("vw_bg_capacity", null, 32, 32);
                                    temp.Load(new ZPackage(decoded));
                                    inventory = temp;
                                }
                            }
                            catch { }
                        }
                    }
                }
                if (inventory == null) return false;
                capacity = WorkerStorageRuntime.GetAvailableCapacityForPrefab(inventory, prefab);
                return true;
            }
            catch
            {
                capacity = 0;
                return false;
            }
        }

        internal static int GetSnapshotAmount(ZDO zdo, string prefab)
        {
            if (zdo == null || string.IsNullOrEmpty(prefab) || !HasSnapshot(zdo)) return 0;
            Dictionary<string, int> counts = GetCachedCounts(zdo.GetString(SnapshotKey, string.Empty));
            string wanted = WorkerStorageRuntime.NormalizeObjectName(prefab);
            if (counts.TryGetValue(wanted, out int exact)) return Math.Max(0, exact);

            string wantedShared = string.Empty;
            try
            {
                GameObject wantedPrefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(wanted) : null;
                wantedShared = wantedPrefab?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_name ?? string.Empty;
            }
            catch { }

            foreach (KeyValuePair<string, int> pair in counts)
            {
                if (pair.Value <= 0) continue;
                string key = WorkerStorageRuntime.NormalizeObjectName(pair.Key);
                if (string.Equals(key, wanted, StringComparison.OrdinalIgnoreCase)) return pair.Value;
                if (!string.IsNullOrEmpty(wantedShared) && string.Equals(key, wantedShared, StringComparison.OrdinalIgnoreCase)) return pair.Value;
                if (string.IsNullOrEmpty(wantedShared) || ObjectDB.instance == null) continue;
                try
                {
                    GameObject keyPrefab = ObjectDB.instance.GetItemPrefab(key);
                    string keyShared = keyPrefab?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_name;
                    if (!string.IsNullOrEmpty(keyShared) && string.Equals(keyShared, wantedShared, StringComparison.Ordinal))
                        return pair.Value;
                }
                catch { }
            }
            return 0;
        }

        internal static bool TryCaptureSorterStorageFromPersistentZdo(ZDO zdo, long ownerId, bool requestProvisionAudit = true)
        {
            if (zdo == null) return false;
            WorkerContainerRole role = (WorkerContainerRole)Mathf.Clamp(zdo.GetInt(WorkerContainerRoleRuntime.RoleKey, 0), 0, 3);
            if (role != WorkerContainerRole.Normal) return false;

            if (HasSnapshot(zdo))
            {
                bool wasTracked = zdo.GetBool(SorterTrackedKey, false);
                long previousOwner = zdo.GetLong(SorterTrackedOwnerKey, 0L);
                zdo.Set(SorterTrackedKey, true);
                if (ownerId != 0L) zdo.Set(SorterTrackedOwnerKey, ownerId);
                PersistentContainerRegistryRuntime.Remember(zdo);
                if (requestProvisionAudit && (!wasTracked || (ownerId != 0L && previousOwner != ownerId)))
                    WorkerProvisioningLogisticsRuntime.RequestPersistentProvisionAudit();
                return true;
            }
            if (zdo.GetBool(DirtyKey, false)) return false;

            try
            {
                // Prefer confirming the ZDO prefab as a real Valheim Container. A remote zone can
                // temporarily expose the ZDO before ZNetScene can resolve its prefab, however, so a
                // missing prefab lookup is no longer treated as proof that the ZDO is not storage.
                // In that case the serialized inventory itself is the conservative fallback: only a
                // payload that Inventory.Load can successfully parse is accepted.
                if (ZNetScene.instance != null)
                {
                    MethodInfo getPrefab = typeof(ZNetScene).GetMethod("GetPrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
                    GameObject prefabObject = getPrefab?.Invoke(ZNetScene.instance, new object[] { zdo.GetPrefab() }) as GameObject;
                    if (prefabObject != null && prefabObject.GetComponent<Container>() == null && prefabObject.GetComponentInChildren<Container>(true) == null)
                        return false;
                }

                if (!TryLoadPersistentInventoryCounts(zdo, out Dictionary<string, int> counts) || counts.Count == 0)
                    return false;

                zdo.Set(SorterTrackedKey, true);
                if (ownerId != 0L) zdo.Set(SorterTrackedOwnerKey, ownerId);
                zdo.Set(SnapshotKey, SerializeCounts(counts));
                zdo.Set(CapturedKey, true);
                zdo.Set(LastSyncTicksKey, DateTime.UtcNow.Ticks);
                zdo.Set(RevisionKey, Math.Max(1, zdo.GetInt(RevisionKey, 0) + 1));
                PersistentContainerRegistryRuntime.Remember(zdo);
                if (requestProvisionAudit)
                    WorkerProvisioningLogisticsRuntime.RequestPersistentProvisionAudit();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Reads the real serialized Valheim Container inventory from a persistent ZDO without
        /// requiring the chest GameObject to be loaded. Valheim versions have used both byte[] and
        /// Base64/string backed ZDO fields, and some expose string-key overloads while others expose
        /// stable-hash overloads. Support all of those representations so an unloaded Base can be
        /// simulated from real stock instead of requiring the player to visit it first.
        /// </summary>
        private static bool TryLoadPersistentInventoryCounts(ZDO zdo, out Dictionary<string, int> counts)
        {
            counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (zdo == null) return false;

            byte[] bytes = TryReadSerializedInventoryBytes(zdo);
            if (bytes != null && bytes.Length > 0 && TryLoadInventoryPackage(new ZPackage(bytes), out counts))
                return true;

            string encoded = TryReadSerializedInventoryString(zdo);
            if (string.IsNullOrEmpty(encoded)) return false;

            // Prefer ZPackage's own string/Base64 constructor when the installed Valheim build
            // exposes it. This avoids assuming the exact on-disk encoding used by that build.
            try
            {
                ConstructorInfo ctor = typeof(ZPackage).GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(string) }, null);
                if (ctor != null)
                {
                    ZPackage package = ctor.Invoke(new object[] { encoded }) as ZPackage;
                    if (package != null && TryLoadInventoryPackage(package, out counts)) return true;
                }
            }
            catch { }

            // Fallback for builds where GetBase64() is simply a Base64 encoded package byte array.
            try
            {
                byte[] decoded = Convert.FromBase64String(encoded);
                if (decoded.Length > 0 && TryLoadInventoryPackage(new ZPackage(decoded), out counts)) return true;
            }
            catch { }

            return false;
        }

        private static bool TryLoadInventoryPackage(ZPackage package, out Dictionary<string, int> counts)
        {
            counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (package == null) return false;
            try
            {
                Inventory temp = new Inventory("vw_bg_sorter_remote", null, 32, 32);
                temp.Load(package);
                counts = SnapshotInventory(temp);
                return true;
            }
            catch
            {
                counts.Clear();
                return false;
            }
        }

        private static byte[] TryReadSerializedInventoryBytes(ZDO zdo)
        {
            if (zdo == null) return null;
            int itemsHash = StableHash("items");

            // Hash-key overload used by several Valheim versions.
            try
            {
                MethodInfo getBytes = typeof(ZDO).GetMethod("GetByteArray", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int), typeof(byte[]) }, null);
                byte[] bytes = getBytes?.Invoke(zdo, new object[] { itemsHash, null }) as byte[];
                if (bytes != null && bytes.Length > 0) return bytes;
            }
            catch { }

            // String-key overload used by other builds/modded ZDO wrappers.
            try
            {
                MethodInfo getBytes = typeof(ZDO).GetMethod("GetByteArray", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(byte[]) }, null);
                byte[] bytes = getBytes?.Invoke(zdo, new object[] { "items", null }) as byte[];
                if (bytes != null && bytes.Length > 0) return bytes;
            }
            catch { }

            return null;
        }

        private static string TryReadSerializedInventoryString(ZDO zdo)
        {
            if (zdo == null) return string.Empty;
            int itemsHash = StableHash("items");

            try
            {
                MethodInfo getString = typeof(ZDO).GetMethod("GetString", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int), typeof(string) }, null);
                string encoded = getString?.Invoke(zdo, new object[] { itemsHash, string.Empty }) as string;
                if (!string.IsNullOrEmpty(encoded)) return encoded;
            }
            catch { }

            try
            {
                MethodInfo getString = typeof(ZDO).GetMethod("GetString", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(string) }, null);
                string encoded = getString?.Invoke(zdo, new object[] { "items", string.Empty }) as string;
                if (!string.IsNullOrEmpty(encoded)) return encoded;
            }
            catch { }

            return string.Empty;
        }

        private static int StableHash(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            unchecked
            {
                int hash1 = 5381;
                int hash2 = hash1;
                for (int i = 0; i < text.Length; i += 2)
                {
                    hash1 = ((hash1 << 5) + hash1) ^ text[i];
                    if (i == text.Length - 1) break;
                    hash2 = ((hash2 << 5) + hash2) ^ text[i + 1];
                }
                return hash1 + hash2 * 1566083941;
            }
        }

        internal static bool HasSnapshot(ZDO zdo)
        {
            return zdo != null && zdo.GetBool(CapturedKey, false);
        }

        // v0.5.93: An unloaded logistics endpoint may exist in the world before this session has
        // physically loaded its Container component. Bootstrap the ledger directly from Valheim's
        // serialized ZDO inventory so background haulers do not require a player visit first.
        internal static bool EnsureSnapshotFromPersistentZdo(ZDO zdo)
        {
            if (zdo == null) return false;
            if (HasSnapshot(zdo)) return true;
            if (zdo.GetBool(DirtyKey, false)) return false;

            try
            {
                if (!TryLoadPersistentInventoryCounts(zdo, out Dictionary<string, int> counts)) return false;
                zdo.Set(SnapshotKey, SerializeCounts(counts));
                zdo.Set(CapturedKey, true);
                zdo.Set(LastSyncTicksKey, DateTime.UtcNow.Ticks);
                zdo.Set(RevisionKey, Math.Max(1, zdo.GetInt(RevisionKey, 0) + 1));
                PersistentContainerRegistryRuntime.Remember(zdo);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static int GetRevision(ZDO zdo)
        {
            return zdo?.GetInt(RevisionKey, 0) ?? 0;
        }

        internal static string CreateTransactionId()
        {
            return Guid.NewGuid().ToString("N");
        }

        internal static void SyncFromPhysical(Container container, string reason = null)
        {
            if (container == null || !ShouldTrack(container)) return;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(container);
            Inventory inventory = container.GetInventory();
            if (zdo == null || inventory == null) return;
            PersistentContainerRegistryRuntime.Remember(zdo);

            // A dirty ledger contains committed background changes that are not yet reflected in
            // this physical inventory. Never overwrite those changes with stale physical contents.
            if (zdo.GetBool(DirtyKey, false)) return;

            Dictionary<string, int> counts = SnapshotInventory(inventory);
            string serialized = SerializeCounts(counts);
            string previous = zdo.GetString(SnapshotKey, string.Empty);

            // v0.5.250: Do not generate network/ZDO traffic for an unchanged inventory. This is
            // especially important when dozens or hundreds of Base chests save close together.
            if (zdo.GetBool(CapturedKey, false) && string.Equals(previous, serialized, StringComparison.Ordinal))
                return;

            zdo.Set(SnapshotKey, serialized);
            zdo.Set(CapturedKey, true);
            zdo.Set(LastSyncTicksKey, DateTime.UtcNow.Ticks);
            zdo.Set(RevisionKey, Math.Max(1, zdo.GetInt(RevisionKey, 0) + 1));
        }

        internal static bool TryGetCount(ZDO zdo, string prefab, out int count)
        {
            count = 0;
            if (zdo == null || string.IsNullOrEmpty(prefab) || !HasSnapshot(zdo)) return false;
            count = GetSnapshotAmount(zdo, prefab);
            return true;
        }

        internal static bool TryGetAvailable(ZDO zdo, string prefab, int minimumRemain, out int available)
        {
            available = 0;
            string canonicalPrefab = WorkerStorageRuntime.NormalizeObjectName(prefab);
            if (!TryGetCount(zdo, canonicalPrefab, out int total)) return false;
            Dictionary<string, int> reserved = GetCachedCounts(zdo.GetString(ReservedKey, string.Empty));
            reserved.TryGetValue(canonicalPrefab, out int reservedAmount);
            available = Math.Max(0, total - Math.Max(0, reservedAmount) - Math.Max(0, minimumRemain));
            return true;
        }

        internal static void CollectSnapshotPrefabs(ZDO zdo, List<string> output)
        {
            if (output == null) return;
            output.Clear();
            if (zdo == null || !HasSnapshot(zdo)) return;
            Dictionary<string, int> counts = GetCachedCounts(zdo.GetString(SnapshotKey, string.Empty));
            foreach (KeyValuePair<string, int> pair in counts)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value > 0)
                    output.Add(pair.Key);
            }
            output.Sort(StringComparer.OrdinalIgnoreCase);
        }

        internal static bool TryReserveRemoval(
            ZDO sourceZdo,
            string transactionId,
            string prefab,
            int requestedAmount,
            int minimumRemain,
            string ownerTag,
            out int reservedAmount,
            out string status)
        {
            reservedAmount = 0;
            status = "ledger:invalid";
            if (sourceZdo == null || string.IsNullOrEmpty(transactionId) || string.IsNullOrEmpty(prefab) || requestedAmount <= 0)
                return false;
            if (!HasSnapshot(sourceZdo))
            {
                status = "ledger:no-snapshot";
                return false;
            }

            if (TryGetTransaction(sourceZdo, transactionId, out TransactionInfo existing))
            {
                reservedAmount = existing.Amount;
                status = $"ledger:transaction-{existing.State.ToString().ToLowerInvariant()}";
                return existing.State == TransactionState.Reserved || existing.State == TransactionState.SourceCommitted || existing.State == TransactionState.Completed;
            }

            if (!TryGetAvailable(sourceZdo, prefab, minimumRemain, out int available) || available <= 0)
            {
                status = "ledger:protected-or-empty";
                return false;
            }

            reservedAmount = Math.Min(requestedAmount, available);
            Dictionary<string, int> reserved = DeserializeCounts(sourceZdo.GetString(ReservedKey, string.Empty));
            reserved.TryGetValue(prefab, out int oldReserved);
            reserved[prefab] = oldReserved + reservedAmount;
            sourceZdo.Set(ReservedKey, SerializeCounts(reserved));

            UpsertTransaction(sourceZdo, new TransactionInfo
            {
                Id = transactionId,
                Prefab = prefab,
                Amount = reservedAmount,
                State = TransactionState.Reserved,
                OwnerTag = ownerTag ?? string.Empty
            });
            status = $"ledger:reserved {prefab} x{reservedAmount}";
            return true;
        }

        internal static bool TryCommitRemoval(ZDO sourceZdo, string transactionId, out TransactionInfo transaction, out string status)
        {
            transaction = null;
            status = "ledger:transaction-missing";
            if (!TryGetTransaction(sourceZdo, transactionId, out TransactionInfo tx)) return false;
            transaction = tx;

            if (tx.State == TransactionState.SourceCommitted || tx.State == TransactionState.Completed)
            {
                status = $"ledger:already-{tx.State.ToString().ToLowerInvariant()}";
                return true;
            }
            if (tx.State != TransactionState.Reserved || tx.Amount <= 0)
            {
                status = "ledger:not-reserved";
                return false;
            }

            Dictionary<string, int> snapshot = DeserializeCounts(sourceZdo.GetString(SnapshotKey, string.Empty));
            snapshot.TryGetValue(tx.Prefab, out int total);
            if (total < tx.Amount)
            {
                RollbackReservation(sourceZdo, tx);
                tx.State = TransactionState.RolledBack;
                UpsertTransaction(sourceZdo, tx);
                transaction = tx;
                status = "ledger:revision-conflict";
                return false;
            }

            snapshot[tx.Prefab] = total - tx.Amount;
            if (snapshot[tx.Prefab] <= 0) snapshot.Remove(tx.Prefab);
            sourceZdo.Set(SnapshotKey, SerializeCounts(snapshot));
            RollbackReservation(sourceZdo, tx);

            Dictionary<string, int> pending = DeserializeCounts(sourceZdo.GetString(PendingRemoveKey, string.Empty));
            pending.TryGetValue(tx.Prefab, out int pendingAmount);
            pending[tx.Prefab] = pendingAmount + tx.Amount;
            sourceZdo.Set(PendingRemoveKey, SerializeCounts(pending));
            sourceZdo.Set(DirtyKey, true);
            sourceZdo.Set(RevisionKey, Math.Max(1, sourceZdo.GetInt(RevisionKey, 0) + 1));

            tx.State = TransactionState.SourceCommitted;
            UpsertTransaction(sourceZdo, tx);
            transaction = tx;
            status = $"ledger:source-committed {tx.Prefab} x{tx.Amount}";
            return true;
        }

        internal static bool CompleteTransaction(ZDO sourceZdo, string transactionId)
        {
            if (!TryGetTransaction(sourceZdo, transactionId, out TransactionInfo tx)) return false;
            tx.State = TransactionState.Completed;
            UpsertTransaction(sourceZdo, tx);
            return true;
        }

        internal static bool RollbackTransaction(ZDO sourceZdo, string transactionId)
        {
            if (!TryGetTransaction(sourceZdo, transactionId, out TransactionInfo tx)) return false;
            if (tx.State == TransactionState.Reserved) RollbackReservation(sourceZdo, tx);
            if (tx.State == TransactionState.SourceCommitted || tx.State == TransactionState.Completed) return false;
            tx.State = TransactionState.RolledBack;
            UpsertTransaction(sourceZdo, tx);
            return true;
        }

        internal static bool TryGetTransaction(ZDO zdo, string transactionId, out TransactionInfo transaction)
        {
            transaction = null;
            if (zdo == null || string.IsNullOrEmpty(transactionId)) return false;
            List<TransactionInfo> list = DeserializeTransactions(zdo.GetString(TransactionsKey, string.Empty));
            for (int i = 0; i < list.Count; i++)
            {
                if (!string.Equals(list[i].Id, transactionId, StringComparison.Ordinal)) continue;
                transaction = list[i];
                return true;
            }
            return false;
        }

        internal static bool TryReconcileLoaded(Container container, out string status)
        {
            status = "ledger:not-tracked";
            if (container == null || !ShouldTrack(container)) return false;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(container);
            Inventory inventory = container.GetInventory();
            if (zdo == null || inventory == null) return false;

            if (!zdo.GetBool(DirtyKey, false))
            {
                // v0.5.250: Opening an ordinary clean chest must be O(1). Container.Save already
                // refreshes the snapshot after real inventory changes, so rescanning/serializing
                // the complete inventory merely because the player pressed E is wasted work.
                // Only bootstrap a missing snapshot; otherwise trust the clean ledger.
                if (!HasSnapshot(zdo))
                    SyncFromPhysical(container, "loaded-clean-bootstrap");
                status = "ledger:clean";
                return true;
            }

            if (!WorkerStorageRuntime.TryOwn(container))
            {
                status = "ledger:waiting-ownership";
                return false;
            }

            // A busy inventory is not evidence that the committed source items are physically
            // missing. Defer reconciliation and keep PendingRemove/Dirty intact until Workers
            // can safely inspect and mutate the loaded inventory.
            if (!WorkerInventoryMutationGuard.CanWorkersMutate(inventory))
            {
                status = $"ledger:waiting-mutation-guard {WorkerInventoryMutationGuard.GetRemainingQuietTime(inventory):0.00}s";
                return false;
            }

            Dictionary<string, int> pending = DeserializeCounts(zdo.GetString(PendingRemoveKey, string.Empty));
            bool changed = false;
            foreach (KeyValuePair<string, int> pair in pending)
            {
                int remaining = Math.Max(0, pair.Value);
                while (remaining > 0)
                {
                    ItemDrop.ItemData item = WorkerStorageRuntime.FindPrefab(inventory, pair.Key);
                    if (item == null) break;
                    int take = Math.Min(remaining, Math.Max(1, item.m_stack));
                    int removed = 0;
                    while (removed < take && WorkerStorageRuntime.TryRemoveOne(inventory, item)) removed++;
                    if (removed <= 0) break;
                    remaining -= removed;
                    changed = true;
                }
                // If physical reality contains fewer items than expected, physical wins. We clear
                // the remainder rather than ever restoring/minting missing items.
            }

            zdo.Set(PendingRemoveKey, string.Empty);
            zdo.Set(DirtyKey, false);
            if (changed) WorkerStorageRuntime.PersistContainer(container, inventory);
            SyncFromPhysical(container, "reconciled");
            status = changed ? "ledger:reconciled" : "ledger:reconciled-noop/physical-wins";
            return true;
        }

        internal static string GetDebugSummary(ZDO zdo)
        {
            if (zdo == null) return "ledger:none";
            string state = HasSnapshot(zdo) ? "captured" : "uncaptured";
            string dirty = zdo.GetBool(DirtyKey, false) ? "dirty" : "clean";
            int txCount = DeserializeTransactions(zdo.GetString(TransactionsKey, string.Empty)).Count;
            return $"ledger:{state}/{dirty}; rev={GetRevision(zdo)}; tx={txCount}";
        }

        private static Dictionary<string, int> SnapshotInventory(Inventory inventory)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (inventory == null) return counts;
            List<ItemDrop.ItemData> items = inventory.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (item == null || item.m_stack <= 0) continue;
                string prefab = WorkerStorageRuntime.GetPrefabName(item);
                if (string.IsNullOrEmpty(prefab)) continue;
                counts.TryGetValue(prefab, out int old);
                counts[prefab] = old + item.m_stack;
            }
            return counts;
        }

        private static void RollbackReservation(ZDO sourceZdo, TransactionInfo tx)
        {
            Dictionary<string, int> reserved = DeserializeCounts(sourceZdo.GetString(ReservedKey, string.Empty));
            reserved.TryGetValue(tx.Prefab, out int value);
            value = Math.Max(0, value - Math.Max(0, tx.Amount));
            if (value <= 0) reserved.Remove(tx.Prefab); else reserved[tx.Prefab] = value;
            sourceZdo.Set(ReservedKey, SerializeCounts(reserved));
        }

        private static void UpsertTransaction(ZDO zdo, TransactionInfo tx)
        {
            List<TransactionInfo> list = DeserializeTransactions(zdo.GetString(TransactionsKey, string.Empty));
            int found = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].Id, tx.Id, StringComparison.Ordinal)) { found = i; break; }
            }
            if (found >= 0) list[found] = tx; else list.Add(tx);
            if (list.Count > MaxTransactions) list.RemoveRange(0, list.Count - MaxTransactions);
            zdo.Set(TransactionsKey, SerializeTransactions(list));
        }

        private static string SerializeCounts(Dictionary<string, int> counts)
        {
            if (counts == null || counts.Count == 0) return string.Empty;
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, int> pair in counts)
            {
                if (string.IsNullOrEmpty(pair.Key) || pair.Value <= 0) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(Uri.EscapeDataString(pair.Key));
                sb.Append('=');
                sb.Append(pair.Value.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        internal static void ClearCaches()
        {
            ParsedCountCache.Clear();
        }

        private static Dictionary<string, int> GetCachedCounts(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return EmptyCounts;
            if (ParsedCountCache.TryGetValue(raw, out Dictionary<string, int> cached))
                return cached;

            Dictionary<string, int> parsed = DeserializeCounts(raw);
            if (ParsedCountCache.Count >= MaxParsedCountCacheEntries)
                ParsedCountCache.Clear();

            ParsedCountCache[raw] = parsed;
            return parsed;
        }

        private static Dictionary<string, int> DeserializeCounts(string raw)
        {
            Dictionary<string, int> result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(raw)) return result;
            string[] entries = raw.Split(';');
            for (int i = 0; i < entries.Length; i++)
            {
                int split = entries[i].LastIndexOf('=');
                if (split <= 0) continue;
                string prefab;
                try { prefab = Uri.UnescapeDataString(entries[i].Substring(0, split)); }
                catch { continue; }
                if (!int.TryParse(entries[i].Substring(split + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount) || amount <= 0) continue;
                result[prefab] = amount;
            }
            return result;
        }

        private static string SerializeTransactions(List<TransactionInfo> transactions)
        {
            if (transactions == null || transactions.Count == 0) return string.Empty;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < transactions.Count; i++)
            {
                TransactionInfo tx = transactions[i];
                if (tx == null || string.IsNullOrEmpty(tx.Id)) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(Uri.EscapeDataString(tx.Id)).Append(',')
                  .Append(Uri.EscapeDataString(tx.Prefab ?? string.Empty)).Append(',')
                  .Append(tx.Amount.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(((int)tx.State).ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(Uri.EscapeDataString(tx.OwnerTag ?? string.Empty));
            }
            return sb.ToString();
        }

        private static List<TransactionInfo> DeserializeTransactions(string raw)
        {
            List<TransactionInfo> result = new List<TransactionInfo>();
            if (string.IsNullOrEmpty(raw)) return result;
            string[] entries = raw.Split(';');
            for (int i = 0; i < entries.Length; i++)
            {
                string[] a = entries[i].Split(',');
                if (a.Length < 5) continue;
                if (!int.TryParse(a[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount)) continue;
                if (!int.TryParse(a[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int state)) continue;
                try
                {
                    result.Add(new TransactionInfo
                    {
                        Id = Uri.UnescapeDataString(a[0]),
                        Prefab = Uri.UnescapeDataString(a[1]),
                        Amount = amount,
                        State = (TransactionState)Mathf.Clamp(state, 0, 4),
                        OwnerTag = Uri.UnescapeDataString(a[4])
                    });
                }
                catch { }
            }
            return result;
        }
    }

    [HarmonyPatch(typeof(Container), "Save")]
    internal static class ContainerLedgerSavePatch
    {
        private static void Postfix(Container __instance)
        {
            try { SharedStorageStockIndexRuntime.OnContainerSaved(__instance); }
            catch { }
            try { FieldSupplyDemandCacheRuntime.OnContainerSaved(__instance); }
            catch { }
            try { ContainerLedgerRuntime.SyncFromPhysical(__instance, "container-save"); }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Container), "Interact", typeof(Humanoid), typeof(bool), typeof(bool))]
    [HarmonyPriority(Priority.First + 50)]
    internal static class ContainerLedgerInteractPatch
    {
        private static void Prefix(Container __instance)
        {
            try
            {
                if (__instance == null || !ContainerLedgerRuntime.ShouldTrack(__instance)) return;
                ZDO zdo = WorkerContainerRoleRuntime.GetZdo(__instance);
                if (zdo == null) return;

                // Clean + captured means there is nothing to reconcile. Avoid touching the whole
                // inventory on every chest open. Dirty ledgers still reconcile before vanilla UI.
                if (!zdo.GetBool(ContainerLedgerRuntime.DirtyKey, false) &&
                    zdo.GetBool(ContainerLedgerRuntime.CapturedKey, false)) return;

                ContainerLedgerRuntime.TryReconcileLoaded(__instance, out _);
            }
            catch { }
        }
    }
}
