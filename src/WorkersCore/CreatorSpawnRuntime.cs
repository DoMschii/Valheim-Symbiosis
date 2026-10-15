using System;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Explicit creator/debug spawning only. This is intentionally separate from normal recruitment:
    /// it creates one real vanilla network prefab, assigns it to the local player as a Worker and
    /// starts it with the configured default Worker combat stance.
    /// </summary>
    internal sealed class CreatorSpawnRuntime
    {

        private readonly Action<string> _debugLog;
        private MethodInfo _getPrefabMethod;
        private bool _prefabLookupResolved;

        internal CreatorSpawnRuntime(Action<string> debugLog)
        {
            _debugLog = debugLog;
        }

        internal bool TrySpawnWorker(
            string prefabName,
            Player player,
            float workRadius,
            WorkerStance stance,
            out Character worker,
            out string failure)
        {
            worker = null;
            failure = string.Empty;

            if (player == null)
            {
                failure = "no local player";
                return false;
            }

            if (!SpeciesRoleRegistry.TryGetRole(prefabName, out WorkerRole role))
            {
                failure = $"species '{prefabName}' has no Worker role mapping";
                return false;
            }

            // Translate Workers' canonical species names to Valheim's vanilla network prefab names.
            string canonicalSpecies = SpeciesRoleRegistry.NormalizePrefabName(prefabName);
            string networkPrefabName;
            if (string.Equals(canonicalSpecies, "Drake", StringComparison.OrdinalIgnoreCase)) networkPrefabName = "Hatchling";
            else if (string.Equals(canonicalSpecies, "Fuling", StringComparison.OrdinalIgnoreCase)) networkPrefabName = "Goblin";
            else if (string.Equals(canonicalSpecies, "Fuling_Shaman", StringComparison.OrdinalIgnoreCase)) networkPrefabName = "GoblinShaman";
            else if (string.Equals(canonicalSpecies, "Fuling_Berserker", StringComparison.OrdinalIgnoreCase)) networkPrefabName = "GoblinBrute";
            else networkPrefabName = prefabName;

            GameObject prefab = ResolveNetworkPrefab(networkPrefabName);
            if (prefab == null)
            {
                failure = $"network prefab '{networkPrefabName}' (Workers species '{prefabName}') not found";
                return false;
            }

            Vector3 forward = player.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();

            bool largeSpawn = string.Equals(prefabName, "Lox", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(prefabName, "Troll", StringComparison.OrdinalIgnoreCase);
            bool airSpawn = string.Equals(prefabName, "Drake", StringComparison.OrdinalIgnoreCase);
            float spawnDistance = largeSpawn ? 6f : (airSpawn ? 5f : 3f);
            float spawnHeight = airSpawn ? 3.0f : 0.35f;
            Vector3 spawnPosition = player.transform.position + forward * spawnDistance + Vector3.up * spawnHeight;
            Quaternion rotation = Quaternion.LookRotation(-forward, Vector3.up);
            GameObject instance = UnityEngine.Object.Instantiate(prefab, spawnPosition, rotation);
            if (instance == null)
            {
                failure = $"failed to instantiate '{prefabName}'";
                return false;
            }

            // The vanilla Draugr prefab randomly chooses between three axe entries and one bow entry.
            // A Draugr worker is specifically our Tier-2 lumberjack, so creator/debug spawns must not
            // accidentally become the ranged variant. Override the instance loadout before Start/
            // SetupEquipment runs; do not mutate the shared vanilla prefab.
            if (string.Equals(prefabName, "Draugr", StringComparison.OrdinalIgnoreCase))
            {
                Humanoid draugrHumanoid = instance.GetComponent<Humanoid>();
                if (draugrHumanoid == null) draugrHumanoid = instance.GetComponentInChildren<Humanoid>();
                GameObject axePrefab = ResolveNetworkPrefab("draugr_axe");
                if (draugrHumanoid != null && axePrefab != null)
                {
                    draugrHumanoid.m_randomWeapon = new[] { axePrefab };
                    _debugLog?.Invoke("Creator mode: forced Draugr lumberjack loadout to draugr_axe.");
                }
                else
                {
                    _debugLog?.Invoke("Creator mode: could not force Draugr axe loadout; Humanoid or draugr_axe prefab missing.");
                }
            }

            Character character = instance.GetComponent<Character>();
            if (character == null) character = instance.GetComponentInChildren<Character>();
            if (character == null)
            {
                UnityEngine.Object.Destroy(instance);
                failure = $"spawned prefab '{prefabName}' has no Character component";
                return false;
            }

            ZNetView nview = character.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid() || nview.GetZDO() == null)
            {
                UnityEngine.Object.Destroy(instance);
                failure = $"spawned prefab '{prefabName}' has no valid ZNetView/ZDO";
                return false;
            }

            if (!nview.IsOwner())
            {
                // Do not mutate a newly spawned Worker if this client is not authoritative.
                // A normal local ZNetScene spawn should be locally owned; fail safely otherwise.
                failure = $"spawned prefab '{prefabName}' is not locally owned";
                return false;
            }

            ZDO zdo = nview.GetZDO();
            WorkerZdo.WriteInitialState(
                zdo,
                player.GetPlayerID(),
                role,
                spawnPosition,
                Mathf.Max(5f, workRadius),
                progressionEligible: false);
            string spawnedSpecies = SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name);
            if (spawnedSpecies.StartsWith("Fuling", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(spawnedSpecies, "Dverger", StringComparison.OrdinalIgnoreCase))
            {
                // Creator-mode Dvergr gets a temporary local camp anchor until a dedicated
                // Dvergr guard/hunter rest piece and recruitment path are implemented.
                WorkerZdo.AssignHome(zdo, spawnPosition, WorkerHomeProfileCatalog.FulingCampKind);
            }
            zdo.Set(WorkerZdo.Stance, (int)stance);
            WorkerFastLookupRuntime.RegisterKnownWorker(character, zdo);

            character.SetTamed(true);
            worker = character;
            _debugLog?.Invoke($"Creator mode spawned {prefabName} as {role}, stance={WorkerCombatStanceRuntime.DisplayName(stance)}, owner={player.GetPlayerID()}.");
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
                    _debugLog?.Invoke("Creator mode: ZNetScene.GetPrefab(string) is unavailable; debug spawning is disabled.");
            }

            if (_getPrefabMethod == null) return null;

            try
            {
                return _getPrefabMethod.Invoke(scene, new object[] { prefabName }) as GameObject;
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Creator mode prefab lookup failed for '{prefabName}': {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }
}
