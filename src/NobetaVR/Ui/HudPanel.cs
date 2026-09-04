using System;
using Il2CppInterop.Runtime;
using NobetaVR.Vr;
using UnityEngine;

namespace NobetaVR.Ui
{
    /// <summary>
    /// Puts the game's interface on a panel in front of you.
    ///
    /// The interface is captured, not rebuilt. Every screen-space canvas is redirected through
    /// a camera of our own that draws into a render texture, and that texture is shown on a
    /// quad floating in the world. Unity keeps full control of the layout, so TextMeshPro, the
    /// game's own animations, and its habit of hiding elements by sliding them off screen all
    /// keep working exactly as they did. Rebuilding the HUD would mean re-implementing all of
    /// that and re-breaking it on every game update.
    ///
    /// Screen-space overlay canvases are drawn straight to the display after everything else,
    /// which is why none of the interface was visible in the headset: there was nothing wrong
    /// with it, it simply was not part of the scene either eye renders.
    /// </summary>
    public sealed class HudPanel : MonoBehaviour
    {
        public HudPanel(IntPtr ptr) : base(ptr) { }

        private Camera _capture;
        private RenderTexture _texture;
        private Transform _panel;
        private Material _material;

        private float _nextScan;
        private int _canvasCount = -1;
        private bool _failed;
        private Vector3 _direction;

        private void LateUpdate()
        {
            if (_failed || !Plugin.Instance.HudEnabled.Value) return;

            if (_texture == null && !Build()) return;

            Rescan();
            Follow();
        }

        // -- construction --------------------------------------------------------------

        private bool Build()
        {
            var cfg = Plugin.Instance;
            var shader = TransparentShader.Find();
            if (shader == null)
            {
                // Nothing to fall back on: an opaque shader here hangs a solid white slab
                // in front of the player's face, which is worse than no interface at all.
                Plugin.Log.LogError("The HUD panel would be an opaque slab. Leaving it off.");
                _failed = true;
                return false;
            }

            // 1080p. The capture camera draws the interface and nothing else, so this costs
            // almost nothing to render and there is no other size anyone would want.
            _texture = new RenderTexture(1920, 1080, 0, RenderTextureFormat.ARGB32) { name = "NobetaVR HUD" };
            _texture.Create();
            ClearToTransparent(_texture);

            _capture = new GameObject("NobetaVR HUD Camera").AddComponent<Camera>();
            UnityEngine.Object.DontDestroyOnLoad(_capture.gameObject);
            _capture.gameObject.hideFlags = HideFlags.HideAndDontSave;
            _capture.clearFlags = CameraClearFlags.SolidColor;

            // Transparent black, not black. Clearing to opaque black is the other half of the
            // white-slab mistake: the panel then shows a black rectangle over the whole view.
            _capture.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _capture.targetTexture = _texture;
            _capture.stereoTargetEye = StereoTargetEyeMask.None;   // never render this one in stereo
            _capture.orthographic = false;
            _capture.nearClipPlane = 0.01f;
            _capture.farClipPlane = 100f;
            _capture.allowHDR = false;
            _capture.allowMSAA = false;
            _capture.cullingMask = 0;    // filled in by Rescan, from the canvases actually found

            _material = new Material(shader) { mainTexture = _texture };

            // Drawn after the cutscene frame, which is an overlay of its own at 3900. The bars
            // are there to frame what the game is showing you, and eating the subtitles while
            // doing it would be the one way they could make a cutscene worse.
            _material.renderQueue = 3950;

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "NobetaVR HUD Panel";
            UnityEngine.Object.DontDestroyOnLoad(quad);
            quad.hideFlags = HideFlags.HideAndDontSave;

            // The collider would put an invisible wall in front of the player.
            var collider = quad.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);

            var renderer = quad.GetComponent<MeshRenderer>();
            renderer.material = _material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            _panel = quad.transform;
            _panel.gameObject.layer = 0;   // seen by the game's cameras, not by the capture one

            Plugin.Log.LogInfo($"HUD panel built: {_texture.width}x{_texture.height}, shader '{shader.name}'");
            return true;
        }

        /// <summary>
        /// A new render texture holds whatever was in that memory. Clearing it to transparent
        /// before anything can draw it is what stops the panel appearing as a bright rectangle
        /// on the first frame, which is the classic way this goes wrong.
        /// </summary>
        private static void ClearToTransparent(RenderTexture rt)
        {
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
            RenderTexture.active = previous;
        }

        // -- capture -------------------------------------------------------------------

        /// <summary>
        /// Redirects the game's screen-space canvases through the capture camera.
        ///
        /// Rescanned on a timer rather than done once: canvases are created and destroyed per
        /// stage and per menu, and one that appears later would otherwise go on drawing itself
        /// straight to a display nobody is looking at.
        /// </summary>
        private void Rescan()
        {
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 1f;

            var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Canvas>());
            if (found == null) return;

            var mask = 0;
            var redirected = 0;

            for (var i = 0; i < found.Length; i++)
            {
                var canvas = found[i].TryCast<Canvas>();
                if (canvas == null || canvas.rootCanvas != canvas) continue;

                if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = _capture;
                    canvas.planeDistance = 1f;
                    redirected++;
                }

                if (canvas.worldCamera == _capture)
                    mask |= 1 << canvas.gameObject.layer;
            }

            // Taken from the canvases themselves rather than assumed to be layer 5. The capture
            // camera must see the interface and nothing else -- above all not our own panel,
            // which would feed the texture back into itself.
            if (mask != 0 && _capture.cullingMask != mask)
            {
                _capture.cullingMask = mask;
                Plugin.Log.LogInfo($"HUD capture layers 0x{mask:X}");
            }

            if (redirected > 0 || _canvasCount != found.Length)
            {
                _canvasCount = found.Length;
                if (redirected > 0) Plugin.Log.LogInfo($"redirected {redirected} canvas(es) to the HUD panel");
            }
        }

        // -- placement -----------------------------------------------------------------

        /// <summary>
        /// Keeps the panel in front of you: rigid when you move, damped when you turn.
        ///
        /// The two want opposite treatments and one lerp cannot give both. Damping the panel's
        /// world position makes it lag when you walk, so it drifts towards you when you stop
        /// and away when you set off — which reads as the interface sliding about the room.
        /// Damping only the *direction* it sits in, and pinning it rigidly to your head for
        /// position, gives what is actually wanted: walk and it comes with you exactly, turn
        /// your head and it swings round to catch up a moment later.
        /// </summary>
        private void Follow()
        {
            var cfg = Plugin.Instance;
            var camera = VrCamera.CameraTransform;
            if (camera == null || _panel == null) return;

            var look = camera.forward;
            look.y = 0f;
            if (look.sqrMagnitude < 0.0001f) return;
            look.Normalize();

            // Frame-rate independent: the fraction remaining after dt seconds rather than a
            // fixed fraction per frame, so the feel does not change with the frame rate.
            var t = 1f - Mathf.Exp(-cfg.HudFollowSpeed.Value * Time.unscaledDeltaTime);

            if (_direction.sqrMagnitude < 0.0001f) _direction = look;
            _direction = Vector3.Slerp(_direction, look, t).normalized;

            // Position is not smoothed at all. It is the anchor that must not lag.
            _panel.position = camera.position
                            + _direction * cfg.HudDistance.Value
                            + Vector3.up * cfg.HudHeightOffset.Value;
            _panel.rotation = Quaternion.LookRotation(_direction, Vector3.up);

            var width = cfg.HudSize.Value;
            var height = width * _texture.height / _texture.width;
            _panel.localScale = new Vector3(width, height, 1f);
        }
    }
}
