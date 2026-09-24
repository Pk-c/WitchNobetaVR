using System;
using UnityEngine;

namespace NobetaVR.Input
{
    /// <summary>
    /// Drives Nobeta from the Touch controllers.
    ///
    /// Everything goes through the game's own seams rather than around them. Movement is handed
    /// to <c>PlayerInputController.Move</c>, the same method the game's own bindings call, so
    /// everything downstream — walk and dash states, the animator, the character controller —
    /// behaves exactly as it does on a pad; the same is true of every other action here, each
    /// of which calls the method its keyboard or pad binding calls. Turning is the one
    /// exception, and adds to <c>PlayerCamera.g_fX</c>, the camera's own yaw, so the view, the
    /// direction movement is relative to, and the game's idea of where you are looking all stay
    /// one value instead of three that must be kept in step.
    ///
    /// The map:
    ///
    /// <list type="table">
    /// <item><term>Left stick</term><description>move; click to run</description></item>
    /// <item><term>Right stick</term><description>turn; click for the spell wheel, then point with it</description></item>
    /// <item><term>A / B</term><description>jump / dodge</description></item>
    /// <item><term>X</term><description>use item</description></item>
    /// <item><term>Y</term><description>interact; held, the pause menu</description></item>
    /// <item><term>Triggers</term><description>left prays, right shoots</description></item>
    /// <item><term>Grips</term><description>left cycles items, right focuses; together, recentre</description></item>
    /// </list>
    ///
    /// Three of those share a control with something else — Y with the pause menu, the grips
    /// with recentring, the right stick with turning — and each of the three is resolved
    /// rather than by asking the player to be careful. See <see cref="Interact"/>,
    /// <see cref="Grips"/> and <see cref="MagicWheel"/>, which owns the last of them because
    /// the stick's click and the stick's push cannot be given at once by one thumb.
    /// </summary>
    public sealed class VrControls : MonoBehaviour
    {
        public VrControls(IntPtr ptr) : base(ptr) { }

        internal static VrControls Instance { get; private set; }

        /// <summary>
        /// Whether the right grip is holding focus. Read by the reticle, which draws the gap
        /// at its centre from it: focused is the steadier shot, and the mark closes to say so.
        /// Kept here rather than asked of the game, because this is the hold as the player made
        /// it — what the game does with it afterwards is another question, and one the reticle
        /// would be a frame late in reading.
        /// </summary>
        internal static bool Focusing { get; private set; }

        /// <summary>
        /// Whether the player has asked for anything since the stage opened: a stick past its
        /// dead zone, or any button, on a frame the gameplay bindings actually had the
        /// controllers.
        ///
        /// It exists for the one question a stage's first seconds turn on — whether the view is
        /// still the game's to point or the player's — and the answer cannot come from the game,
        /// which reports her controllable while it is standing her up against a save pillar. It
        /// can only come from the player. Until they touch something there is nothing to
        /// contradict, so the view rides her facing; the moment they do, it is theirs. See
        /// <see cref="Vr.FirstPerson.RideHerFacing"/>.
        ///
        /// Noted where the gameplay frame is, below every gate, so a stick that was scrolling a
        /// menu or a button pressed while the game had her is not the player asking to look
        /// somewhere.
        /// </summary>
        internal static bool PlayerActed { get; private set; }

        /// <summary>Forgets it, for a new stage: see <see cref="PlayerActed"/>.</summary>
        internal static void ForgetPlayerAction() => PlayerActed = false;

        private readonly VrInput _input = new();
        private readonly Ui.GameUiInput _gameUi = new();
        private readonly MagicWheel _wheel = new();

        /// <summary>The controllers, for anything else that needs to read them.</summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        internal VrInput Input => _input;

        /// <summary>
        /// Whether one of the game's own menus currently has the input. Read by the hands,
        /// which stand down while she cannot be moved.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        internal bool GameMenuOpen => _gameUi.MenuOpen;

        /// <summary>Captured from a postfix on PlayerInputController.Init.</summary>
        internal PlayerInputController InputController;
        internal PlayerCamera Camera;

        private bool _snapArmed = true;
        private bool _wasMoving;

        // Set whenever something other than turning owned the right stick this frame; cleared
        // by the stick coming home. See TurnHeldBack.
        private bool _turnLocked;

