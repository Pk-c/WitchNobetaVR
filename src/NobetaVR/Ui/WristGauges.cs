using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using NobetaVR.Vr;
using UnityEngine;
using UnityEngine.XR;

namespace NobetaVR.Ui
{
    /// <summary>
    /// Health, stamina and mana, worn round the left wrist as three bands.
    ///
    /// The game draws these three across the top of the screen, which works on a monitor
    /// because the whole screen is already in front of you. In a headset the top of the view
    /// is somewhere you have to look up at, and whatever is drawn there sits between you and
    /// the room for as long as it is up. Three bands on your wrist cost nothing while you are
    /// not looking at them and are read by turning your arm, which is a movement you make
    /// anyway and one nobody has to be taught.
    ///
    /// <para>
    /// <b>Geometry rather than a canvas.</b> The first version was flat uGUI images on a
    /// world-space canvas, and a flat panel worn on a round wrist is a flat panel worn on a
    /// round wrist — it reads as a screen taped to your arm, and it thins to a line the moment
    /// you see it edge-on. These are arcs of tube generated here: a strip following a circle
    /// about the forearm, with a rounded cross-section whose brightness falls off from the
    /// crest. That falloff is the whole of the roundness, and it is carried in vertex colours,
    /// so it costs no shader, no texture and no light — which matters, because this build has
    /// been stripped to what the game itself uses and a custom shader is not something that
    /// can be added at runtime.
    /// </para>
    ///
    /// <para>
    /// What cannot honestly be done this way is a true glow. Bloom belongs to the game's own
    /// post-processing and there is no way to ask for it from here. What there is instead is
    /// <c>WristGaugeGlow</c>, which drives the lit colour past 1: on a camera rendering to an
    /// HDR buffer that is exactly what a bloom threshold looks for and the bands bloom for
    /// free, and on one that is not it clamps to full saturation and costs nothing. Either way
    /// the roundness above is doing most of the work.
    /// </para>
    ///
    /// The pose comes from the controller rather than from the character's hand. The hand is
    /// only posed while she is yours — cutscenes, conversations and death give her whole body
    /// back to the game — and a health gauge that vanished whenever the game took over would be
    /// missing at exactly the times you want to check it.
    /// </summary>
    public sealed class WristGauges : MonoBehaviour
    {
        public WristGauges(IntPtr ptr) : base(ptr) { }

        // -- shape -----------------------------------------------------------------------

        /// <summary>Steps around the wrist. The band is short and near the eye; 40 is smooth.</summary>
        private const int MajorSegments = 40;

        /// <summary>Steps across the tube. Five is enough for the falloff to read as round.</summary>
        private const int MinorSegments = 5;

        /// <summary>
        /// How far round its own axis the tube is drawn, either side of the crest.
        ///
        /// Past 90 the band curls back towards the arm, which is what gives it a rounded
        /// silhouette from a grazing angle instead of ending in a visible flat edge. Not the
        /// full 180: the far half is inside the wrist, where the arm's own mesh covers it, so
        /// drawing it would be paying for geometry nobody can see.
        /// </summary>
        private const float MinorSpan = 110f;

        private const int Bands = 3;
        private const int RingVertices = (MajorSegments + 1) * (MinorSegments + 1);

        /// <summary>Below this fraction a gauge pulses, the way the game's own bars do.</summary>
        private const float WarnBelow = 0.25f;

        /// <summary>
        /// How the bands sit on the wrist before any adjustment is made to them.
        ///
        /// Measured on a controller, not derived. An earlier version worked the angles out from
        /// the device's own axes and the reasoning was sound and the answer was wrong: a rest
        /// pose is a fact about how a thing is held, and there is no substitute for holding it.
        /// Baking it here is also what makes the three adjustment settings useful — they were
        /// carrying the whole orientation, so nothing could be nudged without re-deriving the
        /// rest of it, and zero meant a pose nobody wanted.
        ///
        /// Its <b>+Y is the forearm</b>: the bands stack along it and each one wraps about it.
        /// Its +Z is the face you read, so the arc is centred there. The offsets are taken in
        /// this same frame, before the adjustment angles, so a nudge of the pitch turns the
        /// bands without also moving them.
        /// </summary>
        private static readonly Quaternion Rest = Quaternion.Euler(0f, -45f, 30f);

