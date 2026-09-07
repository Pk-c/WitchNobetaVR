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
        internal ConfigEntry<float> HeadHideDistance;
        internal ConfigEntry<bool> HeadBobbing;
        internal ConfigEntry<float> HeadOffsetX;
        internal ConfigEntry<float> HeadOffsetY;
        internal ConfigEntry<float> HeadOffsetZ;
        internal ConfigEntry<float> MenuDistance;
        internal ConfigEntry<float> MenuDeadzone;
        internal ConfigEntry<string> HeadBoneName;
        internal ConfigEntry<bool> DisableRespiration;
        internal ConfigEntry<bool> DisableCameraShake;
        internal ConfigEntry<bool> ThirdPersonInCutscenes;
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
        internal ConfigEntry<bool> HoldWandStill;
        internal ConfigEntry<float> WandFollowSpeed;
        internal ConfigEntry<float> HandSteadiness;
        internal ConfigEntry<float> HandSteadinessResponse;
        internal ConfigEntry<float> HandRotationPitch;
        internal ConfigEntry<float> HandRotationYaw;
        internal ConfigEntry<float> HandRotationRoll;
        internal ConfigEntry<bool> AimFromView;
        internal ConfigEntry<bool> AimFromHand;
        internal ConfigEntry<float> AimPitchOffset;
        internal ConfigEntry<float> AimYawOffset;
        internal ConfigEntry<float> AimRollOffset;
        internal ConfigEntry<float> AimDistance;
        internal ConfigEntry<bool> ShowAimReticle;
        internal ConfigEntry<float> AimReticleSize;
        internal ConfigEntry<bool> HideGameCrosshair;
        internal ConfigEntry<bool> HudEnabled;
        internal ConfigEntry<float> HudDistance;
        internal ConfigEntry<float> HudSize;
        internal ConfigEntry<float> HudHeightOffset;
        internal ConfigEntry<float> HudFollowSpeed;
        internal ConfigEntry<bool> HudDrawOnTop;
        internal ConfigEntry<float> HudFadeSpeed;
        internal ConfigEntry<bool> VrFade;
        internal ConfigEntry<bool> TidyGameHud;
        internal ConfigEntry<bool> HideHealthBars;
        internal ConfigEntry<bool> HideChargeBar;
        internal ConfigEntry<bool> HideSoulCounter;
        internal ConfigEntry<bool> HideItemBar;
        internal ConfigEntry<bool> HideCutsceneBars;
        internal ConfigEntry<float> MoneyShowSeconds;
        internal ConfigEntry<float> ItemBarShowSeconds;
        internal ConfigEntry<bool> WristGauges;
        internal ConfigEntry<float> WristGaugeRadius;
        internal ConfigEntry<float> WristGaugeThickness;
        internal ConfigEntry<float> WristGaugeSpacing;
        internal ConfigEntry<float> WristGaugeArc;
        internal ConfigEntry<float> WristGaugeGlow;
        internal ConfigEntry<float> WristGaugeOpacity;
        internal ConfigEntry<float> WristGaugeFillSpeed;
        internal ConfigEntry<float> WristGaugeOffsetX;
        internal ConfigEntry<float> WristGaugeOffsetY;
        internal ConfigEntry<float> WristGaugeOffsetZ;
        internal ConfigEntry<float> WristGaugePitch;
        internal ConfigEntry<float> WristGaugeYaw;
        internal ConfigEntry<float> WristGaugeRoll;
        internal ConfigEntry<bool> AlignViewToBodyOnSpawn;
        internal ConfigEntry<bool> Melee;
        internal ConfigEntry<float> MeleeSpeed;
        internal ConfigEntry<float> MeleeDistance;
        internal ConfigEntry<float> MeleeReleaseSpeed;
        internal ConfigEntry<float> MeleeCooldown;
        internal ConfigEntry<float> MeleeHitboxReach;
        internal ConfigEntry<bool> MeleeShowHitbox;
        internal ConfigEntry<bool> MeleeFreeSwingOnGround;
        internal ConfigEntry<float> MeleeHitboxSize;
        internal ConfigEntry<float> MeleeTrailSeconds;
        internal ConfigEntry<bool> MeleeSwingVoice;
        internal ConfigEntry<string> MeleeRangeName;
        internal ConfigEntry<bool> Haptics;
        internal ConfigEntry<NobetaVR.Vr.HapticsHands> HapticsHand;
        internal ConfigEntry<float> HapticsStrength;
        internal ConfigEntry<float> HapticsMinAmplitude;
        internal ConfigEntry<float> HapticsMaxSeconds;
        internal ConfigEntry<float> HapticsFrequency;
        internal ConfigEntry<bool> DodgeAlwaysBackstep;
        internal ConfigEntry<bool> ThirdPersonOnDeath;
        internal ConfigEntry<float> DeathViewRise;

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









            HoldWandStill = Config.Bind("Hands", "HoldWandStill", true,
                "Steadies the wand in her hand. It is a bone of the rig rather than a prop "
              + "hanging off one, so the animator kicks it on every shot. On a monitor that "
              + "recoil is a flourish behind a crosshair that does not move; in a headset "
              + "the wand is the sight, and a sight that swings out from under your hand "
              + "each time you fire and settles somewhere new makes the next shot "
              + "guesswork. Nothing is lost by steadying it: the shot comes from the "
              + "controller, never from the wand's transform, so this only stops the "
              + "picture disagreeing with where the shot was always going.");

            WandFollowSpeed = Config.Bind("Hands", "WandFollowSpeed", 3f,
                "How quickly the steadied wand catches up with the pose the animation is "
              + "asking for, per second. Slow on purpose, and a follow rather than a pin: "
              + "pinning it to one frame's pose put the wand somewhere it had never been, "
              + "because the animator goes on writing that bone and no single frame of it "
              + "is the socket. Following slowly always arrives where the game wants the "
              + "wand, and a recoil is long over before it gets there. Raise it if the wand "
              + "lags behind a deliberate change of pose; lower it if a shot still throws "
              + "your aim off.");

            HandSteadiness = Config.Bind("Hands", "HandSteadiness", 0.5f,
                "How much tremor is taken out of the controllers, from 0 to 1. Zero is "
              + "off. The hands never looked like they were shaking; the reticle did, and "
              + "that is the same tremor seen through a lever — a tenth of a degree at "
              + "the controller is nothing on a hand and four centimetres at fifteen "
              + "metres. Raise it if the mark still crawls while you hold still; lower it "
              + "if precise aiming starts to feel sticky.");

            HandSteadinessResponse = Config.Bind("Hands", "HandSteadinessResponse", 1f,
                "How readily the steadying lets go when your hand actually moves. This is "
              + "the part that keeps the filter from being felt: the cutoff rides the speed "
              + "of the hand, so lag is only ever spent while nothing is happening to be "
              + "late for. One is the tuned value. Below it the hands begin to swim behind "
              + "you; above it the tremor comes back with any movement at all.");

            HandRotationPitch = Config.Bind("Hands", "HandRotationPitch", 0f,
                "Wrist pitch adjustment, in degrees. Zero by default: the difference between how "
              + "a controller is held and how the hand bone is oriented is taken from the rig "
              + "itself, so an identity controller rotation reproduces the pose the animator "
              + "authored. These three are for taste, not for correcting the rig.");



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
              + "are not being drawn — menus, cutscenes, and any moment she is not "
              + "yours to move.");

            AimPitchOffset = Config.Bind("Aim", "AimPitchOffset", 10f,
                "Angle between the controller and where the wand points, in degrees. A Touch "
              + "controller is gripped at an angle rather than in line with what it is aiming, so "
              + "its own forward points somewhat below the wand.");

            AimYawOffset = Config.Bind("Aim", "AimYawOffset", 15f,
                "Sideways angle between the controller and where the wand points, in "
              + "degrees. Zero unless a grip is habitually turned in or out; with the pitch "
              + "above it covers every direction the wand can be sent, which is why the roll "
              + "below is a refinement rather than a third axis.");

            AimRollOffset = Config.Bind("Aim", "AimRollOffset", 0f,
                "Which way round the controller the pitch and yaw above are applied, in "
              + "degrees about the controller's own forward axis. It cannot send the wand "
              + "anywhere those two cannot — turning a direction about itself leaves it "
              + "where it was — but it turns the plane they work in, so a wand that comes "
              + "out low and off to one side can be brought back with the pitch alone once "
              + "this is set.");

            AimDistance = Config.Bind("Aim", "AimDistance", 15f,
                "How far down the view the aim target sits when nothing is in the way, in "
              + "metres. When something is, the target lands on it instead.");

            ShowAimReticle = Config.Bind("Aim", "ShowAimReticle", true,
                "Marks where the shot will land, in the world, on whatever the aim ray "
              + "found. The game's own crosshair cannot say that in a headset. It is fixed at "
              + "the centre of the screen, which is the aim line on a monitor and is neither "
              + "the wand nor your gaze here, and it is painted on a flat panel at a fixed "
              + "distance, so it would read at a different place in each eye. A mark placed in "
              + "the world has neither problem, and it is the same answer whichever aim mode "
              + "is on.");

            AimReticleSize = Config.Bind("Aim", "AimReticleSize", 0.035f,
                "How big the reticle is, as a fraction of how far away it is. Angular rather "
              + "than absolute on purpose: a fixed world size disappears at range, which is "
              + "when a shot needs it most, and swells into a dinner plate against a near "
              + "wall.");

            HideGameCrosshair = Config.Bind("Aim", "HideGameCrosshair", false,
                "Switches off the game's centred crosshair. Off by default, because that mark "
              + "is not only a crosshair — it grows with the charge and carries the "
              + "magic's colour, and that information has nowhere else to go yet. Turn it on "
              + "once the world reticle is doing the aiming and the one that never moves has "
              + "become a smudge on the lens.");

            HandRotationYaw = Config.Bind("Hands", "HandRotationYaw", 0f,
                "Wrist yaw adjustment, in degrees.");
            HandRotationRoll = Config.Bind("Hands", "HandRotationRoll", -25f,
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


            DisableRespiration = Config.Bind(
                "Comfort", "DisableRespiration", true,
                "Switches off the camera's breathing sway. Pleasant on a monitor; in a headset "
              + "it moves the horizon under you.");

            DisableCameraShake = Config.Bind(
                "Comfort", "DisableCameraShake", true,
                "Switches off combat camera shake, for the same reason.");

            ThirdPersonInCutscenes = Config.Bind(
                "Comfort", "ThirdPersonInCutscenes", true,
                "Steps back out of her head whenever the game is placing the camera, and "
              + "returns when she is yours again. A cut, a sweep or a push-in is direction on "
              + "a monitor; from inside her head it is your own head being turned by someone "
              + "else, which is the sharpest vection there is because you cannot brace against "
              + "a movement you did not make. From the end of the game's own boom it is a "
              + "camera moving through a room, which is a thing you watch rather than a thing "
              + "done to you — and the framing the scene was authored with works as intended "
              + "instead of being fought. The horizon is kept level and your head still moves "
              + "the view, so it is a step back rather than the camera taking over. It applies "
              + "to every moment the game stages her, but not to the face-camera mode, which "
              + "you asked for yourself.");

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

            HudDrawOnTop = Config.Bind(
                "Interface", "HudDrawOnTop", true,
                "Draws the panel in front of the world rather than in it. The panel is geometry "
              + "hanging at a fixed distance, so without this a wall, a crate or an enemy "
              + "closer than that distance hides the interface behind it — correct of the "
              + "renderer and a bug from where you are sitting. On the flat game a screen-space "
              + "overlay drew over everything, and this is that behaviour back.");

            HudFadeSpeed = Config.Bind(
                "Interface", "HudFadeSpeed", 4f,
                "How quickly a piece of the interface fades in and out, in alpha per second. 4 "
              + "is a quarter of a second. Fades rather than switches because a widget that "
              + "appears instantly reads as a glitch in a headset, where nothing else does.");

            VrFade = Config.Bind(
                "Interface", "VrFade", true,
                "Fades the whole view to black instead of a rectangle in front of it. The "
              + "game hides its transitions behind a full-screen black image, which is right "
              + "on a monitor and useless here: a screen-space image is captured onto the HUD "
              + "panel like everything else, so a fade to black came out as a black rectangle "
              + "hanging a metre away with the level still visible around it. This switches "
              + "the game's own image off and reads its alpha instead, so the game keeps "
              + "control of when a fade starts, how long it takes and what curve it follows — "
              + "only the surface it lands on changes.");

            TidyGameHud = Config.Bind(
                "Interface", "TidyGameHud", true,
                "Master switch for the four settings below. Off gives the game its interface "
              + "back as it ships it, with everything on screen all the time.");

            HideHealthBars = Config.Bind(
                "Interface", "HideHealthBars", true,
                "Hides the health, stamina and mana bars along the top. They are the three "
              + "readings you take constantly, and the top of the view is the worst place in a "
              + "headset to take them from — see WristGauges, which is where they go instead.");

            HideChargeBar = Config.Bind(
                "Interface", "HideChargeBar", true,
                "Hides the spell charge bar. What it tells you the wand already tells you: the "
              + "charge has its own sound, its own light and its own release.");

            HideSoulCounter = Config.Bind(
                "Interface", "HideSoulCounter", true,
                "Hides the soul counter until it has something to say — it comes back for "
              + "MoneyShowSeconds whenever the count changes, and stays up for the whole of a "
              + "save statue, which is the one place you are reading the number rather than "
              + "glancing at it.");

            HideItemBar = Config.Bind(
                "Interface", "HideItemBar", true,
                "Hides the item bar along the bottom until you change item. What it is for is "
              + "telling you what you just selected, and that is a question you ask for a "
              + "second at a time.");

            HideCutsceneBars = Config.Bind(
                "Interface", "HideCutsceneBars", true,
                "Hides the black bars the game puts across the top and bottom of the screen "
              + "during a cutscene. They are letterboxing — a way of saying 'this is a film "
              + "now' on a screen that has edges — and the view in a headset has none, so they "
              + "arrive as two black planes hanging across the middle distance, cropping the "
              + "scene they were meant to frame.");

            MoneyShowSeconds = Config.Bind(
                "Interface", "MoneyShowSeconds", 3f,
                "How long the soul counter stays up after the count changes, in seconds.");

            ItemBarShowSeconds = Config.Bind(
                "Interface", "ItemBarShowSeconds", 3f,
                "How long the item bar stays up after you change item, in seconds.");

            WristGauges = Config.Bind(
                "Interface", "WristGauges", true,
                "Wears health, stamina and mana on your left wrist. Three short bars on the "
              + "back of your hand cost nothing while you are not looking at them and are read "
              + "by turning your wrist, which is a movement you make anyway. They are placed "
              + "from the controller rather than from her hand, so they are still there during "
              + "cutscenes and conversations, when the game has her body back.");

            WristGaugeRadius = Config.Bind(
                "Interface", "WristGaugeRadius", 0.045f,
                "How far the bands sit from the centre of your wrist, in metres. This is the "
              + "one to set first: it is the size of the wrist they are worn on, and the two "
              + "below are proportions of the band rather than of you.");

            WristGaugeThickness = Config.Bind(
                "Interface", "WristGaugeThickness", 0.006f,
                "How thick each band is, in metres — the radius of the tube, so a band stands "
              + "this far off your wrist and is twice this across.");

            WristGaugeSpacing = Config.Bind(
                "Interface", "WristGaugeSpacing", 0.015f,
                "Gap between one band and the next along your forearm, in metres. A negative "
              + "value stacks them the other way, which is the whole of the fix if they come "
              + "out with mana at your hand rather than at your elbow.");

            WristGaugeArc = Config.Bind(
                "Interface", "WristGaugeArc", 200f,
                "How far round your wrist a full band reaches, in degrees. Rather more than a "
              + "half turn by default: the part you cannot see is not wasted, it is what makes "
              + "a full gauge read as a closed band rather than as a strip that stops.");

            WristGaugeGlow = Config.Bind(
                "Interface", "WristGaugeGlow", 1.6f,
                "How far past full brightness the lit part of a band is driven. There is no way "
              + "to ask the game's post-processing for bloom from here, so this is the nearest "
              + "honest thing: on a camera rendering to an HDR buffer a colour above 1 is "
              + "exactly what a bloom threshold looks for and the bands will glow, and on one "
              + "that is not it clamps to full saturation and costs nothing. 1 leaves the "
              + "colours exactly as they are set.");

            WristGaugeOpacity = Config.Bind(
                "Interface", "WristGaugeOpacity", 0.9f,
                "How solid the wrist gauges are, from 0 to 1.");

            WristGaugeFillSpeed = Config.Bind(
                "Interface", "WristGaugeFillSpeed", 1.5f,
                "How fast a wrist gauge catches up when it is refilling, in bar lengths per "
              + "second. Damage is never smoothed: a hit should land on the bar as a hit, and "
              + "only the way back up reads better as a bar filling.");

            WristGaugeOffsetX = Config.Bind(
                "Interface", "WristGaugeOffsetX", 0f,
                "Offset of the wrist gauges across the panel, in metres. The three offsets are "
              + "taken in the panel's resting frame rather than after the angles below, so a "
              + "nudge of the pitch turns the panel without also moving it.");

            WristGaugeOffsetY = Config.Bind(
                "Interface", "WristGaugeOffsetY", 0f,
                "Offset of the wrist gauges up the panel, in metres.");

            WristGaugeOffsetZ = Config.Bind(
                "Interface", "WristGaugeOffsetZ", 0f,
                "Offset of the wrist gauges out of their own face, in metres. Positive lifts "
              + "them off your wrist and towards you.");

            WristGaugePitch = Config.Bind(
                "Interface", "WristGaugePitch", 0f,
                "Tilt of the wrist gauges, in degrees, from how they sit by default. Zero is "
              + "upright on the wrist: the resting pose is measured on a real controller and "
              + "baked in, so all three angles here are small corrections rather than the thing "
              + "carrying the whole orientation.");

            WristGaugeYaw = Config.Bind(
                "Interface", "WristGaugeYaw", 0f,
                "Turn of the wrist gauges about their own up axis, in degrees.");

            WristGaugeRoll = Config.Bind(
                "Interface", "WristGaugeRoll", 0f,
                "Roll of the wrist gauges about their own facing, in degrees.");



            Melee = Config.Bind(
                "Melee", "Melee", true,
                "Swing the wand to hit with it. The right controller is watched for a movement "
              + "that is both fast enough and long enough to be a swing rather than a reach, and "
              + "her own attack is triggered from it — the same one the pad's melee button "
              + "fires, so the combo, the animation and the damage are all the game's.");

            MeleeSpeed = Config.Bind(
                "Melee", "MeleeSpeed", 3f,
                "How fast your hand must be moving to begin a swing, in metres per second, "
              + "measured relative to your own head. Speed alone is not enough to fire — see "
              + "MeleeDistance — which is why this can sit low enough to catch a flick of the "
              + "wrist without every reach for a door counting as an attack.");

            MeleeDistance = Config.Bind(
                "Melee", "MeleeDistance", 0.27f,
                "How far your hand must travel, in metres, while above MeleeSpeed, before the "
              + "swing lands. This is the half that tells a swing from a twitch: a hand can "
              + "cross any speed you like for a single frame, and only a deliberate movement "
              + "keeps going for a quarter of a metre.");

            MeleeReleaseSpeed = Config.Bind(
                "Melee", "MeleeReleaseSpeed", 0.7f,
                "How slowly your hand must be moving, in metres per second, before the next "
              + "swing can begin. Below MeleeSpeed on purpose: a real swing slows at both ends "
              + "of its arc without ever stopping, and one release speed set equal to the "
              + "trigger speed would chop a single sweep into three or four attacks.");

            MeleeCooldown = Config.Bind(
                "Melee", "MeleeCooldown", 0.15f,
                "Shortest gap between two swings, in seconds. Short, because on the ground "
              + "there is no animation and no stamina underneath it — the only thing pacing a "
              + "flurry is your arm, and a limit longer than a stroke reads as hits that did "
              + "not register. It is kept above zero so a shake cannot open the collision every "
              + "other frame; set it to 0 to be held back by nothing at all, at the cost of a "
              + "melee rather stronger than the game was balanced for.");


            MeleeHitboxReach = Config.Bind(
                "Melee", "MeleeHitboxReach", 0.45f,
                "How far along the wand the hitbox sits, in metres from your hand. It rides the "
              + "same line the shot goes down, so the wand pitch and yaw offsets under Aim point "
              + "both at once; this is only how far up that line the business end is. With "
              + "MeleeHitboxSize it is the whole geometry of a blow.");

            MeleeShowHitbox = Config.Bind(
                "Melee", "MeleeShowHitbox", false,
                "Draws the hitbox: a sphere where the blow will be, at the size it will be, "
              + "turning orange for as long as it is actually open. The game has a switch of "
              + "its own for this and it cannot work in a shipped build — range drawing goes "
              + "through OnDrawGizmos, which is editor-only — so this is drawn rather than "
              + "asked for. It answers the one question a miss cannot: whether the sphere was "
              + "somewhere else, or never opened at all.");

            MeleeFreeSwingOnGround = Config.Bind(
                "Melee", "MeleeFreeSwingOnGround", true,
                "On the ground, opens the hitbox without playing the attack animation. That "
              + "animation plants her feet and swings the wand for her, which on a monitor is "
              + "the attack itself and in a headset is the game taking your arm away in the "
              + "middle of your own swing. Nothing else is given up: the damage, the element "
              + "and the knockback are authored on the hitbox rather than on the animation, the "
              + "impact effect and hit sound come from the game's collision code, and the swing "
              + "sound, the voice and the wand trail are its own calls made from here. In the "
              + "air she always keeps the game's attack whatever this says — see below.");

            MeleeHitboxSize = Config.Bind(
                "Melee", "MeleeHitboxSize", 2.25f,
                "How big the sphere on the wand is, as a multiplier on the game's own radius. "
              + "Her attack ranges carry no collider and all share one point, so a blow is a "
              + "sphere about wherever that point was put: this is not a refinement of how "
              + "forgiving a swing is, it is the whole of it. Turn on MeleeShowHitbox to see "
              + "what you are setting.");

            MeleeTrailSeconds = Config.Bind(
                "Melee", "MeleeTrailSeconds", 0.35f,
                "How long the wand trail is drawn for on a ground swing, in seconds. Zero turns "
              + "it off. The animation would normally start this; with no animation there is "
              + "nothing to say a swing happened at all when it misses.");

            MeleeSwingVoice = Config.Bind(
                "Melee", "MeleeSwingVoice", true,
                "Lets her call out on a ground swing, cycling the four attack voices the way the "
              + "combo does. Another animation event with no animation left to fire it.");

            MeleeRangeName = Config.Bind(
                "Melee", "MeleeRangeName", "",
                "Which of the character's attack ranges a ground swing opens. Empty picks the "
              + "first combo step. Worth setting once you have read the log: every range is "
              + "listed there with its strength and knockback, and since those are authored on "
              + "the range rather than on the animation, this is the choice of how hard a swing "
              + "hits as much as of where it reaches.");

            Haptics = Config.Bind(
                "Haptics", "Haptics", true,
                "Plays the game's own rumble on the controllers. The game has haptics already "
              + "and they are not lost in a headset so much as sent nowhere: every one of them "
              + "ends at a gamepad's two motors, and a headset session usually has no gamepad. "
              + "Nothing is invented here — the events, their strengths and their durations are "
              + "the game's, and its own vibration setting still switches them off. The pad "
              + "keeps its rumble either way, for anyone playing with one.");

            HapticsHand = Config.Bind(
                "Haptics", "HapticsHand", NobetaVR.Vr.HapticsHands.Both,
                "Which hand a rumble is felt in. Both is the safe answer and the default: the "
              + "game's rumble is a whole-pad event with nothing in it to say which side of her "
              + "it happened on, so putting it in one hand only would be a guess. Motors is the "
              + "interesting one — a pad's heavy motor goes to the left hand and its light one "
              + "to the right, which is what those two motors are, and it costs nothing when "
              + "the game drives both; when it drives only one, one hand goes quiet. Left and "
              + "Right put everything in one hand.");

            HapticsStrength = Config.Bind(
                "Haptics", "HapticsStrength", 1f,
                "Multiplier on every rumble. One plays the game's own levels as they are. Zero "
              + "is silence, and means it whatever the floor below is set to.");

            HapticsMinAmplitude = Config.Bind(
                "Haptics", "HapticsMinAmplitude", 0.15f,
                "The weakest rumble the controllers are asked for, from 0 to 1, applied to "
              + "events the game did ask for and not to silence. A pad motor is an eccentric "
              + "mass with real inertia and a Touch controller holds a linear actuator, so the "
              + "bottom of the pad's range does not exist in the hand: the level that hums "
              + "audibly on a pad arrives as nothing at all. Set it to 0 to take the game's "
              + "levels literally and lose its lightest taps; raise it if a footstep or a menu "
              + "tick is still not felt.");

            HapticsMaxSeconds = Config.Bind(
                "Haptics", "HapticsMaxSeconds", 2f,
                "Longest single rumble, in seconds. A safety rail rather than a taste setting: "
              + "an impulse is handed to the runtime with its whole duration at once, so a "
              + "length the game means as a fade would otherwise be felt as a controller stuck "
              + "on.");

            HapticsFrequency = Config.Bind(
                "Haptics", "HapticsFrequency", 160f,
                "Vibration frequency in hertz. A pad has no such setting — its motors run at "
              + "whatever speed the level implies — but an OpenXR impulse carries one, and a "
              + "controller's actuator has a band it is loud in: 160 is around the middle of it "
              + "for a Touch-style controller. Lower reads as a heavier thud, higher as a "
              + "sharper tick. Zero hands the choice to the runtime, which is the specification's "
              + "own default and is worth trying if nothing is felt at all.");

            DodgeAlwaysBackstep = Config.Bind(
                "Comfort", "DodgeAlwaysBackstep", true,
                "Makes the dodge always the backward hop, never the roll. The game picks "
              + "between them from the direction you are holding, and on a monitor that is a "
              + "good trade — the roll covers more ground and its spin is a flourish. In a "
              + "headset the spin is the camera going over with her, which is the single most "
              + "reliable way to make someone ill, and it happens on a button you press under "
              + "pressure. The hop is the same dodge otherwise: the same invulnerability "
              + "window, the same stamina, the same recovery. It is done by handing the game a "
              + "centred stick for the length of the dodge, so nothing is overridden and the "
              + "choice stays the game's own.");

            DeathViewRise = Config.Bind(
                "Comfort", "DeathViewRise", 2f,
                "How far the view lifts while she dies, in metres, eased in over the first "
              + "second and a half. The game's death camera sinks towards the floor with her, "
              + "which is a shot on a monitor and is your own head going down to the ground in "
              + "a headset — the one direction a view you have no control over should never "
              + "travel. Rising instead reads as leaving rather than falling, and it keeps her "
              + "in frame while it does. Zero leaves the game's camera exactly as it is.");

            ThirdPersonOnDeath = Config.Bind(
                "Comfort", "ThirdPersonOnDeath", true,
                "Steps back out of her head while she dies, and returns when you respawn. Death "
              + "is the one moment the first-person view has nothing to offer: she falls, and "
              + "the view falls with her from inside a body that is no longer yours. Pulling "
              + "back to the game's own camera gives the death its framing back and gives you "
              + "somewhere to be while it plays. The horizon is kept level and the head pose "
              + "still moves the view, so it is a step back rather than the camera taking over.");

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
            ClassInjector.RegisterTypeInIl2Cpp<NobetaVR.Ui.GameHud>();
            ClassInjector.RegisterTypeInIl2Cpp<NobetaVR.Ui.WristGauges>();
            ClassInjector.RegisterTypeInIl2Cpp<NobetaVR.Ui.VrMenu>();
            ClassInjector.RegisterTypeInIl2Cpp<NobetaVR.Ui.AimReticle>();
            ClassInjector.RegisterTypeInIl2Cpp<NobetaVR.Vr.VrHands>();
            ClassInjector.RegisterTypeInIl2Cpp<NobetaVR.Vr.VrMelee>();
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
            host.AddComponent<NobetaVR.Ui.GameHud>();
            host.AddComponent<NobetaVR.Ui.WristGauges>();
            host.AddComponent<NobetaVR.Ui.VrMenu>();
            host.AddComponent<NobetaVR.Ui.AimReticle>();
            host.AddComponent<NobetaVR.Vr.VrHands>();
            host.AddComponent<NobetaVR.Vr.VrMelee>();
        }
    }
}
