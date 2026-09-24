using HarmonyLib;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Catches the moment the game starts standing her up at a save statue, which is the one
    /// thing watching her state per frame cannot do in time.
    ///
    /// A stage that opens on a save statue does not open in the wake: the log has her in
    /// <c>Normal</c>, with the game's own controllable flag already true, for the whole of the
    /// load and for some seconds after it, and <c>Resurrection</c> arrives only once the stage
    /// is standing. So every reading the mod has of "the game has her" says she is the player's
    /// during exactly the stretch <see cref="VrCamera.SpawnView"/> was meant to cover, and a
    /// latch closed on those readings closes before the sequence it exists for has begun —
    /// measured at 0.0 seconds, which is what "the camera is stuck in her head at spawn" was.
    ///
    /// <para>
    /// Waiting a fixed while instead would be a guess in both directions: too short and it
    /// misses a slow load, too long and an ordinary stage transition — a door into the next
    /// area, where she spawns on her feet — spends it standing outside her head for no reason.
    /// The game already knows, and says so, on the frame it decides.
    /// </para>
    ///
    /// <c>Resurrection</c> and <c>Wake</c> only. <c>StandUp</c> is deliberately not here: it is
    /// also the end of a knockdown in a fight, and being pulled out of your own head mid-combat
    /// is its own kind of unpleasant. Both ways in are patched because a state can be entered
    /// as a transition or set as a stage's opening state, and which of the two a save statue
    /// uses is not worth depending on.
    /// </summary>
    [HarmonyPatch]
    internal static class WakeSequencePatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.SetStatus))]
        private static void StatusSet(NobetaState CharacterStatus) => Note(CharacterStatus);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.InitState))]
        private static void StateInitialised(NobetaState state) => Note(state);

        private static void Note(NobetaState state)
        {
            if (state != NobetaState.Resurrection && state != NobetaState.Wake) return;

            VrCamera.ArmSpawnView();
        }
    }
}
