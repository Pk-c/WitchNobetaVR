using System;
using UnityEngine;
using UnityEngine.XR;
using Native = NobetaVR.Xr.UnityOpenXrNative;

namespace NobetaVR.Vr
{
    /// <summary>Which controller a pad rumble is played on.</summary>
    internal enum HapticsHands
    {
        /// <summary>Both controllers, at the stronger of the two motors.</summary>
        Both,

        /// <summary>
        /// The pad's own split: the heavy motor to the left hand, the light one to the right.
        /// That is what the two motors are on an XInput pad, so it is a mapping rather than an
        /// invention — but it depends on the game actually using both, and this game drives
        /// them together, so it currently only halves the strength. Kept for the events that
        /// may not.
        /// </summary>
        Motors,

        Left,
        Right,
    }

    /// <summary>
    /// Plays the game's own rumble on the Touch controllers.
    ///
    /// The game has haptics already, and in VR they are not lost so much as sent nowhere: every
    /// one of them ends at <c>GamepadVibration</c>, which drives a pad's two motors, and a
    /// headset session usually has no pad. Nothing here invents feedback. The events are the
    /// game's, at the game's own durations and strengths — see <see cref="VibrationPatches"/>
    /// for where they are caught — and this is only the other end of the wire.
    ///
    /// Two things have to be translated on the way across. A pad has two motors and a
    /// controller has one actuator per hand, so the pair becomes an amplitude and, under
    /// <see cref="HapticsHands.Motors"/>, a hand. And a pad motor is an eccentric mass with
    /// real inertia while a Touch controller holds a linear actuator: the same 0.05 that hums
    /// audibly on a pad is nothing at all in the hand, which is what <c>HapticsMinAmplitude</c>
    /// is for.
    ///
    /// <para>
    /// Getting it *out* is the part that took measuring, and the note is left here because the
    /// obvious route is the one that does not work. <c>InputDevices.SendHapticImpulse</c> — the
    /// XR input subsystem, the same half of the engine every control in this mod is read
    /// through — returns false on these devices: the legacy device definition the provider
    /// registers carries no haptic capability, whatever actions are bound to it. So the
    /// impulse goes to the provider directly, through the haptic action
    /// <see cref="Xr.XrActionSet"/> declares.
    /// </para>
    ///
    /// <para>
    /// And the provider addresses its devices by <em>its own</em> ids, not the ones the XR
    /// input subsystem hands out for the same controllers. The package never has to think
    /// about this because it asks each Input System device for its "internal device id" with a
    /// device command; nothing here goes through the Input System, so the ids are kept from
    /// <c>RegisterDeviceDefinition</c>, which is the one moment the provider says what it calls
    /// them. Both are tried and the working pair is held, because a wrong id is not an error
    /// here — the action resolves, the impulse is accepted, and nothing is felt.
    /// </para>
    ///
    /// The whole resolution is written to the log once per hand, for the same reason: a rumble
    /// that is not felt is otherwise indistinguishable between a controller that refused it, an
    /// id nobody recognised, and a game event that never fired.
    /// </summary>
    internal static class VrHaptics
    {
        /// <summary>
        /// A rumble with no duration still means "tap". An impulse of zero seconds is nothing at
        /// all, so the shortest event the game can ask for is floored rather than dropped.
        /// </summary>
        private const float MinSeconds = 0.02f;

        /// <summary>One controller, and everything learned about how to reach it.</summary>
        private sealed class Side
        {
            public Side(XRNode node, string name) { Node = node; Name = name; }

            public readonly XRNode Node;
            public readonly string Name;

            public InputDevice Device;

            /// <summary>Set once the routes below have been tried and reported.</summary>
            public bool Resolved;

            /// <summary>The legacy route works on this device. Measured, not assumed.</summary>
            public bool Legacy;

            /// <summary>The provider's own id and haptic action, when one pair answered.</summary>
            public uint ProviderDevice;
            public ulong Action;

            public bool Provider => Action != 0;
        }

        private static readonly Side LeftSide = new(XRNode.LeftHand, "left");
        private static readonly Side RightSide = new(XRNode.RightHand, "right");

