using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Executes the Lumberjack's complete real-Valheim wood cycle.
    ///
    /// The worker uses its native creature attack for the visible swing. Damage is then applied to
    /// the actual target type: TreeBase (standing tree), TreeLog (fallen trunk pieces) or a stump's
    /// Destructible component. Valheim remains authoritative for health, splitting and normal drops.
    /// </summary>
    internal static class LumberjackWorkRuntime
    {
        internal const float AttackInterval = 2.4f;
        internal const float HitDelay = 0.55f;
        internal const float MaxWorkDistance = 3.6f;

        private sealed class Cycle
        {
            internal WoodTarget Target;
            internal int TargetInstanceId;
            internal float NextAttackAt;
            internal float ApplyDamageAt;
            internal bool DamagePending;
            internal string DebugStatus = "waiting";
            internal float NextFailureLogAt;
            internal float NativeAttackFailureSince;
        }

        private static readonly Dictionary<int, Cycle> Cycles = new Dictionary<int, Cycle>();
        private static Action<string> _debugLog;

        internal static void Initialize(Action<string> debugLog)
        {
            _debugLog = debugLog;
        }

        internal static void Shutdown()
        {
            Cycles.Clear();
            _debugLog = null;
        }

        internal static void Reset(Character worker)
        {
            if (worker == null) return;
            Cycles.Remove(worker.GetInstanceID());
        }

        internal static string GetDebugStatus(Character worker)
        {
            if (worker == null) return "inactive";
            return Cycles.TryGetValue(worker.GetInstanceID(), out Cycle cycle)
                ? cycle.DebugStatus
                : "waiting for work cycle";
        }

        /// <summary>
        /// Called every owned MonsterAI frame while the persisted Worker state is Work.
        /// Movement has already been stopped by WorkerNavigationRuntime.
        /// </summary>
        internal static bool TryTick(MonsterAI ai, Character worker, ZDO zdo)
        {
            if (ai == null || worker == null || zdo == null) return false;
            if ((WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None) != WorkerRole.Lumberjack)
                return false;

            if (!WorkerZdo.TryReadTarget(zdo, out string targetName, out string targetKind, out Vector3 targetPosition) ||
                !IsSupportedKind(targetKind))
            {
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                Reset(worker);
                return true;
            }

            int workerKey = worker.GetInstanceID();
            if (!Cycles.TryGetValue(workerKey, out Cycle cycle))
            {
                cycle = new Cycle();
                Cycles[workerKey] = cycle;
            }

            int toolTier = WorkerSkillCatalog.GetWoodcutTier(worker);
            float chopDamage = WorkerSkillCatalog.GetWoodcutDamage(worker);
            Vector3 areaCenter = WorkerZdo.ReadHome(zdo, worker.transform.position);
            float areaRadius = zdo.GetFloat(WorkerZdo.WorkRadius, 40f);
            if (WorkerZdo.TryReadWorkArea(zdo, out WorkOrder workOrder, out Vector3 workCenter, out float workRadius) && workOrder == WorkOrder.FellTrees)
            {
                areaCenter = workCenter;
                areaRadius = workRadius;
            }

            if (cycle.Target == null ||
                !string.Equals(cycle.Target.KindKey, targetKind, StringComparison.Ordinal) ||
                !TreeTargetFinder.IsUsable(cycle.Target, areaCenter, areaRadius, toolTier, zdo))
            {
                cycle.Target = TreeTargetFinder.ResolveNear(targetPosition, targetName, targetKind, 4.5f, toolTier, zdo);
                cycle.TargetInstanceId = cycle.Target != null && cycle.Target.GameObject != null
                    ? cycle.Target.GameObject.GetInstanceID()
                    : 0;
                cycle.DamagePending = false;
                cycle.NativeAttackFailureSince = 0f;

                if (cycle.Target == null)
                {
                    cycle.DebugStatus = $"{FriendlyKind(targetKind)} vanished; requesting next wood target";
                    LocalWorkFallbackRuntime.Clear(zdo);
                    WorkerZdo.ClearTarget(zdo);
                    zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                    return true;
                }
            }

            WoodTarget target = cycle.Target;
            Vector3 closestPoint = ColliderGeometryRuntime.ClosestPointSafe(target, worker.transform.position);
            float distance = Vector3.Distance(worker.transform.position, closestPoint);
            bool localFallback = LocalWorkFallbackRuntime.IsActive(zdo, targetName, targetKind);
            if (distance > MaxWorkDistance && !localFallback)
            {
                // Logs can roll after being selected. Refresh the persisted destination before asking
                // navigation to move again so the worker follows the real trunk instead of stale coordinates.
                WorkerZdo.WriteTarget(zdo, TreeTargetFinder.NormalizePrefabName(target.GameObject.name), target.Position, target.KindKey);
                cycle.DebugStatus = $"{target.DisplayKind} moved / too far ({distance:0.0} m); moving closer";
                zdo.Set(WorkerZdo.State, (int)WorkerState.MoveToTarget);
                cycle.DamagePending = false;
                cycle.NativeAttackFailureSince = 0f;
                return true;
            }

            Vector3 look = closestPoint - worker.transform.position;
            look.y = 0f;
            if (look.sqrMagnitude > 0.01f)
                worker.SetLookDir(look.normalized, 0f);

            // Apply the actual chop slightly after the native attack begins so the damage lines up
            // with the visible creature swing instead of landing instantly on StartAttack().
            if (cycle.DamagePending && Time.time >= cycle.ApplyDamageAt)
            {
                cycle.DamagePending = false;

                if (!TreeTargetFinder.IsUsable(target, areaCenter, areaRadius, toolTier, zdo))
                {
                    cycle.DebugStatus = $"{target.DisplayKind} finished; requesting next wood target";
                    LocalWorkFallbackRuntime.Clear(zdo);
                    WorkerZdo.ClearTarget(zdo);
                    zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                    return true;
                }

                Vector3 preHitPosition = target.Position;
                bool wasStandingTree = target.Kind == WoodTargetKind.TreeBase;
                ApplyChopDamage(worker, target, chopDamage, toolTier);
                cycle.DebugStatus = localFallback
                    ? $"local fallback hit: {chopDamage:0} chop, wood tier {toolTier}"
                    : $"hit applied to {target.DisplayKind}: {chopDamage:0} chop, wood tier {toolTier}";

                if (!TreeTargetFinder.IsUsable(target, areaCenter, areaRadius, toolTier, zdo))
                {
                    if (wasStandingTree)
                    {
                        long ownerPlayerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
                        if (ForestryPlanRuntime.ReactivateNearest(ownerPlayerId, preHitPosition))
                            _debugLog?.Invoke($"Forestry persistent slot reactivated after Lumberjack felled tree at {preHitPosition}.");
                    }
                    cycle.DebugStatus = $"{target.DisplayKind} destroyed; cleanup continues";
                    LocalWorkFallbackRuntime.Clear(zdo);
                    WorkerZdo.ClearTarget(zdo);
                    zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                    return true;
                }
            }

            if (cycle.DamagePending || Time.time < cycle.NextAttackAt || worker.InAttack())
                return true;

            bool started = worker.StartAttack(null, false);
            if (started)
            {
                cycle.NativeAttackFailureSince = 0f;
                cycle.ApplyDamageAt = Time.time + HitDelay;
                cycle.NextAttackAt = Time.time + AttackInterval;
                cycle.DamagePending = true;
                cycle.DebugStatus = $"native attack started on {target.DisplayKind}; chop hit pending";
                return true;
            }

            // Some native creature loadouts (notably the log Troll) can temporarily or permanently
            // refuse Character.StartAttack even though the worker is already at the real tree. Do not
            // let that animation/equipment readiness gate the actual Lumberjack job forever. After a
            // short grace period, keep Valheim's real TreeBase/TreeLog/Destructible damage path as a
            // deterministic fallback. Navigation, target selection and drops remain unchanged.
            if (cycle.NativeAttackFailureSince <= 0f)
                cycle.NativeAttackFailureSince = Time.time;

            if (Time.time - cycle.NativeAttackFailureSince >= 2.5f)
            {
                Vector3 preHitPosition = target.Position;
                bool wasStandingTree = target.Kind == WoodTargetKind.TreeBase;
                ApplyChopDamage(worker, target, chopDamage, toolTier);
                cycle.NextAttackAt = Time.time + AttackInterval;
                cycle.DebugStatus = $"native attack unavailable; fallback hit applied to {target.DisplayKind}";

                if (!TreeTargetFinder.IsUsable(target, areaCenter, areaRadius, toolTier, zdo))
                {
                    if (wasStandingTree)
                    {
                        long ownerPlayerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
                        if (ForestryPlanRuntime.ReactivateNearest(ownerPlayerId, preHitPosition))
                            _debugLog?.Invoke($"Forestry persistent slot reactivated after Lumberjack felled tree at {preHitPosition}.");
                    }
                    cycle.DebugStatus = $"{target.DisplayKind} destroyed by fallback; cleanup continues";
                    LocalWorkFallbackRuntime.Clear(zdo);
                    WorkerZdo.ClearTarget(zdo);
                    zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                }
                return true;
            }

            cycle.NextAttackAt = Time.time + 0.75f;
            cycle.DebugStatus = $"native StartAttack not ready on {target.DisplayKind}; retrying";
            if (Time.time >= cycle.NextFailureLogAt)
            {
                cycle.NextFailureLogAt = Time.time + 10f;
                _debugLog?.Invoke($"Lumberjack StartAttack returned false for {SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name)} while targeting {target.KindKey}. Retrying without forcing an animation trigger.");
            }
            return true;
        }

        private static bool IsSupportedKind(string kind)
        {
            return string.Equals(kind, "TreeBase", StringComparison.Ordinal) ||
                   string.Equals(kind, "TreeLog", StringComparison.Ordinal) ||
                   string.Equals(kind, "TreeStump", StringComparison.Ordinal);
        }

        private static string FriendlyKind(string kind)
        {
            if (string.Equals(kind, "TreeLog", StringComparison.Ordinal)) return "log";
            if (string.Equals(kind, "TreeStump", StringComparison.Ordinal)) return "stump";
            return "tree";
        }

        private static void ApplyChopDamage(Character worker, WoodTarget target, float chopDamage, int toolTier)
        {
            if (worker == null || target == null || target.GameObject == null) return;

            Collider collider = target.ResolveCollider();
            Vector3 point = collider != null ? collider.bounds.center : target.Position;

            Vector3 direction = point - worker.transform.position;
            if (direction.sqrMagnitude > 0.001f)
                direction.Normalize();
            else
                direction = worker.transform.forward;

            HitData hit = new HitData();
            hit.m_damage.m_chop = chopDamage;
            hit.m_toolTier = (short)toolTier;
            hit.m_point = point;
            hit.m_dir = direction;
            hit.m_pushForce = 0f;
            hit.m_hitCollider = collider;
            hit.SetAttacker(worker);

            // TreeBase/TreeLog/Destructible all use Valheim's normal network damage path. Workers
            // never spawns wood or deletes the stump itself; normal split/drop behavior stays native.
            target.Damage(hit);
        }
    }
}
