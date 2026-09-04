using HarmonyLib;

namespace NobetaVR.Ui
{
    /// <summary>
    /// The moments a hidden widget is worth seeing again.
    ///
    /// Taken from the game rather than watched for. Every one of these is the call the game
    /// already makes when the thing changed — the soul count moving, an item being selected,
    /// a save statue opening — so the widget appears on the same frame the game itself
    /// decided there was something to say, and there is no value to poll and no change to
    /// miss between polls.
    ///
    /// All postfixes, all of them doing nothing but noting a time. Nothing here alters what
    /// the game does; if <see cref="GameHud"/> is not up yet, each one is a no-op.
    /// </summary>
    [HarmonyPatch]
    internal static class GameHudPatches
    {
        /// <summary>
        /// The count itself is compared rather than the call trusted. Nothing says this is
        /// only called on a change, and one called per frame with the same number would pin
        /// the counter on permanently — the one failure of this whole feature that would look
        /// like it simply did not work.
        /// </summary>
        private static float _souls = float.NaN;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(StageUIManager), nameof(StageUIManager.UpdateMoney))]
        private static void SoulsChanged(float moneyValue)
        {
            if (moneyValue == _souls) return;

            var first = float.IsNaN(_souls);
            _souls = moneyValue;

            // Not on the first reading: that one is the stage handing over its saved count,
            // not something the player did.
            if (!first) GameHud.FlashMoney();
        }

        /// <summary>
        /// The save statue. This is the one place the soul count is a number you are reading
        /// rather than one you are glancing at, so it stays up for the whole visit.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(StageUIManager), nameof(StageUIManager.AppearSavePointMenu))]
        private static void StatueOpened() => GameHud.SetPraying(true);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(StageUIManager), nameof(StageUIManager.OnSavePointMenuClosed))]
        private static void StatueClosed() => GameHud.SetPraying(false);

        /// <summary>Compared rather than trusted, for the reason given above <see cref="_souls"/>.</summary>
        private static int _slot = int.MinValue;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(StageUIManager), nameof(StageUIManager.UpdateItemSelectMove))]
        private static void ItemSelected(int iPos)
        {
            if (iPos == _slot) return;

            _slot = iPos;
            GameHud.FlashItems();
        }

        /// <summary>Picking something up changes what is in the bar, which is worth a look.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(StageUIManager), nameof(StageUIManager.UpdateItemSprite))]
        private static void ItemsChanged() => GameHud.FlashItems();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(StageUIManager), nameof(StageUIManager.UpdateItemSize))]
        private static void ItemCountChanged() => GameHud.FlashItems();

        /// <summary>
        /// The game's own "here is what this item does" prompt, which it puts up on the item
        /// bar. Showing the bar with it is the difference between a caption and a caption
        /// floating over nothing.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(StageUIManager), nameof(StageUIManager.UpdateInstructions))]
        private static void InstructionsShown() => GameHud.FlashItems();
    }
}
