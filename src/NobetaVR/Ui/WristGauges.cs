using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using NobetaVR.Vr;
using UnityEngine;

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
    /// <b>Sorting, which is where this got hard.</b> Every alpha-blended shader that survived
    /// stripping has <c>ZWrite Off</c> written into it, not exposed as a property, so these
    /// bands cannot write depth and cannot be resolved against each other by the depth buffer.
    /// Two things stand in for it. Between bands, each is its own renderer with its own bounds
    /// centre, so Unity's own back-to-front sort of transparent renderers has something real to
    /// sort by — the earlier arrangement of one mesh per *layer* gave all three identical
    /// centres and the sort nothing to work with, which is why it looked like there was no
    /// sorting at all. Within a band, the far side of the ring is removed instead of being
    /// ordered: its surface faces away from you, so it is faded out per vertex on the facing,
    /// which is back-face culling done on the processor because <c>Cull Off</c> is written into
    /// the shader as well. What is left inside one band is concentric and in index order, and
    /// that orders itself.
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
    /// They are worn on the hands and they go where the hands go. The mod draws her hands only
    /// while she is yours to move — cutscenes, conversations, menus and death all give her whole
    /// body back to the game — and three lit bands left hanging in the air where a hand is not
    /// would be worse than no bands at all. So the placement arrives from inside the hand's own,
    /// and a frame with no such call is the whole of the reading; see <see cref="Follow"/>.
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
        /// full 180: the far half is inside the wrist, so drawing it would be paying for
        /// geometry that is either hidden or faded out by the facing test anyway.
        /// </summary>
        private const float MinorSpan = 110f;

        private const int Bands = 3;
        private const int Rings = 3;   // trough, ghost, lit — nested, in that order
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
        /// The three channels: mana blue, stamina amber, health red, stacked in that order
        /// down the arm.
        ///
        /// <para>
        /// The discipline behind it is what makes the bands readable rather than merely pretty.
        /// Hue alone will not separate five millimetres of colour across a room — that is under
        /// a degree of arc, where hue discrimination has largely given out — so the gap between
        /// two bands is carried in lightness, which survives peripheral vision, colour blindness
        /// and the bloom a headset puts on a saturated colour. Only the adjacent pairs have to
        /// be told apart, since those are the two that are ever seen edge to edge.
        /// </para>
        ///
        /// <para>
        /// Amber is the lightest of the three at an L* of about 81 and red the darkest at 55,
        /// with stamina between the two, so it is the pair mana makes with amber that is the
        /// tightest on the arm: blue at 70 clears the amber above it by ten and is cleared by
        /// the red below it by twenty-six. Ten is a narrower step than the rest of this ladder
        /// wants, so that one pair leans on hue to finish the job — and blue against amber is
        /// the hue pair that survives nearly every form of colour blindness, which is the right
        /// place to be spending hue if it is to be spent anywhere.
        /// </para>
        /// </summary>
        private static readonly Color Mana = new(0.157f, 0.722f, 0.961f);     // #28B8F5
        private static readonly Color Stamina = new(1.000f, 0.745f, 0.094f);  // #FFBE18
        private static readonly Color Health = new(0.941f, 0.267f, 0.267f);   // #F04444

        /// <summary>
        /// The unlit channel each band runs in, and the rim it shows past them. Dark, and not
        /// black: an invisible trough shows you what you have and not what you are missing,
        /// which is half of what a gauge is for.
        /// </summary>
        private static readonly Color TroughPaint = new(0.125f, 0.137f, 0.153f);  // #202327

        /// <summary>How solid each ring is, before the whole thing is faded.</summary>
        private static readonly float[] RingAlpha = { 0.55f, 0.85f, 1f };

        /// <summary>
        /// How far apart the three rings of one band sit, as a fraction of the tube's thickness.
        ///
        /// They have to be concentric rather than coincident: index order puts the lit ring
        /// last so it wins where they overlap, but three surfaces at exactly one radius is the
        /// arrangement that z-fights on one driver and not another, and a twentieth of a
        /// millimetre costs nothing to be certain of.
        /// </summary>
        private const float RingStep = 0.02f;

        /// <summary>
        /// How much narrower than its trough the two rings it carries are, as a fraction of the
        /// tube's thickness, so that the trough shows as a rim all the way round a full band
        /// rather than being covered to the millimetre by it.
        ///
        /// <para>
        /// Taken off the fill rather than added to the trough, because the trough is what makes
        /// the band's silhouette: a rim added on the outside would grow every band by twice this
        /// and eat the gap between them, which at the default spacing of ten millimetres against
        /// a thickness of four and a half is only one millimetre wide to begin with. Taken off
        /// the inside, a band occupies exactly what the thickness setting says it does, and the
        /// rim comes out of the fill, which has it to spare.
        /// </para>
        ///
        /// <para>
        /// Eight per cent of the tube is about a third of a millimetre at the default
        /// thickness, which at arm's length is a few minutes of arc: enough to read as an edge,
        /// not enough to read as a border.
        /// </para>
        /// </summary>
        private const float TroughRim = 0.08f;

        /// <summary>
        /// The tube's cross-section, worked out once.
        ///
        /// Every vertex of every ring of every band sits at one of six angles around the tube,
        /// and those angles are fixed by <see cref="MinorSegments"/> and <see cref="MinorSpan"/>
        /// alone — nothing about the frame moves them. Computing them per vertex was two
        /// thousand sine and cosine calls a frame for six answers.
        /// </summary>
        private static readonly float[] MinorCos = MinorTable(true);
        private static readonly float[] MinorSin = MinorTable(false);

        /// <summary>
        /// How lit each of those six angles is: brightest along the crest, falling away to the
        /// sides. A function of the cross-section and nothing else, so it is tabled with it.
        /// </summary>
        private static readonly float[] MinorLit = MinorLitTable();

        private static float[] MinorTable(bool cosine)
        {
            var table = new float[MinorSegments + 1];

            for (var j = 0; j <= MinorSegments; j++)
            {
                var minor = (-MinorSpan + 2f * MinorSpan * j / MinorSegments) * Mathf.Deg2Rad;
                table[j] = cosine ? Mathf.Cos(minor) : Mathf.Sin(minor);
            }

            return table;
        }

        private static float[] MinorLitTable()
        {
            var table = new float[MinorSegments + 1];
            for (var j = 0; j <= MinorSegments; j++) table[j] = 0.42f + 0.58f * Mathf.Max(0f, MinorCos[j]);
            return table;
        }

        private sealed class Bar
        {
            public Color Colour;
            public Color Warned;
            public Func<CharacterBaseData, float> Read;

            /// <summary>What the band is showing, and what the ghost behind it still is.</summary>
            public float Shown = 1f;
            public float Lost = 1f;
        }

        /// <summary>
        /// One band: its own renderer, so that Unity's back-to-front sort of transparent
        /// renderers has a distinct bounds centre to sort it by.
        /// </summary>
        private sealed class Band
        {
            public Bar Bar;
            public Mesh Mesh;
            public Il2CppStructArray<Vector3> Vertices;
            public Il2CppStructArray<Color> Colours;

            /// <summary>
            /// The same points and surface normals as <see cref="Vertices"/>, on this side of
            /// the interop boundary.
            ///
            /// Kept because the colours are recomputed on frames the geometry is not, and the
            /// facing test needs both — so without a mirror every such frame would either read
            /// the il2cpp buffer back a vertex at a time or work the trigonometry out again to
            /// arrive at numbers it already had.
            /// </summary>
            public Vector3[] Points;
            public Vector3[] Normals;

            /// <summary>What the bar read when the geometry was last written; see Reshape.</summary>
            public float LastShown = float.NaN;
            public float LastLost = float.NaN;
        }

        private readonly Band[] _bands = new Band[Bands];

        private GameObject _root;
        private Material _material;
        private bool _failed;
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
        /// Fades them out on any frame the hands are not being placed.
        ///
        /// The bands are worn on the hands, so they go where the hands go: menus, cutscenes,
        /// conversations and death all hand her body back to the game, and three lit bands
        /// hanging in the air where a hand is not is worse than no bands at all. Nothing calls
        /// <see cref="Follow"/> on those frames, so the absence of a call is the whole of the
        /// reading — there is no second condition here to get out of step with the one the
        /// hands are already using.
        ///
        /// A frame's grace rather than none, because component order within one GameObject is
        /// not something to depend on: if this runs before the hands do, the call for this
        /// frame is still to come, and fading on the strength of that would strobe the bands
        /// off and on every frame they were up.
        /// </summary>
        private void LateUpdate()
        {
            if (_failed || _root == null) return;
            if (_drivenFrame >= Time.frameCount - 1) return;

            // Kept on the wrist while they go out. Nothing is driving the placement any more,
            // so the transform still holds the pose of the frame the hands were taken away on
            // — and the fade is long enough to walk an arm out from under it, which reads as
            // three lit bands left hanging in the air at the last place a hand was. Worse on
            // the way back: a stage load moves the world underneath a set of bands that were
            // never told, so they come back at a pose that has nothing to do with anywhere.
            if (_alpha > Invisible
             && VrHands.TryWristPose(UnityEngine.XR.XRNode.LeftHand, out var wrist, out var held))
                Place(wrist, held);

            FadeTo(0f);
        }

        private void Render(Vector3 handPosition, Quaternion controllerRotation)
        {
            if (_root == null && !Build()) return;

            // No character to read: a stage load, or the gap between one body and the next.
            // Placed anyway, for the reason in LateUpdate — the bands are on their way out,
            // and they have to go out on the wrist rather than wherever they last were.
            var data = CharacterData();
            if (data == null) { Place(handPosition, controllerRotation); FadeTo(0f); return; }

            Place(handPosition, controllerRotation);
            Read(data);

            var target = Plugin.Instance.WristGaugeOpacity.Value;

            // Nothing to shape into a mesh nobody can see: at zero opacity the bands are a
            // setting somebody turned off, and the frames either side of a fade are the only
            // ones where a shape at zero alpha is worth building. The bars are still *read*
            // above, so a gauge that moved while they were down is already at its right value
            // when they come back rather than sliding to it in front of you.
            if (_alpha > Invisible || target > Invisible) Reshape();

            FadeTo(target);
        }

        /// <summary>The alpha at or under which the bands are not worth building a mesh for.</summary>
        private const float Invisible = 0.001f;

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

            foreach (var band in _bands)
            {
                var bar = band?.Bar;
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

        private void FadeTo(float target)
        {
            if (_root == null) return;

            _alpha = Mathf.MoveTowards(_alpha, target,
                Mathf.Max(0.01f, Plugin.Instance.HudFadeSpeed.Value) * Time.unscaledDeltaTime);

            // Carried on the one shared material rather than on every vertex, so fading is a
            // single colour write instead of three mesh rebuilds.
            if (_material != null) _material.color = new Color(1f, 1f, 1f, _alpha);

            var visible = _alpha > 0.001f;
            if (_root.activeSelf != visible) _root.SetActive(visible);
        }

        // -- geometry --------------------------------------------------------------------

        /// <summary>How far the eye may drift, in metres, before the facing test is redone.</summary>
        private const float ViewerStill = 0.001f;

        private float _lastRadius = float.NaN;
        private float _lastThickness, _lastSpacing, _lastArc, _lastGlow, _lastPulse;
        private Vector3 _lastViewer;
        private bool _haveLastViewer;

        /// <summary>
        /// Writes this frame's shape into the three band meshes — or as much of it as this
        /// frame actually moved.
        ///
        /// The two halves of a band have nothing in common but the loop that used to write
        /// them. Where the vertices *are* depends on the shape settings and on what the bars
        /// read; what colour they are depends on where your eye is, which moves constantly,
        /// and on the warning pulse, which only exists on a band that is low. Rebuilding both
        /// together meant the expensive half — <c>Mesh.vertices</c>, which revalidates and
        /// re-uploads the whole stream, and the bounds that have to be recomputed after it —
        /// was paid on every frame the cheap half needed, which is every frame.
        ///
        /// So each band is asked two questions rather than one, and a steady gauge — which is
        /// what a gauge is nearly all of the time — writes colours and leaves its geometry
        /// alone.
        /// </summary>
        private void Reshape()
        {
            var cfg = Plugin.Instance;

            var radius = Mathf.Max(0.005f, cfg.WristGaugeRadius.Value);
            var thickness = Mathf.Max(0.0005f, cfg.WristGaugeThickness.Value);
            var spacing = cfg.WristGaugeSpacing.Value;
            var arc = Mathf.Clamp(cfg.WristGaugeArc.Value, 10f, 350f);
            var glow = Mathf.Max(0.1f, cfg.WristGaugeGlow.Value);

            // Between the colour and a hotter version of it, not between the colour and
            // nothing. Pulsing towards transparent faded the band into the trough, which reads
            // as a gauge going out rather than as one asking for attention.
            var pulse = 0.5f + 0.5f * Mathf.Cos(Time.unscaledTime * 6f);

            // The eye, in the bands' own space. Taken once: the two eyes are three centimetres
            // apart and the bands are forty away, which is far below what the facing test can
            // tell apart.
            var eye = VrCamera.CameraTransform;
            var viewer = eye != null ? _root.transform.InverseTransformPoint(eye.position) : Vector3.zero;
            var facingKnown = eye != null;

            var geometryChanged = radius != _lastRadius
                               || thickness != _lastThickness
                               || spacing != _lastSpacing
                               || arc != _lastArc;

            _lastRadius = radius;
            _lastThickness = thickness;
            _lastSpacing = spacing;
            _lastArc = arc;

            // Against the eye position the shading was last built for, not against the previous
            // frame's: a drift below the threshold must not be able to accumulate unnoticed
            // simply because it arrives a tenth of a millimetre at a time.
            var viewerMoved = facingKnown
                           && (!_haveLastViewer
                            || (viewer - _lastViewer).sqrMagnitude > ViewerStill * ViewerStill);

            if (viewerMoved) { _lastViewer = viewer; _haveLastViewer = true; }
            if (!facingKnown) _haveLastViewer = false;

            var glowChanged = glow != _lastGlow;
            var pulseChanged = pulse != _lastPulse;
            _lastGlow = glow;
            _lastPulse = pulse;

            for (var i = 0; i < Bands; i++)
            {
                var band = _bands[i];
                if (band == null) continue;

                var moveVertices = geometryChanged
                                || band.Bar.Shown != band.LastShown
                                || band.Bar.Lost != band.LastLost;

                // The pulse is on the clock, so it changes every frame — but it only reaches
                // the picture on a band that is low enough to be pulsing, which is where the
                // test belongs. Asked before the geometry is written, since writing it is what
                // brings these two up to date.
                var repaint = moveVertices
                           || viewerMoved
                           || glowChanged
                           || (pulseChanged && band.Bar.Shown < WarnBelow);

                band.LastShown = band.Bar.Shown;
                band.LastLost = band.Bar.Lost;

                Shape(band, i, radius, thickness, spacing, arc, glow, pulse, viewer, facingKnown,
                      moveVertices, repaint);
            }
        }

        /// <summary>
        /// Lays one band's three concentric rings into its vertex and colour arrays.
        ///
        /// The arc is centred on the frame's +Z — the face you read — and grows from one end,
        /// so a full band wraps the whole sweep and an empty one is nothing. A zero-length arc
        /// collapses to coincident vertices and draws nothing, which is why there is no special
        /// case for it: degenerate triangles are free and a branch here would not be.
        ///
        /// <para>
        /// The trough takes the whole of the arc and the whole of the thickness; the two rings
        /// it carries are inset from both by <see cref="TroughRim"/>, which is what leaves the
        /// trough showing as a rim round a band that is completely full. The inset is one
        /// distance rather than two — the same rim across the tube and off each end of the arc —
        /// so the end inset is that distance turned into an angle at this radius.
        /// </para>
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        private void Shape(Band band, int index, float radius, float thickness, float spacing,
                           float arc, float glow, float pulse, Vector3 viewer, bool facingKnown,
                           bool moveVertices, bool repaint)
        {
            if (band == null || (!moveVertices && !repaint)) return;

            // Negative spacing stacks them the other way, which is the whole of the fix if
            // they come out with mana at the hand rather than at the elbow.
            var along = (1 - index) * spacing;
            var v = 0;

            // The rim, and the same rim in degrees at this radius. Capped against the arc
            // because the settings can ask for a thick band on a small wrist over a short
            // sweep, and two rims that met in the middle would leave nothing to fill.
            var rim = thickness * TroughRim;
            var ends = Mathf.Min(rim * Mathf.Rad2Deg / radius, arc * 0.2f);

            for (var ring = 0; ring < Rings; ring++)
            {
                var paint = Paint(ring, band.Bar, pulse, glow);
                var alpha = RingAlpha[ring];
                var ringThickness = ring == 0 ? thickness : thickness - rim;

                var start = 0f;
                var sweep = 0f;
                var ringRadius = 0f;

                if (moveVertices)
                {
                    var fraction = ring == 0 ? 1f
                                 : ring == 1 ? band.Bar.Lost
                                 : band.Bar.Shown;

                    var span = ring == 0 ? arc : arc - 2f * ends;

                    start = -span * 0.5f;
                    sweep = span * Mathf.Clamp01(fraction);
                    ringRadius = radius + thickness * RingStep * ring;
                }

                for (var i = 0; i <= MajorSegments; i++)
                {
                    var outward = Vector3.zero;

                    if (moveVertices)
                    {
                        var angle = (start + sweep * i / MajorSegments) * Mathf.Deg2Rad;
                        outward = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
                    }

                    for (var j = 0; j <= MinorSegments; j++)
                    {
                        Vector3 position, normal;

                        if (moveVertices)
                        {
                            var cos = MinorCos[j];
                            var sin = MinorSin[j];

                            position = outward * (ringRadius + ringThickness * cos)
                                     + Vector3.up * (along + ringThickness * sin);
                            normal = outward * cos + Vector3.up * sin;

                            band.Vertices[v] = position;
                            band.Points[v] = position;
                            band.Normals[v] = normal;
                        }
                        else
                        {
                            // Unchanged since they were last written, and read from this side
                            // of the boundary rather than out of the il2cpp buffer.
                            position = band.Points[v];
                            normal = band.Normals[v];
                        }

                        // The roundness, and the only place it exists. Brightest along the
                        // crest and falling away to the sides, which is what a lit tube does
                        // and what a flat strip of one colour never can. A property of the
                        // tube's cross-section alone, so it is tabled with it.
                        var lit = MinorLit[j];

                        // Back-face culling, done here because Cull Off is written into the
                        // shader and cannot be asked to stop. Without it the far side of the
                        // ring draws over the near side purely because it comes later in the
                        // index buffer, and no amount of sorting between objects can help with
                        // something inside one of them. The cut is tight on purpose: only
                        // surface within a few degrees of edge-on fades, so the band keeps its
                        // full width right up to its own silhouette.
                        var seen = 1f;
                        if (facingKnown)
                        {
                            var toEye = viewer - position;
                            var facing = Vector3.Dot(normal, toEye.normalized);
                            seen = Mathf.Clamp01(facing / 0.12f);
                        }

                        band.Colours[v] = new Color(paint.r * lit, paint.g * lit, paint.b * lit,
                                                    alpha * seen);
                        v++;
                    }
                }
            }

            // The costly pair, and the reason the frame above is split at all: the vertex
            // setter revalidates and re-uploads the whole stream, and the bounds can only be
            // recomputed after it. Neither is owed on a frame that only changed a colour.
            if (moveVertices)
            {
                band.Mesh.vertices = band.Vertices;
                band.Mesh.RecalculateBounds();
            }

            band.Mesh.colors = band.Colours;
        }

        private static Color Paint(int ring, Bar bar, float pulse, float glow)
        {
            if (ring == 0) return TroughPaint;
            if (ring == 1) return Ghost(bar.Colour);

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

            _material = new Material(shader)
            {
                // White with the fade in its alpha; the bands' own colours are on the vertices,
                // and UI/Default multiplies the two — which is what lets one material serve all
                // three renderers, and keeps them in one sorting group while it does.
                color = new Color(1f, 1f, 1f, 0f),
                mainTexture = Texture2D.whiteTexture,
                renderQueue = 3000,
            };

            // Depth tested, and said so out loud.
            //
            // `UI/Default` declares `ZTest [unity_GUIZTestMode]` so that a canvas can choose,
            // which means a material that never chooses takes whatever that global happens to
            // hold — and outside the canvas system it holds nothing useful, so the bands drew
            // over the world including the hand they are worn on. What it does *not* buy is
            // depth between the bands themselves: `ZWrite Off` is written into the shader, so
            // nothing here writes depth for anything else to test against. See the class
            // remarks for what stands in for it.
            _material.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            _material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);

            // Mana blue, stamina amber, health red down the arm. Health is at the end of the run
            // rather than the start of it, which puts the one you cannot afford to miss nearest
            // your hand — and cool to warm reads as an ascending scale of how much it matters.
            _bands[0] = MakeBand("Mana", new Bar
            {
                Colour = Mana, Warned = Warn(Mana), Read = d => Fraction(d.GetMP(), d.GetMPMax()),
            });
            _bands[1] = MakeBand("Stamina", new Bar
            {
                Colour = Stamina, Warned = Warn(Stamina), Read = d => Fraction(d.GetSP(), d.GetSPMax()),
            });
            _bands[2] = MakeBand("Health", new Bar
            {
                Colour = Health, Warned = Warn(Health), Read = d => Fraction(d.GetHP(), d.GetHPMax()),
            });

            _root.SetActive(false);
            return true;
        }

        /// <summary>
        /// One band on its own renderer.
        ///
        /// That is the point of the split rather than an accident of it. Unity sorts transparent
        /// renderers back to front by their bounds centre, which is the only depth resolution
        /// available with a shader that cannot write depth — and it needs the centres to differ.
        /// One mesh per *layer*, which is what this was, gave three renderers sitting on top of
        /// one another with identical centres, so the sort had nothing to work with and the
        /// bands drew in whatever order they were created in.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        private Band MakeBand(string name, Bar bar)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            go.layer = 0;   // seen by the game's cameras, not by the HUD capture camera

            var band = new Band
            {
                Bar = bar,
                Mesh = new Mesh { name = $"NobetaVR Wrist {name}" },
                Vertices = new Il2CppStructArray<Vector3>(Rings * RingVertices),
                Colours = new Il2CppStructArray<Color>(Rings * RingVertices),
                Points = new Vector3[Rings * RingVertices],
                Normals = new Vector3[Rings * RingVertices],
            };

            // Rewritten every frame, so Unity is told not to treat the buffer as static.
            band.Mesh.MarkDynamic();
            band.Mesh.vertices = band.Vertices;
            band.Mesh.triangles = Topology();

            // sharedMesh, not mesh: the `mesh` accessor is the one that quietly clones, and a
            // clone would leave every per-frame rebuild being written to a mesh nothing draws.
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = band.Mesh;

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            return band;
        }

        /// <summary>
        /// The triangle list for one band's three rings, which never changes: only the vertices
        /// move. The rings are laid out trough, ghost, lit, so index order alone puts the lit
        /// one last where they overlap.
        /// </summary>
        private static Il2CppStructArray<int> Topology()
        {
            var indices = new Il2CppStructArray<int>(Rings * MajorSegments * MinorSegments * 6);
            var t = 0;

            for (var ring = 0; ring < Rings; ring++)
            {
                var origin = ring * RingVertices;

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
