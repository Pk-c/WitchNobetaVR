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
    /// Three sources, because the game has three, one per lifetime.
    /// <c>GameUIManager.blackScreen</c> covers scene transitions and outlives any single stage;
    /// <c>StageUIManager.background</c> is the per-stage veil, used for in-level fades and as
    /// the dimming behind a menu; <c>UIScriptMode.blackScreen</c> is the cutscene's own, which
    /// is what a scene fades through on its way in and out. They are separate objects with
    /// separate fields and separate coroutines, so finding one says nothing about the others —
    /// the cutscene's was the one still landing on the panel as a black rectangle after the
    /// other two had been dealt with. The largest wins, so a menu dims the room and a
    /// transition blacks it out, which is what each was asking for in the first place.
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

        /// <summary>The active scene as last seen, which is how a stage change is noticed.</summary>
        private static string _scene;

        /// <summary>Until when the stage veil is looked for on every frame; see <see cref="Rescan"/>.</summary>
        private static float _eagerUntil;

        /// <summary>
        /// How black the view is being held right now, nothing to fully black.
        ///
        /// Published because a transition is the one time something else needs to know: the HUD
        /// panel is drawn over this quad on purpose, so that a conversation stays readable
        /// through a fade, and that is precisely wrong while a stage is still building itself
        /// behind the black. Zero while the VR fade is switched off, because then the game is
        /// drawing its own black onto the panel and there is nothing here to wait for.
        /// </summary>
        internal static float Amount { get; private set; }

        public static void Apply(Transform view)
        {
            if (_failed || view == null) return;

            if (!Plugin.Instance.VrFade.Value) { Restore(); Hide(); Amount = 0f; return; }

            var amount = Mathf.Clamp01(Read());
            Amount = amount;

            if (amount <= 0.002f) { Hide(); return; }

            if (_quad == null && !Build()) return;

            Place(view, amount);
        }

        // -- the value -------------------------------------------------------------------

        private static readonly Fader Screen = new("GameUIManager.blackScreen");
        private static readonly Fader Veil = new("StageUIManager.background");
        private static readonly Fader Script = new("UIScriptMode.blackScreen");

        private static float Read()
        {
            Rescan();

            var scriptMode = _stage != null ? _stage.scriptMode : null;

            var amount = Screen.Read(_ui != null ? _ui.blackScreen : null);
            amount = Mathf.Max(amount, Veil.Read(_stage != null ? _stage.background : null));
            amount = Mathf.Max(amount, Script.Read(scriptMode != null ? scriptMode.blackScreen : null));
            return amount;
        }

        private static void Restore()
        {
            Screen.Release();
            Veil.Release();
            Script.Release();
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

            /// <summary>Whether this particular image has been reported as taken over.</summary>
            private bool _taken;

            /// <summary>
            /// Whether the game has been seen to fade by animating an Image's own colour alpha
            /// -- once, by any of the three sources, for all of them.
            ///
            /// Shared rather than per-source, and that is the whole of the fix it represents.
            /// What this gate guards against is the guess itself: that the game animates
            /// <c>color.a</c> rather than a group, a renderer alpha or an object toggle. That
            /// guess is about the game's convention, not about one field of one manager, and
            /// all three of these are the same kind of object driven the same way. Once one of
            /// them has been watched doing it, it is no longer a guess for the other two.
            ///
            /// <para>
            /// Per-source, it cost the first fade from each -- and the per-stage veil is the
            /// one that paid. <c>GameUIManager.blackScreen</c> proves itself during BootUp and
            /// then sits at zero; the fade at the end of every stage load belongs to
            /// <c>StageUIManager.background</c>, which is a fresh object each time and which
            /// this gate held unproven right through the fade it exists to draw. The view
            /// stayed clear when it should have been black, and the game's own black rectangle
            /// -- not hidden either, because the same flag gates the hiding -- was left to land
            /// on the HUD panel as a rectangle floating in a lit room. Both halves of that are
            /// this one flag, which is why it is now one flag for all three.
            /// </para>
            /// </summary>
            private static bool _proven;

            public Fader(string name) => _name = name;

            public float Read(Image image)
            {
                if (!ReferenceEquals(image, _image))
                {
                    _image = image;
                    _group = null;
                    _last = -1f;
                    _taken = false;
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
                        Plugin.Log.LogInfo($"VR fade: {_name} animates its own alpha, so all "
                                         + "three sources are now believed");
                    }
                    _last = alpha;
                    return 0f;
                }

                // Per image rather than per source, so a stage change says so: the veil is a
                // new object every stage, and which fade is on the view is exactly the question
                // this log gets read to answer.
                if (!_taken)
                {
                    _taken = true;
                    Plugin.Log.LogInfo($"VR fade is taking over {_name}");
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

        /// <summary>How long after a scene change the stage veil is looked for every frame.</summary>
        private const float Eager = 5f;

        /// <summary>
        /// <c>GameUIManager</c> outlives every stage and <c>StageUIManager</c> does not, so both
        /// are re-found on a timer rather than once. A null here is not a fault: on the title
        /// screen there is no stage UI to have a backdrop.
        ///
        /// <para>
        /// The timer alone is too slow for the one moment that matters. A stage change destroys
        /// the veil and builds a new one, and it is also the moment a fade-in is already
        /// running -- so a scan that arrives up to a second late arrives after the fade it was
        /// wanted for. The scene name forces the search, and for a few seconds afterwards the
        /// search happens every frame, which is the window the new StageUIManager appears in.
        /// Outside that window nothing goes missing often enough to be worth looking for at
        /// that rate, and the title screen has no stage veil to find at all.
        /// </para>
        /// </summary>
        private static void Rescan()
        {
            var active = ActiveScene.Name;
            if (active != _scene)
            {
                _scene = active;
                _stage = null;
                _eagerUntil = Time.unscaledTime + Eager;
            }

            if (_ui != null && _stage != null) return;
            if (Time.unscaledTime >= _eagerUntil && Time.unscaledTime < _nextScan) return;
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

            // In front of the world and behind the interface. The depth test is off because the
            // quad hangs a metre away and everything it has to cover is nearer than that, so
            // the draw order is the only thing deciding what wins — which makes the queue the
            // whole of the answer. 3940 puts it past the world's transparent queue and just
            // under the HUD panel's own 3950: the room goes black, and the subtitles, prompts
            // and menus that a transition is there to be read through stay on top of it.
            _material.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            _material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            _material.renderQueue = 3940;

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

            // Layer 0: seen by the game's cameras. The HUD capture camera renders that layer
            // as well -- the game leaves canvases on it -- and is kept off this quad by being
            // parked a thousand units away with half a metre of depth to see; see HudPanel.Park.
            _quad.gameObject.layer = 0;
            _quad.gameObject.SetActive(false);

            Plugin.Log.LogInfo("VR fade built");
            return true;
        }
    }
}
