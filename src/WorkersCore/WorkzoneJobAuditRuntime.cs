using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// v0.5.381: on-demand diagnostic snapshot for one loaded Workzone.
    /// This runtime never installs a poll/update loop. Every probe is executed only when the player
    /// presses the audit button on the WorkFlag UI. The dry-run is read-only: it uses the existing
    /// economy forecast and current persistent job caches without advancing workers or ledgers.
    /// </summary>
    internal static class WorkzoneJobAuditRuntime
    {
        internal sealed class JobRow
        {
            internal string Name = string.Empty;
            internal int EnabledWorkers;
            internal int ReadyWorkers;
            internal int WaitingWorkers;
            internal int BackgroundReadyWorkers;
            internal bool BackgroundSupported = true;
            internal string Note = string.Empty;
            internal readonly Dictionary<string, int> WaitReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        internal sealed class WorkerRow
        {
            internal string Name = string.Empty;
            internal string Role = string.Empty;
            internal string State = string.Empty;
            internal string CurrentJob = string.Empty;
            internal string Food = string.Empty;
            internal string Jobs = string.Empty;
            internal string AvailableJobs = string.Empty;
            internal string DisabledJobs = string.Empty;
            internal string JobStatus = string.Empty;
            internal string Decision = string.Empty;
            internal string Warning = string.Empty;
        }

        internal sealed class Snapshot
        {
            internal DateTime CreatedUtc;
            internal int WorkerCount;
            internal int EnabledJobAssignments;
            internal int ReadyJobAssignments;
            internal int WarningCount;
            internal readonly List<JobRow> Jobs = new List<JobRow>();
            internal readonly List<WorkerRow> Workers = new List<WorkerRow>();
            internal readonly List<string> Issues = new List<string>();
            internal readonly List<string> WildGrowth = new List<string>();
            internal readonly List<string> Logistics = new List<string>();
            internal readonly List<string> DryRun = new List<string>();
        }

        private readonly struct TaskDef
        {
            internal readonly WorkerTaskFlags Task;
            internal readonly string Name;
            internal readonly WorkOrder Order;

            internal TaskDef(WorkerTaskFlags task, string name, WorkOrder order)
            {
                Task = task;
                Name = name;
                Order = order;
            }
        }

        private static readonly TaskDef[] TaskDefs =
        {
            new TaskDef(WorkerTaskFlags.Gather, "Sammeln", WorkOrder.GatherResources),
            new TaskDef(WorkerTaskFlags.Harvest, "Ernten", WorkOrder.Harvest),
            new TaskDef(WorkerTaskFlags.Garden, "Pflanzen", WorkOrder.Garden),
            new TaskDef(WorkerTaskFlags.Forestry, "Foerstern", WorkOrder.Forestry),
            new TaskDef(WorkerTaskFlags.Transport, "Transportieren", WorkOrder.Haul),
            new TaskDef(WorkerTaskFlags.Sort, "Sortieren", WorkOrder.Sort),
            new TaskDef(WorkerTaskFlags.Provision, "Versorgen", WorkOrder.Provision),
            new TaskDef(WorkerTaskFlags.FellTrees, "Holz faellen", WorkOrder.FellTrees),
            new TaskDef(WorkerTaskFlags.Stonework, "Stein / Erz abbauen", WorkOrder.Stonework),
            new TaskDef(WorkerTaskFlags.KeepFires, "Feuer hueten", WorkOrder.KeepFires),
            new TaskDef(WorkerTaskFlags.Hunt, "Jagen", WorkOrder.Hunt),
            new TaskDef(WorkerTaskFlags.Guard, "Bewachen", WorkOrder.Guard),
            new TaskDef(WorkerTaskFlags.Bake, "Kochen / Produktion", WorkOrder.Bake),
            new TaskDef(WorkerTaskFlags.Butcher, "Tierhaltung / Metzger", WorkOrder.Butcher),
            new TaskDef(WorkerTaskFlags.Smith, "Metall", WorkOrder.Smith),
            new TaskDef(WorkerTaskFlags.Repair, "Reparieren", WorkOrder.Smith),
            new TaskDef(WorkerTaskFlags.Fish, "Fischen", WorkOrder.Fish)
        };

        internal static Snapshot Build(string areaId, Vector3 center, float radius, IList<Character> loadedWorkers)
        {
            Snapshot result = new Snapshot { CreatedUtc = DateTime.UtcNow };
            Dictionary<WorkerTaskFlags, JobRow> rows = new Dictionary<WorkerTaskFlags, JobRow>();
            for (int i = 0; i < TaskDefs.Length; i++)
            {
                TaskDef def = TaskDefs[i];
                BackgroundJobDescriptor descriptor = BackgroundJobCatalog.Get(def.Order);
                bool background = def.Task == WorkerTaskFlags.Repair || descriptor.ImplementedNow;
                JobRow row = new JobRow
                {
                    Name = def.Name,
                    BackgroundSupported = background,
                    Note = background ? string.Empty : "kein Background-Adapter"
                };
                rows[def.Task] = row;
                result.Jobs.Add(row);
            }

            HashSet<ZDOID> seen = new HashSet<ZDOID>();
            if (loadedWorkers != null)
            {
                for (int i = 0; i < loadedWorkers.Count; i++)
                {
                    Character worker = loadedWorkers[i];
                    if (worker == null || worker.IsDead()) continue;
                    ZNetView view = worker.GetComponent<ZNetView>();
                    ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                    if (!WorkerZdo.IsWorker(zdo)) continue;
                    if (!string.Equals(WorkerZdo.ReadWorkAreaId(zdo), areaId ?? string.Empty, StringComparison.Ordinal)) continue;
                    if (!seen.Add(zdo.m_uid)) continue;

                    result.WorkerCount++;
                    WorkerRole role = ReadRoleReadOnly(zdo);
                    WorkerTaskFlags enabled = ReadTasksReadOnly(zdo, role);
                    WorkerRow workerRow = BuildWorkerRow(worker, zdo, role, center, radius, enabled);
                    result.Workers.Add(workerRow);
                    if (!string.IsNullOrEmpty(workerRow.Warning))
                    {
                        result.WarningCount++;
                        result.Issues.Add(workerRow.Name + ": " + workerRow.Warning);
                    }

                    for (int d = 0; d < TaskDefs.Length; d++)
                    {
                        TaskDef def = TaskDefs[d];
                        if ((enabled & def.Task) == 0) continue;
                        if (!rows.TryGetValue(def.Task, out JobRow row)) continue;
                        row.EnabledWorkers++;
                        result.EnabledJobAssignments++;

                        bool ready = ProbeReady(worker, zdo, role, def.Task, out string reason);
                        if (ready)
                        {
                            row.ReadyWorkers++;
                            result.ReadyJobAssignments++;
                            if (row.BackgroundSupported) row.BackgroundReadyWorkers++;
                        }
                        else
                        {
                            row.WaitingWorkers++;
                            string detailed = DescribeProbeBlocker(zdo, def, reason);
                            AddReason(row.WaitReasons, detailed);
                        }

                        int priority = WorkerJobProfileRuntime.GetPriority(zdo, role, def.Task);
                        AppendJobStatus(workerRow, def.Name, priority, ready, ready ? string.Empty : DescribeProbeBlocker(zdo, def, reason), row.BackgroundSupported);
                    }
                }
            }

            for (int i = 0; i < result.Jobs.Count; i++)
            {
                JobRow row = result.Jobs[i];
                string waits = FormatReasons(row.WaitReasons, 3);
                if (!row.BackgroundSupported)
                    row.Note = string.IsNullOrEmpty(waits) ? "kein Background-Adapter" : "kein Background-Adapter • wartet: " + waits;
                else if (!string.IsNullOrEmpty(waits))
                    row.Note = "wartet: " + waits;
            }

            // Keep only jobs that are actually enabled in this Workzone. This makes large bases readable.
            for (int i = result.Jobs.Count - 1; i >= 0; i--)
                if (result.Jobs[i].EnabledWorkers <= 0) result.Jobs.RemoveAt(i);

            BuildWildGrowthAudit(result, areaId, center, radius);
            BuildLogisticsAudit(result, areaId);
            BuildDryRun(result, areaId, center, radius);
            if (result.WorkerCount == 0)
                result.Issues.Add("Keine geladenen Worker dieser Workzone gefunden.");
            if (result.EnabledJobAssignments > 0 && result.ReadyJobAssignments == 0)
                result.Issues.Add("Kein aktivierter Job meldet im Moment konkreten Bedarf. Das kann normal sein, wenn Lager/Ziele voll sind.");
            return result;
        }

        private static WorkerRole ReadRoleReadOnly(ZDO zdo)
        {
            if (zdo == null) return WorkerRole.None;
            WorkerRole current = (WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
            string species = SpeciesRoleRegistry.NormalizePrefabName(zdo.GetString(WorkerZdo.SpeciesKey, string.Empty));
            return !string.IsNullOrEmpty(species) && SpeciesRoleRegistry.TryGetRole(species, out WorkerRole stable)
                ? stable
                : current;
        }

        private static WorkerTaskFlags ReadTasksReadOnly(ZDO zdo, WorkerRole role)
        {
            if (zdo == null) return WorkerTaskFlags.None;
            WorkerTaskFlags available = WorkerJobProfileRuntime.GetAvailableTasks(zdo, role);
            if (available == WorkerTaskFlags.None) return WorkerTaskFlags.None;

            if (zdo.GetBool(WorkerJobProfileRuntime.TaskMaskSetKey, false))
                return (WorkerTaskFlags)zdo.GetInt(WorkerJobProfileRuntime.TaskMaskKey, 0) & available;

            WorkOrder current = (WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None);
            if (role == WorkerRole.Gatherer)
            {
                if (current == WorkOrder.Harvest) return WorkerTaskFlags.Harvest & available;
                if (current == WorkOrder.GatherResources) return WorkerTaskFlags.Gather & available;
                return available;
            }
            if (role == WorkerRole.Hauler)
            {
                if (current == WorkOrder.Sort) return (WorkerTaskFlags.Sort | WorkerTaskFlags.Provision) & available;
                if (current == WorkOrder.Provision) return WorkerTaskFlags.Provision & available;
                if (current == WorkOrder.Haul) return WorkerTaskFlags.Transport & available;
                return available;
            }
            if (role == WorkerRole.Gardener)
            {
                if (current == WorkOrder.Garden) return WorkerTaskFlags.Garden & available;
                if (current == WorkOrder.Forestry) return WorkerTaskFlags.Forestry & available;
                if (current == WorkOrder.Harvest) return WorkerTaskFlags.Harvest & available;
                if (current == WorkOrder.Bake) return WorkerTaskFlags.Bake & available;
                return available;
            }
            return available;
        }

        private static WorkerRow BuildWorkerRow(Character worker, ZDO zdo, WorkerRole role, Vector3 center, float radius, WorkerTaskFlags enabled)
        {
            string species = SpeciesRoleRegistry.NormalizePrefabName(worker != null ? worker.gameObject.name : zdo.GetString(WorkerZdo.SpeciesKey, string.Empty));
            if (string.IsNullOrEmpty(species)) species = "Worker";
            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            WorkOrder current = (WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None);
            WorkerTaskFlags available = WorkerJobProfileRuntime.GetAvailableTasks(zdo, role);
            WorkerTaskFlags disabled = available & ~enabled;
            string decision = DescribeLastDecision(zdo);
            string warning = string.Empty;

            if (!WorkerWorkAreaRules.TryValidateHomeForArea(zdo, center, radius, out string homeReason))
                warning = string.IsNullOrEmpty(homeReason) ? "Schlafplatz/HomeAnchor ausserhalb der Workzone" : homeReason;
            else if (enabled == WorkerTaskFlags.None)
                warning = "alle Jobs deaktiviert" + (available != WorkerTaskFlags.None ? " (verfuegbar: " + DescribeTaskMask(zdo, role, available) + ")" : string.Empty);
            else if (state == WorkerState.Stuck)
                warning = string.IsNullOrEmpty(decision) ? "Worker steht auf STUCK" : "STUCK: " + decision;
            else if (state == WorkerState.NeedsResource)
                warning = string.IsNullOrEmpty(decision) ? "wartet auf Ressource" : "wartet auf Ressource: " + decision;

            string food = WorkerSustenanceRuntime.GetAuditSummary(zdo, out bool hungry);
            if (hungry && string.IsNullOrEmpty(warning)) warning = "hungrig / Versorgung fehlt";

            return new WorkerRow
            {
                Name = species,
                Role = role.ToString(),
                State = state.ToString(),
                CurrentJob = WorkOrderCatalog.DisplayName(current),
                Food = food,
                Jobs = DescribeTaskMask(zdo, role, enabled),
                AvailableJobs = DescribeTaskMask(zdo, role, available),
                DisabledJobs = disabled == WorkerTaskFlags.None ? string.Empty : DescribeTaskMask(zdo, role, disabled),
                Decision = decision,
                Warning = warning
            };
        }

        private static string DescribeLastDecision(ZDO zdo)
        {
            if (zdo == null) return string.Empty;
            string reason = zdo.GetString(WorkerZdo.LastDecisionReason, string.Empty);
            if (string.IsNullOrWhiteSpace(reason)) return string.Empty;
            long ticks = zdo.GetLong(WorkerZdo.LastDecisionTicks, 0L);
            if (ticks <= 0L) return reason;
            double seconds;
            try { seconds = Math.Max(0.0, (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds); }
            catch { return reason; }
            string age = seconds < 90.0 ? $"{seconds:0}s" : seconds < 5400.0 ? $"{seconds / 60.0:0.0}m" : $"{seconds / 3600.0:0.0}h";
            return reason + " (vor " + age + ")";
        }

        private static string DescribeTaskMask(ZDO zdo, WorkerRole role, WorkerTaskFlags mask)
        {
            List<string> items = new List<string>();
            for (int i = 0; i < TaskDefs.Length; i++)
            {
                TaskDef def = TaskDefs[i];
                if ((mask & def.Task) == 0) continue;
                int p = WorkerJobProfileRuntime.GetPriority(zdo, role, def.Task);
                items.Add(def.Name + " P" + p);
            }
            return items.Count == 0 ? "keine" : string.Join(" • ", items.ToArray());
        }


        private static void AppendJobStatus(WorkerRow row, string job, int priority, bool ready, string reason, bool backgroundSupported)
        {
            if (row == null) return;
            string item = job + " P" + priority + " " + (ready ? "✓ bereit" : "• " + (string.IsNullOrEmpty(reason) ? "wartet" : reason));
            item += backgroundSupported ? " [BG]" : " [nur geladen]";
            row.JobStatus = string.IsNullOrEmpty(row.JobStatus) ? item : row.JobStatus + "  |  " + item;
        }

        private static void AddReason(Dictionary<string, int> reasons, string reason)
        {
            if (reasons == null) return;
            string key = string.IsNullOrWhiteSpace(reason) ? "kein aktueller Bedarf" : reason.Trim();
            reasons.TryGetValue(key, out int count);
            reasons[key] = count + 1;
        }

        private static string FormatReasons(Dictionary<string, int> reasons, int max)
        {
            if (reasons == null || reasons.Count == 0) return string.Empty;
            List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>(reasons);
            list.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
            List<string> parts = new List<string>();
            for (int i = 0; i < list.Count && i < Math.Max(1, max); i++)
                parts.Add(list[i].Value > 1 ? list[i].Value + "× " + list[i].Key : list[i].Key);
            if (list.Count > max) parts.Add("+" + (list.Count - max) + " weitere");
            return string.Join(" • ", parts.ToArray());
        }

        private static string DescribeProbeBlocker(ZDO zdo, TaskDef def, string fallback)
        {
            string reason = string.IsNullOrWhiteSpace(fallback) ? "kein aktueller Bedarf" : fallback.Trim();
            if (zdo == null) return reason;

            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            WorkOrder current = (WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None);
            bool sameJob = current == def.Order || (def.Task == WorkerTaskFlags.Repair && current == WorkOrder.Smith);
            if (sameJob && state == WorkerState.NeedsResource)
            {
                string decision = DescribeLastDecision(zdo);
                if (!string.IsNullOrEmpty(decision)) return decision;
            }
            return reason;
        }

        private static bool ProbeReady(Character worker, ZDO zdo, WorkerRole role, WorkerTaskFlags task, out string reason)
        {
            reason = "kein aktueller Bedarf";
            try
            {
                switch (task)
                {
                    case WorkerTaskFlags.Gather:
                        if (BackgroundGathererRuntime.HasBackgroundWork(zdo)) { reason = string.Empty; return true; }
                        if (worker != null &&
                            WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 gatherCenter, out float gatherRadius))
                        {
                            GathererTargetResult gather = GathererTargetFinder.FindNearestAddable(
                                worker, zdo, gatherCenter, gatherRadius, worker.transform.position,
                                drop => ProductionDemandRuntime.AllowsPrefab(zdo, GathererTargetFinder.GetCargoPrefabName(drop)));
                            if (gather.Target != null) { reason = string.Empty; return true; }
                            reason = WorkerCargoRuntime.IsFull(worker, zdo)
                                ? "Worker-Cargo voll"
                                : "keine erlaubten losen Ressourcen";
                            return false;
                        }
                        reason = "kein Gather-Snapshot / Worker nicht geladen";
                        return false;
                    case WorkerTaskFlags.Harvest:
                        if (BackgroundHarvesterRuntime.HasBackgroundWork(zdo)) { reason = string.Empty; return true; }
                        if (worker != null &&
                            WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 harvestCenter, out float harvestRadius))
                        {
                            GardenHarvestTargetResult harvest = GardenHarvestTargetFinder.FindNearest(
                                harvestCenter, harvestRadius, worker.transform.position,
                                pickable => ProductionDemandRuntime.AllowsHarvestTarget(zdo, pickable));
                            if (harvest.Target != null) { reason = string.Empty; return true; }
                            reason = "keine reife erlaubte Pflanze";
                            return false;
                        }
                        reason = "kein Harvest-Snapshot / Worker nicht geladen";
                        return false;
                    case WorkerTaskFlags.Garden:
                        if (GardenBackgroundRuntime.HasBackgroundGardenWork(zdo)) { reason = string.Empty; return true; }
                        return false;
                    case WorkerTaskFlags.Forestry:
                        if (ForestryBackgroundRuntime.HasBackgroundWork(zdo)) { reason = string.Empty; return true; }
                        return false;
                    case WorkerTaskFlags.Transport:
                        if (WorkerZdo.HasCargo(zdo) || BackgroundGroundPickupRuntime.HasPersistentPickupWork(zdo)) { reason = string.Empty; return true; }
                        return false;
                    case WorkerTaskFlags.Sort:
                        if (BackgroundSorterRuntime.HasPersistentSortWork(zdo)) { reason = string.Empty; return true; }
                        return false;
                    case WorkerTaskFlags.Provision:
                        if (WorkerProvisioningLogisticsRuntime.HasPersistentDemand(zdo)) { reason = string.Empty; return true; }
                        return false;
                    case WorkerTaskFlags.FellTrees:
                        if (WoodResourceLedgerRuntime.TryGetRemainingTrees(zdo, out int trees) && trees > 0) { reason = string.Empty; return true; }
                        if (worker != null &&
                            WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 woodCenter, out float woodRadius))
                        {
                            int maxToolTier = WorkerSkillCatalog.GetWoodcutTier(worker);
                            TreeTargetResult wood = TreeTargetFinder.FindNearest(
                                woodCenter, woodRadius, worker.transform.position, maxToolTier, zdo,
                                target => ProductionDemandRuntime.AllowsWoodTarget(zdo, target));
                            if (wood.Target != null) { reason = string.Empty; return true; }
                            reason = TreeTargetFinder.LastTierRejectedCount > 0
                                ? $"nur zu harte Holz-Ziele ({TreeTargetFinder.LastTierRejectedCount})"
                                : "keine erlaubten Holz-Ziele";
                            return false;
                        }
                        reason = "kein gecachter Baum-Bestand / Worker nicht geladen";
                        return false;
                    case WorkerTaskFlags.Stonework:
                        if (StoneResourceLedgerRuntime.TryGetRemaining(zdo, out int stone) && stone > 0) { reason = string.Empty; return true; }
                        reason = "kein gecachter Stein/Erz-Bestand";
                        return false;
                    case WorkerTaskFlags.KeepFires:
                        if (WorkerZdo.TryReadFireAnchor(zdo, out ZDOID fireId, out _) && fireId != ZDOID.None)
                        {
                            reason = string.Empty;
                            return true;
                        }

                        // v0.5.443: this is an explicit player-triggered Workzone audit, so it is
                        // safe to perform the same bounded loaded-zone probes used by the real
                        // Firekeeper. Older audit code returned READY for every roaming Surtling,
                        // even when every fire/oven/furnace was already full.
                        if (worker != null &&
                            WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 fireCenter, out float fireRadius))
                        {
                            Vector3 from = worker.transform.position;

                            if (FirekeeperWorkRuntime.TryFindNearestNeedingFuel(
                                    fireCenter, fireRadius, from, null,
                                    out Smelter _, out Vector3 _, out string _))
                            {
                                reason = string.Empty;
                                return true;
                            }

                            if (FirekeeperOvenRuntime.TryFindNearestNeedingFuel(
                                    fireCenter, fireRadius, from,
                                    out Smelter _, out Vector3 _, out string _))
                            {
                                reason = string.Empty;
                                return true;
                            }

                            if (FirekeeperZoneFireRuntime.TryFindNearestNeedingFuel(
                                    fireCenter, fireRadius, from,
                                    out Fireplace _, out Vector3 _, out string _))
                            {
                                reason = string.Empty;
                                return true;
                            }

                            reason = "alle geladenen Feuer / Steinoefen / Metalloefen versorgt";
                            return false;
                        }

                        reason = "kein fester Feueranker; geladener Maschinenbedarf nicht pruefbar";
                        return false;
                    case WorkerTaskFlags.Hunt:
                        if (BackgroundHunterRuntime.HasBackgroundWork(zdo)) { reason = string.Empty; return true; }
                        if (worker != null &&
                            WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 huntCenter, out float huntRadius))
                        {
                            if (!HunterStandMarker.TryFindValid(huntCenter, huntRadius, out _))
                            {
                                reason = "keine gueltige Jagdplattform";
                                return false;
                            }

                            if (HunterWorkRuntime.HasAvailablePrey(zdo, huntCenter, huntRadius, worker.transform.position))
                            {
                                reason = string.Empty;
                                return true;
                            }

                            reason = $"keine erlaubte Beute ({HunterPreyFilterRuntime.DescribeAllowed(zdo)})";
                            return false;
                        }
                        reason = "keine simulierbare Beute / kein Lagerplatz";
                        return false;
                    case WorkerTaskFlags.Guard:
                        reason = string.Empty;
                        return true;
                    case WorkerTaskFlags.Bake:
                        if (BackgroundBakerRuntime.HasBackgroundWork(zdo)) { reason = string.Empty; return true; }
                        reason = "kein Produktionsbedarf / keine Zutaten";
                        return false;
                    case WorkerTaskFlags.Butcher:
                        if (HasButcherSurplus(zdo, out reason)) return true;
                        return false;
                    case WorkerTaskFlags.Smith:
                        if (BackgroundSmithMetalRuntime.HasDemand(zdo)) { reason = string.Empty; return true; }
                        reason = "kein Metall-Produktionsbedarf";
                        return false;
                    case WorkerTaskFlags.Repair:
                        if (worker != null)
                        {
                            int backlog = BaseRepairWorkRuntime.CountBackgroundRepairBacklog(worker, zdo);
                            if (backlog > 0) { reason = string.Empty; return true; }
                        }
                        reason = "keine beschaedigten Bauteile";
                        return false;
                    case WorkerTaskFlags.Fish:
                        if (BackgroundFisherRuntime.HasBackgroundWork(zdo)) { reason = string.Empty; return true; }
                        if (worker != null &&
                            WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 fishCenter, out float fishRadius))
                        {
                            if (FisherWorkRuntime.TryFindFish(worker, zdo, fishCenter, fishRadius, out _, out _))
                            {
                                reason = string.Empty;
                                return true;
                            }

                            reason = WorkerCargoRuntime.IsFull(worker, zdo)
                                ? "Worker-Cargo voll"
                                : "kein erlaubter echter Fisch im geladenen Bereich";
                            return false;
                        }
                        reason = "keine lokal gemerkte Fischart / kein Depotplatz";
                        return false;
                    case WorkerTaskFlags.Mine:
                        reason = "Legacy-Bergbau wurde in Stein / Erz abbauen zusammengefuehrt";
                        return false;
                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                reason = "Probe fehlgeschlagen: " + ex.GetType().Name;
                return false;
            }
        }

        private static bool HasButcherSurplus(ZDO zdo, out string reason)
        {
            reason = "kein Tier-Ueberschuss";
            List<string> species = LivestockButcherRuntime.GetRuleSpecies(null, zdo);
            if (species == null || species.Count == 0)
            {
                reason = "keine Schlachtregeln gesetzt";
                return false;
            }

            for (int i = 0; i < species.Count; i++)
            {
                string animal = species[i];
                int total = LivestockButcherRuntime.CountTamedInAssignedArea(zdo, animal, out int adults, out _);
                LivestockButcherRuntime.GetRuleReadOnly(zdo, animal, out int min, out int max);
                int surplus = Math.Min(Math.Max(0, total - max), Math.Max(0, adults - min));
                if (surplus > 0)
                {
                    reason = string.Empty;
                    return true;
                }
            }
            return false;
        }

        private sealed class WildGrowthAuditCounter
        {
            internal string ResourcePrefab = string.Empty;
            internal int Planned;
            internal int PendingGhost;
            internal int Completed;
            internal int Physical;
            internal int Ready;
            internal int Respawn;
            internal int PresentNotReady;
            internal int Missing;
            internal int ActiveWithPhysical;
            internal int DuplicatePhysical;
        }

        /// <summary>
        /// v0.5.384: explicit, read-only WildGrowth lifecycle diagnosis. It runs only when the
        /// player presses "Workzone jetzt testen". The persistent plan is read from the WorkFlag;
        /// physical plants come from WorkerWorldIndex. One bounded local index-repair is allowed
        /// for this manual diagnostic so a streamed-in Pickable cannot be reported missing merely
        /// because its Awake registration was skipped. No recurring scan/update loop is installed.
        /// </summary>
        private static void BuildWildGrowthAudit(Snapshot result, string areaId, Vector3 center, float radius)
        {
            result.WildGrowth.Clear();
            try
            {
                List<GardenWildGrowthAuditSlot> slots = new List<GardenWildGrowthAuditSlot>(64);
                GardenReplantPlanRuntime.CollectWildGrowthAuditSlots(areaId, slots);
                if (slots.Count == 0)
                {
                    result.WildGrowth.Add("Keine geplanten Workers-WildGrowth-Pflanzen in dieser Workzone.");
                    return;
                }

                Dictionary<int, GardenWildGrowthAuditSlot> bySlot = new Dictionary<int, GardenWildGrowthAuditSlot>();
                Dictionary<string, WildGrowthAuditCounter> counters = new Dictionary<string, WildGrowthAuditCounter>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < slots.Count; i++)
                {
                    GardenWildGrowthAuditSlot slot = slots[i];
                    bySlot[slot.SlotId] = slot;
                    string resource = WorkerStorageRuntime.NormalizeObjectName(slot.ResourcePrefab ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(resource)) resource = slot.HarvestPrefab ?? "WildGrowth";
                    if (!counters.TryGetValue(resource, out WildGrowthAuditCounter counter))
                    {
                        counter = new WildGrowthAuditCounter { ResourcePrefab = resource };
                        counters[resource] = counter;
                    }
                    counter.Planned++;
                    if (slot.Active) counter.PendingGhost++;
                    else counter.Completed++;
                }

                // One on-demand local repair is diagnostic-only and never runs in Update/Tick.
                int repaired = WorkerWorldIndex.RepairLoadedPickablesNear(center, Mathf.Max(5f, radius + 3f));
                HashSet<int> seenPhysicalSlots = new HashSet<int>();
                int indexed = WorkerWorldIndex.ForEachPickable(center, Mathf.Max(5f, radius + 3f), pickable =>
                {
                    if (pickable == null) return;
                    ZNetView nview = SmartReplantBridge.FindNetworkView(pickable);
                    if (nview == null) nview = pickable.GetComponent<ZNetView>() ?? pickable.GetComponentInParent<ZNetView>();
                    if (!GardenTargetFinder.TryReadWildGrowthMarker(nview, out int slotId, out string plantArea)) return;
                    if (!string.Equals(plantArea, areaId ?? string.Empty, StringComparison.Ordinal)) return;
                    if (!bySlot.TryGetValue(slotId, out GardenWildGrowthAuditSlot slot)) return;

                    string resource = WorkerStorageRuntime.NormalizeObjectName(slot.ResourcePrefab ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(resource)) resource = slot.HarvestPrefab ?? "WildGrowth";
                    if (!counters.TryGetValue(resource, out WildGrowthAuditCounter counter)) return;

                    if (!seenPhysicalSlots.Add(slotId))
                    {
                        counter.DuplicatePhysical++;
                        return;
                    }

                    counter.Physical++;
                    if (slot.Active) counter.ActiveWithPhysical++;
                    try
                    {
                        if (pickable.GetPicked())
                        {
                            counter.Respawn++;
                        }
                        else if (pickable.CanBePicked())
                        {
                            counter.Ready++;
                        }
                        else
                        {
                            counter.PresentNotReady++;
                        }
                    }
                    catch
                    {
                        counter.PresentNotReady++;
                    }
                });

                // Only completed/inactive slots are expected to own a permanent physical plant.
                for (int i = 0; i < slots.Count; i++)
                {
                    GardenWildGrowthAuditSlot slot = slots[i];
                    if (slot.Active || seenPhysicalSlots.Contains(slot.SlotId)) continue;
                    string resource = WorkerStorageRuntime.NormalizeObjectName(slot.ResourcePrefab ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(resource)) resource = slot.HarvestPrefab ?? "WildGrowth";
                    if (counters.TryGetValue(resource, out WildGrowthAuditCounter counter)) counter.Missing++;
                }

                result.WildGrowth.Add($"Nur dieser Knopfdruck: {indexed} geladene Pickables im Bereich geprueft" +
                    (repaired > 0 ? $", Index lokal +{repaired} repariert." : "."));

                List<string> resources = new List<string>(counters.Keys);
                resources.Sort(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < resources.Count; i++)
                {
                    WildGrowthAuditCounter counter = counters[resources[i]];
                    string name = ProductionMetricsRuntime.DisplayName(counter.ResourcePrefab);
                    result.WildGrowth.Add($"{name}: geplant {counter.Planned} | Ghost {counter.PendingGhost} | physisch {counter.Physical} | reif {counter.Ready} | Respawn {counter.Respawn} | fehlt {counter.Missing}" +
                        (counter.PresentNotReady > 0 ? $" | sonst {counter.PresentNotReady}" : string.Empty));

                    if (counter.Missing > 0)
                    {
                        result.WarningCount++;
                        result.Issues.Add($"WildGrowth {name}: {counter.Missing} abgeschlossene Slot(s) haben kein geladenes markiertes Pflanzenobjekt.");
                    }
                    if (counter.ActiveWithPhysical > 0)
                    {
                        result.WarningCount++;
                        result.Issues.Add($"WildGrowth {name}: {counter.ActiveWithPhysical} Ghost-Slot(s) sind noch aktiv, obwohl die physische Pflanze bereits existiert.");
                    }
                    if (counter.DuplicatePhysical > 0)
                    {
                        result.WarningCount++;
                        result.Issues.Add($"WildGrowth {name}: {counter.DuplicatePhysical} doppeltes physisches Objekt mit derselben Slot-ID erkannt.");
                    }
                }
            }
            catch (Exception ex)
            {
                result.WildGrowth.Add("WildGrowth-Diagnose fehlgeschlagen: " + ex.GetType().Name);
            }
        }

        private static void BuildLogisticsAudit(Snapshot result, string areaId)
        {
            result.Logistics.Clear();
            if (string.IsNullOrEmpty(areaId)) return;
            try
            {
                List<FieldSupplyDemandCacheRuntime.PoolDemandSnapshot> rows =
                    new List<FieldSupplyDemandCacheRuntime.PoolDemandSnapshot>(16);
                FieldSupplyDemandCacheRuntime.CollectPoolDemandSnapshots(areaId, 0L, rows);
                if (rows.Count == 0)
                {
                    result.Logistics.Add("Keine aktiven Felddepot-Zielbestaende/Auftraege in dieser Workzone.");
                    return;
                }

                rows.Sort((a, b) => string.Compare(
                    ProductionMetricsRuntime.DisplayName(a.Prefab),
                    ProductionMetricsRuntime.DisplayName(b.Prefab),
                    StringComparison.OrdinalIgnoreCase));
                for (int i = 0; i < rows.Count && i < 24; i++)
                {
                    FieldSupplyDemandCacheRuntime.PoolDemandSnapshot row = rows[i];
                    string name = ProductionMetricsRuntime.DisplayName(row.Prefab);
                    result.Logistics.Add($"{name}: Ziel {row.Target} | Bestand {row.Current} | unterwegs {row.InTransit} | Sortierer-reserviert {row.StagingClaimed} | offen {row.OpenNeed}");
                }
                if (rows.Count > 24)
                    result.Logistics.Add($"... plus {rows.Count - 24} weitere Felddepot-Auftraege.");
            }
            catch (Exception ex)
            {
                result.Logistics.Add("Felddepot-Diagnose fehlgeschlagen: " + ex.GetType().Name);
            }
        }

        private static void BuildDryRun(Snapshot result, string areaId, Vector3 center, float radius)
        {
            result.DryRun.Clear();
            result.DryRun.Add("Dry Run: 1 Valheim-Tag (nur lesen, veraendert nichts)");
            try
            {
                WorkzoneEconomySnapshot economy = WorkzoneEconomyRuntime.GetSnapshot(areaId, center, radius);
                if (economy == null)
                {
                    result.DryRun.Add("Wirtschaftsmodell nicht verfuegbar.");
                    return;
                }

                int shown = 0;
                for (int i = 0; i < economy.Rows.Count && shown < 12; i++)
                {
                    WorkzoneEconomyResourceRow row = economy.Rows[i];
                    if (row == null || !row.HasForecastActivity) continue;
                    string name = ProductionMetricsRuntime.DisplayName(row.Prefab);
                    double prod = row.ProductionPerDay;
                    double cons = row.ConsumptionPerDay;
                    double net = row.BalancePerDay;
                    result.DryRun.Add($"{name}: +{prod:0.0} / -{cons:0.0} => {(net >= 0 ? "+" : string.Empty)}{net:0.0}");
                    shown++;
                }

                if (shown == 0)
                    result.DryRun.Add("Noch keine modellierten Ressourcenkreise in dieser Workzone.");
                result.DryRun.Add($"Worker-Nahrung: {economy.SupplyReadyWorkerCount}/{economy.SustenanceWorkerCount} versorgt");
                result.DryRun.Add($"Job-Bereitschaft: {result.ReadyJobAssignments}/{result.EnabledJobAssignments} aktiv oder mit aktuellem Bedarf");
            }
            catch (Exception ex)
            {
                result.DryRun.Add("Dry Run fehlgeschlagen: " + ex.GetType().Name);
            }
        }
    }
}
