using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    internal sealed class HunterStandMarker : MonoBehaviour
    {
        internal const float MinimumElevation = 2f;
        internal const string StandPointName = "Workers_HunterStandPoint";
        private static readonly List<HunterStandMarker> Loaded = new List<HunterStandMarker>();
        private static readonly RaycastHit[] GroundHits = new RaycastHit[24];
        private Transform _standPoint;
        private ZNetView _view;
        private Collider[] _geometryColliders;
        private bool _geometryDirty = true;
        private bool _elevationCacheReady;
        private bool _elevationCacheValid;
        private float _cachedElevation;
        private int _initialSyncAttempts;

        internal Transform StandPoint
        {
            get
            {
                EnsureStandPoint();
                EnsureGeometryCurrent();
                return _standPoint;
            }
        }

        private void Awake()
        {
            _view = GetComponent<ZNetView>() ?? GetComponentInParent<ZNetView>();
            EnsureStandPoint();
            _geometryDirty = true;
            RefreshStandPointFromGeometry(true);
            if (!Loaded.Contains(this)) Loaded.Add(this);
        }

        private void OnEnable()
        {
            if (_view == null) _view = GetComponent<ZNetView>() ?? GetComponentInParent<ZNetView>();
            EnsureStandPoint();
            _geometryDirty = true;
            RefreshStandPointFromGeometry(true);
            if (!Loaded.Contains(this)) Loaded.Add(this);

            // v0.5.449: save/load recovery gets a few bounded one-shot retries instead
            // of a permanent 1-second Update on every HunterStand.
            _initialSyncAttempts = 0;
            CancelInvoke(nameof(InitialHomeSync));
            Invoke(nameof(InitialHomeSync), 0.35f);
        }

        private void OnTransformChildrenChanged()
        {
            _geometryDirty = true;
            _elevationCacheReady = false;
        }

        private void OnTransformParentChanged()
        {
            _geometryDirty = true;
            _elevationCacheReady = false;
        }

        private void InitialHomeSync()
        {
            _initialSyncAttempts++;
            if (TrySyncAssignedWorkerHome() || _initialSyncAttempts >= 3) return;
            Invoke(nameof(InitialHomeSync), 1.0f);
        }

        private void EnsureStandPoint()
        {
            if (_standPoint != null) return;

            _standPoint = transform.Find(StandPointName);
            if (_standPoint == null)
            {
                GameObject go = new GameObject(StandPointName);
                _standPoint = go.transform;
                _standPoint.SetParent(transform, false);
                _standPoint.localPosition = new Vector3(0f, 0.18f, 0f);
            }
            _geometryDirty = true;
        }

        private void EnsureGeometryCurrent()
        {
            if (_geometryDirty || transform.hasChanged)
                RefreshStandPointFromGeometry(false);
        }


        private void RefreshStandPointFromGeometry(bool force)
        {
            EnsureStandPoint();
            if (_standPoint == null) return;

            bool transformChanged = transform.hasChanged;
            if (!force && !_geometryDirty && !transformChanged) return;

            if (_geometryColliders == null || _geometryDirty)
                _geometryColliders = GetComponentsInChildren<Collider>(true);

            bool haveBounds = false;
            Bounds bounds = new Bounds(transform.position, Vector3.zero);
            if (_geometryColliders != null)
            {
                for (int i = 0; i < _geometryColliders.Length; i++)
                {
                    Collider collider = _geometryColliders[i];
                    if (collider == null || collider.isTrigger) continue;

                    // Only use geometry that belongs to this actual HunterStand piece.
                    // Nearby stairs/railings are separate ZNetViews and must not shift the center.
                    ZNetView owner = collider.GetComponentInParent<ZNetView>();
                    if (_view != null && owner != null && owner != _view) continue;

                    if (!haveBounds)
                    {
                        bounds = collider.bounds;
                        haveBounds = true;
                    }
                    else bounds.Encapsulate(collider.bounds);
                }
            }

            _geometryDirty = false;
            transform.hasChanged = false;
            _elevationCacheReady = false;

            if (!haveBounds) return;

            Vector3 world = new Vector3(bounds.center.x, bounds.max.y + 0.04f, bounds.center.z);
            if (!force && (_standPoint.position - world).sqrMagnitude < 0.0004f) return;
            _standPoint.position = world;
        }

        private bool TrySyncAssignedWorkerHome()
        {
            if (_view == null) _view = GetComponent<ZNetView>() ?? GetComponentInParent<ZNetView>();
            if (_view == null || !_view.IsValid()) return false;
            if (!_view.IsOwner()) return true;

            ZDO homeZdo = _view.GetZDO();
            if (homeZdo == null) return false;
            if (!WorkerHomeAnchorZdo.IsAssigned(homeZdo)) return true;

            ZDOID workerId = WorkerHomeAnchorZdo.ReadAssignedWorkerZdoId(homeZdo);
            if (workerId.Equals(ZDOID.None)) return true;
            if (ZDOMan.instance == null) return false;

            ZDO workerZdo = ZDOMan.instance.GetZDO(workerId);
            if (!WorkerZdo.IsWorker(workerZdo)) return false;

            Vector3 p = StandPoint.position;
            Vector3 old = WorkerZdo.ReadHome(workerZdo, p);
            Vector3 flat = old - p;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.0004f && Mathf.Abs(old.y - p.y) < 0.02f) return true;

            workerZdo.Set(WorkerZdo.HomeX, p.x);
            workerZdo.Set(WorkerZdo.HomeY, p.y);
            workerZdo.Set(WorkerZdo.HomeZ, p.z);
            return true;
        }

        internal static void NotifyHomeLinked(ZDOID homeId)
        {
            if (homeId.Equals(ZDOID.None)) return;

            for (int i = Loaded.Count - 1; i >= 0; i--)
            {
                HunterStandMarker marker = Loaded[i];
                if (marker == null)
                {
                    Loaded.RemoveAt(i);
                    continue;
                }

                if (marker._view == null)
                    marker._view = marker.GetComponent<ZNetView>() ?? marker.GetComponentInParent<ZNetView>();
                ZDO zdo = marker._view != null && marker._view.IsValid() ? marker._view.GetZDO() : null;
                if (zdo == null || !zdo.m_uid.Equals(homeId)) continue;

                marker.RefreshStandPointFromGeometry(false);
                marker.TrySyncAssignedWorkerHome();
                return;
            }
        }

        private void OnDisable()
        {
            CancelInvoke(nameof(InitialHomeSync));
            Loaded.Remove(this);
        }

        private void OnDestroy()
        {
            CancelInvoke(nameof(InitialHomeSync));
            Loaded.Remove(this);
        }

        internal bool TryGetElevationAboveTerrain(out float elevation)
        {
            elevation = 0f;
            Transform stand = StandPoint;
            if (stand == null) return false;

            if (_elevationCacheReady)
            {
                elevation = _cachedElevation;
                return _elevationCacheValid;
            }

            Vector3 origin = stand.position + Vector3.up * 0.5f;
            int count = Physics.RaycastNonAlloc(origin, Vector3.down, GroundHits, 30f, ~0, QueryTriggerInteraction.Ignore);
            float nearestTerrainDistance = float.MaxValue;
            float terrainY = 0f;
            for (int i = 0; i < count; i++)
            {
                Collider collider = GroundHits[i].collider;
                if (collider == null || collider.GetComponentInParent<Heightmap>() == null) continue;
                float distance = GroundHits[i].distance;
                if (distance >= nearestTerrainDistance) continue;
                nearestTerrainDistance = distance;
                terrainY = GroundHits[i].point.y;
            }

            if (nearestTerrainDistance == float.MaxValue)
            {
                // Do not permanently cache an early load-boundary miss.
                _elevationCacheReady = false;
                _elevationCacheValid = false;
                return false;
            }

            _cachedElevation = stand.position.y - terrainY;
            _elevationCacheValid = true;
            _elevationCacheReady = true;
            elevation = _cachedElevation;
            return true;
        }

        internal bool IsValidElevation(out float elevation) =>
            TryGetElevationAboveTerrain(out elevation) && elevation >= MinimumElevation - 0.05f;

        internal static bool TryFindValid(Vector3 center, float radius, out Vector3 point)
        {
            point = Vector3.zero;
            float best = float.MaxValue;
            for (int i = Loaded.Count - 1; i >= 0; i--)
            {
                HunterStandMarker marker = Loaded[i];
                if (marker == null)
                {
                    Loaded.RemoveAt(i);
                    continue;
                }
                ZNetView view = marker.GetComponent<ZNetView>();
                if (view == null || !view.IsValid() || view.GetZDO() == null) continue;
                Transform stand = marker.StandPoint;
                if (stand == null || !marker.IsValidElevation(out _)) continue;
                Vector3 delta = stand.position - center;
                delta.y = 0f;
                if (delta.sqrMagnitude > radius * radius) continue;
                float sqr = delta.sqrMagnitude;
                if (sqr >= best) continue;
                best = sqr;
                point = stand.position;
            }
            return best < float.MaxValue;
        }

        internal static int LoadedCount => Loaded.Count;
    }
}
