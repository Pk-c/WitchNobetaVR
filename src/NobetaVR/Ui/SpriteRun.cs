using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace NobetaVR.Ui
{
    /// <summary>
    /// Lays a run of atlas sprites out side by side into one mesh, normalised to unit height.
    ///
    /// The game writes its damage numbers as a row of digit sprites on a canvas, and a canvas
    /// is the one place they cannot go in a headset — see <see cref="DamageNumbers"/> for why.
    /// What survives the move is the art: these are the game's own digits, in the game's own
    /// colours, and nothing here redraws them.
    ///
    /// <para>
    /// The mesh comes from <c>Sprite.vertices</c>, <c>uv</c> and <c>triangles</c> rather than
    /// from a quad with the sprite's UVs on it, for the reason <see cref="MagicAimIcon"/> gives:
    /// an atlas sprite may be tightly packed and may be stored rotated, and those three arrays
    /// are what Unity itself draws it with, so taking them settles packing, rotation and trim
    /// at once.
    /// </para>
    ///
    /// <para>
    /// Spacing is taken from each sprite's <c>rect</c> rather than from its mesh bounds. The
    /// rect is the cell the artist drew in; the bounds are whatever survived tight packing, so
    /// a '1' would be spaced as though it were as narrow as its own ink and the row would come
    /// out unevenly kerned. Everything is then scaled so the tallest cell is exactly one unit
    /// high, which is what lets the caller's one size setting mean the same thing for a
    /// one-digit number as for a four-digit one.
    /// </para>
    /// </summary>
    internal static class SpriteRun
    {
        /// <summary>
        /// Fills <paramref name="mesh"/> with the first <paramref name="count"/> sprites, centred
        /// on the origin, and reports how wide the result came out in unit heights.
        ///
        /// False means the sprites were not usable and the mesh was left alone — one of them was
        /// missing, or had no mesh of its own. The caller's answer to that is to draw nothing this
        /// frame rather than to draw something wrong: the sprite set is absent for a frame or two
        /// after a stage loads, and the whole of the answer is to wait.
        /// </summary>
        internal static bool Build(Mesh mesh, Sprite[] sprites, int count, out float width, out Texture texture)
        {
            width = 0f;
            texture = null;

            if (mesh == null || sprites == null || count <= 0 || count > sprites.Length) return false;

            // Two passes over the sprites before anything is written, so a run with one bad
            // sprite in it leaves the mesh holding the last good one rather than half of this.
            var height = 0f;
            var vertexCount = 0;
            var indexCount = 0;

            for (var i = 0; i < count; i++)
            {
                var sprite = sprites[i];
                if (sprite == null) return false;

                var ppu = sprite.pixelsPerUnit;
                if (ppu <= 0f) return false;

                var vertices = sprite.vertices;
                var uv = sprite.uv;
                var triangles = sprite.triangles;

                if (vertices == null || uv == null || triangles == null) return false;
                if (vertices.Length < 3 || uv.Length != vertices.Length || triangles.Length < 3) return false;

                height = Mathf.Max(height, sprite.rect.height / ppu);
                width += sprite.rect.width / ppu;

                vertexCount += vertices.Length;
                indexCount += triangles.Length;
            }

            if (height <= 0f || width <= 0f) return false;

            var scale = 1f / height;

            var points = new Il2CppStructArray<Vector3>(vertexCount);
            var texcoords = new Il2CppStructArray<Vector2>(vertexCount);

            // Widened from the sprites' own 16-bit indices, which is the one conversion the
            // interop will not do on the way in.
            var indices = new Il2CppStructArray<int>(indexCount);

            var pen = -width * 0.5f;
            var vertexAt = 0;
            var indexAt = 0;

            for (var i = 0; i < count; i++)
            {
                var sprite = sprites[i];
                var ppu = sprite.pixelsPerUnit;
                var advance = sprite.rect.width / ppu;

                // A sprite's vertices are placed about its pivot, which need not be its centre.
                // This is the vector from that pivot to the centre of the cell, so adding it puts
                // the cell -- not the ink -- where the pen is.
                var fromPivot = new Vector2(
                    (sprite.rect.width * 0.5f - sprite.pivot.x) / ppu,
                    (sprite.rect.height * 0.5f - sprite.pivot.y) / ppu);

                var offset = new Vector2(pen + advance * 0.5f, 0f) - fromPivot;

                var vertices = sprite.vertices;
                var uv = sprite.uv;
                var triangles = sprite.triangles;

                for (var v = 0; v < vertices.Length; v++)
                {
                    var p = (vertices[v] + offset) * scale;
                    points[vertexAt + v] = new Vector3(p.x, p.y, 0f);
                    texcoords[vertexAt + v] = uv[v];
                }

                for (var t = 0; t < triangles.Length; t++) indices[indexAt + t] = vertexAt + triangles[t];

                vertexAt += vertices.Length;
                indexAt += triangles.Length;
                pen += advance;
            }

            // Cleared first: a shorter run than the last one would otherwise leave the index
            // buffer pointing past the end of the new vertex array.
            mesh.Clear();
            mesh.vertices = points;
            mesh.uv = texcoords;
            mesh.triangles = indices;
            mesh.RecalculateBounds();

            width *= scale;
            texture = sprites[0].texture;
            return true;
        }
    }
}
