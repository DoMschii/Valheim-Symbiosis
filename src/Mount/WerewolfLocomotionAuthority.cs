using UnityEngine;

namespace Valheim_Workers
{
    // Run after ordinary Character/Monster scripts without depending on an Iron Gate method name.
    // Valheim 1.0.17 no longer exposes Character.FixedUpdate, so a Harmony postfix against that
    // method aborts PatchAll. A late-execution component on the temporary Fenring body gives us the
    // same post-native physics authority while remaining tolerant of internal Character refactors.
    [DefaultExecutionOrder(32000)]
    internal sealed class WerewolfLocomotionAuthority : MonoBehaviour
    {
        private Character _character;

        private void Awake()
        {
            _character = GetComponent<Character>();
        }

        private void FixedUpdate()
        {
            if (_character == null) _character = GetComponent<Character>();
            if (_character != null) DrakeMountRuntime.PostCharacterFixedUpdate(_character);
        }

        private void LateUpdate()
        {
            if (_character == null) _character = GetComponent<Character>();
            if (_character != null) DrakeMountRuntime.PostCharacterLateUpdate(_character);
        }
    }
}
