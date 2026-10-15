using System;

namespace Valheim_Workers
{
    public enum WorkerRole
    {
        None = 0,
        Lumberjack = 1,
        Miner = 2,
        Fisher = 3,
        Gardener = 4,
        Gatherer = 5,
        Guard = 6,
        Hauler = 7,
        Stonemason = 8,
        Firekeeper = 9,
        Hunter = 10,
        Baker = 11,
        Butcher = 12,
        Smith = 13
    }

    public enum WorkerState
    {
        Idle = 0,
        FindJob = 1,
        MoveToTarget = 2,
        Work = 3,
        Collect = 4,
        Deliver = 5,
        ReturnHome = 6,
        Eat = 7,
        Sleep = 8,
        Combat = 9,
        Recover = 10,
        NeedsResource = 11,
        Stuck = 12,
        Follow = 13,
        ReturnHomeForRest = 14,
        Resting = 15,
        MoveToWorkArea = 16,
        WaitAtHome = 17,
        // v0.5.108: idle-but-on-duty position. Unlike WaitAtHome this may be a logistics
        // endpoint (Sorter) or another explicit specialist post, while the worker keeps polling
        // its enabled priorities for newly available work. Existing state values stay unchanged.
        Standby = 18
    }


    public enum WorkerStance
    {
        Aggressive = 0,
        Defensive = 1,
        Passive = 2
    }


    public enum GuardBehaviorMode
    {
        Stationary = 0,
        Deploy = 1
    }

    [Flags]
    public enum WorkerCapability
    {
        None = 0,
        ReturnHome = 1 << 0,
        FindLooseResources = 1 << 1,
        PickUpItems = 1 << 2,
        DeliverToContainer = 1 << 3,
        TransportBetweenContainers = 1 << 4,
        FindTrees = 1 << 5,
        DamageWood = 1 << 6,
        HarvestPlants = 1 << 7,
        FetchSupplies = 1 << 8,
        PlantSeeds = 1 << 9,
        FindStone = 1 << 10,
        DamageStone = 1 << 11,
        FindOre = 1 << 12,
        DamageOre = 1 << 13,
        FindWater = 1 << 14,
        CatchFish = 1 << 15,
        Patrol = 1 << 16,
        Combat = 1 << 17,
        UseWorkAnimation = 1 << 18,
        FindFireSource = 1 << 19,
        CheckFuel = 1 << 20,
        Refuel = 1 << 21,
        HuntAnimals = 1 << 22,
        UseHunterStand = 1 << 23,
        AirTransport = 1 << 24,
        OperateProduction = 1 << 25,
        ManageLivestock = 1 << 26,
        UseDoors = 1 << 27,
        RepairStructures = 1 << 28
    }

    public enum WorkerRoleImplementation
    {
        Foundation = 0,
        Targeting = 1,
        Navigation = 2,
        WorkAction = 3,
        Complete = 4
    }
}
