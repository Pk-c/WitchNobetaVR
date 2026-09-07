using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.SubsystemsImplementation;
using UnityEngine.XR;
using Native = NobetaVR.Xr.UnityOpenXrNative;

namespace NobetaVR.Xr
{
    /// <summary>
    /// Brings Unity's own XR display subsystem up inside a game that was built without XR.
    ///
    /// This is a reimplementation of <c>OpenXRLoaderBase</c> from com.unity.xr.openxr@1.6.0,
    /// following that class step for step, because the original cannot run here (see
    /// <see cref="UnityOpenXrNative"/>). Where this file departs from the package, it says so
    /// and why.
    ///
    /// The engine side needs nothing patched: LittleWitchNobeta ships UnityEngine.XRModule
    /// intact and merely dormant. Dropping the provider's native DLL and its subsystem
    /// manifest into the game folder is what makes an "OpenXR Display" descriptor exist;
    /// everything below is the sequence that turns that descriptor into a running headset.
    /// </summary>
    internal sealed class XrLoader
    {
        public enum State
        {
            Idle,
            Failed,
            Initialized,   // instance up, subsystems created, waiting for the runtime to say XrReady
            Running,       // display subsystem started, frames going to the headset
            Stopped,
        }

        public State CurrentState { get; private set; } = State.Idle;
        public XRDisplaySubsystem Display { get; private set; }
        public XRInputSubsystem Input { get; private set; }

        private Native.NativeEvent _sessionState;
        private bool _sessionEverReady;
        private bool _waitingLogged;
        private bool _actionsAttached;

        // The native side keeps a raw function pointer to this delegate for the life of the
        // session. Letting it be collected would leave the runtime calling freed memory, so
        // the reference is held here rather than passed inline.
        private static Native.ReceiveNativeEventDelegate _eventCallback;
        private static XrLoader _instance;

        /// <summary>
        /// Where the provider's binaries live: the game's own native plugin folder, which is
        /// also where UnitySubsystemsManifest.json told the engine to look.
        /// </summary>
        public static string PluginDir =>
            Path.Combine(Application.dataPath, "Plugins", "x86_64");

        public bool Initialize()
        {
            if (CurrentState == State.Initialized || CurrentState == State.Running) return true;

            _instance = this;
            var log = Plugin.Log;

            var loader = Path.Combine(PluginDir, "openxr_loader.dll");
            var provider = Path.Combine(PluginDir, "UnityOpenXR.dll");
            if (!File.Exists(loader) || !File.Exists(provider))
            {
                log.LogError($"OpenXR provider missing from '{PluginDir}'. Run deploy.ps1 -Loader.");
                CurrentState = State.Failed;
                return false;
            }

            Native.InstallResolver(PluginDir);
            PinRuntime();

            // Before anything that can fail, so that a failure has a report to be found in.
            Native.StartReport();

            if (!LoadLoaderLibrary(loader))
            {
                CurrentState = State.Failed;
                return false;
            }

            // The package calls this "hooking", which undersells it. It is what loads the
            // provider's stage 1 and resolves its OpenXR function table, and it has to happen
            // after the loader library is open and before anything asks OpenXR a question.
            HookGetInstanceProcAddr();

            // Package order, kept deliberately: the "session" here is internal bookkeeping,
            // not the XrSession -- that one is created later, by CreateSessionIfNeeded.
            if (!Native.InitializeSession())
            {
                log.LogError($"session_InitializeSession failed. {Native.LastError() ?? "no detail from the runtime"}");
                CurrentState = State.Failed;
                return false;
            }

            SetApplicationInfo();

            Native.GetRuntimeVersion(out var rtMajor, out var rtMinor, out var rtPatch);
            log.LogInfo($"OpenXR runtime: '{Native.RuntimeName() ?? "unnamed"}' {rtMajor}.{rtMinor}.{rtPatch}");

            // No features are requested. The package uses features to enable optional OpenXR
            // extensions; a plain stereo display with head tracking needs none of them, and
            // every feature we do not ask for is a way this can fail that we do not have.
            _eventCallback = OnNativeEvent;
            Native.SetCallbacks(_eventCallback);

            ApplyRenderSettings();

            if (!CreateSubsystems())
            {
                DumpDiagnosticReport("subsystem creation failed");
                CurrentState = State.Failed;
                return false;
            }

            var runtime = Native.RuntimeName();
            log.LogInfo($"OpenXR initialised on runtime '{runtime ?? "unknown"}', render mode {Native.GetRenderMode()}");

            CurrentState = State.Initialized;

            // XR Management would call Start() straight after Initialize(), so we do too.
            StartSession();
            return true;
        }

