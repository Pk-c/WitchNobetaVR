using System.Collections.Generic;
using UnityEngine;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Takes the whole of Nobeta out of the picture while the view is inside her, for players
    /// who want nothing of her body between them and the world.
    ///
    /// The head has its own answer and keeps it: it is hidden by distance, because a head is a
    /// problem exactly while your eyes are within the mesh and is wanted the moment they are
    /// not — see <see cref="FirstPerson.UpdateHeadVisibility"/>. The rest of her is not that
    /// shape of question. A shoulder, a skirt and a cape are never inside your eyes; they are
    /// simply there, below the view, doing what the animation says rather than what your body
    /// is doing. Whether that reads as inhabiting her or as wearing someone else is a taste, so
    /// this is a switch, and it is off by default.
    ///
    /// <para>
    /// Tied to the view being in her head rather than to a cutscene test of its own, which is
    /// the rule the hands already answer to. By default a cutscene is exactly the moment the
    /// view steps back out of her — see <c>VrCamera.ViewStandsBack</c> — so she is whole again
    /// for every shot the game frames, which is what the setting is for. With that stepping
    /// back turned off the view is still inside her for the length of the scene, and a body
    /// handed back there would be a body you are standing in the middle of.
    /// </para>
    ///
    /// <para>
    /// Everything skinned goes and nothing else does. Her body, face, hair, cape, hat and bag
    /// are skinned meshes on her skeleton; the wand is a plain <c>MeshRenderer</c> — the game
    /// keeps it as <c>NobetaSkin.weaponMesh</c>, apart from the <c>bodyMesh</c> array — and so
    /// is everything she casts. So the distinction the game already draws is the one worth
    /// taking, and it leaves the wand in the air where the controller is holding it, which is
    /// where the shot comes from and the last thing anyone would want hidden.
    /// </para>
    ///
    /// <para>
    /// Suppressed with <c>forceRenderingOff</c>, for the reason <see cref="Ui.AimFrame"/> gives
    /// at length: the game raises and lowers these renderers itself — a costume change, a
    /// stealth state, <c>NobetaMeshController.UpdateAppearance</c> — and that switch is its own
    /// to own. This one is ours, nothing else reads it, and on the way back out there is no
    /// guessing whether a renderer was off because we turned it off or because the game did.
    /// </para>
    /// </summary>
    internal static class BodyVisibility
    {
        /// <summary>How long a collected set of renderers is trusted for, in seconds.</summary>
        private const float Rescan = 0.5f;

        private static PlayerCamera _camera;
        private static Renderer[] _parts;

        /// <summary>The character root the current set was collected under.</summary>
        private static int _fromId;

        private static float _nextScan;
        private static bool _suppressed;
        private static bool _reported;

        /// <summary>
        /// Points this at a new <c>PlayerCamera</c>. Called when the view binds one, because a
        /// new stage is a new body: anything held from the last one is a dead pointer.
        /// </summary>
        internal static void Rebind(PlayerCamera camera)
        {
            Show();
            _camera = camera;
            _parts = null;
            _fromId = 0;
            _nextScan = 0f;
            _reported = false;
        }

        /// <summary>
        /// Hides her or shows her, for this frame.
        /// </summary>
        /// <param name="viewInHerHead">
        /// Whether first person is driving the view this frame. False while the game frames a
        /// shot or a death from its own camera, and on the opening frames of a stage before her
        /// head bone has loaded.
        /// </param>
        internal static void Tick(bool viewInHerHead)
        {
            var wanted = Plugin.Instance.HideBody.Value && viewInHerHead;

            // Nothing to do and nothing to undo. Checked before the scan, so a player who never
            // turns this on never pays for walking her skeleton.
            if (!wanted && !_suppressed) return;

            var parts = Parts();
            if (parts == null) return;

            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part != null && part.forceRenderingOff != wanted) part.forceRenderingOff = wanted;
            }

            _suppressed = wanted;
        }

        /// <summary>
        /// The skinned meshes she is made of, collected rather than re-walked.
        ///
        /// Three things force a fresh walk, the same three <see cref="WandTrail"/> answers to. A
        /// different character root is a different body. A destroyed renderer in the set is the
        /// skin having been swapped underneath us — a costume and the story outfit both do that,
        /// and both keep the root they hang from. And a timer covers whatever neither noticed,
        /// at one walk every half second instead of one per frame.
        /// </summary>
        private static Renderer[] Parts()
        {
            var girl = _camera != null ? _camera.wizardGirl : null;
            if (girl == null)
            {
                // Between stages there is nobody to hand anything back to, and the renderers are
                // on their way out with her. Forget them rather than write to them, and let the
                // next body be collected fresh.
                _parts = null;
                _fromId = 0;
                _suppressed = false;
                return null;
            }

            var root = girl.transform;
            var id = root.GetInstanceID();

            if (_parts != null && id == _fromId && Time.unscaledTime < _nextScan && !Stale())
                return _parts;

            // A different body while this one is still held off: give the old one back before
            // its renderers are dropped, or it goes on invisible with nothing left holding it.
            if (id != _fromId) Show();

            _nextScan = Time.unscaledTime + Rescan;
            _fromId = id;
            _parts = Collect(root);
            return _parts;
        }

        /// <summary>Whether anything in the collected set has been destroyed since.</summary>
        private static bool Stale()
        {
            for (var i = 0; i < _parts.Length; i++)
                if (_parts[i] == null) return true;
            return false;
        }

        private static Renderer[] Collect(Transform root)
        {
            var found = new List<Renderer>();

            // Including the inactive: a piece of her outfit the game has switched off is still a
            // piece it can switch back on, and one collected while it was off is one that does
            // not reappear out of nowhere when it does.
            var under = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var i = 0; i < under.Length; i++)
            {
                var renderer = under[i];
                if (renderer != null) found.Add(renderer);
            }

            var parts = found.ToArray();

            if (!_reported)
            {
                _reported = true;
                Plugin.Log.LogInfo($"body hiding: {parts.Length} skinned mesh(es) under "
                                 + $"'{root.name}'{Names(parts)}");
            }

            return parts;
        }

        /// <summary>
        /// Names what was found, once per body. Which meshes a character is made of is a fact
        /// about the model, and the only way to learn that a costume brought a piece this does
        /// not reach is to have the set written down.
        /// </summary>
        private static string Names(Renderer[] parts)
        {
            if (parts.Length == 0) return string.Empty;

            var names = new List<string>(parts.Length);
            for (var i = 0; i < parts.Length; i++) names.Add(parts[i].name);
            return ": " + string.Join(", ", names);
        }

        /// <summary>Gives the current set its rendering back, if we were the ones holding it off.</summary>
        private static void Show()
        {
            if (!_suppressed) return;
            _suppressed = false;

            if (_parts == null) return;

            for (var i = 0; i < _parts.Length; i++)
            {
                var part = _parts[i];
                if (part != null) part.forceRenderingOff = false;
            }
        }
    }
}
