using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    internal sealed class GreydwarfRecruitmentRuntime
    {
        private const float ExistingCandidateRadius = 24f;
        private readonly Action<string> _debugLog;
        private readonly Collider[] _hits = new Collider[128];
        private readonly List<Character> _candidates = new List<Character>(16);
        private MethodInfo _getPrefabMethod;
        private bool _prefabLookupResolved;

        internal GreydwarfRecruitmentRuntime(Action<string> debugLog)
        {
            _debugLog = debugLog;
        }

        internal void EnforcePacifiedSpawner(SpawnArea spawner)
        {
            GreydwarfSpawnerSuppressionRuntime.TrySuppress(spawner, _debugLog);
        }

        internal bool TryReleaseFinalWorker(
            SpawnArea spawner,
            Player player,
            float workRadius,
            out Character worker,
            out string species,
            out string failure)
        {
            worker = null;
            species = string.Empty;
            failure = string.Empty;

            if (spawner == null || player == null)
            {
                failure = "nest or player unavailable";
                return false;
            }

            ZNetView nestView = GetNetworkView(spawner);
            if (nestView == null || !nestView.IsValid() || nestView.GetZDO() == null)
            {
                failure = "nest has no valid ZDO";
                return false;
            }

            if (!nestView.IsOwner())
            {
                failure = "nest is not locally owned";
                return false;
            }

            ZDO nestZdo = nestView.GetZDO();
            if (!SpawnerRecruitmentZdo.IsPacified(nestZdo))
            {
                failure = "nest is not pacified";
                return false;
            }

            if (SpawnerRecruitmentZdo.IsConsumed(nestZdo))
            {
                failure = "nest already consumed";
                return false;
            }

            EnforcePacifiedSpawner(spawner);

            // The first released creature is selected from the permanent offering mix. Once selected,
            // store it so a failed spawn/retry cannot reroll the result.
            species = SpawnerRecruitmentZdo.ReadSelectedSpecies(nestZdo);
            if (string.IsNullOrEmpty(species))
            {
                species = GreydwarfNestSummoningRuntime.ChooseSpecies(nestZdo);
                if (string.IsNullOrEmpty(species))
                {
                    failure = "nest has no saved offering weights";
                    return false;
                }
                SpawnerRecruitmentZdo.WriteSelectedSpecies(nestZdo, species);
            }

            // The pacified nest is now a real caller: it always releases a NEW creature of the
            // exact species selected by the saved offering mix. Nearby wild creatures are never
            // converted implicitly, so the ritual result cannot be stolen by whatever happens to
            // be standing next to the nest.

            GameObject prefab = ResolveNetworkPrefab(species);
            if (prefab == null)
            {
                failure = $"network prefab '{species}' not found";
                return false;
            }

            Vector3 spawnPosition = ChooseSpawnPosition(spawner.transform.position, player.transform.position);
            GameObject instance = UnityEngine.Object.Instantiate(prefab, spawnPosition, Quaternion.identity);
            if (instance == null)
            {
                failure = $"failed to instantiate '{species}'";
                return false;
            }

            Character character = instance.GetComponent<Character>();
            if (character == null) character = instance.GetComponentInChildren<Character>();
            if (character == null)
            {
                UnityEngine.Object.Destroy(instance);
                failure = $"spawned prefab '{species}' has no Character component";
                return false;
            }

            if (!RecruitCharacter(character, player, spawner.transform.position, workRadius, out failure))
            {
                ZNetView spawnedView = character.GetComponent<ZNetView>();
                if (spawnedView == null || !spawnedView.IsValid())
                    UnityEngine.Object.Destroy(instance);
                return false;
            }

            SpawnerRecruitmentZdo.MarkConsumed(nestZdo, species, player.GetPlayerID());
            worker = character;
            _debugLog?.Invoke($"Pacified nest released new vanilla {species} as first weighted worker.");
            return true;
        }

        internal bool TrySummonOwnedWorker(
            string prefabName,
            Player player,
            Vector3 summonOrigin,
            float workRadius,
            out Character worker,
            out string failure)
        {
            worker = null;
            failure = string.Empty;

            if (player == null || string.IsNullOrEmpty(prefabName))
            {
                failure = "player or species unavailable";
                return false;
            }

            if (!GreydwarfNestSummoningRuntime.IsSummonSpecies(prefabName))
            {
                failure = $"species '{prefabName}' is not part of the pacified-nest summon pool";
                return false;
            }

            GameObject prefab = ResolveNetworkPrefab(prefabName);
            if (prefab == null)
            {
                failure = $"network prefab '{prefabName}' not found";
                return false;
            }

            Vector3 spawnPosition = ChooseSpawnPosition(summonOrigin, player.transform.position);
            GameObject instance = UnityEngine.Object.Instantiate(prefab, spawnPosition, Quaternion.identity);
            if (instance == null)
            {
                failure = $"failed to instantiate '{prefabName}'";
                return false;
            }

            Character character = instance.GetComponent<Character>();
            if (character == null) character = instance.GetComponentInChildren<Character>();
            if (character == null)
            {
                UnityEngine.Object.Destroy(instance);
                failure = $"spawned prefab '{prefabName}' has no Character component";
                return false;
            }

            if (!RecruitCharacter(character, player, summonOrigin, workRadius, out failure))
            {
                ZNetView spawnedView = character.GetComponent<ZNetView>();
                if (spawnedView == null || !spawnedView.IsValid())
                    UnityEngine.Object.Destroy(instance);
                return false;
            }

            worker = character;
            _debugLog?.Invoke($"Pacified Greydwarf nest summoned {prefabName} as a recruited worker.");
            return true;
        }

        private Character FindExistingCandidate(Vector3 center, string desiredSpecies)
        {
            _candidates.Clear();
            int count = Physics.OverlapSphereNonAlloc(center, ExistingCandidateRadius, _hits);
            for (int i = 0; i < count; i++)
            {
                Collider hit = _hits[i];
                if (hit == null) continue;

                Character character = hit.GetComponentInParent<Character>();
                if (character == null || character is Player || character.IsDead()) continue;
                if (_candidates.Contains(character)) continue;

                string prefabName = SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name);
                if (!string.Equals(prefabName, desiredSpecies, StringComparison.OrdinalIgnoreCase)) continue;

                ZNetView nview = character.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid() || nview.GetZDO() == null || !nview.IsOwner()) continue;
                if (WorkerZdo.IsWorker(nview.GetZDO())) continue;

                _candidates.Add(character);
            }

            if (_candidates.Count == 0) return null;
            return _candidates[UnityEngine.Random.Range(0, _candidates.Count)];
        }

        private static bool IsGreydwarfFamilyResult(string prefabName)
        {
            string[] results = RecruitmentCatalog.GreydwarfFamily.ResultSpecies;
            for (int i = 0; i < results.Length; i++)
            {
                if (string.Equals(prefabName, results[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool RecruitCharacter(Character character, Player player, Vector3 home, float workRadius, out string failure)
        {
            failure = string.Empty;
            if (character == null || player == null)
            {
                failure = "worker or player unavailable";
                return false;
            }

            string prefabName = SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name);
            if (!SpeciesRoleRegistry.TryGetRole(prefabName, out WorkerRole role))
            {
                failure = $"species '{prefabName}' has no worker role";
                return false;
            }

            ZNetView nview = character.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid() || nview.GetZDO() == null)
            {
                failure = $"'{prefabName}' has no valid worker ZDO";
                return false;
            }

            if (!nview.IsOwner())
            {
                failure = $"'{prefabName}' is not locally owned";
                return false;
            }

            ZDO workerZdo = nview.GetZDO();
            WorkerZdo.WriteInitialState(workerZdo, player.GetPlayerID(), role, home, Mathf.Max(5f, workRadius));
            WorkerFastLookupRuntime.RegisterKnownWorker(character, workerZdo);
            character.SetTamed(true);
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
                    _debugLog?.Invoke("ZNetScene.GetPrefab(string) not available; final worker fallback spawning is disabled.");
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

        private static Vector3 ChooseSpawnPosition(Vector3 nestPosition, Vector3 playerPosition)
        {
            Vector3 away = nestPosition - playerPosition;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
            away.Normalize();
            return nestPosition + away * 3f + Vector3.up * 0.5f;
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
