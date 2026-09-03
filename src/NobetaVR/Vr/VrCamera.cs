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

        private readonly FirstPerson _firstPerson = new();

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

            // A new stage means a new body somewhere else entirely. Carrying the room offset
            // across would replay it as one enormous walk the moment the level loads.
            HeadPose.Forget();
            RoomScale.Reset();

            // The turn control needs the same instance; it owns the camera's yaw.
            var controls = Input.VrControls.Instance;
            if (controls != null) controls.Camera = instance;
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
        /// Where you are actually looking, flattened: the game's yaw with the headset's own
        /// rotation folded in.
        ///
        /// This is what the body must follow. Turning physically rotates the headset and
        /// nothing else — the game's camera yaw only moves when the turn control moves it — so
        /// a body that follows <see cref="ViewYaw"/> alone ignores every physical turn you
        /// make, which is exactly the room-scale rotation that went missing.
        /// </summary>
        internal static Vector3 ViewForwardFlat { get; private set; } = Vector3.forward;

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

            if (Plugin.Instance.ApplyHeadPose.Value)
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

            // If the head bone is not loaded yet, first person declines and the boom pose
            // stands, so a stage opens in third person for a few frames rather than snapping
            // somewhere wrong.
            if (Plugin.Instance.FirstPerson.Value
                && _firstPerson.GetOrigin(_gameRot, out var fpPos, out var fpRot))
            {
                viewPos = fpPos;
                viewRot = fpRot;
            }

            ViewYaw = viewRot;

            var lookForward = viewRot * headRot * Vector3.forward;
            lookForward.y = 0f;
            if (lookForward.sqrMagnitude > 0.0001f) ViewForwardFlat = lookForward.normalized;

            _target.rotation = viewRot * headRot;
            _target.position = viewPos + viewRot * headPos;

            _writtenPos = _target.position;
            _writtenRot = _target.rotation;
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
            var bursting = Time.unscaledTime < _burstUntil;
            var every = bursting ? 0.5f : Plugin.Instance.HeadPoseLogSeconds.Value;
            if (every <= 0f || Time.unscaledTime < _nextPoseLog) return;
            _nextPoseLog = Time.unscaledTime + every;

            var e = rot.eulerAngles;
            Plugin.Log.LogInfo($"head  pos ({pos.x:F3}, {pos.y:F3}, {pos.z:F3})  "
                             + $"rot ({e.x:F1}, {e.y:F1}, {e.z:F1})"
                             + (bursting ? "" : "  [config]"));
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
