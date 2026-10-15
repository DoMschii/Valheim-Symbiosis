using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// v0.5.410 Surtling recruitment ritual.
    /// A dedicated player-built "Praechtiges Signalfeuer" must stand near a natural FireHole
    /// Surtling respawner, be protected by a player-built roof and remain lit with ordinary Wood
    /// for several Valheim days. Burning under cover builds ritual respect; letting the fire go
    /// out or leaving it exposed drains progress again. At 100% the linked geyser is permanently
    /// pacified and one Surtling becomes a Firekeeper. The signal fire itself is NOT a home;
    /// the recruited Surtling must later be assigned to the separate small Surtling-Ruhestaette.
    /// </summary>
    internal sealed class SurtlingSignalFireRecruitmentRuntime : MonoBehaviour
    {
        internal const string ProgressKey = "vw_surtling_signal_progress";
        internal const string CompleteKey = "vw_surtling_signal_complete";
        internal const string WorkerCreatedKey = "vw_surtling_signal_worker_created";
        internal const string HasGeyserKey = "vw_surtling_signal_has_geyser";
        internal const string HasRoofKey = "vw_surtling_signal_has_roof";
        internal const string BoundSpawnerKey = "vw_surtling_signal_spawner";
        internal const string BoundSpawnerXKey = "vw_surtling_signal_spawner_x";
        internal const string BoundSpawnerYKey = "vw_surtling_signal_spawner_y";
        internal const string BoundSpawnerZKey = "vw_surtling_signal_spawner_z";
        internal const string LastStageKey = "vw_surtling_signal_stage";
        internal const string GeyserPacifiedKey = "vw_surtling_geyser_pacified";
        internal const string AdditionalSummonCountKey = "vw_surtling_signal_extra_summons";

        internal const int AdditionalSummonCoreCost = 10;
        internal const float RequiredBurnDays = 3f;
        internal const float FallbackDayLengthSeconds = 1800f;
        internal const float ExtinguishDecayMultiplier = 2f;
        internal const float RoofCheckHeight = 12f;
        internal const float GeyserRadius = 30f;
        internal const float FireHoleClusterRadius = 15f;
        internal const float ExistingSurtlingRadius = 18f;

        private const float TickSeconds = 1f;
        private static readonly HashSet<SpawnArea> LoadedSurtlingSpawners = new HashSet<SpawnArea>();
        private static readonly HashSet<CreatureSpawner> LoadedSurtlingCreatureSpawners = new HashSet<CreatureSpawner>();
        private static readonly HashSet<SurtlingSignalFireRecruitmentRuntime> LoadedSignalFires = new HashSet<SurtlingSignalFireRecruitmentRuntime>();
        private static readonly List<SpawnArea> SpawnerAreaScratch = new List<SpawnArea>(32);
        private static readonly List<CreatureSpawner> CreatureSpawnerScratch = new List<CreatureSpawner>(32);
        private static readonly MethodInfo IsBurningMethod = typeof(Fireplace).GetMethod(
            "IsBurning",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);
        private static readonly FieldInfo FireplaceMaxFuelField = typeof(Fireplace).GetField(
            "m_maxFuel",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo ClaimOwnershipMethod = typeof(ZNetView).GetMethod(
            "ClaimOwnership",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);

        private static bool _forwardingVanillaInteraction;

        private ZNetView _view;
        private Fireplace _fireplace;
        private Piece _piece;
        private float _nextTick;
        private float _lastTickTime;
        private Component _cachedSpawner;
        private bool _completedMaintenanceApplied;
        private readonly RaycastHit[] _roofHits = new RaycastHit[16];

        internal static bool ForwardingVanillaInteraction => _forwardingVanillaInteraction;

        private void Awake()
        {
            _view = GetComponent<ZNetView>() ?? GetComponentInParent<ZNetView>();
            _fireplace = GetComponent<Fireplace>() ?? GetComponentInChildren<Fireplace>(true);
            _piece = GetComponent<Piece>() ?? GetComponentInParent<Piece>();
            LoadedSignalFires.Add(this);
            _lastTickTime = Time.time;
            _nextTick = Time.time + 0.5f;
        }

        private void OnDestroy()
        {
            LoadedSignalFires.Remove(this);
        }

        private void Update()
        {
            if (Time.time < _nextTick) return;
            _nextTick = Time.time + TickSeconds;

            float now = Time.time;
            float dt = Mathf.Clamp(now - _lastTickTime, 0.1f, 3f);
            _lastTickTime = now;
            TickRitual(dt);
        }

        private void TickRitual(float dt)
        {
            if (_view == null || !_view.IsValid()) return;
            ZDO zdo = _view.GetZDO();
            if (zdo == null) return;

            Component spawner = ResolveBoundOrNearestSpawner(zdo, transform.position, GeyserRadius);
            bool hasGeyser = spawner != null;
            if (_view.IsOwner()) zdo.Set(HasGeyserKey, hasGeyser);

            bool complete = zdo.GetBool(CompleteKey, false);
            if (complete)
            {
                // v0.5.450: a completed signal fire used to repeat spawner-cluster pacification
                // and worker-existence checks every second forever. Re-apply suppression once
                // after load, ensure the initial worker exists, then make this component dormant.
                if (!_completedMaintenanceApplied)
                {
                    if (spawner != null) PacifySpawnerCluster(spawner);
                    _completedMaintenanceApplied = true;
                }

                TryEnsureWorker(zdo, spawner);
                if (zdo.GetBool(WorkerCreatedKey, false))
                    enabled = false;
                return;
            }

            if (!_view.IsOwner()) return;

            float requiredBurnSeconds = GetRequiredBurnSeconds();
            float progress = Mathf.Clamp(zdo.GetFloat(ProgressKey, 0f), 0f, requiredBurnSeconds);
            bool burning = IsBurning(_fireplace);
            bool hasRoof = HasBuiltRoof();
            zdo.Set(HasRoofKey, hasRoof);

            if (hasGeyser && burning && hasRoof)
                progress = Mathf.Min(requiredBurnSeconds, progress + dt);
            else if (progress > 0f && (!burning || !hasRoof))
                progress = Mathf.Max(0f, progress - dt * ExtinguishDecayMultiplier);

            zdo.Set(ProgressKey, progress);
            AnnounceMilestone(zdo, progress, requiredBurnSeconds, hasGeyser, burning, hasRoof);

            if (progress + 0.01f < requiredBurnSeconds) return;

            zdo.Set(ProgressKey, requiredBurnSeconds);
            zdo.Set(CompleteKey, true);
            if (spawner != null)
            {
                ZNetView sv = GetSpawnerView(spawner);
                ZDO sz = sv != null && sv.IsValid() ? sv.GetZDO() : null;
                if (sz != null) zdo.Set(BoundSpawnerKey, sz.m_uid);
                Vector3 anchor = GetRitualAnchorPosition(spawner);
                zdo.Set(BoundSpawnerXKey, anchor.x);
                zdo.Set(BoundSpawnerYKey, anchor.y);
                zdo.Set(BoundSpawnerZKey, anchor.z);
                PacifySpawnerCluster(spawner);
                PacifyNearbySurtlings(GetRitualAnchorPosition(spawner), null);
            }

            NotifyLocalCreator("Das Feuer wurde anerkannt. Der Feuergeysir ist befriedet — ein Surtling schliesst sich dir an.");
            Plugin.Log?.LogInfo($"Surtling signal-fire ritual completed at {transform.position}. Geyser={(spawner != null ? spawner.gameObject.name : "missing")}.");
            TryEnsureWorker(zdo, spawner);
        }

        private void TryEnsureWorker(ZDO signalZdo, Component spawner)
        {
            if (signalZdo == null || signalZdo.GetBool(WorkerCreatedKey, false)) return;

            long creator = 0L;
            try { creator = _piece != null ? _piece.GetCreator() : 0L; } catch { }
            Player local = Player.m_localPlayer;
            if (creator == 0L && local != null) creator = local.GetPlayerID();
            // Recruitment is executed by the network owner of the signal fire, so dedicated/hosted
            // worlds do not require the builder's Player object to be locally loaded at 100%.
            if (creator == 0L || _view == null || !_view.IsOwner()) return;

            Vector3 recruitCenter = spawner != null ? GetRitualAnchorPosition(spawner) : transform.position;
            Character surtling = FindNearestNaturalSurtling(recruitCenter, ExistingSurtlingRadius);
            ZNetView workerView = surtling != null ? (surtling.GetComponent<ZNetView>() ?? surtling.GetComponentInParent<ZNetView>()) : null;
            ZDO workerZdo = workerView != null && workerView.IsValid() ? workerView.GetZDO() : null;

            // A natural Surtling may currently be owned by another peer. Do not mutate foreign ZDOs;
            // spawn one authoritative ritual Surtling instead and leave the ambient creature peaceful.
            if (surtling == null || workerZdo == null || !workerView.IsOwner())
            {
                surtling = SpawnSurtling(recruitCenter);
                if (surtling == null) return;
                workerView = surtling.GetComponent<ZNetView>() ?? surtling.GetComponentInParent<ZNetView>();
                workerZdo = workerView != null && workerView.IsValid() ? workerView.GetZDO() : null;
                if (workerZdo == null || !workerView.IsOwner()) return;
            }

            Player followPlayer = local != null && local.GetPlayerID() == creator ? local : null;
            NaturalRecruitmentCompletionRuntime.Complete(
                surtling,
                workerZdo,
                creator,
                WorkerRole.Firekeeper,
                "Surtling",
                surtling.transform.position,
                30f,
                minimumDefensive: false,
                followPlayer: followPlayer);
            signalZdo.Set(WorkerCreatedKey, true);

            if (spawner != null) PacifyNearbySurtlings(GetRitualAnchorPosition(spawner), surtling);
            _completedMaintenanceApplied = true;
            enabled = false;
            NotifyLocalCreator("Surtling rekrutiert: Feuerwart. Er folgt dir bis du ihm eine kleine Surtling-Ruhestaette zuweist.");
            Plugin.Log?.LogInfo($"Surtling recruited by signal fire. Worker={workerZdo.m_uid}, signal={signalZdo.m_uid}, owner={creator}. Home remains unassigned until the player uses the small rest fire.");
        }

        private Character SpawnSurtling(Vector3 center)
        {
            try
            {
                if (ZNetScene.instance == null) return null;
                GameObject prefab = ZNetScene.instance.GetPrefab("Surtling");
                if (prefab == null) return null;
                Vector3 pos = center + new Vector3(2f, 0.45f, 0f);
                GameObject instance = Instantiate(prefab, pos, Quaternion.identity);
                return instance != null
                    ? (instance.GetComponent<Character>() ?? instance.GetComponentInChildren<Character>())
                    : null;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("Surtling signal-fire spawn failed: " + ex.GetBaseException().Message);
                return null;
            }
        }

        private static Character FindNearestNaturalSurtling(Vector3 center, float radius)
        {
            Character best = null;
            float bestSqr = radius * radius;
            List<Character> candidates = WorkerCharacterIndexRuntime.GetUntamedNonPlayers();
            for (int i = 0; i < candidates.Count; i++)
            {
                Character character = candidates[i];
                if (character == null || character.IsDead()) continue;
                if (!string.Equals(SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name), "Surtling", StringComparison.OrdinalIgnoreCase)) continue;
                ZNetView view = character.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (WorkerZdo.IsWorker(zdo)) continue;
                float sqr = (character.transform.position - center).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = character;
            }
            return best;
        }

        private static void PacifyNearbySurtlings(Vector3 center, Character recruited)
        {
            List<Character> candidates = WorkerCharacterIndexRuntime.GetUntamedNonPlayers();
            for (int i = 0; i < candidates.Count; i++)
            {
                Character character = candidates[i];
                if (character == null || character == recruited || character.IsDead()) continue;
                if (!string.Equals(SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name), "Surtling", StringComparison.OrdinalIgnoreCase)) continue;
                if ((character.transform.position - center).sqrMagnitude > ExistingSurtlingRadius * ExistingSurtlingRadius) continue;
                ZNetView view = character.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (WorkerZdo.IsWorker(zdo)) continue;
                try
                {
                    character.SetTamed(true);
                    WorkerCombatStanceRuntime.ClearWorkAttackTargets(character.GetComponent<MonsterAI>());
                }
                catch { }
            }
        }

        private Component ResolveBoundOrNearestSpawner(ZDO signalZdo, Vector3 center, float radius)
        {
            if (_cachedSpawner != null)
                return _cachedSpawner;

            if (signalZdo != null && ZDOMan.instance != null)
            {
                ZDOID stored = signalZdo.GetZDOID(BoundSpawnerKey);
                if (!stored.Equals(ZDOID.None))
                {
                    ZDO target = ZDOMan.instance.GetZDO(stored);
                    if (target != null)
                    {
                        foreach (SpawnArea candidate in LoadedSurtlingSpawners)
                        {
                            if (candidate == null) continue;
                            ZNetView sv = GetSpawnerView(candidate);
                            ZDO sz = sv != null && sv.IsValid() ? sv.GetZDO() : null;
                            if (sz != null && sz.m_uid.Equals(stored))
                            {
                                _cachedSpawner = candidate;
                                return candidate;
                            }
                        }
                        foreach (CreatureSpawner candidate in LoadedSurtlingCreatureSpawners)
                        {
                            if (candidate == null) continue;
                            ZNetView sv = GetSpawnerView(candidate);
                            ZDO sz = sv != null && sv.IsValid() ? sv.GetZDO() : null;
                            if (sz != null && sz.m_uid.Equals(stored))
                            {
                                _cachedSpawner = candidate;
                                return candidate;
                            }
                        }
                    }
                }
            }

            Component nearest = FindNearestSpawner(center, radius);
            if (nearest != null)
                _cachedSpawner = nearest;

            if (nearest != null && signalZdo != null && _view != null && _view.IsOwner())
            {
                ZNetView sv = GetSpawnerView(nearest);
                ZDO sz = sv != null && sv.IsValid() ? sv.GetZDO() : null;
                if (sz != null) signalZdo.Set(BoundSpawnerKey, sz.m_uid);
                Vector3 anchor = GetRitualAnchorPosition(nearest);
                signalZdo.Set(BoundSpawnerXKey, anchor.x);
                signalZdo.Set(BoundSpawnerYKey, anchor.y);
                signalZdo.Set(BoundSpawnerZKey, anchor.z);
            }
            return nearest;
        }

        internal static void RegisterSpawner(SpawnArea spawner)
        {
            if (spawner == null || !IsSurtlingSpawner(spawner)) return;
            LoadedSurtlingSpawners.Add(spawner);
        }

        internal static void RegisterCreatureSpawner(CreatureSpawner spawner)
        {
            if (spawner == null || !IsSurtlingCreatureSpawner(spawner)) return;
            LoadedSurtlingCreatureSpawners.Add(spawner);
        }

        internal static bool IsSurtlingSpawner(SpawnArea spawner)
        {
            return spawner != null && IsSurtlingSpawnerTransform(spawner.transform);
        }

        internal static bool IsSurtlingCreatureSpawner(CreatureSpawner spawner)
        {
            return spawner != null && IsSurtlingSpawnerTransform(spawner.transform);
        }

        private static bool IsSurtlingSpawnerTransform(Transform start)
        {
            if (start == null) return false;

            // FireHole uses CreatureSpawner spawn points in current Valheim builds, while older
            // implementations and custom worlds may expose SpawnArea. Identify either form from
            // the local hierarchy instead of assuming one component type.
            Transform t = start;
            for (int i = 0; i < 12 && t != null; i++, t = t.parent)
            {
                string n = NormalizeSceneName(t.gameObject != null ? t.gameObject.name : string.Empty);
                string lower = n.ToLowerInvariant();
                if (string.Equals(n, "Spawner_imp_respawn", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(n, "Spawner_Surtling", StringComparison.OrdinalIgnoreCase) ||
                    lower.Contains("firehole") || lower.Contains("fire_hole") ||
                    (lower.Contains("spawner") && (lower.Contains("surtling") || lower.Contains("imp"))))
                    return true;
            }
            return false;
        }

        private static string NormalizeSceneName(string name)
        {
            string n = name ?? string.Empty;
            if (n.EndsWith("(Clone)", StringComparison.Ordinal)) n = n.Substring(0, n.Length - 7);
            return n.Trim();
        }

        private static Vector3 GetRitualAnchorPosition(Component spawner)
        {
            if (spawner == null) return Vector3.zero;
            Transform t = spawner.transform;
            for (int i = 0; i < 12 && t != null; i++, t = t.parent)
            {
                string lower = NormalizeSceneName(t.gameObject != null ? t.gameObject.name : string.Empty).ToLowerInvariant();
                if (lower.Contains("firehole") || lower.Contains("fire_hole")) return t.position;
            }
            return spawner.transform.position;
        }

        private static void RefreshLoadedSurtlingSpawners(Vector3 center, float radius)
        {
            // v0.5.452: use the shared lifecycle index rather than repeated
            // Resources.FindObjectsOfTypeAll<SpawnArea/CreatureSpawner>() discovery.
            WorkerSpawnerIndexRuntime.CopySpawnAreasInRadius(center, radius, SpawnerAreaScratch);
            for (int i = 0; i < SpawnerAreaScratch.Count; i++)
            {
                SpawnArea candidate = SpawnerAreaScratch[i];
                if (candidate != null && IsSurtlingSpawner(candidate))
                    LoadedSurtlingSpawners.Add(candidate);
            }
            SpawnerAreaScratch.Clear();

            WorkerSpawnerIndexRuntime.CopyCreatureSpawnersInRadius(center, radius, CreatureSpawnerScratch);
            for (int i = 0; i < CreatureSpawnerScratch.Count; i++)
            {
                CreatureSpawner candidate = CreatureSpawnerScratch[i];
                if (candidate != null && IsSurtlingCreatureSpawner(candidate))
                    LoadedSurtlingCreatureSpawners.Add(candidate);
            }
            CreatureSpawnerScratch.Clear();
        }

        private static Component FindNearestSpawner(Vector3 center, float radius)
        {
            Component best = FindNearestRegisteredSpawner(center, radius);
            if (best != null) return best;

            // v0.5.453: Pass 8 now refreshes Surtling spawners through the
            // bounded shared spawner index using the active search area.
            RefreshLoadedSurtlingSpawners(center, radius);
            return FindNearestRegisteredSpawner(center, radius);
        }

        private static Component FindNearestRegisteredSpawner(Vector3 center, float radius)
        {
            Component best = null;
            float bestSqr = radius * radius;

            foreach (SpawnArea spawner in LoadedSurtlingSpawners)
            {
                if (spawner == null) continue;
                float sqr = (GetRitualAnchorPosition(spawner) - center).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = spawner;
            }

            foreach (CreatureSpawner spawner in LoadedSurtlingCreatureSpawners)
            {
                if (spawner == null) continue;
                float sqr = (GetRitualAnchorPosition(spawner) - center).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = spawner;
            }

            return best;
        }

        internal static bool IsSpawnerPacified(SpawnArea spawner)
        {
            if (!IsSurtlingSpawner(spawner)) return false;
            ZNetView view = GetSpawnerView(spawner);
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo != null && zdo.GetBool(GeyserPacifiedKey, false)) return true;
            return IsAnchorPacifiedByCompletedSignal(GetRitualAnchorPosition(spawner));
        }

        internal static bool IsSpawnerPacified(CreatureSpawner spawner)
        {
            if (!IsSurtlingCreatureSpawner(spawner)) return false;
            ZNetView view = GetSpawnerView(spawner);
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo != null && zdo.GetBool(GeyserPacifiedKey, false)) return true;
            return IsAnchorPacifiedByCompletedSignal(GetRitualAnchorPosition(spawner));
        }

        private static bool IsAnchorPacifiedByCompletedSignal(Vector3 anchor)
        {
            // The completed signal fire is the persistent authority even when the vanilla local
            // CreatureSpawner has no independently writable/persistent ZDO.
            foreach (SurtlingSignalFireRecruitmentRuntime fire in LoadedSignalFires)
            {
                if (fire == null || fire._view == null || !fire._view.IsValid()) continue;
                ZDO fireZdo = fire._view.GetZDO();
                if (fireZdo == null || !fireZdo.GetBool(CompleteKey, false)) continue;
                Vector3 bound = new Vector3(
                    fireZdo.GetFloat(BoundSpawnerXKey, float.NaN),
                    fireZdo.GetFloat(BoundSpawnerYKey, float.NaN),
                    fireZdo.GetFloat(BoundSpawnerZKey, float.NaN));
                if (float.IsNaN(bound.x)) continue;
                if ((anchor - bound).sqrMagnitude <= FireHoleClusterRadius * FireHoleClusterRadius) return true;
            }
            return false;
        }

        internal static bool TrySuppressSpawner(SpawnArea spawner)
        {
            RegisterSpawner(spawner);
            if (!IsSpawnerPacified(spawner)) return false;
            if (spawner.enabled)
            {
                spawner.CancelInvoke();
                spawner.enabled = false;
            }
            return true;
        }

        internal static bool TrySuppressSpawner(CreatureSpawner spawner)
        {
            RegisterCreatureSpawner(spawner);
            if (!IsSpawnerPacified(spawner)) return false;
            if (spawner.enabled)
            {
                spawner.CancelInvoke();
                spawner.enabled = false;
            }
            return true;
        }

        private static void PacifySpawnerCluster(Component anchor)
        {
            if (anchor == null) return;
            Vector3 center = GetRitualAnchorPosition(anchor);
            bool any = false;

            foreach (SpawnArea candidate in LoadedSurtlingSpawners)
            {
                if (candidate == null || !IsSurtlingSpawner(candidate)) continue;
                if ((GetRitualAnchorPosition(candidate) - center).sqrMagnitude > FireHoleClusterRadius * FireHoleClusterRadius) continue;
                MarkSpawnerPacified(candidate);
                any = true;
            }

            foreach (CreatureSpawner candidate in LoadedSurtlingCreatureSpawners)
            {
                if (candidate == null || !IsSurtlingCreatureSpawner(candidate)) continue;
                if ((GetRitualAnchorPosition(candidate) - center).sqrMagnitude > FireHoleClusterRadius * FireHoleClusterRadius) continue;
                MarkSpawnerPacified(candidate);
                any = true;
            }

            if (!any)
            {
                if (anchor is SpawnArea area) MarkSpawnerPacified(area);
                else if (anchor is CreatureSpawner creature) MarkSpawnerPacified(creature);
            }
        }

        private static void MarkSpawnerPacified(SpawnArea spawner)
        {
            if (spawner == null) return;
            MarkSpawnerZdoPacified(spawner);
            TrySuppressSpawner(spawner);
        }

        private static void MarkSpawnerPacified(CreatureSpawner spawner)
        {
            if (spawner == null) return;
            MarkSpawnerZdoPacified(spawner);
            TrySuppressSpawner(spawner);
        }

        private static void MarkSpawnerZdoPacified(Component spawner)
        {
            ZNetView view = GetSpawnerView(spawner);
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (view != null && view.IsValid() && !view.IsOwner() && ClaimOwnershipMethod != null)
            {
                try { ClaimOwnershipMethod.Invoke(view, null); } catch { }
            }
            if (zdo != null && view != null && view.IsOwner())
            {
                try { zdo.Set(GeyserPacifiedKey, true); } catch { }
            }
        }

        private static ZNetView GetSpawnerView(Component spawner)
        {
            if (spawner == null) return null;
            return spawner.GetComponent<ZNetView>() ?? spawner.GetComponentInParent<ZNetView>();
        }

        internal static bool IsBurning(Fireplace fireplace)
        {
            if (fireplace == null || IsBurningMethod == null) return false;
            try { return (bool)IsBurningMethod.Invoke(fireplace, null); }
            catch { return false; }
        }

        private static bool TryReadFuel(Fireplace fireplace, out float fuel, out float maxFuel)
        {
            fuel = 0f;
            maxFuel = 0f;
            if (fireplace == null) return false;

            try
            {
                ZNetView view = fireplace.GetComponent<ZNetView>() ?? fireplace.GetComponentInParent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo == null) return false;
                fuel = Mathf.Max(0f, zdo.GetFloat("fuel", 0f));

                if (FireplaceMaxFuelField != null)
                {
                    object value = FireplaceMaxFuelField.GetValue(fireplace);
                    if (value != null) maxFuel = Mathf.Max(0f, Convert.ToSingle(value));
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TrySummonAdditionalFirekeeper(SurtlingSignalFireInteractable signal, Humanoid user)
        {
            if (signal == null || user == null) return false;
            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer) return false;

            ZNetView signalView = signal.GetNetworkView();
            ZDO signalZdo = signalView != null && signalView.IsValid() ? signalView.GetZDO() : null;
            if (signalZdo == null)
            {
                NotifyPlayer(player, "Das Signalfeuer ist noch nicht netzwerkbereit.");
                return true;
            }

            if (!signalZdo.GetBool(CompleteKey, false))
            {
                NotifyPlayer(player, "Vollende zuerst das dreitägige Feuerritual. Erst danach antworten weitere Surtlinge auf den Ruf.");
                return true;
            }

            Fireplace ritualFire = signal.GetComponent<Fireplace>() ?? signal.GetComponentInChildren<Fireplace>(true) ?? signal.GetComponentInParent<Fireplace>();
            if (!IsBurning(ritualFire))
            {
                NotifyPlayer(player, "Das Signalfeuer muss brennen, damit der befriedete Feuergeysir den Ruf hört.");
                return true;
            }

            Inventory inventory = player.GetInventory();
            int available = CountPrefabItems(inventory, "SurtlingCore");
            if (available < AdditionalSummonCoreCost)
            {
                NotifyPlayer(player, $"Für einen weiteren Feuerwart brauchst du {AdditionalSummonCoreCost} Surtling-Kerne ({available}/{AdditionalSummonCoreCost}).");
                return true;
            }

            if (signalView == null || !signalView.IsValid())
            {
                NotifyPlayer(player, "Das Signalfeuer kann den Ruf gerade nicht ausführen.");
                return true;
            }
            if (!signalView.IsOwner() && ClaimOwnershipMethod != null)
            {
                try { ClaimOwnershipMethod.Invoke(signalView, null); } catch { }
            }
            if (!signalView.IsOwner())
            {
                NotifyPlayer(player, "Das Signalfeuer wird gerade von einem anderen Netzwerkbesitzer verwaltet.");
                return true;
            }

            SurtlingSignalFireRecruitmentRuntime runtime = signal.GetComponent<SurtlingSignalFireRecruitmentRuntime>() ??
                                                           signal.GetComponentInParent<SurtlingSignalFireRecruitmentRuntime>();
            if (runtime == null)
            {
                NotifyPlayer(player, "Das Ritualfeuer ist unvollständig registriert.");
                return true;
            }

            Component spawner = runtime.ResolveBoundOrNearestSpawner(signalZdo, signal.transform.position, GeyserRadius);
            Vector3 summonCenter;
            if (spawner != null)
            {
                summonCenter = GetRitualAnchorPosition(spawner);
            }
            else
            {
                float x = signalZdo.GetFloat(BoundSpawnerXKey, float.NaN);
                float y = signalZdo.GetFloat(BoundSpawnerYKey, float.NaN);
                float z = signalZdo.GetFloat(BoundSpawnerZKey, float.NaN);
                if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z))
                {
                    NotifyPlayer(player, "Der befriedete Feuergeysir ist nicht mehr gebunden. Geh näher an den Geysir und versuche es erneut.");
                    return true;
                }
                summonCenter = new Vector3(x, y, z);
            }

            Character surtling = runtime.SpawnSurtling(summonCenter);
            if (surtling == null)
            {
                NotifyPlayer(player, "Der Ruf verhallt. Es konnte kein Surtling erscheinen; die Kerne wurden nicht verbraucht.");
                return true;
            }

            ZNetView workerView = surtling.GetComponent<ZNetView>() ?? surtling.GetComponentInParent<ZNetView>();
            ZDO workerZdo = workerView != null && workerView.IsValid() ? workerView.GetZDO() : null;
            if (workerZdo == null || !workerView.IsOwner())
            {
                try
                {
                    if (workerView != null && workerView.IsValid() && !workerView.IsOwner() && ClaimOwnershipMethod != null)
                        ClaimOwnershipMethod.Invoke(workerView, null);
                }
                catch { }
                workerZdo = workerView != null && workerView.IsValid() ? workerView.GetZDO() : null;
            }
            if (workerZdo == null || workerView == null || !workerView.IsOwner())
            {
                try { if (surtling != null) UnityEngine.Object.Destroy(surtling.gameObject); } catch { }
                NotifyPlayer(player, "Der gerufene Surtling konnte nicht übernommen werden; die Kerne wurden nicht verbraucht.");
                return true;
            }

            int before = CountPrefabItems(inventory, "SurtlingCore");
            if (!TryConsumePrefabItems(inventory, "SurtlingCore", AdditionalSummonCoreCost))
            {
                try { if (surtling != null) UnityEngine.Object.Destroy(surtling.gameObject); } catch { }
                NotifyPlayer(player, "Die Surtling-Kerne konnten nicht sicher verbraucht werden. Der Ruf wurde abgebrochen.");
                return true;
            }

            long owner = player.GetPlayerID();
            NaturalRecruitmentCompletionRuntime.Complete(
                surtling,
                workerZdo,
                owner,
                WorkerRole.Firekeeper,
                "Surtling",
                surtling.transform.position,
                30f,
                minimumDefensive: false,
                followPlayer: player);

            int summons = Mathf.Max(0, signalZdo.GetInt(AdditionalSummonCountKey, 0)) + 1;
            signalZdo.Set(AdditionalSummonCountKey, summons);
            PacifyNearbySurtlings(summonCenter, surtling);

            int after = CountPrefabItems(inventory, "SurtlingCore");
            NotifyPlayer(player, $"Das befriedete Feuer antwortet: Ein weiterer Surtling-Feuerwart erscheint. {AdditionalSummonCoreCost} Kerne geopfert ({before}->{after}).");
            Plugin.Log?.LogInfo($"Additional Surtling summoned at pacified signal fire. Worker={workerZdo.m_uid}, owner={owner}, extraSummons={summons}.");
            return true;
        }

        private static int CountPrefabItems(Inventory inventory, string prefab)
        {
            if (inventory == null || string.IsNullOrEmpty(prefab)) return 0;
            List<ItemDrop.ItemData> items = inventory.GetAllItems();
            if (items == null) return 0;
            int total = 0;
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (!IsPrefabItem(item, prefab)) continue;
                total += Mathf.Max(0, item.m_stack);
            }
            return total;
        }

        private static bool TryConsumePrefabItems(Inventory inventory, string prefab, int amount)
        {
            if (inventory == null || string.IsNullOrEmpty(prefab) || amount <= 0) return false;
            int before = CountPrefabItems(inventory, prefab);
            if (before < amount) return false;

            List<ItemDrop.ItemData> source = inventory.GetAllItems();
            if (source == null) return false;
            List<ItemDrop.ItemData> items = new List<ItemDrop.ItemData>(source);
            int remaining = amount;
            for (int i = 0; i < items.Count && remaining > 0; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (!IsPrefabItem(item, prefab)) continue;
                int take = Mathf.Min(remaining, Mathf.Max(0, item.m_stack));
                if (take <= 0) continue;
                inventory.RemoveItem(item, take);
                remaining -= take;
            }

            return remaining == 0 && CountPrefabItems(inventory, prefab) <= before - amount;
        }

        private static bool IsPrefabItem(ItemDrop.ItemData item, string prefab)
        {
            if (item == null || item.m_stack <= 0 || item.m_dropPrefab == null) return false;
            string normalized = SpeciesRoleRegistry.NormalizePrefabName(item.m_dropPrefab.name);
            return string.Equals(normalized, prefab, StringComparison.OrdinalIgnoreCase);
        }

        private static void NotifyPlayer(Player player, string text)
        {
            if (player == null || string.IsNullOrEmpty(text)) return;
            try { player.Message(MessageHud.MessageType.Center, text); } catch { }
        }

        internal static bool TryForwardVanillaInteraction(SurtlingSignalFireInteractable signal, Humanoid user, bool hold, bool alt)
        {
            if (signal == null || user == null) return false;
            Fireplace fire = signal.GetComponent<Fireplace>() ?? signal.GetComponentInChildren<Fireplace>(true) ?? signal.GetComponentInParent<Fireplace>();
            if (fire == null) return false;
            try
            {
                _forwardingVanillaInteraction = true;
                return fire.Interact(user, hold, alt);
            }
            finally
            {
                _forwardingVanillaInteraction = false;
            }
        }

        internal static float GetRequiredBurnSeconds()
        {
            try
            {
                EnvMan env = EnvMan.instance;
                if (env != null && env.m_dayLengthSec > 60f)
                    return Mathf.Max(60f, env.m_dayLengthSec * RequiredBurnDays);
            }
            catch { }
            return FallbackDayLengthSeconds * RequiredBurnDays;
        }

        private bool HasBuiltRoof()
        {
            // Require an actual player/build piece above the ritual fire instead of letting terrain,
            // trees or the FireHole itself count as protection. This makes shelter construction a
            // deliberate part of the recruitment challenge.
            Vector3 origin = transform.position + Vector3.up * 1.0f;
            int hitCount;
            try
            {
                hitCount = Physics.RaycastNonAlloc(origin, Vector3.up, _roofHits, RoofCheckHeight, ~0, QueryTriggerInteraction.Ignore);
            }
            catch
            {
                return false;
            }

            for (int i = 0; i < hitCount; i++)
            {
                Collider collider = _roofHits[i].collider;
                if (collider == null) continue;
                Piece roofPiece = collider.GetComponentInParent<Piece>();
                if (roofPiece == null || roofPiece == _piece) continue;
                if (IsRoofPiece(roofPiece)) return true;
            }
            return false;
        }

        private static bool IsRoofPiece(Piece piece)
        {
            if (piece == null) return false;
            string objectName = piece.gameObject != null ? piece.gameObject.name : string.Empty;
            string pieceName = string.Empty;
            try { pieceName = piece.m_name ?? string.Empty; } catch { }
            string combined = (objectName + " " + pieceName).ToLowerInvariant();
            return combined.Contains("roof") ||
                   combined.Contains("thatch") ||
                   combined.Contains("dach") ||
                   combined.Contains("shingle");
        }

        private static string FormatRitualTime(float seconds)
        {
            seconds = Mathf.Max(0f, seconds);
            float dayLength = Mathf.Max(60f, GetRequiredBurnSeconds() / RequiredBurnDays);
            float days = seconds / dayLength;
            if (days >= 1f) return $"{days:0.0} Valheim-Tage";
            int minutes = Mathf.CeilToInt(seconds / 60f);
            return $"{minutes} Min.";
        }

        internal static string BuildHover(SurtlingSignalFireInteractable signal, ZDO zdo)
        {
            if (signal == null || zdo == null) return "Prächtiges Signalfeuer";

            float requiredBurnSeconds = GetRequiredBurnSeconds();
            float progress = Mathf.Clamp(zdo.GetFloat(ProgressKey, 0f), 0f, requiredBurnSeconds);
            int percent = requiredBurnSeconds > 0.01f ? Mathf.RoundToInt(progress / requiredBurnSeconds * 100f) : 0;
            bool complete = zdo.GetBool(CompleteKey, false);
            bool hasGeyser = zdo.GetBool(HasGeyserKey, false);
            bool hasRoof = zdo.GetBool(HasRoofKey, false);
            Fireplace fire = signal.GetComponent<Fireplace>() ?? signal.GetComponentInChildren<Fireplace>(true) ?? signal.GetComponentInParent<Fireplace>();
            bool burning = IsBurning(fire);
            bool hasFuel = TryReadFuel(fire, out float fuel, out float maxFuel);
            string fuelText = hasFuel
                ? $"Holzvorrat: <color=yellow><b>{fuel:0.0}</b></color> / {Mathf.Max(1f, maxFuel):0.#}"
                : "Holzvorrat: <color=#BBBBBB>unbekannt</color>";

            if (complete)
            {
                int extraSummons = Mathf.Max(0, zdo.GetInt(AdditionalSummonCountKey, 0));
                return "<color=#FFB14A><b>Prächtiges Signalfeuer</b></color>\n" +
                       "<color=#7CFF8A>Feuerritual vollendet — Feuergeysir befriedet</color>\n" +
                       fuelText + "\n" +
                       $"Weitere gerufene Feuerwärter: <color=yellow><b>{extraSummons}</b></color>\n" +
                       $"[<color=yellow><b>Shift+E</b></color>] {AdditionalSummonCoreCost} Surtling-Kerne opfern → weiteren Feuerwart rufen\n" +
                       "Das Signalfeuer muss für den Ruf brennen.\n" +
                       "Jeder gerufene Surtling braucht anschließend eine eigene kleine Surtling-Ruhestätte.";
            }

            string geyser = hasGeyser
                ? $"<color=#7CFF8A>Feuergeysir in Reichweite (≤ {GeyserRadius:0} m)</color>"
                : $"<color=#FF8080>Kein Feuergeysir innerhalb {GeyserRadius:0} m</color>";
            string roof = hasRoof
                ? "<color=#7CFF8A>Dachschutz vorhanden</color>"
                : "<color=#FF8080>Kein Dachschutz — das Ritual verliert Fortschritt</color>";
            string state = burning && hasRoof
                ? "<color=#FFB14A>Das Feuer brennt geschützt — Respekt steigt.</color>"
                : !burning
                    ? "<color=#BBBBBB>Das Feuer ist aus — Fortschritt fällt zurück.</color>"
                    : "<color=#BBBBBB>Das Feuer ist ungeschützt — Fortschritt fällt zurück.</color>";
            float remaining = Mathf.Max(0f, requiredBurnSeconds - progress);
            string remainingText = FormatRitualTime(remaining);

            return "<color=#FFB14A><b>Prächtiges Signalfeuer</b></color>\n" +
                   geyser + "\n" +
                   roof + "\n" +
                   fuelText + "\n" +
                   $"Feuerritual: <color=yellow><b>{percent}%</b></color> — noch {remainingText}\n" +
                   $"Ziel: {RequiredBurnDays:0} Valheim-Tage ununterbrochene Feuerpflege\n" +
                   state + "\n" +
                   "[<color=yellow><b>E</b></color>] Normales Holz nachlegen";
        }

        private void AnnounceMilestone(ZDO zdo, float progress, float requiredBurnSeconds, bool hasGeyser, bool burning, bool hasRoof)
        {
            if (zdo == null || !hasGeyser || !burning || !hasRoof || requiredBurnSeconds <= 0.01f) return;
            int stage = Mathf.Clamp(Mathf.FloorToInt((progress / requiredBurnSeconds) * 4f), 0, 4);
            int old = zdo.GetInt(LastStageKey, 0);
            if (stage <= old || stage <= 0) return;
            zdo.Set(LastStageKey, stage);
            if (stage < 4) NotifyLocalCreator($"Surtling-Ritual: {stage * 25}% — halte das Signalfeuer am Brennen.");
        }

        private void NotifyLocalCreator(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Player local = Player.m_localPlayer;
            if (local == null) return;
            long creator = 0L;
            try { creator = _piece != null ? _piece.GetCreator() : 0L; } catch { }
            if (creator != 0L && creator != local.GetPlayerID()) return;
            if ((local.transform.position - transform.position).sqrMagnitude > 60f * 60f) return;
            try { local.Message(MessageHud.MessageType.Center, text); } catch { }
        }
    }

    [HarmonyPatch(typeof(SpawnArea), "Awake")]
    internal static class SurtlingSignalSpawnerAwakePatch
    {
        private static void Postfix(SpawnArea __instance)
        {
            SurtlingSignalFireRecruitmentRuntime.RegisterSpawner(__instance);
            SurtlingSignalFireRecruitmentRuntime.TrySuppressSpawner(__instance);
        }
    }

    [HarmonyPatch(typeof(SpawnArea), "UpdateSpawn")]
    internal static class SurtlingSignalSpawnerUpdatePatch
    {
        private static bool Prefix(SpawnArea __instance)
        {
            return !SurtlingSignalFireRecruitmentRuntime.TrySuppressSpawner(__instance);
        }
    }
}
