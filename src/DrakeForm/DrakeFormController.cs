using System;
using UnityEngine;

namespace Valheim_Workers_DrakeForm
{
    /// <summary>
    /// Local necklace controller. Name retained for binary/source continuity; v0.3 routes any known
    /// creature-specific necklace into the shared Mount symbiosis runtime.
    /// </summary>
    internal sealed class DrakeFormController : MonoBehaviour
    {
        internal const string PlayerMorphKey = "vw_player_drake_form";

        private Player _player;
        private float _nextMissingNecklaceCheck;
        private float _nextBindingPersistenceCheck;
        private bool _bindingPersistenceInitialPassDone;
        private ItemDrop.ItemData _pendingSwitchItem;
        private string _pendingSwitchForm;
        private float _pendingSwitchDeadline;
        private bool _pendingSwitchEgo;

        internal bool IsActive => MountSymbiosisBridge.Active;
        internal Player Player => _player;

        private void Awake()
        {
            _player = GetComponent<Player>();
        }

        private void OnDisable()
        {
            ClearPendingSwitch();
        }

        private void OnDestroy()
        {
            ClearPendingSwitch();
        }

        private void ClearPendingSwitch()
        {
            _pendingSwitchItem = null;
            _pendingSwitchForm = null;
            _pendingSwitchEgo = false;
            _pendingSwitchDeadline = 0f;
        }

        internal void TickLocalInput()
        {
            if (_player == null || _player != Player.m_localPlayer) return;

            if (Time.unscaledTime >= _nextBindingPersistenceCheck)
            {
                // v4.2.0: binding normalization is a recovery/safety sweep, not gameplay polling.
                // Run quickly after load/respawn, then only occasionally. Capture/use paths already
                // normalize metadata immediately when a necklace actually changes.
                _nextBindingPersistenceCheck = Time.unscaledTime + (_bindingPersistenceInitialPassDone ? 15f : 2f);
                int repaired = DrakeCaptureRuntime.NormalizeSavedBindings(_player);
                _bindingPersistenceInitialPassDone = true;
                if (repaired > 0)
                    Plugin.Log?.LogInfo("[Symbiosis] Repaired/persisted " + repaired + " bound necklace profile(s) after load/respawn.");
            }

            if (_player.IsDead())
            {
                ClearPendingSwitch();
                return;
            }

            // v0.6.5: using a different bound necklace while transformed queues a direct
            // form-to-form handoff. Mount owns the current exit animation; as soon as that
            // runtime is fully inactive we enter the queued form. This avoids overlapping
            // temporary creature bodies/cameras and preserves the proven transition safety.
            if (_pendingSwitchItem != null)
            {
                if (Time.unscaledTime > _pendingSwitchDeadline)
                {
                    Plugin.Log?.LogWarning("[Symbiosis] Queued form switch timed out safely.");
                    _pendingSwitchItem = null;
                    _pendingSwitchForm = null;
                    _pendingSwitchEgo = false;
                    Notify("Symbiose-Wechsel abgebrochen.");
                }
                else if (!MountSymbiosisBridge.Active)
                {
                    ItemDrop.ItemData queued = _pendingSwitchItem;
                    string queuedForm = _pendingSwitchForm;
                    bool queuedEgo = _pendingSwitchEgo;
                    _pendingSwitchItem = null;
                    _pendingSwitchForm = null;
                    _pendingSwitchEgo = false;

                    if (queued == null || !DrakeCaptureRuntime.IsBoundSymbiosisNecklace(queued))
                    {
                        Notify("Symbiose-Wechsel abgebrochen: Halskette nicht mehr verfuegbar.");
                    }
                    else if (string.Equals(queuedForm, SymbiosisFormCatalog.Werewolf, StringComparison.OrdinalIgnoreCase) && !IsWerewolfDarkEnough(_player))
                    {
                        Notify("Werwolf-Wechsel abgebrochen: Es ist nicht mehr dunkel genug.");
                    }
                    else if (!MountSymbiosisBridge.TryEnter(_player, queued, out string switchMessage))
                    {
                        Notify(string.IsNullOrEmpty(switchMessage) ? "Symbiose-Wechsel fehlgeschlagen." : switchMessage);
                    }
                    else
                    {
                        MountSymbiosisBridge.TrySetEgoMode(queuedEgo);
                        Notify(string.IsNullOrEmpty(switchMessage) ? ("Symbiose gewechselt: " + GetFormDisplayName(queuedForm)) : switchMessage);
                        Plugin.Log?.LogInfo("[Symbiosis] Direct form switch completed -> " + queuedForm + ", camera=" + (queuedEgo ? "Ego" : "Third") + ".");
                    }
                }
            }

            if (MountSymbiosisBridge.Active && _pendingSwitchItem == null && Time.unscaledTime >= _nextMissingNecklaceCheck)
            {
                _nextMissingNecklaceCheck = Time.unscaledTime + 0.5f;
                string activeForm = MountSymbiosisBridge.ActiveForm;
                if (DrakeCaptureRuntime.FindBoundMedallionInInventory(_player, requiredForm: activeForm) == null)
                {
                    if (MountSymbiosisBridge.TryExit(_player, out string message) && !string.IsNullOrEmpty(message))
                        Notify(message);
                }
            }
        }

