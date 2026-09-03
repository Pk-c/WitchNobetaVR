using System.Collections.Generic;
using Native = NobetaVR.Xr.UnityOpenXrNative;

namespace NobetaVR.Xr
{
    /// <summary>
    /// Declares the controllers to OpenXR.
    ///
    /// OpenXR has no notion of "the left thumbstick". It has actions — named, typed, and bound
    /// to paths on an interaction profile — which are committed to the session in one go. Until
    /// that happens the runtime reports no controllers, so `InputDevices.GetDeviceAtXRNode`
    /// hands back nothing and every feature read returns false. The first cut of this mod
    /// skipped the step, on the grounds that it lived in the package's "features" and no
    /// feature seemed necessary for a plain stereo view. That was true for the display and
    /// wrong for the controllers.
    ///
    /// This is a transcription of what `OpenXRInput.AttachActionSets` and
    /// `OculusTouchControllerProfile` do in com.unity.xr.openxr@1.6.0, reduced to the actions
    /// this mod actually reads. The usage strings are the contract with the other end: each one
    /// becomes a feature on the resulting `InputDevice`, and is the exact string
    /// `TryGetFeatureValue_*` must be given to read it back.
    /// </summary>
    internal static class XrActionSet
    {
        private const string LeftHand = "/user/hand/left";
        private const string RightHand = "/user/hand/right";

        /// <summary>
        /// Only the Oculus Touch profile is suggested. SteamVR and other runtimes remap Touch
        /// bindings onto Index, Vive and WMR controllers on their own, so this covers far more
        /// hardware than its name suggests; profiles for those controllers can be added later
        /// if a runtime turns out not to.
        /// </summary>
        private const string TouchProfile = "/interaction_profiles/oculus/touch_controller";

        // UnityEngine.XR.InputDeviceCharacteristics, as flags.
        private const uint HeldInHand = 1 << 2;
        private const uint TrackedDevice = 1 << 5;
        private const uint Controller = 1 << 6;
        private const uint Left = 1 << 8;
        private const uint Right = 1 << 9;

        private readonly struct Action
        {
            public readonly string Name;
            public readonly Native.ActionType Type;
            public readonly string Usage;

            /// <summary>Path on the profile, or a left/right pair when the two differ.</summary>
            public readonly string Path;
            public readonly string LeftPath;
            public readonly string RightPath;

            public Action(string name, Native.ActionType type, string usage, string path)
            {
                Name = name; Type = type; Usage = usage;
                Path = path; LeftPath = null; RightPath = null;
            }

            public Action(string name, Native.ActionType type, string usage,
                          string leftPath, string rightPath)
            {
                Name = name; Type = type; Usage = usage;
                Path = null; LeftPath = leftPath; RightPath = rightPath;
            }
        }

        private static readonly Action[] Actions =
        {
            new("thumbstick",        Native.ActionType.Axis2D, "Primary2DAxis",      "/input/thumbstick"),
            new("thumbstickClicked", Native.ActionType.Binary, "Primary2DAxisClick", "/input/thumbstick/click"),
            new("trigger",           Native.ActionType.Axis1D, "Trigger",            "/input/trigger/value"),
            new("triggerPressed",    Native.ActionType.Binary, "TriggerButton",      "/input/trigger/value"),
            new("grip",              Native.ActionType.Axis1D, "Grip",               "/input/squeeze/value"),
            new("gripPressed",       Native.ActionType.Binary, "GripButton",         "/input/squeeze/value"),

            // X and Y on the left hand, A and B on the right: the same two actions, different
            // paths per hand, which is why these carry a pair.
            new("primaryButton",     Native.ActionType.Binary, "PrimaryButton",   "/input/x/click", "/input/a/click"),
            new("secondaryButton",   Native.ActionType.Binary, "SecondaryButton", "/input/y/click", "/input/b/click"),

            // Only the left controller has a menu button; /input/system is reserved by the
            // runtime on the right, so binding it would be refused.
            new("menu",              Native.ActionType.Binary, "MenuButton",      "/input/menu/click", null),

            // Not read yet. Declared now because motion-controlled hands will need them, and
            // adding an action later means rebuilding the whole set.
            new("devicePose",        Native.ActionType.Pose,    "Device",  "/input/grip/pose"),
            new("pointerPose",       Native.ActionType.Pose,    "Pointer", "/input/aim/pose"),
            new("haptic",            Native.ActionType.Vibrate, "Haptic",  "/output/haptic"),
        };

