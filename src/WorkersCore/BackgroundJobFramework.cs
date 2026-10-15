using System;

namespace Valheim_Workers
{
    internal enum BackgroundSimulationKind
    {
        None = 0,
        Transfer = 1,
        ResourceRate = 2,
        Production = 3,
        Defense = 4
    }

    internal readonly struct BackgroundJobDescriptor
    {
        internal readonly WorkOrder Order;
        internal readonly BackgroundSimulationKind Kind;
        internal readonly float Efficiency;
        internal readonly bool ImplementedNow;
        internal readonly string Name;

        internal BackgroundJobDescriptor(WorkOrder order, BackgroundSimulationKind kind, float efficiency, bool implementedNow, string name)
        {
            Order = order;
            Kind = kind;
            Efficiency = efficiency;
            ImplementedNow = implementedNow;
            Name = name ?? order.ToString();
        }
    }

    /// <summary>
    /// Central declaration of how every worker job is expected to behave once its zone is unloaded.
    /// Jobs are activated incrementally only after a persistent source/consumption model exists.
    /// Transport, stone, wood and planned forestry now share this scheduler; later jobs plug into
    /// the same framework instead of inventing separate offline systems.
    /// </summary>
    internal static class BackgroundJobCatalog
    {
        internal const float DefaultOfflineEfficiency = 0.80f;

        internal static BackgroundJobDescriptor Get(WorkOrder order)
        {
            switch (order)
            {
                case WorkOrder.Haul:
                    return new BackgroundJobDescriptor(order, BackgroundSimulationKind.Transfer, 1f, true, "transport");
                case WorkOrder.Sort:
                    return new BackgroundJobDescriptor(order, BackgroundSimulationKind.Transfer, DefaultOfflineEfficiency, true, "sorting");
                case WorkOrder.Provision:
                    return new BackgroundJobDescriptor(order, BackgroundSimulationKind.Transfer, DefaultOfflineEfficiency, true, "provisioning");
                case WorkOrder.KeepFires:
                    return new BackgroundJobDescriptor(order, BackgroundSimulationKind.Production, 1f, true, "firekeeping");
                case WorkOrder.FellTrees:
                    return new BackgroundJobDescriptor(order, BackgroundSimulationKind.ResourceRate, DefaultOfflineEfficiency, true, "wood");
                case WorkOrder.Stonework:
                    return new BackgroundJobDescriptor(order, BackgroundSimulationKind.ResourceRate, DefaultOfflineEfficiency, true, "stone");
                case WorkOrder.Garden:
                    return new BackgroundJobDescriptor(order, BackgroundSimulationKind.Production, DefaultOfflineEfficiency, true, "garden");
                case WorkOrder.Forestry:
                    return new BackgroundJobDescriptor(order, BackgroundSimulationKind.Production, DefaultOfflineEfficiency, true, "forestry");
                case WorkOrder.GatherResources:
                    return new BackgroundJobDescriptor(order, BackgroundSimulationKind.ResourceRate, DefaultOfflineEfficiency, true, "gathering");
                case WorkOrder.Harvest:
                    return new BackgroundJobDescriptor(order, BackgroundSimulationKind.ResourceRate, DefaultOfflineEfficiency, true, "harvesting");
                case WorkOrder.Hunt:
                    return new BackgroundJobDescriptor(order, BackgroundSimulationKind.ResourceRate, DefaultOfflineEfficiency, true, "hunting");
                case WorkOrder.Bake:
                    return new BackgroundJobDescriptor(order, BackgroundSimulationKind.Production, DefaultOfflineEfficiency, true, "baking");
                case WorkOrder.Guard:
                    return new BackgroundJobDescriptor(order, BackgroundSimulationKind.Defense, 1f, true, "guard");
                case WorkOrder.Butcher:
                    // v0.5.378: stationary livestock work accrues as a deterministic daily
                    // catch-up budget and is materialised against the real pen population when
                    // the Workzone becomes physical again. No remote Character AI is simulated.
                    return new BackgroundJobDescriptor(order, BackgroundSimulationKind.Production, 1f, true, "livestock-maintenance");
                case WorkOrder.Smith:
                    // v0.5.379: Repair + Metal share one elapsed-time work budget. Repair uses a
                    // handoff backlog snapshot; metal uses snapshotted real smelter recipes and
                    // persistent container/station ledgers. Neither path installs an unloaded scan.
                    return new BackgroundJobDescriptor(order, BackgroundSimulationKind.Production, 1f, true, "smith-workbudget");
                case WorkOrder.Mine:
                    return new BackgroundJobDescriptor(order, BackgroundSimulationKind.ResourceRate, DefaultOfflineEfficiency, false, "mining");
                case WorkOrder.Fish:
                    // v0.5.380: background fishing uses only species that the physical Neck actually
                    // observed in its loaded Workzone. Elapsed time becomes staged FieldDepot catches;
                    // no remote Fish/Physics scan or Character AI is run while the zone is unloaded.
                    return new BackgroundJobDescriptor(order, BackgroundSimulationKind.ResourceRate, DefaultOfflineEfficiency, true, "fishing");
                default:
                    return new BackgroundJobDescriptor(order, BackgroundSimulationKind.None, 0f, false, "none");
            }
        }

        internal static string Describe(WorkOrder order)
        {
            BackgroundJobDescriptor d = Get(order);
            if (d.Kind == BackgroundSimulationKind.None) return "unsupported";
            return d.ImplementedNow ? $"active:{d.Name}" : $"framework-ready:{d.Name}@{d.Efficiency * 100f:0}%";
        }
    }
}