        /// <summary>
        /// Drives the OpenXR message loop. Must be called every frame.
        ///
        /// The package hangs this on <c>Application.onBeforeRender</c>. We call it from a
        /// MonoBehaviour instead: subscribing an il2cpp delegate to that event buys nothing
        /// here, and one frame of extra latency at this stage is not worth the fragility.
        ///
        /// Pumping is also what delivers the session state changes, so the path out of
        /// "waiting for the headset" runs through here.
        /// </summary>
        public void Tick()
        {
            if (CurrentState != State.Initialized && CurrentState != State.Running) return;

            Native.PumpMessageLoop();
        }

        /// <summary>
        /// Creates the XrSession and, once the runtime says it is ready, starts the
        /// subsystems.
        ///
        /// This is called twice, and that is the design rather than a retry. There is a
        /// chicken-and-egg here that cost a full test cycle to see: the runtime only reports
        /// XrReady for a session that exists, so waiting for XrReady before creating one waits
        /// forever. The package resolves it by calling StartInternal unconditionally --
        /// "in order to get XrReady, we have to at least attempt to create the session" -- and
        /// then calling it a second time from the XrReady event. So: the first call creates
        /// the session and returns, the pump delivers XrReady, and the second call finishes
        /// the job.
        /// </summary>
        private void StartSession()
        {
            if (CurrentState == State.Running) return;

            var log = Plugin.Log;

            if (!Native.CreateSessionIfNeeded())
            {
                log.LogError($"session_CreateSessionIfNeeded failed. {Native.LastError() ?? "no detail"}");
                DumpDiagnosticReport("session creation failed");
                CurrentState = State.Failed;
                return;
            }

            if (_sessionState != Native.NativeEvent.XrReady)
            {
                if (!_waitingLogged)
                {
                    _waitingLogged = true;
                    log.LogInfo($"Session created; waiting for the runtime to make it ready "
                              + $"(state {_sessionState}).");
                }
                return;
            }

            // Order is load-bearing and comes from the package: the display subsystem has to
            // be running before xrBeginSession, and input after it, because input needs the
            // session object the display half establishes.
            Display?.Start();
            if (Display == null || !Display.running)
            {
                log.LogError("The display subsystem would not start.");
                DumpDiagnosticReport("display subsystem would not start");
                CurrentState = State.Failed;
                return;
            }

            Native.BeginSession();

            // Order from the package, and the runtime requires it: actions cannot be attached
            // to a session that has not begun, and the input subsystem has no devices to
            // enumerate until they are.
            if (!_actionsAttached) _actionsAttached = XrActionSet.Attach();

            Input?.Start();
            Native.SetSuccessfullyInitialized(true);

            CurrentState = State.Running;
            _sessionEverReady = true;

            log.LogInfo($"XR running. XRSettings.enabled={XRSettings.enabled}, "
                      + $"device='{XRSettings.loadedDeviceName}', mode={XRSettings.stereoRenderingMode}");
        }

        public void Shutdown()
        {
            if (CurrentState == State.Idle) return;

            try
            {
                if (CurrentState == State.Running)
                {
                    Input?.Stop();
                    Display?.Stop();
                    Native.EndSession();
                }

                Native.DestroySession();
                Native.UnloadOpenXRLibrary();
            }
            catch (Exception e)
            {
                // Shutdown runs on the way out of the process; a throw here would turn a
                // clean exit into a crash report the player would reasonably blame on the mod.
                Plugin.Log.LogWarning($"XR shutdown was untidy: {e.Message}");
            }

            CurrentState = State.Stopped;
        }

        // -- setup steps ---------------------------------------------------------------

        /// <summary>
        /// Establishes the provider's OpenXR function table.
        ///
        /// The package builds a chain here: it takes the loader's own xrGetInstanceProcAddr,
        /// lets every enabled feature wrap it in turn so features can intercept OpenXR calls,
        /// then hands the outermost pointer back. We enable no features, so the chain is one
        /// link long and the pointer goes back unchanged -- which makes the whole step look
        /// like feature plumbing that a minimal loader can skip.
        ///
        /// It cannot. The second call also runs the provider's stage-1 load, and that is what
        /// fills in the global function table. Without it xrEnumerateApiLayerProperties and
        /// xrEnumerateInstanceExtensionProperties return XR_ERROR_FUNCTION_UNSUPPORTED -- an
        /// error that names an OpenXR function and so reads like a runtime problem, sending
        /// you off to check headsets and runtimes that were never involved.
        /// </summary>
        private static void HookGetInstanceProcAddr()
        {
            var procAddr = Native.GetProcAddressPtr(true);
            Plugin.Log.LogInfo($"xrGetInstanceProcAddr = 0x{procAddr.ToInt64():X}");
            Native.SetProcAddressPtrAndLoadStage1(procAddr);
        }

