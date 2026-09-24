namespace NobetaVR.Vr
{
    /// <summary>
    /// Stops the game hiding Nobeta because its third-person camera is pressed up against her,
    /// while the view is not that camera.
    ///
    /// <c>PlayerCamera.CameraCollision</c> runs every frame in <c>Normal</c>. It sphere-casts
    /// the boom from her back to where the camera wants to be, pulls the camera in to whatever
    /// it hits, and then calls, on her skin's mesh controller:
    /// <code>EnableAllParts(isAimReady || cameraLocalPosition.magnitude >= threshold)</code>
    /// That goes through <c>UpdateAppearance</c> and switches off every renderer she has: body,
    /// hair, cape, hat, bag. On a monitor that is right. A camera jammed into her back would
    /// otherwise show the inside of her coat.
    ///
    /// <para>
    /// In first person the boom is still computed underneath, because the game still owns that
    /// camera, but nobody is looking down it. So any time a wall or a save pillar is behind her
    /// — and at a spawn one always is — she vanishes from under your own view, and comes back
    /// when you turn and the boom swings clear. The hands stay, because they are ours: they are
    /// copies cut out of her mesh and drawn by renderers the game has never heard of — see
    /// <see cref="DetachedHands"/>.
    /// </para>
    ///
    /// <para>
    /// Overruled after the fact rather than at the call. The call is made every frame, so a
    /// prefix on <c>EnableAllParts</c> would have been enough in principle, and one was tried:
    /// it was reached and did not hold, and she stayed hidden. The native function is sixteen
    /// bytes that end in a tail call, which is a poor place for a detour to rewrite an argument.
    /// This runs from the postfix of <c>PlayerCamera.Update</c>, which is where
    /// <c>CameraCollision</c> is called from, so it lands after the game's answer and before
    /// the frame is drawn. <c>EnableAllParts</c> returns at once when the value is unchanged, so
    /// a frame where nothing was hidden costs one comparison.
    /// </para>
    ///
    /// <para>
    /// <c>EnableAllParts</c> has exactly two callers, <c>CameraCollision</c> and
    /// <c>ScriptCameraCollision</c>, so overruling it touches the camera's hiding and nothing
    /// else the game does with her appearance. And it is the skin's own controller, not
    /// <c>PlayerCamera.g_Display</c>, that the game switches — the two are not the same object.
    /// </para>
    ///
    /// <para>
    /// Gated on the view being in her head, the rule the hands and <see cref="BodyVisibility"/>
    /// already answer to. When the view steps back — a cutscene, a death, the wake at a save
    /// pillar — it is the game's camera again, and a camera that close to her is one the game
    /// is right to hide her from.
    /// </para>
    /// </summary>
    internal static class CollisionHide
    {
        private static bool _reported;

        /// <summary>
        /// Called once a frame, after the game's camera update, with whether the view is in her
        /// head.
        /// </summary>
        internal static void Tick(PlayerCamera camera, bool viewInHerHead)
        {
            if (!viewInHerHead || camera == null) return;

            var girl = camera.wizardGirl;
            var skin = girl != null ? girl.skinInstance : null;
            var mesh = skin != null ? skin.meshController : null;
            if (mesh == null || mesh.enableAllParts) return;

            mesh.EnableAllParts(true);

            if (_reported) return;
            _reported = true;
            Plugin.Log.LogInfo("the game's camera hid her (its boom is pressed against a wall "
                             + "behind her), and the view is in her head, so she is shown again");
        }
    }
}
