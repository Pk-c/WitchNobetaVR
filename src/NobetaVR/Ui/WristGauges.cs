using System;
using NobetaVR.Vr;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

namespace NobetaVR.Ui
{
    /// <summary>
    /// Health, stamina and mana, worn on the left wrist.
    ///
    /// The game draws these three across the top of the screen, which works on a monitor
    /// because the whole screen is already in front of you. In a headset the top of the view
    /// is somewhere you have to look up at, and whatever is drawn there sits between you and
    /// the room for as long as it is up. Three short bars on the back of your hand cost
    /// nothing while you are not looking at them and are read by turning your wrist, which is
    /// a movement you make anyway and one nobody has to be taught.
    ///
    /// Built rather than captured, unlike the rest of the interface: there is nothing on the
    /// flat screen shaped like this to redirect, and a world-space canvas renders in stereo by
    /// itself at full resolution with no texture in the middle. Plain Images throughout — no
    /// sprite, no font, no shader of its own — so there is nothing here that stripping could
    /// have taken away, and the whole thing is one draw pass of coloured quads.
    ///
    /// The pose comes from the controller rather than from the character's hand. The hand is
    /// only posed while she is yours — cutscenes, conversations and death give her whole body
    /// back to the game — and a health bar that vanished whenever the game took over would be
    /// missing at exactly the times you want to check it.
    /// </summary>
    public sealed class WristGauges : MonoBehaviour
    {
        public WristGauges(IntPtr ptr) : base(ptr) { }

        /// <summary>
        /// Canvas units, converted to metres by the root's scale below — one unit is one
        /// millimetre at a scale of 1, so the numbers here read as the size of the thing.
        /// Authored large and scaled down for the same reason the VR menu is: uGUI lays out
        /// badly at millimetre sizes, and the arithmetic costs nothing here instead.
        /// </summary>
        private const float BarWidth = 52f;
        private const float BarHeight = 5f;
        private const float BarGap = 3f;
        private const float Padding = 3f;
        private const float Bezel = 1f;
        private const float MillimetresPerUnit = 0.001f;

        private static float OuterWidth => BarWidth + 2f * (Padding + Bezel);
        private static float OuterHeight => BarHeight * 3f + BarGap * 2f + 2f * (Padding + Bezel);

        /// <summary>Below this fraction a gauge pulses, the way the game's own bars do.</summary>
        private const float WarnBelow = 0.25f;

        /// <summary>
        /// The palette, in one place, because it only works as one.
        ///
        /// <para><b>Red, amber, blue rather than red, green, blue.</b> Health beside stamina was
        /// the one pairing to avoid: red against green is what the commonest colour blindness
        /// cannot separate, and it is also the pair the eye separates worst at this size — five
        /// millimetres at arm's length is well under a degree of arc, where hue discrimination
        /// has largely given out and lightness is doing the work. Amber is a long way from
        /// crimson in lightness as well as hue, so the two stay apart for everyone, in the
        /// corner of the eye, and through the bloom a headset puts on saturated colour.</para>
        ///
        /// <para><b>Warm chrome.</b> The game's own interface is gold on parchment. A cool grey
        /// bezel read as a mod's overlay sitting on top of it; brass on a warm black reads as
        /// part of the same object, and it sets the bars off better than a neutral does — the
        /// frame is the only warm-neutral thing here, so the coloured bars stay the coloured
        /// things.</para>
        ///
        /// <para>The three step apart in lightness as well as hue — L* of roughly 35, 76 and 52,
        /// no two closer than seventeen — so they are still three distinguishable bars with the
        /// colour taken away entirely, which is what covers peripheral vision and the bloom.</para>
        /// </summary>
        private static readonly Color BezelPaint = new(0.66f, 0.56f, 0.35f, 0.34f);
        private static readonly Color PlatePaint = new(0.055f, 0.045f, 0.040f, 0.86f);
        /// <summary>
        /// Lighter than the plate, not darker.
        ///
        /// A trough darker than a near-black plate is invisible, and then the only thing on the
        /// gauge is the part that is full — you can see what you have and not what you are
        /// missing, which is half of what a bar is for. Lifting it matters more with a health
        /// colour this deep: #A31621 against black is a dark thing on a dark thing.
        /// </summary>
        private static readonly Color TroughPaint = new(0.22f, 0.21f, 0.20f, 0.55f);