        /// <summary>Plays one of the game's vibration events.</summary>
        /// <param name="seconds">How long the game asked the pad to run for.</param>
        /// <param name="lowMotor">The heavy motor, 0 to 1.</param>
        /// <param name="highMotor">The light motor, 0 to 1.</param>
        public static void Play(float seconds, float lowMotor, float highMotor)
        {
            var cfg = Plugin.Instance;
            if (cfg == null || !cfg.Haptics.Value) return;

            // The game's own vibration switch is honoured rather than second-guessed: a player
            // who turned rumble off in the options meant it, and the mod's own switch is there
            // for anyone who wants the pad and not the hands.
            if (!GameVibrationOn()) return;

            var low = Mathf.Clamp01(lowMotor);
            var high = Mathf.Clamp01(highMotor);

            // Some of the game's calls are a stop phrased as a start.
            if (low <= 0f && high <= 0f)
            {
                Stop();
                return;
            }

            var longest = Mathf.Max(MinSeconds, cfg.HapticsMaxSeconds.Value);
            var duration = Mathf.Clamp(seconds, MinSeconds, longest);

            Emit(low, high, duration);
        }

        /// <summary>
        /// A rumble the game set as a level rather than as an event, kept running until it is
        /// cleared.
        ///
        /// <c>EnableVibration</c> is not the only way the game rumbles. Underneath it,
        /// <c>GamepadVibration.SetFrequency</c> sets the two motors and nothing else: no
        /// duration, no end, just a level that stands until something sets another one. A pad
        /// is happy with that — a motor holds whatever speed it was given — and an OpenXR
        /// impulse is the opposite, a burst of a stated length that is over when it is over.
        /// So a rumble authored as a level reached the pad on the desk and nothing at all in
        /// the hands, which is what "during cutscenes the haptics play on the gamepad and not
        /// on my controllers" was: the scenes hold their rumble rather than firing it.
        ///
        /// <para>
        /// The level is therefore held here and re-sent as overlapping impulses by
        /// <see cref="Tick"/> for as long as it stands. Only changes are acted on, because a
        /// level is a state rather than an event and a game is free to restate it every frame
        /// — and a zero is the end of the rumble, which is the one the pad's coroutine sends
        /// when a timed event runs out.
        /// </para>
        /// </summary>
        public static void Level(float lowMotor, float highMotor)
        {
            var low = Mathf.Clamp01(lowMotor);
            var high = Mathf.Clamp01(highMotor);

            if (low <= 0f && high <= 0f)
            {
                _sustaining = false;
                Stop();
                return;
            }

            var cfg = Plugin.Instance;
            if (cfg == null || !cfg.Haptics.Value || !GameVibrationOn()) return;

            // A level restated unchanged is the same rumble, not a new one.
            if (_sustaining && low == _levelLow && high == _levelHigh) return;

            _levelLow = low;
            _levelHigh = high;
            _sustaining = true;
            _sustainedSince = Time.unscaledTime;
            _refreshAt = Time.unscaledTime + RefreshEvery;

            Emit(low, high, ImpulseSeconds);
        }

        /// <summary>
        /// Keeps a held level alive, once a frame.
        ///
        /// The impulses overlap on purpose: each is asked for rather more than the gap between
        /// them, so the actuator is handed its next burst before the last has finished and the
        /// rumble has no seam in it. Re-sending to a running actuator replaces what it is doing
        /// rather than queueing behind it, which is what makes that safe.
        ///
        /// <para>
        /// The cap is a safety rail and not a tuning knob. A level that is never cleared is a
        /// rumble with nothing left to end it — a scene interrupted, a stage unloaded mid-event
        /// — and in the hand that is indistinguishable from the mod having broken. It says so
        /// in the log rather than going quiet, because a rumble that stopped early and one that
        /// was never sent read exactly alike.
        /// </para>
        /// </summary>
        public static void Tick()
        {
            if (!_sustaining) return;

            var cfg = Plugin.Instance;
            if (cfg == null || !cfg.Haptics.Value || !GameVibrationOn())
            {
                _sustaining = false;
                Stop();
                return;
            }

            if (Time.unscaledTime - _sustainedSince > SustainMax)
            {
                _sustaining = false;
                Stop();
                Plugin.Log.LogWarning($"haptics: a held rumble ran for {SustainMax:F0}s with "
                                    + "nothing clearing it, so it was stopped here. The game "
                                    + "sets a level and clears it; a level that outlives that "
                                    + "means the call that clears it never arrived.");
                return;
            }

            if (Time.unscaledTime < _refreshAt) return;
            _refreshAt = Time.unscaledTime + RefreshEvery;

            Emit(_levelLow, _levelHigh, ImpulseSeconds);
        }