        internal void ToggleFromHotbar(ItemDrop.ItemData bound)
        {
            if (_player == null || _player != Player.m_localPlayer || bound == null) return;
            if (!DrakeCaptureRuntime.TryGetBoundForm(bound, out string formId)) return;

            DrakeCaptureRuntime.NormalizeBoundMetadata(bound, _player.GetPlayerID());

            if (bound.m_customData != null && bound.m_customData.TryGetValue(DrakeMedallionItemRuntime.BoundOwnerKey, out string ownerText) &&
                long.TryParse(ownerText, out long owner) && owner != 0L && owner != _player.GetPlayerID())
            {
                Notify("Diese Symbiosehalskette ist an einen anderen Spieler gebunden.");
                return;
            }

            if (MountSymbiosisBridge.Active)
            {
                string activeForm = MountSymbiosisBridge.ActiveForm;

                // Same necklace/form remains the intuitive toggle-off path. A different form
                // performs a safe queued handoff after Mount finishes its normal exit animation.
                if (string.Equals(activeForm, formId, StringComparison.OrdinalIgnoreCase))
                {
                    _pendingSwitchItem = null;
                    _pendingSwitchForm = null;
                    _pendingSwitchEgo = false;
                    if (MountSymbiosisBridge.TryExit(_player, out string exitMessage) && !string.IsNullOrEmpty(exitMessage))
                        Notify(exitMessage);
                    return;
                }

                if (string.Equals(formId, SymbiosisFormCatalog.Werewolf, StringComparison.OrdinalIgnoreCase) && !IsWerewolfDarkEnough(_player))
                {
                    Notify("Die Werwolf-Symbiose braucht Dunkelheit: Nacht oder eine Hoehle/ein Dungeon.");
                    return;
                }

                _pendingSwitchItem = bound;
                _pendingSwitchForm = formId;
                _pendingSwitchEgo = MountSymbiosisBridge.EgoActive;
                _pendingSwitchDeadline = Time.unscaledTime + 8f;

                if (MountSymbiosisBridge.TryExit(_player, out string switchExitMessage))
                {
                    Notify("Symbiose-Wechsel: " + GetFormDisplayName(activeForm) + " -> " + GetFormDisplayName(formId));
                    Plugin.Log?.LogInfo("[Symbiosis] Direct form switch queued: " + activeForm + " -> " + formId + ".");
                }
                else
                {
                    _pendingSwitchItem = null;
                    _pendingSwitchForm = null;
                    _pendingSwitchEgo = false;
                    Notify(string.IsNullOrEmpty(switchExitMessage) ? "Symbiose-Wechsel konnte nicht gestartet werden." : switchExitMessage);
                }
                return;
            }

            // v0.6.1: A bound symbiosis necklace is an activatable item, not equipment.
            // Using it from the inventory or hotbar starts the form directly; no utility-slot
            // equip step is required and vanilla must never toggle the necklace on first.

            // v0.6.4: Werewolf transformation needs darkness. Natural night works everywhere;
            // instanced dungeons/caves also count as dark even during daytime. Valheim places
            // dungeon interiors high above the normal world, so this deliberately does NOT
            // treat ordinary player houses/shelters as caves. Other forms are unaffected.
            if (string.Equals(formId, SymbiosisFormCatalog.Werewolf, StringComparison.OrdinalIgnoreCase) && !IsWerewolfDarkEnough(_player))
            {
                Notify("Die Werwolf-Symbiose braucht Dunkelheit: Nacht oder eine Hoehle/ein Dungeon.");
                return;
            }

            if (!MountSymbiosisBridge.TryEnter(_player, bound, out string enterMessage))
            {
                string fallback = string.Equals(formId, SymbiosisFormCatalog.Serpent, StringComparison.OrdinalIgnoreCase)
                    ? "Seeschlangen-Symbiose konnte nicht gestartet werden."
                    : string.Equals(formId, SymbiosisFormCatalog.Troll, StringComparison.OrdinalIgnoreCase)
                        ? "Troll-Symbiose konnte nicht gestartet werden."
                        : string.Equals(formId, SymbiosisFormCatalog.Werewolf, StringComparison.OrdinalIgnoreCase)
                            ? "Werwolf-Symbiose konnte nicht gestartet werden."
                            : "Drake-Symbiose konnte nicht gestartet werden.";
                Notify(string.IsNullOrEmpty(enterMessage) ? fallback : enterMessage);
                return;
            }

            if (!string.IsNullOrEmpty(enterMessage)) Notify(enterMessage);
        }



