using HarmonyLib;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Makes the second jump look like the first.
    ///
    /// The game has two jump animations and plays a different one in the air:
    /// <c>NobetaAnimatorController.PlayJump</c> for the one off the ground and
    /// <c>PlaySkyJump</c> for the air jump, which is the flourish — she turns over as she goes.
    /// On a monitor that is the double jump reading as a double jump. In a headset the view is
    /// on her head, so the flourish is your own head being rolled through it, on a button you
    /// press while crossing a gap and cannot brace for. It is the same complaint the dodge roll
    /// had, arriving from the other direction; see <see cref="Input.DodgeBackstepPatches"/>.
    ///
    /// <para>
    /// Done as a postfix rather than by replacing the call. Whatever else the sky jump sets on
    /// its way through — the animator's own sky timing among it — still happens, and only the
    /// clip is overridden, by a second crossfade in the same frame that the first never gets to
    /// show. Nothing about the jump itself is touched: the same force, the same air control,
    /// the same landing.
    /// </para>
    /// </summary>
    [HarmonyPatch]
    internal static class AirJumpAnimation
    {
        private static int _logged;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(NobetaAnimatorController), nameof(NobetaAnimatorController.PlaySkyJump))]
        private static void SkyJumpLooksLikeTheFirst(
            NobetaAnimatorController __instance, float duration, float startTime)
        {
            var cfg = Plugin.Instance;
            if (cfg == null || !cfg.AirJumpKeepsJumpAnimation.Value) return;

            __instance.PlayJump(duration, startTime);

            // The first few, and no more. Air jumps are frequent, and what this has to confirm
            // is only that the postfix is reached at all -- an animator method that turns out
            // not to be the one the air jump calls would say so by this line never appearing.
            if (_logged >= 4) return;
            _logged++;
            Plugin.Log.LogInfo($"air jump: playing the ground jump's animation instead "
                             + $"(duration {duration:F2}, start {startTime:F2})");
        }
    }
}
