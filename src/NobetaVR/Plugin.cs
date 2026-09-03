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
        internal ConfigEntry<bool> HideHead;
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
        internal ConfigEntry<bool> RoomScale;
        internal ConfigEntry<float> RoomScaleMaxStep;
        internal ConfigEntry<float> NeckModelDown;
        internal ConfigEntry<float> NeckModelBack;
        internal ConfigEntry<bool> BodyFollowsView;
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

            HideHead = Config.Bind(
                "Camera", "HideHead", true,
                "Scales the head bone to nothing so you are not inside Nobeta's skull. The hair "
              + "is parented to that bone and goes with it. Turn off to see her head from the "
              + "outside while adjusting the eye offsets.");

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
                "Controls", "SmoothTurnSpeed", 120f,
                "Degrees per second when SmoothTurn is on.");

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
        }
    }
}
