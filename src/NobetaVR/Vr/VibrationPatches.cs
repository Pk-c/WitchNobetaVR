using HarmonyLib;
using UnityEngine;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Catches the game's pad rumble on its way out, so <see cref="VrHaptics"/> can play it on
    /// the controllers.
    ///
    /// The game's vibration is a small, closed API and every event in the game goes through it:
    /// <c>Game.EnableVibration(duration, low, high)</c> forwards to <c>GamepadVibration</c>,
    /// which sets the two motors and starts a coroutine to clear them when the duration is up.
    /// <c>Disable</c>, <c>Pause</c> and <c>Resume</c> are the rest of it. The animation events
    /// that fire most of the rumble in combat — <c>WizardGirlManage.AniGamePadVibration</c> —
    /// come down the same funnel, so there is nothing to enumerate: catching the funnel catches
    /// every event the game has.
    ///
    /// Both ends of the forwarding are patched rather than one, because neither can be assumed
    /// to be reached. A pad-less machine may make <c>GamepadVibration</c> stand down before it
    /// does anything, and an option check may sit in <c>Game</c> above it. Postfixes run
    /// whatever the body decided, so patching both is what makes this independent of where that
    /// check turns out to live — at the cost of one duplicate per event, which
    /// <see cref="Play"/> collapses.
    ///
    /// Nothing is suppressed. The pad's own rumble still runs for anyone playing with a pad in
    /// one hand, which is also what keeps the game's behaviour outside VR exactly as it was.
    /// </summary>
    [HarmonyPatch]
    internal static class VibrationPatches
    {
        private static int _frame = -1;
        private static float _seconds, _low, _high;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Game), nameof(Game.EnableVibration))]
        private static void GameEnabled(float duration, float lowFrequency, float highFrequency)
            => Play(duration, lowFrequency, highFrequency);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GamepadVibration), nameof(GamepadVibration.EnableVibration))]
        private static void PadEnabled(float duration, float lowFrequency, float highFrequency)
            => Play(duration, lowFrequency, highFrequency);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Game), nameof(Game.DisableVibration))]
        private static void GameDisabled() => VrHaptics.Stop();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Game), nameof(Game.PauseVibration))]
        private static void GamePaused() => VrHaptics.Stop();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GamepadVibration), nameof(GamepadVibration.DisableVibration))]
        private static void PadDisabled() => VrHaptics.Stop();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GamepadVibration), nameof(GamepadVibration.PauseVibration))]
        private static void PadPaused() => VrHaptics.Stop();

        /// <summary>
        /// The motor levels themselves, caught only when they are being cleared.
        ///
        /// This is where a rumble actually ends: the coroutine started by <c>EnableVibration</c>
        /// comes back after the duration and sets both motors to zero. Our impulses carry their
        /// own duration and would end there anyway, so this is for the rumble the game cuts
        /// short. A non-zero level is deliberately not acted on — the only caller that sets one
        /// is <c>EnableVibration</c>, which is already caught above with the duration this call
        /// does not carry.
        ///
        /// The cost of that is <c>ResumeVibration</c>: a rumble paused by a menu and resumed
        /// comes back on the pad and not in the hands. It is the tail of an event that was
        /// interrupted, and buying it back would mean playing an impulse of a length nothing
        /// here knows.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GamepadVibration), nameof(GamepadVibration.SetFrequency))]
        private static void PadLevelSet(float lowFrequency, float highFrequency)
        {
            if (lowFrequency <= 0f && highFrequency <= 0f) VrHaptics.Stop();
        }

        /// <summary>
        /// One event, however many patches saw it.
        ///
        /// <c>Game.EnableVibration</c> forwards to the pad's own, so both fire for a single
        /// rumble. Whichever arrives first wins the frame; an identical call in the same frame
        /// is that forwarding rather than a second event, and two identical rumbles in one
        /// frame would be one rumble in the hand regardless.
        /// </summary>
        private static void Play(float seconds, float low, float high)
        {
            var frame = Time.frameCount;
            if (frame == _frame && seconds == _seconds && low == _low && high == _high) return;

            _frame = frame;
            _seconds = seconds;
            _low = low;
            _high = high;

            VrHaptics.Play(seconds, low, high);
        }
    }
}
