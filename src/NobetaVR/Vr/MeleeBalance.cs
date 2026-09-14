using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Gives an enemy its turn back.
    ///
    /// The VR swing broke an assumption the game never wrote down. On a pad, melee is an
    /// animation: it plants her feet, swings the wand, and only then lets the next input
    /// through. Nothing in the game rate-limits a blow, because the animation already did —
    /// and several separate systems were calibrated behind it. <see cref="VrMelee"/> removed
    /// the animation, correctly and for good reasons, and they came loose at once.
    ///
    /// Two of them are handled here. Both are reached from the same place: every blow on every
    /// enemy in the game passes through <c>NPCManage.Hit(AttackData)</c>, monsters and bosses
    /// alike, so one pair of timestamps per enemy is the whole mechanism.
    ///
    /// <para>
    /// <b>The stagger.</b> <c>AI_NPC</c> has no poise, no armour and no accumulator — the whole
    /// of its interruption logic is <c>AttackData.g_fStiff</c> and <c>g_fRepulse</c> arriving
    /// and switching it into <c>AIStatus.Damaged</c>. At the pad's cadence that is a punish; at
    /// six or seven swings a second it is a lock, and an enemy that never leaves <c>Damaged</c>
    /// never attacks.
    /// </para>
    ///
    /// The rule is a budget on the enemy rather than a gate on the player's tempo, and the
    /// difference is the entire design. A rule of the form "no stagger if your last two swings
    /// were less than X apart" makes the outcome depend on how fast your arm happens to be
    /// moving, and an arm in combat is not a metronome: a player swinging naturally around the
    /// threshold would see some blows stagger and some not, with nothing on screen to say why.
    /// In a headset that reads as hits failing to register, which is the one thing this mod
    /// cannot afford to look like.
    ///
    /// <para>
    /// So it is counted instead. A flurry of <c>StaggerBudget</c> blows staggers; everything
    /// after that in the same flurry lands without interrupting, however fast or slow it comes.
    /// The count only clears once the enemy has been left alone for <c>StaggerRecovery</c> — so
    /// a flurry cannot be extended indefinitely, and backing off is the one thing that restores
    /// it. Both halves state the goal directly: the first four blows still feel like a combo,
    /// and after them the enemy is on its feet until you choose to stop hitting it.
    /// </para>
    ///
    /// <para>
    /// <b>The hit-stop.</b> <c>AttackData.g_bPauseTime</c> reaches
    /// <c>NPCManage.SetPauseTime(time, scale)</c>, which drives <c>AI_NPC.SetTimeScale</c> and
    /// through it the delta time the enemy's whole AI runs on. It is deliberately asymmetric —
    /// the enemy is slowed and Nobeta is not — and at one blow every half second that is the
    /// punch of the impact. At six a second the next pause begins before the last has ended and
    /// it stops being a series of accents: the enemies simply live in slow motion while the
    /// player does not.
    /// </para>
    ///
    /// Hit-stop is feedback rather than balance, so its rule is the other one: a plain interval
    /// on the blows themselves, and a short one. It only ever needed to stop overlapping
    /// itself. The default is the game's own <c>g_fCollisionInterval</c>, 0.30 s — the shortest
    /// gap it allows between two hits on one target — so almost every deliberately thrown swing
    /// keeps its impact and only a flurry loses it.
    ///
    /// <para>
    /// <b>Everything that hits an enemy is subject to both.</b> Not only the VR swing: magic
    /// too, and the pad's own melee. That is the point of putting the rule on the enemy — were
    /// it melee-only, alternating a swing with a shot would walk straight through it. It also
    /// means these settings change the game outside VR melee, which is why each of them turns
    /// off by being set to zero.
    /// </para>
    ///
    /// Two things are knowingly left alone. Damage is untouched: the swing still opens
    /// <c>Attack01_01Range</c>, the cheapest step of the combo, so the VR player trades the
    /// combo's escalation for its cadence and lands nearer the game's own figure than the
    /// swing rate suggests. And <c>WizardGirlManage.ManaHit</c> — melee restores MP, so the
    /// swing rate has broken the mana economy too — is a separate question, not answered here.
    /// </summary>
    [HarmonyPatch]
    internal static class MeleeBalance
    {
        /// <summary>
        /// What one blow had taken off it, so the postfix can put it back.
        ///
        /// It has to be put back. <c>AttackData</c> is a <c>MonoBehaviour</c> on the attack
        /// range's own GameObject — one component, shared by every blow that range will ever
        /// deal — so a field lowered and left lowered is not a waived stagger, it is that
        /// attack permanently weakened for the rest of the run, pad and magic included.
        /// </summary>
        private struct Waived
        {
            internal AttackData Data;

            internal bool Hitstop;
            internal bool PauseTime;

            internal bool Stagger;
            internal float Stiff;
            internal float Repulse;
            internal bool CertainlyRepulse;

            /// <summary>The enemy whose clock is waiting on this blow's result, if it was
            /// allowed to stagger.</summary>
            internal Record Commit;
        }

        /// <summary>What is remembered about one enemy.</summary>
        private sealed class Record
        {
            internal NPCManage Npc;

            internal float LastHitAt = float.NegativeInfinity;
            internal float SeenAt;

            /// <summary>Blows landed in the current flurry — cleared by leaving it alone.</summary>
            internal int Flurry;

            // -- telemetry, all of it idle unless LogMeleeBalance is on ---------------------

            internal int Hits;
            internal int Staggers;
            internal int HitstopsWaived;
            internal int StaggersWaived;

            internal float Tracked;
            internal float Down;

            /// <summary>When the enemy entered the damaged state it is in, or negative.</summary>
            internal float DownSince = -1f;
            internal float LongestDown;
            internal float TotalDownSpans;
            internal int DownSpans;
        }

        private static readonly Dictionary<System.IntPtr, Record> Enemies = new();

        /// <summary>
        /// Before the blow: waive whatever this enemy is currently immune to.
        ///
        /// Never skips the original. A blow that cannot stagger still lands, still deals its
        /// damage, still raises its impact effect and its hit sound — only the interruption is
        /// taken off it. Refusing the hit outright was the other option, and it is the one that
        /// reads as a bug from inside a headset.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(NPCManage), nameof(NPCManage.Hit))]
        private static void BeforeHit(NPCManage __instance, AttackData Data, out Waived __state)
        {
            __state = default;

            var cfg = Plugin.Instance;
            if (cfg == null || Data == null || __instance == null) return;

            var budget = cfg.StaggerBudget.Value;
            var recovery = cfg.StaggerRecovery.Value;
            var hitstop = cfg.HitstopInterval.Value;
            if (budget <= 0 && hitstop <= 0f) return;

            // The game's clock, not the arm's. VrMelee measures the player on unscaled time
            // because an arm keeps moving at its own speed through a hit-stop; this is the
            // other side of that same argument. An enemy's cooldown belongs to the world the
            // enemy is in, including standing still while the game is paused.
            var now = Time.time;
            var record = Track(__instance, now);

            __state.Data = Data;

            if (hitstop > 0f && Data.g_bPauseTime && now - record.LastHitAt < hitstop)
            {
                __state.Hitstop = true;
                __state.PauseTime = Data.g_bPauseTime;
                Data.g_bPauseTime = false;
                record.HitstopsWaived++;
            }

            // A flurry that has been let go of is over. Measured from the last blow rather than
            // from the last stagger, so a player who keeps hitting an enemy that can no longer
            // be staggered is keeping the flurry alive by doing it — which is the whole of what
            // "stop hitting it" is supposed to mean.
            if (now - record.LastHitAt >= recovery) record.Flurry = 0;

            if (budget > 0 && record.Flurry >= budget)
            {
                // The knockback goes with the stiffness. A blow that shoves an enemy out of
                // its attack has interrupted it as surely as one that freezes it, and
                // g_bCertainlyRepulse exists precisely to force that through — so leaving
                // either of them standing would waive the stagger in name only.
                __state.Stagger = true;
                __state.Stiff = Data.g_fStiff;
                __state.Repulse = Data.g_fRepulse;
                __state.CertainlyRepulse = Data.g_bCertainlyRepulse;

                Data.g_fStiff = 0f;
                Data.g_fRepulse = 0f;
                Data.g_bCertainlyRepulse = false;

                record.StaggersWaived++;
            }
            else if (budget > 0)
            {
                // Not counted here. Whether this blow was a blow at all is the game's to say,
                // and it says so by what Hit returns; spending a point of the budget now would
                // spend it on a hit refused for being dead, invulnerable, or already on the
                // collision's exclusion list.
                __state.Commit = record;
            }

            record.LastHitAt = now;
            record.Hits++;
        }

        /// <summary>
        /// After the blow: put the attack back the way it was, and start the clock if the game
        /// agreed that something was hit.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(NPCManage), nameof(NPCManage.Hit))]
        private static void AfterHit(bool __result, Waived __state)
        {
            var data = __state.Data;
            if (data == null) return;

            if (__state.Hitstop) data.g_bPauseTime = __state.PauseTime;

            if (__state.Stagger)
            {
                data.g_fStiff = __state.Stiff;
                data.g_fRepulse = __state.Repulse;
                data.g_bCertainlyRepulse = __state.CertainlyRepulse;
            }

            if (__result && __state.Commit != null)
            {
                __state.Commit.Flurry++;
                __state.Commit.Staggers++;
            }
        }

        /// <summary>
        /// Forgets an enemy the game is bringing up fresh.
        ///
        /// Records are keyed by pointer, and a pointer is only unique while the object behind
        /// it is alive: an enemy destroyed and another allocated at the same address would
        /// inherit its cooldown, and spend its first second unable to be staggered for reasons
        /// nothing on screen could explain. This catches it at the source; the sweep in
        /// <see cref="Tick"/> is the backstop for whatever does not come through here.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(NPCManage), nameof(NPCManage.Init))]
        private static void AfterInit(NPCManage __instance)
        {
            if (__instance == null) return;
            Enemies.Remove(__instance.Pointer);
        }

        private static Record Track(NPCManage npc, float now)
        {
            if (!Enemies.TryGetValue(npc.Pointer, out var record))
            {
                record = new Record();
                Enemies[npc.Pointer] = record;
            }

            record.Npc = npc;
            record.SeenAt = now;
            return record;
        }

        // -- telemetry ---------------------------------------------------------------------

        /// <summary>How long an untouched enemy is kept before its record is dropped.</summary>
        private const float Forget = 30f;

        /// <summary>How often the record sweep runs, in seconds.</summary>
        private const float Sweep = 5f;

        /// <summary>How long after the last blow landed an enemy is still in a fight.</summary>
        private const float FightTail = 4f;

        /// <summary>How often the fight summary is written, in seconds.</summary>
        private const float Report = 5f;

        private static float _nextSweep;
        private static float _nextReport;

        private static readonly List<System.IntPtr> Gone = new();

        /// <summary>
        /// A frame of bookkeeping: sweep stale records, and — only when asked — measure how
        /// long the enemies being hit actually spend unable to act.
        ///
        /// That measurement is what <c>StaggerBudget</c> and <c>StaggerRecovery</c> have to be
        /// set against. Four blows that stagger for 0.4 s each fill a one-second recovery twice
        /// over, and the enemy never gets its turn at all; the same four at 0.15 s leave it most
        /// of the second. The two look identical from inside the headset.
        ///
        /// Called from <c>VrControls.Update</c>, every frame and before any gate: an enemy's
        /// flurry does not stop mattering because the player opened a menu.
        /// </summary>
        internal static void Tick()
        {
            var now = Time.time;

            if (now >= _nextSweep)
            {
                _nextSweep = now + Sweep;
                Prune(now);
            }

            var cfg = Plugin.Instance;
            if (cfg == null || !cfg.LogMeleeBalance.Value) return;

            var dt = Time.deltaTime;
            if (dt <= 0f) return;

            var fighting = false;

            foreach (var record in Enemies.Values)
            {
                var npc = record.Npc;
                if (npc == null) continue;

                var ai = npc.aiNpc;
                if (ai == null) continue;

                if (now - record.SeenAt < FightTail) fighting = true;

                record.Tracked += dt;

                var down = IsDown(ai.g_Status);
                if (down) record.Down += dt;

                // The length of one uninterrupted stagger, which is a different question from
                // the share of time spent staggered and the more useful of the two: it is what
                // the cooldown has to be set against.
                if (down && record.DownSince < 0f)
                {
                    record.DownSince = now;
                }
                else if (!down && record.DownSince >= 0f)
                {
                    var span = now - record.DownSince;
                    record.DownSince = -1f;
                    record.DownSpans++;
                    record.TotalDownSpans += span;
                    if (span > record.LongestDown) record.LongestDown = span;
                }
            }

            if (!fighting || now < _nextReport) return;
            _nextReport = now + Report;

            WriteReport(now);
        }

        private static bool IsDown(AI_NPC.AIStatus status)
            => status == AI_NPC.AIStatus.Damaged
            || status == AI_NPC.AIStatus.DamagedDown
            || status == AI_NPC.AIStatus.DamagedFly
            || status == AI_NPC.AIStatus.GetUp;

        private static void WriteReport(float now)
        {
            var enemies = 0;
            int hits = 0, staggers = 0, staggersWaived = 0, hitstopsWaived = 0, spans = 0;
            float tracked = 0f, down = 0f, spanTotal = 0f, longest = 0f;

            foreach (var record in Enemies.Values)
            {
                if (record.Npc == null || now - record.SeenAt >= FightTail) continue;

                enemies++;
                hits += record.Hits;
                staggers += record.Staggers;
                staggersWaived += record.StaggersWaived;
                hitstopsWaived += record.HitstopsWaived;

                tracked += record.Tracked;
                down += record.Down;

                spans += record.DownSpans;
                spanTotal += record.TotalDownSpans;
                if (record.LongestDown > longest) longest = record.LongestDown;
            }

            if (enemies == 0) return;

            var share = tracked > 0f ? down / tracked * 100f : 0f;
            var mean = spans > 0 ? spanTotal / spans : 0f;

            Plugin.Log.LogInfo(
                $"[balance] {enemies} enemy(s) — {hits} hit(s), {staggers} stagger(s), "
              + $"{staggersWaived} stagger(s) waived, {hitstopsWaived} hit-stop(s) waived | "
              + $"unable to act {share:F0}% of tracked time | "
              + $"stagger length mean {mean:F2}s, longest {longest:F2}s over {spans} span(s)");
        }

        private static void Prune(float now)
        {
            Gone.Clear();

            foreach (var pair in Enemies)
            {
                if (pair.Value.Npc == null || now - pair.Value.SeenAt > Forget)
                    Gone.Add(pair.Key);
            }

            for (var i = 0; i < Gone.Count; i++) Enemies.Remove(Gone[i]);
        }
    }
}
