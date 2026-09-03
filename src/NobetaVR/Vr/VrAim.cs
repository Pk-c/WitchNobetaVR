using UnityEngine;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Points the game's aim where you are looking.
    ///
    /// On a monitor the aim target is placed by the third-person camera, out along the line the
    /// camera is looking down, and the player reads where the shot will go from a reticle drawn
    /// on that same line. In the headset both halves of that break at once: the camera is now
    /// inside Nobeta's head so the line has moved, and the reticle is on a HUD panel floating in
    /// front of you rather than painted over the world. The shot goes somewhere defensible and
    /// there is no longer anything on screen that says where.
    ///
    /// So the target is placed on the view's own line instead. The game keeps aiming at the
    /// transform it always aimed at; it is simply somewhere that now means something.
    /// </summary>
    internal static class VrAim
    {
        /// <summary>
        /// Called from the postfix on <c>PlayerCamera.Update</c>, after the game has placed its
        /// own aim target for the frame, so this is the value that survives to be used.
        /// </summary>
        public static void Apply(PlayerCamera playerCamera, Transform view)
        {
            if (!Plugin.Instance.AimFromView.Value) return;
            if (playerCamera == null || view == null) return;

            var distance = Plugin.Instance.AimDistance.Value;
            var origin = view.position;
            var direction = view.forward;

            // Straight out from the eyes unless something is in the way. Without the cast the
            // target sits behind whatever you are actually pointing at, and a spell aimed at a
            // wall two metres away tries to reach a point ten metres inside it.
            var point = Physics.Raycast(origin, direction, out var hit, distance,
                                        ~0, QueryTriggerInteraction.Ignore)
                ? hit.point
                : origin + direction * distance;

            Place(playerCamera.g_AimTarget, point);

            var girl = playerCamera.wizardGirl;
            if (girl != null)
            {
                Place(girl.aimTarget, point);

                var skin = girl.skinController;
                if (skin != null) Place(skin.aimTarget, point);
            }
        }

        /// <summary>
        /// Moves one of the game's aim transforms, leaving its parent and identity alone.
        ///
        /// Deliberately not replaced with a transform of our own: several systems hold
        /// references to these — the camera, the IK, the reticle — and swapping the object would
        /// mean finding all of them. Moving it is enough, and it is what the game does to it too.
        /// </summary>
        private static void Place(Transform target, Vector3 point)
        {
            if (target != null) target.position = point;
        }
    }
}
