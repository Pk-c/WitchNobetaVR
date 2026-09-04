using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Cuts the hand out of the character's mesh, keeping its skinning.
    ///
    /// The blend is what defeated the two earlier approaches, and restating it explains why this
    /// one works. A skinned vertex is a weighted mixture of several bones. Redrawing the
    /// character's mesh with a rewritten bone array cannot isolate a hand, because every wrist
    /// vertex is part hand and part forearm: send the forearm to a collapsed point and the vertex
    /// lands partway there, so the hand renders shrunk towards its own pivot rather than wrongly.
    /// Two small scraps of geometry is exactly what that looks like.
    ///
    /// Cutting the triangles out removes the arm from the mixture instead of hiding it. What is
    /// kept is what belongs to the wrist and below; the bone weights are kept with it and
    /// renumbered onto a skeleton of just those bones, so the fingers still bend to whatever the
    /// game's animation is doing. Baking the pose flat would have been simpler and would have
    /// given a pair of permanently open hands.
    /// </summary>
    internal static class HandMesh
    {
        internal sealed class Result
        {
            public Mesh Mesh;

            /// <summary>The character's own bones this mesh is skinned to, in bone-array order.</summary>
            public Transform[] Bones;
        }

        /// <summary>
        /// Builds the hand's mesh, or returns null when this renderer holds none of it.
        ///
        /// Null is the common case rather than a fault: a character's meshes usually share one
        /// skeleton, so the hair and the cape carry the hand bone in their bone arrays while
        /// holding not one triangle of it.
        /// </summary>
        public static Result Build(SkinnedMeshRenderer source, Transform handBone, string side,
                                   float minimumWeight)
        {
            var mesh = source.sharedMesh;
            if (mesh == null) return null;

            if (!mesh.isReadable)
            {
                Plugin.Log.LogWarning($"mesh '{mesh.name}' is not readable, so the {side} hand "
                                    + "cannot be cut out of it.");
                return null;
            }

            var sourceBones = source.bones;
            if (sourceBones == null) return null;

            // The fingers count as the hand. They are skinned to their own bones — Bip001 L
            // Finger0 and its neighbours — so weighing vertices against the hand bone alone kept
            // the palm and dropped every finger.
            var owners = OwnedBones(sourceBones, handBone);

            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var uv = mesh.uv;
            var weights = mesh.boneWeights;
            var bindposes = mesh.bindposes;

            if (vertices == null || vertices.Length == 0 || weights == null || bindposes == null)
                return null;

            var belongs = new bool[vertices.Length];
            var owned = 0;
            for (var i = 0; i < vertices.Length; i++)
            {
                if (WeightOn(weights[i], owners) < minimumWeight) continue;
                belongs[i] = true;
                owned++;
            }
            if (owned == 0) return null;

            // Bones are renumbered as they are met, so the result carries a skeleton of a dozen
            // bones rather than the character's whole array.
            var boneRemap = new int[sourceBones.Length];
            for (var i = 0; i < boneRemap.Length; i++) boneRemap[i] = -1;

            var keptBones = new System.Collections.Generic.List<Transform>();
            var keptBindposes = new System.Collections.Generic.List<Matrix4x4>();

            var remap = new int[vertices.Length];
            for (var i = 0; i < remap.Length; i++) remap[i] = -1;

            var keptVertices = new System.Collections.Generic.List<Vector3>();
            var keptNormals = new System.Collections.Generic.List<Vector3>();
            var keptUv = new System.Collections.Generic.List<Vector2>();
            var keptWeights = new System.Collections.Generic.List<BoneWeight>();
            var submeshTriangles = new System.Collections.Generic.List<int[]>();
            var totalTriangles = 0;

            for (var sub = 0; sub < mesh.subMeshCount; sub++)
            {
                var triangles = mesh.GetTriangles(sub);
                var kept = new System.Collections.Generic.List<int>();

                for (var t = 0; t + 2 < triangles.Length; t += 3)
                {
                    int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];

                    // Whole triangles only. A partial one leaves edges anchored to vertices that
                    // are not here, which is the torn look this is avoiding.
                    if (!belongs[a] || !belongs[b] || !belongs[c]) continue;

                    kept.Add(Emit(a));
                    kept.Add(Emit(b));
                    kept.Add(Emit(c));
                }

                totalTriangles += kept.Count / 3;
                submeshTriangles.Add(kept.ToArray());
            }

            if (totalTriangles == 0) return null;

            // Always capped. Without it the hand is an open shell and you see its inside,
            // since the mesh has no back faces — nobody would choose that.
            var capped = Cap(submeshTriangles, keptVertices, keptNormals, keptUv, keptWeights);

            var built = new Mesh { name = $"NobetaVR {side} hand" };
            built.SetVertices(ToVector3Array(keptVertices));
            if (keptNormals.Count == keptVertices.Count) built.SetNormals(ToVector3Array(keptNormals));
            if (keptUv.Count == keptVertices.Count) built.SetUVs(0, ToVector2Array(keptUv));

            built.boneWeights = ToBoneWeightArray(keptWeights);
            built.bindposes = ToMatrixArray(keptBindposes);

            built.subMeshCount = submeshTriangles.Count;
            for (var sub = 0; sub < submeshTriangles.Count; sub++)
                built.SetTriangles(new Il2CppStructArray<int>(submeshTriangles[sub]), sub);

            built.RecalculateBounds();

            Plugin.Log.LogInfo($"{side} hand cut out: {keptVertices.Count} vertices, "
                             + $"{totalTriangles} triangles, {keptBones.Count} bones "
                             + $"from '{mesh.name}'"
                             + (capped > 0 ? $", {capped} triangles capping the wrist" : ""));

            return new Result { Mesh = built, Bones = keptBones.ToArray() };

            int Emit(int index)
            {
                if (remap[index] >= 0) return remap[index];

                remap[index] = keptVertices.Count;
                keptVertices.Add(vertices[index]);
                if (normals != null && normals.Length == vertices.Length) keptNormals.Add(normals[index]);
                if (uv != null && uv.Length == vertices.Length) keptUv.Add(uv[index]);
                keptWeights.Add(Renumber(weights[index]));

                return remap[index];
            }

            // Weights on bones outside the hand are dropped and the rest renormalised, so a wrist
            // vertex that was part forearm becomes wholly the wrist's rather than being dragged
            // towards a bone this mesh does not have. That drag was the earlier bug, in miniature.
            BoneWeight Renumber(BoneWeight weight)
            {
                var renumbered = new BoneWeight();
                var total = 0f;

                Take(weight.boneIndex0, weight.weight0, 0);
                Take(weight.boneIndex1, weight.weight1, 1);
                Take(weight.boneIndex2, weight.weight2, 2);
                Take(weight.boneIndex3, weight.weight3, 3);

                if (total <= 0f)
                {
                    renumbered.boneIndex0 = 0;
                    renumbered.weight0 = 1f;
                    return renumbered;
                }

                renumbered.weight0 /= total;
                renumbered.weight1 /= total;
                renumbered.weight2 /= total;
                renumbered.weight3 /= total;
                return renumbered;

                void Take(int bone, float value, int slot)
                {
                    if (value <= 0f || !Owned(owners, bone)) return;

                    if (boneRemap[bone] < 0)
                    {
                        boneRemap[bone] = keptBones.Count;
                        keptBones.Add(sourceBones[bone]);
                        keptBindposes.Add(bindposes[bone]);
                    }

                    var index = boneRemap[bone];
                    total += value;

                    switch (slot)
                    {
                        case 0: renumbered.boneIndex0 = index; renumbered.weight0 = value; break;
                        case 1: renumbered.boneIndex1 = index; renumbered.weight1 = value; break;
                        case 2: renumbered.boneIndex2 = index; renumbered.weight2 = value; break;
                        default: renumbered.boneIndex3 = index; renumbered.weight3 = value; break;
                    }
                }
            }
        }

        /// <summary>
        /// Closes the openings left by the cut, so you cannot see inside the wrist.
        ///
        /// Cutting triangles out of a closed surface leaves a hole, and a hole in a mesh with no
        /// back faces shows its interior. The rim of that hole needs no guesswork to find: in a
        /// closed surface every edge is shared by two triangles, so after the cut the edges that
        /// belong to exactly one are precisely the boundary. Chaining them end to end gives the
        /// loops around each opening.
        ///
        /// Each loop is filled with a fan to its own centre. The rim vertices are duplicated for
        /// the cap rather than reused, so the cap can be given a flat normal of its own without
        /// disturbing the rounded shading of the wrist beside it. Winding is decided by
        /// measurement rather than assumption: the cap's normal is compared against the direction
        /// leading away from the hand, and the triangles are reversed if it points the wrong way.
        /// A cap facing inwards is invisible and would look exactly like no cap at all.
        /// </summary>
        private static int Cap(System.Collections.Generic.List<int[]> submeshTriangles,
                               System.Collections.Generic.List<Vector3> vertices,
                               System.Collections.Generic.List<Vector3> normals,
                               System.Collections.Generic.List<Vector2> uv,
                               System.Collections.Generic.List<BoneWeight> weights)
        {
            var hasNormals = normals.Count == vertices.Count;
            var hasUv = uv.Count == vertices.Count;

            // Directed edges, counted regardless of direction. A pair seen twice is interior.
            var seen = new System.Collections.Generic.Dictionary<long, int>();
            var direction = new System.Collections.Generic.Dictionary<long, (int From, int To)>();

            foreach (var triangles in submeshTriangles)
            {
                for (var i = 0; i + 2 < triangles.Length; i += 3)
                {
                    Count(triangles[i], triangles[i + 1]);
                    Count(triangles[i + 1], triangles[i + 2]);
                    Count(triangles[i + 2], triangles[i]);
                }
            }

            var next = new System.Collections.Generic.Dictionary<int, int>();
            foreach (var pair in seen)
            {
                if (pair.Value != 1) continue;
                var edge = direction[pair.Key];
                next[edge.From] = edge.To;
            }
            if (next.Count == 0) return 0;

            // The hand's middle, to tell which way is out of the wrist.
            var centre = Vector3.zero;
            foreach (var vertex in vertices) centre += vertex;
            centre /= vertices.Count;

            var cap = new System.Collections.Generic.List<int>();
            var visited = new System.Collections.Generic.HashSet<int>();
            var added = 0;

            foreach (var start in next.Keys)
            {
                if (visited.Contains(start)) continue;

                var loop = new System.Collections.Generic.List<int>();
                var current = start;

                while (visited.Add(current))
                {
                    loop.Add(current);
                    if (!next.TryGetValue(current, out current)) break;
                }

                // Two vertices cannot enclose anything; a stray edge is not an opening.
                if (loop.Count < 3) continue;
                added += Fill(loop);
            }

            if (cap.Count == 0) return 0;

            // Into the submesh with the most geometry, which is the skin rather than any small
            // detail material, so the cap is shaded like the wrist it closes.
            var biggest = 0;
            for (var i = 1; i < submeshTriangles.Count; i++)
                if (submeshTriangles[i].Length > submeshTriangles[biggest].Length) biggest = i;

            var merged = new System.Collections.Generic.List<int>(submeshTriangles[biggest]);
            merged.AddRange(cap);
            submeshTriangles[biggest] = merged.ToArray();

            return added;

            void Count(int a, int b)
            {
                var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                seen[key] = seen.TryGetValue(key, out var count) ? count + 1 : 1;
                direction[key] = (a, b);
            }

            int Fill(System.Collections.Generic.List<int> loop)
            {
                var middle = Vector3.zero;
                var middleUv = Vector2.zero;
                foreach (var index in loop)
                {
                    middle += vertices[index];
                    if (hasUv) middleUv += uv[index];
                }
                middle /= loop.Count;
                if (hasUv) middleUv /= loop.Count;

                var outward = (middle - centre).normalized;
                if (outward.sqrMagnitude < 1e-6f) outward = Vector3.up;

                // Duplicated rim, so the cap gets its own flat normal.
                var rim = new int[loop.Count];
                for (var i = 0; i < loop.Count; i++)
                {
                    rim[i] = vertices.Count;
                    vertices.Add(vertices[loop[i]]);
                    if (hasNormals) normals.Add(outward);
                    if (hasUv) uv.Add(uv[loop[i]]);
                    weights.Add(weights[loop[i]]);
                }

                var centreIndex = vertices.Count;
                vertices.Add(middle);
                if (hasNormals) normals.Add(outward);
                if (hasUv) uv.Add(middleUv);
                weights.Add(weights[loop[0]]);

                // Measured, not assumed: build one triangle, see which way it faces, and reverse
                // the whole fan if it faces into the hand.
                var first = Vector3.Cross(vertices[rim[1]] - vertices[rim[0]],
                                          middle - vertices[rim[0]]);
                var flip = Vector3.Dot(first, outward) < 0f;

                for (var i = 0; i < rim.Length; i++)
                {
                    var a = rim[i];
                    var b = rim[(i + 1) % rim.Length];

                    if (flip) { cap.Add(b); cap.Add(a); cap.Add(centreIndex); }
                    else { cap.Add(a); cap.Add(b); cap.Add(centreIndex); }
                }

                return rim.Length;
            }
        }

        /// <summary>How much of a vertex belongs to the hand, counting every bone below the wrist.</summary>
        private static float WeightOn(BoneWeight weight, bool[] owners)
        {
            var total = 0f;
            if (Owned(owners, weight.boneIndex0)) total += weight.weight0;
            if (Owned(owners, weight.boneIndex1)) total += weight.weight1;
            if (Owned(owners, weight.boneIndex2)) total += weight.weight2;
            if (Owned(owners, weight.boneIndex3)) total += weight.weight3;
            return total;
        }

        private static bool Owned(bool[] owners, int index) =>
            index >= 0 && index < owners.Length && owners[index];

        private static bool[] OwnedBones(Il2CppReferenceArray<Transform> bones, Transform handBone)
        {
            var owners = new bool[bones.Length];
            var wanted = handBone.GetInstanceID();

            for (var i = 0; i < bones.Length; i++)
            {
                var bone = bones[i];
                if (bone == null) continue;

                for (var t = bone; t != null; t = t.parent)
                {
                    if (t.GetInstanceID() != wanted) continue;
                    owners[i] = true;
                    break;
                }
            }
            return owners;
        }

        private static Il2CppStructArray<Vector3> ToVector3Array(System.Collections.Generic.List<Vector3> values)
        {
            var array = new Il2CppStructArray<Vector3>(values.Count);
            for (var i = 0; i < values.Count; i++) array[i] = values[i];
            return array;
        }

        private static Il2CppStructArray<Vector2> ToVector2Array(System.Collections.Generic.List<Vector2> values)
        {
            var array = new Il2CppStructArray<Vector2>(values.Count);
            for (var i = 0; i < values.Count; i++) array[i] = values[i];
            return array;
        }

        private static Il2CppStructArray<BoneWeight> ToBoneWeightArray(System.Collections.Generic.List<BoneWeight> values)
        {
            var array = new Il2CppStructArray<BoneWeight>(values.Count);
            for (var i = 0; i < values.Count; i++) array[i] = values[i];
            return array;
        }

        private static Il2CppStructArray<Matrix4x4> ToMatrixArray(System.Collections.Generic.List<Matrix4x4> values)
        {
            var array = new Il2CppStructArray<Matrix4x4>(values.Count);
            for (var i = 0; i < values.Count; i++) array[i] = values[i];
            return array;
        }
    }
}
