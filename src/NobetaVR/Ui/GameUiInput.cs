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
    /// calls. Whatever is on screen — pause menu, item wheel, the choice box a conversation
    /// puts up — is behind that same interface, so driving it reaches all of them at once, and
    /// nothing has to be found or special-cased per screen.
    ///
    /// Conversations themselves are the one exception, and it is a real one rather than an
    /// oversight in the above: the line-by-line advance is <c>IStoryController</c> on its own
    /// action map, not the UI controller. See <see cref="Dialogue"/>.
    ///
    /// Which controller is bound changes constantly as menus open and close, so the manager is
    /// asked for the current one every frame rather than one being held on to.
    /// </summary>
    internal sealed class GameUiInput
    {
        private GameInputManager _manager;

        private bool _submitHeld, _cancelHeld, _leftHeld, _rightHeld;
        private bool _nextHeld, _skipHeld, _specialHeld;
        private bool _storyReported;

        // The hold, and which screen was told about it. See Holding.
        private System.IntPtr _holdTarget = System.IntPtr.Zero;
        private bool _holdSent;
        private bool _holdBlocked;

        // Held directions repeat, as they do on a pad: one step, a pause, then a steady stream.
        private Direction2D _held = Direction2D.None;
        private float _repeatAt;

        private const float FirstRepeat = 0.4f;
        private const float ThenEvery = 0.12f;

        /// <summary>
        /// Whether one of the game's own menus is up. The game binds a UI controller only
        /// while something is actually open, so its presence is the answer, and it covers
        /// every screen at once rather than one flag per menu.
        /// </summary>
        public bool MenuOpen => Controller() != null;

        /// <summary>
        /// The game's input manager, for the diagnostics that have to read the same state this
        /// class routes on. Exposed rather than found again, so both are looking at one object.
        /// </summary>
        public GameInputManager InputManager => Manager();

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

                // Nothing to release the hold on any more; forgetting it is what stops the
                // next menu inheriting a press that was never made on it.
                _holdTarget = System.IntPtr.Zero;
                _holdSent = false;

                Dialogue(input);
                return false;
            }

            // A menu owns the buttons while it is up — including the choice box a dialogue can
            // put on screen, where A picks an answer rather than advancing the line.
            SeedDialogueEdges(input);

            Navigate(ui, input);
            Buttons(ui, input);
            return true;
        }

        /// <summary>
        /// Advancing and skipping a conversation.
        ///
        /// Dialogue does not come through <c>IUIController</c> like the menus do — it has its
        /// own <c>IStoryController</c>, with <c>NextDialogue</c> for the line and
        /// <c>SkipMenu</c> for the prompt that offers to skip the scene, and the game drives it
        /// from a separate action map. Without this the conversation simply never advances in
        /// the headset: nothing the mod was sending reached it.
        ///
        /// <para>
        /// The input is not taken over the way a menu takes it, deliberately. The gate below is
        /// a reading of the game's own state, and a gate that is wrong in the open direction
        /// would leave the player unable to move with nothing on screen saying why. Firing
        /// alongside the gameplay bindings cannot do that: during a conversation she is not
        /// controllable, so the jump this shares its button with is already inert.
        /// </para>
        /// </summary>
        private void Dialogue(VrInput input)
        {
            var story = Story();
            if (story == null)
            {
                SeedDialogueEdges(input);
                return;
            }

            if (!_storyReported)
            {
                _storyReported = true;
                Plugin.Log.LogInfo("dialogue bound: A advances the line, B opens the skip menu");
            }

            Edge(input.Pressed(VrInput.Hand.Right, VrInput.Button.Primary),
                 ref _nextHeld, story.NextDialogue);
            Edge(input.Pressed(VrInput.Hand.Right, VrInput.Button.Secondary),
                 ref _skipHeld, story.SkipMenu);
        }

        /// <summary>
        /// Holds the dialogue buttons at whatever they are actually doing while no conversation
        /// is listening, so a button already down when one starts is not read as a fresh press
        /// and does not eat the first line.
        /// </summary>
        private void SeedDialogueEdges(VrInput input)
        {
            _nextHeld = input.Pressed(VrInput.Hand.Right, VrInput.Button.Primary);
            _skipHeld = input.Pressed(VrInput.Hand.Right, VrInput.Button.Secondary);
        }

        /// <summary>
        /// The story controller, but only while the game is actually listening to it.
        ///
        /// Bound is not the same as listening: the manager holds a story controller for as long
        /// as the stage's UI exists, and what says a conversation is running is which action map
        /// the game has switched to. So the enabled map is the gate and the reference is only
        /// the target — reading the reference alone would advance a dialogue that is not there
        /// every time the player jumped.
        /// </summary>
        private IStoryController Story()
        {
            var manager = Manager();
            if (manager == null) return null;

            var map = manager.storyActionMap;
            if (map == null || !map.enabled) return null;

            return manager.storyController;
        }

        /// <summary>
        /// Opens the game's pause menu, the way the game's own Escape binding does.
        ///
        /// The pause menu is not something to be found and switched on: it is behind
        /// <c>ISceneMenuController</c>, which the game rebinds as scenes come and go, exactly
        /// like the UI controller above. Asking the manager for the current one each time is
        /// therefore both the call and the check — there is no scene menu to open on the title
        /// screen or mid-cutscene, and a null is the game saying so rather than a fault.
        /// </summary>
        public bool OpenSceneMenu()
        {
            var manager = Manager();
            var menu = manager != null ? manager.sceneMenuController : null;
            if (menu == null) return false;

            menu.OpenSceneMenu();
            return true;
        }

        private GameInputManager Manager()
        {
            if (_manager == null)
            {
                var found = UnityEngine.Object.FindObjectOfType(Il2CppType.Of<GameInputManager>());
                _manager = found != null ? found.TryCast<GameInputManager>() : null;
            }
            return _manager;
        }

        private IUIController Controller()
        {
            var manager = Manager();
            return manager != null ? manager.uiController : null;
        }

        private void Navigate(IUIController ui, VrInput input)
        {
            var step = MenuStick.Step(input.LeftStick);

            var direction = step.y > 0 ? Direction2D.Up
                          : step.y < 0 ? Direction2D.Down
                          : step.x > 0 ? Direction2D.Right
                          : step.x < 0 ? Direction2D.Left
                          : Direction2D.None;

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
            Edge(input.Pressed(VrInput.Hand.Left, VrInput.Button.Trigger), ref _specialHeld, ui.SpecialAction);

            Holding(ui, input.Pressed(VrInput.Hand.Right, VrInput.Button.Trigger));
        }

        /// <summary>
        /// Spending souls at a statue: the right trigger, held.
        ///
        /// This is the one control on <c>IUIController</c> that is a level rather than an
        /// edge, and it had nothing sending it. Levelling up and trading are both a hold —
        /// <c>UIUpgrade</c> and <c>UITrade</c> override <c>Hold</c>, and underneath it
        /// <c>UIUpgradeHandler</c> is <c>StartUpgrade</c> / <c>KeepUpgrade</c> /
        /// <c>CancelUpgrade</c> against a running cost — so with nothing bound, the whole
        /// upgrade screen was reachable and inert: you could select a stat and there was no
        /// way to buy it.
        ///
        /// Sent on change rather than every frame. The game counts souls out for as long as
        /// it is holding, and it is the transitions it acts on.
        /// </summary>
        private void Holding(IUIController ui, bool down)
        {
            // Menus come and go constantly, and the new one has heard nothing. A trigger
            // already down when one opens is not a press *on it* — without this, opening the
            // upgrade screen with the trigger still held from whatever opened it would start
            // spending immediately.
            if (ui.Pointer != _holdTarget)
            {
                _holdTarget = ui.Pointer;
                _holdSent = false;
                _holdBlocked = down;
            }

            if (!down) _holdBlocked = false;

            var wanted = down && !_holdBlocked;
            if (wanted == _holdSent) return;

            _holdSent = wanted;
            ui.Hold(wanted);
        }

        private static void Edge(bool now, ref bool before, System.Action fire)
        {
            if (now && !before) fire();
            before = now;
        }
    }
}
