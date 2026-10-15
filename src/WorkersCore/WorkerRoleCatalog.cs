using System;
using System.Collections.Generic;

namespace Valheim_Workers
{
    internal sealed class WorkerRoleDefinition
    {
        internal WorkerRole Role { get; }
        internal string DisplayName { get; }
        internal string Purpose { get; }
        internal WorkerCapability Capabilities { get; }
        internal WorkerRoleImplementation Implementation { get; }

        internal WorkerRoleDefinition(
            WorkerRole role,
            string displayName,
            string purpose,
            WorkerCapability capabilities,
            WorkerRoleImplementation implementation)
        {
            Role = role;
            DisplayName = displayName;
            Purpose = purpose;
            Capabilities = capabilities;
            Implementation = implementation;
        }
    }

    internal static class WorkerRoleCatalog
    {
        private static readonly Dictionary<WorkerRole, WorkerRoleDefinition> Definitions =
            new Dictionary<WorkerRole, WorkerRoleDefinition>
            {
                [WorkerRole.Gatherer] = new WorkerRoleDefinition(
                    WorkerRole.Gatherer,
                    "Gatherer / Harvester",
                    "Greyling field role with two explicit jobs: Sammeln handles loose real ItemDrops; Ernten harvests mature plants. Both obey Workzone/task filters and use the persistent cargo/depot pipeline.",
                    WorkerCapability.ReturnHome |
                    WorkerCapability.FindLooseResources |
                    WorkerCapability.HarvestPlants |
                    WorkerCapability.PickUpItems |
                    WorkerCapability.DeliverToContainer,
                    WorkerRoleImplementation.Complete),

                [WorkerRole.Hauler] = new WorkerRoleDefinition(
                    WorkerRole.Hauler,
                    "Hauler",
                    "Physical logistics worker. Base Transporters may serve several Outposts; an Outpost Transporter stays bound to its own Outpost. Sort and Provision remain local logistics jobs.",
                    WorkerCapability.ReturnHome |
                    WorkerCapability.PickUpItems |
                    WorkerCapability.DeliverToContainer |
                    WorkerCapability.TransportBetweenContainers,
                    WorkerRoleImplementation.Complete),

                [WorkerRole.Lumberjack] = new WorkerRoleDefinition(
                    WorkerRole.Lumberjack,
                    "Lumberjack",
                    "Finds real trees/logs/stumps, respects species/tool-tier and per-worker wood filters, uses native attacks and applies real Valheim wood damage/drops.",
                    WorkerCapability.ReturnHome |
                    WorkerCapability.FindTrees |
                    WorkerCapability.UseWorkAnimation |
                    WorkerCapability.DamageWood |
                    WorkerCapability.PickUpItems |
                    WorkerCapability.DeliverToContainer,
                    WorkerRoleImplementation.Complete),

                [WorkerRole.Gardener] = new WorkerRoleDefinition(
                    WorkerRole.Gardener,
                    "Gardener / Forester",
                    "Runs persistent player-planned crop/WildGrowth and forestry slots, harvests mature plants, uses real seed/resource stock and keeps loaded/background work on the same plan state.",
                    WorkerCapability.ReturnHome |
                    WorkerCapability.HarvestPlants |
                    WorkerCapability.FetchSupplies |
                    WorkerCapability.PlantSeeds |
                    WorkerCapability.PickUpItems |
                    WorkerCapability.DeliverToContainer,
                    WorkerRoleImplementation.Complete),

                [WorkerRole.Stonemason] = new WorkerRoleDefinition(
                    WorkerRole.Stonemason,
                    "Stonemason",
                    "Heavy quarry worker for stone AND ore deposits. Uses the real Valheim MineRock/MineRock5/Destructible nodes, real drops, tier gates and the per-worker resource-category filter.",
                    WorkerCapability.ReturnHome |
                    WorkerCapability.FindStone |
                    WorkerCapability.FindOre |
                    WorkerCapability.UseWorkAnimation |
                    WorkerCapability.DamageStone |
                    WorkerCapability.DamageOre |
                    WorkerCapability.PickUpItems |
                    WorkerCapability.DeliverToContainer,
                    WorkerRoleImplementation.Complete),

                [WorkerRole.Miner] = new WorkerRoleDefinition(
                    WorkerRole.Miner,
                    "Miner (Legacy)",
                    "Compatibility-only legacy role. New ore work is part of Stonemason/Stonework and no new Worker is assigned this role.",
                    WorkerCapability.ReturnHome |
                    WorkerCapability.FindOre |
                    WorkerCapability.UseWorkAnimation |
                    WorkerCapability.DamageOre |
                    WorkerCapability.PickUpItems |
                    WorkerCapability.DeliverToContainer,
                    WorkerRoleImplementation.Foundation),

                [WorkerRole.Fisher] = new WorkerRoleDefinition(
                    WorkerRole.Fisher,
                    "Fisher",
                    "Neck water worker. Loaded work catches only real Fish objects; background work is limited to fish species actually observed in that Workzone and stages catches into its FieldDepot.",
                    WorkerCapability.ReturnHome |
                    WorkerCapability.FindWater |
                    WorkerCapability.CatchFish |
                    WorkerCapability.PickUpItems |
                    WorkerCapability.DeliverToContainer,
                    WorkerRoleImplementation.Complete),


                [WorkerRole.Firekeeper] = new WorkerRoleDefinition(
                    WorkerRole.Firekeeper,
                    "Firekeeper",
                    "Pure fire specialist. Can bind to one fire as an endless heat source or patrol a workzone and magically tend fireplaces, stone ovens and metal furnaces without consuming wood/coal.",
                    WorkerCapability.ReturnHome |
                    WorkerCapability.FindFireSource |
                    WorkerCapability.CheckFuel |
                    WorkerCapability.UseWorkAnimation |
                    WorkerCapability.Refuel,
                    WorkerRoleImplementation.Complete),

                [WorkerRole.Hunter] = new WorkerRoleDefinition(
                    WorkerRole.Hunter,
                    "Hunter",
                    "Ranged Draugr specialist. Hunt uses allowed prey filters, a valid hunting stand, line-of-sight repositioning and real drops; Guard is the lower-priority fallback when no hunt is available.",
                    WorkerCapability.ReturnHome |
                    WorkerCapability.Combat |
                    WorkerCapability.HuntAnimals |
                    WorkerCapability.UseHunterStand |
                    WorkerCapability.PickUpItems |
                    WorkerCapability.DeliverToContainer,
                    WorkerRoleImplementation.Complete),

                [WorkerRole.Baker] = new WorkerRoleDefinition(
                    WorkerRole.Baker,
                    "Cook / Production",
                    "Cooking/production specialist. The current natural production worker is the Fuling Shaman (T3): cauldron, preparation table, windmill and stone oven. Executes persistent station-owned stock orders and never exceeds the configured target.",
                    WorkerCapability.ReturnHome |
                    WorkerCapability.FetchSupplies |
                    WorkerCapability.DeliverToContainer |
                    WorkerCapability.UseWorkAnimation |
                    WorkerCapability.OperateProduction,
                    WorkerRoleImplementation.Complete),


                [WorkerRole.Butcher] = new WorkerRoleDefinition(
                    WorkerRole.Butcher,
                    "Butcher / Livestock Keeper",
                    "Draugr Elite livestock specialist. Maintains per-species Min/Max population rules, never slaughters juveniles, removes the lowest-star adult surplus first and may open/close doors on its work route.",
                    WorkerCapability.ReturnHome |
                    WorkerCapability.FetchSupplies |
                    WorkerCapability.DeliverToContainer |
                    WorkerCapability.Combat |
                    WorkerCapability.UseWorkAnimation |
                    WorkerCapability.ManageLivestock |
                    WorkerCapability.UseDoors,
                    WorkerRoleImplementation.Complete),

                [WorkerRole.Smith] = new WorkerRoleDefinition(
                    WorkerRole.Smith,
                    "Smith / Metalworker",
                    "Fuling craftsman. Services smelters and blast furnaces with real ore/scrap (including Black Metal) and can repair damaged loaded base pieces inside its Workzone at the structure itself.",
                    WorkerCapability.ReturnHome |
                    WorkerCapability.FetchSupplies |
                    WorkerCapability.PickUpItems |
                    WorkerCapability.DeliverToContainer |
                    WorkerCapability.UseWorkAnimation |
                    WorkerCapability.OperateProduction |
                    WorkerCapability.UseDoors |
                    WorkerCapability.RepairStructures,
                    WorkerRoleImplementation.Complete),

                [WorkerRole.Guard] = new WorkerRoleDefinition(
                    WorkerRole.Guard,
                    "Guard",
                    "Defends the assigned Workzone. Stationary mode holds the guard post; Deploy mode patrols deterministic sectors, breaks patrol for hostile combat and resumes afterward.",
                    WorkerCapability.ReturnHome |
                    WorkerCapability.Patrol |
                    WorkerCapability.Combat,
                    WorkerRoleImplementation.Complete)
            };

        internal static WorkerRoleDefinition Get(WorkerRole role)
        {
            Definitions.TryGetValue(role, out WorkerRoleDefinition definition);
            return definition;
        }

        internal static string DescribeCapabilities(WorkerRole role)
        {
            WorkerRoleDefinition definition = Get(role);
            if (definition == null || definition.Capabilities == WorkerCapability.None) return "None";

            List<string> names = new List<string>();
            foreach (WorkerCapability capability in Enum.GetValues(typeof(WorkerCapability)))
            {
                if (capability == WorkerCapability.None) continue;
                if ((definition.Capabilities & capability) == capability)
                    names.Add(capability.ToString());
            }
            return string.Join(", ", names);
        }
    }
}
