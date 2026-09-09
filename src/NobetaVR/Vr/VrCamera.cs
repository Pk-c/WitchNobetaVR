using System;
using NobetaVR.Xr;
using UnityEngine;
using UnityEngine.XR;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Puts the head pose on the game's camera.
    ///
    /// Unity does not do this for you. Starting the XR display subsystem gets stereo and the
    /// correct per-eye projections into the headset, but nothing moves the camera when you
    /// move your head -- so the world sits rigidly attached to your face. In a normal XR
    /// project a TrackedPoseDriver on the camera closes that gap. This game shipped without
    /// XR and therefore without one, so the mod reads the pose itself.
    ///
    /// Reading it directly is also what the rest of the mod wants: turning, room-scale and
    /// recentring all need the head pose as a value we control rather than something applied
    /// behind our back.
    /// </summary>
    public sealed class VrCamera : MonoBehaviour
    {
        public VrCamera(IntPtr ptr) : base(ptr) { }

        internal static VrCamera Instance { get; private set; }

        private XrLoader _xr;
        private bool _originConfigured;
        private float _nextPoseLog;
        private float _burstUntil = -1f;

        /// <summary>The PlayerCamera currently driving the view, or null on menus.</summary>
        private PlayerCamera _playerCamera;

        /// <summary>The transform the head pose is written to.</summary>
        private Transform _target;

        // What the game itself last wrote. This is what the prefix gives back, and it must
        // stay the game's own value: hand the follower a pose we invented and it feeds on it,
        // which is the runaway this whole dance exists to avoid.
        private Vector3 _gamePos;
        private Quaternion _gameRot = Quaternion.identity;
        private Vector3 _writtenPos;
        private Quaternion _writtenRot = Quaternion.identity;
        private bool _haveOrigin;

        /// <summary>Frame on which the game's own camera update drove us, so LateUpdate knows
        /// to stand aside.</summary>
        private int _drivenFrame = -1;

        // What the view was built from this frame, kept so the render-time latch can rebuild
        // it against a fresher head pose without redoing any of the work that found it.
        private Vector3 _viewPos;
        private Quaternion _viewRot = Quaternion.identity;
        private Vector3 _latchHeadPos;
        private Quaternion _latchHeadRot = Quaternion.identity;
        private int _appliedFrame = -1;
        private int _latchedFrame = -1;
        private int _latchPlacedFrame = -1;
        private float _nextLatchLog;

        /// <summary>
        /// The camera the latch is waiting for, by instance id, and its name for the log.
        ///
        /// An id rather than the object because this is compared on the render path, once per
        /// camera per pass: an int compare costs nothing, where reaching through the wrapper
        /// for a transform or a name is an interop call and a managed string every time.
        /// </summary>
        private int _latchCameraId = -1;
        private string _latchCameraName;

        private readonly FirstPerson _firstPerson = new();

        /// <summary>
        /// Nobeta's head bone, or null before it resolves.
        ///
        /// Anything positioning parts of her body wants this rather than the camera. The camera
        /// carries the player's comfort offsets, which have nothing to do with where her head
        /// actually is.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        internal static Transform HeadBone => Instance?._firstPerson.HeadBone;

        /// <summary>The transform the view is being written to, or null before one is bound.</summary>
        internal static Transform CameraTransform => Instance?._target;

        /// <summary>
        /// Whether the game's own camera update drove us this frame, rather than the fallback
        /// in <c>LateUpdate</c> having to stand in for it.
        ///
        /// The difference is the whole of one failure: a scene that frames itself through some
        /// camera other than the <c>PlayerCamera</c> leaves us writing the head pose onto a
        /// transform nothing renders, which from inside the headset is a view placed at random
        /// with no way to tell it from a camera that simply went to the wrong place.
        /// </summary>
        internal static bool GameDriving
            => Instance != null && Instance._drivenFrame >= Time.frameCount - 1;

        /// <summary>Whether the view stood back from her on the last frame it was applied.</summary>
        internal static bool ViewStandsBack { get; private set; }

        /// <summary>
        /// Eye height with the head's own vertical movement taken out, in world units: the
        /// height at which the panels in front of you sit. Set in <see cref="Apply"/>.
        /// </summary>
        internal static float SteadyEyeHeight { get; private set; }

        /// <summary>Points the view back down Nobeta's forward on the next frame.</summary>
        internal static void RealignToBody() => Instance?._firstPerson.RealignToBody();

        /// <summary>
        /// Whether the view's yaw is the player's this frame rather than Nobeta's own facing.
        ///
        /// False only while the view is still riding her facing through a spawn or a get-up —
        /// see <see cref="FirstPerson.RideHerFacing"/>. Anything that turns her towards the
        /// view has to stand down while it is, because during it the view is her own facing and
        /// turning her towards it closes a loop on itself.
        ///
        /// True when there is nothing to ask. No camera means no first person, so there is no
        /// ride to be in the middle of and nothing for a caller to hold back for.
        /// </summary>
        internal static bool ViewIsYours => Instance?._firstPerson.ViewIsYours ?? true;

        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        internal void Bind(XrLoader xr)
        {
            _xr = xr;
            Instance = this;
        }

        /// <summary>
        /// Called from a Harmony prefix on <c>PlayerCamera.Update</c>, with the instance about
        /// to run.
        ///
        /// This settles two problems at once. It restores the game's own value before the game
        /// reads it — see <see cref="PlayerCameraUpdatePatch"/> for why that has to be a prefix
        /// rather than our own Update. And it is how the mod learns which camera to drive:
        /// being handed the live instance beats going looking for one.
        ///
        /// The looking version bound to whatever <c>Camera.main</c> returned on the first frame
        /// it ran — the BootUp scene's camera — and then kept it. Every later scene builds its
        /// own camera, so from the title screen onward the head pose was being written to an
        /// object that no longer rendered anything. The view read as frozen, with nothing in the
        /// log to say why, because writing to a stale transform fails silently.
        /// </summary>
        internal static void BeforeGameCameraUpdate(PlayerCamera instance)
        {
            var self = Instance;
            if (self == null) return;

            self.BindPlayerCamera(instance);
            self.RestoreGameCamera();
        }

        /// <summary>
        /// Called from a Harmony postfix on <c>PlayerCamera.Update</c>: the game has just
        /// finished placing its camera, so this is the moment the head goes on.
        /// </summary>
        internal static void AfterGameCameraUpdate()
        {
            var self = Instance;
            if (self == null || self._target == null) return;
            if (self._xr is not { CurrentState: XrLoader.State.Running }) return;

            self._drivenFrame = Time.frameCount;
            self.ApplyHeadPose();
        }

        private void BindPlayerCamera(PlayerCamera instance)
        {
            if (ReferenceEquals(instance, _playerCamera) && _target != null) return;

            _playerCamera = instance;
            _target = instance.g_Camera != null ? instance.g_Camera
                    : instance.g_CameraSet != null ? instance.g_CameraSet.transform
                    : null;
            _haveOrigin = false;

            if (_target == null)
            {
                Plugin.Log.LogWarning("PlayerCamera has neither g_Camera nor g_CameraSet.");
                return;
            }

            _firstPerson.Rebind(instance);

            // The game's focus frame hangs off this camera, so it is rebuilt with it.
            Ui.AimFrame.Rebind(_target);

            // A new stage means a new body somewhere else entirely. Carrying the room offset
            // across would replay it as one enormous walk the moment the level loads.
            HeadPose.Forget();
            RoomScale.Reset();

            // The turn control needs the same instance; it owns the camera's yaw.
            var controls = Input.VrControls.Instance;
            if (controls != null) controls.Camera = instance;
            EnsureLatch(instance.g_CameraSet);
            Describe("PlayerCamera", _target, instance.g_CameraSet);
        }

        /// <summary>
        /// Puts back the value the game itself last wrote, so the game never reads a transform
        /// with a head on it.
        /// </summary>
        private void RestoreGameCamera()
        {
            if (!_haveOrigin || _target == null) return;

            // Only if the transform still holds our output. If anything else moved it since,
            // that value is the game's business and must not be overwritten.
            if (_target.position != _writtenPos || _target.rotation != _writtenRot) return;

            _target.position = _gamePos;
            _target.rotation = _gameRot;
        }

        private void LateUpdate()
        {
            if (_xr is not { CurrentState: XrLoader.State.Running }) return;

            ConfigureTrackingOrigin();

            // While a PlayerCamera is driving, the pose is applied from the postfix that runs
            // straight after the game's own camera update. Doing it here as well would be at
            // best redundant and at worst a second race.
            if (_drivenFrame >= Time.frameCount - 1) return;

            // Menus have no PlayerCamera to hand us one, so fall back to looking. Re-checked
            // whenever the target goes missing rather than cached once for the process, because
            // every scene builds its own camera.
            if (_target == null) AcquireFallbackCamera();
            if (_target == null) return;

            ApplyHeadPose();
        }

        /// <summary>
        /// The yaw the view is built on, before the headset is added. Room-scale uses this,
        /// because it converts a physical step into a world direction and the step is already
        /// expressed in headset space.
        /// </summary>
        internal static Quaternion ViewYaw { get; private set; } = Quaternion.identity;

        /// <summary>
        /// The headset reading, in tracking space, that the camera now on screen was placed
        /// from -- the frame's own sample normally, the render-time latch's fresher one once
        /// the latch has run.
        ///
        /// Anything hung off the eye by a tracking-space offset has to subtract this one rather
        /// than <see cref="HeadPose.Raw"/>. The hands are the case: their offset from the eye is
        /// a controller reading minus a headset reading, and taking the headset from a different
        /// instant than the camera was placed at leaves the difference between the two instants
        /// in the hand, which is the head's own movement, added to a hand that already had it.
        /// </summary>
        internal static Vector3 ViewHeadRaw { get; private set; }

        /// <summary>
        /// Where you are actually looking, flattened: the game's yaw with the headset's own
        /// rotation folded in.
        ///
        /// This is what the body must follow. Turning physically rotates the headset and
        /// nothing else — the game's camera yaw only moves when the turn control moves it — so
        /// a body that follows <see cref="ViewYaw"/> alone ignores every physical turn you
        /// make, which is exactly the room-scale rotation that went missing.
        /// </summary>
        internal static Vector3 ViewForwardFlat { get; private set; } = Vector3.forward;

        /// <summary>
        /// Whether the view stands back from her rather than sitting in her head.
        ///
        /// Two cases, and they arrive from opposite directions. Death is a latch, because it
        /// spans states the camera mode cannot describe — see <see cref="DeathView"/>. A
        /// cutscene is not: the camera mode says so for exactly as long as it lasts.
        ///
        /// <para>
        /// The cutscene case replaces the black frame that used to be drawn round the view
        /// instead. The frame was treating the symptom. What makes a cutscene hard to sit
        /// through in a headset is being *inside her head* while somebody else turns it: every
        /// sweep is a movement of your own head that your neck did not make, and no amount of
        /// letterboxing changes that it is your head. Standing back to the game's own boom
        /// makes the camera a camera again — it moves through the room, and you watch it move,
        /// the way you watch anything else move. The game's framing then works as authored
        /// rather than being fought, which is the other half of what the frame cost.
        /// </para>
        ///
        /// <c>PlayerFace</c> is excluded: the player asked for that one themselves with
        /// <c>SwitchCameraMode</c>, and pulling the view out of her head because she chose to
        /// look at herself would be the mod second-guessing them.
        /// </summary>
        private static bool ThirdPersonView()
        {
            // Asked first and unconditionally. DeathView owns a latch, and a latch that is only
            // consulted on the frames some other condition happens to be false on is a latch
            // that opens late, closes late, or does not close at all — a cutscene during the
            // respawn would have held it open to its timeout.
            var dying = DeathView();

            return dying || (Plugin.Instance.ThirdPersonInCutscenes.Value && GameIsFraming());
        }

        /// <summary>
        /// Whether the game, rather than the player, is placing the camera this frame.
        ///
        /// <c>Dead</c> and <c>FallDead</c> are left out and handled by the death latch below,
        /// which covers the three beats after them that this cannot see.
        /// </summary>
        private static bool GameIsFraming()
        {
            var mode = BodyFacing.Mode;
            return mode != PlayerCamera.CameraMode.Normal
                && mode != PlayerCamera.CameraMode.PlayerFace
                && mode != PlayerCamera.CameraMode.Dead
                && mode != PlayerCamera.CameraMode.FallDead;
        }

        /// <summary>
        /// Whether the view should stand back from her, from the moment she dies to the moment
        /// she is yours again.
        ///
        /// The camera mode opens this and does not close it. Dying is not one beat but four —
        /// the fall, the stage reloading, her sitting slumped against the save statue, and her
        /// standing up out of it — and only the first of them is `Dead`. The rest are `Normal`
        /// with the game still holding her, so a gate on the mode alone would put the view back
        /// inside her head to watch her own body get up from behind her eyes, which is both the
        /// strangest part of it and the longest.
        ///
        /// What closes it is the game saying she is controllable again, which is the same flag
        /// the hands stand down on and means exactly "she is yours now". It fails open: if
        /// there is no character to ask — a menu, the title screen, a stage that never finished
        /// loading — the latch is dropped rather than held, because being stuck in third person
        /// is a worse fault than a frame of it too few.
        /// </summary>
        private static bool DeathView()
        {
            if (!Plugin.Instance.ThirdPersonOnDeath.Value)
            {
                _deathLatch = false;
                return false;
            }

            var mode = BodyFacing.Mode;

            // Opened by the death itself, and by nothing else. A knockdown in a fight goes
            // through some of the same states on its way back up, and pulling the view out of
            // her head mid-combat because she was floored would be its own kind of unpleasant.
            if (mode == PlayerCamera.CameraMode.Dead
             || mode == PlayerCamera.CameraMode.FallDead
             || PlayerStatus.Dead)
            {
                if (!_deathLatch)
                {
                    _deathLatch = true;
                    _latchedAt = Time.unscaledTime;
                    Plugin.Log.LogInfo("death: the view steps back out of her head");
                }
                return true;
            }

            if (!_deathLatch) return false;

            // Closed by her being plainly the player's again: an ordinary state, an ordinary
            // camera mode, and the game's own controllable flag. All three, because each of
            // them is true on its own somewhere in the middle of this — she reads controllable
            // while sitting against the save statue, and the camera is back to Normal long
            // before she is on her feet.
            if (!PlayerStatus.DownOrGettingUp
             && PlayerStatus.Controllable
             && mode == PlayerCamera.CameraMode.Normal)
            {
                _deathLatch = false;
                Plugin.Log.LogInfo("death: she is yours again; the view goes back on her head");
                return false;
            }

            // The backstop, and the reason the rest of it can afford to be cautious. Held
            // through a level load there is nothing to ask during, so the release has to be
            // able to give up: stuck in third person is a fault a player cannot get out of.
            if (Time.unscaledTime - _latchedAt < LatchTimeout) return true;

            _deathLatch = false;
            Plugin.Log.LogWarning($"death: nothing said she was hers again within "
                                + $"{LatchTimeout:F0}s; putting the view back anyway.");
            return false;
        }

        /// <summary>
        /// How far the view is lifted while she dies, this frame.
        ///
        /// The game's death camera sinks towards the floor with her. On a monitor that is a
        /// shot; in a headset it is your own head travelling to the ground, which is the one
        /// direction a view you have no control over should never take — you cannot brace
        /// against it, you cannot look away from it, and it lasts as long as the death does.
        /// Going the other way costs nothing, keeps her in frame, and reads as leaving rather
        /// than as falling.
        ///
        /// Added to the game's position rather than replacing its height, so the lift survives
        /// the stage reload in the middle of the death: the latch stays open across it and an
        /// absolute height captured before it would belong to a camera that no longer exists.
        ///
        /// Eased in from the moment the latch opened, over a second and a half: arriving
        /// instantly would be its own jolt, on the frame that is already the worst one to
        /// spend one. Cutscenes get none of it — the game is placing that camera deliberately
        /// and it is not falling to the floor, so there is nothing to answer.
        /// </summary>
        private static float DeathRise()
        {
            var rise = Plugin.Instance.DeathViewRise.Value;
            if (rise <= 0.001f || !_deathLatch) return 0f;

            const float ease = 1.5f;
            var t = Mathf.Clamp01((Time.unscaledTime - _latchedAt) / ease);
            return rise * t * t * (3f - 2f * t);
        }

        private static bool _deathLatch;
        private static float _latchedAt;

        /// <summary>Longest the view will stay back waiting to be told she is hers, in seconds.</summary>
        private const float LatchTimeout = 45f;

        private void ApplyHeadPose()
        {
            HeadPose.Sample();
            var headPos = HeadPose.Position;
            var headRot = HeadPose.Rotation;

            // Room-scale hands the neck's travel to the character, and the view is anchored to
            // her head bone, so applying that part here as well would move the view twice. What
            // is left is the vertical -- crouching lowers your eyes without walking her anywhere
            // -- and the eyes' own offset from the neck, so looking around still swings your
            // viewpoint the way a head does rather than pivoting on a point between your ears.
            if (Plugin.Instance.RoomScale.Value)
                headPos = new Vector3(HeadPose.EyesFromNeck.x, headPos.y, HeadPose.EyesFromNeck.z);

            LogPose(headPos, headRot);

            Apply(headPos, headRot);
        }

        /// <summary>
        /// Adds the head pose on top of wherever the game put its camera this frame.
        ///
        /// Pair this with <see cref="RestoreGameCamera"/>, which takes it back off before the
        /// game looks again. The two halves together are what stop the camera running away.
        /// </summary>
        private void Apply(Vector3 headPos, Quaternion headRot)
        {
            // After the prefix's restore the transform holds the game's own value, so this is
            // normally just "take what the game decided". The comparison stays as a backstop for
            // the cases with no PlayerCamera to prefix at all — the title screen, where nothing
            // drives the camera and the origin simply never changes.
            if (!_haveOrigin || _target.position != _writtenPos || _target.rotation != _writtenRot)
            {
                _gamePos = _target.position;
                _gameRot = _target.rotation;
                _haveOrigin = true;
            }

            // Where the VR view is built from. In third person that is the game's own pose; in
            // first person it is Nobeta's head instead. Either way it is a separate value from
            // the one above, which the game gets back untouched.
            var viewPos = _gamePos;
            var viewRot = _gameRot;

            // Death and cutscenes step back out of her head, and the boom pose already there is
            // the step — the game frames both from the end of it, and being inside a body that
            // is no longer yours while somebody else moves it is the one thing first person has
            // nothing to offer. Its rotation is flattened to yaw all the same, for the reason
            // first person does it too: the game's pitch added to the headset's would pitch
            // twice, and a horizon that rolls while you can only watch is the worst place to
            // spend it. So the game gets its framing and its travel; your neck keeps the two
            // axes a neck is entitled to.
            //
            // Nothing else has to be undone. The head comes back on its own, because it is
            // hidden by distance rather than by a switch, and the hands, the body facing and
            // room-scale all already stand down whenever the camera is not in Normal.
            ViewStandsBack = ThirdPersonView();

            var inHead = false;

            if (ViewStandsBack)
            {
                viewRot = Quaternion.Euler(0f, _gameRot.eulerAngles.y, 0f);
                viewPos += Vector3.up * DeathRise();
            }
            // If the head bone is not loaded yet, first person declines and the boom pose
            // stands, so a stage opens in third person for a few frames rather than snapping
            // somewhere wrong.
            else if (_firstPerson.GetOrigin(_gameRot, out var fpPos, out var fpRot))
            {
                viewPos = fpPos;
                viewRot = fpRot;
                inHead = true;
            }

            ViewYaw = viewRot;

            // Where a panel hung in front of you belongs, vertically.
            //
            // Not the view's own height, which is what this used to be. Taking the headset's
            // vertical out of it was necessary and not sufficient: what was left is the height
            // of her head bone, and the head bone is the far end of a loop that runs through
            // the aim target and the game's IK and back — see FirstPerson.EyeLevel. So in first
            // person the height comes from her feet plus a figure that does not move. Standing
            // back there is no body to ask about, and the camera's own height is the only
            // answer there is.
            SteadyEyeHeight = inHead ? _firstPerson.EyeLevel : viewPos.y;

            var lookForward = viewRot * headRot * Vector3.forward;
            lookForward.y = 0f;
            if (lookForward.sqrMagnitude > 0.0001f) ViewForwardFlat = lookForward.normalized;

            _target.rotation = viewRot * headRot;
            _target.position = viewPos + viewRot * headPos;

            _writtenPos = _target.position;
            _writtenRot = _target.rotation;

            // Kept for the render-time latch, which redoes only the last two lines above
            // against a head pose read an instant before the frame is drawn. See LateLatch.
            _viewPos = viewPos;
            _viewRot = viewRot;
            _latchHeadPos = headPos;
            _latchHeadRot = headRot;
            _appliedFrame = Time.frameCount;

            // The raw reading rather than headPos: room-scale rewrites headPos to leave the
            // horizontal to the body, and what an offset from the eye has to be measured
            // against is the headset where it actually was.
            ViewHeadRaw = HeadPose.Raw;

            // The hands, while the eye is final and the frame is still in LateUpdate. This is
            // the one point that satisfies both ends of what a skinned mesh hung off the eye
            // needs; see VrHands.PlaceWithCamera. Before VrAim below, so the aim it takes from
            // the wand hand is this frame's.
            VrHands.PlaceWithCamera();

            // Only now is the camera's real position known, and the head's visibility depends on
            // it. Deciding earlier would test last frame's position against this frame's bone.
            _firstPerson.UpdateHeadVisibility(_writtenPos);

            // Same reason: the aim line is the view's line, and the view is only final here.
            VrAim.Apply(_playerCamera, _target);

            // Everything welded to the view: the fade, which cannot be a frame late without a
            // seam opening at its edge, and the panels, which are pinned rigidly to the eye on
            // purpose -- so a frame-old eye position does not make them lag, it makes them
            // shake, by as much as the head moved and in whichever direction. Which is why the
            // interface was the first part to tremble on a nod.
            //
            // Skipped when the render-time latch is doing this instead. It runs after every
            // LateUpdate and re-places all of it from a head pose read an instant before the
            // frame is drawn, so placing here first is the same work against a poorer pose,
            // thrown away a moment later. A frame's grace rather than this frame, because this
            // frame's latch has not run yet -- it runs after this does.
            if (_latchPlacedFrame < Time.frameCount - 1) PlaceWeldedToView();
        }

        /// <summary>
        /// Everything that hangs off the eye and has to move with it exactly.
        ///
        /// One list, called from whichever of the two places wrote the view last -- see
        /// <see cref="Apply"/> and <see cref="Latch"/>. Two copies of it would be two chances
        /// for something welded to the view to be left off one of them, and the symptom of
        /// that is a single panel shaking while the rest hold still.
        ///
        /// <para>
        /// The hands are the exception, and belong on this list only when the setting asks for
        /// it. Everything else here is an ordinary mesh, whose transform the pipeline reads
        /// during culling -- after this. The hands are skinned, and Unity freezes the bone
        /// matrices of skinned meshes in PostLateUpdate, before it. So the latest possible
        /// placement, which is the right answer for a panel, draws a hand from the previous
        /// frame's pose. Theirs is <see cref="VrHands.PlaceWithCamera"/>, from
        /// <see cref="Apply"/>, where the eye is final and the freeze has not happened yet.
        /// </para>
        /// </summary>
        private void PlaceWeldedToView()
        {
            ViewFade.Apply(_target);
            Ui.HudPanel.FollowView();
            Ui.VrMenu.FollowView();
            Ui.AimReticle.FollowView();
            Ui.FpsCounter.FollowView();
            VrHands.FollowView();
        }

        /// <summary>
        /// Rebuilds the view from a head pose read at render time, called from
        /// <see cref="LateLatch"/>.
        ///
        /// Only the last step of <see cref="Apply"/> is redone. Everything that found the view
        /// -- which camera is driving, whether the view stands back, where her head bone is --
        /// is a decision about this frame and is not revisited; only the head goes on again,
        /// fresher. Nothing that touches the game is repeated either: the aim target has
        /// already been placed and the character has already been moved, and doing either of
        /// them twice in a frame would be handing the game two different accounts of one frame.
        /// </summary>
        internal static void LateLatchPose(Camera source)
        {
            var self = Instance;
            if (self == null) { Trace("no VrCamera instance"); return; }

            // Told which camera is about to be drawn, this waits for the one the view is
            // written to -- the HUD's own capture camera goes through the same pipeline every
            // frame, and refreshing the pose for that one would spend the frame's single latch
            // on it.
            //
            // Told nothing, it latches on the first call of the frame instead. That is the
            // whole-frame hook, which fires once before anything is drawn, so there is no
            // wrong camera to wait for.
            if (source != null && self._latchCameraId != -1
             && source.GetInstanceID() != self._latchCameraId)
            {
                self.TraceWrongCamera(source);
                return;
            }

            self.Latch();
        }

        private void Latch()
        {
            if (_target == null) { Trace("no target transform"); return; }
            if (_xr is not { CurrentState: XrLoader.State.Running }) { Trace("XR not running"); return; }

            // Nothing was placed this frame -- XR down, no camera bound, the view not applied.
            // There is no pose to refresh, and inventing one from a stale _viewPos would put
            // the camera somewhere the frame never agreed to.
            if (_appliedFrame != Time.frameCount)
            {
                Trace("the render arrived on a frame the view was never placed on");
                return;
            }

            // MultiPass renders the scene once per eye, so this message arrives twice. Both
            // eyes must be drawn from one head pose or the stereo pair disagrees, which is a
            // worse tremor than the one this exists to remove.
            if (_latchedFrame == Time.frameCount) return;
            _latchedFrame = Time.frameCount;

            if (!HeadPose.Peek(out var headPos, out var headRot, out var eyesFromNeck, out var headRaw))
            {
                Trace("no recentre origin yet, so there is no pose to express");
                return;
            }

            if (Plugin.Instance.RoomScale.Value)
                headPos = new Vector3(eyesFromNeck.x, headPos.y, eyesFromNeck.z);

            // Measured before the setting is consulted, so a run with the latch off still says
            // what turning it on would have been worth.
            LogLatch(headPos, headRot);

            if (!Plugin.Instance.LateLatchPose.Value) return;

            _target.rotation = _viewRot * headRot;
            _target.position = _viewPos + _viewRot * headPos;

            _writtenPos = _target.position;
            _writtenRot = _target.rotation;

            // The view moved, so the reading everything welded to it measures from moves with
            // it. Set before PlaceWeldedToView below, which is what reads it.
            ViewHeadRaw = headRaw;

            // Placed from here rather than from Apply, and this is the frame Apply reads to
            // know to stand aside. Kept apart from _latchedFrame above: that one says this ran,
            // which it does whether or not it places anything, and standing Apply down on the
            // strength of a latch that declined would leave the panels placed by nobody.
            _latchPlacedFrame = Time.frameCount;
            PlaceWeldedToView();
        }

        /// <summary>
        /// Says once why the render-time latch did or did not happen.
        ///
        /// One line per distinct reason, for the life of the process. A latch that never fires
        /// and a latch that fires and declines are the same silence in a log and want opposite
        /// fixes, and the reason is not something a player can be asked to reproduce twice.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> _traced = new();

        private static void Trace(string why)
        {
            if (!Plugin.Instance.LogPoseLatch.Value || !_traced.Add(why)) return;
            Plugin.Log.LogInfo($"pose latch: {why}");
        }

        /// <summary>
        /// Names the camera the latch turned down, once per camera it is bound to.
        ///
        /// Kept apart from <see cref="Trace"/> and behind a plain bool because this is the one
        /// reason that fires on the render path every frame -- the HUD's capture camera is
        /// drawn through the same pipeline and is never the one wanted. Reading two names to
        /// build a line the set was going to discard is an interop call and two managed strings
        /// per frame, spent on saying nothing.
        /// </summary>
        private bool _wrongCameraTraced;

        private void TraceWrongCamera(Camera source)
        {
            if (_wrongCameraTraced) return;
            _wrongCameraTraced = true;
            Trace($"'{source.name}' is not the driven camera '{_latchCameraName}'");
        }

        /// <summary>
        /// Reports how far the head moved between the frame's own sample and the render.
        ///
        /// This is the measurement that says whether the latch is worth having. If it reads a
        /// steady zero the pose was already fresh and the tremor is somebody else's -- the
        /// frame rate against the headset's cadence is the next thing to look at. If it reads
        /// a fraction of a degree at rest and a degree or more on a nod, that is the shiver,
        /// written down in the units it is seen in.
        /// </summary>
        private void LogLatch(Vector3 pos, Quaternion rot)
        {
            if (!Plugin.Instance.LogPoseLatch.Value) return;
            if (Time.unscaledTime < _nextLatchLog) return;
            _nextLatchLog = Time.unscaledTime + 0.5f;

            // By hand rather than through Quaternion.Angle: the same IL2CPP wall as
            // Quaternion.Normalize in SteadyPose -- what the game never calls is not there.
            var dot = Mathf.Clamp(Mathf.Abs(Quaternion.Dot(_latchHeadRot, rot)), -1f, 1f);
            var degrees = 2f * Mathf.Acos(dot) * Mathf.Rad2Deg;
            var metres = (pos - _latchHeadPos).magnitude;

            Plugin.Log.LogInfo($"pose latch: {degrees:F3} deg and {metres * 1000f:F1} mm of head "
                             + $"movement between the frame's sample and the render, at "
                             + $"{1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f):F0} fps against a "
                             + $"{Diagnostics.VrRuntime.RefreshHz:F0} Hz headset "
                             + $"(latch {(Plugin.Instance.LateLatchPose.Value ? "on" : "off")})");
        }

        /// <summary>
        /// Remembers which camera the render-time latch should wait for.
        ///
        /// Nothing is attached to it. The hook is a patch on URP's own entry point rather than
        /// a component on the camera -- see <see cref="LateLatch"/> for why a component cannot
        /// work here -- so all this has to do is name the camera the view is written to, and
        /// let the dispatch ignore every other one the pipeline draws.
        ///
        /// The game hands us <c>g_Camera</c> and <c>g_CameraSet</c> separately and does not
        /// promise they are the same object, so the Camera is preferred where there is one and
        /// the driven transform is the fallback.
        /// </summary>
        private void EnsureLatch(Camera camera)
        {
            var found = camera != null ? camera
                      : _target != null ? _target.GetComponent<Camera>()
                      : null;

            // A new camera is a new thing to say about, so the report is re-armed rather than
            // being a once-per-process fact like the rest of them.
            _wrongCameraTraced = false;

            if (found == null)
            {
                // Nothing to compare against, so the latch takes the first camera of the frame.
                // That is a guess and it is the right one nearly always -- the view is the
                // camera the game draws the world with -- but it can spend the frame's latch on
                // some other camera, so it is said out loud rather than silently tolerated.
                _latchCameraId = -1;
                _latchCameraName = null;
                Plugin.Log.LogWarning("No Camera on the driven view, so the render-time latch "
                                    + "cannot tell which camera to refresh the pose for; it "
                                    + "will take the first one the pipeline draws.");
                return;
            }

            _latchCameraId = found.GetInstanceID();
            _latchCameraName = found.name;

            Plugin.Log.LogInfo($"the render-time pose latch will wait for '{_latchCameraName}'");
        }

        private void AcquireFallbackCamera()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var all = Camera.allCameras;
                if (all != null && all.Length > 0) cam = all[0];
            }
            if (cam == null) return;

            _target = cam.transform;
            _haveOrigin = false;
            EnsureLatch(cam);
            Describe("fallback", _target, cam);
        }

        /// <summary>
        /// Device-relative tracking, deliberately, and only for now.
        ///
        /// Under a Floor origin the head pose carries your real standing height, which would
        /// lift the camera a metre and a half above wherever the game put it. Device origin
        /// puts the pose near zero at recentre, so the camera sits exactly where the game's
        /// camera is and only head movement moves it. Floor is what room-scale will want later.
        /// </summary>
        private void ConfigureTrackingOrigin()
        {
            if (_originConfigured) return;
            _originConfigured = true;

            var input = _xr.Input;
            if (input == null)
            {
                Plugin.Log.LogWarning("No input subsystem, so no tracking origin to set. "
                                    + "Head pose may read as identity.");
                return;
            }

            Plugin.Log.LogInfo($"input subsystem running={input.running}, "
                             + $"supported origins={input.GetSupportedTrackingOriginModes()}, "
                             + $"current={input.GetTrackingOriginMode()}");

            if (!input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Device))
                Plugin.Log.LogWarning("Could not set a Device tracking origin.");

            if (!input.TryRecenter())
                Plugin.Log.LogInfo("TryRecenter refused; the runtime may not offer it.");

            Plugin.Log.LogInfo($"tracking origin now {input.GetTrackingOriginMode()}");
        }

        /// <summary>
        /// Reports the head pose, in a burst whenever the camera changes and afterwards only if
        /// the config asks.
        ///
        /// The burst is deliberate. "The view does not follow my head" has two very different
        /// causes — the pose never arrives, or it arrives and is written somewhere that no
        /// longer renders — and from inside the headset the two look identical. Re-arming the
        /// burst on every camera change is what would have caught the stale-camera bug in one
        /// run rather than two.
        /// </summary>
        private void LogPose(Vector3 pos, Quaternion rot)
        {
            if (Time.unscaledTime >= _burstUntil || Time.unscaledTime < _nextPoseLog) return;
            _nextPoseLog = Time.unscaledTime + 0.5f;

            var e = rot.eulerAngles;
            Plugin.Log.LogInfo($"head  pos ({pos.x:F3}, {pos.y:F3}, {pos.z:F3})  "
                             + $"rot ({e.x:F1}, {e.y:F1}, {e.z:F1})");
        }

        private void Describe(string how, Transform target, Camera cam)
        {
            var path = target.name;
            for (var t = target.parent; t != null; t = t.parent) path = t.name + "/" + path;

            Plugin.Log.LogInfo($"driving camera via {how}: '{path}'"
                             + (cam != null
                                 ? $"  tag='{cam.tag}' depth={cam.depth} "
                                 + $"stereo={cam.stereoEnabled} targetEye={cam.stereoTargetEye}"
                                 : "  (no Camera component supplied)"));

            // A camera change is exactly when the pose is worth watching again.
            _burstUntil = Time.unscaledTime + 6f;
            _nextPoseLog = 0f;
        }
    }
}
