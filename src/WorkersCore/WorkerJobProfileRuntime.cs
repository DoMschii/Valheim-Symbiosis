using System;

namespace Valheim_Workers
{
    [Flags]
    internal enum WorkerTaskFlags
    {
        None = 0,
        Gather = 1 << 0,
        Harvest = 1 << 1,
        Garden = 1 << 2,
        Forestry = 1 << 3,
        Transport = 1 << 4,
        Sort = 1 << 5,
        FellTrees = 1 << 6,
        Stonework = 1 << 7,
        KeepFires = 1 << 8,
        Hunt = 1 << 9,
        Guard = 1 << 10,
        Bake = 1 << 11,
        Butcher = 1 << 12,
        Smith = 1 << 13,
        Fish = 1 << 14,
        Mine = 1 << 15,
        Repair = 1 << 16,
        Provision = 1 << 17
    }

    /// <summary>
    /// Per-worker job profile stored directly on the Worker ZDO. This is intentionally generic
    /// groundwork for future role-specific filters (for example allowed tree species), while the
    /// first implementation exposes Gather/Harvest as independent tasks for Greyling field workers.
    /// </summary>
    internal static class WorkerJobProfileRuntime
    {
        internal const string TaskMaskKey = "vw_job_task_mask";
        internal const string TaskMaskSetKey = "vw_job_task_mask_set";
        private const string TaskPriorityPrefix = "vw_job_priority_";
        private const string ProvisionSplitMigrationKey = "vw_job_provision_split_migrated_v1";
        private const string GreydwarfShamanHarvestMigrationKey = "vw_job_greydwarf_shaman_harvest_migrated_v1";
        private const string GardenerEqualPriorityNextKey = "vw_job_gardener_equal_next";
        private const string GathererEqualPriorityNextKey = "vw_job_gatherer_equal_next";

