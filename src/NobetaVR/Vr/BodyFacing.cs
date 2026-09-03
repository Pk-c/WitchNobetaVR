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
        /// The camera mode the game is in, tracked so this stays out of the way when the game
        /// has taken the camera for its own purposes.
        /// </summary>
        internal static PlayerCamera.CameraMode Mode { get; set; } = PlayerCamera.CameraMode.Normal;

        public static void Apply(PlayerController controller)
        {
            if (!Plugin.Instance.BodyFollowsView.Value || controller == null) return;

            // Only while the player is the one driving. Cutscenes, death and the face-camera
            // mode all place her deliberately, and turning her to face the headset during any
            // of them would be the mod fighting the game over its own staging.
            if (Mode != PlayerCamera.CameraMode.Normal) return;

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
    /// Follows the camera mode. There is no property to read it back from, so it is taken as it
    /// is set.
    /// </summary>
    [HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.SetMode))]
    internal static class PlayerCameraSetModePatch
    {
        private static void Postfix(PlayerCamera.CameraMode camMode)
        {
            if (BodyFacing.Mode == camMode) return;
            BodyFacing.Mode = camMode;
            Plugin.Log.LogInfo($"camera mode -> {camMode}");
        }
    }
}
