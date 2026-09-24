using UnityEngine;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Puts a shot the game frames in front of the player, rather than in front of whoever
    /// happens to be standing where they were last looking.
    ///
    /// In a stage the view's yaw is the game camera's with the headset's own laid on top, and
    /// that is right for as long as the player owns both halves: a physical turn moves the
    /// headset, the body follows the view, and the two stay one direction. What it also means
    /// is that the headset's yaw is an offset the player is free to accumulate, and half a turn
    /// of it is an ordinary way to be standing after a fight in a room they backed into.
    ///
    /// A cutscene then arrives with a yaw of its own, authored for a screen, and that offset is
    /// still sitting on top of it: the shot is framed exactly as intended and presented over
    /// the player's shoulder. Reported as "the camera is often 180 degrees turned".
    ///
    /// So while the game is framing, the offset comes off. The yaw held here is the inverse of
    /// the headset's at the moment the shot was taken, so the shot's own forward is where the
    /// player's nose already points — and everything the head does afterwards still moves the
    /// view. It is an alignment, not a lock: the one thing a view you cannot control must keep
    /// is your own neck.
    ///
    /// <para>
    /// Retaken at every cut, because one alignment does not carry a scene. The player looks
    /// around during a shot, as they should, and the next shot would otherwise inherit whatever
    /// they happened to be looking at when it began. A cut is also the one moment a yaw snap
    /// costs nothing: the image is being replaced wholesale, so there is no world to be seen
    /// turning. What counts as a cut is the game camera moving further in one frame than any
    /// sweep or dolly could — a fifth of a turn, or a metre and a half — plus every frame the
    /// view is being held black, where the realignment is not merely comfortable but invisible.
    /// </para>
    ///
    /// Nothing here touches the game. The game's camera keeps its own yaw, its own framing and
    /// its own travel; only the frame the headset is read against moves.
    /// </summary>
    internal static class ShotFacing
    {
        /// <summary>
        /// The yaw to put in front of the headset while the game frames a shot, as a rotation
        /// the view is built with. Identity until a shot has been taken, and while the setting
        /// is off, so a caller can apply it unconditionally.
        /// </summary>
        internal static Quaternion Yaw { get; private set; } = Quaternion.identity;

        /// <summary>
        /// How far the game's camera has to turn in a single frame to be a cut rather than a
        /// sweep. A fifth of a turn in one frame is over a thousand degrees a second; the
        /// briskest authored sweep in the game is two orders of magnitude below that.
        /// </summary>
        private const float CutDegrees = 20f;

        /// <summary>
        /// And how far it has to travel, in metres. A dolly runs at a few metres a second,
        /// which is centimetres a frame — so this only catches the camera being put somewhere
        /// else rather than taken there.
        /// </summary>
        private const float CutMetres = 1.5f;

        /// <summary>How black the view has to be for a realignment to be entirely unseen.</summary>
        private const float Black = 0.8f;

        private static bool _held;
        private static Vector3 _wasForward = Vector3.forward;
        private static Vector3 _wasPosition;

        /// <summary>
        /// Asks for the current shot to be put back in front of the player on the next frame.
        ///
        /// The player's own way out, on both grips — the gesture that recentres during play.
        /// Recentring itself cannot run during a staged shot: half of what it does is write the
        /// camera's own yaw, and that yaw belongs to the game for as long as it is framing. So
        /// the gesture means the nearest thing that is ours to give.
        /// </summary>
        internal static void Realign() => _held = false;

        /// <summary>
        /// Drops the alignment, for the frames the view is the player's again. Called every
        /// frame the view sits in her head, so a cutscene always opens by taking a fresh one
        /// rather than reusing a shot two scenes old.
        /// </summary>
        internal static void Forget()
        {
            _held = false;
            Yaw = Quaternion.identity;
        }

        /// <summary>
        /// Keeps the alignment for this frame, and takes a new one whenever the shot changes.
        /// </summary>
        /// <param name="gameRot">The rotation the game gave its camera this frame.</param>
        /// <param name="gamePos">And where it put it, for the cut that is a move rather than a turn.</param>
        /// <param name="headRot">The headset, as the view is about to be built from it.</param>
        internal static void Update(Quaternion gameRot, Vector3 gamePos, Quaternion headRot)
        {
            if (!Plugin.Instance.AlignCutscenesToView.Value) { Forget(); return; }

            var forward = gameRot * Vector3.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : _wasForward;

            // A fade counts as a cut for as long as it lasts, rather than only on the frame it
            // starts. The screen being black is the cheapest realignment there is, and a
            // transition is exactly where a player turns to look at something else.
            var black = ViewFade.Amount > Black;

            var cut = !_held
                   || black
                   || Vector3.Angle(forward, _wasForward) > CutDegrees
                   || (gamePos - _wasPosition).sqrMagnitude > CutMetres * CutMetres;

            _wasForward = forward;
            _wasPosition = gamePos;

            if (!cut) return;

            _held = true;

            // The nose rather than the flattened gaze, for the reason ViewAnchor exists: a
            // player looking down at the floor when the cut lands has almost no horizontal
            // forward left, and what survives flattening it is mostly the roll their neck is
            // carrying.
            var nose = Ui.ViewAnchor.YawForward(headRot, Vector3.forward);
            Yaw = Quaternion.Inverse(Quaternion.LookRotation(nose, Vector3.up));
        }
    }
}
