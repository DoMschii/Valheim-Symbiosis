using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Keeps friendly Workers from physically body-blocking each other while preserving
    /// normal collision with terrain, structures, containers, trees, enemies and projectiles.
    /// Only enabled non-trigger body colliders belonging to two recruited Workers are paired
    /// with Physics.IgnoreCollision. Trigger/hitbox colliders are deliberately left alone.
    /// </summary>
    internal static class WorkerCollisionRuntime
    {
        private sealed class Entry
        {
            internal Character Worker;
            internal Collider[] BodyColliders;
        }

        private static readonly List<Entry> LoadedWorkers = new List<Entry>();
        private static readonly Dictionary<int, Entry> ByInstance = new Dictionary<int, Entry>();
        private static Action<string> _debugLog;
        private static float _nextCleanup;

        internal static void Initialize(Action<string> debugLog)
        {
            _debugLog = debugLog;
            LoadedWorkers.Clear();
            ByInstance.Clear();
            _nextCleanup = 0f;
        }

        internal static void Register(Character worker)
        {
            if (worker == null || worker is Player || worker.IsDead()) return;

            int key = worker.GetInstanceID();
            if (ByInstance.ContainsKey(key))
            {
                CleanupIfNeeded();
                return;
            }

            ZNetView view = worker.GetComponent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (!WorkerZdo.IsWorker(zdo)) return;

            Collider[] body = CollectBodyColliders(worker);
            Entry entry = new Entry { Worker = worker, BodyColliders = body };

            // Disable body-vs-body collision only. World collision remains fully intact.
            for (int i = 0; i < LoadedWorkers.Count; i++)
            {
                Entry other = LoadedWorkers[i];
                if (other == null || other.Worker == null || other.Worker.IsDead()) continue;
                IgnorePairs(body, other.BodyColliders);
            }

            LoadedWorkers.Add(entry);
            ByInstance[key] = entry;
            _debugLog?.Invoke($"Worker collision policy active: {SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name)} bodyColliders={body.Length}; friendly Workers no longer body-block each other.");
            CleanupIfNeeded();
        }

        private static Collider[] CollectBodyColliders(Character worker)
        {
            Collider[] all = worker.GetComponentsInChildren<Collider>(true);
            List<Collider> result = new List<Collider>(all.Length);
            for (int i = 0; i < all.Length; i++)
            {
                Collider collider = all[i];
                if (collider == null || !collider.enabled || collider.isTrigger) continue;

                // Do not accidentally include colliders belonging to a nested Character.
                Character owner = collider.GetComponentInParent<Character>();
                if (owner != worker) continue;

                result.Add(collider);
            }
            return result.ToArray();
        }

        private static void IgnorePairs(Collider[] left, Collider[] right)
        {
            if (left == null || right == null) return;
            for (int i = 0; i < left.Length; i++)
            {
                Collider a = left[i];
                if (a == null) continue;
                for (int j = 0; j < right.Length; j++)
                {
                    Collider b = right[j];
                    if (b == null || a == b) continue;
                    try
                    {
                        Physics.IgnoreCollision(a, b, true);
                    }
                    catch (Exception)
                    {
                        // A collider can disappear during zone unload between the null check and call.
                    }
                }
            }
        }

        private static void CleanupIfNeeded()
        {
            if (Time.time < _nextCleanup) return;
            _nextCleanup = Time.time + 60f;

            for (int i = LoadedWorkers.Count - 1; i >= 0; i--)
            {
                Entry entry = LoadedWorkers[i];
                if (entry != null && entry.Worker != null && !entry.Worker.IsDead()) continue;

                if (entry != null && entry.Worker != null)
                    ByInstance.Remove(entry.Worker.GetInstanceID());
                LoadedWorkers.RemoveAt(i);
            }

            // Unity-destroyed objects compare equal to null, so stale dictionary entries can remain.
            // Rebuild infrequently to keep lookup bounded without per-frame allocations.
            ByInstance.Clear();
            for (int i = 0; i < LoadedWorkers.Count; i++)
            {
                Entry entry = LoadedWorkers[i];
                if (entry?.Worker == null) continue;
                ByInstance[entry.Worker.GetInstanceID()] = entry;
            }
        }
    }
}
