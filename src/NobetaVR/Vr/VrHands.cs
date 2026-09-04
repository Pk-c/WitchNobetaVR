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

            public bool Valid => Upper != null && Fore != null && Hand != null;
        }

        private readonly Arm _left = new();
        private readonly Arm _right = new();

        private readonly DetachedHands _detached = new();

        /// <summary>
        /// One per hand, because they are two independent signals and sharing a filter
        /// between them would let one hand's motion open the other's.
        /// </summary>
        private readonly SteadyPose _leftSteady = new();
        private readonly SteadyPose _rightSteady = new();

        /// <summary>
        /// Where the wand hand is and which way it points, in world space, or null on any
        /// frame the hands are not being drawn. Published so aiming can come from the hand
        /// rather than the head without either of them having to know about the other.
        /// </summary>
        internal static Vector3? AimOrigin { get; private set; }
        internal static Vector3 AimDirection { get; private set; } = Vector3.forward;

        private Transform _boundRoot;
        private bool _reported;

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
        /// Solves in LateUpdate, after the animator has posed the skeleton.
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

            // No character to put hands on at all: the title screen, a loading screen, the
            // gap between stages. Said out loud rather than returned quietly, because
            // anything keyed off it has to stand down too.
            if (girl == null || !Bind(girl.transform))
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
            SetInControl(PlayerHasControl(controls, girl));

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

            AimOrigin = null;

            if (!_detached.Attached)
                _detached.Attach(_left.Upper, _left.Hand, _right.Upper, _right.Hand);

            _detached.SetShown(true);

            PlaceDetached(_left, XRNode.LeftHand, true);
            PlaceDetached(_right, XRNode.RightHand, false);
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
        private static bool PlayerHasControl(VrControls controls, WizardGirlManage girl)
        {
            if (BodyFacing.Mode != PlayerCamera.CameraMode.Normal) return false;
            if (controls.GameMenuOpen) return false;

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
        private bool Bind(Transform root)
        {
            if (ReferenceEquals(root, _boundRoot) && _left.Valid && _right.Valid) return true;
            if (!ReferenceEquals(root, _boundRoot))
            {
                // A new body: give the old one its bones back before forgetting it.
                if (_detached.Attached) _detached.Detach();

                _boundRoot = root;
                _reported = false;
                _solvers = null;
                _weightReported = false;
            }

            var bones = root.GetComponentsInChildren<Transform>(true);

            Resolve(_left, bones, "l hand", "lefthand", "l_hand", "l upperarm", "leftarm", "l_upperarm");
            Resolve(_right, bones, "r hand", "righthand", "r_hand", "r upperarm", "rightarm", "r_upperarm");

            if (_left.Valid)
            {
                _left.RestRelativeToBody = Quaternion.Inverse(root.rotation) * _left.Hand.rotation;
            }
            if (_right.Valid)
            {
                _right.RestRelativeToBody = Quaternion.Inverse(root.rotation) * _right.Hand.rotation;
            }

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

            return _left.Valid && _right.Valid;
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
        private void PlaceDetached(Arm arm, XRNode node, bool left)
        {
            if (arm.Hand == null) return;

            var device = InputDevices.GetDeviceAtXRNode(node);
            if (!device.isValid) return;

            if (!InputDevices.TryGetFeatureValue_Vector3f(device.deviceId, "DevicePosition", out var position)
             || !InputDevices.TryGetFeatureValue_Quaternionf(device.deviceId, "DeviceRotation", out var rotation))
                return;

            var camera = VrCamera.CameraTransform;
            if (camera == null) return;

            var cfg = Plugin.Instance;

            // Steadied here, at the source, before the hand or the shot is taken from it.
            // The tremor is invisible on the hand and plain at the end of the aim ray —
            // one lever, not two faults — and filtering the two separately would let the
            // hand and the mark disagree about where you are pointing.
            if (cfg.HandSteadiness.Value > 0.001f)
            {
                (left ? _leftSteady : _rightSteady).Apply(
                    ref position, ref rotation, Time.unscaledDeltaTime,
                    cfg.HandSteadiness.Value, cfg.HandSteadinessResponse.Value);
            }

            var controllerWorld = VrCamera.ViewYaw * rotation;
            var world = camera.position + VrCamera.ViewYaw * (position - HeadPose.Raw);

            // The rig's own rest orientation does the work of matching a controller's
            // convention to a Biped hand bone's axes; the three Euler values on top are the
            // adjustment left for taste.
            var handRotation = controllerWorld * arm.RestRelativeToBody
                             * Quaternion.Euler(cfg.HandRotationPitch.Value,
                                                cfg.HandRotationYaw.Value,
                                                cfg.HandRotationRoll.Value);

            _detached.Place(left, world, handRotation);

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
