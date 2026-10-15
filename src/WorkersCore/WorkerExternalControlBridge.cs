using System;

namespace Valheim_Workers
{
    /// <summary>
    /// Stable extension point for optional add-ons that temporarily take physical control of a Worker.
    /// The Workers core never needs to know which add-on owns the control session.
    /// </summary>
    internal static class WorkerExternalControlBridge
    {
        private static object _owner;
        private static Func<Character, bool> _isControlled;
        private static Func<Character, float, bool> _applyControls;
        private static Func<Character, string> _hoverText;

        internal static void Register(object owner, Func<Character, bool> isControlled,
            Func<Character, float, bool> applyControls, Func<Character, string> hoverText = null)
        {
            if (owner == null) return;
            _owner = owner;
            _isControlled = isControlled;
            _applyControls = applyControls;
            _hoverText = hoverText;
        }

        internal static void Unregister(object owner)
        {
            if (owner == null || !ReferenceEquals(_owner, owner)) return;
            _owner = null;
            _isControlled = null;
            _applyControls = null;
            _hoverText = null;
        }

        internal static bool IsControlled(Character worker)
        {
            if (worker == null || _isControlled == null) return false;
            try { return _isControlled(worker); }
            catch { return false; }
        }

        internal static bool TryApplyControls(Character worker, float dt)
        {
            if (worker == null || _applyControls == null) return false;
            try { return _applyControls(worker, dt); }
            catch { return false; }
        }

        /// <summary>Null means no override. Empty string intentionally suppresses hover text.</summary>
        internal static string GetHoverText(Character worker)
        {
            if (worker == null || _hoverText == null) return null;
            try { return _hoverText(worker); }
            catch { return null; }
        }
    }
}
