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
    /// There are two shapes of rumble down there and not one, which is the thing this class
    /// got wrong for a while. An <em>event</em> carries a duration and ends by itself; a
    /// <em>level</em> is set and stands until something clears it. The cutscenes use the
    /// second, so their rumble reached the pad and nothing in the hands until
    /// <see cref="PadLevelSet"/> started forwarding levels as well.
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

        // The level route's own record of the same thing, for the dedupe in Play. Separate
        // fields rather than shared ones, because the two arrive in a fixed order and the
        // dedupe runs the other way: see PadLevelSet.
        private static int _levelFrame = -1;
        private static float _levelLow, _levelHigh;

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
        /// The motor levels themselves, which turn out to be a route of their own and not
        /// merely the tail of the one above.
        ///
        /// This method is where a rumble ends — the coroutine started by
        /// <c>EnableVibration</c> comes back after the duration and sets both motors to zero —
        /// and it is also where some of them begin. A level carries no duration: it stands
        /// until something sets another one, which is all a pad motor needs and the exact
        /// opposite of an OpenXR impulse. A non-zero level used to be ignored here on the
        /// reasoning that <c>EnableVibration</c> was its only caller and was already caught
        /// with the duration this call does not have. That reasoning was wrong for the
        /// cutscenes, whose rumble is set and held rather than fired: it reached the pad on the
        /// desk and nothing in the hands.
        ///
        /// So every level goes to <see cref="VrHaptics.Level"/>, which holds it and keeps it
        /// alive until it is cleared. The forwarding is the one case skipped: an identical
        /// level in the same frame as an event we have just played is
        /// <c>EnableVibration</c> setting its own motors, not a second rumble, and playing it
        /// as a held level would outlive the duration the event asked for.
        ///
        /// This also buys back <c>ResumeVibration</c>, which the old reading could not reach:
        /// a rumble paused by a menu and resumed sets its levels again on the way back, and a
        /// level is all this route ever needed.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GamepadVibration), nameof(GamepadVibration.SetFrequency))]
        private static void PadLevelSet(float lowFrequency, float highFrequency)
        {
            _levelFrame = Time.frameCount;
            _levelLow = lowFrequency;
            _levelHigh = highFrequency;

            VrHaptics.Level(lowFrequency, highFrequency);
        }

        /// <summary>
        /// One event, however many patches saw it.
        ///
        /// <c>Game.EnableVibration</c> forwards to the pad's own, so both fire for a single
        /// rumble. Whichever arrives first wins the frame; an identical call in the same frame
        /// is that forwarding rather than a second event, and two identical rumbles in one
        /// frame would be one rumble in the hand regardless.
        ///
        /// <para>
        /// The same event may also have arrived as a level already, and in that case it is left
        /// to the level. Postfixes run innermost first, so the order is fixed and is the
        /// opposite of the nesting: <c>SetFrequency</c> reports before the
        /// <c>EnableVibration</c> that called it. The level is the better of the two accounts
        /// anyway — it runs for exactly as long as the game's own coroutine holds it, rather
        /// than for a duration clamped and handed to the runtime in one piece — so this steps
        /// aside rather than adding a second rumble on top of it.
        /// </para>
        /// </summary>
        private static void Play(float seconds, float low, float high)
        {
            var frame = Time.frameCount;
            if (frame == _frame && seconds == _seconds && low == _low && high == _high) return;

            _frame = frame;
            _seconds = seconds;
            _low = low;
            _high = high;

            if (frame == _levelFrame && low == _levelLow && high == _levelHigh) return;

            VrHaptics.Play(seconds, low, high);
        }
    }
}
