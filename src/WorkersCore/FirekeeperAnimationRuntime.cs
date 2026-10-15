using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Plays the Surtling's visible work-cast through Valheim's synchronized animation wrapper.
    /// Deliberately does not call Character.StartAttack, so no combat projectile/damage is spawned.
    /// </summary>
    internal static class FirekeeperAnimationRuntime
    {
        internal static bool TryPlayCast(Character worker)
        {
            if (worker == null) return false;

            try
            {
                Animator animator = worker.GetComponentInChildren<Animator>();
                string trigger = FindAttackTrigger(animator);
                if (string.IsNullOrEmpty(trigger)) trigger = "attack";

                // Valheim creature animations are normally driven through ZSyncAnimation. Calling the
                // raw Unity Animator can be immediately overwritten by the network animation state,
                // which is why the previous Firekeeper trigger was invisible in game.
                Component zsync = FindZSyncAnimation(worker);
                if (zsync != null)
                {
                    MethodInfo setTrigger = AccessTools.Method(zsync.GetType(), "SetTrigger", new[] { typeof(string) });
                    if (setTrigger != null)
                    {
                        setTrigger.Invoke(zsync, new object[] { trigger });
                        return true;
                    }
                }

                // Fallback for an unusual prefab/controller without ZSyncAnimation.
                if (animator != null)
                {
                    animator.SetTrigger(trigger);
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static Component FindZSyncAnimation(Character worker)
        {
            try
            {
                Component[] components = worker.GetComponentsInChildren<Component>(true);
                if (components == null) return null;
                for (int i = 0; i < components.Length; i++)
                {
                    Component component = components[i];
                    if (component == null) continue;
                    Type type = component.GetType();
                    if (type != null && string.Equals(type.Name, "ZSyncAnimation", StringComparison.Ordinal))
                        return component;
                }
            }
            catch { }
            return null;
        }

        private static string FindAttackTrigger(Animator animator)
        {
            if (animator == null) return null;
            try
            {
                string best = null;
                int bestScore = int.MinValue;
                AnimatorControllerParameter[] parameters = animator.parameters;
                if (parameters == null) return null;

                for (int i = 0; i < parameters.Length; i++)
                {
                    AnimatorControllerParameter parameter = parameters[i];
                    if (parameter.type != AnimatorControllerParameterType.Trigger) continue;
                    string name = parameter.name ?? string.Empty;
                    string lower = name.ToLowerInvariant();
                    if (!lower.Contains("attack")) continue;

                    int score = 0;
                    if (lower == "attack") score += 100;
                    if (lower.Contains("attack1")) score += 80;
                    if (lower.Contains("primary")) score += 20;
                    if (lower.Contains("secondary")) score -= 10;
                    if (score <= bestScore) continue;
                    bestScore = score;
                    best = name;
                }
                return best;
            }
            catch { return null; }
        }
    }
}
