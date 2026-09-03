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
    /// </summary>
    internal sealed class VrInput
    {
        // Unity's canonical feature usage names. Spelled out because CommonUsages, which is
        // where they would normally come from, has no fields left in this build.
        private const string Primary2DAxis = "Primary2DAxis";
        private const string Primary2DAxisClick = "Primary2DAxisClick";
        private const string TriggerButton = "TriggerButton";
        private const string GripButton = "GripButton";
        private const string PrimaryButton = "PrimaryButton";
        private const string SecondaryButton = "SecondaryButton";
        private const string MenuButton = "MenuButton";

        private InputDevice _left;
        private InputDevice _right;
        private bool _reported;

        public Vector2 LeftStick { get; private set; }
        public Vector2 RightStick { get; private set; }
        public bool Connected => _left.isValid || _right.isValid;

        public void Poll()
        {
            // Devices come and go: a controller that sleeps invalidates its handle, so these are
            // re-fetched whenever they stop being valid rather than resolved once.
            if (!_left.isValid) _left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            if (!_right.isValid) _right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);

            LeftStick = Axis(_left, Primary2DAxis);
            RightStick = Axis(_right, Primary2DAxis);

            Report();
        }

        public enum Hand { Left, Right }

        public enum Button { Trigger, Grip, Primary, Secondary, StickClick, Menu }

        public bool Pressed(Hand hand, Button button) => Held(Device(hand), Name(button));

        private InputDevice Device(Hand hand) => hand == Hand.Left ? _left : _right;

        private static string Name(Button b) => b switch
        {
            Button.Trigger => TriggerButton,
            Button.Grip => GripButton,
            Button.Primary => PrimaryButton,
            Button.Secondary => SecondaryButton,
            Button.StickClick => Primary2DAxisClick,
            _ => MenuButton,
        };

        private static Vector2 Axis(InputDevice device, string usage)
        {
            if (!device.isValid) return Vector2.zero;
            return InputDevices.TryGetFeatureValue_Vector2f(device.deviceId, usage, out var v)
                ? v : Vector2.zero;
        }

        private static bool Held(InputDevice device, string usage)
        {
            if (!device.isValid) return false;
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

            Plugin.Log.LogInfo($"controllers: left='{(_left.isValid ? _left.name : "none")}' "
                             + $"right='{(_right.isValid ? _right.name : "none")}'");
        }
    }
}
