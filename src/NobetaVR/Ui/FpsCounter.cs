using System;
using NobetaVR.Diagnostics;
using UnityEngine;
using UnityEngine.UI;

namespace NobetaVR.Ui
{
    /// <summary>
    /// The frame rate, in the headset, where the question is actually asked.
    ///
    /// Steam's own overlay answers it too, and better — it knows what the compositor did with
    /// each frame, which nothing inside the process can see. What it cannot do is be there at
    /// the moment something looks wrong, in the room where it looked wrong, without stopping
    /// the game to go and find it. This is for the other kind of reading: put it on, play, and
    /// see whether the number moved when the world did.
    ///
    /// Three numbers rather than one, because an average is the reading least able to explain
    /// a stutter. Against a 90 Hz headset a steady 89 and a 90 that spends a tenth of every
    /// second at 45 average to nearly the same thing and feel nothing like each other, so the
    /// worst frame in the window and a count of the late ones are given as well.
    ///
    /// <para>
    /// Welded rigidly to the view: same place in your field of vision, every frame, no
    /// following and no easing. Partly because a readout that swims is a readout you cannot
    /// read, and partly because it makes this the control experiment for anything that does
    /// swim — a panel that moves against the world while this one does not is the panel's own
    /// doing, and if both move, the fault is further upstream than either.
    /// </para>
    /// </summary>
    public sealed class FpsCounter : MonoBehaviour
    {
        public FpsCounter(IntPtr ptr) : base(ptr) { }

        internal static FpsCounter Instance { get; private set; }

        private void Awake() => Instance = this;

        /// <summary>
        /// How long the numbers are gathered over, in seconds. Half a second reads as a live
        /// number while still being long enough that a single frame cannot make it lie.
        /// </summary>
        private const float Window = 0.5f;

        /// <summary>
        /// Where it hangs in the view: a metre and a bit out, below the centre line, clear of
        /// where the game's own interface sits on its panel.
        /// </summary>
        private static readonly Vector3 Offset = new(0f, -0.34f, 1.15f);

        // Canvas units and the scale that turns them into metres, as the settings panel does
        // it. 0.39 by 0.12 metres at the distance above, which is about a fifth of the width
        // of your vision and legible without being something you have to look past.
        private const float Width = 560f;
        private const float Height = 170f;
        private const float Scale = 0.0007f;

        private GameObject _root;
        private Text _text;
        private bool _failed;

        private int _frames;
        private float _elapsed;
        private float _worst;
        private int _late;

        private void Update()
        {
            if (_failed) return;

            var show = Plugin.Instance.ShowFpsCounter.Value;

            // Built on first use rather than at startup: off is the default, and a player who
            // never turns it on should not be paying for a canvas and a font.
            if (!show && _root == null) return;
            if (_root == null && !Build()) return;

            if (_root.activeSelf != show) _root.SetActive(show);
            if (!show) { Forget(); return; }

            Measure();
        }

        /// <summary>
        /// Puts it in front of the eyes, driven from the view at the moment the view is final.
        ///
        /// Never from this component's own Update, for the reason every other panel here is
        /// placed from the view: the head pose is written inside the game's own LateUpdate,
        /// after all of the mod's, so anything that places itself earlier is placing itself
        /// against the previous frame.
        /// </summary>
        internal static void FollowView()
        {
            var self = Instance;
            if (self == null || self._root == null || !self._root.activeSelf) return;

            var view = Vr.VrCamera.CameraTransform;
            if (view == null) return;

            // The full rotation, roll and pitch included. Anything less is a panel that moves
            // in your vision when your head tilts, which is the one thing this must not do.
            self._root.transform.position = view.position + view.rotation * Offset;
            self._root.transform.rotation = view.rotation;
        }

        // -- measurement ---------------------------------------------------------------