        // Held state from the previous frame, so a press can be told from a hold. Actions that
        // fire once need the edge; actions the game tracks itself need the level.
        private bool _jumpHeld, _dodgeHeld, _useItemHeld, _chantHeld;
        private bool _shootHeld, _runHeld, _aimHeld;

        // Y is two actions on one button, so its press has to be timed rather than acted on.
        private bool _yHeld, _pauseFired;
        private float _yDownAt;

        // The grips are three actions across two buttons: one each, and a third for both.
        private bool _leftGripHeld, _rightGripHeld;
        private float _leftGripAt, _rightGripAt;
        private bool _gripsConsumed, _cyclePending;

        // Both grips again, for the one thing they can still mean while the game has her.
        private bool _realignHeld;

        private void Awake() => Instance = this;

        private void Update()
        {
            // Before anything reads it, so the camera and room-scale work from one sample.
            Vr.HeadPose.Sample();

            _input.Poll();

            // The other end of the same controllers, and the only per-frame work the haptics
            // have: a rumble the game set as a level rather than as an event has to be kept
            // alive until it is cleared. See VrHaptics.Tick.
            Vr.VrHaptics.Tick();

            // Which of the gates below the frame took is the one thing a stuck cutscene cannot
            // be told from inside the headset, and every one of them is silent. See
            // CutsceneProbe; it costs a string per frame and writes nothing unless asked.
            Diagnostics.CutsceneProbe.Note(Drive());
            Diagnostics.CutsceneProbe.Tick(_gameUi.InputManager);

            // Enemy-side bookkeeping, and the one thing here that is not about the player at
            // all. Outside the gates on purpose: an enemy's flurry does not stop running
            // because the player opened a menu. See MeleeBalance.Tick.
            Vr.MeleeBalance.Tick();
        }

        /// <summary>
        /// A frame of control, and who ended up with it.
        ///
        /// The gates are a chain of early exits rather than one condition, so the answer to
        /// "why did that button do nothing" is which exit was taken — and that is what the
        /// returned name is. Split out from <c>Update</c> only so each exit names itself once,
        /// where it is, instead of the caller having to infer it afterwards.
        /// </summary>
        private string Drive()
        {
            if (!_input.Connected) return "no controllers";

            // Our own menu reads the controllers itself, and a game menu takes them over while
            // it is up. Either way the gameplay bindings stand down: without this the same
            // stick both walks Nobeta and scrolls the menu she is standing in.
            if (Ui.VrMenu.Instance != null && Ui.VrMenu.Instance.IsOpen)
            {
                StandDown();
                _turnLocked = true;
                return "vr menu";
            }

            // Whether she is the player's to drive at all this frame, asked once and used
            // twice: the wheel may not be opened during a moment she is not, and everything
            // below stands down for it.
            var hers = Vr.PlayerStatus.YoursToDrive;

            // The wheel is held open by us rather than by the game's menu stack, so it is
            // settled before the menu gate rather than behind it. Were it behind, anything
            // binding a UI controller while the wheel was up would leave it open with nothing
            // left able to close it. It is handed the menu's state all the same, because a
            // menu already up has to stop the wheel from opening over it — the wheel takes the
            // stick that would otherwise navigate that menu. See MagicWheel.
            var wheel = _wheel.Update(_input, InputController, hers, _gameUi.MenuOpen);
            if (wheel) _turnLocked = true;

            if (!wheel && _gameUi.Update(_input))
            {
                StandDown();
                _turnLocked = true;
                return "game menu";
            }

            // Every moment the game has her rather than the player, whether it says so with
            // the camera, with `controllable`, or with the state machine alone.
            //
            // Each of the three catches something the others do not — see
            // PlayerStatus.YoursToDrive — and the mod used to gate only on the last of them.
            // The other two are just as much the game driving her: during a cutscene the
            // trigger still fired her magic, the stick still walked her off the mark the scene
            // had placed her on, and the right stick still turned a camera the scene was
            // authoring. None of that is a control the player is meant to have while somebody
            // else is filming, and all of it is invisible from the game's side, which holds
            // her by refusing her input rather than by telling the mod to stop sending it.
            //
            // The two readings are still told apart for the log, because "she is down" and "a
            // scene is playing" send an investigation to opposite ends of the mod.
            if (!hers)
            {
                // Getting up from a knockdown is the one held moment with a way out: the game
                // lets the dodge cancel it while a direction is held. Everything else stays
                // stood down, but the stick and the dodge go through, and the game still
                // decides whether the dodge comes out.
                var gettingUp = Vr.PlayerStatus.GettingUpFromKnockdown;

                StandDown(gettingUp);
                RealignOrRecentre();

                if (gettingUp)
                {
                    Move();
                    DodgeButton();
                    return "getting up";
                }

                return Vr.PlayerStatus.DownOrGettingUp ? "she is down" : "the game has her";
            }

            NotePlayerAction();

            Grips();
            Move();
            if (!wheel && !TurnHeldBack()) Turn();
            Actions();
            // Room-scale only while she is plainly the player's, which the gate above has
            // already established for every line down here. A cutscene places her on a mark
            // and then acts around it, so a physical step slides her off it and the scene plays
            // out with her in the wrong place — and nothing in the scene will put her back.
            // The frames that stand down still call through, from StandDown, because the offset
            // has to be absorbed rather than banked; see RoomScale.Apply.
            Vr.RoomScale.Apply(Camera != null ? Camera.wizardGirl : null, Vr.VrCamera.ViewYaw, true);

            return wheel ? "magic wheel" : "gameplay";
        }

