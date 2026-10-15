using System;
using System.Collections;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// One-shot, owner-authoritative world placement for lore stones around Greydwarf nests.
    /// The result is persisted on the nest ZDO; there is no proximity/world scan or repeating AI work.
    /// </summary>
    internal sealed class WorkersLoreNearbyStoneSpawner : MonoBehaviour
    {
        private const string SpawnMaskKey = "vw_lore_stones_v403";
        private const int BondStoneDone = 1;
        private const int FoodStoneDone = 2;
        private SpawnArea _spawner;
        private bool _started;

        internal void Initialize(SpawnArea spawner)
        {
            if (spawner != null) _spawner = spawner;
            BeginOnce();
        }

        private void Awake()
        {
            if (_spawner == null)
                _spawner = GetComponent<SpawnArea>() ?? GetComponentInParent<SpawnArea>();
        }

        private void Start()
        {
            BeginOnce();
        }

        private void BeginOnce()
        {
            if (_started || !gameObject.activeInHierarchy) return;
            _started = true;
            StartCoroutine(TrySpawnWhenReady());
        }

        private IEnumerator TrySpawnWhenReady()
        {
            // Prefab registration and ZDO ownership can settle a little after the scene object wakes.
            // Twenty cheap one-shot retries are preferable to any permanent Update/poll loop.
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (TryHandleLoreStones()) yield break;
                yield return new WaitForSeconds(1f);
            }
        }

        private bool TryHandleLoreStones()
        {
            if (_spawner == null)
                _spawner = GetComponent<SpawnArea>() ?? GetComponentInParent<SpawnArea>();
            if (_spawner == null) return false;

            ZNetView view = _spawner.GetComponent<ZNetView>() ?? _spawner.GetComponentInParent<ZNetView>();
            if (view == null || !view.IsValid() || view.GetZDO() == null || !view.IsOwner()) return false;

            ZDO zdo = view.GetZDO();
            int mask = zdo.GetInt(SpawnMaskKey, 0);
            int seed = StableNestSeed(_spawner.transform.position);

            // Every nest gets the recruitment tablet, but at a deterministic random angle/distance,
            // so it feels like an old world object rather than a UI sign glued to the spawner.
            if ((mask & BondStoneDone) == 0)
            {
                if (!TrySpawnStone(WorkersLorePage.GreydwarfBond, seed ^ 0x13A5B7, 10f, 22f))
                    return false;
                mask |= BondStoneDone;
                zdo.Set(SpawnMaskKey, mask);
            }

            // Food lore is rarer: roughly 45% of nests receive a second, more distant tablet.
            if ((mask & FoodStoneDone) == 0)
            {
                bool shouldSpawnFood = Positive(seed ^ unchecked((int)0x6C8E9CF5)) % 100 < 45;
                if (shouldSpawnFood && !TrySpawnStone(WorkersLorePage.FoodAndSupply, seed ^ 0x2D41C3, 18f, 32f))
                    return false;

                mask |= FoodStoneDone; // also records the deterministic "no second stone" decision.
                zdo.Set(SpawnMaskKey, mask);
            }

            return (mask & (BondStoneDone | FoodStoneDone)) == (BondStoneDone | FoodStoneDone);
        }

        private bool TrySpawnStone(WorkersLorePage page, int seed, float minRadius, float maxRadius)
        {
            if (!WorkersLoreRuntime.TryGetWorldLorePrefab(page, out GameObject prefab) || prefab == null)
                return false;

            Vector3 center = _spawner.transform.position;
            float unit = (Positive(seed) % 10000) / 9999f;
            float radius = Mathf.Lerp(minRadius, maxRadius, unit);
            float baseAngle = (Positive(seed ^ unchecked((int)0x9E3779B9)) % 36000) / 100f;

            for (int i = 0; i < 10; i++)
            {
                float angle = (baseAngle + i * 61f) * Mathf.Deg2Rad;
                float candidateRadius = radius + (i % 3) * 1.5f;
                Vector3 point = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * candidateRadius;

                if (ZoneSystem.instance != null)
                {
                    float ground = ZoneSystem.instance.GetGroundHeight(point);
                    if (ground <= ZoneSystem.instance.m_waterLevel + 0.35f) continue;
                    point.y = ground + 0.03f;
                }
                else
                {
                    point.y = center.y;
                }

                Quaternion rotation = Quaternion.Euler(0f, (baseAngle + 180f + i * 17f) % 360f, 0f);
                GameObject instance = UnityEngine.Object.Instantiate(prefab, point, rotation);
                if (instance == null) continue;

                ZNetView spawnedView = instance.GetComponent<ZNetView>() ?? instance.GetComponentInChildren<ZNetView>();
                if (spawnedView == null || !spawnedView.IsValid())
                {
                    UnityEngine.Object.Destroy(instance);
                    continue;
                }

                return true;
            }

            return false;
        }

        private static int StableNestSeed(Vector3 position)
        {
            unchecked
            {
                int x = Mathf.RoundToInt(position.x * 10f);
                int y = Mathf.RoundToInt(position.y * 10f);
                int z = Mathf.RoundToInt(position.z * 10f);
                int h = 17;
                h = h * 31 + x;
                h = h * 31 + y;
                h = h * 31 + z;
                return h;
            }
        }

        private static int Positive(int value)
        {
            if (value == int.MinValue) return int.MaxValue;
            return Math.Abs(value);
        }
    }
}
