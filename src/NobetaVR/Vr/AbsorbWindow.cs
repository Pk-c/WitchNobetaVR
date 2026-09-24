using UnityEngine;

namespace NobetaVR.Vr
{
    /// <summary>
    /// The parry, made reachable from a swing that never plays an attack animation.
    ///
    /// <b>What the mechanic is.</b> A blow that arrives while
    /// <c>NobetaRuntimeData.absorbTimer</c> is running does not damage Nobeta: the game puts
    /// her into <c>NobetaState.Absorb</c> instead and she takes mana out of the blow. The
    /// attack animation opens that timer as it swings — <c>ABSORB_TIME_MAX</c> is 0.15 s, which
    /// is the figure every account of this mechanic quotes — so on a pad the parry is simply
    /// "attack just before the blow lands".
    ///
    /// <para>
    /// It took four attempts to find that, and the three failures are worth keeping because
    /// each looked right:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item><description><c>absorbStatusTimer</c> (1.50 s) is what a successful absorb leaves
    /// behind, and it is what speeds a later cast up. It parries nothing: a blow was taken for
    /// full damage with 2.90 s of it on the clock.</description></item>
    /// <item><description><c>PlayerInputController.Chant()</c> is not the absorb. It puts her
    /// in <c>Aim</c> for a frame and returns.</description></item>
    /// <item><description>The attack <i>state</i> is not the condition either. A blow was taken
    /// for 33 damage in <c>state Attack</c> with <c>absorbTimer</c> at zero. Being in an attack
    /// is merely how the timer normally comes to be running.</description></item>
    /// </list>
    ///
    /// <b>Why VR needs anything at all.</b> <see cref="VrMelee"/> opens the attack collision
    /// directly on the ground and never plays the attack — that is the point of the free swing,
    /// since the animation plants her feet and takes your arm away mid-stroke. So nothing opens
    /// the timer, and the parry is unavailable however well timed. Turning the free swing off
    /// restores it immediately, which is the control that proved it.
    ///
    /// <para>
    /// So the swing opens it by hand, through the game's own <c>FillAbsorbTimer</c>. Fourth of
    /// its kind in <see cref="VrMelee.FreeSwing"/>, after the swing sound, the voice and the
    /// trail: a thing the animation does, done by hand because there is no animation.
    /// </para>
    ///
    /// <b>What stops it being invulnerability.</b> Two limits, and the first is the one that
    /// matters. A window of a third of a second, reopened seven times a second, never shuts —
    /// so only the <i>first</i> swing of a burst opens one, and <see cref="Plugin.ParryArmInterval"/>
    /// is how long you must go without swinging to earn another. Mash and you get exactly one
    /// parry at the start of the flurry. Underneath that the game's own <c>absorbCDTimer</c>
    /// still runs: filled for a second on every parry and on every blow that does land.
    ///
    /// <para>
    /// A gate on swing spacing would be the wrong tool for a stagger, where it would decide
    /// whether a blow lands on a difference the player cannot feel — which in a headset reads
    /// as a hit that failed to register. It is the right tool here, because nothing is taken
    /// away: the blow lands either way, and what the quiet moment buys is a defence.
    /// </para>
    /// </summary>
    internal static class AbsorbWindow
    {
        /// <summary>When the last ground swing landed, unscaled — the player's arm, not the world.</summary>
        private static float _swungAt = float.NegativeInfinity;

        /// <summary>
        /// A ground swing: opens the parry window the attack animation would have opened.
        ///
        /// Called from <see cref="VrMelee.FreeSwing"/>, which is the only path that needs it.
        /// In the air the swing goes through the game's own attack and the window opens itself.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        internal static void Swung(WizardGirlManage girl)
        {
            var now = Time.unscaledTime;
            var since = now - _swungAt;
            _swungAt = now;

            var cfg = Plugin.Instance;
            if (cfg == null || !cfg.ParryFromSwing.Value || girl == null) return;

            // Only the first swing of a burst arms anything. See the class comment: this is
            // what keeps the parry a reward for timing rather than for flailing.
            if (since < cfg.ParryArmInterval.Value) return;

            var controller = girl.playerController;
            var data = controller != null ? controller.runtimeData : null;
            if (data == null) return;

            // Not while the game's own cooldown runs, which it fills for a second on every
            // parry and on every blow that does land.
            if (data.absorbCDTimer > 0f) return;

            data.FillAbsorbTimer();

            // The game's own length is 0.15 s, authored against a pad. Overridden rather than
            // scaled so the setting reads in the unit the player is actually choosing — and
            // only here, on the window a swing opens. The absorb that a successful parry throws
            // her into is refilled by the game and left alone.
            var window = cfg.ParryWindow.Value;
            if (window > 0f) data.absorbTimer = window;
        }
    }
}
