# Avalonia.ProjectM — Wrapper Patch Changelog

Two files in this directory replace the originals in `Native/` and `Controls/`:

- `ProjectMNative.cs` → copy over `Native/ProjectMNative.cs`
- `ProjectMControl.cs` → copy over `Controls/ProjectMControl.cs`

No other files change. The `.csproj` is untouched. Drop-in replacement.

---

## What this patch does

Fixes the bugs identified in the code review, future-proofs the wrapper for
libprojectM 4.2.0, and keeps the GLEW BindFramebuffer hook for 4.1.x (which
is still the recommended stable release per the projectM maintainers).

The wrapper **auto-detects at runtime** which libprojectM version is loaded:

- **4.1.x** (current stable): GLEW hook path. `glewInit()` + `InstallGlewHooks()`
  from `OnOpenGlInit`, `TargetFbo` set per-frame, `projectm_opengl_render_frame()`.
- **4.2.0+** (when you upgrade): Direct FBO API. `projectm_opengl_render_frame_fbo(handle, fb)`.
  GLEW is never loaded, the hook is never installed. No code change required.

The detection is a single null check on the resolved `projectm_opengl_render_frame_fbo`
delegate — `HasFboRenderApi` property. Drop in a 4.2.0-built `libprojectM.dll` /
`libprojectM.so.4` and the wrapper silently switches to the clean path.

---

## Bug fixes

### Critical

**Bug 1 — `projectm_load_preset_data` delegate signature was wrong + buffer
wasn't null-terminated** (`ProjectMNative.cs`)

The C API is:
```c
void projectm_load_preset_data(projectm_handle instance, const char* data, bool smooth_transition);
```

The old delegate had an extra `nuint length` parameter:
```csharp
// OLD — wrong
public delegate void projectm_load_preset_data_delegate(IntPtr handle, IntPtr data, nuint length, byte smooth_transition);
```

On x64 (both Win64 and System V), the `length` argument landed in the register
where C reads `smooth_transition`, and the `smooth_transition=0` argument was
silently dropped. Result: **every preset load was forced into smooth-transition
mode** whether you asked for it or not.

Additionally, the allocated buffer had no null terminator:
```csharp
// OLD — no null terminator
IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
Marshal.Copy(bytes, 0, ptr, bytes.Length);
```

The C function does `std::stringstream presetDataStream(data)`, which calls
`strlen(data)`. Without a null terminator, `strlen` reads past the buffer into
uninitialized heap memory until it finds a zero byte — undefined behavior,
possible crash, or silent parsing of garbage data.

**Fix:**
```csharp
// NEW — correct signature, no length param, bool marshalled as I1
public delegate void projectm_load_preset_data_delegate(
    IntPtr handle, IntPtr data,
    [MarshalAs(UnmanagedType.I1)] bool smooth_transition);

// NEW — +1 for null terminator
IntPtr ptr = Marshal.AllocHGlobal(bytes.Length + 1);
Marshal.Copy(bytes, 0, ptr, bytes.Length);
Marshal.WriteByte(ptr + bytes.Length, 0);
ProjectMNative.projectm_load_preset_data(_projectMHandle, ptr, smooth);
```

---

**Bug 2 — `projectm_load_preset_file` delegate return type was wrong**
(`ProjectMNative.cs`)

The C API is `void`, but the delegate declared `int`:
```csharp
// OLD — wrong
public delegate int projectm_load_preset_file_delegate(...);
```

The call site then did:
```csharp
int loadRes = ProjectMNative.projectm_load_preset_file(...);
if (loadRes != 1) {
    Console.WriteLine($"[ProjectM] ERROR: ... failed (returned {loadRes}) ...");
}
```

`loadRes` was whatever happened to be in RAX when the function returned —
pure garbage. This produced **false error messages** on some calls and
**false success** on others.

**Fix:** delegate is now `void`, the `loadRes != 1` check is removed.

---

### High severity