        /// <summary>How often a held level is re-sent, in seconds.</summary>
        private const float RefreshEvery = 0.08f;

        /// <summary>
        /// And how long each of those bursts asks for. Longer than the gap above, so they
        /// overlap rather than stutter.
        /// </summary>
        private const float ImpulseSeconds = 0.2f;

        /// <summary>Longest a held level is kept alive with nothing clearing it, in seconds.</summary>
        private const float SustainMax = 30f;

        private static bool _sustaining;
        private static float _levelLow, _levelHigh;
        private static float _sustainedSince;
        private static float _refreshAt;

        /// <summary>One rumble out to whichever hands the setting asks for.</summary>
        private static void Emit(float low, float high, float duration)
        {
            switch (Plugin.Instance.HapticsHand.Value)
            {
                case HapticsHands.Motors:
                    Send(LeftSide, Amplitude(low), duration);
                    Send(RightSide, Amplitude(high), duration);
                    break;

                case HapticsHands.Left:
                    Send(LeftSide, Amplitude(Mathf.Max(low, high)), duration);
                    break;

                case HapticsHands.Right:
                    Send(RightSide, Amplitude(Mathf.Max(low, high)), duration);
                    break;

                default:
                    var both = Amplitude(Mathf.Max(low, high));
                    Send(LeftSide, both, duration);
                    Send(RightSide, both, duration);
                    break;
            }
        }

        /// <summary>
        /// A pulse on demand, for the settings page. Tuning a rumble you can only feel when the
        /// game happens to fire one is tuning it blind, and this is also the one test that
        /// separates "the route is wrong" from "the events are not arriving".
        /// </summary>
        public static void Test()
        {
            Play(0.3f, 0.8f, 0.8f);
        }

        /// <summary>
        /// Cuts whatever is playing. The impulses carry their own duration, so this is only for
        /// the game ending a rumble early — a pause menu, a cutscene, the option being turned
        /// off — rather than for their ordinary end.
        /// </summary>
        public static void Stop()
        {
            _sustaining = false;

            // Nothing was playing, so there is nothing to stop. The game clears its motors
            // rather more often than it sets them — a zero level is how a timed event ends, and
            // a game is free to restate that every frame — and each stop is two native calls a
            // hand that would otherwise be paid for a rumble that finished long ago.
            if (!_running) return;
            _running = false;

            StopOne(LeftSide);
            StopOne(RightSide);
        }

        /// <summary>Whether anything has been sent that has not since been stopped.</summary>
        private static bool _running;

        /// <summary>
        /// A pad motor is an eccentric mass and a Touch controller is a linear actuator, so the
        /// bottom of the pad's range does not exist in the hand: the floor is what keeps the
        /// game's lightest taps from arriving as nothing. Strength at zero still means silence
        /// — it is the switch a player reaches for, and a floor that overrode it would be a bug
        /// with an explanation.
        /// </summary>
        private static float Amplitude(float motor)
        {
            var cfg = Plugin.Instance;
            if (motor <= 0f || cfg.HapticsStrength.Value <= 0f) return 0f;

            return Mathf.Clamp01(Mathf.Max(motor * cfg.HapticsStrength.Value,
                                           cfg.HapticsMinAmplitude.Value));
        }

        private static void Send(Side side, float amplitude, float duration)
        {
            if (amplitude <= 0f) return;
            if (!Refresh(side)) return;

            if (side.Legacy
                && InputDevices.SendHapticImpulse(side.Device.deviceId, 0, amplitude, duration))
            {
                _running = true;
                return;
            }

            if (!side.Provider) return;

            try
            {
                Native.SendHapticImpulse(side.ProviderDevice, side.Action, amplitude,
                                         Mathf.Max(0f, Plugin.Instance.HapticsFrequency.Value),
                                         duration);
                _running = true;
            }
            catch (Exception e)
            {
                side.Action = 0;
                Plugin.Log.LogWarning($"haptics: the provider refused an impulse on the "
                                    + $"{side.Name} controller. {e.Message}");
            }
        }

