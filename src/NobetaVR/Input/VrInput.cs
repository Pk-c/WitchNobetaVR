using UnityEngine;
using UnityEngine.XR;

namespace NobetaVR.Input
{
    /// <summary>
    /// Reads the Touch controllers.
    ///
    /// The usual way to do this — <c>InputDevice.TryGetFeatureValue&lt;T&gt;</c> — does not
    /// exist here. It is generic, the game never called it, and IL2CPP compiles only the
    /// generic instantiations a build actually uses. The same trick as everywhere else in this
    /// mod applies: underneath the stripped generic sit the engine bindings it wraps, and those
    /// survive because they are internal calls registered by name in UnityPlayer rather than
    /// managed code.
    ///
    /// So features are read through <c>InputDevices.TryGetFeatureValue_Vector2f</c> and
    /// friends, which take the usage as a plain string. The strings are Unity's canonical
    /// usage names; <c>CommonUsages</c> itself is stripped to an empty shell, since its members
    /// are only static readonly wrappers around these same names.
    ///
    /// <para>
    /// Every button is read once a frame, in <see cref="Poll"/>, and answered from a bitmask
    /// afterwards. Each of those reads is a native call across the interop boundary with a
    /// managed string marshalled into it, and <see cref="Pressed"/> has upwards of thirty call
    /// sites — several of which ask the same question twice in one frame, because a held flag
    /// and the edge taken from it are written in different places. Reading live meant paying
    /// for every one of those; reading once means paying for the buttons that exist.
    /// </para>
    /// </summary>
    internal sealed class VrInput
    {
        // Unity's canonical feature usage names. Spelled out because CommonUsages, which is
        // where they would normally come from, has no fields left in this build.
        private const string Primary2DAxis = "Primary2DAxis";
        private const string Primary2DAxisClick = "Primary2DAxisClick";
        private const string TriggerButton = "TriggerButton";
        private const string GripButton = "GripButton";

        /// <summary>
        /// The squeeze as a number rather than as a verdict; see <see cref="Squeeze"/>.
        /// Declared alongside the button in <see cref="Xr.XrActionSet"/>, on the same
        /// <c>/input/squeeze/value</c> path, so both are already there to be read.
        /// </summary>
        private const string GripAxis = "Grip";
        private const string PrimaryButton = "PrimaryButton";
        private const string SecondaryButton = "SecondaryButton";
        private const string MenuButton = "MenuButton";

        private InputDevice _left;
        private InputDevice _right;
        private bool _leftValid;
        private bool _rightValid;
        private bool _reported;

        public Vector2 LeftStick { get; private set; }
        public Vector2 RightStick { get; private set; }
        public bool Connected => _leftValid || _rightValid;

        public enum Hand { Left, Right }

        public enum Button { Trigger, Grip, Primary, Secondary, StickClick, Menu }

        /// <summary>How many values <see cref="Button"/> has; the stride of the held bitmask.</summary>
        private const int Buttons = 6;

        /// <summary>
        /// This frame's buttons, left hand in the low bits and right hand above them. Held as
        /// one int rather than an array so a read is a shift and a mask.
        /// </summary>
        private int _held;

        public void Poll()
        {
            // Devices come and go: a controller that sleeps invalidates its handle, so these are
            // re-fetched whenever they stop being valid rather than resolved once.
            if (!_left.isValid) _left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            if (!_right.isValid) _right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);

            // Read once and kept, because `isValid` is itself a native call and every read
            // below would otherwise ask it again.
            _leftValid = _left.isValid;
            _rightValid = _right.isValid;

            LeftStick = Axis(_left, _leftValid, Primary2DAxis);
            RightStick = Axis(_right, _rightValid, Primary2DAxis);

            var held = 0;

            for (var b = 0; b < Buttons; b++)
            {
                var usage = Name((Button)b);
                if (Held(_left, _leftValid, usage)) held |= 1 << b;
                if (Held(_right, _rightValid, usage)) held |= 1 << (b + Buttons);
            }

            // The grip is settled from the analog squeeze rather than from the runtime's own
            // button; see Squeeze. Before `_held` is replaced, because the hysteresis there
            // reads the previous frame out of it.
            held = Squeeze(held, _left, _leftValid, 0);
            held = Squeeze(held, _right, _rightValid, Buttons);

            _held = held;

            Report();
        }