        /// <summary>
        /// Notes that this frame carried an intention, for <see cref="PlayerActed"/>.
        ///
        /// The same dead zones the controls themselves use, so what counts as asking for
        /// something is what counts as doing it — a thumb resting on a stick is neither.
        /// </summary>
        private void NotePlayerAction()
        {
            if (PlayerActed) return;

            var cfg = Plugin.Instance;
            if (_input.LeftStick.magnitude < cfg.MoveDeadzone.Value
             && Mathf.Abs(_input.RightStick.x) < cfg.TurnDeadzone.Value
             && !_input.AnyButton) return;

            PlayerActed = true;
        }

        /// <summary>
        /// Hands the controllers back when a menu takes them.
        ///
        /// Anything the game is tracking as held has to be told it ended, or it stays latched
        /// with nothing left to clear it: a menu opened mid-trigger leaves Nobeta shooting, and
        /// focus is worse, because nothing on screen says why the aim will not let go. The
        /// one-shot edges are set to what the buttons are actually doing rather than to false,
        /// so a button still down when the menu closes is not read as a fresh press.
        ///
        /// <paramref name="keepStickAndDodge"/> leaves the stick and B out of it, for the
        /// get-up the caller is about to feed them to; see <see cref="Drive"/>.
        /// </summary>
        private void StandDown(bool keepStickAndDodge = false)
        {
            // Physical movement is written off for as long as the controllers are handed back,
            // for the reason in RoomScale.Apply: what is not applied has to be spent, or a menu
            // becomes a way to store up a step and cash it in on the way out.
            Vr.RoomScale.Apply(Camera != null ? Camera.wizardGirl : null, Vr.VrCamera.ViewYaw, false);

            _wheel.StandDown(InputController);

            if (InputController != null)
            {
                if (_shootHeld) InputController.Shoot(false);
                if (_runHeld) InputController.Dash(false);
                if (_aimHeld) InputController.Aim(false);
                if (_wasMoving && !keepStickAndDodge) InputController.Move(Vector2.zero);
            }

            _shootHeld = _runHeld = _aimHeld = false;
            if (!keepStickAndDodge) _wasMoving = false;
            Focusing = false;
            _snapArmed = true;

            _jumpHeld = _input.Pressed(VrInput.Hand.Right, VrInput.Button.Primary);
            if (!keepStickAndDodge) _dodgeHeld = _input.Pressed(VrInput.Hand.Right, VrInput.Button.Secondary);
            _useItemHeld = _input.Pressed(VrInput.Hand.Left, VrInput.Button.Primary);
            _chantHeld = _input.Pressed(VrInput.Hand.Left, VrInput.Button.Trigger);
            _yHeld = _input.Pressed(VrInput.Hand.Left, VrInput.Button.Secondary);
            _pauseFired = _yHeld;   // the Y that opened the menu must not interact on release

            _leftGripHeld = _input.Pressed(VrInput.Hand.Left, VrInput.Button.Grip);
            _rightGripHeld = _input.Pressed(VrInput.Hand.Right, VrInput.Button.Grip);
            _cyclePending = false;
            _gripsConsumed = _leftGripHeld || _rightGripHeld;   // nothing fires until both are up
        }

