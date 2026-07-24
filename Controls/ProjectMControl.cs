using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.ProjectM.Native;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using static Avalonia.OpenGL.GlConsts;

namespace Avalonia.ProjectM.Controls;

public class ProjectMControl : OpenGlControlBase
{
    #region Fields & Constants

    /// <summary>
    /// projectm_pcm_get_max_samples() returns 480 (WaveformSamples in
    /// AudioConstants.hpp). Feeding more than this per call is wasteful and
    /// risks silent wraparound inside projectM's 576-sample circular buffer.
    /// FeedPcm chunks incoming audio to this limit per channel.
    /// </summary>
    private const int MaxPcmSamplesPerChannelPerCall = 480;

    private short[] _pcmBuffer = new short[17640];
    private readonly short[] _chunkBuffer = new short[MaxPcmSamplesPerChannelPerCall * 2];

    private IntPtr _projectMHandle;
    private readonly ConcurrentQueue<short> _audioBuffer = new();
    private readonly System.Diagnostics.Stopwatch _renderStopwatch = new();

    // Two preset queues: one for file-path-based loads, one for content-based
    // loads. The content-based queue (LoadPresetData) avoids re-reading the
    // file when the caller has already read and normalized it — eliminates
    // the duplicate IO between VisualizerRuntime.LoadPreset and the old
    // LoadPresetFromQueue. Both queues are processed in OnOpenGlRender.
    private readonly ConcurrentQueue<(string path, bool smooth)> _presetQueue = new();
    private readonly ConcurrentQueue<(string content, bool smooth)> _presetDataQueue = new();

    private long _frameCount;
    private int _lastWidth;
    private int _lastHeight;
    private readonly ProjectMNative.projectm_log_callback _logCallback;
    private bool _engineRequested;

    // FIXED (black flicker): Track the last-loaded preset path/content to
    // prevent duplicate loads. At startup, ContentView.EnsureProjectMAttached
    // calls LoadPreset, then JukeboxVisualizerViewModel.InitializeAsync sets
    // SelectedVisualizerPath which triggers OnVisualizerPropertyChanged which
    // calls LoadPreset again with the SAME path. Each load triggers shader
    // compilation on the GL thread (10-50ms block) + resets projectM's main
    // texture (causing 1-2 black frames). Deduplication eliminates the
    // redundant second load.
    private string? _lastLoadedPresetPath;
    private string? _lastLoadedPresetContent;

    // GL function delegates resolved via GetProcAddress — Avalonia's
    // GlInterface only exposes a minimal subset; we need these extras
    // to reset GL state after ProjectM renders.
    //
    // FIXED (GPU pipeline stalls): Removed _glIsEnabled, _glEnable,
    // _glBlendFuncSeparate, _glScissor — no longer needed since we
    // reset to defaults instead of save/restore. Only the delegates
    // actually called in SaveGlState/RestoreGlState remain.
    private GlGetIntegervDelegate? _glGetIntegerv;
    private GlDisableDelegate? _glDisable;
    private GlUseProgramDelegate? _glUseProgram;
    private GlActiveTextureDelegate? _glActiveTexture;
    private GlBindTextureDelegate? _glBindTexture;
    private GlBindBufferDelegate? _glBindBuffer;
    private GlBindVertexArrayDelegate? _glBindVertexArray;
    private GlViewportDelegate? _glViewport;
    private GlPixelStoreiDelegate? _glPixelStorei;
    private bool _glFunctionsResolved;

    // GL constants not exposed by Avalonia's GlConsts
    private const int GL_BLEND = 0x0BE2;
    private const int GL_VIEWPORT = 0x0BA2;
    private const int GL_ARRAY_BUFFER = 0x8892;
    private const int GL_UNPACK_ALIGNMENT = 0x0CF5;
    private const int GL_PACK_ALIGNMENT = 0x0D05;
    private const int GL_TEXTURE0 = 0x84C0;
    private const int GL_DEPTH_TEST = 0x0B71;
    private const int GL_SCISSOR_TEST = 0x0C11;

    /// <summary>
    /// projectM typically binds textures on units 0..3. We unbind each of
    /// these units after projectM renders to prevent its textures from
    /// leaking into Avalonia's subsequent draws.
    /// </summary>
    private const int SavedTextureUnitCount = 4;

