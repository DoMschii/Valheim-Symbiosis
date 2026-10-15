using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Werewolf / canid colour perception.
    ///
    /// v0.7.0 supports both rendering families that can exist in modern Valheim builds:
    /// - SRP Volume + ColorAdjustments/ChannelMixer (URP/HDRP style)
    /// - legacy PostProcessing v2 PostProcessVolume + ColorGrading
    ///
    /// Valheim Ego taught us not to trust a detached camera guess: the binding is refreshed from
    /// the real Camera.main every frame. A very light IMGUI perception veil is also kept as a
    /// renderer-independent fallback so the transformation is visibly different even when the
    /// running render pipeline silently ignores a dynamically-created volume.
    /// </summary>
    internal static class WerewolfVisionRuntime
    {
        private const float VolumePriority = 50000f;
        private const float FadeSpeed = 4.5f;

        // v0.7.3 is a canid approximation, not a grayscale filter: preserve blue/yellow
        // separation, collapse red/green, and add mild dark adaptation.
        private const float Saturation = -38f;
        private const float DayContrast = 18f;
        private const float NightContrast = 24f;
        private const float DayPostExposure = 0.22f;
        private const float NightPostExposure = 0.78f;

        private static Action<string> _log;
        private static Camera _boundCamera;
        private static Component _volume;
        private static ScriptableObject _profile;
        private static UnityEngine.Object _colorAdjustments;
        private static UnityEngine.Object _channelMixer;
        private static MemberInfo _weightMember;
        private static string _bindingKind = "none";

        private static float _targetWeight;
        private static float _currentWeight;
        private static bool _reportedActive;
        private static bool _reportedBinding;
        private static bool _reportedUnavailable;
        private static bool _subscribed;
        private static float _darkAdaptation;

        internal static void Initialize(Action<string> log)
        {
            _log = log;
            EnsureSubscribed();
        }

        internal static void Tick(float targetWeight)
        {
            EnsureSubscribed();
            _targetWeight = Mathf.Clamp01(targetWeight);
            _currentWeight = Mathf.MoveTowards(_currentWeight, _targetWeight, Time.unscaledDeltaTime * FadeSpeed);
            // v3.8.0: once the effect is fully idle there is no reason to execute reflection-backed
            // volume tuning every frame. Activation still resumes immediately on the first non-zero tick.
            if (_targetWeight <= 0f && _currentWeight <= 0.001f)
            {
                _currentWeight = 0f;
                return;
            }
            float darkTarget = ResolveDarkAdaptationTarget();
            _darkAdaptation = Mathf.MoveTowards(_darkAdaptation, darkTarget, Time.unscaledDeltaTime * 1.35f);

            if (_targetWeight > 0f || _currentWeight > 0.001f)
            {
                Camera camera = Camera.main;
                if (camera != null)
                    EnsureCameraBinding(camera);
            }

            ApplyDynamicVisionTuning();
            ApplyWeight();
            ReportState();
        }

        /// <summary>
        /// Guaranteed visible fallback. The real colour grading remains the primary path; this
        /// merely blends the final screen toward a cool neutral, which visibly suppresses colour
        /// separation even on render pipelines that ignore runtime Volumes.
        /// Called before our skill HUD so the skill slots remain crisp.
        /// </summary>
        internal static void DrawFallbackOverlay()
        {
            if (_currentWeight <= 0.005f || Event.current == null || Event.current.type != EventType.Repaint)
                return;

            float w = Mathf.Clamp01(_currentWeight);
            Color old = GUI.color;
            int oldDepth = GUI.depth;
            try
            {
                GUI.depth = 900;

                // Keep the renderer-independent fallback deliberately subtle. The real canine
                // red/green collapse happens in the channel mixer when the render pipeline accepts
                // it; this veil should no longer make the whole scene merely pale.
                GUI.color = new Color(0.55f, 0.57f, 0.58f, 0.075f * w);
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture, ScaleMode.StretchToFill, false);

                // Cool-blue bias + slight warm luminance keep the characteristic blue/yellow axis.
                GUI.color = new Color(0.30f, 0.43f, 0.72f, 0.045f * w);
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture, ScaleMode.StretchToFill, false);
                GUI.color = new Color(0.78f, 0.67f, 0.36f, 0.018f * w);
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture, ScaleMode.StretchToFill, false);

                // Canids have strong low-light adaptation. This is not thermal/night-vision: it is
                // a small luminance lift that ramps in at night so dark detail remains readable.
                float nightLift = 0.065f * w * _darkAdaptation;
                if (nightLift > 0.001f)
                {
                    GUI.color = new Color(0.72f, 0.76f, 0.80f, nightLift);
                    GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture, ScaleMode.StretchToFill, false);
                }
            }
            catch { }
            finally
            {
                GUI.color = old;
                GUI.depth = oldDepth;
            }
        }

        internal static void Shutdown()
        {
            if (_subscribed)
            {
                try { Camera.onPreCull -= OnCameraPreCull; } catch { }
                _subscribed = false;
            }
            DestroyRuntimeVolume();
            _boundCamera = null;
            _targetWeight = 0f;
            _currentWeight = 0f;
            _reportedActive = false;
            _reportedBinding = false;
            _reportedUnavailable = false;
            _darkAdaptation = 0f;
            _bindingKind = "none";
        }


        private static void EnsureSubscribed()
        {
            if (_subscribed) return;
            Camera.onPreCull += OnCameraPreCull;
            _subscribed = true;
        }

        private static void OnCameraPreCull(Camera camera)
        {
            if (camera == null || camera != Camera.main) return;
            if (_targetWeight <= 0f && _currentWeight <= 0.001f) return;
            try
            {
                EnsureCameraBinding(camera);
                ApplyWeight();
            }
            catch (Exception ex)
            {
                if (!_reportedUnavailable)
                {
                    _reportedUnavailable = true;
                    _log?.Invoke("Werewolf vision final pre-cull binding failed; perception veil remains active: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private static void EnsureCameraBinding(Camera camera)
        {
            if (camera == null) return;

            if (_boundCamera != null && _boundCamera != camera)
            {
                DestroyRuntimeVolume();
                _reportedBinding = false;
            }
            _boundCamera = camera;

            if (_volume != null && _weightMember != null)
                return;

            // Unity 6 Valheim builds can expose SRP Volumes even when old PPv2 assemblies are still
            // loaded. Prefer the active modern Volume system, then fall back to PPv2.
            if (!TryCreateSrpVolume(camera) && !TryCreatePpv2Volume(camera))
            {
                if (!_reportedUnavailable)
                {
                    _reportedUnavailable = true;
                    _log?.Invoke("Werewolf vision: no usable SRP/PPv2 volume path found; renderer-independent perception veil remains active.");
                }
                return;
            }

            _reportedUnavailable = false;
            if (!_reportedBinding)
            {
                _reportedBinding = true;
                _log?.Invoke("Werewolf vision bound to Camera.main: camera=" + camera.name + ", pipeline=" + _bindingKind + ", runtimeVolume=" + (_volume != null ? _volume.GetType().FullName : "none") + ".");
            }
        }

        private static bool TryCreateSrpVolume(Camera camera)
        {
            Type volumeType = FindType("UnityEngine.Rendering.Volume");
            Type profileType = FindType("UnityEngine.Rendering.VolumeProfile");
            if (volumeType == null || profileType == null) return false;

            Type adjustmentsType = FindType("UnityEngine.Rendering.Universal.ColorAdjustments")
                ?? FindType("UnityEngine.Rendering.HighDefinition.ColorAdjustments");
            Type mixerType = FindType("UnityEngine.Rendering.Universal.ChannelMixer")
                ?? FindType("UnityEngine.Rendering.HighDefinition.ChannelMixer");
            if (adjustmentsType == null) return false;

            GameObject go = null;
            try
            {
                int layer = ResolveSrpVolumeLayer(camera);
                go = new GameObject("VW_Werewolf_DichromaticVision_SRP");
                go.hideFlags = HideFlags.HideAndDontSave;
                go.layer = layer;

                Component volume = go.AddComponent(volumeType);
                if (volume == null) throw new InvalidOperationException("Volume component creation returned null");

                ScriptableObject profile = ScriptableObject.CreateInstance(profileType);
                if (profile == null) throw new InvalidOperationException("VolumeProfile creation returned null");
                profile.hideFlags = HideFlags.HideAndDontSave;

                object adjustments = AddSrpOverride(profile, adjustmentsType);
                if (!(adjustments is UnityEngine.Object adjustmentsObject))
                    throw new InvalidOperationException("ColorAdjustments could not be added to VolumeProfile");

                SetNamedMember(adjustmentsObject, "active", true);
                SetParameter(adjustmentsObject, "postExposure", DayPostExposure);
                SetParameter(adjustmentsObject, "contrast", DayContrast);
                SetParameter(adjustmentsObject, "saturation", Saturation);
                // Slight cool/blue weighting. ChannelMixer below owns the real dichromatic mapping.
                SetParameter(adjustmentsObject, "colorFilter", new Color(0.98f, 1.00f, 1.04f, 1f));

                UnityEngine.Object mixerObject = null;
                if (mixerType != null)
                {
                    object mixer = AddSrpOverride(profile, mixerType);
                    mixerObject = mixer as UnityEngine.Object;
                    if (mixerObject != null)
                    {
                        SetNamedMember(mixerObject, "active", true);
                        ConfigureChannelMixer(mixerObject);
                    }
                }

                SetNamedMember(volume, "isGlobal", true);
                SetNamedMember(volume, "priority", VolumePriority);
                SetNamedMember(volume, "blendDistance", 0f);
                if (!SetNamedMember(volume, "sharedProfile", profile))
                    SetNamedMember(volume, "profile", profile);

                MemberInfo weight = FindMember(volume.GetType(), "weight");
                if (weight == null) throw new InvalidOperationException("Volume.weight was not found");
                SetMemberValue(volume, weight, _currentWeight);

                _volume = volume;
                _profile = profile;
                _colorAdjustments = adjustmentsObject;
                _channelMixer = mixerObject;
                _weightMember = weight;
                _bindingKind = adjustmentsType.FullName != null && adjustmentsType.FullName.Contains("HighDefinition") ? "SRP-HDRP" : "SRP-URP";
                return true;
            }
            catch (Exception ex)
            {
                if (go != null) try { UnityEngine.Object.Destroy(go); } catch { }
                _log?.Invoke("Werewolf vision SRP binding fallback: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static object AddSrpOverride(ScriptableObject profile, Type componentType)
        {
            if (profile == null || componentType == null) return null;
            Type profileType = profile.GetType();
            foreach (MethodInfo method in profileType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!string.Equals(method.Name, "Add", StringComparison.Ordinal)) continue;
                ParameterInfo[] p = method.GetParameters();
                if (p.Length == 2 && p[0].ParameterType == typeof(Type) && p[1].ParameterType == typeof(bool))
                    return method.Invoke(profile, new object[] { componentType, true });
                if (p.Length == 1 && p[0].ParameterType == typeof(Type))
                    return method.Invoke(profile, new object[] { componentType });
            }
            return null;
        }

        private static int ResolveSrpVolumeLayer(Camera camera)
        {
            if (camera == null) return 0;
            string[] additionalTypes =
            {
                "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData",
                "UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData"
            };

            for (int i = 0; i < additionalTypes.Length; ++i)
            {
                Type type = FindType(additionalTypes[i]);
                if (type == null) continue;
                try
                {
                    Component data = camera.GetComponent(type);
                    if (data == null) continue;
                    int mask = ReadLayerMask(data, "volumeLayerMask");
                    int selected = FirstLayerFromMask(mask);
                    if (selected >= 0) return selected;
                }
                catch { }
            }
            return Mathf.Clamp(camera.gameObject.layer, 0, 31);
        }

        private static bool TryCreatePpv2Volume(Camera camera)
        {
            Type layerType = FindType("UnityEngine.Rendering.PostProcessing.PostProcessLayer");
            Type managerType = FindType("UnityEngine.Rendering.PostProcessing.PostProcessManager");
            Type gradingType = FindType("UnityEngine.Rendering.PostProcessing.ColorGrading");
            Type settingsBaseType = FindType("UnityEngine.Rendering.PostProcessing.PostProcessEffectSettings");
            if (layerType == null || managerType == null || gradingType == null || settingsBaseType == null)
                return false;

            Component postLayer = null;
            try { postLayer = camera.GetComponent(layerType); } catch { }
            if (postLayer == null) return false;

            int mask = ReadLayerMask(postLayer, "volumeLayer");
            int selectedLayer = FirstLayerFromMask(mask);
            if (selectedLayer < 0) selectedLayer = Mathf.Clamp(camera.gameObject.layer, 0, 31);

            UnityEngine.Object grading = ScriptableObject.CreateInstance(gradingType);
            if (grading == null) return false;
            grading.hideFlags = HideFlags.HideAndDontSave;
            SetNamedMember(grading, "active", true);
            SetParameter(grading, "postExposure", DayPostExposure);
            SetParameter(grading, "saturation", Saturation);
            SetParameter(grading, "contrast", DayContrast);
            ConfigureChannelMixer(grading);

            Component volume = CreateQuickVolume(managerType, settingsBaseType, selectedLayer, grading)
                ?? CreateManualPpv2Volume(selectedLayer, grading);
            if (volume == null)
            {
                SafeDestroy(grading);
                return false;
            }

            MemberInfo weight = FindMember(volume.GetType(), "weight");
            if (weight == null)
            {
                try { UnityEngine.Object.Destroy(volume.gameObject); } catch { }
                SafeDestroy(grading);
                return false;
            }

            SetNamedMember(volume, "isGlobal", true);
            SetNamedMember(volume, "priority", VolumePriority);
            SetMemberValue(volume, weight, _currentWeight);

            _volume = volume;
            _colorAdjustments = grading;
            _weightMember = weight;
            _bindingKind = "PPv2";
            return true;
        }

        private static void ConfigureChannelMixer(UnityEngine.Object setting)
        {
            if (setting == null) return;

            // Canid-style dichromacy approximation: red and green are projected onto almost the
            // same luminance axis, while blue remains independently readable. This deliberately
            // avoids the "everything pale" look of the old heavy desaturation filter.
            SetAnyParameter(setting, new[] { "redOutRedIn", "mixerRedOutRedIn", "colorMixerRedOutRedIn" }, 50f);
            SetAnyParameter(setting, new[] { "redOutGreenIn", "mixerRedOutGreenIn", "colorMixerRedOutGreenIn" }, 50f);
            SetAnyParameter(setting, new[] { "redOutBlueIn", "mixerRedOutBlueIn", "colorMixerRedOutBlueIn" }, 0f);

            SetAnyParameter(setting, new[] { "greenOutRedIn", "mixerGreenOutRedIn", "colorMixerGreenOutRedIn" }, 48f);
            SetAnyParameter(setting, new[] { "greenOutGreenIn", "mixerGreenOutGreenIn", "colorMixerGreenOutGreenIn" }, 52f);
            SetAnyParameter(setting, new[] { "greenOutBlueIn", "mixerGreenOutBlueIn", "colorMixerGreenOutBlueIn" }, 0f);

            SetAnyParameter(setting, new[] { "blueOutRedIn", "mixerBlueOutRedIn", "colorMixerBlueOutRedIn" }, 0f);
            SetAnyParameter(setting, new[] { "blueOutGreenIn", "mixerBlueOutGreenIn", "colorMixerBlueOutGreenIn" }, 10f);
            SetAnyParameter(setting, new[] { "blueOutBlueIn", "mixerBlueOutBlueIn", "colorMixerBlueOutBlueIn" }, 90f);
        }

        private static Component CreateQuickVolume(Type managerType, Type settingsBaseType, int layer, UnityEngine.Object grading)
        {
            try
            {
                object manager = GetStaticMember(managerType, "instance");
                if (manager == null) return null;

                foreach (MethodInfo method in managerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!string.Equals(method.Name, "QuickVolume", StringComparison.Ordinal)) continue;
                    ParameterInfo[] p = method.GetParameters();
                    if (p.Length != 3 || p[0].ParameterType != typeof(int) || p[1].ParameterType != typeof(float) || !p[2].ParameterType.IsArray) continue;
                    Type elementType = p[2].ParameterType.GetElementType();
                    if (elementType == null || !elementType.IsAssignableFrom(grading.GetType())) continue;

                    Array settings = Array.CreateInstance(elementType, 1);
                    settings.SetValue(grading, 0);
                    object result = method.Invoke(manager, new object[] { layer, VolumePriority, settings });
                    if (result is Component component)
                    {
                        try
                        {
                            component.gameObject.name = "VW_Werewolf_DichromaticVision_PPv2";
                            component.gameObject.hideFlags = HideFlags.HideAndDontSave;
                        }
                        catch { }
                        return component;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke("Werewolf vision PPv2 QuickVolume fallback: " + ex.GetType().Name + ": " + ex.Message);
            }
            return null;
        }

        private static Component CreateManualPpv2Volume(int layer, UnityEngine.Object grading)
        {
            Type volumeType = FindType("UnityEngine.Rendering.PostProcessing.PostProcessVolume");
            Type profileType = FindType("UnityEngine.Rendering.PostProcessing.PostProcessProfile");
            if (volumeType == null || profileType == null) return null;

            try
            {
                GameObject go = new GameObject("VW_Werewolf_DichromaticVision_PPv2Manual");
                go.hideFlags = HideFlags.HideAndDontSave;
                go.layer = layer;
                Component volume = go.AddComponent(volumeType);
                if (volume == null) { UnityEngine.Object.Destroy(go); return null; }

                ScriptableObject profile = ScriptableObject.CreateInstance(profileType);
                if (profile == null) { UnityEngine.Object.Destroy(go); return null; }
                profile.hideFlags = HideFlags.HideAndDontSave;

                object settingsObject = GetNamedMember(profile, "settings");
                if (!(settingsObject is IList list))
                {
                    SafeDestroy(profile);
                    UnityEngine.Object.Destroy(go);
                    return null;
                }
                list.Add(grading);

                SetNamedMember(volume, "isGlobal", true);
                SetNamedMember(volume, "blendDistance", 0f);
                SetNamedMember(volume, "priority", VolumePriority);
                if (!SetNamedMember(volume, "sharedProfile", profile)) SetNamedMember(volume, "profile", profile);
                _profile = profile;
                return volume;
            }
            catch { return null; }
        }

        private static void ApplyDynamicVisionTuning()
        {
            if (_colorAdjustments == null) return;
            float exposure = Mathf.Lerp(DayPostExposure, NightPostExposure, _darkAdaptation);
            float contrast = Mathf.Lerp(DayContrast, NightContrast, _darkAdaptation);
            try
            {
                SetParameter(_colorAdjustments, "postExposure", exposure);
                SetParameter(_colorAdjustments, "contrast", contrast);
                SetParameter(_colorAdjustments, "saturation", Saturation);
            }
            catch { }
        }

        private static float ResolveDarkAdaptationTarget()
        {
            // Prefer Valheim's own day/night state without a hard compile-time dependency on its
            // current EnvMan API. Night gets a strong boost; dark ambient lighting can add a smaller
            // boost in caves/overcast environments.
            float target = 0f;
            try
            {
                Type envType = FindType("EnvMan");
                if (envType != null)
                {
                    object instance = GetStaticMember(envType, "instance");
                    MethodInfo isNight = envType.GetMethod("IsNight", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    if (instance != null && isNight != null && isNight.Invoke(instance, null) is bool night && night)
                        target = 1f;
                }
            }
            catch { }

            try
            {
                float ambient = RenderSettings.ambientLight.grayscale * Mathf.Max(0.01f, RenderSettings.ambientIntensity);
                float ambientDark = Mathf.InverseLerp(0.42f, 0.08f, ambient);
                target = Mathf.Max(target, ambientDark * 0.70f);
            }
            catch { }
            return Mathf.Clamp01(target);
        }

        private static void ApplyWeight()
        {
            if (_volume == null || _weightMember == null) return;
            try { SetMemberValue(_volume, _weightMember, _currentWeight); }
            catch (Exception ex)
            {
                if (!_reportedUnavailable)
                {
                    _reportedUnavailable = true;
                    _log?.Invoke("Werewolf vision weight update failed; perception veil remains active: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private static void ReportState()
        {
            if (_currentWeight > 0.08f && !_reportedActive)
            {
                _reportedActive = true;
                _log?.Invoke("Werwolf-Sicht aktiv: canid/dichromatisch; Rot/Gruen kollabiert, Blau/Gelb betont, dynamische Dunkeladaption aktiv; fallback veil=" + (_volume == null ? "primary" : "support") + ".");
            }
            else if (_currentWeight <= 0.001f)
            {
                _reportedActive = false;
            }
        }

        private static void DestroyRuntimeVolume()
        {
            try
            {
                if (_volume != null && _weightMember != null)
                    SetMemberValue(_volume, _weightMember, 0f);
            }
            catch { }

            try
            {
                if (_volume != null && _volume.gameObject != null)
                    UnityEngine.Object.Destroy(_volume.gameObject);
            }
            catch { }

            SafeDestroy(_profile);
            SafeDestroy(_colorAdjustments);
            SafeDestroy(_channelMixer);
            _volume = null;
            _profile = null;
            _colorAdjustments = null;
            _channelMixer = null;
            _weightMember = null;
            _bindingKind = "none";
        }

        private static void SafeDestroy(UnityEngine.Object obj)
        {
            try { if (obj != null) UnityEngine.Object.Destroy(obj); } catch { }
        }

        private static int ReadLayerMask(object target, string memberName)
        {
            try
            {
                object mask = GetNamedMember(target, memberName);
                if (mask == null) return 0;
                if (mask is int direct) return direct;
                if (mask is LayerMask lm) return lm.value;
                MemberInfo valueMember = FindMember(mask.GetType(), "value");
                object raw = GetMemberValue(mask, valueMember);
                return raw is int bits ? bits : 0;
            }
            catch { return 0; }
        }

        private static int FirstLayerFromMask(int bits)
        {
            if (bits == 0) return -1;
            for (int i = 0; i < 32; ++i)
                if ((bits & (1 << i)) != 0) return i;
            return -1;
        }

        private static void SetParameter(UnityEngine.Object setting, string parameterName, object value)
        {
            if (setting == null) return;
            MemberInfo member = FindMember(setting.GetType(), parameterName);
            if (member == null) return;
            object parameter = GetMemberValue(setting, member);
            if (parameter == null) return;
            MemberInfo valueMember = FindMember(parameter.GetType(), "value");
            if (valueMember == null) return;
            SetMemberValue(parameter, valueMember, value);
            MemberInfo overrideMember = FindMember(parameter.GetType(), "overrideState");
            if (overrideMember != null) SetMemberValue(parameter, overrideMember, true);
        }

        private static void SetAnyParameter(UnityEngine.Object setting, string[] names, object value)
        {
            if (setting == null || names == null) return;
            for (int i = 0; i < names.Length; ++i)
            {
                MemberInfo member = FindMember(setting.GetType(), names[i]);
                if (member == null) continue;
                object parameter = GetMemberValue(setting, member);
                if (parameter == null) continue;
                MemberInfo valueMember = FindMember(parameter.GetType(), "value");
                if (valueMember == null) continue;
                SetMemberValue(parameter, valueMember, value);
                MemberInfo overrideMember = FindMember(parameter.GetType(), "overrideState");
                if (overrideMember != null) SetMemberValue(parameter, overrideMember, true);
                return;
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(fullName, false);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        private static object GetStaticMember(Type type, string name)
        {
            if (type == null) return null;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null) return property.GetValue(null, null);
            FieldInfo field = type.GetField(name, flags);
            return field != null ? field.GetValue(null) : null;
        }

        private static MemberInfo FindMember(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null) return property;
            return type.GetField(name, flags);
        }

        private static object GetNamedMember(object target, string name)
        {
            if (target == null) return null;
            return GetMemberValue(target, FindMember(target.GetType(), name));
        }

        private static object GetMemberValue(object target, MemberInfo member)
        {
            if (target == null || member == null) return null;
            if (member is PropertyInfo property) return property.GetValue(target, null);
            if (member is FieldInfo field) return field.GetValue(target);
            return null;
        }

        private static bool SetNamedMember(object target, string name, object value)
        {
            if (target == null) return false;
            MemberInfo member = FindMember(target.GetType(), name);
            if (member == null) return false;
            if (member is PropertyInfo property && !property.CanWrite) return false;
            SetMemberValue(target, member, value);
            return true;
        }

        private static void SetMemberValue(object target, MemberInfo member, object value)
        {
            if (target == null || member == null) return;
            Type targetType = member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;
            object converted = value;
            if (value != null && !targetType.IsInstanceOfType(value))
            {
                try
                {
                    if (targetType.IsEnum && value is IConvertible)
                        converted = Enum.ToObject(targetType, value);
                    else
                        converted = Convert.ChangeType(value, targetType);
                }
                catch { converted = value; }
            }

            if (member is PropertyInfo p && p.CanWrite) p.SetValue(target, converted, null);
            else if (member is FieldInfo f) f.SetValue(target, converted);
        }
    }
}
