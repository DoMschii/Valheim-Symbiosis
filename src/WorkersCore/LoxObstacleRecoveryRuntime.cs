using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Narrow recovery layer for the Heavy Hauler Lox. The stable Workers navigator remains
    /// authoritative. Only after genuine lack of progress do we probe the body-width corridor in
    /// the travel direction; if the blocker is a natural destructible (tree/log/stump/rock/boulder),
    /// the Lox may use its native attack. This never selects buildings or containers and therefore
    /// cannot proactively clear a base or choose natural objects as normal work targets.
    /// </summary>
    internal static class LoxObstacleRecoveryRuntime
    {
        private sealed class Track
        {
            internal Vector3 LastPosition;
            internal float LastProgressAt;
            internal float NextProbeAt;
            internal float NextAttackAt;
            internal bool Initialized;
            internal string Status = "idle";
        }

        private static readonly Dictionary<int, Track> Tracks = new Dictionary<int, Track>();
        private static readonly RaycastHit[] Hits = new RaycastHit[48];
        private static readonly Collider[] OverlapHits = new Collider[48];
        private static bool _enabled = true;
        private static float _stuckSeconds = 6f;

        internal static void Configure(bool enabled, float stuckSeconds)
        {
            _enabled = enabled;
            _stuckSeconds = Mathf.Clamp(stuckSeconds, 3f, 20f);
        }

        internal static void Tick(Character worker, ZDO zdo)
        {
            if (!_enabled || worker == null || zdo == null) return;
            if (!string.Equals(SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name), "Lox", StringComparison.OrdinalIgnoreCase)) return;
            if ((WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None) != WorkerRole.Hauler) return;
            if (!WorkerZdo.TryReadWorkArea(zdo, out WorkOrder order, out _, out _) || order != WorkOrder.Haul) return;

            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            bool travelling = state == WorkerState.MoveToTarget || state == WorkerState.Deliver || state == WorkerState.ReturnHome;
            int key = worker.GetInstanceID();
            if (!Tracks.TryGetValue(key, out Track track))
            {
                track = new Track();
                Tracks[key] = track;
            }

            if (!travelling || !WorkerZdo.TryReadTarget(zdo, out _, out _, out Vector3 target))
            {
                track.Initialized = false;
                track.Status = "idle/not travelling";
                return;
            }

            Vector3 current = worker.transform.position;
            if (!track.Initialized)
            {
                track.Initialized = true;
                track.LastPosition = current;
                track.LastProgressAt = Time.time;
                track.NextProbeAt = Time.time + 0.75f;
                track.Status = "tracking";
                return;
            }

            Vector3 moved = current - track.LastPosition;
            moved.y = 0f;
            if (moved.sqrMagnitude >= 0.64f || worker.GetVelocity().magnitude > 0.75f)
            {
                track.LastPosition = current;
                track.LastProgressAt = Time.time;
                track.Status = "progressing";
                return;
            }

            if (Time.time < track.NextProbeAt) return;
            track.NextProbeAt = Time.time + 0.5f;

            float stuckFor = Time.time - track.LastProgressAt;
            if (stuckFor < _stuckSeconds)
            {
                track.Status = $"slow {stuckFor:0.0}/{_stuckSeconds:0.0}s";
                return;
            }
            if (Time.time < track.NextAttackAt) return;

            Vector3 dir = target - current;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.04f) return;
            dir.Normalize();

            Vector3 origin = current + Vector3.up * 1.15f;
            Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;

            // The Lox is much wider than a humanoid worker. Probe a short corridor across its
            // shoulders as well as the centre line so trees/rocks clipping a flank are detected.
            Vector3[] origins =
            {
                origin,
                origin + right * 1.15f,
                origin - right * 1.15f,
                origin + right * 0.55f,
                origin - right * 0.55f
            };
            Vector3[] directions =
            {
                dir,
                (dir + right * 0.22f).normalized,
                (dir - right * 0.22f).normalized
            };

            Collider bestCollider = null;
            string bestBlocker = string.Empty;
            Vector3 bestPoint = current + dir;
            float bestSqr = float.MaxValue;

            for (int oi = 0; oi < origins.Length; oi++)
            {
                for (int di = 0; di < directions.Length; di++)
                {
                    int hitCount = Physics.SphereCastNonAlloc(origins[oi], 0.95f, directions[di], Hits, 4.75f, Physics.AllLayers, QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < hitCount; i++)
                    {
                        Collider collider = Hits[i].collider;
                        if (collider == null) continue;
                        if (!IsNaturalBlocker(collider, out string blocker)) continue;

                        Vector3 point = Hits[i].point != Vector3.zero ? Hits[i].point : ColliderGeometryRuntime.ClosestPointSafe(collider, current);
                        float sqr = (point - current).sqrMagnitude;
                        if (sqr >= bestSqr) continue;
                        bestSqr = sqr;
                        bestCollider = collider;
                        bestBlocker = blocker;
                        bestPoint = point;
                    }
                }
            }

            // If the obstacle is already touching the Lox body, a forward cast can start beyond
            // the relevant collider. A small overlap volume catches shoulder/horn/body snags.
            Vector3 overlapCenter = current + dir * 1.7f + Vector3.up * 1.0f;
            int overlapCount = Physics.OverlapBoxNonAlloc(overlapCenter, new Vector3(1.9f, 1.45f, 2.2f), OverlapHits,
                Quaternion.LookRotation(dir, Vector3.up), Physics.AllLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < overlapCount; i++)
            {
                Collider collider = OverlapHits[i];
                OverlapHits[i] = null;
                if (collider == null) continue;
                if (!IsNaturalBlocker(collider, out string blocker)) continue;

                Vector3 point = ColliderGeometryRuntime.ClosestPointSafe(collider, current + dir * 1.5f);
                float sqr = (point - current).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                bestCollider = collider;
                bestBlocker = blocker;
                bestPoint = point;
            }

            if (bestCollider != null)
            {
                Vector3 attackDir = bestPoint - current;
                attackDir.y = 0f;
                if (attackDir.sqrMagnitude < 0.04f) attackDir = dir;
                attackDir.Normalize();
                worker.transform.rotation = Quaternion.LookRotation(attackDir, Vector3.up);

                bool started = false;
                try { started = worker.StartAttack(null, false); } catch { }
                track.NextAttackAt = Time.time + (started ? 2.0f : 0.9f);
                // Do not fully reset the stuck detector after an attack. If the obstacle survives,
                // the Lox may attack it again after the cooldown instead of waiting another full
                // stuck window.
                track.LastProgressAt = Time.time - Mathf.Max(0f, _stuckSeconds - 1.0f);
                track.LastPosition = current;
                track.Status = started ? $"clearing natural blocker: {bestBlocker}" : $"blocker detected, attack not ready: {bestBlocker}";
                return;
            }

            track.Status = "stuck, no removable natural blocker in body corridor";
            track.LastProgressAt = Time.time; // leave the normal navigator recovery in control

        }

        internal static string GetDebugStatus(Character worker)
        {
            if (worker == null) return "n/a";
            return Tracks.TryGetValue(worker.GetInstanceID(), out Track track) ? track.Status : "idle";
        }

        private static bool IsNaturalBlocker(Collider collider, out string name)
        {
            name = string.Empty;
            TreeBase tree = collider.GetComponentInParent<TreeBase>();
            if (tree != null)
            {
                name = SpeciesRoleRegistry.NormalizePrefabName(tree.gameObject.name);
                return true;
            }

            TreeLog log = collider.GetComponentInParent<TreeLog>();
            if (log != null)
            {
                name = SpeciesRoleRegistry.NormalizePrefabName(log.gameObject.name);
                return true;
            }

            MineRock5 rock5 = collider.GetComponentInParent<MineRock5>();
            if (rock5 != null)
            {
                name = SpeciesRoleRegistry.NormalizePrefabName(rock5.gameObject.name);
                return true;
            }

            MineRock rock = collider.GetComponentInParent<MineRock>();
            if (rock != null)
            {
                name = SpeciesRoleRegistry.NormalizePrefabName(rock.gameObject.name);
                return true;
            }

            Destructible destructible = collider.GetComponentInParent<Destructible>();
            if (destructible != null)
            {
                // Worker/base pieces must never become recovery targets, even if a prefab name
                // happens to contain words such as wood/stone.
                if (destructible.GetComponentInParent<Piece>() != null) return false;
                if (destructible.GetComponentInParent<Container>() != null) return false;

                string raw = SpeciesRoleRegistry.NormalizePrefabName(destructible.gameObject.name);
                string lower = raw.ToLowerInvariant();
                if (lower.Contains("stump") || lower.Contains("stub") || lower.Contains("tree") ||
                    lower.Contains("log") || lower.Contains("rock") || lower.Contains("stone") ||
                    lower.Contains("boulder") || lower.Contains("branch") || lower.Contains("root"))
                {
                    name = raw;
                    return true;
                }
            }
            return false;
        }
    }
}
