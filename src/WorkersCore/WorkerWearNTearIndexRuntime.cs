using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    internal static class WorkerWearNTearIndexRuntime
    {
        private static readonly Dictionary<int, WearNTear> Loaded = new Dictionary<int, WearNTear>(2048);
        private static readonly List<int> DeadIds = new List<int>(64);
        private static bool _bootstrapped;

        internal static int LoadedCount
        {
            get
            {
                EnsureBootstrapped();
                return Loaded.Count;
            }
        }

        internal static void Register(WearNTear value)
        {
            if (value == null) return;
            try { Loaded[value.GetInstanceID()] = value; }
            catch { }
        }

        internal static int CopyInRadius(Vector3 center, float radius, List<WearNTear> destination)
        {
            if (destination == null) return 0;
            destination.Clear();
            EnsureBootstrapped();

            float safeRadius = Mathf.Max(0.5f, radius);
            float radiusSqr = safeRadius * safeRadius;
            DeadIds.Clear();

            foreach (KeyValuePair<int, WearNTear> pair in Loaded)
            {
                WearNTear value = pair.Value;
                if (value == null || value.gameObject == null)
                {
                    DeadIds.Add(pair.Key);
                    continue;
                }

                if (!value.gameObject.activeInHierarchy) continue;
                if ((value.transform.position - center).sqrMagnitude > radiusSqr) continue;
                destination.Add(value);
            }

            for (int i = 0; i < DeadIds.Count; i++)
                Loaded.Remove(DeadIds[i]);
            DeadIds.Clear();

            return destination.Count;
        }

        internal static void RebuildLoadedState()
        {
            Loaded.Clear();
            DeadIds.Clear();
            _bootstrapped = false;
            EnsureBootstrapped();
        }

        internal static void Clear()
        {
            Loaded.Clear();
            DeadIds.Clear();
            _bootstrapped = false;
        }

        private static void EnsureBootstrapped()
        {
            if (_bootstrapped) return;
            _bootstrapped = true;

            try
            {
                WearNTear[] loaded = Resources.FindObjectsOfTypeAll<WearNTear>();
                if (loaded == null) return;

                for (int i = 0; i < loaded.Length; i++)
                {
                    WearNTear value = loaded[i];
                    if (value == null || value.gameObject == null || !value.gameObject.activeInHierarchy) continue;
                    Register(value);
                }
            }
            catch
            {
                // New structures are still registered through WearNTear.Awake.
            }
        }
    }

    [HarmonyPatch(typeof(WearNTear), "Awake")]
    internal static class WorkerWearNTearIndexAwakePatch
    {
        private static void Postfix(WearNTear __instance)
        {
            WorkerWearNTearIndexRuntime.Register(__instance);
        }
    }
}