        // -- palette ---------------------------------------------------------------------

        /// <summary>
        /// Blue, violet, red, stacked in that order down the arm.
        ///
        /// The violet is the one that had to be chosen rather than given, and it is chosen for
        /// distance from the blue it sits next to: light and leaning towards magenta, at an L*
        /// of about 65 against the blue's 52 and the red's 35. Blue against violet is the
        /// pairing here that gets closest — it is what red against green was in the previous
        /// palette — and hue alone will not separate them at five millimetres across a room,
        /// so the gap is carried in lightness, which survives peripheral vision, colour
        /// blindness and the bloom a headset puts on a saturated colour.
        /// </summary>
        private static readonly Color Mana = new(0.020f, 0.510f, 0.792f);     // #0582CA
        private static readonly Color Stamina = new(0.780f, 0.490f, 1.000f);  // #C77DFF
        private static readonly Color Health = new(0.639f, 0.086f, 0.129f);   // #A31621

        /// <summary>
        /// The unlit channel each band runs in. Dark, and not black: an invisible trough shows
        /// you what you have and not what you are missing, which is half of what a gauge is for.
        /// </summary>
        private static readonly Color TroughPaint = new(0.16f, 0.15f, 0.14f);

        /// <summary>How solid each layer is, before the whole thing is faded.</summary>
        private const float TroughAlpha = 0.55f;
        private const float GhostAlpha = 0.85f;

        private sealed class Bar
        {
            public Color Colour;
            public Color Warned;
            public Func<CharacterBaseData, float> Read;

            /// <summary>What the band is showing, and what the ghost behind it still is.</summary>
            public float Shown = 1f;
            public float Lost = 1f;
        }

        /// <summary>One mesh carrying all three bands, at one depth in the stack.</summary>
        private sealed class Layer
        {
            public Mesh Mesh;
            public Material Material;
            public Il2CppStructArray<Vector3> Vertices;
            public Il2CppStructArray<Color> Colours;
        }

        private readonly Bar[] _bars = new Bar[Bands];

        private GameObject _root;
        private Layer _trough, _ghost, _fill;
        private bool _failed;
        private float _alpha;

        /// <summary>The shape the trough was last built at, so a static mesh is not rebuilt
        /// for nothing.</summary>
        private Vector4 _builtShape = Vector4.one * -1f;

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
        /// bands and the hand disagreed by exactly that filter every time the hand moved
        /// quickly. Two readings of one thing cannot be kept in step by tuning; there has to be
        /// one reading. This is it, and it also settles the component-order question that would
        /// otherwise sit underneath it, because the call arrives from inside the hand's own
        /// placement rather than from a LateUpdate racing it.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        internal static void Follow(Vector3 handPosition, Quaternion controllerRotation)
        {
            var self = Instance;
            if (self == null || self._failed || !Plugin.Instance.WristGauges.Value) return;

            self._drivenFrame = Time.frameCount;
            self.Render(handPosition, controllerRotation);
        }

