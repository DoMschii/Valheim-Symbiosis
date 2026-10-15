using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    internal static class GuardBehaviorRuntime
    {
        private const BindingFlags InstanceAny = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly MethodInfo SetPatrolPointMethod = typeof(BaseAI).GetMethod(
            "SetPatrolPoint", InstanceAny, null, new[] { typeof(Vector3) }, null);

        internal static GuardBehaviorMode Read(ZDO zdo)
        {
            if (zdo == null) return GuardBehaviorMode.Stationary;
            int raw = zdo.GetInt(WorkerZdo.GuardBehavior, (int)GuardBehaviorMode.Stationary);
            return raw == (int)GuardBehaviorMode.Deploy ? GuardBehaviorMode.Deploy : GuardBehaviorMode.Stationary;
        }

        internal static void Set(Character worker, ZDO zdo, GuardBehaviorMode mode)
        {
            if (zdo == null) return;
            zdo.Set(WorkerZdo.GuardBehavior, (int)mode);
            if (worker == null) return;

            GuardPatrolRuntime.Reset(zdo);
            WorkerZdo.ClearTarget(zdo);

            if (mode == GuardBehaviorMode.Stationary)
            {
                // Home is the HunterStand/guard-point center for the ranged Draugr.
                // Return there immediately; once centered, the postfix below prevents combat locomotion.
                zdo.Set(WorkerZdo.State, (int)WorkerState.ReturnHome);
            }
            else
            {
                // Do not wait for the next legacy idle/home transition. The next decision tick can
                // immediately assign the first cheap Workzone patrol point.
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            }
        }

        internal static string DisplayName(GuardBehaviorMode mode) =>
            mode == GuardBehaviorMode.Deploy ? "Ausruecken" : "Stationaer";

        internal static bool IsGuardJob(ZDO zdo)
        {
            if (zdo == null) return false;
            WorkOrder order = (WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None);
            if (order != WorkOrder.Guard) return false;
            WorkerRole role = (WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
            return role == WorkerRole.Hunter || role == WorkerRole.Guard;
        }

        internal static void EnforceAfterVanilla(MonsterAI ai)
        {
            if (ai == null) return;
            if (!WorkerFastLookupRuntime.TryGetWorker(ai, true, out Character worker, out _, out ZDO zdo)) return;
            if (!IsGuardJob(zdo) || Read(zdo) != GuardBehaviorMode.Stationary) return;

            Vector3 guardPoint = WorkerZdo.ReadHome(zdo, worker.transform.position);
            Vector3 delta = guardPoint - worker.transform.position;
            delta.y = 0f;
            float distance = delta.magnitude;

            TrySetPatrolPoint(ai, guardPoint);

            // v0.2.54: stationary guards use the same controlled native bow trigger as Hunters.
            // Enemy acquisition is performed proactively in WorkerCombatStanceRuntime, so a guard
            // can detect and shoot across the configured 15-50 m range even when vanilla aggro
            // would not yet have noticed the target. Damage/projectiles remain native Valheim.
            Character guardTarget = ai.GetTargetCreature();
            if (guardTarget == null || guardTarget.IsDead())
                guardTarget = WorkerCombatStanceRuntime.TryFindGuardEnemy(worker, zdo);
            if (guardTarget != null)
                HunterWorkRuntime.TryTickGuardRangedAttack(worker, guardTarget, out _);

            // Keep the guard centered on its assigned platform. We only override locomotion after
            // vanilla AI has chosen/aimed/attacked, so native bow animation/projectiles remain intact.
            if (distance > 0.45f)
            {
                worker.SetMoveDir(delta / Mathf.Max(distance, 0.001f));
                worker.SetRun(false);
            }
            else
            {
                worker.SetMoveDir(Vector3.zero);
                worker.SetRun(false);
            }
        }

        private static void TrySetPatrolPoint(BaseAI ai, Vector3 point)
        {
            if (ai == null || SetPatrolPointMethod == null) return;
            try { SetPatrolPointMethod.Invoke(ai, new object[] { point }); }
            catch { }
        }
    }
}