        internal static WorkerRole NormalizeRuntimeRole(ZDO zdo)
        {
            if (zdo == null) return WorkerRole.None;

            WorkerRole current = (WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
            string species = SpeciesRoleRegistry.NormalizePrefabName(zdo.GetString(WorkerZdo.SpeciesKey, string.Empty));
            if (string.IsNullOrEmpty(species) || !SpeciesRoleRegistry.TryGetRole(species, out WorkerRole stableRole))
                return current;

            // v0.5.442: Bergbau is no longer a separate gameplay job. Very old/experimental
            // Stonemason saves that still carry WorkOrder.Mine or the Mine task are migrated
            // losslessly into the existing Stonework task. Numeric enum values remain untouched
            // for binary/save compatibility; only new player-facing assignment is consolidated.
            if (stableRole == WorkerRole.Stonemason)
            {
                WorkOrder persistedOrder = (WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None);
                if (persistedOrder == WorkOrder.Mine)
                    zdo.Set(WorkerZdo.WorkOrderKey, (int)WorkOrder.Stonework);

                if (zdo.GetBool(TaskMaskSetKey, false))
                {
                    WorkerTaskFlags raw = (WorkerTaskFlags)zdo.GetInt(TaskMaskKey, (int)WorkerTaskFlags.None);
                    if ((raw & WorkerTaskFlags.Mine) != 0)
                    {
                        raw &= ~WorkerTaskFlags.Mine;
                        raw |= WorkerTaskFlags.Stonework;
                        zdo.Set(TaskMaskKey, (int)raw);

                        int oldPriority = zdo.GetInt(GetPriorityKey(WorkerTaskFlags.Mine), 0);
                        int stonePriority = zdo.GetInt(GetPriorityKey(WorkerTaskFlags.Stonework), 0);
                        if (stonePriority <= 0 && oldPriority >= 1 && oldPriority <= 4)
                            zdo.Set(GetPriorityKey(WorkerTaskFlags.Stonework), oldPriority);
                    }
                }
            }

            if (current == stableRole) return current;

            // v0.5.110: the old selectable "role/specialization" is no longer gameplay state.
            // Collapse any legacy alternate role to the species' stable internal role. The task
            // mask/priority keys are preserved, so Baker/Guard settings survive the migration.
            zdo.Set(WorkerZdo.Role, (int)stableRole);

            ZDOID homeId = WorkerZdo.ReadHomeAnchorZdoId(zdo);
            if (homeId != ZDOID.None && ZDOMan.instance != null)
            {
                ZDO homeZdo = ZDOMan.instance.GetZDO(homeId);
                if (homeZdo != null && WorkerHomeAnchorZdo.IsAssigned(homeZdo))
                    homeZdo.Set(WorkerHomeAnchorZdo.WorkerRoleKey, (int)stableRole);
            }

            return stableRole;
        }

        private static WorkerTaskFlags GetRoleMask(ZDO zdo, WorkerRole role)
        {
            if (role == WorkerRole.Gatherer) return WorkerTaskFlags.Gather | WorkerTaskFlags.Harvest;
            if (role == WorkerRole.Gardener)
            {
                WorkerTaskFlags mask = WorkerTaskFlags.Garden | WorkerTaskFlags.Forestry;
                // v0.5.368: Both Shaman gardeners may harvest ripe plants/pickables in their
                // Workzone. The Greydwarf Shaman can therefore tend its own WildGrowth berry
                // bushes instead of only planting them; the Fuling Shaman keeps its advanced
                // area-harvest / crop-specialist behaviour.
                string species = zdo != null ? SpeciesRoleRegistry.NormalizePrefabName(zdo.GetString(WorkerZdo.SpeciesKey, string.Empty)) : string.Empty;
                bool greydwarfShaman = string.Equals(species, "Greydwarf_Shaman", StringComparison.OrdinalIgnoreCase);
                bool fulingShaman = string.Equals(species, "Fuling_Shaman", StringComparison.OrdinalIgnoreCase);
                if (fulingShaman)
                {
                    // Cooking remains a Plains/Fuling Shaman capability only. The Greydwarf
                    // Shaman is the Wildwuchs gardener and must not receive Cook/Bake jobs.
                    // Keep the historic Bake bit/order internally for Fuling save compatibility.
                    mask |= WorkerTaskFlags.Bake;
                }
                if (greydwarfShaman || fulingShaman)
                {
                    // Both Shaman species can harvest mature Workzone Pickables. Category filters
                    // remain authoritative, so berries/vegetables/etc. can still be enabled per worker.
                    mask |= WorkerTaskFlags.Harvest;
                }
                return mask;
            }
            if (role == WorkerRole.Hauler)
            {
                // Sorting is a small-settlement Greydwarf job. Heavy and air haulers keep their
                // specialized transport identity and do not expose an unusable Sort checkbox.
                string species = zdo != null ? SpeciesRoleRegistry.NormalizePrefabName(zdo.GetString(WorkerZdo.SpeciesKey, string.Empty)) : string.Empty;
                if (string.Equals(species, "Lox", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(species, "Drake", StringComparison.OrdinalIgnoreCase))
                    return WorkerTaskFlags.Transport;
                return WorkerTaskFlags.Transport | WorkerTaskFlags.Sort | WorkerTaskFlags.Provision;
            }
            if (role == WorkerRole.Lumberjack) return WorkerTaskFlags.FellTrees;
            if (role == WorkerRole.Stonemason) return WorkerTaskFlags.Stonework;
            if (role == WorkerRole.Firekeeper) return WorkerTaskFlags.KeepFires;
            if (role == WorkerRole.Hunter) return WorkerTaskFlags.Hunt | WorkerTaskFlags.Guard;
            if (role == WorkerRole.Guard) return WorkerTaskFlags.Guard;
            if (role == WorkerRole.Butcher) return WorkerTaskFlags.Butcher;
            if (role == WorkerRole.Smith) return WorkerTaskFlags.Smith | WorkerTaskFlags.Repair;
            if (role == WorkerRole.Baker) return WorkerTaskFlags.Bake;
            if (role == WorkerRole.Fisher) return WorkerTaskFlags.Fish;
            return WorkerTaskFlags.None;
        }

        internal static WorkerTaskFlags GetAvailableTasks(ZDO zdo, WorkerRole role)
        {
            return GetRoleMask(zdo, role);
        }

        internal static WorkerTaskFlags ReadTasks(ZDO zdo, WorkerRole role)
        {
            if (zdo == null) return WorkerTaskFlags.None;

            WorkerTaskFlags roleMask = GetRoleMask(zdo, role);
            if (roleMask == WorkerTaskFlags.None) return WorkerTaskFlags.None;

            if (zdo.GetBool(TaskMaskSetKey, false))
            {
                WorkerTaskFlags stored = (WorkerTaskFlags)zdo.GetInt(TaskMaskKey, (int)WorkerTaskFlags.None) & roleMask;
                // v0.5.221: Sort used to include provisioning. Existing Haulers that had Sort
                // enabled keep the old behaviour once by receiving the new independent Provision
                // task. Afterwards both checkboxes/priorities are fully independent.
                if (role == WorkerRole.Hauler && !zdo.GetBool(ProvisionSplitMigrationKey, false))
                {
                    if ((stored & WorkerTaskFlags.Sort) != 0 && (roleMask & WorkerTaskFlags.Provision) != 0)
                        stored |= WorkerTaskFlags.Provision;
                    zdo.Set(TaskMaskKey, (int)stored);
                    zdo.Set(ProvisionSplitMigrationKey, true);
                }

                // v0.5.368: Greydwarf Shamans could not select Harvest before this version, so an
                // already-persisted Gardener mask would otherwise hide the newly added task forever.
                // Add Harvest once for active Greydwarf-Shaman profiles. An intentionally paused
                // worker (empty task mask) stays paused. Fuling Shamans are not migrated because
                // Harvest was already user-configurable for them and an explicit off must remain off.
                if (role == WorkerRole.Gardener && !zdo.GetBool(GreydwarfShamanHarvestMigrationKey, false))
                {
                    string species = SpeciesRoleRegistry.NormalizePrefabName(zdo.GetString(WorkerZdo.SpeciesKey, string.Empty));
                    if (string.Equals(species, "Greydwarf_Shaman", StringComparison.OrdinalIgnoreCase) &&
                        (roleMask & WorkerTaskFlags.Harvest) != 0 &&
                        (stored & (WorkerTaskFlags.Garden | WorkerTaskFlags.Forestry)) != 0)
                    {
                        stored |= WorkerTaskFlags.Harvest;
                        zdo.Set(TaskMaskKey, (int)stored);
                    }
                    zdo.Set(GreydwarfShamanHarvestMigrationKey, true);
                }
                return stored;
            }

            // Migration: preserve the exact previously selected assignment the first time a
            // modular profile is read. New/unassigned workers default to both compatible tasks.
            WorkOrder current = (WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None);
            if (role == WorkerRole.Gatherer)
            {
                if (current == WorkOrder.Harvest) return WorkerTaskFlags.Harvest;
                if (current == WorkOrder.GatherResources) return WorkerTaskFlags.Gather;
                return WorkerTaskFlags.Gather | WorkerTaskFlags.Harvest;
            }

            if (role == WorkerRole.Hauler)
            {
                // Mark the split migration as completed even for profiles that did not yet have an
                // explicit task mask. Otherwise the first later checkbox change could re-enable
                // Provision behind the player's back.
                if (!zdo.GetBool(ProvisionSplitMigrationKey, false))
                    zdo.Set(ProvisionSplitMigrationKey, true);

                // Before v0.5.221 WorkOrder.Sort also meant provisioning. Preserve that exact old
                // behaviour for legacy no-mask workers by enabling both independent modules once.
                if (current == WorkOrder.Sort && (roleMask & WorkerTaskFlags.Sort) != 0)
                    return WorkerTaskFlags.Sort | (roleMask & WorkerTaskFlags.Provision);
                if (current == WorkOrder.Provision && (roleMask & WorkerTaskFlags.Provision) != 0) return WorkerTaskFlags.Provision;
                if (current == WorkOrder.Haul) return WorkerTaskFlags.Transport;
                return roleMask;
            }

            if (current == WorkOrder.Garden) return WorkerTaskFlags.Garden;
            if (current == WorkOrder.Forestry) return WorkerTaskFlags.Forestry;
            if (current == WorkOrder.Harvest && (roleMask & WorkerTaskFlags.Harvest) != 0) return WorkerTaskFlags.Harvest;
            if (current == WorkOrder.Bake && (roleMask & WorkerTaskFlags.Bake) != 0) return WorkerTaskFlags.Bake;
            return roleMask;
        }

        internal static bool HasTask(ZDO zdo, WorkerRole role, WorkerTaskFlags task)
        {
            return (ReadTasks(zdo, role) & task) != 0;
        }

        internal static int GetPriority(ZDO zdo, WorkerRole role, WorkerTaskFlags task)
        {
            if (zdo == null) return 4;
            WorkerTaskFlags roleMask = GetRoleMask(zdo, role);
            if ((roleMask & task) == 0) return 4;

            int stored = zdo.GetInt(GetPriorityKey(task), 0);
            if (stored >= 1 && stored <= 4) return stored;
            return GetDefaultPriority(role, task);
        }


        internal static int GetTaskLevel(ZDO zdo, WorkerRole role, WorkerTaskFlags task)
        {
            if (zdo == null) return 0;
            return HasTask(zdo, role, task) ? GetPriority(zdo, role, task) : 0;
        }

        internal static bool TrySetTaskLevel(ZDO zdo, WorkerRole role, WorkerTaskFlags task, int level)
        {
            if (zdo == null) return false;
            WorkerTaskFlags roleMask = GetRoleMask(zdo, role);
            if (roleMask == WorkerTaskFlags.None || (task & roleMask) == 0) return false;

            level = Math.Max(0, Math.Min(4, level));
            WorkerTaskFlags current = ReadTasks(zdo, role);
            bool wasEnabled = (current & task) != 0;
            int oldLevel = wasEnabled ? GetPriority(zdo, role, task) : 0;
            if (oldLevel == level) return true;

            bool nextEnabled = level > 0;
            WorkerTaskFlags next = nextEnabled ? current | task : current & ~task;
            next &= roleMask;

            if (next != current && WorkerZdo.HasCargo(zdo))
                WorkerZdo.MarkCargoDropPending(zdo, $"task profile changed: {current} -> {next}");

            zdo.Set(TaskMaskKey, (int)next);
            zdo.Set(TaskMaskSetKey, true);
            if (nextEnabled)
                zdo.Set(GetPriorityKey(task), level);

            WorkerZdo.ClearTarget(zdo);
            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state != WorkerState.ReturnHomeForRest && state != WorkerState.Resting && state != WorkerState.Recover)
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            if ((task & WorkerTaskFlags.Provision) != 0)
                WorkerProvisioningLogisticsRuntime.NotifyWorkerTopologyChanged(zdo);

            return true;
        }

        internal static bool TrySetPriority(ZDO zdo, WorkerRole role, WorkerTaskFlags task, int priority)
        {
            if (zdo == null) return false;
            WorkerTaskFlags roleMask = GetRoleMask(zdo, role);
            if ((roleMask & task) == 0) return false;
            priority = Math.Max(1, Math.Min(4, priority));
            zdo.Set(GetPriorityKey(task), priority);
            return true;
        }

        internal static int GetPriorityForOrder(ZDO zdo, WorkerRole role, WorkOrder order)
        {
            WorkerTaskFlags task = TaskForOrder(order);
            return task == WorkerTaskFlags.None ? 4 : GetPriority(zdo, role, task);
        }

        internal static WorkerTaskFlags TaskForOrder(WorkOrder order)
        {
            switch (order)
            {
                case WorkOrder.GatherResources: return WorkerTaskFlags.Gather;
                case WorkOrder.Harvest: return WorkerTaskFlags.Harvest;
                case WorkOrder.Garden: return WorkerTaskFlags.Garden;
                case WorkOrder.Forestry: return WorkerTaskFlags.Forestry;
                case WorkOrder.Haul: return WorkerTaskFlags.Transport;
                case WorkOrder.Sort: return WorkerTaskFlags.Sort;
                case WorkOrder.Provision: return WorkerTaskFlags.Provision;
                case WorkOrder.FellTrees: return WorkerTaskFlags.FellTrees;
                case WorkOrder.Stonework: return WorkerTaskFlags.Stonework;
                case WorkOrder.KeepFires: return WorkerTaskFlags.KeepFires;
                case WorkOrder.Hunt: return WorkerTaskFlags.Hunt;
                case WorkOrder.Guard: return WorkerTaskFlags.Guard;
                case WorkOrder.Bake: return WorkerTaskFlags.Bake;
                case WorkOrder.Butcher: return WorkerTaskFlags.Butcher;
                case WorkOrder.Smith: return WorkerTaskFlags.Smith;
                case WorkOrder.Fish: return WorkerTaskFlags.Fish;
                case WorkOrder.Mine: return WorkerTaskFlags.Mine;
                default: return WorkerTaskFlags.None;
            }
        }

        // v0.5.111+: one selector owns numeric priority semantics for every multi-job worker.
        // v0.5.377 adds role-specific equal-priority ordering so an old current module cannot
        // starve a more important sibling job forever. Explicit numeric priorities remain the
        // first authority; these arrays are used only when two available jobs have the same level.
        private static readonly WorkOrder[] PriorityTieOrder =
        {
            WorkOrder.Harvest,
            WorkOrder.GatherResources,
            WorkOrder.Provision,
            WorkOrder.Haul,
            WorkOrder.Sort,
            WorkOrder.Garden,
            WorkOrder.Forestry,
            WorkOrder.Hunt,
            WorkOrder.Guard,
            WorkOrder.Bake,
            WorkOrder.FellTrees,
            WorkOrder.Stonework,
            WorkOrder.KeepFires,
            WorkOrder.Butcher,
            WorkOrder.Smith,
            WorkOrder.Fish,
            WorkOrder.Mine
        };

        // At equal priority a real worker/home provisioning request is more important than
        // shuffling stock around, and transport is more important than cosmetic sorting. Cargo
        // and already-started routes are still protected by the callers before selection.
        private static readonly WorkOrder[] HaulerPriorityTieOrder =
        {
            WorkOrder.Provision,
            WorkOrder.Haul,
            WorkOrder.Sort
        };

        // Hunting is the Draugr hunter's primary job. Guard is deliberately the fallback when no
        // valid prey exists (unless the player gives Guard a numerically higher priority).
        private static readonly WorkOrder[] HunterPriorityTieOrder =
        {
            WorkOrder.Hunt,
            WorkOrder.Guard
        };

        private static readonly WorkOrder[] GathererPriorityTieOrder =
        {
            WorkOrder.Harvest,
            WorkOrder.GatherResources
        };

        // Garden/Harvest use a persisted one-action handoff below. The static order is the fallback
        // for the first decision or when only one of the two jobs has concrete work.
        private static readonly WorkOrder[] GardenerPriorityTieOrder =
        {
            WorkOrder.Harvest,
            WorkOrder.Garden,
            WorkOrder.Forestry,
            WorkOrder.Bake
        };

        private static WorkOrder[] GetPriorityTieOrder(WorkerRole role)
        {
            if (role == WorkerRole.Hauler) return HaulerPriorityTieOrder;
            if (role == WorkerRole.Hunter) return HunterPriorityTieOrder;
            if (role == WorkerRole.Gatherer) return GathererPriorityTieOrder;
            if (role == WorkerRole.Gardener) return GardenerPriorityTieOrder;
            return PriorityTieOrder;
        }

        private static bool IsCommittedCurrentAction(ZDO zdo, WorkerRole role, WorkOrder current, WorkerTaskFlags enabled, int bestPriority)
        {
            if (zdo == null || current == WorkOrder.None) return false;
            if (string.Equals(zdo.GetString("vw_bg_mode", string.Empty), "background", StringComparison.OrdinalIgnoreCase))
                return false;
            WorkerTaskFlags currentTask = TaskForOrder(current);
            if (currentTask == WorkerTaskFlags.None || (enabled & currentTask) == 0) return false;
            if (GetPriorityForOrder(zdo, role, current) != bestPriority)
                return false;

            // Guard patrol/positioning is an interruptible fallback for Hunters. As soon as a real
            // hunt becomes equally/more important, do not make the Hunter finish a patrol leg first.
            if (role == WorkerRole.Hunter && current == WorkOrder.Guard) return false;

            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state != WorkerState.MoveToTarget && state != WorkerState.Work && state != WorkerState.Collect && state != WorkerState.Deliver)
                return false;

            // A persisted concrete target/cargo cycle is a real in-progress action and is allowed
            // to finish before equal-priority arbitration runs again. This avoids oscillation while
            // still removing the old "current module wins forever" starvation rule at FindJob.
            if (WorkerZdo.HasCargo(zdo)) return true;
            return WorkerZdo.TryReadTarget(zdo, out _, out _, out _);
        }

