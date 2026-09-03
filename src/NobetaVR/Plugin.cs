using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using BepInEx.Logging;
using Il2CppInterop.Runtime.Injection;
using NobetaVR.Diagnostics;
using UnityEngine;

namespace NobetaVR
{
    [BepInPlugin(Guid, "NobetaVR", Version)]
    [BepInProcess("LittleWitchNobeta.exe")]
    public sealed class Plugin : BasePlugin
    {
        public const string Guid = "fr.chromatic.nobetavr";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log { get; private set; }
        internal static Plugin Instance { get; private set; }

        internal ConfigEntry<bool> Enabled;
        internal ConfigEntry<bool> VerboseStartupReport;
        internal ConfigEntry<string> OpenXrRuntimeJson;
        internal ConfigEntry<bool> ApplyHeadPose;
        internal ConfigEntry<float> HeadPoseLogSeconds;
        internal ConfigEntry<bool> FirstPerson;
        internal ConfigEntry<float> EyeOffsetForward;
        internal ConfigEntry<float> EyeOffsetUp;
        internal ConfigEntry<float> HeadHideDistance;
        internal ConfigEntry<bool> HeadBobbing;
        internal ConfigEntry<float> HeadOffsetX;
        internal ConfigEntry<float> HeadOffsetY;
        internal ConfigEntry<float> HeadOffsetZ;
        internal ConfigEntry<float> MenuDistance;
        internal ConfigEntry<float> MenuDeadzone;
        internal ConfigEntry<string> HeadBoneName;
        internal ConfigEntry<bool> YawFromGameCamera;
        internal ConfigEntry<bool> DisableRespiration;
        internal ConfigEntry<bool> DisableCameraShake;
        internal ConfigEntry<float> ViewYawOffset;
        internal ConfigEntry<float> MoveDeadzone;
        internal ConfigEntry<float> TurnDeadzone;
        internal ConfigEntry<float> SnapTurnDegrees;
        internal ConfigEntry<bool> SmoothTurn;
        internal ConfigEntry<float> SmoothTurnSpeed;
        internal ConfigEntry<float> PauseHoldSeconds;
        internal ConfigEntry<float> RecentreGripWindow;
        internal ConfigEntry<bool> ItemCycleForward;
        internal ConfigEntry<bool> RoomScale;
        internal ConfigEntry<float> RoomScaleMaxStep;
        internal ConfigEntry<float> NeckModelDown;
        internal ConfigEntry<float> NeckModelBack;
        internal ConfigEntry<bool> BodyFollowsView;
        internal ConfigEntry<bool> HandTracking;
        internal ConfigEntry<bool> DetachedHands;
        internal ConfigEntry<float> HandVertexWeight;
        internal ConfigEntry<bool> CapWristHole;
        internal ConfigEntry<bool> CarryHandAttachments;
        internal ConfigEntry<float> HandReachScale;
        internal ConfigEntry<float> HandOffsetSide;
        internal ConfigEntry<float> HandOffsetUp;
        internal ConfigEntry<float> HandOffsetForward;
        internal ConfigEntry<float> HandRotationPitch;
        internal ConfigEntry<float> HandRotationYaw;
        internal ConfigEntry<float> HandRotationRoll;
        internal ConfigEntry<bool> HandFollowRotation;
        internal ConfigEntry<float> ForearmTwistShare;
        internal ConfigEntry<bool> HandDiagnostics;
        internal ConfigEntry<bool> DisableGameAimIk;
        internal ConfigEntry<bool> StopFinalIkFixTransforms;
        internal ConfigEntry<bool> AimFromView;
        internal ConfigEntry<bool> AimFromHand;
        internal ConfigEntry<float> AimPitchOffset;
        internal ConfigEntry<float> AimDistance;
        internal ConfigEntry<bool> HudEnabled;
        internal ConfigEntry<float> HudDistance;
        internal ConfigEntry<float> HudSize;
        internal ConfigEntry<float> HudHeightOffset;
        internal ConfigEntry<float> HudFollowSpeed;
        internal ConfigEntry<int> HudResolutionWidth;
        internal ConfigEntry<int> HudResolutionHeight;
        internal ConfigEntry<bool> AlignViewToBodyOnSpawn;

