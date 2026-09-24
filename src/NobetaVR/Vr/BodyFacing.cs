using HarmonyLib;
using UnityEngine;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Turns Nobeta to face wherever you are looking.
    ///
    /// This is what makes a side step a side step. The game moves her relative to the camera
    /// and then rotates her to face the direction she is travelling, which is right on a
    /// monitor and wrong in a headset: pushing the stick left made her turn left and walk off,
    /// when what was wanted was a step to the left with her body still facing forward. Keep her
    /// facing the view and the same camera-relative movement becomes strafing, with no change
    /// to the movement code at all.
    ///
    /// The facing goes through the game's own <c>MoveController.LookAt</c> rather than by
    /// writing to the transform. That path carries the game's rotation smoothing
    /// (<c>rotationDragFactor</c>), so she turns the way she always has instead of snapping,
    /// and nothing downstream has to be told the transform was tampered with.
    /// </summary>
    internal static class BodyFacing
    {
        /// <summary>
        /// The camera mode the game is in, read from the live camera every time it is asked
        /// for, so that this stays out of the way when the game has taken the camera for its
        /// own purposes.
        ///
        /// This used to be a cached value written from a postfix on <c>SetMode</c>, and that
        /// is a trap. <c>PlayerCamera</c> is built fresh for each stage, and a new one is
        /// never told to be `Normal` — `Normal` is what it already is, so nothing calls
        /// <c>SetMode</c> on the way in. The cache therefore kept whatever the *previous*
        /// stage last set, across a load that had thrown that camera away. A cutscene ending
        /// in a stage change left it on `Dead`, and everything that reads this — the hands,
        /// the melee swing, the reticle, the facing below — stood down for the rest of the
        /// session, with nothing in the log after the last transition to say why.
        ///
        /// The mode is a field on the camera and it is there to be read, so read it: a value
        /// that is never stored cannot go stale. Missing is `Normal` rather than a nullable
        /// answer every caller would have to spell out — no camera means a menu or a loading
        /// screen, where nothing here applies and the callers have their own reasons to stand
        /// down anyway.
        /// </summary>
        internal static PlayerCamera.CameraMode Mode
        {
            get
            {
                var controls = Input.VrControls.Instance;
                var camera = controls != null ? controls.Camera : null;
                if (camera == null) return PlayerCamera.CameraMode.Normal;

                return camera.cameraMode;
            }
        }

        public static void Apply(PlayerController controller)
        {
            if (!Plugin.Instance.BodyFollowsView.Value || controller == null) return;

            // Only while the player is the one driving. Cutscenes, death and the face-camera
            // mode all place her deliberately, and turning her to face the headset during any
            // of them would be the mod fighting the game over its own staging.
            //
            // The camera mode alone does not cover it. Waking against a save pillar and standing
            // up out of it both run in `Normal` with the game reporting her controllable, and
            // the state machine is what holds her — so turning her there took the get-up over
            // and left her facing the pillar she had her back to. `YoursToDrive` is all three
            // readings at once; see PlayerStatus.
            if (!PlayerStatus.YoursToDrive) return;

            // And not while the view is her own facing rather than the player's. During the
            // ride the two are one value plus whatever the headset adds, so turning her towards
            // the view turns her towards herself and a little further every frame — a body that
            // walks itself round in a slow circle while she is meant to be getting up. See
            // FirstPerson.RideHerFacing.
            if (!VrCamera.ViewIsYours) return;

            // Nor while the view stands back from her at all. Every such moment is the game's
            // or the mod's framing rather than the player's look, and the spawn turn is the one
            // where she also reads as the player's to move — so this is the only reading that
            // stops the half turn at the end of a get-up from taking her round with it.
            if (VrCamera.ViewStandsBack) return;

            var move = controller.moveController;
            if (move == null) return;

            // The whole view, headset included -- not the game's camera yaw on its own. That
            // yaw only changes when the turn control changes it, so following it alone left her
            // deaf to every physical turn: you would rotate on the spot and she would keep
            // facing where you used to look.
            move.LookAt(VrCamera.ViewForwardFlat);
        }
    }

    /// <summary>
    /// Applies the facing once the game has finished its own movement for the frame, so it is
    /// the last word rather than something the game's turn-towards-travel overwrites.
    /// </summary>
    [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.Update))]
    internal static class PlayerControllerUpdatePatch
    {
        private static void Postfix(PlayerController __instance) => BodyFacing.Apply(__instance);
    }

    /// <summary>
    /// Checks that <c>cameraMode</c> really is the field <c>SetMode</c> writes.
    ///
    /// <see cref="BodyFacing.Mode"/> reads that field instead of following the calls, which is
    /// only correct if the two agree. It cannot be checked by reading the code — an IL2CPP
    /// interop assembly carries signatures and no method bodies — so it is checked against the
    /// running game instead, and only a disagreement says anything. Should this ever fire, the
    /// mode gate is watching the wrong field, and the symptom would be hands that come and go
    /// for reasons nothing else in the log explains.
    /// </summary>
    [HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.SetMode))]
    internal static class PlayerCameraSetModePatch
    {
        private static bool _warned;

        private static void Postfix(PlayerCamera __instance, PlayerCamera.CameraMode camMode)
        {
            if (_warned || __instance == null || __instance.cameraMode == camMode) return;

            _warned = true;
            Plugin.Log.LogWarning($"SetMode({camMode}) left cameraMode at {__instance.cameraMode}: "
                                + "BodyFacing.Mode is reading the wrong field, so every control "
                                + "gated on the camera mode is now guessing.");
        }
    }
}
