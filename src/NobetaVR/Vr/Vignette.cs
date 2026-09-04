using Il2CppInterop.Runtime.InteropTypes.Arrays;
using NobetaVR.Ui;
using UnityEngine;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Narrows the view to a rectangle whenever the game is the one placing the camera.
    ///
    /// A cutscene moves the camera, and on a monitor that is direction: a cut, a sweep, a push
    /// in. In a headset the camera is your head, so the same sweep is your head being turned
    /// for you — the sharpest form of vection there is, because nothing you feel agrees with
    /// what you see and you cannot brace against a movement you did not start.
    ///
    /// A frame is the standard answer and it is a cheap one. What moves is confined to a
    /// window; the black around it does not move with the world, so the eye has something
    /// fixed to hold on to while the middle of the view travels. It costs the edges of the
    /// framing, which a cutscene rarely uses, and nothing else — the game's camera is left
    /// running exactly as it was.
    ///
    /// Four straight bars rather than a soft round tunnel, because a rectangle reads as
    /// watching a scene and an oval reads as being unwell. They are the same object here:
    /// <c>CutsceneVignetteSoftness</c> takes the aperture from crisp bars to a wide gradient
    /// without changing its shape.
    ///
    /// <para>
    /// It hangs several metres away rather than in front of your face. A frame held at arm's
    /// length is seen from two places at once, and each eye puts its edge somewhere different:
    /// at 0.3 m the two disagree by about ten degrees, which reads as a doubled bar. At six
    /// metres they disagree by half a degree, less than the softness of the edge itself. The
    /// price is that everything a cutscene frames is nearer than the frame, so the depth test
    /// has to be switched off and the draw pushed past the world's transparent queue — the
    /// frame is an overlay, not an object in the scene. The game's interface is pushed past it
    /// in turn, in <see cref="Ui.HudPanel"/>, so the bars can never cover the subtitles they
    /// are there to frame.
    /// </para>
    ///
    /// <para>
    /// Driven from <see cref="VrCamera"/>'s own update rather than from a LateUpdate of its
    /// own. A frame welded to your head is the one thing that must not lag by even a frame, or
    /// it swims against the view every time you turn; and <c>PlayerCamera.Update</c> is called
    /// by hand from <c>WizardGirlManage.LateUpdate</c>, so Unity's component order says nothing
    /// about whether a LateUpdate of ours would run before or after the pose was written.
    /// </para>
    /// </summary>
    internal static class Vignette
    {
        /// <summary>
        /// Half the quad's own angular extent, in degrees. Wide enough to reach past the edge
        /// of any headset's field of view — 70 degrees to a side is 140 across, against about
        /// 110 on the widest consumer optics — and no wider, because the aperture is baked into
        /// a texture spanning this whole angle and every degree of margin is resolution taken
        /// away from the edge that is actually looked at.
        /// </summary>
        private const float HalfAngle = 70f;

        /// <summary>
        /// Side of the baked texture. The aperture edge lands around a fifth of the way out
        /// from the centre, so this is roughly a texel every quarter of a degree there — finer
        /// than the softest edge worth having, and cheap enough to rebake while a setting is
        /// being nudged in the menu.
        /// </summary>
        private const int Size = 512;

        private static Transform _quad;
        private static Material _material;
        private static Texture2D _texture;
        private static bool _failed;

        /// <summary>How far in, from 0 to 1. Faded rather than switched so a cut does not flash.</summary>
        private static float _amount;

        // What the texture currently holds, so an unchanged frame costs nothing to redraw.
        private static float _bakedWidth = -1f;
        private static float _bakedHeight = -1f;
        private static float _bakedSoftness = -1f;

        /// <summary>
        /// Whether the game, rather than the player, is placing the camera this frame.
        ///
        /// The same reading the rest of the mod gates on, and for the same reason: most modes
        /// are the game staging her — a cutscene, a conversation, the lerp back out of one.
        /// Three are not framed, each for its own reason.
        ///
        /// <c>PlayerFace</c> is asked for by the player themselves with <c>SwitchCameraMode</c>,
        /// and a frame that drops over the view when you chose to look at her would be the mod
        /// second-guessing you.
        ///
        /// <c>Dead</c> and <c>FallDead</c> are the game taking the camera, so by the rule above
        /// they would be framed — but there is nothing to protect anyone from. The death camera
        /// is not a sweep you have to hold still through; it is a beat, then a scene reload, and
        /// the frame fading in over it only announces that you died a second time. It also
        /// arrives at the worst moment for it: the fade out is racing the load, so the bars
        /// often vanish with the scene rather than lifting.
        /// </summary>
        private static bool GameIsFraming()
        {
            var mode = BodyFacing.Mode;
            return mode != PlayerCamera.CameraMode.Normal
                && mode != PlayerCamera.CameraMode.PlayerFace
                && mode != PlayerCamera.CameraMode.Dead
                && mode != PlayerCamera.CameraMode.FallDead;
        }

        /// <summary>
        /// Called once the view pose for this frame is final. See the class remarks for why it
        /// is called from there rather than run on its own.
        /// </summary>
        public static void Apply(Transform view)
        {
            var cfg = Plugin.Instance;

            var wanted = !_failed
                      && cfg.CutsceneVignette.Value
                      && view != null
                      && GameIsFraming();

            var fade = cfg.CutsceneVignetteFade.Value;
            var target = wanted ? 1f : 0f;

            _amount = fade <= 0.01f
                ? target
                : Mathf.MoveTowards(_amount, target, Time.unscaledDeltaTime / fade);

            if (_amount <= 0.001f) { Hide(); return; }
            if (_quad == null && !Build()) return;

            Rebake(cfg);
            Place(view, cfg);
        }

        private static void Hide()
        {
            if (_quad != null && _quad.gameObject.activeSelf) _quad.gameObject.SetActive(false);
        }

        // -- placement -----------------------------------------------------------------

        private static void Place(Transform view, Plugin cfg)
        {
            var distance = Mathf.Max(0.2f, cfg.CutsceneVignetteDistance.Value);

            // The quad covers a fixed angle, so its size follows the distance and the aperture
            // inside it never changes with either. Distance is a setting only for the
            // depth-test escape hatch described in its own description.
            var extent = 2f * distance * Mathf.Tan(HalfAngle * Mathf.Deg2Rad);

            _quad.position = view.position + view.forward * distance;
            _quad.rotation = view.rotation;
            _quad.localScale = new Vector3(extent, extent, 1f);

            // The texture is black with the shape in its alpha; the tint carries how far in we
            // are. White rather than black, so the bars stay the texture's black instead of
            // being multiplied away.
            _material.color = new Color(1f, 1f, 1f, _amount);

            if (!_quad.gameObject.activeSelf) _quad.gameObject.SetActive(true);
        }

        // -- the frame itself ----------------------------------------------------------

        private static void Rebake(Plugin cfg)
        {
            // Half-angles, because that is what the drawing works in. Clamped against the
            // quad's own extent: an aperture as wide as the quad has no bars left to draw.
            var halfWidth = Mathf.Clamp(cfg.CutsceneVignetteWidth.Value * 0.5f, 2f, HalfAngle - 2f);
            var halfHeight = Mathf.Clamp(cfg.CutsceneVignetteHeight.Value * 0.5f, 2f, HalfAngle - 2f);
            var softness = Mathf.Clamp01(cfg.CutsceneVignetteSoftness.Value);

            if (halfWidth == _bakedWidth && halfHeight == _bakedHeight && softness == _bakedSoftness)
                return;

            _bakedWidth = halfWidth;
            _bakedHeight = halfHeight;
            _bakedSoftness = softness;

            Paint(halfWidth, halfHeight, softness);

            Plugin.Log.LogInfo($"cutscene frame {halfWidth * 2f:F0}x{halfHeight * 2f:F0} deg, "
                             + $"softness {softness:F2}");
        }

        /// <summary>
        /// Draws the aperture, in angles rather than in pixels.
        ///
        /// The quad is flat and the eye is not, so a pixel's distance from the centre of the
        /// texture is not proportional to the angle it will be seen at — it is the tangent of
        /// it, and badly so this far out, where the last tenth of the texture carries thirty
        /// degrees. Every pixel is therefore converted back to its angle before it is tested,
        /// which is what makes the settings mean degrees of your vision rather than a fraction
        /// of a quad whose edges nobody can see.
        ///
        /// The two axes are independent, so the angle is worked out once per row and once per
        /// column rather than once per pixel, and what is left inside the loop is a comparison.
        /// </summary>
        private static void Paint(float halfWidth, float halfHeight, float softness)
        {
            var tanMax = Mathf.Tan(HalfAngle * Mathf.Deg2Rad);

            // A gradient of at least a texel even at zero softness. Hard bars are worth having,
            // and a hard bar is not the same thing as a stepped one.
            var softX = Mathf.Max(halfWidth * softness, 0.5f);
            var softY = Mathf.Max(halfHeight * softness, 0.5f);

            var acrossX = new float[Size];
            var acrossY = new float[Size];

            for (var i = 0; i < Size; i++)
            {
                var offset = ((i + 0.5f) / Size * 2f - 1f) * tanMax;
                var angle = Mathf.Atan(Mathf.Abs(offset)) * Mathf.Rad2Deg;

                acrossX[i] = Mathf.Clamp01((angle - halfWidth) / softX);
                acrossY[i] = Mathf.Clamp01((angle - halfHeight) / softY);
            }

            var pixels = new Il2CppStructArray<Color32>(Size * Size);

            for (var y = 0; y < Size; y++)
            {
                var row = y * Size;
                var outsideY = acrossY[y];

                for (var x = 0; x < Size; x++)
                {
                    // The larger of the two: outside the opening in either direction is outside
                    // it. Taking them together this way is what keeps the corners square, so
                    // the opening stays a rectangle however soft the edge is made.
                    var t = Mathf.Max(acrossX[x], outsideY);
                    var alpha = t * t * (3f - 2f * t);

                    pixels[row + x] = new Color32(0, 0, 0, (byte)(alpha * 255f + 0.5f));
                }
            }

            _texture.SetPixels32(pixels);
            _texture.Apply(false);
        }

        // -- construction --------------------------------------------------------------

        private static bool Build()
        {
            var shader = TransparentShader.Find();
            if (shader == null) { _failed = true; return false; }

            _texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                name = "NobetaVR Vignette",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            _material = new Material(shader) { mainTexture = _texture };

            // An overlay rather than an object. Everything a cutscene frames is nearer than the
            // plane the frame hangs on, so the depth test is turned off and the draw is pushed
            // past the world's transparent queue instead. Both halves are needed: the queue
            // alone would still let a wall in front of it win the depth test, and the depth
            // test alone would leave it sorted among the game's own transparencies.
            _material.SetFloat("unity_GUIZTestMode", (float)UnityEngine.Rendering.CompareFunction.Always);
            _material.renderQueue = 3900;

            if (shader.name != "UI/Default")
            {
                Plugin.Log.LogWarning($"the cutscene frame is drawn with '{shader.name}', which "
                                    + "may not carry the depth-test override. If scenery covers "
                                    + "the bars, set CutsceneVignetteDistance to about 0.4.");
            }

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "NobetaVR Vignette";
            UnityEngine.Object.DontDestroyOnLoad(quad);
            quad.hideFlags = HideFlags.HideAndDontSave;

            // Tens of metres of invisible wall across the view otherwise, and the aim ray would
            // find it long before it found anything worth shooting.
            var collider = quad.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);

            var renderer = quad.GetComponent<MeshRenderer>();
            renderer.material = _material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            _quad = quad.transform;

            // Layer 0: seen by the game's cameras, and not by the HUD capture camera, whose
            // mask is built from the interface canvases alone.
            _quad.gameObject.layer = 0;
            _quad.gameObject.SetActive(false);

            Plugin.Log.LogInfo("cutscene frame built");
            return true;
        }
    }
}
