using System;
using NobetaVR.Vr;
using NobetaVR.Xr;
using UnityEngine;
using UnityEngine.XR;

// Deliberately NOT `using UnityEngine.SceneManagement`. Assembly-CSharp defines its own
// global `SceneManager` -- the game's per-stage god object, holding PlayerObj, CameraObj,
// stageCam and stageUI -- and it wins name resolution over Unity's. Both are wanted here,
// so Unity's is spelled out in full every time.

namespace NobetaVR.Diagnostics
{
    /// <summary>
    /// The mod's only always-alive component: it brings XR up and keeps the OpenXR message
    /// loop pumped, and reports what the engine says about itself along the way.
    /// </summary>
    public sealed class VrRuntime : MonoBehaviour
    {
        public VrRuntime(IntPtr ptr) : base(ptr) { }

        /// <summary>
        /// The headset's refresh rate, or zero until the display has driven a frame and can be
        /// asked. Anything judging whether a frame arrived late needs it, and this is the one
        /// place in the mod that holds the display subsystem.
        /// </summary>
        internal static float RefreshHz { get; private set; }

        private XrLoader _xr;
        private bool _started;
        private string _scene = string.Empty;
        private bool _announcedRunning;
        private int _framesRunning;

        private void Update()
        {
            // SystemInfo.graphicsDeviceType is only meaningful once a device exists, which is
            // not true during Load(). One frame in, it is -- and the same frame is the
            // earliest point at which starting XR makes any sense.
            if (!_started)
            {
                _started = true;
                if (Plugin.Instance.VerboseStartupReport.Value) Report();
                BeginXr();
            }

            _xr?.Tick();

            // Asked until it answers. The rate is not available before the display has driven
            // a stereo frame, and one float compare a frame afterwards is cheaper than caring
            // exactly when that was.
            if (RefreshHz < 1f && _xr is { CurrentState: XrLoader.State.Running }
             && _xr.Display != null && _xr.Display.TryGetDisplayRefreshRate(out var refresh))
                RefreshHz = refresh;

            // Not on the frame XR starts: the pass count and refresh rate are only populated
            // once the display has actually driven a stereo frame, so reporting immediately
            // prints zeros and reads like a failure. Wait for the first real pass, and give up
            // waiting after a couple of seconds so a genuine zero still gets reported.
            if (!_announcedRunning && _xr is { CurrentState: XrLoader.State.Running })
            {
                _framesRunning++;
                if (_xr.Display.GetRenderPassCount() > 0 || _framesRunning > 120)
                {
                    _announcedRunning = true;
                    ReportDisplay();
                }
            }

            Uncap();

            // Volumes come and go with the stage and with the scene being played, so this is
            // a standing job rather than a one-off. It lives here because it is not welded to
            // the view the way the fade is -- it only has to happen once a frame, somewhere.
            Vr.FocusBlur.Tick();

            // Cheap, and it saves wiring an il2cpp delegate onto sceneLoaded just to learn
            // which of level0..level14 is which.
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (active.name != _scene)
            {
                _scene = active.name;
                Plugin.Log.LogInfo($"scene -> '{_scene}' (build index {active.buildIndex})");
            }
        }

