using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using NobetaVR.Vr;
using UnityEngine;

namespace NobetaVR.Ui
{
    /// <summary>
    /// Puts each enemy's health bar over the enemy, in the world.
    ///
    /// The game's own bars have the same problem the damage numbers do, and it comes from the
    /// same place: <c>UIEnemyHPUpdater</c> takes the enemy's <c>HPPosition</c> transform, projects
    /// it into screen space, and moves a pooled <c>EnemyHpHandler</c> on the stage canvas to the
    /// answer. That canvas is captured onto <see cref="HudPanel"/> in a headset -- a flat quad in
    /// front of your face covering rather less of your view than your view does -- so a screen
    /// coordinate taken through a stereo camera lands nowhere useful on it. The bars were being
    /// updated the whole time, off the edge of the panel.
    ///
    /// <para>
    /// So the bar goes where the enemy is. It is a small billboard at the same <c>HPPosition</c>
    /// the game was projecting, at the enemy's own depth, sized angularly so it reads the same
    /// across a room as it does at arm's length. Nothing about the game's own bookkeeping is
    /// touched: the pooled handler goes on being updated and is simply faded out, which is what
    /// makes this reversible from the VR menu with no stage reload.
    /// </para>
    ///
    /// <para>
    /// The boss bar is left alone. It is not anchored to anything in the world -- it is a banner
    /// across the top of the screen with a name on it -- so the panel is exactly where it belongs
    /// and it has been arriving correctly all along.
    /// </para>
    ///
    /// <para>
    /// Drawn rather than borrowed, unlike the damage numbers. A bar is two rectangles and a
    /// fraction; there is no art in the game's version worth the trouble of lifting an atlas
    /// sprite and a fill-amount out of a pooled <c>Image</c>, and drawing it means the trailing
    /// segment below can exist at all.
    /// </para>
    /// </summary>
    public sealed class EnemyHealthBars : MonoBehaviour
    {
        public EnemyHealthBars(IntPtr ptr) : base(ptr) { }

        internal static EnemyHealthBars Instance { get; private set; }

        /// <summary>
        /// How many bars can be on screen at once. Every enemy the stage has registered is
        /// tracked; this bounds only how many of them can be *drawn*, which is a different and
        /// much smaller number -- a bar is only up for a few seconds after the enemy was hit.
        /// </summary>
        private const int Pool = 16;

        /// <summary>Vertices and indices in one bar: three stacked quads.</summary>
        private const int Quads = 3;
        private const int Vertices = Quads * 4;
        private const int Indices = Quads * 6;

        /// <summary>The trough the bar is drawn in.</summary>
        private static readonly Color Trough = new(0.04f, 0.03f, 0.05f, 0.72f);

        /// <summary>The pale segment left behind by damage, which drains a moment later.</summary>
        private static readonly Color Trail = new(0.97f, 0.86f, 0.55f, 0.9f);

        /// <summary>The health itself.</summary>
        private static readonly Color Health = new(0.86f, 0.17f, 0.21f, 0.95f);

        /// <summary>How fast the trailing segment catches the health up, in bar widths a second.</summary>
        private const float TrailSpeed = 0.9f;

        /// <summary>How long the bar takes to arrive, and to leave, in seconds.</summary>
        private const float FadeIn = 0.12f;
        private const float FadeOut = 0.35f;

        private readonly List<Entry> _entries = new();
        private readonly Bar[] _bars = new Bar[Pool];

        private Transform _root;
        private Material _material;
        private bool _failed;
        private bool _crowded;
        private string _scene;

        private void Awake() => Instance = this;

        // -- what the game tells us ------------------------------------------------------

        /// <summary>
        /// A new enemy has a bar. Taken from <c>UIEnemyHp.AddEnemyHPBar</c>, which is the game's
        /// own registration call, so the set tracked here is exactly the set it tracks -- there is
        /// no scene to sweep and no spawn to miss between sweeps.
        /// </summary>
        internal static void Add(EnemiesManager.EnemyData data)
        {
            var self = Instance;
            if (self == null || self._failed || data == null) return;

            self.Track(data);
        }

