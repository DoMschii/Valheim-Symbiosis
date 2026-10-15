using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Shared base-storage bridge for Workers. It deliberately talks only to Valheim's real
    /// Container/Inventory objects, so container mods such as SmartStorage can keep patching the
    /// same inventory methods without a hard compile dependency from Workers.
    ///
    /// Searches are local around the Worker's HomeAnchor and throttled by WorkerAiController.
    /// There is no global FindObjectsOfType scan.
    /// </summary>
    internal static class WorkerStorageRuntime
    {
        internal const string DepositTargetKind = "storage_deposit";
        internal const string OverflowDepositTargetKind = "storage_overflow_deposit";
        internal const string SeedSourceTargetKind = "storage_seed_source";
        internal const string GardenResourceSourceTargetKind = "storage_garden_resource_source";
        internal const string FieldDepotDepositTargetKind = "storage_field_depot_deposit";
        internal const string HaulSourceTargetKind = "storage_haul_source";
        internal const string ImportDepositTargetKind = "storage_import_deposit";
        internal const string ExportHaulSourceTargetKind = "storage_export_haul_source";
        internal const string SupplyFieldDepotDepositTargetKind = "storage_supply_field_depot_deposit";
        internal const string SortImportSourceTargetKind = "storage_sort_import_source";
        internal const string SortBaseSourceTargetKind = "storage_sort_base_source";
        internal const string SortBaseDepositTargetKind = "storage_sort_base_deposit";
        internal const string ExportDepositTargetKind = "storage_export_deposit";
        internal static float ContainerAccessRadius { get; private set; } = 3.0f;
        internal static float LogisticsNetworkRadius { get; private set; } = 250f;
        internal static float HaulerMinimumLoadPercent { get; private set; } = 75f;
        // Navigation aims at a real ground point well inside the interaction cylinder. The stop
        // radius is therefore only about reaching that walkable point; it is not a chest distance.
        internal const float ContainerApproachStopDistance = 0.5f;
        internal static float ContainerStopDistance => ContainerAccessRadius;
        internal const float MinStorageSearchRadius = 30f;
        internal const float MaxStorageSearchRadius = 80f;
        internal const float MinWorkAreaStorageSearchRadius = 60f;
        internal const float MaxWorkAreaStorageSearchRadius = 150f;

        private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Collider[] Nearby = new Collider[256];
        private static readonly RaycastHit[] GroundHits = new RaycastHit[16];
        private static readonly float[] GroundAngleOffsets = { 0f, 15f, -15f, 30f, -30f, 45f, -45f, 60f, -60f, 75f, -75f, 90f, -90f, 105f, -105f, 120f, -120f, 135f, -135f, 150f, -150f, 165f, -165f, 180f };
        private static readonly Collider[] ApproachClearanceHits = new Collider[32];
        private static readonly HashSet<Container> Seen = new HashSet<Container>();
        private static readonly Dictionary<int, string> DebugStatus = new Dictionary<int, string>();
        private sealed class CookingStockSnapshotCacheEntry
        {
            internal float ExpiresAt;
            internal Dictionary<string, int> Totals;
        }
        private static readonly Dictionary<int, CookingStockSnapshotCacheEntry> CookingStockSnapshots = new Dictionary<int, CookingStockSnapshotCacheEntry>();
        private const float CookingStockSnapshotCacheSeconds = 5.0f;
        private sealed class ApproachCacheEntry
        {
            internal Vector3 ContainerPosition;
            internal Vector3 GroundPoint;
            internal float Radius;
        }
        private static readonly Dictionary<int, ApproachCacheEntry> ApproachCache = new Dictionary<int, ApproachCacheEntry>();
        private static FieldInfo _sceneInstancesField;
        private static bool _sceneInstancesFieldResolved;
        internal static string LastSceneStorageStatus { get; private set; } = "idle";
        internal static int LastPhysicsStorageHits { get; private set; }
        internal static int LastSceneStorageViews { get; private set; }
        internal static int LastSceneStorageContainers { get; private set; }
        internal static int LastHaulContainersScanned { get; private set; }
        internal static int LastHaulFieldDepots { get; private set; }
        internal static int LastHaulOwnedDepots { get; private set; }
        internal static int LastHaulItemStacks { get; private set; }
        internal static int LastHaulMovableUnits { get; private set; }
        internal static string LastHaulScanMode { get; private set; } = "idle";
        internal static int LastHaulMinimumLoad { get; private set; }
        internal static int LastHaulLargestMovableStack { get; private set; }

        private static readonly MethodInfo ClaimOwnershipMethod = typeof(ZNetView).GetMethod(
            "ClaimOwnership", InstanceFlags, null, Type.EmptyTypes, null);
        private static readonly MethodInfo ContainerSaveMethod = typeof(Container).GetMethod(
            "Save", InstanceFlags, null, Type.EmptyTypes, null);
        private static readonly MethodInfo InventoryAddItemMethod = typeof(Inventory).GetMethod(
            "AddItem", InstanceFlags, null, new[] { typeof(ItemDrop.ItemData) }, null);
        private static readonly MethodInfo InventoryRemoveItemDataMethod = typeof(Inventory).GetMethod(
            "RemoveItem", InstanceFlags, null, new[] { typeof(ItemDrop.ItemData), typeof(int) }, null);
        private static readonly MethodInfo InventoryRemoveItemNameMethod = typeof(Inventory).GetMethod(
            "RemoveItem", InstanceFlags, null, new[] { typeof(string), typeof(int), typeof(int), typeof(bool) }, null);
        private static readonly MethodInfo InventoryChangedTwoArgsMethod = typeof(Inventory).GetMethod(
            "Changed", InstanceFlags, null, new[] { typeof(bool), typeof(bool) }, null);
        private static readonly MethodInfo InventoryChangedNoArgsMethod = typeof(Inventory).GetMethod(
            "Changed", InstanceFlags, null, Type.EmptyTypes, null);

        private static Action<string> _debugLog;
        private static bool _reportedApiWarning;

        internal static void Initialize(Action<string> debugLog)
        {
            _debugLog = debugLog;
            _reportedApiWarning = false;
            ValidateApiOnce();
        }

        internal static void ConfigureContainerAccessRadius(float radius)
        {
            ContainerAccessRadius = Mathf.Clamp(radius, 1.5f, 6f);
        }

        internal static void ConfigureLogisticsNetworkRadius(float radius)
        {
            LogisticsNetworkRadius = Mathf.Clamp(radius, 80f, 500f);
        }

        internal static void ConfigureHaulerMinimumLoadPercent(float percent)
        {
            HaulerMinimumLoadPercent = Mathf.Clamp(percent, 0f, 100f);
        }

        internal static void InvalidateCookingStockSnapshots()
        {
            CookingStockSnapshots.Clear();
        }

        internal static void Shutdown()
        {
            WorkerInventoryMutationGuard.Clear();
            DebugStatus.Clear();
            CookingStockSnapshots.Clear();
            Seen.Clear();
            ApproachCache.Clear();
            _debugLog = null;
            _reportedApiWarning = false;
            LastSceneStorageStatus = "idle";
            LastPhysicsStorageHits = 0;
            LastSceneStorageViews = 0;
            LastSceneStorageContainers = 0;
            LastHaulContainersScanned = 0;
            LastHaulFieldDepots = 0;
            LastHaulOwnedDepots = 0;
            LastHaulItemStacks = 0;
            LastHaulMovableUnits = 0;
            LastHaulScanMode = "idle";
            LastHaulMinimumLoad = 0;
            LastHaulLargestMovableStack = 0;
        }

        internal static string GetHaulDebugSummary()
        {
            return $"mode={LastHaulScanMode}; containers={LastHaulContainersScanned}; fieldDepots={LastHaulFieldDepots}; owned={LastHaulOwnedDepots}; stacks={LastHaulItemStacks}; movableUnits={LastHaulMovableUnits}; batchMin={LastHaulMinimumLoad}; largest={LastHaulLargestMovableStack}; transportReach=MAP-WIDE; localStorageRadius={LogisticsNetworkRadius:0}m";
        }

        internal static string GetDebugStatus(Character worker)
        {
            if (worker == null) return "storage: worker unavailable";
            return DebugStatus.TryGetValue(worker.GetInstanceID(), out string value)
                ? value
                : "storage: idle";
        }

        internal static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        internal static bool IsInsideContainerAura(Vector3 workerPosition, Vector3 containerPosition)
        {
            return HorizontalDistance(workerPosition, containerPosition) <= ContainerAccessRadius;
        }

        /// <summary>
        /// Chooses a ground-projected navigation point inside the container's vertical interaction
        /// cylinder. The container may be on a shelf or upper wall: only X/Z distance controls
        /// interaction. Several points are tested around the cylinder and the closest usable ground
        /// point to the Worker wins.
        /// </summary>
        internal static Vector3 GetContainerApproachPoint(Character worker, Vector3 containerPosition)
        {
            Vector3 workerPosition = worker != null ? worker.transform.position : containerPosition;
            if (IsInsideContainerAura(workerPosition, containerPosition))
            {
                if (worker != null) ApproachCache.Remove(worker.GetInstanceID());
                return workerPosition;
            }

            int cacheKey = worker != null ? worker.GetInstanceID() : 0;
            if (cacheKey != 0 && ApproachCache.TryGetValue(cacheKey, out ApproachCacheEntry cached) &&
                HorizontalDistance(cached.ContainerPosition, containerPosition) <= 0.25f &&
                Mathf.Abs(cached.Radius - ContainerAccessRadius) <= 0.01f)
                return cached.GroundPoint;

            Vector3 away = new Vector3(workerPosition.x - containerPosition.x, 0f, workerPosition.z - containerPosition.z);
            if (away.sqrMagnitude < 0.0001f) away = Vector3.forward;
            away.Normalize();

            // v0.2.12: A storage target in a wall/corner used to create a perfectly valid ground
            // sample *inside* a wall/pillar. Valheim's native path then did its job and led the
            // Worker right up to that impossible final point, where our direct steering kept
            // pressing into the corner. Keep storage navigation out of the navigator itself:
            // sample several points inside the interaction cylinder, reject points without actual
            // body clearance, and strongly prefer a point with a direct body-clear line from the
            // Worker's current position. This preserves the stable v0.2.10 navigation stack.
            float baseRingRadius = Mathf.Max(0.45f, ContainerAccessRadius * 0.50f);
            float maxRingRadius = Mathf.Max(
                baseRingRadius,
                ContainerAccessRadius - ContainerApproachStopDistance - 0.20f);
            float middleRingRadius = Mathf.Min(maxRingRadius, baseRingRadius + 0.35f);

            float[] ringRadii = Mathf.Abs(maxRingRadius - middleRingRadius) < 0.05f
                ? new[] { baseRingRadius, maxRingRadius }
                : new[] { baseRingRadius, middleRingRadius, maxRingRadius };

            Vector3 best = new Vector3(containerPosition.x, workerPosition.y, containerPosition.z) + away * baseRingRadius;
            float bestScore = float.MaxValue;
            bool found = false;

            for (int r = 0; r < ringRadii.Length; r++)
            {
                float ringRadius = ringRadii[r];
                for (int i = 0; i < GroundAngleOffsets.Length; i++)
                {
                    Vector3 direction = Quaternion.Euler(0f, GroundAngleOffsets[i], 0f) * away;
                    Vector3 candidateXZ =
                        new Vector3(containerPosition.x, workerPosition.y, containerPosition.z) +
                        direction * ringRadius;
                    if (!TryProjectWalkableGround(candidateXZ, workerPosition.y, out Vector3 ground)) continue;
                    if (!HasApproachBodyClearance(ground)) continue;

                    float horizontalToWorker = HorizontalDistance(workerPosition, ground);
                    float verticalDelta = Mathf.Abs(ground.y - workerPosition.y);
                    bool directClear = WorkerGroundRoutePlanner.IsDirectPathClear(workerPosition, ground);

                    // A direct-clear candidate is much safer around shelves, wall corners and
                    // pillars. If none exists, a body-clear candidate is still valid and Valheim's
                    // native route can take the Worker around the obstacle.
                    float score =
                        horizontalToWorker +
                        verticalDelta * 1.5f +
                        Mathf.Abs(GroundAngleOffsets[i]) * 0.002f +
                        (directClear ? 0f : 12f) +
                        r * 0.01f;
                    if (score >= bestScore) continue;

                    bestScore = score;
                    best = ground;
                    found = true;
                }
            }

            if (!found)
            {
                // Extremely cramped builds can legitimately have no body-clear point inside the
                // interaction cylinder. Preserve the old fail-soft behaviour rather than deleting
                // the work target; the debug/route layer can still retry if the build changes.
                best =
                    new Vector3(containerPosition.x, workerPosition.y, containerPosition.z) +
                    away * baseRingRadius;
            }

            if (cacheKey != 0)
            {
                ApproachCache[cacheKey] = new ApproachCacheEntry
                {
                    ContainerPosition = containerPosition,
                    GroundPoint = best,
                    Radius = ContainerAccessRadius
                };
            }

            return best;
        }

        internal static void ClearContainerApproach(Character worker)
        {
            if (worker != null) ApproachCache.Remove(worker.GetInstanceID());
        }

        private static bool TryProjectWalkableGround(Vector3 candidateXZ, float workerY, out Vector3 ground)
        {
            ground = default;
            Vector3 origin = new Vector3(candidateXZ.x, workerY + 1.25f, candidateXZ.z);
            int hits = Physics.RaycastNonAlloc(origin, Vector3.down, GroundHits, 4.5f, ~0, QueryTriggerInteraction.Ignore);
            float bestDistance = float.MaxValue;
            bool found = false;

            for (int i = 0; i < hits; i++)
            {
                RaycastHit hit = GroundHits[i];
                GroundHits[i] = default;
                if (hit.collider == null || hit.normal.y < 0.45f) continue;
                if (hit.collider.GetComponentInParent<Character>() != null) continue;
                if (hit.collider.GetComponentInParent<Container>() != null) continue;
                if (Mathf.Abs(hit.point.y - workerY) > 2.25f) continue;
                if (hit.distance >= bestDistance) continue;

                bestDistance = hit.distance;
                ground = hit.point + Vector3.up * 0.05f;
                found = true;
            }

            for (int i = hits; i < GroundHits.Length; i++) GroundHits[i] = default;
            return found;
        }

        private static bool HasApproachBodyClearance(Vector3 ground)
        {
            Vector3 bottom = ground + Vector3.up * 0.46f;
            Vector3 top = ground + Vector3.up * 1.38f;
            const float radius = 0.36f;

            int count = Physics.OverlapCapsuleNonAlloc(
                bottom,
                top,
                radius,
                ApproachClearanceHits,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
            {
                Collider collider = ApproachClearanceHits[i];
                ApproachClearanceHits[i] = null;
                if (collider == null) continue;
                if (collider.GetComponentInParent<Character>() != null) continue;
                if (collider.GetComponentInParent<ItemDrop>() != null) continue;
                if (collider.GetComponentInParent<Pickable>() != null) continue;
                if (collider.GetComponentInParent<Container>() != null) continue;
                return false;
            }

            for (int i = count; i < ApproachClearanceHits.Length; i++)
                ApproachClearanceHits[i] = null;
            return true;
        }

        internal static float GetSearchRadius(ZDO zdo)
        {
            float homeRadius = zdo != null ? zdo.GetFloat(WorkerZdo.WorkRadius, 40f) : 40f;
            return Mathf.Clamp(Mathf.Max(MinStorageSearchRadius, homeRadius), MinStorageSearchRadius, MaxStorageSearchRadius);
        }

        internal static float GetWorkAreaStorageSearchRadius(ZDO zdo)
        {
            if (WorkerZdo.TryReadWorkArea(zdo, out _, out _, out float workAreaRadius))
                return Mathf.Clamp(Mathf.Max(MinWorkAreaStorageSearchRadius, workAreaRadius + 20f), MinWorkAreaStorageSearchRadius, MaxWorkAreaStorageSearchRadius);
            return MinWorkAreaStorageSearchRadius;
        }

        internal static bool TryFindMatchingDepositContainer(Character worker, ZDO workerZdo, string cargoPrefab, out Container container, out Vector3 position)
        {
            container = null;
            position = default;
            if (worker == null || workerZdo == null || string.IsNullOrEmpty(cargoPrefab)) return false;

            Vector3 home = WorkerZdo.ReadHome(workerZdo, worker.transform.position);
            float radius = GetSearchRadius(workerZdo);
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);

            float bestSqr = float.MaxValue;
            Container bestContainer = null;
            Vector3 bestPosition = default;
            ForEachContainerNear(home, radius, candidate =>
            {
                if (!CanUseContainer(candidate, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(candidate) != WorkerContainerRole.Normal) return;
                Inventory inventory = candidate.GetInventory();
                if (inventory == null) return;
                if (!InventoryContainsPrefab(inventory, cargoPrefab)) return;
                if (!HasLikelyRoomFor(inventory, cargoPrefab)) return;

                float sqr = (candidate.transform.position - worker.transform.position).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                bestContainer = candidate;
                bestPosition = candidate.transform.position;
            });

            container = bestContainer;
            position = bestPosition;
            if (container != null)
            {
                SetDebug(worker, $"storage route: deposit {cargoPrefab} -> matching chest {NormalizeObjectName(container.gameObject.name)} ({Mathf.Sqrt(bestSqr):0.0} m)");
                return true;
            }

            SetDebug(worker, $"storage: no loaded base chest already containing {cargoPrefab}; flag depot fallback");
            return false;
        }

        internal static bool TryFindAnyNormalDepositContainer(Character worker, ZDO workerZdo, string cargoPrefab, out Container container, out Vector3 position)
        {
            container = null;
            position = default;
            if (worker == null || workerZdo == null || string.IsNullOrEmpty(cargoPrefab)) return false;

            Vector3 searchCenter = WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 workCenter, out _)
                ? workCenter
                : WorkerZdo.ReadHome(workerZdo, worker.transform.position);
            float radius = GetWorkAreaStorageSearchRadius(workerZdo);
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);

            float bestSqr = float.MaxValue;
            Container bestContainer = null;
            Vector3 bestPosition = default;
            ForEachContainerNear(searchCenter, radius, candidate =>
            {
                if (!CanUseContainer(candidate, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(candidate) != WorkerContainerRole.Normal) return;
                Inventory inventory = candidate.GetInventory();
                if (inventory == null || !HasLikelyRoomFor(inventory, cargoPrefab)) return;

                float sqr = (candidate.transform.position - worker.transform.position).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                bestContainer = candidate;
                bestPosition = candidate.transform.position;
            });

            container = bestContainer;
            position = bestPosition;
            if (container == null)
            {
                SetDebug(worker, $"storage: no free normal chest for {cargoPrefab}; cargo retained");
                return false;
            }

            SetDebug(worker, $"storage route: overflow {cargoPrefab} -> normal chest {NormalizeObjectName(container.gameObject.name)} ({Mathf.Sqrt(bestSqr):0.0} m)");
            return true;
        }

        internal static bool TryFindForestrySeedContainer(Character worker, ZDO workerZdo, out Container container, out string seedPrefab, out Vector3 position)
        {
            container = null;
            seedPrefab = string.Empty;
            position = default;
            if (worker == null || workerZdo == null) return false;

            Vector3 home = WorkerZdo.ReadHome(workerZdo, worker.transform.position);
            float homeRadius = GetSearchRadius(workerZdo);
            bool hasWorkArea = WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 workCenter, out _);
            float workRadius = GetWorkAreaStorageSearchRadius(workerZdo);
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);

            float bestSqr = float.MaxValue;
            Container bestContainer = null;
            string bestSeedPrefab = string.Empty;
            Vector3 bestPosition = default;

            Action<Container> consider = candidate =>
            {
                if (!CanUseContainer(candidate, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(candidate, ownerId)) return;
                WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(candidate);
                if (role == WorkerContainerRole.Import || role == WorkerContainerRole.Export) return;
                Inventory inventory = candidate.GetInventory();
                if (inventory == null) return;

                ItemDrop.ItemData seed = FindSupportedSeed(inventory, out string prefabName);
                if (seed == null || string.IsNullOrEmpty(prefabName)) return;

                float sqr = (candidate.transform.position - worker.transform.position).sqrMagnitude;
                float score = sqr + (role == WorkerContainerRole.FieldDepot ? 0f : 100000f);
                if (score >= bestSqr) return;
                bestSqr = score;
                bestContainer = candidate;
                bestSeedPrefab = prefabName;
                bestPosition = candidate.transform.position;
            };

            ForEachContainerNear(home, homeRadius, consider);
            if (hasWorkArea) ForEachContainerNear(workCenter, workRadius, consider);

            container = bestContainer;
            seedPrefab = bestSeedPrefab;
            position = bestPosition;
            if (container != null)
            {
                SetDebug(worker, $"storage route: fetch {seedPrefab} from {WorkerContainerRoleRuntime.DisplayName(WorkerContainerRoleRuntime.GetRole(container))} {NormalizeObjectName(container.gameObject.name)} ({Vector3.Distance(container.transform.position, worker.transform.position):0.0} m); searched Home {homeRadius:0} m + WorkArea {workRadius:0} m");
                return true;
            }

            SetDebug(worker, hasWorkArea
                ? $"storage: no supported tree seed found; searched Home {homeRadius:0} m + WorkArea {workRadius:0} m"
                : $"storage: no supported tree seed found in loaded chests within {homeRadius:0} m of HomeAnchor");
            return false;
        }

        /// <summary>
        /// Builds one aggregated storage snapshot for cooking/production availability checks.
        /// Unlike CountGardenResourceInStorage this scans the relevant containers only once, then
        /// all recipe/output/ingredient checks become dictionary lookups. This is important for the
        /// preparation table where dozens of recipes may be configured at the same time.
        /// </summary>
        internal static Dictionary<string, int> BuildGardenResourceStockSnapshot(Character worker, ZDO workerZdo)
        {
            if (worker == null || workerZdo == null)
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int workerKey = worker.GetInstanceID();
            float now = Time.realtimeSinceStartup;
            CookingStockSnapshotCacheEntry cached = null;
            if (CookingStockSnapshots.TryGetValue(workerKey, out cached) &&
                cached != null && cached.Totals != null && now < cached.ExpiresAt)
                return cached.Totals;

            Dictionary<string, int> totals = cached?.Totals ??
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            Vector3 home = WorkerZdo.ReadHome(workerZdo, worker.transform.position);
            float homeRadius = GetSearchRadius(workerZdo);
            bool hasWorkArea = WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 workCenter, out _);
            float workRadius = GetWorkAreaStorageSearchRadius(workerZdo);
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);

            // v0.5.354: the shared storage index already receives Container.Awake/Save events.
            // Aggregate those cached snapshots instead of opening every nearby Inventory and
            // allocating GetAllItems lists on every cooking/production availability probe.
            SharedStorageStockIndexRuntime.BuildCachedStockSnapshot(
                home, homeRadius, hasWorkArea, workCenter, workRadius,
                candidate =>
                {
                    if (candidate == null || !CanUseContainer(candidate, ownerId) ||
                        !WorkerContainerRoleRuntime.MatchesOwner(candidate, ownerId)) return false;
                    WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(candidate);
                    return role != WorkerContainerRole.Import && role != WorkerContainerRole.Export;
                },
                totals);

            CookingStockSnapshots[workerKey] = new CookingStockSnapshotCacheEntry
            {
                ExpiresAt = now + CookingStockSnapshotCacheSeconds,
                Totals = totals
            };
            return totals;
        }

        internal static int CountStockSnapshot(Dictionary<string, int> snapshot, string prefabName)
        {
            if (snapshot == null || string.IsNullOrEmpty(prefabName)) return 0;
            return snapshot.TryGetValue(prefabName, out int value) ? Mathf.Max(0, value) : 0;
        }

        internal static int CountGardenResourceInStorage(Character worker, ZDO workerZdo, string prefabName)
        {
            if (worker == null || workerZdo == null || string.IsNullOrEmpty(prefabName)) return 0;
            Vector3 home = WorkerZdo.ReadHome(workerZdo, worker.transform.position);
            float homeRadius = GetSearchRadius(workerZdo);
            bool hasWorkArea = WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 workCenter, out _);
            float workRadius = GetWorkAreaStorageSearchRadius(workerZdo);
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            int total = 0;
            var counted = new HashSet<Container>();
            Action<Container> count = c =>
            {
                if (c == null || !counted.Add(c) || !CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return;
                WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(c);
                if (role == WorkerContainerRole.Import || role == WorkerContainerRole.Export) return;
                total += CountPrefab(c.GetInventory(), prefabName);
            };
            ForEachContainerNear(home, homeRadius, count);
            if (hasWorkArea) ForEachContainerNear(workCenter, workRadius, count);
            return total;
        }


        internal static bool TryConsumeBackgroundResource(Character worker, ZDO workerZdo, string prefabName, int amount)
        {
            if (worker == null || workerZdo == null || string.IsNullOrEmpty(prefabName) || amount <= 0) return false;

            Vector3 home = WorkerZdo.ReadHome(workerZdo, worker.transform.position);
            float homeRadius = GetSearchRadius(workerZdo);
            bool hasWorkArea = WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 workCenter, out _);
            float workRadius = GetWorkAreaStorageSearchRadius(workerZdo);
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);

            Container best = null;
            float bestSqr = float.MaxValue;
            var counted = new HashSet<Container>();
            Action<Container> consider = candidate =>
            {
                if (candidate == null || !counted.Add(candidate) ||
                    !CanUseContainer(candidate, ownerId) ||
                    !WorkerContainerRoleRuntime.MatchesOwner(candidate, ownerId)) return;
                WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(candidate);
                if (role == WorkerContainerRole.Import || role == WorkerContainerRole.Export) return;
                Inventory inv = candidate.GetInventory();
                int protect = WorkerContainerRoleRuntime.ProtectLastItem(candidate) ? 1 : 0;
                if (inv == null || CountPrefab(inv, prefabName) < amount + protect) return;
                float sqr = (candidate.transform.position - worker.transform.position).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                best = candidate;
            };

            ForEachContainerNear(home, homeRadius, consider);
            if (hasWorkArea) ForEachContainerNear(workCenter, workRadius, consider);
            if (best == null || !TryOwn(best)) return false;

            Inventory inventory = best.GetInventory();
            if (inventory == null) return false;
            WorkerContainerRole bestRole = WorkerContainerRoleRuntime.GetRole(best);
            int protectedCount = WorkerContainerRoleRuntime.ProtectLastItem(best) ? 1 : 0;
            if (CountPrefab(inventory, prefabName) < amount + protectedCount) return false;

            int removed = 0;
            while (removed < amount)
            {
                ItemDrop.ItemData item = FindPrefab(inventory, prefabName);
                if (item == null || !TryRemoveOne(inventory, item)) break;
                removed++;
            }

            if (removed > 0) PersistContainer(best, inventory);
            return removed == amount;
        }

        internal static bool TryFindGardenResourceContainer(Character worker, ZDO workerZdo, out Container container, out string resourcePrefab, out int requiredAmount, out Vector3 position)
        {
            return TryFindGardenResourceContainerInternal(worker, workerZdo, string.Empty, 0, out container, out resourcePrefab, out requiredAmount, out position);
        }

        internal static bool TryFindGardenResourceContainer(Character worker, ZDO workerZdo, string desiredResource, int desiredAmount, out Container container, out Vector3 position)
        {
            bool found = TryFindGardenResourceContainerInternal(worker, workerZdo, desiredResource, desiredAmount, out container, out _, out _, out position);
            return found;
        }

        private static bool TryFindGardenResourceContainerInternal(Character worker, ZDO workerZdo, string desiredResource, int desiredAmount, out Container container, out string resourcePrefab, out int requiredAmount, out Vector3 position)
        {
            container = null; resourcePrefab = string.Empty; requiredAmount = 0; position = default;
            if (worker == null || workerZdo == null) return false;

            // Explicit resource lookups are also used by Forestry (for example BeechSeeds/BirchSeeds).
            // They do not depend on the garden crop registry being ready. Requiring EnsureReady() here
            // made the forestry fallback blind to real seeds that were already present in a local chest,
            // so a latched missing BirchSeeds request could keep the Shaman in NeedsResource even while
            // another planned Beech slot had BeechSeeds available. Only the generic garden-resource scan
            // needs GardenWorkRuntime's registry.
            if (string.IsNullOrEmpty(desiredResource) && !GardenWorkRuntime.EnsureReady()) return false;

            Vector3 home = WorkerZdo.ReadHome(workerZdo, worker.transform.position);
            float homeRadius = GetSearchRadius(workerZdo);
            bool hasWorkArea = WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 workCenter, out _);
            float workRadius = GetWorkAreaStorageSearchRadius(workerZdo);
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            float bestSqr = float.MaxValue;
            Container bestContainer = null; string bestResourcePrefab = string.Empty; int bestRequiredAmount = 0; Vector3 bestPosition = default;

            Action<Container> consider = candidate =>
            {
                if (!CanUseContainer(candidate, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(candidate, ownerId)) return;
                WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(candidate);
                if (role == WorkerContainerRole.Import || role == WorkerContainerRole.Export) return;
                Inventory inventory = candidate.GetInventory(); if (inventory == null) return;
                List<ItemDrop.ItemData> items = inventory.GetAllItems();
                for (int i = 0; i < items.Count; i++)
                {
                    ItemDrop.ItemData item = items[i]; if (item == null || item.m_stack <= 0) continue;
                    string prefab = GetPrefabName(item);
                    int needed;
                    if (!string.IsNullOrEmpty(desiredResource))
                    {
                        if (!string.Equals(prefab, desiredResource, StringComparison.OrdinalIgnoreCase)) continue;
                        needed = Mathf.Max(1, desiredAmount);
                    }
                    else
                    {
                        if (!GardenWorkRuntime.TryGetPlantingData(prefab, out _, out needed, out _)) continue;
                    }
                    // Keep-one invariant: a Worker may never consume the final copy from a chest.
                    if (CountPrefab(inventory, prefab) < needed + 1) continue;
                    float sqr = (candidate.transform.position - worker.transform.position).sqrMagnitude;
                    float score = sqr + (role == WorkerContainerRole.FieldDepot ? 0f : 100000f);
                    if (score >= bestSqr) continue;
                    bestSqr = score; bestContainer = candidate; bestResourcePrefab = prefab; bestRequiredAmount = needed; bestPosition = candidate.transform.position;
                }
            };

            ForEachContainerNear(home, homeRadius, consider);
            if (hasWorkArea) ForEachContainerNear(workCenter, workRadius, consider);
            container = bestContainer; resourcePrefab = bestResourcePrefab; requiredAmount = bestRequiredAmount; position = bestPosition;
            if (container != null)
            {
                SetDebug(worker, $"storage route: fetch garden resource {resourcePrefab} x{requiredAmount} from {WorkerContainerRoleRuntime.DisplayName(WorkerContainerRoleRuntime.GetRole(container))} {NormalizeObjectName(container.gameObject.name)} ({Vector3.Distance(container.transform.position, worker.transform.position):0.0} m); keep-one protected; searched Home {homeRadius:0} m + WorkArea {workRadius:0} m");
                return true;
            }
            string gardenSummary = GardenWorkRuntime.GetDebugSummary();
            SetDebug(worker, hasWorkArea
                ? $"storage: no usable garden resource (keep-one protected); searched Home {homeRadius:0} m + WorkArea {workRadius:0} m | {gardenSummary}"
                : $"storage: no usable garden resource in loaded HomeAnchor storage (keep-one protected) | {gardenSummary}");
            return false;
        }

        internal static bool TryCollectGardenResourceFromStorage(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return false;
            if (!WorkerZdo.TryReadTarget(zdo, out string desiredResource, out string targetKind, out Vector3 targetPosition)) return false;
            if (!string.Equals(targetKind, GardenResourceSourceTargetKind, StringComparison.Ordinal)) return false;
            int requiredAmount;
            WorkerGardenPlan plan = default;
            bool hasPlan = WorkerZdo.TryReadGardenPlan(zdo, out plan) && string.Equals(plan.ResourcePrefab, desiredResource, StringComparison.OrdinalIgnoreCase);
            if (hasPlan)
                requiredAmount = plan.ResourceAmount;
            else if (!GardenWorkRuntime.TryGetPlantingData(desiredResource, out _, out requiredAmount, out _)) return false;

            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Container container = FindNearbyContainer(targetPosition, 2.5f, c =>
            {
                if (!CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId) || !IsInsideContainerAura(worker.transform.position, c.transform.position)) return false;
                WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(c);
                if (role == WorkerContainerRole.Import || role == WorkerContainerRole.Export) return false;
                Inventory inv = c.GetInventory();
                ItemDrop.ItemData data = FindPrefab(inv, desiredResource);
                int protect = WorkerContainerRoleRuntime.ProtectLastItem(c) ? 1 : 0;
                return data != null && CountPrefab(inv, desiredResource) >= requiredAmount + protect;
            });

            if (container == null)
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                SetDebug(worker, $"garden resource source vanished/moved: {desiredResource}; re-scan requested");
                return true;
            }

            if (!TryOwn(container))
            {
                SetDebug(worker, $"garden resource source ownership pending: {NormalizeObjectName(container.gameObject.name)}");
                return true;
            }

            Inventory inventory = container.GetInventory();
            ItemDrop.ItemData item = FindPrefab(inventory, desiredResource);
            int protectedCount = WorkerContainerRoleRuntime.ProtectLastItem(container) ? 1 : 0;
            int availableAmount = Mathf.Max(0, CountPrefab(inventory, desiredResource) - protectedCount);
            if (item == null || availableAmount < requiredAmount)
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return true;
            }

            int pickupAmount = hasPlan
                ? FulingShamanGardenRuntime.GetDesiredPickupAmount(worker, zdo, plan, availableAmount)
                : requiredAmount;
            pickupAmount = Mathf.Clamp(pickupAmount, requiredAmount, availableAmount);

            for (int i = 0; i < pickupAmount; i++)
            {
                item = FindPrefab(inventory, desiredResource);
                if (item == null || !TryRemoveOne(inventory, item))
                {
                    // Fail closed. This should only happen before cargo is persisted. We intentionally
                    // do not invent resources; the next scan will retry against the real inventory.
                    PersistContainer(container, inventory);
                    WorkerZdo.ClearTarget(zdo);
                    zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                    SetDebug(worker, $"garden resource removal interrupted: {desiredResource}; re-scan requested");
                    return true;
                }
            }

            PersistContainer(container, inventory);
            WorkerZdo.SetCargo(zdo, desiredResource, pickupAmount);
            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.State, (int)WorkerState.MoveToWorkArea);
            string batchText = pickupAmount > requiredAmount ? $"; Fuling-Shaman 3x3 batch={pickupAmount / Mathf.Max(1, requiredAmount)} plants" : string.Empty;
            SetDebug(worker, $"storage fetched: garden {desiredResource} x{pickupAmount} -> Worker cargo; returning to work flag{batchText}");
            _debugLog?.Invoke($"Garden supply fetched from base storage: worker={worker.gameObject.name}, resource={desiredResource}, amount={pickupAmount}, chest={NormalizeObjectName(container.gameObject.name)}{batchText}");
            return true;
        }

        internal static bool TryCollectSeedFromStorage(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return false;
            if (!WorkerZdo.TryReadTarget(zdo, out string desiredSeed, out string targetKind, out Vector3 targetPosition)) return false;
            if (!string.Equals(targetKind, SeedSourceTargetKind, StringComparison.Ordinal)) return false;
            if (!ForestryWorkRuntime.TryGetSaplingPrefab(desiredSeed, out _)) return false;

            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Container container = FindNearbyContainer(targetPosition, 2.5f, c =>
            {
                if (!CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId) || !IsInsideContainerAura(worker.transform.position, c.transform.position)) return false;
                WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(c);
                if (role == WorkerContainerRole.Import || role == WorkerContainerRole.Export) return false;
                Inventory inv = c.GetInventory();
                return inv != null && CountPrefab(inv, desiredSeed) >= 2;
            });

            if (container == null)
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                SetDebug(worker, $"storage seed source vanished/moved: {desiredSeed}; re-scan requested");
                return true;
            }

            if (!TryOwn(container))
            {
                SetDebug(worker, $"storage seed source ownership pending: {NormalizeObjectName(container.gameObject.name)}");
                return true;
            }

            Inventory inventory = container.GetInventory();
            ItemDrop.ItemData item = FindPrefab(inventory, desiredSeed);
            if (item == null || CountPrefab(inventory, desiredSeed) < 2)
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return true;
            }

            if (!TryRemoveOne(inventory, item))
            {
                SetDebug(worker, $"storage seed removal failed: {desiredSeed}; cargo unchanged");
                return true;
            }

            PersistContainer(container, inventory);
            WorkerZdo.SetCargo(zdo, desiredSeed, 1);
            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.State, (int)WorkerState.MoveToWorkArea);
            SetDebug(worker, $"storage fetched: {desiredSeed} x1 -> Worker cargo; returning to work flag");
            _debugLog?.Invoke($"Forestry supply fetched from base storage: worker={worker.gameObject.name}, seed={desiredSeed}, chest={NormalizeObjectName(container.gameObject.name)}");
            return true;
        }

        internal static bool TryDepositCargoToStorage(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return false;
            if (!WorkerZdo.TryReadCargo(zdo, out string cargoPrefab, out int amount)) return false;
            if (!WorkerZdo.TryReadTarget(zdo, out string targetName, out string targetKind, out Vector3 targetPosition)) return false;
            bool matchingTarget = string.Equals(targetKind, DepositTargetKind, StringComparison.Ordinal);
            bool overflowTarget = string.Equals(targetKind, OverflowDepositTargetKind, StringComparison.Ordinal);
            if (!matchingTarget && !overflowTarget) return false;
            if (!string.Equals(targetName, cargoPrefab, StringComparison.OrdinalIgnoreCase)) return false;

            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            bool localFallback = LocalWorkFallbackRuntime.IsActive(zdo, targetName, targetKind);
            Container container = FindNearbyContainer(targetPosition, 2.5f, c =>
            {
                if (!CanUseContainer(c, ownerId) || (!localFallback && !IsInsideContainerAura(worker.transform.position, c.transform.position))) return false;
                if (WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.Normal) return false;
                Inventory inv = c.GetInventory();
                return inv != null && (!matchingTarget || InventoryContainsPrefab(inv, cargoPrefab)) && HasLikelyRoomFor(inv, cargoPrefab);
            });

            if (container == null)
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                SetDebug(worker, $"{(matchingTarget ? "matching" : "overflow")} chest for {cargoPrefab} unavailable/full; re-scan");
                return true;
            }

            if (!TryOwn(container))
            {
                SetDebug(worker, $"storage deposit ownership pending: {NormalizeObjectName(container.gameObject.name)}");
                return true;
            }

            GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(cargoPrefab) : null;
            if (prefab == null && ZNetScene.instance != null)
                prefab = ZNetScene.instance.GetPrefab(cargoPrefab);
            ItemDrop template = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (template == null || template.m_itemData == null)
            {
                SetDebug(worker, $"storage deposit blocked: item prefab {cargoPrefab} unavailable; cargo kept");
                return true;
            }

            Inventory inventory = container.GetInventory();
            ItemDrop.ItemData data = template.m_itemData.Clone();
            data.m_dropPrefab = prefab;
            data.m_stack = Mathf.Max(1, amount);

            if (!TryAddItem(inventory, data))
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                SetDebug(worker, $"matching chest rejected {cargoPrefab} x{amount} (likely full); cargo kept");
                return true;
            }

            PersistContainer(container, inventory);
            WorkerZdo.RemoveCargoAmount(zdo, cargoPrefab, amount);
            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            SetDebug(worker, $"storage deposited: {cargoPrefab} x{amount} -> {NormalizeObjectName(container.gameObject.name)}");
            _debugLog?.Invoke($"Gatherer stored cargo: worker={worker.gameObject.name}, item={cargoPrefab}, amount={amount}, chest={NormalizeObjectName(container.gameObject.name)}");
            return true;
        }

        internal static bool TryFindFieldDepotForCargo(Character worker, ZDO workerZdo, string cargoPrefab, out Container container, out Vector3 position)
        {
            container = null; position = default;
            if (worker == null || workerZdo == null || string.IsNullOrEmpty(cargoPrefab)) return false;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 workCenter, out float workRadius)) return false;
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);

            bool bestHasPartial = false;
            int bestPartialFill = -1;
            int bestOccupiedSlots = int.MaxValue;
            int bestPrefabCount = int.MaxValue;
            int bestRoom = -1;
            float bestSqr = float.MaxValue;
            Container bestContainer = null;
            Vector3 bestPosition = default;

            // v0.5.394: FieldDepots assigned to one Workzone are one logical pool. Prefer finishing
            // an existing partial stack; after that, place the next new stack in the least occupied
            // depot instead of always dumping everything into the chest nearest the worker.
            ForEachContainerNear(workCenter, Mathf.Clamp(workRadius + 4f, 12f, 150f), candidate =>
            {
                if (candidate == null || !WorkerLogisticsRuntime.IsInsideAssignedOutpost(workerZdo, candidate.transform.position, 2f)) return;
                if (!CanUseContainer(candidate, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(candidate, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(candidate) != WorkerContainerRole.FieldDepot) return;
                if (!FieldDepotWorkzoneRuntime.IsCompatibleWithWorkerWorkArea(candidate, workerZdo)) return;
                Inventory inv = candidate.GetInventory();
                if (inv == null) return;
                int room = GetAvailableCapacityForPrefab(inv, cargoPrefab);
                if (room <= 0) return;

                int partialFill = GetFullestPartialStackAmount(inv, cargoPrefab);
                bool hasPartial = partialFill > 0;
                int occupiedSlots = GetOccupiedSlotCount(inv);
                int prefabCount = CountPrefab(inv, cargoPrefab);
                float sqr = (candidate.transform.position - worker.transform.position).sqrMagnitude;

                bool better = bestContainer == null ||
                              (hasPartial && !bestHasPartial) ||
                              (hasPartial == bestHasPartial && hasPartial && partialFill > bestPartialFill) ||
                              (hasPartial == bestHasPartial && (!hasPartial || partialFill == bestPartialFill) && occupiedSlots < bestOccupiedSlots) ||
                              (hasPartial == bestHasPartial && (!hasPartial || partialFill == bestPartialFill) && occupiedSlots == bestOccupiedSlots && prefabCount < bestPrefabCount) ||
                              (hasPartial == bestHasPartial && (!hasPartial || partialFill == bestPartialFill) && occupiedSlots == bestOccupiedSlots && prefabCount == bestPrefabCount && room > bestRoom) ||
                              (hasPartial == bestHasPartial && (!hasPartial || partialFill == bestPartialFill) && occupiedSlots == bestOccupiedSlots && prefabCount == bestPrefabCount && room == bestRoom && sqr < bestSqr);
                if (!better) return;

                bestContainer = candidate;
                bestPosition = candidate.transform.position;
                bestHasPartial = hasPartial;
                bestPartialFill = partialFill;
                bestOccupiedSlots = occupiedSlots;
                bestPrefabCount = prefabCount;
                bestRoom = room;
                bestSqr = sqr;
            });

            container = bestContainer; position = bestPosition;
            if (container != null)
            {
                SetDebug(worker, $"storage route: gathered {cargoPrefab} -> pooled FieldDepot {NormalizeObjectName(container.gameObject.name)} ({Mathf.Sqrt(bestSqr):0.0} m; slots={bestOccupiedSlots}; partial={(bestHasPartial ? bestPartialFill.ToString() : "none")})");
                return true;
            }
            return false;
        }

        internal static bool TryFindHaulSource(Character worker, ZDO workerZdo, out Container container, out string cargoPrefab, out int amount, out Vector3 position)
        {
            container = null; cargoPrefab = string.Empty; amount = 0; position = default;
            if (worker == null || workerZdo == null) return false;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 workCenter, out float workRadius)) return false;
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            int capacity = HaulerWorkRuntime.GetCarryCapacity(worker);
            int preferredLoad = Mathf.Clamp(Mathf.CeilToInt(capacity * (HaulerMinimumLoadPercent / 100f)), 1, capacity);
            LastHaulMinimumLoad = preferredLoad;

            float bestFullSqr = float.MaxValue;
            float bestPartialSqr = float.MaxValue;
            Container bestFull = null, bestPartial = null;
            string bestFullPrefab = string.Empty, bestPartialPrefab = string.Empty;
            int bestFullAmount = 0, bestPartialAmount = 0;
            Vector3 bestFullPosition = default, bestPartialPosition = default;
            var scanned = new HashSet<Container>();
            var pooledSurplusByPrefab = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            LastHaulContainersScanned = 0;
            LastHaulFieldDepots = 0;
            LastHaulOwnedDepots = 0;
            LastHaulItemStacks = 0;
            LastHaulMovableUnits = 0;
            LastHaulLargestMovableStack = 0;
            LastHaulScanMode = "work-area";

            Action<Container> consider = candidate =>
            {
                if (candidate == null || !scanned.Add(candidate)) return;
                LastHaulContainersScanned++;
                WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(candidate);
                if (role != WorkerContainerRole.FieldDepot) return;
                if (!WorkerLogisticsRuntime.IsInsideAssignedOutpost(workerZdo, candidate.transform.position, 4f)) return;
                LastHaulFieldDepots++;
                if (!CanUseContainer(candidate, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(candidate, ownerId)) return;
                LastHaulOwnedDepots++;
                Inventory inv = candidate.GetInventory();
                if (inv == null) return;
                List<ItemDrop.ItemData> items = inv.GetAllItems();
                HashSet<string> itemTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < items.Count; i++)
                {
                    ItemDrop.ItemData item = items[i];
                    if (item == null || item.m_stack <= 0) continue;
                    string prefab = GetPrefabName(item);
                    if (string.IsNullOrEmpty(prefab) || !itemTypes.Add(prefab)) continue;
                    LastHaulItemStacks++;
                    int total = CountPrefab(inv, prefab);
                    int localKeep = WorkerContainerRoleRuntime.ProtectLastItem(candidate) ? 1 : 0;
                    int usable;
                    if (!pooledSurplusByPrefab.TryGetValue(prefab, out int zoneSurplus))
                    {
                        if (WorkerLogisticsRuntime.TryGetFieldDepotPoolStock(worker, workerZdo, prefab, out int poolTotal, out _, out int poolProtected) &&
                            WorkerLogisticsRuntime.TryGetFieldDepotPoolTarget(worker, workerZdo, prefab, out int poolTarget))
                        {
                            // A canonical request may live on another sibling depot. Export only the
                            // true Workzone surplus, never "surplus" created by looking at this chest
                            // in isolation. This is especially important for pooled seed reserves.
                            int zoneProtected = Mathf.Max(poolTarget, poolProtected);
                            zoneSurplus = Mathf.Max(0, poolTotal - zoneProtected);
                        }
                        else
                        {
                            int effectiveTarget = WorkerLogisticsRuntime.GetEffectiveFieldDepotTarget(candidate, prefab);
                            zoneSurplus = Mathf.Max(0, total - Mathf.Max(localKeep, effectiveTarget));
                        }
                        pooledSurplusByPrefab[prefab] = zoneSurplus;
                    }
                    usable = Mathf.Min(Mathf.Max(0, total - localKeep), Mathf.Max(0, zoneSurplus));
                    LastHaulMovableUnits += usable;
                    if (usable > LastHaulLargestMovableStack) LastHaulLargestMovableStack = usable;
                    if (usable <= 0) continue;

                    // Legacy inbound backpressure required free matching Base storage. Once an
                    // explicit Import Min/Max rule exists, that Import buffer becomes the authority:
                    // it may intentionally fill up to Max even while Sorters have nowhere to place it.
                    // At Max the per-item buffer closes and hauling stops until stock falls to Min.
                    bool managedImportBuffer = ContainerStockLimitRuntime.HasConfiguredImportLimit(ownerId, prefab);
                    if (!managedImportBuffer && !WorkerLogisticsRuntime.HasDownstreamNormalStorageCapacity(worker, workerZdo, prefab)) continue;
                    int importRoom = WorkerLogisticsRuntime.GetRoleFreeCapacity(worker, workerZdo, WorkerContainerRole.Import, prefab, requireExisting: false);
                    if (importRoom <= 0) continue;

                    int take = Mathf.Min(importRoom, Mathf.Min(HaulerWorkRuntime.GetCarryCapacity(worker, prefab, usable), usable));
                    float sqr = (candidate.transform.position - worker.transform.position).sqrMagnitude;
                    if (take >= preferredLoad)
                    {
                        if (sqr >= bestFullSqr) continue;
                        bestFullSqr = sqr; bestFull = candidate; bestFullPrefab = prefab; bestFullAmount = take; bestFullPosition = candidate.transform.position;
                    }
                    else
                    {
                        // Preferred load is a batching preference, not a deadlock condition. If no
                        // full batch exists, move the best partial stack so logistics keeps flowing.
                        bool better = bestPartial == null || take > bestPartialAmount || (take == bestPartialAmount && sqr < bestPartialSqr);
                        if (!better) continue;
                        bestPartialSqr = sqr; bestPartial = candidate; bestPartialPrefab = prefab; bestPartialAmount = take; bestPartialPosition = candidate.transform.position;
                    }
                }
            };

            // v0.2.00: a Hauler belongs to one physical outpost. Never broaden an inbound pickup
            // to every FieldDepot in the home/network radius, otherwise a Farm-A transporter can
            // silently steal work from Farm-B. The route may leave the WorkArea for the Base, but
            // the FieldDepot side is always scoped to the assigned flag.
            LastHaulScanMode = "assigned-outpost";
            float localRadius = Mathf.Clamp(workRadius + 8f, 16f, 180f);
            ForEachContainerNear(workCenter, localRadius, consider);

            Container chosen = bestFull ?? bestPartial;
            if (chosen != null)
            {
                bool full = bestFull != null;
                string selectedPrefab = full ? bestFullPrefab : bestPartialPrefab;
                int destinationCapacity = WorkerLogisticsRuntime.GetRoleFreeCapacity(worker, workerZdo, WorkerContainerRole.Import, selectedPrefab, requireExisting: false);
                if (destinationCapacity <= 0)
                {
                    SetDebug(worker, $"haul waiting: no usable Base Import capacity for {selectedPrefab} (missing/full/ownership); FieldDepot stock stays untouched; {GetHaulDebugSummary()}");
                    return false;
                }

                container = chosen;
                cargoPrefab = selectedPrefab;
                amount = Mathf.Min(destinationCapacity, full ? bestFullAmount : bestPartialAmount);
                position = full ? bestFullPosition : bestPartialPosition;
                SetDebug(worker, $"haul route: FieldDepot -> Import; fetch {cargoPrefab} x{amount} from {NormalizeObjectName(container.gameObject.name)}; destinationFree={destinationCapacity}; batch={(full ? "preferred" : "partial")}; {GetHaulDebugSummary()}");
                return amount > 0;
            }

            if (LastHaulFieldDepots == 0)
                SetDebug(worker, $"haul: no marked FieldDepot found in assigned outpost; {GetHaulDebugSummary()}");
            else if (LastHaulOwnedDepots == 0)
                SetDebug(worker, $"haul: {LastHaulFieldDepots} FieldDepot(s) found, but none match this worker/player ownership; {GetHaulDebugSummary()}");
            else if (LastHaulItemStacks == 0)
                SetDebug(worker, $"haul: {LastHaulOwnedDepots} usable FieldDepot(s) found, but they are empty; {GetHaulDebugSummary()}");
            else if (LastHaulMovableUnits <= 0)
                SetDebug(worker, $"haul: FieldDepot stock is fully protected by keep-one or active field supply targets; {GetHaulDebugSummary()}");
            else
                SetDebug(worker, $"haul: FieldDepot cargo was seen but no source could be selected; {GetHaulDebugSummary()}");
            return false;
        }

        private readonly struct LocalReturnCandidate
        {
            internal readonly Container Depot;
            internal readonly string Prefab;
            internal readonly int Usable;
            internal readonly float DistanceSqr;
            internal LocalReturnCandidate(Container depot, string prefab, int usable, float distanceSqr)
            {
                Depot = depot; Prefab = prefab ?? string.Empty; Usable = usable; DistanceSqr = distanceSqr;
            }
        }

        /// <summary>
        /// Prefer a return load when a Hauler is already standing at a FieldDepot. This is the
        /// field-side half of the pendulum route: Export -> FieldDepot, then FieldDepot -> Import
        /// without walking back to the Base empty when movable stock is available locally.
        /// Import capacity is accumulated in one network pass to avoid one expensive Base scan per
        /// item type in a mixed FieldDepot.
        /// </summary>
        internal static bool TryFindLocalReturnHaulSource(Character worker, ZDO workerZdo, float radius, out Container container, out string cargoPrefab, out int amount, out Vector3 position)
        {
            container = null; cargoPrefab = string.Empty; amount = 0; position = default;
            if (worker == null || workerZdo == null) return false;

            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            int capacity = HaulerWorkRuntime.GetCarryCapacity(worker);
            var localDepots = new List<Container>(8);
            ForEachContainerNear(worker.transform.position, Mathf.Clamp(radius, 6f, 30f), candidate =>
            {
                if (candidate == null) return;
                if (WorkerContainerRoleRuntime.GetRole(candidate) != WorkerContainerRole.FieldDepot) return;
                if (!WorkerLogisticsRuntime.IsInsideAssignedOutpost(workerZdo, candidate.transform.position, 4f)) return;
                if (!CanUseContainer(candidate, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(candidate, ownerId)) return;
                localDepots.Add(candidate);
            });

            if (localDepots.Count == 0) return false;

            var candidates = new List<LocalReturnCandidate>(24);
            var wantedPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pooledSurplusByPrefab = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int d = 0; d < localDepots.Count; d++)
            {
                Container depot = localDepots[d];
                Inventory inv = depot != null ? depot.GetInventory() : null;
                if (inv == null) continue;
                var checkedPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<ItemDrop.ItemData> items = inv.GetAllItems();
                for (int i = 0; i < items.Count; i++)
                {
                    ItemDrop.ItemData item = items[i];
                    if (item == null || item.m_stack <= 0) continue;
                    string prefab = GetPrefabName(item);
                    if (string.IsNullOrEmpty(prefab) || !checkedPrefabs.Add(prefab)) continue;
                    int total = CountPrefab(inv, prefab);
                    int localKeep = WorkerContainerRoleRuntime.ProtectLastItem(depot) ? 1 : 0;
                    if (!pooledSurplusByPrefab.TryGetValue(prefab, out int zoneSurplus))
                    {
                        if (WorkerLogisticsRuntime.TryGetFieldDepotPoolStock(worker, workerZdo, prefab, out int poolTotal, out _, out int poolProtected) &&
                            WorkerLogisticsRuntime.TryGetFieldDepotPoolTarget(worker, workerZdo, prefab, out int poolTarget))
                        {
                            int zoneProtected = Mathf.Max(poolTarget, poolProtected);
                            zoneSurplus = Mathf.Max(0, poolTotal - zoneProtected);
                        }
                        else
                        {
                            int effectiveTarget = WorkerLogisticsRuntime.GetEffectiveFieldDepotTarget(depot, prefab);
                            zoneSurplus = Mathf.Max(0, total - Mathf.Max(localKeep, effectiveTarget));
                        }
                        pooledSurplusByPrefab[prefab] = zoneSurplus;
                    }
                    int usable = Mathf.Min(Mathf.Max(0, total - localKeep), Mathf.Max(0, zoneSurplus));
                    if (usable <= 0) continue;
                    // A configured Import Min/Max rule deliberately allows a bounded buffer even
                    // while normal Base storage is full. Unconfigured items keep the legacy
                    // downstream-storage backpressure.
                    bool managedImportBuffer = ContainerStockLimitRuntime.HasConfiguredImportLimit(ownerId, prefab);
                    if (!managedImportBuffer && !WorkerLogisticsRuntime.HasDownstreamNormalStorageCapacity(worker, workerZdo, prefab)) continue;
                    candidates.Add(new LocalReturnCandidate(depot, prefab, usable, (depot.transform.position - worker.transform.position).sqrMagnitude));
                    wantedPrefabs.Add(prefab);
                }
            }
            if (candidates.Count == 0) return false;

            var importCapacity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string prefab in wantedPrefabs) importCapacity[prefab] = 0;
            // Base Import is global for the logistics network. Do not scope it to the outpost
            // HomeAnchor; an outpost Hauler must be able to return excess to any loaded Base Import.
            ForEachLoadedContainer(import =>
            {
                if (!CanUseContainer(import, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(import, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(import) != WorkerContainerRole.Import) return;
                Inventory inv = import.GetInventory();
                if (inv == null) return;
                foreach (string prefab in wantedPrefabs)
                {
                    int physical = GetAvailableCapacityForPrefab(inv, prefab);
                    int room = ContainerStockLimitRuntime.GetAcceptCapacity(import, prefab, physical);
                    if (room <= 0) continue;
                    long next = (long)importCapacity[prefab] + room;
                    importCapacity[prefab] = next >= int.MaxValue ? int.MaxValue : (int)next;
                }
            });

            LocalReturnCandidate best = default;
            bool found = false;
            int bestAmount = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                LocalReturnCandidate candidate = candidates[i];
                int room = importCapacity.TryGetValue(candidate.Prefab, out int value) ? value : 0;
                int take = Mathf.Min(HaulerWorkRuntime.GetCarryCapacity(worker, candidate.Prefab, candidate.Usable), Mathf.Min(candidate.Usable, room));
                if (take <= 0) continue;
                bool better = !found || take > bestAmount || (take == bestAmount && candidate.DistanceSqr < best.DistanceSqr);
                if (!better) continue;
                best = candidate; bestAmount = take; found = true;
            }

            if (!found || best.Depot == null || bestAmount <= 0) return false;
            container = best.Depot;
            cargoPrefab = best.Prefab;
            amount = bestAmount;
            position = best.Depot.transform.position;
            SetDebug(worker, $"haul pendulum: local FieldDepot return load {cargoPrefab} x{amount} -> Import; depotDistance={Mathf.Sqrt(best.DistanceSqr):0.0}m");
            return true;
        }

        internal static bool TryFindImportContainer(Character worker, ZDO workerZdo, string cargoPrefab, out Container container, out Vector3 position)
        {
            container = null; position = default;
            if (worker == null || workerZdo == null || string.IsNullOrEmpty(cargoPrefab)) return false;
            long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Vector3 home = WorkerZdo.ReadHome(workerZdo, worker.transform.position);
            float bestSqr = float.MaxValue;
            Container bestContainer = null;
            Vector3 bestPosition = default;
            // Import is a Base endpoint. The Hauler HomeAnchor belongs to the outpost, so limiting
            // this lookup to a radius around Home makes distant-but-loaded Base Import chests
            // invisible. Iterate the lifecycle registry instead; this is bounded to loaded
            // containers and performs no FindObjectsOfType/physics world scan.
            ForEachLoadedContainer(candidate =>
            {
                if (!CanUseContainer(candidate, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(candidate, ownerId)) return;
                if (WorkerContainerRoleRuntime.GetRole(candidate) != WorkerContainerRole.Import) return;
                Inventory inv = candidate.GetInventory();
                if (inv == null || !HasLikelyRoomFor(inv, cargoPrefab)) return;
                int physical = GetAvailableCapacityForPrefab(inv, cargoPrefab);
                if (ContainerStockLimitRuntime.GetAcceptCapacity(candidate, cargoPrefab, physical) <= 0) return;
                float sqr = (candidate.transform.position - worker.transform.position).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr; bestContainer = candidate; bestPosition = candidate.transform.position;
            });
            container = bestContainer; position = bestPosition;
            if (container != null)
            {
                SetDebug(worker, $"haul route: deliver {cargoPrefab} -> Import {NormalizeObjectName(container.gameObject.name)} ({Mathf.Sqrt(bestSqr):0.0} m)");
                return true;
            }

            // The Base can be completely unloaded. Route to the persistent Import ZDO position;
            // when the zone materializes the normal physical deposit path takes over. If the chest
            // is full, keep the cargo and wait at Base instead of cancelling the delivery.
            if (ContainerStockLimitRuntime.TryFindPersistentImportWithCapacity(ownerId, worker.transform.position, cargoPrefab, out _, out Vector3 persistentPosition, out int persistentCapacity))
            {
                position = persistentPosition;
                SetDebug(worker, $"haul route: persistent Import endpoint ({HorizontalDistance(worker.transform.position, position):0.0} m); accept={persistentCapacity}; Base unloaded -> carry cargo there");
                return true;
            }

            SetDebug(worker, $"haul blocked: no registered Import endpoint for {cargoPrefab}");
            return false;
        }

        internal static bool TryCollectHaulCargo(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null || WorkerZdo.HasCargo(zdo)) return false;
            if (!WorkerZdo.TryReadTarget(zdo, out string desiredPrefab, out string kind, out Vector3 targetPosition) ||
                !string.Equals(kind, HaulSourceTargetKind, StringComparison.Ordinal)) return false;
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            bool localFallback = LocalWorkFallbackRuntime.IsActive(zdo, desiredPrefab, kind);
            Container container = FindNearbyContainer(targetPosition, Mathf.Max(4f, ContainerAccessRadius + 1f), c =>
            {
                if (!CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return false;
                if (WorkerContainerRoleRuntime.GetRole(c) != WorkerContainerRole.FieldDepot) return false;
                if (!localFallback && !IsInsideContainerAura(worker.transform.position, c.transform.position)) return false;
                int effectiveTarget = WorkerLogisticsRuntime.GetEffectiveFieldDepotTarget(c, desiredPrefab);
                int keepOne = WorkerContainerRoleRuntime.ProtectLastItem(c) ? 1 : 0;
                int reserve = Mathf.Max(keepOne, effectiveTarget);
                return CountPrefab(c.GetInventory(), desiredPrefab) > reserve;
            });
            if (container == null)
            {
                WorkerZdo.ClearTarget(zdo); zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob); return true;
            }
            int destinationCapacity = WorkerLogisticsRuntime.GetRoleFreeCapacity(worker, zdo, WorkerContainerRole.Import, desiredPrefab, requireExisting: false);
            if (destinationCapacity <= 0)
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                SetDebug(worker, $"haul pickup skipped: no registered Import endpoint for {desiredPrefab}; cargo remains in FieldDepot");
                return true;
            }

            if (!TryOwn(container)) return true;
            Inventory inv = container.GetInventory();
            if (inv == null) return true;
            if (!WorkerInventoryMutationGuard.CanWorkersMutate(inv))
            {
                zdo.Set(WorkerZdo.State, (int)WorkerState.Collect);
                SetDebug(worker, $"haul pickup waiting: FieldDepot inventory busy for {desiredPrefab}; quiet={WorkerInventoryMutationGuard.GetRemainingQuietTime(inv):0.00}s");
                return true;
            }
            int total = CountPrefab(inv, desiredPrefab);
            int effectiveTarget = WorkerLogisticsRuntime.GetEffectiveFieldDepotTarget(container, desiredPrefab);
            int keepOne = WorkerContainerRoleRuntime.ProtectLastItem(container) ? 1 : 0;
            int protectedCount = Mathf.Max(keepOne, effectiveTarget);
            int maxTake = Mathf.Min(destinationCapacity, HaulerWorkRuntime.GetCarryCapacity(worker, desiredPrefab, Mathf.Max(0, total - protectedCount)));
            int removed = 0;
            while (removed < maxTake)
            {
                ItemDrop.ItemData item = FindPrefab(inv, desiredPrefab);
                if (item == null || !TryRemoveOne(inv, item)) break;
                removed++;
            }
            if (removed <= 0)
            {
                if (!WorkerInventoryMutationGuard.CanWorkersMutate(inv))
                {
                    zdo.Set(WorkerZdo.State, (int)WorkerState.Collect);
                    SetDebug(worker, $"haul pickup deferred: inventory changed externally for {desiredPrefab}");
                    return true;
                }
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return true;
            }
            PersistContainer(container, inv);
            WorkerZdo.SetCargo(zdo, desiredPrefab, removed);
            WorkzoneLogisticsFlowRuntime.TagCargoSource(zdo, container, WorkerZdo.ReadWorkAreaId(zdo));
            if (!WorkerZdo.TryReadCargoRoute(zdo, out _, out _))
                WorkerZdo.SetCargoRoute(zdo, WorkerLogisticsRuntime.RouteHaulInbound, default);
            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
            SetDebug(worker, $"haul collected: {desiredPrefab} x{removed}; FieldDepot keep-one={(WorkerContainerRoleRuntime.ProtectLastItem(container) ? "on" : "off")}; reserve={protectedCount}");
            return true;
        }

        internal static bool TryDepositCargoToFieldDepot(Character worker, ZDO zdo)
        {
            return TryDepositCargoToRole(worker, zdo, FieldDepotDepositTargetKind, WorkerContainerRole.FieldDepot, "FieldDepot");
        }

        internal static bool TryDepositCargoToImport(Character worker, ZDO zdo)
        {
            return TryDepositCargoToRole(worker, zdo, ImportDepositTargetKind, WorkerContainerRole.Import, "Import");
        }

        private static bool TryDepositCargoToRole(Character worker, ZDO zdo, string expectedTargetKind, WorkerContainerRole role, string label)
        {
            if (worker == null || zdo == null) return false;
            if (!WorkerZdo.TryReadCargo(zdo, out string cargoPrefab, out int amount)) return false;

            if (GardenWorkRuntime.TryResolveGatherableItemPrefab(cargoPrefab, out string canonicalCargo) &&
                !string.IsNullOrEmpty(canonicalCargo) &&
                !string.Equals(canonicalCargo, cargoPrefab, StringComparison.OrdinalIgnoreCase))
            {
                SetDebug(worker, $"storage cargo identity normalized: {cargoPrefab} -> {canonicalCargo}");
                cargoPrefab = canonicalCargo;
                WorkerZdo.SetCargo(zdo, cargoPrefab, amount);
            }

            if (!WorkerZdo.TryReadTarget(zdo, out string targetName, out string targetKind, out Vector3 targetPosition) ||
                !string.Equals(targetKind, expectedTargetKind, StringComparison.Ordinal)) return false;
            bool localFallback = LocalWorkFallbackRuntime.IsActive(zdo, targetName, targetKind);
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Container container = FindNearbyContainer(targetPosition, Mathf.Max(4f, ContainerAccessRadius + 1f), c =>
            {
                if (!CanUseContainer(c, ownerId) || !WorkerContainerRoleRuntime.MatchesOwner(c, ownerId)) return false;
                if (WorkerContainerRoleRuntime.GetRole(c) != role) return false;
                if (!localFallback && !IsInsideContainerAura(worker.transform.position, c.transform.position)) return false;
                Inventory candidateInventory = c.GetInventory();
                if (!HasLikelyRoomFor(candidateInventory, cargoPrefab)) return false;
                if (role == WorkerContainerRole.Import)
                {
                    int physical = GetAvailableCapacityForPrefab(candidateInventory, cargoPrefab);
                    if (ContainerStockLimitRuntime.GetAcceptCapacity(c, cargoPrefab, physical) <= 0) return false;
                }
                return true;
            });
            if (container == null)
            {
                if (role == WorkerContainerRole.Import)
                {
                    // Keep the target and cargo. The Hauler waits at the Base endpoint and retries
                    // on later ticks when the zone/chest becomes available or space is freed.
                    zdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
                    SetDebug(worker, $"haul waiting at Import: no physical chest with room for {cargoPrefab}; cargo retained");
                    return true;
                }

                // Gatherers must never lose their delivery phase just because the selected
                // FieldDepot became full, was replaced, or moved while they were travelling.
                // Clear only the stale physical target; the next AI decision will immediately
                // resolve another marked FieldDepot with free space. Cargo remains untouched.
                WorkerRole workerRole = (WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
                bool gathererFieldDepot = role == WorkerContainerRole.FieldDepot && workerRole == WorkerRole.Gatherer;
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)(gathererFieldDepot ? WorkerState.Deliver : WorkerState.FindJob));
                if (gathererFieldDepot)
                    SetDebug(worker, $"Gatherer FieldDepot target unavailable/full for {cargoPrefab}; cargo retained, searching another FieldDepot");
                return true;
            }
            if (!TryOwn(container)) return true;
            GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(cargoPrefab) : null;
            if (prefab == null && ZNetScene.instance != null) prefab = ZNetScene.instance.GetPrefab(cargoPrefab);
            ItemDrop template = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (template?.m_itemData == null) return true;
            Inventory inv = container.GetInventory();
            if (inv == null) return true;

            if (role == WorkerContainerRole.Import)
            {
                int physical = GetAvailableCapacityForPrefab(inv, cargoPrefab);
                int accepted = ContainerStockLimitRuntime.GetAcceptCapacity(container, cargoPrefab, physical);
                int request = Mathf.Min(amount, accepted);
                if (request <= 0)
                {
                    zdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
                    SetDebug(worker, $"haul waiting at Import: {cargoPrefab} Min/Max buffer closed; cargo x{amount} retained");
                    return true;
                }

                int before = CountPrefab(inv, cargoPrefab);
                ItemDrop.ItemData importData = template.m_itemData.Clone();
                importData.m_dropPrefab = prefab;
                importData.m_stack = Mathf.Max(1, request);
                TryAddItem(inv, importData);
                int after = CountPrefab(inv, cargoPrefab);
                int deposited = Mathf.Clamp(after - before, 0, request);
                if (deposited <= 0)
                {
                    zdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
                    SetDebug(worker, $"haul waiting at Import: no measured room for {cargoPrefab}; cargo x{amount} retained");
                    return true;
                }

                PersistContainer(container, inv);
                bool trackedInboundExport = WorkerZdo.TryReadCargoRoute(zdo, out string flowImportRoute, out _) &&
                                            string.Equals(flowImportRoute, WorkerLogisticsRuntime.RouteHaulInbound, StringComparison.Ordinal);
                if (trackedInboundExport)
                    WorkzoneLogisticsFlowRuntime.RecordTaggedExport(zdo, cargoPrefab, deposited, WorkerZdo.ReadWorkAreaId(zdo));
                WorkerZdo.RemoveCargoAmount(zdo, cargoPrefab, deposited);
                if (WorkerZdo.HasCargo(zdo))
                {
                    int remaining = WorkerZdo.GetCargoAmount(zdo, cargoPrefab);
                    zdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
                    SetDebug(worker, $"storage partial deposit: {cargoPrefab} x{deposited} -> Import; x{remaining} retained until buffer reopens");
                    return true;
                }

                bool completedInboundImport = WorkerZdo.TryReadCargoRoute(zdo, out string completedImportRoute, out _) &&
                                              string.Equals(completedImportRoute, WorkerLogisticsRuntime.RouteHaulInbound, StringComparison.Ordinal);
                WorkerZdo.ClearCargoRoute(zdo);
                if (completedInboundImport) WorkzoneLogisticsFlowRuntime.ClearCargoSource(zdo);
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)(completedInboundImport ? WorkerState.ReturnHome : WorkerState.FindJob));
                SetDebug(worker, completedInboundImport
                    ? $"storage deposited: {cargoPrefab} x{deposited} -> Import {NormalizeObjectName(container.gameObject.name)}; haul complete -> ReturnHome"
                    : $"storage deposited: {cargoPrefab} x{deposited} -> Import {NormalizeObjectName(container.gameObject.name)}");
                return true;
            }

            int beforeField = CountPrefab(inv, cargoPrefab);
            ItemDrop.ItemData data = template.m_itemData.Clone();
            data.m_dropPrefab = prefab; data.m_stack = Mathf.Max(1, amount);

            // AddItem may accept only part of a stack if the selected chest changes between route
            // planning and arrival. Measure the real delta so cargo is never duplicated by retaining
            // the full Worker stack after a partial physical insert.
            TryAddItem(inv, data);
            int afterField = CountPrefab(inv, cargoPrefab);
            int depositedField = Mathf.Clamp(afterField - beforeField, 0, amount);
            if (depositedField <= 0)
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
                SetDebug(worker, $"FieldDepot rejected {cargoPrefab} x{amount}; cargo retained, selecting another pooled depot");
                return true;
            }

            PersistContainer(container, inv);
            WorkerZdo.RemoveCargoAmount(zdo, cargoPrefab, depositedField);
            WorkerZdo.ClearTarget(zdo);

            WorkerRole completedWorkerRole = (WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
            bool hasMoreCargo = WorkerZdo.HasCargo(zdo);
            if (hasMoreCargo)
            {
                // Keep all remaining cargo intact and resolve the next best FieldDepot on the next
                // delivery decision. This covers both a partially accepted same-item stack and a
                // multi-prefab Gatherer manifest.
                zdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
                SetDebug(worker, $"storage deposited: {cargoPrefab} x{depositedField}/{amount} -> {label} {NormalizeObjectName(container.gameObject.name)}; remaining cargo will use pooled fallback");
                return true;
            }

            WorkerZdo.ClearCargoRoute(zdo);
            if (completedWorkerRole == WorkerRole.Gatherer)
                WorkerJobProfileRuntime.NoteCompletedAction(zdo, WorkerRole.Gatherer, WorkOrder.GatherResources);
            zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            SetDebug(worker, $"storage deposited: {cargoPrefab} x{depositedField} -> {label} {NormalizeObjectName(container.gameObject.name)}; keep-one={(WorkerContainerRoleRuntime.ProtectLastItem(container) ? "on" : "off")}");
            return true;
        }

        private static void ValidateApiOnce()
        {
            if (_reportedApiWarning) return;
            List<string> missing = new List<string>();
            if (ClaimOwnershipMethod == null) missing.Add("ZNetView.ClaimOwnership");
            if (ContainerSaveMethod == null) missing.Add("Container.Save");
            if (InventoryAddItemMethod == null) missing.Add("Inventory.AddItem(ItemData)");
            if (InventoryRemoveItemDataMethod == null && InventoryRemoveItemNameMethod == null) missing.Add("Inventory.RemoveItem");
            if (missing.Count > 0)
            {
                _reportedApiWarning = true;
                _debugLog?.Invoke("Storage API partial/missing: " + string.Join(", ", missing) + ". Workers will fail closed and keep cargo instead of inventing/losing items.");
            }
        }

        internal static void ForEachLoadedContainer(Action<Container> visitor)
        {
            int containers = WorkerWorldIndex.ForEachLoadedContainer(container =>
            {
                if (container == null || !container.gameObject.activeInHierarchy) return;
                visitor?.Invoke(container);
            });
            LastPhysicsStorageHits = 0;
            LastSceneStorageViews = 0;
            LastSceneStorageContainers = containers;
            LastSceneStorageStatus = $"registry-all:{containers}/{WorkerWorldIndex.ContainerCount}";
        }

        internal static void ForEachContainerNear(Vector3 center, float radius, Action<Container> visitor)
        {
            // v0.1.74: use the lifecycle index instead of broad physics queries. A 250 m
            // OverlapSphere in a built-up base can touch thousands of floor/wall/decoration
            // colliders even though Workers only cares about a few dozen containers.
            Seen.Clear();
            int containers = WorkerWorldIndex.ForEachContainer(center, radius, container =>
            {
                if (container == null || !container.gameObject.activeInHierarchy || !Seen.Add(container)) return;
                visitor(container);
            });
            LastPhysicsStorageHits = 0;
            LastSceneStorageViews = 0;
            LastSceneStorageContainers = containers;
            LastSceneStorageStatus = $"registry:{containers}/{WorkerWorldIndex.ContainerCount}";
            Seen.Clear();
        }

        private static void VisitSceneContainer(object value, Vector3 center, float radiusSqr, Action<Container> visitor, ref int views, ref int containers)
        {
            ZNetView view = value as ZNetView;
            if (view == null || !view || !view.IsValid()) return;
            views++;
            if ((view.transform.position - center).sqrMagnitude > radiusSqr) return;
            Container container = view.GetComponent<Container>() ?? view.GetComponentInChildren<Container>(true) ?? view.GetComponentInParent<Container>();
            if (container == null || !container.gameObject.activeInHierarchy || !Seen.Add(container)) return;
            containers++;
            visitor(container);
        }

        private static object GetSceneInstancesCollection()
        {
            if (!_sceneInstancesFieldResolved)
            {
                _sceneInstancesFieldResolved = true;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                _sceneInstancesField = typeof(ZNetScene).GetField("m_instances", flags);
                if (_sceneInstancesField == null)
                {
                    FieldInfo[] fields = typeof(ZNetScene).GetFields(flags);
                    for (int i = 0; i < fields.Length; i++)
                    {
                        FieldInfo field = fields[i];
                        Type type = field.FieldType;
                        if (!typeof(IEnumerable).IsAssignableFrom(type)) continue;
                        if (type.IsGenericType)
                        {
                            Type[] args = type.GetGenericArguments();
                            if (args.Length == 2 && typeof(ZNetView).IsAssignableFrom(args[1])) { _sceneInstancesField = field; break; }
                        }
                        if (field.Name.IndexOf("instance", StringComparison.OrdinalIgnoreCase) >= 0) { _sceneInstancesField = field; break; }
                    }
                }
            }
            if (_sceneInstancesField == null || ZNetScene.instance == null) return null;
            try { return _sceneInstancesField.GetValue(ZNetScene.instance); }
            catch { return null; }
        }

        private static Container FindNearbyContainer(Vector3 center, float radius, Func<Container, bool> predicate)
        {
            Container best = null;
            float bestSqr = float.MaxValue;
            ForEachContainerNear(center, radius, container =>
            {
                if (predicate != null && !predicate(container)) return;
                float sqr = (container.transform.position - center).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                best = container;
            });
            return best;
        }

        internal static bool CanUseContainer(Container container, long ownerPlayerId)
        {
            if (container == null || container.GetInventory() == null) return false;

            WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(container);
            bool explicitBaseEndpoint = role == WorkerContainerRole.Import || role == WorkerContainerRole.Export;

            // Import/Export are explicit logistics endpoints. Their Workers ownership marker is the
            // authority for routing; do not reject them only because the underlying Piece creator
            // belongs to another character/save identity. This previously made a perfectly marked
            // Base Import invisible to an outpost Hauler and surfaced as "Import storage full".
            if (explicitBaseEndpoint)
            {
                if (!WorkerContainerRoleRuntime.MatchesOwner(container, ownerPlayerId)) return false;
            }
            else
            {
                Piece piece = container.GetComponentInParent<Piece>();
                if (piece != null && ownerPlayerId != 0L)
                {
                    long creator = piece.GetCreator();
                    if (creator != 0L && creator != ownerPlayerId) return false;
                }
            }

            ZNetView nview = GetNetworkView(container);
            return nview != null && nview.IsValid() && nview.GetZDO() != null;
        }

        internal static bool TryOwn(Container container)
        {
            ZNetView nview = GetNetworkView(container);
            if (nview == null || !nview.IsValid()) return false;
            if (nview.IsOwner()) return true;
            if (ClaimOwnershipMethod == null) return false;
            try
            {
                ClaimOwnershipMethod.Invoke(nview, null);
                return nview.IsOwner();
            }
            catch
            {
                return false;
            }
        }

        private static ZNetView GetNetworkView(Container container)
        {
            if (container == null) return null;
            ZNetView nview = container.GetComponent<ZNetView>();
            if (nview == null) nview = container.GetComponentInParent<ZNetView>();
            return nview;
        }

        internal static ItemDrop.ItemData FindSupportedSeed(Inventory inventory, out string prefabName)
        {
            prefabName = string.Empty;
            if (inventory == null) return null;
            List<ItemDrop.ItemData> items = inventory.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (item == null || item.m_stack <= 0) continue;
                string prefab = GetPrefabName(item);
                if (!ForestryWorkRuntime.TryGetSaplingPrefab(prefab, out _)) continue;
                if (CountPrefab(inventory, prefab) < 2) continue;
                prefabName = prefab;
                return item;
            }
            return null;
        }

        internal static int CountPrefab(Inventory inventory, string prefabName)
        {
            if (inventory == null || string.IsNullOrEmpty(prefabName)) return 0;
            int total = 0;
            List<ItemDrop.ItemData> items = inventory.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (item != null && item.m_stack > 0 && ItemMatchesPrefab(item, prefabName)) total += item.m_stack;
            }
            return total;
        }

        internal static ItemDrop.ItemData FindPrefab(Inventory inventory, string prefabName)
        {
            if (inventory == null || string.IsNullOrEmpty(prefabName)) return null;
            List<ItemDrop.ItemData> items = inventory.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (item == null || item.m_stack <= 0) continue;
                if (ItemMatchesPrefab(item, prefabName)) return item;
            }
            return null;
        }

        internal static bool InventoryContainsPrefab(Inventory inventory, string prefabName)
        {
            return FindPrefab(inventory, prefabName) != null;
        }

        internal static bool HasLikelyRoomFor(Inventory inventory, string prefabName)
        {
            return GetAvailableCapacityForPrefab(inventory, prefabName) > 0;
        }

        /// <summary>
        /// Returns how many additional units of one prefab can fit in this inventory right now.
        /// Existing partial stacks are counted first, then empty slots using the real ObjectDB max stack.
        /// This is used before logistics pickup so Workers never create cargo that has nowhere to go.
        /// </summary>
        internal static int GetAvailableCapacityForPrefab(Inventory inventory, string prefabName)
        {
            if (inventory == null || string.IsNullOrEmpty(prefabName)) return 0;

            GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            if (prefab == null && ZNetScene.instance != null) prefab = ZNetScene.instance.GetPrefab(prefabName);
            ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            int maxStack = Mathf.Max(1, drop?.m_itemData?.m_shared?.m_maxStackSize ?? 1);

            long capacity = 0;
            List<ItemDrop.ItemData> items = inventory.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (item == null || item.m_stack <= 0 || !ItemMatchesPrefab(item, prefabName)) continue;
                int itemMax = Mathf.Max(1, item.m_shared != null ? item.m_shared.m_maxStackSize : maxStack);
                capacity += Mathf.Max(0, itemMax - item.m_stack);
            }

            int emptySlots = Mathf.Max(0, inventory.GetEmptySlots());

            // Defensive fallback for container inventories whose GetEmptySlots() can transiently
            // report 0 even though the backing grid is empty/has free cells (observed with the
            // Blackmetal Chest while the UI visibly showed an empty inventory).  Derive free
            // cells from the inventory grid dimensions when available instead of treating a
            // false zero as "Import full". Reflection is cached by the CLR and only used on
            // this rare fallback path; the normal fast path remains GetEmptySlots().
            if (emptySlots <= 0)
            {
                int occupiedSlots = 0;
                for (int i = 0; i < items.Count; i++)
                {
                    ItemDrop.ItemData item = items[i];
                    if (item != null && item.m_stack > 0) occupiedSlots++;
                }

                try
                {
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    FieldInfo widthField = typeof(Inventory).GetField("m_width", flags);
                    FieldInfo heightField = typeof(Inventory).GetField("m_height", flags);
                    if (widthField != null && heightField != null)
                    {
                        int width = Convert.ToInt32(widthField.GetValue(inventory));
                        int height = Convert.ToInt32(heightField.GetValue(inventory));
                        int gridSlots = Mathf.Max(0, width) * Mathf.Max(0, height);
                        if (gridSlots > occupiedSlots) emptySlots = gridSlots - occupiedSlots;
                    }
                }
                catch { }
            }

            capacity += (long)emptySlots * maxStack;
            return capacity >= int.MaxValue ? int.MaxValue : (int)capacity;
        }

        internal static int GetOccupiedSlotCount(Inventory inventory)
        {
            if (inventory == null) return int.MaxValue;
            int occupied = 0;
            List<ItemDrop.ItemData> items = inventory.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (item != null && item.m_stack > 0) occupied++;
            }
            return occupied;
        }

        /// <summary>
        /// Returns the amount in the fullest non-full stack for one prefab, or 0 when no partial
        /// stack exists. Delivery routing uses this to finish an existing stack before opening a
        /// fresh slot in another pooled FieldDepot.
        /// </summary>
        internal static int GetFullestPartialStackAmount(Inventory inventory, string prefabName)
        {
            if (inventory == null || string.IsNullOrEmpty(prefabName)) return 0;
            int fullest = 0;
            List<ItemDrop.ItemData> items = inventory.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (item == null || item.m_stack <= 0 || !ItemMatchesPrefab(item, prefabName)) continue;
                int maxStack = Mathf.Max(1, item.m_shared != null ? item.m_shared.m_maxStackSize : 1);
                if (maxStack <= 1 || item.m_stack >= maxStack) continue;
                if (item.m_stack > fullest) fullest = item.m_stack;
            }
            return fullest;
        }

        internal static string GetPrefabName(ItemDrop.ItemData item)
        {
            if (item == null) return string.Empty;
            if (item.m_dropPrefab != null) return NormalizeObjectName(item.m_dropPrefab.name);
            // Some inventories/modded item paths can temporarily lack m_dropPrefab. Resolve the
            // canonical prefab through ObjectDB using the shared localization-name identity.
            string sharedName = item.m_shared != null ? item.m_shared.m_name : string.Empty;
            if (!string.IsNullOrEmpty(sharedName) && ObjectDB.instance != null)
            {
                List<GameObject> prefabs = ObjectDB.instance.m_items;
                if (prefabs != null)
                    for (int i = 0; i < prefabs.Count; i++)
                    {
                        GameObject go = prefabs[i];
                        ItemDrop drop = go != null ? go.GetComponent<ItemDrop>() : null;
                        if (drop?.m_itemData?.m_shared == null) continue;
                        if (string.Equals(drop.m_itemData.m_shared.m_name, sharedName, StringComparison.Ordinal))
                            return NormalizeObjectName(go.name);
                    }
            }
            return string.Empty;
        }

        internal static bool ItemMatchesPrefab(ItemDrop.ItemData item, string prefabName)
        {
            if (item == null || string.IsNullOrEmpty(prefabName)) return false;
            string actual = GetPrefabName(item);
            if (string.Equals(actual, prefabName, StringComparison.OrdinalIgnoreCase)) return true;

            // Secondary identity: compare Valheim's shared localization key. This protects Workers
            // from display/localization aliases while still using the real ObjectDB item identity.
            if (item.m_shared == null || ObjectDB.instance == null) return false;
            GameObject wanted = ObjectDB.instance.GetItemPrefab(prefabName);
            ItemDrop wantedDrop = wanted != null ? wanted.GetComponent<ItemDrop>() : null;
            string wantedShared = wantedDrop?.m_itemData?.m_shared?.m_name;
            return !string.IsNullOrEmpty(wantedShared) && string.Equals(item.m_shared.m_name, wantedShared, StringComparison.Ordinal);
        }

        internal static bool TryAddItem(Inventory inventory, ItemDrop.ItemData item)
        {
            if (inventory == null || item == null || InventoryAddItemMethod == null) return false;
            if (!WorkerInventoryMutationGuard.CanWorkersMutate(inventory)) return false;
            try
            {
                using (WorkerInventoryMutationGuard.EnterWorkerMutation())
                {
                    object result = InventoryAddItemMethod.Invoke(inventory, new object[] { item });
                    return result is bool b && b;
                }
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryRemoveOne(Inventory inventory, ItemDrop.ItemData item)
        {
            if (inventory == null || item == null) return false;
            if (!WorkerInventoryMutationGuard.CanWorkersMutate(inventory)) return false;
            try
            {
                using (WorkerInventoryMutationGuard.EnterWorkerMutation())
                {
                    if (InventoryRemoveItemDataMethod != null)
                    {
                        object result = InventoryRemoveItemDataMethod.Invoke(inventory, new object[] { item, 1 });
                        // Current Valheim returns void for this overload in some builds and bool in others.
                        if (result is bool b) return b;
                        return true;
                    }

                    if (InventoryRemoveItemNameMethod != null && item.m_shared != null)
                    {
                        int before = item.m_stack;
                        InventoryRemoveItemNameMethod.Invoke(inventory, new object[] { item.m_shared.m_name, 1, -1, true });
                        return item.m_stack < before || !inventory.ContainsItem(item);
                    }
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        internal static void PersistContainer(Container container, Inventory inventory)
        {
            using (WorkerInventoryMutationGuard.EnterWorkerMutation())
            {
                // v0.5.394: repair fragmented resource stacks whenever Workers already own a
                // FieldDepot mutation. This is event-driven (no periodic chest scan) and also heals
                // old states such as Carrot 2/50 + Carrot 18/50 in the same depot.
                if (container != null && inventory != null && WorkerContainerRoleRuntime.GetRole(container) == WorkerContainerRole.FieldDepot)
                    CompactFieldDepotStacks(inventory);

                try
                {
                    if (ContainerSaveMethod != null) ContainerSaveMethod.Invoke(container, null);
                }
                catch { }

                try
                {
                    if (InventoryChangedTwoArgsMethod != null)
                        InventoryChangedTwoArgsMethod.Invoke(inventory, new object[] { false, false });
                    else if (InventoryChangedNoArgsMethod != null)
                        InventoryChangedNoArgsMethod.Invoke(inventory, null);
                }
                catch { }
            }
        }

        private static int CompactFieldDepotStacks(Inventory inventory)
        {
            if (inventory == null || InventoryRemoveItemDataMethod == null) return 0;
            List<ItemDrop.ItemData> items = inventory.GetAllItems();
            if (items == null || items.Count < 2) return 0;

            int movedTotal = 0;
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData target = items[i];
                if (target == null || target.m_stack <= 0 || !inventory.ContainsItem(target)) continue;
                int maxStack = Mathf.Max(1, target.m_shared != null ? target.m_shared.m_maxStackSize : 1);
                if (maxStack <= 1 || target.m_stack >= maxStack) continue;
                string prefab = GetPrefabName(target);
                if (string.IsNullOrEmpty(prefab)) continue;

                for (int j = i + 1; j < items.Count && target.m_stack < maxStack; j++)
                {
                    ItemDrop.ItemData donor = items[j];
                    if (donor == null || donor.m_stack <= 0 || !inventory.ContainsItem(donor)) continue;
                    if (!ItemMatchesPrefab(donor, prefab)) continue;

                    int room = maxStack - target.m_stack;
                    int wanted = Mathf.Min(room, donor.m_stack);
                    if (wanted <= 0) continue;

                    int before = donor.m_stack;
                    try
                    {
                        InventoryRemoveItemDataMethod.Invoke(inventory, new object[] { donor, wanted });
                    }
                    catch { continue; }

                    bool donorStillPresent = inventory.ContainsItem(donor);
                    int after = donorStillPresent ? Mathf.Max(0, donor.m_stack) : 0;
                    int removed = Mathf.Clamp(before - after, 0, wanted);
                    if (removed <= 0) continue;

                    target.m_stack += removed;
                    movedTotal += removed;
                    if (!donorStillPresent) donor.m_stack = 0;
                }
            }
            return movedTotal;
        }

        internal static void SetDebug(Character worker, string status)
        {
            if (worker != null) DebugStatus[worker.GetInstanceID()] = status ?? string.Empty;
        }

        internal static string NormalizeObjectName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            const string clone = "(Clone)";
            return name.EndsWith(clone, StringComparison.Ordinal) ? name.Substring(0, name.Length - clone.Length) : name;
        }
    }
}
