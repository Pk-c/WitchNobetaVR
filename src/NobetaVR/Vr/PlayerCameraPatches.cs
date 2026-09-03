using HarmonyLib;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Wraps the game's camera update so the head pose exists only for rendering.
    ///
    /// `PlayerCamera` is a smoothed follower: it reads its own transform and eases towards a
    /// target, which is what `g_fMoveLeap`, `g_fRotationLeap` and `g_fDisLeap` are for. Leave a
    /// head offset in that transform and it becomes the follower's input state on the next
    /// pass; the loop diverges and the view runs away and rolls. So the prefix takes the offset
    /// back off, and the postfix puts it on again once the game has finished its arithmetic.
    ///
    /// Doing the applying here rather than in a LateUpdate of our own is not a stylistic
    /// choice. **`PlayerCamera` is not a MonoBehaviour** — it derives from Object, so Unity
    /// never calls this method; the game does, from `WizardGirlManage.LateUpdate`. Applying the
    /// pose from our own LateUpdate therefore raced the game and lost: our offset went on, the
    /// game's LateUpdate ran afterwards and recomputed the camera cleanly, and the headset saw
    /// a world with no head tracking at all. On the title screen, where no WizardGirlManage
    /// exists, the very same code worked — which is what made the fault look like two
    /// unrelated bugs instead of one.
    ///
    /// Wrapping the method sidesteps the question entirely: whenever and from wherever the game
    /// chooses to update its camera, the offset lands immediately after it.
    /// </summary>
    [HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.Update))]
    internal static class PlayerCameraUpdatePatch
    {
        // __instance is also how the mod learns which camera is live, without searching.
        private static void Prefix(PlayerCamera __instance) => VrCamera.BeforeGameCameraUpdate(__instance);

        private static void Postfix() => VrCamera.AfterGameCameraUpdate();
    }
}
