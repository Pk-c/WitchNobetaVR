using UnityEngine;

namespace NobetaVR.Diagnostics
{
    /// <summary>
    /// One line a second saying who owns the controllers, for as long as the game is framing a
    /// scene rather than the player.
    ///
    /// A cutscene that will not advance looks the same from inside the headset whatever the
    /// cause, and the causes are far apart: the buttons may be reaching a menu that is quietly
    /// bound, or reaching a story controller the game is not listening to, or not being read at
    /// all because <see cref="Input.VrControls"/> stood down before it got to them. All three
    /// are silent, and none of them can be told from the others by pressing harder.
    ///
    /// So each of the three is written down instead. <see cref="Note"/> records which gate the
    /// input loop took this frame, and the rest is the game's own answer to the same question —
    /// which action map is enabled, which controllers are bound and to what — read live rather
    /// than inferred. Between them one line is enough to say where a press went.
    ///
    /// Off by default and rate-limited to a line a second, because it is here to be turned on
    /// for one reproduction and read afterwards, not to run in a release.
    /// </summary>
    internal static class CutsceneProbe
    {
        private const float Every = 1f;

        /// <summary>
        /// How long the probe keeps writing after the game hands the scene back.
        ///
        /// The last line matters as much as the first: a cutscene that ends leaving a menu
        /// bound, or an action map switched away from and never back, only shows up on the way
        /// out. Stopping on the frame the camera returns to Normal would drop exactly that.
        /// </summary>
        private const float Tail = 3f;

        private static float _next;
        private static float _framingUntil;
        private static string _gate = "-";

        /// <summary>
        /// Records which branch of the input loop this frame took. Called from every exit in
        /// <see cref="Input.VrControls.Update"/>, including the one that reaches the end.
        /// </summary>
        internal static void Note(string gate) => _gate = gate;

        internal static void Tick(GameInputManager manager)
        {
            if (!Plugin.Instance.LogCutsceneState.Value) return;

            var mode = Vr.BodyFacing.Mode;
            if (mode != PlayerCamera.CameraMode.Normal) _framingUntil = Time.unscaledTime + Tail;
            if (Time.unscaledTime > _framingUntil) return;

            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + Every;

            Plugin.Log.LogInfo($"[cutscene] gate={_gate} mode={mode} "
                             + $"state={Vr.PlayerStatus.State?.ToString() ?? "<none>"} "
                             + $"controllable={Vr.PlayerStatus.Controllable} | "
                             + Maps(manager) + " | " + Bound(manager) + " | " + View());

            Plugin.Log.LogInfo("[cutscene] " + Post());
        }

        /// <summary>
        /// What the render pipeline is doing to the whole image, which is what a blurred
        /// interface is really a symptom of.
        ///
        /// The HUD is a quad hanging in the world, so anything the camera does after the scene
        /// is drawn happens to it too. Depth of field is the one that matters: a panel a metre
        /// away, in front of a shot focused on something across the room, is out of focus and
        /// gets blurred exactly as a real object at that distance would. That is the pipeline
        /// being correct, and it is still the interface being unreadable.
        ///
        /// So the two halves are reported separately — whether the camera runs post-processing
        /// at all, and which overrides the active volumes actually carry. If depth of field is
        /// not in the list, the blur came from somewhere else and the fix is somewhere else.
        /// </summary>
        private static string Post()
        {
            var target = Vr.VrCamera.CameraTransform;
            var camera = target != null ? target.GetComponent<Camera>() : null;
            if (camera == null) return "post <no camera>";

            var data = camera.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            var state = data == null
                ? "post <no urp data>"
                : $"post on={data.renderPostProcessing} aa={data.antialiasing} "
                + $"volumeMask=0x{data.volumeLayerMask.value:X}";

            return state + " | volumes " + Volumes();
        }