        /// <summary>
        /// Both grips while the game has her, which is the same gesture that recentres during
        /// play and cannot mean quite the same thing here.
        ///
        /// Recentring writes the camera's own yaw — <c>g_fX</c>, through
        /// <see cref="Vr.FirstPerson.RealignToBody"/> — and during a staged shot that yaw is
        /// the game's, being authored frame by frame. So while the view stands back from her
        /// the gesture retakes <see cref="Vr.ShotFacing"/>'s alignment instead, which is the
        /// same request answered with the half of it that is ours to give: the shot goes back
        /// in front of you. While the view is still in her head — a conversation, a door — a
        /// recentre is exactly what it always was, and it stays that.
        ///
        /// It has its own edge rather than going through <see cref="Grips"/>, because the rest
        /// of that method is focus and item cycling, neither of which she is available for.
        /// </summary>
        private void RealignOrRecentre()
        {
            var both = _input.Pressed(VrInput.Hand.Left, VrInput.Button.Grip)
                    && _input.Pressed(VrInput.Hand.Right, VrInput.Button.Grip);

            if (both && !_realignHeld)
            {
                if (Vr.VrCamera.ViewStandsBack) Vr.ShotFacing.Realign();
                else Vr.HeadPose.Recenter();
            }

            _realignHeld = both;
        }

        /// <summary>
        /// The grips: focus on the right, item cycling on the left, recentre on both.
        ///
        /// Sharing two buttons between three actions only works if the combination can be told
        /// from its halves, and the two halves are not alike. Focus is a hold the game tracks
        /// itself, so it can start immediately and be taken back if the other grip joins it.
        /// Cycling is a one-shot, and a one-shot cannot be taken back — so it may not fire on
        /// the press.
        ///
        /// <para>
        /// It fires on the release instead, or on the window running out while the grip is
        /// still down, whichever comes first. Letting go inside the window is itself the proof
        /// that this was never half a recentre — that needs both grips down at once, and one
        /// of them is now up — so there is nothing left to wait for. Which is what makes a tap
        /// a tap: the item steps when your finger comes off, rather than a fifth of a second
        /// after it went on. Waiting the window out was the first cut of this and was wrong in
        /// exactly the way a delay is always wrong on a button you press to change something
        /// you are looking at.
        /// </para>
        ///
        /// The window also runs the other way. Both grips down is only a recentre if they went
        /// down together: holding focus and then reaching for an item is an ordinary thing to
        /// do, and it must not put your head back on Nobeta.
        /// </summary>
        private void Grips()
        {
            if (InputController == null) return;

            var left = _input.Pressed(VrInput.Hand.Left, VrInput.Button.Grip);
            var right = _input.Pressed(VrInput.Hand.Right, VrInput.Button.Grip);
            var now = Time.unscaledTime;
            var window = Plugin.Instance.RecentreGripWindow.Value;

            var leftReleased = !left && _leftGripHeld;

            if (left && !_leftGripHeld) { _leftGripAt = now; _cyclePending = true; }
            if (right && !_rightGripHeld) _rightGripAt = now;

            _leftGripHeld = left;
            _rightGripHeld = right;

            if (left && right && !_gripsConsumed
                && Mathf.Abs(_leftGripAt - _rightGripAt) <= window)
            {
                _gripsConsumed = true;
                _cyclePending = false;
                Aim(false);
                Vr.HeadPose.Recenter();
            }

            // Both have to come up before either hand acts on its own again, or letting go of
            // one grip after a recentre would read as the other being freshly squeezed.
            if (!left && !right) _gripsConsumed = false;
            if (_gripsConsumed) return;

            if (!left)
            {
                if (leftReleased && _cyclePending) CycleItem();
                _cyclePending = false;
            }
            else if (_cyclePending && now - _leftGripAt >= window)
            {
                _cyclePending = false;
                CycleItem();
            }

            Aim(right);
        }

        /// <summary>One step along the item bar, in whichever direction the setting asks for.</summary>
        private void CycleItem()
        {
            if (Plugin.Instance.ItemCycleForward.Value) InputController.SelectItemRightward();
            else InputController.SelectItemLeftward();
        }

