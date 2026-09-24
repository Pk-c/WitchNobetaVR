using HarmonyLib;

namespace NobetaVR.Input
{
    /// <summary>
    /// Makes the dodge come out as the backward hop rather than the roll.
    ///
    /// The two are one dodge in the game: one <c>NobetaState.Dodge</c> on the ground,
    /// <c>NobetaState.AirDodge</c> in the air, and a fork inside <c>PlayerController.InitState</c>
    /// for each that picks the animation and the travel together —
    /// <c>NobetaAnimatorController.PlayDodgeForward</c> on one side, <c>PlayDodgeBack</c> on the
    /// other. What the fork asks is whether <c>NobetaRuntimeData.moveDirection</c> is zero, and
    /// nothing the mod feeds in on the press frame reaches that: the redirect below is the whole
    /// mechanism, armed only while <see cref="VrControls"/> is inside its own dodge call.
    ///
    /// <para>
    /// The question the fork does not ask must be left alone. <c>NobetaInputData.IsDefaultDirection</c>
    /// is read by <c>OnDodgeKeyDown</c> alone, as a gate: in <c>Braking</c>, <c>Land</c>,
    /// <c>HighlyLand</c>, <c>DamagedLand</c>, <c>AirSlip</c>, <c>StandUp</c> and — outside the
    /// brief window after a hit — <c>AirDamagedFly</c>, the game only lets the dodge out when a
    /// direction is held. The mod used to force that predicate to "centred" for the call, which
    /// never moved the fork and silently refused every one of those dodges: most visibly the
    /// recovery out of being thrown into the air, and the air jump that follows it.
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
        /// nothing else in the game can see the redirect acting.
        /// </summary>
        internal static bool Forcing;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NobetaAnimatorController), nameof(NobetaAnimatorController.PlayDodgeForward))]
        private static bool RollBecomesHop(NobetaAnimatorController __instance)
        {
            if (!Forcing) return true;

            __instance.PlayDodgeBack();
            return false;
        }
    }
}
