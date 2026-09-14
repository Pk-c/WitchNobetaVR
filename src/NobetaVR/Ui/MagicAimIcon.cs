using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace NobetaVR.Ui
{
    /// <summary>
    /// Draws the game's own aim icon in the world, wherever <see cref="AimReticle"/> put the mark.
    ///
    /// The game has four of these — the metadata names them <c>Aim_Null</c>, <c>Aim_Fire</c>,
    /// <c>Aim_Ice</c> and <c>Aim_Lightning</c> — and swaps between them in
    /// <c>UIAimingPoint.UpdateMagicAimIcon(Magic)</c> as the spell changes. That is a piece of the
    /// game telling the player which magic is loaded, drawn at the one place they are already
    /// looking, and the mod was throwing it away: the sprite lives on a flat panel at a fixed
    /// distance, so the crosshair itself cannot be kept, and hiding it took the spell with it.
    /// Nothing says the picture has to go with the surface. This takes the sprite off the
    /// crosshair and puts it in the world, on the aim point, at the depth the shot will land.
    ///
    /// <para>
    /// Read live off <c>aimImg</c> rather than matched against the current spell here. Whatever
    /// <c>UpdateMagicAimIcon</c> decides is on that <c>Image</c> the moment it decides it, so
    /// following the sprite is both shorter and more correct than reproducing the rule that
    /// chose it — and it goes on working with the crosshair hidden, because disabling an
    /// <c>Image</c> stops it drawing and not the component that writes to it.
    /// </para>
    ///
    /// <para>
    /// The mesh is built from the sprite rather than from a quad with the sprite's UVs on it.
    /// These are atlas sprites, and a sprite in an atlas may be tightly packed and may be stored
    /// rotated; <c>Sprite.vertices</c>, <c>uv</c> and <c>triangles</c> are the mesh Unity itself
    /// draws it with, so taking them settles packing, rotation and trim at once and cannot
    /// disagree with what the flat crosshair looked like.
    /// </para>
    /// </summary>
    internal sealed class MagicAimIcon
    {
        /// <summary>
        /// How solid the icon is, for the same reason the three marks are not solid either: this
        /// hangs over the thing being aimed at. Higher than theirs, because the game's icons are
        /// open in the middle and drawn thin, and dimming a thin line costs more than dimming a
        /// filled triangle.
        /// </summary>
        private const float Fill = 0.9f;

        private readonly GameObject _object;
        private readonly MeshFilter _filter;
        private readonly Material _material;

        private Mesh _mesh;

        /// <summary>The sprite the mesh was built from, so it is rebuilt on a change and not per frame.</summary>
        private Sprite _built;

        /// <summary>Measured on the way through <see cref="Rebuild"/>; see <see cref="Reach"/>.</summary>
        private float _reach = 0.5f;

        internal MagicAimIcon(Transform root, Shader shader)
        {
            _material = new Material(shader);

            _object = new GameObject("NobetaVR Reticle Magic Icon") { hideFlags = HideFlags.HideAndDontSave };

            var renderer = _object.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            _filter = _object.AddComponent<MeshFilter>();

            // Layer 0 and no collider, for the reasons AimReticle.Mark gives: the HUD capture
            // camera is kept off this by distance, and a collider here would be a pane of
            // invisible glass for the aim ray to find instead of the wall behind it.
            _object.layer = 0;

            var icon = _object.transform;
            icon.SetParent(root, false);
            icon.localScale = Vector3.one;

            _object.SetActive(false);
        }

        /// <summary>Centre to the furthest corner of the icon, in reticle sizes.</summary>
        internal float Reach => _reach;

        /// <summary>
        /// Shows the given sprite, in the given colour, and says whether it could be shown.
        ///
        /// A false here is not a failure to report: it is the caller's cue to draw its own marks
        /// this frame, which is what it would have drawn anyway. The sprite is absent for a frame
        /// or two after a stage loads, and the whole of the answer is to wait.
        /// </summary>
        internal bool Show(Sprite sprite, Color tint)
        {
            if (sprite == null) { Hide(); return false; }

            if (sprite != _built && !Rebuild(sprite)) { Hide(); return false; }

            // The alpha is ours, not the game's. The crosshair's own alpha is driven by
            // UIAimingPoint as the aim comes up and goes down, and in a headset the reticle is
            // wanted on the frames that fade it: the mod already decides when to draw the mark,
            // and inheriting a second opinion would only make it flicker. The colour is kept,
            // because that is the part carrying the spell.
            _material.color = new Color(tint.r, tint.g, tint.b, Fill);

            if (!_object.activeSelf) _object.SetActive(true);
            return true;
        }

        internal void Hide()
        {
            if (_object != null && _object.activeSelf) _object.SetActive(false);
        }

        /// <summary>
        /// Rebuilds the mesh from a sprite, normalised into the reticle's own square.
        ///
        /// The sprite's vertices are in its own units — the size it would be drawn at in the world
        /// at its pixels-per-unit — and are placed about its pivot, which for a crosshair is its
        /// centre but need not be. Both are taken out here: the mesh is centred on the art's own
        /// bounds and scaled so its longer side is exactly one. That makes the size setting mean
        /// the same thing for this as it does for the three marks, and lets an icon that is not
        /// square keep its proportions rather than be squashed into one.
        /// </summary>
        private bool Rebuild(Sprite sprite)
        {
            var vertices = sprite.vertices;
            var uv = sprite.uv;
            var triangles = sprite.triangles;

            if (vertices == null || uv == null || triangles == null
                || vertices.Length < 3 || uv.Length != vertices.Length || triangles.Length < 3)
            {
                Plugin.Log.LogWarning($"aim icon '{sprite.name}' has no usable mesh; keeping the built-in reticle");
                _built = null;
                return false;
            }

            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);

            for (var i = 0; i < vertices.Length; i++)
            {
                var v = vertices[i];
                min = Vector2.Min(min, v);
                max = Vector2.Max(max, v);
            }

            var extent = max - min;
            var longest = Mathf.Max(extent.x, extent.y);
            if (longest <= 0f)
            {
                Plugin.Log.LogWarning($"aim icon '{sprite.name}' is degenerate; keeping the built-in reticle");
                _built = null;
                return false;
            }

            var centre = (min + max) * 0.5f;
            var scale = 1f / longest;

            var points = new Il2CppStructArray<Vector3>(vertices.Length);
            for (var i = 0; i < vertices.Length; i++)
            {
                var v = (vertices[i] - centre) * scale;
                points[i] = new Vector3(v.x, v.y, 0f);
            }

            // Widened from the sprite's own 16-bit indices, which is the one conversion the
            // interop will not do on the way in.
            var indices = new Il2CppStructArray<int>(triangles.Length);
            for (var i = 0; i < triangles.Length; i++) indices[i] = triangles[i];

            var texcoords = new Il2CppStructArray<Vector2>(uv.Length);
            for (var i = 0; i < uv.Length; i++) texcoords[i] = uv[i];

            if (_mesh == null)
            {
                _mesh = new Mesh { name = "NobetaVR Reticle Magic Icon", hideFlags = HideFlags.HideAndDontSave };
                _filter.sharedMesh = _mesh;
            }

            // Cleared first: the new sprite may have fewer vertices than the old, and assigning
            // the shorter array over a longer mesh leaves the index buffer pointing past its end.
            _mesh.Clear();
            _mesh.vertices = points;
            _mesh.uv = texcoords;
            _mesh.triangles = indices;
            _mesh.RecalculateBounds();

            _material.mainTexture = sprite.texture;

            // Half the diagonal of what was just laid out, which is what the caller lifts the
            // reticle off a wall by. Measured rather than assumed, because a normalised icon
            // reaches one half on its longer side and less on the other.
            _reach = extent.magnitude * scale * 0.5f;

            _built = sprite;
            Plugin.Log.LogInfo($"aim icon: '{sprite.name}'");
            return true;
        }
    }
}
