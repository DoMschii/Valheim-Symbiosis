using System;
using UnityEngine;

namespace Valheim_Workers
{
    internal sealed class GreydwarfSpawnerProbeRuntime
    {
        internal const float TickSeconds = 2.0f;

        private readonly Action<string> _debugLog;
        private readonly System.Collections.Generic.List<SpawnArea> _spawnAreaScratch = new System.Collections.Generic.List<SpawnArea>(32);
        private float _nextTick;

        internal SpawnArea NearestSpawner { get; private set; }
        internal float NearestDistance { get; private set; } = -1f;
        internal string PrefabName { get; private set; } = string.Empty;
        internal bool HasValidNetworkState { get; private set; }
        internal bool IsLocalOwner { get; private set; }
        internal int Progress { get; private set; }
        internal int Required { get; private set; } = SpawnerRecruitmentZdo.DefaultRequiredProgress;
        internal bool Pacified { get; private set; }
        internal bool Consumed { get; private set; }
        internal bool HomeSeedGranted { get; private set; }
        internal string ScanMode { get; private set; } = "none";
        internal int PhysicsHitCount { get; private set; }
        internal int LoadedSpawnAreaCount { get; private set; }

        internal GreydwarfSpawnerProbeRuntime(Action<string> debugLog)
        {
            _debugLog = debugLog;
        }

        internal void Tick(float radius)
        {
            if (Time.unscaledTime < _nextTick) return;
            _nextTick = Time.unscaledTime + TickSeconds;

            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Clear();
                return;
            }

            Scan(player.transform.position, Mathf.Clamp(radius, 5f, 80f));
        }

        private void Scan(Vector3 center, float radius)
        {
            SpawnArea best = null;
            float bestSqr = float.MaxValue;
            string bestName = string.Empty;

            // v0.5.452: SpawnArea discovery is lifecycle-indexed. The previous probe used a
            // collider pass every 2 s plus Resources.FindObjectsOfTypeAll every 30 s when the
            // nest had no useful collider. The registry handles both collider and collider-less
            // nests without any recurring Unity world-discovery fallback.
            PhysicsHitCount = 0;
            LoadedSpawnAreaCount = WorkerSpawnerIndexRuntime.CopySpawnAreasInRadius(center, radius, _spawnAreaScratch);

            for (int i = 0; i < _spawnAreaScratch.Count; i++)
            {
                SpawnArea spawnArea = _spawnAreaScratch[i];
                TryConsider(spawnArea, center, radius, ref best, ref bestSqr, ref bestName);
            }

            _spawnAreaScratch.Clear();

            if (best != null)
            {
                Apply(best, bestSqr, bestName, "spawner index");
                return;
            }

            ClearObservedOnly();
            ScanMode = $"none (indexedSpawnAreas={LoadedSpawnAreaCount})";
        }

        private static void TryConsider(
            SpawnArea spawnArea,
            Vector3 center,
            float radius,
            ref SpawnArea best,
            ref float bestSqr,
            ref string bestName)
        {
            if (spawnArea == null) return;

            string identity = GetGreydwarfSpawnerIdentity(spawnArea);
            if (string.IsNullOrEmpty(identity)) return;

            float sqr = (spawnArea.transform.position - center).sqrMagnitude;
            if (sqr > radius * radius || sqr >= bestSqr) return;

            bestSqr = sqr;
            best = spawnArea;
            bestName = identity;
        }

        private static string GetGreydwarfSpawnerIdentity(SpawnArea spawnArea)
        {
            Transform current = spawnArea.transform;
            for (int depth = 0; current != null && depth < 6; depth++, current = current.parent)
            {
                string normalized = SpeciesRoleRegistry.NormalizePrefabName(current.gameObject.name);
                if (RecruitmentCatalog.IsGreydwarfSpawner(normalized))
                    return normalized;
            }

            return string.Empty;
        }

        private void Apply(SpawnArea best, float bestSqr, string prefabName, string scanMode)
        {
            bool changed = best != NearestSpawner || !string.Equals(scanMode, ScanMode, StringComparison.Ordinal);
            NearestSpawner = best;
            NearestDistance = Mathf.Sqrt(bestSqr);
            PrefabName = prefabName;
            ScanMode = scanMode;

            ZNetView nview = best.GetComponent<ZNetView>();
            if (nview == null) nview = best.GetComponentInParent<ZNetView>();

            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            HasValidNetworkState = zdo != null;
            IsLocalOwner = nview != null && nview.IsValid() && nview.IsOwner();
            Progress = SpawnerRecruitmentZdo.ReadProgress(zdo);
            Required = Mathf.Max(1, SpawnerRecruitmentZdo.ReadRequired(zdo));
            Pacified = SpawnerRecruitmentZdo.IsPacified(zdo);
            Consumed = SpawnerRecruitmentZdo.IsConsumed(zdo);
            HomeSeedGranted = SpawnerRecruitmentZdo.IsHomeSeedGranted(zdo);

            GreydwarfNestInteractable interactable = best.GetComponent<GreydwarfNestInteractable>();
            if (interactable == null) interactable = best.gameObject.AddComponent<GreydwarfNestInteractable>();
            interactable.Initialize(best);

            WorkersLoreNearbyStoneSpawner loreSpawner = best.GetComponent<WorkersLoreNearbyStoneSpawner>();
            if (loreSpawner == null) loreSpawner = best.gameObject.AddComponent<WorkersLoreNearbyStoneSpawner>();
            loreSpawner.Initialize(best);

            if (changed)
                _debugLog?.Invoke($"Greydwarf recruitment probe found {PrefabName} at {NearestDistance:0.0}m via {ScanMode}; ZDO={(HasValidNetworkState ? "valid" : "missing")}; owner={(IsLocalOwner ? "LOCAL" : "REMOTE/none")}.");
        }

        internal string GetNetworkStatus()
        {
            if (NearestSpawner == null) return "none";
            if (!HasValidNetworkState) return "NO ZDO";
            return IsLocalOwner ? "LOCAL" : "REMOTE";
        }

        private void ClearObservedOnly()
        {
            NearestSpawner = null;
            NearestDistance = -1f;
            PrefabName = string.Empty;
            HasValidNetworkState = false;
            IsLocalOwner = false;
            Progress = 0;
            Required = SpawnerRecruitmentZdo.DefaultRequiredProgress;
            Pacified = false;
            Consumed = false;
            HomeSeedGranted = false;
        }

        private void Clear()
        {
            PhysicsHitCount = 0;
            LoadedSpawnAreaCount = 0;
            ScanMode = "none";
            ClearObservedOnly();
        }
    }
}
