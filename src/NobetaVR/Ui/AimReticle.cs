using System;
using Il2CppInterop.Runtime;
using NobetaVR.Input;
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
    ///
    /// <para>
    /// The shape is three marks around an opening — up to the left, up to the right and
    /// straight below — each pointing in at the aim point, with nothing drawn on the point
    /// itself. That is the point: a dot covers the thing you are about to shoot, at the
    /// moment you most want to see it. Three points say where the centre is without occupying
    /// it, and putting the upper two on the diagonals leaves the horizon through the aim point
    /// clear, which is the line a target crosses it on.
    /// </para>
    ///
    /// <para>
    /// The opening is not a fixed width. It is wide while the wand is loose and closes as the
    /// right grip takes focus, which is the one thing this shape can say that a dot cannot: a
    /// wand swung free kicks, and a mark drawn tight around a shot that will not land there is
    /// a lie told precisely. Both widths are settings, and setting them equal gives back a
    /// reticle that never moves.
    /// </para>
    ///
    /// <para>
    /// All of that is about the shape, and the placement above is about the position — and only
    /// the position was ever the VR problem. The game draws its own crosshair with one of four
    /// sprites, one per magic, so the shape is a thing it can already say better than this can:
    /// see <see cref="MagicAimIcon"/>, which is drawn at exactly the point worked out here and
    /// is what <c>UseGameAimIcon</c> switches between. The three marks stay as the alternative
    /// and as the fallback for any frame there is no sprite to draw.
    /// </para>
    /// </summary>
    public sealed class AimReticle : MonoBehaviour
    {
        public AimReticle(IntPtr ptr) : base(ptr) { }

        // -- the shape ------------------------------------------------------------------
        //
        // Everything here is a fraction of the reticle's own size, so the one size setting
        // still scales the whole of it and the proportions survive being made bigger.

        /// <summary>How big each of the three marks is.</summary>
        private const float MarkSize = 0.26f;

        /// <summary>
        /// Where the three marks stand, in degrees anticlockwise from the player's right —
        /// which is also how far each is turned, because a mark points inwards wherever it is
        /// put, so one angle is both its place on the ring and its own roll.
        /// </summary>
        private static readonly float[] Angles = { 45f, 135f, 270f };

        // The triangle inside its texture, in the texture's own [-1, 1] space: apex towards
        // -X, base at +X, so an unrotated mark points left. Named out here rather than left
        // inside the drawing, because the placement needs the apex too — the gap is measured
        // to the point of each mark, which is the part the eye reads, and not to the middle of
        // a quad that is mostly empty.
        private const float ApexX = -0.72f;
        private const float BaseX = 0.72f;
        private const float BaseHalfHeight = 0.66f;

        /// <summary>Apex to quad centre, in quad widths: the texture spans [-1, 1] over one.</summary>
        private const float ApexOffset = -ApexX * 0.5f;

        /// <summary>How quickly the opening follows the focus, per second.</summary>
        private const float GapSpeed = 16f;

        /// <summary>
        /// How solid the marks are. Transparent on purpose: this hangs over the thing you are
        /// aiming at, and a solid mark in front of an enemy's tell is a sight that costs you
        /// the fight it was drawn to win.
        /// </summary>
        private const float Fill = 0.75f;

        private Transform _root;
        private readonly Transform[] _marks = new Transform[Angles.Length];

        /// <summary>Centre to mark, one unit vector per angle, worked out once.</summary>
        private readonly Vector3[] _outward = new Vector3[Angles.Length];
        private Material _material;
        private Texture2D _texture;
        private bool _failed;

        /// <summary>The opening as it is being drawn, and whether it has been set at all yet.</summary>
        private float _gap;
        private bool _gapSettled;

        private UIAimingPoint _gameCrosshair;
        private float _nextScan;
        private bool _gameCrosshairHidden;

        /// <summary>The game's own aim sprite, drawn at the same point when it is asked for.</summary>
        private MagicAimIcon _icon;

        internal static AimReticle Instance { get; private set; }

        private void Awake() => Instance = this;

        /// <summary>The frame the view last placed the mark on; see <see cref="FollowView"/>.</summary>
        private int _placedFrame = -1;

        private void LateUpdate()
        {
            GameCrosshair();

            // A frame's grace, and then stand down. The mark is placed from the view now, so
            // if the view stops being placed at all -- XR down, a scene with no camera to
            // drive -- there is nothing left to move it, and a mark left hanging on a wall is
            // the one thing the player cannot dismiss. A frame rather than none, because this
            // runs before the view does within the same frame.
            if (_placedFrame < Time.frameCount - 1) Hide();
        }

        /// <summary>
        /// Marks the aim point, driven from the view at the moment the view is final.
        ///
        /// Not from <c>LateUpdate</c>, where this used to sit. The head pose is written inside
        /// the game's own LateUpdate, after every one of the mod's, so the camera position
        /// read there was a frame old: the mark is placed along the line from the eye to the
        /// target, and an eye one frame behind swings that line by as much as the head just
        /// moved.
        /// </summary>
        internal static void FollowView()
        {
            var self = Instance;
            if (self == null) return;

            self.Follow();
        }

        private void Follow()
        {
            // Marked as placed whatever comes of it: the grace period in LateUpdate is
            // watching for the view going away, not for the reticle deciding to hide itself.
            _placedFrame = Time.frameCount;

            if (_failed || !Plugin.Instance.ShowAimReticle.Value) { Hide(); return; }

            // Only while she is the player's to aim. The same gate the hands use: in a
            // cutscene or a menu the aim target still exists and still has a position, and a
            // mark left hanging on a wall through a conversation is exactly the kind of thing a
            // mod leaves behind by never asking.
            if (!VrHands.PlayerInControl) { Hide(); return; }

            var target = VrAim.Target;
            var camera = VrCamera.CameraTransform;
            if (target == null || camera == null) { Hide(); return; }

            if (_root == null && !Build()) return;

            var toTarget = target.Value - camera.position;
            var distance = toTarget.magnitude;
            if (distance < 0.05f) { Hide(); return; }

            var direction = toTarget / distance;

            // Constant angular size rather than constant world size: a reticle that shrinks
            // with distance disappears exactly when a shot needs it most, and one that does
            // not swells into a dinner plate against a near wall.
            var size = distance * Plugin.Instance.AimReticleSize.Value;

            var reach = Shape();

            // Lifted off the surface towards the eye. The aim point is *on* the wall it found,
            // and a quad coplanar with a wall is a coin toss between the two every frame,
            // which reads as the reticle flickering rather than as anything to do with depth.
            // Scaled with distance so the lift stays small next to what it is marking, and
            // with the reticle's own reach on top of that: the marks stand off the centre now,
            // so on a wall taken at an angle they are the parts that go through it first.
            var lift = Mathf.Min(0.05f, distance * 0.04f) + reach * size * 0.5f;

            _root.position = target.Value - direction * lift;
            _root.rotation = Quaternion.LookRotation(direction, camera.up);
            _root.localScale = new Vector3(size, size, 1f);

            if (!_root.gameObject.activeSelf) _root.gameObject.SetActive(true);
        }

        /// <summary>
        /// Draws one of the two shapes at the point <see cref="Follow"/> has already settled on,
        /// and answers with how far that shape reaches from it, in reticle sizes.
        ///
        /// The choice is made per frame rather than once, because the sprite is not always
        /// there to take: the aiming UI is rebuilt per stage, and for a frame or two after a
        /// loading screen it exists without one. Falling back to the three marks on those frames
        /// costs nothing — they are what would have been drawn anyway — and it means the setting
        /// can be changed in the VR menu and take effect on the next frame without anything
        /// being rebuilt.
        ///
        /// Whichever is drawn is drawn in the same place, which is the point of doing it here:
        /// the mark does not move when the shape changes.
        /// </summary>
        private float Shape()
        {
            var sprite = MagicSprite(out var tint);

            if (sprite != null && _icon.Show(sprite, tint))
            {
                Marks(false);

                // The gap is left unsettled while the icon has the frame, so that the marks snap
                // to their width rather than easing out of a stale one if they come back.
                _gapSettled = false;
                return _icon.Reach;
            }

            _icon.Hide();
            Marks(true);

            Gap();
            Place();
            return Reach();
        }

        private void Marks(bool show)
        {
            for (var i = 0; i < _marks.Length; i++)
            {
                var mark = _marks[i].gameObject;
                if (mark.activeSelf != show) mark.SetActive(show);
            }
        }

        /// <summary>
        /// The sprite the game is currently drawing its crosshair with, and the colour it is
        /// drawing it in — which between them are the magic, since <c>UpdateMagicAimIcon</c>
        /// writes the first and the <c>COLOR_MAGIC_*</c> constants the second.
        ///
        /// Taken off the <c>Image</c> whether or not that <c>Image</c> is switched on. Hiding the
        /// centred crosshair clears <c>enabled</c>, which stops it being drawn and leaves every
        /// field on it exactly as the game last wrote it; the two settings are independent, and
        /// the useful pair is both — the flat one off, its picture in the world.
        /// </summary>
        private Sprite MagicSprite(out Color tint)
        {
            tint = Color.white;
            if (!Plugin.Instance.UseGameAimIcon.Value) return null;

            var aiming = AimingPoint();
            var image = aiming != null ? aiming.aimImg : null;
            if (image == null) return null;

            tint = image.color;
            return image.sprite;
        }

        /// <summary>
        /// Opens or closes the gap towards whichever of the two widths the right grip asks for.
        ///
        /// Eased rather than switched, because the switch is what it means and the ease is how
        /// it reads: a gap that jumps between two widths is two reticles seen in turn, while
        /// one that closes over a fifth of a second is a single reticle settling — which is
        /// exactly what the hand it is drawn from is doing. On unscaled time, so it neither
        /// freezes nor races when the game takes the clock.
        /// </summary>
        private void Gap()
        {
            var wanted = VrControls.Focusing
                ? Plugin.Instance.AimReticleFocusGap.Value
                : Plugin.Instance.AimReticleGap.Value;

            // Snapped the first time, and after every spell of being hidden. Easing out of a
            // width left over from before a cutscene is an animation of nothing, played at the
            // moment the player is hunting for the mark again.
            if (!_gapSettled) { _gap = wanted; _gapSettled = true; return; }

            _gap = Mathf.Lerp(_gap, wanted, 1f - Mathf.Exp(-GapSpeed * Time.unscaledDeltaTime));
        }

        /// <summary>Centre to the outer corner of a mark, in reticle sizes.</summary>
        private float Reach() => _gap + (BaseX - ApexX) * 0.5f * MarkSize;

        /// <summary>
        /// Puts the three marks around the opening, at the width <see cref="Gap"/> arrived at.
        ///
        /// In the root's own space, which the root has already turned to face the eye: +X is
        /// the player's right and +Y is up, so <see cref="Angles"/> reads as it looks. The
        /// offset is to each quad's centre, hence the apex term — the setting is the distance
        /// to the points, because the points are what the eye measures the gap by.
        /// </summary>
        private void Place()
        {
            var offset = _gap + ApexOffset * MarkSize;

            for (var i = 0; i < _marks.Length; i++) _marks[i].localPosition = _outward[i] * offset;
        }

        private void Hide()
        {
            if (_root != null && _root.gameObject.activeSelf) _root.gameObject.SetActive(false);
            _gapSettled = false;
        }

        /// <summary>
        /// The game's aiming UI, found on a timer rather than held.
        ///
        /// The timer is because this UI is rebuilt per stage, so a reference taken once is a dead
        /// pointer from the next loading screen onwards. It sits here rather than inside either
        /// caller because there are two of them now and they want it for unrelated reasons — one
        /// to switch the crosshair off, one to borrow its sprite — and neither should pay for a
        /// scan the other asked for.
        /// </summary>
        private UIAimingPoint AimingPoint()
        {
            if (_gameCrosshair != null) return _gameCrosshair;
            if (Time.unscaledTime < _nextScan) return null;

            _nextScan = Time.unscaledTime + 1f;

            var found = UnityEngine.Object.FindObjectOfType(Il2CppType.Of<UIAimingPoint>());
            _gameCrosshair = found != null ? found.TryCast<UIAimingPoint>() : null;

            return _gameCrosshair;
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

            if (AimingPoint() == null) return;

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

        /// <summary>
        /// One texture, three quads, and a parent to aim all three at once.
        ///
        /// Three quads rather than one, because the opening moves. A single baked reticle would
        /// have to be redrawn on every frame the gap changed, which is a texture upload per
        /// frame to say something a transform already says; keeping the marks apart makes the
        /// whole of the animation three local positions, and the mark itself stays the one
        /// texture it was.
        /// </summary>
        private bool Build()
        {
            var shader = TransparentShader.Find();
            if (shader == null) { _failed = true; return false; }

            _texture = Draw();
            _material = new Material(shader) { mainTexture = _texture };

            var root = new GameObject("NobetaVR Reticle");
            UnityEngine.Object.DontDestroyOnLoad(root);
            root.hideFlags = HideFlags.HideAndDontSave;
            _root = root.transform;

            // The unrotated mark sits to the right and points left, so one angle turns it to
            // its place on the ring and leaves it pointing in, both at once.
            for (var i = 0; i < Angles.Length; i++) Mark(i, Angles[i]);

            // Built alongside the marks rather than on the first frame it is wanted. It is one
            // object with an empty mesh until a sprite arrives, and building it here keeps the
            // setting a per-frame choice between two shapes that both already exist.
            _icon = new MagicAimIcon(_root, shader);

            root.SetActive(false);

            return true;
        }

        private void Mark(int index, float angle)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "NobetaVR Reticle Mark";
            quad.hideFlags = HideFlags.HideAndDontSave;

            // A collider here would be a pane of invisible glass hanging wherever you point,
            // and the aim raycast would then find it rather than the wall behind it.
            var collider = quad.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);

            var renderer = quad.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            // Layer 0, with the rest of what this mod draws in the world. The HUD capture
            // camera renders that layer too and is kept off this by distance instead; see
            // HudPanel.Park.
            quad.layer = 0;

            var mark = quad.transform;
            mark.SetParent(_root, false);
            mark.localRotation = Quaternion.Euler(0f, 0f, angle);
            mark.localScale = new Vector3(MarkSize, MarkSize, 1f);

            var radians = angle * Mathf.Deg2Rad;
            _outward[index] = new Vector3(Mathf.Cos(radians), Mathf.Sin(radians), 0f);
            _marks[index] = mark;
        }

        /// <summary>
        /// Draws one mark into a texture rather than shipping one.
        ///
        /// A triangle pointing towards -X, white inside a dark rim. The rim is the cheapest way
        /// to stay readable over anything — a white mark disappears on snow and a black one in
        /// a crypt, and this game has both.
        /// </summary>
        private static Texture2D Draw()
        {
            const int size = 128;
            const float rim = 0.035f;      // thickness of the dark rim, where 1 is half the texture

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "NobetaVR Reticle",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            // The outward normal of the upper slanted edge. The lower edge is its mirror, which
            // is why the distance below is taken to |dy| rather than to dy.
            var edgeX = BaseX - ApexX;
            var length = Mathf.Sqrt(edgeX * edgeX + BaseHalfHeight * BaseHalfHeight);
            var normalX = -BaseHalfHeight / length;
            var normalY = edgeX / length;

            var edge = 2f / size;          // a texel or so, so the edges do not step
            var clear = new Color(0f, 0f, 0f, 0f);

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = (x + 0.5f) / size * 2f - 1f;
                    var dy = (y + 0.5f) / size * 2f - 1f;

                    // How far outside the triangle a pixel is; negative inside it. The greater
                    // of the two half-plane distances, which for a convex shape is the distance
                    // to it, and which mitres the corners rather than rounding them off.
                    var slant = normalX * (dx - ApexX) + normalY * Mathf.Abs(dy);
                    var outside = Mathf.Max(slant, dx - BaseX);

                    var core = Mathf.Clamp01(1f - outside / edge);
                    var halo = Mathf.Clamp01(1f - (outside - edge) / rim);

                    var alpha = Mathf.Max(core, halo * 0.85f) * Fill;
                    if (alpha <= 0.001f) { texture.SetPixel(x, y, clear); continue; }

                    // White in the triangle, black in the rim around it.
                    texture.SetPixel(x, y, new Color(core, core, core, alpha));
                }
            }

            texture.Apply();
            return texture;
        }
    }
}
