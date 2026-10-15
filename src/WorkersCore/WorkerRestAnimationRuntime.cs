using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Generic visible rest presentation for loaded Workers. Gameplay rest remains owned by
    /// WorkerRestRuntime; this layer only selects native creature animation parameters/states.
    /// It never invents combat actions and fails closed when a creature controller has no rest pose.
    /// </summary>
    internal static class WorkerRestAnimationRuntime
    {
        private sealed class RestBinding
        {
            internal Animator Animator;
            internal Component ZSync;
            internal string Parameter;
            internal AnimatorControllerParameterType ParameterType;
            internal string State;
            internal float NextRefresh;
            internal bool Active;
            internal AnimationClip StoneDormantClip;
            internal bool StonePoseActive;
            internal bool AnimatorWasEnabled;
            internal float AnimatorWasSpeed;
            internal float StoneTransitionStarted;
            internal bool StoneCollapsing;
            internal bool StoneWaking;
            internal ParticleSystem[] StoneParticles;
            internal bool[] StoneParticlesWerePlaying;
            internal Light[] StoneLights;
            internal bool[] StoneLightsWereEnabled;
            internal Behaviour[] StoneVisualEffects;
            internal bool[] StoneVisualEffectsWereEnabled;
        }

        private static readonly Dictionary<int, RestBinding> Bindings = new Dictionary<int, RestBinding>();
        private static readonly HashSet<string> ReportedMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static Action<string> _debug;

        internal static void Initialize(Action<string> debug) => _debug = debug;

        internal static void Shutdown()
        {
            Bindings.Clear();
            ReportedMissing.Clear();
            _debug = null;
        }

        internal static bool ReleaseIfNeeded(Character worker, WorkerState state)
        {
            if (worker == null) return false;
            if (IsPresentationRestState(state)) return false;

            int key = worker.GetInstanceID();
            Bindings.TryGetValue(key, out RestBinding binding);

            // v0.5.178: do not snap the Stone Golem from its compact boulder pose straight back to
            // the full rig. Play the same native wake-up clip forward over time while navigation is
            // held. This mirrors the vanilla "rock builds itself" presentation instead of changing
            // size in one frame. The permanent cyan/black dormant VFX are suppressed while parked.
            if (IsStoneGolem(worker) && binding != null && binding.StonePoseActive &&
                binding.Animator != null && binding.StoneDormantClip != null)
            {
                try
                {
                    // v0.5.180: do NOT keep vanilla MonsterAI sleeping while we manually sample
                    // the dormant rock pose. Vanilla sleeping has a proximity wake check; walking near
                    // the parked Worker wakes it for a frame and spawns the cyan wake particles.
                    // Our authored pose is visual-only, so keep vanilla sleep authority OFF here.
                    ForceStoneGolemSleepAuthority(worker, null, false);
                    SuppressStoneDormantEffects(binding);

                    if (!binding.StoneWaking)
                    {
                        binding.StoneWaking = true;
                        binding.StoneCollapsing = false;
                        binding.StoneTransitionStarted = Time.time;
                        binding.Animator.enabled = false;
                    }

                    float duration = StoneTransitionDuration(binding.StoneDormantClip);
                    float t = Mathf.Clamp01((Time.time - binding.StoneTransitionStarted) / duration);
                    float sampleTime = Mathf.Lerp(0f, Mathf.Max(0f, binding.StoneDormantClip.length * 0.985f), t);
                    binding.StoneDormantClip.SampleAnimation(binding.Animator.gameObject, sampleTime);

                    if (t < 1f)
                        return true; // block Worker navigation until the native build-up is complete

                    binding.Animator.enabled = binding.AnimatorWasEnabled;
                    binding.Animator.speed = binding.AnimatorWasSpeed;
                    if (binding.Animator.enabled)
                    {
                        binding.Animator.Rebind();
                        binding.Animator.Update(0f);
                    }
                    RestoreStoneDormantEffects(binding);
                    ForceStoneGolemSleepAuthority(worker, null, false);
                    binding.StonePoseActive = false;
                    binding.StoneWaking = false;
                    binding.StoneCollapsing = false;
                    binding.Active = false;
                    return false;
                }
                catch
                {
                    RestoreStoneDormantEffects(binding);
                    ForceStoneGolemSleepAuthority(worker, null, false);
                    binding.StonePoseActive = false;
                    binding.StoneWaking = false;
                    binding.StoneCollapsing = false;
                }
            }
            else if (IsStoneGolem(worker))
            {
                ForceStoneGolemSleepAuthority(worker, null, false);
            }

            if (binding == null || !binding.Active) return false;

            if (!string.IsNullOrEmpty(binding.Parameter) && binding.ParameterType == AnimatorControllerParameterType.Bool)
                TrySetBool(binding, false);
            binding.Active = false;
            return false;
        }

        internal static bool TryMaintain(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return false;
            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (!IsPresentationRestState(state)) return false;

            int key = worker.GetInstanceID();
            if (!Bindings.TryGetValue(key, out RestBinding binding) || binding == null || binding.Animator == null)
            {
                binding = Discover(worker);
                Bindings[key] = binding;
            }
            if (binding == null || binding.Animator == null) return false;

            // v0.5.178: the native wake-up clip is also our collapse animation in reverse. Entering
            // rest now visibly folds the full Golem into the authored boulder pose instead of snapping
            // from large to small. Once frame zero is reached, keep that pose frozen. Dormant particle
            // columns/lights are explicitly suppressed so the hidden "rock" does not glow continuously.
            if (IsStoneGolem(worker) && binding.StoneDormantClip != null)
            {
                // v0.5.180: the parked Worker uses the native wake clip as a manually sampled
                // visual pose, but must NOT be flagged as vanilla sleeping. MonsterAI wakes sleeping
                // creatures when the player enters its wake radius, which is what caused the particle
                // burst only when approaching the Golem. Keeping sleeping=false prevents that wake
                // event while the animation remains frozen/collapsed by this runtime.
                ForceStoneGolemSleepAuthority(worker, zdo, false);
                try
                {
                    if (!binding.StonePoseActive)
                    {
                        binding.AnimatorWasEnabled = binding.Animator.enabled;
                        binding.AnimatorWasSpeed = binding.Animator.speed;
                        CacheStoneDormantEffects(worker, binding);
                        binding.StonePoseActive = true;
                        binding.StoneCollapsing = true;
                        binding.StoneWaking = false;
                        binding.StoneTransitionStarted = Time.time;
                    }

                    SuppressStoneDormantEffects(binding);
                    binding.Animator.enabled = false;

                    if (binding.StoneCollapsing)
                    {
                        float duration = StoneTransitionDuration(binding.StoneDormantClip);
                        float t = Mathf.Clamp01((Time.time - binding.StoneTransitionStarted) / duration);
                        float sampleTime = Mathf.Lerp(
                            Mathf.Max(0f, binding.StoneDormantClip.length * 0.985f),
                            0f,
                            t);
                        binding.StoneDormantClip.SampleAnimation(binding.Animator.gameObject, sampleTime);
                        if (t >= 1f) binding.StoneCollapsing = false;
                    }
                    else
                    {
                        binding.StoneDormantClip.SampleAnimation(binding.Animator.gameObject, 0f);
                    }

                    binding.Active = true;
                    return true;
                }
                catch (Exception ex)
                {
                    if (ReportedMissing.Add("StoneGolem:DormantSampleFailed"))
                        _debug?.Invoke($"Rest animation: StoneGolem dormant wake-up sampling failed ({ex.GetType().Name}); using vanilla sleep fallback.");
                }
            }

            if (!string.IsNullOrEmpty(binding.Parameter))
            {
                if (binding.ParameterType == AnimatorControllerParameterType.Bool)
                {
                    // v0.5.176: the Stone Golem does not enter its native boulder pose from the
                    // animator parameter alone. MonsterAI.m_sleeping + the persistent ZDO sleeping
                    // bit are the real authority used by vanilla. Keep all three in sync while home.
                    bool stoneGolem = IsStoneGolem(worker);
                    if (stoneGolem)
                        ForceStoneGolemSleepAuthority(worker, zdo, true);
                    if (!binding.Active || stoneGolem) TrySetBool(binding, true);
                    binding.Active = true;
                    return true;
                }

                if (binding.ParameterType == AnimatorControllerParameterType.Trigger && Time.time >= binding.NextRefresh)
                {
                    binding.NextRefresh = Time.time + 4f;
                    TrySetTrigger(binding);
                    binding.Active = true;
                    return true;
                }
                return binding.Active;
            }

            if (!string.IsNullOrEmpty(binding.State) && Time.time >= binding.NextRefresh)
            {
                binding.NextRefresh = Time.time + 4f;
                try
                {
                    binding.Animator.CrossFade(binding.State, 0.2f, 0, 0f);
                    binding.Active = true;
                    return true;
                }
                catch { }
            }
            return binding.Active;
        }


        private static float StoneTransitionDuration(AnimationClip clip)
        {
            if (clip == null) return 1.4f;
            // Respect the authored clip length where practical, but keep Workers responsive if a
            // controller contains unusually short/long padding.
            return Mathf.Clamp(clip.length, 0.9f, 2.4f);
        }

        private static void CacheStoneDormantEffects(Character worker, RestBinding binding)
        {
            if (worker == null || binding == null || binding.StoneParticles != null) return;
            try
            {
                binding.StoneParticles = worker.GetComponentsInChildren<ParticleSystem>(true);
                binding.StoneParticlesWerePlaying = new bool[binding.StoneParticles.Length];
                for (int i = 0; i < binding.StoneParticles.Length; i++)
                    binding.StoneParticlesWerePlaying[i] = binding.StoneParticles[i] != null && binding.StoneParticles[i].isPlaying;

                binding.StoneLights = worker.GetComponentsInChildren<Light>(true);
                binding.StoneLightsWereEnabled = new bool[binding.StoneLights.Length];
                for (int i = 0; i < binding.StoneLights.Length; i++)
                    binding.StoneLightsWereEnabled[i] = binding.StoneLights[i] != null && binding.StoneLights[i].enabled;

                Component[] all = worker.GetComponentsInChildren<Component>(true);
                var vfx = new List<Behaviour>();
                for (int i = 0; i < all.Length; i++)
                {
                    Behaviour b = all[i] as Behaviour;
                    if (b == null) continue;
                    string typeName = b.GetType().FullName ?? b.GetType().Name;
                    if (typeName.IndexOf("VisualEffect", StringComparison.OrdinalIgnoreCase) >= 0)
                        vfx.Add(b);
                }
                binding.StoneVisualEffects = vfx.ToArray();
                binding.StoneVisualEffectsWereEnabled = new bool[binding.StoneVisualEffects.Length];
                for (int i = 0; i < binding.StoneVisualEffects.Length; i++)
                    binding.StoneVisualEffectsWereEnabled[i] = binding.StoneVisualEffects[i] != null && binding.StoneVisualEffects[i].enabled;
            }
            catch { }
        }

        private static void SuppressStoneDormantEffects(RestBinding binding)
        {
            if (binding == null) return;
            try
            {
                if (binding.StoneParticles != null)
                    for (int i = 0; i < binding.StoneParticles.Length; i++)
                        if (binding.StoneParticles[i] != null)
                            binding.StoneParticles[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                if (binding.StoneLights != null)
                    for (int i = 0; i < binding.StoneLights.Length; i++)
                        if (binding.StoneLights[i] != null) binding.StoneLights[i].enabled = false;
                if (binding.StoneVisualEffects != null)
                    for (int i = 0; i < binding.StoneVisualEffects.Length; i++)
                        if (binding.StoneVisualEffects[i] != null) binding.StoneVisualEffects[i].enabled = false;
            }
            catch { }
        }

        private static void RestoreStoneDormantEffects(RestBinding binding)
        {
            if (binding == null) return;
            try
            {
                if (binding.StoneLights != null && binding.StoneLightsWereEnabled != null)
                    for (int i = 0; i < binding.StoneLights.Length && i < binding.StoneLightsWereEnabled.Length; i++)
                        if (binding.StoneLights[i] != null) binding.StoneLights[i].enabled = binding.StoneLightsWereEnabled[i];
                if (binding.StoneVisualEffects != null && binding.StoneVisualEffectsWereEnabled != null)
                    for (int i = 0; i < binding.StoneVisualEffects.Length && i < binding.StoneVisualEffectsWereEnabled.Length; i++)
                        if (binding.StoneVisualEffects[i] != null) binding.StoneVisualEffects[i].enabled = binding.StoneVisualEffectsWereEnabled[i];
                if (binding.StoneParticles != null && binding.StoneParticlesWerePlaying != null)
                    for (int i = 0; i < binding.StoneParticles.Length && i < binding.StoneParticlesWerePlaying.Length; i++)
                        if (binding.StoneParticles[i] != null && binding.StoneParticlesWerePlaying[i]) binding.StoneParticles[i].Play(true);
            }
            catch { }
        }


        private static bool IsStoneGolem(Character worker)
        {
            if (worker == null) return false;
            string species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
            return string.Equals(species, "StoneGolem", StringComparison.OrdinalIgnoreCase);
        }

        private static void ForceStoneGolemSleepAuthority(Character worker, ZDO zdo, bool sleeping)
        {
            if (worker == null) return;
            try
            {
                MonsterAI ai = worker.GetComponent<MonsterAI>();
                if (ai != null)
                {
                    // m_sleeping is public in current Valheim, but reflection keeps this source tolerant
                    // if Iron Gate changes the member visibility again.
                    FieldInfo sleepingField = ai.GetType().GetField("m_sleeping", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (sleepingField != null && sleepingField.FieldType == typeof(bool))
                        sleepingField.SetValue(ai, sleeping);

                    if (sleeping)
                    {
                        // MonsterAI.SetAlerted(bool) is non-public in Valheim 1.0.16, so calling it
                        // directly does not compile against the current game assemblies. Invoke it
                        // reflectively when present; sleeping still works if Iron Gate removes it.
                        try
                        {
                            MethodInfo setAlerted = ai.GetType().GetMethod(
                                "SetAlerted",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                null,
                                new Type[] { typeof(bool) },
                                null);
                            if (setAlerted != null)
                                setAlerted.Invoke(ai, new object[] { false });
                        }
                        catch { }
                    }
                }

                ZDO targetZdo = zdo;
                if (targetZdo == null)
                {
                    ZNetView nview = worker.GetComponent<ZNetView>();
                    if (nview != null && nview.IsValid()) targetZdo = nview.GetZDO();
                }
                if (targetZdo != null)
                    targetZdo.Set("sleeping", sleeping);
            }
            catch { }
        }

        private static bool IsPresentationRestState(WorkerState state)
        {
            // WaitAtHome is the normal no-work state. Treat it as a visible home/rest pose too;
            // otherwise Workers merely freeze beside their assigned furniture for most of the day.
            return state == WorkerState.Resting ||
                   state == WorkerState.Recover ||
                   state == WorkerState.WaitAtHome;
        }

        private static RestBinding Discover(Character worker)
        {
            Animator animator = worker.GetComponentInChildren<Animator>(true);
            if (animator == null) return null;
            RestBinding result = new RestBinding { Animator = animator, ZSync = FindZSync(worker) };

            // v0.5.163: Troll rigs do not provide a reliable chair/seat contract. The Troll-Lehnplatz
            // is deliberately a standing rest station made from tree trunks, so preserving the native
            // idle is both more stable and more believable than forcing an arbitrary sit/rest trigger.
            string normalizedSpecies = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
            if (string.Equals(normalizedSpecies, "Troll", StringComparison.OrdinalIgnoreCase))
            {
                if (ReportedMissing.Add("Troll:NativeLeanIdle"))
                    _debug?.Invoke("Rest animation: Troll uses stable native standing idle inside Troll-Lehnplatz.");
                return result;
            }

            string best = null;
            AnimatorControllerParameterType bestType = AnimatorControllerParameterType.Float;
            int bestScore = int.MinValue;
            try
            {
                AnimatorControllerParameter[] ps = animator.parameters;

                // v0.5.177: the visually correct Stone Golem disguise is the start pose of its
                // wake-up animation, not the generic runtime sleep transition we used in 0.5.175/176.
                // Discover the actual wake-up clip from the creature's own controller and sample its
                // first frame while resting. Keep the sleeping bool only as a fallback/AI authority.
                if (string.Equals(normalizedSpecies, "StoneGolem", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        AnimationClip[] clips = animator.runtimeAnimatorController != null
                            ? animator.runtimeAnimatorController.animationClips
                            : null;
                        AnimationClip bestWake = null;
                        int bestWakeScore = int.MinValue;
                        if (clips != null)
                        {
                            for (int i = 0; i < clips.Length; i++)
                            {
                                AnimationClip clip = clips[i];
                                if (clip == null) continue;
                                string n = (clip.name ?? string.Empty).ToLowerInvariant();
                                int score = 0;
                                if (n.Contains("wakeup") || n.Contains("wake_up")) score = 1000;
                                else if (n.Contains("wake")) score = 900;
                                else if (n.Contains("awaken")) score = 850;
                                else if (n.Contains("spawn")) score = 500;
                                if (score > bestWakeScore)
                                {
                                    bestWakeScore = score;
                                    bestWake = clip;
                                }
                            }
                        }
                        if (bestWake != null && bestWakeScore > 0)
                        {
                            result.StoneDormantClip = bestWake;
                            _debug?.Invoke($"Rest animation: StoneGolem dormant disguise samples frame 0 of native clip '{bestWake.name}'.");
                        }
                        else if (ReportedMissing.Add("StoneGolem:WakeClipMissing"))
                        {
                            string names = clips == null ? "<none>" : string.Join(", ", Array.ConvertAll(clips, c => c != null ? c.name : "<null>"));
                            _debug?.Invoke($"Rest animation: StoneGolem wake-up clip not found. Controller clips: {names}");
                        }
                    }
                    catch { }

                    for (int i = 0; i < ps.Length; i++)
                    {
                        string exact = ps[i].name ?? string.Empty;
                        if (ps[i].type == AnimatorControllerParameterType.Bool &&
                            (string.Equals(exact, "sleeping", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(exact, "sleep", StringComparison.OrdinalIgnoreCase)))
                        {
                            result.Parameter = exact;
                            result.ParameterType = AnimatorControllerParameterType.Bool;
                            if (result.StoneDormantClip == null)
                                _debug?.Invoke($"Rest animation: StoneGolem falls back to {exact} (Bool); no wake-up clip was discoverable.");
                            return result;
                        }
                    }

                    if (result.StoneDormantClip != null)
                        return result;
                }
                for (int i = 0; i < ps.Length; i++)
                {
                    string name = ps[i].name ?? string.Empty;
                    string lower = name.ToLowerInvariant();
                    int score = Score(lower);
                    if (score <= bestScore) continue;
                    if (ps[i].type != AnimatorControllerParameterType.Bool && ps[i].type != AnimatorControllerParameterType.Trigger) continue;
                    bestScore = score;
                    best = name;
                    bestType = ps[i].type;
                }
            }
            catch { }

            if (bestScore > 0)
            {
                result.Parameter = best;
                result.ParameterType = bestType;
                _debug?.Invoke($"Rest animation discovered: {SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name)} -> {best} ({bestType}).");
                return result;
            }

            // Some creature controllers expose rest only as a state, not a parameter.
            string[] states = { "sleep", "Sleep", "rest", "Rest", "sit", "Sit", "sitting", "Sitting" };
            for (int i = 0; i < states.Length; i++)
            {
                try
                {
                    if (animator.HasState(0, Animator.StringToHash(states[i])))
                    {
                        result.State = states[i];
                        _debug?.Invoke($"Rest animation state discovered: {SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name)} -> {states[i]}.");
                        return result;
                    }
                }
                catch { }
            }

            string species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
            if (ReportedMissing.Add(species))
                _debug?.Invoke($"Rest animation: {species} exposes no native sleep/rest/sit parameter or state; keeping stable idle pose for this species.");
            return result;
        }

        private static int Score(string lower)
        {
            if (string.IsNullOrEmpty(lower)) return int.MinValue;
            int score = 0;
            if (lower == "sleep" || lower == "sleeping") score += 1000;
            else if (lower.Contains("sleep")) score += 900;
            if (lower == "rest" || lower == "resting") score += 800;
            else if (lower.Contains("rest")) score += 700;
            if (lower == "sit" || lower == "sitting") score += 650;
            else if (lower.Contains("sit")) score += 550;
            if (lower.Contains("attach_chair") || lower.Contains("attach_throne")) score += 400;
            return score;
        }

        private static Component FindZSync(Character worker)
        {
            try
            {
                Component[] all = worker.GetComponentsInChildren<Component>(true);
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null && string.Equals(all[i].GetType().Name, "ZSyncAnimation", StringComparison.Ordinal)) return all[i];
            }
            catch { }
            return null;
        }

        private static void TrySetBool(RestBinding binding, bool value)
        {
            try
            {
                MethodInfo method = binding.ZSync?.GetType().GetMethod("SetBool", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(bool) }, null);
                if (method != null) { method.Invoke(binding.ZSync, new object[] { binding.Parameter, value }); return; }
                binding.Animator.SetBool(binding.Parameter, value);
            }
            catch { }
        }

        private static void TrySetTrigger(RestBinding binding)
        {
            try
            {
                MethodInfo method = binding.ZSync?.GetType().GetMethod("SetTrigger", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                if (method != null) { method.Invoke(binding.ZSync, new object[] { binding.Parameter }); return; }
                binding.Animator.SetTrigger(binding.Parameter);
            }
            catch { }
        }
    }
}
