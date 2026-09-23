using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Her whole shadow, while the parts of her the view cannot have are taken away.
    ///
    /// First person takes her head and arms out of the picture by scaling a bone to nothing —
    /// the head because your eyes are inside it (<see cref="FirstPerson.UpdateHeadVisibility"/>),
    /// the arms because the hands are drawn on the controllers instead (<see cref="DetachedHands"/>).
    /// A zero scale is the right tool for the view and the wrong one for the shadow: the shadow
    /// pass draws the same collapsed mesh, so what falls on the floor is a headless, armless
    /// body, which is exactly the thing the view went to some trouble not to show.
    ///
    /// <para>
    /// So her shadow is cast by a second copy of her that is never seen. Every skinned mesh
    /// with geometry on a collapsed branch is drawn again with <c>ShadowsOnly</c>, bound to the
    /// character's own bones everywhere except on those branches, where it is bound to copies
    /// that take the animation's local pose each frame and keep their scale. The copies of an
    /// arm then reach for the hand on the controller with a two-bone solve, so the shadow's
    /// hands are where yours are rather than where the animation left hers.
    /// </para>
    ///
    /// <para>
    /// The real renderers keep casting too. Their collapsed parts are gathered at the head and
    /// shoulder joints, inside the copy's shadow, so the two together draw the same shadow the
    /// copy draws alone, and nothing of hers has a second owner for its shadow setting —
    /// <see cref="BodyVisibility"/> is the one that writes it, for the same reason.
    /// </para>
    ///
    /// <para>
    /// Kept outside the character, as the detached hands are: anything added under her would be
    /// found by every <c>GetComponentsInChildren</c> the mod and the game run on her, and the
    /// hands would be cut out of this copy as well as out of her.
    /// </para>
    /// </summary>
    internal static class ShadowBody
    {
        /// <summary>
        /// One collapsed bone and everything below it, copied.
        /// </summary>
        private sealed class Branch
        {
            public Transform Source;
            public Transform[] Sources;   // the subtree, Source first
            public Transform[] Copies;    // parallel to Sources

            /// <summary>The copies of the forearm and hand, on an arm; null on the head.</summary>
            public Transform Fore, Hand;
        }

        private static PlayerCamera _camera;
        private static Transform _holder;

        private static Branch _head, _leftArm, _rightArm;
        private static SkinnedMeshRenderer[] _sources;
        private static Renderer[] _proxies;

        /// <summary>
        /// The rigid meshes hanging off a collapsed branch — a hat or a hair ornament kept as a
        /// plain <c>MeshRenderer</c> — paired with the copies that cast their shadow.
        /// </summary>
        private static Renderer[] _rigidSources;

        /// <summary>What the current copy was built from: the body, the head and both upper arms.</summary>
        private static int _fromRoot, _fromHead, _fromLeft, _fromRight;

        private static bool _built;
        private static bool _shown;
        private static bool _reported;

        /// <summary>
        /// Points this at a new <c>PlayerCamera</c>. A new stage is a new body, and a copy of
        /// the last one is bound to bones that no longer exist.
        /// </summary>
        internal static void Rebind(PlayerCamera camera)
        {
            Clear();
            _camera = camera;
            _reported = false;
        }

        /// <summary>
        /// Poses the copy for this frame, or puts it away.
        ///
        /// Called once the view is final and the head and arms have been collapsed or given
        /// back for this frame, and still inside LateUpdate: the copies are skinned meshes, and
        /// Unity takes their bone matrices in PostLateUpdate, straight after.
        /// </summary>
        /// <param name="viewInHerHead">Whether first person is driving the view this frame.</param>
        /// <param name="head">The head bone first person anchors to, or null before it resolves.</param>
        /// <param name="headRestScale">The scale the head bone has while it is not hidden.</param>
        internal static void Tick(bool viewInHerHead, Transform head, Vector3 headRestScale)
        {
            var girl = _camera != null ? _camera.wizardGirl : null;
            var wanted = viewInHerHead && girl != null && Plugin.Instance.FullShadow.Value;

            if (!wanted)
            {
                Show(false);

                // Between stages there is nothing to hold on to, and the copy is bound to bones
                // that are on their way out with her.
                if (girl == null && _built) Clear();
                return;
            }

            VrHands.TryArm(true, out var leftUpper, out var leftFore, out var leftHand);
            VrHands.TryArm(false, out var rightUpper, out var rightFore, out var rightHand);

            if (!Current(girl.transform, head, leftUpper, rightUpper))
            {
                Clear();
                Build(girl.transform, head, leftUpper, leftFore, leftHand,
                      rightUpper, rightFore, rightHand);
            }

            if (_proxies == null || _proxies.Length == 0)
            {
                Show(false);
                return;
            }

            Show(true);

            Pose(_head, headRestScale);

            PoseArm(_leftArm, true, girl.transform);
            PoseArm(_rightArm, false, girl.transform);

            MirrorVisibility();
        }

        /// <summary>
        /// Whether the copy was built from this body, this head and these arms, and none of the
        /// meshes it copies has been destroyed since — a costume and the story outfit both swap
        /// the skin under a root that stays.
        /// </summary>
        private static bool Current(Transform root, Transform head, Transform leftUpper, Transform rightUpper)
        {
            if (!_built) return false;
            if (root.GetInstanceID() != _fromRoot) return false;
            if (Id(head) != _fromHead || Id(leftUpper) != _fromLeft || Id(rightUpper) != _fromRight)
                return false;

            for (var i = 0; i < _sources.Length; i++)
                if (_sources[i] == null) return false;
            for (var i = 0; i < _rigidSources.Length; i++)
                if (_rigidSources[i] == null) return false;

            return true;
        }

        private static int Id(Object thing) => thing != null ? thing.GetInstanceID() : 0;

        private static void Build(Transform root, Transform head,
                                  Transform leftUpper, Transform leftFore, Transform leftHand,
                                  Transform rightUpper, Transform rightFore, Transform rightHand)
        {
            _built = true;
            _fromRoot = root.GetInstanceID();
            _fromHead = Id(head);
            _fromLeft = Id(leftUpper);
            _fromRight = Id(rightUpper);

            if (_holder == null)
            {
                var holder = new GameObject("NobetaVR Shadow");
                Object.DontDestroyOnLoad(holder);
                holder.hideFlags = HideFlags.HideAndDontSave;
                _holder = holder.transform;
            }
            _holder.gameObject.SetActive(false);
            _shown = false;

            // Every bone on a collapsed branch, keyed to its copy.
            var copies = new Dictionary<int, Transform>();
            _head = Copy(head, copies);
            _leftArm = Copy(leftUpper, copies);
            _rightArm = Copy(rightUpper, copies);

            if (_leftArm != null)
            {
                _leftArm.Fore = Find(leftFore, copies);
                _leftArm.Hand = Find(leftHand, copies);
            }
            if (_rightArm != null)
            {
                _rightArm.Fore = Find(rightFore, copies);
                _rightArm.Hand = Find(rightHand, copies);
            }

            var sources = new List<SkinnedMeshRenderer>();
            var proxies = new List<Renderer>();

            var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var i = 0; i < skinned.Length; i++)
            {
                var source = skinned[i];
                if (source == null || source.sharedMesh == null) continue;

                var proxy = SkinnedProxy(source, copies);
                if (proxy == null) continue;

                sources.Add(source);
                proxies.Add(proxy);
            }

            // Rigid meshes riding on the head went to nothing with it. Their copies are hung off
            // the bone copies, where they follow the pose without any help. Not on the arms:
            // what rides on a hand is the wand, which the detached hands carry and which casts
            // its own shadow from there — copied here as well, it would cast a second.
            var rigidSources = new List<Renderer>();
            RigidProxies(_head, rigidSources, proxies);

            _sources = sources.ToArray();
            _rigidSources = rigidSources.ToArray();
            _proxies = proxies.ToArray();

            if (!_reported)
            {
                _reported = true;
                var names = new List<string>(_proxies.Length);
                for (var i = 0; i < _sources.Length; i++) names.Add(_sources[i].name);
                for (var i = 0; i < _rigidSources.Length; i++) names.Add(_rigidSources[i].name);

                Plugin.Log.LogInfo($"full shadow: {copies.Count} bone(s) copied, "
                                 + $"{_proxies.Length} shadow caster(s)"
                                 + (names.Count > 0 ? ": " + string.Join(", ", names) : string.Empty));
            }
        }

        /// <summary>
        /// Copies one bone and its subtree as bare transforms. Only transforms: instantiating
        /// the bone would bring the hair's physics, the colliders and whatever else the game
        /// hangs off a skeleton, all of it running a second time on a body nobody sees.
        /// </summary>
        private static Branch Copy(Transform source, Dictionary<int, Transform> copies)
        {
            if (source == null) return null;

            var sources = new List<Transform>();
            var made = new List<Transform>();
            CopyInto(source, _holder, sources, made, copies);

            return new Branch
            {
                Source = source,
                Sources = sources.ToArray(),
                Copies = made.ToArray(),
            };
        }

        private static void CopyInto(Transform source, Transform parent, List<Transform> sources,
                                     List<Transform> made, Dictionary<int, Transform> copies)
        {
            var copy = new GameObject(source.name).transform;
            copy.gameObject.hideFlags = HideFlags.HideAndDontSave;
            copy.SetParent(parent, false);
            copy.localPosition = source.localPosition;
            copy.localRotation = source.localRotation;
            copy.localScale = source.localScale;

            sources.Add(source);
            made.Add(copy);
            copies[source.GetInstanceID()] = copy;

            var count = source.childCount;
            for (var i = 0; i < count; i++) CopyInto(source.GetChild(i), copy, sources, made, copies);
        }

        private static Transform Find(Transform source, Dictionary<int, Transform> copies) =>
            source != null && copies.TryGetValue(source.GetInstanceID(), out var copy) ? copy : null;

        /// <summary>
        /// A shadow-only copy of one skinned mesh, bound to the copied branches — or null for a
        /// mesh with nothing on them, which casts its whole shadow already.
        ///
        /// Decided by the vertex weights rather than by the bone array. A character's meshes
        /// commonly share one skeleton, so the cape lists the head and both arms whether or not
        /// a single vertex of it follows them, and copying on the bone array alone would skin
        /// every mesh she has twice for nothing.
        /// </summary>
        private static Renderer SkinnedProxy(SkinnedMeshRenderer source, Dictionary<int, Transform> copies)
        {
            if (source.shadowCastingMode == ShadowCastingMode.Off) return null;

            var bones = source.bones;
            if (bones == null || bones.Length == 0) return null;

            var onBranch = new bool[bones.Length];
            var any = false;
            for (var b = 0; b < bones.Length; b++)
            {
                var bone = bones[b];
                if (bone == null || !copies.ContainsKey(bone.GetInstanceID())) continue;
                onBranch[b] = true;
                any = true;
            }
            if (!any || !Weighted(source.sharedMesh, onBranch)) return null;

            var remapped = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Transform>(bones.Length);
            for (var b = 0; b < bones.Length; b++)
                remapped[b] = onBranch[b] ? copies[bones[b].GetInstanceID()] : bones[b];

            var part = new GameObject(source.name);
            part.hideFlags = HideFlags.HideAndDontSave;
            part.layer = source.gameObject.layer;
            part.transform.SetParent(_holder, false);

            var proxy = part.AddComponent<SkinnedMeshRenderer>();
            proxy.sharedMesh = source.sharedMesh;
            proxy.sharedMaterials = source.sharedMaterials;
            proxy.bones = remapped;
            proxy.rootBone = Find(source.rootBone, copies) ?? source.rootBone;
            proxy.quality = source.quality;

            // Recomputed from the bones every frame. The arms reach well outside the bounds the
            // mesh was authored with, and a shadow caster culled on stale bounds is a shadow
            // that blinks out whenever she raises a hand.
            proxy.updateWhenOffscreen = true;

            proxy.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            proxy.receiveShadows = false;
            proxy.lightProbeUsage = LightProbeUsage.Off;
            proxy.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return proxy;
        }

        /// <summary>Whether any vertex of the mesh carries weight on a marked bone.</summary>
        private static bool Weighted(Mesh mesh, bool[] marked)
        {
            // A mesh whose data never left the GPU cannot be read, and asking logs an error.
            // Copied on trust instead: a spare shadow caster costs less than a missing one.
            if (!mesh.isReadable) return true;

            var weights = mesh.boneWeights;
            if (weights == null) return false;

            for (var i = 0; i < weights.Length; i++)
            {
                var w = weights[i];
                if (On(marked, w.boneIndex0, w.weight0) || On(marked, w.boneIndex1, w.weight1)
                 || On(marked, w.boneIndex2, w.weight2) || On(marked, w.boneIndex3, w.weight3))
                    return true;
            }
            return false;
        }

        private static bool On(bool[] marked, int index, float weight) =>
            weight > 0f && index >= 0 && index < marked.Length && marked[index];

        private static void RigidProxies(Branch branch, List<Renderer> sources, List<Renderer> proxies)
        {
            if (branch == null) return;

            for (var i = 0; i < branch.Sources.Length; i++)
            {
                var bone = branch.Sources[i];
                if (bone == null) continue;

                var renderer = bone.GetComponent<MeshRenderer>();
                if (renderer == null || renderer.shadowCastingMode == ShadowCastingMode.Off) continue;

                var filter = bone.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;

                var copy = branch.Copies[i];
                copy.gameObject.layer = bone.gameObject.layer;

                var proxyFilter = copy.gameObject.AddComponent<MeshFilter>();
                proxyFilter.sharedMesh = filter.sharedMesh;

                var proxy = copy.gameObject.AddComponent<MeshRenderer>();
                proxy.sharedMaterials = renderer.sharedMaterials;
                proxy.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                proxy.receiveShadows = false;
                proxy.lightProbeUsage = LightProbeUsage.Off;
                proxy.reflectionProbeUsage = ReflectionProbeUsage.Off;

                sources.Add(renderer);
                proxies.Add(proxy);
            }
        }

        /// <summary>
        /// Puts a branch where the animation has it, at the scale it is owed.
        ///
        /// The branch's own bone is placed in the world, from its real parent — which is never
        /// collapsed — and its real local pose, which the animator goes on writing whatever its
        /// scale. Everything below it takes its local pose straight across.
        /// </summary>
        private static void Pose(Branch branch, Vector3 restScale)
        {
            if (branch == null) return;

            var source = branch.Source;
            var parent = source != null ? source.parent : null;
            if (parent == null) return;

            var root = branch.Copies[0];
            root.SetPositionAndRotation(parent.TransformPoint(source.localPosition),
                                        parent.rotation * source.localRotation);
            root.localScale = Vector3.Scale(parent.lossyScale, restScale);

            for (var i = 1; i < branch.Sources.Length; i++)
            {
                var from = branch.Sources[i];
                if (from == null) continue;

                var to = branch.Copies[i];
                to.localPosition = from.localPosition;
                to.localRotation = from.localRotation;
                to.localScale = from.localScale;
            }
        }

        /// <summary>
        /// Poses an arm, and bends it to the hand on the controller while the real one is
        /// collapsed behind that hand. While it is not, the real arm is whole and casting its
        /// own shadow, and the copy follows the same animation so the two agree.
        /// </summary>
        private static void PoseArm(Branch arm, bool left, Transform body)
        {
            if (arm == null) return;

            if (!VrHands.TryCollapsedArm(left, out var restScale, out var target, out var targetRotation))
            {
                Pose(arm, arm.Source != null ? arm.Source.localScale : Vector3.one);
                return;
            }

            Pose(arm, restScale);
            if (arm.Fore == null || arm.Hand == null) return;

            Reach(arm.Copies[0], arm.Fore, arm.Hand, target, Pole(arm.Copies[0].position, body));
            arm.Hand.rotation = targetRotation;
        }

        /// <summary>
        /// Where the elbow should point: down, out to her side and a little behind, which is
        /// where an elbow goes for nearly anything a hand does in front of the body. A shadow
        /// only needs the arm to be plausible, and a fixed hint never flips the way the
        /// animation's own bend does once the hand is somewhere the animation never put it.
        /// </summary>
        private static Vector3 Pole(Vector3 shoulder, Transform body)
        {
            var outward = shoulder - body.position;
            outward.y = 0f;
            outward = outward.sqrMagnitude > 1e-6f ? outward.normalized : Vector3.zero;

            return shoulder + Vector3.down * 0.5f + outward * 0.3f - body.forward * 0.15f;
        }

        /// <summary>
        /// A two-bone solve: turns the upper arm and the forearm so the hand lands on the target,
        /// with the elbow in the plane of the pole. Beyond reach the arm points straight at it.
        /// </summary>
        private static void Reach(Transform upper, Transform fore, Transform hand, Vector3 target, Vector3 pole)
        {
            var a = upper.position;
            var upperLength = Vector3.Distance(a, fore.position);
            var foreLength = Vector3.Distance(fore.position, hand.position);
            if (upperLength < 1e-4f || foreLength < 1e-4f) return;

            var toTarget = target - a;
            var distance = toTarget.magnitude;
            if (distance < 1e-4f) return;

            var direction = toTarget / distance;
            distance = Mathf.Clamp(distance, Mathf.Abs(upperLength - foreLength) + 1e-3f,
                                   (upperLength + foreLength) * 0.999f);

            // Law of cosines: how far along the line to the target the elbow sits, and how far
            // off it towards the pole.
            var along = (upperLength * upperLength - foreLength * foreLength + distance * distance)
                      / (2f * distance);
            var off = Mathf.Sqrt(Mathf.Max(0f, upperLength * upperLength - along * along));

            var bend = Vector3.ProjectOnPlane(pole - a, direction);
            if (bend.sqrMagnitude < 1e-6f) bend = Vector3.ProjectOnPlane(Vector3.down, direction);
            if (bend.sqrMagnitude < 1e-6f) bend = Vector3.ProjectOnPlane(Vector3.forward, direction);
            bend.Normalize();

            var elbow = a + direction * along + bend * off;

            upper.rotation = Quaternion.FromToRotation(fore.position - a, elbow - a) * upper.rotation;

            var wrist = fore.position;
            fore.rotation = Quaternion.FromToRotation(hand.position - wrist, target - wrist) * fore.rotation;
        }

        /// <summary>
        /// Casts only what her real renderers would be drawing: a mesh the game has switched
        /// off, or that <see cref="BodyVisibility"/> is holding off entirely, casts nothing
        /// here either.
        /// </summary>
        private static void MirrorVisibility()
        {
            var i = 0;
            for (var s = 0; s < _sources.Length; s++, i++) Mirror(_sources[s], _proxies[i]);
            for (var s = 0; s < _rigidSources.Length; s++, i++) Mirror(_rigidSources[s], _proxies[i]);
        }

        private static void Mirror(Renderer source, Renderer proxy)
        {
            if (proxy == null) return;

            var casts = source != null
                     && source.enabled
                     && source.gameObject.activeInHierarchy
                     && !source.forceRenderingOff
                     && source.shadowCastingMode != ShadowCastingMode.Off;

            if (proxy.enabled != casts) proxy.enabled = casts;
        }

        private static void Show(bool shown)
        {
            if (_shown == shown || _holder == null) return;
            _shown = shown;
            _holder.gameObject.SetActive(shown);
        }

        /// <summary>Destroys the copy. Nothing of hers was changed, so nothing is given back.</summary>
        private static void Clear()
        {
            if (_holder != null)
            {
                _holder.gameObject.SetActive(false);
                // Switched off before being destroyed: Destroy waits for the end of the frame, and
                // a rebuild turns the holder straight back on, which would let the old copy cast
                // one more shadow from bones that may already be gone.
                var count = _holder.childCount;
                for (var i = count - 1; i >= 0; i--)
                {
                    var child = _holder.GetChild(i).gameObject;
                    child.SetActive(false);
                    Object.Destroy(child);
                }
            }

            _shown = false;
            _built = false;
            _head = _leftArm = _rightArm = null;
            _sources = null;
            _rigidSources = null;
            _proxies = null;
            _fromRoot = _fromHead = _fromLeft = _fromRight = 0;
        }
    }
}