        internal static void NoteCompletedAction(ZDO zdo, WorkerRole role, WorkOrder completed)
        {
            if (zdo == null) return;
            if (role == WorkerRole.Gardener)
            {
                if (completed == WorkOrder.Harvest)
                    zdo.Set(GardenerEqualPriorityNextKey, (int)WorkOrder.Garden);
                else if (completed == WorkOrder.Garden)
                    zdo.Set(GardenerEqualPriorityNextKey, (int)WorkOrder.Harvest);
                return;
            }

            if (role == WorkerRole.Gatherer)
            {
                if (completed == WorkOrder.Harvest)
                    zdo.Set(GathererEqualPriorityNextKey, (int)WorkOrder.GatherResources);
                else if (completed == WorkOrder.GatherResources)
                    zdo.Set(GathererEqualPriorityNextKey, (int)WorkOrder.Harvest);
            }
        }

        private static WorkOrder SelectGardenerEqualPriorityTurn(ZDO zdo, WorkerTaskFlags enabled, int bestPriority)
        {
            WorkerTaskFlags pair = WorkerTaskFlags.Harvest | WorkerTaskFlags.Garden;
            if ((enabled & pair) != pair) return WorkOrder.None;
            if (GetPriority(zdo, WorkerRole.Gardener, WorkerTaskFlags.Harvest) != bestPriority ||
                GetPriority(zdo, WorkerRole.Gardener, WorkerTaskFlags.Garden) != bestPriority)
                return WorkOrder.None;

            WorkOrder preferred = (WorkOrder)zdo.GetInt(GardenerEqualPriorityNextKey, (int)WorkOrder.Harvest);
            return preferred == WorkOrder.Garden ? WorkOrder.Garden : WorkOrder.Harvest;
        }