        /// <summary>
        /// Takes the game's frame-rate limit off, and keeps taking it off.
        ///
        /// The game caps itself. `GameSettings` carries an `FPSLimitation` whose only values
        /// are 30, 60 and 120, and a `vSync` that `UpdateVSyncSettings` hands to Unity — which
        /// syncs to the desktop monitor, not to the headset. A 60 Hz screen therefore holds the
        /// game at a flat 60 no matter what the headset asks for.
        ///
        /// On a monitor a cap is a preference. Against a 90 Hz headset it is a third of every
        /// frame you see being invented by the compositor from the one before it, by rotating
        /// the last image to the new head pose — and since this mod submits no depth buffer,
        /// that rotation cannot get parallax right, so the error lands on whatever is nearest.
        /// Which is the interface, a metre and a half from your face. It is not something the
        /// mod can filter out downstream: the frames are simply not there.
        ///
        /// Enforced every frame rather than once, because the game rewrites both values
        /// whenever its own settings change and would quietly take the cap back.
        /// </summary>
        private void Uncap()
        {
            if (!Plugin.Instance.UncapFrameRate.Value) return;

            // Only while the headset is actually being fed. A flat game keeps the frame rate
            // its own settings asked for: there is no compositor to starve, and the cap stops
            // being a comfort problem the moment it stops being a VR one.
            if (_xr is not { CurrentState: XrLoader.State.Running }) return;

            var target = Application.targetFrameRate;
            var vsync = QualitySettings.vSyncCount;
            if (target == -1 && vsync == 0) return;

            // Only when what we found changed, so the game putting the cap back every frame
            // costs one line rather than a log full of them.
            if (target != _capTarget || vsync != _capVsync)
            {
                _capTarget = target;
                _capVsync = vsync;
                Plugin.Log.LogInfo($"frame cap found: targetFrameRate={target}, "
                                 + $"vSyncCount={vsync}. Lifting both — at {RefreshHz:F0} Hz the "
                                 + "compositor would be inventing the difference.");
            }

            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
        }

        private int _capTarget = -1;
        private int _capVsync;

        private void BeginXr()
        {
            try
            {
                _xr = new XrLoader();
                if (!_xr.Initialize()) { _xr = null; return; }

                // Head tracking lives in its own component so that it runs in LateUpdate,
                // after the game's camera code has had its say for the frame.
                gameObject.AddComponent<VrCamera>().Bind(_xr);
            }
            catch (Exception e)
            {
                // A throw here would take the game down with it. The mod failing to reach the
                // headset should leave a flat game running and an explanation in the log.
                Plugin.Log.LogError($"XR initialisation threw, leaving the game flat: {e}");
                _xr = null;
            }
        }

        private void OnApplicationQuit() => _xr?.Shutdown();

        private static void Report()
        {
            var log = Plugin.Log;
            log.LogInfo("---- NobetaVR environment ----");
            log.LogInfo($"  unity            {Application.unityVersion}");
            log.LogInfo($"  graphics API     {SystemInfo.graphicsDeviceType} ({SystemInfo.graphicsDeviceVersion})");
            log.LogInfo($"  graphics device  {SystemInfo.graphicsDeviceName}");
            var srp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            log.LogInfo($"  render pipeline  {(srp != null ? srp.name : "built-in")}");

            try
            {
                log.LogInfo($"  XRSettings.enabled          {XRSettings.enabled}");
                log.LogInfo($"  XRSettings.loadedDeviceName '{XRSettings.loadedDeviceName}'");
                log.LogInfo($"  XRSettings.isDeviceActive   {XRSettings.isDeviceActive}");
                log.LogInfo($"  XRSettings.stereoRenderMode {XRSettings.stereoRenderingMode}");
            }
            catch (Exception e)
            {
                log.LogWarning($"  XRSettings unreadable: {e.Message}");
            }

            log.LogInfo("------------------------------");
        }

        private void ReportDisplay()
        {
            var log = Plugin.Log;
            var d = _xr.Display;
            log.LogInfo("---- XR display ----");
            log.LogInfo($"  running          {d.running}");
            log.LogInfo($"  render passes    {d.GetRenderPassCount()}");
            log.LogInfo($"  single-pass off  {d.singlePassRenderingDisabled}");
            log.LogInfo($"  render scale     {d.scaleOfAllRenderTargets}");
            if (d.TryGetDisplayRefreshRate(out var hz)) log.LogInfo($"  refresh rate     {hz} Hz");
            log.LogInfo($"  XRSettings.enabled {XRSettings.enabled}, device '{XRSettings.loadedDeviceName}'");
            log.LogInfo("--------------------");
        }
    }
}
