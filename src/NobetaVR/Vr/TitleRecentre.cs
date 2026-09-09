using UnityEngine;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Puts your head straight the moment the title screen comes up.
    ///
    /// The title screen is the one place the mod has nothing to point you at. In a stage the
    /// view is built on Nobeta — her facing when the game is moving her, the camera's yaw when
    /// it is yours — so however you are standing physically, forward is a direction the game
    /// decided. The title has no body and no <c>PlayerCamera</c>, so the scene's camera is
    /// taken as it is and the headset laid straight on top of it: you face wherever you
    /// happened to be facing when the runtime fixed its tracking origin, which for a player
    /// who put the headset on sideways, or who has been turning on the spot in the stage they
    /// just quit, is not the front of the menu.
    ///
    /// <para>
    /// The fix is the recentre the player would otherwise reach for, taken for them at the one
    /// moment it is unambiguously wanted. Nothing is in motion on the title, nothing is aiming,
    /// and the screen is the natural place to settle into the headset — so unlike a recentre
    /// during play there is no state it can interrupt.
    /// </para>
    /// </summary>
    internal static class TitleRecentre
    {
        /// <summary>The title screen's scene name. See <c>docs/GAME-NOTES.md</c>.</summary>
        private const string Title = "Title";

        /// <summary>
        /// How long after the scene becomes active the recentre is taken, in seconds.
        ///
        /// Not on the frame itself. The scene becomes active behind a fade, and the head is
        /// still moving then — the player is looking away from a stage that just ended, or
        /// following the loading screen down. Half a second later the fade is still running and
        /// the head has stopped, which is both late enough to read a settled pose and early
        /// enough that the correction happens where nobody can see it move.
        /// </summary>
        private const float Settle = 0.5f;

        private static string _scene;
        private static float _due = -1f;

        /// <summary>
        /// Once a frame from <see cref="VrCamera"/>, whatever scene is up.
        ///
        /// Called even in a stage, where it does nothing but watch the name go by. That is the
        /// point: the arming below is a change of scene, and a tick that only ran on the title
        /// would still be holding the name of the last title screen when the player came back
        /// to a second one — so the change would never be seen and the recentre would happen
        /// exactly once per session.
        /// </summary>
        internal static void Tick()
        {
            var active = ActiveScene.Name;
            if (active != _scene)
            {
                _scene = active;
                _due = active == Title ? Time.unscaledTime + Settle : -1f;
            }

            if (_due < 0f || Time.unscaledTime < _due) return;
            _due = -1f;

            // Read when it fires rather than when it is armed, so turning the setting off
            // during the half second it waits is honoured rather than ignored.
            if (!Plugin.Instance.RecentreOnTitle.Value) return;

            // The frame's own sample, in case nothing else has asked for one yet: the view is
            // placed after this, and recentring against the previous frame's reading would
            // leave the difference between the two frames in the result.
            HeadPose.Sample();

            Plugin.Log.LogInfo("the title screen is up, so the view is put straight ahead");
            HeadPose.Recenter();
        }
    }
}
