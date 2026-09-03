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

        /// <summary>Neck pivot in tracking space, from the current raw pose.</summary>
        private static Vector3 RawNeck()
        {
            var cfg = Plugin.Instance;
            // Down and back from the eyes, in head space: forward is +Z, so behind is -Z.
            return Raw + Rotation * new Vector3(0f, -cfg.NeckModelDown.Value, -cfg.NeckModelBack.Value);
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
        /// Position only. Recentring the yaw as well would fight the game's camera, which is
        /// what the view's yaw comes from and what the turn control drives; the head's rotation
        /// is added on top of that and has no origin of its own to reset.
        /// </summary>
        public static void Recenter()
        {
            _origin = Raw;
            _neckOrigin = RawNeck();
            _originSet = true;
            Position = Vector3.zero;
            Neck = Vector3.zero;
            RoomScale.Reset();

            // Direction as well as place. Recentring the position alone leaves you standing
            // where Nobeta is but facing wherever you happened to be looking.
            VrCamera.RealignToBody();

            Plugin.Log.LogInfo("recentred");
        }

        /// <summary>
        /// Reset when a stage or a character changes, so the room offset is not carried across
        /// a teleport as a sudden walk.
        /// </summary>
        public static void Forget() => _originSet = false;
    }
}
