using UnityEngine;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Makes the game's crystals look through to the eye that is drawing them.
    ///
    /// The crystals of the late stages — the cave in <c>Act06_03</c> is full of them — are drawn
    /// with the game's <c>Crystal_Shader_Urp</c>, and they are not blended with the scene at
    /// all: the pixel shader writes an alpha of one. The "see-through" part is the frame already
    /// drawn behind the crystal, read back from <c>_CameraOpaqueTexture</c> at the crystal's
    /// screen position and mixed with the lit surface.
    ///
    /// <para>
    /// URP fills that texture once per camera render, straight after the skybox, so it only
    /// holds this render's picture for what is drawn after that point — which is what the
    /// transparent queue is for. Most crystal materials are there (2900). Two of them,
    /// <c>Crystal_3_Blue</c> and <c>Crystal_3_Yellow</c>, carry a queue override of 2000 and
    /// are drawn with the opaque geometry, before the copy exists. What they read is whatever
    /// the pooled texture held from the render before. On a monitor that is the previous frame
    /// from the same camera — one frame stale and indistinguishable. Under multi-pass stereo the
    /// render before is the other eye, so one eye sees a piece of the room, displaced by the
    /// distance between the two views, through the crystal: a reflection in one eye only.
    /// Measured, not supposed: captures of every eye pass showed each eye's copy matching that
    /// eye by the end of its render, and the fault following the queue.
    /// </para>
    ///
    /// <para>
    /// So any crystal material below the transparent range is moved to the queue its siblings
    /// already use, and is drawn after the copy, reading this eye's picture. Nothing else about
    /// it changes: it is still written to depth, and it only stops appearing in the copy other
    /// see-through surfaces read — which, drawn after it, never showed it correctly either.
    /// This is not a setting: it is a fault in stereo and has no look to prefer.
    /// </para>
    ///
    /// <para>
    /// Written on the materials themselves, which are shared assets, so every crystal using
    /// one changes at once and a runtime copy made from one inherits the queue.
    /// </para>
    /// </summary>
    internal static class Crystals
    {
        private const string ShaderName = "Crystal_Shader_Urp";

        /// <summary>
        /// The queue the game's own transparent crystal materials use, and the one the rest are
        /// moved to. Anything at or below the end of the opaque range is drawn before the copy.
        /// </summary>
        private const int AfterCopy = 2900;
        private const int LastOpaque = 2500;

        /// <summary>
        /// Rescan interval, in seconds. Materials arrive with a stage and with the effects it
        /// loads on demand, so this is a standing job; comparing a few thousand shader handles
        /// once in a while is cheap next to missing a cave full of crystals.
        /// </summary>
        private const float Every = 5f;

        private static Shader _shader;
        private static float _nextScan;

        public static void Tick()
        {
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + Every;

            Scan();
        }

        /// <summary>
        /// Asks for a scan on the next tick. A new stage has its crystals in memory by the
        /// frame its scene is active, and waiting out the timer would show them as the game
        /// draws them for the first seconds of it.
        /// </summary>
        public static void SceneChanged() => _nextScan = 0f;

        private static void Scan()
        {
            // Found by name once and compared by handle after that: a shader's name is a string
            // marshalled out of the engine, a handle comparison is not. Not loaded on a stage
            // without crystals, which is most of them.
            if (_shader == null) _shader = Shader.Find(ShaderName);
            if (_shader == null) return;

            var materials = Resources.FindObjectsOfTypeAll<Material>();
            var moved = 0;

            for (var i = 0; i < materials.Length; i++)
            {
                var material = materials[i];
                if (material == null || material.shader != _shader) continue;
                if (material.renderQueue > LastOpaque) continue;

                material.renderQueue = AfterCopy;
                moved++;
            }
        }
    }
}
