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
    /// <item><term>Right stick</term><description>turn; click and hold for the magic wheel</description></item>
    /// <item><term>A / B</term><description>jump / dodge</description></item>
    /// <item><term>X</term><description>use item</description></item>
    /// <item><term>Y</term><description>interact; held, the pause menu</description></item>
    /// <item><term>Triggers</term><description>left prays, right shoots</description></item>
    /// <item><term>Grips</term><description>left cycles items, right focuses; together, recentre</description></item>
    /// </list>
    ///
    /// Three of those share a control with something else — Y with the pause menu, the grips
    /// with recentring, the right stick with turning — and each of the three is resolved here
    /// rather than by asking the player to be careful. See <see cref="Interact"/>,
    /// <see cref="Grips"/> and <see cref="MagicWheel"/>.
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

        private readonly VrInput _input = new();
        private readonly Ui.GameUiInput _gameUi = new();

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

        private bool _wheelOpen;

        private void Awake() => Instance = this;

        private void Update()
        {
            // Before anything reads it, so the camera and room-scale work from one sample.
            Vr.HeadPose.Sample();

            _input.Poll();

            // Which of the gates below the frame took is the one thing a stuck cutscene cannot
            // be told from inside the headset, and every one of them is silent. See
            // CutsceneProbe; it costs a string per frame and writes nothing unless asked.
            Diagnostics.CutsceneProbe.Note(Drive());
            Diagnostics.CutsceneProbe.Tick(_gameUi.InputManager);
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
            if (Ui.VrMenu.Instance != null && Ui.VrMenu.Instance.IsOpen) { StandDown(); return "vr menu"; }

            // The wheel is held open by us rather than by the game's menu stack, so it is
            // settled before that gate rather than behind it. Were it behind, anything binding
            // a UI controller while the wheel was up would leave it open with nothing left able
            // to close it.
            var wheel = MagicWheel();

            if (!wheel && _gameUi.Update(_input)) { StandDown(); return "game menu"; }

            // Dying, waking at a save point and getting back to her feet are the game's to
            // drive, and it does not stop reporting her controllable through them — it holds
            // her with the state machine instead. Without this the player can spin the world
            // and wave her hands about while she is still sitting slumped against the statue,
            // which is the game and the player driving one body at once.
            if (Vr.PlayerStatus.DownOrGettingUp) { StandDown(); return "she is down"; }

            Grips();
            Move();
            if (!wheel) Turn();
            Actions();
            // Room-scale only while she is plainly the player's. A cutscene places her on a
            // mark and then acts around it, so a physical step slides her off it and the scene
            // plays out with her in the wrong place — and nothing in the scene will put her
            // back. The camera mode catches the staged moments and `controllable` catches the
            // rest of them: conversations, doors, pickups. Standing down still calls through,
            // because the offset has to be absorbed rather than banked; see RoomScale.Apply.
            Vr.RoomScale.Apply(Camera != null ? Camera.wizardGirl : null, Vr.VrCamera.ViewYaw,
                               Vr.BodyFacing.Mode == PlayerCamera.CameraMode.Normal
                            && Vr.PlayerStatus.Controllable);

            return wheel ? "magic wheel" : "gameplay";
        }

        /// <summary>
        /// Hands the controllers back when a menu takes them.
        ///
        /// Anything the game is tracking as held has to be told it ended, or it stays latched
        /// with nothing left to clear it: a menu opened mid-trigger leaves Nobeta shooting, and
        /// focus is worse, because nothing on screen says why the aim will not let go. The
        /// one-shot edges are set to what the buttons are actually doing rather than to false,
        /// so a button still down when the menu closes is not read as a fresh press.
        /// </summary>
        private void StandDown()
        {
            // Physical movement is written off for as long as the controllers are handed back,
            // for the reason in RoomScale.Apply: what is not applied has to be spent, or a menu
            // becomes a way to store up a step and cash it in on the way out.
            Vr.RoomScale.Apply(Camera != null ? Camera.wizardGirl : null, Vr.VrCamera.ViewYaw, false);

            if (InputController != null)
            {
                if (_shootHeld) InputController.Shoot(false);
                if (_runHeld) InputController.Dash(false);
                if (_aimHeld) InputController.Aim(false);
                if (_wheelOpen) InputController.AppearMagicMenu(false);
                if (_wasMoving) InputController.Move(Vector2.zero);
            }

            _shootHeld = _runHeld = _aimHeld = _wheelOpen = _wasMoving = false;
            Focusing = false;
            _snapArmed = true;

            _jumpHeld = _input.Pressed(VrInput.Hand.Right, VrInput.Button.Primary);
            _dodgeHeld = _input.Pressed(VrInput.Hand.Right, VrInput.Button.Secondary);
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
        /// The magic wheel, on a held right stick click, pointed at with the same stick.
        ///
        /// The game's own wheel works exactly this way — <c>AppearMagicMenu</c> takes a held
        /// flag and <c>MoveMenuPointer</c> takes the stick — so there is nothing to rebuild,
        /// only somewhere to send it. Turning stands down while it is up, since the stick that
        /// opened the wheel is the stick that has to point around it.
        ///
        /// A right click with the left one already down is the mod's own settings menu opening,
        /// not the wheel. That is checked here rather than by update order, because VrMenu is a
        /// separate component and Unity's order between the two is not ours to decide.
        /// </summary>
        private bool MagicWheel()
        {
            if (InputController == null) return false;

            var open = _input.Pressed(VrInput.Hand.Right, VrInput.Button.StickClick)
                    && !_input.Pressed(VrInput.Hand.Left, VrInput.Button.StickClick);

            if (open != _wheelOpen)
            {
                _wheelOpen = open;
                InputController.AppearMagicMenu(open);
            }

            if (_wheelOpen) InputController.MoveMenuPointer(_input.RightStick);

            return _wheelOpen;
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
            var dodge = _input.Pressed(VrInput.Hand.Right, VrInput.Button.Secondary);
            if (dodge && !_dodgeHeld)
            {
                if (Plugin.Instance.DodgeAlwaysBackstep.Value) DodgeAsBackstep();
                else InputController.Dodge();
            }
            _dodgeHeld = dodge;

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

        /// Fires the dodge as the backward hop rather than the roll.
        ///
        /// The two are one dodge in the game, forked inside <c>Dodge()</c> on whether a
        /// direction is being held -- see <see cref="DodgeBackstepPatches"/>, which is where
        /// that fork is answered. All this has to do is say when: the flag is raised for the
        /// one synchronous call and lowered again, so nothing else in the game can see it.
        ///
        /// The input is centred across the same call as well. Handing the game a centred stick
        /// was the first attempt on its own and it was not enough -- the direction the fork
        /// reads is computed across a frame boundary, so the one already in hand at the press
        /// is the one it answers with -- but as part of the pair it costs nothing and covers
        /// anything downstream that reads the input rather than the predicate. Both fields are
        /// put back straight after, so a walk is not interrupted by a dodge.
        /// </summary>
        private void DodgeAsBackstep()
        {
            var controller = InputController.controller;
            var input = controller != null ? controller.inputData : null;

            var movement = input != null ? input.inputMovement : Vector2.zero;
            var character = input != null ? input.characterMovement : Vector3.zero;

            if (input != null)
            {
                input.inputMovement = Vector2.zero;
                input.characterMovement = Vector3.zero;
            }

            DodgeBackstepPatches.Forcing = true;
            try
            {
                InputController.Dodge();
            }
            finally
            {
                DodgeBackstepPatches.Forcing = false;

                if (input != null)
                {
                    input.inputMovement = movement;
                    input.characterMovement = character;
                }
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
