using System;
using NobetaVR.Input;
using UnityEngine;
using UnityEngine.XR;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Puts Nobeta's hands where your controllers are.
    ///
    /// The controller pose arrives in tracking space, the same space the headset pose does,
    /// so the two are related by simple subtraction: a controller's offset from the headset
    /// is the hand's offset from the eyes. Turned into the world by the view's yaw and hung
    /// off the camera, that puts her hand exactly where your hand is, one to one, with
    /// nothing in between that could be off.
    ///
    /// The hands are her own, cut out of the character's mesh and drawn as rigid geometry
    /// with the arms collapsed behind them; <see cref="DetachedHands"/> records why the arms
    /// are not solved instead. They are shown only while she is yours to move — the game
    /// gets her whole body back, arms and hands included, for cutscenes, conversations,
    /// menus and death.
    ///
    /// Runs in LateUpdate, after the animator has posed the skeleton, so the fingers can be
    /// copied from whatever the game is animating them to do.
    /// </summary>
    public sealed class VrHands : MonoBehaviour
    {
        public VrHands(IntPtr ptr) : base(ptr) { }

        private void Awake() => Instance = this;

        private sealed class Arm
        {
            public Transform Upper, Fore, Hand;

            /// <summary>
            /// The hand bone's rest orientation, expressed relative to the body's rotation.
            ///
            /// This is what makes the wrists come out straight without anyone dialling in
            /// angles. A Biped hand bone's local axes have nothing to do with how a controller
            /// is held -- the same trap as the head bone, whose forward measured 96 degrees off
            /// the view -- so a fixed Euler correction can only be found by eye and would be
            /// wrong for the next rig. Taken from the rig instead, an identity controller
            /// rotation reproduces exactly the pose the animator authored, and the controller
            /// turns the hand from there.
            /// </summary>
            public Quaternion RestRelativeToBody = Quaternion.identity;

            /// <summary>Whether <see cref="RestRelativeToBody"/> holds a real reading yet.</summary>
            public bool RestTaken;

            public bool Valid => Upper != null && Fore != null && Hand != null;

            /// <summary>
            /// Whether this hand has been holding one pose long enough for the pose to be worth
            /// calibrating on, and how long it has been asked. See <see cref="TakeRest"/>.
            ///
            /// Movement between consecutive frames rather than against the first reading: what
            /// disqualifies a pose is that it is on its way somewhere, and an animation on its
            /// way somewhere moves every frame. An idle does not — a breath at the wrist is a
            /// fraction of a degree — so the threshold has a factor of several either side of
            /// it rather than being a line drawn through the middle of the two cases.
            /// </summary>
            public bool HoldingStill(Quaternion measured, float now, out float waited, out float moved)
            {
                if (!_asked)
                {
                    _asked = true;
                    _askedSince = now;
                    _stillSince = now;
                    _last = measured;
                }

                moved = Quaternion.Angle(_last, measured);
                _last = measured;
                if (moved > StillWithin) _stillSince = now;

                waited = now - _askedSince;
                return now - _stillSince >= StillFor;
            }

            public void ForgetStillness() => _asked = false;

            private bool _asked;
            private float _askedSince;
            private float _stillSince;
            private Quaternion _last = Quaternion.identity;

            /// <summary>Degrees between frames a hand may move and still count as held.</summary>
            private const float StillWithin = 1.5f;

            /// <summary>How long it must stay within that, in seconds.</summary>
            private const float StillFor = 0.3f;

            /// <summary>
            /// How long the calibration will wait for a hand that never holds still, in seconds,
            /// before taking whatever pose it has. The hands are not drawn until it is taken, so
            /// this cannot be allowed to wait forever — a reading late is a wrist a little off,
            /// where no reading at all is no hands.
            /// </summary>
            public const float WaitAtMost = 3f;
        }

        private readonly Arm _left = new();
        private readonly Arm _right = new();

        private readonly DetachedHands _detached = new();

        /// <summary>
        /// One per hand, because they are two independent signals and sharing a filter
        /// between them would let one hand's motion open the other's.
        /// </summary>
        private readonly Steadied _leftSteady = new();
        private readonly Steadied _rightSteady = new();

        /// <summary>
        /// A hand's filter, advanced once a frame however many times the hand is placed.
        ///
        /// A hand is normally placed once a frame, but not always: the fallback in
        /// <see cref="LateUpdate"/> can place a hand that the camera then places again. A filter
        /// stepped twice against one frame's worth of dt is a filter running at half the cutoff
        /// it was asked for, so the pose is filtered on the first placement of a frame and the
        /// result handed back unchanged on any second. Nothing is lost by that: the controller
        /// reading does not change within a frame — measured, and exactly zero every time — and
        /// what the second placement is for is the eye, not the hand.
        /// </summary>
        private sealed class Steadied
        {
            private readonly SteadyPose _filter = new();
            private int _frame = -1;
            private Vector3 _position;
            private Quaternion _rotation = Quaternion.identity;

            public void Reset()
            {
                _filter.Reset();
                _frame = -1;
            }

            public void Apply(ref Vector3 position, ref Quaternion rotation)
            {
                if (_frame == Time.frameCount)
                {
                    position = _position;
                    rotation = _rotation;
                    return;
                }

                var cfg = Plugin.Instance;

                if (cfg.HandSteadiness.Value > 0.001f)
                {
                    _filter.Apply(ref position, ref rotation, Time.unscaledDeltaTime,
                                  cfg.HandSteadiness.Value, cfg.HandSteadinessResponse.Value);
                }

                _frame = Time.frameCount;
                _position = position;
                _rotation = rotation;
            }
        }

        /// <summary>
        /// Where the wand hand is and which way it points, in world space, or null on any
        /// frame the hands are not being drawn. Published so aiming can come from the hand
        /// rather than the head without either of them having to know about the other.
        /// </summary>
        internal static Vector3? AimOrigin { get; private set; }
        internal static Vector3 AimDirection { get; private set; } = Vector3.forward;

        /// <summary>
        /// The props riding on the wand hand, or null when the hands are not out.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        internal static Transform[] WandProps =>
            Instance != null && Instance._detached.Attached
                ? Instance._detached.RightAttachments
                : null;

        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        internal static VrHands Instance { get; private set; }

        /// <summary>
        /// One side's arm chain as resolved on the rig, or false before it resolves. For
        /// <see cref="ShadowBody"/>, which needs the same three bones to cast the arm's shadow.
        /// </summary>
        internal static bool TryArm(bool left, out Transform upper, out Transform fore,
                                    out Transform hand)
        {
            var arm = Instance == null ? null : left ? Instance._left : Instance._right;
            upper = arm?.Upper;
            fore = arm?.Fore;
            hand = arm?.Hand;
            return arm != null && arm.Valid;
        }

        /// <summary>See <see cref="DetachedHands.TryCollapsed"/>.</summary>
        internal static bool TryCollapsedArm(bool left, out Vector3 restScale,
                                             out Vector3 handPosition, out Quaternion handRotation)
        {
            if (Instance == null)
            {
                restScale = Vector3.one;
                handPosition = Vector3.zero;
                handRotation = Quaternion.identity;
                return false;
            }

            return Instance._detached.TryCollapsed(left, out restScale,
                                                   out handPosition, out handRotation);
        }

        private Transform _boundRoot;
        private bool _reported;

        /// <summary>
        /// The wrist calibration per rig, kept for the session rather than per body. See
        /// <see cref="TakeRest"/> — this is what stops the aim moving when you die.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, Quaternion> RestByRig = new();

        /// <summary>
        /// The rigs whose bind pose could not be read, so the search is not repeated and the
        /// line explaining why is not printed twice. See <see cref="BindPoseRest"/>.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> BindPoseRefused = new();

        /// <summary>
        /// The rigs whose calibration came from the bind pose rather than from a sample.
        ///
        /// Only the drift line reads this. "A second body would have measured this far away"
        /// was the symptom of a calibration that could move between bodies, and against a bind
        /// pose it is measuring something else entirely — how far her arm happens to be from
        /// the modelled rest pose right now, which is a number with no fault in it and would
        /// read as a warning about nothing.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> FromBindPose = new();

        /// <summary>
        /// Whether she is the player's to move this frame.
        ///
        /// Read by anything that has to stand down with the hands — the aim reticle, so
        /// far — and kept here because this is where the question is already being asked.
        /// The change is logged once rather than every frame: if the hands never appear, the
        /// log says whether this was ever true, which separates a gate that is wrong from
        /// hands that failed to build.
        /// </summary>
        internal static bool PlayerInControl { get; private set; }

        private static void SetInControl(bool value)
        {
            if (value == PlayerInControl) return;
            PlayerInControl = value;
            Plugin.Log.LogInfo(value ? "hands on: she is yours to move"
                                     : "hands off: the game has her");
        }

        /// <summary>
        /// Decides in LateUpdate, after the animator has posed the skeleton; places from the
        /// camera, in <see cref="PlaceWithCamera"/>.
        ///
        /// Whether the hands are out at all, whose skeleton they belong to and who owns the
        /// arms are all questions about this frame, and this is after the animator, which is
        /// where they have to be answered. Where the hands *go* is a different question, and
        /// it was the wrong one to answer here.
        ///
        /// A hand sits at an offset from the eye, and the eye is not written until the game's
        /// own LateUpdate — which runs after every one of the mod's, because the mod's host
        /// object is created at load, long before any stage builds the WizardGirlManage that
        /// drives the camera. So the anchor read from here was always a frame old: 30 to 80 mm
        /// while walking, measured. Invisible standing still, and a tremor the moment the
        /// anchor moves.
        ///
        /// The reticle had that fault and was fixed by moving to render time. The hands cannot
        /// be: they are skinned meshes, and Unity freezes the bone matrices of those in
        /// PostLateUpdate, before the render callback the reticle rides. Placed there they are
        /// drawn from the previous frame's matrices, which is the same frame of error again
        /// from the other end. Both were tried in the headset and neither was better than the
        /// other, which is what the two faults being worth one frame each predicts.
        ///
        /// An attempt was made to move this to `Application.onBeforeRender`, which runs after
        /// every LateUpdate and would have settled the ordering by construction. It cannot be
        /// used: the add accessor is a managed method the game never calls, so IL2CPP stripped
        /// it, and subscribing threw inside OnEnable and took the whole component down with it.
        /// Stripping is not a detail of this codebase, it is the terrain — the same wall as
        /// GetSubsystemDescriptors and TryGetFeatureValue.
        ///
        /// So the contention is removed at its source instead; see <see cref="SetFinalIkRestoring"/>.
        /// </summary>
        private void LateUpdate()
        {
            var controls = VrControls.Instance;
            var girl = controls != null && controls.Camera != null
                ? controls.Camera.wizardGirl
                : null;

            // Before every gate below, because the frame this matters most on is a costume
            // change, and she is not the player's to move while one is being put on.
            WandTrailOff.Tick(girl);

            // No character to put hands on at all: the title screen, a loading screen, the
            // gap between stages. Said out loud rather than returned quietly, because
            // anything keyed off it has to stand down too.
            // Settled before the bind rather than after it, because the bind is where the wrist
            // calibration is taken and that reading is only worth anything on a body the game
            // has finished doing things to; see TakeRest.
            var hasControl = girl != null && PlayerHasControl(controls, girl)
                          && VrCamera.ViewIsYours;

            if (girl == null || !Bind(girl.transform, hasControl))
            {
                SetInControl(false);
                return;
            }

            // Give her her arms back the moment she stops being yours to move. In a
            // cutscene, a conversation, a menu or a death she is being framed and acted
            // deliberately, often close up, and an armless Nobeta gesturing through a
            // conversation is not a trade worth making for hands nobody is holding. The
            // cut-out hands are kept rather than rebuilt: cutting them out of the mesh
            // costs a visible hitch, and every doorway would pay it twice.
            SetInControl(hasControl);

            if (!PlayerInControl)
            {
                AimOrigin = null;
                _detached.SetShown(false);
                SetFinalIkRestoring(girl.transform, true);

                // Nothing to carry across a cutscene: the hand that comes back has no
                // relation to the one that went away, and filtering between the two would
                // slide it into place.
                _leftSteady.Reset();
                _rightSteady.Reset();
                return;
            }

            YieldTheArms(girl);
            SetFinalIkRestoring(girl.transform, false);

            if (!_detached.Attached)
                _detached.Attach(_left.Upper, _left.Hand, _right.Upper, _right.Hand);

            _detached.SetShown(true);

            // Placed later in the frame — see PlaceWithCamera — unless this is the placement
            // the setting asked for, or unless nothing placed them last frame. That last one is
            // the safety net: if the camera path is not running at all, the hands are shown and
            // nobody is moving them, and hands frozen in the air are worse than hands placed
            // against a stale anchor. A frame's grace rather than none, because this runs
            // before the placements that would clear it.
            if (Plugin.Instance.HandPlacement.Value == FromLateUpdate
             || _placedFrame < Time.frameCount - 1)
                Place();
        }

        /// <summary>Where in the frame the hands are put on the controllers; see the setting.</summary>
        private const int FromLateUpdate = 0;
        private const int WithCamera = 1;
        private const int AtRender = 2;

        /// <summary>
        /// Puts the hands where the controllers are, from the postfix on the game's own camera
        /// update — the default, and the only one of the three placements with nothing wrong
        /// with it.
        ///
        /// The eye has just been written, so the anchor is this frame's rather than last
        /// frame's. And this is still inside a LateUpdate, so Unity has not yet frozen the bone
        /// matrices of the skinned meshes the hands are made of — which is what happens to the
        /// placement at render time, and why moving that placement later did not help.
        /// </summary>
        internal static void PlaceWithCamera()
        {
            // Anything that is not one of the other two, rather than an equality test on this
            // one: a number typed into the config file that matches no placement would
            // otherwise leave the hands to the fallback in LateUpdate, which is a visibly
            // broken hand rather than a setting quietly out of range. This also keeps the
            // behaviour and the menu's own label, which defaults the same way, in agreement.
            var placement = Plugin.Instance.HandPlacement.Value;
            if (placement == FromLateUpdate || placement == AtRender) return;

            PlaceIfShown();
        }

        /// <summary>
        /// The render-time placement, from <see cref="VrCamera"/>'s welded list. Kept for
        /// comparison; see the setting for why it is not the default.
        /// </summary>
        internal static void FollowView()
        {
            if (Plugin.Instance.HandPlacement.Value != AtRender) return;
            PlaceIfShown();
        }

        private static void PlaceIfShown()
        {
            var self = Instance;
            if (self == null || !PlayerInControl || !self._detached.Attached) return;

            self.Place();
        }

        /// <summary>The frame the hands were last put on the controllers; see LateUpdate.</summary>
        private int _placedFrame = -1;

        private void Place()
        {
            var controls = VrControls.Instance;
            if (controls == null) return;

            _placedFrame = Time.frameCount;

            // Cleared here rather than in LateUpdate, where it used to be. Nulling it there and
            // filling it in from the placement would leave it null for the whole LateUpdate
            // phase now that the placement happens after it, and melee reads it from a
            // LateUpdate of its own.
            AimOrigin = null;

            PlaceDetached(controls.Input, _left, XRNode.LeftHand, true);
            PlaceDetached(controls.Input, _right, XRNode.RightHand, false);
        }


        /// <summary>
        /// Puts one controller reading into the world, against the view the camera on screen
        /// was placed from.
        ///
        /// The head is subtracted from the headset reading the camera was placed from rather
        /// than from the frame's committed sample. The two are the same reading in the default
        /// placement and differ under the render-time one, where the camera has been rebuilt on
        /// a fresher head: subtracting the older sample there would leave the difference between
        /// the two instants in the hand, which is the head's own movement, added to a hand that
        /// already had it.
        /// </summary>
        private static void Compose(Transform camera, Vector3 position, Quaternion rotation,
                                    out Vector3 world, out Quaternion controllerWorld)
        {
            controllerWorld = VrCamera.ViewYaw * rotation;
            world = camera.position + VrCamera.ViewYaw * (position - VrCamera.ViewHeadRaw);
        }

        /// <summary>
        /// Where one controller is right now, for anything worn on it that outlives the hands.
        ///
        /// Unsteadied, and deliberately so. <see cref="SteadyPose"/> is a filter with state, and
        /// there is exactly one of it per hand because the hand and the shot have to agree; a
        /// second caller stepping it would be a second reading of one thing, which is the fault
        /// <see cref="Ui.WristGauges.Follow"/> exists to avoid. This is the other case — the
        /// hands are not being placed at all, so the filter is not running and there is nothing
        /// to agree with. What is left is the raw pose, which is what a thing on its way out
        /// wants: it only has to stay on the wrist while it goes.
        /// </summary>
        internal static bool TryWristPose(XRNode node, out Vector3 world,
                                          out Quaternion controllerWorld)
        {
            world = default;
            controllerWorld = Quaternion.identity;

            var controls = VrControls.Instance;
            var camera = VrCamera.CameraTransform;
            if (controls == null || camera == null) return false;
            if (!ReadController(controls.Input, node, out var position, out var rotation)) return false;

            Compose(camera, position, rotation, out world, out controllerWorld);
            return true;
        }

        /// <summary>One controller reading, or false if the device is not there this frame.</summary>
        private static bool ReadController(Input.VrInput input, XRNode node,
                                           out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;

            // The handle this frame's poll already resolved; see VrInput.TryDevice.
            if (input == null || !input.TryDevice(node, out var device)) return false;

            return InputDevices.TryGetFeatureValue_Vector3f(device.deviceId, "DevicePosition", out position)
                && InputDevices.TryGetFeatureValue_Quaternionf(device.deviceId, "DeviceRotation", out rotation);
        }

        /// <summary>
        /// Whether the player is the one moving her this frame.
        ///
        /// Three things have to agree, and each catches a case the other two miss. The
        /// camera mode covers the game staging her — cutscenes, death, the face-camera
        /// mode. The game's own controllable flag is what
        /// <c>WizardGirlManage.SetPlayerInput</c> writes, so it covers every scripted
        /// moment that leaves the camera where it was: a conversation, a door, a pickup.
        /// And a bound UI controller means a menu is up, which stops her without touching
        /// either of the other two.
        /// </summary>
        /// <remarks>
        /// The caller adds a fifth reading to this, <see cref="VrCamera.ViewIsYours"/>, and the
        /// wrist calibration is why. Loading a save does not go through any of the states below:
        /// she is `Normal` and reported controllable from the first frame of the stage while the
        /// game is still placing her, waking her against the save pillar and standing her up —
        /// her body was measured at 0 degrees on the frame this used to fire and at 225 by the
        /// time she was on her feet. So the calibration was read off whatever the animator had
        /// her wrists doing at a moment that comes out differently every launch: 38.2, 205.1,
        /// 117.6 one run against 7.5, 179.7, 155.6 the next, on the same rig in the same stage.
        /// It multiplies the hand's rotation, so the hand turned by the difference while the
        /// wrist gauges — worn on the controller's own frame — stayed put, and the bands read as
        /// having moved off the wrist. The view's own handover is the reading that covers it,
        /// because it is the one that waits for a body the game has stopped moving.
        /// </remarks>
        private static bool PlayerHasControl(VrControls controls, WizardGirlManage girl)
        {
            if (BodyFacing.Mode != PlayerCamera.CameraMode.Normal) return false;
            if (controls.GameMenuOpen) return false;

            // A fourth: the states the game drives her through while still calling her
            // controllable — dying, waking at a save point, getting back to her feet. The flag
            // below does not catch those, and hands on a body that is sitting slumped against a
            // statue are hands the player is waving through a scene they are not in.
            if (PlayerStatus.DownOrGettingUp) return false;

            var player = girl.playerController;
            if (player == null) return false;

            var runtime = player.runtimeData;
            if (runtime == null) return false;

            return runtime.Controllable && !runtime.IsDead;
        }

        private bool _weightReported;

        private Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<RootMotion.SolverManager> _solvers;
        private bool[] _solverFixTransforms;
        private bool _restoringDisabled;

        /// <summary>
        /// Turns FinalIK's pose restoration off while the mod owns the arms, and back on when it
        /// does not.
        ///
        /// `SolverManager.fixTransforms` rewinds every bone a solver manages to its animated pose
        /// at the start of that solver's update. It is restoration rather than solving, so it
        /// happens even at weight zero — which is why standing the aim IK down changed nothing:
        /// it was already at zero and still wiping the arm. Those solvers update in LateUpdate
        /// exactly as this component does, in an order Unity does not define, so the arm survived
        /// on some frames and was erased on others. That alternation was the flicker, and a limb
        /// snapping between two poses is what shook the cape.
        ///
        /// Reversible rather than a one-way switch, and each solver's own original value is
        /// remembered rather than assumed: during a cutscene the game is posing her, and its
        /// solvers need their rewind back or her gaze drifts over the course of the scene.
        /// </summary>
        private void SetFinalIkRestoring(Transform root, bool enabled)
        {
            if (_restoringDisabled == !enabled) return;

            if (_solvers == null)
            {
                _solvers = root.GetComponentsInChildren<RootMotion.SolverManager>(true);
                _solverFixTransforms = new bool[_solvers == null ? 0 : _solvers.Length];

                if (_solvers == null || _solvers.Length == 0)
                {
                    Plugin.Log.LogInfo("no FinalIK solvers on this character");
                    return;
                }

                for (var i = 0; i < _solvers.Length; i++)
                {
                    var solver = _solvers[i];
                    if (solver == null) continue;
                    _solverFixTransforms[i] = solver.fixTransforms;
                    Plugin.Log.LogInfo($"FinalIK '{solver.GetIl2CppType().Name}' on '{solver.name}': "
                                     + $"fixTransforms {solver.fixTransforms}");
                }
            }

            for (var i = 0; i < _solvers.Length; i++)
            {
                var solver = _solvers[i];
                if (solver == null) continue;
                solver.fixTransforms = enabled && _solverFixTransforms[i];
            }

            _restoringDisabled = !enabled;
        }

        /// <summary>
        /// Stands the game's own aim IK down while we are driving the arms.
        ///
        /// `NobetaIKController.aimIK` is a FinalIK solver that swings the upper body round to
        /// point the wand at the aim target, and its chain runs through the spine — which is why
        /// the cape moved with the arm, something a solver touching three arm bones cannot do.
        /// It updates in LateUpdate, and so do we, and Unity guarantees nothing about the order
        /// between two components' LateUpdate methods. So on any given frame one of us wrote
        /// last, and which one changed frame to frame: the arm flicked between the pose the
        /// controller asked for and the pose the aim asked for.
        ///
        /// The same ordering trap as `PlayerCamera.Update` at the start of this project, and the
        /// same lesson: do not race, remove the contention. With hand tracking on, the arms have
        /// one owner. Aiming still works — it now comes from the view, in `VrAim`.
        /// </summary>
        private void YieldTheArms(WizardGirlManage girl)
        {
            var skin = girl.skinController;
            var ik = skin != null ? skin.ik : null;
            if (ik == null) return;

            if (!_weightReported)
            {
                _weightReported = true;
                Plugin.Log.LogInfo($"game aim IK weight was {skin.aimIKWeight:F2}; standing it down "
                                 + "so the arms have one owner");
            }

            // Every frame, not once: the game sets this itself as she enters and leaves aiming,
            // so a single call would be undone the next time she raised the wand.
            ik.SetAimWeight(0f);
        }

        // -- skeleton ------------------------------------------------------------------

        /// <summary>
        /// Works out each arm's bone chain, once per character.
        ///
        /// Matching three names and hoping was wrong, and wrong in a way that looked like a
        /// solver bug: the solve only ever writes rotations, so it cannot stretch anything —
        /// unless the three bones are not actually an ancestor chain, in which case rotating
        /// them independently pulls them apart and the skinned mesh stretches between them.
        /// That is what dragged the cape rig around too.
        ///
        /// So only the two ends are found by name, and the middle is *derived*: walk up from the
        /// hand until the bone whose parent is the upper arm. That is the elbow by construction,
        /// whatever it is called, and it is correct whether or not the rig puts wrist or twist
        /// bones in between — those sit below the elbow, get carried along, and are never
        /// touched, so they keep exactly the local rotation the animator gave them.
        ///
        /// The chain is logged the first time. Reading it off the rig beats assuming it.
        /// </summary>
        private bool Bind(Transform root, bool settled)
        {
            // Bound already — but the calibration may still be waiting for a hand that is
            // holding still, and this early return is where that used to be lost. TakeRest ran
            // once, from the full bind below, on the first frame the rig appeared; a stage that
            // did not open with her already the player's therefore never calibrated at all, and
            // an identity rest pose is a hand held at whatever angle the Biped bone happens to
            // use. So the reading is retried here until it succeeds, which is also what lets it
            // hold out for a pose worth reading.
            if (ReferenceEquals(root, _boundRoot) && _left.Valid && _right.Valid)
                return Calibrate(root, settled);

            // Anything that gets us here invalidates the cut-out hands, and only half of it was
            // being caught. A *new body* is the obvious half. The other is the same body with a
            // new skeleton under it: `girl.transform` outlives a skin change, so a costume swap
            // — or the story skin a cutscene puts on and takes off again — leaves the root
            // identical while every bone beneath it is replaced.
            //
            // That case fell straight through. The arm chain went null, so it was re-resolved
            // against the new rig; the detached hands were not, so they went on holding the old
            // one. `Place` then collapsed an upper arm that no longer existed, which is a no-op,
            // so her real arms came back *and* the old cut-outs stayed on the controllers. That
            // is the two pairs of hands after a cutscene, and it is the same fault the death
            // path had before the root check was added — one level further down.
            if (_detached.Attached)
            {
                Plugin.Log.LogInfo("the arm binding changed; rebuilding the detached hands");
                _detached.Detach();
            }

            // The solvers belong to the skeleton that has just gone, and the latch below them
            // is what stops the new ones ever being touched. Both are forgotten here for the
            // same reason the hands are.
            _solvers = null;
            _solverFixTransforms = null;
            _restoringDisabled = false;

            if (!ReferenceEquals(root, _boundRoot))
            {
                _boundRoot = root;
                _reported = false;
                _weightReported = false;
            }

            var bones = root.GetComponentsInChildren<Transform>(true);

            // A new rig is a new set of bones, so anything measured off the old ones goes with
            // them — including how long the old hand had been holding still.
            _left.RestTaken = _right.RestTaken = false;
            _left.ForgetStillness();
            _right.ForgetStillness();

            Resolve(_left, bones, "l hand", "lefthand", "l_hand", "l upperarm", "leftarm", "l_upperarm");
            Resolve(_right, bones, "r hand", "righthand", "r_hand", "r upperarm", "rightarm", "r_upperarm");

            if (!_reported)
            {
                _reported = true;
                Report("left", _left);
                Report("right", _right);
                if (!_left.Valid || !_right.Valid)
                    Plugin.Log.LogWarning("Hand tracking is off for this character: the arm chain "
                                        + "above is incomplete. The bone list it walked is what "
                                        + "the matcher needs widening for.");
            }

            return _left.Valid && _right.Valid && Calibrate(root, settled);
        }

        /// <summary>
        /// Both wrists calibrated, taking the reading now if this is a moment worth taking it
        /// on. False until they are, which stands the hands down: a hand drawn on an identity
        /// rest pose is worse than no hand, because it looks like a bug in the tracking rather
        /// than like something the mod is still waiting for.
        /// </summary>
        private bool Calibrate(Transform root, bool settled)
        {
            // Both, every frame, rather than stopping at the first that is not ready: they are
            // two independent readings and a hand that settles first should keep its reading
            // rather than re-take it when the other one catches up.
            var left = TakeRest(_left, root, settled);
            var right = TakeRest(_right, root, settled);
            return left && right;
        }

        /// <summary>
        /// The wrist calibration: the rig's own rest pose, read out of the bind pose.
        ///
        /// <para>
        /// A skinned mesh carries the pose it was authored in. <c>Mesh.bindposes[i]</c> is the
        /// matrix that took the renderer's local space into bone <c>i</c>'s at bind time, so
        /// its inverse is that bone laid out in the renderer's space -- the rest pose, as
        /// modelled, with no animation anywhere in it. Carried out through the renderer's own
        /// transform and back into the body's frame, that is exactly the quantity this needs,
        /// and it is the same number on every launch, in every stage, for every body: model
        /// geometry rather than a moment. The offsets a player tunes against it stay tuned.
        /// </para>
        ///
        /// <para>
        /// The renderer's transform is a plain mesh object rather than a bone, so its rotation
        /// in the body's frame is a constant too -- and that is checked rather than assumed.
        /// See <see cref="BindPoseRest"/>.
        /// </para>
        ///
        /// <para>
        /// Everything from here down is the fallback for a rig that cannot answer, and the
        /// history is worth keeping because it is the argument for the paragraphs above.
        /// Sampling the animated skeleton reads off whatever pose the character happened to be
        /// in on the frame the mod bound to her. That was fine while
        /// there was one body per session. Dying reloads the stage — the log goes
        /// <c>Dead</c>, <c>Loader</c>, the act again — so a new <c>WizardGirl_Nonota(Clone)</c>
        /// arrives, the bind runs a second time, and the second reading is taken from a
        /// different pose than the first. Everything downstream of it moves: the hand mesh
        /// turns on the controller, and the wand with it, so the shot still leaves along the
        /// controller's forward while the wand it appears to leave from is pointing somewhere
        /// else. From inside the headset that reads as the aim having drifted since you died.
        ///
        /// So the reading is kept, keyed on the bone it was taken from. The same rig gives the
        /// same path every time, and the calibration a player tuned their offsets against
        /// survives every death of the session. A genuinely different skeleton has a different
        /// path and is measured afresh.
        ///
        /// <para>
        /// Holding it fixes the session and says nothing about the next one, because what is
        /// held is still the first reading and the first reading was still whatever frame the
        /// bind caught. A stage opening is the worst possible moment to ask: she is being
        /// placed, faded in and walked onto a mark, and which of those the first bound frame
        /// lands in is a race with the loader that comes out differently every launch.
        /// </para>
        ///
        /// <para>
        /// Waiting for "she is yours to move" was the first answer to that and was not enough:
        /// loading a save satisfies every part of that test — `Normal`, `controllable`, none of
        /// the respawn states — from the stage's first frame, while the game spends the next
        /// few seconds waking her against a save pillar and standing her up. Two launches of
        /// the same stage measured 38.2, 205.1, 117.6 and then 7.5, 179.7, 155.6, which is the
        /// same race it always was. It reported as the *wrist gauges* having moved, since they
        /// are hung off the controller frame and stayed exactly where they were while the hand
        /// turned out from under them.
        /// </para>
        ///
        /// <para>
        /// So two readings gate it now, and neither alone would do. Being the player's says the
        /// game has stopped *placing* her. The hand <b>holding still</b> — see
        /// <see cref="Arm.HoldingStill"/> — says her animation has stopped *moving* her, which
        /// is the part the flags cannot say and the part the measurement is actually about. It
        /// is retried every frame until both are true rather than attempted once, with a
        /// three-second cap so a hand that never settles gets a slightly wrong wrist instead of
        /// no hands at all, and the log says which of the two happened. The hands are not drawn
        /// until the reading is taken, so nothing is ever waiting on an identity rest pose.
        /// </para>
        ///
        /// <para>
        /// What is measured is logged, not just what a second body would have differed by. Two
        /// launches of the same stage should now print the same angles, and that line is the
        /// only way to tell a calibration that moved from a controller that was held
        /// differently.
        /// </para>
        ///
        /// What a second reading *would* have been is logged when there is one, because that
        /// difference is the whole of the fault above and one line settles whether it is really
        /// what moved.
        /// </summary>
        private static bool TakeRest(Arm arm, Transform root, bool settled)
        {
            if (arm.RestTaken) return true;

            var key = BonePath(arm.Hand);
            var measured = Quaternion.Inverse(root.rotation) * arm.Hand.rotation;

            // The rig's own answer, and it waits for nothing: a bind pose is not a frame, so
            // there is no moment to hold out for and the hands can be drawn at once.
            //
            // Asked once per rig either way. A rig that cannot answer is remembered as such,
            // because this runs every frame until the fallback below finds a pose worth taking
            // — and a search through every renderer under her, with a line in the log to say it
            // failed, is not something to do sixty times a second for three seconds.
            if (!RestByRig.ContainsKey(key) && !BindPoseRefused.Contains(key))
            {
                if (BindPoseRest(arm.Hand, root, out var authored))
                {
                    RestByRig[key] = authored;
                    FromBindPose.Add(key);
                    arm.RestRelativeToBody = authored;
                    arm.RestTaken = true;

                    var rest = authored.eulerAngles;
                    Plugin.Log.LogInfo($"wrist calibration for {arm.Hand.name}: "
                                     + $"{rest.x:F1}, {rest.y:F1}, {rest.z:F1} "
                                     + $"(the rig's bind pose, so the same on every launch; the "
                                     + $"animated wrist is "
                                     + $"{Quaternion.Angle(authored, measured):F1}° from it "
                                     + $"just now)");
                    return true;
                }

                BindPoseRefused.Add(key);
            }

            // A rig already calibrated this session keeps its first reading, and does not have
            // to wait for anything to take it again. The drift line is only worth printing when
            // this body is one the reading could have been taken on: a comparison against a
            // hand mid-animation is a number with nothing in it.
            if (RestByRig.TryGetValue(key, out var held))
            {
                arm.RestRelativeToBody = held;
                arm.RestTaken = true;

                var drift = Quaternion.Angle(held, measured);
                if (settled && drift > 0.5f && !FromBindPose.Contains(key))
                {
                    Plugin.Log.LogInfo($"wrist calibration held for {arm.Hand.name}: this body "
                                     + $"would have measured {drift:F1}° away from the first one.");
                }
                return true;
            }

            if (!settled) return false;

            // Still, rather than merely allowed. Being the player's is what says the game is no
            // longer *placing* her; it does not say her hands have stopped moving, and loading a
            // save is where the two come apart — she is reported controllable from the stage's
            // first frame and spends the next few seconds asleep against a pillar and then
            // standing up out of it. A reading taken anywhere in there is a reading of an
            // animation, which is why the same stage calibrated 38.2, 205.1, 117.6 one launch
            // and 7.5, 179.7, 155.6 the next.
            var still = arm.HoldingStill(measured, Time.unscaledTime, out var waited, out var moved);
            if (!still && waited < Arm.WaitAtMost) return false;

            RestByRig[key] = measured;
            arm.RestRelativeToBody = measured;
            arm.RestTaken = true;

            var angles = measured.eulerAngles;
            Plugin.Log.LogInfo($"wrist calibration for {arm.Hand.name}: "
                             + $"{angles.x:F1}, {angles.y:F1}, {angles.z:F1} "
                             + $"(relative to her body, taken {waited:F2}s after she became "
                             + $"yours, on a hand moving {moved:F2}°/frame"
                             + (still ? ")" : " — it never held still, so this is whatever it "
                                             + "was doing when the wait ran out)"));
            return true;
        }

        /// <summary>
        /// The hand bone's rest orientation in the body's frame, taken from the bind pose of
        /// whatever skinned mesh the bone belongs to. False when the rig cannot answer.
        ///
        /// Every renderer under her that skins this bone is asked, and they have to agree to
        /// within <see cref="BindPoseAgreement"/>. Agreement is what checks the one assumption
        /// this rests on: the readings can differ only through the transforms between each
        /// renderer and the body, so two renderers agreeing on a bone says neither of those
        /// chains is being animated. A disagreement is reported and the reading refused rather
        /// than averaged -- half of a wrong wrist is still a wrong wrist, and the animated
        /// fallback is at least honest about what it is.
        ///
        /// The mod's own cut-out hands cannot be picked up here: they hang off the camera
        /// rather than off her, so they are not under <paramref name="root"/> at all.
        /// </summary>
        private static bool BindPoseRest(Transform hand, Transform root, out Quaternion rest)
        {
            rest = Quaternion.identity;

            var inverseBody = Quaternion.Inverse(root.rotation);
            var found = 0;
            var fromRenderer = string.Empty;

            foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (skin == null) continue;

                var mesh = skin.sharedMesh;
                var bones = skin.bones;
                if (mesh == null || bones == null) continue;

                var poses = mesh.bindposes;
                if (poses == null || poses.Length != bones.Length) continue;

                for (var b = 0; b < bones.Length; b++)
                {
                    if (bones[b] != hand) continue;

                    // bindposes[b] takes the renderer's space into the bone's; inverted, it is
                    // the bone in the renderer's space, as modelled. The renderer's own
                    // rotation carries that into the world, and the body's takes it back out.
                    var candidate = inverseBody * skin.transform.rotation
                                  * poses[b].inverse.rotation;

                    if (found == 0)
                    {
                        rest = candidate;
                        fromRenderer = skin.name;
                    }
                    else if (Quaternion.Angle(rest, candidate) > BindPoseAgreement)
                    {
                        Plugin.Log.LogWarning(
                            $"wrist calibration: '{skin.name}' and '{fromRenderer}' disagree by "
                          + $"{Quaternion.Angle(rest, candidate):F1}° about {hand.name}'s rest "
                          + $"pose, so one of them hangs off something animated. Sampling the "
                          + $"animated wrist instead.");
                        return false;
                    }

                    found++;
                    break;
                }
            }

            if (found > 0) return true;

            Plugin.Log.LogInfo($"wrist calibration: no skinned mesh under '{root.name}' carries "
                             + $"{hand.name} among its bones, so the rest pose has to be "
                             + $"sampled off the animation.");
            return false;
        }

        /// <summary>
        /// How far two renderers may disagree about one bone's rest pose and still be believed,
        /// in degrees. Tight on purpose: they are reading one authored pose out of one rig, so
        /// anything above rounding is a chain that moves.
        /// </summary>
        private const float BindPoseAgreement = 1f;

        /// <summary>
        /// A bone's path from the scene root, which is what makes "the same rig" a question with
        /// an answer. Instance names carry the clone suffix and so are equal across reloads.
        /// </summary>
        private static string BonePath(Transform bone)
        {
            var path = bone.name;
            for (var t = bone.parent; t != null; t = t.parent) path = t.name + "/" + path;
            return path;
        }

        private static void Resolve(Arm arm, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Transform> bones,
                                    string hand1, string hand2, string hand3,
                                    string upper1, string upper2, string upper3)
        {
            arm.Upper = arm.Fore = arm.Hand = null;

            var hand = Find(bones, hand1, hand2, hand3);
            var upper = Find(bones, upper1, upper2, upper3);
            if (hand == null || upper == null) return;

            // The elbow is whichever bone on the path from the hand has the upper arm as its
            // parent. Deriving it rather than naming it also verifies the ancestry: if the
            // walk reaches the root without meeting the upper arm, these two bones are not
            // one arm, and collapsing the upper one would take something else with it.
            Transform mid = null;
            for (var t = hand; t != null; t = t.parent)
            {
                if (ReferenceEquals(t.parent, upper)) { mid = t; break; }
            }
            if (mid == null || ReferenceEquals(mid, hand)) return;

            arm.Upper = upper;
            arm.Fore = mid;
            arm.Hand = hand;
        }

        /// <summary>
        /// First bone whose name ends with one of the given suffixes, case-insensitively.
        /// Matching on the end rather than the whole name keeps it indifferent to the rig's
        /// prefix — `Bip001 L Hand` here, something else on the next character.
        /// </summary>
        private static Transform Find(
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Transform> bones,
            params string[] wanted)
        {
            for (var i = 0; i < bones.Length; i++)
            {
                var bone = bones[i];
                if (bone == null) continue;
                var name = bone.name.ToLowerInvariant();
                foreach (var w in wanted)
                    if (name.EndsWith(w, StringComparison.Ordinal)) return bone;
            }
            return null;
        }

        private static void Report(string side, Arm arm)
        {
            if (!arm.Valid)
            {
                Plugin.Log.LogWarning($"{side} arm: chain not resolved");
                return;
            }

            // The whole path, so an unexpected wrist or twist bone is visible rather than
            // inferred from how the mesh looks in the headset.
            var path = arm.Hand.name;
            for (var t = arm.Hand.parent; t != null && !ReferenceEquals(t, arm.Upper.parent); t = t.parent)
                path = t.name + " > " + path;

            Plugin.Log.LogInfo($"{side} arm chain: {path}   (collapsing {arm.Upper.name}, elbow {arm.Fore.name})");
        }

        private void OnDisable() => Release();

        /// <summary>
        /// Hands the skeleton back to the game and takes the cut-out hands apart: props
        /// reparented, arms unscaled, FinalIK allowed to restore poses again. This is the
        /// full teardown, for the component going away or the character being replaced.
        /// Handing her back for a cutscene only hides the hands; see
        /// <see cref="DetachedHands.SetShown"/>.
        /// </summary>
        private void Release()
        {
            if (_detached.Attached) _detached.Detach();
            if (_boundRoot != null) SetFinalIkRestoring(_boundRoot, true);
        }

        /// <summary>
        /// One hand, one to one, exactly where the controller is.
        ///
        /// Anchored on the camera and unscaled. With the arms gone there is no reach to run
        /// out of, so the hand can simply be where your hand is, and that correspondence is
        /// the whole point — you reach for something and the hand is there, with nothing in
        /// between that could be off.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        private void PlaceDetached(Input.VrInput input, Arm arm, XRNode node, bool left)
        {
            if (arm.Hand == null) return;

            if (!ReadController(input, node, out var position, out var rotation)) return;

            var camera = VrCamera.CameraTransform;
            if (camera == null) return;

            var cfg = Plugin.Instance;

            // Steadied here, at the source, before the hand or the shot is taken from it.
            // The tremor is invisible on the hand and plain at the end of the aim ray —
            // one lever, not two faults — and filtering the two separately would let the
            // hand and the mark disagree about where you are pointing.
            (left ? _leftSteady : _rightSteady).Apply(ref position, ref rotation);

            Compose(camera, position, rotation, out var world, out var controllerWorld);

            // The rig's own rest orientation does the work of matching a controller's
            // convention to a Biped hand bone's axes; the three Euler values on top are the
            // adjustment left for taste.
            var handRotation = controllerWorld * arm.RestRelativeToBody
                             * Quaternion.Euler(cfg.HandRotationPitch.Value,
                                                cfg.HandRotationYaw.Value,
                                                cfg.HandRotationRoll.Value);

            _detached.Place(left, world, handRotation);

            // The wrist gauges are worn on this hand, so they are placed from this pose rather
            // than from a second sample of the same controller — see WristGauges.Follow for why
            // a second sample is not the same pose.
            //
            // The controller's frame, not the hand's, and that is the whole of it: the bands'
            // rest pose was measured on a controller, and a controller is held the same way
            // every time the game is launched. The hand's frame is not, because it is the
            // controller's turned by the rig calibration — see TakeRest — and the bands were
            // briefly hung off it so they would follow the three hand-rotation adjustments.
            // That works out as `C · R · U · R⁻¹`, which is the taste offset U turned about an
            // axis that R decides: it cancels to C exactly while U is zero, and the moment it
            // is not, the bands inherit every wobble in a calibration read off whatever pose
            // the animator had her in on the frame the mod bound to her. Which is a different
            // pose from one launch to the next, and it showed as the bands sitting somewhere
            // new each time the game started. The bands have their own three angles for taste;
            // they do not need the hand's.
            if (left) Ui.WristGauges.Follow(world, controllerWorld);

            // The wand is in the right hand, so that is the one aiming. The direction is taken
            // from the controller rather than from the hand bone: a Biped hand's axes have no
            // relation to how a controller is held — the same trap as the head bone's 96-degree
            // forward — while the controller's forward is the thing you actually point.
            if (!left)
            {
                AimOrigin = world;
                AimDirection = controllerWorld
                             * Quaternion.AngleAxis(cfg.AimRollOffset.Value, Vector3.forward)
                             * Quaternion.Euler(cfg.AimPitchOffset.Value,
                                                cfg.AimYawOffset.Value, 0f)
                             * Vector3.forward;
            }
        }
    }
}