        private static WorkOrder SelectGathererEqualPriorityTurn(ZDO zdo, WorkerTaskFlags enabled, int bestPriority)
        {
            WorkerTaskFlags pair = WorkerTaskFlags.Harvest | WorkerTaskFlags.Gather;
            if ((enabled & pair) != pair) return WorkOrder.None;
            if (GetPriority(zdo, WorkerRole.Gatherer, WorkerTaskFlags.Harvest) != bestPriority ||
                GetPriority(zdo, WorkerRole.Gatherer, WorkerTaskFlags.Gather) != bestPriority)
                return WorkOrder.None;

            WorkOrder preferred = (WorkOrder)zdo.GetInt(GathererEqualPriorityNextKey, (int)WorkOrder.Harvest);
            return preferred == WorkOrder.GatherResources ? WorkOrder.GatherResources : WorkOrder.Harvest;
        }

        internal static WorkOrder SelectBestAvailableOrder(
            ZDO zdo,
            WorkerRole role,
            WorkOrder current,
            WorkerTaskFlags availableTasks)
        {
            if (zdo == null || availableTasks == WorkerTaskFlags.None) return WorkOrder.None;

            WorkerTaskFlags enabled = ReadTasks(zdo, role) & availableTasks;
            // v0.5.291: a task that has waited in NeedsResource long enough is temporarily
            // excluded so another enabled job can run instead of repeatedly selecting the same
            // unavailable input. Cargo-owning cycles are never blocked by this mechanism.
            enabled = WorkerResourceBackoffRuntime.FilterBlockedTasks(zdo, enabled);
            if (enabled == WorkerTaskFlags.None) return WorkOrder.None;

            int bestPriority = 5;
            WorkOrder[] tieOrder = GetPriorityTieOrder(role);
            for (int i = 0; i < tieOrder.Length; i++)
            {
                WorkOrder candidate = tieOrder[i];
                WorkerTaskFlags task = TaskForOrder(candidate);
                if (task == WorkerTaskFlags.None || (enabled & task) == 0 || !SupportsOrder(zdo, role, candidate)) continue;
                int priority = GetPriority(zdo, role, task);
                if (priority < bestPriority) bestPriority = priority;
            }

            if (bestPriority > 4) return WorkOrder.None;

            // Do not tear a worker out of a concrete movement/interaction/cargo cycle merely
            // because another sibling job has the same numeric priority. Once that one action is
            // complete and the worker returns to FindJob, the role-specific tie rules below apply.
            if (IsCommittedCurrentAction(zdo, role, current, enabled, bestPriority))
                return current;

            // Equal-priority Gardener work is intentionally fair rather than sticky: with both
            // Ernten and Pflanzen pending at the same level, harvest one real target, then give one
            // planting action first refusal, then return to harvest. This is persisted so loaded and
            // background scheduling agree. Unequal explicit priorities still override this.
            if (role == WorkerRole.Gardener)
            {
                WorkOrder gardenerTurn = SelectGardenerEqualPriorityTurn(zdo, enabled, bestPriority);
                if (gardenerTurn != WorkOrder.None) return gardenerTurn;
            }
            else if (role == WorkerRole.Gatherer)
            {
                WorkOrder gathererTurn = SelectGathererEqualPriorityTurn(zdo, enabled, bestPriority);
                if (gathererTurn != WorkOrder.None) return gathererTurn;
            }

            // v0.5.377: the old global anti-thrash rule returned the current module for every tie,
            // which could permanently starve Jagen behind Bewachen or Versorgen behind Sortieren.
            // At a safe boundary the role-specific order now wins instead.
            for (int i = 0; i < tieOrder.Length; i++)
            {
                WorkOrder candidate = tieOrder[i];
                WorkerTaskFlags task = TaskForOrder(candidate);
                if (task == WorkerTaskFlags.None || (enabled & task) == 0 || !SupportsOrder(zdo, role, candidate)) continue;
                if (GetPriority(zdo, role, task) == bestPriority) return candidate;
            }

            return WorkOrder.None;
        }

