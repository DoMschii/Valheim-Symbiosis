using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Shared lifecycle index for loaded world spawners.
    ///
    /// Previous recruitment probes used Resources.FindObjectsOfTypeAll as a fallback when a
    /// spawner had no useful collider. In large worlds those fallback walks can be expensive
    /// and, more importantly, were repeated. v0.5.452 bootstraps once and then keeps the index
    /// current from spawner lifecycle hooks.
    /// </summary>
    internal static class WorkerSpawnerIndexRuntime
    {
        private static readonly HashSet<SpawnArea> SpawnAreas = new HashSet<SpawnArea>();
        private static readonly HashSet<CreatureSpawner> CreatureSpawners = new HashSet<CreatureSpawner>();
        private static readonly List<SpawnArea> DeadSpawnAreas = new List<SpawnArea>(16);
        private static readonly List<CreatureSpawner> DeadCreatureSpawners = new List<CreatureSpawner>(16);
        private static bool _bootstrapped;

        internal static int SpawnAreaCount
        {
            get
            {
                EnsureBootstrapped();
                return SpawnAreas.Count;
            }
        }

        internal static int CreatureSpawnerCount
        {
            get
            {
                EnsureBootstrapped();
                return CreatureSpawners.Count;
            }
        }

        internal static void Register(SpawnArea spawner)
        {
            if (spawner == null || spawner.gameObject == null) return;
            SpawnAreas.Add(spawner);
        }

        internal static void Register(CreatureSpawner spawner)
        {
            if (spawner == null || spawner.gameObject == null) return;
            CreatureSpawners.Add(spawner);
        }

        internal static int CopySpawnAreasInRadius(Vector3 center, float radius, List<SpawnArea> destination)
        {
            if (destination == null) return 0;
            destination.Clear();
            EnsureBootstrapped();

            float safeRadius = Mathf.Max(0.5f, radius);
            float radiusSqr = safeRadius * safeRadius;
            DeadSpawnAreas.Clear();

            foreach (SpawnArea spawner in SpawnAreas)
            {
                if (spawner == null || spawner.gameObject == null)
                {
                    DeadSpawnAreas.Add(spawner);
                    continue;
                }

                GameObject go = spawner.gameObject;
                if (!go.activeInHierarchy || !go.scene.IsValid() || !go.scene.isLoaded) continue;
                if ((spawner.transform.position - center).sqrMagnitude > radiusSqr) continue;
                destination.Add(spawner);
            }

            for (int i = 0; i < DeadSpawnAreas.Count; i++)
                SpawnAreas.Remove(DeadSpawnAreas[i]);
            DeadSpawnAreas.Clear();

            return destination.Count;
        }

        internal static int CopyCreatureSpawnersInRadius(Vector3 center, float radius, List<CreatureSpawner> destination)
        {
            if (destination == null) return 0;
            destination.Clear();
            EnsureBootstrapped();

            float safeRadius = Mathf.Max(0.5f, radius);
            float radiusSqr = safeRadius * safeRadius;
            DeadCreatureSpawners.Clear();

            foreach (CreatureSpawner spawner in CreatureSpawners)
            {
                if (spawner == null || spawner.gameObject == null)
                {
                    DeadCreatureSpawners.Add(spawner);
                    continue;
                }

                GameObject go = spawner.gameObject;
                if (!go.activeInHierarchy || !go.scene.IsValid() || !go.scene.isLoaded) continue;
                if ((spawner.transform.position - center).sqrMagnitude > radiusSqr) continue;
                destination.Add(spawner);
            }

            for (int i = 0; i < DeadCreatureSpawners.Count; i++)
                CreatureSpawners.Remove(DeadCreatureSpawners[i]);
            DeadCreatureSpawners.Clear();

            return destination.Count;
        }

        internal static void RebuildLoadedState()
        {
            SpawnAreas.Clear();
            CreatureSpawners.Clear();
            DeadSpawnAreas.Clear();
            DeadCreatureSpawners.Clear();
            _bootstrapped = false;
            EnsureBootstrapped();
        }

        internal static void Clear()
        {
            SpawnAreas.Clear();
            CreatureSpawners.Clear();
            DeadSpawnAreas.Clear();
            DeadCreatureSpawners.Clear();
            _bootstrapped = false;
        }

        private static void EnsureBootstrapped()
        {
            if (_bootstrapped) return;
            _bootstrapped = true;

            // Compatibility bootstrap for spawners whose lifecycle ran before this runtime was
            // initialized. This full Unity-object walk is one-shot, never a recurring probe.
            try
            {
                SpawnArea[] areas = Resources.FindObjectsOfTypeAll<SpawnArea>();
                if (areas != null)
                {
                    for (int i = 0; i < areas.Length; i++)
                    {
                        SpawnArea spawner = areas[i];
                        if (spawner == null || spawner.gameObject == null) continue;
                        if (!spawner.gameObject.scene.IsValid() || !spawner.gameObject.scene.isLoaded) continue;
                        Register(spawner);
                    }
                }
            }
            catch { }

            try
            {
                CreatureSpawner[] spawners = Resources.FindObjectsOfTypeAll<CreatureSpawner>();
                if (spawners != null)
                {
                    for (int i = 0; i < spawners.Length; i++)
                    {
                        CreatureSpawner spawner = spawners[i];
                        if (spawner == null || spawner.gameObject == null) continue;
                        if (!spawner.gameObject.scene.IsValid() || !spawner.gameObject.scene.isLoaded) continue;
                        Register(spawner);
                    }
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(SpawnArea), "Awake")]
    internal static class WorkerSpawnerIndexSpawnAreaAwakePatch
    {
        private static void Postfix(SpawnArea __instance)
        {
            WorkerSpawnerIndexRuntime.Register(__instance);
        }
    }

    [HarmonyPatch(typeof(CreatureSpawner), "Awake")]
    internal static class WorkerSpawnerIndexCreatureSpawnerAwakePatch
    {
        private static void Postfix(CreatureSpawner __instance)
        {
            WorkerSpawnerIndexRuntime.Register(__instance);
        }
    }
}
