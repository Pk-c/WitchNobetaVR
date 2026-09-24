using HarmonyLib;
using UnityEngine;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Keeps Nobeta's swing trail from being drawn at all.
    ///
    /// The trail is an <c>XWeaponTrail</c>, and it does not follow an object: it samples two
    /// transforms every frame — <c>PointStart</c> and <c>PointEnd</c> — and builds a ribbon
    /// between where they were and where they are. Those two live on the rig, placed for the
    /// attack animations, and the mod has moved the wand off the rig: the visible wand is on
    /// the detached hand, wherever your controller is, while the arm it came from is collapsed
    /// into the shoulder. So the ribbon was drawn correctly, along a line with no wand on it.
    ///
    /// <para>
    /// Putting it back on the wand was tried and abandoned. The mod knows the wand line
    /// exactly, so the two transforms were replaced with two of ours on that line, then made
    /// settable — an offset, two angles and a length — when the first placement came out wrong,
    /// then measured off the wand's own mesh when the length did. The instrumentation says all
    /// of that worked: the game never took the field back, all four trails lit up on our
    /// transforms, and the segment they were handed ran from the hand out along the wand. The
    /// ribbon was still not where it belonged, which puts the fault somewhere past the two
    /// points this ever had purchase on. Three headset cycles, on decoration.
    /// </para>
    ///
    /// It takes two things to hold it off, and each catches what the other cannot.
    ///
    /// <para>
    /// The <b>patches</b> are the calls that raise it deliberately:
    /// <c>WizardGirlManage.OpenWTrail</c> is what an attack animation event calls, and
    /// <c>PlayerEffectPlay.SetWTrailActive</c> is what that reaches. Both are hers by type, so
    /// nothing here can touch a boss's weapon trail — which a patch on <c>XWeaponTrail</c>
    /// itself would, that component being the generic one every weapon in the game uses.
    /// </para>
    ///
    /// <para>
    /// The <b>sweep</b> is for the trail that raises itself, which is what a costume change
    /// produces and what the patches alone could not hold: a skin swap destroys her effect
    /// objects and instantiates new ones, and a fresh <c>XWeaponTrail</c> comes up through its
    /// own <c>OnEnable</c> and <c>Start</c> with none of her calls being made. So her four are
    /// re-read whenever the set underneath changes, and the component is disabled — a disabled
    /// component has no <c>LateUpdate</c>, so nothing samples the two points and nothing is
    /// built — with its mesh object switched off behind it, so the last ribbon it built goes
    /// with it rather than hanging in the air at the shape it had.
    /// </para>
    /// </summary>
    [HarmonyPatch]
    internal static class WandTrailOff
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(WizardGirlManage), nameof(WizardGirlManage.OpenWTrail))]
        private static bool OpenWTrail() => false;

        /// <summary>Switching one off still goes through; only raising one is refused.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerEffectPlay), nameof(PlayerEffectPlay.SetWTrailActive))]
        private static bool SetWTrailActive(bool bActive) => !bActive;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerEffectPlay), nameof(PlayerEffectPlay.PlayFireWTrail))]
        private static bool PlayFireWTrail() => false;

        /// <summary>The <c>PlayerEffectPlay</c> the current set was read from, as a pointer.</summary>
        private static System.IntPtr _effect;

        private static XftWeapon.XWeaponTrail[] _trails;

        /// <summary>How often the set is re-read while it still looks live, in seconds.</summary>
        private const float Rescan = 0.5f;

        private static float _nextScan;

        /// <summary>
        /// Holds her trails down for this frame. Called with whatever character the camera has,
        /// every frame and before any gate: a ribbon nobody asked for is as unwelcome in a
        /// cutscene as in play, and the moment it is most likely to appear — a costume being put
        /// on — is a moment she is not the player's to move.
        /// </summary>
        internal static void Tick(WizardGirlManage girl)
        {
            var trails = Trails(girl);
            if (trails == null) return;

            for (var i = 0; i < trails.Length; i++)
            {
                var trail = trails[i];
                if (trail == null) continue;

                // Not before it has built itself. A disabled component gets no Start, so
                // disabling one early would leave it forever uninitialised -- and its snapshot
                // list is built in Init, so the game's own Deactivate would then clear a list
                // that does not exist. Waiting costs nothing: what draws a ribbon is LateUpdate
                // on an activated trail, and a trail cannot be activated before it is built.
                if (!trail.mInited) continue;

                if (trail.enabled) trail.enabled = false;

                var mesh = trail.mMeshObj;
                if (mesh != null && mesh.activeSelf) mesh.SetActive(false);
            }
        }

        /// <summary>
        /// Her four trails, re-read when the set underneath changes.
        ///
        /// Three things force a fresh read, and each catches a case the other two miss. A
        /// different <c>PlayerEffectPlay</c> is a different body. A destroyed component in the
        /// set is the effect objects having been rebuilt underneath us — which is exactly what a
        /// costume does, and it is the fault this class was rewritten for: holding the old
        /// components means finding every entry null and doing nothing to the new ones while
        /// believing the work was done. And a half-second timer covers whatever neither noticed,
        /// at one read every thirty frames rather than one a frame.
        ///
        /// Compared by pointer rather than by reference: this is a plain il2cpp object, not a
        /// Unity one, and the managed wrapper around it is not guaranteed to be one instance.
        /// </summary>
        private static XftWeapon.XWeaponTrail[] Trails(WizardGirlManage girl)
        {
            if (girl == null)
            {
                _trails = null;
                _effect = System.IntPtr.Zero;
                return null;
            }

            var effect = girl.GetEffect();
            var handle = effect != null ? effect.Pointer : System.IntPtr.Zero;

            if (handle == _effect && _trails != null && Live()
             && Time.unscaledTime < _nextScan) return _trails;

            _nextScan = Time.unscaledTime + Rescan;
            _effect = handle;

            if (effect == null) { _trails = null; return null; }

            var found = new System.Collections.Generic.List<XftWeapon.XWeaponTrail>(4);
            Add(found, effect.g_WTrail);
            Add(found, effect.g_WTrail02);
            Add(found, effect.g_WTrail03);
            Add(found, effect.g_WTrail04);

            _trails = found.ToArray();

            return _trails;
        }

        private static void Add(System.Collections.Generic.List<XftWeapon.XWeaponTrail> trails,
                                XftWeapon.XWeaponTrail trail)
        {
            if (trail != null) trails.Add(trail);
        }

        /// <summary>Whether every component in the set still exists.</summary>
        private static bool Live()
        {
            for (var i = 0; i < _trails.Length; i++)
                if (_trails[i] == null) return false;

            return true;
        }
    }
}