        /// <summary>
        /// The active volumes, named with the overrides they switch on.
        ///
        /// A volume with no weight is not doing anything, and a profile is a list of overrides
        /// most of which are off, so both are filtered out: what is left is the short list of
        /// effects actually reaching the image this second.
        /// </summary>
        private static string Volumes()
        {
            var found = Object.FindObjectsOfType(
                Il2CppInterop.Runtime.Il2CppType.Of<UnityEngine.Rendering.Volume>());
            if (found == null || found.Length == 0) return "<none>";

            var line = string.Empty;
            var counted = 0;

            for (var i = 0; i < found.Length && counted < 6; i++)
            {
                var volume = found[i].TryCast<UnityEngine.Rendering.Volume>();
                if (volume == null || !volume.isActiveAndEnabled || volume.weight <= 0.001f) continue;

                counted++;
                line += $" '{volume.name}'(w={volume.weight:F2} p={volume.priority:F0})[{Overrides(volume)}]";
            }

            return counted == 0 ? "<none active>" : line.Trim();
        }

        private static string Overrides(UnityEngine.Rendering.Volume volume)
        {
            var profile = volume.profileRef;
            var components = profile != null ? profile.components : null;
            if (components == null) return "<no profile>";

            var names = string.Empty;
            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i];
                if (component == null || !component.active) continue;
                names += (names.Length == 0 ? "" : ",") + component.GetIl2CppType().Name;
            }

            return names.Length == 0 ? "-" : names;
        }

        /// <summary>
        /// Which action map the game has enabled, which is how it decides where a button goes.
        /// <c>SwitchActionMap</c> enables exactly one, so this is the routing rather than a
        /// symptom of it.
        /// </summary>
        private static string Maps(GameInputManager manager)
        {
            if (manager == null) return "maps <no manager>";

            return $"maps play={Enabled(manager.gameplayActionMap)} "
                 + $"ui={Enabled(manager.uiControlActionMap)} "
                 + $"story={Enabled(manager.storyActionMap)} "
                 + $"last='{manager.lastActionMap}'";
        }

        private static string Enabled(UnityEngine.InputSystem.InputActionMap map)
            => map == null ? "-" : map.enabled ? "on" : "off";

        /// <summary>
        /// Which controllers are bound, named by the object carrying them.
        ///
        /// The name rather than the type, because the type is what would need a decompiler and
        /// the name is what the log can point at: a UI controller bound during a conversation
        /// is the whole finding, and knowing which object bound it says where to look next.
        /// </summary>
        private static string Bound(GameInputManager manager)
        {
            if (manager == null) return "bound <no manager>";

            return $"bound ui={Who(manager.uiController)} "
                 + $"story={Who(manager.storyController)} "
                 + $"menu={Who(manager.sceneMenuController)}";
        }

        private static string Who(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase controller)
        {
            if (controller == null) return "<none>";

            var component = controller.TryCast<Component>();
            return component != null ? Path(component.transform) : "<bound>";
        }

        /// <summary>
        /// Where the view actually ended up, which is the other half of a broken scene.
        ///
        /// The camera the mod drives and the camera the scene renders through are two different
        /// questions, and a cutscene is exactly where they come apart: if the scene frames
        /// itself through a camera of its own, the head pose lands on a transform nothing looks
        /// at, and the view reads as placed at random. So both are named, and the one actually
        /// rendering is the one with the highest depth.
        /// </summary>
        private static string View()
        {
            var target = Vr.VrCamera.CameraTransform;
            var where = target == null
                ? "<no camera>"
                : $"'{target.name}' ({target.position.x:F1}, {target.position.y:F1}, "
                + $"{target.position.z:F1})";

            return $"view driving={where} gameDrives={Vr.VrCamera.GameDriving} "
                 + $"standsBack={Vr.VrCamera.ViewStandsBack} rendering={Rendering(target)}";
        }

        /// <summary>The enabled camera the game is actually presenting, and whether it is ours.</summary>
        private static string Rendering(Transform target)
        {
            Camera top = null;
            var cameras = Camera.allCameras;
            if (cameras != null)
                foreach (var camera in cameras)
                    if (camera != null && camera.enabled
                        && (top == null || camera.depth > top.depth)) top = camera;

            if (top == null) return "<none>";

            var mine = target != null && ReferenceEquals(top.transform, target);
            return $"'{top.name}' depth={top.depth}{(mine ? "" : " NOT-THE-ONE-WE-DRIVE")}";
        }

        private static string Path(Transform t)
        {
            var path = t.name;
            for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }
    }
}
