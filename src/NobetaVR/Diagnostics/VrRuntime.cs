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

            // Cheap, and it saves wiring an il2cpp delegate onto sceneLoaded just to learn
            // which of level0..level14 is which.
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (active.name != _scene)
            {
                _scene = active.name;
                Plugin.Log.LogInfo($"scene -> '{_scene}' (build index {active.buildIndex})");
            }
        }

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
