using System;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// v0.5.420: direct feeding of wild Necks is retired. Recruitment now happens through the
    /// player-built Nixenfalle: bait with one raw fish, wait for a captive, feed the captive to
    /// 100% trust, then release that exact Neck as the Fisher worker.
    /// </summary>
    internal static class NeckRecruitmentRuntime
    {
        internal const string FoodPrefab = "FishRaw";
        internal const int RequiredFish = 5;

        internal static bool IsNeck(Character character)
        {
            if (character == null || character is Player) return false;
            return string.Equals(
                SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name),
                "Neck",
                StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryHandleInteract(Character neck, Player player, bool hold)
        {
            if (hold || neck == null || player == null || !IsNeck(neck)) return false;
            ZNetView view = neck.GetComponent<ZNetView>() ?? neck.GetComponentInParent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (WorkerZdo.IsWorker(zdo)) return false;

            try
            {
                player.Message(MessageHud.MessageType.Center,
                    "Wilde Nixen lassen sich nicht direkt anfüttern. Baue eine Nixenfalle am Ufer und lege einen Fisch als Köder hinein.");
            }
            catch { }
            return true;
        }

        internal static string BuildHover(Character neck)
        {
            if (!IsNeck(neck)) return string.Empty;
            ZNetView view = neck.GetComponent<ZNetView>() ?? neck.GetComponentInParent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (WorkerZdo.IsWorker(zdo)) return string.Empty;
            if (zdo != null && !string.IsNullOrEmpty(zdo.GetString(NeckTrapRecruitmentRuntime.CaptiveTrapIdKey, string.Empty)))
                return string.Empty;

            return "<color=#77C7D8><b>Nixe</b></color>\n" +
                   "<color=#9FD6DF>Rekrutierung über eine Nixenfalle am Ufer.</color>";
        }
    }

    [HarmonyPatch(typeof(Player), "Interact", typeof(GameObject), typeof(bool), typeof(bool))]
    internal static class NeckRecruitmentInteractPatch
    {
        [HarmonyPriority(Priority.High)]
        private static bool Prefix(Player __instance, GameObject go, bool hold, bool alt)
        {
            if (__instance == null || go == null || __instance != Player.m_localPlayer) return true;
            Character neck = go.GetComponentInParent<Character>();
            if (neck == null || !NeckRecruitmentRuntime.IsNeck(neck)) return true;

            ZNetView view = neck.GetComponent<ZNetView>() ?? neck.GetComponentInParent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (WorkerZdo.IsWorker(zdo)) return true;
            if (zdo != null && !string.IsNullOrEmpty(zdo.GetString(NeckTrapRecruitmentRuntime.CaptiveTrapIdKey, string.Empty)))
                return false;

            return !NeckRecruitmentRuntime.TryHandleInteract(neck, __instance, hold);
        }
    }

    [HarmonyPatch(typeof(Character), "GetHoverText")]
    internal static class NeckRecruitmentHoverPatch
    {
        [HarmonyPriority(Priority.High)]
        private static void Postfix(Character __instance, ref string __result)
        {
            if (__instance == null || !NeckRecruitmentRuntime.IsNeck(__instance)) return;
            ZNetView view = __instance.GetComponent<ZNetView>() ?? __instance.GetComponentInParent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (WorkerZdo.IsWorker(zdo)) return;
            if (zdo != null && !string.IsNullOrEmpty(zdo.GetString(NeckTrapRecruitmentRuntime.CaptiveTrapIdKey, string.Empty)))
            {
                __result = "<color=#77C7D8><b>Gefangene Nixe</b></color>\nDie Nixenfalle verwaltet Fütterung und Freilassung.";
                return;
            }

            string recruitment = NeckRecruitmentRuntime.BuildHover(__instance);
            if (!string.IsNullOrEmpty(recruitment)) __result = recruitment;
        }
    }
}
