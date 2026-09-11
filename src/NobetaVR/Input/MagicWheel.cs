using Il2CppInterop.Runtime;
using UnityEngine;

namespace NobetaVR.Input
{
    /// <summary>
    /// The spell wheel, opened with the right stick and pointed around with the same stick.
    ///
    /// The game's own wheel is a hold: <c>AppearMagicMenu</c> takes a held flag,
    /// <c>MoveMenuPointer</c> takes the stick, and letting go of the button that opened it
    /// applies whatever the arrow is on. On a pad that works because the button and the stick
    /// are different controls. On a Touch controller the only spare control the right hand has
    /// is the stick's own click, and a thumb that presses a stick down cannot then push it
    /// sideways without lifting off the click it is holding — so the wheel came up and shut
    /// again the moment the player tried to choose anything. "I can open the spell menu by
    /// clicking the right stick but I cannot select a new spell by moving it."
    ///
    /// <para>
    /// So the click is read the way Y already is: a tap and a hold are two gestures, and the
    /// ambiguity is settled here rather than by asking the player to be dextrous. A hold
    /// behaves exactly as the game's own does, closing on release. A tap — including the tap a
    /// hold turns into the instant the thumb rolls off it — leaves the wheel up with the stick
    /// free, and it closes when the stick comes home after choosing, or on a second tap if
    /// nothing was chosen. Both gestures end the same way, so there is nothing new to learn:
    /// push towards a spell and let go.
    /// </para>
    ///
    /// <para>
    /// The selection itself is taken from the wheel rather than computed. Its own
    /// <c>curMagicID</c> is the slot the arrow is on, so applying it cannot point anywhere the
    /// player is not already looking at — where deriving the angle ourselves would mean
    /// guessing which way round the game measures its wheel, and being wrong silently, with a
    /// spell change as the punishment. What we do carry is the <em>timing</em>: the game's
    /// apply-on-close belongs to a button we are no longer letting go of at the same moment,
    /// so the close applies the slot explicitly.
    /// </para>
    ///
    /// The pointer is still handed to the game at <c>PlayerInputController.MoveMenuPointer</c>,
    /// the method the game's own binding calls. If it turns out not to arrive — measured, by
    /// watching whether the wheel's arrow moves at all while the stick is over — the same value
    /// goes to <c>StageUIManager.UpdateMagicPointer</c> one level below it, and the log says so
    /// once. A pointer that silently does nothing is otherwise indistinguishable from a wheel
    /// that closed before the push landed, which is the fault this class was written for.
    /// </summary>
    internal sealed class MagicWheel
    {
        /// <summary>
        /// How quickly the click has to come back up to be a tap rather than a hold, in
        /// seconds. Generous, because the release this has to catch is often not a deliberate
        /// tap at all but a thumb sliding off a stick it is trying to push.
        /// </summary>
        private const float TapWindow = 0.4f;

        /// <summary>
        /// How long a wheel nobody is closing stays up, in seconds. A safety rail: a latched
        /// wheel that outlives the player's intention to use it is a wheel with the turning
        /// control held down underneath it.
        /// </summary>
        private const float Patience = 12f;

        /// <summary>
        /// How long the stick has to be over with nothing happening before the pointer is
        /// handed to the UI directly, in seconds. Long enough that the wheel's own easing has
        /// plainly had its chance.
        /// </summary>
        private const float Deaf = 0.35f;

        /// <summary>
        /// Where the stick counts as home again, as a fraction of the push that counts as a
        /// choice. The hysteresis is what makes "let go to choose" survive a thumb that rolls
        /// from one slot to the next rather than lifting between them.
        /// </summary>
        private const float HomeFraction = 0.5f;

        private bool _open;
        private bool _latched;
        private bool _blocked;
        private bool _clickHeld;
        private bool _chose;
        private float _openedAt;

        private float _deflectedSince;
        private float _arrowAtOpen;
        private bool _direct;
        private int _reported;

        private StageUIManager _ui;
        private float _nextScan;

        /// <summary>Whether the wheel is up, and therefore owns the right stick.</summary>
        public bool IsOpen => _open;