        private static readonly Color Health = new(0.639f, 0.086f, 0.129f);   // #A31621
        private static readonly Color Stamina = new(0.965f, 0.682f, 0.176f);  // #F6AE2D
        private static readonly Color Mana = new(0.020f, 0.510f, 0.792f);     // #0582CA

        private sealed class Bar
        {
            public RectTransform Fill;
            public RectTransform Ghost;
            public Image Paint;
            public Color Colour;
            public Color Warned;
            public Func<CharacterBaseData, float> Read;

            /// <summary>What the fill is showing, and what the ghost behind it still is.</summary>
            public float Shown = 1f;
            public float Lost = 1f;
        }

        private readonly Bar[] _bars = new Bar[3];

        private GameObject _root;
        private CanvasGroup _group;
        private float _alpha;

        internal static WristGauges Instance { get; private set; }

        /// <summary>Frame on which the hands drove us; see <see cref="Follow"/>.</summary>
        private int _drivenFrame = -1;

        private void Awake() => Instance = this;

        /// <summary>
        /// Placed from the same pose the left hand was placed at, on the same frame, by the
        /// code that placed it.
        ///
        /// Sampling the controller a second time here looked equivalent and is not. The hand is
        /// steadied at the source — <c>HandSteadiness</c> filters the pose before the hand or
        /// the aim ray is taken from it — so a second, raw sample is a different pose, and the
        /// gauge and the hand disagreed by exactly that filter every time the hand moved
        /// quickly. Two readings of one thing cannot be kept in step by tuning; there has to be
        /// one reading. This is it, and it also settles the component-order question that would
        /// otherwise sit underneath it, because the call arrives from inside the hand's own
        /// placement rather than from a LateUpdate racing it.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        internal static void Follow(Vector3 handPosition, Quaternion controllerRotation)
        {
            var self = Instance;
            if (self == null || !Plugin.Instance.WristGauges.Value) return;

            self._drivenFrame = Time.frameCount;
            self.Render(handPosition, controllerRotation);
        }

        /// <summary>
        /// The fallback, for every frame the hands are not being placed: menus, conversations,
        /// cutscenes, death. The gauges stay up through all of those — a health bar that went
        /// away whenever the game took her would be missing at the times you most want it — so
        /// the controller is read here instead.
        ///
        /// Stands aside for a frame after the hands last drove it, rather than for the same
        /// frame only. Component order within one GameObject is not something to depend on: if
        /// this runs first, the hands' call is still to come, and doing the work twice would
        /// place the panel at the raw pose and then at the steadied one every frame.
        /// </summary>
        private void LateUpdate()
        {
            if (_root == null && !Plugin.Instance.WristGauges.Value) return;
            if (_drivenFrame >= Time.frameCount - 1) return;

            if (!Plugin.Instance.WristGauges.Value || !SampleController(out var world, out var rotation))
            {
                FadeTo(0f);
                return;
            }

            Render(world, rotation);
        }

        private void Render(Vector3 handPosition, Quaternion controllerRotation)
        {
            if (_root == null) Build();

            var data = CharacterData();
            if (data == null) { FadeTo(0f); return; }

            Place(handPosition, controllerRotation);
            Read(data);
            FadeTo(Plugin.Instance.WristGaugeOpacity.Value);
        }

        // -- the numbers -----------------------------------------------------------------

