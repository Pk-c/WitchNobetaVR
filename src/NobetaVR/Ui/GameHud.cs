using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace NobetaVR.Ui
{
    /// <summary>
    /// Decides which parts of the game's own interface are on the panel, and when.
    ///
    /// A flat game shows the whole HUD at all times because it costs the player nothing: it
    /// sits in the corners of a screen the eye is already pointed at. In a headset every one
    /// of those corners is somewhere you have to look, and the panel they are drawn on is in
    /// front of the world rather than around it, so a permanent HUD is a permanent film over
    /// the room. What is worth keeping is what you would actually go and read — and the bars
    /// you read constantly are better worn than framed, which is what <see cref="WristGauges"/>
    /// is for.
    ///
    /// Nothing here destroys or disables a widget. Each one gets a <see cref="CanvasGroup"/>
    /// and is faded by it, which leaves the game's own animations, its own alpha handling and
    /// its own hiding rules underneath completely intact — a CanvasGroup multiplies, so
    /// anything the game has already hidden stays hidden and anything it fades still fades.
    /// It also means every one of these choices is reversible at runtime from the VR menu,
    /// with no stage reload and nothing left behind.
    ///
    /// The widgets are rebuilt per stage, so they are re-found on a timer rather than once.
    /// </summary>
    public sealed class GameHud : MonoBehaviour
    {
        public GameHud(IntPtr ptr) : base(ptr) { }

        internal static GameHud Instance { get; private set; }

        private StageUIManager _ui;
        private float _nextScan;

        private CanvasGroup _stats;    // health, stamina and mana, along the top
        private CanvasGroup _charge;   // the spell charge bar, top left
        private CanvasGroup _money;    // the soul counter
        private CanvasGroup _items;    // the item bar along the bottom
        private RectTransform _background;

        private float _statsAlpha = 1f;
        private float _chargeAlpha = 1f;
        private float _moneyAlpha = 1f;
        private float _itemsAlpha = 1f;

        /// <summary>Unscaled time at which each timed widget goes away again.</summary>
        private float _moneyUntil;
        private float _itemsUntil;

        /// <summary>Set while she is at a save statue, where the soul count is the point.</summary>
        private bool _praying;

        private void Awake() => Instance = this;

        private void LateUpdate()
        {
            Resolve();
            if (_ui == null) return;

            Fade();
            Background();
        }

        // -- what the game tells us ------------------------------------------------------

        /// <summary>Souls gained or spent: show the counter, then let it go again.</summary>
        internal static void FlashMoney()
        {
            var self = Instance;
            if (self == null) return;
            self._moneyUntil = Time.unscaledTime + Plugin.Instance.MoneyShowSeconds.Value;
        }

        /// <summary>A different item is selected, or the bar's contents changed.</summary>
        internal static void FlashItems()
        {
            var self = Instance;
            if (self == null) return;
            self._itemsUntil = Time.unscaledTime + Plugin.Instance.ItemBarShowSeconds.Value;
        }

        /// <summary>
        /// Held for the whole of a save statue rather than flashed: levelling up spends souls
        /// several at a time, and a counter that timed out halfway through would be gone for
        /// the one part of the game where the number is what you are looking at.
        /// </summary>
        internal static void SetPraying(bool praying)
        {
            var self = Instance;
            if (self == null) return;

            self._praying = praying;
            if (!praying) FlashMoney();   // leave it up briefly on the way out
        }

        // -- resolution ------------------------------------------------------------------

        private void Resolve()
        {
            if (_ui != null) return;
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 1f;

            var found = UnityEngine.Object.FindObjectOfType(Il2CppType.Of<StageUIManager>());
            var ui = found != null ? found.TryCast<StageUIManager>() : null;
            if (ui == null) return;

            _ui = ui;

            // A stage that ended while she was at a statue would otherwise leave the counter
            // pinned on for the whole of the next one.
            _praying = false;

            _stats = Group(ui.playerStats != null ? ui.playerStats.gameObject : null);
            _charge = Group(ui.magicBar != null ? ui.magicBar.gameObject : null);
            _money = Group(ui.playersSubStats != null ? ui.playersSubStats.gameObject : null);
            _items = Group(ui.itemBar != null ? ui.itemBar.gameObject : null);
            _background = ui.background != null ? ui.background.rectTransform : null;

            _statsAlpha = _chargeAlpha = _moneyAlpha = _itemsAlpha = 1f;

            Plugin.Log.LogInfo("game HUD bound: "
                             + $"stats {Seen(_stats)}, charge {Seen(_charge)}, "
                             + $"souls {Seen(_money)}, items {Seen(_items)}, "
                             + $"background {(_background != null ? "yes" : "no")}");
        }

        private static string Seen(CanvasGroup group) => group != null ? "yes" : "no";

        /// <summary>
        /// The group is added rather than looked for and required. None of these objects has
        /// one in the shipped game, and adding it is what makes a widget fadeable as a whole
        /// instead of one Image at a time.
        /// </summary>
        private static CanvasGroup Group(GameObject go)
        {
            if (go == null) return null;

            var group = go.GetComponent<CanvasGroup>();
            if (group == null) group = go.AddComponent<CanvasGroup>();
            return group;
        }

        // -- fading ----------------------------------------------------------------------

        private void Fade()
        {
            var cfg = Plugin.Instance;
            var on = cfg.TidyGameHud.Value;
            var now = Time.unscaledTime;

            // Alpha per second rather than a fraction per frame, so a fade takes the same time
            // whatever the frame rate is doing.
            var step = Mathf.Max(0.01f, cfg.HudFadeSpeed.Value) * Time.unscaledDeltaTime;

            Apply(ref _statsAlpha, _stats,
                  !on || !cfg.HideHealthBars.Value ? 1f : 0f, step);
            Apply(ref _chargeAlpha, _charge,
                  !on || !cfg.HideChargeBar.Value ? 1f : 0f, step);
            Apply(ref _moneyAlpha, _money,
                  !on || !cfg.HideSoulCounter.Value || _praying || now < _moneyUntil ? 1f : 0f, step);
            Apply(ref _itemsAlpha, _items,
                  !on || !cfg.HideItemBar.Value || now < _itemsUntil ? 1f : 0f, step);
        }

        private static void Apply(ref float current, CanvasGroup group, float target, float step)
        {
            if (group == null) return;

            current = Mathf.MoveTowards(current, target, step);
            group.alpha = current;

            // A widget faded out should not be one you can still click through to. The game's
            // HUD takes no pointer input today, so this changes nothing now; it is what stops
            // the first piece of it that does from becoming an invisible trap.
            group.blocksRaycasts = current > 0.001f;
        }

        // -- the backdrop ----------------------------------------------------------------

        /// <summary>
        /// Stretches the game's own backdrop image.
        ///
        /// It is sized for a flat screen, so on the capture it leaves the edges uncovered: a
        /// fade to black comes out as a black rectangle with the game still showing around it.
        /// Scaling it up costs nothing and covers the whole capture. The image is otherwise
        /// left alone, so the game's fades, its colour and its timing all still drive it.
        ///
        /// Re-applied whenever it drifts rather than once, because this object is rebuilt with
        /// the rest of the stage UI and the game animates its transform.
        /// </summary>
        private void Background()
        {
            if (_background == null) return;

            var scale = Mathf.Max(1f, Plugin.Instance.BackgroundScale.Value);
            var current = _background.localScale;
            if (Mathf.Abs(current.x - scale) < 0.001f && Mathf.Abs(current.y - scale) < 0.001f) return;

            _background.localScale = new Vector3(scale, scale, 1f);
        }
    }
}
