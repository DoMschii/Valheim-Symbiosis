using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Keeps Worker rest-station recipes hidden until the local player has actually recruited
    /// a matching Worker family. Unlocks are persisted per Valheim player id in the Workers config,
    /// so discovering Wood no longer exposes every custom home at once.
    ///
    /// The runtime only touches Piece.m_enabled on the registered prefab. Already placed homes and
    /// their save/ZDO state are never modified.
    /// </summary>
    internal static class RestStationUnlockRuntime
    {
        private const string ConfigSection = "RestStationUnlocks";
        private const float MigrationSliceSeconds = 0.50f;
        private const float MigrationSnapshotSeconds = 4f;
        private const int MigrationWorkersPerSlice = 8;
        private const int ManagedStationCount = 8;

        private static readonly Dictionary<RestStationType, List<Piece>> Pieces =
            new Dictionary<RestStationType, List<Piece>>();
        private static readonly HashSet<RestStationType> Unlocked =
            new HashSet<RestStationType>();
        private static readonly List<Character> KnownWorkers = new List<Character>();

        private static ConfigFile _config;
        private static ConfigEntry<string> _playerUnlocks;
        private static Action<string> _debugLog;
        private static Action<string> _infoLog;
        private static long _playerId;
        private static float _nextMigrationScan;
        private static float _nextMigrationSnapshot;
        private static int _migrationCursor;

        internal static void Initialize(ConfigFile config, Action<string> debugLog, Action<string> infoLog)
        {
            _config = config;
            _debugLog = debugLog;
            _infoLog = infoLog;
            _playerUnlocks = null;
            _playerId = 0L;
            _nextMigrationScan = 0f;
            _nextMigrationSnapshot = 0f;
            _migrationCursor = 0;
            Unlocked.Clear();
            Pieces.Clear();
            KnownWorkers.Clear();
        }

        internal static void RegisterPiece(GameObject prefab, RestStationType stationType)
        {
            if (prefab == null || !IsManaged(stationType)) return;

            // The Draugr hunter platform is a work/home station with its own progression and is not
            // part of this rest-station recipe gate. Only the actual species rest pieces are gated.
            if (string.Equals(prefab.name, HunterStandPieceRuntime.PrefabName, StringComparison.OrdinalIgnoreCase))
                return;

            Piece piece = prefab.GetComponent<Piece>() ?? prefab.GetComponentInChildren<Piece>(true);
            if (piece == null) return;

            if (!Pieces.TryGetValue(stationType, out List<Piece> list))
            {
                list = new List<Piece>();
                Pieces[stationType] = list;
            }
            if (!list.Contains(piece)) list.Add(piece);

            // Fail closed until a local player profile has been resolved. This prevents Wood from
            // momentarily unlocking all homes while joining a world.
            piece.m_enabled = _playerId != 0L && Unlocked.Contains(stationType);
        }

        internal static void ObserveWorker(Character character, ZDO zdo)
        {
            if (character == null || zdo == null || !WorkerZdo.IsWorker(zdo)) return;

            Player player = Player.m_localPlayer;
            if (player == null) return;

            long localPlayerId = player.GetPlayerID();
            if (localPlayerId == 0L) return;
            EnsurePlayer(localPlayerId);

            long owner = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            if (owner != localPlayerId) return;

            // Creator/debug Workers are intentionally outside normal progression and must never
            // permanently unlock build recipes on the player's real character. Missing key = true
            // keeps all pre-v0.5.440 natural Workers compatible.
            if (!zdo.GetBool(WorkerZdo.ProgressionEligible, true)) return;

            string species = SpeciesRoleRegistry.NormalizePrefabName(
                zdo.GetString(WorkerZdo.SpeciesKey, character.gameObject != null ? character.gameObject.name : string.Empty));
            if (string.IsNullOrEmpty(species) && character.gameObject != null)
                species = SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name);

            if (TryMapSpecies(species, out RestStationType stationType))
                Unlock(stationType, species);
        }


        internal static void UnlockFromProgression(Player player, RestStationType stationType, string source)
        {
            if (player == null || player != Player.m_localPlayer || !IsManaged(stationType)) return;
            long playerId = player.GetPlayerID();
            if (playerId == 0L) return;
            EnsurePlayer(playerId);
            Unlock(stationType, string.IsNullOrEmpty(source) ? "Progression" : source);
        }

        internal static void Tick()
        {
            Player player = Player.m_localPlayer;
            if (player == null) return;

            long localPlayerId = player.GetPlayerID();
            if (localPlayerId == 0L) return;
            EnsurePlayer(localPlayerId);

            if (Unlocked.Count >= ManagedStationCount) return;

            if (Time.unscaledTime < _nextMigrationScan) return;
            _nextMigrationScan = Time.unscaledTime + MigrationSliceSeconds;

            if (Time.unscaledTime >= _nextMigrationSnapshot || KnownWorkers.Count == 0)
            {
                WorkerFastLookupRuntime.CopyKnownWorkers(KnownWorkers);
                _nextMigrationSnapshot = Time.unscaledTime + MigrationSnapshotSeconds;
                if (_migrationCursor >= KnownWorkers.Count) _migrationCursor = 0;
            }

            int count = KnownWorkers.Count;
            if (count == 0) return;

            int inspected = 0;
            int processed = 0;
            while (inspected < count && processed < MigrationWorkersPerSlice)
            {
                if (_migrationCursor >= count) _migrationCursor = 0;
                Character character = KnownWorkers[_migrationCursor++];
                inspected++;
                processed++;

                if (character == null || character.IsDead()) continue;
                ZNetView view = character.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                ObserveWorker(character, zdo);

                if (Unlocked.Count >= ManagedStationCount)
                {
                    KnownWorkers.Clear();
                    _migrationCursor = 0;
                    break;
                }
            }
        }

        internal static void Shutdown()
        {
            Pieces.Clear();
            Unlocked.Clear();
            KnownWorkers.Clear();
            _playerUnlocks = null;
            _playerId = 0L;
            _nextMigrationScan = 0f;
            _nextMigrationSnapshot = 0f;
            _migrationCursor = 0;
            _config = null;
            _debugLog = null;
            _infoLog = null;
        }

        private static void EnsurePlayer(long playerId)
        {
            if (_playerId == playerId && _playerUnlocks != null) return;

            _playerId = playerId;
            Unlocked.Clear();
            KnownWorkers.Clear();
            _nextMigrationScan = 0f;
            _nextMigrationSnapshot = 0f;
            _migrationCursor = 0;

            if (_config != null)
            {
                string key = "Player_" + playerId;
                _playerUnlocks = _config.Bind(
                    ConfigSection,
                    key,
                    string.Empty,
                    "Persisted Worker rest-station unlocks for this Valheim character. Managed automatically; do not edit while the game is running.");
                ParseUnlocks(_playerUnlocks.Value);
            }
            else
            {
                _playerUnlocks = null;
            }

            ApplyAllPieceStates();
            _debugLog?.Invoke($"Rest-station unlock profile loaded for player {playerId}: {SerializeUnlocks()}.");
        }

        private static void Unlock(RestStationType stationType, string sourceSpecies)
        {
            if (!IsManaged(stationType) || !Unlocked.Add(stationType)) return;

            Persist();
            ApplyPieceState(stationType);
            _infoLog?.Invoke($"[RestUnlock] {WorkerRestStation.GetDisplayName(stationType)} freigeschaltet durch {sourceSpecies}.");
        }

        private static void Persist()
        {
            if (_playerUnlocks == null) return;
            string serialized = SerializeUnlocks();
            if (!string.Equals(_playerUnlocks.Value, serialized, StringComparison.Ordinal))
                _playerUnlocks.Value = serialized;
        }

        private static void ParseUnlocks(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            string[] tokens = raw.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                if (!Enum.TryParse(tokens[i].Trim(), true, out RestStationType stationType)) continue;
                if (IsManaged(stationType)) Unlocked.Add(stationType);
            }
        }

        private static string SerializeUnlocks()
        {
            // Fixed order keeps the config stable and avoids needless rewrites.
            RestStationType[] order =
            {
                RestStationType.Greydwarf,
                RestStationType.Draugr,
                RestStationType.Troll,
                RestStationType.Golem,
                RestStationType.Surtling,
                RestStationType.Neck,
                RestStationType.Drake,
                RestStationType.Fuling
            };

            string result = string.Empty;
            for (int i = 0; i < order.Length; i++)
            {
                if (!Unlocked.Contains(order[i])) continue;
                if (result.Length > 0) result += ",";
                result += order[i].ToString();
            }
            return result;
        }

        private static void ApplyAllPieceStates()
        {
            foreach (KeyValuePair<RestStationType, List<Piece>> pair in Pieces)
                ApplyPieceState(pair.Key);
        }

        private static void ApplyPieceState(RestStationType stationType)
        {
            if (!Pieces.TryGetValue(stationType, out List<Piece> list)) return;
            bool enabled = _playerId != 0L && Unlocked.Contains(stationType);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Piece piece = list[i];
                if (piece == null)
                {
                    list.RemoveAt(i);
                    continue;
                }
                piece.m_enabled = enabled;
            }
        }

        private static bool IsManaged(RestStationType stationType)
        {
            switch (stationType)
            {
                case RestStationType.Greydwarf:
                case RestStationType.Draugr:
                case RestStationType.Troll:
                case RestStationType.Golem:
                case RestStationType.Surtling:
                case RestStationType.Neck:
                case RestStationType.Drake:
                case RestStationType.Fuling:
                    return true;
                default:
                    // Lox is a hidden legacy home and Wolf is not a Worker rest progression path.
                    return false;
            }
        }

        private static bool TryMapSpecies(string species, out RestStationType stationType)
        {
            stationType = RestStationType.Greydwarf;
            if (string.IsNullOrEmpty(species)) return false;

            if (string.Equals(species, "Greyling", StringComparison.OrdinalIgnoreCase) ||
                species.StartsWith("Greydwarf", StringComparison.OrdinalIgnoreCase))
            {
                stationType = RestStationType.Greydwarf;
                return true;
            }
            if (species.StartsWith("Draugr", StringComparison.OrdinalIgnoreCase))
            {
                stationType = RestStationType.Draugr;
                return true;
            }
            if (string.Equals(species, "Troll", StringComparison.OrdinalIgnoreCase))
            {
                stationType = RestStationType.Troll;
                return true;
            }
            if (string.Equals(species, "StoneGolem", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(species, "IceGolem", StringComparison.OrdinalIgnoreCase))
            {
                stationType = RestStationType.Golem;
                return true;
            }
            if (string.Equals(species, "Surtling", StringComparison.OrdinalIgnoreCase))
            {
                stationType = RestStationType.Surtling;
                return true;
            }
            if (string.Equals(species, "Neck", StringComparison.OrdinalIgnoreCase))
            {
                stationType = RestStationType.Neck;
                return true;
            }
            if (string.Equals(species, "Drake", StringComparison.OrdinalIgnoreCase))
            {
                stationType = RestStationType.Drake;
                return true;
            }
            if (species.StartsWith("Fuling", StringComparison.OrdinalIgnoreCase))
            {
                stationType = RestStationType.Fuling;
                return true;
            }
            return false;
        }
    }
}
