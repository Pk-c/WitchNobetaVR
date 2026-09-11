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
        /// Where the shot is going this frame, in world space, or null when nothing is
        /// aiming. Published so the reticle can mark the point without having to work out
        /// which mode found it, or repeat the raycast that did.
        /// </summary>
        internal static Vector3? Target { get; private set; }

        /// <summary>
        /// Called from the postfix on <c>PlayerCamera.Update</c>, after the game has placed its
        /// own aim target for the frame, so this is the value that survives to be used.
        /// </summary>
        public static void Apply(PlayerCamera playerCamera, Transform view)
        {
            Target = null;
            if (playerCamera == null || view == null) return;

            // Not ours to place while the game has her, and that is not only a question of
            // where a shot would go: the game's IK turns her head and chest towards this
            // target, so writing it during a cutscene drags her about by the player's gaze
            // while the scene is trying to act with her. From inside the headset that is the
            // character following your head instead of playing her part, which is the other
            // half of "she should not be movable or follow the camera during cutscenes" —
            // standing the controls down stops her walking, and this stops her looking.
            //
            // It is left where the game put it rather than restored, because the game is
            // placing it itself on exactly these frames: it is the third-person camera's aim
            // target, and a scene owns that camera.
            if (!Plugin.Instance.AimFromView.Value || !PlayerStatus.YoursToDrive)
            {
                // Not ours to place — but still worth reading back, so the reticle marks
                // where the game is aiming whether or not the mod moved it.
                var own = playerCamera.g_AimTarget;
                if (own != null) Target = own.position;
                return;
            }

            var cfg = Plugin.Instance;
            var distance = cfg.AimDistance.Value;

            // From the wand hand when there is one, from the eyes otherwise. Pointing a wand is
            // the more natural of the two once the hand is tracked, and it is the only one that
            // lets you aim somewhere you are not looking; the view remains the fallback for
            // menus, cutscenes and any moment the hands are not being drawn.
            var origin = view.position;
            var direction = view.forward;

            if (cfg.AimFromHand.Value && VrHands.AimOrigin.HasValue)
            {
                origin = VrHands.AimOrigin.Value;
                direction = VrHands.AimDirection;
            }

            // Straight out from the eyes unless something is in the way. Without the cast the
            // target sits behind whatever you are actually pointing at, and a spell aimed at a
            // wall two metres away tries to reach a point ten metres inside it.
            var point = Physics.Raycast(origin, direction, out var hit, distance,
                                        ~0, QueryTriggerInteraction.Ignore)
                ? hit.point
                : origin + direction * distance;

            Target = point;

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