        /// <summary>
        /// The fallback, for every frame the hands are not being placed: menus, conversations,
        /// cutscenes, death. The bands stay on through all of those — a health gauge that went
        /// away whenever the game took her would be missing at the times you most want it — so
        /// the controller is read here instead.
        ///
        /// Stands aside for a frame after the hands last drove it, rather than for the same
        /// frame only. Component order within one GameObject is not something to depend on: if
        /// this runs first, the hands' call is still to come, and doing the work twice would
        /// place the bands at the raw pose and then at the steadied one every frame.
        /// </summary>
        private void LateUpdate()
        {
            if (_failed) return;
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
            if (_root == null && !Build()) return;

            var data = CharacterData();
            if (data == null) { FadeTo(0f); return; }

            Place(handPosition, controllerRotation);
            Read(data);
            Reshape();
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
        /// Damage lands on the band as damage; everything else is smoothed.
        ///
        /// A hit that eased in would be a hit you found out about a moment late, which is the
        /// one direction where the animation costs you something. The dimmer ghost behind the
        /// lit part is the other half of it: it holds where the band was and slides down after,
        /// so a glance a second later still shows how much that cost. Refilling reads better as
        /// a band filling than as one that was simply longer next time you looked.
        /// </summary>
        private void Read(CharacterBaseData data)
        {
            var refill = Mathf.Max(0.01f, Plugin.Instance.WristGaugeFillSpeed.Value)
                       * Time.unscaledDeltaTime;
            var settle = refill * 0.45f;

            foreach (var bar in _bars)
            {
                if (bar == null) continue;

                var value = Mathf.Clamp01(bar.Read(data));

                bar.Shown = value < bar.Shown ? value : Mathf.MoveTowards(bar.Shown, value, refill);
                bar.Lost = bar.Lost < bar.Shown ? bar.Shown : Mathf.MoveTowards(bar.Lost, bar.Shown, settle);
            }
        }

        private static float Fraction(float value, float max) => max > 0.0001f ? value / max : 0f;

        // -- placement -------------------------------------------------------------------

        /// <summary>
        /// Takes the wrist pose rather than reading it, so that when the hands are being drawn
        /// this is the very pose the hand went to. See <see cref="Follow"/>.
        ///
        /// How the bands sit is <see cref="Rest"/>; the settings are corrections from there,
        /// and all three angles are zero by default.
        /// </summary>
        private void Place(Vector3 wrist, Quaternion controller)
        {
            var cfg = Plugin.Instance;

            var worn = controller * Rest;

            _root.transform.position = wrist + worn * new Vector3(cfg.WristGaugeOffsetX.Value,
                                                                  cfg.WristGaugeOffsetY.Value,
                                                                  cfg.WristGaugeOffsetZ.Value);
            _root.transform.rotation = worn * Quaternion.Euler(cfg.WristGaugePitch.Value,
                                                               cfg.WristGaugeYaw.Value,
                                                               cfg.WristGaugeRoll.Value);
        }

        /// <summary>
        /// The left controller in world space, worked out the same way the hands are: the
        /// controller's offset from the headset is the wrist's offset from the eyes, turned
        /// into the world by the view's yaw.
        ///
        /// Only reached when the hands are not being drawn. Returns false when there is no left
        /// controller to read — a runtime that has not brought one up yet, or one that has gone
        /// to sleep — and the bands then fade out rather than freezing where they were.
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
            if (_root == null) return;

            _alpha = Mathf.MoveTowards(_alpha, target,
                Mathf.Max(0.01f, Plugin.Instance.HudFadeSpeed.Value) * Time.unscaledDeltaTime);

            // Carried on the shared materials rather than on every vertex, so fading is one
            // colour write per layer instead of a mesh rebuild.
            Tint(_trough, TroughAlpha);
            Tint(_ghost, GhostAlpha);
            Tint(_fill, 1f);

            var visible = _alpha > 0.001f;
            if (_root.activeSelf != visible) _root.SetActive(visible);
        }

        private void Tint(Layer layer, float layerAlpha)
        {
            if (layer != null && layer.Material != null)
                layer.Material.color = new Color(1f, 1f, 1f, _alpha * layerAlpha);
        }

        // -- geometry --------------------------------------------------------------------

        /// <summary>
        /// Writes this frame's shape into the three meshes.
        ///
        /// The trough is the full arc and only changes when a setting does, so it is rebuilt on
        /// a comparison rather than every frame. The other two are rebuilt always: their sweep
        /// *is* the reading, and the lit one's colour carries the warning pulse on top of it.
        /// </summary>
        private void Reshape()
        {
            var cfg = Plugin.Instance;

            var radius = Mathf.Max(0.005f, cfg.WristGaugeRadius.Value);
            var thickness = Mathf.Max(0.0005f, cfg.WristGaugeThickness.Value);
            var spacing = cfg.WristGaugeSpacing.Value;
            var arc = Mathf.Clamp(cfg.WristGaugeArc.Value, 10f, 350f);

            var shape = new Vector4(radius, thickness, spacing, arc);
            if (shape != _builtShape)
            {
                _builtShape = shape;
                Fill(_trough, radius, thickness, spacing, arc, 0f);
            }

            // Between the colour and a hotter version of it, not between the colour and
            // nothing. Pulsing towards transparent faded the band into the trough, which reads
            // as a gauge going out rather than as one asking for attention.
            var pulse = 0.5f + 0.5f * Mathf.Cos(Time.unscaledTime * 6f);

            // Stacked outwards by a fraction of a millimetre. They are drawn in queue order
            // rather than sorted, so this is belt and braces — but three coincident transparent
            // surfaces is exactly the arrangement that z-fights on some drivers and not others,
            // and a twentieth of a millimetre costs nothing to be sure.
            Fill(_ghost, radius + thickness * 0.02f, thickness, spacing, arc, 0f);
            Fill(_fill, radius + thickness * 0.04f, thickness, spacing, arc, pulse);
        }