        internal static WorkOrder SelectBestEnabledOrder(ZDO zdo, WorkerRole role, WorkOrder current)
        {
            return SelectBestAvailableOrder(zdo, role, current, ReadTasks(zdo, role));
        }

        private static string GetPriorityKey(WorkerTaskFlags task)
        {
            return TaskPriorityPrefix + ((int)task).ToString();
        }

        private static int GetDefaultPriority(WorkerRole role, WorkerTaskFlags task)
        {
            // v0.5.234: Every job shown at a worker rest station starts neutral at priority 4.
            // Players can then deliberately promote the jobs that matter for that individual worker.
            // Explicit priorities already stored on a worker remain untouched; this only changes the
            // fallback for jobs that have never received a player-selected priority.
            return 4;
        }

        internal static bool TrySetTaskEnabled(ZDO zdo, WorkerRole role, WorkerTaskFlags task, bool enabled)
        {
            if (zdo == null) return false;
            WorkerTaskFlags roleMask = GetRoleMask(zdo, role);
            if (roleMask == WorkerTaskFlags.None || (task & roleMask) == 0) return false;

            WorkerTaskFlags current = ReadTasks(zdo, role);
            WorkerTaskFlags next = enabled ? current | task : current & ~task;
            next &= roleMask;

            // v0.5.333: an empty task mask is intentional and means "this Worker currently has
            // no allowed jobs". The workzone assignment stays intact, so the player can pause an
            // individual Worker without tearing down/reassigning its area. The normal scheduler
            // will park it at home until at least one checkbox is enabled again.

            if (next != current && WorkerZdo.HasCargo(zdo))
                WorkerZdo.MarkCargoDropPending(zdo, $"task profile changed: {current} -> {next}");
            zdo.Set(TaskMaskKey, (int)next);
            zdo.Set(TaskMaskSetKey, true);
            WorkerZdo.ClearTarget(zdo);
            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state != WorkerState.ReturnHomeForRest && state != WorkerState.Resting && state != WorkerState.Recover)
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            if ((task & WorkerTaskFlags.Provision) != 0)
                WorkerProvisioningLogisticsRuntime.NotifyWorkerTopologyChanged(zdo);
            return true;
        }