        [HideFromIl2Cpp]
        private void Track(EnemiesManager.EnemyData data)
        {
            var chara = data.CharData;
            if (chara == null) return;

            // The game may register the same enemy again -- a boss changing phase, an enemy
            // revived by the stage -- and two entries would be two bars in the same place.
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Data.CharData == chara) { _entries[i].Data = data; return; }
            }

            _entries.Add(new Entry
            {
                Data = data,
                Health = chara.GetHPPercent(),
                Trail = chara.GetHPPercent(),
            });
        }

        // -- per frame -------------------------------------------------------------------

        private void LateUpdate()
        {
            if (_failed) return;

            // The stage's enemies go with the stage. Held entries would be dead pointers, and a
            // registration list that only ever grew would be a leak for the length of a session.
            var scene = ActiveScene.Name;
            if (scene != _scene)
            {
                _scene = scene;
                _entries.Clear();
                Park(0);
            }

            var on = Plugin.Instance.WorldEnemyHealthBars.Value;
            var camera = VrCamera.CameraTransform;

            if (!on || camera == null || _entries.Count == 0)
            {
                Park(0);
                return;
            }

            if (_root == null && !Build()) return;

            var used = 0;

            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];

                if (!Alive(entry)) { _entries.RemoveAt(i); continue; }

                Advance(entry);

                if (entry.Fade <= 0.001f) continue;
                if (used >= _bars.Length) { Crowded(); continue; }

                _bars[used++].Draw(entry, camera);
            }

            Park(used);
        }

        /// <summary>
        /// Whether an entry is still worth keeping. An enemy whose anchor has been destroyed is
        /// gone; one whose health has run out is kept only long enough for its bar to leave, so
        /// that the last hit is the one you see land rather than the one that made the bar vanish.
        /// </summary>
        [HideFromIl2Cpp]
        private static bool Alive(Entry entry)
        {
            var data = entry.Data;
            if (data == null) return false;

            var chara = data.CharData;
            if (chara == null || data.HPPosition == null) return false;

            return chara.GetHP() > 0f || entry.Fade > 0.001f;
        }

        /// <summary>
        /// Moves one entry's health, trail and fade on by a frame.
        ///
        /// The rule for when a bar is up is the game's own: it comes up when the enemy is hit and
        /// goes away a few seconds later. Reproduced rather than read, because the game keeps it
        /// on a pooled <c>UIEnemyHPUpdater</c> that is not reachable from outside the pool -- and
        /// a health value that changed is the same cue the game itself uses.
        /// </summary>
        [HideFromIl2Cpp]
        private static void Advance(Entry entry)
        {
            var chara = entry.Data.CharData;
            var health = Mathf.Clamp01(chara.GetHPPercent());

            if (!Mathf.Approximately(health, entry.Health))
            {
                entry.Health = health;
                entry.Until = Time.unscaledTime + Mathf.Max(0.5f, Plugin.Instance.EnemyHealthBarHold.Value);

                // Healing does not deserve a pale segment behind it: the trail is the width the
                // bar just lost, so a bar that gained snaps its trail forward instead.
                if (health > entry.Trail) entry.Trail = health;
            }

            entry.Trail = Mathf.MoveTowards(entry.Trail, health, TrailSpeed * Time.unscaledDeltaTime);

            // Dead enemies take their bar with them at once rather than sitting out the hold:
            // the corpse is already gone, and a bar left hanging over nothing is worse than an
            // empty one that leaves early. Cutscenes take them all: an interface that belongs
            // to the fight has no business in a conversation, and ViewIsYours is the reading
            // that says which one this is.
            var wanted = chara.GetHP() > 0f
                      && Time.unscaledTime < entry.Until
                      && VrCamera.ViewIsYours
                          ? 1f : 0f;
            var speed = wanted > entry.Fade ? 1f / FadeIn : 1f / FadeOut;

            entry.Fade = Mathf.MoveTowards(entry.Fade, wanted, speed * Time.unscaledDeltaTime);
        }

        /// <summary>Puts away every bar from <paramref name="from"/> on.</summary>
        [HideFromIl2Cpp]
        private void Park(int from)
        {
            for (var i = from; i < _bars.Length; i++) _bars[i]?.Hide();
        }

        /// <summary>Said once a session: more enemies wanted a bar at one moment than there are.</summary>
        [HideFromIl2Cpp]
        private void Crowded()
        {
            if (_crowded) return;

            _crowded = true;
            Plugin.Log.LogInfo($"more than {Pool} enemy health bars were wanted at once; "
                             + "the ones past that are left undrawn this frame");
        }

        // -- construction ----------------------------------------------------------------

        [HideFromIl2Cpp]
        private bool Build()
        {
            var shader = TransparentShader.Find();
            if (shader == null)
            {
                Plugin.Log.LogError("No alpha-blended shader survived stripping, so the enemy "
                                  + "health bars would be opaque slabs. Leaving them off.");
                _failed = true;
                return false;
            }

            var host = new GameObject("NobetaVR Enemy Health Bars");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            _root = host.transform;

            // One material for all of them, which is what keeps this to one draw setup however
            // many bars are up: the colours are on the vertices and UI/Default multiplies the
            // two, so the fade can be per-bar without the material being.
            _material = new Material(shader)
            {
                color = Color.white,
                mainTexture = Texture2D.whiteTexture,
                renderQueue = 3000,
            };

            // Depth tested, unlike the damage numbers. A bar belongs to a thing you can see, and
            // one that showed through a wall would be a wallhack rather than an interface --
            // where a damage number is a report of something that already happened, drawn for a
            // second at the point it happened, and burying it in the enemy that caused it would
            // be the bug.
            _material.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            _material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);

            for (var i = 0; i < _bars.Length; i++) _bars[i] = new Bar(_root, _material, i);

            Plugin.Log.LogInfo($"enemy health bars built: {Pool} bars, shader '{shader.name}'");
            return true;
        }

        // -- one enemy -------------------------------------------------------------------

        /// <summary>What is known about one registered enemy, between its bars.</summary>
        private sealed class Entry
        {
            internal EnemiesManager.EnemyData Data;

            /// <summary>Health as a fraction, as last seen; a change is what raises the bar.</summary>
            internal float Health;

            /// <summary>The pale segment, chasing <see cref="Health"/> down.</summary>
            internal float Trail;

            /// <summary>Unscaled time the bar stays up until.</summary>
            internal float Until;

            /// <summary>How far in or out of the fade it is, 0 to 1.</summary>
            internal float Fade;
        }

        // -- one bar ---------------------------------------------------------------------

        /// <summary>
        /// One drawn bar, lent to whichever entry needs it this frame.
        ///
        /// Three quads in one mesh -- trough, trail, health -- rather than three renderers. Within
        /// a mesh the triangle order decides what covers what, which is the whole of the depth
        /// sorting a bar needs and the only kind available from a shader that cannot write depth.
        /// <see cref="WristGauges"/> needed one renderer per band for exactly the opposite reason:
        /// its rings are separated in space, so Unity's own back-to-front sort had something to
        /// work with. These three are coplanar.
        /// </summary>
        private sealed class Bar
        {
            private readonly GameObject _object;
            private readonly Transform _transform;
            private readonly Mesh _mesh;
            private readonly Il2CppStructArray<Vector3> _points = new(Vertices);
            private readonly Il2CppStructArray<Color> _colours = new(Vertices);

            internal Bar(Transform parent, Material material, int index)
            {
                _object = new GameObject($"Bar {index}") { hideFlags = HideFlags.HideAndDontSave };
                _transform = _object.transform;
                _transform.SetParent(parent, false);
                _object.layer = 0;

                _mesh = new Mesh { name = $"NobetaVR Enemy Health Bar {index}", hideFlags = HideFlags.HideAndDontSave };
                _mesh.MarkDynamic();
                _mesh.vertices = _points;
                _mesh.triangles = Topology();

                var filter = _object.AddComponent<MeshFilter>();
                filter.sharedMesh = _mesh;

                var renderer = _object.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;

                _object.SetActive(false);
            }

            internal void Hide()
            {
                if (_object.activeSelf) _object.SetActive(false);
            }

            internal void Draw(Entry entry, Transform camera)
            {
                var cfg = Plugin.Instance;
                var anchor = entry.Data.HPPosition.position;

                var toward = anchor - camera.position;
                var distance = toward.magnitude;
                if (distance < 0.05f) { Hide(); return; }

                // Angular, like everything else the mod draws in the world, so a bar is as legible
                // across a hall as it is on something in your face.
                var width = distance * Mathf.Max(0.001f, cfg.EnemyHealthBarSize.Value);
                var height = width * Mathf.Clamp(cfg.EnemyHealthBarThickness.Value, 0.02f, 1f);

                // A quad drawn exactly on the anchor is a quad drawn inside the enemy's head. The
                // lift is in bar heights rather than metres so it scales with the bar.
                var lift = height * 2f;

                _transform.position = anchor + camera.up * lift;
                _transform.rotation = Quaternion.LookRotation(toward / distance, camera.up);
                _transform.localScale = Vector3.one;

                Shape(width, height, entry.Health, entry.Trail, entry.Fade);

                if (!_object.activeSelf) _object.SetActive(true);
            }

            /// <summary>
            /// Lays the three quads out for this frame.
            ///
            /// The border is taken out of the trough rather than added around it, so that the
            /// width setting is the width of the whole bar and a thin bar cannot end up with a
            /// border thicker than the health it is framing.
            /// </summary>
            private void Shape(float width, float height, float health, float trail, float fade)
            {
                var halfW = width * 0.5f;
                var halfH = height * 0.5f;

                var border = Mathf.Min(height * 0.22f, width * 0.04f);
                var innerLeft = -halfW + border;
                var innerRight = halfW - border;
                var innerTop = halfH - border;
                var innerBottom = -halfH + border;
                var innerWidth = innerRight - innerLeft;

                // Health first, so the trail can be clamped to sit behind it rather than the
                // other way round: a trail narrower than the health would draw a pale sliver
                // inside the red, which reads as damage that is about to be undone.
                var healthAt = innerLeft + innerWidth * Mathf.Clamp01(health);
                var trailAt = innerLeft + innerWidth * Mathf.Clamp01(Mathf.Max(trail, health));

                Quad(0, -halfW, halfW, -halfH, halfH, Trough, fade);
                Quad(1, innerLeft, trailAt, innerBottom, innerTop, Trail, fade);
                Quad(2, innerLeft, healthAt, innerBottom, innerTop, Health, fade);

                _mesh.vertices = _points;
                _mesh.colors = _colours;
                _mesh.RecalculateBounds();
            }

            private void Quad(int index, float left, float right, float bottom, float top, Color colour, float fade)
            {
                var at = index * 4;

                _points[at + 0] = new Vector3(left, bottom, 0f);
                _points[at + 1] = new Vector3(left, top, 0f);
                _points[at + 2] = new Vector3(right, top, 0f);
                _points[at + 3] = new Vector3(right, bottom, 0f);

                var faded = new Color(colour.r, colour.g, colour.b, colour.a * fade);
                for (var i = 0; i < 4; i++) _colours[at + i] = faded;
            }

            /// <summary>The triangle list, which never changes: only the corners move.</summary>
            private static Il2CppStructArray<int> Topology()
            {
                var indices = new Il2CppStructArray<int>(Indices);

                for (var q = 0; q < Quads; q++)
                {
                    var v = q * 4;
                    var t = q * 6;

                    indices[t + 0] = v + 0;
                    indices[t + 1] = v + 1;
                    indices[t + 2] = v + 2;
                    indices[t + 3] = v + 0;
                    indices[t + 4] = v + 2;
                    indices[t + 5] = v + 3;
                }

                return indices;
            }
        }
    }
}
