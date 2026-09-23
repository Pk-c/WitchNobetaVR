using HarmonyLib;
using UnityEngine;

namespace NobetaVR.Ui
{
    /// <summary>
    /// The two places the game announces a piece of interface that belongs on a thing in the
    /// world rather than on a screen.
    ///
    /// Both are taken from the game rather than watched for, which is the same argument
    /// <see cref="GameHudPatches"/> makes: these are the calls the game already makes at the
    /// moment it decided there was something to show, with the position and the value it decided
    /// them with. There is nothing to poll and nothing to miss between polls.
    ///
    /// Postfixes, save one guard. Nothing here changes what the game does — the flat versions go
    /// on being built, moved and updated exactly as they were, and are faded out separately by
    /// the components below, so every one of these choices is reversible at runtime. The
    /// exception is <see cref="SkipDeadEnemyBar"/>, which only stops the game's bar updater from
    /// running in the two states where it would throw.
    /// </summary>
    [HarmonyPatch]
    internal static class WorldUiPatches
    {
        /// <summary>
        /// A hit landed for <paramref name="iHitNumber"/> damage, at <paramref name="v3Pos"/>.
        ///
        /// Patched on <c>UIHitNumber</c> rather than on <c>StageUIManager.SetHitNumber</c>, which
        /// is the wrapper one level above it: the wrapper is what the game's own combat code calls
        /// today, but this is where the number actually is, so anything that reaches the pool by
        /// another route is caught here too.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIHitNumber), nameof(UIHitNumber.SetHitNumber))]
        private static void HitLanded(int iHitNumber, Vector3 v3Pos, PlayerEffectPlay.Magic iElement)
            => DamageNumbers.Hit(iHitNumber, v3Pos, iElement);

        /// <summary>An enemy was registered for a health bar, with the anchor to hang it on.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIEnemyHp), nameof(UIEnemyHp.AddEnemyHPBar))]
        private static void EnemyRegistered(EnemiesManager.EnemyData data)
            => EnemyHealthBars.Add(data);

        /// <summary>
        /// Keeps the game's bar updater from running where it would throw.
        ///
        /// <c>UpdateInformation</c> reads <c>data.HPPosition.position</c> through
        /// <c>Game.GetStageCamera()</c>, and two states break it: an enemy destroyed while its
        /// bar is still up leaves the anchor dead, and a scene with no stage loaded has no
        /// <c>Game.sceneManager</c> for the camera lookup to go through. Once the method is
        /// patched, Il2CppInterop's trampoline catches the exception instead of letting it reach
        /// the game's coroutine, so the updater is never recycled and throws again on every tick:
        /// tens of thousands of <c>NullReferenceException</c>s in the log over one session.
        ///
        /// Skipping the original in those states leaves such an updater parked, and its flat bar
        /// is hidden so it does not stay frozen on screen.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(UIEnemyHPUpdater), nameof(UIEnemyHPUpdater.UpdateInformation))]
        private static bool SkipDeadEnemyBar(UIEnemyHPUpdater __instance)
        {
            var data = __instance.data;
            if (data != null && data.HPPosition != null && Game.sceneManager != null) return true;

            var bar = __instance.hpBarUI;
            var group = bar != null ? bar.canvasGroup : null;
            if (group != null) group.alpha = 0f;

            return false;
        }

        /// <summary>
        /// Holds the game's own enemy bar out of the way while ours is on.
        ///
        /// Done here rather than with a <c>CanvasGroup</c> like the rest of the HUD, because there
        /// is no one object to put a group on: the bars come out of a pool and are re-parented as
        /// they are lent out, and the only object above all of them is the one carrying the boss
        /// banner too — which is a screen element, arrives on the panel correctly, and must not go
        /// with them. This is the one call the game makes per bar per frame, so writing the alpha
        /// after it is both the narrowest place to do it and the last word on the frame.
        ///
        /// The game's own value is put back the moment the setting is off, rather than left where
        /// this last drove it.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIEnemyHPUpdater), nameof(UIEnemyHPUpdater.UpdateInformation))]
        private static void FlatEnemyBar(UIEnemyHPUpdater __instance)
        {
            if (!Plugin.Instance.WorldEnemyHealthBars.Value) return;

            var bar = __instance.hpBarUI;
            var group = bar != null ? bar.canvasGroup : null;
            if (group == null) return;

            group.alpha = 0f;
        }
    }
}