        /// <summary>
        /// Focus, level-triggered, because the game tracks the hold itself.
        ///
        /// What the game does to its camera on the way in is left alone: the FOV zoom is
        /// already inert, since under XR the projection comes from the display subsystem rather
        /// than from <c>Camera.fieldOfView</c>, and the shoulder-cam offset is overwritten every
        /// frame by the first-person view. The half that matters — the game holding its aim
        /// where <c>VrAim</c> put it — is the half that still runs.
        /// </summary>
        private void Aim(bool held)
        {
            if (_aimHeld == held || InputController == null) return;
            _aimHeld = held;
            Focusing = held;
            InputController.Aim(held);
        }

        /// <summary>
        /// Y: interact on a tap, the pause menu on a hold.
        ///
        /// Both are on the one button, so interact cannot fire on the press — until it comes
        /// back up there is no telling which of the two was meant. It fires on release instead,
        /// and only when the hold was short. The timer runs on unscaled time, because opening
        /// the pause menu is precisely the thing that stops the scaled one.
        /// </summary>
        private void Interact()
        {
            var y = _input.Pressed(VrInput.Hand.Left, VrInput.Button.Secondary);

            if (y && !_yHeld)
            {
                _yDownAt = Time.unscaledTime;
                _pauseFired = false;
            }
            else if (y && !_pauseFired
                     && Time.unscaledTime - _yDownAt >= Plugin.Instance.PauseHoldSeconds.Value)
            {
                _pauseFired = true;
                if (!_gameUi.OpenSceneMenu())
                    Plugin.Log.LogInfo("nothing here to pause; no scene menu is bound");
            }
            else if (!y && _yHeld && !_pauseFired)
            {
                InputController.Interact();
            }

            _yHeld = y;
        }

        /// <summary>
        /// The rest of the on-foot controls.
        ///
        /// Each one calls the method the game's own bindings call, so nothing downstream can
        /// tell a Touch controller from a pad. The split between edge and level is the game's,
        /// not ours: `Jump`, `Dodge`, `UseItem` and `Chant` are one-shots and must fire on the
        /// press only, while `Shoot` and `Dash` take a held flag because the game tracks the
        /// hold itself and needs to be told when it ends.
        ///
        /// A and B are on the right controller, X and Y on the left; the action set maps both
        /// pairs to the same primary/secondary actions, so the hand is what picks between them.
        /// </summary>
        private void Actions()
        {
            if (InputController == null) return;

            // A — jump
            var jump = _input.Pressed(VrInput.Hand.Right, VrInput.Button.Primary);
            if (jump && !_jumpHeld) InputController.Jump();
            _jumpHeld = jump;

            // B — dodge
            DodgeButton();

            // X — use the selected item
            var useItem = _input.Pressed(VrInput.Hand.Left, VrInput.Button.Primary);
            if (useItem && !_useItemHeld) InputController.UseItem();
            _useItemHeld = useItem;

            // Left trigger — pray, which is how she takes her mana back
            var chant = _input.Pressed(VrInput.Hand.Left, VrInput.Button.Trigger);
            if (chant && !_chantHeld) InputController.Chant();
            _chantHeld = chant;

            // Y — interact, or the pause menu when it is held
            Interact();

            // Right trigger — shoot
            var shoot = _input.Pressed(VrInput.Hand.Right, VrInput.Button.Trigger);
            if (shoot != _shootHeld)
            {
                InputController.Shoot(shoot);
                _shootHeld = shoot;
            }

            // Left stick click — run
            var run = _input.Pressed(VrInput.Hand.Left, VrInput.Button.StickClick);
            if (run != _runHeld)
            {
                InputController.Dash(run);
                _runHeld = run;
            }
        }

        /// <summary>
        /// B, on its own, because it is also live while she gets up from a knockdown.
        /// </summary>
        private void DodgeButton()
        {
            if (InputController == null) return;

            var dodge = _input.Pressed(VrInput.Hand.Right, VrInput.Button.Secondary);
            if (dodge && !_dodgeHeld)
            {
                if (Plugin.Instance.DodgeAlwaysBackstep.Value) DodgeAsBackstep();
                else InputController.Dodge();
            }
            _dodgeHeld = dodge;
        }