        /// <summary>
        /// Read live and never cached. A stage reload builds a new character, and a reference
        /// kept across one is a reference to a body that no longer exists.
        /// </summary>
        private static CharacterBaseData CharacterData()
        {
            var controls = Input.VrControls.Instance;
            var camera = controls != null ? controls.Camera : null;
            var girl = camera != null ? camera.wizardGirl : null;
            return girl != null ? girl.g_CharData : null;
        }

        /// <summary>
        /// Damage lands on the bar as damage; everything else is smoothed.
        ///
        /// A hit that eased in would be a hit you found out about a moment late, which is the
        /// one direction where the animation costs you something. The dimmer ghost behind the
        /// fill is the other half of it: it holds where the bar was and slides down after,
        /// so a glance a second later still shows you how much that cost. Refilling reads
        /// better as a bar filling than as one that was simply longer next time you looked.
        /// </summary>
        private void Read(CharacterBaseData data)
        {
            var dt = Time.unscaledDeltaTime;
            var refill = Mathf.Max(0.01f, Plugin.Instance.WristGaugeFillSpeed.Value) * dt;
            var settle = refill * 0.45f;

            // Between the colour and a hotter version of it, not between the colour and
            // nothing. Pulsing the alpha faded the bar towards the trough, which reads as a
            // gauge going out rather than as one asking for attention.
            var pulse = 0.5f + 0.5f * Mathf.Cos(Time.unscaledTime * 6f);

            foreach (var bar in _bars)
            {
                if (bar == null || bar.Fill == null) continue;

                var value = Mathf.Clamp01(bar.Read(data));

                bar.Shown = value < bar.Shown ? value : Mathf.MoveTowards(bar.Shown, value, refill);
                bar.Lost = bar.Lost < bar.Shown ? bar.Shown : Mathf.MoveTowards(bar.Lost, bar.Shown, settle);

                Width(bar.Fill, bar.Shown);
                Width(bar.Ghost, bar.Lost);

                // The pulse is the game's own warning behaviour, kept: at a quarter left the
                // bar is something you need to notice without looking at it.
                bar.Paint.color = bar.Shown < WarnBelow
                    ? Color.Lerp(bar.Colour, bar.Warned, pulse)
                    : bar.Colour;
            }
        }

        private static void Width(RectTransform rect, float fraction)
        {
            if (rect == null) return;
            rect.sizeDelta = new Vector2(BarWidth * fraction, BarHeight);
        }

        private static float Fraction(float value, float max) => max > 0.0001f ? value / max : 0f;

        // -- placement -------------------------------------------------------------------

        /// <summary>
        /// Puts the panel on the left controller, the same way the hands are placed: the
        /// controller's offset from the headset is the panel's offset from the eyes, turned
        /// into the world by the view's yaw. Nothing in between, so it is exactly where your
        /// hand is.
        ///
        /// <para>
        /// The default angles lay it flat across the back of the hand, reading away from you,
        /// so it is square on the wrist with the palm down and a turn of the wrist brings it
        /// up. They are derived rather than dialled in: the mod already aims the wand along
        /// the controller's own +Z — that is what <c>AimDirection</c> is — so +Z is the
        /// pointing direction and +Y is up out of the controller. A canvas faces its own +Z,
        /// so a pitch of -90° lays it face-up, and the roll of 180° is what puts the top of
        /// the readout towards the fingers instead of towards the elbow.
        /// </para>
        ///
        /// The offset is taken along the *controller's* axes rather than the panel's, so the
        /// three sliders keep meaning the same thing however it has been angled: Z is along
        /// the forearm, Y is up out of the controller, X is across it.
        ///
        /// Takes the wrist pose rather than reading it, so that when the hands are being drawn
        /// this is the very pose the hand went to. See <see cref="Follow"/>.
        /// </summary>
        private void Place(Vector3 wrist, Quaternion controller)
        {
            var cfg = Plugin.Instance;

            _root.transform.position = wrist + controller * new Vector3(cfg.WristGaugeOffsetX.Value,
                                                                        cfg.WristGaugeOffsetY.Value,
                                                                        cfg.WristGaugeOffsetZ.Value);
            _root.transform.rotation = controller * Quaternion.Euler(cfg.WristGaugePitch.Value,
                                                                     cfg.WristGaugeYaw.Value,
                                                                     cfg.WristGaugeRoll.Value);
            _root.transform.localScale =
                Vector3.one * (MillimetresPerUnit * Mathf.Max(0.1f, cfg.WristGaugeScale.Value));
        }

