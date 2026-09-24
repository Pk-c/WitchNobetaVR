using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Switches the game's depth of field off, because in a headset it is aimed at the wrong
    /// eyes.
    ///
    /// Depth of field picks a focus distance and blurs everything else. On a monitor that is a
    /// camera doing what a camera does, and your eye goes where the shot wants it. In VR there
    /// is no shot: your eyes converge and focus wherever you look, and an image that is already
    /// blurred where you chose to look is one your eyes cannot resolve however hard they try.
    /// It reads as a headset that will not focus rather than as an effect.
    ///
    /// <para>
    /// It also takes the interface with it. The HUD is a quad hanging a metre or so in front of
    /// you, so it is in the image the pipeline blurs like anything else at that distance — and
    /// during a cutscene, focused across the room, the dialogue box is blurred with the scenery
    /// and cannot be read. That is the pipeline being right and the result being unusable, which
    /// is the shape of most of what this mod has to undo.
    /// </para>
    ///
    /// <para>
    /// Only the one override is touched. Colour grading, bloom, vignette and the rest are the
    /// game's look and survive untouched; this is the single effect whose whole premise is a
    /// viewer who cannot choose where to look. Every override switched off is remembered, so
    /// turning the setting back on in the VR menu puts them all back live.
    /// </para>
    /// </summary>
    internal static class FocusBlur
    {
        /// <summary>The override's type name, which is how it is picked out of a profile.</summary>
        private const string DepthOfField = "DepthOfField";

        private const float Every = 1f;

        private static readonly List<VolumeComponent> Silenced = new();

        private static float _nextScan;
        private static PlayerCamera.CameraMode _lastMode = PlayerCamera.CameraMode.Normal;
        private static bool _wasDisabling;

        public static void Tick()
        {
            var wanted = Plugin.Instance.DisableDepthOfField.Value;

            if (!wanted)
            {
                if (_wasDisabling) Restore();
                _wasDisabling = false;
                return;
            }

            _wasDisabling = true;

            // A cutscene brings a volume of its own with it -- the game's own scene grading,
            // switched on at a higher priority than the level's -- so the moment the camera
            // stops being the player's is exactly the moment a new depth of field appears. On
            // the timer alone that arrives up to a second late, which is a second of unreadable
            // dialogue at the start of every conversation. The mode change is the cue.
            var mode = BodyFacing.Mode;
            var moved = mode != _lastMode;
            _lastMode = mode;

            if (!moved && Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + Every;

            Scan();
        }

        /// <summary>
        /// Finds every volume in the scene and silences the depth of field in each.
        ///
        /// Scanned rather than found once: volumes come and go with the stage and with the
        /// scene being played, and the one that matters most is the one a cutscene creates
        /// after everything else already exists.
        /// </summary>
        private static void Scan()
        {
            var found = Object.FindObjectsOfType(Il2CppType.Of<Volume>());
            if (found == null) return;

            for (var i = 0; i < found.Length; i++)
            {
                var volume = found[i].TryCast<Volume>();
                if (volume == null) continue;

                var profile = volume.profileRef;
                var components = profile != null ? profile.components : null;
                if (components == null) continue;

                for (var c = 0; c < components.Count; c++)
                {
                    var component = components[c];
                    if (component == null || !component.active) continue;
                    if (!IsDepthOfField(component)) continue;

                    component.active = false;
                    Silenced.Add(component);
                }
            }
        }

        private static readonly System.Collections.Generic.HashSet<System.IntPtr> Blur = new();
        private static readonly System.Collections.Generic.HashSet<System.IntPtr> NotBlur = new();

        /// <summary>
        /// Whether one override is the depth of field, answered by class pointer after the
        /// first time each class is seen.
        ///
        /// The name is what identifies it — the URP type is not in the interop assemblies to
        /// cast to — but asking for it is not a comparison, it is a managed <c>Type</c> wrapper
        /// and a string marshalled out of il2cpp, per override, per volume, per scan. A profile
        /// carries a dozen overrides and a stage carries several profiles, and none of their
        /// classes ever change what they are called. So the answer is kept against the class
        /// itself: a raw pointer, hashed, with the string read exactly once per class per
        /// session.
        /// </summary>
        private static bool IsDepthOfField(VolumeComponent component)
        {
            var klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(component.Pointer);

            if (Blur.Contains(klass)) return true;
            if (NotBlur.Contains(klass)) return false;

            if (component.GetIl2CppType().Name == DepthOfField)
            {
                Blur.Add(klass);
                return true;
            }

            NotBlur.Add(klass);
            return false;
        }

        /// <summary>
        /// Puts back every override this switched off, for when the setting is turned off
        /// mid-session. Anything whose volume has since been destroyed is simply dropped.
        /// </summary>
        private static void Restore()
        {
            for (var i = 0; i < Silenced.Count; i++)
            {
                var component = Silenced[i];
                if (component != null) component.active = true;
            }

            Silenced.Clear();
        }
    }
}