        /// <summary>
        /// Makes a name usable as an OpenXR path component.
        ///
        /// Paths are lowercase, and only letters, digits, and <c>- . _ /</c> are allowed.
        /// A capital letter is not tolerated and not corrected: the runtime answers
        /// XR_ERROR_PATH_FORMAT_INVALID and the action is simply never created. That failure
        /// then hides — the action set still attaches, the devices still register, and the
        /// only symptom is that half the controls silently do nothing. The package has a
        /// SanitizeStringForOpenXRPath for exactly this; leaving it out cost every action
        /// whose name was camelCase.
        /// </summary>
        private static string SanitizePath(string name)
        {
            var chars = new char[name.Length];
            var n = 0;
            foreach (var c in name)
            {
                if (char.IsUpper(c)) chars[n++] = char.ToLowerInvariant(c);
                else if (char.IsLower(c) || char.IsDigit(c)) chars[n++] = c;
                else if (c is '-' or '.' or '_' or '/') chars[n++] = c;
                // anything else is dropped, as the package does
            }
            return new string(chars, 0, n);
        }

        /// <summary>
        /// Builds and commits the action set. Called once per session, after xrBeginSession and
        /// before the input subsystem starts — the order the package uses, and the order the
        /// runtime requires: actions cannot be attached to a session that has not begun, and
        /// the input subsystem has nothing to enumerate until they are.
        /// </summary>
        public static bool Attach()
        {
            var log = Plugin.Log;

            if (Native.RegisterDeviceDefinition(LeftHand, TouchProfile,
                    HeldInHand | TrackedDevice | Controller | Left,
                    "Oculus Touch Controller OpenXR", "Oculus", "") == 0
             || Native.RegisterDeviceDefinition(RightHand, TouchProfile,
                    HeldInHand | TrackedDevice | Controller | Right,
                    "Oculus Touch Controller OpenXR", "Oculus", "") == 0)
            {
                log.LogError($"Could not register the controller devices. {Native.LastError()}");
                return false;
            }

            var guid = default(Native.SerializedGuid);
            var actionSet = Native.CreateActionSet(SanitizePath("nobetavr"), "NobetaVR", guid);
            if (actionSet == 0)
            {
                log.LogError($"Could not create the action set. {Native.LastError()}");
                return false;
            }

            var bindings = new List<Native.SerializedBinding>();
            var hands = new[] { LeftHand, RightHand };
            var created = 0;

            foreach (var action in Actions)
            {
                var actionId = Native.CreateAction(
                    actionSet, SanitizePath(action.Name), action.Name, (uint)action.Type, guid,
                    hands, (uint)hands.Length,
                    new[] { action.Usage }, 1);

                if (actionId == 0)
                {
                    log.LogWarning($"Action '{action.Name}' was refused. {Native.LastError()}");
                    continue;
                }

                created++;

                foreach (var hand in hands)
                {
                    var path = action.Path
                            ?? (hand == LeftHand ? action.LeftPath : action.RightPath);
                    if (path == null) continue;   // this hand does not have this control

                    bindings.Add(new Native.SerializedBinding { ActionId = actionId, Path = hand + path });
                }
            }

            if (!Native.SuggestBindings(TouchProfile, bindings.ToArray(), (uint)bindings.Count))
            {
                log.LogError($"The runtime refused the suggested bindings. {Native.LastError()}");
                return false;
            }

            if (!Native.AttachActionSets())
            {
                log.LogError($"Could not attach the action set. {Native.LastError()}");
                return false;
            }

            // Reports what was created, not what was asked for. The first version logged the
            // latter and read as a success while seven of twelve actions had been refused.
            if (created < Actions.Length)
                log.LogWarning($"{Actions.Length - created} of {Actions.Length} actions were refused.");
            log.LogInfo($"controller actions attached: {created} actions, {bindings.Count} bindings");
            return true;
        }
    }
}
