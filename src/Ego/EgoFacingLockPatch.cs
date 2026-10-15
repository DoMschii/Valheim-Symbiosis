using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// Valheim Ego v1.6.48
//
// Ego:
//   Native first-person controls stay unchanged. Body follows view yaw so
//   W=forward, S=backwards and A/D=strafe.
//
// v1.6.48 persists the current Ego/Third-Person choice per player across full game restarts.
// It deliberately does NOT patch Werewolf/Serpent Third Person.
// Workers Mount owns symbiosis movement/facing so the free camera and body
// cannot fight each other.

[HarmonyPatch]
internal static class ValheimEgoFacingLockPatch
{
    private static Player _boundPlayer;
    private static Transform _head;
    private static bool _reportedEgoActive;
    private static bool _reportedTargets;

    // v1.6.48: global perspective persistence. FirstPersonPlugin historically kept the
    // runtime state only in memory, so quitting while in Ego could restart in
    // Third Person. Store the effective F6 target per Valheim player and restore
    // it as soon as the local Player is bound. Workers Mount already reads
    // FirstPersonPlugin.FirstPersonEnabled when symbiosis starts, so restoring
    // this state also makes every creature inherit the perspective used before
    // entering symbiosis.
    private const string PerspectivePrefPrefix = "Valheim_Ego_Perspective_v1_";
    private static bool _perspectiveRestoredForBoundPlayer;
    private static bool _lastPersistedPerspective;
    private static bool _lastPersistedPerspectiveValid;
    private static Type _egoPluginType;
    private static FieldInfo _egoFirstPersonField;
    private static FieldInfo _egoTransitionActiveField;
    private static FieldInfo _egoTransitionTargetField;
    private static PropertyInfo _egoInstanceProperty;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        var yielded = new HashSet<MethodBase>();

        MethodInfo characterMethod = AccessTools.DeclaredMethod(typeof(Character), "AlwaysRotateCamera", Type.EmptyTypes);
        if (characterMethod != null && yielded.Add(characterMethod))
            yield return characterMethod;

