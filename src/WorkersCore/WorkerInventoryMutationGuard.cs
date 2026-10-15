using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Prevents Workers from mutating a live Valheim inventory immediately after another system
    /// (player interaction, SmartStorage QuickStack, etc.) changed it. Unity gameplay code is
    /// single-threaded, so a short post-change quiet window is enough to avoid verification races
    /// without hard-depending on SmartStorage.
    /// </summary>
    internal static class WorkerInventoryMutationGuard
    {
        private const float ExternalQuietSeconds = 0.85f;
        private static readonly Dictionary<Inventory, float> QuietUntil = new Dictionary<Inventory, float>();

        [ThreadStatic]
        private static int _workerMutationDepth;

        internal readonly struct MutationScope : IDisposable
        {
            public void Dispose()
            {
                if (_workerMutationDepth > 0) _workerMutationDepth--;
            }
        }

        internal static MutationScope EnterWorkerMutation()
        {
            _workerMutationDepth++;
            return new MutationScope();
        }

        internal static bool CanWorkersMutate(Inventory inventory)
        {
            if (inventory == null) return false;
            if (_workerMutationDepth > 0) return true;
            if (!QuietUntil.TryGetValue(inventory, out float until)) return true;
            if (Time.unscaledTime >= until)
            {
                QuietUntil.Remove(inventory);
                return true;
            }
            return false;
        }

        internal static float GetRemainingQuietTime(Inventory inventory)
        {
            if (inventory == null || !QuietUntil.TryGetValue(inventory, out float until)) return 0f;
            return Mathf.Max(0f, until - Time.unscaledTime);
        }

        internal static void NoteExternalMutation(Inventory inventory)
        {
            if (inventory == null || _workerMutationDepth > 0) return;
            QuietUntil[inventory] = Time.unscaledTime + ExternalQuietSeconds;
        }

        internal static void Clear()
        {
            QuietUntil.Clear();
        }
    }

    [HarmonyPatch]
    internal static class WorkerInventoryChangedActivityPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo withFlags = typeof(Inventory).GetMethod("Changed", flags, null, new[] { typeof(bool), typeof(bool) }, null);
            if (withFlags != null) yield return withFlags;

            MethodInfo noArgs = typeof(Inventory).GetMethod("Changed", flags, null, Type.EmptyTypes, null);
            if (noArgs != null) yield return noArgs;
        }

        private static void Postfix(Inventory __instance)
        {
            WorkerInventoryMutationGuard.NoteExternalMutation(__instance);
        }
    }
}