        private static string GetFormDisplayName(string formId)
        {
            if (string.Equals(formId, SymbiosisFormCatalog.Serpent, StringComparison.OrdinalIgnoreCase)) return "Seeschlange";
            if (string.Equals(formId, SymbiosisFormCatalog.Troll, StringComparison.OrdinalIgnoreCase)) return "Troll";
            if (string.Equals(formId, SymbiosisFormCatalog.Werewolf, StringComparison.OrdinalIgnoreCase)) return "Werwolf";
            return "Drake";
        }

        private static bool IsWerewolfDarkEnough(Player player)
        {
            return IsWorldNight() || IsDungeonInterior(player);
        }

        private static bool IsDungeonInterior(Player player)
        {
            try
            {
                if (player == null) return false;

                // Vanilla Valheim dungeon/cave instances live far above the normal world.
                // This is the standard runtime distinction between an instanced dungeon and
                // a roofed building in the overworld, so houses do not accidentally qualify.
                return player.transform.position.y > 3000f;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Symbiosis] Dungeon darkness check failed safely: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static bool IsWorldNight()
        {
            try
            {
                EnvMan env = EnvMan.instance;
                if (env == null) return false;

                Type type = env.GetType();
                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic;

                // Preferred path: use Valheim's own IsNight() when this build exposes it.
                var isNight = type.GetMethod("IsNight", flags, null, Type.EmptyTypes, null);
                if (isNight != null && isNight.ReturnType == typeof(bool))
                {
                    object target = isNight.IsStatic ? null : (object)env;
                    bool result = (bool)isNight.Invoke(target, null);
                    Plugin.Log?.LogDebug("[Symbiosis] Werewolf darkness via EnvMan.IsNight -> " + result);
                    return result;
                }

                // Valheim 1.0.x builds may expose the day cycle instead of IsNight.
                // Treat late dusk through early dawn as dark enough for the Werewolf.
                var getDayFraction = type.GetMethod("GetDayFraction", flags, null, Type.EmptyTypes, null);
                if (getDayFraction != null)
                {
                    object target = getDayFraction.IsStatic ? null : (object)env;
                    object value = getDayFraction.Invoke(target, null);
                    if (value != null)
                    {
                        float fraction = Convert.ToSingle(value);
                        fraction = fraction - Mathf.Floor(fraction);
                        bool result = fraction <= 0.22f || fraction >= 0.78f;
                        Plugin.Log?.LogDebug("[Symbiosis] Werewolf darkness via GetDayFraction=" + fraction.ToString("0.000") + " -> " + result);
                        return result;
                    }
                }

                // Secondary compatibility path: if a build only exposes IsDay(), invert it.
                var isDay = type.GetMethod("IsDay", flags, null, Type.EmptyTypes, null);
                if (isDay != null && isDay.ReturnType == typeof(bool))
                {
                    object target = isDay.IsStatic ? null : (object)env;
                    bool result = !(bool)isDay.Invoke(target, null);
                    Plugin.Log?.LogDebug("[Symbiosis] Werewolf darkness via !EnvMan.IsDay -> " + result);
                    return result;
                }

                // Last-resort cycle calculation for builds where only EnvMan fields are available.
                var totalField = type.GetField("m_totalSeconds", flags);
                var lengthField = type.GetField("m_dayLengthSec", flags);
                if (totalField != null && lengthField != null)
                {
                    double total = Convert.ToDouble(totalField.GetValue(totalField.IsStatic ? null : (object)env));
                    double length = Convert.ToDouble(lengthField.GetValue(lengthField.IsStatic ? null : (object)env));
                    if (length > 0.001)
                    {
                        double fraction = (total % length) / length;
                        if (fraction < 0.0) fraction += 1.0;
                        bool result = fraction <= 0.22 || fraction >= 0.78;
                        Plugin.Log?.LogDebug("[Symbiosis] Werewolf darkness via EnvMan cycle fields=" + fraction.ToString("0.000") + " -> " + result);
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Symbiosis] Night check failed safely: " + ex.GetType().Name + ": " + ex.Message);
            }

            Plugin.Log?.LogWarning("[Symbiosis] Could not resolve Valheim day/night state; Werewolf transformation stays blocked outside dungeons.");
            return false;
        }

        internal void ForceExit(string reason)
        {
            if (!MountSymbiosisBridge.Active) return;
            MountSymbiosisBridge.TryExit(_player, out _);
            if (!string.IsNullOrEmpty(reason)) Notify(reason);
        }

        private void Notify(string text)
        {
            try { _player?.Message(MessageHud.MessageType.Center, text); } catch { }
        }
    }
}
