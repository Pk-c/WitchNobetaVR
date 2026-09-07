using Il2CppInterop.Runtime;
using UnityEngine;
using XftWeapon;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Puts the game's wand trail on the wand.
    ///
    /// The trail is an <c>XWeaponTrail</c>: it does not follow an object, it samples two
    /// transforms every frame — <c>PointStart</c> and <c>PointEnd</c> — and builds a ribbon
    /// between where they were and where they are. Those two live on the rig, placed for the
    /// attack animations, and the mod has moved the wand off the rig: the visible wand is on
    /// the detached hand, wherever your controller is, while the arm it came from is collapsed
    /// into the shoulder. So the ribbon was drawn correctly, along a line that no longer had a
    /// wand on it.
    ///
    /// Rather than chase the points around the skeleton, they are replaced. The mod already
    /// knows the wand line exactly — it is the same origin and direction the shot goes down and
    /// the melee hitbox rides, so this cannot disagree with either by construction. Two
    /// transforms of ours are put on that line and handed to the trail, and the rig's own are
    /// handed back the moment the hands are.
    ///
    /// The far point sits at <c>MeleeHitboxReach</c> rather than at some length of its own,
    /// which makes the ribbon the path the blow actually swept. That is a better thing for it
    /// to be than decoration: on a ground swing there is no animation, so the trail is the only
    /// thing on screen that says where the wand went.
    /// </summary>
    internal sealed class WandTrail
    {
        private Transform _boundRoot;
        private XWeaponTrail[] _trails;
        private Transform[] _originalStart;
        private Transform[] _originalEnd;

        private Transform _start;
        private Transform _end;
        private bool _retargeted;

        /// <summary>
        /// Drives the trail along the wand for this frame, taking it over the first time.
        /// </summary>
        public void Follow(Transform root, Vector3 origin, Vector3 direction, float fallbackReach)
        {
            if (!Bind(root)) return;

            EnsurePoints();

            var length = TipDistance(origin, direction, fallbackReach);

            // A hair in front of the hand rather than at it, so the ribbon starts on the wand
            // and not inside the fist holding it, and out to the far end of the wand itself.
            _start.position = origin + direction * 0.05f;
            _end.position = origin + direction * length;

            if (_retargeted) return;
            _retargeted = true;

            for (var i = 0; i < _trails.Length; i++)
            {
                var trail = _trails[i];
                if (trail == null) continue;

                _originalStart[i] = trail.PointStart;
                _originalEnd[i] = trail.PointEnd;

                trail.PointStart = _start;
                trail.PointEnd = _end;
            }

            Plugin.Log.LogInfo($"wand trail: {_trails.Length} trail(s) moved onto the wand");
        }

        /// <summary>
        /// Gives the rig its own points back. Called whenever the hands stop being drawn, so a
        /// cutscene's animated swing trails along the animated wand as it always did.
        /// </summary>
        public void Release()
        {
            if (!_retargeted || _trails == null) { _retargeted = false; return; }
            _retargeted = false;

            for (var i = 0; i < _trails.Length; i++)
            {
                var trail = _trails[i];
                if (trail == null) continue;

                trail.PointStart = _originalStart[i];
                trail.PointEnd = _originalEnd[i];
            }
        }

        /// <summary>
        /// How far the wand actually reaches, in metres along the aim line.
        ///
        /// Measured off the wand rather than set, for the same reason the eye offsets are
        /// numbers in the source and not settings: how long a wand is, is a fact about the
        /// model. The first version ended the ribbon at <c>MeleeHitboxReach</c> — the hitbox is
        /// on the same line, so it seemed like the same answer — and it is not: the hitbox sits
        /// where a blow is dealt, which is well short of the tip, so the ribbon came out as a
        /// stub hanging off the hand.
        ///
        /// The renderers under the wand are asked for their world bounds and the far corner
        /// along the aim line wins. A world AABB overshoots a rotated object, but the wand is
        /// thin and pointed along this very line, so the overshoot is small and lands past the
        /// tip rather than short of it, which is the right way round for a trail.
        ///
        /// The *bounds* are read every frame, because they move with the hand and cost nothing
        /// to ask for. Which renderers to ask is the part that is cached: walking the prop's
        /// hierarchy is a fresh il2cpp array and a fresh managed wrapper per renderer, every
        /// frame, for an answer that only changes when the wand itself does. See
        /// <see cref="Renderers"/> for what still notices that change.
        /// </summary>
        private float TipDistance(Vector3 origin, Vector3 direction, float fallback)
        {
            var renderers = Renderers();
            if (renderers == null) return Fallback(fallback);

            var farthest = 0f;

            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null || !renderer.enabled) continue;

                var bounds = renderer.bounds;
                var centre = bounds.center - origin;
                var extents = bounds.extents;

                // The corner of the box that reaches furthest along the line, without
                // walking all eight: the extent projects as the sum of its absolute parts.
                var reach = Vector3.Dot(centre, direction)
                          + Mathf.Abs(direction.x) * extents.x
                          + Mathf.Abs(direction.y) * extents.y
                          + Mathf.Abs(direction.z) * extents.z;

                if (reach > farthest) farthest = reach;
            }

            if (farthest <= 0.1f) return Fallback(fallback);

            Report(farthest);
            return farthest;
        }

        private Renderer[] _renderers;
        private Transform[] _renderersFrom;
        private float _nextRendererScan;

        /// <summary>How long a collected set of renderers is trusted for, in seconds.</summary>
        private const float RendererScan = 0.25f;

        /// <summary>
        /// The renderers under the wand, collected rather than re-walked.
        ///
        /// Three things force a fresh walk, and each catches a case the other two miss. A
        /// different props array is a different hand or a different body. A destroyed renderer
        /// in the set is the prop having been swapped underneath us. And a quarter-second timer
        /// covers the rest — a magic element changing, a cutscene prop, a different skin —
        /// which is the invalidation the per-frame walk used to buy, at a hundredth of the
        /// price. A quarter of a second of the ribbon ending where the last wand ended is not
        /// something the eye has any way to catch.
        /// </summary>
        private Renderer[] Renderers()
        {
            var props = VrHands.WandProps;
            if (props == null) { _renderers = null; _renderersFrom = null; return null; }

            if (!ReferenceEquals(props, _renderersFrom)
             || _renderers == null
             || Time.unscaledTime >= _nextRendererScan
             || Stale())
            {
                _renderersFrom = props;
                _nextRendererScan = Time.unscaledTime + RendererScan;
                _renderers = Collect(props);
            }

            return _renderers;
        }

        /// <summary>Whether anything in the collected set has been destroyed since.</summary>
        private bool Stale()
        {
            for (var i = 0; i < _renderers.Length; i++)
                if (_renderers[i] == null) return true;
            return false;
        }

        private static Renderer[] Collect(Transform[] props)
        {
            var found = new System.Collections.Generic.List<Renderer>();

            foreach (var prop in props)
            {
                if (prop == null) continue;

                var under = prop.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
                for (var i = 0; i < under.Length; i++)
                {
                    var renderer = under[i] != null ? under[i].TryCast<Renderer>() : null;
                    if (renderer != null) found.Add(renderer);
                }
            }

            return found.ToArray();
        }

        private static float Fallback(float reach) => Mathf.Max(0.1f, reach);

        private float _reported = -1f;

        /// <summary>
        /// Says the measured length once, and again only if it moves by a centimetre, so a
        /// trail that comes out the wrong length can be told from one that was never measured.
        /// </summary>
        private void Report(float length)
        {
            if (_reported > 0f && Mathf.Abs(length - _reported) < 0.01f) return;
            _reported = length;

            Plugin.Log.LogInfo($"wand trail: the wand reaches {length:F2} m from the hand");
        }

        /// <summary>
        /// Collects the character's trails, once per body.
        ///
        /// Found on the skeleton rather than through <c>PlayerEffectPlay</c>, which holds the
        /// same four in named fields but is not reachable from anything the mod already has.
        /// Searching for the component answers the question directly and does not care how many
        /// there turn out to be — the build carries four, one per element the wand can be.
        /// </summary>
        private bool Bind(Transform root)
        {
            if (root == null) return false;
            if (ReferenceEquals(root, _boundRoot) && Live()) return true;

            Release();
            _boundRoot = root;

            var found = root.GetComponentsInChildren(Il2CppType.Of<XWeaponTrail>(), true);
            var trails = new System.Collections.Generic.List<XWeaponTrail>();

            for (var i = 0; i < found.Length; i++)
            {
                var trail = found[i] != null ? found[i].TryCast<XWeaponTrail>() : null;
                if (trail != null) trails.Add(trail);
            }

            _trails = trails.ToArray();
            _originalStart = new Transform[_trails.Length];
            _originalEnd = new Transform[_trails.Length];

            if (_trails.Length == 0)
                Plugin.Log.LogWarning("wand trail: no XWeaponTrail on this character; the swing "
                                    + "trail will stay wherever the rig puts it.");

            return _trails.Length > 0;
        }

        /// <summary>
        /// Whether the trails we are holding still exist.
        ///
        /// The root alone is not enough to answer that. <c>girl.transform</c> outlives a skin
        /// change, so a costume swap — or the story skin a cutscene puts on and takes off
        /// again — leaves the root identical while the effect objects beneath it, trails
        /// included, are destroyed and rebuilt. The cached array then holds nothing but
        /// destroyed components, and every loop over it skips every entry: <c>Release</c> put
        /// nothing back, the retarget put nothing anywhere, and <c>_retargeted</c> stayed true
        /// so it never tried again. The new trails were left on the rig's own points, drawing
        /// a ribbon along a collapsed arm — the trail in the wrong place after a cutscene.
        ///
        /// The same hole the detached hands had, one object over, and the same fix: notice
        /// that what was bound is gone rather than trusting that the thing it hung off is the
        /// same object.
        /// </summary>
        private bool Live()
        {
            if (_trails == null || _trails.Length == 0) return false;

            foreach (var trail in _trails)
                if (trail == null) return false;

            return true;
        }

        /// <summary>
        /// The two transforms handed to the trail. Kept out of the character's hierarchy and
        /// placed in world space each frame, so nothing about them can be disturbed by the rig
        /// being rebuilt, an animation, or the hands being taken away and given back.
        /// </summary>
        private void EnsurePoints()
        {
            if (_start != null && _end != null) return;

            _start = Make("NobetaVR WandTrailStart");
            _end = Make("NobetaVR WandTrailEnd");
        }

        private static Transform Make(string name)
        {
            var go = new GameObject(name);
            Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            return go.transform;
        }
    }
}
