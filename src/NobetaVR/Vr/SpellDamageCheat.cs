using System.Collections.Generic;
using HarmonyLib;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Multiplies the damage her spells deal, for testing. Off at its default of one.
    ///
    /// Worked from the same funnel as <see cref="MeleeBalance"/>: every blow on every enemy
    /// arrives at <c>NPCManage.Hit(AttackData)</c>, and a spell is told apart from a swing by
    /// <c>AttackData.g_AT2</c>, which the game itself labels <c>MAGIC</c> or <c>PHUSICAL</c>.
    /// Not by the element: arcane magic carries the <c>Null</c> element a plain swing does, and
    /// the fire-levelled melee ranges carry <c>Fire</c>.
    ///
    /// The strength is raised for the one call and put back after it, for the reason
    /// <see cref="MeleeBalance"/> puts its fields back: an <c>AttackData</c> is one component
    /// shared by every blow its range will ever deal, so a value left raised would keep the
    /// spell boosted after the setting was turned back down.
    /// </summary>
    [HarmonyPatch]
    internal static class SpellDamageCheat
    {
        internal const float MinMultiplier = 1f;
        internal const float MaxMultiplier = 20f;

        private struct Boosted
        {
            internal AttackData Data;
            internal float Strength;
            internal float SecondStrength;
        }

        /// <summary>Attack names already reported, so the log says which ranges count as
        /// spells once each instead of on every hit.</summary>
        private static readonly HashSet<string> Reported = new();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NPCManage), nameof(NPCManage.Hit))]
        private static void BeforeHit(AttackData Data, out Boosted __state)
        {
            __state = default;

            var cfg = Plugin.Instance;
            if (cfg == null || Data == null) return;

            var multiplier = UnityEngine.Mathf.Clamp(
                cfg.SpellDamageMultiplier.Value, MinMultiplier, MaxMultiplier);
            if (multiplier <= MinMultiplier) return;
            if (Data.g_AT2 != AttackData.AttackType2.MAGIC) return;

            __state.Data = Data;
            __state.Strength = Data.g_fStrength;
            __state.SecondStrength = Data.g_fSecondStrength;

            Data.g_fStrength *= multiplier;
            Data.g_fSecondStrength *= multiplier;

            var name = Data.name;
            if (Reported.Add(name))
                Plugin.Log.LogInfo($"[cheat] spell damage ×{multiplier:F1} applied to '{name}' "
                                 + $"({__state.Strength:F1} -> {Data.g_fStrength:F1})");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(NPCManage), nameof(NPCManage.Hit))]
        private static void AfterHit(Boosted __state)
        {
            var data = __state.Data;
            if (data == null) return;

            data.g_fStrength = __state.Strength;
            data.g_fSecondStrength = __state.SecondStrength;
        }
    }
}