        private static void StopOne(Side side)
        {
            if (!Refresh(side)) return;

            // The legacy stop returns nothing, so there is no telling whether it landed. Both
            // routes are told to stop when both are open: they address the same actuator, and
            // stopping a controller that was never running costs nothing.
            if (side.Legacy) InputDevices.StopHaptics(side.Device.deviceId);
            if (!side.Provider) return;

            try { Native.StopHaptics(side.ProviderDevice, side.Action); }
            catch { side.Action = 0; }
        }

        /// <summary>
        /// Devices come and go — a controller that sleeps invalidates its handle — so these are
        /// re-fetched whenever they stop being valid, the same way <see cref="Input.VrInput"/>
        /// does. Its handles are not borrowed: a rumble arrives from the game's own call stack
        /// rather than from the frame's input poll, and may well arrive on a frame where the
        /// controls have stood down.
        /// </summary>
        private static bool Refresh(Side side)
        {
            if (!side.Device.isValid)
            {
                side.Device = InputDevices.GetDeviceAtXRNode(side.Node);
                side.Resolved = false;
            }

            if (!side.Device.isValid) return false;
            if (!side.Resolved) Resolve(side);

            return true;
        }

        /// <summary>
        /// Works out how to reach this controller, once, and says so out loud.
        ///
        /// Everything here is a question a build cannot answer: whether the legacy device
        /// carries a haptic capability at all, which of the two id namespaces the provider
        /// answers on, and which spelling of the action name its lookup matches. Each is
        /// tried and the result recorded, so the log carries the measurement rather than this
        /// comment carrying an assumption.
        /// </summary>
        private static void Resolve(Side side)
        {
            side.Resolved = true;
            side.Legacy = false;
            side.Action = 0;
            side.ProviderDevice = 0;

            var log = Plugin.Log;
            var legacyId = side.Device.deviceId;

            var known = InputDevices.TryGetHapticCapabilities(legacyId, out var caps);
            var capabilities = known
                ? $"impulse={caps.supportsImpulse} channels={caps.numChannels}"
                : "unavailable";

            // Unknown is tried rather than refused: the send returns a bool, so an attempt that
            // cannot work costs one false. Only a device that answered and said no is skipped.
            side.Legacy = !known || (caps.supportsImpulse && caps.numChannels > 0);

            // The provider's own id first, kept from RegisterDeviceDefinition, then the XR
            // subsystem's, in case this provider does use one namespace for both.
            var registered = side.Node == XRNode.LeftHand
                ? Xr.XrActionSet.LeftDeviceId
                : Xr.XrActionSet.RightDeviceId;

            foreach (var candidate in new[] { registered, legacyId })
            {
                if (candidate == 0 || side.Provider) continue;

                // "Haptic" is the usage the action was declared with, and the name the
                // package's own lookup builds from a control: each path component capitalised,
                // which turns "haptic" into it. The lowercase path name is tried after, since
                // one of the two is what this provider matches on.
                foreach (var name in new[] { "Haptic", "haptic" })
                {
                    ulong action;
                    try { action = Native.GetActionIdByControl((uint)candidate, name); }
                    catch (Exception e)
                    {
                        log.LogWarning($"haptics: the provider has no action lookup. {e.Message}");
                        return;
                    }

                    if (action == 0) continue;

                    side.ProviderDevice = (uint)candidate;
                    side.Action = action;
                    break;
                }
            }

            log.LogInfo($"haptics on the {side.Name} controller: xr device {legacyId} "
                      + $"({capabilities}), provider device {side.ProviderDevice} "
                      + $"action {side.Action}, registered id {registered} — "
                      + $"routes: legacy {(side.Legacy ? "yes" : "no")}, "
                      + $"provider {(side.Provider ? "yes" : "no")}");

            if (!side.Legacy && !side.Provider)
            {
                log.LogWarning($"haptics: nothing will reach the {side.Name} controller. Neither "
                             + "the XR input subsystem nor the OpenXR provider offered a way in.");
            }
        }

        private static bool GameVibrationOn()
        {
            try
            {
                var settings = Game.Config?.gameSettings;
                return settings == null || settings.gamepadVibration;
            }
            catch
            {
                // Before the config is loaded there is no setting to disagree with.
                return true;
            }
        }
    }
}
