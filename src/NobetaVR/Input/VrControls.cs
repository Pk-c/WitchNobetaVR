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

        /// <summary>Captured from a postfix on PlayerInputController.Init.</summary>
        internal PlayerInputController InputController;
        internal PlayerCamera Camera;

        private bool _snapArmed = true;
        private bool _wasMoving;

        private void Awake() => Instance = this;

        private void Update()
        {
            _input.Poll();
            if (!_input.Connected) return;

            Move();
            Turn();
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
