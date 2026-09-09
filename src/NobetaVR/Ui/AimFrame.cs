using UnityEngine;

namespace NobetaVR.Ui
{
    /// <summary>
    /// Stops the game drawing its focus frame, which is a pair of sprites bolted to the camera.
    ///
    /// Holding focus puts a bracketed border around the picture — two sprite halves, left and
    /// right, on an object called <c>AimFrame</c> parented to <c>PlayerCamera</c>'s camera.
    /// That is a border drawn on the edge of a screen, and a headset has no edge to draw on:
    /// the frame is welded to the view a short distance in front of your eyes, so it hangs in
    /// the room at a fixed depth with the world carrying on past it, and turning your head
    /// takes it with you. It is the one piece of furniture that cannot be looked away from.
    ///
    /// <para>
    /// Worth saying what it is not, because the obvious guesses are all wrong and each one
    /// costs a headset test. It is not <c>UIAimingPoint</c> and not on a canvas at all, so
    /// nothing done to the game's interface reaches it: no <c>CanvasGroup</c>, no alpha, none
    /// of the machinery <see cref="GameHud"/> uses. <c>UIAimingPoint</c> does carry
    /// <c>aimFrameAlpha</c>, <c>aimFrameColor</c> and <c>UpdateAimFrameUI()</c>, which is
    /// almost certainly what drives these two sprites — but the sprites are scene objects and
    /// have to be reached as scene objects.
    /// </para>
    ///
    /// <para>
    /// Suppressed with <c>forceRenderingOff</c> rather than by clearing <c>enabled</c> or
    /// deactivating the object. That flag exists for exactly this: it stops the renderer
    /// drawing without touching the switch the game itself uses, so whatever turns the frame
    /// on and off with focus goes on doing it, undisturbed and unread. Clearing <c>enabled</c>
    /// would put us in a fight over one boolean with code we cannot see — and, worse, would
    /// leave us guessing on the way back out whether a renderer was off because we turned it
    /// off or because the game did.
    /// </para>
    /// </summary>
    internal static class AimFrame
    {
        /// <summary>The object's name in the camera's hierarchy.</summary>
        private const string ObjectName = "AimFrame";

        /// <summary>How often the frame is looked for again while it has not been found.</summary>
        private const float Every = 1f;

        private static Transform _camera;
        private static Transform _frame;
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Renderer> _parts;

        private static float _nextScan;

        /// <summary>Whether the frame is currently being held off, so it can be given back.</summary>
        private static bool _suppressed;

        /// <summary>
        /// Points this at a new camera. Called when the view binds one, because
        /// <c>PlayerCamera</c> is built fresh per stage and the frame goes with it — anything
        /// held from the last stage is a dead pointer, not a hit.
        /// </summary>
        internal static void Rebind(Transform camera)
        {
            _camera = camera;
            _frame = null;
            _parts = null;
            _suppressed = false;
            _nextScan = 0f;
        }

        internal static void Tick()
        {
            var wanted = Plugin.Instance.HideAimOverlay.Value;

            // Nothing to do and nothing to undo. Checked before the scan so that a player who
            // wants the frame never pays for looking for it.
            if (!wanted && !_suppressed) return;

            if (_parts == null && !Resolve()) return;

            for (var i = 0; i < _parts.Length; i++)
            {
                var part = _parts[i];
                if (part != null && part.forceRenderingOff != wanted) part.forceRenderingOff = wanted;
            }

            _suppressed = wanted;
        }

        /// <summary>
        /// Finds the frame under the bound camera, on a timer rather than once.
        ///
        /// The timer is here because the object need not exist at the moment the camera does:
        /// it was invisible until the first press of focus, and something invisible may equally
        /// be something not built yet. Retrying costs a walk of a handful of transforms a
        /// second, and only until it is found.
        /// </summary>
        private static bool Resolve()
        {
            if (_camera == null) return false;
            if (Time.unscaledTime < _nextScan) return false;
            _nextScan = Time.unscaledTime + Every;

            _frame = Search(_camera, 0);
            if (_frame == null) return false;

            // Taken including the inactive: the frame spends most of the game switched off, and
            // a search that only saw what was on screen would find it on no frame that matters.
            _parts = _frame.GetComponentsInChildren<Renderer>(true);

            Plugin.Log.LogInfo($"focus frame found: '{_frame.name}' with {_parts.Length} renderer(s)");
            return _parts.Length > 0;
        }

        /// <summary>
        /// The named object anywhere under the camera. Depth-limited, because a diagnostic that
        /// can hang on a cycle in a hierarchy is worse than one that occasionally misses.
        /// </summary>
        private static Transform Search(Transform root, int depth)
        {
            if (root.name == ObjectName) return root;
            if (depth >= 6) return null;

            for (var i = 0; i < root.childCount; i++)
            {
                var hit = Search(root.GetChild(i), depth + 1);
                if (hit != null) return hit;
            }

            return null;
        }
    }
}