        MethodInfo playerMethod = AccessTools.DeclaredMethod(typeof(Player), "AlwaysRotateCamera", Type.EmptyTypes);
        if (playerMethod != null && yielded.Add(playerMethod))
            yield return playerMethod;
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Character __instance, ref bool __result, MethodBase __originalMethod)
    {
        try
        {
            Player player = Player.m_localPlayer;
            if (player == null || __instance == null || __instance != player)
                return;

            if (_boundPlayer != player)
                Bind(player);

            SyncPerspectivePreference(player);

            if (!IsSkullLockedFirstPerson(player))
            {
                _reportedEgoActive = false;
                return;
            }

            __result = true;
            if (!_reportedEgoActive)
            {
                _reportedEgoActive = true;
                string target = __originalMethod != null
                    ? __originalMethod.DeclaringType.Name + "." + __originalMethod.Name
                    : "AlwaysRotateCamera";
                Debug.Log("[Valheim Ego] Ego facing ACTIVE via " + target + ": view-yaw facing; S=backwards, A/D=strafe.");
            }
        }
        catch (Exception ex)
        {
            if (_reportedEgoActive)
                Debug.LogWarning("[Valheim Ego] Ego facing rule suspended after transient error: " + ex.GetType().Name + ": " + ex.Message);
            _reportedEgoActive = false;
            _boundPlayer = null;
            _head = null;
            _perspectiveRestoredForBoundPlayer = false;
            _lastPersistedPerspectiveValid = false;
        }
    }

    internal static bool IsSkullLockedFirstPerson(Player player)
    {
        Camera cam = Camera.main;
        if (cam == null || player == null)
            return false;

        if (_boundPlayer != player || _head == null)
            Bind(player);

        if (_head != null)
        {
            float sqr = (cam.transform.position - _head.position).sqrMagnitude;
            return sqr <= 0.85f * 0.85f;
        }

        return (cam.transform.position - player.transform.position).sqrMagnitude <= 2.35f * 2.35f;
    }

    private static void Bind(Player player)
    {
        _boundPlayer = player;
        _perspectiveRestoredForBoundPlayer = false;
        _lastPersistedPerspectiveValid = false;
        Transform visual = player != null ? player.transform.Find("Visual") : null;
        _head = visual != null ? FindRecursive(visual, "Head") : null;

        if (!_reportedTargets)
        {
            _reportedTargets = true;
            Debug.Log("[Valheim Ego] Facing-rule v1.6.48 bound: Ego/Third perspective persists across restart; Symbiosis inherits the current perspective.");
        }
    }

    private static void SyncPerspectivePreference(Player player)
    {
        if (player == null || player.GetPlayerID() == 0L)
            return;

        if (!ResolveEgoPluginReflection())
            return;

        string key = PerspectivePrefPrefix + player.GetPlayerID();
        object instance = null;
        try { instance = _egoInstanceProperty != null ? _egoInstanceProperty.GetValue(null, null) : null; }
        catch { }

        if (!_perspectiveRestoredForBoundPlayer)
        {
            _perspectiveRestoredForBoundPlayer = true;
            if (PlayerPrefs.HasKey(key))
            {
                bool saved = PlayerPrefs.GetInt(key, 0) != 0;
                try
                {
                    _egoFirstPersonField.SetValue(null, saved);
                    if (instance != null && _egoTransitionTargetField != null)
                        _egoTransitionTargetField.SetValue(instance, saved);
                    if (instance != null && _egoTransitionActiveField != null)
                        _egoTransitionActiveField.SetValue(instance, false);
                    _lastPersistedPerspective = saved;
                    _lastPersistedPerspectiveValid = true;
                    Debug.Log("[Valheim Ego] Restored saved perspective for player " + player.GetPlayerID() + ": " + (saved ? "Ego" : "Third Person") + ".");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Valheim Ego] Perspective restore failed safely: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        bool effective = false;
        // While Symbiosis owns Camera.main, Valheim Ego is deliberately suspended with
        // FirstPersonEnabled=false. That false value is NOT a real Third-Person choice.
        // Read the active creature camera mode instead so F6 remains one global setting
        // shared by player + Drake + Troll + Serpent + Werewolf.
        if (!TryReadSymbiosisPerspective(out bool symbiosisActive, out bool symbiosisEgo) || !symbiosisActive)
        {
            try
            {
                effective = (bool)_egoFirstPersonField.GetValue(null);
                if (instance != null && _egoTransitionActiveField != null && _egoTransitionTargetField != null)
                {
                    bool transitionActive = (bool)_egoTransitionActiveField.GetValue(instance);
                    if (transitionActive)
                        effective = (bool)_egoTransitionTargetField.GetValue(instance);
                }
            }
            catch { return; }
        }
        else
        {
            effective = symbiosisEgo;
        }

        if (!_lastPersistedPerspectiveValid || effective != _lastPersistedPerspective || !PlayerPrefs.HasKey(key))
        {
            PlayerPrefs.SetInt(key, effective ? 1 : 0);
            PlayerPrefs.Save();
            _lastPersistedPerspective = effective;
            _lastPersistedPerspectiveValid = true;
            Debug.Log("[Valheim Ego] Saved perspective: " + (effective ? "Ego" : "Third Person") + ".");
        }
    }


    private static bool TryReadSymbiosisPerspective(out bool active, out bool ego)
    {
        active = false;
        ego = false;
        try
        {
            Type runtimeType = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                runtimeType = assembly.GetType("Valheim_Workers.DrakeMountRuntime", false);
                if (runtimeType != null) break;
            }
            if (runtimeType == null) return false;

            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo activeProperty = runtimeType.GetProperty("Active", flags);
            PropertyInfo egoProperty = runtimeType.GetProperty("IsEgoCameraActive", flags);
            if (activeProperty == null || egoProperty == null) return false;

            active = (bool)activeProperty.GetValue(null, null);
            ego = active && (bool)egoProperty.GetValue(null, null);
            return true;
        }
        catch
        {
            active = false;
            ego = false;
            return false;
        }
    }

    private static bool ResolveEgoPluginReflection()
    {
        if (_egoPluginType != null && _egoFirstPersonField != null)
            return true;

        _egoPluginType = AccessTools.TypeByName("Valheim_Ego.FirstPersonPlugin");
        if (_egoPluginType == null)
            return false;

        const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        _egoFirstPersonField = _egoPluginType.GetField("FirstPersonEnabled", flags);
        _egoTransitionActiveField = _egoPluginType.GetField("transitionActive", flags);
        _egoTransitionTargetField = _egoPluginType.GetField("transitionToFirstPerson", flags);
        _egoInstanceProperty = _egoPluginType.GetProperty("Instance", flags);
        return _egoFirstPersonField != null && _egoFirstPersonField.FieldType == typeof(bool);
    }

    private static Transform FindRecursive(Transform root, string name)
    {
        if (root == null) return null;
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; ++i)
        {
            Transform found = FindRecursive(root.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }
}