        /// <summary>
        /// Hands the provider a working OpenXR loader.
        ///
        /// The package passes the bare name "openxr_loader" and lets the player's native
        /// plugin search path find it. We are not the player, and nothing has put that folder
        /// on this process's search path, so an absolute path is tried first. All three forms
        /// are tried because they differ in who appends ".dll" and in which directory the
        /// Windows loader looks -- and a wrong guess here does not fail here. It surfaces much
        /// later as XR_ERROR_FUNCTION_UNSUPPORTED on xrEnumerateApiLayerProperties, which is
        /// the provider saying its own function table was never filled in, not anything the
        /// runtime said.
        /// </summary>
        private static bool LoadLoaderLibrary(string loaderDll)
        {
            var log = Plugin.Log;
            var withoutExtension = loaderDll.Substring(0, loaderDll.Length - ".dll".Length);

            foreach (var candidate in new[] { withoutExtension, loaderDll, "openxr_loader" })
            {
                bool ok;
                try
                {
                    ok = Native.LoadOpenXRLibrary(Native.ToWideBytes(candidate));
                }
                catch (Exception e)
                {
                    log.LogWarning($"main_LoadOpenXRLibrary threw for '{candidate}': {e.Message}");
                    continue;
                }

                log.LogInfo($"main_LoadOpenXRLibrary('{candidate}') -> {ok}");
                if (ok) return true;
            }

            log.LogError("No form of the loader path was accepted.");
            return false;
        }

        /// <summary>
        /// Chooses the OpenXR runtime for this process only.
        ///
        /// A machine with Virtual Desktop or Oculus installed usually has one of them
        /// registered as the system-wide runtime, so a mod that just asks for "OpenXR" can
        /// come up somewhere the player did not expect, silently. Setting XR_RUNTIME_JSON in
        /// our own environment overrides that for the game and nothing else; the machine-wide
        /// setting is never touched.
        /// </summary>
        private static void PinRuntime()
        {
            var pin = Plugin.Instance.OpenXrRuntimeJson.Value;
            if (string.IsNullOrWhiteSpace(pin)) return;

            if (!File.Exists(pin))
            {
                Plugin.Log.LogWarning($"OpenXrRuntimeJson points at '{pin}', which does not exist. "
                                    + "Falling back to the system default runtime.");
                return;
            }

            Environment.SetEnvironmentVariable("XR_RUNTIME_JSON", pin);
            Plugin.Log.LogInfo($"OpenXR runtime pinned to '{pin}' for this process.");
        }

        private static void SetApplicationInfo()
        {
            // The hash is the package's own recipe: MD5 of the version string, big-endian,
            // first four bytes. The runtime only uses it to tell builds apart.
            var data = MD5.HashData(Encoding.UTF8.GetBytes(Application.version));
            if (BitConverter.IsLittleEndian) Array.Reverse(data);
            var hash = BitConverter.ToUInt32(data, 0);

            Native.SetApplicationInfo(
                Application.productName, Application.version, hash, Application.unityVersion);
        }

        /// <summary>
        /// Forces multi-pass stereo.
        ///
        /// This is not a preference. The game's shaders were compiled without XR, so no
        /// single-pass-instanced variants exist in the shipped shader collection: asking for
        /// SinglePassInstanced would render one eye, or garbage. Multi-pass renders each eye
        /// as an ordinary pass using variants the game already has. It costs roughly twice
        /// the GPU time and it is the only mode that can work here.
        /// </summary>
        private static void ApplyRenderSettings()
        {
            Native.SetRenderMode(Native.RenderMode.MultiPass);

            // Depth submission lets the compositor reproject with parallax instead of only
            // rotating the last image about your eye, which is what a frame the game failed to
            // deliver gets otherwise. Rotation is exact at infinity and wrong in proportion to
            // how near a thing is, so the whole of the error arrives on whatever is closest --
            // the interface panel, a metre and a half away.
            //
            // Still opt-in: it asks for XR_KHR_composition_layer_depth and a depth buffer in a
            // format the runtime will accept, and if either is missing the failure is a black
            // headset rather than a warning.
            var depth = Plugin.Instance.SubmitDepth.Value;

            Native.SetDepthSubmissionMode(depth
                ? Native.DepthSubmissionMode.Depth24Bit
                : Native.DepthSubmissionMode.None);

            if (depth) Plugin.Log.LogInfo("submitting a 24-bit depth buffer to the compositor");
        }