        /// <summary>
        /// A frame of the wheel. Returns whether it is up, which is what the caller turns the
        /// turn control off on.
        /// </summary>
        /// <param name="input">This frame's controllers.</param>
        /// <param name="controller">The game's input controller, or null before one exists.</param>
        /// <param name="hers">
        /// False while the game rather than the player is driving her — a cutscene, a
        /// conversation, a death. The wheel closes and refuses to reopen until the click has
        /// been let go, so a click still held when the game takes her cannot reopen it on the
        /// next frame against a caller that will only close it again.
        /// </param>
        /// <param name="menuOpen">
        /// Whether one of the game's own menus has the input. It blocks the wheel from opening
        /// and deliberately does not close one that is already up: a wheel open over a menu
        /// leaves the menu undrivable, since the caller hands the wheel the stick that would
        /// otherwise navigate it — but if the wheel's own appearance were ever to count as a
        /// menu, closing on this would make the wheel impossible to open at all. Blocking only
        /// the opening cannot fail that way, because nothing is bound yet on the frame the
        /// player asks for it.
        /// </param>
        public bool Update(VrInput input, PlayerInputController controller, bool hers,
                           bool menuOpen)
        {
            if (controller == null)
            {
                _open = _latched = false;
                return false;
            }

            // Both sticks clicked together is the mod's own settings menu opening, not the
            // wheel. Checked here rather than by update order, because VrMenu is a separate
            // component and Unity's order between the two is not ours to decide.
            var click = input.Pressed(VrInput.Hand.Right, VrInput.Button.StickClick)
                     && !input.Pressed(VrInput.Hand.Left, VrInput.Button.StickClick);

            var pressed = click && !_clickHeld;
            var released = !click && _clickHeld;
            _clickHeld = click;

            if (!hers)
            {
                if (_open) Close(controller, false);
                _blocked = click;
                return false;
            }

            if (_blocked)
            {
                if (click) return false;
                _blocked = false;
            }

            if (!_open)
            {
                if (pressed && !menuOpen) Open(controller);
                return _open;
            }

            var stick = input.RightStick;
            var reach = Plugin.Instance.MenuDeadzone.Value;

            var deflected = stick.magnitude >= reach;
            if (deflected) _chose = true;

            // Letting go has to be a lower bar than pushing, or the wheel closes on the way
            // between two slots: the menu dead zone is deliberately high — a menu step is a
            // deliberate act — and a thumb travelling round the circle passes under it.
            var home = stick.magnitude <= reach * HomeFraction;

            Point(controller, stick, deflected);

            if (!_latched)
            {
                if (!released) return true;

                // A click that came straight back up is a tap, and a tap hands the stick over
                // instead of ending the wheel. A longer one is the game's own gesture and ends
                // the way the game's does.
                if (Time.unscaledTime - _openedAt <= TapWindow)
                {
                    _latched = true;
                    return true;
                }

                Close(controller, true);
                return false;
            }

            // Latched: the stick coming home after a push is the same "let go to choose" the
            // hold has, a second click is the way out for a wheel opened by accident, and the
            // patience is for a wheel nobody closed at all.
            if (pressed || (_chose && home) || Time.unscaledTime - _openedAt > Patience)
            {
                Close(controller, true);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Shuts the wheel because something else took the controllers. Nothing is applied: a
        /// menu opening over a wheel is not a choice.
        /// </summary>
        public void StandDown(PlayerInputController controller)
        {
            if (!_open) return;
            if (controller != null) Close(controller, false);
            else _open = _latched = _chose = false;
        }

        /// <summary>
        /// How quickly the wheel itself moves, which is not the same question as where the
        /// stick is pointing.
        ///
        /// The wheel eases: the pointer, the arrow and the icons all chase their targets at
        /// <c>MAGIC_SPEED</c> rather than snapping to them. On a monitor that reads as polish
        /// and costs nothing, because the wheel is opened and held for as long as it takes. In
        /// a headset it is the whole interaction — you push towards a spell and let go — and
        /// the easing is then a lag between the push and the answer, felt as the wheel
        /// dragging behind the thumb.
        ///
        /// So the game's own rate is multiplied rather than replaced. The original is read off
        /// the running game the first time and kept, so the setting is always applied to it
        /// rather than to a value this method wrote last time — which is what makes it a knob
        /// that can be turned both ways instead of one that only ever compounds. Both statics
        /// are scaled: the selector's is the pointer and the arrow, the handler's is the icons
        /// growing and settling under it, and an arrow that arrives ahead of the highlight is
        /// worse than either being slow.
        ///
        /// <c>MAGIC_INTERVAL</c> next to the second of them is deliberately left alone — the
        /// name says spacing, not rate, and a wheel whose slots have moved is a different bug
        /// from a wheel that is slow.
        /// </summary>
        private void ApplyWheelSpeed()
        {
            if (_speedFailed) return;

            var factor = Mathf.Clamp(Plugin.Instance.SpellWheelSpeed.Value, 0.25f, 8f);

            try
            {
                if (float.IsNaN(_selectorSpeedWas))
                {
                    _selectorSpeedWas = UIMagicSelector.MAGIC_SPEED;
                    _handlerSpeedWas = UIMagicHandler.MAGIC_SPEED;
                    Plugin.Log.LogInfo($"the spell wheel's own rates are "
                                     + $"{_selectorSpeedWas:F2} for the pointer and "
                                     + $"{_handlerSpeedWas:F2} for the icons; "
                                     + $"×{factor:F2} from here on");
                }

                UIMagicSelector.MAGIC_SPEED = _selectorSpeedWas * factor;
                UIMagicHandler.MAGIC_SPEED = _handlerSpeedWas * factor;
            }
            catch (System.Exception e)
            {
                // Nothing here is load-bearing: a wheel at the game's own speed is the wheel
                // the game shipped. Tried once and then given up on, both so a build where
                // these are not readable does not pay for the attempt on every open, and so
                // that a half-finished attempt cannot leave a rate of its own behind.
                _speedFailed = true;
                Plugin.Log.LogWarning($"the spell wheel's speed could not be set: {e.Message}");
            }
        }

        private static float _selectorSpeedWas = float.NaN;
        private static float _handlerSpeedWas = float.NaN;
        private static bool _speedFailed;

        private void Open(PlayerInputController controller)
        {
            ApplyWheelSpeed();

            _open = true;
            _latched = false;
            _chose = false;
            _direct = false;
            _deflectedSince = 0f;
            _openedAt = Time.unscaledTime;
            _arrowAtOpen = Arrow();

            controller.AppearMagicMenu(true);
        }

        private void Close(PlayerInputController controller, bool apply)
        {
            // Read before the close, because a wheel on its way out is entitled to forget what
            // it was pointing at.
            var id = SlotUnderTheArrow();

            controller.AppearMagicMenu(false);

            if (apply && _chose && id >= 0) controller.ApplyMagic(id);

            Report(apply, id);

            _open = _latched = _chose = false;
            _deflectedSince = 0f;
        }

        /// <summary>
        /// Hands the stick to the game, and notices when it does not arrive.
        ///
        /// The game's own seam is asked first and every frame — <c>MoveMenuPointer</c> is the
        /// method the game's pad binding calls, so everything downstream of it behaves as it
        /// always has. The direct route below is only reached when the wheel's arrow has not
        /// moved a degree while the stick was over for a third of a second, which is the
        /// measurement rather than a suspicion.
        /// </summary>
        private void Point(PlayerInputController controller, Vector2 stick, bool deflected)
        {
            if (_direct)
            {
                var ui = Ui();
                if (ui != null) ui.UpdateMagicPointer(stick);
                else controller.MoveMenuPointer(stick);
                return;
            }

            controller.MoveMenuPointer(stick);

            if (!deflected) { _deflectedSince = 0f; return; }
            if (_deflectedSince <= 0f) { _deflectedSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - _deflectedSince < Deaf) return;

            var arrow = Arrow();

            // No wheel to ask yet — it is found asynchronously, and a stage's opening second
            // has none. Nothing can be concluded from that, so nothing is.
            if (float.IsNaN(arrow)) return;

            // And the reading that was taken before there was one is not a baseline either.
            if (float.IsNaN(_arrowAtOpen))
            {
                _arrowAtOpen = arrow;
                _deflectedSince = Time.unscaledTime;
                return;
            }

            if (Mathf.Abs(Mathf.DeltaAngle(arrow, _arrowAtOpen)) > 1f) return;

            _direct = true;
            Plugin.Log.LogInfo($"the spell wheel's arrow stayed at {arrow:F0} deg for "
                             + $"{Deaf:F2}s with the stick over, so MoveMenuPointer is not "
                             + "reaching it. Feeding the wheel directly from here on.");
        }

        /// <summary>
        /// The slot the wheel's arrow is on, or -1 when there is no wheel to ask.
        ///
        /// The wheel's own answer rather than one derived from the stick. Which way round the
        /// game measures its arrow, and which slot each sixth of a turn belongs to, are facts
        /// about a UI we cannot read the code of — and being wrong about them costs the player
        /// a spell they did not ask for, silently.
        /// </summary>
        private int SlotUnderTheArrow()
        {
            var selector = Selector();
            return selector != null ? selector.curMagicID : -1;
        }

        /// <summary>Where the wheel's arrow is pointing, or NaN when there is no wheel.</summary>
        private float Arrow()
        {
            var selector = Selector();
            return selector != null ? selector.arrowDegree : float.NaN;
        }

        private UIMagicSelector Selector()
        {
            var ui = Ui();
            return ui != null ? ui.magicSelector : null;
        }

        /// <summary>
        /// The stage's UI manager, re-looked-for while missing rather than cached for the
        /// process: it is built per stage, and a reference kept across a load is a reference to
        /// an object Unity has thrown away.
        /// </summary>
        private StageUIManager Ui()
        {
            if (_ui != null) return _ui;
            if (Time.unscaledTime < _nextScan) return null;
            _nextScan = Time.unscaledTime + 1f;

            var found = Object.FindObjectOfType(Il2CppType.Of<StageUIManager>());
            _ui = found != null ? found.TryCast<StageUIManager>() : null;
            return _ui;
        }

        /// <summary>
        /// Says what the wheel did, for the first few uses of it and then never again.
        ///
        /// Three of the four ways this can go wrong look identical from inside the headset — a
        /// wheel that closed too early, a pointer that never arrived, and an apply that went to
        /// a slot nothing was pointing at — and a player can only report the one symptom. One
        /// line names which of them happened.
        /// </summary>
        private void Report(bool apply, int id)
        {
            if (_reported >= 4) return;
            _reported++;

            var selector = Selector();
            var enabled = selector != null && selector.isMagicSelectEnabled;

            Plugin.Log.LogInfo($"spell wheel closed after "
                             + $"{Time.unscaledTime - _openedAt:F2}s "
                             + $"({(_latched ? "latched" : "held")}): "
                             + $"{(_chose ? "pointed" : "nothing chosen")}, slot {id}, "
                             + $"arrow {Arrow():F0} deg, wheel enabled={enabled}, "
                             + $"{(apply && _chose && id >= 0 ? "applied" : "applied nothing")}");
        }
    }
}