        /// <summary>
        /// Lays one layer's three arcs into its vertex and colour arrays.
        ///
        /// The arc is centred on the frame's +Z — the face you read — and grows from one end,
        /// so a full band wraps the whole sweep and an empty one is nothing. A zero-length arc
        /// collapses to coincident vertices and draws nothing, which is why there is no special
        /// case for it: degenerate triangles are free and a branch here would not be.
        /// </summary>
        private void Fill(Layer layer, float radius, float thickness, float spacing, float arc,
                          float pulse)
        {
            if (layer == null) return;

            var glow = Mathf.Max(0.1f, Plugin.Instance.WristGaugeGlow.Value);
            var v = 0;

            for (var band = 0; band < Bands; band++)
            {
                var bar = _bars[band];

                // Negative spacing stacks them the other way, which is the whole of the fix if
                // they come out with mana at the hand rather than at the elbow.
                var along = (1 - band) * spacing;

                var fraction = ReferenceEquals(layer, _trough) ? 1f
                             : ReferenceEquals(layer, _fill) ? bar.Shown
                             : bar.Lost;

                var sweep = arc * Mathf.Clamp01(fraction);
                var start = -arc * 0.5f;

                var paint = Paint(layer, bar, pulse, glow);

                for (var i = 0; i <= MajorSegments; i++)
                {
                    var angle = (start + sweep * i / MajorSegments) * Mathf.Deg2Rad;
                    var outward = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));

                    for (var j = 0; j <= MinorSegments; j++)
                    {
                        var minor = (-MinorSpan + 2f * MinorSpan * j / MinorSegments) * Mathf.Deg2Rad;
                        var cos = Mathf.Cos(minor);
                        var sin = Mathf.Sin(minor);

                        layer.Vertices[v] = outward * (radius + thickness * cos)
                                          + Vector3.up * (along + thickness * sin);

                        // The roundness, and the only place it exists. Brightest along the
                        // crest and falling away to the sides, which is what a lit tube does
                        // and what a flat strip of one colour never can.
                        var lit = 0.42f + 0.58f * Mathf.Max(0f, cos);
                        layer.Colours[v] = new Color(paint.r * lit, paint.g * lit, paint.b * lit, 1f);
                        v++;
                    }
                }
            }