        /// <summary>
        /// Finds the descriptors the native provider registered and creates the subsystems.
        ///
        /// The package calls <c>CreateSubsystem&lt;XRDisplaySubsystemDescriptor, XRDisplaySubsystem&gt;</c>,
        /// which routes to <c>SubsystemManager.GetSubsystemDescriptors&lt;T&gt;</c>. Neither
        /// survives in this build. IL2CPP compiles only the generic instantiations a game
        /// actually uses, and strips whole methods it never calls -- this game calls none of
        /// them, so Il2CppInterop has to rebuild their bodies from Unity's reference
        /// assemblies, and for these it cannot ("Method unstripping failed").
        ///
        /// What cannot be stripped is the store the native side writes into.
        /// <c>SubsystemDescriptorStore.s_IntegratedDescriptors</c> is a plain static field,
        /// populated from C++ as each provider registers, so reading it is a memory access
        /// rather than a method call and nothing has to be reconstructed.
        /// </summary>
        private bool CreateSubsystems()
        {
            var log = Plugin.Log;

            var store = SubsystemDescriptorStore.s_IntegratedDescriptors;
            if (store == null)
            {
                log.LogError("SubsystemDescriptorStore.s_IntegratedDescriptors is null; "
                           + "the engine has not initialised its subsystem registry.");
                return false;
            }

            log.LogInfo($"{store.Count} integrated subsystem descriptor(s) registered:");
            IntegratedSubsystemDescriptor displayDesc = null;
            IntegratedSubsystemDescriptor inputDesc = null;

            for (var i = 0; i < store.Count; i++)
            {
                var d = store[i];
                var id = SafeId(d);
                log.LogInfo($"    '{id}'");
                if (id == "OpenXR Display") displayDesc = d;
                else if (id == "OpenXR Input") inputDesc = d;
            }

            if (displayDesc == null)
            {
                log.LogError("No 'OpenXR Display' descriptor. The engine reads "
                           + "UnitySubsystems/UnityOpenXR/UnitySubsystemsManifest.json at startup, so it "
                           + "must be on disk before launch -- and UnityOpenXR.dll must sit in "
                           + "LittleWitchNobeta_Data/Plugins/x86_64 where the manifest's libraryName points.");
                return false;
            }

            Display = Instantiate<XRDisplaySubsystem>(displayDesc, "OpenXR Display");
            if (Display == null) return false;

            // Input is wanted but not required: a headset with no controller bindings yet is
            // still a headset, and losing the display over it would be the wrong trade.
            Input = inputDesc == null ? null : Instantiate<XRInputSubsystem>(inputDesc, "OpenXR Input");
            if (Input == null)
                log.LogWarning("No 'OpenXR Input' subsystem; head tracking only for now.");

            return true;
        }

