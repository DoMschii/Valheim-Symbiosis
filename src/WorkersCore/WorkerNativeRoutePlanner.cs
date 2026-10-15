using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Uses Valheim's own Pathfinding.GetPath only as a route source. Workers still owns the
    /// actual locomotion via Character.SetMoveDir, so native MonsterAI decisions cannot fight the
    /// work command. Reflection is cached once because Pathfinding/AgentType details are game API.
    /// </summary>
    internal static class WorkerNativeRoutePlanner
    {
        private static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Type PathfindingType = AccessTools.TypeByName("Pathfinding");
        private static readonly FieldInfo InstanceField = PathfindingType?.GetField("instance", StaticFlags);
        private static readonly PropertyInfo InstanceProperty = PathfindingType?.GetProperty("instance", StaticFlags);
        private static readonly FieldInfo AgentTypeField = typeof(BaseAI).GetField("m_pathAgentType", InstanceFlags);
        private static readonly MethodInfo GetPathMethod = ResolveGetPathMethod();

        private static bool _reportedUnavailable;
        private static bool _reportedInvokeFailure;

        internal static bool Available => PathfindingType != null && GetPathMethod != null && AgentTypeField != null && (InstanceField != null || InstanceProperty != null);

        internal static bool TryBuildRoute(BaseAI ai, Vector3 start, Vector3 destination, out List<Vector3> route, out string detail)
        {
            route = null;
            detail = "native unavailable";
            if (ai == null || !Available)
            {
                ReportUnavailableOnce();
                return false;
            }

            object instance = null;
            try
            {
                instance = InstanceField != null ? InstanceField.GetValue(null) : InstanceProperty?.GetValue(null, null);
            }
            catch
            {
                instance = null;
            }

            if (instance == null)
            {
                detail = "native instance missing";
                return false;
            }

            object agentType;
            try
            {
                agentType = AgentTypeField.GetValue(ai);
            }
            catch
            {
                detail = "agent type unavailable";
                return false;
            }

            var nativePath = new List<Vector3>(32);
            try
            {
                // Mirrors BaseAI.FindPath: GetPath(start, target, path, m_pathAgentType,
                // false, true, false). We only consume the returned points; movement stays ours.
                object result = GetPathMethod.Invoke(instance, new object[]
                {
                    start,
                    destination,
                    nativePath,
                    agentType,
                    false,
                    true,
                    false
                });

                if (!(result is bool ok) || !ok || nativePath.Count == 0)
                {
                    detail = $"native miss ({nativePath.Count} pts)";
                    return false;
                }
            }
            catch (Exception ex)
            {
                if (!_reportedInvokeFailure)
                {
                    _reportedInvokeFailure = true;
                    Exception inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                    Plugin.Log?.LogWarning($"Workers native path invoke failed; coarse fallback remains active. {inner.GetType().Name}: {inner.Message}");
                }
                detail = "native invoke failed";
                return false;
            }

            // BaseAI discards very near path points itself. Do the same here so a path does not
            // begin by asking the creature to turn back into its own current navmesh sample.
            var cleaned = new List<Vector3>(nativePath.Count);
            Vector3 previous = start;
            for (int i = 0; i < nativePath.Count; i++)
            {
                Vector3 point = nativePath[i];
                if (!IsFinite(point)) continue;
                if (Vector3.Distance(start, point) <= 0.65f) continue;
                if (Vector3.Distance(previous, point) <= 0.20f) continue;
                cleaned.Add(point);
                previous = point;
            }

            if (cleaned.Count == 0)
            {
                detail = "native path collapsed";
                return false;
            }

            route = cleaned;
            detail = $"native {cleaned.Count} pts";
            return true;
        }

        internal static void ResetDiagnostics()
        {
            _reportedUnavailable = false;
            _reportedInvokeFailure = false;
        }

        private static MethodInfo ResolveGetPathMethod()
        {
            if (PathfindingType == null) return null;
            MethodInfo[] methods;
            try { methods = PathfindingType.GetMethods(InstanceFlags); }
            catch { return null; }

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (!string.Equals(method.Name, "GetPath", StringComparison.Ordinal)) continue;
                if (method.ReturnType != typeof(bool)) continue;
                ParameterInfo[] p = method.GetParameters();
                if (p.Length != 7) continue;
                if (p[0].ParameterType != typeof(Vector3) || p[1].ParameterType != typeof(Vector3)) continue;
                if (p[2].ParameterType != typeof(List<Vector3>)) continue;
                if (p[4].ParameterType != typeof(bool) || p[5].ParameterType != typeof(bool) || p[6].ParameterType != typeof(bool)) continue;
                return method;
            }
            return null;
        }

        private static void ReportUnavailableOnce()
        {
            if (_reportedUnavailable) return;
            _reportedUnavailable = true;
            Plugin.Log?.LogWarning("Workers native Pathfinding.GetPath API unavailable; using bounded coarse fallback only.");
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                   !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                   !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }
    }
}