            layer.Mesh.vertices = layer.Vertices;
            layer.Mesh.colors = layer.Colours;
            layer.Mesh.RecalculateBounds();
        }

        private Color Paint(Layer layer, Bar bar, float pulse, float glow)
        {
            if (ReferenceEquals(layer, _trough)) return TroughPaint;
            if (ReferenceEquals(layer, _ghost)) return Ghost(bar.Colour);

            var lit = bar.Shown < WarnBelow ? Color.Lerp(bar.Colour, bar.Warned, pulse) : bar.Colour;
            return new Color(lit.r * glow, lit.g * glow, lit.b * glow, 1f);
        }

        /// <summary>
        /// What the band was, behind what it is. Taken towards the trough rather than towards
        /// black: multiplying a saturated colour down in sRGB crushes it into a dark mush that
        /// no longer says which band it belongs to, while a step towards the trough keeps the
        /// hue and lands it just clear of the channel, which is where it should sit.
        /// </summary>
        private static Color Ghost(Color colour) => Color.Lerp(colour, TroughPaint, 0.62f);

        /// <summary>The hot end of the warning pulse: the same colour, driven up.</summary>
        private static Color Warn(Color colour) => Color.Lerp(colour, Color.white, 0.45f);

        // -- construction ----------------------------------------------------------------

        private bool Build()
        {
            var shader = TransparentShader.Find();
            if (shader == null)
            {
                Plugin.Log.LogError("No alpha-blended shader survived stripping, so the wrist "
                                  + "gauges would be three opaque slabs. Leaving them off.");
                _failed = true;
                return false;
            }

            _root = new GameObject("NobetaVR Wrist Gauges");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.hideFlags = HideFlags.HideAndDontSave;

            // Blue, violet, red down the arm. Health is at the end of the run rather than the
            // start of it, which puts the one you cannot afford to miss nearest your hand —
            // and cool to warm reads as an ascending scale of how much it matters.
            _bars[0] = new Bar { Colour = Mana, Warned = Warn(Mana), Read = d => Fraction(d.GetMP(), d.GetMPMax()) };
            _bars[1] = new Bar { Colour = Stamina, Warned = Warn(Stamina), Read = d => Fraction(d.GetSP(), d.GetSPMax()) };
            _bars[2] = new Bar { Colour = Health, Warned = Warn(Health), Read = d => Fraction(d.GetHP(), d.GetHPMax()) };

            // Drawn in queue order rather than left to the distance sort. All three sit within
            // a fraction of a millimetre of each other, so their bounds centres are effectively
            // identical and the sort has nothing to work with.
            _trough = MakeLayer(shader, "Trough", 3000);
            _ghost = MakeLayer(shader, "Ghost", 3001);
            _fill = MakeLayer(shader, "Fill", 3002);

            Plugin.Log.LogInfo($"wrist gauges built: {Bands} bands, "
                             + $"{MajorSegments}x{MinorSegments} segments each, "
                             + $"shader '{shader.name}'");

            _root.SetActive(false);
            return true;
        }

        private Layer MakeLayer(Shader shader, string name, int queue)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            go.layer = 0;   // seen by the game's cameras, not by the HUD capture camera

            var layer = new Layer
            {
                Mesh = new Mesh { name = $"NobetaVR Wrist {name}" },
                Material = new Material(shader),
                Vertices = new Il2CppStructArray<Vector3>(Bands * RingVertices),
                Colours = new Il2CppStructArray<Color>(Bands * RingVertices),
            };

            // Rewritten every frame, so Unity is told not to treat the buffer as static.
            layer.Mesh.MarkDynamic();
            layer.Mesh.vertices = layer.Vertices;
            layer.Mesh.triangles = Topology();

            // White with the fade in its alpha; the bands' own colours are on the vertices, and
            // UI/Default multiplies the two — which is what lets one material serve all three.
            layer.Material.color = new Color(1f, 1f, 1f, 0f);
            layer.Material.mainTexture = Texture2D.whiteTexture;
            layer.Material.renderQueue = queue;

            // Depth tested, and said so out loud.
            //
            // `UI/Default` declares `ZTest [unity_GUIZTestMode]` so that a canvas can choose,
            // which means a material that never chooses takes whatever that global happens to
            // hold — and outside the canvas system it holds nothing useful, so the bands were
            // drawing over everything including each other. That is not the bands being sorted
            // wrongly, it is the bands not being depth tested at all: the far side of an arc
            // came out on top of the near side because it was drawn later, and the arm behind
            // them never got a say. The two other places the mod hangs geometry in the world
            // both set this too, in the other direction, which is what made it look deliberate
            // rather than missing.
            layer.Material.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            layer.Material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);

            // sharedMesh, not mesh: the `mesh` accessor is the one that quietly clones, and a
            // clone would leave every per-frame rebuild being written to a mesh nothing draws.
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = layer.Mesh;

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.material = layer.Material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            return layer;
        }

        /// <summary>
        /// The triangle list, which never changes: only the vertices move. There is no need to
        /// wind both ways — <c>UI/Default</c> is <c>Cull Off</c>, so a band is solid from either
        /// side and from inside the wrist as well.
        /// </summary>
        private static Il2CppStructArray<int> Topology()
        {
            var indices = new Il2CppStructArray<int>(Bands * MajorSegments * MinorSegments * 6);
            var t = 0;

            for (var band = 0; band < Bands; band++)
            {
                var origin = band * RingVertices;

                for (var i = 0; i < MajorSegments; i++)
                {
                    for (var j = 0; j < MinorSegments; j++)
                    {
                        var a = origin + i * (MinorSegments + 1) + j;
                        var b = a + 1;
                        var c = a + (MinorSegments + 1);
                        var d = c + 1;

                        indices[t++] = a; indices[t++] = c; indices[t++] = b;
                        indices[t++] = b; indices[t++] = c; indices[t++] = d;
                    }
                }
            }

            return indices;
        }
    }
}
