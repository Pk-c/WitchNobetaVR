using System.Collections.Generic;
using UnityEngine.XR;
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
    /// This is a transcription of what `OpenXRInput.AttachActionSets`,
    /// `OculusTouchControllerProfile` and `HTCViveControllerProfile` do in
    /// com.unity.xr.openxr@1.6.0, reduced to the actions this mod actually reads. The usage
    /// strings are the contract with the other end: each one becomes a feature on the resulting
    /// `InputDevice`, and is the exact string `TryGetFeatureValue_*` must be given to read it
    /// back.
    ///
    /// <para>
    /// Each interaction profile gets its own action set and its own pair of devices, as the
    /// package does with one feature per profile. The provider connects whichever device was
    /// registered for the profile the runtime reports as current, so a profile suggested
    /// without devices of its own would leave no controller at all.
    /// </para>
    /// </summary>
    internal static class XrActionSet
    {
        private const string LeftHand = "/user/hand/left";
        private const string RightHand = "/user/hand/right";

        /// <summary>
        /// Touch. SteamVR and other runtimes remap these bindings onto controllers that have no
        /// profile of their own here, which works for anything shaped like Touch — Index and WMR
        /// have a stick and face buttons of their own.
        /// </summary>
        private const string TouchProfile = "/interaction_profiles/oculus/touch_controller";

        /// <summary>
        /// The Vive wand, which is not shaped like Touch. Left to SteamVR's remapping, the
        /// thumbstick landed on nothing — a wand has a trackpad where Touch has a stick — and
        /// the player could not move.
        /// </summary>
        private const string ViveProfile = "/interaction_profiles/htc/vive_controller";

        /// <summary>
        /// The Steam Frame's controllers: a gamepad split in two, with A, B, X and Y all on the
        /// right and a d-pad on the left. SteamVR's remapping of Touch onto it works, but spends
        /// the right hand's X and Y on B again. The profile is behind a Valve extension, so it
        /// exists only on a runtime that grants <see cref="FrameExtension"/>; anywhere else it
        /// is skipped and the remapping carries on as before. Its paths are the ones SteamVR's
        /// runtime lists for it.
        /// </summary>
        private const string FrameProfile = "/interaction_profiles/valve/frame_controller";
        private const string FrameExtension = "XR_VALVE_frame_controller_interaction";

        // UnityEngine.XR.InputDeviceCharacteristics, as flags.
        private const uint HeldInHand = 1 << 2;
        private const uint TrackedDevice = 1 << 5;
        private const uint Controller = 1 << 6;
        private const uint Left = 1 << 8;
        private const uint Right = 1 << 9;

        /// <summary>
        /// The id the provider gave a controller when it was registered.
        ///
        /// Kept because the provider addresses its own devices by these, and they are not the
        /// same numbers the XR input subsystem hands out for the same controllers — the package
        /// asks an Input System device for its "internal device id" precisely because the two
        /// namespaces differ. Nothing in this mod goes through the Input System, so the id is
        /// kept from the one moment it is handed over. <see cref="Vr.VrHaptics"/> is what needs
        /// it: haptic output is applied to a device, and applying it to a device the provider
        /// does not recognise fails without saying so.
        ///
        /// <para>
        /// Every profile registers its own pair, so the one wanted is the one belonging to the
        /// controller actually connected, found by the name it was registered under. The other
        /// profile's haptic action has no binding on this hardware: it accepts the impulse and
        /// nothing is felt.
        /// </para>
        /// </summary>
        internal static ulong DeviceId(XRNode node, string deviceName)
        {
            var profile = Profiles[0];
            foreach (var candidate in Profiles)
            {
                if (candidate.DeviceName != deviceName) continue;
                profile = candidate;
                break;
            }
            return node == XRNode.LeftHand ? profile.LeftDeviceId : profile.RightDeviceId;
        }

        private readonly struct Action
        {
            public readonly string Name;
            public readonly Native.ActionType Type;
            public readonly string Usage;

            /// <summary>
            /// The paths on the profile, per hand. Usually one, the same on both hands; none
            /// where a hand does not have the control; several where more than one control
            /// should do the same thing, which OpenXR reads as either of them.
            /// </summary>
            public readonly string[] LeftPaths;
            public readonly string[] RightPaths;

            public Action(string name, Native.ActionType type, string usage, string path)
                : this(name, type, usage, new[] { path }, new[] { path }) { }

            public Action(string name, Native.ActionType type, string usage,
                          string leftPath, string rightPath)
                : this(name, type, usage, One(leftPath), One(rightPath)) { }

            public Action(string name, Native.ActionType type, string usage,
                          string[] leftPaths, string[] rightPaths)
            {
                Name = name; Type = type; Usage = usage;
                LeftPaths = leftPaths; RightPaths = rightPaths;
            }

            private static string[] One(string path) => path == null ? new string[0] : new[] { path };
        }

        private static readonly Action[] TouchActions =
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

            // The poses are not read yet. Declared now because motion-controlled hands will
            // need them, and adding an action later means rebuilding the whole set.
            new("devicePose",        Native.ActionType.Pose,    "Device",  "/input/grip/pose"),
            new("pointerPose",       Native.ActionType.Pose,    "Pointer", "/input/aim/pose"),

            // The output. This one is used: it is what the game's own rumble is played through
            // once VrHaptics has translated it. Its usage doubles as the name the provider's
            // action lookup answers to — see VrHaptics.HapticAction.
            new("haptic",            Native.ActionType.Vibrate, "Haptic",  "/output/haptic"),
        };

        /// <summary>
        /// The same usages on the wand, so <see cref="Input.VrInput"/> reads them without
        /// knowing which controller is held. The trackpad stands in for the stick: touched is
        /// deflected, pressed is clicked.
        ///
        /// The wand has no face buttons, so only the two the game cannot be played without get
        /// a default: the menu buttons stand in for A (jump, confirm, next line) and Y
        /// (interact, held for the pause menu). X and B are declared all the same, with no
        /// binding, so SteamVR's binding editor still lists them for players who want them
        /// somewhere.
        /// </summary>
        private static readonly Action[] ViveActions =
        {
            new("thumbstick",        Native.ActionType.Axis2D, "Primary2DAxis",      "/input/trackpad"),
            new("thumbstickClicked", Native.ActionType.Binary, "Primary2DAxisClick", "/input/trackpad/click"),
            new("trigger",           Native.ActionType.Axis1D, "Trigger",            "/input/trigger/value"),
            new("triggerPressed",    Native.ActionType.Binary, "TriggerButton",      "/input/trigger/click"),

            // The wand's squeeze is a switch, not an axis. Bound to the axis anyway, which
            // OpenXR allows and reads as 0 or 1, so VrInput's threshold works unchanged.
            new("grip",              Native.ActionType.Axis1D, "Grip",               "/input/squeeze/click"),
            new("gripPressed",       Native.ActionType.Binary, "GripButton",         "/input/squeeze/click"),

            new("primaryButton",     Native.ActionType.Binary, "PrimaryButton",   null, "/input/menu/click"),
            new("secondaryButton",   Native.ActionType.Binary, "SecondaryButton", "/input/menu/click", null),

            new("devicePose",        Native.ActionType.Pose,    "Device",  "/input/grip/pose"),
            new("pointerPose",       Native.ActionType.Pose,    "Pointer", "/input/aim/pose"),
            new("haptic",            Native.ActionType.Vibrate, "Haptic",  "/output/haptic"),
        };

        /// <summary>
        /// The same actions on the Frame. The left hand is left exactly as SteamVR's remapping
        /// of Touch had it — d-pad down is X, the other three directions are Y, and the bumpers
        /// pull the trigger — so nothing a Frame player already knows moves. What changes is
        /// the right hand's X and Y, which the remapping spent on B: they become a second use
        /// item and a second interact, carried on usages of their own that
        /// <see cref="Input.VrInput"/> folds into the left hand's buttons.
        /// </summary>
        private static readonly Action[] FrameActions =
        {
            new("thumbstick",        Native.ActionType.Axis2D, "Primary2DAxis",      "/input/thumbstick"),
            new("thumbstickClicked", Native.ActionType.Binary, "Primary2DAxisClick", "/input/thumbstick/click"),
            new("trigger",           Native.ActionType.Axis1D, "Trigger",
                new[] { "/input/trigger/value", "/input/bumper/click" },
                new[] { "/input/trigger/value", "/input/bumper/click" }),
            new("triggerPressed",    Native.ActionType.Binary, "TriggerButton",
                new[] { "/input/trigger/value", "/input/bumper/click" },
                new[] { "/input/trigger/value", "/input/bumper/click" }),
            new("grip",              Native.ActionType.Axis1D, "Grip",               "/input/squeeze/value"),
            new("gripPressed",       Native.ActionType.Binary, "GripButton",         "/input/squeeze/value"),

            new("primaryButton",     Native.ActionType.Binary, "PrimaryButton",
                new[] { "/input/dpad_down/click" },
                new[] { "/input/a/click" }),
            new("secondaryButton",   Native.ActionType.Binary, "SecondaryButton",
                new[] { "/input/dpad_up/click", "/input/dpad_left/click", "/input/dpad_right/click" },
                new[] { "/input/b/click" }),

            // The left hand's X and Y, again on the right.
            new("offhandPrimaryButton",   Native.ActionType.Binary, "OffhandPrimaryButton",   null, "/input/x/click"),
            new("offhandSecondaryButton", Native.ActionType.Binary, "OffhandSecondaryButton", null, "/input/y/click"),

            // Where the remapping put Touch's left menu button.
            new("menu",              Native.ActionType.Binary, "MenuButton",      "/input/view/click", null),

            new("devicePose",        Native.ActionType.Pose,    "Device",  "/input/grip/pose"),
            new("pointerPose",       Native.ActionType.Pose,    "Pointer", "/input/aim/pose"),
            new("haptic",            Native.ActionType.Vibrate, "Haptic",  "/output/haptic"),
        };

        private sealed class Profile
        {
            public readonly string Path;
            public readonly string SetName;
            public readonly string SetLabel;
            public readonly string DeviceName;
            public readonly string Manufacturer;
            public readonly Action[] Actions;

            /// <summary>The extension the profile belongs to, or null for a core profile.</summary>
            public readonly string Extension;

            public ulong LeftDeviceId;
            public ulong RightDeviceId;

            public Profile(string path, string setName, string setLabel,
                           string deviceName, string manufacturer, Action[] actions,
                           string extension = null)
            {
                Path = path; SetName = setName; SetLabel = setLabel;
                DeviceName = deviceName; Manufacturer = manufacturer; Actions = actions;
                Extension = extension;
            }

            /// <summary>
            /// Whether the runtime has this profile at all. Suggesting a profile whose extension
            /// was not granted is refused outright, so it is not tried.
            /// </summary>
            public bool Available => Extension == null || Native.IsExtensionEnabled(Extension);
        }

        /// <summary>
        /// Touch first, because it is the fallback for a device name no profile claims. Its set
        /// keeps the name it always had, so bindings players already saved in SteamVR survive.
        /// </summary>
        private static readonly Profile[] Profiles =
        {
            new(TouchProfile, "nobetavr", "NobetaVR",
                "Oculus Touch Controller OpenXR", "Oculus", TouchActions),
            new(ViveProfile, "nobetavr-vive", "NobetaVR (Vive)",
                "HTC Vive Controller OpenXR", "HTC", ViveActions),
            new(FrameProfile, "nobetavr-frame", "NobetaVR (Steam Frame)",
                "Steam Frame Controller OpenXR", "Valve", FrameActions, FrameExtension),
        };

        /// <summary>
        /// Asks for the extensions the profiles need. Called before the instance is created,
        /// which is the only time a request can still be granted.
        /// </summary>
        public static void RequestExtensions()
        {
            foreach (var profile in Profiles)
            {
                if (profile.Extension == null) continue;
                if (!Native.RequestEnableExtensionString(profile.Extension))
                    Plugin.Log.LogInfo($"{profile.Extension} is not offered by this runtime; "
                                     + $"{profile.SetLabel} bindings will not be suggested.");
            }
        }

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
        /// Builds and commits the action sets. Called once per session, after xrBeginSession and
        /// before the input subsystem starts — the order the package uses, and the order the
        /// runtime requires: actions cannot be attached to a session that has not begun, and
        /// the input subsystem has nothing to enumerate until they are.
        /// </summary>
        public static bool Attach()
        {
            var log = Plugin.Log;

            var available = new List<Profile>();
            foreach (var profile in Profiles)
                if (profile.Available) available.Add(profile);

            // Every device before any action set, as the package does.
            foreach (var profile in available)
            {
                profile.LeftDeviceId = Native.RegisterDeviceDefinition(LeftHand, profile.Path,
                    HeldInHand | TrackedDevice | Controller | Left,
                    profile.DeviceName, profile.Manufacturer, "");
                profile.RightDeviceId = Native.RegisterDeviceDefinition(RightHand, profile.Path,
                    HeldInHand | TrackedDevice | Controller | Right,
                    profile.DeviceName, profile.Manufacturer, "");

                if (profile.LeftDeviceId == 0 || profile.RightDeviceId == 0)
                {
                    log.LogError($"Could not register the {profile.DeviceName} devices. {Native.LastError()}");
                    return false;
                }
            }

            // One profile refused is one family of controllers left to the runtime's remapping;
            // every profile refused is no controllers at all, and attaching would only hide it.
            var suggested = 0;
            foreach (var profile in available)
                if (Suggest(profile)) suggested++;
            if (suggested == 0) return false;

            if (!Native.AttachActionSets())
            {
                log.LogError($"Could not attach the action sets. {Native.LastError()}");
                return false;
            }

            foreach (var profile in available)
                log.LogInfo($"provider device ids for '{profile.DeviceName}': "
                          + $"left={profile.LeftDeviceId} right={profile.RightDeviceId}");
            return true;
        }

        /// <summary>Creates one profile's action set and suggests its bindings.</summary>
        private static bool Suggest(Profile profile)
        {
            var log = Plugin.Log;

            var guid = default(Native.SerializedGuid);
            var actionSet = Native.CreateActionSet(SanitizePath(profile.SetName), profile.SetLabel, guid);
            if (actionSet == 0)
            {
                log.LogError($"Could not create the {profile.SetLabel} action set. {Native.LastError()}");
                return false;
            }

            var bindings = new List<Native.SerializedBinding>();
            var hands = new[] { LeftHand, RightHand };
            var created = 0;

            foreach (var action in profile.Actions)
            {
                var actionId = Native.CreateAction(
                    actionSet, SanitizePath(action.Name), action.Name, (uint)action.Type, guid,
                    hands, (uint)hands.Length,
                    new[] { action.Usage }, 1);

                if (actionId == 0)
                {
                    log.LogWarning($"{profile.SetLabel}: action '{action.Name}' was refused. {Native.LastError()}");
                    continue;
                }

                created++;

                foreach (var hand in hands)
                    foreach (var path in hand == LeftHand ? action.LeftPaths : action.RightPaths)
                        bindings.Add(new Native.SerializedBinding { ActionId = actionId, Path = hand + path });
            }

            if (!Native.SuggestBindings(profile.Path, bindings.ToArray(), (uint)bindings.Count))
            {
                log.LogError($"The runtime refused the {profile.SetLabel} bindings. {Native.LastError()}");
                return false;
            }

            // Reports what was created, not what was asked for. The first version logged the
            // latter and read as a success while seven of twelve actions had been refused.
            if (created < profile.Actions.Length)
                log.LogWarning($"{profile.SetLabel}: {profile.Actions.Length - created} of "
                             + $"{profile.Actions.Length} actions were refused.");
            log.LogInfo($"{profile.SetLabel} actions attached: {created} actions, {bindings.Count} bindings");
            return true;
        }
    }
}
