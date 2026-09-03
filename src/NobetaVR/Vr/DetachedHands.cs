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
            /// Props that were parented to the real hand and have been moved onto ours, with
            /// enough remembered to put them back.
            /// </summary>
            public Transform[] Attachments;
            public Transform[] AttachmentParents;
        }

        private Transform _holder;
        private Hand _left, _right;

        private Transform _leftUpper, _rightUpper;
        private Vector3 _leftUpperScale = Vector3.one, _rightUpperScale = Vector3.one;

        private bool _attached;
        public bool Attached => _attached;

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

            _leftUpper = leftUpper;
            _rightUpper = rightUpper;
            if (_leftUpper != null) _leftUpperScale = _leftUpper.localScale;
            if (_rightUpper != null) _rightUpperScale = _rightUpper.localScale;

            _left = Build(leftHand, "left");
            _right = Build(rightHand, "right");

            if (Plugin.Instance.CarryHandAttachments.Value)
            {
                CarryAttachments(_left, leftHand, "left");
                CarryAttachments(_right, rightHand, "right");
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

            // The cut-outs live in the hand bone's own space, so what carries them has to match
            // that bone's world scale — not its local one, which silently drops any scale the
            // character's hierarchy applies and draws the hand at the wrong size.
            holder.transform.localScale = handBone.lossyScale;

            var meshes = new System.Collections.Generic.List<Mesh>();

            for (var i = 0; i < renderers.Length; i++)
            {
                var source = renderers[i];
                if (source == null || source.sharedMesh == null) continue;
                if (!Uses(source, wanted)) continue;

                var mesh = HandMesh.Build(source, handBone, side, Plugin.Instance.HandVertexWeight.Value);
                if (mesh == null) continue;

                var part = new GameObject(source.name);
                part.hideFlags = HideFlags.HideAndDontSave;
                part.transform.SetParent(holder.transform, false);

                part.AddComponent<MeshFilter>().sharedMesh = mesh;

                var renderer = part.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = source.sharedMaterials;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                meshes.Add(mesh);
            }

            if (meshes.Count == 0)
            {
                Plugin.Log.LogWarning($"no mesh under '{root.name}' holds any {side} hand geometry "
                                    + $"at {Plugin.Instance.HandVertexWeight.Value:P0} weight. "
                                    + "Lowering HandVertexWeight is the thing to try.");
                Object.Destroy(holder);
                return null;
            }

            return new Hand { Root = holder.transform, Meshes = meshes.ToArray() };
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
        /// Moves whatever hangs off the real hand onto ours.
        ///
        /// Off by default. The wand is parented to the hand bone and is hidden and shown by the
        /// game as its animations call for it; lifting it out of the collapsed arm makes it
        /// visible at times the game never intended, which is exactly what happened.
        /// </summary>
        private static void CarryAttachments(Hand hand, Transform handBone, string side)
        {
            if (hand == null || handBone == null) return;

            var count = handBone.childCount;
            if (count == 0) return;

            hand.Attachments = new Transform[count];
            hand.AttachmentParents = new Transform[count];

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
                child.SetParent(hand.Root, false);
                Plugin.Log.LogInfo($"{side} hand carries '{child.name}'");
            }
        }

        private static bool HasRenderer(Transform subtree)
        {
            var renderers = subtree.GetComponentsInChildren<Renderer>(true);
            return renderers != null && renderers.Length > 0;
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

            hand.Root.SetPositionAndRotation(position, rotation);
        }
    }
}
