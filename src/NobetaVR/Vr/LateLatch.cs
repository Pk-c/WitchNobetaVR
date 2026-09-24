using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Re-reads the headset an instant before the frame is drawn, so the pose the world is
    /// rendered from is the same pose the compositor is told it was rendered from.
    ///
    /// Everything else in the mod places the view from <c>LateUpdate</c> — from the postfix on
    /// <c>PlayerCamera.Update</c> in game, from <c>VrCamera.LateUpdate</c> on menus. Unity
    /// updates its tracked poses twice a frame, though: once at the top of the frame, which is
    /// what a <c>LateUpdate</c> reads, and again at BeforeRender, which is what the renderer
    /// and the projection layer submitted to OpenXR use. So the camera transform carried a pose
    /// one update old while the runtime was told the frame belonged to the newer one, and the
    /// compositor's reprojection duly corrected for a difference that was not there.
    ///
    /// A constant lag would read as lag. This one is not constant — it is whatever the head did
    /// between the two updates — so it reads as the world shivering, at its worst while the
    /// head is moving fastest and gone entirely while it is still. That is the tremor on a nod,
    /// and the reason it shows first on near, high-contrast things: the hands and the interface.
    ///
    /// <para>
    /// <b>Why this is a Harmony patch and not a camera message.</b> <c>OnPreCull</c> is the
    /// obvious hook and this was written that way first. It never fired once, and the reason
    /// was already in the mod's own startup report: <c>render pipeline URP_Advanced</c>. Unity
    /// does not send <c>OnPreCull</c>, <c>OnPreRender</c> or <c>OnPostRender</c> under a
    /// scriptable render pipeline at all — they are built-in-pipeline messages, and a component
    /// waiting for one under URP is not late, it is never called. <c>Application.onBeforeRender</c>
    /// is out for a different reason: its add accessor is a managed method the game never
    /// calls, so IL2CPP stripped it (see <c>VrHands.LateUpdate</c>), and the same doubt hangs
    /// over <c>RenderPipelineManager.beginCameraRendering</c>.
    /// </para>
    ///
    /// <para>
    /// So the pipeline's own entry point is patched instead. That method is not optional
    /// decoration a build might have dropped — it is how anything gets drawn at all, so its
    /// body is certainly present and certainly called, which is exactly the property the two
    /// event routes could not promise.
    /// </para>
    /// </summary>
    internal static class LateLatch
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Static | BindingFlags.Instance;

        /// <summary>
        /// Finds a render entry point and hangs the latch on it, most precise first.
        ///
        /// Three candidates because the shape of URP's insides is a fact about the package
        /// version this game shipped, not something to assume: <c>RenderCameraStack</c> and
        /// <c>RenderSingleCamera</c> both name the camera about to be drawn, which keeps the
        /// latch honest about which view it is refreshing; <c>Render</c> is the whole frame and
        /// names nothing, so it latches once and lets <see cref="VrCamera"/>'s own frame guard
        /// decide. Any of the three is late enough — all of them run after every LateUpdate.
        /// </summary>
        internal static void Install(Harmony harmony)
        {
            var pipeline = typeof(UniversalRenderPipeline);

            if (TryPatch(harmony, pipeline, "RenderCameraStack", nameof(BeforeCamera), true)) return;
            if (TryPatch(harmony, pipeline, "RenderSingleCamera", nameof(BeforeCamera), true)) return;
            if (TryPatch(harmony, pipeline, "Render", nameof(BeforeFrame), false)) return;

            Plugin.Log.LogError("No URP render entry point could be patched, so the head pose "
                              + "stays at its LateUpdate age; expect the view to shimmer while "
                              + "the head is moving.");
        }

        /// <summary>
        /// The camera about to be drawn is the second declared argument on both camera-shaped
        /// candidates, so it is taken positionally. Harmony's <c>__1</c> indexes the declared
        /// parameters and skips <c>this</c>, which is what lets one prefix serve a static
        /// method and an instance method alike — and taking it by position rather than by name
        /// means nothing here depends on IL2CPP having kept the parameter names.
        ///
        /// The first argument is a <c>ScriptableRenderContext</c> and is deliberately not
        /// declared: it is a by-value struct this has no use for, and not asking for it is one
        /// less thing for the patch to have to marshal correctly.
        /// </summary>
        private static void BeforeCamera(Camera __1) => VrCamera.LateLatchPose(__1);

        /// <summary>The whole-frame fallback, which names no camera; see <see cref="Install"/>.</summary>
        private static void BeforeFrame() => VrCamera.LateLatchPose(null);

        private static bool TryPatch(Harmony harmony, Type type, string name,
                                     string prefix, bool wantsCamera)
        {
            MethodInfo chosen = null;

            foreach (var m in type.GetMethods(Any))
            {
                if (m.Name != name) continue;

                // Overloads are the trap here. URP's public RenderSingleCamera takes a Camera
                // and is for user code; the one the pipeline actually drives itself takes a
                // CameraData. Matching on the argument rather than the name is what tells them
                // apart, and patching the wrong one is a hook that resolves and never fires.
                if (wantsCamera)
                {
                    var ps = m.GetParameters();
                    if (ps.Length != 2 || ps[1].ParameterType != typeof(Camera)) continue;
                }

                chosen = m;
                break;
            }

            if (chosen == null) return false;

            try
            {
                harmony.Patch(chosen, prefix: new HarmonyMethod(
                    typeof(LateLatch).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)));

                Plugin.Log.LogInfo($"head pose latched at render time via {Describe(chosen)}");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"could not patch {Describe(chosen)}: {e.Message}");
                return false;
            }
        }

        private static string Describe(MethodBase m)
        {
            var args = string.Empty;
            foreach (var p in m.GetParameters())
                args += (args.Length > 0 ? ", " : string.Empty) + p.ParameterType.Name;

            return $"{(m.IsStatic ? "static " : string.Empty)}{m.DeclaringType?.Name}.{m.Name}({args})";
        }
    }
}
