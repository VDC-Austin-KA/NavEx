using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NavEx.Core.Exporters
{
    /// <summary>
    /// P/Invoke surface for NavExDatasmith.dll, the native bridge onto Epic's
    /// Datasmith Export SDK.
    ///
    /// Datasmith only ever reads a Datasmith-native `.udsmesh` mesh payload, and the
    /// only supported way to write one is the SDK's FDatasmithMeshExporter, which is
    /// C++ with UE types across every signature. Hence a native bridge with a flat C
    /// waist rather than anything managed.
    ///
    /// The bridge and DatasmithSDK.dll both ship beside NavEx.dll in the plugin
    /// folder. Windows resolves a DllImport against the *process* directory — which
    /// here is the Navisworks install, not ours — so the load is done explicitly
    /// against our own folder before any entry point is touched.
    /// </summary>
    internal static class DatasmithNative
    {
        public const string NativeLibrary = "NavExDatasmith.dll";
        private const string SdkLibrary = "DatasmithSDK.dll";

        private static bool _probed;
        private static bool _available;
        private static string _unavailableReason = "";
        private static bool _initialized;

        /// <summary>
        /// Whether the native bridge is present and loadable. False is a normal,
        /// explainable state — the SDK has to be built once from Unreal Engine
        /// source — not an error to throw on.
        /// </summary>
        public static bool Available
        {
            get { Probe(); return _available; }
        }

        /// <summary>Why <see cref="Available"/> is false. Empty when it is true.</summary>
        public static string UnavailableReason
        {
            get { Probe(); return _unavailableReason; }
        }

        /// <summary>The folder NavEx.dll was loaded from — where the natives live too.</summary>
        public static string PluginFolder
        {
            get
            {
                try
                {
                    return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                }
                catch (Exception)
                {
                    return "";
                }
            }
        }

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;

            string folder = PluginFolder;
            string bridge = Path.Combine(folder, NativeLibrary);
            string sdk = Path.Combine(folder, SdkLibrary);

            if (!File.Exists(bridge))
            {
                _unavailableReason =
                    NativeLibrary + " is not installed next to NavEx.dll (" + folder + "). " +
                    "Build native/NavExDatasmith and copy it, together with " + SdkLibrary + ", into the plugin folder.";
                return;
            }

            if (!File.Exists(sdk))
            {
                // Worth naming separately: the bridge alone loads nothing, and the
                // failure would otherwise surface as an opaque BadImageFormat.
                _unavailableReason =
                    SdkLibrary + " is missing from " + folder + ". It is built from Unreal Engine source " +
                    "(Programs/Enterprise/DatasmithSDK) and must ship beside " + NativeLibrary + ".";
                return;
            }

            // Put our folder on the search path first so the bridge's own dependency
            // on DatasmithSDK.dll resolves, then load the bridge by full path.
            SetDllDirectory(folder);
            if (LoadLibrary(bridge) == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                _unavailableReason = "Could not load " + NativeLibrary + " (Win32 error " + error +
                    "). It and " + SdkLibrary + " must both be x64 and built against the same Unreal Engine version.";
                return;
            }

            _available = true;
        }

        /// <summary>
        /// Boots the SDK once per process. The SDK does not support an
        /// initialise/shutdown cycle, so this never un-initialises between exports.
        /// </summary>
        public static void EnsureInitialized()
        {
            if (_initialized) return;
            if (NavExDs_Initialize() != 0)
                throw new InvalidOperationException("Datasmith SDK failed to initialise: " + LastError());
            _initialized = true;

            // Epic: Shutdown "must be called when the process performing exports
            // exits", and the SDK does not support initialising a second time. So it
            // is hung off process exit rather than plugin unload -- Navisworks can
            // unload and reload an add-in within one session, and doing it there
            // would leave the next export unable to start.
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            try { NavExDs_Shutdown(); }
            catch (Exception) { /* the process is going away regardless */ }
        }

        public static string LastError()
        {
            try
            {
                IntPtr text = NavExDs_LastError();
                return text == IntPtr.Zero ? "" : (Marshal.PtrToStringUni(text) ?? "");
            }
            catch (Exception)
            {
                return "";
            }
        }

        // ── Bridge entry points ───────────────────────────────────────────────

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern int NavExDs_Initialize();

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern void NavExDs_Shutdown();

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr NavExDs_LastError();

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern IntPtr NavExDs_BeginScene(
            [MarshalAs(UnmanagedType.LPWStr)] string sceneName,
            [MarshalAs(UnmanagedType.LPWStr)] string outputDir,
            [MarshalAs(UnmanagedType.LPWStr)] string host,
            [MarshalAs(UnmanagedType.LPWStr)] string vendor,
            [MarshalAs(UnmanagedType.LPWStr)] string productName,
            [MarshalAs(UnmanagedType.LPWStr)] string productVersion);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern int NavExDs_AddMesh(
            IntPtr scene,
            [MarshalAs(UnmanagedType.LPWStr)] string meshName,
            float[] positions, int vertexCount,
            int[] indices, int triangleCount,
            float[] vertexNormals,
            float[] vertexUVs,
            int[] faceMaterialIds);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern int NavExDs_AddMeshActor(
            IntPtr scene,
            [MarshalAs(UnmanagedType.LPWStr)] string actorName,
            [MarshalAs(UnmanagedType.LPWStr)] string meshName,
            [MarshalAs(UnmanagedType.LPWStr)] string label,
            [MarshalAs(UnmanagedType.LPWStr)] string layer);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern int NavExDs_AddMetaData(
            IntPtr scene,
            [MarshalAs(UnmanagedType.LPWStr)] string actorName,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] keys,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] values,
            int count);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern int NavExDs_Export(IntPtr scene);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern void NavExDs_DestroyScene(IntPtr scene);

        // ── Loader ────────────────────────────────────────────────────────────

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDllDirectory(string path);
    }
}
