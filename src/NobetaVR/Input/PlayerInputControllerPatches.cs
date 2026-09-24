using HarmonyLib;

namespace NobetaVR.Input
{
    /// <summary>
    /// Catches the live <c>PlayerInputController</c> as it is built.
    ///
    /// Nothing in the game exposes it as a property, and searching the scene for it would not
    /// help either — it is not a MonoBehaviour, so it is not a component anyone can find. Its
    /// own <c>Init</c> is the one moment it is guaranteed to exist and be reachable, and it
    /// runs once per stage, which is exactly the cadence the binding needs.
    /// </summary>
    [HarmonyPatch(typeof(PlayerInputController), nameof(PlayerInputController.Init))]
    internal static class PlayerInputControllerInitPatch
    {
        private static void Postfix(PlayerInputController __instance)
        {
            var controls = VrControls.Instance;
            if (controls == null) return;

            controls.InputController = __instance;
        }
    }
}
