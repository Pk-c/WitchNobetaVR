using HarmonyLib;

namespace NobetaVR.Ui
{
    /// <summary>
    /// How far through a load the game believes it is.
    ///
    /// Taken from the call that draws the number rather than polled from anywhere: the loading
    /// screen is told its own progress, once per update, and that call is both the earliest and
    /// the most honest place the figure exists. Reading it here means the mod's idea of "nearly
    /// loaded" is the same figure the player is watching tick up, to the frame.
    ///
    /// <para>
    /// A postfix that does nothing but remember a number. Nothing about the game's own loading
    /// changes, and if this patch fails to apply the value simply stays at zero, which every
    /// reader treats as "no load in progress".
    /// </para>
    /// </summary>
    [HarmonyPatch]
    internal static class LoadingProgress
    {
        /// <summary>The last progress the loading screen was given, 0 to 1.</summary>
        internal static float Value { get; private set; }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UISceneLoading), nameof(UISceneLoading.UpdateProgress))]
        private static void Progressed(float progress) => Value = progress;
    }
}