**Issue 3 — `glewInit()` was called from the wrong place**
(`ProjectMControl.cs`)

The old code called `glewInit()` and `InstallGlewHooks()` from
`InitializeProjectM()`, which only runs once (guarded by
`_projectMHandle != IntPtr.Zero`). If the GL context was ever recreated
(window recreation, display change, etc.), `__glewBindFramebuffer` would be
repopulated by a new `glewInit()` that we never called — and our hook would
still be pointing at the **old (now-dead) context's function pointer**.
Calls to it on the new context could crash or no-op.

**Fix:** Moved `glewInit()` + `InstallGlewHooks()` to `OnOpenGlInit()`,
which fires whenever a new GL context is created. Also removed the
`_hooksInstalled` guard in `InstallGlewHooks()` so the hook is re-installed
on every call (necessary because `glewInit()` overwrites our hook with the
new function pointer).

Gated on `!HasFboRenderApi` so 4.2.0+ builds skip GLEW entirely.

---

**Issue 5 — GL function delegates declared `StdCall` on all platforms**
(`ProjectMControl.cs`)

Every GL delegate used `CallingConvention.StdCall`. On x64 this is harmless
(x64 has one ABI per platform). On x86 or ARM it would cause crashes.

**Fix:** Changed all GL delegates (and the `BindFramebufferDelegate` hook)
to `CallingConvention.Winapi`, which resolves to `stdcall` on Windows and
`cdecl` on Linux/macOS. Correct for GL entrypoints on every platform.

---

**Issue 6 — GL state save/restore was incomplete**
(`ProjectMControl.cs`)

The old `SaveGlState`/`RestoreGlState` saved: current program, active texture
unit (just the enum), texture binding on the *active unit only*, array buffer,
VAO, blend state (RGB only — alpha factors not saved!), depth test, scissor
test, viewport, scissor box.

