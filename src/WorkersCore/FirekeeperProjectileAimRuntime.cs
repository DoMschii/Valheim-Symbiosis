using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Redirects the real Surtling fireball spawned by a Firekeeper work attack toward the exact
    /// fireplace selected by the job. The native projectile speed is preserved; only its direction
    /// is replaced. Context is short lived and consumed by the first Projectile.Setup owned by the
    /// armed worker, so normal combat projectiles remain untouched.
    /// </summary>
    internal static class FirekeeperProjectileAimRuntime
    {
        private sealed class AimContext
        {
            internal Vector3 Target;
            internal float ExpiresAt;
        }

        private static readonly Dictionary<int, AimContext> Aims = new Dictionary<int, AimContext>();

        internal static void Arm(Character worker, Vector3 target)
        {
            if (worker == null) return;
            Aims[worker.GetInstanceID()] = new AimContext
            {
                Target = target,
                // The fireball is spawned by an animation event shortly after StartAttack returns.
                ExpiresAt = Time.time + 2.0f
            };
        }

        internal static bool TryRedirect(Character owner, Projectile projectile, ref Vector3 velocity)
        {
            if (owner == null || projectile == null) return false;

            int key = owner.GetInstanceID();
            if (!Aims.TryGetValue(key, out AimContext context)) return false;
            if (Time.time > context.ExpiresAt)
            {
                Aims.Remove(key);
                return false;
            }

            Vector3 origin = projectile.transform.position;
            Vector3 delta = context.Target - origin;
            float speed = velocity.magnitude;
            if (delta.sqrMagnitude < 0.0025f || speed < 0.01f)
            {
                Aims.Remove(key);
                return false;
            }

            Vector3 redirected = delta.normalized * speed;
            velocity = redirected;

            // Keep the projectile visual aligned with its corrected trajectory as well.
            try { projectile.transform.rotation = Quaternion.LookRotation(redirected.normalized, Vector3.up); }
            catch { }

            Aims.Remove(key);
            return true;
        }
    }

    [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
    internal static class FirekeeperProjectileSetupPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(Projectile __instance, Character owner, ref Vector3 velocity)
        {
            FirekeeperProjectileAimRuntime.TryRedirect(owner, __instance, ref velocity);
        }
    }
}
