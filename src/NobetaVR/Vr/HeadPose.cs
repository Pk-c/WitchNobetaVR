using UnityEngine;
using UnityEngine.XR;

namespace NobetaVR.Vr
{
    /// <summary>
    /// The headset pose, sampled once per frame and expressed relative to a recentre point the
    /// mod owns.
    ///
    /// Two things read it — the camera, for where to put the view, and room-scale, for how far
    /// you have physically walked — and they must agree to the millimetre or the body and the
    /// view drift apart. Sampling once and sharing the result is what guarantees that; two
    /// independent reads of <c>InputTracking</c> in the same frame would not necessarily return
    /// the same value.
    ///
    /// Recentring is done here rather than through the runtime. <c>TryRecenter</c> is optional
    /// in OpenXR and refused outright by some runtimes, and even where it works it resets a
    /// notion of origin the mod does not control. Subtracting our own origin works everywhere
    /// and, more usefully, lets recentring mean exactly "put my head back where Nobeta's is",
    /// which is the thing actually wanted.
    /// </summary>
    internal static class HeadPose
    {
        /// <summary>Raw headset position, relative to the runtime's tracking origin.</summary>
        public static Vector3 Raw { get; private set; }

        /// <summary>Headset rotation. Never recentred: turning physically must turn the view.</summary>
        public static Quaternion Rotation { get; private set; } = Quaternion.identity;

        /// <summary>
        /// The yaw the last recentre took out of the headset, as a rotation to put in front of
        /// it.
        ///
        /// <see cref="Rotation"/> above is right that the headset's own yaw is never taken
        /// away: in a stage the view's direction belongs to the game's camera, the turn control
        /// drives that, and subtracting a yaw here would fight both. But that owner has to
        /// exist. On the title screen and any other screen with no <c>PlayerCamera</c> the view
        /// is the scene's own camera with the headset laid straight on top, so which way you
        /// face is decided by which way you happened to be standing when the runtime came up —
        /// which is how you arrive at the menu looking off to one side with nothing that puts
        /// it right, since recentring there has no body to align to either.
        ///
        /// This is that missing owner, and only there. <see cref="VrCamera"/> applies it on the
        /// frames nothing else claims the yaw, so a recentre means "straight ahead" on a menu
        /// and "back on Nobeta" in a stage, which is the same request answered by whatever the
        /// screen has to offer.
        /// </summary>
        public static Quaternion MenuYaw { get; private set; } = Quaternion.identity;

        /// <summary>Eye position relative to the last recentre.</summary>
        public static Vector3 Position { get; private set; }

        /// <summary>
        /// Neck-pivot position relative to the last recentre: where your head actually turns
        /// about, rather than where your eyes happen to be.
        ///
        /// Room-scale must use this. The tracked point is the headset, but you do not rotate
        /// about your eyes — you rotate about your neck, and the headset sits some way in front
        /// of and above that pivot. Turning on the spot therefore sweeps the headset around a
        /// circle of a good ten centimetres, and nothing downstream can tell that circle from
        /// walking: the character is quietly led around it. The tell is that the error closes
        /// exactly on a full turn, because the circle does.
        /// </summary>
        public static Vector3 Neck { get; private set; }

        /// <summary>
        /// The eyes' offset from the neck pivot, horizontally. What the body does not absorb,
        /// the camera keeps, so leaning and looking still move the view relative to the body
        /// instead of being flattened away.
        /// </summary>
        public static Vector3 EyesFromNeck { get; private set; }

        private static Vector3 _origin;
        private static Vector3 _neckOrigin;
        private static bool _originSet;

        /// <summary>Neck pivot in tracking space, for a raw pose.</summary>
        private static Vector3 RawNeck(Vector3 raw, Quaternion rotation)
        {
            var cfg = Plugin.Instance;
            // Down and back from the eyes, in head space: forward is +Z, so behind is -Z.
            return raw + rotation * new Vector3(0f, -cfg.NeckModelDown.Value, -cfg.NeckModelBack.Value);
        }

        /// <summary>Neck pivot in tracking space, from the current raw pose.</summary>
        private static Vector3 RawNeck() => RawNeck(Raw, Rotation);

