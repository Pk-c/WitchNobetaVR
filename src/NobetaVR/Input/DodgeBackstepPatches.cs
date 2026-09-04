using HarmonyLib;

namespace NobetaVR.Input
{
    /// <summary>
    /// Makes the dodge come out as the backward hop rather than the roll.
    ///
    /// The two are one dodge in the game: one <c>Dodge()</c>, one <c>NobetaState.Dodge</c>, and
    /// a fork inside it that picks the animation and the speed together —
    /// <c>NobetaAnimatorController.PlayDodgeForward</c> with <c>NobetaConfigData.GetDodgeSpeedF</c>
    /// on one side, <c>PlayDodgeBack</c> with <c>GetDodgeSpeedB</c> on the other. What the fork
    /// asks is <c>NobetaInputData.IsDefaultDirection()</c>: whether you are holding a direction
    /// at all.
    ///
    /// Handing the game a centred stick was the first attempt and it was not enough — the
    /// direction the fork reads is computed across a frame boundary, so the one already in hand
    /// when the button is pressed is the one it answers with. So the question is answered
    /// instead, for the length of the one call and no longer. Both halves of the fork move
    /// together that way: the hop's animation comes with the hop's speed, rather than a back
    /// animation played over a forward roll's travel.
    ///
    /// <para>
    /// <see cref="RollBecomesHop"/> is a backstop under that, armed only while the same call is
    /// running. If the fork turns out not to be this predicate, the roll still cannot reach the
    /// screen — and the log says which of the two paths did the work, so the backstop can be
    /// removed once it is known to be dead weight.
    /// </para>
    ///
    /// Why any of it: on a monitor the roll is the better dodge and its spin is a flourish. In
    /// a headset the view is on her head, so that spin is the camera going over with her — the
    /// most reliable way there is to make someone ill, on a button pressed under pressure. The
    /// dodge is otherwise untouched: same invulnerability window, same stamina, same recovery.
    /// </summary>
    [HarmonyPatch]
    internal static class DodgeBackstepPatches
    {
        /// <summary>
        /// Set only around <c>PlayerInputController.Dodge()</c>, which is synchronous, so
        /// nothing else in the game can see either patch acting.
        /// </summary>
        internal static bool Forcing;

        private static int _logged;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(NobetaInputData), nameof(NobetaInputData.IsDefaultDirection))]
        private static void DirectionReadsAsCentred(ref bool __result)
        {
            if (Forcing) __result = true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NobetaAnimatorController), nameof(NobetaAnimatorController.PlayDodgeForward))]
        private static bool RollBecomesHop(NobetaAnimatorController __instance)
        {
            if (!Forcing)
            {
                Report("roll, left alone");
                return true;
            }

            Report("roll, redirected to the hop — the predicate was not the fork");

            _redirecting = true;
            try { __instance.PlayDodgeBack(); }
            finally { _redirecting = false; }

            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(NobetaAnimatorController), nameof(NobetaAnimatorController.PlayDodgeBack))]
        private static void Hop()
        {
            // The redirect above already said what happened; this would only repeat it.
            if (_redirecting) return;

            Report(Forcing ? "hop, forced at the fork" : "hop, the game's own choice");
        }

        private static bool _redirecting;

        /// <summary>
        /// The first few dodges, and no more. Which side of the fork ran is the one thing that
        /// cannot be read off a build, and it is the difference between this being solved at
        /// the fork and being papered over at the animator.
        /// </summary>
        private static void Report(string what)
        {
            if (_logged >= 6) return;
            _logged++;

            Plugin.Log.LogInfo($"dodge: {what}");
        }
    }
}
