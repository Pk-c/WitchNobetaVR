using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace NobetaVR.Xr
{
    /// <summary>
    /// The native side of Unity's OpenXR provider, <c>UnityOpenXR.dll</c>.
    ///
    /// None of this is guesswork. The OpenXR package ships its own C# sources, so every
    /// entry point below is copied from <c>Runtime/OpenXRLoaderInternal.cs</c> and
    /// <c>Runtime/OpenXRRenderSettings.cs</c> in com.unity.xr.openxr@1.6.0 -- the version
    /// that declares <c>unity: 2020.3</c>, which is this game.
    ///
    /// What the mod reimplements is the *managed* loader, because that half of the package
    /// cannot be dropped into an IL2CPP process: it is compiled against the real UnityEngine
    /// assemblies, not against Il2CppInterop's proxies of them, so it would bind to types
    /// whose shapes no longer match.
    ///
    /// The native half needs no such treatment. It is a plain C DLL, so these are ordinary
    /// P/Invokes from BepInEx's CoreCLR runtime with none of IL2CPP in the way.
    /// </summary>
    internal static class UnityOpenXrNative
    {
        private const string Lib = "UnityOpenXR";

        /// <summary>Stereo rendering mode. See <see cref="XrLoader"/> for why this is always MultiPass.</summary>
        public enum RenderMode
        {
            MultiPass = 0,
            SinglePassInstanced = 1,
        }

        public enum DepthSubmissionMode
        {
            None = 0,
            Depth16Bit = 1,
            Depth24Bit = 2,
        }

        /// <summary>
        /// Session lifecycle events pushed from the runtime. Ordinals matter -- the native
        /// side sends an index into this list -- so the order is the package's order.
        /// </summary>
        public enum NativeEvent
        {
            XrSetupConfigValues = 0,
            XrSystemIdChanged,
            XrInstanceChanged,
            XrSessionChanged,
            XrBeginSession,
            XrSessionStateChanged,
            XrChangedSpaceApp,
            XrEndSession,
            XrDestroySession,
            XrDestroyInstance,
            XrIdle,
            XrReady,
            XrSynchronized,
            XrVisible,
            XrFocused,
            XrStopping,
            XrExiting,
            XrLossPending,
            XrInstanceLossPending,
            XrRestartRequested,
            XrRequestRestartLoop,
        }

        public delegate void ReceiveNativeEventDelegate(NativeEvent e, ulong payload);

        // -- library ------------------------------------------------------------------

        [DllImport(Lib, EntryPoint = "main_LoadOpenXRLibrary")]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool LoadOpenXRLibrary(byte[] loaderPath);

        [DllImport(Lib, EntryPoint = "main_UnloadOpenXRLibrary")]
        public static extern void UnloadOpenXRLibrary();

        // -- configuration ------------------------------------------------------------

        [DllImport(Lib, EntryPoint = "NativeConfig_SetCallbacks")]
        public static extern void SetCallbacks(ReceiveNativeEventDelegate callback);

        [DllImport(Lib, EntryPoint = "NativeConfig_SetApplicationInfo", CharSet = CharSet.Ansi)]
        public static extern void SetApplicationInfo(
            string applicationName, string applicationVersion, uint applicationVersionHash, string engineVersion);

        [DllImport(Lib, EntryPoint = "NativeConfig_SetRenderMode")]
        public static extern void SetRenderMode(RenderMode renderMode);

        [DllImport(Lib, EntryPoint = "NativeConfig_GetRenderMode")]
        public static extern RenderMode GetRenderMode();

        [DllImport(Lib, EntryPoint = "NativeConfig_SetDepthSubmissionMode")]
        public static extern void SetDepthSubmissionMode(DepthSubmissionMode mode);

        [DllImport(Lib, EntryPoint = "NativeConfig_GetRuntimeName")]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool GetRuntimeNamePtr(out IntPtr runtimeNamePtr);

        [DllImport(Lib, EntryPoint = "NativeConfig_GetRuntimeVersion")]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool GetRuntimeVersion(out ushort major, out ushort minor, out uint patch);

        // -- proc address chain --------------------------------------------------------
        //
        // How the provider gets from "a loader library is open" to "the OpenXR entry points
        // are callable". NativeConfig_GetProcAddressPtr(true) returns the loader's own
        // xrGetInstanceProcAddr; the package then lets each enabled feature wrap that pointer
        // to intercept calls, and hands the final one back through
        // NativeConfig_SetProcAddressPtrAndLoadStage1 -- which, as the name says, also runs
        // the provider's stage-1 load and fills in its global function table.
        //
        // With no features there is nothing to wrap, which makes this look skippable. It is
        // not: skip it and every global call returns XR_ERROR_FUNCTION_UNSUPPORTED, because
        // the table was never populated. That error names the OpenXR function, so it reads
        // like the runtime refusing, and it is not the runtime at all.

        [DllImport(Lib, EntryPoint = "NativeConfig_GetProcAddressPtr")]
        public static extern IntPtr GetProcAddressPtr([MarshalAs(UnmanagedType.U1)] bool loaderDefault);

        [DllImport(Lib, EntryPoint = "NativeConfig_SetProcAddressPtrAndLoadStage1")]
        public static extern void SetProcAddressPtrAndLoadStage1(IntPtr func);

        // -- session ------------------------------------------------------------------

        [DllImport(Lib, EntryPoint = "session_InitializeSession")]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool InitializeSession();

        [DllImport(Lib, EntryPoint = "session_CreateSessionIfNeeded")]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool CreateSessionIfNeeded();

        [DllImport(Lib, EntryPoint = "session_BeginSession")]
        public static extern void BeginSession();

        [DllImport(Lib, EntryPoint = "session_EndSession")]
        public static extern void EndSession();

        [DllImport(Lib, EntryPoint = "session_DestroySession")]
        public static extern void DestroySession();

        [DllImport(Lib, EntryPoint = "session_RequestExitSession")]
        public static extern void RequestExitSession();

        [DllImport(Lib, EntryPoint = "session_SetSuccessfullyInitialized")]
        public static extern void SetSuccessfullyInitialized([MarshalAs(UnmanagedType.U1)] bool value);

        [DllImport(Lib, EntryPoint = "session_GetLastError", CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool GetLastErrorPtr(out IntPtr error);

        [DllImport(Lib, EntryPoint = "messagepump_PumpMessageLoop")]
        public static extern void PumpMessageLoop();

        // -- input -----------------------------------------------------------------------
        //
        // Controllers do not exist until an action set has been built and attached. OpenXR has
        // no notion of "the thumbstick"; it has actions, bound to interaction-profile paths,
        // committed to the session in one go. The provider turns those actions into the
        // InputDevice features that XRModule reports, and the usage strings passed to
        // CreateAction are exactly the names TryGetFeatureValue_* will answer to later.
        //
        // Skipping this was why the sticks read as nothing: not a binding mistake, but no
        // devices at all, because nothing had ever been declared to the runtime.

        [StructLayout(LayoutKind.Explicit, Size = 16)]
        public struct SerializedGuid
        {
            [FieldOffset(0)] public ulong Low;
            [FieldOffset(8)] public ulong High;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct SerializedBinding
        {
            public ulong ActionId;
            [MarshalAs(UnmanagedType.LPStr)] public string Path;
        }

        public enum ActionType : uint
        {
            Binary = 0,
            Axis1D = 1,
            Axis2D = 2,
            Pose = 3,
            Vibrate = 4,
        }

        [DllImport(Lib, EntryPoint = "OpenXRInputProvider_RegisterDeviceDefinition", CharSet = CharSet.Ansi)]
        public static extern ulong RegisterDeviceDefinition(
            string userPath, string interactionProfile, uint characteristics,
            string name, string manufacturer, string serialNumber);

        [DllImport(Lib, EntryPoint = "OpenXRInputProvider_CreateActionSet", CharSet = CharSet.Ansi)]
        public static extern ulong CreateActionSet(string name, string localizedName, SerializedGuid guid);

        [DllImport(Lib, EntryPoint = "OpenXRInputProvider_CreateAction", CharSet = CharSet.Ansi)]
        public static extern ulong CreateAction(
            ulong actionSetId, string name, string localizedName, uint actionType, SerializedGuid guid,
            string[] userPaths, uint userPathCount, string[] usages, uint usageCount);

        [DllImport(Lib, EntryPoint = "OpenXRInputProvider_SuggestBindings", CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool SuggestBindings(
            string interactionProfile, SerializedBinding[] bindings, uint bindingCount);

        [DllImport(Lib, EntryPoint = "OpenXRInputProvider_AttachActionSets")]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool AttachActionSets();

        // -- haptics --------------------------------------------------------------------
        //
        // The output half of the same action set. An OpenXR haptic is not a device feature you
        // write to, it is an action you apply feedback through, so the provider addresses it by
        // action id -- and the id is looked up from the device by control name rather than
        // handed back by CreateAction, because the package's own path there goes through an
        // Input System control this mod does not have.
        //
        // Copied from OpenXRInput.cs in the same package version as everything above; the entry
        // point name for the lookup really is ...ByControl while the others are not.
        //
        // These are the fallback. VrHaptics tries the XR input subsystem first, which is the
        // half of the engine every control here is already read through.

        [DllImport(Lib, EntryPoint = "OpenXRInputProvider_GetActionIdByControl")]
        public static extern ulong GetActionIdByControl(uint deviceId, string name);

        [DllImport(Lib, EntryPoint = "OpenXRInputProvider_SendHapticImpulse",
                   CallingConvention = CallingConvention.Cdecl)]
        public static extern void SendHapticImpulse(
            uint deviceId, ulong actionId, float amplitude, float frequency, float duration);

        [DllImport(Lib, EntryPoint = "OpenXRInputProvider_StopHaptics",
                   CallingConvention = CallingConvention.Cdecl)]
        public static extern void StopHaptics(uint deviceId, ulong actionId);

        // -- diagnostics ---------------------------------------------------------------
        //
        // The provider keeps its own structured report of everything OpenXR told it: which
        // runtime answered, which extensions it offers, the system id, and the actual XrResult
        // behind a failure. That report is the difference between "the engine refused to
        // create the subsystem" and knowing why, so it is started before anything else can
        // fail and dumped whenever something does.

        [DllImport(Lib, EntryPoint = "DiagnosticReport_StartReport")]
        public static extern void StartReport();

        [DllImport(Lib, EntryPoint = "DiagnosticReport_GenerateReport")]
        private static extern IntPtr GenerateReportPtr();

        [DllImport(Lib, EntryPoint = "DiagnosticReport_ReleaseReport")]
        private static extern void ReleaseReport(IntPtr report);

        public static string GenerateReport()
        {
            try
            {
                var buffer = GenerateReportPtr();
                if (buffer == IntPtr.Zero) return null;
                var text = Marshal.PtrToStringAnsi(buffer);
                ReleaseReport(buffer);
                return text;
            }
            catch
            {
                return null;
            }
        }

        // -- helpers ------------------------------------------------------------------

        /// <summary>
        /// The loader path crosses the boundary as wide characters rather than as a
        /// marshalled string: <c>main_LoadOpenXRLibrary</c> takes a raw <c>wchar_t*</c>
        /// buffer. Copied from the package's own StringToWCHAR_T.
        /// </summary>
        public static byte[] ToWideBytes(string s) => Encoding.Unicode.GetBytes(s + "\0");

        public static string LastError() => ReadAnsi(GetLastErrorPtr);

        public static string RuntimeName() => ReadAnsi(GetRuntimeNamePtr);

        private delegate bool PtrGetter(out IntPtr p);

        private static string ReadAnsi(PtrGetter getter)
        {
            try
            {
                return getter(out var p) && p != IntPtr.Zero ? Marshal.PtrToStringAnsi(p) : null;
            }
            catch
            {
                // Called from failure paths, including ones where the library never loaded.
                // A diagnostic helper must never be the thing that throws.
                return null;
            }
        }

        private static bool _resolverInstalled;

        /// <summary>
        /// Points every P/Invoke above at the copy of UnityOpenXR.dll in the game's plugin
        /// folder.
        ///
        /// The engine loads that DLL itself at startup, because UnitySubsystemsManifest.json
        /// tells it to -- but it loads it by absolute path, and the plugin folder is not on
        /// this process's DLL search path. A bare <c>DllImport("UnityOpenXR")</c> from
        /// BepInEx's runtime would therefore have nothing to bind to, even though the module
        /// is already mapped. Naming the full path settles it rather than depending on the
        /// Windows loader's module cache to paper over the difference.
        /// </summary>
        public static void InstallResolver(string pluginDir)
        {
            if (_resolverInstalled) return;
            _resolverInstalled = true;

            var full = Path.Combine(pluginDir, "UnityOpenXR.dll");
            NativeLibrary.SetDllImportResolver(
                Assembly.GetExecutingAssembly(),
                (name, asm, path) =>
                    name == Lib && File.Exists(full) ? NativeLibrary.Load(full) : IntPtr.Zero);
        }
    }
}