        /// <summary>
        /// The left controller in world space, worked out the same way the hands are: the
        /// controller's offset from the headset is the wrist's offset from the eyes, turned
        /// into the world by the view's yaw.
        ///
        /// Only reached when the hands are not being drawn. Returns false when there is no
        /// left controller to read — a runtime that has not brought one up yet, or one that
        /// has gone to sleep — and the panel then fades out rather than freezing where it was.
        /// </summary>
        private static bool SampleController(out Vector3 wrist, out Quaternion controller)
        {
            wrist = default;
            controller = Quaternion.identity;

            var device = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            if (!device.isValid) return false;

            if (!InputDevices.TryGetFeatureValue_Vector3f(device.deviceId, "DevicePosition", out var position)
             || !InputDevices.TryGetFeatureValue_Quaternionf(device.deviceId, "DeviceRotation", out var rotation))
                return false;

            var camera = VrCamera.CameraTransform;
            if (camera == null) return false;

            controller = VrCamera.ViewYaw * rotation;
            wrist = camera.position + VrCamera.ViewYaw * (position - HeadPose.Raw);
            return true;
        }

        private void FadeTo(float target)
        {
            if (_group == null) return;

            _alpha = Mathf.MoveTowards(_alpha, target,
                Mathf.Max(0.01f, Plugin.Instance.HudFadeSpeed.Value) * Time.unscaledDeltaTime);

            _group.alpha = _alpha;

            // Off entirely at zero rather than merely transparent: a canvas kept alive to draw
            // nothing still costs a rebuild every time one of its rects is touched.
            var visible = _alpha > 0.001f;
            if (_root.activeSelf != visible) _root.SetActive(visible);
        }

        // -- construction ----------------------------------------------------------------

        private void Build()
        {
            _root = new GameObject("NobetaVR Wrist Gauges");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.hideFlags = HideFlags.HideAndDontSave;

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var rect = canvas.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(OuterWidth, OuterHeight);

            _group = _root.AddComponent<CanvasGroup>();
            _group.alpha = 0f;

            // Drawn in hierarchy order, so this is also the stacking order: a faint bezel, the
            // plate the bars sit in, then the bars. The bezel is what stops three bars reading
            // as three things floating near your hand rather than one instrument on it.
            Plate("Bezel", BezelPaint, Vector2.zero, OuterWidth, OuterHeight);
            Plate("Plate", PlatePaint, new Vector2(Bezel, -Bezel),
                  OuterWidth - 2f * Bezel, OuterHeight - 2f * Bezel);

            // Top to bottom in the order they are read: the one you die without, the one you
            // run out of, the one you cast with. Warm to cool with it, which is also the order
            // they matter in when three things are all asking at once.
            _bars[0] = Row(0, Health, d => Fraction(d.GetHP(), d.GetHPMax()));
            _bars[1] = Row(1, Stamina, d => Fraction(d.GetSP(), d.GetSPMax()));
            _bars[2] = Row(2, Mana, d => Fraction(d.GetMP(), d.GetMPMax()));

            _root.SetActive(false);
            Plugin.Log.LogInfo($"wrist gauges built, {OuterWidth:F0}x{OuterHeight:F0} mm at scale 1");
        }

