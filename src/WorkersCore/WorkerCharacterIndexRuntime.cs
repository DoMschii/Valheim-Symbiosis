using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    internal static class WorkerCharacterIndexRuntime
    {
        private const float RefreshSeconds = 1.0f;

        private static readonly List<Character> LiveNonPlayers = new List<Character>(128);
        private static readonly List<Character> UntamedNonPlayers = new List<Character>(96);
        private static readonly List<Character> TamedNonWorkers = new List<Character>(64);
        private static readonly List<Player> LivePlayers = new List<Player>(8);
        private static float _nextRefresh;

        internal static List<Character> GetLiveNonPlayers() { RefreshIfDue(); return LiveNonPlayers; }
        internal static List<Character> GetUntamedNonPlayers() { RefreshIfDue(); return UntamedNonPlayers; }
        internal static List<Character> GetTamedNonWorkers() { RefreshIfDue(); return TamedNonWorkers; }
        internal static List<Player> GetLivePlayers() { RefreshIfDue(); return LivePlayers; }

        internal static void Clear()
        {
            _nextRefresh = 0f;
            LiveNonPlayers.Clear();
            UntamedNonPlayers.Clear();
            TamedNonWorkers.Clear();
            LivePlayers.Clear();
        }

        private static void RefreshIfDue()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextRefresh) return;
            _nextRefresh = now + RefreshSeconds;

            LiveNonPlayers.Clear();
            UntamedNonPlayers.Clear();
            TamedNonWorkers.Clear();
            LivePlayers.Clear();

            List<Character> all = null;
            try { all = Character.GetAllCharacters(); } catch { }

            if (all == null)
            {
                Player local = Player.m_localPlayer;
                if (local != null && !local.IsDead()) LivePlayers.Add(local);
                return;
            }

            for (int i = 0; i < all.Count; i++)
            {
                Character character = all[i];
                if (character == null || character.gameObject == null ||
                    !character.gameObject.activeInHierarchy || character.IsDead()) continue;

                if (character is Player player)
                {
                    LivePlayers.Add(player);
                    continue;
                }

                LiveNonPlayers.Add(character);

                bool tamed = false;
                try { tamed = character.IsTamed(); } catch { }

                if (!tamed)
                {
                    UntamedNonPlayers.Add(character);
                    continue;
                }

                ZDO zdo = null;
                try
                {
                    ZNetView view = character.GetComponent<ZNetView>();
                    zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                }
                catch { }

                if (!WorkerZdo.IsWorker(zdo))
                    TamedNonWorkers.Add(character);
            }
        }
    }
}