        public override void Load()
        {
            Instance = this;
            Log = base.Log;

            Enabled = Config.Bind(
                "General", "Enabled", true,
                "Turns the whole mod off without uninstalling it. The plugin still loads, so "
              + "this file stays readable and writable, but nothing touches the game.");

            VerboseStartupReport = Config.Bind(
                "Diagnostics", "VerboseStartupReport", true,
                "Writes the XR and graphics state of the running game to the BepInEx log at "
              + "startup. It is the first thing to read when the headset stays black, so it is "
              + "on by default; it costs one screenful of log, once.");

            OpenXrRuntimeJson = Config.Bind(
                "XR", "OpenXrRuntimeJson", "",
                "Full path to an OpenXR runtime manifest, to force which runtime the game uses. "
              + "Leave empty to use whatever the system is set to. Worth setting on a machine "
              + "with several runtimes installed: Virtual Desktop and the Oculus app both tend "
              + "to claim the system-wide setting, so a game can come up somewhere you did not "
              + "expect with nothing to say why. SteamVR's manifest is usually "
              + "steamapps\\common\\SteamVR\\steamxr_win64.json. This applies to this game only; "
              + "the machine-wide setting is never modified.");

            ApplyHeadPose = Config.Bind(
                "Camera", "ApplyHeadPose", true,
                "Moves the game's camera with your head. Starting XR gets stereo into the headset "
              + "but nothing tracks the head: this game shipped without XR and so without a "
              + "TrackedPoseDriver, which is what would normally do it. Turn this off to see the "
              + "game's own camera framing untouched, in stereo, with the world locked to your head.");

            HeadPoseLogSeconds = Config.Bind(
                "Diagnostics", "HeadPoseLogSeconds", 0f,
                "Writes the head pose to the log this often, in seconds. Zero turns it off. "
              + "Set it to 1 when the view is not following your head and you need to know "
              + "whether the pose is arriving at all or arriving and being ignored.");

            FirstPerson = Config.Bind(
                "Camera", "FirstPerson", true,
                "Puts the view on Nobeta's head bone instead of at the end of the game's camera "
              + "boom. The game's camera keeps running either way — its yaw is still your look "
              + "direction, and cutscenes, aiming and lock-on are untouched.");

            EyeOffsetForward = Config.Bind(
                "Camera", "EyeOffsetForward", 0.10f,
                "How far in front of the head bone the eyes sit, in metres. The bone is at the "
              + "base of the skull on most rigs, so this moves the view forward to roughly where "
              + "eyes are. Raise it if you can see the inside of her face, lower it if the view "
              + "feels detached from the body.");

            EyeOffsetUp = Config.Bind(
                "Camera", "EyeOffsetUp", 0.05f,
                "How far above the head bone the eyes sit, in metres. Together with "
              + "EyeOffsetForward this is the one thing worth tuning by eye — it is model "
              + "geometry, not preference.");

            HandTracking = Config.Bind(
                "Hands", "HandTracking", true,
                "Puts Nobeta's hands where your controllers are, bending her arms to follow. The "
              + "game ships no limb IK — only an aim solver, a look-at and one for her hair — so "
              + "the mod solves the arms itself and corrects the animated pose rather than "
              + "replacing it.");

            DetachedHands = Config.Bind("Hands", "DetachedHands", true,
                "Shows the hands alone, exactly where your controllers are, with the arms hidden. "
              + "This rig fights arm IK on every front — one forearm bone to spread a wrist roll "
              + "over, wrist vertices shared with a sleeve, and a dynamic-bone cape sharing "
              + "LateUpdate — for a pair of arms you can barely see in first person anyway. With "
              + "them gone there is no reach to run out of and nothing to tune: your hand is "
              + "where your hand is. Turn this off to drive her real arms with IK instead.");

            CapWristHole = Config.Bind("Hands", "CapWristHole", true,
                "Closes the opening the cut leaves at the wrist. Without it the hand is an "
              + "open shell and you can see its inside, since the mesh has no back faces. "
              + "The rim is found from the geometry — after a cut, an edge belonging to only "
              + "one triangle is by definition on the boundary — and filled with a fan.");

            HandVertexWeight = Config.Bind("Hands", "HandVertexWeight", 0.5f,
                "How much of a vertex must belong to the hand bone for it to be cut out with the "
              + "hand, from 0 to 1. Lower takes more of the wrist and risks a ragged edge where "
              + "the sleeve was; higher gives a cleaner cut and a shorter hand.");

            CarryHandAttachments = Config.Bind("Hands", "CarryHandAttachments", true,
                "Moves what is parented to the hand — the wand — onto the detached hand, so it "
              + "goes where your hand goes. Without it the wand stays on the real hand, which is "
              + "collapsed into the shoulder, so it never appears at all. Only things that draw "
              + "something are moved: finger bones live there too, and taking those out of the "
              + "skeleton deforms the character's own hand.");

            HandReachScale = Config.Bind("Hands", "HandReachScale", 0.2f,
                "How much of your reach maps onto Nobeta's. She is a child and you are not: her "
              + "arms span perhaps half of yours, so at 1.0 most of your range asks for a hand "
              + "further than she can put one, and the arm locks out straight. Lower it if her "
              + "arms still snap straight at the edges of your reach, raise it if her hands feel "
              + "like they lag behind yours.");

            HandOffsetSide = Config.Bind("Hands", "HandOffsetSide", 0f,
                "Sideways offset from the controller to the hand bone, in metres. Mirrored "
              + "between hands, so one value serves both.");
            HandOffsetUp = Config.Bind("Hands", "HandOffsetUp", 0f,
                "Vertical offset from the controller to the hand bone, in metres.");
            HandOffsetForward = Config.Bind("Hands", "HandOffsetForward", 0f,
                "Forward offset from the controller to the hand bone, in metres, in the "
              + "controller's own frame. Negative by "
              + "default because a controller is gripped in the palm while the bone sits at the "
              + "wrist, a little behind it.");
            HandRotationPitch = Config.Bind("Hands", "HandRotationPitch", 0f,
                "Wrist pitch adjustment, in degrees. Zero by default: the difference between how "
              + "a controller is held and how the hand bone is oriented is taken from the rig "
              + "itself, so an identity controller rotation reproduces the pose the animator "
              + "authored. These three are for taste, not for correcting the rig.");
            ForearmTwistShare = Config.Bind("Hands", "ForearmTwistShare", 0f,
                "How much of the wrist's roll the forearm takes, from 0 to 1. A real forearm "
              + "carries pronation along its whole length, so turning a palm over rotates the arm "
              + "from the elbow down; a rig with a single forearm bone has nowhere to put that, "
              + "and leaving it all on the hand shears the wrist. Raise it if the wrist still "
              + "looks wrung, lower it if the elbow rolls when only the hand should.");

            HandFollowRotation = Config.Bind("Hands", "HandFollowRotation", true,
                "Lets the controller twist the wrist. Turn it off if the forearm wrings: its "
              + "vertices are "
              + "weighted partly to the hand bone, so a large wrist angle can wring it, and from "
              + "inside a headset that looks exactly like the arm itself being broken. With it "
              + "off the hand keeps the animated relationship to the forearm, which is always "
              + "anatomically right and is the way to judge the arm on its own.");

            StopFinalIkFixTransforms = Config.Bind("Hands", "StopFinalIkFixTransforms", true,
                "Stops FinalIK restoring the animated pose over the mod's arm solve. Its solvers "
              + "rewind every bone they manage at the start of their own update — restoration, "
              + "not solving, so it happens even at weight zero — and they update in LateUpdate "
              + "exactly as this mod does, in an order Unity does not define. The arm was "
              + "therefore erased on some frames and not others, which is the flicker, and a limb "
              + "snapping between two poses is what shook the cape.");

            DisableGameAimIk = Config.Bind("Hands", "DisableGameAimIk", true,
                "Stands the game's own aim IK down while hand tracking is driving the arms. That "
              + "solver swings the upper body to point the wand, its chain runs through the "
              + "spine, and it updates in LateUpdate exactly as this mod does — with no ordering "
              + "guarantee between them, so the arm flicked between the two poses frame by frame "
              + "and the cape was dragged along by the spine. Aiming is not lost: it comes from "
              + "the view instead.");

            HandDiagnostics = Config.Bind("Hands", "HandDiagnostics", false,
                "Writes the arm's bone lengths, the distance being asked of it, and the bone "
              + "scales to the log once a second. Only useful with the IK arms; those numbers "
              + "say whether a bad-looking arm is out of reach or sheared by a non-uniform "
              + "scale, which looking at it cannot.");

            AimFromView = Config.Bind("Aim", "AimFromView", true,
                "Puts the game's aim target on the line you are looking down. On a monitor that "
              + "line comes from the third-person camera and a reticle painted over the world; "
              + "in the headset the camera has moved into Nobeta's head and the reticle is on a "
              + "floating panel, so the shot goes somewhere defensible with nothing to say "
              + "where. Aiming later moves to the wand hand.");

            AimFromHand = Config.Bind("Aim", "AimFromHand", true,
                "Aims along the wand hand instead of along your gaze. Pointing a wand is the more "
              + "natural of the two once the hand is tracked, and it is the only one that lets you "
              + "aim somewhere you are not looking. The view stays the fallback whenever the hands "
              + "are not being drawn — menus, cutscenes, hand tracking turned off.");

            AimPitchOffset = Config.Bind("Aim", "AimPitchOffset", 25f,
                "Angle between the controller and where the wand points, in degrees. A Touch "
              + "controller is gripped at an angle rather than in line with what it is aiming, so "
              + "its own forward points somewhat below the wand.");

            AimDistance = Config.Bind("Aim", "AimDistance", 15f,
                "How far down the view the aim target sits when nothing is in the way, in "
              + "metres. When something is, the target lands on it instead.");

            HandRotationYaw = Config.Bind("Hands", "HandRotationYaw", 0f,
                "Wrist yaw adjustment, in degrees.");
            HandRotationRoll = Config.Bind("Hands", "HandRotationRoll", -50f,
                "Wrist roll adjustment, in degrees. Not zero, because the rest orientation "
              + "taken from the rig only accounts for how the hand bone sits on the body — "
              + "not for how a Touch controller is gripped, which is turned about its own "
              + "axis relative to what it points at. This is that difference, measured.");

            HeadHideDistance = Config.Bind(
                "Camera", "HeadHideDistance", 0.35f,
                "How close the camera has to get to the head bone, in metres, before her head is "
              + "hidden. The head is only a problem while your eyes are inside the mesh, and is "
              + "wanted the rest of the time — when a cutscene pulls back, or an animation "
              + "carries her head away from the view. Raise it if you catch sight of the inside "
              + "of her face; lower it if her head vanishes when it should not.");

            HeadBobbing = Config.Bind(
                "Camera", "HeadBobbing", false,
                "Lets the walk animation move your viewpoint, because the view rides her head "
              + "bone. It is the difference between inhabiting her and floating behind her eyes; "
              + "it is also the first thing to turn off if walking makes you queasy.");

            HeadOffsetX = Config.Bind("Camera", "HeadOffsetX", 0f,
                "Your own adjustment to the eye position, sideways, in metres. Separate from the "
              + "eye offsets above, which are the model's geometry rather than your preference.");
            HeadOffsetY = Config.Bind("Camera", "HeadOffsetY", 0.23f, "Your own adjustment, up, in metres.");
            HeadOffsetZ = Config.Bind("Camera", "HeadOffsetZ", 0f, "Your own adjustment, forward, in metres.");

            MenuDistance = Config.Bind("Interface", "MenuDistance", 1.2f,
                "How far in front of you the mod's own settings panel sits, in metres.");

            MenuDeadzone = Config.Bind("Interface", "MenuDeadzone", 0.5f,
                "How far the stick must move to step through a menu.");

            HeadBoneName = Config.Bind(
                "Camera", "HeadBoneName", "",
                "Name of the bone the view sits on. Leave empty to let the mod pick it out of "
              + "the skeleton, which it does by preferring a bone called exactly \"Head\" and "
              + "rejecting look-at helpers such as HeadDirect. Set it explicitly if a costume "
              + "or a game update names things differently; the candidates it found are listed "
              + "in the log.");

            YawFromGameCamera = Config.Bind(
                "Camera", "YawFromGameCamera", true,
                "Takes the look direction's yaw from the game's camera, leaving pitch and roll "
              + "to your neck. Turning this off pins the view to world north and makes stick "
              + "turning do nothing, which is only useful for diagnosis.");

            DisableRespiration = Config.Bind(
                "Comfort", "DisableRespiration", true,
                "Switches off the camera's breathing sway. Pleasant on a monitor; in a headset "
              + "it moves the horizon under you.");

            DisableCameraShake = Config.Bind(
                "Comfort", "DisableCameraShake", true,
                "Switches off combat camera shake, for the same reason.");

            ViewYawOffset = Config.Bind(
                "Camera", "ViewYawOffset", 0f,
                "Degrees added to the view's yaw. Set it to 180 if you find yourself looking "
              + "back at Nobeta rather than out through her eyes. It exists because which way a "
              + "rig's bones and camera point is a fact about this game's assets, not something "
              + "that can be derived.");

            MoveDeadzone = Config.Bind(
                "Controls", "MoveDeadzone", 0.15f,
                "How far the left stick must move before Nobeta does. Travel past this point is "
              + "rescaled to the full range, so the first millimetre is not a full step.");

            TurnDeadzone = Config.Bind(
                "Controls", "TurnDeadzone", 0.7f,
                "How far the right stick must go to turn. High on purpose for snap turning: the "
              + "stick must also fall back below this to re-arm the next step, which is what "
              + "makes holding it over give one turn instead of a spin.");

            SnapTurnDegrees = Config.Bind(
                "Controls", "SnapTurnDegrees", 45f,
                "Degrees per snap turn step.");

            SmoothTurn = Config.Bind(
                "Controls", "SmoothTurn", false,
                "Sweep the view continuously instead of snapping. More natural, and harder on "
              + "the stomach.");

            SmoothTurnSpeed = Config.Bind(
                "Controls", "SmoothTurnSpeed", 130f,
                "Degrees per second when SmoothTurn is on.");

            PauseHoldSeconds = Config.Bind(
                "Controls", "PauseHoldSeconds", 2f,
                "How long Y must be held to open the game's pause menu, in seconds. Y is also "
              + "interact, which is why interact fires when you let go rather than when you "
              + "press: until the button comes back up there is no telling which of the two you "
              + "meant.");

            RecentreGripWindow = Config.Bind(
                "Controls", "RecentreGripWindow", 0.2f,
                "How close together the two grips must be squeezed to count as recentring "
              + "rather than as focus and item cycling, in seconds. Squeezing the second grip "
              + "later than this is taken at face value, so reaching for an item while holding "
              + "focus does not put your head back on Nobeta.");

            ItemCycleForward = Config.Bind(
                "Controls", "ItemCycleForward", true,
                "Which way the left grip steps through the item bar. On is the direction the "
              + "game calls rightward; turn it off to walk the bar the other way.");

            AlignViewToBodyOnSpawn = Config.Bind(
                "Camera", "AlignViewToBodyOnSpawn", true,
                "Points the view where Nobeta is facing when a stage opens. The game frames a "
              + "new stage as it likes and does not always park the camera behind her, which on "
              + "a monitor is a camera angle and in a headset means starting the level facing "
              + "backwards. With the body following the view this cannot correct itself — she "
              + "would just turn to match the wrong direction.");

            NeckModelDown = Config.Bind(
                "Room scale", "NeckModelDown", 0.12f,
                "How far below your eyes your neck pivots, in metres. You do not turn about your "
              + "eyes, and without this the headset sweeps a circle every time you turn on the "
              + "spot — which room-scale cannot distinguish from walking, so it leads the "
              + "character around that circle. The error closes on a full turn, which is the "
              + "tell. Set both neck values to zero to track the headset itself.");

            NeckModelBack = Config.Bind(
                "Room scale", "NeckModelBack", 0.08f,
                "How far behind your eyes your neck pivots, in metres. See NeckModelDown.");

            BodyFollowsView = Config.Bind(
                "Camera", "BodyFollowsView", true,
                "Keeps Nobeta facing where you are looking. This is what turns a sideways push "
              + "of the stick into a side step: the game moves her relative to the camera and "
              + "then turns her to face where she is travelling, so without this, pushing left "
              + "makes her turn left and walk off instead of stepping aside. It goes through the "
              + "game's own LookAt, so she turns with her usual smoothing, and it stands aside "
              + "during cutscenes, death and the face-camera mode.");

            RoomScale = Config.Bind(
                "Room scale", "RoomScale", true,
                "Walks Nobeta to wherever you physically walk. The step goes through the game's "
              + "own CharacterController, so walls and slopes are resolved exactly as they are "
              + "for stick movement, and the view stops with her rather than passing through "
              + "geometry. Right stick click puts your head back on her.");

            RoomScaleMaxStep = Config.Bind(
                "Room scale", "RoomScaleMaxStep", 0.5f,
                "Largest physical step accepted in one frame, in metres. A tracking glitch can "
              + "otherwise teleport the character across the level; anything larger is treated "
              + "as a bad sample and absorbed rather than walked.");

            HudEnabled = Config.Bind(
                "Interface", "HudEnabled", true,
                "Shows the game's interface on a panel in front of you. The interface is "
              + "captured rather than rebuilt: screen-space canvases are drawn through a camera "
              + "of ours into a texture, so Unity keeps control of the layout and the game's own "
              + "HUD animations keep working. Without this the interface is invisible in the "
              + "headset — screen-space overlay draws straight to the display, and neither eye "
              + "renders it.");

            HudDistance = Config.Bind(
                "Interface", "HudDistance", 1.6f,
                "How far in front of you the panel sits, in metres.");

            HudSize = Config.Bind(
                "Interface", "HudSize", 1.8f,
                "Panel width in metres. Height follows from the capture aspect ratio.");

            HudHeightOffset = Config.Bind(
                "Interface", "HudHeightOffset", 0f,
                "Raises or lowers the panel relative to eye level, in metres.");

            HudFollowSpeed = Config.Bind(
                "Interface", "HudFollowSpeed", 6f,
                "How quickly the panel catches up with your head. The lag is deliberate: a panel "
              + "welded to the head is hard to read and makes the world feel strapped to your "
              + "face. Higher is tighter; very high is uncomfortable.");

            HudResolutionWidth = Config.Bind(
                "Interface", "HudResolutionWidth", 1920,
                "Width of the texture the interface is captured into.");

            HudResolutionHeight = Config.Bind(
                "Interface", "HudResolutionHeight", 1080,
                "Height of the texture the interface is captured into.");

            Log.LogInfo($"NobetaVR {Version} loading");

            if (!Enabled.Value)
            {
                Log.LogWarning("Disabled by configuration; standing down.");
                return;
            }

            // The engine is not up yet at Load() time: no graphics device, no scene, and
            // SystemInfo lies. Everything that needs a running frame goes in a component.
            ClassInjector.RegisterTypeInIl2Cpp<VrRuntime>();
            ClassInjector.RegisterTypeInIl2Cpp<NobetaVR.Vr.VrCamera>();
            ClassInjector.RegisterTypeInIl2Cpp<NobetaVR.Input.VrControls>();
            ClassInjector.RegisterTypeInIl2Cpp<NobetaVR.Ui.HudPanel>();
            ClassInjector.RegisterTypeInIl2Cpp<NobetaVR.Ui.VrMenu>();
            ClassInjector.RegisterTypeInIl2Cpp<NobetaVR.Vr.VrHands>();
            // Reported rather than assumed: a patch that silently fails to apply would look
            // exactly like the bug it was written to fix.
            try
            {
                var harmony = new Harmony(Guid);
                harmony.PatchAll();
                var patched = 0;
                foreach (var m in harmony.GetPatchedMethods()) { Log.LogInfo($"patched {m.DeclaringType?.Name}.{m.Name}"); patched++; }
                if (patched == 0) Log.LogWarning("Harmony applied no patches; the camera will fight the game.");
            }
            catch (Exception e)
            {
                Log.LogError($"Harmony patching failed: {e}");
            }

            var host = new GameObject("NobetaVR");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<VrRuntime>();
            host.AddComponent<NobetaVR.Input.VrControls>();
            host.AddComponent<NobetaVR.Ui.HudPanel>();
            host.AddComponent<NobetaVR.Ui.VrMenu>();
            host.AddComponent<NobetaVR.Vr.VrHands>();
        }
    }
}