**Missing and now added:**
- `GL_BLEND_SRC_ALPHA` / `GL_BLEND_DST_ALPHA` — the constants were already
  declared but never queried. Restored via `glBlendFuncSeparate` (was
  `glBlendFunc`, which can't restore RGB ≠ alpha states).
- `GL_UNPACK_ALIGNMENT` (default 4) — projectM may set this to 1 for tight
  texture uploads. If not restored, Avalonia's later texture uploads assuming
  4-byte alignment would produce corrupted textures.
- `GL_PACK_ALIGNMENT` — defensive.
- 2D texture bindings on units 0..3 — projectM uses multiple texture units
  and leaves them bound. The old code only saved the binding of the *active*
  unit, leaking projectM's textures into Avalonia's subsequent draws.

---

### Medium severity

**Issue 7 — PCM sample count not bounded**
(`ProjectMControl.cs`)

`projectm_pcm_get_max_samples()` returns 480 (`WaveformSamples` in
`AudioConstants.hpp`). The internal buffer is 576 samples per channel. The
old `FeedPcm` accepted arbitrary `short[]` and passed it through unchanged.
If a caller fed more than 960 stereo samples per call, `PCM::AddToBuffer`
wrapped modulo 576, silently overwriting earlier samples.

**Fix:** `FeedPcm` now chunks incoming audio to ≤480 samples/channel per
queue entry (≤960 shorts for stereo). Chunk boundaries are aligned to stereo
pairs so no sample is split across two chunks.

---

**Issue 9 — Initial `set_window_size` didn't apply `RenderScaling`**
(`ProjectMControl.cs`)

`InitializeProjectM` called:
```csharp
projectm_set_window_size(_projectMHandle, (nuint)Math.Max(1, Bounds.Width), (nuint)Math.Max(1, Bounds.Height));
```

Didn't apply `RenderScaling`. The next frame corrected this via the
`_lastWidth != width` check, but for one frame projectM rendered at the wrong
size on HiDPI displays.

**Fix:** Applies `RenderScaling` to the initial call, matching what
`OnOpenGlRender` does every frame.

---

**Honor the `smooth` flag** (`ProjectMControl.cs`)

The old `LoadPreset(string path, bool smooth = true)` accepted a `smooth`
parameter but the call sites ignored it and always passed `false` to the
native calls. The preset queue was `ConcurrentQueue<string>` so the flag
was lost in transit.

**Fix:** Queue is now `ConcurrentQueue<(string path, bool smooth)>`. The
`smooth` flag is honored on both `projectm_load_preset_data` and
`projectm_load_preset_file` calls.

---

### Low severity / cosmetic

- **Issue 10:** Removed dead `_lastLog` field (was written, never read).
- **Issue 11:** Removed empty `OnPropertyChanged` override.
- **Issue 13:** Fixed `projectm_write_debug_image_on_next_frame_delegate`
  signature — was missing the `output_file` parameter.
- **Issue 15:** Gated the `_hookedCallsCount` debug counter behind `#if DEBUG`.
- **Dead code removal:** Removed the mangled-name fallbacks for
  `projectm_set_log_callback` / `projectm_set_log_level` (lines 149–158 of
  the old `ProjectMNative.cs`). These functions don't exist on 4.1.6 at all
  (neither the unmangled nor mangled symbol), and on 4.2.0+ they exist as
  proper `extern "C"` symbols — no mangling. The fallback was pure dead code
  on both versions.

---

## What stays the same

- **GLEW hook mechanism on 4.1.x.** Still loads `glew32.dll` (Windows) /
  `libGLEW.so.2` (Linux), still sets `glewExperimental=1`, still calls
  `glewInit()`, still patches `__glewBindFramebuffer` to redirect
  `BindFramebuffer(*, 0)` calls to Avalonia's FBO. This is the only
  supported way to embed projectM 4.1.x into an external GL context.

- **WGL on Windows.** Required for 4.1.6's desktop-GL build. ANGLE/GLES is
  not viable on 4.1.6 (CMake refuses) and not viable on master either
  (ANGLE-D3D11 only does GLES 3.0, projectM needs 3.1+ for 3D textures and
  compute shaders). Stick with WGL.

- **Linux desktop GL.** projectM 4.1.6 supports `ENABLE_GLES=ON` on Linux,
  but the current build is desktop GL (libGLEW shows up as a dependency).
  Keep it that way for consistency with Windows.

- **All public APIs.** `StartEngine()`, `FeedPcm(short[])`, `LoadPreset(string, bool)`,
  `IsHandleValid` — unchanged signatures. Drop-in replacement.

---

## Upgrade path to libprojectM 4.2.0

When 4.2.0 is tagged stable and you rebuild `libprojectM.dll` / `libprojectM.so.4`
from it:

1. Drop the new binaries into `lib/` (replacing the 4.1.x ones).
2. On Windows, you can **delete `glew32.dll`** from `lib/` — 4.2.0 uses GLAD
   internally and doesn't link GLEW. (Keep it if you want; the wrapper won't
   load it because `HasFboRenderApi` will be true and the GLEW code path is
   skipped.)
3. On Linux, you can **stop installing `libglew2.2`** — same reason.
4. Restart the app. The wrapper logs:
   ```
   [ProjectM] Detected projectm_opengl_render_frame_fbo (libprojectM 4.2.0+) — GLEW hook disabled, using direct FBO API.
   ```
5. Done. No code change, no rebuild of the wrapper.

The GLEW code stays in the wrapper as a 4.1.x fallback. If you ever ship a
4.1.x binary again (regression test, customer issue), it just works.

---

## Files

| File | Lines (new) | Lines (old) | Delta |
|------|-------------|-------------|-------|
| `Native/ProjectMNative.cs` | 311 | 294 | +17 |
| `Controls/ProjectMControl.cs` | 596 | 449 | +147 |

Net growth is mostly comments documenting the bugs and the new GL state
save/restore logic. No behavioral regressions.
