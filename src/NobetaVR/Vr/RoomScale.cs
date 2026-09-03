using UnityEngine;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Walks Nobeta to wherever you have physically walked.
    ///
    /// The view is anchored to her head bone, so the camera already follows her body. That is
    /// what makes this simple: physical movement does not have to be added to the camera at
    /// all. It only has to be handed to the character, and the view comes along because the
    /// bone does.
    ///
    /// It also settles the collision question by construction. The move goes through the game's
    /// own <c>CharacterController</c>, so walls, slopes and steps are resolved by the game
    /// exactly as they are for stick movement. When a wall refuses the step the character stops
    /// and, because the view is on the character rather than on your head, the view stops with
    /// her — you cannot walk your head through geometry.
    /// </summary>
    internal static class RoomScale
    {
        /// <summary>
        /// How much physical movement has already been handed to the character, in world space.
        ///
        /// This tracks intent rather than outcome. If a wall blocked the step, the character did
        /// not travel that far, but the amount is still counted as spent — otherwise the mod
        /// would push against the wall again every frame. Stepping back produces a negative
        /// delta from the same bookkeeping and she returns correctly.
        /// </summary>
        private static Vector3 _spent;

        public static void Reset() => _spent = Vector3.zero;

        public static void Apply(WizardGirlManage girl, Quaternion viewYaw)
        {
            if (!Plugin.Instance.RoomScale.Value) return;
            if (girl == null) return;

            var controller = girl.characterController;
            if (controller == null || !controller.enabled) return;

            // The neck pivot, not the eyes -- see HeadPose.Neck. Walking the eyes led the
            // character around a small circle every time the player turned on the spot.
            //
            // Horizontal only. Crouching and standing move the view, not the body: there is no
            // sensible thing to do with vertical motion through a character controller, and
            // feeding it in would fight gravity every frame.
            var room = HeadPose.Neck;
            room.y = 0f;

            var want = viewYaw * room;
            var delta = want - _spent;

            var step = delta.magnitude;
            if (step < 0.001f) return;

            // A tracking glitch, a runtime hiccup or a recentre missed somewhere else can
            // produce an enormous delta. Teleporting the character across the level on the
            // strength of one bad sample is not worth the honesty of applying it.
            var max = Plugin.Instance.RoomScaleMaxStep.Value;
            if (step > max)
            {
                Plugin.Log.LogWarning($"ignoring a {step:F2} m room-scale step (limit {max:F2} m); "
                                    + "treating it as a tracking glitch and recentring on it.");
                _spent = want;
                return;
            }

            controller.Move(delta);
            _spent = want;
        }
    }
}
