using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("com.jotunn.jotunn", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("local.smartreplant", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "germa.valheim.workers";
        public const string PluginName = "Valheim_Workers";
        public const string PluginVersion = "0.6.0";
        private const string NextWorkzoneIndexKey = "vw_next_workzone_index";

        private sealed class FieldDepotStockUiEntry
        {
            internal string Prefab = string.Empty;
            internal string DisplayName = string.Empty;
            internal int Current;
            internal int Reserve;
            internal bool SeedResource;
            internal int Trigger;
            internal bool SeedMode;
        }

        private sealed class ContainerStockLimitUiEntry
        {
            internal string Prefab = string.Empty;
            internal string DisplayName = string.Empty;
            internal int Current;
            internal bool Configured;
            internal int Minimum;
            internal int Maximum;
            internal bool Paused;
        }

        private sealed class DepotItemSearchEntry
        {
            internal string Prefab = string.Empty;
            internal string DisplayName = string.Empty;
            internal string SearchText = string.Empty;
        }

        internal static ManualLogSource Log;
        internal static Plugin Instance { get; private set; }
        private static bool _depotLocalizationReflectionResolved;
        private static object _depotLocalizationInstance;
        private static MethodInfo _depotLocalizationMethod;
        private static readonly System.Collections.Generic.Dictionary<string, string> _depotLocalizationCache = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<bool> _debugMode;
        private ConfigEntry<bool> _showDebugOverlay;
        private ConfigEntry<bool> _backgroundMonitorOnStart;
        private ConfigEntry<KeyCode> _backgroundMonitorKey;
        private ConfigEntry<float> _workRadius;
        private ConfigEntry<float> _recruitSearchRadius;
        private ConfigEntry<KeyCode> _recruitKey;
        private ConfigEntry<bool> _showWorkerMapMarkers;
        private ConfigEntry<bool> _showHomeMapMarkers;
        private ConfigEntry<bool> _showMapMarkerNames;
        private ConfigEntry<float> _authorityHandoffRadius;
        private ConfigEntry<float> _spawnerProbeRadius;
        private ConfigEntry<float> _spawnerOfferingRadius;
        private ConfigEntry<float> _homeAssignmentRadius;
        private ConfigEntry<float> _trollHomeAssignmentRadius;
        private ConfigEntry<float> _stoneGolemHomeAssignmentRadius;
        private ConfigEntry<float> _neckHomeAssignmentRadius;
        private ConfigEntry<float> _loxHomeAssignmentRadius;
        private ConfigEntry<float> _drakeHomeAssignmentRadius;
        private ConfigEntry<float> _workerAssignmentAimRadius;
        private ConfigEntry<bool> _creatorMode;
        private ConfigEntry<KeyCode> _devReloadKey;
        private ConfigEntry<float> _workFlagDefaultRadius;
        private ConfigEntry<float> _containerAccessRadius;
        private ConfigEntry<float> _logisticsNetworkRadius;
        private ConfigEntry<float> _navigatorStuckSeconds;
        private ConfigEntry<float> _navigatorRecoveryDistance;
        private ConfigEntry<float> _haulerMinimumLoadPercent;
        private ConfigEntry<float> _rangedShootRange;
        private ConfigEntry<WorkerStance> _defaultWorkerStance;
        private ConfigEntry<float> _wolfPatrolRadius;
        private ConfigEntry<bool> _loxClearNaturalObstacles;
        private ConfigEntry<float> _loxObstacleClearSeconds;
        private ConfigEntry<bool> _autoRecoverStuckWorkers;
        private ConfigEntry<float> _waterStuckRecoverySeconds;

        private string _status = "Ready";
        private Character _lastWorker;
        private float _nextStatusExpiry;
        private float _nextSmartCoreAttempt;
        private float _smartCoreAttemptDeadline;
        private float _nextIndependentAirTick;
        private float _lastIndependentAirTick;
        private const float IndependentAirTickSeconds = 0.05f;
        private WorkerAiController _workerAi;
        private WorkerOwnershipRuntime _ownership;
        private MapMarkerManager _mapMarkers;
        private GreydwarfSpawnerProbeRuntime _spawnerProbe;
        private GreydwarfRecruitmentRuntime _greydwarfRecruitment;
        private GreydwarfHomeSeedRewardRuntime _greydwarfHomeSeedReward;
        private GreydwarfHomePieceRuntime _greydwarfHomePiece;
        private DraugrRestPieceRuntime _draugrRestPiece;
        private DraugrHonorShrinePieceRuntime _draugrHonorShrinePiece;
        private TrollHomePieceRuntime _trollHomePiece;
        private StoneGolemHomePieceRuntime _stoneGolemHomePiece;
        private SurtlingHomePieceRuntime _surtlingHomePiece;
        private SurtlingSignalFirePieceRuntime _surtlingSignalFirePiece;
        private NeckHomePieceRuntime _neckHomePiece;
        private NeckTrapPieceRuntime _neckTrapPiece;
        private LoxHomePieceRuntime _loxHomePiece;
        private DrakeHomePieceRuntime _drakeHomePiece;
        private DrakeEggNestPieceRuntime _drakeEggNestPiece;
        private FulingHomePieceRuntime _fulingHomePiece;
        private FulingRestChairPieceRuntime _fulingRestChairPiece;
        private NormalFieldDepotRuntime _normalFieldDepotPiece;
        private AdvancedFieldDepotRuntime _advancedFieldDepotPiece;
        private HunterStandPieceRuntime _hunterStandPiece;
        private LivestockFeedSignPieceRuntime _livestockFeedSignPiece;
        private ProductionDisplayPieceRuntime _productionDisplayPiece;
        private SmartStorageFeedingPieceBridge _smartStorageFeedingBridge;
        private LivestockFeedSignMarker _openLivestockFeedSign;
        private Rect _livestockFeedMenuRect = new Rect(650f, 100f, 640f, 700f);
        private Vector2 _livestockFeedMenuScroll;
        private Vector2 _livestockFeedAddScroll;
        private string _livestockFeedSearch = string.Empty;
        private readonly System.Collections.Generic.List<LivestockFeedingRuntime.FeedItemOption> _livestockNearbyFeedOptions = new System.Collections.Generic.List<LivestockFeedingRuntime.FeedItemOption>();
        private CreatorSpawnRuntime _creatorSpawn;
        private WorkFlagPieceRuntime _workFlagPiece;
        private WorkZoneBirdViewRuntime _workZoneBirdView;
        private LivestockPenBirdViewRuntime _livestockPenBirdView;
        private WorkerLocateCameraRuntime _workerLocateCamera;
        private GardenPlanningRuntime _gardenPlanning;
        private WorkersWildFloraRuntime _workersWildFlora;
        private WorkerFoodItemRuntime _workerFoodItem;
        private WorkersLoreRuntime _workersLore;
        private WorkerReachabilityHeatmapRuntime _reachabilityHeatmap;
        private WorkFlagInteractable _openWorkFlag;
        private Rect _workFlagMenuRect = new Rect(680f, 70f, 480f, 720f);
        private GameObject _openProductionStation;
        private Rect _productionStationMenuRect = new Rect(690f, 120f, 520f, 520f);
        private Vector2 _productionStationScroll;
        private Character _openWorkerMenu;
        private ZDO _openPersistentWorkerMenuZdo;
        private ZDO _openPersistentWorkerMenuHomeZdo;
        private Vector3 _openPersistentWorkerMenuHomePosition;
        private string _openPersistentWorkerMenuHomeLabel = string.Empty;
        private Rect _workerMenuRect = new Rect(650f, 100f, 560f, 680f);
        private Vector2 _workerMenuScroll;
        private bool _gatherTaskDetailsExpanded;
        private bool _harvestTaskDetailsExpanded;
        private bool _lumberjackTreeDetailsExpanded;
        private bool _stonemasonResourceDetailsExpanded;
        private readonly System.Collections.Generic.List<LumberjackTreeOption> _lumberjackTreeOptionScratch =
            new System.Collections.Generic.List<LumberjackTreeOption>(16);
        private readonly System.Collections.Generic.List<StonemasonRockOption> _stonemasonRockOptionScratch =
            new System.Collections.Generic.List<StonemasonRockOption>(16);
        private bool _gardenerPlantDetailsExpanded;
        private bool _gardenerForestryDetailsExpanded;
        private bool _hunterPreyDetailsExpanded;
        private Character _openWorkerInventory;
        private Rect _workerInventoryRect = new Rect(700f, 140f, 500f, 460f);
        private Vector2 _workerInventoryScroll;
        private readonly System.Collections.Generic.List<WorkAssignmentOption> _workerWorkOptions = new System.Collections.Generic.List<WorkAssignmentOption>();
        private string _workFlagNameEdit = string.Empty;
        private string _workFlagNameEditId = string.Empty;
        private Container _openWorkerContainer;
        private Rect _workerContainerMenuRect = new Rect(640f, 70f, 600f, 690f);
        private Vector2 _workerContainerScroll;
        private readonly System.Collections.Generic.List<WorkAreaSnapshot> _fieldDepotWorkzoneOptions = new System.Collections.Generic.List<WorkAreaSnapshot>();
        private readonly System.Collections.Generic.List<FieldSupplyDemandCacheRuntime.PoolDemandSnapshot> _fieldDepotPoolDemandRows = new System.Collections.Generic.List<FieldSupplyDemandCacheRuntime.PoolDemandSnapshot>();
        private float _fieldDepotPoolDemandRefreshAt;
        private string _fieldDepotPoolDemandWorkAreaId = string.Empty;
        private bool _fieldDepotStockSettingsOpen;
        private Rect _fieldDepotStockMenuRect = new Rect(510f, 90f, 820f, 720f);
        private Vector2 _fieldDepotStockScroll;
        private string _fieldDepotItemSearch = string.Empty;
        private readonly System.Collections.Generic.Dictionary<string, string> _containerReserveEdits = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
        private readonly System.Collections.Generic.Dictionary<string, string> _containerTriggerEdits = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
        private readonly System.Collections.Generic.List<string> _containerSeedRuleScratch = new System.Collections.Generic.List<string>();
        private readonly System.Collections.Generic.List<string> _containerConfiguredRuleScratch = new System.Collections.Generic.List<string>();
        private FieldDepotStockRuleRuntime.PolicySnapshot _fieldDepotStockClipboard;
        private readonly System.Collections.Generic.List<FieldDepotStockUiEntry> _fieldDepotStockUiEntries = new System.Collections.Generic.List<FieldDepotStockUiEntry>();
        private bool _containerStockLimitSettingsOpen;
        private Rect _containerStockLimitMenuRect = new Rect(535f, 105f, 780f, 690f);
        private Vector2 _containerStockLimitScroll;
        private string _containerStockLimitSearch = string.Empty;
        private readonly System.Collections.Generic.Dictionary<string, string> _containerStockMinEdits = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
        private readonly System.Collections.Generic.Dictionary<string, string> _containerStockMaxEdits = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
        private readonly System.Collections.Generic.List<ContainerStockLimitUiEntry> _containerStockLimitUiEntries = new System.Collections.Generic.List<ContainerStockLimitUiEntry>();
        private string _containerStockLimitUiContainerId = string.Empty;
        private string _containerStockLimitUiSearch = string.Empty;
        private float _containerStockLimitUiNextRefresh;
        // v0.5.131: fast per-item editor resolves hovered slot directly from InventoryElement RectTransforms.
        // Air-freight chests combine FieldDepot + Import + Export semantics in the same per-item editor.
        // It deliberately does not enumerate ObjectDB/search candidates while the player types.
        private bool _containerStockQuickEditorOpen;
        private Rect _containerStockQuickEditorRect = new Rect(690f, 220f, 430f, 330f);
        private Container _containerStockQuickEditorContainer;
        private string _containerStockQuickPrefab = string.Empty;
        private string _containerStockQuickDisplayName = string.Empty;
        private string _containerStockQuickKeepText = "0";
        private string _containerStockQuickMinText = "0";
        private string _containerStockQuickMaxText = "100";
        private string _containerStockQuickMessage = string.Empty;
        private readonly System.Collections.Generic.Dictionary<string, string> _fieldDepotDisplayNameCache = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Generic.HashSet<string> _fieldDepotSeedResourceSet = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Generic.List<string> _fieldDepotKnownSearchPrefabs = new System.Collections.Generic.List<string>();
        private string _fieldDepotKnownSearchQuery = string.Empty;
        // v0.5.346: ObjectDB search is indexed incrementally while the relevant UI is open.
        // Typing therefore performs only cheap string comparisons and never localization/reflection/ObjectDB lookups per key stroke.
        private readonly System.Collections.Generic.List<DepotItemSearchEntry> _depotItemSearchIndex = new System.Collections.Generic.List<DepotItemSearchEntry>();
        private readonly System.Collections.Generic.Dictionary<string, DepotItemSearchEntry> _depotItemSearchByPrefab = new System.Collections.Generic.Dictionary<string, DepotItemSearchEntry>(StringComparer.OrdinalIgnoreCase);
        private int _depotItemSearchDbInstanceId;
        private int _depotItemSearchSourceCount = -1;
        private int _depotItemSearchNextSourceIndex;
        private int _depotItemSearchVersion;
        private int _fieldDepotKnownSearchVersion = -1;
        private string _fieldDepotStockUiDepotId = string.Empty;
        private string _fieldDepotStockUiSearch = string.Empty;
        private float _fieldDepotStockUiNextRefresh;
        private float _fieldDepotSeedRuleNextRefresh;
        private float _nextWorkFlagAssignedCountRefresh;
        private int _workFlagAssignedCount;
        private readonly System.Collections.Generic.List<Character> _workFlagAssignedWorkers = new System.Collections.Generic.List<Character>();
        private readonly System.Collections.Generic.List<Character> _loadedWorkerScratch = new System.Collections.Generic.List<Character>();
        // v0.5.386: diagnostic data is built only when the player presses the Workzone test button; blocker reasons are read from persisted decisions.
        // No audit Update/Tick loop exists; keeping the last snapshot here only avoids recomputing while scrolling.
        private WorkzoneJobAuditRuntime.Snapshot _workFlagAuditSnapshot;
        private string _workFlagAuditAreaId = string.Empty;
        private bool _workFlagAuditWorkerDetailsExpanded;
        private WorkFlagInteractable _pendingWorkFlagAssignment;
        private GreydwarfHomeNestInteractable _pendingGreydwarfHomeAssignment;
        private TrollHomeCampInteractable _pendingTrollHomeAssignment;
        private StoneGolemHomeInteractable _pendingStoneGolemHomeAssignment;
        private PendingGenericHomeSelection _pendingGenericHomeAssignment;
        private int _pendingWorkerSelectionStartedFrame = -1;
        private int _suppressNativeInteractFrame = -1;

        private sealed class PendingGenericHomeSelection
        {
            internal ZNetView View;
            internal Vector3 Position;
            internal string AnchorKind;
            internal string DisplayName;
        }

        private Harmony _harmony;
        private float _nextOfferingAllowed;
        private float _nextDevReloadAllowed;
        private float _nextRuntimeConfigSync;
        private float _nextPieceRegistrationTick;

        private GUIStyle _boxStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _smartPopupWindowStyle;
        private GUIStyle _smartPopupHeadingStyle;
        private GUIStyle _smartPopupLabelStyle;
        private GUIStyle _smartPopupGoodStyle;
        private GUIStyle _smartPopupWarnStyle;
        private GUIStyle _smartPopupCriticalStyle;
        private GUIStyle _smartPopupTableHeaderStyle;
        private GUIStyle _smartPopupTableCellStyle;
        private GUIStyle _smartPopupTableValueStyle;
        private GUIStyle _smartPopupTableGoodValueStyle;
        private GUIStyle _smartPopupTableBadValueStyle;
        private GUIStyle _smartPopupButtonStyle;
        private Texture2D _smartPopupBackground;
        private Texture2D _smartPopupButtonBackground;
        private bool _menuCursorCaptured;
        private bool _savedCursorVisible;
        private CursorLockMode _savedCursorLock;
        private Vector2 _workFlagScroll;
        private bool _backgroundMonitorOpen;
        private bool _backgroundMonitorOpenedForWorld;
        private Rect _backgroundMonitorRect = new Rect(0f, 70f, 520f, 420f);
        private Vector2 _backgroundMonitorScroll;
        private readonly System.Collections.Generic.List<BackgroundWorkerScheduler.TransportMapSnapshot> _backgroundMonitorTransports = new System.Collections.Generic.List<BackgroundWorkerScheduler.TransportMapSnapshot>();
        private readonly System.Collections.Generic.List<string> _backgroundMonitorAirHubs = new System.Collections.Generic.List<string>();
        private readonly System.Collections.Generic.List<string> _backgroundMonitorEvents = new System.Collections.Generic.List<string>();
        private readonly System.Collections.Generic.List<string> _performanceMonitorLines = new System.Collections.Generic.List<string>();
        private float _nextBackgroundMonitorDataRefresh;
        private bool _outpostManagerOpen;
        private Vector3 _outpostManagerAnchorPosition;
        private Rect _outpostManagerRect = new Rect(430f, 70f, 900f, 760f);
        private Vector2 _outpostManagerScroll;
        private float _nextOutpostManagerRefresh;
        private bool _outpostManagerMapPreviewOpen;
        private float _outpostManagerMapPreviewReadyAt;
        private bool _workFlagOpenedFromOutpostManager;
        private readonly System.Collections.Generic.List<WorkAreaSnapshot> _outpostManagerFlags = new System.Collections.Generic.List<WorkAreaSnapshot>();
        private readonly System.Collections.Generic.List<Character> _outpostManagerWorkers = new System.Collections.Generic.List<Character>();
        // v0.5.225: the cartography manager must also see unloaded/background Workers. The scheduler
        // already owns their persistent ZDOs, so keep a cheap once-per-second snapshot instead of
        // scanning the world or requiring the outpost to be loaded.
        private readonly System.Collections.Generic.List<ZDO> _outpostManagerPersistentWorkers = new System.Collections.Generic.List<ZDO>();
        private readonly System.Collections.Generic.Dictionary<ZDOID, Character> _outpostManagerLoadedWorkerById = new System.Collections.Generic.Dictionary<ZDOID, Character>();
        private readonly System.Collections.Generic.Dictionary<string, string> _outpostManagerNameEdits = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
        private readonly System.Collections.Generic.Dictionary<string, float> _outpostManagerRadiusEdits = new System.Collections.Generic.Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly WorkerTaskFlags[] OutpostRemoteTaskOrder =
        {
            WorkerTaskFlags.Harvest,
            WorkerTaskFlags.Gather,
            WorkerTaskFlags.Transport,
            WorkerTaskFlags.Provision,
            WorkerTaskFlags.Sort,
            WorkerTaskFlags.Garden,
            WorkerTaskFlags.Forestry,
            WorkerTaskFlags.Bake,
            WorkerTaskFlags.Hunt,
            WorkerTaskFlags.Guard,
            WorkerTaskFlags.FellTrees,
            WorkerTaskFlags.Stonework,
            WorkerTaskFlags.KeepFires,
            WorkerTaskFlags.Butcher,
            WorkerTaskFlags.Smith,
            WorkerTaskFlags.Repair,
            WorkerTaskFlags.Fish,
            WorkerTaskFlags.Mine
        };

        internal Action<string> DebugLogPublic => DebugLog;

        private void Awake()
        {
            Log = Logger;
            Instance = this;
            _enabled = Config.Bind("General", "Enabled", true, "Enable Valheim_Workers.");
            _debugMode = Config.Bind("General", "DebugMode", true, "Enable Workers debug logging.");
            _showDebugOverlay = Config.Bind("General", "ShowDebugOverlay", false, "Show the Workers MVP debug overlay. This diagnostic overlay is disabled by default to avoid unnecessary per-frame UI work.");
            _backgroundMonitorOnStart = Config.Bind("Debug", "BackgroundMonitorOnStart", false, "Open the compact Workers background monitor automatically when entering a world. F10 still toggles it on demand.");
            _backgroundMonitorKey = Config.Bind("Debug", "BackgroundMonitorToggleKey", KeyCode.F10, "Toggle the compact Workers background monitor.");
            _workRadius = Config.Bind("Workers", "DefaultWorkRadius", 40f, "Default work radius in meters.");
            _recruitSearchRadius = Config.Bind("Recruitment", "TestRecruitRadius", 8f, "Radius used by the temporary MVP recruitment hotkey.");
            _recruitKey = Config.Bind("Recruitment", "TestRecruitKey", KeyCode.F7, "Temporary MVP key: recruit nearest eligible creature.");
            _showWorkerMapMarkers = Config.Bind("Markers", "ShowWorkerMapMarkers", true, "Show transient map markers for recruited workers, including projected background transport positions.");
            _showHomeMapMarkers = Config.Bind("Markers", "ShowHomeMapMarkers", true, "Show transient map markers for all persistent workers' home positions across the complete map.");
            _showMapMarkerNames = Config.Bind("Markers", "ShowMapMarkerNames", true, "Include species/role names on Workers map markers.");
            _authorityHandoffRadius = Config.Bind("Multiplayer", "AuthorityHandoffRadius", 80f, "Assigned player must be within this distance of a remotely-owned Worker before Workers requests a network ownership handoff.");
            _spawnerProbeRadius = Config.Bind("Recruitment", "GreydwarfSpawnerProbeRadius", 30f, "Radius used to discover nearby Greydwarf nests for recruitment.");
            _spawnerOfferingRadius = Config.Bind("Recruitment", "GreydwarfOfferingRadius", 5f, "Maximum distance from a Greydwarf nest for the offering interaction.");
            _homeAssignmentRadius = Config.Bind("Homes", "GreydwarfHomeAssignmentRadius", 25f, "Maximum distance from a Greydwarf Wurzelnest when assigning one owned Greydwarf-family worker with E.");
            _trollHomeAssignmentRadius = Config.Bind("Homes", "TrollHomeAssignmentRadius", 35f, "Maximum distance from a Troll-Lehnplatz when assigning one owned Troll worker with E.");
            _stoneGolemHomeAssignmentRadius = Config.Bind("Homes", "StoneGolemHomeAssignmentRadius", 35f, "Maximum distance from a Golem-Steinlager when assigning one owned Stone Golem worker with E.");
            _neckHomeAssignmentRadius = Config.Bind("Homes", "NeckHomeAssignmentRadius", 25f, "Maximum distance from an Ufernest when assigning one owned Neck/Fisher worker with E.");
            _loxHomeAssignmentRadius = Config.Bind("Homes", "LoxHomeAssignmentRadius", 40f, "Maximum distance from a Lox-Ruheplatz when assigning one owned Lox worker with E.");
            _drakeHomeAssignmentRadius = Config.Bind("Homes", "DrakeHomeAssignmentRadius", 50f, "Maximum distance from a Drake-Landeplatz when assigning one owned Drake Air Hauler with E.");
            _workerAssignmentAimRadius = Config.Bind("Homes", "WorkerAssignmentAimRadius", 120f, "Maximum player-to-worker aim distance while a Workzone/Home assignment is pending. Aim at the owned Worker and press E; this bypasses Valheim's short normal interaction range only for assignment selection.");
            _creatorMode = Config.Bind("Developer", "CreatorMode", false, "Enable explicit F8 creator/debug spawn controls. This is not part of normal recruitment progression.");
            _devReloadKey = Config.Bind("Developer", "DevReloadKey", KeyCode.F12, "Reload Workers config and transient runtime caches without restarting Valheim. C# DLL code changes still require a game restart.");
            _workFlagDefaultRadius = Config.Bind("WorkAreas", "DefaultFlagRadius", 30f, "Default radius in meters for a newly configured Workers work flag.");
            _containerAccessRadius = Config.Bind("Storage", "ContainerAccessRadius", 3f, "Shared horizontal interaction radius for worker-accessed containers and machines. Workers only need a reachable ground point inside this cylinder; target height is ignored.");
            _logisticsNetworkRadius = Config.Bind("Storage", "LogisticsNetworkRadius", 250f, "LOCAL normal-storage/sorter search radius only. Hauler transport between assigned FieldDepots and Base Import/Export is map-wide and has no distance limit.");
            _navigatorStuckSeconds = Config.Bind("Navigation", "StuckSeconds", 5.5f, "Seconds without meaningful progress before the cached directional ground-route planner is allowed to recalculate a path.");
            _navigatorRecoveryDistance = Config.Bind("Navigation", "RecoveryStepDistance", 2.5f, "Legacy v1 navigator setting retained for config compatibility; Navigator v2 no longer performs periodic side-steps.");
            if (_navigatorStuckSeconds.Value < 4.5f) _navigatorStuckSeconds.Value = 5.5f;
            _haulerMinimumLoadPercent = Config.Bind("Storage", "HaulerMinimumLoadPercent", 75f, "Preferred percentage of a Hauler's carry capacity for inbound FieldDepot batches. This is a preference, not a hard block; partial loads still move when no preferred batch exists.");
            _rangedShootRange = Config.Bind("Combat", "HunterGuardShootRange", 30f, "Preferred ranged engagement distance for Hunter and Guard bow workers. Default 30 m; adjustable from SmartCore > Workers.");
            _defaultWorkerStance = Config.Bind("Combat", "DefaultWorkerStance", WorkerStance.Defensive, "Default combat stance assigned to newly recruited or creator-spawned Workers. Existing Workers keep their own stance until explicitly changed or the SmartCore apply-all button is used.");
            WorkerCombatStanceRuntime.ConfigureDefaultStance(_defaultWorkerStance.Value);
            _wolfPatrolRadius = Config.Bind("Animals", "WolfPatrolRadius", 18f, "Radius for the optional patrol command on normal tamed Wolves. Shift+E on a tamed Wolf starts/stops patrol; normal E remains vanilla Follow/Stay.");
            WolfPatrolRuntime.ConfigureRadius(_wolfPatrolRadius.Value);
            _loxClearNaturalObstacles = Config.Bind("Navigation", "LoxClearNaturalObstacles", true, "Heavy Hauler Lox may use its native attack to clear natural destructible obstacles (trees, logs, stumps, rocks, boulders) blocking travel after being genuinely stuck. Player-built pieces and containers are never recovery targets.");
            _loxObstacleClearSeconds = Config.Bind("Navigation", "LoxObstacleClearSeconds", 6f, "Seconds of negligible travel progress before a Heavy Hauler may clear a directly blocking natural destructible obstacle.");
            if (_loxObstacleClearSeconds.Value < 3f) _loxObstacleClearSeconds.Value = 3f;
            _autoRecoverStuckWorkers = Config.Bind("Recovery", "AutoRecoverStuckWorkers", true, "Automatically rescue owned loaded Workers that are swimming/in water and make no meaningful movement for the configured timeout. Recovery returns them to their assigned HomeAnchor without deleting cargo.");
            _waterStuckRecoverySeconds = Config.Bind("Recovery", "WaterStuckRecoverySeconds", 18f, "Seconds a Worker may remain effectively stationary while swimming before it is safely returned to its HomeAnchor.");
            if (_waterStuckRecoverySeconds.Value < 8f) _waterStuckRecoverySeconds.Value = 8f;
            _workerAi = new WorkerAiController(DebugLog);
            _ownership = new WorkerOwnershipRuntime(DebugLog);
            _mapMarkers = new MapMarkerManager(DebugLog);
            _spawnerProbe = new GreydwarfSpawnerProbeRuntime(DebugLog);
            _greydwarfRecruitment = new GreydwarfRecruitmentRuntime(DebugLog);
            _greydwarfHomeSeedReward = new GreydwarfHomeSeedRewardRuntime(DebugLog);
            RestStationUnlockRuntime.Initialize(Config, DebugLog, Logger.LogInfo);
            _greydwarfHomePiece = new GreydwarfHomePieceRuntime(DebugLog, Logger.LogInfo);
            _greydwarfHomePiece.Begin();
            _draugrRestPiece = new DraugrRestPieceRuntime(DebugLog, Logger.LogInfo);
            _draugrRestPiece.Begin();
            _draugrHonorShrinePiece = new DraugrHonorShrinePieceRuntime(DebugLog, Logger.LogInfo);
            _draugrHonorShrinePiece.Begin();
            _trollHomePiece = new TrollHomePieceRuntime(DebugLog, Logger.LogInfo);
            _trollHomePiece.Begin();
            _stoneGolemHomePiece = new StoneGolemHomePieceRuntime(DebugLog, Logger.LogInfo);
            _stoneGolemHomePiece.Begin();
            _surtlingHomePiece = new SurtlingHomePieceRuntime(DebugLog, Logger.LogInfo);
            _surtlingHomePiece.Begin();
            _surtlingSignalFirePiece = new SurtlingSignalFirePieceRuntime(DebugLog, Logger.LogInfo);
            _surtlingSignalFirePiece.Begin();
            _neckHomePiece = new NeckHomePieceRuntime(DebugLog, Logger.LogInfo);
            _neckHomePiece.Begin();
            _neckTrapPiece = new NeckTrapPieceRuntime(DebugLog, Logger.LogInfo);
            _neckTrapPiece.Begin();
            _loxHomePiece = new LoxHomePieceRuntime(DebugLog, Logger.LogInfo);
            _loxHomePiece.Begin();
            _drakeHomePiece = new DrakeHomePieceRuntime(DebugLog, Logger.LogInfo);
            _drakeHomePiece.Begin();
            _drakeEggNestPiece = new DrakeEggNestPieceRuntime(DebugLog, Logger.LogInfo);
            _drakeEggNestPiece.Begin();
            _fulingHomePiece = new FulingHomePieceRuntime(DebugLog, Logger.LogInfo);
            _fulingHomePiece.Begin();
            _fulingRestChairPiece = new FulingRestChairPieceRuntime(DebugLog, Logger.LogInfo);
            _fulingRestChairPiece.Begin();
            _normalFieldDepotPiece = new NormalFieldDepotRuntime(DebugLog, Logger.LogInfo);
            _normalFieldDepotPiece.Begin();
            _advancedFieldDepotPiece = new AdvancedFieldDepotRuntime(DebugLog, Logger.LogInfo);
            _advancedFieldDepotPiece.Begin();
            _hunterStandPiece = new HunterStandPieceRuntime(DebugLog, Logger.LogInfo);
            _hunterStandPiece.Begin();
            _livestockFeedSignPiece = new LivestockFeedSignPieceRuntime(DebugLog, Logger.LogInfo);
            _livestockFeedSignPiece.Begin();
            _productionDisplayPiece = new ProductionDisplayPieceRuntime(DebugLog, Logger.LogInfo);
            _productionDisplayPiece.Begin();
            // SmartStorage owns the actual feeding prefabs. This compatibility bridge starts late-safe
            // and only re-exposes the existing original prefabs if SmartStorage has them registered
            // but the live Hammer table lost the entries.
            _smartStorageFeedingBridge = new SmartStorageFeedingPieceBridge(DebugLog, Logger.LogInfo);
            _smartStorageFeedingBridge.Begin();
            _creatorSpawn = new CreatorSpawnRuntime(DebugLog);
            _workFlagPiece = new WorkFlagPieceRuntime(DebugLog, Logger.LogInfo);
            _workFlagPiece.Begin();
            _workZoneBirdView = new WorkZoneBirdViewRuntime(DebugLog);
            _livestockPenBirdView = new LivestockPenBirdViewRuntime(DebugLog);
            _workerLocateCamera = new WorkerLocateCameraRuntime(DebugLog);
            _workerFoodItem = new WorkerFoodItemRuntime(DebugLog, Logger.LogInfo);
            _workerFoodItem.Begin();
            _workersWildFlora = new WorkersWildFloraRuntime(DebugLog, Logger.LogInfo);
            _workersWildFlora.Begin();
            _workersLore = new WorkersLoreRuntime(DebugLog, Logger.LogInfo);
            _workersLore.Begin();
            _gardenPlanning = new GardenPlanningRuntime(DebugLog, SetStatus);
            _reachabilityHeatmap = new WorkerReachabilityHeatmapRuntime(DebugLog, SetStatus);
            WorkerCollisionRuntime.Initialize(DebugLog);
            WorkerNavigationRuntime.Initialize(DebugLog);
            BoatPassengerRuntime.Initialize(DebugLog);
            HunterWorkRuntime.ConfigureShootRange(_rangedShootRange.Value);
            WorkerStorageRuntime.ConfigureContainerAccessRadius(_containerAccessRadius.Value);
            WorkerStorageRuntime.ConfigureLogisticsNetworkRadius(_logisticsNetworkRadius.Value);
            WorkerStorageRuntime.ConfigureHaulerMinimumLoadPercent(_haulerMinimumLoadPercent.Value);
            WorkerNavigationRuntime.ConfigureRecovery(_navigatorStuckSeconds.Value, _navigatorRecoveryDistance.Value);
            LoxObstacleRecoveryRuntime.Configure(_loxClearNaturalObstacles.Value, _loxObstacleClearSeconds.Value);
            WorkerRespawnRuntime.Initialize(DebugLog);
            WorkerRespawnRuntime.Configure(_autoRecoverStuckWorkers.Value, _waterStuckRecoverySeconds.Value);
            BackgroundWorkerScheduler.Tick();
            WorkerRestRuntime.Initialize(DebugLog);
            WorkerRestAnimationRuntime.Initialize(DebugLog);
            SmartReplantBridge.Initialize(DebugLog);
            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            SmartReplantPlanningCompatibility.TryInstall(_harmony, DebugLog);

            // Workers intentionally does NOT listen for F8. SmartCore owns that key.
            _smartCoreAttemptDeadline = Time.unscaledTime + 12f;
            TryRegisterSmartCore();

            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded. Player-bubble physical simulation + background worker authority active; Navigator v5 preserved unchanged.");
        }

        internal bool HasOpenWorkersPopup()
        {
            return _openWorkFlag != null || _openProductionStation != null || _openWorkerContainer != null || _openWorkerMenu != null || _openPersistentWorkerMenuZdo != null || _openWorkerInventory != null || _openLivestockFeedSign != null || _outpostManagerOpen || (_workZoneBirdView?.Active ?? false) || (_livestockPenBirdView?.Active ?? false) || (_reachabilityHeatmap?.CapturingInput ?? false) || (_workerLocateCamera?.Active ?? false);
        }

        private void ApplyWorkersPopupInputLock()
        {
            bool active = _enabled != null && _enabled.Value && HasOpenWorkersPopup();
            WorkersModalUiRuntime.SetActive(active);
            if (!active) return;

            CaptureMenuCursor();
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            Player player = Player.m_localPlayer;
            if (player != null)
                player.SetMoveDir(Vector3.zero);
        }

        private void LateUpdate()
        {
            long perfLateAir = WorkersPerformanceProfiler.Begin();
            // Drake AirHaulers must keep travelling even when Valheim throttles/sleeps the native
            // MonsterAI because no player is close. This runs after normal UpdateAI; the navigation
            // runtime skips Drakes that already received their native Postfix this frame.
            if (_enabled != null && _enabled.Value && Time.unscaledTime >= _nextIndependentAirTick)
            {
                float now = Time.unscaledTime;
                float independentDt = _lastIndependentAirTick > 0f
                    ? Mathf.Clamp(now - _lastIndependentAirTick, 0.001f, 0.10f)
                    : Mathf.Max(Time.deltaTime, 0.001f);
                _lastIndependentAirTick = now;
                _nextIndependentAirTick = now + IndependentAirTickSeconds;
                long perfIndependentAir = WorkersPerformanceProfiler.Begin();
                WorkerNavigationRuntime.TickIndependentAirHaulers(independentDt);
                WorkersPerformanceProfiler.End(WorkersPerfBucket.IndependentAir, perfIndependentAir);
            }

            // Boat passengers are locked after the vessel has completed its normal frame movement.
            BoatPassengerRuntime.Tick();

            // Final-frame rest lock. Keep authored rest furniture poses stable after Valheim has
            // finished normal movement/physics for this frame. This deliberately avoids patching
            // Character.FixedUpdate because that method is not present on current Valheim Character.
            WorkerRestPhysicsLockRuntime.TickLate();

            // Final-frame carry lock: WASD steers the Drake and must not make the Player controller
            // detach or move the rider independently.

            // GameCamera may update after our normal Update and try to recapture the mouse.
            // Re-assert the modal UI state late in the frame so Workers windows always remain usable.
            ApplyWorkersPopupInputLock();
            WorkersPerformanceProfiler.End(WorkersPerfBucket.LateAir, perfLateAir);
        }


        private void Update()
        {
            CartographyRemoteRuntime.TickRegistration();
            SmartStorageReflectionCacheBridge.TryInstall(_harmony);
            // v0.5.431: own Shift+E at the raw input stage, using Valheim's actual hovered object.
            // This remains reliable even when SmartStorage replaces the chest hover/interact chain.
            WorkerContainerDirectInputRuntime.Tick();
            long perfMainUpdate = WorkersPerformanceProfiler.Begin();
            if (_backgroundMonitorKey != null && Input.GetKeyDown(_backgroundMonitorKey.Value))
                _backgroundMonitorOpen = !_backgroundMonitorOpen;

            if (Player.m_localPlayer == null)
            {
                _backgroundMonitorOpenedForWorld = false;
            }
            else if (!_backgroundMonitorOpenedForWorld)
            {
                _backgroundMonitorOpenedForWorld = true;
                _backgroundMonitorOpen = _backgroundMonitorOnStart != null && _backgroundMonitorOnStart.Value;
                _backgroundMonitorRect.x = Mathf.Max(12f, Screen.width - _backgroundMonitorRect.width - 20f);
                _backgroundMonitorRect.y = 70f;
            }

            if (_devReloadKey != null && Input.GetKeyDown(_devReloadKey.Value))
            {
                PerformDevReload();
                WorkersPerformanceProfiler.End(WorkersPerfBucket.MainUpdate, perfMainUpdate);
                WorkersPerformanceProfiler.TickSnapshot();
                return;
            }

            // These values are runtime configuration, not frame-state. Reapplying the same values
            // every rendered frame caused needless ConfigEntry reads and static writes. A 0.5 s sync
            // keeps live config edits responsive without putting configuration plumbing on the hot path.
            if (Time.unscaledTime >= _nextRuntimeConfigSync)
            {
                _nextRuntimeConfigSync = Time.unscaledTime + 0.5f;
                WorkerStorageRuntime.ConfigureContainerAccessRadius(_containerAccessRadius.Value);
                WorkerStorageRuntime.ConfigureLogisticsNetworkRadius(_logisticsNetworkRadius.Value);
                WorkerStorageRuntime.ConfigureHaulerMinimumLoadPercent(_haulerMinimumLoadPercent.Value);
                HunterWorkRuntime.ConfigureShootRange(_rangedShootRange.Value);
                WorkerNavigationRuntime.ConfigureRecovery(_navigatorStuckSeconds.Value, _navigatorRecoveryDistance.Value);
                LoxObstacleRecoveryRuntime.Configure(_loxClearNaturalObstacles.Value, _loxObstacleClearSeconds.Value);
                WorkerRespawnRuntime.Configure(_autoRecoverStuckWorkers.Value, _waterStuckRecoverySeconds.Value);
            }
            long perfPlanning = WorkersPerformanceProfiler.Begin();
            _workZoneBirdView?.Tick();
            _livestockPenBirdView?.Tick();
            _workerLocateCamera?.Tick();
            _gardenPlanning?.Tick();
            _reachabilityHeatmap?.Tick(_workFlagDefaultRadius.Value);
            WorkersPerformanceProfiler.End(WorkersPerfBucket.Planning, perfPlanning);
            if (!_enabled.Value)
            {
                WorkersModalUiRuntime.SetActive(false);
                _mapMarkers?.ClearAll();
                _workZoneBirdView?.Shutdown();
                _livestockPenBirdView?.Shutdown();
                _workerLocateCamera?.Shutdown();
                _gardenPlanning?.Shutdown();
                _reachabilityHeatmap?.Shutdown();
                CloseWorkFlagMenu();
                CloseProductionStationMenu();
                CloseWorkerContainerMenu();
                CloseWorkerMenu();
                CloseWorkerInventory();
                CloseLivestockFeedMenu();
                CloseOutpostManager();
                RestoreMenuCursor();
                WorkersPerformanceProfiler.End(WorkersPerfBucket.MainUpdate, perfMainUpdate);
                WorkersPerformanceProfiler.TickSnapshot();
                return;
            }

            // v0.5.97: Do not globally react to Shift+E. Shift is also Valheim's sprint modifier,
            // so a player sprinting while interacting with a door must never open a nearby Worker
            // rest-station menu. Worker menus are now opened only through the exact GameObject that
            // Valheim actually routes to Player.Interact / our collider-local interaction proxies.

            // SmartCore can finish initializing after Workers (especially with a large mod stack).
            // Never give up after an arbitrary startup deadline: keep retrying at a low frequency
            // until the Workers page has actually registered. This is UI registration only and
            // does not touch worker simulation, navigation, or piece registration.
            if (!SmartCoreBridge.IsRegistered && Time.unscaledTime >= _nextSmartCoreAttempt)
            {
                _nextSmartCoreAttempt = Time.unscaledTime + 2.0f;
                TryRegisterSmartCore();
            }

            // SmartReplant is optional. The soft dependency normally gives us the correct load order,
            // but retrying this tiny reflection bind makes manual/plugin reload order robust.
            if (!SmartReplantPlanningCompatibility.Installed)
                SmartReplantPlanningCompatibility.TryInstall(_harmony, DebugLog);

            if (Input.GetKeyDown(_recruitKey.Value))
                TryRecruitNearest();

            long perfPieces = WorkersPerformanceProfiler.Begin();

            // v0.5.457: these are startup/late-Jotunn registration retries, not frame-state.
            // Each runtime already retries at ~1.5-2 s internally. Calling all of them every
            // rendered frame only added dozens of no-op method calls after registration.
            // A shared 0.25 s cadence keeps startup behavior responsive while removing the
            // permanent per-frame fan-out.
            if (Time.unscaledTime >= _nextPieceRegistrationTick)
            {
                _nextPieceRegistrationTick = Time.unscaledTime + 0.25f;

                RestStationUnlockRuntime.Tick();
                _greydwarfHomePiece?.Tick();
                _draugrRestPiece?.Tick();
                _draugrHonorShrinePiece?.Tick();
                _trollHomePiece?.Tick();
                _stoneGolemHomePiece?.Tick();
                _surtlingHomePiece?.Tick();
                _surtlingSignalFirePiece?.Tick();
                _neckHomePiece?.Tick();
                _neckTrapPiece?.Tick();
                _loxHomePiece?.Tick();
                _drakeHomePiece?.Tick();
                _drakeEggNestPiece?.Tick();
                _fulingHomePiece?.Tick();
                _fulingRestChairPiece?.Tick();
                _normalFieldDepotPiece?.Tick();
                _advancedFieldDepotPiece?.Tick();
                _hunterStandPiece?.Tick();
                _livestockFeedSignPiece?.Tick();
                _smartStorageFeedingBridge?.Tick();
                _workFlagPiece?.Tick();

                _productionDisplayPiece?.Tick();
                _workersWildFlora?.Tick();
                _workersLore?.Tick();
            }

            // Actual gameplay runtimes keep their own sub-second cadence and stay outside
            // the registration throttle.
            LivestockFeedingRuntime.TickAutomaticFeedRelease();
            FulingVillageDiplomacyRuntime.Tick();
            WorkersPerformanceProfiler.End(WorkersPerfBucket.RuntimePieces, perfPieces);

            long perfOwnership = WorkersPerformanceProfiler.Begin();
            _ownership?.Tick(_authorityHandoffRadius.Value);
            WorkersPerformanceProfiler.End(WorkersPerfBucket.Ownership, perfOwnership);

            long perfWorkerAi = WorkersPerformanceProfiler.Begin();
            int gc0BeforeWorkerAi = GC.CollectionCount(0);
            int gc1BeforeWorkerAi = GC.CollectionCount(1);
            int gc2BeforeWorkerAi = GC.CollectionCount(2);
            _workerAi?.Tick();
            double workerAiCallerMs = WorkersPerformanceProfiler.ElapsedMilliseconds(perfWorkerAi);
            double workerAiInternalMs = _workerAi != null ? _workerAi.LastInternalTickMs : 0.0;
            if (workerAiCallerMs >= 20.0 && workerAiInternalMs < 20.0)
            {
                int gc0AfterWorkerAi = GC.CollectionCount(0);
                int gc1AfterWorkerAi = GC.CollectionCount(1);
                int gc2AfterWorkerAi = GC.CollectionCount(2);
                BackgroundMonitorRuntime.Note($"PERF WORKER CALL GAP: caller={workerAiCallerMs:0.0}ms internal={workerAiInternalMs:0.0}ms gc0={gc0AfterWorkerAi-gc0BeforeWorkerAi} gc1={gc1AfterWorkerAi-gc1BeforeWorkerAi} gc2={gc2AfterWorkerAi-gc2BeforeWorkerAi}");
            }
            WorkersPerformanceProfiler.End(WorkersPerfBucket.WorkerAI, perfWorkerAi);

            long perfBackground = WorkersPerformanceProfiler.Begin();
            BackgroundWorkerScheduler.Tick();
            WorkersPerformanceProfiler.End(WorkersPerfBucket.Background, perfBackground);

            long perfMarkers = WorkersPerformanceProfiler.Begin();
            _mapMarkers?.Tick(_showWorkerMapMarkers.Value, _showHomeMapMarkers.Value, _showMapMarkerNames.Value);
            WorkersPerformanceProfiler.End(WorkersPerfBucket.MapMarkers, perfMarkers);

            // When the workzone map preview was opened from the Cartography table, returning from
            // the large map (including vanilla ESC) should restore the outpost manager instead of
            // dropping the player back into the world with the management UI gone.
            if (_outpostManagerMapPreviewOpen && _mapMarkers != null &&
                Time.unscaledTime >= _outpostManagerMapPreviewReadyAt && !_mapMarkers.IsOutpostAreaMapVisible())
            {
                _mapMarkers.ClearOutpostAreaPins();
                ResumeOutpostManagerFromMapPreview();
            }

            long perfSpawner = WorkersPerformanceProfiler.Begin();
            _spawnerProbe?.Tick(_spawnerProbeRadius.Value);
            WorkersPerformanceProfiler.End(WorkersPerfBucket.SpawnerProbe, perfSpawner);
            // Pacified nests are suppressed by SpawnArea.Awake/UpdateSpawn Harmony guards. Do not
            // re-resolve the same SpawnArea ZNetView/ZDO every rendered frame here.
            if (_workerAi != null && _workerAi.LastObservedWorker != null)
                _lastWorker = _workerAi.LastObservedWorker;

            ApplyWorkersPopupInputLock();

            if (_openWorkFlag != null)
            {
                bool tooFar = Player.m_localPlayer != null && (_workFlagOpenedFromOutpostManager
                    ? Vector3.Distance(Player.m_localPlayer.transform.position, _outpostManagerAnchorPosition) > 10f
                    : Vector3.Distance(Player.m_localPlayer.transform.position, _openWorkFlag.transform.position) > 10f);
                if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || Player.m_localPlayer == null || tooFar)
                    CloseWorkFlagMenu();
            }

            if (_openProductionStation != null)
            {
                bool invalid = !ProductionStationRuntime.TryResolve(_openProductionStation, out ProductionStationRef openStation);
                bool tooFar = !invalid && Player.m_localPlayer != null && Vector3.Distance(Player.m_localPlayer.transform.position, openStation.Position) > 10f;
                if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || Player.m_localPlayer == null || invalid || tooFar)
                    CloseProductionStationMenu();
            }

            if (_openWorkerContainer != null)
            {
                if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || Player.m_localPlayer == null ||
                    Vector3.Distance(Player.m_localPlayer.transform.position, _openWorkerContainer.transform.position) > 10f)
                    CloseWorkerContainerMenu();
            }

            if (_openWorkerMenu != null)
            {
                if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || Player.m_localPlayer == null || _openWorkerMenu.IsDead())
                    CloseWorkerMenu();
            }
            else if (_openPersistentWorkerMenuZdo != null)
            {
                if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || Player.m_localPlayer == null ||
                    Vector3.Distance(Player.m_localPlayer.transform.position, _openPersistentWorkerMenuHomePosition) > 12f)
                    CloseWorkerMenu();
            }

            if (_openWorkerInventory != null)
            {
                if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || Player.m_localPlayer == null || _openWorkerInventory.IsDead() ||
                    Vector3.Distance(Player.m_localPlayer.transform.position, _openWorkerInventory.transform.position) > 12f)
                    CloseWorkerInventory();
            }

            if (_openLivestockFeedSign != null)
            {
                if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || Player.m_localPlayer == null ||
                    Vector3.Distance(Player.m_localPlayer.transform.position, _openLivestockFeedSign.transform.position) > 10f)
                    CloseLivestockFeedMenu();
            }

            if (_outpostManagerOpen)
            {
                if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || Player.m_localPlayer == null ||
                    Vector3.Distance(Player.m_localPlayer.transform.position, _outpostManagerAnchorPosition) > 10f)
                    CloseOutpostManager();
            }

            if (!HasOpenWorkersPopup())
            {
                WorkersModalUiRuntime.SetActive(false);
                RestoreMenuCursor();
            }

            if (HasPendingWorkerSelection() && Input.GetKeyDown(KeyCode.E))
                TryHandleLongRangePendingWorkerSelection();

            if (Input.GetKeyDown(KeyCode.Escape) && HasPendingWorkerSelection())
                CancelPendingWorkerSelection("Arbeiter-Zuweisung abgebrochen.");

            if (_nextStatusExpiry > 0f && Time.unscaledTime >= _nextStatusExpiry)
            {
                _nextStatusExpiry = 0f;
                if (_status != "Ready") _status = "Ready";
            }

            WorkersPerformanceProfiler.End(WorkersPerfBucket.MainUpdate, perfMainUpdate);
            WorkersPerformanceProfiler.TickSnapshot();
        }

        private void TryRegisterSmartCore()
        {
            SmartCoreBridge.TryRegister("workers", "Workers", 400, DrawSmartCorePage, DebugLog, Logger.LogInfo);
        }

        private void DrawSmartCorePage()
        {
            GUILayout.Label("Valheim Workers");
            GUILayout.Space(4f);

            bool enabled = GUILayout.Toggle(_enabled.Value, "Workers enabled");
            if (enabled != _enabled.Value) _enabled.Value = enabled;

            bool debugMode = GUILayout.Toggle(_debugMode.Value, "Debug logging");
            if (debugMode != _debugMode.Value) _debugMode.Value = debugMode;

            bool showOverlay = GUILayout.Toggle(_showDebugOverlay.Value, "Show worker debug overlay");
            if (showOverlay != _showDebugOverlay.Value) _showDebugOverlay.Value = showOverlay;
            bool monitorOnStart = GUILayout.Toggle(_backgroundMonitorOnStart.Value, $"Background monitor on world start (toggle: {_backgroundMonitorKey.Value})");
            if (monitorOnStart != _backgroundMonitorOnStart.Value) _backgroundMonitorOnStart.Value = monitorOnStart;

            GUILayout.Space(8f);
            GUILayout.Label("Standard-Kampfverhalten neuer Worker");
            GUILayout.Label($"Aktuell: {WorkerCombatStanceRuntime.DisplayName(_defaultWorkerStance.Value)}");
            GUILayout.BeginHorizontal();
            DrawDefaultWorkerStanceButton(WorkerStance.Aggressive, "Aggressiv");
            DrawDefaultWorkerStanceButton(WorkerStance.Defensive, "Defensiv");
            DrawDefaultWorkerStanceButton(WorkerStance.Passive, "Nicht angreifen");
            GUILayout.EndHorizontal();
            GUILayout.Label("Gilt fuer neue Rekrutierungen UND Creator-Spawns. Bestehende Worker behalten ihre individuelle Haltung.");
            if (GUILayout.Button("Standard auf alle eigenen Worker anwenden"))
                ApplyDefaultStanceToOwnedWorkers();

            GUILayout.Space(8f);
            GUILayout.Label("Gezähmte Wölfe");
            GUILayout.Label($"Optionaler Patrouillenradius: {_wolfPatrolRadius.Value:0} m");
            float wolfPatrolRadius = GUILayout.HorizontalSlider(_wolfPatrolRadius.Value, 5f, 50f);
            wolfPatrolRadius = Mathf.Round(wolfPatrolRadius);
            if (Math.Abs(wolfPatrolRadius - _wolfPatrolRadius.Value) >= 0.5f)
            {
                _wolfPatrolRadius.Value = wolfPatrolRadius;
                WolfPatrolRuntime.ConfigureRadius(wolfPatrolRadius);
            }
            GUILayout.Label("Wolf bleibt normales Zucht-/Begleittier: Shift+E am gezähmten Wolf = Patrouille hier an/aus. Normales E = Valheim Folgen/Bleiben und beendet eine aktive Patrouille.");

            GUILayout.Space(8f);
            GUILayout.Label("Creator mode (debug / testing)");
            bool creatorMode = GUILayout.Toggle(_creatorMode.Value, "Enable creator mode");
            if (creatorMode != _creatorMode.Value) _creatorMode.Value = creatorMode;
            if (_creatorMode.Value)
            {
                GUILayout.Label("Creator spawns bypass normal recruitment progression and exist only for development/testing.");
                GUILayout.Label($"Spawn mapped Worker species (stance: {WorkerCombatStanceRuntime.DisplayName(_defaultWorkerStance.Value)})");
                if (GUILayout.Button("Spawn Greyling — Gatherer")) TryCreatorSpawnWorker("Greyling");
                if (GUILayout.Button("Spawn Greydwarf — Hauler")) TryCreatorSpawnWorker("Greydwarf");
                if (GUILayout.Button("Spawn Greydwarf Berserker — Tier 1 Lumberjack")) TryCreatorSpawnWorker("Greydwarf_Elite");
                if (GUILayout.Button("Spawn Draugr — Tier 2 Lumberjack")) TryCreatorSpawnWorker("Draugr");
                if (GUILayout.Button("Spawn Draugr Elite — Tier 2 Butcher")) TryCreatorSpawnWorker("Draugr_Elite");
                if (GUILayout.Button("Spawn Draugr Archer — Tier 2 Hunter/Guard")) TryCreatorSpawnWorker("Draugr_Ranged");
                if (GUILayout.Button("Spawn Greydwarf Shaman — Tier 1 Gardener/Forester + Wildwuchs")) TryCreatorSpawnWorker("Greydwarf_Shaman");
                if (GUILayout.Button("Spawn Troll — Tier 3 Lumberjack")) TryCreatorSpawnWorker("Troll");
                if (GUILayout.Button("Spawn Stone Golem — Tier 3 Stonemason")) TryCreatorSpawnWorker("StoneGolem");
                if (GUILayout.Button("Spawn Neck — Fisher")) TryCreatorSpawnWorker("Neck");
                if (GUILayout.Button("Spawn Drake — Tier 2 Air Hauler")) TryCreatorSpawnWorker("Drake");
                if (GUILayout.Button("Spawn Surtling — Firekeeper")) TryCreatorSpawnWorker("Surtling");
                if (GUILayout.Button("Spawn Fuling — Tier 2 Smith / Repairer")) TryCreatorSpawnWorker("Fuling");
                if (GUILayout.Button("Spawn Fuling Shaman — Tier 3 Harvest/Garden/Forestry + Cook")) TryCreatorSpawnWorker("Fuling_Shaman");
                if (GUILayout.Button("Spawn Fuling Berserker — Tier 3 Stonemason")) TryCreatorSpawnWorker("Fuling_Berserker");
                if (GUILayout.Button("Spawn Dvergr — Tier 3 Hunter/Guard")) TryCreatorSpawnWorker("Dverger");
                GUILayout.Label("Recruited Workers stay tame and can be deliberately slaughtered with the vanilla Butcher Knife (KnifeButcher).");
            }

            GUILayout.Space(8f);
            GUILayout.Label($"Dev Reload: {_devReloadKey.Value} — Config + Runtime-Caches neu laden (DLL-Code weiterhin Neustart)");

            GUILayout.Space(8f);
            GUILayout.Label("Work areas");
            GUILayout.Label($"Arbeitsfahne build piece: {_workFlagPiece?.Status ?? "not initialized"}");
            GUILayout.Label($"Draugr-Jagdplattform: {_hunterStandPiece?.Status ?? "not initialized"} | loaded={HunterStandMarker.LoadedCount}");
            GUILayout.Label($"Gehege-Marker (Trog + Legacy-Schild): {_livestockFeedSignPiece?.Status ?? "not initialized"} | loaded={LivestockFeedSignRegistry.LoadedCount}");
            GUILayout.Label($"SmartStorage Futterteile: {_smartStorageFeedingBridge?.Status ?? "not initialized"}");
            GUILayout.Label($"Loaded work flags: {WorkFlagRegistry.LoadedCount}");
            GUILayout.Label($"Default flag radius: {_workFlagDefaultRadius.Value:0} m");
            float flagRadius = GUILayout.HorizontalSlider(_workFlagDefaultRadius.Value, 10f, 80f);
            if (Math.Abs(flagRadius - _workFlagDefaultRadius.Value) >= 0.5f)
                _workFlagDefaultRadius.Value = Mathf.Round(flagRadius);
            GUILayout.Label("Work flag: no Workbench required. Place it during exploration, look at it + E -> choose job/radius and explicitly assign your loaded compatible Workers.");

            GUILayout.Space(6f);
            GUILayout.Label("Worker interaction");
            GUILayout.Label($"Interaction radius (containers + machines): {_containerAccessRadius.Value:0.0} m");
            float containerRadius = GUILayout.HorizontalSlider(_containerAccessRadius.Value, 1.5f, 6f);
            containerRadius = Mathf.Round(containerRadius * 2f) / 2f;
            if (Math.Abs(containerRadius - _containerAccessRadius.Value) >= 0.25f)
            {
                _containerAccessRadius.Value = containerRadius;
                WorkerStorageRuntime.ConfigureContainerAccessRadius(containerRadius);
            }
            GUILayout.Label("Workers choose a reachable ground point inside this interaction cylinder. It is shared by chests and machines; exact pivot contact is not required.");

            GUILayout.Space(6f);
            GUILayout.Label("Hunter / Guard ranged combat");
            GUILayout.Label($"Bow engagement range: {_rangedShootRange.Value:0} m");
            float rangedRange = GUILayout.HorizontalSlider(_rangedShootRange.Value, 15f, 50f);
            rangedRange = Mathf.Round(rangedRange);
            if (Math.Abs(rangedRange - _rangedShootRange.Value) >= 0.5f)
            {
                _rangedShootRange.Value = rangedRange;
                HunterWorkRuntime.ConfigureShootRange(rangedRange);
            }
            GUILayout.Label("Used by Hunter approach and stationary/mobile Guards. Higher platforms therefore gain a real practical range advantage.");
            GUILayout.Label("Long-distance transport reach: UNLIMITED / map-wide");
            GUILayout.Label($"Local normal-storage/sorter radius: {_logisticsNetworkRadius.Value:0} m");
            float logisticsRadius = GUILayout.HorizontalSlider(_logisticsNetworkRadius.Value, 80f, 500f);
            logisticsRadius = Mathf.Round(logisticsRadius / 10f) * 10f;
            if (Math.Abs(logisticsRadius - _logisticsNetworkRadius.Value) >= 5f)
            {
                _logisticsNetworkRadius.Value = logisticsRadius;
                WorkerStorageRuntime.ConfigureLogisticsNetworkRadius(logisticsRadius);
            }
            GUILayout.Label("Transportieren ist mapweit: zugewiesene FieldDepots sowie Base-Import/Export haben keine Entfernungsgrenze. Dieser Regler gilt nur für normale lokale Lagerkisten und Sortierer.");
            GUILayout.Label($"Hauler preferred load: {_haulerMinimumLoadPercent.Value:0}%");
            float haulerMin = GUILayout.HorizontalSlider(_haulerMinimumLoadPercent.Value, 0f, 100f);
            haulerMin = Mathf.Round(haulerMin / 5f) * 5f;
            if (Math.Abs(haulerMin - _haulerMinimumLoadPercent.Value) >= 2.5f)
            {
                _haulerMinimumLoadPercent.Value = haulerMin;
                WorkerStorageRuntime.ConfigureHaulerMinimumLoadPercent(haulerMin);
            }
            GUILayout.Label("This is only a batching preference now. Partial inbound loads still move, so a high value can no longer deadlock transport.");

            GUILayout.Space(6f);
            GUILayout.Label("Worker navigation v2");
            GUILayout.Label($"Repath only after no progress: {_navigatorStuckSeconds.Value:0.0} s");
            bool clearLoxTrees = GUILayout.Toggle(_loxClearNaturalObstacles.Value, "Legacy Lox Worker: clear natural obstacles when genuinely stuck");
            if (clearLoxTrees != _loxClearNaturalObstacles.Value) _loxClearNaturalObstacles.Value = clearLoxTrees;
            GUILayout.Label($"Legacy Lox obstacle clear after: {_loxObstacleClearSeconds.Value:0.0} s stuck");
            float loxClearSeconds = GUILayout.HorizontalSlider(_loxObstacleClearSeconds.Value, 3f, 12f);
            loxClearSeconds = Mathf.Round(loxClearSeconds * 2f) / 2f;
            if (Math.Abs(loxClearSeconds - _loxObstacleClearSeconds.Value) >= 0.25f) _loxObstacleClearSeconds.Value = loxClearSeconds;
            float stuckSeconds = GUILayout.HorizontalSlider(_navigatorStuckSeconds.Value, 4.5f, 8f);
            stuckSeconds = Mathf.Round(stuckSeconds * 2f) / 2f;
            if (Math.Abs(stuckSeconds - _navigatorStuckSeconds.Value) >= 0.25f) _navigatorStuckSeconds.Value = stuckSeconds;
            bool autoRecover = GUILayout.Toggle(_autoRecoverStuckWorkers.Value, "Auto-rescue Workers stuck in water");
            if (autoRecover != _autoRecoverStuckWorkers.Value)
            {
                _autoRecoverStuckWorkers.Value = autoRecover;
                WorkerRespawnRuntime.Configure(autoRecover, _waterStuckRecoverySeconds.Value);
            }
            GUILayout.Label($"Water rescue after: {_waterStuckRecoverySeconds.Value:0} s without progress");
            float waterRecoverySeconds = GUILayout.HorizontalSlider(_waterStuckRecoverySeconds.Value, 8f, 60f);
            waterRecoverySeconds = Mathf.Round(waterRecoverySeconds);
            if (Math.Abs(waterRecoverySeconds - _waterStuckRecoverySeconds.Value) >= 0.5f)
            {
                _waterStuckRecoverySeconds.Value = waterRecoverySeconds;
                WorkerRespawnRuntime.Configure(_autoRecoverStuckWorkers.Value, waterRecoverySeconds);
            }
            GUILayout.Label("Recovery preserves the Worker, role, HomeAnchor and cargo. It only moves a genuinely water-stuck loaded Worker back to its assigned HomeAnchor; normal pathfinding is untouched.");
            GUILayout.Label("Workers first let native Valheim movement run. Only after this timeout does a bounded bidirectional A* search build a cached coarse ground route in the rough direction of the target.");
            GUILayout.Label("The cached route is kept while the Worker moves; no left/right side-step spam and no continuous path recalculation.");
            GUILayout.Label($"Loaded Workers indexes: containers={WorkerWorldIndex.ContainerCount}, drops={WorkerWorldIndex.ItemDropCount}, pickables={WorkerWorldIndex.PickableCount}");

            GUILayout.Space(8f);
            GUILayout.Label("Reachability Heatmap (Planet-Zoo style) — H = ein/aus, H halten = Kreatur wählen");
            GUILayout.Label("Zeigt fuer die naechste eigene geladene Workzone, welche Flaechen ein ausgewaehlter Kreaturentyp mit seiner Koerperbreite/hoehe wirklich erreichen kann. Reine Diagnose: Navigator bleibt unveraendert.");
            if (GUILayout.Button("Erreichbarkeit anzeigen / Kreatur waehlen", GUILayout.Height(30f)))
            {
                if (_reachabilityHeatmap != null && _reachabilityHeatmap.BeginNearest(_workFlagDefaultRadius.Value))
                    SetStatus("Reachability Heatmap gestartet. Kreatur im Dropdown waehlen.");
                else
                    SetStatus("Heatmap konnte nicht gestartet werden: keine geladene eigene Workzone gefunden.");
            }

            GUILayout.Space(6f);
            GUILayout.Label("Map markers");
            bool showWorkerPins = GUILayout.Toggle(_showWorkerMapMarkers.Value, "Show worker map markers");
            if (showWorkerPins != _showWorkerMapMarkers.Value)
            {
                _showWorkerMapMarkers.Value = showWorkerPins;
                if (!showWorkerPins)
                {
                    _mapMarkers?.ClearWorkerPins();
                    _mapMarkers?.ClearTransportPins();
                }
            }

            bool showHomePins = GUILayout.Toggle(_showHomeMapMarkers.Value, "Show worker home markers");
            if (showHomePins != _showHomeMapMarkers.Value)
            {
                _showHomeMapMarkers.Value = showHomePins;
                if (!showHomePins) _mapMarkers?.ClearHomePins();
            }

            bool showPinNames = GUILayout.Toggle(_showMapMarkerNames.Value, "Show marker names / roles");
            if (showPinNames != _showMapMarkerNames.Value) _showMapMarkerNames.Value = showPinNames;
            GUILayout.Label($"Transient pins: workers={_mapMarkers?.WorkerPinCount ?? 0}, transports={_mapMarkers?.TransportPinCount ?? 0}, homes={_mapMarkers?.HomePinCount ?? 0}");
            GUILayout.Label("Transport pins remain map-wide for the complete trip: physical travel uses the real Worker position; unloaded travel uses projected background progress + ETA. Pins are client-side and are not written into saved/shared map data.");
            if (_mapMarkers != null)
            {
                var transportLines = _mapMarkers.TransportStatusLines;
                GUILayout.Label($"Aktive Transporte mapweit: {transportLines.Count}");
                for (int i = 0; i < transportLines.Count; i++)
                    GUILayout.Label($"  • {transportLines[i]}");
            }

            GUILayout.Space(8f);
            GUILayout.Label($"Default work radius: {_workRadius.Value:0} m");
            float workRadius = GUILayout.HorizontalSlider(_workRadius.Value, 10f, 100f);
            if (Math.Abs(workRadius - _workRadius.Value) >= 0.5f)
                _workRadius.Value = Mathf.Round(workRadius);

            GUILayout.Label($"Test recruitment radius: {_recruitSearchRadius.Value:0} m");
            float recruitRadius = GUILayout.HorizontalSlider(_recruitSearchRadius.Value, 2f, 25f);
            if (Math.Abs(recruitRadius - _recruitSearchRadius.Value) >= 0.5f)
                _recruitSearchRadius.Value = Mathf.Round(recruitRadius);

            GUILayout.Label($"Greydwarf nest search radius: {_spawnerProbeRadius.Value:0} m");
            float probeRadius = GUILayout.HorizontalSlider(_spawnerProbeRadius.Value, 10f, 60f);
            if (Math.Abs(probeRadius - _spawnerProbeRadius.Value) >= 0.5f)
                _spawnerProbeRadius.Value = Mathf.Round(probeRadius);

            GUILayout.Label($"Greydwarf offering radius: {_spawnerOfferingRadius.Value:0.0} m");
            float offeringRadius = GUILayout.HorizontalSlider(_spawnerOfferingRadius.Value, 2f, 10f);
            if (Math.Abs(offeringRadius - _spawnerOfferingRadius.Value) >= 0.25f)
                _spawnerOfferingRadius.Value = Mathf.Round(offeringRadius * 2f) / 2f;

            GUILayout.Label($"Greydwarf home assignment radius: {_homeAssignmentRadius.Value:0} m");
            float homeAssignRadius = GUILayout.HorizontalSlider(_homeAssignmentRadius.Value, 5f, 60f);
            if (Math.Abs(homeAssignRadius - _homeAssignmentRadius.Value) >= 0.5f)
                _homeAssignmentRadius.Value = Mathf.Round(homeAssignRadius);

            GUILayout.Label($"Troll home assignment radius: {_trollHomeAssignmentRadius.Value:0} m");
            float trollHomeAssignRadius = GUILayout.HorizontalSlider(_trollHomeAssignmentRadius.Value, 8f, 70f);
            if (Math.Abs(trollHomeAssignRadius - _trollHomeAssignmentRadius.Value) >= 0.5f)
                _trollHomeAssignmentRadius.Value = Mathf.Round(trollHomeAssignRadius);

            GUILayout.Label($"Zuweisungs-Zielreichweite: {_workerAssignmentAimRadius.Value:0} m");
            float workerAssignmentAimRadius = GUILayout.HorizontalSlider(_workerAssignmentAimRadius.Value, 20f, 200f);
            if (Math.Abs(workerAssignmentAimRadius - _workerAssignmentAimRadius.Value) >= 0.5f)
                _workerAssignmentAimRadius.Value = Mathf.Round(workerAssignmentAimRadius);
            GUILayout.Label("Bei aktiver Schlafplatz-/Workzone-Zuweisung kannst du einen eigenen Worker aus dieser Entfernung ansehen und E druecken. Besonders fuer fliegende Drakes.");

            GUILayout.Label($"Authority handoff radius: {_authorityHandoffRadius.Value:0} m");
            float handoffRadius = GUILayout.HorizontalSlider(_authorityHandoffRadius.Value, 20f, 150f);
            if (Math.Abs(handoffRadius - _authorityHandoffRadius.Value) >= 1f)
                _authorityHandoffRadius.Value = Mathf.Round(handoffRadius);
            GUILayout.Label("If an assigned Worker is REMOTE-owned, Workers asks the current owner to hand authority to the assigned player only while that player is nearby.");

            GUILayout.Space(8f);
            GUILayout.Label($"Temporary debug recruitment key: {_recruitKey.Value}");
            GUILayout.Label("Worker command: look at an owned recruited Worker and press E to toggle Follow / ReturnHome");
            GUILayout.Label("Work flag: build Workers-Arbeitsfahne, look at it + E, then choose a job from the list");
            GUILayout.Label("Greydwarf nest interaction: look at the nest and press E");
            GUILayout.Label("Species -> role mapping");
            GUILayout.Label("Greyling -> Gatherer (Sammler)");
            GUILayout.Label("Greydwarf -> Hauler (Träger)");
            GUILayout.Label("Greydwarf_Elite -> Lumberjack (Tier 1)");
            GUILayout.Label("Draugr -> Lumberjack (Tier 2)");
            GUILayout.Label("Draugr_Elite -> Butcher / Livestock Keeper (Tier 2)");
            GUILayout.Label("Greydwarf Shaman -> Garden/Forestry + Wildwuchs (Tier 1)");
            GUILayout.Label("Troll -> Lumberjack (Tier 3)");
            GUILayout.Label("StoneGolem -> Stonemason: Stein + Erz abbauen (Tier 3)");
            GUILayout.Label("Neck -> Fisher (Rekrutierung über Nixenfalle)");
            GUILayout.Label("Lox -> normales gezaehmtes Tier / Reittier (kein neuer Worker; Heavy-Hauler nur Legacy-Saves)");
            GUILayout.Label("Drake -> Tier 2 Air Hauler (12 Slots / 400 kg, native flight; Rekrutierung ueber Drachenei + Brutnest)");
            GUILayout.Label("Surtling -> Firekeeper (Feuerhüter) — Rekrutierung ueber Signalfeuer am Feuergeysir");
            GUILayout.Label("Fuling -> Smith / Repairer (Tier 2)");
            GUILayout.Label("Fuling Shaman -> Harvest + Garden/Forestry + Cook (Tier 3)");
            GUILayout.Label("Fuling Berserker -> Stonemason: Stein + Erz abbauen (Tier 3)");
            GUILayout.Label("Dvergr -> Hunter / Guard (Tier 3, Crossbow)");
            GUILayout.Label("Smith -> Fuling (Tier 2)");
            GUILayout.Label("Bergbau ist Teil des Steinmetz-Jobs; die alte Miner-Rolle bleibt nur Save-Kompatibilitaet.");
            GUILayout.Label("Firekeeper: endless no-resource heat source. Patrols the Workzone and fuels fires, stone ovens, smelters and blast furnaces with Surtling fire magic without consuming wood/coal. Erzabbau = Steinmetz; Erzverarbeitung/Schmelzen = Schmied.");
            GUILayout.Label($"Worker AI: {WorkerAiController.TickSeconds:0.0}s per worker, staggered in {WorkerAiController.SchedulerSliceSeconds:0.00}s slices (max {WorkerAiController.MaxDecisionsPerSlice} heavy decision/slice).");
            GUILayout.Label("Role architecture is now capability-driven. All roles inherit persistence, authority handoff, Home/WorkRadius and ReturnHome. Job-specific work is added one role at a time.");


            GUILayout.Space(10f);
            GUILayout.Label("Species-specific homes");
            GUILayout.Label($"Greydwarf family -> Grauzwerg-Wurzelnest: {_greydwarfHomePiece?.Status ?? "unavailable"}");
            GUILayout.Label($"Troll -> Troll-Lehnplatz: {_trollHomePiece?.Status ?? "unavailable"}");
            GUILayout.Label($"Stone Golem -> Golem-Steinlager: {_stoneGolemHomePiece?.Status ?? "unavailable"}");
            GUILayout.Label($"Surtling -> Ruhestätte (kleines Lagerfeuer): {_surtlingHomePiece?.Status ?? "unavailable"}");
            GUILayout.Label($"Surtling -> Prächtiges Signalfeuer (Rekrutierung): {_surtlingSignalFirePiece?.Status ?? "unavailable"}");
            GUILayout.Label($"Neck -> Nixenfalle (Rekrutierung): {_neckTrapPiece?.Status ?? "unavailable"}");
            GUILayout.Label("Neck -> Nixenfalle: nach Freilassen automatisch Follow; E toggelt Folgen | Ufernest: Ruhestätte | Fisher: physischer Fang + Lagerlieferung");
            GUILayout.Label($"Lox -> Legacy-Ruheplatz (im Hammer verborgen, nur Save-Kompatibilitaet): {_loxHomePiece?.Status ?? "unavailable"}");
            GUILayout.Label($"Drake -> Drachenbrutnest (Rekrutierung): {_drakeEggNestPiece?.Status ?? "unavailable"}");
            GUILayout.Label($"Drake -> Drake-Landeplatz (Ruhestätte): {_drakeHomePiece?.Status ?? "unavailable"}");
            GUILayout.Label($"Fuling family -> Fuling-Bett: {_fulingHomePiece?.Status ?? "unavailable"}");
            GUILayout.Label($"Fuling family -> Fuling-Ruhesitz: {_fulingRestChairPiece?.Status ?? "unavailable"}");
            GUILayout.Label($"Logistik -> Felddepot: {_normalFieldDepotPiece?.Status ?? "unavailable"}");
            GUILayout.Label($"AirTransport -> Erweitertes Felddepot: {_advancedFieldDepotPiece?.Status ?? "unavailable"}");
            GUILayout.Label("Each home uses the same persistent anchor schema, but keeps species-specific visuals, recipe and interaction rules.");

            GUILayout.Space(10f);
            GUILayout.Label("Shared Worker rest / recovery");
            GUILayout.Label("Night: normal Workers return to HomeAnchor; active Hauler deliveries continue until cargo is delivered.");
            GUILayout.Label($"Low HP: <= {WorkerRestRuntime.RecoveryEnterFraction * 100f:0}% -> ReturnHomeForRest / Recover; work resumes at >= {WorkerRestRuntime.RecoveryExitFraction * 100f:0}%.");
            GUILayout.Label($"Healing: {WorkerRestRuntime.HealFractionPerPulse * 100f:0}% max HP every {WorkerRestRuntime.HealIntervalSeconds:0}s while Resting/Recover at home (minimum 1 HP per pulse).");
            GUILayout.Label("Rest healing requires a species HomeAnchor (Wurzelnest, Troll-Lehnplatz, later Lagerfeuer/Ufernest/etc.). Legacy home coordinates do not grant healing.");
            GUILayout.Label($"Current environment: {(WorkerRestRuntime.IsNight() ? "NIGHT" : "DAY")}");

            GUILayout.Space(10f);
            GUILayout.Label("Worker combat stance");
            GUILayout.Label("Worker: E = Folgen/Return; bei einem nahen Boot kontextuell Auf-/Absetzen. Shift+E = Frachtinventar. Worker-Menue: Shift+E an der zugewiesenen Ruhestaette.");
            GUILayout.Label("Aggressive: vanilla tamed AI may proactively fight hostiles. Defensive: only retaliates against the creature that damaged the Worker (20 s window). Nicht angreifen: creature combat disabled; job work such as tree chopping still functions.");

            GUILayout.Space(10f);
            GUILayout.Label("Recruitment foundation — Greydwarf family");
            RecruitmentProfile greydwarfProfile = RecruitmentCatalog.GreydwarfFamily;
            GUILayout.Label($"Method: {greydwarfProfile.Method} | Source: {greydwarfProfile.SourcePrefab}");
            GUILayout.Label($"Home anchor: {greydwarfProfile.HomeAnchor} | build piece: {_greydwarfHomePiece?.Status ?? "unavailable"}");
            GUILayout.Label("Nest result pool: Himbeeren -> Greyling | Blaubeeren -> Greydwarf | Distel -> Shaman | Loewenzahn -> Berserker");
            DrawRecruitmentProbeLines(false);
            GUILayout.Label($"Offer gift: look at the Greydwarf nest and press E within {_spawnerOfferingRadius.Value:0.0} m.");
            GUILayout.Label($"Accepted MVP gifts: {GreydwarfOfferingCatalog.DescribeAcceptedOfferings()}");
            GUILayout.Label("At 100/100 the nest is pacified and releases 1 Ancient Seed. The first Worker is only summoned when that seed is used on the nest; every later Worker also costs 1 Ancient Seed.");
            GUILayout.Label("Test phase: Wurzelnest still costs only 1 Wood. Summoning happens ONLY at the natural pacified nest; the root nest is a rest/home anchor.");
            GUILayout.Label("Troll T3: natuerliche Trollhoehle aufsuchen und 10x Hirschragout schenken. Der echte Hoehlen-Troll wird rekrutiert und respawnt bei Verlust nicht. Danach Troll-Lehnplatz bauen/zuweisen.");
            GUILayout.Label("Use an Ancient Seed directly on the SAME pacified nest for every summon, including the first one. No new donations are required; the saved chance mix is reused forever.");

            GUILayout.Space(10f);
            GUILayout.Label("Current worker debug");
            DrawWorkerDebugLines(false);
        }

        internal string GetWorkerCommandHoverText(Character worker)
        {
            if (!_enabled.Value || worker == null || worker is Player) return string.Empty;

            ZNetView nview = worker.GetComponent<ZNetView>();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (!WorkerZdo.IsWorker(zdo)) return string.Empty;

            Player localPlayer = Player.m_localPlayer;
            if (localPlayer == null) return string.Empty;

            long assignedPlayerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            if (assignedPlayerId != localPlayer.GetPlayerID())
                return "Arbeiter eines anderen Spielers";

            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            string selection = GetPendingWorkerSelectionHoverText(worker, zdo);
            if (!string.IsNullOrEmpty(selection))
                return selection;

            string boatHover = BoatPassengerRuntime.GetHoverText(worker);
            if (boatHover != null) return boatHover;

            string externalHover = WorkerExternalControlBridge.GetHoverText(worker);
            if (externalHover != null) return externalHover;

            bool canBoard = state == WorkerState.Follow && BoatPassengerRuntime.CanBoardNearby(worker, localPlayer);
            string follow = canBoard
                ? "[<color=yellow><b>E</b></color>] Auf Boot setzen"
                : state == WorkerState.Follow
                ? "[<color=yellow><b>E</b></color>] Folgen beenden / nach Hause"
                : "[<color=yellow><b>E</b></color>] Mir folgen";
            return follow + "\n[<color=yellow><b>Shift+E</b></color>] Arbeiter-Inventar";
        }

        internal bool TryOpenWorkerInventory(Character worker, Humanoid user)
        {
            if (!_enabled.Value || worker == null || worker is Player) return false;

            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer) return false;

            ZNetView nview = worker.GetComponent<ZNetView>();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (!WorkerZdo.IsWorker(zdo)) return false;

            long assignedPlayerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            if (assignedPlayerId != player.GetPlayerID())
            {
                SetStatus("Dieser Arbeiter gehoert einem anderen Spieler.");
                return true;
            }

            CloseWorkFlagMenu();
            CloseWorkerContainerMenu();
            CloseWorkerMenu();
            _openWorkerInventory = worker;
            _workerInventoryScroll = Vector2.zero;
            CenterWorkerInventoryWindow();
            CaptureMenuCursor();
            _lastWorker = worker;
            SetStatus("Arbeiter-Inventar geoeffnet.");
            return true;
        }

        internal bool TryOpenWorkerMenu(Character worker, Humanoid user)
        {
            if (!_enabled.Value || worker == null || worker is Player) return false;

            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer) return false;

            ZNetView nview = worker.GetComponent<ZNetView>();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (!WorkerZdo.IsWorker(zdo)) return false;

            long assignedPlayerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            if (assignedPlayerId != player.GetPlayerID())
            {
                SetStatus("Dieser Arbeiter gehoert einem anderen Spieler.");
                return true;
            }

            if (!nview.IsOwner())
            {
                SetStatus("Arbeiter ist REMOTE-owned. Bleib kurz in seiner Naehe und oeffne das Worker-Menue danach erneut.");
                return true;
            }

            CloseWorkFlagMenu();
            CloseWorkerContainerMenu();
            CloseWorkerInventory();
            _openWorkerMenu = worker;
            _openPersistentWorkerMenuZdo = null;
            _openPersistentWorkerMenuHomeZdo = null;
            _openPersistentWorkerMenuHomeLabel = string.Empty;
            _workerMenuScroll = Vector2.zero;
            CenterWorkerWindow();
            CaptureMenuCursor();
            _lastWorker = worker;
            SetStatus("Worker-Menue geoeffnet.");
            return true;
        }

        internal bool TryInteractWithWorker(Character worker, Humanoid user)
        {
            if (!_enabled.Value || worker == null || worker is Player) return false;

            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer)
                return false;

            ZNetView nview = worker.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid() || nview.GetZDO() == null)
            {
                SetStatus("Worker command failed: no valid network state.");
                return true;
            }

            ZDO zdo = nview.GetZDO();
            if (!WorkerZdo.IsWorker(zdo)) return false;

            _lastWorker = worker;
            long assignedPlayerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            if (assignedPlayerId != player.GetPlayerID())
            {
                SetStatus("Dieser Arbeiter gehoert einem anderen Spieler.");
                return true;
            }

            if (!nview.IsOwner())
            {
                SetStatus("Arbeiter ist REMOTE-owned. Bleib kurz in seiner Naehe fuer den normalen Ownership-Handoff und drueck dann erneut E.");
                return true;
            }

            if (BoatPassengerRuntime.IsTransported(worker))
            {
                if (BoatPassengerRuntime.TryDisembark(worker, zdo, player, out string disembarkStatus))
                    SetStatus(disembarkStatus);
                return true;
            }

            if (HasPendingWorkerSelection())
                return TryCompletePendingWorkerSelection(worker, nview, zdo, player);

            string species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);

            // Follow remains the normal direct Worker command for every species.
            MonsterAI monster = worker.GetComponent<MonsterAI>();
            if (monster == null)
            {
                SetStatus("Follow nicht verfuegbar: dieser Arbeiter hat keine MonsterAI.");
                return true;
            }

            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);

            if (state == WorkerState.Follow)
            {
                if (BoatPassengerRuntime.TryBoardNearestBoat(worker, zdo, player, out string boardStatus))
                {
                    if (!string.IsNullOrEmpty(boardStatus)) SetStatus(boardStatus);
                    return true;
                }

                WorkerNavigationRuntime.SetNativeFollowTarget(monster, null);
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.ReturnHome);
                SetStatus($"{species}: Folgen beendet -> ReturnHome.");
                Logger.LogInfo($"Worker follow stopped. Worker={species}, player={player.GetPlayerID()}.");
            }
            else
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.Follow);
                WorkerNavigationRuntime.SetNativeFollowTarget(monster, player.gameObject);
                SetStatus($"{species} folgt dir. Fuehre ihn zu seinem passenden neuen Zuhause und druecke dort E.");
                Logger.LogInfo($"Worker follow started. Worker={species}, player={player.GetPlayerID()}.");
            }

            return true;
        }

        private bool HasPendingWorkerSelection() =>
            _pendingWorkFlagAssignment != null || _pendingGreydwarfHomeAssignment != null || _pendingTrollHomeAssignment != null ||
            _pendingStoneGolemHomeAssignment != null || _pendingGenericHomeAssignment != null;

        private void CancelPendingWorkerSelection(string message)
        {
            _pendingWorkFlagAssignment = null;
            _pendingGreydwarfHomeAssignment = null;
            _pendingTrollHomeAssignment = null;
            _pendingStoneGolemHomeAssignment = null;
            _pendingGenericHomeAssignment = null;
            _pendingWorkerSelectionStartedFrame = -1;
            if (!string.IsNullOrEmpty(message)) SetStatus(message);
        }

        private void BeginGenericHomeWorkerSelection(ZNetView homeView, Vector3 homePosition, string anchorKind, string displayName)
        {
            if (homeView == null || !homeView.IsValid() || homeView.GetZDO() == null) return;
            CancelPendingWorkerSelection(null);
            _pendingGenericHomeAssignment = new PendingGenericHomeSelection
            {
                View = homeView,
                Position = homePosition,
                AnchorKind = anchorKind ?? string.Empty,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Schlafplatz" : displayName
            };
            _pendingWorkerSelectionStartedFrame = Time.frameCount;
            SetStatus($"Schlafplatz-Zuweisung: Schau den gewuenschten Arbeiter bis {_workerAssignmentAimRadius.Value:0} m Entfernung an und druecke E. ESC bricht ab. ({_pendingGenericHomeAssignment.DisplayName})");
        }

        private static bool IsSpeciesCompatibleWithGenericHome(string anchorKind, string species)
        {
            species = SpeciesRoleRegistry.NormalizePrefabName(species ?? string.Empty);
            if (string.Equals(anchorKind, WorkerHomeProfileCatalog.HunterStandKind, StringComparison.Ordinal))
                // HunterStand is a job/capability home, not a Draugr-only home.
                // Draugr_Ranged (T2) and Dverger (T3) both expose UseHunterStand.
                return WorkerTierCatalog.GetCapabilityTier(species, WorkerCapability.UseHunterStand) > 0;
            if (string.Equals(anchorKind, WorkerHomeProfileCatalog.DraugrRestKind, StringComparison.Ordinal))
                return species.StartsWith("Draugr", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(anchorKind, WorkerHomeProfileCatalog.FulingCampKind, StringComparison.Ordinal))
                return species.StartsWith("Fuling", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(anchorKind, WorkerHomeProfileCatalog.WatersideNestKind, StringComparison.Ordinal))
                return string.Equals(species, "Neck", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(anchorKind, WorkerHomeProfileCatalog.LoxShelterKind, StringComparison.Ordinal))
                return string.Equals(species, "Lox", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(anchorKind, WorkerHomeProfileCatalog.DrakePerchKind, StringComparison.Ordinal))
                return string.Equals(species, "Drake", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(anchorKind, WorkerHomeProfileCatalog.FireHearthKind, StringComparison.Ordinal))
                return string.Equals(species, "Surtling", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(anchorKind, WorkerHomeProfileCatalog.WolfDenKind, StringComparison.Ordinal))
                return string.Equals(species, "Wolf", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        private void BeginWorkFlagWorkerSelection(WorkFlagInteractable flag)
        {
            if (flag == null) return;
            CancelPendingWorkerSelection(null);
            _pendingWorkFlagAssignment = flag;
            _pendingWorkerSelectionStartedFrame = Time.frameCount;
            CloseWorkFlagMenu();
            SetStatus($"Arbeitsplatz-Zuweisung: Schau den gewuenschten eigenen Arbeiter bis {_workerAssignmentAimRadius.Value:0} m Entfernung an und druecke E. ESC bricht ab.");
        }

        private void BeginGreydwarfHomeWorkerSelection(GreydwarfHomeNestInteractable home)
        {
            if (home == null) return;
            CancelPendingWorkerSelection(null);
            _pendingGreydwarfHomeAssignment = home;
            _pendingWorkerSelectionStartedFrame = Time.frameCount;
            SetStatus($"Schlafplatz-Zuweisung: Schau den gewuenschten Greyling/Grauzwerg bis {_workerAssignmentAimRadius.Value:0} m Entfernung an und druecke E. ESC bricht ab.");
        }

        private void BeginTrollHomeWorkerSelection(TrollHomeCampInteractable home)
        {
            if (home == null) return;
            CancelPendingWorkerSelection(null);
            _pendingTrollHomeAssignment = home;
            _pendingWorkerSelectionStartedFrame = Time.frameCount;
            SetStatus($"Schlafplatz-Zuweisung: Schau den gewuenschten Troll-Arbeiter bis {_workerAssignmentAimRadius.Value:0} m Entfernung an und druecke E. ESC bricht ab.");
        }


        private void BeginStoneGolemHomeWorkerSelection(StoneGolemHomeInteractable home)
        {
            if (home == null) return;
            CancelPendingWorkerSelection(null);
            _pendingStoneGolemHomeAssignment = home;
            _pendingWorkerSelectionStartedFrame = Time.frameCount;
            SetStatus($"Schlafplatz-Zuweisung: Schau den gewuenschten Stone Golem bis {_workerAssignmentAimRadius.Value:0} m Entfernung an und druecke E. ESC bricht ab.");
        }

        private bool TryHandleLongRangePendingWorkerSelection()
        {
            if (!HasPendingWorkerSelection() || Player.m_localPlayer == null) return false;
            // Do not let the same E press that opened the home/flag selection immediately pick a
            // Worker that happens to stand behind it. Long-range targeting starts next frame.
            if (_pendingWorkerSelectionStartedFrame >= Time.frameCount) return false;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) return false;
            if (HasOpenWorkersPopup()) return false;

            Camera camera = Camera.main;
            if (camera == null) return false;

            Player player = Player.m_localPlayer;
            float maxDistance = Mathf.Clamp(_workerAssignmentAimRadius != null ? _workerAssignmentAimRadius.Value : 120f, 20f, 250f);
            Vector3 origin = camera.transform.position;
            Vector3 forward = camera.transform.forward.normalized;
            Character best = null;
            float bestScore = float.MaxValue;

            WorkerFastLookupRuntime.CopyKnownWorkers(_loadedWorkerScratch);
            for (int i = 0; i < _loadedWorkerScratch.Count; i++)
            {
                Character candidate = _loadedWorkerScratch[i];
                if (candidate == null || candidate.IsDead()) continue;
                if (!WorkerFastLookupRuntime.TryGetWorker(candidate, false, out ZNetView view, out ZDO zdo)) continue;
                if (zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != player.GetPlayerID()) continue;
                if (!IsCompatibleWithPendingWorkerSelection(candidate, zdo)) continue;

                // Root transforms of Troll/Golem/Drake are not all centered on the visible body.
                // Prefer the Character collider center so aiming at the creature works naturally.
                Vector3 aimPoint = candidate.transform.position + Vector3.up;
                float bodyRadius = 0.9f;
                Collider body = candidate.GetComponent<Collider>();
                if (body == null) body = candidate.GetComponentInChildren<Collider>();
                if (body != null)
                {
                    aimPoint = body.bounds.center;
                    bodyRadius = Mathf.Clamp(Mathf.Max(body.bounds.extents.x, body.bounds.extents.y), 0.75f, 3.5f);
                }

                Vector3 to = aimPoint - origin;
                float depth = Vector3.Dot(to, forward);
                // Inside normal Valheim E range the existing hover/interact path stays authoritative,
                // preventing one press from assigning and then toggling Follow as a second action.
                if (depth < 6f || depth > maxDistance) continue;

                Vector3 closest = origin + forward * depth;
                float lateral = Vector3.Distance(aimPoint, closest);
                float allowed = Mathf.Clamp(bodyRadius + depth * 0.018f, 1.0f, 5.0f);
                if (lateral > allowed) continue;

                float normalizedLateral = lateral / Mathf.Max(0.1f, allowed);
                float score = normalizedLateral * normalizedLateral + depth * 0.0005f;
                if (score >= bestScore) continue;
                bestScore = score;
                best = candidate;
            }

            if (best == null) return false;

            bool handled = TryInteractWithWorker(best, player);
            if (handled)
            {
                _suppressNativeInteractFrame = Time.frameCount;
                string species = SpeciesRoleRegistry.NormalizePrefabName(best.gameObject.name);
                Logger.LogInfo($"Long-range Worker assignment target handled. Worker={species}, distance={Vector3.Distance(player.transform.position, best.transform.position):0.0}m, max={maxDistance:0}m.");
            }
            return handled;
        }

        private bool IsCompatibleWithPendingWorkerSelection(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return false;
            string species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);

            if (_pendingGreydwarfHomeAssignment != null)
                return IsGreydwarfHomeSpecies(species);
            if (_pendingTrollHomeAssignment != null)
                return string.Equals(species, "Troll", StringComparison.OrdinalIgnoreCase);
            if (_pendingStoneGolemHomeAssignment != null)
                return string.Equals(species, "StoneGolem", StringComparison.OrdinalIgnoreCase);
            if (_pendingGenericHomeAssignment != null)
                return IsSpeciesCompatibleWithGenericHome(_pendingGenericHomeAssignment.AnchorKind, species);
            if (_pendingWorkFlagAssignment != null)
            {
                WorkerRole role = (WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
                return WorkerJobProfileRuntime.ResolveAssignmentOrder(zdo, role) != WorkOrder.None;
            }
            return false;
        }

        internal bool ShouldSuppressNativeInteractThisFrame(Player player)
        {
            return player != null && player == Player.m_localPlayer && _suppressNativeInteractFrame == Time.frameCount;
        }

        private string GetPendingWorkerSelectionHoverText(Character worker, ZDO zdo)
        {
            if (!HasPendingWorkerSelection() || worker == null || zdo == null) return string.Empty;
            string species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
            WorkerRole role = WorkerJobProfileRuntime.NormalizeRuntimeRole(zdo);

            if (_pendingGreydwarfHomeAssignment != null && !IsGreydwarfHomeSpecies(species))
                return $"{species} — {role}\nNicht kompatibel mit Wurzelnest\n[ESC] Auswahl abbrechen";
            if (_pendingTrollHomeAssignment != null && !string.Equals(species, "Troll", StringComparison.OrdinalIgnoreCase))
                return $"{species} — {role}\nNicht kompatibel mit Troll-Lehnplatz\n[ESC] Auswahl abbrechen";
            if (_pendingStoneGolemHomeAssignment != null && !string.Equals(species, "StoneGolem", StringComparison.OrdinalIgnoreCase))
                return $"{species} — {role}\nNicht kompatibel mit Golem-Steinlager\n[ESC] Auswahl abbrechen";
            if (_pendingGenericHomeAssignment != null)
            {
                if (!IsSpeciesCompatibleWithGenericHome(_pendingGenericHomeAssignment.AnchorKind, species))
                    return $"{species} — {role}\nNicht kompatibel mit {_pendingGenericHomeAssignment.DisplayName}\n[ESC] Auswahl abbrechen";
                return $"{species} — {role}\n[<color=yellow><b>E</b></color>] Diesem Schlafplatz zuweisen\n[ESC] Auswahl abbrechen";
            }
            if (_pendingWorkFlagAssignment != null)
            {
                ZNetView flagView = _pendingWorkFlagAssignment.GetNetworkView();
                ZDO flagZdo = flagView != null && flagView.IsValid() ? flagView.GetZDO() : null;
                WorkOrder order = WorkerJobProfileRuntime.ResolveAssignmentOrder(zdo, role);
                if (order == WorkOrder.None)
                    return $"{species} — {role}\nKeine kompatible Worker-Aufgabe vorhanden\n[ESC] Auswahl abbrechen";
                return $"{species} — {role}\n[<color=yellow><b>E</b></color>] Dieser Workzone zuweisen\n[ESC] Auswahl abbrechen";
            }
            return $"{species} — {role}\n[<color=yellow><b>E</b></color>] Diesen Arbeiter als Bewohner zuweisen\n[ESC] Auswahl abbrechen";
        }

        private bool TryCompletePendingWorkerSelection(Character worker, ZNetView workerView, ZDO workerZdo, Player player)
        {
            if (_pendingWorkFlagAssignment != null)
                return TryAssignSelectedWorkerToFlag(worker, workerView, workerZdo, player);
            if (_pendingGreydwarfHomeAssignment != null)
                return TryAssignSelectedWorkerToGreydwarfHome(worker, workerView, workerZdo, player);
            if (_pendingTrollHomeAssignment != null)
                return TryAssignSelectedWorkerToTrollHome(worker, workerView, workerZdo, player);
            if (_pendingStoneGolemHomeAssignment != null)
                return TryAssignSelectedWorkerToStoneGolemHome(worker, workerView, workerZdo, player);
            if (_pendingGenericHomeAssignment != null)
                return TryAssignSelectedWorkerToGenericHome(worker, workerView, workerZdo, player);
            return false;
        }

        private bool TryAssignSelectedWorkerToFlag(Character worker, ZNetView workerView, ZDO workerZdo, Player player)
        {
            WorkFlagInteractable flag = _pendingWorkFlagAssignment;
            if (flag == null) return false;
            ZNetView flagView = flag.GetNetworkView();
            ZDO flagZdo = flagView != null && flagView.IsValid() ? flagView.GetZDO() : null;
            if (flagZdo == null || !flagView.IsOwner())
            {
                CancelPendingWorkerSelection("Arbeitsfahne ist nicht mehr lokal verfuegbar.");
                return true;
            }
            WorkerRole role = (WorkerRole)workerZdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
            if (WorkerZdo.HasCargo(workerZdo))
            {
                SetStatus("Zuweisung blockiert: Der Arbeiter traegt noch Ladung. Erst abliefern lassen.");
                return true;
            }
            WorkOrder order = WorkerJobProfileRuntime.ResolveAssignmentOrder(workerZdo, role);
            if (order == WorkOrder.None)
            {
                SetStatus($"Dieser Arbeiter ({role}) besitzt keine kompatible Worker-Aufgabe. Auswahl bleibt aktiv.");
                return true;
            }

            string flagId = WorkFlagZdo.EnsureId(flagZdo);
            string flagName = EnsureWorkFlagDisplayName(flagZdo, WorkFlagZdo.ReadOwner(flagZdo));
            float radius = WorkFlagZdo.ReadRadius(flagZdo, _workFlagDefaultRadius.Value);
            if (!WorkerWorkAreaRules.TryValidateHomeForArea(workerZdo, flag.transform.position, radius, out string homeReason))
            {
                SetStatus($"Zuweisung blockiert: {homeReason}. Baue/weise zuerst einen Schlafplatz innerhalb dieser Workzone zu.");
                return true;
            }
            WorkerZdo.AssignWorkArea(workerZdo, flagId, flagName, order, flag.transform.position, radius);
            WorkerZdo.ClearTarget(workerZdo);
            MonsterAI ai = worker.GetComponent<MonsterAI>();
            if (ai != null && ai.GetFollowTarget() != null) WorkerNavigationRuntime.SetNativeFollowTarget(ai, null);
            WorkerState state = (WorkerState)workerZdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state != WorkerState.ReturnHomeForRest && state != WorkerState.Resting && state != WorkerState.Recover)
                workerZdo.Set(WorkerZdo.State, role == WorkerRole.Stonemason ? (int)WorkerState.FindJob : (int)WorkerState.MoveToWorkArea);

            string species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
            CancelPendingWorkerSelection(null);
            SetStatus($"{species} gezielt der Workzone zugewiesen. Jobs/Prioritaeten kommen von seiner Ruhestaette.");
            Logger.LogInfo($"Targeted work assignment. Worker={species}, role={role}, order={order}, player={player.GetPlayerID()}, flag={flagId}.");
            return true;
        }

        // v0.5.335: Home assignment must never be blocked merely because the player currently
        // carries no food/fuel. If one accepted unit is present, transfer exactly one item as a
        // convenient starter stock; otherwise keep the assignment valid and let the normal
        // provisioning/hunger system request supply afterwards. This preserves the useful
        // auto-seeding behaviour from v0.5.335 without turning sustenance into an assignment fee.
        private bool TryLinkHomeWithInitialProvision(
            Player player, ZDO workerZdo, ZDO homeZdo, Vector3 homePosition, string anchorKind,
            string species, WorkerRole role, string homeDisplayName)
        {
            if (player == null || workerZdo == null || homeZdo == null) return false;

            bool hasRequirement = WorkerSustenanceRuntime.TryGetRequirement(
                workerZdo, out string provisionPrefab, out string provisionDisplay, out _);

            WorkerHomeAssignmentRuntime.Link(
                workerZdo, homeZdo, homePosition, anchorKind, player.GetPlayerID(), species, role);

            if (hasRequirement && WorkerSustenanceRuntime.CountPlayerSupply(player, workerZdo) > 0)
            {
                int moved = WorkerSustenanceRuntime.DepositFromPlayer(player, workerZdo, 1);
                if (moved > 0)
                    Plugin.Log?.LogInfo($"Initial home provision auto-deposited. Worker={workerZdo.m_uid}, species={species}, home={homeZdo.m_uid}, provision={provisionPrefab} x1.");
            }

            return true;
        }

        private bool TryAssignSelectedWorkerToGreydwarfHome(Character worker, ZNetView workerView, ZDO workerZdo, Player player)
        {
            GreydwarfHomeNestInteractable home = _pendingGreydwarfHomeAssignment;
            if (home == null) return false;
            string species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
            if (!IsGreydwarfHomeSpecies(species))
            {
                SetStatus("Dieser Arbeiter kann das Wurzelnest nicht als Zuhause benutzen. Auswahl bleibt aktiv.");
                return true;
            }
            ZNetView homeView = home.GetNetworkView();
            ZDO homeZdo = homeView != null && homeView.IsValid() ? homeView.GetZDO() : null;
            if (homeZdo == null || !homeView.IsOwner() || GreydwarfHomeAnchorZdo.IsAssigned(homeZdo))
            {
                CancelPendingWorkerSelection("Wurzelnest ist nicht mehr frei/lokal verfuegbar.");
                return true;
            }
            WorkerRole role = (WorkerRole)workerZdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
            Vector3 pos = home.transform.position;
            if (!WorkerWorkAreaRules.TryValidateProposedHomeForCurrentAssignment(workerZdo, pos, out _, out string homeAreaReason))
            {
                SetStatus(homeAreaReason + ". Schlafplatz nicht zugewiesen.");
                return true;
            }
            if (!TryLinkHomeWithInitialProvision(player, workerZdo, homeZdo, pos,
                GreydwarfHomeAnchorZdo.GreydwarfNestKind, species, role, "Wurzelnest"))
                return true;
            CancelPendingWorkerSelection(null);
            SetStatus($"{species}: Wurzelnest als Schlafplatz/Zuhause zugewiesen.");
            Logger.LogInfo($"Targeted Greydwarf home assignment. Worker={species}, role={role}, player={player.GetPlayerID()}, home={pos}.");
            return true;
        }

        private bool TryAssignSelectedWorkerToTrollHome(Character worker, ZNetView workerView, ZDO workerZdo, Player player)
        {
            TrollHomeCampInteractable home = _pendingTrollHomeAssignment;
            if (home == null) return false;
            string species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
            if (!string.Equals(species, "Troll", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Nur ein rekrutierter Troll kann diese Lagerstatt benutzen. Auswahl bleibt aktiv.");
                return true;
            }
            ZNetView homeView = home.GetNetworkView();
            ZDO homeZdo = homeView != null && homeView.IsValid() ? homeView.GetZDO() : null;
            if (homeZdo == null || !homeView.IsOwner() || WorkerHomeAnchorZdo.IsAssigned(homeZdo))
            {
                CancelPendingWorkerSelection("Troll-Lehnplatz ist nicht mehr frei/lokal verfuegbar.");
                return true;
            }
            WorkerRole role = (WorkerRole)workerZdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
            Vector3 pos = home.transform.position;
            if (!WorkerWorkAreaRules.TryValidateProposedHomeForCurrentAssignment(workerZdo, pos, out _, out string homeAreaReason))
            {
                SetStatus(homeAreaReason + ". Schlafplatz nicht zugewiesen.");
                return true;
            }
            if (!TryLinkHomeWithInitialProvision(player, workerZdo, homeZdo, pos,
                WorkerHomeProfileCatalog.TrollCampKind, species, role, "Troll-Lehnplatz"))
                return true;
            CancelPendingWorkerSelection(null);
            SetStatus("Troll-Lehnplatz als Schlafplatz/Zuhause zugewiesen.");
            Logger.LogInfo($"Targeted Troll home assignment. Worker={species}, role={role}, player={player.GetPlayerID()}, home={pos}.");
            return true;
        }

        private bool TryAssignSelectedWorkerToStoneGolemHome(Character worker, ZNetView workerView, ZDO workerZdo, Player player)
        {
            StoneGolemHomeInteractable home = _pendingStoneGolemHomeAssignment;
            if (home == null) return false;
            string species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
            if (!string.Equals(species, "StoneGolem", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Nur ein rekrutierter Stone Golem kann dieses Steinlager benutzen. Auswahl bleibt aktiv.");
                return true;
            }
            ZNetView homeView = home.GetNetworkView();
            ZDO homeZdo = homeView != null && homeView.IsValid() ? homeView.GetZDO() : null;
            if (homeZdo == null || !homeView.IsOwner() || WorkerHomeAnchorZdo.IsAssigned(homeZdo))
            {
                CancelPendingWorkerSelection("Golem-Steinlager ist nicht mehr frei/lokal verfuegbar.");
                return true;
            }
            WorkerRole role = (WorkerRole)workerZdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
            Vector3 pos = home.transform.position;
            if (!WorkerWorkAreaRules.TryValidateProposedHomeForCurrentAssignment(workerZdo, pos, out _, out string homeAreaReason))
            {
                SetStatus(homeAreaReason + ". Schlafplatz nicht zugewiesen.");
                return true;
            }
            if (!TryLinkHomeWithInitialProvision(player, workerZdo, homeZdo, pos,
                WorkerHomeProfileCatalog.StoneGolemShelterKind, species, role, "Golem-Steinlager"))
                return true;
            CancelPendingWorkerSelection(null);
            SetStatus("Golem-Steinlager als Schlafplatz/Zuhause zugewiesen.");
            Logger.LogInfo($"Targeted Stone Golem home assignment. Worker={species}, role={role}, player={player.GetPlayerID()}, home={pos}.");
            return true;
        }

        private bool TryAssignSelectedWorkerToGenericHome(Character worker, ZNetView workerView, ZDO workerZdo, Player player)
        {
            PendingGenericHomeSelection pending = _pendingGenericHomeAssignment;
            if (pending == null) return false;

            string species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
            WorkerRole role = (WorkerRole)workerZdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
            if (!IsSpeciesCompatibleWithGenericHome(pending.AnchorKind, species))
            {
                SetStatus($"{species} kann {_pendingGenericHomeAssignment.DisplayName} nicht als Schlafplatz benutzen. Auswahl bleibt aktiv.");
                return true;
            }

            ZNetView homeView = pending.View;
            ZDO homeZdo = homeView != null && homeView.IsValid() ? homeView.GetZDO() : null;
            if (homeZdo == null || !homeView.IsOwner() || WorkerHomeAnchorZdo.IsAssigned(homeZdo))
            {
                CancelPendingWorkerSelection($"{pending.DisplayName} ist nicht mehr frei/lokal verfuegbar.");
                return true;
            }
            if (workerView == null || !workerView.IsValid() || !workerView.IsOwner())
            {
                SetStatus("Schlafplatz-Zuweisung fehlgeschlagen: Worker ist nicht lokal steuerbar.");
                return true;
            }
            if (workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != player.GetPlayerID())
            {
                SetStatus("Schlafplatz-Zuweisung blockiert: Dieser Worker gehoert einem anderen Spieler.");
                return true;
            }

            if (!WorkerWorkAreaRules.TryValidateProposedHomeForCurrentAssignment(workerZdo, pending.Position, out _, out string homeAreaReason))
            {
                SetStatus(homeAreaReason + ". Schlafplatz nicht zugewiesen.");
                return true;
            }

            if (string.Equals(pending.AnchorKind, WorkerHomeProfileCatalog.FireHearthKind, StringComparison.Ordinal))
                FirekeeperAssignedFireRuntime.ReleaseWorkerFire(workerZdo);

            // Link() itself enforces the v0.5.133 1:1 invariant and releases the Worker's old
            // HomeAnchor before the new link is written.
            if (!TryLinkHomeWithInitialProvision(player, workerZdo, homeZdo, pending.Position,
                pending.AnchorKind, species, role, pending.DisplayName))
                return true;
            WorkerZdo.ClearTarget(workerZdo);

            string displayName = pending.DisplayName;
            string anchorKind = pending.AnchorKind;
            CancelPendingWorkerSelection(null);

            if (string.Equals(anchorKind, WorkerHomeProfileCatalog.WatersideNestKind, StringComparison.Ordinal))
            {
                if (WorkFlagRegistry.TryFindBestForOrder(worker, workerZdo, WorkerRole.Fisher, WorkOrder.Fish,
                    1000f, _workFlagDefaultRadius.Value, out WorkAreaSnapshot fishArea))
                {
                    WorkerZdo.AssignWorkArea(workerZdo, fishArea.Id, fishArea.DisplayName, WorkOrder.Fish, fishArea.Center, fishArea.Radius);
                    workerZdo.Set(WorkerZdo.State, (int)WorkerState.MoveToWorkArea);
                    string zoneName = string.IsNullOrWhiteSpace(fishArea.DisplayName) ? "Fischer-Workzone" : fishArea.DisplayName;
                    SetStatus($"{displayName} zugewiesen. Neck automatisch -> {zoneName} / Fischen.");
                }
                else
                {
                    workerZdo.Set(WorkerZdo.State, (int)WorkerState.ReturnHome);
                    SetStatus($"{displayName} zugewiesen, aber keine aktive Workzone gefunden, die den Schlafplatz enthaelt.");
                }
                return true;
            }

            if (string.Equals(anchorKind, WorkerHomeProfileCatalog.LoxShelterKind, StringComparison.Ordinal))
            {
                if (WorkFlagRegistry.TryFindBestForOrder(worker, workerZdo, WorkerRole.Hauler, WorkOrder.Haul,
                    1000f, _workFlagDefaultRadius.Value, out WorkAreaSnapshot transportArea))
                {
                    WorkerZdo.AssignWorkArea(workerZdo, transportArea.Id, transportArea.DisplayName, WorkOrder.Haul,
                        transportArea.Center, transportArea.Radius);
                    workerZdo.Set(WorkerZdo.State, (int)WorkerState.MoveToWorkArea);
                    string zoneName = string.IsNullOrWhiteSpace(transportArea.DisplayName) ? "Transport-Workzone" : transportArea.DisplayName;
                    SetStatus($"{displayName} zugewiesen. Heavy Hauler automatisch -> {zoneName} / Transportieren. 24 Slots / 1200 kg.");
                }
                else
                {
                    workerZdo.Set(WorkerZdo.State, (int)WorkerState.ReturnHome);
                    SetStatus($"{displayName} zugewiesen, aber keine aktive Transport-Workzone gefunden, die diesen Ruheplatz enthaelt.");
                }
                return true;
            }

            workerZdo.Set(WorkerZdo.State, (int)WorkerState.ReturnHome);
            if (string.Equals(anchorKind, WorkerHomeProfileCatalog.DrakePerchKind, StringComparison.Ordinal))
                SetStatus("Drake-Landeplatz zugewiesen. Air Hauler: 12 Slots / 400 kg.");
            else if (string.Equals(anchorKind, WorkerHomeProfileCatalog.HunterStandKind, StringComparison.Ordinal))
                SetStatus($"Draugr-Jagdplattform zugewiesen: {species} — {role}. Plattform ist jetzt Zuhause + Jagdstation.");
            else if (string.Equals(anchorKind, WorkerHomeProfileCatalog.FireHearthKind, StringComparison.Ordinal))
                SetStatus("Surtling-Ruhestätte zugewiesen. Dauerfeuer braucht keinen Brennstoff; der Feuerhueter kann in seiner Workzone arbeiten.");
            else if (string.Equals(anchorKind, WorkerHomeProfileCatalog.WolfDenKind, StringComparison.Ordinal))
                SetStatus("Wolfsbau zugewiesen. Der Wolf nutzt ihn als Zuhause und kehrt fuer Nacht/Ruhe dorthin zurueck.");
            else
                SetStatus($"{displayName} als Schlafplatz/Zuhause fuer {species} zugewiesen.");

            Logger.LogInfo($"Targeted generic home assignment. Worker={species}, role={role}, player={player.GetPlayerID()}, anchor={anchorKind}, home={pending.Position}.");
            return true;
        }

        internal bool TryCycleWorkerStance(Character worker, Humanoid user)
        {
            if (!_enabled.Value || worker == null || worker is Player) return false;

            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer) return false;

            ZNetView nview = worker.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid() || nview.GetZDO() == null)
            {
                SetStatus("Haltung konnte nicht geaendert werden: kein gueltiger Netzwerkstatus.");
                return true;
            }

            ZDO zdo = nview.GetZDO();
            if (!WorkerZdo.IsWorker(zdo)) return false;

            _lastWorker = worker;
            if (zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != player.GetPlayerID())
            {
                SetStatus("Dieser Arbeiter gehoert einem anderen Spieler.");
                return true;
            }

            if (!nview.IsOwner())
            {
                SetStatus("Arbeiter ist REMOTE-owned. Bleib kurz in seiner Naehe und versuch Shift+E erneut.");
                return true;
            }

            WorkerStance next = WorkerCombatStanceRuntime.CycleStance(worker, zdo);
            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state == WorkerState.Combat && next != WorkerStance.Aggressive)
                zdo.Set(WorkerZdo.State, (int)WorkerState.ReturnHome);

            string species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
            SetStatus($"{species}: Haltung -> {WorkerCombatStanceRuntime.DisplayName(next)}.");
            Logger.LogInfo($"Worker stance changed. Worker={species}, stance={next}, player={player.GetPlayerID()}.");
            return true;
        }

        internal bool TryOpenWorkerContainerMenu(Container container, Humanoid user)
        {
            if (!_enabled.Value || container == null) return false;
            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer) return false;
            ZNetView view = WorkerContainerRoleRuntime.GetNetworkView(container);
            if (view == null || !view.IsValid() || view.GetZDO() == null)
            {
                SetStatus("Kiste hat keinen gueltigen Netzwerkstatus.");
                return true;
            }
            if (!view.IsOwner())
            {
                SetStatus("Kiste ist REMOTE-owned. Bleib kurz in der Naehe und versuch Shift+E erneut.");
                return true;
            }
            long roleOwner = WorkerContainerRoleRuntime.GetOwner(container);
            if (roleOwner != 0L && roleOwner != player.GetPlayerID())
            {
                SetStatus("Diese Workers-Logistikkiste gehoert einem anderen Spieler.");
                return true;
            }
            _openWorkerContainer = container;
            string autoZoneId = string.Empty;
            string autoZoneName = string.Empty;
            bool autoAssignedWorkzone = false;
            if (WorkerContainerRoleRuntime.GetRole(container) == WorkerContainerRole.FieldDepot)
                autoAssignedWorkzone = FieldDepotWorkzoneRuntime.TryAutoAssignNearestWorkArea(container, out autoZoneId, out autoZoneName);
            string autoAssignedWorkzoneLabel = autoAssignedWorkzone
                ? (string.IsNullOrWhiteSpace(autoZoneName) ? autoZoneId : autoZoneName)
                : string.Empty;
            _workerContainerScroll = Vector2.zero;
            _fieldDepotStockSettingsOpen = false;
            _fieldDepotStockScroll = Vector2.zero;
            _containerStockLimitSettingsOpen = false;
            _containerStockLimitScroll = Vector2.zero;
            _containerStockLimitSearch = string.Empty;
            _containerReserveEdits.Clear();
            _containerTriggerEdits.Clear();
            _containerStockMinEdits.Clear();
            _containerStockMaxEdits.Clear();
            CaptureMenuCursor();
            if (autoAssignedWorkzone)
                SetStatus($"Felddepot automatisch der naechsten Workzone zugewiesen: {autoAssignedWorkzoneLabel}.");
            else
                SetStatus($"Logistikkiste: {WorkerContainerRoleRuntime.DisplayName(WorkerContainerRoleRuntime.GetRole(container))}.");
            return true;
        }

        internal bool TryOpenProductionStationMenu(GameObject target, Humanoid user)
        {
            if (!_enabled.Value || target == null) return false;
            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer) return false;
            if (!ProductionStationRuntime.TryResolve(target, out ProductionStationRef station) || station.View == null || station.Zdo == null)
                return false;

            _openProductionStation = station.Root != null ? station.Root : target;
            _productionStationScroll = Vector2.zero;
            CaptureMenuCursor();
            float width = Mathf.Min(580f, Screen.width - 24f);
            float height = Mathf.Min(580f, Screen.height - 24f);
            _productionStationMenuRect = new Rect(Mathf.Max(12f, (Screen.width - width) * 0.5f), Mathf.Max(12f, (Screen.height - height) * 0.5f), width, height);
            SetStatus(station.DisplayName + ": Produktionsziele.");
            return true;
        }

        private void DrawProductionStationMenuWindow(int windowId)
        {
            EnsureSmartPopupStyles();
            if (_openProductionStation == null || !ProductionStationRuntime.TryResolve(_openProductionStation, out ProductionStationRef station) || station.Zdo == null)
            {
                GUILayout.Label("Produktionsstation nicht mehr verfuegbar.", _smartPopupLabelStyle);
                if (GUILayout.Button("Schliessen", _smartPopupButtonStyle)) CloseProductionStationMenu();
                return;
            }

            bool writable = station.View != null && station.View.IsOwner();
            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            GUILayout.Label(station.DisplayName.ToUpperInvariant(), _smartPopupHeadingStyle, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("X", _smartPopupButtonStyle, GUILayout.Width(36f), GUILayout.Height(28f)))
            {
                CloseProductionStationMenu();
                GUIUtility.ExitGUI();
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Produktionsziele gelten nur fuer diese Station. Angezeigt werden nur Produkte, die diese Station wirklich herstellen kann. Vorprodukte werden NICHT automatisch an anderen Stationen hergestellt.", _smartPopupLabelStyle);
            string operatorHint = ProductionStationRuntime.OperatorHint(station.Kind);
            if (!string.IsNullOrEmpty(operatorHint))
                GUILayout.Label("Worker: " + operatorHint, _smartPopupLabelStyle);
            if (!writable) GUILayout.Label("Nur Lesen: Station ist aktuell nicht lokal network-owned.", _smartPopupLabelStyle);

            _productionStationScroll = GUILayout.BeginScrollView(_productionStationScroll, GUILayout.ExpandHeight(true));
            string[] outputs = ProductionStationRuntime.SupportedOutputs(station);
            if (outputs.Length == 0)
                GUILayout.Label("Fuer diese Station sind aktuell keine passenden Worker-Produktionsrezepte freigeschaltet.", _smartPopupLabelStyle);
            for (int oi = 0; oi < outputs.Length; oi++)
            {
                string output = outputs[oi];
                GUILayout.Space(6f);
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label(ProductionOrderRuntime.DisplayProduct(output), _smartPopupHeadingStyle);
                if (CookingRecipeRuntime.TryGetRecipe(station.Kind, output, out CookingRecipeSpec cookingRecipe))
                    GUILayout.Label("Zutaten: " + CookingRecipeRuntime.DescribeIngredients(cookingRecipe) + $"   |   Ausgabe: x{Math.Max(1, cookingRecipe.OutputAmount)}", _smartPopupLabelStyle);
                else if ((station.Kind == ProductionStationKind.Smelter || station.Kind == ProductionStationKind.BlastFurnace) &&
                         FirekeeperWorkRuntime.TryGetMetalInputForOutput(
                             station.Root != null ? (station.Root.GetComponent<Smelter>() ?? station.Root.GetComponentInParent<Smelter>() ?? station.Root.GetComponentInChildren<Smelter>()) : null,
                             output, out string metalInput))
                    GUILayout.Label("Rohstoff: " + ProductionOrderRuntime.DisplayProduct(metalInput) + " x1   |   Ausgabe: x1", _smartPopupLabelStyle);
                if (ProductionOrderRuntime.TryGetOrder(station.Zdo, output, out ProductionOrderState order))
                {
                    GUILayout.Label(ProductionOrderRuntime.Describe(order), _smartPopupLabelStyle);
                    GUILayout.BeginHorizontal();
                    GUI.enabled = writable;
                    if (order.Mode == ProductionOrderMode.MaintainStock)
                    {
                        if (GUILayout.Button("Bestand halten: " + Math.Max(1, order.Target), _smartPopupButtonStyle, GUILayout.ExpandWidth(true)))
                        {
                            // Keep the familiar multi-mode system available: clicking the large
                            // target button cycles to the next bill mode. +/- remain target edits.
                            ProductionOrderRuntime.CycleMode(station.Zdo, output);
                            GUI.enabled = true;
                            GUIUtility.ExitGUI();
                        }
                    }
                    else if (GUILayout.Button(ProductionOrderRuntime.DisplayMode(order.Mode), _smartPopupButtonStyle, GUILayout.ExpandWidth(true)))
                    {
                        ProductionOrderRuntime.CycleMode(station.Zdo, output);
                        GUI.enabled = true;
                        GUIUtility.ExitGUI();
                    }
                    if (GUILayout.Button("-10", _smartPopupButtonStyle, GUILayout.Width(52f)))
                    {
                        ProductionOrderRuntime.AdjustTarget(station.Zdo, output, -10);
                        GUI.enabled = true;
                        GUIUtility.ExitGUI();
                    }
                    if (GUILayout.Button("-1", _smartPopupButtonStyle, GUILayout.Width(44f)))
                    {
                        ProductionOrderRuntime.AdjustTarget(station.Zdo, output, -1);
                        GUI.enabled = true;
                        GUIUtility.ExitGUI();
                    }
                    if (GUILayout.Button("+1", _smartPopupButtonStyle, GUILayout.Width(44f)))
                    {
                        ProductionOrderRuntime.AdjustTarget(station.Zdo, output, 1);
                        GUI.enabled = true;
                        GUIUtility.ExitGUI();
                    }
                    if (GUILayout.Button("+10", _smartPopupButtonStyle, GUILayout.Width(52f)))
                    {
                        ProductionOrderRuntime.AdjustTarget(station.Zdo, output, 10);
                        GUI.enabled = true;
                        GUIUtility.ExitGUI();
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    if (order.Completed && GUILayout.Button("Auftrag neu starten", _smartPopupButtonStyle))
                    {
                        ProductionOrderRuntime.EnsureOrder(station.Zdo, output, Math.Max(1, order.Target));
                        GUI.enabled = true;
                        GUIUtility.ExitGUI();
                    }
                    if (GUILayout.Button("Auftrag entfernen", _smartPopupButtonStyle))
                    {
                        ProductionOrderRuntime.RemoveOrder(station.Zdo, output);
                        GUI.enabled = true;
                        GUIUtility.ExitGUI();
                    }
                    GUI.enabled = true;
                    GUILayout.EndHorizontal();
                }
                else
                {
                    GUILayout.Label("Kein Produktionsziel.", _smartPopupLabelStyle);
                    GUI.enabled = writable;
                    if (GUILayout.Button("Bestand halten aktivieren", _smartPopupButtonStyle))
                    {
                        ProductionOrderRuntime.SetMaintainStock(station.Zdo, output, ProductionStationRuntime.DefaultTarget(output));
                        GUI.enabled = true;
                        GUIUtility.ExitGUI();
                    }
                    GUI.enabled = true;
                }
                GUILayout.EndVertical();
            }
            GUILayout.EndScrollView();
            GUILayout.Space(6f);
            GUILayout.Label("Zubereitungstisch/Kessel lesen ihre echten ObjectDB-Rezepte; Windmuehle, Steinofen, Schmelzofen und Hochofen verwenden die echten Maschinenrezepte. Koch bzw. Schmied holen Rohstoffe cache-first und produzieren nur solange das eingestellte Ziel unterschritten ist. Der Feuerwart liefert nur Hitze/Brennstoff und verbraucht dabei keine Ressourcen; Erz/Schrott bleibt Sache des Schmieds.", _smartPopupLabelStyle);
            if (GUILayout.Button("Schliessen", _smartPopupButtonStyle))
            {
                CloseProductionStationMenu();
                GUIUtility.ExitGUI();
            }
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 34f));
        }

        private void DrawWorkerContainerMenuWindow(int windowId)
        {
            if (_openWorkerContainer == null)
            {
                GUILayout.Label("Kiste nicht mehr verfuegbar.");
                if (GUILayout.Button("Schliessen")) CloseWorkerContainerMenu();
                return;
            }
            ZNetView view = WorkerContainerRoleRuntime.GetNetworkView(_openWorkerContainer);
            if (view == null || !view.IsValid() || view.GetZDO() == null)
            {
                GUILayout.Label("Kein gueltiger Netzwerkzustand.");
                if (GUILayout.Button("Schliessen")) CloseWorkerContainerMenu();
                return;
            }

            _workerContainerScroll = GUILayout.BeginScrollView(_workerContainerScroll);
            WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(_openWorkerContainer);
            GUILayout.Label($"Aktuelle Rolle: {WorkerContainerRoleRuntime.DisplayName(role)}");
            GUILayout.Label(WorkerContainerRoleRuntime.ShortDescription(role));
            GUILayout.Space(8f);
            GUILayout.Label("Kistentyp");
            DrawContainerRoleButton(view, WorkerContainerRole.Normal, "Normale Lagerkiste");
            DrawContainerRoleButton(view, WorkerContainerRole.FieldDepot, "Felddepot / Arbeitsdepot");
            DrawContainerRoleButton(view, WorkerContainerRole.Import, "Importkiste (Wareneingang)");
            DrawContainerRoleButton(view, WorkerContainerRole.Export, "Exportkiste (Warenausgang)");
            DrawAirFreightRoleButton(view);
            GUILayout.Space(8f);
            bool keepOneEnabled = WorkerContainerRoleRuntime.ProtectLastItem(_openWorkerContainer);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Keep-One: {(keepOneEnabled ? "AN" : "AUS")}");
            if (GUILayout.Button(keepOneEnabled ? "Ausschalten" : "Einschalten", GUILayout.Width(120f), GUILayout.Height(28f)))
            {
                if (WorkerContainerRoleRuntime.SetKeepOne(_openWorkerContainer, !keepOneEnabled))
                    SetStatus($"Keep-One -> {(!keepOneEnabled ? "AN" : "AUS")} fuer diese Kiste.");
                else
                    SetStatus("Keep-One konnte nicht geaendert werden (keine Netzwerk-Ownership).");
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(keepOneEnabled
                ? "Mindestens 1 Stueck je Itemtyp bleibt in dieser Kiste."
                : "Diese Kiste darf bei Transport/Verbrauch vollstaendig geleert werden.");
            if (role == WorkerContainerRole.FieldDepot)
            {
                GUILayout.Space(8f);
                ZDO depotZdo = WorkerContainerRoleRuntime.GetZdo(_openWorkerContainer);
                long depotOwnerId = depotZdo?.GetLong(WorkerContainerRoleRuntime.OwnerKey, 0L) ?? 0L;
                string depotWorkAreaId = FieldDepotWorkzoneRuntime.GetAssignedWorkAreaId(_openWorkerContainer);
                string depotWorkAreaName = FieldDepotWorkzoneRuntime.GetDisplayName(depotWorkAreaId);
                int linkedDepotCount = FieldSupplyDemandCacheRuntime.GetLoadedDepotCountForWorkArea(depotWorkAreaId, depotOwnerId);
                GUILayout.Label("Workzone-Verknuepfung");
                GUILayout.Label(string.IsNullOrEmpty(depotWorkAreaId)
                    ? "Aktuell: Nicht zugewiesen - dieses Depot arbeitet einzeln."
                    : $"Aktuell: {depotWorkAreaName} | verknuepfte geladene Felddepots: {Mathf.Max(1, linkedDepotCount)}");
                GUILayout.Label("Alle Felddepots derselben Workzone bilden einen gemeinsamen logischen Pool. Bedarf gehoert der Workzone; die konkrete Kiste wird erst beim Abholen/Abliefern gewaehlt.");

                if (!string.IsNullOrEmpty(depotWorkAreaId))
                {
                    float now = Time.unscaledTime;
                    if (!string.Equals(_fieldDepotPoolDemandWorkAreaId, depotWorkAreaId, StringComparison.Ordinal) || now >= _fieldDepotPoolDemandRefreshAt)
                    {
                        FieldSupplyDemandCacheRuntime.CollectPoolDemandSnapshots(depotWorkAreaId, depotOwnerId, _fieldDepotPoolDemandRows);
                        _fieldDepotPoolDemandWorkAreaId = depotWorkAreaId;
                        _fieldDepotPoolDemandRefreshAt = now + 1f;
                    }

                    GUILayout.Space(6f);
                    GUILayout.Label("Depot-Pool / offene Bedarfe");
                    if (_fieldDepotPoolDemandRows.Count == 0)
                    {
                        GUILayout.Label("Keine Min-/Zielwerte im verknuepften Depot-Pool.");
                    }
                    else
                    {
                        for (int poolIndex = 0; poolIndex < _fieldDepotPoolDemandRows.Count; poolIndex++)
                        {
                            FieldSupplyDemandCacheRuntime.PoolDemandSnapshot row = _fieldDepotPoolDemandRows[poolIndex];
                            string itemName = GetDepotItemDisplayName(row.Prefab);
                            if (string.IsNullOrWhiteSpace(itemName)) itemName = row.Prefab;
                            string staging = row.StagingClaimed > 0 ? $" | vorbereitet {row.StagingClaimed}" : string.Empty;
                            string transit = row.InTransit > 0 ? $" | unterwegs {row.InTransit}" : string.Empty;
                            string open = row.OpenNeed > 0 ? $" | OFFEN {row.OpenNeed}" : " | gedeckt";
                            GUILayout.Label($"{itemName}: {row.Current}/{row.Target}{staging}{transit}{open}");
                        }
                    }
                    GUILayout.Label("Anzeige kommt nur aus dem Event-/Cache-Index; dafuer wird kein neuer Kisten- oder Welt-Scan gestartet.");
                }
                else
                {
                    _fieldDepotPoolDemandRows.Clear();
                    _fieldDepotPoolDemandWorkAreaId = string.Empty;
                    _fieldDepotPoolDemandRefreshAt = 0f;
                }

                if (FieldDepotWorkzoneRuntime.TrySuggestContainingWorkArea(_openWorkerContainer, out string suggestedZoneId, out string suggestedZoneName) &&
                    !string.Equals(suggestedZoneId, depotWorkAreaId, StringComparison.Ordinal))
                {
                    string suggestionLabel = string.IsNullOrWhiteSpace(suggestedZoneName) ? suggestedZoneId : suggestedZoneName;
                    if (GUILayout.Button($"Naheliegende Workzone zuweisen: {suggestionLabel}", GUILayout.Height(28f)))
                    {
                        if (FieldDepotWorkzoneRuntime.SetAssignedWorkArea(_openWorkerContainer, suggestedZoneId))
                            SetStatus($"Felddepot -> Workzone {suggestionLabel}. Depots dieser Zone sind jetzt logisch verknuepft.");
                        else
                            SetStatus("Workzone konnte nicht zugewiesen werden (Ownership/Workzone nicht verfuegbar).");
                    }
                }

                WorkFlagRegistry.CollectOwnedSnapshots(depotOwnerId, 40f, _openWorkerContainer.transform.position, _fieldDepotWorkzoneOptions);
                if (_fieldDepotWorkzoneOptions.Count > 0)
                {
                    GUILayout.Label("Workzone auswaehlen:");
                    for (int zoneIndex = 0; zoneIndex < _fieldDepotWorkzoneOptions.Count; zoneIndex++)
                    {
                        WorkAreaSnapshot option = _fieldDepotWorkzoneOptions[zoneIndex];
                        if (string.IsNullOrEmpty(option.Id)) continue;
                        string optionName = string.IsNullOrWhiteSpace(option.DisplayName) ? option.Id : option.DisplayName;
                        bool selected = string.Equals(option.Id, depotWorkAreaId, StringComparison.Ordinal);
                        GUI.enabled = !selected;
                        if (GUILayout.Button(selected ? $"[Zugewiesen] {optionName}" : optionName, GUILayout.Height(26f)))
                        {
                            if (FieldDepotWorkzoneRuntime.SetAssignedWorkArea(_openWorkerContainer, option.Id))
                                SetStatus($"Felddepot -> Workzone {optionName}. Verknuepfte Depots teilen sich Bedarf und Zielwahl.");
                            GUI.enabled = true;
                            GUIUtility.ExitGUI();
                        }
                        GUI.enabled = true;
                    }
                }
                if (!string.IsNullOrEmpty(depotWorkAreaId) && GUILayout.Button("Workzone-Zuweisung loesen", GUILayout.Height(26f)))
                {
                    if (FieldDepotWorkzoneRuntime.SetAssignedWorkArea(_openWorkerContainer, string.Empty))
                        SetStatus("Felddepot ist nicht mehr mit einer Workzone verknuepft und arbeitet wieder einzeln.");
                    GUIUtility.ExitGUI();
                }

                GUILayout.Space(8f);
                bool airEnabled = WorkerContainerRoleRuntime.SupportsAirTransport(_openWorkerContainer);
                if (airEnabled)
                {
                    bool clearSky = WorkerContainerRoleRuntime.HasClearSky(_openWorkerContainer);
                    AirDepotMode airMode = WorkerContainerRoleRuntime.GetAirDepotMode(_openWorkerContainer);
                    GUILayout.Label($"Luftfrachtkiste: Import + Export | Freier Himmel: {(clearSky ? "JA" : "NEIN")}");
                    if (!clearSky)
                        GUILayout.Label("Luftfracht blockiert: Ueber der Kiste befindet sich ein Dach/Bauteil. Der Drake nutzt sie erst wieder bei freiem Himmel.");
                    else
                        GUILayout.Label("Diese Kiste ist gleichzeitig Wareneingang UND Warenausgang fuer Drakes. Eingehende Fracht wird von Sortierern eingelagert; angeforderte Ware wird hier fuer den Abflug bereitgestellt.");

                    GUILayout.Space(6f);
                    GUILayout.Label($"Standorttyp: {WorkerContainerRoleRuntime.AirDepotModeDisplayName(airMode)}");
                    GUILayout.BeginHorizontal();
                    GUI.enabled = airMode != AirDepotMode.Outpost;
                    if (GUILayout.Button("Aussenposten", GUILayout.Height(30f)))
                    {
                        WorkerContainerRoleRuntime.SetAirDepotMode(_openWorkerContainer, AirDepotMode.Outpost);
                        SetStatus("Luftfrachtkiste -> Aussenposten.");
                    }
                    GUI.enabled = airMode != AirDepotMode.BaseHub;
                    if (GUILayout.Button("Base-Luftfracht", GUILayout.Height(30f)))
                    {
                        WorkerContainerRoleRuntime.SetAirDepotMode(_openWorkerContainer, AirDepotMode.BaseHub);
                        SetStatus("Luftfrachtkiste -> Base. Import + Export aktiv.");
                    }
                    GUI.enabled = true;
                    GUILayout.EndHorizontal();
                    GUILayout.Label(airMode == AirDepotMode.BaseHub
                        ? "Base-Hub: Drake liefert hier an UND holt hier Rueckfracht ab. Import- und Exportfunktion sind kombiniert."
                        : "Aussenposten-Hub: Drake holt Ueberschuss ab UND liefert angeforderte Versorgung in dieselbe Kiste.");
                }

                GUILayout.Space(6f);
                if (GUILayout.Button("Bestand / Produktion einstellen", GUILayout.Height(34f)))
                {
                    _fieldDepotStockSettingsOpen = true;
                    _fieldDepotStockScroll = Vector2.zero;
                    _fieldDepotItemSearch = string.Empty;
                    _containerReserveEdits.Clear();
                    _containerTriggerEdits.Clear();
                    InvalidateFieldDepotStockUiCache();
                }
                GUILayout.Label(airEnabled
                    ? "Luftfracht kombiniert Felddepot + Import + Export. Pro Item kannst du per MMB Bestand, Min und Max direkt in derselben Kiste setzen."
                    : "Hier stellst du pro Item ein, wie viel am Aussenposten bleiben soll. Welche Pflanzen und Saatpflanzen angebaut werden, bestimmt ausschliesslich deine Feldplanung.");
                GUILayout.Label("Schnellzugriff: Kiste normal mit E oeffnen -> Maus auf Item -> MITTLERE MAUSTASTE. Das funktioniert auch bei normalen Kisten, die per Shift+E zu Felddepot/Luftfracht umgeschaltet wurden.");
            }
            if (role == WorkerContainerRole.Import)
            {
                GUILayout.Space(8f);
                GUILayout.Label("Min/Max direkt am Item einstellen:");
                GUILayout.Label("Importkiste normal mit E oeffnen -> Maus auf den gewuenschten Item-Stack -> MITTLERE MAUSTASTE.");
                GUILayout.Label("Dann erscheint nur fuer dieses Item: Bestand, Min und Max. Es gibt keine Suchliste mehr, die beim Tippen alle bekannten Items scannt.");
                GUILayout.Label("Beispiel Holz: Min 30 / Max 100 -> bei 100 stoppt neue Holzlieferung; erst bei 30 oder weniger wird sie wieder freigegeben.");
            }
            GUILayout.Space(8f);
            GUILayout.Label("Logistics v2: Sammler -> Felddepot -> Transport -> Import -> Sortierer -> Basislager. Rueckweg fuer Bedarf: Basislager -> Sortierer -> Export -> Transport -> Felddepot.");
            GUILayout.EndScrollView();
            if (GUILayout.Button("Schliessen")) CloseWorkerContainerMenu();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
        }

        internal void OpenContainerStockQuickEditor(Container container, ItemDrop.ItemData item)
        {
            if (container == null || item == null) return;
            WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(container);
            if (role != WorkerContainerRole.Import && role != WorkerContainerRole.FieldDepot) return;
            string prefab = WorkerStorageRuntime.GetPrefabName(item);
            if (string.IsNullOrWhiteSpace(prefab)) return;

            _containerStockQuickEditorContainer = container;
            _containerStockQuickPrefab = prefab;
            _containerStockQuickDisplayName = GetDepotItemDisplayName(prefab) ?? prefab;

            bool airFreight = role == WorkerContainerRole.FieldDepot && WorkerContainerRoleRuntime.SupportsAirTransport(container);
            if (role == WorkerContainerRole.Import)
            {
                bool configured = ContainerStockLimitRuntime.HasRule(container, prefab);
                _containerStockQuickKeepText = "0";
                _containerStockQuickMinText = (configured ? ContainerStockLimitRuntime.GetMin(container, prefab) : 0).ToString();
                _containerStockQuickMaxText = (configured ? ContainerStockLimitRuntime.GetMax(container, prefab) : 100).ToString();
                _containerStockQuickMessage = configured ? "Bestehende Import-Regel geladen." : "Noch unbegrenzt. Min/Max setzen und Speichern.";
            }
            else if (airFreight)
            {
                bool configured = ContainerStockLimitRuntime.HasRule(container, prefab);
                _containerStockQuickKeepText = FieldDepotStockRuleRuntime.GetTransportReserve(container, prefab).ToString();
                _containerStockQuickMinText = (configured ? ContainerStockLimitRuntime.GetMin(container, prefab) : 0).ToString();
                _containerStockQuickMaxText = (configured ? ContainerStockLimitRuntime.GetMax(container, prefab) : 100).ToString();
                _containerStockQuickMessage = configured
                    ? "Luftfracht: lokaler Bestand + gemeinsame Workzone-Min/Max geladen."
                    : "Luftfracht: Bestand bleibt lokal; Min/Max gelten fuer alle Air-Kisten der Workzone.";
            }
            else
            {
                _containerStockQuickKeepText = "0";
                _containerStockQuickMinText = FieldDepotStockRuleRuntime.GetTransportReserve(container, prefab).ToString();
                _containerStockQuickMaxText = string.Empty;
                _containerStockQuickMessage = "Felddepot: Bestandsschutz fuer genau dieses Item. Pflanzmengen kommen nur aus geplanten Ghosts.";
            }

            _containerStockQuickEditorOpen = true;
            float width = Mathf.Min(430f, Screen.width - 24f);
            float height = Mathf.Min(350f, Screen.height - 24f);
            float mouseX = Input.mousePosition.x;
            float mouseY = Screen.height - Input.mousePosition.y;
            _containerStockQuickEditorRect = new Rect(
                Mathf.Clamp(mouseX + 18f, 12f, Mathf.Max(12f, Screen.width - width - 12f)),
                Mathf.Clamp(mouseY + 18f, 12f, Mathf.Max(12f, Screen.height - height - 12f)),
                width,
                height);
            SetStatus($"{_containerStockQuickDisplayName}: Schnellbestand geoeffnet ({WorkerContainerRoleRuntime.DisplayName(role)}).");
        }

        private bool IsFieldDepotSeedResourceQuick(string prefab)
        {
            if (string.IsNullOrWhiteSpace(prefab)) return false;
            float now = Time.realtimeSinceStartup;
            if (_containerSeedRuleScratch.Count == 0 || now >= _fieldDepotSeedRuleNextRefresh)
            {
                SmartReplantBridge.CollectSeedProductionResources(_containerSeedRuleScratch);
                _fieldDepotSeedResourceSet.Clear();
                for (int i = 0; i < _containerSeedRuleScratch.Count; i++)
                {
                    string candidate = _containerSeedRuleScratch[i];
                    if (!string.IsNullOrWhiteSpace(candidate)) _fieldDepotSeedResourceSet.Add(candidate);
                }
                _fieldDepotSeedRuleNextRefresh = now + 5f;
            }
            return _fieldDepotSeedResourceSet.Contains(prefab);
        }

        internal void ValidateContainerStockQuickEditor(Container currentContainer)
        {
            if (!_containerStockQuickEditorOpen) return;
            WorkerContainerRole role = currentContainer != null ? WorkerContainerRoleRuntime.GetRole(currentContainer) : WorkerContainerRole.Normal;
            if (currentContainer == null || currentContainer != _containerStockQuickEditorContainer ||
                (role != WorkerContainerRole.Import && role != WorkerContainerRole.FieldDepot))
                CloseContainerStockQuickEditor();
        }

        internal void CloseContainerStockQuickEditor()
        {
            _containerStockQuickEditorOpen = false;
            _containerStockQuickEditorContainer = null;
            _containerStockQuickPrefab = string.Empty;
            _containerStockQuickDisplayName = string.Empty;
            _containerStockQuickMessage = string.Empty;
            GUI.FocusControl(null);
        }

        private void DrawContainerStockQuickEditorWindow(int windowId)
        {
            Container container = _containerStockQuickEditorContainer;
            string prefab = _containerStockQuickPrefab;
            WorkerContainerRole role = container != null ? WorkerContainerRoleRuntime.GetRole(container) : WorkerContainerRole.Normal;
            if (container == null || string.IsNullOrWhiteSpace(prefab) ||
                (role != WorkerContainerRole.Import && role != WorkerContainerRole.FieldDepot))
            {
                CloseContainerStockQuickEditor();
                return;
            }

            bool airFreight = role == WorkerContainerRole.FieldDepot && WorkerContainerRoleRuntime.SupportsAirTransport(container);

            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            string heading = role == WorkerContainerRole.Import
                ? "IMPORT — ITEM-BESTAND"
                : (airFreight ? "LUFTFRACHT — WORKZONE-BESTAND" : "FELDDEPOT — ITEM-BESTAND");
            GUILayout.Label(heading, _smartPopupHeadingStyle, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("X", _smartPopupButtonStyle, GUILayout.Width(36f), GUILayout.Height(28f)))
            {
                CloseContainerStockQuickEditor();
                GUIUtility.ExitGUI();
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);
            GUILayout.Label(_containerStockQuickDisplayName, _smartPopupHeadingStyle);
            int current = airFreight
                ? ContainerStockLimitRuntime.GetCurrentStock(container, prefab)
                : WorkerStorageRuntime.CountPrefab(container.GetInventory(), prefab);

            if (role == WorkerContainerRole.Import)
            {
                bool configured = ContainerStockLimitRuntime.HasRule(container, prefab);
                bool paused = configured && ContainerStockLimitRuntime.IsPaused(container, prefab);
                GUILayout.Label($"Bestand: {current}" + (configured ? $"   |   Lieferung: {(paused ? "STOPP" : "FREI")}" : "   |   Unbegrenzt"), _smartPopupLabelStyle);
                GUILayout.Space(8f);

                GUILayout.BeginHorizontal();
                GUILayout.Label("Min", GUILayout.Width(80f));
                _containerStockQuickMinText = GUILayout.TextField(_containerStockQuickMinText ?? "0", GUILayout.Width(110f));
                GUILayout.Label("Ab hier wird nach einem Max-Stopp wieder geliefert.", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("Max", GUILayout.Width(80f));
                _containerStockQuickMaxText = GUILayout.TextField(_containerStockQuickMaxText ?? "100", GUILayout.Width(110f));
                GUILayout.Label("Harte Obergrenze fuer automatische Lieferungen.", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();

                GUILayout.Space(8f);
                if (!string.IsNullOrEmpty(_containerStockQuickMessage)) GUILayout.Label(_containerStockQuickMessage, _smartPopupLabelStyle);
                GUILayout.FlexibleSpace();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Speichern", _smartPopupButtonStyle, GUILayout.Height(34f)))
                {
                    if (!int.TryParse(_containerStockQuickMinText, out int minimum) || minimum < 0)
                        _containerStockQuickMessage = "Min muss eine Zahl ab 0 sein.";
                    else if (!int.TryParse(_containerStockQuickMaxText, out int maximum) || maximum <= 0)
                        _containerStockQuickMessage = "Max muss eine Zahl groesser 0 sein.";
                    else if (minimum >= maximum)
                        _containerStockQuickMessage = "Min muss kleiner als Max sein.";
                    else if (ContainerStockLimitRuntime.SetRule(container, prefab, minimum, maximum))
                    {
                        _containerStockQuickMinText = ContainerStockLimitRuntime.GetMin(container, prefab).ToString();
                        _containerStockQuickMaxText = ContainerStockLimitRuntime.GetMax(container, prefab).ToString();
                        _containerStockQuickMessage = $"Gespeichert: Min {_containerStockQuickMinText} / Max {_containerStockQuickMaxText}.";
                        SetStatus($"{_containerStockQuickDisplayName}: Min {_containerStockQuickMinText} / Max {_containerStockQuickMaxText}.");
                    }
                    else _containerStockQuickMessage = "Speichern fehlgeschlagen (Netzwerk-Ownership pruefen).";
                }

                GUI.enabled = configured;
                if (GUILayout.Button("Regel entfernen", _smartPopupButtonStyle, GUILayout.Height(34f)))
                {
                    if (ContainerStockLimitRuntime.RemoveRule(container, prefab))
                    {
                        _containerStockQuickMinText = "0";
                        _containerStockQuickMaxText = "100";
                        _containerStockQuickMessage = "Regel entfernt: dieses Item ist wieder unbegrenzt.";
                        SetStatus($"{_containerStockQuickDisplayName}: Min/Max entfernt.");
                    }
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            else if (airFreight)
            {
                int reserve = FieldDepotStockRuleRuntime.GetTransportReserve(container, prefab);
                bool configured = ContainerStockLimitRuntime.HasRule(container, prefab);
                bool paused = configured && ContainerStockLimitRuntime.IsPaused(container, prefab);
                string airScope = ContainerStockLimitRuntime.GetScopeDisplayName(container);
                GUILayout.Label($"Workzone gesamt: {current}   |   lokaler Bestandsschutz: {reserve}" + (configured ? $"   |   Import: {(paused ? "STOPP" : "FREI")}" : "   |   Import: unbegrenzt"), _smartPopupLabelStyle);
                GUILayout.Label($"Min/Max gelten gemeinsam fuer alle Luftfrachtkisten in: {airScope}", _smartPopupLabelStyle);
                GUILayout.Space(8f);

                GUILayout.BeginHorizontal();
                GUILayout.Label("Bestand", GUILayout.Width(80f));
                _containerStockQuickKeepText = GUILayout.TextField(_containerStockQuickKeepText ?? "0", GUILayout.Width(110f));
                GUILayout.Label("Lokaler Schutz nur fuer diese Kiste; Min/Max darunter gelten Workzone-weit.", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("Min", GUILayout.Width(80f));
                _containerStockQuickMinText = GUILayout.TextField(_containerStockQuickMinText ?? "0", GUILayout.Width(110f));
                GUILayout.Label("Nach Max-Stopp werden neue Lieferungen erst ab diesem Bestand wieder freigegeben.", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("Max", GUILayout.Width(80f));
                _containerStockQuickMaxText = GUILayout.TextField(_containerStockQuickMaxText ?? "100", GUILayout.Width(110f));
                GUILayout.Label("Harte Obergrenze fuer Boden- und Luftfracht ueber ALLE Air-Kisten dieser Workzone zusammen.", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();

                GUILayout.Space(8f);
                if (!string.IsNullOrEmpty(_containerStockQuickMessage)) GUILayout.Label(_containerStockQuickMessage, _smartPopupLabelStyle);
                GUILayout.FlexibleSpace();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Speichern", _smartPopupButtonStyle, GUILayout.Height(34f)))
                {
                    if (!int.TryParse(_containerStockQuickKeepText, out int keep) || keep < 0)
                        _containerStockQuickMessage = "Bestand muss eine Zahl ab 0 sein.";
                    else if (!int.TryParse(_containerStockQuickMinText, out int minimum) || minimum < 0)
                        _containerStockQuickMessage = "Min muss eine Zahl ab 0 sein.";
                    else if (!int.TryParse(_containerStockQuickMaxText, out int maximum) || maximum <= 0)
                        _containerStockQuickMessage = "Max muss eine Zahl groesser 0 sein.";
                    else if (minimum >= maximum)
                        _containerStockQuickMessage = "Min muss kleiner als Max sein.";
                    else if (!WorkerStorageRuntime.TryOwn(container))
                        _containerStockQuickMessage = "Speichern fehlgeschlagen (keine Netzwerk-Ownership).";
                    else if (!FieldDepotStockRuleRuntime.SetTransportReserve(container, prefab, keep))
                        _containerStockQuickMessage = "Bestand konnte nicht gespeichert werden.";
                    else if (!ContainerStockLimitRuntime.SetRule(container, prefab, minimum, maximum))
                        _containerStockQuickMessage = "Min/Max konnten nicht gespeichert werden.";
                    else
                    {
                        _containerStockQuickKeepText = FieldDepotStockRuleRuntime.GetTransportReserve(container, prefab).ToString();
                        _containerStockQuickMinText = ContainerStockLimitRuntime.GetMin(container, prefab).ToString();
                        _containerStockQuickMaxText = ContainerStockLimitRuntime.GetMax(container, prefab).ToString();
                        _containerStockQuickMessage = $"Gespeichert: lokaler Bestand {_containerStockQuickKeepText} / gemeinsame Workzone Min {_containerStockQuickMinText} / Max {_containerStockQuickMaxText}.";
                        SetStatus($"{_containerStockQuickDisplayName}: Luftfracht Bestand {_containerStockQuickKeepText}, Min {_containerStockQuickMinText}, Max {_containerStockQuickMaxText}.");
                    }
                }

                GUI.enabled = configured;
                if (GUILayout.Button("Min/Max entfernen", _smartPopupButtonStyle, GUILayout.Height(34f)))
                {
                    if (ContainerStockLimitRuntime.RemoveRule(container, prefab))
                    {
                        _containerStockQuickMinText = "0";
                        _containerStockQuickMaxText = "100";
                        _containerStockQuickMessage = $"Min/Max entfernt. Bestandsschutz {_containerStockQuickKeepText} bleibt bestehen.";
                        SetStatus($"{_containerStockQuickDisplayName}: Luftfracht Min/Max entfernt; Bestandsschutz bleibt.");
                    }
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            else
            {
                int reserve = FieldDepotStockRuleRuntime.GetTransportReserve(container, prefab);
                GUILayout.Label($"Bestand: {current}   |   Geschuetzt: {reserve}", _smartPopupLabelStyle);
                GUILayout.Space(8f);

                GUILayout.BeginHorizontal();
                GUILayout.Label("Bestand", GUILayout.Width(80f));
                _containerStockQuickMinText = GUILayout.TextField(_containerStockQuickMinText ?? "0", GUILayout.Width(110f));
                GUILayout.Label("So viel bleibt in genau diesem Felddepot vor Transport zur Base geschuetzt.", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();

                GUILayout.Space(8f);
                if (!string.IsNullOrEmpty(_containerStockQuickMessage)) GUILayout.Label(_containerStockQuickMessage, _smartPopupLabelStyle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Speichern", _smartPopupButtonStyle, GUILayout.Height(34f)))
                {
                    if (!int.TryParse(_containerStockQuickMinText, out int keep) || keep < 0)
                    {
                        _containerStockQuickMessage = "Bestand muss eine Zahl ab 0 sein.";
                    }
                    else if (!FieldDepotStockRuleRuntime.SetTransportReserve(container, prefab, keep))
                    {
                        _containerStockQuickMessage = "Speichern fehlgeschlagen (Netzwerk-Ownership pruefen).";
                    }
                    else
                    {
                        _containerStockQuickMinText = FieldDepotStockRuleRuntime.GetTransportReserve(container, prefab).ToString();
                        _containerStockQuickMessage = $"Gespeichert: Bestand {_containerStockQuickMinText}.";
                        SetStatus($"{_containerStockQuickDisplayName}: Felddepot Bestand {_containerStockQuickMinText}.");
                    }
                }
            }

            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 34f));
        }

        private void DrawContainerStockLimitSettingsWindow(int windowId)
        {
            Container container = _openWorkerContainer;
            if (container == null || WorkerContainerRoleRuntime.GetRole(container) != WorkerContainerRole.Import)
            {
                _containerStockLimitSettingsOpen = false;
                return;
            }

            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            GUILayout.Label("IMPORT — MIN / MAX BESTAND", _smartPopupHeadingStyle, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("X", _smartPopupButtonStyle, GUILayout.Width(36f), GUILayout.Height(28f)))
            {
                _containerStockLimitSettingsOpen = false;
                GUIUtility.ExitGUI();
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("Max = harte Obergrenze fuer automatische Lieferungen in DIESE Importkiste. Sobald Max erreicht ist, wird dieses Item fuer neue Transporte gesperrt.", _smartPopupLabelStyle);
            GUILayout.Label("Min = Wiedereinschaltgrenze. Nach einem Max-Stopp werden neue Lieferungen erst wieder freigegeben, wenn der Bestand auf Min oder darunter gefallen ist. Dadurch pendeln Transporter nicht bei jedem einzelnen sortierten Item sofort wieder los.", _smartPopupLabelStyle);
            GUILayout.Space(6f);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Item suchen", _smartPopupLabelStyle, GUILayout.Width(90f));
            string newSearch = GUILayout.TextField(_containerStockLimitSearch ?? string.Empty, GUILayout.ExpandWidth(true));
            if (!string.Equals(newSearch, _containerStockLimitSearch, StringComparison.Ordinal))
            {
                _containerStockLimitSearch = newSearch;
                InvalidateContainerStockLimitUiCache();
            }
            GUI.enabled = !string.IsNullOrWhiteSpace(_containerStockLimitSearch);
            if (GUILayout.Button("X", _smartPopupButtonStyle, GUILayout.Width(34f), GUILayout.Height(24f)))
            {
                _containerStockLimitSearch = string.Empty;
                GUI.FocusControl(null);
                InvalidateContainerStockLimitUiCache();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("Ohne Suche siehst du Items, die bereits in dieser Importkiste liegen oder hier konfiguriert sind. Mit Suche kannst du jede bekannte Valheim-/Mod-Ressource vorab konfigurieren.", _smartPopupLabelStyle);
            EnsureDepotItemSearchIndexProgress(24);
            if (_depotItemSearchNextSourceIndex < _depotItemSearchSourceCount)
                GUILayout.Label($"Item-Index wird im Hintergrund aufgebaut: {_depotItemSearchNextSourceIndex}/{_depotItemSearchSourceCount}", _smartPopupLabelStyle);
            GUILayout.Space(6f);

            _containerStockLimitScroll = GUILayout.BeginScrollView(_containerStockLimitScroll, GUILayout.ExpandHeight(true));
            DrawContainerStockLimitRules(container);
            GUILayout.EndScrollView();

            GUILayout.Space(6f);
            if (GUILayout.Button("Fertig", _smartPopupButtonStyle, GUILayout.Height(32f)))
            {
                _containerStockLimitSettingsOpen = false;
                GUIUtility.ExitGUI();
            }
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 34f));
        }

        private void DrawContainerStockLimitRules(Container container)
        {
            if (container == null) return;
            Inventory inventory = container.GetInventory();
            if (inventory == null) return;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(container);
            string containerId = zdo != null ? zdo.m_uid.ToString() : container.GetInstanceID().ToString();
            string search = (_containerStockLimitSearch ?? string.Empty).Trim();
            EnsureContainerStockLimitUiCache(container, inventory, containerId, search);

            if (_containerStockLimitUiEntries.Count == 0)
            {
                GUILayout.Label(string.IsNullOrEmpty(search)
                    ? "Noch keine Items in der Importkiste und keine Min/Max-Regeln konfiguriert."
                    : $"Kein bekanntes Item fuer '{search}' gefunden.", _smartPopupLabelStyle);
                return;
            }

            for (int i = 0; i < _containerStockLimitUiEntries.Count; i++)
            {
                ContainerStockLimitUiEntry entry = _containerStockLimitUiEntries[i];
                string prefab = entry.Prefab;
                GUILayout.BeginVertical("box");
                string itemLabel = string.Equals(entry.DisplayName, prefab, StringComparison.OrdinalIgnoreCase)
                    ? prefab
                    : $"{entry.DisplayName}  ({prefab})";
                string state = entry.Configured
                    ? entry.Paused ? " | Lieferung: STOPP bis Min" : " | Lieferung: FREI"
                    : " | unbegrenzt";
                GUILayout.Label($"{itemLabel}   |   Bestand: {entry.Current}{state}");

                if (!entry.Configured)
                {
                    if (GUILayout.Button("Min/Max aktivieren (0 / 100)", GUILayout.Height(28f)))
                    {
                        if (ContainerStockLimitRuntime.SetRule(container, prefab, 0, 100))
                        {
                            _containerStockMinEdits[containerId + ":min:" + prefab] = "0";
                            _containerStockMaxEdits[containerId + ":max:" + prefab] = "100";
                            InvalidateContainerStockLimitUiCache();
                            SetStatus($"{prefab}: Import Min 0 / Max 100 aktiviert.");
                            GUIUtility.ExitGUI();
                        }
                    }
                    GUILayout.EndVertical();
                    continue;
                }

                string minKey = containerId + ":min:" + prefab;
                string maxKey = containerId + ":max:" + prefab;
                if (!_containerStockMinEdits.TryGetValue(minKey, out string minText)) minText = entry.Minimum.ToString();
                if (!_containerStockMaxEdits.TryGetValue(maxKey, out string maxText)) maxText = entry.Maximum.ToString();

                GUILayout.BeginHorizontal();
                GUILayout.Label("Minimum", GUILayout.Width(100f));
                if (GUILayout.Button("-10", GUILayout.Width(48f))) AdjustContainerStockMinimum(container, prefab, -10, minKey, maxKey);
                if (GUILayout.Button("-1", GUILayout.Width(42f))) AdjustContainerStockMinimum(container, prefab, -1, minKey, maxKey);
                minText = _containerStockMinEdits.TryGetValue(minKey, out string editedMin) ? editedMin : minText;
                string newMinText = GUILayout.TextField(minText, GUILayout.Width(72f));
                if (!string.Equals(newMinText, minText, StringComparison.Ordinal))
                {
                    _containerStockMinEdits[minKey] = newMinText;
                    ApplyContainerStockMinimumText(container, prefab, newMinText, minKey, maxKey);
                }
                else _containerStockMinEdits[minKey] = minText;
                if (GUILayout.Button("+1", GUILayout.Width(42f))) AdjustContainerStockMinimum(container, prefab, 1, minKey, maxKey);
                if (GUILayout.Button("+10", GUILayout.Width(48f))) AdjustContainerStockMinimum(container, prefab, 10, minKey, maxKey);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("Maximum", GUILayout.Width(100f));
                if (GUILayout.Button("-10", GUILayout.Width(48f))) AdjustContainerStockMaximum(container, prefab, -10, minKey, maxKey);
                if (GUILayout.Button("-1", GUILayout.Width(42f))) AdjustContainerStockMaximum(container, prefab, -1, minKey, maxKey);
                maxText = _containerStockMaxEdits.TryGetValue(maxKey, out string editedMax) ? editedMax : maxText;
                string newMaxText = GUILayout.TextField(maxText, GUILayout.Width(72f));
                if (!string.Equals(newMaxText, maxText, StringComparison.Ordinal))
                {
                    _containerStockMaxEdits[maxKey] = newMaxText;
                    ApplyContainerStockMaximumText(container, prefab, newMaxText, minKey, maxKey);
                }
                else _containerStockMaxEdits[maxKey] = maxText;
                if (GUILayout.Button("+1", GUILayout.Width(42f))) AdjustContainerStockMaximum(container, prefab, 1, minKey, maxKey);
                if (GUILayout.Button("+10", GUILayout.Width(48f))) AdjustContainerStockMaximum(container, prefab, 10, minKey, maxKey);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label($"Hysterese: STOPP bei >= {entry.Maximum}; wieder FREI bei <= {entry.Minimum}.", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                if (GUILayout.Button("Regel entfernen", _smartPopupButtonStyle, GUILayout.Width(130f), GUILayout.Height(28f)))
                {
                    if (ContainerStockLimitRuntime.RemoveRule(container, prefab))
                    {
                        _containerStockMinEdits.Remove(minKey);
                        _containerStockMaxEdits.Remove(maxKey);
                        InvalidateContainerStockLimitUiCache();
                        SetStatus($"{prefab}: Import Min/Max entfernt -> wieder unbegrenzt.");
                        GUIUtility.ExitGUI();
                    }
                }
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
        }

        private void EnsureContainerStockLimitUiCache(Container container, Inventory inventory, string containerId, string search)
        {
            float now = Time.realtimeSinceStartup;
            string query = search ?? string.Empty;
            bool sameTarget = string.Equals(_containerStockLimitUiContainerId, containerId, StringComparison.Ordinal) &&
                              string.Equals(_containerStockLimitUiSearch, query, StringComparison.Ordinal);
            if (sameTarget && now < _containerStockLimitUiNextRefresh) return;

            _containerStockLimitUiContainerId = containerId ?? string.Empty;
            _containerStockLimitUiSearch = query;
            _containerStockLimitUiNextRefresh = now + 0.5f;
            _containerStockLimitUiEntries.Clear();

            var counts = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            System.Collections.Generic.List<ItemDrop.ItemData> items = inventory.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (item == null || item.m_stack <= 0) continue;
                string prefab = WorkerStorageRuntime.GetPrefabName(item);
                if (string.IsNullOrEmpty(prefab)) continue;
                names.Add(prefab);
                if (counts.TryGetValue(prefab, out int old)) counts[prefab] = old + item.m_stack;
                else counts[prefab] = item.m_stack;
            }

            ContainerStockLimitRuntime.CollectConfiguredPrefabs(container, _containerConfiguredRuleScratch);
            for (int i = 0; i < _containerConfiguredRuleScratch.Count; i++) names.Add(_containerConfiguredRuleScratch[i]);

            if (!string.IsNullOrEmpty(query))
            {
                EnsureFieldDepotKnownSearchCache(query);
                for (int i = 0; i < _fieldDepotKnownSearchPrefabs.Count; i++) names.Add(_fieldDepotKnownSearchPrefabs[i]);
            }

            foreach (string prefab in names)
            {
                if (!string.IsNullOrEmpty(query) && !DepotItemMatchesSearch(prefab, query)) continue;
                string displayName;
                if (!_fieldDepotDisplayNameCache.TryGetValue(prefab, out displayName))
                {
                    displayName = GetDepotItemDisplayName(prefab);
                    _fieldDepotDisplayNameCache[prefab] = displayName ?? prefab;
                }

                bool configured = ContainerStockLimitRuntime.HasRule(container, prefab);
                _containerStockLimitUiEntries.Add(new ContainerStockLimitUiEntry
                {
                    Prefab = prefab,
                    DisplayName = displayName ?? prefab,
                    Current = counts.TryGetValue(prefab, out int current) ? current : 0,
                    Configured = configured,
                    Minimum = configured ? ContainerStockLimitRuntime.GetMin(container, prefab) : 0,
                    Maximum = configured ? ContainerStockLimitRuntime.GetMax(container, prefab) : 0,
                    Paused = configured && ContainerStockLimitRuntime.IsPaused(container, prefab)
                });
            }

            _containerStockLimitUiEntries.Sort((a, b) =>
                string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        }

        private void InvalidateContainerStockLimitUiCache()
        {
            _containerStockLimitUiNextRefresh = 0f;
        }

        private void ApplyContainerStockMinimumText(Container container, string prefab, string text, string minKey, string maxKey)
        {
            if (!int.TryParse(text, out int minimum) || minimum < 0) return;
            int maximum = ContainerStockLimitRuntime.GetMax(container, prefab);
            if (_containerStockMaxEdits.TryGetValue(maxKey, out string maxText) && int.TryParse(maxText, out int editedMax) && editedMax > 0)
                maximum = editedMax;
            if (maximum <= 0) return;
            if (!ContainerStockLimitRuntime.SetRule(container, prefab, minimum, maximum)) return;
            _containerStockMinEdits[minKey] = ContainerStockLimitRuntime.GetMin(container, prefab).ToString();
            _containerStockMaxEdits[maxKey] = ContainerStockLimitRuntime.GetMax(container, prefab).ToString();
            InvalidateContainerStockLimitUiCache();
        }

        private void ApplyContainerStockMaximumText(Container container, string prefab, string text, string minKey, string maxKey)
        {
            if (!int.TryParse(text, out int maximum) || maximum <= 0) return;
            int minimum = ContainerStockLimitRuntime.GetMin(container, prefab);
            if (_containerStockMinEdits.TryGetValue(minKey, out string minText) && int.TryParse(minText, out int editedMin) && editedMin >= 0)
                minimum = editedMin;
            if (!ContainerStockLimitRuntime.SetRule(container, prefab, minimum, maximum)) return;
            _containerStockMinEdits[minKey] = ContainerStockLimitRuntime.GetMin(container, prefab).ToString();
            _containerStockMaxEdits[maxKey] = ContainerStockLimitRuntime.GetMax(container, prefab).ToString();
            InvalidateContainerStockLimitUiCache();
        }

        private void AdjustContainerStockMinimum(Container container, string prefab, int delta, string minKey, string maxKey)
        {
            int current = ContainerStockLimitRuntime.GetMin(container, prefab);
            if (_containerStockMinEdits.TryGetValue(minKey, out string text) && int.TryParse(text, out int edited)) current = edited;
            int maximum = ContainerStockLimitRuntime.GetMax(container, prefab);
            if (_containerStockMaxEdits.TryGetValue(maxKey, out string maxText) && int.TryParse(maxText, out int editedMax) && editedMax > 0) maximum = editedMax;
            if (maximum <= 0) maximum = 100;
            int next = Mathf.Clamp(current + delta, 0, Mathf.Max(0, maximum - 1));
            if (ContainerStockLimitRuntime.SetRule(container, prefab, next, maximum))
            {
                _containerStockMinEdits[minKey] = ContainerStockLimitRuntime.GetMin(container, prefab).ToString();
                _containerStockMaxEdits[maxKey] = ContainerStockLimitRuntime.GetMax(container, prefab).ToString();
                InvalidateContainerStockLimitUiCache();
            }
        }

        private void AdjustContainerStockMaximum(Container container, string prefab, int delta, string minKey, string maxKey)
        {
            int current = ContainerStockLimitRuntime.GetMax(container, prefab);
            if (_containerStockMaxEdits.TryGetValue(maxKey, out string text) && int.TryParse(text, out int edited) && edited > 0) current = edited;
            if (current <= 0) current = 100;
            int next = Mathf.Max(1, current + delta);
            int minimum = ContainerStockLimitRuntime.GetMin(container, prefab);
            if (_containerStockMinEdits.TryGetValue(minKey, out string minText) && int.TryParse(minText, out int editedMin) && editedMin >= 0) minimum = editedMin;
            if (ContainerStockLimitRuntime.SetRule(container, prefab, minimum, next))
            {
                _containerStockMinEdits[minKey] = ContainerStockLimitRuntime.GetMin(container, prefab).ToString();
                _containerStockMaxEdits[maxKey] = ContainerStockLimitRuntime.GetMax(container, prefab).ToString();
                InvalidateContainerStockLimitUiCache();
            }
        }

        private void DrawFieldDepotStockSettingsWindow(int windowId)
        {
            Container depot = _openWorkerContainer;
            if (depot == null || WorkerContainerRoleRuntime.GetRole(depot) != WorkerContainerRole.FieldDepot)
            {
                _fieldDepotStockSettingsOpen = false;
                return;
            }

            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            GUILayout.Label("FIELDDEPOT — BESTAND & PRODUKTION", _smartPopupHeadingStyle, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("X", _smartPopupButtonStyle, GUILayout.Width(36f), GUILayout.Height(28f)))
            {
                _containerReserveEdits.Clear();
                _fieldDepotStockSettingsOpen = false;
                GUIUtility.ExitGUI();
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("Keep Items = dieser Bestand bleibt in GENAU DIESEM Felddepot vor Transport zur Base geschuetzt. 0 bedeutet: nichts fuer den Transport reservieren.", _smartPopupLabelStyle);
            GUILayout.Label("Lokale Worker duerfen Keep Items weiterhin verbrauchen. Anbau und Saatproduktion richten sich nur nach den geplanten Feld-Ghosts.", _smartPopupLabelStyle);
            GUILayout.Label("Aenderungen an Keep Items werden erst mit SPEICHERN in die Feldkiste uebernommen.", _smartPopupLabelStyle);
            GUILayout.Space(6f);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Bestand kopieren", _smartPopupButtonStyle, GUILayout.Height(32f)))
            {
                _fieldDepotStockClipboard = FieldDepotStockRuleRuntime.CapturePolicy(depot);
                if (_fieldDepotStockClipboard != null)
                    SetStatus($"Bestandseinstellungen kopiert: {_fieldDepotStockClipboard.ConfiguredItemCount} Item-Regeln.");
                else
                    SetStatus("Bestandseinstellungen konnten nicht kopiert werden.");
            }

            bool hadClipboard = _fieldDepotStockClipboard != null;
            GUI.enabled = hadClipboard;
            string pasteLabel = hadClipboard
                ? $"Bestand einfuegen ({_fieldDepotStockClipboard.ConfiguredItemCount})"
                : "Bestand einfuegen";
            if (GUILayout.Button(pasteLabel, _smartPopupButtonStyle, GUILayout.Height(32f)))
            {
                if (FieldDepotStockRuleRuntime.ApplyPolicy(depot, _fieldDepotStockClipboard))
                {
                    _containerReserveEdits.Clear();
                    _containerTriggerEdits.Clear();
                    InvalidateFieldDepotStockUiCache();
                    SetStatus($"Bestandseinstellungen eingefuegt: {_fieldDepotStockClipboard.ConfiguredItemCount} Item-Regeln vollstaendig uebernommen.");
                }
                else
                {
                    SetStatus("Bestandseinstellungen konnten nicht eingefuegt werden (keine Netzwerk-Ownership?).");
                }
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("Kopiert die Keep-Items-Einstellungen fuer alle konfigurierten Items. Kisteninhalt, Kistenrolle und Keep-One werden nicht kopiert.", _smartPopupLabelStyle);
            GUILayout.Space(6f);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Item suchen", _smartPopupLabelStyle, GUILayout.Width(90f));
            _fieldDepotItemSearch = GUILayout.TextField(_fieldDepotItemSearch ?? string.Empty, GUILayout.ExpandWidth(true));
            GUI.enabled = !string.IsNullOrWhiteSpace(_fieldDepotItemSearch);
            if (GUILayout.Button("X", _smartPopupButtonStyle, GUILayout.Width(34f), GUILayout.Height(24f)))
            {
                _fieldDepotItemSearch = string.Empty;
                GUI.FocusControl(null);
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("Die Suche findet alle bekannten Valheim-/Mod-Items aus ObjectDB – auch wenn aktuell 0 davon in dieser Feldkiste liegen.", _smartPopupLabelStyle);
            EnsureDepotItemSearchIndexProgress(24);
            if (_depotItemSearchNextSourceIndex < _depotItemSearchSourceCount)
                GUILayout.Label($"Item-Index wird im Hintergrund aufgebaut: {_depotItemSearchNextSourceIndex}/{_depotItemSearchSourceCount}", _smartPopupLabelStyle);
            GUILayout.Space(6f);

            _fieldDepotStockScroll = GUILayout.BeginScrollView(_fieldDepotStockScroll, GUILayout.ExpandHeight(true));
            DrawFieldDepotStockRules(depot);
            GUILayout.EndScrollView();

            GUILayout.Space(6f);
            int pendingFieldDepotChanges = CountPendingFieldDepotReserveEdits(depot);
            GUILayout.Label(pendingFieldDepotChanges > 0
                ? $"Ungespeicherte Aenderungen: {pendingFieldDepotChanges}"
                : "Keine ungespeicherten Aenderungen.", _smartPopupLabelStyle);
            GUILayout.BeginHorizontal();
            GUI.enabled = pendingFieldDepotChanges > 0;
            if (GUILayout.Button("Speichern", _smartPopupButtonStyle, GUILayout.Height(34f)))
            {
                if (TrySaveFieldDepotReserveEdits(depot, out string saveMessage))
                    SetStatus(saveMessage);
                else
                    SetStatus(saveMessage);
            }
            GUI.enabled = true;
            if (GUILayout.Button("Schliessen", _smartPopupButtonStyle, GUILayout.Height(34f)))
            {
                if (pendingFieldDepotChanges > 0) SetStatus("Ungespeicherte Feldkisten-Aenderungen verworfen.");
                _containerReserveEdits.Clear();
                _fieldDepotStockSettingsOpen = false;
                GUIUtility.ExitGUI();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 34f));
        }

        private void DrawFieldDepotStockRules(Container depot)
        {
            if (depot == null) return;
            Inventory inventory = depot.GetInventory();
            if (inventory == null) return;

            GUILayout.Space(4f);

            ZDO depotPolicyZdo = WorkerContainerRoleRuntime.GetZdo(depot);
            string depotPolicyId = depotPolicyZdo != null ? depotPolicyZdo.m_uid.ToString() : depot.GetInstanceID().ToString();
            string itemSearch = (_fieldDepotItemSearch ?? string.Empty).Trim();

            EnsureFieldDepotStockUiCache(depot, inventory, depotPolicyId, itemSearch);
            if (_fieldDepotStockUiEntries.Count == 0)
            {
                GUILayout.Label(string.IsNullOrEmpty(itemSearch)
                    ? "Noch keine Items im Felddepot."
                    : $"Kein bekanntes Item fuer '{itemSearch}' gefunden.");
                return;
            }

            for (int i = 0; i < _fieldDepotStockUiEntries.Count; i++)
            {
                FieldDepotStockUiEntry entry = _fieldDepotStockUiEntries[i];
                string prefab = entry.Prefab;
                int current = entry.Current;
                int reserve = entry.Reserve;

                GUILayout.BeginVertical("box");
                string itemLabel = string.Equals(entry.DisplayName, prefab, StringComparison.OrdinalIgnoreCase)
                    ? prefab
                    : $"{entry.DisplayName}  ({prefab})";
                GUILayout.Label($"{itemLabel}   |   Bestand: {current}");

                string reserveKey = depotPolicyId + ":reserve:" + prefab;
                if (!_containerReserveEdits.TryGetValue(reserveKey, out string reserveText))
                    reserveText = reserve.ToString();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Keep Items", GUILayout.Width(130f));
                if (GUILayout.Button("-10", GUILayout.Width(48f))) AdjustDepotReserve(depot, prefab, -10, reserveKey);
                if (GUILayout.Button("-1", GUILayout.Width(42f))) AdjustDepotReserve(depot, prefab, -1, reserveKey);
                reserveText = _containerReserveEdits.TryGetValue(reserveKey, out string editedReserve) ? editedReserve : reserveText;
                string newReserveText = GUILayout.TextField(reserveText, GUILayout.Width(72f));
                if (!string.Equals(newReserveText, reserveText, StringComparison.Ordinal))
                    _containerReserveEdits[reserveKey] = newReserveText;
                if (GUILayout.Button("+1", GUILayout.Width(42f))) AdjustDepotReserve(depot, prefab, 1, reserveKey);
                if (GUILayout.Button("+10", GUILayout.Width(48f))) AdjustDepotReserve(depot, prefab, 10, reserveKey);
                GUILayout.EndHorizontal();

                GUILayout.EndVertical();
            }
        }

        private void EnsureFieldDepotStockUiCache(Container depot, Inventory inventory, string depotPolicyId, string itemSearch)
        {
            float now = Time.realtimeSinceStartup;
            string search = itemSearch ?? string.Empty;
            bool sameTarget = string.Equals(_fieldDepotStockUiDepotId, depotPolicyId, StringComparison.Ordinal) &&
                              string.Equals(_fieldDepotStockUiSearch, search, StringComparison.Ordinal);
            if (sameTarget && now < _fieldDepotStockUiNextRefresh) return;

            _fieldDepotStockUiDepotId = depotPolicyId ?? string.Empty;
            _fieldDepotStockUiSearch = search;
            _fieldDepotStockUiNextRefresh = now + 0.75f;
            _fieldDepotStockUiEntries.Clear();

            var counts = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            System.Collections.Generic.List<ItemDrop.ItemData> items = inventory.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (item == null || item.m_stack <= 0) continue;
                string prefab = WorkerStorageRuntime.GetPrefabName(item);
                if (string.IsNullOrEmpty(prefab)) continue;
                names.Add(prefab);
                if (counts.TryGetValue(prefab, out int oldCount))
                    counts[prefab] = oldCount + item.m_stack;
                else
                    counts[prefab] = item.m_stack;
            }

            FieldDepotStockRuleRuntime.CollectConfiguredPrefabs(depot, _containerConfiguredRuleScratch);
            for (int i = 0; i < _containerConfiguredRuleScratch.Count; i++) names.Add(_containerConfiguredRuleScratch[i]);

            if (!string.IsNullOrEmpty(search))
            {
                EnsureFieldDepotKnownSearchCache(search);
                for (int i = 0; i < _fieldDepotKnownSearchPrefabs.Count; i++)
                    names.Add(_fieldDepotKnownSearchPrefabs[i]);
            }

            foreach (string prefab in names)
            {
                if (!string.IsNullOrEmpty(search) && !DepotItemMatchesSearch(prefab, search)) continue;

                string displayName;
                if (!_fieldDepotDisplayNameCache.TryGetValue(prefab, out displayName))
                {
                    displayName = GetDepotItemDisplayName(prefab);
                    _fieldDepotDisplayNameCache[prefab] = displayName ?? prefab;
                }

                _fieldDepotStockUiEntries.Add(new FieldDepotStockUiEntry
                {
                    Prefab = prefab,
                    DisplayName = displayName ?? prefab,
                    Current = counts.TryGetValue(prefab, out int stock) ? stock : 0,
                    Reserve = FieldDepotStockRuleRuntime.GetTransportReserve(depot, prefab),
                    SeedResource = false,
                    Trigger = 0,
                    SeedMode = false
                });
            }

            _fieldDepotStockUiEntries.Sort((a, b) =>
                string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        }

        private void EnsureFieldDepotKnownSearchCache(string search)
        {
            EnsureDepotItemSearchIndexProgress(24);
            string query = (search ?? string.Empty).Trim();
            if (string.Equals(_fieldDepotKnownSearchQuery, query, StringComparison.Ordinal) &&
                _fieldDepotKnownSearchVersion == _depotItemSearchVersion) return;

            _fieldDepotKnownSearchQuery = query;
            _fieldDepotKnownSearchVersion = _depotItemSearchVersion;
            _fieldDepotKnownSearchPrefabs.Clear();
            if (string.IsNullOrWhiteSpace(query)) return;

            for (int i = 0; i < _depotItemSearchIndex.Count && _fieldDepotKnownSearchPrefabs.Count < 200; i++)
            {
                DepotItemSearchEntry entry = _depotItemSearchIndex[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.Prefab)) continue;
                if (entry.SearchText.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                _fieldDepotKnownSearchPrefabs.Add(entry.Prefab);
            }
        }

        private void InvalidateFieldDepotStockUiCache()
        {
            _fieldDepotStockUiNextRefresh = 0f;
        }

        private void EnsureDepotItemSearchIndexProgress(int budget)
        {
            if (ObjectDB.instance == null || ObjectDB.instance.m_items == null) return;

            int dbId = ObjectDB.instance.GetInstanceID();
            System.Collections.Generic.List<GameObject> items = ObjectDB.instance.m_items;
            int sourceCount = items.Count;
            if (_depotItemSearchDbInstanceId != dbId || _depotItemSearchSourceCount != sourceCount)
            {
                _depotItemSearchDbInstanceId = dbId;
                _depotItemSearchSourceCount = sourceCount;
                _depotItemSearchNextSourceIndex = 0;
                _depotItemSearchVersion++;
                _depotItemSearchIndex.Clear();
                _depotItemSearchByPrefab.Clear();
                _fieldDepotKnownSearchVersion = -1;
            }

            int remaining = Mathf.Max(1, budget);
            while (_depotItemSearchNextSourceIndex < sourceCount && remaining-- > 0)
            {
                GameObject go = items[_depotItemSearchNextSourceIndex++];
                if (go == null) continue;
                ItemDrop drop = go.GetComponent<ItemDrop>();
                if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null) continue;

                string prefab = go.name;
                if (string.IsNullOrWhiteSpace(prefab) || _depotItemSearchByPrefab.ContainsKey(prefab)) continue;
                string sharedName = drop.m_itemData.m_shared.m_name ?? string.Empty;
                string localized = TryLocalizeDepotItemName(sharedName);
                string displayName = !string.IsNullOrWhiteSpace(localized) && !localized.StartsWith("$", StringComparison.Ordinal)
                    ? localized
                    : (sharedName.StartsWith("$", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(sharedName) ? prefab : sharedName);

                DepotItemSearchEntry entry = new DepotItemSearchEntry
                {
                    Prefab = prefab,
                    DisplayName = displayName,
                    SearchText = prefab + "\n" + sharedName + "\n" + localized
                };
                _depotItemSearchIndex.Add(entry);
                _depotItemSearchByPrefab[prefab] = entry;
                _fieldDepotDisplayNameCache[prefab] = displayName;
                _depotItemSearchVersion++;
            }
        }

        private bool DepotItemMatchesSearch(string prefab, string search)
        {
            if (string.IsNullOrWhiteSpace(prefab) || string.IsNullOrWhiteSpace(search)) return false;
            if (prefab.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) return true;

            if (_depotItemSearchByPrefab.TryGetValue(prefab, out DepotItemSearchEntry entry) && entry != null)
                return entry.SearchText.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;

            // The index is intentionally incremental. Until this prefab is indexed, do not fall back
            // to reflection/localization/ObjectDB lookups on the IMGUI hot path. It will appear on a
            // following refresh after the index reaches it.
            return false;
        }

        private string GetDepotItemDisplayName(string prefab)
        {
            if (string.IsNullOrWhiteSpace(prefab)) return prefab ?? string.Empty;
            if (_fieldDepotDisplayNameCache.TryGetValue(prefab, out string cachedDisplay) && !string.IsNullOrWhiteSpace(cachedDisplay))
                return cachedDisplay;

            if (_depotItemSearchByPrefab.TryGetValue(prefab, out DepotItemSearchEntry indexed) && indexed != null)
                return indexed.DisplayName ?? prefab;

            if (ObjectDB.instance == null) return prefab;
            GameObject go = ObjectDB.instance.GetItemPrefab(prefab);
            if (go == null) return prefab;
            ItemDrop drop = go.GetComponent<ItemDrop>();
            if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null) return prefab;

            string sharedName = drop.m_itemData.m_shared.m_name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sharedName)) return prefab;
            string localized = TryLocalizeDepotItemName(sharedName);
            string display = !string.IsNullOrWhiteSpace(localized) && !localized.StartsWith("$", StringComparison.Ordinal)
                ? localized
                : (sharedName.StartsWith("$", StringComparison.Ordinal) ? prefab : sharedName);
            _fieldDepotDisplayNameCache[prefab] = display;
            return display;
        }

        private static string TryLocalizeDepotItemName(string sharedName)
        {
            if (string.IsNullOrWhiteSpace(sharedName)) return string.Empty;
            if (_depotLocalizationCache.TryGetValue(sharedName, out string cached)) return cached;
            try
            {
                if (!_depotLocalizationReflectionResolved || _depotLocalizationInstance == null || _depotLocalizationMethod == null)
                {
                    Type localizationType = AccessTools.TypeByName("Localization");
                    if (localizationType != null)
                    {
                        var instanceField = localizationType.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (instanceField != null) _depotLocalizationInstance = instanceField.GetValue(null);
                        if (_depotLocalizationInstance == null)
                        {
                            var instanceProperty = localizationType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                            if (instanceProperty != null) _depotLocalizationInstance = instanceProperty.GetValue(null, null);
                        }
                        _depotLocalizationMethod = AccessTools.Method(localizationType, "Localize", new[] { typeof(string) });
                    }
                    _depotLocalizationReflectionResolved = _depotLocalizationInstance != null && _depotLocalizationMethod != null;
                }

                string localized = string.Empty;
                if (_depotLocalizationInstance != null && _depotLocalizationMethod != null)
                    localized = _depotLocalizationMethod.Invoke(_depotLocalizationInstance, new object[] { sharedName }) as string ?? string.Empty;
                _depotLocalizationCache[sharedName] = localized;
                return localized;
            }
            catch
            {
                _depotLocalizationCache[sharedName] = string.Empty;
                return string.Empty;
            }
        }

        private int CountPendingFieldDepotReserveEdits(Container depot)
        {
            if (depot == null || _containerReserveEdits.Count == 0) return 0;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            string depotId = zdo != null ? zdo.m_uid.ToString() : depot.GetInstanceID().ToString();
            string prefix = depotId + ":reserve:";
            int count = 0;
            foreach (var pair in _containerReserveEdits)
                if (pair.Key.StartsWith(prefix, StringComparison.Ordinal)) count++;
            return count;
        }

        private bool TrySaveFieldDepotReserveEdits(Container depot, out string message)
        {
            message = "Keine Aenderungen zu speichern.";
            if (depot == null || WorkerContainerRoleRuntime.GetRole(depot) != WorkerContainerRole.FieldDepot)
            {
                message = "Feldkiste ist nicht mehr verfuegbar.";
                return false;
            }

            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            string depotId = zdo != null ? zdo.m_uid.ToString() : depot.GetInstanceID().ToString();
            string prefix = depotId + ":reserve:";
            var updates = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in _containerReserveEdits)
            {
                if (!pair.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string prefab = pair.Key.Substring(prefix.Length);
                if (string.IsNullOrWhiteSpace(prefab)) continue;
                if (!int.TryParse(pair.Value, out int value) || value < 0)
                {
                    message = $"{prefab}: Keep Items muss eine Zahl ab 0 sein.";
                    return false;
                }
                updates[prefab] = value;
            }

            if (updates.Count == 0) return true;
            if (!FieldDepotStockRuleRuntime.ApplyTransportReserveBatch(depot, updates))
            {
                message = "Speichern fehlgeschlagen (Netzwerk-Ownership pruefen).";
                return false;
            }

            _containerReserveEdits.Clear();
            InvalidateFieldDepotStockUiCache();
            message = updates.Count == 1
                ? "1 Feldkisten-Einstellung gespeichert."
                : $"{updates.Count} Feldkisten-Einstellungen gespeichert.";
            return true;
        }

        private void ApplyDepotTriggerTextImmediately(Container depot, string prefab, string text, string editKey)
        {
            if (!int.TryParse(text, out int value) || value < 0) return;
            if (!FieldDepotStockRuleRuntime.SetProductionTrigger(depot, prefab, value)) return;
            int actual = FieldDepotStockRuleRuntime.GetProductionTrigger(depot, prefab);
            _containerTriggerEdits[editKey] = actual.ToString();
            InvalidateFieldDepotStockUiCache();
            SetStatus($"{prefab}: Samenproduktion ab -> {actual} (sofort gespeichert).");
        }

        private void AdjustDepotReserve(Container depot, string prefab, int delta, string editKey)
        {
            int current = FieldDepotStockRuleRuntime.GetTransportReserve(depot, prefab);
            if (_containerReserveEdits.TryGetValue(editKey, out string text) && int.TryParse(text, out int edited)) current = edited;
            int next = Mathf.Max(0, current + delta);
            _containerReserveEdits[editKey] = next.ToString();
        }

        private void AdjustDepotTrigger(Container depot, string prefab, int delta, string editKey)
        {
            int current = FieldDepotStockRuleRuntime.GetProductionTrigger(depot, prefab);
            if (_containerTriggerEdits.TryGetValue(editKey, out string text) && int.TryParse(text, out int edited)) current = edited;
            int next = Mathf.Max(0, current + delta);
            if (FieldDepotStockRuleRuntime.SetProductionTrigger(depot, prefab, next))
            {
                _containerTriggerEdits[editKey] = FieldDepotStockRuleRuntime.GetProductionTrigger(depot, prefab).ToString();
                InvalidateFieldDepotStockUiCache();
                SetStatus($"{prefab}: Samenproduktion ab -> {_containerTriggerEdits[editKey]}.");
            }
        }

        private void DrawAirFreightRoleButton(ZNetView view)
        {
            bool active = _openWorkerContainer != null &&
                          WorkerContainerRoleRuntime.GetRole(_openWorkerContainer) == WorkerContainerRole.FieldDepot &&
                          WorkerContainerRoleRuntime.SupportsAirTransport(_openWorkerContainer);
            if (!GUILayout.Button((active ? "[AKTIV] " : string.Empty) + "Luftfrachtkiste (Import + Export)")) return;
            if (view == null || !view.IsValid() || !view.IsOwner())
            {
                SetStatus("Kiste ist REMOTE-owned; Rolle konnte nicht geaendert werden.");
                return;
            }
            long owner = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;
            if (!WorkerContainerRoleRuntime.SetRole(_openWorkerContainer, WorkerContainerRole.FieldDepot, owner) ||
                !WorkerContainerRoleRuntime.SetAirTransport(_openWorkerContainer, true, owner))
            {
                SetStatus("Luftfrachtkiste konnte nicht aktiviert werden (keine Netzwerk-Ownership).");
                return;
            }
            _fieldDepotStockSettingsOpen = false;
            if (FieldDepotWorkzoneRuntime.TryAutoAssignNearestWorkArea(_openWorkerContainer, out string airZoneId, out string airZoneName))
            {
                string label = string.IsNullOrWhiteSpace(airZoneName) ? airZoneId : airZoneName;
                SetStatus($"Kistenrolle -> Luftfrachtkiste. Naechste Workzone automatisch: {label}.");
            }
            else
            {
                SetStatus("Kistenrolle -> Luftfrachtkiste. Import + Export fuer Drakes aktiv; freier Himmel erforderlich.");
            }
        }

        private void DrawContainerRoleButton(ZNetView view, WorkerContainerRole role, string label)
        {
            bool active = _openWorkerContainer != null && WorkerContainerRoleRuntime.GetRole(_openWorkerContainer) == role;
            if (!GUILayout.Button((active ? "[AKTIV] " : string.Empty) + label)) return;
            if (view == null || !view.IsValid() || !view.IsOwner())
            {
                SetStatus("Kiste ist REMOTE-owned; Rolle konnte nicht geaendert werden.");
                return;
            }
            long owner = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;
            if (WorkerContainerRoleRuntime.SetRole(_openWorkerContainer, role, owner))
            {
                // "Felddepot" and "Luftfrachtkiste" are two explicit UI roles. The latter uses
                // the same stock-rule foundation but adds bidirectional air freight.
                if (role == WorkerContainerRole.FieldDepot && WorkerContainerRoleRuntime.SupportsAirTransport(_openWorkerContainer))
                    WorkerContainerRoleRuntime.SetAirTransport(_openWorkerContainer, false, owner);
                if (role != WorkerContainerRole.FieldDepot) _fieldDepotStockSettingsOpen = false;
                if (role != WorkerContainerRole.Import) _containerStockLimitSettingsOpen = false;
                if (role == WorkerContainerRole.FieldDepot &&
                    FieldDepotWorkzoneRuntime.TryAutoAssignNearestWorkArea(_openWorkerContainer, out string zoneId, out string zoneName))
                {
                    string zoneLabel = string.IsNullOrWhiteSpace(zoneName) ? zoneId : zoneName;
                    SetStatus($"Kistenrolle -> Felddepot. Naechste Workzone automatisch: {zoneLabel}.");
                }
                else
                {
                    SetStatus($"Kistenrolle -> {WorkerContainerRoleRuntime.DisplayName(role)}. Keep-One {(WorkerContainerRoleRuntime.ProtectLastItem(_openWorkerContainer) ? "AN" : "AUS")}.");
                }
            }
        }

        internal string GetWorkFlagHoverText(WorkFlagInteractable flag)
        {
            if (!_enabled.Value || flag == null) return string.Empty;
            ZNetView nview = flag.GetNetworkView();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo == null) return "Workers-Arbeitsfahne\nNetzwerkstatus nicht verfuegbar";

            WorkFlagZdo.InitializeIfNeeded(zdo, _workFlagDefaultRadius.Value);
            string name = WorkFlagZdo.ReadDisplayName(zdo);
            if (string.IsNullOrWhiteSpace(name)) name = "Workers-Arbeitsfahne";
            float radius = WorkFlagZdo.ReadRadius(zdo, _workFlagDefaultRadius.Value);
            return $"{name}\nWorkzone fuer alle Worker-Jobs\nRadius: {radius:0} m\n[<color=yellow><b>E</b></color>] Workzone / Arbeiter";
        }

        internal bool TryOpenWorkFlagMenu(WorkFlagInteractable flag, Humanoid user)
        {
            if (!_enabled.Value || flag == null) return false;
            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer) return false;

            ZNetView nview = flag.GetNetworkView();
            if (nview == null || !nview.IsValid() || nview.GetZDO() == null)
            {
                SetStatus("Arbeitsfahne hat keinen gueltigen ZDO.");
                return true;
            }
            if (!nview.IsOwner())
            {
                SetStatus("Arbeitsfahne ist REMOTE-owned. Multiplayer-Flag-RPC folgt spaeter.");
                return true;
            }

            ZDO zdo = nview.GetZDO();
            WorkFlagZdo.InitializeIfNeeded(zdo, _workFlagDefaultRadius.Value);
            WorkFlagZdo.EnsureId(zdo);
            long owner = WorkFlagZdo.ReadOwner(zdo);
            if (owner != 0L && owner != player.GetPlayerID())
            {
                SetStatus("Diese Arbeitsfahne gehoert einem anderen Spieler.");
                return true;
            }
            if (owner == 0L)
            {
                owner = player.GetPlayerID();
                zdo.Set(WorkFlagZdo.OwnerPlayerId, owner);
            }
            string flagName = EnsureWorkFlagDisplayName(zdo, owner);

            _openWorkFlag = flag;
            _workFlagOpenedFromOutpostManager = false;
            _workFlagScroll = Vector2.zero;
            CaptureMenuCursor();
            CenterWorkFlagWindow();
            _workFlagNameEditId = WorkFlagZdo.ReadId(zdo);
            _workFlagNameEdit = flagName;
            _nextWorkFlagAssignedCountRefresh = 0f;
            RefreshOpenWorkFlagAssignedCount();
            SetStatus("Arbeitsfahne: Workzone verwalten. Jobs werden pro Worker an der Ruhestaette eingestellt.");
            return true;
        }

        private void DrawWorkFlagMenuWindow(int windowId)
        {
            EnsureSmartPopupStyles();
            if (_openWorkFlag == null)
            {
                GUILayout.Label("Arbeitsfahne nicht mehr verfuegbar.", _smartPopupLabelStyle);
                if (GUILayout.Button("Schliessen", _smartPopupButtonStyle)) CloseWorkFlagMenu();
                return;
            }

            ZNetView nview = _openWorkFlag.GetNetworkView();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo == null)
            {
                GUILayout.Label("Kein gueltiger Netzwerkzustand.", _smartPopupLabelStyle);
                if (GUILayout.Button("Schliessen", _smartPopupButtonStyle)) CloseWorkFlagMenu();
                return;
            }

            WorkFlagZdo.InitializeIfNeeded(zdo, _workFlagDefaultRadius.Value);
            string flagId = nview.IsOwner() ? WorkFlagZdo.EnsureId(zdo) : WorkFlagZdo.ReadId(zdo);
            RefreshOpenWorkFlagAssignedCount();
            string currentName = WorkFlagZdo.ReadDisplayName(zdo);
            if (string.IsNullOrWhiteSpace(currentName) && nview.IsOwner())
                currentName = EnsureWorkFlagDisplayName(zdo, WorkFlagZdo.ReadOwner(zdo));
            if (!string.Equals(_workFlagNameEditId, flagId, StringComparison.Ordinal))
            {
                _workFlagNameEditId = flagId;
                _workFlagNameEdit = currentName;
            }

            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            GUILayout.Label("WORKERS", _smartPopupHeadingStyle, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("X", _smartPopupButtonStyle, GUILayout.Width(36f), GUILayout.Height(28f)))
            {
                CloseWorkFlagMenu();
                GUIUtility.ExitGUI();
            }
            GUILayout.EndHorizontal();

            GUILayout.Label(currentName + "  •  Arbeitsfahne", _smartPopupHeadingStyle);
            _workFlagScroll = GUILayout.BeginScrollView(_workFlagScroll, GUILayout.ExpandHeight(true));

            GUILayout.Space(4f);
            GUILayout.Label("Arbeitsgebiet / Workzone", _smartPopupHeadingStyle);
            _workFlagNameEdit = GUILayout.TextField(_workFlagNameEdit ?? string.Empty, 32);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Name speichern", _smartPopupButtonStyle) && nview.IsOwner())
            {
                WorkFlagZdo.SetDisplayName(zdo, _workFlagNameEdit);
                _workFlagNameEdit = WorkFlagZdo.ReadDisplayName(zdo);
                UpdateLoadedWorkerWorkzoneNames(flagId, _workFlagNameEdit);
                SetStatus($"Arbeitsgebiet umbenannt: {_workFlagNameEdit}");
            }
            if (GUILayout.Button("Defaultname", _smartPopupButtonStyle) && nview.IsOwner())
            {
                _workFlagNameEdit = $"Workzone{AllocateWorkzoneIndex(WorkFlagZdo.ReadOwner(zdo))}";
                WorkFlagZdo.SetDisplayName(zdo, _workFlagNameEdit);
                UpdateLoadedWorkerWorkzoneNames(flagId, _workFlagNameEdit);
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            GUILayout.Label("Worker-Jobs", _smartPopupHeadingStyle);
            GUILayout.Label("Diese Fahne definiert nur noch die Workzone. Welche Arbeiten ein Worker ausfuehrt und mit welcher Prioritaet (1-4), stellst du an seiner Ruhestaette ein.", _smartPopupLabelStyle);
            GUILayout.Label($"Zugewiesene geladene Arbeiter: {_workFlagAssignedCount}", _smartPopupLabelStyle);

            GUILayout.Space(10f);
            GUILayout.Label("Arbeitsradius", _smartPopupHeadingStyle);
            float currentRadius = WorkFlagZdo.ReadRadius(zdo, _workFlagDefaultRadius.Value);
            GUILayout.Label($"{currentRadius:0} m", _smartPopupLabelStyle);
            float newRadius = Mathf.Round(GUILayout.HorizontalSlider(currentRadius, 10f, 80f));
            if (Math.Abs(newRadius - currentRadius) >= 1f && nview.IsOwner())
            {
                WorkFlagZdo.SetRadius(zdo, newRadius);
                UpdateLoadedWorkerWorkzoneRadius(flagId, newRadius);
            }
            bool canUseBirdView = !_workFlagOpenedFromOutpostManager;
            GUI.enabled = canUseBirdView;
            if (GUILayout.Button("Radius in Vogelperspektive bearbeiten", _smartPopupButtonStyle))
            {
                BeginWorkZoneBirdView(_openWorkFlag, currentRadius);
                GUI.enabled = true;
                GUIUtility.ExitGUI();
            }
            GUI.enabled = true;
            if (canUseBirdView)
                GUILayout.Label("Vogelperspektive: Mausrad = Kamera-Zoom; Radius wird dort per Slider eingestellt. Speichern/Abbrechen ueber Buttons, ESC bricht ab.", _smartPopupLabelStyle);
            else
                GUILayout.Label("Kartographietisch-Fernverwaltung: Name, Radius und Arbeiter sind editierbar. Jobs/Prioritaeten werden an den Ruhestaetten der Worker eingestellt. Vogelperspektive nur direkt an der Fahne.", _smartPopupLabelStyle);

            GUILayout.Space(10f);
            DrawWorkzoneEconomy(flagId, _openWorkFlag.transform.position, currentRadius);

            GUILayout.Space(10f);
            DrawWorkzoneJobAudit(flagId, _openWorkFlag.transform.position, currentRadius);

            GUILayout.Space(10f);
            GUILayout.Label("Arbeiter-Zuweisung", _smartPopupHeadingStyle);
            if (GUILayout.Button("Gezielten Arbeiter zuweisen", _smartPopupButtonStyle)) BeginWorkFlagWorkerSelection(_openWorkFlag);
            if (GUILayout.Button("Alle geladenen passenden Arbeiter zuweisen", _smartPopupButtonStyle)) AssignLoadedWorkersToFlag(nview, zdo);
            if (GUILayout.Button("Geladene Zuweisungen dieser Fahne loesen", _smartPopupButtonStyle)) ReleaseLoadedWorkersFromFlag(zdo);

            GUILayout.Space(10f);
            DrawWorkFlagAssignedWorkers(zdo);

            GUILayout.Space(10f);
            GUILayout.Label("Status", _smartPopupHeadingStyle);
            GUILayout.Label(SmartReplantBridge.GetStatusText(), _smartPopupLabelStyle);
            GUILayout.Label("Sammler fuellen ihre Traglast mit demselben Item, bevor sie zum Felddepot laufen. Traeger warten standardmaessig auf eine sinnvolle Batch-Groesse, damit Felddepots als echte Puffer arbeiten.", _smartPopupLabelStyle);
            GUILayout.Label("ESC schliesst dieses Fenster. Solange es offen ist, bleibt der Mauszeiger frei und die Kamera wird nicht durch die Maussteuerung eingefangen.", _smartPopupLabelStyle);

            GUILayout.EndScrollView();
            GUILayout.Space(6f);
            if (GUILayout.Button("Schliessen", _smartPopupButtonStyle)) CloseWorkFlagMenu();
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 34f));
        }

        private void DrawWorkzoneEconomy(string areaId, Vector3 center, float radius)
        {
            GUILayout.Label("Workzone-Wirtschaft", _smartPopupHeadingStyle);
            WorkzoneEconomySnapshot economy = null;
            try { economy = WorkzoneEconomyRuntime.GetSnapshot(areaId, center, radius); }
            catch { }
            if (economy == null)
            {
                GUILayout.Label("Bilanz derzeit nicht verfuegbar.", _smartPopupLabelStyle);
                return;
            }

            string backgroundStatus = WorkzoneEconomyBackgroundRuntime.GetStatus(areaId);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Background", _smartPopupTableCellStyle, GUILayout.Width(92f));
            GUILayout.Label(backgroundStatus, _smartPopupGoodStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();

            if (economy.SustenanceWorkerCount > 0)
            {
                int missing = Math.Max(0, economy.SustenanceWorkerCount - economy.SupplyReadyWorkerCount);
                string autoOff = economy.SupplyAutoDisabledWorkerCount > 0
                    ? $"  •  Auto AUS: {economy.SupplyAutoDisabledWorkerCount}"
                    : string.Empty;
                GUIStyle supplyStyle = missing > 0 ? _smartPopupWarnStyle : _smartPopupGoodStyle;
                string icon = missing > 0 ? "!" : "✓";
                GUILayout.Label($"{icon} Worker-Nahrung: {economy.SupplyReadyWorkerCount}/{economy.SustenanceWorkerCount} versorgt{autoOff}", supplyStyle);
            }
            else
            {
                GUILayout.Label($"Worker: {economy.WorkerCount}  •  kein Nahrungsbedarf registriert", _smartPopupLabelStyle);
            }

            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Ressource", _smartPopupTableHeaderStyle, GUILayout.Width(174f));
            GUILayout.Label("Bestand", _smartPopupTableHeaderStyle, GUILayout.Width(70f));
            GUILayout.Label("Ø Ertrag/d", _smartPopupTableHeaderStyle, GUILayout.Width(76f));
            GUILayout.Label("Ø Verbrauch/d", _smartPopupTableHeaderStyle, GUILayout.Width(88f));
            GUILayout.Label("Ø Bilanz/d", _smartPopupTableHeaderStyle, GUILayout.Width(76f));
            GUILayout.EndHorizontal();

            int shown = 0;
            for (int i = 0; i < economy.Rows.Count && shown < 10; i++)
            {
                WorkzoneEconomyResourceRow row = economy.Rows[i];
                if (row == null || !row.HasForecastActivity) continue;

                string name = ProductionMetricsRuntime.DisplayName(row.Prefab);
                if (name.Length > 22) name = name.Substring(0, 21) + "…";
                string prod = row.HasProductionModel ? row.ProductionPerDay.ToString("0.0") : "–";
                string cons = row.ConsumptionPerDay > 0.0001 ? row.ConsumptionPerDay.ToString("0.0") : "–";
                string net = row.HasProductionModel || row.ConsumptionPerDay > 0.0001
                    ? (row.BalancePerDay >= -0.0001 ? "+" : string.Empty) + row.BalancePerDay.ToString("0.0")
                    : "–";

                GUIStyle netStyle = row.BalancePerDay < -0.0001
                    ? _smartPopupTableBadValueStyle
                    : row.BalancePerDay > 0.0001
                        ? _smartPopupTableGoodValueStyle
                        : _smartPopupTableValueStyle;

                GUILayout.BeginHorizontal();
                GUILayout.Label(name, _smartPopupTableCellStyle, GUILayout.Width(174f));
                GUILayout.Label(row.Stock.ToString(), _smartPopupTableValueStyle, GUILayout.Width(70f));
                GUILayout.Label(prod, _smartPopupTableValueStyle, GUILayout.Width(76f));
                GUILayout.Label(cons, _smartPopupTableValueStyle, GUILayout.Width(88f));
                GUILayout.Label(net, netStyle, GUILayout.Width(76f));
                GUILayout.EndHorizontal();
                shown++;
            }

            if (shown == 0)
            {
                GUILayout.Label("Noch keine modellierte Produktion bzw. kein Worker-Verbrauch in dieser Workzone.", _smartPopupLabelStyle);
            }
            else
            {
                GUILayout.Space(4f);
                if (economy.HasForecastDeficit)
                    GUILayout.Label("! Prognose: Defizit – Import oder zusaetzliche Produktion noetig.", _smartPopupCriticalStyle);
                else
                    GUILayout.Label("✓ Prognose: Die aktuell modellierten Ressourcen sind nachhaltig.", _smartPopupGoodStyle);
            }

            GUILayout.Space(2f);
            GUILayout.Label($"Ø = {WorkzoneActivityFlowRuntime.AveragingWindowDays}-Tage-Mittel. WildGrowth/Nahrung werden prognostiziert; echte Produktion, Verbrauch und abgeschlossene Lieferungen werden ereignisbasiert geglaettet. Kein Dauerscan.", _smartPopupLabelStyle);
        }

        private void DrawWorkzoneJobAudit(string areaId, Vector3 center, float radius)
        {
            GUILayout.Label("Workzone-Test / Job-Audit", _smartPopupHeadingStyle);
            GUILayout.Label("Nur auf Knopfdruck: prueft die aktuell geladenen Worker dieser Workzone und erstellt einen read-only Dry Run fuer 1 Valheim-Tag. Es wird kein Worker bewegt, kein Item verbraucht und kein Background-Tick ausgeloest.", _smartPopupLabelStyle);

            if (GUILayout.Button("Workzone jetzt testen", _smartPopupButtonStyle))
            {
                // Force one fresh assignment snapshot for this explicit diagnostic action. The normal
                // one-second UI cache stays untouched; there is no additional recurring scan.
                _nextWorkFlagAssignedCountRefresh = 0f;
                RefreshOpenWorkFlagAssignedCount();
                try
                {
                    _workFlagAuditSnapshot = WorkzoneJobAuditRuntime.Build(areaId, center, radius, _workFlagAssignedWorkers);
                    _workFlagAuditAreaId = areaId ?? string.Empty;
                    SetStatus($"Workzone-Test abgeschlossen: {_workFlagAuditSnapshot.WorkerCount} Worker, {_workFlagAuditSnapshot.WarningCount} Warnung(en).");
                }
                catch (Exception ex)
                {
                    _workFlagAuditSnapshot = null;
                    _workFlagAuditAreaId = areaId ?? string.Empty;
                    SetStatus("Workzone-Test fehlgeschlagen: " + ex.GetType().Name);
                }
            }

            WorkzoneJobAuditRuntime.Snapshot snapshot = _workFlagAuditSnapshot;
            if (snapshot == null || !string.Equals(_workFlagAuditAreaId, areaId ?? string.Empty, StringComparison.Ordinal))
            {
                GUILayout.Label("Noch kein Test fuer diese Workzone ausgefuehrt.", _smartPopupLabelStyle);
                return;
            }

            GUILayout.Space(4f);
            GUIStyle summaryStyle = snapshot.WarningCount > 0 ? _smartPopupWarnStyle : _smartPopupGoodStyle;
            GUILayout.Label($"Worker: {snapshot.WorkerCount}  •  Job-Zuweisungen: {snapshot.EnabledJobAssignments}  •  aktuell bereit: {snapshot.ReadyJobAssignments}  •  Warnungen: {snapshot.WarningCount}", summaryStyle);

            if (snapshot.Jobs.Count > 0)
            {
                GUILayout.Space(4f);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Job", _smartPopupTableHeaderStyle, GUILayout.Width(174f));
                GUILayout.Label("Worker", _smartPopupTableHeaderStyle, GUILayout.Width(60f));
                GUILayout.Label("Bereit", _smartPopupTableHeaderStyle, GUILayout.Width(60f));
                GUILayout.Label("Wartet", _smartPopupTableHeaderStyle, GUILayout.Width(60f));
                GUILayout.Label("BG bereit", _smartPopupTableHeaderStyle, GUILayout.Width(72f));
                GUILayout.EndHorizontal();

                for (int i = 0; i < snapshot.Jobs.Count; i++)
                {
                    WorkzoneJobAuditRuntime.JobRow row = snapshot.Jobs[i];
                    GUIStyle readyStyle = row.ReadyWorkers > 0 ? _smartPopupTableGoodValueStyle : _smartPopupTableValueStyle;
                    GUIStyle bgStyle = row.BackgroundSupported ? _smartPopupTableGoodValueStyle : _smartPopupTableBadValueStyle;
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(row.Name, _smartPopupTableCellStyle, GUILayout.Width(174f));
                    GUILayout.Label(row.EnabledWorkers.ToString(), _smartPopupTableValueStyle, GUILayout.Width(60f));
                    GUILayout.Label(row.ReadyWorkers.ToString(), readyStyle, GUILayout.Width(60f));
                    GUILayout.Label(row.WaitingWorkers.ToString(), _smartPopupTableValueStyle, GUILayout.Width(60f));
                    GUILayout.Label(row.BackgroundSupported ? $"{row.BackgroundReadyWorkers}/{row.EnabledWorkers}" : "–", bgStyle, GUILayout.Width(72f));
                    GUILayout.EndHorizontal();
                    if (!string.IsNullOrEmpty(row.Note))
                        GUILayout.Label("  " + row.Name + ": " + row.Note, _smartPopupLabelStyle);
                }
            }

            if (snapshot.Issues.Count > 0)
            {
                GUILayout.Space(6f);
                GUILayout.Label("Auffaelligkeiten", _smartPopupHeadingStyle);
                int maxIssues = Math.Min(12, snapshot.Issues.Count);
                for (int i = 0; i < maxIssues; i++)
                    GUILayout.Label("! " + snapshot.Issues[i], _smartPopupWarnStyle);
                if (snapshot.Issues.Count > maxIssues)
                    GUILayout.Label($"… plus {snapshot.Issues.Count - maxIssues} weitere.", _smartPopupLabelStyle);
            }

            if (snapshot.WildGrowth.Count > 0)
            {
                GUILayout.Space(6f);
                GUILayout.Label("WildGrowth-Diagnose", _smartPopupHeadingStyle);
                for (int i = 0; i < snapshot.WildGrowth.Count; i++)
                    GUILayout.Label(snapshot.WildGrowth[i], _smartPopupLabelStyle);
                GUILayout.Label("Ghost = noch nie gesetzt. Respawn = physische Pflanze existiert, ist aber gerade abgeerntet. Fehlt > 0 ist verdaechtig.", _smartPopupLabelStyle);
            }

            if (snapshot.Logistics.Count > 0)
            {
                GUILayout.Space(6f);
                GUILayout.Label("Felddepot-Auftraege", _smartPopupHeadingStyle);
                for (int i = 0; i < snapshot.Logistics.Count; i++)
                    GUILayout.Label(snapshot.Logistics[i], _smartPopupLabelStyle);
                GUILayout.Label("Die Werte stammen aus dem event-gepflegten Workzone-Pool/Transport-Cache; dieser Audit startet keinen Dauerscan.", _smartPopupLabelStyle);
            }

            GUILayout.Space(6f);
            GUILayout.Label("Dry Run – 1 Valheim-Tag", _smartPopupHeadingStyle);
            for (int i = 0; i < snapshot.DryRun.Count; i++)
                GUILayout.Label(snapshot.DryRun[i], i == 0 ? _smartPopupGoodStyle : _smartPopupLabelStyle);

            GUILayout.Space(6f);
            _workFlagAuditWorkerDetailsExpanded = GUILayout.Toggle(_workFlagAuditWorkerDetailsExpanded,
                $"Worker-Details anzeigen ({snapshot.Workers.Count})", GUI.skin.toggle);
            if (_workFlagAuditWorkerDetailsExpanded)
            {
                int shown = Math.Min(40, snapshot.Workers.Count);
                for (int i = 0; i < shown; i++)
                {
                    WorkzoneJobAuditRuntime.WorkerRow row = snapshot.Workers[i];
                    GUILayout.BeginVertical(GUI.skin.box);
                    GUILayout.Label($"{row.Name}  •  {row.Role}  •  {row.CurrentJob} / {row.State}", _smartPopupLabelStyle);
                    GUILayout.Label("Aktiv: " + row.Jobs, _smartPopupLabelStyle);
                    if (!string.IsNullOrEmpty(row.DisabledJobs)) GUILayout.Label("Deaktiviert: " + row.DisabledJobs, _smartPopupLabelStyle);
                    GUILayout.Label("Nahrung: " + row.Food, _smartPopupLabelStyle);
                    if (!string.IsNullOrEmpty(row.JobStatus)) GUILayout.Label("Job-Pruefung: " + row.JobStatus, _smartPopupLabelStyle);
                    if (!string.IsNullOrEmpty(row.Decision)) GUILayout.Label("Letzte Entscheidung: " + row.Decision, _smartPopupLabelStyle);
                    if (!string.IsNullOrEmpty(row.Warning)) GUILayout.Label("! " + row.Warning, _smartPopupWarnStyle);
                    GUILayout.EndVertical();
                }
                if (snapshot.Workers.Count > shown)
                    GUILayout.Label($"… {snapshot.Workers.Count - shown} weitere Worker ausgeblendet (UI-Schutz).", _smartPopupLabelStyle);
            }

            GUILayout.Label($"Snapshot: {snapshot.CreatedUtc.ToLocalTime():HH:mm:ss}. Erneut 'Workzone jetzt testen' druecken, um aktuelle Werte einzulesen.", _smartPopupLabelStyle);
        }

        private void DrawWorkOrderToggle(ZNetView nview, ZDO zdo, WorkOrder order, string label, bool implemented)
        {
            bool enabled = WorkFlagZdo.HasOrder(zdo, order);
            string suffix = implemented ? " [AKTIV]" : " [Grundlage]";
            bool next = GUILayout.Toggle(enabled, label + suffix, GUI.skin.toggle);
            if (next == enabled) return;
            if (nview == null || !nview.IsOwner())
            {
                SetStatus("Arbeitsfahne ist REMOTE-owned; Auftrag konnte nicht geaendert werden.");
                return;
            }

            long playerId = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;
            WorkFlagZdo.SetOrderEnabled(zdo, playerId, order, next);
            SetStatus($"Arbeitsfahne: {WorkOrderCatalog.DisplayName(order)} {(next ? "aktiviert" : "deaktiviert")}{(implemented ? string.Empty : " (Job-AI folgt)")}.");
        }

        private void RefreshOpenWorkFlagAssignedCount()
        {
            if (_openWorkFlag == null)
            {
                _workFlagAssignedWorkers.Clear();
                _workFlagAssignedCount = 0;
                return;
            }
            if (Time.unscaledTime < _nextWorkFlagAssignedCountRefresh) return;
            _nextWorkFlagAssignedCountRefresh = Time.unscaledTime + 1f;

            ZNetView nview = _openWorkFlag.GetNetworkView();
            ZDO flagZdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            _workFlagAssignedWorkers.Clear();
            if (flagZdo == null)
            {
                _workFlagAssignedCount = 0;
                return;
            }

            string flagId = WorkFlagZdo.ReadId(flagZdo);
            long ownerPlayerId = WorkFlagZdo.ReadOwner(flagZdo);
            if (string.IsNullOrEmpty(flagId))
            {
                _workFlagAssignedCount = 0;
                return;
            }

            // v0.5.447: menu refresh reuses the lifecycle Worker cache.
            WorkerFastLookupRuntime.CopyKnownWorkers(_loadedWorkerScratch);
            for (int i = 0; i < _loadedWorkerScratch.Count; i++)
            {
                Character character = _loadedWorkerScratch[i];
                if (character == null || character.IsDead()) continue;
                if (!WorkerFastLookupRuntime.TryGetWorker(character, false, out ZNetView workerView, out ZDO workerZdo)) continue;
                if (ownerPlayerId != 0L && workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != ownerPlayerId) continue;
                if (!string.Equals(WorkerZdo.ReadWorkAreaId(workerZdo), flagId, StringComparison.Ordinal)) continue;
                _workFlagAssignedWorkers.Add(character);
            }
            _workFlagAssignedCount = _workFlagAssignedWorkers.Count;
        }

        private void DrawWorkFlagAssignedWorkers(ZDO flagZdo)
        {
            GUILayout.Label("Zugewiesene Arbeiter", _smartPopupHeadingStyle);
            if (_openWorkFlag == null || flagZdo == null)
            {
                GUILayout.Label("Keine Fahne verfuegbar.", _smartPopupLabelStyle);
                return;
            }

            RefreshOpenWorkFlagAssignedCount();
            if (_workFlagAssignedWorkers.Count == 0)
            {
                GUILayout.Label("Noch keine geladenen Arbeiter dieser Workzone zugewiesen.", _smartPopupLabelStyle);
                GUILayout.Label("Regel: Ein Arbeiter kann nur in einer Workzone arbeiten, wenn sein echter Schlafplatz/HomeAnchor innerhalb des Fahnenradius liegt.", _smartPopupLabelStyle);
                return;
            }

            float radius = WorkFlagZdo.ReadRadius(flagZdo, _workFlagDefaultRadius.Value);
            Vector3 center = _openWorkFlag.transform.position;
            for (int i = 0; i < _workFlagAssignedWorkers.Count; i++)
            {
                Character worker = _workFlagAssignedWorkers[i];
                if (worker == null) continue;
                ZNetView workerView = worker.GetComponent<ZNetView>();
                ZDO workerZdo = workerView != null && workerView.IsValid() ? workerView.GetZDO() : null;
                if (!WorkerZdo.IsWorker(workerZdo)) continue;

                string species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
                WorkerRole role = (WorkerRole)workerZdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
                WorkerState state = (WorkerState)workerZdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
                WorkerZdo.TryReadWorkArea(workerZdo, out WorkOrder order, out _, out _);
                string cargo = WorkerZdo.TryReadCargo(workerZdo, out string cargoPrefab, out int cargoAmount)
                    ? $"{cargoPrefab} x{cargoAmount}"
                    : "leer";
                bool homeOk = WorkerWorkAreaRules.TryValidateHomeForArea(workerZdo, center, radius, out _);

                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{species}  •  {WorkerTierCatalog.DisplayTier(worker)}  •  {GetWorkerRoleDisplayName(role)}", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                bool oldEnabled = GUI.enabled;
                GUI.enabled = workerView != null && workerView.IsValid() && workerView.IsOwner();
                if (GUILayout.Button("Zuordnung loesen", _smartPopupButtonStyle, GUILayout.Width(150f)))
                {
                    ReleaseWorkerFromOpenFlag(worker, workerZdo);
                    GUI.enabled = oldEnabled;
                    GUILayout.EndHorizontal();
                    GUILayout.EndVertical();
                    GUIUtility.ExitGUI();
                    return;
                }
                GUI.enabled = oldEnabled;
                GUILayout.EndHorizontal();
                GUILayout.Label($"Aktuell: {WorkOrderCatalog.DisplayName(order)}  •  Status: {state}  •  Cargo: {cargo}", _smartPopupLabelStyle);
                GUILayout.Label(WorkerWorkAreaRules.DescribeHomeStatus(workerZdo, center, radius), _smartPopupLabelStyle);
                if (!homeOk)
                    GUILayout.Label("ARBEIT BLOCKIERT: Schlafplatz muss innerhalb dieser Workzone liegen.", _smartPopupLabelStyle);
                GUILayout.EndVertical();
            }
        }

        private void ReleaseWorkerFromOpenFlag(Character worker, ZDO workerZdo)
        {
            if (worker == null || workerZdo == null) return;
            ZNetView workerView = worker.GetComponent<ZNetView>();
            if (workerView == null || !workerView.IsValid() || !workerView.IsOwner())
            {
                SetStatus("Zuordnung konnte nicht geloest werden: Worker ist REMOTE-owned.");
                return;
            }
            if (WorkerZdo.HasCargo(workerZdo))
            {
                SetStatus("Zuordnung bleibt aktiv: Der Arbeiter traegt noch Ladung. Erst abliefern lassen.");
                return;
            }

            WorkerZdo.ClearWorkArea(workerZdo);
            WorkerZdo.ClearTarget(workerZdo);
            WorkerState state = (WorkerState)workerZdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state != WorkerState.Follow && state != WorkerState.ReturnHomeForRest && state != WorkerState.Resting && state != WorkerState.Recover)
                workerZdo.Set(WorkerZdo.State, (int)WorkerState.ReturnHome);

            _nextWorkFlagAssignedCountRefresh = 0f;
            SetStatus($"{SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name)}: Arbeitszuweisung geloest; Schlafplatz bleibt erhalten.");
        }

        private void AssignLoadedWorkersToFlag(ZNetView flagNview, ZDO flagZdo)
        {
            Player player = Player.m_localPlayer;
            if (player == null || flagNview == null || flagZdo == null || !flagNview.IsOwner())
            {
                SetStatus("Arbeitsfahne: Zuweisung nicht moeglich (kein lokaler Netzwerkbesitz).");
                return;
            }
            string flagId = WorkFlagZdo.EnsureId(flagZdo);
            float radius = WorkFlagZdo.ReadRadius(flagZdo, _workFlagDefaultRadius.Value);
            long playerId = player.GetPlayerID();
            int assigned = 0;
            int remoteSkipped = 0;
            int homeSkipped = 0;
            int cargoSkipped = 0;

            WorkerFastLookupRuntime.CopyKnownWorkers(_loadedWorkerScratch);
            for (int i = 0; i < _loadedWorkerScratch.Count; i++)
            {
                Character character = _loadedWorkerScratch[i];
                if (character == null || character.IsDead()) continue;
                if (!WorkerFastLookupRuntime.TryGetWorker(character, false, out ZNetView workerNview, out ZDO workerZdo)) continue;
                if (workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != playerId) continue;

                WorkerRole role = (WorkerRole)workerZdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
                WorkOrder order = WorkerJobProfileRuntime.ResolveAssignmentOrder(workerZdo, role);
                if (order == WorkOrder.None) continue;
                if (WorkerZdo.HasCargo(workerZdo))
                {
                    cargoSkipped++;
                    continue;
                }
                if (!workerNview.IsOwner())
                {
                    remoteSkipped++;
                    continue;
                }
                if (!WorkerWorkAreaRules.TryValidateHomeForArea(workerZdo, _openWorkFlag.transform.position, radius, out _))
                {
                    homeSkipped++;
                    continue;
                }

                WorkerZdo.AssignWorkArea(workerZdo, flagId, WorkFlagZdo.ReadDisplayName(flagZdo), order, _openWorkFlag.transform.position, radius);
                WorkerZdo.ClearTarget(workerZdo);
                MonsterAI ai = character.GetComponent<MonsterAI>();
                if (ai != null && ai.GetFollowTarget() != null)
                    WorkerNavigationRuntime.SetNativeFollowTarget(ai, null);

                WorkerState currentState = (WorkerState)workerZdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
                if (currentState != WorkerState.ReturnHomeForRest && currentState != WorkerState.Resting && currentState != WorkerState.Recover)
                    workerZdo.Set(WorkerZdo.State, role == WorkerRole.Stonemason ? (int)WorkerState.FindJob : (int)WorkerState.MoveToWorkArea);
                assigned++;
            }

            _nextWorkFlagAssignedCountRefresh = 0f;
            RefreshOpenWorkFlagAssignedCount();
            string skipText = string.Empty;
            if (homeSkipped > 0) skipText += $"; {homeSkipped} ohne Schlafplatz in dieser Workzone";
            if (cargoSkipped > 0) skipText += $"; {cargoSkipped} mit Ladung nicht umgestellt";
            if (remoteSkipped > 0) skipText += $"; {remoteSkipped} REMOTE";
            SetStatus($"Arbeitsfahne: {assigned} geladene passende Arbeiter zugewiesen{skipText}.");
        }

        private void ReleaseLoadedWorkersFromFlag(ZDO flagZdo)
        {
            if (flagZdo == null) return;
            string flagId = WorkFlagZdo.ReadId(flagZdo);
            if (string.IsNullOrEmpty(flagId))
            {
                SetStatus("Arbeitsfahne: Keine persistente Fahnen-ID vorhanden.");
                return;
            }

            long playerId = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;
            int released = 0;
            WorkerFastLookupRuntime.CopyKnownWorkers(_loadedWorkerScratch);
            for (int i = 0; i < _loadedWorkerScratch.Count; i++)
            {
                Character character = _loadedWorkerScratch[i];
                if (character == null || character.IsDead()) continue;
                if (!WorkerFastLookupRuntime.TryGetWorker(character, true, out ZNetView workerNview, out ZDO workerZdo)) continue;
                if (playerId != 0L && workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != playerId) continue;
                if (!string.Equals(WorkerZdo.ReadWorkAreaId(workerZdo), flagId, StringComparison.Ordinal)) continue;

                WorkerZdo.ClearWorkArea(workerZdo);
                WorkerZdo.ClearTarget(workerZdo);
                WorkerState currentState = (WorkerState)workerZdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
                if (currentState != WorkerState.Follow && currentState != WorkerState.ReturnHomeForRest && currentState != WorkerState.Resting && currentState != WorkerState.Recover)
                    workerZdo.Set(WorkerZdo.State, (int)WorkerState.ReturnHome);
                released++;
            }

            _nextWorkFlagAssignedCountRefresh = 0f;
            RefreshOpenWorkFlagAssignedCount();
            SetStatus($"Arbeitsfahne: {released} geladene Zuweisungen geloest.");
        }

        private void BeginWorkZoneBirdView(WorkFlagInteractable flag, float currentRadius)
        {
            if (flag == null || _workZoneBirdView == null) return;
            ZNetView view = flag.GetNetworkView();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null || !view.IsOwner())
            {
                SetStatus("Arbeitsradius: Fahne ist nicht lokal beschreibbar.");
                return;
            }

            WorkFlagInteractable editFlag = flag;
            CloseWorkFlagMenu();
            bool started = _workZoneBirdView.Begin(editFlag, currentRadius, (finishedFlag, confirmed, radius) =>
            {
                if (confirmed && finishedFlag != null)
                {
                    ZNetView finishedView = finishedFlag.GetNetworkView();
                    ZDO finishedZdo = finishedView != null && finishedView.IsValid() ? finishedView.GetZDO() : null;
                    if (finishedZdo != null && finishedView.IsOwner())
                    {
                        WorkFlagZdo.SetRadius(finishedZdo, radius);
                        string finishedId = WorkFlagZdo.ReadId(finishedZdo);
                        UpdateLoadedWorkerWorkzoneRadius(finishedId, radius);
                        SetStatus($"Arbeitsradius gespeichert: {radius:0} m.");
                    }
                    else SetStatus("Arbeitsradius konnte nicht gespeichert werden: Fahne ist REMOTE-owned.");
                }
                else SetStatus("Arbeitsradius bearbeiten abgebrochen.");

                if (confirmed && finishedFlag != null && Player.m_localPlayer != null && Vector3.Distance(Player.m_localPlayer.transform.position, finishedFlag.transform.position) <= 10f)
                    TryOpenWorkFlagMenu(finishedFlag, Player.m_localPlayer);
            });

            if (!started)
            {
                SetStatus("Vogelperspektive konnte nicht gestartet werden.");
                if (Player.m_localPlayer != null) TryOpenWorkFlagMenu(editFlag, Player.m_localPlayer);
            }
        }

        private void UpdateLoadedWorkerWorkzoneRadius(string flagId, float radius)
        {
            if (string.IsNullOrEmpty(flagId)) return;
            WorkerFastLookupRuntime.CopyKnownWorkers(_loadedWorkerScratch);
            for (int i = 0; i < _loadedWorkerScratch.Count; i++)
            {
                Character character = _loadedWorkerScratch[i];
                if (character == null || character.IsDead()) continue;
                if (!WorkerFastLookupRuntime.TryGetWorker(character, true, out ZNetView view, out ZDO workerZdo)) continue;
                if (!string.Equals(WorkerZdo.ReadWorkAreaId(workerZdo), flagId, StringComparison.Ordinal)) continue;
                workerZdo.Set(WorkerZdo.WorkAreaRadius, Mathf.Max(5f, radius));
            }
        }

        private string EnsureWorkFlagDisplayName(ZDO flagZdo, long ownerPlayerId)
        {
            if (flagZdo == null) return string.Empty;
            string existing = WorkFlagZdo.ReadDisplayName(flagZdo);
            if (!string.IsNullOrWhiteSpace(existing)) return existing;
            int index = AllocateWorkzoneIndex(ownerPlayerId);
            return WorkFlagZdo.EnsureDisplayName(flagZdo, index);
        }

        private int AllocateWorkzoneIndex(long ownerPlayerId)
        {
            int nextLoaded = WorkFlagRegistry.GetNextDefaultIndex(ownerPlayerId);
            Player player = Player.m_localPlayer;
            if (player == null || ownerPlayerId == 0L || player.GetPlayerID() != ownerPlayerId) return nextLoaded;
            ZNetView playerView = player.GetComponent<ZNetView>();
            ZDO playerZdo = playerView != null && playerView.IsValid() ? playerView.GetZDO() : null;
            if (playerZdo == null || !playerView.IsOwner()) return nextLoaded;
            int persistedNext = Mathf.Max(1, playerZdo.GetInt(NextWorkzoneIndexKey, 1));
            int chosen = Mathf.Max(persistedNext, nextLoaded);
            playerZdo.Set(NextWorkzoneIndexKey, chosen + 1);
            return chosen;
        }

        internal bool TryInitializeWorkFlagIdentity(WorkFlagInteractable flag)
        {
            if (flag == null) return false;
            ZNetView view = flag.GetNetworkView();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null || !view.IsOwner()) return false;

            long owner = WorkFlagZdo.ReadOwner(zdo);
            if (owner == 0L)
            {
                Piece piece = flag.GetComponent<Piece>() ?? flag.GetComponentInParent<Piece>();
                long creator = piece != null ? piece.GetCreator() : 0L;
                long local = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;
                if (creator != 0L && local != 0L && creator != local) return false;
                owner = creator != 0L ? creator : local;
                if (owner == 0L) return false;
                zdo.Set(WorkFlagZdo.OwnerPlayerId, owner);
            }

            WorkFlagZdo.InitializeIfNeeded(zdo, _workFlagDefaultRadius.Value);
            WorkFlagZdo.EnsureId(zdo);
            WorkFlagRegistry.RememberPersistent(zdo);
            EnsureWorkFlagDisplayName(zdo, owner);
            return true;
        }

        private void UpdateLoadedWorkerWorkzoneNames(string flagId, string displayName)
        {
            if (string.IsNullOrEmpty(flagId)) return;
            WorkerFastLookupRuntime.CopyKnownWorkers(_loadedWorkerScratch);
            for (int i = 0; i < _loadedWorkerScratch.Count; i++)
            {
                Character character = _loadedWorkerScratch[i];
                if (character == null || character.IsDead()) continue;
                if (!WorkerFastLookupRuntime.TryGetWorker(character, true, out ZNetView view, out ZDO workerZdo)) continue;
                if (!string.Equals(WorkerZdo.ReadWorkAreaId(workerZdo), flagId, StringComparison.Ordinal)) continue;
                workerZdo.Set(WorkerZdo.WorkAreaName, displayName ?? string.Empty);
            }
        }

        internal bool TryOpenAssignedWorkerMenuFromHome(ZNetView homeView, Vector3 homePosition, Humanoid user, string homeLabel)
        {
            ZDO homeZdo = homeView != null && homeView.IsValid() ? homeView.GetZDO() : null;
            if (homeZdo == null)
            {
                SetStatus(homeLabel + ": kein gueltiger Netzwerkzustand.");
                return true;
            }
            if (!WorkerHomeAnchorZdo.IsAssigned(homeZdo))
            {
                SetStatus(homeLabel + ": noch kein Arbeiter zugewiesen.");
                return true;
            }
            return TryOpenAssignedWorkerMenuFromHome(homeZdo, homePosition, user, homeLabel);
        }

        private bool TryOpenAssignedWorkerMenuFromHome(ZDO homeZdo, Vector3 homePosition, Humanoid user, string homeLabel)
        {
            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer || homeZdo == null || !WorkerHomeAnchorZdo.IsAssigned(homeZdo)) return false;

            long owner = homeZdo.GetLong(WorkerHomeAnchorZdo.OwnerPlayerId, 0L);
            if (owner != 0L && owner != player.GetPlayerID())
            {
                SetStatus(homeLabel + " gehoert einem anderen Spieler.");
                return true;
            }

            ZDOID workerId = WorkerHomeAnchorZdo.ReadAssignedWorkerZdoId(homeZdo);
            Character worker = null;
            ZDO resolvedWorkerZdo = null;

            // Explicit interaction with the physical rest station gets first chance to repair old
            // saves from the actually loaded Worker standing/resting nearby. This is deliberately
            // shared by every rest-station family; species + role + owner + Home position keep the
            // recovery deterministic even when several stations are close together.
            worker = WorkerHomeAssignmentRuntime.ResolveLoadedWorkerForHome(homeZdo, homePosition);
            if (worker != null)
            {
                ZNetView loadedView = worker.GetComponent<ZNetView>();
                resolvedWorkerZdo = loadedView != null && loadedView.IsValid() ? loadedView.GetZDO() : null;
                if (WorkerZdo.IsWorker(resolvedWorkerZdo))
                    workerId = resolvedWorkerZdo.m_uid;
            }

            // If no matching Character is currently loaded, fall back to the durable ZDO recovery
            // path so a rest station can still manage an unloaded Worker.
            if (!WorkerZdo.IsWorker(resolvedWorkerZdo))
            {
                resolvedWorkerZdo = WorkerHomeAssignmentRuntime.ResolveWorker(homeZdo, homePosition);
                if (WorkerZdo.IsWorker(resolvedWorkerZdo))
                {
                    workerId = resolvedWorkerZdo.m_uid;
                    WorkerFastLookupRuntime.CopyKnownWorkers(_loadedWorkerScratch);
                    for (int i = 0; i < _loadedWorkerScratch.Count; i++)
                    {
                        Character candidate = _loadedWorkerScratch[i];
                        if (candidate == null || candidate.IsDead()) continue;
                        if (!WorkerFastLookupRuntime.TryGetWorker(candidate, false, out _, out ZDO workerZdo)) continue;
                        if (!workerZdo.m_uid.Equals(workerId)) continue;
                        worker = candidate;
                        break;
                    }
                }
            }

            if (worker != null)
            {
                ZNetView loadedView = worker.GetComponent<ZNetView>();
                if (loadedView != null && loadedView.IsValid() && loadedView.IsOwner())
                    return TryOpenWorkerMenu(worker, player);
            }

            // The rest station is the management console. It must remain useful even when the
            // assigned worker is away or its zone is unloaded. Resolve the durable worker ZDO by
            // the ID stored on the HomeAnchor and open a persistent menu instead of requiring a
            // live Character instance.
            ZDO persistentWorker = WorkerZdo.IsWorker(resolvedWorkerZdo) ? resolvedWorkerZdo : null;
            if (persistentWorker == null && !workerId.Equals(ZDOID.None) && ZDOMan.instance != null)
                persistentWorker = ZDOMan.instance.GetZDO(workerId);
            if (persistentWorker == null && worker != null)
            {
                ZNetView loadedView = worker.GetComponent<ZNetView>();
                persistentWorker = loadedView != null && loadedView.IsValid() ? loadedView.GetZDO() : null;
            }

            if (WorkerZdo.IsWorker(persistentWorker))
                return OpenPersistentWorkerMenu(persistentWorker, homeZdo, homePosition, homeLabel, player);

            SetStatus(homeLabel + ": Worker-ZDO derzeit nicht aufloesbar. Zuweisung bleibt bestehen; bitte Weltzustand kurz laden und erneut versuchen.");
            return true;
        }

        private bool OpenPersistentWorkerMenu(ZDO workerZdo, ZDO homeZdo, Vector3 homePosition, string homeLabel, Player player)
        {
            if (workerZdo == null || player == null || !WorkerZdo.IsWorker(workerZdo)) return false;
            long owner = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            if (owner != 0L && owner != player.GetPlayerID())
            {
                SetStatus("Dieser Arbeiter gehoert einem anderen Spieler.");
                return true;
            }

            CloseWorkFlagMenu();
            CloseWorkerContainerMenu();
            CloseWorkerInventory();
            _openWorkerMenu = null;
            _openPersistentWorkerMenuZdo = workerZdo;
            _openPersistentWorkerMenuHomeZdo = homeZdo;
            _openPersistentWorkerMenuHomePosition = homePosition;
            _openPersistentWorkerMenuHomeLabel = homeLabel ?? "Worker-Ruhestaette";
            _workerMenuScroll = Vector2.zero;
            CenterWorkerWindow();
            CaptureMenuCursor();
            SetStatus("Worker-Menue an Ruhestaette geoeffnet (persistenter Worker-Zustand).");
            return true;
        }

        private static string WithHomeSupplyHover(string text, ZDO homeZdo)
        {
            string supply = WorkerSustenanceRuntime.BuildHomeHoverSummary(homeZdo);
            return string.IsNullOrEmpty(supply) ? text : text + "\n" + supply;
        }

        internal string GetGreydwarfHomeNestHoverText(GreydwarfHomeNestInteractable homeNest)
        {
            if (!_enabled.Value || homeNest == null) return string.Empty;

            ZNetView nview = homeNest.GetNetworkView();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo == null)
                return "Grauzwerg-Wurzelnest\nNetzwerkstatus nicht verfuegbar";

            if (GreydwarfHomeAnchorZdo.IsAssigned(zdo))
            {
                string species = GreydwarfHomeAnchorZdo.ReadWorkerSpecies(zdo);
                WorkerRole role = GreydwarfHomeAnchorZdo.ReadWorkerRole(zdo);
                string text = string.IsNullOrEmpty(species)
                    ? "Grauzwerg-Wurzelnest — bewohnt\n[<color=yellow><b>Shift+E</b></color>] Worker-Menue"
                    : $"Grauzwerg-Wurzelnest — bewohnt\n{species} — {role}\n[<color=yellow><b>Shift+E</b></color>] Worker-Menue";
                return WithHomeSupplyHover(text, zdo);
            }

            return "Grauzwerg-Wurzelnest — frei\n[<color=yellow><b>E</b></color>] Arbeiter auswaehlen";
        }

        internal bool TryInteractWithGreydwarfHomeNest(GreydwarfHomeNestInteractable homeNest, Humanoid user, bool alt = false)
        {
            if (!_enabled.Value || homeNest == null) return false;

            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer)
            {
                SetStatus("Home assignment failed: interaction must come from the local player.");
                return false;
            }

            ZNetView homeView = homeNest.GetNetworkView();
            if (homeView == null || !homeView.IsValid() || homeView.GetZDO() == null)
            {
                SetStatus("Home assignment failed: Wurzelnest has no valid ZDO.");
                return true;
            }

            if (!homeView.IsOwner())
            {
                SetStatus("Home assignment blocked: Wurzelnest is REMOTE-owned. Multiplayer home RPC comes later.");
                return true;
            }

            ZDO homeZdo = homeView.GetZDO();
            if (GreydwarfHomeAnchorZdo.IsAssigned(homeZdo))
            {
                bool release = alt || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (release) return TryOpenAssignedWorkerMenuFromHome(homeView, homeNest.transform.position, user, "Grauzwerg-Wurzelnest");
                string current = GreydwarfHomeAnchorZdo.ReadWorkerSpecies(homeZdo);
                if (!release)
                {
                    SetStatus(string.IsNullOrEmpty(current) ? "Wurzelnest ist belegt. Shift+E oeffnet das Worker-Menue; Schlafplatz loesen geht dort." : $"Wurzelnest gehoert zu {current}. Shift+E oeffnet das Worker-Menue; Schlafplatz loesen geht dort.");
                    return true;
                }
                if (homeZdo.GetLong(WorkerHomeAnchorZdo.OwnerPlayerId, 0L) != 0L && homeZdo.GetLong(WorkerHomeAnchorZdo.OwnerPlayerId, 0L) != player.GetPlayerID())
                {
                    SetStatus("Wurzelnest gehoert einem anderen Spieler.");
                    return true;
                }
                WorkerHomeAssignmentRuntime.ReleaseFromHome(homeZdo, homeNest.transform.position, out bool workerUpdated);
                SetStatus($"Wurzelnest freigegeben{(workerUpdated ? "; Worker-Zuhause geloest" : "; kein geladener Worker-Link gefunden")}.");
                Logger.LogInfo($"Greydwarf home assignment released manually. Worker={current}, player={player.GetPlayerID()}, workerUpdated={workerUpdated}.");
                return true;
            }

            BeginGreydwarfHomeWorkerSelection(homeNest);
            return true;
        }

        internal bool TryUseItemOnGreydwarfHomeNest(GreydwarfHomeNestInteractable homeNest, Humanoid user, ItemDrop.ItemData item)
        {
            if (!_enabled.Value || homeNest == null || item == null) return false;

            if (GreydwarfNestSummoningRuntime.IsAncientSeed(item) ||
                GreydwarfOfferingCatalog.TryClassifyOffering(item, out GreydwarfOfferingDefinition _))
            {
                SetStatus("Spenden und Rufen gehoert zum natuerlichen Grauzwerg-Nest. Das Wurzelnest ist nur Ruhestaette/Zuhause.");
                return true;
            }

            return false;
        }

        private Character FindNearestOwnedGreydwarfWorker(Vector3 center, float radius, long playerId)
        {
            Collider[] hits = Physics.OverlapSphere(center, radius);
            Character best = null;
            float bestSqr = float.MaxValue;

            for (int i = 0; i < hits.Length; i++)
            {
                Collider hit = hits[i];
                if (hit == null) continue;
                Character character = hit.GetComponentInParent<Character>();
                if (character == null || character is Player || character.IsDead()) continue;

                string species = SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name);
                if (!IsGreydwarfHomeSpecies(species)) continue;

                ZNetView nview = character.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid() || nview.GetZDO() == null) continue;
                ZDO zdo = nview.GetZDO();
                if (!WorkerZdo.IsWorker(zdo)) continue;
                if (zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != playerId) continue;

                float sqr = (character.transform.position - center).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = character;
            }

            return best;
        }

        private static bool IsGreydwarfHomeSpecies(string species)
        {
            return string.Equals(species, "Greyling", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(species, "Greydwarf", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(species, "Greydwarf_Elite", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(species, "Greydwarf_Shaman", StringComparison.OrdinalIgnoreCase);
        }


        internal string GetTrollHomeCampHoverText(TrollHomeCampInteractable homeCamp)
        {
            if (!_enabled.Value || homeCamp == null) return string.Empty;

            ZNetView nview = homeCamp.GetNetworkView();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo == null)
                return "Troll-Lehnplatz\nNetzwerkstatus nicht verfuegbar";

            if (WorkerHomeAnchorZdo.IsAssigned(zdo))
            {
                string species = WorkerHomeAnchorZdo.ReadWorkerSpecies(zdo);
                WorkerRole role = WorkerHomeAnchorZdo.ReadWorkerRole(zdo);
                string text = string.IsNullOrEmpty(species)
                    ? "Troll-Lehnplatz — bewohnt\n[<color=yellow><b>Shift+E</b></color>] Worker-Menue"
                    : $"Troll-Lehnplatz — bewohnt\n{species} — {role}\n[<color=yellow><b>Shift+E</b></color>] Worker-Menue";
                return WithHomeSupplyHover(text, zdo);
            }

            return "Troll-Lehnplatz — frei\n[<color=yellow><b>E</b></color>] Troll auswaehlen";
        }

        internal bool TryInteractWithTrollHomeCamp(TrollHomeCampInteractable homeCamp, Humanoid user, bool alt = false)
        {
            if (!_enabled.Value || homeCamp == null) return false;

            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer)
            {
                SetStatus("Troll home assignment failed: interaction must come from the local player.");
                return false;
            }

            ZNetView homeView = homeCamp.GetNetworkView();
            if (homeView == null || !homeView.IsValid() || homeView.GetZDO() == null)
            {
                SetStatus("Troll home assignment failed: Lagerstatt has no valid ZDO.");
                return true;
            }

            if (!homeView.IsOwner())
            {
                SetStatus("Troll home assignment blocked: Lagerstatt is REMOTE-owned. Multiplayer home RPC comes later.");
                return true;
            }

            ZDO homeZdo = homeView.GetZDO();
            if (WorkerHomeAnchorZdo.IsAssigned(homeZdo))
            {
                bool release = alt || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (release) return TryOpenAssignedWorkerMenuFromHome(homeView, homeCamp.transform.position, user, "Troll-Lehnplatz");
                string current = WorkerHomeAnchorZdo.ReadWorkerSpecies(homeZdo);
                if (!release)
                {
                    SetStatus(string.IsNullOrEmpty(current) ? "Troll-Lehnplatz ist belegt. Shift+E oeffnet das Worker-Menue; Schlafplatz loesen geht dort." : $"Troll-Lehnplatz gehoert zu {current}. Shift+E oeffnet das Worker-Menue; Schlafplatz loesen geht dort.");
                    return true;
                }
                if (homeZdo.GetLong(WorkerHomeAnchorZdo.OwnerPlayerId, 0L) != 0L && homeZdo.GetLong(WorkerHomeAnchorZdo.OwnerPlayerId, 0L) != player.GetPlayerID())
                {
                    SetStatus("Troll-Lehnplatz gehoert einem anderen Spieler.");
                    return true;
                }
                WorkerHomeAssignmentRuntime.ReleaseFromHome(homeZdo, homeCamp.transform.position, out bool workerUpdated);
                SetStatus($"Troll-Lehnplatz freigegeben{(workerUpdated ? "; Worker-Zuhause geloest" : "; kein geladener Worker-Link gefunden")}.");
                Logger.LogInfo($"Troll home assignment released manually. Worker={current}, player={player.GetPlayerID()}, workerUpdated={workerUpdated}.");
                return true;
            }

            BeginTrollHomeWorkerSelection(homeCamp);
            return true;
        }

        internal string GetStoneGolemHomeHoverText(StoneGolemHomeInteractable home)
        {
            if (!_enabled.Value || home == null) return string.Empty;
            ZNetView nview = home.GetNetworkView();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo == null) return "Golem-Steinlager\nNetzwerkstatus nicht verfuegbar";
            if (WorkerHomeAnchorZdo.IsAssigned(zdo))
            {
                string species = WorkerHomeAnchorZdo.ReadWorkerSpecies(zdo);
                WorkerRole role = WorkerHomeAnchorZdo.ReadWorkerRole(zdo);
                string text = string.IsNullOrEmpty(species)
                    ? "Golem-Steinlager — bewohnt\n[<color=yellow><b>Shift+E</b></color>] Worker-Menue"
                    : $"Golem-Steinlager — bewohnt\n{species} — {role}\n[<color=yellow><b>Shift+E</b></color>] Worker-Menue";
                return WithHomeSupplyHover(text, zdo);
            }
            return "Golem-Steinlager — frei\n[<color=yellow><b>E</b></color>] Stone Golem auswaehlen";
        }

        internal bool TryInteractWithStoneGolemHome(StoneGolemHomeInteractable home, Humanoid user, bool alt = false)
        {
            if (!_enabled.Value || home == null) return false;
            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer) return false;
            ZNetView homeView = home.GetNetworkView();
            if (homeView == null || !homeView.IsValid() || homeView.GetZDO() == null)
            {
                SetStatus("Stone Golem home assignment failed: Steinlager has no valid ZDO.");
                return true;
            }
            if (!homeView.IsOwner())
            {
                SetStatus("Stone Golem home assignment blocked: Steinlager is REMOTE-owned. Multiplayer home RPC comes later.");
                return true;
            }
            ZDO homeZdo = homeView.GetZDO();
            if (WorkerHomeAnchorZdo.IsAssigned(homeZdo))
            {
                bool release = alt || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (release) return TryOpenAssignedWorkerMenuFromHome(homeView, home.transform.position, user, "Golem-Steinlager");
                string current = WorkerHomeAnchorZdo.ReadWorkerSpecies(homeZdo);
                if (!release)
                {
                    SetStatus(string.IsNullOrEmpty(current) ? "Golem-Steinlager ist belegt. Shift+E oeffnet das Worker-Menue; Schlafplatz loesen geht dort." : $"Golem-Steinlager gehoert zu {current}. Shift+E oeffnet das Worker-Menue; Schlafplatz loesen geht dort.");
                    return true;
                }
                if (homeZdo.GetLong(WorkerHomeAnchorZdo.OwnerPlayerId, 0L) != 0L && homeZdo.GetLong(WorkerHomeAnchorZdo.OwnerPlayerId, 0L) != player.GetPlayerID())
                {
                    SetStatus("Golem-Steinlager gehoert einem anderen Spieler.");
                    return true;
                }
                WorkerHomeAssignmentRuntime.ReleaseFromHome(homeZdo, home.transform.position, out bool workerUpdated);
                SetStatus($"Golem-Steinlager freigegeben{(workerUpdated ? "; Worker-Zuhause geloest" : "; kein geladener Worker-Link gefunden")}.");
                return true;
            }
            BeginStoneGolemHomeWorkerSelection(home);
            return true;
        }


        internal string GetHunterStandHomeHoverText(HunterStandHomeInteractable home)
        {
            if (!_enabled.Value || home == null) return string.Empty;
            ZNetView nview = home.GetNetworkView();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo == null) return "Draugr-Jagdplattform\nNetzwerkstatus nicht verfuegbar";
            if (WorkerHomeAnchorZdo.IsAssigned(zdo))
            {
                string species = WorkerHomeAnchorZdo.ReadWorkerSpecies(zdo);
                WorkerRole role = WorkerHomeAnchorZdo.ReadWorkerRole(zdo);
                return WithHomeSupplyHover($"Draugr-Jagdplattform — bewohnt\n{species} — {role}\n[<color=yellow><b>Shift+E</b></color>] Worker-Menue", zdo);
            }
            if (!home.IsElevatedEnough(out float elevation))
            {
                return $"Draugr-Jagdplattform — <color=#ff6666><b>ZU NIEDRIG</b></color>\n" +
                       $"Hoehe: {elevation:0.0} m / mindestens {HunterStandMarker.MinimumElevation:0.0} m\n" +
                       "[<color=yellow><b>E</b></color>] Hinweis anzeigen";
            }
            return "Draugr-Jagdplattform — frei\n[<color=yellow><b>E</b></color>] Schlafplatz auswaehlen, dann Worker mit E";
        }

        internal bool TryInteractWithHunterStandHome(HunterStandHomeInteractable home, Humanoid user, bool alt = false)
        {
            if (!_enabled.Value || home == null) return false;
            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer) return false;
            ZNetView homeView = home.GetNetworkView();
            if (homeView == null || !homeView.IsValid() || homeView.GetZDO() == null)
            {
                SetStatus("Hunter home assignment failed: Jagdplattform has no valid ZDO.");
                return true;
            }
            if (!homeView.IsOwner())
            {
                SetStatus("Hunter home assignment blocked: Jagdplattform is REMOTE-owned.");
                return true;
            }
            if (!home.IsElevatedEnough(out float platformElevation))
            {
                string message = $"Jagdplattform zu niedrig: {platformElevation:0.0} m. Mindestens {HunterStandMarker.MinimumElevation:0.0} m ueber dem Boden erforderlich.";
                SetStatus(message);
                try { player.Message(MessageHud.MessageType.Center, message); } catch { }
                return true;
            }
            ZDO homeZdo = homeView.GetZDO();
            if (WorkerHomeAnchorZdo.IsAssigned(homeZdo))
            {
                bool release = alt || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (release) return TryOpenAssignedWorkerMenuFromHome(homeView, home.transform.position, user, "Draugr-Jagdplattform");
                if (!release)
                {
                    SetStatus("Draugr-Jagdplattform ist belegt. Shift+E oeffnet das Worker-Menue; Schlafplatz loesen geht dort.");
                    return true;
                }
                if (homeZdo.GetLong(WorkerHomeAnchorZdo.OwnerPlayerId, 0L) != 0L && homeZdo.GetLong(WorkerHomeAnchorZdo.OwnerPlayerId, 0L) != player.GetPlayerID())
                {
                    SetStatus("Draugr-Jagdplattform gehoert einem anderen Spieler.");
                    return true;
                }
                WorkerHomeAssignmentRuntime.ReleaseFromHome(homeZdo, home.GetHomePoint(), out bool workerUpdated);
                SetStatus($"Draugr-Jagdplattform freigegeben{(workerUpdated ? "; Worker-Zuhause geloest" : string.Empty)}.");
                return true;
            }

            BeginGenericHomeWorkerSelection(homeView, home.GetHomePoint(), WorkerHomeProfileCatalog.HunterStandKind, "Draugr-Jagdplattform");
            return true;
        }

        private Character FindNearestOwnedRangedDraugrWorker(Vector3 center, float radius, long playerId)
        {
            Collider[] hits = Physics.OverlapSphere(center, radius);
            Character best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider hit = hits[i];
                if (hit == null) continue;
                Character character = hit.GetComponentInParent<Character>();
                if (character == null || character is Player || character.IsDead()) continue;
                string species = SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name);
                if (!string.Equals(species, "Draugr_Ranged", StringComparison.OrdinalIgnoreCase)) continue;
                ZNetView view = character.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (!WorkerZdo.IsWorker(zdo)) continue;
                if (zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != playerId) continue;
                float sqr = (character.transform.position - center).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = character;
            }
            return best;
        }

        internal string GetDraugrHomeHoverText(DraugrHomeInteractable home)
        {
            if (!_enabled.Value || home == null) return string.Empty;
            ZNetView nview = home.GetNetworkView();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo == null) return "Draugr-Ruhestätte\nNetzwerkstatus nicht verfuegbar";
            if (WorkerHomeAnchorZdo.IsAssigned(zdo))
            {
                string species = WorkerHomeAnchorZdo.ReadWorkerSpecies(zdo);
                WorkerRole role = WorkerHomeAnchorZdo.ReadWorkerRole(zdo);
                return WithHomeSupplyHover($"Draugr-Ruhestätte — bewohnt\n{species} — {role}\n[<color=yellow><b>Shift+E</b></color>] Worker-Menue", zdo);
            }
            return "Draugr-Ruhestätte — frei\n[<color=yellow><b>E</b></color>] Schlafplatz auswaehlen, dann Worker mit E";
        }

        internal bool TryInteractWithDraugrHome(DraugrHomeInteractable home, Humanoid user, bool alt = false)
        {
            if (!_enabled.Value || home == null) return false;
            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer) return false;
            ZNetView homeView = home.GetNetworkView();
            if (homeView == null || !homeView.IsValid() || homeView.GetZDO() == null)
            {
                SetStatus("Draugr home assignment failed: Ruhestätte has no valid ZDO.");
                return true;
            }
            if (!homeView.IsOwner())
            {
                SetStatus("Draugr home assignment blocked: Ruhestätte is REMOTE-owned.");
                return true;
            }
            ZDO homeZdo = homeView.GetZDO();
            if (WorkerHomeAnchorZdo.IsAssigned(homeZdo))
            {
                bool release = alt || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (release) return TryOpenAssignedWorkerMenuFromHome(homeView, home.transform.position, user, "Draugr-Ruhestaette");
                if (!release)
                {
                    SetStatus("Draugr-Ruhestätte ist belegt. Shift+E oeffnet das Worker-Menue; Schlafplatz loesen geht dort.");
                    return true;
                }
                if (homeZdo.GetLong(WorkerHomeAnchorZdo.OwnerPlayerId, 0L) != 0L && homeZdo.GetLong(WorkerHomeAnchorZdo.OwnerPlayerId, 0L) != player.GetPlayerID())
                {
                    SetStatus("Draugr-Ruhestätte gehoert einem anderen Spieler.");
                    return true;
                }
                WorkerHomeAssignmentRuntime.ReleaseFromHome(homeZdo, home.transform.position, out bool workerUpdated);
                SetStatus($"Draugr-Ruhestätte freigegeben{(workerUpdated ? "; Worker-Zuhause geloest" : string.Empty)}.");
                return true;
            }

            BeginGenericHomeWorkerSelection(homeView, home.transform.position, WorkerHomeProfileCatalog.DraugrRestKind, "Draugr-Ruhestaette");
            return true;
        }

        private Character FindNearestOwnedDraugrWorker(Vector3 center, float radius, long playerId)
        {
            Collider[] hits = Physics.OverlapSphere(center, radius);
            Character best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider hit = hits[i];
                if (hit == null) continue;
                Character character = hit.GetComponentInParent<Character>();
                if (character == null || character is Player || character.IsDead()) continue;
                string species = SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name);
                if (!species.StartsWith("Draugr", StringComparison.OrdinalIgnoreCase)) continue;
                ZNetView view = character.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (!WorkerZdo.IsWorker(zdo)) continue;
                if (zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != playerId) continue;
                float sqr = (character.transform.position - center).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = character;
            }
            return best;
        }

        internal string GetFulingHomeHoverText(FulingHomeInteractable home)
        {
            if (!_enabled.Value || home == null) return string.Empty;
            string displayName = home.DisplayName;
            ZNetView nview = home.GetNetworkView();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo == null) return $"{displayName}\nNetzwerkstatus nicht verfuegbar";
            if (WorkerHomeAnchorZdo.IsAssigned(zdo))
            {
                string species = WorkerHomeAnchorZdo.ReadWorkerSpecies(zdo);
                WorkerRole role = WorkerHomeAnchorZdo.ReadWorkerRole(zdo);
                string text = string.IsNullOrEmpty(species)
                    ? $"{displayName} — bewohnt\n[<color=yellow><b>Shift+E</b></color>] Worker-Menue"
                    : $"{displayName} — bewohnt\n{species} — {role}\n[<color=yellow><b>Shift+E</b></color>] Worker-Menue";
                return WithHomeSupplyHover(text, zdo);
            }
            return $"{displayName} — frei\n[<color=yellow><b>E</b></color>] Schlafplatz auswaehlen, dann Worker mit E";
        }

        internal bool TryInteractWithFulingHome(FulingHomeInteractable home, Humanoid user, bool alt = false)
        {
            if (!_enabled.Value || home == null) return false;
            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer) return false;
            string displayName = home.DisplayName;

            ZNetView homeView = home.GetNetworkView();
            if (homeView == null || !homeView.IsValid() || homeView.GetZDO() == null)
            {
                SetStatus($"Fuling home assignment failed: {displayName} has no valid ZDO.");
                return true;
            }
            if (!homeView.IsOwner())
            {
                SetStatus($"Fuling home assignment blocked: {displayName} is REMOTE-owned.");
                return true;
            }

            ZDO homeZdo = homeView.GetZDO();
            if (WorkerHomeAnchorZdo.IsAssigned(homeZdo))
            {
                bool release = alt || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (release)
                    return TryOpenAssignedWorkerMenuFromHome(homeView, home.transform.position, user, displayName);

                SetStatus($"{displayName} ist belegt. Shift+E oeffnet das Worker-Menue; Schlafplatz loesen geht dort.");
                return true;
            }

            BeginGenericHomeWorkerSelection(homeView, home.transform.position, WorkerHomeProfileCatalog.FulingCampKind, displayName);
            return true;
        }

        private Character FindNearestOwnedFulingWorker(Vector3 center, float radius, long playerId)
        {
            Collider[] hits = Physics.OverlapSphere(center, radius);
            Character best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider hit = hits[i];
                if (hit == null) continue;
                Character character = hit.GetComponentInParent<Character>();
                if (character == null || character is Player || character.IsDead()) continue;
                string species = SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name);
                if (!species.StartsWith("Fuling", StringComparison.OrdinalIgnoreCase)) continue;
                ZNetView view = character.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (!WorkerZdo.IsWorker(zdo)) continue;
                if (zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != playerId) continue;
                float sqr = (character.transform.position - center).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = character;
            }
            return best;
        }

        internal string GetNeckHomeHoverText(NeckHomeInteractable home)
        {
            if (!_enabled.Value || home == null) return string.Empty;
            ZNetView nview = home.GetNetworkView();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo == null) return "Ufernest\nNetzwerkstatus nicht verfuegbar";
            if (WorkerHomeAnchorZdo.IsAssigned(zdo))
            {
                string species = WorkerHomeAnchorZdo.ReadWorkerSpecies(zdo);
                WorkerRole role = WorkerHomeAnchorZdo.ReadWorkerRole(zdo);
                return WithHomeSupplyHover($"Ufernest — bewohnt\n{species} — {role}\n[<color=yellow><b>Shift+E</b></color>] Worker-Menue", zdo);
            }
            return "Ufernest — frei\n[<color=yellow><b>E</b></color>] Schlafplatz auswaehlen, dann Worker mit E";
        }

        internal bool TryInteractWithNeckHome(NeckHomeInteractable home, Humanoid user, bool alt = false)
        {
            if (!_enabled.Value || home == null) return false;
            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer) return false;

            ZNetView homeView = home.GetNetworkView();
            if (homeView == null || !homeView.IsValid() || homeView.GetZDO() == null)
            {
                SetStatus("Ufernest-Zuweisung fehlgeschlagen: kein gueltiges ZDO.");
                return true;
            }
            if (!homeView.IsOwner())
            {
                SetStatus("Ufernest-Zuweisung blockiert: REMOTE-owned.");
                return true;
            }

            ZDO homeZdo = homeView.GetZDO();
            if (WorkerHomeAnchorZdo.IsAssigned(homeZdo))
            {
                SetStatus("Ufernest ist belegt. Shift+E oeffnet das Worker-Menue; Schlafplatz loesen geht dort.");
                return true;
            }

            BeginGenericHomeWorkerSelection(homeView, home.transform.position, WorkerHomeProfileCatalog.WatersideNestKind, "Ufernest");
            return true;
        }

        private Character FindNearestOwnedNeckWorker(Vector3 center, float radius, long playerId)
        {
            Collider[] hits = Physics.OverlapSphere(center, radius);
            Character best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider hit = hits[i];
                if (hit == null) continue;
                Character character = hit.GetComponentInParent<Character>();
                if (character == null || character is Player || character.IsDead()) continue;
                if (!string.Equals(SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name), "Neck", StringComparison.OrdinalIgnoreCase)) continue;
                ZNetView view = character.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (!WorkerZdo.IsWorker(zdo)) continue;
                if (zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != playerId) continue;
                float sqr = (character.transform.position - center).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = character;
            }
            return best;
        }

        internal string GetWolfHomeHoverText(WolfHomeInteractable home)
        {
            if (!_enabled.Value || home == null) return string.Empty;
            ZNetView nview = home.GetNetworkView();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo == null) return "Wolfsbau\nNetzwerkstatus nicht verfuegbar";
            if (WorkerHomeAnchorZdo.IsAssigned(zdo))
            {
                string species = WorkerHomeAnchorZdo.ReadWorkerSpecies(zdo);
                WorkerRole role = WorkerHomeAnchorZdo.ReadWorkerRole(zdo);
                return WithHomeSupplyHover($"Wolfsbau — bewohnt\n{species} — {role}\n[<color=yellow><b>Shift+E</b></color>] Worker-Menue", zdo);
            }
            return "Wolfsbau — frei\n[<color=yellow><b>E</b></color>] Schlafplatz auswaehlen, dann Wolf mit E";
        }

        internal bool TryInteractWithWolfHome(WolfHomeInteractable home, Humanoid user, bool alt = false)
        {
            if (!_enabled.Value || home == null) return false;
            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer) return false;

            ZNetView homeView = home.GetNetworkView();
            if (homeView == null || !homeView.IsValid() || homeView.GetZDO() == null)
            {
                SetStatus("Wolfsbau-Zuweisung fehlgeschlagen: kein gueltiges ZDO.");
                return true;
            }
            if (!homeView.IsOwner())
            {
                SetStatus("Wolfsbau-Zuweisung blockiert: REMOTE-owned.");
                return true;
            }

            ZDO homeZdo = homeView.GetZDO();
            if (WorkerHomeAnchorZdo.IsAssigned(homeZdo))
            {
                if (alt || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                    return TryOpenAssignedWorkerMenuFromHome(homeView, home.transform.position, user, "Wolfsbau");

                SetStatus("Wolfsbau ist belegt. Shift+E oeffnet das Worker-Menue; Schlafplatz loesen geht dort.");
                return true;
            }

            BeginGenericHomeWorkerSelection(homeView, home.transform.position, WorkerHomeProfileCatalog.WolfDenKind, "Wolfsbau");
            return true;
        }

        internal string GetLoxHomeHoverText(LoxHomeInteractable home)
        {
            if (!_enabled.Value || home == null) return string.Empty;
            ZNetView nview = home.GetNetworkView();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo == null) return "Lox-Ruheplatz\nNetzwerkstatus nicht verfuegbar";
            if (WorkerHomeAnchorZdo.IsAssigned(zdo))
            {
                string species = WorkerHomeAnchorZdo.ReadWorkerSpecies(zdo);
                WorkerRole role = WorkerHomeAnchorZdo.ReadWorkerRole(zdo);
                return WithHomeSupplyHover($"Lox-Ruheplatz — bewohnt\n{species} — {role}\n[<color=yellow><b>Shift+E</b></color>] Worker-Menue", zdo);
            }
            return "Lox-Ruheplatz — frei\n[<color=yellow><b>E</b></color>] Schlafplatz auswaehlen, dann Worker mit E";
        }

        internal bool TryInteractWithLoxHome(LoxHomeInteractable home, Humanoid user, bool alt = false)
        {
            if (!_enabled.Value || home == null) return false;
            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer) return false;

            ZNetView homeView = home.GetNetworkView();
            if (homeView == null || !homeView.IsValid() || homeView.GetZDO() == null)
            {
                SetStatus("Lox home assignment failed: Ruheplatz has no valid ZDO.");
                return true;
            }
            if (!homeView.IsOwner())
            {
                SetStatus("Lox home assignment blocked: Ruheplatz is REMOTE-owned.");
                return true;
            }

            ZDO homeZdo = homeView.GetZDO();
            if (WorkerHomeAnchorZdo.IsAssigned(homeZdo))
            {
                bool release = alt || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (release) return TryOpenAssignedWorkerMenuFromHome(homeView, home.transform.position, user, "Lox-Ruheplatz");
                if (!release)
                {
                    SetStatus("Lox-Ruheplatz ist belegt. Shift+E oeffnet das Worker-Menue; Schlafplatz loesen geht dort.");
                    return true;
                }
                if (homeZdo.GetLong(WorkerHomeAnchorZdo.OwnerPlayerId, 0L) != 0L && homeZdo.GetLong(WorkerHomeAnchorZdo.OwnerPlayerId, 0L) != player.GetPlayerID())
                {
                    SetStatus("Lox-Ruheplatz gehoert einem anderen Spieler.");
                    return true;
                }
                WorkerHomeAssignmentRuntime.ReleaseFromHome(homeZdo, home.transform.position, out bool workerUpdated);
                SetStatus($"Lox-Ruheplatz freigegeben{(workerUpdated ? "; Worker-Zuhause geloest" : string.Empty)}.");
                return true;
            }

            BeginGenericHomeWorkerSelection(homeView, home.transform.position, WorkerHomeProfileCatalog.LoxShelterKind, "Lox-Ruheplatz");
            return true;
        }

        private Character FindNearestOwnedLoxWorker(Vector3 center, float radius, long playerId)
        {
            Collider[] hits = Physics.OverlapSphere(center, radius);
            Character best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider hit = hits[i];
                if (hit == null) continue;
                Character character = hit.GetComponentInParent<Character>();
                if (character == null || character is Player || character.IsDead()) continue;
                if (!string.Equals(SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name), "Lox", StringComparison.OrdinalIgnoreCase)) continue;
                ZNetView view = character.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (!WorkerZdo.IsWorker(zdo)) continue;
                if (zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != playerId) continue;
                float sqr = (character.transform.position - center).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = character;
            }
            return best;
        }

        internal string GetDrakeHomeHoverText(DrakeHomeInteractable home)
        {
            if (!_enabled.Value || home == null) return string.Empty;
            ZNetView nview = home.GetNetworkView();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo == null) return "Drake-Landeplatz\nNetzwerkstatus nicht verfuegbar";
            if (WorkerHomeAnchorZdo.IsAssigned(zdo))
            {
                string species = WorkerHomeAnchorZdo.ReadWorkerSpecies(zdo);
                WorkerRole role = WorkerHomeAnchorZdo.ReadWorkerRole(zdo);
                return WithHomeSupplyHover($"Drake-Landeplatz — bewohnt\n{species} — {role}\n[<color=yellow><b>Shift+E</b></color>] Worker-Menue", zdo);
            }
            return "Drake-Landeplatz — frei\n[<color=yellow><b>E</b></color>] Schlafplatz auswaehlen, dann Worker mit E";
        }

        internal bool TryInteractWithDrakeHome(DrakeHomeInteractable home, Humanoid user, bool alt = false)
        {
            if (!_enabled.Value || home == null) return false;
            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer) return false;

            ZNetView homeView = home.GetNetworkView();
            if (homeView == null || !homeView.IsValid() || homeView.GetZDO() == null)
            {
                SetStatus("Drake home assignment failed: Landeplatz has no valid ZDO.");
                return true;
            }
            if (!homeView.IsOwner())
            {
                SetStatus("Drake home assignment blocked: Landeplatz is REMOTE-owned.");
                return true;
            }

            ZDO homeZdo = homeView.GetZDO();
            if (WorkerHomeAnchorZdo.IsAssigned(homeZdo))
            {
                bool release = alt || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (release) return TryOpenAssignedWorkerMenuFromHome(homeView, home.transform.position, user, "Drake-Landeplatz");
                if (!release)
                {
                    SetStatus("Drake-Landeplatz ist belegt. Shift+E oeffnet das Worker-Menue; Schlafplatz loesen geht dort.");
                    return true;
                }
                if (homeZdo.GetLong(WorkerHomeAnchorZdo.OwnerPlayerId, 0L) != 0L && homeZdo.GetLong(WorkerHomeAnchorZdo.OwnerPlayerId, 0L) != player.GetPlayerID())
                {
                    SetStatus("Drake-Landeplatz gehoert einem anderen Spieler.");
                    return true;
                }
                WorkerHomeAssignmentRuntime.ReleaseFromHome(homeZdo, home.transform.position, out bool workerUpdated);
                SetStatus($"Drake-Landeplatz freigegeben{(workerUpdated ? "; Worker-Zuhause geloest" : string.Empty)}.");
                return true;
            }

            BeginGenericHomeWorkerSelection(homeView, home.transform.position, WorkerHomeProfileCatalog.DrakePerchKind, "Drake-Landeplatz");
            return true;
        }

        private Character FindNearestOwnedDrakeWorker(Vector3 center, float radius, long playerId)
        {
            Collider[] hits = Physics.OverlapSphere(center, radius);
            Character best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider hit = hits[i];
                if (hit == null) continue;
                Character character = hit.GetComponentInParent<Character>();
                if (character == null || character is Player || character.IsDead()) continue;
                if (!string.Equals(SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name), "Drake", StringComparison.OrdinalIgnoreCase)) continue;
                ZNetView view = character.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (!WorkerZdo.IsWorker(zdo)) continue;
                if (zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != playerId) continue;
                float sqr = (character.transform.position - center).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = character;
            }
            return best;
        }

        internal string GetSurtlingHomeHoverText(SurtlingHomeInteractable home)
        {
            if (!_enabled.Value || home == null) return string.Empty;
            ZNetView nview = home.GetNetworkView();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo == null) return "Surtling-Ruhestätte\nNetzwerkstatus nicht verfuegbar";
            if (WorkerHomeAnchorZdo.IsAssigned(zdo))
            {
                string species = WorkerHomeAnchorZdo.ReadWorkerSpecies(zdo);
                WorkerRole role = WorkerHomeAnchorZdo.ReadWorkerRole(zdo);
                return WithHomeSupplyHover($"Surtling-Ruhestätte — bewohnt\n{species} — {role}\n[<color=yellow><b>Shift+E</b></color>] Worker-Menue", zdo);
            }
            return "Surtling-Ruhestätte — frei\n[<color=yellow><b>E</b></color>] Schlafplatz auswaehlen, dann Worker mit E\n<color=#ff9b42>Dauerfeuer — kein Holz erforderlich</color>";
        }

        internal bool TryInteractWithSurtlingHome(SurtlingHomeInteractable home, Humanoid user, bool alt = false)
        {
            if (!_enabled.Value || home == null) return false;
            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer) return false;

            ZNetView homeView = home.GetNetworkView();
            if (homeView == null || !homeView.IsValid() || homeView.GetZDO() == null)
            {
                SetStatus("Surtling home assignment failed: Ruhestätte has no valid ZDO.");
                return true;
            }
            if (!homeView.IsOwner())
            {
                SetStatus("Surtling home assignment blocked: Ruhestätte is REMOTE-owned.");
                return true;
            }

            ZDO homeZdo = homeView.GetZDO();
            if (WorkerHomeAnchorZdo.IsAssigned(homeZdo))
            {
                if (alt || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                    return TryOpenAssignedWorkerMenuFromHome(homeView, home.transform.position, user, "Surtling-Ruhestätte");

                SetStatus("Surtling-Ruhestätte ist belegt. Shift+E oeffnet das Worker-Menue.");
                return true;
            }

            BeginGenericHomeWorkerSelection(homeView, home.transform.position, WorkerHomeProfileCatalog.FireHearthKind, "Surtling-Ruhestätte");
            return true;
        }

        private Character FindNearestOwnedSurtlingWorker(Vector3 center, float radius, long playerId)
        {
            Collider[] hits = Physics.OverlapSphere(center, radius);
            Character best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider hit = hits[i];
                if (hit == null) continue;
                Character character = hit.GetComponentInParent<Character>();
                if (character == null || character is Player || character.IsDead()) continue;
                if (!string.Equals(SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name), "Surtling", StringComparison.OrdinalIgnoreCase)) continue;
                ZNetView view = character.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (!WorkerZdo.IsWorker(zdo)) continue;
                if (zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != playerId) continue;
                float sqr = (character.transform.position - center).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = character;
            }
            return best;
        }

        private Character FindNearestOwnedTrollWorker(Vector3 center, float radius, long playerId)
        {
            Collider[] hits = Physics.OverlapSphere(center, radius);
            Character best = null;
            float bestSqr = float.MaxValue;

            for (int i = 0; i < hits.Length; i++)
            {
                Collider hit = hits[i];
                if (hit == null) continue;
                Character character = hit.GetComponentInParent<Character>();
                if (character == null || character is Player || character.IsDead()) continue;

                string species = SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name);
                if (!string.Equals(species, "Troll", StringComparison.OrdinalIgnoreCase)) continue;

                ZNetView nview = character.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid() || nview.GetZDO() == null) continue;
                ZDO zdo = nview.GetZDO();
                if (!WorkerZdo.IsWorker(zdo)) continue;
                if (zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != playerId) continue;

                float sqr = (character.transform.position - center).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = character;
            }

            return best;
        }


        internal bool TryInteractWithGreydwarfNest(SpawnArea spawner, Humanoid user)
        {
            if (!_enabled.Value || spawner == null) return false;
            if (Time.unscaledTime < _nextOfferingAllowed) return false;
            _nextOfferingAllowed = Time.unscaledTime + 0.35f;

            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer)
            {
                SetStatus("Offering failed: interaction must come from the local player.");
                return false;
            }

            float distance = Vector3.Distance(player.transform.position, spawner.transform.position);
            if (distance > Mathf.Max(1f, _spawnerOfferingRadius.Value))
            {
                SetStatus($"Move closer to the Greydwarf nest ({distance:0.0} m / {_spawnerOfferingRadius.Value:0.0} m).");
                return true;
            }

            ZNetView nview = spawner.GetComponent<ZNetView>();
            if (nview == null) nview = spawner.GetComponentInParent<ZNetView>();
            if (nview == null || !nview.IsValid() || nview.GetZDO() == null)
            {
                SetStatus("Offering failed: nest has no valid network state.");
                return true;
            }

            if (!nview.IsOwner())
            {
                SetStatus("Offering blocked: nest is REMOTE-owned. Multiplayer offering RPC is not enabled yet.");
                return true;
            }

            ZDO zdo = nview.GetZDO();
            if (SpawnerRecruitmentZdo.IsConsumed(zdo))
            {
                _greydwarfRecruitment?.EnforcePacifiedSpawner(spawner);
                if (!SpawnerRecruitmentZdo.IsHomeSeedGranted(zdo))
                {
                    TryGrantGreydwarfHomeSeed(spawner, player, zdo, false);
                    return true;
                }

                SetStatus($"Grauzwerg-Nest ist als Rufer aktiv. Benutze einen Uralten Samen direkt am Nest. {GreydwarfNestSummoningRuntime.BuildChanceText(zdo)}");
                return true;
            }

            if (SpawnerRecruitmentZdo.IsPacified(zdo))
            {
                _greydwarfRecruitment?.EnforcePacifiedSpawner(spawner);
                TryFinalizeGreydwarfRecruitment(spawner, player, zdo);
                return true;
            }

            Inventory inventory = player.GetInventory();
            if (!GreydwarfOfferingCatalog.TryFindOffering(inventory, out ItemDrop.ItemData item, out GreydwarfOfferingDefinition offering))
            {
                SetStatus($"Keine passende Spende im Inventar. {GreydwarfOfferingCatalog.DescribeAcceptedOfferings()}");
                return true;
            }

            return ApplyGreydwarfNestOffering(spawner, player, zdo, inventory, item, offering, 1);
        }

        internal bool TryUseItemOnGreydwarfNest(SpawnArea spawner, Humanoid user, ItemDrop.ItemData item)
        {
            if (!_enabled.Value || spawner == null || item == null) return false;
            if (Time.unscaledTime < _nextOfferingAllowed) return true;
            _nextOfferingAllowed = Time.unscaledTime + 0.25f;

            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer)
            {
                SetStatus("Nest-Interaktion fehlgeschlagen: lokaler Spieler fehlt.");
                return true;
            }

            float distance = Vector3.Distance(player.transform.position, spawner.transform.position);
            if (distance > Mathf.Max(1f, _spawnerOfferingRadius.Value))
            {
                SetStatus($"Naeher ans Grauzwerg-Nest gehen ({distance:0.0} m / {_spawnerOfferingRadius.Value:0.0} m).");
                return true;
            }

            ZNetView nview = spawner.GetComponent<ZNetView>();
            if (nview == null) nview = spawner.GetComponentInParent<ZNetView>();
            if (nview == null || !nview.IsValid() || nview.GetZDO() == null)
            {
                SetStatus("Nest-Interaktion fehlgeschlagen: kein gueltiger ZDO.");
                return true;
            }
            if (!nview.IsOwner())
            {
                SetStatus("Nest-Interaktion blockiert: REMOTE-owned. Multiplayer-RPC kommt spaeter.");
                return true;
            }

            ZDO zdo = nview.GetZDO();
            Inventory inventory = player.GetInventory();
            if (inventory == null) return true;

            if (GreydwarfNestSummoningRuntime.IsAncientSeed(item))
            {
                if (!SpawnerRecruitmentZdo.IsPacified(zdo))
                {
                    SetStatus("Der Uralte Samen reagiert noch nicht. Erst das Nest auf 100% bringen.");
                    return true;
                }

                _greydwarfRecruitment?.EnforcePacifiedSpawner(spawner);

                if (!SpawnerRecruitmentZdo.IsHomeSeedGranted(zdo))
                {
                    SetStatus("Das Nest ist befriedet, aber sein erster Uralter Samen wurde noch nicht freigegeben. Dein eigener Samen wird nicht verbraucht.");
                    TryGrantGreydwarfHomeSeed(spawner, player, zdo, false);
                    return true;
                }

                bool firstSummon = !SpawnerRecruitmentZdo.IsConsumed(zdo);
                string species = GreydwarfNestSummoningRuntime.ChooseSpecies(zdo);
                if (string.IsNullOrEmpty(species) || _greydwarfRecruitment == null)
                {
                    SetStatus("Rufritual nicht verfuegbar: das Nest hat keine gespeicherten Rufchancen.");
                    return true;
                }

                int originalStack = item.m_stack;
                if (!GreydwarfOfferingCatalog.TryConsumeOne(inventory, item, DebugLog))
                {
                    SetStatus("Rufritual fehlgeschlagen: Uralter Samen konnte nicht sicher verbraucht werden.");
                    return true;
                }

                if (_greydwarfRecruitment.TrySummonOwnedWorker(
                    species,
                    player,
                    spawner.transform.position,
                    Mathf.Max(5f, _workRadius.Value),
                    out Character worker,
                    out string failure))
                {
                    if (firstSummon)
                        SpawnerRecruitmentZdo.MarkConsumed(zdo, species, player.GetPlayerID());
                    else
                        SpawnerRecruitmentZdo.RecordRepeatSummon(zdo);
                    _lastWorker = worker;
                    string display = GreydwarfNestSummoningRuntime.GetSpeciesDisplayName(species);
                    WorkerRole role = WorkerRole.None;
                    SpeciesRoleRegistry.TryGetRole(species, out role);
                    SetStatus(firstSummon
                        ? $"Uralter Samen verbraucht: erster Worker gerufen — {display} ({role}). {GreydwarfNestSummoningRuntime.BuildChanceText(zdo)}"
                        : $"Uralter Samen verbraucht: {display} gerufen ({role}). Die Rufchancen des Nestes bleiben unveraendert. {GreydwarfNestSummoningRuntime.BuildChanceText(zdo)}");
                    Logger.LogInfo($"Greydwarf pacified-nest summon complete. First={firstSummon}, Species={species}, role={role}, player={player.GetPlayerID()}, nest={spawner.transform.position}.");
                    return true;
                }

                bool refunded = GreydwarfOfferingCatalog.TryRestoreConsumed(inventory, item, originalStack, DebugLog);
                SetStatus(refunded
                    ? $"Rufritual fehlgeschlagen: {failure}. Uralter Samen wurde zurueckgegeben."
                    : $"Rufritual fehlgeschlagen: {failure}. ACHTUNG: Uralter Samen konnte nicht automatisch zurueckgegeben werden.");
                return true;
            }

            if (!GreydwarfOfferingCatalog.TryClassifyOffering(item, out GreydwarfOfferingDefinition offering))
                return false;

            if (SpawnerRecruitmentZdo.IsPacified(zdo))
            {
                SetStatus($"Nest ist bereits auf 100%. Weitere Spenden sind nicht noetig; die gespeicherten Rufchancen bleiben dauerhaft. {GreydwarfNestSummoningRuntime.BuildChanceText(zdo)}");
                return true;
            }

            int requested = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                ? Mathf.Max(1, item.m_stack)
                : 1;
            int amount = GreydwarfOfferingCatalog.GetMaxUsefulItemCount(zdo, offering, requested);
            if (amount <= 0) return true;
            return ApplyGreydwarfNestOffering(spawner, player, zdo, inventory, item, offering, amount);
        }

        private bool ApplyGreydwarfNestOffering(
            SpawnArea spawner,
            Player player,
            ZDO zdo,
            Inventory inventory,
            ItemDrop.ItemData item,
            GreydwarfOfferingDefinition offering,
            int amount)
        {
            if (spawner == null || player == null || zdo == null || inventory == null || item == null || offering == null)
                return true;

            amount = GreydwarfOfferingCatalog.GetMaxUsefulItemCount(zdo, offering, Mathf.Max(1, amount));
            if (amount <= 0) return true;

            if (!GreydwarfOfferingCatalog.TryConsumeAmount(inventory, item, amount, DebugLog))
            {
                SetStatus("Spende fehlgeschlagen: Gegenstand konnte nicht sicher aus dem Inventar entfernt werden.");
                return true;
            }

            int requestedProgress = offering.Progress * amount;
            int progress = SpawnerRecruitmentZdo.AddProgressAndOffering(
                zdo,
                requestedProgress,
                offering.Kind,
                player.GetPlayerID(),
                RecruitmentCatalog.GreydwarfFamilyId,
                out int creditedProgress);

            int required = Mathf.Max(1, SpawnerRecruitmentZdo.ReadRequired(zdo));
            bool pacified = SpawnerRecruitmentZdo.IsPacified(zdo);
            string itemLabel = GreydwarfOfferingCatalog.GetDisplayName(item, offering);
            string chances = GreydwarfNestSummoningRuntime.BuildChanceText(zdo);

            DebugLog($"Greydwarf nest offering accepted: prefab={offering.PrefabName}, kind={offering.Kind}, items={amount}, credited={creditedProgress}, progress={progress}/{required}, player={player.GetPlayerID()}, nest={spawner.gameObject.name}.");

            if (pacified)
            {
                _greydwarfRecruitment?.EnforcePacifiedSpawner(spawner);
                SetStatus($"Nest 100%! {itemLabel}: {amount} gespendet (+{creditedProgress}) | {chances}. Nest befriedet — Uralter Samen wird freigegeben...");
                TryFinalizeGreydwarfRecruitment(spawner, player, zdo);
            }
            else
            {
                SetStatus($"{itemLabel}: {amount} gespendet (+{creditedProgress}) | {progress}/{required} | {chances}");
            }

            return true;
        }


        private void TryFinalizeGreydwarfRecruitment(SpawnArea spawner, Player player, ZDO nestZdo)
        {
            if (spawner == null || player == null || nestZdo == null) return;
            if (!SpawnerRecruitmentZdo.IsPacified(nestZdo)) return;

            _greydwarfRecruitment?.EnforcePacifiedSpawner(spawner);

            // v0.5.404: reaching 100% only pacifies the nest. No worker is free. The nest
            // releases one Ancient Seed, and that seed must then be used on the nest for the
            // first weighted summon. Every later summon also costs one Ancient Seed.
            if (!SpawnerRecruitmentZdo.IsHomeSeedGranted(nestZdo))
            {
                TryGrantGreydwarfHomeSeed(spawner, player, nestZdo, false);
                return;
            }

            SetStatus(SpawnerRecruitmentZdo.IsConsumed(nestZdo)
                ? $"Grauzwerg-Nest ist als Rufer aktiv. Uralten Samen direkt am Nest benutzen. {GreydwarfNestSummoningRuntime.BuildChanceText(nestZdo)}"
                : $"Nest ist befriedet. Benutze einen Uralten Samen direkt am Nest, um den ersten Worker zu rufen. {GreydwarfNestSummoningRuntime.BuildChanceText(nestZdo)}");
        }

        private void TryGrantGreydwarfHomeSeed(SpawnArea spawner, Player player, ZDO nestZdo, bool alreadyConsumedMessage)
        {
            if (spawner == null || player == null || nestZdo == null) return;

            if (SpawnerRecruitmentZdo.IsHomeSeedGranted(nestZdo))
            {
                if (alreadyConsumedMessage)
                {
                    SetStatus($"Grauzwerg-Nest ist als Rufer aktiv. Uralter Samen direkt am Nest benutzen. {GreydwarfNestSummoningRuntime.BuildChanceText(nestZdo)}");
                }
                return;
            }

            if (_greydwarfHomeSeedReward == null)
            {
                SetStatus("Ancient Seed reward unavailable: reward runtime missing.");
                return;
            }

            if (_greydwarfHomeSeedReward.TryGrant(spawner, player, nestZdo, out string failure))
            {
                // The Ancient Seed is the Greydwarf-family progression key: once the pacified nest
                // releases it, the player learns the matching Greydwarf rest/throne recipe. The
                // first Worker still costs that seed at the nest; unlocking the home is not a free summon.
                RestStationUnlockRuntime.UnlockFromProgression(player, RestStationType.Greydwarf, "Uralter Samen");
                SetStatus($"Nest befriedet: 1x Uralter Samen freigegeben + Grauzwerg-Ruhestaette freigeschaltet. Benutze den Samen direkt am Nest, um einen Worker zu rufen. {GreydwarfNestSummoningRuntime.BuildChanceText(nestZdo)}");
                Logger.LogInfo($"Greydwarf nest home-seed reward granted. Prefab={GreydwarfHomeSeedRewardRuntime.AncientSeedPrefab}, player={player.GetPlayerID()}, nest={spawner.transform.position}.");
            }
            else
            {
                SetStatus($"Nest ist befriedet, aber der Uralte Samen wartet noch: {failure}. E am Nest versucht es erneut.");
                DebugLog($"Greydwarf Ancient Seed reward pending: {failure}.");
            }
        }

        internal string GetGreydwarfNestHoverText(SpawnArea spawner)
        {
            if (!_enabled.Value || spawner == null) return string.Empty;

            ZNetView nview = spawner.GetComponent<ZNetView>();
            if (nview == null) nview = spawner.GetComponentInParent<ZNetView>();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo == null)
                return "Grauzwerg-Nest\nNetzwerkstatus nicht verfuegbar";

            int progress = SpawnerRecruitmentZdo.ReadProgress(zdo);
            int required = Mathf.Max(1, SpawnerRecruitmentZdo.ReadRequired(zdo));
            bool pacified = SpawnerRecruitmentZdo.IsPacified(zdo);
            bool firstReleased = SpawnerRecruitmentZdo.IsConsumed(zdo);
            bool seedGranted = SpawnerRecruitmentZdo.IsHomeSeedGranted(zdo);
            string chances = GreydwarfNestSummoningRuntime.BuildChanceText(zdo);

            if (firstReleased)
            {
                return $"Grauzwerg-Nest — Rufer aktiv\n{chances}\n[Gegenstand benutzen] Uralter Samen = Worker rufen";
            }

            if (pacified)
            {
                if (!seedGranted)
                    return $"Grauzwerg-Nest — befriedet {progress}/{required}\n{chances}\n[<color=yellow><b>E</b></color>] Uralten Samen freigeben";

                return $"Grauzwerg-Nest — befriedet\n{chances}\n[Gegenstand benutzen] Uralter Samen = ersten Worker rufen";
            }

            return $"Grauzwerg-Nest — Bindung {progress}/{required}\n{chances}\n[<color=yellow><b>E</b></color>] Spende anbieten\n[Gegenstand benutzen] gezielt spenden";
        }

        internal string GetGreydwarfNestHoverName() => "Grauzwerg-Nest";

        private void DrawDefaultWorkerStanceButton(WorkerStance stance, string label)
        {
            WorkerStance current = _defaultWorkerStance != null ? _defaultWorkerStance.Value : WorkerCombatStanceRuntime.DefaultStance;
            bool active = current == stance;
            GUI.enabled = !active;
            if (GUILayout.Button((active ? "AKTIV • " : string.Empty) + label))
            {
                _defaultWorkerStance.Value = stance;
                WorkerCombatStanceRuntime.ConfigureDefaultStance(stance);
                SetStatus("Standard-Haltung neuer Worker -> " + WorkerCombatStanceRuntime.DisplayName(stance) + ".");
            }
            GUI.enabled = true;
        }

        private void ApplyDefaultStanceToOwnedWorkers()
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                SetStatus("Standard-Haltung konnte nicht angewendet werden: kein lokaler Spieler.");
                return;
            }

            WorkerStance stance = _defaultWorkerStance != null ? _defaultWorkerStance.Value : WorkerCombatStanceRuntime.DefaultStance;
            WorkerCombatStanceRuntime.ConfigureDefaultStance(stance);
            long ownerId = player.GetPlayerID();
            int updated = 0;
            int skippedAuthority = 0;
            var touched = new System.Collections.Generic.HashSet<ZDOID>();
            var loaded = new System.Collections.Generic.List<Character>();
            WorkerFastLookupRuntime.CopyKnownWorkers(loaded);

            for (int i = 0; i < loaded.Count; i++)
            {
                Character worker = loaded[i];
                if (worker == null || worker.IsDead()) continue;
                ZNetView view = worker.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (!WorkerZdo.IsWorker(zdo) || zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != ownerId) continue;
                touched.Add(zdo.m_uid);
                if (!view.IsOwner())
                {
                    skippedAuthority++;
                    continue;
                }

                WorkerCombatStanceRuntime.SetStance(worker, zdo, stance);
                updated++;
            }

            bool canWritePersistent = false;
            try { canWritePersistent = ZNet.instance != null && ZNet.instance.IsServer(); } catch { }
            if (canWritePersistent)
            {
                BackgroundWorkerScheduler.ForEachRegisteredWorker(zdo =>
                {
                    if (!WorkerZdo.IsWorker(zdo) || zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != ownerId) return;
                    if (touched.Contains(zdo.m_uid)) return;
                    zdo.Set(WorkerZdo.Stance, (int)stance);
                    touched.Add(zdo.m_uid);
                    updated++;
                });
            }

            string suffix = skippedAuthority > 0 ? $" • {skippedAuthority} ohne Netzwerk-Autoritaet uebersprungen" : string.Empty;
            SetStatus($"Standard-Haltung {WorkerCombatStanceRuntime.DisplayName(stance)} auf {updated} eigene Worker angewendet{suffix}.");
        }

        private void TryCreatorSpawnWorker(string prefabName)
        {
            string normalizedSpecies = SpeciesRoleRegistry.NormalizePrefabName(prefabName);
            if (string.Equals(normalizedSpecies, "Wolf", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedSpecies, "Lox", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus(string.Equals(normalizedSpecies, "Wolf", StringComparison.OrdinalIgnoreCase)
                    ? "Wolf bleibt ein normales gezaehmtes Zucht-/Begleittier. Optionale Patrouillen folgen spaeter als Tierbefehl, nicht als Worker."
                    : "Lox bleibt ein normales gezaehmtes Tier/Reittier. Neue Heavy-Hauler-Lox werden nicht mehr erzeugt; alte Worker-Lox bleiben Legacy-kompatibel.");
                return;
            }

            if (_creatorMode == null || !_creatorMode.Value)
            {
                SetStatus("Creator mode is disabled.");
                return;
            }

            Player player = Player.m_localPlayer;
            if (player == null)
            {
                SetStatus("Creator spawn failed: no local player.");
                return;
            }

            if (_creatorSpawn == null)
            {
                SetStatus("Creator spawn runtime unavailable.");
                return;
            }

            if (_creatorSpawn.TrySpawnWorker(
                prefabName,
                player,
                Mathf.Max(5f, _workRadius.Value),
                _defaultWorkerStance != null ? _defaultWorkerStance.Value : WorkerCombatStanceRuntime.DefaultStance,
                out Character worker,
                out string failure))
            {
                _lastWorker = worker;
                WorkerRole role = WorkerRole.None;
                SpeciesRoleRegistry.TryGetRole(prefabName, out role);
                WorkerStance stance = _defaultWorkerStance != null ? _defaultWorkerStance.Value : WorkerCombatStanceRuntime.DefaultStance;
                SetStatus($"Creator mode: {prefabName} spawned as {role} | stance: {WorkerCombatStanceRuntime.DisplayName(stance)}.");
                Logger.LogInfo($"Creator mode spawned {prefabName} as {role} for player {player.GetPlayerID()} at {worker.transform.position}.");
            }
            else
            {
                SetStatus("Creator spawn failed: " + failure);
            }
        }

        private void TryRecruitNearest()
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                SetStatus("No local player.");
                return;
            }

            Character candidate = FindNearestEligible(player.transform.position, _recruitSearchRadius.Value);
            if (candidate == null)
            {
                SetStatus("No eligible worker species in range.");
                return;
            }

            ZNetView nview = candidate.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid() || nview.GetZDO() == null)
            {
                SetStatus("Creature has no valid ZNetView/ZDO.");
                return;
            }

            if (!nview.IsOwner())
            {
                _lastWorker = candidate;
                SetStatus("Recruit blocked: this client is not Network Owner.");
                return;
            }

            ZDO zdo = nview.GetZDO();
            if (WorkerZdo.IsWorker(zdo))
            {
                _lastWorker = candidate;
                SetStatus("Creature is already recruited.");
                return;
            }

            string prefabName = GetPrefabName(candidate.gameObject.name);
            string normalizedCandidateSpecies = SpeciesRoleRegistry.NormalizePrefabName(candidate.gameObject.name);
            if (string.Equals(normalizedCandidateSpecies, "Wolf", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedCandidateSpecies, "Lox", StringComparison.OrdinalIgnoreCase))
            {
                _lastWorker = candidate;
                SetStatus(string.Equals(normalizedCandidateSpecies, "Wolf", StringComparison.OrdinalIgnoreCase)
                    ? "Wolf bleibt ein normales gezaehmtes Zucht-/Begleittier und wird nicht als Worker rekrutiert."
                    : "Lox bleibt ein normales gezaehmtes Tier/Reittier und wird nicht mehr als neuer Heavy-Hauler-Worker rekrutiert.");
                return;
            }
            if (string.Equals(normalizedCandidateSpecies, "Troll", StringComparison.OrdinalIgnoreCase))
            {
                _lastWorker = candidate;
                SetStatus("Troll-Rekrutierung: natuerliche Trollhoehle aufsuchen und dem Troll 10x Hirschragout schenken. Creator-Spawn bleibt nur fuer Tests verfuegbar.");
                return;
            }
            if (string.Equals(normalizedCandidateSpecies, "StoneGolem", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedCandidateSpecies, "IceGolem", StringComparison.OrdinalIgnoreCase))
            {
                _lastWorker = candidate;
                SetStatus($"Steingolem-Rekrutierung: natuerlichen Golem {StoneGolemRecruitmentRuntime.RequiredParries}x in Folge perfekt parieren und danach {StoneGolemRecruitmentRuntime.RequiredCrystals}x Kristall zur Bindung opfern. Creator-Spawn bleibt nur fuer Tests verfuegbar.");
                return;
            }
            if (normalizedCandidateSpecies.StartsWith("Fuling", StringComparison.OrdinalIgnoreCase))
            {
                _lastWorker = candidate;
                SetStatus("Fuling recruitment uses village diplomacy: befriend the settlement first, then win over the individual worker with food gifts.");
                return;
            }
            if (string.Equals(normalizedCandidateSpecies, "Surtling", StringComparison.OrdinalIgnoreCase))
            {
                _lastWorker = candidate;
                SetStatus("Surtling-Rekrutierung: Prächtiges Signalfeuer innerhalb 30 m eines natuerlichen Feuergeysirs unter einem Dach bauen und 3 Valheim-Tage mit normalem Holz am Brennen halten. Geht das Feuer aus oder fehlt der Dachschutz, faellt der Fortschritt zurueck. Nach dem ersten Feuerwart können am vollendeten Signalfeuer per Shift+E jeweils 10 Surtling-Kerne für einen weiteren Feuerwart geopfert werden. Jeder Feuerwart braucht eine separate kleine Surtling-Ruhestätte.");
                return;
            }
            if (string.Equals(normalizedCandidateSpecies, "Neck", StringComparison.OrdinalIgnoreCase))
            {
                _lastWorker = candidate;
                SetStatus("Nixen-Rekrutierung: Nixenfalle am Ufer bauen, 1x Fisch als Köder einlegen, auf einen Fang warten, die gefangene Nixe mit 5x Fisch auf 100% Vertrauen bringen und dann freilassen. Danach braucht sie ein Ufernest.");
                return;
            }
            if (string.Equals(normalizedCandidateSpecies, "Drake", StringComparison.OrdinalIgnoreCase))
            {
                _lastWorker = candidate;
                SetStatus("Drake-Rekrutierung: Drachenbrutnest bauen, 1x Drachenei einlegen und 2 Valheim-Tage inkubieren lassen. Der geschlüpfte Drake folgt dir automatisch zur Base und braucht dort einen Drake-Landeplatz.");
                return;
            }
            if (normalizedCandidateSpecies.StartsWith("Draugr", StringComparison.OrdinalIgnoreCase))
            {
                _lastWorker = candidate;
                SetStatus("Draugr-Rekrutierung: Draugr-Ahnenmal innerhalb 22 m eines natuerlichen Leichenhaufens bauen und fremde Trophäen bis 100 Ehre opfern. Die Opfer bestimmen die Chance auf Krieger, Jaeger oder Elite. Draugr-Trophäen werden nicht akzeptiert.");
                return;
            }
            if (!SpeciesRoleRegistry.TryGetRole(prefabName, out WorkerRole role))
            {
                SetStatus($"Species '{prefabName}' has no worker role mapping.");
                return;
            }

            Vector3 home = candidate.transform.position;
            WorkerZdo.WriteInitialState(zdo, player.GetPlayerID(), role, home, Mathf.Max(5f, _workRadius.Value));
            WorkerFastLookupRuntime.RegisterKnownWorker(candidate, zdo);
            string recruitedSpecies = SpeciesRoleRegistry.NormalizePrefabName(candidate.gameObject.name);
            if (recruitedSpecies.StartsWith("Fuling", StringComparison.OrdinalIgnoreCase))
                WorkerZdo.AssignHome(zdo, home, WorkerHomeProfileCatalog.FulingCampKind);

            // MVP behavior change only: use Valheim's real tame state so a recruited worker
            // no longer behaves as a normal hostile creature toward its owner.
            candidate.SetTamed(true);

            _lastWorker = candidate;
            SetStatus($"{prefabName} recruited | Role: {role} | State: Idle");
            Logger.LogInfo($"{prefabName} recruited as {role}. Home={home}, Radius={_workRadius.Value:0.#}m, PlayerID={player.GetPlayerID()}");
        }

        private Character FindNearestEligible(Vector3 center, float radius)
        {
            Collider[] hits = Physics.OverlapSphere(center, Mathf.Max(1f, radius));
            Character best = null;
            float bestSqr = float.MaxValue;

            for (int i = 0; i < hits.Length; i++)
            {
                Character character = hits[i].GetComponentInParent<Character>();
                if (character == null || character is Player || character.IsDead()) continue;

                string prefabName = GetPrefabName(character.gameObject.name);
                if (string.Equals(prefabName, "Wolf", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prefabName, "Lox", StringComparison.OrdinalIgnoreCase)) continue;
                if (!SpeciesRoleRegistry.TryGetRole(prefabName, out _)) continue;

                float sqr = (character.transform.position - center).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = character;
            }

            return best;
        }

        private static string GetPrefabName(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return string.Empty;
            return SpeciesRoleRegistry.NormalizePrefabName(objectName);
        }

        internal GardenPlanningRuntime GardenPlanning => _gardenPlanning;

        private void PerformDevReload()
        {
            if (Time.unscaledTime < _nextDevReloadAllowed) return;
            _nextDevReloadAllowed = Time.unscaledTime + 1.0f;

            try
            {
                // Close transient UI/edit modes first so no stale Unity references survive the refresh.
                CloseWorkFlagMenu();
                CloseProductionStationMenu();
                CloseWorkerContainerMenu();
                CloseWorkerMenu();
                _workZoneBirdView?.Shutdown();
                _gardenPlanning?.Shutdown();
                _reachabilityHeatmap?.Shutdown();
                RestoreMenuCursor();

                // Reload the BepInEx config file into the existing ConfigEntry objects.
                Config.Reload();
                WorkerCombatStanceRuntime.ConfigureDefaultStance(_defaultWorkerStance.Value);
                WolfPatrolRuntime.ConfigureRadius(_wolfPatrolRadius.Value);
                if (_navigatorStuckSeconds.Value < 4.5f) _navigatorStuckSeconds.Value = 5.5f;

                // Reset transient runtime state only. Persistent ZDO state (worker assignment, cargo,
                // homes, workzones, field-depot rules, planting plans) remains authoritative.
                _mapMarkers?.Dispose();
                _ownership?.Shutdown();
                BoatPassengerRuntime.Shutdown();
                WorkerNavigationRuntime.Shutdown();
                WorkerRestAnimationRuntime.Shutdown();
                WorkerRestRuntime.Shutdown();
                WorkerRestPhysicsLockRuntime.Clear();
                SmartReplantBridge.Shutdown();

                WorkerWorldIndex.RebuildLoadedState();
                WorkerWearNTearIndexRuntime.RebuildLoadedState();
                WorkerSpawnerIndexRuntime.RebuildLoadedState();
                WorkFlagRegistry.RebuildLoadedState();
                WorkerDoorRuntime.Clear();
                FirekeeperZoneFireRuntime.ClearIndex();
                WorkerCharacterIndexRuntime.Clear();
                LumberjackTreeFilterRuntime.ClearCaches();
                StonemasonRockFilterRuntime.ClearCaches();
                HunterPreyFilterRuntime.ClearCaches();
                FulingVillageDiplomacyRuntime.ClearCaches();
                ContainerLedgerRuntime.ClearCaches();
                BackgroundGroundPickupRuntime.ClearCaches();
                BackgroundHunterRuntime.ClearCaches();
                BackgroundFisherRuntime.ClearCaches();
                LivestockButcherRuntime.ClearCaches();
                LivestockFeedingRuntime.ClearCaches();
                LivestockPenRuntime.ClearCaches();
                WolfPatrolRuntime.ClearCaches();
                BaseRepairWorkRuntime.ResetAll();
                ProductionOrderClaimRuntime.Clear();
                WorkersPerformanceProfiler.Reset();

                WorkerNavigationRuntime.Initialize(DebugLog);
                BoatPassengerRuntime.Initialize(DebugLog);
                HunterWorkRuntime.ConfigureShootRange(_rangedShootRange.Value);
                WorkerStorageRuntime.ConfigureContainerAccessRadius(_containerAccessRadius.Value);
                WorkerStorageRuntime.ConfigureLogisticsNetworkRadius(_logisticsNetworkRadius.Value);
                WorkerStorageRuntime.ConfigureHaulerMinimumLoadPercent(_haulerMinimumLoadPercent.Value);
                WorkerNavigationRuntime.ConfigureRecovery(_navigatorStuckSeconds.Value, _navigatorRecoveryDistance.Value);
                WorkerRestRuntime.Initialize(DebugLog);
                WorkerRestAnimationRuntime.Initialize(DebugLog);
                SmartReplantBridge.Initialize(DebugLog);
                SmartReplantPlanningCompatibility.TryInstall(_harmony, DebugLog);

                _workerAi = new WorkerAiController(DebugLog);
                _ownership = new WorkerOwnershipRuntime(DebugLog);
                _mapMarkers = new MapMarkerManager(DebugLog);
                _spawnerProbe = new GreydwarfSpawnerProbeRuntime(DebugLog);
                _lastWorker = null;
                _workFlagAssignedWorkers.Clear();
                _workFlagAssignedCount = 0;
                _nextWorkFlagAssignedCountRefresh = 0f;
                _containerReserveEdits.Clear();
                _containerTriggerEdits.Clear();
                _containerSeedRuleScratch.Clear();
                _containerConfiguredRuleScratch.Clear();
                _nextSmartCoreAttempt = 0f;
                _smartCoreAttemptDeadline = Time.unscaledTime + 5f;

                string message = $"Workers Dev Reload OK — flags={WorkFlagRegistry.LoadedCount}, containers={WorkerWorldIndex.ContainerCount}, drops={WorkerWorldIndex.ItemDropCount}, pickables={WorkerWorldIndex.PickableCount}, structures={WorkerWearNTearIndexRuntime.LoadedCount}, spawnAreas={WorkerSpawnerIndexRuntime.SpawnAreaCount}, creatureSpawners={WorkerSpawnerIndexRuntime.CreatureSpawnerCount}";
                SetStatus(message);
                Logger.LogInfo(message + ". Config/runtime refreshed; DLL code was not hot-reloaded.");
            }
            catch (Exception ex)
            {
                Logger.LogError("Workers Dev Reload failed safely: " + ex);
                SetStatus("Workers Dev Reload FEHLER — Details im BepInEx-Log.");
            }
        }

        internal void SetExternalStatus(string message) => SetStatus(message);

        private void SetStatus(string message)
        {
            _status = message;
            _nextStatusExpiry = Time.unscaledTime + 8f;
            DebugLog(message);
        }

        private void DebugLog(string message)
        {
            if (_debugMode != null && _debugMode.Value)
                Logger.LogDebug(message);
        }

        private void DrawWorkerInventoryWindow(int windowId)
        {
            EnsureSmartPopupStyles();

            Character worker = _openWorkerInventory;
            if (worker == null || worker.IsDead())
            {
                GUILayout.Label("Arbeiter nicht mehr verfuegbar.", _smartPopupLabelStyle);
                if (GUILayout.Button("Schliessen", _smartPopupButtonStyle)) CloseWorkerInventory();
                return;
            }

            ZNetView nview = worker.GetComponent<ZNetView>();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (!WorkerZdo.IsWorker(zdo))
            {
                GUILayout.Label("Kein gueltiges Worker-Inventar.", _smartPopupLabelStyle);
                if (GUILayout.Button("Schliessen", _smartPopupButtonStyle)) CloseWorkerInventory();
                return;
            }

            Player player = Player.m_localPlayer;
            if (player == null)
            {
                GUILayout.Label("Spieler nicht verfuegbar.", _smartPopupLabelStyle);
                if (GUILayout.Button("Schliessen", _smartPopupButtonStyle)) CloseWorkerInventory();
                return;
            }

            WorkerRole role = WorkerJobProfileRuntime.NormalizeRuntimeRole(zdo);
            string species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);

            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            GUILayout.Label("ARBEITER-INVENTAR", _smartPopupHeadingStyle, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("X", _smartPopupButtonStyle, GUILayout.Width(36f), GUILayout.Height(28f)))
            {
                CloseWorkerInventory();
                GUIUtility.ExitGUI();
            }
            GUILayout.EndHorizontal();

            GUILayout.Label($"{species}  •  {WorkerTierCatalog.DisplayTier(worker)}", _smartPopupHeadingStyle);
            GUILayout.Label(WorkerCargoRuntime.GetSummary(worker, zdo), _smartPopupLabelStyle);

            if (WorkerZdo.TryReadCargoRoute(zdo, out string route, out Vector3 routeTarget))
                GUILayout.Label($"Route: {route}  →  {routeTarget.x:0}, {routeTarget.y:0}, {routeTarget.z:0}", _smartPopupLabelStyle);
            else
                GUILayout.Label("Route: keine", _smartPopupLabelStyle);

            GUILayout.Space(8f);
            GUILayout.Label("Worker-Inventar", _smartPopupHeadingStyle);
            _workerInventoryScroll = GUILayout.BeginScrollView(_workerInventoryScroll, GUILayout.ExpandHeight(true));
            System.Collections.Generic.List<WorkerZdo.CargoStack> cargo = WorkerCargoRuntime.ReadInventorySlots(zdo);
            for (int i = 0; i < WorkerCargoRuntime.InventorySlots; i++)
            {
                GUILayout.BeginHorizontal();
                if (i >= cargo.Count)
                {
                    GUILayout.Label($"Slot {i + 1}: Leer", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                }
                else
                {
                    WorkerZdo.CargoStack stack = cargo[i];
                    GUILayout.Label($"Slot {i + 1}: {stack.Prefab}  x{stack.Amount}", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("1 nehmen", _smartPopupButtonStyle, GUILayout.Width(88f)))
                    {
                        if (!WorkerCargoRuntime.TryTransferCargoToPlayer(worker, zdo, player, stack.Prefab, 1))
                            SetStatus("Item konnte nicht ins Spielerinventar verschoben werden.");
                        GUIUtility.ExitGUI();
                    }
                    if (GUILayout.Button("Stack", _smartPopupButtonStyle, GUILayout.Width(66f)))
                    {
                        if (!WorkerCargoRuntime.TryTransferCargoToPlayer(worker, zdo, player, stack.Prefab, stack.Amount))
                            SetStatus("Nicht der ganze Stack konnte ins Spielerinventar verschoben werden.");
                        GUIUtility.ExitGUI();
                    }
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            GUI.enabled = WorkerZdo.HasCargo(zdo);
            if (GUILayout.Button("Alles nehmen", _smartPopupButtonStyle, GUILayout.ExpandWidth(true)))
            {
                int moved = WorkerCargoRuntime.TransferAllCargoToPlayer(worker, zdo, player);
                if (moved > 0)
                    SetStatus($"{moved} Item(s) aus dem Arbeiter-Inventar genommen.");
                else
                    SetStatus("Spielerinventar voll oder keine Ladung verschiebbar.");
                GUI.enabled = true;
                GUIUtility.ExitGUI();
            }
            if (GUILayout.Button("Alles fallen lassen", _smartPopupButtonStyle, GUILayout.ExpandWidth(true)))
            {
                if (WorkerCargoRuntime.DropAllCargoFromInventory(worker, zdo))
                    SetStatus("Arbeiter hat seine komplette Ladung fallen gelassen.");
                else
                    SetStatus("Keine Ladung zum Fallenlassen vorhanden.");
                GUI.enabled = true;
                GUIUtility.ExitGUI();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            GUILayout.Label("Vom Spieler einlagern", _smartPopupHeadingStyle);
            Inventory playerInventory = player.GetInventory();
            System.Collections.Generic.List<ItemDrop.ItemData> playerItems = playerInventory != null ? playerInventory.GetAllItems() : null;
            bool showedPlayerItem = false;
            if (playerItems != null)
            {
                var shownPrefabs = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < playerItems.Count; i++)
                {
                    ItemDrop.ItemData item = playerItems[i];
                    if (item == null || item.m_stack <= 0) continue;
                    string prefab = WorkerStorageRuntime.GetPrefabName(item);
                    if (string.IsNullOrEmpty(prefab) || !shownPrefabs.Add(prefab)) continue;
                    showedPlayerItem = true;
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"{prefab}  x{playerInventory.CountItems(item.m_shared.m_name)}", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("1 geben", _smartPopupButtonStyle, GUILayout.Width(88f)))
                    {
                        if (!WorkerCargoRuntime.TryTransferPlayerToCargo(worker, zdo, player, prefab, 1))
                            SetStatus("Worker-Inventar voll oder Item konnte nicht verschoben werden.");
                        GUIUtility.ExitGUI();
                    }
                    GUILayout.EndHorizontal();
                }
            }
            if (!showedPlayerItem) GUILayout.Label("Spielerinventar leer", _smartPopupLabelStyle);
            GUILayout.EndScrollView();

            GUILayout.Space(6f);
            GUILayout.Label("Items koennen jetzt direkt zwischen Spieler und persistentem Worker-Inventar verschoben werden.", _smartPopupLabelStyle);
            if (GUILayout.Button("Schliessen", _smartPopupButtonStyle))
            {
                CloseWorkerInventory();
                GUIUtility.ExitGUI();
            }
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 34f));
        }

        private void DrawWorkerMenuWindow(int windowId)
        {
            EnsureSmartPopupStyles();

            if (_openWorkerMenu == null && _openPersistentWorkerMenuZdo != null)
            {
                DrawPersistentWorkerMenuContents();
                GUI.DragWindow(new Rect(0f, 0f, 10000f, 34f));
                return;
            }

            Character worker = _openWorkerMenu;
            if (worker == null || worker.IsDead())
            {
                GUILayout.Label("Arbeiter nicht mehr verfuegbar.", _smartPopupLabelStyle);
                if (GUILayout.Button("Schliessen", _smartPopupButtonStyle)) CloseWorkerMenu();
                return;
            }

            ZNetView nview = worker.GetComponent<ZNetView>();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (!WorkerZdo.IsWorker(zdo))
            {
                GUILayout.Label("Kein gueltiger Worker-Netzwerkzustand.", _smartPopupLabelStyle);
                if (GUILayout.Button("Schliessen", _smartPopupButtonStyle)) CloseWorkerMenu();
                return;
            }

            WorkerRole role = WorkerJobProfileRuntime.NormalizeRuntimeRole(zdo);
            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            string species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            bool writable = nview.IsOwner() && Player.m_localPlayer != null && ownerId == Player.m_localPlayer.GetPlayerID();

            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            GUILayout.Label("WORKER", _smartPopupHeadingStyle, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("X", _smartPopupButtonStyle, GUILayout.Width(36f), GUILayout.Height(28f)))
            {
                CloseWorkerMenu();
                GUIUtility.ExitGUI();
            }
            GUILayout.EndHorizontal();

            GUILayout.Label($"{species}  •  {WorkerTierCatalog.DisplayTier(worker)}", _smartPopupHeadingStyle);
            _workerMenuScroll = GUILayout.BeginScrollView(_workerMenuScroll, GUILayout.ExpandHeight(true));

            GUILayout.Space(4f);
            GUILayout.Label("Status", _smartPopupHeadingStyle);
            GUILayout.Label($"Zustand: {state}", _smartPopupLabelStyle);
            GUILayout.Label($"Leben: {worker.GetHealth():0}/{worker.GetMaxHealth():0}", _smartPopupLabelStyle);
            if (WorkerZdo.TryReadCargo(zdo, out string cargoPrefab, out int cargoAmount))
                GUILayout.Label($"Ladung: {cargoPrefab} x{cargoAmount}", _smartPopupLabelStyle);
            else
                GUILayout.Label("Ladung: leer", _smartPopupLabelStyle);

            GUILayout.Space(8f);
            if (GUILayout.Button("Arbeiter anzeigen", _smartPopupButtonStyle))
            {
                CloseWorkerMenu();
                _workerLocateCamera?.Begin(worker, zdo);
                GUIUtility.ExitGUI();
            }

            DrawWorkerSustenanceSection(zdo, Player.m_localPlayer, writable);

            GUILayout.Space(10f);
            GUILayout.Label("Schnellbefehl", _smartPopupHeadingStyle);
            string followText = state == WorkerState.Follow ? "Folgen beenden / nach Hause" : "Mir folgen";
            GUI.enabled = writable;
            if (GUILayout.Button(followText, _smartPopupButtonStyle))
            {
                TryInteractWithWorker(worker, Player.m_localPlayer);
                state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            }
            GUI.enabled = true;

            GUILayout.Space(10f);
            GUILayout.Label("Haltung", _smartPopupHeadingStyle);
            WorkerStance stance = WorkerCombatStanceRuntime.ReadStance(zdo);
            GUILayout.BeginHorizontal();
            DrawWorkerStanceButton(worker, zdo, WorkerStance.Aggressive, "Aggressiv", stance, writable);
            DrawWorkerStanceButton(worker, zdo, WorkerStance.Defensive, "Defensiv", stance, writable);
            DrawWorkerStanceButton(worker, zdo, WorkerStance.Passive, "Nicht angreifen", stance, writable);
            GUILayout.EndHorizontal();

            WorkOrder menuOrder = (WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None);
            if (role == WorkerRole.Guard || (role == WorkerRole.Hunter && WorkerJobProfileRuntime.HasTask(zdo, role, WorkerTaskFlags.Guard)))
            {
                GUILayout.Space(10f);
                GUILayout.Label("Guard-Verhalten", _smartPopupHeadingStyle);
                GuardBehaviorMode guardMode = GuardBehaviorRuntime.Read(zdo);
                GUILayout.Label(guardMode == GuardBehaviorMode.Stationary
                    ? "Stationaer: bleibt am GuardPoint/Hochsitz und schiesst von dort."
                    : "Ausruecken: patrouilliert durch die Workzone, verfolgt Gegner und setzt danach die Patrouille fort.", _smartPopupLabelStyle);
                GUILayout.BeginHorizontal();
                DrawGuardBehaviorButton(worker, zdo, GuardBehaviorMode.Stationary, "Stationaer", guardMode, writable);
                DrawGuardBehaviorButton(worker, zdo, GuardBehaviorMode.Deploy, "Ausruecken", guardMode, writable);
                GUILayout.EndHorizontal();
            }

            if (role == WorkerRole.Firekeeper)
            {
                GUILayout.Space(10f);
                GUILayout.Label("Feuerwaechter", _smartPopupHeadingStyle);
                if (FirekeeperAssignedFireRuntime.TryResolveAssignedFire(worker, zdo, out Vector3 assignedFirePos, out string assignedFireName))
                {
                    GUILayout.Label($"Zugewiesen: {assignedFireName} • Entfernung {Vector3.Distance(worker.transform.position, assignedFirePos):0.0} m", _smartPopupLabelStyle);
                    GUI.enabled = writable;
                    if (GUILayout.Button("Feuer-Zuweisung loesen", _smartPopupButtonStyle))
                    {
                        FirekeeperAssignedFireRuntime.ReleaseWorkerFire(zdo);
                        SetStatus("Surtling: Feuer-Zuweisung geloest.");
                    }
                    GUI.enabled = true;
                }
                else
                {
                    GUILayout.Label("Kein Feuer zugewiesen. Starte die Auswahl und benutze danach mit E das gewuenschte Lagerfeuer/Feuer.", _smartPopupLabelStyle);
                }

                GUI.enabled = writable;
                if (GUILayout.Button("Feuer zuweisen", _smartPopupButtonStyle))
                {
                    FirekeeperAssignedFireRuntime.BeginFireSelection(worker, zdo, Player.m_localPlayer);
                    CloseWorkerMenu();
                    GUIUtility.ExitGUI();
                }
                GUI.enabled = true;
            }

            if (role == WorkerRole.Butcher)
            {
                GUILayout.Space(10f);
                GUILayout.Label("Tierhaltung", _smartPopupHeadingStyle);
                GUILayout.Label("Min = harte Sicherheitsgrenze. Max = Zielobergrenze. Jungtiere werden nie geschlachtet; bei Überschuss zuerst die niedrigste Sternstufe.", _smartPopupLabelStyle);
                DrawButcherPopulationRules(worker, zdo, writable);
            }

            DrawWorkerTaskProfile(zdo, role, writable, species);

            GUILayout.Space(10f);
            GUILayout.Label("Arbeitszuweisung", _smartPopupHeadingStyle);
            string currentFlagId = WorkerZdo.ReadWorkAreaId(zdo);
            string currentZone = WorkFlagRegistry.TryGetDisplayName(currentFlagId, WorkerZdo.ReadWorkAreaName(zdo));
            if (WorkerZdo.TryReadWorkArea(zdo, out WorkOrder currentOrder, out Vector3 currentCenter, out float currentRadius))
            {
                string radiusText = role == WorkerRole.Hauler && (WorkerJobProfileRuntime.ReadTasks(zdo, role) & WorkerTaskFlags.Transport) != 0
                    ? "Transport MAP-WIDE / lokale Module im Zonenradius"
                    : currentRadius.ToString("0") + " m";
                GUILayout.Label($"Aktuell: Workzone • {(string.IsNullOrWhiteSpace(currentZone) ? "ohne Namen" : currentZone)} • {radiusText}", _smartPopupLabelStyle);
                GUILayout.Label(WorkerWorkAreaRules.DescribeHomeStatus(zdo, currentCenter, currentRadius), _smartPopupLabelStyle);
            }
            else
                GUILayout.Label("Aktuell: keine Arbeitszone", _smartPopupLabelStyle);

            WorkFlagRegistry.CollectCompatibleAssignments(ownerId, role, zdo, worker.transform.position, _workFlagDefaultRadius.Value, _workerWorkOptions);
            if (_workerWorkOptions.Count == 0)
            {
                if (!WorkerWorkAreaRules.HasRealHome(zdo))
                    GUILayout.Label("Keine Workzone waehlbar: Weise diesem Arbeiter zuerst einen echten Schlafplatz/HomeAnchor zu.", _smartPopupLabelStyle);
                else
                    GUILayout.Label("Keine passende Workzone gefunden, die den Schlafplatz dieses Arbeiters innerhalb ihres Radius enthaelt.", _smartPopupLabelStyle);
            }
            else
            {
                GUILayout.Label("Verfuegbare Workzones:", _smartPopupLabelStyle);
                for (int i = 0; i < _workerWorkOptions.Count; i++)
                {
                    WorkAssignmentOption option = _workerWorkOptions[i];
                    bool active = string.Equals(currentFlagId, option.Id, StringComparison.Ordinal);
                    string zoneName = string.IsNullOrWhiteSpace(option.DisplayName) ? "Workzone" : option.DisplayName;
                    string text = $"{(active ? "AKTIV • " : string.Empty)}{zoneName} ({option.Distance:0} m)";
                    GUI.enabled = writable && !active;
                    if (GUILayout.Button(text, _smartPopupButtonStyle))
                    {
                        AssignWorkerFromMenu(worker, nview, zdo, option);
                        currentFlagId = WorkerZdo.ReadWorkAreaId(zdo);
                    }
                    GUI.enabled = true;
                }
            }

            GUI.enabled = writable && !string.IsNullOrEmpty(currentFlagId);
            if (GUILayout.Button("Arbeitszuweisung loesen", _smartPopupButtonStyle))
                ReleaseWorkerWorkAssignmentFromMenu(worker, zdo);
            GUI.enabled = true;

            GUILayout.Space(10f);
            GUILayout.Label("Zuhause / Schlafplatz", _smartPopupHeadingStyle);
            string homeKind = zdo.GetString(WorkerZdo.HomeAnchorType, string.Empty);
            if (string.IsNullOrEmpty(homeKind))
            {
                GUILayout.Label("Kein Schlafplatz zugewiesen.", _smartPopupLabelStyle);
            }
            else
            {
                Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
                GUILayout.Label($"Zugewiesen: {homeKind} • Entfernung {Vector3.Distance(worker.transform.position, home):0.0} m", _smartPopupLabelStyle);
                GUI.enabled = writable;
                if (GUILayout.Button("Schlafplatz-Zuweisung loesen", _smartPopupButtonStyle))
                {
                    WorkerHomeAssignmentRuntime.ReleaseForWorker(zdo);
                    SetStatus($"{species}: Schlafplatz freigegeben.");
                }
                GUI.enabled = true;
            }

            if (!writable)
            {
                GUILayout.Space(8f);
                GUILayout.Label("Nur Lesen: Der Worker ist aktuell nicht lokal network-owned.", _smartPopupLabelStyle);
            }

            GUILayout.Space(10f);
            GUILayout.Label("Bedienung", _smartPopupHeadingStyle);
            GUILayout.Label("Worker: E = Folgen / nach Hause; am Boot kontextuell Auf-/Absetzen. Shift+E = Arbeiter-Inventar. Dieses Worker-Menue oeffnest du mit Shift+E an der zugewiesenen Ruhestaette. ESC schliesst das Fenster.", _smartPopupLabelStyle);

            GUILayout.EndScrollView();
            GUILayout.Space(6f);
            if (GUILayout.Button("Schliessen", _smartPopupButtonStyle))
            {
                CloseWorkerMenu();
                GUIUtility.ExitGUI();
            }
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 34f));
        }

        private void DrawPersistentWorkerMenuContents()
        {
            ZDO zdo = _openPersistentWorkerMenuZdo;
            Player player = Player.m_localPlayer;
            if (!WorkerZdo.IsWorker(zdo) || player == null)
            {
                GUILayout.Label("Persistenter Worker-Zustand nicht mehr verfuegbar.", _smartPopupLabelStyle);
                if (GUILayout.Button("Schliessen", _smartPopupButtonStyle)) CloseWorkerMenu();
                return;
            }

            long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            bool writable = ownerId == 0L || ownerId == player.GetPlayerID();
            WorkerRole role = WorkerJobProfileRuntime.NormalizeRuntimeRole(zdo);
            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            string species = zdo.GetString(WorkerZdo.SpeciesKey, string.Empty);
            if (string.IsNullOrEmpty(species) && _openPersistentWorkerMenuHomeZdo != null)
                species = WorkerHomeAnchorZdo.ReadWorkerSpecies(_openPersistentWorkerMenuHomeZdo);
            if (string.IsNullOrEmpty(species)) species = "Worker";

            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            GUILayout.Label("WORKER", _smartPopupHeadingStyle, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("X", _smartPopupButtonStyle, GUILayout.Width(36f), GUILayout.Height(28f)))
            {
                CloseWorkerMenu();
                GUIUtility.ExitGUI();
            }
            GUILayout.EndHorizontal();

            GUILayout.Label($"{species}  •  {WorkerTierCatalog.DisplayTier(WorkerTierCatalog.GetWorkerTier(species))}", _smartPopupHeadingStyle);
            GUILayout.Label($"Verwaltung ueber {_openPersistentWorkerMenuHomeLabel}; Worker darf dabei entladen sein.", _smartPopupLabelStyle);
            _workerMenuScroll = GUILayout.BeginScrollView(_workerMenuScroll, GUILayout.ExpandHeight(true));

            GUILayout.Space(4f);
            GUILayout.Label("Status", _smartPopupHeadingStyle);
            GUILayout.Label($"Zustand: {state}", _smartPopupLabelStyle);
            System.Collections.Generic.List<WorkerZdo.CargoStack> cargo = WorkerZdo.ReadCargoStacks(zdo);
            if (cargo.Count == 0) GUILayout.Label("Ladung: leer", _smartPopupLabelStyle);
            else
            {
                for (int i = 0; i < cargo.Count; i++)
                    GUILayout.Label($"Ladung: {cargo[i].Prefab} x{cargo[i].Amount}", _smartPopupLabelStyle);
            }

            GUILayout.Label("Folgen/sofortige Bewegungsbefehle sind nur moeglich, wenn der Worker physisch geladen ist.", _smartPopupLabelStyle);

            Character locateWorker = _openPersistentWorkerMenuHomeZdo != null
                ? WorkerHomeAssignmentRuntime.ResolveLoadedWorkerForHome(_openPersistentWorkerMenuHomeZdo, _openPersistentWorkerMenuHomePosition)
                : null;
            GUI.enabled = locateWorker != null;
            if (GUILayout.Button(locateWorker != null ? "Arbeiter anzeigen" : "Arbeiter anzeigen (zu weit entfernt / entladen)", _smartPopupButtonStyle))
            {
                CloseWorkerMenu();
                _workerLocateCamera?.Begin(locateWorker, zdo);
                GUIUtility.ExitGUI();
            }
            GUI.enabled = true;

            DrawWorkerSustenanceSection(zdo, player, writable);

            GUILayout.Space(10f);
            GUILayout.Label("Haltung", _smartPopupHeadingStyle);
            WorkerStance stance = WorkerCombatStanceRuntime.ReadStance(zdo);
            GUILayout.BeginHorizontal();
            DrawPersistentWorkerStanceButton(zdo, WorkerStance.Aggressive, "Aggressiv", stance, writable);
            DrawPersistentWorkerStanceButton(zdo, WorkerStance.Defensive, "Defensiv", stance, writable);
            DrawPersistentWorkerStanceButton(zdo, WorkerStance.Passive, "Nicht angreifen", stance, writable);
            GUILayout.EndHorizontal();

            WorkOrder menuOrder = (WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None);
            if (role == WorkerRole.Guard || (role == WorkerRole.Hunter && WorkerJobProfileRuntime.HasTask(zdo, role, WorkerTaskFlags.Guard)))
            {
                GUILayout.Space(10f);
                GUILayout.Label("Guard-Verhalten", _smartPopupHeadingStyle);
                GuardBehaviorMode guardMode = GuardBehaviorRuntime.Read(zdo);
                GUILayout.Label(guardMode == GuardBehaviorMode.Stationary
                    ? "Stationaer: bleibt am GuardPoint/Hochsitz und schiesst von dort."
                    : "Ausruecken: patrouilliert durch die Workzone, verfolgt Gegner und setzt danach die Patrouille fort.", _smartPopupLabelStyle);
                GUILayout.BeginHorizontal();
                DrawPersistentGuardBehaviorButton(zdo, GuardBehaviorMode.Stationary, "Stationaer", guardMode, writable);
                DrawPersistentGuardBehaviorButton(zdo, GuardBehaviorMode.Deploy, "Ausruecken", guardMode, writable);
                GUILayout.EndHorizontal();
            }

            if (role == WorkerRole.Butcher)
            {
                GUILayout.Space(10f);
                GUILayout.Label("Tierhaltung", _smartPopupHeadingStyle);
                GUILayout.Label("Min = harte Sicherheitsgrenze. Max = Zielobergrenze. Jungtiere werden nie geschlachtet; bei Überschuss zuerst die niedrigste Sternstufe.", _smartPopupLabelStyle);
                DrawButcherPopulationRules(null, zdo, writable);
            }

            DrawWorkerTaskProfile(zdo, role, writable, species);

            GUILayout.Space(10f);
            GUILayout.Label("Arbeitszuweisung", _smartPopupHeadingStyle);
            string currentFlagId = WorkerZdo.ReadWorkAreaId(zdo);
            string currentZone = WorkFlagRegistry.TryGetDisplayName(currentFlagId, WorkerZdo.ReadWorkAreaName(zdo));
            if (WorkerZdo.TryReadWorkArea(zdo, out WorkOrder currentOrder, out Vector3 currentCenter, out float currentRadius))
            {
                string radiusText = role == WorkerRole.Hauler && (WorkerJobProfileRuntime.ReadTasks(zdo, role) & WorkerTaskFlags.Transport) != 0
                    ? "Transport MAP-WIDE / lokale Module im Zonenradius"
                    : currentRadius.ToString("0") + " m";
                GUILayout.Label($"Aktuell: Workzone • {(string.IsNullOrWhiteSpace(currentZone) ? "ohne Namen" : currentZone)} • {radiusText}", _smartPopupLabelStyle);
            }
            else GUILayout.Label("Aktuell: keine Arbeitszone", _smartPopupLabelStyle);

            WorkFlagRegistry.CollectCompatibleAssignments(ownerId, role, zdo, _openPersistentWorkerMenuHomePosition, _workFlagDefaultRadius.Value, _workerWorkOptions);
            if (_workerWorkOptions.Count == 0)
            {
                GUILayout.Label("Keine aktuell geladene kompatible Workzone an dieser Ruhestaette gefunden.", _smartPopupLabelStyle);
            }
            else
            {
                GUILayout.Label("Verfuegbare Workzones:", _smartPopupLabelStyle);
                for (int i = 0; i < _workerWorkOptions.Count; i++)
                {
                    WorkAssignmentOption option = _workerWorkOptions[i];
                    bool active = string.Equals(currentFlagId, option.Id, StringComparison.Ordinal);
                    string zoneName = string.IsNullOrWhiteSpace(option.DisplayName) ? "Workzone" : option.DisplayName;
                    string buttonText = $"{(active ? "AKTIV • " : string.Empty)}{zoneName} ({option.Distance:0} m)";
                    GUI.enabled = writable && !active && !WorkerZdo.HasCargo(zdo);
                    if (GUILayout.Button(buttonText, _smartPopupButtonStyle))
                    {
                        WorkerZdo.AssignWorkArea(zdo, option.Id, option.DisplayName, option.Order, option.Center, option.Radius);
                        WorkerZdo.ClearTarget(zdo);
                        WorkerState s = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
                        if (s != WorkerState.ReturnHomeForRest && s != WorkerState.Resting && s != WorkerState.Recover)
                            zdo.Set(WorkerZdo.State, role == WorkerRole.Stonemason ? (int)WorkerState.FindJob : (int)WorkerState.MoveToWorkArea);
                        currentFlagId = option.Id;
                        SetStatus($"{species} -> Workzone / {zoneName}. Jobs und Prioritaeten bleiben am Worker gespeichert.");
                    }
                    GUI.enabled = true;
                }
            }

            GUI.enabled = writable && !string.IsNullOrEmpty(currentFlagId) && !WorkerZdo.HasCargo(zdo);
            if (GUILayout.Button("Arbeitszuweisung loesen", _smartPopupButtonStyle))
            {
                WorkerZdo.ClearWorkArea(zdo);
                WorkerZdo.ClearTarget(zdo);
                WorkerState s = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
                if (s != WorkerState.Follow && s != WorkerState.ReturnHomeForRest && s != WorkerState.Resting && s != WorkerState.Recover)
                    zdo.Set(WorkerZdo.State, (int)WorkerState.ReturnHome);
                SetStatus($"{species}: Arbeitszuweisung geloest.");
            }
            GUI.enabled = true;

            GUILayout.Space(10f);
            GUILayout.Label("Zuhause / Schlafplatz", _smartPopupHeadingStyle);
            string homeKind = zdo.GetString(WorkerZdo.HomeAnchorType, string.Empty);
            GUILayout.Label(string.IsNullOrEmpty(homeKind) ? "Kein Schlafplatz zugewiesen." : $"Zugewiesen: {homeKind}", _smartPopupLabelStyle);
            GUI.enabled = writable && _openPersistentWorkerMenuHomeZdo != null;
            if (GUILayout.Button("Schlafplatz-Zuweisung loesen", _smartPopupButtonStyle))
            {
                WorkerHomeAssignmentRuntime.ReleaseFromHome(_openPersistentWorkerMenuHomeZdo, _openPersistentWorkerMenuHomePosition, out _);
                SetStatus($"{species}: Schlafplatz freigegeben.");
                CloseWorkerMenu();
                GUIUtility.ExitGUI();
            }
            GUI.enabled = true;

            if (!writable)
            {
                GUILayout.Space(8f);
                GUILayout.Label("Nur Lesen: Der Worker gehoert einem anderen Spieler.", _smartPopupLabelStyle);
            }

            GUILayout.Space(10f);
            GUILayout.Label("Bedienung", _smartPopupHeadingStyle);
            GUILayout.Label("Shift+E an der Ruhestaette oeffnet diese Verwaltung auch dann, wenn der Worker gerade arbeitet oder entladen ist. Shift+E direkt am Worker oeffnet sein Frachtinventar.", _smartPopupLabelStyle);
            GUILayout.EndScrollView();

            GUILayout.Space(6f);
            if (GUILayout.Button("Schliessen", _smartPopupButtonStyle))
            {
                CloseWorkerMenu();
                GUIUtility.ExitGUI();
            }
            GUILayout.EndVertical();
        }

        private void DrawWorkerSustenanceSection(ZDO zdo, Player player, bool writable)
        {
            if (!WorkerZdo.IsWorker(zdo) || player == null) return;
            if (!WorkerSustenanceRuntime.TryGetRequirement(zdo, out _, out _, out string needWord)) return;

            GUILayout.Space(10f);
            GUILayout.Label("Versorgung", _smartPopupHeadingStyle);
            WorkerSustenanceRuntime.EnsureInitialized(zdo);
            ZDO homeZdo = WorkerSustenanceRuntime.ResolveHomeZdo(zdo);
            if (homeZdo == null)
            {
                GUILayout.Label("Versorgung wird an der zugewiesenen Ruhestaette gelagert. HomeAnchor ist gerade nicht aufloesbar.", _smartPopupCriticalStyle ?? _smartPopupLabelStyle);
                return;
            }

            int stock = WorkerSustenanceRuntime.GetStock(zdo);
            int target = WorkerSustenanceRuntime.GetTargetStock(zdo);
            int missing = target > 0 ? Math.Max(0, target - stock) : 0;
            int playerCount = WorkerSustenanceRuntime.CountPlayerSupply(player, zdo);
            bool hungry = WorkerSustenanceRuntime.IsHungry(zdo);
            double coverageDays = WorkerSustenanceRuntime.GetCoverageDays(zdo);
            string coverage = WorkerSustenanceRuntime.FormatCoverageDays(zdo);
            string foodSummary = WorkerSustenanceRuntime.GetEnabledFoodSummary(zdo);

            GUIStyle supplyStateStyle;
            string supplyState;
            if (hungry && stock <= 0)
            {
                supplyStateStyle = _smartPopupCriticalStyle ?? _smartPopupLabelStyle;
                supplyState = $"KRITISCH: {needWord} fehlt — Worker wartet auf {foodSummary}.";
            }
            else if (target <= 0)
            {
                supplyStateStyle = _smartPopupWarnStyle ?? _smartPopupLabelStyle;
                supplyState = $"AUTO-VERSORGUNG AUS — vorhandener passender Vorrat: {stock}.";
            }
            else if (stock <= 0)
            {
                supplyStateStyle = _smartPopupCriticalStyle ?? _smartPopupLabelStyle;
                supplyState = $"VORRAT LEER — bis Ziel fehlen {target} Mahlzeiten.";
            }
            else if (missing > 0)
            {
                supplyStateStyle = _smartPopupWarnStyle ?? _smartPopupLabelStyle;
                supplyState = $"NACHFUELLEN: noch {missing} Mahlzeiten bis zum Zielbestand.";
            }
            else
            {
                supplyStateStyle = _smartPopupGoodStyle ?? _smartPopupLabelStyle;
                supplyState = "VERSORGUNG OK — Zielbestand erreicht.";
            }

            GUILayout.Label(supplyState, supplyStateStyle);
            GUILayout.Label($"Verbrauch: ca. 1 Mahlzeit pro Valheim-Tag  •  erlaubt: {foodSummary}", _smartPopupLabelStyle);
            GUILayout.Label($"Naechste Versorgung: {(hungry ? "JETZT" : WorkerSustenanceRuntime.FormatTimeUntilMeal(zdo))}", hungry ? (_smartPopupCriticalStyle ?? _smartPopupLabelStyle) : _smartPopupLabelStyle);
            GUILayout.Label($"Ruhestaetten-Vorrat: {stock}" + (target > 0 ? $" / Ziel {target}  •  Fehlt {missing}" : "  •  Auto-Nachfuellen deaktiviert"), _smartPopupLabelStyle);
            GUILayout.Label($"Geschaetzte Reichweite: ~{coverage} (inkl. aktueller Saettigung)", coverageDays < 1.0 ? (_smartPopupWarnStyle ?? _smartPopupLabelStyle) : _smartPopupLabelStyle);

            GUILayout.Space(5f);
            GUILayout.Label("Erlaubte Nahrung / Prioritaet", _smartPopupHeadingStyle);
            GUILayout.Label("Nur die hier aktivierten Lebensmittel werden gefressen oder automatisch geliefert. Prioritaet 1 wird zuerst verwendet; fehlt sie, darf der Worker auf die naechste aktivierte Nahrung ausweichen.", _smartPopupLabelStyle);

            System.Collections.Generic.List<WorkerSustenanceRuntime.FoodOption> options = WorkerSustenanceRuntime.GetFoodOptions(zdo);
            int enabledFoodCount = 0;
            for (int oi = 0; oi < options.Count; oi++) if (options[oi].Enabled) enabledFoodCount++;
            for (int i = 0; i < options.Count; i++)
            {
                WorkerSustenanceRuntime.FoodOption option = options[i];
                int homeCount = WorkerSustenanceRuntime.GetFoodStock(zdo, option.Prefab);
                int inventoryCount = WorkerSustenanceRuntime.CountPlayerSupply(player, option.Prefab);
                string tag = string.IsNullOrWhiteSpace(option.Tag) ? string.Empty : $"  [{option.Tag}]";

                GUILayout.BeginHorizontal();
                GUI.enabled = writable;
                bool nextEnabled = GUILayout.Toggle(option.Enabled, $"{option.DisplayName}{tag}", GUILayout.ExpandWidth(true));
                GUI.enabled = true;
                if (nextEnabled != option.Enabled)
                {
                    if (WorkerSustenanceRuntime.SetFoodEnabled(zdo, option.Prefab, nextEnabled))
                    {
                        SetStatus($"Nahrung fuer Worker aktualisiert: {option.DisplayName} {(nextEnabled ? "erlaubt" : "deaktiviert")}.");
                        GUIUtility.ExitGUI();
                    }
                    else
                    {
                        SetStatus("Mindestens eine Nahrung muss fuer den Worker aktiviert bleiben.");
                    }
                }

                if (option.Enabled)
                {
                    GUILayout.Label($"P{option.Priority}", _smartPopupLabelStyle, GUILayout.Width(28f));
                    GUI.enabled = writable && option.Priority > 1;
                    if (GUILayout.Button("▲", _smartPopupButtonStyle, GUILayout.Width(30f)))
                    {
                        WorkerSustenanceRuntime.MoveFoodPriority(zdo, option.Prefab, -1);
                        GUIUtility.ExitGUI();
                    }
                    GUI.enabled = writable && option.Priority < enabledFoodCount;
                    if (GUILayout.Button("▼", _smartPopupButtonStyle, GUILayout.Width(30f)))
                    {
                        WorkerSustenanceRuntime.MoveFoodPriority(zdo, option.Prefab, 1);
                        GUIUtility.ExitGUI();
                    }
                    GUI.enabled = true;
                }
                else
                {
                    GUILayout.Space(88f);
                }
                GUILayout.Label($"Ruhestaette {homeCount}  •  Du {inventoryCount}", _smartPopupLabelStyle, GUILayout.Width(170f));
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            GUI.enabled = writable;
            if (GUILayout.Button("-5", _smartPopupButtonStyle, GUILayout.Width(48f))) WorkerSustenanceRuntime.SetTargetStock(zdo, target - 5);
            if (GUILayout.Button("-1", _smartPopupButtonStyle, GUILayout.Width(48f))) WorkerSustenanceRuntime.SetTargetStock(zdo, target - 1);
            GUILayout.Label(target <= 0 ? "Zielbestand: AUS" : $"Zielbestand: {target} Mahlzeiten", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("+1", _smartPopupButtonStyle, GUILayout.Width(48f))) WorkerSustenanceRuntime.SetTargetStock(zdo, target + 1);
            if (GUILayout.Button("+5", _smartPopupButtonStyle, GUILayout.Width(48f))) WorkerSustenanceRuntime.SetTargetStock(zdo, target + 5);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Label($"Passende Nahrung im Spielerinventar: {playerCount}", _smartPopupLabelStyle);
            GUILayout.BeginHorizontal();
            GUI.enabled = writable && playerCount > 0;
            if (GUILayout.Button("1 einlagern", _smartPopupButtonStyle))
            {
                int moved = WorkerSustenanceRuntime.DepositFromPlayer(player, zdo, 1);
                SetStatus(moved > 0 ? "1 Mahlzeit an der Ruhestaette eingelagert." : "Einlagern fehlgeschlagen.");
                GUIUtility.ExitGUI();
            }
            if (GUILayout.Button("5 einlagern", _smartPopupButtonStyle))
            {
                int moved = WorkerSustenanceRuntime.DepositFromPlayer(player, zdo, 5);
                SetStatus(moved > 0 ? $"{moved} Mahlzeiten an der Ruhestaette eingelagert." : "Einlagern fehlgeschlagen.");
                GUIUtility.ExitGUI();
            }
            if (GUILayout.Button("Alle einlagern", _smartPopupButtonStyle))
            {
                int moved = WorkerSustenanceRuntime.DepositFromPlayer(player, zdo, Math.Max(1, playerCount));
                SetStatus(moved > 0 ? $"{moved} Mahlzeiten an der Ruhestaette eingelagert." : "Einlagern fehlgeschlagen.");
                GUIUtility.ExitGUI();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.enabled = writable && stock > 0;
            if (GUILayout.Button("1 nehmen", _smartPopupButtonStyle))
            {
                int moved = WorkerSustenanceRuntime.WithdrawToPlayer(player, zdo, 1);
                SetStatus(moved > 0 ? "1 Versorgungseinheit entnommen." : "Entnehmen fehlgeschlagen oder Inventar voll.");
                GUIUtility.ExitGUI();
            }
            if (GUILayout.Button("Alles nehmen", _smartPopupButtonStyle))
            {
                int moved = WorkerSustenanceRuntime.WithdrawToPlayer(player, zdo, stock);
                SetStatus(moved > 0 ? $"{moved} Versorgungseinheiten entnommen." : "Entnehmen fehlgeschlagen oder Inventar voll.");
                GUIUtility.ExitGUI();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Label("Der Zielbestand ist persistent gespeichert. Der Versorger nutzt nur aktivierte Nahrung und folgt der Prioritaet oben. Ziel 0 deaktiviert die automatische Versorgung fuer diese Ruhestaette.", _smartPopupLabelStyle);
        }

        private void DrawWorkerTaskProfile(ZDO zdo, WorkerRole role, bool writable, string species)
        {
            if (zdo == null) return;

            if (role == WorkerRole.Gatherer)
            {
                GUILayout.Space(10f);
                GUILayout.Label("Aufgaben dieses Arbeiters", _smartPopupHeadingStyle);
                GUILayout.Label("Links schaltest du die Aufgabe per Checkbox an/aus. Rechts daneben stellst du die Prioritaet ein: Linksklick = hoeher, Rechtsklick = niedriger. 1 = hoechste Prioritaet, 4 = niedrigste. Bei gleicher Prioritaet wechseln Sammeln und Ernten an sicheren Arbeitszyklus-Grenzen fair ab; Ernten startet zuerst. Die Workzone legt nur den Arbeitsbereich fest.", _smartPopupLabelStyle);

                WorkerTaskFlags tasks = WorkerJobProfileRuntime.ReadTasks(zdo, role);
                bool gather = (tasks & WorkerTaskFlags.Gather) != 0;
                bool harvest = (tasks & WorkerTaskFlags.Harvest) != 0;

                GUILayout.BeginHorizontal();
                GUILayout.Label("Sammeln  (lose Steine/Holz/Aeste + Arbeitsdrops)", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                gather = DrawWorkerTaskPriorityBox(zdo, role, WorkerTaskFlags.Gather, writable, gather, species);
                if (GUILayout.Button(_gatherTaskDetailsExpanded ? "▲" : "▼", _smartPopupButtonStyle, GUILayout.Width(36f), GUILayout.Height(24f)))
                    _gatherTaskDetailsExpanded = !_gatherTaskDetailsExpanded;
                GUILayout.EndHorizontal();

                if (_gatherTaskDetailsExpanded)
                {
                    GUILayout.Space(4f);
                    GUILayout.Label("Sammeln - was darf dieser Worker aufnehmen?", _smartPopupLabelStyle);
                    GUI.enabled = writable;
                    for (int i = 0; i < GatherHarvestCategoryFilterRuntime.GatherOptions.Length; i++)
                    {
                        WorkerCategoryOption option = GatherHarvestCategoryFilterRuntime.GatherOptions[i];
                        bool allowed = GatherHarvestCategoryFilterRuntime.IsGatherCategoryAllowed(zdo, option.Key);
                        bool nextAllowed = GUILayout.Toggle(allowed, "   " + option.DisplayName, GUI.skin.toggle);
                        if (nextAllowed == allowed) continue;
                        GatherHarvestCategoryFilterRuntime.SetGatherCategoryAllowed(zdo, option.Key, nextAllowed);
                        SetStatus(species + ": Sammeln / " + option.DisplayName + (nextAllowed ? " freigegeben." : " gesperrt."));
                    }
                    GUI.enabled = true;
                }

                GUILayout.Space(6f);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Ernten  (Gemuese/Samenpflanzen/Beeren/Pilze/Pickables)", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                harvest = DrawWorkerTaskPriorityBox(zdo, role, WorkerTaskFlags.Harvest, writable, harvest, species);
                if (GUILayout.Button(_harvestTaskDetailsExpanded ? "▲" : "▼", _smartPopupButtonStyle, GUILayout.Width(36f), GUILayout.Height(24f)))
                    _harvestTaskDetailsExpanded = !_harvestTaskDetailsExpanded;
                GUILayout.EndHorizontal();

                if (_harvestTaskDetailsExpanded)
                {
                    GUILayout.Space(4f);
                    GUILayout.Label("Ernten - was darf dieser Worker abernten?", _smartPopupLabelStyle);
                    GUI.enabled = writable;
                    for (int i = 0; i < GatherHarvestCategoryFilterRuntime.HarvestOptions.Length; i++)
                    {
                        WorkerCategoryOption option = GatherHarvestCategoryFilterRuntime.HarvestOptions[i];
                        bool allowed = GatherHarvestCategoryFilterRuntime.IsHarvestCategoryAllowed(zdo, option.Key);
                        bool nextAllowed = GUILayout.Toggle(allowed, "   " + option.DisplayName, GUI.skin.toggle);
                        if (nextAllowed == allowed) continue;
                        GatherHarvestCategoryFilterRuntime.SetHarvestCategoryAllowed(zdo, option.Key, nextAllowed);
                        SetStatus(species + ": Ernten / " + option.DisplayName + (nextAllowed ? " freigegeben." : " gesperrt."));
                    }
                    GUI.enabled = true;
                }


                if (GatherHarvestCategoryFilterRuntime.HasCustomGatherFilter(zdo) || GatherHarvestCategoryFilterRuntime.HasCustomHarvestFilter(zdo))
                    GUILayout.Label("Individuelle Kategorien gelten pro Worker. Fuer gefilterte Aufgaben bleibt die Offscreen-Simulation vorerst pausiert, damit keine gesperrte Ressource simuliert wird.", _smartPopupLabelStyle);
                return;
            }

            if (role == WorkerRole.Hauler)
            {
                GUILayout.Space(10f);
                GUILayout.Label("Logistik", _smartPopupHeadingStyle);
                GUILayout.Label("Jeder Logistik-Job hat eine eigene Checkbox und Prioritaet 1-4. Linksklick auf die Zahl = hoeher, Rechtsklick = niedriger. Bei gleicher Prioritaet gilt: Versorgen > Transportieren > Sortieren. Laufende Fracht wird zuerst beendet.", _smartPopupLabelStyle);

                WorkerTaskFlags availableTasks = WorkerJobProfileRuntime.GetAvailableTasks(zdo, role);
                WorkerTaskFlags tasks = WorkerJobProfileRuntime.ReadTasks(zdo, role);
                bool transport = (tasks & WorkerTaskFlags.Transport) != 0;
                bool sort = (tasks & WorkerTaskFlags.Sort) != 0;
                bool provision = (tasks & WorkerTaskFlags.Provision) != 0;
                bool canSort = (availableTasks & WorkerTaskFlags.Sort) != 0;
                bool canProvision = (availableTasks & WorkerTaskFlags.Provision) != 0;
                string normalizedSpecies = SpeciesRoleRegistry.NormalizePrefabName(species);

                GUILayout.BeginHorizontal();
                GUILayout.Label("Transportieren", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                transport = DrawWorkerTaskPriorityBox(zdo, role, WorkerTaskFlags.Transport, writable, transport, species);
                GUILayout.EndHorizontal();

                if (canSort)
                {
                    GUILayout.Space(6f);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Sortieren", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                    sort = DrawWorkerTaskPriorityBox(zdo, role, WorkerTaskFlags.Sort, writable, sort, species);
                    GUILayout.EndHorizontal();
                }

                if (canProvision)
                {
                    GUILayout.Space(6f);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Versorgen", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                    provision = DrawWorkerTaskPriorityBox(zdo, role, WorkerTaskFlags.Provision, writable, provision, species);
                    GUILayout.EndHorizontal();
                    GUILayout.Label("Versorger holen die benoetigte Nahrung / Energie aus normalen Basiskisten bzw. dem Aussenposten-Hub und bringen sie zu den Ruhestaetten.", _smartPopupLabelStyle);
                }

                if (!canSort && !canProvision)
                    GUILayout.Label(normalizedSpecies + " ist ein spezialisierter Transporter; Sortieren und Versorgen werden fuer diese Kreatur nicht angeboten.", _smartPopupLabelStyle);

                return;
            }

            if (role == WorkerRole.Gardener)
            {
                GUILayout.Space(10f);
                GUILayout.Label("Erlaubte Aufgaben", _smartPopupHeadingStyle);
                GUILayout.Label("Jeder verfuegbare Job hat eine eigene Checkbox und Prioritaet 1-4. Linksklick auf die Zahl = hoeher, Rechtsklick = niedriger. Sind Ernten und Pflanzen gleich priorisiert und beide haben Arbeit, gilt 1:1: einmal ernten, danach einmal pflanzen. Die Spezies begrenzt weiterhin, was moeglich ist.", _smartPopupLabelStyle);

                WorkerTaskFlags availableTasks = WorkerJobProfileRuntime.GetAvailableTasks(zdo, role);
                WorkerTaskFlags tasks = WorkerJobProfileRuntime.ReadTasks(zdo, role);
                bool harvest = (tasks & WorkerTaskFlags.Harvest) != 0;
                bool garden = (tasks & WorkerTaskFlags.Garden) != 0;
                bool forestry = (tasks & WorkerTaskFlags.Forestry) != 0;
                bool canHarvest = (availableTasks & WorkerTaskFlags.Harvest) != 0;

                if (canHarvest)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Ernten  (reife Pflanzen/Pickables in der Workzone)", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                    harvest = DrawWorkerTaskPriorityBox(zdo, role, WorkerTaskFlags.Harvest, writable, harvest, species);
                    if (GUILayout.Button(_harvestTaskDetailsExpanded ? "▲" : "▼", _smartPopupButtonStyle, GUILayout.Width(36f), GUILayout.Height(24f)))
                        _harvestTaskDetailsExpanded = !_harvestTaskDetailsExpanded;
                    GUILayout.EndHorizontal();

                    if (_harvestTaskDetailsExpanded)
                    {
                        GUILayout.Space(4f);
                        GUILayout.Label("Ernten - welche reifen Ressourcen darf dieser Worker abernten?", _smartPopupLabelStyle);
                        GUI.enabled = writable;
                        for (int i = 0; i < GatherHarvestCategoryFilterRuntime.HarvestOptions.Length; i++)
                        {
                            WorkerCategoryOption option = GatherHarvestCategoryFilterRuntime.HarvestOptions[i];
                            bool allowed = GatherHarvestCategoryFilterRuntime.IsHarvestCategoryAllowed(zdo, option.Key);
                            bool nextAllowed = GUILayout.Toggle(allowed, "   " + option.DisplayName, GUI.skin.toggle);
                            if (nextAllowed == allowed) continue;
                            GatherHarvestCategoryFilterRuntime.SetHarvestCategoryAllowed(zdo, option.Key, nextAllowed);
                            SetStatus(species + ": Ernten / " + option.DisplayName + (nextAllowed ? " freigegeben." : " gesperrt."));
                        }
                        GUI.enabled = true;
                    }
                    GUILayout.Space(6f);
                    string normalizedGardenerSpecies = SpeciesRoleRegistry.NormalizePrefabName(species);
                    if (string.Equals(normalizedGardenerSpecies, "Fuling_Shaman", StringComparison.OrdinalIgnoreCase))
                        GUILayout.Label("Spezialfaehigkeit: Flaechenernten + Flaechenpflanzen. Der Fuling Shaman bearbeitet kompakte Feldgruppen in einem Arbeitsgang, kann aber keine Beerenbuesche pflanzen.", _smartPopupLabelStyle);
                    else if (string.Equals(normalizedGardenerSpecies, "Greydwarf_Shaman", StringComparison.OrdinalIgnoreCase))
                        GUILayout.Label("Spezialfaehigkeit: Wildwuchs. Der Greydwarf Shaman darf geplante Himbeer-, Blaubeer-, Moltebeerbuesche und Disteln pflanzen und reife Buesche/Pickables auch selbst ernten.", _smartPopupLabelStyle);
                    GUILayout.Space(6f);
                }

                GUILayout.BeginHorizontal();
                GUILayout.Label("Pflanzen", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                garden = DrawWorkerTaskPriorityBox(zdo, role, WorkerTaskFlags.Garden, writable, garden, species);
                if (GUILayout.Button(_gardenerPlantDetailsExpanded ? "▲" : "▼", _smartPopupButtonStyle, GUILayout.Width(36f), GUILayout.Height(24f)))
                    _gardenerPlantDetailsExpanded = !_gardenerPlantDetailsExpanded;
                GUILayout.EndHorizontal();

                if (_gardenerPlantDetailsExpanded)
                {
                    GUILayout.Space(4f);
                    GUILayout.Label("Pflanzen - nur deine geplanten Ghosts werden ausgefuehrt. Saatpflanzen planst du genauso wie normales Gemuese.", _smartPopupLabelStyle);
                    bool normal = GardenerTaskFilterRuntime.AllowNormalCropPlanting(zdo);
                    GUI.enabled = writable;
                    bool nextNormal = GUILayout.Toggle(normal, "   Geplante Feldpflanzen inkl. Saatpflanzen", GUI.skin.toggle);
                    if (nextNormal != normal)
                    {
                        GardenerTaskFilterRuntime.SetNormalCropPlanting(zdo, nextNormal);
                        SetStatus(species + ": geplante Feldpflanzen " + (nextNormal ? "freigegeben." : "gesperrt."));
                    }
                    GUI.enabled = true;
                }

                GUILayout.Space(6f);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Foerstern", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                forestry = DrawWorkerTaskPriorityBox(zdo, role, WorkerTaskFlags.Forestry, writable, forestry, species);
                if (GUILayout.Button(_gardenerForestryDetailsExpanded ? "▲" : "▼", _smartPopupButtonStyle, GUILayout.Width(36f), GUILayout.Height(24f)))
                    _gardenerForestryDetailsExpanded = !_gardenerForestryDetailsExpanded;
                GUILayout.EndHorizontal();

                if (_gardenerForestryDetailsExpanded)
                {
                    GUILayout.Space(4f);
                    GUILayout.Label("Foerstern - welche Baumarten darf dieser Worker nachpflanzen?", _smartPopupLabelStyle);
                    var forestryOptions = new System.Collections.Generic.List<GardenerForestryOption>(8);
                    GardenerTaskFilterRuntime.CollectForestryOptions(species, forestryOptions);
                    GUI.enabled = writable;
                    for (int i = 0; i < forestryOptions.Count; i++)
                    {
                        GardenerForestryOption option = forestryOptions[i];
                        bool allowed = GardenerTaskFilterRuntime.IsForestrySpeciesAllowed(zdo, option.Key);
                        bool nextAllowed = GUILayout.Toggle(allowed, "   " + option.DisplayName, GUI.skin.toggle);
                        if (nextAllowed == allowed) continue;
                        GardenerTaskFilterRuntime.SetForestrySpeciesAllowed(zdo, option.Key, nextAllowed);
                        SetStatus(species + ": Foerstern / " + option.DisplayName + (nextAllowed ? " freigegeben." : " gesperrt."));
                    }
                    GUI.enabled = true;
                }


                bool canBake = (availableTasks & WorkerTaskFlags.Bake) != 0;
                if (canBake)
                {
                    GUILayout.Space(8f);
                    GUILayout.Label("Produktion", _smartPopupHeadingStyle);
                    GUILayout.Label("Shaman-Spezialfaehigkeit: Kochen/Produktion ist ein priorisierbarer Job. Greydwarf Shaman = T1 (Kessel), Fuling Shaman = T3 (alle Koch-/Produktionsstationen).", _smartPopupLabelStyle);
                    bool bake = (tasks & WorkerTaskFlags.Bake) != 0;
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Kochen / Produktion", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                    bake = DrawWorkerTaskPriorityBox(zdo, role, WorkerTaskFlags.Bake, writable, bake, species);
                    GUILayout.EndHorizontal();
                    GUILayout.Label("Kochstufe: " + CookingWorkerTierRuntime.DescribeTier(null, zdo), _smartPopupLabelStyle);
                    GUILayout.Space(4f);
                    GUILayout.Label("Auftraege und Zielbestaende bleiben direkt an den jeweils freigeschalteten Koch-/Produktionsstationen (inkl. Kessel).", _smartPopupLabelStyle);
                    GUILayout.Label("Aktuell: " + BakerWorkRuntime.GetDebug(zdo), _smartPopupLabelStyle);
                }

                if (GardenerTaskFilterRuntime.HasCustomGardenFilter(zdo) || GardenerTaskFilterRuntime.HasCustomForestryFilter(zdo) || GatherHarvestCategoryFilterRuntime.HasCustomHarvestFilter(zdo))
                    GUILayout.Label("Individuelle Ernte-/Pflanz-/Forstfilter gelten pro Worker. Gefilterte Offscreen-Arbeit bleibt konservativ, damit keine gesperrte Ressource simuliert wird.", _smartPopupLabelStyle);
                return;
            }

            if (role == WorkerRole.Butcher)
            {
                GUILayout.Space(10f);
                GUILayout.Label("Metzger / Tierpflege", _smartPopupHeadingStyle);
                WorkerTaskFlags tasks = WorkerJobProfileRuntime.ReadTasks(zdo, role);
                bool butcher = (tasks & WorkerTaskFlags.Butcher) != 0;
                GUILayout.BeginHorizontal();
                GUILayout.Label("Tierpflege / Metzger", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                butcher = DrawWorkerTaskPriorityBox(zdo, role, WorkerTaskFlags.Butcher, writable, butcher, species);
                GUILayout.EndHorizontal();
                GUILayout.Label("Die Min/Max-Regeln oben bleiben die Detailregeln dieses Jobs. Auf Aus werden keine neuen Tierpflege-/Schlachtauftraege angenommen.", _smartPopupLabelStyle);
                return;
            }

            if (role == WorkerRole.Firekeeper)
            {
                GUILayout.Space(10f);
                GUILayout.Label("Feuerhueter", _smartPopupHeadingStyle);
                GUILayout.Label("Der Firekeeper-Job wird ueber Checkbox + Prioritaet 1-4 gesteuert. Die Unterpunkte legen nur fest, welche Ziele der Surtling innerhalb dieses Jobs pflegen darf.", _smartPopupLabelStyle);

                WorkerTaskFlags tasks = WorkerJobProfileRuntime.ReadTasks(zdo, role);
                bool keepFires = (tasks & WorkerTaskFlags.KeepFires) != 0;
                GUILayout.BeginHorizontal();
                GUILayout.Label("Firekeeper / Feuer hueten", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                keepFires = DrawWorkerTaskPriorityBox(zdo, role, WorkerTaskFlags.KeepFires, writable, keepFires, species);
                GUILayout.EndHorizontal();

                GUILayout.Space(6f);
                bool fires = FirekeeperTaskFilterRuntime.AllowFires(zdo);
                bool ovens = FirekeeperTaskFilterRuntime.AllowOvens(zdo);

                GUI.enabled = writable;
                bool nextFires = GUILayout.Toggle(fires, "Feuerstellen / Fackeln kostenlos nachspeisen", GUI.skin.toggle, GUILayout.ExpandWidth(true));
                if (nextFires != fires)
                {
                    FirekeeperTaskFilterRuntime.SetAllowFires(zdo, nextFires);
                    SetStatus(species + ": Feuerstellen / Fackeln " + (nextFires ? "freigegeben." : "gesperrt."));
                    fires = nextFires;
                }

                bool nextOvens = GUILayout.Toggle(ovens, "Oefen/Maschinen: Schmelz-/Hochofen + Steinofen kostenlos mit Surtling-Hitze pflegen", GUI.skin.toggle, GUILayout.ExpandWidth(true));
                if (nextOvens != ovens)
                {
                    FirekeeperTaskFilterRuntime.SetAllowOvens(zdo, nextOvens);
                    SetStatus(species + ": Oefen/Maschinen " + (nextOvens ? "freigegeben." : "gesperrt."));
                    ovens = nextOvens;
                }
                GUI.enabled = true;

                if (!keepFires)
                    GUILayout.Label("Firekeeper steht auf Aus: Der Surtling nimmt keine neuen Feuer-/Ofenauftraege an.", _smartPopupLabelStyle);
                else if (!fires && !ovens)
                    GUILayout.Label("Firekeeper ist aktiv, aber beide Zielgruppen sind gesperrt.", _smartPopupLabelStyle);
                return;
            }

            if (role == WorkerRole.Baker)
            {
                GUILayout.Space(10f);
                GUILayout.Label("Koch / Produktion", _smartPopupHeadingStyle);
                GUILayout.Label("Der Koch-Job wird ueber Checkbox + Prioritaet 1-4 gesteuert. Zielbestaende bleiben direkt an den Koch-/Produktionsstationen.", _smartPopupLabelStyle);

                WorkerTaskFlags tasks = WorkerJobProfileRuntime.ReadTasks(zdo, role);
                bool bake = (tasks & WorkerTaskFlags.Bake) != 0;
                GUILayout.BeginHorizontal();
                GUILayout.Label("Kochen / Produktion", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                bake = DrawWorkerTaskPriorityBox(zdo, role, WorkerTaskFlags.Bake, writable, bake, species);
                GUILayout.EndHorizontal();
                GUILayout.Label("Kochstufe: " + CookingWorkerTierRuntime.DescribeTier(null, zdo), _smartPopupLabelStyle);

                GUILayout.Space(4f);
                GUILayout.Label("Auftraege liegen direkt an den Produktionsstationen. Fehlende Vorprodukte werden nicht automatisch erzeugt.", _smartPopupLabelStyle);
                GUILayout.Label("Aktuell: " + BakerWorkRuntime.GetDebug(zdo), _smartPopupLabelStyle);
                if (!bake)
                    GUILayout.Label("Kochen / Produktion steht auf Aus: Dieser Worker nimmt keine neuen Produktionsauftraege an.", _smartPopupLabelStyle);
                return;
            }

            if (role == WorkerRole.Smith)
            {
                GUILayout.Space(10f);
                GUILayout.Label("Schmied / Reparateur", _smartPopupHeadingStyle);
                WorkerTaskFlags tasks = WorkerJobProfileRuntime.ReadTasks(zdo, role);
                bool smith = (tasks & WorkerTaskFlags.Smith) != 0;
                bool repair = (tasks & WorkerTaskFlags.Repair) != 0;
                GUILayout.BeginHorizontal();
                GUILayout.Label("Schmieden / Metall verarbeiten", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                smith = DrawWorkerTaskPriorityBox(zdo, role, WorkerTaskFlags.Smith, writable, smith, species);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Basis reparieren", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                repair = DrawWorkerTaskPriorityBox(zdo, role, WorkerTaskFlags.Repair, writable, repair, species);
                GUILayout.EndHorizontal();
                GUILayout.Label("Schmieden versorgt passende Schmelz-/Hochofen mit echtem Erz/Schrott. Basis reparieren sucht geladene, beschaedigte Bauteile in der Workzone und laeuft physisch zum Reparaturpunkt.", _smartPopupLabelStyle);
                return;
            }

            if (role == WorkerRole.Fisher)
            {
                GUILayout.Space(10f);
                GUILayout.Label("Fischer", _smartPopupHeadingStyle);
                GUILayout.Label("Der Neck sucht nur echte geladene Fische innerhalb seiner Workzone. Gefangene Fische belegen das normale 2-Slot-Workerinventar und werden in eine passende Kiste bzw. ins FieldDepot gebracht.", _smartPopupLabelStyle);
                WorkerTaskFlags tasks = WorkerJobProfileRuntime.ReadTasks(zdo, role);
                bool fish = (tasks & WorkerTaskFlags.Fish) != 0;
                GUILayout.BeginHorizontal();
                GUILayout.Label("Fischen", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                fish = DrawWorkerTaskPriorityBox(zdo, role, WorkerTaskFlags.Fish, writable, fish, species);
                GUILayout.EndHorizontal();
                if (!fish)
                    GUILayout.Label("Fischen steht auf Aus: Der Neck bleibt an seinem Ufernest.", _smartPopupLabelStyle);
                return;
            }

            if (role == WorkerRole.Guard)
            {
                GUILayout.Space(10f);
                GUILayout.Label("Wache", _smartPopupHeadingStyle);
                WorkerTaskFlags tasks = WorkerJobProfileRuntime.ReadTasks(zdo, role);
                bool guard = (tasks & WorkerTaskFlags.Guard) != 0;
                GUILayout.BeginHorizontal();
                GUILayout.Label("Bewachen", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                guard = DrawWorkerTaskPriorityBox(zdo, role, WorkerTaskFlags.Guard, writable, guard, species);
                GUILayout.EndHorizontal();
                return;
            }

            if (role == WorkerRole.Hunter)
            {
                GUILayout.Space(10f);
                GUILayout.Label("Jaeger / Wache", _smartPopupHeadingStyle);
                GUILayout.Label("Jagen und Bewachen sind zwei getrennte Jobs. Jeder hat eine Checkbox und Prioritaet 1-4; 1 ist die hoechste Prioritaet. Bei gleicher Prioritaet gewinnt Jagen immer vor Bewachen. Ein bereits begonnener Jagd-/Beutezyklus wird zuerst sauber beendet.", _smartPopupLabelStyle);

                WorkerTaskFlags tasks = WorkerJobProfileRuntime.ReadTasks(zdo, role);
                bool hunt = (tasks & WorkerTaskFlags.Hunt) != 0;
                bool guard = (tasks & WorkerTaskFlags.Guard) != 0;
                GUILayout.BeginHorizontal();
                GUILayout.Label("Jagen", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                hunt = DrawWorkerTaskPriorityBox(zdo, role, WorkerTaskFlags.Hunt, writable, hunt, species);
                GUILayout.EndHorizontal();
                GUILayout.Space(4f);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Bewachen", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                guard = DrawWorkerTaskPriorityBox(zdo, role, WorkerTaskFlags.Guard, writable, guard, species);
                GUILayout.EndHorizontal();

                GUILayout.Space(6f);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Jagdziele", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                if (GUILayout.Button(_hunterPreyDetailsExpanded ? "▲" : "▼", _smartPopupButtonStyle, GUILayout.Width(36f), GUILayout.Height(24f)))
                    _hunterPreyDetailsExpanded = !_hunterPreyDetailsExpanded;
                GUILayout.EndHorizontal();

                if (_hunterPreyDetailsExpanded)
                {
                    GUILayout.Space(4f);
                    GUILayout.Label("Welche Tiere darf dieser Jaeger als Beute auswaehlen?", _smartPopupLabelStyle);
                    GUI.enabled = writable;
                    for (int i = 0; i < HunterPreyFilterRuntime.Options.Length; i++)
                    {
                        HunterPreyOption option = HunterPreyFilterRuntime.Options[i];
                        if (!HunterPreyFilterRuntime.IsOptionAvailableForWorker(zdo, option)) continue;
                        bool allowed = HunterPreyFilterRuntime.IsPreyAllowed(zdo, option.Key);
                        bool nextAllowed = GUILayout.Toggle(allowed, "   " + option.DisplayName, GUI.skin.toggle);
                        if (nextAllowed == allowed) continue;
                        HunterPreyFilterRuntime.SetPreyAllowed(zdo, option.Key, nextAllowed);
                        SetStatus(species + ": Jagdziel " + option.DisplayName + (nextAllowed ? " freigegeben." : " gesperrt."));
                    }
                    GUI.enabled = true;

                    if (HunterPreyFilterRuntime.HasCustomFilter(zdo))
                        GUILayout.Label("Die Jagdziel-Auswahl gilt pro Worker. Nicht angehakte Tiere werden vom Hunter ignoriert.", _smartPopupLabelStyle);
                }
                return;
            }

            if (role == WorkerRole.Lumberjack)
            {
                GUILayout.Space(10f);
                int woodTier = WorkerSkillCatalog.GetWoodcutTier(species);
                GUILayout.Label("Holzfaeller", _smartPopupHeadingStyle);
                WorkerTaskFlags tasks = WorkerJobProfileRuntime.ReadTasks(zdo, role);
                bool fellTrees = (tasks & WorkerTaskFlags.FellTrees) != 0;
                GUILayout.BeginHorizontal();
                GUILayout.Label("Holz faellen", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                fellTrees = DrawWorkerTaskPriorityBox(zdo, role, WorkerTaskFlags.FellTrees, writable, fellTrees, species);
                GUILayout.EndHorizontal();

                GUILayout.Space(6f);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Baeume", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                if (GUILayout.Button(_lumberjackTreeDetailsExpanded ? "▲" : "▼", _smartPopupButtonStyle, GUILayout.Width(36f), GUILayout.Height(24f)))
                    _lumberjackTreeDetailsExpanded = !_lumberjackTreeDetailsExpanded;
                GUILayout.EndHorizontal();

                if (_lumberjackTreeDetailsExpanded)
                {
                    GUILayout.Space(4f);
                    GUILayout.Label($"Nur Baumarten bis Holzfaeller-Stufe {woodTier} werden angezeigt. Hoehere Stufen bleiben ausgeblendet.", _smartPopupLabelStyle);

                    LumberjackTreeFilterRuntime.CollectVisibleOptions(woodTier, _lumberjackTreeOptionScratch);
                    if (_lumberjackTreeOptionScratch.Count == 0)
                    {
                        GUILayout.Label("Keine schlagbaren Baumarten fuer diese Stufe gefunden.", _smartPopupLabelStyle);
                        return;
                    }

                    GUI.enabled = writable;
                    for (int i = 0; i < _lumberjackTreeOptionScratch.Count; i++)
                    {
                        LumberjackTreeOption option = _lumberjackTreeOptionScratch[i];
                        bool allowed = LumberjackTreeFilterRuntime.IsSpeciesAllowed(zdo, option.Key);
                        bool nextAllowed = GUILayout.Toggle(allowed, "   " + option.DisplayName, GUI.skin.toggle);
                        if (nextAllowed == allowed) continue;

                        LumberjackTreeFilterRuntime.SetSpeciesAllowed(zdo, option.Key, nextAllowed);
                        SetStatus(species + ": " + option.DisplayName + (nextAllowed ? " freigegeben." : " gesperrt."));
                    }
                    GUI.enabled = true;

                    if (LumberjackTreeFilterRuntime.HasCustomFilter(zdo))
                        GUILayout.Label("Hinweis: Individuelle Baumfilter laufen vorerst nur physisch. Die alte globale Holz-Offscreen-Simulation wird fuer diesen Worker pausiert, damit keine falsche Baumart simuliert wird.", _smartPopupLabelStyle);
                }
            }

            if (role == WorkerRole.Stonemason)
            {
                GUILayout.Space(10f);
                int stoneTier = WorkerSkillCatalog.GetStoneworkTier(species);
                GUILayout.Label("Steinmetz", _smartPopupHeadingStyle);
                WorkerTaskFlags tasks = WorkerJobProfileRuntime.ReadTasks(zdo, role);
                bool stonework = (tasks & WorkerTaskFlags.Stonework) != 0;
                GUILayout.BeginHorizontal();
                GUILayout.Label("Stein / Erz abbauen", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                stonework = DrawWorkerTaskPriorityBox(zdo, role, WorkerTaskFlags.Stonework, writable, stonework, species);
                GUILayout.EndHorizontal();

                GUILayout.Space(6f);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Rohstoffe", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                if (GUILayout.Button(_stonemasonResourceDetailsExpanded ? "▲" : "▼", _smartPopupButtonStyle, GUILayout.Width(36f), GUILayout.Height(24f)))
                    _stonemasonResourceDetailsExpanded = !_stonemasonResourceDetailsExpanded;
                GUILayout.EndHorizontal();

                if (_stonemasonResourceDetailsExpanded)
                {
                    GUILayout.Space(4f);
                    GUILayout.Label($"Nur Rohstoff-Kategorien bis Steinmetz-Stufe {stoneTier} werden angezeigt. Interne Felsvarianten werden automatisch zusammengefasst.", _smartPopupLabelStyle);

                    StonemasonRockFilterRuntime.CollectVisibleOptions(stoneTier, _stonemasonRockOptionScratch);
                    if (_stonemasonRockOptionScratch.Count == 0)
                    {
                        GUILayout.Label("Keine abbaubaren Rohstoff-Kategorien fuer diese Stufe gefunden.", _smartPopupLabelStyle);
                        return;
                    }

                    GUI.enabled = writable;
                    for (int i = 0; i < _stonemasonRockOptionScratch.Count; i++)
                    {
                        StonemasonRockOption option = _stonemasonRockOptionScratch[i];
                        bool allowed = StonemasonRockFilterRuntime.IsRockTypeAllowed(zdo, option.Key);
                        bool nextAllowed = GUILayout.Toggle(allowed, "   " + option.DisplayName, GUI.skin.toggle);
                        if (nextAllowed == allowed) continue;

                        StonemasonRockFilterRuntime.SetRockTypeAllowed(zdo, option.Key, nextAllowed);
                        SetStatus(species + ": " + option.DisplayName + (nextAllowed ? " freigegeben." : " gesperrt."));
                    }
                    GUI.enabled = true;

                    if (StonemasonRockFilterRuntime.HasCustomFilter(zdo))
                        GUILayout.Label("Der Filter gilt pro Worker. Tier-Grenzen bleiben immer aktiv, auch wenn ein Vorkommen angehakt ist.", _smartPopupLabelStyle);
                }
            }
        }

        private bool DrawWorkerTaskPriorityBox(ZDO zdo, WorkerRole role, WorkerTaskFlags task, bool writable, bool enabled, string species)
        {
            // v0.5.333: task permission and task priority are deliberately separate controls.
            // The old single box overloaded 0 as both "disabled" and the next priority step, which
            // made it too easy to switch a job off while only trying to lower its priority. The
            // checkbox now owns permission; the adjacent box owns only priorities 1..4.
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && writable;
            bool nextEnabled = GUILayout.Toggle(
                enabled,
                new GUIContent(string.Empty, "Aufgabe fuer diesen Worker erlauben / sperren"),
                GUI.skin.toggle,
                GUILayout.Width(24f),
                GUILayout.Height(24f));
            GUI.enabled = previousEnabled;

            if (writable && nextEnabled != enabled)
            {
                if (WorkerJobProfileRuntime.TrySetTaskEnabled(zdo, role, task, nextEnabled))
                {
                    enabled = nextEnabled;
                    SetStatus(species + ": " + GetWorkerTaskPriorityDisplayName(task) +
                              (enabled ? " aktiviert." : " deaktiviert."));
                }
                else
                {
                    nextEnabled = enabled;
                }
            }

            int level = enabled ? WorkerJobProfileRuntime.GetPriority(zdo, role, task) : 0;
            Rect rect = GUILayoutUtility.GetRect(48f, 24f, GUILayout.Width(48f), GUILayout.Height(24f));
            Event evt = Event.current;
            bool hover = rect.Contains(evt.mousePosition);

            if (writable && enabled && hover && evt.type == EventType.MouseDown && (evt.button == 0 || evt.button == 1))
            {
                int next = level;
                if (evt.button == 0)
                {
                    // Left click raises effective priority: 4 -> 3 -> 2 -> 1.
                    if (level > 1) next = level - 1;
                }
                else
                {
                    // Right click lowers effective priority: 1 -> 2 -> 3 -> 4.
                    if (level >= 1 && level < 4) next = level + 1;
                }

                if (next != level && WorkerJobProfileRuntime.TrySetTaskLevel(zdo, role, task, next))
                {
                    level = next;
                    SetStatus(species + ": " + GetWorkerTaskPriorityDisplayName(task) + " -> Prioritaet " + level + ".");
                }
                evt.Use();
            }

            GUI.enabled = previousEnabled && writable && enabled;
            string text = enabled ? level.ToString() : "—";
            GUI.Box(
                rect,
                new GUIContent(text, enabled
                    ? "Linksklick: Prioritaet erhoehen   |   Rechtsklick: Prioritaet verringern   |   1 = hoch, 4 = niedrig"
                    : "Aufgabe ist deaktiviert. Erst das Kaestchen links aktivieren."),
                _smartPopupButtonStyle);
            GUI.enabled = previousEnabled;
            return enabled;
        }

        internal static string GetWorkerTaskPriorityDisplayNameForRemote(WorkerTaskFlags task)
        {
            return GetWorkerTaskPriorityDisplayName(task);
        }

        private static string GetWorkerActiveJobsDebug(ZDO zdo, WorkerRole role)
        {
            if (zdo == null) return "keine ZDO";

            WorkerTaskFlags available = WorkerJobProfileRuntime.GetAvailableTasks(zdo, role);
            if (available == WorkerTaskFlags.None) return "keine modularen Jobs";

            WorkerTaskFlags enabled = WorkerJobProfileRuntime.ReadTasks(zdo, role);
            WorkerTaskFlags[] ordered =
            {
                WorkerTaskFlags.Harvest, WorkerTaskFlags.Gather, WorkerTaskFlags.Garden, WorkerTaskFlags.Forestry,
                WorkerTaskFlags.Bake, WorkerTaskFlags.Transport, WorkerTaskFlags.Sort, WorkerTaskFlags.Provision,
                WorkerTaskFlags.FellTrees, WorkerTaskFlags.Stonework, WorkerTaskFlags.Hunt, WorkerTaskFlags.Guard,
                WorkerTaskFlags.KeepFires, WorkerTaskFlags.Butcher, WorkerTaskFlags.Smith, WorkerTaskFlags.Repair,
                WorkerTaskFlags.Fish, WorkerTaskFlags.Mine
            };

            string result = string.Empty;
            for (int i = 0; i < ordered.Length; i++)
            {
                WorkerTaskFlags task = ordered[i];
                if ((available & task) == 0) continue;
                if (result.Length > 0) result += " | ";
                result += GetWorkerTaskPriorityDisplayName(task) + "=";
                result += (enabled & task) != 0 ? WorkerJobProfileRuntime.GetPriority(zdo, role, task).ToString() : "Aus";
            }

            return result.Length > 0 ? result : "keine modularen Jobs";
        }

        private static string GetWorkerTaskPriorityDisplayName(WorkerTaskFlags task)
        {
            switch (task)
            {
                case WorkerTaskFlags.Gather: return "Sammeln";
                case WorkerTaskFlags.Harvest: return "Ernten";
                case WorkerTaskFlags.Garden: return "Pflanzen";
                case WorkerTaskFlags.Forestry: return "Foerstern";
                case WorkerTaskFlags.Transport: return "Transportieren";
                case WorkerTaskFlags.Sort: return "Sortieren";
                case WorkerTaskFlags.Provision: return "Versorgen";
                case WorkerTaskFlags.FellTrees: return "Holz faellen";
                case WorkerTaskFlags.Stonework: return "Stein / Erz abbauen";
                case WorkerTaskFlags.KeepFires: return "Firekeeper";
                case WorkerTaskFlags.Hunt: return "Jagen";
                case WorkerTaskFlags.Guard: return "Bewachen";
                case WorkerTaskFlags.Bake: return "Kochen / Produktion";
                case WorkerTaskFlags.Butcher: return "Tierpflege / Metzger";
                case WorkerTaskFlags.Smith: return "Schmieden / Metall";
                case WorkerTaskFlags.Repair: return "Basis reparieren";
                case WorkerTaskFlags.Fish: return "Fischen";
                case WorkerTaskFlags.Mine: return "Bergbau";
                default: return "Aufgabe";
            }
        }

        private void DrawPersistentWorkerStanceButton(ZDO zdo, WorkerStance buttonStance, string label, WorkerStance current, bool writable)
        {
            bool active = current == buttonStance;
            GUI.enabled = writable && !active;
            if (GUILayout.Button((active ? "AKTIV • " : string.Empty) + label, _smartPopupButtonStyle))
            {
                WorkerCombatStanceRuntime.SetStance(null, zdo, buttonStance);
                SetStatus("Worker-Haltung -> " + WorkerCombatStanceRuntime.DisplayName(buttonStance) + ".");
            }
            GUI.enabled = true;
        }

        private void DrawPersistentGuardBehaviorButton(ZDO zdo, GuardBehaviorMode buttonMode, string label, GuardBehaviorMode current, bool writable)
        {
            bool active = current == buttonMode;
            GUI.enabled = writable && !active;
            if (GUILayout.Button((active ? "AKTIV • " : string.Empty) + label, _smartPopupButtonStyle))
            {
                GuardBehaviorRuntime.Set(null, zdo, buttonMode);
                SetStatus("Guard-Verhalten -> " + GuardBehaviorRuntime.DisplayName(buttonMode) + ".");
            }
            GUI.enabled = true;
        }

        private void DrawWorkerStanceButton(Character worker, ZDO zdo, WorkerStance buttonStance, string label, WorkerStance current, bool writable)
        {
            bool active = current == buttonStance;
            GUI.enabled = writable && !active;
            if (GUILayout.Button((active ? "AKTIV • " : string.Empty) + label, _smartPopupButtonStyle))
            {
                WorkerCombatStanceRuntime.SetStance(worker, zdo, buttonStance);
                SetStatus($"{SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name)}: Haltung -> {WorkerCombatStanceRuntime.DisplayName(buttonStance)}.");
            }
            GUI.enabled = true;
        }

        private void DrawGuardBehaviorButton(Character worker, ZDO zdo, GuardBehaviorMode buttonMode, string label, GuardBehaviorMode current, bool writable)
        {
            bool active = current == buttonMode;
            GUI.enabled = writable && !active;
            if (GUILayout.Button((active ? "AKTIV • " : string.Empty) + label, _smartPopupButtonStyle))
            {
                GuardBehaviorRuntime.Set(worker, zdo, buttonMode);
                SetStatus($"{SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name)}: Guard-Verhalten -> {GuardBehaviorRuntime.DisplayName(buttonMode)}.");
            }
            GUI.enabled = true;
        }

        private void AssignWorkerFromMenu(Character worker, ZNetView nview, ZDO zdo, WorkAssignmentOption option)
        {
            if (worker == null || nview == null || zdo == null || !nview.IsOwner()) return;

            if (WorkerZdo.HasCargo(zdo))
            {
                SetStatus("Jobwechsel blockiert: Der Arbeiter traegt noch Ladung. Erst abliefern lassen.");
                return;
            }

            if (!WorkerWorkAreaRules.TryValidateHomeForArea(zdo, option.Center, option.Radius, out string homeReason))
            {
                SetStatus($"Jobwechsel blockiert: {homeReason}. Schlafplatz muss innerhalb der gewaehlten Workzone liegen.");
                return;
            }

            WorkerZdo.AssignWorkArea(zdo, option.Id, option.DisplayName, option.Order, option.Center, option.Radius);
            WorkerZdo.ClearTarget(zdo);
            MonsterAI ai = worker.GetComponent<MonsterAI>();
            if (ai != null && ai.GetFollowTarget() != null)
                WorkerNavigationRuntime.SetNativeFollowTarget(ai, null);

            WorkerState currentState = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (currentState != WorkerState.ReturnHomeForRest && currentState != WorkerState.Resting && currentState != WorkerState.Recover)
            {
                WorkerRole assignedRole = (WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
                zdo.Set(WorkerZdo.State, assignedRole == WorkerRole.Stonemason ? (int)WorkerState.FindJob : (int)WorkerState.MoveToWorkArea);
            }

            string zone = string.IsNullOrWhiteSpace(option.DisplayName) ? "Workzone" : option.DisplayName;
            SetStatus($"{SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name)} -> {WorkOrderCatalog.DisplayName(option.Order)} / {zone}.");
        }

        private void ReleaseWorkerWorkAssignmentFromMenu(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return;
            if (WorkerZdo.HasCargo(zdo))
            {
                SetStatus("Zuweisung bleibt aktiv: Der Arbeiter traegt noch Ladung. Erst abliefern lassen.");
                return;
            }

            WorkerZdo.ClearWorkArea(zdo);
            WorkerZdo.ClearTarget(zdo);
            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state != WorkerState.Follow && state != WorkerState.ReturnHomeForRest && state != WorkerState.Resting && state != WorkerState.Recover)
                zdo.Set(WorkerZdo.State, (int)WorkerState.ReturnHome);
            SetStatus($"{SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name)}: Arbeitszuweisung geloest.");
        }

        private static string GetWorkerRoleDisplayName(WorkerRole role)
        {
            switch (role)
            {
                case WorkerRole.Lumberjack: return "Holzfaeller";
                case WorkerRole.Miner: return "Bergarbeiter";
                case WorkerRole.Fisher: return "Fischer";
                case WorkerRole.Gardener: return "Gaertner / Foerster";
                case WorkerRole.Gatherer: return "Sammler / Ernter";
                case WorkerRole.Guard: return "Wache";
                case WorkerRole.Hauler: return "Traeger / Logistik";
                case WorkerRole.Stonemason: return "Steinmetz";
                case WorkerRole.Firekeeper: return "Feuerhueter";
                case WorkerRole.Baker: return "Koch / Produktion";
                case WorkerRole.Butcher: return "Metzger / Tierpfleger";
                case WorkerRole.Smith: return "Schmied / Metall";
                default: return "Arbeiter";
            }
        }


        private void DrawButcherPopulationRules(Character worker, ZDO zdo, bool writable)
        {
            System.Collections.Generic.List<string> species = LivestockButcherRuntime.GetRuleSpecies(worker, zdo);
            GUILayout.Label(LivestockButcherRuntime.GetScanDebug(zdo), _smartPopupLabelStyle);
            GUILayout.Label(LivestockPenRuntime.GetDebug(zdo), _smartPopupLabelStyle);
            GUILayout.Label(LivestockFeedingRuntime.GetDebug(zdo), _smartPopupLabelStyle);
            GUILayout.Label("Min/Max gehoeren jetzt zum Futtertrog/Gehege. Mehrere Tierpfleger am selben Gehege teilen dieselben Regeln.", _smartPopupLabelStyle);
            if (species.Count == 0)
            {
                GUILayout.Label("Noch keine gezähmten Tierarten in der Workzone gefunden.", _smartPopupLabelStyle);
                return;
            }

            for (int i = 0; i < species.Count; i++)
            {
                string animal = species[i];
                int min = LivestockButcherRuntime.GetMin(zdo, animal);
                int max = LivestockButcherRuntime.GetMax(zdo, animal);
                int total = LivestockButcherRuntime.CountTamedInAssignedArea(zdo, animal, out int adults, out int young);
                string stars = LivestockButcherRuntime.GetAdultStarSummary(zdo, animal);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{animal}: {total} gesamt ({adults} erwachsen / {young} jung) | Sterne: {stars}", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                GUI.enabled = writable;
                if (GUILayout.Button("Min-", _smartPopupButtonStyle, GUILayout.Width(52f))) LivestockButcherRuntime.SetRule(zdo, animal, Mathf.Max(0, min - 1), max);
                GUILayout.Label($"Min {min}", _smartPopupLabelStyle, GUILayout.Width(54f));
                if (GUILayout.Button("Min+", _smartPopupButtonStyle, GUILayout.Width(52f))) LivestockButcherRuntime.SetRule(zdo, animal, min + 1, Mathf.Max(max, min + 1));
                if (GUILayout.Button("Max-", _smartPopupButtonStyle, GUILayout.Width(52f))) LivestockButcherRuntime.SetRule(zdo, animal, min, Mathf.Max(min, max - 1));
                GUILayout.Label($"Max {max}", _smartPopupLabelStyle, GUILayout.Width(58f));
                if (GUILayout.Button("Max+", _smartPopupButtonStyle, GUILayout.Width(52f))) LivestockButcherRuntime.SetRule(zdo, animal, min, max + 1);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.Label("Schlachtlogik: nur ueber Max, niemals Jungtiere; niedrigste Sternstufe zuerst. Min wird niemals unterschritten.", _smartPopupLabelStyle);
        }

        internal bool TryOpenOutpostManager(Humanoid user, GameObject tableObject)
        {
            if (!_enabled.Value || tableObject == null) return false;
            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer) return false;

            CloseWorkFlagMenu();
            CloseWorkerContainerMenu();
            CloseWorkerMenu();
            _outpostManagerOpen = true;
            _outpostManagerAnchorPosition = CartographyOutpostRuntime.ResolveTablePosition(tableObject);
            _outpostManagerScroll = Vector2.zero;
            _nextOutpostManagerRefresh = 0f;
            RefreshOutpostManagerData(true);
            CenterOutpostManagerWindow();
            CaptureMenuCursor();
            SetStatus("Kartographietisch: Workers-Aussenpostenverwaltung geoeffnet.");
            return true;
        }

        private void RefreshOutpostManagerData(bool force = false)
        {
            if (!_outpostManagerOpen && !force) return;
            if (!force && Time.unscaledTime < _nextOutpostManagerRefresh) return;
            _nextOutpostManagerRefresh = Time.unscaledTime + 1f;

            Player player = Player.m_localPlayer;
            _outpostManagerFlags.Clear();
            _outpostManagerWorkers.Clear();
            _outpostManagerPersistentWorkers.Clear();
            _outpostManagerLoadedWorkerById.Clear();
            if (player == null) return;

            long ownerId = player.GetPlayerID();
            WorkFlagRegistry.CollectOwnedPersistentSnapshots(ownerId, _workFlagDefaultRadius.Value, player.transform.position, _outpostManagerFlags, force);

            WorkerFastLookupRuntime.CopyKnownWorkers(_loadedWorkerScratch);
            for (int i = 0; i < _loadedWorkerScratch.Count; i++)
            {
                Character character = _loadedWorkerScratch[i];
                if (character == null || character.IsDead()) continue;
                if (!WorkerFastLookupRuntime.TryGetWorker(character, false, out _, out ZDO zdo)) continue;
                if (zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != ownerId) continue;
                _outpostManagerWorkers.Add(character);
                _outpostManagerLoadedWorkerById[zdo.m_uid] = character;
            }

            BackgroundWorkerScheduler.ForEachRegisteredWorker(workerZdo =>
            {
                if (!WorkerZdo.IsWorker(workerZdo)) return;
                if (workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != ownerId) return;
                _outpostManagerPersistentWorkers.Add(workerZdo);
            });
        }

        private void DrawOutpostManagerWindow(int windowId)
        {
            EnsureSmartPopupStyles();
            RefreshOutpostManagerData();

            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            GUILayout.Label("AUSSENPOSTEN", _smartPopupHeadingStyle, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("X", _smartPopupButtonStyle, GUILayout.Width(36f), GUILayout.Height(28f)))
            {
                CloseOutpostManager();
                GUIUtility.ExitGUI();
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("Workers-Verwaltung am Kartographietisch", _smartPopupHeadingStyle);
            GUILayout.Label("Persistenter Welt-Index deiner Workers-Arbeitsfahnen. E am Kartographietisch bleibt Vanilla; Shift+E oeffnet diese Verwaltung.", _smartPopupLabelStyle);
            int loadedOutposts = 0;
            for (int i = 0; i < _outpostManagerFlags.Count; i++) if (_outpostManagerFlags[i].Flag != null) loadedOutposts++;
            bool cartographyServer = false;
            try { cartographyServer = ZNet.instance != null && ZNet.instance.IsServer(); } catch { }
            string cartographyMode = cartographyServer ? "HOST/SERVER" : "CLIENT";
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Aussenposten weltweit: {_outpostManagerFlags.Count}  •  aktuell geladen: {loadedOutposts}  •  eigene Worker: {_outpostManagerPersistentWorkers.Count} ({_outpostManagerWorkers.Count} geladen)  •  Modus: {cartographyMode}", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Aktualisieren", _smartPopupButtonStyle, GUILayout.Width(130f)))
            {
                _nextOutpostManagerRefresh = 0f;
                RefreshOutpostManagerData(true);
            }
            GUILayout.EndHorizontal();
            if (WorkFlagRegistry.LastPersistentWorldIndexRejectedCount > 0)
                GUILayout.Label($"Welt-Index: {WorkFlagRegistry.LastPersistentWorldIndexRejectedCount} veraltete/ungueltige Flag-ZDO-Eintraege wurden ausgefiltert.", _smartPopupLabelStyle);
            if (!cartographyServer)
                GUILayout.Label("CLIENT: Entfernte Aussenposten werden per Server-RPC bearbeitet. Falls der Server die Workers-Version nicht hat, bleibt der betroffene Eintrag read-only.", _smartPopupLabelStyle);

            GUILayout.Space(6f);
            _outpostManagerScroll = GUILayout.BeginScrollView(_outpostManagerScroll, GUILayout.ExpandHeight(true));
            if (_outpostManagerFlags.Count == 0)
            {
                GUILayout.Label("Keine eigene persistente Workers-Arbeitsfahne im Welt-Index gefunden.", _smartPopupLabelStyle);
            }
            else
            {
                for (int i = 0; i < _outpostManagerFlags.Count; i++)
                {
                    WorkAreaSnapshot snapshot = _outpostManagerFlags[i];
                    DrawOutpostCard(snapshot);
                    GUILayout.Space(8f);
                }
            }
            GUILayout.EndScrollView();

            GUILayout.Space(6f);
            GUILayout.Label("Welt-Index: ungeladene Aussenposten werden aus persistenten ZDO-Daten gelesen. Name/Radius koennen remote gespeichert werden; Multiplayer-Clients schicken die Aenderung server-autoritativ per RPC. Arbeitsbereich anzeigen oeffnet die Karte und zeichnet den kompletten Workzone-Umkreis clientseitig.", _smartPopupLabelStyle);
            if (GUILayout.Button("Schliessen", _smartPopupButtonStyle))
            {
                CloseOutpostManager();
                GUIUtility.ExitGUI();
            }
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 34f));
        }

        private void DrawOutpostCard(WorkAreaSnapshot snapshot)
        {
            WorkFlagInteractable flag = snapshot.Flag;
            ZNetView flagView = flag != null ? flag.GetNetworkView() : null;
            ZDO flagZdo = flagView != null && flagView.IsValid() ? flagView.GetZDO() : null;
            if (flagZdo == null && !WorkFlagRegistry.TryGetPersistentState(snapshot.Id, out flagZdo)) return;

            bool loaded = flag != null && flagView != null && flagView.IsValid();
            string name = string.IsNullOrWhiteSpace(snapshot.DisplayName) ? "Workzone" : snapshot.DisplayName;
            int workerCount = 0;
            int loadedWorkerCount = 0;
            int homeProblems = 0;
            int provisioners = 0;
            int supplyOkay = 0;
            int supplyRefill = 0;
            int supplyCritical = 0;
            int supplyDisabled = 0;

            for (int i = 0; i < _outpostManagerPersistentWorkers.Count; i++)
            {
                ZDO workerZdo = _outpostManagerPersistentWorkers[i];
                if (!WorkerZdo.IsWorker(workerZdo)) continue;
                if (!string.Equals(WorkerZdo.ReadWorkAreaId(workerZdo), snapshot.Id, StringComparison.Ordinal)) continue;

                workerCount++;
                if (_outpostManagerLoadedWorkerById.ContainsKey(workerZdo.m_uid)) loadedWorkerCount++;
                if (!WorkerWorkAreaRules.TryValidateHomeForArea(workerZdo, snapshot.Center, snapshot.Radius, out _)) homeProblems++;
                if (IsOutpostProvisioner(workerZdo)) provisioners++;

                BuildOutpostSupplySummary(workerZdo, out OutpostSupplyUiState supplyState);
                switch (supplyState)
                {
                    case OutpostSupplyUiState.Okay: supplyOkay++; break;
                    case OutpostSupplyUiState.Refill: supplyRefill++; break;
                    case OutpostSupplyUiState.Critical: supplyCritical++; break;
                    case OutpostSupplyUiState.Disabled: supplyDisabled++; break;
                }
            }

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            string loadState = loaded ? "GELADEN" : "UNGELADEN";
            GUILayout.Label($"{name}  •  {snapshot.Distance:0} m  •  {loadState}", _smartPopupHeadingStyle, GUILayout.ExpandWidth(true));
            bool oldEnabled = GUI.enabled;
            if (GUILayout.Button("Arbeitsbereich anzeigen", _smartPopupButtonStyle, GUILayout.Width(175f)))
            {
                Vector3 mapCenter = snapshot.Center;
                float mapRadius = snapshot.Radius;
                string mapName = name;
                SuspendOutpostManagerForMapPreview();
                bool shown = _mapMarkers != null && _mapMarkers.ShowOutpostArea(mapCenter, mapRadius, mapName);
                if (!shown)
                    ResumeOutpostManagerFromMapPreview();
                SetStatus(shown
                    ? $"Kartographietisch: Arbeitsbereich von {mapName} auf der Karte angezeigt (Radius {mapRadius:0} m)."
                    : "Kartographietisch: Karte konnte nicht geoeffnet werden.");
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                GUIUtility.ExitGUI();
                return;
            }
            GUI.enabled = loaded && flagView.IsOwner();
            if (GUILayout.Button(loaded ? "Fahne verwalten" : "Nicht geladen", _smartPopupButtonStyle, GUILayout.Width(150f)))
            {
                GUI.enabled = oldEnabled;
                OpenWorkFlagFromOutpostManager(flag);
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                GUIUtility.ExitGUI();
                return;
            }
            GUI.enabled = oldEnabled;
            GUILayout.EndHorizontal();

            GUILayout.Label("Jobs: pro Worker an der Ruhestaette", _smartPopupLabelStyle);
            GUILayout.Label($"Radius: {snapshot.Radius:0} m  •  Worker: {workerCount} ({loadedWorkerCount} geladen)  •  Versorger: {provisioners}  •  Schlafplatz-Probleme: {homeProblems}", _smartPopupLabelStyle);
            if (workerCount > 0)
            {
                GUIStyle supplyStyle = supplyCritical > 0 ? _smartPopupCriticalStyle : (supplyRefill > 0 ? _smartPopupWarnStyle : _smartPopupGoodStyle);
                string disabledText = supplyDisabled > 0 ? $"  •  Auto AUS: {supplyDisabled}" : string.Empty;
                GUILayout.Label($"Versorgung: {supplyOkay} OK  •  {supplyRefill} nachfuellen  •  {supplyCritical} kritisch{disabledText}", supplyStyle);
                if (provisioners <= 0 && (supplyRefill > 0 || supplyCritical > 0))
                    GUILayout.Label("WARNUNG: In dieser Workzone ist kein Versorger aktiv. Lokale Vorräte erreichen die Ruhestaetten daher nicht automatisch.", _smartPopupWarnStyle);
            }
            if (!loaded)
                GUILayout.Label("Welt-ZDO gefunden: Aussenposten ist aktuell nicht geladen. Worker- und Versorgungsstatus darunter stammen aus dem persistenten Background-Register.", _smartPopupLabelStyle);
            else if (!flagView.IsOwner())
                GUILayout.Label("Fahne ist aktuell nicht lokal network-owned.", _smartPopupLabelStyle);

            DrawWorkzoneEconomy(snapshot.Id, snapshot.Center, snapshot.Radius);

            DrawOutpostBasicEditor(snapshot, flagView, flagZdo, loaded);

            if (workerCount > 0)
            {
                GUILayout.Space(4f);
                GUILayout.Label("Worker / Versorgung", _smartPopupHeadingStyle);
                for (int i = 0; i < _outpostManagerPersistentWorkers.Count; i++)
                {
                    ZDO workerZdo = _outpostManagerPersistentWorkers[i];
                    if (!WorkerZdo.IsWorker(workerZdo) || !string.Equals(WorkerZdo.ReadWorkAreaId(workerZdo), snapshot.Id, StringComparison.Ordinal)) continue;

                    _outpostManagerLoadedWorkerById.TryGetValue(workerZdo.m_uid, out Character worker);
                    ZNetView workerView = worker != null ? worker.GetComponent<ZNetView>() : null;
                    bool workerLoaded = worker != null && !worker.IsDead();
                    WorkerRole role = (WorkerRole)workerZdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
                    WorkerState state = (WorkerState)workerZdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
                    WorkOrder order = (WorkOrder)workerZdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None);
                    string species = SpeciesRoleRegistry.NormalizePrefabName(workerZdo.GetString(WorkerZdo.SpeciesKey, string.Empty));
                    if (string.IsNullOrWhiteSpace(species) && worker != null) species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
                    if (string.IsNullOrWhiteSpace(species)) species = "Worker";
                    string cargo = WorkerZdo.TryReadCargo(workerZdo, out string cargoPrefab, out int cargoAmount) ? $"{cargoPrefab} x{cargoAmount}" : "leer";
                    string home = WorkerWorkAreaRules.DescribeHomeStatus(workerZdo, snapshot.Center, snapshot.Radius);
                    string supply = BuildOutpostSupplySummary(workerZdo, out OutpostSupplyUiState supplyState);
                    string loadLabel = workerLoaded ? "GELADEN" : "BACKGROUND";
                    string tier = WorkerTierCatalog.DisplayTier(WorkerTierCatalog.GetWorkerTier(species));

                    GUIStyle lineStyle = supplyState == OutpostSupplyUiState.Critical
                        ? _smartPopupCriticalStyle
                        : supplyState == OutpostSupplyUiState.Refill
                            ? _smartPopupWarnStyle
                            : _smartPopupLabelStyle;

                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"• {species} — {tier} — {GetWorkerRoleDisplayName(role)} — {WorkOrderCatalog.DisplayName(order)} — {state} — {loadLabel}\n  Cargo: {cargo}  •  {supply}\n  {home}", lineStyle, GUILayout.ExpandWidth(true));
                    bool canRelease = workerLoaded && workerView != null && workerView.IsValid() && workerView.IsOwner();
                    GUI.enabled = canRelease;
                    if (GUILayout.Button("Zuordnung loesen", _smartPopupButtonStyle, GUILayout.Width(150f)))
                    {
                        ReleaseWorkerFromOpenFlag(worker, workerZdo);
                        _nextOutpostManagerRefresh = 0f;
                        GUI.enabled = oldEnabled;
                        GUILayout.EndHorizontal();
                        GUILayout.EndVertical();
                        GUIUtility.ExitGUI();
                        return;
                    }
                    GUI.enabled = oldEnabled;
                    GUILayout.EndHorizontal();

                    if (!workerLoaded)
                    {
                        string bg = workerZdo.GetString("vw_bg_status", string.Empty);
                        if (!string.IsNullOrWhiteSpace(bg))
                            GUILayout.Label("  Background: " + bg, _smartPopupLabelStyle);
                    }

                    DrawOutpostWorkerRemoteControls(snapshot.Id, workerZdo, role, species);
                }
            }
            GUILayout.EndVertical();
        }

        private void DrawOutpostWorkerRemoteControls(string flagId, ZDO workerZdo, WorkerRole role, string species)
        {
            if (workerZdo == null || string.IsNullOrEmpty(flagId)) return;
            Player player = Player.m_localPlayer;
            long playerId = player != null ? player.GetPlayerID() : 0L;
            bool canEdit = playerId != 0L && CartographyRemoteRuntime.CanRequestRemoteEdit();

            WorkerTaskFlags available = WorkerJobProfileRuntime.GetAvailableTasks(workerZdo, role);
            if (available != WorkerTaskFlags.None)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("  Jobs:", _smartPopupLabelStyle, GUILayout.Width(54f));
                for (int i = 0; i < OutpostRemoteTaskOrder.Length; i++)
                {
                    WorkerTaskFlags task = OutpostRemoteTaskOrder[i];
                    if ((available & task) == 0) continue;
                    int level = WorkerJobProfileRuntime.HasTask(workerZdo, role, task)
                        ? WorkerJobProfileRuntime.GetPriority(workerZdo, role, task)
                        : 0;
                    if (DrawOutpostRemoteTaskButton(task, level, canEdit, out int nextLevel))
                    {
                        bool requested = CartographyRemoteRuntime.RequestWorkerTaskLevel(flagId, workerZdo, task, nextLevel, playerId);
                        if (!requested)
                            SetStatus("Kartographietisch: Worker-Job konnte nicht an den Server gesendet werden.");
                        GUIUtility.ExitGUI();
                    }
                }
                GUILayout.EndHorizontal();
            }

            if (WorkerSustenanceRuntime.TryGetRequirement(workerZdo, out _, out string supplyDisplay, out _))
            {
                int target = WorkerSustenanceRuntime.GetTargetStock(workerZdo);
                int stock = WorkerSustenanceRuntime.GetStock(workerZdo);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"  Versorgung: {supplyDisplay} {stock}/{(target <= 0 ? 0 : target)}", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                bool previousEnabled = GUI.enabled;
                GUI.enabled = previousEnabled && canEdit;
                if (GUILayout.Button("-5", _smartPopupButtonStyle, GUILayout.Width(42f)))
                {
                    CartographyRemoteRuntime.RequestWorkerSupplyTarget(flagId, workerZdo, Math.Max(0, target - 5), playerId);
                    GUI.enabled = previousEnabled;
                    GUILayout.EndHorizontal();
                    GUIUtility.ExitGUI();
                    return;
                }
                if (GUILayout.Button("-1", _smartPopupButtonStyle, GUILayout.Width(42f)))
                {
                    CartographyRemoteRuntime.RequestWorkerSupplyTarget(flagId, workerZdo, Math.Max(0, target - 1), playerId);
                    GUI.enabled = previousEnabled;
                    GUILayout.EndHorizontal();
                    GUIUtility.ExitGUI();
                    return;
                }
                GUILayout.Label(target <= 0 ? "Auto AUS" : $"Ziel {target}", _smartPopupLabelStyle, GUILayout.Width(72f));
                if (GUILayout.Button("+1", _smartPopupButtonStyle, GUILayout.Width(42f)))
                {
                    CartographyRemoteRuntime.RequestWorkerSupplyTarget(flagId, workerZdo, Math.Min(999, target + 1), playerId);
                    GUI.enabled = previousEnabled;
                    GUILayout.EndHorizontal();
                    GUIUtility.ExitGUI();
                    return;
                }
                if (GUILayout.Button("+5", _smartPopupButtonStyle, GUILayout.Width(42f)))
                {
                    CartographyRemoteRuntime.RequestWorkerSupplyTarget(flagId, workerZdo, Math.Min(999, target + 5), playerId);
                    GUI.enabled = previousEnabled;
                    GUILayout.EndHorizontal();
                    GUIUtility.ExitGUI();
                    return;
                }
                GUI.enabled = previousEnabled;
                GUILayout.EndHorizontal();
            }

            if (!canEdit)
                GUILayout.Label("  Fernsteuerung aktuell nur lesbar: kein Server-RPC / kein lokaler Spieler.", _smartPopupLabelStyle);
        }

        private bool DrawOutpostRemoteTaskButton(WorkerTaskFlags task, int level, bool writable, out int nextLevel)
        {
            nextLevel = level;
            string label = GetWorkerTaskPriorityDisplayName(task);
            Rect rect = GUILayoutUtility.GetRect(new GUIContent($"{label}: {(level <= 0 ? "Aus" : level.ToString())}"), _smartPopupButtonStyle,
                GUILayout.MinWidth(105f), GUILayout.Height(24f));
            Event evt = Event.current;
            bool clicked = writable && rect.Contains(evt.mousePosition) && evt.type == EventType.MouseDown && (evt.button == 0 || evt.button == 1);
            if (clicked)
            {
                if (evt.button == 0)
                {
                    if (level == 0) nextLevel = 4;
                    else if (level > 1) nextLevel = level - 1;
                }
                else
                {
                    if (level >= 1 && level < 4) nextLevel = level + 1;
                    else if (level == 4) nextLevel = 0;
                }
                evt.Use();
            }

            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && writable;
            GUI.Box(rect, new GUIContent($"{label}: {(level <= 0 ? "Aus" : "P" + level)}",
                "Linksklick: Prioritaet erhoehen | Rechtsklick: Prioritaet verringern | 1 = hoch, 4 = niedrig, Aus = deaktiviert"), _smartPopupButtonStyle);
            GUI.enabled = previousEnabled;
            return clicked && nextLevel != level;
        }

        private enum OutpostSupplyUiState
        {
            None = 0,
            Okay = 1,
            Refill = 2,
            Critical = 3,
            Disabled = 4
        }

        private static bool IsOutpostProvisioner(ZDO workerZdo)
        {
            if (!WorkerZdo.IsWorker(workerZdo)) return false;
            WorkerRole role = (WorkerRole)workerZdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
            if (role != WorkerRole.Hauler) return false;

            string species = SpeciesRoleRegistry.NormalizePrefabName(workerZdo.GetString(WorkerZdo.SpeciesKey, string.Empty));
            if (string.Equals(species, "Lox", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(species, "Drake", StringComparison.OrdinalIgnoreCase))
                return false;

            if (workerZdo.GetBool(WorkerJobProfileRuntime.TaskMaskSetKey, false))
            {
                WorkerTaskFlags mask = (WorkerTaskFlags)workerZdo.GetInt(WorkerJobProfileRuntime.TaskMaskKey, 0);
                return (mask & WorkerTaskFlags.Provision) != 0;
            }

            WorkOrder current = (WorkOrder)workerZdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None);
            // Legacy no-mask Greydwarfs defaulted to all compatible local logistics modules. A pure
            // Haul assignment is the only legacy state that explicitly meant transport only.
            return current != WorkOrder.Haul;
        }

        private static string BuildOutpostSupplySummary(ZDO workerZdo, out OutpostSupplyUiState state)
        {
            state = OutpostSupplyUiState.None;
            if (!WorkerZdo.IsWorker(workerZdo) ||
                !WorkerSustenanceRuntime.TryGetRequirement(workerZdo, out string prefab, out string display, out string needWord))
                return "Versorgung: nicht erforderlich";

            ZDO home = WorkerSustenanceRuntime.ResolveHomeZdo(workerZdo);
            if (home == null)
            {
                state = OutpostSupplyUiState.Critical;
                return $"{needWord}: {display} • keine Ruhestaette aufloesbar";
            }

            int stock = WorkerSustenanceRuntime.GetHomeStock(home);
            int target = Math.Max(0, home.GetInt(WorkerSustenanceRuntime.HomeSupplyTargetKey, WorkerSustenanceRuntime.DefaultTargetStock));
            long now = DateTime.UtcNow.Ticks;
            long nextMeal = workerZdo.GetLong(WorkerSustenanceRuntime.NextMealTicksKey, 0L);
            bool hungry = nextMeal > 0L && now >= nextMeal;

            if (hungry && stock <= 0)
            {
                state = OutpostSupplyUiState.Critical;
                return $"KRITISCH: wartet auf {display} • {stock}/{target}";
            }

            if (target <= 0)
            {
                state = OutpostSupplyUiState.Disabled;
                return $"{display}: {stock} • automatische Versorgung AUS";
            }

            int missing = Math.Max(0, target - stock);
            double untilMealMinutes = nextMeal > now ? TimeSpan.FromTicks(nextMeal - now).TotalMinutes : 0.0;
            string nextText = nextMeal <= 0L
                ? "Mahlzeit noch nicht initialisiert"
                : hungry
                    ? "Mahlzeit jetzt faellig"
                    : $"naechste Mahlzeit ~{Math.Max(0.0, untilMealMinutes):0} min";

            if (stock <= 0 || missing > 0)
            {
                state = OutpostSupplyUiState.Refill;
                return $"NACHFUELLEN: {display} {stock}/{target} • fehlen {missing} • ~{stock} Tage Reserve • {nextText}";
            }

            state = OutpostSupplyUiState.Okay;
            return $"OK: {display} {stock}/{target} • ~{stock} Tage Reserve • {nextText}";
        }

        private void DrawOutpostBasicEditor(WorkAreaSnapshot snapshot, ZNetView flagView, ZDO flagZdo, bool loaded)
        {
            if (flagZdo == null || string.IsNullOrEmpty(snapshot.Id)) return;

            if (!_outpostManagerNameEdits.TryGetValue(snapshot.Id, out string nameDraft))
            {
                nameDraft = string.IsNullOrWhiteSpace(snapshot.DisplayName) ? "Workzone" : snapshot.DisplayName;
                _outpostManagerNameEdits[snapshot.Id] = nameDraft;
            }
            if (!_outpostManagerRadiusEdits.TryGetValue(snapshot.Id, out float radiusDraft))
            {
                radiusDraft = Mathf.Clamp(snapshot.Radius, 10f, 80f);
                _outpostManagerRadiusEdits[snapshot.Id] = radiusDraft;
            }

            bool canEdit = CanEditOutpostBasics(flagView, flagZdo, loaded, out string editReason);
            GUILayout.Space(5f);
            GUILayout.Label("Fernverwaltung", _smartPopupHeadingStyle);

            bool previousEnabled = GUI.enabled;
            GUI.enabled = canEdit;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name:", _smartPopupLabelStyle, GUILayout.Width(70f));
            string nextName = GUILayout.TextField(nameDraft ?? string.Empty, 32, GUILayout.ExpandWidth(true));
            if (!string.Equals(nextName, nameDraft, StringComparison.Ordinal))
                _outpostManagerNameEdits[snapshot.Id] = nextName;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Radius: {radiusDraft:0} m", _smartPopupLabelStyle, GUILayout.Width(110f));
            float nextRadius = Mathf.Round(GUILayout.HorizontalSlider(radiusDraft, 10f, 80f));
            if (Math.Abs(nextRadius - radiusDraft) >= 0.5f)
            {
                radiusDraft = nextRadius;
                _outpostManagerRadiusEdits[snapshot.Id] = radiusDraft;
            }
            GUILayout.EndHorizontal();

            bool nameChanged = !string.Equals((_outpostManagerNameEdits[snapshot.Id] ?? string.Empty).Trim(), (snapshot.DisplayName ?? string.Empty).Trim(), StringComparison.Ordinal);
            bool radiusChanged = Math.Abs(_outpostManagerRadiusEdits[snapshot.Id] - snapshot.Radius) >= 0.5f;
            GUI.enabled = canEdit && (nameChanged || radiusChanged);
            if (GUILayout.Button("Name / Radius speichern", _smartPopupButtonStyle))
            {
                string requestedName = _outpostManagerNameEdits[snapshot.Id] ?? string.Empty;
                float requestedRadius = _outpostManagerRadiusEdits[snapshot.Id];
                bool isServer = false;
                try { isServer = ZNet.instance != null && ZNet.instance.IsServer(); } catch { }

                bool canWriteDirect = isServer || (loaded && flagView != null && flagView.IsValid() && flagView.IsOwner());
                if (canWriteDirect)
                {
                    if (!string.IsNullOrWhiteSpace(requestedName))
                        WorkFlagZdo.SetDisplayName(flagZdo, requestedName);
                    WorkFlagZdo.SetRadius(flagZdo, requestedRadius);
                    WorkFlagRegistry.RememberPersistent(flagZdo);

                    string savedName = WorkFlagZdo.ReadDisplayName(flagZdo);
                    float savedRadius = WorkFlagZdo.ReadRadius(flagZdo, _workFlagDefaultRadius.Value);
                    _outpostManagerNameEdits[snapshot.Id] = savedName;
                    _outpostManagerRadiusEdits[snapshot.Id] = savedRadius;
                    UpdateLoadedWorkerWorkzoneNames(snapshot.Id, savedName);
                    UpdateLoadedWorkerWorkzoneRadius(snapshot.Id, savedRadius);
                    int persistentWorkersUpdated = isServer
                        ? WorkFlagRegistry.UpdatePersistentAssignedWorkerAreaMetadata(snapshot.Id, savedName, snapshot.Center, savedRadius)
                        : 0;
                    _nextOutpostManagerRefresh = 0f;
                    SetStatus($"Kartographietisch: {savedName} gespeichert, Radius {savedRadius:0} m. Persistente Worker aktualisiert: {persistentWorkersUpdated}.");
                }
                else
                {
                    long playerId = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;
                    if (CartographyRemoteRuntime.RequestEdit(snapshot.Id, requestedName, requestedRadius, playerId))
                        SetStatus($"Kartographietisch: Aenderung fuer {snapshot.DisplayName} an Server gesendet...");
                    else
                        SetStatus("Kartographietisch: Server-Fernbearbeitung ist nicht verfuegbar.");
                }
            }
            GUI.enabled = previousEnabled;

            if (!canEdit)
                GUILayout.Label(editReason, _smartPopupLabelStyle);
            else if (!loaded)
                GUILayout.Label("Fernbearbeitung aktiv: Name/Radius werden bei Clients per Server-RPC gespeichert; die Zone muss nicht geladen sein.", _smartPopupLabelStyle);
        }

        private static bool CanEditOutpostBasics(ZNetView flagView, ZDO flagZdo, bool loaded, out string reason)
        {
            reason = string.Empty;
            Player localPlayer = Player.m_localPlayer;
            if (flagZdo == null || localPlayer == null)
            {
                reason = "Nur Lesen: kein gueltiger lokaler Spieler/ZDO-Zustand.";
                return false;
            }

            long assignedOwner = WorkFlagZdo.ReadOwner(flagZdo);
            long localPlayerId = localPlayer.GetPlayerID();
            if (assignedOwner != 0L && localPlayerId != 0L && assignedOwner != localPlayerId)
            {
                reason = "Nur Lesen: dieser Aussenposten gehoert einem anderen Spieler.";
                return false;
            }

            try
            {
                if (ZNet.instance != null && ZNet.instance.IsServer()) return true;
            }
            catch { }

            if (loaded && flagView != null && flagView.IsValid() && flagView.IsOwner()) return true;
            if (CartographyRemoteRuntime.CanRequestRemoteEdit()) return true;

            reason = loaded
                ? "Nur Lesen: Fahne ist nicht lokal network-owned und der Server-RPC ist nicht verfuegbar."
                : "Nur Lesen: Server-Fernbearbeitung ist aktuell nicht verfuegbar.";
            return false;
        }

        internal void HandleRemoteOutpostEditResult(bool success, string flagId, string savedName, float savedRadius, string message)
        {
            if (success && !string.IsNullOrEmpty(flagId))
            {
                _outpostManagerNameEdits[flagId] = savedName ?? string.Empty;
                _outpostManagerRadiusEdits[flagId] = savedRadius;
                UpdateLoadedWorkerWorkzoneNames(flagId, savedName ?? string.Empty);
                UpdateLoadedWorkerWorkzoneRadius(flagId, savedRadius);
                _nextOutpostManagerRefresh = 0f;
                if (_outpostManagerOpen) RefreshOutpostManagerData(true);
            }

            SetStatus($"Kartographietisch: {(success ? string.Empty : "FEHLER: ")}{message}");
        }

        internal void HandleRemoteOutpostWorkerEditResult(bool success, string flagId, string workerUid, string message)
        {
            _nextOutpostManagerRefresh = 0f;
            if (_outpostManagerOpen) RefreshOutpostManagerData(true);
            SetStatus($"Kartographietisch: {(success ? string.Empty : "FEHLER: ")}{message}");
        }

        private void OpenWorkFlagFromOutpostManager(WorkFlagInteractable flag)
        {
            if (flag == null || Player.m_localPlayer == null) return;
            CloseOutpostManager();
            if (TryOpenWorkFlagMenu(flag, Player.m_localPlayer) && _openWorkFlag == flag)
            {
                _workFlagOpenedFromOutpostManager = true;
                SetStatus("Kartographietisch: Arbeitsfahne in Fernverwaltung geoeffnet.");
            }
        }

        private void SuspendOutpostManagerForMapPreview()
        {
            _outpostManagerMapPreviewOpen = true;
            _outpostManagerMapPreviewReadyAt = Time.unscaledTime + 0.25f;
            _outpostManagerOpen = false;
            RestoreMenuCursor();
        }

        private void ResumeOutpostManagerFromMapPreview()
        {
            _outpostManagerMapPreviewOpen = false;
            _outpostManagerMapPreviewReadyAt = 0f;
            if (Player.m_localPlayer == null) return;
            if (Vector3.Distance(Player.m_localPlayer.transform.position, _outpostManagerAnchorPosition) > 10f) return;

            _outpostManagerOpen = true;
            _nextOutpostManagerRefresh = 0f;
            RefreshOutpostManagerData(true);
            CaptureMenuCursor();
        }

        private void CloseCartographyMapPreviewAndReturn()
        {
            _mapMarkers?.CloseOutpostAreaMap();
            ResumeOutpostManagerFromMapPreview();
            SetStatus("Kartographietisch: Karte geschlossen. Fernverwaltung wieder geoeffnet.");
        }

        private void DrawCartographyMapCloseButton()
        {
            if (!_outpostManagerMapPreviewOpen || _mapMarkers == null || !_mapMarkers.IsOutpostAreaMapVisible()) return;

            EnsureSmartPopupStyles();
            float width = 190f;
            float height = 42f;
            Rect buttonRect = new Rect(Mathf.Max(16f, (Screen.width - width) * 0.5f), 104f, width, height);
            if (GUI.Button(buttonRect, "Karte schliessen", _smartPopupButtonStyle))
            {
                CloseCartographyMapPreviewAndReturn();
                GUIUtility.ExitGUI();
            }
        }

        private void CloseOutpostManager()
        {
            _outpostManagerMapPreviewOpen = false;
            _outpostManagerMapPreviewReadyAt = 0f;
            _outpostManagerOpen = false;
            _outpostManagerFlags.Clear();
            _outpostManagerWorkers.Clear();
            _outpostManagerPersistentWorkers.Clear();
            _outpostManagerLoadedWorkerById.Clear();
            _outpostManagerNameEdits.Clear();
            _outpostManagerRadiusEdits.Clear();
            _nextOutpostManagerRefresh = 0f;
            if (_openWorkFlag == null && _openProductionStation == null && _openWorkerContainer == null && _openWorkerMenu == null && _openPersistentWorkerMenuZdo == null && _openWorkerInventory == null) RestoreMenuCursor();
        }

        private void CenterOutpostManagerWindow()
        {
            float width = Mathf.Min(960f, Screen.width - 24f);
            float height = Mathf.Min(800f, Screen.height - 24f);
            _outpostManagerRect = new Rect(Mathf.Max(12f, (Screen.width - width) * 0.5f), Mathf.Max(12f, (Screen.height - height) * 0.5f), width, height);
        }

        private void CenterWorkerInventoryWindow()
        {
            float width = Mathf.Min(520f, Screen.width - 24f);
            float height = Mathf.Min(500f, Screen.height - 24f);
            _workerInventoryRect = new Rect(Mathf.Max(12f, (Screen.width - width) * 0.5f), Mathf.Max(12f, (Screen.height - height) * 0.5f), width, height);
        }

        private void CloseWorkerInventory()
        {
            _openWorkerInventory = null;
            _workerInventoryScroll = Vector2.zero;
            if (_openWorkFlag == null && _openProductionStation == null && _openWorkerContainer == null && _openWorkerMenu == null && _openPersistentWorkerMenuZdo == null && !_outpostManagerOpen) RestoreMenuCursor();
        }

        private void CenterWorkerWindow()
        {
            float width = Mathf.Min(620f, Screen.width - 24f);
            float height = Mathf.Min(720f, Screen.height - 24f);
            _workerMenuRect = new Rect(Mathf.Max(12f, (Screen.width - width) * 0.5f), Mathf.Max(12f, (Screen.height - height) * 0.5f), width, height);
        }

        private void CloseWorkerMenu()
        {
            _openWorkerMenu = null;
            _openPersistentWorkerMenuZdo = null;
            _openPersistentWorkerMenuHomeZdo = null;
            _openPersistentWorkerMenuHomePosition = Vector3.zero;
            _openPersistentWorkerMenuHomeLabel = string.Empty;
            _workerWorkOptions.Clear();
            if (_openWorkFlag == null && _openProductionStation == null && _openWorkerContainer == null && _openWorkerInventory == null && !_outpostManagerOpen) RestoreMenuCursor();
        }

        private void CaptureMenuCursor()
        {
            if (!_menuCursorCaptured)
            {
                _savedCursorVisible = Cursor.visible;
                _savedCursorLock = Cursor.lockState;
                _menuCursorCaptured = true;
            }
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        private void RestoreMenuCursor()
        {
            if (!_menuCursorCaptured) return;
            Cursor.visible = _savedCursorVisible;
            Cursor.lockState = _savedCursorLock;
            _menuCursorCaptured = false;
        }

        private void CloseWorkFlagMenu()
        {
            _openWorkFlag = null;
            _workFlagOpenedFromOutpostManager = false;
            _workFlagAssignedWorkers.Clear();
            _workFlagAssignedCount = 0;
            _workFlagAuditSnapshot = null;
            _workFlagAuditAreaId = string.Empty;
            _workFlagAuditWorkerDetailsExpanded = false;
            if (_openProductionStation == null && _openWorkerContainer == null && _openWorkerMenu == null && _openPersistentWorkerMenuZdo == null && _openWorkerInventory == null && !_outpostManagerOpen) RestoreMenuCursor();
        }

        private void CloseProductionStationMenu()
        {
            _openProductionStation = null;
            _productionStationScroll = Vector2.zero;
            if (_openWorkFlag == null && _openWorkerContainer == null && _openWorkerMenu == null && _openPersistentWorkerMenuZdo == null && _openWorkerInventory == null && !_outpostManagerOpen)
                RestoreMenuCursor();
        }

        private void CloseWorkerContainerMenu()
        {
            _openWorkerContainer = null;
            _workerContainerScroll = Vector2.zero;
            _fieldDepotStockSettingsOpen = false;
            _fieldDepotStockScroll = Vector2.zero;
            _containerStockLimitSettingsOpen = false;
            _containerStockLimitScroll = Vector2.zero;
            _containerStockLimitSearch = string.Empty;
            _containerReserveEdits.Clear();
            _containerTriggerEdits.Clear();
            _containerStockMinEdits.Clear();
            _containerStockMaxEdits.Clear();
            if (_openWorkFlag == null && _openProductionStation == null && _openWorkerMenu == null && _openPersistentWorkerMenuZdo == null && _openWorkerInventory == null && _openLivestockFeedSign == null && !_outpostManagerOpen) RestoreMenuCursor();
        }

        internal bool TryOpenLivestockFeedMenu(LivestockFeedSignMarker sign, Humanoid user)
        {
            if (sign == null || user == null || Player.m_localPlayer == null) return false;
            if (user != Player.m_localPlayer) return false;
            if (Vector3.Distance(Player.m_localPlayer.transform.position, sign.transform.position) > 10f) return false;

            _openLivestockFeedSign = sign;
            _livestockFeedSearch = string.Empty;
            _livestockFeedMenuScroll = Vector2.zero;
            _livestockFeedAddScroll = Vector2.zero;
            _livestockNearbyFeedOptions.Clear();
            _livestockNearbyFeedOptions.AddRange(LivestockFeedingRuntime.GetNearbyAcceptedFeedItems(sign, 80f));
            float width = Mathf.Min(680f, Screen.width - 24f);
            float height = Mathf.Min(720f, Screen.height - 24f);
            _livestockFeedMenuRect = new Rect(Mathf.Max(12f, (Screen.width - width) * 0.5f), Mathf.Max(12f, (Screen.height - height) * 0.5f), width, height);
            CaptureMenuCursor();
            SetStatus("Futtertrog / Gehege geoeffnet.");
            return true;
        }

        private void CloseLivestockFeedMenu()
        {
            _openLivestockFeedSign = null;
            _livestockFeedSearch = string.Empty;
            _livestockFeedMenuScroll = Vector2.zero;
            _livestockFeedAddScroll = Vector2.zero;
            _livestockNearbyFeedOptions.Clear();
        }

        private void DrawLivestockFeedMenuWindow(int windowId)
        {
            if (_openLivestockFeedSign == null) { CloseLivestockFeedMenu(); return; }
            GUILayout.Label("Futtertrog — Gehege & Tierfutter", _smartPopupHeadingStyle);
            GUILayout.Label("Der Metzger/Tierpfleger fuellt diesen Futtertrog aus den Lagerkisten bis zu den eingestellten Zielmengen. Derselbe Trog ist der Mittelpunkt des Geheges.", _smartPopupLabelStyle);
            float penRadius = LivestockPenRuntime.GetPenRadius(_openLivestockFeedSign);
            GUILayout.Label($"Gehege-Bereich: {penRadius:0} m Radius — Tiere gelten erst 3 m ausserhalb als ausgebuext.", _smartPopupLabelStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("-5 m", _smartPopupButtonStyle, GUILayout.Width(64f))) ChangeLivestockPenRadius(-5f);
            if (GUILayout.Button("-1 m", _smartPopupButtonStyle, GUILayout.Width(64f))) ChangeLivestockPenRadius(-1f);
            GUILayout.Label($"{penRadius:0} m", _smartPopupLabelStyle, GUILayout.Width(58f));
            if (GUILayout.Button("+1 m", _smartPopupButtonStyle, GUILayout.Width(64f))) ChangeLivestockPenRadius(1f);
            if (GUILayout.Button("+5 m", _smartPopupButtonStyle, GUILayout.Width(64f))) ChangeLivestockPenRadius(5f);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Vogelperspektive / Radius", _smartPopupButtonStyle, GUILayout.Height(30f)))
            {
                BeginLivestockPenBirdView(_openLivestockFeedSign, penRadius);
                GUIUtility.ExitGUI();
            }
            bool penGuideVisible = _livestockPenBirdView != null && _livestockPenBirdView.IsPreviewVisible(_openLivestockFeedSign);
            if (GUILayout.Button(penGuideVisible ? "Gehege-Kreis ausblenden" : "Gehege-Kreis anzeigen", _smartPopupButtonStyle, GUILayout.Height(30f)))
            {
                _livestockPenBirdView?.TogglePreview(_openLivestockFeedSign);
                SetStatus(penGuideVisible ? "Gehege-Kreis ausgeblendet." : "Gehege-Kreis als Bauhilfe eingeblendet.");
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Der eingeblendete Kreis bleibt als Bauhilfe sichtbar, auch nachdem dieses Fenster geschlossen wurde.", _smartPopupLabelStyle);
            GUILayout.Space(8f);

            DrawLivestockPenPopulationRules(_openLivestockFeedSign);
            GUILayout.Space(8f);

            System.Collections.Generic.List<LivestockFeedingRuntime.FeedRuleSnapshot> rules = LivestockFeedingRuntime.GetConfiguredRules(_openLivestockFeedSign);
            GUILayout.Label("Erlaubtes Futter", _smartPopupHeadingStyle);
            GUILayout.Label("Nur Futter mit Haken darf der Tierpfleger/Metzger in Trog oder Spender legen und nur dieses Futter darf automatisch ausgegeben werden.", _smartPopupLabelStyle);
            if (rules.Count == 0)
                GUILayout.Label("Aktuell ist kein Futter freigegeben — die Station bleibt leer.", _smartPopupLabelStyle);

            _livestockFeedMenuScroll = GUILayout.BeginScrollView(_livestockFeedMenuScroll, GUILayout.Height(Mathf.Min(285f, _livestockFeedMenuRect.height * 0.40f)));
            System.Collections.Generic.HashSet<string> shownFeed = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // First show foods the actually loaded tamed animals around this pen can consume.
            for (int i = 0; i < _livestockNearbyFeedOptions.Count; i++)
            {
                LivestockFeedingRuntime.FeedItemOption option = _livestockNearbyFeedOptions[i];
                if (option == null || string.IsNullOrWhiteSpace(option.Prefab) || !shownFeed.Add(option.Prefab)) continue;
                DrawLivestockFeedCheckboxRow(option, rules, false);
            }

            // Keep already configured modded/remote foods visible even when the matching animal is
            // currently outside the loaded 80 m neighborhood.
            for (int i = 0; i < rules.Count; i++)
            {
                LivestockFeedingRuntime.FeedRuleSnapshot rule = rules[i];
                if (rule == null || string.IsNullOrWhiteSpace(rule.Prefab) || !shownFeed.Add(rule.Prefab)) continue;
                DrawLivestockFeedCheckboxRow(new LivestockFeedingRuntime.FeedItemOption { Prefab = rule.Prefab, Label = rule.Label }, rules, false);
            }
            GUILayout.EndScrollView();

            GUILayout.Space(8f);
            GUILayout.Label("Weiteres Futter", _smartPopupHeadingStyle);
            GUILayout.Label("Falls ein Mod-Tier sein Futter gerade nicht in der Naehe meldet, kannst du es hier trotzdem per Haken freigeben.", _smartPopupLabelStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Suche:", _smartPopupLabelStyle, GUILayout.Width(55f));
            _livestockFeedSearch = GUILayout.TextField(_livestockFeedSearch ?? string.Empty, GUILayout.Height(26f));
            GUILayout.EndHorizontal();

            System.Collections.Generic.List<LivestockFeedingRuntime.FeedItemOption> options = LivestockFeedingRuntime.GetSelectableFeedItems(_livestockFeedSearch, 80);
            _livestockFeedAddScroll = GUILayout.BeginScrollView(_livestockFeedAddScroll, GUILayout.Height(Mathf.Min(170f, _livestockFeedMenuRect.height * 0.23f)));
            int shown = 0;
            for (int i = 0; i < options.Count && shown < 40; i++)
            {
                LivestockFeedingRuntime.FeedItemOption option = options[i];
                if (option == null || string.IsNullOrWhiteSpace(option.Prefab)) continue;
                if (shownFeed.Contains(option.Prefab) && string.IsNullOrWhiteSpace(_livestockFeedSearch)) continue;
                DrawLivestockFeedCheckboxRow(option, rules, !string.IsNullOrWhiteSpace(_livestockFeedSearch));
                shown++;
            }
            GUILayout.EndScrollView();

            GUILayout.Space(6f);
            if (GUILayout.Button("Schliessen", _smartPopupButtonStyle, GUILayout.Height(30f))) CloseLivestockFeedMenu();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 30f));
        }

        private void DrawLivestockPenPopulationRules(LivestockFeedSignMarker sign)
        {
            GUILayout.Label("Tierbestand / Metzger", _smartPopupHeadingStyle);
            GUILayout.Label("Min = geschuetzte ERWACHSENE Zuchttiere, Max = Gesamtbestand inkl. Jungtiere. Geschlachtet werden nur erwachsene Tiere ueber Max und immer zuerst die niedrigste Sternstufe.", _smartPopupLabelStyle);

            System.Collections.Generic.List<string> species = LivestockButcherRuntime.GetPenRuleSpecies(sign);
            if (species.Count == 0)
            {
                GUILayout.Label("Noch keine gezaehmten Tiere innerhalb dieses Gehege-Radius gefunden.", _smartPopupLabelStyle);
                return;
            }

            ZNetView penView = sign != null ? sign.GetNetworkView() : null;
            bool writable = penView != null && penView.IsValid() && penView.IsOwner();
            for (int i = 0; i < species.Count; i++)
            {
                string animal = species[i];
                int min = LivestockButcherRuntime.GetPenMin(sign, animal);
                int max = LivestockButcherRuntime.GetPenMax(sign, animal);
                int total = LivestockButcherRuntime.CountTamedInPen(sign, animal, out int adults, out int young);
                string stars = LivestockButcherRuntime.GetAdultStarSummaryInPen(sign, animal);

                GUILayout.BeginHorizontal();
                GUILayout.Label($"{animal}: {total} ({adults} erw. / {young} jung) | {stars}", _smartPopupLabelStyle, GUILayout.ExpandWidth(true));
                GUI.enabled = writable;
                if (GUILayout.Button("Min-", _smartPopupButtonStyle, GUILayout.Width(48f))) SetLivestockPenPopulationRule(sign, animal, Mathf.Max(0, min - 1), max);
                GUILayout.Label($"Min Erw. {min}", _smartPopupLabelStyle, GUILayout.Width(72f));
                if (GUILayout.Button("Min+", _smartPopupButtonStyle, GUILayout.Width(48f))) SetLivestockPenPopulationRule(sign, animal, min + 1, Mathf.Max(max, min + 1));
                if (GUILayout.Button("Max-", _smartPopupButtonStyle, GUILayout.Width(48f))) SetLivestockPenPopulationRule(sign, animal, min, Mathf.Max(min, max - 1));
                GUILayout.Label($"Max Ges. {max}", _smartPopupLabelStyle, GUILayout.Width(72f));
                if (GUILayout.Button("Max+", _smartPopupButtonStyle, GUILayout.Width(48f))) SetLivestockPenPopulationRule(sign, animal, min, max + 1);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            if (!writable) GUILayout.Label("Nur Lesen: Dieses Gehege ist aktuell nicht lokal network-owned.", _smartPopupLabelStyle);
        }

        private void SetLivestockPenPopulationRule(LivestockFeedSignMarker sign, string species, int min, int max)
        {
            if (!LivestockButcherRuntime.SetPenRule(sign, species, min, max, out string error))
            {
                SetStatus(error);
                return;
            }
            SetStatus($"{species}: Gehege-Min {Mathf.Max(0, min)} / Max {Mathf.Max(Mathf.Max(0, min), max)}");
        }

        private void DrawLivestockFeedCheckboxRow(LivestockFeedingRuntime.FeedItemOption option, System.Collections.Generic.List<LivestockFeedingRuntime.FeedRuleSnapshot> rules, bool showPrefab)
        {
            if (option == null || string.IsNullOrWhiteSpace(option.Prefab)) return;
            LivestockFeedingRuntime.FeedRuleSnapshot rule = rules != null
                ? rules.Find(r => r != null && string.Equals(r.Prefab, option.Prefab, StringComparison.OrdinalIgnoreCase))
                : null;
            bool enabled = rule != null;

            GUILayout.BeginHorizontal();
            string label = showPrefab ? option.Label + "  [" + option.Prefab + "]" : option.Label;
            bool nextEnabled = GUILayout.Toggle(enabled, label, GUILayout.Width(300f));
            if (nextEnabled != enabled)
            {
                SetLivestockFeedTarget(option.Prefab, nextEnabled ? 20 : 0);
            }

            if (enabled)
            {
                GUILayout.Label($"{rule.Current}/{rule.Target}", _smartPopupLabelStyle, GUILayout.Width(72f));
                if (GUILayout.Button("-10", _smartPopupButtonStyle, GUILayout.Width(48f))) ChangeLivestockFeedTarget(rule, -10);
                if (GUILayout.Button("-1", _smartPopupButtonStyle, GUILayout.Width(42f))) ChangeLivestockFeedTarget(rule, -1);
                if (GUILayout.Button("+1", _smartPopupButtonStyle, GUILayout.Width(42f))) ChangeLivestockFeedTarget(rule, 1);
                if (GUILayout.Button("+10", _smartPopupButtonStyle, GUILayout.Width(48f))) ChangeLivestockFeedTarget(rule, 10);
            }
            else
            {
                GUILayout.Label("gesperrt", _smartPopupLabelStyle, GUILayout.Width(72f));
            }
            GUILayout.EndHorizontal();
        }

        private void BeginLivestockPenBirdView(LivestockFeedSignMarker sign, float currentRadius)
        {
            if (sign == null || _livestockPenBirdView == null) return;
            ZNetView view = sign.GetNetworkView();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null || !view.IsOwner())
            {
                SetStatus("Gehege-Radius: Futtertrog ist nicht lokal beschreibbar.");
                return;
            }

            LivestockFeedSignMarker editSign = sign;
            CloseLivestockFeedMenu();
            bool started = _livestockPenBirdView.Begin(editSign, currentRadius, (finishedSign, confirmed, radius) =>
            {
                if (confirmed && finishedSign != null)
                {
                    if (LivestockPenRuntime.SetPenRadius(finishedSign, radius, out string error))
                    {
                        _livestockPenBirdView?.NotifyRadiusChanged(finishedSign);
                        SetStatus($"Gehege-Radius gespeichert: {radius:0} m (Durchmesser {radius * 2f:0} m).");
                    }
                    else SetStatus(string.IsNullOrWhiteSpace(error) ? "Gehege-Radius konnte nicht gespeichert werden." : error);
                }
                else SetStatus("Gehege-Radius bearbeiten abgebrochen.");

                if (finishedSign != null && Player.m_localPlayer != null && Vector3.Distance(Player.m_localPlayer.transform.position, finishedSign.transform.position) <= 10f)
                    TryOpenLivestockFeedMenu(finishedSign, Player.m_localPlayer);
            });

            if (!started)
            {
                SetStatus("Gehege-Vogelperspektive konnte nicht gestartet werden.");
                if (Player.m_localPlayer != null) TryOpenLivestockFeedMenu(editSign, Player.m_localPlayer);
            }
        }

        private void ChangeLivestockPenRadius(float delta)
        {
            if (_openLivestockFeedSign == null) return;
            float next = LivestockPenRuntime.GetPenRadius(_openLivestockFeedSign) + delta;
            if (!LivestockPenRuntime.SetPenRadius(_openLivestockFeedSign, next, out string error))
            {
                SetStatus(error);
                return;
            }
            _livestockPenBirdView?.NotifyRadiusChanged(_openLivestockFeedSign);
            SetStatus($"Gehege-Radius: {LivestockPenRuntime.GetPenRadius(_openLivestockFeedSign):0} m");
        }

        private void ChangeLivestockFeedTarget(LivestockFeedingRuntime.FeedRuleSnapshot rule, int delta)
        {
            if (rule == null) return;
            SetLivestockFeedTarget(rule.Prefab, Mathf.Clamp(rule.Target + delta, 0, 200));
        }

        private void SetLivestockFeedTarget(string prefab, int target)
        {
            if (_openLivestockFeedSign == null) return;
            if (!LivestockFeedingRuntime.SetFeedTarget(_openLivestockFeedSign, prefab, target, out string error))
            {
                SetStatus(string.IsNullOrWhiteSpace(error) ? "Futterziel konnte nicht gespeichert werden." : error);
                return;
            }
            string label = LivestockFeedingRuntime.GetItemDisplayName(prefab);
            SetStatus(target <= 0 ? $"{label} aus Futterliste entfernt." : $"{label}: Zielmenge {target}.");
        }

        private void CenterWorkFlagWindow()
        {
            float width = Mathf.Min(620f, Screen.width - 24f);
            float height = Mathf.Min(760f, Screen.height - 24f);
            _workFlagMenuRect = new Rect(Mathf.Max(12f, (Screen.width - width) * 0.5f), Mathf.Max(12f, (Screen.height - height) * 0.5f), width, height);
        }

        private Texture2D MakeSolidTexture(Color color)
        {
            Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private void EnsureSmartPopupStyles()
        {
            if (_smartPopupWindowStyle != null) return;
            _smartPopupBackground = MakeSolidTexture(new Color(0.055f, 0.045f, 0.032f, 0.97f));
            _smartPopupButtonBackground = MakeSolidTexture(new Color(0.18f, 0.135f, 0.075f, 0.98f));
            _smartPopupWindowStyle = new GUIStyle(GUI.skin.window);
            _smartPopupWindowStyle.normal.background = _smartPopupBackground;
            _smartPopupWindowStyle.padding = new RectOffset(14, 14, 12, 12);
            _smartPopupWindowStyle.border = new RectOffset(2, 2, 2, 2);
            _smartPopupHeadingStyle = new GUIStyle(GUI.skin.label);
            _smartPopupHeadingStyle.normal.textColor = new Color(1f, 0.62f, 0.08f, 1f);
            _smartPopupHeadingStyle.fontStyle = FontStyle.Bold;
            _smartPopupHeadingStyle.fontSize = 15;
            _smartPopupLabelStyle = new GUIStyle(GUI.skin.label);
            _smartPopupLabelStyle.normal.textColor = new Color(1f, 0.66f, 0.12f, 1f);
            _smartPopupLabelStyle.wordWrap = true;
            _smartPopupGoodStyle = new GUIStyle(_smartPopupLabelStyle);
            _smartPopupGoodStyle.normal.textColor = new Color(0.40f, 1f, 0.60f, 1f);
            _smartPopupGoodStyle.fontStyle = FontStyle.Bold;
            _smartPopupWarnStyle = new GUIStyle(_smartPopupLabelStyle);
            _smartPopupWarnStyle.normal.textColor = new Color(1f, 0.82f, 0.28f, 1f);
            _smartPopupWarnStyle.fontStyle = FontStyle.Bold;
            _smartPopupCriticalStyle = new GUIStyle(_smartPopupLabelStyle);
            _smartPopupCriticalStyle.normal.textColor = new Color(1f, 0.36f, 0.36f, 1f);
            _smartPopupCriticalStyle.fontStyle = FontStyle.Bold;
            _smartPopupTableHeaderStyle = new GUIStyle(_smartPopupLabelStyle);
            _smartPopupTableHeaderStyle.fontStyle = FontStyle.Bold;
            _smartPopupTableHeaderStyle.fontSize = 12;
            _smartPopupTableHeaderStyle.alignment = TextAnchor.MiddleCenter;
            _smartPopupTableHeaderStyle.wordWrap = false;
            _smartPopupTableCellStyle = new GUIStyle(_smartPopupLabelStyle);
            _smartPopupTableCellStyle.fontSize = 12;
            _smartPopupTableCellStyle.wordWrap = false;
            _smartPopupTableCellStyle.alignment = TextAnchor.MiddleLeft;
            _smartPopupTableValueStyle = new GUIStyle(_smartPopupTableCellStyle);
            _smartPopupTableValueStyle.alignment = TextAnchor.MiddleRight;
            _smartPopupTableGoodValueStyle = new GUIStyle(_smartPopupTableValueStyle);
            _smartPopupTableGoodValueStyle.normal.textColor = _smartPopupGoodStyle.normal.textColor;
            _smartPopupTableGoodValueStyle.fontStyle = FontStyle.Bold;
            _smartPopupTableBadValueStyle = new GUIStyle(_smartPopupTableValueStyle);
            _smartPopupTableBadValueStyle.normal.textColor = _smartPopupCriticalStyle.normal.textColor;
            _smartPopupTableBadValueStyle.fontStyle = FontStyle.Bold;
            _smartPopupButtonStyle = new GUIStyle(GUI.skin.button);
            _smartPopupButtonStyle.normal.background = _smartPopupButtonBackground;
            _smartPopupButtonStyle.hover.background = _smartPopupButtonBackground;
            _smartPopupButtonStyle.active.background = _smartPopupButtonBackground;
            _smartPopupButtonStyle.normal.textColor = new Color(1f, 0.72f, 0.18f, 1f);
            _smartPopupButtonStyle.hover.textColor = Color.white;
            _smartPopupButtonStyle.fontStyle = FontStyle.Bold;
        }

        private void OnGUI()
        {
            long perfOnGui = WorkersPerformanceProfiler.Begin();
            if (!_enabled.Value)
            {
                WorkersPerformanceProfiler.End(WorkersPerfBucket.OnGUI, perfOnGui);
                return;
            }
            ApplyWorkersPopupInputLock();
            EnsureGuiStyles();
            DrawCartographyMapCloseButton();

            if (_showDebugOverlay.Value && !(_workZoneBirdView?.Active ?? false))
            {
                Rect rect = new Rect(20f, 90f, 620f, 560f);
                GUI.Box(rect, GUIContent.none, _boxStyle);

                GUILayout.BeginArea(new Rect(rect.x + 12f, rect.y + 10f, rect.width - 24f, rect.height - 20f));
                GUILayout.Label($"Valheim_Workers v{PluginVersion} — MVP Debug", _labelStyle);
                GUILayout.Label($"Status: {_status}", _labelStyle);
                DrawRecruitmentProbeLines(true);
                GUILayout.Space(6f);
                DrawWorkerDebugOverlayCompact();
                GUILayout.Space(6f);
                GUILayout.Label(WorkersPerformanceProfiler.CompactSummary, _labelStyle);
                GUILayout.EndArea();
            }

            if (_backgroundMonitorOpen && Player.m_localPlayer != null)
            {
                EnsureSmartPopupStyles();
                _backgroundMonitorRect.width = Mathf.Min(560f, Screen.width - 24f);
                _backgroundMonitorRect.height = Mathf.Min(460f, Screen.height - 100f);
                _backgroundMonitorRect.x = Mathf.Clamp(_backgroundMonitorRect.x, 0f, Mathf.Max(0f, Screen.width - _backgroundMonitorRect.width));
                _backgroundMonitorRect.y = Mathf.Clamp(_backgroundMonitorRect.y, 0f, Mathf.Max(0f, Screen.height - _backgroundMonitorRect.height));
                _backgroundMonitorRect = GUI.Window(9142, _backgroundMonitorRect, DrawBackgroundMonitorWindow, string.Empty, _smartPopupWindowStyle);
            }

            if (_openWorkFlag != null)
            {
                EnsureSmartPopupStyles();
                _workFlagMenuRect.width = Mathf.Min(620f, Screen.width - 24f);
                _workFlagMenuRect.height = Mathf.Min(760f, Screen.height - 24f);
                _workFlagMenuRect = GUI.Window(9135, _workFlagMenuRect, DrawWorkFlagMenuWindow, string.Empty, _smartPopupWindowStyle);
            }

            if (_openProductionStation != null)
            {
                EnsureSmartPopupStyles();
                _productionStationMenuRect.width = Mathf.Min(580f, Screen.width - 24f);
                _productionStationMenuRect.height = Mathf.Min(580f, Screen.height - 24f);
                _productionStationMenuRect = GUI.Window(9143, _productionStationMenuRect, DrawProductionStationMenuWindow, string.Empty, _smartPopupWindowStyle);
            }

            if (_openLivestockFeedSign != null)
            {
                EnsureSmartPopupStyles();
                _livestockFeedMenuRect.width = Mathf.Min(680f, Screen.width - 24f);
                _livestockFeedMenuRect.height = Mathf.Min(720f, Screen.height - 24f);
                _livestockFeedMenuRect = GUI.Window(9147, _livestockFeedMenuRect, DrawLivestockFeedMenuWindow, string.Empty, _smartPopupWindowStyle);
            }

            if (_openWorkerContainer != null)
            {
                _workerContainerMenuRect = GUI.Window(9136, _workerContainerMenuRect, DrawWorkerContainerMenuWindow, "Workers-Logistikkiste");
                if (_fieldDepotStockSettingsOpen && WorkerContainerRoleRuntime.GetRole(_openWorkerContainer) == WorkerContainerRole.FieldDepot)
                {
                    EnsureSmartPopupStyles();
                    _fieldDepotStockMenuRect.width = Mathf.Min(860f, Screen.width - 24f);
                    _fieldDepotStockMenuRect.height = Mathf.Min(760f, Screen.height - 24f);
                    _fieldDepotStockMenuRect = GUI.Window(9138, _fieldDepotStockMenuRect, DrawFieldDepotStockSettingsWindow, string.Empty, _smartPopupWindowStyle);
                }
                if (_containerStockLimitSettingsOpen && WorkerContainerRoleRuntime.GetRole(_openWorkerContainer) == WorkerContainerRole.Import)
                {
                    EnsureSmartPopupStyles();
                    _containerStockLimitMenuRect.width = Mathf.Min(820f, Screen.width - 24f);
                    _containerStockLimitMenuRect.height = Mathf.Min(740f, Screen.height - 24f);
                    _containerStockLimitMenuRect = GUI.Window(9148, _containerStockLimitMenuRect, DrawContainerStockLimitSettingsWindow, string.Empty, _smartPopupWindowStyle);
                }
            }

            if (_containerStockQuickEditorOpen)
            {
                EnsureSmartPopupStyles();
                _containerStockQuickEditorRect.width = Mathf.Min(430f, Screen.width - 24f);
                _containerStockQuickEditorRect.height = Mathf.Min(330f, Screen.height - 24f);
                _containerStockQuickEditorRect.x = Mathf.Clamp(_containerStockQuickEditorRect.x, 0f, Mathf.Max(0f, Screen.width - _containerStockQuickEditorRect.width));
                _containerStockQuickEditorRect.y = Mathf.Clamp(_containerStockQuickEditorRect.y, 0f, Mathf.Max(0f, Screen.height - _containerStockQuickEditorRect.height));
                _containerStockQuickEditorRect = GUI.Window(9149, _containerStockQuickEditorRect, DrawContainerStockQuickEditorWindow, string.Empty, _smartPopupWindowStyle);
            }

            if (_openWorkerMenu != null || _openPersistentWorkerMenuZdo != null)
            {
                EnsureSmartPopupStyles();
                _workerMenuRect.width = Mathf.Min(620f, Screen.width - 24f);
                _workerMenuRect.height = Mathf.Min(720f, Screen.height - 24f);
                _workerMenuRect = GUI.Window(9137, _workerMenuRect, DrawWorkerMenuWindow, string.Empty, _smartPopupWindowStyle);
            }

            if (_openWorkerInventory != null)
            {
                EnsureSmartPopupStyles();
                _workerInventoryRect.width = Mathf.Min(520f, Screen.width - 24f);
                _workerInventoryRect.height = Mathf.Min(500f, Screen.height - 24f);
                _workerInventoryRect = GUI.Window(9140, _workerInventoryRect, DrawWorkerInventoryWindow, string.Empty, _smartPopupWindowStyle);
            }

            if (_outpostManagerOpen)
            {
                EnsureSmartPopupStyles();
                _outpostManagerRect.width = Mathf.Min(960f, Screen.width - 24f);
                _outpostManagerRect.height = Mathf.Min(800f, Screen.height - 24f);
                _outpostManagerRect = GUI.Window(9139, _outpostManagerRect, DrawOutpostManagerWindow, string.Empty, _smartPopupWindowStyle);
            }

            _workZoneBirdView?.OnGUI();
            _livestockPenBirdView?.OnGUI();
            _workerLocateCamera?.OnGUI();
            _gardenPlanning?.OnGUI();
            _reachabilityHeatmap?.OnGUI();
            WorkersPerformanceProfiler.End(WorkersPerfBucket.OnGUI, perfOnGui);
        }

        private void DrawBackgroundMonitorWindow(int windowId)
        {
            GUILayout.Label($"WORKERS BACKGROUND MONITOR  v{PluginVersion}", _smartPopupHeadingStyle);
            GUILayout.Label($"Auto-Monitor: AN | {_backgroundMonitorKey.Value} = ein/aus | breite Diagnose bei Bedarf per PowerShell", _smartPopupLabelStyle);
            GUILayout.Space(4f);

            // IMGUI invokes this method more than once per rendered frame (layout/repaint). The
            // underlying diagnostics traverse persistent registries and allocate display strings,
            // so keep a short-lived snapshot instead of rebuilding it for every GUI event.
            if (Time.unscaledTime >= _nextBackgroundMonitorDataRefresh)
            {
                _nextBackgroundMonitorDataRefresh = Time.unscaledTime + 0.75f;
                BackgroundWorkerScheduler.GetTransportMapSnapshots(_backgroundMonitorTransports);
                BackgroundSorterRuntime.GetAirHubMonitorLines(_backgroundMonitorAirHubs, 6);
                BackgroundMonitorRuntime.GetRecent(_backgroundMonitorEvents, 12);
                WorkersPerformanceProfiler.CopySummaryLines(_performanceMonitorLines, 8);
            }

            GUILayout.Label($"Persistente Worker: {BackgroundWorkerScheduler.RegisteredWorkerCount}   |   Logistik-Endpunkte: {PersistentContainerRegistryRuntime.RegisteredCount}   |   Economy-Zonen: {WorkzoneEconomyBackgroundRuntime.ActiveZoneCount}   |   aktive simulierte Transporte: {_backgroundMonitorTransports.Count}", _smartPopupLabelStyle);

            _backgroundMonitorScroll = GUILayout.BeginScrollView(_backgroundMonitorScroll);
            GUILayout.Space(4f);
            GUILayout.Label("PERFORMANCE — LETZTE 5 SEKUNDEN", _smartPopupHeadingStyle);
            for (int i = 0; i < _performanceMonitorLines.Count; i++)
                GUILayout.Label("  • " + _performanceMonitorLines[i], _smartPopupLabelStyle);

            GUILayout.Space(6f);
            GUILayout.Label("LETZTE BACKGROUND-EREIGNISSE", _smartPopupHeadingStyle);
            if (_backgroundMonitorEvents.Count == 0) GUILayout.Label("  noch keine Ereignisse", _smartPopupLabelStyle);
            for (int i = 0; i < _backgroundMonitorEvents.Count; i++)
                GUILayout.Label("  " + _backgroundMonitorEvents[i], _smartPopupLabelStyle);

            GUILayout.Space(6f);
            GUILayout.Label("AIR-HUBS", _smartPopupHeadingStyle);
            if (_backgroundMonitorAirHubs.Count == 0) GUILayout.Label("  keine persistenten AirFreight-Hubs sichtbar", _smartPopupLabelStyle);
            for (int i = 0; i < _backgroundMonitorAirHubs.Count; i++)
                GUILayout.Label("  • " + _backgroundMonitorAirHubs[i], _smartPopupLabelStyle);

            GUILayout.Space(6f);
            GUILayout.Label("SIMULIERTE TRANSPORTE", _smartPopupHeadingStyle);
            if (_backgroundMonitorTransports.Count == 0) GUILayout.Label("  aktuell keine", _smartPopupLabelStyle);
            int shown = Math.Min(6, _backgroundMonitorTransports.Count);
            for (int i = 0; i < shown; i++)
            {
                BackgroundWorkerScheduler.TransportMapSnapshot t = _backgroundMonitorTransports[i];
                int pct = Mathf.RoundToInt(t.Progress01 * 100f);
                int min = t.EtaSeconds / 60;
                int sec = t.EtaSeconds % 60;
                string cargo = WorkerZdo.TryReadCargo(t.Zdo, out string cargoPrefab, out int cargoAmount) && cargoAmount > 0 ? $" | {cargoPrefab} x{cargoAmount}" : string.Empty;
                GUILayout.Label($"  • {t.Species}/{t.Role}: {t.Mode} {pct}% ETA {min}:{sec:00}{cargo}", _smartPopupLabelStyle);
            }
            GUILayout.EndScrollView();

            GUI.DragWindow(new Rect(0f, 0f, _backgroundMonitorRect.width - 8f, 28f));
        }

        private void DrawRecruitmentProbeLines(bool useOverlayStyle)
        {
            GUIStyle style = useOverlayStyle ? _labelStyle : GUI.skin.label;
            if (_spawnerProbe == null || _spawnerProbe.NearestSpawner == null)
            {
                GUILayout.Label($"Greydwarf nest: none within {_spawnerProbeRadius.Value:0} m", style);
                if (_spawnerProbe != null)
                    GUILayout.Label($"Probe: {_spawnerProbe.ScanMode}", style);
                return;
            }

            GUILayout.Label($"Greydwarf nest: {_spawnerProbe.PrefabName} | {_spawnerProbe.NearestDistance:0.0} m", style);
            GUILayout.Label($"Probe: {_spawnerProbe.ScanMode}", style);
            GUILayout.Label($"Spawner owner: {_spawnerProbe.GetNetworkStatus()} | ZDO valid: {_spawnerProbe.HasValidNetworkState}", style);
            GUILayout.Label($"Pacification: {_spawnerProbe.Progress}/{_spawnerProbe.Required} | pacified={_spawnerProbe.Pacified} | consumed={_spawnerProbe.Consumed}", style);
            GUILayout.Label($"Home seed reward: {(_spawnerProbe.HomeSeedGranted ? "GRANTED" : (_spawnerProbe.Pacified ? "PENDING" : "locked"))}", style);
            if (!_spawnerProbe.Pacified && !_spawnerProbe.Consumed)
                GUILayout.Label($"Offering: look at nest + E within {_spawnerOfferingRadius.Value:0.0} m | {GreydwarfOfferingCatalog.DescribeAcceptedOfferings()}", style);
            else if (_spawnerProbe.Pacified && !_spawnerProbe.Consumed)
                GUILayout.Label("100%: Vanilla-SpawnArea bleibt deaktiviert; E gibt ggf. den Uralten Samen frei. Jeder Worker — auch der erste — wird erst durch einen Uralten Samen gerufen.", style);
            else if (_spawnerProbe.Consumed)
            {
                ZNetView nview = _spawnerProbe.NearestSpawner.GetComponent<ZNetView>();
                if (nview == null) nview = _spawnerProbe.NearestSpawner.GetComponentInParent<ZNetView>();
                ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
                string released = SpawnerRecruitmentZdo.ReadReleasedSpecies(zdo);
                GUILayout.Label(string.IsNullOrEmpty(released) ? "Rufer: erstes Ritual abgeschlossen" : $"Rufer: erstes Ritual abgeschlossen — erster Worker {released}", style);
                if (!SpawnerRecruitmentZdo.IsHomeSeedGranted(zdo))
                    GUILayout.Label("Ancient Seed reward pending — interact with E to retry", style);
            }
        }

        private void DrawWorkerDebugOverlayCompact()
        {
            if (_lastWorker == null)
            {
                GUILayout.Label("Worker: kein geladener Worker beobachtet", _labelStyle);
                return;
            }

            ZNetView nview = _lastWorker.GetComponent<ZNetView>();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo == null)
            {
                GUILayout.Label("Worker ZDO: unavailable", _labelStyle);
                return;
            }

            WorkerRole role = (WorkerRole)zdo.GetInt(WorkerZdo.Role, 0);
            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, 0);
            GUILayout.Label($"Worker: {GetPrefabName(_lastWorker.gameObject.name)} | {role} | {state}", _labelStyle);

            string decision = zdo.GetString(WorkerZdo.LastDecisionReason, string.Empty);
            if (!string.IsNullOrEmpty(decision))
                GUILayout.Label($"AI: {decision}", _labelStyle);

            if (WorkerZdo.TryReadCargo(zdo, out string cargoPrefab, out int cargoAmount))
                GUILayout.Label($"Cargo: {cargoPrefab} x{cargoAmount}", _labelStyle);
            else
                GUILayout.Label("Cargo: leer", _labelStyle);

            if (WorkerZdo.TryReadTarget(zdo, out string targetName, out string targetKind, out Vector3 targetPosition))
                GUILayout.Label($"Target: {targetName} [{targetKind}] | {Vector3.Distance(_lastWorker.transform.position, targetPosition):0} m", _labelStyle);
            else
                GUILayout.Label("Target: none", _labelStyle);

            GUILayout.Label($"Navigation: {WorkerNavigationRuntime.GetDebugStatus(_lastWorker)}", _labelStyle);
            GUILayout.Label($"Background: {BackgroundWorkerScheduler.GetDebugSummary(zdo)}", _labelStyle);
            GUILayout.Label("Vollstaendige Detaildiagnose: SmartCore > Workers", _labelStyle);
        }

        private void DrawWorkerDebugLines(bool useOverlayStyle)
        {
            GUIStyle style = useOverlayStyle ? _labelStyle : GUI.skin.label;

            if (_lastWorker == null)
            {
                GUILayout.Label("Worker: no recruited worker currently loaded/observed", style);
                return;
            }

            ZNetView nview = _lastWorker.GetComponent<ZNetView>();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo == null)
            {
                GUILayout.Label("Worker ZDO: unavailable", style);
                return;
            }

            WorkerRole role = (WorkerRole)zdo.GetInt(WorkerZdo.Role, 0);
            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, 0);
            Vector3 home = WorkerZdo.ReadHome(zdo, _lastWorker.transform.position);
            float distance = Vector3.Distance(_lastWorker.transform.position, home);

            GUILayout.Label($"Worker name: {GetPrefabName(_lastWorker.gameObject.name)}", style);
            GUILayout.Label($"Role: {role}", style);
            GUILayout.Label($"Worker tier: {WorkerTierCatalog.DisplayTier(_lastWorker)}", style);
            WorkerRoleDefinition roleDefinition = WorkerRoleCatalog.Get(role);
            if (roleDefinition != null)
                GUILayout.Label($"Role layer: {roleDefinition.Implementation} | {roleDefinition.Purpose}", style);
            GUILayout.Label($"State: {state}", style);
            string lastDecisionReason = zdo.GetString(WorkerZdo.LastDecisionReason, string.Empty);
            long lastDecisionTicks = zdo.GetLong(WorkerZdo.LastDecisionTicks, 0L);
            if (!string.IsNullOrEmpty(lastDecisionReason))
            {
                string ageText = string.Empty;
                if (lastDecisionTicks > 0L)
                {
                    double ageSeconds = Math.Max(0.0, (DateTime.UtcNow - new DateTime(lastDecisionTicks, DateTimeKind.Utc)).TotalSeconds);
                    ageText = $" ({ageSeconds:0}s ago)";
                }
                GUILayout.Label($"AI decision: {lastDecisionReason}{ageText}", style);
            }
            GUILayout.Label($"Combat stance: {WorkerCombatStanceRuntime.DisplayName(WorkerCombatStanceRuntime.ReadStance(zdo))}", style);
            GUILayout.Label($"Aktive Jobs: {GetWorkerActiveJobsDebug(zdo, role)}", style);
            if (WorkerZdo.TryReadCargo(zdo, out string cargoPrefab, out int cargoAmount))
            {
                GUILayout.Label($"Cargo: {cargoPrefab} x{cargoAmount}", style);
                if (WorkerZdo.TryReadCargoRoute(zdo, out string cargoRoute, out Vector3 cargoRouteTarget))
                    GUILayout.Label($"Cargo route: {cargoRoute} -> {cargoRouteTarget.x:0.0}, {cargoRouteTarget.y:0.0}, {cargoRouteTarget.z:0.0}", style);
                else
                    GUILayout.Label("Cargo route: none", style);
            }
            else if (role == WorkerRole.Gatherer || role == WorkerRole.Hauler)
                GUILayout.Label("Cargo: empty", style);

            if (WorkerZdo.TryReadTarget(zdo, out string targetName, out string targetKind, out Vector3 targetPosition))
            {
                GUILayout.Label($"Current target: {targetName} [{targetKind}]", style);
                GUILayout.Label($"Target distance: {Vector3.Distance(_lastWorker.transform.position, targetPosition):0.0} m", style);
                if (string.Equals(targetKind, WorkerStorageRuntime.SeedSourceTargetKind, StringComparison.Ordinal) ||
                    string.Equals(targetKind, WorkerStorageRuntime.GardenResourceSourceTargetKind, StringComparison.Ordinal) ||
                    string.Equals(targetKind, WorkerStorageRuntime.HaulSourceTargetKind, StringComparison.Ordinal) ||
                    string.Equals(targetKind, WorkerStorageRuntime.DepositTargetKind, StringComparison.Ordinal) ||
                    string.Equals(targetKind, WorkerStorageRuntime.OverflowDepositTargetKind, StringComparison.Ordinal) ||
                    string.Equals(targetKind, WorkerStorageRuntime.FieldDepotDepositTargetKind, StringComparison.Ordinal) ||
                    string.Equals(targetKind, WorkerStorageRuntime.ImportDepositTargetKind, StringComparison.Ordinal))
                {
                    float horizontalContainerDistance = WorkerStorageRuntime.HorizontalDistance(_lastWorker.transform.position, targetPosition);
                    GUILayout.Label($"Container horizontal: {horizontalContainerDistance:0.0} m / column {WorkerStorageRuntime.ContainerAccessRadius:0.0} m; ground-point stop {WorkerStorageRuntime.ContainerApproachStopDistance:0.0} m", style);
                }
                GUILayout.Label($"Target from home: {Vector3.Distance(home, targetPosition):0.0} m", style);
            }
            else
            {
                GUILayout.Label("Current target: none", style);
            }

            GUILayout.Label($"Distance from home: {distance:0.0} m", style);
            GUILayout.Label($"Network owner: {(_ownership != null ? _ownership.GetDebugOwnerStatus(_lastWorker) : (nview.IsOwner() ? "LOCAL" : "REMOTE"))}", style);
            GUILayout.Label($"Work radius: {zdo.GetFloat(WorkerZdo.WorkRadius, _workRadius.Value):0.0} m", style);
            GUILayout.Label($"Home: {home.x:0.0}, {home.y:0.0}, {home.z:0.0}", style);
            string homeAnchorType = zdo.GetString(WorkerZdo.HomeAnchorType, string.Empty);
            GUILayout.Label($"Home anchor: {(string.IsNullOrEmpty(homeAnchorType) ? "legacy position" : homeAnchorType)}", style);
            if (WorkerZdo.TryReadWorkArea(zdo, out WorkOrder debugOrder, out Vector3 debugCenter, out float debugRadius))
            {
                string debugFlagId = WorkerZdo.ReadWorkAreaId(zdo);
                string debugZoneName = WorkFlagRegistry.TryGetDisplayName(debugFlagId, WorkerZdo.ReadWorkAreaName(zdo));
                GUILayout.Label($"Work area: {(string.IsNullOrWhiteSpace(debugZoneName) ? "unnamed" : debugZoneName)} | {WorkOrderCatalog.DisplayName(debugOrder)} | radius {debugRadius:0} m | persistent", style);
                GUILayout.Label($"Work flag id: {(string.IsNullOrEmpty(debugFlagId) ? "legacy/no-id" : debugFlagId.Substring(0, Math.Min(8, debugFlagId.Length)))}", style);
                GUILayout.Label($"Work center: {debugCenter.x:0.0}, {debugCenter.y:0.0}, {debugCenter.z:0.0} | distance {Vector3.Distance(_lastWorker.transform.position, debugCenter):0.0} m", style);
                if (role == WorkerRole.Lumberjack)
                {
                    GUILayout.Label($"Woodcut capability tier: {WorkerSkillCatalog.GetWoodcutTier(_lastWorker)}", style);
                    if (TreeTargetFinder.LastTierRejectedCount > 0)
                        GUILayout.Label($"Wood tier blocked: {TreeTargetFinder.LastTierRejectedCount} target(s); lowest={TreeTargetFinder.LastTierRejectedPrefab} requires tier {TreeTargetFinder.LastLowestRejectedTier}", style);
                }
                else if (role == WorkerRole.Gatherer)
                    GUILayout.Label($"Gather carry capacity: {WorkerSkillCatalog.GetGatherCarryCapacity(_lastWorker)}", style);
                else if (role == WorkerRole.Hauler)
                    GUILayout.Label($"Haul carry capacity: {HaulerWorkRuntime.GetCarryCapacity(_lastWorker)}", style);
            }
            else
            {
                GUILayout.Label("Work area: home fallback (no persistent assignment)", style);
            }
            if (WorkerRestRuntime.TryGetHealth(_lastWorker, out float workerHealth, out float workerMaxHealth))
                GUILayout.Label($"Health: {workerHealth:0.0}/{workerMaxHealth:0.0} ({(workerHealth / workerMaxHealth) * 100f:0}%)", style);
            GUILayout.Label($"Environment: {(WorkerRestRuntime.IsNight() ? "NIGHT" : "DAY")}", style);
            GUILayout.Label($"Velocity: {_lastWorker.GetVelocity().magnitude:0.00} m/s", style);
            GUILayout.Label($"Navigation: {WorkerNavigationRuntime.GetDebugStatus(_lastWorker)}", style);
            if (state == WorkerState.ReturnHome || state == WorkerState.ReturnHomeForRest)
                GUILayout.Label($"Home watchdog: {WorkerRespawnRuntime.GetHomeWatchdogDebug(_lastWorker, zdo)}", style);
            if (role == WorkerRole.Hauler && string.Equals(GetPrefabName(_lastWorker.gameObject.name), "Lox", StringComparison.OrdinalIgnoreCase))
                GUILayout.Label($"Lox obstacle recovery: {LoxObstacleRecoveryRuntime.GetDebugStatus(_lastWorker)}", style);
            GUILayout.Label($"Background: {BackgroundWorkerScheduler.GetDebugSummary(zdo)}", style);
            if (role == WorkerRole.Gatherer)
                GUILayout.Label($"Gather background: {BackgroundGathererRuntime.GetDebugSummary(zdo)}", style);
            if (role == WorkerRole.Gatherer || role == WorkerRole.Hauler || (role == WorkerRole.Gardener &&
                ((WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None) == WorkOrder.Forestry ||
                 (WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None) == WorkOrder.Garden)))
                GUILayout.Label($"Storage route: {WorkerStorageRuntime.GetDebugStatus(_lastWorker)}", style);
                if (role == WorkerRole.Hauler)
                    GUILayout.Label($"Haul scan: {WorkerStorageRuntime.GetHaulDebugSummary()}", style);
                GUILayout.Label($"Harvest index: scanned={GardenHarvestTargetFinder.LastPhysicsHits}, pickables={GardenHarvestTargetFinder.LastPickablesResolved}, smartMatches={GardenHarvestTargetFinder.LastSmartMatches}, usable={GardenHarvestTargetFinder.LastUsableCandidates}", style);
                GUILayout.Label($"Harvest pickables: {GardenHarvestTargetFinder.LastPickableSample}", style);
                GUILayout.Label($"Harvest registry: status={GardenHarvestTargetFinder.LastSceneStatus}; loaded={GardenHarvestTargetFinder.LastSceneRegistered}; scanned={GardenHarvestTargetFinder.LastScenePickables}; usable={GardenHarvestTargetFinder.LastSceneUsable}", style);
                GUILayout.Label($"Harvest bridge: {SmartReplantBridge.GetDebugSummary()}", style);
                GUILayout.Label($"Garden plan: pending={GardenReplantPlanRuntime.LastPendingCount}; status={GardenReplantPlanRuntime.LastStatus}", style);
                if (GardenerTaskFilterRuntime.CanUseWildGrowth(zdo))
                    GUILayout.Label($"WildGrowth: {WildGrowthRuntime.GetDebugSummary()}", style);
                if (WorkerZdo.TryReadGardenPlan(zdo, out WorkerGardenPlan debugGardenPlan))
                    GUILayout.Label($"Garden worker plan: slot={debugGardenPlan.SlotId}; plant={debugGardenPlan.PlantPrefab}; resource={debugGardenPlan.ResourcePrefab} x{debugGardenPlan.ResourceAmount}; explicitGhost=true", style);
                GUILayout.Label($"Forestry plan: pending={ForestryPlanRuntime.LastPendingCount}; checked={ForestryPlanRuntime.LastCheckedCount}; blocked={ForestryPlanRuntime.LastBlockedCount}; status={ForestryPlanRuntime.LastStatus}", style);
                GUILayout.Label($"Forestry background: {ForestryBackgroundRuntime.GetDebugSummary(zdo)}", style);
                GUILayout.Label($"Air staging: {BackgroundSorterRuntime.GetPersistentAirStageDebug(zdo)}", style);
                if (WorkerZdo.TryReadForestryPlan(zdo, out WorkerForestryPlan debugForestryPlan))
                    GUILayout.Label($"Forestry worker plan: slot={debugForestryPlan.SlotId}; sapling={debugForestryPlan.SaplingPrefab}; seed={debugForestryPlan.SeedPrefab}", style);

            if (role == WorkerRole.Stonemason)
                GUILayout.Label($"Stone scan: {StoneTargetFinder.GetLastDebugSummary()}", style);

            if (state == WorkerState.Follow)
                GUILayout.Label("Navigation: vanilla MonsterAI follow -> assigned player", style);
            else if (state == WorkerState.ReturnHome)
                GUILayout.Label("Navigation: post-vanilla BaseAI.MoveTo -> Home", style);
            else if (state == WorkerState.WaitAtHome)
                GUILayout.Label("Idle: no work available; waiting at assigned rest station", style);
            else if (state == WorkerState.Standby)
            {
                string standbyName = WorkerZdo.TryReadTarget(zdo, out string sn, out string sk, out _) &&
                                     string.Equals(sk, WorkerStandbyRuntime.TargetKind, StringComparison.Ordinal)
                    ? sn
                    : "post";
                GUILayout.Label("Standby: on duty at " + standbyName, style);
            }
            else if (state == WorkerState.ReturnHomeForRest)
                GUILayout.Label($"Rest: returning to HomeAnchor ({WorkerRestRuntime.GetReason(zdo)})", style);
            else if (state == WorkerState.Resting)
                GUILayout.Label("Rest: sleeping/resting at HomeAnchor; healing pulses active if injured", style);
                if (string.Equals(homeAnchorType, GreydwarfHomeAnchorZdo.GreydwarfNestKind, StringComparison.Ordinal))
                    GUILayout.Label($"Wurzelnest pose: {GreydwarfThroneRestRuntime.GetDebugStatus(_lastWorker, zdo)}", style);
            else if (state == WorkerState.Recover)
                GUILayout.Label("Rest: low-HP recovery at HomeAnchor; work resumes after recovery threshold", style);
                if (string.Equals(homeAnchorType, GreydwarfHomeAnchorZdo.GreydwarfNestKind, StringComparison.Ordinal))
                    GUILayout.Label($"Wurzelnest pose: {GreydwarfThroneRestRuntime.GetDebugStatus(_lastWorker, zdo)}", style);
            else if (state == WorkerState.Combat)
                GUILayout.Label("Rest: interrupted by damage; vanilla combat temporarily released", style);
            else if (state == WorkerState.MoveToWorkArea)
                GUILayout.Label("Navigation: post-vanilla BaseAI.MoveTo -> Workers-Arbeitsfahne", style);
            else if (state == WorkerState.MoveToTarget)
                GUILayout.Label("Navigation: post-vanilla BaseAI.MoveTo -> Target", style);
            else if (state == WorkerState.Collect)
            {
                if (WorkerZdo.TryReadTarget(zdo, out _, out string collectDebugKind, out _) &&
                    string.Equals(collectDebugKind, WorkerStorageRuntime.SeedSourceTargetKind, StringComparison.Ordinal))
                    GUILayout.Label("Forestry: taking one real seed/cone from HomeAnchor storage", style);
                else if (string.Equals(collectDebugKind, WorkerStorageRuntime.GardenResourceSourceTargetKind, StringComparison.Ordinal))
                    GUILayout.Label("Garden: taking one real validated planting resource from HomeAnchor storage", style);
                else
                    GUILayout.Label("Gatherer: claiming a real loose ItemDrop", style);
            }
            else if (state == WorkerState.Deliver)
            {
                if (WorkerZdo.TryReadTarget(zdo, out _, out string deliveryDebugKind, out _) &&
                    (string.Equals(deliveryDebugKind, WorkerStorageRuntime.DepositTargetKind, StringComparison.Ordinal) ||
                     string.Equals(deliveryDebugKind, WorkerStorageRuntime.OverflowDepositTargetKind, StringComparison.Ordinal)))
                    GUILayout.Label("Gatherer: carrying persisted cargo -> normal base chest", style);
                else
                    GUILayout.Label("Gatherer: cargo retained; waiting for available storage", style);
            }
            else if (state == WorkerState.Work)
            {
                WorkOrder runningOrder = (WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None);
                if (role == WorkerRole.Lumberjack)
                {
                    GUILayout.Label("Navigation: arrived; native Lumberjack work action active", style);
                    GUILayout.Label($"Work action: {LumberjackWorkRuntime.GetDebugStatus(_lastWorker)}", style);
                }
                else if (role == WorkerRole.Stonemason)
                {
                    GUILayout.Label("Navigation: arrived; native Stonemason work action active", style);
                    GUILayout.Label($"Work action: {StonemasonWorkRuntime.GetDebugStatus(_lastWorker)}", style);
                }
                else if (role == WorkerRole.Gardener && runningOrder == WorkOrder.Garden)
                    GUILayout.Label("Garden: arrived at validated crop planting point", style);
                else if (role == WorkerRole.Gardener && runningOrder == WorkOrder.Forestry)
                    GUILayout.Label("Forestry: arrived at sapling planting point", style);
            }
        }

        private void OnDestroy()
        {
            WorkersModalUiRuntime.SetActive(false);
            if (Instance == this) Instance = null;
            _gardenPlanning?.Shutdown();
            _workZoneBirdView?.Shutdown();
            _livestockPenBirdView?.Shutdown();
            _mapMarkers?.Dispose();
            _ownership?.Shutdown();
            _smartStorageFeedingBridge?.Shutdown();
            _harmony?.UnpatchSelf();
            BoatPassengerRuntime.Shutdown();
            WorkerNavigationRuntime.Shutdown();
            WorkerRestAnimationRuntime.Shutdown();
            WorkerRestRuntime.Shutdown();
            WorkerRestPhysicsLockRuntime.Clear();
            RestStationUnlockRuntime.Shutdown();
            SmartReplantBridge.Shutdown();
            WorkFlagRegistry.Clear();
            BackgroundWorkerScheduler.Clear();
            WorkerFastLookupRuntime.Clear();
            WorkerCharacterIndexRuntime.Clear();
            WorkerWearNTearIndexRuntime.Clear();
            WorkerSpawnerIndexRuntime.Clear();
            LumberjackTreeFilterRuntime.ClearCaches();
            StonemasonRockFilterRuntime.ClearCaches();
            HunterPreyFilterRuntime.ClearCaches();
            FulingVillageDiplomacyRuntime.ClearCaches();
            ContainerLedgerRuntime.ClearCaches();
            BackgroundGroundPickupRuntime.ClearCaches();
            BackgroundHunterRuntime.ClearCaches();
            BackgroundFisherRuntime.ClearCaches();
            LivestockButcherRuntime.ClearCaches();
            LivestockFeedingRuntime.ClearCaches();
            ProductionOrderClaimRuntime.Clear();
            WorkersPerformanceProfiler.Reset();
            RestoreMenuCursor();
            if (_smartPopupBackground != null) Destroy(_smartPopupBackground);
            if (_smartPopupButtonBackground != null) Destroy(_smartPopupButtonBackground);
        }

        private void EnsureGuiStyles()
        {
            if (_boxStyle != null) return;
            _boxStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft };
            _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
        }
    }
}
