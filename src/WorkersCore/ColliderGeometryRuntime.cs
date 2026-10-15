using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Unity's Collider.ClosestPoint delegates to Physics.ClosestPoint, which only supports
    /// primitive colliders and convex MeshColliders. Calling it for Valheim's non-convex world
    /// meshes logs a warning every time and can become a severe per-frame log/FPS penalty.
    /// </summary>
    internal static class ColliderGeometryRuntime
    {
        // Wrapper targets already know which concrete collider/anchor represents the actual
        // work surface. Keep these overloads here so call sites can use the same safe API
        // without accidentally passing WoodTarget/StoneTarget to the Collider overload.
        internal static Vector3 ClosestPointSafe(WoodTarget target, Vector3 point)
        {
            return target != null ? target.ClosestPoint(point) : point;
        }

        internal static Vector3 ClosestPointSafe(StoneTarget target, Vector3 point)
        {
            return target != null ? target.ClosestPoint(point) : point;
        }

        internal static Vector3 ClosestPointSafe(Collider collider, Vector3 point)
        {
            if (collider == null) return point;

            bool supported = collider is BoxCollider ||
                             collider is SphereCollider ||
                             collider is CapsuleCollider;
            if (collider is MeshCollider mesh)
                supported = mesh.convex;

            if (!supported)
                return collider.ClosestPointOnBounds(point);

            try { return collider.ClosestPoint(point); }
            catch { return collider.ClosestPointOnBounds(point); }
        }
    }
}
