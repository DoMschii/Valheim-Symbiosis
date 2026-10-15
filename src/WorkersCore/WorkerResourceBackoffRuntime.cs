using System;

namespace Valheim_Workers
{
    /// <summary>
    /// Prevents a multi-job worker from pinning itself forever to one module that is waiting for
    /// an unavailable input. A NeedsResource state is tolerated briefly; afterwards only that
    /// task is backed off, while other enabled tasks remain schedulable.
    /// </summary>
    internal static class WorkerResourceBackoffRuntime
    {
        private const string WaitOrderKey = "vw_resource_wait_order";
        private const string WaitSinceTicksKey = "vw_resource_wait_since_ticks";
        private const string BlockUntilPrefix = "vw_resource_block_until_";

        internal const double GraceSeconds = 6.0;
        internal const double BackoffSeconds = 30.0;

        internal static bool Observe(ZDO zdo, WorkerRole role, WorkOrder order, Action<string> debugLog)
        {
            if (zdo == null || order == WorkOrder.None)
            {
                ClearWait(zdo);
                return false;
            }

            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state != WorkerState.NeedsResource || WorkerZdo.HasCargo(zdo))
            {
                ClearWait(zdo);
                return false;
            }

            WorkerTaskFlags task = WorkerJobProfileRuntime.TaskForOrder(order);
            if (task == WorkerTaskFlags.None)
            {
                ClearWait(zdo);
                return false;
            }

            // Backoff is useful only when the worker actually has another enabled task to do.
            WorkerTaskFlags enabled = WorkerJobProfileRuntime.ReadTasks(zdo, role);
            if ((enabled & task) == 0 || (enabled & ~task) == WorkerTaskFlags.None)
            {
                ClearWait(zdo);
                return false;
            }

            long now = DateTime.UtcNow.Ticks;
            WorkOrder waitingOrder = (WorkOrder)zdo.GetInt(WaitOrderKey, (int)WorkOrder.None);
            long since = zdo.GetLong(WaitSinceTicksKey, 0L);
            if (waitingOrder != order || since <= 0L || since > now)
            {
                zdo.Set(WaitOrderKey, (int)order);
                zdo.Set(WaitSinceTicksKey, now);
                return false;
            }

            if (now - since < TimeSpan.FromSeconds(GraceSeconds).Ticks) return false;

            long until = DateTime.UtcNow.AddSeconds(BackoffSeconds).Ticks;
            zdo.Set(GetBlockKey(task), until);
            ClearWait(zdo);
            debugLog?.Invoke($"Resource backoff: order={order}, task={task}, blocked={BackoffSeconds:0}s after {GraceSeconds:0}s NeedsResource");
            return true;
        }

        internal static bool IsBlocked(ZDO zdo, WorkOrder order)
        {
            WorkerTaskFlags task = WorkerJobProfileRuntime.TaskForOrder(order);
            return task != WorkerTaskFlags.None && IsBlocked(zdo, task);
        }

        internal static bool IsBlocked(ZDO zdo, WorkerTaskFlags task)
        {
            if (zdo == null || task == WorkerTaskFlags.None) return false;
            long until = zdo.GetLong(GetBlockKey(task), 0L);
            if (until <= 0L) return false;
            long now = DateTime.UtcNow.Ticks;
            if (until > now) return true;
            zdo.Set(GetBlockKey(task), 0L);
            return false;
        }

        internal static WorkerTaskFlags FilterBlockedTasks(ZDO zdo, WorkerTaskFlags tasks)
        {
            if (zdo == null || tasks == WorkerTaskFlags.None) return tasks;
            WorkerTaskFlags result = tasks;
            foreach (WorkerTaskFlags task in Enum.GetValues(typeof(WorkerTaskFlags)))
            {
                if (task == WorkerTaskFlags.None || (task & (task - 1)) != 0) continue;
                if ((result & task) != 0 && IsBlocked(zdo, task)) result &= ~task;
            }
            return result;
        }

        // v0.5.372: an explicit player action that creates new work is a stronger signal than
        // an old temporary "missing resource" backoff. Clear only the affected task; other task
        // backoffs remain intact. This is event-driven and performs no scan/polling.
        internal static void WakeTask(ZDO zdo, WorkerTaskFlags task)
        {
            if (zdo == null || task == WorkerTaskFlags.None) return;
            zdo.Set(GetBlockKey(task), 0L);

            WorkOrder waitingOrder = (WorkOrder)zdo.GetInt(WaitOrderKey, (int)WorkOrder.None);
            if (WorkerJobProfileRuntime.TaskForOrder(waitingOrder) == task)
                ClearWait(zdo);
        }

        private static string GetBlockKey(WorkerTaskFlags task)
        {
            return BlockUntilPrefix + ((int)task).ToString();
        }

        private static void ClearWait(ZDO zdo)
        {
            if (zdo == null) return;
            if (zdo.GetInt(WaitOrderKey, (int)WorkOrder.None) != (int)WorkOrder.None)
                zdo.Set(WaitOrderKey, (int)WorkOrder.None);
            if (zdo.GetLong(WaitSinceTicksKey, 0L) != 0L)
                zdo.Set(WaitSinceTicksKey, 0L);
        }
    }
}
