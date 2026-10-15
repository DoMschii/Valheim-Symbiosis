using System;
using System.Collections.Generic;

namespace Valheim_Workers
{
    internal enum WorkOrder
    {
        None = 0,
        FellTrees = 1,
        GatherResources = 2,
        Haul = 3,
        Garden = 4,
        Stonework = 5,
        Mine = 6,
        Fish = 7,
        Guard = 8,
        KeepFires = 9,
        Forestry = 10,
        Sort = 11,
        Hunt = 12,
        Harvest = 13,
        Bake = 14,
        Butcher = 15,
        Smith = 16,
        Provision = 17
    }

    internal static class WorkOrderCatalog
    {
        internal static readonly WorkOrder[] All =
        {
            WorkOrder.FellTrees,
            WorkOrder.GatherResources,
            WorkOrder.Harvest,
            WorkOrder.Haul,
            WorkOrder.Sort,
            WorkOrder.Provision,
            WorkOrder.Garden,
            WorkOrder.Forestry,
            WorkOrder.Stonework,
            WorkOrder.Fish,
            WorkOrder.Guard,
            WorkOrder.KeepFires,
            WorkOrder.Hunt,
            WorkOrder.Bake,
            WorkOrder.Butcher,
            WorkOrder.Smith
        };

        internal static string DisplayName(WorkOrder order)
        {
            switch (order)
            {
                case WorkOrder.FellTrees: return "Holz faellen";
                case WorkOrder.GatherResources: return "Sammeln";
                case WorkOrder.Harvest: return "Ernten";
                case WorkOrder.Haul: return "Transportieren";
                case WorkOrder.Sort: return "Sortieren";
                case WorkOrder.Provision: return "Versorgen";
                case WorkOrder.Garden: return "Gaertnern";
                case WorkOrder.Forestry: return "Foerstern / Aufforsten";
                case WorkOrder.Stonework: return "Stein / Erz abbauen";
                case WorkOrder.Mine: return "Bergbau (Legacy)";
                case WorkOrder.Fish: return "Fischen";
                case WorkOrder.Guard: return "Bewachen";
                case WorkOrder.KeepFires: return "Feuer hueten";
                case WorkOrder.Hunt: return "Jagen";
                case WorkOrder.Bake: return "Kochen / Produktion";
                case WorkOrder.Butcher: return "Tierhaltung / Metzger";
                case WorkOrder.Smith: return "Schmieden / Metall";
                default: return "Kein Auftrag";
            }
        }

        internal static bool IsImplemented(WorkOrder order) =>
            order == WorkOrder.FellTrees || order == WorkOrder.GatherResources || order == WorkOrder.Harvest || order == WorkOrder.Haul || order == WorkOrder.Sort || order == WorkOrder.Provision || order == WorkOrder.Garden || order == WorkOrder.Forestry || order == WorkOrder.Stonework || order == WorkOrder.Fish || order == WorkOrder.Guard || order == WorkOrder.KeepFires || order == WorkOrder.Hunt || order == WorkOrder.Bake || order == WorkOrder.Butcher || order == WorkOrder.Smith;

        internal static readonly int ImplementedMask = BuildImplementedMask();

        private static int BuildImplementedMask()
        {
            int mask = 0;
            for (int i = 0; i < All.Length; i++)
                if (IsImplemented(All[i])) mask |= Bit(All[i]);
            return mask;
        }

        internal static bool MatchesRole(WorkOrder order, WorkerRole role)
        {
            switch (order)
            {
                case WorkOrder.FellTrees: return role == WorkerRole.Lumberjack;
                case WorkOrder.GatherResources: return role == WorkerRole.Gatherer;
                case WorkOrder.Harvest: return role == WorkerRole.Gatherer || role == WorkerRole.Gardener;
                case WorkOrder.Haul: return role == WorkerRole.Hauler;
                case WorkOrder.Sort: return role == WorkerRole.Hauler;
                case WorkOrder.Provision: return role == WorkerRole.Hauler;
                case WorkOrder.Garden: return role == WorkerRole.Gardener;
                case WorkOrder.Forestry: return role == WorkerRole.Gardener;
                case WorkOrder.Stonework: return role == WorkerRole.Stonemason;
                case WorkOrder.Mine: return role == WorkerRole.Miner;
                case WorkOrder.Fish: return role == WorkerRole.Fisher;
                case WorkOrder.Guard: return role == WorkerRole.Guard || role == WorkerRole.Hunter;
                case WorkOrder.KeepFires: return role == WorkerRole.Firekeeper;
                case WorkOrder.Hunt: return role == WorkerRole.Hunter;
                case WorkOrder.Bake: return role == WorkerRole.Baker;
                case WorkOrder.Butcher: return role == WorkerRole.Butcher;
                case WorkOrder.Smith: return role == WorkerRole.Smith;
                default: return false;
            }
        }

        internal static int Bit(WorkOrder order)
        {
            int value = (int)order;
            return value <= 0 || value >= 31 ? 0 : 1 << value;
        }

        internal static bool Contains(int mask, WorkOrder order)
        {
            int bit = Bit(order);
            return bit != 0 && (mask & bit) != 0;
        }

        internal static int SetEnabled(int mask, WorkOrder order, bool enabled)
        {
            int bit = Bit(order);
            if (bit == 0) return mask;
            return enabled ? mask | bit : mask & ~bit;
        }

        internal static bool TryResolveForRole(int mask, WorkerRole role, out WorkOrder order)
        {
            // One creature role may serve multiple explicit jobs (currently the Shaman/Gardener role
            // serves Garden and Forestry). The work orders stay separate so gardening can use
            // SmartReplant rules without changing the already-working forestry runtime.
            WorkOrder fallback = WorkOrder.None;
            for (int i = 0; i < All.Length; i++)
            {
                WorkOrder candidate = All[i];
                if (!Contains(mask, candidate) || !MatchesRole(candidate, role)) continue;
                if (fallback == WorkOrder.None) fallback = candidate;
                if (IsImplemented(candidate))
                {
                    order = candidate;
                    return true;
                }
            }

            order = fallback;
            return order != WorkOrder.None;
        }

        internal static WorkOrder FirstEnabled(int mask)
        {
            for (int i = 0; i < All.Length; i++)
                if (Contains(mask, All[i])) return All[i];
            return WorkOrder.None;
        }

        internal static string DescribeMask(int mask)
        {
            List<string> names = new List<string>();
            for (int i = 0; i < All.Length; i++)
            {
                WorkOrder order = All[i];
                if (Contains(mask, order)) names.Add(DisplayName(order));
            }
            return names.Count == 0 ? "keine" : string.Join(", ", names);
        }
    }
}
