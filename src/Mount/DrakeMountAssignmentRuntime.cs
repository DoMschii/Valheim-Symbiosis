using System;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    internal static class DrakeMountAssignmentRuntime
    {
        internal const string MountPlayerIdKey = "vw_drake_mount_player_id";
        internal const string MountSelectionSerialKey = "vw_drake_mount_selection_serial";
        private const string LocalSelectionPrefix = "vw_drake_mount_selected_";
        private const string PreviousStanceKey = "vw_drake_mount_prev_stance";
        private const string PreviousStanceValidKey = "vw_drake_mount_prev_stance_valid";
        private const string PreviousFactionKey = "vw_drake_mount_prev_faction";
        private const string PreviousFactionValidKey = "vw_drake_mount_prev_faction_valid";
        private const float CompanionEnforceInterval = 1.5f;
        private const float LegacyProbeInterval = 15f;
        private const float LegacyProbeWindow = 60f;

        private static float _nextCompanionEnforceAt;
        private static float _nextLegacyAssignmentProbeAt;
        private static float _legacyProbeWindowUntil;
        private static long _legacyProbePlayerId;
        private static bool _legacyProbeCompleted;
        private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo CharacterFactionField = typeof(Character).GetField("m_faction", InstanceFlags);
        private static readonly FieldInfo MonsterTargetCreatureField = typeof(MonsterAI).GetField("m_targetCreature", InstanceFlags);

        private static Action<string> _status;
        private static Action<string> _debug;

        internal static void Initialize(Action<string> status, Action<string> debug)
        {
            _status = status;
            _debug = debug;
        }

        internal static void Shutdown()
        {
            _status = null;
            _debug = null;
            _nextCompanionEnforceAt = 0f;
            _nextLegacyAssignmentProbeAt = 0f;
            _legacyProbeWindowUntil = 0f;
            _legacyProbePlayerId = 0L;
            _legacyProbeCompleted = false;
        }

        internal static void Tick()
        {
            if (Time.unscaledTime < _nextCompanionEnforceAt) return;
            _nextCompanionEnforceAt = Time.unscaledTime + CompanionEnforceInterval;

            Player player = Player.m_localPlayer;
            if (player == null || player.IsDead()) return;

            // The personal-mount system is legacy-only now. If a modern player has no persisted
            // assignment, probe slowly for one minute after their Player object becomes usable so
            // an old ZDO-only assignment can still be migrated. After that, stop scanning entirely
            // for the rest of the session.
            long playerId = player.GetPlayerID();
            if (_legacyProbePlayerId != playerId)
            {
                _legacyProbePlayerId = playerId;
                _legacyProbeCompleted = false;
                _legacyProbeWindowUntil = Time.unscaledTime + LegacyProbeWindow;
                _nextLegacyAssignmentProbeAt = Time.unscaledTime;
            }

            bool hasPersistentAssignment = HasPersistentAssignment(player);
            if (!hasPersistentAssignment)
            {
                if (_legacyProbeCompleted) return;
                if (Time.unscaledTime > _legacyProbeWindowUntil)
                {
                    _legacyProbeCompleted = true;
                    _debug?.Invoke("Legacy Drake mount compatibility probe ended; no assignment found, background scans disabled for this session.");
                    return;
                }
                if (Time.unscaledTime < _nextLegacyAssignmentProbeAt) return;
                _nextLegacyAssignmentProbeAt = Time.unscaledTime + LegacyProbeInterval;
            }

            Character drake = FindAssignedLoadedDrake(player);
            if (drake == null || drake.IsDead()) return;

            if (!hasPersistentAssignment)
            {
                ZNetView migratedView = drake.GetComponent<ZNetView>();
                ZDO migratedZdo = migratedView != null && migratedView.IsValid() ? migratedView.GetZDO() : null;
                if (migratedZdo != null && playerId != 0L)
                {
                    PlayerPrefs.SetString(SelectionKey(playerId), WorkerToken(migratedZdo));
                    PlayerPrefs.Save();
                    _legacyProbeCompleted = true;
                    _debug?.Invoke($"Migrated legacy Drake mount assignment to local selection token: worker={migratedZdo.m_uid}, player={playerId}.");
                }
            }

            ZNetView view = drake.GetComponent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (!IsAssignedMount(zdo)) return;

            EnforceCompanionState(zdo, player, drake, false);
        }

        private static string SelectionKey(long playerId) => LocalSelectionPrefix + playerId;
        private static string WorkerToken(ZDO zdo) => zdo == null ? string.Empty : zdo.m_uid.ToString();

        internal static bool HasPersistentAssignment(Player player)
        {
            if (player == null) return false;
            long playerId = player.GetPlayerID();
            if (playerId == 0L) return false;
            return !string.IsNullOrWhiteSpace(PlayerPrefs.GetString(SelectionKey(playerId), string.Empty));
        }

        internal static bool SupportsWorkerMenuTab(ZDO workerZdo)
        {
            if (!WorkerZdo.IsWorker(workerZdo)) return false;
            string species = workerZdo.GetString(WorkerZdo.SpeciesKey, string.Empty);
            return string.Equals(species, "Drake", StringComparison.OrdinalIgnoreCase);
        }

        internal static void DrawWorkerMenuTab(ZDO workerZdo, bool writable)
        {
            GUILayout.Space(8f);
            GUILayout.Label("DRAKE MOUNT");
            GUILayout.Space(4f);
            GUILayout.Label("Die gebundene Drake-Halskette startet und beendet die Symbiose. Eine separate Pfeife wird nicht mehr benoetigt.");
            GUILayout.Label("Hier legst du fest, welcher Drake dein persoenlicher Mount ist.");
            GUILayout.Space(10f);

            Player player = Player.m_localPlayer;
            if (player == null || workerZdo == null)
            {
                GUILayout.Label("Lokaler Spieler nicht verfuegbar.");
                return;
            }

            long playerId = player.GetPlayerID();
            long workerOwner = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            string selectedToken = PlayerPrefs.GetString(SelectionKey(playerId), string.Empty);
            bool assignedHere = playerId != 0L && string.Equals(selectedToken, WorkerToken(workerZdo), StringComparison.Ordinal);

            GUILayout.Label(assignedHere ? "Status: Dieser Drake ist dein Mount und Begleiter." : "Status: Dieser Drake ist nicht als dein Mount festgelegt.");
            if (assignedHere)
            {
                GUILayout.Label("Begleiter-Modus: keine Workzone • folgt dir • greift Gegner an.");
            }
            GUILayout.Space(8f);

            bool canWrite = writable && playerId != 0L && (workerOwner == 0L || workerOwner == playerId);
            GUI.enabled = canWrite;
            if (assignedHere)
            {
                if (GUILayout.Button("Mount-Zuweisung loesen", GUILayout.Height(34f)))
                {
                    ReleaseCompanionState(workerZdo);
                    workerZdo.Set(MountPlayerIdKey, 0L);
                    workerZdo.Set(MountSelectionSerialKey, 0L);
                    PlayerPrefs.DeleteKey(SelectionKey(playerId));
                    PlayerPrefs.Save();
                    _status?.Invoke("Drake: Mount-Zuweisung geloest. Begleiter-Modus beendet.");
                    _debug?.Invoke($"Drake mount assignment cleared: worker={workerZdo.m_uid}, player={playerId}.");
                }
            }
            else
            {
                if (GUILayout.Button("Als meinen Mount festlegen", GUILayout.Height(34f)))
                {
                    ClearLoadedAssignmentsForPlayer(playerId, workerZdo);
                    workerZdo.Set(MountPlayerIdKey, playerId);
                    workerZdo.Set(MountSelectionSerialKey, DateTime.UtcNow.Ticks);
                    PlayerPrefs.SetString(SelectionKey(playerId), WorkerToken(workerZdo));
                    PlayerPrefs.Save();
                    EnforceCompanionState(workerZdo, player, FindLoadedCharacter(workerZdo), true);
                    _status?.Invoke("Drake als persoenlichen Mount + Begleiter festgelegt: Workzone geloest, folgt dir und greift Gegner an.");
                    _debug?.Invoke($"Drake mount assignment set: worker={workerZdo.m_uid}, player={playerId}, token={WorkerToken(workerZdo)}.");
                }
            }
            GUI.enabled = true;

            if (!canWrite)
            {
                GUILayout.Space(6f);
                GUILayout.Label("Nur Lesen: Diese Mount-Zuweisung kann aktuell nicht geaendert werden.");
            }

            GUILayout.Space(12f);
            GUILayout.Label("Halskette: Hotbar-Taste = Symbiose starten/beenden. Der echte geladene Drake ist dein Begleiter und hat keine Workzone. Ist er nicht geladen, erzeugt die Symbiose weiterhin nur einen temporaeren Drake-Koerper bei dir.");
        }

        internal static bool IsAssignedMount(ZDO zdo)
        {
            return WorkerZdo.IsWorker(zdo) && zdo.GetLong(MountPlayerIdKey, 0L) != 0L;
        }

        private static Character FindLoadedCharacter(ZDO zdo)
        {
            if (zdo == null) return null;
            ZDOID uid = zdo.m_uid;
            foreach (Character candidate in Character.GetAllCharacters())
            {
                if (candidate == null || candidate is Player || candidate.IsDead()) continue;
                ZNetView view = candidate.GetComponent<ZNetView>();
                ZDO candidateZdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (candidateZdo != null && candidateZdo.m_uid.Equals(uid)) return candidate;
            }
            return null;
        }

        private static void EnforceCompanionState(ZDO zdo, Player player, Character loadedDrake, bool initialAssignment)
        {
            if (zdo == null || player == null) return;

            if (!zdo.GetBool(PreviousStanceValidKey, false))
            {
                zdo.Set(PreviousStanceKey, zdo.GetInt(WorkerZdo.Stance, (int)WorkerStance.Defensive));
                zdo.Set(PreviousStanceValidKey, true);
            }

            bool hadWorkArea = WorkerZdo.TryReadWorkArea(zdo, out _, out _, out _);
            if (hadWorkArea)
            {
                WorkerZdo.ClearWorkArea(zdo);
                _debug?.Invoke($"Drake companion cleared Workzone: worker={zdo.m_uid}.");
            }

            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.RestReason, (int)WorkerRestReason.None);
            zdo.Set(WorkerZdo.Stance, (int)WorkerStance.Aggressive);
            if ((WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle) != WorkerState.Follow)
                zdo.Set(WorkerZdo.State, (int)WorkerState.Follow);

            if (loadedDrake == null) return;
            MakeAssignedMountFriendly(zdo, loadedDrake, initialAssignment);
            ClearOwnedWorkerTargetsAgainstMount(loadedDrake, player.GetPlayerID());
            WorkerCombatStanceRuntime.SetStance(loadedDrake, zdo, WorkerStance.Aggressive);
            MonsterAI ai = loadedDrake.GetComponent<MonsterAI>();
            if (ai != null && !DrakeMountRuntime.IsMounted(loadedDrake) && ai.GetFollowTarget() != player.gameObject)
                WorkerNavigationRuntime.SetNativeFollowTarget(ai, player.gameObject);
        }

        private static void ReleaseCompanionState(ZDO zdo)
        {
            if (zdo == null) return;

            Character loadedDrake = FindLoadedCharacter(zdo);
            MonsterAI ai = loadedDrake != null ? loadedDrake.GetComponent<MonsterAI>() : null;
            if (ai != null && ai.GetFollowTarget() != null)
                WorkerNavigationRuntime.SetNativeFollowTarget(ai, null);

            if (zdo.GetBool(PreviousStanceValidKey, false))
            {
                int raw = zdo.GetInt(PreviousStanceKey, (int)WorkerStance.Defensive);
                WorkerStance stance = raw >= (int)WorkerStance.Aggressive && raw <= (int)WorkerStance.Passive
                    ? (WorkerStance)raw
                    : WorkerStance.Defensive;
                zdo.Set(WorkerZdo.Stance, (int)stance);
                if (loadedDrake != null) WorkerCombatStanceRuntime.SetStance(loadedDrake, zdo, stance);
            }
            zdo.Set(PreviousStanceValidKey, false);

            RestoreAssignedMountFaction(zdo, loadedDrake);
            WorkerZdo.ClearTarget(zdo);
            if ((WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle) == WorkerState.Follow)
            {
                zdo.Set(WorkerZdo.State, WorkerWorkAreaRules.HasRealHome(zdo)
                    ? (int)WorkerState.ReturnHome
                    : (int)WorkerState.Idle);
            }
        }


        internal static void MakeSymbiosisAvatarFriendly(Character drake)
        {
            SetCharacterFactionToPlayers(drake, false, null, false);
        }

        private static void MakeAssignedMountFriendly(ZDO zdo, Character drake, bool logChange)
        {
            SetCharacterFactionToPlayers(drake, true, zdo, logChange);
        }

        private static void SetCharacterFactionToPlayers(Character character, bool persistPrevious, ZDO zdo, bool logChange)
        {
            if (character == null || CharacterFactionField == null) return;
            try
            {
                Type factionType = CharacterFactionField.FieldType;
                object playersFaction = Enum.Parse(factionType, "Players", true);
                object current = CharacterFactionField.GetValue(character);

                if (persistPrevious && zdo != null && !zdo.GetBool(PreviousFactionValidKey, false))
                {
                    zdo.Set(PreviousFactionKey, Convert.ToInt32(current));
                    zdo.Set(PreviousFactionValidKey, true);
                }

                if (!Equals(current, playersFaction))
                {
                    CharacterFactionField.SetValue(character, playersFaction);
                    if (logChange) _debug?.Invoke($"Drake companion faction -> Players: worker={zdo?.m_uid}.");
                }
            }
            catch (Exception ex)
            {
                _debug?.Invoke($"Drake companion friendly faction failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void RestoreAssignedMountFaction(ZDO zdo, Character drake)
        {
            if (zdo == null || !zdo.GetBool(PreviousFactionValidKey, false)) return;
            try
            {
                if (drake != null && CharacterFactionField != null)
                {
                    int raw = zdo.GetInt(PreviousFactionKey, 0);
                    object previous = Enum.ToObject(CharacterFactionField.FieldType, raw);
                    CharacterFactionField.SetValue(drake, previous);
                    _debug?.Invoke($"Drake companion faction restored: worker={zdo.m_uid}, faction={previous}.");
                }
            }
            catch (Exception ex)
            {
                _debug?.Invoke($"Drake companion faction restore failed safely: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                zdo.Set(PreviousFactionValidKey, false);
            }
        }

        private static void ClearOwnedWorkerTargetsAgainstMount(Character drake, long ownerPlayerId)
        {
            if (drake == null || ownerPlayerId == 0L || MonsterTargetCreatureField == null) return;
            try
            {
                foreach (Character candidate in Character.GetAllCharacters())
                {
                    if (candidate == null || candidate == drake || candidate is Player || candidate.IsDead()) continue;
                    ZNetView view = candidate.GetComponent<ZNetView>();
                    ZDO workerZdo = view != null && view.IsValid() ? view.GetZDO() : null;
                    if (!WorkerZdo.IsWorker(workerZdo)) continue;
                    if (workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != ownerPlayerId) continue;

                    MonsterAI ai = candidate.GetComponent<MonsterAI>();
                    if (ai == null) continue;
                    Character target = MonsterTargetCreatureField.GetValue(ai) as Character;
                    if (target != drake) continue;
                    MonsterTargetCreatureField.SetValue(ai, null);
                    WorkerZdo.ClearTarget(workerZdo);
                    _debug?.Invoke($"Cleared friendly guard target against assigned Drake mount: guard={candidate.name}, mount={drake.name}.");
                }
            }
            catch (Exception ex)
            {
                _debug?.Invoke($"Friendly Drake guard-target cleanup failed safely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        internal static Character FindAssignedLoadedDrake(Player player)
        {
            if (player == null) return null;
            long playerId = player.GetPlayerID();
            string selectedToken = PlayerPrefs.GetString(SelectionKey(playerId), string.Empty);
            Character fallback = null;
            long fallbackSerial = long.MinValue;

            foreach (Character candidate in Character.GetAllCharacters())
            {
                if (candidate == null || candidate is Player || candidate.IsDead()) continue;
                if (!string.Equals(SpeciesRoleRegistry.NormalizePrefabName(candidate.gameObject.name), "Drake", StringComparison.OrdinalIgnoreCase)) continue;

                ZNetView view = candidate.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (!WorkerZdo.IsWorker(zdo)) continue;
                if (zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != playerId) continue;

                if (!string.IsNullOrEmpty(selectedToken) && string.Equals(selectedToken, WorkerToken(zdo), StringComparison.Ordinal))
                    return candidate;

                if (zdo.GetLong(MountPlayerIdKey, 0L) == playerId)
                {
                    long serial = zdo.GetLong(MountSelectionSerialKey, 0L);
                    if (fallback == null || serial > fallbackSerial)
                    {
                        fallback = candidate;
                        fallbackSerial = serial;
                    }
                }
            }

            return fallback;
        }

        private static void ClearLoadedAssignmentsForPlayer(long playerId, ZDO keep)
        {
            if (playerId == 0L) return;
            foreach (Character candidate in Character.GetAllCharacters())
            {
                if (candidate == null || candidate is Player) continue;
                if (!string.Equals(SpeciesRoleRegistry.NormalizePrefabName(candidate.gameObject.name), "Drake", StringComparison.OrdinalIgnoreCase)) continue;
                ZNetView view = candidate.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (!WorkerZdo.IsWorker(zdo) || (keep != null && zdo.m_uid == keep.m_uid)) continue;
                if (zdo.GetLong(MountPlayerIdKey, 0L) != playerId) continue;
                ReleaseCompanionState(zdo);
                zdo.Set(MountPlayerIdKey, 0L);
                zdo.Set(MountSelectionSerialKey, 0L);
            }
        }
    }
}
