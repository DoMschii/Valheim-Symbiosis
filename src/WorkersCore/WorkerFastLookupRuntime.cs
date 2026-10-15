using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Hot-path cache for the Harmony hooks that run on MonsterAI.UpdateAI / BaseAI.FindEnemy.
    /// Most creatures in a loaded Valheim area are not Workers. Re-resolving Character, ZNetView
    /// and the recruited ZDO flag from every Harmony hook every AI frame is unnecessary and scales
    /// badly with creature count. Cache those stable component references and negative Worker
    /// results briefly; recruited creatures are promoted immediately by RegisterKnownWorker.
    /// </summary>
    internal static class WorkerFastLookupRuntime
    {
        private sealed class Entry
        {
            internal Character Character;
            internal ZNetView View;
            internal ZDO Zdo;
            internal bool IsWorker;
            internal float NextNegativeRefresh;
            internal float LastAccess;
        }

        private const float NegativeRefreshSeconds = 2f;
        private const float CleanupSeconds = 120f;
        private const float StaleEntrySeconds = 90f;

        private static readonly Dictionary<int, Entry> Characters = new Dictionary<int, Entry>();
        private static readonly Dictionary<int, int> MonsterToCharacter = new Dictionary<int, int>();
        private static readonly List<int> StaleCharacterKeys = new List<int>();
        private static readonly List<int> StaleMonsterKeys = new List<int>();
        private static float _nextCleanupAt;

        internal static bool TryGetWorker(MonsterAI ai, bool requireOwner, out Character worker, out ZNetView view, out ZDO zdo)
        {
            worker = null;
            view = null;
            zdo = null;
            if (ai == null) return false;

            int aiKey = ai.GetInstanceID();
            if (MonsterToCharacter.TryGetValue(aiKey, out int characterKey) && Characters.TryGetValue(characterKey, out Entry cached))
            {
                if (TryUseEntry(cached, requireOwner, out worker, out view, out zdo))
                    return true;
                if (cached.Character != null && cached.View != null)
                    return false;
                MonsterToCharacter.Remove(aiKey);
                Characters.Remove(characterKey);
            }

            Character character = ai.GetComponent<Character>();
            if (character == null || character is Player || character.IsDead()) return false;
            characterKey = character.GetInstanceID();
            Entry entry = Resolve(character);
            MonsterToCharacter[aiKey] = characterKey;
            return TryUseEntry(entry, requireOwner, out worker, out view, out zdo);
        }

        internal static bool TryGetWorker(Character character, bool requireOwner, out ZNetView view, out ZDO zdo)
        {
            view = null;
            zdo = null;
            if (character == null || character is Player || character.IsDead()) return false;
            Entry entry = Resolve(character);
            Character ignored;
            return TryUseEntry(entry, requireOwner, out ignored, out view, out zdo);
        }

        internal static void RegisterKnownWorker(Character character, ZDO zdo = null)
        {
            if (character == null || character is Player) return;
            Entry entry = Resolve(character);
            if (entry == null) return;
            if (zdo != null) entry.Zdo = zdo;
            entry.IsWorker = entry.Zdo != null && WorkerZdo.IsWorker(entry.Zdo);
            if (entry.IsWorker)
            {
                // v0.5.440: recruitment completion stores the canonical species immediately.
                // Several downstream systems (home compatibility, food identity, background work and
                // recipe progression) use this durable key; waiting for the background scheduler made
                // freshly recruited Workers temporarily look species-less.
                string species = SpeciesRoleRegistry.NormalizePrefabName(
                    entry.Zdo.GetString(WorkerZdo.SpeciesKey, character.gameObject != null ? character.gameObject.name : string.Empty));
                if (string.IsNullOrEmpty(species) && character.gameObject != null)
                    species = SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name);
                if (!string.IsNullOrEmpty(species))
                    entry.Zdo.Set(WorkerZdo.SpeciesKey, species);

                RestStationUnlockRuntime.ObserveWorker(character, entry.Zdo);
            }
            entry.NextNegativeRefresh = 0f;
            entry.LastAccess = Time.unscaledTime;

            MonsterAI ai = character.GetComponent<MonsterAI>();
            if (ai != null) MonsterToCharacter[ai.GetInstanceID()] = character.GetInstanceID();
        }


        internal static void CopyKnownWorkers(List<Character> target)
        {
            if (target == null) return;
            CleanupIfDue();
            target.Clear();
            foreach (Entry entry in Characters.Values)
            {
                if (entry == null || !entry.IsWorker || entry.Character == null || entry.Character.IsDead()) continue;
                target.Add(entry.Character);
            }
        }

        internal static void Clear()
        {
            Characters.Clear();
            MonsterToCharacter.Clear();
            StaleCharacterKeys.Clear();
            StaleMonsterKeys.Clear();
            _nextCleanupAt = 0f;
        }

        private static Entry Resolve(Character character)
        {
            CleanupIfDue();
            int key = character.GetInstanceID();
            if (Characters.TryGetValue(key, out Entry entry) && entry.Character == character)
                return entry;

            ZNetView view = character.GetComponent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            entry = new Entry
            {
                Character = character,
                View = view,
                Zdo = zdo,
                IsWorker = WorkerZdo.IsWorker(zdo),
                NextNegativeRefresh = Time.unscaledTime + NegativeRefreshSeconds,
                LastAccess = Time.unscaledTime
            };
            Characters[key] = entry;
            return entry;
        }

        private static bool TryUseEntry(Entry entry, bool requireOwner, out Character worker, out ZNetView view, out ZDO zdo)
        {
            worker = null;
            view = null;
            zdo = null;
            if (entry == null || entry.Character == null || entry.Character.IsDead()) return false;

            entry.LastAccess = Time.unscaledTime;
            if (entry.View == null || !entry.View.IsValid())
            {
                entry.View = entry.Character.GetComponent<ZNetView>();
                entry.Zdo = entry.View != null && entry.View.IsValid() ? entry.View.GetZDO() : null;
                entry.IsWorker = WorkerZdo.IsWorker(entry.Zdo);
                entry.NextNegativeRefresh = Time.unscaledTime + NegativeRefreshSeconds;
            }
            else if (entry.Zdo == null)
            {
                entry.Zdo = entry.View.GetZDO();
                entry.IsWorker = WorkerZdo.IsWorker(entry.Zdo);
            }

            if (!entry.IsWorker)
            {
                if (Time.unscaledTime < entry.NextNegativeRefresh) return false;
                entry.NextNegativeRefresh = Time.unscaledTime + NegativeRefreshSeconds;
                entry.IsWorker = WorkerZdo.IsWorker(entry.Zdo);
                if (!entry.IsWorker) return false;
            }

            if (requireOwner && !entry.View.IsOwner()) return false;
            worker = entry.Character;
            view = entry.View;
            zdo = entry.Zdo;
            return zdo != null;
        }

        private static void CleanupIfDue()
        {
            float now = Time.unscaledTime;
            if (now < _nextCleanupAt) return;
            _nextCleanupAt = now + CleanupSeconds;

            StaleCharacterKeys.Clear();
            foreach (KeyValuePair<int, Entry> pair in Characters)
            {
                Entry entry = pair.Value;
                if (entry == null || entry.Character == null || now - entry.LastAccess > StaleEntrySeconds)
                    StaleCharacterKeys.Add(pair.Key);
            }
            for (int i = 0; i < StaleCharacterKeys.Count; i++) Characters.Remove(StaleCharacterKeys[i]);

            StaleMonsterKeys.Clear();
            foreach (KeyValuePair<int, int> pair in MonsterToCharacter)
                if (!Characters.ContainsKey(pair.Value)) StaleMonsterKeys.Add(pair.Key);
            for (int i = 0; i < StaleMonsterKeys.Count; i++) MonsterToCharacter.Remove(StaleMonsterKeys[i]);
        }
    }
}
