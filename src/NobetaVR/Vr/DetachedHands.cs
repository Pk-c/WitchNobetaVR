using UnityEngine;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Hands only, exactly where the controllers are — the arms taken out of the picture.
    ///
    /// This is the honest answer for this rig. Driving her real arms needs a two-bone solve, an
    /// elbow pole, and a wrist roll split across a forearm with one bone to split it over, while
    /// sharing vertex weights with a sleeve and LateUpdate with a dynamic-bone cape. Each was
    /// solvable and each cost a test cycle, for a pair of arms you can barely see in first person.
    ///
    /// Two approaches were tried and abandoned before this one, and both failed for the same
    /// underlying reason: **a skinned vertex is a blend, and a blend cannot be split.**
    ///
    /// Lifting the hand bones out of the skeleton and collapsing the arms behind them left elbow
    /// vertices weighted to both the upper arm and the forearm — one collapsed at the shoulder,
    /// the other parked on the hand — so they stretched between the two. That was the strand
    /// running from each wrist back into the body.
    ///
    /// Redrawing the mesh with a rewritten bone array had the same flaw one joint further down.
    /// Wrist vertices are part hand, part forearm, and the forearm was being sent to a collapsed
    /// point, so each landed partway between where it belonged and that point: the hand rendered
    /// *shrunk* towards its own pivot rather than wrongly. Two small scraps of geometry.
    ///
    /// So the blend is taken out of the problem rather than worked around. The hand's triangles
    /// are cut out of the character's mesh once, baked into the hand bone's own space, and drawn
    /// as a rigid mesh that needs no skinning at all. See <see cref="HandMesh"/>.
    /// </summary>
    internal sealed class DetachedHands
    {
        private sealed class Hand
        {
            public Transform Root;   // carries the cut-out meshes, and is what we place
            public Mesh[] Meshes;

            /// <summary>
            /// The character's own hand and finger bones, paired with our copies of them. Every
            /// frame the copies take the originals' local rotations, so the fingers do whatever
            /// the game's animation is doing while the hand sits on the controller.
            /// </summary>
            public Transform[] SourceBones;
            public Transform[] CopiedBones;

            /// <summary>
            /// The copy of the hand bone itself. Excluded from the per-frame pose copy: it is the
            /// anchor the controller places, not something the animation gets to move.
            /// </summary>
            public Transform CopyRoot;

            /// <summary>
            /// Props that were parented to the real hand and have been moved onto ours, with
            /// enough remembered to put them back — and to hold them where they were taken
            /// from, which is a separate problem; see <see cref="HoldAttachments"/>.
            /// </summary>
            public Transform[] Attachments;
            public Transform[] AttachmentParents;

            /// <summary>
            /// The steadied pose each prop is being held at, carried frame to frame. Not a
            /// snapshot: it chases the animated pose, slowly. See
            /// <see cref="SteadyAttachments"/>.
            /// </summary>
            public Vector3[] AttachmentPositions;
            public Quaternion[] AttachmentRotations;
        }

        private Transform _holder;
        private Hand _left, _right;

        private Transform _leftUpper, _rightUpper;
        private Vector3 _leftUpperScale = Vector3.one, _rightUpperScale = Vector3.one;

        private bool _attached;
        public bool Attached => _attached;

        private bool _shown = true;

        public void Attach(Transform leftUpper, Transform leftHand,
                           Transform rightUpper, Transform rightHand)
        {
            if (_attached) return;

            if (_holder == null)
            {
                var holder = new GameObject("NobetaVR Hands");
                Object.DontDestroyOnLoad(holder);
                holder.hideFlags = HideFlags.HideAndDontSave;
                _holder = holder.transform;
            }

            // The holder outlives any one pair of hands, and it may have been left hidden
            // by the last one. A rebuild under an inactive parent draws nothing at all.
            _holder.gameObject.SetActive(true);
            _shown = true;

            _leftUpper = leftUpper;
            _rightUpper = rightUpper;
            if (_leftUpper != null) _leftUpperScale = _leftUpper.localScale;
            if (_rightUpper != null) _rightUpperScale = _rightUpper.localScale;

            _left = Build(leftHand, "left");
            _right = Build(rightHand, "right");

            if (Plugin.Instance.CarryHandAttachments.Value)
            {
                CollectAttachments(_left, leftHand, "left");
                CollectAttachments(_right, rightHand, "right");
                MoveAttachments(_left, true);
                MoveAttachments(_right, true);
                ResyncAttachments(_left);
                ResyncAttachments(_right);
            }

            _attached = _left != null || _right != null;
            if (!_attached)
                Plugin.Log.LogWarning("Could not build detached hands; leaving the arms alone.");
        }

        /// <summary>
        /// Builds one hand out of every mesh that has some of it.
        ///
        /// Not the first renderer that lists the hand bone, which is what an earlier version
        /// took. A character's meshes commonly share one skeleton, so the body, the hair and the
        /// cape all carry the same bone array while only one of them holds any hand geometry —
        /// and the first in the list turned out not to be the body. It also lets a hand made of
        /// more than one mesh, skin and glove say, come out whole.
        /// </summary>
        private Hand Build(Transform handBone, string side)
        {
            if (handBone == null) return null;

            var root = handBone.root;
            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers == null || renderers.Length == 0)
            {
                Plugin.Log.LogWarning($"no skinned renderers at all under '{root.name}'");
                return null;
            }

            var wanted = handBone.GetInstanceID();

            var holder = new GameObject($"NobetaVR {side} hand");
            Object.DontDestroyOnLoad(holder);
            holder.hideFlags = HideFlags.HideAndDontSave;
            holder.transform.SetParent(_holder, false);

            // A copy of the hand and its fingers, kept outside the character so the arm can be
            // collapsed without taking the copy with it. It is posed from the originals each
            // frame rather than animated: the originals are still being animated by the game,
            // collapsed or not, because a zero scale does not stop a bone's local rotation.
            var copyRoot = Object.Instantiate(handBone.gameObject).transform;
            copyRoot.SetParent(holder.transform, false);
            copyRoot.localPosition = Vector3.zero;
            copyRoot.localRotation = Quaternion.identity;

            // The copy is meant to be a skeleton and nothing else. Instantiate duplicates the
            // whole subtree, so the wand hanging off the hand bone came along — and lifting that
            // duplicate out of the collapsed arm put a second wand in the scene, drawn by us.
            // Anything that renders is stripped; only the bones are wanted.
            StripRenderers(copyRoot);

            // The cut-out is skinned through bind poses expressed in the character's own scale,
            // so the bone that carries it has to match the real hand bone's world scale. This is
            // set here rather than on the holder: two transforms multiplying scales is one more
            // place for it to be applied twice or not at all.
            copyRoot.localScale = handBone.lossyScale;

            var originalTree = handBone.GetComponentsInChildren<Transform>(true);
            var copiedTree = copyRoot.GetComponentsInChildren<Transform>(true);
            copyRoot.name = $"{side} hand bones";

            var meshes = new System.Collections.Generic.List<Mesh>();
            var sourceBones = new System.Collections.Generic.List<Transform>();
            var copiedBones = new System.Collections.Generic.List<Transform>();

            for (var i = 0; i < renderers.Length; i++)
            {
                var source = renderers[i];
                if (source == null || source.sharedMesh == null) continue;
                if (!Uses(source, wanted)) continue;

                var cut = HandMesh.Build(source, handBone, side, Plugin.Instance.HandVertexWeight.Value);
                if (cut == null) continue;

                // The cut mesh is skinned to the character's bones; the renderer is given our
                // copies of exactly those, in the same order.
                var bones = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Transform>(cut.Bones.Length);
                var complete = true;

                for (var b = 0; b < cut.Bones.Length; b++)
                {
                    var copy = MatchInCopy(cut.Bones[b], originalTree, copiedTree);
                    if (copy == null) { complete = false; break; }

                    bones[b] = copy;
                    if (!sourceBones.Contains(cut.Bones[b]))
                    {
                        sourceBones.Add(cut.Bones[b]);
                        copiedBones.Add(copy);
                    }
                }

                if (!complete)
                {
                    Plugin.Log.LogWarning($"{side} hand: a bone of '{source.name}' has no copy; skipping it.");
                    Object.Destroy(cut.Mesh);
                    continue;
                }

                var part = new GameObject(source.name);
                part.hideFlags = HideFlags.HideAndDontSave;
                part.transform.SetParent(holder.transform, false);

                var renderer = part.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = cut.Mesh;
                renderer.sharedMaterials = source.sharedMaterials;
                renderer.bones = bones;
                renderer.rootBone = copyRoot;
                renderer.updateWhenOffscreen = true;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                meshes.Add(cut.Mesh);
            }

            if (meshes.Count == 0)
            {
                Plugin.Log.LogWarning($"no mesh under '{root.name}' holds any {side} hand geometry "
                                    + $"at {Plugin.Instance.HandVertexWeight.Value:P0} weight. "
                                    + "Lowering HandVertexWeight is the thing to try.");
                Object.Destroy(holder);
                return null;
            }

            return new Hand
            {
                // The bone is what gets placed. The holder only keeps these objects together.
                Root = copyRoot,
                CopyRoot = copyRoot,
                Meshes = meshes.ToArray(),
                SourceBones = sourceBones.ToArray(),
                CopiedBones = copiedBones.ToArray(),
            };
        }

        /// <summary>
        /// The copy of a given original bone, found by its position in the two identical trees.
        ///
        /// By structure rather than by name: names are not unique in a skeleton, Instantiate
        /// appends "(Clone)" to the root, and this method renames it anyway. An earlier version
        /// matched on names and quietly kept nothing.
        /// </summary>
        private static Transform MatchInCopy(
            Transform bone,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Transform> originals,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Transform> copies)
        {
            if (bone == null) return null;

            var wanted = bone.GetInstanceID();
            var count = Mathf.Min(originals.Length, copies.Length);

            for (var i = 0; i < count; i++)
            {
                var original = originals[i];
                if (original != null && original.GetInstanceID() == wanted) return copies[i];
            }
            return null;
        }

        /// <summary>
        /// Removes everything from a copied bone tree that draws something, leaving the bones.
        /// </summary>
        private static void StripRenderers(Transform copy)
        {
            var renderers = copy.GetComponentsInChildren<Renderer>(true);
            if (renderers == null) return;

            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null) continue;
                Object.Destroy(renderer.gameObject);
            }
        }

        private static bool Uses(SkinnedMeshRenderer renderer, int boneInstanceId)
        {
            var bones = renderer.bones;
            if (bones == null) return false;

            for (var b = 0; b < bones.Length; b++)
            {
                var bone = bones[b];
                if (bone != null && bone.GetInstanceID() == boneInstanceId) return true;
            }
            return false;
        }

        /// <summary>
        /// Notes whatever hangs off the real hand, so it can be moved onto ours and put
        /// back again.
        ///
        /// The wand is parented to the hand bone and is hidden and shown by the game as its
        /// animations call for it. It has to travel with the hand that is being drawn: left
        /// on the real hand it is collapsed into the shoulder and never appears, and left on
        /// ours it disappears the moment the game takes her back for a cutscene, which is
        /// exactly when a wand is being pointed at something.
        /// </summary>
        private static void CollectAttachments(Hand hand, Transform handBone, string side)
        {
            if (hand == null || handBone == null) return;

            var count = handBone.childCount;
            if (count == 0) return;

            hand.Attachments = new Transform[count];
            hand.AttachmentParents = new Transform[count];
            hand.AttachmentPositions = new Vector3[count];
            hand.AttachmentRotations = new Quaternion[count];

            // Collected before any reparenting: moving a child renumbers the ones after it.
            for (var i = 0; i < count; i++) hand.Attachments[i] = handBone.GetChild(i);

            for (var i = 0; i < count; i++)
            {
                var child = hand.Attachments[i];
                if (child == null) continue;

                // Props only — anything that draws something. The first version moved every
                // child, which meant the finger bones as well: taking those out of the skeleton
                // deformed the character's own hand while trying to build a copy of it. A bone
                // has no renderer under it, and a wand does.
                if (!HasRenderer(child))
                {
                    hand.Attachments[i] = null;
                    continue;
                }

                hand.AttachmentParents[i] = child.parent;

                Plugin.Log.LogInfo($"{side} hand carries '{child.name}'");
            }
        }

        /// <summary>
        /// Moves the collected props between the character's hand and ours.
        /// </summary>
        private static void MoveAttachments(Hand hand, bool toOurs)
        {
            if (hand == null || hand.Attachments == null) return;

            for (var i = 0; i < hand.Attachments.Length; i++)
            {
                var child = hand.Attachments[i];
                var parent = toOurs ? hand.Root : hand.AttachmentParents[i];
                if (child != null && parent != null) child.SetParent(parent, false);
            }
        }

        /// <summary>
        /// Steadies the props against the animation, without deciding where they belong.
        ///
        /// The wand is not an object hanging off the hand, it is a bone of the rig —
        /// `Bone_Weapon`, a child of the right hand — so the animator kicks it on every
        /// shot. On a monitor that recoil is a flourish behind a crosshair that does not
        /// move. In a headset the wand *is* the sight: the thing you line the shot up with
        /// swings out from under your hand each time you fire and settles somewhere you
        /// have to re-learn, and the next shot is guesswork.
        ///
        /// Damped rather than pinned, and that distinction is the whole of it. Pinning it
        /// to the pose it was taken in was the first attempt and put the wand somewhere it
        /// had never been: the animator goes on writing that bone even from outside the
        /// character — which is what the recoil was in the first place — so any one
        /// frame's pose is not the socket, it is just that frame. Chasing the animated
        /// pose slowly cannot make that mistake: it always converges on wherever the game
        /// wants the wand, and a recoil is over long before it arrives.
        ///
        /// It also costs nothing if the reparenting did break the animator's binding after
        /// all: the value read back is then the one we wrote, and chasing that holds still.
        ///
        /// Nothing is lost either way. The shot never came from the wand's transform — it
        /// comes from the controller — so this only stops the picture disagreeing with
        /// where the shot was always going.
        /// </summary>
        private static void SteadyAttachments(Hand hand, float t)
        {
            if (hand.Attachments == null) return;

            for (var i = 0; i < hand.Attachments.Length; i++)
            {
                var child = hand.Attachments[i];
                if (child == null) continue;

                // Read first: whatever is there now is the animator's word for this frame,
                // because it ran before any LateUpdate did.
                hand.AttachmentPositions[i] =
                    Vector3.Lerp(hand.AttachmentPositions[i], child.localPosition, t);
                hand.AttachmentRotations[i] =
                    Quaternion.Slerp(hand.AttachmentRotations[i], child.localRotation, t);

                child.localPosition = hand.AttachmentPositions[i];
                child.localRotation = hand.AttachmentRotations[i];
            }
        }

        /// <summary>
        /// Starts the steadied pose from where the prop actually is.
        ///
        /// Called whenever a prop changes hands, so that coming back from a cutscene does
        /// not begin with the wand sliding in from wherever it was left half a scene ago.
        /// </summary>
        private static void ResyncAttachments(Hand hand)
        {
            if (hand == null || hand.Attachments == null) return;

            for (var i = 0; i < hand.Attachments.Length; i++)
            {
                var child = hand.Attachments[i];
                if (child == null) continue;

                hand.AttachmentPositions[i] = child.localPosition;
                hand.AttachmentRotations[i] = child.localRotation;
            }
        }

        private static bool HasRenderer(Transform subtree)
        {
            var renderers = subtree.GetComponentsInChildren<Renderer>(true);
            return renderers != null && renderers.Length > 0;
        }

        /// <summary>
        /// Shows or hides the hands without taking them apart.
        ///
        /// Cutting a hand out of the character's mesh is not free, and the game takes her
        /// back constantly — every conversation, every menu, every door. Rebuilding on each
        /// return would put a hitch on all of them, so hiding is a scale and a deactivation:
        /// the arms get their own scale back, the wand goes back on the real hand, and the
        /// cut-out geometry waits offstage until she is yours to move again.
        /// </summary>
        public void SetShown(bool shown)
        {
            if (!_attached || _shown == shown) return;
            _shown = shown;

            if (shown)
            {
                MoveAttachments(_left, true);
                MoveAttachments(_right, true);
                ResyncAttachments(_left);
                ResyncAttachments(_right);
            }
            else
            {
                // The arms come back before the props do: a prop returned to a bone that is
                // still collapsed would be handed back at zero scale.
                if (_leftUpper != null) _leftUpper.localScale = _leftUpperScale;
                if (_rightUpper != null) _rightUpper.localScale = _rightUpperScale;

                MoveAttachments(_left, false);
                MoveAttachments(_right, false);
            }

            if (_holder != null) _holder.gameObject.SetActive(shown);
        }

        /// <summary>
        /// Gives the character back exactly what was taken: the arms' scale, and any prop that
        /// was moved. Everything else here is ours and is destroyed.
        /// </summary>
        public void Detach()
        {
            if (!_attached) return;

            if (_leftUpper != null) _leftUpper.localScale = _leftUpperScale;
            if (_rightUpper != null) _rightUpper.localScale = _rightUpperScale;

            Destroy(_left);
            Destroy(_right);
            _left = _right = null;
            _attached = false;
            _shown = true;
            if (_holder != null) _holder.gameObject.SetActive(true);
        }

        private static void Destroy(Hand hand)
        {
            if (hand == null) return;

            // Props first: they have to be back on the character before the object they hang
            // from is destroyed, or the wand is destroyed along with it.
            if (hand.Attachments != null)
            {
                for (var i = 0; i < hand.Attachments.Length; i++)
                {
                    var child = hand.Attachments[i];
                    var parent = hand.AttachmentParents[i];
                    if (child != null && parent != null) child.SetParent(parent, false);
                }
            }

            if (hand.Root != null) Object.Destroy(hand.Root.gameObject);

            if (hand.Meshes == null) return;
            foreach (var mesh in hand.Meshes)
                if (mesh != null) Object.Destroy(mesh);
        }

        /// <summary>
        /// Places one hand for this frame, and collapses that side's real arm in place.
        ///
        /// In place matters: scaling the upper arm to nothing pulls everything below it — the
        /// forearm, the hand, the fingers — to the upper arm's own origin, inside the shoulder.
        /// The only blend that can then distort is the one with the clavicle, which is right
        /// there, so there is nothing to stretch and nothing to see.
        ///
        /// The arm is only collapsed once there is a hand to put in its place. Doing it first
        /// meant that when the rebuild failed, the arms vanished and nothing replaced them.
        /// </summary>
        public void Place(bool left, Vector3 position, Quaternion rotation)
        {
            var hand = left ? _left : _right;
            if (hand == null) return;

            var upper = left ? _leftUpper : _rightUpper;
            if (upper != null) upper.localScale = Vector3.zero;

            // The fingers follow the game's animation. Local rotations are copied, not world
            // ones: the originals sit inside a collapsed arm, so their world transforms are
            // meaningless, while their local rotations are exactly what the animator wrote.
            //
            // The hand bone itself is skipped, and that exception is the whole difference between
            // a hand on your controller and one at an odd angle beside it. The palm is skinned to
            // that bone, so it is one of the bones copied — but its copy is also the anchor being
            // placed, and its local transform is measured relative to a forearm that is no longer
            // in the picture. Writing the animation onto it moved the anchor out from under the
            // hand every frame.
            if (hand.SourceBones != null)
            {
                for (var i = 0; i < hand.SourceBones.Length; i++)
                {
                    var from = hand.SourceBones[i];
                    var to = hand.CopiedBones[i];
                    if (from == null || to == null) continue;
                    if (ReferenceEquals(to, hand.CopyRoot)) continue;

                    to.localRotation = from.localRotation;
                    to.localPosition = from.localPosition;
                }
            }

            if (Plugin.Instance.HoldWandStill.Value)
            {
                // Frame-rate independent, as the HUD's follow is: the fraction remaining
                // after dt seconds rather than a fixed fraction per frame.
                var t = 1f - Mathf.Exp(-Plugin.Instance.WandFollowSpeed.Value * Time.deltaTime);
                SteadyAttachments(hand, t);
            }

            hand.Root.SetPositionAndRotation(position, rotation);
        }
    }
}
