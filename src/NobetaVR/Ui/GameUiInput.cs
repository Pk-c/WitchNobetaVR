using Il2CppInterop.Runtime;
using MarsSDK;
using NobetaVR.Input;
using UnityEngine;

namespace NobetaVR.Ui
{
    /// <summary>
    /// Drives the game's own menus from the Touch controllers.
    ///
    /// The game routes every menu through <c>GameInputManager.uiController</c>, an
    /// <c>IUIController</c> with <c>Move</c>, <c>Submit</c>, <c>Cancel</c> and the two switch
    /// calls. Whatever is on screen — pause menu, item wheel, dialogue — is behind that same
    /// interface, so driving it reaches all of them at once, and nothing has to be found or
    /// special-cased per screen.
    ///
    /// Which controller is bound changes constantly as menus open and close, so the manager is
    /// asked for the current one every frame rather than one being held on to.
    /// </summary>
    internal sealed class GameUiInput
    {
        private GameInputManager _manager;

        private bool _submitHeld, _cancelHeld, _leftHeld, _rightHeld;

        // Held directions repeat, as they do on a pad: one step, a pause, then a steady stream.
        private Direction2D _held = Direction2D.None;
        private float _repeatAt;

        private const float FirstRepeat = 0.4f;
        private const float ThenEvery = 0.12f;

        /// <summary>
        /// Returns true when a menu was up and took the input, so the gameplay bindings can
        /// stand down. The game only binds a UI controller while something is actually open,
        /// which makes that check the gate as well as the target.
        /// </summary>
        public bool Update(VrInput input)
        {
            var ui = Controller();
            if (ui == null)
            {
                _held = Direction2D.None;
                return false;
            }

            Navigate(ui, input);
            Buttons(ui, input);
            return true;
        }

        private IUIController Controller()
        {
            if (_manager == null)
            {
                var found = UnityEngine.Object.FindObjectOfType(Il2CppType.Of<GameInputManager>());
                _manager = found != null ? found.TryCast<GameInputManager>() : null;
            }
            return _manager != null ? _manager.uiController : null;
        }

        private void Navigate(IUIController ui, VrInput input)
        {
            var stick = input.LeftStick;
            var dead = Plugin.Instance.MenuDeadzone.Value;

            var direction = Direction2D.None;
            if (Mathf.Abs(stick.y) > Mathf.Abs(stick.x))
            {
                if (stick.y > dead) direction = Direction2D.Up;
                else if (stick.y < -dead) direction = Direction2D.Down;
            }
            else
            {
                if (stick.x > dead) direction = Direction2D.Right;
                else if (stick.x < -dead) direction = Direction2D.Left;
            }

            if (direction == Direction2D.None)
            {
                _held = Direction2D.None;
                return;
            }

            if (direction != _held)
            {
                _held = direction;
                _repeatAt = Time.unscaledTime + FirstRepeat;
                ui.Move(direction);
                return;
            }

            if (Time.unscaledTime < _repeatAt) return;
            _repeatAt = Time.unscaledTime + ThenEvery;
            ui.Move(direction);
        }

        private void Buttons(IUIController ui, VrInput input)
        {
            Edge(input.Pressed(VrInput.Hand.Right, VrInput.Button.Primary), ref _submitHeld, ui.Submit);
            Edge(input.Pressed(VrInput.Hand.Right, VrInput.Button.Secondary), ref _cancelHeld, ui.Cancel);
            Edge(input.Pressed(VrInput.Hand.Left, VrInput.Button.Grip), ref _leftHeld, ui.SwitchLeftward);
            Edge(input.Pressed(VrInput.Hand.Right, VrInput.Button.Grip), ref _rightHeld, ui.SwitchRightward);
        }

        private static void Edge(bool now, ref bool before, System.Action fire)
        {
            if (now && !before) fire();
            before = now;
        }
    }
}
