using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Reusable low-tier Neck trap. One raw fish arms the trap. After a random half-to-one Valheim
    /// day a Neck is caught. Five further raw fish raise trust in 20% steps; at 100% the player may
    /// open the cage and the exact captive becomes a Fisher worker. The trap then resets for reuse.
    /// </summary>
    internal sealed class NeckTrapRecruitmentRuntime : MonoBehaviour
    {
        internal const string BaitedKey = "vw_neck_trap_baited";
        internal const string CatchAtKey = "vw_neck_trap_catch_at";
        internal const string CapturedKey = "vw_neck_trap_captured";
        internal const string TrustKey = "vw_neck_trap_trust";
        internal const string RecruiterKey = "vw_neck_trap_recruiter";
        internal const string TrapIdKey = "vw_neck_trap_id";
        internal const string CaptiveTrapIdKey = "vw_neck_captive_trap";
        internal const string ReleasedCountKey = "vw_neck_trap_released";
        internal const string FoodPrefab = "FishRaw";
        private static readonly Type FishComponentType = AccessTools.TypeByName("Fish");
        internal const int TrustPerFish = 20;
        internal const int RequiredTrust = 100;

        private const float TickSeconds = 2f;
        private const float IdleNetworkCheckSeconds = 10f;
        private const float FallbackDayLengthSeconds = 1800f;
        private const float CatchMinDays = 0.5f;
        private const float CatchMaxDays = 1.0f;

        private static readonly MethodInfo GetTimeSecondsMethod = typeof(ZNet).GetMethod(
            "GetTimeSeconds", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null);
        private static readonly MethodInfo ClaimOwnershipMethod = typeof(ZNetView).GetMethod(
            "ClaimOwnership", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null);

        private ZNetView _view;
        private Character _captive;
        private bool _resolvedCaptive;

        private void Awake()
        {
            _view = GetComponent<ZNetView>() ?? GetComponentInParent<ZNetView>();
        }

        private void OnEnable()
        {
            ScheduleMonitor(0.35f + ComputeMonitorStagger());
        }

        private void OnDisable()
        {
            CancelInvoke(nameof(MonitorTrap));
        }

        private float ComputeMonitorStagger()
        {
            int id = 0;
            try { id = GetInstanceID() & 0x7fffffff; } catch { }
            return (id % 1000) / 1000f * 1.5f;
        }

        private void ScheduleMonitor(float delay)
        {
            if (!isActiveAndEnabled) return;
            CancelInvoke(nameof(MonitorTrap));
            Invoke(nameof(MonitorTrap), Mathf.Max(0.05f, delay));
        }

        private void MonitorTrap()
        {
            if (!isActiveAndEnabled) return;

            float nextDelay = TickTrap();
            ScheduleMonitor(nextDelay);
        }

        private float TickTrap()
        {
            ZDO zdo = GetZdo();
            if (zdo == null) return 1f;
            EnsureTrapId(zdo);

            bool captured = zdo.GetBool(CapturedKey, false);
            if (captured)
            {
                if (!_resolvedCaptive) ResolveCaptiveOnce(zdo);
                if (_view != null && _view.IsOwner() && (_captive == null || _captive.IsDead()))
                    _captive = SpawnCaptive(zdo);
                return TickSeconds;
            }

            _captive = null;
            _resolvedCaptive = false;
            bool baited = zdo.GetBool(BaitedKey, false);
            if (!baited) return IdleNetworkCheckSeconds;
            if (_view == null || !_view.IsOwner()) return TickSeconds;

            long catchAt = zdo.GetLong(CatchAtKey, 0L);
            if (catchAt <= 0L || GetWorldSeconds() < catchAt) return TickSeconds;

            zdo.Set(CapturedKey, true);
            zdo.Set(TrustKey, 0);
            _resolvedCaptive = true;
            _captive = SpawnCaptive(zdo);
            NotifyLocalRecruiter(zdo, "Die Nixenfalle hat zugeschnappt! Eine Nixe sitzt im Käfig.");
            Plugin.Log?.LogInfo($"Neck trap captured a Neck at {transform.position}.");
            return TickSeconds;
        }

        internal bool TryInteract(Player player)
        {
            if (player == null) return false;
            ZDO zdo = GetZdo();
            if (zdo == null)
            {
                Notify(player, "Die Nixenfalle ist noch nicht netzwerkbereit.");
                return true;
            }
            if (!EnsureOwnership())
            {
                Notify(player, "Die Nixenfalle wird gerade von einem anderen Netzwerkbesitzer verwaltet.");
                return true;
            }

            EnsureTrapId(zdo);
            long playerId = player.GetPlayerID();
            long recruiter = zdo.GetLong(RecruiterKey, 0L);
            bool baited = zdo.GetBool(BaitedKey, false);
            bool captured = zdo.GetBool(CapturedKey, false);
            int trust = Mathf.Clamp(zdo.GetInt(TrustKey, 0), 0, RequiredTrust);

            if ((baited || captured) && recruiter != 0L && recruiter != playerId)
            {
                Notify(player, "Diese Nixenfalle wurde bereits von jemand anderem gestellt.");
                return true;
            }

            if (!baited && !captured)
            {
                if (!TryConsumeOneRawFish(player.GetInventory()))
                {
                    Notify(player, "Lege 1 Fisch als Köder in die Nixenfalle. Roher Fisch und echte gefangene Fische werden akzeptiert.");
                    return true;
                }

                float day = GetDayLengthSeconds();
                float delay = UnityEngine.Random.Range(CatchMinDays * day, CatchMaxDays * day);
                zdo.Set(BaitedKey, true);
                zdo.Set(CapturedKey, false);
                zdo.Set(TrustKey, 0);
                zdo.Set(RecruiterKey, playerId);
                zdo.Set(CatchAtKey, GetWorldSeconds() + Math.Max(30L, (long)Math.Round(delay)));
                ScheduleMonitor(0.10f);
                Notify(player, "Der Fisch liegt als Köder aus. Jetzt heißt es warten, bis eine Nixe in die Falle geht.");
                return true;
            }

            if (baited && !captured)
            {
                Notify(player, "Der Köder liegt aus. Die Falle wartet auf eine Nixe.");
                return true;
            }

            if (captured && trust < RequiredTrust)
            {
                if (!TryConsumeOneRawFish(player.GetInventory()))
                {
                    Notify(player, $"Die gefangene Nixe möchte Fisch. Vertrauen {trust}%.");
                    return true;
                }

                trust = Mathf.Min(RequiredTrust, trust + TrustPerFish);
                zdo.Set(TrustKey, trust);
                Notify(player, trust >= RequiredTrust
                    ? "Die Nixe vertraut dir jetzt vollständig. Benutze die Falle noch einmal, um den Käfig zu öffnen."
                    : $"Fisch angenommen. Nixen-Vertrauen {trust}%.");
                return true;
            }

            return ReleaseAsWorker(zdo, player);
        }

        internal string BuildHover()
        {
            ZDO zdo = GetZdo();
            if (zdo == null) return "Nixenfalle\nNetzwerkstatus nicht verfügbar";

            bool baited = zdo.GetBool(BaitedKey, false);
            bool captured = zdo.GetBool(CapturedKey, false);
            int trust = Mathf.Clamp(zdo.GetInt(TrustKey, 0), 0, RequiredTrust);
            int released = Math.Max(0, zdo.GetInt(ReleasedCountKey, 0));
            string used = released > 0 ? $"\nBereits freigelassen: {released}" : string.Empty;

            if (!baited && !captured)
                return "<color=#77C7D8><b>Nixenfalle</b></color>\nFalle leer" + used +
                       $"\nFisch im Inventar: <color=#9FD6DF>{CountCompatibleFish(Player.m_localPlayer?.GetInventory())}</color>" +
                       "\n[<color=yellow><b>E</b></color>] 1 Fisch als Köder einlegen";

            if (baited && !captured)
                return "<color=#77C7D8><b>Nixenfalle</b></color>\nKöder liegt aus" + used +
                       "\n<color=#9FD6DF>Warten ... irgendwann schnappt sie zu.</color>";

            if (trust < RequiredTrust)
                return "<color=#77C7D8><b>Nixenfalle</b></color>\nNixe im Käfig" + used +
                       $"\nVertrauen: <color=#EFC85B>{trust}%</color>" +
                       $"\nFisch im Inventar: <color=#9FD6DF>{CountCompatibleFish(Player.m_localPlayer?.GetInventory())}</color>" +
                       "\n[<color=yellow><b>E</b></color>] Fisch füttern";

            return "<color=#77C7D8><b>Nixenfalle</b></color>\nNixe im Käfig" + used +
                   "\nVertrauen: <color=#7CFF8C>100%</color>" +
                   "\n[<color=yellow><b>E</b></color>] Käfig öffnen / Nixe freilassen";
        }

        private bool ReleaseAsWorker(ZDO trapZdo, Player player)
        {
            if (!SpeciesRoleRegistry.TryGetRole("Neck", out WorkerRole role))
            {
                Notify(player, "Die Nixe hat noch keine Worker-Rolle.");
                return true;
            }

            if (!_resolvedCaptive) ResolveCaptiveOnce(trapZdo);
            if (_captive == null || _captive.IsDead()) _captive = SpawnCaptive(trapZdo);
            if (_captive == null)
            {
                Notify(player, "Die Nixe konnte gerade nicht aus der Falle gelöst werden. Versuch es gleich noch einmal.");
                return true;
            }

            ZNetView workerView = _captive.GetComponent<ZNetView>() ?? _captive.GetComponentInParent<ZNetView>();
            if (workerView == null || !workerView.IsValid())
            {
                Notify(player, "Die gefangene Nixe hat keinen gültigen Netzwerkstatus.");
                return true;
            }
            if (!workerView.IsOwner() && ClaimOwnershipMethod != null)
            {
                try { ClaimOwnershipMethod.Invoke(workerView, null); } catch { }
            }
            ZDO workerZdo = workerView.IsOwner() ? workerView.GetZDO() : null;
            if (workerZdo == null)
            {
                Notify(player, "Die gefangene Nixe konnte nicht übernommen werden.");
                return true;
            }

            Vector3 release = transform.TransformPoint(new Vector3(0f, 0.25f, -2.0f));
            _captive.transform.position = release;
            MonsterAI ai = _captive.GetComponent<MonsterAI>() ?? _captive.GetComponentInChildren<MonsterAI>();
            if (ai != null) ai.enabled = true;

            NaturalRecruitmentCompletionRuntime.Complete(
                _captive,
                workerZdo,
                player.GetPlayerID(),
                role,
                "Neck",
                release,
                25f,
                minimumDefensive: false,
                followPlayer: player);
            workerZdo.Set(CaptiveTrapIdKey, string.Empty);

            int releasedCount = Math.Max(0, trapZdo.GetInt(ReleasedCountKey, 0)) + 1;
            trapZdo.Set(ReleasedCountKey, releasedCount);
            ResetTrap(trapZdo);
            _captive = null;
            _resolvedCaptive = false;
            ScheduleMonitor(IdleNetworkCheckSeconds + ComputeMonitorStagger());

            Notify(player, "Du öffnest den Käfig. Die Nixe schließt sich dir als Fischer an und folgt dir automatisch. Führe sie in deine Base; mit E kannst du Folgen jederzeit umschalten. Danach baue ihr ein Ufernest und weise es ihr zu.");
            Plugin.Log?.LogInfo($"Neck released from trap as Fisher: player={player.GetPlayerID()}, pos={release}.");
            return true;
        }

        private Character SpawnCaptive(ZDO trapZdo)
        {
            try
            {
                if (ZNetScene.instance == null) return null;
                GameObject prefab = ZNetScene.instance.GetPrefab("Neck");
                if (prefab == null) return null;

                Vector3 pos = transform.TransformPoint(new Vector3(0f, 0.30f, 0f));
                GameObject instance = Instantiate(prefab, pos, transform.rotation);
                Character neck = instance != null ? (instance.GetComponent<Character>() ?? instance.GetComponentInChildren<Character>()) : null;
                if (neck == null) return null;

                ZNetView view = neck.GetComponent<ZNetView>() ?? neck.GetComponentInParent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo == null) return neck;
                zdo.Set(CaptiveTrapIdKey, EnsureTrapId(trapZdo));
                neck.SetTamed(true);
                WorkerCombatStanceRuntime.ClearWorkAttackTargets(neck.GetComponent<MonsterAI>());
                return neck;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("Neck trap captive spawn failed: " + ex.GetBaseException().Message);
                return null;
            }
        }

        private void ResolveCaptiveOnce(ZDO trapZdo)
        {
            _resolvedCaptive = true;
            string trapId = EnsureTrapId(trapZdo);
            float bestSqr = 16f;
            Character best = null;
            List<Character> candidates = WorkerCharacterIndexRuntime.GetTamedNonWorkers();
            for (int i = 0; i < candidates.Count; i++)
            {
                Character character = candidates[i];
                if (character == null || character.IsDead()) continue;
                if (!NeckRecruitmentRuntime.IsNeck(character)) continue;
                ZNetView view = character.GetComponent<ZNetView>() ?? character.GetComponentInParent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo == null || WorkerZdo.IsWorker(zdo)) continue;
                if (!string.Equals(zdo.GetString(CaptiveTrapIdKey, string.Empty), trapId, StringComparison.Ordinal)) continue;
                float sqr = (character.transform.position - transform.position).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = character;
            }
            _captive = best;
            if (_captive != null)
            {
                _captive.SetTamed(true);
                WorkerCombatStanceRuntime.ClearWorkAttackTargets(_captive.GetComponent<MonsterAI>());
            }
        }

        private ZDO GetZdo()
        {
            if (_view == null) _view = GetComponent<ZNetView>() ?? GetComponentInParent<ZNetView>();
            return _view != null && _view.IsValid() ? _view.GetZDO() : null;
        }

        private bool EnsureOwnership()
        {
            if (_view == null || !_view.IsValid()) return false;
            if (_view.IsOwner()) return true;
            try { ClaimOwnershipMethod?.Invoke(_view, null); } catch { }
            return _view.IsOwner();
        }

        private string EnsureTrapId(ZDO zdo)
        {
            string id = zdo != null ? zdo.GetString(TrapIdKey, string.Empty) : string.Empty;
            if (!string.IsNullOrEmpty(id)) return id;
            id = zdo != null ? zdo.m_uid.ToString() : Guid.NewGuid().ToString("N");
            if (zdo != null && _view != null && _view.IsOwner()) zdo.Set(TrapIdKey, id);
            return id;
        }

        private static void ResetTrap(ZDO zdo)
        {
            if (zdo == null) return;
            zdo.Set(BaitedKey, false);
            zdo.Set(CapturedKey, false);
            zdo.Set(TrustKey, 0);
            zdo.Set(RecruiterKey, 0L);
            zdo.Set(CatchAtKey, 0L);
        }

        private static bool TryConsumeOneRawFish(Inventory inventory)
        {
            if (inventory == null) return false;
            List<ItemDrop.ItemData> items = inventory.GetAllItems();
            if (items == null) return false;
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (!IsCompatibleFishItem(item)) continue;
                return GreydwarfOfferingCatalog.TryConsumeOne(inventory, item, Plugin.Instance?.DebugLogPublic);
            }
            return false;
        }

        private static int CountCompatibleFish(Inventory inventory)
        {
            if (inventory == null) return 0;
            List<ItemDrop.ItemData> items = inventory.GetAllItems();
            if (items == null) return 0;
            int total = 0;
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (IsCompatibleFishItem(item)) total += Math.Max(0, item.m_stack);
            }
            return total;
        }

        private static bool IsCompatibleFishItem(ItemDrop.ItemData item)
        {
            if (item == null || item.m_stack <= 0 || item.m_dropPrefab == null) return false;

            string normalized = SpeciesRoleRegistry.NormalizePrefabName(item.m_dropPrefab.name);
            if (string.Equals(normalized, FoodPrefab, StringComparison.OrdinalIgnoreCase)) return true;

            // A freshly caught Valheim fish is not FishRaw. It keeps its species prefab (Fish1,
            // Fish2, Fish4_cave, ...). These are valid bait/feed too; cooked fish and fish meals
            // deliberately do not pass this test. Prefer the real Fish component when available.
            try
            {
                if (FishComponentType != null &&
                    (item.m_dropPrefab.GetComponent(FishComponentType) != null ||
                     item.m_dropPrefab.GetComponentInChildren(FishComponentType, true) != null))
                    return true;
            }
            catch { }

            return !string.IsNullOrEmpty(normalized) && normalized.Length > 4 &&
                   normalized.StartsWith("Fish", StringComparison.OrdinalIgnoreCase) &&
                   char.IsDigit(normalized[4]);
        }

        private static float GetDayLengthSeconds()
        {
            try
            {
                EnvMan env = EnvMan.instance;
                if (env != null && env.m_dayLengthSec > 60f) return env.m_dayLengthSec;
            }
            catch { }
            return FallbackDayLengthSeconds;
        }

        private static long GetWorldSeconds()
        {
            try
            {
                ZNet net = ZNet.instance;
                if (net != null && GetTimeSecondsMethod != null)
                {
                    object value = GetTimeSecondsMethod.Invoke(net, null);
                    if (value != null) return (long)Math.Floor(Convert.ToDouble(value));
                }
            }
            catch { }
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
        }

        private static void Notify(Player player, string text)
        {
            if (player == null || string.IsNullOrEmpty(text)) return;
            try { player.Message(MessageHud.MessageType.Center, text); } catch { }
        }

        private static void NotifyLocalRecruiter(ZDO trapZdo, string text)
        {
            Player player = Player.m_localPlayer;
            if (player == null || trapZdo == null) return;
            long recruiter = trapZdo.GetLong(RecruiterKey, 0L);
            if (recruiter != 0L && recruiter != player.GetPlayerID()) return;
            Notify(player, text);
        }
    }
}
