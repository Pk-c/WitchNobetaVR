using Il2CppInterop.Runtime;
using NobetaVR.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace NobetaVR.Vr
{
    /// <summary>
    /// The game's fades to black, done to the whole view instead of to a rectangle in front of
    /// it.
    ///
    /// The game hides its transitions behind a full-screen black Image, which is exactly right
    /// on a monitor and does nothing useful here: a screen-space image is captured onto the HUD
    /// panel like everything else, so "fade to black" came out as a black rectangle floating a
    /// metre and a half away with the level still perfectly visible around it. The whole point
    /// of the fade — that you do not see the seam — was lost, and no amount of enlarging the
    /// image could fix it, because the image can never be bigger than the panel it is drawn on.
    ///
    /// So the value is taken and the drawing is not. The game's own image is hidden and its
    /// alpha is read off it every frame, which means the game keeps complete control of when a
    /// fade starts, how long it takes and what curve it follows — all of that is still its own
    /// tween running on its own object. Only the surface it lands on changes. Nothing is taken
    /// over until that alpha has been seen to move; see <c>Fader</c> for why.
    ///
    /// Two sources, because the game has two. <c>GameUIManager.blackScreen</c> is the one that
    /// covers scene transitions and outlives any single stage; <c>StageUIManager.background</c>
    /// is the per-stage veil, used both for in-level fades and as the dimming behind a menu.
    /// The larger of the two wins, so a menu opening dims the room and a transition blacks it
    /// out, which is what each was asking for in the first place.
    ///
    /// <para>
    /// Driven from <see cref="VrCamera"/>'s own update rather than from a LateUpdate of its
    /// own, for the reason the cutscene frame was before it: this is welded to the head, and
    /// <c>PlayerCamera.Update</c> is called by hand out of <c>WizardGirlManage.LateUpdate</c>,
    /// so Unity's component order says nothing about whether a LateUpdate of ours would run
    /// before or after the view pose was written.
    /// </para>
    /// </summary>
    internal static class ViewFade
    {
        /// <summary>
        /// Half the quad's angular extent, in degrees. Comfortably past the edge of any
        /// headset's field of view: 80 to a side is 160 across, against about 110 on the widest
        /// consumer optics. A fade with a visible edge is not a fade.
        /// </summary>
        private const float HalfAngle = 80f;

        /// <summary>Where the quad hangs. Only the ratio to the extent matters; see Place.</summary>
        private const float Distance = 1f;

        private static Transform _quad;
        private static Material _material;
        private static bool _failed;

        private static GameUIManager _ui;
        private static StageUIManager _stage;
        private static float _nextScan;

        public static void Apply(Transform view)
        {
            if (_failed || view == null) return;

            if (!Plugin.Instance.VrFade.Value) { Restore(); Hide(); return; }

            var amount = Mathf.Clamp01(Read());
            if (amount <= 0.002f) { Hide(); return; }

            if (_quad == null && !Build()) return;

            Place(view, amount);
        }

        // -- the value -------------------------------------------------------------------

        private static readonly Fader Screen = new("GameUIManager.blackScreen");
        private static readonly Fader Veil = new("StageUIManager.background");

        private static float Read()
        {
            Rescan();

            return Mathf.Max(Screen.Read(_ui != null ? _ui.blackScreen : null),
                             Veil.Read(_stage != null ? _stage.background : null));
        }

        private static void Restore()
        {
            Screen.Release();
            Veil.Release();
        }

        /// <summary>
        /// One of the game's fade images: what it is asking for, and whether it is safe to
        /// believe it.
        ///
        /// The alpha of the Image is the obvious reading and it is a guess until it is not.
        /// The game could be driving that black screen some other way — a canvas renderer
        /// alpha, a group, an object toggle — in which case the colour would sit at a constant
        /// 1, and a constant 1 read as "fade to black and stay there". That is the one failure
        /// here that a player cannot see past or work around, so nothing is taken over until
        /// this source has been *seen to move*: until then it draws itself exactly as it
        /// always did and we draw nothing. It costs the first fade of a session, and it turns
        /// every wrong guess about which value the game animates into a no-op.
        ///
        /// Hidden with a <see cref="CanvasGroup"/> rather than by switching the graphic off,
        /// so the reading and the hiding stay independent: <c>enabled</c> is one of the things
        /// the game might be using to hide it, and writing to it would destroy the signal.
        /// </summary>
        private sealed class Fader
        {
            private readonly string _name;

            private Image _image;
            private CanvasGroup _group;
            private float _last = -1f;
            private bool _proven;

            public Fader(string name) => _name = name;

            public float Read(Image image)
            {
                if (!ReferenceEquals(image, _image))
                {
                    _image = image;
                    _group = null;
                    _last = -1f;
                }

                if (_image == null) return 0f;

                var alpha = _image.gameObject.activeInHierarchy && _image.enabled
                    ? _image.color.a
                    : 0f;

                if (!_proven)
                {
                    if (_last >= 0f && Mathf.Abs(alpha - _last) > 0.002f)
                    {
                        _proven = true;
                        Plugin.Log.LogInfo($"VR fade is taking over {_name}");
                    }
                    _last = alpha;
                    return 0f;
                }

                Show(false);
                return alpha;
            }

            /// <summary>Gives the image back, for when the setting is turned off mid-session.</summary>
            public void Release()
            {
                if (_group != null) Show(true);
            }

            private void Show(bool shown)
            {
                if (_image == null) return;

                if (_group == null)
                {
                    var go = _image.gameObject;
                    _group = go.GetComponent<CanvasGroup>();
                    if (_group == null) _group = go.AddComponent<CanvasGroup>();
                }

                _group.alpha = shown ? 1f : 0f;
            }
        }

        /// <summary>
        /// <c>GameUIManager</c> outlives every stage and <c>StageUIManager</c> does not, so both
        /// are re-found on a timer rather than once. A null here is not a fault: on the title
        /// screen there is no stage UI to have a backdrop.
        /// </summary>
        private static void Rescan()
        {
            if (_ui != null && _stage != null) return;
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 1f;

            if (_ui == null)
            {
                var found = Object.FindObjectOfType(Il2CppType.Of<GameUIManager>());
                _ui = found != null ? found.TryCast<GameUIManager>() : null;
                if (_ui != null) Plugin.Log.LogInfo("VR fade found GameUIManager.blackScreen");
            }

            if (_stage == null)
            {
                var found = Object.FindObjectOfType(Il2CppType.Of<StageUIManager>());
                _stage = found != null ? found.TryCast<StageUIManager>() : null;
            }
        }

        // -- the quad --------------------------------------------------------------------

        private static void Place(Transform view, float amount)
        {
            // A fixed angle, so the distance it hangs at cannot change what it covers.
            var extent = 2f * Distance * Mathf.Tan(HalfAngle * Mathf.Deg2Rad);

            _quad.position = view.position + view.forward * Distance;
            _quad.rotation = view.rotation;
            _quad.localScale = new Vector3(extent, extent, 1f);

            _material.color = new Color(0f, 0f, 0f, amount);

            if (!_quad.gameObject.activeSelf) _quad.gameObject.SetActive(true);
        }

        private static void Hide()
        {
            if (_quad != null && _quad.gameObject.activeSelf) _quad.gameObject.SetActive(false);
        }

        private static bool Build()
        {
            var shader = TransparentShader.Find();
            if (shader == null) { _failed = true; return false; }

            _material = new Material(shader);

            // In front of everything, including the interface. A transition fade that the HUD
            // panel showed through would be a fade with the health bar still on it, so this is
            // pushed past the panel's own 3950 as well as past the world's transparent queue,
            // and the depth test goes with it — the quad hangs a metre away and most of what it
            // has to cover is nearer than that.
            _material.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            _material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            _material.renderQueue = 4000;

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "NobetaVR View Fade";
            Object.DontDestroyOnLoad(quad);
            quad.hideFlags = HideFlags.HideAndDontSave;

            // Metres of invisible wall across the view otherwise, and the aim ray would find it
            // long before it found anything worth shooting.
            var collider = quad.GetComponent<Collider>();
            if (collider != null) Object.Destroy(collider);

            var renderer = quad.GetComponent<MeshRenderer>();
            renderer.material = _material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            _quad = quad.transform;

            // Layer 0: seen by the game's cameras, and not by the HUD capture camera, whose
            // mask is built from the interface canvases alone.
            _quad.gameObject.layer = 0;
            _quad.gameObject.SetActive(false);

            Plugin.Log.LogInfo("VR fade built");
            return true;
        }
    }
}
