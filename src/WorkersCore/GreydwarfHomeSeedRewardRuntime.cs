using System;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    internal sealed class GreydwarfHomeSeedRewardRuntime
    {
        internal const string AncientSeedPrefab = "AncientSeed";

        private readonly Action<string> _debugLog;
        private MethodInfo _getPrefabMethod;
        private bool _prefabLookupResolved;

        internal GreydwarfHomeSeedRewardRuntime(Action<string> debugLog)
        {
            _debugLog = debugLog;
        }

        internal bool TryGrant(SpawnArea spawner, Player player, ZDO nestZdo, out string failure)
        {
            failure = string.Empty;

            if (spawner == null || player == null || nestZdo == null)
            {
                failure = "nest, player or nest ZDO unavailable";
                return false;
            }

            if (!SpawnerRecruitmentZdo.IsPacified(nestZdo))
            {
                failure = "nest is not pacified yet";
                return false;
            }

            if (SpawnerRecruitmentZdo.IsHomeSeedGranted(nestZdo))
                return true;

            ZNetView nestView = GetNetworkView(spawner);
            if (nestView == null || !nestView.IsValid() || nestView.GetZDO() == null)
            {
                failure = "nest has no valid network state";
                return false;
            }

            if (!nestView.IsOwner())
            {
                failure = "nest is not locally owned";
                return false;
            }

            GameObject prefab = ResolveNetworkPrefab(AncientSeedPrefab);
            if (prefab == null)
            {
                failure = $"network prefab '{AncientSeedPrefab}' not found";
                return false;
            }

            // Release the physical seed slightly above and toward the player-facing side of the
            // pacified nest so it is visible and can fall naturally as a normal ItemDrop.
            Vector3 position = ChooseRewardPosition(spawner.transform.position, player.transform.position);
            GameObject instance = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
            if (instance == null)
            {
                failure = $"failed to instantiate '{AncientSeedPrefab}'";
                return false;
            }

            ItemDrop itemDrop = instance.GetComponent<ItemDrop>();
            if (itemDrop == null) itemDrop = instance.GetComponentInChildren<ItemDrop>();
            ZNetView seedView = instance.GetComponent<ZNetView>();
            if (seedView == null) seedView = instance.GetComponentInChildren<ZNetView>();

            if (itemDrop == null || seedView == null || !seedView.IsValid())
            {
                if (seedView == null || !seedView.IsValid())
                    UnityEngine.Object.Destroy(instance);

                failure = $"spawned '{AncientSeedPrefab}' is not a valid network ItemDrop";
                return false;
            }

            SpawnerRecruitmentZdo.MarkHomeSeedGranted(nestZdo, player.GetPlayerID());
            _debugLog?.Invoke($"Pacified Greydwarf nest released one {AncientSeedPrefab} home seed reward.");
            return true;
        }

        private GameObject ResolveNetworkPrefab(string prefabName)
        {
            ZNetScene scene = ZNetScene.instance;
            if (scene == null) return null;

            if (!_prefabLookupResolved)
            {
                _prefabLookupResolved = true;
                _getPrefabMethod = typeof(ZNetScene).GetMethod(
                    "GetPrefab",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(string) },
                    null);

                if (_getPrefabMethod == null)
                    _debugLog?.Invoke("ZNetScene.GetPrefab(string) not available; Ancient Seed reward is disabled.");
            }

            if (_getPrefabMethod == null) return null;

            try
            {
                return _getPrefabMethod.Invoke(scene, new object[] { prefabName }) as GameObject;
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Network prefab lookup failed for '{prefabName}': {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static Vector3 ChooseRewardPosition(Vector3 nestPosition, Vector3 playerPosition)
        {
            Vector3 towardPlayer = playerPosition - nestPosition;
            towardPlayer.y = 0f;
            if (towardPlayer.sqrMagnitude < 0.01f) towardPlayer = Vector3.forward;
            towardPlayer.Normalize();
            return nestPosition + towardPlayer * 1.6f + Vector3.up * 1.4f;
        }

        private static ZNetView GetNetworkView(SpawnArea spawner)
        {
            if (spawner == null) return null;
            ZNetView nview = spawner.GetComponent<ZNetView>();
            if (nview == null) nview = spawner.GetComponentInParent<ZNetView>();
            return nview;
        }
    }
}