        /// <summary>
        /// Whether a button was down when this frame was polled.
        ///
        /// Answered from the mask rather than from the device: two callers asking on the same
        /// frame now get the same answer by construction, which they did not before — a button
        /// released between two live reads used to read down for one of them and up for the
        /// other, and the pair of them is exactly how a one-shot edge is built.
        /// </summary>
        /// <summary>
        /// Whether anything at all is held, either hand. One test against the mask rather than
        /// twelve calls, and it is only ever asked the once a frame.
        /// </summary>
        public bool AnyButton => _held != 0;

        public bool Pressed(Hand hand, Button button)
            => (_held & (1 << ((int)button + (hand == Hand.Left ? 0 : Buttons)))) != 0;

        /// <summary>
        /// This frame's device for one of the two hands, for the code that reads poses rather
        /// than buttons.
        ///
        /// The hands and the melee swing were each calling <c>GetDeviceAtXRNode</c> themselves,
        /// three times a frame between them, to arrive at the two handles this already holds
        /// and already keeps valid. Handing them out is the same handle and one fewer place
        /// that can disagree about which controller is which.
        /// </summary>
        public bool TryDevice(XRNode node, out InputDevice device)
        {
            switch (node)
            {
                case XRNode.LeftHand:
                    device = _left;
                    return _leftValid;
                case XRNode.RightHand:
                    device = _right;
                    return _rightValid;
                default:
                    device = default;
                    return false;
            }
        }

        private static string Name(Button b) => b switch
        {
            Button.Trigger => TriggerButton,
            Button.Grip => GripButton,
            Button.Primary => PrimaryButton,
            Button.Secondary => SecondaryButton,
            Button.StickClick => Primary2DAxisClick,
            _ => MenuButton,
        };

        private static Vector2 Axis(InputDevice device, bool valid, string usage)
        {
            if (!valid) return Vector2.zero;
            return InputDevices.TryGetFeatureValue_Vector2f(device.deviceId, usage, out var v)
                ? v : Vector2.zero;
        }

        /// <summary>
        /// How far the squeeze falls back before the grip counts as released, as a fraction of
        /// the press threshold. A single threshold chatters: a finger held near it crosses it
        /// several times a second, and each crossing is a fresh press to everything reading an
        /// edge — which on the left grip is an item change.
        /// </summary>
        private const float ReleaseFraction = 0.6f;

        /// <summary>
        /// Replaces one hand's grip bit with a verdict taken from the analog squeeze.
        ///
        /// <c>GripButton</c> is the runtime's own answer to the same question, and on Touch it
        /// is a hard one: the press does not register until the squeeze is most of the way in,
        /// which is fine for a grab and wrong for a button you tap to step through your items.
        /// The axis underneath it is the same input without the verdict attached, so the
        /// threshold becomes a setting instead of the runtime's business.
        ///
        /// <para>
        /// The runtime's button is kept as a floor rather than discarded. A controller that
        /// reports no analog squeeze at all — and the profile is suggested to runtimes that
        /// remap it onto hardware we have never seen — would otherwise lose the grip
        /// entirely, so the two are OR'd: a lighter threshold can only ever add presses.
        /// </para>
        /// </summary>
        private int Squeeze(int held, InputDevice device, bool valid, int shift)
        {
            var bit = 1 << ((int)Button.Grip + shift);
            if (!valid) return held & ~bit;

            if (!InputDevices.TryGetFeatureValue_float(device.deviceId, GripAxis, out var pull))
                return held;

            var press = Mathf.Clamp(Plugin.Instance.GripThreshold.Value, 0.05f, 1f);
            var down = (held & bit) != 0
                    || pull >= ((_held & bit) != 0 ? press * ReleaseFraction : press);

            return down ? held | bit : held & ~bit;
        }

        private static bool Held(InputDevice device, bool valid, string usage)
        {
            if (!valid) return false;
            return InputDevices.TryGetFeatureValue_bool(device.deviceId, usage, out var v) && v;
        }

        /// <summary>
        /// Names the controllers once they arrive. Worth a line: "the stick does nothing" is
        /// otherwise indistinguishable between a controller that never connected and a binding
        /// that reads the wrong usage.
        /// </summary>
        private void Report()
        {
            if (_reported || !Connected) return;
            _reported = true;

            Plugin.Log.LogInfo($"controllers: left='{(_leftValid ? _left.name : "none")}' "
                             + $"right='{(_rightValid ? _right.name : "none")}'");
        }
    }
}