        /// <summary>
        /// Fires the dodge as the backward hop rather than the roll.
        ///
        /// The two are one dodge in the game, forked inside <c>InitState</c> on whether she is
        /// moving -- see <see cref="DodgeBackstepPatches"/>, which is where that fork is
        /// answered. All this has to do is say when: the flag is raised for the one synchronous
        /// call and lowered again, so nothing else in the game can see it.
        ///
        /// The stick is deliberately left as it is. The game reads it on the way in to decide
        /// whether the dodge may come out at all -- the recovery out of being thrown into the
        /// air is one of the moves that needs a direction held -- and centring it for the call
        /// refused exactly those.
        /// </summary>
        private void DodgeAsBackstep()
        {
            DodgeBackstepPatches.Forcing = true;
            try
            {
                InputController.Dodge();
            }
            finally
            {
                DodgeBackstepPatches.Forcing = false;
            }
        }

        /// <summary>
        /// Feeds the left stick to the game every frame.
        ///
        /// The game's own binding is event-driven — Move is called when an input action
        /// changes, not once per frame — so replacing its argument would only work while
        /// something else was already moving. Calling it ourselves each frame is what gives
        /// continuous movement. The zero is sent once on release rather than every frame, so
        /// that a centred stick stops Nobeta without also fighting the keyboard for the rest of
        /// the session.
        /// </summary>
        private void Move()
        {
            if (InputController == null) return;

            var stick = _input.LeftStick;
            var dead = Plugin.Instance.MoveDeadzone.Value;

            if (stick.magnitude < dead)
            {
                if (_wasMoving)
                {
                    _wasMoving = false;
                    InputController.Move(Vector2.zero);
                }
                return;
            }

            _wasMoving = true;

            // Rescale past the dead zone so the first millimetre of travel is not a full step.
            var scaled = stick.normalized * Mathf.InverseLerp(dead, 1f, stick.magnitude);
            InputController.Move(scaled);
        }

        /// <summary>
        /// Whether the right stick is still finishing a gesture that belonged to something
        /// else, and must not be read as a turn.
        ///
        /// The spell wheel is the case that made this necessary. Both of its gestures end with
        /// the stick pushed at a spell: the hold ends when the click comes up, the tap-latched
        /// one when the stick starts coming home. Either way the wheel closes on a frame where
        /// the thumb is still over — so the very next frame handed that same deflection to the
        /// turn control, and choosing a spell also spun the player round. The stick was doing
        /// one thing throughout; only its owner changed.
        ///
        /// <para>
        /// So a gesture has to end before turning begins, and the end of a gesture is the stick
        /// coming home — not a timer, which would either cut a slow thumb off or let a fast one
        /// through. The same lock is taken by the two menus for the same reason: a menu closed
        /// with the stick over is a stick nobody has let go of yet.
        /// </para>
        ///
        /// Snap turn is re-armed on the way out rather than left as it was found, because the
        /// press that would have armed it is the press this withheld.
        /// </summary>
        private bool TurnHeldBack()
        {
            if (!_turnLocked) return false;

            if (Mathf.Abs(_input.RightStick.x) >= Plugin.Instance.TurnDeadzone.Value) return true;

            _turnLocked = false;
            _snapArmed = true;
            return false;
        }

        /// <summary>
        /// Right stick turns the view, by snapping in steps or sweeping smoothly.
        ///
        /// Snapping re-arms only once the stick has returned near centre, so holding it over
        /// gives one step rather than a spin. That is the whole reason snap turning is
        /// comfortable, and it is easy to leave out.
        /// </summary>
        private void Turn()
        {
            if (Camera == null) return;

            var cfg = Plugin.Instance;
            var x = _input.RightStick.x;

            if (cfg.SmoothTurn.Value)
            {
                if (Mathf.Abs(x) < cfg.TurnDeadzone.Value) return;
                Camera.g_fX += x * cfg.SmoothTurnSpeed.Value * Time.deltaTime;
                return;
            }

            if (Mathf.Abs(x) < cfg.TurnDeadzone.Value)
            {
                _snapArmed = true;
                return;
            }

            if (!_snapArmed) return;
            _snapArmed = false;
            Camera.g_fX += Mathf.Sign(x) * cfg.SnapTurnDegrees.Value;
        }
    }
}