        internal static WorkerTaskFlags ResolveEffectiveGathererTasks(ZDO zdo)
        {
            return zdo == null ? WorkerTaskFlags.None : ReadTasks(zdo, WorkerRole.Gatherer);
        }

        internal static WorkerTaskFlags ResolveEffectiveGardenerTasks(ZDO zdo)
        {
            return zdo == null ? WorkerTaskFlags.None : ReadTasks(zdo, WorkerRole.Gardener);
        }

        internal static WorkerTaskFlags ResolveEffectiveHaulerTasks(ZDO zdo)
        {
            return zdo == null ? WorkerTaskFlags.None : ReadTasks(zdo, WorkerRole.Hauler);
        }

        /// <summary>
        /// Returns the compatibility WorkOrder stored on the worker when assigning a generic
        /// Workzone. The flag no longer selects jobs; this value is only the currently preferred
        /// runtime order / legacy compatibility anchor. Per-worker task settings remain authoritative.
        /// </summary>
        internal static WorkOrder ResolveAssignmentOrder(ZDO zdo, WorkerRole role)
        {
            if (role == WorkerRole.None) return WorkOrder.None;

            WorkOrder current = zdo != null
                ? (WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None)
                : WorkOrder.None;
            if (current != WorkOrder.None && SupportsOrder(zdo, role, current))
                return current;

            if (zdo != null && (role == WorkerRole.Gatherer || role == WorkerRole.Gardener || role == WorkerRole.Hauler))
            {
                WorkerTaskFlags tasks = ReadTasks(zdo, role);
                WorkOrder best = WorkOrder.None;
                int bestPriority = 5;
                for (int i = 0; i < WorkOrderCatalog.All.Length; i++)
                {
                    WorkOrder candidate = WorkOrderCatalog.All[i];
                    WorkerTaskFlags task = TaskForOrder(candidate);
                    if (task == WorkerTaskFlags.None || (tasks & task) == 0 || !SupportsOrder(zdo, role, candidate)) continue;
                    int priority = GetPriority(zdo, role, task);
                    if (priority < bestPriority)
                    {
                        bestPriority = priority;
                        best = candidate;
                    }
                }
                if (best != WorkOrder.None) return best;
            }

            // Stable role defaults when no modular task is enabled yet. Hunter deliberately
            // defaults to Hunt rather than Guard; Guard-role workers still default to Guard.
            if (role == WorkerRole.Hunter && SupportsOrder(zdo, role, WorkOrder.Hunt)) return WorkOrder.Hunt;
            if (role == WorkerRole.Gatherer && SupportsOrder(zdo, role, WorkOrder.GatherResources)) return WorkOrder.GatherResources;
            if (role == WorkerRole.Hauler && SupportsOrder(zdo, role, WorkOrder.Haul)) return WorkOrder.Haul;
            if (role == WorkerRole.Gardener && SupportsOrder(zdo, role, WorkOrder.Garden)) return WorkOrder.Garden;

            for (int i = 0; i < WorkOrderCatalog.All.Length; i++)
            {
                WorkOrder candidate = WorkOrderCatalog.All[i];
                if (WorkOrderCatalog.IsImplemented(candidate) && SupportsOrder(zdo, role, candidate))
                    return candidate;
            }
            return WorkOrder.None;
        }

