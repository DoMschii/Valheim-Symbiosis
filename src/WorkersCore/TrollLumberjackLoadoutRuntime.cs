using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Keeps recruited Troll lumberjacks on Valheim's native log-wielding Troll loadout.
    /// Vanilla Trolls randomly choose between an unarmed attack set and the two LOG swing attacks;
    /// Workers' Tier-3 lumberjack always uses the latter.
    /// </summary>
    internal static class TrollLumberjackLoadoutRuntime
    {
        private const string LogSwingVertical = "troll_log_swing_v";
        private const string LogSwingHorizontal = "troll_log_swing_h";
        private const string Punch = "troll_punch";
        private const string GroundSlam = "troll_groundslam";
        private const string Throw = "troll_throw";

        private static readonly HashSet<int> ReadyInstances = new HashSet<int>();
        private static readonly MethodInfo GiveDefaultItemMethod = typeof(Humanoid).GetMethod(
            "GiveDefaultItem",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(GameObject) },
            null);
        private static readonly MethodInfo SetupEquipmentMethod = typeof(Humanoid).GetMethod(
            "SetupEquipment",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);

        private static readonly MethodInfo ZNetSceneGetPrefabMethod = typeof(ZNetScene).GetMethod(
            "GetPrefab",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(string) },
            null);

        private static Action<string> _debugLog;

        internal static void Initialize(Action<string> debugLog)
        {
            _debugLog = debugLog;
            ReadyInstances.Clear();
        }

        internal static void Shutdown()
        {
            ReadyInstances.Clear();
            _debugLog = null;
        }

        internal static void EnsureLoadedWorker(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null || !WorkerZdo.IsWorker(zdo)) return;
            if ((WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None) != WorkerRole.Lumberjack) return;
            if (!string.Equals(SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name), "Troll", StringComparison.OrdinalIgnoreCase)) return;

            int key = worker.GetInstanceID();
            if (ReadyInstances.Contains(key)) return;

            ZNetView nview = worker.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;

            Humanoid humanoid = worker as Humanoid ?? worker.GetComponent<Humanoid>();
            if (humanoid == null) return;

            if (!TryConfigureNativeLogSet(humanoid, out GameObject vertical, out GameObject horizontal))
                return;

            Inventory inventory = humanoid.GetInventory();
            if (inventory == null) return;

            bool hasVertical = false;
            bool hasHorizontal = false;
            List<ItemDrop.ItemData> items = inventory.GetAllItems();
            for (int i = items.Count - 1; i >= 0; i--)
            {
                ItemDrop.ItemData item = items[i];
                string prefab = item?.m_dropPrefab != null ? item.m_dropPrefab.name : string.Empty;
                if (string.Equals(prefab, LogSwingVertical, StringComparison.OrdinalIgnoreCase))
                {
                    hasVertical = true;
                    continue;
                }
                if (string.Equals(prefab, LogSwingHorizontal, StringComparison.OrdinalIgnoreCase))
                {
                    hasHorizontal = true;
                    continue;
                }

                // A Workers Troll is deliberately the log variant, not the random vanilla unarmed set.
                if (string.Equals(prefab, Punch, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prefab, GroundSlam, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prefab, Throw, StringComparison.OrdinalIgnoreCase))
                {
                    inventory.RemoveItem(item);
                }
            }

            if (GiveDefaultItemMethod == null)
            {
                _debugLog?.Invoke("Troll lumberjack loadout: Humanoid.GiveDefaultItem(GameObject) unavailable; cannot migrate loaded Troll.");
                return;
            }

            try
            {
                if (!hasVertical)
                    GiveDefaultItemMethod.Invoke(humanoid, new object[] { vertical });
                if (!hasHorizontal)
                    GiveDefaultItemMethod.Invoke(humanoid, new object[] { horizontal });

                SetupEquipmentMethod?.Invoke(humanoid, null);
                ReadyInstances.Add(key);
                _debugLog?.Invoke("Troll lumberjack loadout enforced: native LOG swing set equipped.");
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Troll lumberjack loadout migration failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        internal static void PrepareDefaultItems(Humanoid humanoid)
        {
            if (humanoid == null) return;

            ZNetView nview = humanoid.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            ZDO zdo = nview.GetZDO();
            if (zdo == null || !WorkerZdo.IsWorker(zdo)) return;
            if ((WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None) != WorkerRole.Lumberjack) return;
            if (!string.Equals(SpeciesRoleRegistry.NormalizePrefabName(humanoid.gameObject.name), "Troll", StringComparison.OrdinalIgnoreCase)) return;

            TryConfigureNativeLogSet(humanoid, out _, out _);
        }

        private static bool TryConfigureNativeLogSet(Humanoid humanoid, out GameObject vertical, out GameObject horizontal)
        {
            vertical = ResolvePrefab(LogSwingVertical);
            horizontal = ResolvePrefab(LogSwingHorizontal);
            if (vertical == null || horizontal == null)
            {
                _debugLog?.Invoke($"Troll lumberjack loadout: native log attacks not resolved (v={(vertical != null)}, h={(horizontal != null)}).");
                return false;
            }

            // Do not touch Humanoid.m_randomSets here. The concrete random-set element type
            // differs between Valheim builds and is not exposed as a stable public ItemSet type.
            // Loaded Workers Trolls are normalized in EnsureLoadedWorker(): vanilla unarmed attack
            // items are removed, the two native LOG attack items are added, and equipment is rebuilt.
            // This keeps the implementation compatible with the current assembly_valheim API.
            return true;
        }

        private static GameObject ResolvePrefab(string prefabName)
        {
            ZNetScene scene = ZNetScene.instance;
            if (scene != null && ZNetSceneGetPrefabMethod != null)
            {
                try
                {
                    GameObject prefab = ZNetSceneGetPrefabMethod.Invoke(scene, new object[] { prefabName }) as GameObject;
                    if (prefab != null) return prefab;
                }
                catch
                {
                    // Fall through to ObjectDB.
                }
            }

            ObjectDB db = ObjectDB.instance;
            if (db != null)
            {
                try
                {
                    return db.GetItemPrefab(prefabName);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }
    }

    [HarmonyPatch(typeof(Humanoid), "GiveDefaultItems")]
    internal static class TrollLumberjackGiveDefaultItemsPatch
    {
        private static void Prefix(Humanoid __instance)
        {
            TrollLumberjackLoadoutRuntime.PrepareDefaultItems(__instance);
        }
    }
}
