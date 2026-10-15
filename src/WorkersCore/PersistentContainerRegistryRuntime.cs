using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Persistent map-wide registry for logistics endpoints.
    /// Loaded Container instances are transient; their ZDOs remain the durable identity and
    /// position source used by long-distance haulers while a Base/Outpost scene is unloaded.
    /// </summary>
    internal static class PersistentContainerRegistryRuntime
    {
        private sealed class Record
        {
            internal ZDO Zdo;
            internal WorkerContainerRole Role;
            internal long Owner;
            internal Vector3 Position;
        }

        private static readonly Dictionary<ZDOID, Record> Records = new Dictionary<ZDOID, Record>();
        private static readonly List<ZDOID> SharedIterationKeys = new List<ZDOID>(256);
        private static int _iterationDepth;
        internal static int RegisteredCount => Records.Count;
        private static bool _bootstrapped;
        private static float _nextPersistentScanRealtime;
        private static float _nextLoadedIndexSyncRealtime;
        private static float _nextExplicitRescanAllowedRealtime;
        private static bool _persistentScanInProgress;
        // Container lifecycle hooks keep the registry current after startup. v0.5.229 reduces the
        // expensive full-world ZDO safety reconciliation to once per Valheim day (~30 minutes).
        private const float PersistentMaintenanceScanSeconds = 30f * 60f;
        private const float PersistentStartupRetrySeconds = 2f;
        private const float ExplicitRescanCooldownSeconds = 20f;
        // Container.Awake lifecycle hooks already register loaded endpoints immediately. This is
        // only a fallback mirror, so once every 30 seconds is plenty and avoids scanning a Base
        // with hundreds of loaded chests every second.
        private const float LoadedIndexSyncSeconds = 30f;

        internal static void Remember(Container container)
        {
            if (container == null) return;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(container);
            Remember(zdo);
        }

        internal static void Remember(ZDO zdo)
        {
            if (zdo == null) return;
            WorkerContainerRole role = (WorkerContainerRole)Mathf.Clamp(zdo.GetInt(WorkerContainerRoleRuntime.RoleKey, 0), 0, 3);
            if (role == WorkerContainerRole.Normal && !zdo.GetBool(ContainerLedgerRuntime.SorterTrackedKey, false))
            {
                Records.Remove(zdo.m_uid);
                return;
            }

            Records[zdo.m_uid] = new Record
            {
                Zdo = zdo,
                Role = role,
                Owner = zdo.GetLong(WorkerContainerRoleRuntime.OwnerKey, 0L),
                Position = zdo.GetPosition()
            };
        }

        internal static bool TryFindNearest(WorkerContainerRole role, long ownerId, Vector3 from, out ZDO zdo, out Vector3 position)
        {
            EnsureBootstrapped();
            zdo = null;
            position = default;
            float bestSqr = float.MaxValue;
            List<ZDOID> stale = null;

            foreach (KeyValuePair<ZDOID, Record> pair in Records)
            {
                Record record = pair.Value;
                if (record == null || record.Zdo == null)
                {
                    if (stale == null) stale = new List<ZDOID>();
                    stale.Add(pair.Key);
                    continue;
                }

                WorkerContainerRole currentRole = (WorkerContainerRole)Mathf.Clamp(record.Zdo.GetInt(WorkerContainerRoleRuntime.RoleKey, 0), 0, 3);
                if (currentRole == WorkerContainerRole.Normal && !record.Zdo.GetBool(ContainerLedgerRuntime.SorterTrackedKey, false))
                {
                    if (stale == null) stale = new List<ZDOID>();
                    stale.Add(pair.Key);
                    continue;
                }
                record.Role = currentRole;
                record.Owner = record.Zdo.GetLong(WorkerContainerRoleRuntime.OwnerKey, 0L);
                record.Position = record.Zdo.GetPosition();

                if (record.Role != role) continue;
                if (record.Owner != 0L && ownerId != 0L && record.Owner != ownerId) continue;
                float sqr = (record.Position - from).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                zdo = record.Zdo;
                position = record.Position;
            }

            if (stale != null)
            {
                for (int i = 0; i < stale.Count; i++) Records.Remove(stale[i]);
            }
            return zdo != null;
        }

        internal static bool HasEndpoint(WorkerContainerRole role, long ownerId)
        {
            return TryFindNearest(role, ownerId, Vector3.zero, out _, out _);
        }

        internal static void ForEachPersistent(Action<ZDO, WorkerContainerRole, long, Vector3> visitor)
        {
            if (visitor == null) return;
            EnsureBootstrapped();

            // Visitors are allowed to update persistent container state. Some of those updates call
            // Remember() again (for example clearing stale supply requests), which mutates Records.
            // Snapshot only the keys. Reusing the top-level buffer avoids allocating/copying a
            // KeyValuePair list for every logistics query; nested queries receive a private list.
            bool useShared = _iterationDepth == 0;
            List<ZDOID> keys = useShared ? SharedIterationKeys : new List<ZDOID>(Records.Count);
            _iterationDepth++;
            try
            {
                keys.Clear();
                foreach (ZDOID id in Records.Keys) keys.Add(id);
                for (int i = 0; i < keys.Count; i++)
                {
                    ZDOID id = keys[i];
                    if (!Records.TryGetValue(id, out Record record) || record == null || record.Zdo == null)
                    {
                        Records.Remove(id);
                        continue;
                    }

                    WorkerContainerRole currentRole = (WorkerContainerRole)Mathf.Clamp(record.Zdo.GetInt(WorkerContainerRoleRuntime.RoleKey, 0), 0, 3);
                    if (currentRole == WorkerContainerRole.Normal && !record.Zdo.GetBool(ContainerLedgerRuntime.SorterTrackedKey, false))
                    {
                        Records.Remove(id);
                        continue;
                    }
                    record.Role = currentRole;
                    record.Owner = record.Zdo.GetLong(WorkerContainerRoleRuntime.OwnerKey, 0L);
                    record.Position = record.Zdo.GetPosition();
                    visitor(record.Zdo, record.Role, record.Owner, record.Position);
                }
            }
            finally
            {
                keys.Clear();
                _iterationDepth--;
            }
        }

        internal static void ForEachRemembered(Action<ZDO, WorkerContainerRole, long, Vector3> visitor)
        {
            if (visitor == null) return;
            bool useShared = _iterationDepth == 0;
            List<ZDOID> keys = useShared ? SharedIterationKeys : new List<ZDOID>(Records.Count);
            _iterationDepth++;
            try
            {
                keys.Clear();
                foreach (ZDOID id in Records.Keys) keys.Add(id);
                for (int i = 0; i < keys.Count; i++)
                {
                    if (!Records.TryGetValue(keys[i], out Record record) || record == null || record.Zdo == null) continue;
                    WorkerContainerRole currentRole = (WorkerContainerRole)Mathf.Clamp(record.Zdo.GetInt(WorkerContainerRoleRuntime.RoleKey, 0), 0, 3);
                    record.Role = currentRole;
                    record.Owner = record.Zdo.GetLong(WorkerContainerRoleRuntime.OwnerKey, 0L);
                    record.Position = record.Zdo.GetPosition();
                    visitor(record.Zdo, record.Role, record.Owner, record.Position);
                }
            }
            finally
            {
                keys.Clear();
                _iterationDepth--;
            }
        }

        internal static void RequestPersistentRescan()
        {
            // Never let a repeatedly refreshed supply request force a full ZDOMan reflection walk
            // every AI/scheduler tick. Missing endpoints may request an earlier maintenance pass,
            // but explicit requests are strongly throttled.
            float now = Time.realtimeSinceStartup;
            if (now < _nextExplicitRescanAllowedRealtime) return;
            _nextExplicitRescanAllowedRealtime = now + ExplicitRescanCooldownSeconds;
            _nextPersistentScanRealtime = Mathf.Min(_nextPersistentScanRealtime <= 0f ? now + 0.25f : _nextPersistentScanRealtime, now + 0.25f);
        }

        private static void EnsureBootstrapped()
        {
            float now = Time.realtimeSinceStartup;

            // The loaded-container index is already maintained by lifecycle callbacks. Mirroring
            // it into the persistent registry on every ForEachPersistent call caused needless
            // repeated work because one logistics decision may query the registry many times.
            if (now >= _nextLoadedIndexSyncRealtime)
            {
                _nextLoadedIndexSyncRealtime = now + LoadedIndexSyncSeconds;
                try
                {
                    WorkerWorldIndex.ForEachLoadedContainer(Remember);
                }
                catch { }
            }

            if (_persistentScanInProgress) return;
            if (_bootstrapped && now < _nextPersistentScanRealtime) return;

            ZDOMan man = ZDOMan.instance;
            if (man == null)
            {
                _nextPersistentScanRealtime = now + PersistentStartupRetrySeconds;
                return;
            }

            _persistentScanInProgress = true;
            try
            {
                int before = Records.Count;
                FieldInfo[] fields = typeof(ZDOMan).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < fields.Length; i++)
                {
                    object value;
                    try { value = fields[i].GetValue(man); }
                    catch { continue; }
                    HarvestZdos(value, 0);
                }

                _bootstrapped = true;
                _nextPersistentScanRealtime = now + PersistentMaintenanceScanSeconds;
                int added = Records.Count - before;
                if (added > 0)
                    BackgroundMonitorRuntime.Note($"Persistent container scan discovered {added} logistics endpoint(s); total={Records.Count}");
            }
            catch
            {
                // ZDOMan can still be filling its collections during world startup. Keep this
                // retryable; an incomplete scan must never permanently hide a remote Base hub.
                _bootstrapped = false;
                _nextPersistentScanRealtime = now + PersistentStartupRetrySeconds;
            }
            finally
            {
                _persistentScanInProgress = false;
            }
        }

        private static void HarvestZdos(object value, int depth)
        {
            if (value == null || depth > 2) return;
            if (value is ZDO single)
            {
                int role = single.GetInt(WorkerContainerRoleRuntime.RoleKey, 0);
                if ((role >= (int)WorkerContainerRole.FieldDepot && role <= (int)WorkerContainerRole.Export) ||
                    single.GetBool(ContainerLedgerRuntime.SorterTrackedKey, false))
                    Remember(single);
                return;
            }

            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value is ZDO zdo)
                    {
                        int role = zdo.GetInt(WorkerContainerRoleRuntime.RoleKey, 0);
                        if ((role >= (int)WorkerContainerRole.FieldDepot && role <= (int)WorkerContainerRole.Export) ||
                            zdo.GetBool(ContainerLedgerRuntime.SorterTrackedKey, false))
                            Remember(zdo);
                    }
                    else if (depth < 2)
                    {
                        HarvestZdos(entry.Value, depth + 1);
                    }
                }
                return;
            }

            if (value is IEnumerable enumerable && !(value is string))
            {
                int guard = 0;
                foreach (object item in enumerable)
                {
                    if (++guard > 200000) break;
                    if (item is ZDO zdo)
                    {
                        int role = zdo.GetInt(WorkerContainerRoleRuntime.RoleKey, 0);
                        if ((role >= (int)WorkerContainerRole.FieldDepot && role <= (int)WorkerContainerRole.Export) ||
                            zdo.GetBool(ContainerLedgerRuntime.SorterTrackedKey, false))
                            Remember(zdo);
                    }
                    else if (depth < 2)
                    {
                        HarvestZdos(item, depth + 1);
                    }
                }
            }
        }
    }
}
