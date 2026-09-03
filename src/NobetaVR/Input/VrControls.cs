using System;
using UnityEngine;

namespace NobetaVR.Input
{
    /// <summary>
    /// Drives Nobeta from the Touch controllers: left stick to move, right stick to turn.
    ///
    /// Both go through the game's own seams rather than around them. Movement is handed to
    /// <c>PlayerInputController.Move</c>, the same method the game's own bindings call, so
    /// everything downstream — walk and dash states, the animator, the character controller —
    /// behaves exactly as it does on a pad. Turning adds to <c>PlayerCamera.g_fX</c>, the
    /// camera's own yaw, so the view, the direction movement is relative to, and the game's
    /// idea of where you are looking all stay one value instead of three that must be kept in
    /// step.
    /// </summary>
    public sealed class VrControls : MonoBehaviour
    {
        public VrControls(IntPtr ptr) : base(ptr) { }

        internal static VrControls Instance { get; private set; }

        private readonly VrInput _input = new();
        private readonly Ui.GameUiInput _gameUi = new();

        /// <summary>The controllers, for anything else that needs to read them.</summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        internal VrInput Input => _input;

        /// <summary>Captured from a postfix on PlayerInputController.Init.</summary>
        internal PlayerInputController InputController;
        internal PlayerCamera Camera;

        private bool _snapArmed = true;
        private bool _wasMoving;

        // Held state from the previous frame, so a press can be told from a hold. Actions that
        // fire once need the edge; actions the game tracks itself need the level.
        private bool _recenterHeld, _jumpHeld, _dodgeHeld, _shootHeld, _runHeld;

        private void Awake() => Instance = this;

        private void Update()
        {
            // Before anything reads it, so the camera and room-scale work from one sample.
            Vr.HeadPose.Sample();

            _input.Poll();
            if (!_input.Connected) return;

            // Our own menu reads the controllers itself, and a game menu takes them over while
            // it is up. Either way the gameplay bindings stand down: without this the same
            // stick both walks Nobeta and scrolls the menu she is standing in.
            if (Ui.VrMenu.Instance != null && Ui.VrMenu.Instance.IsOpen) return;
            if (_gameUi.Update(_input)) return;

            Recenter();
            Move();
            Turn();
            Actions();
            Vr.RoomScale.Apply(Camera != null ? Camera.wizardGirl : null, Vr.VrCamera.ViewYaw);
        }

        /// <summary>
        /// Puts the head back on Nobeta, on the edge of a press rather than while held.
        /// </summary>
        private void Recenter()
        {
            // Both sticks together opens the VR menu, so a right click with the left one down
            // is not a recentre. Checked here rather than by ordering, because the menu is a
            // separate component and their update order is not ours to decide.
            if (_input.Pressed(VrInput.Hand.Left, VrInput.Button.StickClick))
            {
                _recenterHeld = true;
                return;
            }

            var held = _input.Pressed(VrInput.Hand.Right, VrInput.Button.StickClick);
            if (held && !_recenterHeld) Vr.HeadPose.Recenter();
            _recenterHeld = held;
        }

        /// <summary>
        /// The rest of the on-foot controls.
        ///
        /// Each one calls the method the game's own bindings call, so nothing downstream can
        /// tell a Touch controller from a pad. The split between edge and level is the game's,
        /// not ours: `Jump` and `Dodge` are one-shots and must fire on the press only, while
        /// `Shoot` and `Dash` take a held flag because the game tracks the hold itself and
        /// needs to be told when it ends.
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

            // B — dodge roll
            var dodge = _input.Pressed(VrInput.Hand.Right, VrInput.Button.Secondary);
            if (dodge && !_dodgeHeld) InputController.Dodge();
            _dodgeHeld = dodge;

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