        internal static bool SupportsOrder(ZDO zdo, WorkerRole role, WorkOrder order)
        {
            WorkerTaskFlags available = GetRoleMask(zdo, role);
            WorkerTaskFlags task = TaskForOrder(order);

            // v0.5.110: the persisted WorkerRole is now only an internal compatibility/runtime
            // anchor. Species specialties can be folded into that role mask (for example the
            // Fuling Shaman runs Garden/Forestry/Harvest/Bake without a manual role switch).
            // If the task is explicitly present in the worker's capability mask, it is supported
            // even when the legacy WorkOrderCatalog maps it to another historical role.
            if (task != WorkerTaskFlags.None && (available & task) != 0)
                return true;

            if (!WorkOrderCatalog.MatchesRole(order, role)) return false;
            return available == WorkerTaskFlags.None || task == WorkerTaskFlags.None;
        }

        internal static bool IsOrderEnabled(ZDO zdo, WorkerRole role, WorkOrder order)
        {
            if (!SupportsOrder(zdo, role, order)) return false;

            // v0.5.118: WorkOrder.Smith is the persistent compatibility anchor for the Fuling
            // craftsman profile. The actual player-facing modules are now Metal and Base Repair.
            // Keeping either module enabled must keep the assigned Workzone active; otherwise a
            // repair-only Fuling would be rejected before TickSmithInsideArea can run.
            if (role == WorkerRole.Smith && order == WorkOrder.Smith)
            {
                WorkerTaskFlags smithTasks = ReadTasks(zdo, role);
                return (smithTasks & (WorkerTaskFlags.Smith | WorkerTaskFlags.Repair)) != 0;
            }

            WorkerTaskFlags task = TaskForOrder(order);
            return task == WorkerTaskFlags.None || HasTask(zdo, role, task);
        }

