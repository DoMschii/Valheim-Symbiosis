using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    internal enum WoodTargetKind
    {
        None = 0,
        TreeBase = 1,
        TreeLog = 2,
        TreeStump = 3
    }

    /// <summary>
    /// A uniform wrapper over the three real Valheim chop targets that make up a complete tree job:
    /// standing TreeBase -> spawned TreeLog pieces -> Destructible stump.
    /// </summary>
    internal sealed class WoodTarget
    {
        internal readonly TreeBase Tree;
        internal readonly TreeLog Log;
        internal readonly Destructible Stump;
        internal readonly WoodTargetKind Kind;

        internal WoodTarget(TreeBase tree)
        {
            Tree = tree;
            Kind = WoodTargetKind.TreeBase;
        }

        internal WoodTarget(TreeLog log)
        {
            Log = log;
            Kind = WoodTargetKind.TreeLog;
        }

        internal WoodTarget(Destructible stump)
        {
            Stump = stump;
            Kind = WoodTargetKind.TreeStump;
        }

        internal GameObject GameObject
        {
            get
            {
                if (Tree != null) return Tree.gameObject;
                if (Log != null) return Log.gameObject;
                if (Stump != null) return Stump.gameObject;
                return null;
            }
        }

        internal Vector3 Position => GameObject != null ? GameObject.transform.position : Vector3.zero;

        internal int MinToolTier
        {
            get
            {
                if (Tree != null) return Tree.m_minToolTier;
                if (Log != null) return Log.m_minToolTier;
                if (Stump != null) return Stump.m_minToolTier;
                return int.MaxValue;
            }
        }

        internal string KindKey
        {
            get
            {
                switch (Kind)
                {
                    case WoodTargetKind.TreeLog: return "TreeLog";
                    case WoodTargetKind.TreeStump: return "TreeStump";
                    case WoodTargetKind.TreeBase: return "TreeBase";
                    default: return string.Empty;
                }
            }
        }

        internal string DisplayKind
        {
            get
            {
                switch (Kind)
                {
                    case WoodTargetKind.TreeLog: return "log";
                    case WoodTargetKind.TreeStump: return "stump";
                    case WoodTargetKind.TreeBase: return "tree";
                    default: return "wood target";
                }
            }
        }

        internal bool IsAliveAndNetworked()
        {
            GameObject go = GameObject;
            if (go == null || !go.activeInHierarchy) return false;

            ZNetView nview = go.GetComponent<ZNetView>() ?? go.GetComponentInParent<ZNetView>();
            return nview != null && nview.IsValid() && nview.GetZDO() != null;
        }

        internal Collider ResolveCollider()
        {
            GameObject go = GameObject;
            if (go == null) return null;

            Collider best = null;
            float bestVolume = -1f;
            Collider[] colliders = go.GetComponentsInChildren<Collider>(false);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || !collider.enabled || collider.isTrigger) continue;
                Vector3 size = collider.bounds.size;
                float volume = size.x * size.y * size.z;
                if (volume <= bestVolume) continue;
                best = collider;
                bestVolume = volume;
            }
            return best;
        }

        internal Vector3 ClosestPoint(Vector3 from)
        {
            Collider collider = ResolveCollider();
            if (collider == null) return Position;

            MeshCollider mesh = collider as MeshCollider;
            if (mesh != null && !mesh.convex)
                return collider.ClosestPointOnBounds(from);

            return ColliderGeometryRuntime.ClosestPointSafe(collider, from);
        }

        internal void Damage(HitData hit)
        {
            if (Tree != null)
            {
                Tree.Damage(hit);
                return;
            }
            if (Log != null)
            {
                Log.Damage(hit);
                return;
            }
            if (Stump != null)
                Stump.Damage(hit);
        }
    }

    internal readonly struct TreeTargetResult
    {
        internal readonly WoodTarget Target;
        internal readonly string PrefabName;
        internal readonly string TargetKind;
        internal readonly Vector3 Position;
        internal readonly int PhysicsHits;
        internal readonly int Candidates;
        internal readonly bool BufferFull;

        internal TreeTargetResult(WoodTarget target, string prefabName, string targetKind, Vector3 position, int physicsHits, int candidates, bool bufferFull)
        {
            Target = target;
            PrefabName = prefabName;
            TargetKind = targetKind;
            Position = position;
            PhysicsHits = physicsHits;
            Candidates = candidates;
            BufferFull = bufferFull;
        }
    }


    internal readonly struct WoodResourceScan
    {
        internal readonly int PhysicsHits;
        internal readonly int StandingTrees;
        internal readonly int ManagedStandingTrees;
        internal readonly int TierRejected;
        internal readonly bool BufferFull;

        internal WoodResourceScan(int physicsHits, int standingTrees, int managedStandingTrees, int tierRejected, bool bufferFull)
        {
            PhysicsHits = physicsHits;
            StandingTrees = standingTrees;
            ManagedStandingTrees = managedStandingTrees;
            TierRejected = tierRejected;
            BufferFull = bufferFull;
        }
    }

    internal static class TreeTargetFinder
    {
        // Local, allocation-free scan. No global FindObjectsOfType loops.
        private static readonly Collider[] ColliderBuffer = new Collider[2048];
        private static readonly HashSet<int> SeenInstanceIds = new HashSet<int>();
        private static readonly List<Vector3> ManagedForestrySlots = new List<Vector3>(192);

        internal static int LastManagedSlotCount { get; private set; }
        internal static int LastManagedTreeCandidates { get; private set; }
        internal static int LastTierRejectedCount { get; private set; }
        internal static int LastLowestRejectedTier { get; private set; }
        internal static string LastTierRejectedPrefab { get; private set; } = string.Empty;

        /// <summary>
        /// Complete-lumber priority deliberately prefers loose logs, then stumps, then standing trees.
        /// This makes a worker finish the tree it just felled instead of immediately walking to the next trunk.
        /// </summary>
        internal static TreeTargetResult FindNearest(Vector3 areaCenter, float workRadius, Vector3 workerPosition, int maxToolTier, ZDO workerZdo = null, Predicate<WoodTarget> targetFilter = null)
        {
            ManagedForestrySlots.Clear();
            LastManagedTreeCandidates = 0;
            LastTierRejectedCount = 0;
            LastLowestRejectedTier = 0;
            LastTierRejectedPrefab = string.Empty;
            if (workerZdo != null)
                ForestryPlanRuntime.AppendFulfilledSlotPositionsForWorker(workerZdo, ManagedForestrySlots);
            LastManagedSlotCount = ManagedForestrySlots.Count;

            float radius = Mathf.Max(5f, workRadius);
            float radiusSqr = radius * radius;
            int hitCount = Physics.OverlapSphereNonAlloc(areaCenter, radius, ColliderBuffer);
            bool bufferFull = hitCount >= ColliderBuffer.Length;

            SeenInstanceIds.Clear();
            WoodTarget bestTarget = null;
            string bestName = string.Empty;
            string bestKind = string.Empty;
            Vector3 bestPosition = default;
            int bestPriority = int.MaxValue;
            float bestSqr = float.MaxValue;
            int candidates = 0;

            for (int i = 0; i < hitCount; i++)
            {
                Collider collider = ColliderBuffer[i];
                ColliderBuffer[i] = null;
                if (collider == null) continue;

                WoodTarget target = ResolveFromCollider(collider);
                if (target == null || !target.IsAliveAndNetworked()) continue;
                if (targetFilter != null && !targetFilter(target)) continue;
                if (workerZdo != null && target.Kind == WoodTargetKind.TreeBase &&
                    !LumberjackTreeFilterRuntime.IsStandingTreeAllowed(workerZdo, target)) continue;

                GameObject go = target.GameObject;
                int instanceId = go.GetInstanceID();
                if (!SeenInstanceIds.Add(instanceId)) continue;

                Vector3 position = target.Position;
                if ((position - areaCenter).sqrMagnitude > radiusSqr) continue;
                if (target.MinToolTier > maxToolTier)
                {
                    LastTierRejectedCount++;
                    if (LastLowestRejectedTier == 0 || target.MinToolTier < LastLowestRejectedTier)
                    {
                        LastLowestRejectedTier = target.MinToolTier;
                        LastTierRejectedPrefab = NormalizePrefabName(go.name);
                    }
                    continue;
                }

                candidates++;
                bool managedStandingTree = target.Kind == WoodTargetKind.TreeBase && IsNearManagedForestrySlot(position);
                if (managedStandingTree) LastManagedTreeCandidates++;
                int priority = Priority(target.Kind, managedStandingTree);
                float sqr = (ColliderGeometryRuntime.ClosestPointSafe(target, workerPosition) - workerPosition).sqrMagnitude;
                if (priority > bestPriority || (priority == bestPriority && sqr >= bestSqr)) continue;

                bestTarget = target;
                bestPriority = priority;
                bestSqr = sqr;
                bestPosition = position;
                bestName = NormalizePrefabName(go.name);
                bestKind = target.KindKey;
            }

            SeenInstanceIds.Clear();
            ManagedForestrySlots.Clear();
            return new TreeTargetResult(bestTarget, bestName, bestKind, bestPosition, hitCount, candidates, bufferFull);
        }


        /// <summary>
        /// Finds only already-felled cleanup targets (logs / stumps). This is used when the
        /// persistent wood ledger is depleted: no new standing tree may be started, but a
        /// physically loaded tree cycle that was already begun must still be finished.
        /// </summary>
        internal static TreeTargetResult FindNearestCleanup(Vector3 areaCenter, float workRadius, Vector3 workerPosition, int maxToolTier, Predicate<WoodTarget> targetFilter = null)
        {
            float radius = Mathf.Max(5f, workRadius);
            float radiusSqr = radius * radius;
            int hitCount = Physics.OverlapSphereNonAlloc(areaCenter, radius, ColliderBuffer);
            bool bufferFull = hitCount >= ColliderBuffer.Length;

            SeenInstanceIds.Clear();
            WoodTarget bestTarget = null;
            string bestName = string.Empty;
            string bestKind = string.Empty;
            Vector3 bestPosition = default;
            int bestPriority = int.MaxValue;
            float bestSqr = float.MaxValue;
            int candidates = 0;

            for (int i = 0; i < hitCount; i++)
            {
                Collider collider = ColliderBuffer[i];
                ColliderBuffer[i] = null;
                if (collider == null) continue;

                WoodTarget target = ResolveFromCollider(collider);
                if (target == null || !target.IsAliveAndNetworked()) continue;
                if (target.Kind == WoodTargetKind.TreeBase) continue;
                if (targetFilter != null && !targetFilter(target)) continue;

                GameObject go = target.GameObject;
                int instanceId = go.GetInstanceID();
                if (!SeenInstanceIds.Add(instanceId)) continue;

                Vector3 position = target.Position;
                if ((position - areaCenter).sqrMagnitude > radiusSqr) continue;
                if (target.MinToolTier > maxToolTier) continue;

                candidates++;
                int priority = target.Kind == WoodTargetKind.TreeLog ? 0 : 1;
                float sqr = (ColliderGeometryRuntime.ClosestPointSafe(target, workerPosition) - workerPosition).sqrMagnitude;
                if (priority > bestPriority || (priority == bestPriority && sqr >= bestSqr)) continue;

                bestTarget = target;
                bestPriority = priority;
                bestSqr = sqr;
                bestPosition = position;
                bestName = NormalizePrefabName(go.name);
                bestKind = target.KindKey;
            }

            SeenInstanceIds.Clear();
            return new TreeTargetResult(bestTarget, bestName, bestKind, bestPosition, hitCount, candidates, bufferFull);
        }


        internal static WoodResourceScan ScanStandingTrees(Vector3 areaCenter, float workRadius, int maxToolTier, ZDO workerZdo = null)
        {
            ManagedForestrySlots.Clear();
            if (workerZdo != null) ForestryPlanRuntime.AppendFulfilledSlotPositionsForWorker(workerZdo, ManagedForestrySlots);

            float radius = Mathf.Max(5f, workRadius);
            float radiusSqr = radius * radius;
            int hitCount = Physics.OverlapSphereNonAlloc(areaCenter, radius, ColliderBuffer);
            bool bufferFull = hitCount >= ColliderBuffer.Length;
            int standing = 0;
            int managed = 0;
            int tierRejected = 0;

            SeenInstanceIds.Clear();
            for (int i = 0; i < hitCount; i++)
            {
                Collider collider = ColliderBuffer[i];
                ColliderBuffer[i] = null;
                if (collider == null) continue;
                WoodTarget target = ResolveFromCollider(collider, "TreeBase");
                if (target == null || !target.IsAliveAndNetworked()) continue;
                if (workerZdo != null && !LumberjackTreeFilterRuntime.IsStandingTreeAllowed(workerZdo, target)) continue;
                GameObject go = target.GameObject;
                int instanceId = go.GetInstanceID();
                if (!SeenInstanceIds.Add(instanceId)) continue;
                if ((target.Position - areaCenter).sqrMagnitude > radiusSqr) continue;
                if (target.MinToolTier > maxToolTier)
                {
                    tierRejected++;
                    continue;
                }
                standing++;
                if (IsNearManagedForestrySlot(target.Position)) managed++;
            }
            SeenInstanceIds.Clear();
            ManagedForestrySlots.Clear();
            return new WoodResourceScan(hitCount, standing, managed, tierRejected, bufferFull);
        }

        internal static bool IsUsable(WoodTarget target, Vector3 areaCenter, float workRadius, int maxToolTier, ZDO workerZdo = null)
        {
            if (target == null || !target.IsAliveAndNetworked()) return false;
            if (target.MinToolTier > maxToolTier) return false;
            if (workerZdo != null && target.Kind == WoodTargetKind.TreeBase &&
                !LumberjackTreeFilterRuntime.IsStandingTreeAllowed(workerZdo, target)) return false;

            float radius = Mathf.Max(5f, workRadius);
            return (target.Position - areaCenter).sqrMagnitude <= radius * radius;
        }

        internal static WoodTarget ResolveNear(Vector3 targetPosition, string targetName, string targetKind, float radius, int maxToolTier, ZDO workerZdo = null)
        {
            int hitCount = Physics.OverlapSphereNonAlloc(
                targetPosition,
                Mathf.Max(1f, radius),
                ColliderBuffer);

            WoodTarget nearest = null;
            float nearestSqr = float.MaxValue;

            for (int i = 0; i < hitCount; i++)
            {
                Collider collider = ColliderBuffer[i];
                ColliderBuffer[i] = null;
                if (collider == null) continue;

                WoodTarget target = ResolveFromCollider(collider, targetKind);
                if (target == null || !target.IsAliveAndNetworked()) continue;
                if (target.MinToolTier > maxToolTier) continue;
                if (workerZdo != null && target.Kind == WoodTargetKind.TreeBase &&
                    !LumberjackTreeFilterRuntime.IsStandingTreeAllowed(workerZdo, target)) continue;

                string prefabName = NormalizePrefabName(target.GameObject.name);
                if (!string.IsNullOrEmpty(targetName) && !string.Equals(prefabName, targetName, StringComparison.Ordinal))
                    continue;

                float sqr = (target.Position - targetPosition).sqrMagnitude;
                if (sqr >= nearestSqr) continue;
                nearestSqr = sqr;
                nearest = target;
            }

            for (int i = 0; i < hitCount; i++)
                ColliderBuffer[i] = null;

            return nearest;
        }

        private static WoodTarget ResolveFromCollider(Collider collider, string requiredKind = null)
        {
            if (collider == null) return null;

            if (string.IsNullOrEmpty(requiredKind) || string.Equals(requiredKind, "TreeLog", StringComparison.Ordinal))
            {
                TreeLog log = collider.GetComponentInParent<TreeLog>();
                if (log != null)
                    return string.IsNullOrEmpty(requiredKind) || string.Equals(requiredKind, "TreeLog", StringComparison.Ordinal)
                        ? new WoodTarget(log)
                        : null;
            }

            if (string.IsNullOrEmpty(requiredKind) || string.Equals(requiredKind, "TreeBase", StringComparison.Ordinal))
            {
                TreeBase tree = collider.GetComponentInParent<TreeBase>();
                if (tree != null)
                    return string.IsNullOrEmpty(requiredKind) || string.Equals(requiredKind, "TreeBase", StringComparison.Ordinal)
                        ? new WoodTarget(tree)
                        : null;
            }

            if (string.IsNullOrEmpty(requiredKind) || string.Equals(requiredKind, "TreeStump", StringComparison.Ordinal))
            {
                Destructible destructible = collider.GetComponentInParent<Destructible>();
                if (IsTreeStump(destructible))
                    return new WoodTarget(destructible);
            }

            return null;
        }

        internal static bool IsTreeStump(Destructible destructible)
        {
            if (destructible == null) return false;
            if (destructible.GetComponent<TreeBase>() != null || destructible.GetComponent<TreeLog>() != null) return false;

            string name = NormalizePrefabName(destructible.gameObject.name);
            return name.IndexOf("stub", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("stump", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsNearManagedForestrySlot(Vector3 treePosition)
        {
            // The planted sapling is written at the planned slot, but grown TreeBase prefabs may have
            // a small pivot offset. 2.6 m is intentionally below the normal 5 m forestry spacing, so
            // one tree cannot accidentally match a neighbouring planned slot.
            const float maxDistanceSqr = 2.6f * 2.6f;
            for (int i = 0; i < ManagedForestrySlots.Count; i++)
            {
                Vector3 delta = ManagedForestrySlots[i] - treePosition;
                delta.y = 0f;
                if (delta.sqrMagnitude <= maxDistanceSqr) return true;
            }
            return false;
        }

        private static int Priority(WoodTargetKind kind, bool managedStandingTree)
        {
            switch (kind)
            {
                // Finish already-felled wood before walking to the next standing tree.
                case WoodTargetKind.TreeLog: return 0;
                case WoodTargetKind.TreeStump: return 1;
                // Mature trees grown on persistent forestry slots are the managed crop.
                case WoodTargetKind.TreeBase: return managedStandingTree ? 2 : 3;
                default: return 99;
            }
        }

        internal static string NormalizePrefabName(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return string.Empty;
            int cloneIndex = objectName.IndexOf("(Clone)", StringComparison.Ordinal);
            if (cloneIndex >= 0) objectName = objectName.Substring(0, cloneIndex);
            int spaceIndex = objectName.IndexOf(' ');
            if (spaceIndex > 0) objectName = objectName.Substring(0, spaceIndex);
            return objectName.Trim();
        }
    }
}