        /// <summary>
        /// Unscaled time throughout: the game takes the clock during menus, cutscenes and
        /// deaths, and a frame-rate counter that reads zero because time stopped is answering
        /// a different question from the one being asked.
        /// </summary>
        private void Measure()
        {
            var dt = Time.unscaledDeltaTime;
            if (dt <= 0f) return;

            _frames++;
            _elapsed += dt;
            if (dt > _worst) _worst = dt;

            // Late against the headset's cadence rather than against the frame before it. What
            // decides whether the compositor has to invent a frame is the refresh period, and
            // a quarter of one is about the slack a frame can take up without costing one.
            var hz = VrRuntime.RefreshHz;
            if (hz > 1f && dt > 1.25f / hz) _late++;

            if (_elapsed < Window) return;

            Draw(_frames / _elapsed, _worst, _late);
            Forget();
        }

        private void Forget()
        {
            _frames = 0;
            _elapsed = 0f;
            _worst = 0f;
            _late = 0;
        }

        /// <summary>
        /// Redrawn twice a second, not every frame. A <c>Text</c> whose string changes rebuilds
        /// its mesh, and a frame-rate counter that costs frames to read is a poor instrument.
        /// </summary>
        private void Draw(float fps, float worst, int late)
        {
            if (_text == null) return;

            var hz = VrRuntime.RefreshHz;

            // Coloured against the headset, which is the only number that matters here: below
            // its refresh rate the compositor starts making frames up, and that is the whole
            // of what "it judders" means. White when the refresh rate is unknown rather than
            // green, because a colour nothing was compared against would be a claim.
            var colour = hz < 1f ? "ffffff"
                       : fps >= hz * 0.97f ? "9dff9d"
                       : fps >= hz * 0.85f ? "ffd24d"
                       : "ff6b6b";

            var head = $"<color=#{colour}>{fps:F0} fps</color>";
            if (hz > 1f) head += $" / {hz:F0} Hz";

            _text.text = head + $"\nworst {worst * 1000f:F1} ms" + (late > 0 ? $"    late {late}" : "");
        }

        // -- construction --------------------------------------------------------------

        private bool Build()
        {
            var font = VrMenu.FindFont();
            if (font == null)
            {
                Plugin.Log.LogError("No font could be obtained, so there is nothing to show the "
                                  + "frame rate on. Leaving the counter off.");
                _failed = true;
                return false;
            }

            _root = new GameObject("NobetaVR FPS");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.hideFlags = HideFlags.HideAndDontSave;

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.GetComponent<RectTransform>().sizeDelta = new Vector2(Width, Height);
            _root.transform.localScale = Vector3.one * Scale;

            var background = new GameObject("Background").AddComponent<Image>();
            background.transform.SetParent(_root.transform, false);
            background.color = new Color(0f, 0f, 0f, 0.5f);
            Stretch(background.GetComponent<RectTransform>());

            var textObject = new GameObject("Text");
            textObject.transform.SetParent(_root.transform, false);
            _text = textObject.AddComponent<Text>();
            _text.font = font;
            _text.fontSize = 34;
            _text.color = Color.white;
            _text.supportRichText = true;
            _text.alignment = TextAnchor.MiddleCenter;
            _text.horizontalOverflow = HorizontalWrapMode.Overflow;
            _text.verticalOverflow = VerticalWrapMode.Overflow;
            Stretch(_text.GetComponent<RectTransform>());

            OnTop(background);
            OnTop(_text);

            _root.SetActive(false);
            return true;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// Draws over everything, the HUD panel included.
        ///
        /// The panel is drawn last of all with its depth test off, so anything left on the
        /// default interface queue is at the mercy of whatever the game has opaque at that
        /// spot — and a readout you cannot read while the item bar is up is not a readout. The
        /// same two properties the panel sets on itself, one queue further along.
        ///
        /// The font atlas survives the swap: uGUI takes a Graphic's texture from
        /// <c>mainTexture</c> and only its shader and properties from the material, which is
        /// what makes a custom material on a <c>Text</c> an ordinary thing to do rather than a
        /// way to lose the glyphs.
        /// </summary>
        private static void OnTop(Graphic graphic)
        {
            var shader = TransparentShader.Find();
            if (shader == null) return;

            var material = new Material(shader) { renderQueue = 4000 };
            var always = (int)UnityEngine.Rendering.CompareFunction.Always;
            material.SetInt("unity_GUIZTestMode", always);
            material.SetInt("_ZTest", always);

            graphic.material = material;
        }
    }
}
