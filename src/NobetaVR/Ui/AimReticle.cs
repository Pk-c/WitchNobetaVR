using System;
using Il2CppInterop.Runtime;
using NobetaVR.Vr;
using UnityEngine;

namespace NobetaVR.Ui
{
    /// <summary>
    /// Marks where the shot will land, in the world, at the depth it will land.
    ///
    /// The game's own crosshair cannot answer that question in a headset, and that is not a
    /// fault in it. On a monitor the camera looks straight down the aim line, so a mark fixed
    /// at the centre of the screen *is* where the shot goes; the crosshair is right because
    /// the two coincide. In VR they come apart twice over. The aim no longer runs down the
    /// middle of the view — it runs down the wand, or down your gaze, from an origin that is
    /// not the camera — and the interface it is painted on is a flat panel floating at a fixed
    /// distance, so even a perfectly projected point would sit at the wrong depth and land
    /// somewhere different in each eye.
    ///
    /// So the mark goes into the world instead, on whatever the aim ray found. There is no
    /// projection left to get wrong and no parallax left to disagree about: both eyes see it
    /// where it actually is. It also stops mattering which aim mode is on, because this marks
    /// the target rather than the line that found it, and gaze and wand differ only in the
    /// line.
    /// </summary>
    public sealed class AimReticle : MonoBehaviour
    {
        public AimReticle(IntPtr ptr) : base(ptr) { }

        private Transform _quad;
        private Material _material;
        private Texture2D _texture;
        private bool _failed;

        private UIAimingPoint _gameCrosshair;
        private float _nextScan;
        private bool _gameCrosshairHidden;

        private void LateUpdate()
        {
            GameCrosshair();

            if (_failed || !Plugin.Instance.ShowAimReticle.Value) { Hide(); return; }

            // Only while she is the player's to aim. The same gate the hands use: in a
            // cutscene or a menu the aim target still exists and still has a position, and a
            // dot left hanging on a wall through a conversation is exactly the kind of thing a
            // mod leaves behind by never asking.
            if (!VrHands.PlayerInControl) { Hide(); return; }

            var target = VrAim.Target;
            var camera = VrCamera.CameraTransform;
            if (target == null || camera == null) { Hide(); return; }

            if (_quad == null && !Build()) return;

            var toTarget = target.Value - camera.position;
            var distance = toTarget.magnitude;
            if (distance < 0.05f) { Hide(); return; }

            var direction = toTarget / distance;

            // Lifted off the surface towards the eye. The aim point is *on* the wall it found,
            // and a quad coplanar with a wall is a coin toss between the two every frame,
            // which reads as the reticle flickering rather than as anything to do with depth.
            // Scaled with distance so the lift stays small next to what it is marking.
            _quad.position = target.Value - direction * Mathf.Min(0.05f, distance * 0.04f);
            _quad.rotation = Quaternion.LookRotation(direction, camera.up);

            // Constant angular size rather than constant world size: a reticle that shrinks
            // with distance disappears exactly when a shot needs it most, and one that does
            // not swells into a dinner plate against a near wall.
            var size = distance * Plugin.Instance.AimReticleSize.Value;
            _quad.localScale = new Vector3(size, size, 1f);

            if (!_quad.gameObject.activeSelf) _quad.gameObject.SetActive(true);
        }

        private void Hide()
        {
            if (_quad != null && _quad.gameObject.activeSelf) _quad.gameObject.SetActive(false);
        }

        private void OnDisable()
        {
            RestoreGameCrosshair();
            Hide();
        }

        // -- the game's own crosshair --------------------------------------------------

        /// <summary>
        /// Hides the game's centred crosshair, if asked.
        ///
        /// Off by default, because that mark is not only a crosshair: <c>aimImg</c> grows with
        /// the charge and carries the magic's colour, so switching it off costs information
        /// that has nowhere else to go yet. Worth having as a switch all the same — once the
        /// world reticle is doing the aiming, a second mark that never moves reads as a smudge
        /// on the lens.
        ///
        /// Re-applied every frame rather than once, and the reference re-found on a timer:
        /// this UI is rebuilt per stage.
        /// </summary>
        private void GameCrosshair()
        {
            var wanted = Plugin.Instance.HideGameCrosshair.Value;
            if (!wanted && !_gameCrosshairHidden) return;

            if (_gameCrosshair == null && Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 1f;
                var found = UnityEngine.Object.FindObjectOfType(Il2CppType.Of<UIAimingPoint>());
                _gameCrosshair = found != null ? found.TryCast<UIAimingPoint>() : null;
                if (_gameCrosshair != null) Plugin.Log.LogInfo("found the game's UIAimingPoint");
            }

            if (_gameCrosshair == null) return;

            SetEnabled(_gameCrosshair.aimImg, !wanted);
            SetEnabled(_gameCrosshair.aimCenterImg, !wanted);
            _gameCrosshairHidden = wanted;
        }

        private void RestoreGameCrosshair()
        {
            if (!_gameCrosshairHidden || _gameCrosshair == null) return;
            SetEnabled(_gameCrosshair.aimImg, true);
            SetEnabled(_gameCrosshair.aimCenterImg, true);
            _gameCrosshairHidden = false;
        }

        private static void SetEnabled(UnityEngine.UI.Image image, bool enabled)
        {
            if (image != null && image.enabled != enabled) image.enabled = enabled;
        }

        // -- construction --------------------------------------------------------------

        private bool Build()
        {
            var shader = TransparentShader.Find();
            if (shader == null) { _failed = true; return false; }

            _texture = Draw();
            _material = new Material(shader) { mainTexture = _texture };

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "NobetaVR Reticle";
            UnityEngine.Object.DontDestroyOnLoad(quad);
            quad.hideFlags = HideFlags.HideAndDontSave;

            // A collider here would be a pane of invisible glass hanging wherever you point,
            // and the aim raycast would then find it rather than the wall behind it.
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

            Plugin.Log.LogInfo("aim reticle built");
            return true;
        }

        /// <summary>
        /// Draws the reticle into a texture rather than shipping one.
        ///
        /// A ring with a dot in the middle: the dot is the point, the ring is what makes it
        /// findable against a busy wall. Both are white inside a dark rim, which is the
        /// cheapest way to stay readable over anything — a white mark disappears on snow and a
        /// black one in a crypt, and this game has both.
        /// </summary>
        private static Texture2D Draw()
        {
            const int size = 128;
            const float ring = 0.30f;      // radius of the ring, where 1 is the texture's edge
            const float band = 0.055f;     // half its thickness
            const float dot = 0.075f;      // radius of the centre dot
            const float rim = 0.035f;      // thickness of the dark rim around both

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "NobetaVR Reticle",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            var edge = 2f / size;          // a texel or so, so the edges do not step
            var clear = new Color(0f, 0f, 0f, 0f);

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = (x + 0.5f) / size * 2f - 1f;
                    var dy = (y + 0.5f) / size * 2f - 1f;
                    var d = Mathf.Sqrt(dx * dx + dy * dy);

                    // How far outside the shape a pixel is; negative inside either part of it.
                    var outside = Mathf.Min(Mathf.Abs(d - ring) - band, d - dot);

                    var core = Mathf.Clamp01(1f - outside / edge);
                    var halo = Mathf.Clamp01(1f - (outside - edge) / rim);

                    var alpha = Mathf.Max(core, halo * 0.85f);
                    if (alpha <= 0.001f) { texture.SetPixel(x, y, clear); continue; }

                    // White in the shape, black in the rim around it.
                    texture.SetPixel(x, y, new Color(core, core, core, alpha));
                }
            }

            texture.Apply();
            return texture;
        }
    }
}
