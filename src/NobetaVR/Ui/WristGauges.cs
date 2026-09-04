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
    /// flat screen that is shaped like this to redirect, and a world-space canvas renders in
    /// stereo by itself at full resolution with no texture in the middle. Six plain Images,
    /// no sprite and no font, so there is nothing here that stripping could have taken away.
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
        /// Canvas units, converted to metres by the root's scale below. Authored large and
        /// scaled down for the same reason the VR menu is: uGUI lays out badly at millimetre
        /// sizes, and it costs nothing to do the arithmetic here instead.
        /// </summary>
        private const float BarWidth = 100f;
        private const float BarHeight = 10f;
        private const float BarGap = 7f;
        private const float MillimetresPerUnit = 0.001f;

        private sealed class Bar
        {
            public RectTransform Fill;
            public Func<CharacterBaseData, float> Read;
            public float Shown;
        }

        private readonly Bar[] _bars = new Bar[3];

        private GameObject _root;
        private CanvasGroup _group;
        private bool _failed;
        private float _alpha;

        private void LateUpdate()
        {
            if (_failed) return;

            var wanted = Plugin.Instance.WristGauges.Value;
            if (!wanted && _root == null) return;
            if (_root == null && !Build()) return;

            var data = CharacterData();
            var placed = wanted && data != null && Place();

            if (placed) Read(data);
            FadeTo(placed ? Plugin.Instance.WristGaugeOpacity.Value : 0f);
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

        private void Read(CharacterBaseData data)
        {
            // Damage should land on the bar as damage. The catch-up is there for the other
            // direction — a potion or a stamina refill reads better as a bar filling than as
            // one that was simply longer the next time you looked at it.
            var step = Mathf.Max(0.01f, Plugin.Instance.WristGaugeFillSpeed.Value)
                     * Time.unscaledDeltaTime;

            foreach (var bar in _bars)
            {
                if (bar == null || bar.Fill == null) continue;

                var value = Mathf.Clamp01(bar.Read(data));
                bar.Shown = value < bar.Shown
                    ? value
                    : Mathf.MoveTowards(bar.Shown, value, step);

                var size = bar.Fill.sizeDelta;
                bar.Fill.sizeDelta = new Vector2(BarWidth * bar.Shown, size.y);
            }
        }

        private static float Fraction(float value, float max) => max > 0.0001f ? value / max : 0f;

        // -- placement -------------------------------------------------------------------

        /// <summary>
        /// Puts the panel on the left controller, in the same way the hands are placed: the
        /// controller's offset from the headset is the panel's offset from the eyes, turned
        /// into the world by the view's yaw. Nothing in between, so it is exactly where your
        /// hand is.
        ///
        /// Returns false when there is no left controller to read, which is the case on a
        /// runtime that has not brought one up yet as well as on a controller that has gone
        /// to sleep. The panel fades out rather than freezing where it last was.
        /// </summary>
        private bool Place()
        {
            var device = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            if (!device.isValid) return false;

            if (!InputDevices.TryGetFeatureValue_Vector3f(device.deviceId, "DevicePosition", out var position)
             || !InputDevices.TryGetFeatureValue_Quaternionf(device.deviceId, "DeviceRotation", out var rotation))
                return false;

            var camera = VrCamera.CameraTransform;
            if (camera == null) return false;

            var cfg = Plugin.Instance;

            var world = camera.position + VrCamera.ViewYaw * (position - HeadPose.Raw);
            var facing = VrCamera.ViewYaw * rotation
                       * Quaternion.Euler(cfg.WristGaugePitch.Value,
                                          cfg.WristGaugeYaw.Value,
                                          cfg.WristGaugeRoll.Value);

            // The offset is taken along the panel's own axes rather than the controller's, so
            // the three sliders mean what they look like they mean once it has been angled:
            // up is up the panel, not up the controller.
            _root.transform.position = world + facing * new Vector3(cfg.WristGaugeOffsetX.Value,
                                                                    cfg.WristGaugeOffsetY.Value,
                                                                    cfg.WristGaugeOffsetZ.Value);
            _root.transform.rotation = facing;
            _root.transform.localScale =
                Vector3.one * (MillimetresPerUnit * Mathf.Max(0.1f, cfg.WristGaugeScale.Value));

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

        private bool Build()
        {
            _root = new GameObject("NobetaVR Wrist Gauges");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.hideFlags = HideFlags.HideAndDontSave;

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var height = BarHeight * 3f + BarGap * 2f;
            var rect = canvas.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(BarWidth, height);

            _group = _root.AddComponent<CanvasGroup>();
            _group.alpha = 0f;

            // Top to bottom in the order they were asked for, which is also the order they
            // are read in: the one you die without, the one you run out of, the one you cast
            // with.
            _bars[0] = Row(0, new Color(0.89f, 0.27f, 0.36f), d => Fraction(d.GetHP(), d.GetHPMax()));
            _bars[1] = Row(1, new Color(0.48f, 0.85f, 0.42f), d => Fraction(d.GetSP(), d.GetSPMax()));
            _bars[2] = Row(2, new Color(0.29f, 0.66f, 0.89f), d => Fraction(d.GetMP(), d.GetMPMax()));

            _root.SetActive(false);
            Plugin.Log.LogInfo("wrist gauges built");
            return true;
        }

        /// <summary>
        /// One bar: a dark trough and a coloured fill anchored to its left edge.
        ///
        /// The fill is resized rather than filled by a shader. <c>Image.Type.Filled</c> needs a
        /// sprite, and there is no sprite here to give it — a plain Image with no sprite draws
        /// a solid quad, which is exactly what a bar is.
        /// </summary>
        private Bar Row(int index, Color colour, Func<CharacterBaseData, float> read)
        {
            var top = -(BarHeight + BarGap) * index;

            var trough = Panel("Trough", new Color(0.03f, 0.03f, 0.05f, 0.72f));
            trough.anchorMin = trough.anchorMax = new Vector2(0f, 1f);
            trough.pivot = new Vector2(0f, 1f);
            trough.anchoredPosition = new Vector2(0f, top);
            trough.sizeDelta = new Vector2(BarWidth, BarHeight);

            var fill = Panel("Fill", colour);
            fill.SetParent(trough, false);
            fill.anchorMin = fill.anchorMax = new Vector2(0f, 0.5f);
            fill.pivot = new Vector2(0f, 0.5f);
            fill.anchoredPosition = Vector2.zero;
            fill.sizeDelta = new Vector2(BarWidth, BarHeight);

            return new Bar { Fill = fill, Read = read, Shown = 1f };
        }

        private RectTransform Panel(string name, Color colour)
        {
            var image = new GameObject(name).AddComponent<Image>();
            image.transform.SetParent(_root.transform, false);
            image.color = colour;
            image.raycastTarget = false;
            return image.rectTransform;
        }
    }
}
