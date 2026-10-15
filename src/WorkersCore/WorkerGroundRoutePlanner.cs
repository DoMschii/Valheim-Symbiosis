using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Bounded A* fallback used only after Worker direct steering has genuinely stalled.
    /// The normal v0.1.80+ locomotion remains direct Character steering. This planner exists only
    /// to find a small chain of walkable detour points around real obstacles such as base walls,
    /// corners and narrow passages.
    ///
    /// v0.1.94 keeps the finer 1.5 m grid than the old 3 m grid. A 3 m grid could skip
    /// ordinary Valheim door/opening geometry entirely and the previous 24-node-per-side budget
    /// was too small to walk around even a modest building wall. The search is still globally
    /// throttled by WorkerNavigationRuntime and therefore never runs continuously per Worker.
    /// </summary>
    internal static class WorkerGroundRoutePlanner
    {
        private const float CellSize = 1.5f;
        private const float MaxStepUp = 1.35f;
        private const float MaxStepDown = 2.25f;
        private const int MaxExpanded = 240;
        private const float MinCorridorHalfWidth = 12f;
        private const float MaxCorridorHalfWidth = 36f;
        private const float CorridorWidthFactor = 0.55f;
        private const float GoalVisibilityDistance = 22f;
        private const float BodyHalfWidth = 0.30f;
        private const float GroundProbeUp = 1.65f;
        private const float GroundProbeDown = 4.75f;
        // Only forgive the collider immediately at the target itself. v0.1.92/93 incorrectly
        // used the gameplay stop distance (often 3 m) as raycast forgiveness. A wall sitting
        // within those final 3 m was therefore treated as if the direct path were clear.
        private const float TerminalColliderAllowance = 0.55f;

        private static readonly RaycastHit[] SegmentHits = new RaycastHit[24];
        private static readonly RaycastHit[] GroundHits = new RaycastHit[16];
        private static readonly Collider[] ClearanceHits = new Collider[24];

        private static readonly GridKey[] Neighbours =
        {
            new GridKey(1, 0), new GridKey(-1, 0), new GridKey(0, 1), new GridKey(0, -1),
            new GridKey(1, 1), new GridKey(1, -1), new GridKey(-1, 1), new GridKey(-1, -1)
        };

        private readonly struct GridKey : IEquatable<GridKey>
        {
            internal readonly int X;
            internal readonly int Z;
            internal GridKey(int x, int z) { X = x; Z = z; }
            internal GridKey Add(GridKey other) => new GridKey(X + other.X, Z + other.Z);
            public bool Equals(GridKey other) => X == other.X && Z == other.Z;
            public override bool Equals(object obj) => obj is GridKey other && Equals(other);
            public override int GetHashCode() => unchecked((X * 397) ^ Z);
        }

        private sealed class Node
        {
            internal GridKey Key;
            internal Vector3 Position;
            internal float G;
            internal float F;
            internal GridKey Parent;
            internal bool HasParent;
            internal bool Closed;
        }

        internal static bool TryBuildRoute(Vector3 start, Vector3 destination, float stopDistance, out List<Vector3> waypoints, out int expanded)
        {
            waypoints = null;
            expanded = 0;

            Vector3 flat = destination - start;
            flat.y = 0f;
            float directDistance = flat.magnitude;
            if (directDistance < 3.25f) return false;

            Vector3 startGround;
            Vector3 goalGround;
            if (!TrySampleGround(start, start.y, out startGround)) startGround = start;
            if (!TrySampleGround(destination, destination.y, out goalGround)) goalGround = destination;

            // If the line really is free there is no useful detour to build. The caller reached us
            // because goal progress stalled, so letting direct locomotion retry is cheaper than
            // creating a fake route on open ground.
            // The gameplay stop radius is NOT obstacle forgiveness. A Worker may stop a few
            // metres from a target, but it still must not consider a wall near that target
            // transparent. Only forgive a tiny terminal slice for the target's own collider.
            float terminalAllowance = TerminalColliderAllowance;
            if (SegmentClear(startGround, goalGround, terminalAllowance)) return false;

            GridKey startKey = ToKey(startGround);
            GridKey goalKey = ToKey(goalGround);
            if (startKey.Equals(goalKey)) return false;

            Vector3 corridorDir = goalGround - startGround;
            corridorDir.y = 0f;
            float corridorLength = corridorDir.magnitude;
            if (corridorLength < 0.1f) return false;
            corridorDir /= corridorLength;
            float corridorHalfWidth = Mathf.Clamp(
                corridorLength * CorridorWidthFactor,
                MinCorridorHalfWidth,
                MaxCorridorHalfWidth);

            var nodes = new Dictionary<GridKey, Node>(384);
            var open = new List<GridKey>(192);

            Node startNode = new Node
            {
                Key = startKey,
                Position = startGround,
                G = 0f,
                F = Heuristic(startGround, goalGround),
                HasParent = false,
                Closed = false
            };
            nodes[startKey] = startNode;
            open.Add(startKey);

            GridKey reachedKey = default;
            bool found = false;

            while (open.Count > 0 && expanded < MaxExpanded)
            {
                int bestIndex = FindBestOpenIndex(open, nodes);
                if (bestIndex < 0) break;

                GridKey currentKey = open[bestIndex];
                open.RemoveAt(bestIndex);
                if (!nodes.TryGetValue(currentKey, out Node current) || current.Closed) continue;
                current.Closed = true;
                expanded++;

                // The exact gameplay target can sit beside a chest/tree/plant collider. We do not
                // need a grid cell on the target itself: as soon as a node has a body-clear line to
                // it, the normal Worker steering can finish the final segment.
                float goalDistance = Vector3.Distance(current.Position, goalGround);
                // A node is only a valid hand-off point when it has a genuinely clear body-line
                // to the target. Do not accept "within stopDistance" by itself: that is exactly
                // how a Worker on the wrong side of a wall could be considered close enough.
                if (goalDistance <= GoalVisibilityDistance &&
                    SegmentClear(current.Position, goalGround, terminalAllowance))
                {
                    reachedKey = currentKey;
                    found = true;
                    break;
                }

                for (int i = 0; i < Neighbours.Length; i++)
                {
                    GridKey nextKey = currentKey.Add(Neighbours[i]);
                    Vector3 xz = KeyToXZ(nextKey, current.Position.y);

                    if (!InsideCorridor(xz, startGround, corridorDir, corridorLength, corridorHalfWidth)) continue;
                    if (!TrySampleGround(xz, current.Position.y, out Vector3 nextPosition)) continue;

                    float vertical = nextPosition.y - current.Position.y;
                    if (vertical > MaxStepUp || vertical < -MaxStepDown) continue;
                    if (!PointHasBodyClearance(nextPosition)) continue;
                    if (!SegmentClear(current.Position, nextPosition)) continue;

                    float step = Vector3.Distance(current.Position, nextPosition);
                    // Very small penalty for diagonal steps near geometry. This is not intended to
                    // optimise distance perfectly; it simply discourages corner-hugging routes.
                    bool diagonal = Neighbours[i].X != 0 && Neighbours[i].Z != 0;
                    float tentativeG = current.G + step + (diagonal ? 0.08f : 0f);

                    if (nodes.TryGetValue(nextKey, out Node existing))
                    {
                        if (tentativeG >= existing.G - 0.01f) continue;
                        existing.Position = nextPosition;
                        existing.G = tentativeG;
                        existing.F = tentativeG + Heuristic(nextPosition, goalGround);
                        existing.Parent = currentKey;
                        existing.HasParent = true;
                        if (existing.Closed) existing.Closed = false;
                        if (!open.Contains(nextKey)) open.Add(nextKey);
                        continue;
                    }

                    Node next = new Node
                    {
                        Key = nextKey,
                        Position = nextPosition,
                        G = tentativeG,
                        F = tentativeG + Heuristic(nextPosition, goalGround),
                        Parent = currentKey,
                        HasParent = true,
                        Closed = false
                    };
                    nodes[nextKey] = next;
                    open.Add(nextKey);
                }
            }

            if (!found) return false;

            List<Vector3> raw = BuildPath(nodes, reachedKey, startGround, goalGround);
            if (raw == null || raw.Count < 2) return false;

            waypoints = Simplify(raw);
            return waypoints != null && waypoints.Count > 0;
        }

        internal static bool IsDirectPathClear(Vector3 start, Vector3 destination)
        {
            Vector3 startGround;
            Vector3 goalGround;
            if (!TrySampleGround(start, start.y, out startGround)) startGround = start;
            if (!TrySampleGround(destination, destination.y, out goalGround)) goalGround = destination;
            return SegmentClear(startGround, goalGround, TerminalColliderAllowance);
        }

        private static int FindBestOpenIndex(List<GridKey> open, Dictionary<GridKey, Node> nodes)
        {
            int bestIndex = -1;
            float bestF = float.MaxValue;
            float bestG = float.MaxValue;
            for (int i = 0; i < open.Count; i++)
            {
                if (!nodes.TryGetValue(open[i], out Node candidate) || candidate.Closed) continue;
                if (candidate.F > bestF + 0.001f) continue;
                if (Mathf.Abs(candidate.F - bestF) <= 0.001f && candidate.G >= bestG) continue;
                bestF = candidate.F;
                bestG = candidate.G;
                bestIndex = i;
            }
            return bestIndex;
        }

        private static List<Vector3> BuildPath(
            Dictionary<GridKey, Node> nodes,
            GridKey reached,
            Vector3 start,
            Vector3 goal)
        {
            var reverse = new List<Vector3>(64);
            GridKey key = reached;
            int guard = 0;
            while (nodes.TryGetValue(key, out Node node) && guard++ < MaxExpanded + 4)
            {
                reverse.Add(node.Position);
                if (!node.HasParent) break;
                key = node.Parent;
            }
            if (reverse.Count == 0) return null;
            reverse.Reverse();

            var result = new List<Vector3>(reverse.Count + 2) { start };
            for (int i = 0; i < reverse.Count; i++)
            {
                if ((reverse[i] - result[result.Count - 1]).sqrMagnitude > 0.16f)
                    result.Add(reverse[i]);
            }
            if ((goal - result[result.Count - 1]).sqrMagnitude > 0.16f)
                result.Add(goal);
            return result;
        }

        private static List<Vector3> Simplify(List<Vector3> raw)
        {
            if (raw == null || raw.Count <= 2)
            {
                if (raw != null && raw.Count > 1) raw.RemoveAt(0);
                return raw;
            }

            // Keep long, body-clear segments. A detour around a wall should usually become only a
            // few real corners rather than a 1.5 m waypoint chain.
            var result = new List<Vector3>(raw.Count);
            int current = 0;
            while (current < raw.Count - 1)
            {
                int best = current + 1;
                for (int test = raw.Count - 1; test > current + 1; test--)
                {
                    if (Vector3.Distance(raw[current], raw[test]) > 20f) continue;
                    if (Mathf.Abs(raw[test].y - raw[current].y) > 2.5f) continue;
                    if (!SegmentClear(raw[current], raw[test])) continue;
                    best = test;
                    break;
                }
                result.Add(raw[best]);
                current = best;
            }
            return result;
        }

        private static bool InsideCorridor(Vector3 point, Vector3 start, Vector3 dir, float length, float halfWidth)
        {
            Vector3 rel = point - start;
            rel.y = 0f;
            float along = Vector3.Dot(rel, dir);

            // Allow space behind both endpoints. This matters when a Worker is flush against a wall:
            // the first valid move can briefly be sideways/backwards before the route turns around it.
            float endSlack = Mathf.Max(CellSize * 3f, halfWidth * 0.35f);
            if (along < -endSlack || along > length + endSlack) return false;

            Vector3 projected = dir * along;
            float side = (rel - projected).magnitude;
            return side <= halfWidth;
        }

        private static bool TrySampleGround(Vector3 point, float referenceY, out Vector3 ground)
        {
            Vector3 origin = new Vector3(point.x, referenceY + GroundProbeUp, point.z);
            int hitCount = Physics.RaycastNonAlloc(
                origin,
                Vector3.down,
                GroundHits,
                GroundProbeUp + GroundProbeDown,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);

            bool found = false;
            float bestScore = float.MaxValue;
            Vector3 bestPoint = default;

            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit hit = GroundHits[i];
                GroundHits[i] = default;
                Collider collider = hit.collider;
                if (collider == null) continue;
                if (ShouldIgnoreCollider(collider)) continue;
                if (hit.normal.y < 0.45f) continue;

                float vertical = hit.point.y - referenceY;
                if (vertical > MaxStepUp + 0.15f || vertical < -MaxStepDown - 0.75f) continue;

                // Prefer the floor/foundation nearest the Worker's current elevation. RaycastAll is
                // intentional here: when working inside a roofed base, the first downward hit can be
                // the roof rather than the floor.
                float score = Mathf.Abs(vertical);
                if (vertical > 0f) score += 0.05f;
                if (score >= bestScore) continue;
                bestScore = score;
                bestPoint = hit.point + Vector3.up * 0.05f;
                found = true;
            }

            ground = bestPoint;
            return found;
        }

        private static bool PointHasBodyClearance(Vector3 position)
        {
            Vector3 bottom = position + Vector3.up * 0.48f;
            Vector3 top = position + Vector3.up * 1.35f;
            int count = Physics.OverlapCapsuleNonAlloc(
                bottom,
                top,
                BodyHalfWidth,
                ClearanceHits,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
            {
                Collider collider = ClearanceHits[i];
                ClearanceHits[i] = null;
                if (collider == null || ShouldIgnoreCollider(collider)) continue;
                return false;
            }
            return true;
        }

        private static bool SegmentClear(Vector3 from, Vector3 to)
            => SegmentClear(from, to, 0f);

        private static bool SegmentClear(Vector3 from, Vector3 to, float endAllowance)
        {
            Vector3 flat = to - from;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.04f) return true;

            Vector3 horizontalDir = flat.normalized;
            Vector3 side = Vector3.Cross(Vector3.up, horizontalDir) * BodyHalfWidth;

            // Three lower-body rays approximate Worker width, plus one upper ray. This is cheap,
            // deterministic and much less likely than the old single centre ray to accept a route
            // that merely clips the edge of a wall.
            if (!RayClear(from + Vector3.up * 0.78f, to + Vector3.up * 0.78f, endAllowance)) return false;
            if (!RayClear(from + side + Vector3.up * 0.78f, to + side + Vector3.up * 0.78f, endAllowance)) return false;
            if (!RayClear(from - side + Vector3.up * 0.78f, to - side + Vector3.up * 0.78f, endAllowance)) return false;
            if (!RayClear(from + Vector3.up * 1.38f, to + Vector3.up * 1.38f, endAllowance)) return false;
            return true;
        }

        private static bool RayClear(Vector3 from, Vector3 to, float endAllowance)
        {
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance < 0.12f) return true;
            Vector3 dir = delta / distance;

            int hits = Physics.RaycastNonAlloc(
                from,
                dir,
                SegmentHits,
                distance,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < hits; i++)
            {
                RaycastHit hit = SegmentHits[i];
                SegmentHits[i] = default;
                Collider collider = hit.collider;
                if (collider == null || ShouldIgnoreCollider(collider)) continue;
                if (endAllowance > 0f && hit.distance >= distance - endAllowance) continue;
                return false;
            }
            return true;
        }

        private static bool ShouldIgnoreCollider(Collider collider)
        {
            if (collider == null) return true;
            if (collider.GetComponentInParent<Character>() != null) return true;
            if (collider.GetComponentInParent<ItemDrop>() != null) return true;
            if (collider.GetComponentInParent<Pickable>() != null) return true;
            return false;
        }

        private static GridKey ToKey(Vector3 position)
            => new GridKey(Mathf.RoundToInt(position.x / CellSize), Mathf.RoundToInt(position.z / CellSize));

        private static Vector3 KeyToXZ(GridKey key, float y)
            => new Vector3(key.X * CellSize, y, key.Z * CellSize);

        private static float Heuristic(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }
    }
}
