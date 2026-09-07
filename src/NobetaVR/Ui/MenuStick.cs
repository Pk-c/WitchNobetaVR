using UnityEngine;

namespace NobetaVR.Ui
{
    /// <summary>
    /// Reads the stick as a menu step: one axis, one direction, or nothing.
    ///
    /// Shared by the game's menus and the mod's own settings panel, which were reading the same
    /// stick by two different rules — one on <c>MenuDeadzone</c>, the other on a 0.5 written into
    /// the panel — so the setting only governed half of what it named, and the two felt
    /// different for no reason a player could see.
    /// </summary>
    internal static class MenuStick
    {
        /// <summary>
        /// How far the leading axis must lead by.
        ///
        /// Picking the larger axis and acting on it is enough to always produce an answer and
        /// not enough to produce the right one: at the diagonal the two are a hair apart, so a
        /// push meant as "up" lands as "right" on whichever side of the hair it happened to
        /// fall. In a menu whose vertical is "which setting" and whose horizontal is "change
        /// it", that is the difference between moving the cursor and altering a value you were
        /// not looking at — and both directions of the mistake are silent.
        ///
        /// A margin turns the diagonal into a band where nothing happens. At 1.4 each axis owns
        /// a little over seventy degrees and about nineteen separate them, which is wide enough
        /// to have to mean the diagonal and narrow enough that no deliberate push is refused.
        /// </summary>
        private const float Lead = 1.4f;

        /// <summary>
        /// The step the stick is asking for: one of x or y is -1 or 1, or both are zero when
        /// the stick is inside the dead zone or too close to a diagonal to be read.
        /// </summary>
        public static Vector2Int Step(Vector2 stick)
        {
            var dead = Plugin.Instance.MenuDeadzone.Value;

            var x = Mathf.Abs(stick.x);
            var y = Mathf.Abs(stick.y);

            if (y > dead && y > x * Lead) return new Vector2Int(0, stick.y > 0f ? 1 : -1);
            if (x > dead && x > y * Lead) return new Vector2Int(stick.x > 0f ? 1 : -1, 0);

            return Vector2Int.zero;
        }
    }
}
