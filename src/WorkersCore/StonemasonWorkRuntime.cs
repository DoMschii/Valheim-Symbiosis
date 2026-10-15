using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Stonework action. A Stone Golem walks to a stone/ore MineRock/MineRock5/Destructible node,
    /// performs its native creature attack, then applies real Valheim pickaxe damage to that node.
    /// Drops remain entirely vanilla; this runtime creates no resources itself.
    /// </summary>
    internal static class StonemasonWorkRuntime
    {
        internal const float AttackInterval = 3.0f;
        internal const float HitDelay = 0.75f;
        // Stone Golems must actually walk up to the quarry face before swinging.
        // Navigation stops slightly closer than the final damage guard so collider jitter
        // cannot make the worker attack from several metres away.
        internal const float ApproachDistance = 1.75f;
        internal const float MaxWorkDistance = 2.15f;

        private sealed class Cycle
        {
            internal StoneTarget Target;
            internal float NextAttackAt;
            internal float ApplyDamageAt;
            internal bool DamagePending;
            internal string DebugStatus = "waiting";
            internal float NextFailureLogAt;
        }

        private static readonly Dictionary<int, Cycle> Cycles = new Dictionary<int, Cycle>();
        // Exact physical quarry target selected by WorkerAiController. Keep this separate from
        // the attack cycle so MoveToTarget can reset attack timing without losing the concrete
        // MineRock/MineRock5/Destructible instance that was already found.
        private static readonly Dictionary<int, StoneTarget> BoundTargets = new Dictionary<int, StoneTarget>();
        private static Action<string> _debugLog;

        internal static void Initialize(Action<string> debugLog) => _debugLog = debugLog;

        internal static void Shutdown()
        {
            Cycles.Clear();
            BoundTargets.Clear();
            _debugLog = null;
        }

        internal static void Reset(Character worker)
        {
            // Reset only the transient attack cycle. The bound physical target must survive the
            // MoveToTarget -> Work hand-off; navigation deliberately calls Reset while travelling.
            if (worker != null) Cycles.Remove(worker.GetInstanceID());
        }

        internal static void BindTarget(Character worker, StoneTarget target)
        {
            if (worker == null) return;
            int key = worker.GetInstanceID();
            if (target == null || target.GameObject == null) BoundTargets.Remove(key);
            else BoundTargets[key] = target;
        }

        internal static void ClearBoundTarget(Character worker)
        {
            if (worker == null) return;
            ClearBoundTarget(worker.GetInstanceID());
        }

        internal static void ClearBoundTarget(int workerKey)
        {
            BoundTargets.Remove(workerKey);
            Cycles.Remove(workerKey);
        }

        /// <summary>
        /// Returns a stable point just outside the exact quarry collider selected by the scanner.
        /// The persisted target remains the real rock surface point; only locomotion uses this
        /// stand-off point. This prevents the ground navigator from treating the target rock itself
        /// as a blocking wall and detouring around the boulder instead of walking straight up to it.
        /// </summary>
        internal static bool TryGetNavigationApproachPoint(Character worker, out Vector3 approachPoint)
        {
            approachPoint = default;
            if (worker == null) return false;
            if (!BoundTargets.TryGetValue(worker.GetInstanceID(), out StoneTarget target) ||
                target == null || target.GameObject == null)
                return false;

            Vector3 surface = target.Position;
            Vector3 away = Vector3.zero;
            Collider collider = target.ResolveCollider(worker.transform.position);
            if (collider != null)
            {
                away = surface - collider.bounds.center;
                away.y = 0f;
            }

            Vector3 towardWorker = worker.transform.position - surface;
            towardWorker.y = 0f;

            // The selected AnchorPoint is the face nearest the worker. Prefer the collider's outward
            // direction because it stays stable while the worker moves; fall back to the worker side
            // for unusual/flat collider bounds.
            if (away.sqrMagnitude < 0.04f ||
                (towardWorker.sqrMagnitude > 0.04f && Vector3.Dot(away, towardWorker) < 0f))
                away = towardWorker;
            if (away.sqrMagnitude < 0.04f)
                away = worker.transform.forward * -1f;
            if (away.sqrMagnitude < 0.04f)
                away = Vector3.forward;

            away.Normalize();
            const float standOff = 1.25f;
            approachPoint = surface + away * standOff;
            // TryMoveTo/DriveDirect steer horizontally; keeping the worker's current elevation lets
            // the existing navigator perform its normal ground sample without targeting the rock top.
            approachPoint.y = worker.transform.position.y;
            return true;
        }

        internal static string GetDebugStatus(Character worker)
        {
            if (worker == null) return "inactive";
            string scan = StoneTargetFinder.GetLastDebugSummary();
            return Cycles.TryGetValue(worker.GetInstanceID(), out Cycle cycle)
                ? $"{cycle.DebugStatus} | scan: {scan}"
                : $"waiting for stonework cycle | scan: {scan}";
        }

        internal static bool TryTick(MonsterAI ai, Character worker, ZDO zdo)
        {
            if (ai == null || worker == null || zdo == null) return false;
            if ((WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None) != WorkerRole.Stonemason)
                return false;

            int workerKey = worker.GetInstanceID();
            bool hasPersistedTarget = WorkerZdo.TryReadTarget(zdo, out string targetName, out string targetKind, out Vector3 targetPosition) &&
                (string.Equals(targetKind, StoneTarget.MineRock5Kind, StringComparison.Ordinal) ||
                 string.Equals(targetKind, StoneTarget.MineRockKind, StringComparison.Ordinal) ||
                 string.Equals(targetKind, StoneTarget.DestructibleKind, StringComparison.Ordinal));

            // v0.4.44: the scanner already found an exact physical rock patch. Do not lose it just
            // because the persisted target hand-off was briefly cleared between navigation ticks.
            // Rehydrate the ZDO target from that exact object instead of forcing another world scan.
            if (!hasPersistedTarget)
            {
                if (BoundTargets.TryGetValue(workerKey, out StoneTarget rebound) && rebound != null && rebound.GameObject != null)
                {
                    targetName = StoneTargetFinder.NormalizePrefabName(rebound.GameObject.name);
                    targetKind = rebound.KindKey;
                    targetPosition = rebound.Position;
                    WorkerZdo.WriteTarget(zdo, targetName, targetPosition, targetKind);
                }
                else
                {
                    zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                    Reset(worker);
                    return true;
                }
            }

            if (!Cycles.TryGetValue(workerKey, out Cycle cycle))
            {
                cycle = new Cycle();
                Cycles[workerKey] = cycle;
            }

            int toolTier = WorkerSkillCatalog.GetStoneworkTier(worker);
            float pickaxeDamage = WorkerSkillCatalog.GetStoneworkDamage(worker);
            Vector3 areaCenter = WorkerZdo.ReadHome(zdo, worker.transform.position);
            float areaRadius = zdo.GetFloat(WorkerZdo.WorkRadius, 40f);
            if (WorkerZdo.TryReadWorkArea(zdo, out WorkOrder order, out Vector3 workCenter, out float workRadius) && order == WorkOrder.Stonework)
            {
                areaCenter = workCenter;
                areaRadius = workRadius;
            }

            if (cycle.Target == null ||
                !string.Equals(cycle.Target.KindKey, targetKind, StringComparison.Ordinal) ||
                !StoneTargetFinder.IsUsable(cycle.Target, areaCenter, areaRadius, toolTier))
            {
                // Prefer the exact in-memory target selected by WorkerAiController. This avoids a
                // fragile second Physics scan when the Golem reaches the rock.
                if (BoundTargets.TryGetValue(workerKey, out StoneTarget bound) &&
                    bound != null && bound.GameObject != null &&
                    string.Equals(bound.KindKey, targetKind, StringComparison.Ordinal) &&
                    StoneTargetFinder.IsUsable(bound, areaCenter, areaRadius, toolTier))
                {
                    cycle.Target = bound;
                }
                else
                {
                    cycle.Target = null;
                }

                // Only if the exact physical reference is genuinely gone do we attempt recovery
                // around the persisted point (e.g. after zone reload or object replacement).
                if (cycle.Target == null)
                {
                // First try to recover the exact quarry patch that FindJob selected. MineRock5
                // hierarchies can expose changing child collider names/patches between the decision
                // and work ticks, so an exact prefab-name lookup is sometimes too strict.
                cycle.Target = StoneTargetFinder.ResolveNear(targetPosition, targetName, targetKind, 6.0f, toolTier);
                if (cycle.Target == null)
                    cycle.Target = StoneTargetFinder.ResolveNear(targetPosition, string.Empty, targetKind, 8.0f, toolTier);

                // Final local fallback: keep working the nearest valid stone around the persisted
                // target point rather than bouncing FindJob -> MoveToTarget -> Work -> FindJob.
                if (cycle.Target == null)
                {
                    StoneTargetResult fallback = StoneTargetFinder.FindNearest(
                        targetPosition, 8.0f, worker.transform.position, toolTier,
                        candidate => candidate != null && string.Equals(candidate.KindKey, targetKind, StringComparison.Ordinal));
                    cycle.Target = fallback.Target;
                }
                }

                cycle.DamagePending = false;
                if (cycle.Target == null)
                {
                    cycle.DebugStatus = "stone target vanished; requesting next target";
                    BoundTargets.Remove(workerKey);
                    LocalWorkFallbackRuntime.Clear(zdo);
                    WorkerZdo.ClearTarget(zdo);
                    zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                    return true;
                }

                // Persist the actually recovered patch so navigation/work stay on the same target.
                WorkerZdo.WriteTarget(zdo, StoneTargetFinder.NormalizePrefabName(cycle.Target.GameObject.name), cycle.Target.Position, cycle.Target.KindKey);
            }

            StoneTarget target = cycle.Target;
            Vector3 point = ColliderGeometryRuntime.ClosestPointSafe(target, worker.transform.position);
            float distance = Vector3.Distance(worker.transform.position, point);
            bool localFallback = LocalWorkFallbackRuntime.IsActive(zdo, targetName, targetKind);
            if (distance > MaxWorkDistance && !localFallback)
            {
                WorkerZdo.WriteTarget(zdo, StoneTargetFinder.NormalizePrefabName(target.GameObject.name), target.Position, target.KindKey);
                cycle.DebugStatus = $"stone target too far ({distance:0.0} m); moving closer";
                zdo.Set(WorkerZdo.State, (int)WorkerState.MoveToTarget);
                cycle.DamagePending = false;
                return true;
            }

            Vector3 look = point - worker.transform.position;
            look.y = 0f;
            if (look.sqrMagnitude > 0.01f)
                worker.SetLookDir(look.normalized, 0f);

            if (cycle.DamagePending && Time.time >= cycle.ApplyDamageAt)
            {
                cycle.DamagePending = false;
                if (!StoneTargetFinder.IsUsable(target, areaCenter, areaRadius, toolTier))
                {
                    cycle.DebugStatus = "stone target finished; requesting next target";
                    BoundTargets.Remove(workerKey);
                    LocalWorkFallbackRuntime.Clear(zdo);
                    WorkerZdo.ClearTarget(zdo);
                    zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                    return true;
                }

                ApplyPickaxeDamage(worker, target, pickaxeDamage, toolTier);
                cycle.DebugStatus = localFallback
                    ? $"local fallback hit: {pickaxeDamage:0} pickaxe, stone tier {toolTier}"
                    : $"hit applied: {pickaxeDamage:0} pickaxe, stone tier {toolTier}";
            }

            if (cycle.DamagePending || Time.time < cycle.NextAttackAt || worker.InAttack())
                return true;

            // Stone Golems carry several attack items in their Humanoid inventory, but vanilla
            // MonsterAI normally chooses/equips one only while it owns a combat target. Workers
            // intentionally suppress that combat target while doing stonework, so StartAttack()
            // can otherwise fail forever because GetCurrentWeapon() is null. Equip one of the
            // golem's own native melee attacks first; no fake animation or custom weapon is used.
            string weaponStatus = EnsureStoneGolemWorkWeapon(worker);
            bool started = worker.StartAttack(null, false);
            if (started)
            {
                cycle.ApplyDamageAt = Time.time + HitDelay;
                cycle.NextAttackAt = Time.time + AttackInterval;
                cycle.DamagePending = true;
                cycle.DebugStatus = $"native Stone Golem attack started ({weaponStatus}); pickaxe hit pending";
                return true;
            }

            cycle.NextAttackAt = Time.time + 0.75f;
            cycle.DebugStatus = $"native StartAttack not ready ({weaponStatus}); retrying";
            if (Time.time >= cycle.NextFailureLogAt)
            {
                cycle.NextFailureLogAt = Time.time + 10f;
                _debugLog?.Invoke($"Stonemason StartAttack returned false for {SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name)}; retrying.");
            }
            return true;
        }


        private static string EnsureStoneGolemWorkWeapon(Character worker)
        {
            Humanoid humanoid = worker as Humanoid;
            if (humanoid == null) return "not Humanoid";

            ItemDrop.ItemData current = humanoid.GetCurrentWeapon();
            if (IsStoneGolemMeleeAttack(current))
                return DescribeWeapon(current);

            List<ItemDrop.ItemData> items = humanoid.GetInventory()?.GetAllItems();
            if (items == null || items.Count == 0)
                return "no inventory attacks";

            ItemDrop.ItemData best = null;
            int bestScore = int.MinValue;
            for (int i = 0; i < items.Count; ++i)
            {
                ItemDrop.ItemData item = items[i];
                if (!IsStoneGolemMeleeAttack(item)) continue;

                string name = GetWeaponPrefabName(item).ToLowerInvariant();
                int score = 0;
                if (name.Contains("groundslam")) score += 40;
                if (name.Contains("doublesmash")) score += 35;
                if (name.Contains("spikesweep")) score += 25;
                if (name.Contains("attack1")) score += 15;
                if (item.m_shared.m_aiPrioritized) score += 5;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = item;
                }
            }

            if (best == null) return "no native melee attack item";

            bool equipped = humanoid.EquipItem(best, false);
            ItemDrop.ItemData after = humanoid.GetCurrentWeapon();
            if (IsStoneGolemMeleeAttack(after))
                return $"equipped {DescribeWeapon(after)}";

            return equipped ? $"equip pending {DescribeWeapon(best)}" : $"equip failed {DescribeWeapon(best)}";
        }

        private static bool IsStoneGolemMeleeAttack(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null || !item.HavePrimaryAttack()) return false;

            string name = GetWeaponPrefabName(item);
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();

            if (!lower.Contains("stonegolem") && !lower.Contains("stone_golem")) return false;

            // Vanilla Stone Golem's actual attacks are item-prefabs named stonegolem_attack*.
            // Require that explicit attack marker so visual/default items such as hat/clubs/spikes
            // are never selected as the work weapon.
            return lower.Contains("attack");
        }

        private static string GetWeaponPrefabName(ItemDrop.ItemData item)
        {
            if (item == null) return string.Empty;
            if (item.m_dropPrefab != null) return StoneTargetFinder.NormalizePrefabName(item.m_dropPrefab.name);
            return item.m_shared != null ? item.m_shared.m_name ?? string.Empty : string.Empty;
        }

        private static string DescribeWeapon(ItemDrop.ItemData item)
        {
            string name = GetWeaponPrefabName(item);
            return string.IsNullOrEmpty(name) ? "native attack" : name;
        }

        private static void ApplyPickaxeDamage(Character worker, StoneTarget target, float damage, int toolTier)
        {
            if (worker == null || target == null || target.GameObject == null) return;

            Collider collider = target.ResolveCollider(worker.transform.position);
            // Damage exactly the quarry patch selected by StoneTargetFinder. MineRock5 can have
            // one root with many far-apart colliders, so recomputing ClosestPoint from the worker
            // here can jump to a different patch and make the visible swing miss.
            Vector3 point = target.Position;
            Vector3 direction = point - worker.transform.position;
            if (direction.sqrMagnitude > 0.001f) direction.Normalize();
            else direction = worker.transform.forward;

            HitData hit = new HitData();
            hit.m_damage.m_pickaxe = damage;
            hit.m_toolTier = (short)toolTier;
            hit.m_point = point;
            hit.m_dir = direction;
            hit.m_pushForce = 0f;
            hit.m_hitCollider = collider;
            hit.SetAttacker(worker);
            target.Damage(hit);
        }
    }
}
