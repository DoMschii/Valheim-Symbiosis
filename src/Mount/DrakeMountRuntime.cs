using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Local-player multi-form symbiosis / full-possession runtime.
    ///
    /// The real Player remains authoritative for HP, inventory, food, death, ownership and world
    /// streaming while a temporary networked creature is the visible collision/movement/attack body.
    /// Movement/camera/combat live here; DrakeForm only owns necklace binding and activation.
    /// </summary>
    internal static class DrakeMountRuntime
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const string DrakeFormId = "Drake";
        private const string SerpentFormId = "Serpent";
        private const string TrollFormId = "Troll";
        private const string WerewolfFormId = "Werewolf";
        private const string SeatName = "VW_DrakeMountSeat";
        private const string TemporaryAvatarMarkerKey = "vw_symbiosis_temp_avatar";
        private const string TemporaryAvatarOwnerKey = "vw_symbiosis_temp_owner";
        private const string TemporaryAvatarFormKey = "vw_symbiosis_temp_form";
        private const string TemporaryAvatarPrefabKey = "vw_symbiosis_temp_prefab";
        private const string TemporaryAvatarLevelKey = "vw_symbiosis_temp_level";
        private const string PlayerSymbiosisActiveKey = "vw_symbiosis_player_active";
        private const string PlayerSymbiosisFormKey = "vw_symbiosis_player_form";
        private const string PlayerSymbiosisRevisionKey = "vw_symbiosis_player_revision";
        private const string CameraModePrefPrefix = "vw_drake_symbiosis_camera_";
        private const string GlobalPerspectivePrefPrefix = "Valheim_Ego_Perspective_v1_";
        private const string CrashRecoveryPrefPrefix = "vw_drake_symbiosis_recovery_020_";
        private const string LegacyCleanupPrefPrefix = "vw_drake_symbiosis_legacy_cleanup_0143_";

        private static readonly MethodInfo AttachStartMethod = typeof(Character).GetMethod(
            "AttachStart",
            InstanceFlags,
            null,
            new[]
            {
                typeof(Transform), typeof(GameObject), typeof(bool), typeof(bool), typeof(bool),
                typeof(string), typeof(Vector3), typeof(Transform)
            },
            null);
        private static readonly MethodInfo AttachStopMethod = typeof(Character).GetMethod("AttachStop", InstanceFlags, null, Type.EmptyTypes, null);
        private static readonly MethodInfo IsAttachedMethod = typeof(Character).GetMethod("IsAttached", InstanceFlags, null, Type.EmptyTypes, null);
        private static readonly MethodInfo GetVisualMethod = typeof(Character).GetMethod("GetVisual", InstanceFlags, null, Type.EmptyTypes, null);
        private static readonly FieldInfo CharacterEyeField = typeof(Character).GetField("m_eye", InstanceFlags);
        private static readonly FieldInfo CharacterFactionField = typeof(Character).GetField("m_faction", InstanceFlags);
        private static readonly Type ZInputType = typeof(Player).Assembly.GetType("ZInput");
        private static readonly MethodInfo ZInputGetButtonMethod = ZInputType?.GetMethod("GetButton", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
        private static readonly MethodInfo ZInputGetButtonDownMethod = ZInputType?.GetMethod("GetButtonDown", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
        private static readonly MethodInfo PlayerIsTeleportingMethod = typeof(Player).GetMethod("IsTeleporting", InstanceFlags, null, Type.EmptyTypes, null);
        private static readonly FieldInfo MonsterTargetCreatureField = typeof(MonsterAI).GetField("m_targetCreature", InstanceFlags);
        private static readonly FieldInfo MonsterTargetStaticField = typeof(MonsterAI).GetField("m_targetStatic", InstanceFlags);
        private static readonly MethodInfo SetAlertedMethod = typeof(BaseAI).GetMethod("SetAlerted", InstanceFlags, null, new[] { typeof(bool) }, null);
        private static readonly MethodInfo ZdoSetPersistentMethod = typeof(ZDO).GetMethod("SetPersistent", InstanceFlags, null, new[] { typeof(bool) }, null);
        private static readonly MethodInfo HumanoidAbortAttackMethod = typeof(Humanoid).GetMethod("AbortAttack", InstanceFlags, null, Type.EmptyTypes, null);
        private static readonly MethodInfo CharacterIsSwimmingMethod = typeof(Character).GetMethod("IsSwimming", InstanceFlags, null, Type.EmptyTypes, null);
        private static readonly MethodInfo CharacterIsOnGroundMethod = typeof(Character).GetMethod("IsOnGround", InstanceFlags, null, Type.EmptyTypes, null);
        private static readonly FieldInfo HumanoidCurrentAttackField = typeof(Humanoid).GetField("m_currentAttack", InstanceFlags);

        // Troll tank identity: the real Player remains the only HP pool, but incoming hits are
        // transformed before they are forwarded to Player.Damage. This keeps normal player armour,
        // resistances, food and death handling authoritative while giving the Troll form a distinct
        // defensive niche instead of merely being a larger land skin. Elemental/spirit damage is
        // intentionally NOT reduced.
        private const float TrollPhysicalDamageMultiplier = 0.65f;
        private const float DrakeFrostDamageMultiplier0Star = 0.50f;
        private const float DrakeFrostDamageMultiplier1Star = 0.35f;
        private const float DrakeFrostDamageMultiplier2Star = 0.20f;
        private const float SerpentPoisonDamageMultiplier0Star = 0.50f;
        private const float SerpentPoisonDamageMultiplier1Star = 0.35f;
        private const float SerpentPoisonDamageMultiplier2Star = 0.20f;
        private const float TrollStaggerMultiplier = 0.35f;
        private const float TrollPushForceMultiplier = 0.25f;
        private const float TrollSprintStaminaPerSecond = 16f;
        private const float DrakeClimbStaminaPerSecond = 10f;
        private const float DrakeDiveMomentumMinDuration = 0.45f;
        private const float DrakeDiveMomentumFreeClimbSeconds = 2.0f;
        private const float DrakeClimbIntentThreshold = 0.08f;
        private const float DrakeCruiseSpeed = 18f;
        private const float DrakeDiveSpeed = 32f;
        // v3.0.8: accelerate real world travel without touching Hatchling fly-speed fields.
        // Native m_flySlowSpeed/m_flyFastSpeed must stay prefab-authored so FlyForward/upward
        // flight keeps the good wing-flap locomotion restored in v3.0.7.
        private const float DrakeNativeTravelMultiplier = 1.35f;
        private const float DrakeNativeDiveTravelMultiplier = 1.50f;
        private const float DrakeDiveMinDownwardY = -0.58f;
        private const float DrakeNormalMaxPitchY = 0.57f;
        private const float DrakeWingbeatPropulsionMin = 0.88f;
        private const float DrakeWingbeatPropulsionMax = 1.14f;
        private const float DrakeWingbeatPropulsionMaxBoost = 1.18f;
        private const float TrollGroundSlamDamage = 95f;
        private const float TrollGroundSlamRadius = 6.0f;
        private const float TrollGroundSlamPush = 24f;
        private const float TrollGroundSlamCooldown = 5.5f;
        private const float TrollGroundSlamStamina = 24f;
        private const float TrollGroundSlamWorldRadius = 5.5f;
        private const float TrollGroundSlamWorldBlunt = 120f;
        private const float TrollGroundSlamWorldChop = 85f;
        private const float TrollGroundSlamWorldPickaxe = 85f;
        private const float TrollRoarRadius = 9.0f;
        private const float TrollRoarDamage = 28f;
        private const float TrollRoarPush = 36f;
        private const float TrollRoarStagger = 4.5f;
        private const float TrollRoarCooldown = 9.0f;
        private const float TrollRoarStamina = 18f;
        private const int TrollLogMaxDurability = 12;
        private const float TrollLogPickupRadius = 6.0f;
        private const float TrollLogTargetDistance = 8.0f;
        private const float TrollLogBluntMultiplier = 1.35f;
        private const float TrollLogBonusChop = 35f;
        private const int TrollLogGroundSlamDurabilityCost = 2;
        private const float TrollLogGroundSlamDamageMultiplier = 1.45f;
        private const float TrollLogGroundSlamRadiusMultiplier = 1.20f;
        private const float TrollLogGroundSlamPushMultiplier = 1.25f;
        private const float TrollLogGroundSlamWorldDamageMultiplier = 1.50f;
        private const float TrollUnarmedTreeHitRadius = 2.4f;
        private const float TrollUnarmedTreeHitForwardOffset = 1.8f;
        private const float TrollUnarmedTreeBlunt = 70f;
        private const float TrollUnarmedTreeChop = 55f;
        // v4.0.1: custom Troll damage is synchronized to the visible impact instead of the input edge.
        // Native Troll LMB already applies character damage through Valheim's animation event; only
        // our extra natural-object punch needs a delayed contact window.
        private const float TrollUnarmedPunchImpactDelay = 0.56f;
        private const float TrollLogSwingImpactDelay = 0.70f;
        private const float TrollGroundSlamImpactDelay = 0.68f;
        private const float TrollRoarImpactDelay = 0.55f;

        private const float SerpentBiteBurstPierce = 75f;
        private const float SerpentBiteBurstBlunt = 22f;
        private const float SerpentBiteBurstRange = 8.5f;
        private const float SerpentBiteBurstCooldown = 3.8f;
        private const float SerpentBiteBurstStamina = 16f;
        private const float SerpentBiteBurstPoison = 35f;
        private const float SerpentBiteLungeSpeed = 15.5f;
        private const float SerpentBiteLungeDuration = 0.28f;
        private const float SerpentTailSweepDamage = 58f;
        private const float SerpentTailSweepRadius = 5.5f;
        private const float SerpentTailSweepPush = 20f;
        private const float SerpentTailSweepCooldown = 5.2f;
        private const float SerpentTailSweepStamina = 22f;

        // Werewolf identity: fast land traversal + short burst assassin combo. Bound creature level
        // maps directly to the requested star multiplier: level 1/0*=1.5x, level 2/1*=1.75x,
        // level 3+/2*=2.5x. The multiplier is applied to real outgoing native claw HitData.
        private const float WerewolfSprintStaminaPerSecond = 12f;
        private const float SerpentBoostStaminaPerSecond = 8f;
        private const float SerpentBackwardSpeedScale = 0.16f;
        private const float SerpentLateralSpeedScale = 0.24f;
        private const float SerpentSurfaceEyeClearance = 2.65f;
        private const float SerpentSurfaceMotorSpeed = 7.0f;
        private const float WerewolfWalkSpeed = 11.0f;
        private const float WerewolfRunSpeed = 24.0f;
        private const float WerewolfMovementJumpVelocity = 10.0f;
        private const float WerewolfMovementJumpStaminaCost = 8f;
        private const float WerewolfMovementJumpCooldown = 0.42f;
        private const float WerewolfComboStaminaCost = 35f;
        private const float WerewolfComboCooldown = 18f;
        private const float WerewolfComboInitialRange = 15f;
        private const float WerewolfComboChainRange = 7f;
        private const int WerewolfComboMaxTargets = 5;
        private const float WerewolfComboStepSeconds = 0.085f;
        private const float WerewolfComboReturnDelaySeconds = 0.06f;
        private const float WerewolfClawFallbackSlash = 70f;
        private const float WerewolfClawRange = 3.4f;
        private const float WerewolfClawCooldown = 0.62f;
        private const float WerewolfAimDotThreshold = 0.58f;
        private const float WerewolfPounceSpeed = 18.0f;
        private const float WerewolfPounceUpVelocity = 4.8f;
        private const float WerewolfPounceDuration = 0.22f;
        private const float WerewolfPounceStaminaCost = 18f;
        private static readonly FieldInfo HitDamageField = typeof(HitData).GetField("m_damage", InstanceFlags);
        private static readonly FieldInfo HitPushForceField = typeof(HitData).GetField("m_pushForce", InstanceFlags);
        private static readonly FieldInfo HitStaggerMultiplierField = typeof(HitData).GetField("m_staggerMultiplier", InstanceFlags);
        private static readonly Type HitDamageType = HitDamageField != null ? HitDamageField.FieldType : null;
        private static readonly FieldInfo HitBluntField = HitDamageType?.GetField("m_blunt", InstanceFlags);
        private static readonly FieldInfo HitSlashField = HitDamageType?.GetField("m_slash", InstanceFlags);
        private static readonly FieldInfo HitPierceField = HitDamageType?.GetField("m_pierce", InstanceFlags);
        private static readonly FieldInfo HitChopField = HitDamageType?.GetField("m_chop", InstanceFlags);
        private static readonly FieldInfo HitPickaxeField = HitDamageType?.GetField("m_pickaxe", InstanceFlags);
        private static readonly FieldInfo HitFrostField = HitDamageType?.GetField("m_frost", InstanceFlags);
        private static readonly FieldInfo HitPoisonField = HitDamageType?.GetField("m_poison", InstanceFlags);

        private struct ElementalHitSnapshot
        {
            internal bool Active;
            internal bool HasElement;
            internal FieldInfo Field;
            internal float Original;
        }

        private struct TrollHitSnapshot
        {
            internal bool Active;
            internal bool HasBlunt, HasSlash, HasPierce, HasChop, HasPickaxe, HasPushForce, HasStagger;
            internal float Blunt, Slash, Pierce, Chop, Pickaxe, PushForce, Stagger;
        }

        private sealed class PendingTrollNativeHit
        {
            internal Character Target;
            internal HitData Hit;
            internal float ApplyAt;
        }

        private static Character _mountedDrake;
        private static string _activeFormId = DrakeFormId;
        private static int _activeBoundLevel = 1;
        private static float _serpentSurfaceY;
        private static float _serpentDepth;
        private static GameObject _serpentVisualRoot;
        private static Quaternion _serpentVisualBaseLocalRotation = Quaternion.identity;
        private static Transform[] _serpentWaveBones = Array.Empty<Transform>();
        private static Quaternion[] _serpentWaveBoneBaseRotations = Array.Empty<Quaternion>();
        private static bool _serpentVisualWaveInitialized;
        private static float _serpentSwimPhase;
        private static float _serpentVisualPitch;

        // v0.8.0 visual-only Drake banking. Physics/root rotation remains authoritative; this
        // only leans the render hierarchy during lateral flight/turning so the body no longer
        // looks rigid while A/D is used.
        private static GameObject _drakeBankVisualRoot;
        private static Quaternion _drakeBankBaseLocalRotation = Quaternion.identity;
        private static Vector3 _drakeBankBaseLocalPosition = Vector3.zero;
        private static float _drakeVisualBankDegrees;
        private static float _drakeVisualWingPitchDegrees;
        private static float _drakeVisualWingLift;
        private static GameObject _temporarySymbiosisAvatar;
        private static GameObject _troll1PrefabTemplate;
        private static GameObject _troll2PrefabTemplate;
        private static Player _rider;
        private static Transform _seat;
        private static int _mountedAtFrame = -1;
        private static Action<string> _debugLog;
        private static Action<string> _status;
        private static bool _reportedAttachApiMissing;
        private static Renderer[] _hiddenPlayerRenderers = Array.Empty<Renderer>();
        private static bool[] _hiddenPlayerRendererStates = Array.Empty<bool>();
        private static Collider[] _hiddenPlayerColliders = Array.Empty<Collider>();
        private static bool[] _hiddenPlayerColliderStates = Array.Empty<bool>();
        private static bool _drakeEgoActive;
        private static Transform _drakeEgoAnchor;
        private static Transform _egoHeadLookTransform;
        private static Quaternion _egoHeadLookOriginalRotation = Quaternion.identity;
        private static bool _egoHeadLookApplied;
        private static bool _cameraCallbacksRegistered;
        private static Vector3 _preCullCameraPosition;
        private static Quaternion _preCullCameraRotation;
        private static bool _cameraOverriddenThisRender;
        private static float _cameraYaw;
        private static float _cameraPitch = 6f;
        private static bool _cameraPoseInitialized;
        private static Vector3 _chaseFocusLocal;
        private static float _chaseDistance = 8.5f;
        private static float _chaseHeight = 1.75f;
        private static float _savedNearClip = -1f;
        private static float _savedFov = -1f;
        private static bool _cameraStateCaptured;
        private static Vector3 _smoothedThirdPosition;
        private static Quaternion _smoothedThirdRotation = Quaternion.identity;
        private static bool _thirdCameraSmoothInitialized;
        private static int _thirdCameraSmoothFrame = -1;
        private static float _thirdChaseYaw;
        private static float _thirdChaseYawVelocity;
        private static bool _thirdChaseYawInitialized;
        private static Vector3 _smoothedEgoPosition;
        private static bool _egoPositionSmoothInitialized;
        private static int _egoPositionSmoothFrame = -1;
        // v0.1.48: flight/root smoothing. The Drake Character is physics-driven, while the
        // render camera runs once per rendered frame. Interpolating the body and low-pass filtering
        // player-owned movement prevents fixed-step "stair stepping" from becoming visible camera shake.
        private static Vector3 _smoothedFlightMove = Vector3.zero;
        private static bool _flightMoveSmoothInitialized;
        private static Rigidbody _smoothFlightBody;
        // v3.8.0: cache the two rigidbodies touched on every render/physics frame. GetComponent is
        // cheap in isolation but becomes measurable when repeated several times per frame for long
        // symbiosis sessions. The owner refs make body swaps/self-heal safe.
        private static Character _cachedMountedBodyOwner;
        private static Rigidbody _cachedMountedBody;
        private static Player _cachedRiderBodyOwner;
        private static Rigidbody _cachedRiderBody;
        // v4.0.0: cache the mounted ZNetView as well. Ownership/validity checks run every frame,
        // but the component itself only changes when the possessed avatar changes.
        private static Character _cachedMountedViewOwner;
        private static ZNetView _cachedMountedView;
        // v4.0.0: IsMountUiBlocking used to rebuild reflection metadata several times per frame
        // from movement, combat and camera paths. Resolve those probes once and cache the result
        // for the current rendered frame.
        private sealed class UiVisibilityProbe
        {
            internal MethodInfo StaticVisible;
            internal PropertyInfo InstanceProperty;
            internal FieldInfo InstanceField;
            internal MethodInfo InstanceVisible;
        }
        private static readonly string[] UiBlockingTypeNames = { "InventoryGui", "Menu", "TextInput", "StoreGui" };
        private static UiVisibilityProbe[] _uiVisibilityProbes;
        private static int _uiBlockingCachedFrame = -1;
        private static bool _uiBlockingCachedValue;
        private static readonly RaycastHit[] _trollRaycastBuffer = new RaycastHit[24];
        private static readonly Collider[] _trollOverlapBuffer = new Collider[64];
        // v4.0.0: chase-camera collision runs every Third-Person render frame. Keep it
        // allocation-free so long flight sessions do not generate a RaycastHit[] per frame.
        private static readonly RaycastHit[] _chaseCameraHitBuffer = new RaycastHit[32];
        private static float _nextInactiveHousekeepingAt;
        private static RigidbodyInterpolation _savedFlightBodyInterpolation = RigidbodyInterpolation.None;
        private static bool _savedFlightBodyInterpolationValid;
        private static CollisionDetectionMode _savedFlightCollisionMode = CollisionDetectionMode.Discrete;
        private static bool _savedFlightCollisionModeValid;
        private static float _smoothedWingbeatMeters;
        private static bool _wingbeatSmoothInitialized;
        private static Character _drakePropulsionCharacter;
        private static float _drakeBaseWalkSpeed;
        private static float _drakeBaseRunSpeed;
        private static bool _drakePropulsionSpeedCaptured;
        private static float _drakeWingbeatPropulsion = 1f;
        private static bool _drakeNativeTravelAssistInitialized;
        private static Vector3 _drakeNativeTravelAssistLastPosition;
        private static bool _drakeFastFlightActive;
        private static float _drakeDiveStartedAt = -1f;
        private static float _drakeFreeClimbUntil = -1f;
        private static Animator _drakeAnimator;
        private static string _lastWingbeatClipName = string.Empty;
        private static int _lastWingbeatStateHash;
        private static Vector3 _stableEgoLocalOffset;
        private static bool _stableEgoOffsetInitialized;
        private static Renderer[] _drakeEgoRenderers = Array.Empty<Renderer>();
        private static bool[] _drakeEgoRendererStates = Array.Empty<bool>();
        private static bool _drakeVisualHiddenForRender;
        private static float _playerDamageImmuneUntil;
        private static bool _forwardingSymbiosisDamageToPlayer;
        private static bool _cameraCallbacksMovedToTail;
        private static object _egoToggleKeyEntry;
        private static object _egoToggleKeySavedValue;
        private static PropertyInfo _egoToggleKeyValueProperty;
        private static FieldInfo _egoFirstPersonEnabledField;
        private static bool _egoFirstPersonEnabledSaved;
        private static object _egoPluginInstance;
        private static FieldInfo _egoTransitionActiveField;
        private static bool _egoTransitionActiveSaved;
        private static FieldInfo _egoTransitionTargetField;
        private static bool _egoTransitionTargetSaved;
        private static bool _egoCameraStateSuspended;
        private static bool _manualFlightInputActive;
        private static bool _idleHoldActive;
        private static Vector3 _idleHoldPosition;
        private static Quaternion _idleHoldRotation = Quaternion.identity;
        private static bool _menuCameraLocked;
        private static Vector3 _menuLockedCameraPosition;
        private static Quaternion _menuLockedCameraRotation = Quaternion.identity;
        private static float _menuLockedNearClip = 0.02f;
        private static bool _lastOwnedCameraPoseValid;
        private static Vector3 _lastOwnedCameraPosition;
        private static Quaternion _lastOwnedCameraRotation = Quaternion.identity;
        private static float _lastOwnedNearClip = 0.02f;
        private static Func<float> _wingbeatIntensityProvider;
        private static Func<string, Vector3> _egoFineTuneProvider;
        private static Action<string, Vector3> _egoFineTuneSaver;
        private static KeyCode _egoFineTuneHeldKey = KeyCode.None;
        private static bool _egoFineTuneHeldVertical;
        private static float _egoFineTuneHeldSince;
        private static float _egoFineTuneNextRepeatAt;
        private const float FrostSalvoCooldown = 6f;
        private const float FrostSalvoStamina = 20f;
        private const float FrostProjectileSpeed = 15f;
        private const float FrostProjectileDamage = 90f;
        private const float FrostBoltCooldown = 3.5f;
        private const float FrostBoltStamina = 18f;
        private const float FrostBurstCooldown = 7.5f;
        private const float FrostBurstStamina = 28f;
        private static float _nextFrostSalvoAt;
        private static float _nextFrostBoltAt;
        private static float _nextFrostBurstAt;
        private static float _nextTrollGroundSlamAt;
        private static float _nextTrollRoarAt;
        private static float _pendingTrollUnarmedPunchAt;
        private static float _pendingTrollGroundSlamAt;
        private static float _pendingTrollRoarAt;
        private static float _trollNativeAttackImpactAt;
        private static bool _replayingTrollNativeHit;
        private static bool _bypassTrollOutgoingDelay;
        private static readonly List<PendingTrollNativeHit> _pendingTrollNativeHits = new List<PendingTrollNativeHit>(8);
        private static bool _pendingTrollGroundSlamHasLog;
        private static int _pendingTrollGroundSlamBoundLevel;
        private static int _pendingTrollRoarBoundLevel;
        private static bool _trollHasLog;
        private static int _trollLogDurability;
        private static string _trollHeldLogPrefabName = string.Empty;
        private static Renderer[] _trollLogRenderers = Array.Empty<Renderer>();
        private static bool[] _trollLogRendererDefaultStates = Array.Empty<bool>();
        private static float _nextTrollLogHintAt;
        private static float _nextTrollLogProbeAt;
        private static GameObject _trollNearbyLog;
        private static GameObject _trollHeldLogVisual;
        private static ItemDrop.ItemData _trollNativeLogWeapon;
        private static GameObject _trollNativeLogWeaponPrefab;
        private static float _nextSerpentBiteBurstAt;
        private static float _nextSerpentTailSweepAt;
        private static float _serpentBiteLungeUntil;
        private static Vector3 _serpentBiteLungeVelocity;
        private static bool _reportedFrostProjectileMissing;

        private static bool _werewolfComboActive;
        private static readonly List<Character> _werewolfComboTargets = new List<Character>(WerewolfComboMaxTargets);
        private static int _werewolfComboIndex;
        private static float _werewolfComboNextStepAt;
        private static float _nextWerewolfComboAt;
        private static float _nextWerewolfClawAt;
        private static readonly Collider[] _werewolfMeleeColliderBuffer = new Collider[48];
        // v4.0.0: Blood Hunt is activation-only, but its old SphereCastAll/RaycastAll path could
        // still produce a visible GC spike in a dense fight. Keep dedicated reusable hit buffers.
        private static readonly RaycastHit[] _werewolfAimHitBuffer = new RaycastHit[64];
        private static readonly RaycastHit[] _werewolfLosHitBuffer = new RaycastHit[64];
        private static readonly RaycastHit[] _werewolfGroundHitBuffer = new RaycastHit[32];
        private static readonly HashSet<Character> _werewolfMeleeInspected = new HashSet<Character>();
        private static Vector3 _werewolfComboOriginPosition;
        private static Quaternion _werewolfComboOriginRotation = Quaternion.identity;
        private static float _werewolfMovementJumpQueuedUntil;
        private static float _nextWerewolfMovementJumpAt;
        private static float _werewolfJumpBoostUntil;
        private static float _werewolfPounceUntil;
        private static Vector3 _werewolfPounceVelocity;
        private static Transform _werewolfVisualRoot;
        private static Quaternion _werewolfVisualLocalBaseline = Quaternion.identity;
        private static bool _werewolfVisualBaselineCaptured;
        // v0.7.1: Fenring's native Character locomotion always tries to face its move vector.
        // Keep animation intent native, but make physical translation + yaw authoritative here so
        // first-person S is true backwards movement instead of a hidden 180-degree turn.
        private static Vector3 _werewolfDesiredHorizontalVelocity = Vector3.zero;
        private static Quaternion _werewolfDesiredFacingRotation = Quaternion.identity;
        private static bool _werewolfAuthoritativeMoveActive;
        private static bool _werewolfNativeForwardMoveActive;
        // v0.7.5: preserve the known-good native Fenring motor only for pure forward W. Reverse,
        // strafe and diagonals never receive a native move vector; a late FixedUpdate MovePosition
        // owns signed translation while the Animator receives the intended local blend values.
        private static float _werewolfAnimForwardInput;
        private static float _werewolfAnimStrafeInput;
        private static bool _werewolfAnimFast;
        private static bool _werewolfAnimatorParamsReported;
        private static bool _werewolfSignedLocomotionReported;
        private static bool _werewolfAnimatorParamCacheReady;
        private static int _werewolfAnimForwardHash;
        private static int _werewolfAnimSideHash;
        private static int _werewolfAnimTurnHash;

        internal struct WerewolfSkillHudState
        {
            internal bool Visible;
            internal bool ComboActive;
            internal float ClawRemaining;
            internal float ClawDuration;
            internal float JumpRemaining;
            internal float JumpDuration;
            internal float ComboRemaining;
            internal float ComboDuration;
            internal int ComboIndex;
            internal int ComboTargets;
        }

        internal static WerewolfSkillHudState GetWerewolfSkillHudState()
        {
            WerewolfSkillHudState state = new WerewolfSkillHudState
            {
                ClawDuration = WerewolfClawCooldown,
                ComboDuration = WerewolfComboCooldown
            };

            if (!Active || !IsWerewolfForm || _transitionState != SymbiosisTransitionState.None || IsMountUiBlocking())
                return state;

            state.Visible = true;
            float now = Time.time;
            state.ClawRemaining = Mathf.Max(0f, _nextWerewolfClawAt - now);
            state.ComboRemaining = Mathf.Max(0f, _nextWerewolfComboAt - now);
            state.ComboActive = _werewolfComboActive;
            state.ComboIndex = Mathf.Clamp(_werewolfComboIndex, 0, WerewolfComboMaxTargets);
            state.ComboTargets = Mathf.Clamp(_werewolfComboTargets.Count, 0, WerewolfComboMaxTargets);

            try
            {
                EnsureNativeDrakeAttackCatalog(_mountedDrake);
                NativeDrakeAttackEntry jump = GetPreferredWerewolfNativeAttack(true);
                if (jump != null && jump.Item != null && jump.Item.m_shared != null)
                {
                    float interval = Mathf.Max(0f, jump.Item.m_shared.m_aiAttackInterval);
                    state.JumpDuration = interval;
                    if (interval > 0f)
                        state.JumpRemaining = Mathf.Max(0f, interval - (now - jump.Item.m_lastAttackTime));
                }
            }
            catch { }

            return state;
        }

        internal struct FormSkillHudState
        {
            internal bool Visible;
            internal string Header;
            internal string Key1;
            internal string Name1;
            internal float Remaining1;
            internal float Duration1;
            internal bool Active1;
            internal string ActiveText1;
            internal string Key2;
            internal string Name2;
            internal float Remaining2;
            internal float Duration2;
            internal bool Active2;
            internal string ActiveText2;
            internal string Key3;
            internal string Name3;
            internal float Remaining3;
            internal float Duration3;
            internal bool Active3;
            internal string ActiveText3;
        }

        internal static FormSkillHudState GetFormSkillHudState()
        {
            FormSkillHudState state = new FormSkillHudState();
            if (!Active || _transitionState != SymbiosisTransitionState.None || IsMountUiBlocking())
                return state;

            state.Visible = true;
            float now = Time.time;

            if (IsWerewolfForm)
            {
                WerewolfSkillHudState wolf = GetWerewolfSkillHudState();
                state.Header = "WERWOLF" + GetBoundStarSuffix();
                state.Key1 = "LMB"; state.Name1 = "KLAUE";
                state.Remaining1 = wolf.ClawRemaining; state.Duration1 = wolf.ClawDuration;
                state.Key2 = "MMB"; state.Name2 = "SPRUNG-ATK";
                state.Remaining2 = wolf.JumpRemaining; state.Duration2 = wolf.JumpDuration;
                state.Key3 = "RMB+LMB"; state.Name3 = "BLUTJAGD";
                state.Remaining3 = wolf.ComboRemaining; state.Duration3 = wolf.ComboDuration;
                state.Active3 = wolf.ComboActive;
                if (wolf.ComboActive)
                    state.ActiveText3 = wolf.ComboTargets > 0
                        ? $"{Mathf.Clamp(wolf.ComboIndex <= 0 ? 1 : wolf.ComboIndex, 1, wolf.ComboTargets)}/{wolf.ComboTargets}"
                        : "AKTIV";
                return state;
            }

            if (IsDrakeForm)
            {
                // v3.1.0: expose the new dive-momentum flight loop directly in the existing
                // zero-scan combat HUD. During Shift dive the player sees STURZFLUG; after a
                // qualifying dive the remaining free-climb window is shown as SCHWUNG x.xs.
                // This reads only local runtime state and therefore adds no world/physics scan.
                string flightState = string.Empty;
                if (_drakeFastFlightActive)
                {
                    flightState = " | STURZFLUG";
                }
                else if (Time.time < _drakeFreeClimbUntil)
                {
                    float momentumRemaining = Mathf.Max(0f, _drakeFreeClimbUntil - Time.time);
                    flightState = $" | SCHWUNG {momentumRemaining:0.0}s";
                }
                state.Header = "DRAKE" + GetBoundStarSuffix() + flightState;
                state.Key1 = "LMB"; state.Name1 = "FROST";
                state.Remaining1 = Mathf.Max(0f, _nextFrostSalvoAt - now); state.Duration1 = FrostSalvoCooldown;
                state.Key2 = "RMB"; state.Name2 = "EISBOLZEN";
                state.Remaining2 = Mathf.Max(0f, _nextFrostBoltAt - now); state.Duration2 = FrostBoltCooldown;
                state.Key3 = "MMB"; state.Name3 = "EISFAECHER";
                state.Remaining3 = Mathf.Max(0f, _nextFrostBurstAt - now); state.Duration3 = FrostBurstCooldown;
                return state;
            }

            if (IsTrollForm)
            {
                // v2.9.0: make the Troll1/Troll2 body state readable at a glance. The log already
                // has real 12-swing durability; surface it in the existing zero-scan HUD instead of
                // forcing the player to count swings or wait for transient status messages.
                string logState = _trollHasLog
                    ? $" | STAMM {Mathf.Clamp(_trollLogDurability, 0, TrollLogMaxDurability)}/{TrollLogMaxDurability}" + (_trollLogDurability <= 3 ? " !" : string.Empty)
                    : " | E: STAMM";
                state.Header = "TROLL" + GetBoundStarSuffix() + logState;
                state.Key1 = "LMB"; state.Name1 = _trollHasLog ? "STAMMSCHLAG" : "SCHLAG";
                state.Key2 = "RMB"; state.Name2 = _trollHasLog ? "STAMM-SLAM" : "BODENSCHLAG";
                state.Remaining2 = Mathf.Max(0f, _nextTrollGroundSlamAt - now); state.Duration2 = TrollGroundSlamCooldown;
                state.Key3 = "MMB"; state.Name3 = "GEBRUELL";
                state.Remaining3 = Mathf.Max(0f, _nextTrollRoarAt - now); state.Duration3 = TrollRoarCooldown;
                return state;
            }

            if (IsSerpentForm)
            {
                state.Header = "SEESCHLANGE" + GetBoundStarSuffix();
                state.Key1 = "LMB"; state.Name1 = "BISS";
                state.Key2 = "RMB"; state.Name2 = "BISSSTOSS";
                state.Remaining2 = Mathf.Max(0f, _nextSerpentBiteBurstAt - now); state.Duration2 = SerpentBiteBurstCooldown;
                state.Key3 = "MMB"; state.Name3 = "SCHWANZFEGER";
                state.Remaining3 = Mathf.Max(0f, _nextSerpentTailSweepAt - now); state.Duration3 = SerpentTailSweepCooldown;
                return state;
            }

            state.Visible = false;
            return state;
        }

        private sealed class NativeDrakeAttackEntry
        {
            internal ItemDrop.ItemData Item;
            internal bool Secondary;
            internal string Label;
            internal string Animation;
        }

        // Native Hatchling weapon attacks are discovered from the possessed Humanoid inventory
        // after spawn/equipment initialization. Slots intentionally remain unique: LMB=primary,
        // RMB=secondary, MMB=third. Missing slots stay empty instead of cloning one attack three times.
        private static readonly List<NativeDrakeAttackEntry>[] _nativeAttackSlots =
        {
            new List<NativeDrakeAttackEntry>(),
            new List<NativeDrakeAttackEntry>(),
            new List<NativeDrakeAttackEntry>()
        };
        private static readonly int[] _nativeAttackNextIndex = { 0, 0, 0 };
        private static Character _nativeAttackCatalogDrake;
        private static float _nextNativeAttackProbeAt;
        private static float _nativeAttackProbeUntil;
        private static bool _nativeAttackCatalogLogged;
        private static float _nextNativeAttackAt;
        private static float _nextTemporaryAvatarCleanupAt;
        private static float _nextRemoteVisibilityRefreshAt;
        private static float _nextInactiveRecoveryCheckAt;
        private static long _inactiveRecoveryHandshakePlayerId;

        private sealed class RemotePlayerVisualState
        {
            internal Renderer[] Renderers = Array.Empty<Renderer>();
            internal bool[] EnabledStates = Array.Empty<bool>();
            internal Collider[] Colliders = Array.Empty<Collider>();
            internal bool[] ColliderStates = Array.Empty<bool>();
        }

        // Multiplayer: the owner publishes a tiny ZDO flag on the real Player while the Drake is
        // the visible network body. Every modded client consumes that flag and hides only that
        // remote Player's renderers locally. The Player object itself remains alive/networked for
        // ownership, streaming, chat/name identity and vanilla profile state.
        private static readonly Dictionary<Player, RemotePlayerVisualState> _remoteSymbiosisVisuals =
            new Dictionary<Player, RemotePlayerVisualState>();
        private static readonly List<Player> _remoteVisibilitySeen = new List<Player>(8);
        private static readonly List<Player> _remoteVisibilityStale = new List<Player>(8);

        private static long _legacyCleanupPlayerId;
        private static Vector3 _legacyCleanupSpawnAnchor;
        private static bool _legacyCleanupAnchorCaptured;
        private static bool _legacyCleanupCompletedForSession;
        private static float _legacyCleanupWindowUntil;
        private static float _nextLegacyCleanupAt;
        private const float LegacyCleanupWindowSeconds = 45f;
        private const float LegacyCleanupRadius = 6f;
        private const float MaxSafeDismountDrop = 45f;

        // Local-player lifecycle tracking. Valheim may replace the Player component/object on
        // respawn or world transitions. Symbiosis must never keep references/camera locks from
        // the dead Player object or accidentally re-enter the Drake body after respawn.
        private static Player _observedLocalPlayer;
        private static bool _observedLocalPlayerDead;

        private enum SymbiosisTransitionState
        {
            None,
            Entering,
            Exiting
        }

        private const float TransformationDuration = 1.05f;
        // v4.2.0 release readiness: transitions should never be able to leave the player trapped
        // between bodies/cameras. A generous watchdog only fires when the normal 1.05 s hand-off
        // has clearly stalled because of a scene/pause/mod interaction.
        private const float TransformationWatchdogSeconds = 4.0f;
        private static SymbiosisTransitionState _transitionState;
        private static float _transitionStartedAt;
        private static float _nextActiveIntegrityCheckAt;
        private static Vector3 _transitionCameraStartPosition;
        private static Quaternion _transitionCameraStartRotation = Quaternion.identity;
        private static Vector3 _entryPlayerAnchorPosition;
        private static Quaternion _entryPlayerAnchorRotation = Quaternion.identity;
        private static Vector3 _exitPlayerAnchorPosition;
        private static Quaternion _exitPlayerAnchorRotation = Quaternion.identity;
        private static Vector3 _humanCameraLocalOffset;
        private static Quaternion _humanCameraLocalRotation = Quaternion.identity;
        private static bool _humanCameraRigCaptured;
        private static string _pendingDismountMessage = string.Empty;
        private static Transform _transitionDrakeVisual;
        private static Vector3 _transitionDrakeOriginalScale = Vector3.one;
        private static bool _transitionDrakeScaleCaptured;
        private static bool _transitionUsesTemporaryAvatar;
        private static bool _playerVisualsHidden;
        private static bool _transitionHandoffFxPlayed;
        private static Material _transformationParticleMaterial;
        private static Texture2D _transformationParticleTexture;
        private static AudioClip _transformationWhooshClip;
        private static AudioClip _transformationSnapClip;

        internal static int ApiRevision => 5;
        internal static bool Active => _mountedDrake != null && _rider != null;
        internal static bool IsLocalSerpentSymbiosis(Player player) => Active && IsSerpentForm && player != null && player == _rider;
        internal static string ActiveFormId => Active ? _activeFormId : string.Empty;

        internal static float GetWerewolfVisionWeight()
        {
            if (!Active || !IsWerewolfForm) return 0f;
            if (_transitionState == SymbiosisTransitionState.None) return 1f;

            float t = Mathf.Clamp01((Time.unscaledTime - _transitionStartedAt) / TransformationDuration);
            // Let the colour perception morph with the body rather than snapping at the first frame.
            float eased = Smooth01(Mathf.InverseLerp(0.10f, 0.90f, t));
            return _transitionState == SymbiosisTransitionState.Exiting ? 1f - eased : eased;
        }
        private static bool IsSerpentForm => string.Equals(_activeFormId, SerpentFormId, StringComparison.OrdinalIgnoreCase);
        private static bool IsTrollForm => string.Equals(_activeFormId, TrollFormId, StringComparison.OrdinalIgnoreCase);
        private static bool IsWerewolfForm => string.Equals(_activeFormId, WerewolfFormId, StringComparison.OrdinalIgnoreCase);
        private static bool IsDrakeForm => string.Equals(_activeFormId, DrakeFormId, StringComparison.OrdinalIgnoreCase);
        private static string ActiveFormDisplayName => IsSerpentForm ? "Seeschlangen" : IsTrollForm ? "Troll" : IsWerewolfForm ? "Werwolf" : "Drake";
        internal static bool BlockLocalPlayerDamage =>
            !_forwardingSymbiosisDamageToPlayer &&
            (Active || (Player.m_localPlayer != null && Time.time < _playerDamageImmuneUntil));

        /// <summary>
        /// The temporary Drake is only the visible/controlled body of the local player.
        /// It must never own a second health pool. Damage that hits this avatar is therefore
        /// rerouted into the real Player Character so Valheim applies the player's current
        /// health, armour, resistances, food state and death handling exactly once.
        /// </summary>
        internal static bool TryForwardTemporaryAvatarDamage(Character target, HitData hit)
        {
            if (target == null || hit == null || !IsTemporaryAvatar(target)) return false;

            Player rider = _rider;
            if (rider == null || rider != Player.m_localPlayer || rider.IsDead())
            {
                // The temporary body must never fall back to an independent health pool.
                return true;
            }

            // Ignore accidental self-hits from the controlled Drake's own projectiles/attacks.
            Character attacker = null;
            try { attacker = hit.GetAttacker(); } catch { }
            if (attacker == target) return true;

            // During the sub-second Blood Hunt blink chain the avatar is between discrete positions.
            // Consuming incoming hits only for that short sequence prevents stale melee/AoE contacts from
            // hitting a body that has already blinked away. It is not a persistent Werewolf defence buff.
            if (IsWerewolfForm && _werewolfComboActive) return true;

            bool riderDied = false;
            TrollHitSnapshot trollHit = default;
            ElementalHitSnapshot elementalHit = default;
            try
            {
                // Keep the real Player exactly inside the controlled creature before Valheim resolves
                // the hit. If this hit is lethal, vanilla death/tombstone logic therefore uses the
                // actual symbiosis-body position instead of a stale hidden-player position.
                SyncRiderToSeat();

                // Troll is the deliberate tank/breacher form. Reduce physical components before
                // Player.Damage so the player's own armour/resistances still resolve normally after
                // this form bonus. The original HitData is restored immediately afterwards because
                // the same hit object must never leak Troll mitigation to any other target.
                if (IsTrollForm)
                    trollHit = ApplyTrollTankMitigation(hit);
                else if (IsDrakeForm)
                    elementalHit = ApplyElementalFormMitigation(hit, HitFrostField, GetElementalPassiveMultiplier(_activeBoundLevel));
                else if (IsSerpentForm)
                    elementalHit = ApplyElementalFormMitigation(hit, HitPoisonField, GetElementalPassiveMultiplier(_activeBoundLevel));

                _forwardingSymbiosisDamageToPlayer = true;
                rider.Damage(hit);
                riderDied = rider.IsDead();
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Symbiosis damage forwarding failed safely: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (trollHit.Active) RestoreTrollTankMitigation(hit, trollHit);
                if (elementalHit.Active) RestoreElementalFormMitigation(hit, elementalHit);
                _forwardingSymbiosisDamageToPlayer = false;
            }

            if (riderDied)
                HandleRiderDeathDuringSymbiosis();

            return true;
        }

        internal static bool TryDeferTrollOutgoingHit(Character target, HitData hit)
        {
            if (!Active || !IsTrollForm || _mountedDrake == null || target == null || hit == null || _replayingTrollNativeHit || _bypassTrollOutgoingDelay)
                return false;

            Character attacker = null;
            try { attacker = hit.GetAttacker(); } catch { }
            if (attacker != _mountedDrake || target == _mountedDrake || target == _rider) return false;

            // Troll native attacks can resolve their HitData on the input frame even though the
            // visible fist/log has not reached the target yet. Capture that hit and replay it at
            // the authored impact point instead of showing damage numbers on mouse-down.
            HitData clone = CloneTrollHitData(hit);
            if (clone == null) return false;

            float fallbackDelay = _trollHasLog ? TrollLogSwingImpactDelay : TrollUnarmedPunchImpactDelay;
            float applyAt = _trollNativeAttackImpactAt > Time.time
                ? _trollNativeAttackImpactAt
                : Time.time + fallbackDelay;
            _pendingTrollNativeHits.Add(new PendingTrollNativeHit { Target = target, Hit = clone, ApplyAt = applyAt });
            return true;
        }

        private static HitData CloneTrollHitData(HitData source)
        {
            if (source == null) return null;
            try
            {
                HitData clone = new HitData();
                clone.m_pushForce = source.m_pushForce;
                clone.m_staggerMultiplier = source.m_staggerMultiplier;
                clone.m_point = source.m_point;
                clone.m_dir = source.m_dir;

                if (HitDamageField != null)
                {
                    object srcDamage = HitDamageField.GetValue(source);
                    object dstDamage = HitDamageField.GetValue(clone);
                    if (srcDamage != null && dstDamage != null)
                    {
                        FieldInfo[] fields = dstDamage.GetType().GetFields(InstanceFlags);
                        for (int i = 0; i < fields.Length; i++)
                        {
                            FieldInfo dst = fields[i];
                            if (dst == null || dst.FieldType != typeof(float)) continue;
                            FieldInfo src = srcDamage.GetType().GetField(dst.Name, InstanceFlags);
                            if (src == null || src.FieldType != typeof(float)) continue;
                            dst.SetValue(dstDamage, (float)src.GetValue(srcDamage));
                        }
                        HitDamageField.SetValue(clone, dstDamage);
                    }
                }
                clone.SetAttacker(_mountedDrake);
                return clone;
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke("Troll native hit clone failed safely: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        internal static void ApplyOutgoingSymbiosisDamage(Character target, HitData hit)
        {
            if (!Active || target == null || hit == null || _mountedDrake == null) return;
            Character attacker = null;
            try { attacker = hit.GetAttacker(); } catch { }
            if (attacker != _mountedDrake) return;

            if (IsWerewolfForm)
            {
                float multiplier = GetWerewolfStarDamageMultiplier(_activeBoundLevel);
                try
                {
                    if (HitDamageField == null) return;
                    object damage = HitDamageField.GetValue(hit);
                    if (damage == null) return;
                    FieldInfo[] fields = damage.GetType().GetFields(InstanceFlags);
                    for (int i = 0; i < fields.Length; i++)
                    {
                        FieldInfo field = fields[i];
                        if (field == null || field.FieldType != typeof(float)) continue;
                        float value = (float)field.GetValue(damage);
                        field.SetValue(damage, value * multiplier);
                    }
                    HitDamageField.SetValue(hit, damage);
                }
                catch (Exception ex)
                {
                    _debugLog?.Invoke("Werewolf outgoing damage scaling failed safely: " + ex.GetType().Name + ": " + ex.Message);
                }
                return;
            }

            if (IsTrollForm && _trollHasLog)
            {
                try
                {
                    object damage = HitDamageField?.GetValue(hit);
                    if (damage == null) return;
                    if (HitBluntField != null && HitBluntField.FieldType == typeof(float))
                    {
                        float blunt = (float)HitBluntField.GetValue(damage);
                        HitBluntField.SetValue(damage, blunt * TrollLogBluntMultiplier);
                    }
                    if (HitChopField != null && HitChopField.FieldType == typeof(float))
                    {
                        float chop = (float)HitChopField.GetValue(damage);
                        HitChopField.SetValue(damage, chop + TrollLogBonusChop);
                    }
                    HitDamageField?.SetValue(hit, damage);
                }
                catch (Exception ex)
                {
                    _debugLog?.Invoke("Troll held-log damage bonus failed safely: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }


        private static float GetElementalPassiveMultiplier(int boundLevel)
        {
            if (boundLevel >= 3) return DrakeFrostDamageMultiplier2Star;
            if (boundLevel == 2) return DrakeFrostDamageMultiplier1Star;
            return DrakeFrostDamageMultiplier0Star;
        }

        private static ElementalHitSnapshot ApplyElementalFormMitigation(HitData hit, FieldInfo field, float multiplier)
        {
            ElementalHitSnapshot snapshot = default;
            if (hit == null || field == null || field.FieldType != typeof(float)) return snapshot;
            try
            {
                object damage = HitDamageField?.GetValue(hit);
                if (damage == null) return snapshot;
                snapshot.Original = (float)field.GetValue(damage);
                snapshot.HasElement = true;
                snapshot.Field = field;
                field.SetValue(damage, snapshot.Original * Mathf.Clamp(multiplier, 0f, 1f));
                HitDamageField?.SetValue(hit, damage);
                snapshot.Active = true;
            }
            catch { }
            return snapshot;
        }

        private static void RestoreElementalFormMitigation(HitData hit, ElementalHitSnapshot snapshot)
        {
            if (hit == null || !snapshot.Active || !snapshot.HasElement || snapshot.Field == null) return;
            try
            {
                object damage = HitDamageField?.GetValue(hit);
                if (damage == null) return;
                snapshot.Field.SetValue(damage, snapshot.Original);
                HitDamageField?.SetValue(hit, damage);
            }
            catch { }
        }

        private static float GetWerewolfStarDamageMultiplier(int boundLevel)
        {
            // Valheim level 1 = 0 stars, level 2 = 1 star, level 3 = 2 stars.
            if (boundLevel >= 3) return 2.50f;
            if (boundLevel == 2) return 1.75f;
            return 1.50f;
        }

        private static float GetBoundAbilityMultiplier(int boundLevel)
        {
            if (boundLevel >= 3) return 1.60f;
            if (boundLevel == 2) return 1.25f;
            return 1.00f;
        }

        private static string GetBoundStarSuffix()
        {
            int stars = Mathf.Clamp(_activeBoundLevel - 1, 0, 2);
            return $"  {stars}★";
        }

        private static TrollHitSnapshot ApplyTrollTankMitigation(HitData hit)
        {
            TrollHitSnapshot snapshot = default;
            if (hit == null) return snapshot;

            try
            {
                object damage = HitDamageField?.GetValue(hit);
                if (damage != null)
                {
                    ScaleTrollDamageField(damage, HitBluntField, TrollPhysicalDamageMultiplier, ref snapshot.HasBlunt, ref snapshot.Blunt);
                    ScaleTrollDamageField(damage, HitSlashField, TrollPhysicalDamageMultiplier, ref snapshot.HasSlash, ref snapshot.Slash);
                    ScaleTrollDamageField(damage, HitPierceField, TrollPhysicalDamageMultiplier, ref snapshot.HasPierce, ref snapshot.Pierce);
                    ScaleTrollDamageField(damage, HitChopField, TrollPhysicalDamageMultiplier, ref snapshot.HasChop, ref snapshot.Chop);
                    ScaleTrollDamageField(damage, HitPickaxeField, TrollPhysicalDamageMultiplier, ref snapshot.HasPickaxe, ref snapshot.Pickaxe);
                    HitDamageField?.SetValue(hit, damage);
                }

                if (HitPushForceField != null && HitPushForceField.FieldType == typeof(float))
                {
                    snapshot.PushForce = (float)HitPushForceField.GetValue(hit);
                    snapshot.HasPushForce = true;
                    HitPushForceField.SetValue(hit, snapshot.PushForce * TrollPushForceMultiplier);
                }

                if (HitStaggerMultiplierField != null && HitStaggerMultiplierField.FieldType == typeof(float))
                {
                    snapshot.Stagger = (float)HitStaggerMultiplierField.GetValue(hit);
                    snapshot.HasStagger = true;
                    HitStaggerMultiplierField.SetValue(hit, snapshot.Stagger * TrollStaggerMultiplier);
                }

            }
            catch
            {
                // Failing open is safer than breaking Player.Damage on a game update. Captured
                // fields are still restored below if anything was already changed.
            }

            snapshot.Active = snapshot.HasBlunt || snapshot.HasSlash || snapshot.HasPierce || snapshot.HasChop ||
                              snapshot.HasPickaxe || snapshot.HasPushForce || snapshot.HasStagger;
            return snapshot;
        }

        private static void ScaleTrollDamageField(object damage, FieldInfo field, float multiplier, ref bool captured, ref float original)
        {
            if (damage == null || field == null || field.FieldType != typeof(float)) return;
            original = (float)field.GetValue(damage);
            captured = true;
            field.SetValue(damage, original * multiplier);
        }

        private static void RestoreTrollTankMitigation(HitData hit, TrollHitSnapshot snapshot)
        {
            if (hit == null || !snapshot.Active) return;
            try
            {
                object damage = HitDamageField?.GetValue(hit);
                if (damage != null)
                {
                    RestoreTrollDamageField(damage, HitBluntField, snapshot.HasBlunt, snapshot.Blunt);
                    RestoreTrollDamageField(damage, HitSlashField, snapshot.HasSlash, snapshot.Slash);
                    RestoreTrollDamageField(damage, HitPierceField, snapshot.HasPierce, snapshot.Pierce);
                    RestoreTrollDamageField(damage, HitChopField, snapshot.HasChop, snapshot.Chop);
                    RestoreTrollDamageField(damage, HitPickaxeField, snapshot.HasPickaxe, snapshot.Pickaxe);
                    HitDamageField?.SetValue(hit, damage);
                }
                if (snapshot.HasPushForce && HitPushForceField != null) HitPushForceField.SetValue(hit, snapshot.PushForce);
                if (snapshot.HasStagger && HitStaggerMultiplierField != null) HitStaggerMultiplierField.SetValue(hit, snapshot.Stagger);
            }
            catch { }
        }

        private static void RestoreTrollDamageField(object damage, FieldInfo field, bool captured, float original)
        {
            if (!captured || damage == null || field == null || field.FieldType != typeof(float)) return;
            field.SetValue(damage, original);
        }

        private static bool ConsumeTrollSprintStamina(Player player, float dt)
        {
            if (player == null) return false;
            float cost = TrollSprintStaminaPerSecond * Mathf.Clamp(dt, 0.001f, 0.05f);
            try
            {
                if (!player.HaveStamina(cost)) return false;
                player.UseStamina(cost);
                return true;
            }
            catch
            {
                // Movement must remain usable if Valheim changes the stamina API.
                return true;
            }
        }

        private static bool ConsumeWerewolfSprintStamina(Player player, float dt)
        {
            if (player == null) return false;
            float cost = WerewolfSprintStaminaPerSecond * Mathf.Clamp(dt, 0.001f, 0.05f);
            try
            {
                if (!player.HaveStamina(cost)) return false;
                player.UseStamina(cost);
                return true;
            }
            catch { return true; }
        }

        private static bool ConsumeMovementBoostStamina(Player player, float costPerSecond, float dt)
        {
            if (player == null || costPerSecond <= 0f) return false;
            float cost = costPerSecond * Mathf.Clamp(dt, 0.001f, 0.05f);
            try
            {
                if (!player.HaveStamina(cost)) return false;
                player.UseStamina(cost);
                return true;
            }
            catch
            {
                // Fail open on API drift: never soft-lock form movement.
                return true;
            }
        }

        internal static void PrepareForLocalPlayerDeath(Player player)
        {
            if (!Active || player == null || _rider != player) return;
            HandleRiderDeathDuringSymbiosis();
        }

        private static void HandleRiderDeathDuringSymbiosis()
        {
            // Death must never use the normal animated dismount path. That path searches for a safe
            // landing point and moves the Player, which would be wrong for tombstones/death position.
            // Also clear the short transformation immunity window so it cannot leak into respawn.
            _playerDamageImmuneUntil = 0f;
            _debugLog?.Invoke("Drake symbiosis ended immediately because the real Player died; death position remains the Drake position.");
            ForceDismount(false);
        }

        private static void UpdateEgoFineTuneInput()
        {
            if (!Active || !_drakeEgoActive || _transitionState != SymbiosisTransitionState.None || IsMountUiBlocking())
            {
                ResetEgoFineTuneHold();
                return;
            }
            if (!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift))
            {
                ResetEgoFineTuneHold();
                return;
            }

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            KeyCode key = KeyCode.None;
            bool vertical = false;
            Vector3 axis = Vector3.zero;

            // Priority keeps one axis deterministic while a key is held.
            if (Input.GetKey(KeyCode.PageUp))      { key = KeyCode.PageUp;   vertical = true; axis = Vector3.up; }
            else if (Input.GetKey(KeyCode.PageDown)) { key = KeyCode.PageDown; vertical = true; axis = Vector3.down; }
            else if ((ctrl || alt) && Input.GetKey(KeyCode.UpArrow))   { key = KeyCode.UpArrow;   vertical = true; axis = Vector3.up; }
            else if ((ctrl || alt) && Input.GetKey(KeyCode.DownArrow)) { key = KeyCode.DownArrow; vertical = true; axis = Vector3.down; }
            else if (Input.GetKey(KeyCode.UpArrow))    { key = KeyCode.UpArrow;    axis = Vector3.forward; }
            else if (Input.GetKey(KeyCode.DownArrow))  { key = KeyCode.DownArrow;  axis = Vector3.back; }
            else if (Input.GetKey(KeyCode.RightArrow)) { key = KeyCode.RightArrow; axis = Vector3.right; }
            else if (Input.GetKey(KeyCode.LeftArrow))  { key = KeyCode.LeftArrow;  axis = Vector3.left; }

            if (key == KeyCode.None)
            {
                ResetEgoFineTuneHold();
                return;
            }

            float now = Time.unscaledTime;
            bool newHold = key != _egoFineTuneHeldKey || vertical != _egoFineTuneHeldVertical;
            if (newHold)
            {
                _egoFineTuneHeldKey = key;
                _egoFineTuneHeldVertical = vertical;
                _egoFineTuneHeldSince = now;
                _egoFineTuneNextRepeatAt = now + 0.28f;
                ApplyEgoFineTuneDelta(axis * 0.01f); // first tick: always 1 cm
                return;
            }

            if (now < _egoFineTuneNextRepeatAt) return;

            float held = now - _egoFineTuneHeldSince;
            float step;
            float repeat;
            if (held < 0.80f)
            {
                step = 0.01f; // first few repeat ticks: still precise
                repeat = 0.11f;
            }
            else if (held < 1.80f)
            {
                step = 0.025f;
                repeat = 0.085f;
            }
            else if (held < 3.00f)
            {
                step = 0.05f;
                repeat = 0.070f;
            }
            else
            {
                step = 0.10f;
                repeat = 0.060f;
            }

            _egoFineTuneNextRepeatAt = now + repeat;
            ApplyEgoFineTuneDelta(axis * step);
        }

        private static void ResetEgoFineTuneHold()
        {
            _egoFineTuneHeldKey = KeyCode.None;
            _egoFineTuneHeldVertical = false;
            _egoFineTuneHeldSince = 0f;
            _egoFineTuneNextRepeatAt = 0f;
        }

        private static void ApplyEgoFineTuneDelta(Vector3 delta)
        {
            if (delta == Vector3.zero) return;
            string form = ActiveFormIdForSettings;
            Vector3 current = _egoFineTuneProvider != null ? _egoFineTuneProvider(form) : Vector3.zero;
            Vector3 next = current + delta;
            next.x = Mathf.Clamp(next.x, -1.50f, 1.50f);
            next.y = Mathf.Clamp(next.y, -1.50f, 1.50f);
            next.z = Mathf.Clamp(next.z, -1.50f, 1.50f);
            _egoFineTuneSaver?.Invoke(form, next);

            string msg = $"{ActiveFormDisplayName}-Ego Feinjustierung: X={next.x:+0.00;-0.00;0.00}m  Y={next.y:+0.00;-0.00;0.00}m  Z={next.z:+0.00;-0.00;0.00}m";
            _status?.Invoke(msg);
            _debugLog?.Invoke(msg + " | Halten beschleunigt automatisch: zuerst 1cm, dann 2,5cm, 5cm und 10cm pro Tick. Shift+Up/Down=vor/zurueck, Shift+Left/Right=seitlich, Shift+PageUp/PageDown=hoch/runter.");
        }

        internal static void SetWingbeatIntensityProvider(Func<float> provider)
        {
            _wingbeatIntensityProvider = provider;
        }

        internal static void SetEgoFineTuneBridge(Func<string, Vector3> provider, Action<string, Vector3> saver)
        {
            _egoFineTuneProvider = provider;
            _egoFineTuneSaver = saver;
        }

        internal static bool IsEgoCameraActive => Active && _drakeEgoActive;

        internal static bool TrySetExternalEgoCameraMode(bool ego)
        {
            if (!Active || _mountedDrake == null || _rider == null) return false;
            _drakeEgoActive = ego;
            if (!ego) RestoreDrakeVisualsAfterEgoRender();
            _thirdCameraSmoothInitialized = false;
            _egoPositionSmoothInitialized = false;
            _egoPositionSmoothFrame = -1;
            _thirdCameraSmoothFrame = -1;
            _thirdChaseYawInitialized = false;
            _wingbeatSmoothInitialized = false;
            _smoothedWingbeatMeters = 0f;
            SaveSymbiosisCameraMode(_rider, ego);
            SaveGlobalPerspective(_rider, ego);
            _debugLog?.Invoke($"External symbiosis camera handoff applied: {(ego ? "Ego" : "Third")}.");
            return true;
        }

        internal static string ActiveFormIdForSettings => IsTrollForm ? TrollFormId : IsWerewolfForm ? WerewolfFormId : IsSerpentForm ? SerpentFormId : DrakeFormId;

        internal static void Initialize(Action<string> debugLog, Action<string> status)
        {
            _debugLog = debugLog;
            _status = status;
            EnsureCameraCallbacks();
        }

        internal static void Shutdown()
        {
            try { PrepareForWorldExit(); }
            catch { ForceDismount(false); }
            _debugLog = null;
            _status = null;
            _reportedAttachApiMissing = false;
            if (_observedLocalPlayer != null) SetPlayerSymbiosisNetworkState(_observedLocalPlayer, false);
            RestoreAllRemoteSymbiosisVisuals();
            _nextInactiveRecoveryCheckAt = 0f;
            _inactiveRecoveryHandshakePlayerId = 0L;
            _observedLocalPlayer = null;
            _observedLocalPlayerDead = false;
            RemoveCameraCallbacks();
        }

        internal static bool IsMounted(Character worker)
        {
            return worker != null && _mountedDrake == worker && _rider != null;
        }

        internal static bool IsTemporaryAvatar(Character worker)
        {
            return worker != null && _temporarySymbiosisAvatar != null && worker.gameObject == _temporarySymbiosisAvatar && IsMounted(worker);
        }

        internal static bool TryMount(Character worker, Player player, out string message)
        {
            message = string.Empty;
            if (worker == null || player == null || player != Player.m_localPlayer)
            {
                message = "Drake-Symbiose nicht verfuegbar.";
                return false;
            }

            if (IsMounted(worker))
            {
                Dismount("Drake-Symbiose beendet.");
                message = "Drake-Symbiose beendet.";
                return true;
            }

            if (Active)
            {
                Dismount("Drake-Symbiose beendet.");
                message = "Drake-Symbiose beendet.";
                return true;
            }

            string species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
            if (!string.Equals(species, "Drake", StringComparison.OrdinalIgnoreCase))
                return false;

            ZNetView nview = worker.GetComponent<ZNetView>();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (!WorkerZdo.IsWorker(zdo))
            {
                message = "Nur ein rekrutierter Drake kann als persoenlicher Mount verwendet werden.";
                return true;
            }

            if (zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != player.GetPlayerID())
            {
                message = "Dieser Drake gehoert einem anderen Spieler.";
                return true;
            }

            if (!nview.IsOwner())
            {
                message = "Drake ist noch REMOTE-owned. Bleib kurz daneben und drueck danach erneut.";
                return true;
            }

            if (WorkerZdo.HasCargo(zdo))
            {
                message = "Drake transportiert gerade Fracht. Erst abladen, dann Symbiose starten.";
                return true;
            }

            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state == WorkerState.MoveToTarget || state == WorkerState.Collect || state == WorkerState.Deliver || state == WorkerState.Work)
            {
                message = "Drake hat gerade einen aktiven Transportauftrag. Warte bis der Auftrag beendet ist.";
                return true;
            }

            return BeginSymbiosis(worker, player, false, out message);
        }


        private static GameObject ResolveTrollStatePrefab(bool armed)
        {
            GameObject cached = armed ? _troll2PrefabTemplate : _troll1PrefabTemplate;
            if (cached != null) return cached;

            ZNetScene scene = ZNetScene.instance;
            GameObject source = scene != null ? scene.GetPrefab("Troll") : null;
            if (source == null) return null;

            try
            {
                GameObject template = UnityEngine.Object.Instantiate(source);
                // Keep the network prefab name/hash identical to vanilla Troll; Troll1/Troll2 are runtime state templates.
                template.name = source.name;
                template.SetActive(false);
                bool configured = ConfigureTrollRandomSetVariant(template, armed);
                UnityEngine.Object.DontDestroyOnLoad(template);
                if (armed) _troll2PrefabTemplate = template;
                else _troll1PrefabTemplate = template;
                _debugLog?.Invoke($"Troll {(armed ? "Troll2/log" : "Troll1/unarmed")} runtime prefab prepared; randomSetsConfigured={configured}.");
                return template;
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Troll {(armed ? "Troll2" : "Troll1")} runtime prefab preparation failed safely: {ex.GetType().Name}: {ex.Message}");
                return source;
            }
        }

        private static bool ConfigureTrollRandomSetVariant(GameObject template, bool armed)
        {
            if (template == null) return false;
            Humanoid humanoid = template.GetComponent<Humanoid>();
            if (humanoid == null) return false;

            try
            {
                FieldInfo randomSetsField = null;
                Type type = humanoid.GetType();
                while (type != null && randomSetsField == null)
                {
                    randomSetsField = type.GetField("m_randomSets", InstanceFlags | BindingFlags.DeclaredOnly);
                    type = type.BaseType;
                }
                if (randomSetsField == null) return false;

                object rawSets = randomSetsField.GetValue(humanoid);
                System.Collections.IList sets = rawSets as System.Collections.IList;
                if (sets == null || sets.Count == 0) return false;

                List<object> keep = new List<object>();
                for (int i = 0; i < sets.Count; i++)
                {
                    object set = sets[i];
                    bool logSet = TrollRandomSetContainsLogAttack(set);
                    if ((armed && logSet) || (!armed && !logSet)) keep.Add(set);
                }
                if (keep.Count == 0) return false;

                Array array = rawSets as Array;
                if (array != null)
                {
                    Type elementType = array.GetType().GetElementType();
                    Array replacement = Array.CreateInstance(elementType, keep.Count);
                    for (int i = 0; i < keep.Count; i++) replacement.SetValue(keep[i], i);
                    randomSetsField.SetValue(humanoid, replacement);
                    return true;
                }

                if (sets.IsReadOnly || sets.IsFixedSize) return false;
                sets.Clear();
                for (int i = 0; i < keep.Count; i++) sets.Add(keep[i]);
                return true;
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke("Troll random-set variant filtering failed safely: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static bool TrollRandomSetContainsLogAttack(object set)
        {
            if (set == null) return false;
            try
            {
                Type type = set.GetType();
                FieldInfo[] fields = type.GetFields(InstanceFlags);
                for (int i = 0; i < fields.Length; i++)
                {
                    object value = null;
                    try { value = fields[i].GetValue(set); } catch { continue; }
                    if (ValueContainsTrollLogAttack(value)) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool ValueContainsTrollLogAttack(object value)
        {
            if (value == null) return false;
            GameObject go = value as GameObject;
            if (go != null) return IsTrollLogAttackPrefabName(go.name);
            ItemDrop drop = value as ItemDrop;
            if (drop != null) return IsTrollLogAttackPrefabName(drop.gameObject != null ? drop.gameObject.name : string.Empty);

            System.Collections.IEnumerable enumerable = value as System.Collections.IEnumerable;
            if (enumerable == null || value is string) return false;
            try
            {
                foreach (object entry in enumerable)
                {
                    GameObject itemGo = entry as GameObject;
                    if (itemGo != null && IsTrollLogAttackPrefabName(itemGo.name)) return true;
                    ItemDrop itemDrop = entry as ItemDrop;
                    if (itemDrop != null && IsTrollLogAttackPrefabName(itemDrop.gameObject != null ? itemDrop.gameObject.name : string.Empty)) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool IsTrollLogAttackPrefabName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string lower = name.ToLowerInvariant();
            return lower.Contains("troll_log_swing") || lower.Contains("troll_summoned_log_swing");
        }

        private static GameObject ResolveRemoteAvatarPrefab(string formId, string preferredPrefabName)
        {
            ZNetScene scene = ZNetScene.instance;
            if (scene == null) return null;

            if (!string.IsNullOrWhiteSpace(preferredPrefabName))
            {
                string preferred = NormalizeRuntimePrefabName(preferredPrefabName.Trim());
                GameObject preferredPrefab = scene.GetPrefab(preferred);
                if (preferredPrefab != null)
                {
                    _debugLog?.Invoke($"Remote {formId} avatar prefab resolved from bound necklace profile: {preferred}.");
                    return preferredPrefab;
                }
                _debugLog?.Invoke($"Bound {formId} necklace prefab '{preferred}' is unavailable; trying vanilla form fallback.");
            }

            if (string.Equals(formId, SerpentFormId, StringComparison.OrdinalIgnoreCase))
            {
                GameObject serpent = scene.GetPrefab("Serpent");
                if (serpent != null) _debugLog?.Invoke("Remote avatar prefab resolved: Serpent.");
                return serpent;
            }
            if (string.Equals(formId, TrollFormId, StringComparison.OrdinalIgnoreCase))
            {
                GameObject troll = scene.GetPrefab("Troll");
                if (troll != null) _debugLog?.Invoke("Remote avatar prefab resolved: Troll.");
                return troll;
            }
            if (string.Equals(formId, WerewolfFormId, StringComparison.OrdinalIgnoreCase))
            {
                GameObject fenring = scene.GetPrefab("Fenring");
                if (fenring != null) _debugLog?.Invoke("Remote avatar prefab resolved: Fenring/Werewolf.");
                return fenring;
            }

            GameObject prefab = scene.GetPrefab("Hatchling");
            if (prefab != null)
            {
                _debugLog?.Invoke("Remote avatar prefab resolved: Hatchling.");
                return prefab;
            }
            prefab = scene.GetPrefab("Drake");
            if (prefab != null)
            {
                _debugLog?.Invoke("Remote avatar prefab resolved through fallback: Drake.");
                return prefab;
            }
            return null;
        }

        internal static bool TryMountRemoteAvatar(Player player, out string message)
        {
            return TryMountBoundForm(player, DrakeFormId, "Hatchling", 1, out message);
        }

        internal static bool TryMountBoundAvatar(Player player, string boundPrefabName, int boundLevel, out string message)
        {
            // Legacy DrakeForm API retained for old necklace DLLs.
            return TryMountBoundForm(player, DrakeFormId, boundPrefabName, boundLevel, out message);
        }

        internal static bool TryMountBoundForm(Player player, string formId, string boundPrefabName, int boundLevel, out string message)
        {
            message = string.Empty;
            if (player == null || player != Player.m_localPlayer)
            {
                message = "Symbiose nicht verfuegbar.";
                return false;
            }

            bool serpent = string.Equals(formId, SerpentFormId, StringComparison.OrdinalIgnoreCase);
            bool troll = string.Equals(formId, TrollFormId, StringComparison.OrdinalIgnoreCase);
            bool werewolf = string.Equals(formId, WerewolfFormId, StringComparison.OrdinalIgnoreCase);
            bool drake = string.Equals(formId, DrakeFormId, StringComparison.OrdinalIgnoreCase);
            string formLabel = serpent ? "Seeschlangen" : troll ? "Troll" : werewolf ? "Werwolf" : "Drake";
            if (!serpent && !troll && !werewolf && !drake)
            {
                message = "Unbekannte Symbioseform: " + (formId ?? "?");
                return false;
            }

            if (Active)
            {
                Dismount(ActiveFormDisplayName + "-Symbiose beendet.");
                message = ActiveFormDisplayName + "-Symbiose beendet.";
                return true;
            }

            if (serpent && !IsPlayerSwimming(player))
            {
                message = "Die Seeschlangen-Symbiose kann nur im Wasser gestartet werden.";
                return false;
            }
            if (troll && IsPlayerSwimming(player))
            {
                message = "Die Troll-Symbiose kann nicht beim Schwimmen gestartet werden. Geh erst an Land.";
                return false;
            }
            if (werewolf && IsPlayerSwimming(player))
            {
                message = "Die Werwolf-Symbiose ist eine Landform. Geh erst an Land.";
                return false;
            }

            boundLevel = Math.Max(1, boundLevel);
            if (serpent && string.IsNullOrWhiteSpace(boundPrefabName)) boundPrefabName = "Serpent";
            if (troll && string.IsNullOrWhiteSpace(boundPrefabName)) boundPrefabName = "Troll";
            if (werewolf && string.IsNullOrWhiteSpace(boundPrefabName)) boundPrefabName = "Fenring";
            if (drake && string.IsNullOrWhiteSpace(boundPrefabName)) boundPrefabName = "Hatchling";
            GameObject prefab = troll ? ResolveTrollStatePrefab(false) : ResolveRemoteAvatarPrefab(formId, boundPrefabName);
            if (prefab == null)
            {
                message = serpent
                    ? "Seeschlangen-Prefab nicht verfuegbar (gebundenes Profil/Serpent)."
                    : troll
                        ? "Troll-Prefab nicht verfuegbar (gebundenes Profil/Troll)."
                        : werewolf
                            ? "Werwolf-Prefab nicht verfuegbar (gebundenes Profil/Fenring)."
                            : "Drake-Prefab nicht verfuegbar (gebundenes Profil/Hatchling/Drake).";
                _debugLog?.Invoke("Remote symbiosis avatar prefab lookup failed for form=" + formId + ".");
                return false;
            }

            // v4.3.0 release hardening: a previous interrupted session must never leave a second
            // local temporary creature body behind. Clean tagged stale avatars before creating the
            // next form and clear queued one-shot combat state from any previous body.
            CleanupTaggedTemporaryAvatars(player);
            ResetPendingTrollImpacts();
            _werewolfMovementJumpQueuedUntil = 0f;
            _werewolfPounceUntil = 0f;
            _werewolfPounceVelocity = Vector3.zero;
            _serpentBiteLungeUntil = 0f;
            _serpentBiteLungeVelocity = Vector3.zero;

            string previousForm = _activeFormId;
            int previousBoundLevel = _activeBoundLevel;
            _activeFormId = serpent ? SerpentFormId : troll ? TrollFormId : werewolf ? WerewolfFormId : DrakeFormId;
            _activeBoundLevel = boundLevel;
            try
            {
                Vector3 spawn = player.transform.position + player.transform.forward * (serpent ? 1.0f : troll ? 1.2f : werewolf ? 0.9f : 1.5f) + Vector3.up * (serpent ? 0.1f : troll ? 0.05f : werewolf ? 0.05f : 1.0f);
                GameObject avatar = UnityEngine.Object.Instantiate(prefab, spawn, player.transform.rotation);
                if (avatar != null && !avatar.activeSelf) avatar.SetActive(true);
                Character character = avatar != null ? avatar.GetComponent<Character>() : null;
                ZNetView nview = avatar != null ? avatar.GetComponent<ZNetView>() : null;
                if (character == null || nview == null || !nview.IsValid())
                {
                    if (avatar != null) UnityEngine.Object.Destroy(avatar);
                    _activeFormId = previousForm;
                    _activeBoundLevel = previousBoundLevel;
                    message = $"Temporaerer {formLabel}-Koerper konnte nicht erstellt werden (ZNetView ungueltig).";
                    _debugLog?.Invoke(message);
                    return false;
                }

                if (!nview.IsOwner())
                {
                    try
                    {
                        MethodInfo claim = typeof(ZNetView).GetMethod("ClaimOwnership", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                        claim?.Invoke(nview, null);
                    }
                    catch (Exception ex)
                    {
                        _debugLog?.Invoke($"Remote avatar ClaimOwnership failed safely: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                if (!nview.IsOwner())
                {
                    UnityEngine.Object.Destroy(avatar);
                    _activeFormId = previousForm;
                    _activeBoundLevel = previousBoundLevel;
                    message = $"Temporaerer {formLabel}-Koerper hat keine Netzwerk-Ownership.";
                    _debugLog?.Invoke(message);
                    return false;
                }

                ApplyBoundCreatureProfile(character, boundPrefabName, boundLevel);
                if (serpent) PrepareSerpentProfile(character, player);
                else if (troll) PrepareTrollProfile(character);
                else if (werewolf) PrepareWerewolfProfile(character, boundLevel);
                ConfigureTemporarySymbiosisAvatar(nview, player, NormalizeRuntimePrefabName(prefab.name), boundLevel);
                _temporarySymbiosisAvatar = avatar;
                if (!BeginSymbiosis(character, player, true, out message))
                {
                    DestroyTemporarySymbiosisAvatar();
                    _activeFormId = previousForm;
                    _activeBoundLevel = previousBoundLevel;
                    return false;
                }

                _debugLog?.Invoke($"Remote {_activeFormId} symbiosis avatar created from necklace profile; prefab={NormalizeRuntimePrefabName(prefab.name)}, level={boundLevel}.");
                return true;
            }
            catch (Exception ex)
            {
                DestroyTemporarySymbiosisAvatar();
                _activeFormId = previousForm;
                    _activeBoundLevel = previousBoundLevel;
                message = $"{(serpent ? "Seeschlangen" : troll ? "Troll" : werewolf ? "Werwolf" : "Drake")}-Symbiose konnte nicht gestartet werden: {ex.GetType().Name}: {ex.Message}";
                _debugLog?.Invoke(message);
                return false;
            }
        }

        private static bool IsPlayerSwimming(Player player)
        {
            if (player == null) return false;
            try
            {
                if (CharacterIsSwimmingMethod != null)
                {
                    object value = CharacterIsSwimmingMethod.Invoke(player, null);
                    if (value is bool swimming) return swimming;
                }
            }
            catch { }
            // If Iron Gate changes IsSwimming, fail open only when the player is physically close to
            // sea level is not robust enough. Better require a working water-state API than spawn a
            // Serpent on land where native water locomotion cannot initialize correctly.
            return false;
        }

        private static void PrepareSerpentProfile(Character character, Player player)
        {
            if (character == null) return;
            _serpentSurfaceY = player != null ? player.transform.position.y + 0.25f : character.transform.position.y;
            _serpentDepth = 0f;

            // The temporary body is disposable, so boosting its native movement fields does not mutate
            // any real captured Serpent. Reflection keeps this tolerant of field layout changes.
            TryRaiseFloatField(character, "m_swimSpeed", 18f, 3.0f);
            TryRaiseFloatField(character, "m_runSpeed", 18f, 2.8f);
            TryRaiseFloatField(character, "m_walkSpeed", 10f, 2.0f);
            _debugLog?.Invoke($"Sea Serpent mobility profile prepared; surfaceY={_serpentSurfaceY:0.00}, surface-locked eye clearance={SerpentSurfaceEyeClearance:0.00}m, forward=18, reverse={SerpentBackwardSpeedScale:P0}, Shift boost stamina={SerpentBoostStaminaPerSecond:0.#}/s.");
        }

        private static void PrepareTrollProfile(Character character)
        {
            if (character == null) return;
            // Keep Troll movement weighty. We only guarantee a sane minimum so custom prefab tuning
            // remains authoritative; native gravity, slopes, step handling and animation stay intact.
            TryRaiseFloatField(character, "m_walkSpeed", 2.6f, 1.0f);
            TryRaiseFloatField(character, "m_runSpeed", 5.5f, 1.0f);
            InitializeTrollLogState(character);
            _debugLog?.Invoke("Troll tank profile prepared: 35% less physical damage, strong stagger/knockback resistance, stamina-paid sprint; starts unarmed and can pick up a fallen tree log with E.");
        }

        private static void InitializeTrollLogState(Character character)
        {
            ResetTrollLogState(false);
            if (character == null) return;

            try
            {
                GameObject visual = ResolveVisual(character) ?? character.gameObject;
                Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(true);
                List<Renderer> logRenderers = new List<Renderer>();
                List<bool> states = new List<bool>();

                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null) continue;
                    if (!LooksLikeTrollLogVisual(renderer.transform)) continue;
                    logRenderers.Add(renderer);
                    states.Add(renderer.enabled);
                    renderer.enabled = false;
                }

                _trollLogRenderers = logRenderers.ToArray();
                _trollLogRendererDefaultStates = states.ToArray();
                _trollHasLog = false;
                _trollLogDurability = 0;
                _trollHeldLogPrefabName = string.Empty;

                Humanoid humanoid = character as Humanoid;
                _trollNativeLogWeapon = FindTrollNativeLogWeapon(humanoid);
                _trollNativeLogWeaponPrefab = FindTrollNativeLogWeaponPrefab(humanoid);
                ForceTrollUnarmed(humanoid);
                string nativeLabel = _trollNativeLogWeapon != null ? GetNativeAttackLabel(_trollNativeLogWeapon, false) : "none";
                string prefabLabel = _trollNativeLogWeaponPrefab != null ? _trollNativeLogWeaponPrefab.name : "none";
                _debugLog?.Invoke($"Troll log state initialized: forced Troll1/unarmed; native log weapon candidate={nativeLabel}; random/default weapon prefab={prefabLabel}; hidden native log renderers={_trollLogRenderers.Length}.");
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke("Troll log visual initialization failed safely: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static ItemDrop.ItemData FindTrollNativeLogWeapon(Humanoid humanoid)
        {
            if (humanoid == null) return null;
            try
            {
                ItemDrop.ItemData current = humanoid.GetCurrentWeapon();
                ItemDrop.ItemData unarmed = humanoid.m_unarmedWeapon != null ? humanoid.m_unarmedWeapon.m_itemData : null;
                if (current != null && current != unarmed && LooksLikeTrollLogWeaponItem(current)) return current;

                List<ItemDrop.ItemData> candidates = new List<ItemDrop.ItemData>();
                Inventory inventory = humanoid.GetInventory();
                if (inventory != null)
                {
                    List<ItemDrop.ItemData> items = inventory.GetAllItems();
                    if (items != null)
                        for (int i = 0; i < items.Count; i++) AddNativeAttackCandidate(candidates, items[i]);
                }
                CollectReflectedAttackCandidates(humanoid, candidates);
                CollectReflectedAttackCandidates(humanoid.GetComponent<MonsterAI>(), candidates);

                for (int i = 0; i < candidates.Count; i++)
                {
                    ItemDrop.ItemData item = candidates[i];
                    if (item == null || item == unarmed) continue;
                    if (LooksLikeTrollLogWeaponItem(item)) return item;
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke("Troll native log weapon discovery failed safely: " + ex.GetType().Name + ": " + ex.Message);
            }
            return null;
        }

        private static GameObject FindTrollNativeLogWeaponPrefab(Humanoid humanoid)
        {
            if (humanoid == null) return null;
            try
            {
                List<GameObject> strict = new List<GameObject>();
                List<GameObject> randomWeapon = new List<GameObject>();
                CollectTrollWeaponPrefabFields(humanoid, strict, randomWeapon);
                MonsterAI ai = humanoid.GetComponent<MonsterAI>();
                if (ai != null) CollectTrollWeaponPrefabFields(ai, strict, randomWeapon);

                for (int i = 0; i < strict.Count; i++)
                {
                    GameObject candidate = strict[i];
                    ItemDrop drop = candidate != null ? candidate.GetComponent<ItemDrop>() : null;
                    if (drop != null && LooksLikeTrollLogWeaponItem(drop.m_itemData)) return candidate;
                }

                // Vanilla Troll uses a random weapon slot for the tree trunk. The item/prefab name
                // is not guaranteed to contain "log" on every game build, so when no strict name
                // match exists prefer the first real ItemDrop from a field named random*weapon*.
                for (int i = 0; i < randomWeapon.Count; i++)
                {
                    GameObject candidate = randomWeapon[i];
                    ItemDrop drop = candidate != null ? candidate.GetComponent<ItemDrop>() : null;
                    if (drop == null || drop.m_itemData == null) continue;
                    ItemDrop.ItemData data = drop.m_itemData;
                    ItemDrop.ItemData unarmed = humanoid.m_unarmedWeapon != null ? humanoid.m_unarmedWeapon.m_itemData : null;
                    if (data == unarmed) continue;
                    string anim = (GetNativeAttackAnimation(data, false) + " " + GetNativeAttackAnimation(data, true)).ToLowerInvariant();
                    if (anim.Contains("punch") || anim.Contains("unarmed")) continue;
                    return candidate;
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke("Troll native weapon prefab discovery failed safely: " + ex.GetType().Name + ": " + ex.Message);
            }
            return null;
        }

        private static void CollectTrollWeaponPrefabFields(object source, List<GameObject> strict, List<GameObject> randomWeapon)
        {
            if (source == null) return;
            try
            {
                Type type = source.GetType();
                while (type != null && type != typeof(object))
                {
                    FieldInfo[] fields = type.GetFields(InstanceFlags | BindingFlags.DeclaredOnly);
                    for (int i = 0; i < fields.Length; i++)
                    {
                        FieldInfo field = fields[i];
                        if (field == null) continue;
                        string fieldName = (field.Name ?? string.Empty).ToLowerInvariant();
                        if (!fieldName.Contains("weapon") && !fieldName.Contains("item")) continue;
                        object value = null;
                        try { value = field.GetValue(source); } catch { continue; }
                        if (value == null) continue;

                        GameObject single = value as GameObject;
                        if (single != null)
                        {
                            AddTrollWeaponPrefabCandidate(fieldName, single, strict, randomWeapon);
                            continue;
                        }

                        Array array = value as Array;
                        if (array == null) continue;
                        for (int a = 0; a < array.Length; a++)
                        {
                            GameObject go = null;
                            try { go = array.GetValue(a) as GameObject; } catch { }
                            if (go != null) AddTrollWeaponPrefabCandidate(fieldName, go, strict, randomWeapon);
                        }
                    }
                    type = type.BaseType;
                }
            }
            catch { }
        }

        private static void AddTrollWeaponPrefabCandidate(string fieldName, GameObject candidate, List<GameObject> strict, List<GameObject> randomWeapon)
        {
            if (candidate == null) return;
            ItemDrop drop = null;
            try { drop = candidate.GetComponent<ItemDrop>(); } catch { }
            if (drop == null || drop.m_itemData == null) return;
            if (!strict.Contains(candidate) && LooksLikeTrollLogWeaponItem(drop.m_itemData)) strict.Add(candidate);
            if (fieldName.Contains("random") && fieldName.Contains("weapon") && !randomWeapon.Contains(candidate)) randomWeapon.Add(candidate);
        }

        private static ItemDrop.ItemData MaterializeTrollNativeLogWeapon(Humanoid humanoid)
        {
            if (humanoid == null) return null;
            if (_trollNativeLogWeapon != null) return _trollNativeLogWeapon;
            if (_trollNativeLogWeaponPrefab == null) _trollNativeLogWeaponPrefab = FindTrollNativeLogWeaponPrefab(humanoid);
            if (_trollNativeLogWeaponPrefab == null) return null;

            try
            {
                // Use Humanoid's own default-item path first. This is the same path monsters use for
                // their native equipment and therefore also wires VisEquipment/attack animations.
                MethodInfo giveDefault = null;
                Type type = humanoid.GetType();
                while (type != null && giveDefault == null)
                {
                    giveDefault = type.GetMethod("GiveDefaultItem", InstanceFlags | BindingFlags.DeclaredOnly, null, new[] { typeof(GameObject) }, null);
                    type = type.BaseType;
                }
                if (giveDefault != null)
                {
                    giveDefault.Invoke(humanoid, new object[] { _trollNativeLogWeaponPrefab });
                    _trollNativeLogWeapon = FindTrollNativeLogWeapon(humanoid);
                    if (_trollNativeLogWeapon != null)
                    {
                        _debugLog?.Invoke("Troll2 native weapon materialized via Humanoid.GiveDefaultItem: " + _trollNativeLogWeaponPrefab.name);
                        return _trollNativeLogWeapon;
                    }
                }

                Inventory inventory = humanoid.GetInventory();
                ItemDrop drop = _trollNativeLogWeaponPrefab.GetComponent<ItemDrop>();
                if (inventory != null && drop != null && drop.m_itemData != null)
                {
                    ItemDrop.ItemData clone = drop.m_itemData.Clone();
                    MethodInfo addItem = typeof(Inventory).GetMethod("AddItem", InstanceFlags, null, new[] { typeof(ItemDrop.ItemData) }, null);
                    if (addItem != null) addItem.Invoke(inventory, new object[] { clone });
                    _trollNativeLogWeapon = FindTrollNativeLogWeapon(humanoid) ?? clone;
                    _debugLog?.Invoke("Troll2 native weapon materialized from random/default prefab: " + _trollNativeLogWeaponPrefab.name);
                    return _trollNativeLogWeapon;
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke("Troll2 native weapon materialization failed safely: " + ex.GetType().Name + ": " + ex.Message);
            }
            return null;
        }

        private static bool LooksLikeTrollLogWeaponItem(ItemDrop.ItemData item)
        {
            if (item == null) return false;
            string text = string.Empty;
            try
            {
                if (item.m_dropPrefab != null) text += " " + item.m_dropPrefab.name;
                if (item.m_shared != null) text += " " + item.m_shared.m_name;
                text += " " + GetNativeAttackAnimation(item, false);
                text += " " + GetNativeAttackAnimation(item, true);
            }
            catch { }
            text = text.ToLowerInvariant();
            if (text.Contains("punch") || text.Contains("unarmed")) return false;
            return text.Contains("log") || text.Contains("club") || text.Contains("trunk") ||
                   text.Contains("treeweapon") || text.Contains("woodweapon") || text.Contains("troll_weapon");
        }

        private static void ForceTrollUnarmed(Humanoid humanoid)
        {
            if (humanoid == null) return;
            try
            {
                ItemDrop.ItemData unarmed = humanoid.m_unarmedWeapon != null ? humanoid.m_unarmedWeapon.m_itemData : null;
                ItemDrop.ItemData current = humanoid.GetCurrentWeapon();
                if (current != null && current != unarmed)
                {
                    MethodInfo[] methods = typeof(Humanoid).GetMethods(InstanceFlags);
                    for (int i = 0; i < methods.Length; i++)
                    {
                        MethodInfo method = methods[i];
                        if (!string.Equals(method.Name, "UnequipItem", StringComparison.Ordinal)) continue;
                        ParameterInfo[] pars = method.GetParameters();
                        if (pars.Length == 2 && pars[0].ParameterType == typeof(ItemDrop.ItemData) && pars[1].ParameterType == typeof(bool))
                        {
                            method.Invoke(humanoid, new object[] { current, false });
                            break;
                        }
                        if (pars.Length == 1 && pars[0].ParameterType == typeof(ItemDrop.ItemData))
                        {
                            method.Invoke(humanoid, new object[] { current });
                            break;
                        }
                    }
                }
                if (unarmed != null && humanoid.GetCurrentWeapon() != unarmed)
                    humanoid.EquipItem(unarmed, false);
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke("Troll forced-unarmed state failed safely: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool EquipTrollNativeLogWeapon()
        {
            Humanoid humanoid = _mountedDrake as Humanoid;
            if (humanoid == null) return false;
            if (_trollNativeLogWeapon == null) _trollNativeLogWeapon = MaterializeTrollNativeLogWeapon(humanoid);
            if (_trollNativeLogWeapon == null) return false;
            try
            {
                if (humanoid.GetCurrentWeapon() == _trollNativeLogWeapon || humanoid.EquipItem(_trollNativeLogWeapon, false))
                {
                    _debugLog?.Invoke("Troll2/native-log state equipped: " + GetNativeAttackLabel(_trollNativeLogWeapon, false));
                    return true;
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke("Troll native log equip failed safely: " + ex.GetType().Name + ": " + ex.Message);
            }
            return false;
        }

        private static bool LooksLikeTrollLogVisual(Transform transform)
        {
            Transform current = transform;
            int depth = 0;
            while (current != null && depth++ < 5)
            {
                string name = (current.name ?? string.Empty).ToLowerInvariant();
                if (name.Contains("log") || name.Contains("trunk") || name.Contains("club") ||
                    name.Contains("treeweapon") || name.Contains("woodweapon"))
                    return true;
                current = current.parent;
            }
            return false;
        }

        private static bool SetTrollLogVisual(bool visible)
        {
            bool changed = false;
            if (_trollLogRenderers != null)
            {
                for (int i = 0; i < _trollLogRenderers.Length; i++)
                {
                    Renderer renderer = _trollLogRenderers[i];
                    if (renderer == null) continue;
                    try
                    {
                        renderer.enabled = visible;
                        changed = true;
                    }
                    catch { }
                }
            }
            if (!visible) DestroyTrollHeldLogVisual();
            return changed;
        }

        private static bool CreateTrollHeldLogVisualFromWorldLog(GameObject worldLog)
        {
            DestroyTrollHeldLogVisual();
            if (worldLog == null || _mountedDrake == null) return false;

            try
            {
                MeshFilter[] filters = worldLog.GetComponentsInChildren<MeshFilter>(true);
                MeshFilter sourceFilter = null;
                MeshRenderer sourceRenderer = null;
                float bestVolume = 0f;

                for (int i = 0; i < filters.Length; i++)
                {
                    MeshFilter filter = filters[i];
                    if (filter == null || filter.sharedMesh == null) continue;
                    MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                    if (renderer == null) continue;
                    Bounds b = renderer.bounds;
                    float volume = Mathf.Max(0.001f, b.size.x * b.size.y * b.size.z);
                    if (sourceFilter == null || volume > bestVolume)
                    {
                        sourceFilter = filter;
                        sourceRenderer = renderer;
                        bestVolume = volume;
                    }
                }

                if (sourceFilter == null || sourceRenderer == null)
                {
                    _debugLog?.Invoke("Troll held-log visual fallback could not find a MeshFilter/MeshRenderer on the picked world log.");
                    return false;
                }

                GameObject visualRoot = ResolveVisual(_mountedDrake) ?? _mountedDrake.gameObject;
                Transform hand = FindTrollRightHand(visualRoot.transform);
                if (hand == null) hand = visualRoot.transform;

                GameObject holder = new GameObject("VW_TrollHeldLogVisual");
                holder.transform.SetParent(hand, false);
                // The world log mesh normally points along a different axis from the Troll hand.
                // These values intentionally keep the trunk large/heavy while placing its lower
                // third in the right hand. They are cosmetic only and never affect collision.
                holder.transform.localPosition = new Vector3(0.12f, 0.18f, 0.95f);
                holder.transform.localRotation = Quaternion.Euler(82f, 6f, -8f);

                float sourceLength = Mathf.Max(sourceRenderer.bounds.size.x, Mathf.Max(sourceRenderer.bounds.size.y, sourceRenderer.bounds.size.z));
                float targetLength = Mathf.Clamp(_mountedDrake.GetRadius() * 2.6f, 4.5f, 7.0f);
                float uniformScale = sourceLength > 0.05f ? Mathf.Clamp(targetLength / sourceLength, 0.18f, 1.15f) : 0.5f;
                holder.transform.localScale = Vector3.one * uniformScale;

                GameObject meshObject = new GameObject("LogMesh");
                meshObject.transform.SetParent(holder.transform, false);
                meshObject.transform.localPosition = Vector3.zero;
                meshObject.transform.localRotation = Quaternion.identity;
                meshObject.transform.localScale = Vector3.one;
                MeshFilter meshFilter = meshObject.AddComponent<MeshFilter>();
                meshFilter.sharedMesh = sourceFilter.sharedMesh;
                MeshRenderer meshRenderer = meshObject.AddComponent<MeshRenderer>();
                meshRenderer.sharedMaterials = sourceRenderer.sharedMaterials;
                meshRenderer.shadowCastingMode = sourceRenderer.shadowCastingMode;
                meshRenderer.receiveShadows = sourceRenderer.receiveShadows;

                _trollHeldLogVisual = holder;
                _debugLog?.Invoke($"Troll held-log visual cloned from picked log mesh; hand='{hand.name}', source='{sourceFilter.name}', targetLength={targetLength:F1}m, scale={uniformScale:F2}.");
                return true;
            }
            catch (Exception ex)
            {
                DestroyTrollHeldLogVisual();
                _debugLog?.Invoke("Troll held-log visual clone failed safely: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static Transform FindTrollRightHand(Transform root)
        {
            if (root == null) return null;
            try
            {
                Animator animator = root.GetComponentInChildren<Animator>(true);
                if (animator != null && animator.isHuman)
                {
                    Transform humanHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                    if (humanHand != null) return humanHand;
                }
            }
            catch { }

            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            Transform fallback = null;
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null) continue;
                string n = (t.name ?? string.Empty).ToLowerInvariant();
                bool hand = n.Contains("hand");
                if (!hand) continue;
                if (fallback == null) fallback = t;
                if (n.Contains("right") || n.Contains("r_hand") || n.Contains("hand_r") || n.Contains("hand.r") || n == "rhand")
                    return t;
            }
            return fallback;
        }

        private static void DestroyTrollHeldLogVisual()
        {
            if (_trollHeldLogVisual == null) return;
            try { UnityEngine.Object.Destroy(_trollHeldLogVisual); } catch { }
            _trollHeldLogVisual = null;
        }

        private static void UpdateTrollLogInteraction()
        {
            if (!Active || !IsTrollForm || _mountedDrake == null || _transitionState != SymbiosisTransitionState.None) return;
            if (_trollHasLog) return;

            if (Time.unscaledTime >= _nextTrollLogProbeAt)
            {
                _nextTrollLogProbeAt = Time.unscaledTime + 0.10f;
                ForceTrollUnarmed(_mountedDrake as Humanoid);
                // Prefer the log the player is actually looking at. This makes pickup deterministic
                // instead of silently choosing some other TreeLog inside the Troll's large body radius.
                _trollNearbyLog = FindTargetedFallenTreeLog(TrollLogTargetDistance);
                if (_trollNearbyLog == null)
                    _trollNearbyLog = FindNearbyFallenTreeLog(_mountedDrake.transform.position, TrollLogPickupRadius);
            }

            if (_trollNearbyLog != null && Time.unscaledTime >= _nextTrollLogHintAt)
            {
                _nextTrollLogHintAt = Time.unscaledTime + 0.75f;
                _status?.Invoke("[E] Baumstamm aufnehmen");
            }

            if (IsGameplayUseButtonDown() && _trollNearbyLog != null)
            {
                GameObject picked = _trollNearbyLog;
                _trollNearbyLog = null;
                PickUpTrollLog(picked);
            }
        }

        private static bool IsGameplayUseButtonDown()
        {
            try
            {
                if (ZInputGetButtonDownMethod != null && (bool)ZInputGetButtonDownMethod.Invoke(null, new object[] { "Use" })) return true;
            }
            catch { }
            return Input.GetKeyDown(KeyCode.E);
        }


        private static GameObject FindTargetedFallenTreeLog(float maxDistance)
        {
            try
            {
                Camera camera = Camera.main;
                if (camera == null) return null;

                Ray ray = new Ray(camera.transform.position, camera.transform.forward);
                int count = Physics.RaycastNonAlloc(ray, _trollRaycastBuffer, maxDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                GameObject best = null;
                float bestDistance = float.MaxValue;
                for (int i = 0; i < count; i++)
                {
                    RaycastHit hit = _trollRaycastBuffer[i];
                    if (hit.collider == null || hit.distance >= bestDistance) continue;
                    GameObject log = ResolveFallenTreeLog(hit.collider);
                    if (log == null) continue;
                    best = log;
                    bestDistance = hit.distance;
                }
                return best;
            }
            catch { }
            return null;
        }

        private static GameObject FindNearbyFallenTreeLog(Vector3 center, float radius)
        {
            try
            {
                int count = Physics.OverlapSphereNonAlloc(center, radius, _trollOverlapBuffer, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                GameObject best = null;
                float bestSqr = radius * radius;

                for (int i = 0; i < count; i++)
                {
                    GameObject log = ResolveFallenTreeLog(_trollOverlapBuffer[i]);
                    if (log == null) continue;
                    float sqr = (log.transform.position - center).sqrMagnitude;
                    if (sqr < bestSqr)
                    {
                        bestSqr = sqr;
                        best = log;
                    }
                }
                return best;
            }
            catch { return null; }
        }

        private static GameObject ResolveFallenTreeLog(Collider collider)
        {
            if (collider == null) return null;
            Transform current = collider.transform;
            GameObject fallback = null;

            for (int depth = 0; current != null && depth < 7; depth++, current = current.parent)
            {
                GameObject go = current.gameObject;
                if (go == null) continue;
                if (go == _temporarySymbiosisAvatar || (_mountedDrake != null && (current == _mountedDrake.transform || current.IsChildOf(_mountedDrake.transform)))) return null;
                if (go.GetComponent<Character>() != null || go.GetComponent<Piece>() != null || go.GetComponent<ItemDrop>() != null) return null;

                Component[] components = go.GetComponents<Component>();
                for (int i = 0; i < components.Length; i++)
                {
                    Component component = components[i];
                    if (component == null) continue;
                    if (string.Equals(component.GetType().Name, "TreeLog", StringComparison.OrdinalIgnoreCase))
                        return go;
                }

                string objectName = (go.name ?? string.Empty).ToLowerInvariant();
                bool logLikeName = objectName.Contains("treelog") || objectName.Contains("tree_log") ||
                                   objectName.Contains("_log") || objectName.EndsWith("log") ||
                                   objectName.Contains("loghalf") || objectName.Contains("log_half") ||
                                   objectName.Contains("beechlog") || objectName.Contains("firlog") ||
                                   objectName.Contains("pinelog") || objectName.Contains("oaklog") ||
                                   objectName.Contains("birchlog") || objectName.Contains("ancientlog");
                if (fallback == null && logLikeName)
                {
                    // Some Valheim logs keep the Rigidbody/Destructible on a parent or child rather
                    // than the collider GameObject itself. Keep the highest matching world object.
                    bool physical = go.GetComponent<Rigidbody>() != null ||
                                    go.GetComponentInChildren<Rigidbody>() != null ||
                                    go.GetComponent<Destructible>() != null ||
                                    go.GetComponentInChildren<Destructible>() != null;
                    if (physical) fallback = go;
                }
            }
            return fallback;
        }

        private static bool TrySwapTrollAvatarState(bool armed)
        {
            if (!Active || !IsTrollForm || _mountedDrake == null || _rider == null) return false;
            GameObject template = ResolveTrollStatePrefab(armed);
            if (template == null) return false;

            Character oldCharacter = _mountedDrake;
            GameObject oldAvatar = _temporarySymbiosisAvatar != null ? _temporarySymbiosisAvatar : oldCharacter.gameObject;
            Vector3 position = oldCharacter.transform.position;
            Quaternion rotation = oldCharacter.transform.rotation;

            // v3.8.1: Troll1/Troll2 is a body-state handoff, not a new possession. Preserve the
            // currently owned camera pose instead of letting InitializeSymbiosisCamera recenter the
            // view to the freshly instantiated Troll. This removes the visible yaw/pitch snap when
            // picking up or breaking the log while keeping the deterministic two-body architecture.
            bool preserveCameraPose = _cameraPoseInitialized;
            float preservedCameraYaw = _cameraYaw;
            float preservedCameraPitch = _cameraPitch;
            float preservedThirdYaw = _thirdChaseYaw;
            float preservedThirdYawVelocity = _thirdChaseYawVelocity;
            bool preservedThirdYawInitialized = _thirdChaseYawInitialized;
            bool preservedEgoMode = _drakeEgoActive;

            Vector3 velocity = Vector3.zero;
            try
            {
                Rigidbody oldBody = oldCharacter.GetComponent<Rigidbody>();
                if (oldBody != null) velocity = oldBody.velocity;
            }
            catch { }

            GameObject avatar = null;
            try
            {
                avatar = UnityEngine.Object.Instantiate(template, position, rotation);
                if (avatar != null && !avatar.activeSelf) avatar.SetActive(true);
                Character character = avatar != null ? avatar.GetComponent<Character>() : null;
                ZNetView nview = avatar != null ? avatar.GetComponent<ZNetView>() : null;
                if (character == null || nview == null || !nview.IsValid())
                {
                    if (avatar != null) UnityEngine.Object.Destroy(avatar);
                    return false;
                }
                if (!nview.IsOwner())
                {
                    MethodInfo claim = typeof(ZNetView).GetMethod("ClaimOwnership", InstanceFlags, null, Type.EmptyTypes, null);
                    claim?.Invoke(nview, null);
                }
                if (!nview.IsOwner())
                {
                    UnityEngine.Object.Destroy(avatar);
                    return false;
                }

                ApplyBoundCreatureProfile(character, "Troll", _activeBoundLevel);
                TryRaiseFloatField(character, "m_walkSpeed", 2.6f, 1.0f);
                TryRaiseFloatField(character, "m_runSpeed", 5.5f, 1.0f);
                ConfigureTemporarySymbiosisAvatar(nview, _rider, "Troll", _activeBoundLevel);
                DrakeMountAssignmentRuntime.MakeSymbiosisAvatarFriendly(character);

                Transform seat = ResolveOrCreateSeat(character);
                if (seat == null)
                {
                    DestroyTemporaryNetworkObject(avatar);
                    return false;
                }

                PrepareSmoothFlightBody(character);
                _mountedDrake = character;
                _cachedMountedViewOwner = character;
                _cachedMountedView = nview;
                _cachedMountedBodyOwner = character;
                _cachedMountedBody = character.GetComponent<Rigidbody>();
                _temporarySymbiosisAvatar = avatar;
                _seat = seat;
                _drakeEgoAnchor = ResolveDrakeEgoAnchor(character);
                InitializeSymbiosisCamera(character);
                if (preserveCameraPose)
                {
                    _drakeEgoActive = preservedEgoMode;
                    _cameraYaw = preservedCameraYaw;
                    _cameraPitch = preservedCameraPitch;
                    _cameraPoseInitialized = true;
                    _thirdChaseYaw = preservedThirdYaw;
                    _thirdChaseYawVelocity = preservedThirdYawVelocity;
                    _thirdChaseYawInitialized = preservedThirdYawInitialized;
                    _menuCameraLocked = false;
                    _lastOwnedCameraPoseValid = false;
                }
                CacheDrakeVisuals(character);
                _drakeAnimator = ResolveDrakeAnimator(character);
                ClearMountedDrakeNativeIntent(character);
                character.SetMoveDir(Vector3.zero);
                character.SetRun(false);
                character.SetWalk(false);
                CaptureIdleHold(character);
                try
                {
                    Rigidbody body = character.GetComponent<Rigidbody>();
                    if (body != null) body.velocity = velocity;
                }
                catch { }

                ResetNativeDrakeAttackCatalog(character);
                DestroyTemporaryNetworkObject(oldAvatar);
                _debugLog?.Invoke($"Troll avatar state switched: {(armed ? "Troll1 -> Troll2/log" : "Troll2 -> Troll1/unarmed")}.");
                return true;
            }
            catch (Exception ex)
            {
                if (avatar != null) DestroyTemporaryNetworkObject(avatar);
                _mountedDrake = oldCharacter;
                _cachedMountedViewOwner = oldCharacter;
                _cachedMountedView = oldCharacter != null ? oldCharacter.GetComponent<ZNetView>() : null;
                _cachedMountedBodyOwner = oldCharacter;
                _cachedMountedBody = oldCharacter != null ? oldCharacter.GetComponent<Rigidbody>() : null;
                _temporarySymbiosisAvatar = oldAvatar;
                _debugLog?.Invoke("Troll avatar state swap failed safely: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static void PickUpTrollLog(GameObject log)
        {
            if (log == null || _trollHasLog || !IsTrollForm) return;

            _trollHeldLogPrefabName = SanitizePrefabName(log.name);
            _trollHasLog = true;
            _trollLogDurability = TrollLogMaxDurability;

            // v2.0.7: treat Troll1 and Troll2 as two deterministic creature-body states instead
            // of equipping an item on the existing body. Troll2 is a runtime Troll prefab whose
            // random-set table contains only the native troll_log_swing set.
            if (!TrySwapTrollAvatarState(true))
            {
                _trollHasLog = false;
                _trollLogDurability = 0;
                _trollHeldLogPrefabName = string.Empty;
                _status?.Invoke("Troll2 mit Baumstamm konnte nicht erzeugt werden.");
                return;
            }

            if (!DestroyWorldLog(log))
            {
                TrySwapTrollAvatarState(false);
                _trollHasLog = false;
                _trollLogDurability = 0;
                _trollHeldLogPrefabName = string.Empty;
                _status?.Invoke("Baumstamm konnte nicht aufgenommen werden.");
                return;
            }

            _trollHasLog = true;
            _trollLogDurability = TrollLogMaxDurability;
            ResetNativeDrakeAttackCatalog(_mountedDrake);
            _status?.Invoke($"Troll2: Baumstamm aufgenommen ({_trollLogDurability}/{TrollLogMaxDurability} Schlaege).");
            _debugLog?.Invoke($"Troll1 -> Troll2 body swap completed; fallen log prefab='{_trollHeldLogPrefabName}', durability={_trollLogDurability}.");
        }

        private static bool DestroyWorldLog(GameObject log)
        {
            if (log == null) return false;
            try
            {
                ZNetView nview = log.GetComponent<ZNetView>() ?? log.GetComponentInParent<ZNetView>();
                GameObject root = nview != null ? nview.gameObject : log;
                ZNetScene scene = ZNetScene.instance;
                if (scene != null)
                {
                    MethodInfo destroyMethod = typeof(ZNetScene).GetMethod("Destroy", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(GameObject) }, null);
                    if (destroyMethod != null)
                    {
                        destroyMethod.Invoke(scene, new object[] { root });
                        return true;
                    }
                }
                UnityEngine.Object.Destroy(root);
                return true;
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke("Troll log pickup destroy failed safely: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static string SanitizePrefabName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            string value = name.Trim();
            const string clone = "(Clone)";
            if (value.EndsWith(clone, StringComparison.OrdinalIgnoreCase))
                value = value.Substring(0, value.Length - clone.Length).Trim();
            return value;
        }

        private static void ConsumeTrollLogSwing()
        {
            ConsumeTrollLogDurability(1, "Schlag");
        }

        private static void ConsumeTrollLogDurability(int amount, string reason)
        {
            if (!_trollHasLog || amount <= 0) return;
            _trollLogDurability = Mathf.Max(0, _trollLogDurability - amount);
            if (_trollLogDurability > 0)
            {
                _status?.Invoke($"Baumstamm: {_trollLogDurability}/{TrollLogMaxDurability}");
                _debugLog?.Invoke($"Troll log durability -{amount} ({reason}); remaining={_trollLogDurability}/{TrollLogMaxDurability}.");
                return;
            }

            _trollHasLog = false;
            _trollHeldLogPrefabName = string.Empty;
            if (!TrySwapTrollAvatarState(false))
            {
                SetTrollLogVisual(false);
                ForceTrollUnarmed(_mountedDrake as Humanoid);
                ResetNativeDrakeAttackCatalog(_mountedDrake);
            }
            _status?.Invoke("Der Baumstamm ist zerbrochen. Troll2 -> Troll1.");
            _debugLog?.Invoke($"Troll held log broke after {reason}; avatar returned to Troll1/unarmed.");
        }

        private static void DropHeldTrollLog()
        {
            if (!_trollHasLog || _mountedDrake == null) return;

            try
            {
                string prefabName = _trollHeldLogPrefabName;
                GameObject prefab = null;
                if (!string.IsNullOrWhiteSpace(prefabName) && ZNetScene.instance != null)
                    prefab = ZNetScene.instance.GetPrefab(prefabName);

                if (prefab == null && ZNetScene.instance != null)
                {
                    string[] fallbacks = { "Beech_log", "FirTree_log", "PineTree_log", "Birch_log", "Oak_log" };
                    for (int i = 0; i < fallbacks.Length && prefab == null; i++)
                        prefab = ZNetScene.instance.GetPrefab(fallbacks[i]);
                }

                if (prefab != null)
                {
                    Vector3 forward = _mountedDrake.transform.forward;
                    forward.y = 0f;
                    if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
                    forward.Normalize();
                    Vector3 position = _mountedDrake.transform.position + forward * Mathf.Max(2.2f, _mountedDrake.GetRadius() + 0.8f) + Vector3.up * 0.4f;
                    UnityEngine.Object.Instantiate(prefab, position, Quaternion.Euler(0f, _mountedDrake.transform.eulerAngles.y + 90f, 90f));
                    _debugLog?.Invoke("Troll dropped held tree log back into the world: " + (prefabName ?? string.Empty));
                }
                else
                {
                    _debugLog?.Invoke("Troll held-log drop skipped safely: no compatible log prefab resolved.");
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke("Troll held-log drop failed safely: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                ResetTrollLogState(true);
            }
        }

        private static void ResetTrollLogState(bool keepRendererDefaults)
        {
            try
            {
                if (_trollLogRenderers != null)
                {
                    for (int i = 0; i < _trollLogRenderers.Length; i++)
                    {
                        Renderer renderer = _trollLogRenderers[i];
                        if (renderer == null) continue;
                        bool enabled = keepRendererDefaults &&
                                       _trollLogRendererDefaultStates != null &&
                                       i < _trollLogRendererDefaultStates.Length &&
                                       _trollLogRendererDefaultStates[i];
                        renderer.enabled = enabled;
                    }
                }
            }
            catch { }

            _trollHasLog = false;
            _trollLogDurability = 0;
            _trollHeldLogPrefabName = string.Empty;
            _trollLogRenderers = Array.Empty<Renderer>();
            _trollLogRendererDefaultStates = Array.Empty<bool>();
            _nextTrollLogHintAt = 0f;
            _nextTrollLogProbeAt = 0f;
            _trollNearbyLog = null;
            _trollNativeLogWeapon = null;
            _trollNativeLogWeaponPrefab = null;
            ResetPendingTrollImpacts();
            DestroyTrollHeldLogVisual();
        }

        private static void PrepareWerewolfProfile(Character character, int boundLevel)
        {
            if (character == null) return;

            // v0.7.6 rollback: horizontal locomotion is back on the stable pre-v0.7 native
            // Character motor. Only the speed envelope is raised here; no late Rigidbody
            // translation/facing authority is attached to the temporary Fenring.
            TryRaiseFloatField(character, "m_walkSpeed", WerewolfWalkSpeed, 1.75f);
            TryRaiseFloatField(character, "m_runSpeed", WerewolfRunSpeed, 2.15f);

            float damageMultiplier = GetWerewolfStarDamageMultiplier(boundLevel);
            _debugLog?.Invoke($"Werewolf native locomotion rollback active: walk>={WerewolfWalkSpeed:0.0}, run>={WerewolfRunSpeed:0.0}, SPACE jump={WerewolfMovementJumpVelocity:0.0}m/s, no late Rigidbody/body-facing override, star damage x{damageMultiplier:0.##}.");
        }

        private static void TryRaiseFloatField(object target, string fieldName, float minimum, float multiplier)
        {
            if (target == null) return;
            try
            {
                FieldInfo field = target.GetType().GetField(fieldName, InstanceFlags) ?? typeof(Character).GetField(fieldName, InstanceFlags);
                if (field == null || field.FieldType != typeof(float)) return;
                float current = (float)field.GetValue(target);
                field.SetValue(target, Mathf.Max(minimum, current * multiplier));
            }
            catch { }
        }

        private static void ApplyBoundCreatureProfile(Character character, string boundPrefabName, int boundLevel)
        {
            if (character == null) return;
            boundLevel = Math.Max(1, boundLevel);

            try
            {
                MethodInfo setLevel = character.GetType().GetMethod(
                    "SetLevel",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(int) },
                    null);
                if (setLevel != null)
                {
                    setLevel.Invoke(character, new object[] { boundLevel });
                    _debugLog?.Invoke($"Bound {_activeFormId} level restored on temporary symbiosis body: {boundLevel}.");
                }
                else
                {
                    _debugLog?.Invoke($"Bound {_activeFormId} level={boundLevel} saved in necklace, but Character.SetLevel(int) is unavailable in this Valheim build; continuing safely at prefab default level.");
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Bound {_activeFormId} level restore failed safely (level={boundLevel}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string NormalizeRuntimePrefabName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            const string cloneSuffix = "(Clone)";
            return name.EndsWith(cloneSuffix, StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - cloneSuffix.Length)
                : name;
        }

        private static bool BeginSymbiosis(Character worker, Player player, bool temporaryAvatar, out string message)
        {
            message = string.Empty;
            try
            {
                if (player == null || player != Player.m_localPlayer || player.IsDead())
                {
                    message = ActiveFormDisplayName + "-Symbiose: Spieler ist nicht bereit.";
                    return false;
                }
                if (worker == null || worker.IsDead())
                {
                    message = ActiveFormDisplayName + "-Symbiose: Kreaturenkoerper ist nicht bereit.";
                    return false;
                }

                ZNetView nview = worker.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid() || !nview.IsOwner())
                {
                    message = ActiveFormDisplayName + "-Symbiose: Netzwerk-Ownership nicht verfuegbar.";
                    return false;
                }

                Transform seat = ResolveOrCreateSeat(worker);
                if (seat == null)
                {
                    message = ActiveFormDisplayName + "-Symbiosepunkt konnte nicht erstellt werden.";
                    return false;
                }

                // A build tool can already be active before the necklace is used. Once symbiosis
                // starts, equipment changes are intentionally locked, so leaving Hammer/Hoe/etc.
                // in placement mode would trap the hidden player in the build UI for the whole form.
                // Cancel vanilla placement mode and unequip only the currently active build tool
                // before possession becomes active. Food/mead use remains unaffected afterwards.
                CancelHiddenHumanBuildMode(player);

                // A weapon attack can already be armed before the necklace hotkey is pressed
                // (most visible with a loaded crossbow). Cancel that human combat state before
                // possession becomes active so no delayed animation event can still spawn a bolt.
                CancelHiddenHumanAttack(player);

                CaptureHumanCameraRig(player);
                bool startInEgo = ResolveInitialSymbiosisCameraMode(player);
                CachePlayerVisuals(player);
                DrakeMountAssignmentRuntime.MakeSymbiosisAvatarFriendly(worker);

                _mountedDrake = worker;
                _cachedMountedViewOwner = worker;
                _cachedMountedView = nview;
                _rider = player;
                SaveCrashRecoveryAnchor(player, player.transform.position, player.transform.rotation);
                // Multiplayer clients do not render the local morph animation, so publish the body
                // handoff as soon as the networked Drake exists. Keep it active through the whole
                // exit animation and clear it only when ForceDismount destroys the avatar.
                SetPlayerSymbiosisNetworkState(player, true);
                _seat = seat;
                _mountedAtFrame = Time.frameCount;
                _nextActiveIntegrityCheckAt = Time.unscaledTime + 0.50f;
                PrepareSmoothFlightBody(worker);
                _drakeEgoActive = startInEgo;
                _drakeEgoAnchor = ResolveDrakeEgoAnchor(worker);
                InitializeSymbiosisCamera(worker);
                CaptureCameraState();
                SuspendExternalEgoCameraState();
                MoveCameraCallbacksToTail();
                CacheDrakeVisuals(worker);
                BeginEnterTransformation(worker, player, temporaryAvatar);

                MonsterAI monster = worker.GetComponent<MonsterAI>();
                if (monster != null && monster.GetFollowTarget() != null)
                    WorkerNavigationRuntime.SetNativeFollowTarget(monster, null);

                worker.SetMoveDir(Vector3.zero);
                worker.SetRun(false);
                worker.SetWalk(false);
                ClearMountedDrakeNativeIntent(worker);
                CaptureIdleHold(worker);

                message = ActiveFormDisplayName + "-Symbiose verbindet...";
                _status?.Invoke(message);
                _debugLog?.Invoke(temporaryAvatar
                    ? "Remote " + _activeFormId + " symbiosis transformation started."
                    : _activeFormId + " symbiosis transformation started for local player.");
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                message = $"{ActiveFormDisplayName}-Symbiose fehlgeschlagen: {inner.GetType().Name}: {inner.Message}";
                _debugLog?.Invoke(message);
                if (_rider == player || _mountedDrake == worker) ForceDismount(false);
                else SetPlayerSymbiosisNetworkState(player, false);
                return false;
            }
            catch (Exception ex)
            {
                message = $"{ActiveFormDisplayName}-Symbiose fehlgeschlagen: {ex.GetType().Name}: {ex.Message}";
                _debugLog?.Invoke(message);
                if (_rider == player || _mountedDrake == worker) ForceDismount(false);
                else SetPlayerSymbiosisNetworkState(player, false);
                return false;
            }
        }

        private static void CancelHiddenHumanBuildMode(Player player)
        {
            if (player == null) return;

            bool cancelled = false;
            try
            {
                // Valheim's build mode is owned by Player.SetPlaceMode(PieceTable). Use reflection
                // so this remains compatible with minor game builds where the parameter visibility
                // or exact PieceTable type metadata can shift. Passing null is vanilla's exit path.
                MethodInfo[] methods = typeof(Player).GetMethods(InstanceFlags);
                for (int i = 0; i < methods.Length; ++i)
                {
                    MethodInfo method = methods[i];
                    if (!string.Equals(method.Name, "SetPlaceMode", StringComparison.Ordinal)) continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 1 || parameters[0].ParameterType.IsValueType) continue;
                    method.Invoke(player, new object[] { null });
                    cancelled = true;
                    break;
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Symbiosis build-mode SetPlaceMode(null) failed safely: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                // If a Hammer/Hoe/Cultivator-style item is still equipped, unequip only that item.
                // We identify build tools by SharedData.m_buildPieces rather than item names so
                // modded construction tools are handled too and ordinary weapons stay equipped.
                ItemDrop.ItemData current = player.GetCurrentWeapon();
                if (current != null && current.m_shared != null)
                {
                    FieldInfo buildPiecesField = current.m_shared.GetType().GetField("m_buildPieces", InstanceFlags);
                    object buildPieces = buildPiecesField?.GetValue(current.m_shared);
                    if (buildPieces != null)
                    {
                        MethodInfo[] methods = typeof(Humanoid).GetMethods(InstanceFlags);
                        for (int i = 0; i < methods.Length; ++i)
                        {
                            MethodInfo method = methods[i];
                            if (!string.Equals(method.Name, "UnequipItem", StringComparison.Ordinal)) continue;
                            ParameterInfo[] parameters = method.GetParameters();
                            if (parameters.Length == 0 || parameters[0].ParameterType != typeof(ItemDrop.ItemData)) continue;

                            object[] args = new object[parameters.Length];
                            args[0] = current;
                            bool supported = true;
                            for (int a = 1; a < parameters.Length; ++a)
                            {
                                if (parameters[a].HasDefaultValue) args[a] = parameters[a].DefaultValue;
                                else if (parameters[a].ParameterType == typeof(bool)) args[a] = true;
                                else if (!parameters[a].ParameterType.IsValueType) args[a] = null;
                                else { supported = false; break; }
                            }
                            if (!supported) continue;
                            method.Invoke(player, args);
                            cancelled = true;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Symbiosis build-tool unequip failed safely: {ex.GetType().Name}: {ex.Message}");
            }

            if (cancelled)
                _debugLog?.Invoke("Symbiosis entry cleared the player's active vanilla build mode/build tool.");
        }

        private static void CancelHiddenHumanAttack(Player player)
        {
            if (player == null) return;

            try
            {
                // Prefer Valheim's own cancellation routine when present so draw/reload/animation
                // state is released cleanly. Reflection keeps this compatible across minor builds.
                HumanoidAbortAttackMethod?.Invoke(player, null);
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Symbiosis human AbortAttack failed safely: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                // Defensive fallback: an already-created Attack object can receive a late animation
                // trigger even after input is blocked. Nulling it prevents the hidden player's
                // weapon (notably a preloaded crossbow) from spawning its projectile.
                if (HumanoidCurrentAttackField != null && HumanoidCurrentAttackField.GetValue(player) != null)
                    HumanoidCurrentAttackField.SetValue(player, null);
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Symbiosis human current-attack clear failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        internal static bool TryToggleOff(Player player, out string message)
        {
            message = string.Empty;
            if (!Active || player == null || player != _rider) return false;
            Dismount(ActiveFormDisplayName + "-Symbiose beendet.");
            message = ActiveFormDisplayName + "-Symbiose loest sich...";
            return true;
        }

        private static bool ValidateActiveSessionOrRecover()
        {
            if (!Active) return false;

            float now = Time.unscaledTime;
            if (_transitionState != SymbiosisTransitionState.None && _transitionStartedAt > 0f &&
                now - _transitionStartedAt > TransformationWatchdogSeconds)
            {
                _debugLog?.Invoke($"Symbiosis transition watchdog fired after {now - _transitionStartedAt:0.00}s ({_transitionState}).");
                EmergencyRelease("Symbiose-Uebergang hing fest und wurde sicher beendet.");
                return false;
            }

            // Most integrity conditions are already checked every Tick below. The slower check is for
            // cross-system residue that can survive a scene/respawn edge case without throwing.
            if (now < _nextActiveIntegrityCheckAt) return Active;
            _nextActiveIntegrityCheckAt = now + 0.50f;

            if (_rider == null || _mountedDrake == null || _rider != Player.m_localPlayer)
            {
                EmergencyRelease("Symbiose-Zustand war nicht mehr gueltig und wurde zurueckgesetzt.");
                return false;
            }

            if (_rider.gameObject == null || _mountedDrake.gameObject == null)
            {
                EmergencyRelease("Symbiose-Koerper war nicht mehr verfuegbar und wurde zurueckgesetzt.");
                return false;
            }

            return Active;
        }

        private static void ClearQueuedInputWhileUiBlocked()
        {
            // A key pressed while a menu is open must never fire immediately after closing it.
            // Only queued/edge-triggered state is cleared; active movement/combat animations are not
            // force-cancelled, so normal gameplay timing remains unchanged.
            _werewolfMovementJumpQueuedUntil = 0f;
            ResetEgoFineTuneHold();
        }

        /// <summary>
        /// Called from Plugin.Update for lifecycle, camera ownership and explicit symbiosis input.
        /// While possessed, the Drake's native MonsterAI is skipped completely; the existing Mount
        /// controller is the sole source of flight movement and manual attacks.
        /// </summary>
        internal static void Tick()
        {
            RunSymbiosisHousekeeping();
            if (!Active) return;
            if (!ValidateActiveSessionOrRecover()) return;

            if (_rider == null || _rider != Player.m_localPlayer || _mountedDrake == null)
            {
                ForceDismount(false);
                return;
            }

            if (_rider.IsDead())
            {
                HandleRiderDeathDuringSymbiosis();
                return;
            }

            if (_mountedDrake.IsDead())
            {
                ForceDismount(false);
                return;
            }

            ZNetView nview = GetCachedMountedView();
            if (nview == null || !nview.IsValid() || !nview.IsOwner())
            {
                Dismount(ActiveFormDisplayName + "-Symbiose beendet: Netzwerk-Ownership verloren.", false);
                return;
            }

            UpdateEgoFineTuneInput();

            // Symbiosis never uses Character.Attach. That API is designed for chairs/ships and can
            // inject vanilla detach/fall state into the local Player. Keep the real Player as a hidden
            // network anchor only. If a portal starts teleporting the Player, release symbiosis first
            // so Valheim owns the teleport lifecycle and cannot get stuck on a black screen.
            if (IsPlayerTeleporting(_rider))
            {
                Dismount(ActiveFormDisplayName + "-Symbiose fuer Portalreise beendet.", false);
                return;
            }

            // During the transformation sequence both bodies are frozen. Flight input, F6 and
            // dismount input are deliberately unlocked only after the visual/camera hand-off ends.
            if (_transitionState != SymbiosisTransitionState.None)
            {
                _rider.SetMoveDir(Vector3.zero);
                UpdateManualPossessionHold(_mountedDrake);
                AdvanceTransformation();
                return;
            }

            // Prevent the Player controller from moving the rider independently while the exact same
            // WASD keys are being consumed by Drake flight steering.
            _rider.SetMoveDir(Vector3.zero);

            // Full possession: native Drake AI must never be allowed to fly, chase or wander on its own.
            // Mouse look alone is NOT movement input. With no flight key held we hard-hold the Drake
            // at the current world pose; only explicit WASD/vertical input releases that hold.
            UpdateManualPossessionHold(_mountedDrake);

            bool uiBlocking = IsMountUiBlocking();

            // v4.2.0: pause queued edge-input and delayed custom impacts while a blocking UI is open.
            // This prevents a hit/jump from happening behind inventory/map/settings screens and keeps
            // the pending impact intact until normal gameplay resumes.
            if (uiBlocking)
                ClearQueuedInputWhileUiBlocked();
            else
                ProcessPendingTrollImpacts();

            // While any game/menu UI is open, symbiosis is a hard pause: no camera input, no
            // mount hotkeys and no flight input are consumed. The exact last owned camera pose
            // is restored on every render pass until the UI closes.
            if (!uiBlocking)
            {
                // Workers owns the visible camera while symbiosis is active. F6 only toggles
                // between the Drake chase camera and the Drake head camera here.
                if (Time.frameCount != _mountedAtFrame && Input.GetKeyDown(KeyCode.F6))
                {
                    _drakeEgoActive = !_drakeEgoActive;
                    if (!_drakeEgoActive) RestoreDrakeVisualsAfterEgoRender();
                    _thirdCameraSmoothInitialized = false;
                    _egoPositionSmoothInitialized = false;
                    _egoPositionSmoothFrame = -1;
                    _thirdCameraSmoothFrame = -1;
                    _thirdChaseYawInitialized = false;
                    _wingbeatSmoothInitialized = false;
                    _smoothedWingbeatMeters = 0f;
                    SaveSymbiosisCameraMode(_rider, _drakeEgoActive);
                    SaveGlobalPerspective(_rider, _drakeEgoActive);
                    _status?.Invoke(_drakeEgoActive ? ActiveFormDisplayName + "-Ego aktiviert." : ActiveFormDisplayName + "-Third-Person aktiviert.");
                }

                if (IsWerewolfForm && Input.GetKeyDown(KeyCode.Space))
                    _werewolfMovementJumpQueuedUntil = Time.unscaledTime + 0.18f;

                UpdateSymbiosisCameraLook();
                if (_werewolfComboActive) AdvanceWerewolfCombo();
                else HandleMountedAttacks();

                if (IsTrollForm)
                    UpdateTrollLogInteraction();

                // v1.3.0: E belongs to normal Valheim interaction again. Leaving symbiosis is
                // intentionally handled only by using the active bound necklace a second time.
                // This keeps doors, chests, pickups and hold-E world interactions available while
                // transformed instead of stealing the game's primary interaction key.
            }
        }

        private static void HandleMountedAttacks()
        {
            if (!Active || _mountedDrake == null || _transitionState != SymbiosisTransitionState.None) return;
            if (IsMountUiBlocking()) return;

            EnsureNativeDrakeAttackCatalog(_mountedDrake);

            // Keep the possessed Humanoid's look vector aligned with the camera every frame.
            // Native projectile attacks can spawn from a later animation event, so setting this
            // continuously is safer than only writing it on the click frame.
            try { _mountedDrake.SetLookDir(GetMountedAttackDirection(_mountedDrake), 0f); } catch { }

            // Werewolf controls are deliberately explicit and deterministic:
            // LMB = normal claw, MMB = native jump/leap attack, RMB+LMB = Blood Hunt.
            // Raw mouse state is checked first because Valheim's Block binding may be consumed by
            // the hidden Player before the possessed creature runtime observes it. ZInput remains
            // as a secondary compatibility path for rebound Attack/Block controls.
            if (IsWerewolfForm)
            {
                bool lmbDown = Input.GetMouseButtonDown(0) || IsGameplayButtonDown("Attack", 0);
                bool rmbHeld = Input.GetMouseButton(1) || IsGameplayButtonHeld("Block", 1);
                bool mmbDown = Input.GetMouseButtonDown(2);

                if (rmbHeld && lmbDown)
                {
                    _debugLog?.Invoke("Werewolf Blood Hunt chord detected: RMB held + LMB down.");
                    TryStartWerewolfCombo();
                    return;
                }

                if (lmbDown)
                {
                    TryUseWerewolfClawFallback();
                    return;
                }

                if (mmbDown)
                {
                    TryUseWerewolfJumpAttack();
                    return;
                }

                return;
            }

            int slot = -1;
            // v0.8.0: respect Valheim's rebound Attack/Block bindings instead of hard-wiring
            // every creature attack to raw mouse buttons. MMB remains the dedicated third slot.
            if (IsGameplayButtonDown("Attack", 0)) slot = 0;
            else if (IsGameplayButtonDown("Block", 1)) slot = 1;
            else if (Input.GetMouseButtonDown(2)) slot = 2;
            if (slot < 0) return;

            // v1.0.0: each non-Werewolf creature now has a clear form identity instead of
            // relying entirely on whatever native weapon slots happen to exist on the live prefab.
            // LMB stays native; RMB/MMB are deterministic form abilities.
            if (IsTrollForm && slot > 0)
            {
                TryUseTrollFormAbility(slot);
                return;
            }
            if (IsSerpentForm && slot > 0)
            {
                TryUseSerpentFormAbility(slot);
                return;
            }

            // v3.6.0: Drake combat identity is deterministic. LMB may still use the native
            // hatchling attack, but RMB/MMB always remain the curated Eisbolzen/Eisfaecher slots
            // even if a prefab exposes extra native attacks. This keeps controls stable across
            // Valheim/prefab variants and prevents native slot discovery from silently replacing
            // the advertised Drake abilities.
            if (IsDrakeForm && slot == 1)
            {
                float nowBolt = Time.time;
                if (nowBolt < _nextFrostBoltAt)
                {
                    _status?.Invoke($"Drake-Eisbolzen bereit in {Mathf.Max(0f, _nextFrostBoltAt - nowBolt):0.0}s");
                    return;
                }
                if (!CanSpendFormStamina(FrostBoltStamina, "Drake-Eisbolzen")) return;
                if (TryFireFrostPattern(_mountedDrake, 1, 0f, FrostProjectileDamage * 1.15f, FrostProjectileSpeed * 1.35f, "Eisbolzen"))
                {
                    SpendFormStamina(FrostBoltStamina);
                    _nextFrostBoltAt = nowBolt + FrostBoltCooldown;
                }
                return;
            }
            if (IsDrakeForm && slot == 2)
            {
                float nowBurst = Time.time;
                if (nowBurst < _nextFrostBurstAt)
                {
                    _status?.Invoke($"Drake-Eisfaecher bereit in {Mathf.Max(0f, _nextFrostBurstAt - nowBurst):0.0}s");
                    return;
                }
                if (!CanSpendFormStamina(FrostBurstStamina, "Drake-Eisfaecher")) return;
                if (TryFireFrostPattern(_mountedDrake, 5, 28f, FrostProjectileDamage * 0.48f, FrostProjectileSpeed * 0.95f, "Eisfaecher"))
                {
                    SpendFormStamina(FrostBurstStamina);
                    _nextFrostBurstAt = nowBurst + FrostBurstCooldown;
                }
                return;
            }

            if (TryUseNativeDrakeAttack(slot))
            {
                if (IsTrollForm && slot == 0)
                {
                    if (_trollHasLog)
                        ConsumeTrollLogSwing();
                    else
                        ScheduleTrollUnarmedWorldPunch();
                }
                return;
            }

            // Preserve the proven frost salvo as the LMB fallback. Other forms use native attacks only.
            if (slot != 0)
            {
                if (_nativeAttackCatalogLogged)
                    _status?.Invoke(slot == 1 ? "Kein zweiter " + ActiveFormDisplayName + "-Angriff vorhanden." : "Kein dritter " + ActiveFormDisplayName + "-Angriff vorhanden.");
                return;
            }
            if (!IsDrakeForm)
            {
                if (_nativeAttackCatalogLogged) _status?.Invoke("Kein nativer " + ActiveFormDisplayName + "-Angriff gefunden.");
                return;
            }

            float now = Time.time;
            if (now < _nextFrostSalvoAt)
            {
                float remaining = Mathf.Max(0f, _nextFrostSalvoAt - now);
                _status?.Invoke($"Frostsalve bereit in {remaining:0.0}s");
                return;
            }

            if (!CanSpendFormStamina(FrostSalvoStamina, "Drake-Frostsalve")) return;
            if (TryFireVanillaFrostSalvo(_mountedDrake))
            {
                SpendFormStamina(FrostSalvoStamina);
                _nextFrostSalvoAt = now + FrostSalvoCooldown;
                _status?.Invoke("Drake-Frostsalve!");
            }
        }

        private static bool CanSpendFormStamina(float cost, string label)
        {
            if (cost <= 0f || _rider == null) return true;
            try
            {
                if (_rider.HaveStamina(cost)) return true;
                _status?.Invoke($"{label}: nicht genug Ausdauer ({cost:0}).");
                return false;
            }
            catch
            {
                // Fail open if Valheim changes the stamina API; never soft-lock form combat.
                return true;
            }
        }

        private static void SpendFormStamina(float cost)
        {
            if (cost <= 0f || _rider == null) return;
            try { _rider.UseStamina(cost); } catch { }
        }

        private static bool TryUseTrollFormAbility(int slot)
        {
            if (!Active || !IsTrollForm || _mountedDrake == null) return false;
            float now = Time.time;

            if (slot == 1)
            {
                if (now < _nextTrollGroundSlamAt)
                {
                    _status?.Invoke($"Troll-Bodenschlag bereit in {Mathf.Max(0f, _nextTrollGroundSlamAt - now):0.0}s");
                    return true;
                }
                if (!CanSpendFormStamina(TrollGroundSlamStamina, "Troll-Bodenschlag")) return true;
                float abilityScale = GetBoundAbilityMultiplier(_activeBoundLevel);
                bool logSlam = _trollHasLog;
                float slamDamageScale = logSlam ? TrollLogGroundSlamDamageMultiplier : 1f;
                float slamRadiusScale = logSlam ? TrollLogGroundSlamRadiusMultiplier : 1f;
                float slamPushScale = logSlam ? TrollLogGroundSlamPushMultiplier : 1f;
                float slamWorldScale = logSlam ? TrollLogGroundSlamWorldDamageMultiplier : 1f;
                string slamLabel = logSlam ? "Troll-Stamm-Bodenschlag" : "Troll-Bodenschlag";

                SpendFormStamina(TrollGroundSlamStamina);
                _nextTrollGroundSlamAt = now + TrollGroundSlamCooldown;
                _pendingTrollGroundSlamAt = now + TrollGroundSlamImpactDelay;
                _pendingTrollGroundSlamHasLog = logSlam;
                _pendingTrollGroundSlamBoundLevel = _activeBoundLevel;
                PlayMountedAttackAnimation();
                _status?.Invoke(slamLabel + "...");
                return true;
            }

            if (slot == 2)
            {
                if (now < _nextTrollRoarAt)
                {
                    _status?.Invoke($"Troll-Schockgebruell bereit in {Mathf.Max(0f, _nextTrollRoarAt - now):0.0}s");
                    return true;
                }
                if (!CanSpendFormStamina(TrollRoarStamina, "Troll-Schockgebruell")) return true;
                SpendFormStamina(TrollRoarStamina);
                _nextTrollRoarAt = now + TrollRoarCooldown;
                _pendingTrollRoarAt = now + TrollRoarImpactDelay;
                _pendingTrollRoarBoundLevel = _activeBoundLevel;
                try
                {
                    if (_drakeAnimator == null) _drakeAnimator = ResolveDrakeAnimator(_mountedDrake);
                    TryTriggerAnimatorParameter(_drakeAnimator, "roar");
                    TryTriggerAnimatorParameter(_drakeAnimator, "taunt");
                }
                catch { }
                _status?.Invoke("Troll-Schockgebruell...");
                return true;
            }
            return false;
        }

        private static bool TryUseSerpentFormAbility(int slot)
        {
            if (!Active || !IsSerpentForm || _mountedDrake == null) return false;
            float now = Time.time;

            if (slot == 1)
            {
                if (now < _nextSerpentBiteBurstAt)
                {
                    _status?.Invoke($"Seeschlangen-Bissstoss bereit in {Mathf.Max(0f, _nextSerpentBiteBurstAt - now):0.0}s");
                    return true;
                }
                Character target = FindWerewolfLookTarget(SerpentBiteBurstRange);
                if (target == null)
                {
                    _status?.Invoke("Seeschlangen-Bissstoss: kein Ziel in Reichweite.");
                    return true;
                }
                if (!CanSpendFormStamina(SerpentBiteBurstStamina, "Seeschlangen-Bissstoss")) return true;
                try
                {
                    Vector3 to = target.transform.position - _mountedDrake.transform.position;
                    to.y = 0f;
                    if (to.sqrMagnitude > 0.01f)
                    {
                        to.Normalize();
                        _mountedDrake.transform.rotation = Quaternion.LookRotation(to, Vector3.up);
                        _mountedDrake.SetLookDir(to, 0f);

                        // Core Serpent identity: Bissstoss is a real surface lunge, not a ranged
                        // instant melee hit. The short velocity window keeps Valheim collision and
                        // the existing surface lock authoritative while visibly carrying the body
                        // into the target. Y stays zero so this cannot reintroduce diving.
                        _serpentBiteLungeVelocity = to * SerpentBiteLungeSpeed;
                        _serpentBiteLungeVelocity.y = 0f;
                        _serpentBiteLungeUntil = Time.time + SerpentBiteLungeDuration;
                    }
                    float abilityScale = GetBoundAbilityMultiplier(_activeBoundLevel);
                    HitData hit = new HitData();
                    hit.m_damage.m_pierce = SerpentBiteBurstPierce * abilityScale;
                    hit.m_damage.m_blunt = SerpentBiteBurstBlunt * abilityScale;
                    hit.m_damage.m_poison = SerpentBiteBurstPoison * abilityScale;
                    hit.m_pushForce = 10f;
                    hit.m_staggerMultiplier = 1.5f;
                    hit.SetAttacker(_mountedDrake);
                    target.Damage(hit);
                    SpendFormStamina(SerpentBiteBurstStamina);
                    PlayMountedAttackAnimation();
                    _nextSerpentBiteBurstAt = now + SerpentBiteBurstCooldown;
                    _status?.Invoke("Seeschlangen-Bissstoss!");
                }
                catch (Exception ex)
                {
                    _debugLog?.Invoke("Serpent bite burst failed safely: " + ex.GetType().Name + ": " + ex.Message);
                }
                return true;
            }

            if (slot == 2)
            {
                if (now < _nextSerpentTailSweepAt)
                {
                    _status?.Invoke($"Seeschlangen-Schwanzfeger bereit in {Mathf.Max(0f, _nextSerpentTailSweepAt - now):0.0}s");
                    return true;
                }
                if (!CanSpendFormStamina(SerpentTailSweepStamina, "Seeschlangen-Schwanzfeger")) return true;
                float abilityScale = GetBoundAbilityMultiplier(_activeBoundLevel);
                int hits = DamageHostilesInRadius(_mountedDrake.transform.position, SerpentTailSweepRadius,
                    SerpentTailSweepDamage * abilityScale, 0f, SerpentTailSweepPush, 1.7f, "Seeschlangen-Schwanzfeger");
                SpendFormStamina(SerpentTailSweepStamina);
                _nextSerpentTailSweepAt = now + SerpentTailSweepCooldown;
                PlayMountedAttackAnimation();
                _status?.Invoke(hits > 0 ? $"Seeschlangen-Schwanzfeger! {hits} Ziel(e)." : "Seeschlangen-Schwanzfeger!");
                return true;
            }
            return false;
        }

        private static int DamageHostilesInRadius(Vector3 center, float radius, float blunt, float pierce, float push, float stagger, string label)
        {
            int hitCount = 0;
            try
            {
                float radiusSqr = radius * radius;
                foreach (Character target in Character.GetAllCharacters())
                {
                    if (!IsValidWerewolfEnemy(target)) continue;
                    Vector3 delta = target.transform.position - center;
                    if (delta.sqrMagnitude > radiusSqr) continue;

                    HitData hit = new HitData();
                    hit.m_damage.m_blunt = blunt;
                    hit.m_damage.m_pierce = pierce;
                    hit.m_pushForce = push;
                    hit.m_staggerMultiplier = stagger;
                    hit.m_point = target.transform.position;
                    hit.m_dir = delta.sqrMagnitude > 0.001f ? delta.normalized : _mountedDrake.transform.forward;
                    hit.SetAttacker(_mountedDrake);
                    target.Damage(hit);
                    hitCount++;
                }
                _debugLog?.Invoke($"{label} resolved: radius={radius:0.0}m, hits={hitCount}, blunt={blunt:0.#}, pierce={pierce:0.#}, push={push:0.#}.");
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke(label + " failed safely: " + ex.GetType().Name + ": " + ex.Message);
            }
            return hitCount;
        }

        private static int DamageNaturalDestructiblesInRadius(Vector3 center, float radius, float blunt, float chop, float pickaxe, string label)
        {
            int hitCount = 0;
            try
            {
                Collider[] colliders = Physics.OverlapSphere(center, radius, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                HashSet<int> damaged = new HashSet<int>();
                for (int i = 0; i < colliders.Length; ++i)
                {
                    Collider collider = colliders[i];
                    if (collider == null) continue;
                    Transform t = collider.transform;
                    if (t == null) continue;
                    if (t == _mountedDrake.transform || t.IsChildOf(_mountedDrake.transform)) continue;
                    if (_rider != null && (t == _rider.transform || t.IsChildOf(_rider.transform))) continue;

                    // Never smash player-built pieces with the passive breacher pulse. The Troll
                    // should clear trees/rocks and natural blockers without deleting the base the
                    // player is standing in. Characters are handled by DamageHostilesInRadius.
                    if (collider.GetComponentInParent<Piece>() != null) continue;
                    if (collider.GetComponentInParent<Character>() != null) continue;

                    IDestructible destructible = null;
                    Component owner = null;
                    Component[] components = collider.GetComponentsInParent<Component>(true);
                    for (int c = 0; c < components.Length; ++c)
                    {
                        if (components[c] is IDestructible candidate)
                        {
                            destructible = candidate;
                            owner = components[c];
                            break;
                        }
                    }
                    if (destructible == null || owner == null) continue;
                    int id = owner.GetInstanceID();
                    if (!damaged.Add(id)) continue;

                    Vector3 delta = owner.transform.position - center;
                    HitData hit = new HitData();
                    hit.m_damage.m_blunt = blunt;
                    hit.m_damage.m_chop = chop;
                    hit.m_damage.m_pickaxe = pickaxe;
                    hit.m_pushForce = 0f;
                    hit.m_staggerMultiplier = 1f;
                    hit.m_point = owner.transform.position;
                    hit.m_dir = delta.sqrMagnitude > 0.001f ? delta.normalized : _mountedDrake.transform.forward;
                    hit.SetAttacker(_mountedDrake);
                    destructible.Damage(hit);
                    hitCount++;
                }
                _debugLog?.Invoke($"{label} world-breacher resolved: radius={radius:0.0}m, naturalTargets={hitCount}, blunt={blunt:0.#}, chop={chop:0.#}, pickaxe={pickaxe:0.#}.");
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke(label + " world-breacher failed safely: " + ex.GetType().Name + ": " + ex.Message);
            }
            return hitCount;
        }

        private static bool IsGameplayButtonDown(string name, int fallbackMouseButton)
        {
            try
            {
                if (ZInputGetButtonDownMethod != null && (bool)ZInputGetButtonDownMethod.Invoke(null, new object[] { name })) return true;
            }
            catch { }
            return Input.GetMouseButtonDown(fallbackMouseButton);
        }

        private static bool IsGameplayButtonHeld(string name, int fallbackMouseButton)
        {
            try
            {
                if (ZInputGetButtonMethod != null && (bool)ZInputGetButtonMethod.Invoke(null, new object[] { name })) return true;
            }
            catch { }
            return Input.GetMouseButton(fallbackMouseButton);
        }

        private static bool TryUseWerewolfClawFallback()
        {
            if (!Active || !IsWerewolfForm || _mountedDrake == null || _werewolfComboActive) return false;

            float now = Time.time;
            if (now < _nextWerewolfClawAt) return true;

            // Use Valheim's real Humanoid/Attack pipeline first. Merely setting the Animator trigger
            // string is not enough for Fenring: StartAttack drives the creature's ZSyncAnimation and
            // native animation events. This gives LMB the same real attack animation path that the
            // working MMB jump already uses. Werewolf star scaling is applied later by the existing
            // Character.Damage Harmony prefix, so native damage remains balanced by the bound level.
            EnsureNativeDrakeAttackCatalog(_mountedDrake);
            NativeDrakeAttackEntry preferred = GetPreferredWerewolfNativeAttack(false);
            Humanoid humanoid = _mountedDrake as Humanoid;
            if (preferred != null && preferred.Item != null && humanoid != null && !_mountedDrake.IsDead())
            {
                if (_mountedDrake.InAttack()) return true;

                try
                {
                    if (humanoid.GetCurrentWeapon() == preferred.Item || humanoid.EquipItem(preferred.Item, false))
                    {
                        try { _mountedDrake.SetLookDir(GetMountedAttackDirection(_mountedDrake), 0f); } catch { }
                        if (_mountedDrake.StartAttack(null, preferred.Secondary))
                        {
                            _nextWerewolfClawAt = now + WerewolfClawCooldown;
                            _nextNativeAttackAt = now + 0.15f;
                            _status?.Invoke("Werwolf-Klaue!");
                            _debugLog?.Invoke($"Werewolf LMB native claw started -> {preferred.Label} [{preferred.Animation}].");
                            return true;
                        }

                        _debugLog?.Invoke($"Werewolf LMB native claw StartAttack returned false -> {preferred.Label} [{preferred.Animation}]; using deterministic damage fallback.");
                    }
                    else
                    {
                        _debugLog?.Invoke($"Werewolf LMB could not equip native claw -> {preferred.Label}; using deterministic damage fallback.");
                    }
                }
                catch (Exception ex)
                {
                    _debugLog?.Invoke("Werewolf LMB native claw failed safely: " + ex.GetType().Name + ": " + ex.Message + "; using deterministic damage fallback.");
                }
            }

            // Compatibility fallback for variants where the natural claw cannot be started as a
            // Humanoid weapon. Keep the proven synthetic hit, but route the visual trigger through
            // ZSyncAnimation first rather than talking directly to the raw Animator.
            _nextWerewolfClawAt = now + WerewolfClawCooldown;
            Character target = FindWerewolfMeleeTarget(WerewolfClawRange);
            PlayWerewolfNativeAnimation(false);
            if (target == null)
            {
                _status?.Invoke("Werwolf-Klaue");
                return true;
            }

            DealWerewolfClawHit(target, "claw-fallback");
            _status?.Invoke("Werwolf-Klaue!");
            return true;
        }

        private static Character FindWerewolfMeleeTarget(float range)
        {
            if (_mountedDrake == null) return null;
            Vector3 origin = _mountedDrake.transform.position + Vector3.up * Mathf.Max(0.5f, _mountedDrake.GetRadius() * 0.5f);
            Vector3 forward = _mountedDrake.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            forward.Normalize();

            int count = Physics.OverlapSphereNonAlloc(
                _mountedDrake.transform.position,
                range + 1.5f,
                _werewolfMeleeColliderBuffer,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);

            Character best = null;
            float bestScore = float.MaxValue;
            _werewolfMeleeInspected.Clear();
            for (int i = 0; i < count; i++)
            {
                Collider collider = _werewolfMeleeColliderBuffer[i];
                _werewolfMeleeColliderBuffer[i] = null;
                if (collider == null) continue;
                Character candidate = collider.GetComponentInParent<Character>();
                if (candidate == null || !_werewolfMeleeInspected.Add(candidate) || !IsValidWerewolfEnemy(candidate)) continue;

                Vector3 targetPoint = candidate.transform.position + Vector3.up * Mathf.Max(0.35f, candidate.GetRadius() * 0.35f);
                Vector3 to = targetPoint - origin;
                float distance = to.magnitude;
                float allowed = range + candidate.GetRadius();
                if (distance < 0.05f || distance > allowed) continue;
                Vector3 flat = new Vector3(to.x, 0f, to.z);
                if (flat.sqrMagnitude < 0.001f) flat = forward;
                float dot = Vector3.Dot(forward, flat.normalized);
                if (dot < 0.20f) continue;
                if (!HasWerewolfLineOfSight(origin, candidate, distance + 0.5f)) continue;
                float score = distance - dot * 0.75f;
                if (score < bestScore) { bestScore = score; best = candidate; }
            }
            return best;
        }

        private static bool TryStartWerewolfCombo()
        {
            if (!Active || !IsWerewolfForm || _mountedDrake == null || _rider == null || _werewolfComboActive) return false;
            float now = Time.time;
            if (now < _nextWerewolfComboAt)
            {
                _status?.Invoke($"Blutjagd bereit in {Mathf.Max(0f, _nextWerewolfComboAt - now):0.0}s");
                return true;
            }

            Character first = FindWerewolfLookTarget(WerewolfComboInitialRange);
            if (first == null)
            {
                _debugLog?.Invoke("Werewolf Blood Hunt chord accepted, but no valid first target was resolved inside 15m.");
                _status?.Invoke("Blutjagd: Kein Gegner im Blick (max. 15m).");
                return true;
            }

            try
            {
                if (!_rider.HaveStamina(WerewolfComboStaminaCost))
                {
                    _status?.Invoke($"Blutjagd braucht {WerewolfComboStaminaCost:0} Ausdauer.");
                    return true;
                }
                _rider.UseStamina(WerewolfComboStaminaCost);
            }
            catch { }

            BuildWerewolfComboTargetChain(first);

            _werewolfComboOriginPosition = _mountedDrake.transform.position;
            _werewolfComboOriginRotation = _mountedDrake.transform.rotation;
            _werewolfComboIndex = 0;
            _werewolfComboNextStepAt = now;
            _werewolfComboActive = true;
            _nextWerewolfComboAt = now + WerewolfComboCooldown;
            AbortMountedCreatureAttack();
            _status?.Invoke($"Blutjagd! {_werewolfComboTargets.Count} Ziel{(_werewolfComboTargets.Count == 1 ? "" : "e")}");
            float estimatedDuration = Mathf.Max(0f, (_werewolfComboTargets.Count - 1) * WerewolfComboStepSeconds + WerewolfComboReturnDelaySeconds);
            _debugLog?.Invoke($"Werewolf Blood Hunt started: targets={_werewolfComboTargets.Count}, hop={WerewolfComboStepSeconds:0.000}s, estimated={estimatedDuration:0.00}s, cooldown={WerewolfComboCooldown:0.#}s, stamina={WerewolfComboStaminaCost:0.#}.");
            return true;
        }

        private static void AdvanceWerewolfCombo()
        {
            if (!_werewolfComboActive) return;
            if (!Active || !IsWerewolfForm || _mountedDrake == null || _rider == null || _rider.IsDead())
            {
                ResetWerewolfCombo(false);
                return;
            }
            if (Time.time < _werewolfComboNextStepAt) return;

            while (_werewolfComboIndex < _werewolfComboTargets.Count)
            {
                Character target = _werewolfComboTargets[_werewolfComboIndex++];
                if (!IsValidWerewolfEnemy(target)) continue;

                Vector3 destination = ResolveWerewolfBlinkPosition(target);
                Vector3 face = target.transform.position - destination;
                face.y = 0f;
                Quaternion rotation = face.sqrMagnitude > 0.001f ? Quaternion.LookRotation(face.normalized, Vector3.up) : _mountedDrake.transform.rotation;
                MoveWerewolfBody(destination, rotation);
                DealWerewolfComboHit(target);
                PlayWerewolfNativeAnimation(false);
                // Blood Hunt is meant to read as a near-instant assassination chain, not as a
                // sequence of ordinary attacks. At 85 ms per hop even the full five-target route
                // finishes in well under half a second, while still giving Unity one rendered
                // frame between most teleports on common frame rates.
                _werewolfComboNextStepAt = Time.time +
                    (_werewolfComboIndex >= _werewolfComboTargets.Count ? WerewolfComboReturnDelaySeconds : WerewolfComboStepSeconds);
                return;
            }

            MoveWerewolfBody(_werewolfComboOriginPosition, _werewolfComboOriginRotation);
            int hitCount = _werewolfComboTargets.Count;
            ResetWerewolfCombo(true);
            _status?.Invoke($"Blutjagd beendet - {hitCount} Ziel{(hitCount == 1 ? "" : "e")} | Rueckkehr zur Startposition");
        }

        private static void ResetWerewolfCombo(bool keepCooldown)
        {
            _werewolfComboActive = false;
            _werewolfComboTargets.Clear();
            _werewolfComboIndex = 0;
            _werewolfComboNextStepAt = 0f;
            _werewolfComboOriginPosition = Vector3.zero;
            _werewolfComboOriginRotation = Quaternion.identity;
            if (!keepCooldown && !Active) _nextWerewolfComboAt = 0f;
        }

        private static void AbortMountedCreatureAttack()
        {
            try
            {
                Humanoid humanoid = _mountedDrake as Humanoid;
                if (humanoid != null) HumanoidAbortAttackMethod?.Invoke(humanoid, null);
            }
            catch { }
        }

        private static Character FindWerewolfLookTarget(float maxRange)
        {
            Camera camera = Camera.main;
            Vector3 origin = camera != null ? camera.transform.position : GetDrakeEyePosition();
            Vector3 forward = camera != null ? camera.transform.forward : _mountedDrake.transform.forward;
            if (forward.sqrMagnitude < 0.001f) forward = _mountedDrake.transform.forward;
            forward.Normalize();

            // First prefer a generous sphere-cast through the crosshair. This is much closer to
            // what the player means by "I am looking at that enemy" than a pure center-dot test,
            // especially with the large Fenring body/camera offset. Only evaluated on activation.
            try
            {
                int aimCount = Physics.SphereCastNonAlloc(origin, 0.85f, forward, _werewolfAimHitBuffer, maxRange, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                Character bestAimed = null;
                float bestAimedDistance = float.MaxValue;
                for (int i = 0; i < aimCount; ++i)
                {
                    RaycastHit aimHit = _werewolfAimHitBuffer[i];
                    Collider collider = aimHit.collider;
                    _werewolfAimHitBuffer[i] = default(RaycastHit);
                    if (collider == null || aimHit.distance >= bestAimedDistance) continue;
                    Transform t = collider.transform;
                    if (t == _mountedDrake.transform || t.IsChildOf(_mountedDrake.transform) || t == _rider.transform || t.IsChildOf(_rider.transform)) continue;
                    Character aimed = collider.GetComponentInParent<Character>();
                    if (!IsValidWerewolfEnemy(aimed)) continue;
                    if (!HasWerewolfLineOfSight(origin, aimed, maxRange + aimed.GetRadius())) continue;
                    bestAimed = aimed;
                    bestAimedDistance = aimHit.distance;
                }
                if (bestAimed != null)
                {
                    _debugLog?.Invoke($"Werewolf Blood Hunt aim target resolved by sphere-cast: {bestAimed.name}, distance={bestAimedDistance:0.0}m.");
                    return bestAimed;
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke("Werewolf Blood Hunt sphere-cast target probe failed safely: " + ex.GetType().Name + ": " + ex.Message);
            }

            // Target selection is intentionally evaluated only when Blood Hunt is pressed, so a
            // broad character scan here has no per-frame cost. Prefer the enemy closest to the
            // crosshair, but allow a forgiving cone instead of the old ~17 degree requirement.
            Character best = null;
            float bestScore = float.MaxValue;
            foreach (Character candidate in Character.GetAllCharacters())
            {
                if (!IsValidWerewolfEnemy(candidate)) continue;
                Vector3 aimPoint = candidate.transform.position + Vector3.up * Mathf.Max(0.4f, candidate.GetRadius() * 0.45f);
                Vector3 to = aimPoint - origin;
                float distance = to.magnitude;
                if (distance < 0.1f || distance > maxRange + candidate.GetRadius()) continue;
                Vector3 dir = to / distance;
                float dot = Vector3.Dot(forward, dir);
                if (dot < WerewolfAimDotThreshold) continue;
                if (!HasWerewolfLineOfSight(origin, candidate, distance + 0.75f)) continue;
                float score = (1f - dot) * 12f + distance / Mathf.Max(1f, maxRange);
                if (score < bestScore) { bestScore = score; best = candidate; }
            }
            return best;
        }

        private static void BuildWerewolfComboTargetChain(Character first)
        {
            _werewolfComboTargets.Clear();
            if (!IsValidWerewolfEnemy(first)) return;
            _werewolfComboTargets.Add(first);

            // v2.8.0: Blood Hunt should read as a deliberate pack-clearing route, not a blind
            // nearest-neighbour teleport. Snapshot once per activation, then prefer candidates
            // that keep the chain inside the densest visible cluster. A wall/large obstacle now
            // breaks the chain instead of letting the Werewolf blink through solid geometry.
            List<Character> candidates = Character.GetAllCharacters();
            Character current = first;
            while (_werewolfComboTargets.Count < WerewolfComboMaxTargets)
            {
                Character best = null;
                int bestCluster = -1;
                float bestSqr = float.MaxValue;
                Vector3 origin = current.transform.position;
                Vector3 losOrigin = origin + Vector3.up * Mathf.Max(0.45f, current.GetRadius() * 0.40f);
                float maxSqr = WerewolfComboChainRange * WerewolfComboChainRange;

                for (int i = 0; i < candidates.Count; i++)
                {
                    Character candidate = candidates[i];
                    if (!IsValidWerewolfEnemy(candidate) || _werewolfComboTargets.Contains(candidate)) continue;
                    float sqr = (candidate.transform.position - origin).sqrMagnitude;
                    if (sqr > maxSqr) continue;
                    float distance = Mathf.Sqrt(Mathf.Max(0.001f, sqr));
                    if (!HasWerewolfLineOfSight(losOrigin, candidate, distance + candidate.GetRadius() + 0.75f)) continue;

                    int cluster = CountWerewolfChainNeighbors(candidate, candidates);
                    if (cluster > bestCluster || (cluster == bestCluster && sqr < bestSqr))
                    {
                        bestCluster = cluster;
                        bestSqr = sqr;
                        best = candidate;
                    }
                }

                if (best == null) break;
                _werewolfComboTargets.Add(best);
                current = best;
            }

            _debugLog?.Invoke($"Werewolf Blood Hunt smart chain built: {_werewolfComboTargets.Count} target(s), visible-cluster priority, no wall traversal.");
        }

        private static int CountWerewolfChainNeighbors(Character center, List<Character> candidates)
        {
            if (center == null || candidates == null) return 0;
            int count = 0;
            float maxSqr = WerewolfComboChainRange * WerewolfComboChainRange;
            Vector3 origin = center.transform.position;
            for (int i = 0; i < candidates.Count; i++)
            {
                Character candidate = candidates[i];
                if (candidate == center || !IsValidWerewolfEnemy(candidate) || _werewolfComboTargets.Contains(candidate)) continue;
                if ((candidate.transform.position - origin).sqrMagnitude <= maxSqr) count++;
            }
            return count;
        }

        private static bool IsValidWerewolfEnemy(Character candidate)
        {
            if (candidate == null || candidate == _mountedDrake || candidate == _rider || candidate is Player || candidate.IsDead()) return false;
            try
            {
                if (CharacterFactionField != null && _rider != null)
                {
                    object candidateFaction = CharacterFactionField.GetValue(candidate);
                    object playerFaction = CharacterFactionField.GetValue(_rider);
                    if (candidateFaction != null && playerFaction != null && Equals(candidateFaction, playerFaction)) return false;
                }
            }
            catch { }
            return candidate.GetComponent<BaseAI>() != null;
        }

        private static bool HasWerewolfLineOfSight(Vector3 origin, Character candidate, float maxDistance)
        {
            if (candidate == null) return false;
            Vector3 aim = candidate.transform.position + Vector3.up * Mathf.Max(0.4f, candidate.GetRadius() * 0.45f);
            Vector3 dir = aim - origin;
            float distance = dir.magnitude;
            if (distance < 0.05f) return true;
            int hitCount = Physics.RaycastNonAlloc(origin, dir / distance, _werewolfLosHitBuffer, Mathf.Min(maxDistance, distance + 0.5f), Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            Character hitCharacter = null;
            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit rayHit = _werewolfLosHitBuffer[i];
                _werewolfLosHitBuffer[i] = default(RaycastHit);
                Collider collider = rayHit.collider;
                if (collider == null) continue;
                Transform t = collider.transform;
                if (t == _mountedDrake.transform || t.IsChildOf(_mountedDrake.transform) || t == _rider.transform || t.IsChildOf(_rider.transform)) continue;
                if (rayHit.distance >= nearest) continue;
                nearest = rayHit.distance;
                hitCharacter = collider.GetComponentInParent<Character>();
            }
            return hitCharacter == candidate;
        }

        private static Vector3 ResolveWerewolfBlinkPosition(Character target)
        {
            Vector3 targetPos = target.transform.position;
            Vector3 away = _mountedDrake.transform.position - targetPos;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = -target.transform.forward;
            if (away.sqrMagnitude < 0.01f) away = Vector3.back;
            away.Normalize();
            float distance = Mathf.Max(1.2f, target.GetRadius() + _mountedDrake.GetRadius() + 0.35f);
            Vector3 wanted = targetPos + away * distance;

            int groundCount = Physics.RaycastNonAlloc(wanted + Vector3.up * 2.5f, Vector3.down, _werewolfGroundHitBuffer, 6f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            for (int i = 0; i < groundCount; i++)
            {
                RaycastHit groundHit = _werewolfGroundHitBuffer[i];
                _werewolfGroundHitBuffer[i] = default(RaycastHit);
                Collider collider = groundHit.collider;
                if (collider == null || groundHit.normal.y < 0.30f) continue;
                Transform t = collider.transform;
                if (t == target.transform || t.IsChildOf(target.transform) || t == _mountedDrake.transform || t.IsChildOf(_mountedDrake.transform)) continue;
                if (groundHit.distance < nearest)
                {
                    nearest = groundHit.distance;
                    wanted.y = groundHit.point.y + 0.08f;
                }
            }
            return wanted;
        }

        private static void MoveWerewolfBody(Vector3 position, Quaternion rotation)
        {
            if (_mountedDrake == null) return;
            try
            {
                _mountedDrake.transform.SetPositionAndRotation(position, rotation);
                Rigidbody body = _mountedDrake.GetComponent<Rigidbody>();
                if (body != null)
                {
                    body.position = position;
                    body.rotation = rotation;
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
                _mountedDrake.SetMoveDir(Vector3.zero);
                _mountedDrake.SetRun(false);
                _mountedDrake.SetWalk(false);
                SyncRiderToSeat();
                _idleHoldPosition = position;
                _idleHoldRotation = rotation;
                _idleHoldActive = true;
            }
            catch { }
        }

        private static void DealWerewolfComboHit(Character target)
        {
            DealWerewolfClawHit(target, "Blood Hunt");
        }

        private static void DealWerewolfClawHit(Character target, string sourceLabel)
        {
            if (target == null || _mountedDrake == null) return;
            try
            {
                HitData hit = new HitData();
                if (!TryCopyWerewolfNativeDamage(hit))
                    hit.m_damage.m_slash = WerewolfClawFallbackSlash;
                hit.SetAttacker(_mountedDrake);
                target.Damage(hit);
                _debugLog?.Invoke($"Werewolf {sourceLabel} hit -> {target.name}; baseFallback={WerewolfClawFallbackSlash:0.#}, starMultiplier=x{GetWerewolfStarDamageMultiplier(_activeBoundLevel):0.##}.");
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke("Werewolf " + sourceLabel + " hit failed safely: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool TryCopyWerewolfNativeDamage(HitData hit)
        {
            if (hit == null) return false;
            EnsureNativeDrakeAttackCatalog(_mountedDrake);
            NativeDrakeAttackEntry clawEntry = GetPreferredWerewolfNativeAttack(false);
            ItemDrop.ItemData item = clawEntry?.Item;
            if (item?.m_shared == null || HitDamageField == null) return false;

            try
            {
                FieldInfo sharedDamagesField = item.m_shared.GetType().GetField("m_damages", InstanceFlags);
                if (sharedDamagesField == null) return false;
                object source = sharedDamagesField.GetValue(item.m_shared);
                object destination = HitDamageField.GetValue(hit);
                if (source == null || destination == null) return false;
                Type sourceType = source.GetType();
                Type destinationType = destination.GetType();
                FieldInfo[] fields = destinationType.GetFields(InstanceFlags);
                bool copied = false;
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo dst = fields[i];
                    if (dst.FieldType != typeof(float)) continue;
                    FieldInfo src = sourceType.GetField(dst.Name, InstanceFlags);
                    if (src == null || src.FieldType != typeof(float)) continue;
                    float value = (float)src.GetValue(source);
                    dst.SetValue(destination, value);
                    if (Mathf.Abs(value) > 0.0001f) copied = true;
                }
                HitDamageField.SetValue(hit, destination);
                return copied;
            }
            catch { return false; }
        }

        private static void ResetNativeDrakeAttackCatalog(Character drake = null)
        {
            for (int i = 0; i < _nativeAttackSlots.Length; ++i)
            {
                _nativeAttackSlots[i].Clear();
                _nativeAttackNextIndex[i] = 0;
            }
            _nativeAttackCatalogDrake = drake;
            _nextNativeAttackProbeAt = Time.time + 0.10f;
            _nativeAttackProbeUntil = Time.time + 3.0f;
            _nativeAttackCatalogLogged = false;
            _nextNativeAttackAt = 0f;
        }

        private static void EnsureNativeDrakeAttackCatalog(Character drake)
        {
            if (drake == null) return;
            if (_nativeAttackCatalogDrake != drake) ResetNativeDrakeAttackCatalog(drake);
            if (_nativeAttackCatalogLogged || Time.time < _nextNativeAttackProbeAt) return;

            _nextNativeAttackProbeAt = Time.time + 0.20f;
            bool foundAny = BuildNativeDrakeAttackCatalog(drake);
            if (!foundAny && Time.time < _nativeAttackProbeUntil) return;

            _nativeAttackCatalogLogged = true;
            _debugLog?.Invoke(
                ActiveFormDisplayName + " native attacks: LMB=" + DescribeNativeAttackSlot(0) +
                "; RMB=" + DescribeNativeAttackSlot(1) +
                "; MMB=" + DescribeNativeAttackSlot(2));
        }

        private static bool BuildNativeDrakeAttackCatalog(Character drake)
        {
            for (int i = 0; i < _nativeAttackSlots.Length; ++i) _nativeAttackSlots[i].Clear();

            Humanoid humanoid = drake as Humanoid;
            if (humanoid == null) return false;

            try
            {
                List<ItemDrop.ItemData> candidates = new List<ItemDrop.ItemData>();
                Inventory inventory = humanoid.GetInventory();
                if (inventory != null)
                {
                    List<ItemDrop.ItemData> items = inventory.GetAllItems();
                    if (items != null)
                    {
                        for (int i = 0; i < items.Count; ++i)
                            AddNativeAttackCandidate(candidates, items[i]);
                    }
                }

                AddNativeAttackCandidate(candidates, humanoid.GetCurrentWeapon());

                // Creature prefabs frequently keep their natural weapon outside the visible inventory.
                try
                {
                    if (humanoid.m_unarmedWeapon != null)
                        AddNativeAttackCandidate(candidates, humanoid.m_unarmedWeapon.m_itemData);
                }
                catch { }

                // v0.2.0: minor Valheim updates and creature variants move AI weapon references between
                // Humanoid and MonsterAI fields. Probe only fields whose name explicitly contains
                // "attack" or "weapon" and only accept real ItemData that report an attack. This keeps
                // the system data-driven without hard-coding private field names from one game build.
                CollectReflectedAttackCandidates(humanoid, candidates);
                CollectReflectedAttackCandidates(drake.GetComponent<MonsterAI>(), candidates);

                List<NativeDrakeAttackEntry> primary = new List<NativeDrakeAttackEntry>();
                List<NativeDrakeAttackEntry> secondary = new List<NativeDrakeAttackEntry>();
                HashSet<string> unique = new HashSet<string>(StringComparer.Ordinal);

                for (int i = 0; i < candidates.Count; ++i)
                {
                    ItemDrop.ItemData item = candidates[i];
                    if (item == null) continue;

                    bool primaryAttack = false;
                    bool secondaryAttack = false;
                    try
                    {
                        primaryAttack = item.HavePrimaryAttack();
                        secondaryAttack = item.HaveSecondaryAttack();
                    }
                    catch { }

                    if (primaryAttack)
                    {
                        string label = GetNativeAttackLabel(item, false);
                        if (unique.Add("P|" + label))
                            primary.Add(new NativeDrakeAttackEntry { Item = item, Secondary = false, Label = label, Animation = GetNativeAttackAnimation(item, false) });
                    }
                    if (secondaryAttack)
                    {
                        string label = GetNativeAttackLabel(item, true);
                        if (unique.Add("S|" + label))
                            secondary.Add(new NativeDrakeAttackEntry { Item = item, Secondary = true, Label = label, Animation = GetNativeAttackAnimation(item, true) });
                    }
                }

                // Fenring exposes its howl/taunt as a perfectly valid "weapon" attack. Generic
                // discovery therefore used to promote Fenring_taunt to LMB simply because it was
                // encountered before the real claw. Werewolf is a curated form, so map its native
                // attacks semantically: LMB = claw/swipe, MMB = jump/leap, taunt/howl = never combat.
                if (IsWerewolfForm)
                {
                    NativeDrakeAttackEntry claw = null;
                    NativeDrakeAttackEntry jump = null;
                    NativeDrakeAttackEntry fallbackCombat = null;

                    for (int listIndex = 0; listIndex < 2; ++listIndex)
                    {
                        List<NativeDrakeAttackEntry> sourceEntries = listIndex == 0 ? primary : secondary;
                        for (int i = 0; i < sourceEntries.Count; ++i)
                        {
                            NativeDrakeAttackEntry entry = sourceEntries[i];
                            if (entry == null || IsWerewolfTauntAttack(entry)) continue;
                            if (jump == null && IsWerewolfJumpAttack(entry)) jump = entry;
                            if (claw == null && IsWerewolfClawAttack(entry)) claw = entry;
                            if (fallbackCombat == null && !IsWerewolfJumpAttack(entry)) fallbackCombat = entry;
                        }
                    }

                    if (claw == null) claw = fallbackCombat;
                    if (claw != null) _nativeAttackSlots[0].Add(claw);
                    if (jump != null) _nativeAttackSlots[2].Add(jump);

                    return claw != null || jump != null;
                }

                // Stable, intuitive mapping for the other forms: LMB gets the main native attack,
                // RMB gets the first true secondary attack, and MMB cycles genuinely additional attacks.
                if (primary.Count > 0)
                    _nativeAttackSlots[0].Add(primary[0]);
                else if (secondary.Count > 0)
                    _nativeAttackSlots[0].Add(secondary[0]);

                if (secondary.Count > 0 && !ReferenceEquals(_nativeAttackSlots[0].Count > 0 ? _nativeAttackSlots[0][0] : null, secondary[0]))
                    _nativeAttackSlots[1].Add(secondary[0]);

                for (int i = 1; i < primary.Count; ++i)
                    _nativeAttackSlots[2].Add(primary[i]);
                for (int i = 1; i < secondary.Count; ++i)
                    _nativeAttackSlots[2].Add(secondary[i]);

                return _nativeAttackSlots[0].Count + _nativeAttackSlots[1].Count + _nativeAttackSlots[2].Count > 0;
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Symbiosis native attack discovery failed safely: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static void AddNativeAttackCandidate(List<ItemDrop.ItemData> candidates, ItemDrop.ItemData item)
        {
            if (candidates == null || item == null || candidates.Contains(item)) return;
            try
            {
                if (!item.IsWeapon()) return;
                if (!item.HavePrimaryAttack() && !item.HaveSecondaryAttack()) return;
                candidates.Add(item);
            }
            catch { }
        }

        private static void CollectReflectedAttackCandidates(object source, List<ItemDrop.ItemData> candidates)
        {
            if (source == null || candidates == null) return;
            try
            {
                FieldInfo[] fields = source.GetType().GetFields(InstanceFlags);
                for (int i = 0; i < fields.Length; ++i)
                {
                    FieldInfo field = fields[i];
                    if (field == null) continue;
                    string name = field.Name ?? string.Empty;
                    if (name.IndexOf("attack", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("weapon", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    object value = null;
                    try { value = field.GetValue(source); }
                    catch { continue; }
                    CollectReflectedAttackCandidateValue(value, candidates);
                }
            }
            catch { }
        }

        private static void CollectReflectedAttackCandidateValue(object value, List<ItemDrop.ItemData> candidates)
        {
            if (value == null || candidates == null) return;

            ItemDrop.ItemData itemData = value as ItemDrop.ItemData;
            if (itemData != null)
            {
                AddNativeAttackCandidate(candidates, itemData);
                return;
            }

            ItemDrop itemDrop = value as ItemDrop;
            if (itemDrop != null)
            {
                AddNativeAttackCandidate(candidates, itemDrop.m_itemData);
                return;
            }

            GameObject gameObject = value as GameObject;
            if (gameObject != null)
            {
                ItemDrop drop = gameObject.GetComponent<ItemDrop>();
                if (drop != null) AddNativeAttackCandidate(candidates, drop.m_itemData);
                return;
            }

            System.Collections.IEnumerable enumerable = value as System.Collections.IEnumerable;
            if (enumerable == null || value is string) return;

            int inspected = 0;
            foreach (object entry in enumerable)
            {
                if (++inspected > 32) break;
                if (ReferenceEquals(entry, value)) continue;

                ItemDrop.ItemData listItem = entry as ItemDrop.ItemData;
                if (listItem != null)
                {
                    AddNativeAttackCandidate(candidates, listItem);
                    continue;
                }
                ItemDrop listDrop = entry as ItemDrop;
                if (listDrop != null)
                {
                    AddNativeAttackCandidate(candidates, listDrop.m_itemData);
                    continue;
                }
                GameObject listObject = entry as GameObject;
                if (listObject != null)
                {
                    ItemDrop drop = listObject.GetComponent<ItemDrop>();
                    if (drop != null) AddNativeAttackCandidate(candidates, drop.m_itemData);
                }
            }
        }

        private static string GetNativeAttackLabel(ItemDrop.ItemData item, bool secondary)
        {
            if (item == null) return "?";
            string label = null;
            try
            {
                if (item.m_dropPrefab != null) label = item.m_dropPrefab.name;
                if (string.IsNullOrEmpty(label) && item.m_shared != null) label = item.m_shared.m_name;
            }
            catch { }
            if (string.IsNullOrEmpty(label)) label = IsSerpentForm ? "SerpentAttack" : IsTrollForm ? "TrollAttack" : IsWerewolfForm ? "WerewolfClaw" : "DrakeAttack";
            return secondary ? label + " (secondary)" : label;
        }

        private static string DescribeNativeAttackSlot(int slot)
        {
            if (slot < 0 || slot >= _nativeAttackSlots.Length || _nativeAttackSlots[slot].Count == 0) return "none";
            List<NativeDrakeAttackEntry> entries = _nativeAttackSlots[slot];
            string result = string.Empty;
            for (int i = 0; i < entries.Count; ++i)
            {
                if (i > 0) result += " / ";
                result += entries[i].Label;
                if (!string.IsNullOrEmpty(entries[i].Animation)) result += " [" + entries[i].Animation + "]";
            }
            return result;
        }

        private static string GetNativeAttackAnimation(ItemDrop.ItemData item, bool secondary)
        {
            if (item?.m_shared == null) return string.Empty;
            try
            {
                object shared = item.m_shared;
                FieldInfo attackField = shared.GetType().GetField(secondary ? "m_secondaryAttack" : "m_attack", InstanceFlags);
                object attack = attackField?.GetValue(shared);
                if (attack == null) return string.Empty;

                FieldInfo animationField = attack.GetType().GetField("m_attackAnimation", InstanceFlags);
                string animation = animationField?.GetValue(attack) as string;
                return animation ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string GetWerewolfAttackHaystack(NativeDrakeAttackEntry entry)
        {
            if (entry == null) return string.Empty;
            return ((entry.Label ?? string.Empty) + " " + (entry.Animation ?? string.Empty)).ToLowerInvariant();
        }

        private static bool IsWerewolfTauntAttack(NativeDrakeAttackEntry entry)
        {
            string text = GetWerewolfAttackHaystack(entry);
            return text.Contains("taunt") || text.Contains("howl") || text.Contains("roar") || text.Contains("scream");
        }

        private static bool IsWerewolfJumpAttack(NativeDrakeAttackEntry entry)
        {
            string text = GetWerewolfAttackHaystack(entry);
            return text.Contains("jump") || text.Contains("leap") || text.Contains("pounce");
        }

        private static bool IsWerewolfClawAttack(NativeDrakeAttackEntry entry)
        {
            if (IsWerewolfTauntAttack(entry) || IsWerewolfJumpAttack(entry)) return false;
            string text = GetWerewolfAttackHaystack(entry);
            return text.Contains("claw") || text.Contains("slash") || text.Contains("swipe");
        }

        private static NativeDrakeAttackEntry GetPreferredWerewolfNativeAttack(bool jump)
        {
            NativeDrakeAttackEntry fallback = null;
            for (int slot = 0; slot < _nativeAttackSlots.Length; slot++)
            {
                List<NativeDrakeAttackEntry> entries = _nativeAttackSlots[slot];
                for (int i = 0; i < entries.Count; i++)
                {
                    NativeDrakeAttackEntry entry = entries[i];
                    if (entry == null || entry.Item == null || IsWerewolfTauntAttack(entry)) continue;
                    if (jump && IsWerewolfJumpAttack(entry)) return entry;
                    if (!jump && IsWerewolfClawAttack(entry)) return entry;
                    if (fallback == null && (jump || !IsWerewolfJumpAttack(entry))) fallback = entry;
                }
            }
            return fallback;
        }

        private static void PlayWerewolfNativeAnimation(bool jump)
        {
            if (!Active || !IsWerewolfForm || _mountedDrake == null) return;
            EnsureNativeDrakeAttackCatalog(_mountedDrake);
            NativeDrakeAttackEntry entry = GetPreferredWerewolfNativeAttack(jump);
            if (entry == null)
            {
                PlayMountedAttackAnimation();
                return;
            }

            try
            {
                if (!string.IsNullOrEmpty(entry.Animation) && TrySetCreatureZSyncTrigger(_mountedDrake, entry.Animation))
                {
                    _debugLog?.Invoke($"Werewolf ZSyncAnimation trigger: {(jump ? "jump" : "claw")} -> {entry.Animation} ({entry.Label}).");
                    return;
                }

                if (_drakeAnimator == null) _drakeAnimator = ResolveDrakeAnimator(_mountedDrake);
                if (_drakeAnimator != null && !string.IsNullOrEmpty(entry.Animation))
                {
                    _drakeAnimator.SetTrigger(entry.Animation);
                    _debugLog?.Invoke($"Werewolf raw Animator fallback trigger: {(jump ? "jump" : "claw")} -> {entry.Animation} ({entry.Label}).");
                    return;
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke("Werewolf animation trigger failed safely: " + ex.GetType().Name + ": " + ex.Message);
            }

            PlayMountedAttackAnimation();
        }

        private static bool TrySetCreatureZSyncTrigger(Character creature, string trigger)
        {
            if (creature == null || string.IsNullOrEmpty(trigger)) return false;
            try
            {
                Component[] components = creature.GetComponentsInChildren<Component>(true);
                for (int i = 0; i < components.Length; i++)
                {
                    Component component = components[i];
                    if (component == null) continue;
                    Type type = component.GetType();
                    if (!string.Equals(type.Name, "ZSyncAnimation", StringComparison.Ordinal)) continue;

                    MethodInfo setTrigger = type.GetMethod("SetTrigger", InstanceFlags, null, new[] { typeof(string) }, null);
                    if (setTrigger == null) continue;
                    setTrigger.Invoke(component, new object[] { trigger });
                    return true;
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke("Werewolf ZSyncAnimation trigger failed safely: " + ex.GetType().Name + ": " + ex.Message);
            }
            return false;
        }

        private static bool TryUseWerewolfJumpAttack()
        {
            if (!Active || !IsWerewolfForm || _mountedDrake == null || _werewolfComboActive) return false;
            EnsureNativeDrakeAttackCatalog(_mountedDrake);
            NativeDrakeAttackEntry preferred = GetPreferredWerewolfNativeAttack(true);
            if (preferred == null)
            {
                _status?.Invoke("Kein nativer Werwolf-Sprungangriff gefunden.");
                return true;
            }

            Humanoid humanoid = _mountedDrake as Humanoid;
            if (humanoid == null || _mountedDrake.IsDead() || _mountedDrake.InAttack()) return true;
            if (Time.time < _nextNativeAttackAt) return true;

            try
            {
                float interval = preferred.Item.m_shared != null ? Mathf.Max(0f, preferred.Item.m_shared.m_aiAttackInterval) : 0f;
                if (Time.time - preferred.Item.m_lastAttackTime < interval)
                {
                    float remaining = interval - (Time.time - preferred.Item.m_lastAttackTime);
                    _status?.Invoke($"Werwolf-Sprung bereit in {Mathf.Max(0f, remaining):0.0}s");
                    return true;
                }

                // The pounce is the Werewolf's committed mobility attack. Check stamina before
                // starting the native attack so a failed resource check never consumes the
                // creature's native interval/cooldown. LMB remains the free basic claw.
                if (_rider != null)
                {
                    try
                    {
                        if (!_rider.HaveStamina(WerewolfPounceStaminaCost))
                        {
                            _status?.Invoke($"Werwolf-Sprung braucht {WerewolfPounceStaminaCost:0} Ausdauer.");
                            return true;
                        }
                    }
                    catch { }
                }

                if (humanoid.GetCurrentWeapon() != preferred.Item && !humanoid.EquipItem(preferred.Item, false)) return true;
                try { _mountedDrake.SetLookDir(GetMountedAttackDirection(_mountedDrake), 0f); } catch { }
                bool nativeStarted = _mountedDrake.StartAttack(null, preferred.Secondary);
                if (!nativeStarted) PlayWerewolfNativeAnimation(true);

                if (_rider != null)
                {
                    try { _rider.UseStamina(WerewolfPounceStaminaCost); } catch { }
                }
                StartWerewolfPounceMotion();
                _nextNativeAttackAt = Time.time + 0.15f;
                _status?.Invoke("Werwolf-Sprungangriff!");
                _debugLog?.Invoke($"Werewolf MMB aimed pounce -> {preferred.Label} [{preferred.Animation}], speed={WerewolfPounceSpeed:0.#}m/s, stamina={WerewolfPounceStaminaCost:0.#}, nativeStarted={nativeStarted}.");
                return true;
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke("Werewolf jump attack failed safely: " + ex.GetType().Name + ": " + ex.Message);
                PlayWerewolfNativeAnimation(true);
                return true;
            }
        }


        private static void StartWerewolfPounceMotion()
        {
            if (_mountedDrake == null || !IsWerewolfForm) return;
            try
            {
                Vector3 aim = GetMountedAttackDirection(_mountedDrake);
                if (aim.sqrMagnitude < 0.001f) aim = _mountedDrake.transform.forward;
                aim.Normalize();

                // Keep the pounce readable and controllable: mouse pitch contributes to the leap,
                // but extreme up/down views cannot turn it into a vertical launch or ground dive.
                Vector3 flat = new Vector3(aim.x, 0f, aim.z);
                if (flat.sqrMagnitude < 0.001f) flat = _mountedDrake.transform.forward;
                flat.Normalize();
                float pitchY = Mathf.Clamp(aim.y, -0.25f, 0.35f);
                _werewolfPounceVelocity = flat * WerewolfPounceSpeed;
                _werewolfPounceVelocity.y = WerewolfPounceUpVelocity + pitchY * 5f;
                _werewolfPounceUntil = Time.time + WerewolfPounceDuration;

                Rigidbody body = _mountedDrake.GetComponent<Rigidbody>();
                if (body != null)
                {
                    Vector3 v = body.velocity;
                    v.x = _werewolfPounceVelocity.x;
                    v.z = _werewolfPounceVelocity.z;
                    v.y = Mathf.Max(v.y, _werewolfPounceVelocity.y);
                    body.velocity = v;
                }
            }
            catch (Exception ex)
            {
                _werewolfPounceUntil = 0f;
                _debugLog?.Invoke("Werewolf aimed pounce motion failed safely: " + ex.GetType().Name + ": " + ex.Message);
            }
        }


        private static void ScheduleTrollUnarmedWorldPunch()
        {
            if (!Active || !IsTrollForm || _mountedDrake == null || _trollHasLog) return;
            _pendingTrollUnarmedPunchAt = _trollNativeAttackImpactAt > Time.time
                ? _trollNativeAttackImpactAt
                : Time.time + TrollUnarmedPunchImpactDelay;
        }

        private static void ResetPendingTrollImpacts()
        {
            _pendingTrollUnarmedPunchAt = 0f;
            _pendingTrollGroundSlamAt = 0f;
            _pendingTrollRoarAt = 0f;
            _trollNativeAttackImpactAt = 0f;
            _pendingTrollNativeHits.Clear();
            _pendingTrollGroundSlamHasLog = false;
            _pendingTrollGroundSlamBoundLevel = 0;
            _pendingTrollRoarBoundLevel = 0;
        }

        private static void ProcessPendingTrollImpacts()
        {
            if (!Active || !IsTrollForm || _mountedDrake == null)
            {
                ResetPendingTrollImpacts();
                return;
            }

            float now = Time.time;

            for (int i = _pendingTrollNativeHits.Count - 1; i >= 0; --i)
            {
                PendingTrollNativeHit pending = _pendingTrollNativeHits[i];
                if (pending == null || now < pending.ApplyAt) continue;
                _pendingTrollNativeHits.RemoveAt(i);
                if (pending.Target == null || pending.Target.IsDead() || pending.Hit == null) continue;
                try
                {
                    _replayingTrollNativeHit = true;
                    pending.Target.Damage(pending.Hit);
                }
                catch (Exception ex)
                {
                    _debugLog?.Invoke("Deferred Troll native hit failed safely: " + ex.GetType().Name + ": " + ex.Message);
                }
                finally { _replayingTrollNativeHit = false; }
            }

            if (_pendingTrollUnarmedPunchAt > 0f && now >= _pendingTrollUnarmedPunchAt)
            {
                _pendingTrollUnarmedPunchAt = 0f;
                if (!_trollHasLog) ApplyTrollUnarmedWorldPunch();
            }

            if (_pendingTrollGroundSlamAt > 0f && now >= _pendingTrollGroundSlamAt)
            {
                bool logSlam = _pendingTrollGroundSlamHasLog;
                int boundLevel = _pendingTrollGroundSlamBoundLevel;
                _pendingTrollGroundSlamAt = 0f;
                _pendingTrollGroundSlamHasLog = false;

                float abilityScale = GetBoundAbilityMultiplier(boundLevel);
                float slamDamageScale = logSlam ? TrollLogGroundSlamDamageMultiplier : 1f;
                float slamRadiusScale = logSlam ? TrollLogGroundSlamRadiusMultiplier : 1f;
                float slamPushScale = logSlam ? TrollLogGroundSlamPushMultiplier : 1f;
                float slamWorldScale = logSlam ? TrollLogGroundSlamWorldDamageMultiplier : 1f;
                string slamLabel = logSlam ? "Troll-Stamm-Bodenschlag" : "Troll-Bodenschlag";

                int hits;
                try
                {
                    _bypassTrollOutgoingDelay = true;
                    hits = DamageHostilesInRadius(_mountedDrake.transform.position, TrollGroundSlamRadius * slamRadiusScale,
                        TrollGroundSlamDamage * abilityScale * slamDamageScale, 0f, TrollGroundSlamPush * slamPushScale, 2.0f, slamLabel);
                }
                finally { _bypassTrollOutgoingDelay = false; }
                int worldHits = DamageNaturalDestructiblesInRadius(_mountedDrake.transform.position, TrollGroundSlamWorldRadius * slamRadiusScale,
                    TrollGroundSlamWorldBlunt * abilityScale * slamWorldScale, TrollGroundSlamWorldChop * abilityScale * slamWorldScale,
                    TrollGroundSlamWorldPickaxe * abilityScale * slamWorldScale, slamLabel);

                if (logSlam && _trollHasLog)
                    ConsumeTrollLogDurability(TrollLogGroundSlamDurabilityCost, "Stamm-Bodenschlag");

                _status?.Invoke((hits > 0 || worldHits > 0)
                    ? $"{slamLabel}! {hits} Gegner / {worldHits} Naturziel(e)."
                    : slamLabel + "!");
            }

            if (_pendingTrollRoarAt > 0f && now >= _pendingTrollRoarAt)
            {
                int boundLevel = _pendingTrollRoarBoundLevel;
                _pendingTrollRoarAt = 0f;
                float abilityScale = GetBoundAbilityMultiplier(boundLevel);
                int hits;
                try
                {
                    _bypassTrollOutgoingDelay = true;
                    hits = DamageHostilesInRadius(_mountedDrake.transform.position, TrollRoarRadius,
                        TrollRoarDamage * abilityScale, 0f, TrollRoarPush, TrollRoarStagger, "Troll-Schockgebruell");
                }
                finally { _bypassTrollOutgoingDelay = false; }
                _status?.Invoke(hits > 0
                    ? $"Troll-Schockgebruell! {hits} Ziel(e) weggestossen."
                    : "Troll-Schockgebruell!");
            }
        }

        private static void ApplyTrollUnarmedWorldPunch()
        {
            if (!Active || !IsTrollForm || _mountedDrake == null || _trollHasLog) return;
            try
            {
                Vector3 forward = _mountedDrake.transform.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
                forward.Normalize();
                Vector3 center = _mountedDrake.transform.position + forward * TrollUnarmedTreeHitForwardOffset;
                int worldHits = DamageNaturalDestructiblesInRadius(center, TrollUnarmedTreeHitRadius,
                    TrollUnarmedTreeBlunt, TrollUnarmedTreeChop, 0f, "Troll-Faustschlag");
                if (worldHits > 0)
                    _debugLog?.Invoke($"Troll unarmed LMB restored world damage: {worldHits} natural target(s).");
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke("Troll unarmed world punch failed safely: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool TryUseNativeDrakeAttack(int slot)
        {
            if (slot < 0 || slot >= _nativeAttackSlots.Length) return false;
            List<NativeDrakeAttackEntry> entries = _nativeAttackSlots[slot];
            if (entries.Count == 0 || _mountedDrake == null) return false;

            Humanoid humanoid = _mountedDrake as Humanoid;
            if (humanoid == null || _mountedDrake.IsDead() || _mountedDrake.InAttack()) return false;
            if (Time.time < _nextNativeAttackAt) return false;

            int index = _nativeAttackNextIndex[slot] % entries.Count;
            NativeDrakeAttackEntry entry = entries[index];
            if (entry == null || entry.Item == null) return false;

            try
            {
                float interval = entry.Item.m_shared != null ? Mathf.Max(0f, entry.Item.m_shared.m_aiAttackInterval) : 0f;
                if (Time.time - entry.Item.m_lastAttackTime < interval)
                {
                    float remaining = interval - (Time.time - entry.Item.m_lastAttackTime);
                    _status?.Invoke($"{ActiveFormDisplayName}-Angriff bereit in {Mathf.Max(0f, remaining):0.0}s");
                    return true;
                }

                if (humanoid.GetCurrentWeapon() != entry.Item && !humanoid.EquipItem(entry.Item, false))
                    return false;

                try { _mountedDrake.SetLookDir(GetMountedAttackDirection(_mountedDrake), 0f); } catch { }

                bool trollPrimary = IsTrollForm && slot == 0;
                if (trollPrimary)
                    _trollNativeAttackImpactAt = Time.time + (_trollHasLog ? TrollLogSwingImpactDelay : TrollUnarmedPunchImpactDelay);

                if (!_mountedDrake.StartAttack(null, entry.Secondary))
                {
                    if (trollPrimary) _trollNativeAttackImpactAt = 0f;
                    return false;
                }

                _nativeAttackNextIndex[slot] = (index + 1) % entries.Count;
                _nextNativeAttackAt = Time.time + 0.15f;
                _status?.Invoke(ActiveFormDisplayName + "-Angriff: " + entry.Label);
                return true;
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Symbiosis native attack failed safely ({entry.Label}): {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static bool TryFireFrostPattern(Character drake, int count, float totalYawSpread, float frostDamage, float projectileSpeed, string label)
        {
            if (drake == null || count <= 0) return false;
            try
            {
                ZNetScene scene = ZNetScene.instance;
                GameObject projectilePrefab = scene != null ? scene.GetPrefab("hatchling_cold_projectile") : null;
                if (projectilePrefab == null) return false;

                Vector3 aim = GetMountedAttackDirection(drake);
                Vector3 origin = GetDrakeEyePosition() + aim * Mathf.Max(0.65f, drake.GetRadius() * 0.7f);
                for (int i = 0; i < count; ++i)
                {
                    float t = count <= 1 ? 0.5f : (float)i / (count - 1);
                    float yaw = Mathf.Lerp(-totalYawSpread * 0.5f, totalYawSpread * 0.5f, t);
                    float pitch = count <= 1 ? 0f : Mathf.Sin((t - 0.5f) * Mathf.PI) * -3f;
                    Vector3 velocity = Quaternion.Euler(pitch, yaw, 0f) * aim * projectileSpeed;
                    Quaternion rotation = velocity.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(velocity.normalized, Vector3.up) : drake.transform.rotation;
                    GameObject projectileObject = UnityEngine.Object.Instantiate(projectilePrefab, origin, rotation);
                    if (projectileObject == null) continue;
                    IProjectile projectile = projectileObject.GetComponent<IProjectile>() ?? projectileObject.GetComponentInChildren<IProjectile>();
                    if (projectile == null) { UnityEngine.Object.Destroy(projectileObject); continue; }
                    HitData hit = new HitData();
                    hit.m_damage.m_frost = frostDamage * GetBoundAbilityMultiplier(_activeBoundLevel);
                    hit.m_ranged = true;
                    hit.SetAttacker(drake);
                    projectile.Setup(drake, velocity, 10f, hit, null, null);
                }
                PlayMountedAttackAnimation();
                _status?.Invoke("Drake-" + label + "!");
                _debugLog?.Invoke($"Drake {label} fallback fired: count={count}, spread={totalYawSpread:0.#}, speed={projectileSpeed:0.#}, frost={frostDamage:0.#}.");
                return true;
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Drake {label} fallback failed safely: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static bool TryFireVanillaFrostSalvo(Character drake)
        {
            if (drake == null) return false;
            try
            {
                ZNetScene scene = ZNetScene.instance;
                GameObject projectilePrefab = scene != null ? scene.GetPrefab("hatchling_cold_projectile") : null;
                if (projectilePrefab == null)
                {
                    if (!_reportedFrostProjectileMissing)
                    {
                        _reportedFrostProjectileMissing = true;
                        _debugLog?.Invoke("Drake attack unavailable: prefab 'hatchling_cold_projectile' was not found in ZNetScene.");
                    }
                    return false;
                }

                Vector3 aim = GetMountedAttackDirection(drake);
                Vector3 origin = GetDrakeEyePosition() + aim * Mathf.Max(0.65f, drake.GetRadius() * 0.7f);

                // Vanilla Drake behaviour: a three-projectile cold salvo with visible spread.
                // Keep the centre shot exact and mirror two side shots so player aim remains predictable.
                float[] yawSpread = { -12f, 0f, 12f };
                float[] pitchSpread = { 4f, 0f, -4f };
                for (int i = 0; i < 3; ++i)
                {
                    Quaternion spread = Quaternion.Euler(pitchSpread[i], yawSpread[i], 0f);
                    Vector3 velocity = spread * aim * FrostProjectileSpeed;
                    Quaternion rotation = velocity.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(velocity.normalized, Vector3.up) : drake.transform.rotation;
                    GameObject projectileObject = UnityEngine.Object.Instantiate(projectilePrefab, origin, rotation);
                    if (projectileObject == null) continue;

                    IProjectile projectile = projectileObject.GetComponent<IProjectile>();
                    if (projectile == null) projectile = projectileObject.GetComponentInChildren<IProjectile>();
                    if (projectile == null)
                    {
                        UnityEngine.Object.Destroy(projectileObject);
                        continue;
                    }

                    HitData hit = new HitData();
                    hit.m_damage.m_frost = FrostProjectileDamage * GetBoundAbilityMultiplier(_activeBoundLevel);
                    hit.m_ranged = true;
                    hit.SetAttacker(drake);
                    projectile.Setup(drake, velocity, 10f, hit, null, null);
                }

                PlayMountedAttackAnimation();
                _debugLog?.Invoke($"Drake frost salvo fired: speed={FrostProjectileSpeed:0.#}, frost={FrostProjectileDamage:0.#}, cooldown={FrostSalvoCooldown:0.#}s.");
                return true;
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Drake frost salvo failed safely: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static Vector3 GetMountedAttackDirection(Character drake)
        {
            // In Third Person, Werewolf and Serpent use movement-facing just like the normal
            // Valheim character. Do not let the free camera continuously twist their look vector.
            if (!_drakeEgoActive && (IsWerewolfForm || IsSerpentForm) && drake != null)
            {
                Vector3 bodyForward = drake.transform.forward;
                bodyForward.y = 0f;
                if (bodyForward.sqrMagnitude > 0.0001f) return bodyForward.normalized;
            }

            Vector3 direction;
            if (_cameraPoseInitialized)
                direction = Quaternion.Euler(_cameraPitch, _cameraYaw, 0f) * Vector3.forward;
            else
            {
                Camera camera = Camera.main;
                direction = camera != null ? camera.transform.forward : drake.transform.forward;
            }

            if (direction.sqrMagnitude < 0.0001f) direction = drake.transform.forward;
            return direction.normalized;
        }

        private static void PlayMountedAttackAnimation()
        {
            try
            {
                if (_drakeAnimator == null && _mountedDrake != null) _drakeAnimator = ResolveDrakeAnimator(_mountedDrake);
                if (_drakeAnimator == null) return;

                // Valheim's Drake attack is authored inside its animator controller. Triggering the
                // common attack parameter gives us the native mouth/neck motion when that controller
                // exposes it; failure is harmless because projectile gameplay is independent.
                _drakeAnimator.SetTrigger("attack");
            }
            catch { }
        }

        private static ZNetView GetCachedMountedView()
        {
            if (_mountedDrake == null) return null;
            if (_cachedMountedViewOwner != _mountedDrake || _cachedMountedView == null)
            {
                _cachedMountedViewOwner = _mountedDrake;
                _cachedMountedView = _mountedDrake.GetComponent<ZNetView>();
            }
            return _cachedMountedView;
        }

        private static Rigidbody GetCachedMountedBody()
        {
            if (_mountedDrake == null) return null;
            if (_cachedMountedBodyOwner != _mountedDrake || _cachedMountedBody == null)
            {
                _cachedMountedBodyOwner = _mountedDrake;
                _cachedMountedBody = _mountedDrake.GetComponent<Rigidbody>();
            }
            return _cachedMountedBody;
        }

        private static Rigidbody GetCachedRiderBody()
        {
            if (_rider == null) return null;
            if (_cachedRiderBodyOwner != _rider || _cachedRiderBody == null)
            {
                _cachedRiderBodyOwner = _rider;
                _cachedRiderBody = _rider.GetComponent<Rigidbody>();
            }
            return _cachedRiderBody;
        }

        internal static void LateTick()
        {
            if (!Active || _rider == null || _mountedDrake == null) return;
            if (_rider.IsDead() || _mountedDrake.IsDead()) return;

            // Run after Player/GameCamera Update. Symbiosis does not use Character.Attach; the hidden
            // local Player is simply kept at the Drake for world streaming/network relevance.
            // During symbiosis the player body is hidden and kept inside the Drake. The local
            // Player object still exists for camera/input/network ownership, while the Drake is
            // the only visible body and receives the movement controls.
            SyncRiderForCurrentState();

            _rider.SetMoveDir(Vector3.zero);
            try
            {
                Rigidbody riderBody = GetCachedRiderBody();
                Rigidbody drakeBody = GetCachedMountedBody();
                if (riderBody != null)
                {
                    bool riderFollowsDrake = _transitionState == SymbiosisTransitionState.None ||
                        (_transitionState == SymbiosisTransitionState.Entering && _playerVisualsHidden);
                    riderBody.velocity = riderFollowsDrake && drakeBody != null ? drakeBody.velocity : Vector3.zero;
                    if (!riderFollowsDrake) riderBody.angularVelocity = Vector3.zero;
                }
            }
            catch { }

            // Re-assert possession at the end of the frame so external physics or another component
            // cannot leave a residual movement impulse. Native MonsterAI is already skipped entirely.
            UpdateManualPossessionHold(_mountedDrake);

            // v3.0.8: let Valheim finish the native Hatchling movement/animation first, then
            // scale only the real displacement it produced. That makes travel faster without
            // changing the fly-speed fields that choose the wing-flap locomotion.
            ApplyDrakeNativeTravelAssist(_mountedDrake);

            if (IsWerewolfForm)
            {
                if (Time.time < _werewolfJumpBoostUntil)
                {
                    try
                    {
                        Rigidbody werewolfBody = GetCachedMountedBody();
                        if (werewolfBody != null && werewolfBody.velocity.y < WerewolfMovementJumpVelocity * 0.82f)
                        {
                            Vector3 v = werewolfBody.velocity;
                            v.y = WerewolfMovementJumpVelocity * 0.82f;
                            werewolfBody.velocity = v;
                        }
                    }
                    catch { }
                }

                // v2.4.0: MMB pounce follows the mouse-owned attack direction for a very short
                // burst. Reasserting only during the authored leap window lets Valheim keep
                // normal gravity/collision/landing while preventing the manual possession motor
                // from flattening the Fenring's actual forward lunge on the next frame.
                if (Time.time < _werewolfPounceUntil)
                {
                    try
                    {
                        Rigidbody werewolfBody = GetCachedMountedBody();
                        if (werewolfBody != null)
                        {
                            Vector3 v = werewolfBody.velocity;
                            v.x = _werewolfPounceVelocity.x;
                            v.z = _werewolfPounceVelocity.z;
                            v.y = Mathf.Max(v.y, _werewolfPounceVelocity.y);
                            werewolfBody.velocity = v;
                        }
                    }
                    catch { }
                }
            }

            if (IsSerpentForm && Time.time < _serpentBiteLungeUntil)
            {
                try
                {
                    Rigidbody serpentBody = GetCachedMountedBody();
                    if (serpentBody != null)
                    {
                        Vector3 v = serpentBody.velocity;
                        v.x = _serpentBiteLungeVelocity.x;
                        v.z = _serpentBiteLungeVelocity.z;
                        serpentBody.velocity = v;
                    }
                }
                catch { }
            }

            // Keep the player body hidden even if another camera mod has just restored local visibility.
            EnforcePlayerVisualsHidden();

            if (IsSerpentForm)
            {
                ApplySerpentVisualSwimWave(_mountedDrake);
            }
            else if (IsDrakeForm)
            {
                ApplyDrakeVisualBank(_mountedDrake);
            }

            // Keep the actual render camera on the Drake for the whole frame. Ego-style final-pose
            // locking in onPreCull/onPreRender below then re-applies the animated head point immediately before render.
            try
            {
                Camera camera = Camera.main;
                if (camera != null) ApplyOwnedSymbiosisCamera(camera);
            }
            catch { }
        }

        private static void SyncRiderToSeat()
        {
            if (_rider == null || _mountedDrake == null) return;
            Transform seat = _seat != null ? _seat : ResolveOrCreateSeat(_mountedDrake);
            if (seat == null) return;
            _seat = seat;

            try
            {
                _rider.transform.position = seat.position;
                _rider.transform.rotation = seat.rotation;
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Drake carry seat sync failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void SyncRiderForCurrentState()
        {
            if (_rider == null) return;

            if (_transitionState == SymbiosisTransitionState.Entering && !_playerVisualsHidden)
            {
                HoldRiderAt(_entryPlayerAnchorPosition, _entryPlayerAnchorRotation);
                return;
            }

            if (_transitionState == SymbiosisTransitionState.Exiting)
            {
                HoldRiderAt(_exitPlayerAnchorPosition, _exitPlayerAnchorRotation);
                return;
            }

            SyncRiderToSeat();
        }

        private static void HoldRiderAt(Vector3 position, Quaternion rotation)
        {
            if (_rider == null) return;
            try
            {
                _rider.transform.position = position;
                _rider.transform.rotation = rotation;
                Rigidbody body = _rider.GetComponent<Rigidbody>();
                if (body != null)
                {
                    body.position = position;
                    body.rotation = rotation;
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
            }
            catch { }
        }

        private static bool EnsureRiderAttached()
        {
            if (_rider == null || _mountedDrake == null || AttachStartMethod == null) return false;
            try
            {
                Transform seat = _seat != null ? _seat : ResolveOrCreateSeat(_mountedDrake);
                if (seat == null) return false;
                _seat = seat;
                GameObject visual = ResolveVisual(_mountedDrake) ?? _mountedDrake.gameObject;
                AttachStartMethod.Invoke(_rider, new object[]
                {
                    seat, visual, false, false, false, "attach_chair", Vector3.zero, null
                });
                return IsAttached(_rider);
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Drake carry reattach failed safely: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Physics-rate ownership for the possessed Drake. v0.1.50 deliberately drives manual
        /// flight here instead of from MonsterAI.UpdateAI, whose cadence can be throttled and
        /// produced visibly stepped steering/rotation.
        /// </summary>
        internal static void FixedTick()
        {
            if (!Active || _mountedDrake == null || _rider == null) return;
            if (_rider != Player.m_localPlayer || _rider.IsDead() || _mountedDrake.IsDead()) return;
            ApplyControls(_mountedDrake, Time.fixedDeltaTime);
        }

        internal static bool ApplyControls(Character worker, float dt)
        {
            if (!IsMounted(worker) || _rider == null) return false;
            if (IsSerpentForm) return ApplySerpentControls(worker, dt);
            if (IsTrollForm) return ApplyTrollControls(worker, dt);
            if (IsWerewolfForm) return ApplyWerewolfControls(worker, dt);

            ClearMountedDrakeNativeIntent(worker);

            if (_transitionState != SymbiosisTransitionState.None)
            {
                ResetFlightMoveSmoothing();
                if (!_idleHoldActive) CaptureIdleHold(worker);
                EnforceIdleHold(worker);
                return true;
            }

            if (IsMountUiBlocking())
            {
                ResetFlightMoveSmoothing();
                if (!_idleHoldActive) CaptureIdleHold(worker);
                EnforceIdleHold(worker);
                return true;
            }

            Vector3 aimForward;
            if (_cameraPoseInitialized)
                aimForward = Quaternion.Euler(_cameraPitch, _cameraYaw, 0f) * Vector3.forward;
            else
            {
                Camera camera = Camera.main;
                aimForward = camera != null ? camera.transform.forward : _rider.transform.forward;
            }
            if (aimForward.sqrMagnitude < 0.0001f) aimForward = worker.transform.forward;
            aimForward.Normalize();

            float forwardInput = 0f;
            float strafeInput = 0f;
            float verticalInput = 0f;

            if (Input.GetKey(KeyCode.W)) forwardInput += 1f;
            if (Input.GetKey(KeyCode.S)) forwardInput -= 1f;
            if (Input.GetKey(KeyCode.A)) strafeInput -= 1f;
            if (Input.GetKey(KeyCode.D)) strafeInput += 1f;
            if (Input.GetKey(KeyCode.Space)) verticalInput += 1f;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.C)) verticalInput -= 1f;

            bool hasInput = Mathf.Abs(forwardInput) > 0.001f || Mathf.Abs(strafeInput) > 0.001f || Mathf.Abs(verticalInput) > 0.001f;
            bool wantsFast = forwardInput > 0.10f && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));

            // v3.0.9: Shift is a gravity-assisted dive, not a stamina sprint. A committed dive
            // stores enough momentum for two seconds of free climbing after Shift is released.
            // Once that momentum window expires, only upward flight consumes player stamina.
            bool wasDive = _drakeFastFlightActive;
            bool fast = wantsFast;
            if (fast && !wasDive) _drakeDiveStartedAt = Time.time;
            if (!fast && wasDive)
            {
                if (_drakeDiveStartedAt >= 0f && Time.time - _drakeDiveStartedAt >= DrakeDiveMomentumMinDuration)
                    _drakeFreeClimbUntil = Time.time + DrakeDiveMomentumFreeClimbSeconds;
                _drakeDiveStartedAt = -1f;
            }

            _drakeFastFlightActive = fast;
            if (fast)
            {
                // Sturzflug: no stamina cost, committed downward vector, no lift-key override.
                aimForward.y = Mathf.Min(aimForward.y, DrakeDiveMinDownwardY);
                aimForward.Normalize();
                verticalInput = 0f;
            }
            else if (forwardInput > 0.10f)
            {
                // Normal forward flight follows camera pitch inside the natural flight cone.
                aimForward.y = Mathf.Clamp(aimForward.y, -DrakeNormalMaxPitchY, DrakeNormalMaxPitchY);
                aimForward.Normalize();
            }

            bool climbingIntent = !fast &&
                ((forwardInput > 0.10f && aimForward.y > DrakeClimbIntentThreshold) ||
                 verticalInput > DrakeClimbIntentThreshold);
            bool freeMomentumClimb = climbingIntent && Time.time < _drakeFreeClimbUntil;
            if (climbingIntent && !freeMomentumClimb)
            {
                float staminaDt = dt > 0f ? dt : Time.fixedDeltaTime;
                if (!ConsumeMovementBoostStamina(_rider, DrakeClimbStaminaPerSecond, staminaDt))
                {
                    // Out of stamina: preserve forward/sideways flight but remove positive lift.
                    if (aimForward.y > 0f)
                    {
                        aimForward.y = 0f;
                        if (aimForward.sqrMagnitude > 0.0001f) aimForward.Normalize();
                    }
                    if (verticalInput > 0f) verticalInput = 0f;
                }
            }
            _manualFlightInputActive = hasInput;
            if (!hasInput)
            {
                _drakeFastFlightActive = false;
                _drakeNativeTravelAssistInitialized = false;
            }

            MonsterAI monster = worker.GetComponent<MonsterAI>();
            if (monster != null && monster.GetFollowTarget() != null)
                WorkerNavigationRuntime.SetNativeFollowTarget(monster, null);

            float controlDt = Mathf.Clamp(dt > 0f ? dt : Time.fixedDeltaTime, 0.001f, 0.05f);

            if (!hasInput)
            {
                // v2.7.0: even while hovering, the Drake body follows the camera yaw.
                // Previously the camera could orbit while the creature kept its old heading, which
                // made Ego feel detached from the body. Only yaw is owned here so looking up/down
                // does not tip the whole Drake while stationary.
                Vector3 cameraYawForward = Quaternion.Euler(0f, _cameraYaw, 0f) * Vector3.forward;
                if (cameraYawForward.sqrMagnitude > 0.0001f)
                {
                    Quaternion yawRotation = Quaternion.LookRotation(cameraYawForward.normalized, Vector3.up);
                    worker.transform.rotation = Quaternion.RotateTowards(
                        worker.transform.rotation,
                        yawRotation,
                        220f * controlDt);
                }

                // Keep v0.1.48's short release smoothing, then settle into a deterministic hover.
                if (_flightMoveSmoothInitialized && _smoothedFlightMove.sqrMagnitude > 0.0016f)
                {
                    float releaseT = 1f - Mathf.Exp(-12.0f * controlDt);
                    _smoothedFlightMove = Vector3.Lerp(_smoothedFlightMove, Vector3.zero, releaseT);
                    if (_smoothedFlightMove.sqrMagnitude > 0.0016f)
                    {
                        _idleHoldActive = false;
                        worker.SetMoveDir(_smoothedFlightMove);
                        worker.SetRun(false);
                        worker.SetWalk(true);
                        return true;
                    }
                }

                _smoothedFlightMove = Vector3.zero;
                _flightMoveSmoothInitialized = true;
                if (!_idleHoldActive) CaptureIdleHold(worker);
                EnforceIdleHold(worker);
                return true;
            }

            _idleHoldActive = false;

            // v0.1.50: the body always LOOKS toward the aim direction, even while S is held.
            // Backward input therefore becomes genuine reverse flight instead of rotating the Drake
            // 180 degrees and pretending the reverse command was forward flight. If Valheim's
            // Hatchling animator contains a reverse blend/clip, the negative local movement can now
            // select it naturally. Pure strafe/vertical correction keeps a stable horizontal facing.
            // v4.2.1: in Drake Ego the whole body owns the camera yaw. Keep the torso level while
            // aim pitch still controls climb/dive, so turning the mouse feels like turning the Drake
            // instead of free-looking inside a body that keeps its old heading.
            Vector3 desiredFacing;
            if (IsDrakeForm && _drakeEgoActive)
                desiredFacing = Quaternion.Euler(0f, _cameraYaw, 0f) * Vector3.forward;
            else
                desiredFacing = Mathf.Abs(forwardInput) > 0.1f
                    ? aimForward
                    : Quaternion.Euler(0f, _cameraYaw, 0f) * Vector3.forward;

            if (desiredFacing.sqrMagnitude > 0.0001f)
            {
                Quaternion desiredRotation = Quaternion.LookRotation(desiredFacing.normalized, Vector3.up);
                // Bounded angular velocity makes mouse turns read as a flight arc instead of a snap.
                float turnSpeedDeg = fast ? 185f : 150f;
                worker.transform.rotation = Quaternion.RotateTowards(
                    worker.transform.rotation,
                    desiredRotation,
                    turnSpeedDeg * controlDt);
            }

            // Translation follows the SAME smoothed body axis used by the visible Drake. This removes
            // the old visual slide where the velocity changed toward the camera before the body had
            // finished turning. S simply negates this axis, so the Drake backs up without turning around.
            Vector3 flightForward = worker.transform.forward;
            if (flightForward.sqrMagnitude < 0.0001f) flightForward = desiredFacing;
            flightForward.Normalize();

            Vector3 flightRight = worker.transform.right;
            if (flightRight.sqrMagnitude < 0.0001f)
                flightRight = Quaternion.Euler(0f, _cameraYaw, 0f) * Vector3.right;
            flightRight.Normalize();

            Vector3 rawMove = flightForward * forwardInput + flightRight * (strafeInput * 0.82f) + Vector3.up * (verticalInput * 0.72f);
            Vector3 targetMove = rawMove.sqrMagnitude > 0.0001f ? rawMove.normalized : Vector3.zero;

            if (!_flightMoveSmoothInitialized)
            {
                _smoothedFlightMove = Vector3.zero;
                _flightMoveSmoothInitialized = true;
            }

            // Slightly calmer acceleration than v0.1.48. Direction and body now converge together,
            // so this filtering no longer causes a sideways-looking slide during turns.
            float moveT = 1f - Mathf.Exp(-7.0f * controlDt);
            _smoothedFlightMove = Vector3.Lerp(_smoothedFlightMove, targetMove, moveT);

            // v2.2.0: make real Drake translation breathe with the actual wingbeat animation.
            // The minimum stays high enough that the creature never stalls between beats; the
            // downstroke only adds a modest propulsion pulse. Because this adjusts the temporary
            // avatar's native walk/run speeds, Valheim still owns collision/animation/physics.
            float propulsion = EvaluateDrakeWingbeatPropulsion(worker, fast);
            ApplyDrakeWingbeatPropulsion(worker, propulsion);

            worker.SetMoveDir(_smoothedFlightMove);
            worker.SetRun(fast);
            worker.SetWalk(!fast);
            return true;
        }

        private static bool ApplyTrollControls(Character worker, float dt)
        {
            if (!IsMounted(worker) || _rider == null) return false;
            ClearMountedDrakeNativeIntent(worker);

            if (_transitionState != SymbiosisTransitionState.None || IsMountUiBlocking())
            {
                _idleHoldActive = false;
                ResetFlightMoveSmoothing();
                try
                {
                    worker.SetMoveDir(Vector3.zero);
                    worker.SetRun(false);
                    worker.SetWalk(false);
                }
                catch { }
                return true;
            }

            float controlDt = Mathf.Clamp(dt > 0f ? dt : Time.fixedDeltaTime, 0.001f, 0.05f);
            Vector3 viewForward = _cameraPoseInitialized
                ? Quaternion.Euler(0f, _cameraYaw, 0f) * Vector3.forward
                : new Vector3((_rider != null ? _rider.transform.forward : worker.transform.forward).x, 0f, (_rider != null ? _rider.transform.forward : worker.transform.forward).z);
            viewForward.y = 0f;
            if (viewForward.sqrMagnitude < 0.001f) viewForward = Vector3.forward;
            viewForward.Normalize();
            Vector3 viewRight = Vector3.Cross(Vector3.up, viewForward).normalized;

            float forwardInput = 0f;
            float strafeInput = 0f;
            if (Input.GetKey(KeyCode.W)) forwardInput += 1f;
            if (Input.GetKey(KeyCode.S)) forwardInput -= 1f;
            if (Input.GetKey(KeyCode.A)) strafeInput -= 1f;
            if (Input.GetKey(KeyCode.D)) strafeInput += 1f;
            bool hasInput = Mathf.Abs(forwardInput) > 0.001f || Mathf.Abs(strafeInput) > 0.001f;
            bool wantsFast = hasInput && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
            bool fast = wantsFast && ConsumeTrollSprintStamina(_rider, controlDt);
            _manualFlightInputActive = hasInput;
            _idleHoldActive = false;

            Vector3 move;
            if (!_drakeEgoActive)
            {
                // v0.8.0 Third Person uses normal Valheim land semantics, matching the Werewolf:
                // input is camera-relative and the whole body turns into the requested movement
                // direction. A Troll therefore never crab-walks or moonwalks in third person.
                Vector3 desiredMove = viewForward * forwardInput + viewRight * strafeInput;
                if (desiredMove.sqrMagnitude > 0.001f)
                {
                    desiredMove.Normalize();
                    Quaternion desiredRotation = Quaternion.LookRotation(desiredMove, Vector3.up);
                    worker.transform.rotation = Quaternion.RotateTowards(worker.transform.rotation, desiredRotation, 420f * controlDt);
                    try { worker.SetLookDir(worker.transform.forward, 0f); } catch { }
                    move = worker.transform.forward;
                }
                else
                {
                    move = Vector3.zero;
                }
            }
            else
            {
                // Ego keeps the direct view-facing controller: W/S forward/back and A/D strafe.
                Quaternion desiredRotation = Quaternion.LookRotation(viewForward, Vector3.up);
                worker.transform.rotation = Quaternion.RotateTowards(worker.transform.rotation, desiredRotation, 360f * controlDt);
                Vector3 rawMove = worker.transform.forward * forwardInput + worker.transform.right * (strafeInput * 0.78f);
                move = rawMove.sqrMagnitude > 1f ? rawMove.normalized : rawMove;
            }

            worker.SetMoveDir(move);
            worker.SetRun(fast);
            worker.SetWalk(hasInput && !fast);
            return true;
        }

        private static bool ApplyWerewolfControls(Character worker, float dt)
        {
            if (!IsMounted(worker) || _rider == null) return false;
            ClearMountedDrakeNativeIntent(worker);

            if (_werewolfComboActive)
            {
                worker.SetMoveDir(Vector3.zero);
                worker.SetRun(false);
                worker.SetWalk(false);
                _manualFlightInputActive = false;
                return true;
            }

            if (_transitionState != SymbiosisTransitionState.None || IsMountUiBlocking())
            {
                worker.SetMoveDir(Vector3.zero);
                worker.SetRun(false);
                worker.SetWalk(false);
                return true;
            }

            float controlDt = Mathf.Clamp(dt > 0f ? dt : Time.fixedDeltaTime, 0.001f, 0.05f);
            float forwardInput = 0f;
            float strafeInput = 0f;
            if (Input.GetKey(KeyCode.W)) forwardInput += 1f;
            if (Input.GetKey(KeyCode.S)) forwardInput -= 1f;
            if (Input.GetKey(KeyCode.A)) strafeInput -= 1f;
            if (Input.GetKey(KeyCode.D)) strafeInput += 1f;
            bool hasInput = Mathf.Abs(forwardInput) > 0.001f || Mathf.Abs(strafeInput) > 0.001f;
            // Werewolf core movement rule: in Third Person every requested direction becomes
            // forward travel because the Fenring turns into the movement vector. In Ego, Shift
            // is a true forward predator sprint only; reverse/strafe remain controlled movement
            // and must not drain sprint stamina for an animation the Fenring cannot represent.
            bool sprintDirectionAllowed = !_drakeEgoActive || forwardInput > 0.35f;
            bool wantsFast = hasInput && sprintDirectionAllowed && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
            bool fast = wantsFast && ConsumeWerewolfSprintStamina(_rider, controlDt);
            _manualFlightInputActive = hasInput;
            _idleHoldActive = false;

            if (!_drakeEgoActive)
            {
                // Third Person: normal Valheim semantics. Input is camera-relative, but the body
                // turns into the actual movement direction. No strafing/reverse pose is kept on
                // the creature because Fenring has no useful sideways/backwards locomotion clips.
                Vector3 camForward = _cameraPoseInitialized
                    ? Quaternion.Euler(0f, _cameraYaw, 0f) * Vector3.forward
                    : (Camera.main != null ? Camera.main.transform.forward : worker.transform.forward);
                camForward.y = 0f;
                if (camForward.sqrMagnitude < 0.001f) camForward = worker.transform.forward;
                camForward.Normalize();
                Vector3 camRight = Vector3.Cross(Vector3.up, camForward).normalized;
                Vector3 desiredMove = camForward * forwardInput + camRight * strafeInput;

                if (desiredMove.sqrMagnitude > 0.001f)
                {
                    desiredMove.Normalize();
                    Quaternion desiredRotation = Quaternion.LookRotation(desiredMove, Vector3.up);
                    worker.transform.rotation = Quaternion.RotateTowards(worker.transform.rotation, desiredRotation, 720f * controlDt);
                    try { worker.SetLookDir(worker.transform.forward, 0f); } catch { }
                    worker.SetMoveDir(worker.transform.forward);
                }
                else
                {
                    worker.SetMoveDir(Vector3.zero);
                }
            }
            else
            {
                // Ego keeps the proven view-facing controller: W forward, S reverse, A/D strafe.
                Vector3 viewForward = GetWerewolfViewForward(worker);
                Quaternion desiredRotation = Quaternion.LookRotation(viewForward, Vector3.up);
                worker.transform.rotation = Quaternion.RotateTowards(worker.transform.rotation, desiredRotation, 540f * controlDt);
                Vector3 rawMove = worker.transform.forward * forwardInput + worker.transform.right * (strafeInput * 0.95f);
                Vector3 move = rawMove.sqrMagnitude > 1f ? rawMove.normalized : rawMove;
                worker.SetMoveDir(move);
            }

            worker.SetRun(fast);
            worker.SetWalk(hasInput && !fast);
            TryConsumeWerewolfMovementJump(worker);
            return true;
        }

        internal static void PostCharacterFixedUpdate(Character worker)
        {
            // v0.7.7: Third Person movement-facing is owned directly in the form controller; no post-Character override.
        }

        internal static void PostCharacterLateUpdate(Character worker)
        {
            // v0.7.7: no late body-facing correction; Third Person is resolved before SetMoveDir.
        }

        private static void ApplyWerewolfAuthoritativePhysics(Character worker, bool translateSignedMove)
        {
            if (worker == null || !IsMounted(worker) || !IsWerewolfForm) return;
            try
            {
                Vector3 forward = GetWerewolfViewForward(worker);
                Quaternion facing = Quaternion.LookRotation(forward, Vector3.up);
                _werewolfDesiredFacingRotation = facing;

                Rigidbody body = worker == _mountedDrake ? GetCachedMountedBody() : worker.GetComponent<Rigidbody>();
                if (body != null)
                {
                    // Rotation and translation are deliberately independent. Native Fenring movement
                    // can briefly turn 180 degrees for negative input; this post/native lock removes
                    // that turn while preserving gravity, contacts and vertical jump velocity.
                    body.angularVelocity = Vector3.zero;
                    body.rotation = facing;
                    worker.transform.rotation = facing;

                    Vector3 velocity = body.velocity;
                    bool blocked = _transitionState != SymbiosisTransitionState.None || _werewolfComboActive || IsMountUiBlocking();

                    if (blocked)
                    {
                        velocity.x = 0f;
                        velocity.z = 0f;
                        body.velocity = velocity;
                    }
                    else if (_werewolfAuthoritativeMoveActive)
                    {
                        // Reverse/strafe/diagonal translation is applied only from FixedUpdate.
                        // MovePosition works even if Valheim changes the creature Rigidbody mode and
                        // avoids the native Fenring controller reinterpreting S/A/D as forward facing.
                        velocity.x = 0f;
                        velocity.z = 0f;
                        if (Time.time < _werewolfJumpBoostUntil)
                            velocity.y = Mathf.Max(velocity.y, WerewolfMovementJumpVelocity * 0.82f);
                        body.velocity = velocity;

                        if (translateSignedMove)
                        {
                            float step = Mathf.Clamp(Time.fixedDeltaTime, 0.001f, 0.05f);
                            Vector3 delta = _werewolfDesiredHorizontalVelocity * step;
                            if (delta.sqrMagnitude > 0.000001f)
                                body.MovePosition(body.position + delta);
                        }
                    }
                    else if (_werewolfNativeForwardMoveActive)
                    {
                        // Pure W remains fully native; do not overwrite horizontal velocity here.
                        if (Time.time < _werewolfJumpBoostUntil)
                        {
                            velocity.y = Mathf.Max(velocity.y, WerewolfMovementJumpVelocity * 0.82f);
                            body.velocity = velocity;
                        }
                    }
                    else
                    {
                        // Released input: clear only residual horizontal drift.
                        velocity.x = 0f;
                        velocity.z = 0f;
                        if (Time.time < _werewolfJumpBoostUntil)
                            velocity.y = Mathf.Max(velocity.y, WerewolfMovementJumpVelocity * 0.82f);
                        body.velocity = velocity;
                    }
                }
                else
                {
                    worker.transform.rotation = facing;
                }

                try { worker.SetLookDir(forward, 0f); } catch { }

                // Signed movement keeps native moveDir at zero, so there is no second system trying
                // to rotate the Visual root. Pure W remains native. Drive the local blend here and
                // never snap the Visual transform itself (that snap was visible through the head camera).
                ApplyWerewolfAnimatorLocomotion();
            }
            catch { }
        }

        private static Vector3 GetWerewolfViewForward(Character worker)
        {
            Vector3 viewForward;
            if (_cameraPoseInitialized)
                viewForward = Quaternion.Euler(0f, _cameraYaw, 0f) * Vector3.forward;
            else
            {
                Camera camera = Camera.main;
                Vector3 source = camera != null ? camera.transform.forward : (worker != null ? worker.transform.forward : Vector3.forward);
                viewForward = new Vector3(source.x, 0f, source.z);
            }

            viewForward.y = 0f;
            if (viewForward.sqrMagnitude < 0.001f && worker != null)
                viewForward = new Vector3(worker.transform.forward.x, 0f, worker.transform.forward.z);
            if (viewForward.sqrMagnitude < 0.001f) viewForward = Vector3.forward;
            return viewForward.normalized;
        }

        private static void TryConsumeWerewolfMovementJump(Character worker)
        {
            if (worker == null || _rider == null) return;
            if (_werewolfMovementJumpQueuedUntil <= 0f || Time.unscaledTime > _werewolfMovementJumpQueuedUntil) return;

            _werewolfMovementJumpQueuedUntil = 0f;
            if (Time.time < _nextWerewolfMovementJumpAt) return;
            if (!IsWerewolfGrounded(worker)) return;

            try
            {
                if (!_rider.HaveStamina(WerewolfMovementJumpStaminaCost))
                {
                    _status?.Invoke($"Sprung braucht {WerewolfMovementJumpStaminaCost:0} Ausdauer.");
                    return;
                }
                _rider.UseStamina(WerewolfMovementJumpStaminaCost);
            }
            catch { }

            try
            {
                Rigidbody body = worker == _mountedDrake ? GetCachedMountedBody() : worker.GetComponent<Rigidbody>();
                if (body == null) return;
                Vector3 velocity = body.velocity;
                velocity.y = Mathf.Max(velocity.y, WerewolfMovementJumpVelocity);
                body.velocity = velocity;
                _werewolfJumpBoostUntil = Time.time + 0.10f;
                _nextWerewolfMovementJumpAt = Time.time + WerewolfMovementJumpCooldown;

                // Do not reuse the MMB attack_jump weapon here. This is locomotion, not an attack.
                // The native controller may have a plain jump trigger; use it only when present.
                TryTriggerAnimatorParameter(_drakeAnimator ?? ResolveDrakeAnimator(worker), "jump");
                _status?.Invoke("Werwolf-Sprung!");
                _debugLog?.Invoke($"Werewolf movement jump: velocityY={WerewolfMovementJumpVelocity:0.0}, stamina={WerewolfMovementJumpStaminaCost:0}.");
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke("Werewolf movement jump failed safely: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool IsWerewolfGrounded(Character worker)
        {
            if (worker == null) return false;
            try
            {
                if (CharacterIsOnGroundMethod != null)
                {
                    object result = CharacterIsOnGroundMethod.Invoke(worker, null);
                    // A possessed Fenring can report false for one or more ticks because its AI is
                    // intentionally suspended. Native TRUE is authoritative; native FALSE falls
                    // through to the physical foot probe instead of cancelling jump forever.
                    if (result is bool grounded && grounded) return true;
                }
            }
            catch { }

            try
            {
                Collider collider = worker.GetComponent<Collider>() ?? worker.GetComponentInChildren<Collider>();
                Bounds bounds = collider != null ? collider.bounds : new Bounds(worker.transform.position + Vector3.up * worker.GetRadius(), Vector3.one * Mathf.Max(0.5f, worker.GetRadius() * 2f));
                Vector3 origin = new Vector3(bounds.center.x, bounds.min.y + 0.20f, bounds.center.z);
                RaycastHit[] hits = Physics.SphereCastAll(origin, Mathf.Max(0.12f, worker.GetRadius() * 0.28f), Vector3.down, 0.42f, ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < hits.Length; ++i)
                {
                    Collider hit = hits[i].collider;
                    if (hit == null) continue;
                    Character owner = hit.GetComponentInParent<Character>();
                    if (owner == worker) continue;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static void ApplyWerewolfAnimatorLocomotion()
        {
            Animator animator = _drakeAnimator;
            if (animator == null && _mountedDrake != null)
            {
                animator = ResolveDrakeAnimator(_mountedDrake);
                if (animator != null) _drakeAnimator = animator;
            }
            if (animator == null) return;

            try
            {
                EnsureWerewolfAnimatorParamCache(animator);

                // Valheim creature controllers commonly expose forward_speed/sideway_speed floats.
                // Feeding the animator is safe: unlike Character.SetMoveDir it cannot rotate the
                // physical Fenring. Negative forward input can therefore select a reverse blend
                // while the body/camera keeps facing the view direction.
                float stride = _werewolfAuthoritativeMoveActive ? (_werewolfAnimFast ? 1.0f : 0.62f) : 0f;
                float dampDt = Time.deltaTime > 0f ? Time.deltaTime : Time.fixedDeltaTime;
                if (_werewolfAnimForwardHash != 0)
                    animator.SetFloat(_werewolfAnimForwardHash, _werewolfAnimForwardInput * stride, 0.05f, dampDt);
                if (_werewolfAnimSideHash != 0)
                    animator.SetFloat(_werewolfAnimSideHash, _werewolfAnimStrafeInput * stride, 0.05f, dampDt);
                if (_werewolfAnimTurnHash != 0)
                    animator.SetFloat(_werewolfAnimTurnHash, 0f, 0.03f, dampDt);
            }
            catch { }
        }

        private static void EnsureWerewolfAnimatorParamCache(Animator animator)
        {
            if (_werewolfAnimatorParamCacheReady || animator == null) return;
            _werewolfAnimatorParamCacheReady = true;
            try
            {
                List<string> floats = new List<string>();
                AnimatorControllerParameter[] parameters = animator.parameters;
                for (int i = 0; i < parameters.Length; ++i)
                {
                    AnimatorControllerParameter parameter = parameters[i];
                    if (parameter.type != AnimatorControllerParameterType.Float) continue;
                    floats.Add(parameter.name);
                    if (string.Equals(parameter.name, "forward_speed", StringComparison.OrdinalIgnoreCase))
                        _werewolfAnimForwardHash = Animator.StringToHash(parameter.name);
                    else if (string.Equals(parameter.name, "sideway_speed", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(parameter.name, "sideways_speed", StringComparison.OrdinalIgnoreCase))
                        _werewolfAnimSideHash = Animator.StringToHash(parameter.name);
                    else if (string.Equals(parameter.name, "turn_speed", StringComparison.OrdinalIgnoreCase))
                        _werewolfAnimTurnHash = Animator.StringToHash(parameter.name);
                }

                if (!_werewolfAnimatorParamsReported)
                {
                    _werewolfAnimatorParamsReported = true;
                    _debugLog?.Invoke("Werewolf animator locomotion override active; float params=" + string.Join(",", floats.ToArray()) +
                        $"; mapped forward={_werewolfAnimForwardHash != 0}, side={_werewolfAnimSideHash != 0}, turn={_werewolfAnimTurnHash != 0}.");
                }
            }
            catch { }
        }

        private static void TryTriggerAnimatorParameter(Animator animator, string parameterName)
        {
            if (animator == null || string.IsNullOrEmpty(parameterName)) return;
            try
            {
                AnimatorControllerParameter[] parameters = animator.parameters;
                for (int i = 0; i < parameters.Length; ++i)
                {
                    AnimatorControllerParameter parameter = parameters[i];
                    if (!string.Equals(parameter.name, parameterName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (parameter.type == AnimatorControllerParameterType.Trigger)
                        animator.SetTrigger(parameter.name);
                    return;
                }
            }
            catch { }
        }

        private static void EnforceWerewolfFacingLock()
        {
            if (!Active || !IsWerewolfForm || _mountedDrake == null || _transitionState != SymbiosisTransitionState.None) return;
            try
            {
                Vector3 forward = GetWerewolfViewForward(_mountedDrake);
                _mountedDrake.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
                _mountedDrake.SetLookDir(forward, 0f);

                // No Visual-root snap here. Reverse locomotion no longer reaches Character.m_moveDir,
                // so there is nothing native to "correct" back from 180°. Keeping the hierarchy free
                // of competing rotations is what makes the head-mounted Ego camera stable.
                ApplyWerewolfAnimatorLocomotion();
            }
            catch { }
        }

        private static bool ApplySerpentControls(Character worker, float dt)
        {
            if (!IsMounted(worker) || _rider == null) return false;
            ClearMountedDrakeNativeIntent(worker);

            if (_transitionState != SymbiosisTransitionState.None || IsMountUiBlocking())
            {
                ResetFlightMoveSmoothing();
                if (!_idleHoldActive) CaptureIdleHold(worker);
                EnforceIdleHold(worker);
                return true;
            }

            float controlDt = Mathf.Clamp(dt > 0f ? dt : Time.fixedDeltaTime, 0.001f, 0.05f);
            Vector3 cameraForward = _cameraPoseInitialized
                ? Quaternion.Euler(0f, _cameraYaw, 0f) * Vector3.forward
                : (Camera.main != null ? Camera.main.transform.forward : worker.transform.forward);
            cameraForward.y = 0f;
            if (cameraForward.sqrMagnitude < 0.001f) cameraForward = worker.transform.forward;
            cameraForward.Normalize();
            Vector3 cameraRight = Vector3.Cross(Vector3.up, cameraForward).normalized;

            float forwardInput = 0f;
            float strafeInput = 0f;
            if (Input.GetKey(KeyCode.W)) forwardInput += 1f;
            if (Input.GetKey(KeyCode.S)) forwardInput -= 1f;
            if (Input.GetKey(KeyCode.A)) strafeInput -= 1f;
            if (Input.GetKey(KeyCode.D)) strafeInput += 1f;

            bool hasDirectionalInput = Mathf.Abs(forwardInput) > 0.001f || Mathf.Abs(strafeInput) > 0.001f;
            bool wantsFast = hasDirectionalInput && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
            bool fast = wantsFast && ConsumeMovementBoostStamina(_rider, SerpentBoostStaminaPerSecond, controlDt);
            _manualFlightInputActive = hasDirectionalInput;
            _idleHoldActive = false;

            // v2.6.1: Valheim's water/combat/camera stack is much more stable when the possessed
            // Sea Serpent remains a surface swimmer. Mouse look now owns yaw only. W is the very
            // fast travel direction, while S is deliberately a slow reverse crawl so normal retreat
            // still means turning the whole body around.
            float reverseScale = forwardInput < -0.10f ? SerpentBackwardSpeedScale : 1f;
            Vector3 aimHorizontal = cameraForward;
            if (aimHorizontal.sqrMagnitude < 0.001f) aimHorizontal = worker.transform.forward;
            aimHorizontal.y = 0f;
            aimHorizontal.Normalize();

            if (forwardInput > 0.10f)
            {
                Quaternion desiredRotation = Quaternion.LookRotation(aimHorizontal, Vector3.up);
                worker.transform.rotation = Quaternion.RotateTowards(worker.transform.rotation, desiredRotation, 220f * controlDt);
                try { worker.SetLookDir(aimHorizontal, 0f); } catch { }
            }

            Vector3 bodyForward = worker.transform.forward; bodyForward.y = 0f;
            if (bodyForward.sqrMagnitude < 0.001f) bodyForward = aimHorizontal;
            bodyForward.Normalize();
            Vector3 bodyRight = worker.transform.right; bodyRight.y = 0f;
            if (bodyRight.sqrMagnitude < 0.001f) bodyRight = cameraRight;
            bodyRight.Normalize();

            Vector3 horizontalMove = bodyForward * (forwardInput * reverseScale)
                                   + bodyRight * (strafeInput * SerpentLateralSpeedScale);
            Vector3 targetMove = horizontalMove.sqrMagnitude > 0.0001f ? Vector3.ClampMagnitude(horizontalMove, 1f) : Vector3.zero;

            if (!_flightMoveSmoothInitialized)
            {
                _smoothedFlightMove = Vector3.zero;
                _flightMoveSmoothInitialized = true;
            }
            float moveT = 1f - Mathf.Exp(-(fast ? 8.5f : 6.5f) * controlDt);
            _smoothedFlightMove = Vector3.Lerp(_smoothedFlightMove, targetMove, moveT);
            _serpentVisualPitch = Mathf.Lerp(_serpentVisualPitch, 0f, 1f - Mathf.Exp(-7f * controlDt));

            worker.SetMoveDir(_smoothedFlightMove);
            worker.SetRun(fast || hasDirectionalInput);
            worker.SetWalk(!fast && hasDirectionalInput);

            // Keep the actual animated eye/head point above the water instead of guessing a fixed
            // root depth. v2.6.2 deliberately keeps the whole head well above the waterline;
            // Sea Serpent diving is no longer supported because Valheim's water stack fights it.
            ApplySerpentSurfaceLock(worker, controlDt);
            return true;
        }

        private static void ApplySerpentSurfaceLock(Character worker, float dt)
        {
            if (worker == null || !IsSerpentForm) return;
            try
            {
                Transform eye = _drakeEgoAnchor != null ? _drakeEgoAnchor : ResolveDrakeEgoAnchor(worker);
                if (eye != null) _drakeEgoAnchor = eye;
                float currentEyeY = eye != null ? eye.position.y : worker.transform.position.y + Mathf.Max(0.8f, worker.GetRadius() * 0.8f);
                float targetEyeY = _serpentSurfaceY + SerpentSurfaceEyeClearance;
                float desiredRootY = worker.transform.position.y + (targetEyeY - currentEyeY);

                Rigidbody body = worker.GetComponent<Rigidbody>();
                float currentRootY = body != null ? body.position.y : worker.transform.position.y;
                float nextY = Mathf.MoveTowards(currentRootY, desiredRootY, SerpentSurfaceMotorSpeed * dt);
                float verticalSpeed = Mathf.Clamp((desiredRootY - currentRootY) * 5f, -SerpentSurfaceMotorSpeed, SerpentSurfaceMotorSpeed);

                if (body != null)
                {
                    Vector3 v = body.velocity;
                    v.y = verticalSpeed;
                    body.velocity = v;
                    Vector3 pos = body.position;
                    pos.y = nextY;
                    body.MovePosition(pos);
                }
                else
                {
                    Vector3 pos = worker.transform.position;
                    pos.y = nextY;
                    worker.transform.position = pos;
                }
            }
            catch { }
        }

        private static void ApplyDrakeVisualBank(Character worker)
        {
            if (worker == null || !IsDrakeForm) return;
            try
            {
                if (_drakeBankVisualRoot == null)
                {
                    _drakeBankVisualRoot = ResolveVisual(worker);
                    if (_drakeBankVisualRoot == null) return;
                    _drakeBankBaseLocalRotation = _drakeBankVisualRoot.transform.localRotation;
                    _drakeBankBaseLocalPosition = _drakeBankVisualRoot.transform.localPosition;
                    _drakeVisualBankDegrees = 0f;
                    _drakeVisualWingPitchDegrees = 0f;
                    _drakeVisualWingLift = 0f;
                    _debugLog?.Invoke("Drake visual banking initialized.");
                }

                float lateral = 0f;
                if (_transitionState == SymbiosisTransitionState.None && !IsMountUiBlocking())
                {
                    if (Input.GetKey(KeyCode.A)) lateral -= 1f;
                    if (Input.GetKey(KeyCode.D)) lateral += 1f;
                }

                float yawError = Mathf.DeltaAngle(worker.transform.eulerAngles.y, _cameraYaw);
                float turnBank = Mathf.Clamp(yawError * -0.075f, -7f, 7f);
                float targetBank = Mathf.Clamp((-lateral * 13f) + turnBank, -18f, 18f);
                if (!_manualFlightInputActive) targetBank = 0f;

                float dt = Mathf.Clamp(Time.deltaTime, 0.001f, 0.05f);
                float t = 1f - Mathf.Exp(-6.5f * dt);
                _drakeVisualBankDegrees = Mathf.Lerp(_drakeVisualBankDegrees, targetBank, t);

                // v0.9.0: reuse the real Drake animator clock for a tiny render-only wingbeat pulse.
                // This gives the body weight on each flap without touching Rigidbody, collider or authoritative movement.
                float wingMeters = EvaluateAnimatorWingbeat(worker);
                float speed01 = Mathf.Clamp01(_smoothedFlightMove.magnitude);
                float targetLift = Mathf.Clamp(wingMeters * Mathf.Lerp(1.5f, 4.0f, speed01), -0.055f, 0.055f);
                float targetPitch = Mathf.Clamp(-wingMeters * Mathf.Lerp(220f, 460f, speed01), -5.0f, 5.0f);
                _drakeVisualWingLift = Mathf.Lerp(_drakeVisualWingLift, targetLift, t);
                _drakeVisualWingPitchDegrees = Mathf.Lerp(_drakeVisualWingPitchDegrees, targetPitch, t);

                _drakeBankVisualRoot.transform.localRotation = _drakeBankBaseLocalRotation * Quaternion.Euler(_drakeVisualWingPitchDegrees, 0f, _drakeVisualBankDegrees);
                _drakeBankVisualRoot.transform.localPosition = _drakeBankBaseLocalPosition + Vector3.up * _drakeVisualWingLift;
            }
            catch { }
        }

        private static void ApplySerpentVisualSwimWave(Character worker)
        {
            if (worker == null || !IsSerpentForm) return;
            try
            {
                if (_serpentVisualRoot == null)
                {
                    _serpentVisualRoot = ResolveVisual(worker);
                    if (_serpentVisualRoot == null) return;
                    _serpentVisualBaseLocalRotation = _serpentVisualRoot.transform.localRotation;
                    BuildSerpentWaveBoneChain(_serpentVisualRoot.transform);
                    _serpentVisualWaveInitialized = true;
                    _serpentSwimPhase = 0f;
                    _debugLog?.Invoke($"Sea Serpent swim wave initialized: visual={_serpentVisualRoot.name}, chainBones={_serpentWaveBones.Length}.");
                }

                Rigidbody rb = worker.GetComponent<Rigidbody>();
                Vector3 horizontalVelocity = rb != null ? rb.velocity : Vector3.zero;
                horizontalVelocity.y = 0f;
                float speed = horizontalVelocity.magnitude;
                float activity = Mathf.Clamp01(speed / 7.5f);
                if (_manualFlightInputActive) activity = Mathf.Max(activity, 0.50f);

                float dt = Mathf.Clamp(Time.deltaTime, 0.001f, 0.05f);
                float turnBoost = (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.D)) ? 0.22f : 0f;
                float frequency = Mathf.Lerp(0.78f, 1.72f, activity) + turnBoost;
                _serpentSwimPhase += dt * frequency * Mathf.PI * 2f;

                if (_serpentWaveBones.Length > 1 && _serpentWaveBoneBaseRotations.Length == _serpentWaveBones.Length)
                {
                    // Propagating S-curve along spine/tail. Each segment receives a phase offset so the
                    // body actually snakes through the water instead of rotating as one rigid object.
                    int count = _serpentWaveBones.Length;
                    for (int i = 0; i < count; i++)
                    {
                        Transform bone = _serpentWaveBones[i];
                        if (bone == null) continue;
                        float t = count <= 1 ? 0f : i / (float)(count - 1);
                        float phase = _serpentSwimPhase - t * 2.25f * Mathf.PI;
                        float tailGain = Mathf.Lerp(0.45f, 1.25f, t);
                        float yaw = Mathf.Sin(phase) * Mathf.Lerp(1.4f, 6.2f, activity) * tailGain;
                        float roll = Mathf.Sin(phase + 1.15f) * Mathf.Lerp(0.20f, 1.55f, activity) * tailGain;
                        // Root carries the global dive/climb pitch; chain bones keep only the local
                        // travelling S-wave so the pitch does not multiply down the skeleton.
                        bone.localRotation = _serpentWaveBoneBaseRotations[i] * Quaternion.Euler(0f, yaw, roll);
                    }
                    // Keep only a tiny root sway; the real motion now comes from the articulated chain.
                    float rootYaw = Mathf.Sin(_serpentSwimPhase) * Mathf.Lerp(0.5f, 2.1f, activity);
                    _serpentVisualRoot.transform.localRotation = _serpentVisualBaseLocalRotation * Quaternion.Euler(_serpentVisualPitch, rootYaw, 0f);
                }
                else
                {
                    // Safe fallback for unexpected prefab skeletons.
                    float yaw = Mathf.Sin(_serpentSwimPhase) * Mathf.Lerp(2.0f, 8.0f, activity);
                    float roll = Mathf.Sin(_serpentSwimPhase * 2f + 0.7f) * Mathf.Lerp(0.4f, 2.0f, activity);
                    _serpentVisualRoot.transform.localRotation = _serpentVisualBaseLocalRotation * Quaternion.Euler(_serpentVisualPitch, yaw, roll);
                }
            }
            catch { }
        }

        private static void BuildSerpentWaveBoneChain(Transform visualRoot)
        {
            if (visualRoot == null)
            {
                _serpentWaveBones = Array.Empty<Transform>();
                _serpentWaveBoneBaseRotations = Array.Empty<Quaternion>();
                return;
            }

            List<Transform> candidates = new List<Transform>();
            Transform[] all = visualRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null || t == visualRoot) continue;
                string n = (t.name ?? string.Empty).ToLowerInvariant();
                bool bodyBone = n.Contains("spine") || n.Contains("tail") || n.Contains("neck") ||
                                n.Contains("body") || n.Contains("verte") || n.Contains("bone");
                if (!bodyBone) continue;
                if (n.Contains("head") || n.Contains("jaw") || n.Contains("eye")) continue;
                candidates.Add(t);
            }

            // Prefer the longest useful chain but cap it to avoid over-bending dense rigs.
            if (candidates.Count > 14)
            {
                List<Transform> sampled = new List<Transform>();
                for (int i = 0; i < 14; i++)
                {
                    int idx = Mathf.RoundToInt(i * (candidates.Count - 1) / 13f);
                    Transform t = candidates[idx];
                    if (!sampled.Contains(t)) sampled.Add(t);
                }
                candidates = sampled;
            }

            _serpentWaveBones = candidates.ToArray();
            _serpentWaveBoneBaseRotations = new Quaternion[_serpentWaveBones.Length];
            for (int i = 0; i < _serpentWaveBones.Length; i++)
                _serpentWaveBoneBaseRotations[i] = _serpentWaveBones[i] != null ? _serpentWaveBones[i].localRotation : Quaternion.identity;
        }

        private static bool HasManualFlightInput()
        {
            if (_transitionState != SymbiosisTransitionState.None) return false;
            if (IsMountUiBlocking()) return false;
            return Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.S) ||
                   Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.D) ||
                   Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.LeftControl) ||
                   Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.C);
        }

        private static bool HasManualLandInput()
        {
            if (_transitionState != SymbiosisTransitionState.None || IsMountUiBlocking()) return false;
            return Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.D);
        }

        private static void UpdateManualPossessionHold(Character worker)
        {
            if (worker == null || !IsMounted(worker)) return;
            ClearMountedDrakeNativeIntent(worker);
            if (IsSerpentForm)
            {
                // Water form owns its depth continuously in FixedUpdate; a Drake-style absolute
                // hover lock would fight that depth correction and make the Serpent stutter.
                _idleHoldActive = false;
                return;
            }
            if (IsTrollForm || IsWerewolfForm)
            {
                // Land forms must keep native gravity/ground contact. Never use the Drake absolute
                // position hold here or a grounded form standing on a slope/step would hover or jitter.
                _idleHoldActive = false;
                if (!HasManualLandInput())
                {
                    try
                    {
                        worker.SetMoveDir(Vector3.zero);
                        worker.SetRun(false);
                        worker.SetWalk(false);
                    }
                    catch { }
                }
                return;
            }

            bool input = HasManualFlightInput();
            _manualFlightInputActive = input;
            if (input)
            {
                _idleHoldActive = false;
                return;
            }

            // ApplyControls owns a short key-release deceleration. Do not let Update/LateUpdate
            // hard-snap the body into the hover lock while that filtered velocity is still fading.
            if (_flightMoveSmoothInitialized && _smoothedFlightMove.sqrMagnitude > 0.0016f)
            {
                _idleHoldActive = false;
                return;
            }

            if (!_idleHoldActive) CaptureIdleHold(worker);
            EnforceIdleHold(worker);
        }

        private static void CaptureIdleHold(Character worker)
        {
            if (worker == null) return;
            _idleHoldPosition = worker.transform.position;
            _idleHoldRotation = worker.transform.rotation;
            _idleHoldActive = true;
            _manualFlightInputActive = false;
            _smoothedFlightMove = Vector3.zero;
            _flightMoveSmoothInitialized = true;
            EnforceIdleHold(worker);
        }

        private static void EnforceIdleHold(Character worker)
        {
            if (worker == null || !_idleHoldActive) return;
            try
            {
                worker.SetMoveDir(Vector3.zero);
                worker.SetRun(false);
                worker.SetWalk(false);

                Rigidbody body = _smoothFlightBody != null ? _smoothFlightBody : worker.GetComponent<Rigidbody>();
                if (body != null)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;

                    // Do not rewrite transform + Rigidbody every rendered frame when the body is
                    // already at the hold pose. Repeated hard writes reset Unity interpolation and
                    // show up as tiny camera/body jumps even though the numerical position is stable.
                    if ((body.position - _idleHoldPosition).sqrMagnitude > 0.0004f)
                        body.position = _idleHoldPosition;
                    if (Quaternion.Angle(body.rotation, _idleHoldRotation) > 0.5f)
                        body.rotation = _idleHoldRotation;
                }
                else
                {
                    if ((worker.transform.position - _idleHoldPosition).sqrMagnitude > 0.0004f)
                        worker.transform.position = _idleHoldPosition;
                    if (Quaternion.Angle(worker.transform.rotation, _idleHoldRotation) > 0.5f)
                        worker.transform.rotation = _idleHoldRotation;
                }
            }
            catch { }
        }

        private static void PrepareSmoothFlightBody(Character worker)
        {
            RestoreSmoothFlightBodyInterpolation();
            ResetFlightMoveSmoothing();
            ResetNativeDrakeAttackCatalog(null);
            _wingbeatSmoothInitialized = false;
            _smoothedWingbeatMeters = 0f;

            if (worker == null) return;
            try
            {
                _smoothFlightBody = worker.GetComponent<Rigidbody>();
                if (_smoothFlightBody == null) return;

                _savedFlightBodyInterpolation = _smoothFlightBody.interpolation;
                _savedFlightBodyInterpolationValid = true;
                _savedFlightCollisionMode = _smoothFlightBody.collisionDetectionMode;
                _savedFlightCollisionModeValid = true;

                // Character movement is physics/fixed-step based. Interpolate the root transform
                // onto render frames so both the Drake mesh and cameras see continuous movement.
                if (_smoothFlightBody.interpolation != RigidbodyInterpolation.Interpolate)
                    _smoothFlightBody.interpolation = RigidbodyInterpolation.Interpolate;

                // Flight can be much faster than vanilla ground movement. Continuous collision
                // detection prevents the possessed Drake from visually stepping through thin roof/rock
                // colliders between physics ticks, especially while Shift-boosting.
                CollisionDetectionMode desiredCollision = _smoothFlightBody.isKinematic
                    ? CollisionDetectionMode.ContinuousSpeculative
                    : CollisionDetectionMode.ContinuousDynamic;
                if (_smoothFlightBody.collisionDetectionMode != desiredCollision)
                    _smoothFlightBody.collisionDetectionMode = desiredCollision;
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Drake flight interpolation setup failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void RestoreSmoothFlightBodyInterpolation()
        {
            try
            {
                if (_smoothFlightBody != null && _savedFlightBodyInterpolationValid)
                    _smoothFlightBody.interpolation = _savedFlightBodyInterpolation;
                if (_smoothFlightBody != null && _savedFlightCollisionModeValid)
                    _smoothFlightBody.collisionDetectionMode = _savedFlightCollisionMode;
            }
            catch { }

            _smoothFlightBody = null;
            _savedFlightBodyInterpolationValid = false;
            _savedFlightCollisionModeValid = false;
        }

        private static void ApplyDrakeNativeTravelAssist(Character worker)
        {
            if (worker == null || IsSerpentForm || IsTrollForm || IsWerewolfForm ||
                _transitionState != SymbiosisTransitionState.None || IsMountUiBlocking() || !_manualFlightInputActive)
            {
                _drakeNativeTravelAssistInitialized = false;
                return;
            }

            try
            {
                Rigidbody body = worker == _mountedDrake ? GetCachedMountedBody() : worker.GetComponent<Rigidbody>();
                if (body == null)
                {
                    _drakeNativeTravelAssistInitialized = false;
                    return;
                }

                Vector3 current = body.position;
                if (!_drakeNativeTravelAssistInitialized)
                {
                    _drakeNativeTravelAssistLastPosition = current;
                    _drakeNativeTravelAssistInitialized = true;
                    return;
                }

                Vector3 nativeDelta = current - _drakeNativeTravelAssistLastPosition;
                // Ignore teleports/body swaps/network correction jumps. Normal frame travel is
                // far below this threshold.
                if (nativeDelta.sqrMagnitude > 9f)
                {
                    _drakeNativeTravelAssistLastPosition = current;
                    return;
                }

                Vector3 desired = _smoothedFlightMove;
                if (desired.sqrMagnitude < 0.0004f)
                {
                    _drakeNativeTravelAssistLastPosition = current;
                    return;
                }
                desired.Normalize();

                float nativeAlong = Vector3.Dot(nativeDelta, desired);
                if (nativeAlong <= 0.00001f)
                {
                    _drakeNativeTravelAssistLastPosition = current;
                    return;
                }

                float multiplier = _drakeFastFlightActive ? DrakeNativeDiveTravelMultiplier : DrakeNativeTravelMultiplier;
                Vector3 assist = desired * (nativeAlong * (multiplier - 1f));
                assist = Vector3.ClampMagnitude(assist, _drakeFastFlightActive ? 1.25f : 0.75f);
                if (assist.sqrMagnitude > 0.0000001f)
                {
                    body.position = current + assist;
                    current = body.position;
                }

                _drakeNativeTravelAssistLastPosition = current;
            }
            catch
            {
                _drakeNativeTravelAssistInitialized = false;
            }
        }

        private static void ResetFlightMoveSmoothing()
        {
            RestoreDrakeWingbeatPropulsion();
            _drakeNativeTravelAssistInitialized = false;
            _drakeFastFlightActive = false;
            _drakeDiveStartedAt = -1f;
            _drakeFreeClimbUntil = -1f;
            _smoothedFlightMove = Vector3.zero;
            _flightMoveSmoothInitialized = false;
        }

        private static float EvaluateDrakeWingbeatPropulsion(Character worker, bool boosted)
        {
            if (worker == null || IsSerpentForm || IsTrollForm || IsWerewolfForm) return 1f;
            try
            {
                Animator animator = _drakeAnimator ?? ResolveDrakeAnimator(worker);
                if (animator == null || !animator.isActiveAndEnabled) return 1f;
                _drakeAnimator = animator;

                AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
                float phase = state.normalizedTime - Mathf.Floor(state.normalizedTime);

                // A smooth single propulsion pulse per animation cycle. Peak is intentionally
                // around the first half of the cycle where the visible wing downstroke normally
                // contributes the strongest thrust. Unknown clips still remain close to 1.0.
                float pulse01 = 0.5f + 0.5f * Mathf.Sin((phase - 0.10f) * Mathf.PI * 2f);
                pulse01 = Mathf.SmoothStep(0f, 1f, pulse01);

                float max = boosted ? DrakeWingbeatPropulsionMaxBoost : DrakeWingbeatPropulsionMax;
                float factor = Mathf.Lerp(DrakeWingbeatPropulsionMin, max, pulse01);

                // Blend toward neutral while hovering/using unknown clips. FlyForward gets the full
                // pulse; IdleHover only gets a very small living-motion variation.
                AnimatorClipInfo[] clips = animator.GetCurrentAnimatorClipInfo(0);
                float flyWeight = 0f;
                float hoverWeight = 0f;
                if (clips != null)
                {
                    for (int i = 0; i < clips.Length; i++)
                    {
                        if (clips[i].clip == null || clips[i].weight <= 0.0001f) continue;
                        string name = clips[i].clip.name ?? string.Empty;
                        if (name.IndexOf("FlyForward", StringComparison.OrdinalIgnoreCase) >= 0) flyWeight += clips[i].weight;
                        else if (name.IndexOf("IdleHover", StringComparison.OrdinalIgnoreCase) >= 0) hoverWeight += clips[i].weight;
                    }
                }

                float authority = Mathf.Clamp01(flyWeight + hoverWeight * 0.18f);
                factor = Mathf.Lerp(1f, factor, authority);
                _drakeWingbeatPropulsion = Mathf.Lerp(_drakeWingbeatPropulsion, factor, 0.35f);
                return Mathf.Clamp(_drakeWingbeatPropulsion, 0.85f, 1.20f);
            }
            catch
            {
                return 1f;
            }
        }

        private static void ApplyDrakeWingbeatPropulsion(Character worker, float factor)
        {
            if (worker == null || IsSerpentForm || IsTrollForm || IsWerewolfForm) return;
            try
            {
                if (!_drakePropulsionSpeedCaptured || _drakePropulsionCharacter != worker)
                {
                    RestoreDrakeWingbeatPropulsion();
                    _drakePropulsionCharacter = worker;
                    // v3.0.7: do NOT touch Hatchling m_flySlowSpeed/m_flyFastSpeed here.
                    // Those native flight-envelope fields also feed the vanilla locomotion/animation
                    // selection. Overriding them made FlyForward stop flapping and left wingbeats mostly
                    // in IdleHover. The pre-v3.0.4 behaviour felt correct, so preserve native flight
                    // speeds/animation and keep the old walk/run pulse only as a harmless compatibility
                    // layer. Shift still selects the native fast-flight state via SetRun(true).
                    FieldInfo walk = worker.GetType().GetField("m_walkSpeed", InstanceFlags) ?? typeof(Character).GetField("m_walkSpeed", InstanceFlags);
                    FieldInfo run = worker.GetType().GetField("m_runSpeed", InstanceFlags) ?? typeof(Character).GetField("m_runSpeed", InstanceFlags);
                    if (walk == null || run == null || walk.FieldType != typeof(float) || run.FieldType != typeof(float)) return;
                    _drakeBaseWalkSpeed = (float)walk.GetValue(worker);
                    _drakeBaseRunSpeed = (float)run.GetValue(worker);
                    _drakePropulsionSpeedCaptured = true;
                    _debugLog?.Invoke($"Drake native flight envelope preserved; wingbeat compatibility pulse uses {walk.Name}/{run.Name}. slow/fast flight fields remain prefab-native.");
                }

                FieldInfo walkField = worker.GetType().GetField("m_walkSpeed", InstanceFlags) ?? typeof(Character).GetField("m_walkSpeed", InstanceFlags);
                FieldInfo runField = worker.GetType().GetField("m_runSpeed", InstanceFlags) ?? typeof(Character).GetField("m_runSpeed", InstanceFlags);
                if (walkField != null && walkField.FieldType == typeof(float)) walkField.SetValue(worker, _drakeBaseWalkSpeed * factor);
                if (runField != null && runField.FieldType == typeof(float)) runField.SetValue(worker, _drakeBaseRunSpeed * factor);
            }
            catch { }
        }

        private static void RestoreDrakeWingbeatPropulsion()
        {
            try
            {
                if (_drakePropulsionSpeedCaptured && _drakePropulsionCharacter != null)
                {
                    FieldInfo walk = _drakePropulsionCharacter.GetType().GetField("m_walkSpeed", InstanceFlags) ?? typeof(Character).GetField("m_walkSpeed", InstanceFlags);
                    FieldInfo run = _drakePropulsionCharacter.GetType().GetField("m_runSpeed", InstanceFlags) ?? typeof(Character).GetField("m_runSpeed", InstanceFlags);
                    if (walk != null && walk.FieldType == typeof(float)) walk.SetValue(_drakePropulsionCharacter, _drakeBaseWalkSpeed);
                    if (run != null && run.FieldType == typeof(float)) run.SetValue(_drakePropulsionCharacter, _drakeBaseRunSpeed);
                }
            }
            catch { }
            _drakePropulsionCharacter = null;
            _drakeBaseWalkSpeed = 0f;
            _drakeBaseRunSpeed = 0f;
            _drakePropulsionSpeedCaptured = false;
            _drakeWingbeatPropulsion = 1f;
        }

        private static void ClearMountedDrakeNativeIntent(Character worker)
        {
            if (worker == null) return;
            try
            {
                MonsterAI monster = worker.GetComponent<MonsterAI>();
                if (monster == null) return;

                if (monster.GetFollowTarget() != null)
                    WorkerNavigationRuntime.SetNativeFollowTarget(monster, null);

                MonsterTargetCreatureField?.SetValue(monster, null);
                MonsterTargetStaticField?.SetValue(monster, null);
                SetAlertedMethod?.Invoke(monster, new object[] { false });
            }
            catch { }
        }

        private static void CaptureHumanCameraRig(Player player)
        {
            _humanCameraRigCaptured = false;
            if (player == null) return;
            try
            {
                Camera camera = Camera.main;
                if (camera == null) return;
                _humanCameraLocalOffset = player.transform.InverseTransformPoint(camera.transform.position);
                _humanCameraLocalRotation = Quaternion.Inverse(player.transform.rotation) * camera.transform.rotation;
                _humanCameraRigCaptured = true;
            }
            catch { }
        }

        private static void BeginEnterTransformation(Character worker, Player player, bool temporaryAvatar)
        {
            _transitionState = SymbiosisTransitionState.Entering;
            _transitionStartedAt = Time.unscaledTime;
            _transitionUsesTemporaryAvatar = temporaryAvatar;
            _pendingDismountMessage = string.Empty;
            _entryPlayerAnchorPosition = player.transform.position;
            _entryPlayerAnchorRotation = player.transform.rotation;
            _playerVisualsHidden = false;

            try
            {
                Camera camera = Camera.main;
                if (camera != null)
                {
                    _transitionCameraStartPosition = camera.transform.position;
                    _transitionCameraStartRotation = camera.transform.rotation;
                }
                else
                {
                    _transitionCameraStartPosition = player.transform.position + Vector3.up * 1.6f;
                    _transitionCameraStartRotation = player.transform.rotation;
                }
            }
            catch
            {
                _transitionCameraStartPosition = player.transform.position + Vector3.up * 1.6f;
                _transitionCameraStartRotation = player.transform.rotation;
            }

            CaptureTransitionDrakeScale(worker, temporaryAvatar);
            _transitionHandoffFxPlayed = false;
            PlayTransformationChargeFx(true);
            _debugLog?.Invoke($"Drake transformation enter started (temporary={temporaryAvatar}, duration={TransformationDuration:0.00}s).");
        }

        private static void CaptureTransitionDrakeScale(Character worker, bool temporaryAvatar, bool startSmall = true)
        {
            RestoreTransitionDrakeScale();
            _transitionUsesTemporaryAvatar = temporaryAvatar;
            if (!temporaryAvatar || worker == null) return;

            try
            {
                GameObject visual = ResolveVisual(worker);
                if (visual == null || visual.transform == worker.transform) return;
                _transitionDrakeVisual = visual.transform;
                _transitionDrakeOriginalScale = _transitionDrakeVisual.localScale;
                _transitionDrakeScaleCaptured = true;
                if (startSmall) _transitionDrakeVisual.localScale = _transitionDrakeOriginalScale * 0.12f;
            }
            catch (Exception ex)
            {
                _transitionDrakeVisual = null;
                _transitionDrakeScaleCaptured = false;
                _debugLog?.Invoke($"Drake transformation scale setup failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void RestoreTransitionDrakeScale()
        {
            if (_transitionDrakeScaleCaptured && _transitionDrakeVisual != null)
            {
                try { _transitionDrakeVisual.localScale = _transitionDrakeOriginalScale; }
                catch { }
            }
            _transitionDrakeVisual = null;
            _transitionDrakeOriginalScale = Vector3.one;
            _transitionDrakeScaleCaptured = false;
        }

        private static void AdvanceTransformation()
        {
            if (_transitionState == SymbiosisTransitionState.None || !Active) return;
            float t = Mathf.Clamp01((Time.unscaledTime - _transitionStartedAt) / TransformationDuration);

            if (_transitionState == SymbiosisTransitionState.Entering)
            {
                if (_transitionDrakeScaleCaptured && _transitionDrakeVisual != null)
                {
                    float morph = Smooth01(Mathf.InverseLerp(0.12f, 0.88f, t));
                    float factor = Mathf.Lerp(0.12f, 1f, morph);
                    try { _transitionDrakeVisual.localScale = _transitionDrakeOriginalScale * factor; }
                    catch { }
                }

                if (t >= 0.47f && !_transitionHandoffFxPlayed)
                {
                    _transitionHandoffFxPlayed = true;
                    PlayTransformationHandoffFx(true);
                }

                if (t >= 0.52f && !_playerVisualsHidden)
                {
                    SetPlayerVisualsHidden(true);
                    SyncRiderToSeat();
                }

                if (t >= 1f)
                {
                    RestoreTransitionDrakeScale();
                    SetPlayerVisualsHidden(true);
                    SyncRiderToSeat();
                    _transitionState = SymbiosisTransitionState.None;
                    _transitionStartedAt = 0f;
                    string controls = IsSerpentForm
                        ? "W schnell schwimmen | S langsam rueckwaerts | A/D lenken | Shift sprinten | LMB Biss | RMB Bissstoss | MMB Schwanzfeger | Halskette erneut benutzen = zurueck"
                        : IsTrollForm
                            ? "Maus zielen | W/S vor/zurueck | A/D seitwaerts | Shift sprinten | E Baumstamm aufnehmen | LMB/RMB/MMB Angriffe | F6 Ego/Third | Halskette erneut benutzen = zurueck"
                            : IsWerewolfForm
                                ? "W/S vor/zurueck | A/D seitwaerts | Shift High-Speed | SPACE Sprung | LMB Klaue | MMB Sprungangriff | RMB halten + LMB Blutjagd (bis 5 Ziele) | F6 Ego/Third | Halskette erneut benutzen = zurueck"
                                : "Maus zielen | W folgt Blickrichtung | LMB/RMB/MMB Angriffe | Space hoch | Strg/C runter | Shift+W Sturzflug | Halskette erneut benutzen = zurueck";
                    _status?.Invoke(ActiveFormDisplayName + (_transitionUsesTemporaryAvatar ? "-Symbiose verbunden | " : "-Symbiose aktiv | ") + controls);
                    _debugLog?.Invoke(_activeFormId + " transformation enter completed; controls unlocked.");
                }
                return;
            }

            if (_transitionState == SymbiosisTransitionState.Exiting)
            {
                if (_transitionDrakeScaleCaptured && _transitionDrakeVisual != null)
                {
                    float morph = Smooth01(Mathf.InverseLerp(0.18f, 0.92f, t));
                    float factor = Mathf.Lerp(1f, 0.12f, morph);
                    try { _transitionDrakeVisual.localScale = _transitionDrakeOriginalScale * factor; }
                    catch { }
                }

                if (t >= 0.43f && !_transitionHandoffFxPlayed)
                {
                    _transitionHandoffFxPlayed = true;
                    PlayTransformationHandoffFx(false);
                }

                if (t >= 0.48f && _playerVisualsHidden)
                    SetPlayerVisualsHidden(false);

                if (t >= 1f)
                {
                    string message = _pendingDismountMessage;
                    _pendingDismountMessage = string.Empty;
                    RestoreTransitionDrakeScale();
                    _transitionState = SymbiosisTransitionState.None;
                    _transitionStartedAt = 0f;
                    if (IsTrollForm) DropHeldTrollLog();
                    ForceDismount(true);
                    if (!string.IsNullOrEmpty(message)) _status?.Invoke(message);
                    _debugLog?.Invoke("Drake transformation exit completed.");
                }
            }
        }

        private static Vector3 GetTransformationFxPosition(bool entering, bool handoff)
        {
            try
            {
                if (entering)
                {
                    if (!handoff && _rider != null)
                        return _rider.transform.position + Vector3.up * 1.05f;
                    if (_mountedDrake != null)
                        return _mountedDrake.transform.position + Vector3.up * Mathf.Max(0.75f, _mountedDrake.GetRadius() * 0.55f);
                }
                else
                {
                    if (!handoff && _mountedDrake != null)
                        return _mountedDrake.transform.position + Vector3.up * Mathf.Max(0.75f, _mountedDrake.GetRadius() * 0.55f);
                    return _exitPlayerAnchorPosition + Vector3.up * 1.05f;
                }
            }
            catch { }

            return _rider != null ? _rider.transform.position + Vector3.up : Vector3.zero;
        }

        private static void PlayTransformationChargeFx(bool entering)
        {
            try
            {
                Vector3 position = GetTransformationFxPosition(entering, false);
                SpawnTransformationParticles(position, false, entering);
                PlayTransformationSound(position, false, entering);
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Drake transformation charge FX failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void PlayTransformationHandoffFx(bool entering)
        {
            try
            {
                Vector3 position = GetTransformationFxPosition(entering, true);
                SpawnTransformationParticles(position, true, entering);
                PlayTransformationSound(position, true, entering);
                TrySpawnValheimTransformationFx(position);
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Drake transformation handoff FX failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static Material GetTransformationParticleMaterial()
        {
            if (_transformationParticleMaterial != null) return _transformationParticleMaterial;

            Shader shader = Shader.Find("Particles/Standard Unlit");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) return null;

            _transformationParticleMaterial = new Material(shader)
            {
                name = "VW_DrakeTransformationParticleMaterial",
                hideFlags = HideFlags.HideAndDontSave
            };

            try
            {
                const int size = 32;
                _transformationParticleTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    name = "VW_DrakeTransformationParticleTexture",
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                Color[] pixels = new Color[size * size];
                for (int y = 0; y < size; ++y)
                {
                    for (int x = 0; x < size; ++x)
                    {
                        float nx = (x + 0.5f) / size * 2f - 1f;
                        float ny = (y + 0.5f) / size * 2f - 1f;
                        float radius = Mathf.Sqrt(nx * nx + ny * ny);
                        float alpha = Mathf.Clamp01(1f - radius);
                        alpha = alpha * alpha * (3f - 2f * alpha);
                        pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                    }
                }
                _transformationParticleTexture.SetPixels(pixels);
                _transformationParticleTexture.Apply(false, true);
                _transformationParticleMaterial.mainTexture = _transformationParticleTexture;
            }
            catch { }

            return _transformationParticleMaterial;
        }

        private static void SpawnTransformationParticles(Vector3 position, bool majorBurst, bool entering)
        {
            GameObject root = new GameObject(majorBurst ? "VW_DrakeTransformationBurst" : "VW_DrakeTransformationCharge");
            root.transform.position = position;
            root.hideFlags = HideFlags.HideAndDontSave;

            ParticleSystem particles = root.AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = particles.main;
            main.loop = false;
            main.duration = majorBurst ? 0.48f : 0.62f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = majorBurst ? 120 : 70;
            main.startLifetime = majorBurst
                ? new ParticleSystem.MinMaxCurve(0.28f, 0.72f)
                : new ParticleSystem.MinMaxCurve(0.38f, 0.9f);
            main.startSpeed = majorBurst
                ? new ParticleSystem.MinMaxCurve(3.0f, 6.2f)
                : new ParticleSystem.MinMaxCurve(0.7f, 2.2f);
            main.startSize = majorBurst
                ? new ParticleSystem.MinMaxCurve(0.055f, 0.19f)
                : new ParticleSystem.MinMaxCurve(0.035f, 0.12f);
            Color frostA = entering ? new Color(0.72f, 0.92f, 1f, 0.95f) : new Color(0.82f, 0.95f, 1f, 0.9f);
            Color frostB = new Color(1f, 1f, 1f, 0.72f);
            main.startColor = new ParticleSystem.MinMaxGradient(frostA, frostB);
            main.gravityModifier = majorBurst ? -0.03f : -0.08f;

            ParticleSystem.EmissionModule emission = particles.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[]
            {
                new ParticleSystem.Burst(0f, (short)(majorBurst ? 72 : 34))
            });

            ParticleSystem.ShapeModule shape = particles.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = majorBurst ? 0.58f : 0.36f;
            shape.radiusThickness = majorBurst ? 0.68f : 1f;

            ParticleSystem.ColorOverLifetimeModule colorOverLifetime = particles.colorOverLifetime;
            colorOverLifetime.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(new Color(0.62f, 0.88f, 1f), 0.55f),
                    new GradientColorKey(new Color(0.82f, 0.95f, 1f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.95f, 0.08f),
                    new GradientAlphaKey(0.78f, 0.58f),
                    new GradientAlphaKey(0f, 1f)
                });
            colorOverLifetime.color = gradient;

            ParticleSystemRenderer renderer = root.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.renderMode = majorBurst ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
                renderer.velocityScale = majorBurst ? 0.22f : 0f;
                renderer.lengthScale = majorBurst ? 1.55f : 1f;
                Material material = GetTransformationParticleMaterial();
                if (material != null) renderer.sharedMaterial = material;
            }

            particles.Play(true);
            UnityEngine.Object.Destroy(root, majorBurst ? 1.4f : 1.8f);
        }

        private static void TrySpawnValheimTransformationFx(Vector3 position)
        {
            ZNetScene scene = ZNetScene.instance;
            if (scene == null) return;

            string[] candidates = { "vfx_drake_hit", "vfx_ColdBall_hit", "vfx_drake_death" };
            for (int i = 0; i < candidates.Length; ++i)
            {
                GameObject prefab = null;
                try { prefab = scene.GetPrefab(candidates[i]); }
                catch { }
                if (prefab == null) continue;

                // Only borrow client-side VFX. Never instantiate another network object as decoration.
                if (prefab.GetComponent<ZNetView>() != null) continue;
                try
                {
                    GameObject fx = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
                    if (fx != null)
                    {
                        fx.name = "VW_DrakeTransformation_" + candidates[i];
                        UnityEngine.Object.Destroy(fx, 4f);
                        _debugLog?.Invoke($"Drake transformation borrowed Valheim VFX: {candidates[i]}.");
                        return;
                    }
                }
                catch { }
            }
        }

        private static void PlayTransformationSound(Vector3 position, bool snap, bool entering)
        {
            AudioClip clip = snap ? GetTransformationSnapClip() : GetTransformationWhooshClip();
            if (clip == null) return;

            GameObject audioObject = new GameObject(snap ? "VW_DrakeTransformationSnap" : "VW_DrakeTransformationWhoosh");
            audioObject.transform.position = position;
            audioObject.hideFlags = HideFlags.HideAndDontSave;
            AudioSource source = audioObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.volume = snap ? 0.38f : 0.28f;
            source.pitch = entering ? 1f : 0.9f;
            source.clip = clip;
            source.Play();
            UnityEngine.Object.Destroy(audioObject, Mathf.Max(1f, clip.length / Mathf.Max(0.2f, source.pitch) + 0.15f));
        }

        private static AudioClip GetTransformationWhooshClip()
        {
            if (_transformationWhooshClip != null) return _transformationWhooshClip;
            _transformationWhooshClip = CreateTransformationClip("VW_DrakeTransformationWhoosh", 0.78f, false);
            return _transformationWhooshClip;
        }

        private static AudioClip GetTransformationSnapClip()
        {
            if (_transformationSnapClip != null) return _transformationSnapClip;
            _transformationSnapClip = CreateTransformationClip("VW_DrakeTransformationSnap", 0.34f, true);
            return _transformationSnapClip;
        }

        private static AudioClip CreateTransformationClip(string name, float duration, bool snap)
        {
            const int sampleRate = 44100;
            int sampleCount = Mathf.Max(64, Mathf.RoundToInt(sampleRate * duration));
            AudioClip clip = AudioClip.Create(name, sampleCount, 1, sampleRate, false);
            if (clip == null) return null;

            float[] data = new float[sampleCount];
            System.Random random = new System.Random(snap ? 912731 : 491153);
            float filteredNoise = 0f;
            double phase = 0.0;
            for (int i = 0; i < sampleCount; ++i)
            {
                float t = i / (float)(sampleCount - 1);
                float envelope;
                if (snap)
                    envelope = Mathf.Exp(-t * 8.5f) * Mathf.Clamp01(t * 45f);
                else
                    envelope = Mathf.Pow(Mathf.Sin(Mathf.PI * t), 1.35f);

                float noise = (float)(random.NextDouble() * 2.0 - 1.0);
                filteredNoise += (noise - filteredNoise) * (snap ? 0.24f : 0.075f);
                float frequency = snap ? Mathf.Lerp(980f, 430f, t) : Mathf.Lerp(170f, 760f, t * t);
                phase += Math.PI * 2.0 * frequency / sampleRate;
                float tone = Mathf.Sin((float)phase);
                float overtone = Mathf.Sin((float)(phase * 2.013)) * 0.24f;
                float mixed = snap
                    ? tone * 0.56f + overtone * 0.24f + filteredNoise * 0.34f
                    : filteredNoise * 0.72f + tone * 0.23f + overtone * 0.08f;
                data[i] = Mathf.Clamp(mixed * envelope * (snap ? 0.52f : 0.34f), -1f, 1f);
            }

            clip.SetData(data, 0);
            return clip;
        }

        private static float Smooth01(float value)
        {
            value = Mathf.Clamp01(value);
            return value * value * (3f - 2f * value);
        }

        private static void Dismount(string message, bool animate = true)
        {
            Character drake = _mountedDrake;
            Player rider = _rider;
            if (drake == null || rider == null)
            {
                ForceDismount(false);
                return;
            }

            if (animate && _transitionState != SymbiosisTransitionState.Exiting && Camera.main != null)
            {
                BeginExitTransformation(message);
                return;
            }

            if (animate && _transitionState == SymbiosisTransitionState.Exiting)
                return;

            CompleteImmediateDismount(message);
        }

        private static void BeginExitTransformation(string message)
        {
            Character drake = _mountedDrake;
            Player rider = _rider;
            if (drake == null || rider == null)
            {
                CompleteImmediateDismount(message);
                return;
            }

            Vector3 safeExit = rider.transform.position;
            try { safeExit = ResolveSafeDismountPosition(drake, rider); }
            catch { }

            SetPlayerVisualsHidden(true);
            _exitPlayerAnchorPosition = safeExit;
            Vector3 forward = drake.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) forward = rider.transform.forward;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            _exitPlayerAnchorRotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
            HoldRiderAt(_exitPlayerAnchorPosition, _exitPlayerAnchorRotation);
            ResetPlayerFallState(rider);

            try
            {
                Camera camera = Camera.main;
                _transitionCameraStartPosition = camera != null ? camera.transform.position : GetDrakeEyePosition();
                _transitionCameraStartRotation = camera != null ? camera.transform.rotation : Quaternion.Euler(_cameraPitch, _cameraYaw, 0f);
            }
            catch
            {
                _transitionCameraStartPosition = GetDrakeEyePosition();
                _transitionCameraStartRotation = Quaternion.Euler(_cameraPitch, _cameraYaw, 0f);
            }

            bool temporaryAvatar = _temporarySymbiosisAvatar != null && drake.gameObject == _temporarySymbiosisAvatar;
            CaptureTransitionDrakeScale(drake, temporaryAvatar, false);

            _pendingDismountMessage = message ?? string.Empty;
            _transitionState = SymbiosisTransitionState.Exiting;
            _transitionStartedAt = Time.unscaledTime;
            _transitionHandoffFxPlayed = false;
            PlayTransformationChargeFx(false);
            _playerDamageImmuneUntil = Time.time + 5f;
            _status?.Invoke(ActiveFormDisplayName + "-Symbiose loest sich...");
            _debugLog?.Invoke($"{ActiveFormDisplayName} transformation exit started (duration={TransformationDuration:0.00}s).");
        }

        private static void CompleteImmediateDismount(string message)
        {
            Character drake = _mountedDrake;
            Player rider = _rider;
            Vector3 safeExit = rider != null ? rider.transform.position : Vector3.zero;

            if (drake != null && rider != null)
            {
                try { safeExit = ResolveSafeDismountPosition(drake, rider); }
                catch { }
            }

            if (rider != null)
            {
                try
                {
                    rider.transform.position = safeExit;
                    Rigidbody body = rider.GetComponent<Rigidbody>();
                    if (body != null)
                    {
                        body.velocity = Vector3.zero;
                        body.angularVelocity = Vector3.zero;
                    }
                    ResetPlayerFallState(rider);
                }
                catch { }
            }

            _playerDamageImmuneUntil = Time.time + 5f;
            if (IsTrollForm) DropHeldTrollLog();
            ForceDismount(true);

            if (!string.IsNullOrEmpty(message))
                _status?.Invoke(message);
        }

        private static Vector3 ResolveSafeDismountPosition(Character drake, Player rider, bool preferEntryFallback = false)
        {
            if (IsSerpentForm && drake != null)
            {
                if (preferEntryFallback && _entryPlayerAnchorPosition.sqrMagnitude > 0.001f)
                {
                    _debugLog?.Invoke("World exit during Sea Serpent symbiosis: returning Player to the pre-symbiosis water anchor before save.");
                    return _entryPlayerAnchorPosition;
                }

                // Leaving a water form should return the human to the surface next to the Serpent,
                // never to a sea-floor ray hit. The entry waterline is stable for the whole session.
                Vector3 side = drake.transform.right;
                side.y = 0f;
                if (side.sqrMagnitude < 0.01f) side = Vector3.right;
                side.Normalize();
                Vector3 waterExit = drake.transform.position + side * Mathf.Max(1.2f, drake.GetRadius() * 0.55f);
                waterExit.y = _serpentSurfaceY + 0.15f;
                return waterExit;
            }

            Vector3 origin = drake.transform.position + Vector3.up * 3f;
            RaycastHit[] hits = Physics.RaycastAll(
                origin,
                Vector3.down,
                5000f,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);

            float nearest = float.MaxValue;
            Vector3 ground = Vector3.zero;
            bool found = false;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider == null) continue;
                Transform t = collider.transform;
                if (t == drake.transform || t.IsChildOf(drake.transform)) continue;
                if (t == rider.transform || t.IsChildOf(rider.transform)) continue;
                if (hits[i].normal.y < 0.35f) continue;
                // A ray can hit the sea floor hundreds of metres below a flying Drake. Treat that
                // as "no nearby landing" instead of teleporting the human body to the ocean floor.
                if (hits[i].distance > MaxSafeDismountDrop) continue;
                if (hits[i].distance < nearest)
                {
                    nearest = hits[i].distance;
                    ground = hits[i].point;
                    found = true;
                }
            }

            if (found)
            {
                Vector3 side = drake.transform.right;
                side.y = 0f;
                if (side.sqrMagnitude < 0.01f) side = Vector3.right;
                side.Normalize();
                return ground + side * 0.7f + Vector3.up * 0.35f;
            }

            if (preferEntryFallback && _entryPlayerAnchorPosition.sqrMagnitude > 0.001f)
            {
                _debugLog?.Invoke("No nearby safe ground below Drake during world exit; returning real Player to the pre-symbiosis anchor before save.");
                return _entryPlayerAnchorPosition;
            }

            Vector3 fallbackSide = drake.transform.right;
            fallbackSide.y = 0f;
            if (fallbackSide.sqrMagnitude < 0.01f) fallbackSide = Vector3.right;
            fallbackSide.Normalize();
            return drake.transform.position
                + fallbackSide * Mathf.Max(1.6f, drake.GetRadius() + 0.8f)
                + Vector3.up * 0.25f;
        }

        private static void ResetPlayerFallState(Player rider)
        {
            if (rider == null) return;
            try
            {
                FieldInfo field = typeof(Character).GetField("m_maxAirAltitude", InstanceFlags)
                    ?? typeof(Player).GetField("m_maxAirAltitude", InstanceFlags);
                if (field != null && field.FieldType == typeof(float))
                    field.SetValue(rider, rider.transform.position.y);
            }
            catch { }
        }

        private static void ForceDismount(bool stopDrake)
        {
            Character drake = _mountedDrake;
            Player rider = _rider;

            if (rider != null)
            {
                SaveSymbiosisCameraMode(rider, _drakeEgoActive);
                SetPlayerSymbiosisNetworkState(rider, false);
                ClearCrashRecoveryAnchor(rider);
            }

            RestoreTransitionDrakeScale();
            RestoreDrakeVisualsAfterEgoRender();
            RestorePlayerVisuals();
            RestoreCameraState();
            RestoreExternalEgoCameraState();
            RestoreSmoothFlightBodyInterpolation();

            _mountedDrake = null;
            _rider = null;
            _cachedMountedBodyOwner = null;
            _cachedMountedBody = null;
            _cachedMountedViewOwner = null;
            _cachedMountedView = null;
            _cachedRiderBodyOwner = null;
            _cachedRiderBody = null;
            _activeFormId = DrakeFormId;
            _activeBoundLevel = 1;
            ResetTrollLogState(false);
            ResetWerewolfCombo(true);
            _werewolfMovementJumpQueuedUntil = 0f;
            _nextWerewolfMovementJumpAt = 0f;
            // v1.5.0: Form combat cooldowns are intentionally NOT reset on dismount/form switch.
            // Direct switching must not be usable to bypass Troll/Serpent ability cooldowns; all
            // form cooldowns now remain time-based across exit/re-entry just like Drake and Blood Hunt.
            _werewolfJumpBoostUntil = 0f;
            _werewolfPounceUntil = 0f;
            _werewolfPounceVelocity = Vector3.zero;
            _serpentBiteLungeUntil = 0f;
            _serpentBiteLungeVelocity = Vector3.zero;
            _werewolfVisualRoot = null;
            _werewolfVisualBaselineCaptured = false;
            _werewolfVisualLocalBaseline = Quaternion.identity;
            _werewolfDesiredHorizontalVelocity = Vector3.zero;
            _werewolfDesiredFacingRotation = Quaternion.identity;
            _werewolfAuthoritativeMoveActive = false;
            _werewolfNativeForwardMoveActive = false;
            _werewolfAnimForwardInput = 0f;
            _werewolfAnimStrafeInput = 0f;
            _werewolfAnimFast = false;
            _werewolfAnimatorParamsReported = false;
            _werewolfSignedLocomotionReported = false;
            _werewolfAnimatorParamCacheReady = false;
            _werewolfAnimForwardHash = 0;
            _werewolfAnimSideHash = 0;
            _werewolfAnimTurnHash = 0;
            try
            {
                if (_drakeBankVisualRoot != null)
                {
                    _drakeBankVisualRoot.transform.localRotation = _drakeBankBaseLocalRotation;
                    _drakeBankVisualRoot.transform.localPosition = _drakeBankBaseLocalPosition;
                }
            }
            catch { }
            _drakeBankVisualRoot = null;
            _drakeBankBaseLocalRotation = Quaternion.identity;
            _drakeBankBaseLocalPosition = Vector3.zero;
            _drakeVisualBankDegrees = 0f;
            _drakeVisualWingPitchDegrees = 0f;
            _drakeVisualWingLift = 0f;
            try
            {
                if (_serpentVisualWaveInitialized && _serpentVisualRoot != null)
                    _serpentVisualRoot.transform.localRotation = _serpentVisualBaseLocalRotation;
            }
            catch { }
            try
            {
                if (_serpentWaveBones != null && _serpentWaveBoneBaseRotations != null)
                {
                    int restoreCount = Math.Min(_serpentWaveBones.Length, _serpentWaveBoneBaseRotations.Length);
                    for (int i = 0; i < restoreCount; i++)
                        if (_serpentWaveBones[i] != null) _serpentWaveBones[i].localRotation = _serpentWaveBoneBaseRotations[i];
                }
            }
            catch { }
            _serpentVisualRoot = null;
            _serpentVisualBaseLocalRotation = Quaternion.identity;
            _serpentWaveBones = Array.Empty<Transform>();
            _serpentWaveBoneBaseRotations = Array.Empty<Quaternion>();
            _serpentVisualWaveInitialized = false;
            _serpentSwimPhase = 0f;
            _serpentVisualPitch = 0f;
            _serpentSurfaceY = 0f;
            _serpentDepth = 0f;
            _seat = null;
            _mountedAtFrame = -1;
            _drakeEgoActive = false;
            RestoreEgoHeadMouseLook();
            _drakeEgoAnchor = null;
            _egoHeadLookTransform = null;
            _stableEgoOffsetInitialized = false;
            _cameraPoseInitialized = false;
            _chaseFocusLocal = Vector3.zero;
            _chaseDistance = 8.5f;
            _chaseHeight = 1.75f;
            _drakeEgoRenderers = Array.Empty<Renderer>();
            _drakeEgoRendererStates = Array.Empty<bool>();
            _drakeVisualHiddenForRender = false;
            _thirdCameraSmoothInitialized = false;
            _thirdCameraSmoothFrame = -1;
            _thirdChaseYawInitialized = false;
            _thirdChaseYawVelocity = 0f;
            ResetFlightMoveSmoothing();
            _wingbeatSmoothInitialized = false;
            _smoothedWingbeatMeters = 0f;
            _manualFlightInputActive = false;
            _idleHoldActive = false;
            _idleHoldPosition = Vector3.zero;
            _idleHoldRotation = Quaternion.identity;
            _transitionState = SymbiosisTransitionState.None;
            _transitionStartedAt = 0f;
            _nextActiveIntegrityCheckAt = 0f;
            _pendingDismountMessage = string.Empty;
            _transitionUsesTemporaryAvatar = false;
            _transitionHandoffFxPlayed = false;
            _entryPlayerAnchorPosition = Vector3.zero;
            _entryPlayerAnchorRotation = Quaternion.identity;
            _exitPlayerAnchorPosition = Vector3.zero;
            _exitPlayerAnchorRotation = Quaternion.identity;
            _humanCameraRigCaptured = false;

            if (stopDrake && drake != null)
            {
                try
                {
                    drake.SetMoveDir(Vector3.zero);
                    drake.SetRun(false);
                    drake.SetWalk(false);
                }
                catch { }
            }

            DestroyTemporarySymbiosisAvatar();
        }

        private static void ResetTransientSessionResidue(Player player, bool cleanupTaggedAvatars)
        {
            ResetPendingTrollImpacts();
            ResetEgoFineTuneHold();
            _werewolfMovementJumpQueuedUntil = 0f;
            _nextWerewolfMovementJumpAt = 0f;
            _werewolfJumpBoostUntil = 0f;
            _werewolfPounceUntil = 0f;
            _werewolfPounceVelocity = Vector3.zero;
            _serpentBiteLungeUntil = 0f;
            _serpentBiteLungeVelocity = Vector3.zero;
            _playerDamageImmuneUntil = 0f;
            _forwardingSymbiosisDamageToPlayer = false;
            _menuCameraLocked = false;
            _lastOwnedCameraPoseValid = false;
            _manualFlightInputActive = false;
            _idleHoldActive = false;
            _transitionState = SymbiosisTransitionState.None;
            _transitionStartedAt = 0f;
            _pendingDismountMessage = string.Empty;

            if (!Active)
            {
                RestoreDrakeVisualsAfterEgoRender();
                RestorePlayerVisuals();
                RestoreCameraState();
                RestoreExternalEgoCameraState();
                RestoreSmoothFlightBodyInterpolation();
                DestroyTemporarySymbiosisAvatar();
                if (cleanupTaggedAvatars && player != null) CleanupTaggedTemporaryAvatars(player);
                if (player != null) SetPlayerSymbiosisNetworkState(player, false);
            }
        }

        internal static void EmergencyRelease(string message)
        {
            try
            {
                ResetPendingTrollImpacts();
                _werewolfPounceUntil = 0f;
                _werewolfPounceVelocity = Vector3.zero;
                _playerDamageImmuneUntil = 0f;
                _forwardingSymbiosisDamageToPlayer = false;
                if (Active)
                {
                    ForceDismount(false);
                    if (!string.IsNullOrWhiteSpace(message)) _status?.Invoke(message);
                }
                else
                {
                    DestroyTemporarySymbiosisAvatar();
                }
            }
            catch
            {
                // Last-resort safety path: never let recovery itself create an Update exception loop.
                try { DestroyTemporarySymbiosisAvatar(); } catch { }
            }
        }

        internal static void PrepareForWorldExit()
        {
            Player localPlayer = Player.m_localPlayer;
            ResetTransientSessionResidue(localPlayer, false);
            if (!Active)
            {
                // v4.3.0: world exit can happen after an interrupted transition has already made
                // Active false. Still purge tagged temporary bodies and published network state so
                // the next world/session always starts clean.
                if (localPlayer != null) CleanupTaggedTemporaryAvatars(localPlayer);
                DestroyTemporarySymbiosisAvatar();
                return;
            }

            Player rider = _rider;
            Character drake = _mountedDrake;
            if (rider != null) SaveSymbiosisCameraMode(rider, _drakeEgoActive);

            // Never save the hidden Player in the middle of a temporary Drake body. Move the real
            // Player to the nearest safe ground point before the normal profile/world save runs.
            if (rider != null && drake != null && !rider.IsDead())
            {
                try
                {
                    Vector3 safeExit = ResolveSafeDismountPosition(drake, rider, true);
                    rider.transform.position = safeExit;
                    rider.transform.rotation = Quaternion.Euler(0f, drake.transform.eulerAngles.y, 0f);
                    Rigidbody body = rider.GetComponent<Rigidbody>();
                    if (body != null)
                    {
                        body.position = safeExit;
                        body.velocity = Vector3.zero;
                        body.angularVelocity = Vector3.zero;
                    }
                    ResetPlayerFallState(rider);
                }
                catch { }
            }

            _playerDamageImmuneUntil = 0f;
            ForceDismount(true);
            _debugLog?.Invoke("Drake symbiosis closed before world/profile exit; temporary avatar removed before save.");
        }

        private static void ConfigureTemporarySymbiosisAvatar(ZNetView nview, Player player, string prefabName, int level)
        {
            if (nview == null || !nview.IsValid()) return;
            try
            {
                ZDO zdo = nview.GetZDO();
                if (zdo == null) return;
                zdo.Set(TemporaryAvatarMarkerKey, true);
                zdo.Set(TemporaryAvatarOwnerKey, player != null ? player.GetPlayerID() : 0L);
                zdo.Set(TemporaryAvatarFormKey, (_activeFormId ?? DrakeFormId).ToLowerInvariant());
                zdo.Set(TemporaryAvatarPrefabKey, string.IsNullOrWhiteSpace(prefabName) ? "Hatchling" : prefabName);
                zdo.Set(TemporaryAvatarLevelKey, Math.Max(1, level));
                if (ZdoSetPersistentMethod != null)
                    ZdoSetPersistentMethod.Invoke(zdo, new object[] { false });
                _debugLog?.Invoke($"Temporary {_activeFormId} symbiosis avatar marked non-persistent; prefab={prefabName}, level={Math.Max(1, level)}.");
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Temporary Drake non-persistent marker failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string CrashRecoveryKey(Player player, string suffix)
        {
            long id = player != null ? player.GetPlayerID() : 0L;
            return CrashRecoveryPrefPrefix + id + "_" + suffix;
        }

        private static void SaveCrashRecoveryAnchor(Player player, Vector3 position, Quaternion rotation)
        {
            if (player == null || player.GetPlayerID() == 0L) return;
            try
            {
                PlayerPrefs.SetFloat(CrashRecoveryKey(player, "x"), position.x);
                PlayerPrefs.SetFloat(CrashRecoveryKey(player, "y"), position.y);
                PlayerPrefs.SetFloat(CrashRecoveryKey(player, "z"), position.z);
                PlayerPrefs.SetFloat(CrashRecoveryKey(player, "yaw"), rotation.eulerAngles.y);
                PlayerPrefs.SetInt(CrashRecoveryKey(player, "active"), 1);
                PlayerPrefs.Save();
            }
            catch { }
        }

        private static void ClearCrashRecoveryAnchor(Player player)
        {
            if (player == null || player.GetPlayerID() == 0L) return;
            try
            {
                PlayerPrefs.SetInt(CrashRecoveryKey(player, "active"), 0);
                PlayerPrefs.Save();
            }
            catch { }
        }

        private static bool TryRecoverFromUncleanSymbiosis(Player player)
        {
            if (player == null || player.GetPlayerID() == 0L) return false;
            try
            {
                if (PlayerPrefs.GetInt(CrashRecoveryKey(player, "active"), 0) == 0) return false;

                Vector3 current = player.transform.position;
                Vector3 recovery = new Vector3(
                    PlayerPrefs.GetFloat(CrashRecoveryKey(player, "x"), current.x),
                    PlayerPrefs.GetFloat(CrashRecoveryKey(player, "y"), current.y),
                    PlayerPrefs.GetFloat(CrashRecoveryKey(player, "z"), current.z));
                float yaw = PlayerPrefs.GetFloat(CrashRecoveryKey(player, "yaw"), player.transform.eulerAngles.y);
                Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);

                player.transform.position = recovery;
                player.transform.rotation = rotation;
                Rigidbody body = player.GetComponent<Rigidbody>();
                if (body != null)
                {
                    body.position = recovery;
                    body.rotation = rotation;
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
                ResetPlayerFallState(player);
                SetPlayerSymbiosisNetworkState(player, false);
                ClearCrashRecoveryAnchor(player);
                _debugLog?.Invoke($"Recovered Player from an unclean Drake symbiosis shutdown to pre-symbiosis anchor {recovery}.");
                _status?.Invoke("Drake-Symbiose nach unsauberem Spielende sicher zurueckgesetzt.");
                return true;
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Drake crash-recovery anchor failed safely: {ex.GetType().Name}: {ex.Message}");
                ClearCrashRecoveryAnchor(player);
                return false;
            }
        }

        private static bool SetPlayerSymbiosisNetworkState(Player player, bool active)
        {
            if (player == null) return false;
            try
            {
                ZNetView view = player.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo == null) return false;

                zdo.Set(PlayerSymbiosisActiveKey, active);
                zdo.Set(PlayerSymbiosisFormKey, active ? (_activeFormId ?? DrakeFormId).ToLowerInvariant() : string.Empty);
                zdo.Set(PlayerSymbiosisRevisionKey, 5);
                return true;
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Drake multiplayer player-state publish failed safely: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static bool IsRemotePlayerPublishedInSymbiosis(Player player)
        {
            if (player == null) return false;
            try
            {
                ZNetView view = player.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo == null || !zdo.GetBool(PlayerSymbiosisActiveKey, false)) return false;
                string form = zdo.GetString(PlayerSymbiosisFormKey, string.Empty);
                return string.IsNullOrEmpty(form) ||
                       string.Equals(form, "drake", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(form, "serpent", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(form, "troll", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(form, "werewolf", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static RemotePlayerVisualState CaptureRemotePlayerVisualState(Player player)
        {
            RemotePlayerVisualState state = new RemotePlayerVisualState();
            if (player == null) return state;
            try
            {
                Renderer[] renderers = player.GetComponentsInChildren<Renderer>(true) ?? Array.Empty<Renderer>();
                state.Renderers = renderers;
                state.EnabledStates = new bool[renderers.Length];
                for (int i = 0; i < renderers.Length; ++i)
                    state.EnabledStates[i] = renderers[i] != null && renderers[i].enabled;

                Collider[] colliders = player.GetComponentsInChildren<Collider>(true) ?? Array.Empty<Collider>();
                state.Colliders = colliders;
                state.ColliderStates = new bool[colliders.Length];
                for (int i = 0; i < colliders.Length; ++i)
                    state.ColliderStates[i] = colliders[i] != null && colliders[i].enabled;
            }
            catch { }
            return state;
        }

        private static void SetRemotePlayerVisualState(RemotePlayerVisualState state, bool hidden)
        {
            if (state == null) return;
            int count = Math.Min(state.Renderers?.Length ?? 0, state.EnabledStates?.Length ?? 0);
            for (int i = 0; i < count; ++i)
            {
                Renderer renderer = state.Renderers[i];
                if (renderer == null) continue;
                try { renderer.enabled = hidden ? false : state.EnabledStates[i]; }
                catch { }
            }

            int colliderCount = Math.Min(state.Colliders?.Length ?? 0, state.ColliderStates?.Length ?? 0);
            for (int i = 0; i < colliderCount; ++i)
            {
                Collider collider = state.Colliders[i];
                if (collider == null) continue;
                try { collider.enabled = hidden ? false : state.ColliderStates[i]; }
                catch { }
            }
        }

        private static void RestoreAllRemoteSymbiosisVisuals()
        {
            try
            {
                foreach (KeyValuePair<Player, RemotePlayerVisualState> pair in _remoteSymbiosisVisuals)
                    SetRemotePlayerVisualState(pair.Value, false);
            }
            catch { }
            _remoteSymbiosisVisuals.Clear();
        }

        private static void UpdateRemoteSymbiosisVisuals()
        {
            if (Time.unscaledTime < _nextRemoteVisibilityRefreshAt) return;
            // Idle/solo multiplayer probing is deliberately slow. Once a remote form is actually
            // active, tighten the cadence for a responsive body handoff. Reuse scratch lists to
            // avoid per-refresh GC allocations.
            _nextRemoteVisibilityRefreshAt = Time.unscaledTime + (_remoteSymbiosisVisuals.Count > 0 ? 0.20f : 2.0f);

            try
            {
                _remoteVisibilitySeen.Clear();
                foreach (Character candidate in Character.GetAllCharacters())
                {
                    Player remote = candidate as Player;
                    if (remote == null || remote == Player.m_localPlayer) continue;
                    _remoteVisibilitySeen.Add(remote);

                    bool active = IsRemotePlayerPublishedInSymbiosis(remote);
                    if (active)
                    {
                        if (!_remoteSymbiosisVisuals.TryGetValue(remote, out RemotePlayerVisualState state))
                        {
                            state = CaptureRemotePlayerVisualState(remote);
                            _remoteSymbiosisVisuals[remote] = state;
                            _debugLog?.Invoke($"Remote symbiosis visual handoff detected: player={remote.name}.");
                        }
                        SetRemotePlayerVisualState(state, true);
                    }
                    else if (_remoteSymbiosisVisuals.TryGetValue(remote, out RemotePlayerVisualState previous))
                    {
                        SetRemotePlayerVisualState(previous, false);
                        _remoteSymbiosisVisuals.Remove(remote);
                    }
                }

                if (_remoteSymbiosisVisuals.Count > 0)
                {
                    _remoteVisibilityStale.Clear();
                    foreach (KeyValuePair<Player, RemotePlayerVisualState> pair in _remoteSymbiosisVisuals)
                    {
                        Player remote = pair.Key;
                        if (remote == null || !_remoteVisibilitySeen.Contains(remote) || !IsRemotePlayerPublishedInSymbiosis(remote))
                        {
                            SetRemotePlayerVisualState(pair.Value, false);
                            _remoteVisibilityStale.Add(remote);
                        }
                    }
                    for (int i = 0; i < _remoteVisibilityStale.Count; ++i)
                        _remoteSymbiosisVisuals.Remove(_remoteVisibilityStale[i]);
                    _remoteVisibilityStale.Clear();
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Remote symbiosis visibility refresh failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void RunSymbiosisHousekeeping()
        {
            UpdateRemoteSymbiosisVisuals();
            Player player = Player.m_localPlayer;

            // v3.8.0: while completely inactive and already handshaken, lifecycle recovery does not
            // need to run every rendered frame. Player-object changes still bypass this gate
            // immediately; the steady-state inactive path is sampled four times per second.
            if (!Active && player != null && player == _observedLocalPlayer)
            {
                long stablePlayerId = player.GetPlayerID();
                if (stablePlayerId != 0L && _inactiveRecoveryHandshakePlayerId == stablePlayerId)
                {
                    if (Time.unscaledTime < _nextInactiveHousekeepingAt) return;
                    _nextInactiveHousekeepingAt = Time.unscaledTime + 0.25f;
                }
            }

            // Respawn/world-change hardening: if Valheim replaces the local Player object, any
            // symbiosis still pointing at the old object is invalid and must be torn down before
            // the new Player can receive input/camera ownership. This also guarantees that a death
            // never auto-restores Drake form on respawn.
            if (player != _observedLocalPlayer)
            {
                if (Active && _rider != null && _rider != player)
                {
                    _debugLog?.Invoke("Local Player object changed while Drake symbiosis was active; clearing stale possession state.");
                    ForceDismount(false);
                }

                _observedLocalPlayer = player;
                _inactiveRecoveryHandshakePlayerId = 0L;
                _nextInactiveRecoveryCheckAt = 0f;
                _nextInactiveHousekeepingAt = 0f;
                _observedLocalPlayerDead = player != null && player.IsDead();
                _playerDamageImmuneUntil = 0f;
                _forwardingSymbiosisDamageToPlayer = false;

                // A previous death/scene transition can interrupt camera restoration between
                // frames. Re-applying these idempotent restores keeps respawn in normal human
                // camera mode until the necklace is explicitly activated again.
                if (!Active)
                {
                    ResetTransientSessionResidue(player, false);
                }

                if (player != null)
                {
                    if (!Active)
                    {
                        TryRecoverFromUncleanSymbiosis(player);
                        long initializedPlayerId = player.GetPlayerID();
                        if (initializedPlayerId != 0L && SetPlayerSymbiosisNetworkState(player, false))
                            _inactiveRecoveryHandshakePlayerId = initializedPlayerId;
                    }
                    CancelHiddenHumanAttack(player);
                    CleanupTaggedTemporaryAvatars(player);
                    _debugLog?.Invoke("Local Player lifecycle initialized/recovered; Drake symbiosis remains off until explicitly activated.");
                }
            }

            if (player == null) return;

            // Valheim can expose Player.m_localPlayer a few frames before GetPlayerID/ZNetView are
            // fully initialized. Retry crash recovery/network-state cleanup briefly while inactive
            // instead of relying only on the first Player-object observation.
            long inactivePlayerId = player.GetPlayerID();
            if (!Active && inactivePlayerId != 0L && _inactiveRecoveryHandshakePlayerId != inactivePlayerId &&
                Time.unscaledTime >= _nextInactiveRecoveryCheckAt)
            {
                _nextInactiveRecoveryCheckAt = Time.unscaledTime + 1.0f;
                TryRecoverFromUncleanSymbiosis(player);
                if (SetPlayerSymbiosisNetworkState(player, false))
                    _inactiveRecoveryHandshakePlayerId = inactivePlayerId;
            }

            bool playerDeadNow = player.IsDead();
            if (playerDeadNow && !_observedLocalPlayerDead && Active && _rider == player)
            {
                HandleRiderDeathDuringSymbiosis();
            }
            else if (!playerDeadNow && _observedLocalPlayerDead)
            {
                // Respawn completed. Clear every temporary protection/input residue but deliberately
                // do not recreate the Drake body. The player must press the necklace hotkey again.
                _playerDamageImmuneUntil = 0f;
                _forwardingSymbiosisDamageToPlayer = false;
                CancelHiddenHumanAttack(player);
                CleanupTaggedTemporaryAvatars(player);
                _menuCameraLocked = false;
                _lastOwnedCameraPoseValid = false;
                _debugLog?.Invoke("Player respawn detected; symbiosis state reset to normal human form.");
            }
            _observedLocalPlayerDead = playerDeadNow;

            if (Time.unscaledTime >= _nextTemporaryAvatarCleanupAt)
            {
                // Stale-avatar cleanup is important after interrupted lifecycle events, but a full
                // Character list scan every two seconds while simply playing as a human is wasteful.
                // Keep the old fast cadence only while a form is active; otherwise a ten-second
                // safety sweep is sufficient, with immediate sweeps still occurring on respawn/start.
                _nextTemporaryAvatarCleanupAt = Time.unscaledTime + (Active ? 2f : 20f);
                CleanupTaggedTemporaryAvatars(player);
            }

            // v0.1.41 and older could persist an untagged temporary Hatchling. v0.1.42 tried
            // to remove it only once, which was too early on some world loads: the Player existed
            // before the character/zone containing the stale Drake had streamed in.
            //
            // Capture the Player's initial load position and retry for a short startup window. The
            // old temporary avatar was spawned/followed directly at the Player, so restricting the
            // legacy purge to this small anchor radius avoids touching unrelated wild Drakes.
            long playerId = player.GetPlayerID();
            if (playerId != 0L)
            {
                if (_legacyCleanupPlayerId != playerId)
                {
                    _legacyCleanupPlayerId = playerId;
                    _legacyCleanupSpawnAnchor = player.transform.position;
                    _legacyCleanupAnchorCaptured = true;
                    _legacyCleanupWindowUntil = Time.unscaledTime + LegacyCleanupWindowSeconds;
                    _nextLegacyCleanupAt = Time.unscaledTime + 0.75f;
                    _legacyCleanupCompletedForSession = PlayerPrefs.GetInt(LegacyCleanupPrefPrefix + playerId, 0) != 0;
                }

                if (!_legacyCleanupCompletedForSession && _legacyCleanupAnchorCaptured)
                {
                    if (Time.unscaledTime <= _legacyCleanupWindowUntil && Time.unscaledTime >= _nextLegacyCleanupAt)
                    {
                        _nextLegacyCleanupAt = Time.unscaledTime + 1.5f;
                        if (CleanupLegacyTemporaryAvatarNearSpawn(player, _legacyCleanupSpawnAnchor))
                        {
                            PlayerPrefs.SetInt(LegacyCleanupPrefPrefix + playerId, 1);
                            PlayerPrefs.Save();
                            _legacyCleanupCompletedForSession = true;
                        }
                    }
                    else if (Time.unscaledTime > _legacyCleanupWindowUntil)
                    {
                        // Do not persist a false success. If the zone never loaded this session, retry
                        // again on the next game start instead of permanently suppressing cleanup.
                        _legacyCleanupCompletedForSession = true;
                        _debugLog?.Invoke("Legacy symbiosis Drake cleanup window ended without finding a stale avatar; will retry next session.");
                    }
                }
            }
        }

        private static void CleanupTaggedTemporaryAvatars(Player player)
        {
            long localPlayerId = player != null ? player.GetPlayerID() : 0L;
            foreach (Character candidate in Character.GetAllCharacters())
            {
                if (candidate == null || candidate is Player) continue;
                if (_temporarySymbiosisAvatar != null && candidate.gameObject == _temporarySymbiosisAvatar) continue;
                ZNetView view = candidate.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo == null || !zdo.GetBool(TemporaryAvatarMarkerKey, false)) continue;

                long owner = zdo.GetLong(TemporaryAvatarOwnerKey, 0L);
                if (owner != 0L && localPlayerId != 0L && owner != localPlayerId) continue;
                _debugLog?.Invoke($"Removing stale tagged symbiosis avatar: {candidate.name}.");
                DestroyTemporaryNetworkObject(candidate.gameObject);
            }
        }

        private static bool CleanupLegacyTemporaryAvatarNearSpawn(Player player, Vector3 spawnAnchor)
        {
            if (player == null) return false;
            Character best = null;
            float bestDistance = LegacyCleanupRadius;

            foreach (Character candidate in Character.GetAllCharacters())
            {
                if (candidate == null || candidate is Player || candidate.IsDead()) continue;
                if (_temporarySymbiosisAvatar != null && candidate.gameObject == _temporarySymbiosisAvatar) continue;
                if (!string.Equals(SpeciesRoleRegistry.NormalizePrefabName(candidate.gameObject.name), "Drake", StringComparison.OrdinalIgnoreCase)) continue;

                ZNetView view = candidate.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo == null) continue;

                // Real recruited/assigned Workers are persistent gameplay entities and must never be
                // considered legacy temporary bodies. Tagged new temporary avatars are handled by
                // CleanupTaggedTemporaryAvatars above.
                if (WorkerZdo.IsWorker(zdo)) continue;
                if (zdo.GetBool(TemporaryAvatarMarkerKey, false)) continue;
                // Legacy temporary avatars were switched to the Players faction. Wild Drakes are not.
                // Requiring this signature prevents the one-time migration from deleting an unrelated
                // vanilla Drake that merely happened to fly close to the player's startup position.
                if (!IsPlayersFaction(candidate)) continue;

                float distance = Vector3.Distance(spawnAnchor, candidate.transform.position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            if (best == null) return false;
            _debugLog?.Invoke($"Removing legacy pre-v0.1.42 symbiosis Drake at startup anchor (distance={bestDistance:0.00}m): {best.name}.");
            DestroyTemporaryNetworkObject(best.gameObject);
            return true;
        }

        private static bool IsPlayersFaction(Character character)
        {
            if (character == null || CharacterFactionField == null) return false;
            try
            {
                object value = CharacterFactionField.GetValue(character);
                return value != null && string.Equals(value.ToString(), "Players", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void DestroyTemporaryNetworkObject(GameObject avatar)
        {
            if (avatar == null) return;
            try
            {
                ZNetView view = avatar.GetComponent<ZNetView>();
                if (view != null && view.IsValid() && !view.IsOwner())
                {
                    MethodInfo claim = typeof(ZNetView).GetMethod("ClaimOwnership", InstanceFlags, null, Type.EmptyTypes, null);
                    claim?.Invoke(view, null);
                }
            }
            catch { }

            try
            {
                if (ZNetScene.instance != null) ZNetScene.instance.Destroy(avatar);
                else UnityEngine.Object.Destroy(avatar);
            }
            catch
            {
                try { UnityEngine.Object.Destroy(avatar); } catch { }
            }
        }

        private static void DestroyTemporarySymbiosisAvatar()
        {
            GameObject avatar = _temporarySymbiosisAvatar;
            _temporarySymbiosisAvatar = null;
            if (avatar == null) return;
            DestroyTemporaryNetworkObject(avatar);
        }

        private static void EnsureCameraCallbacks()
        {
            if (_cameraCallbacksRegistered) return;
            Camera.onPreCull += OnCameraPreCull;
            Camera.onPreRender += OnCameraPreRender;
            Camera.onPostRender += OnCameraPostRender;
            _cameraCallbacksRegistered = true;
        }

        private static void RemoveCameraCallbacks()
        {
            if (!_cameraCallbacksRegistered) return;
            Camera.onPreCull -= OnCameraPreCull;
            Camera.onPreRender -= OnCameraPreRender;
            Camera.onPostRender -= OnCameraPostRender;
            _cameraCallbacksRegistered = false;
            _cameraCallbacksMovedToTail = false;
            _cameraOverriddenThisRender = false;
        }


        private static void MoveCameraCallbacksToTail()
        {
            if (!_cameraCallbacksRegistered || _cameraCallbacksMovedToTail) return;
            try
            {
                // Workers is loaded before Valheim Ego in the user's profile. Unity invokes
                // Camera callbacks in subscription order, so Ego's onPreCull used to run after
                // Workers and overwrite the Drake pose again. Remove + re-add our callbacks at
                // symbiosis start so our final pose lock runs last for the active session.
                Camera.onPreCull -= OnCameraPreCull;
                Camera.onPreRender -= OnCameraPreRender;
                Camera.onPostRender -= OnCameraPostRender;
                Camera.onPreCull += OnCameraPreCull;
                Camera.onPreRender += OnCameraPreRender;
                Camera.onPostRender += OnCameraPostRender;
                _cameraCallbacksMovedToTail = true;
                _debugLog?.Invoke("Drake symbiosis camera callbacks moved to tail after external camera mods.");
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Drake camera callback reorder failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool ResolveInitialSymbiosisCameraMode(Player player)
        {
            // The transformation should mirror the perspective the human player is using NOW.
            // A persisted symbiosis mode is only a fallback for the rare case where the external
            // camera state cannot be inspected. This fixes the old behaviour where a stale saved
            // Third-Person value could override a player who had started the game in Ego.
            if (TryReadCurrentPlayerCameraMode(player, out bool currentMode, out string source))
            {
                _debugLog?.Invoke($"Drake symbiosis inherited current player camera mode from {source}: {(currentMode ? "Ego" : "Third Person")}");
                SaveSymbiosisCameraMode(player, currentMode);
                return currentMode;
            }

            if (TryLoadSymbiosisCameraMode(player, out bool savedMode))
            {
                _debugLog?.Invoke($"Drake symbiosis restored persisted camera mode fallback: {(savedMode ? "Ego" : "Third Person")}");
                return savedMode;
            }

            SaveSymbiosisCameraMode(player, false);
            return false;
        }

        private static bool TryReadCurrentPlayerCameraMode(Player player, out bool firstPerson, out string source)
        {
            firstPerson = false;
            source = string.Empty;

            try
            {
                // Valheim Ego is authoritative when installed. Read both the steady state and an
                // in-progress transition target so entering symbiosis during a camera blend keeps
                // the exact perspective the player requested.
                Type egoType = null;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    egoType = assembly.GetType("Valheim_Ego.FirstPersonPlugin", false);
                    if (egoType != null) break;
                }

                if (egoType != null)
                {
                    FieldInfo firstPersonField = egoType.GetField("FirstPersonEnabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (firstPersonField != null && firstPersonField.FieldType == typeof(bool))
                    {
                        firstPerson = (bool)firstPersonField.GetValue(null);

                        PropertyInfo instanceProperty = egoType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        object instance = instanceProperty?.GetValue(null, null);
                        if (instance != null)
                        {
                            FieldInfo transitionActiveField = egoType.GetField("transitionActive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            FieldInfo transitionTargetField = egoType.GetField("transitionToFirstPerson", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            bool transitionActive = transitionActiveField != null && transitionActiveField.FieldType == typeof(bool)
                                && (bool)transitionActiveField.GetValue(instance);
                            if (transitionActive && transitionTargetField != null && transitionTargetField.FieldType == typeof(bool))
                                firstPerson = (bool)transitionTargetField.GetValue(instance);
                        }

                        source = "Valheim Ego";
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Could not read Valheim Ego camera mode; trying camera-distance fallback: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                Camera camera = Camera.main;
                if (player != null && camera != null)
                {
                    Vector3 eyeApprox = player.transform.position + Vector3.up * 1.6f;
                    firstPerson = Vector3.Distance(camera.transform.position, eyeApprox) < 1.25f;
                    source = "camera distance";
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static string SymbiosisCameraPrefKey(Player player)
        {
            long id = player != null ? player.GetPlayerID() : 0L;
            return CameraModePrefPrefix + id;
        }

        private static bool TryLoadSymbiosisCameraMode(Player player, out bool ego)
        {
            ego = false;
            if (player == null || player.GetPlayerID() == 0L) return false;
            string key = SymbiosisCameraPrefKey(player);
            if (!PlayerPrefs.HasKey(key)) return false;
            ego = PlayerPrefs.GetInt(key, 0) != 0;
            return true;
        }

        private static void SaveSymbiosisCameraMode(Player player, bool ego)
        {
            if (player == null || player.GetPlayerID() == 0L) return;
            try
            {
                PlayerPrefs.SetInt(SymbiosisCameraPrefKey(player), ego ? 1 : 0);
                PlayerPrefs.Save();
            }
            catch { }
        }

        // v3.8.2: F6 is one global perspective choice for the human player and every
        // symbiosis form. Valheim Ego uses the same per-player preference key. Writing it
        // here prevents Ego's intentionally suspended runtime state (FirstPersonEnabled=false)
        // from being mistaken for a real switch to Third Person while a creature owns Camera.main.
        private static void SaveGlobalPerspective(Player player, bool ego)
        {
            if (player == null || player.GetPlayerID() == 0L) return;
            try
            {
                PlayerPrefs.SetInt(GlobalPerspectivePrefPrefix + player.GetPlayerID(), ego ? 1 : 0);
                PlayerPrefs.Save();
            }
            catch { }
        }

        private static void SuspendExternalEgoCameraState()
        {
            if (_egoCameraStateSuspended) return;
            try
            {
                Type egoType = null;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    egoType = assembly.GetType("Valheim_Ego.FirstPersonPlugin", false);
                    if (egoType != null) break;
                }
                if (egoType == null) return;

                // Disable the external F6 toggle while the Drake owns Camera.main.
                FieldInfo toggleField = egoType.GetField("ToggleKey", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                object entry = toggleField?.GetValue(null);
                if (entry != null)
                {
                    PropertyInfo valueProperty = entry.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                    if (valueProperty != null && valueProperty.CanRead && valueProperty.CanWrite)
                    {
                        _egoToggleKeyEntry = entry;
                        _egoToggleKeySavedValue = valueProperty.GetValue(entry, null);
                        _egoToggleKeyValueProperty = valueProperty;
                        valueProperty.SetValue(entry, KeyCode.None, null);
                    }
                }

                // IMPORTANT: disabling only the key is not enough. Valheim Ego keeps its LateUpdate
                // and Camera.onPreCull callbacks alive while FirstPersonEnabled/transitionActive are
                // true, which means two mods write Camera.main in the same frame. Suspend those state
                // flags for the whole symbiosis session and restore them exactly on dismount.
                _egoFirstPersonEnabledField = egoType.GetField("FirstPersonEnabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (_egoFirstPersonEnabledField != null && _egoFirstPersonEnabledField.FieldType == typeof(bool))
                {
                    _egoFirstPersonEnabledSaved = (bool)_egoFirstPersonEnabledField.GetValue(null);
                    _egoFirstPersonEnabledField.SetValue(null, false);
                }

                PropertyInfo instanceProperty = egoType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                _egoPluginInstance = instanceProperty?.GetValue(null, null);
                if (_egoPluginInstance != null)
                {
                    _egoTransitionActiveField = egoType.GetField("transitionActive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _egoTransitionTargetField = egoType.GetField("transitionToFirstPerson", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (_egoTransitionActiveField != null && _egoTransitionActiveField.FieldType == typeof(bool))
                    {
                        _egoTransitionActiveSaved = (bool)_egoTransitionActiveField.GetValue(_egoPluginInstance);
                        _egoTransitionActiveField.SetValue(_egoPluginInstance, false);
                    }
                    if (_egoTransitionTargetField != null && _egoTransitionTargetField.FieldType == typeof(bool))
                    {
                        _egoTransitionTargetSaved = (bool)_egoTransitionTargetField.GetValue(_egoPluginInstance);
                        _egoTransitionTargetField.SetValue(_egoPluginInstance, false);
                    }
                }

                _egoCameraStateSuspended = true;
                _debugLog?.Invoke($"Valheim Ego camera state suspended for Drake symbiosis (firstPerson={_egoFirstPersonEnabledSaved}, transition={_egoTransitionActiveSaved}).");
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Valheim Ego camera suspend failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void RestoreExternalEgoCameraState()
        {
            try
            {
                if (_egoToggleKeyEntry != null && _egoToggleKeyValueProperty != null && _egoToggleKeySavedValue != null)
                    _egoToggleKeyValueProperty.SetValue(_egoToggleKeyEntry, _egoToggleKeySavedValue, null);

                // F6 is global: leaving a creature must return the human player in the
                // SAME perspective that was active in symbiosis, not the perspective that
                // happened to be active before possession started.
                bool restoreEgo = _drakeEgoActive;
                if (_rider != null) SaveGlobalPerspective(_rider, restoreEgo);

                if (_egoFirstPersonEnabledField != null)
                    _egoFirstPersonEnabledField.SetValue(null, restoreEgo);

                if (_egoPluginInstance != null)
                {
                    if (_egoTransitionTargetField != null)
                        _egoTransitionTargetField.SetValue(_egoPluginInstance, restoreEgo);
                    if (_egoTransitionActiveField != null)
                        _egoTransitionActiveField.SetValue(_egoPluginInstance, false);
                }

                if (_egoCameraStateSuspended)
                    _debugLog?.Invoke($"Valheim Ego camera state restored after symbiosis using global F6 mode: {(restoreEgo ? "Ego" : "Third Person")}.");
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Valheim Ego camera restore failed safely: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _egoToggleKeyEntry = null;
                _egoToggleKeySavedValue = null;
                _egoToggleKeyValueProperty = null;
                _egoFirstPersonEnabledField = null;
                _egoFirstPersonEnabledSaved = false;
                _egoPluginInstance = null;
                _egoTransitionActiveField = null;
                _egoTransitionActiveSaved = false;
                _egoTransitionTargetField = null;
                _egoTransitionTargetSaved = false;
                _egoCameraStateSuspended = false;
            }
        }

        private static void OnCameraPreCull(Camera camera)
        {
            if (!Active || camera == null || camera != Camera.main || _mountedDrake == null)
                return;

            try
            {
                _preCullCameraPosition = camera.transform.position;
                _preCullCameraRotation = camera.transform.rotation;
                _cameraOverriddenThisRender = true;

                // v2.1.1: Keep the possessed creature body visible in Ego. Older builds hid every
                // renderer here, which made the player effectively invisible in first person.
                // The camera/near clip now handles facial clipping while limbs/body/attacks remain visible.
                ApplyOwnedSymbiosisCamera(camera);
            }
            catch (Exception ex)
            {
                _cameraOverriddenThisRender = false;
                _debugLog?.Invoke($"Drake symbiosis camera override failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void OnCameraPreRender(Camera camera)
        {
            if (!Active || camera == null || camera != Camera.main || _mountedDrake == null)
                return;

            try
            {
                // Valheim Ego deliberately restores the local player's renderers while leaving
                // first person. Its Camera.onPreCull callback can therefore run after our earlier
                // hide pass. Re-assert symbiosis ownership at the later onPreRender stage so F6
                // can only change the Drake camera and can never make the human body visible.
                EnforcePlayerVisualsHidden();
                // v2.1.1: Keep the possessed creature body visible in Ego. Older builds hid every
                // renderer here, which made the player effectively invisible in first person.
                // The camera/near clip now handles facial clipping while limbs/body/attacks remain visible.
                ApplyOwnedSymbiosisCamera(camera);
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Drake symbiosis final render lock failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void OnCameraPostRender(Camera camera)
        {
            // Do not restore the hidden Player/GameCamera pose while symbiosis is active. Restoring
            // it here was why pause/menu frames could show the camera tens of metres away. GameCamera
            // takes authority back naturally on the first frame after symbiosis ends.
            if (camera == null || camera != Camera.main) return;
            RestoreEgoHeadMouseLook();
            RestoreDrakeVisualsAfterEgoRender();
            _cameraOverriddenThisRender = false;
        }

        private static void InitializeSymbiosisCamera(Character worker)
        {
            if (worker == null) return;
            Vector3 euler = worker.transform.rotation.eulerAngles;
            _cameraYaw = euler.y;
            _cameraPitch = _drakeEgoActive ? 0f : 8f;
            _thirdChaseYaw = euler.y;
            _thirdChaseYawVelocity = 0f;
            _thirdChaseYawInitialized = true;
            _menuCameraLocked = false;
            _lastOwnedCameraPoseValid = false;
            if (!IsDrakeForm)
            {
                _drakeAnimator = null;
                _debugLog?.Invoke(ActiveFormDisplayName + " camera initialized without Drake wingbeat bobbing.");
            }
            else
            {
                _drakeAnimator = ResolveDrakeAnimator(worker);
                LogAvailableDrakeFlightClips(_drakeAnimator);
            }
            ResetNativeDrakeAttackCatalog(worker);
            _lastWingbeatClipName = string.Empty;
            _lastWingbeatStateHash = 0;
            _stableEgoOffsetInitialized = false;
            _wingbeatSmoothInitialized = false;
            _smoothedWingbeatMeters = 0f;
            CalibrateStableEgoOffset(worker);
            ConfigureChaseRig(worker);
            _cameraPoseInitialized = true;
        }


        private static void CalibrateStableEgoOffset(Character worker)
        {
            if (worker == null) return;
            try
            {
                Transform anchor = _drakeEgoAnchor != null ? _drakeEgoAnchor : ResolveDrakeEgoAnchor(worker);
                Vector3 worldEye = anchor != null
                    ? anchor.position
                    : worker.transform.position + worker.transform.up * Mathf.Max(0.8f, worker.GetRadius() * 0.8f);

                // Sample the anatomical eye/head point once, then store it in the Drake root's local
                // space. During flight we rebuild the camera point from the stable root transform
                // instead of reading the animated eye/head bone every frame. This keeps the camera
                // at the Drake's head while removing animation/IK jitter completely.
                _stableEgoLocalOffset = worker.transform.InverseTransformPoint(worldEye);
                _stableEgoOffsetInitialized = true;
                _debugLog?.Invoke($"Drake ego stable root offset calibrated: {_stableEgoLocalOffset}");
            }
            catch (Exception ex)
            {
                _stableEgoOffsetInitialized = false;
                _debugLog?.Invoke($"Drake ego root-offset calibration failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static Animator ResolveDrakeAnimator(Character worker)
        {
            if (worker == null) return null;
            try
            {
                GameObject visual = ResolveVisual(worker) ?? worker.gameObject;
                Animator animator = visual.GetComponentInChildren<Animator>(true);
                if (animator == null) animator = worker.GetComponentInChildren<Animator>(true);
                if (animator != null)
                    _debugLog?.Invoke($"Drake wingbeat animator resolved: {animator.gameObject.name}");
                else
                    _debugLog?.Invoke("Drake wingbeat animator not found; ego bobbing disabled safely.");
                return animator;
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Drake wingbeat animator resolve failed safely: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static void LogAvailableDrakeFlightClips(Animator animator)
        {
            if (animator == null) return;
            try
            {
                RuntimeAnimatorController controller = animator.runtimeAnimatorController;
                AnimationClip[] clips = controller != null ? controller.animationClips : null;
                if (clips == null || clips.Length == 0) return;

                string names = string.Empty;
                for (int i = 0; i < clips.Length; i++)
                {
                    AnimationClip clip = clips[i];
                    if (clip == null || string.IsNullOrEmpty(clip.name)) continue;
                    string n = clip.name;
                    if (n.IndexOf("fly", StringComparison.OrdinalIgnoreCase) < 0 &&
                        n.IndexOf("hover", StringComparison.OrdinalIgnoreCase) < 0 &&
                        n.IndexOf("soar", StringComparison.OrdinalIgnoreCase) < 0 &&
                        n.IndexOf("back", StringComparison.OrdinalIgnoreCase) < 0 &&
                        n.IndexOf("reverse", StringComparison.OrdinalIgnoreCase) < 0 &&
                        n.IndexOf("turn", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    if (names.Length > 0) names += ", ";
                    names += n;
                }

                if (names.Length > 0)
                    _debugLog?.Invoke("Drake locomotion animation clips available: " + names);
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Drake locomotion clip scan failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static float EvaluateAnimatorWingbeat(Character worker)
        {
            if (!IsDrakeForm) return 0f;
            try
            {
                if (_drakeAnimator == null) _drakeAnimator = ResolveDrakeAnimator(worker);
                Animator animator = _drakeAnimator;
                if (animator == null || !animator.isActiveAndEnabled || animator.layerCount <= 0) return 0f;

                AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
                AnimatorClipInfo[] clips = animator.GetCurrentAnimatorClipInfo(0);

                // The Drake uses a blend tree: IdleHover and FlyForward can share the same state hash.
                // Therefore we must look at the real clip weights, not only at the Animator state.
                string dominantClip = $"state:{state.fullPathHash}";
                float dominantWeight = -1f;
                if (clips != null)
                {
                    for (int i = 0; i < clips.Length; i++)
                    {
                        if (clips[i].clip == null) continue;
                        if (clips[i].weight > dominantWeight)
                        {
                            dominantWeight = clips[i].weight;
                            dominantClip = clips[i].clip.name;
                        }
                    }
                }

                if (_lastWingbeatStateHash != state.fullPathHash || !string.Equals(_lastWingbeatClipName, dominantClip, StringComparison.Ordinal))
                {
                    _lastWingbeatStateHash = state.fullPathHash;
                    _lastWingbeatClipName = dominantClip ?? string.Empty;
                    _debugLog?.Invoke($"Drake wingbeat animation: clip={_lastWingbeatClipName}, state={state.fullPathHash}, speed={state.speed:0.00}, speedMultiplier={state.speedMultiplier:0.00}, loop={state.loop}");
                }

                float phase01 = state.normalizedTime - Mathf.Floor(state.normalizedTime);
                float totalWeight = 0f;
                float bobMeters = 0f;

                if (clips != null && clips.Length > 0)
                {
                    for (int i = 0; i < clips.Length; i++)
                    {
                        AnimatorClipInfo info = clips[i];
                        if (info.clip == null || info.weight <= 0.0001f) continue;

                        string name = info.clip.name ?? string.Empty;
                        float amplitude;
                        float phaseOffset;

                        if (name.IndexOf("FlyForward", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // Forward flight has the strongest body heave.
                            amplitude = 0.014f;
                            phaseOffset = 0.00f;
                        }
                        else if (name.IndexOf("IdleHover", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // Hover should feel alive but much calmer.
                            amplitude = 0.006f;
                            phaseOffset = 0.00f;
                        }
                        else
                        {
                            // Unknown/transitioning flight clips get only a tiny safe amount.
                            amplitude = 0.003f;
                            phaseOffset = 0.00f;
                        }

                        float clipWave = Mathf.Sin((phase01 + phaseOffset) * Mathf.PI * 2f);
                        bobMeters += clipWave * amplitude * info.weight;
                        totalWeight += info.weight;
                    }
                }

                if (totalWeight > 0.0001f)
                    bobMeters /= totalWeight;

                float intensity = 1f;
                try
                {
                    if (_wingbeatIntensityProvider != null)
                        intensity = Mathf.Clamp(_wingbeatIntensityProvider(), 0f, 2f);
                }
                catch
                {
                    intensity = 1f;
                }

                // bobMeters is already authored in metres. The old *10 multiplier turned a
                // 6-14 mm animation pulse into 6-14 cm camera jumps and made clip blending look
                // like a camera "twitch". Keep the physical scale here; smoothing is applied by
                // the camera owner below.
                return bobMeters * intensity;
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Drake wingbeat sampling failed safely: {ex.GetType().Name}: {ex.Message}");
                return 0f;
            }
        }

        private static void ConfigureChaseRig(Character worker)
        {
            _chaseFocusLocal = new Vector3(0f, 0.70f, -0.10f);
            _chaseDistance = 8.5f;
            _chaseHeight = 1.75f;
            _thirdCameraSmoothInitialized = false;
            _thirdCameraSmoothFrame = -1;
            if (worker == null) return;

            try
            {
                GameObject visual = ResolveVisual(worker) ?? worker.gameObject;
                Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(true);
                bool hasBounds = false;
                Bounds bounds = new Bounds();
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null || !renderer.enabled) continue;
                    if (!hasBounds)
                    {
                        bounds = renderer.bounds;
                        hasBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(renderer.bounds);
                    }
                }

                if (!hasBounds) return;

                _chaseFocusLocal = worker.transform.InverseTransformPoint(
                    bounds.center + Vector3.up * Mathf.Clamp(bounds.extents.y * 0.28f, 0.18f, 0.55f));

                // Compute this once when symbiosis starts. Using animated bounds every frame made the
                // camera breathe in/out with the Drake's wing flap. The fixed rig stays far enough
                // behind the wings while remaining much closer than the old runaway camera.
                float visualRadius = Mathf.Max(bounds.extents.x, bounds.extents.z);
                if (IsSerpentForm)
                {
                    // Anchor the chase focus to the actual head when the prefab exposes one. This avoids
                    // the long-body renderer bounds pulling the camera toward the middle of the snake.
                    Transform head = FindSerpentHeadTransform(visual.transform);
                    if (head != null)
                    {
                        Vector3 headFocus = head.position + Vector3.up * 0.35f;
                        _chaseFocusLocal = worker.transform.InverseTransformPoint(headFocus);
                    }
                    else
                    {
                        _chaseFocusLocal = new Vector3(
                            0f,
                            Mathf.Clamp(bounds.extents.y * 0.30f + 0.50f, 1.00f, 2.00f),
                            Mathf.Clamp(bounds.extents.z * 0.30f, 2.00f, 4.60f));
                    }
                    _chaseDistance = Mathf.Clamp(visualRadius * 0.95f + 4.5f, 8.5f, 12.0f);
                    _chaseHeight = Mathf.Clamp(bounds.extents.y * 0.42f + 0.95f, 1.55f, 2.75f);
                }
                else if (IsTrollForm)
                {
                    _chaseDistance = Mathf.Clamp(visualRadius * 1.65f + 4.0f, 9.5f, 14.0f);
                    _chaseHeight = Mathf.Clamp(bounds.extents.y * 0.72f + 1.0f, 2.6f, 5.0f);
                }
                else if (IsWerewolfForm)
                {
                    _chaseDistance = Mathf.Clamp(visualRadius * 1.55f + 3.2f, 6.5f, 9.5f);
                    _chaseHeight = Mathf.Clamp(bounds.extents.y * 0.62f + 0.8f, 1.8f, 3.3f);
                }
                else
                {
                    _chaseDistance = Mathf.Clamp(visualRadius * 1.80f + 2.5f, 7.5f, 10.5f);
                    _chaseHeight = Mathf.Clamp(bounds.extents.y * 0.70f + 0.60f, 1.35f, 2.5f);
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Drake chase-camera calibration failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static Transform FindSerpentHeadTransform(Transform root)
        {
            if (root == null) return null;
            try
            {
                Transform[] all = root.GetComponentsInChildren<Transform>(true);
                Transform fallback = null;
                for (int i = 0; i < all.Length; i++)
                {
                    Transform t = all[i];
                    if (t == null) continue;
                    string n = (t.name ?? string.Empty).ToLowerInvariant();
                    if (n == "head" || n.EndsWith("_head") || n.Contains("head.")) return t;
                    if (fallback == null && n.Contains("head") && !n.Contains("eye") && !n.Contains("jaw")) fallback = t;
                }
                return fallback;
            }
            catch { return null; }
        }

        private static void UpdateSymbiosisCameraLook()
        {
            if (!Active || _mountedDrake == null || IsMountUiBlocking()) return;
            if (!_cameraPoseInitialized) InitializeSymbiosisCamera(_mountedDrake);

            try
            {
                float sensitivity = 2.15f;
                _cameraYaw += Input.GetAxis("Mouse X") * sensitivity;
                _cameraPitch -= Input.GetAxis("Mouse Y") * sensitivity;
                _cameraPitch = Mathf.Clamp(_cameraPitch, -75f, 75f);
            }
            catch { }
        }

        private static void EnsureUiVisibilityProbes()
        {
            if (_uiVisibilityProbes != null) return;
            Assembly gameAssembly = typeof(Player).Assembly;
            UiVisibilityProbe[] probes = new UiVisibilityProbe[UiBlockingTypeNames.Length];
            for (int i = 0; i < UiBlockingTypeNames.Length; ++i)
            {
                UiVisibilityProbe probe = new UiVisibilityProbe();
                Type type = gameAssembly.GetType(UiBlockingTypeNames[i], false);
                if (type != null)
                {
                    probe.StaticVisible = type.GetMethod("IsVisible", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                        ?? type.GetMethod("IsOpen", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    probe.InstanceProperty = type.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    probe.InstanceField = type.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                        ?? type.GetField("m_instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    probe.InstanceVisible = type.GetMethod("IsVisible", InstanceFlags, null, Type.EmptyTypes, null)
                        ?? type.GetMethod("IsOpen", InstanceFlags, null, Type.EmptyTypes, null);
                }
                probes[i] = probe;
            }
            _uiVisibilityProbes = probes;
        }

        private static bool IsMountUiBlocking()
        {
            int frame = Time.frameCount;
            if (_uiBlockingCachedFrame == frame) return _uiBlockingCachedValue;

            bool blocking = false;
            try
            {
                if (Cursor.visible)
                {
                    blocking = true;
                }
                else
                {
                    EnsureUiVisibilityProbes();
                    for (int i = 0; i < _uiVisibilityProbes.Length && !blocking; ++i)
                    {
                        UiVisibilityProbe probe = _uiVisibilityProbes[i];
                        if (probe == null) continue;

                        if (probe.StaticVisible != null && probe.StaticVisible.ReturnType == typeof(bool))
                        {
                            object result = probe.StaticVisible.Invoke(null, null);
                            if (result is bool visible && visible)
                            {
                                blocking = true;
                                break;
                            }
                        }

                        object instance = probe.InstanceProperty != null
                            ? probe.InstanceProperty.GetValue(null, null)
                            : probe.InstanceField?.GetValue(null);
                        if (instance == null || probe.InstanceVisible == null || probe.InstanceVisible.ReturnType != typeof(bool)) continue;
                        object instanceResult = probe.InstanceVisible.Invoke(instance, null);
                        if (instanceResult is bool instanceVisible && instanceVisible) blocking = true;
                    }
                }
            }
            catch { }

            _uiBlockingCachedFrame = frame;
            _uiBlockingCachedValue = blocking;
            return blocking;
        }

        private static bool ApplyMenuCameraLock(Camera camera)
        {
            if (camera == null) return false;
            bool blocking = IsMountUiBlocking();
            if (!blocking)
            {
                _menuCameraLocked = false;
                return false;
            }

            if (!_menuCameraLocked)
            {
                _menuCameraLocked = true;
                if (_lastOwnedCameraPoseValid)
                {
                    _menuLockedCameraPosition = _lastOwnedCameraPosition;
                    _menuLockedCameraRotation = _lastOwnedCameraRotation;
                    _menuLockedNearClip = _lastOwnedNearClip;
                }
                else
                {
                    _menuLockedCameraPosition = camera.transform.position;
                    _menuLockedCameraRotation = camera.transform.rotation;
                    _menuLockedNearClip = camera.nearClipPlane;
                }
            }

            camera.transform.position = _menuLockedCameraPosition;
            camera.transform.rotation = _menuLockedCameraRotation;
            camera.nearClipPlane = _menuLockedNearClip;
            return true;
        }

        private static void RememberOwnedCameraPose(Camera camera)
        {
            if (camera == null || _menuCameraLocked) return;
            _lastOwnedCameraPosition = camera.transform.position;
            _lastOwnedCameraRotation = camera.transform.rotation;
            _lastOwnedNearClip = camera.nearClipPlane;
            _lastOwnedCameraPoseValid = true;
        }

        private static float GetSymbiosisEgoForwardOffset()
        {
            // v2.1.3: the detected eye/head pivot is centered correctly, but several creature rigs
            // place that pivot deeper inside the skull than the visible eyes. Move from that animated
            // pivot toward the face by a small scale-aware amount. This is deliberately anatomical
            // forward (the anchor's animated forward), not camera-look forward, so looking around
            // cannot make the camera orbit sideways through the head.
            float radius = _mountedDrake != null ? Mathf.Max(0.1f, _mountedDrake.GetRadius()) : 1f;
            if (IsTrollForm) return Mathf.Clamp(radius * 0.28f, 0.34f, 0.58f);
            if (IsWerewolfForm) return Mathf.Clamp(radius * 0.22f, 0.18f, 0.30f);
            if (IsSerpentForm) return Mathf.Clamp(radius * 0.22f, 0.20f, 0.36f);
            return Mathf.Clamp(radius * 0.22f, 0.20f, 0.34f); // Drake
        }

        private static float GetSymbiosisEgoNearClip()
        {
            // Match the normal Ego principle: camera at the skull/eyes and a very small near plane.
            // This clips only the immediate inside of the face while keeping chest, arms, claws,
            // Troll log and attack animation visible.
            return 0.025f;
        }

        private static Vector3 GetDrakeEyePosition()
        {
            if (_mountedDrake == null) return _rider != null ? _rider.transform.position + Vector3.up * 1.6f : Vector3.zero;
            if (!_stableEgoOffsetInitialized) CalibrateStableEgoOffset(_mountedDrake);
            if (_stableEgoOffsetInitialized)
                return _mountedDrake.transform.TransformPoint(_stableEgoLocalOffset);

            Transform anchor = _drakeEgoAnchor != null ? _drakeEgoAnchor : ResolveDrakeEgoAnchor(_mountedDrake);
            if (anchor != null) return anchor.position;
            return _mountedDrake.transform.position + _mountedDrake.transform.up * Mathf.Max(0.8f, _mountedDrake.GetRadius() * 0.8f);
        }

        private static void GetHumanExitCameraPose(out Vector3 position, out Quaternion rotation)
        {
            if (_rider == null)
            {
                position = _transitionCameraStartPosition;
                rotation = _transitionCameraStartRotation;
                return;
            }

            if (_humanCameraRigCaptured)
            {
                position = _rider.transform.TransformPoint(_humanCameraLocalOffset);
                rotation = _rider.transform.rotation * _humanCameraLocalRotation;
                return;
            }

            position = _rider.transform.position + Vector3.up * 1.62f;
            rotation = _rider.transform.rotation;
        }

        private static void ApplyTransformationCamera(Camera camera)
        {
            if (camera == null || _transitionState == SymbiosisTransitionState.None) return;

            float t = Mathf.Clamp01((Time.unscaledTime - _transitionStartedAt) / TransformationDuration);
            float eased = Smooth01(t);
            Vector3 targetPosition;
            Quaternion targetRotation;

            if (_transitionState == SymbiosisTransitionState.Entering)
            {
                targetPosition = GetDrakeEyePosition();
                targetRotation = Quaternion.Euler(_cameraPitch, _cameraYaw, 0f);
            }
            else
            {
                GetHumanExitCameraPose(out targetPosition, out targetRotation);
            }

            Vector3 position = Vector3.Lerp(_transitionCameraStartPosition, targetPosition, eased);
            // A tiny vertical arc prevents the hand-off from looking like a linear teleport while
            // remaining subtle enough to avoid motion sickness.
            position += Vector3.up * (Mathf.Sin(eased * Mathf.PI) * 0.18f);
            Quaternion rotation = Quaternion.Slerp(_transitionCameraStartRotation, targetRotation, eased);

            // A short impact pulse around the actual body hand-off makes the morph feel physical
            // instead of like a camera lerp. The pulse is deliberately small so it also remains
            // comfortable in first person.
            float impactCenter = _transitionState == SymbiosisTransitionState.Entering ? 0.52f : 0.48f;
            float impactDelta = (t - impactCenter) / 0.105f;
            float impact = Mathf.Exp(-(impactDelta * impactDelta));
            Vector3 impactBack = rotation * Vector3.back;
            position += impactBack * (impact * 0.14f);
            position += Vector3.up * (impact * 0.055f);
            rotation *= Quaternion.Euler(-impact * 0.8f, 0f, Mathf.Sin(t * Mathf.PI * 5f) * impact * 0.45f);

            camera.transform.position = position;
            camera.transform.rotation = rotation;
            camera.nearClipPlane = 0.02f;
            if (_savedFov > 1f)
                camera.fieldOfView = _savedFov + Mathf.Sin(eased * Mathf.PI) * 2.5f + impact * 4.5f;
        }

        private static void ApplyOwnedSymbiosisCamera(Camera camera)
        {
            if (camera == null || _mountedDrake == null) return;
            if (!_cameraPoseInitialized) InitializeSymbiosisCamera(_mountedDrake);
            if (ApplyMenuCameraLock(camera)) return;

            if (_transitionState != SymbiosisTransitionState.None)
            {
                ApplyTransformationCamera(camera);
                RememberOwnedCameraPose(camera);
                return;
            }

            Transform egoAnchor = _drakeEgoAnchor != null ? _drakeEgoAnchor : ResolveDrakeEgoAnchor(_mountedDrake);
            if (egoAnchor != null) _drakeEgoAnchor = egoAnchor;

            Quaternion lookRotation = Quaternion.Euler(_cameraPitch, _cameraYaw, 0f);
            if (_drakeEgoActive)
            {
                // v2.1.6: make the creature's actual animated head follow the player's mouse look
                // before sampling the eye anchor. This keeps the camera skull-locked while also
                // moving the face/neck naturally with pitch, so looking down exposes the chest,
                // arms/claws and weapon instead of leaving the camera buried in a forward-facing head.
                ApplyEgoHeadMouseLook();
                // True Drake-eye camera. The anchor is calibrated ONCE in the animated Head bone's
                // local space (prefer real eye transforms / Character.m_eye) and is then read back
                // on every render pass, just like Valheim Ego's skull lock. No artificial forward
                // push is applied: the camera really lives at the Drake's eyes/head.
                // v2.1.1: follow the ACTUAL animated anatomical eye/head anchor every frame.
                // The old root-local snapshot was stable, but it detached the camera from the skull
                // as soon as the creature animation moved the head. Position follows animation;
                // rotation remains player-owned below so attack/head animation cannot whip the view.
                Vector3 eyePosition = egoAnchor != null
                    ? egoAnchor.position
                    : (_stableEgoOffsetInitialized
                        ? _mountedDrake.transform.TransformPoint(_stableEgoLocalOffset)
                        : _mountedDrake.transform.position
                          + _mountedDrake.transform.up * Mathf.Max(0.8f, _mountedDrake.GetRadius() * 0.8f));

                // Same principle as Valheim Ego: the animated Head/Eye anchor owns POSITION only.
                // Bone rotation is intentionally not inherited because creature eye/head bones can carry
                // animation-space roll/pitch that makes the view whip around. Look rotation stays player-owned.
                Quaternion egoRotation = Quaternion.Euler(_cameraPitch, _cameraYaw, 0f);

                // v2.1.2 TRUE-EYE skull lock: use the authored animated eye point directly on the
                // render frame. Do not low-pass the head motion: even a fast filter creates visible
                // camera lag during punches, bites and locomotion and makes the body slide around
                // underneath the view. onPreCull/onPreRender already sample after animation, so the
                // exact anchor is both the most stable and the most natural reference.
                _smoothedEgoPosition = eyePosition;
                _egoPositionSmoothInitialized = true;
                _egoPositionSmoothFrame = Time.frameCount;

                // Wingbeat motion is driven by the REAL Drake Animator clock, never by a synthetic timer.
                // AnimatorStateInfo.normalizedTime advances with the actual active animation (including
                // speed changes and transitions), so the camera pulse remains locked to the same cycle
                // that drives the wings. We deliberately keep this positional-only and tiny; the exact
                // phase can be tuned later per real Drake clip after seeing its clip name in the log.
                // Already returned in metres and blended from the actual active animation clips.
                // No synthetic timer and no extra amplitude multiplier here.
                float wingbeatMeters = EvaluateAnimatorWingbeat(_mountedDrake);
                if (!_wingbeatSmoothInitialized)
                {
                    _smoothedWingbeatMeters = wingbeatMeters;
                    _wingbeatSmoothInitialized = true;
                }
                else
                {
                    float wingDt = Mathf.Clamp(Time.unscaledDeltaTime, 0.001f, 0.05f);
                    float wingT = 1f - Mathf.Exp(-9f * wingDt);
                    _smoothedWingbeatMeters = Mathf.Lerp(_smoothedWingbeatMeters, wingbeatMeters, wingT);
                }
                Vector3 wingbeatOffset = Vector3.up * _smoothedWingbeatMeters;

                // v2.1.2 true-eye skull-lock pass: the creature remains rendered. A tiny
                // form-specific forward bias plus a slightly larger near clip keeps the camera out
                // of the inside of the face while preserving visible chest/arms/claws/log attacks.
                float egoForward = GetSymbiosisEgoForwardOffset();
                float egoNearClip = GetSymbiosisEgoNearClip();
                camera.nearClipPlane = egoNearClip;
                Vector3 anatomicalForward;
                if (IsTrollForm)
                    anatomicalForward = -(egoAnchor != null ? egoAnchor.forward : _mountedDrake.transform.forward);
                else if (IsWerewolfForm && _egoHeadLookTransform != null && egoAnchor != null)
                {
                    // Fenring uses a non-standard Head-bone forward axis. Use the animated
                    // Head -> Eye direction for camera forward placement.
                    anatomicalForward = egoAnchor.position - _egoHeadLookTransform.position;
                    if (anatomicalForward.sqrMagnitude < 0.0004f) anatomicalForward = egoAnchor.forward;
                }
                else if (IsSerpentForm && egoAnchor != null)
                {
                    // Sea Serpent m_eye is a positional sight point, not a facial direction vector.
                    // The Ego anchor was authored in world/root forward before parenting to Head, so
                    // its forward axis is the stable visible snout axis even though the Head bone basis
                    // itself is rotated. Using Head->Eye here pitches the whole head downward.
                    anatomicalForward = egoAnchor.forward;
                }
                else
                    anatomicalForward = egoAnchor != null ? egoAnchor.forward : _mountedDrake.transform.forward;
                Vector3 anatomicalRight = egoAnchor != null ? egoAnchor.right : _mountedDrake.transform.right;
                Vector3 anatomicalUp = egoAnchor != null ? egoAnchor.up : _mountedDrake.transform.up;
                if (anatomicalForward.sqrMagnitude < 0.001f) anatomicalForward = _mountedDrake.transform.forward;
                if (anatomicalRight.sqrMagnitude < 0.001f) anatomicalRight = _mountedDrake.transform.right;
                if (anatomicalUp.sqrMagnitude < 0.001f) anatomicalUp = _mountedDrake.transform.up;
                anatomicalForward.Normalize();
                anatomicalRight.Normalize();
                anatomicalUp.Normalize();
                Vector3 fine = _egoFineTuneProvider != null ? _egoFineTuneProvider(ActiveFormIdForSettings) : Vector3.zero;
                camera.transform.position = _smoothedEgoPosition + wingbeatOffset
                    + anatomicalForward * (egoForward + fine.z)
                    + anatomicalRight * fine.x
                    + anatomicalUp * fine.y;
                camera.transform.rotation = egoRotation;
                RememberOwnedCameraPose(camera);
                return;
            }

            if (_savedNearClip > 0f)
                camera.nearClipPlane = _savedNearClip;

            // Stable chase rig: calibrated once from the actual Drake renderer dimensions so the
            // camera clears the wings but cannot jump tens of metres away. The focus follows the
            // Drake body, while mouse look orbits around that point.
            // Keep the chase rig world-up stable. Do not TransformPoint through the Drake's
            // animated/flight rotation: pitch/roll from the creature would otherwise throw the
            // camera above/below the body and can even rotate the perceived horizon.
            Vector3 yawRight = Quaternion.Euler(0f, _mountedDrake.transform.eulerAngles.y, 0f) * Vector3.right;
            Vector3 yawForward = Quaternion.Euler(0f, _mountedDrake.transform.eulerAngles.y, 0f) * Vector3.forward;
            Vector3 bodyFocus = _mountedDrake.transform.position
                + yawRight * _chaseFocusLocal.x
                + Vector3.up * _chaseFocusLocal.y
                + yawForward * _chaseFocusLocal.z;
            // Aim-to-fly third person: camera orbit follows the player's mouse aim directly.
            // The Drake then turns toward the same yaw/pitch, so camera, view direction and flight
            // direction are one coherent axis instead of the camera trailing a separate body axis.
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0.001f, 0.05f);
            // Free-look chase camera: mouse pitch/yaw owns the view directly in third person.
            // The camera remains behind the Drake, but it no longer forces its rotation back toward
            // the bodyFocus every frame. This allows looking fully up/down while keeping aim-to-fly.
            float chasePitch = Mathf.Clamp(_cameraPitch, -75f, 75f);
            Quaternion chaseRotation = Quaternion.Euler(chasePitch, _cameraYaw, 0f);
            Vector3 aimForward = chaseRotation * Vector3.forward;
            Vector3 desired = bodyFocus - aimForward * _chaseDistance + Vector3.up * _chaseHeight;

            // Keep the camera above the Drake at very low altitude, but do not clamp the actual view pitch.
            desired.y = Mathf.Max(desired.y, bodyFocus.y + 0.70f);
            desired = ResolveChaseCameraCollision(bodyFocus, desired);

            Quaternion targetRotation = chaseRotation;

            if (!_thirdCameraSmoothInitialized)
            {
                _smoothedThirdPosition = desired;
                _smoothedThirdRotation = targetRotation;
                _thirdCameraSmoothInitialized = true;
                _thirdCameraSmoothFrame = Time.frameCount;
            }
            else if (_thirdCameraSmoothFrame != Time.frameCount)
            {
                float positionT = 1f - Mathf.Exp(-6.5f * dt);
                float rotationT = 1f - Mathf.Exp(-11f * dt);
                _smoothedThirdPosition = Vector3.Lerp(_smoothedThirdPosition, desired, positionT);
                _smoothedThirdRotation = Quaternion.Slerp(_smoothedThirdRotation, targetRotation, rotationT);
                _thirdCameraSmoothFrame = Time.frameCount;
            }

            camera.transform.position = _smoothedThirdPosition;
            camera.transform.rotation = _smoothedThirdRotation;
            RememberOwnedCameraPose(camera);
        }

        private static Vector3 ResolveChaseCameraCollision(Vector3 focus, Vector3 desired)
        {
            Vector3 delta = desired - focus;
            float distance = delta.magnitude;
            if (distance < 0.01f) return desired;

            Vector3 direction = delta / distance;
            try
            {
                int hitCount = Physics.SphereCastNonAlloc(
                    focus,
                    0.18f,
                    direction,
                    _chaseCameraHitBuffer,
                    distance,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);

                float nearest = distance;
                for (int i = 0; i < hitCount; i++)
                {
                    RaycastHit cameraHit = _chaseCameraHitBuffer[i];
                    _chaseCameraHitBuffer[i] = default(RaycastHit);
                    Collider collider = cameraHit.collider;
                    if (collider == null) continue;
                    Transform t = collider.transform;
                    if (_mountedDrake != null && (t == _mountedDrake.transform || t.IsChildOf(_mountedDrake.transform))) continue;
                    if (_rider != null && (t == _rider.transform || t.IsChildOf(_rider.transform))) continue;
                    if (cameraHit.distance > 0.01f && cameraHit.distance < nearest) nearest = cameraHit.distance;
                }

                if (nearest < distance)
                    return focus + direction * Mathf.Max(0.45f, nearest - 0.22f);
            }
            catch { }
            return desired;
        }

        private static void CaptureCameraState()
        {
            if (_cameraStateCaptured) return;
            try
            {
                Camera camera = Camera.main;
                if (camera == null) return;
                _savedNearClip = camera.nearClipPlane;
                _savedFov = camera.fieldOfView;
                _cameraStateCaptured = true;
            }
            catch { }
        }

        private static void RestoreCameraState()
        {
            if (!_cameraStateCaptured) return;
            try
            {
                Camera camera = Camera.main;
                if (camera != null)
                {
                    if (_savedNearClip > 0f) camera.nearClipPlane = _savedNearClip;
                    if (_savedFov > 1f) camera.fieldOfView = _savedFov;
                }
            }
            catch { }
            _savedNearClip = -1f;
            _savedFov = -1f;
            _cameraStateCaptured = false;
        }

        private static bool IsLikelyFirstPerson(Player player)
        {
            try
            {
                Camera camera = Camera.main;
                if (player == null || camera == null) return false;
                // Valheim Ego places the camera essentially on the character in first person.
                return Vector3.Distance(camera.transform.position, player.transform.position) < 1.65f;
            }
            catch { return false; }
        }

        private static void ApplyEgoHeadMouseLook()
        {
            if (!_drakeEgoActive || _mountedDrake == null || _egoHeadLookTransform == null || _egoHeadLookApplied) return;
            try
            {
                Transform head = _egoHeadLookTransform;
                _egoHeadLookOriginalRotation = head.rotation;

                Vector3 targetForward = Quaternion.Euler(_cameraPitch, _cameraYaw, 0f) * Vector3.forward;
                if (targetForward.sqrMagnitude < 0.0001f) return;
                targetForward.Normalize();

                Vector3 currentForward;
                if (IsTrollForm)
                {
                    // Troll Head bone/eye anchor faces opposite the visible face.
                    currentForward = -head.forward;
                }
                else if (IsWerewolfForm && _drakeEgoAnchor != null)
                {
                    // Fenring does not use Head +Z as its visible facial/muzzle axis.
                    currentForward = _drakeEgoAnchor.position - head.position;
                    if (currentForward.sqrMagnitude < 0.0004f) currentForward = head.forward;
                }
                else if ((IsSerpentForm || IsDrakeForm) && _drakeEgoAnchor != null)
                {
                    // Sea Serpent and Drake Head bones do not use their local +Z as the visible
                    // muzzle/face direction. The animated eye anchor is created with the creature/root
                    // forward as its neutral world rotation before being parented to Head, so its
                    // forward axis preserves the authored neutral head pose while still following
                    // animation. Using head.forward on the Drake pitched the visible head upward.
                    currentForward = _drakeEgoAnchor.forward;
                }
                else
                {
                    currentForward = head.forward;
                }
                if (currentForward.sqrMagnitude < 0.0001f) currentForward = _mountedDrake.transform.forward;
                currentForward.Normalize();

                // World-space correction preserves the authored/animated head-bone basis and roll,
                // but points the creature's real facial axis exactly at the mouse-owned camera direction.
                Quaternion correction = Quaternion.FromToRotation(currentForward, targetForward);
                head.rotation = correction * head.rotation;
                _egoHeadLookApplied = true;
            }
            catch { _egoHeadLookApplied = false; }
        }

        private static void RestoreEgoHeadMouseLook()
        {
            if (!_egoHeadLookApplied) return;
            try
            {
                if (_egoHeadLookTransform != null) _egoHeadLookTransform.rotation = _egoHeadLookOriginalRotation;
            }
            catch { }
            _egoHeadLookApplied = false;
        }

        private static Transform ResolveDrakeEgoAnchor(Character worker)
        {
            if (worker == null) return null;
            GameObject visual = ResolveVisual(worker) ?? worker.gameObject;
            Transform root = visual.transform;

            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            Transform head = null;
            Transform leftEye = null;
            Transform rightEye = null;
            int bestScore = int.MinValue;

            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null || string.IsNullOrEmpty(t.name)) continue;
                string n = t.name.ToLowerInvariant();

                if (n.Contains("eye"))
                {
                    bool left = n.Contains("left") || n.Contains("eye_l") || n.Contains("l_eye") || n.EndsWith(".l") || n.EndsWith("_l");
                    bool right = n.Contains("right") || n.Contains("eye_r") || n.Contains("r_eye") || n.EndsWith(".r") || n.EndsWith("_r");
                    if (left && leftEye == null) leftEye = t;
                    if (right && rightEye == null) rightEye = t;
                }

                int score = int.MinValue;
                if (n == "head" || n.EndsWith("_head") || n.EndsWith(".head")) score = 100;
                else if (n.Contains("head")) score = 80;
                else if (n.Contains("neck")) score = 45;
                else if (n.Contains("jaw")) score = 30;
                if (score > bestScore)
                {
                    head = t;
                    bestScore = score;
                }
            }

            Transform nativeEye = null;
            try
            {
                nativeEye = CharacterEyeField?.GetValue(worker) as Transform;
            }
            catch { }

            // Non-humanoid Valheim creatures often expose their authored sight point through
            // Character.m_eye. Prefer real left/right eye bones when present, otherwise use m_eye.
            // Both are superior to a guessed positional offset.
            Vector3 eyeWorld;
            string source;
            if (leftEye != null && rightEye != null)
            {
                eyeWorld = (leftEye.position + rightEye.position) * 0.5f;
                source = "LeftEye/RightEye midpoint";
            }
            else if (nativeEye != null)
            {
                eyeWorld = nativeEye.position;
                source = $"Character.m_eye ({nativeEye.name})";
            }
            else if (head != null && bestScore >= 45)
            {
                eyeWorld = head.position;
                source = "Head pivot fallback";
            }
            else
            {
                eyeWorld = worker.transform.position
                    + worker.transform.up * Mathf.Max(0.8f, worker.GetRadius() * 0.8f);
                source = "root fallback";
            }

            // Anchor to the animated head when possible. Set its WORLD pose first, then parent with
            // worldPositionStays=true. This stores both eye position and a neutral forward rotation
            // in Head-local space, so head animation moves/rotates the camera naturally thereafter.
            Transform parent = head != null && bestScore >= 45
                ? head
                : (nativeEye != null ? nativeEye.parent : root);
            if (parent == null) parent = root;

            _egoHeadLookTransform = head != null && bestScore >= 45 ? head : parent;

            Transform existing = parent.Find("VW_DrakeEgoAnchor");
            if (existing != null) return existing;

            GameObject go = new GameObject("VW_DrakeEgoAnchor");
            Transform anchor = go.transform;
            anchor.position = eyeWorld;

            Vector3 forward = worker.transform.forward;
            if (forward.sqrMagnitude < 0.001f) forward = root.forward;
            Vector3 up = worker.transform.up;
            if (up.sqrMagnitude < 0.001f) up = Vector3.up;
            anchor.rotation = Quaternion.LookRotation(forward.normalized, up.normalized);
            anchor.SetParent(parent, true);
            anchor.localScale = Vector3.one;

            _debugLog?.Invoke(
                $"{ActiveFormDisplayName} animated eye anchor created: source={source}, parent={parent.name}, " +
                $"world={eyeWorld}, local={anchor.localPosition}. Ego follows this animated position live.");
            return anchor;
        }

        private static bool IsPlayerTeleporting(Player player)
        {
            if (player == null || PlayerIsTeleportingMethod == null) return false;
            try
            {
                object value = PlayerIsTeleportingMethod.Invoke(player, null);
                return value is bool b && b;
            }
            catch { return false; }
        }

        private static Transform ResolveOrCreateSeat(Character worker)
        {
            if (worker == null) return null;

            GameObject visual = ResolveVisual(worker);
            Transform parent = visual != null ? visual.transform : worker.transform;
            Transform seat = parent.Find(SeatName);
            if (seat == null)
            {
                GameObject seatObject = new GameObject(SeatName);
                seat = seatObject.transform;
                seat.SetParent(parent, false);
            }

            // Symbiosis anchor: keep the hidden Player object in the visual centre of the Drake.
            // Renderer bounds are used instead of the creature root because the Drake visual can be
            // offset/scaled relative to its network root.
            Vector3 worldCenter = worker.transform.position + worker.transform.up * Mathf.Max(0.2f, worker.GetRadius() * 0.25f);
            try
            {
                Renderer[] renderers = parent.GetComponentsInChildren<Renderer>(true);
                bool hasBounds = false;
                Bounds bounds = new Bounds();
                foreach (Renderer renderer in renderers)
                {
                    if (renderer == null || !renderer.enabled) continue;
                    if (!hasBounds)
                    {
                        bounds = renderer.bounds;
                        hasBounds = true;
                    }
                    else bounds.Encapsulate(renderer.bounds);
                }
                if (hasBounds) worldCenter = bounds.center;
            }
            catch { }

            seat.position = worldCenter;
            seat.rotation = parent.rotation;
            return seat;
        }

        private static void CacheDrakeVisuals(Character worker)
        {
            RestoreDrakeVisualsAfterEgoRender();
            _drakeEgoRenderers = Array.Empty<Renderer>();
            _drakeEgoRendererStates = Array.Empty<bool>();
            if (worker == null) return;
            try
            {
                GameObject visual = ResolveVisual(worker) ?? worker.gameObject;
                _drakeEgoRenderers = visual.GetComponentsInChildren<Renderer>(true) ?? Array.Empty<Renderer>();
                _drakeEgoRendererStates = new bool[_drakeEgoRenderers.Length];
            }
            catch { }
        }

        private static void HideDrakeVisualsForEgoRender()
        {
            if (!_drakeEgoActive || _drakeVisualHiddenForRender) return;
            try
            {
                int count = _drakeEgoRenderers?.Length ?? 0;
                if (_drakeEgoRendererStates == null || _drakeEgoRendererStates.Length != count)
                    _drakeEgoRendererStates = new bool[count];
                for (int i = 0; i < count; i++)
                {
                    Renderer renderer = _drakeEgoRenderers[i];
                    if (renderer == null) continue;
                    _drakeEgoRendererStates[i] = renderer.enabled;
                    renderer.enabled = false;
                }
                _drakeVisualHiddenForRender = true;
            }
            catch { }
        }

        private static void RestoreDrakeVisualsAfterEgoRender()
        {
            if (!_drakeVisualHiddenForRender) return;
            try
            {
                int count = Math.Min(_drakeEgoRenderers?.Length ?? 0, _drakeEgoRendererStates?.Length ?? 0);
                for (int i = 0; i < count; i++)
                {
                    Renderer renderer = _drakeEgoRenderers[i];
                    if (renderer != null) renderer.enabled = _drakeEgoRendererStates[i];
                }
            }
            catch { }
            _drakeVisualHiddenForRender = false;
        }

        private static void CachePlayerVisuals(Player player)
        {
            RestorePlayerVisuals();
            if (player == null) return;
            try
            {
                Renderer[] renderers = player.GetComponentsInChildren<Renderer>(true);
                _hiddenPlayerRenderers = renderers ?? Array.Empty<Renderer>();
                _hiddenPlayerRendererStates = new bool[_hiddenPlayerRenderers.Length];
                for (int i = 0; i < _hiddenPlayerRenderers.Length; i++)
                {
                    Renderer renderer = _hiddenPlayerRenderers[i];
                    _hiddenPlayerRendererStates[i] = renderer != null && renderer.enabled;
                }

                Collider[] colliders = player.GetComponentsInChildren<Collider>(true);
                _hiddenPlayerColliders = colliders ?? Array.Empty<Collider>();
                _hiddenPlayerColliderStates = new bool[_hiddenPlayerColliders.Length];
                for (int i = 0; i < _hiddenPlayerColliders.Length; i++)
                {
                    Collider collider = _hiddenPlayerColliders[i];
                    _hiddenPlayerColliderStates[i] = collider != null && collider.enabled;
                }
                _playerVisualsHidden = false;
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Drake symbiosis player-visual cache failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void SetPlayerVisualsHidden(bool hidden)
        {
            try
            {
                int count = Math.Min(_hiddenPlayerRenderers?.Length ?? 0, _hiddenPlayerRendererStates?.Length ?? 0);
                for (int i = 0; i < count; i++)
                {
                    Renderer renderer = _hiddenPlayerRenderers[i];
                    if (renderer == null) continue;
                    renderer.enabled = hidden ? false : _hiddenPlayerRendererStates[i];
                }

                int colliderCount = Math.Min(_hiddenPlayerColliders?.Length ?? 0, _hiddenPlayerColliderStates?.Length ?? 0);
                for (int i = 0; i < colliderCount; i++)
                {
                    Collider collider = _hiddenPlayerColliders[i];
                    if (collider == null) continue;
                    collider.enabled = hidden ? false : _hiddenPlayerColliderStates[i];
                }
                _playerVisualsHidden = hidden;
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Drake symbiosis player visibility switch failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void EnforcePlayerVisualsHidden()
        {
            if (!Active || _rider == null || !_playerVisualsHidden) return;
            try
            {
                // Only touch renderers captured when symbiosis started. That lets us restore their
                // exact pre-symbiosis states later instead of fighting equipment/visibility state.
                for (int i = 0; i < (_hiddenPlayerRenderers?.Length ?? 0); i++)
                {
                    Renderer renderer = _hiddenPlayerRenderers[i];
                    if (renderer != null && renderer.enabled)
                        renderer.enabled = false;
                }
                for (int i = 0; i < (_hiddenPlayerColliders?.Length ?? 0); i++)
                {
                    Collider collider = _hiddenPlayerColliders[i];
                    if (collider != null && collider.enabled)
                        collider.enabled = false;
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Drake symbiosis visibility lock failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void RestorePlayerVisuals()
        {
            try
            {
                int count = Math.Min(_hiddenPlayerRenderers?.Length ?? 0, _hiddenPlayerRendererStates?.Length ?? 0);
                for (int i = 0; i < count; i++)
                {
                    Renderer renderer = _hiddenPlayerRenderers[i];
                    if (renderer != null) renderer.enabled = _hiddenPlayerRendererStates[i];
                }
                int colliderCount = Math.Min(_hiddenPlayerColliders?.Length ?? 0, _hiddenPlayerColliderStates?.Length ?? 0);
                for (int i = 0; i < colliderCount; i++)
                {
                    Collider collider = _hiddenPlayerColliders[i];
                    if (collider != null) collider.enabled = _hiddenPlayerColliderStates[i];
                }
            }
            catch { }
            _hiddenPlayerRenderers = Array.Empty<Renderer>();
            _hiddenPlayerRendererStates = Array.Empty<bool>();
            _hiddenPlayerColliders = Array.Empty<Collider>();
            _hiddenPlayerColliderStates = Array.Empty<bool>();
            _playerVisualsHidden = false;
        }

        private static GameObject ResolveVisual(Character worker)
        {
            if (worker == null || GetVisualMethod == null) return null;
            try { return GetVisualMethod.Invoke(worker, null) as GameObject; }
            catch { return null; }
        }

        private static bool IsAttached(Character character)
        {
            if (character == null || IsAttachedMethod == null) return false;
            try
            {
                object value = IsAttachedMethod.Invoke(character, null);
                return value is bool attached && attached;
            }
            catch
            {
                return false;
            }
        }
    }
}
