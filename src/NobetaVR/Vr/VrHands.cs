using System;
using NobetaVR.Input;
using UnityEngine;
using UnityEngine.XR;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Puts Nobeta's hands where your controllers are.
    ///
    /// The controller pose arrives in tracking space, the same space the headset pose does, so
    /// the two are related by simple subtraction: a controller's offset from the headset is the
    /// hand's offset from the eyes. That offset is turned into the world by the view's yaw and
    /// hung off Nobeta's head bone — not off the camera, which carries the player's comfort
    /// offsets and would put her hands above her own shoulders.
    ///
    /// It is also scaled. She is a child and you are not, so an unscaled offset asks for a hand
    /// further out than her arm can put one, and the solver has nothing to do but lock the arm
    /// straight.
    ///
    /// Runs in LateUpdate, after the animator has posed the skeleton. The solver corrects the
    /// animated pose rather than replacing it, so everything the game animates that the arms are
    /// not doing still happens.
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

            /// <summary>
            /// The hand's orientation relative to the forearm, in the animated pose. Used when
            /// the wrist is left to the game: it is the one relationship that is always
            /// anatomically right, whatever the arm is doing.
            /// </summary>
            public Quaternion RestRelativeToFore = Quaternion.identity;

            public bool Valid => Upper != null && Fore != null && Hand != null;
        }

        private readonly Arm _left = new();
        private readonly Arm _right = new();

        private readonly DetachedHands _detached = new();

        /// <summary>
        /// Where the wand hand is and which way it points, in world space, or null when hand
        /// tracking is not running. Published so aiming can come from the hand rather than the
        /// head without either of them having to know about the other.
        /// </summary>
        internal static Vector3? AimOrigin { get; private set; }
        internal static Vector3 AimDirection { get; private set; } = Vector3.forward;

        private Transform _boundRoot;
        private bool _reported;

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
            if (!Plugin.Instance.HandTracking.Value) return;

            var controls = VrControls.Instance;
            if (controls == null || controls.Camera == null) return;

            var girl = controls.Camera.wizardGirl;
            if (girl == null) return;

            if (!Bind(girl.transform)) return;

            var head = VrCamera.HeadBone;
            if (head == null) return;

            // Give her her arms back whenever the game has taken the camera. A cutscene, a
            // death, the face-camera mode: in every one of them she is being framed and acted
            // deliberately, often close up, and an armless Nobeta gesturing through a
            // conversation is not a trade worth making for hands nobody is holding.
            if (BodyFacing.Mode != PlayerCamera.CameraMode.Normal)
            {
                Release();
                return;
            }

            YieldTheArms(girl);
            SetFinalIkRestoring(girl.transform, false);

            AimOrigin = null;

            if (Plugin.Instance.DetachedHands.Value)
            {
                if (!_detached.Attached)
                    _detached.Attach(_left.Upper, _left.Hand, _right.Upper, _right.Hand);

                PlaceDetached(_left, XRNode.LeftHand, true);
                PlaceDetached(_right, XRNode.RightHand, false);
                return;
            }

            if (_detached.Attached) _detached.Detach();

            Apply(_left, XRNode.LeftHand, head, girl.transform, -1f);
            Apply(_right, XRNode.RightHand, head, girl.transform, 1f);
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
            if (!Plugin.Instance.StopFinalIkFixTransforms.Value) return;
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
            if (!Plugin.Instance.DisableGameAimIk.Value) return;

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
                _left.RestRelativeToFore = Quaternion.Inverse(_left.Fore.rotation) * _left.Hand.rotation;
            }
            if (_right.Valid)
            {
                _right.RestRelativeToBody = Quaternion.Inverse(root.rotation) * _right.Hand.rotation;
                _right.RestRelativeToFore = Quaternion.Inverse(_right.Fore.rotation) * _right.Hand.rotation;
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
            // parent. Deriving it this way also verifies the ancestry the solver depends on: if
            // the walk reaches the root without meeting the upper arm, these are not one chain
            // and there is nothing safe to solve.
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

        private static float _nextDiagnostic;

        /// <summary>
        /// Reports what the solver is actually being asked for.
        ///
        /// Four attempts at these arms have each been a hypothesis tested by putting a headset
        /// on, and three were wrong. These are the numbers that separate the remaining
        /// candidates without another round of that: if the target distance keeps exceeding the
        /// arm's own length, the reach scale is still too generous and the arm is locking out;
        /// if the bone scales are not uniform, setting world rotations on them shears the mesh
        /// and no amount of correct geometry will help.
        /// </summary>
        private static void Diagnose(Arm arm, Vector3 target, float side)
        {
            if (!Plugin.Instance.HandDiagnostics.Value) return;
            if (side < 0f) return;                       // one hand is enough
            if (Time.unscaledTime < _nextDiagnostic) return;
            _nextDiagnostic = Time.unscaledTime + 1f;

            var shoulder = arm.Upper.position;
            var upperLength = Vector3.Distance(shoulder, arm.Fore.position);
            var foreLength = Vector3.Distance(arm.Fore.position, arm.Hand.position);
            var wanted = Vector3.Distance(shoulder, target);
            var limit = upperLength + foreLength;

            Plugin.Log.LogInfo(
                $"arm: upper {upperLength:F3} + fore {foreLength:F3} = {limit:F3} m, "
              + $"target {wanted:F3} m {(wanted > limit ? "OUT OF REACH" : "ok")}; "
              + $"scales upper {arm.Upper.lossyScale} fore {arm.Fore.lossyScale} hand {arm.Hand.lossyScale}");
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

            Plugin.Log.LogInfo($"{side} arm chain: {path}   (solving {arm.Upper.name} and {arm.Fore.name})");
        }

        private void OnDisable() => Release();

        /// <summary>
        /// Hands the skeleton back to the game, in full: bones reparented, and FinalIK allowed
        /// to restore poses again. Used when the component goes away and whenever the game takes
        /// over the staging, so nothing the mod did outlives its turn.
        /// </summary>
        private void Release()
        {
            if (_detached.Attached) _detached.Detach();
            if (_boundRoot != null) SetFinalIkRestoring(_boundRoot, true);
        }

        /// <summary>
        /// One hand, one to one, exactly where the controller is.
        ///
        /// Anchored on the camera rather than on her head bone, and unscaled, which is the
        /// opposite of what the IK path wants and right for the same reason: with the arms gone
        /// there is no reach to run out of, so the hand can simply be where your hand is. That
        /// correspondence is the whole point — you reach for something and the hand is there,
        /// with nothing in between that could be off.
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
            var controllerWorld = VrCamera.ViewYaw * rotation;
            var world = camera.position + VrCamera.ViewYaw * (position - HeadPose.Raw);

            world += controllerWorld * new Vector3(cfg.HandOffsetSide.Value * (left ? -1f : 1f),
                                                   cfg.HandOffsetUp.Value,
                                                   cfg.HandOffsetForward.Value);

            // The rig's own rest orientation still does the work of matching a controller's
            // convention to a Biped hand bone's axes, exactly as it did for the IK path.
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
                             * Quaternion.Euler(Plugin.Instance.AimPitchOffset.Value, 0f, 0f)
                             * Vector3.forward;
            }
        }

        // -- posing --------------------------------------------------------------------

        private static void Apply(Arm arm, XRNode node, Transform head, Transform body, float side)
        {
            if (!arm.Valid) return;

            var device = InputDevices.GetDeviceAtXRNode(node);
            if (!device.isValid) return;

            if (!InputDevices.TryGetFeatureValue_Vector3f(device.deviceId, "DevicePosition", out var position)
             || !InputDevices.TryGetFeatureValue_Quaternionf(device.deviceId, "DeviceRotation", out var rotation))
                return;

            var cfg = Plugin.Instance;

            // The controller's offset from the headset, in tracking space, is the hand's offset
            // from the eyes. Rotated by the view's yaw rather than by the full camera rotation:
            // the headset's own pitch and roll are already in the camera, and applying them
            // again would swing the hands every time the player looked down.
            var fromHead = position - HeadPose.Raw;

            // Scaled, because Nobeta is a child and you are not. Her arm spans perhaps half of
            // yours, so an unscaled offset asks for a hand well past anywhere she can reach; the
            // solver then clamps to full extension and the arm locks out straight, which is the
            // spike the screenshot showed. Scaling maps your reach onto hers proportionally
            // instead, so the middle of your range lands in the middle of hers.
            var reach = cfg.HandReachScale.Value;
            var world = head.position + VrCamera.ViewYaw * (fromHead * reach);

            var controllerWorld = VrCamera.ViewYaw * rotation;

            // Rig first, taste second. The rest orientation puts the wrist where the animator
            // had it; the Euler values are a small adjustment on top, and are zero by default
            // because with the rig-derived part in place there is nothing left to correct.
            var handRotation = controllerWorld * arm.RestRelativeToBody
                             * Quaternion.Euler(cfg.HandRotationPitch.Value,
                                                cfg.HandRotationYaw.Value,
                                                cfg.HandRotationRoll.Value);

            // A controller is not held where a hand bone sits: the grip is in the palm and the
            // bone is at the wrist. Expressed in the controller's own frame, which is the one
            // you can reason about while wearing the headset. The sideways part is mirrored so
            // one setting serves both hands.
            world += controllerWorld * new Vector3(cfg.HandOffsetSide.Value * side,
                                                   cfg.HandOffsetUp.Value,
                                                   cfg.HandOffsetForward.Value);

            // Anchoring on the head bone rather than on the camera is deliberate. The camera
            // carries HeadOffset, the player's own comfort adjustment -- 23 cm of it in the
            // tuned defaults -- and hanging her hands off that would place them a hand's width
            // above her shoulders, which is exactly where an arm cannot go.

            // Which way the elbow points, in the body's own frame: out from the chest, down,
            // and back. Taken from the body rather than from the current pose on purpose — the
            // pose puts shoulder, elbow and wrist almost in a line while she stands at rest, and
            // a direction derived from three nearly collinear points is noise.
            var pole = body.rotation * new Vector3(side * 0.35f, -0.60f, -0.70f);

            // The wrist is left to the game by default.
            //
            // Twisting it from the controller is the obvious thing to want and the thing that
            // wrecks the arm: forearm vertices are weighted partly to the hand bone, so a large
            // wrist angle wrings the mesh -- the "candy wrapper" -- and from inside the headset
            // that is indistinguishable from the arm itself being broken. Keeping the animated
            // hand-to-forearm relationship is always anatomically right, and it means the arm
            // can be judged on its own. Turn HandFollowRotation on once it looks right.
            var wrist = cfg.HandFollowRotation.Value
                ? handRotation
                : arm.Fore.rotation * arm.RestRelativeToFore;

            TwoBoneIk.Solve(arm.Upper, arm.Fore, arm.Hand, world, wrist, pole,
                            arm.RestRelativeToFore, cfg.ForearmTwistShare.Value);

            Diagnose(arm, world, side);
        }
    }
}