        /// <summary>
        /// Reads the headset again, without touching anything.
        ///
        /// For the render-time latch and for nothing else. The frame's own sample is what the
        /// body, the aim and room-scale are all built from, and they have to agree with each
        /// other rather than with the newest reading available -- a second commit part way
        /// through a frame would hand room-scale a step the character had already been given.
        /// So this derives the same values against the same origin and commits none of
        /// them; the caller uses the result to place a camera and then throws it away.
        ///
        /// The untouched tracking-space reading comes back too. Anything placed against the
        /// view has to measure its offset from the same headset reading the view was built
        /// from -- the hands are the case, and measuring them from the frame's committed
        /// sample while the camera sits on this one would put the head's own movement into
        /// them twice.
        ///
        /// Returns false before the origin has been established, which is the frame XR comes
        /// up on and no other.
        /// </summary>
        public static bool Peek(out Vector3 position, out Quaternion rotation,
                                out Vector3 eyesFromNeck, out Vector3 raw)
        {
            position = default;
            rotation = Quaternion.identity;
            eyesFromNeck = default;
            raw = default;

            if (!_originSet) return false;

            raw = InputTracking.GetLocalPosition(XRNode.CenterEye);
            rotation = InputTracking.GetLocalRotation(XRNode.CenterEye);

            position = raw - _origin;
            var neck = RawNeck(raw, rotation) - _neckOrigin;
            eyesFromNeck = new Vector3(position.x - neck.x, 0f, position.z - neck.z);
            return true;
        }

        private static int _sampledFrame = -1;

        public static void Sample()
        {
            // Once per frame, whoever asks first. Both callers are on the main thread, and
            // sampling twice would not be wrong so much as make "they agree by construction"
            // untrue, which is the only reason this class exists.
            if (_sampledFrame == Time.frameCount) return;
            _sampledFrame = Time.frameCount;

            Raw = InputTracking.GetLocalPosition(XRNode.CenterEye);
            Rotation = InputTracking.GetLocalRotation(XRNode.CenterEye);

            // The first sample establishes the origin, so a session that never recentres still
            // starts with the view on Nobeta rather than wherever the headset happened to be
            // when the runtime came up.
            if (!_originSet)
            {
                _originSet = true;
                _origin = Raw;
                _neckOrigin = RawNeck();
            }

            Position = Raw - _origin;
            Neck = RawNeck() - _neckOrigin;

            // Bounded by the neck offset and identical for identical head orientations, so this
            // can never accumulate the way the raw position did.
            EyesFromNeck = new Vector3(Position.x - Neck.x, 0f, Position.z - Neck.z);
        }

        /// <summary>
        /// Puts the head back on Nobeta.
        ///
        /// Position, and the yaw only where nothing else owns it. Recentring the headset's yaw
        /// in a stage would fight the game's camera, which is what the view's yaw comes from
        /// and what the turn control drives. On a menu there is no such camera and no body
        /// either, so the yaw is recorded in <see cref="MenuYaw"/> for the view to subtract
        /// there and nowhere else.
        /// </summary>
        public static void Recenter()
        {
            _origin = Raw;
            _neckOrigin = RawNeck();
            _originSet = true;
            Position = Vector3.zero;
            Neck = Vector3.zero;
            RoomScale.Reset();

            // The yaw for the screens with no body to face. Taken from where you are looking
            // now, so that whatever the scene's camera was framing is what you are pointed at
            // once this is subtracted. Kept across a stage, because it is not read there and
            // the value the player last asked for is the better answer than identity if the
            // automatic recentre on the way back to the title is switched off.
            var forward = Ui.ViewAnchor.YawForward(Rotation, Vector3.forward);
            MenuYaw = Quaternion.Inverse(Quaternion.LookRotation(forward, Vector3.up));

            // Direction as well as place. Recentring the position alone leaves you standing
            // where Nobeta is but facing wherever you happened to be looking.
            VrCamera.RealignToBody();
        }

        /// <summary>
        /// Reset when a stage or a character changes, so the room offset is not carried across
        /// a teleport as a sudden walk.
        /// </summary>
        public static void Forget() => _originSet = false;
    }
}
