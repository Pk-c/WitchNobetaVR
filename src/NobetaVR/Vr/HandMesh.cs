using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Cuts the hand out of the character's mesh as a small rigid mesh of its own.
    ///
    /// This replaces an approach that could not have worked, and the reason is worth keeping
    /// because it explains what was on screen. Reusing the character's skinned mesh with a
    /// rewritten bone array means every vertex is still a weighted blend of several bones, and
    /// every bone that is not the hand was being sent to a single collapsed point. A wrist
    /// vertex weighted half to the hand and half to the forearm therefore landed halfway between
    /// where it belongs and that point — so the hand did not render wrongly, it rendered
    /// *shrunk*, pulled towards its own pivot in proportion to how much of each vertex belonged
    /// to the arm. Two small scraps of geometry is exactly what that looks like.
    ///
    /// Taking the triangles out removes the blend from the problem entirely. What is kept is
    /// what belongs to the hand outright; it is baked into the hand bone's own space using the
    /// bind pose, so it needs no skinning at all and is simply drawn wherever the hand is.
    /// Normals come along and are transformed the same way, so lighting is unchanged.
    /// </summary>
    internal static class HandMesh
    {
        /// <summary>
        /// Builds the hand's mesh, or returns null if the character's mesh cannot be read.
        ///
        /// A mesh shipped in a game usually has Read/Write disabled, in which case Unity keeps no
        /// CPU copy and the vertex arrays come back empty. That is a property of the build, not
        /// something to work around, so it is checked up front and reported plainly.
        /// </summary>
        public static Mesh Build(SkinnedMeshRenderer source, Transform handBone, string side,
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

            var handIndex = IndexOf(source.bones, handBone);
            if (handIndex < 0) return null;

            // The fingers count as the hand. They are skinned to their own bones —
            // Bip001 L Finger0 and its neighbours — so weighing vertices against the hand bone
            // alone kept the palm and dropped every finger, which is what came out: hands with
            // nothing on the end of them. Everything below the wrist is one rigid piece here, so
            // the whole subtree is treated as one owner.
            var owners = OwnedBones(source.bones, handBone);

            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var uv = mesh.uv;
            var weights = mesh.boneWeights;
            var bindposes = mesh.bindposes;

            if (vertices == null || vertices.Length == 0 || weights == null || bindposes == null)
            {
                Plugin.Log.LogWarning($"mesh '{mesh.name}' reports itself readable but returned no "
                                    + "vertex data.");
                return null;
            }

            // Into the hand bone's own space, so the result is a rigid mesh that needs only a
            // transform rather than a skeleton.
            var toHandSpace = bindposes[handIndex];

            var belongs = new bool[vertices.Length];
            var owned = 0;
            for (var i = 0; i < vertices.Length; i++)
            {
                if (WeightOn(weights[i], owners) < minimumWeight) continue;
                belongs[i] = true;
                owned++;
            }

            if (owned == 0) return null;

            var result = new Mesh { name = $"NobetaVR {side} Hand" };
            var remap = new int[vertices.Length];
            for (var i = 0; i < remap.Length; i++) remap[i] = -1;

            var keptVertices = new System.Collections.Generic.List<Vector3>();
            var keptNormals = new System.Collections.Generic.List<Vector3>();
            var keptUv = new System.Collections.Generic.List<Vector2>();
            var submeshTriangles = new System.Collections.Generic.List<int[]>();
            var totalTriangles = 0;

            for (var sub = 0; sub < mesh.subMeshCount; sub++)
            {
                var triangles = mesh.GetTriangles(sub);
                var kept = new System.Collections.Generic.List<int>();

                for (var t = 0; t + 2 < triangles.Length; t += 3)
                {
                    int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];

                    // Whole triangles only. Keeping partial ones would leave edges anchored to
                    // vertices that are not here, which is the torn look this is avoiding.
                    if (!belongs[a] || !belongs[b] || !belongs[c]) continue;

                    kept.Add(Emit(a));
                    kept.Add(Emit(b));
                    kept.Add(Emit(c));
                }

                totalTriangles += kept.Count / 3;
                submeshTriangles.Add(kept.ToArray());
            }

            if (totalTriangles == 0)
            {
                // Normal, and not worth a warning on its own: several meshes share one skeleton,
                // so most of them list the hand bone while holding none of its geometry. The
                // caller tries them all and reports only if none of them had any.
                Object.Destroy(result);
                return null;
            }

            result.SetVertices(ToIl2Cpp(keptVertices));
            if (keptNormals.Count == keptVertices.Count) result.SetNormals(ToIl2Cpp(keptNormals));
            if (keptUv.Count == keptVertices.Count) result.SetUVs(0, ToIl2Cpp2(keptUv));

            result.subMeshCount = submeshTriangles.Count;
            for (var sub = 0; sub < submeshTriangles.Count; sub++)
                result.SetTriangles(new Il2CppStructArray<int>(submeshTriangles[sub]), sub);

            result.RecalculateBounds();

            Plugin.Log.LogInfo($"{side} hand cut out: {keptVertices.Count} vertices, "
                             + $"{totalTriangles} triangles from '{mesh.name}' "
                             + $"({owned} of {vertices.Length} vertices owned)");
            return result;

            int Emit(int index)
            {
                if (remap[index] >= 0) return remap[index];

                remap[index] = keptVertices.Count;
                keptVertices.Add(toHandSpace.MultiplyPoint3x4(vertices[index]));
                if (normals != null && normals.Length == vertices.Length)
                    keptNormals.Add(toHandSpace.MultiplyVector(normals[index]).normalized);
                if (uv != null && uv.Length == vertices.Length)
                    keptUv.Add(uv[index]);

                return remap[index];
            }
        }

        /// <summary>
        /// How much of a vertex belongs to the hand, counting every bone at or below the wrist.
        /// </summary>
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

        /// <summary>
        /// Flags every bone of the renderer that is the hand or a descendant of it.
        /// </summary>
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

        private static int IndexOf(Il2CppReferenceArray<Transform> bones, Transform bone)
        {
            if (bones == null || bone == null) return -1;
            var wanted = bone.GetInstanceID();

            for (var i = 0; i < bones.Length; i++)
                if (bones[i] != null && bones[i].GetInstanceID() == wanted) return i;
            return -1;
        }

        private static Il2CppStructArray<Vector3> ToIl2Cpp(System.Collections.Generic.List<Vector3> values)
        {
            var array = new Il2CppStructArray<Vector3>(values.Count);
            for (var i = 0; i < values.Count; i++) array[i] = values[i];
            return array;
        }

        private static Il2CppStructArray<Vector2> ToIl2Cpp2(System.Collections.Generic.List<Vector2> values)
        {
            var array = new Il2CppStructArray<Vector2>(values.Count);
            for (var i = 0; i < values.Count; i++) array[i] = values[i];
            return array;
        }
    }
}
