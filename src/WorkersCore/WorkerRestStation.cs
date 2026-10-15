using System;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    internal enum RestStationType
    {
        Greydwarf = 0,
        Draugr = 1,
        Troll = 2,
        Golem = 3,
        Surtling = 4,
        Lox = 5,
        Drake = 6,
        Fuling = 7,
        Neck = 8,
        Wolf = 9
    }

    /// <summary>
    /// Stable, AI-agnostic contract exposed by every Worker rest piece.  The existing Worker AI
    /// deliberately does not depend on this yet; future rest navigation can discover these named
    /// transforms without knowing how the visible prefab was assembled.
    /// </summary>
    internal sealed class WorkerRestStation : MonoBehaviour
    {
        private static readonly System.Collections.Generic.HashSet<WorkerRestStation> LoadedStations =
            new System.Collections.Generic.HashSet<WorkerRestStation>();

        internal static void CopyLoaded(System.Collections.Generic.List<WorkerRestStation> destination)
        {
            if (destination == null) return;
            destination.Clear();

            foreach (WorkerRestStation station in LoadedStations)
            {
                if (station == null || station.gameObject == null || !station.gameObject.activeInHierarchy) continue;
                destination.Add(station);
            }
        }

        internal static bool HasPlacedStationWithin(Vector3 position, float radius)
        {
            float safeRadius = Mathf.Max(0.1f, radius);
            float radiusSqr = safeRadius * safeRadius;

            foreach (WorkerRestStation station in LoadedStations)
            {
                if (station == null || !WorkerRestStationFactory.IsPlacedStation(station)) continue;
                Vector3 delta = station.transform.position - position;
                delta.y = 0f;
                if (delta.sqrMagnitude <= radiusSqr) return true;
            }

            return false;
        }
        internal const string ApproachPointName = "WorkerApproachPoint";
        internal const string RestPointName = "WorkerRestPoint";
        internal const string ExitPointName = "WorkerExitPoint";

        [SerializeField] private RestStationType _stationType;
        [SerializeField] private Transform _approachPoint;
        [SerializeField] private Transform _restPoint;
        [SerializeField] private Transform _exitPoint;

        private int _backgroundRegistrationAttempts;

        private void OnEnable()
        {
            LoadedStations.Add(this);
        }

        private void Start()
        {
            LoadedStations.Add(this);
            // v0.5.356: this used to start an infinite InvokeRepeating(..., 3s) on every rest
            // station, including unassigned ones. Stations load in the same burst, so their timers
            // stayed phase-aligned and all woke on the same frame every three seconds. Registration
            // is a load-time repair only; do one immediate attempt and a few bounded settling
            // retries when an assigned station is not ready yet.
            _backgroundRegistrationAttempts = 0;
            TryRegisterAssignedWorkerForBackground();
        }

        private void OnDisable()
        {
            LoadedStations.Remove(this);
            CancelInvoke(nameof(TryRegisterAssignedWorkerForBackground));
        }

        private void OnDestroy()
        {
            LoadedStations.Remove(this);
        }

        private void TryRegisterAssignedWorkerForBackground()
        {
            CancelInvoke(nameof(TryRegisterAssignedWorkerForBackground));

            ZNetView view = GetComponent<ZNetView>() ?? GetComponentInParent<ZNetView>();
            if (view == null || !view.IsValid())
            {
                ScheduleBackgroundRegistrationRetry();
                return;
            }

            ZDO homeZdo = view.GetZDO();
            if (homeZdo == null)
            {
                ScheduleBackgroundRegistrationRetry();
                return;
            }

            // An unassigned rest station has nothing to recover. Do not leave a permanent timer
            // behind just in case it gets assigned later; assignment itself already updates the
            // durable worker/home link.
            if (!WorkerHomeAnchorZdo.IsAssigned(homeZdo)) return;

            // v0.5.331: opening a rest-station menu used to appear to "wake" Workers after a
            // world load. The menu was not the real trigger: TryOpenAssignedWorkerMenuFromHome()
            // calls WorkerHomeAssignmentRuntime.ResolveWorker(), which repairs the durable
            // Home <-> Worker ZDOID link. Do that repair here once during the load lifecycle.
            ZDO workerZdo = WorkerHomeAssignmentRuntime.ResolveWorker(homeZdo, transform.position);
            if (WorkerZdo.IsWorker(workerZdo))
            {
                WorkerSustenanceRuntime.EnsureInitialized(workerZdo);
                WorkerProvisioningLogisticsRuntime.NotifyWorkerSupplyChanged(workerZdo, false);

                WorkerState state = (WorkerState)workerZdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
                bool wakeFromReturnHome = false;

                // v0.5.439: after a save/reload a Worker can legitimately persist as ReturnHome
                // although its ZDO is already parked at the rest station. With no movement target
                // left this could look like the old "click the rest station once" problem.
                // Only treat ReturnHome as stale when the persisted Worker position is already
                // inside the station's rest radius AND no rest/sustenance gate currently requires
                // it to stay home. The next normal AI tick remains authoritative.
                if (state == WorkerState.ReturnHome)
                {
                    float restRadius = WorkerRestRuntime.GetRestDistance(workerZdo) + 1.0f;
                    bool alreadyAtHome = (workerZdo.GetPosition() - homeZdo.GetPosition()).sqrMagnitude <= restRadius * restRadius;
                    bool recovery = workerZdo.GetInt(WorkerZdo.RestReason, (int)WorkerRestReason.None) == (int)WorkerRestReason.Recovery;
                    bool hungry = WorkerSustenanceRuntime.IsHungry(workerZdo);
                    wakeFromReturnHome = alreadyAtHome && !recovery && !hungry && !WorkerRestRuntime.IsNight();
                }

                if (state == WorkerState.WaitAtHome || state == WorkerState.Idle || wakeFromReturnHome)
                {
                    WorkerZdo.ClearTarget(workerZdo);
                    workerZdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                    string from = wakeFromReturnHome ? "ReturnHome@station" : "Wait/Idle";
                    BackgroundMonitorRuntime.Note($"HOME LOAD WAKE: worker={workerZdo.m_uid}, anchor={WorkerHomeAnchorZdo.ReadAnchorKind(homeZdo)}, {from} -> FindJob");
                }
            }

            if (BackgroundWorkerScheduler.RegisterPersistentWorkerFromHome(homeZdo)) return;
            ScheduleBackgroundRegistrationRetry();
        }

        private void ScheduleBackgroundRegistrationRetry()
        {
            // The network/ZDO layer can still be settling for a few seconds after world load.
            // Bound the retries so a stale assignment can never become a permanent periodic hitch.
            _backgroundRegistrationAttempts++;
            if (_backgroundRegistrationAttempts >= 5 || !isActiveAndEnabled) return;
            float delay = 0.45f + _backgroundRegistrationAttempts * 0.55f;
            Invoke(nameof(TryRegisterAssignedWorkerForBackground), delay);
        }

        internal static WorkerRestStation ResolveNearestLoaded(RestStationType stationType, Vector3 position, float radius, long ownerPlayerId = 0L)
        {
            WorkerRestStation nearest = null;
            float nearestSqr = Mathf.Max(0.1f, radius) * Mathf.Max(0.1f, radius);

            // Local loaded-station registry only; this is intentionally not a scene/world scan.
            // Owner 0 on an old station is accepted as a compatibility fallback, but a station
            // explicitly owned by another player is never used.
            foreach (WorkerRestStation station in LoadedStations)
            {
                if (station == null || station.StationType != stationType || !WorkerRestStationFactory.IsPlacedStation(station)) continue;

                if (ownerPlayerId != 0L)
                {
                    ZNetView stationView = WorkerRestStationFactory.ResolveNetworkView(station);
                    ZDO stationZdo = stationView != null && stationView.IsValid() ? stationView.GetZDO() : null;
                    if (stationZdo != null)
                    {
                        long stationOwner = stationZdo.GetLong(WorkerHomeAnchorZdo.OwnerPlayerId, 0L);
                        if (stationOwner != 0L && stationOwner != ownerPlayerId) continue;
                    }
                }

                Vector3 delta = station.transform.position - position;
                float sqr = delta.x * delta.x + delta.z * delta.z;
                if (sqr > nearestSqr) continue;
                nearestSqr = sqr;
                nearest = station;
            }

            return nearest;
        }

        internal static WorkerRestStation ResolveLoadedForWorker(ZDO workerZdo)
        {
            if (workerZdo == null) return null;

            ZDOID homeId = WorkerZdo.ReadHomeAnchorZdoId(workerZdo);
            Vector3 persistedHome = WorkerZdo.ReadHome(workerZdo, workerZdo.GetPosition());
            string species = SpeciesRoleRegistry.NormalizePrefabName(workerZdo.GetString(WorkerZdo.SpeciesKey, string.Empty));
            WorkerRestStation nearest = null;
            float nearestSqr = 12f * 12f;

            // This is a registry of already-loaded rest stations, not a scene/world scan. A direct
            // ZDOID match is authoritative; distance/species is only a compatibility fallback for
            // old saves created before the bidirectional HomeAnchor ID was guaranteed.
            foreach (WorkerRestStation station in LoadedStations)
            {
                if (station == null || !WorkerRestStationFactory.IsPlacedStation(station)) continue;
                ZNetView view = WorkerRestStationFactory.ResolveNetworkView(station);
                ZDO stationZdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (stationZdo == null) continue;

                if (!homeId.Equals(ZDOID.None) && stationZdo.m_uid.Equals(homeId))
                    return station;

                if (!station.SupportsSpecies(species)) continue;
                float sqr = (station.transform.position - persistedHome).sqrMagnitude;
                if (sqr >= nearestSqr) continue;
                nearestSqr = sqr;
                nearest = station;
            }

            return nearest;
        }

        internal RestStationType StationType => _stationType;
        internal Transform ApproachPoint => _approachPoint;
        internal Transform RestPoint => _restPoint;
        internal Transform ExitPoint => _exitPoint;
        internal string DisplayName => GetDisplayName(_stationType);

        internal static string GetDisplayName(RestStationType stationType)
        {
            switch (stationType)
            {
                case RestStationType.Greydwarf: return "Grauzwerg-Wurzelnest";
                case RestStationType.Draugr: return "Draugr-Ruhestaette";
                case RestStationType.Troll: return "Troll-Lehnplatz";
                case RestStationType.Golem: return "Golem-Steinlager";
                case RestStationType.Surtling: return "Surtling-Ruhestaette";
                case RestStationType.Lox: return "Lox-Ruheplatz";
                case RestStationType.Drake: return "Drake-Landeplatz";
                case RestStationType.Fuling: return "Fuling-Ruhestätte";
                case RestStationType.Neck: return "Ufernest";
                case RestStationType.Wolf: return "Wolfsbau";
                default: return "Worker-Ruhestaette";
            }
        }

        internal void Configure(RestStationType stationType, Transform approachPoint, Transform restPoint, Transform exitPoint)
        {
            _stationType = stationType;
            _approachPoint = approachPoint;
            _restPoint = restPoint;
            _exitPoint = exitPoint;
        }

        internal bool SupportsSpecies(string species)
        {
            if (string.IsNullOrEmpty(species)) return false;
            switch (_stationType)
            {
                case RestStationType.Greydwarf:
                    return string.Equals(species, "Greyling", StringComparison.OrdinalIgnoreCase) ||
                           species.StartsWith("Greydwarf", StringComparison.OrdinalIgnoreCase);
                case RestStationType.Draugr:
                    return species.StartsWith("Draugr", StringComparison.OrdinalIgnoreCase);
                case RestStationType.Troll:
                    return string.Equals(species, "Troll", StringComparison.OrdinalIgnoreCase);
                case RestStationType.Golem:
                    return string.Equals(species, "StoneGolem", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(species, "IceGolem", StringComparison.OrdinalIgnoreCase);
                case RestStationType.Surtling:
                    return string.Equals(species, "Surtling", StringComparison.OrdinalIgnoreCase);
                case RestStationType.Lox:
                    return string.Equals(species, "Lox", StringComparison.OrdinalIgnoreCase);
                case RestStationType.Drake:
                    return string.Equals(species, "Drake", StringComparison.OrdinalIgnoreCase);
                case RestStationType.Fuling:
                    return species.StartsWith("Fuling", StringComparison.OrdinalIgnoreCase);
                case RestStationType.Neck:
                    return string.Equals(species, "Neck", StringComparison.OrdinalIgnoreCase);
                case RestStationType.Wolf:
                    return string.Equals(species, "Wolf", StringComparison.OrdinalIgnoreCase);
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Collider-local interaction bridge for Worker rest pieces.
    ///
    /// Valheim resolves interaction from the GameObject hit by the player's ray. Most of the
    /// custom rest pieces use child Mesh/BoxColliders while the real Worker Interactable lives on
    /// the prefab root. In that layout Player.Interact may never see the root component. The Draugr
    /// bed happened to work because its collider hierarchy resolves back to the root; other homes
    /// did not. This proxy is therefore attached directly to every child collider and forwards the
    /// interaction to the authoritative root home component.
    /// </summary>
    internal sealed class WorkerRestStationInteractionProxy : MonoBehaviour, Hoverable, Interactable
    {
        private WorkerRestStation Station => GetComponentInParent<WorkerRestStation>();

        public string GetHoverText()
        {
            object home = ResolveRootHome();
            if (home is Hoverable hoverable)
                return hoverable.GetHoverText();

            WorkerRestStation station = Station;
            return station != null
                ? station.DisplayName + "\n[<color=yellow><b>Shift+E</b></color>] Worker-Menue"
                : string.Empty;
        }

        public string GetHoverName()
        {
            object home = ResolveRootHome();
            if (home is Hoverable hoverable)
                return hoverable.GetHoverName();

            WorkerRestStation station = Station;
            return station != null ? station.DisplayName : "Worker-Ruhestaette";
        }

        public float GetHoverOffset() => 0f;

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold) return false;

            WorkerRestStation station = Station;
            Plugin plugin = Plugin.Instance;
            if (station == null || plugin == null) return false;

            bool menuRequest = alt || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (menuRequest)
            {
                ZNetView homeView = WorkerRestStationFactory.ResolveNetworkView(station);
                return plugin.TryOpenAssignedWorkerMenuFromHome(
                    homeView,
                    station.transform.position,
                    user,
                    GetHoverName());
            }

            // Normal E keeps the existing species-specific assignment/follow-home behavior.
            object home = ResolveRootHome();
            if (home is Interactable interactable && !ReferenceEquals(interactable, this))
                return interactable.Interact(user, false, false);

            return false;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item)
        {
            object home = ResolveRootHome();
            if (home is Interactable interactable && !ReferenceEquals(interactable, this))
                return interactable.UseItem(user, item);
            return false;
        }

        private object ResolveRootHome()
        {
            WorkerRestStation station = Station;
            if (station == null) return null;
            GameObject root = station.gameObject;

            // Hunter stand must be checked before the generic Draugr home because both use the
            // Draugr station family, although only one component is normally present on a prefab.
            HunterStandHomeInteractable hunter = root.GetComponent<HunterStandHomeInteractable>();
            if (hunter != null) return hunter;
            DraugrHomeInteractable draugr = root.GetComponent<DraugrHomeInteractable>();
            if (draugr != null) return draugr;
            GreydwarfHomeNestInteractable greydwarf = root.GetComponent<GreydwarfHomeNestInteractable>();
            if (greydwarf != null) return greydwarf;
            TrollHomeCampInteractable troll = root.GetComponent<TrollHomeCampInteractable>();
            if (troll != null) return troll;
            StoneGolemHomeInteractable golem = root.GetComponent<StoneGolemHomeInteractable>();
            if (golem != null) return golem;
            LoxHomeInteractable lox = root.GetComponent<LoxHomeInteractable>();
            if (lox != null) return lox;
            DrakeHomeInteractable drake = root.GetComponent<DrakeHomeInteractable>();
            if (drake != null) return drake;
            SurtlingHomeInteractable surtling = root.GetComponent<SurtlingHomeInteractable>();
            if (surtling != null) return surtling;
            FulingHomeInteractable fuling = root.GetComponent<FulingHomeInteractable>();
            if (fuling != null) return fuling;
            NeckHomeInteractable neck = root.GetComponent<NeckHomeInteractable>();
            if (neck != null) return neck;
            WolfHomeInteractable wolf = root.GetComponent<WolfHomeInteractable>();
            if (wolf != null) return wolf;

            return null;
        }
    }

    internal static class WorkerRestStationFactory
    {
        private static readonly System.Collections.Generic.List<WorkerRestStation> LoadedStationScratch = new System.Collections.Generic.List<WorkerRestStation>(32);
        private static readonly System.Reflection.FieldInfo PlacementGhostField =
            AccessTools.Field(typeof(Player), "m_placementGhost");

        internal static bool IsPlacementGhost(GameObject candidate)
        {
            if (candidate == null) return false;

            try
            {
                Player player = Player.m_localPlayer;
                GameObject ghost = player != null && PlacementGhostField != null
                    ? PlacementGhostField.GetValue(player) as GameObject
                    : null;
                if (ghost == null) return false;

                Transform candidateTransform = candidate.transform;
                Transform ghostTransform = ghost.transform;
                return candidate == ghost ||
                       candidateTransform.IsChildOf(ghostTransform) ||
                       ghostTransform.IsChildOf(candidateTransform);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsPlacedStation(WorkerRestStation station)
        {
            if (station == null || station.gameObject == null) return false;
            if (!station.gameObject.scene.IsValid() || !station.gameObject.activeInHierarchy) return false;
            if (IsPlacementGhost(station.gameObject)) return false;

            ZNetView view = ResolveNetworkView(station);
            return view != null && view.IsValid() && view.GetZDO() != null;
        }

        internal static bool IsPlacedHomeComponent(Component component)
        {
            if (component == null || component.gameObject == null) return false;
            if (IsPlacementGhost(component.gameObject)) return false;

            WorkerRestStation station = component.GetComponent<WorkerRestStation>() ??
                                        component.GetComponentInParent<WorkerRestStation>();
            if (station != null) return IsPlacedStation(station);

            ZNetView view = component.GetComponent<ZNetView>() ?? component.GetComponentInParent<ZNetView>();
            return view != null && view.IsValid() && view.GetZDO() != null;
        }
        internal static WorkerRestStation Attach(
            GameObject prefab,
            RestStationType stationType,
            Vector3 approachPosition,
            Vector3 restPosition,
            Vector3 exitPosition,
            Vector3 restEuler,
            Transform existingRestAnchor = null)
        {
            if (prefab == null) return null;

            Transform approach = FindOrCreate(prefab.transform, WorkerRestStation.ApproachPointName, approachPosition, Vector3.zero);
            Transform rest = FindOrCreate(
                existingRestAnchor ?? prefab.transform,
                WorkerRestStation.RestPointName,
                existingRestAnchor != null ? Vector3.zero : restPosition,
                existingRestAnchor != null ? Vector3.zero : restEuler);
            Transform exit = FindOrCreate(prefab.transform, WorkerRestStation.ExitPointName, exitPosition, Vector3.zero);

            // The approach/exit points face the station.  RestPoint.forward is the canonical look direction.
            FaceLocalTarget(approach, restPosition);
            FaceLocalTarget(exit, restPosition);

            WorkerRestStation station = prefab.GetComponent<WorkerRestStation>();
            if (station == null) station = prefab.AddComponent<WorkerRestStation>();
            station.Configure(stationType, approach, rest, exit);
            RestStationUnlockRuntime.RegisterPiece(prefab, stationType);
            EnsurePlayerSleepFireAura(prefab);
            EnsureInteractionProxies(prefab);
            return station;
        }


        /// <summary>
        /// Makes Worker rest stations count as a nearby fire for Valheim's player sleep/rest checks.
        /// This deliberately does not add Fireplace fuel gameplay, interactions or resource use; it
        /// is only the vanilla EffectArea.Fire aura. Worker homes can therefore replace the extra
        /// campfire normally required next to the player's bed without becoming refillable fires.
        /// </summary>
        private static void EnsurePlayerSleepFireAura(GameObject prefab)
        {
            if (prefab == null) return;

            // v0.5.34: Do not synthesize EffectArea components on Worker prefabs.
            // Current Valheim EffectArea.Awake expects vanilla-initialized references and a
            // hand-created component can throw a NullReferenceException whenever ZNetScene
            // instantiates the saved object. Bed.CheckFire is already handled safely by
            // PlayerBedFireRuntime, so the synthetic aura is unnecessary.
            const string auraName = "Workers_PlayerSleepFireAura";
            Transform existing = prefab.transform.Find(auraName);
            if (existing == null) return;

            try
            {
                existing.gameObject.SetActive(false);
                UnityEngine.Object.DestroyImmediate(existing.gameObject);
            }
            catch
            {
                // Fail-soft. Disabling it above is enough to prevent EffectArea.Awake.
            }
        }

        internal static void EnsurePlacementCollider(GameObject prefab, Vector3 center, Vector3 size)
        {
            if (prefab == null) return;
            Transform existing = prefab.transform.Find("Workers_RestStationPlacementCollider");
            GameObject holder = existing != null ? existing.gameObject : new GameObject("Workers_RestStationPlacementCollider");
            holder.transform.SetParent(prefab.transform, false);
            holder.transform.localPosition = Vector3.zero;
            holder.layer = prefab.layer;

            BoxCollider collider = holder.GetComponent<BoxCollider>();
            if (collider == null) collider = holder.AddComponent<BoxCollider>();
            collider.center = center;
            collider.size = size;
            collider.isTrigger = false;

            EnsureInteractionProxy(holder, prefab);
        }

        /// <summary>
        /// Adds a small invisible, level physical surface below an authored rest point.  This is
        /// intentionally separate from the placement collider: large creatures (especially the
        /// Troll) use native ground/foot IK while idling, and uneven terrain under the two feet can
        /// make that IK alternate between slightly different ground heights every frame.  A flat
        /// support surface gives Valheim's own ground probes one deterministic plane without
        /// changing Worker navigation or the visible furniture.
        /// </summary>
        internal static void EnsureRestSupportCollider(GameObject prefab, Vector3 center, Vector3 size)
        {
            if (prefab == null) return;

            Transform existing = prefab.transform.Find("Workers_RestStationFlatSupport");
            GameObject holder = existing != null ? existing.gameObject : new GameObject("Workers_RestStationFlatSupport");
            holder.transform.SetParent(prefab.transform, false);
            holder.transform.localPosition = Vector3.zero;
            holder.transform.localRotation = Quaternion.identity;
            holder.transform.localScale = Vector3.one;
            holder.layer = prefab.layer;

            BoxCollider collider = holder.GetComponent<BoxCollider>();
            if (collider == null) collider = holder.AddComponent<BoxCollider>();
            collider.center = center;
            collider.size = size;
            collider.isTrigger = false;

            // No interaction proxy here.  This collider is deliberately floor-like and should not
            // steal E/Shift+E rays from the actual rest-station interaction geometry.
        }

        private static void EnsureInteractionProxies(GameObject prefab)
        {
            if (prefab == null) return;
            Collider[] colliders = prefab.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null) continue;
                EnsureInteractionProxy(collider.gameObject, prefab);
            }
        }

        private static void EnsureInteractionProxy(GameObject target, GameObject prefabRoot)
        {
            if (target == null || prefabRoot == null || target == prefabRoot) return;
            if (target.GetComponent<WorkerRestStationInteractionProxy>() == null)
                target.AddComponent<WorkerRestStationInteractionProxy>();
        }

        internal static ZNetView ResolveNetworkView(WorkerRestStation station)
        {
            if (station == null) return null;
            ZNetView view = station.GetComponent<ZNetView>();
            if (view == null) view = station.GetComponentInParent<ZNetView>();
            if (view == null) view = station.GetComponentInChildren<ZNetView>(true);
            if (view != null) return view;

            Piece piece = station.GetComponent<Piece>() ?? station.GetComponentInParent<Piece>();
            if (piece != null)
            {
                view = piece.GetComponent<ZNetView>() ?? piece.GetComponentInParent<ZNetView>() ?? piece.GetComponentInChildren<ZNetView>(true);
                if (view != null) return view;
            }
            return null;
        }

        internal static bool TryOpenMenuFromAim(Player player)
        {
            if (player == null || Plugin.Instance == null) return false;
            Camera camera = Camera.main;
            if (camera == null) return false;

            const float maxDistance = 8f;
            Ray ray = new Ray(camera.transform.position, camera.transform.forward);
            RaycastHit[] hits = Physics.RaycastAll(ray, maxDistance, ~0, QueryTriggerInteraction.Collide);
            if (hits != null && hits.Length > 0)
            {
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider collider = hits[i].collider;
                    if (collider == null) continue;
                    WorkerRestStation station = collider.GetComponent<WorkerRestStation>() ?? collider.GetComponentInParent<WorkerRestStation>();
                    if (station != null && IsPlacedStation(station))
                        return OpenStationMenu(station, player);

                    // Do not let the cone fallback steal Shift+E from a Worker or logistics chest
                    // the player is actually aiming at.
                    if (i == 0 && (collider.GetComponentInParent<Character>() != null || collider.GetComponentInParent<Container>() != null))
                        return false;
                }
            }

            // Some decorative rest pieces have renderer-only geometry and a placement collider that
            // can sit below the crosshair. On an explicit Shift+E key press, use a tiny look-cone
            // fallback around the camera so the rest station remains a reliable management console.
            WorkerRestStation.CopyLoaded(LoadedStationScratch);
            WorkerRestStation best = null;
            float bestScore = float.MaxValue;
            Vector3 origin = camera.transform.position;
            Vector3 forward = camera.transform.forward;
            for (int i = 0; i < LoadedStationScratch.Count; i++)
            {
                WorkerRestStation station = LoadedStationScratch[i];
                if (!IsPlacedStation(station)) continue;
                Vector3 to = station.RestPoint != null ? station.RestPoint.position - origin : station.transform.position - origin;
                float distance = to.magnitude;
                if (distance < 0.2f || distance > maxDistance) continue;
                float dot = Vector3.Dot(forward, to / distance);
                if (dot < 0.88f) continue;
                float score = distance + (1f - dot) * 20f;
                if (score >= bestScore) continue;
                bestScore = score;
                best = station;
            }
            bool opened = best != null && OpenStationMenu(best, player);
            LoadedStationScratch.Clear();
            return opened;
        }

        private static bool OpenStationMenu(WorkerRestStation station, Player player)
        {
            if (station == null || player == null || Plugin.Instance == null || !IsPlacedStation(station)) return false;
            ZNetView view = ResolveNetworkView(station);
            return Plugin.Instance.TryOpenAssignedWorkerMenuFromHome(view, station.transform.position, player, station.DisplayName);
        }

        private static Transform FindOrCreate(Transform parent, string name, Vector3 position, Vector3 euler)
        {
            Transform child = parent.Find(name);
            if (child == null)
            {
                GameObject point = new GameObject(name);
                child = point.transform;
                child.SetParent(parent, false);
            }
            child.localPosition = position;
            child.localRotation = Quaternion.Euler(euler);
            child.localScale = Vector3.one;
            return child;
        }

        private static void FaceLocalTarget(Transform point, Vector3 localTarget)
        {
            if (point == null) return;
            Vector3 direction = localTarget - point.localPosition;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.001f)
                point.localRotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        }
    }

    /// <summary>
    /// Reliable Shift+E choke point for Worker rest pieces. A placed rest prefab can be hit through
    /// one of its child/placement colliders, in which case Valheim may never call the custom root
    /// Interactable. Resolve WorkerRestStation upward from the actually hit GameObject and own the
    /// alternate interaction before vanilla consumes it.
    /// </summary>
    [HarmonyPatch(typeof(Player), "Interact", typeof(GameObject), typeof(bool), typeof(bool))]
    internal static class WorkerRestStationPlayerInteractPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Player __instance, GameObject go, bool hold, bool alt)
        {
            try
            {
                if (__instance == null || __instance != Player.m_localPlayer || go == null || hold) return true;

                bool alternateInteract = alt || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (!alternateInteract) return true;

                WorkerRestStation station = go.GetComponent<WorkerRestStation>() ?? go.GetComponentInParent<WorkerRestStation>();
                ZNetView homeView;
                Vector3 homePosition;
                string homeLabel;

                if (station != null)
                {
                    if (!WorkerRestStationFactory.IsPlacedStation(station)) return true;
                    homeView = WorkerRestStationFactory.ResolveNetworkView(station);
                    homePosition = station.transform.position;
                    homeLabel = station.DisplayName;
                }
                else if (!TryResolveLegacyHome(go, out homeView, out homePosition, out homeLabel))
                {
                    return true;
                }

                Plugin plugin = Plugin.Instance;
                if (plugin == null) return true;

                // Surtling campfires are special: Shift+E is both the assignment action
                // for a free hearth and the worker-menu action for an occupied hearth.
                // The generic rest-station menu path cannot assign an unoccupied home,
                // so route this piece through its species-specific interaction first.
                SurtlingHomeInteractable surtlingHome =
                    go.GetComponent<SurtlingHomeInteractable>() ??
                    go.GetComponentInParent<SurtlingHomeInteractable>();
                if (surtlingHome == null && station != null)
                    surtlingHome = station.GetComponent<SurtlingHomeInteractable>() ??
                                   station.GetComponentInParent<SurtlingHomeInteractable>();

                bool handled = surtlingHome != null
                    ? plugin.TryInteractWithSurtlingHome(surtlingHome, __instance, true)
                    : plugin.TryOpenAssignedWorkerMenuFromHome(
                        homeView,
                        homePosition,
                        __instance,
                        homeLabel);

                // When Workers handled the rest-station Shift+E, stop Player.Interact here so a
                // vanilla Piece/Chair/Bed interaction cannot consume the same key press afterwards.
                return !handled;
            }
            catch
            {
                // Fail open: a broken Workers interaction must never make the underlying piece unusable.
                return true;
            }
        }

        private static bool TryResolveLegacyHome(GameObject go, out ZNetView homeView, out Vector3 homePosition, out string homeLabel)
        {
            homeView = null;
            homePosition = Vector3.zero;
            homeLabel = string.Empty;
            if (go == null) return false;

            GreydwarfHomeNestInteractable greydwarf = go.GetComponent<GreydwarfHomeNestInteractable>() ?? go.GetComponentInParent<GreydwarfHomeNestInteractable>();
            if (greydwarf != null) return Resolve(greydwarf.GetNetworkView(), greydwarf.transform.position, greydwarf.GetHoverName(), out homeView, out homePosition, out homeLabel);

            TrollHomeCampInteractable troll = go.GetComponent<TrollHomeCampInteractable>() ?? go.GetComponentInParent<TrollHomeCampInteractable>();
            if (troll != null) return Resolve(troll.GetNetworkView(), troll.transform.position, troll.GetHoverName(), out homeView, out homePosition, out homeLabel);

            StoneGolemHomeInteractable golem = go.GetComponent<StoneGolemHomeInteractable>() ?? go.GetComponentInParent<StoneGolemHomeInteractable>();
            if (golem != null) return Resolve(golem.GetNetworkView(), golem.transform.position, golem.GetHoverName(), out homeView, out homePosition, out homeLabel);

            DraugrHomeInteractable draugr = go.GetComponent<DraugrHomeInteractable>() ?? go.GetComponentInParent<DraugrHomeInteractable>();
            if (draugr != null) return Resolve(draugr.GetNetworkView(), draugr.transform.position, draugr.GetHoverName(), out homeView, out homePosition, out homeLabel);

            HunterStandHomeInteractable hunter = go.GetComponent<HunterStandHomeInteractable>() ?? go.GetComponentInParent<HunterStandHomeInteractable>();
            if (hunter != null) return Resolve(hunter.GetNetworkView(), hunter.transform.position, hunter.GetHoverName(), out homeView, out homePosition, out homeLabel);

            LoxHomeInteractable lox = go.GetComponent<LoxHomeInteractable>() ?? go.GetComponentInParent<LoxHomeInteractable>();
            if (lox != null) return Resolve(lox.GetNetworkView(), lox.transform.position, lox.GetHoverName(), out homeView, out homePosition, out homeLabel);

            DrakeHomeInteractable drake = go.GetComponent<DrakeHomeInteractable>() ?? go.GetComponentInParent<DrakeHomeInteractable>();
            if (drake != null) return Resolve(drake.GetNetworkView(), drake.transform.position, drake.GetHoverName(), out homeView, out homePosition, out homeLabel);

            SurtlingHomeInteractable surtling = go.GetComponent<SurtlingHomeInteractable>() ?? go.GetComponentInParent<SurtlingHomeInteractable>();
            if (surtling != null) return Resolve(surtling.GetNetworkView(), surtling.transform.position, surtling.GetHoverName(), out homeView, out homePosition, out homeLabel);

            return false;
        }

        private static bool Resolve(ZNetView view, Vector3 position, string label, out ZNetView homeView, out Vector3 homePosition, out string homeLabel)
        {
            homeView = view;
            homePosition = position;
            homeLabel = string.IsNullOrEmpty(label) ? "Worker-Ruhestaette" : label;
            return homeView != null;
        }
    }

}
