using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Avalonia.ProjectM.Native;

public enum ProjectMChannels
{
    Mono = 1,
    Stereo = 2
}

public static class ProjectMNative
{
    private static IntPtr _libraryHandle = IntPtr.Zero;
    private static IntPtr _glewHandle = IntPtr.Zero;

    static ProjectMNative()
    {
        try
        {
            string libraryPath = GetLibraryPath();
            if (File.Exists(libraryPath))
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    string dir = Path.GetDirectoryName(libraryPath) ?? "";
                    string glewPath = Path.Combine(dir, "glew32.dll");
                    if (File.Exists(glewPath))
                    {
                        try
                        {
                            _glewHandle = NativeLibrary.Load(glewPath, typeof(ProjectMNative).Assembly, DllImportSearchPath.SafeDirectories);
                            if (NativeLibrary.TryGetExport(_glewHandle, "glewExperimental", out IntPtr glewExpAddr))
                            {
                                Marshal.WriteByte(glewExpAddr, 1);
                                Console.WriteLine("[ProjectM] Set glewExperimental = true successfully.");
                            }
                            if (NativeLibrary.TryGetExport(_glewHandle, "glewInit", out IntPtr glewInitAddr))
                            {
                                glewInit = Marshal.GetDelegateForFunctionPointer<glewInit_delegate>(glewInitAddr);
                                Console.WriteLine("[ProjectM] Loaded glewInit delegate successfully.");
                            }
                        }
                        catch (Exception glewEx)
                        {
                            Console.WriteLine($"[ProjectM] Failed to set glewExperimental: {glewEx.Message}");
                        }
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // libprojectM.so.4 (4.1.x builds with libglew-dev present at compile time)
                    // links libGLEW.so.2 as a shared dependency. The dynamic linker loads
                    // libGLEW.so.2 into the process automatically when libprojectM.so.4 is
                    // loaded, so the __glewBindFramebuffer global is shared between us and
                    // libprojectM — same hook mechanism as Windows.
                    //
                    // NOTE: This entire GLEW path is only used on libprojectM 4.1.x builds
                    // that still link GLEW. On 4.2.0+ (which uses GLAD internally and
                    // exposes projectm_opengl_render_frame_fbo), HasFboRenderApi returns
                    // true and the GLEW code path is never invoked.
                    string[] glewCandidates = ["libGLEW.so.2", "libGLEW.so.2.2", "libGLEW.so.2.1", "libGLEW.so"];
                    foreach (var candidate in glewCandidates)
                    {
                        try
                        {
                            _glewHandle = NativeLibrary.Load(candidate);
                            if (_glewHandle != IntPtr.Zero)
                            {
                                if (NativeLibrary.TryGetExport(_glewHandle, "glewExperimental", out IntPtr glewExpAddr))
                                    Marshal.WriteByte(glewExpAddr, 1);
                                if (NativeLibrary.TryGetExport(_glewHandle, "glewInit", out IntPtr glewInitAddr))
                                    glewInit = Marshal.GetDelegateForFunctionPointer<glewInit_delegate>(glewInitAddr);
                                Console.WriteLine($"[ProjectM] Loaded GLEW from system: {candidate}");
                                break;
                            }
                        }
                        catch { }
                    }
                    if (_glewHandle == IntPtr.Zero)
                        Console.WriteLine("[ProjectM] WARNING: libGLEW not found on system — FBO hook unavailable. " +
                                          "Either install libglew2.2 (Ubuntu/Debian: sudo apt install libglew2.2), " +
                                          "or upgrade libprojectM to 4.2.0+ which exposes projectm_opengl_render_frame_fbo " +
                                          "and doesn't need the hook at all.");
                }

                // Crucial: we MUST tell the loader to look in the same directory as the DLL for dependencies!
                // (e.g. libprojectM.dll needs glew32.dll on Windows — both sit in lib/ together.)
                _libraryHandle = NativeLibrary.Load(libraryPath, typeof(ProjectMNative).Assembly, DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
                LoadDelegates();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load libprojectM: {ex}");
        }
    }

    /// <summary>
    /// Resolves the path to the native libprojectM binary.
    ///
    /// <para>
    /// Loaded from <c>&lt;appdir&gt;/lib/</c> — flat layout, no per-platform
    /// subfolders. Windows <c>.dll</c> and Linux <c>.so</c> files coexist by
    /// extension; we pick the right filename per OS at runtime.
    /// </para>
    /// </summary>
    private static string GetLibraryPath()
    {
        var dir = Path.GetDirectoryName(typeof(ProjectMNative).Assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory;

        string fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "libprojectM.dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "libprojectM.dylib"
                : "libprojectM.so.4";

        return Path.Combine(dir, fileName);
    }

    private static T GetDelegate<T>(string name, bool throwOnError = true) where T : Delegate
    {
        if (_libraryHandle == IntPtr.Zero) return null!;
        if (NativeLibrary.TryGetExport(_libraryHandle, name, out IntPtr address))
        {
            try
            {
                return Marshal.GetDelegateForFunctionPointer<T>(address);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProjectM] ERROR parsing delegate {name}: {ex}");
                return null!;
            }
        }
        Console.WriteLine($"[ProjectM] TryGetExport failed for {name}");
        if (throwOnError)
            throw new Exception($"Export {name} not found in libprojectM.");
        return null!;
    }

    private static void LoadDelegates()
    {
        projectm_create = GetDelegate<projectm_create_delegate>("projectm_create", false);
        projectm_destroy = GetDelegate<projectm_destroy_delegate>("projectm_destroy", false);
        projectm_set_window_size = GetDelegate<projectm_set_window_size_delegate>("projectm_set_window_size", false);
        projectm_set_fps = GetDelegate<projectm_set_fps_delegate>("projectm_set_fps", false);
        projectm_pcm_add_float = GetDelegate<projectm_pcm_add_float_delegate>("projectm_pcm_add_float", false);
        projectm_pcm_add_int16 = GetDelegate<projectm_pcm_add_int16_delegate>("projectm_pcm_add_int16", false);
        projectm_opengl_render_frame = GetDelegate<projectm_opengl_render_frame_delegate>("projectm_opengl_render_frame", false);

        // 4.2.0+ API — direct FBO targeting. If present, we bypass the GLEW
        // BindFramebuffer hook entirely. This is the future-proof path: when
        // the user upgrades libprojectM from 4.1.x to 4.2.0+, this delegate
        // resolves, HasFboRenderApi becomes true, and the GLEW hook is never
        // installed — no code change required in the wrapper.
        projectm_opengl_render_frame_fbo = GetDelegate<projectm_opengl_render_frame_fbo_delegate>("projectm_opengl_render_frame_fbo", false);
        if (HasFboRenderApi)
            Console.WriteLine("[ProjectM] Detected projectm_opengl_render_frame_fbo (libprojectM 4.2.0+) — GLEW hook disabled, using direct FBO API.");

        projectm_set_texture_search_paths = GetDelegate<projectm_set_texture_search_paths_delegate>("projectm_set_texture_search_paths", false);
        projectm_set_log_callback = GetDelegate<projectm_set_log_callback_delegate>("projectm_set_log_callback", false);
        projectm_set_log_level = GetDelegate<projectm_set_log_level_delegate>("projectm_set_log_level", false);

        projectm_load_preset_file = GetDelegate<projectm_load_preset_file_delegate>("projectm_load_preset_file", false);
        projectm_load_preset_data = GetDelegate<projectm_load_preset_data_delegate>("projectm_load_preset_data", false);
        projectm_write_debug_image_on_next_frame = GetDelegate<projectm_write_debug_image_on_next_frame_delegate>("projectm_write_debug_image_on_next_frame", false);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr projectm_create_delegate();
    public static projectm_create_delegate projectm_create { get; private set; } = null!;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void projectm_destroy_delegate(IntPtr instance);
    public static projectm_destroy_delegate projectm_destroy { get; private set; } = null!;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void projectm_set_window_size_delegate(IntPtr handle, nuint width, nuint height);
    public static projectm_set_window_size_delegate projectm_set_window_size { get; private set; } = null!;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void projectm_set_texture_search_paths_delegate(
        IntPtr handle,
        IntPtr[] paths,
        nuint count);
    public static projectm_set_texture_search_paths_delegate projectm_set_texture_search_paths { get; private set; } = null!;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int glewInit_delegate();
    public static glewInit_delegate? glewInit { get; private set; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void projectm_log_callback(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string message,
        int level,
        IntPtr user_data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void projectm_set_log_callback_delegate(
        IntPtr handle,
        projectm_log_callback callback,
        IntPtr user_data);
    public static projectm_set_log_callback_delegate projectm_set_log_callback { get; private set; } = null!;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void projectm_set_log_level_delegate(IntPtr handle, int level);
    public static projectm_set_log_level_delegate projectm_set_log_level { get; private set; } = null!;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void projectm_set_fps_delegate(IntPtr instance, int fps);
    public static projectm_set_fps_delegate projectm_set_fps { get; private set; } = null!;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void projectm_pcm_add_float_delegate(IntPtr instance, float[] samples, uint count, ProjectMChannels channels);
    public static projectm_pcm_add_float_delegate projectm_pcm_add_float { get; private set; } = null!;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void projectm_pcm_add_int16_delegate(IntPtr instance, short[] samples, uint count, ProjectMChannels channels);
    public static projectm_pcm_add_int16_delegate projectm_pcm_add_int16 { get; private set; } = null!;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void projectm_opengl_render_frame_delegate(IntPtr instance);
    public static projectm_opengl_render_frame_delegate projectm_opengl_render_frame { get; private set; } = null!;

    /// <summary>
    /// Renders a single frame into a user-specified framebuffer. Available in
    /// libprojectM 4.2.0+. When present, this is preferred over
    /// <see cref="projectm_opengl_render_frame"/> + the GLEW BindFramebuffer
    /// hook, since it removes the GLEW dependency entirely.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void projectm_opengl_render_frame_fbo_delegate(IntPtr instance, uint framebuffer_object_id);
    public static projectm_opengl_render_frame_fbo_delegate? projectm_opengl_render_frame_fbo { get; private set; }

    /// <summary>
    /// True if the loaded libprojectM exposes
    /// <see cref="projectm_opengl_render_frame_fbo"/> (4.2.0+).
    /// When true, the GLEW hook is unnecessary and is never installed.
    /// </summary>
    public static bool HasFboRenderApi => projectm_opengl_render_frame_fbo != null;

    // FIXED (Bug 2): was `int` return type, but the C API is `void`. The
    // previous code was reading garbage from RAX and falsely reporting load
    // failures on some calls, false success on others.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void projectm_load_preset_file_delegate(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
        [MarshalAs(UnmanagedType.I1)] bool smooth_transition);
    public static projectm_load_preset_file_delegate projectm_load_preset_file { get; private set; } = null!;

    // FIXED (Bug 1): removed the bogus `length` parameter. The C API is
    //   void projectm_load_preset_data(projectm_handle, const char* data, bool smooth);
    // The previous delegate had an extra `nuint length` slot, which on x64
    // landed in the register where C reads `smooth_transition` — silently
    // forcing every load into smooth-transition mode.
    //
    // Caller MUST null-terminate the buffer, because the C function does
    //   std::stringstream presetDataStream(data);
    // which calls strlen(data). Without a null terminator, strlen reads past
    // the buffer into uninitialized heap memory — undefined behavior, possible
    // crash, or silent parsing of garbage data.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void projectm_load_preset_data_delegate(
        IntPtr handle,
        IntPtr data,
        [MarshalAs(UnmanagedType.I1)] bool smooth_transition);
    public static projectm_load_preset_data_delegate projectm_load_preset_data { get; private set; } = null!;

    // FIXED (Issue 13): added the missing `output_file` parameter.
    // C API: void projectm_write_debug_image_on_next_frame(projectm_handle, const char* output_file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void projectm_write_debug_image_on_next_frame_delegate(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? output_file);
    public static projectm_write_debug_image_on_next_frame_delegate? projectm_write_debug_image_on_next_frame { get; private set; }

    // ─── GLEW BindFramebuffer hook (libprojectM 4.1.x only) ────────────────
    //
    // On libprojectM 4.1.x, RenderFrame() hard-codes
    //   glBindFramebuffer(GL_DRAW_FRAMEBUFFER, 0);
    // before the final composite blit. In an embedded GL context (Avalonia),
    // FBO 0 is NOT the FBO we want to render into — Avalonia hands us a
    // specific FBO id via OnOpenGlRender(gl, int fb). There is no public API
    // to override this on 4.1.x, so we hook GLEW's __glewBindFramebuffer
    // global function pointer to redirect BindFramebuffer(*, 0) calls to the
    // FBO Avalonia gave us.
    //
    // On 4.2.0+, projectm_opengl_render_frame_fbo takes the target FBO as a
    // parameter and this hook is unnecessary. HasFboRenderApi gates this path.

    // Winapi = stdcall on Windows, cdecl on Linux/macOS. Correct for GL
    // entrypoints on every platform. (Was StdCall, which is wrong on x86
    // Linux and would crash if we ever shipped an x86 build.)
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void BindFramebufferDelegate(uint target, uint framebuffer);

    public static BindFramebufferDelegate? OriginalBindFramebuffer;
    private static readonly BindFramebufferDelegate _hookedBindFramebufferDelegate = HookedBindFramebuffer;

    [ThreadStatic]
    private static int _targetFbo;

    /// <summary>
    /// The FBO ID to redirect BindFramebuffer(*, 0) calls to. Set immediately
    /// before calling projectm_opengl_render_frame, reset to 0 after. Only
    /// used on 4.1.x builds without the FBO API.
    /// </summary>
    public static int TargetFbo
    {
        get => _targetFbo;
        set => _targetFbo = value;
    }

#if DEBUG
    private static int _hookedCallsCount = 0;
    private const int HookDebugLogLimit = 5;
#else
    private const int HookDebugLogLimit = 0;
#endif

    private static void HookedBindFramebuffer(uint target, uint framebuffer)
    {
#if DEBUG
        if (_hookedCallsCount < HookDebugLogLimit)
        {
            _hookedCallsCount++;
            Console.WriteLine($"[ProjectM Hook] HookedBindFramebuffer called! target={target}, original_fb={framebuffer}, target_fb={TargetFbo}");
        }
#endif
        if (framebuffer == 0 && TargetFbo != 0)
        {
            framebuffer = (uint)TargetFbo;
        }
        OriginalBindFramebuffer?.Invoke(target, framebuffer);
    }

    /// <summary>
    /// Installs the BindFramebuffer hook by overwriting GLEW's
    /// __glewBindFramebuffer function pointer with our wrapper.
    ///
    /// Safe to call multiple times — re-installs on every call. This is
    /// intentional: if the GL context is recreated (window recreation,
    /// display change, etc.), GLEW's __glewBindFramebuffer global is
    /// repopulated by a fresh glewInit(), and our hook must be re-installed
    /// to wrap the new function pointer.
    ///
    /// The previous code guarded with `_hooksInstalled` and refused to
    /// re-install, leaving the hook pointing at a stale (now-dead) context's
    /// function pointer after context recreation.
    /// </summary>
    public static void InstallGlewHooks()
    {
        if (_glewHandle == IntPtr.Zero) return;
        try
        {
            if (NativeLibrary.TryGetExport(_glewHandle, "__glewBindFramebuffer", out IntPtr addr))
            {
                IntPtr originalFuncPtr = Marshal.ReadIntPtr(addr);
                if (originalFuncPtr != IntPtr.Zero)
                {
                    OriginalBindFramebuffer = Marshal.GetDelegateForFunctionPointer<BindFramebufferDelegate>(originalFuncPtr);
                    IntPtr hookedFuncPtr = Marshal.GetFunctionPointerForDelegate(_hookedBindFramebufferDelegate);
                    Marshal.WriteIntPtr(addr, hookedFuncPtr);
                    Console.WriteLine("[ProjectM] Hooked __glewBindFramebuffer successfully.");
                }
                else
                {
                    Console.WriteLine("[ProjectM] __glewBindFramebuffer function pointer is currently NULL (glewInit not run yet?)");
                }
            }
            else
            {
                Console.WriteLine("[ProjectM] __glewBindFramebuffer export not found in glew32.dll/libGLEW.so");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProjectM] Failed to install GLEW hooks: {ex.Message}");
        }
    }
}