        /// <summary>
        /// Writes the provider's own account of the session to the log. It carries the
        /// XrResult codes, the runtime's name and version, and the extension list -- the
        /// things that say whether a failure is our sequencing or the runtime's answer.
        /// </summary>
        public static void DumpDiagnosticReport(string reason)
        {
            var report = Native.GenerateReport();
            if (string.IsNullOrWhiteSpace(report))
            {
                Plugin.Log.LogWarning($"No OpenXR diagnostic report available ({reason}).");
                return;
            }

            Plugin.Log.LogError($"---- OpenXR diagnostic report ({reason}) ----");
            foreach (var line in report.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                if (!string.IsNullOrWhiteSpace(line)) Plugin.Log.LogError("  " + line.TrimEnd());
            Plugin.Log.LogError("---- end of report ----");

            Explain(report);
        }

        /// <summary>
        /// Turns the failures a player will actually hit into something they can act on. The
        /// report above is precise and unreadable; this is the sentence underneath it.
        /// </summary>
        private static void Explain(string report)
        {
            var log = Plugin.Log;

            // The runtime answered the enumeration calls and then refused to create an
            // instance. Nothing is wrong with the install: there is no headset for it to
            // create an instance for.
            if (report.Contains("xrCreateInstance") && report.Contains("XR_ERROR_RUNTIME_FAILURE"))
            {
                log.LogError("Your OpenXR runtime is installed and answering, but would not start a "
                           + "session. Put the headset on and get it as far as its own home "
                           + "environment, then launch the game.");
                return;
            }

            if (report.Contains("XR_ERROR_FORM_FACTOR_UNAVAILABLE"))
            {
                log.LogError("The runtime is running but reports no headset connected.");
                return;
            }

            if (report.Contains("XR_ERROR_FUNCTION_UNSUPPORTED"))
            {
                log.LogError("The provider never finished loading its OpenXR function table. That is a "
                           + "fault in the mod's start-up sequence, not in your runtime.");
            }
        }

        private static string SafeId(IntegratedSubsystemDescriptor d)
        {
            try { return d.id; } catch (Exception e) { return $"<unreadable: {e.GetType().Name}>"; }
        }

        /// <summary>
        /// Turns a descriptor into a subsystem, the way the engine does it.
        ///
        /// The typed path -- <c>IntegratedSubsystemDescriptor&lt;T&gt;.Create()</c>, reached
        /// through <c>CreateImpl</c> -- is stripped from this build, and Il2CppInterop cannot
        /// rebuild its body. So we do by hand exactly what Unity 2020.3's
        /// IntegratedSubsystemDescriptor.bindings.cs does:
        ///
        ///     IntPtr ptr = SubsystemDescriptorBindings.Create(m_Ptr);
        ///     var subsystem = (TSubsystem)SubsystemManager.GetIntegratedSubsystemByPtr(ptr);
        ///     if (subsystem != null) subsystem.m_SubsystemDescriptor = this;
        ///
        /// Every piece of that survives stripping, and for a reason worth remembering: those
        /// are engine bindings, not managed logic. <c>SubsystemDescriptorBindings.Create</c>
        /// is an internal call whose body lives in UnityPlayer and is registered by name at
        /// startup, so it exists whether or not any managed code was compiled to reach it.
        /// The native side builds the managed wrapper on the way through -- which is why the
        /// return value is fetched by pointer rather than constructed here.
        /// </summary>
        private static T Instantiate<T>(IntegratedSubsystemDescriptor descriptor, string id)
            where T : IntegratedSubsystem
        {
            try
            {
                var ptr = SubsystemDescriptorBindings.Create(descriptor.m_Ptr);
                if (ptr == IntPtr.Zero)
                {
                    Plugin.Log.LogError($"'{id}': the engine refused to create the subsystem.");
                    return null;
                }

                var subsystem = SubsystemManager.GetIntegratedSubsystemByPtr(ptr);
                if (subsystem == null)
                {
                    Plugin.Log.LogError($"'{id}': created at {ptr:X}, but no managed wrapper came back.");
                    return null;
                }

                subsystem.m_SubsystemDescriptor = descriptor.Cast<ISubsystemDescriptor>();
                return subsystem.Cast<T>();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"'{id}' exists but would not instantiate: {e.GetType().Name}: {e.Message}");
                return null;
            }
        }

        // -- runtime events ------------------------------------------------------------

        private static void OnNativeEvent(Native.NativeEvent e, ulong payload)
        {
            var self = _instance;
            if (self == null) return;

            self._sessionState = e;

            switch (e)
            {
                case Native.NativeEvent.XrReady:
                case Native.NativeEvent.XrFocused:
                case Native.NativeEvent.XrStopping:
                case Native.NativeEvent.XrExiting:
                case Native.NativeEvent.XrInstanceLossPending:
                    Plugin.Log.LogInfo($"OpenXR session state: {e}");
                    break;
            }

            // This is the second half of the chicken-and-egg described on StartSession: the
            // session had to exist before the runtime would ever say XrReady, and now that it
            // has, this is what actually starts rendering.
            if (e == Native.NativeEvent.XrReady)
                self.StartSession();

            // The headset went away, or the runtime asked us to stand down. Stop rendering to
            // it rather than pumping frames at something that is no longer listening.
            if (e is Native.NativeEvent.XrStopping or Native.NativeEvent.XrExiting
                && self.CurrentState == State.Running)
            {
                self.Display?.Stop();
                self.Input?.Stop();
                self.CurrentState = State.Initialized;
                self._waitingLogged = false;
            }

            if (self._sessionEverReady && e == Native.NativeEvent.XrIdle)
                Plugin.Log.LogInfo("OpenXR session idle; waiting for the runtime to make it ready again.");
        }
    }
}
