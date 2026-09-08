using System.Collections.Generic;

namespace NobetaVR.Vr
{
    /// <summary>
    /// What the game currently has Nobeta doing, read live rather than cached.
    ///
    /// The camera mode was the mod's only reading of "the game has her", and it is not enough.
    /// Dying is four beats and only the first is <c>Dead</c>: the fall, the stage reloading, her
    /// slumped against the save statue, and her standing up out of it. The last two run in
    /// <c>Normal</c>, and the game reports her controllable through them — it holds her with the
    /// state machine instead — so anything gated on the camera mode or on <c>controllable</c>
    /// alone handed the player back a character who was still sitting down.
    ///
    /// <c>NobetaState</c> is the reading that covers it. It is also the only one that can tell a
    /// scripted get-up from ordinary play, which is why turning and the hands stand down on it
    /// too: swinging your arms and spinning the world while she gets to her feet is the game and
    /// the player driving the same body at once.
    ///
    /// Nothing here is stored between frames. A state cached across a stage reload is a state
    /// belonging to a character who no longer exists.
    /// </summary>
    internal static class PlayerStatus
    {
        /// <summary>
        /// The states in which she is the game's rather than the player's, whatever the camera
        /// mode and the controllable flag happen to say.
        ///
        /// <c>StandUp</c> is in here for the knockdown as well as the respawn — the same
        /// argument applies to both, and it ends when the animation does either way.
        /// </summary>
        private static readonly HashSet<NobetaState> Down = new()
        {
            NobetaState.Dead,
            NobetaState.DeadEnd,
            NobetaState.FallDead,
            NobetaState.Resurrection,
            NobetaState.Wake,
            NobetaState.StandUp,
        };

        /// <summary>The states that mean she is down for good rather than getting up.</summary>
        private static readonly HashSet<NobetaState> Dying = new()
        {
            NobetaState.Dead,
            NobetaState.DeadEnd,
            NobetaState.FallDead,
        };

        private static PlayerController Controller()
        {
            var controls = Input.VrControls.Instance;
            var camera = controls != null ? controls.Camera : null;
            var girl = camera != null ? camera.wizardGirl : null;
            return girl != null ? girl.playerController : null;
        }

        /// <summary>Her current state, or null when there is no character to ask.</summary>
        internal static NobetaState? State
        {
            get
            {
                var controller = Controller();
                if (controller == null) return null;

                var state = controller.state;
                Note(state);
                return state;
            }
        }

        /// <summary>
        /// Whether the game is putting her through something the player does not drive: dying,
        /// waking at a save point, or getting back to her feet. False when there is no character
        /// — a menu or a loading screen is not a get-up.
        /// </summary>
        internal static bool DownOrGettingUp
        {
            get
            {
                var state = State;
                return state.HasValue && Down.Contains(state.Value);
            }
        }

        /// <summary>Whether she is dying or dead, as opposed to on her way back up.</summary>
        internal static bool Dead
        {
            get
            {
                var state = State;
                return state.HasValue && Dying.Contains(state.Value);
            }
        }

        /// <summary>
        /// The game's own controllable flag, and <c>false</c> when there is nothing to ask.
        ///
        /// The missing case is the cautious one on purpose. Its one caller holds the view back
        /// out of her head while this is false, and the moment it has to survive is precisely
        /// the one where there is no character at all: the stage reloading between the death
        /// and the save statue. Answering "yes, she is yours" for a character who does not
        /// exist put the view back in an empty head for the length of a level load.
        /// </summary>
        internal static bool Controllable
        {
            get
            {
                var controller = Controller();
                var runtime = controller != null ? controller.runtimeData : null;
                return runtime != null && runtime.controllable;
            }
        }

        /// <summary>
        /// Whether she is the player's to drive this frame: an ordinary camera mode, the game's
        /// own controllable flag, and none of the states the game holds her through.
        ///
        /// All three, because each is true on its own somewhere the other two are not. The mode
        /// is `Normal` while she sits slumped against a save pillar; `controllable` is true
        /// there as well, and false during a conversation the mode calls ordinary; and the state
        /// machine is the only one of the three that can tell a scripted get-up from play.
        /// Anything that turns her body or takes her facing wants all three answered at once,
        /// which is why the question is asked here rather than assembled again at each caller.
        /// </summary>
        internal static bool YoursToDrive =>
            BodyFacing.Mode == PlayerCamera.CameraMode.Normal
            && Controllable
            && !DownOrGettingUp;

        private static readonly HashSet<NobetaState> Seen = new();

        /// <summary>
        /// Names each state the first time it is seen, and never again.
        ///
        /// Bounded by the enum, so it cannot run away, and it is the only way to find out what
        /// the game actually calls a moment: the set above was assembled from the enum's names,
        /// and a name is a guess until the log shows it under the character's feet. If a
        /// scripted moment still hands the player controls they should not have, the state it
        /// runs in is in this list.
        /// </summary>
        private static void Note(NobetaState state)
        {
            if (!Seen.Add(state)) return;
            Plugin.Log.LogInfo($"player state seen for the first time: {state}");
        }
    }
}