        internal static bool CanHarvest(ZDO zdo)
        {
            if (zdo == null) return false;
            WorkerRole role = (WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
            if (role == WorkerRole.Gatherer)
                return (ResolveEffectiveGathererTasks(zdo) & WorkerTaskFlags.Harvest) != 0;
            if (role == WorkerRole.Gardener)
                return (ResolveEffectiveGardenerTasks(zdo) & WorkerTaskFlags.Harvest) != 0;
            return false;
        }

        internal static string DescribeHauler(WorkerTaskFlags flags)
        {
            var names = new System.Collections.Generic.List<string>(3);
            if ((flags & WorkerTaskFlags.Transport) != 0) names.Add("Transportieren");
            if ((flags & WorkerTaskFlags.Sort) != 0) names.Add("Sortieren");
            if ((flags & WorkerTaskFlags.Provision) != 0) names.Add("Versorgen");
            return names.Count == 0 ? "Keine" : string.Join(" + ", names);
        }

        internal static string DescribeGardener(WorkerTaskFlags flags)
        {
            var names = new System.Collections.Generic.List<string>(4);
            if ((flags & WorkerTaskFlags.Harvest) != 0) names.Add("Ernten");
            if ((flags & WorkerTaskFlags.Garden) != 0) names.Add("Pflanzen");
            if ((flags & WorkerTaskFlags.Forestry) != 0) names.Add("Foerstern");
            if ((flags & WorkerTaskFlags.Bake) != 0) names.Add("Kochen");
            return names.Count == 0 ? "Keine" : string.Join(" + ", names);
        }

        internal static string Describe(WorkerTaskFlags flags)
        {
            bool gather = (flags & WorkerTaskFlags.Gather) != 0;
            bool harvest = (flags & WorkerTaskFlags.Harvest) != 0;
            if (gather && harvest) return "Sammeln + Ernten";
            if (gather) return "Sammeln";
            if (harvest) return "Ernten";
            return "Keine";
        }
    }
}