    // Winapi = stdcall on Windows, cdecl on Linux/macOS. Correct for GL
    // entrypoints on every platform.
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlGetIntegervDelegate(int pname, int[] data);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlDisableDelegate(int cap);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlUseProgramDelegate(int program);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlActiveTextureDelegate(int texture);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlBindTextureDelegate(int target, int texture);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlBindBufferDelegate(int target, int buffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlBindVertexArrayDelegate(int array);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlViewportDelegate(int x, int y, int width, int height);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlPixelStoreiDelegate(int pname, int param);
    #endregion

    #region Public Properties
    public bool IsHandleValid => _projectMHandle != IntPtr.Zero;
    #endregion

    #region Constructor
    public ProjectMControl()
    {
        _logCallback = (string message, int level, IntPtr userData) =>
        {
            if (level <= 2)
            {
                Console.WriteLine($"[ProjectM NATIVE] {message}");
            }
        };
    }
    #endregion

    #region Public Methods
    public void StartEngine()
    {
        _engineRequested = true;
    }

    /// <summary>
    /// Enqueues a buffer of interleaved 16-bit stereo PCM samples (L,R,L,R,...)
    /// to the continuous queue to be smoothly streamed to projectM during render frames.
    /// </summary>
    public void FeedPcm(short[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            _audioBuffer.Enqueue(samples[i]);
        }

        // Limit maximum queue size to ~50ms of audio at 44.1kHz stereo (4410 shorts)
        // to prevent audio latency lag relative to GL rendering frames.
        const int maxBufferSize = 4410;
        while (_audioBuffer.Count > maxBufferSize)
        {
            _audioBuffer.TryDequeue(out _);
        }
    }

    public void LoadPreset(string path, bool smooth = true)
    {
        _presetQueue.Enqueue((path, smooth));
        while (_presetQueue.Count > 5)
        {
            _presetQueue.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Enqueues preset file contents (already-read, already-normalized Milkdrop
    /// text) to be loaded into projectM on the next render frame.
    ///
    /// This method eliminates the duplicate file read that occurred when
    /// VisualizerRuntime.LoadPreset read the file, normalized line endings,
    /// wrote it to current_preset/, and then called LoadPreset(path) which
    /// re-read the file. With LoadPresetData, VisualizerRuntime reads +
    /// normalizes once and passes the content directly.
    ///
    /// The content is enqueued to a separate ConcurrentQueue and processed
    /// in OnOpenGlRender alongside the file-path queue. If both queues have
    /// entries, path-based loads are processed first (FIFO within each queue).
    /// </summary>
    /// <param name="content">The preset file contents (Milkdrop .milk text).
    /// Line endings should already be normalized to \n by the caller.</param>
    /// <param name="smooth">If true, projectM blends smoothly from the
    /// current preset to the new one. If false, hard cut.</param>
    public void LoadPresetData(string content, bool smooth = true)
    {
        _presetDataQueue.Enqueue((content, smooth));
        while (_presetDataQueue.Count > 5)
        {
            _presetDataQueue.TryDequeue(out _);
        }
    }
    #endregion

    #region Protected Methods
    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
        ResolveGlFunctions(gl);

        // Initialize GLEW and install the BindFramebuffer hook here, NOT in
        // InitializeProjectM(). Reason: OnOpenGlInit fires whenever a new GL
        // context is created (window recreation, display change, etc.), and
        // the hook must be re-installed after every glewInit() repopulates
        // __glewBindFramebuffer. The previous code only ran glewInit inside
        // InitializeProjectM (guarded by _projectMHandle != Zero), so context
        // recreation left the hook pointing at a stale function pointer.
        //
        // On libprojectM 4.2.0+, HasFboRenderApi is true and we skip GLEW
        // entirely — the new projectm_opengl_render_frame_fbo API takes the
        // target FBO as a parameter, no hook needed.
        if (!ProjectMNative.HasFboRenderApi && ProjectMNative.glewInit != null)
        {
            int glewRes = ProjectMNative.glewInit();
            if (glewRes != 0)
                Console.WriteLine($"[ProjectM] glewInit() failed with code: {glewRes}");
            ProjectMNative.InstallGlewHooks();
        }

        RequestNextFrameRendering();
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        try
        {
            var renderScaling = global::Avalonia.Controls.TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            int width = (int)Math.Max(1, Bounds.Width * renderScaling);
            int height = (int)Math.Max(1, Bounds.Height * renderScaling);

            gl.BindFramebuffer(GL_FRAMEBUFFER, fb);
            gl.Viewport(0, 0, width, height);

            // FIXED (black flicker): Removed the gl.Clear that was here.
            //
            // The old code cleared the FBO to black at the START of every
            // frame, BEFORE projectM rendered. This created a window where
            // Avalonia's compositor could composite a black FBO:
            //
            //   1. gl.Clear()          → FBO is now solid black
            //   2. SaveGlState()       → 12+ glGetIntegerv calls (slow)
            //   3. projectm_render()   → draws the visualization
            //   4. RestoreGlState()    → restores state
            //
            // If the compositor ran between step 1 and step 3, the user saw
            // a full-screen black frame — the "flicker" or "frame dropping"
            // effect. On slower GPUs or with heavy GL state queries, this
            // window was large enough to produce visible black flashes every
            // few seconds, even when idle.
            //
            // projectM's final composite is a full-screen quad that
            // overwrites every pixel in the FBO. The clear is redundant —
            // projectM overwrites it completely. Without the clear, if the
            // compositor runs mid-render, it sees the PREVIOUS frame's
            // content (which looks like a brief frame repeat — far less
            // jarring than solid black).
            //
            // The depth clear is also removed. projectM's shaders are 2D
            // screen-space and don't use depth testing. If a depth attachment
            // exists on Avalonia's FBO, projectM doesn't read or write it.

            if (!_engineRequested)
            {
                RequestNextFrameRendering();
                return;
            }

            if (_projectMHandle == IntPtr.Zero)
            {
                InitializeProjectM();
                if (_projectMHandle == IntPtr.Zero)
                {
                    RequestNextFrameRendering();
                    return;
                }
            }

            if (_lastWidth != width || _lastHeight != height)
            {
                _lastWidth = width;
                _lastHeight = height;
                ProjectMNative.projectm_set_window_size(_projectMHandle, (nuint)width, (nuint)height);
            }

            while (_presetQueue.TryDequeue(out var preset))
            {
                LoadPresetFromPath(preset.path, preset.smooth);
            }

            while (_presetDataQueue.TryDequeue(out var presetData))
            {
                LoadPresetFromContent(presetData.content, presetData.smooth);
            }

            // Measure elapsed time since last render frame for smooth audio feeding
            if (!_renderStopwatch.IsRunning)
            {
                _renderStopwatch.Start();
            }
            double elapsedSeconds = _renderStopwatch.Elapsed.TotalSeconds;
            _renderStopwatch.Restart();

            // Cap elapsedSeconds at 0.1s to avoid huge spikes (e.g., startup/resume)
            if (elapsedSeconds > 0.1)
            {
                elapsedSeconds = 0.016; // default 60 FPS frame time
            }

            // Calculate the number of samples to feed based on time elapsed.
            // Target is CD quality 44.1kHz stereo (2 channels).
            const int sampleRate = 44100;
            const int channels = 2;
            int samplesToFeed = (int)(elapsedSeconds * sampleRate);
            int shortsToFeed = samplesToFeed * channels;

            if (shortsToFeed > 0)
            {
                if (_pcmBuffer.Length < shortsToFeed)
                {
                    _pcmBuffer = new short[shortsToFeed * 2];
                }

                for (int i = 0; i < shortsToFeed; i++)
                {
                    if (_audioBuffer.TryDequeue(out short s))
                    {
                        _pcmBuffer[i] = s;
                    }
                    else
                    {
                        // Buffer underrun or playback paused: pad with silence
                        _pcmBuffer[i] = 0;
                    }
                }

                // Feed to projectM, chunking to max 480 samples/channel (960 shorts) per call
                int maxShortsPerCall = MaxPcmSamplesPerChannelPerCall * channels; // 960
                int offset = 0;
                while (offset < shortsToFeed)
                {
                    int len = Math.Min(maxShortsPerCall, shortsToFeed - offset);
                    len = (len / channels) * channels; // align to stereo pair
                    if (len <= 0) break;

                    Array.Copy(_pcmBuffer, offset, _chunkBuffer, 0, len);

                    ProjectMNative.projectm_pcm_add_int16(
                        _projectMHandle, _chunkBuffer,
                        (uint)len / 2, ProjectMChannels.Stereo);

                    offset += len;
                }
            }

            // Save GL state before ProjectM renders — it modifies
            // shaders, textures, blend modes, depth test, viewport, etc.
            // without restoring them, which corrupts Avalonia's Skia GL
            // state and causes the driver to leak orphaned resources.
            SaveGlState(out var savedState);

            if (ProjectMNative.HasFboRenderApi && ProjectMNative.projectm_opengl_render_frame_fbo is { } fboRender)
            {
                // 4.2.0+ clean path — pass the FBO directly to projectM.
                // No GLEW hook, no function-pointer patching, no TargetFbo
                // bookkeeping. This is the path the projectM maintainers
                // added specifically for embedded GL contexts like ours.
                fboRender(_projectMHandle, (uint)fb);
            }
            else
            {
                // 4.1.x path — GLEW hook redirects BindFramebuffer(*, 0) to fb.
                // TargetFbo is [ThreadStatic] so this is safe across multiple
                // ProjectMControl instances on different threads, though in
                // practice Avalonia renders on a single UI thread.
                ProjectMNative.TargetFbo = fb;
                ProjectMNative.projectm_opengl_render_frame(_projectMHandle);
                ProjectMNative.TargetFbo = 0;
            }

            // Restore GL state so Avalonia's compositor can safely
            // composite this FBO without resource leaks.
            RestoreGlState(gl, fb, savedState);

            _frameCount++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ProjectM Render Error: {ex}");
        }

        RequestNextFrameRendering();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_projectMHandle != IntPtr.Zero)
        {
            ProjectMNative.projectm_destroy(_projectMHandle);
            _projectMHandle = IntPtr.Zero;
        }
        base.OnOpenGlDeinit(gl);
    }
    #endregion

    #region Private Methods
    /// <summary>
    /// Loads a preset from a file path. Reads the file, normalizes line
    /// endings, then delegates to <see cref="LoadPresetFromContent"/>.
    /// </summary>
    private void LoadPresetFromPath(string presetPath, bool smooth)
    {
        // FIXED (preset stutter): Skip duplicate loads. At startup,
        // ContentView and JukeboxVisualizerViewModel can both trigger
        // a load of the same preset within a few hundred milliseconds.
        // Each load compiles shaders (10-50ms GL thread block) and
        // resets projectM's main texture (1-2 black frames). Checking
        // the path prevents the redundant second load.
        if (string.Equals(_lastLoadedPresetPath, presetPath, StringComparison.OrdinalIgnoreCase))
            return;

        if (ProjectMNative.projectm_load_preset_data != null)
        {
            try
            {
                if (System.IO.File.Exists(presetPath))
                {
                    string content = System.IO.File.ReadAllText(presetPath);
                    // Normalize line endings to Unix format (\n)
                    content = content.Replace("\r\n", "\n");
                    _lastLoadedPresetPath = presetPath;
                    LoadPresetFromContent(content, smooth);
                    Console.WriteLine($"[ProjectM] Loaded preset from file: {presetPath}");
                }
                else
                {
                    Console.WriteLine($"[ProjectM] ERROR: Preset file not found: {presetPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProjectM] ERROR loading preset from file {presetPath}: {ex.Message}");
            }
        }
        else if (ProjectMNative.projectm_load_preset_file != null)
        {
            var normalizedPath = presetPath.Replace('\\', '/');
            ProjectMNative.projectm_load_preset_file(_projectMHandle, normalizedPath, smooth);
            _lastLoadedPresetPath = presetPath;
        }
        else
        {
            Console.WriteLine("[ProjectM] ERROR: Both load_preset_data and load_preset_file delegates are NULL!");
        }
    }

    /// <summary>
    /// Loads a preset from already-read, already-normalized content text.
    /// Allocates an unmanaged buffer, copies the UTF-8 bytes with a null
    /// terminator, and calls projectm_load_preset_data.
    ///
    /// This is the shared bottom of the preset-load pipeline — both
    /// LoadPresetFromPath and the public LoadPresetData method converge
    /// here, ensuring the marshalling + null-terminator logic exists in
    /// exactly one place.
    /// </summary>
    private void LoadPresetFromContent(string content, bool smooth)
    {
        if (ProjectMNative.projectm_load_preset_data == null)
        {
            Console.WriteLine("[ProjectM] ERROR: projectm_load_preset_data delegate is NULL!");
            return;
        }

        // Defensive: normalize line endings even if the caller already did.
        // Costs nothing and protects against callers that forget.
        content = content.Replace("\r\n", "\n");

        // FIXED (preset stutter): Skip if this exact content was just loaded.
        // VisualizerRuntime.LoadPreset can be called multiple times for the
        // same preset (startup race between ContentView and the ViewModel).
        // Comparing content (not just path) catches duplicates even when
        // the caller uses LoadPresetData (no path available).
        if (string.Equals(_lastLoadedPresetContent, content, StringComparison.Ordinal))
            return;

        _lastLoadedPresetContent = content;

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        // +1 for null terminator. The C API does
        //   std::stringstream presetDataStream(data);
        // which calls strlen(data). Without the null terminator, strlen
        // reads past the buffer into uninitialized heap memory.
        IntPtr ptr = Marshal.AllocHGlobal(bytes.Length + 1);
        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Marshal.WriteByte(ptr + bytes.Length, 0);
            ProjectMNative.projectm_load_preset_data(_projectMHandle, ptr, smooth);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private void ResolveGlFunctions(GlInterface gl)
    {
        try
        {
            try
            {
                string? glVersion = gl.GetString(GL_VERSION);
                string? glRenderer = gl.GetString(GL_RENDERER);
                string? glVendor = gl.GetString(GL_VENDOR);
                Console.WriteLine($"[ProjectM] OpenGL Version: {glVersion}");
                Console.WriteLine($"[ProjectM] OpenGL Renderer: {glRenderer}");
                Console.WriteLine($"[ProjectM] OpenGL Vendor: {glVendor}");
            }
            catch (Exception glEx)
            {
                Console.WriteLine($"[ProjectM] Failed to query OpenGL info: {glEx.Message}");
            }

            _glGetIntegerv = ResolveDelegate<GlGetIntegervDelegate>(gl, "glGetIntegerv");
            _glDisable = ResolveDelegate<GlDisableDelegate>(gl, "glDisable");
            _glUseProgram = ResolveDelegate<GlUseProgramDelegate>(gl, "glUseProgram");
            _glActiveTexture = ResolveDelegate<GlActiveTextureDelegate>(gl, "glActiveTexture");
            _glBindTexture = ResolveDelegate<GlBindTextureDelegate>(gl, "glBindTexture");
            _glBindBuffer = ResolveDelegate<GlBindBufferDelegate>(gl, "glBindBuffer");
            _glBindVertexArray = ResolveDelegate<GlBindVertexArrayDelegate>(gl, "glBindVertexArray");
            _glViewport = ResolveDelegate<GlViewportDelegate>(gl, "glViewport");
            _glPixelStorei = ResolveDelegate<GlPixelStoreiDelegate>(gl, "glPixelStorei");

            _glFunctionsResolved = _glGetIntegerv != null && _glDisable != null
                && _glViewport != null;

            if (_glFunctionsResolved)
                Console.WriteLine("[ProjectM] GL state save/restore functions resolved successfully.");
            else
                Console.WriteLine("[ProjectM] WARNING: Some GL functions could not be resolved — state save/restore disabled.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProjectM] Failed to resolve GL functions: {ex.Message}");
            _glFunctionsResolved = false;
        }
    }

    private static T? ResolveDelegate<T>(GlInterface gl, string name) where T : Delegate
    {
        var ptr = gl.GetProcAddress(name);
        if (ptr == IntPtr.Zero) return null;
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    // FIXED (GPU pipeline stalls): Minimal state save — only viewport,
    // which projectM changes and Avalonia needs restored. Everything else
    // is RESET to defaults after projectM renders, not saved/restored.
    //
    // The previous save/restore did 19 synchronous glGetIntegerv/glIsEnabled
    // queries per frame. Each query is a GPU→CPU readback that flushes the
    // GL pipeline — on Intel/AMD Windows drivers, each costs 0.1-1ms,
    // totaling 2-19ms of pipeline stall per frame. At 60fps (16.6ms budget),
    // this caused skipped VSync deadlines and frame drops.
    //
    // Avalonia's Skia compositor re-binds its own program, textures, blend
    // state, and VBOs on every draw call — it doesn't depend on those being
    // preserved across our render. We only need to:
    //   1. Save viewport (projectM overwrites it)
    //   2. After projectM: reset program/textures/buffers to 0 (unbind),
    //      restore viewport, reset pixel store alignment to 4 (default)
    //
    // This reduces per-frame GL queries from 19 to 1 (viewport only).
    private struct GlSavedState
    {
        public int ViewportX, ViewportY, ViewportW, ViewportH;
    }

    private void SaveGlState(out GlSavedState state)
    {
        state = default;
        if (!_glFunctionsResolved) return;

        // Single glGetIntegerv call for viewport — the only state projectM
        // changes that Avalonia needs restored.
        var viewport = new int[4];
        _glGetIntegerv!(GL_VIEWPORT, viewport);
        state.ViewportX = viewport[0];
        state.ViewportY = viewport[1];
        state.ViewportW = viewport[2];
        state.ViewportH = viewport[3];
    }

    private void RestoreGlState(GlInterface gl, int fb, in GlSavedState state)
    {
        if (!_glFunctionsResolved) return;

        // Re-bind Avalonia's FBO (projectM may have bound its own).
        gl.BindFramebuffer(GL_FRAMEBUFFER, fb);

        // Unbind projectM's program, textures, and buffers. Avalonia's
        // compositor re-binds its own on the next draw — we just need to
        // clear projectM's residue so it doesn't leak.
        _glUseProgram?.Invoke(0);
        _glBindVertexArray?.Invoke(0);
        _glBindBuffer?.Invoke(GL_ARRAY_BUFFER, 0);

        // Unbind textures on units 0..3 (projectM leaves them bound).
        for (int i = 0; i < SavedTextureUnitCount; i++)
        {
            _glActiveTexture?.Invoke(GL_TEXTURE0 + i);
            _glBindTexture?.Invoke(GL_TEXTURE_2D, 0);
        }
        _glActiveTexture?.Invoke(GL_TEXTURE0); // reset to default unit

        // Reset blend/depth/scissor to defaults. Avalonia sets its own
        // per-draw, but defensive reset prevents edge cases.
        _glDisable?.Invoke(GL_BLEND);
        _glDisable?.Invoke(GL_DEPTH_TEST);
        _glDisable?.Invoke(GL_SCISSOR_TEST);

        // Reset pixel store alignment to default (4). projectM may set
        // GL_UNPACK_ALIGNMENT to 1 for tight texture uploads.
        _glPixelStorei?.Invoke(GL_UNPACK_ALIGNMENT, 4);
        _glPixelStorei?.Invoke(GL_PACK_ALIGNMENT, 4);

        // Restore viewport — the only thing we saved.
        _glViewport?.Invoke(state.ViewportX, state.ViewportY, state.ViewportW, state.ViewportH);
    }

    private void InitializeProjectM()
    {
        try
        {
            if (ProjectMNative.projectm_create != null)
            {
                _projectMHandle = ProjectMNative.projectm_create();
                if (_projectMHandle == IntPtr.Zero)
                {
                    Console.WriteLine("[ProjectM] ERROR: projectm_create() returned NULL handle");
                }
                else
                {
                    if (ProjectMNative.projectm_set_log_level != null)
                    {
                        ProjectMNative.projectm_set_log_level(_projectMHandle, 4);
                    }
                    if (ProjectMNative.projectm_set_log_callback != null)
                    {
                        ProjectMNative.projectm_set_log_callback(_projectMHandle, _logCallback, IntPtr.Zero);
                    }

                    ProjectMNative.projectm_set_fps(_projectMHandle, 60);

                    // FIXED (Issue 9): apply RenderScaling to the initial
                    // window size. The previous code used Bounds.Width/Height
                    // without scaling, causing projectM to render at the
                    // wrong size for one frame on HiDPI displays until the
                    // _lastWidth != width check corrected it on the next
                    // frame.
                    var renderScaling = global::Avalonia.Controls.TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
                    int initialWidth = (int)Math.Max(1, Bounds.Width * renderScaling);
                    int initialHeight = (int)Math.Max(1, Bounds.Height * renderScaling);
                    ProjectMNative.projectm_set_window_size(_projectMHandle, (nuint)initialWidth, (nuint)initialHeight);

                    if (ProjectMNative.projectm_set_texture_search_paths != null)
                    {
                        var assemblyDir = System.IO.Path.GetDirectoryName(typeof(ProjectMControl).Assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory;
                        var texturesPath = System.IO.Path.Combine(assemblyDir, "ProjectM", "textures");
                        
                        // Fallback to legacy path if plugin path does not exist
                        if (!System.IO.Directory.Exists(texturesPath))
                        {
                            texturesPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectM", "textures");
                        }

                        if (System.IO.Directory.Exists(texturesPath))
                        {
                            IntPtr ptr = Marshal.StringToHGlobalAnsi(texturesPath);
                            ProjectMNative.projectm_set_texture_search_paths(_projectMHandle, new IntPtr[] { ptr }, 1);
                            Marshal.FreeHGlobal(ptr);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ProjectM Init Error: {ex}");
        }
    }
    #endregion
}