        /// <summary>
        /// One gauge: a dark trough, the ghost of what it was, the fill itself, and a highlight
        /// across the top of the fill.
        ///
        /// Everything is resized rather than filled by a shader. <c>Image.Type.Filled</c> wants
        /// a sprite and there is none here to give it — a plain Image with no sprite draws a
        /// solid quad, which is exactly what a bar is. The highlight is the cheapest thing that
        /// stops a flat rectangle looking like a flat rectangle: a lighter band over the upper
        /// half reads as a rounded, lit surface for the price of one more quad.
        /// </summary>
        private Bar Row(int index, Color colour, Func<CharacterBaseData, float> read)
        {
            var inset = Bezel + Padding;
            var top = -inset - (BarHeight + BarGap) * index;

            var trough = Plate($"Trough{index}", TroughPaint,
                               new Vector2(inset, top), BarWidth, BarHeight);

            var ghost = Bracket("Ghost", trough, Ghost(colour));
            var fill = Bracket("Fill", trough, colour);

            // The highlight rides the fill rather than the trough, stretched to it, so an
            // empty bar has no shine on the part of it that is empty.
            var gloss = Quad("Gloss", fill.transform, Gloss(colour)).rectTransform;
            gloss.anchorMin = new Vector2(0f, 1f);
            gloss.anchorMax = new Vector2(1f, 1f);
            gloss.pivot = new Vector2(0f, 1f);
            gloss.offsetMin = new Vector2(0f, -BarHeight * 0.45f);
            gloss.offsetMax = Vector2.zero;

            return new Bar
            {
                Fill = fill.rectTransform,
                Ghost = ghost.rectTransform,
                Paint = fill,
                Colour = colour,
                Warned = Warn(colour),
                Read = read,
            };
        }

        // -- shades of one colour --------------------------------------------------------
        //
        // Each of these is derived from the bar's own colour rather than picked, so the three
        // gauges cannot drift out of step with each other, and retuning the palette above is
        // the whole of retuning the palette.

        /// <summary>
        /// What the bar was, behind what it is. Taken towards the plate rather than towards
        /// black: multiplying a saturated colour down in sRGB crushes it into a dark mush that
        /// no longer says which bar it belongs to, while a step towards the plate keeps the
        /// hue and lands it just clear of the trough, which is exactly where it should sit.
        /// </summary>
        private static Color Ghost(Color colour) =>
            Color.Lerp(colour, new Color(PlatePaint.r, PlatePaint.g, PlatePaint.b, 1f), 0.70f);

        /// <summary>
        /// The highlight along the top of the fill. A lighter version of the bar's own colour
        /// rather than white: white over a saturated fill is white being mixed into it, and it
        /// took the top half of every bar back towards grey — the palette looked washed out and
        /// the cause was here rather than in the colours themselves.
        /// </summary>
        private static Color Gloss(Color colour)
        {
            var lit = Color.Lerp(colour, Color.white, 0.55f);
            return new Color(lit.r, lit.g, lit.b, 0.30f);
        }

        /// <summary>The hot end of the warning pulse: the same colour, driven up.</summary>
        private static Color Warn(Color colour) => Color.Lerp(colour, Color.white, 0.45f);

        /// <summary>A quad anchored to the panel's top-left corner, sized in canvas units.</summary>
        private Image Plate(string name, Color colour, Vector2 topLeft, float width, float height)
        {
            var image = Quad(name, _root.transform, colour);
            var rect = image.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = topLeft;
            rect.sizeDelta = new Vector2(width, height);
            return image;
        }

        /// <summary>A bar anchored to the left edge of its trough, grown rightwards.</summary>
        private static Image Bracket(string name, Image trough, Color colour)
        {
            var image = Quad(name, trough.transform, colour);
            var rect = image.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 0.5f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(BarWidth, BarHeight);
            return image;
        }

        private static Image Quad(string name, Transform parent, Color colour)
        {
            var image = new GameObject(name).AddComponent<Image>();
            image.transform.SetParent(parent, false);
            image.color = colour;
            image.raycastTarget = false;
            return image;
        }
    }
}
